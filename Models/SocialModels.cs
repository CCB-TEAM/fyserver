namespace fyserver.Models;

/// <summary>好友关系。一对玩家最多一条记录：RequesterId 为发起方，AddresseeId 为接收方。</summary>
public sealed record FriendshipRecord(int RequesterId, int AddresseeId, string Status, DateTime CreatedAt, DateTime UpdatedAt)
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";

    public bool Involves(int userId) => RequesterId == userId || AddresseeId == userId;
    public int Other(int userId) => RequesterId == userId ? AddresseeId : RequesterId;
}

/// <summary>持久化的好友私聊消息。Delivered 表示已经推送到接收方的 WebSocket。</summary>
public sealed record ChatMessageRecord(long Id, int SenderId, int ReceiverId, string Body, string Context, DateTime CreatedAt, bool Delivered);

/// <summary>local 存储模式下好友与聊天数据的落盘快照（data/social/social.json）。</summary>
public sealed class SocialLocalSnapshot
{
    public long NextMessageId { get; set; } = 1;
    public List<FriendshipRecord> Friendships { get; set; } = [];
    public List<ChatMessageRecord> Messages { get; set; } = [];
}

/// <summary>好友系统与服务器小助手的运行参数（data/social-settings.json，后台可改）。</summary>
public sealed record SocialSettings(
    string AssistantName,
    string WelcomeMessage,
    int MaxFriends,
    int MaxPendingRequests,
    int HistoryDays,
    int HistoryPerFriend,
    int MaxMessageLength,
    bool ReplayOwnMessages)
{
    public static SocialSettings Default { get; } = new(
        AssistantName: "服务器小助手",
        WelcomeMessage: "欢迎回来！你可以发送 /帮助 查看小助手的所有指令。",
        MaxFriends: 100,
        MaxPendingRequests: 50,
        HistoryDays: 30,
        HistoryPerFriend: 50,
        MaxMessageLength: 500,
        ReplayOwnMessages: true);

    public SocialSettings Normalize() => this with
    {
        AssistantName = string.IsNullOrWhiteSpace(AssistantName) ? Default.AssistantName : AssistantName.Trim()[..Math.Min(AssistantName.Trim().Length, 32)],
        WelcomeMessage = (WelcomeMessage ?? "").Trim()[..Math.Min((WelcomeMessage ?? "").Trim().Length, 500)],
        MaxFriends = Math.Clamp(MaxFriends, 1, 1000),
        MaxPendingRequests = Math.Clamp(MaxPendingRequests, 1, 500),
        HistoryDays = Math.Clamp(HistoryDays, 1, 3650),
        HistoryPerFriend = Math.Clamp(HistoryPerFriend, 0, 500),
        MaxMessageLength = Math.Clamp(MaxMessageLength, 10, 2000)
    };
}
