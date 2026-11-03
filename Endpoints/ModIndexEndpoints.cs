using System.Text.Json;
using System.Text.Json.Nodes;
using fyserver.Services;

namespace fyserver.Endpoints;

/// <summary>
/// 游戏侧/第三方工具使用的模组索引接口。
/// 列表、详情、图标、下载无需登录；上传、更新、删除需要玩家登录（Authorization 头，与其它游戏接口相同）。
/// </summary>
public static class ModIndexEndpoints
{
    public static IEndpointRouteBuilder MapModIndexEndpoints(this IEndpointRouteBuilder app)
    {
        var mods = app.MapGroup("/mods");

        // 分页：offset 从 0 开始，limit 默认 10、最大 50（列表项不含图标，单页约 30 KB 以内）。
        mods.MapGet("/", (HttpContext context, ModIndexService index) =>
        {
            var offset = ParseInt(context.Request.Query["offset"], 0);
            var limit = ParseInt(context.Request.Query["limit"], DefaultPageSize);
            if (offset < 0) return Fail(400, "offset 不能小于 0");
            if (limit < 1 || limit > MaxPageSize) return Fail(400, $"limit 必须在 1 到 {MaxPageSize} 之间");

            var all = index.List(includeHidden: false);
            var items = all.Skip(offset).Take(limit)
                .Select(m => index.ToPublic(m["id"]!.GetValue<string>(), false, IconUrl(m["id"]!.GetValue<string>())).Json)
                .Select(j => (JsonNode?)j).ToArray();
            return Json(new JsonObject
            {
                ["total"] = all.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["mods"] = new JsonArray(items)
            });
        });

        mods.MapGet("/{id}", (string id, ModIndexService index) =>
        {
            if (!IsVisible(index, id)) return NotFound();
            return Json(index.ToPublic(id, false, IconUrl(id)).Json);
        });

        mods.MapGet("/{id}/icon", (string id, ModIndexService index) =>
        {
            if (!IsVisible(index, id) || index.GetIcon(id) is not { } icon) return NotFound();
            return Results.File(icon.Data, icon.ContentType);
        });

        // 下载：返回 manifest（含 base64 图标）以及上传者提供的 Pak 下载直链。
        mods.MapGet("/{id}/download", (string id, ModIndexService index) =>
        {
            if (!IsVisible(index, id)) return NotFound();
            var (json, _) = index.ToPublic(id, true, IconUrl(id));
            index.RecordDownload(id);
            return Json(new JsonObject
            {
                ["manifest"] = json,
                ["downloadUrl"] = json["downloadUrl"]?.DeepClone()
            });
        });

        // 上传/更新模组信息。同一 id 只能由原上传者更新。
        mods.MapPost("/", async (HttpContext context, AuthService auth, ModIndexService index) =>
        {
            var player = await auth.GetUserFromAuthAsync(context);
            if (player == null)
                return Json(new JsonObject { ["message"] = "Authorization required" }, 401);

            JsonObject? body;
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var text = await reader.ReadToEndAsync(context.RequestAborted);
                if (text.Length > 1024 * 1024 * 2) return Fail(413, "请求体过大");
                body = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException) { body = null; }
            if (body == null) return Fail(400, "请求体不是合法的 JSON 对象");

            try
            {
                var result = await index.UpsertAsync(player.Id, string.IsNullOrWhiteSpace(player.Name) ? "player" + player.Id : player.Name, body, context.RequestAborted);
                var response = new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message };
                if (result.Manifest != null)
                    response["mod"] = index.ToPublic(result.Manifest["id"]!.GetValue<string>(), false, IconUrl(result.Manifest["id"]!.GetValue<string>())).Json;
                return Json(response, result.Status);
            }
            catch (InvalidOperationException) { return Fail(400, "字段类型不正确"); }
            catch (FormatException) { return Fail(400, "字段类型不正确"); }
        });

        // 获取对象存储的预签名上传链接：启动器用返回的 uploadUrl 直接 PUT Pak 文件，再调用 POST /mods（可省略 downloadUrl）。
        mods.MapPost("/upload-url", async (HttpContext context, AuthService auth, ModIndexService index) =>
        {
            var player = await auth.GetUserFromAuthAsync(context);
            if (player == null)
                return Json(new JsonObject { ["message"] = "Authorization required" }, 401);
            JsonObject? body;
            try { body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject; }
            catch (JsonException) { body = null; }
            if (body == null) return Fail(400, "请求体不是合法的 JSON 对象");
            var result = index.CreateUploadUrl(player.Id, body);
            return result.Result != null ? Json(result.Result) : Fail(result.Status, result.Message);
        });

        mods.MapDelete("/{id}", async (string id, HttpContext context, AuthService auth, ModIndexService index) =>
        {
            var player = await auth.GetUserFromAuthAsync(context);
            if (player == null)
                return Json(new JsonObject { ["message"] = "Authorization required" }, 401);
            var result = index.Delete(id, player.Id);
            return Json(new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message },
                result.Ok ? 200 : result.Message.Contains("自己") ? 403 : 404);
        });

        return app;
    }

    private const int DefaultPageSize = 10;
    private const int MaxPageSize = 50;

    private static int ParseInt(string? value, int fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : int.TryParse(value, out var n) ? n : -1;

    private static bool IsVisible(ModIndexService index, string id) =>
        index.Get(id) is { } m && ModIndexService.IsPublic(m);

    private static string IconUrl(string id) => "/mods/" + id + "/icon";
    private static IResult NotFound() => Fail(404, "模组不存在");
    private static IResult Fail(int status, string message) =>
        Json(new JsonObject { ["ok"] = false, ["message"] = message }, status);
    private static IResult Json(JsonObject body, int status = 200) =>
        Results.Text(body.ToJsonString(), "application/json", statusCode: status);
}
