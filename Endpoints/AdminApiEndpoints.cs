using System.Text.Json;
using System.Text.Json.Nodes;
using fyserver.Models;
using fyserver.Services;

namespace fyserver.Endpoints;

/// <summary>
/// 静态后台（/admin-ui）用的 JSON 数据接口。
///
/// 为什么全部手写 JSON 字符串、不用 DTO + 序列化器：
///   - 这些响应只给自家静态页用，字段名可自由定（统一 camelCase）；
///   - 手写字符串零反射、零源生成上下文改动，NativeAOT 下一定安全。
/// 访问控制由 AdminApiAuthorizationMiddleware 负责（/admin/api 前缀）。
/// </summary>
public static class AdminApiEndpoints
{
    public static IEndpointRouteBuilder MapAdminApiEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/admin/api");

        // ---------------------------------------------------------- 会话（静态页登录用）
        api.MapGet("/session", (HttpContext context, ServerOptions options, AdminAccountService account) =>
        {
            var loopback = ClientAddress.IsLoopback(context);
            var authorized = account.ValidateSession(context.Request.Cookies[AdminAccountService.CookieName]) ||
                             AdminAuth.CheckKey(context.Request.Headers["X-Admin-Key"].FirstOrDefault(), options);

            return Results.Text(new JsonObject
            {
                ["authorized"] = authorized,
                ["loopback"] = loopback,
                ["initialized"] = account.IsInitialized,
                ["username"] = authorized ? account.Username : null,
                ["keyConfigured"] = !string.IsNullOrEmpty(options.adminApiKey),
                ["hasSession"] = account.ValidateSession(context.Request.Cookies[AdminAccountService.CookieName])
            }.ToJsonString(), "application/json");
        });

        api.MapPost("/setup", async (HttpContext context, AdminAccountService account) =>
        {
            if (!ClientAddress.IsLoopback(context))
                return Results.Text(new JsonObject { ["ok"] = false, ["message"] = "首次设置仅允许在服务器本机完成" }.ToJsonString(), "application/json", statusCode: 403);
            var body = await ReadJsonBody(context);
            var result = account.Initialize(body?["username"]?.GetValue<string>(), body?["password"]?.GetValue<string>());
            if (!result.Ok)
                return Results.Text(new JsonObject { ["ok"] = false, ["message"] = result.Message }.ToJsonString(), "application/json", statusCode: 400);
            SetSessionCookie(context, account.CreateSession());
            return Results.Text(new JsonObject { ["ok"] = true, ["message"] = result.Message }.ToJsonString(), "application/json");
        });

        api.MapPost("/login", async (HttpContext context, AdminAccountService account) =>
        {
            var body = await ReadJsonBody(context);
            var username = body?["username"]?.GetValue<string>();
            var password = body?["password"]?.GetValue<string>();

            if (!account.IsInitialized)
                return Results.Text(new JsonObject { ["ok"] = false, ["message"] = "请先完成管理员初始设置" }.ToJsonString(), "application/json", statusCode: 428);
            if (!account.VerifyCredentials(username, password))
                return Results.Text(new JsonObject { ["ok"] = false, ["message"] = "用户名或密码不正确" }.ToJsonString(),
                    "application/json", statusCode: StatusCodes.Status401Unauthorized);
            SetSessionCookie(context, account.CreateSession());

            return Results.Text(new JsonObject { ["ok"] = true, ["message"] = "登录成功" }.ToJsonString(), "application/json");
        });

        api.MapPost("/logout", (HttpContext context) =>
        {
            context.Response.Cookies.Delete(AdminAccountService.CookieName);
            return Results.Text(new JsonObject { ["ok"] = true, ["message"] = "已退出" }.ToJsonString(), "application/json");
        });

        // ---------------------------------------------------------- 概览
        api.MapGet("/stats", async (UserStoreService users, WebSocketHubService hub, MatchManagerService matches,
            ServerOptions options, ServerMetricsService metrics) =>
        {
            var all = await users.GetAllUsersAsync();
            var activeMatchCount = matches.GetActiveRealMatches().Count;
            var queuedPlayerCount = matches.WaitingPlayers1.Count + matches.WaitingPlayers2.Count +
                                    matches.WaitingPlayersClassic.Count + matches.WaitingPlayersUnranked.Count +
                                    matches.WaitingPlayersDraft.Count + matches.WaitingPlayersBrawl.Count;
            var metricSnapshot = metrics.Capture(hub.OnlineCount, activeMatchCount, queuedPlayerCount);
            var queues = new JsonArray
            {
                Queue("经典", matches.WaitingPlayers1.Count + matches.WaitingPlayers2.Count),
                Queue("战役/战斗码", matches.WaitingPlayersClassic.Count),
                Queue("休闲", matches.WaitingPlayersUnranked.Count),
                Queue("竞技场", matches.WaitingPlayersDraft.Count),
                Queue("乱斗", matches.WaitingPlayersBrawl.Count)
            };

            var process = System.Diagnostics.Process.GetCurrentProcess();
            var uptime = DateTime.Now - process.StartTime;

            var payload = new JsonObject
            {
                ["userCount"] = all.Count,
                ["bannedCount"] = all.Count(u => u.Banned),
                ["deckCount"] = all.Sum(u => u.Decks.Count),
                ["onlineCount"] = hub.OnlineCount,
                ["activeMatchCount"] = activeMatchCount,
                ["queuedPlayerCount"] = queuedPlayerCount,
                ["queues"] = queues,
                ["httpAddress"] = options.GetAddressHttp(),
                ["clientHttpAddress"] = options.GetAddressHttpR(),
                ["webSocketAddress"] = options.GetAddressWsR(),
                ["port"] = options.portHttp,
                ["ip"] = options.ip,
                ["bancheat"] = options.bancheat,
                ["adminKeyConfigured"] = !string.IsNullOrEmpty(options.adminApiKey),
                ["startedAt"] = process.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                ["uptime"] = $"{(int)uptime.TotalDays} 天 {uptime.Hours} 小时 {uptime.Minutes} 分 {uptime.Seconds} 秒",
                ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                ["performance"] = new JsonObject
                {
                    ["cpuPercent"] = Math.Round(metricSnapshot.CpuPercent, 1),
                    ["memoryUsedBytes"] = metricSnapshot.MemoryUsedBytes,
                    ["memoryTotalBytes"] = metricSnapshot.MemoryTotalBytes,
                    ["memoryPercent"] = Math.Round(metricSnapshot.MemoryPercent, 1),
                    ["databaseBytes"] = metricSnapshot.DatabaseBytes,
                    ["diskTotalBytes"] = metricSnapshot.DiskTotalBytes,
                    ["diskPercent"] = Math.Round(metricSnapshot.DiskPercent, 4)
                },
                ["activity"] = new JsonArray(metricSnapshot.Activity.Select(point => (JsonNode)new JsonObject
                {
                    ["timestamp"] = point.Timestamp,
                    ["online"] = point.Online,
                    ["matches"] = point.Matches,
                    ["queued"] = point.Queued
                }).ToArray())
            };

            return Results.Text(payload.ToJsonString(), "application/json");
        });

        // ---------------------------------------------------------- 商店配置热重载
        api.MapPost("/store/reload", (StoreConfigService store) =>
        {
            store.Reload();
            return Results.Text(new JsonObject { ["ok"] = true, ["message"] = "商店配置已重新加载（config/store.json）" }.ToJsonString(), "application/json");
        });

        // ---------------------------------------------------------- 用户
        api.MapGet("/users", async (string? q, UserStoreService users, WebSocketHubService hub) =>
        {
            var all = await users.GetAllUsersAsync();
            all.Sort((a, b) => a.Id.CompareTo(b.Id));

            var online = hub.OnlineUserIds().ToHashSet();
            var keyword = q?.Trim();
            var array = new JsonArray();
            foreach (var user in all)
            {
                if (!string.IsNullOrEmpty(keyword) &&
                    !user.Id.ToString().Contains(keyword, StringComparison.Ordinal) &&
                    !user.UserName.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                    !user.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    continue;

                array.Add(new JsonObject
                {
                    ["id"] = user.Id,
                    ["userName"] = user.UserName,
                    ["name"] = user.Name,
                    ["tag"] = user.Tag,
                    ["deckCount"] = user.Decks.Count,
                    ["banned"] = user.Banned,
                    ["online"] = online.Contains(user.Id),
                    ["createdAt"] = user.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                });
            }

            return Results.Text(new JsonObject { ["total"] = all.Count, ["users"] = array }.ToJsonString(), "application/json");
        });

        api.MapGet("/users/{id:int}", async (int id, UserStoreService users, WebSocketHubService hub, MatchManagerService matches) =>
        {
            var user = await users.GetByIdAsync(id);
            if (user == null)
                return Results.NotFound();

            var decks = new JsonArray();
            foreach (var deck in user.Decks.Values.OrderBy(d => d.Id))
            {
                decks.Add(new JsonObject
                {
                    ["id"] = deck.Id,
                    ["name"] = deck.Name,
                    ["mainFaction"] = deck.MainFaction,
                    ["allyFaction"] = deck.AllyFaction,
                    ["favorite"] = deck.Favorite,
                    ["lastPlayed"] = deck.LastPlayed.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                });
            }

            var match = matches.GetActiveMatchForUser(id);
            var payload = new JsonObject
            {
                ["id"] = user.Id,
                ["userName"] = user.UserName,
                ["name"] = user.Name,
                ["tag"] = user.Tag,
                ["locale"] = user.Locale,
                ["banned"] = user.Banned,
                ["online"] = hub.TryGetClient(id, out _),
                ["createdAt"] = user.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                ["updatedAt"] = user.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                ["equippedItemCount"] = user.EquippedItem.Count,
                ["decks"] = decks,
                ["activeMatchId"] = match?.MatchId
            };

            return Results.Text(payload.ToJsonString(), "application/json");
        });

        api.MapPost("/users/{id:int}/ban", async (int id, AdminUserService adminUsers) => ToResult(await adminUsers.BanAsync(id)));
        api.MapPost("/users/{id:int}/unban", async (int id, AdminUserService adminUsers) => ToResult(await adminUsers.UnbanAsync(id)));
        api.MapPost("/users/{id:int}/kick", async (int id, string? reason, AdminUserService adminUsers) => ToResult(await adminUsers.KickAsync(id, reason)));
        api.MapDelete("/users/{id:int}", async (int id, AdminUserService adminUsers) => ToResult(await adminUsers.DeleteAsync(id)));

        // ---------------------------------------------------------- 对局与队列
        api.MapGet("/matches", async (MatchManagerService matches, UserStoreService users, WebSocketHubService hub) =>
        {
            var names = new Dictionary<int, string>();
            foreach (var user in await users.GetAllUsersAsync())
                names[user.Id] = user.UserName;
            var online = hub.OnlineUserIds().ToHashSet();

            string NameOf(int playerId) => playerId <= 0
                ? (playerId == -9178 ? "人机" : $"#{playerId}")
                : names.TryGetValue(playerId, out var name) ? name : $"#{playerId}";

            var active = new JsonArray();
            foreach (var match in matches.GetActiveRealMatches())
            {
                active.Add(new JsonObject
                {
                    ["matchId"] = match.MatchId,
                    ["matchType"] = string.IsNullOrEmpty(match.Ex) ? "classic" : match.Ex,
                    ["status"] = match.WinnerSide is null ? "进行中" : $"已结束（{match.WinnerSide}）",
                    ["currentTurn"] = match.Turns,
                    ["actionCount"] = match.MatchActions.Count,
                    ["leftPlayerId"] = match.Left?.PlayerId ?? 0,
                    ["leftPlayerName"] = NameOf(match.Left?.PlayerId ?? 0),
                    ["leftStatus"] = match.PlayerStatusLeft,
                    ["leftOnline"] = (match.Left?.PlayerId ?? 0) > 0 && online.Contains(match.Left!.PlayerId),
                    ["rightPlayerId"] = match.Right?.PlayerId ?? 0,
                    ["rightPlayerName"] = NameOf(match.Right?.PlayerId ?? 0),
                    ["rightStatus"] = match.PlayerStatusRight,
                    ["rightOnline"] = (match.Right?.PlayerId ?? 0) > 0 && online.Contains(match.Right!.PlayerId)
                });
            }

            var queues = new JsonArray();
            void AddQueue(string name, List<LobbyPlayer> players)
            {
                if (players.Count == 0)
                    return;
                var list = new JsonArray();
                foreach (var player in players)
                    list.Add(new JsonObject { ["playerId"] = player.PlayerId, ["name"] = NameOf(player.PlayerId) });
                queues.Add(new JsonObject { ["name"] = name, ["players"] = list });
            }

            AddQueue("经典（1/2 号位）", matches.WaitingPlayers1.Concat(matches.WaitingPlayers2).ToList());
            AddQueue("战役/战斗码", matches.WaitingPlayersClassic);
            AddQueue("休闲", matches.WaitingPlayersUnranked);
            AddQueue("竞技场", matches.WaitingPlayersDraft);
            AddQueue("乱斗", matches.WaitingPlayersBrawl);

            var payload = new JsonObject
            {
                ["onlineCount"] = hub.OnlineCount,
                ["activeMatches"] = active,
                ["queues"] = queues
            };

            return Results.Text(payload.ToJsonString(), "application/json");
        });

        api.MapPost("/matches/{matchId:int}/remove", (int matchId, MatchManagerService matches) =>
            matches.RemoveMatch(matchId)
                ? Results.Text(new JsonObject { ["ok"] = true, ["message"] = $"已移除对局 {matchId}" }.ToJsonString(), "application/json")
                : Results.Text(new JsonObject { ["ok"] = false, ["message"] = $"对局 {matchId} 不存在" }.ToJsonString(), "application/json"));

        api.MapPost("/queues/clear", (MatchManagerService matches) =>
        {
            matches.WaitingPlayers1.Clear();
            matches.WaitingPlayers2.Clear();
            matches.WaitingPlayersClassic.Clear();
            matches.WaitingPlayersUnranked.Clear();
            matches.WaitingPlayersDraft.Clear();
            matches.WaitingPlayersBrawl.Clear();
            return Results.Text(new JsonObject { ["ok"] = true, ["message"] = "已清空全部匹配队列" }.ToJsonString(), "application/json");
        });

        // ---------------------------------------------------------- 内容配置（frontpage / skirmish / knockout）
        foreach (var kind in ContentKinds.All)
        {
            var path = kind.Path;
            var isFrontpage = kind.Key == "frontpage";

            api.MapGet($"/content/{kind.Key}", (ContentEntriesService entries) =>
            {
                var array = new JsonArray();
                foreach (var entry in entries.List(path))
                {
                    array.Add(new JsonObject
                    {
                        ["id"] = entry.Id,
                        ["name"] = entry.Name,
                        ["startDate"] = entry.StartDate,
                        ["endDate"] = entry.EndDate
                    });
                }
                return Results.Text(new JsonObject { ["count"] = array.Count, ["entries"] = array }.ToJsonString(), "application/json");
            });

            api.MapGet($"/content/{kind.Key}/{{id:int}}", (int id, ContentEntriesService entries) =>
            {
                var entry = entries.Get(path, id);
                if (entry == null)
                    return Results.NotFound();
                return Results.Text(new JsonObject
                {
                    ["id"] = entry.Id,
                    ["name"] = entry.Name,
                    ["startDate"] = entry.StartDate,
                    ["endDate"] = entry.EndDate,
                    ["raw"] = entry.Raw
                }.ToJsonString(), "application/json");
            });

            api.MapPost($"/content/{kind.Key}", async (HttpContext context, ContentEntriesService entries) =>
            {
                var body = await ReadJsonBody(context);
                if (body == null)
                    return BadJson();

                return Save(entries, path, isFrontpage, AsInt(body["id"]), body);
            });

            api.MapPost($"/content/{kind.Key}/{{id:int}}", async (int id, HttpContext context, ContentEntriesService entries) =>
            {
                var body = await ReadJsonBody(context);
                if (body == null)
                    return BadJson();

                return Save(entries, path, isFrontpage, id, body);
            });

            api.MapDelete($"/content/{kind.Key}/{{id:int}}", (int id, ContentEntriesService entries) =>
                ToResult(entries.Delete(path, id)));
        }

        return app;
    }

    private static void SetSessionCookie(HttpContext context, string value) => context.Response.Cookies.Append(
        AdminAccountService.CookieName, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddDays(7)
        });

    // ---------------------------------------------------------------- 辅助

    private static JsonObject Queue(string name, int count) => new() { ["name"] = name, ["count"] = count };

    private static IResult ToResult((bool ok, string message) result) =>
        Results.Text(new JsonObject { ["ok"] = result.ok, ["message"] = result.message }.ToJsonString(), "application/json");

    private static IResult BadJson() =>
        Results.Text(new JsonObject { ["ok"] = false, ["message"] = "请求体不是合法的 JSON 对象" }.ToJsonString(),
            "application/json", statusCode: StatusCodes.Status400BadRequest);

    /// <summary>保存内容条目：raw 里未建模的字段原样保留，与 Razor 后台行为一致。</summary>
    private static IResult Save(ContentEntriesService entries, string path, bool isFrontpage, int id, JsonObject body)
    {
        var payloadKey = isFrontpage ? "content" : "rules";
        var raw = body["raw"]?.GetValue<string>();

        // 前端可以直接给 raw（完整条目 JSON），也可以给 name/startDate/endDate + raw
        JsonObject merged;
        try
        {
            merged = string.IsNullOrWhiteSpace(raw) ? new JsonObject() : JsonNode.Parse(raw) as JsonObject ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            return Results.Text(new JsonObject { ["ok"] = false, ["message"] = $"raw JSON 格式错误：{ex.Message}" }.ToJsonString(),
                "application/json", statusCode: StatusCodes.Status400BadRequest);
        }

        // 允许把 content / rules 作为独立字段传来（编辑页表单场景）
        if (body[payloadKey] is JsonObject nested)
            merged[payloadKey] = nested.DeepClone();

        var result = entries.Save(path, id, new ContentEntriesService.Entry
        {
            Name = body["name"]?.GetValue<string>() ?? "",
            StartDate = body["startDate"]?.GetValue<string>() ?? "",
            EndDate = body["endDate"]?.GetValue<string>() ?? "",
            Raw = merged.ToJsonString()
        });

        return ToResult(result);
    }

    private static async Task<JsonObject?> ReadJsonBody(HttpContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.Body);
            var text = await reader.ReadToEndAsync(context.RequestAborted);
            return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int AsInt(JsonNode? node) => node == null ? 0 : (int?)node ?? 0;
}

/// <summary>三类内容配置的键与文件路径（静态后台接口共用）。</summary>
internal static class ContentKinds
{
    public sealed record Kind(string Key, string Path);

    public static readonly Kind[] All =
    {
        new("frontpage", ContentEntriesService.FrontpagePath),
        new("skirmish", ContentEntriesService.SkirmishPath),
        new("knockout", ContentEntriesService.KnockoutPath)
    };
}
