using fyserver.Models;
using fyserver.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace fyserver.Pages;

/// <summary>用户详情：账户信息、卡组列表与单个用户的封禁/踢出/删除。</summary>
[IgnoreAntiforgeryToken]
public class UserModel : PageModel
{
    private readonly AdminUserService _adminUsers;
    private readonly WebSocketHubService _hub;
    private readonly MatchManagerService _matches;

    public UserModel(AdminUserService adminUsers, WebSocketHubService hub, MatchManagerService matches)
    {
        _adminUsers = adminUsers;
        _hub = hub;
        _matches = matches;
    }

    [BindProperty(SupportsGet = true, Name = "id")]
    public int UserId { get; set; }

    public User? Target { get; private set; }
    public bool IsOnline { get; private set; }
    public MatchInfo? ActiveMatch { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        Target = await _adminUsers.GetAsync(UserId);
        if (Target == null)
        {
            TempData["Error"] = $"用户 {UserId} 不存在";
            return RedirectToPage("/Users");
        }

        IsOnline = _hub.TryGetClient(UserId, out _);
        ActiveMatch = _matches.GetActiveMatchForUser(UserId);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? op, string? reason)
    {
        var (ok, message) = op switch
        {
            "ban" => await _adminUsers.BanAsync(UserId),
            "unban" => await _adminUsers.UnbanAsync(UserId),
            "kick" => await _adminUsers.KickAsync(UserId, reason),
            "delete" => await _adminUsers.DeleteAsync(UserId),
            _ => (false, $"未知操作：{op}")
        };

        if (ok)
            TempData["Message"] = message;
        else
            TempData["Error"] = message;

        return RedirectToPage(new { id = UserId });
    }
}
