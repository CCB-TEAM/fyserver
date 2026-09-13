using fyserver.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace fyserver.Pages;

/// <summary>后台概览：进程/端口、用户与连接统计、常用运维操作。</summary>
[IgnoreAntiforgeryToken]
public class IndexModel : PageModel
{
    private readonly UserStoreService _users;
    private readonly WebSocketHubService _hub;
    private readonly MatchManagerService _matches;
    private readonly StoreConfigService _store;

    public IndexModel(
        UserStoreService users,
        WebSocketHubService hub,
        MatchManagerService matches,
        StoreConfigService store)
    {
        _users = users;
        _hub = hub;
        _matches = matches;
        _store = store;
    }

    public string HttpListenAddress { get; private set; } = "";
    public string HttpClientAddress { get; private set; } = "";
    public string WebSocketAddress { get; private set; } = "";
    public int Port { get; private set; }
    public string Ip { get; private set; } = "";
    public bool BanCheat { get; private set; }
    public bool AdminKeyConfigured { get; private set; }
    public string StartedAt { get; private set; } = "";
    public string Uptime { get; private set; } = "";
    public string OsVersion { get; private set; } = "";
    public string RuntimeVersion { get; private set; } = "";
    public int UserCount { get; private set; }
    public int BannedCount { get; private set; }
    public int DeckCount { get; private set; }
    public int OnlineCount { get; private set; }
    public int ActiveMatchCount { get; private set; }
    public int QueuedPlayerCount { get; private set; }
    public List<(string Name, int Count)> Queues { get; } = new();
    public string SettingJson { get; private set; } = "";

    public async Task OnGetAsync()
    {
        FillStaticInfo();
        await FillStatsAsync();
    }

    /// <summary>重载商店配置（等同控制台 reloadstore）。</summary>
    public IActionResult OnPostReloadStore()
    {
        _store.Reload();
        TempData["Message"] = "商店配置已重新加载（config/store.json）";
        return RedirectToPage();
    }

    private void FillStaticInfo()
    {
        var options = HttpContext.RequestServices.GetRequiredService<ServerOptions>();
        HttpListenAddress = options.GetAddressHttp();
        HttpClientAddress = options.GetAddressHttpR();
        WebSocketAddress = options.GetAddressWsR();
        Port = options.portHttp;
        Ip = options.ip;
        BanCheat = options.bancheat;
        AdminKeyConfigured = !string.IsNullOrEmpty(options.adminApiKey);

        var process = System.Diagnostics.Process.GetCurrentProcess();
        StartedAt = process.StartTime.ToString("yyyy-MM-dd HH:mm:ss");
        var uptime = DateTime.Now - process.StartTime;
        Uptime = $"{(int)uptime.TotalDays} 天 {uptime.Hours} 小时 {uptime.Minutes} 分 {uptime.Seconds} 秒";

        OsVersion = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        RuntimeVersion = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

        try
        {
            SettingJson = System.IO.File.Exists("./setting.json")
                ? System.IO.File.ReadAllText("./setting.json")
                : "(setting.json 不存在，启动时会生成默认配置)";
        }
        catch (IOException ex)
        {
            SettingJson = $"(读取失败：{ex.Message})";
        }
    }

    private async Task FillStatsAsync()
    {
        var all = await _users.GetAllUsersAsync();
        UserCount = all.Count;
        BannedCount = all.Count(u => u.Banned);
        DeckCount = all.Sum(u => u.Decks.Count);

        OnlineCount = _hub.OnlineCount;
        ActiveMatchCount = _matches.GetActiveRealMatches().Count;

        Queues.Add(("经典", _matches.WaitingPlayers1.Count + _matches.WaitingPlayers2.Count));
        Queues.Add(("战役/战斗码", _matches.WaitingPlayersClassic.Count));
        Queues.Add(("休闲", _matches.WaitingPlayersUnranked.Count));
        Queues.Add(("竞技场", _matches.WaitingPlayersDraft.Count));
        Queues.Add(("乱斗", _matches.WaitingPlayersBrawl.Count));
        QueuedPlayerCount = Queues.Sum(q => q.Count);
    }
}
