using fyserver.Models;
using fyserver.Serialization;
using fyserver.Services;

namespace fyserver.Endpoints;

public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        // 2. 配置和基本信息
        app.MapPost("/session", async (Session session, UserStoreService users, CodecService codec, ServerOptions options) =>
        {
            string addressHttp = options.GetAddressHttpR();
            User? user;
            try
            {
                user = await users.GetByUserNameAsync(session.Username);
                Console.WriteLine($"Find user: {session.Username}");
            }
            catch (Exception)
            {
                user = null;
            }

            if (user == null)
            {
                Console.WriteLine("未找到");
                try
                {
                    user = await users.CreateUserAsync(session.Username);
                    Console.WriteLine($"Created new user: {session.Username}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.InnerException?.Message);
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
                    FyJsonContext.Default.BannedResponse,
                    statusCode: 403
                );
            }

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
                    ["headers"] = user.Decks.Values.Select(d => new DeckSummaryDto(
                        d.Name,
                        d.MainFaction,
                        d.AllyFaction,
                        d.CardBack,
                        d.DeckCode,
                        d.Favorite,
                        d.Id,
                        d.PlayerId,
                        d.LastPlayed.ToString("o"),
                        d.CreateDate.ToString("o"),
                        d.ModifyDate.ToString("o")
                    )).ToList()
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
                Jwt: $"{codec.Encode(session.Username, 114)}",
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
                ServerOptions: File.Exists("./config/serverOptions.json") ? File.ReadAllText("./config/serverOptions.json").Replace("{WsAddress}", options.GetAddressWsR()) : "",
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

        app.MapGet("/", async (HttpContext context, UserStoreService users, CodecService codec, ServerOptions options) =>
        {
            var auth = context.Request.Headers.Authorization.FirstOrDefault();
            // Console.WriteLine(auth);
            if (string.IsNullOrEmpty(auth))
            {
                auth = "JWT 1939Mother";
            }

            string userName = "1939Mother";
            try
            {
                userName = codec.Decode(auth["JWT ".Length..], out _);
            }
            catch
            {
                if (auth is not "JWT 1939Mother")
                    return Results.BadRequest("Invalid Authorization header");
            }

            User? user = await users.GetByUserNameAsync(userName);
            if (user == null)
                try
                {
                    user = await users.CreateUserAsync(userName);
                    Console.WriteLine($"Created new user: {userName}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.InnerException?.Message);
                    // 用户已存在或其他错误
                    return Results.BadRequest(ex.Message);
                }

            return Results.Ok(await BuildConfigResponse(user, codec, options));
        });

        return app;
    }

    /// <summary>构建 GET / 的 Config 响应（原 config.appconfig.getConfigAsync）。</summary>
    private static Task<Config> BuildConfigResponse(User? user, CodecService codec, ServerOptions options)
    {
        CurrentUser? currentUser = null;
        var userName = user?.UserName ?? "1939Mother";
        var jwt = codec.Encode(userName, 114);
        if (userName != "1939Mother" && user != null)
        {
            currentUser = new CurrentUser(
                ClientId: user.Id,
                Exp: 1770289815,
                ExternalId: userName,
                Iat: 1770289815,
                IdentityId: user.Id,
                Iss: "fyserver",
                Jti: "114514",
                Language: user.Locale,
                Payment: "notavailable",
                PlayerId: user.Id.ToString(),
                Provider: "device_id",
                Roles: new List<string>(),
                Tier: "LIVE",
                UserId: user.Id,
                UserName: userName
            );
        }

        var addressHttp = options.GetAddressHttpR();
        var config1 = new Config(
            CurrentUser: currentUser,
            Endpoints: new fyserver.Models.Endpoints(
                Draft: $"{addressHttp}/draft/",
                Email: $"{addressHttp}/email/set",
                Lobbyplayers: $"{addressHttp}/lobbyplayers",
                Matches: $"{addressHttp}/matches",
                Matches2: $"{addressHttp}/matches/v2/",
                MyDraft: userName.Equals("1939Mother") ? "" : $"{addressHttp}/draft/{jwt}",
                MyItems: userName.Equals("1939Mother") ? "" : $"{addressHttp}/items/{jwt}",
                MyPlayer: userName.Equals("1939Mother") ? "" : $"{addressHttp}/players/{jwt}",
                Players: $"{addressHttp}/players",
                Purchase: $"{addressHttp}/store/v2/txn",
                Root: addressHttp,
                Session: $"{addressHttp}/session",
                Store: $"{addressHttp}/store/",
                Tourneys: $"{addressHttp}/tourney/",
                Transactions: $"{addressHttp}/store/txn",
                ViewOffers: $"{addressHttp}/store/v2/"
            ));
        return Task.FromResult(config1);
    }
}
