using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace fyserver.Services;

/// <summary>
/// 云端整合包：只保存清单（启动器 modpack.json 里的 index 条目），不保存任何文件。
/// 每项为 {"id":模组索引 id,"version":版本,"sha256":哈希,"enabled":是否启用}，顺序即加载顺序。
/// 本地模组（不在模组索引里）不能上传：启动器上传前去掉 local 条目，服务端也会拒绝未发布的 id。
/// 数据保存在 ./data/launcher-modpacks.json。
/// </summary>
public sealed class LauncherModpackService(ModIndexService modIndex)
{
    private const string StorePath = "./data/launcher-modpacks.json";
    private const int MaxPacksPerAccount = 50;
    private const int MaxModsPerPack = 200;
    private readonly object _gate = new();
    private JsonArray? _packs;

    public readonly record struct Result(bool Ok, int Status, string Message, JsonObject? Pack = null);

    public List<JsonObject> List(int viewerId, bool mineOnly, string? query)
    {
        query = query?.Trim();
        lock (_gate)
        {
            return Load().OfType<JsonObject>()
                .Where(p => Int(p["ownerId"]) == viewerId || (!mineOnly && Str(p["visibility"]) == "public"))
                .Where(p => string.IsNullOrEmpty(query) ||
                            (Str(p["name"]) ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            (Str(p["ownerName"]) ?? "").Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => Str(p["updatedAt"]))
                .Select(p => (JsonObject)p.DeepClone())
                .ToList();
        }
    }

    /// <summary>后台：列出全部整合包（含私有）。</summary>
    public List<JsonObject> All()
    {
        lock (_gate)
            return Load().OfType<JsonObject>().OrderByDescending(p => Str(p["updatedAt"])).Select(p => (JsonObject)p.DeepClone()).ToList();
    }

    /// <summary>公开的整合包谁都能看；私有的只有作者能看（但知道 id 的房间成员可以通过房间拿到清单）。</summary>
    public JsonObject? Get(string id, int viewerId)
    {
        lock (_gate)
        {
            var pack = Find(id);
            if (pack == null) return null;
            if (Str(pack["visibility"]) != "public" && Int(pack["ownerId"]) != viewerId) return null;
            return (JsonObject)pack.DeepClone();
        }
    }

    public Result Create(int ownerId, string ownerName, JsonObject body)
    {
        var (fields, error) = Validate(body, requireAll: true);
        if (error != null) return new(false, 400, error);
        lock (_gate)
        {
            var packs = Load();
            if (packs.OfType<JsonObject>().Count(p => Int(p["ownerId"]) == ownerId) >= MaxPacksPerAccount)
                return new(false, 409, $"每个账号最多保存 {MaxPacksPerAccount} 个整合包");
            var now = DateTime.UtcNow.ToString("O");
            var pack = new JsonObject
            {
                ["id"] = NewId(packs),
                ["ownerId"] = ownerId,
                ["ownerName"] = ownerName,
                ["createdAt"] = now,
                ["updatedAt"] = now
            };
            ApplyFields(pack, fields!);
            packs.Add(pack);
            Save();
            return new(true, 201, "整合包已保存", (JsonObject)pack.DeepClone());
        }
    }

    public Result Update(string id, int ownerId, JsonObject body)
    {
        var (fields, error) = Validate(body, requireAll: false);
        if (error != null) return new(false, 400, error);
        lock (_gate)
        {
            var pack = Find(id);
            if (pack == null) return new(false, 404, "整合包不存在");
            if (Int(pack["ownerId"]) != ownerId) return new(false, 403, "只能修改自己的整合包");
            ApplyFields(pack, fields!);
            pack["updatedAt"] = DateTime.UtcNow.ToString("O");
            Save();
            return new(true, 200, "整合包已更新", (JsonObject)pack.DeepClone());
        }
    }

    public Result Delete(string id, int? ownerId)
    {
        lock (_gate)
        {
            var pack = Find(id);
            if (pack == null) return new(false, 404, "整合包不存在");
            if (ownerId != null && Int(pack["ownerId"]) != ownerId) return new(false, 403, "只能删除自己的整合包");
            Load().Remove(pack);
            Save();
            return new(true, 200, "整合包已删除");
        }
    }

    private sealed record Fields(string? Name, string? Description, string? Visibility, JsonArray? Mods);

    private (Fields? Fields, string? Error) Validate(JsonObject body, bool requireAll)
    {
        var name = Str(body["name"])?.Trim();
        if (requireAll && string.IsNullOrEmpty(name)) return (null, "请填写整合包名称");
        if (name != null && (name.Length is 0 or > 40 || name.Any(char.IsControl))) return (null, "整合包名称需要 1 到 40 个字");
        var description = Str(body["description"])?.Trim();
        if (description is { Length: > 500 }) return (null, "简介最多 500 个字");
        var visibility = Str(body["visibility"])?.Trim().ToLowerInvariant();
        if (visibility != null && visibility is not ("public" or "private")) return (null, "visibility 只能是 public 或 private");
        JsonArray? mods = null;
        if (body["mods"] is JsonArray list)
        {
            if (list.Count > MaxModsPerPack) return (null, $"整合包最多 {MaxModsPerPack} 个模组");
            mods = [];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var unknown = new List<string>();
            foreach (var item in list)
            {
                if (item is not JsonObject entry) return (null, "mods 的每一项都必须是对象");
                var id = Str(entry["id"])?.Trim().ToLowerInvariant();
                if (id == null || !ModIndexService.IsValidId(id)) return (null, "mods 里有无效的模组 id（本地模组不能上传）");
                if (!seen.Add(id)) continue;
                var manifest = modIndex.Get(id);
                if (manifest == null || !ModIndexService.IsPublic(manifest)) { unknown.Add(id); continue; }
                var published = (JsonObject)manifest["published"]!;
                mods.Add(new JsonObject
                {
                    ["id"] = id,
                    // 版本和哈希以上传时客户端声明的为准；没给就记下当时已发布的版本。
                    ["version"] = Str(entry["version"]) ?? Str(published["version"]),
                    ["sha256"] = Str(entry["sha256"])?.ToLowerInvariant() ?? Str(published["sha256"]),
                    ["enabled"] = entry["enabled"] is not JsonValue e || !e.TryGetValue<bool>(out var en) || en
                });
            }
            if (unknown.Count > 0) return (null, "这些模组不在模组索引里或未发布：" + string.Join("、", unknown.Take(10)));
            if (requireAll && mods.Count == 0) return (null, "整合包里至少要有一个模组");
        }
        else if (requireAll) return (null, "缺少 mods 清单");
        return (new Fields(name, description, visibility, mods), null);
    }

    private static void ApplyFields(JsonObject pack, Fields f)
    {
        if (f.Name != null) pack["name"] = f.Name;
        if (f.Description != null) pack["description"] = f.Description;
        if (f.Visibility != null) pack["visibility"] = f.Visibility;
        else pack["visibility"] ??= "private";
        if (f.Mods != null) pack["mods"] = f.Mods;
        pack["description"] ??= "";
    }

    private JsonObject? Find(string id) =>
        Load().OfType<JsonObject>().FirstOrDefault(p => string.Equals(Str(p["id"]), id, StringComparison.Ordinal));

    private JsonArray Load()
    {
        if (_packs != null) return _packs;
        try
        {
            _packs = File.Exists(StorePath) ? (JsonNode.Parse(File.ReadAllText(StorePath))?["packs"] as JsonArray) ?? [] : [];
        }
        catch (Exception ex)
        {
            Console.WriteLine($"读取 {StorePath} 失败：{ex.Message}");
            throw;
        }
        return _packs;
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        var temp = StorePath + ".tmp";
        File.WriteAllText(temp, new JsonObject { ["packs"] = _packs!.DeepClone() }.ToJsonString(new() { WriteIndented = true }));
        File.Move(temp, StorePath, true);
    }

    private static string NewId(JsonArray packs)
    {
        while (true)
        {
            var id = "mp_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5));
            if (!packs.OfType<JsonObject>().Any(p => Str(p["id"]) == id)) return id;
        }
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
}
