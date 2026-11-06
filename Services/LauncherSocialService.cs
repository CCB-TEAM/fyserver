using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using fyserver.Models;

namespace fyserver.Services;

/// <summary>
/// 启动器侧的好友、私聊、服务器小助手和“邀请进房”，按启动器账号 id 存储
/// （fy_launcher_friendships / fy_launcher_chat_messages，local 模式为 data/social/launcher.json）。
/// 与游戏内好友（FriendService）完全独立。推送走启动器控制 WebSocket；历史记录由启动器按会话分页拉取，
/// 未读状态用消息的 delivered 字段表示（启动器打开会话时调用“已读”）。
/// </summary>
public sealed class LauncherSocialService
{
    /// <summary>小助手的虚拟账号 id（启动器账号 id 从 1 开始）。</summary>
    public const int AssistantId = 0;

    private readonly SocialService _store;
    private readonly SocialService _settingsSource;
    private readonly LauncherAccountService _accounts;
    private readonly LauncherHubService _hub;
    private readonly LauncherRoomService _rooms;
    private readonly ConcurrentDictionary<int, Queue<DateTime>> _rate = new();

    public LauncherSocialService(SocialService store, SocialService settingsSource, LauncherAccountService accounts,
        LauncherHubService hub, LauncherRoomService rooms)
    {
        _store = store;
        _settingsSource = settingsSource;
        _accounts = accounts;
        _hub = hub;
        _rooms = rooms;
        hub.Connected = OnConnectedAsync;
        hub.Disconnected = OnDisconnectedAsync;
        hub.MessageReceived = OnMessageAsync;
        rooms.PresenceChanged = async ids =>
        {
            foreach (var id in ids.Distinct()) await BroadcastPresenceAsync(id);
        };
    }

    private SocialSettings Settings => _settingsSource.GetSettings();
    private string AssistantName => Settings.AssistantName;

    public readonly record struct Result(bool Ok, int Status, string Message, JsonObject? Data = null);

    // ==================== 好友 ====================

    public async Task<HashSet<int>> FriendIdsAsync(int accountId) =>
        (await _store.GetFriendshipsAsync(accountId)).Where(f => f.Status == FriendshipRecord.Accepted).Select(f => f.Other(accountId)).ToHashSet();

    public async Task<bool> AreFriendsAsync(int a, int b) =>
        (await _store.GetFriendshipAsync(a, b))?.Status == FriendshipRecord.Accepted;

    public async Task<JsonObject> ListFriendsAsync(int accountId)
    {
        var unread = await _store.GetUnreadCountsAsync(accountId);
        var friends = new JsonArray
        {
            new JsonObject
            {
                ["accountId"] = AssistantId, ["username"] = AssistantName, ["status"] = "accepted",
                ["online"] = true, ["roomId"] = null, ["assistant"] = true, ["unread"] = unread.GetValueOrDefault(AssistantId)
            }
        };
        foreach (var f in await _store.GetFriendshipsAsync(accountId))
        {
            var otherId = f.Other(accountId);
            var name = _accounts.GetUsername(otherId);
            if (name == null) continue;
            var accepted = f.Status == FriendshipRecord.Accepted;
            friends.Add(new JsonObject
            {
                ["accountId"] = otherId,
                ["username"] = name,
                ["status"] = accepted ? "accepted" : f.RequesterId == accountId ? "request_sent" : "request_received",
                ["online"] = accepted && _hub.IsOnline(otherId),
                ["roomId"] = accepted ? RoomIdVisibleTo(otherId, accountId) : null,
                ["unread"] = unread.GetValueOrDefault(otherId),
                ["since"] = f.UpdatedAt.ToString("O")
            });
        }
        return new JsonObject { ["ok"] = true, ["friends"] = friends };
    }

    /// <summary>我和每个有关系的账号的好友状态：accepted / request_sent / request_received。</summary>
    public async Task<Dictionary<int, string>> RelationsAsync(int accountId)
    {
        var result = new Dictionary<int, string>();
        foreach (var f in await _store.GetFriendshipsAsync(accountId))
            result[f.Other(accountId)] = f.Status == FriendshipRecord.Accepted ? "accepted" : f.RequesterId == accountId ? "request_sent" : "request_received";
        return result;
    }

    /// <summary>好友当前所在的房间号（任何可见性的房间好友都能看到）。</summary>
    private string? RoomIdVisibleTo(int friendId, int viewerId) => _rooms.RoomOf(friendId)?.Id;

    /// <summary>按账号名发好友申请；对方已向我申请时即为同意。</summary>
    public async Task<Result> AddAsync(int accountId, string? username, int? targetIdInput)
    {
        int targetId;
        if (targetIdInput is > 0) targetId = targetIdInput.Value;
        else if (_accounts.FindByUsername(username) is { } found) targetId = found.Id;
        else return new(false, 404, "找不到这个启动器账号");
        if (_accounts.GetUsername(targetId) == null) return new(false, 404, "找不到这个启动器账号");
        if (targetId == accountId) return new(false, 400, "不能添加自己为好友");

        var settings = Settings;
        var result = await _store.MutateFriendshipAsync(accountId, targetId, async existing =>
        {
            if (existing is { Status: FriendshipRecord.Pending } && existing.RequesterId == targetId)
            {
                if (await CountAcceptedAsync(accountId) >= settings.MaxFriends)
                    return (FriendshipMutation.None, new Result(false, 409, $"好友数量已达上限（{settings.MaxFriends}个）"));
                if (await CountAcceptedAsync(targetId) >= settings.MaxFriends)
                    return (FriendshipMutation.None, new Result(false, 409, "对方好友数量已达上限"));
                return (new FriendshipMutation(FriendshipMutationKind.Accept, targetId, accountId, existing.CreatedAt), new Result(true, 200, "已成为好友"));
            }
            if (existing is { Status: FriendshipRecord.Accepted }) return (FriendshipMutation.None, new Result(false, 409, "你们已经是好友了"));
            if (existing is { Status: FriendshipRecord.Pending }) return (FriendshipMutation.None, new Result(false, 409, "好友申请已发送，请等待对方同意"));
            var mine = await _store.GetFriendshipsAsync(accountId);
            if (mine.Count(f => f.Status == FriendshipRecord.Accepted) >= settings.MaxFriends)
                return (FriendshipMutation.None, new Result(false, 409, $"好友数量已达上限（{settings.MaxFriends}个）"));
            if (mine.Count(f => f.Status == FriendshipRecord.Pending && f.RequesterId == accountId) >= settings.MaxPendingRequests)
                return (FriendshipMutation.None, new Result(false, 409, $"好友申请已达上限（{settings.MaxPendingRequests}个）"));
            var theirs = await _store.GetFriendshipsAsync(targetId);
            if (theirs.Count(f => f.Status == FriendshipRecord.Pending && f.AddresseeId == targetId) >= settings.MaxPendingRequests)
                return (FriendshipMutation.None, new Result(false, 409, "对方待处理的好友申请已满"));
            return (new FriendshipMutation(FriendshipMutationKind.CreatePending, accountId, targetId), new Result(true, 201, "好友申请已发送"));
        });
        if (result.Ok)
        {
            await _hub.SendAsync(targetId, new JsonObject
            {
                ["type"] = "friends_changed",
                ["reason"] = result.Status == 201 ? "request_received" : "accepted",
                ["from"] = new JsonObject { ["accountId"] = accountId, ["username"] = _accounts.GetUsername(accountId) }
            });
            if (result.Status == 200) await SendPresenceAsync(accountId, targetId);
        }
        return result with { Data = new JsonObject { ["accountId"] = targetId } };
    }

    /// <summary>删除好友，或拒绝/撤回好友申请。</summary>
    public async Task<Result> RemoveAsync(int accountId, int friendId)
    {
        if (friendId == AssistantId) return new(false, 403, "不能删除服务器小助手");
        var removed = await _store.MutateFriendshipAsync(accountId, friendId, existing => Task.FromResult(existing == null
            ? (FriendshipMutation.None, false)
            : (new FriendshipMutation(FriendshipMutationKind.Delete, accountId, friendId), true)));
        if (!removed) return new(false, 404, "好友关系不存在");
        await _hub.SendAsync(friendId, new JsonObject { ["type"] = "friends_changed", ["reason"] = "removed", ["from"] = new JsonObject { ["accountId"] = accountId } });
        return new(true, 200, "已删除");
    }

    private async Task<int> CountAcceptedAsync(int accountId) =>
        (await _store.GetFriendshipsAsync(accountId)).Count(f => f.Status == FriendshipRecord.Accepted);

    // ==================== 在线状态 ====================

    private async Task OnConnectedAsync(int accountId)
    {
        var friendIds = await FriendIdsAsync(accountId);
        var unread = await _store.GetUnreadCountsAsync(accountId);
        var room = _rooms.RoomOf(accountId);
        await _hub.SendAsync(accountId, new JsonObject
        {
            ["type"] = "hello",
            ["accountId"] = accountId,
            ["username"] = _accounts.GetUsername(accountId),
            ["onlineFriends"] = new JsonArray(friendIds.Where(_hub.IsOnline).Select(id => (JsonNode?)id).ToArray()),
            ["unreadTotal"] = unread.Values.Sum(),
            ["roomId"] = room?.Id
        });
        await BroadcastPresenceAsync(accountId, friendIds);
    }

    private async Task OnDisconnectedAsync(int accountId)
    {
        _rate.TryRemove(accountId, out _);
        await BroadcastPresenceAsync(accountId, await FriendIdsAsync(accountId));
    }

    /// <summary>把该账号的在线状态和所在房间推给在线的好友（房间进出时也调用）。</summary>
    public async Task BroadcastPresenceAsync(int accountId, ISet<int>? friendIds = null)
    {
        friendIds ??= await FriendIdsAsync(accountId);
        foreach (var friendId in friendIds)
            if (_hub.IsOnline(friendId)) await SendPresenceAsync(accountId, friendId);
    }

    private Task<bool> SendPresenceAsync(int accountId, int toId) => _hub.SendAsync(toId, new JsonObject
    {
        ["type"] = "presence",
        ["accountId"] = accountId,
        ["online"] = _hub.IsOnline(accountId),
        ["roomId"] = _rooms.RoomOf(accountId)?.Id
    });

    // ==================== 私聊 ====================

    public async Task<Result> GetHistoryAsync(int accountId, int friendId, long? beforeId, int limit)
    {
        if (friendId != AssistantId && !await AreFriendsAsync(accountId, friendId)) return new(false, 403, "对方不是你的好友");
        var rows = await _store.GetConversationAsync(accountId, friendId, beforeId ?? long.MaxValue, limit);
        return new(true, 200, "ok", new JsonObject
        {
            ["messages"] = new JsonArray(rows.Select(m => (JsonNode?)MessageJson(m)).ToArray()),
            ["hasMore"] = rows.Count >= Math.Clamp(limit, 1, 200)
        });
    }

    public async Task<Result> MarkReadAsync(int accountId, int friendId)
    {
        await _store.MarkConversationReadAsync(accountId, friendId);
        return new(true, 200, "ok");
    }

    public async Task<Result> SendChatAsync(int accountId, int toId, string? text)
    {
        var body = (text ?? "").Trim();
        if (body.Length == 0) return new(false, 400, "消息不能为空");
        if (!TryConsumeRate(accountId)) return new(false, 429, "发送太频繁了，请稍后再试");
        if (toId == AssistantId)
        {
            if (body.Length > 200) return new(false, 400, "指令太长了");
            var commandAt = DateTime.UtcNow;
            var commandId = await _store.AddMessageAsync(accountId, AssistantId, body, "", commandAt, delivered: true);
            var commandRecord = new ChatMessageRecord(commandId, accountId, AssistantId, body, "", commandAt, true);
            var reply = await AssistantReplyAsync(accountId, body);
            var at = DateTime.UtcNow;
            var replyId = await _store.AddMessageAsync(AssistantId, accountId, reply, AssistantName, at, delivered: true);
            var replyRecord = new ChatMessageRecord(replyId, AssistantId, accountId, reply, AssistantName, at, true);
            await _hub.SendAsync(accountId, new JsonObject { ["type"] = "chat", ["message"] = MessageJson(replyRecord) });
            return new(true, 200, "ok", new JsonObject { ["message"] = MessageJson(commandRecord), ["reply"] = MessageJson(replyRecord) });
        }
        var settings = Settings;
        if (body.Length > settings.MaxMessageLength) return new(false, 400, $"消息太长了，最多 {settings.MaxMessageLength} 个字");
        if (!await AreFriendsAsync(accountId, toId)) return new(false, 403, "对方不是你的好友");
        var createdAt = DateTime.UtcNow;
        var id = await _store.AddMessageAsync(accountId, toId, body, _accounts.GetUsername(accountId) ?? "", createdAt, delivered: false);
        var record = new ChatMessageRecord(id, accountId, toId, body, "", createdAt, false);
        await _hub.SendAsync(toId, new JsonObject { ["type"] = "chat", ["message"] = MessageJson(record) });
        return new(true, 200, "ok", new JsonObject { ["message"] = MessageJson(record) });
    }

    private async Task OnMessageAsync(int accountId, JsonObject message)
    {
        var type = message["type"]?.GetValue<string>();
        if (type == "chat")
        {
            var to = message["to"] is JsonValue v && v.TryGetValue<int>(out var t) ? t : -1;
            var result = await SendChatAsync(accountId, to, message["text"]?.GetValue<string>());
            // 回执：带上客户端自己的 clientId，方便启动器把“发送中”的气泡换成正式消息或标红。
            await _hub.SendAsync(accountId, new JsonObject
            {
                ["type"] = "chat_ack",
                ["clientId"] = message["clientId"]?.DeepClone(),
                ["ok"] = result.Ok,
                ["message"] = result.Ok ? result.Data?["message"]?.DeepClone() : null,
                ["error"] = result.Ok ? null : result.Message
            });
        }
        else if (type == "read" && message["with"] is JsonValue w && w.TryGetValue<int>(out var with))
        {
            await MarkReadAsync(accountId, with);
        }
    }

    private bool TryConsumeRate(int accountId)
    {
        var queue = _rate.GetOrAdd(accountId, _ => new Queue<DateTime>());
        lock (queue)
        {
            var now = DateTime.UtcNow;
            while (queue.Count > 0 && now - queue.Peek() > TimeSpan.FromSeconds(10)) queue.Dequeue();
            if (queue.Count >= 8) return false;
            queue.Enqueue(now);
            return true;
        }
    }

    private static JsonObject MessageJson(ChatMessageRecord m) => new()
    {
        ["id"] = m.Id,
        ["from"] = m.SenderId,
        ["to"] = m.ReceiverId,
        ["text"] = m.Body,
        ["at"] = DateTime.SpecifyKind(m.CreatedAt, DateTimeKind.Utc).ToString("O"),
        ["read"] = m.Delivered
    };

    // ==================== 邀请进房 ====================

    public async Task<Result> InviteAsync(int accountId, string roomId, int friendId)
    {
        var room = _rooms.Get(roomId);
        if (room == null || !room.Members.ContainsKey(accountId)) return new(false, 404, "你不在这个房间里");
        if (!await AreFriendsAsync(accountId, friendId)) return new(false, 403, "只能邀请好友");
        if (!_hub.IsOnline(friendId)) return new(false, 409, "对方不在线");
        if (room.Members.ContainsKey(friendId)) return new(false, 409, "对方已经在房间里了");
        if (!_rooms.AddInvite(room.Id, accountId, friendId)) return new(false, 404, "你不在这个房间里");
        await _hub.SendAsync(friendId, new JsonObject
        {
            ["type"] = "room_invite",
            ["roomId"] = room.Id,
            ["roomName"] = room.Name,
            // 受邀者 10 分钟内进房不用密码
            ["hasPassword"] = false,
            ["hostName"] = room.HostName,
            ["playerCount"] = room.Members.Count,
            ["maxPlayers"] = room.MaxPlayers,
            ["from"] = new JsonObject { ["accountId"] = accountId, ["username"] = _accounts.GetUsername(accountId) }
        });
        return new(true, 200, "邀请已发送");
    }

    // ==================== 服务器小助手 ====================

    private Task<string> AssistantReplyAsync(int accountId, string text)
    {
        var command = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0].TrimStart('/', '／');
        var reply = command switch
        {
            "帮助" or "help" => "小助手指令：\n/帮助 查看指令\n/在线 查看在线人数和房间数\n/房间 查看你所在的房间",
            "在线" or "在线人数" => $"启动器在线：{_hub.OnlineCount}\n房间：{_rooms.All().Count} 个，房内玩家 {_rooms.All().Sum(r => r.Members.Count)} 人",
            "房间" => _rooms.RoomOf(accountId) is { } room
                ? $"你在房间 {room.Id}「{room.Name}」，房主 {room.HostName}，{room.Members.Count}/{room.MaxPlayers} 人"
                : "你现在不在任何房间里",
            _ => "未知指令，发送 /帮助 查看所有指令"
        };
        return Task.FromResult(reply);
    }

    /// <summary>后台以小助手身份给启动器账号发消息（accountId 为 null 时发给所有在线账号）。</summary>
    public async Task<int> SendAdminMessageAsync(int? accountId, string text)
    {
        var targets = accountId is { } id ? new[] { id } : _hub.OnlineIds();
        foreach (var target in targets)
        {
            var at = DateTime.UtcNow;
            var messageId = await _store.AddMessageAsync(AssistantId, target, text, AssistantName, at, delivered: false);
            await _hub.SendAsync(target, new JsonObject
            {
                ["type"] = "chat",
                ["message"] = MessageJson(new ChatMessageRecord(messageId, AssistantId, target, text, AssistantName, at, false))
            });
        }
        return targets.Length;
    }

    public Task<(long Friendships, long Pending, long Messages)> GetStatsAsync() => _store.GetStatsAsync();
}
