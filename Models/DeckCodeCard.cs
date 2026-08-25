namespace fyserver.Models;

/// <summary>卡牌库表项（library/deckCodeIDsTable2.json 中的一行，字段名保持原样）。</summary>
public class DeckCodeCard
{
    public string card { get; set; } = "";
    public string deck_code_id { get; set; } = "";
    public int ID { get; set; }
}
