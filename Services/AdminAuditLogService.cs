using System.Text.Json.Nodes;

namespace fyserver.Services;

/// <summary>后台写操作和登录审计日志，统一存储在所选数据库中。</summary>
public sealed class AdminAuditLogService(AppDataStoreService appData)
{
    private const string ActionPrefix = "admin:audit:";
    private const string LoginPrefix = "admin:login:";
    private readonly object _gate = new();

    public void Record(string actor, string action, string path, int status, string? address)
    {
        var now = DateTime.UtcNow;
        var entry = new JsonObject
        {
            ["time"] = now.ToString("O"), ["actor"] = actor,
            ["action"] = action, ["path"] = path, ["status"] = status,
            ["address"] = address ?? ""
        };
        Write(ActionPrefix, now, entry);
    }

    public JsonArray Recent(int count = 200) => new(
        ReadAll(ActionPrefix)
            .OrderByDescending(item => (string?)item["time"])
            .Take(Math.Clamp(count, 1, 500))
            .Select(item => (JsonNode?)item)
            .ToArray());

    public void RecordLogin(string username, bool success, string? address)
    {
        var now = DateTime.UtcNow;
        var entry = new JsonObject
        {
            ["time"] = now.ToString("O"), ["username"] = username,
            ["success"] = success, ["address"] = address ?? ""
        };
        Write(LoginPrefix, now, entry);
    }

    public (JsonArray Entries, int Total) ActionsFor(string username, int page = 1) => ReadFiltered(ActionPrefix, "actor", [username], page);
    public (JsonArray Entries, int Total) LoginsFor(string username, int page = 1) => ReadFiltered(LoginPrefix, "username", [username], page);
    public (JsonArray Entries, int Total) ActionsFor(IEnumerable<string> usernames, int page = 1) => ReadFiltered(ActionPrefix, "actor", usernames, page);
    public (JsonArray Entries, int Total) LoginsFor(IEnumerable<string> usernames, int page = 1) => ReadFiltered(LoginPrefix, "username", usernames, page);

    private void Write(string prefix, DateTime time, JsonObject entry)
    {
        lock (_gate)
        {
            try
            {
                var key = prefix + time.ToString("yyyyMMddHHmmssfffffff") + ":" + Guid.NewGuid().ToString("N");
                appData.Set(key, entry.ToJsonString());
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Data.Common.DbException)
            {
                Console.WriteLine($"后台审计日志数据库写入失败：{ex.GetBaseException().Message}");
            }
        }
    }

    private List<JsonObject> ReadAll(string prefix)
    {
        lock (_gate)
        {
            try
            {
                return appData.List(prefix)
                    .Select(pair => JsonNode.Parse(pair.Value) as JsonObject)
                    .Where(item => item != null)
                    .Cast<JsonObject>()
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Data.Common.DbException)
            {
                Console.WriteLine($"后台审计日志数据库读取失败：{ex.GetBaseException().Message}");
                return [];
            }
        }
    }

    private (JsonArray Entries, int Total) ReadFiltered(string prefix, string field, IEnumerable<string> values, int page)
    {
        var filter = values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = ReadAll(prefix)
            .Where(entry => filter.Contains((string?)entry[field] ?? ""))
            .OrderByDescending(entry => (string?)entry["time"])
            .ToList();
        var result = new JsonArray();
        foreach (var entry in matches.Skip((Math.Clamp(page, 1, 100000) - 1) * 100).Take(100)) result.Add(entry.DeepClone());
        return (result, matches.Count);
    }
}
