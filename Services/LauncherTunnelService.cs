using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace fyserver.Services;

/// <summary>
/// 房间隧道：用 frp 实现“服务器协助打洞（xtcp），失败回落中继（stcp）”。
/// 云端只跑 frps，本服务提供 frps 的 HTTP 插件接口，用进房票据决定谁能登录、谁能为哪个房间开代理。
///
/// 约定：房主的 frpc 为房间 R 注册两个代理，都指向本机嵌入版 fyserver：
///   room_R_p2p   类型 xtcp（打洞直连）
///   room_R_relay 类型 stcp（经 frps 中继）
/// 两者的 secretKey 都是开房响应里的 sk。加入者的 frpc 以 visitor 方式连接 room_R_p2p，
/// 并把 fallbackTo 设为 room_R_relay 的 visitor，打洞失败自动走中继。
/// 所有 frpc 登录时都要在 metadatas.ticket 里带上当前有效的进房票据。frpc 不要设置 user。
/// </summary>
public sealed class LauncherTunnelService
{
    private const string ConfigPath = "./data/launcher-tunnel.json";
    private readonly object _gate = new();
    private Settings? _settings;

    public sealed record Settings(bool Enabled, string ServerAddr, int ServerPort, string RelayBandwidthLimit, string PluginKey);

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
                    root?["enabled"]?.GetValue<bool>() ?? false,
                    root?["serverAddr"]?.GetValue<string>() ?? "",
                    root?["serverPort"]?.GetValue<int>() ?? 7000,
                    root?["relayBandwidthLimit"]?.GetValue<string>() ?? "256KB",
                    root?["pluginKey"]?.GetValue<string>() ?? "");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"读取 {ConfigPath} 失败：{ex.Message}");
                settings = new Settings(false, "", 7000, "256KB", "");
            }
            if (string.IsNullOrEmpty(settings.PluginKey))
            {
                settings = settings with { PluginKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)) };
                SaveLocked(settings);
            }
            _settings = settings;
            return settings;
        }
    }

    public (bool Ok, string Message, Settings? Settings) Save(bool enabled, string? serverAddr, int serverPort, string? bandwidth)
    {
        serverAddr = serverAddr?.Trim() ?? "";
        bandwidth = string.IsNullOrWhiteSpace(bandwidth) ? "256KB" : bandwidth.Trim().ToUpperInvariant();
        if (enabled && (serverAddr.Length is 0 or > 253 || serverAddr.Any(c => char.IsWhiteSpace(c) || c == '/')))
            return (false, "请填写 frps 的公网地址（域名或 IP，不带协议和端口）", null);
        if (serverPort is < 1 or > 65535) return (false, "端口需要在 1 到 65535 之间", null);
        if (!System.Text.RegularExpressions.Regex.IsMatch(bandwidth, "^[0-9]{1,6}(KB|MB)$")) return (false, "中继限速格式应为 256KB 或 1MB", null);
        lock (_gate)
        {
            var settings = new Settings(enabled, serverAddr, serverPort, bandwidth, Get().PluginKey);
            SaveLocked(settings);
            _settings = settings;
            return (true, "已保存", settings);
        }
    }

    private static void SaveLocked(Settings s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var json = new JsonObject
        {
            ["enabled"] = s.Enabled, ["serverAddr"] = s.ServerAddr, ["serverPort"] = s.ServerPort,
            ["relayBandwidthLimit"] = s.RelayBandwidthLimit, ["pluginKey"] = s.PluginKey
        };
        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, json.ToJsonString(new() { WriteIndented = true }));
        File.Move(temp, ConfigPath, true);
    }

    public static string P2pProxyName(string roomId) => $"room_{roomId}_p2p";
    public static string RelayProxyName(string roomId) => $"room_{roomId}_relay";

    /// <summary>开房 / 进房响应里给启动器的隧道参数。隧道未启用时返回 null（只能本机或局域网测试）。</summary>
    public JsonObject? ClientConfig(LauncherRoomService.Room room, string role)
    {
        var s = Get();
        if (!s.Enabled) return null;
        return new JsonObject
        {
            ["role"] = role,
            ["serverAddr"] = s.ServerAddr,
            ["serverPort"] = s.ServerPort,
            ["p2pProxy"] = P2pProxyName(room.Id),
            ["relayProxy"] = RelayProxyName(room.Id),
            ["sk"] = room.Sk
        };
    }

    // ==================== frps HTTP 插件 ====================

    /// <summary>
    /// 只接受本机 frps 的调用：连接必须来自回环地址、没有经过反向代理（无 X-Forwarded-For），
    /// 并且查询串 key 与 data/launcher-tunnel.json 里的 pluginKey 一致。
    /// </summary>
    public bool IsTrustedPluginCall(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote == null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)) return false;
        if (context.Request.Headers.ContainsKey("X-Forwarded-For") || context.Request.Headers.ContainsKey("Forwarded")) return false;
        var key = context.Request.Query["key"].ToString();
        var expected = Get().PluginKey;
        return key.Length == expected.Length &&
               CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(key), System.Text.Encoding.ASCII.GetBytes(expected));
    }

    /// <summary>处理 frps 插件请求 {"version","op","content"}，返回 frps 期望的响应对象。</summary>
    public JsonObject HandlePlugin(JsonObject request, RoomTicketService tickets, LauncherRoomService rooms)
    {
        var op = request["op"]?.GetValue<string>() ?? "";
        var content = request["content"] as JsonObject ?? new JsonObject();
        switch (op)
        {
            case "Login":
            {
                var (ticket, error) = CheckTicket(content["metas"] as JsonObject, tickets, rooms);
                if (ticket == null) return Reject(error);
                Console.WriteLine($"frp 登录：房间 {ticket.RoomId}，{ticket.Role} {ticket.Name}（#{ticket.AccountId}）");
                return Allow();
            }
            case "NewProxy":
            {
                var (ticket, error) = CheckTicket((content["user"] as JsonObject)?["metas"] as JsonObject, tickets, rooms);
                if (ticket == null) return Reject(error);
                if (ticket.Role != "host") return Reject("只有房主可以注册房间代理");
                var room = rooms.Get(ticket.RoomId);
                if (room == null) return Reject("房间不存在或已关闭");
                var name = content["proxy_name"]?.GetValue<string>() ?? "";
                var type = content["proxy_type"]?.GetValue<string>() ?? "";
                var sk = content["sk"]?.GetValue<string>() ?? "";
                var valid = (name == P2pProxyName(room.Id) && type == "xtcp") || (name == RelayProxyName(room.Id) && type == "stcp");
                if (!valid) return Reject($"只允许注册 {P2pProxyName(room.Id)}（xtcp）和 {RelayProxyName(room.Id)}（stcp）");
                if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(sk), System.Text.Encoding.UTF8.GetBytes(room.Sk)))
                    return Reject("代理密钥与房间不符");
                if (type == "stcp")
                {
                    // 中继走云端带宽：强制服务端限速，防止被当作免费代理。
                    var changed = (JsonObject)content.DeepClone();
                    changed["bandwidth_limit"] = Get().RelayBandwidthLimit;
                    changed["bandwidth_limit_mode"] = "server";
                    return new JsonObject { ["reject"] = false, ["unchange"] = false, ["content"] = changed };
                }
                return Allow();
            }
            // 只有 tcp/http 这类公开代理才会触发 NewUserConn；房间只允许 stcp/xtcp，一律拒绝。
            case "NewUserConn":
                return Reject("不允许公开代理");
            default:
                // Ping / NewWorkConn / CloseProxy 等：登录时已经校验过身份，放行。
                return Allow();
        }
    }

    /// <summary>
    /// 校验 metas.ticket：签名有效，且账号此刻仍在该房间里（以房间成员为准，不看票据是否过期，
    /// 因为 frpc 断线重连时会沿用登录时的 metas）。
    /// </summary>
    private static (RoomTicketService.Ticket? Ticket, string Error) CheckTicket(JsonObject? metas, RoomTicketService tickets, LauncherRoomService rooms)
    {
        var token = metas?["ticket"]?.GetValue<string>();
        var ticket = tickets.Verify(token, allowExpired: true);
        if (ticket == null) return (null, "缺少或无效的进房票据");
        if (!rooms.IsMember(ticket.RoomId, ticket.AccountId, out var role)) return (null, "你不在这个房间里或房间已关闭");
        if (role != ticket.Role) return (null, "票据身份与房间不符");
        return (ticket, "");
    }

    private static JsonObject Allow() => new() { ["reject"] = false, ["unchange"] = true };
    private static JsonObject Reject(string reason) => new() { ["reject"] = true, ["reject_reason"] = reason };
}
