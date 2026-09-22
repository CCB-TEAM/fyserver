using System.Text.Json;
using fyserver.Models;
using fyserver.Serialization;

namespace fyserver.Services;

/// <summary>商店配置的读取与热重载（替代原 GlobalState 的静态缓存）。</summary>
public class StoreConfigService
{
    private readonly object _lock = new();
    private StoreConfig? _storeConfig;
    private const string ConfigPath = "./config/store.json";

    public StoreConfig GetStoreConfig()
    {
        lock (_lock)
        {
            if (_storeConfig != null)
                return _storeConfig;

            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                _storeConfig = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.StoreConfig)
                               ?? CreateDefaultConfig();
            }
            else
            {
                _storeConfig = CreateDefaultConfig();
            }

            return _storeConfig;
        }
    }

    public void Reload()
    {
        lock (_lock) _storeConfig = null;
    }

    /// <summary>原子写入商店配置，并在替换前保留一份 .bak。</summary>
    public (bool Ok, string Message) Save(StoreConfig config)
    {
        if (config == null) return (false, "商店配置不能为空");
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var temp = ConfigPath + ".tmp";
            try
            {
                var json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.StoreConfig);
                File.WriteAllText(temp, json);
                if (File.Exists(ConfigPath)) File.Copy(ConfigPath, ConfigPath + ".bak", true);
                File.Move(temp, ConfigPath, true);
                _storeConfig = config;
                return (true, "商店配置已保存并生效");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return (false, "保存商店配置失败：" + ex.Message);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    private static StoreConfig CreateDefaultConfig() => new()
    {
        Currency = "USD",
        Groups = new List<StoreGroup>(),
        AlwaysFeatured = new AlwaysFeaturedGroup(1, -1, "2018-01-01T00:00:00Z", "2099-01-01T00:00:00Z", new List<StoreOffer>())
    };
}
