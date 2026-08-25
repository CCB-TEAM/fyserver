using System.Text.Json.Serialization;
using fyserver.Models;
using fyserver.Services;

namespace fyserver.Serialization;

/// <summary>
/// 配置文件读写用源生成上下文（无命名策略：属性名保持原样，兼容既有
/// setting.json / store.json / library 表的字段命名）。
/// </summary>
[JsonSerializable(typeof(ServerOptions))]
[JsonSerializable(typeof(StoreConfig))]
[JsonSerializable(typeof(DeckCodeCard))]
[JsonSerializable(typeof(List<DeckCodeCard>))]
[JsonSerializable(typeof(List<string>))]
public partial class ConfigJsonContext : JsonSerializerContext;
