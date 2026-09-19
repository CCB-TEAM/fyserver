namespace fyserver.Models;

/// <summary>玩家账户数据。无参构造是 FASTER 反序列化所必需的。</summary>
public class User
{
    public User()
    {
        // 初始化集合，防止空引用
        Decks = new Dictionary<int, Deck>();
        EquippedItem = new List<EquippedItem>();
    }

    public User(string userName) : this()
    {
        UserName = userName;
        // 默认值
        Name = "<anon>";
        Locale = "zh-Hans";
        Tag = 0;
        Banned = false;
    }

    public User(int id, string userName) : this()
    {
        Id = id;
        UserName = userName;
        // 默认值
        Name = "<anon>";
        Locale = "zh-Hans";
        Tag = 0;
        Banned = false;
    }

    public int Id { get; set; }
    public string UserName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Locale { get; set; } = "";
    public int Tag { get; set; }
    public int Gold { get; set; }
    public int Diamonds { get; set; }
    public int Dust { get; set; } = 1000;
    public List<PlayerPack> Packs { get; set; } = new();
    public Dictionary<int, int> PurchasedOffers { get; set; } = new();

    // System.Text.Json 支持 Dictionary<int, T> 的序列化
    // 会自动将 int key 转为 string key ("1": {...})
    public Dictionary<int, Deck> Decks { get; set; } = new();

    public List<EquippedItem> EquippedItem { get; set; } = new();
    public bool Banned { get; set; }
    public string BanReason { get; set; } = "";
    public DateTime? BanExpiresAt { get; set; }
    public DateTime? BannedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    public bool IsBanActive(DateTime nowUtc) => Banned && (BanExpiresAt == null || BanExpiresAt > nowUtc);
    [System.Text.Json.Serialization.JsonIgnore]
    public string BanDescription => !string.IsNullOrWhiteSpace(BanReason)
        ? BanReason
        : BanExpiresAt is { } expires
            ? $"解封时间：{expires.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}"
            : "永久封禁";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
