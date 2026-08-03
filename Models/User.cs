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
        Name = "XDLG";
        Locale = "zh-Hans";
        Tag = Random.Shared.Next(1000, 9999);
        Banned = false;
    }

    public User(int id, string userName) : this()
    {
        Id = id;
        UserName = userName;
        // 默认值
        Name = userName;
        Locale = "zh-Hans";
        Tag = Random.Shared.Next(1000, 9999);
        Banned = false;
    }

    public int Id { get; set; }
    public string UserName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Locale { get; set; } = "";
    public int Tag { get; set; }

    // System.Text.Json 支持 Dictionary<int, T> 的序列化
    // 会自动将 int key 转为 string key ("1": {...})
    public Dictionary<int, Deck> Decks { get; set; } = new();

    public List<EquippedItem> EquippedItem { get; set; } = new();
    public bool Banned { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
