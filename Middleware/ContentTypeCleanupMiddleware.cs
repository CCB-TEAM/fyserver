namespace fyserver.Middleware;

/// <summary>响应发送前去除 Content-Type 中的 "; charset=utf-8" 后缀（客户端协议要求）。</summary>
public class ContentTypeCleanupMiddleware
{
    private readonly RequestDelegate _next;

    public ContentTypeCleanupMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["Content-Type"] = context.Response.Headers["Content-Type"].ToString().Replace("; charset=utf-8", "");
            return Task.CompletedTask;
        });
        await _next(context);
    }
}
