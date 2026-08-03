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

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false
        };
        // 先捕获默认（反射）resolver，再按 [源生成上下文, 反射兜底] 顺序装配：
        // source-gen 优先命中具名类型，匿名对象等回退到反射
        var reflectionResolver = options.TypeInfoResolver;
        options.TypeInfoResolver = null; // 阻止 Chain.Add 的隐式前移语义
        options.TypeInfoResolverChain.Clear();
        options.TypeInfoResolverChain.Add(fyserver.Serialization.FyJsonContext.Default);
        if (reflectionResolver != null)
            options.TypeInfoResolverChain.Add(reflectionResolver);
        return options;
    }
}
