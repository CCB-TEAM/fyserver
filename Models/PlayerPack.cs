namespace fyserver.Models;

/// <summary>尚未打开的卡包，与客户端 /players/{id}/packs 响应保持一致。</summary>
public sealed class PlayerPack
{
    public int Id { get; set; }
    public string CardSet { get; set; } = "";
    public DateTime CreateDate { get; set; }
    public DateTime? DateOpened { get; set; }
    public string? Details { get; set; }
    public DateTime ModifyDate { get; set; }
    public int PlayerId { get; set; }
}
