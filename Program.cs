using fyserver;
using fyserver.Endpoints;
using fyserver.Middleware;
using fyserver.Models;
using fyserver.Serialization;
using fyserver.Services;
using Microsoft.AspNetCore.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

// ==================== 配置与共享服务 ====================
// 单例实例由本进程显式创建，HTTP 与 WebSocket 合并到同一个 host，
// 从而共享用户存储 / 匹配队列 / WebSocket 连接表等运行时状态。

var serverOptions = new ServerOptions();
serverOptions.ReadFromFile(); // 读 ./setting.json；不存在则生成默认配置

var fasterKv = new FasterKvService();
var users = new UserStoreService(fasterKv);
var codec = new CodecService();
var playerLibrary = new PlayerLibraryService();
playerLibrary.InitLibrary("./library/deckCodeIDsTable2.json", "./library/emojiLib.json", "./library/cardbackLib.json");
var storeConfig = new StoreConfigService();
var webSocketHub = new WebSocketHubService();
var auth = new AuthService(users, codec);
var matches = new MatchManagerService(users, playerLibrary, codec, serverOptions);

// ==================== 后台（Razor 页面）服务 ====================
var adminUsers = new AdminUserService(users, webSocketHub);
var frontpage = new FrontpageConfigService();
var contentEntries = new ContentEntriesService();

void RegisterSharedServices(IServiceCollection services)
{
    services.AddSingleton(serverOptions);
    services.AddSingleton(fasterKv);
    services.AddSingleton(users);
    services.AddSingleton(codec);
    services.AddSingleton(playerLibrary);
    services.AddSingleton(storeConfig);
    services.AddSingleton(webSocketHub);
    services.AddSingleton(auth);
    services.AddSingleton(matches);
    services.AddSingleton(adminUsers);
    services.AddSingleton(frontpage);
    services.AddSingleton(contentEntries);
}

// ============ HTTP host（含 WebSocket 端点，共用同一端口） ============
var httpBuilder = WebApplication.CreateSlimBuilder();
RegisterSharedServices(httpBuilder.Services);
httpBuilder.Services.AddAdminRazorPages();
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
httpApp.UseMiddleware<AdminAuthorizationMiddleware>();

// 显式路由注册：保证 PathNormalizationMiddleware 对 // 路径的改写先于路由匹配生效
httpApp.UseRouting();

httpApp.MapUserEndpoints();
httpApp.MapPlayerEndpoints();
httpApp.MapDeckEndpoints();
httpApp.MapLobbyEndpoints();
httpApp.MapMatchEndpoints();
httpApp.MapAdminEndpoints();
// 后台静态资源（wwwroot/admin-assets/*）：CreateSlimBuilder 默认未启用静态文件中间件
httpApp.UseStaticFiles();
httpApp.MapRazorPages();


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
    Console.WriteLine("Faster 已准备");
});
httpApp.Lifetime.ApplicationStopping.Register(() =>
{
    Console.WriteLine("Application stopping. Cleaning up...");
    fasterKv.Dispose(); // 幂等
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
