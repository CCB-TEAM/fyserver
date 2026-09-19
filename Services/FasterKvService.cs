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
    private bool _disposed;
    private readonly object _sync = new();

    // 内存索引：key -> JSON。FASTER 在托管 API 下不易全量迭代，
    // 用镜像索引支撑 GetKeysByPrefix/GetAllByPrefix（管理端点使用）。
    private readonly ConcurrentDictionary<string, string> _index = new();

    // System.Text.Json 序列化：FASTER 存储走 StoreJsonContext（源生成，保持 PascalCase 存储格式）
    private static JsonTypeInfo<T> GetStoreTypeInfo<T>() where T : class =>
        StoreJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException($"类型 {typeof(T).FullName} 未注册到 StoreJsonContext，无法序列化存储");

    public FasterKvService(string logDirectory = "./faster-log", bool verboseLogging = false, CheckpointType checkpointType = CheckpointType.FoldOver)
    {
        _verboseLogging = verboseLogging;
        _checkpointType = checkpointType;

        // 确保日志目录存在
        if (!Directory.Exists(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
        }

        var logSettings = new LogSettings
        {
            LogDevice = Devices.CreateLogDevice(Path.Combine(logDirectory, "hlog.log")),
            ObjectLogDevice = Devices.CreateLogDevice(Path.Combine(logDirectory, "hlog.obj.log")),
            PageSizeBits = 12, // 4KB 页面
            MemorySizeBits = 20 // 1MB 内存
        };

        _fasterKv = new FasterKV<string, string>(
            size: 1L << 15, // 1M 条记录的哈希表
            logSettings: logSettings,
            checkpointSettings: new CheckpointSettings
            {
                CheckpointDir = logDirectory,
                RemoveOutdated = true
            }
        );

        if (File.Exists("./YCDR"))
        {
            _fasterKv.Recover();
            RebuildIndex();
        }

        _session = _fasterKv.NewSession(new SimpleFunctions<string, string, Empty>());
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
            if (!File.Exists("./YCDR"))
                File.Create("./YCDR").Dispose();
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
            // 统一写 YCDR 标志：增量/全量检查点后重启都能 Recover
            if (!File.Exists("./YCDR"))
                File.Create("./YCDR").Dispose();
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
            try
            {
                _session?.Dispose();
            }
            catch { }

            try
            {
                _fasterKv?.Dispose();
            }
            catch { }

            _index.Clear();

            if (Directory.Exists(LogDirectory))
            {
                Directory.Delete(LogDirectory, true);
            }

            Directory.CreateDirectory(LogDirectory);

            var logSettings = new LogSettings
            {
                LogDevice = Devices.CreateLogDevice(Path.Combine(LogDirectory, "hlog.log")),
                ObjectLogDevice = Devices.CreateLogDevice(Path.Combine(LogDirectory, "hlog.obj.log")),
                PageSizeBits = 12,
                MemorySizeBits = 20
            };

            _fasterKv = new FasterKV<string, string>(
                size: 1L << 20,
                logSettings: logSettings,
                checkpointSettings: new CheckpointSettings
                {
                    CheckpointDir = LogDirectory,
                    RemoveOutdated = true
                }
            );

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

        if (File.Exists("./YCDR"))
            Checkpoint(CheckpointType.FoldOver);
        _session?.Dispose();
        _fasterKv?.Dispose();
    }
}
