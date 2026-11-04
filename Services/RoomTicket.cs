using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace fyserver.Services;

/// <summary>云端签发的进房票据里与本服务端有关的内容。</summary>
public sealed record RoomTicketClaims(string RoomId, long AccountId, string Name, string Role, long ExpiresAt);

/// <summary>
/// 嵌入模式下校验云端签发的进房票据（ES256 JWS，签名为 IEEE P1363 的 r||s）。
/// 公钥由启动器通过 --ticket-pubkey 传入，见 docs/launcher-cloud-api.md 第 3 节。
/// </summary>
public sealed class RoomTicketVerifier : IDisposable
{
    public const string HeaderName = "X-Fy-Ticket";
    private const string ItemKey = "fy.roomTicket";
    /// <summary>容忍两台电脑之间的时钟误差。</summary>
    private const long ClockSkewSeconds = 120;

    private readonly ECDsa _key;
    private readonly string _roomId;

    public RoomTicketVerifier(string spkiBase64, string roomId)
    {
        _key = ECDsa.Create();
        _key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spkiBase64), out _);
        _roomId = roomId;
    }

    public RoomTicketClaims? Verify(string? ticket, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(ticket) || ticket.Length > 4096) return null;
        var parts = ticket.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            using var header = JsonDocument.Parse(Base64Url(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "ES256") return null;

            var signature = Base64Url(parts[2]);
            var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            if (!_key.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return null;

            using var payload = JsonDocument.Parse(Base64Url(parts[1]));
            var root = payload.RootElement;
            var claims = new RoomTicketClaims(
                root.GetProperty("rid").GetString() ?? "",
                root.GetProperty("aid").GetInt64(),
                root.GetProperty("name").GetString() ?? "",
                root.GetProperty("role").GetString() ?? "",
                root.GetProperty("exp").GetInt64());
            if (claims.RoomId != _roomId || string.IsNullOrWhiteSpace(claims.Name)) return null;
            if (claims.ExpiresAt + ClockSkewSeconds < now.ToUnixTimeSeconds()) return null;
            return claims;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or CryptographicException)
        {
            return null;
        }
    }

    public static void Attach(HttpContext context, RoomTicketClaims claims) => context.Items[ItemKey] = claims;

    public static RoomTicketClaims? Current(HttpContext context) => context.Items.TryGetValue(ItemKey, out var value) ? value as RoomTicketClaims : null;

    private static byte[] Base64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException() };
        return Convert.FromBase64String(s);
    }

    public void Dispose() => _key.Dispose();
}
