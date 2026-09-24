using System.Text.Json;
using System.Text.Json.Nodes;
using fyserver.Models;

namespace fyserver.Services;

/// <summary>
/// 兑换码存储与并发领取。数据保存在所选的玩家数据库中。
/// </summary>
public sealed class RedeemCodeService
{
    private const string StoreKey = "redeem:codes";
    private readonly AppDataStoreService _appData;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _claimGate = new(1, 1);
    private List<RedeemCode>? _codes;

    public RedeemCodeService(AppDataStoreService appData) => _appData = appData;

    public static readonly string[] SupportedItemTypes =
    ["diamonds", "gold", "pack", "card", "draft", "medkit", "prop", "alt_art", "avatar", "cardback", "emote", "token", "deck"];

    public List<RedeemCode> List()
    {
        lock (_gate) return LoadLocked().Select(Clone).ToList();
    }

    public RedeemCode? Get(string code)
    {
        lock (_gate)
        {
            var item = LoadLocked().FirstOrDefault(x => x.Code == code);
            return item == null ? null : Clone(item);
        }
    }

    public (bool Ok, string Message, RedeemCode? Code) Create(string code, string type, List<RedeemReward> rewards, string? expiresAt)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(code)) return (false, "兑换码不能为空", null);
            if (type is not ("single" or "perUser")) return (false, "兑换码类型必须是 single 或 perUser", null);
            if (LoadLocked().Any(x => x.Code == code)) return (false, "兑换码已存在", null);
            var item = new RedeemCode
            {
                Code = code,
                Type = type,
                Rewards = rewards.Select(CloneReward).ToList(),
                ExpiresAt = expiresAt,
                CreatedAt = DateTime.UtcNow.ToString("O"),
                UsedByUsers = type == "perUser" ? [] : null
            };
            LoadLocked().Add(item);
            SaveLocked();
            return (true, "兑换码已创建", Clone(item));
        }
    }

    public (bool Ok, string Message) Delete(string code)
    {
        lock (_gate)
        {
            var list = LoadLocked();
            var item = list.FirstOrDefault(x => x.Code == code);
            if (item == null) return (false, "兑换码不存在");
            list.Remove(item);
            SaveLocked();
            return (true, "兑换码已删除");
        }
    }

    /// <summary>
    /// 在全局领取锁内执行奖励发放，避免两个玩家同时领取 single 兑换码。
    /// 回调返回 null 表示玩家不存在或发放失败，此时不会标记兑换码已使用。
    /// </summary>
    public async Task<RedeemClaimResult> ClaimAsync(
        string code,
        int userId,
        Func<RedeemCode, Task<List<RedeemReward>?>> grant)
    {
        await _claimGate.WaitAsync();
        try
        {
            RedeemCode? item;
            lock (_gate) item = LoadLocked().FirstOrDefault(x => x.Code == code);
            if (item == null || !IsValid(item)) return RedeemClaimResult.Invalid;
            if (item.Type == "single" || string.IsNullOrWhiteSpace(item.Type))
            {
                if (!string.IsNullOrWhiteSpace(item.UsedBy)) return RedeemClaimResult.AlreadyClaimed;
            }
            else if (item.Type == "perUser")
            {
                if ((item.UsedByUsers ?? []).Contains(userId.ToString(), StringComparer.Ordinal))
                    return RedeemClaimResult.AlreadyClaimed;
            }
            else return RedeemClaimResult.Invalid;

            List<RedeemReward>? claimed;
            try { claimed = await grant(Clone(item)); }
            catch (Exception ex) { return new RedeemClaimResult("error", null, ex.Message); }
            if (claimed == null) return new RedeemClaimResult("userNotFound", null, "玩家不存在");

            lock (_gate)
            {
                var current = LoadLocked().FirstOrDefault(x => x.Code == code);
                if (current == null) return RedeemClaimResult.Invalid;
                if (current.Type == "perUser")
                {
                    current.UsedByUsers ??= [];
                    current.UsedByUsers.Add(userId.ToString());
                }
                else current.UsedBy = userId.ToString();
                current.UsedAt = DateTime.UtcNow.ToString("O");
                SaveLocked();
            }
            return new RedeemClaimResult("redeemCodeClaimed", claimed, null);
        }
        finally { _claimGate.Release(); }
    }

    public static bool TryNormalizeRewards(JsonNode? value, out List<RedeemReward> rewards, out string error)
    {
        rewards = [];
        error = "";
        if (value is not JsonArray array || array.Count == 0)
        {
            error = "rewards 必须是非空数组";
            return false;
        }
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonObject raw || raw["data"] is not JsonObject data)
            {
                error = $"rewards[{index}] 必须包含 data 对象";
                return false;
            }
            var itemType = data["itemType"]?.GetValue<string>();
            var qty = raw["qty"] == null ? 1 : ReadInt(raw["qty"]);
            if (itemType == null || !SupportedItemTypes.Contains(itemType, StringComparer.Ordinal) || qty < 1)
            {
                error = $"rewards[{index}] 的 itemType 或 qty 无效";
                return false;
            }
            if (itemType == "pack" && (ReadInt(data["cardCount"]) < 1 || string.IsNullOrWhiteSpace(data["cardSet"]?.GetValue<string>())))
            {
                error = $"rewards[{index}] pack 需要 cardCount 和 cardSet";
                return false;
            }
            if (itemType is "card" or "prop" or "alt_art" or "avatar" or "cardback" or "emote" or "token" or "deck" &&
                string.IsNullOrWhiteSpace(data["name"]?.GetValue<string>()))
            {
                error = $"rewards[{index}] {itemType} 需要 data.name";
                return false;
            }
            if (itemType == "medkit" && ReadInt(data["duration"]) < 0)
            {
                error = $"rewards[{index}] medkit 需要非负 duration";
                return false;
            }
            rewards.Add(new RedeemReward { Data = (JsonObject)data.DeepClone(), Qty = qty });
        }
        return true;
    }

    public static JsonObject ToJson(RedeemCode code, bool includeRewards = true)
    {
        var node = new JsonObject
        {
            ["code"] = code.Code,
            ["type"] = code.Type,
            ["expiresAt"] = code.ExpiresAt,
            ["usedBy"] = code.UsedBy,
            ["usedAt"] = code.UsedAt,
            ["createdAt"] = code.CreatedAt
        };
        if (code.UsedByUsers != null) node["usedByUsers"] = new JsonArray(code.UsedByUsers.Select(value => JsonValue.Create(value)).ToArray());
        if (includeRewards) node["rewards"] = new JsonArray(code.Rewards.Select(RewardJson).ToArray());
        return node;
    }

    private List<RedeemCode> LoadLocked()
    {
        if (_codes != null) return _codes;
        _codes = [];
        try
        {
            var raw = _appData.Get(StoreKey);
            if (raw == null)
            {
                _appData.Set(StoreKey, "[]");
                return _codes;
            }
            if (JsonNode.Parse(raw) is not JsonArray array) return _codes;
            foreach (var node in array.OfType<JsonObject>())
            {
                var code = new RedeemCode
                {
                    Code = node["code"]?.GetValue<string>() ?? "",
                    Type = node["type"]?.GetValue<string>() ?? "single",
                    UsedBy = node["usedBy"]?.GetValue<string>(),
                    UsedAt = node["usedAt"]?.GetValue<string>(),
                    ExpiresAt = node["expiresAt"]?.GetValue<string>(),
                    CreatedAt = node["createdAt"]?.GetValue<string>() ?? DateTime.UtcNow.ToString("O"),
                    UsedByUsers = (node["usedByUsers"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList()
                };
                if (node["rewards"] is JsonArray rewards && TryNormalizeRewards(rewards, out var normalized, out _)) code.Rewards = normalized;
                if (code.Code.Length > 0 && code.Rewards.Count > 0) _codes.Add(code);
            }
        }
        catch (Exception ex) { Console.WriteLine($"兑换码文件读取失败：{ex.Message}"); }
        return _codes;
    }

    private void SaveLocked()
    {
        _appData.Set(StoreKey, new JsonArray(LoadLocked().Select(x => ToJson(x)).ToArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool IsValid(RedeemCode code) => !DateTimeOffset.TryParse(code.ExpiresAt, out var expires) || expires > DateTimeOffset.UtcNow;
    private static int ReadInt(JsonNode? value) => value is JsonValue json && json.TryGetValue<int>(out var number) ? number : -1;
    private static RedeemCode Clone(RedeemCode source) => new()
    {
        Code = source.Code, Type = source.Type, UsedBy = source.UsedBy, UsedAt = source.UsedAt,
        ExpiresAt = source.ExpiresAt, CreatedAt = source.CreatedAt,
        UsedByUsers = source.UsedByUsers?.ToList(), Rewards = source.Rewards.Select(r => new RedeemReward { Qty = r.Qty, Data = (JsonObject)r.Data.DeepClone() }).ToList()
    };

    private static RedeemReward CloneReward(RedeemReward source) => new()
    {
        Qty = source.Qty,
        Data = (JsonObject)source.Data.DeepClone()
    };

    private static JsonObject RewardJson(RedeemReward reward) => new()
    {
        ["data"] = reward.Data.DeepClone(),
        ["qty"] = reward.Qty
    };
}

public sealed record RedeemClaimResult(string Status, List<RedeemReward>? Items, string? Error)
{
    public static readonly RedeemClaimResult Invalid = new("redeemCodeInvalid", null, null);
    public static readonly RedeemClaimResult AlreadyClaimed = new("redeemCodeAlreadyClaimed", null, null);
}
