using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using fyserver.Models;

namespace fyserver.Services;

/// <summary>
/// 游戏内好友系统：好友列表 / 申请 / 同意 / 删除、在线状态推送、好友私聊（持久化）、服务器小助手。
///
/// 协议参照 comet：
///   GET    /players/{id}/friends    返回 friends + previous_opponents
///   POST   /players/{id}/friends    {friend_name, friend_id, friend_tag} 发申请；对方已向我申请时即为同意
///   DELETE /players/{id}/friends    {friend_id}
///   WebSocket channel "refresh_friends" 让客户端重新拉好友列表；channel "chat" 为私聊。
///
/// 与 comet 的区别：私聊会先写入数据库再转发。对方离线时消息标记为未送达，上线后补发；
/// 玩家重新登录（重启游戏）后，服务器把最近的聊天记录按原时间重新推送给客户端，
/// 因为客户端只在内存里保留聊天内容。
/// </summary>
public sealed class FriendService : IWebSocketSocialHandler
{
    /// <summary>服务器小助手的虚拟玩家 id（真实玩家 id 在 100000 以上，不会冲突）。</summary>
    public const int AssistantId = 1;

    private readonly SocialService _social;
    private readonly UserStoreService _users;
    private readonly WebSocketHubService _hub;
    private readonly MatchManagerService _matches;
    private readonly MatchHistoryService _history;
    private readonly FriendChallengeService _challenges;

    // 刚通过 /session 登录、客户端内存里还没有聊天记录的玩家；下一次 WebSocket 连接时补发完整历史。
    private readonly ConcurrentDictionary<int, DateTime> _freshLogins = new();
    // 已连接但还没补发历史的玩家：值为连接时间与本次连接的序号（防止旧连接的延迟任务误发）。
    private readonly ConcurrentDictionary<int, PendingReplay> _pendingReplays = new();
    private long _connectionSerial;
    // 私聊频率限制：每名玩家最近的发送时间。
    private readonly ConcurrentDictionary<int, Queue<DateTime>> _rate = new();
    private const int RateLimitCount = 8;
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromSeconds(10);

    private sealed record PendingReplay(long Serial, DateTime ConnectedAt, bool Fresh);

    public FriendService(SocialService social, UserStoreService users, WebSocketHubService hub, MatchManagerService matches,
        MatchHistoryService history, FriendChallengeService challenges)
    {
        _social = social;
        _users = users;
        _hub = hub;
        _matches = matches;
        _history = history;
        _challenges = challenges;
    }

    private SocialSettings Settings => _social.GetSettings();

    // ==================== 好友列表 ====================

    public async Task<JsonObject> GetFriendsJsonAsync(int userId)
    {
        // 客户端拉好友列表说明界面已就绪，此时补发聊天记录最稳妥。
        TriggerReplaySoon(userId, TimeSpan.FromSeconds(1));

        var friendships = await _social.GetFriendshipsAsync(userId);
        var friends = new JsonArray();
        foreach (var f in friendships)
        {
            var otherId = f.Other(userId);
            var other = await _users.GetByIdAsync(otherId);
            if (other == null) continue;
            var status = f.Status == FriendshipRecord.Accepted ? "accepted"
                : f.RequesterId == userId ? "request_sent" : "request_received";
            var online = _hub.IsOnline(otherId);
            friends.Add(new JsonObject
            {
                ["player_id"] = otherId,
                ["player_name"] = other.Name,
                ["player_tag"] = other.Tag,
                ["is_online"] = online,
                ["busy_status"] = online && _matches.IsInRealMatch(otherId) ? "pvp" : null,
                ["status"] = status
            });
        }
        friends.Add(new JsonObject
        {
            ["player_id"] = AssistantId,
            ["player_name"] = Settings.AssistantName,
            ["player_tag"] = 0,
            ["is_online"] = true,
            ["busy_status"] = null,
            ["status"] = "accepted"
        });

        return new JsonObject
        {
            ["friends"] = friends,
            ["previous_opponents"] = await GetPreviousOpponentsAsync(userId)
        };
    }

    /// <summary>最近 10 场真人对局的对手。卡组取对手最近使用的卡组代码（对局记录里没有保存双方卡组 id）。</summary>
    private async Task<JsonArray> GetPreviousOpponentsAsync(int userId)
    {
        var result = new JsonArray();
        IReadOnlyList<MatchHistorySummary> recent;
        try { recent = await _history.ListRecentAsync(20, completedOnly: false, playerId: userId); }
        catch (Exception ex)
        {
            Console.WriteLine($"读取最近对手失败：{ex.GetBaseException().Message}");
            return result;
        }
        var seen = new HashSet<int>();
        foreach (var match in recent)
        {
            var opponentId = match.LeftPlayerId == userId ? match.RightPlayerId : match.LeftPlayerId;
            if (opponentId <= 0 || opponentId == userId || !seen.Add(opponentId)) continue;
            var opponent = await _users.GetByIdAsync(opponentId);
            var fallbackName = match.LeftPlayerId == userId ? match.RightPlayerName : match.LeftPlayerName;
            var fallbackTag = match.LeftPlayerId == userId ? match.RightPlayerTag : match.LeftPlayerTag;
            var deck = opponent?.Decks.Values.OrderByDescending(d => d.LastPlayed).FirstOrDefault()?.DeckCode ?? "";
            result.Add(new JsonObject
            {
                ["deck"] = deck.Split('~')[0],
                ["name"] = opponent?.Name ?? fallbackName,
                ["player_id"] = opponentId,
                ["tag"] = opponent?.Tag ?? (int.TryParse(fallbackTag, out var t) ? t : 0)
            });
            if (result.Count >= 10) break;
        }
        return result;
    }

    public async Task<JsonObject?> GetProfileJsonAsync(int playerId)
    {
        if (playerId == AssistantId)
            return Profile(null, Settings.AssistantName, 0);
        var user = await _users.GetByIdAsync(playerId);
        if (user == null) return null;
        var avatar = user.EquippedItem.FirstOrDefault(i => i.Slot == "avatar")?.ItemId;
        return Profile(avatar, user.Name, user.Tag);
    }

    private static JsonObject Profile(string? avatar, string name, int tag) => new()
    {
        ["avatar"] = avatar,
        ["medals"] = new JsonArray(),
        ["player_name"] = name,
        ["player_tag"] = tag,
        ["ribbons"] = new JsonArray()
    };

    // ==================== 申请 / 同意 / 删除 ====================

    public readonly record struct FriendResult(int StatusCode, string? Error, int FriendId);

    public async Task<FriendResult> AddOrAcceptAsync(int userId, JsonObject body)
    {
        var friendName = FriendChallengeService.Str(body["friend_name"])?.Trim();
        var friendId = FriendChallengeService.Int(body["friend_id"]) ?? -1;
        var friendTag = FriendChallengeService.Int(body["friend_tag"]) ?? -1;

        // 定位目标玩家：按 id，或按 名字#tag。
        User? target = null;
        if (friendId > 0)
        {
            if (friendId == AssistantId) return await Fail(userId, StatusCodes.Status409Conflict, "服务器小助手已经是你的好友了");
            target = await _users.GetByIdAsync(friendId);
        }
        else if (!string.IsNullOrEmpty(friendName))
        {
            var all = await _users.GetAllUsersAsync();
            target = all.FirstOrDefault(u => u.Tag == friendTag && string.Equals(u.Name, friendName, StringComparison.Ordinal))
                     ?? (friendTag < 0 ? null : all.FirstOrDefault(u => u.Tag == friendTag && string.Equals(u.Name, friendName, StringComparison.OrdinalIgnoreCase)));
        }
        if (target == null) return await Fail(userId, StatusCodes.Status404NotFound, "你要添加的好友不存在");
        if (target.Id == userId) return await Fail(userId, StatusCodes.Status400BadRequest, "不能添加自己为好友");

        var settings = Settings;
        var targetId = target.Id;
        var result = await _social.MutateFriendshipAsync(userId, targetId, async existing =>
        {
            // 对方已经向我发过申请：这次请求就是同意。
            if (existing is { Status: FriendshipRecord.Pending } && existing.RequesterId == targetId)
            {
                var mine = await CountAcceptedAsync(userId);
                if (mine >= settings.MaxFriends)
                    return (FriendshipMutation.None, new FriendResult(StatusCodes.Status409Conflict, $"好友数量已达上限（{settings.MaxFriends}个），请清理好友后再试", targetId));
                var theirs = await CountAcceptedAsync(targetId);
                if (theirs >= settings.MaxFriends)
                    return (FriendshipMutation.None, new FriendResult(StatusCodes.Status409Conflict, $"对方好友数量已达上限（{settings.MaxFriends}个）", targetId));
                return (new FriendshipMutation(FriendshipMutationKind.Accept, targetId, userId, existing.CreatedAt),
                    new FriendResult(StatusCodes.Status201Created, null, targetId));
            }
            if (existing is { Status: FriendshipRecord.Accepted })
                return (FriendshipMutation.None, new FriendResult(StatusCodes.Status409Conflict, "你们已经是好友了，不能重复添加", targetId));
            if (existing is { Status: FriendshipRecord.Pending })
                return (FriendshipMutation.None, new FriendResult(StatusCodes.Status409Conflict, "好友申请已发送，请等待对方同意", targetId));

            var all = await _social.GetFriendshipsAsync(userId);
            if (all.Count(f => f.Status == FriendshipRecord.Accepted) >= settings.MaxFriends)
                return (FriendshipMutation.None, new FriendResult(StatusCodes.Status409Conflict, $"好友数量已达上限（{settings.MaxFriends}个），请清理好友后再试", targetId));
            if (all.Count(f => f.Status == FriendshipRecord.Pending && f.RequesterId == userId) >= settings.MaxPendingRequests)
                return (FriendshipMutation.None, new FriendResult(StatusCodes.Status409Conflict, $"好友申请已达上限（{settings.MaxPendingRequests}个），请取消部分申请后再试", targetId));
            var theirsAll = await _social.GetFriendshipsAsync(targetId);
            if (theirsAll.Count(f => f.Status == FriendshipRecord.Pending && f.AddresseeId == targetId) >= settings.MaxPendingRequests)
                return (FriendshipMutation.None, new FriendResult(StatusCodes.Status409Conflict, $"对方等待处理的好友申请已满（{settings.MaxPendingRequests}个）", targetId));
            if (theirsAll.Count(f => f.Status == FriendshipRecord.Accepted) >= settings.MaxFriends)
                return (FriendshipMutation.None, new FriendResult(StatusCodes.Status409Conflict, $"对方好友数量已达上限（{settings.MaxFriends}个）", targetId));
            return (new FriendshipMutation(FriendshipMutationKind.CreatePending, userId, targetId),
                new FriendResult(StatusCodes.Status201Created, null, targetId));
        });

        if (result.Error != null)
            return await Fail(userId, result.StatusCode, result.Error, targetId);
        await SendRefreshAsync(targetId, userId);
        return result;
    }

    public async Task<FriendResult> RemoveAsync(int userId, int friendId)
    {
        if (friendId == AssistantId) return await Fail(userId, StatusCodes.Status403Forbidden, "不能删除服务器小助手");
        var removed = await _social.MutateFriendshipAsync(userId, friendId, existing => Task.FromResult(existing == null
            ? (FriendshipMutation.None, false)
            : (new FriendshipMutation(FriendshipMutationKind.Delete, userId, friendId), true)));
        if (!removed) return new FriendResult(StatusCodes.Status404NotFound, "好友关系不存在", friendId);
        await SendRefreshAsync(friendId, userId);
        return new FriendResult(StatusCodes.Status204NoContent, null, friendId);
    }

    private async Task<int> CountAcceptedAsync(int userId) =>
        (await _social.GetFriendshipsAsync(userId)).Count(f => f.Status == FriendshipRecord.Accepted);

    private async Task<FriendResult> Fail(int userId, int status, string message, int friendId = 0)
    {
        await _hub.SendNoticeAsync(userId, message);
        return new FriendResult(status, message, friendId);
    }

    private Task<bool> SendRefreshAsync(int targetId, int senderId) => _hub.SendAsync(targetId, new WebSocketMessage(
        Timestamp: NowText(), Context: null, Message: "refresh_friends", Channel: "refresh_friends", Sender: senderId.ToString(), Receiver: null));

    private async Task NotifyFriendsPresenceAsync(int userId)
    {
        foreach (var f in await _social.GetFriendshipsAsync(userId))
        {
            if (f.Status != FriendshipRecord.Accepted) continue;
            var other = f.Other(userId);
            if (_hub.IsOnline(other)) await SendRefreshAsync(other, userId);
        }
    }

    // ==================== WebSocket 事件 ====================

    /// <summary>/session 登录成功时调用：下次 WebSocket 连接要补发完整聊天记录。</summary>
    public void MarkClientLogin(int userId) => _freshLogins[userId] = DateTime.UtcNow;

    public async Task OnConnectedAsync(int userId)
    {
        var fresh = _freshLogins.TryRemove(userId, out var loginAt) && DateTime.UtcNow - loginAt < TimeSpan.FromMinutes(10);
        var serial = Interlocked.Increment(ref _connectionSerial);
        _pendingReplays[userId] = new PendingReplay(serial, DateTime.UtcNow, fresh);

        if (fresh && !string.IsNullOrWhiteSpace(Settings.WelcomeMessage))
            await SendAssistantAsync(userId, Settings.WelcomeMessage);
        await NotifyFriendsPresenceAsync(userId);
        // 客户端若一直不拉好友列表，6 秒后也补发一次。
        TriggerReplaySoon(userId, TimeSpan.FromSeconds(6));
    }

    public async Task OnDisconnectedAsync(int userId)
    {
        _pendingReplays.TryRemove(userId, out _);
        _rate.TryRemove(userId, out _);
        await _challenges.CleanupAsync(userId);
        await NotifyFriendsPresenceAsync(userId);
    }

    private void TriggerReplaySoon(int userId, TimeSpan delay)
    {
        if (!_pendingReplays.TryGetValue(userId, out var pending)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);
                // 只有第一个到点的任务能取走这次补发。
                if (!_pendingReplays.TryRemove(new KeyValuePair<int, PendingReplay>(userId, pending))) return;
                await ReplayAsync(userId, pending);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"补发聊天记录失败 [{userId}]：{ex.GetBaseException().Message}");
            }
        });
    }

    /// <summary>
    /// 补发聊天记录。
    /// 刚登录（客户端内存为空）：每个好友最近 N 条（可设置）+ 所有未送达的消息。
    /// 断线重连（客户端还留着记录）：只补发离线期间收到的未送达消息，避免重复。
    /// </summary>
    private async Task ReplayAsync(int userId, PendingReplay pending)
    {
        var settings = Settings;
        var since = pending.Fresh ? pending.ConnectedAt.AddDays(-settings.HistoryDays) : DateTime.UtcNow.AddDays(1);
        var rows = await _social.GetRecentMessagesAsync(userId, since, 3000);
        // 连接之后产生的消息已经实时推送过了。
        rows = rows.Where(m => m.CreatedAt < pending.ConnectedAt || !m.Delivered).ToList();
        if (rows.Count == 0) return;

        var friendIds = (await _social.GetFriendshipsAsync(userId))
            .Where(f => f.Status == FriendshipRecord.Accepted).Select(f => f.Other(userId)).ToHashSet();
        friendIds.Add(AssistantId);

        var maxIncoming = rows.Where(m => m.ReceiverId == userId).Select(m => m.Id).DefaultIfEmpty(0).Max();
        var toSend = new List<ChatMessageRecord>();
        foreach (var conversation in rows.GroupBy(m => m.SenderId == userId ? m.ReceiverId : m.SenderId))
        {
            if (!friendIds.Contains(conversation.Key)) continue;
            var ordered = conversation.OrderBy(m => m.Id).ToList();
            var recent = pending.Fresh ? ordered.TakeLast(settings.HistoryPerFriend) : Enumerable.Empty<ChatMessageRecord>();
            var undelivered = ordered.Where(m => m.ReceiverId == userId && !m.Delivered).TakeLast(200);
            toSend.AddRange(recent.Concat(undelivered).DistinctBy(m => m.Id));
        }
        if (!settings.ReplayOwnMessages) toSend.RemoveAll(m => m.SenderId == userId);

        var sent = 0;
        foreach (var m in toSend.OrderBy(m => m.Id))
        {
            if (!await _hub.SendAsync(userId, ToWire(m))) return; // 玩家又断开了：保留未送达状态，下次再补
            sent++;
        }
        if (maxIncoming > 0) await _social.MarkDeliveredAsync(userId, maxIncoming);
        if (sent > 0) Console.WriteLine($"已为玩家 {userId} 补发 {sent} 条聊天记录（{(pending.Fresh ? "登录" : "重连")}）");
    }

    // ==================== 私聊 ====================

    public async Task HandleChatAsync(int userId, WebSocketMessage message)
    {
        if (!int.TryParse(message.Receiver, out var receiverId) || receiverId <= 0) return;
        var body = (message.Message ?? "").Trim();
        if (body.Length == 0) return;

        if (receiverId == AssistantId)
        {
            await HandleAssistantCommandAsync(userId, body);
            return;
        }

        var settings = Settings;
        if (body.Length > settings.MaxMessageLength)
        {
            await _hub.SendNoticeAsync(userId, $"消息太长了，最多 {settings.MaxMessageLength} 个字");
            return;
        }
        if (!TryConsumeRate(userId))
        {
            await _hub.SendNoticeAsync(userId, "发送太频繁了，请稍后再试");
            return;
        }
        if ((await _social.GetFriendshipAsync(userId, receiverId))?.Status != FriendshipRecord.Accepted)
        {
            await _hub.SendNoticeAsync(userId, "对方不是你的好友，消息没有发出");
            return;
        }

        var context = message.Context ?? "";
        if (context.Length > 128) context = context[..128];
        var createdAt = DateTime.UtcNow;
        var id = await _social.AddMessageAsync(userId, receiverId, body, context, createdAt, delivered: false);
        var record = new ChatMessageRecord(id, userId, receiverId, body, context, createdAt, false);
        if (await _hub.SendAsync(receiverId, ToWire(record)))
            await _social.MarkMessageDeliveredAsync(id);
    }

    private bool TryConsumeRate(int userId)
    {
        var queue = _rate.GetOrAdd(userId, _ => new Queue<DateTime>());
        lock (queue)
        {
            var now = DateTime.UtcNow;
            while (queue.Count > 0 && now - queue.Peek() > RateLimitWindow) queue.Dequeue();
            if (queue.Count >= RateLimitCount) return false;
            queue.Enqueue(now);
            return true;
        }
    }

    private static WebSocketMessage ToWire(ChatMessageRecord m) => new(
        Timestamp: TimeText(m.CreatedAt),
        Context: m.Context,
        Message: m.Body,
        Channel: "chat",
        Sender: m.SenderId.ToString(),
        Receiver: m.ReceiverId.ToString());

    // ==================== 服务器小助手 ====================

    private const string HelpText = "小助手指令：\n/帮助 查看指令\n/在线人数 查看在线与匹配情况\n/改名 新名字 修改游戏名（不能带空格）";

    private async Task HandleAssistantCommandAsync(int userId, string text)
    {
        if (!TryConsumeRate(userId))
        {
            await SendAssistantAsync(userId, "发送太频繁了，请稍后再试");
            return;
        }
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].TrimStart('/', '／');
        string reply;
        switch (command)
        {
            case "帮助" or "help":
                reply = HelpText;
                break;
            case "在线人数" or "在线":
                reply = OnlineSummary();
                break;
            case "改名":
                if (parts.Length == 1) reply = "请在指令后面加上新名字，例如：/改名 小明";
                else if (parts.Length > 2) reply = "名字里不能有空格";
                else
                {
                    reply = await RenameAsync(userId, parts[1]);
                    if (reply.Length == 0) return; // 改名成功会断开连接，由断开消息提示
                }
                break;
            default:
                reply = "未知指令，发送 /帮助 查看所有指令";
                break;
        }
        await SendAssistantAsync(userId, reply);
    }

    private string OnlineSummary()
    {
        var queues = _matches.QueueCounts();
        var inGame = _matches.GetActiveRealMatches()
            .Where(m => !MatchManagerService.IsSoloMatch(m) && string.IsNullOrEmpty(m.WinnerSide))
            .Sum(m => (m.Left?.PlayerId > 0 ? 1 : 0) + (m.Right?.PlayerId > 0 ? 1 : 0));
        return $"在线人数：{_hub.OnlineCount}\n对战中：{inGame}\n" +
               $"匹配队列：排位 {queues.Ranked} · 休闲 {queues.Classic} · 非排位 {queues.Unranked} · 竞技场 {queues.Draft} · 乱斗 {queues.Brawl} · 好友对战 {queues.Friendly}";
    }

    /// <returns>失败时返回提示；成功时返回空字符串（连接已被断开，提示随断开消息发送）。</returns>
    private async Task<string> RenameAsync(int userId, string requestedName)
    {
        var name = requestedName.Trim();
        if (name.Length is < 1 or > 32 || name.Any(char.IsControl)) return "名字需要 1 到 32 个字符";
        if (string.Equals(name, User.AnonymousName, StringComparison.OrdinalIgnoreCase)) return "这个名字不能用";
        var result = await _users.WithUserLockAsync(userId, async user =>
        {
            if (!await _users.SavePlayerIdentityAsync(user, name)) return new RenameOutcome(false, "这个名字已经被用满了，换一个吧");
            _users.RecordIncremental();
            return new RenameOutcome(true, $"{user.Name}#{user.Tag:D4}");
        });
        if (result == null) return "账号不存在";
        if (!result.Ok) return result.Text;
        await _hub.DisconnectAsync(userId, $"游戏名已修改为 {result.Text}，重新连接游戏即可看到。");
        return "";
    }

    private sealed record RenameOutcome(bool Ok, string Text);

    private Task<bool> SendAssistantAsync(int userId, string text) => _hub.SendAsync(userId, new WebSocketMessage(
        Timestamp: NowText(), Context: Settings.AssistantName, Message: text, Channel: "chat",
        Sender: AssistantId.ToString(), Receiver: userId.ToString()));

    // ==================== 后台 ====================

    /// <summary>
    /// 以小助手的身份给玩家发消息并写入聊天记录（离线玩家上线后收到）。
    /// playerId 为 null 时发给当前所有在线玩家。返回实际写入的玩家数。
    /// </summary>
    public async Task<int> SendAdminMessageAsync(int? playerId, string text)
    {
        var targets = playerId is { } id ? new[] { id } : _hub.OnlineUserIds();
        var context = Settings.AssistantName;
        var count = 0;
        foreach (var target in targets)
        {
            var createdAt = DateTime.UtcNow;
            var messageId = await _social.AddMessageAsync(AssistantId, target, text, context, createdAt, delivered: false);
            if (await _hub.SendAsync(target, ToWire(new ChatMessageRecord(messageId, AssistantId, target, text, context, createdAt, false))))
                await _social.MarkMessageDeliveredAsync(messageId);
            count++;
        }
        return count;
    }

    public async Task<JsonArray> GetFriendsForAdminAsync(int userId)
    {
        var list = new JsonArray();
        foreach (var f in await _social.GetFriendshipsAsync(userId))
        {
            var otherId = f.Other(userId);
            var other = await _users.GetByIdAsync(otherId);
            list.Add(new JsonObject
            {
                ["playerId"] = otherId,
                ["name"] = other?.Name ?? $"#{otherId}",
                ["tag"] = other?.Tag ?? 0,
                ["status"] = f.Status == FriendshipRecord.Accepted ? "accepted" : f.RequesterId == userId ? "request_sent" : "request_received",
                ["online"] = _hub.IsOnline(otherId),
                ["since"] = f.UpdatedAt.ToString("o")
            });
        }
        return list;
    }

    // ==================== 工具 ====================

    private static string NowText() => TimeText(DateTime.UtcNow);

    /// <summary>与 comet 相同的时间格式：2026-11-03T17:08:51.123Z。</summary>
    private static string TimeText(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
