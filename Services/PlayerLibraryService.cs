using System.Text.Json;
using fyserver.Models;
using fyserver.Serialization;

namespace fyserver.Services;

/// <summary>卡牌库、物品与卡组编码表（替代原 PlayerLibrary 静态类）。启动时初始化一次。</summary>
public class PlayerLibraryService
{
    public LibraryResponse Library { get; } = new(
        new List<LibraryItem>(),
        new List<object>()
    );

    public List<Item> Items { get; } = new();

    public Dictionary<string, CardLookup> DeckCodeTable { get; } = new();

    public void InitLibrary(string deckCodePath, string emojiPath, string cardbackPath)
    {
        List<DeckCodeCard> cs = JsonSerializer.Deserialize(File.ReadAllText(deckCodePath), ConfigJsonContext.Default.ListDeckCodeCard) ?? new();
        List<string> emojis = JsonSerializer.Deserialize(File.ReadAllText(emojiPath), ConfigJsonContext.Default.ListString) ?? new();
        List<string> cbs = JsonSerializer.Deserialize(File.ReadAllText(cardbackPath), ConfigJsonContext.Default.ListString) ?? new();

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
