using System.Text.Json.Nodes;

namespace fyserver.Services;

/// <summary>后台写操作审计日志。只记录路径和结果，不记录请求体、密码、Cookie 或密钥。</summary>
public sealed class AdminAuditLogService
{
    private const string PathName = "./data/admin-audit.jsonl";
    private const string LoginPath = "./data/admin-logins.jsonl";
    private readonly object _gate = new();

    public void Record(string actor, string action, string path, int status, string? address)
    {
        var entry = new JsonObject
        {
            ["time"] = DateTime.UtcNow.ToString("O"), ["actor"] = actor,
            ["action"] = action, ["path"] = path, ["status"] = status,
            ["address"] = address ?? ""
        };
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
                File.AppendAllText(PathName, entry.ToJsonString() + Environment.NewLine);
            }
            catch (IOException ex) { Console.WriteLine($"后台审计日志写入失败：{ex.Message}"); }
        }
    }

    public JsonArray Recent(int count = 200)
    {
        var entries = new JsonArray();
        lock (_gate)
        {
            if (!File.Exists(PathName)) return entries;
            foreach (var line in File.ReadLines(PathName).TakeLast(Math.Clamp(count, 1, 500)))
            {
                try { if (JsonNode.Parse(line) is { } node) entries.Add(node); }
                catch (System.Text.Json.JsonException) { }
            }
        }
        return entries;
    }

    public void RecordLogin(string username, bool success, string? address)
    {
        var entry = new JsonObject
        {
            ["time"] = DateTime.UtcNow.ToString("O"), ["username"] = username,
            ["success"] = success, ["address"] = address ?? ""
        };
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LoginPath)!);
                File.AppendAllText(LoginPath, entry.ToJsonString() + Environment.NewLine);
            }
            catch (IOException ex) { Console.WriteLine($"后台登录日志写入失败：{ex.Message}"); }
        }
    }

    public (JsonArray Entries, int Total) ActionsFor(string username, int page = 1) => ReadFiltered(PathName, "actor", username, page);
    public (JsonArray Entries, int Total) LoginsFor(string username, int page = 1) => ReadFiltered(LoginPath, "username", username, page);

    public (JsonArray Entries, int Total) ActionsFor(IEnumerable<string> usernames, int page = 1) => ReadFiltered(PathName, "actor", usernames, page);
    public (JsonArray Entries, int Total) LoginsFor(IEnumerable<string> usernames, int page = 1) => ReadFiltered(LoginPath, "username", usernames, page);

    private (JsonArray Entries, int Total) ReadFiltered(string path, string field, string value, int page) =>
        ReadFiltered(path, field, [value], page);

    private (JsonArray Entries, int Total) ReadFiltered(string path, string field, IEnumerable<string> values, int page)
    {
        var filter = values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = new List<JsonNode>();
        lock (_gate)
        {
            if (!File.Exists(path)) return (new JsonArray(), 0);
            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    if (JsonNode.Parse(line) is not JsonObject entry || !filter.Contains(entry[field]?.GetValue<string>() ?? "")) continue;
                    matches.Add(entry);
                }
                catch (System.Text.Json.JsonException) { }
            }
        }
        var result = new JsonArray();
        foreach (var entry in matches.AsEnumerable().Reverse().Skip((Math.Clamp(page, 1, 100000) - 1) * 100).Take(100)) result.Add(entry);
        return (result, matches.Count);
    }
}
