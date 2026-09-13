using fyserver.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace fyserver.Pages;

/// <summary>
/// 后台登录页：输入 setting.json 中的 adminApiKey，换取会话 Cookie。
///
/// 关键规则（未配置 adminApiKey 时不能出现登录死循环）：
///   - 本机（loopback）访问：直接放行，无需登录，登录页只做提示；
///   - 远程访问且未配置 adminApiKey：明确告知"仅本机可访问"并提示配置方法，
///     不显示登录表单（否则任何输入都会被判为密钥错误，来回重定向）；
///   - 远程访问且已配置 adminApiKey：正常校验并下发 Cookie。
/// </summary>
[IgnoreAntiforgeryToken]
public class LoginModel : PageModel
{
    [BindProperty]
    public string? Key { get; set; }

    /// <summary>是否已配置 adminApiKey。</summary>
    public bool KeyConfigured { get; private set; }

    /// <summary>是否本机访问。</summary>
    public bool IsLoopback { get; private set; }

    /// <summary>是否可以提交密钥登录（仅在服务端配置了 adminApiKey 时）。</summary>
    public bool CanLogin => KeyConfigured;

    public void OnGet()
    {
        FillState(GetOptions());

        if (IsLoopback && !KeyConfigured)
            TempData["Message"] = "当前未配置 adminApiKey，本机访问无需登录，可直接进入后台。";
    }

    public IActionResult OnPost()
    {
        var options = GetOptions();
        FillState(options);

        // 本机访问无需密钥（与 AdminAuthorizationMiddleware 的放行规则保持一致）
        if (IsLoopback && !KeyConfigured)
            return RedirectToPage("/Index");

        if (!KeyConfigured)
        {
            // 远程 + 未配置密钥：给出明确原因，不要用"密钥不正确"误导操作者
            TempData["Error"] = "服务器未在 setting.json 中配置 adminApiKey，远程访问后台被拒绝。" +
                                "请在服务端填写 adminApiKey 后重启，或改由本机访问后台。";
            return RedirectToPage();
        }

        if (!AdminAuth.CheckKey(Key, options))
        {
            TempData["Error"] = "管理密钥不正确";
            return RedirectToPage();
        }

        var value = AdminAuth.CreateSessionValue(options);
        if (value == null)
        {
            TempData["Error"] = "会话创建失败：adminApiKey 为空。";
            return RedirectToPage();
        }

        Response.Cookies.Append(AdminAuth.CookieName, value, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Expires = DateTimeOffset.UtcNow.AddDays(7)
        });

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

    private ServerOptions GetOptions() => HttpContext.RequestServices.GetRequiredService<ServerOptions>();

    private void FillState(ServerOptions options)
    {
        IsLoopback = ClientAddress.IsLoopback(HttpContext);
        KeyConfigured = !string.IsNullOrEmpty(options.adminApiKey);
    }
}
