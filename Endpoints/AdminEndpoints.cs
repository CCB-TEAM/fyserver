using fyserver.Services;
using fyserver.Models;

namespace fyserver.Endpoints;

/// <summary>
/// 管理 API。封禁/踢出/删除统一走 AdminUserService，
/// 与 Razor 后台页面共用同一套流程（都会向 WebSocket 发送 disconnect 后关闭连接）。
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/users/count", async (UserStoreService users) =>
        {
            var allUsers = await users.GetAllUsersAsync();
            return Results.Ok(new CountResponseDto(allUsers.Count));
        });

        app.MapGet("/admin/users/list", async (UserStoreService users) =>
        {
            var allUsers = await users.GetAllUsersAsync();
            var simplifiedUsers = allUsers.Select(u => new UserSummaryDto(
                u.Id,
                u.UserName,
                u.Name,
                u.Tag,
                u.Decks.Count,
                u.Banned,
                u.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
            )).ToList();

            return Results.Ok(simplifiedUsers);
        });

        app.MapDelete("/admin/users/{userId}", async (int userId, AdminUserService adminUsers) =>
        {
            var (ok, message) = await adminUsers.DeleteAsync(userId);
            return ok ? Results.Ok(new MessageResponseDto(message)) : Results.NotFound(message);
        });

        app.MapPost("/admin/users/{userId}/ban", async (int userId, AdminUserService adminUsers) =>
        {
            var (ok, message) = await adminUsers.BanAsync(userId);
            return ok ? Results.Ok(new MessageResponseDto(message)) : Results.NotFound(message);
        });

        app.MapPost("/admin/users/{userId}/unban", async (int userId, AdminUserService adminUsers) =>
        {
            var (ok, message) = await adminUsers.UnbanAsync(userId);
            return ok ? Results.Ok(new MessageResponseDto(message)) : Results.NotFound(message);
        });

        app.MapPost("/admin/users/{userId}/kick", async (int userId, string? reason, AdminUserService adminUsers) =>
        {
            var (ok, message) = await adminUsers.KickAsync(userId, reason);
            return ok ? Results.Ok(new MessageResponseDto(message)) : Results.NotFound(message);
        });

        return app;
    }
}