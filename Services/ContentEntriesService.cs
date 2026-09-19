using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Globalization;

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
        public bool IsPublished { get; set; } = true;
        public bool IsTargeted { get; set; }
        public int? Type { get; set; }
        public int? Priority { get; set; }
        public int? Slot { get; set; }
        public string Status { get; set; } = "";
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
        foreach (var node in EntryNodes(root, path))
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
        var isFrontpage = path == FrontpagePath;
        var entries = isFrontpage ? EnsureArray(root, "elements") : EntryArray(root);
        var targeted = isFrontpage ? EnsureArray(root, "targeted") : null;
        var allEntries = targeted == null ? entries.ToList() : entries.Concat(targeted).ToList();
        var idKey = isFrontpage ? "elementId" : "id";
        var payloadKey = isFrontpage ? "content" : "rules";

        if (!TryDate(input.StartDate, out var start) || !TryDate(input.EndDate, out var end) ||
            (!string.IsNullOrWhiteSpace(input.StartDate) && !string.IsNullOrWhiteSpace(input.EndDate) && start > end))
            return (false, "开始和结束时间必须是有效日期，且开始不能晚于结束");

        JsonObject target;
        if (id > 0)
        {
            var existing = allEntries.FirstOrDefault(n => EntryId(n) == id) as JsonObject;
            if (existing == null)
                return (false, $"条目 {id} 不存在");
            target = existing;
        }
        else
        {
            target = new JsonObject { [idKey] = NextId(allEntries) };
            entries.Add(target);
        }

        var assignedId = EntryId(target);

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

            // 首页与乱斗编辑器都提交完整条目；以 JSON 为准，允许高级编辑器删除字段。
            // 淘汰赛仍采用合并策略，避免旧格式表单意外丢字段。
            if (isFrontpage || path == SkirmishPath) target.Clear();

            // id / 名称 / 日期由表单管理，其余键原样带回
            foreach (var kv in payload.ToList())
            {
                if (kv.Key is "id" or "element_id" or "elementId" or "name" or "start_date" or "end_date" or "startDate" or "endDate")
                    continue;

                if (kv.Key == payloadKey && kv.Value is JsonObject nested)
                {
                    var current = target[payloadKey] as JsonObject;
                    if (current == null)
                    {
                        current = new JsonObject();
                        target[payloadKey] = current;
                    }
                    foreach (var inner in nested)
                        current[inner.Key] = inner.Value is { } innerValue ? innerValue.DeepClone() : null;
                }
                else
                {
                    target[kv.Key] = kv.Value is { } value ? value.DeepClone() : null;
                }
            }
        }

        target["name"] = input.Name.Trim();
        if (!isFrontpage) target["id"] = assignedId;
        target[isFrontpage ? "startDate" : "start_date"] = string.IsNullOrWhiteSpace(input.StartDate) ? "0001-01-01T00:00:00Z" : input.StartDate.Trim().Replace(' ', 'T');
        target[isFrontpage ? "endDate" : "end_date"] = string.IsNullOrWhiteSpace(input.EndDate) ? "9999-12-31T23:59:00Z" : input.EndDate.Trim().Replace(' ', 'T');

        if (isFrontpage && targeted != null)
        {
            target.Remove("element_id");
            target.Remove("start_date");
            target.Remove("end_date");
            target["elementId"] = assignedId;
            target["isPublished"] = (bool?)target["isPublished"] ?? (bool?)target["is_published"] ?? true;
            target["isTargeted"] = (bool?)target["isTargeted"] ?? (bool?)target["is_targeted"] ?? false;
            target.Remove("is_published");
            target.Remove("is_targeted");
            var destination = (bool?)target["isTargeted"] == true ? targeted : entries;
            if (!destination.Contains(target))
            {
                entries.Remove(target);
                targeted.Remove(target);
                destination.Add(target);
            }
        }

        return Write(path, root, id > 0 ? $"已保存条目 {id}" : "已新增条目");
    }

    /// <summary>删除一条。</summary>
    public (bool ok, string message) Delete(string path, int id)
    {
        var root = ReadRoot(path);
        var arrays = path == FrontpagePath
            ? new[] { EnsureArray(root, "elements"), EnsureArray(root, "targeted") }
            : new[] { EntryArray(root) };
        var entries = arrays.FirstOrDefault(array => array.Any(node => EntryId(node) == id));
        var node = entries?.FirstOrDefault(n => EntryId(n) == id);
        if (node == null)
            return (false, $"条目 {id} 不存在");

        entries!.Remove(node);
        return Write(path, root, $"已删除条目 {id}");
    }

    public (bool ok, string message) SetFrontpagePublished(int id, bool published)
    {
        var root = ReadRoot(FrontpagePath);
        var item = EntryNodes(root, FrontpagePath).FirstOrDefault(node => EntryId(node) == id) as JsonObject;
        if (item == null) return (false, $"条目 {id} 不存在");
        item["isPublished"] = published;
        item.Remove("is_published");
        return Write(FrontpagePath, root, published ? $"已发布条目 {id}" : $"已取消发布条目 {id}");
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

        if (path == FrontpagePath)
        {
            NormalizeFrontpageRoot(root);
            if (root["elements"] is not JsonArray || root["targeted"] is not JsonArray)
                return (false, "首页 JSON 必须包含 elements 和 targeted 数组（也接受镜像的 rows 数组）");
        }
        else if (root["entries"] is not JsonArray)
            return (false, "内容 JSON 必须包含 entries 数组");

        var ids = new HashSet<int>();
        foreach (var item in EntryNodes(root, path))
        {
            if (item is not JsonObject entry || EntryId(entry) <= 0 || !ids.Add(EntryId(entry)))
                return (false, "条目 ID 缺失、重复或不是正整数");
            var start = (string?)entry["startDate"] ?? (string?)entry["start_date"] ?? "";
            var end = (string?)entry["endDate"] ?? (string?)entry["end_date"] ?? "";
            if (!TryDate(start, out var startAt) || !TryDate(end, out var endAt) ||
                (!string.IsNullOrWhiteSpace(start) && !string.IsNullOrWhiteSpace(end) && startAt > endAt))
                return (false, $"条目 {EntryId(entry)} 的发布时间无效");
        }
        return Write(path, root, "已保存（原文件已备份为 .bak）");
    }

    // ---------------------------------------------------------------- 内部

    private static IEnumerable<JsonNode?> EntryNodes(JsonObject root, string path)
    {
        if (path == FrontpagePath)
            return EnsureArray(root, "elements").Concat(EnsureArray(root, "targeted"));
        return root["entries"] is JsonArray entries ? entries : Enumerable.Empty<JsonNode?>();
    }

    private static JsonArray EnsureArray(JsonObject root, string key)
    {
        if (root[key] is JsonArray array) return array;
        array = new JsonArray();
        root[key] = array;
        return array;
    }

    private static int EntryId(JsonNode? node) => (int?)node?["elementId"] ?? (int?)node?["element_id"] ?? (int?)node?["id"] ?? 0;

    private static void NormalizeFrontpageRoot(JsonObject root)
    {
        if (root["rows"] is JsonArray rows && root["entries"] is not JsonArray)
        {
            root["entries"] = rows.DeepClone();
            root.Remove("rows");
            root.Remove("total");
            root.Remove("totalNotFiltered");
        }
        if (root["entries"] is not JsonArray legacy) return;
        var global = EnsureArray(root, "elements");
        var targeted = EnsureArray(root, "targeted");
        foreach (var node in legacy)
        {
            if (node is not JsonObject item) continue;
            var copy = (JsonObject)item.DeepClone();
            copy["elementId"] = EntryId(copy);
            copy.Remove("element_id");
            copy.Remove("id");
            copy["startDate"] = (string?)copy["startDate"] ?? (string?)copy["start_date"] ?? "0001-01-01T00:00:00Z";
            copy["endDate"] = (string?)copy["endDate"] ?? (string?)copy["end_date"] ?? "9999-12-31T23:59:00Z";
            copy.Remove("start_date");
            copy.Remove("end_date");
            var isTargeted = (bool?)copy["isTargeted"] ?? (bool?)copy["is_targeted"] ?? false;
            copy["isTargeted"] = isTargeted;
            copy["isPublished"] = (bool?)copy["isPublished"] ?? (bool?)copy["is_published"] ?? true;
            copy.Remove("is_targeted");
            copy.Remove("is_published");
            (isTargeted ? targeted : global).Add(copy);
        }
        root.Remove("entries");
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
        var id = EntryId(obj);

        // frontpage 条目没有 name：优先取本地化标题，其次按类型给个可读名字
        var name = Localised(obj["name"]);
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
            IsPublished = (bool?)obj["isPublished"] ?? (bool?)obj["is_published"] ?? true,
            IsTargeted = (bool?)obj["isTargeted"] ?? (bool?)obj["is_targeted"] ?? false,
            Type = (int?)obj["content"]?["type"],
            Priority = (int?)obj["content"]?["priority"],
            Slot = (int?)obj["content"]?["slot"],
            Status = PublicationStatus(obj, DateTimeOffset.UtcNow),
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

    private static int NextId(IEnumerable<JsonNode?> entries)
    {
        var max = 0;
        foreach (var node in entries)
        {
            var id = EntryId(node);
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

            if (path == FrontpagePath) NormalizeFrontpageRoot(root);

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

            var temp = path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(Indented));
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            File.Move(temp, path, true);
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

    private static bool TryDate(string value, out DateTimeOffset date)
    {
        if (string.IsNullOrWhiteSpace(value)) { date = default; return true; }
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
    }

    private static string PublicationStatus(JsonObject item, DateTimeOffset now)
    {
        if ((bool?)item["isPublished"] == false || (bool?)item["is_published"] == false) return "unpublished";
        var startText = (string?)item["startDate"] ?? (string?)item["start_date"] ?? "";
        var endText = (string?)item["endDate"] ?? (string?)item["end_date"] ?? "";
        if (!TryDate(startText, out var start) || !TryDate(endText, out var end)) return "invalid_date";
        if (!string.IsNullOrWhiteSpace(startText) && now < start) return "scheduled";
        if (!string.IsNullOrWhiteSpace(endText) && now > end) return "expired";
        return "active";
    }

    /// <summary>按发布时间生成客户端格式，不修改存储文件。定向规则未实现前绝不向所有玩家广播定向条目。</summary>
    public string ReadPublishedFrontpage()
    {
        var root = ReadRoot(FrontpagePath);
        var now = DateTimeOffset.UtcNow;
        foreach (var key in new[] { "elements", "targeted" })
        {
            var source = EnsureArray(root, key);
            var active = new JsonArray();
            if (key == "targeted")
            {
                root[key] = active;
                continue;
            }
            foreach (var node in source)
            {
                if (node is JsonObject item && PublicationStatus(item, now) == "active")
                {
                    var copy = (JsonObject)item.DeepClone();
                    copy.Remove("name");
                    active.Add(copy);
                }
            }
            root[key] = active;
        }
        root["changed"] = true;
        return root.ToJsonString();
    }
}
