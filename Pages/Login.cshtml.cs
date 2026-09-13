using fyserver.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace fyserver.Pages;

/// <summary>后台登录页：输入 setting.json 中的 adminApiKey，换取会话 Cookie。</summary>
[IgnoreAntiforgeryToken]
public class LoginModel : PageModel
{
    [BindProperty]
    public string? Key { get; set; }

    public bool KeyConfigured { get; private set; }

    public void OnGet()
    {
        var options = HttpContext.RequestServices.GetRequiredService<ServerOptions>();
        KeyConfigured = !string.IsNullOrEmpty(options.adminApiKey);

        if (!KeyConfigured)
            TempData["Message"] = "当前未配置 adminApiKey，本机访问无需登录。";
    }

    public IActionResult OnPost()
    {
        var options = HttpContext.RequestServices.GetRequiredService<ServerOptions>();
        KeyConfigured = !string.IsNullOrEmpty(options.adminApiKey);

        if (!KeyConfigured)
            return RedirectToPage("/Index");

        if (!AdminAuth.CheckKey(Key, options))
        {
            TempData["Error"] = "管理密钥不正确";
            return RedirectToPage();
        }

        var value = AdminAuth.CreateSessionValue(options);
        if (value != null)
        {
            Response.Cookies.Append(AdminAuth.CookieName, value, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                IsEssential = true,
                Expires = DateTimeOffset.UtcNow.AddDays(7)
            });
        }

        TempData["Message"] = "登录成功";
        return RedirectToPage("/Index");
    }

    /// <summary>退出登录：清除会话 Cookie（不影响 X-Admin-Key 方式）。</summary>
    public IActionResult OnPostLogout()
    {
        Response.Cookies.Delete(AdminAuth.CookieName);
        TempData["Message"] = "已退出登录";
        return RedirectToPage();
    }
}
