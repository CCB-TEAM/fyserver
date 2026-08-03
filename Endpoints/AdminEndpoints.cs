using fyserver.Services;

namespace fyserver.Endpoints;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/users/count", async (UserStoreService users) =>
        {
            var allUsers = await users.GetAllUsersAsync();
            return Results.Ok(new { count = allUsers.Count });
        });

        app.MapGet("/admin/users/list", async (UserStoreService users) =>
        {
            var allUsers = await users.GetAllUsersAsync();
            var simplifiedUsers = allUsers.Select(u => new
            {
                u.Id,
                u.UserName,
                u.Name,
                u.Tag,
                DeckCount = u.Decks.Count,
                u.Banned,
                CreatedAt = u.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
            }).ToList();

            return Results.Ok(simplifiedUsers);
        });

        app.MapDelete("/admin/users/{userId}", async (int userId, UserStoreService users) =>
        {
            var user = await users.GetByIdAsync(userId);
            if (user == null)
                return Results.NotFound($"User with ID {userId} not found");

            await users.DeleteUserAsync(userId);
            return Results.Ok(new { message = $"User {userId} deleted successfully" });
        });

        app.MapPost("/admin/users/{userId}/ban", async (int userId, UserStoreService users) =>
        {
            var user = await users.GetByIdAsync(userId);
            if (user == null)
                return Results.NotFound($"User with ID {userId} not found");

            user.Banned = true;
            await users.SaveUserAsync(user);
            return Results.Ok(new { message = $"User {userId} banned successfully" });
        });

        app.MapPost("/admin/users/{userId}/unban", async (int userId, UserStoreService users) =>
        {
            var user = await users.GetByIdAsync(userId);
            if (user == null)
                return Results.NotFound($"User with ID {userId} not found");

            user.Banned = false;
            await users.SaveUserAsync(user);
            return Results.Ok(new { message = $"User {userId} unbanned successfully" });
        });

        return app;
    }
}
