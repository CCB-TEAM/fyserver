using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace fyserver.Services;

public sealed record PatchPakInfo(string Id, string FileName, string Version, string Description,
    long Size, string Sha256, DateTime CreatedAt);

/// <summary>Persists uploaded patch PAKs as chunked application data in the configured player database.</summary>
public sealed class PatchPakService(AppDataStoreService data)
{
    private const string IndexKey = "patch-paks:index";
    private const string ChunkPrefix = "patch-paks:blob:";
    private const int ChunkSize = 4 * 1024 * 1024;
    public const long MaxPakSize = 128L * 1024 * 1024;
    private readonly object _gate = new();
    private List<PatchPakInfo>? _items;

    public IReadOnlyList<PatchPakInfo> List()
    {
        lock (_gate) return Load().OrderByDescending(item => item.CreatedAt).ToArray();
    }

    public async Task<(PatchPakInfo? Pak, string Error)> UploadAsync(string fileName, string version,
        string description, Stream input, long expectedLength, CancellationToken cancellationToken)
    {
        fileName = NormalizeFileName(fileName);
        version = version.Trim();
        description = description.Trim();
        if (!fileName.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)) return (null, "只允许上传 .pak 文件");
        if (fileName.Length is < 5 or > 180 || fileName.Any(char.IsControl)) return (null, "文件名长度或格式无效");
        if (version.Length > 64 || version.Any(char.IsControl)) return (null, "版本号最多 64 个字符且不能包含控制字符");
        if (description.Length > 500) return (null, "说明最多 500 个字符");
        if (expectedLength <= 0 || expectedLength > MaxPakSize) return (null, $"Pak 文件必须大于 0 且不超过 {MaxPakSize / 1024 / 1024} MB");

        var id = Guid.NewGuid().ToString("N");
        var chunkKeys = new List<string>();
        long total = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkSize];
        try
        {
            while (true)
            {
                var count = 0;
                while (count < buffer.Length)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(count, buffer.Length - count), cancellationToken);
                    if (read == 0) break;
                    count += read;
                }
                if (count == 0) break;
                total += count;
                if (total > MaxPakSize || total > expectedLength) throw new InvalidDataException("上传数据超过声明的文件大小限制");
                hash.AppendData(buffer, 0, count);
                var key = ChunkKey(id, chunkKeys.Count);
                data.Set(key, Convert.ToBase64String(buffer, 0, count));
                chunkKeys.Add(key);
            }
            if (total != expectedLength) throw new InvalidDataException("上传过程中 Pak 文件长度发生变化");
            var item = new PatchPakInfo(id, fileName, version, description, total,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), DateTime.UtcNow);
            lock (_gate)
            {
                var items = Load();
                items.Add(item);
                Save(items);
            }
            return (item, "");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or System.Data.Common.DbException)
        {
            foreach (var key in chunkKeys) { try { data.Delete(key); } catch { /* best-effort cleanup */ } }
            return (null, ex is InvalidDataException ? ex.Message : "Pak 保存失败：" + ex.GetBaseException().Message);
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var items = Load();
            var item = items.FirstOrDefault(x => x.Id == id);
            if (item == null) return false;
            var count = checked((int)((item.Size + ChunkSize - 1) / ChunkSize));
            items.Remove(item);
            Save(items);
            for (var i = 0; i < count; i++) data.Delete(ChunkKey(id, i));
            return true;
        }
    }

    public byte[]? Read(string id)
    {
        lock (_gate)
        {
            var item = Load().FirstOrDefault(x => x.Id == id);
            if (item == null || item.Size > int.MaxValue) return null;
            var result = new byte[(int)item.Size];
            var offset = 0;
            var count = checked((int)((item.Size + ChunkSize - 1) / ChunkSize));
            for (var i = 0; i < count; i++)
            {
                var encoded = data.Get(ChunkKey(id, i));
                if (encoded == null) return null;
                var chunk = Convert.FromBase64String(encoded);
                if (chunk.Length > result.Length - offset) return null;
                chunk.CopyTo(result, offset);
                offset += chunk.Length;
            }
            return offset == result.Length ? result : null;
        }
    }

    private List<PatchPakInfo> Load()
    {
        if (_items != null) return _items;
        _items = [];
        var encoded = data.Get(IndexKey);
        if (string.IsNullOrWhiteSpace(encoded)) return _items;
        try
        {
            if (JsonNode.Parse(encoded) is not JsonArray array) return _items;
            foreach (var row in array.OfType<JsonObject>())
            {
                var id = row["id"]?.GetValue<string>() ?? "";
                var size = row["size"]?.GetValue<long>() ?? 0;
                if (id.Length != 32 || size <= 0 || size > MaxPakSize) continue;
                _items.Add(new(id, row["fileName"]?.GetValue<string>() ?? "patch.pak",
                    row["version"]?.GetValue<string>() ?? "", row["description"]?.GetValue<string>() ?? "",
                    size, row["sha256"]?.GetValue<string>() ?? "", DateTime.TryParse(row["createdAt"]?.GetValue<string>(), out var created) ? created : DateTime.UnixEpoch));
            }
        }
        catch { _items = []; }
        return _items;
    }

    private void Save(List<PatchPakInfo> items)
    {
        var rows = new JsonArray(items.Select(item => (JsonNode)new JsonObject
        {
            ["id"] = item.Id, ["fileName"] = item.FileName, ["version"] = item.Version,
            ["description"] = item.Description, ["size"] = item.Size, ["sha256"] = item.Sha256,
            ["createdAt"] = item.CreatedAt.ToString("O")
        }).ToArray());
        data.Set(IndexKey, rows.ToJsonString());
        _items = items;
    }

    private static string ChunkKey(string id, int index) => $"{ChunkPrefix}{id}:{index:D4}";
    private static string NormalizeFileName(string name)
    {
        var leaf = name.Replace('\\', '/').Split('/').LastOrDefault() ?? "";
        return string.Concat(leaf.Where(c => !char.IsControl(c) && c is not '/' and not '\\' and not ':' and not '"' and not '<' and not '>' and not '|' and not '?' and not '*')).Trim();
    }
}
