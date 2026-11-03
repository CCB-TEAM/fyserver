using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using fyserver.Models;
using fyserver.Serialization;

namespace fyserver.Services;

/// <summary>好友 / 聊天 / 小助手对 WebSocket 事件的处理（由 FriendService 实现，启动时挂到 hub 上）。</summary>
public interface IWebSocketSocialHandler
{
    Task OnConnectedAsync(int userId);
    Task OnDisconnectedAsync(int userId);
    Task HandleChatAsync(int userId, WebSocketMessage message);
}

/// <summary>
/// WebSocket 连接表与会话消息路由。每名用户只保留一条有效连接；所有发送/关闭按连接串行化。
/// </summary>
public class WebSocketHubService
{
    private sealed class ClientConnection(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<int, ClientConnection> _usersById = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _userGates = new();

    /// <summary>好友系统处理器；为 null 时 chat 通道被忽略。</summary>
    public IWebSocketSocialHandler? Social { get; set; }

    private SemaphoreSlim GetUserGate(int userId) => _userGates.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));

    /// <summary>注册连接；同一用户的新连接会替换并主动关闭旧连接。</summary>
    public async Task RegisterClientAsync(int clientId, WebSocket webSocket)
    {
        var userGate = GetUserGate(clientId);
        await userGate.WaitAsync();
        try
        {
            var connection = new ClientConnection(webSocket);
            if (_usersById.TryGetValue(clientId, out var oldConnection))
            {
                _usersById[clientId] = connection;
                await DisconnectConnectionAsync(oldConnection, "您的账户已在其他位置连接", WebSocketCloseStatus.PolicyViolation);
            }
            else
            {
                _usersById[clientId] = connection;
            }
            Console.WriteLine($"客户端已连接: {clientId}");
        }
        finally
        {
            userGate.Release();
        }
    }

    /// <summary>仅移除当前连接，避免旧连接 finally 误删同一玩家的新连接。</summary>
    /// <returns>是否真的移除了该玩家的当前连接（被新连接顶掉的旧连接返回 false）。</returns>
    public bool UnregisterClient(int clientId, WebSocket webSocket)
    {
        var removed = false;
        if (_usersById.TryGetValue(clientId, out var connection) && ReferenceEquals(connection.Socket, webSocket))
        {
            removed = ((ICollection<KeyValuePair<int, ClientConnection>>)_usersById)
                .Remove(new KeyValuePair<int, ClientConnection>(clientId, connection));
        }
        Console.WriteLine($"客户端已断开: {clientId}");
        return removed;
    }

    public bool IsOnline(int userId) => _usersById.ContainsKey(userId);

    /// <summary>向在线玩家推送一条消息；玩家不在线或发送失败返回 false。</summary>
    public Task<bool> SendAsync(int userId, WebSocketMessage message) => SendToUserAsync(userId, message);

    /// <summary>
    /// 用 disconnect 通道给玩家弹一条提示但不关闭连接（参考服务端好友系统的报错方式）。
    /// </summary>
    public Task<bool> SendNoticeAsync(int userId, string text) => SendToUserAsync(userId, new WebSocketMessage(
        Message: text, Channel: "disconnect", Context: null, Timestamp: "", Sender: "Server", Receiver: null));

    public bool TryGetClient(int userId, out WebSocket webSocket)
    {
        if (_usersById.TryGetValue(userId, out var connection))
        {
            webSocket = connection.Socket;
            return true;
        }
        webSocket = null!;
        return false;
    }

    /// <summary>当前有效的 WebSocket 连接数（同一用户只计一条）。后台概览使用。</summary>
    public int OnlineCount => _usersById.Count;

    /// <summary>当前有效连接对应的用户 ID 快照。后台页面标记在线用户使用。</summary>
    public int[] OnlineUserIds() => _usersById.Keys.ToArray();

    /// <summary>
    /// 向玩家发送参考服务端兼容的 disconnect 消息并关闭其 WebSocket。
    /// HTTP 请求本身不会被强制断开。
    /// </summary>
    public async Task<bool> DisconnectAsync(
        int userId,
        string reason,
        WebSocketCloseStatus closeStatus = WebSocketCloseStatus.PolicyViolation)
    {
        var userGate = GetUserGate(userId);
        await userGate.WaitAsync();
        try
        {
            if (!_usersById.TryRemove(userId, out var connection))
                return false;

            await DisconnectConnectionAsync(connection, reason, closeStatus);
            Console.WriteLine($"已断开玩家 {userId} 的 WebSocket，原因：{reason}");
            return true;
        }
        finally
        {
            userGate.Release();
        }
    }

    private static async Task DisconnectConnectionAsync(
        ClientConnection connection,
        string reason,
        WebSocketCloseStatus closeStatus)
    {
        await connection.Gate.WaitAsync();
        try
        {
            if (connection.Socket.State == WebSocketState.Open)
            {
                var notification = new WebSocketMessage(
                    Message: reason,
                    Channel: "disconnect",
                    Context: null,
                    Timestamp: "",
                    Sender: "Server",
                    Receiver: null);
                await SendUnsafeAsync(connection.Socket, JsonSerializer.Serialize(notification, FyJsonContext.Default.WebSocketMessage));

                // CloseStatusDescription 最多 123 UTF-8 bytes；固定短原因避免外部 reason 导致 500。
                await connection.Socket.CloseOutputAsync(closeStatus, "Disconnected", CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ArgumentException or InvalidOperationException or ObjectDisposedException)
        {
            Console.WriteLine($"关闭 WebSocket 时发生异常: {ex.Message}");
            try { connection.Socket.Abort(); } catch { }
        }
    }

    /// <summary>处理客户端上行消息（ping/touchcard/emoji/notification 通道）。</summary>
    public async Task HandleMessageAsync(int clientId, WebSocket sender, string message)
    {
        try
        {
            Console.WriteLine($"收到消息: {message}");
            var msg = ParseClientMessage(message);
            if (msg == null) return;

            switch (msg.Channel)
            {
                case "ping":
                    await SendObjAsync(sender, new WebSocketMessage(
                        Message: "pong",
                        Channel: "ping",
                        Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                        Sender: clientId.ToString(),
                        Receiver: ""));
                    break;

                case "touchcard":
                    if (int.TryParse(msg.Receiver, out var touchCardTarget))
                    {
                        await SendToUserAsync(touchCardTarget, new WebSocketMessage(
                            Message: msg.Message,
                            Channel: "touchcard",
                            Context: msg.Context,
                            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                            Sender: clientId.ToString(),
                            Receiver: msg.Receiver));
                    }
                    break;

                case "emoji":
                    if (int.TryParse(msg.Receiver, out var emojiTarget))
                    {
                        await SendToUserAsync(emojiTarget, new WebSocketMessage(
                            Message: msg.Message,
                            Channel: "emoji",
                            Context: msg.Context,
                            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                            Sender: clientId.ToString(),
                            Receiver: msg.Receiver));
                    }
                    break;

                case "chat":
                    if (Social != null) await Social.HandleChatAsync(clientId, msg);
                    break;

                case "notification":
                    if (int.TryParse(msg.Receiver, out var notificationTarget))
                    {
                        var response = new WebSocketMessage(
                            Message: msg.Message,
                            Channel: "notification",
                            Context: msg.Message == "im_here" ? "" : msg.Context,
                            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                            Sender: clientId.ToString(),
                            Receiver: msg.Receiver);
                        await SendToUserAsync(notificationTarget, response);
                    }
                    break;
            }
        }
        catch (JsonException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(message);
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"JSON 解析错误 [{clientId}]: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 好友/聊天处理失败（例如数据库暂时不可用）不应断开玩家的 WebSocket。
            Console.WriteLine($"处理 WebSocket 消息失败 [{clientId}]: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// 宽容解析客户端上行消息：sender / receiver 等字段无论是 JSON 字符串还是数字都接受，
    /// 避免客户端发数字 id 时整个消息因类型不符被丢弃。
    /// </summary>
    private static WebSocketMessage? ParseClientMessage(string message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        string? Str(string name) => root.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
                _ => null
            }
            : null;
        JsonElement? matchId = root.TryGetProperty("match_id", out var m) ? m.Clone() : null;
        return new WebSocketMessage(
            Timestamp: Str("timestamp") ?? "",
            Context: Str("context") ?? "",
            Message: Str("message") ?? "",
            Channel: Str("channel") ?? "",
            Sender: Str("sender") ?? "",
            Receiver: Str("receiver") ?? "",
            MatchId: matchId);
    }

    private async Task<bool> SendToUserAsync(int userId, WebSocketMessage message)
    {
        if (!_usersById.TryGetValue(userId, out var connection))
            return false;

        await connection.Gate.WaitAsync();
        try
        {
            if (connection.Socket.State != WebSocketState.Open)
                return false;
            await SendUnsafeAsync(connection.Socket, JsonSerializer.Serialize(message, FyJsonContext.Default.WebSocketMessage));
            Console.WriteLine("发送:" + message);
            return true;
        }
        catch (WebSocketException)
        {
            connection.Socket.Abort();
            return false;
        }
        finally
        {
            connection.Gate.Release();
        }
    }

    public async Task SendObjAsync(WebSocket socket, WebSocketMessage message)
    {
        var connection = _usersById.Values.FirstOrDefault(c => ReferenceEquals(c.Socket, socket));
        if (connection == null)
            return;

        await connection.Gate.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await SendUnsafeAsync(socket, JsonSerializer.Serialize(message, FyJsonContext.Default.WebSocketMessage));
                Console.WriteLine("发送:" + message);
            }
        }
        finally
        {
            connection.Gate.Release();
        }
    }

    private static Task SendUnsafeAsync(WebSocket socket, string message)
    {
        byte[] buffer = Encoding.UTF8.GetBytes(message);
        return socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
    }
}
