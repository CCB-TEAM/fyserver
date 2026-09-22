using System.Text.Json.Nodes;

namespace fyserver.Models;

/// <summary>兑换码奖励；Data 保留客户端需要的原始 itemType 字段。</summary>
public sealed class RedeemReward
{
    public JsonObject Data { get; set; } = new();
    public int Qty { get; set; } = 1;
}

/// <summary>兑换码持久化记录，对齐 NestJS redeem.ts 的结构。</summary>
public sealed class RedeemCode
{
    public string Code { get; set; } = "";
    public string Type { get; set; } = "single";
    public List<RedeemReward> Rewards { get; set; } = new();
    public string? UsedBy { get; set; }
    public List<string>? UsedByUsers { get; set; }
    public string? UsedAt { get; set; }
    public string? ExpiresAt { get; set; }
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("O");
}
