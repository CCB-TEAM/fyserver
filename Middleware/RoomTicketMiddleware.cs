using fyserver.Services;

namespace fyserver.Middleware;

/// <summary>
/// 嵌入模式开房后，所有请求都必须带本房间有效的进房票据（启动器的本地网关负责加上）。
/// 玩家身份以票据里的启动器用户名为准：/session 用它建号，其它请求里游戏自带的 JWT 必须属于同一个人。
/// </summary>
public sealed class RoomTicketMiddleware(RequestDelegate next, RoomTicketVerifier verifier, CodecService codec)
{
    private const string JwtPrefix = "JWT ";

    public async Task InvokeAsync(HttpContext context)
    {
        // 探活不需要票据，方便启动器在拿到票据前确认服务端已就绪
        if (context.Request.Path.StartsWithSegments("/embedded/health"))
        {
            await next(context);
            return;
        }

        var claims = verifier.Verify(context.Request.Headers[RoomTicketVerifier.HeaderName].FirstOrDefault(), DateTimeOffset.UtcNow);
        if (claims == null)
        {
            await Reject(context, "ticket_required", "没有有效的进房票据，请通过启动器加入房间");
            return;
        }

        var tokenUser = TokenUserName(context.Request.Headers.Authorization.FirstOrDefault());
        if (tokenUser != null && !string.Equals(tokenUser, claims.Name, StringComparison.Ordinal))
        {
            await Reject(context, "identity_mismatch", "登录身份和启动器账号不一致，请重新进入游戏");
            return;
        }

        RoomTicketVerifier.Attach(context, claims);
        await next(context);
    }

    /// <summary>游戏请求里 Authorization 头编码的用户名；没有或解不开时返回 null。</summary>
    private string? TokenUserName(string? authorization)
    {
        if (string.IsNullOrEmpty(authorization)) return null;
        var encoded = authorization.StartsWith(JwtPrefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[JwtPrefix.Length..].Trim()
            : authorization.Trim();
        if (encoded.Length == 0) return null;
        try { return codec.Decode(encoded, out _); }
        catch { return null; }
    }

    private static Task Reject(HttpContext context, string error, string message)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(
            $"{{\"error\":\"{error}\",\"message\":\"{message}\",\"status_code\":403}}");
    }
}
