using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FASTER.core;
using fyserver.Serialization;

namespace fyserver.Services;

/// <summary>
/// 本地玩家存储：内存索引（key → JSON）+ 原子 JSON 快照持久化。
///
/// 早期实现把持久化交给 FASTER 的 <c>TakeFullCheckpointAsync</c>，但该路径在
/// ASP.NET 宿主里永远等不到设备 I/O 完成（检查点状态机停在刷盘阶段：只有
/// <c>index-checkpoints/&lt;guid&gt;/ht.dat.0</c> 被创建、始终 0 字节，hlog 也不增长），
/// 于是任何调用 <c>RecordIncremental()</c> 的接口——改名、卡组保存、兑换码领取、
/// 后台改钱包——都会无限挂起，进程退出时的检查点也会卡住，账号因此不落盘。
///
/// 现在读写只操作内存索引，持久化改为把索引整体写成 JSON 快照（临时文件 +
/// File.Replace 原子替换，并保留上一版为 .bak）。旧的 faster-log 检查点仍会在启动时
/// 尝试一次性导入，导入成功后同样写入快照，之后不再依赖 FASTER 的检查点机制。
/// </summary>
public class FasterKvService : IDisposable
{
    private const string LogDirectory = "./faster-log";
    private const string SnapshotFileName = "users.snapshot.json";

    private readonly bool _verboseLogging;
    private CheckpointType _checkpointType = CheckpointType.FoldOver;
    private readonly object _sync = new();
    private readonly string _stateDirectory;

    // 内存索引：key -> JSON。所有读写都走这里，绝不触发设备 I/O。
    private readonly ConcurrentDictionary<string, string> _index = new();

    /// <summary>本次启动是否从既有持久化数据（快照或旧检查点）恢复出了内容。</summary>
    public bool RecoveredFromCheckpoint { get; private set; }

    private bool _disposed;

    // 自上次成功写快照以来是否发生过写入，避免重复写同一份内容。
    private volatile bool _dirty;

    private string SnapshotPath => Path.Combine(_stateDirectory, SnapshotFileName);
    private string SnapshotBackupPath => SnapshotPath + ".bak";

    // System.Text.Json 序列化：走 StoreJsonContext（源生成，保持 PascalCase 存储格式，NativeAOT 兼容）
    private static JsonTypeInfo<T> GetStoreTypeInfo<T>() where T : class =>
        StoreJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException($"类型 {typeof(T).FullName} 未注册到 StoreJsonContext，无法序列化存储");

    public FasterKvService(string logDirectory = LogDirectory, bool verboseLogging = false, CheckpointType checkpointType = CheckpointType.FoldOver)
    {
        _verboseLogging = verboseLogging;
        _checkpointType = checkpointType;
        _stateDirectory = logDirectory;

        try { Directory.CreateDirectory(_stateDirectory); }
        catch (Exception ex) { Console.WriteLine($"本地存储：无法创建目录 {_stateDirectory}：{ex.GetBaseException().Message}"); }

        var fromSnapshot = TryLoadSnapshot();
        var fromLegacy = !fromSnapshot && TryImportLegacyCheckpoint();
        RecoveredFromCheckpoint = fromSnapshot || fromLegacy;
    }

    // ---------------------------------------------------------------- 读写

    // 存储值 (同步)
    public void Put<T>(string key, T value) where T : class
    {
        var json = JsonSerializer.Serialize(value, GetStoreTypeInfo<T>());
        _index[key] = json;
        _dirty = true;
        if (_verboseLogging)
            Console.WriteLine($"Put: key={key}, size={json.Length} bytes");
    }

    // 获取值
    public T? Get<T>(string key) where T : class
    {
        if (!_index.TryGetValue(key, out var json) || string.IsNullOrEmpty(json))
        {
            if (_verboseLogging)
                Console.WriteLine($"Get<{typeof(T).Name}>: key={key} not found");
            return default;
        }
        if (_verboseLogging)
            Console.WriteLine($"Get<{typeof(T).Name}>: key={key}, size={json.Length} bytes");
        return JsonSerializer.Deserialize(json, GetStoreTypeInfo<T>());
    }

    // 删除值 (同步)
    public void Delete(string key)
    {
        _index.TryRemove(key, out _);
        _dirty = true;
        if (_verboseLogging)
            Console.WriteLine($"Delete: key={key}");
    }

    // 检查键是否存在 (同步)
    public bool Exists(string key) => _index.ContainsKey(key);

    public List<string> GetAllKeys() => _index.Keys.ToList();

    // 根据前缀获取所有键 (同步)
    public List<string> GetKeysByPrefix(string prefix) =>
        _index.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();

    // 根据前缀获取所有值 (同步)
    public List<T> GetAllByPrefix<T>(string prefix) where T : class
    {
        var result = new List<T>();
        foreach (var key in GetKeysByPrefix(prefix))
        {
            if (!_index.TryGetValue(key, out var json) || string.IsNullOrEmpty(json))
                continue;
            var value = JsonSerializer.Deserialize(json, GetStoreTypeInfo<T>());
            if (value != null)
                result.Add(value);
        }
        return result;
    }

    // 批量操作 (同步)
    public void Batch<T>(Dictionary<string, T>? puts, List<string>? deletes = null) where T : class
    {
        if (deletes is { Count: > 0 })
        {
            foreach (var key in deletes)
                _index.TryRemove(key, out _);
            _dirty = true;
        }

        if (puts is { Count: > 0 })
        {
            foreach (var kvp in puts)
                _index[kvp.Key] = JsonSerializer.Serialize(kvp.Value, GetStoreTypeInfo<T>());
            _dirty = true;
        }

        if (_verboseLogging)
            Console.WriteLine($"Batch: puts={puts?.Count ?? 0}, deletes={deletes?.Count ?? 0}");
    }

    // ---------------------------------------------------------------- 持久化

    // 创建检查点（快照）
    public void Checkpoint() => SaveSnapshot(force: true);

    public void Checkpoint(CheckpointType checkpointType)
    {
        if (_verboseLogging)
            Console.WriteLine($"本地存储：按 {checkpointType} 语义写快照（实现为全量快照）");
        SaveSnapshot();
    }

    public void SetCheckpointType(CheckpointType checkpointType) => _checkpointType = checkpointType;

    /// <summary>把内存索引整体写成 JSON 快照（临时文件 + 原子替换，保留上一版为 .bak）。</summary>
    public void SaveSnapshot(bool force = false)
    {
        if (_disposed) return;
        lock (_sync)
        {
            // 没有新写入且快照已存在时跳过，避免同一份内容反复落盘。
            if (!force && !_dirty && File.Exists(SnapshotPath)) return;

            var tmp = SnapshotPath + ".tmp";
            try
            {
                Directory.CreateDirectory(_stateDirectory);
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("version", 1);
                    writer.WriteString("savedAt", DateTime.UtcNow.ToString("O"));
                    writer.WriteNumber("count", _index.Count);
                    writer.WriteStartObject("entries");
                    foreach (var entry in _index)
                    {
                        if (string.IsNullOrEmpty(entry.Key)) continue;
                        writer.WritePropertyName(entry.Key);
                        // 值本身已是 JSON 文本，直接写入，避免二次转义。
                        writer.WriteRawValue(entry.Value, skipInputValidation: true);
                    }
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                if (File.Exists(SnapshotPath))
                    File.Replace(tmp, SnapshotPath, SnapshotBackupPath, ignoreMetadataErrors: true);
                else
                    File.Move(tmp, SnapshotPath);

                _dirty = false;
                if (_verboseLogging)
                    Console.WriteLine($"本地存储：快照已写入 {_index.Count} 条记录 → {SnapshotPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"本地存储：写入快照失败：{ex.GetBaseException().Message}");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }

    /// <summary>重新从磁盘装载（快照优先，其次旧的 faster-log 检查点）。</summary>
    public void Recover()
    {
        _index.Clear();
        RecoveredFromCheckpoint = TryLoadSnapshot() || TryImportLegacyCheckpoint();
        _dirty = false;
    }

    private bool TryLoadSnapshot()
    {
        try
        {
            if (!File.Exists(SnapshotPath)) return false;
            using var document = JsonDocument.Parse(File.ReadAllBytes(SnapshotPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!document.RootElement.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Object) return false;

            var loaded = 0;
            foreach (var property in entries.EnumerateObject())
            {
                if (string.IsNullOrEmpty(property.Name)) continue;
                _index[property.Name] = property.Value.GetRawText();
                loaded++;
            }
            Console.WriteLine($"本地存储：已从快照恢复 {loaded} 条记录");
            return loaded > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"本地存储：快照读取失败：{ex.GetBaseException().Message}");
            return false;
        }
    }

    /// <summary>
    /// 从旧版本的 faster-log 检查点导入一次数据。导入成功后立即落成快照，
    /// 之后不再使用 FASTER 的检查点机制。
    /// </summary>
    private bool TryImportLegacyCheckpoint()
    {
        try
        {
            if (!Directory.Exists(_stateDirectory)) return false;
            var hasLegacy = Directory.EnumerateFiles(_stateDirectory, "*", SearchOption.AllDirectories)
                .Any(path =>
                {
                    var name = Path.GetFileName(path);
                    return name.StartsWith("hlog", StringComparison.OrdinalIgnoreCase) ||
                           path.Contains("checkpoints", StringComparison.OrdinalIgnoreCase);
                });
            if (!hasLegacy) return false;

            using var store = new FasterKV<string, string>(1L << 15, new LogSettings
            {
                LogDevice = Devices.CreateLogDevice(Path.Combine(_stateDirectory, "hlog.log")),
                ObjectLogDevice = Devices.CreateLogDevice(Path.Combine(_stateDirectory, "hlog.obj.log")),
                PageSizeBits = 12,
                MemorySizeBits = 20
            }, new CheckpointSettings { CheckpointDir = _stateDirectory, RemoveOutdated = true });

            store.Recover();
            using var iterator = store.Log.Scan(
                store.Log.BeginAddress,
                store.Log.TailAddress,
                ScanBufferingMode.DoublePageBuffering);

            var imported = 0;
            while (iterator.GetNext(out var recordInfo, out var key, out var value))
            {
                if (recordInfo.Tombstone || recordInfo.Invalid) continue;
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) continue;
                _index[key] = value;
                imported++;
            }

            Console.WriteLine($"本地存储：已从旧 faster-log 检查点导入 {imported} 条记录");
            if (imported == 0) return false;
            SaveSnapshot();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"本地存储：旧检查点导入跳过：{ex.GetBaseException().Message}");
            return false;
        }
    }

    // 清空数据库
    public void Clear()
    {
        lock (_sync)
        {
            _index.Clear();
            RecoveredFromCheckpoint = false;
            _dirty = false;
            try { if (File.Exists(SnapshotPath)) File.Delete(SnapshotPath); } catch { }
            try { if (File.Exists(SnapshotBackupPath)) File.Delete(SnapshotBackupPath); } catch { }
            // 清掉旧 faster-log 检查点，重启后不会再把旧数据导入回来。
            try
            {
                foreach (var path in Directory.EnumerateFiles(_stateDirectory, "*", SearchOption.AllDirectories).ToList())
                    File.Delete(path);
                foreach (var dir in Directory.EnumerateDirectories(_stateDirectory).ToList())
                    Directory.Delete(dir, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"本地存储：清理旧检查点失败：{ex.GetBaseException().Message}");
            }

            if (_verboseLogging)
                Console.WriteLine("FasterKvService: Clear completed.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        // 退出前无条件落盘；旧实现依赖 FASTER 检查点，在宿主里会卡死，导致账号不保存。
        try { SaveSnapshot(force: true); }
        catch (Exception ex) { Console.WriteLine($"退出前保存快照失败：{ex.GetBaseException().Message}"); }
        _disposed = true;
    }
}
