using System.Text.Json;
using System.Text.Json.Nodes;
using fyserver.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace fyserver.Pages;

/// <summary>
/// Skirmish（乱斗）编辑器。数据存 config/skirmish.json，结构：
/// <code>
/// { "entries": [ { "id", "name", "start_date", "end_date",
///                  "rules": { localization, blacklist, reward, max_cards_of_type, turn_length … } } ] }
/// </code>
/// 表单建模常用字段（奖励 / 黑名单 / 回合长度 / 多语言文本），其余字段通过 rules JSON 原样保留，
/// 保存前校验 JSON 并自动备份为 .bak。
/// </summary>
[IgnoreAntiforgeryToken]
public class SkirmishModel : PageModel
{
    private static readonly string[] CardTypes =
        { "infantry", "tank", "artillery", "bomber", "fighter", "gotcha", "order" };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly ContentEntriesService _entries;

    public SkirmishModel(ContentEntriesService entries) => _entries = entries;

    [BindProperty(SupportsGet = true, Name = "id")]
    public int EntryId { get; set; }

    [BindProperty]
    public string? Name { get; set; }

    [BindProperty]
    public string? StartDate { get; set; }

    [BindProperty]
    public string? EndDate { get; set; }

    [BindProperty]
    public string? RewardType { get; set; }

    [BindProperty]
    public string? RewardAmount { get; set; }

    [BindProperty]
    public List<string> Blacklist { get; set; } = new();

    [BindProperty]
    public string? TurnLength { get; set; }

    [BindProperty]
    public string? DescriptionZh { get; set; }

    [BindProperty]
    public string? DescriptionEn { get; set; }

    [BindProperty]
    public string? RulesJson { get; set; }

    public string ConfigPath => ContentEntriesService.SkirmishPath;
    public IReadOnlyList<string> CardTypeOptions => CardTypes;
    public IReadOnlyList<ContentEntriesService.Entry> Entries { get; private set; } = Array.Empty<ContentEntriesService.Entry>();
    public bool IsNew => EntryId <= 0 && Request.Query.ContainsKey("new");

    /// <summary>true 表示渲染列表；带 ?id=N 或 ?new=1 时渲染表单（直接看查询串，不依赖模型绑定）。</summary>
    public bool ShowList => EntryId <= 0 && !Request.Query.ContainsKey("new");
    public string PageTitle => IsNew ? "新建 Skirmish" : $"编辑 Skirmish #{EntryId}";

    public IActionResult OnGet()
    {
        Entries = _entries.List(ContentEntriesService.SkirmishPath);

        if (EntryId <= 0)
        {
            Name = "";
            RewardType = "gold";
            RewardAmount = "100";
            RulesJson = DefaultRules().ToJsonString(Indented);
            return Page();
        }

        var entry = _entries.Get(ContentEntriesService.SkirmishPath, EntryId);
        if (entry == null)
        {
            TempData["Error"] = $"Skirmish 条目 {EntryId} 不存在";
            return RedirectToPage("/Skirmish");
        }

        Name = entry.Name;
        StartDate = entry.StartDate;
        EndDate = entry.EndDate;
        RulesJson = entry.Raw;
        FillFieldsFromRules(entry.Raw);
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
            entry = JsonNode.Parse(RulesJson ?? "{}") as JsonObject ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            TempData["Error"] = $"JSON 格式错误：{ex.Message}";
            return RedirectToPage(new { id = EntryId });
        }

        // 表单字段覆盖 JSON 里的对应位置，rules JSON 作为其余字段的权威来源
        var rules = entry["rules"] as JsonObject ?? new JsonObject();
        rules["reward"] = new JsonObject
        {
            ["type"] = string.IsNullOrWhiteSpace(RewardType) ? "gold" : RewardType.Trim(),
            ["amount"] = ParseInt(RewardAmount, 0)
        };
        rules["blacklist"] = new JsonArray(Blacklist
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => (JsonNode)JsonValue.Create(b.Trim())!)
            .ToArray());
        rules["turn_length"] = ParseInt(TurnLength, 90);

        var localization = rules["localization"] as JsonObject ?? new JsonObject();
        localization["ZH-HANS"] = Merge(localization["ZH-HANS"] as JsonObject, "description", DescriptionZh);
        localization["EN"] = Merge(localization["EN"] as JsonObject, "description", DescriptionEn);
        rules["localization"] = localization;
        entry["rules"] = rules;

        var (ok, message) = _entries.Save(ContentEntriesService.SkirmishPath, EntryId, new ContentEntriesService.Entry
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

        return RedirectToPage("/Skirmish");
    }

    /// <summary>把 rules 里的常用字段抽出来填进表单控件。</summary>
    private void FillFieldsFromRules(string raw)
    {
        try
        {
            if (JsonNode.Parse(raw) is not JsonObject entry || entry["rules"] is not JsonObject rules)
                return;

            if (rules["reward"] is JsonObject reward)
            {
                RewardType = (string?)reward["type"] ?? RewardType;
                RewardAmount = reward["amount"]?.ToString() ?? RewardAmount;
            }

            if (rules["blacklist"] is JsonArray blacklist)
                Blacklist = blacklist.Select(n => n?.ToString() ?? "").Where(s => s.Length > 0).ToList();

            TurnLength = rules["turn_length"]?.ToString() ?? TurnLength;

            if (rules["localization"] is JsonObject localization)
            {
                DescriptionZh = (string?)localization["ZH-HANS"]?["description"];
                DescriptionEn = (string?)localization["EN"]?["description"];
            }
        }
        catch (JsonException)
        {
            // JSON 非法时保持表单默认值，页面上仍会显示原始 JSON 供修正
        }
    }

    private static JsonObject Merge(JsonObject? existing, string key, string? value)
    {
        var target = existing ?? new JsonObject();
        if (value != null)
            target[key] = value;
        return target;
    }

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value?.Trim(), out var parsed) ? parsed : fallback;

    /// <summary>新条目的默认 rules 骨架（字段名对齐抓包）。</summary>
    private static JsonObject DefaultRules() => new()
    {
        ["localization"] = new JsonObject
        {
            ["EN"] = new JsonObject { ["rules_list"] = "", ["description"] = "" },
            ["ZH-HANS"] = new JsonObject { ["rules_list"] = "", ["description"] = "" }
        },
        ["blacklist"] = new JsonArray(),
        ["max_cards_of_type"] = new JsonObject
        {
            ["infantry"] = 0, ["tank"] = 0, ["artillery"] = 0, ["bomber"] = 0,
            ["fighter"] = 0, ["gotcha"] = 0, ["order"] = 0
        },
        ["card_min_cost"] = 0,
        ["starting_kredits"] = 0,
        ["hq_starting_defense"] = 20,
        ["turn_length"] = 90,
        ["reward"] = new JsonObject { ["type"] = "gold", ["amount"] = 100 }
    };
    /// <summary>列表页的删除操作（独立 URL：/admin/<页面>/delete）。</summary>
    public IActionResult OnPostDelete(int id)
    {
        var (ok, message) = _entries.Delete(ContentEntriesService.SkirmishPath, id);
        if (ok)
            TempData["Message"] = message;
        else
            TempData["Error"] = message;

        return RedirectToPage();
    }
}
