using System.Text.Json;
using System.Text.Json.Nodes;
using fyserver.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace fyserver.Pages;

/// <summary>
/// 首页 / 公告编辑器（带类游戏预览）。
///
/// 数据存 config/frontpage.json，读写兼容两种结构：
///   1. 客户端既有格式：顶层 elements / targeted 数组，文本字段是单串；
///   2. dev 后台格式：{"entries":[…]}，content 下的多语言映射 {"_": …, "zh-hans": …}。
/// 保存统一写成 entries 结构。
///
/// 预览完全在浏览器端完成：camelCase 化的条目 JSON 交给 admin-frontpage-preview.js，
/// 用游戏客户端的真实画布尺寸（轮播 1540×770 / 按钮 614×307 / 弹窗 1232×564）画 SVG。
/// </summary>
[IgnoreAntiforgeryToken]
public class FrontpageModel : PageModel
{
    private static readonly string[] Languages =
        { "zh-hans", "en-en", "zh-hant", "ja-jp", "ko-kr", "ru-ru", "de-de", "fr-fr", "es-es", "it-it", "pl-pl", "pt-br" };

    private static readonly string[] EntryTypes = { "0|轮播 Carousel", "1|侧栏按钮 Sidebar", "2|弹窗 Popup" };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly ContentEntriesService _entries;

    public FrontpageModel(ContentEntriesService entries) => _entries = entries;

    [BindProperty(SupportsGet = true, Name = "id")]
    public int EntryId { get; set; }

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public int Type { get; set; }
    [BindProperty] public string? ImageUrl { get; set; }
    [BindProperty] public string? Link { get; set; }
    [BindProperty] public string? HeadingText { get; set; }
    [BindProperty] public string? HeadingSize { get; set; }
    [BindProperty] public string? SubHeadingText { get; set; }
    [BindProperty] public string? SubHeadingSize { get; set; }
    [BindProperty] public string? StartDate { get; set; }
    [BindProperty] public string? EndDate { get; set; }
    [BindProperty] public bool IsPublished { get; set; }
    [BindProperty] public string? EntryJson { get; set; }

    public string ConfigPath => ContentEntriesService.FrontpagePath;
    public IReadOnlyList<ContentEntriesService.Entry> Entries { get; private set; } = Array.Empty<ContentEntriesService.Entry>();
    public IReadOnlyList<string> LanguageOptions => Languages;
    public IReadOnlyList<string> TypeOptions => EntryTypes;
    public bool IsNew => EntryId <= 0 && Request.Query.ContainsKey("new");

    /// <summary>true 表示渲染列表；带 ?id=N 或 ?new=1 时渲染表单（直接看查询串，不依赖模型绑定）。</summary>
    public bool ShowList => EntryId <= 0 && !Request.Query.ContainsKey("new");
    public string PageTitle => IsNew ? "新建首页条目" : $"编辑首页条目 #{EntryId}";
    public string InitialLanguage { get; private set; } = "zh-hans";

    public IActionResult OnGet()
    {
        Entries = _entries.List(ContentEntriesService.FrontpagePath);

        if (EntryId <= 0)
        {
            Name = "";
            Type = 0;
            HeadingSize = "56";
            SubHeadingSize = "30";
            IsPublished = true;
            EntryJson = "{}";
            return Page();
        }

        var entry = _entries.Get(ContentEntriesService.FrontpagePath, EntryId);
        if (entry == null)
        {
            TempData["Error"] = $"首页条目 {EntryId} 不存在";
            return RedirectToPage("/Frontpage");
        }

        Name = entry.Name;
        StartDate = entry.StartDate;
        EndDate = entry.EndDate;
        EntryJson = entry.Raw;
        FillFromEntry(entry.Raw);
        return Page();
    }

    public IActionResult OnPost()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            TempData["Error"] = "名称不能为空";
            return RedirectToPage(new { id = EntryId });
        }

        JsonObject entry;
        try
        {
            entry = JsonNode.Parse(EntryJson ?? "{}") as JsonObject ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            TempData["Error"] = $"JSON 格式错误：{ex.Message}";
            return RedirectToPage(new { id = EntryId });
        }

        var content = entry["content"] as JsonObject ?? new JsonObject();
        content["type"] = Type;
        SetLocalised(content, "image_url", ImageUrl);
        SetLocalised(content, "link", Link);
        SetHeading(content, "heading", HeadingText, HeadingSize);
        SetHeading(content, "sub_heading", SubHeadingText, SubHeadingSize);
        entry["content"] = content;
        entry["is_published"] = IsPublished;

        var (ok, message) = _entries.Save(ContentEntriesService.FrontpagePath, EntryId, new ContentEntriesService.Entry
        {
            Name = Name,
            StartDate = StartDate ?? "",
            EndDate = EndDate ?? "",
            Raw = entry.ToJsonString(Indented)
        });

        if (ok)
            TempData["Message"] = message;
        else
            TempData["Error"] = message;

        return RedirectToPage("/Frontpage");
    }

    private void FillFromEntry(string raw)
    {
        try
        {
            if (JsonNode.Parse(raw) is not JsonObject entry || entry["content"] is not JsonObject content)
                return;

            Type = (int?)content["type"] ?? Type;
            IsPublished = (bool?)entry["is_published"] ?? IsPublished;
            ImageUrl = ReadLocalised(content["image_url"]);
            Link = ReadLocalised(content["link"]);
            HeadingText = ReadLocalised(content["heading"]?["text"]);
            HeadingSize = ReadLocalised(content["heading"]?["font_size"]) ?? HeadingSize;
            SubHeadingText = ReadLocalised(content["sub_heading"]?["text"]);
            SubHeadingSize = ReadLocalised(content["sub_heading"]?["font_size"]) ?? SubHeadingSize;
        }
        catch (JsonException)
        {
            // JSON 非法时保持空表单，页面上仍可编辑原始 JSON
        }
    }

    /// <summary>兼容双模式读取：单串（既有客户端格式）或 {"_":…,"zh-hans":…}（dev 格式）。</summary>
    private static string? ReadLocalised(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value => value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToString(),
        JsonObject obj => (string?)obj["_"] ?? (string?)obj["zh-hans"] ?? (string?)obj["en-en"],
        _ => node.ToString()
    };

    /// <summary>双模式写入：单串直接写字符串；多语言对象只改 "_" 默认值，保留其它语言。</summary>
    private static void SetLocalised(JsonObject content, string key, string? value)
    {
        if (value == null)
            return;

        if (content[key] is JsonObject existing)
            existing["_"] = value;
        else
            content[key] = value;
    }

    private static void SetHeading(JsonObject content, string key, string? text, string? fontSize)
    {
        if (content[key] is not JsonObject heading)
        {
            heading = new JsonObject();
            content[key] = heading;
        }

        if (text != null)
        {
            if (heading["text"] is JsonObject textObj)
                textObj["_"] = text;
            else
                heading["text"] = text;
        }

        if (int.TryParse(fontSize?.Trim(), out var size) && size > 0)
        {
            if (heading["font_size"] is JsonObject sizeObj)
                sizeObj["_"] = size;
            else
                heading["font_size"] = size;
        }
    }
    /// <summary>
    /// 列表页的删除（POST /admin/frontpage?handler=Delete&amp;targetId=N）。
    /// 注意：targetId 必须走 URL 查询串，不能用表单字段——本宿主用 CreateSlimBuilder，
    /// POST 表单值绑定到 handler 参数会得到 0（表现为"删除返回 302 但条目还在"），查询串参数则正常。
    /// </summary>
    public IActionResult OnPostDelete(int targetId)
    {
        var (ok, message) = _entries.Delete(ContentEntriesService.FrontpagePath, targetId);
        if (ok)
            TempData["Message"] = message;
        else
            TempData["Error"] = message;

        return RedirectToPage();
    }
}
