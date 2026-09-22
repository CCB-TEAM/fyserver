using fyserver.Services;

namespace fyserver.Middleware;

/// <summary>用户数据库未就绪时只开放后台初始化所需资源，游戏服务返回 503。</summary>
public sealed class ServerInitializationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, UserStoreService users)
    {
        if (users.IsReady || IsInitializationPath(context.Request.Path))
        {
            await next(context);
            return;
        }
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.RetryAfter = "30";
        await context.Response.WriteAsync("{\"error\":\"server_initialization_required\",\"message\":\"请先在后台完成用户数据库配置\",\"status_code\":503}");
    }

    private static bool IsInitializationPath(PathString path)
    {
        if (path.StartsWithSegments("/admin-ui")) return true;
        return path.Equals("/admin/api/session", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/admin/api/setup", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/admin/api/login", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/admin/api/logout", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWithSegments("/admin/api/database", StringComparison.OrdinalIgnoreCase);
    }
}
