using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using fyserver.Models;
using fyserver.Serialization;
using fyserver.Services;
using Microsoft.AspNetCore.Mvc;

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
    private static readonly (string CardType, string Rarity)[] WildcardTypes =
    [
        ("card_wildcard_standard", "Standard"),
        ("card_wildcard_limited", "Limited"),
        ("card_wildcard_special", "Special"),
        ("card_wildcard_elite", "Elite")
    ];
    public static IEndpointRouteBuilder MapAdminApiEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/admin/api");

        // ---------------------------------------------------------- 会话（静态页登录用）
        api.MapGet("/session", (HttpContext context, AdminAccountService account, UserStoreService users,
            UserDatabaseConfigurationService databaseConfiguration) =>
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
                ["avatarUrl"] = actor?.AvatarUrl,
                ["isOwner"] = actor?.IsOwner ?? false,
                ["permissions"] = permissions,
                ["hasSession"] = actor != null,
                ["serverReady"] = users.IsReady,
                ["databaseConfigured"] = databaseConfiguration.IsConfigured,
                ["databaseProvider"] = users.Provider,
                ["databaseError"] = users.LastInitializationError
            }.ToJsonString(), "application/json");
        });

        api.MapPost("/setup", async (HttpContext context, AdminAccountService account, AdminAuditLogService audit,
            UserStoreService users, UserDatabaseConfigurationService databaseConfiguration) =>
        {
            if (!ClientAddress.IsLoopback(context))
                return Results.Text(new JsonObject { ["ok"] = false, ["message"] = "首次设置仅允许在服务器本机完成" }.ToJsonString(), "application/json", statusCode: 403);
            if (!databaseConfiguration.IsConfigured || !users.IsReady)
                return Results.Text(new JsonObject { ["ok"] = false, ["message"] = "请先选择并初始化玩家数据存储，再创建 Owner 账号" }.ToJsonString(), "application/json", statusCode: 428);
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

        api.MapGet("/me", (HttpContext context, AdminAccountService accounts) =>
        {
            var actor = (AdminAccount)context.Items["adminActor"]!;
            var current = accounts.ListAccounts().FirstOrDefault(account => account.Id == actor.Id);
            return current == null ? Results.NotFound() : Results.Text(AccountJson(current, accounts).ToJsonString(), "application/json");
        });
        api.MapPut("/me/profile", async (HttpContext context, AdminAccountService accounts) =>
        {
            var body = await ReadJsonBody(context);
            if (body == null) return BadJson();
            var actor = (AdminAccount)context.Items["adminActor"]!;
            return ToResult(accounts.UpdateOwnUsername(actor, body["currentPassword"]?.GetValue<string>(), body["username"]?.GetValue<string>()));
        });
        api.MapPost("/me/password", async (HttpContext context, AdminAccountService accounts) =>
        {
            var body = await ReadJsonBody(context);
            if (body == null) return BadJson();
            var actor = (AdminAccount)context.Items["adminActor"]!;
            return ToResult(accounts.ChangeOwnPassword(actor, body["currentPassword"]?.GetValue<string>(), body["newPassword"]?.GetValue<string>()));
        });
        api.MapPost("/me/avatar", async (HttpContext context, AdminAccountService accounts, AppDataStoreService appData) =>
        {
            const int maxBytes = 1_500_000;
            if (context.Request.ContentLength > maxBytes + 32_768) return SystemSettingsError("头像文件超过 1.5 MB", 413);
            if (!context.Request.HasFormContentType) return SystemSettingsError("请选择裁剪后的 PNG 头像");
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var file = form.Files.GetFile("avatar");
            if (file == null || file.Length is <= 0 or > maxBytes) return SystemSettingsError("头像文件无效或超过 1.5 MB", 413);
            await using var input = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, context.RequestAborted);
            var image = buffer.ToArray();
            if (image.Length < 24 || !image.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                !image.AsSpan(12, 4).SequenceEqual("IHDR"u8))
                return SystemSettingsError("头像必须是有效的 PNG 图片");
            var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(16, 4));
            var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(20, 4));
            if (width is < 64 or > 1024 || height != width) return SystemSettingsError("头像必须为 1:1 正方形，边长为 64–1024 像素");
            var filename = Guid.NewGuid().ToString("N") + ".png";
            appData.Set("asset:admin:admin-avatars/" + filename, Convert.ToBase64String(image));
            var avatarUrl = "/admin-ui/uploads/admin-avatars/" + filename;
            var actor = (AdminAccount)context.Items["adminActor"]!;
            if (!accounts.UpdateOwnAvatar(actor, avatarUrl)) return Results.NotFound();
            return Results.Text(new JsonObject { ["ok"] = true, ["message"] = "头像已更新", ["avatarUrl"] = avatarUrl }.ToJsonString(), "application/json");
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
            return Results.Text(new JsonObject { ["ok"] = true, ["message"] = "商店配置已从数据库重新加载" }.ToJsonString(), "application/json");
        });

        // ---------------------------------------------------------- 兑换码（参考 NestJS /admin/redeem）
        api.MapGet("/redeem", (RedeemCodeService redeemCodes) =>
        {
            var list = new JsonArray(redeemCodes.List().Select(code => RedeemCodeService.ToJson(code)).ToArray());
            return Results.Text(new JsonObject { ["codes"] = list }.ToJsonString(), "application/json");
        });
        // 保留 NestJS 后台列表路径的命名兼容（当前静态后台使用 /admin/api/redeem）。
        api.MapGet("/redeem/list", (RedeemCodeService redeemCodes) =>
        {
            var list = new JsonArray(redeemCodes.List().Select(code => RedeemCodeService.ToJson(code)).ToArray());
            return Results.Text(new JsonObject { ["codes"] = list }.ToJsonString(), "application/json");
        });
        api.MapGet("/redeem/{code}", (string code, RedeemCodeService redeemCodes) =>
        {
            var item = redeemCodes.Get(code);
            return item == null ? Results.NotFound() : Results.Text(RedeemCodeService.ToJson(item).ToJsonString(), "application/json");
        });
        api.MapPost("/redeem", async (HttpContext context, RedeemCodeService redeemCodes) =>
        {
            var body = await ReadJsonBody(context);
            if (body == null) return BadJson();
            var code = body["code"]?.GetValue<string>()?.Trim();
            var type = body["type"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(code) || type is not ("single" or "perUser"))
                return SystemSettingsError("code 和 type 必须有效，type 只能是 single 或 perUser");
            if (!RedeemCodeService.TryNormalizeRewards(body["rewards"], out var rewards, out var rewardError))
                return SystemSettingsError(rewardError);
            string? expiresAt = null;
            if (body["expiresAt"] is JsonValue expiry && !string.IsNullOrWhiteSpace(expiry.GetValue<string>()))
            {
                if (!DateTimeOffset.TryParse(expiry.GetValue<string>(), out var parsed))
                    return SystemSettingsError("expiresAt 不是有效日期");
                expiresAt = parsed.ToUniversalTime().ToString("O");
            }
            var result = redeemCodes.Create(code, type, rewards, expiresAt);
            return Results.Text(new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message, ["code"] = result.Code?.Code }.ToJsonString(), "application/json", statusCode: result.Ok ? 201 : 400);
        });
        api.MapDelete("/redeem/{code}", (string code, RedeemCodeService redeemCodes) =>
        {
            var result = redeemCodes.Delete(code);
            return Results.Text(new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message }.ToJsonString(), "application/json", statusCode: result.Ok ? 200 : 404);
        });

        // ---------------------------------------------------------- Patch Pak 管理
        api.MapGet("/patch-paks", (PatchPakService paks) => Results.Text(new JsonObject
        {
            ["algorithm"] = "SHA-256",
            ["patches"] = new JsonArray(paks.List().Select(item => (JsonNode)PatchPakJson(item)).ToArray())
        }.ToJsonString(), "application/json"));
        api.MapPost("/patch-paks", async (HttpContext context, PatchPakService paks) =>
        {
            const long maxRequestBytes = PatchPakService.MaxPakSize + 1024 * 1024;
            if (context.Request.ContentLength > maxRequestBytes) return SystemSettingsError("Pak 文件不能超过 128 MB", 413);
            if (!context.Request.HasFormContentType) return SystemSettingsError("请使用 multipart/form-data 上传 Pak 文件");
            IFormCollection form;
            try { form = await context.Request.ReadFormAsync(context.RequestAborted); }
            catch (InvalidDataException) { return SystemSettingsError("上传请求无效或超过 128 MB", 413); }
            var file = form.Files.GetFile("pak");
            if (file == null) return SystemSettingsError("请选择 .pak 文件");
            if (file.Length <= 0 || file.Length > PatchPakService.MaxPakSize) return SystemSettingsError("Pak 文件必须大于 0 且不超过 128 MB", 413);
            await using var input = file.OpenReadStream();
            var (item, error) = await paks.UploadAsync(file.FileName, form["version"].ToString(), form["description"].ToString(), input, file.Length, context.RequestAborted);
            if (item == null) return SystemSettingsError(error, error.Contains("不超过") ? 413 : 400);
            return Results.Text(new JsonObject { ["ok"] = true, ["message"] = "Pak 已保存并发布", ["patch"] = PatchPakJson(item) }.ToJsonString(), "application/json", statusCode: 201);
        }).WithMetadata(new RequestSizeLimitAttribute(PatchPakService.MaxPakSize + 1024 * 1024),
            new RequestFormLimitsAttribute { MultipartBodyLengthLimit = PatchPakService.MaxPakSize + 1024 * 1024 });
        api.MapDelete("/patch-paks/{id}", (string id, PatchPakService paks) =>
        {
            var removed = paks.Delete(id);
            return Results.Text(new JsonObject { ["ok"] = removed, ["message"] = removed ? "Pak 已删除" : "Pak 不存在" }.ToJsonString(), "application/json", statusCode: removed ? 200 : 404);
        });

        // ---------------------------------------------------------- 玩家数据库初始化
        api.MapGet("/database", (UserDatabaseConfigurationService configuration, UserStoreService users) =>
        {
            var status = configuration.PublicStatus();
            status["ready"] = users.IsReady;
            status["activeProvider"] = users.Provider;
            status["initializationError"] = users.LastInitializationError;
            return Results.Text(status.ToJsonString(), "application/json");
        });

        api.MapPost("/database/configure", async (HttpContext context, UserDatabaseConfigurationService configuration,
            UserStoreService users, MatchHistoryService matchHistory, AdminAccountService accounts) =>
        {
            var actor = accounts.GetSessionAccount(context.Request.Cookies[AdminAccountService.CookieName]);
            var firstRunLoopback = actor == null && ClientAddress.IsLoopback(context) &&
                !accounts.IsInitialized && !configuration.IsConfigured;
            if (!(actor?.IsOwner ?? false) && !firstRunLoopback)
                return JsonResult(false, "只有 Owner 可以配置玩家数据库", 403);

            var body = await ReadJsonBody(context);
            var provider = (body?["provider"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
            if (provider is not ("local" or "mysql" or "postgresql"))
                return JsonResult(false, "数据库类型必须是 local、mysql 或 postgresql", 400);

            UserDatabaseSettings settings;
            if (provider == "local")
            {
                settings = new("local", "", 0, "", "", "", false);
            }
            else
            {
                var host = (body?["host"]?.GetValue<string>() ?? "").Trim();
                var database = (body?["database"]?.GetValue<string>() ?? "").Trim();
                var username = (body?["username"]?.GetValue<string>() ?? "").Trim();
                var port = body?["port"]?.GetValue<int>() ?? (provider == "mysql" ? 3306 : 5432);
                var password = body?["password"]?.GetValue<string>() ?? "";
                var requireSsl = body?["requireSsl"]?.GetValue<bool>() ?? true;
                if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(username))
                    return JsonResult(false, "主机、数据库名和用户名不能为空", 400);
                if (host.Length > 253 || database.Length > 128 || username.Length > 128 || password.Length > 1024 || port is < 1 or > 65535)
                    return JsonResult(false, "数据库连接参数无效或过长", 400);
                if (password.Length == 0)
                {
                    try
                    {
                        var current = configuration.Load();
                        if (current?.Provider == provider && current.Host == host && current.Port == port &&
                            current.Database == database && current.Username == username)
                            password = current.Password;
                    }
                    catch { /* 损坏配置可由 Owner 直接覆盖修复。 */ }
                }
                settings = new(provider, host, port, database, username, password, requireSsl);
            }

            var result = await users.ConfigureAsync(settings, context.RequestAborted);
            if (result.Ok)
            {
                accounts.ReloadFromStore();
                try { await matchHistory.InitializeAsync(context.RequestAborted); }
                catch (Exception ex) { return JsonResult(false, "玩家数据库已连接，但对局历史表初始化失败：" + ex.GetBaseException().Message, 500); }
            }
            return JsonResult(result.Ok, result.Message, result.Ok ? 200 : 400);
        });

        // ---------------------------------------------------------- 商店编辑器
        api.MapGet("/store", (StoreConfigService store) =>
        {
            return Results.Text(new JsonObject
            {
                ["ok"] = true,
                ["storage"] = "database",
                ["config"] = StoreConfigNode(store.GetStoreConfig())
            }.ToJsonString(), "application/json");
        });
        api.MapPut("/store", async (HttpContext context, StoreConfigService store) =>
        {
            if (context.Request.ContentLength is > 2_000_000)
                return StoreError("商店配置超过 2 MB", 413);
            var body = await ReadJsonBody(context);
            if (body == null) return BadJson();
            JsonObject? configNode = body["config"] as JsonObject;
            if (configNode == null && body["raw"] is JsonValue rawValue && rawValue.TryGetValue<string>(out var raw))
            {
                try { configNode = JsonNode.Parse(raw) as JsonObject; }
                catch (JsonException ex) { return StoreError("raw JSON 格式错误：" + ex.Message); }
            }
            configNode ??= body;
            try
            {
                var config = JsonSerializer.Deserialize(configNode.ToJsonString(), ConfigJsonContext.Default.StoreConfig);
                var validation = ValidateStoreConfig(config);
                if (config == null || validation != null) return StoreError(validation ?? "商店配置格式无效");
                var result = store.Save(config);
                return Results.Text(new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message, ["config"] = StoreConfigNode(config) }.ToJsonString(),
                    "application/json", statusCode: result.Ok ? 200 : 500);
            }
            catch (JsonException ex)
            {
                return StoreError("商店配置格式无效：" + ex.Message);
            }
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
            var history = audit.ActionsFor(user.PreviousUsernames.Append(user.Username), page ?? 1);
            return Results.Text(new JsonObject { ["entries"] = history.Entries, ["total"] = history.Total, ["page"] = page ?? 1 }.ToJsonString(), "application/json");
        });
        api.MapGet("/accounts/{id}/logins", (string id, int? page, AdminAccountService accounts, AdminAuditLogService audit) =>
        {
            var user = accounts.ListAccounts().FirstOrDefault(a => a.Id == id);
            if (user == null) return Results.NotFound();
            var history = audit.LoginsFor(user.PreviousUsernames.Append(user.Username), page ?? 1);
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

        // 宿主网络地址保存在数据库。保存不修改正在运行的监听器，重启后生效。
        api.MapGet("/system-settings/match-retention", (MatchHistoryService history) =>
        {
            var settings = history.GetRetentionSettings();
            return Results.Text(new JsonObject
            {
                ["mode"] = settings.Mode,
                ["keepCount"] = settings.KeepCount,
                ["keepDays"] = settings.KeepDays,
                ["cleanupDayUtc"] = settings.CleanupDayUtc,
                ["cleanupHourUtc"] = settings.CleanupHourUtc
            }.ToJsonString(), "application/json");
        });

        api.MapPut("/system-settings/match-retention", async (HttpContext context, MatchHistoryService history) =>
        {
            var body = await ReadJsonBody(context);
            if (body == null) return BadJson();
            var mode = body["mode"]?.GetValue<string>();
            if (mode is not ("age" or "count")) return SystemSettingsError("保留模式必须是 age 或 count");
            if (!int.TryParse(body["keepCount"]?.ToString(), out var keepCount) || keepCount is < 1 or > 1_000_000)
                return SystemSettingsError("保留对局数量必须在 1 到 1,000,000 之间");
            if (!int.TryParse(body["keepDays"]?.ToString(), out var keepDays) || keepDays is < 1 or > 3650)
                return SystemSettingsError("保留天数必须在 1 到 3650 之间");
            if (!int.TryParse(body["cleanupDayUtc"]?.ToString(), out var cleanupDay) || cleanupDay is < 0 or > 6 ||
                !int.TryParse(body["cleanupHourUtc"]?.ToString(), out var cleanupHour) || cleanupHour is < 0 or > 23)
                return SystemSettingsError("每周清理时间无效");

            history.SaveRetentionSettings(new MatchRetentionSettings(mode, keepCount, keepDays, cleanupDay, cleanupHour));
            return Results.Text("{\"ok\":true,\"message\":\"对局保留策略已保存，每周按 UTC 计划清理\"}", "application/json");
        });

        // 卡牌目录存于所选玩家数据库；JSON 种子只在数据库中尚无目录时导入一次。
        api.MapGet("/cards", (HttpContext context, CardCatalogService catalog) =>
        {
            var query = context.Request.Query;
            int? ReadOptionalInt(string key) => int.TryParse(query[key], out var value) ? value : null;
            var page = int.TryParse(query["page"], out var parsedPage) ? parsedPage : 1;
            var pageSize = int.TryParse(query["pageSize"], out var parsedSize) ? parsedSize : 50;
            return Results.Text(catalog.Query(query["q"], query["cardSet"], query["type"], ReadOptionalInt("minKredits"),
                ReadOptionalInt("maxKredits"), page, pageSize).ToJsonString(), "application/json");
        });

        api.MapPut("/cards/defaults", async (HttpContext context, CardCatalogService catalog) =>
        {
            var body = await ReadJsonBody(context);
            if (body == null) return BadJson();
            var count = AsInt(body["count"]);
            var result = catalog.AddFilteredCardsToDefaults(body["q"]?.GetValue<string>(), body["cardSet"]?.GetValue<string>(),
                body["type"]?.GetValue<string>(), body["minKredits"] == null ? null : AsInt(body["minKredits"]),
                body["maxKredits"] == null ? null : AsInt(body["maxKredits"]), count);
            return Results.Text(new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message, ["added"] = result.Added }.ToJsonString(),
                "application/json", statusCode: result.Ok ? 200 : 400);
        });

        api.MapPut("/cards/{id}", async (string id, HttpContext context, CardCatalogService catalog) =>
        {
            var body = await ReadJsonBody(context);
            if (body == null) return BadJson();
            var result = catalog.UpdateCardMetadata(id, body);
            return ToResult(result);
        });

        api.MapGet("/system-settings/player-library", (CardCatalogService catalog) =>
            Results.Text(catalog.GetInitialLibraryPolicy().ToJsonString(), "application/json"));

        api.MapPut("/system-settings/player-library", async (HttpContext context, CardCatalogService catalog) =>
        {
            var body = await ReadJsonBody(context);
            if (body == null) return BadJson();
            return ToResult(catalog.SaveInitialLibraryPolicy(body));
        });

        api.MapGet("/system-settings", (ServerOptions active, AppDataStoreService appData) =>
        {
            try
            {
                lock (SystemSettingsLock)
                {
                    var saved = JsonNode.Parse(appData.Get("server:settings") ?? JsonSerializer.Serialize(active, ConfigJsonContext.Default.ServerOptions)) as JsonObject ?? new JsonObject();
                    var listenPort = (int?)saved["portHttp"] ?? active.portHttp;
                    var publicPort = (int?)saved["publicPortHttp"];
                    if (publicPort is <= 0) publicPort = null;
                    var publicScheme = string.Equals((string?)saved["publicScheme"], "https", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
                    var payload = new JsonObject
                    {
                        ["listenIp"] = (string?)saved["listenIp"] ?? "0.0.0.0",
                        ["listenPort"] = listenPort,
                        ["publicIp"] = (string?)saved["ip"] ?? active.ip,
                        ["publicPort"] = publicPort,
                        ["publicScheme"] = publicScheme,
                        ["activeListenIp"] = active.listenIp,
                        ["activeListenPort"] = active.portHttp,
                        ["activePublicIp"] = active.ip,
                        ["activePublicPort"] = active.publicPortHttp is > 0 ? active.publicPortHttp : null,
                        ["activePublicScheme"] = string.Equals(active.publicScheme, "https", StringComparison.OrdinalIgnoreCase) ? "https" : "http"
                    };
                    payload["restartRequired"] = (string?)payload["listenIp"] != active.listenIp ||
                        (int?)payload["listenPort"] != active.portHttp || (string?)payload["publicIp"] != active.ip ||
                        (int?)payload["publicPort"] != (active.publicPortHttp is > 0 ? active.publicPortHttp : null) ||
                        (string?)payload["publicScheme"] != (string?)payload["activePublicScheme"];
                    return Results.Text(payload.ToJsonString(), "application/json");
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.Data.Common.DbException)
            {
                return Results.Text(new JsonObject { ["message"] = "读取数据库设置失败：" + ex.Message }.ToJsonString(), "application/json", statusCode: 500);
            }
        });

        api.MapPut("/system-settings", async (HttpContext context, AppDataStoreService appData, ServerOptions active) =>
        {
            if (context.Request.ContentLength > 4096) return Results.StatusCode(413);
            var body = await ReadJsonBody(context);
            if (body == null) return BadJson();
            var listenIp = body["listenIp"]?.ToString()?.Trim() ?? "";
            var publicIp = body["publicIp"]?.ToString()?.Trim() ?? "";
            var publicScheme = body["publicScheme"]?.ToString()?.Trim().ToLowerInvariant() ?? "http";
            if (!IPAddress.TryParse(listenIp, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return SystemSettingsError("监听 IP 必须是 IPv4 地址，例如 0.0.0.0 或 127.0.0.1");
            if (publicIp.Length > 253 || publicIp.Contains("://") ||
                Uri.CheckHostName(publicIp) is not (UriHostNameType.Dns or UriHostNameType.IPv4) || publicIp == "0.0.0.0")
                return SystemSettingsError("对外 IP 必须是可供客户端访问的 IPv4 地址或域名，不能是 0.0.0.0");
            if (publicScheme is not ("http" or "https"))
                return SystemSettingsError("对外协议只能选择 http 或 https");
            if (!int.TryParse(body["listenPort"]?.ToString(), out var listenPort) || listenPort is < 1 or > 65535)
                return SystemSettingsError("监听端口必须在 1–65535 之间");
            int? publicPort = null;
            var publicPortText = body["publicPort"]?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(publicPortText))
            {
                if (!int.TryParse(publicPortText, out var parsedPublicPort) || parsedPublicPort is < 1 or > 65535)
                    return SystemSettingsError("对外端口留空即可省略；填写时必须在 1–65535 之间");
                publicPort = parsedPublicPort;
            }
            try
            {
                lock (SystemSettingsLock)
                {
                    var root = JsonNode.Parse(appData.Get("server:settings") ?? JsonSerializer.Serialize(active, ConfigJsonContext.Default.ServerOptions)) as JsonObject;
                    if (root == null) return SystemSettingsError("服务器设置数据格式无效");
                    root["listenIp"] = listenIp;
                    root["portHttp"] = listenPort;
                    root["ip"] = publicIp;
                    root["publicPortHttp"] = publicPort is null ? null : JsonValue.Create(publicPort.Value);
                    root["publicScheme"] = publicScheme;
                    appData.Set("server:settings", root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
                return Results.Text("{\"ok\":true,\"message\":\"已保存到数据库；重启服务器后生效\",\"restartRequired\":true}", "application/json");
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or System.Data.Common.DbException)
            {
                return SystemSettingsError("保存服务器设置到数据库失败：" + ex.Message, 500);
            }
        });

        // 游戏客户端 /session 的 server_options（与监听端口等宿主网络设置分开存储）。
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
                    !user.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                    !$"{user.Name}#{user.Tag:D4}".Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    continue;

                array.Add(new JsonObject
                {
                    ["id"] = user.Id,
                    ["userName"] = user.UserName,
                    ["name"] = user.Name,
                    ["tag"] = user.Tag,
                    ["displayName"] = $"{user.Name}#{user.Tag:D4}",
                    ["deckCount"] = user.Decks.Count,
                    ["gold"] = user.Gold,
                    ["diamonds"] = user.Diamonds,
                    ["dust"] = user.Dust,
                    ["banned"] = user.IsBanActive(DateTime.UtcNow),
                    ["banExpiresAt"] = user.BanExpiresAt?.ToString("O"),
                    ["lastLoginAt"] = user.LastLoginAt?.ToString("O"),
                    ["lastLoginIp"] = user.LastLoginIp,
                    ["lastLoginDevice"] = user.LastLoginDevice,
                    ["online"] = online.Contains(user.Id),
                    ["createdAt"] = user.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                });
            }

            return Results.Text(new JsonObject { ["total"] = all.Count, ["users"] = array }.ToJsonString(), "application/json");
        });

        api.MapGet("/users/{id:int}", async (int id, UserStoreService users, WebSocketHubService hub, MatchManagerService matches, MatchHistoryService history, CancellationToken cancellationToken) =>
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
            var recentMatches = await history.ListAdminAsync(1, 10, null, id, null, cancellationToken);
            var recentMatchItems = new JsonArray();
            foreach (var recent in recentMatches.Matches)
            {
                recentMatchItems.Add(new JsonObject
                {
                    ["matchId"] = recent.MatchId,
                    ["matchType"] = recent.MatchType,
                    ["status"] = recent.Status,
                    ["startedAt"] = recent.StartedAt,
                    ["completedAt"] = recent.CompletedAt,
                    ["leftPlayerId"] = recent.LeftPlayerId,
                    ["leftPlayerName"] = recent.LeftPlayerName,
                    ["leftPlayerTag"] = recent.LeftPlayerTag,
                    ["rightPlayerId"] = recent.RightPlayerId,
                    ["rightPlayerName"] = recent.RightPlayerName,
                    ["rightPlayerTag"] = recent.RightPlayerTag,
                    ["turns"] = recent.Turns,
                    ["actionCount"] = recent.ActionCount,
                    ["winnerSide"] = recent.WinnerSide
                });
            }
            var playerRoles = new JsonArray();
            foreach (var role in PlayerRoleCatalog.Normalize(user.Roles))
                playerRoles.Add(JsonValue.Create(role));
            var availablePlayerRoles = new JsonArray();
            foreach (var role in PlayerRoleCatalog.Available)
                availablePlayerRoles.Add(JsonValue.Create(role));
            user.UserCards ??= new UserCardCollection();
            user.UserCards.Cards ??= [];
            user.CardCollection ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var wildcards = new JsonArray();
            foreach (var wildcard in WildcardTypes)
            {
                var card = user.UserCards.Cards.FirstOrDefault(item => string.Equals(item.CardType, wildcard.CardType, StringComparison.OrdinalIgnoreCase));
                var normalCount = card?.Count ?? user.CardCollection.GetValueOrDefault(wildcard.CardType);
                var goldCount = card?.GoldCardCount ?? user.CardCollection.GetValueOrDefault(wildcard.CardType + "#gold");
                wildcards.Add(new JsonObject
                {
                    ["cardType"] = wildcard.CardType,
                    ["rarity"] = wildcard.Rarity,
                    ["count"] = Math.Max(0, normalCount),
                    ["goldCount"] = Math.Max(0, goldCount)
                });
            }
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
                ["lastLoginIp"] = user.LastLoginIp,
                ["lastLoginDevice"] = user.LastLoginDevice,
                ["online"] = hub.TryGetClient(id, out _),
                ["createdAt"] = user.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                ["updatedAt"] = user.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                ["equippedItemCount"] = user.EquippedItem.Count,
                ["gold"] = user.Gold,
                ["diamonds"] = user.Diamonds,
                ["roles"] = playerRoles,
                ["availableRoles"] = availablePlayerRoles,
                ["wildcards"] = wildcards,
                ["dust"] = user.Dust,
                ["packCount"] = user.Packs.Count,
                ["decks"] = decks,
                ["activeMatchId"] = match?.MatchId,
                ["recentMatches"] = recentMatchItems
            };

            return Results.Text(payload.ToJsonString(), "application/json");
        });

        api.MapPut("/users/{id:int}/roles", async (int id, HttpContext context, UserStoreService users) =>
        {
            var body = await ReadJsonBody(context);
            if (body?["roles"] is not JsonArray roleNodes)
                return SystemSettingsError("roles 必须是数组");

            var roles = new List<string>();
            foreach (var node in roleNodes)
            {
                if (node is not JsonValue value || !value.TryGetValue<string>(out var role) ||
                    string.IsNullOrWhiteSpace(role) || !PlayerRoleCatalog.Available.Contains(role, StringComparer.Ordinal))
                    return SystemSettingsError("角色列表包含无效角色");
                if (!roles.Contains(role, StringComparer.Ordinal)) roles.Add(role);
            }

            var result = await users.WithUserLockAsync<IResult>(id, async user =>
            {
                user.Roles = PlayerRoleCatalog.Normalize(roles);
                await users.SaveUserAsync(user);
                users.RecordIncremental();
                return ToResult((true, "玩家角色已保存"));
            });
            return result ?? Results.NotFound();
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
            user.Locale = locale;
            if (!await users.SavePlayerIdentityAsync(user, name, tag))
                return SystemSettingsError("该昵称与 Tag 已被其他玩家使用");
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
        api.MapPut("/users/{id:int}/wildcards", async (int id, HttpContext context, UserStoreService users) =>
        {
            var body = await ReadJsonBody(context);
            if (body?["wildcards"] is not JsonArray wildcardNodes || wildcardNodes.Count != WildcardTypes.Length)
                return SystemSettingsError("必须提交 Standard、Limited、Special、Elite 四类万能卡数量");

            var requested = new Dictionary<string, (int Count, int GoldCount)>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in wildcardNodes)
            {
                if (node is not JsonObject item || item["cardType"] is not JsonValue cardTypeNode ||
                    !cardTypeNode.TryGetValue<string>(out var cardType) || string.IsNullOrWhiteSpace(cardType) ||
                    !WildcardTypes.Any(wildcard => string.Equals(wildcard.CardType, cardType, StringComparison.OrdinalIgnoreCase)) ||
                    !TryNonNegativeInt(item["count"], out var count) ||
                    !TryNonNegativeInt(item["goldCount"], out var goldCount) ||
                    !requested.TryAdd(cardType, (count, goldCount)))
                    return SystemSettingsError("万能卡类型重复或数量无效；数量必须是非负整数");
            }
            if (WildcardTypes.Any(wildcard => !requested.ContainsKey(wildcard.CardType)))
                return SystemSettingsError("万能卡数据不完整");

            var result = await users.WithUserLockAsync<IResult>(id, async user =>
            {
                user.UserCards ??= new UserCardCollection();
                user.UserCards.Cards ??= [];
                user.CardCollection ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var wildcard in WildcardTypes)
                {
                    var card = user.UserCards.Cards.FirstOrDefault(item => string.Equals(item.CardType, wildcard.CardType, StringComparison.OrdinalIgnoreCase));
                    if (card == null)
                    {
                        card = new UserCard(wildcard.CardType, 0, 0, 0, 0);
                        user.UserCards.Cards.Add(card);
                    }
                    var counts = requested[wildcard.CardType];
                    card.Count = counts.Count;
                    card.GoldCardCount = counts.GoldCount;
                    user.CardCollection[wildcard.CardType] = counts.Count;
                    user.CardCollection[wildcard.CardType + "#gold"] = counts.GoldCount;
                }
                await users.SaveUserAsync(user);
                users.RecordIncremental();
                return ToResult((true, "玩家万能卡数量已保存"));
            });
            return result ?? Results.NotFound();
        });
        api.MapPost("/users/{id:int}/unban", async (int id, AdminUserService adminUsers) => ToResult(await adminUsers.UnbanAsync(id)));
        api.MapPost("/users/{id:int}/kick", async (int id, string? reason, AdminUserService adminUsers) => ToResult(await adminUsers.KickAsync(id, reason)));
        api.MapDelete("/users/{id:int}", async (int id, AdminUserService adminUsers) => ToResult(await adminUsers.DeleteAsync(id)));

        // ---------------------------------------------------------- 对局与队列
        api.MapGet("/matches/history/storage", async (MatchHistoryService history, CancellationToken cancellationToken) =>
            Results.Text((await history.GetStorageInfoAsync(cancellationToken)).ToJsonString(), "application/json"));

        api.MapGet("/matches/history", async (int? page, int? pageSize, string? status, int? playerId, string? playerName,
            MatchHistoryService history, CancellationToken cancellationToken) =>
        {
            var result = await history.ListAdminAsync(page ?? 1, pageSize ?? 25, status, playerId, playerName, cancellationToken);
            var items = new JsonArray();
            foreach (var match in result.Matches)
            {
                items.Add(new JsonObject
                {
                    ["matchId"] = match.MatchId, ["matchType"] = match.MatchType, ["status"] = match.Status,
                    ["startedAt"] = match.StartedAt, ["completedAt"] = match.CompletedAt,
                    ["leftPlayerId"] = match.LeftPlayerId, ["leftPlayerName"] = match.LeftPlayerName, ["leftPlayerTag"] = match.LeftPlayerTag,
                    ["rightPlayerId"] = match.RightPlayerId, ["rightPlayerName"] = match.RightPlayerName, ["rightPlayerTag"] = match.RightPlayerTag,
                    ["turns"] = match.Turns, ["actionCount"] = match.ActionCount, ["winnerSide"] = match.WinnerSide
                });
            }
            return Results.Text(new JsonObject { ["matches"] = items, ["total"] = result.Total, ["page"] = Math.Max(1, page ?? 1), ["pageSize"] = Math.Clamp(pageSize ?? 25, 1, 100) }.ToJsonString(), "application/json");
        });

        api.MapGet("/matches/history/{matchId:int}", async (int matchId, MatchHistoryService history, CancellationToken cancellationToken) =>
        {
            var record = await history.GetAsync(matchId, cancellationToken);
            if (record == null) return Results.Text("{\"ok\":false,\"message\":\"找不到这条已持久化的对局记录\"}", "application/json", statusCode: 404);
            var summary = record.Summary;
            var detail = new JsonObject
            {
                ["summary"] = new JsonObject
                {
                    ["matchId"] = summary.MatchId, ["matchType"] = summary.MatchType, ["status"] = summary.Status,
                    ["startedAt"] = summary.StartedAt, ["completedAt"] = summary.CompletedAt,
                    ["leftPlayerId"] = summary.LeftPlayerId, ["leftPlayerName"] = summary.LeftPlayerName, ["leftPlayerTag"] = summary.LeftPlayerTag,
                    ["rightPlayerId"] = summary.RightPlayerId, ["rightPlayerName"] = summary.RightPlayerName, ["rightPlayerTag"] = summary.RightPlayerTag,
                    ["turns"] = summary.Turns, ["actionCount"] = summary.ActionCount, ["winnerSide"] = summary.WinnerSide
                },
                ["startingInfo"] = record.StartingInfo == null ? null : JsonSerializer.SerializeToNode(record.StartingInfo, FyJsonContext.Default.MatchStartingInfo)
            };
            return Results.Text(detail.ToJsonString(), "application/json");
        });

        api.MapGet("/matches/history/{matchId:int}/actions", async (int matchId, int? afterActionId, int? limit,
            MatchHistoryService history, CancellationToken cancellationToken) =>
        {
            var page = await history.GetActionsAsync(matchId, afterActionId ?? 0, limit ?? 500, cancellationToken);
            return page == null ? Results.NotFound() : Results.Json(page, FyJsonContext.Default.MatchHistoryActionPage);
        });

        api.MapDelete("/matches/history/{matchId:int}", async (int matchId, MatchHistoryService history, MatchManagerService matches, CancellationToken cancellationToken) =>
        {
            if (matches.GetMatch(matchId) != null)
                return Results.Text("{\"ok\":false,\"message\":\"这场对局仍保留在运行时状态中，请先让服务器完成结算并移除对局后再删除持久化数据\"}", "application/json", statusCode: 409);
            var deleted = await history.DeleteAsync(matchId, cancellationToken);
            return Results.Text(new JsonObject { ["ok"] = deleted, ["message"] = deleted ? $"已删除对局 {matchId} 的持久化快照与全部动作" : $"未找到对局 {matchId}" }.ToJsonString(), "application/json", statusCode: deleted ? 200 : 404);
        });

        api.MapGet("/matches", async (MatchManagerService matches, UserStoreService users, WebSocketHubService hub) =>
        {
            var names = new Dictionary<int, string>();
            foreach (var user in await users.GetAllUsersAsync())
                names[user.Id] = $"{user.Name}#{user.Tag:D4}";
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

        api.MapPost("/matches/{matchId:int}/remove", async (int matchId, MatchManagerService matches, MatchHistoryService history) =>
        {
            var match = matches.GetMatch(matchId);
            if (match == null)
                return Results.Text(new JsonObject { ["ok"] = false, ["message"] = $"对局 {matchId} 不存在" }.ToJsonString(), "application/json");
            if (!string.IsNullOrEmpty(match.WinnerSide)) await history.MarkCompletedAsync(match);
            else await history.MarkAbortedAsync(match);
            matches.RemoveMatch(matchId);
            return Results.Text(new JsonObject { ["ok"] = true, ["message"] = $"已移除对局 {matchId}" }.ToJsonString(), "application/json");
        });

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

        api.MapPost("/content/frontpage/upload-image", async (HttpContext context, ServerOptions options, AppDataStoreService appData) =>
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
                var filename = Guid.NewGuid().ToString("N") + extension;
                appData.Set("asset:admin:" + filename, Convert.ToBase64String(bytes));
                return Results.Text(new JsonObject { ["ok"] = true, ["url"] = options.GetAddressHttpR() + "/admin-ui/uploads/" + filename }.ToJsonString(), "application/json");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Data.Common.DbException)
            {
                return SystemSettingsError("图片保存到数据库失败：" + ex.Message, 500);
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

    private static JsonObject PatchPakJson(PatchPakInfo item) => new()
    {
        ["id"] = item.Id, ["fileName"] = item.FileName, ["version"] = item.Version,
        ["description"] = item.Description, ["size"] = item.Size, ["sha256"] = item.Sha256,
        ["createdAt"] = item.CreatedAt.ToString("O"),
        ["downloadUrl"] = "/patch-paks/" + item.Id + "/download"
    };

    private static IResult ToResult((bool ok, string message) result) =>
        Results.Text(new JsonObject { ["ok"] = result.ok, ["message"] = result.message }.ToJsonString(), "application/json");

    private static IResult BadJson() =>
        Results.Text(new JsonObject { ["ok"] = false, ["message"] = "请求体不是合法的 JSON 对象" }.ToJsonString(),
            "application/json", statusCode: StatusCodes.Status400BadRequest);

    private static JsonNode StoreConfigNode(StoreConfig config) =>
        JsonSerializer.SerializeToNode(config, ConfigJsonContext.Default.StoreConfig) ?? new JsonObject();

    private static IResult StoreError(string message, int status = StatusCodes.Status400BadRequest) =>
        Results.Text(new JsonObject { ["ok"] = false, ["message"] = message }.ToJsonString(), "application/json", statusCode: status);

    private static string? ValidateStoreConfig(StoreConfig? config)
    {
        if (config == null) return "商店配置根节点必须是对象";
        if (string.IsNullOrWhiteSpace(config.Currency) || config.Currency.Length > 16) return "currency 必须是 1–16 个字符";
        if (config.AlwaysFeatured == null) return "缺少 alwaysFeatured";
        var groupIds = new HashSet<int>();
        var offerIds = new HashSet<int>();
        foreach (var group in config.Groups ?? [])
        {
            if (group.GroupId < -1 || !groupIds.Add(group.GroupId)) return $"分组 ID 无效或重复：{group.GroupId}";
            var error = ValidateOffers(group.Offers, offerIds);
            if (error != null) return error;
        }
        var featuredError = ValidateOffers(config.AlwaysFeatured.Offers, offerIds);
        return featuredError;
    }

    private static string? ValidateOffers(IEnumerable<StoreOffer>? offers, HashSet<int> offerIds)
    {
        foreach (var offer in offers ?? [])
        {
            if (offer.OfferId <= 0 || !offerIds.Add(offer.OfferId)) return $"商品 ID 无效或重复：{offer.OfferId}";
            if (string.IsNullOrWhiteSpace(offer.OfferName) || offer.OfferName.Length > 160) return $"商品 {offer.OfferId} 的 offerName 无效";
            if (string.IsNullOrWhiteSpace(offer.Title) || offer.Title.Length > 300) return $"商品 {offer.OfferId} 的 title 无效";
            if (offer.Limit is < 0 || offer.Gold is < 0 || offer.Diamonds is < 0 || offer.Real is < 0) return $"商品 {offer.OfferId} 的价格或限购不能为负数";
            foreach (var item in (offer.Items ?? []).Concat(offer.BonusItems ?? []))
            {
                if (item.Qty <= 0 || item.Qty > 100000 || item.Data == null || string.IsNullOrWhiteSpace(item.Data.ItemType))
                    return $"商品 {offer.OfferId} 包含无效奖励条目";
            }
        }
        return null;
    }

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
            ["avatarUrl"] = account.AvatarUrl,
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

    private static IResult JsonResult(bool ok, string message, int status) => Results.Text(
        new JsonObject { ["ok"] = ok, ["message"] = message }.ToJsonString(),
        "application/json", statusCode: status);
}

/// <summary>三类内容配置的数据库键（静态后台接口共用）。</summary>
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
