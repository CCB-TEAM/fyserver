using fyserver.Services;

namespace fyserver.Middleware;

/// <summary>后台会话鉴权与按类别授权；权限始终在服务端校验，前端隐藏按钮仅用于体验。</summary>
public sealed class AdminApiAuthorizationMiddleware
{
    private readonly RequestDelegate _next;
    public AdminApiAuthorizationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, AdminAccountService accounts, AdminAuditLogService audit)
    {
        var path = context.Request.Path.Value ?? "";
        if (!context.Request.Path.StartsWithSegments("/admin/api", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }
        if (path.Equals("/admin/api/login", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/admin/api/session", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/admin/api/setup", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var actor = accounts.GetSessionAccount(context.Request.Cookies[AdminAccountService.CookieName]);
        if (actor == null)
        {
            await Reject(context, 401, "请先登录后台账号");
            return;
        }
        context.Items["adminActor"] = actor;

        var local = path["/admin/api".Length..];
        var permission = RequiredPermission(local);
        var mutating = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method);
        var isSelfAccountRoute = local.Equals("/me", StringComparison.OrdinalIgnoreCase) || local.StartsWith("/me/", StringComparison.OrdinalIgnoreCase);
        if (local.Equals("/logout", StringComparison.OrdinalIgnoreCase)) mutating = false;
        var isPlayerRoleChange = mutating && local.StartsWith("/users/", StringComparison.OrdinalIgnoreCase) &&
                                 local.EndsWith("/roles", StringComparison.OrdinalIgnoreCase);
        if (isPlayerRoleChange && !accounts.HasPermission(actor, "permissions"))
        {
            await Reject(context, 403, "修改玩家角色需要权限管理权限");
            audit.Record(actor.Username, context.Request.Method, path, 403, context.Connection.RemoteIpAddress?.ToString());
            return;
        }
        if (permission != null && (mutating || permission == "permissions") && !accounts.HasPermission(actor, permission))
        {
            await Reject(context, 403, "没有执行此操作所需的权限");
            audit.Record(actor.Username, context.Request.Method, path, 403, context.Connection.RemoteIpAddress?.ToString());
            return;
        }
        if (mutating && permission == null && !actor.IsOwner && !isSelfAccountRoute)
        {
            await Reject(context, 403, "此操作仅 Owner 可执行");
            audit.Record(actor.Username, context.Request.Method, path, 403, context.Connection.RemoteIpAddress?.ToString());
            return;
        }

        try { await _next(context); }
        catch
        {
            if (mutating) audit.Record(actor.Username, context.Request.Method, path, 500, context.Connection.RemoteIpAddress?.ToString());
            throw;
        }
        if (mutating || local.Equals("/logout", StringComparison.OrdinalIgnoreCase))
            audit.Record(actor.Username, context.Request.Method, path, context.Response.StatusCode, context.Connection.RemoteIpAddress?.ToString());
    }

    private static string? RequiredPermission(string local)
    {
        if (local.StartsWith("/users", StringComparison.OrdinalIgnoreCase)) return "players";
        if (local.StartsWith("/content", StringComparison.OrdinalIgnoreCase) || local.StartsWith("/store", StringComparison.OrdinalIgnoreCase)) return "content";
        if (local.StartsWith("/redeem", StringComparison.OrdinalIgnoreCase)) return "content";
        if (local.StartsWith("/matches", StringComparison.OrdinalIgnoreCase) || local.StartsWith("/queues", StringComparison.OrdinalIgnoreCase)) return "matches";
        if (local.StartsWith("/server-config", StringComparison.OrdinalIgnoreCase)) return "serverConfig";
        if (local.StartsWith("/system-settings", StringComparison.OrdinalIgnoreCase)) return "systemSettings";
        if (local.StartsWith("/database", StringComparison.OrdinalIgnoreCase)) return "permissions";
        if (local.StartsWith("/accounts", StringComparison.OrdinalIgnoreCase) || local.StartsWith("/audit-logs", StringComparison.OrdinalIgnoreCase)) return "permissions";
        return null;
    }

    private static async Task Reject(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(new System.Text.Json.Nodes.JsonObject { ["ok"] = false, ["message"] = message }.ToJsonString());
    }
}
