using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using fyserver.Models;
using fyserver.Serialization;

namespace fyserver.Services;

/// <summary>
/// WebSocket 连接表与会话消息路由（替代原 config.appconfig.UsersById 与 ws.cs 中的路由逻辑）。
/// 供 WebSocket 服务端（发送）与 HTTP 端点（推送）共享。
/// </summary>
public class WebSocketHubService
{
    private readonly ConcurrentDictionary<int, WebSocket> _usersById = new();

    public void RegisterClient(int clientId, WebSocket webSocket)
    {
        Console.WriteLine($"客户端已连接: {clientId}");
        _usersById[clientId] = webSocket;
    }

    public void UnregisterClient(int clientId)
    {
        Console.WriteLine($"客户端已断开: {clientId}");
        _usersById.TryRemove(clientId, out _);
    }

    public bool TryGetClient(int userId, out WebSocket webSocket)
    {
        return _usersById.TryGetValue(userId, out webSocket!);
    }

    public void ClearMatchPairs() { /* 占位，避免误用；对局由 MatchManagerService 管理 */ }

    /// <summary>处理客户端上行消息（ping/touchcard/emoji/notification 通道）。</summary>
    public async Task HandleMessageAsync(int clientId, WebSocket sender, string message)
    {
        try
        {
            Console.WriteLine($"收到消息: {message}");
            var msg = JsonSerializer.Deserialize(message, FyJsonContext.Default.WebSocketMessage);
            if (msg == null) return;

            switch (msg.Channel)
            {
                case "ping":
                    await SendObjAsync(sender, new WebSocketMessage
                    (
                        Message: "pong",
                        Channel: "ping",
                        Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                        Sender: clientId.ToString(),
                        Receiver: ""
                    ));
                    break;

                case "touchcard":
                    if (int.TryParse(msg.Receiver, out var touchCardTarget) &&
                        TryGetClient(touchCardTarget, out var touchCardClient))
                    {
                        await SendObjAsync(touchCardClient, new WebSocketMessage
                        (
                            Message: msg.Message,
                            Channel: "touchcard",
                            Context: msg.Context,
                            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                            Sender: clientId.ToString(),
                            Receiver: msg.Receiver
                        ));
                    }
                    break;

                case "emoji":
                    if (int.TryParse(msg.Receiver, out var emojiTarget) &&
                        TryGetClient(emojiTarget, out var emojiClient))
                    {
                        await SendObjAsync(emojiClient, new WebSocketMessage
                        (
                            Message: msg.Message,
                            Channel: "emoji",
                            Context: msg.Context,
                            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                            Sender: clientId.ToString(),
                            Receiver: msg.Receiver
                        ));
                    }
                    break;

                case "notification":
                    if (int.TryParse(msg.Receiver, out var notificationTarget) &&
                        TryGetClient(notificationTarget, out var notificationClient))
                    {
                        var response = new WebSocketMessage
                        (
                            Message: msg.Message,
                            Channel: "notification",
                            Context: msg.Context,
                            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                            Sender: clientId.ToString(),
                            Receiver: msg.Receiver
                        );
                        if (msg.Message == "im_here")
                            response = response with { Context = "" };
                        await SendObjAsync(notificationClient, response);
                    }
                    break;
            }
        }
        catch (JsonException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(message);
            Console.WriteLine(message.GetType().Name);
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"JSON 解析错误 [{clientId}]: {ex.Message}");
        }
    }

    public static async Task SendAsync(WebSocket ws, string message)
    {
        if (ws.State == WebSocketState.Open)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(message);
            await ws.SendAsync(
                new ArraySegment<byte>(buffer),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None
            );
        }
    }

    public static async Task SendObjAsync(WebSocket c, WebSocketMessage a)
    {
        await SendAsync(c, JsonSerializer.Serialize(a, FyJsonContext.Default.WebSocketMessage));
        Console.WriteLine("发送:" + a?.ToString());
    }
}
