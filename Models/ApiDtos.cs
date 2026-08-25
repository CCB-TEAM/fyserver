namespace fyserver.Models;

// ============================================================================
// 具名响应 DTO：替代各端点中的匿名对象，使其可被 System.Text.Json 源生成
// 上下文（FyJsonContext）覆盖，从而支持 NativeAOT。字段保持原协议命名。
// ============================================================================

/// <summary>卡组摘要（/session 的 decks.headers 与创建卡组响应共用）。</summary>
public record DeckSummaryDto(
    string Name,
    string MainFaction,
    string AllyFaction,
    string CardBack,
    string DeckCode,
    bool Favorite,
    int Id,
    int PlayerId,
    string LastPlayed,
    string CreateDate,
    string ModifyDate
);

/// <summary>空响应（原 new { }）。</summary>
public record EmptyResponseDto();

/// <summary>状态码响应（DELETE /lobbyplayers）。</summary>
public record StatusResponseDto(int Status);

/// <summary>通用计数响应（/admin/users/count）。</summary>
public record CountResponseDto(int Count);

/// <summary>通用消息响应（/admin/users/{id}/ban 等）。</summary>
public record MessageResponseDto(string Message);

/// <summary>管理端用户摘要（/admin/users/list）。</summary>
public record UserSummaryDto(
    int Id,
    string UserName,
    string Name,
    int Tag,
    int DeckCount,
    bool Banned,
    string CreatedAt
);

/// <summary>对局内其他玩家就绪标记。</summary>
public record OtherPlayerReadyDto(int OtherPlayerReady);

/// <summary>对局轮询响应中的 match 对象（/matches/v2/{id}/actions）。</summary>
public record MatchPollDto(
    string PlayerStatusLeft,
    string PlayerStatusRight,
    string Status
);

/// <summary>GET /config 响应。</summary>
public record ServerConfigDto(
    string XserverClosed,
    string XserverClosedHeader,
    string ForgotPasswordUrl
);
