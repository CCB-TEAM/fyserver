using System.Net;
using System.Security.Cryptography;
using System.Text;
using fyserver.Services;

namespace fyserver.Middleware;

/// <summary>
/// 保护 /admin 路由：配置 adminApiKey 后要求 X-Admin-Key；未配置时仅允许 loopback。
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

        bool authorized;
        if (string.IsNullOrEmpty(options.adminApiKey))
        {
            authorized = context.Connection.RemoteIpAddress is { } remoteAddress && IPAddress.IsLoopback(remoteAddress);
        }
        else
        {
            var supplied = context.Request.Headers["X-Admin-Key"].FirstOrDefault() ?? "";
            var expectedBytes = Encoding.UTF8.GetBytes(options.adminApiKey);
            var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
            authorized = expectedBytes.Length == suppliedBytes.Length &&
                         CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
        }

        if (!authorized)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await _next(context);
    }
}
