using System.Text.Json;
using System.Text.Json.Nodes;
using fyserver.Services;

namespace fyserver.Endpoints;

/// <summary>
/// 模组索引接口（审核制）。列表、详情、图标、下载无需登录，只返回已通过审核且未隐藏的版本；
/// 提交、查看自己的模组、删除需要启动器账号登录（Authorization: Bearer &lt;token&gt;，见 /launcher/login）。
/// </summary>
public static class ModIndexEndpoints
{
    private const int DefaultPageSize = 10;
    private const int MaxPageSize = 50;
    private const int MaxBodyBytes = 2 * 1024 * 1024;
    private const int MaxIdFilter = 200;

    public static IEndpointRouteBuilder MapModIndexEndpoints(this IEndpointRouteBuilder app)
    {
        var mods = app.MapGroup("/mods");

        // 分页：offset 从 0 开始，limit 默认 10、最大 50（列表项不含图标，单页约 30 KB 以内）。
        // 搜索 q、排序 sort（latest/hot/downloads/likes）、只看 ids / 排除 exclude（逗号分隔，各最多 200 个）。
        // 带有效启动器 token 时，每项额外返回 liked。
        mods.MapGet("/", (HttpContext context, ModIndexService index, LauncherAccountService accounts) =>
        {
            var query = context.Request.Query;
            var offset = ParseInt(query["offset"], 0);
            var limit = ParseInt(query["limit"], DefaultPageSize);
            if (offset < 0) return Fail(400, "offset 必须是不小于 0 的整数");
            if (limit < 1 || limit > MaxPageSize) return Fail(400, $"limit 必须在 1 到 {MaxPageSize} 之间");
            var sort = string.IsNullOrWhiteSpace(query["sort"]) ? "latest" : query["sort"].ToString().Trim().ToLowerInvariant();
            if (sort is not ("latest" or "hot" or "downloads" or "likes")) return Fail(400, "sort 只能是 latest、hot、downloads 或 likes");
            var q = query["q"].ToString();
            if (q.Length > 100) return Fail(400, "搜索关键词不能超过 100 个字符");
            var onlyIds = ParseIds(query["ids"]);
            var excludeIds = ParseIds(query["exclude"]);
            if (onlyIds?.Count > MaxIdFilter || excludeIds?.Count > MaxIdFilter) return Fail(400, $"ids 和 exclude 各最多 {MaxIdFilter} 个");

            var viewer = accounts.Authenticate(context);
            var all = index.ListPublic(q, sort, onlyIds, excludeIds);
            var items = all.Skip(offset).Take(limit).Select(m => (JsonNode?)index.ToPublic(m, false, viewer?.Id)).ToArray();
            return Json(new JsonObject
            {
                ["total"] = all.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["mods"] = new JsonArray(items)
            });
        });

        // 当前启动器账号上传的全部模组（含待审核、被驳回的提交）。
        mods.MapGet("/mine", (HttpContext context, LauncherAccountService accounts, ModIndexService index) =>
        {
            var account = accounts.Authenticate(context);
            if (account == null) return Unauthorized();
            var items = index.ListOwnedBy(account.Id).Select(m => (JsonNode?)ModIndexService.ToOwnerView(m)).ToArray();
            return Json(new JsonObject { ["mods"] = new JsonArray(items) });
        });

        mods.MapGet("/{id}", (string id, HttpContext context, ModIndexService index, LauncherAccountService accounts) =>
            Visible(index, id) is { } m ? Json(index.ToPublic(m, false, accounts.Authenticate(context)?.Id)!) : NotFound());

        mods.MapGet("/{id}/icon", (string id, ModIndexService index) =>
            Visible(index, id) != null && index.GetIcon(id, pending: false) is { } icon
                ? Results.File(icon.Data, icon.ContentType)
                : NotFound());

        // 下载：返回已发布版本的 manifest（含 base64 图标）以及 Pak 下载直链，并计入下载次数。
        mods.MapGet("/{id}/download", (string id, HttpContext context, ModIndexService index, LauncherAccountService accounts) =>
        {
            if (Visible(index, id) is not { } m) return NotFound();
            var json = index.ToPublic(m, true, accounts.Authenticate(context)?.Id)!;
            index.RecordDownload(id, LauncherEndpoints.ClientIp(context));
            return Json(new JsonObject
            {
                ["manifest"] = json,
                ["downloadUrl"] = json["downloadUrl"]?.DeepClone()
            });
        });

        // 提交新模组或更新（同一 id 只能由原上传者提交），进入待审核。
        mods.MapPost("/", async (HttpContext context, LauncherAccountService accounts, ModIndexService index) =>
        {
            var account = accounts.Authenticate(context);
            if (account == null) return Unauthorized();
            var (body, error) = await ReadBody(context);
            if (body == null) return error!;
            var result = index.Submit(account.Id, account.Username, body);
            var response = new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message };
            if (result.Manifest != null) response["mod"] = ModIndexService.ToOwnerView(result.Manifest);
            return Json(response, result.Status);
        });

        // 点赞 / 取消点赞（幂等），返回最新点赞数。
        mods.MapPost("/{id}/like", (string id, HttpContext context, LauncherAccountService accounts, ModIndexService index) =>
            SetLike(id, true, context, accounts, index));
        mods.MapDelete("/{id}/like", (string id, HttpContext context, LauncherAccountService accounts, ModIndexService index) =>
            SetLike(id, false, context, accounts, index));

        mods.MapDelete("/{id}", (string id, HttpContext context, LauncherAccountService accounts, ModIndexService index) =>
        {
            var account = accounts.Authenticate(context);
            if (account == null) return Unauthorized();
            var result = index.DeleteOwned(id, account.Id);
            return Json(new JsonObject { ["ok"] = result.Ok, ["message"] = result.Message }, result.Status);
        });

        return app;
    }

    internal static async Task<(JsonObject? Body, IResult? Error)> ReadBody(HttpContext context)
    {
        if (context.Request.ContentLength > MaxBodyBytes) return (null, Fail(413, "请求体过大"));
        try
        {
            using var reader = new StreamReader(context.Request.Body);
            var buffer = new char[8192];
            var text = new System.Text.StringBuilder();
            int n;
            while ((n = await reader.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                text.Append(buffer, 0, n);
                if (text.Length > MaxBodyBytes) return (null, Fail(413, "请求体过大"));
            }
            if (JsonNode.Parse(text.ToString()) is JsonObject o) return (o, null);
            return (null, Fail(400, "请求体不是合法的 JSON 对象"));
        }
        catch (JsonException)
        {
            return (null, Fail(400, "请求体不是合法的 JSON 对象"));
        }
    }

    private static IResult SetLike(string id, bool like, HttpContext context, LauncherAccountService accounts, ModIndexService index)
    {
        var account = accounts.Authenticate(context);
        if (account == null) return Unauthorized();
        var result = index.SetLike(id, account.Id, like);
        if (!result.Ok) return Fail(result.Status, result.Message);
        return Json(new JsonObject { ["ok"] = true, ["message"] = result.Message, ["likes"] = result.Likes, ["liked"] = result.Liked });
    }

    private static HashSet<string>? ParseIds(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.ToLowerInvariant()).ToHashSet();

    private static JsonObject? Visible(ModIndexService index, string id) =>
        index.Get(id) is { } m && ModIndexService.IsPublic(m) ? m : null;

    private static int ParseInt(string? value, int fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : int.TryParse(value, out var n) ? n : -1;

    internal static IResult Unauthorized() =>
        Json(new JsonObject { ["ok"] = false, ["message"] = "请先登录启动器账号" }, 401);
    private static IResult NotFound() => Fail(404, "模组不存在");
    internal static IResult Fail(int status, string message) =>
        Json(new JsonObject { ["ok"] = false, ["message"] = message }, status);
    internal static IResult Json(JsonObject body, int status = 200) =>
        Results.Text(body.ToJsonString(), "application/json", statusCode: status);
}
