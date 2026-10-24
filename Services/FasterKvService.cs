using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FASTER.core;
using fyserver.Serialization;

namespace fyserver.Services;

/// <summary>FASTER KV 持久化封装。值为 JSON 字符串。</summary>
public class FasterKvService : IDisposable
{
    private FasterKV<string, string> _fasterKv;
    private ClientSession<string, string, string, string, Empty, IFunctions<string, string, string, string, Empty>> _session;
    private const string LogDirectory = "./faster-log";
    private readonly bool _verboseLogging;
    private CheckpointType _checkpointType = CheckpointType.FoldOver;

    /// <summary>本次启动是否从既有检查点恢复出了数据。</summary>
    public bool RecoveredFromCheckpoint { get; private set; }
    private bool _disposed;
    private readonly object _sync = new();
    private readonly string _logDirectory;

    // 内存索引：key -> JSON。FASTER 在托管 API 下不易全量迭代，
    // 用镜像索引支撑 GetKeysByPrefix/GetAllByPrefix（管理端点使用）。
    private readonly ConcurrentDictionary<string, string> _index = new();

    // System.Text.Json 序列化：FASTER 存储走 StoreJsonContext（源生成，保持 PascalCase 存储格式）
    private static JsonTypeInfo<T> GetStoreTypeInfo<T>() where T : class =>
        StoreJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException($"类型 {typeof(T).FullName} 未注册到 StoreJsonContext，无法序列化存储");

    public FasterKvService(string logDirectory = LogDirectory, bool verboseLogging = false, CheckpointType checkpointType = CheckpointType.FoldOver)
    {
        _verboseLogging = verboseLogging;
        _checkpointType = checkpointType;
        _logDirectory = logDirectory;

        _fasterKv = CreateStore();

        // 不再依赖 ./YCDR 标志文件判断有没有历史数据：每次启动都直接尝试恢复
        // 最后一次检查点，没有可用检查点时按全新存储启动。
        RecoveredFromCheckpoint = TryRecover();

        _session = _fasterKv.NewSession(new SimpleFunctions<string, string, Empty>());
    }

    /// <summary>按当前存储目录构建一个全新的 FasterKV 实例（不创建 session）。</summary>
    private FasterKV<string, string> CreateStore()
    {
        if (!Directory.Exists(_logDirectory))
            Directory.CreateDirectory(_logDirectory);

        return new FasterKV<string, string>(
            size: 1L << 15, // 32768 条记录的哈希表
            logSettings: new LogSettings
            {
                LogDevice = Devices.CreateLogDevice(Path.Combine(_logDirectory, "hlog.log")),
                ObjectLogDevice = Devices.CreateLogDevice(Path.Combine(_logDirectory, "hlog.obj.log")),
                PageSizeBits = 12, // 4KB 页面
                MemorySizeBits = 20 // 1MB 内存
            },
            checkpointSettings: new CheckpointSettings
            {
                CheckpointDir = _logDirectory,
                RemoveOutdated = true
            }
        );
    }

    /// <summary>丢弃当前存储并重建为空库。session 由调用方负责，这里不创建。</summary>
    private void RecreateStore()
    {
        try { _fasterKv?.Dispose(); } catch { }
        _index.Clear();
        if (Directory.Exists(_logDirectory))
            Directory.Delete(_logDirectory, true);
        _fasterKv = CreateStore();
    }

    // 存储值 (同步)
    public void Put<T>(string key, T value) where T : class
    {
        lock (_sync)
        {
            var jsonString = JsonSerializer.Serialize(value, GetStoreTypeInfo<T>());
            _session.Upsert(ref key, ref jsonString);
            _session.CompletePending(true);
            _index[key] = jsonString;
            if (_verboseLogging)
                Console.WriteLine($"Put: key={key}, size={jsonString.Length} bytes");
        }
    }

    // 获取值
    public T? Get<T>(string key) where T : class
    {
        lock (_sync)
        {
            if (_verboseLogging)
                Console.WriteLine($"Get<{typeof(T).Name}>: Looking for key={key}");

            string output = default!;
            var status = _session.Read(ref key, ref output);
            _session.CompletePending(true);

            if (!string.IsNullOrEmpty(output))
            {
                if (_verboseLogging)
                    Console.WriteLine($"Get<{typeof(T).Name}>: key={key}, size={output.Length} bytes");
                return JsonSerializer.Deserialize(output, GetStoreTypeInfo<T>());
            }

            if (_verboseLogging)
                Console.WriteLine($"Get<{typeof(T).Name}>: key={key} not found");

            return default;
        }
    }

    // 删除值 (同步)
    public void Delete(string key)
    {
        lock (_sync)
        {
            _session.Delete(ref key);
            _session.CompletePending(true);
            _index.TryRemove(key, out _);
            if (_verboseLogging)
                Console.WriteLine($"Delete: key={key}");
        }
    }

    // 检查键是否存在 (同步)
    public bool Exists(string key)
    {
        lock (_sync)
        {
            string output = default!;
            var status = _session.Read(ref key, ref output);
            _session.CompletePending(true);
            return !string.IsNullOrEmpty(output);
        }
    }

    public List<string> GetAllKeys()
    {
        lock (_sync)
        {
            return _index.Keys.ToList();
        }
    }

    // 根据前缀获取所有键 (同步)
    public List<string> GetKeysByPrefix(string prefix)
    {
        lock (_sync)
        {
            return _index.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        }
    }

    // 根据前缀获取所有值 (同步)
    public List<T> GetAllByPrefix<T>(string prefix) where T : class
    {
        lock (_sync)
        {
            var result = new List<T>();
            foreach (var key in GetKeysByPrefix(prefix))
            {
                if (_index.TryGetValue(key, out var json) && !string.IsNullOrEmpty(json))
                {
                    var value = JsonSerializer.Deserialize(json, GetStoreTypeInfo<T>());
                    if (value != null)
                        result.Add(value);
                }
            }
            return result;
        }
    }

    // 批量操作 (同步)
    public void Batch<T>(Dictionary<string, T>? puts, List<string>? deletes = null) where T : class
    {
        lock (_sync)
        {
            if ((deletes == null || deletes.Count == 0) && (puts == null || puts.Count == 0))
            {
                return;
            }

            if (deletes != null && deletes.Count > 0)
            {
                foreach (var key in deletes)
                {
                    var keyRef = key;
                    _session.Delete(ref keyRef);
                    _index.TryRemove(key, out _);
                }
            }

            if (puts != null && puts.Count > 0)
            {
                foreach (var kvp in puts)
                {
                    var key = kvp.Key;
                    var jsonString = JsonSerializer.Serialize(kvp.Value, GetStoreTypeInfo<T>());
                    _session.Upsert(ref key, ref jsonString);
                    _index[key] = jsonString;
                }
            }

            _session.CompletePending(true);

            if (_verboseLogging)
            {
                var putCount = puts?.Count ?? 0;
                var delCount = deletes?.Count ?? 0;
                Console.WriteLine($"Batch: puts={putCount}, deletes={delCount}");
            }
        }
    }

    // 创建检查点（快照）
    public void Checkpoint()
    {
        lock (_sync)
        {
            _fasterKv.TakeFullCheckpointAsync(_checkpointType).GetAwaiter().GetResult();
            _session.CompletePending(true);
            if (_verboseLogging)
                Console.WriteLine("FasterKvService: CompletePending called for checkpoint.");
        }
    }

    public void Checkpoint(CheckpointType checkpointType)
    {
        lock (_sync)
        {
            _fasterKv.TakeFullCheckpointAsync(checkpointType).GetAwaiter().GetResult();
            _session.CompletePending(true);
            if (_verboseLogging)
                Console.WriteLine($"FasterKvService: CompletePending called for checkpoint (type={checkpointType}).");
        }
    }

    public void SetCheckpointType(CheckpointType checkpointType)
    {
        _checkpointType = checkpointType;
    }

    // 恢复到最后一次检查点
    public void Recover()
    {
        lock (_sync)
        {
            _fasterKv.Recover();
            RebuildIndex();
        }
    }

    /// <summary>
    /// 启动时无条件尝试恢复最后一次检查点。全新安装没有检查点、或检查点损坏时，
    /// 回退到空库并返回 false，而不是让进程带着半初始化状态启动。
    /// </summary>
    private bool TryRecover()
    {
        try
        {
            _fasterKv.Recover();
            RebuildIndex();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"未找到可用的存储检查点，按全新存储启动：{ex.GetBaseException().Message}");
            RecreateStore();
            return false;
        }
    }

    private void RebuildIndex()
    {
        _index.Clear();
        using var iterator = _fasterKv.Log.Scan(
            _fasterKv.Log.BeginAddress,
            _fasterKv.Log.TailAddress,
            ScanBufferingMode.DoublePageBuffering);

        while (iterator.GetNext(out var recordInfo, out var key, out var value))
        {
            if (recordInfo.Tombstone)
                _index.TryRemove(key, out _);
            else if (!recordInfo.Invalid && !string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                _index[key] = value;
        }
    }

    // 清空数据库 (注意：FASTER 没有直接清空的方法，需要删除日志文件)
    public void Clear()
    {
        lock (_sync)
        {
            try { _session?.Dispose(); } catch { }

            // RecreateStore 会把检查点文件一并删掉，重启后不会再把旧数据恢复回来。
            RecreateStore();

            _session = _fasterKv.NewSession(new SimpleFunctions<string, string, Empty>());

            if (_verboseLogging)
                Console.WriteLine("FasterKvService: Clear completed and instance recreated.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // 退出前无条件落盘。此前依赖 ./YCDR 标志决定是否保存，导致全新安装
        // 第一次关服不写盘、重启后所有账户丢失。
        try
        {
            Checkpoint(CheckpointType.FoldOver);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"退出前保存检查点失败：{ex.GetBaseException().Message}");
        }
        _session?.Dispose();
        _fasterKv?.Dispose();
    }
}
