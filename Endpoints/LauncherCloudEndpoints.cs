using System.Text.Json.Nodes;
using fyserver.Services;

namespace fyserver.Endpoints;

/// <summary>
/// 启动器服务端接口：房间大厅、进房票据、中继、启动器好友/私聊、整合包、控制 WebSocket。
/// 除公钥和中继外都需要 Authorization: Bearer &lt;启动器登录 token&gt;（中继用进房票据）。完整说明见 docs/launcher-cloud-api.md。
/// </summary>
public static class LauncherCloudEndpoints
{
    public static IEndpointRouteBuilder MapLauncherCloudEndpoints(this IEndpointRouteBuilder app)
    {
        var launcher = app.MapGroup("/launcher");

        // ==================== 控制 WebSocket ====================
        launcher.Map("/ws", async (HttpContext context, LauncherAccountService accounts, LauncherHubService hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest) return Results.BadRequest("需要 WebSocket 连接");
            var account = accounts.Authenticate(context);
            if (account == null) return ModIndexEndpoints.Unauthorized();
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await hub.RunAsync(account.Id, socket, context.RequestAborted);
            return Results.Empty;
        });

        // ==================== 房间 ====================
        launcher.MapGet("/rooms/pubkey", (RoomTicketService tickets) => Json(tickets.PublicKeyJson()));

        launcher.MapGet("/rooms", async (HttpContext context, LauncherAccountService accounts, LauncherRoomService rooms, LauncherSocialService social) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var friends = await social.FriendIdsAsync(account.Id);
            var q = context.Request.Query["q"].ToString().Trim();
            var list = rooms.ListVisible(account.Id, friends)
                .Where(r => q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Id.Equals(q, StringComparison.OrdinalIgnoreCase) ||
                            r.HostName.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(r =>
                {
                    var json = rooms.ToJson(r, account.Id);
                    json["hostIsFriend"] = friends.Contains(r.HostId);
                    return (JsonNode?)json;
                }).ToArray();
            return Json(new JsonObject { ["ok"] = true, ["rooms"] = new JsonArray(list) });
        });

        launcher.MapGet("/rooms/mine", (HttpContext context, LauncherAccountService accounts, LauncherRoomService rooms) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var room = rooms.RoomOf(account.Id);
            return Json(new JsonObject { ["ok"] = true, ["room"] = room == null ? null : rooms.ToJson(room, account.Id) });
        });

        launcher.MapPost("/rooms", async (HttpContext context, LauncherAccountService accounts, LauncherRoomService rooms,
            RoomTicketService tickets, LauncherTunnelService tunnel) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var (body, error) = await ModIndexEndpoints.ReadBody(context);
            if (body == null) return error!;
            var result = rooms.Create(account.Id, account.Username, body);
            return result.Ok ? Entered(result.Room!, account, "host", rooms, tickets, tunnel, 201, result.Message) : Fail(result.Status, result.Message);
        });

        launcher.MapGet("/rooms/{id}", async (string id, HttpContext context, LauncherAccountService accounts, LauncherRoomService rooms, LauncherSocialService social) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var room = rooms.Get(id);
            if (room == null || (room.Visibility == "friends" && !room.Members.ContainsKey(account.Id) && !await social.AreFriendsAsync(account.Id, room.HostId)))
                return Fail(404, "房间不存在或已关闭");
            return Json(new JsonObject { ["ok"] = true, ["room"] = rooms.ToJson(room, account.Id) });
        });

        launcher.MapPut("/rooms/{id}", async (string id, HttpContext context, LauncherAccountService accounts, LauncherRoomService rooms) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var (body, error) = await ModIndexEndpoints.ReadBody(context);
            if (body == null) return error!;
            var result = rooms.Update(Code(id), account.Id, body);
            return result.Ok ? Json(new JsonObject { ["ok"] = true, ["message"] = result.Message, ["room"] = rooms.ToJson(result.Room!, account.Id) }) : Fail(result.Status, result.Message);
        });

        launcher.MapPost("/rooms/{id}/join", async (string id, HttpContext context, LauncherAccountService accounts, LauncherRoomService rooms,
            LauncherSocialService social, RoomTicketService tickets, LauncherTunnelService tunnel) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var (body, error) = await ModIndexEndpoints.ReadBody(context);
            if (body == null) return error!;
            var room = rooms.Get(id);
            var isFriend = room != null && await social.AreFriendsAsync(account.Id, room.HostId);
            var password = body["password"] is JsonValue p && p.TryGetValue<string>(out var s) ? s : null;
            var result = rooms.Join(id, account.Id, account.Username, password, isFriend);
            if (!result.Ok) return Fail(result.Status, result.Message);
            var role = result.Room!.HostId == account.Id ? "host" : "member";
            return Entered(result.Room, account, role, rooms, tickets, tunnel, 200, result.Message);
        });

        launcher.MapPost("/rooms/{id}/heartbeat", (string id, HttpContext context, LauncherAccountService accounts, LauncherRoomService rooms,
            RoomTicketService tickets, LauncherTunnelService tunnel) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var result = rooms.Heartbeat(Code(id), account.Id);
            if (!result.Ok) return Fail(result.Status, result.Message);
            var role = result.Room!.HostId == account.Id ? "host" : "member";
            return Entered(result.Room, account, role, rooms, tickets, tunnel, 200, "ok");
        });

        launcher.MapPost("/rooms/{id}/leave", (string id, HttpContext context, LauncherAccountService accounts, LauncherRoomService rooms) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var result = rooms.Leave(Code(id), account.Id);
            return result.Ok ? Ok(result.Message) : Fail(result.Status, result.Message);
        });

        launcher.MapPost("/rooms/{id}/kick", async (string id, HttpContext context, LauncherAccountService accounts, LauncherRoomService rooms) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var (body, error) = await ModIndexEndpoints.ReadBody(context);
            if (body == null) return error!;
            var target = body["accountId"] is JsonValue v && v.TryGetValue<int>(out var t) ? t : 0;
            var result = rooms.Kick(Code(id), account.Id, target);
            return result.Ok ? Ok(result.Message) : Fail(result.Status, result.Message);
        });

        launcher.MapPost("/rooms/{id}/invite", async (string id, HttpContext context, LauncherAccountService accounts, LauncherSocialService social) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var (body, error) = await ModIndexEndpoints.ReadBody(context);
            if (body == null) return error!;
            var target = body["accountId"] is JsonValue v && v.TryGetValue<int>(out var t) ? t : 0;
            var result = await social.InviteAsync(account.Id, Code(id), target);
            return result.Ok ? Ok(result.Message) : Fail(result.Status, result.Message);
        });

        launcher.MapPost("/rooms/{id}/chat", async (string id, HttpContext context, LauncherAccountService accounts, LauncherRoomService rooms) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var (body, error) = await ModIndexEndpoints.ReadBody(context);
            if (body == null) return error!;
            var text = body["text"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : null;
            var result = await rooms.ChatAsync(Code(id), account.Id, account.Username, text);
            return result.Ok ? Ok(result.Message) : Fail(result.Status, result.Message);
        });

        launcher.MapDelete("/rooms/{id}", (string id, HttpContext context, LauncherAccountService accounts, LauncherRoomService rooms) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var result = rooms.Close(Code(id), account.Id, "房主关闭了房间");
            return result.Ok ? Ok(result.Message) : Fail(result.Status, result.Message);
        });

        // ==================== 中继（启动器内置隧道） ====================
        // 三个接口都要 Upgrade: fy-relay 和 X-Fy-Ticket 头，成功时回 101 后变成原始字节流。
        launcher.MapGet("/relay/host", async (HttpContext context, LauncherTunnelService tunnel, RoomTicketService tickets, LauncherRoomService rooms) =>
        {
            var (ticket, error) = tunnel.Authorize(context, tickets, rooms, "host");
            if (ticket == null) return error!;
            await tunnel.RunHostAsync(context, ticket, rooms);
            return Results.Empty;
        });

        launcher.MapGet("/relay/connect", async (HttpContext context, LauncherTunnelService tunnel, RoomTicketService tickets, LauncherRoomService rooms) =>
        {
            var (ticket, error) = tunnel.Authorize(context, tickets, rooms, "member");
            if (ticket == null) return error!;
            return await tunnel.RunConnectAsync(context, ticket, rooms);
        });

        launcher.MapGet("/relay/accept/{conn}", async (HttpContext context, string conn, LauncherTunnelService tunnel, RoomTicketService tickets, LauncherRoomService rooms) =>
        {
            var (ticket, error) = tunnel.Authorize(context, tickets, rooms, "host");
            if (ticket == null) return error!;
            return await tunnel.RunAcceptAsync(context, ticket, conn);
        });

        // 打洞：成员先向 UDP 端口发登记包，再调用这里；云端通知房主向成员发包，并返回房主地址。
        launcher.MapPost("/relay/punch", async (HttpContext context, LauncherTunnelService tunnel, LauncherPunchService punch, RoomTicketService tickets, LauncherRoomService rooms) =>
        {
            var (ticket, error) = tunnel.Authorize(context, tickets, rooms, "member", upgrade: false);
            if (ticket == null) return error!;
            var (status, body) = await punch.PunchAsync(ticket, tunnel);
            return Json(body, status);
        });

        // ==================== 好友 ====================
        // 按名字搜索启动器账号，附带和我的好友关系：none / accepted / request_sent / request_received。
        launcher.MapGet("/users/search", async (HttpContext context, LauncherAccountService accounts, LauncherSocialService social) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var q = context.Request.Query["q"].ToString().Trim();
            if (q.Length > 32) q = q[..32];
            var relations = await social.RelationsAsync(account.Id);
            var users = accounts.Search(q, 20).Where(u => u.Id != account.Id).Select(u => (JsonNode?)new JsonObject
            {
                ["accountId"] = u.Id,
                ["username"] = u.Username,
                ["relation"] = relations.GetValueOrDefault(u.Id, "none")
            }).ToArray();
            return Json(new JsonObject { ["ok"] = true, ["users"] = new JsonArray(users) });
        });

        launcher.MapGet("/friends", async (HttpContext context, LauncherAccountService accounts, LauncherSocialService social) =>
            accounts.Authenticate(context) is { } account ? Json(await social.ListFriendsAsync(account.Id)) : ModIndexEndpoints.Unauthorized());

        launcher.MapPost("/friends", async (HttpContext context, LauncherAccountService accounts, LauncherSocialService social) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var (body, error) = await ModIndexEndpoints.ReadBody(context);
            if (body == null) return error!;
            var username = body["username"] is JsonValue u && u.TryGetValue<string>(out var name) ? name : null;
            int? targetId = body["accountId"] is JsonValue a && a.TryGetValue<int>(out var id) ? id : null;
            var result = await social.AddAsync(account.Id, username, targetId);
            return result.Ok
                ? Json(new JsonObject { ["ok"] = true, ["message"] = result.Message, ["accountId"] = result.Data?["accountId"]?.DeepClone() }, result.Status)
                : Fail(result.Status, result.Message);
        });

        launcher.MapDelete("/friends/{friendId:int}", async (int friendId, HttpContext context, LauncherAccountService accounts, LauncherSocialService social) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var result = await social.RemoveAsync(account.Id, friendId);
            return result.Ok ? Ok(result.Message) : Fail(result.Status, result.Message);
        });

        // ==================== 私聊 ====================
        launcher.MapGet("/chat/{friendId:int}", async (int friendId, HttpContext context, LauncherAccountService accounts, LauncherSocialService social) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            long? before = long.TryParse(context.Request.Query["before"], out var b) ? b : null;
            var limit = int.TryParse(context.Request.Query["limit"], out var l) ? l : 50;
            var result = await social.GetHistoryAsync(account.Id, friendId, before, limit);
            if (!result.Ok) return Fail(result.Status, result.Message);
            result.Data!["ok"] = true;
            return Json(result.Data);
        });

        launcher.MapPost("/chat/{friendId:int}", async (int friendId, HttpContext context, LauncherAccountService accounts, LauncherSocialService social) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var (body, error) = await ModIndexEndpoints.ReadBody(context);
            if (body == null) return error!;
            var text = body["text"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : null;
            var result = await social.SendChatAsync(account.Id, friendId, text);
            if (!result.Ok) return Fail(result.Status, result.Message);
            result.Data!["ok"] = true;
            return Json(result.Data);
        });

        launcher.MapPost("/chat/{friendId:int}/read", async (int friendId, HttpContext context, LauncherAccountService accounts, LauncherSocialService social) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            await social.MarkReadAsync(account.Id, friendId);
            return Ok("ok");
        });

        // ==================== 整合包 ====================
        launcher.MapGet("/modpacks", (HttpContext context, LauncherAccountService accounts, LauncherModpackService packs) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var mine = context.Request.Query["mine"] == "1" || context.Request.Query["mine"] == "true";
            var list = packs.List(account.Id, mine, context.Request.Query["q"]);
            return Json(new JsonObject { ["ok"] = true, ["modpacks"] = new JsonArray(list.Select(p => (JsonNode?)p).ToArray()) });
        });

        launcher.MapGet("/modpacks/{id}", (string id, HttpContext context, LauncherAccountService accounts, LauncherModpackService packs, LauncherRoomService rooms) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var pack = packs.Get(id, account.Id);
            // 私有整合包：如果它正被自己所在房间使用，房间成员也能读取清单。
            if (pack == null && rooms.RoomOf(account.Id) is { } room && room.ModpackId == id)
                pack = packs.Get(id, room.HostId);
            return pack == null ? Fail(404, "整合包不存在") : Json(new JsonObject { ["ok"] = true, ["modpack"] = pack });
        });

        launcher.MapPost("/modpacks", async (HttpContext context, LauncherAccountService accounts, LauncherModpackService packs) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var (body, error) = await ModIndexEndpoints.ReadBody(context);
            if (body == null) return error!;
            var result = packs.Create(account.Id, account.Username, body);
            return result.Ok ? Json(new JsonObject { ["ok"] = true, ["message"] = result.Message, ["modpack"] = result.Pack }, result.Status) : Fail(result.Status, result.Message);
        });

        launcher.MapPut("/modpacks/{id}", async (string id, HttpContext context, LauncherAccountService accounts, LauncherModpackService packs) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var (body, error) = await ModIndexEndpoints.ReadBody(context);
            if (body == null) return error!;
            var result = packs.Update(id, account.Id, body);
            return result.Ok ? Json(new JsonObject { ["ok"] = true, ["message"] = result.Message, ["modpack"] = result.Pack }) : Fail(result.Status, result.Message);
        });

        launcher.MapDelete("/modpacks/{id}", (string id, HttpContext context, LauncherAccountService accounts, LauncherModpackService packs) =>
        {
            if (accounts.Authenticate(context) is not { } account) return ModIndexEndpoints.Unauthorized();
            var result = packs.Delete(id, account.Id);
            return result.Ok ? Ok(result.Message) : Fail(result.Status, result.Message);
        });

        return app;
    }

    /// <summary>开房 / 进房 / 心跳的统一响应：房间信息 + 新票据 + 隧道参数。</summary>
    private static IResult Entered(LauncherRoomService.Room room, LauncherAccountService.Account account, string role,
        LauncherRoomService rooms, RoomTicketService tickets, LauncherTunnelService tunnel, int status, string message)
    {
        var (ticket, expiresAt) = tickets.Issue(room.Id, account.Id, account.Username, role);
        return Json(new JsonObject
        {
            ["ok"] = true,
            ["message"] = message,
            ["room"] = rooms.ToJson(room, account.Id),
            ["role"] = role,
            ["ticket"] = ticket,
            ["ticketExpiresAt"] = expiresAt.ToString("O"),
            ["tunnel"] = tunnel.ClientConfig(room, role)
        }, status);
    }

    private static string Code(string id) => id.Trim().ToUpperInvariant();
    private static IResult Json(JsonObject body, int status = 200) => ModIndexEndpoints.Json(body, status);
    private static IResult Ok(string message) => Json(new JsonObject { ["ok"] = true, ["message"] = message });
    private static IResult Fail(int status, string message) => ModIndexEndpoints.Fail(status, message);
}
