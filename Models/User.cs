namespace fyserver.Models;

/// <summary>玩家账户数据。无参构造是 FASTER 反序列化所必需的。</summary>
public class User
{
    public User()
    {
        // 初始化集合，防止空引用
        Decks = new Dictionary<int, Deck>();
        EquippedItem = new List<EquippedItem>();
        Items = new List<Item>();
        UserCards = new UserCardCollection();
        Medkits = new List<Medkit>();
        Tokens = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        CardCollection = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Inventory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    public User(string userName) : this()
    {
        UserName = userName;
        // 默认值
        Name = "<anon>";
        Locale = "zh-Hans";
        Tag = 0;
        Banned = false;
        UserCards.Cards.AddRange(DefaultCards());
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
    /// <summary>客户端用户数据：拥有的卡牌，兼容 NestJS 的 user_cards 结构。</summary>
    public UserCardCollection UserCards { get; set; } = new();
    /// <summary>客户端用户物品，兼容 NestJS 的 items 数组。</summary>
    public List<Item> Items { get; set; } = new();
    public int DraftTickets { get; set; }
    public List<Medkit> Medkits { get; set; } = new();
    public Dictionary<string, int> Tokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string LinkerAccount { get; set; } = "";
    public int Wins { get; set; }
    public int Stars { get; set; }
    /// <summary>商店直接发放的卡牌数量；键为卡牌名称或客户端 card id。</summary>
    public Dictionary<string, int> CardCollection { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>装备、代币、选秀入场券等非货币奖励的通用库存。</summary>
    public Dictionary<string, int> Inventory { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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

    private static List<UserCard> DefaultCards() =>
    [
        new("card_location_stalingrad", 1, 0, 0, 0),
        new("card_location_changchun", 1, 0, 0, 0),
        new("card_location_cherbourg", 1, 0, 0, 0),
        new("card_location_london", 1, 0, 0, 0),
        new("card_location_berlin", 1, 0, 0, 0)
    ];
}

public sealed class UserCardCollection
{
    public List<UserCard> Cards { get; set; } = new();
    public List<System.Text.Json.JsonElement> NewCards { get; set; } = new();
}

public sealed class UserCard
{
    public UserCard() { }
    public UserCard(string cardType, int count, int goldCardCount, int id, int recentlyCraftedCount)
    {
        CardType = cardType; Count = count; GoldCardCount = goldCardCount;
        Id = id; RecentlyCraftedCount = recentlyCraftedCount;
    }
    public string CardType { get; set; } = "";
    public int Count { get; set; }
    public int GoldCardCount { get; set; }
    public int Id { get; set; }
    public int RecentlyCraftedCount { get; set; }
}

public sealed class Medkit
{
    public int Duration { get; set; }
}
