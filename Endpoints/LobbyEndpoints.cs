using fyserver.Models;
using fyserver.Services;

namespace fyserver.Endpoints;

public static class LobbyEndpoints
{
    public static IEndpointRouteBuilder MapLobbyEndpoints(this IEndpointRouteBuilder app)
    {
        // 匹配系统（双人匹配/战斗码匹配等）
        app.MapPost("/lobbyplayers", async (LobbyPlayer lobbyPlayer, HttpContext context, UserStoreService users, MatchManagerService matches) =>
        {
            var validation = await ValidateLobbyPlayerAsync(lobbyPlayer, context, users);
            if (validation != null)
                return validation;

            matches.RemovePlayerFromAllQueues(lobbyPlayer.PlayerId);
            matches.RemovePlayerActiveMatches(lobbyPlayer.PlayerId, "requeue");
            matches.JoinLobby(lobbyPlayer, useAiOpponent: false);
            return Results.Text("OK");
        });

        app.MapPost("/singleplayerlobby", async (LobbyPlayer lobbyPlayer, HttpContext context, UserStoreService users, MatchManagerService matches) =>
        {
            var validation = await ValidateLobbyPlayerAsync(lobbyPlayer, context, users);
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
    private static async Task<IResult?> ValidateLobbyPlayerAsync(LobbyPlayer lobbyPlayer, HttpContext context, UserStoreService users)
    {
        var user = await users.GetByIdAsync(lobbyPlayer.PlayerId);
        if (user == null)
        {
            // TODO: WebSocket 断开连接消息
            context.Connection.RequestClose();
            return Results.BadRequest("问号问号问号");
        }

        // 检查卡组有效性（简化）
        if (!user.Decks.TryGetValue(lobbyPlayer.DeckId, out _))
        {
            context.Connection.RequestClose();
            return Results.BadRequest("无效卡组");
        }

        return null;
    }
}
