using fyserver.Models;

namespace fyserver.Services;

/// <summary>
/// 基于 Authorization: JWT &lt;用户名&gt; 头（经 codec.dll 编码）的身份解析。
/// 替代原 http 类的静态 GetPlayerIdFromAuthAsync / GetUserFromAuthAsync。
/// </summary>
public class AuthService
{
    private readonly UserStoreService _users;
    private readonly CodecService _codec;
    private const string SchemePrefix = "JWT ";

    public AuthService(UserStoreService users, CodecService codec)
    {
        _users = users;
        _codec = codec;
    }

    public async Task<int> GetPlayerIdFromAuthAsync(HttpContext context)
    {
        var user = await GetUserFromAuthAsync(context);
        return user?.Id ?? 0;
    }

    public async Task<User?> GetUserFromAuthAsync(HttpContext context)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith(SchemePrefix, StringComparison.Ordinal))
            return null;

        var encoded = authHeader[SchemePrefix.Length..].Trim();
        if (string.IsNullOrEmpty(encoded))
            return null;

        string userName;
        try
        {
            userName = _codec.Decode(encoded, out _);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"认证解码失败: {ex.Message}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(userName))
            return null;

        var user = await _users.GetByUserNameAsync(userName);
        if (user != null)
            Console.WriteLine($"Authorization header found: {userName} (id={user.Id})");

        return user;
    }
}
