using System.Net;

namespace fyserver.Services;

/// <summary>请求来源判断。后台鉴权需要区分本机与远程访问。</summary>
public static class ClientAddress
{
    /// <summary>是否为 loopback（127.0.0.1 / ::1）请求。</summary>
    public static bool IsLoopback(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);
}
