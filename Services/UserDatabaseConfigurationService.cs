using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace fyserver.Services;

public sealed record UserDatabaseSettings(string Provider, string Host, int Port, string Database, string Username,
    string Password, bool RequireSsl);

/// <summary>用户数据库配置。密码使用独立随机密钥通过 AES-GCM 加密，不会写入后台响应。</summary>
public sealed class UserDatabaseConfigurationService
{
    private const string ConfigPath = "./data/user-database.json";
    private const string KeyPath = "./data/user-database.key";
    private readonly object _gate = new();
    public bool IsConfigured => File.Exists(ConfigPath);

    public UserDatabaseSettings? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(ConfigPath)) return null;
            var root = JsonNode.Parse(File.ReadAllText(ConfigPath))?.AsObject() ?? throw new InvalidDataException("用户数据库配置无效");
            var provider = root["provider"]?.GetValue<string>() ?? "";
            if (provider == "local") return new(provider, "", 0, "", "", "", false);
            var password = Decrypt(root["password"]?.GetValue<string>() ?? "");
            return new(provider, root["host"]?.GetValue<string>() ?? "", root["port"]?.GetValue<int>() ?? 0,
                root["database"]?.GetValue<string>() ?? "", root["username"]?.GetValue<string>() ?? "", password,
                root["requireSsl"]?.GetValue<bool>() ?? true);
        }
    }

    public void Save(UserDatabaseSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var root = new JsonObject
            {
                ["version"] = 1, ["provider"] = settings.Provider, ["host"] = settings.Host,
                ["port"] = settings.Port, ["database"] = settings.Database, ["username"] = settings.Username,
                ["requireSsl"] = settings.RequireSsl,
                ["password"] = settings.Provider == "local" ? "" : Encrypt(settings.Password)
            };
            var temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(new() { WriteIndented = true }));
            File.Move(temp, ConfigPath, true);
        }
    }

    public JsonObject PublicStatus()
    {
        try
        {
            var settings = Load();
            return settings == null ? new JsonObject { ["configured"] = false } : new JsonObject
            {
                ["configured"] = true, ["provider"] = settings.Provider, ["host"] = settings.Host,
                ["port"] = settings.Port, ["database"] = settings.Database, ["username"] = settings.Username,
                ["requireSsl"] = settings.RequireSsl, ["hasPassword"] = !string.IsNullOrEmpty(settings.Password)
            };
        }
        catch (Exception ex) { return new JsonObject { ["configured"] = true, ["error"] = ex.Message }; }
    }

    private static byte[] GetKey()
    {
        if (File.Exists(KeyPath)) return File.ReadAllBytes(KeyPath);
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        var key = RandomNumberGenerator.GetBytes(32);
        using var stream = new FileStream(KeyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(key);
        return key;
    }

    private static string Encrypt(string value)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(value);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(GetKey(), 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    private static string Decrypt(string value)
    {
        var payload = Convert.FromBase64String(value);
        if (payload.Length < 28) throw new InvalidDataException("数据库密码密文无效");
        var plain = new byte[payload.Length - 28];
        using var aes = new AesGcm(GetKey(), 16);
        aes.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(28), payload.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }
}
