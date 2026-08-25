using System.Text.Json;
using fyserver.Models;
using fyserver.Serialization;

namespace fyserver.Services;

/// <summary>
/// 服务器运行配置（端口/地址/反作弊开关）。
/// 兼容旧 setting.json（System.Text.Json 读取，字段名保持原样）。
/// </summary>
public class ServerOptions
{
    public int portWs { get; set; } = 5232;
    public int portHttp { get; set; } = 5231;
    public bool bancheat { get; set; } = false;
    public string ip { get; set; } = "0.0.0.0";

    // 监听地址固定 0.0.0.0（与原实现一致）；R 版本用配置的 ip 生成客户端可达地址
    public string GetAddressWs() => $"http://0.0.0.0:{portWs}";
    public string GetAddressWsR() => $"ws://{ip}:{portWs}";
    public string GetAddressHttp() => $"http://0.0.0.0:{portHttp}";
    public string GetAddressHttpR() => $"http://{ip}:{portHttp}";

    /// <summary>读取 ./setting.json；不存在则写一份默认配置。</summary>
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

        portWs = loaded.portWs;
        portHttp = loaded.portHttp;
        ip = loaded.ip;
        bancheat = loaded.bancheat;
    }

    public void Write()
    {
        File.WriteAllText("./setting.json", JsonSerializer.Serialize(this, ConfigJsonContext.Default.ServerOptions));
    }
}
