using System.Text.Json.Nodes;
using fyserver.Services;

namespace fyserver.Endpoints;

/// <summary>启动器账号接口：与游戏账号（/session）独立。注册需要后台生成的邀请码。</summary>
public static class LauncherEndpoints
{
    public static IEndpointRouteBuilder MapLauncherEndpoints(this IEndpointRouteBuilder app)
    {
        var launcher = app.MapGroup("/launcher");

        launcher.MapPost("/register", async (HttpContext context, LauncherAccountService accounts) =>
        {
            var (body, error) = await ModIndexEndpoints.ReadBody(context);
            if (body == null) return error!;
            var result = accounts.Register(Str(body["username"]), Str(body["password"]), Str(body["inviteCode"]));
            var response = new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message };
            if (result.Account != null) response["account"] = Public(result.Account);
            return ModIndexEndpoints.Json(response, result.Status);
        });

        launcher.MapPost("/login", async (HttpContext context, LauncherAccountService accounts) =>
        {
            var (body, error) = await ModIndexEndpoints.ReadBody(context);
            if (body == null) return error!;
            var ip = ClientIp(context);
            var result = accounts.Login(Str(body["username"]), Str(body["password"]), ip);
            var response = new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message };
            if (result.Token != null)
            {
                response["token"] = result.Token;
                response["expiresAt"] = result.ExpiresAt.ToString("O");
                response["account"] = Public(result.Account!);
            }
            return ModIndexEndpoints.Json(response, result.Status);
        });

        launcher.MapPost("/logout", (HttpContext context, LauncherAccountService accounts) =>
        {
            accounts.Logout(context);
            return ModIndexEndpoints.Json(new JsonObject { ["ok"] = true, ["message"] = "已退出登录" });
        });

        launcher.MapGet("/me", (HttpContext context, LauncherAccountService accounts) =>
        {
            var account = accounts.Authenticate(context);
            return account == null
                ? ModIndexEndpoints.Unauthorized()
                : ModIndexEndpoints.Json(new JsonObject { ["ok"] = true, ["account"] = Public(account) });
        });

        launcher.MapPost("/password", async (HttpContext context, LauncherAccountService accounts) =>
        {
            var account = accounts.Authenticate(context);
            if (account == null) return ModIndexEndpoints.Unauthorized();
            var (body, error) = await ModIndexEndpoints.ReadBody(context);
            if (body == null) return error!;
            var result = accounts.ChangePassword(account, Str(body["currentPassword"]), Str(body["newPassword"]));
            return ModIndexEndpoints.Json(new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message }, result.Status);
        });

        // 头像：启动器缩小后以 PNG 上传，任何人都能读（好友、房间成员列表要显示）。
        launcher.MapPost("/avatar", async (HttpContext context, LauncherAccountService accounts) =>
        {
            var account = accounts.Authenticate(context);
            if (account == null) return ModIndexEndpoints.Unauthorized();
            var (body, error) = await ModIndexEndpoints.ReadBody(context);
            if (body == null) return error!;
            var parsed = ModIndexService.ParseIcon(Str(body["image"]) ?? "");
            if (parsed.Error != null) return ModIndexEndpoints.Fail(400, parsed.Error);
            if (parsed.ContentType != "image/png") return ModIndexEndpoints.Fail(400, "头像必须是 PNG 图片");
            Directory.CreateDirectory(AvatarDir);
            var temp = AvatarPath(account.Id) + ".tmp";
            await File.WriteAllBytesAsync(temp, parsed.Data!);
            File.Move(temp, AvatarPath(account.Id), true);
            return ModIndexEndpoints.Json(new JsonObject { ["ok"] = true, ["message"] = "头像已更新" });
        });

        launcher.MapDelete("/avatar", (HttpContext context, LauncherAccountService accounts) =>
        {
            var account = accounts.Authenticate(context);
            if (account == null) return ModIndexEndpoints.Unauthorized();
            File.Delete(AvatarPath(account.Id));
            return ModIndexEndpoints.Json(new JsonObject { ["ok"] = true, ["message"] = "头像已移除" });
        });

        // 玩家资料：账号名和头衔，好友页、房间成员列表用来显示。
        launcher.MapGet("/profiles/{accountId:int}", (int accountId, LauncherAccountService accounts) =>
            accounts.ProfileJson(accountId) is { } profile ? ModIndexEndpoints.Json(profile) : ModIndexEndpoints.Fail(404, "账号不存在"));

        launcher.MapGet("/avatars/{accountId:int}", (int accountId, HttpContext context) =>
        {
            var path = AvatarPath(accountId);
            if (!File.Exists(path)) return ModIndexEndpoints.Fail(404, "没有头像");
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(File.ReadAllBytes(path), "image/png");
        });

        return app;
    }

    private const string AvatarDir = "./data/launcher-avatars";
    private static string AvatarPath(int accountId) => Path.Combine(AvatarDir, $"{accountId}.png");

    /// <summary>
    /// 客户端 IP：直连时取连接地址；连接来自本机（Caddy 等反向代理在同一台机器上）时，
    /// 取 X-Forwarded-For 最右侧的地址，即代理实际看到的客户端地址。
    /// 只信任来自回环地址的转发头，外部请求伪造的 X-Forwarded-For 不会生效。
    /// </summary>
    internal static string ClientIp(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote == null) return "unknown";
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (System.Net.IPAddress.IsLoopback(remote))
        {
            var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
            var last = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            if (last != null && System.Net.IPAddress.TryParse(last, out var client))
                return (client.IsIPv4MappedToIPv6 ? client.MapToIPv4() : client).ToString();
        }
        return remote.ToString();
    }

    private static JsonObject Public(LauncherAccountService.Account a) => new()
    {
        ["id"] = a.Id,
        ["username"] = a.Username,
        ["createdAt"] = a.CreatedAt
    };

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
