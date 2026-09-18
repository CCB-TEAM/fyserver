using fyserver.Services;

namespace fyserver.Middleware;

/// <summary>
/// 保护静态后台的数据接口 /admin/api/*。
///
/// 与 Razor 后台的 AdminAuthorizationMiddleware 分开的原因：
///   - 静态页本身（/admin-ui/*.html、/admin-ui/assets/*）必须匿名可访问，否则连登录页都打不开；
///   - 这里只挡 /admin/api/*：本机放行；远程需 X-Admin-Key 或 /admin/api/login 下发的会话 Cookie；
///     未配置 adminApiKey 时不接受任何远程调用。
/// 这样两套后台（Razor 版 /admin、静态版 /admin-ui）可以并存且互不干扰。
/// </summary>
public sealed class AdminApiAuthorizationMiddleware
{
    private readonly RequestDelegate _next;

    public AdminApiAuthorizationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ServerOptions options)
    {
        if (!context.Request.Path.StartsWithSegments("/admin/api", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // 登录接口放行（静态页用它换 Cookie）
        if (context.Request.Path.Equals("/admin/api/login", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var authorized = ClientAddress.IsLoopback(context) ||
                         AdminAuth.ValidateSession(context.Request.Cookies[AdminAuth.CookieName], options) ||
                         AdminAuth.CheckKey(context.Request.Headers["X-Admin-Key"].FirstOrDefault(), options);

        if (!authorized)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"ok\":false,\"message\":\"需要授权：请先登录或携带 X-Admin-Key\"}");
            return;
        }

        await _next(context);
    }
}
