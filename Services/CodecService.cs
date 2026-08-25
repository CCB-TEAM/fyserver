using System.Text;
using System.Text.Json;
using fyserver.Models;
using fyserver.Serialization;

namespace fyserver.Services;

/// <summary>
/// 消息编解码服务（纯 C# 实现，替代原 codec.dll 的 P/Invoke）。
/// 编码格式：{tableIndex:2} {dataLength:6} {b64ActionId:4} {key:keyLength} {b64Cipher}，
/// 其中 key 长度由 SALT_LENGTH_TABLE[tableIndex] 查得，密文为 plaintext XOR key 循环，actionId 为 3 字节 XOR key 前 3 字节。
/// </summary>
public class CodecService
{
    // SALT_LENGTH_TABLE（75 个元素）——取自 codec.dll 原始 C 源码（注意：第 50 位起与早期提取的 C# 版有差异，以源码为准）
    private static readonly int[] SaltLengthTable =
    {
        47, 53, 73, 55, 61, 103, 47, 103, 33, 45,
        73, 37, 97, 71, 39, 71, 31, 61, 83, 101,
        53, 97, 79, 75, 37, 31, 33, 69, 43, 63,
        39, 43, 79, 55, 49, 73, 83, 67, 59, 69,
        103, 39, 47, 37, 41, 71, 89, 55, 49, 45,
        33, 45, 69, 49, 43, 53, 59, 31, 59, 101,
        61, 41, 79, 75, 83, 89, 75, 67, 41, 89,
        63, 101, 67, 63, 97
    };

    // key 字符集（与原 codec.dll 一致：Base64 字母表）
    private const string B64Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    /// <summary>加密：plaintext 与随机 key 异或，输出 base64 编码串。</summary>
    public string Encode(string plaintext, int actionId)
    {
        int tableIndex = Random.Shared.Next(SaltLengthTable.Length);
        int keyLength = SaltLengthTable[tableIndex];

        // 随机 key（与原 codec.dll 相同：从 Base64 字符表选取）
        var keyChars = new char[keyLength];
        for (int i = 0; i < keyLength; i++)
            keyChars[i] = B64Chars[Random.Shared.Next(B64Chars.Length)];
        string keyStr = new string(keyChars);
        byte[] keyBytes = Encoding.ASCII.GetBytes(keyStr);

        byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);

        // actionId 编码为 3 字节并与 key 前 3 字节异或
        byte[] actionIdBytes =
        {
            (byte)((actionId >> 16) & 0xFF),
            (byte)((actionId >> 8) & 0xFF),
            (byte)(actionId & 0xFF)
        };
        byte[] encActionId =
        {
            (byte)(actionIdBytes[0] ^ keyBytes[0]),
            (byte)(actionIdBytes[1] ^ keyBytes[1 % keyLength]),
            (byte)(actionIdBytes[2] ^ keyBytes[2 % keyLength])
        };
        string b64ActionId = Convert.ToBase64String(encActionId);

        // 明文与 key 循环异或
        byte[] cipher = new byte[plainBytes.Length];
        for (int i = 0; i < plainBytes.Length; i++)
            cipher[i] = (byte)(plainBytes[i] ^ keyBytes[i % keyLength]);
        string b64Cipher = Convert.ToBase64String(cipher);

        return $"{tableIndex:D2}{plainBytes.Length:D6}{b64ActionId}{keyStr}{b64Cipher}";
    }

    /// <summary>解码：还原明文并取出 actionId。</summary>
    public string Decode(string encoded, out int actionId)
    {
        int tableIndex = int.Parse(encoded.Substring(0, 2));
        int dataLength = int.Parse(encoded.Substring(2, 6));
        string body = encoded.Substring(8);
        int keyLength = SaltLengthTable[tableIndex];
        string b64ActionId = body.Substring(0, 4);
        string keyStr = body.Substring(4, keyLength);
        string b64Cipher = body.Substring(4 + keyLength);

        byte[] actionIdBytes = Convert.FromBase64String(b64ActionId);
        byte[] keyBytes = Encoding.ASCII.GetBytes(keyStr);
        byte[] cipherBytes = Convert.FromBase64String(b64Cipher);

        byte[] plainBytes = new byte[cipherBytes.Length];
        for (int i = 0; i < cipherBytes.Length; i++)
            plainBytes[i] = (byte)(cipherBytes[i] ^ keyBytes[i % keyLength]);
        string plaintext = Encoding.UTF8.GetString(plainBytes);

        int actionIdHigh = actionIdBytes[0] ^ keyBytes[0];
        int actionIdMid = actionIdBytes[1] ^ keyBytes[1 % keyLength];
        int actionIdLow = actionIdBytes[2] ^ keyBytes[2 % keyLength];
        actionId = (actionIdHigh << 16) | (actionIdMid << 8) | actionIdLow;
        return plaintext;
    }

    /// <summary>加密一个对局动作。</summary>
    public string Encode<T>(T? abc) where T : MatchAction
    {
        if (abc == null)
            throw new ArgumentNullException(nameof(abc));

        var typeInfo = FyJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw new InvalidOperationException($"类型 {typeof(T).FullName} 未注册到 FyJsonContext");
        var plaintext = JsonSerializer.Serialize(abc, typeInfo);
        return Encode(plaintext, abc.SendActionId);
    }
}
