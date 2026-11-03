using System.Text.Json.Nodes;
using fyserver.Models;

namespace fyserver.Services;

/// <summary>
/// 好友对战邀请（POST /players/notifications/{被邀请者id}，notification = "challenge"）。
/// str_1 为 request / accept / decline / cancel。邀请被接受后双方客户端各自以
/// extra_data "challenge:&lt;对方id&gt;" 进入 /lobbyplayers，由 MatchManagerService 配对。
/// 邀请只保存在内存里，超时或任一方下线即作废。
/// </summary>
public sealed class FriendChallengeService(WebSocketHubService hub, MatchManagerService matches, SocialService social)
{
    private sealed record Challenge(int InviterId, int InviteeId, DateTime ExpiresAt);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly object _gate = new();
    private readonly Dictionary<int, Challenge> _byInviter = new();

    public readonly record struct Result(int StatusCode, string? Error);

    public async Task<Result> HandleAsync(int userId, int inviteeId, JsonObject body)
    {
        if (Str(body["notification"]) != "challenge")
            return new(StatusCodes.Status400BadRequest, "未知通知类型");
        var action = Str(body["str_1"]) ?? "";
        var inviterId = Int(body["from_player_id"]) ?? 0;

        // 小助手不接受对战，直接替它拒绝。
        if (inviteeId == FriendService.AssistantId)
        {
            if (action == "request")
                await ForwardAsync(userId, body, FriendService.AssistantId, userId, userId, "decline");
            return new(StatusCodes.Status201Created, null);
        }

        switch (action)
        {
            case "request":
            {
                if (userId != inviterId) return new(StatusCodes.Status401Unauthorized, "无权操作");
                if (!await AreFriendsAsync(userId, inviteeId)) return new(StatusCodes.Status403Forbidden, "非好友关系");
                string? busy = null;
                if (!hub.IsOnline(inviteeId)) busy = "对方不在线";
                else if (matches.IsInRealMatch(userId)) busy = "你正在对战中";
                else if (matches.IsInRealMatch(inviteeId)) busy = "对方正在对战中";
                else
                {
                    lock (_gate)
                    {
                        Prune();
                        if (InChallenge(userId)) busy = "你已经在另一场好友对战邀请中";
                        else if (InChallenge(inviteeId)) busy = "对方正在处理其他好友对战邀请";
                        else _byInviter[userId] = new Challenge(userId, inviteeId, DateTime.UtcNow + Timeout);
                    }
                }
                if (busy != null)
                {
                    await hub.SendNoticeAsync(userId, busy);
                    return new(StatusCodes.Status409Conflict, busy);
                }
                await ForwardAsync(inviteeId, body, userId, inviteeId, inviterId, action);
                return new(StatusCodes.Status201Created, null);
            }
            case "accept" or "decline":
            {
                if (userId != inviteeId) return new(StatusCodes.Status401Unauthorized, "无权操作");
                if (!TryTake(inviterId, inviteeId))
                {
                    if (action == "accept") await hub.SendNoticeAsync(userId, "对战邀请不存在或已过期");
                    return new(StatusCodes.Status404NotFound, "对战邀请不存在或已过期");
                }
                await ForwardAsync(inviterId, body, userId, inviterId, inviterId, action);
                return new(StatusCodes.Status201Created, null);
            }
            case "cancel":
            {
                if (userId != inviterId) return new(StatusCodes.Status401Unauthorized, "无权操作");
                if (!TryTake(inviterId, inviteeId)) return new(StatusCodes.Status404NotFound, "对战邀请不存在或已过期");
                await ForwardAsync(inviteeId, body, userId, inviteeId, inviterId, action);
                return new(StatusCodes.Status201Created, null);
            }
            default:
                return new(StatusCodes.Status400BadRequest, $"未知操作: {action}");
        }
    }

    /// <summary>玩家下线时作废与他有关的邀请，并通知另一方邀请已取消。</summary>
    public async Task CleanupAsync(int userId)
    {
        List<Challenge> removed;
        lock (_gate)
        {
            removed = _byInviter.Values.Where(c => c.InviterId == userId || c.InviteeId == userId).ToList();
            foreach (var c in removed) _byInviter.Remove(c.InviterId);
        }
        foreach (var c in removed)
        {
            var other = c.InviterId == userId ? c.InviteeId : c.InviterId;
            var action = c.InviterId == userId ? "cancel" : "decline";
            await ForwardAsync(other, new JsonObject(), userId, other, c.InviterId, action);
        }
    }

    public bool IsInChallenge(int userId)
    {
        lock (_gate)
        {
            Prune();
            return InChallenge(userId);
        }
    }

    private bool TryTake(int inviterId, int inviteeId)
    {
        lock (_gate)
        {
            Prune();
            if (!_byInviter.TryGetValue(inviterId, out var c) || c.InviteeId != inviteeId) return false;
            _byInviter.Remove(inviterId);
            return true;
        }
    }

    private bool InChallenge(int userId) => _byInviter.Values.Any(c => c.InviterId == userId || c.InviteeId == userId);

    private void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _byInviter.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList())
            _byInviter.Remove(key);
    }

    private async Task<bool> AreFriendsAsync(int a, int b) =>
        (await social.GetFriendshipAsync(a, b))?.Status == FriendshipRecord.Accepted;

    private Task<bool> ForwardAsync(int targetId, JsonObject body, int sender, int receiver, int inviterId, string action)
    {
        var context = new JsonObject
        {
            ["id"] = Int(body["id"]) ?? 1,
            ["from_player_id"] = inviterId,
            ["notification"] = "challenge",
            ["int_1"] = Int(body["int_1"]) ?? 0,
            ["int_2"] = Int(body["int_2"]) ?? 0,
            ["str_1"] = action,
            ["str_2"] = Str(body["str_2"]) ?? ""
        };
        return hub.SendAsync(targetId, new WebSocketMessage(
            Timestamp: DateTimeOffset.UtcNow.ToString("o"),
            Context: context.ToJsonString(),
            Message: "challenge",
            Channel: "notification",
            Sender: sender.ToString(),
            Receiver: receiver.ToString()));
    }

    internal static string? Str(JsonNode? node) => node is JsonValue v
        ? v.TryGetValue<string>(out var s) ? s : v.ToJsonString().Trim('"')
        : null;

    internal static int? Int(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue) return (int)d;
        return v.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed) ? parsed : null;
    }
}
