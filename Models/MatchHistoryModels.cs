namespace fyserver.Models;

/// <summary>可公开展示的对局历史摘要。不要在此 DTO 放置认证信息或隐藏牌数据。</summary>
public sealed record MatchHistorySummary(
    int MatchId,
    string MatchType,
    string Status,
    DateTime StartedAt,
    DateTime? CompletedAt,
    int LeftPlayerId,
    string LeftPlayerName,
    string LeftPlayerTag,
    int RightPlayerId,
    string RightPlayerName,
    string RightPlayerTag,
    int Turns,
    int ActionCount,
    string? WinnerSide
);

/// <summary>独立于运行时 MatchInfo 的持久化回放文档。</summary>
public sealed class MatchHistoryDocument
{
    public MatchHistorySummary Summary { get; set; } = null!;
    public MatchStartingInfo? StartingInfo { get; set; }
    public List<MatchAction> Actions { get; set; } = [];
}

public sealed record MatchHistoryActionPage(
    int MatchId,
    int NextActionId,
    bool HasMore,
    List<MatchAction> Actions
);

public sealed record MatchHistoryListResponse(List<MatchHistorySummary> Matches);
public sealed record MatchHistoryDetailResponse(MatchHistorySummary Summary, MatchStartingInfo? StartingInfo);

public sealed record MatchRetentionSettings(
    string Mode,
    int KeepCount,
    int KeepDays,
    int CleanupDayUtc,
    int CleanupHourUtc
);
