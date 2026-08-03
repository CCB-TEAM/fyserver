namespace fyserver.Middleware;

/// <summary>将请求路径中的重复斜杠（//）归一化为单斜杠。</summary>
public class PathNormalizationMiddleware
{
    private readonly RequestDelegate _next;

    public PathNormalizationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (!string.IsNullOrEmpty(path) && path.Contains("//", StringComparison.Ordinal))
        {
            while (path.Contains("//", StringComparison.Ordinal))
            {
                path = path.Replace("//", "/");
            }
            context.Request.Path = path;
        }
        await _next(context);
    }
}
