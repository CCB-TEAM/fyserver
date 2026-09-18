using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace fyserver.Services;

/// <summary>
/// 后台内容配置的落盘存储：frontpage 元素 / skirmish / knockout 共用一套「条目数组」模型。
///
/// 文件结构（三者一致）：
/// <code>
/// {
///   "entries": [
///     { "id": 1, "name": "…", "start_date": "…", "end_date": "…", "rules": { … } }
///   ]
/// }
/// </code>
/// frontpage 额外兼容客户端既有格式（顶层 elements/targeted 数组），读写时自动转换。
///
/// 一律走 JsonNode DOM API（不依赖反射序列化），NativeAOT 安全；
/// 保存前校验 JSON 并备份为 <c>{path}.bak</c>。
/// </summary>
public class ContentEntriesService
{
    public const string FrontpagePath = "./config/frontpage.json";
    public const string SkirmishPath = "./config/skirmish.json";
    public const string KnockoutPath = "./config/knockout.json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>后台列表里显示的一条内容配置。</summary>
    public sealed class Entry
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string StartDate { get; set; } = "";
        public string EndDate { get; set; } = "";
        /// <summary>完整条目 JSON（含后台未建模的字段），编辑页直接回填。</summary>
        public string Raw { get; set; } = "{}";
        /// <summary>提交上来的一级字段（键为 snake_case 字段名）。</summary>
        public Dictionary<string, string> Fields { get; set; } = new();
    }

    // ---------------------------------------------------------------- 读取

    public List<Entry> List(string path)
    {
        var root = ReadRoot(path);
        var list = new List<Entry>();
        if (root["entries"] is not JsonArray entries)
            return list;

        foreach (var node in entries)
        {
            if (node is JsonObject obj)
                list.Add(ToEntry(obj));
        }
        return list;
    }

    public Entry? Get(string path, int id) => List(path).FirstOrDefault(e => e.Id == id);

    // ---------------------------------------------------------------- 写入

    /// <summary>新增或更新一条；id&lt;=0 表示新增（自动分配 id）。返回 (是否成功, 提示)。</summary>
    public (bool ok, string message) Save(string path, int id, Entry input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
            return (false, "名称不能为空");

        var root = ReadRoot(path);
        var entries = EntryArray(root);

        var isFrontpage = path == FrontpagePath;
        var idKey = isFrontpage ? "element_id" : "id";
        var payloadKey = isFrontpage ? "content" : "rules";

        JsonObject target;
        if (id > 0)
        {
            var existing = entries.FirstOrDefault(n =>
                (int?)n?[idKey] == id || (int?)n?["id"] == id || (int?)n?["elementId"] == id) as JsonObject;
            if (existing == null)
                return (false, $"条目 {id} 不存在");
            target = existing;
        }
        else
        {
            target = new JsonObject { [idKey] = NextId(entries) };
            entries.Add(target);
        }

        target["name"] = input.Name.Trim();
        target["start_date"] = string.IsNullOrWhiteSpace(input.StartDate) ? "0001-01-01 00:00:00" : input.StartDate.Trim();
        target["end_date"] = string.IsNullOrWhiteSpace(input.EndDate) ? "9999-12-31 23:59:00" : input.EndDate.Trim();

        // 编辑页未建模的字段（rules / content 的深层内容）按原文回写，避免丢字段
        if (!string.IsNullOrWhiteSpace(input.Raw))
        {
            JsonObject? payload;
            try
            {
                payload = JsonNode.Parse(input.Raw) as JsonObject;
            }
            catch (JsonException ex)
            {
                return (false, $"JSON 格式错误：{ex.Message}");
            }

            if (payload == null)
                return (false, "JSON 根节点必须是对象");

            // id / 名称 / 日期由表单管理，其余键原样带回
            foreach (var kv in payload.ToList())
            {
                if (kv.Key is "id" or "element_id" or "name" or "start_date" or "end_date")
                    continue;

                if (kv.Key == payloadKey && kv.Value is JsonObject nested)
                {
                    var current = target[payloadKey] as JsonObject ?? new JsonObject();
                    foreach (var inner in nested)
                        current[inner.Key] = inner.Value is { } innerValue ? innerValue.DeepClone() : null;
                    target[payloadKey] = current;
                }
                else
                {
                    target[kv.Key] = kv.Value is { } value ? value.DeepClone() : null;
                }
            }
        }

        return Write(path, root, id > 0 ? $"已保存条目 {id}" : "已新增条目");
    }

    /// <summary>删除一条。</summary>
    public (bool ok, string message) Delete(string path, int id)
    {
        var root = ReadRoot(path);
        var entries = EntryArray(root);
        var node = entries.FirstOrDefault(n => (int?)n?["id"] == id);
        if (node == null)
            return (false, $"条目 {id} 不存在");

        entries.Remove(node);
        return Write(path, root, $"已删除条目 {id}");
    }

    /// <summary>读取文件原始文本（编辑页的 JSON 视图）。</summary>
    public string ReadRaw(string path) => File.Exists(path) ? File.ReadAllText(path) : DefaultDocument(path);

    /// <summary>直接保存编辑后的文件原文（校验 + 备份）。</summary>
    public (bool ok, string message) SaveRaw(string path, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (false, "内容为空，未保存");

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            return (false, $"JSON 格式错误：{ex.Message}");
        }

        if (node is not JsonObject root)
            return (false, "JSON 根节点必须是对象");

        return Write(path, root, "已保存（原文件已备份为 .bak）");
    }

    // ---------------------------------------------------------------- 内部

    private static IEnumerable<JsonNode?> EntryNodes(JsonObject root)
    {
        return root["entries"] is JsonArray entries ? entries : Enumerable.Empty<JsonNode?>();
    }

    /// <summary>
    /// 统一成 entries 数组。注意 JsonArray.Add&lt;T&gt; 带 RequiresDynamicCode 标记（IL3050），
    /// 但传入的已是 JsonNode 引用、不会构造 JsonValue，因此该告警在 csproj 的 NoWarn 里统一降噪。
    /// </summary>
    private static JsonArray EntryArray(JsonObject root)
    {
        if (root["entries"] is JsonArray entries)
            return entries;

        var array = new JsonArray();
        foreach (var key in new[] { "elements", "targeted" })
        {
            if (root[key] is not JsonArray source)
                continue;
            foreach (var node in source)
            {
                if (node is { } n)
                    array.Add(n.DeepClone());
            }
            root.Remove(key);
        }
        root["entries"] = array;
        return array;
    }

    private static Entry ToEntry(JsonObject obj)
    {
        var id = (int?)obj["id"] ?? (int?)obj["element_id"] ?? (int?)obj["elementId"] ?? 0;

        // frontpage 条目没有 name：优先取本地化标题，其次按类型给个可读名字
        var name = (string?)obj["name"];
        if (string.IsNullOrWhiteSpace(name))
        {
            var heading = obj["content"]?["heading"]?["text"];
            name = Localised(heading) ?? Localised(obj["content"]?["heading"]) ?? HeadingFallback(obj, id);
        }

        return new Entry
        {
            Id = id,
            Name = name ?? $"#{id}",
            StartDate = Normalize((string?)obj["start_date"] ?? (string?)obj["startDate"] ?? ""),
            EndDate = Normalize((string?)obj["end_date"] ?? (string?)obj["endDate"] ?? ""),
            Raw = obj.ToJsonString(Indented),
            Fields = Flatten(obj)
        };
    }

    /// <summary>从 "_"/"en-en" 等本地化节点里取一个可读文本。</summary>
    private static string? Localised(JsonNode? node) => node switch
    {
        JsonValue value => value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null,
        JsonObject obj => (string?)obj["_"] ?? obj.Where(kv => kv.Value is JsonValue).Select(kv => kv.Value!.GetValue<string>()).FirstOrDefault(),
        _ => null
    };

    private static string HeadingFallback(JsonObject obj, int id)
    {
        var type = (int?)obj["content"]?["type"];
        var label = type switch
        {
            0 => "轮播",
            1 => "按钮/槽位",
            2 => "弹窗",
            _ => "条目"
        };
        return $"{label} #{id}";
    }

    /// <summary>把一条条目拍平成「字段名 → 字符串」，供编辑页表单回填。</summary>
    private static Dictionary<string, string> Flatten(JsonObject obj)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(JsonNode? node, string prefix)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var kv in o)
                        Walk(kv.Value, prefix.Length == 0 ? kv.Key : $"{prefix}.{kv.Key}");
                    break;
                case JsonArray a:
                    result[prefix] = a.ToJsonString();
                    break;
                case null:
                    break;
                default:
                    result[prefix] = node.ToString();
                    break;
            }
        }
        Walk(obj, "");
        return result;
    }

    private static int NextId(JsonArray entries)
    {
        var max = 0;
        foreach (var node in entries)
        {
            var id = (int?)node?["id"] ?? (int?)node?["element_id"] ?? (int?)node?["elementId"] ?? 0;
            if (id > max)
                max = id;
        }
        return max + 1;
    }

    private JsonObject ReadRoot(string path)
    {
        if (!File.Exists(path))
            return new JsonObject { ["entries"] = new JsonArray() };

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                       ?? new JsonObject { ["entries"] = new JsonArray() };

            // 客户端既有 frontpage 格式（顶层 elements/targeted）→ 统一成 entries
            if (root["entries"] is not JsonArray)
                EntryArray(root);

            return root;
        }
        catch (JsonException)
        {
            return new JsonObject { ["entries"] = new JsonArray() };
        }
    }

    private (bool ok, string message) Write(string path, JsonObject root, string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (File.Exists(path))
                File.Copy(path, path + ".bak", overwrite: true);

            File.WriteAllText(path, root.ToJsonString(Indented));
            return (true, $"{message}（{path}，原文件已备份为 .bak）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, $"写入 {path} 失败：{ex.Message}");
        }
    }

    private static string DefaultDocument(string path) => "{ \"entries\": [] }";

    /// <summary>把 JSON 里的日期规整成跟抓包一致的 "yyyy-MM-dd HH:mm:ss"。</summary>
    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return value.Replace('T', ' ').Trim();
    }
}
