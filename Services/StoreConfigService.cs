using System.Text.Json;
using fyserver.Models;
using fyserver.Serialization;

namespace fyserver.Services;

/// <summary>商店配置的读取与热重载（替代原 GlobalState 的静态缓存）。</summary>
public class StoreConfigService
{
    private const string StoreKey = "store:config";
    private readonly object _lock = new();
    private readonly AppDataStoreService _appData;
    private StoreConfig? _storeConfig;

    public StoreConfigService(AppDataStoreService appData) => _appData = appData;

    public StoreConfig GetStoreConfig()
    {
        lock (_lock)
        {
            if (_storeConfig != null)
                return _storeConfig;

            var json = _appData.Get(StoreKey);
            if (!string.IsNullOrWhiteSpace(json))
            {
                _storeConfig = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.StoreConfig)
                               ?? CreateDefaultConfig();
            }
            else
            {
                _storeConfig = CreateDefaultConfig();
                _appData.Set(StoreKey, JsonSerializer.Serialize(_storeConfig, ConfigJsonContext.Default.StoreConfig));
            }

            return _storeConfig;
        }
    }

    public void Reload()
    {
        lock (_lock) _storeConfig = null;
    }

    /// <summary>持久化商店配置并立即刷新运行时缓存。</summary>
    public (bool Ok, string Message) Save(StoreConfig config)
    {
        if (config == null) return (false, "商店配置不能为空");
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.StoreConfig);
                _appData.Set(StoreKey, json);
                _storeConfig = config;
                return (true, "商店配置已保存并生效");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.Data.Common.DbException)
            {
                return (false, "保存商店配置失败：" + ex.Message);
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
