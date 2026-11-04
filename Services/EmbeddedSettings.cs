using System.Text.Json;
using System.Text.Json.Nodes;

namespace fyserver.Services;

/// <summary>
/// 嵌入模式下房主在启动器里改的服务器设置，来自 --settings 指定的 JSON 文件，启动器随时可能改写它。
/// 版本白名单、反作弊和玩家身份改了就生效（玩家重新进游戏时读取）；卡牌库只在启动时加载。
///
/// 文件内容：{"versions":["Kards 1.58"],"bancheat":true,"libraryFile":"server-library.json","roles":{"账号名":["vip"]}}
/// libraryFile 相对于设置文件所在目录。
/// </summary>
public sealed class EmbeddedSettings
{
    private static readonly TimeSpan ReloadEvery = TimeSpan.FromSeconds(2);

    private sealed record Snapshot(IReadOnlyList<string> Versions, bool BanCheat, string? LibraryFile, IReadOnlyDictionary<string, IReadOnlyList<string>> Roles);

    private static readonly Snapshot Empty = new([], false, null, new Dictionary<string, IReadOnlyList<string>>());

    private readonly string? _path;
    private readonly ServerOptions? _options;
    private Snapshot _current = Empty;
    private DateTime _loadedAt;
    private Timer? _timer;

    /// <summary>没有 --settings 时什么也不改。</summary>
    public EmbeddedSettings() { }

    /// <param name="applyBanCheat">只有联机房间才反作弊；房主一个人本地测试时不会封自己。</param>
    public EmbeddedSettings(string path, ServerOptions options, bool applyBanCheat)
    {
        _path = Path.GetFullPath(path);
        _options = applyBanCheat ? options : null;
        Reload();
        _timer = new Timer(_ => Reload(), null, ReloadEvery, ReloadEvery);
    }

    /// <summary>客户端版本白名单；为空表示不限制。</summary>
    public IReadOnlyList<string> Versions => _current.Versions;

    /// <summary>房主导入的卡牌库文件的完整路径；没有导入时为 null。</summary>
    public string? LibraryPath =>
        _current.LibraryFile is { } file && _path != null ? Path.Combine(Path.GetDirectoryName(_path)!, file) : null;

    /// <summary>房主授予这个账号的身份；没有授予时为空。</summary>
    public IReadOnlyList<string> RolesFor(string username) =>
        _current.Roles.TryGetValue(username, out var roles) ? roles : [];

    private void Reload()
    {
        try
        {
            var modified = File.GetLastWriteTimeUtc(_path!);
            if (modified == _loadedAt) return;
            _current = Parse(File.ReadAllText(_path!));
            _loadedAt = modified;
            if (_options != null) _options.bancheat = _current.BanCheat;
        }
        // 启动器正在改写文件或者文件内容不对：保留上一份，下次再读
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Console.WriteLine($"读取服务器设置失败：{ex.Message}");
        }
    }

    private static Snapshot Parse(string text)
    {
        var root = JsonNode.Parse(text) as JsonObject ?? throw new JsonException("顶层必须是 JSON 对象");
        var roles = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (root["roles"] is JsonObject map)
        {
            foreach (var item in map)
                roles[item.Key] = Strings(item.Value);
        }
        return new Snapshot(
            Strings(root["versions"]),
            root["bancheat"] is JsonValue ban && ban.TryGetValue<bool>(out var on) && on,
            FileNameOnly(root["libraryFile"] is JsonValue file && file.TryGetValue<string>(out var name) ? name : null),
            roles);
    }

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray array
            ? array.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList()
            : [];

    /// <summary>只认设置文件旁边的文件，不能指到别的目录。</summary>
    private static string? FileNameOnly(string? name) =>
        string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name ? null : name;
}
