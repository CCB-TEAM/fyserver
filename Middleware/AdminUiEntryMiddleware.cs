namespace fyserver.Middleware;

/// <summary>
/// 静态后台的入口重定向：<c>/admin-ui</c> 与 <c>/admin-ui/</c> → <c>/admin-ui/index.html</c>。
///
/// 原因：UseStaticFiles() 不做"目录默认页"，直接访问 /admin-ui/ 会 404，
/// 而这是用户最自然会输入的地址（旧 Razor 后台是 /admin/login，现在换成 /admin-ui/）。
/// 登录页另有 /admin-ui/login → login.html 的别名，避免必须记住 .html 后缀。
/// 只处理 GET/HEAD，且只在这两个精确路径上生效，其余请求原样放行。
/// </summary>
public sealed class AdminUiEntryMiddleware
{
    private readonly RequestDelegate _next;

    public AdminUiEntryMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            var path = context.Request.Path.Value ?? "";
            var target = path switch
            {
                "/admin-ui" => "/admin-ui/index.html",
                "/admin-ui/" => "/admin-ui/index.html",
                "/admin-ui/login" => "/admin-ui/login.html",
                _ => null
            };

            if (target != null)
            {
                context.Response.Redirect(context.Request.PathBase + target + context.Request.QueryString, permanent: false);
                return;
            }
        }

        await _next(context);
    }
}
