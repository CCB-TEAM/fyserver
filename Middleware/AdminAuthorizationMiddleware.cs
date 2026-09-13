using System.Net;
using fyserver.Services;

namespace fyserver.Middleware;

/// <summary>
/// 保护 /admin 路由（Razor 后台页面与 /admin/* 管理 API）。
/// 规则：
///   1. 本机（loopback）访问直接放行，与原有行为一致；
///   2. 远程访问必须携带 X-Admin-Key（值等于 setting.json 的 adminApiKey），
///      或通过 /admin/login 登录后拿到的签名 Cookie；
///   3. adminApiKey 为空时不接受任何远程访问；
///   4. /admin/login 始终放行，否则无法输入管理密钥。
/// </summary>
public sealed class AdminAuthorizationMiddleware
{
    private readonly RequestDelegate _next;

    public AdminAuthorizationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ServerOptions options)
    {
        if (!context.Request.Path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // 登录页始终放行
        if (context.Request.Path.Equals("/admin/login", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var isLoopback = context.Connection.RemoteIpAddress is { } remoteAddress && IPAddress.IsLoopback(remoteAddress);

        var authorized = isLoopback ||
                         AdminAuth.ValidateSession(context.Request.Cookies[AdminAuth.CookieName], options) ||
                         AdminAuth.CheckKey(context.Request.Headers["X-Admin-Key"].FirstOrDefault(), options);

        if (!authorized)
        {
            // 浏览器访问后台页面时给出可操作的 401 页面，而不是空响应
            if (AcceptsHtml(context))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(
                    "<!DOCTYPE html><html lang=\"zh-Hans\"><head><meta charset=\"utf-8\">" +
                    "<title>401 需要授权</title></head><body style=\"font-family:sans-serif;padding:2rem\">" +
                    "<h2>需要授权</h2><p>请通过 <a href=\"/admin/login\">/admin/login</a> 登录，" +
                    "或使用 <code>X-Admin-Key</code> 请求头访问。</p></body></html>");
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await _next(context);
    }

    private static bool AcceptsHtml(HttpContext context)
    {
        var accept = context.Request.Headers.Accept.ToString();
        return context.Request.Method == HttpMethods.Get &&
               accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }
}