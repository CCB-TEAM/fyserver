using fyserver.Models;
using fyserver.Services;

namespace fyserver.Endpoints;

public static class LobbyEndpoints
{
    public static IEndpointRouteBuilder MapLobbyEndpoints(this IEndpointRouteBuilder app)
    {
        // 匹配系统（双人匹配/战斗码匹配等）
        app.MapPost("/lobbyplayers", async (LobbyPlayer lobbyPlayer, UserStoreService users, MatchManagerService matches, WebSocketHubService webSockets) =>
        {
            var validation = await ValidateLobbyPlayerAsync(lobbyPlayer, users, webSockets);
            if (validation != null)
                return validation;

            matches.RemovePlayerFromAllQueues(lobbyPlayer.PlayerId);
            matches.RemovePlayerActiveMatches(lobbyPlayer.PlayerId, "requeue");
            matches.JoinLobby(lobbyPlayer, useAiOpponent: false);
            return Results.Text("OK");
        });

        app.MapPost("/singleplayerlobby", async (LobbyPlayer lobbyPlayer, UserStoreService users, MatchManagerService matches, WebSocketHubService webSockets) =>
        {
            var validation = await ValidateLobbyPlayerAsync(lobbyPlayer, users, webSockets);
            if (validation != null)
                return validation;

            matches.RemovePlayerFromAllQueues(lobbyPlayer.PlayerId);
            matches.RemovePlayerActiveMatches(lobbyPlayer.PlayerId, "requeue");
            matches.JoinLobby(lobbyPlayer, useAiOpponent: true);
            return Results.Text("OK");
        });

        app.MapDelete("/lobbyplayers", (LobbyPlayer lobbyPlayer, MatchManagerService matches) =>
        {
            matches.RemovePlayerFromAllQueues(lobbyPlayer.PlayerId);
            return Results.Ok(new StatusResponseDto(200));
        });

        return app;
    }

    /// <summary>校验玩家存在且卡组有效（原 handler 内联逻辑）。</summary>
    private static async Task<IResult?> ValidateLobbyPlayerAsync(
        LobbyPlayer lobbyPlayer,
        UserStoreService users,
        WebSocketHubService webSockets)
    {
        var user = await users.GetByIdAsync(lobbyPlayer.PlayerId);
        if (user == null)
        {
            await webSockets.DisconnectAsync(lobbyPlayer.PlayerId, "账户无效");
            return Results.BadRequest("账户无效");
        }

        if (user.Banned)
        {
            await webSockets.DisconnectAsync(user.Id, "该账户已被封禁");
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        // 检查卡组有效性（简化）
        if (!user.Decks.TryGetValue(lobbyPlayer.DeckId, out _))
        {
            await webSockets.DisconnectAsync(user.Id, "无效卡组");
            return Results.BadRequest("无效卡组");
        }

        return null;
    }
}
