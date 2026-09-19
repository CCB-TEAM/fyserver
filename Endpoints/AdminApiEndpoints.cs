using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
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
    private static readonly object SystemSettingsLock = new();
    public static IEndpointRouteBuilder MapAdminApiEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/admin/api");

        // ---------------------------------------------------------- 会话（静态页登录用）
        api.MapGet("/session", (HttpContext context, AdminAccountService account) =>
        {
            var loopback = ClientAddress.IsLoopback(context);
            var actor = account.GetSessionAccount(context.Request.Cookies[AdminAccountService.CookieName]);
            var permissions = new JsonArray();
            if (actor != null)
                foreach (var permission in AdminAccountService.AvailablePermissions.Where(p => account.HasPermission(actor, p)))
                    permissions.Add(JsonValue.Create(permission));

            return Results.Text(new JsonObject
            {
                ["authorized"] = actor != null,
                ["loopback"] = loopback,
                ["initialized"] = account.IsInitialized,
                ["username"] = actor?.Username,
                ["isOwner"] = actor?.IsOwner ?? false,
                ["permissions"] = permissions,
                ["hasSession"] = actor != null
            }.ToJsonString(), "application/json");
        });

        api.MapPost("/setup", async (HttpContext context, AdminAccountService account, AdminAuditLogService audit) =>
        {
            if (!ClientAddress.IsLoopback(context))
                return Results.Text(new JsonObject { ["ok"] = false, ["message"] = "首次设置仅允许在服务器本机完成" }.ToJsonString(), "application/json", statusCode: 403);
            var body = await ReadJsonBody(context);
            var result = account.Initialize(body?["username"]?.GetValue<string>(), body?["password"]?.GetValue<string>());
            if (!result.Ok)
                return Results.Text(new JsonObject { ["ok"] = false, ["message"] = result.Message }.ToJsonString(), "application/json", statusCode: 400);
            var owner = account.GetByUsername(body?["username"]?.GetValue<string>()?.Trim())!;
            account.RecordSuccessfulLogin(owner.Id, context.Connection.RemoteIpAddress?.ToString());
            SetSessionCookie(context, account.CreateSession(owner));
            audit.Record(body?["username"]?.GetValue<string>() ?? "", "SETUP", "/admin/api/setup", 200, context.Connection.RemoteIpAddress?.ToString());
            audit.RecordLogin(owner.Username, true, context.Connection.RemoteIpAddress?.ToString());
            return Results.Text(new JsonObject { ["ok"] = true, ["message"] = result.Message }.ToJsonString(), "application/json");
        });

        api.MapPost("/login", async (HttpContext context, AdminAccountService account, AdminAuditLogService audit) =>
        {
            var body = await ReadJsonBody(context);
            var username = body?["username"]?.GetValue<string>();
            var password = body?["password"]?.GetValue<string>();

            if (!account.IsInitialized)
                return Results.Text(new JsonObject { ["ok"] = false, ["message"] = "请先完成管理员初始设置" }.ToJsonString(), "application/json", statusCode: 428);
            var actor = account.Authenticate(username, password);
            if (actor == null)
            {
                audit.Record(username ?? "", "LOGIN", "/admin/api/login", 401, context.Connection.RemoteIpAddress?.ToString());
                audit.RecordLogin(username ?? "", false, context.Connection.RemoteIpAddress?.ToString());
                return Results.Text(new JsonObject { ["ok"] = false, ["message"] = "用户名或密码不正确" }.ToJsonString(),
                    "application/json", statusCode: StatusCodes.Status401Unauthorized);
            }
            account.RecordSuccessfulLogin(actor.Id, context.Connection.RemoteIpAddress?.ToString());
            SetSessionCookie(context, account.CreateSession(actor));
            audit.Record(actor.Username, "LOGIN", "/admin/api/login", 200, context.Connection.RemoteIpAddress?.ToString());
            audit.RecordLogin(actor.Username, true, context.Connection.RemoteIpAddress?.ToString());

            return Results.Text(new JsonObject { ["ok"] = true, ["message"] = "登录成功" }.ToJsonString(), "application/json");
        });

        api.MapPost("/logout", (HttpContext context, AdminAccountService account) =>
        {
            account.RevokeSession(context.Request.Cookies[AdminAccountService.CookieName]);
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
                ["startedAt"] = process.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                ["uptime"] = $"{(int)uptime.TotalDays} 天 {uptime.Hours} 小时 {uptime.Minutes} 分 {uptime.Seconds} 秒",
                ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                ["performance"] = new JsonObject
                {
                    ["systemCpuPercent"] = metricSnapshot.SystemCpuPercent,
                    ["systemMemoryUsedBytes"] = metricSnapshot.SystemMemoryUsedBytes,
                    ["systemMemoryTotalBytes"] = metricSnapshot.SystemMemoryTotalBytes,
                    ["systemMemoryPercent"] = metricSnapshot.SystemMemoryPercent,
                    ["diskUsedBytes"] = metricSnapshot.DiskUsedBytes,
                    ["diskUsedPercent"] = metricSnapshot.DiskUsedPercent,
                    ["cpuPercent"] = metricSnapshot.CpuPercent,
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

        // ---------------------------------------------------------- 后台用户与审计日志
        api.MapGet("/accounts", (AdminAccountService accounts) =>
        {
            var array = new JsonArray();
            foreach (var user in accounts.ListAccounts())
            {
                array.Add(AccountJson(user, accounts));
            }
            return Results.Text(new JsonObject { ["accounts"] = array }.ToJsonString(), "application/json");
        });
        api.MapGet("/accounts/{id}", (string id, AdminAccountService accounts) =>
        {
            var user = accounts.ListAccounts().FirstOrDefault(a => a.Id == id);
            return user == null ? Results.NotFound() : Results.Text(AccountJson(user, accounts).ToJsonString(), "application/json");
        });
        api.MapGet("/accounts/{id}/actions", (string id, int? page, AdminAccountService accounts, AdminAuditLogService audit) =>
        {
            var user = accounts.ListAccounts().FirstOrDefault(a => a.Id == id);
            if (user == null) return Results.NotFound();
            var history = audit.ActionsFor(user.Username, page ?? 1);
            return Results.Text(new JsonObject { ["entries"] = history.Entries, ["total"] = history.Total, ["page"] = page ?? 1 }.ToJsonString(), "application/json");
        });
        api.MapGet("/accounts/{id}/logins", (string id, int? page, AdminAccountService accounts, AdminAuditLogService audit) =>
        {
            var user = accounts.ListAccounts().FirstOrDefault(a => a.Id == id);
            if (user == null) return Results.NotFound();
            var history = audit.LoginsFor(user.Username, page ?? 1);
            return Results.Text(new JsonObject { ["entries"] = history.Entries, ["total"] = history.Total, ["page"] = page ?? 1 }.ToJsonString(), "application/json");
        });
        api.MapPost("/accounts", async (HttpContext context, AdminAccountService accounts) =>
        {
            var body = await ReadJsonBody(context);
            if (body == null) return BadJson();
            if (!TryPermissions(body["permissions"], out var permissions)) return SystemSettingsError("权限列表无效");
            var actor = (AdminAccount)context.Items["adminActor"]!;
            return ToResult(accounts.CreateAccount(actor, body["username"]?.GetValue<string>(), body["password"]?.GetValue<string>(), permissions));
        });
        api.MapPut("/accounts/{id}", async (string id, HttpContext context, AdminAccountService accounts) =>
        {
            var body = await ReadJsonBody(context);
            if (body == null) return BadJson();
            if (!TryPermissions(body["permissions"], out var permissions)) return SystemSettingsError("权限列表无效");
            bool? enabled = null;
            if (body["enabled"] is JsonValue flag)
            {
                if (!flag.TryGetValue<bool>(out var value)) return SystemSettingsError("enabled 必须是布尔值");
                enabled = value;
            }
            var actor = (AdminAccount)context.Items["adminActor"]!;
            return ToResult(accounts.UpdateAccount(actor, id, enabled, body.ContainsKey("permissions") ? permissions : null,
                body["password"]?.GetValue<string>()));
        });
        api.MapDelete("/accounts/{id}", (string id, AdminAccountService accounts) => ToResult(accounts.DeleteAccount(id)));
        api.MapPost("/accounts/{id}/reset-password", async (string id, HttpContext context, AdminAccountService accounts) =>
        {
            var body = await ReadJsonBody(context);
            if (body == null) return BadJson();
            var actor = (AdminAccount)context.Items["adminActor"]!;
            return ToResult(accounts.ResetPassword(actor, id, body["newPassword"]?.GetValue<string>(), body["currentPassword"]?.GetValue<string>()));
        });
        api.MapGet("/audit-logs", (AdminAuditLogService audit) =>
            Results.Text(new JsonObject { ["entries"] = audit.Recent() }.ToJsonString(), "application/json"));

        // 宿主网络地址保存在 setting.json。保存不修改正在运行的监听器，重启后生效。
        api.MapGet("/system-settings", (ServerOptions active) =>
        {
            try
            {
                lock (SystemSettingsLock)
                {
                    var saved = JsonNode.Parse(File.ReadAllText("setting.json")) as JsonObject ?? new JsonObject();
                    var listenPort = (int?)saved["portHttp"] ?? active.portHttp;
                    var publicPort = (int?)saved["publicPortHttp"] ?? 0;
                    var payload = new JsonObject
                    {
                        ["listenIp"] = (string?)saved["listenIp"] ?? "0.0.0.0",
                        ["listenPort"] = listenPort,
                        ["publicIp"] = (string?)saved["ip"] ?? active.ip,
                        ["publicPort"] = publicPort > 0 ? publicPort : listenPort,
                        ["activeListenIp"] = active.listenIp,
                        ["activeListenPort"] = active.portHttp,
                        ["activePublicIp"] = active.ip,
                        ["activePublicPort"] = active.publicPortHttp > 0 ? active.publicPortHttp : active.portHttp
                    };
                    payload["restartRequired"] = (string?)payload["listenIp"] != active.listenIp ||
                        (int?)payload["listenPort"] != active.portHttp || (string?)payload["publicIp"] != active.ip ||
                        (int?)payload["publicPort"] != (active.publicPortHttp > 0 ? active.publicPortHttp : active.portHttp);
                    return Results.Text(payload.ToJsonString(), "application/json");
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                return Results.Text(new JsonObject { ["message"] = "读取 setting.json 失败：" + ex.Message }.ToJsonString(), "application/json", statusCode: 500);
            }
        });

        api.MapPut("/system-settings", async (HttpContext context) =>
        {
            if (context.Request.ContentLength > 4096) return Results.StatusCode(413);
            var body = await ReadJsonBody(context);
            if (body == null) return BadJson();
            var listenIp = body["listenIp"]?.ToString()?.Trim() ?? "";
            var publicIp = body["publicIp"]?.ToString()?.Trim() ?? "";
            if (!IPAddress.TryParse(listenIp, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return SystemSettingsError("监听 IP 必须是 IPv4 地址，例如 0.0.0.0 或 127.0.0.1");
            if (publicIp.Length > 253 || publicIp.Contains("://") ||
                Uri.CheckHostName(publicIp) is not (UriHostNameType.Dns or UriHostNameType.IPv4) || publicIp == "0.0.0.0")
                return SystemSettingsError("对外 IP 必须是可供客户端访问的 IPv4 地址或域名，不能是 0.0.0.0");
            if (!int.TryParse(body["listenPort"]?.ToString(), out var listenPort) || listenPort is < 1 or > 65535 ||
                !int.TryParse(body["publicPort"]?.ToString(), out var publicPort) || publicPort is < 1 or > 65535)
                return SystemSettingsError("端口必须在 1–65535 之间");
            try
            {
                lock (SystemSettingsLock)
                {
                    const string path = "setting.json";
                    var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                    if (root == null) return SystemSettingsError("setting.json 根节点必须是对象");
                    root["listenIp"] = listenIp;
                    root["portHttp"] = listenPort;
                    root["ip"] = publicIp;
                    root["publicPortHttp"] = publicPort;
                    var temp = path + ".tmp";
                    try
                    {
                        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        File.Copy(path, path + ".bak", true);
                        File.Move(temp, path, true);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                return Results.Text("{\"ok\":true,\"message\":\"已保存到 setting.json；重启服务器后生效\",\"restartRequired\":true}", "application/json");
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return SystemSettingsError("保存 setting.json 失败：" + ex.Message, 500);
            }
        });

        // 游戏客户端 /session 的 server_options（不是监听端口等宿主 setting.json）。
        api.MapGet("/server-config", (ClientServerConfigService config) =>
        {
            JsonNode schema;
            try { schema = JsonNode.Parse(config.ReadSchema()) ?? new JsonObject(); }
            catch (JsonException) { schema = new JsonObject(); }
            return Results.Text(new JsonObject
            {
                ["raw"] = config.ReadTemplate(),
                ["schema"] = schema["entries"]?.DeepClone() ?? new JsonArray(),
                ["flags"] = config.ReadFlags(),
                ["comments"] = config.ReadComments()
            }.ToJsonString(), "application/json");
        });

        api.MapPut("/server-config", async (HttpContext context, ClientServerConfigService config) =>
        {
            if (context.Request.ContentLength > 140_000)
                return Results.Text(new JsonObject { ["ok"] = false, ["message"] = "请求体过大" }.ToJsonString(), "application/json", statusCode: 413);
            var body = await ReadJsonBody(context);
            if (body == null) return BadJson();
            var raw = body["raw"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            var result = config.Save(raw);
            return Results.Text(new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message }.ToJsonString(),
                "application/json", statusCode: result.Ok ? 200 : 400);
        });

        api.MapPut("/server-config/item", async (HttpContext context, ClientServerConfigService config) =>
        {
            var body = await ReadJsonBody(context);
            if (body == null) return BadJson();
            var result = config.Upsert(body["key"]?.GetValue<string>(), body["value"]?.DeepClone(),
                body["description"]?.GetValue<string>());
            return Results.Text(new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message }.ToJsonString(),
                "application/json", statusCode: result.Ok ? 200 : 400);
        });

        api.MapDelete("/server-config/item/{key}", (string key, ClientServerConfigService config) =>
        {
            var result = config.Delete(key);
            return Results.Text(new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message }.ToJsonString(),
                "application/json", statusCode: result.Ok ? 200 : 400);
        });

        api.MapPut("/server-config/item/{key}/enabled", async (string key, HttpContext context, ClientServerConfigService config) =>
        {
            var body = await ReadJsonBody(context);
            if (body == null) return BadJson();
            var enabled = body["enabled"]?.GetValue<bool>() ?? false;
            var result = config.SetEnabled(key, enabled);
            return Results.Text(new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message }.ToJsonString(),
                "application/json", statusCode: result.Ok ? 200 : 400);
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
                    ["gold"] = user.Gold,
                    ["diamonds"] = user.Diamonds,
                    ["dust"] = user.Dust,
                    ["banned"] = user.IsBanActive(DateTime.UtcNow),
                    ["banExpiresAt"] = user.BanExpiresAt?.ToString("O"),
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
                ["banned"] = user.IsBanActive(DateTime.UtcNow),
                ["banReason"] = user.BanReason,
                ["banDescription"] = user.BanDescription,
                ["banExpiresAt"] = user.BanExpiresAt?.ToString("O"),
                ["bannedAt"] = user.BannedAt?.ToString("O"),
                ["lastLoginAt"] = user.LastLoginAt?.ToString("O"),
                ["online"] = hub.TryGetClient(id, out _),
                ["createdAt"] = user.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                ["updatedAt"] = user.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                ["equippedItemCount"] = user.EquippedItem.Count,
                ["gold"] = user.Gold,
                ["diamonds"] = user.Diamonds,
                ["dust"] = user.Dust,
                ["packCount"] = user.Packs.Count,
                ["decks"] = decks,
                ["activeMatchId"] = match?.MatchId
            };

            return Results.Text(payload.ToJsonString(), "application/json");
        });

        api.MapPost("/users/{id:int}/ban", async (int id, HttpContext context, AdminUserService adminUsers) =>
        {
            var body = await ReadJsonBody(context);
            if (body == null) return BadJson();
            var reason = body["reason"]?.GetValue<string>();
            if (reason?.Length > 500) return SystemSettingsError("封禁原因不能超过 500 字");
            DateTime? expiresAt = null;
            var expiryText = body["expiresAt"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(expiryText))
            {
                if (!DateTimeOffset.TryParse(expiryText, out var parsed))
                    return SystemSettingsError("解封时间格式不正确");
                expiresAt = parsed.UtcDateTime;
            }
            return ToResult(await adminUsers.BanAsync(id, reason, expiresAt));
        });
        api.MapPut("/users/{id:int}/profile", async (int id, HttpContext context, UserStoreService users) =>
        {
            var body = await ReadJsonBody(context);
            if (body == null) return BadJson();
            var user = await users.GetByIdAsync(id);
            if (user == null) return Results.NotFound();
            var name = body["name"]?.GetValue<string>()?.Trim() ?? "";
            var locale = body["locale"]?.GetValue<string>()?.Trim() ?? "";
            var tag = AsInt(body["tag"]);
            if (name.Length is < 1 or > 32 || locale.Length is < 2 or > 20 || tag is < 0 or > 9999)
                return SystemSettingsError("昵称、语言或 Tag 格式不正确");
            user.Name = name;
            user.Locale = locale;
            user.Tag = tag;
            await users.SaveUserAsync(user);
            return ToResult((true, "玩家资料已保存"));
        });
        api.MapPut("/users/{id:int}/wallet", async (int id, HttpContext context, UserStoreService users) =>
        {
            var body = await ReadJsonBody(context);
            if (body == null) return BadJson();
            if (!TryNonNegativeInt(body["gold"], out var gold) ||
                !TryNonNegativeInt(body["diamonds"], out var diamonds))
                return SystemSettingsError("金币和钻石必须是非负整数");
            var result = await users.WithUserLockAsync(id, async user =>
            {
                user.Gold = gold;
                user.Diamonds = diamonds;
                await users.SaveUserAsync(user);
                users.RecordIncremental();
                return (IResult)ToResult((true, "玩家货币已保存"));
            });
            return result ?? Results.NotFound();
        });
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
                        ["endDate"] = entry.EndDate,
                        ["isPublished"] = entry.IsPublished,
                        ["isTargeted"] = entry.IsTargeted,
                        ["status"] = entry.Status,
                        ["type"] = entry.Type,
                        ["priority"] = entry.Priority,
                        ["slot"] = entry.Slot
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

            api.MapGet($"/content/{kind.Key}/export", (ContentEntriesService entries) =>
                Results.Text(entries.ReadRaw(path), "application/json"));

            api.MapPost($"/content/{kind.Key}/import", async (HttpContext context, ContentEntriesService entries) =>
            {
                if (context.Request.ContentLength > 1_100_000)
                    return Results.Text("{\"ok\":false,\"message\":\"文件超过 1 MB\"}", "application/json", statusCode: 413);
                var body = await ReadJsonBody(context);
                if (body == null) return BadJson();
                return ToResult(entries.SaveRaw(path, body["raw"]?.GetValue<string>() ?? ""));
            });
        }

        api.MapPut("/content/frontpage/{id:int}/published", async (int id, HttpContext context, ContentEntriesService entries) =>
        {
            var body = await ReadJsonBody(context);
            if (body == null) return BadJson();
            if (body["published"] is not JsonValue flag || !flag.TryGetValue<bool>(out var published))
                return Results.Text("{\"ok\":false,\"message\":\"published 必须是布尔值\"}", "application/json", statusCode: 400);
            return ToResult(entries.SetFrontpagePublished(id, published));
        });

        api.MapPost("/content/frontpage/upload-image", async (HttpContext context, ServerOptions options) =>
        {
            const int maxBytes = 5 * 1024 * 1024;
            if (context.Request.ContentLength > maxBytes + 16_384)
                return SystemSettingsError("图片不能超过 5 MB", 413);
            if (!context.Request.HasFormContentType)
                return SystemSettingsError("请使用 multipart/form-data 上传图片");
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var file = form.Files.GetFile("image");
            if (file == null || file.Length == 0 || file.Length > maxBytes)
                return SystemSettingsError("请选择不超过 5 MB 的图片");
            try
            {
                await using var input = file.OpenReadStream();
                using var buffer = new MemoryStream();
                await input.CopyToAsync(buffer, context.RequestAborted);
                var bytes = buffer.ToArray();
                if (bytes.Length > maxBytes) return SystemSettingsError("图片不能超过 5 MB", 413);
                var extension = ImageExtension(bytes);
                if (extension == null) return SystemSettingsError("只支持 PNG、JPEG、WebP 和 GIF 位图");
                var directory = Path.Combine("wwwroot", "admin-ui", "uploads");
                Directory.CreateDirectory(directory);
                var filename = Guid.NewGuid().ToString("N") + extension;
                await File.WriteAllBytesAsync(Path.Combine(directory, filename), bytes, context.RequestAborted);
                return Results.Text(new JsonObject { ["ok"] = true, ["url"] = options.GetAddressHttpR() + "/admin-ui/uploads/" + filename }.ToJsonString(), "application/json");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return SystemSettingsError("图片保存失败：" + ex.Message, 500);
            }
        });

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
        if (isFrontpage)
        {
            if (body["isPublished"] is JsonValue published && published.TryGetValue<bool>(out var isPublished))
                merged["isPublished"] = isPublished;
            if (body["isTargeted"] is JsonValue targeted && targeted.TryGetValue<bool>(out var isTargeted))
                merged["isTargeted"] = isTargeted;
        }

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

    private static bool TryNonNegativeInt(JsonNode? node, out int value)
    {
        value = 0;
        return node is JsonValue json && json.TryGetValue<int>(out value) && value >= 0;
    }

    private static bool TryPermissions(JsonNode? node, out List<string> permissions)
    {
        permissions = [];
        if (node == null) return true;
        if (node is not JsonArray array) return false;
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var permission) || permission == null)
                return false;
            permissions.Add(permission);
        }
        return true;
    }

    private static JsonObject AccountJson(AdminAccount account, AdminAccountService accounts)
    {
        var permissions = new JsonArray();
        foreach (var permission in account.Permissions.Order()) permissions.Add(JsonValue.Create(permission));
        var presence = accounts.PresenceFor(account.Id);
        return new JsonObject
        {
            ["id"] = account.Id, ["username"] = account.Username,
            ["isOwner"] = account.IsOwner, ["enabled"] = account.Enabled,
            ["permissions"] = permissions, ["createdAt"] = account.CreatedAt.ToString("O"),
            ["lastLoginAt"] = account.LastLoginAt?.ToString("O"), ["lastLoginIp"] = account.LastLoginIp,
            ["online"] = presence.Online, ["activeSessions"] = presence.Sessions,
            ["lastSeenAt"] = presence.LastSeenAt?.ToString("O")
        };
    }

    private static string? ImageExtension(byte[] data)
    {
        if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
        if (data.Length >= 3 && data[0] == 255 && data[1] == 216 && data[2] == 255) return ".jpg";
        if (data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return ".webp";
        if (data.Length >= 6 && (data.AsSpan(0, 6).SequenceEqual("GIF87a"u8) || data.AsSpan(0, 6).SequenceEqual("GIF89a"u8))) return ".gif";
        return null;
    }

    private static IResult SystemSettingsError(string message, int status = 400) => Results.Text(
        new JsonObject { ["ok"] = false, ["message"] = message }.ToJsonString(),
        "application/json", statusCode: status);
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
