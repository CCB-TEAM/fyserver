using System.Collections.Concurrent;
using System.Text.Json;
using fyserver.Models;
using fyserver.Serialization;

namespace fyserver.Services;

/// <summary>
/// 匹配队列与对局运行时状态的管理服务（替代原 config.appconfig 上的队列/对局状态与 http.cs 中的匹配逻辑）。
/// 单例注入：HTTP 端点与 WebSocket 服务共享同一实例。
/// </summary>
public class MatchManagerService
{
    private readonly UserStoreService _users;
    private readonly PlayerLibraryService _playerLibrary;
    private readonly CodecService _codec;
    private readonly ServerOptions _serverOptions;

    public MatchManagerService(UserStoreService users, PlayerLibraryService playerLibrary, CodecService codec, ServerOptions serverOptions)
    {
        _users = users;
        _playerLibrary = playerLibrary;
        _codec = codec;
        _serverOptions = serverOptions;
    }

    // ---- 匹配队列状态 ----
    public List<LobbyPlayer> WaitingPlayers1 { get; } = [];
    public List<LobbyPlayer> WaitingPlayers2 { get; } = [];
    public List<LobbyPlayer> WaitingPlayersClassic { get; } = [];
    public List<LobbyPlayer> WaitingPlayersUnranked { get; } = [];
    public List<LobbyPlayer> WaitingPlayersDraft { get; } = [];
    public List<LobbyPlayer> WaitingPlayersBrawl { get; } = [];
    public ConcurrentDictionary<string, List<LobbyPlayer>> BattleCodePlayers { get; } = new();
    public ConcurrentDictionary<int, MatchInfo> MatchedPairs { get; } = new();

    // 匹配队列为普通 List，用锁保护并发入队/出队
    private readonly object _queueLock = new();
    // 对局起始信息生成锁（避免并发重复生成/洗牌不一致）
    private readonly SemaphoreSlim _matchInitLock = new(1, 1);

    // ---- 队列/对局清理 ----

    public void RemovePlayerFromAllQueues(int playerId)
    {
        lock (_queueLock)
        {
            WaitingPlayers1.RemoveAll(p => p.PlayerId == playerId);
            WaitingPlayers2.RemoveAll(p => p.PlayerId == playerId);
            WaitingPlayersClassic.RemoveAll(p => p.PlayerId == playerId);
            WaitingPlayersUnranked.RemoveAll(p => p.PlayerId == playerId);
            WaitingPlayersDraft.RemoveAll(p => p.PlayerId == playerId);
            WaitingPlayersBrawl.RemoveAll(p => p.PlayerId == playerId);
            foreach (var code in BattleCodePlayers.Keys)
            {
                BattleCodePlayers[code].RemoveAll(p => p.PlayerId == playerId);
            }
        }
    }

    public void ClearMatchRuntimeState(MatchInfo match)
    {
        match.MatchActions.Clear();
        match.EndResolutionActionId = 0;
        match.MulliganLeft = null;
        match.MulliganRight = null;
        match.LeftDeck.Clear();
        match.RightDeck.Clear();
        match.LeftHand.Clear();
        match.RightHand.Clear();
        match.MatchStartingInfo = null;
    }

    public void RemovePlayerActiveMatches(int playerId, string reason)
    {
        lock (_queueLock)
        {
            foreach (var kvp in MatchedPairs.ToArray())
            {
                if (!kvp.Value.HasPlayer(playerId))
                    continue;

                ClearMatchRuntimeState(kvp.Value);
                MatchedPairs.TryRemove(kvp.Key, out _);
                Console.WriteLine($"移除玩家 {playerId} 的旧对局 {kvp.Key}，原因：{reason}");
            }
        }
    }

    public int GenerateMatchId()
    {
        for (var i = 0; i < 64; i++)
        {
            var id = Random.Shared.Next(100000, 999999);
            if (!MatchedPairs.ContainsKey(id))
                return id;
        }
        throw new InvalidOperationException("Unable to allocate unique match id.");
    }

    // ---- 匹配 ----

    /// <summary>加入匹配（battle_code / training / 空 / classic / unranked / draft / brawl）。</summary>
    /// <param name="useAiOpponent">training 匹配时是否用 AI 人机作为对手（/singleplayerlobby）。</param>
    public void JoinLobby(LobbyPlayer lobbyPlayer, bool useAiOpponent)
    {
        lock (_queueLock)
        {
            JoinLobbyCore(lobbyPlayer, useAiOpponent);
        }
    }

    private void JoinLobbyCore(LobbyPlayer lobbyPlayer, bool useAiOpponent)
    {
        string exData = "training";
        try
        {
            exData = lobbyPlayer.ExtraData.GetString() ?? "training";
        }
        catch
        {
            exData = "training";
        }

        Console.WriteLine(exData);

        // 对战码匹配
        if (exData.StartsWith("battle_code:", StringComparison.Ordinal))
        {
            var code = exData["battle_code:".Length..];
            if (!BattleCodePlayers.ContainsKey(code))
                BattleCodePlayers[code] = new List<LobbyPlayer>();

            var players = BattleCodePlayers[code];
            players.Add(lobbyPlayer);

            if (players.Count >= 2)
            {
                var matchId = GenerateMatchId();
                var matchInfo = new MatchInfo(matchId, players[0], players[1], "battle_code:" + code);
                players.RemoveAt(0);
                players.RemoveAt(0);

                MatchedPairs[matchId] = matchInfo;
            }
        }
        // 普通匹配
        else if (exData == "training")
        {
            Console.WriteLine("检测到匹配：" + lobbyPlayer);
            WaitingPlayers1.Add(lobbyPlayer);
            Console.WriteLine($"当前有{WaitingPlayers1.Count}");
            // 单人对战
            if (WaitingPlayers1.Count >= 1)
            {
                var matchId = GenerateMatchId();
                var right = useAiOpponent
                    ? new LobbyPlayer(-9178, -1, new JsonElement())
                    : WaitingPlayers1[0];
                var matchInfo = new MatchInfo(matchId, WaitingPlayers1[0], right, "pw");
                MatchedPairs[matchId] = matchInfo;
                WaitingPlayers1.RemoveAt(0);
                Console.WriteLine("匹配成功，信息为" + matchInfo);
            }
        }
        else if (exData == "")
        {
            WaitingPlayers2.Add(lobbyPlayer);
            if (WaitingPlayers2.Count >= 2)
            {
                var matchId = GenerateMatchId();
                var matchInfo = new MatchInfo(matchId, WaitingPlayers2[0], WaitingPlayers2[1], "");
                WaitingPlayers2.RemoveAt(0);
                WaitingPlayers2.RemoveAt(0);
                MatchedPairs[matchId] = matchInfo;
                Console.WriteLine("匹配成功，信息为" + matchInfo);
            }
        }
        else if (exData == "classic")
        {
            WaitingPlayersClassic.Add(lobbyPlayer);
            if (WaitingPlayersClassic.Count >= 2)
            {
                var matchId = GenerateMatchId();
                var matchInfo = new MatchInfo(matchId, WaitingPlayersClassic[0], WaitingPlayersClassic[1], "classic");
                WaitingPlayersClassic.RemoveAt(0);
                WaitingPlayersClassic.RemoveAt(0);
                MatchedPairs[matchId] = matchInfo;
                Console.WriteLine("匹配成功，信息为" + matchInfo);
            }
        }
        else if (exData == "unranked")
        {
            WaitingPlayersUnranked.Add(lobbyPlayer);
            if (WaitingPlayersUnranked.Count >= 2)
            {
                var matchId = GenerateMatchId();
                var matchInfo = new MatchInfo(matchId, WaitingPlayersUnranked[0], WaitingPlayersUnranked[1], "unranked");
                WaitingPlayersUnranked.RemoveAt(0);
                WaitingPlayersUnranked.RemoveAt(0);
                MatchedPairs[matchId] = matchInfo;
                Console.WriteLine("匹配成功，信息为" + matchInfo);
            }
        }
        else if (exData == "draft")
        {
            WaitingPlayersDraft.Add(lobbyPlayer);
            if (WaitingPlayersDraft.Count >= 2)
            {
                var matchId = GenerateMatchId();
                var matchInfo = new MatchInfo(matchId, WaitingPlayersDraft[0], WaitingPlayersDraft[1], "draft");
                WaitingPlayersDraft.RemoveAt(0);
                WaitingPlayersDraft.RemoveAt(0);
                MatchedPairs[matchId] = matchInfo;
                Console.WriteLine("匹配成功，信息为" + matchInfo);
            }
        }
        else if (exData == "brawl")
        {
            WaitingPlayersBrawl.Add(lobbyPlayer);
            if (WaitingPlayersBrawl.Count >= 2)
            {
                var matchId = GenerateMatchId();
                var matchInfo = new MatchInfo(matchId, WaitingPlayersBrawl[0], WaitingPlayersBrawl[1], "brawl");
                WaitingPlayersBrawl.RemoveAt(0);
                WaitingPlayersBrawl.RemoveAt(0);
                MatchedPairs[matchId] = matchInfo;
                Console.WriteLine("匹配成功，信息为" + matchInfo);
            }
        }
    }

    /// <summary>查找玩家当前未结束的对局；顺带清理已满足移除条件的残留对局。</summary>
    public MatchInfo? GetActiveMatchForUser(int userId)
    {
        foreach (var kvp in MatchedPairs.ToArray())
        {
            if (!kvp.Value.HasPlayer(userId))
                continue;

            // 清理历史遗留的已结束对局，避免下次匹配拿到旧数据
            if (CanRemoveMatch(kvp.Value))
            {
                if (MatchedPairs.TryRemove(kvp.Key, out var removed))
                {
                    ClearMatchRuntimeState(removed);
                }
                continue;
            }

            if (!string.IsNullOrEmpty(kvp.Value.WinnerSide) || HasPlayerEndedMatch(kvp.Value, userId))
                continue;

            return kvp.Value;
        }

        return null;
    }

    public MatchInfo? GetMatch(int matchId)
    {
        return MatchedPairs.TryGetValue(matchId, out var match) ? match : null;
    }

    // ---- 对局辅助 ----

    public static bool IsSoloMatch(MatchInfo match)
    {
        return string.Equals(match.Ex, "pw", StringComparison.OrdinalIgnoreCase) ||
               (match.Left?.PlayerId != 0 && match.Left?.PlayerId == match.Right?.PlayerId);
    }

    public static bool HasPlayerEndedMatch(MatchInfo match, int playerId)
    {
        if (playerId == match.Left?.PlayerId && match.PlayerStatusLeft == GameConstants.EndMatch)
            return true;

        if (playerId == match.Right?.PlayerId && match.PlayerStatusRight == GameConstants.EndMatch)
            return true;

        return false;
    }

    public static bool CanRemoveMatch(MatchInfo match)
    {
        if (match.PlayerStatusLeft == GameConstants.EndMatch && match.PlayerStatusRight == GameConstants.EndMatch)
            return true;

        if (IsSoloMatch(match) &&
            (match.PlayerStatusLeft == GameConstants.EndMatch || match.PlayerStatusRight == GameConstants.EndMatch))
            return true;

        return false;
    }

    public MatchAction DecryptMatchAction(MatchActionEn matchActionen)
    {
        var matchAction = new MatchAction();
        Console.ForegroundColor = ConsoleColor.Green;
        try
        {
            int actionId = 0;
            string plaintext = _codec.Decode(matchActionen.A, out actionId);
            matchAction = JsonSerializer.Deserialize(plaintext, FyJsonContext.Default.MatchAction) ?? new MatchAction();
            Console.WriteLine("action：" + matchAction);
            matchAction = matchAction with { SendActionId = actionId };
            Console.WriteLine("ActionId：" + matchAction.ActionId);
            Console.WriteLine("解密结果：" + plaintext);
        }
        catch (Exception ex)
        {
            Console.WriteLine("解密失败，使用原始数据");
            Console.WriteLine("原始数据：" + matchActionen.A);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(ex?.Message);
            Console.WriteLine(ex?.InnerException?.Message);
        }
        Console.ForegroundColor = ConsoleColor.White;
        return matchAction;
    }

    public bool TryParseMatchActionPayload(JsonElement payload, out MatchAction matchAction)
    {
        matchAction = new MatchAction();
        try
        {
            if (payload.ValueKind != JsonValueKind.Object)
                return false;

            var hasEncryptedField = false;
            if (payload.TryGetProperty("a", out var encryptedProp) &&
                encryptedProp.ValueKind == JsonValueKind.String)
            {
                hasEncryptedField = true;
                var encrypted = encryptedProp.GetString();
                if (!string.IsNullOrWhiteSpace(encrypted))
                {
                    int actionId = 0;
                    var plaintext = _codec.Decode(encrypted, out actionId);
                    var decoded = JsonSerializer.Deserialize(plaintext, FyJsonContext.Default.MatchAction);
                    if (decoded != null)
                    {
                        matchAction = decoded with { SendActionId = actionId };
                        return true;
                    }
                }
            }

            if (hasEncryptedField)
                return false;

            var direct = JsonSerializer.Deserialize(payload.GetRawText(), FyJsonContext.Default.MatchAction);
            if (direct != null)
            {
                matchAction = direct;
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"解析 match action 失败: {ex.Message}");
        }

        return false;
    }

    public static string? TryGetWinnerSide(MatchAction matchAction, JsonElement? payload = null)
    {
        if (matchAction.Value != null &&
            matchAction.Value.TryGetValue("winner_side", out var winnerFromValue))
        {
            var side = winnerFromValue?.ToString();
            if (side == "left" || side == "right")
                return side;
        }

        if (matchAction.ActionData != null &&
            matchAction.ActionData.TryGetValue("winner_side", out var winnerFromActionData))
        {
            var side = winnerFromActionData?.ToString();
            if (side == "left" || side == "right")
                return side;
        }

        if (payload.HasValue &&
            payload.Value.ValueKind == JsonValueKind.Object &&
            payload.Value.TryGetProperty("winner_side", out var winnerSideProp) &&
            winnerSideProp.ValueKind == JsonValueKind.String)
        {
            var side = winnerSideProp.GetString();
            if (side == "left" || side == "right")
                return side;
        }

        return null;
    }

    public static bool IsEndMatchSignal(MatchAction matchAction)
    {
        return matchAction.Action == "end-match" || matchAction.ActionType == "XActionEndMatch";
    }

    public void TryApplyWinnerSide(MatchInfo match, MatchAction matchAction, int matchId, string source, JsonElement? payload = null)
    {
        if (!string.IsNullOrEmpty(match.WinnerSide))
            return;

        if (!IsEndMatchSignal(matchAction))
            return;

        var winnerSide = TryGetWinnerSide(matchAction, payload);
        string? reason;
        try
        {
            reason = matchAction.Value?["result"]?.ToString();
        }
        catch
        {
            reason = null;
        }

        if (winnerSide == "left" || winnerSide == "right")
        {
            match.WinnerSide = winnerSide;
            match.MatchActions.Add(new MatchAction
            {
                ActionId = match.currentActionId,
                ActionType = "ActionEndMatch",
                PlayerId = (reason == "Victory_DestroyHQ" ? (winnerSide == "left" ? match.Left?.PlayerId : match.Right?.PlayerId) : (winnerSide == "left" ? match.Right?.PlayerId : match.Left?.PlayerId)),
                ActionData = new()
                {
                    { "reason", reason ?? "" },
                    { "winner_side", winnerSide },
                },
                turn_number = match.Turns,
                SendActionId = matchAction.SendActionId,
            });
            match.currentActionId++;
            Console.WriteLine($"对局 {matchId} 设置 winner_side={winnerSide}（来自 {source}）");
        }
    }

    /// <summary>生成对局起始信息（手牌/牌库/双方信息），结果缓存到 match.MatchStartingInfo。</summary>
    public async Task<MatchStartingInfo> MakeMatchStartingInfo(int myId, MatchInfo match)
    {
        if (match.MatchStartingInfo != null)
            return match.MatchStartingInfo;

        // 双重检查 + 信号量：避免并发请求重复生成（洗牌不一致）
        await _matchInitLock.WaitAsync();
        try
        {
            if (match.MatchStartingInfo != null)
                return match.MatchStartingInfo;

            var other = match.Left?.PlayerId == myId ? match.Right : match.Left;
            Console.WriteLine("正在生成MatchStartingInfo，玩家ID：" + myId);
            Console.WriteLine("MatchStartingInfo不存在，正在生成...");

            var leftUser = await _users.GetByIdAsync(match.Left!.PlayerId);
            User? rightUser = await _users.GetByIdAsync(match.Right!.PlayerId);
            if (match.Right.PlayerId == -9178)
                rightUser = new User(-9178, "彗星服人机");
            Console.WriteLine("双方为" + leftUser!.Id + "," + rightUser!.Id);

            // 获取卡组信息
            var leftDeck = leftUser.Decks[match.Left.DeckId];
            Deck rightDeck;
            if (match.Right.DeckId == -1)
                rightDeck = new Deck(new CreateDeck("FK", "Germany", "Finland", "%%21|4v32323232sTgv0z0C0C0C0CoBoBoBoB0Y0Y101010hShShShS1902020202030303ououpRpRpRsU;;;~;;;|0N1b"), -9178);
            else
                rightDeck = rightUser.Decks[match.Right.DeckId];

            // 生成卡牌列表（简化版本，实际应该解析 deck_code）
            var (leftCards, leftLocation) = GetCardsFromDeck(leftDeck, 1, true);
            var (rightCards, rightLocation) = GetCardsFromDeck(rightDeck, 41, false);

            // 手区从 0 开始；牌库从手牌数量开始，避免同侧 location_number 冲突
            const int leftStartingHandCount = 4;
            const int rightStartingHandCount = 5;
            match.LeftHand = leftCards.Take(leftStartingHandCount).Select((card, index) =>
                card with { Location = "hand_left", LocationNumber = index }).ToList();
            match.RightHand = rightCards.Take(rightStartingHandCount).Select((card, index) =>
                card with { Location = "hand_right", LocationNumber = index }).ToList();

            match.LeftDeck = leftCards.Skip(leftStartingHandCount).Select((card, index) =>
                card with { Location = "deck_left", LocationNumber = leftStartingHandCount + index }).ToList();
            match.RightDeck = rightCards.Skip(rightStartingHandCount).Select((card, index) =>
                card with { Location = "deck_right", LocationNumber = rightStartingHandCount + index }).ToList();

            string matchType = "battle";
            if (match.Ex.StartsWith("battle_code", StringComparison.Ordinal)) matchType = "code";
            else if (match.Ex == "brawl") matchType = "brawl";
            else if (match.Ex == "draft") matchType = "draft";
            else if (match.Ex == "training" || match.Ex == "pw") matchType = "training";

            // 构建MatchStartingInfo
            match.MatchStartingInfo = new MatchStartingInfo(
                LocalSubactions: true,
                MatchAndStartingData: new MatchAndStartingData(
                    Match: new MatchData(
                        ActionPlayerId: other?.PlayerId,
                        ActionSide: other?.PlayerId == match.Left?.PlayerId ? "left" : "right",
                        Actions: new List<MatchAction>(),
                        ActionsUrl: $"{_serverOptions.GetAddressHttpR()}/matches/v2/{match.MatchId}/actions",
                        CurrentActionId: 0,
                        CurrentTurn: 1,
                        DeckIdLeft: match.Left!.DeckId,
                        DeckIdRight: match.Right!.DeckId,
                        LeftIsOnline: 1,
                        MatchId: match.MatchId,
                        MatchType: matchType,
                        MatchUrl: $"{_serverOptions.GetAddressHttpR()}/matches/v2/{match.MatchId}",
                        ModifyDate: DateTime.UtcNow.ToString("o"),
                        Notifications: new List<object>(),
                        PlayerIdLeft: leftUser.Id,
                        PlayerIdRight: rightUser.Id,
                        PlayerStatusLeft: "not_done",
                        PlayerStatusRight: (matchType == "training") ? GameConstants.MulliganDone : "not_done",
                        RightIsOnline: 1,
                        StartSide: "left",
                        Status: "pending",
                        WinnerId: 0,
                        WinnerSide: ""
                    ),
                    StartingData: new StartingData(
                        AllyFactionLeft: leftDeck.AllyFaction,
                        AllyFactionRight: rightDeck.AllyFaction,
                        CardBackLeft: leftDeck.CardBack,
                        CardBackRight: rightDeck.CardBack,
                        StartingHandLeft: match.LeftHand,
                        StartingHandRight: match.RightHand,
                        DeckLeft: match.LeftDeck,
                        DeckRight: match.RightDeck,
                        EquipmentLeft: leftUser.EquippedItem?.Select(i => i.ItemId).ToList() ?? new List<string>(),
                        EquipmentRight: rightUser.EquippedItem?.Select(i => i.ItemId).ToList() ?? new List<string>(),
                        IsAiMatch: false,
                        LeftPlayerName: leftUser.Name,
                        LeftPlayerOfficer: false,
                        LeftPlayerTag: leftUser.Tag.ToString(),
                        LocationCardLeft: leftLocation,
                        LocationCardRight: rightLocation,
                        PlayerIdLeft: leftUser.Id,
                        PlayerIdRight: rightUser.Id,
                        PlayerStarsLeft: 120,
                        PlayerStarsRight: 120,
                        RightPlayerName: rightUser.Name,
                        RightPlayerOfficer: false,
                        RightPlayerTag: rightUser.Tag.ToString()
                    )
                )
            );

            return match.MatchStartingInfo;
        }
        finally
        {
            _matchInitLock.Release();
        }
    }

    /// <summary>从卡组编码生成卡牌列表（简化版本）。</summary>
    public (List<MatchCard> cards, MatchLocation? location) GetCardsFromDeck(Deck deck, int startId, bool isLeft)
    {
        Console.WriteLine(deck.DeckCode);
        var cards = new List<MatchCard>();
        var deckCode = deck.DeckCode ?? string.Empty;
        if (deckCode.Length < 5)
        {
            return (cards, new MatchLocation(
                CardId: startId,
                IsGold: false,
                Location: isLeft ? "board_hqleft" : "board_hqright",
                LocationNumber: 0,
                Name: "invalid_deck_code",
                Faction: deck.MainFaction
            ));
        }

        MatchLocation? locationCard = null;
        if (deckCode.Length >= 4 &&
            _playerLibrary.DeckCodeTable.TryGetValue(deckCode[^4..^2], out var hqCard))
        {
            locationCard = new MatchLocation(
                CardId: startId,
                IsGold: false,
                Location: isLeft ? "board_hqleft" : "board_hqright",
                LocationNumber: 0,
                Faction: deck.MainFaction,
                Name: hqCard.Card
            );
        }

        // 预留起始 ID 给 HQ 卡，避免和牌堆/手牌 card_id 冲突
        startId++;
        var parsedDeckCode = deckCode.Remove(0, 5);
        parsedDeckCode.Split(';').Take(4).Select((item, index) => new { Item = item, RepeatCount = index }).ToList().ForEach(x =>
        {
            foreach (var chunk in x.Item.Chunk(2))
            {
                if (chunk.Length != 2) return;
                var key = string.Concat(chunk);
                if (!_playerLibrary.DeckCodeTable.TryGetValue(key, out var lkp)) return;
                Console.Write(lkp.DeckCodeId);
                for (int i = 0; i <= x.RepeatCount; i++)
                {
                    cards.Add(new MatchCard(
                        CardId: startId++,
                        IsGold: false,
                        Location: isLeft ? "deck_left" : "deck_right",
                        LocationNumber: 0,
                        Name: lkp.Card
                    ));
                }
            }
        });
        Console.WriteLine(cards.Count);
        Console.WriteLine(JsonSerializer.Serialize(cards, FyJsonContext.Default.ListMatchCard));
        cards = [.. cards.OrderBy(_ => Random.Shared.Next()).Select((a, l) => a with { LocationNumber = l })];
        return (cards, locationCard);
    }
}
