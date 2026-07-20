//using Newtonsoft.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
namespace fyserver
{
    public class ws
    {
        private WebApplication? app;


        public async Task StartWsServerAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            app = builder.Build();

            app.UseWebSockets();
            app.Use(async (context, next) =>
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    var auth = context.Request.Headers["Authorization"].FirstOrDefault();
                        var userId = 0;
                    if (auth != null&&!(auth.Equals("")))
                    {
                        try
                        {
                            var user = (await GlobalState.users.GetByUserNameAsync(http.Decode(auth, out var actionId)));
                            if (user == null)
                            {
                                // webSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unauthorized", CancellationToken.None);
                                userId = -1; // 使用 -1 表示未认证用户
                            }
                            else
                                userId = user.Id;
                        }
                        catch {
                            userId = -2; // 使用 -2 表示认证失败（例如解密错误）
                        }
                    }
                    else
                        userId = -1;
                        // 【原事件触发位置】OnClientConnected?.Invoke(null, new ConnectionEventArgs(clientId, webSocket));
                        ClientConnected(userId, webSocket);
                    await HandleWebSocketAsync(webSocket, userId);
                    ClientDisconnected(userId);
                }
                else
                {
                    context.Response.StatusCode = 400;
                    await next();
                }
            });
            await app.RunAsync(config.appconfig.getAddressWs());
        }

        private static async Task HandleWebSocketAsync(WebSocket webSocket, int clientId)
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
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    MessageReceived(clientId, webSocket, message);
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
        private static void ClientConnected(int clientId, WebSocket webSocket)
        {
            Console.WriteLine($"客户端已连接: {clientId}");
            config.appconfig.UsersById[clientId] = webSocket;
        }
        private static void ClientDisconnected(int clientId)
        {
            Console.WriteLine($"客户端已断开: {clientId}");
            if (config.appconfig.UsersById[clientId] != null)
            {
                config.appconfig.UsersById.TryRemove(clientId, out _);
            }
        }

        private static void MessageReceived(int clientId, WebSocket webSocket, string message)
        {
            try
            {
                Console.WriteLine($"收到消息: {message}");
                var msg = JsonSerializer.Deserialize<WebSocketMessage>(message,GameConstants.JsonOptions);
                if (msg == null) return;
                var r = 0;
                switch (msg.Channel)
                {
                    case "ping":
                        _ = SendObjAsync(webSocket, new WebSocketMessage
                        (
                            Message : "pong",
                            Channel : "ping",
                            Timestamp : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                            Sender :clientId.ToString(),
                            Receiver : ""
                        ));
                        break;
                    case "touchcard":
                        int.TryParse(msg.Receiver ,out r);
                        if (config.appconfig.UsersById.TryGetValue(r, out var touchCardClient))
                        {
                            _ = SendObjAsync(touchCardClient,new WebSocketMessage
                            (
                                Message : msg.Message,
                                Channel: "touchcard",
                                Context : msg.Context,
                                Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                                Sender: clientId.ToString(),
                                Receiver : msg.Receiver
                            ));
                        }
                        break;
                    case "emoji":
                        int.TryParse(msg.Receiver, out  r);
                        if (config.appconfig.UsersById.TryGetValue(r, out var emojiClient))
                        {
                            _ = _ = SendObjAsync(emojiClient,new WebSocketMessage
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
                        int.TryParse(msg.Receiver, out r);
                        if (config.appconfig.UsersById.TryGetValue(r, out var notificationClient))
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
                                response = response with { Context ="" };
                            _ = SendObjAsync(notificationClient,response);
                        }
                        break;
                }
            }
            catch (JsonException ex)
            {
                Console.ForegroundColor=ConsoleColor.Red;
                Console.WriteLine(message);
                Console.WriteLine(message.GetType().Name);
                Console.ForegroundColor=ConsoleColor.White;
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
        /// <summary>
        /// 接收字符串消息
        /// </summary>
        public static async Task SendObjAsync<T>(WebSocket c, T a)
        {
            await SendAsync(c, JsonSerializer.Serialize(a, GameConstants.JsonOptions));
            Console.WriteLine("发送:"+a.ToString());
        }
    }
}