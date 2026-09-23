using System.Text.Json;
using fyserver.Models;
using fyserver.Serialization;
using fyserver.Services;

namespace fyserver.Endpoints;

public static class MatchEndpoints
{
    public static IEndpointRouteBuilder MapMatchEndpoints(this IEndpointRouteBuilder app)
    {
        // KARDS replay entry point. Keep this literal route separate from /matches/v2/{id}:
        // otherwise "replay" is bound as an integer id and fails before a useful response is produced.
        app.MapPut("/matches/v2/replay", async (JsonElement payload, HttpContext context,
            AuthService auth, MatchHistoryService history, CodecService codec, ServerOptions options) =>
        {
            if (await auth.GetUserFromAuthAsync(context) == null)
                return Results.Unauthorized();
            if (payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("match_id", out var idNode) || !idNode.TryGetInt32(out var matchId) || matchId <= 0)
                return Results.Text("{\"error\":\"Invalid request\",\"message\":\"match_id must be a positive integer\",\"status_code\":400}", "application/json", statusCode: 400);
            if (!payload.TryGetProperty("pov_side", out var sideNode) || sideNode.ValueKind != JsonValueKind.String ||
                sideNode.GetString() is not ("left" or "right"))
                return Results.Text("{\"error\":\"Invalid request\",\"message\":\"pov_side must be left or right\",\"status_code\":400}", "application/json", statusCode: 400);

            var side = sideNode.GetString()!;
            var record = await history.GetAsync(matchId, context.RequestAborted);
            if (record?.Summary.Status != "completed" || record.StartingInfo == null)
                return Results.NotFound($"Replay with ID {matchId} not found");

            var actions = new List<MatchAction>();
            var cursor = 0;
            while (true)
            {
                var page = await history.GetActionsAsync(matchId, cursor, 1000, context.RequestAborted);
                if (page == null) return Results.NotFound($"Replay with ID {matchId} not found");
                actions.AddRange(page.Actions);
                if (!page.HasMore) break;
                if (page.NextActionId <= cursor)
                    return Results.Problem("Stored replay actions have an invalid cursor", statusCode: 500);
                cursor = page.NextActionId;
            }

            var starting = record.StartingInfo.MatchAndStartingData;
            var selectedPlayerId = side == "left" ? starting.StartingData.PlayerIdLeft : starting.StartingData.PlayerIdRight;
            var replayUrl = $"{options.GetAddressHttpR()}/replays/{matchId}";
            var replayActionsUrl = $"{replayUrl}/actions";
            var replay = new MatchReconnect
            {
                Actions = actions.OrderBy(x => x.ActionId).Select(x => codec.Encode(x)).ToArray(),
                LocalSubactions = record.StartingInfo.LocalSubactions,
                Match = starting.Match with
                {
                    ActionPlayerId = selectedPlayerId,
                    ActionSide = side,
                    Actions = [],
                    ActionsUrl = replayActionsUrl,
                    CurrentActionId = 0,
                    CurrentTurn = 1,
                    MatchId = matchId,
                    MatchUrl = replayUrl,
                    // Replay clients reject a live-match status; this payload represents an ended match.
                    Status = GameConstants.Finished,
                    WinnerId = record.Summary.WinnerSide == "left" ? record.Summary.LeftPlayerId :
                        record.Summary.WinnerSide == "right" ? record.Summary.RightPlayerId : 0,
                    WinnerSide = record.Summary.WinnerSide ?? ""
                },
                SameTurn = false,
                StartingData = starting.StartingData,
                TimeSinceStartOfTurn = -1,
                WaitingForSitNGoMatch = false
            };
            return Results.Json(replay, FyJsonContext.Default.MatchReconnect);
        });

        app.MapGet("/matches/v2", async (HttpContext context, AuthService auth, UserStoreService users, MatchManagerService matches, MatchHistoryService history, CodecService codec) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null)
                return Results.Unauthorized();
            Console.WriteLine("正在获取匹配信息，用户ID：" + user.Id);

            var match = matches.GetActiveMatchForUser(user.Id);
            if (match == null)
                return Results.Text("null");

            var startingInfo = await matches.MakeMatchStartingInfo(user.Id, match);
            await history.SaveSnapshotAsync(match, startingInfo, context.RequestAborted);
            return Results.Ok(startingInfo);
        });

        app.MapGet("/matches/v2/reconnect", async (HttpContext context, AuthService auth, MatchManagerService matches, CodecService codec) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null)
                return Results.Unauthorized();

            foreach (var i in matches.MatchedPairs)
            {
                MatchInfo mi = i.Value;
                if ((mi.WinnerSide != "left" && mi.WinnerSide != "right") &&
                    (mi.Left?.PlayerId == user.Id || mi.Right?.PlayerId == user.Id))
                {
                    if (mi.MatchStartingInfo == null)
                        continue;

                    MatchReconnect mr = new()
                    {
                        Actions = [],
                        LocalSubactions = true,
                        Match = mi.MatchStartingInfo.MatchAndStartingData.Match with
                        {
                            CurrentActionId = mi.currentActionId,
                            CurrentTurn = mi.Turns,
                            PlayerStatusLeft = mi.PlayerStatusLeft,
                            PlayerStatusRight = mi.PlayerStatusRight,
                            Status = "running"
                        },
                        MulliganLeft = mi.MulliganLeft,
                        MulliganRight = mi.MulliganRight,
                        SameTurn = false,
                        StartingData = mi.MatchStartingInfo.MatchAndStartingData.StartingData,
                        TimeSinceStartOfTurn = -1,
                        WaitingForSitNGoMatch = false
                    };
                    var actions = mi.GetActionsByMinActionId(0);
                    if (actions.Count > 0)
                    {
                        mr.Actions = actions.Select(x => codec.Encode(x)).ToArray();
                    }
                    return Results.Ok(mr);
                }
            }
            return Results.Ok("null");
        });

        app.MapGet("/matches/v2/{id}", async (int id, HttpContext context, AuthService auth, MatchManagerService matches) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null) return Results.Unauthorized();
            if (!matches.MatchedPairs.TryGetValue(id, out var match)) return Results.NotFound($"Match with ID {id} not found");
            return IsParticipant(match, user.Id) ? Results.Text("running") : Results.StatusCode(StatusCodes.Status403Forbidden);
        });

        app.MapPut("/matches/v2/{id}/", async (int id, JsonElement payload, HttpContext context,
            AuthService auth, UserStoreService users, MatchManagerService matches, MatchHistoryService history, ServerOptions options, WebSocketHubService webSockets) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null)
                return Results.Unauthorized();
            if (!matches.MatchedPairs.TryGetValue(id, out var match))
                return Results.NotFound($"Match with ID {id} not found");
            if (!IsParticipant(match, user.Id)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            if (!matches.TryParseMatchActionPayload(payload, out var matchAction))
            {
                var rawPayload = payload.ValueKind == JsonValueKind.Undefined ? "<undefined>" : payload.GetRawText();
                Console.WriteLine($"无法解析 /matches/v2/{id} 请求体: {rawPayload}");
                return Results.Text("OK");
            }

            // 反作弊检查
            if (options.bancheat && matchAction.ActionType == GameConstants.XActionCheat)
            {
                user.Banned = true;
                await users.SaveUserAsync(user);
                lock (match.SyncRoot) match.WinnerSide = user.Id == match.Left?.PlayerId ? "right" : "left";
                await history.MarkCompletedAsync(match, context.RequestAborted);
                await webSockets.DisconnectAsync(user.Id, "该账户已被封禁");
                return Results.Ok(new EmptyResponseDto());
            }

            if (matchAction.Action == "lvl-loaded")
                return Results.Ok(new OtherPlayerReadyDto(1));

            MatchAction[] recorded;
            lock (match.SyncRoot)
            {
                var priorCount = match.MatchActions.Count;
                matches.TryApplyWinnerSide(match, matchAction, id, $"/matches/v2/{id}", payload);
                if (!string.IsNullOrEmpty(matchAction.ActionType) || !string.IsNullOrEmpty(matchAction.Action))
                {
                    matchAction = matchAction with { ActionId = match.currentActionId, turn_number = match.Turns };
                    match.MatchActions.Add(matchAction);
                    match.currentActionId++;
                }
                recorded = match.MatchActions.Skip(priorCount).ToArray();
            }
            if (recorded.Length > 0) await history.AppendActionsAsync(match, recorded, context.RequestAborted);
            if (!string.IsNullOrEmpty(match.WinnerSide)) await history.MarkCompletedAsync(match, context.RequestAborted);
            return Results.Text("OK");
        });

        app.MapPut("/matches/v2/{id}/actions", async (int id, MatchPut matchPut, HttpContext context,
            AuthService auth, MatchManagerService matches, CodecService codec) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null)
                return Results.Unauthorized();

            if (!matches.MatchedPairs.TryGetValue(id, out var match))
                return Results.NotFound($"Match with ID {id} not found");
            if (!IsParticipant(match, user.Id)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var result = new Dictionary<string, object>();
            var actions = match.GetActionsByMinActionId(matchPut.MinActionId);
            Console.WriteLine($"正在获取动作，最小动作id：{matchPut.MinActionId}，动作数量：{actions.Count}");
            Console.ForegroundColor = ConsoleColor.Blue;
            foreach (var a in actions)
            {
                Console.WriteLine(a?.ToString());
            }
            Console.ForegroundColor = ConsoleColor.White;
            if (actions.Count > 0)
            {
                result["actions"] = actions.Select(x => codec.Encode(x)).ToArray();
            }

            result["match"] = new MatchPollDto(
                match.PlayerStatusLeft,
                // 单人对战
                string.Equals(match.Ex, "pw", StringComparison.Ordinal) ? "mulligan_done" : match.PlayerStatusRight,
                "running"
            );

            result["opponent_polling"] = true;

            if (!string.IsNullOrEmpty(match.WinnerSide))
            {
                result["match"] = new MatchPollDto(
                    GameConstants.EndMatch,
                    GameConstants.EndMatch,
                    GameConstants.Finished
                );
            }

            return Results.Ok(result);
        });

        app.MapGet("/config", () => Results.Ok(new ServerConfigDto(
            XserverClosed: "",
            XserverClosedHeader: "Server maintenance",
            ForgotPasswordUrl: "https://pornhub.com"
        )));

        app.MapPost("/matches/v2/{id}/actions", async (int id, MatchActionEn matchActionen, HttpContext context,
            AuthService auth, UserStoreService users, MatchManagerService matches, MatchHistoryService history, ServerOptions options, WebSocketHubService webSockets) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null)
                return Results.Unauthorized();
            if (!matches.MatchedPairs.TryGetValue(id, out var match))
                return Results.NotFound($"Match with ID {id} not found");
            if (!IsParticipant(match, user.Id)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var matchAction = matches.DecryptMatchAction(matchActionen);
            Console.WriteLine(matchActionen.A);

            // 反作弊检查
            if (options.bancheat && matchAction.ActionType == GameConstants.XActionCheat)
            {
                user.Banned = true;
                await users.SaveUserAsync(user);
                lock (match.SyncRoot) match.WinnerSide = user.Id == match.Left?.PlayerId ? "right" : "left";
                await history.MarkCompletedAsync(match, context.RequestAborted);
                await webSockets.DisconnectAsync(user.Id, "该账户已被封禁");
                return Results.Ok(new EmptyResponseDto());
            }

            if (matchAction.Action == "lvl-loaded")
                return Results.Ok(new OtherPlayerReadyDto(1));

            MatchAction[] recordedActions;
            lock (match.SyncRoot)
            {
                if (string.Equals(matchAction.ActionType, "XActionStartOfTurn", StringComparison.Ordinal))
                {
                    Console.WriteLine("OK有个入开始了回合");
                    match.Turns += 1;
                }
                var priorCount = match.MatchActions.Count;
                matches.TryApplyWinnerSide(match, matchAction, id, $"/matches/v2/{id}/actions");
                if (!string.IsNullOrEmpty(matchAction.ActionType) || !string.IsNullOrEmpty(matchAction.Action))
                {
                    if (matchAction.sub_actions != null)
                        matchAction = matchAction with { ActionId = match.currentActionId, turn_number = match.Turns };
                    else
                        matchAction = matchAction with { ActionId = match.currentActionId, turn_number = match.Turns, sub_actions = [] };
                    match.MatchActions.Add(matchAction);
                    match.currentActionId++;
                }

                if (string.Equals(matchAction.ActionType, "XActionEndOfTurn", StringComparison.Ordinal) && string.Equals(match.Ex, "pw", StringComparison.Ordinal))
                {
                    Console.WriteLine("OK有个入开始了回合");
                    match.Turns += 1;
                    match.MatchActions.Add(new MatchAction(match.currentActionId, "XActionStartOfTurn", -9178, new()
                    {
                        { "side", "right" },
                        { "75", "20" },
                    }, [], match.Turns, SendActionId: matchAction.SendActionId));
                    match.currentActionId++;
                    match.MatchActions.Add(new MatchAction(match.currentActionId, "XActionEndOfTurn", -9178, new()
                    {
                        { "side", "right" },
                        { "75", "20" },
                    }, [], match.Turns, SendActionId: matchAction.SendActionId));
                    match.currentActionId++;
                }
                recordedActions = match.MatchActions.Skip(priorCount).ToArray();
            }

            if (recordedActions.Length > 0)
                await history.AppendActionsAsync(match, recordedActions, context.RequestAborted);
            if (!string.IsNullOrEmpty(match.WinnerSide)) await history.MarkCompletedAsync(match, context.RequestAborted);

            return Results.Text("OK");
        });

        // 调度阶段
        app.MapPost("/matches/v2/{id}/mulligan", async (int id, MulliganCards mulliganCards, HttpContext context,
            AuthService auth, MatchManagerService matches) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null)
                return Results.Unauthorized();

            if (!matches.MatchedPairs.TryGetValue(id, out var match))
                return Results.NotFound($"Match with ID {id} not found");
            if (!IsParticipant(match, user.Id)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            List<MatchCard> deck, hand;
            if (user.Id == match.Left?.PlayerId)
            {
                deck = match.LeftDeck;
                hand = match.LeftHand;
                match.PlayerStatusLeft = GameConstants.MulliganDone;
            }
            else if (user.Id == match.Right?.PlayerId)
            {
                deck = match.RightDeck;
                hand = match.RightHand;
                match.PlayerStatusRight = GameConstants.MulliganDone;
            }
            else return Results.StatusCode(StatusCodes.Status403Forbidden);

            var result = new MulliganResult(
                Deck: deck,
                ReplacementCards: new List<MatchCard>()
            );

            foreach (var id2 in mulliganCards.DiscardedCardIds)
            {
                // 1. 定位手牌
                int handIndex = hand.FindIndex(c => c.CardId == id2);
                if (handIndex == -1) continue;
                var cardInHand = hand[handIndex];
                // 2. 随机选取牌库中的一张牌
                int deckIndex = Random.Shared.Next(result.Deck.Count);
                var cardInDeck = result.Deck[deckIndex];
                // 3. 使用 'with' 交换位置信息 (Location 和 LocationNumber)
                // 产生一张 “带着旧手牌位置信息” 的新手牌
                var newHandCard = cardInDeck with
                {
                    Location = cardInHand.Location,
                    LocationNumber = cardInHand.LocationNumber
                };
                // 产生一张 “带着旧牌库位置信息” 的旧手牌
                var newDeckCard = cardInHand with
                {
                    Location = cardInDeck.Location,
                    LocationNumber = cardInDeck.LocationNumber
                };
                result.ReplacementCards.Add(newHandCard); // 这张牌将进入玩家手牌
                result.Deck[deckIndex] = newDeckCard; // 旧牌被洗回牌库对应位置
                hand[handIndex] = newHandCard; // 同步内存中的手牌状态，避免后续状态不一致
            }

            // 保存该侧的 mulligan 结果，供对手通过 /mulligan/{location} 拉取
            var snapshot = new MulliganResult(
                Deck: result.Deck.ToList(),
                ReplacementCards: result.ReplacementCards.ToList()
            );
            if (user.Id == match.Left?.PlayerId)
                match.MulliganLeft = snapshot;
            else
                match.MulliganRight = snapshot;

            return Results.Ok(result);
        });

        app.MapGet("/matches/v2/{id}/mulligan/{location}", async (int id, string location, HttpContext context, AuthService auth, MatchManagerService matches) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null) return Results.Unauthorized();
            if (!matches.MatchedPairs.TryGetValue(id, out var match))
                return Results.Text("null");
            if (!IsParticipant(match, user.Id)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (location is not ("left" or "right")) return Results.BadRequest();

            var mulligan = location == "left" ? match.MulliganLeft : match.MulliganRight;
            if (string.Equals(match.Ex, "pw", StringComparison.Ordinal))
            {
                // 单人
                mulligan = new MulliganResult(
                    Deck: match.RightDeck,
                    ReplacementCards: new List<MatchCard>()
                );
            }

            return mulligan == null ? Results.Text("null") : Results.Ok(mulligan);
        });

        // 比赛结束
        app.MapGet("/matches/v2/{id}/post", async (int id, HttpContext context, AuthService auth, MatchManagerService matches, MatchHistoryService history) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null)
                return Results.Unauthorized();

            if (!matches.MatchedPairs.TryGetValue(id, out var match))
                return Results.NotFound($"Match with ID {id} not found");
            if (!IsParticipant(match, user.Id)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            if (MatchManagerService.IsSoloMatch(match))
            {
                match.PlayerStatusLeft = GameConstants.EndMatch;
                match.PlayerStatusRight = GameConstants.EndMatch;
            }
            else if (user.Id == match.Left?.PlayerId)
            {
                match.PlayerStatusLeft = GameConstants.EndMatch;
            }
            else
            {
                match.PlayerStatusRight = GameConstants.EndMatch;
            }

            if (MatchManagerService.CanRemoveMatch(match))
            {
                await history.MarkCompletedAsync(match, context.RequestAborted);
                matches.ClearMatchRuntimeState(match);
                matches.MatchedPairs.TryRemove(id, out _);
            }

            var player = match.GetPlayerById(user.Id);
            var isWinner = match.WinnerSide == (user.Id == match.Left?.PlayerId ? "left" : "right");

            if (player != null && user.Decks.TryGetValue(player.DeckId, out var deck))
            {
                var response = new PostMatchResponse(
                    Faction: deck.MainFaction,
                    Winner: isWinner
                );
                Console.WriteLine(JsonSerializer.Serialize(response, FyJsonContext.Default.PostMatchResponse));
                return Results.Ok(response);
            }

            return Results.NotFound();
        });

        return app;
    }

    private static bool IsParticipant(MatchInfo match, int userId) => match.Left?.PlayerId == userId || match.Right?.PlayerId == userId;
}

