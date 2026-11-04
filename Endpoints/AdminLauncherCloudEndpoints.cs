using System.Text.Json;
using System.Text.Json.Nodes;
using fyserver.Services;

namespace fyserver.Endpoints;

/// <summary>
/// 后台：启动器云端（房间大厅监控、frp 隧道设置、启动器好友/小助手、云端整合包）。
/// 权限由 AdminApiAuthorizationMiddleware 按路径判断：/launcher-cloud/tunnel 需要 serverConfig，其余需要 players。
/// </summary>
public static class AdminLauncherCloudEndpoints
{
    public static IEndpointRouteBuilder MapAdminLauncherCloudEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/admin/api/launcher-cloud");

        api.MapGet("/overview", async (LauncherRoomService rooms, LauncherHubService hub, LauncherSocialService social, LauncherTunnelService tunnel, LauncherModpackService packs) =>
        {
            var list = rooms.All().OrderByDescending(r => r.CreatedAt).Select(r => (JsonNode?)rooms.ToJson(r)).ToArray();
            var root = new JsonObject
            {
                ["online"] = hub.OnlineCount,
                ["rooms"] = new JsonArray(list),
                ["tunnelEnabled"] = tunnel.Get().Enabled,
                ["modpacks"] = new JsonArray(packs.All().Select(p =>
                {
                    p.Remove("mods");
                    return (JsonNode?)p;
                }).ToArray())
            };
            try
            {
                var stats = await social.GetStatsAsync();
                root["stats"] = new JsonObject { ["friendships"] = stats.Friendships, ["pending"] = stats.Pending, ["messages"] = stats.Messages };
            }
            catch (Exception ex) { root["statsError"] = ex.GetBaseException().Message; }
            return Text(root);
        });

        api.MapPost("/rooms/{id}/close", (string id, LauncherRoomService rooms) =>
        {
            var result = rooms.Close(id, null, "房间已被管理员关闭");
            return Result(result.Ok, result.Message, result.Status);
        });

        api.MapPost("/message", async (HttpContext context, LauncherSocialService social) =>
        {
            var body = await ReadBody(context);
            if (body == null) return Result(false, "请求体不是合法的 JSON 对象", 400);
            var text = (body["message"] is JsonValue m && m.TryGetValue<string>(out var t) ? t : "").Trim();
            if (text.Length is 0 or > 2000) return Result(false, "消息内容需要 1 到 2000 个字", 400);
            int? accountId = body["accountId"] is JsonValue a && a.TryGetValue<int>(out var id) && id > 0 ? id : null;
            var all = body["all"] is JsonValue b && b.TryGetValue<bool>(out var x) && x;
            if (accountId == null && !all) return Result(false, "请填写启动器账号 ID，或选择发给所有在线账号", 400);
            var count = await social.SendAdminMessageAsync(all ? null : accountId, text);
            return Result(true, all ? $"已发送给 {count} 个在线账号" : $"已发送给账号 {accountId}（离线时上线后收到）", 200);
        });

        api.MapDelete("/modpacks/{id}", (string id, LauncherModpackService packs) =>
        {
            var result = packs.Delete(id, null);
            return Result(result.Ok, result.Message, result.Status);
        });

        api.MapGet("/tunnel", (LauncherTunnelService tunnel, RoomTicketService tickets, ServerOptions options) =>
            Text(TunnelJson(tunnel.Get(), tickets, options)));

        api.MapPut("/tunnel", async (HttpContext context, LauncherTunnelService tunnel, RoomTicketService tickets, ServerOptions options) =>
        {
            var body = await ReadBody(context);
            if (body == null) return Result(false, "请求体不是合法的 JSON 对象", 400);
            var current = tunnel.Get();
            var enabled = body["enabled"] is JsonValue e && e.TryGetValue<bool>(out var en) ? en : current.Enabled;
            var addr = body["serverAddr"] is JsonValue s && s.TryGetValue<string>(out var sa) ? sa : current.ServerAddr;
            var port = body["serverPort"] is JsonValue p && p.TryGetValue<int>(out var sp) ? sp : current.ServerPort;
            var bw = body["relayBandwidthLimit"] is JsonValue w && w.TryGetValue<string>(out var bws) ? bws : current.RelayBandwidthLimit;
            var (ok, message, saved) = tunnel.Save(enabled, addr, port, bw);
            if (!ok) return Result(false, message, 400);
            var json = TunnelJson(saved!, tickets, options);
            json["ok"] = true;
            json["message"] = message;
            return Text(json);
        });

        return app;
    }

    private static JsonObject TunnelJson(LauncherTunnelService.Settings s, RoomTicketService tickets, ServerOptions options) => new()
    {
        ["enabled"] = s.Enabled,
        ["serverAddr"] = s.ServerAddr,
        ["serverPort"] = s.ServerPort,
        ["relayBandwidthLimit"] = s.RelayBandwidthLimit,
        ["pluginKey"] = s.PluginKey,
        ["ticketKeyId"] = tickets.KeyId,
        // 部署在同一台机器上的 frps 配置示例（插件只接受回环地址调用）
        ["frpsExample"] = $"""
            bindPort = {s.ServerPort}
            # 打洞（xtcp）不需要额外端口；stcp 中继走 bindPort。
            transport.maxPoolCount = 5

            [[httpPlugins]]
            name = "fyserver-rooms"
            addr = "127.0.0.1:{options.portHttp}"
            path = "/launcher/frp/plugin?key={s.PluginKey}"
            ops = ["Login", "NewProxy", "NewUserConn"]
            """
    };

    private static IResult Text(JsonObject json) => Results.Text(json.ToJsonString(), "application/json");

    private static IResult Result(bool ok, string message, int status) =>
        Results.Text(new JsonObject { ["ok"] = ok, ["message"] = message }.ToJsonString(), "application/json", statusCode: status);

    private static async Task<JsonObject?> ReadBody(HttpContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.Body);
            var text = await reader.ReadToEndAsync(context.RequestAborted);
            return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException) { return null; }
    }
}
