using System.Net.WebSockets;
using fyserver.Models;
using fyserver.Services;

namespace fyserver.Endpoints;

/// <summary>
/// WebSocket 端点：与 HTTP 共用同一端口/同一 host 管线，
/// 任意路径上的 WebSocket 升级请求都在这里完成认证、注册与消息循环。
/// </summary>
public static class WebSocketEndpoint
{
    private const string AuthorizationScheme = "JWT ";

    /// <summary>解析 Authorization 头中的用户名；兼容裸 token 与 "JWT " 前缀两种客户端行为。</summary>
    private static string? DecodeUserName(string authorization, CodecService codec)
    {
        var candidates = authorization.StartsWith(AuthorizationScheme, StringComparison.Ordinal)
            ? new[] { authorization[AuthorizationScheme.Length..], authorization }
            : new[] { authorization };

        foreach (var candidate in candidates)
        {
            if (candidate.Length == 0)
                continue;
            try
            {
                return codec.Decode(candidate, out _);
            }
            catch
            {
                // 换下一种格式重试
            }
        }
        return null;
    }

    /// <summary>注册 WebSocket 中间件；非 WebSocket 请求原样交给后续管线。</summary>
    public static void MapWebSocketEndpoint(this WebApplication app)
    {
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                await next();
                return;
            }

            var users = context.RequestServices.GetRequiredService<UserStoreService>();
            var codec = context.RequestServices.GetRequiredService<CodecService>();
            var hub = context.RequestServices.GetRequiredService<WebSocketHubService>();

            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var auth = context.Request.Headers["Authorization"].FirstOrDefault();
            var userId = 0;
            User? user = null;
            if (!string.IsNullOrEmpty(auth))
            {
                try
                {
                    var userName = DecodeUserName(auth, codec);
                    user = userName == null ? null : await users.GetByUserNameAsync(userName);
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

            // 未认证连接不进入共享连接表，避免 -1/-2 槽位互相覆盖并使用 WS 协议。
            if (userId <= 0 || user == null)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Authentication required", CancellationToken.None);
                return;
            }

            await hub.RegisterClientAsync(userId, webSocket);
            // 注册后重新读取状态，避免并发封禁发生在首次查询与注册之间。
            user = await users.GetByIdAsync(userId);
            if (user?.IsBanActive(DateTime.UtcNow) == true)
            {
                await hub.DisconnectAsync(userId, "该账户已被封禁");
            }
            else
            {
                await HandleWebSocketAsync(webSocket, userId, hub);
            }
            hub.UnregisterClient(userId, webSocket);
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
