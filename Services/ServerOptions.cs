using System.Text.Json;
using fyserver.Models;
using fyserver.Serialization;

namespace fyserver.Services;

/// <summary>
/// 服务器运行配置（端口/地址/反作弊开关）。HTTP 与 WebSocket 共用 portHttp 端口。
/// 兼容旧 setting.json（System.Text.Json 读取，字段名保持原样）。
/// </summary>
public class ServerOptions
{
    public int portHttp { get; set; } = 5231;
    public bool bancheat { get; set; } = false;
    public string ip { get; set; } = "0.0.0.0";
    /// <summary>管理 API 密钥；为空时管理接口仅允许 loopback 访问。</summary>
    public string adminApiKey { get; set; } = "";

    // 监听地址固定 0.0.0.0（与原实现一致）；R 版本用配置的 ip 生成客户端可达地址（HTTP 与 WebSocket 合并到同一端口）
    // WebSocket 不再单独监听端口：客户端直连 HTTP 端口的根路径升级（ws://<ip>:<portHttp>/）
    public string GetAddressHttp() => $"http://0.0.0.0:{portHttp}";
    public string GetAddressHttpR() => $"http://{ip}:{portHttp}";
    public string GetAddressWsR() => $"ws://{ip}:{portHttp}/";

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
