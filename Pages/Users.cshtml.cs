using fyserver.Models;
using fyserver.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace fyserver.Pages;

/// <summary>用户管理：搜索、封禁/解封、踢下线、删除。</summary>
[IgnoreAntiforgeryToken]
public class UsersModel : PageModel
{
    private readonly AdminUserService _adminUsers;
    private readonly WebSocketHubService _hub;

    public UsersModel(AdminUserService adminUsers, WebSocketHubService hub)
    {
        _adminUsers = adminUsers;
        _hub = hub;
    }

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    public List<User> Users { get; private set; } = new();
    public HashSet<int> OnlineIds { get; private set; } = new();

    private string ReturnUrl =>
        string.IsNullOrWhiteSpace(Query)
            ? Url.Page("/Users")!
            : Url.Page("/Users", new { q = Query })!;

    public async Task OnGetAsync()
    {
        var all = await _adminUsers.ListAsync();
        all.Sort((a, b) => a.Id.CompareTo(b.Id));

        var keyword = Query?.Trim();
        if (!string.IsNullOrEmpty(keyword))
        {
            all = all.Where(u =>
                    u.Id.ToString().Contains(keyword, StringComparison.Ordinal) ||
                    u.UserName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    u.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        Users = all;
        OnlineIds = _hub.OnlineUserIds().ToHashSet();
    }

    public async Task<IActionResult> OnPostAsync(int userId, string? op, string? reason)
    {
        var (ok, message) = op switch
        {
            "ban" => await _adminUsers.BanAsync(userId),
            "unban" => await _adminUsers.UnbanAsync(userId),
            "kick" => await _adminUsers.KickAsync(userId, reason),
            "delete" => await _adminUsers.DeleteAsync(userId),
            _ => (false, $"未知操作：{op}")
        };

        if (ok)
            TempData["Message"] = message;
        else
            TempData["Error"] = message;

        return Redirect(ReturnUrl);
    }
}
