using fyserver.Models;
using fyserver.Serialization;
using fyserver.Services;

namespace fyserver.Endpoints;

public static class MatchHistoryEndpoints
{
    public static IEndpointRouteBuilder MapMatchHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        // 公开只读接口：供游戏端观战与未来官网展示近期对局使用，不返回 Authorization/JWT 等凭据。
        app.MapGet("/replays", async (int? limit, int? playerId, MatchHistoryService history, CancellationToken cancellationToken) =>
        {
            var rows = (await history.ListRecentAsync(limit ?? 50, playerId: playerId, cancellationToken: cancellationToken)).ToList();
            return Results.Json(new MatchHistoryListResponse(rows), FyJsonContext.Default.MatchHistoryListResponse);
        });

        app.MapGet("/replays/{id:int}", async (int id, MatchHistoryService history, CancellationToken cancellationToken) =>
        {
            var record = await history.GetAsync(id, cancellationToken);
            if (record?.Summary.Status != "completed") return Results.NotFound();
            return Results.Json(new MatchHistoryDetailResponse(record.Summary, record.StartingInfo), FyJsonContext.Default.MatchHistoryDetailResponse);
        });

        app.MapGet("/replays/{id:int}/actions", async (int id, int? afterActionId, int? limit, MatchHistoryService history, CancellationToken cancellationToken) =>
        {
            var record = await history.GetAsync(id, cancellationToken);
            if (record?.Summary.Status != "completed") return Results.NotFound();
            var page = await history.GetActionsAsync(id, afterActionId ?? 0, limit ?? 500, cancellationToken);
            return page == null ? Results.NotFound() : Results.Json(page, FyJsonContext.Default.MatchHistoryActionPage);
        });

        app.MapGet("/spectate/matches", (MatchManagerService matches) =>
        {
            var response = new MatchHistoryListResponse(matches.GetActiveRealMatches().Where(x => !x.IsCompleted).Select(MatchHistoryService.CreateSummary).ToList());
            return Results.Json(response, FyJsonContext.Default.MatchHistoryListResponse);
        });

        app.MapGet("/spectate/matches/{id:int}", (int id, MatchManagerService matches) =>
        {
            var match = matches.GetMatch(id);
            if (match?.MatchStartingInfo == null || match.IsCompleted) return Results.NotFound();
            return Results.Json(new MatchHistoryDetailResponse(MatchHistoryService.CreateSummary(match), match.MatchStartingInfo), FyJsonContext.Default.MatchHistoryDetailResponse);
        });

        app.MapGet("/spectate/matches/{id:int}/actions", (int id, int? afterActionId, int? limit, MatchManagerService matches) =>
        {
            var match = matches.GetMatch(id);
            if (match == null || match.IsCompleted) return Results.NotFound();
            var take = Math.Clamp(limit ?? 500, 1, 1000);
            var actions = match.SnapshotActions().Where(x => x.ActionId > (afterActionId ?? 0)).OrderBy(x => x.ActionId).Take(take + 1).ToList();
            var hasMore = actions.Count > take;
            if (hasMore) actions.RemoveAt(actions.Count - 1);
            var next = actions.Count == 0 ? afterActionId ?? 0 : actions[^1].ActionId;
            var page = new MatchHistoryActionPage(id, next, hasMore, actions);
            return Results.Json(page, FyJsonContext.Default.MatchHistoryActionPage);
        });

        return app;
    }
}
