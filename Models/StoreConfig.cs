using System.Text.Json.Serialization;

namespace fyserver.Models;

/// <summary>商店配置（config/store.json）。</summary>
public class StoreConfig
{
    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "USD";

    [JsonPropertyName("groups")]
    public List<StoreGroup> Groups { get; set; } = new();

    [JsonPropertyName("alwaysFeatured")]
    public AlwaysFeaturedGroup AlwaysFeatured { get; set; } = new(1, -1, "2018-01-01T00:00:00Z", "2099-01-01T00:00:00Z", new List<StoreOffer>());
}
