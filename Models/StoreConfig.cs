using Newtonsoft.Json;

namespace fyserver.Models;

/// <summary>商店配置（config/store.json）。</summary>
public class StoreConfig
{
    [JsonProperty("currency")]
    public string Currency { get; set; } = "USD";

    [JsonProperty("groups")]
    public List<StoreGroup> Groups { get; set; } = new();

    [JsonProperty("alwaysFeatured")]
    public AlwaysFeaturedGroup AlwaysFeatured { get; set; } = new(1, -1, "2018-01-01T00:00:00Z", "2099-01-01T00:00:00Z", new List<StoreOffer>());
}
