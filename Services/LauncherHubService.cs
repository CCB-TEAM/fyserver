using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace fyserver.Services;

/// <summary>
/// 启动器与云端之间的控制 WebSocket（/launcher/ws）。每个启动器账号保留一条连接，新连接顶掉旧连接。
/// 消息都是 JSON 对象，以 "type" 区分；推送内容见 docs/launcher-cloud-api.md。
/// </summary>
public sealed class LauncherHubService
{
    private sealed class Connection(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<int, Connection> _connections = new();
    private const int MaxMessageBytes = 64 * 1024;

    /// <summary>上线、下线、收到消息时的处理（由 LauncherSocialService 挂上）。</summary>
    public Func<int, Task>? Connected { get; set; }
    public Func<int, Task>? Disconnected { get; set; }
    public Func<int, JsonObject, Task>? MessageReceived { get; set; }

    public bool IsOnline(int accountId) => _connections.ContainsKey(accountId);
    public int OnlineCount => _connections.Count;
    public int[] OnlineIds() => _connections.Keys.ToArray();

    public async Task<bool> SendAsync(int accountId, JsonObject message)
    {
        if (!_connections.TryGetValue(accountId, out var connection)) return false;
        return await SendAsync(connection, message);
    }

    private static async Task<bool> SendAsync(Connection connection, JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await connection.Gate.WaitAsync();
        try
        {
            if (connection.Socket.State != WebSocketState.Open) return false;
            await connection.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
        finally { connection.Gate.Release(); }
    }

    /// <summary>接管一条已接受的 WebSocket，直到它关闭。</summary>
    public async Task RunAsync(int accountId, WebSocket socket, CancellationToken cancellationToken)
    {
        var connection = new Connection(socket);
        if (_connections.TryGetValue(accountId, out var old))
        {
            _connections[accountId] = connection;
            await SendAsync(old, new JsonObject { ["type"] = "replaced", ["message"] = "账号已在其他地方登录启动器" });
            try { await old.Socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Replaced", CancellationToken.None); } catch { }
        }
        else _connections[accountId] = connection;

        await Safe(() => Connected?.Invoke(accountId));
        var buffer = new byte[8192];
        using var assembled = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try { result = await socket.ReceiveAsync(buffer, cancellationToken); }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { break; }
                if (result.MessageType == WebSocketMessageType.Close) break;
                assembled.Write(buffer, 0, result.Count);
                if (assembled.Length > MaxMessageBytes) break;
                if (!result.EndOfMessage) continue;

                JsonObject? message = null;
                try { message = JsonNode.Parse(Encoding.UTF8.GetString(assembled.GetBuffer(), 0, (int)assembled.Length)) as JsonObject; }
                catch (JsonException) { }
                assembled.SetLength(0);
                if (message == null) continue;

                if (message["type"]?.GetValue<string>() == "ping")
                {
                    await SendAsync(connection, new JsonObject { ["type"] = "pong", ["at"] = DateTimeOffset.UtcNow.ToString("O") });
                    continue;
                }
                await Safe(() => MessageReceived?.Invoke(accountId, message));
            }
        }
        finally
        {
            var removed = ((ICollection<KeyValuePair<int, Connection>>)_connections)
                .Remove(new KeyValuePair<int, Connection>(accountId, connection));
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", CancellationToken.None); } catch { }
            }
            if (removed) await Safe(() => Disconnected?.Invoke(accountId));
        }
    }

    private static async Task Safe(Func<Task?> action)
    {
        try
        {
            var task = action();
            if (task != null) await task;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"启动器控制连接处理失败：{ex.GetBaseException().Message}");
        }
    }
}
