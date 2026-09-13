using System.Text.Json;
using fyserver.Models;
using fyserver.Serialization;

namespace fyserver.Services;

/// <summary>
/// 鏈嶅姟鍣ㄨ繍琛岄厤缃紙绔彛/鍦板潃/鍙嶄綔寮婂紑鍏筹級銆侶TTP 涓?WebSocket 鍏辩敤 portHttp 绔彛銆?
/// 鍏煎鏃?setting.json锛圫ystem.Text.Json 璇诲彇锛屽瓧娈靛悕淇濇寔鍘熸牱锛夈€?
/// </summary>
public class ServerOptions
{
    public int portHttp { get; set; } = 5231;
    public bool bancheat { get; set; } = false;
    public string ip { get; set; } = "0.0.0.0";
    /// <summary>绠＄悊 API 瀵嗛挜锛涗负绌烘椂绠＄悊鎺ュ彛浠呭厑璁?loopback 璁块棶銆?/summary>
    public string adminApiKey { get; set; } = "";

    // 鐩戝惉鍦板潃鍥哄畾 0.0.0.0锛堜笌鍘熷疄鐜颁竴鑷达級锛汻 鐗堟湰鐢ㄩ厤缃殑 ip 鐢熸垚瀹㈡埛绔彲杈惧湴鍧€锛圚TTP 涓?WebSocket 鍚堝苟鍒板悓涓€绔彛锛?
    // WebSocket 涓嶅啀鍗曠嫭鐩戝惉绔彛锛氬鎴风鐩磋繛 HTTP 绔彛鐨勬牴璺緞鍗囩骇锛坵s://<ip>:<portHttp>/锛?
    public string GetAddressHttp() => $"http://0.0.0.0:{portHttp}";
    public string GetAddressHttpR() => $"http://{ip}:{portHttp}";
    public string GetAddressWsR() => $"ws://{ip}:{portHttp}/";

    /// <summary>璇诲彇 ./setting.json锛涗笉瀛樺湪鍒欏啓涓€浠介粯璁ら厤缃€?/summary>
    public void ReadFromFile()
    {
        const string path = "./setting.json";
        if (!File.Exists(path))
        {
            Write();
            return;
        }

        var loaded = JsonSerializer.Deserialize(File.ReadAllText(path), ConfigJsonContext.Default.ServerOptions);
        if (loaded == null)
        {
            Write();
            return;
        }

        portHttp = loaded.portHttp;
        ip = loaded.ip;
        bancheat = loaded.bancheat;
        adminApiKey = loaded.adminApiKey ?? "";
    }

    public void Write()
    {
        File.WriteAllText("./setting.json", JsonSerializer.Serialize(this, ConfigJsonContext.Default.ServerOptions));
    }
}
