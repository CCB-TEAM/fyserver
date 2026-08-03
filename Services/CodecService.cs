using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using fyserver.Models;

namespace fyserver.Services;

/// <summary>
/// 基于 codec.dll 的消息编解码服务（替代原 http 类的静态 Encode/Decode）。
/// </summary>
public class CodecService
{
    [DllImport("codec.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int _xR7qM2vP(string plaintext, int actionId, StringBuilder output, int outputBufSize);

    [DllImport("codec.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int _kW3nJ9tF(string encoded, StringBuilder plaintextOut, int plaintextBufSize, ref int actionIdOut);

    public string Encode(string plaintext, int actionId)
    {
        StringBuilder sb = new StringBuilder(plaintext.Length * 4 + 256);
        int len = _xR7qM2vP(plaintext, actionId, sb, sb.Capacity);
        if (len < 0)
            throw new InvalidOperationException("编码失败");
        return sb.ToString();
    }

    public string Encode<T>(T? abc) where T : MatchAction
    {
        if (abc == null)
            throw new ArgumentNullException(nameof(abc));

        var plaintext = JsonSerializer.Serialize(abc, GameConstants.JsonOptions);
        StringBuilder sb = new StringBuilder(plaintext.Length * 4 + 256);
        int len = _xR7qM2vP(plaintext, abc.SendActionId, sb, sb.Capacity);
        if (len < 0)
            throw new InvalidOperationException("编码失败");
        return sb.ToString();
    }

    public string Decode(string encoded, out int actionId)
    {
        StringBuilder sb = new StringBuilder(encoded.Length + 256);
        actionId = 0;
        int len = _kW3nJ9tF(encoded, sb, sb.Capacity, ref actionId);
        if (len < 0)
            throw new InvalidOperationException("解码失败");
        return sb.ToString();
    }
}
