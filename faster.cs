using FASTER.core;
using System.Text.Json;

public class FasterKvService : IDisposable
{
    private FasterKV<string, string> _fasterKv;
    private ClientSession<string, string, string, string, Empty, IFunctions<string, string, string, string, Empty>> _session;
    private const string LogDirectory = "./faster-log";
    private readonly bool _verboseLogging;
    private CheckpointType _checkpointType = CheckpointType.FoldOver;
    // System.Text.Json 序列化选项
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    // 构造函数：初始化 FASTER KV
    public FasterKvService(string logDirectory = "./faster-log", bool verboseLogging = false, CheckpointType checkpointType = CheckpointType.FoldOver)
    {
        _verboseLogging = verboseLogging;
        _checkpointType = checkpointType;

        // 确保日志目录存在
        if (!Directory.Exists(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
        }
        // 创建 FASTER KV 设置
        var logSettings = new LogSettings
        {
            LogDevice = Devices.CreateLogDevice(Path.Combine(logDirectory, "hlog.log")),
            ObjectLogDevice = Devices.CreateLogDevice(Path.Combine(logDirectory, "hlog.obj.log")),
            PageSizeBits = 12, // 4KB 页面
            MemorySizeBits = 20 // 1MB 内存
        };

        // 初始化 FASTER KV
        _fasterKv = new FasterKV<string, string>(
            size: (1L << 15), // 1M 条记录的哈希表
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
        }
        // 创建客户端会话
        _session = _fasterKv.NewSession(new SimpleFunctions<string, string, Empty>());
    }
    // 存储值 (同步)
    public void Put(string key, object value)
    {
        // 使用 System.Text.Json 序列化为 JSON 字符串
        var jsonString = JsonSerializer.Serialize(value, value.GetType(), JsonOptions);

        // FASTER 的 Upsert 操作
        _session.Upsert(ref key, ref jsonString);
        _session.CompletePending(true);
        if (_verboseLogging)
            Console.WriteLine($"Put: key={key}, size={jsonString.Length} bytes");
    }

    // 获取值
    public T? Get<T>(string key)
    {
        if (_verboseLogging)
            Console.WriteLine($"Get<{typeof(T).Name}>: Looking for key={key}");

        string output = default;
        var status = _session.Read(ref key, ref output);
        // Complete pending operations to ensure output is available
        _session.CompletePending(true);

        // If output was populated, consider it found
        if (!string.IsNullOrEmpty(output))
        {
            if (_verboseLogging)
                Console.WriteLine($"Get<{typeof(T).Name}>: key={key}, size={output.Length} bytes");

            // 使用 System.Text.Json 反序列化
            return JsonSerializer.Deserialize<T>(output, JsonOptions);
        }

        if (_verboseLogging)
            Console.WriteLine($"Get<{typeof(T).Name}>: key={key} not found");

        return default;
    }

    // 删除值 (同步)
    public void Delete(string key)
    {
        _session.Delete(ref key);
        _session.CompletePending(true);

        if (_verboseLogging)
            Console.WriteLine($"Delete: key={key}");
    }

    // 检查键是否存在 (同步)
    public bool Exists(string key)
    {
        string output = default;
        var status = _session.Read(ref key, ref output);
        _session.CompletePending(true);
        return !string.IsNullOrEmpty(output);
    }
    public List<string> GetAllKeys()
    {
        throw new NotSupportedException("GetAllKeys is not supported for FasterKvService in this build.");
    }

    // 根据前缀获取所有键 (同步)
    public List<string> GetKeysByPrefix(string prefix)
    {
        throw new NotSupportedException("GetKeysByPrefix is not supported for FasterKvService in this build.");
    }

    // 根据前缀获取所有值 (同步)
    public List<T> GetAllByPrefix<T>(string prefix)
    {
        throw new NotSupportedException("GetAllByPrefix is not supported for FasterKvService in this build.");
    }

    // 批量操作 (同步)
    public void Batch(Dictionary<string, object> puts, List<string> deletes = null)
    {
        if ((deletes == null || deletes.Count == 0) && (puts == null || puts.Count == 0))
        {
            return;
        }

        try
        {
            if (deletes != null && deletes.Count > 0)
            {
                foreach (var key in deletes)
                {
                    var keyRef = key;
                    _session.Delete(ref keyRef);
                }
            }

            if (puts != null && puts.Count > 0)
            {
                foreach (var kvp in puts)
                {
                    var key = kvp.Key;
                    var jsonString = JsonSerializer.Serialize(kvp.Value, kvp.Value.GetType(), JsonOptions);
                    _session.Upsert(ref key, ref jsonString);
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
        catch
        {
            // FASTER 没有事务回滚，但操作是原子的
            throw;
        }
    }

    // 创建检查点（快照）
    public void Checkpoint()
    {
        // FASTER checkpointing API usage is environment/version dependent.
        // For now just ensure pending operations are completed.
        _fasterKv.TakeFullCheckpointAsync(_checkpointType);
        _session.CompletePending(true);
        if (_verboseLogging)
            Console.WriteLine("FasterKvService: CompletePending called for checkpoint.");
        if (!File.Exists("./YCDR"))
            File.Create("./YCDR").Dispose();
    }

    // Overload to request a specific checkpoint type at call time
    public void Checkpoint(CheckpointType checkpointType)
    {
        _fasterKv.TakeFullCheckpointAsync(checkpointType);
        _session.CompletePending(true);
        if (_verboseLogging)
            Console.WriteLine($"FasterKvService: CompletePending called for checkpoint (type={checkpointType}).");
    }

    // Allow changing the default checkpoint type at runtime
    public void SetCheckpointType(CheckpointType checkpointType)
    {
        _checkpointType = checkpointType;
    }

    // 恢复到最后一次检查点
    public void Recover()
    {
        _fasterKv.Recover();
    }

    // 清空数据库 (注意：FASTER 没有直接清空的方法，需要删除日志文件)
    public void Clear()
    {
        // Dispose current session and kv instance
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

        // Delete log directory if exists
        if (Directory.Exists(LogDirectory))
        {
            Directory.Delete(LogDirectory, true);
        }

        // Recreate log directory and reinitialize FASTER KV and session
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
    public void Dispose()
    {
        if (File.Exists("./YCDR"))
            Checkpoint(CheckpointType.FoldOver);
        _session?.Dispose();
        _fasterKv?.Dispose();
    }
}