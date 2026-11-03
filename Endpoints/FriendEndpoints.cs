using System.Text.Json;
using System.Text.Json.Nodes;
using fyserver.Services;

namespace fyserver.Endpoints;

/// <summary>游戏内好友系统接口（好友列表、申请/同意/删除、个人资料、好友对战邀请）。</summary>
public static class FriendEndpoints
{
    public static IEndpointRouteBuilder MapFriendEndpoints(this IEndpointRouteBuilder app)
    {
        // 部分客户端配置会在路径前多带一段（历史上的 /{a}/players/{id}/friends 路由），两种都接受。
        foreach (var pattern in new[] { "/players/{player_id}/friends", "/{prefix}/players/{player_id}/friends" })
        {
            app.MapGet(pattern, async (string player_id, HttpContext context, AuthService auth, FriendService friends) =>
            {
                var userId = await AuthorizeAsync(player_id, context, auth);
                if (userId <= 0) return Results.StatusCode(userId == 0 ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden);
                return Json(await friends.GetFriendsJsonAsync(userId));
            });

            app.MapPost(pattern, async (string player_id, HttpContext context, AuthService auth, FriendService friends) =>
            {
                var userId = await AuthorizeAsync(player_id, context, auth);
                if (userId <= 0) return Results.StatusCode(userId == 0 ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden);
                var body = await ReadBodyAsync(context);
                if (body == null) return Results.BadRequest("Invalid JSON");
                var result = await friends.AddOrAcceptAsync(userId, body);
                return result.Error != null
                    ? Results.Text(result.Error, "text/plain; charset=utf-8", statusCode: result.StatusCode)
                    : Json(new JsonObject { ["friend_id"] = result.FriendId }, StatusCodes.Status201Created);
            });

            app.MapDelete(pattern, async (string player_id, HttpContext context, AuthService auth, FriendService friends) =>
            {
                var userId = await AuthorizeAsync(player_id, context, auth);
                if (userId <= 0) return Results.StatusCode(userId == 0 ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden);
                var body = await ReadBodyAsync(context);
                var friendId = FriendChallengeService.Int(body?["friend_id"])
                               ?? (int.TryParse(context.Request.Query["friend_id"], out var q) ? q : 0);
                if (friendId <= 0) return Json(new JsonObject { ["error"] = "缺少 friend_id" }, StatusCodes.Status400BadRequest);
                var result = await friends.RemoveAsync(userId, friendId);
                return result.Error != null
                    ? Json(new JsonObject { ["error"] = result.Error }, result.StatusCode)
                    : Results.NoContent();
            });
        }

        app.MapGet("/players/{id:int}/profile", async (int id, FriendService friends) =>
            await friends.GetProfileJsonAsync(id) is { } profile
                ? Json(profile)
                : Json(new JsonObject { ["error"] = "Not Found" }, StatusCodes.Status404NotFound));

        // 好友对战邀请：路径里的 id 是被邀请者，from_player_id 是邀请者。
        app.MapPost("/players/notifications/{id:int}", async (int id, HttpContext context, AuthService auth, FriendChallengeService challenges) =>
        {
            var userId = await auth.GetPlayerIdFromAuthAsync(context);
            if (userId <= 0) return Results.Unauthorized();
            var body = await ReadBodyAsync(context);
            if (body == null) return Results.BadRequest();
            var result = await challenges.HandleAsync(userId, id, body);
            return result.Error != null
                ? Json(new JsonObject { ["error"] = result.Error }, result.StatusCode)
                : Results.StatusCode(StatusCodes.Status201Created);
        });

        app.MapGet("/players/notifications/{id}", () => Results.Text("[]", "application/json"));

        return app;
    }

    /// <returns>认证用户 id；0 表示未登录，-1 表示访问的不是自己的好友列表。</returns>
    private static async Task<int> AuthorizeAsync(string playerId, HttpContext context, AuthService auth)
    {
        var userId = await auth.GetPlayerIdFromAuthAsync(context);
        if (userId <= 0) return 0;
        return int.TryParse(playerId, out var id) && id == userId ? userId : -1;
    }

    private static async Task<JsonObject?> ReadBodyAsync(HttpContext context)
    {
        try
        {
            if (context.Request.ContentLength == 0) return null;
            return await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IResult Json(JsonObject body, int statusCode = StatusCodes.Status200OK) =>
        Results.Text(body.ToJsonString(), "application/json", statusCode: statusCode);
}
