using Newtonsoft.Json;
using fyserver.Models;

namespace fyserver.Services;

/// <summary>卡牌库、物品与卡组编码表（替代原 PlayerLibrary 静态类）。启动时初始化一次。</summary>
public class PlayerLibraryService
{
    private class Card
    {
        public string card { get; set; } = "";
        public string deck_code_id { get; set; } = "";
        public int ID { get; set; }
    }

    public LibraryResponse Library { get; } = new(
        new List<LibraryItem>(),
        new List<object>()
    );

    public List<Item> Items { get; } = new();

    public Dictionary<string, CardLookup> DeckCodeTable { get; } = new();

    public void InitLibrary(string deckCodePath, string emojiPath, string cardbackPath)
    {
        List<Card> cs = JsonConvert.DeserializeObject<List<Card>>(File.ReadAllText(deckCodePath)) ?? new();
        List<string> emojis = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(emojiPath)) ?? new();
        List<string> cbs = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(cardbackPath)) ?? new();

        foreach (var c in cs)
        {
            Library.Cards.Add(new LibraryItem(c.card, 40, 0, c.ID, 0));
            if (!DeckCodeTable.ContainsKey(c.deck_code_id))
                DeckCodeTable.Add(c.deck_code_id, new CardLookup(c.card, c.deck_code_id, c.ID));
        }

        foreach (var e in emojis)
            Items.Add(new Item("{}", e, 0));

        foreach (var c in cbs)
            Items.Add(new Item("{}", c, 0));
    }
}
