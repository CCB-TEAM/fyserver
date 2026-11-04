using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace fyserver.Services;

/// <summary>
/// 进房票据：ES256（ECDSA P-256 + SHA-256）签名的 JWS compact 字符串
/// base64url(header).base64url(payload).base64url(signature)，签名为 64 字节 r||s（IEEE P1363）。
///
/// header  {"alg":"ES256","typ":"JWT","kid":"&lt;公钥指纹&gt;"}
/// payload {"rid":房间id,"aid":启动器账号id,"name":启动器用户名,"role":"host"|"member","iat":签发秒,"exp":过期秒}
///
/// 房主本机的嵌入版 fyserver 用 GET /launcher/rooms/pubkey 取到的公钥离线验证。
/// 私钥在首次启动时生成并保存在 ./data/room-signing-key.pem（PKCS#8），不进仓库、不出现在任何接口里。
/// </summary>
public sealed class RoomTicketService
{
    private const string KeyPath = "./data/room-signing-key.pem";
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(15);

    private readonly ECDsa _key;
    public string KeyId { get; }
    public byte[] PublicKeySpki { get; }

    public RoomTicketService()
    {
        _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(KeyPath))
        {
            _key.ImportFromPem(File.ReadAllText(KeyPath));
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
            var temp = KeyPath + ".tmp";
            File.WriteAllText(temp, _key.ExportPkcs8PrivateKeyPem());
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, KeyPath, true);
            Console.WriteLine("已生成进房票据签名密钥 ./data/room-signing-key.pem（请随 data 目录一起备份）");
        }
        PublicKeySpki = _key.ExportSubjectPublicKeyInfo();
        KeyId = Convert.ToHexStringLower(SHA256.HashData(PublicKeySpki))[..16];
    }

    public JsonObject PublicKeyJson() => new()
    {
        ["alg"] = "ES256",
        ["kid"] = KeyId,
        ["spki"] = Convert.ToBase64String(PublicKeySpki),
        ["pem"] = _key.ExportSubjectPublicKeyInfoPem(),
        ["ticketLifetimeSeconds"] = (int)TicketLifetime.TotalSeconds
    };

    public sealed record Ticket(string RoomId, int AccountId, string Name, string Role, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);

    public (string Token, DateTimeOffset ExpiresAt) Issue(string roomId, int accountId, string name, string role)
    {
        var now = DateTimeOffset.UtcNow;
        var exp = now + TicketLifetime;
        var header = new JsonObject { ["alg"] = "ES256", ["typ"] = "JWT", ["kid"] = KeyId };
        var payload = new JsonObject
        {
            ["rid"] = roomId,
            ["aid"] = accountId,
            ["name"] = name,
            ["role"] = role,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = exp.ToUnixTimeSeconds()
        };
        var signingInput = B64(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + B64(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var signature = _key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return (signingInput + "." + B64(signature), exp);
    }

    /// <summary>验证签名；allowExpired 为 true 时不检查过期（frp 重连等场景另行核对房间成员）。</summary>
    public Ticket? Verify(string? token, bool allowExpired = false)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 4096) return null;
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var header = JsonNode.Parse(FromB64(parts[0])) as JsonObject;
            if (header?["alg"]?.GetValue<string>() != "ES256") return null;
            var signature = FromB64(parts[2]);
            if (!_key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return null;
            var payload = JsonNode.Parse(FromB64(parts[1])) as JsonObject;
            if (payload == null) return null;
            var ticket = new Ticket(
                payload["rid"]!.GetValue<string>(),
                payload["aid"]!.GetValue<int>(),
                payload["name"]?.GetValue<string>() ?? "",
                payload["role"]!.GetValue<string>(),
                DateTimeOffset.FromUnixTimeSeconds(payload["iat"]!.GetValue<long>()),
                DateTimeOffset.FromUnixTimeSeconds(payload["exp"]!.GetValue<long>()));
            if (!allowExpired && ticket.ExpiresAt < DateTimeOffset.UtcNow) return null;
            return ticket;
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or InvalidOperationException or NullReferenceException or CryptographicException)
        {
            return null;
        }
    }

    private static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }
}
