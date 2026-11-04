using System.Diagnostics;

namespace fyserver.Services;

/// <summary>
/// 嵌入模式：由启动器作为子进程拉起，给房主在本机开房用。
/// 只听 127.0.0.1，自动使用本地存储，不提供后台 / 启动器账号 / 模组索引接口，没有控制台命令循环。
/// 游戏客户端固定连本机 127.0.0.1:1145（启动器的本地网关），所以下发给客户端的地址是网关地址，
/// 而本进程实际监听另一个本地端口，由网关转发过来。
///
/// 命令行：--embedded [--port 11450] [--public-port 1145] [--parent-pid 1234]
///        [--room-id 8MJ257 --ticket-pubkey &lt;base64 SPKI&gt;]
/// 带上房间号和公钥时进入联机房间模式：每个请求都要带本房间的进房票据。
/// </summary>
public sealed class EmbeddedMode
{
    public const int DefaultPort = 11450;
    public const int DefaultPublicPort = 1145;

    public bool Enabled { get; private init; }
    public int Port { get; private init; } = DefaultPort;
    public int PublicPort { get; private init; } = DefaultPublicPort;
    public int? ParentPid { get; private init; }
    public string? RoomId { get; private init; }
    public string? TicketPublicKey { get; private init; }

    /// <summary>联机房间模式：别人经隧道进来，必须校验票据。</summary>
    public bool RequiresTicket => RoomId != null && TicketPublicKey != null;

    public static EmbeddedMode Parse(string[] args)
    {
        if (!args.Any(a => a.Equals("--embedded", StringComparison.OrdinalIgnoreCase)))
            return new EmbeddedMode();

        return new EmbeddedMode
        {
            Enabled = true,
            Port = IntArg(args, "--port") ?? DefaultPort,
            PublicPort = IntArg(args, "--public-port") ?? DefaultPublicPort,
            ParentPid = IntArg(args, "--parent-pid"),
            RoomId = StringArg(args, "--room-id"),
            TicketPublicKey = StringArg(args, "--ticket-pubkey"),
        };
    }

    private static string? StringArg(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1])) return null;
        return args[i + 1].Trim();
    }

    private static int? IntArg(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length) return null;
        if (!int.TryParse(args[i + 1], out var value) || value <= 0 || value > 65535)
            throw new ArgumentException($"{name} 的值无效：{args[i + 1]}");
        return value;
    }

    /// <summary>覆盖 setting.json 里的网络设置（只改内存，不写回文件）。</summary>
    public void Apply(ServerOptions options)
    {
        options.listenIp = "127.0.0.1";
        options.portHttp = Port;
        options.ip = "127.0.0.1";
        options.publicPortHttp = PublicPort;
        options.publicScheme = "http";
        options.adminApiKey = "";
    }

    /// <summary>首次运行时自动选本地存储，不需要进后台配置数据库。</summary>
    public static void EnsureLocalStorage(UserDatabaseConfigurationService configuration)
    {
        UserDatabaseSettings? current = null;
        try { current = configuration.Load(); }
        catch { /* 配置损坏时直接覆盖为本地存储 */ }
        if (current?.Provider == "local") return;
        configuration.Save(new UserDatabaseSettings("local", "", 0, "", "", "", false));
    }

    /// <summary>启动器退出（包括崩溃）后本进程跟着退出，不留后台进程。</summary>
    public void WatchParent(IHostApplicationLifetime lifetime)
    {
        if (ParentPid is not { } pid) return;
        Process parent;
        try { parent = Process.GetProcessById(pid); }
        catch (ArgumentException)
        {
            Console.WriteLine($"父进程 {pid} 不存在，退出");
            lifetime.StopApplication();
            return;
        }

        _ = Task.Run(async () =>
        {
            try { await parent.WaitForExitAsync(lifetime.ApplicationStopping); }
            catch (OperationCanceledException) { return; }
            Console.WriteLine("启动器已退出，内置服务端随之关闭");
            lifetime.StopApplication();
        });
    }
}
