using System.Text.Json;
using System.Text.Json.Nodes;
using fyserver.Models;

namespace fyserver.Services;

/// <summary>
/// Runtime card catalog and first-player collection policy. The seed file is imported once;
/// after that the selected database is the sole source of truth.
/// </summary>
public sealed class CardCatalogService(AppDataStoreService appData)
{
    private const string CatalogKey = "cards:catalog:v1";
    private const string PolicyKey = "players:initial-library:v1";
    private static readonly HashSet<string> ExcludedSets = new(StringComparer.OrdinalIgnoreCase)
        { "Homefront", "Special", "Placeholder", "Expansion1", "OnlySpawnable" };
    private static readonly HashSet<string> Rarities = new(StringComparer.Ordinal)
        { "Common", "Uncommon", "Rare", "Unique" };
    private static readonly string[] Wildcards =
        ["card_wildcard_standard", "card_wildcard_limited", "card_wildcard_special", "card_wildcard_elite"];
    private readonly object _gate = new();
    private JsonObject? _cards;
    private JsonObject? _policy;

    public void EnsureInitialized()
    {
        lock (_gate)
        {
            if (_cards != null && _policy != null) return;
            if (!appData.IsReady) throw new InvalidOperationException("玩家数据库尚未初始化");

            var catalogJson = appData.Get(CatalogKey);
            if (catalogJson == null)
            {
                var seedPath = Path.Combine(AppContext.BaseDirectory, "cards-from-fmodel.json");
                if (!File.Exists(seedPath)) throw new FileNotFoundException("缺少初始卡牌目录 cards-from-fmodel.json", seedPath);
                catalogJson = File.ReadAllText(seedPath);
                _ = JsonNode.Parse(catalogJson) as JsonObject
                    ?? throw new InvalidDataException("初始卡牌目录必须是以卡牌 ID 为键的 JSON 对象");
                appData.Set(CatalogKey, catalogJson);
            }

            _cards = JsonNode.Parse(catalogJson) as JsonObject
                ?? throw new InvalidDataException("数据库中的卡牌目录格式无效");
            var policyJson = appData.Get(PolicyKey);
            _policy = policyJson == null ? CreateDefaultPolicy() : JsonNode.Parse(policyJson) as JsonObject;
            _policy ??= CreateDefaultPolicy();
            if (policyJson == null) appData.Set(PolicyKey, _policy.ToJsonString());
        }
    }

    public JsonObject Query(string? search, string? cardSet, string? type, int? minKredits, int? maxKredits, int page, int pageSize)
    {
        EnsureInitialized();
        lock (_gate)
        {
            var filtered = _cards!
                .Where(pair => Matches(pair.Key, pair.Value as JsonObject, search, cardSet, type, minKredits, maxKredits))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var safePage = Math.Max(1, page);
            var safeSize = Math.Clamp(pageSize, 1, 100);
            var items = new JsonArray();
            foreach (var (id, node) in filtered.Skip((safePage - 1) * safeSize).Take(safeSize))
            {
                var item = node?.DeepClone() as JsonObject ?? new JsonObject();
                item["id"] = id;
                item["packEligible"] = IsPackEligible(id, item, core: false);
                item["corePackEligible"] = IsPackEligible(id, item, core: true);
                items.Add(item);
            }
            return new JsonObject
            {
                ["items"] = items,
                ["total"] = filtered.Count,
                ["page"] = safePage,
                ["pageSize"] = safeSize,
                ["cardSets"] = new JsonArray(_cards!.Select(pair => pair.Value).Select(node => node?["cardSet"]?.GetValue<string>() ?? node?["cardset"]?.GetValue<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                    .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["types"] = new JsonArray(_cards!.Select(pair => pair.Value).Select(node => node?["type"]?.GetValue<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                    .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
            };
        }
    }

    public JsonObject? GetCard(string cardId)
    {
        EnsureInitialized();
        lock (_gate) return _cards![cardId]?.DeepClone() as JsonObject;
    }

    public Dictionary<int, List<string>> GetPackPool(string cardSet, bool coreOnly = false)
    {
        EnsureInitialized();
        lock (_gate)
        {
            var pool = new Dictionary<int, List<string>> { [0] = [], [1] = [], [2] = [], [3] = [] };
            var normalizedSet = cardSet == "Core" ? "Basic" : cardSet;
            foreach (var (id, node) in _cards!)
            {
                if (node is not JsonObject row || !IsPackEligible(id, row, coreOnly)) continue;
                var set = GetString(row, "cardSet") ?? GetString(row, "cardset");
                if (!coreOnly && !string.Equals(set, normalizedSet, StringComparison.OrdinalIgnoreCase)) continue;
                var rarity = GetString(row, "rarity") switch
                {
                    "Common" => 0,
                    "Uncommon" => 1,
                    "Rare" => 2,
                    "Unique" => 3,
                    _ => -1
                };
                if (rarity >= 0) pool[rarity].Add(id);
            }
            return pool;
        }
    }

    public bool IsPackEligible(string cardId, bool core = false)
    {
        EnsureInitialized();
        lock (_gate) return _cards![cardId] is JsonObject row && IsPackEligible(cardId, row, core);
    }

    public (bool Ok, string Message) UpdateCardMetadata(string cardId, JsonObject changes)
    {
        EnsureInitialized();
        lock (_gate)
        {
            if (_cards![cardId] is not JsonObject row) return (false, "卡牌不存在");
            foreach (var key in new[] { "title", "text", "type", "rarity", "cardSet", "faction", "kredits", "operationcost", "attack", "defense", "range", "isReserved" })
            {
                if (!changes.ContainsKey(key)) continue;
                var value = changes[key];
                if (key == "rarity" && value != null && (value is not JsonValue rarityValue || !rarityValue.TryGetValue<string>(out var rarityText) || !Rarities.Contains(rarityText)))
                    return (false, "稀有度只能是 Common、Uncommon、Rare 或 Unique");
                if (key == "kredits" && value != null && (!int.TryParse(value.ToString(), out var kredits) || kredits is < 0 or > 100))
                    return (false, "Kredits 必须是 0 到 100 之间的整数");
                if (key == "isReserved" && value != null && !bool.TryParse(value.ToString(), out _))
                    return (false, "isReserved 必须是布尔值");
                if (key is "title" or "text" or "type" or "rarity" or "cardSet" or "faction")
                {
                    if (value != null && (value is not JsonValue stringValue || !stringValue.TryGetValue<string>(out var text))) return (false, $"{key} 必须是字符串");
                    if (value is JsonValue candidate && candidate.TryGetValue<string>(out var textValue) && textValue.Length > 2048) return (false, $"{key} 长度不能超过 2048 个字符");
                }
            }
            foreach (var key in new[] { "title", "text", "type", "rarity", "cardSet", "faction", "kredits", "operationcost", "attack", "defense", "range", "isReserved" })
                if (changes.ContainsKey(key)) row[key] = changes[key]?.DeepClone();
            row["cardset"] = row["cardSet"]?.DeepClone();
            appData.Set(CatalogKey, _cards.ToJsonString());
            return (true, "卡牌资料已保存");
        }
    }

    public JsonObject BuildClientLibrary()
    {
        EnsureInitialized();
        lock (_gate)
        {
            var rows = new JsonArray();
            foreach (var (id, node) in _cards!)
            {
                if (node is not JsonObject row || !IsClientLibraryCard(row)) continue;
                rows.Add(new JsonObject
                {
                    ["card_type"] = id,
                    ["count"] = 4,
                    ["gold_card_count"] = 5,
                    ["id"] = 1902,
                    ["recently_crafted_count"] = 0
                });
            }
            return new JsonObject { ["cards"] = rows, ["new_cards"] = new JsonArray() };
        }
    }

    public JsonObject GetInitialLibraryPolicy()
    {
        EnsureInitialized();
        lock (_gate)
        {
            var result = (JsonObject)_policy!.DeepClone();
            var entries = new JsonArray();
            if (result["defaultCards"] is JsonObject selected)
            {
                foreach (var (id, count) in selected)
                {
                    var row = _cards![id] as JsonObject;
                    entries.Add(new JsonObject
                    {
                        ["cardId"] = id,
                        ["count"] = count?.GetValue<int>() ?? 0,
                        ["name"] = row?["title"]?.GetValue<string>() ?? row?["name"]?.GetValue<string>() ?? id,
                        ["cardSet"] = row?["cardSet"]?.GetValue<string>() ?? row?["cardset"]?.GetValue<string>() ?? "",
                        ["type"] = row?["type"]?.GetValue<string>() ?? "",
                        ["kredits"] = row?["kredits"]?.DeepClone()
                    });
                }
            }
            result["defaultCards"] = entries;
            return result;
        }
    }

    public (bool Ok, string Message) SaveInitialLibraryPolicy(JsonObject body)
    {
        EnsureInitialized();
        var mode = body["mode"]?.GetValue<string>();
        if (mode is not ("all" or "selected")) return (false, "新玩家卡牌模式必须是 all 或 selected");
        if (body["allGold"] is not JsonValue allGoldNode || !allGoldNode.TryGetValue<bool>(out var allGold))
            return (false, "allGold 必须是布尔值");
        if (body["defaultCards"] is not JsonArray entries) return (false, "defaultCards 必须是数组");

        var selected = new JsonObject();
        foreach (var entry in entries)
        {
            if (entry is not JsonObject item) return (false, "默认卡牌配置格式无效");
            var cardId = item["cardId"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(cardId) || _cards![cardId] is not JsonObject)
                return (false, $"卡牌不存在：{cardId}");
            if (!int.TryParse(item["count"]?.ToString(), out var count) || count is < 1 or > 1000)
                return (false, $"卡牌 {cardId} 数量必须在 1 到 1000 之间");
            selected[cardId] = count;
        }

        lock (_gate)
        {
            _policy = new JsonObject { ["mode"] = mode, ["allGold"] = allGold, ["defaultCards"] = selected };
            appData.Set(PolicyKey, _policy.ToJsonString());
        }
        return (true, "新玩家卡牌设置已保存");
    }

    public (bool Ok, string Message, int Added) AddFilteredCardsToDefaults(string? search, string? cardSet, string? type,
        int? minKredits, int? maxKredits, int count)
    {
        EnsureInitialized();
        if (count is < 1 or > 1000) return (false, "默认卡牌数量必须在 1 到 1000 之间", 0);
        lock (_gate)
        {
            var selected = _policy!["defaultCards"] as JsonObject ?? new JsonObject();
            var matches = _cards!.Where(pair => Matches(pair.Key, pair.Value as JsonObject, search, cardSet, type, minKredits, maxKredits)).ToList();
            foreach (var (id, _) in matches) selected[id] = count;
            _policy["defaultCards"] = selected;
            // Adding defaults selects the curated mode; the full-collection mode remains one click away.
            _policy["mode"] = "selected";
            appData.Set(PolicyKey, _policy.ToJsonString());
            return (true, $"已将 {matches.Count} 张筛选结果加入新玩家默认卡牌", matches.Count);
        }
    }

    public void ApplyInitialCollection(User user)
    {
        EnsureInitialized();
        lock (_gate)
        {
            user.UserCards ??= new UserCardCollection();
            user.UserCards.Cards.Clear();
            var countById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.Equals((string?)_policy!["mode"], "all", StringComparison.Ordinal))
            {
                foreach (var (id, node) in _cards!)
                    if (node is JsonObject row && IsClientLibraryCard(row) && !string.Equals((string?)row["type"], "wildcard", StringComparison.OrdinalIgnoreCase))
                        countById[id] = 4;
            }
            else if (_policy["defaultCards"] is JsonObject defaults)
            {
                foreach (var (id, node) in defaults)
                    if (int.TryParse(node?.ToString(), out var count) && count > 0 && _cards!.ContainsKey(id)) countById[id] = count;
            }

            var allGold = _policy["allGold"]?.GetValue<bool>() ?? false;
            foreach (var (id, count) in countById)
                user.UserCards.Cards.Add(new UserCard(id, allGold ? 0 : count, allGold ? count : 0, 0, 0));
            foreach (var wildcard in Wildcards)
                if (!countById.ContainsKey(wildcard)) user.UserCards.Cards.Add(new UserCard(wildcard, 0, 0, 0, 0));
        }
    }

    private static JsonObject CreateDefaultPolicy() => new()
    {
        ["mode"] = "selected",
        ["allGold"] = false,
        ["defaultCards"] = new JsonObject
        {
            ["card_location_stalingrad"] = 1,
            ["card_location_changchun"] = 1,
            ["card_location_cherbourg"] = 1,
            ["card_location_london"] = 1,
            ["card_location_berlin"] = 1
        }
    };

    private static bool IsClientLibraryCard(JsonObject row)
    {
        var set = GetString(row, "cardSet") ?? GetString(row, "cardset");
        return !string.IsNullOrWhiteSpace(set) && !ExcludedSets.Contains(set) &&
               !string.IsNullOrWhiteSpace(GetString(row, "faction")) && Rarities.Contains(GetString(row, "rarity") ?? "");
    }

    private static bool IsPackEligible(string id, JsonObject row, bool core)
    {
        var set = GetString(row, "cardSet") ?? GetString(row, "cardset");
        var eligible = Rarities.Contains(GetString(row, "rarity") ?? "") && !string.IsNullOrWhiteSpace(set) && !ExcludedSets.Contains(set) &&
                       !string.IsNullOrWhiteSpace(GetString(row, "faction")) &&
                       !string.Equals(GetString(row, "type"), "location", StringComparison.OrdinalIgnoreCase) &&
                       !id.StartsWith("card_location", StringComparison.OrdinalIgnoreCase);
        return eligible && (!core || !(row["isReserved"]?.GetValue<bool>() ?? row["is_reserved"]?.GetValue<bool>() ?? false));
    }

    private static bool Matches(string id, JsonObject? row, string? search, string? cardSet, string? type, int? minKredits, int? maxKredits)
    {
        if (row == null) return false;
        var set = GetString(row, "cardSet") ?? GetString(row, "cardset");
        if (!string.IsNullOrWhiteSpace(cardSet) && !string.Equals(cardSet, set, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(type) && !string.Equals(type, GetString(row, "type"), StringComparison.OrdinalIgnoreCase)) return false;
        var kredits = row["kredits"]?.GetValue<int?>();
        if (minKredits.HasValue && (!kredits.HasValue || kredits < minKredits)) return false;
        if (maxKredits.HasValue && (!kredits.HasValue || kredits > maxKredits)) return false;
        return string.IsNullOrWhiteSpace(search) || id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               (GetString(row, "title")?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (GetString(row, "name")?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string? GetString(JsonObject row, string key) => row[key] is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}
