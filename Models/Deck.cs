using System.Text.Json.Serialization;

namespace fyserver.Models;

public class Deck
{
    public Deck()
    {
    }

    public Deck(CreateDeck createDeck, int playerId)
    {
        Name = createDeck.Name;
        MainFaction = createDeck.MainFaction;
        AllyFaction = createDeck.AllyFaction;
        CardBack = $"cardback_starter_{createDeck.MainFaction.ToLower()}";
        DeckCode = createDeck.DeckCode;
        Favorite = false;
        Id = Random.Shared.Next(100000, 999999);
        PlayerId = playerId;
        LastPlayed = DateTime.Now;
        CreateDate = DateTime.Now;
        ModifyDate = DateTime.Now;
    }

    public string Name { get; set; } = "";
    public string MainFaction { get; set; } = "";
    public string AllyFaction { get; set; } = "";
    public string CardBack { get; set; } = "";
    public string DeckCode { get; set; } = "";
    public bool Favorite { get; set; }
    public int Id { get; set; }
    public int PlayerId { get; set; }

    [JsonIgnore]
    public DateTime LastPlayed { get; set; }

    public string LastPlayedString
    {
        get => LastPlayed.ToString("o");
        set => LastPlayed = DateTime.Parse(value);
    }

    [JsonIgnore]
    public DateTime CreateDate { get; set; }

    public string CreateDateString
    {
        get => CreateDate.ToString("o");
        set => CreateDate = DateTime.Parse(value);
    }

    [JsonIgnore]
    public DateTime ModifyDate { get; set; }

    public string ModifyDateString
    {
        get => ModifyDate.ToString("o");
        set => ModifyDate = DateTime.Parse(value);
    }
}

public class EquippedItem
{
    public EquippedItem(string faction, string itemId, string slot)
    {
        Faction = faction;
        ItemId = itemId;
        Slot = slot;
    }

    public string Faction { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string Slot { get; set; } = "";
}
