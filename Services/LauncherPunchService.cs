using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace fyserver.Services;

/// <summary>
/// 打洞协调：在游戏端口号的 UDP 上记录房主和成员的公网 UDP 地址，再通过中继控制连接让双方互相发包。
///
/// 启动器用同一个 UDP 套接字做三件事：给这里发登记包、给对方发打洞包、跑 QUIC。
///   登记包（UTF-8 文本）：<c>FYP1 REG &lt;票据&gt; [证书指纹]</c>，房主每 15 秒发一次，成员打洞前发几次。
///   云端只记下看到的源地址，不回包（回包会被 QUIC 当成杂包丢掉，没有用）。
/// 成员登记后调用 <c>POST /launcher/relay/punch</c>：云端把成员地址经控制连接发给房主（<c>punch &lt;ip:port&gt;</c>），
/// 并把房主地址和证书指纹返回给成员。之后两边同时向对方发包，成员发起 QUIC 连接；不通就继续走中继。
/// </summary>
public sealed class LauncherPunchService : IDisposable
{
    /// <summary>登记多久内有效。房主每 15 秒刷新一次。</summary>
    private static readonly TimeSpan HostFresh = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MemberFresh = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PunchCooldown = TimeSpan.FromSeconds(5);

    private readonly RoomTicketService _tickets;
    private readonly LauncherRoomService _rooms;
    private readonly ConcurrentDictionary<(string Room, int Account), Seen> _seen = new();
    private readonly ConcurrentDictionary<(string Room, int Account), DateTime> _lastPunch = new();
    private UdpClient? _udp;
    private Timer? _sweeper;
    private readonly CancellationTokenSource _stop = new();

    private sealed record Seen(IPEndPoint EndPoint, string? Fingerprint, DateTime At);

    public int? Port { get; private set; }

    public LauncherPunchService(RoomTicketService tickets, LauncherRoomService rooms)
    {
        _tickets = tickets;
        _rooms = rooms;
    }

    public void Start(int port)
    {
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            Port = port;
            _ = Task.Run(ReceiveLoop);
            _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            Console.WriteLine($"打洞协调：监听 UDP {port}");
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"打洞协调：无法监听 UDP {port}（{ex.Message}），联机只走中继");
        }
    }

    private async Task ReceiveLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await _udp!.ReceiveAsync(_stop.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            // Windows 上对方端口不可达会让下一次接收抛异常，忽略即可
            catch (SocketException) { continue; }
            try
            {
                Handle(packet.Buffer, packet.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"打洞协调：处理 UDP 包出错：{ex.Message}");
            }
        }
    }

    private void Handle(byte[] data, IPEndPoint from)
    {
        if (data.Length is < 10 or > 4096) return;
        var text = Encoding.UTF8.GetString(data);
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts[0] != "FYP1" || parts[1] != "REG") return;
        var ticket = _tickets.Verify(parts[2]);
        if (ticket == null || !_rooms.IsMember(ticket.RoomId, ticket.AccountId, out var role) || role != ticket.Role) return;
        string? fingerprint = null;
        if (ticket.Role == "host")
        {
            if (parts.Length < 4 || parts[3].Length != 64 || !parts[3].All(Uri.IsHexDigit)) return;
            fingerprint = parts[3].ToLowerInvariant();
        }
        var endpoint = from.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(from.Address.MapToIPv4(), from.Port) : from;
        _seen[(ticket.RoomId, ticket.AccountId)] = new Seen(endpoint, fingerprint, DateTime.UtcNow);
    }

    /// <summary>
    /// 成员请求打洞。成功时已经通知房主向成员发包，返回房主地址和证书指纹。
    /// </summary>
    public async Task<(int Status, JsonObject Body)> PunchAsync(RoomTicketService.Ticket member, LauncherTunnelService relay)
    {
        if (Port == null) return (503, Message("云端没有开启打洞"));
        var room = _rooms.Get(member.RoomId);
        if (room == null) return (404, Message("房间不存在或已关闭"));
        var now = DateTime.UtcNow;
        var key = (member.RoomId, member.AccountId);
        if (_lastPunch.TryGetValue(key, out var last) && now - last < PunchCooldown) return (429, Message("打洞请求太频繁"));
        _lastPunch[key] = now;
        if (!_seen.TryGetValue((room.Id, room.HostId), out var host) || now - host.At > HostFresh || host.Fingerprint == null)
            return (409, Message("房主还没有登记 UDP 地址"));
        if (!_seen.TryGetValue(key, out var self) || now - self.At > MemberFresh)
            return (409, Message("还没有收到你的 UDP 登记包"));
        if (!await relay.SendToHostAsync(room.Id, "punch " + self.EndPoint))
            return (503, Message("房主的启动器没有连上云端中继"));
        return (200, new JsonObject
        {
            ["ok"] = true,
            ["hostAddr"] = host.EndPoint.ToString(),
            ["fingerprint"] = host.Fingerprint,
            ["yourAddr"] = self.EndPoint.ToString()
        });
    }

    /// <summary>每分钟清掉过期的登记。</summary>
    private void Sweep()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, seen) in _seen)
            if (now - seen.At > TimeSpan.FromMinutes(2)) _seen.TryRemove(key, out _);
        foreach (var (key, at) in _lastPunch)
            if (now - at > TimeSpan.FromMinutes(2)) _lastPunch.TryRemove(key, out _);
    }

    private static JsonObject Message(string message) => new() { ["ok"] = false, ["message"] = message };

    public void Dispose()
    {
        _stop.Cancel();
        _sweeper?.Dispose();
        _udp?.Dispose();
    }
}
