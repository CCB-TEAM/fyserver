using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using fyserver.Models;

namespace fyserver.Services;

/// <summary>Database-backed implementation of the NestJS device-account linking contract.</summary>
public sealed class PlayerLoginAccountService(AppDataStoreService appData)
{
    private const string StoreKey = "players:linked-accounts:v1";
    private const int Iterations = 210_000;
    private readonly object _gate = new();
    private JsonObject? _accounts;

    public bool TryAuthenticate(string username, string password, out int playerId, out string canonicalUsername)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_accounts![Normalize(username)] is JsonObject account &&
                int.TryParse(account["playerId"]?.ToString(), out playerId) &&
                Verify(password, account["passwordHash"]?.GetValue<string>() ?? ""))
            {
                canonicalUsername = account["username"]?.GetValue<string>() ?? username.Trim();
                return true;
            }
            playerId = 0;
            canonicalUsername = "";
            return false;
        }
    }

    public bool TryGetPlayerId(string identity, out int playerId)
    {
        const string prefix = "linker:";
        playerId = 0;
        if (!identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        lock (_gate)
        {
            EnsureLoaded();
            return _accounts![Normalize(identity[prefix.Length..])] is JsonObject account &&
                   int.TryParse(account["playerId"]?.ToString(), out playerId);
        }
    }

    public string CreateLink(User? user, string? username, string? password)
    {
        if (user == null || user.Banned) return "CREATE:USER_ALREADY_LINKED";
        if (!string.IsNullOrEmpty(user.LinkerAccount)) return "CREATE:USER_ALREADY_LINKED";
        var name = username?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(password)) return "CREATE:LINKER_ALREADY_EXISTS";
        lock (_gate)
        {
            EnsureLoaded();
            var key = Normalize(name);
            if (_accounts!.ContainsKey(key)) return "CREATE:LINKER_ALREADY_EXISTS";
            _accounts[key] = new JsonObject
            {
                ["username"] = name,
                ["playerId"] = user.Id,
                ["passwordHash"] = Hash(password),
                ["createdAt"] = DateTime.UtcNow.ToString("O")
            };
            user.LinkerAccount = name;
            Save();
        }
        return "CREATE:OK";
    }

    public string VerifyLink(User? user, string? username, string? password, out string? linkedUsername)
    {
        linkedUsername = null;
        var name = username?.Trim() ?? "";
        int playerId;
        string canonical;
        lock (_gate)
        {
            EnsureLoaded();
            if (_accounts![Normalize(name)] is not JsonObject account) return "LINK:USER_NOT_FOUND";
            if (!Verify(password ?? "", account["passwordHash"]?.GetValue<string>() ?? "")) return "LINK:WRONG_PASSWORD";
            if (!int.TryParse(account["playerId"]?.ToString(), out playerId)) return "LINK:USER_NOT_FOUND";
            canonical = account["username"]?.GetValue<string>() ?? name;
        }
        if (user?.LinkerAccount is { Length: > 0 } existing && !string.Equals(Normalize(existing), Normalize(canonical), StringComparison.Ordinal))
            return "LINK:PLATFORM_LINK_EXISTS";
        if (user != null && user.Id == playerId)
        {
            user.LinkerAccount = canonical;
            linkedUsername = canonical;
        }
        return "LINK:OK";
    }

    private void EnsureLoaded()
    {
        if (_accounts != null) return;
        if (!appData.IsReady) throw new InvalidOperationException("玩家数据库尚未初始化");
        var json = appData.Get(StoreKey);
        _accounts = json == null ? new JsonObject() : JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        if (json == null) Save();
    }

    private void Save() => appData.Set(StoreKey, _accounts!.ToJsonString());
    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
    }

    private static bool Verify(string password, string encoded)
    {
        var parts = encoded.Split(':', 2);
        if (parts.Length != 2) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException) { return false; }
    }
}
