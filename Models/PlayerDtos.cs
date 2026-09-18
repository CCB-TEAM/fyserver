using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace fyserver.Models;

public record ProviderDetail(
    string PaymentProvider
);

// Session DTOs as records
public record Session(
    string Provider,
    ProviderDetail ProviderDetails,
    string ClientType,
    string Build,
    string PlatformType,
    string AppGuid,
    string Version,
    string PlatformInfo,
    string PlatformVersion,
    string AccountLinking,
    string Language,
    bool AutomaticAccountCreation,
    string Username,
    string Password
);

public record MiniSitNGo(
    string EndDate,
    [property: JsonPropertyName("hasWon")] bool HasWon,
    int Id,
    string Name,
    string RulesJsonStr,
    string StartDate
);

public record Entitlement(
    string EntitlementType,
    string Name
);

public record FriendsReponse(
    List<int> Friends,
    List<int> PreviousOpponents
);

// Config records
public record CurrentUser(
    int ClientId,
    int Exp,
    string ExternalId,
    int Iat,
    int IdentityId,
    string Iss,
    string Jti,
    string Language,
    string Payment,
    string PlayerId,
    string Provider,
    List<string> Roles,
    string Tier,
    int UserId,
    string UserName
)
{
    /// <summary>
    /// /session（GET /）下发的 roles。参照 dev 服抓包，对所有用户统一分发同一组角色，
    /// 不做权限管理（客户端只根据该字段解锁 dev/vip/tester 等界面）。
    /// 静态成员不参与 JSON 序列化，因此响应里只会出现 roles 数组本身。
    /// </summary>
    public static IReadOnlyList<string> DefaultRoles { get; } = new[]
    {
        "spectator",
        "vip",
        "dev",
        "internal_tester",
        "support",
        "tester"
    };
}

public record Endpoints(
    string Draft,
    string Email,
    string Lobbyplayers,
    string Matches,
    string Matches2,
    string MyDraft,
    string MyItems,
    string MyPlayer,
    string Players,
    string Purchase,
    string Root,
    string Session,
    string Store,
    string Tourneys,
    string Transactions,
    string ViewOffers
);

public record Config(
    CurrentUser? CurrentUser,
    Endpoints Endpoints
);

// Library/Cards records
public record Card(
    string CardType,
    int Count,
    int GoldCardCount,
    int Id,
    int RecentlyCraftedCount
);

public record Library(
    List<Card> Cards,
    List<object> NewCards
);

public record ItemsResponse(
    string Date,
    List<EquippedItem> EquippedItems,
    List<Item> Items
);

// WebSocket messages as records
/// <summary>WebSocket 消息。MatchId 用 JsonElement 宽容接收（客户端可能发数字/字符串/缺失）。</summary>
public record WebSocketMessage(
    string Timestamp,
    string? Context = "",
    string Message = "",
    string Channel = "",
    string? Sender = "",
    string? Receiver = "",
    JsonElement? MatchId = null
);

public record ClientInfo(
    User User,
    WebSocket Client
);
