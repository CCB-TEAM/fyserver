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
    private readonly PlayerLoginAccountService _playerAccounts;
    private const string SchemePrefix = "JWT ";

    public AuthService(UserStoreService users, CodecService codec, PlayerLoginAccountService playerAccounts)
    {
        _users = users;
        _codec = codec;
        _playerAccounts = playerAccounts;
    }

    public async Task<int> GetPlayerIdFromAuthAsync(HttpContext context)
    {
        var user = await GetUserFromAuthAsync(context);
        return user?.Id ?? 0;
    }

    public async Task<User?> GetUserFromAuthAsync(HttpContext context)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase))
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

        var user = await GetUserByIdentityAsync(userName);
        if (user?.Banned == true)
        {
            if (user.IsBanActive(DateTime.UtcNow)) return null;
            user.Banned = false;
            user.BanReason = "";
            user.BanExpiresAt = null;
            await _users.SaveUserAsync(user);
        }
        if (user != null)
            Console.WriteLine($"Authorization header found: {userName} (id={user.Id})");

        return user;
    }

    public Task<User?> GetUserByIdentityAsync(string identity)
    {
        if (_playerAccounts.TryGetPlayerId(identity, out var linkedId)) return _users.GetByIdAsync(linkedId);
        // Never reinterpret an invalid linked-account identity as a device username.
        if (identity.StartsWith("linker:", StringComparison.OrdinalIgnoreCase)) return Task.FromResult<User?>(null);
        return _users.GetByUserNameAsync(identity);
    }
}
