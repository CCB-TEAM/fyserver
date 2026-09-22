using System.Text.Json.Serialization;

namespace fyserver.Models;

// 错误响应相关的 record 类型
public record ErrorResponse(
    string Error,
    string Message,
    int StatusCode
);

public record BannedResponse(
    Error Error,
    string Message,
    int StatusCode
);

public record Error(
    string Code,
    string Description
);

public record SessionResponse(
    string AchievementsUrl,
    List<object> AllKnockoutTourneys,
    int BritainLevel,
    int BritainLevelClaimed,
    int BritainXp,
    List<object> CardsBlacklist,
    int ClaimableCrateLevel,
    int ClientId,
    string Currency,
    Dictionary<string, object> CurrentKnockoutTourney,
    MiniSitNGo CurrentMiniSitNGo,
    string DailymissionsUrl,
    Dictionary<string, object> Decks,
    string DecksUrl,
    int Diamonds,
    string DoubleXpEndDate,
    int DraftAdmissions,
    int Dust,
    string? Email,
    bool EmailRewardReceived,
    bool EmailVerified,
    bool ExtendedRewards,
    int GermanyLevel,
    int GermanyLevelClaimed,
    int GermanyXp,
    int Gold,
    bool HasBeenOfficer,
    string HeartbeatUrl,
    bool IsOfficer,
    bool IsOnline,
    int JapanLevel,
    int JapanLevelClaimed,
    int JapanXp,
    string Jti,
    string Jwt,
    string LastCrateClaimedDate,
    string? LastDailyMissionCancel,
    string LastDailyMissionRenewal,
    string LastLogonDate,
    List<object> LaunchMessages,
    string LibraryUrl,
    string LinkerAccount,
    string Locale,
    Dictionary<string, object> Misc,
    List<object> NewCards,
    Dictionary<string, object> NewPlayerLoginReward,
    bool Npc,
    bool OnlineFlag,
    string PacksUrl,
    int PlayerId,
    string PlayerName,
    string PlayerTag,
    List<object> Rewards,
    string SeasonEnd,
    int SeasonWins,
    string ServerOptions,
    string ServerTime,
    int SovietLevel,
    int SovietLevelClaimed,
    int SovietXp,
    int Stars,
    int TutorialsDone,
    List<string> TutorialsFinished,
    int UsaLevel,
    int UsaLevelClaimed,
    int UsaXp,
    int UserId
);

// 商店相关的数据模型
public record StoreItemData(
    [property: JsonPropertyName("itemType")] string ItemType,
    [property: JsonPropertyName("cardCount")] int? CardCount = null,
    [property: JsonPropertyName("cardSet")] string? CardSet = null,
    [property: JsonPropertyName("guaranteedGoldCards")] int? GuaranteedGoldCards = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("duration")] int? Duration = null,
    [property: JsonPropertyName("isGold")] bool? IsGold = null,
    [property: JsonPropertyName("is_gold_card")] bool? IsGoldCard = null,
    [property: JsonPropertyName("gold_card")] bool? GoldCard = null,
    [property: JsonPropertyName("month")] int? Month = null,
    [property: JsonPropertyName("year")] int? Year = null
);

public record StoreItem(
    [property: JsonPropertyName("data")] StoreItemData Data,
    [property: JsonPropertyName("qty")] int Qty
);

public record StoreOffer(
    [property: JsonPropertyName("offerId")] int OfferId,
    [property: JsonPropertyName("offerName")] string OfferName,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("items")] List<StoreItem>? Items = null,
    [property: JsonPropertyName("bonusItems")] List<StoreItem>? BonusItems = null,
    [property: JsonPropertyName("diamonds")] int? Diamonds = null,
    [property: JsonPropertyName("gold")] int? Gold = null,
    [property: JsonPropertyName("real")] double? Real = null,
    [property: JsonPropertyName("mainImage")] string? MainImage = null,
    [property: JsonPropertyName("thumbnail")] string? Thumbnail = null,
    [property: JsonPropertyName("smallThumb")] string? SmallThumb = null,
    [property: JsonPropertyName("priority")] int? Priority = null,
    [property: JsonPropertyName("limit")] int? Limit = null,
    [property: JsonPropertyName("slotType")] string? SlotType = null,
    [property: JsonPropertyName("slotValue")] string? SlotValue = null,
    [property: JsonPropertyName("timed")] bool? Timed = null,
    [property: JsonPropertyName("bonus")] bool? Bonus = null,
    [property: JsonPropertyName("fulfilAfter")] string? FulfilAfter = null,
    [property: JsonPropertyName("purchased")] bool? Purchased = null
);

public record StoreGroup(
    [property: JsonPropertyName("groupId")] int GroupId,
    [property: JsonPropertyName("group")] int Group,
    [property: JsonPropertyName("startDate")] string StartDate,
    [property: JsonPropertyName("endDate")] string EndDate,
    [property: JsonPropertyName("offers")] List<StoreOffer> Offers,
    [property: JsonPropertyName("hidden")] bool? Hidden = null
);

public record AlwaysFeaturedGroup(
    [property: JsonPropertyName("group")] int Group,
    [property: JsonPropertyName("groupId")] int GroupId,
    [property: JsonPropertyName("startDate")] string StartDate,
    [property: JsonPropertyName("endDate")] string EndDate,
    [property: JsonPropertyName("offers")] List<StoreOffer> Offers
);

public record StoreResponse(
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("groups")] List<StoreGroup> Groups,
    [property: JsonPropertyName("alwaysFeatured")] AlwaysFeaturedGroup AlwaysFeatured,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("ts")] double Ts
);

// 物品相关
public record Item(
    string details,
    string item_id,
    int cnt = 0
);

public record ClaimItemData(
    int Dust,
    [property: JsonPropertyName("isGold")] bool? IsGold,
    [property: JsonPropertyName("itemType")] string ItemType,
    string Name
);

public record ClaimItem(
    ClaimItemData Data,
    int Qty
);

public record Claim(
    int Dust,
    List<ClaimItem> Items
);

// 游戏库和物品相关的 record 类型
public record LibraryItem(
    string CardType,
    int Count,
    int GoldCardCount,
    int Id,
    int RecentlyCraftedCount
);

public record LibraryResponse(
    List<LibraryItem> Cards,
    List<object> NewCards
);

// WebSocket 消息相关的 record 类型
public record WsNotification(
    string Message,
    string Channel,
    string Context,
    DateTime Timestamp,
    string Sender,
    string Receiver
);
