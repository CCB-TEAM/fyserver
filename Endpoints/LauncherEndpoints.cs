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
            var ip = context.Connection.RemoteIpAddress is { } addr
                ? (addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4().ToString() : addr.ToString())
                : "unknown";
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

        return app;
    }

    private static JsonObject Public(LauncherAccountService.Account a) => new()
    {
        ["id"] = a.Id,
        ["username"] = a.Username,
        ["createdAt"] = a.CreatedAt
    };

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
