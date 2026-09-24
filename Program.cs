using fyserver;
using fyserver.Endpoints;
using fyserver.Middleware;
using fyserver.Models;
using fyserver.Serialization;
using fyserver.Services;
using Microsoft.AspNetCore.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

// 发行版可能从快捷方式、服务管理器或其它工作目录启动；所有相对路径
//（setting.json、config、library、data 与 wwwroot）都必须固定到 exe 所在目录。
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// ==================== 配置与共享服务 ====================
// 单例实例由本进程显式创建，HTTP 与 WebSocket 合并到同一个 host，
// 从而共享用户存储 / 匹配队列 / WebSocket 连接表等运行时状态。

var serverOptions = new ServerOptions();
serverOptions.ReadFromFile(); // 读 ./setting.json；不存在则生成默认配置

FasterKvService? fasterKv = null;
var userDatabaseConfiguration = new UserDatabaseConfigurationService();
var appData = new AppDataStoreService(() => fasterKv ??= new FasterKvService());
var users = new UserStoreService(() => fasterKv ??= new FasterKvService(), userDatabaseConfiguration, appData);
users.TryInitializeConfiguredAsync().GetAwaiter().GetResult();
if (appData.IsReady && appData.Get("server:settings") is { } savedServerSettings)
    serverOptions.ReadFromJson(savedServerSettings);
var codec = new CodecService();
var playerLibrary = new PlayerLibraryService();
playerLibrary.InitLibrary("./library/deckCodeIDsTable2.json", "./library/emojiLib.json", "./library/cardbackLib.json");
var storeConfig = new StoreConfigService(appData);
var redeemCodes = new RedeemCodeService(appData);
var webSocketHub = new WebSocketHubService();
var auth = new AuthService(users, codec);
var matchHistory = new MatchHistoryService(userDatabaseConfiguration, appData);
try { await matchHistory.InitializeAsync(); }
catch (Exception ex) { Console.WriteLine($"对局历史数据库初始化失败：{ex.GetBaseException().Message}"); }
var matches = new MatchManagerService(users, playerLibrary, codec, serverOptions, matchHistory);

// ==================== 后台（静态页 /admin-ui 的数据层）服务 ====================
var adminUsers = new AdminUserService(users, webSocketHub);
var frontpage = new FrontpageConfigService(appData);
var contentEntries = new ContentEntriesService(appData);
var serverMetrics = new ServerMetricsService();
var adminAccount = new AdminAccountService(appData);
var adminAudit = new AdminAuditLogService(appData);
var clientServerConfig = new ClientServerConfigService(appData);
var patchPaks = new PatchPakService(appData);

void RegisterSharedServices(IServiceCollection services)
{
    services.AddSingleton(serverOptions);
    services.AddSingleton(users);
    services.AddSingleton(userDatabaseConfiguration);
    services.AddSingleton(appData);
    services.AddSingleton(codec);
    services.AddSingleton(playerLibrary);
    services.AddSingleton(storeConfig);
    services.AddSingleton(redeemCodes);
    services.AddSingleton(webSocketHub);
    services.AddSingleton(auth);
    services.AddSingleton(matches);
    services.AddSingleton(matchHistory);
    services.AddSingleton(adminUsers);
    services.AddSingleton(frontpage);
    services.AddSingleton(contentEntries);
    services.AddSingleton(serverMetrics);
    services.AddSingleton(adminAccount);
    services.AddSingleton(adminAudit);
    services.AddSingleton(clientServerConfig);
    services.AddSingleton(patchPaks);
}

// ============ HTTP host（含 WebSocket 端点，共用同一端口） ============
var httpBuilder = WebApplication.CreateSlimBuilder();
RegisterSharedServices(httpBuilder.Services);
httpBuilder.Services.AddHostedService<MatchHistoryCleanupWorker>();

httpBuilder.WebHost.UseUrls(serverOptions.GetAddressHttp());
httpBuilder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    // 纯源生成：NativeAOT 兼容（所有响应类型注册到 FyJsonContext）
    options.SerializerOptions.TypeInfoResolverChain.Clear();
    options.SerializerOptions.TypeInfoResolverChain.Add(FyJsonContext.Default);
});

var httpApp = httpBuilder.Build();

httpApp.UseMiddleware<PathNormalizationMiddleware>();

// 全局异常处理：统一返回 JSON 500
httpApp.UseExceptionHandler(exceptionHandlerApp =>
{
    exceptionHandlerApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(
            new ErrorResponse(
                Error: "Internal server error",
                Message: "An unexpected error occurred",
                StatusCode: 500
            ),
            FyJsonContext.Default.ErrorResponse,
            cancellationToken: context.RequestAborted);
    });
});

// WebSocket 端点：与 HTTP 共用同一端口与管线（非 WS 请求继续走后面的 HTTP 管线）
httpApp.MapWebSocketEndpoint();

httpApp.UseMiddleware<ContentTypeCleanupMiddleware>();
httpApp.UseMiddleware<ServerInitializationMiddleware>();
// 静态后台（/admin-ui）的数据接口鉴权：只挡 /admin/api/*，静态页自身匿名可访问

// 后台数据接口鉴权（静态后台 /admin-ui）
httpApp.UseMiddleware<AdminApiAuthorizationMiddleware>();

// 显式路由注册：保证 PathNormalizationMiddleware 对 // 路径的改写先于路由匹配生效
httpApp.UseRouting();

httpApp.MapUserEndpoints();
httpApp.MapPlayerEndpoints();
httpApp.MapRedeemEndpoints();
httpApp.MapDeckEndpoints();
httpApp.MapLobbyEndpoints();
httpApp.MapMatchEndpoints();
httpApp.MapMatchHistoryEndpoints();
httpApp.MapAdminApiEndpoints();
httpApp.MapGet("/patch-paks", (ServerOptions options, PatchPakService paks) =>
{
    var baseUrl = options.GetAddressHttpR().TrimEnd('/');
    var patches = new System.Text.Json.Nodes.JsonArray(paks.List().Select(item => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
    {
        ["id"] = item.Id, ["fileName"] = item.FileName, ["version"] = item.Version,
        ["description"] = item.Description, ["size"] = item.Size, ["sha256"] = item.Sha256,
        ["createdAt"] = item.CreatedAt.ToString("O"), ["downloadUrl"] = baseUrl + "/patch-paks/" + item.Id + "/download"
    }).ToArray());
    return Results.Text(new System.Text.Json.Nodes.JsonObject { ["algorithm"] = "SHA-256", ["patches"] = patches }.ToJsonString(), "application/json");
});
httpApp.MapGet("/patch-paks/{id}/download", (string id, PatchPakService paks) =>
{
    if (id.Length != 32 || id.Any(c => !char.IsAsciiHexDigit(c))) return Results.NotFound();
    var item = paks.List().FirstOrDefault(candidate => candidate.Id == id);
    var bytes = item == null ? null : paks.Read(id);
    if (item == null || bytes == null) return Results.NotFound();
    return Results.File(bytes, "application/octet-stream", item.FileName, lastModified: item.CreatedAt,
        entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue("\"" + item.Sha256 + "\""), enableRangeProcessing: true);
});
httpApp.MapGet("/admin-ui/uploads/{**assetPath}", (string assetPath, AppDataStoreService data) =>
{
    var relative = assetPath.Replace('\\', '/');
    if (relative.Split('/').Any(segment => segment is "" or "." or "..")) return Results.NotFound();
    var encoded = data.Get("asset:admin:" + relative);
    if (encoded == null) return Results.NotFound();
    try
    {
        var extension = Path.GetExtension(relative).ToLowerInvariant();
        var contentType = extension switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", _ => "application/octet-stream" };
        return Results.File(Convert.FromBase64String(encoded), contentType);
    }
    catch (FormatException) { return Results.NotFound(); }
});
// 后台入口别名：/admin-ui/ → index.html、/admin-ui/login → login.html
httpApp.UseMiddleware<AdminUiEntryMiddleware>();
// 静态资源（wwwroot/admin-ui）：CreateSlimBuilder 默认未启用静态文件中间件
httpApp.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        // Always revalidate admin pages and assets after replacing a distribution.
        if (context.Context.Request.Path.StartsWithSegments("/admin-ui"))
            context.Context.Response.Headers.CacheControl = "no-cache";
    }
});


// 未找到路由的处理
httpApp.UseStatusCodePages(async statusCodeContext =>
{
    var response = statusCodeContext.HttpContext.Response;
    if (response.StatusCode == 404)
    {
        await response.WriteAsJsonAsync(
            new ErrorResponse(
                Error: "Not found",
                Message: "The requested resource was not found",
                StatusCode: 404
            ),
            FyJsonContext.Default.ErrorResponse,
            cancellationToken: statusCodeContext.HttpContext.RequestAborted);
    }
});

// 数据库初始化和清理
httpApp.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"Application started on {serverOptions.GetAddressHttp()}");
    Console.WriteLine($"WebSocket endpoint: {serverOptions.GetAddressWsR()}");
    Console.WriteLine(users.IsReady ? $"用户数据库已准备：{users.Provider}" : "用户数据库尚未配置，游戏接口已暂停");
    if (!adminAccount.IsInitialized)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("============================================================");
        Console.WriteLine("管理员后台尚未初始化，请在浏览器完成初始设置：");
        Console.WriteLine($"http://127.0.0.1:{serverOptions.portHttp}/admin-ui/login.html");
        Console.WriteLine("首次设置仅允许从服务器本机访问。");
        Console.WriteLine("============================================================");
        Console.ResetColor();
    }
});
httpApp.Lifetime.ApplicationStopping.Register(() =>
{
    Console.WriteLine("Application stopping. Cleaning up...");
    fasterKv?.Dispose(); // 仅本地存储模式会创建
});

Console.ForegroundColor = ConsoleColor.Blue;
Console.WriteLine("正在启动http服务器");
Console.WriteLine("等待两秒确保初始化成功");
if (File.Exists("./YCDR"))
    Console.WriteLine("发现持久化数据，已加载");
Console.ForegroundColor = ConsoleColor.White;

_ = httpApp.RunAsync();

// ==================== 控制台命令循环（阻塞主线程） ====================
Command.StartCommandLoop(users, storeConfig, matches);
