using System.Text.Json;
using System.Text.Json.Nodes;

namespace fyserver.Services;

/// <summary>
/// 首页/公告配置（config/frontpage.json）读写。
/// 使用 JsonNode 走 DOM API，不依赖反射序列化，NativeAOT 安全。
/// 保存前自动备份到 config/frontpage.json.bak。
/// </summary>
public class FrontpageConfigService
{
    public const string ConfigPath = "./config/frontpage.json";
    private const string BackupPath = "./config/frontpage.json.bak";

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    /// <summary>读取原始 JSON 文本；文件不存在时返回空对象。</summary>
    public string ReadRaw()
    {
        if (!File.Exists(ConfigPath))
            return "{ }";
        return File.ReadAllText(ConfigPath);
    }

    /// <summary>保存 JSON 文本，保存前校验格式并备份原文件。</summary>
    public (bool ok, string message) SaveRaw(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (false, "内容为空，未保存");

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            return (false, $"JSON 格式错误：{ex.Message}");
        }

        if (node == null)
            return (false, "JSON 根节点为空，未保存");

        if (File.Exists(ConfigPath))
        {
            try
            {
                File.Copy(ConfigPath, BackupPath, overwrite: true);
            }
            catch (IOException)
            {
                // 备份失败不阻塞保存
            }
        }

        File.WriteAllText(ConfigPath, node.ToJsonString(IndentedOptions));
        return (true, $"已保存（{DateTime.Now:HH:mm:ss}），原文件备份为 {BackupPath}");
    }
}
