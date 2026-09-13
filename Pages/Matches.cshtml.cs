using fyserver.Models;
using fyserver.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace fyserver.Pages;

/// <summary>对局监控：进行中的真人对局与各匹配队列。</summary>
[IgnoreAntiforgeryToken]
public class MatchesModel : PageModel
{
    private readonly MatchManagerService _matches;
    private readonly UserStoreService _users;
    private readonly WebSocketHubService _hub;

    public MatchesModel(MatchManagerService matches, UserStoreService users, WebSocketHubService hub)
    {
        _matches = matches;
        _users = users;
        _hub = hub;
    }

    public List<MatchRow> ActiveMatches { get; private set; } = new();
    public List<QueueRow> Queues { get; private set; } = new();
    public int OnlineCount { get; private set; }

    public sealed record MatchRow(
        int MatchId,
        string MatchType,
        string Status,
        int CurrentTurn,
        int ActionCount,
        int LeftPlayerId,
        string LeftPlayerName,
        string LeftStatus,
        bool LeftOnline,
        int RightPlayerId,
        string RightPlayerName,
        string RightStatus,
        bool RightOnline);

    public sealed record QueueRow(string Name, List<(int PlayerId, string Name)> Players);

    public async Task OnGetAsync()
    {
        OnlineCount = _hub.OnlineCount;

        var names = new Dictionary<int, string>();
        foreach (var user in await _users.GetAllUsersAsync())
            names[user.Id] = user.UserName;

        var onlineIds = _hub.OnlineUserIds().ToHashSet();

        string NameOf(int id) => id <= 0
            ? (id == -9178 ? "人机" : $"#{id}")
            : names.TryGetValue(id, out var name) ? name : $"#{id}";

        ActiveMatches = _matches.GetActiveRealMatches()
            .Select(m => new MatchRow(
                m.MatchId,
                string.IsNullOrEmpty(m.Ex) ? "classic" : m.Ex,
                m.WinnerSide is null ? "进行中" : $"已结束（{m.WinnerSide}）",
                m.Turns,
                m.MatchActions.Count,
                m.Left?.PlayerId ?? 0,
                NameOf(m.Left?.PlayerId ?? 0),
                m.PlayerStatusLeft,
                (m.Left?.PlayerId ?? 0) > 0 && onlineIds.Contains(m.Left!.PlayerId),
                m.Right?.PlayerId ?? 0,
                NameOf(m.Right?.PlayerId ?? 0),
                m.PlayerStatusRight,
                (m.Right?.PlayerId ?? 0) > 0 && onlineIds.Contains(m.Right!.PlayerId)))
            .ToList();

        void AddQueue(string name, List<LobbyPlayer> players)
        {
            if (players.Count == 0)
                return;
            Queues.Add(new QueueRow(name, players.Select(p => (p.PlayerId, NameOf(p.PlayerId))).ToList()));
        }

        AddQueue("经典（1/2 号位）", _matches.WaitingPlayers1.Concat(_matches.WaitingPlayers2).ToList());
        AddQueue("战役 / 战斗码", _matches.WaitingPlayersClassic);
        AddQueue("休闲", _matches.WaitingPlayersUnranked);
        AddQueue("竞技场", _matches.WaitingPlayersDraft);
        AddQueue("乱斗", _matches.WaitingPlayersBrawl);
    }

    /// <summary>强制移除一条对局（异常残留对局清理用），等价控制台 cm 的单条版本。</summary>
    public IActionResult OnPostRemoveMatch(int matchId)
    {
        if (!_matches.RemoveMatch(matchId))
        {
            TempData["Error"] = $"对局 {matchId} 不存在";
            return RedirectToPage();
        }
        TempData["Message"] = $"已移除对局 {matchId}（玩家需重新匹配）";
        return RedirectToPage();
    }

    /// <summary>清空所有匹配队列。</summary>
    public IActionResult OnPostClearQueues()
    {
        _matches.WaitingPlayers1.Clear();
        _matches.WaitingPlayers2.Clear();
        _matches.WaitingPlayersClassic.Clear();
        _matches.WaitingPlayersUnranked.Clear();
        _matches.WaitingPlayersDraft.Clear();
        _matches.WaitingPlayersBrawl.Clear();
        TempData["Message"] = "已清空全部匹配队列";
        return RedirectToPage();
    }
}
