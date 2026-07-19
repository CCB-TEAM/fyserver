using Microsoft.AspNetCore.Mvc;
using System;
using System.Buffers.Text;
using System.Diagnostics;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace fyserver
{
    public class http
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        public static async Task<int> GetPlayerIdFromAuthAsync(HttpContext context)
        {
            var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
            if (authHeader == null)
            {
                authHeader = context.Request.Headers["authorization"].FirstOrDefault();
            }
            User? a = null;
            if (authHeader != null && authHeader.StartsWith("JWT "))
                a = await GlobalState.users.GetByUserNameAsync(Decode(authHeader["JWT ".Length..], out var actionId));
            else
                return 0;
            if (a != null)
            {
                Console.WriteLine($"Authorization header found: {Decode(authHeader["JWT ".Length..], out var actionId)}");
                return a.Id;
            }
            return 0;
        }
        public static async Task<User?> GetUserFromAuthAsync(HttpContext context)
        {
            var playerId = await GetPlayerIdFromAuthAsync(context);
            Console.WriteLine($"Extracted player ID from auth: {playerId}");
            if (playerId > 0)
            {
                // 纯粹的数据获取，不需要注入 UserStore
                return await GlobalState.users.GetByIdAsync(playerId);
            }
            return null;
        }
        [DllImport("codec.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int _xR7qM2vP(string plaintext, int actionId, StringBuilder output, int outputBufSize);
        [DllImport("codec.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int _kW3nJ9tF(string encoded, StringBuilder plaintextOut, int plaintextBufSize, ref int actionIdOut);
        public static string Encode(string plaintext, int actionId)
        {
            StringBuilder sb = new StringBuilder(plaintext.Length * 4 + 256);
            int len = _xR7qM2vP(plaintext, actionId, sb, sb.Capacity);
            if (len < 0)
                throw new Exception("编码失败");
            return sb.ToString();
        }
        public static string Encode<T>(T abc) where T : MatchAction
        {
            var plaintext = JsonSerializer.Serialize(abc, GameConstants.JsonOptions);
            StringBuilder sb = new StringBuilder(plaintext.Length * 4 + 256);
            int len = _xR7qM2vP(plaintext, abc?.SendActionId ?? 0, sb, sb.Capacity);
            if (len < 0)
                throw new Exception("编码失败");
            return sb.ToString();
        }
        /*
        public static string Encode(ServerMatchAction sma)
        {
            var plaintext = JsonSerializer.Serialize(sma, GameConstants.JsonOptions);
            StringBuilder sb = new StringBuilder(plaintext.Length * 4 + 256);
            int len = _xR7qM2vP(plaintext, sma.ActionId, sb, sb.Capacity);
            if (len < 0)
                throw new Exception("编码失败");
            return sb.ToString();
        }
        */
        public static string Decode(string encoded, out int actionId)
        {
            StringBuilder sb = new StringBuilder(encoded.Length + 256);
            actionId = 0;
            int len = _kW3nJ9tF(encoded, sb, sb.Capacity, ref actionId);
            if (len < 0)
                throw new Exception("解码失败");
            return sb.ToString();
        }
        async public Task StartHttpServer()
        {
            PlayerLibrary.InitLibrary("./library/deckCodeIDsTable2.json", "./library/emojiLib.json", "./library/cardbackLib.json");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("XXXXXXXXXX");
            Console.ForegroundColor = ConsoleColor.White;
            //builder.Services.AddOpenApi();
            builder.WebHost.UseUrls(config.appconfig.getAddressHttp());
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
            {
                options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            });
            builder.Services.AddRouting();
            var app = builder.Build();
            if (app.Environment.IsDevelopment())
            {
                //app.MapOpenApi();
            }
            void UseMiddleWares()
            {
                app.Use(async (context, next) =>
                {
                    var path = context.Request.Path.Value;
                    if (!string.IsNullOrEmpty(path) && path.Contains("//"))
                    {
                        while (path.Contains("//"))
                        {
                            path = path.Replace("//", "/");
                        }
                        context.Request.Path = path;
                    }
                    await next();
                });
                app.UseRouting();

                app.Use(async (context, next) =>
                {
                    context.Response.OnStarting(() =>
                    {
                        context.Response.Headers["Content-Type"] = context.Response.Headers["Content-Type"].ToString().Replace("; charset=utf-8", "");
                        return Task.CompletedTask;
                    });
                    await next();
                });
            }
            // 在 http.cs 中
            // 2. 配置和基本信息
            void MakeUserEndpoints()
            {
                app.MapPost("/session", async (Session session) =>
                {
                    string addressHttp = config.appconfig.getAddressHttpR();
                    User? user = null;
                    try
                    {
                        user = await GlobalState.users.GetByUserNameAsync(session.Username);
                        Console.WriteLine($"Find user: {session.Username}");
                    }
                    catch (Exception)
                    {
                    }
                    if (user == null)
                    {
                        Console.WriteLine("未找到");
                        try
                        {
                            user = await GlobalState.users.CreateUserAsync(session.Username);
                            Console.WriteLine($"Created new user: {session.Username}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.InnerException.Message);
                            // 用户已存在或其他错误
                            return Results.BadRequest(ex.Message);
                        }
                    }

                    if (user.Banned)
                    {
                        return Results.Json(
                            new BannedResponse(
                                new Error("user_error", "banned"),
                                "Forbidden",
                                403
                            ),
                            statusCode: 403
                        );
                    }
                    // 设置用户存储服务
                    //

                    var response = new SessionResponse(
                        AchievementsUrl: $"{addressHttp}/players/{user.Id}/achievements",
                        AllKnockoutTourneys: new List<object>(),
                        BritainLevel: 500,
                        BritainLevelClaimed: 500,
                        BritainXp: 0,
                        CardsBlacklist: new List<object>(),
                        ClaimableCrateLevel: 0,
                        ClientId: user.Id,
                        Currency: "USD",
                        CurrentKnockoutTourney: new Dictionary<string, object>(),
                        CurrentMiniSitNGo: new(
                            "2026-05-01T18:00:00.000000Z",
                            false,
                            114514,
                            "Skirmish #91",
                            File.Exists("./config/current_mini_sit_n_go.json") ? File.ReadAllText("./config/current_mini_sit_n_go.json") : "",
                            "2026-03-27T12:00:00.000000Z"
                        ),
                        DailymissionsUrl: $"{addressHttp}/players/{user.Id}/dailymissions",
                        Decks: new Dictionary<string, object>
                        {
                            ["headers"] = user.Decks.Values.Select(d => new
                            {
                                d.Name,
                                d.MainFaction,
                                d.AllyFaction,
                                d.CardBack,
                                d.DeckCode,
                                d.Favorite,
                                d.Id,
                                d.PlayerId,
                                LastPlayed = d.LastPlayed.ToString("o"),
                                CreateDate = d.CreateDate.ToString("o"),
                                ModifyDate = d.ModifyDate.ToString("o")
                            }).ToList()
                        },
                        DecksUrl: $"{addressHttp}/players/{user.Id}/decks",
                        Diamonds: 99999,
                        DoubleXpEndDate: "2025-07-03T12:13:36.889692Z",
                        DraftAdmissions: 1,
                        Dust: 1000,
                        Email: null,
                        EmailRewardReceived: false,
                        EmailVerified: false,
                        ExtendedRewards: false,
                        GermanyLevel: 500,
                        GermanyLevelClaimed: 500,
                        GermanyXp: 0,
                        Gold: 999999,
                        HasBeenOfficer: true,
                        HeartbeatUrl: $"{addressHttp}/players/{user.Id}/heartbeat",
                        IsOfficer: true,
                        IsOnline: true,
                        JapanLevel: 500,
                        JapanLevelClaimed: 500,
                        JapanXp: 0,
                        Jti: "114514",
                        Jwt: $"{Encode(session.Username, 114)}",
                        LastCrateClaimedDate: "2025-07-02T11:24:15.567042Z",
                        LastDailyMissionCancel: null,
                        LastDailyMissionRenewal: "2025-07-05T15:21:43.653915Z",
                        LastLogonDate: "2025-07-05T15:21:06.168847Z",
                        LaunchMessages: new List<object>(),
                        LibraryUrl: $"{addressHttp}/players/{user.Id}/library",
                        LinkerAccount: "",
                        Locale: "zh-hans",
                        Misc: new Dictionary<string, object>
                        {
                            ["createDate"] = "2025-07-02T11:24:15.529671Z",
                            ["featuredAchievements"] = new List<object>()
                        },
                        NewCards: new List<object>(),
                        NewPlayerLoginReward: new Dictionary<string, object>
                        {
                            ["day"] = 8,
                            ["reset"] = "0001-01-01 00:00:00",
                            ["seconds"] = 0
                        },
                        Npc: false,
                        OnlineFlag: true,
                        PacksUrl: $"{addressHttp}/players/{user.Id}/packs",
                        PlayerId: user.Id,
                        PlayerName: user.Name,
                        PlayerTag: user.Tag.ToString(),
                        Rewards: new List<object>(),
                        SeasonEnd: "2025-08-01T00:00:00Z",
                        SeasonWins: 9999,
                        ServerOptions: File.Exists("./config/serverOptions.json") ? File.ReadAllText("./config/serverOptions.json").Replace("{WsAddress}", config.appconfig.getAddressWsR()) : "",//'{"nui_mobile": 1, "scalability_override": {"Android_Low": {"console_commands": ["r.Screenpercentage 100"]}, "Android_Mid": {"console_commands": ["r.Screenpercentage 100"]}, "Android_High": {"console_commands": ["r.Screenpercentage 100"]}}, "appscale_desktop_default": 1.0, "appscale_desktop_max": 1.4, "appscale_mobile_default": 1.4, "appscale_mobile_max": 1.4, "appscale_mobile_min": 1.0, "appscale_tablet_min": 1.0, "battle_wait_time": 60, "nui_mobile": 1, "scalability_override": {"Android_Low": {"console_commands": ["r.Screenpercentage 100"]}, "Android_Mid": {"console_commands": ["r.Screenpercentage 100"]}, "Android_High": {"console_commands": ["r.Screenpercentage 100"]}},"websocketurl": "ws://127.0.0.1:5232","homefront_date":"2025.11.27-09.00.00"}',
                        ServerTime: DateTime.UtcNow.ToString("yyyy.MM.dd-HH.mm.ss"),
                        SovietLevel: 500,
                        SovietLevelClaimed: 500,
                        SovietXp: 0,
                        Stars: 120,
                        TutorialsDone: 0,
                        TutorialsFinished: new List<string>
                        {
                "unlocking_germany_1",
                "unlocking_germany_2",
                "unlocking_germany_0",
                "germany_cards_rewarded",
                "unlocking_usa_8",
                "recruit_missions_done",
                "draft_1",
                "draft_ally",
                "draft_kredits",
                "unlocking_japan_0",
                "japan_cards_rewarded",
                "unlocking_soviet_0",
                "soviet_cards_rewarded",
                "unlocking_usa_0",
                "usa_cards_rewarded",
                "unlocking_britain_0",
                "britain_cards_rewarded"
                        },
                        UsaLevel: 500,
                        UsaLevelClaimed: 500,
                        UsaXp: 0,
                        UserId: user.Id
                    );
                    return Results.Ok(response);
                });
                app.MapGet("/", async (HttpContext context) =>
                {
                    var auth = (context.Request.Headers["Authorization"].FirstOrDefault());
                    // Console.WriteLine(auth);
                    if (auth == null)
                    {
                        auth = "JWT 1939Mother";
                    }
                    string userName = "1939Mother";
                    try
                    {
                        userName = Decode(auth["JWT ".Length..], out var a);
                    }
                    catch
                    {
                        if (auth is not "JWT 1939Mother")
                            return Results.BadRequest("Invalid Authorization header");
                    }
                    User? user = await GlobalState.users.GetByUserNameAsync(userName);
                    if (user == null)
                        try
                        {
                            user = await GlobalState.users.CreateUserAsync(userName);
                            Console.WriteLine($"Created new user: {userName}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.InnerException.Message);
                            // 用户已存在或其他错误
                            return Results.BadRequest(ex.Message);
                        }
                    return Results.Ok(await config.appconfig.getConfigAsync(user));
                });
            }
            // 3. 玩家管理
            void MakePlayerEndpoints()
            {
                //TODO: 完善entitlements接口,用于处理所有权
                // FP接口 - 前端展示（Front Page）
                app.MapGet("/fp/", (HttpContext context) =>
                {
                    var fpt = File.Exists("./config/frontpage.json") ? File.ReadAllText("./config/frontpage.json") : "{}";
                    JsonDocument fp1 = JsonDocument.Parse(fpt);
                    return Results.Ok(
                        fp1
                        );
                });
                // Store接口 - 商店数据
                StoreResponse BuildStoreResponse()
                {
                    var storeConfig = GlobalState.GetStoreConfig();
                    var now = DateTime.UtcNow;
                    var timestamp = (now - new DateTime(1970, 1, 1)).TotalSeconds;
                    return new StoreResponse(
                        Currency: storeConfig.Currency,
                        Groups: storeConfig.Groups,
                        AlwaysFeatured: storeConfig.AlwaysFeatured,
                        Message: $"Offers for {now:yyyy-MM-ddTHH:mm:ss.ffffffZ}",
                        Status: 200,
                        Ts: timestamp
                    );
                }
                app.MapGet("/store/v2/", (HttpContext context, string? provider) =>
                {
                    return Results.Ok(BuildStoreResponse());
                });
                // 兼容旧客户端：部分版本会请求 /store/ 和 /store/txn
                app.MapGet("/store/", () =>
                {
                    return Results.Ok(BuildStoreResponse());
                });
                app.MapPost("/store/v2/txn", () =>
                {
                    return Results.Ok();
                });
                app.MapPost("/store/txn", () =>
                {
                    return Results.Ok();
                });
                app.MapGet("/entitlements/{id}", (HttpContext context) =>
                {
                    List<Entitlement> e = new();
                    e.Add(new Entitlement
                    (
                        EntitlementType: "emote",
                        Name: "emote_appreciate"
                    ));
                    return Results.Ok(e);
                });
                app.MapGet("/{a}/players/{player_id}/friends", async (HttpContext context) =>
                {
                    List<int> nil = new();
                    return Results.Ok(new FriendsReponse(Friends: nil, PreviousOpponents: nil));
                });
                app.MapMethods("/players/{id}/heartbeat", new[] { "PUT", "DELETE" }, (string id) =>
                {
                    return Results.Ok(new { });
                });
                app.MapMethods("/players/notifications/{id}", new[] { "PUT", "DELETE" }, (string id) =>
                {
                    return Results.Ok(new { });
                });
                // 4. 卡牌库和卡组
                app.MapGet("/players/{id}/librarynew", (string id) =>
                {
                    return Results.Ok(PlayerLibrary.Library);
                });
                app.MapGet("/players/{id}/library", (string id) =>
                {
                    return Results.Ok(PlayerLibrary.Library);
                });
            }
            void MakeDecksEndpoints()
            {
                app.MapPost("/players/{id}/decks", async (string id, CreateDeck createDeck) =>
                {
                    var user = await GlobalState.users.GetByIdAsync(int.Parse(id));
                    if (user == null)
                        return Results.NotFound($"User with ID {id} not found");
                    var deck = new Deck(createDeck, user.Id);
                    user.Decks[deck.Id] = deck;

                    await GlobalState.users.SaveUserAsync(user);

                    return Results.Ok(new
                    {
                        deck.Id,
                        deck.Name,
                        deck.MainFaction,
                        deck.AllyFaction,
                        deck.CardBack,
                        deck.DeckCode,
                        deck.Favorite,
                        deck.PlayerId,
                        LastPlayed = deck.LastPlayed.ToString("o"),
                        CreateDate = deck.CreateDate.ToString("o"),
                        ModifyDate = deck.ModifyDate.ToString("o")
                    });
                });

                app.MapPut("/players/{player_id}/decks/{deck_id}", async (string player_id, int deck_id, [FromBody] DeckAction action) =>
                {
                    var user = await GlobalState.users.GetByIdAsync(int.Parse(player_id));
                    if (user == null)
                        return Results.NotFound($"User with ID {player_id} not found");
                    if (user.Decks.TryGetValue(deck_id, out var deck))
                    {
                        Console.WriteLine(user.Decks[deck_id].Name);
                        Console.WriteLine(action.ToString());
                        switch (action.Action)
                        {
                            case "fill":
                                Console.WriteLine(deck.Name);
                                Console.WriteLine(action.DeckCode);
                                Console.WriteLine(action.ToString());
                                deck.DeckCode = action.DeckCode;
                                deck.ModifyDate = DateTime.Now;
                                break;
                        }
                        await GlobalState.users.SaveUserAsync(user);
                    }
                    Console.WriteLine((await GlobalState.users.GetByIdAsync(int.Parse(player_id))).Decks[deck_id].DeckCode);
                    return Results.Ok(new { });
                });

                app.MapPut("/players/{player_id}/decks/", async (string player_id, ChangeDeck changeDeck) =>
                {
                    var user = await GlobalState.users.GetByIdAsync(int.Parse(player_id));
                    if (user == null)
                        return Results.NotFound($"User with ID {player_id} not found");
                    if (user.Decks.TryGetValue(changeDeck.Id, out var deck))
                    {
                        switch (changeDeck.Action)
                        {
                            case "rename":
                                deck.Name = changeDeck.Name;
                                deck.ModifyDate = DateTime.Now;
                                break;
                            case "change_card_back":
                                deck.CardBack = changeDeck.Name;
                                deck.ModifyDate = DateTime.Now;
                                break;
                            case "make_favorite":
                                user.Name = deck.Name;
                                deck.Favorite = true;
                                deck.ModifyDate = DateTime.Now;
                                break;
                        }

                        await GlobalState.users.SaveUserAsync(user);
                    }

                    return Results.Ok(new { });
                });

                app.MapDelete("/players/{player_id}/decks/{deck_id}", async (string player_id, int deck_id) =>
                {
                    var user = await GlobalState.users.GetByIdAsync(int.Parse(player_id));
                    if (user == null)
                        return Results.NotFound($"User with ID {player_id} not found");
                    user.Decks.Remove(deck_id);
                    await GlobalState.users.SaveUserAsync(user);

                    return Results.Ok(new { });
                });
                //这个端点获取卡组详情（预制卡组），暂未使用
                app.MapGet("/items/decks/{id}", async (string id) =>
                {
                    return Results.Ok("");
                });
            }
            // 5. 物品装备
            app.MapGet("/items/{id}", async (string id) =>
            {
                var user = await GlobalState.users.GetByIdAsync(int.Parse(id));
                if (user == null)
                    return Results.NotFound($"User with ID {id} not found");
                var response = new ItemsResponse(
                    Date: DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
        EquippedItems: user.EquippedItem,
                    Items: PlayerLibrary.Items.ToList()
                );

                return Results.Ok(response);
            });
            app.MapPost("/items/{id}", async (string id, EquippedItem item) =>
            {
                var user = await GlobalState.users.GetByIdAsync(int.Parse(id));
                if (user == null)
                    return Results.NotFound($"User with ID {id} not found");
                if (user.EquippedItem == null)
                    user.EquippedItem = new List<EquippedItem>();
                // 移除相同槽位的装备
                user.EquippedItem.RemoveAll(i => i.Slot == item.Slot);
                // 添加新装备
                user.EquippedItem.Add(item);
                await GlobalState.users.SaveUserAsync(user);
                return Results.Created();
            });
            app.MapPut("/crate/claim", () =>
            {
                Claim c = new
                (
                    1000,
                    new()
                    {
                        new (
                            new (
                               1000,
                               null,
                               "card",
                               "card_wildcard_elite"
                            ),
                            5
                        ),
                        new (
                            new (
                               1000,
                               null,
                               "gold",
                               ""
                            ),
                            30
                        )
                    }
                );
                return Results.Ok(c);
            });

            void RemovePlayerFromAllQueues(int playerId)
            {
                config.appconfig.WaitingPlayers1.RemoveAll(p => p.PlayerId == playerId);
                config.appconfig.WaitingPlayers2.RemoveAll(p => p.PlayerId == playerId);
                config.appconfig.WaitingPlayersClassic.RemoveAll(p => p.PlayerId == playerId);
                config.appconfig.WaitingPlayersUnranked.RemoveAll(p => p.PlayerId == playerId);
                config.appconfig.WaitingPlayersDraft.RemoveAll(p => p.PlayerId == playerId);
                config.appconfig.WaitingPlayersBrawl.RemoveAll(p => p.PlayerId == playerId);
                foreach (var code in config.appconfig.BattleCodePlayers.Keys)
                {
                    config.appconfig.BattleCodePlayers[code].RemoveAll(p => p.PlayerId == playerId);
                }
            }

            void ClearMatchRuntimeState(MatchInfo match)
            {
                match.MatchActions.Clear();
                match.EndResolutionActionId = 0;
                match.MulliganLeft = null;
                match.MulliganRight = null;
                match.LeftDeck.Clear();
                match.RightDeck.Clear();
                match.LeftHand.Clear();
                match.RightHand.Clear();
                match.MatchStartingInfo = null;
            }

            void RemovePlayerActiveMatches(int playerId, string reason)
            {
                foreach (var kvp in config.appconfig.MatchedPairs.ToArray())
                {
                    if (!kvp.Value.HasPlayer(playerId))
                        continue;

                    ClearMatchRuntimeState(kvp.Value);
                    config.appconfig.MatchedPairs.TryRemove(kvp.Key, out _);
                    Console.WriteLine($"移除玩家 {playerId} 的旧对局 {kvp.Key}，原因：{reason}");
                }
            }

            int GenerateMatchId()
            {
                for (var i = 0; i < 64; i++)
                {
                    var id = Random.Shared.Next(100000, 999999);
                    if (!config.appconfig.MatchedPairs.ContainsKey(id))
                        return id;
                }
                throw new InvalidOperationException("Unable to allocate unique match id.");
            }
            // 6. 匹配系统
            //现在的lobby还只是单人匹配，后续会增加战斗码匹配和分段匹配
            app.MapPost("/lobbyplayers", async (LobbyPlayer lobbyPlayer, HttpContext context) =>
            {
                var user = await GlobalState.users.GetByIdAsync(lobbyPlayer.PlayerId);
                if (user == null)// || user.Name == "XDLG")
                {
                    // TODO: WebSocket断开连接消息
                    //context.Connection.Close();
                    context.Connection.RequestClose();
                    return Results.BadRequest("问号问号问号");
                }
                // 检查卡组有效性（简化）
                if (!user.Decks.TryGetValue(lobbyPlayer.DeckId, out var deck))
                {
                    // context.Connection.Close();
                    context.Connection.RequestClose();
                    return Results.BadRequest("无效卡组");
                }

                // 重新进入匹配前，先清掉该玩家在队列和旧对局中的残留状态
                RemovePlayerFromAllQueues(lobbyPlayer.PlayerId);
                RemovePlayerActiveMatches(lobbyPlayer.PlayerId, "requeue");

                string exData = "training";
                try
                {
                    exData = lobbyPlayer.ExtraData.GetString();
                }
                catch
                {
                    exData = "training";
                }

                Console.WriteLine(exData);

                // 对战码匹配
                if (exData.StartsWith("battle_code:"))
                {
                    var code = exData["battle_code:".Length..];
                    if (!config.appconfig.BattleCodePlayers.ContainsKey(code))
                        config.appconfig.BattleCodePlayers[code] = new List<LobbyPlayer>();

                    var players = config.appconfig.BattleCodePlayers[code];
                    players.Add(lobbyPlayer);

                    if (players.Count >= 2)
                    {
                        var matchId = GenerateMatchId();
                        var matchInfo = new MatchInfo(matchId, players[0], players[1], "battle_code:" + code);
                        players.RemoveAt(0);
                        players.RemoveAt(0);

                        config.appconfig.MatchedPairs[matchId] = matchInfo;
                    }
                }
                // 普通匹配
                else if (exData == "training")
                {
                    Console.WriteLine("检测到匹配：" + lobbyPlayer.ToString());
                    config.appconfig.WaitingPlayers1.Add(lobbyPlayer);
                    Console.WriteLine($"当前有{config.appconfig.WaitingPlayers1.Count}");
                    //单人对战
                    if (config.appconfig.WaitingPlayers1.Count >= 1)
                    {
                        var matchId = GenerateMatchId();
                        //var matchInfo = new MatchInfo(matchId, config.appconfig.WaitingPlayers1[0], config.appconfig.WaitingPlayers1[1]);
                        //config.appconfig.MatchedPairs[matchId] = matchInfo;
                        //config.appconfig.WaitingPlayers1.RemoveAt(0);
                        //config.appconfig.WaitingPlayers1.RemoveAt(0);

                        //上面的是双人，下面的是单人
                        var matchInfo = new MatchInfo(matchId, config.appconfig.WaitingPlayers1[0], config.appconfig.WaitingPlayers1[0], "pw");
                        config.appconfig.MatchedPairs[matchId] = matchInfo;
                        config.appconfig.WaitingPlayers1.RemoveAt(0);
                        //config.appconfig.WaitingPlayers1.RemoveAt(0);
                        Console.WriteLine("匹配成功，信息为" + matchInfo.ToString());
                    }
                }
                else if (exData == "")
                {
                    config.appconfig.WaitingPlayers2.Add(lobbyPlayer);
                    if (config.appconfig.WaitingPlayers2.Count >= 2)
                    {
                        var matchId = GenerateMatchId();
                        var matchInfo = new MatchInfo(matchId, config.appconfig.WaitingPlayers2[0], config.appconfig.WaitingPlayers2[1], "");
                        config.appconfig.WaitingPlayers2.RemoveAt(0);
                        config.appconfig.WaitingPlayers2.RemoveAt(0);
                        config.appconfig.MatchedPairs[matchId] = matchInfo;
                        Console.WriteLine("匹配成功，信息为" + matchInfo.ToString());
                    }
                }
                else if (exData == "classic")
                {
                    config.appconfig.WaitingPlayersClassic.Add(lobbyPlayer);
                    if (config.appconfig.WaitingPlayersClassic.Count >= 2)
                    {
                        var matchId = GenerateMatchId();
                        var matchInfo = new MatchInfo(matchId, config.appconfig.WaitingPlayersClassic[0], config.appconfig.WaitingPlayersClassic[1], "classic");
                        config.appconfig.WaitingPlayersClassic.RemoveAt(0);
                        config.appconfig.WaitingPlayersClassic.RemoveAt(0);
                        config.appconfig.MatchedPairs[matchId] = matchInfo;
                        Console.WriteLine("匹配成功，信息为" + matchInfo.ToString());
                    }
                }
                else if (exData == "unranked")
                {
                    config.appconfig.WaitingPlayersUnranked.Add(lobbyPlayer);
                    if (config.appconfig.WaitingPlayersUnranked.Count >= 2)
                    {
                        var matchId = GenerateMatchId();
                        var matchInfo = new MatchInfo(matchId, config.appconfig.WaitingPlayersUnranked[0], config.appconfig.WaitingPlayersUnranked[1], "unranked");
                        config.appconfig.WaitingPlayersUnranked.RemoveAt(0);
                        config.appconfig.WaitingPlayersUnranked.RemoveAt(0);
                        config.appconfig.MatchedPairs[matchId] = matchInfo;
                        Console.WriteLine("匹配成功，信息为" + matchInfo.ToString());
                    }
                }
                else if (exData == "draft")
                {
                    config.appconfig.WaitingPlayersDraft.Add(lobbyPlayer);
                    if (config.appconfig.WaitingPlayersDraft.Count >= 2)
                    {
                        var matchId = GenerateMatchId();
                        var matchInfo = new MatchInfo(matchId, config.appconfig.WaitingPlayersDraft[0], config.appconfig.WaitingPlayersDraft[1], "draft");
                        config.appconfig.WaitingPlayersDraft.RemoveAt(0);
                        config.appconfig.WaitingPlayersDraft.RemoveAt(0);
                        config.appconfig.MatchedPairs[matchId] = matchInfo;
                        Console.WriteLine("匹配成功，信息为" + matchInfo.ToString());
                    }
                }
                else if (exData == "brawl")
                {
                    config.appconfig.WaitingPlayersBrawl.Add(lobbyPlayer);
                    if (config.appconfig.WaitingPlayersBrawl.Count >= 2)
                    {
                        var matchId = GenerateMatchId();
                        var matchInfo = new MatchInfo(matchId, config.appconfig.WaitingPlayersBrawl[0], config.appconfig.WaitingPlayersBrawl[1], "brawl");
                        config.appconfig.WaitingPlayersBrawl.RemoveAt(0);
                        config.appconfig.WaitingPlayersBrawl.RemoveAt(0);
                        config.appconfig.MatchedPairs[matchId] = matchInfo;
                        Console.WriteLine("匹配成功，信息为" + matchInfo.ToString());
                    }
                }
                return Results.Text("OK");
            });
            app.MapPost("/singleplayerlobby", async (LobbyPlayer lobbyPlayer, HttpContext context) =>
            {
                var user = await GlobalState.users.GetByIdAsync(lobbyPlayer.PlayerId);
                if (user == null)// || user.Name == "XDLG")
                {
                    // TODO: WebSocket断开连接消息
                    //context.Connection.Close();
                    context.Connection.RequestClose();
                    return Results.BadRequest("问号问号问号");
                }
                // 检查卡组有效性（简化）
                if (!user.Decks.TryGetValue(lobbyPlayer.DeckId, out var deck))
                {
                    // context.Connection.Close();
                    context.Connection.RequestClose();
                    return Results.BadRequest("无效卡组");
                }

                // 重新进入匹配前，先清掉该玩家在队列和旧对局中的残留状态
                RemovePlayerFromAllQueues(lobbyPlayer.PlayerId);
                RemovePlayerActiveMatches(lobbyPlayer.PlayerId, "requeue");

                string exData = "training";
                try
                {
                    exData = lobbyPlayer.ExtraData.GetString();
                }
                catch
                {
                    exData = "training";
                }

                Console.WriteLine(exData);

                // 对战码匹配
                if (exData.StartsWith("battle_code:"))
                {
                    var code = exData["battle_code:".Length..];
                    if (!config.appconfig.BattleCodePlayers.ContainsKey(code))
                        config.appconfig.BattleCodePlayers[code] = new List<LobbyPlayer>();

                    var players = config.appconfig.BattleCodePlayers[code];
                    players.Add(lobbyPlayer);

                    if (players.Count >= 2)
                    {
                        var matchId = GenerateMatchId();
                        var matchInfo = new MatchInfo(matchId, players[0], players[1], "battle_code:" + code);
                        players.RemoveAt(0);
                        players.RemoveAt(0);

                        config.appconfig.MatchedPairs[matchId] = matchInfo;
                    }
                }
                // 普通匹配
                else if (exData == "training")
                {
                    //Console.WriteLine("检测到匹配：" + lobbyPlayer.ToString());
                    config.appconfig.WaitingPlayers1.Add(lobbyPlayer);
                    //Console.WriteLine($"当前有{config.appconfig.WaitingPlayers1.Count}");
                    //单人对战
                    if (config.appconfig.WaitingPlayers1.Count >= 1)
                    {
                        var matchId = GenerateMatchId();
                        //var matchInfo = new MatchInfo(matchId, config.appconfig.WaitingPlayers1[0], config.appconfig.WaitingPlayers1[1]);
                        //config.appconfig.MatchedPairs[matchId] = matchInfo;
                        //config.appconfig.WaitingPlayers1.RemoveAt(0);
                        //config.appconfig.WaitingPlayers1.RemoveAt(0);

                        //上面的是双人，下面的是单人
                        var matchInfo = new MatchInfo(matchId, config.appconfig.WaitingPlayers1[0], new LobbyPlayer(-9178, -1, new JsonElement()), "pw");
                        config.appconfig.MatchedPairs[matchId] = matchInfo;
                        config.appconfig.WaitingPlayers1.RemoveAt(0);
                        //config.appconfig.WaitingPlayers1.RemoveAt(0);
                        Console.WriteLine("匹配成功，信息为" + matchInfo.ToString());
                    }
                }
                else if (exData == "")
                {
                    config.appconfig.WaitingPlayers2.Add(lobbyPlayer);
                    if (config.appconfig.WaitingPlayers2.Count >= 2)
                    {
                        var matchId = GenerateMatchId();
                        var matchInfo = new MatchInfo(matchId, config.appconfig.WaitingPlayers2[0], config.appconfig.WaitingPlayers2[1], "");
                        config.appconfig.WaitingPlayers2.RemoveAt(0);
                        config.appconfig.WaitingPlayers2.RemoveAt(0);
                        config.appconfig.MatchedPairs[matchId] = matchInfo;
                        Console.WriteLine("匹配成功，信息为" + matchInfo.ToString());
                    }
                }
                else if (exData == "classic")
                {
                    config.appconfig.WaitingPlayersClassic.Add(lobbyPlayer);
                    if (config.appconfig.WaitingPlayersClassic.Count >= 2)
                    {
                        var matchId = GenerateMatchId();
                        var matchInfo = new MatchInfo(matchId, config.appconfig.WaitingPlayersClassic[0], config.appconfig.WaitingPlayersClassic[1], "classic");
                        config.appconfig.WaitingPlayersClassic.RemoveAt(0);
                        config.appconfig.WaitingPlayersClassic.RemoveAt(0);
                        config.appconfig.MatchedPairs[matchId] = matchInfo;
                        Console.WriteLine("匹配成功，信息为" + matchInfo.ToString());
                    }
                }
                else if (exData == "unranked")
                {
                    config.appconfig.WaitingPlayersUnranked.Add(lobbyPlayer);
                    if (config.appconfig.WaitingPlayersUnranked.Count >= 2)
                    {
                        var matchId = GenerateMatchId();
                        var matchInfo = new MatchInfo(matchId, config.appconfig.WaitingPlayersUnranked[0], config.appconfig.WaitingPlayersUnranked[1], "unranked");
                        config.appconfig.WaitingPlayersUnranked.RemoveAt(0);
                        config.appconfig.WaitingPlayersUnranked.RemoveAt(0);
                        config.appconfig.MatchedPairs[matchId] = matchInfo;
                        Console.WriteLine("匹配成功，信息为" + matchInfo.ToString());
                    }
                }
                else if (exData == "draft")
                {
                    config.appconfig.WaitingPlayersDraft.Add(lobbyPlayer);
                    if (config.appconfig.WaitingPlayersDraft.Count >= 2)
                    {
                        var matchId = GenerateMatchId();
                        var matchInfo = new MatchInfo(matchId, config.appconfig.WaitingPlayersDraft[0], config.appconfig.WaitingPlayersDraft[1], "draft");
                        config.appconfig.WaitingPlayersDraft.RemoveAt(0);
                        config.appconfig.WaitingPlayersDraft.RemoveAt(0);
                        config.appconfig.MatchedPairs[matchId] = matchInfo;
                        Console.WriteLine("匹配成功，信息为" + matchInfo.ToString());
                    }
                }
                else if (exData == "brawl")
                {
                    config.appconfig.WaitingPlayersBrawl.Add(lobbyPlayer);
                    if (config.appconfig.WaitingPlayersBrawl.Count >= 2)
                    {
                        var matchId = GenerateMatchId();
                        var matchInfo = new MatchInfo(matchId, config.appconfig.WaitingPlayersBrawl[0], config.appconfig.WaitingPlayersBrawl[1], "brawl");
                        config.appconfig.WaitingPlayersBrawl.RemoveAt(0);
                        config.appconfig.WaitingPlayersBrawl.RemoveAt(0);
                        config.appconfig.MatchedPairs[matchId] = matchInfo;
                        Console.WriteLine("匹配成功，信息为" + matchInfo.ToString());
                    }
                }
                return Results.Text("OK");
            });
            app.MapDelete("/lobbyplayers", ([FromBody] LobbyPlayer lobbyPlayer) =>
            {
                RemovePlayerFromAllQueues(lobbyPlayer.PlayerId);
                return Results.Ok(new { status = 200 });
            });
            void MakeMatchEndpoints()
            {
                bool IsSoloMatch(MatchInfo match)
                {
                    return match.Ex.Equals("pw", StringComparison.OrdinalIgnoreCase) ||
                           (match.Left?.PlayerId != 0 && match.Left?.PlayerId == match.Right?.PlayerId);
                }

                bool HasPlayerEndedMatch(MatchInfo match, int playerId)
                {
                    if (playerId == match.Left?.PlayerId && match.PlayerStatusLeft == GameConstants.EndMatch)
                        return true;

                    if (playerId == match.Right?.PlayerId && match.PlayerStatusRight == GameConstants.EndMatch)
                        return true;

                    return false;
                }

                bool CanRemoveMatch(MatchInfo match)
                {
                    if (match.PlayerStatusLeft == GameConstants.EndMatch && match.PlayerStatusRight == GameConstants.EndMatch)
                        return true;

                    if (IsSoloMatch(match) &&
                        (match.PlayerStatusLeft == GameConstants.EndMatch || match.PlayerStatusRight == GameConstants.EndMatch))
                        return true;

                    return false;
                }

                MatchAction decryptMA(MatchActionEn matchActionen)
                {
                    var matchAction = new MatchAction();
                    Console.ForegroundColor = ConsoleColor.Green;
                    try
                    {
                        int actionId = 0;
                        string plaintext = Decode(matchActionen.A, out actionId);
                        matchAction = JsonSerializer.Deserialize<MatchAction>(plaintext, GameConstants.JsonOptions);
                        Console.WriteLine("action：" + matchAction);
                        matchAction = matchAction with { SendActionId = actionId };
                        Console.WriteLine("ActionId：" + matchAction.ActionId);
                        Console.WriteLine("解密结果：" + plaintext);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("解密失败，使用原始数据");
                        Console.WriteLine("原始数据：" + matchActionen.A);
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine(ex?.Message);
                        Console.WriteLine(ex?.InnerException?.Message);
                    }
                    Console.ForegroundColor = ConsoleColor.White;
                    return matchAction;
                }
                bool TryParseMatchActionPayload(JsonElement payload, out MatchAction matchAction)
                {
                    matchAction = new MatchAction();
                    try
                    {
                        if (payload.ValueKind != JsonValueKind.Object)
                            return false;

                        var hasEncryptedField = false;
                        if (payload.TryGetProperty("a", out var encryptedProp) &&
                            encryptedProp.ValueKind == JsonValueKind.String)
                        {
                            hasEncryptedField = true;
                            var encrypted = encryptedProp.GetString();
                            if (!string.IsNullOrWhiteSpace(encrypted))
                            {
                                int actionId = 0;
                                var plaintext = Decode(encrypted, out actionId);
                                var decoded = JsonSerializer.Deserialize<MatchAction>(plaintext, GameConstants.JsonOptions);
                                if (decoded != null)
                                {
                                    matchAction = decoded with { SendActionId = actionId };
                                    return true;
                                }
                            }
                        }

                        if (hasEncryptedField)
                            return false;

                        var direct = JsonSerializer.Deserialize<MatchAction>(payload.GetRawText(), GameConstants.JsonOptions);
                        if (direct != null)
                        {
                            matchAction = direct;
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"解析 match action 失败: {ex.Message}");
                    }

                    return false;
                }

                string? TryGetWinnerSide(MatchAction matchAction, JsonElement? payload = null)
                {
                    if (matchAction.Value != null &&
                        matchAction.Value.TryGetValue("winner_side", out var winnerFromValue))
                    {
                        var side = winnerFromValue?.ToString();
                        if (side == "left" || side == "right")
                            return side;
                    }

                    if (matchAction.ActionData != null &&
                        matchAction.ActionData.TryGetValue("winner_side", out var winnerFromActionData))
                    {
                        var side = winnerFromActionData?.ToString();
                        if (side == "left" || side == "right")
                            return side;
                    }

                    if (payload.HasValue &&
                        payload.Value.ValueKind == JsonValueKind.Object &&
                        payload.Value.TryGetProperty("winner_side", out var winnerSideProp) &&
                        winnerSideProp.ValueKind == JsonValueKind.String)
                    {
                        var side = winnerSideProp.GetString();
                        if (side == "left" || side == "right")
                            return side;
                    }

                    return null;
                }

                bool IsEndMatchSignal(MatchAction matchAction)
                {
                    return matchAction.Action == "end-match" || matchAction.ActionType == "XActionEndMatch";
                }

                void TryApplyWinnerSide(MatchInfo match, MatchAction matchAction, int matchId, string source, JsonElement? payload = null)
                {
                    if (!string.IsNullOrEmpty(match.WinnerSide))
                        return;

                    if (!IsEndMatchSignal(matchAction))
                        return;

                    var winnerSide = TryGetWinnerSide(matchAction, payload);
                    string reason;
                    try
                    {
                        reason = matchAction.Value["result"]?.ToString();
                    }
                    catch
                    {
                        reason = null;
                    }
                    if (winnerSide == "left" || winnerSide == "right")
                    {
                        match.WinnerSide = winnerSide;
                        match.MatchActions.Add(new()
                        {
                            ActionId = match.currentActionId,
                            ActionType = "ActionEndMatch",
                            PlayerId = (reason == "Victory_DestroyHQ" ? (winnerSide == "left" ? match.Left.PlayerId : match.Right.PlayerId) : (winnerSide == "left" ? match.Right.PlayerId : match.Left.PlayerId)),
                            ActionData = new()
                            {
                                { "reason", reason },
                                { "winner_side", winnerSide },
                            },
                            turn_number = match.Turns,
                            SendActionId = matchAction.SendActionId,
                        });
                        match.currentActionId++;
                        Console.WriteLine($"对局 {matchId} 设置 winner_side={winnerSide}（来自 {source}）");
                    }
                }
                app.MapGet("/matches/v2", async (HttpContext context) =>
                {
                    var user = await GetUserFromAuthAsync(context);
                    if (user == null)
                        return Results.Unauthorized();
                    Console.WriteLine("正在获取匹配信息，用户ID：" + user.Id);
                    MatchInfo? match = null;
                    Console.WriteLine(config.appconfig.MatchedPairs.Count);
                    foreach (var kvp in config.appconfig.MatchedPairs.ToArray())
                    {
                        Console.WriteLine(kvp.Value.LeftDeck);
                        if (!kvp.Value.HasPlayer(user.Id))
                            continue;

                        // 清理历史遗留的已结束对局，避免下次匹配拿到旧数据
                        if (CanRemoveMatch(kvp.Value))
                        {
                            if (config.appconfig.MatchedPairs.TryRemove(kvp.Key, out var removed))
                            {
                                ClearMatchRuntimeState(removed);
                            }
                            continue;
                        }

                        if (!string.IsNullOrEmpty(kvp.Value.WinnerSide) || HasPlayerEndedMatch(kvp.Value, user.Id))
                            continue;

                        if (kvp.Value.HasPlayer(user.Id))
                        {
                            match = kvp.Value;
                            break;
                        }
                    }

                    if (match == null)
                        return Results.Text("null");

                    // TODO: 实现makeMatchStartingInfo逻辑
                    return Results.Ok(await MakeMatchStartingInfo(user.Id, match));
                });
                async Task<MatchStartingInfo> MakeMatchStartingInfo(int myId, MatchInfo match)
                {
                    var other = match.Left?.PlayerId == myId ? match.Right : match.Left;
                    Console.WriteLine("正在生成MatchStartingInfo，玩家ID：" + myId);
                    if (match.MatchStartingInfo != null)
                    {
                        return match.MatchStartingInfo;
                    }
                    Console.WriteLine("MatchStartingInfo不存在，正在生成...");
                    var leftUser = await GlobalState.users.GetByIdAsync(match.Left.PlayerId);
                    User? rightUser = await GlobalState.users.GetByIdAsync(match.Right.PlayerId);
                    if (match.Right.PlayerId == -9178) rightUser = new(-9178, "彗星服人机");
                    Console.WriteLine("双方为" + leftUser.Id + "," + rightUser.Id);
                    // 获取卡组信息
                    var leftDeck = leftUser.Decks[match.Left.DeckId];
                    Deck rightDeck;
                    if (match.Right.DeckId == -1) rightDeck = new(new CreateDeck("FK","Germany","Finland", "%%21|4v32323232sTgv0z0C0C0C0CoBoBoBoB0Y0Y101010hShShShS1902020202030303ououpRpRpRsU;;;~;;;|0N1b"), -9178);
                    else rightDeck = rightUser.Decks[match.Right.DeckId];

                    //leftDeck.DeckCode = "%%45|8NbG8b848b8b8b848d8Nj384848d8N8NiQiQggggggohohohoh7W7W7W7WftftftftrgrgrgrgnZnZ;;;~;;;|8v1i";
                    //rightDeck.DeckCode = "%%21|4v32323232sTgv0z0C0C0C0CoBoBoBoB0Y0Y101010hShShShS1902020202030303ououpRpRpRsU;;;~;;;|0N1b";

                    // 生成卡牌列表（简化版本，实际应该解析deck_code）
                    var (leftCards, leftLocation) = GetCardsFromDeck(leftDeck, 1, true);
                    var (rightCards, rightLocation) = GetCardsFromDeck(rightDeck, 41, false);

                    // 手区从 0 开始；牌库从手牌数量开始，避免同侧 location_number 冲突
                    const int leftStartingHandCount = 4;
                    const int rightStartingHandCount = 5;
                    match.LeftHand = leftCards.Take(leftStartingHandCount).Select((card, index) =>
                        card with { Location = "hand_left", LocationNumber = index }).ToList();
                    match.RightHand = rightCards.Take(rightStartingHandCount).Select((card, index) =>
                        card with { Location = "hand_right", LocationNumber = index }).ToList();

                    match.LeftDeck = leftCards.Skip(leftStartingHandCount).Select((card, index) =>
                        card with { Location = "deck_left", LocationNumber = leftStartingHandCount + index }).ToList();
                    match.RightDeck = rightCards.Skip(rightStartingHandCount).Select((card, index) =>
                        card with { Location = "deck_right", LocationNumber = rightStartingHandCount + index }).ToList();

                    string matchType = "battle";
                    if (match.Ex.StartsWith("battle_code")) matchType = "code";
                    else if (match.Ex == "brawl") matchType = "brawl";
                    else if (match.Ex == "draft") matchType = "draft";
                    else if (match.Ex == "training" || match.Ex == "pw") matchType = "training";

                    // 构建MatchStartingInfo
                    match.MatchStartingInfo = new MatchStartingInfo(
                        LocalSubactions: true,
                        MatchAndStartingData: new MatchAndStartingData(
                            Match: new MatchData(
                                ActionPlayerId: other?.PlayerId,
                                //单人
                                //ActionSide:"right",
                                ActionSide: other?.PlayerId == match.Left?.PlayerId ? "left" : "right",
                                Actions: new List<MatchAction>(),
                                ActionsUrl: $"{config.appconfig.getAddressHttpR()}/matches/v2/{match.MatchId}/actions",
                                CurrentActionId: 0,
                                CurrentTurn: 1,
                                DeckIdLeft: match.Left.DeckId,
                                DeckIdRight: match.Right.DeckId,
                                //单人对战特供
                                LeftIsOnline: 1,
                                //LeftIsOnline:1,
                                MatchId: match.MatchId,
                                MatchType: matchType,
                                MatchUrl: $"{config.appconfig.getAddressHttpR()}/matches/v2/{match.MatchId}",
                                ModifyDate: DateTime.UtcNow.ToString("o"),
                                Notifications: new List<object>(),
                                PlayerIdLeft: leftUser.Id,
                                PlayerIdRight: rightUser.Id,
                                PlayerStatusLeft: "not_done",
                                PlayerStatusRight: (matchType == "training") ? GameConstants.MulliganDone : "not_done",
                                //单人对战特供
                                //RightIsOnline:1,
                                RightIsOnline: 1,
                                StartSide: "left",
                                Status: "pending",
                                WinnerId: 0,
                                WinnerSide: ""
                            ),
                            StartingData: new StartingData(
                                AllyFactionLeft: leftDeck.AllyFaction,
                                AllyFactionRight: rightDeck.AllyFaction,
                                CardBackLeft: leftDeck.CardBack,
                                CardBackRight: rightDeck.CardBack,
                                StartingHandLeft: match.LeftHand,
                                StartingHandRight: match.RightHand,
                                DeckLeft: match.LeftDeck,
                                DeckRight: match.RightDeck,
                                EquipmentLeft: leftUser.EquippedItem?.Select(i => i.ItemId).ToList() ?? new List<string>(),
                                EquipmentRight: rightUser.EquippedItem?.Select(i => i.ItemId).ToList() ?? new List<string>(),
                                IsAiMatch: false,
                                LeftPlayerName: leftUser.Name,
                                LeftPlayerOfficer: false,
                                LeftPlayerTag: leftUser.Tag.ToString(),
                                LocationCardLeft: leftLocation,
                                LocationCardRight: rightLocation,
                                PlayerIdLeft: leftUser.Id,
                                PlayerIdRight: rightUser.Id,
                                PlayerStarsLeft: 120,
                                PlayerStarsRight: 120,
                                RightPlayerName: rightUser.Name,
                                RightPlayerOfficer: false,
                                RightPlayerTag: rightUser.Tag.ToString()
                            )
                        )
                    );

                    return match.MatchStartingInfo;
                }
                // 新增辅助方法：从卡组生成卡牌（简化版本）
                (List<MatchCard> cards, MatchLocation location) GetCardsFromDeck(Deck deck, int startId, bool isLeft)
                {
                    Console.WriteLine(deck.DeckCode);
                    var cards = new List<MatchCard>();
                    var deckCode = deck.DeckCode ?? string.Empty;
                    if (deckCode.Length < 5)
                    {
                        return (cards, new MatchLocation(
                            CardId: startId,
                            IsGold: false,
                            Location: isLeft ? "board_hqleft" : "board_hqright",
                            LocationNumber: 0,
                            Name: "invalid_deck_code",
                            Faction: deck.MainFaction
                        ));
                    }
                    var locationCard = new MatchLocation(
                        CardId: startId,
                        IsGold: false,
                        Location: isLeft ? "board_hqleft" : "board_hqright",
                        LocationNumber: 0,
                        Faction: deck.MainFaction,
                        Name: PlayerLibrary.DeckCodeTable[deckCode[^4..^2]].Card // 这里应该根据deck_code解析实际卡牌名
                    );
                    // 预留起始 ID 给 HQ 卡，避免和牌堆/手牌 card_id 冲突
                    startId++;
                    var parsedDeckCode = deckCode.Remove(0, 5);
                    parsedDeckCode.Split(';').Take(4).Select((item, index) => new { Item = item, RepeatCount = index }).ToList().ForEach(x =>
                    {
                        foreach (var chunk in x.Item.Chunk(2))
                        {
                            if (chunk.Length != 2) return;
                            var key = string.Concat(chunk);
                            if (!PlayerLibrary.DeckCodeTable.TryGetValue(key, out var lkp)) return;
                            Console.Write(lkp.DeckCodeId);
                            for (int i = 0; i <= x.RepeatCount; i++)
                            {
                                cards.Add(new MatchCard(
                                    CardId: startId++,
                                    IsGold: false,
                                    Location: isLeft ? "deck_left" : "deck_right",
                                    LocationNumber: 0,
                                    Name: lkp.Card
                                ));
                            }
                        }
                    });
                    Console.WriteLine(cards.Count);
                    Console.WriteLine(JsonSerializer.Serialize(cards));
                    cards = [.. cards.OrderBy(_ => Random.Shared.Next()).Select((a, l) => a with { LocationNumber = l })];
                    return (cards, locationCard);
                }
                app.MapGet("/matches/v2/reconnect", async (HttpContext context) =>
                {
                    var user = await GetUserFromAuthAsync(context);
                    foreach (var i in config.appconfig.MatchedPairs)
                    {
                        MatchInfo mi = i.Value;
                        if ((mi.WinnerSide != "left" && mi.WinnerSide != "right") && (mi.Left.PlayerId == user.Id || mi.Right.PlayerId == user.Id))
                        {
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
                                mr.Actions = actions.Select(x => { return Encode(x); }).ToArray();
                            }
                            return Results.Ok(mr);
                        }
                    }
                    return Results.Ok("null");
                });
                app.MapGet("/matches/v2/{id}", (int id) =>
                {
                    return Results.Text("running");
                });

                app.MapPut("/matches/v2/{id}/", async (int id, JsonElement payload, HttpContext context) =>
                {
                    var user = await GetUserFromAuthAsync(context);
                    if (user == null)
                        return Results.Unauthorized();
                    if (!config.appconfig.MatchedPairs.TryGetValue(id, out var match))
                        return Results.NotFound($"Match with ID {id} not found");

                    if (!TryParseMatchActionPayload(payload, out var matchAction))
                    {
                        var rawPayload = payload.ValueKind == JsonValueKind.Undefined ? "<undefined>" : payload.GetRawText();
                        Console.WriteLine($"无法解析 /matches/v2/{id} 请求体: {rawPayload}");
                        return Results.Text("OK");
                    }

                    // 反作弊检查
                    if (config.appconfig.bancheat && matchAction.ActionType == GameConstants.XActionCheat)
                    {
                        // TODO: WebSocket发送封禁消息
                        user.Banned = true;

                        await GlobalState.users.SaveUserAsync(user);
                        match.WinnerSide = user.Id == match.Left?.PlayerId ? "right" : "left";
                        return Results.Ok(new { });
                    }
                    if (matchAction.Action == "lvl-loaded")
                        return Results.Ok(new { otherPlayerReady = 1 });
                    TryApplyWinnerSide(match, matchAction, id, $"/matches/v2/{id}", payload);
                    return Results.Text("OK");
                });

                app.MapPut("/matches/v2/{id}/actions", async (int id, MatchPut matchPut, HttpContext context) =>
                {
                    var user = await GetUserFromAuthAsync(context);
                    if (user == null)
                        return Results.Unauthorized();

                    if (!config.appconfig.MatchedPairs.TryGetValue(id, out var match))
                        return Results.NotFound($"Match with ID {id} not found");

                    /*
                    // 只在结束态生成一次正式结算动作，避免每次轮询都注入新动作导致客户端长时间等待
                    if (!string.IsNullOrEmpty(match.WinnerSide) && match.EndResolutionActionId == 0)
                    {
                        var winnerId = match.WinnerSide == "left" ? match.Left?.PlayerId : match.Right?.PlayerId;
                        var losingHqId = match.WinnerSide == "left" ? "41" : "1";
                        var endAction = new MatchAction(
                            ActionType: GameConstants.XActionCheat,
                            PlayerId: winnerId,
                            ActionData: new Dictionary<string, object>
                            {
                                ["0"] = "DamageCard",
                                ["1"] = losingHqId,
                                ["2"] = "99",
                                ["playerID"] = winnerId?.ToString() ?? string.Empty
                            },
                            ActionId: match.currentActionId,
                            turn_number: match.Turns,
                            sub_actions: new object[] { }
                        );
                        match.MatchActions.Add(endAction);
                        match.EndResolutionActionId = match.currentActionId;
                        match.currentActionId++;
                    }
                    */

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
                        result["actions"] = actions.Select(x => { return Encode(x); }).ToArray();
                        actions.Clear();
                    }
                    result["match"] = new
                    {
                        player_status_left = match.PlayerStatusLeft,
                        //单人对战
                        player_status_right = match.Ex.Equals("pw") ? "mulligan_done" : match.PlayerStatusRight,

                        status = "running"
                    };

                    result["opponent_polling"] = true;

                    if (!string.IsNullOrEmpty(match.WinnerSide))
                    {
                        result["match"] = new
                        {
                            player_status_left = GameConstants.EndMatch,
                            player_status_right = GameConstants.EndMatch,
                            status = GameConstants.Finished
                        };
                    }
                    //返回action
                    return Results.Ok(result);
                });
                app.MapGet("/config", (HttpContext context) =>
                {
                    return Results.Ok(new
                    {
                        XserverClosed = "",
                        XserverClosedHeader = "Server maintenance",
                        ForgotPasswordUrl = "https://pornhub.com"
                    }
                    );
                });
                app.MapPost("/matches/v2/{id}/actions", async (int id, MatchActionEn matchActionen, HttpContext context) =>
                {
                    var user = await GetUserFromAuthAsync(context);
                    if (user == null)
                        return Results.Unauthorized();
                    if (!config.appconfig.MatchedPairs.TryGetValue(id, out var match))
                        return Results.NotFound($"Match with ID {id} not found");
                    var matchAction = decryptMA((matchActionen));
                    Console.WriteLine(matchActionen.A);
                    if (matchAction.ActionType.Equals("XActionStartOfTurn"))
                    {
                        Console.WriteLine("OK有个入开始了回合");
                        match.Turns += 1;
                    }
                    // 反作弊检查
                    if (config.appconfig.bancheat && matchAction.ActionType == GameConstants.XActionCheat)
                    {
                        // TODO: WebSocket发送封禁消息
                        user.Banned = true;
                        await GlobalState.users.SaveUserAsync(user);
                        match.WinnerSide = user.Id == match.Left?.PlayerId ? "right" : "left";
                        return Results.Ok(new { });
                    }

                    if (matchAction.Action == "lvl-loaded")
                        return Results.Ok(new { otherPlayerReady = 1 });
                    TryApplyWinnerSide(match, matchAction, id, $"/matches/v2/{id}/actions");
                    if (!string.IsNullOrEmpty(matchAction.ActionType) || !string.IsNullOrEmpty(matchAction.Action))
                    {
                        if (matchAction.sub_actions != null) matchAction = matchAction with { ActionId = match.currentActionId, turn_number = match.Turns };
                        else matchAction = matchAction with { ActionId = match.currentActionId, turn_number = match.Turns, sub_actions = [] };
                        match.MatchActions.Add(matchAction);
                        match.currentActionId++;
                        /*
                        if (matchAction.ActionId > 0)
                        {
                            if (user.Id == match.Left?.PlayerId && matchAction.ActionId >= match.LeftMinactionid)
                                match.LeftMinactionid = matchAction.ActionId;
                            else if (matchAction.ActionId >= match.RightMinactionid)
                                match.RightMinactionid = matchAction.ActionId;
                        }
                        */
                        /*
                        if (user.Id == match.Left?.PlayerId)
                        {
                            /*
                            var a = new ServerMatchAction(TurnNumer : match.Turns,
                                SubActions:new(),
                                Action: matchAction.Action, 
                                ActionData:matchAction.ActionData,
                                ActionId:matchAction.ActionId,
                                Value:matchAction.Value,
                                PlayerId:matchAction.PlayerId,ActionType:matchAction.ActionType);
                            
                            match.LeftActions.Add(matchAction);
                            Console.WriteLine("玩家" + user.Id + "提交了一个动作，当前最小ActionId：" + match.LeftMinactionid);
                        }
                        else
                        {
                            var a = new ServerMatchAction(TurnNumer: match.Turns,
                                SubActions: new(),
                                Action: matchAction.Action,
                                ActionData: matchAction.ActionData,
                                ActionId: matchAction.ActionId,
                                Value: matchAction.Value,
                                PlayerId: matchAction.PlayerId, ActionType: matchAction.ActionType);
                            match.RightActions.Add(a);
                        }
                        */
                    }
                    if (matchAction.ActionType.Equals("XActionEndOfTurn") && match.Ex.Equals("pw"))
                    {
                        Console.WriteLine("OK有个入开始了回合");
                        match.Turns += 1;
                        match.MatchActions.Add(new(match.currentActionId, "XActionStartOfTurn", -9178, new()
                        {
                            { "side","right" },
                            { "75","20" },
                        }, [], match.Turns, SendActionId: matchAction.SendActionId));
                        match.currentActionId++;
                        match.MatchActions.Add(new(match.currentActionId, "XActionEndOfTurn", -9178, new()
                        {
                            { "side","right" },
                            { "75","20" },
                        }, [], match.Turns, SendActionId: matchAction.SendActionId));
                        match.currentActionId++;
                    }
                    return Results.Text("OK");
                });
                // 7. 调度阶段
                app.MapPost("/matches/v2/{id}/mulligan", async (int id, MulliganCards mulliganCards, HttpContext context) =>
                {
                    var user = await GetUserFromAuthAsync(context);
                    if (user == null)
                        return Results.Unauthorized();

                    if (!config.appconfig.MatchedPairs.TryGetValue(id, out var match))
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
                    //Console.WriteLine("deck"+deck.Count);
                    //foreach (var cardId in mulliganCards.DiscardedCardIds)
                    //{
                    //    foreach (var card in hand)
                    //    {
                    //        if (card.CardId == cardId)
                    //        {
                    //            var randomIndex = Random.Shared.Next(result.Deck.Count);
                    //            //// 交换位置
                    //            // Use record 'with' to create new instances and preserve immutability.
                    //            var deckCard = result.Deck[randomIndex];

                    //            // The value added to ReplacementCards should reflect the deck card
                    //            // but with the hand card's location info (matching previous tuple-swap behavior).
                    //            var replacementCard = deckCard with
                    //            {
                    //                Location = card.Location,
                    //                LocationNumber = card.LocationNumber
                    //            };

                    //            // The deck slot will be replaced by the hand card but keep the original deck location info.
                    //            var newDeckEntry = card with
                    //            {
                    //                Location = deckCard.Location,
                    //                LocationNumber = deckCard.LocationNumber
                    //            };
                    //            result.ReplacementCards.Add(replacementCard);
                    //            result.Deck[randomIndex] = newDeckEntry;
                    //            break;
                    //        }
                    //    }
                    //}
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
                app.MapGet("/matches/v2/{id}/mulligan/{location}", (int id, string location) =>
                {
                    if (!config.appconfig.MatchedPairs.TryGetValue(id, out var match))
                        return Results.Text("null");
                    var mulligan = location == "left" ? match.MulliganLeft : match.MulliganRight;
                    if (match.Ex.Equals("pw"))
                    {
                        //单人
                        mulligan = new MulliganResult(
                           Deck: match.RightDeck,
                           ReplacementCards: new List<MatchCard>()
                        );
                        //单人结束
                    }
                    return mulligan == null ? Results.Text("null") : Results.Ok(mulligan);
                });
                // 8. 比赛结束
                app.MapGet("/matches/v2/{id}/post", async (int id, HttpContext context) =>
                {
                    var user = await GetUserFromAuthAsync(context);
                    if (user == null)
                        return Results.Unauthorized();

                    if (!config.appconfig.MatchedPairs.TryGetValue(id, out var match))
                        return Results.NotFound($"Match with ID {id} not found");

                    if (IsSoloMatch(match))
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

                    if (CanRemoveMatch(match))
                    {
                        ClearMatchRuntimeState(match);
                        config.appconfig.MatchedPairs.TryRemove(id, out _);
                    }

                    var player = match.GetPlayerById(user.Id);
                    var isWinner = match.WinnerSide == (user.Id == match.Left?.PlayerId ? "left" : "right");

                    if (player != null && user.Decks.TryGetValue(player.DeckId, out var deck))
                    {
                        var response = new PostMatchResponse(
                            Faction: deck.MainFaction,
                            Winner: isWinner
                        );
                        Console.WriteLine(JsonSerializer.Serialize(response));
                        return Results.Ok(response);
                    }

                    return Results.NotFound();
                });
            }
            // 10. 管理API端点,暂时不需要
            void MakeAdminEndpoints()
            {
                app.MapGet("/admin/users/count", async () =>
                {
                    var users = await GlobalState.users.GetAllUsersAsync();
                    return Results.Ok(new { count = users.Count });
                });
                app.MapGet("/admin/users/list", async () =>
                {
                    var Users = await GlobalState.users.GetAllUsersAsync();
                    var simplifiedUsers = Users.Select(u => new
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

                app.MapDelete("/admin/users/{userId}", async (int userId) =>
                {
                    var user = await GlobalState.users.GetByIdAsync(userId);
                    if (user == null)
                        return Results.NotFound($"User with ID {userId} not found");

                    await GlobalState.users.DeleteUserAsync(userId);
                    return Results.Ok(new { message = $"User {userId} deleted successfully" });
                });

                app.MapPost("/admin/users/{userId}/ban", async (int userId) =>
                {
                    var user = await GlobalState.users.GetByIdAsync(userId);
                    if (user == null)
                        return Results.NotFound($"User with ID {userId} not found");

                    user.Banned = true;

                    await GlobalState.users.SaveUserAsync(user);
                    return Results.Ok(new { message = $"User {userId} banned successfully" });
                });

                app.MapPost("/admin/users/{userId}/unban", async (int userId) =>
                {
                    var user = await GlobalState.users.GetByIdAsync(userId);
                    if (user == null)
                        return Results.NotFound($"User with ID {userId} not found");

                    user.Banned = false;

                    await GlobalState.users.SaveUserAsync(user);
                    return Results.Ok(new { message = $"User {userId} unbanned successfully" });
                });
            }
            UseMiddleWares();
            MakePlayerEndpoints();
            MakeUserEndpoints();
            MakeDecksEndpoints();
            MakeMatchEndpoints();
            // 11. 全局异常处理
            app.UseExceptionHandler(exceptionHandlerApp =>
            {
                exceptionHandlerApp.Run(async context =>
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new ErrorResponse(
                        Error: "Internal server error",
                        Message: "An unexpected error occurred",
                        StatusCode: 500
                    ));
                });
            });
            // 12. 未找到路由的处理
            app.UseStatusCodePages(async statusCodeContext =>
            {
                var response = statusCodeContext.HttpContext.Response;

                if (response.StatusCode == 404)
                {
                    await response.WriteAsJsonAsync(new ErrorResponse(
                        Error: "Not found",
                        Message: "The requested resource was not found",
                        StatusCode: 404
                    ));
                }
            });
            // 数据库初始化和清理
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                Console.WriteLine($"Application started on {config.appconfig.getAddressHttp()}");
                Console.WriteLine($"Faster 已准备");
            });
            app.Lifetime.ApplicationStopping.Register(() =>
            {
                Console.WriteLine("Application stopping. Cleaning up...");
                // 清理数据库资源
                GlobalState.users.Dispose();
            });
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine("正在启动http服务器");
            Console.WriteLine("等待两秒确保初始化成功");
            if (File.Exists("./YCDR"))
                Console.WriteLine("发现持久化数据，已加载");
            Console.ForegroundColor = ConsoleColor.White;
            await app.RunAsync();
        }
    }
}
