using System.Text.Json;
using System.Text.Json.Serialization;

namespace fyserver.Models;

/// <summary>游戏常量与全局序列化选项。</summary>
public static class GameConstants
{
    // 位置常量
    public const string DeckLeft = "deck_left";
    public const string DeckRight = "deck_right";
    public const string BoardHqLeft = "board_hqleft";
    public const string BoardHqRight = "board_hqright";
    public const string HandLeft = "hand_left";
    public const string HandRight = "hand_right";

    // 玩家状态常量
    public const string NotDone = "not_done";
    public const string MulliganDone = "mulligan_done";
    public const string EndMatch = "end_match";

    // 比赛状态常量
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Finished = "finished";
    public static readonly List<string> MainFactions = new() { "Germany", "Britain", "Soviet", "USA", "Japan" };
    public static readonly List<string> AllyFactions = new() { "Germany", "Britain", "Soviet", "USA", "Japan", "France", "Italy", "Poland", "Finland" };

    // 动作类型
    public const string XActionCheat = "XActionCheat";

    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <summary>配置文件（setting.json / store.json / library 表）读写选项：大小写不敏感匹配、紧凑输出。</summary>
    public static readonly JsonSerializerOptions ConfigJsonOptions = CreateConfigJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false
        };
        // 纯源生成：NativeAOT 兼容（所有 JSON 类型必须注册到 FyJsonContext）
        options.TypeInfoResolverChain.Clear();
        options.TypeInfoResolverChain.Add(fyserver.Serialization.FyJsonContext.Default);
        return options;
    }

    private static JsonSerializerOptions CreateConfigJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
        // 纯源生成：配置文件类型注册到 ConfigJsonContext（无命名策略，保持原字段名）
        options.TypeInfoResolverChain.Clear();
        options.TypeInfoResolverChain.Add(fyserver.Serialization.ConfigJsonContext.Default);
        return options;
    }
}
