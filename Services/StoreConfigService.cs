using Newtonsoft.Json;
using fyserver.Models;

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
                _storeConfig = JsonConvert.DeserializeObject<StoreConfig>(json)
                               ?? CreateDefaultConfig();
            }
            else
            {
                _storeConfig = CreateDefaultConfig();
            }

            return _storeConfig;
        }
    }

    public void Reload() => _storeConfig = null;

    private static StoreConfig CreateDefaultConfig() => new()
    {
        Currency = "USD",
        Groups = new List<StoreGroup>(),
        AlwaysFeatured = new AlwaysFeaturedGroup(1, -1, "2018-01-01T00:00:00Z", "2099-01-01T00:00:00Z", new List<StoreOffer>())
    };
}
