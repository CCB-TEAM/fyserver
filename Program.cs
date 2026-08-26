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
// 单例实例由本进程显式创建，HTTP 与 WebSocket 两个 host 注册同一批实例，
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
}

// ==================== HTTP host ====================
var httpBuilder = WebApplication.CreateSlimBuilder();
RegisterSharedServices(httpBuilder.Services);
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

httpApp.UseMiddleware<ContentTypeCleanupMiddleware>();

// 显式路由注册：保证 PathNormalizationMiddleware 对 // 路径的改写先于路由匹配生效
httpApp.UseRouting();

httpApp.MapUserEndpoints();
httpApp.MapPlayerEndpoints();
httpApp.MapDeckEndpoints();
httpApp.MapLobbyEndpoints();
httpApp.MapMatchEndpoints();
httpApp.MapAdminEndpoints();

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

// ==================== WebSocket host（独立端口） ====================
await Task.Delay(2000);

var wsBuilder = WebApplication.CreateSlimBuilder();
RegisterSharedServices(wsBuilder.Services);
var wsApp = wsBuilder.Build();
WebSocketServer.Configure(wsApp, users, codec, webSocketHub);
_ = wsApp.RunAsync(serverOptions.GetAddressWs());

// ==================== 控制台命令循环（阻塞主线程） ====================
Command.StartCommandLoop(users, storeConfig, matches);
