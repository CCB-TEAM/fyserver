using System.Text.Json.Serialization;
using fyserver.Models;

namespace fyserver.Serialization;

/// <summary>
/// FASTER 存储用源生成上下文（无命名策略：保持既有 checkpoint 的 PascalCase 存储格式，
/// 向后兼容旧数据）。
/// </summary>
[JsonSerializable(typeof(User))]
[JsonSerializable(typeof(Deck))]
[JsonSerializable(typeof(EquippedItem))]
[JsonSerializable(typeof(Dictionary<int, Deck>))]
[JsonSerializable(typeof(List<EquippedItem>))]
public partial class StoreJsonContext : JsonSerializerContext;
