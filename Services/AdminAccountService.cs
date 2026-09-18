using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace fyserver.Services;

/// <summary>管理员账户、密码校验与签名会话。账户文件不保存明文密码。</summary>
public sealed class AdminAccountService
{
    public const string CookieName = "fyserver_admin_session";
    private const string AccountPath = "./data/admin-auth.json";
    private const int Iterations = 210_000;
    private readonly object _gate = new();
    private string _username = "";
    private byte[] _salt = [];
    private byte[] _passwordHash = [];
    private byte[] _sessionSecret = [];
    private int _sessionVersion = 1;

    public AdminAccountService() => Load();

    public bool IsInitialized { get { lock (_gate) return _passwordHash.Length > 0; } }
    public string? Username { get { lock (_gate) return IsInitialized ? _username : null; } }

    public (bool Ok, string Message) Initialize(string? username, string? password)
    {
        username = username?.Trim();
        if (string.IsNullOrWhiteSpace(username) || username.Length is < 3 or > 32)
            return (false, "管理员用户名长度应为 3–32 个字符");
        if (string.IsNullOrEmpty(password) || password.Length < 10)
            return (false, "密码至少需要 10 个字符");

        lock (_gate)
        {
            if (_passwordHash.Length > 0) return (false, "管理员账户已经初始化");
            _username = username;
            _salt = RandomNumberGenerator.GetBytes(32);
            _passwordHash = HashPassword(password, _salt);
            _sessionSecret = RandomNumberGenerator.GetBytes(32);
            _sessionVersion = 1;
            Save();
            return (true, "初始化完成");
        }
    }

    public bool VerifyCredentials(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return false;
        lock (_gate)
        {
            if (_passwordHash.Length == 0 || !string.Equals(_username, username.Trim(), StringComparison.Ordinal)) return false;
            return CryptographicOperations.FixedTimeEquals(_passwordHash, HashPassword(password, _salt));
        }
    }

    public string CreateSession()
    {
        lock (_gate)
        {
            var expires = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds();
            var payload = $"{_sessionVersion}.{expires}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";
            return payload + "." + Sign(payload);
        }
    }

    public bool ValidateSession(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        lock (_gate)
        {
            var parts = token.Split('.');
            if (parts.Length != 4 || !int.TryParse(parts[0], out var version) || version != _sessionVersion ||
                !long.TryParse(parts[1], out var expires) || expires < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
            var payload = string.Join('.', parts[0], parts[1], parts[2]);
            var expected = Encoding.ASCII.GetBytes(Sign(payload));
            var supplied = Encoding.ASCII.GetBytes(parts[3]);
            return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
        }
    }

    private string Sign(string payload) => Convert.ToHexString(HMACSHA256.HashData(_sessionSecret, Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    private static byte[] HashPassword(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

    private void Load()
    {
        if (!File.Exists(AccountPath)) return;
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(AccountPath))?.AsObject();
            if (json == null) return;
            _username = json["username"]?.GetValue<string>() ?? "";
            _salt = Convert.FromBase64String(json["salt"]?.GetValue<string>() ?? "");
            _passwordHash = Convert.FromBase64String(json["passwordHash"]?.GetValue<string>() ?? "");
            _sessionSecret = Convert.FromBase64String(json["sessionSecret"]?.GetValue<string>() ?? "");
            _sessionVersion = json["sessionVersion"]?.GetValue<int>() ?? 1;
        }
        catch (Exception ex) { Console.WriteLine($"管理员账户配置读取失败：{ex.Message}"); }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AccountPath)!);
        var json = new JsonObject
        {
            ["username"] = _username,
            ["salt"] = Convert.ToBase64String(_salt),
            ["passwordHash"] = Convert.ToBase64String(_passwordHash),
            ["sessionSecret"] = Convert.ToBase64String(_sessionSecret),
            ["sessionVersion"] = _sessionVersion
        };
        var temp = AccountPath + ".tmp";
        File.WriteAllText(temp, json.ToJsonString(new() { WriteIndented = true }));
        File.Move(temp, AccountPath, true);
    }
}
