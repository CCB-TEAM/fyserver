using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace fyserver.Services;

public sealed class AdminAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Username { get; set; } = "";
    public string Salt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
    public List<string> PreviousUsernames { get; set; } = [];
    public bool IsOwner { get; set; }
    public bool Enabled { get; set; } = true;
    public HashSet<string> Permissions { get; set; } = new(StringComparer.Ordinal);
    public int SessionVersion { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public string? LastLoginIp { get; set; }
}

/// <summary>后台多账号、权限及签名会话，持久化到已选数据库。</summary>
public sealed class AdminAccountService
{
    public const string CookieName = "fyserver_admin_session";
    public static readonly string[] AvailablePermissions = ["players", "content", "matches", "serverConfig", "systemSettings", "patchPaks", "permissions"];
    private const string AccountKey = "admin:accounts";
    private const int Iterations = 210_000;
    private readonly object _gate = new();
    private readonly AppDataStoreService _appData;
    private readonly List<AdminAccount> _accounts = [];
    private readonly Dictionary<string, SessionPresence> _presence = new(StringComparer.Ordinal);
    private byte[] _sessionSecret = RandomNumberGenerator.GetBytes(32);
    private bool _loadFailed;
    private sealed record SessionPresence(string AccountId, int Version, DateTime SeenAt, DateTime ExpiresAt);

    public AdminAccountService(AppDataStoreService appData)
    {
        _appData = appData;
        Load();
    }

    public void ReloadFromStore()
    {
        lock (_gate)
        {
            _accounts.Clear();
            _loadFailed = false;
            Load();
        }
    }
    public bool IsInitialized { get { lock (_gate) return _accounts.Any(a => a.IsOwner); } }

    public (bool Ok, string Message) Initialize(string? username, string? password)
    {
        var validation = ValidateCredentials(username, password);
        if (validation != null) return (false, validation);
        lock (_gate)
        {
            if (_loadFailed) return (false, "管理员账户数据库读取失败，请检查数据库连接和数据格式");
            if (!_appData.IsReady) return (false, "请先完成数据库配置");
            if (_accounts.Count != 0) return (false, "管理员账户已经初始化");
            _accounts.Add(NewAccount(username!.Trim(), password!, true, []));
            Save();
            return (true, "Owner 账户初始化完成");
        }
    }

    public AdminAccount? Authenticate(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return null;
        lock (_gate)
        {
            var account = _accounts.FirstOrDefault(a => a.Enabled && a.Username == username.Trim());
            if (account == null) return null;
            var actual = HashPassword(password, Convert.FromBase64String(account.Salt));
            var expected = Convert.FromBase64String(account.PasswordHash);
            return CryptographicOperations.FixedTimeEquals(expected, actual) ? Copy(account) : null;
        }
    }

    public string CreateSession(AdminAccount account)
    {
        lock (_gate)
        {
            var current = _accounts.First(a => a.Id == account.Id && a.Enabled);
            var expires = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds();
            var payload = $"{current.Id}.{current.SessionVersion}.{expires}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";
            var token = payload + "." + Sign(payload);
            _presence[TokenKey(token)] = new SessionPresence(current.Id, current.SessionVersion, DateTime.UtcNow, DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime);
            return token;
        }
    }

    public AdminAccount? GetSessionAccount(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        lock (_gate)
        {
            var parts = token.Split('.');
            if (parts.Length != 5 || !int.TryParse(parts[1], out var version) ||
                !long.TryParse(parts[2], out var expires) || expires < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
            var payload = string.Join('.', parts[0], parts[1], parts[2], parts[3]);
            var expected = Encoding.ASCII.GetBytes(Sign(payload));
            var supplied = Encoding.ASCII.GetBytes(parts[4]);
            if (expected.Length != supplied.Length || !CryptographicOperations.FixedTimeEquals(expected, supplied)) return null;
            var account = _accounts.FirstOrDefault(a => a.Id == parts[0] && a.Enabled && a.SessionVersion == version);
            if (account == null) return null;
            _presence[TokenKey(token)] = new SessionPresence(account.Id, account.SessionVersion, DateTime.UtcNow, DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime);
            if (_presence.Count > 10000) PrunePresence();
            return Copy(account);
        }
    }

    public void RevokeSession(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        lock (_gate) _presence.Remove(TokenKey(token));
    }

    public (bool Online, int Sessions, DateTime? LastSeenAt) PresenceFor(string accountId)
    {
        lock (_gate)
        {
            var account = _accounts.FirstOrDefault(a => a.Id == accountId);
            if (account == null || !account.Enabled) return (false, 0, null);
            var cutoff = DateTime.UtcNow.AddMinutes(-2);
            var sessions = _presence.Values.Where(p => p.AccountId == accountId && p.Version == account.SessionVersion && p.SeenAt >= cutoff && p.ExpiresAt > DateTime.UtcNow).ToList();
            return (sessions.Count > 0, sessions.Count, sessions.Count == 0 ? null : sessions.Max(p => p.SeenAt));
        }
    }

    public void RecordSuccessfulLogin(string accountId, string? address)
    {
        lock (_gate)
        {
            var account = _accounts.FirstOrDefault(a => a.Id == accountId);
            if (account == null) return;
            account.LastLoginAt = DateTime.UtcNow;
            account.LastLoginIp = address;
            Save();
        }
    }

    public bool ValidateSession(string? token) => GetSessionAccount(token) != null;
    public AdminAccount? GetByUsername(string? username)
    {
        lock (_gate) return _accounts.FirstOrDefault(a => a.Username == username) is { } account ? Copy(account) : null;
    }
    public bool HasPermission(AdminAccount account, string permission) => account.IsOwner || account.Permissions.Contains(permission);
    public List<AdminAccount> ListAccounts() { lock (_gate) return _accounts.Select(Copy).ToList(); }

    public (bool Ok, string Message) CreateAccount(AdminAccount actor, string? username, string? password, IEnumerable<string>? permissions)
    {
        var validation = ValidateCredentials(username, password);
        if (validation != null) return (false, validation);
        var selected = permissions?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        if (selected.Except(AvailablePermissions).Any()) return (false, "包含未知权限");
        if (!actor.IsOwner && selected.Any(permission => !actor.Permissions.Contains(permission))) return (false, "不能授予自己没有的权限");
        lock (_gate)
        {
            if (_accounts.Any(a => a.Username.Equals(username!.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                   a.PreviousUsernames.Contains(username.Trim(), StringComparer.OrdinalIgnoreCase))) return (false, "用户名已存在");
            _accounts.Add(NewAccount(username!.Trim(), password!, false, selected));
            Save();
            return (true, "后台账号已创建");
        }
    }

    public (bool Ok, string Message) UpdateAccount(AdminAccount actor, string id, bool? enabled, IEnumerable<string>? permissions, string? newPassword)
    {
        var selected = permissions?.ToHashSet(StringComparer.Ordinal);
        if (selected != null && selected.Except(AvailablePermissions).Any()) return (false, "包含未知权限");
        if (!actor.IsOwner && selected != null && selected.Any(permission => !actor.Permissions.Contains(permission))) return (false, "不能授予自己没有的权限");
        if (newPassword != null && newPassword.Length < 10) return (false, "新密码至少需要 10 个字符");
        lock (_gate)
        {
            var account = _accounts.FirstOrDefault(a => a.Id == id);
            if (account == null) return (false, "账号不存在");
            if (account.IsOwner) return (false, "Owner 账号不可通过此接口修改");
            if (enabled.HasValue) account.Enabled = enabled.Value;
            if (selected != null) account.Permissions = selected;
            if (newPassword != null)
            {
                var salt = RandomNumberGenerator.GetBytes(32);
                account.Salt = Convert.ToBase64String(salt);
                account.PasswordHash = Convert.ToBase64String(HashPassword(newPassword, salt));
            }
            account.SessionVersion++;
            Save();
            return (true, "后台账号已更新，原会话已失效");
        }
    }

    public (bool Ok, string Message) DeleteAccount(string id)
    {
        lock (_gate)
        {
            var account = _accounts.FirstOrDefault(a => a.Id == id);
            if (account == null) return (false, "账号不存在");
            if (account.IsOwner) return (false, "不能删除 Owner 账号");
            _accounts.Remove(account);
            Save();
            return (true, "后台账号已删除");
        }
    }

    public (bool Ok, string Message) ResetPassword(AdminAccount actor, string id, string? newPassword, string? currentPassword)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length is < 10 or > 256)
            return (false, "新密码长度应为 10–256 个字符");
        lock (_gate)
        {
            var account = _accounts.FirstOrDefault(a => a.Id == id);
            if (account == null) return (false, "账号不存在");
            if (account.IsOwner)
            {
                if (!actor.IsOwner || actor.Id != account.Id || string.IsNullOrEmpty(currentPassword))
                    return (false, "Owner 修改密码必须提供当前密码");
                var existing = Convert.FromBase64String(account.PasswordHash);
                var actual = HashPassword(currentPassword, Convert.FromBase64String(account.Salt));
                if (!CryptographicOperations.FixedTimeEquals(existing, actual)) return (false, "当前密码不正确");
            }
            var salt = RandomNumberGenerator.GetBytes(32);
            account.Salt = Convert.ToBase64String(salt);
            account.PasswordHash = Convert.ToBase64String(HashPassword(newPassword, salt));
            account.SessionVersion++;
            Save();
            return (true, "密码已重置，原有会话已失效");
        }
    }

    public (bool Ok, string Message) UpdateOwnUsername(AdminAccount actor, string? currentPassword, string? username)
    {
        var normalized = username?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length is < 3 or > 32 || normalized.Any(char.IsControl))
            return (false, "账号名称须为 3–32 个字符，且不能包含控制字符");
        if (string.IsNullOrEmpty(currentPassword)) return (false, "请输入当前密码以确认身份");
        lock (_gate)
        {
            var account = _accounts.FirstOrDefault(a => a.Id == actor.Id && a.Enabled);
            if (account == null) return (false, "账号不存在或已禁用");
            if (!VerifyPassword(account, currentPassword)) return (false, "当前密码不正确");
            if (_accounts.Any(a => a.Id != account.Id && (a.Username.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                                   a.PreviousUsernames.Contains(normalized, StringComparer.OrdinalIgnoreCase))))
                return (false, "账号名称已被使用");
            if (!account.Username.Equals(normalized, StringComparison.Ordinal))
            {
                if (!account.PreviousUsernames.Contains(account.Username, StringComparer.OrdinalIgnoreCase))
                    account.PreviousUsernames.Add(account.Username);
                account.Username = normalized;
                Save();
            }
            return (true, "账号名称已更新");
        }
    }

    public (bool Ok, string Message) ChangeOwnPassword(AdminAccount actor, string? currentPassword, string? newPassword)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length is < 10 or > 256)
            return (false, "新密码长度应为 10–256 个字符");
        if (string.IsNullOrEmpty(currentPassword)) return (false, "请输入当前密码");
        lock (_gate)
        {
            var account = _accounts.FirstOrDefault(a => a.Id == actor.Id && a.Enabled);
            if (account == null) return (false, "账号不存在或已禁用");
            if (!VerifyPassword(account, currentPassword)) return (false, "当前密码不正确");
            if (VerifyPassword(account, newPassword)) return (false, "新密码不能与当前密码相同");
            SetPassword(account, newPassword);
            account.SessionVersion++;
            Save();
            return (true, "密码已更新，当前会话也将退出，请使用新密码重新登录");
        }
    }

    public bool UpdateOwnAvatar(AdminAccount actor, string avatarUrl)
    {
        lock (_gate)
        {
            var account = _accounts.FirstOrDefault(a => a.Id == actor.Id && a.Enabled);
            if (account == null) return false;
            account.AvatarUrl = avatarUrl;
            Save();
            return true;
        }
    }

    private static string TokenKey(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private void PrunePresence()
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        foreach (var key in _presence.Where(p => p.Value.SeenAt < cutoff || p.Value.ExpiresAt <= DateTime.UtcNow).Select(p => p.Key).ToList())
            _presence.Remove(key);
    }

    private static string? ValidateCredentials(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Trim().Length is < 3 or > 32) return "管理员用户名长度应为 3–32 个字符";
        if (string.IsNullOrEmpty(password) || password.Length < 10) return "密码至少需要 10 个字符";
        return null;
    }

    private static AdminAccount NewAccount(string username, string password, bool owner, IEnumerable<string> permissions)
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        return new AdminAccount
        {
            Username = username, Salt = Convert.ToBase64String(salt),
            PasswordHash = Convert.ToBase64String(HashPassword(password, salt)), IsOwner = owner,
            Permissions = permissions.ToHashSet(StringComparer.Ordinal)
        };
    }

    private static AdminAccount Copy(AdminAccount a) => new()
    {
        Id = a.Id, Username = a.Username, Salt = a.Salt, PasswordHash = a.PasswordHash,
        IsOwner = a.IsOwner, Enabled = a.Enabled, Permissions = new HashSet<string>(a.Permissions, StringComparer.Ordinal),
        SessionVersion = a.SessionVersion, CreatedAt = a.CreatedAt
        , LastLoginAt = a.LastLoginAt, LastLoginIp = a.LastLoginIp,
        AvatarUrl = a.AvatarUrl,
        PreviousUsernames = a.PreviousUsernames.ToList()
    };

    private static bool VerifyPassword(AdminAccount account, string password)
    {
        var expected = Convert.FromBase64String(account.PasswordHash);
        var actual = HashPassword(password, Convert.FromBase64String(account.Salt));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static void SetPassword(AdminAccount account, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        account.Salt = Convert.ToBase64String(salt);
        account.PasswordHash = Convert.ToBase64String(HashPassword(password, salt));
    }

    private string Sign(string payload) => Convert.ToHexString(HMACSHA256.HashData(_sessionSecret, Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    private static byte[] HashPassword(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

    private void Load()
    {
        if (!_appData.IsReady) return;
        try
        {
            var stored = _appData.Get(AccountKey);
            if (string.IsNullOrWhiteSpace(stored)) return;
            var json = JsonNode.Parse(stored)?.AsObject();
            if (json == null) return;
            if (json["accounts"] is JsonArray accounts)
            {
                _sessionSecret = Convert.FromBase64String(json["sessionSecret"]?.GetValue<string>() ?? "");
                if (_sessionSecret.Length < 32) throw new InvalidDataException("会话密钥无效");
                foreach (var node in accounts.OfType<JsonObject>())
                    _accounts.Add(new AdminAccount
                    {
                        Id = node["id"]?.GetValue<string>() ?? "",
                        Username = node["username"]?.GetValue<string>() ?? "",
                        Salt = node["salt"]?.GetValue<string>() ?? "",
                        PasswordHash = node["passwordHash"]?.GetValue<string>() ?? "",
                        IsOwner = node["isOwner"]?.GetValue<bool>() ?? false,
                        Enabled = node["enabled"]?.GetValue<bool>() ?? true,
                        SessionVersion = node["sessionVersion"]?.GetValue<int>() ?? 1,
                        CreatedAt = DateTime.TryParse(node["createdAt"]?.GetValue<string>(), out var created) ? created : DateTime.UtcNow,
                        LastLoginAt = DateTime.TryParse(node["lastLoginAt"]?.GetValue<string>(), out var loginAt) ? loginAt : null,
                        AvatarUrl = node["avatarUrl"]?.GetValue<string>() ?? "",
                        PreviousUsernames = node["previousUsernames"] is JsonArray aliases
                            ? aliases.Select(alias => alias?.GetValue<string>()).Where(alias => !string.IsNullOrWhiteSpace(alias)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                            : [],
                        LastLoginIp = node["lastLoginIp"]?.GetValue<string>(),
                        Permissions = (node["permissions"] as JsonArray)?.Select(p => p?.GetValue<string>() ?? "").Where(p => AvailablePermissions.Contains(p)).ToHashSet(StringComparer.Ordinal) ?? []
                    });
            }
            else if (json["passwordHash"] != null)
            {
                _accounts.Add(new AdminAccount
                {
                    Username = json["username"]?.GetValue<string>() ?? "",
                    Salt = json["salt"]?.GetValue<string>() ?? "",
                    PasswordHash = json["passwordHash"]?.GetValue<string>() ?? "",
                    IsOwner = true
                });
                Save(); // 旧单账号迁移成 owner，原有密码哈希保持不变。
            }
            if (_accounts.Count(a => a.IsOwner) != 1) throw new InvalidDataException("必须且只能存在一个 Owner 账号");
        }
        catch (Exception ex)
        {
            _accounts.Clear();
            _loadFailed = true;
            Console.WriteLine($"管理员账户配置读取失败：{ex.Message}");
        }
    }

    private void Save()
    {
        var accounts = new JsonArray();
        foreach (var a in _accounts)
        {
            var permissions = new JsonArray();
            foreach (var permission in a.Permissions.Order()) permissions.Add(JsonValue.Create(permission));
            accounts.Add(new JsonObject
            {
                ["id"] = a.Id, ["username"] = a.Username, ["salt"] = a.Salt,
                ["passwordHash"] = a.PasswordHash, ["isOwner"] = a.IsOwner,
                ["enabled"] = a.Enabled, ["sessionVersion"] = a.SessionVersion,
                ["createdAt"] = a.CreatedAt.ToString("O"), ["lastLoginAt"] = a.LastLoginAt?.ToString("O"),
                ["lastLoginIp"] = a.LastLoginIp, ["avatarUrl"] = a.AvatarUrl,
                ["previousUsernames"] = new JsonArray(a.PreviousUsernames.Select(username => (JsonNode?)JsonValue.Create(username)).ToArray()),
                ["permissions"] = permissions
            });
        }
        var json = new JsonObject { ["version"] = 2, ["sessionSecret"] = Convert.ToBase64String(_sessionSecret), ["accounts"] = accounts };
        _appData.Set(AccountKey, json.ToJsonString(new() { WriteIndented = true }));
    }
}
