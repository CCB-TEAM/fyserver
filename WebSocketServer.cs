using System.Net.WebSockets;
using fyserver.Services;

namespace fyserver;

/// <summary>
/// WebSocket 服务器（独立监听独立端口，与 HTTP host 共享 DI 单例）。
/// </summary>
public class WebSocketServer
{
    public static void Configure(WebApplication wsApp, UserStoreService users, CodecService codec, WebSocketHubService hub)
    {
        wsApp.UseWebSockets();
        wsApp.Use(async (context, next) =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                var auth = context.Request.Headers["Authorization"].FirstOrDefault();
                var userId = 0;
                if (!string.IsNullOrEmpty(auth))
                {
                    try
                    {
                        var user = await users.GetByUserNameAsync(codec.Decode(auth, out _));
                        if (user == null)
                            userId = -1; // 未认证用户
                        else
                            userId = user.Id;
                    }
                    catch
                    {
                        userId = -2; // 认证失败（例如解密错误）
                    }
                }
                else
                {
                    userId = -1;
                }

                hub.RegisterClient(userId, webSocket);
                await HandleWebSocketAsync(webSocket, userId, hub);
                hub.UnregisterClient(userId);
            }
            else
            {
                context.Response.StatusCode = 400;
                await next();
            }
        });
    }

    private static async Task HandleWebSocketAsync(WebSocket webSocket, int clientId, WebSocketHubService hub)
    {
        var buffer = new byte[4096];
        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        CancellationToken.None
                    );
                }
                catch (WebSocketException ex)
                {
                    Console.WriteLine($"[{clientId}] WebSocket closed during receive: {ex.Message}");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                var message = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                await hub.HandleMessageAsync(clientId, webSocket, message);
            }
        }
        finally
        {
            // 统一关闭（不要在多个地方 Close）
            if (webSocket.State == WebSocketState.Open ||
                webSocket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Server closing",
                        CancellationToken.None
                    );
                }
                catch { }
            }
        }
    }
}
