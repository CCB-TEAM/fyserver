using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;

namespace fyserver.Services;

/// <summary>
/// 房间中继：启动器内置的隧道，复用游戏 HTTP 端口，不需要 frp，也不用开新端口。
///
/// 三种连接都是普通的 HTTP/1.1 GET 请求，带 <c>Connection: Upgrade</c>、<c>Upgrade: fy-relay</c> 和
/// <c>X-Fy-Ticket</c> 头，服务端回 101 后变成原始字节流：
///   /launcher/relay/host           房主的控制连接。云端按行发送 "open &lt;连接号&gt;" 和 "ping"。
///   /launcher/relay/connect        成员的一条游戏连接。云端通知房主，等房主接上后两边对接。
///   /launcher/relay/accept/{conn}  房主为某个连接号接上的数据连接，另一头是本机嵌入版服务端。
/// 进房时房主和成员都按需建立这些连接，退房、被踢或房间关闭后云端会断开它们。
/// 中继走云端带宽，每个房间按后台设置限速。
/// </summary>
public sealed class LauncherTunnelService
{
    public const string UpgradeProtocol = "fy-relay";
    private const string ConfigPath = "./data/launcher-relay.json";
    /// <summary>成员连接等房主接上的最长时间。</summary>
    private static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(10);
    /// <summary>多久核对一次房主 / 成员是否还在房间里。</summary>
    private static readonly TimeSpan MembershipCheck = TimeSpan.FromSeconds(10);
    private const int MaxConnectionsPerRoom = 64;

    private readonly object _gate = new();
    private Settings? _settings;
    private readonly ConcurrentDictionary<string, HostLink> _hosts = new();
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly ConcurrentDictionary<string, RoomTraffic> _traffic = new();

    public sealed record Settings(bool Enabled, string RelayBandwidthLimit)
    {
        public long BytesPerSecond => ParseBandwidth(RelayBandwidthLimit);
    }

    private sealed class HostLink(int accountId, Stream control)
    {
        public readonly int AccountId = accountId;
        public readonly Stream Control = control;
        public readonly SemaphoreSlim WriteLock = new(1, 1);
        public readonly CancellationTokenSource Closed = new();
    }

    private sealed class Pending(string roomId, int hostId)
    {
        public readonly string RoomId = roomId;
        public readonly int HostId = hostId;
        public readonly TaskCompletionSource<Stream> HostStream = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>对接结束后放行房主那边的请求（升级后的流属于那个请求，请求结束流就关了）。</summary>
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RoomTraffic
    {
        public int Connections;
        public readonly TokenBucket Bucket = new();
    }

    // ==================== 设置 ====================

    public Settings Get()
    {
        lock (_gate)
        {
            if (_settings != null) return _settings;
            Settings settings;
            try
            {
                var root = File.Exists(ConfigPath) ? JsonNode.Parse(File.ReadAllText(ConfigPath)) as JsonObject : null;
                settings = new Settings(
                    root?["enabled"]?.GetValue<bool>() ?? true,
                    root?["relayBandwidthLimit"]?.GetValue<string>() ?? "512KB");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"读取 {ConfigPath} 失败：{ex.Message}");
                settings = new Settings(true, "512KB");
            }
            _settings = settings;
            return settings;
        }
    }

    public (bool Ok, string Message, Settings? Settings) Save(bool enabled, string? bandwidth)
    {
        bandwidth = string.IsNullOrWhiteSpace(bandwidth) ? "512KB" : bandwidth.Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(bandwidth, "^[0-9]{1,6}(KB|MB)$") || ParseBandwidth(bandwidth) <= 0)
            return (false, "中继限速格式应为 512KB 或 1MB", null);
        lock (_gate)
        {
            var settings = new Settings(enabled, bandwidth);
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var json = new JsonObject { ["enabled"] = settings.Enabled, ["relayBandwidthLimit"] = settings.RelayBandwidthLimit };
            var temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, json.ToJsonString(new() { WriteIndented = true }));
            File.Move(temp, ConfigPath, true);
            _settings = settings;
            return (true, "已保存", settings);
        }
    }

    private static long ParseBandwidth(string text)
    {
        var unit = text.EndsWith("MB") ? 1024L * 1024 : 1024L;
        return long.TryParse(text[..^2], out var n) ? n * unit : 0;
    }

    /// <summary>开房 / 进房响应里给启动器的隧道参数。中继关闭时返回 null（只能本机测试）。</summary>
    public JsonObject? ClientConfig(LauncherRoomService.Room room, string role)
    {
        if (!Get().Enabled) return null;
        return new JsonObject
        {
            ["role"] = role,
            ["relay"] = true,
            ["hostOnline"] = _hosts.ContainsKey(room.Id)
        };
    }

    public bool HostOnline(string roomId) => _hosts.ContainsKey(roomId);

    public JsonObject Stats() => new()
    {
        ["hosts"] = _hosts.Count,
        ["connections"] = _traffic.Values.Sum(t => Volatile.Read(ref t.Connections))
    };

    // ==================== 连接处理 ====================

    /// <summary>校验票据和房间成员身份。成功时返回票据，失败时返回给客户端的错误。</summary>
    public (RoomTicketService.Ticket? Ticket, IResult? Error) Authorize(HttpContext context, RoomTicketService tickets, LauncherRoomService rooms, string? role)
    {
        if (!Get().Enabled) return (null, Fail(503, "云端中继没有开启"));
        if (context.Features.Get<IHttpUpgradeFeature>() is not { IsUpgradableRequest: true })
            return (null, Fail(426, "需要 Upgrade: fy-relay 连接"));
        var ticket = tickets.Verify(context.Request.Headers["X-Fy-Ticket"].ToString());
        if (ticket == null) return (null, Fail(401, "缺少或无效的进房票据"));
        if (!rooms.IsMember(ticket.RoomId, ticket.AccountId, out var actual) || actual != ticket.Role)
            return (null, Fail(403, "你不在这个房间里或房间已关闭"));
        if (role != null && ticket.Role != role) return (null, Fail(403, role == "host" ? "只有房主可以这样连接" : "房主不需要经中继进自己的房间"));
        return (ticket, null);
    }

    /// <summary>房主的控制连接：一直保持到房主断开、换了新的控制连接，或者房主不在房间里了。</summary>
    public async Task RunHostAsync(HttpContext context, RoomTicketService.Ticket ticket, LauncherRoomService rooms)
    {
        var stream = await Upgrade(context);
        var link = new HostLink(ticket.AccountId, stream);
        if (_hosts.TryGetValue(ticket.RoomId, out var old)) old.Closed.Cancel();
        _hosts[ticket.RoomId] = link;
        Console.WriteLine($"中继：房间 {ticket.RoomId} 的房主 {ticket.Name} 已连上");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(link.Closed.Token, context.RequestAborted);
        try
        {
            // 房主那边断开时读到 0 字节
            var reader = DrainAsync(stream, stop.Token);
            while (!stop.IsCancellationRequested)
            {
                var tick = Task.Delay(MembershipCheck, stop.Token);
                if (await Task.WhenAny(reader, tick) == reader) break;
                if (!rooms.IsMember(ticket.RoomId, ticket.AccountId, out var role) || role != "host") break;
                if (!await SendAsync(link, "ping")) break;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _hosts.TryRemove(new KeyValuePair<string, HostLink>(ticket.RoomId, link));
            if (!_hosts.ContainsKey(ticket.RoomId)) _traffic.TryRemove(ticket.RoomId, out _);
            link.Closed.Cancel();
            await stream.DisposeAsync();
            Console.WriteLine($"中继：房间 {ticket.RoomId} 的房主控制连接已断开");
        }
    }

    /// <summary>成员的一条游戏连接。房主不在线或没有及时接上时返回错误，不升级。</summary>
    public async Task<IResult> RunConnectAsync(HttpContext context, RoomTicketService.Ticket ticket, LauncherRoomService rooms)
    {
        if (!_hosts.TryGetValue(ticket.RoomId, out var host)) return Fail(503, "房主的启动器没有连上云端中继");
        var traffic = _traffic.GetOrAdd(ticket.RoomId, _ => new RoomTraffic());
        if (Interlocked.Increment(ref traffic.Connections) > MaxConnectionsPerRoom)
        {
            Interlocked.Decrement(ref traffic.Connections);
            return Fail(429, "这个房间的中继连接太多了");
        }
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var pending = new Pending(ticket.RoomId, host.AccountId);
        _pending[id] = pending;
        try
        {
            if (!await SendAsync(host, "open " + id)) return Fail(503, "房主的启动器没有连上云端中继");
            Stream hostStream;
            try
            {
                hostStream = await pending.HostStream.Task.WaitAsync(AcceptTimeout, context.RequestAborted);
            }
            catch (TimeoutException)
            {
                return Fail(504, "房主的启动器没有及时接上连接");
            }
            _pending.TryRemove(id, out _);
            var memberStream = await Upgrade(context);
            traffic.Bucket.Rate = Get().BytesPerSecond;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, host.Closed.Token);
            try
            {
                var watchdog = WatchMembershipAsync(rooms, ticket, stop);
                await Task.WhenAny(
                    PumpAsync(memberStream, hostStream, traffic.Bucket, stop.Token),
                    PumpAsync(hostStream, memberStream, traffic.Bucket, stop.Token),
                    watchdog);
            }
            finally
            {
                stop.Cancel();
                await memberStream.DisposeAsync();
            }
            return Results.Empty;
        }
        finally
        {
            _pending.TryRemove(id, out _);
            pending.HostStream.TrySetCanceled();
            pending.Done.TrySetResult();
            Interlocked.Decrement(ref traffic.Connections);
        }
    }

    /// <summary>房主接上某个连接号。连接号不存在、已过期或不属于这个房主时返回 404。</summary>
    public async Task<IResult> RunAcceptAsync(HttpContext context, RoomTicketService.Ticket ticket, string connId)
    {
        if (!_pending.TryGetValue(connId, out var pending) || pending.RoomId != ticket.RoomId || pending.HostId != ticket.AccountId)
            return Fail(404, "连接已失效");
        if (pending.HostStream.Task.IsCompleted) return Fail(409, "连接已被接上");
        var stream = await Upgrade(context);
        try
        {
            if (!pending.HostStream.TrySetResult(stream)) return Results.Empty;
            await pending.Done.Task.WaitAsync(context.RequestAborted);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await stream.DisposeAsync();
        }
        return Results.Empty;
    }

    // ==================== 内部 ====================

    private static async Task<Stream> Upgrade(HttpContext context)
    {
        context.Response.Headers["Upgrade"] = UpgradeProtocol;
        var stream = await context.Features.Get<IHttpUpgradeFeature>()!.UpgradeAsync();
        return stream;
    }

    private static async Task<bool> SendAsync(HostLink link, string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\n");
        try
        {
            await link.WriteLock.WaitAsync(link.Closed.Token);
            try
            {
                await link.Control.WriteAsync(bytes, link.Closed.Token);
                await link.Control.FlushAsync(link.Closed.Token);
            }
            finally
            {
                link.WriteLock.Release();
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            link.Closed.Cancel();
            return false;
        }
    }

    /// <summary>读掉房主发来的任何内容（目前不需要），连接关闭时结束。</summary>
    private static async Task DrainAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[256];
        try
        {
            while (await stream.ReadAsync(buffer, token) > 0) { }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static async Task WatchMembershipAsync(LauncherRoomService rooms, RoomTicketService.Ticket ticket, CancellationTokenSource stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(MembershipCheck, stop.Token);
                if (!rooms.IsMember(ticket.RoomId, ticket.AccountId, out _)) return;
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task PumpAsync(Stream from, Stream to, TokenBucket bucket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var n = await from.ReadAsync(buffer, token);
                if (n == 0) return;
                await bucket.TakeAsync(n, token);
                await to.WriteAsync(buffer.AsMemory(0, n), token);
                await to.FlushAsync(token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static IResult Fail(int status, string message) => fyserver.Endpoints.ModIndexEndpoints.Fail(status, message);

    /// <summary>每个房间一个令牌桶，两个方向共用。最多攒 1 秒的额度。</summary>
    private sealed class TokenBucket
    {
        private readonly object _gate = new();
        private double _tokens;
        private long _last = Environment.TickCount64;
        public long Rate = 512 * 1024;

        public async ValueTask TakeAsync(int n, CancellationToken token)
        {
            double wait;
            lock (_gate)
            {
                var now = Environment.TickCount64;
                _tokens = Math.Min(Rate, _tokens + (now - _last) / 1000.0 * Rate);
                _last = now;
                _tokens -= n;
                wait = _tokens < 0 ? -_tokens / Rate : 0;
            }
            if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), token);
        }
    }
}
