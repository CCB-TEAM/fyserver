using System.Text.Json;
using System.Text.Json.Nodes;
using fyserver.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace fyserver.Pages;

/// <summary>
/// Knockout（淘汰赛）编辑器。数据存 config/knockout.json，结构：
/// <code>
/// { "entries": [ { "id", "name", "start_date", "end_date",
///                  "deck_rules": {…}, "turn_timers": {…}, "entry_prices": {…},
///                  "rewards": [ {…} ], "texts": { "title": { "EN": … }, … } } ] }
/// </code>
/// 建模字段：基础信息、报名费、牌组规则、回合计时、奖励阶梯、多语言文本；
/// 其余字段通过完整 JSON 原样保留。保存前校验 JSON 并备份为 .bak。
/// </summary>
[IgnoreAntiforgeryToken]
public class KnockoutModel : PageModel
{
    private static readonly string[] Languages = { "EN", "ZH-HANS", "ZH-HANT", "FR", "DE", "IT", "JA", "KO", "PL", "PT", "RU", "ES" };

    private static readonly string[] RewardCurrencies =
        { "gold", "diamonds", "trophies", "medkit_1_d", "medkit_3_d", "medkit_7_d", "core_pack", "random_pack", "officer_pack" };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly ContentEntriesService _entries;

    public KnockoutModel(ContentEntriesService entries) => _entries = entries;

    public sealed class LocalisedText
    {
        public string Language { get; set; } = "";
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public string ShortDescription { get; set; } = "";
        public string LongDescription { get; set; } = "";
    }

    [BindProperty(SupportsGet = true, Name = "id")]
    public int EntryId { get; set; }

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? StartDate { get; set; }
    [BindProperty] public string? EndDate { get; set; }
    [BindProperty] public string? Gold { get; set; }
    [BindProperty] public string? Diamonds { get; set; }
    [BindProperty] public string? Ticket { get; set; }
    [BindProperty] public string? Points { get; set; }
    [BindProperty] public string? TurnLength { get; set; }
    [BindProperty] public string? LengthAfterTimeout { get; set; }
    [BindProperty] public bool AllowReserved { get; set; }
    [BindProperty] public bool AllowMoreAllyCards { get; set; }
    [BindProperty] public bool AllowUnowned { get; set; }
    [BindProperty] public string? RewardsJson { get; set; }
    [BindProperty] public string? EntryJson { get; set; }
    [BindProperty] public List<LocalisedText> Texts { get; set; } = new();

    public string ConfigPath => ContentEntriesService.KnockoutPath;
    public IReadOnlyList<ContentEntriesService.Entry> Entries { get; private set; } = Array.Empty<ContentEntriesService.Entry>();
    public IReadOnlyList<string> LanguageOptions => Languages;
    public IReadOnlyList<string> RewardCurrencyOptions => RewardCurrencies;
    public bool IsNew => EntryId <= 0 && Request.Query.ContainsKey("new");

    /// <summary>true 表示渲染列表；带 ?id=N 或 ?new=1 时渲染表单（直接看查询串，不依赖模型绑定）。</summary>
    public bool ShowList => EntryId <= 0 && !Request.Query.ContainsKey("new");
    public string PageTitle => IsNew ? "新建 Knockout" : $"编辑 Knockout #{EntryId}";

    public IActionResult OnGet()
    {
        Entries = _entries.List(ContentEntriesService.KnockoutPath);

        if (EntryId <= 0)
        {
            Name = "";
            Gold = "0";
            Diamonds = "0";
            TurnLength = "90";
            LengthAfterTimeout = "30";
            RewardsJson = new JsonArray
            {
                new JsonObject { ["trophies"] = 0, ["gold"] = 0, ["core_pack"] = 0 }
            }.ToJsonString(Indented);
            EntryJson = DefaultEntry().ToJsonString(Indented);
            Texts = Languages.Select(l => new LocalisedText { Language = l }).ToList();
            return Page();
        }

        var entry = _entries.Get(ContentEntriesService.KnockoutPath, EntryId);
        if (entry == null)
        {
            TempData["Error"] = $"Knockout 条目 {EntryId} 不存在";
            return RedirectToPage("/Knockout");
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

        entry["entry_prices"] = new JsonObject
        {
            ["gold"] = ParseInt(Gold, 0),
            ["diamonds"] = ParseInt(Diamonds, 0),
            ["ticket"] = ParseInt(Ticket, 0),
            ["points"] = ParseInt(Points, 0)
        };

        var deckRules = entry["deck_rules"] as JsonObject ?? new JsonObject();
        deckRules["allow_reserved"] = AllowReserved;
        deckRules["allow_more_ally_cards"] = AllowMoreAllyCards;
        deckRules["allow_unowned"] = AllowUnowned;
        entry["deck_rules"] = deckRules;

        entry["turn_timers"] = new JsonObject
        {
            ["turn_length"] = ParseInt(TurnLength, 90),
            ["length_after_timeout"] = ParseInt(LengthAfterTimeout, 30)
        };

        try
        {
            entry["rewards"] = JsonNode.Parse(string.IsNullOrWhiteSpace(RewardsJson) ? "[]" : RewardsJson);
        }
        catch (JsonException ex)
        {
            TempData["Error"] = $"奖励 JSON 格式错误：{ex.Message}";
            return RedirectToPage(new { id = EntryId });
        }

        var texts = entry["texts"] as JsonObject ?? new JsonObject();
        foreach (var text in Texts.Where(t => !string.IsNullOrWhiteSpace(t.Language)))
        {
            var language = text.Language.Trim();
            var bucket = texts[language] as JsonObject ?? new JsonObject();
            SetIfNotEmpty(bucket, "title", text.Title);
            SetIfNotEmpty(bucket, "subtitle", text.Subtitle);
            SetIfNotEmpty(bucket, "short_description", text.ShortDescription);
            SetIfNotEmpty(bucket, "long_description", text.LongDescription);
            texts[language] = bucket;
        }
        entry["texts"] = texts;

        var (ok, message) = _entries.Save(ContentEntriesService.KnockoutPath, EntryId, new ContentEntriesService.Entry
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

        return RedirectToPage("/Knockout");
    }

    private void FillFromEntry(string raw)
    {
        try
        {
            if (JsonNode.Parse(raw) is not JsonObject entry)
                return;

            if (entry["entry_prices"] is JsonObject prices)
            {
                Gold = prices["gold"]?.ToString() ?? Gold;
                Diamonds = prices["diamonds"]?.ToString() ?? Diamonds;
                Ticket = prices["ticket"]?.ToString() ?? Ticket;
                Points = prices["points"]?.ToString() ?? Points;
            }

            if (entry["deck_rules"] is JsonObject deckRules)
            {
                AllowReserved = (bool?)deckRules["allow_reserved"] ?? AllowReserved;
                AllowMoreAllyCards = (bool?)deckRules["allow_more_ally_cards"] ?? AllowMoreAllyCards;
                AllowUnowned = (bool?)deckRules["allow_unowned"] ?? AllowUnowned;
            }

            if (entry["turn_timers"] is JsonObject timers)
            {
                TurnLength = timers["turn_length"]?.ToString() ?? TurnLength;
                LengthAfterTimeout = timers["length_after_timeout"]?.ToString() ?? LengthAfterTimeout;
            }

            if (entry["rewards"] is JsonArray rewards)
                RewardsJson = rewards.ToJsonString(Indented);

            var texts = entry["texts"] as JsonObject;
            Texts = Languages.Select(language =>
            {
                var bucket = texts?[language] as JsonObject;
                return new LocalisedText
                {
                    Language = language,
                    Title = (string?)bucket?["title"] ?? "",
                    Subtitle = (string?)bucket?["subtitle"] ?? "",
                    ShortDescription = (string?)bucket?["short_description"] ?? "",
                    LongDescription = (string?)bucket?["long_description"] ?? ""
                };
            }).ToList();
        }
        catch (JsonException)
        {
            // JSON 非法时表单留空，页面仍显示原始 JSON 供修正
        }
    }

    private static void SetIfNotEmpty(JsonObject bucket, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            bucket[key] = value.Trim();
    }

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value?.Trim(), out var parsed) ? parsed : fallback;

    /// <summary>新条目的默认骨架（字段名对齐抓包的 knockout 表单）。</summary>
    private static JsonObject DefaultEntry() => new()
    {
        ["visibility_date"] = "0001-01-01 00:00:00",
        ["deck_rules"] = new JsonObject
        {
            ["allow_reserved"] = false,
            ["allow_more_ally_cards"] = false,
            ["allow_unowned"] = false,
            ["blacklist"] = new JsonArray()
        },
        ["turn_timers"] = new JsonObject { ["turn_length"] = 90, ["length_after_timeout"] = 30 },
        ["entry_prices"] = new JsonObject { ["gold"] = 0, ["diamonds"] = 0, ["ticket"] = 0, ["points"] = 0 },
        ["active_period"] = new JsonObject { ["days"] = new JsonArray(), ["hour_from"] = 0, ["hour_to"] = 24 },
        ["rewards"] = new JsonArray { new JsonObject { ["trophies"] = 0, ["gold"] = 0, ["core_pack"] = 0 } },
        ["texts"] = new JsonObject(),
        ["skirmish_rules"] = new JsonObject()
    };
    /// <summary>列表页的删除操作（独立 URL：/admin/<页面>/delete）。</summary>
    public IActionResult OnPostDelete(int id)
    {
        var (ok, message) = _entries.Delete(ContentEntriesService.KnockoutPath, id);
        if (ok)
            TempData["Message"] = message;
        else
            TempData["Error"] = message;

        return RedirectToPage();
    }
}
