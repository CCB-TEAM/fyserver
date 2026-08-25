using System.Text.Json;
using fyserver.Models;
using fyserver.Serialization;
using fyserver.Services;

namespace fyserver.Endpoints;

public static class MatchEndpoints
{
    public static IEndpointRouteBuilder MapMatchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/matches/v2", async (HttpContext context, AuthService auth, UserStoreService users, MatchManagerService matches, CodecService codec) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null)
                return Results.Unauthorized();
            Console.WriteLine("正在获取匹配信息，用户ID：" + user.Id);

            var match = matches.GetActiveMatchForUser(user.Id);
            if (match == null)
                return Results.Text("null");

            return Results.Ok(await matches.MakeMatchStartingInfo(user.Id, match));
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

        app.MapGet("/matches/v2/{id}", (int id) => Results.Text("running"));

        app.MapPut("/matches/v2/{id}/", async (int id, JsonElement payload, HttpContext context,
            AuthService auth, UserStoreService users, MatchManagerService matches, ServerOptions options) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null)
                return Results.Unauthorized();
            if (!matches.MatchedPairs.TryGetValue(id, out var match))
                return Results.NotFound($"Match with ID {id} not found");

            if (!matches.TryParseMatchActionPayload(payload, out var matchAction))
            {
                var rawPayload = payload.ValueKind == JsonValueKind.Undefined ? "<undefined>" : payload.GetRawText();
                Console.WriteLine($"无法解析 /matches/v2/{id} 请求体: {rawPayload}");
                return Results.Text("OK");
            }

            // 反作弊检查
            if (options.bancheat && matchAction.ActionType == GameConstants.XActionCheat)
            {
                // TODO: WebSocket 发送封禁消息
                user.Banned = true;
                await users.SaveUserAsync(user);
                match.WinnerSide = user.Id == match.Left?.PlayerId ? "right" : "left";
                return Results.Ok(new EmptyResponseDto());
            }

            if (matchAction.Action == "lvl-loaded")
                return Results.Ok(new OtherPlayerReadyDto(1));

            matches.TryApplyWinnerSide(match, matchAction, id, $"/matches/v2/{id}", payload);
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
                actions.Clear();
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
            AuthService auth, UserStoreService users, MatchManagerService matches, ServerOptions options) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null)
                return Results.Unauthorized();
            if (!matches.MatchedPairs.TryGetValue(id, out var match))
                return Results.NotFound($"Match with ID {id} not found");

            var matchAction = matches.DecryptMatchAction(matchActionen);
            Console.WriteLine(matchActionen.A);

            if (string.Equals(matchAction.ActionType, "XActionStartOfTurn", StringComparison.Ordinal))
            {
                Console.WriteLine("OK有个入开始了回合");
                match.Turns += 1;
            }

            // 反作弊检查
            if (options.bancheat && matchAction.ActionType == GameConstants.XActionCheat)
            {
                // TODO: WebSocket 发送封禁消息
                user.Banned = true;
                await users.SaveUserAsync(user);
                match.WinnerSide = user.Id == match.Left?.PlayerId ? "right" : "left";
                return Results.Ok(new EmptyResponseDto());
            }

            if (matchAction.Action == "lvl-loaded")
                return Results.Ok(new OtherPlayerReadyDto(1));

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

            List<MatchCard> deck, hand;
            if (user.Id == match.Left?.PlayerId)
            {
                deck = match.LeftDeck;
                hand = match.LeftHand;
                match.PlayerStatusLeft = GameConstants.MulliganDone;
            }
            else
            {
                deck = match.RightDeck;
                hand = match.RightHand;
                match.PlayerStatusRight = GameConstants.MulliganDone;
            }

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

        app.MapGet("/matches/v2/{id}/mulligan/{location}", (int id, string location, MatchManagerService matches) =>
        {
            if (!matches.MatchedPairs.TryGetValue(id, out var match))
                return Results.Text("null");

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
        app.MapGet("/matches/v2/{id}/post", async (int id, HttpContext context, AuthService auth, MatchManagerService matches) =>
        {
            var user = await auth.GetUserFromAuthAsync(context);
            if (user == null)
                return Results.Unauthorized();

            if (!matches.MatchedPairs.TryGetValue(id, out var match))
                return Results.NotFound($"Match with ID {id} not found");

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
}

