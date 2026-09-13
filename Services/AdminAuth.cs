using System.Security.Cryptography;
using System.Text;

namespace fyserver.Services;

/// <summary>
/// 后台会话：登录成功后下发 HMAC 签名 Cookie，
/// 使 Razor 后台页面无需在每个请求上携带 X-Admin-Key（命令行/脚本仍可用该请求头）。
/// </summary>
public static class AdminAuth
{
    public const string CookieName = "fyserver_admin";
    private const string SigningSecret = "fyserver-admin-panel-v1";

    /// <summary>用当前管理密钥生成会话值；密钥为空（仅 loopback 模式）时返回 null。</summary>
    public static string? CreateSessionValue(ServerOptions options)
    {
        if (string.IsNullOrEmpty(options.adminApiKey))
            return null;
        return Sign(options.adminApiKey);
    }

    /// <summary>校验浏览器 Cookie 中的会话值。</summary>
    public static bool ValidateSession(string? cookieValue, ServerOptions options)
    {
        if (string.IsNullOrEmpty(cookieValue) || string.IsNullOrEmpty(options.adminApiKey))
            return false;

        var expected = Sign(options.adminApiKey);
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(cookieValue);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>固定时间比较管理密钥。</summary>
    public static bool CheckKey(string? supplied, ServerOptions options)
    {
        if (string.IsNullOrEmpty(supplied) || string.IsNullOrEmpty(options.adminApiKey))
            return false;

        var expectedBytes = Encoding.UTF8.GetBytes(options.adminApiKey);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static string Sign(string key)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(SigningSecret), Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }
}
