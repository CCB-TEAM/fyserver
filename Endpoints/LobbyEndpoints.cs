using fyserver.Models;
using Microsoft.AspNetCore.Mvc;
using fyserver.Services;

namespace fyserver.Endpoints;

public static class LobbyEndpoints
{
    public static IEndpointRouteBuilder MapLobbyEndpoints(this IEndpointRouteBuilder app)
    {
        // 匹配系统（双人匹配/战斗码匹配等）
        app.MapPost("/lobbyplayers", async (LobbyPlayer lobbyPlayer, UserStoreService users, MatchManagerService matches, MatchHistoryService history, WebSocketHubService webSockets) =>
        {
            var validation = await ValidateLobbyPlayerAsync(lobbyPlayer, users, webSockets);
            if (validation != null)
                return validation;

            var abandoned = matches.GetActiveMatchForUser(lobbyPlayer.PlayerId);
            if (abandoned != null)
            {
                if (!string.IsNullOrEmpty(abandoned.WinnerSide)) await history.MarkCompletedAsync(abandoned);
                else await history.MarkAbortedAsync(abandoned);
            }
            matches.RemovePlayerFromAllQueues(lobbyPlayer.PlayerId);
            matches.RemovePlayerActiveMatches(lobbyPlayer.PlayerId, "requeue");
            matches.JoinLobby(lobbyPlayer, useAiOpponent: false);
            return Results.Text("OK");
        });

        app.MapPost("/singleplayerlobby", async (LobbyPlayer lobbyPlayer, UserStoreService users, MatchManagerService matches, MatchHistoryService history, WebSocketHubService webSockets) =>
        {
            var validation = await ValidateLobbyPlayerAsync(lobbyPlayer, users, webSockets);
            if (validation != null)
                return validation;

            var abandoned = matches.GetActiveMatchForUser(lobbyPlayer.PlayerId);
            if (abandoned != null)
            {
                if (!string.IsNullOrEmpty(abandoned.WinnerSide)) await history.MarkCompletedAsync(abandoned);
                else await history.MarkAbortedAsync(abandoned);
            }
            matches.RemovePlayerFromAllQueues(lobbyPlayer.PlayerId);
            matches.RemovePlayerActiveMatches(lobbyPlayer.PlayerId, "requeue");
            matches.JoinLobby(lobbyPlayer, useAiOpponent: true);
            return Results.Text("OK");
        });

        // 注意：这里显式标注 [FromBody]/[FromQuery]，不要依赖最小 API 的参数来源推断。
        // 暴露了模型绑定相关服务的 host（例如注册 Razor Pages 会引入 JSON 输入格式化器）时，
        // 对 DELETE 上的复杂类型做推断会得到 "Body was inferred but the method does not allow
        // inferred body parameters" 并在启动时直接抛异常。
        app.MapDelete("/lobbyplayers", ([FromBody] LobbyPlayer lobbyPlayer, MatchManagerService matches) =>
        {
            matches.RemovePlayerFromAllQueues(lobbyPlayer.PlayerId);
            return Results.Ok(new StatusResponseDto(200));
        });

        // 兼容：把 PlayerId 放在查询串上的退出请求（DELETE /lobbyplayers?playerId=123）
        app.MapDelete("/lobbyplayers/leave", ([FromQuery] int playerId, MatchManagerService matches) =>
        {
            matches.RemovePlayerFromAllQueues(playerId);
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

        if (user.IsBanActive(DateTime.UtcNow))
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
