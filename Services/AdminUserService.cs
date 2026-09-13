using fyserver.Models;

namespace fyserver.Services;

/// <summary>
/// 后台用户管理操作。统一走与 /admin/users/* API 相同的封禁/踢出/删除流程，
/// 保证后台页面与 HTTP API 行为一致（封禁与踢出都会向 WebSocket 发送 disconnect 并关闭连接）。
/// </summary>
public class AdminUserService(UserStoreService users, WebSocketHubService webSockets)
{
    public Task<List<User>> ListAsync() => users.GetAllUsersAsync();

    public Task<User?> GetAsync(int userId) => users.GetByIdAsync(userId);

    /// <summary>封禁并断开该用户当前连接。</summary>
    public async Task<(bool ok, string message)> BanAsync(int userId)
    {
        var user = await users.GetByIdAsync(userId);
        if (user == null)
            return (false, $"用户 {userId} 不存在");

        user.Banned = true;
        user.UpdatedAt = DateTime.UtcNow;
        await users.SaveUserAsync(user);
        await webSockets.DisconnectAsync(userId, "该账户已被封禁");
        return (true, $"已封禁 {user.UserName}（{userId}）");
    }

    /// <summary>解除封禁；不主动关闭连接（该用户可重新登录）。</summary>
    public async Task<(bool ok, string message)> UnbanAsync(int userId)
    {
        var user = await users.GetByIdAsync(userId);
        if (user == null)
            return (false, $"用户 {userId} 不存在");

        user.Banned = false;
        user.UpdatedAt = DateTime.UtcNow;
        await users.SaveUserAsync(user);
        return (true, $"已解封 {user.UserName}（{userId}）");
    }

    /// <summary>踢出在线用户：发送 disconnect 后关闭 WebSocket，不封禁账户。</summary>
    public async Task<(bool ok, string message)> KickAsync(int userId, string? reason)
    {
        var user = await users.GetByIdAsync(userId);
        if (user == null)
            return (false, $"用户 {userId} 不存在");

        var message = string.IsNullOrWhiteSpace(reason) ? "您已被服务器断开连接" : reason.Trim();
        var disconnected = await webSockets.DisconnectAsync(userId, message);
        return disconnected
            ? (true, $"已踢出 {user.UserName}（{userId}）")
            : (true, $"{user.UserName}（{userId}）当前不在线");
    }

    /// <summary>删除用户并断开其连接。</summary>
    public async Task<(bool ok, string message)> DeleteAsync(int userId)
    {
        var user = await users.GetByIdAsync(userId);
        if (user == null)
            return (false, $"用户 {userId} 不存在");

        await webSockets.DisconnectAsync(userId, "该账户已被删除");
        await users.DeleteUserAsync(userId);
        return (true, $"已删除 {user.UserName}（{userId}）");
    }
}
