using fyserver.Models;
using fyserver.Serialization;
using fyserver.Services;

namespace fyserver.Endpoints;

public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        // 2. 配置和基本信息
        app.MapPost("/session", async (Session session, HttpContext context, UserStoreService users, CodecService codec, ServerOptions options, WebSocketHubService webSockets, ClientServerConfigService clientServerConfig, FriendService friends, EmbeddedSettings embeddedSettings) =>
        {
            string addressHttp = options.GetAddressHttpR();
            // 联机房间里以启动器账号为准，不信游戏自己报的用户名
            var sessionUserName = RoomTicketVerifier.Current(context)?.Name ?? session.Username;
            User? user;
            try
            {
                user = await users.GetByUserNameAsync(sessionUserName);
                Console.WriteLine($"Find user: {sessionUserName}");
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
                    user = await users.CreateUserAsync(sessionUserName);
                    Console.WriteLine($"Created new user: {sessionUserName}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.InnerException?.Message);
                    // 用户已存在或其他错误
                    return Results.BadRequest(ex.Message);
                }
            }

            if (user.Banned && !user.IsBanActive(DateTime.UtcNow))
            {
                user.Banned = false;
                user.BanExpiresAt = null;
                user.BanReason = "";
                await users.SaveUserAsync(user);
            }
            if (user.Banned)
            {
                await webSockets.DisconnectAsync(user.Id, "该账户已被封禁");
                return Results.Json(
                    new BannedResponse(
                        new Error("user_error", user.BanDescription),
                        "Forbidden",
                        403
                    ),
                    FyJsonContext.Default.BannedResponse,
                    statusCode: 403
                );
            }

            user = await users.WithUserLockAsync(user.Id, async current =>
            {
                current.LastLoginAt = DateTime.UtcNow;
                var remoteIp = context.Connection.RemoteIpAddress;
                current.LastLoginIp = remoteIp == null ? "" : remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4().ToString() : remoteIp.ToString();
                var deviceParts = new[] { session.PlatformType, session.PlatformInfo, session.PlatformVersion, session.ClientType, session.Build }
                    .Where(value => !string.IsNullOrWhiteSpace(value));
                var device = string.Join(" · ", deviceParts);
                if (string.IsNullOrWhiteSpace(device)) device = context.Request.Headers.UserAgent.ToString();
                current.LastLoginDevice = device.Length > 512 ? device[..512] : device;
                await users.SaveUserAsync(current);
                return current;
            }) ?? user;
            // 客户端重新登录后内存里没有聊天记录，下次 WebSocket 连接时补发。
            friends.MarkClientLogin(user.Id);

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
                Diamonds: user.Diamonds,
                DoubleXpEndDate: "2025-07-03T12:13:36.889692Z",
                DraftAdmissions: 1,
                Dust: user.Dust,
                Email: null,
                EmailRewardReceived: false,
                EmailVerified: false,
                ExtendedRewards: false,
                GermanyLevel: 500,
                GermanyLevelClaimed: 500,
                GermanyXp: 0,
                Gold: user.Gold,
                HasBeenOfficer: true,
                HeartbeatUrl: $"{addressHttp}/players/{user.Id}/heartbeat",
                IsOfficer: true,
                IsOnline: true,
                JapanLevel: 500,
                JapanLevelClaimed: 500,
                JapanXp: 0,
                Jti: "114514",
                Jwt: $"{codec.Encode(sessionUserName, 114)}",
                LastCrateClaimedDate: "2025-07-02T11:24:15.567042Z",
                LastDailyMissionCancel: null,
                LastDailyMissionRenewal: "2025-07-05T15:21:43.653915Z",
                LastLogonDate: "2025-07-05T15:21:06.168847Z",
                LaunchMessages: new List<object>(),
                LibraryUrl: $"{addressHttp}/players/{user.Id}/library",
                LinkerAccount: user.LinkerAccount,
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
                PlayerTag: user.Tag,
                Rewards: new List<object>(),
                SeasonEnd: "2025-08-01T00:00:00Z",
                SeasonWins: user.Wins,
                ServerOptions: clientServerConfig.ReadForSession(options.GetAddressWsR(), embeddedSettings.Versions),
                ServerTime: DateTime.UtcNow.ToString("yyyy.MM.dd-HH.mm.ss"),
                SovietLevel: 500,
                SovietLevelClaimed: 500,
                SovietXp: 0,
                Stars: user.Stars,
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

        app.MapGet("/", async (HttpContext context, UserStoreService users, CodecService codec, ServerOptions options, EmbeddedSettings embeddedSettings) =>
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

            return Results.Ok(await BuildConfigResponse(user, codec, options, RoomRoles(context, embeddedSettings)));
        });

        return app;
    }

    /// <summary>联机房间里的身份：房主全有，其他人只有房主授予的。不在房间里时返回 null，用账号自己的身份。</summary>
    private static IReadOnlyList<string>? RoomRoles(HttpContext context, EmbeddedSettings settings) =>
        RoomTicketVerifier.Current(context) switch
        {
            null => null,
            { Role: "host" } => PlayerRoleCatalog.Available,
            var claims => settings.RolesFor(claims.Name),
        };

    /// <summary>构建 GET / 的 Config 响应（原 config.appconfig.getConfigAsync）。</summary>
    private static Task<Config> BuildConfigResponse(User? user, CodecService codec, ServerOptions options, IReadOnlyList<string>? roomRoles)
    {
        CurrentUser? currentUser = null;
        var userName = user?.UserName ?? "1939Mother";
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
                Roles: PlayerRoleCatalog.Normalize(roomRoles ?? user.Roles),
                Tier: "LIVE",
                UserId: user.Id,
                UserName: userName
            );
        }

        var addressHttp = options.GetAddressHttpR();
        var playerId = user?.Id ?? 0;
        var config1 = new Config(
            CurrentUser: currentUser,
            Endpoints: new fyserver.Models.Endpoints(
                Draft: $"{addressHttp}/draft/",
                Email: $"{addressHttp}/email/set",
                Lobbyplayers: $"{addressHttp}/lobbyplayers",
                Matches: $"{addressHttp}/matches",
                Matches2: $"{addressHttp}/matches/v2/",
                // 这些地址必须使用玩家数字 ID。此前这里拼接 codec token；token 使用
                // Base64，可能包含 '/'，会被 HTTP 路由拆成多个路径段，导致客户端
                // 发出的 PUT /players/.../set-name 请求直接 404。
                MyDraft: userName.Equals("1939Mother") ? "" : $"{addressHttp}/draft/{playerId}",
                MyItems: userName.Equals("1939Mother") ? "" : $"{addressHttp}/items/{playerId}",
                MyPlayer: userName.Equals("1939Mother") ? "" : $"{addressHttp}/players/{playerId}",
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
