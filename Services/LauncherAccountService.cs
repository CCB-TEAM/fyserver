using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace fyserver.Services;

/// <summary>
/// 启动器账号：与游戏账号（/session）完全独立，只用于模组上传等启动器功能。
/// 注册需要后台生成的一次性邀请码；密码用 PBKDF2-SHA256 加盐哈希；
/// 登录签发随机 token（服务端只保存其 SHA-256），30 天有效。
/// 数据保存在 ./data/launcher-accounts.json，全部使用 JsonNode，NativeAOT 安全。
/// </summary>
public sealed partial class LauncherAccountService
{
    private const string StorePath = "./data/launcher-accounts.json";
    private const int Pbkdf2Iterations = 100_000;
    private const int MinPasswordLength = 8;
    private const int MaxPasswordLength = 128;
    private const int MaxLoginFailures = 10;
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan LoginLockout = TimeSpan.FromMinutes(15);
    private const string InviteAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 去掉易混淆的 I O 0 1

    private readonly object _gate = new();
    private readonly Dictionary<string, (int Failures, DateTime WindowStart)> _loginFailures = new();
    private Store? _store;

    public sealed class Account
    {
        public int Id;
        public string Username = "";
        public string PasswordHash = "";
        public string Salt = "";
        public int Iterations = Pbkdf2Iterations;
        public string CreatedAt = "";
        public string InviteCode = "";
        public bool Disabled;
        public string? LastLoginAt;
        public string? LastLoginIp;
        public string? TitleId;
    }

    /// <summary>后台给启动器账号分发的头衔。</summary>
    private sealed class Title
    {
        public string Id = "";
        public string Name = "";
        public string Color = "";
    }

    private sealed class Invite
    {
        public string Code = "";
        public string CreatedAt = "";
        public string CreatedBy = "";
        public string? ExpiresAt;
        public string? Note;
        public bool Revoked;
        public int? UsedBy;
        public string? UsedAt;
    }

    private sealed class Session
    {
        public string TokenHash = "";
        public int AccountId;
        public string CreatedAt = "";
        public string ExpiresAt = "";
    }

    private sealed class Store
    {
        public int NextId = 1;
        public List<Account> Accounts = [];
        public List<Invite> Invites = [];
        public List<Session> Sessions = [];
        public List<Title> Titles = [];
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]{3,32}$")]
    private static partial Regex UsernameRegex();

    // ------------------------------------------------------------------ 注册 / 登录
    public (bool Ok, int Status, string Message, Account? Account) Register(string? username, string? password, string? inviteCode)
    {
        username = username?.Trim();
        inviteCode = inviteCode?.Trim().ToUpperInvariant();
        if (username == null || !UsernameRegex().IsMatch(username))
            return (false, 400, "账号名需为 3-32 位字母、数字、下划线或连字符", null);
        var passwordError = ValidatePassword(password);
        if (passwordError != null) return (false, 400, passwordError, null);
        if (string.IsNullOrEmpty(inviteCode)) return (false, 400, "请填写邀请码", null);

        lock (_gate)
        {
            var store = LoadLocked();
            var invite = store.Invites.FirstOrDefault(i => i.Code == inviteCode);
            if (invite == null || invite.Revoked || invite.UsedBy != null || IsExpired(invite.ExpiresAt))
                return (false, 400, "邀请码无效、已使用或已过期", null);
            if (store.Accounts.Any(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)))
                return (false, 409, "账号名已被占用", null);

            var salt = RandomNumberGenerator.GetBytes(16);
            var account = new Account
            {
                Id = store.NextId++,
                Username = username,
                Salt = Convert.ToBase64String(salt),
                PasswordHash = Convert.ToBase64String(HashPassword(password!, salt, Pbkdf2Iterations)),
                CreatedAt = Now(),
                InviteCode = inviteCode
            };
            store.Accounts.Add(account);
            invite.UsedBy = account.Id;
            invite.UsedAt = account.CreatedAt;
            SaveLocked();
            return (true, 201, "注册成功", account);
        }
    }

    /// <summary>登录成功返回明文 token（只返回这一次）。按 IP 计数失败次数，连续失败过多时临时锁定。</summary>
    public (bool Ok, int Status, string Message, string? Token, Account? Account, DateTime ExpiresAt) Login(string? username, string? password, string ip)
    {
        lock (_gate)
        {
            if (_loginFailures.TryGetValue(ip, out var f) && f.Failures >= MaxLoginFailures)
            {
                if (DateTime.UtcNow - f.WindowStart < LoginLockout)
                    return (false, 429, "登录失败次数过多，请 15 分钟后再试", null, null, default);
                _loginFailures.Remove(ip);
            }

            var store = LoadLocked();
            var account = store.Accounts.FirstOrDefault(a => string.Equals(a.Username, username?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (account == null || password == null || !VerifyPassword(account, password))
            {
                var current = _loginFailures.TryGetValue(ip, out var old) && DateTime.UtcNow - old.WindowStart < LoginLockout
                    ? (old.Failures + 1, old.WindowStart)
                    : (1, DateTime.UtcNow);
                _loginFailures[ip] = current;
                return (false, 401, "账号名或密码错误", null, null, default);
            }
            if (account.Disabled) return (false, 403, "该启动器账号已被禁用", null, null, default);

            _loginFailures.Remove(ip);
            var token = Base64Url(RandomNumberGenerator.GetBytes(32));
            var expires = DateTime.UtcNow + SessionLifetime;
            store.Sessions.Add(new Session
            {
                TokenHash = TokenHash(token),
                AccountId = account.Id,
                CreatedAt = Now(),
                ExpiresAt = expires.ToString("O")
            });
            account.LastLoginAt = Now();
            account.LastLoginIp = ip;
            SaveLocked();
            return (true, 200, "登录成功", token, account, expires);
        }
    }

    /// <summary>从请求头 Authorization: Bearer &lt;token&gt; 取出当前启动器账号；无效、过期或已禁用返回 null。</summary>
    public Account? Authenticate(HttpContext context)
    {
        var token = BearerToken(context);
        if (token == null) return null;
        var hash = TokenHash(token);
        lock (_gate)
        {
            var store = LoadLocked();
            var session = store.Sessions.FirstOrDefault(s => s.TokenHash == hash);
            if (session == null || IsExpired(session.ExpiresAt)) return null;
            var account = store.Accounts.FirstOrDefault(a => a.Id == session.AccountId);
            return account == null || account.Disabled ? null : account;
        }
    }

    public void Logout(HttpContext context)
    {
        var token = BearerToken(context);
        if (token == null) return;
        var hash = TokenHash(token);
        lock (_gate)
        {
            if (LoadLocked().Sessions.RemoveAll(s => s.TokenHash == hash) > 0) SaveLocked();
        }
    }

    public (bool Ok, int Status, string Message) ChangePassword(Account account, string? currentPassword, string? newPassword)
    {
        var passwordError = ValidatePassword(newPassword);
        if (passwordError != null) return (false, 400, passwordError);
        lock (_gate)
        {
            var stored = LoadLocked().Accounts.FirstOrDefault(a => a.Id == account.Id);
            if (stored == null) return (false, 404, "账号不存在");
            if (currentPassword == null || !VerifyPassword(stored, currentPassword)) return (false, 401, "当前密码错误");
            SetPasswordLocked(stored, newPassword!);
            SaveLocked();
            return (true, 200, "密码已修改，请重新登录");
        }
    }

    // ------------------------------------------------------------------ 后台：账号
    public JsonArray ListAccountsJson()
    {
        lock (_gate)
            return new JsonArray(LoadLocked().Accounts.Select(a => (JsonNode?)AccountJson(a)).ToArray());
    }

    // ------------------------------------------------------------------ 头衔
    public JsonArray ListTitlesJson()
    {
        lock (_gate)
            return new JsonArray(LoadLocked().Titles.Select(t => (JsonNode?)TitleJson(t)).ToArray());
    }

    /// <summary>新建头衔，或者修改 id 对应的头衔。</summary>
    public (bool Ok, string Message) SaveTitle(string? id, string? name, string? color)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 16) return (false, "头衔名称不能为空，也不能超过 16 个字");
        if (color == null || !ColorRegex().IsMatch(color)) return (false, "颜色格式不对，需要形如 #3366FF");
        if (!ColorReadable(color)) return (false, "这个颜色太亮或太暗，在启动器的深色或浅色主题下会看不清");
        lock (_gate)
        {
            var store = LoadLocked();
            var title = store.Titles.FirstOrDefault(t => t.Id == id);
            if (title == null)
            {
                if (!string.IsNullOrEmpty(id)) return (false, "头衔不存在");
                title = new Title { Id = Guid.NewGuid().ToString("N")[..12] };
                store.Titles.Add(title);
            }
            title.Name = name;
            title.Color = color.ToLowerInvariant();
            SaveLocked();
            return (true, "头衔已保存");
        }
    }

    /// <summary>删除头衔，拿着它的账号一并收回。</summary>
    public (bool Ok, string Message) DeleteTitle(string id)
    {
        lock (_gate)
        {
            var store = LoadLocked();
            if (store.Titles.RemoveAll(t => t.Id == id) == 0) return (false, "头衔不存在");
            foreach (var account in store.Accounts.Where(a => a.TitleId == id)) account.TitleId = null;
            SaveLocked();
            return (true, "头衔已删除");
        }
    }

    /// <summary>给账号分发头衔；titleId 为空表示收回。</summary>
    public (bool Ok, string Message) SetTitle(int accountId, string? titleId)
    {
        lock (_gate)
        {
            var store = LoadLocked();
            var account = store.Accounts.FirstOrDefault(a => a.Id == accountId);
            if (account == null) return (false, "账号不存在");
            if (!string.IsNullOrEmpty(titleId) && store.Titles.All(t => t.Id != titleId)) return (false, "头衔不存在");
            account.TitleId = string.IsNullOrEmpty(titleId) ? null : titleId;
            SaveLocked();
            return (true, account.TitleId == null ? "已收回头衔" : "头衔已分发");
        }
    }

    /// <summary>对外公开的玩家资料：账号名和头衔。</summary>
    public JsonObject? ProfileJson(int accountId)
    {
        lock (_gate)
        {
            var store = LoadLocked();
            var account = store.Accounts.FirstOrDefault(a => a.Id == accountId);
            if (account == null) return null;
            var title = store.Titles.FirstOrDefault(t => t.Id == account.TitleId);
            return new JsonObject
            {
                ["accountId"] = account.Id,
                ["username"] = account.Username,
                ["title"] = title == null ? null : new JsonObject { ["name"] = title.Name, ["color"] = title.Color }
            };
        }
    }

    private static JsonObject TitleJson(Title t) => new() { ["id"] = t.Id, ["name"] = t.Name, ["color"] = t.Color };

    /// <summary>深色和浅色主题下都要看得清，所以亮度不能太靠两头。</summary>
    private static bool ColorReadable(string color)
    {
        static double Channel(string hex)
        {
            var c = Convert.ToInt32(hex, 16) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Channel(color[1..3]) + 0.7152 * Channel(color[3..5]) + 0.0722 * Channel(color[5..7]);
        return luminance is >= 0.05 and <= 0.55;
    }

    public string? GetUsername(int id)
    {
        lock (_gate) return LoadLocked().Accounts.FirstOrDefault(a => a.Id == id)?.Username;
    }

    public (bool Ok, string Message) SetDisabled(int id, bool disabled)
    {
        lock (_gate)
        {
            var store = LoadLocked();
            var account = store.Accounts.FirstOrDefault(a => a.Id == id);
            if (account == null) return (false, "账号不存在");
            account.Disabled = disabled;
            if (disabled) store.Sessions.RemoveAll(s => s.AccountId == id);
            SaveLocked();
            return (true, disabled ? "账号已禁用" : "账号已启用");
        }
    }

    /// <summary>重置为随机临时密码并注销该账号的所有登录；临时密码只返回这一次。</summary>
    public (bool Ok, string Message, string? TemporaryPassword) ResetPassword(int id)
    {
        lock (_gate)
        {
            var account = LoadLocked().Accounts.FirstOrDefault(a => a.Id == id);
            if (account == null) return (false, "账号不存在", null);
            var temp = RandomCode(12);
            SetPasswordLocked(account, temp);
            SaveLocked();
            return (true, "密码已重置，请把临时密码告知用户", temp);
        }
    }

    // ------------------------------------------------------------------ 后台：邀请码
    public JsonArray ListInvitesJson()
    {
        lock (_gate)
        {
            var store = LoadLocked();
            return new JsonArray(store.Invites.OrderByDescending(i => i.CreatedAt).Select(i => (JsonNode?)new JsonObject
            {
                ["code"] = i.Code,
                ["createdAt"] = i.CreatedAt,
                ["createdBy"] = i.CreatedBy,
                ["expiresAt"] = i.ExpiresAt,
                ["note"] = i.Note,
                ["revoked"] = i.Revoked,
                ["usedBy"] = i.UsedBy,
                ["usedByName"] = i.UsedBy == null ? null : store.Accounts.FirstOrDefault(a => a.Id == i.UsedBy)?.Username,
                ["usedAt"] = i.UsedAt,
                ["status"] = i.UsedBy != null ? "used" : i.Revoked ? "revoked" : IsExpired(i.ExpiresAt) ? "expired" : "available"
            }).ToArray());
        }
    }

    public (bool Ok, string Message, List<string> Codes) CreateInvites(int count, DateTime? expiresAt, string? note, string createdBy)
    {
        if (count is < 1 or > 100) return (false, "一次可生成 1-100 个邀请码", new List<string>());
        if (expiresAt != null && expiresAt <= DateTime.UtcNow) return (false, "过期时间必须晚于现在", new List<string>());
        lock (_gate)
        {
            var store = LoadLocked();
            var codes = new List<string>();
            while (codes.Count < count)
            {
                var code = $"FY-{RandomCode(4)}-{RandomCode(4)}-{RandomCode(4)}";
                if (store.Invites.Any(i => i.Code == code) || codes.Contains(code)) continue;
                codes.Add(code);
                store.Invites.Add(new Invite
                {
                    Code = code,
                    CreatedAt = Now(),
                    CreatedBy = createdBy,
                    ExpiresAt = expiresAt?.ToUniversalTime().ToString("O"),
                    Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 200)]
                });
            }
            SaveLocked();
            return (true, $"已生成 {count} 个邀请码", codes);
        }
    }

    public (bool Ok, string Message) RevokeInvite(string code)
    {
        lock (_gate)
        {
            var invite = LoadLocked().Invites.FirstOrDefault(i => i.Code == code.Trim().ToUpperInvariant());
            if (invite == null) return (false, "邀请码不存在");
            if (invite.UsedBy != null) return (false, "邀请码已被使用，无法作废");
            invite.Revoked = true;
            SaveLocked();
            return (true, "邀请码已作废");
        }
    }

    public static JsonObject AccountJson(Account a) => new()
    {
        ["id"] = a.Id,
        ["username"] = a.Username,
        ["createdAt"] = a.CreatedAt,
        ["inviteCode"] = a.InviteCode,
        ["disabled"] = a.Disabled,
        ["lastLoginAt"] = a.LastLoginAt,
        ["lastLoginIp"] = a.LastLoginIp,
        ["titleId"] = a.TitleId
    };

    // ------------------------------------------------------------------ 工具
    private static string? ValidatePassword(string? password)
    {
        if (password == null || password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
            return $"密码长度需为 {MinPasswordLength}-{MaxPasswordLength} 位";
        return null;
    }

    private void SetPasswordLocked(Account account, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        account.Salt = Convert.ToBase64String(salt);
        account.Iterations = Pbkdf2Iterations;
        account.PasswordHash = Convert.ToBase64String(HashPassword(password, salt, Pbkdf2Iterations));
        _store!.Sessions.RemoveAll(s => s.AccountId == account.Id);
    }

    private static byte[] HashPassword(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static bool VerifyPassword(Account account, string password)
    {
        try
        {
            var expected = Convert.FromBase64String(account.PasswordHash);
            var actual = HashPassword(password, Convert.FromBase64String(account.Salt), account.Iterations);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string? BearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.FirstOrDefault();
        string? token = null;
        if (header != null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = header[7..].Trim();
        // 浏览器/WebView 里的 WebSocket 不能自定义请求头，控制连接 /launcher/ws 允许把 token 放在查询串里。
        else if (context.WebSockets.IsWebSocketRequest && context.Request.Path.StartsWithSegments("/launcher/ws"))
            token = context.Request.Query["token"].ToString().Trim();
        return token is { Length: > 0 and <= 128 } ? token : null;
    }

    /// <summary>按名字搜索未禁用的账号（不区分大小写，完全匹配排最前，其次是前缀匹配）。</summary>
    public List<(int Id, string Username)> Search(string? query, int limit)
    {
        query = query?.Trim() ?? "";
        if (query.Length == 0) return [];
        lock (_gate)
        {
            return LoadLocked().Accounts
                .Where(a => !a.Disabled && a.Username.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => string.Equals(a.Username, query, StringComparison.OrdinalIgnoreCase) ? 0 : a.Username.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(a => a.Username.Length).ThenBy(a => a.Username, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(a => (a.Id, a.Username))
                .ToList();
        }
    }

    /// <summary>按账号名（不区分大小写）查找未禁用的启动器账号。</summary>
    public (int Id, string Username)? FindByUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        lock (_gate)
        {
            var account = LoadLocked().Accounts.FirstOrDefault(a => !a.Disabled && string.Equals(a.Username, username.Trim(), StringComparison.OrdinalIgnoreCase));
            return account == null ? null : (account.Id, account.Username);
        }
    }

    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Now() => DateTime.UtcNow.ToString("O");
    private static bool IsExpired(string? isoTime) =>
        isoTime != null && DateTime.TryParse(isoTime, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) && t.ToUniversalTime() <= DateTime.UtcNow;

    private static string RandomCode(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = InviteAlphabet[RandomNumberGenerator.GetInt32(InviteAlphabet.Length)];
        return new string(chars);
    }

    // ------------------------------------------------------------------ 存储
    private Store LoadLocked()
    {
        if (_store != null) return _store;
        var store = new Store();
        if (File.Exists(StorePath) && JsonNode.Parse(File.ReadAllText(StorePath)) is JsonObject root)
        {
            store.NextId = Int(root["nextId"]) ?? 1;
            foreach (var n in Arr(root["accounts"]))
                store.Accounts.Add(new Account
                {
                    Id = Int(n["id"]) ?? 0,
                    Username = Str(n["username"]) ?? "",
                    PasswordHash = Str(n["passwordHash"]) ?? "",
                    Salt = Str(n["salt"]) ?? "",
                    Iterations = Int(n["iterations"]) ?? Pbkdf2Iterations,
                    CreatedAt = Str(n["createdAt"]) ?? "",
                    InviteCode = Str(n["inviteCode"]) ?? "",
                    Disabled = Bool(n["disabled"]),
                    LastLoginAt = Str(n["lastLoginAt"]),
                    LastLoginIp = Str(n["lastLoginIp"]),
                    TitleId = Str(n["titleId"])
                });
            foreach (var n in Arr(root["titles"]))
                store.Titles.Add(new Title
                {
                    Id = Str(n["id"]) ?? "",
                    Name = Str(n["name"]) ?? "",
                    Color = Str(n["color"]) ?? ""
                });
            foreach (var n in Arr(root["invites"]))
                store.Invites.Add(new Invite
                {
                    Code = Str(n["code"]) ?? "",
                    CreatedAt = Str(n["createdAt"]) ?? "",
                    CreatedBy = Str(n["createdBy"]) ?? "",
                    ExpiresAt = Str(n["expiresAt"]),
                    Note = Str(n["note"]),
                    Revoked = Bool(n["revoked"]),
                    UsedBy = Int(n["usedBy"]),
                    UsedAt = Str(n["usedAt"])
                });
            foreach (var n in Arr(root["sessions"]))
                store.Sessions.Add(new Session
                {
                    TokenHash = Str(n["tokenHash"]) ?? "",
                    AccountId = Int(n["accountId"]) ?? 0,
                    CreatedAt = Str(n["createdAt"]) ?? "",
                    ExpiresAt = Str(n["expiresAt"]) ?? ""
                });
        }
        return _store = store;
    }

    private void SaveLocked()
    {
        var store = _store!;
        store.Sessions.RemoveAll(s => IsExpired(s.ExpiresAt));
        var root = new JsonObject
        {
            ["nextId"] = store.NextId,
            ["accounts"] = new JsonArray(store.Accounts.Select(a => (JsonNode?)new JsonObject
            {
                ["id"] = a.Id,
                ["username"] = a.Username,
                ["passwordHash"] = a.PasswordHash,
                ["salt"] = a.Salt,
                ["iterations"] = a.Iterations,
                ["createdAt"] = a.CreatedAt,
                ["inviteCode"] = a.InviteCode,
                ["disabled"] = a.Disabled,
                ["lastLoginAt"] = a.LastLoginAt,
                ["lastLoginIp"] = a.LastLoginIp,
                ["titleId"] = a.TitleId
            }).ToArray()),
            ["titles"] = new JsonArray(store.Titles.Select(t => (JsonNode?)TitleJson(t)).ToArray()),
            ["invites"] = new JsonArray(store.Invites.Select(i => (JsonNode?)new JsonObject
            {
                ["code"] = i.Code,
                ["createdAt"] = i.CreatedAt,
                ["createdBy"] = i.CreatedBy,
                ["expiresAt"] = i.ExpiresAt,
                ["note"] = i.Note,
                ["revoked"] = i.Revoked,
                ["usedBy"] = i.UsedBy,
                ["usedAt"] = i.UsedAt
            }).ToArray()),
            ["sessions"] = new JsonArray(store.Sessions.Select(s => (JsonNode?)new JsonObject
            {
                ["tokenHash"] = s.TokenHash,
                ["accountId"] = s.AccountId,
                ["createdAt"] = s.CreatedAt,
                ["expiresAt"] = s.ExpiresAt
            }).ToArray())
        };
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        var temp = StorePath + ".tmp";
        try
        {
            File.WriteAllText(temp, root.ToJsonString());
            File.Move(temp, StorePath, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static IEnumerable<JsonNode> Arr(JsonNode? node) => node is JsonArray a ? a.Where(x => x != null).Select(x => x!) : Enumerable.Empty<JsonNode>();
    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    private static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
