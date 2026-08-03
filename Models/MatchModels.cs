using System.Text.Json;

namespace fyserver.Models;

public record DeckAction(
    string Action,
    string Value,
    string DeckCode
);

public record LobbyPlayer(
    int PlayerId,
    int DeckId,
    JsonElement ExtraData
);

public record MatchAction(
    int ActionId = 0,
    string ActionType = "",
    int? PlayerId = null,
    Dictionary<string, object>? ActionData = null,
    object[]? sub_actions = null,
    int turn_number = 0,
    string Action = "",
    Dictionary<string, object>? Value = null,
    int SendActionId = 0
);

public record MatchPut(
    int MinActionId = 0,
    int OpponentId = 0,
    long TimeSinceOpponentPing = 0
);

public record MatchActionEn(
    string A
);

public record MulliganCards(
    List<int> DiscardedCardIds
);

public record MatchCard(
    int CardId,
    bool IsGold,
    string Location,
    int LocationNumber,
    string Name
);

public record MatchLocation(
    int CardId,
    bool IsGold,
    string Location,
    int LocationNumber,
    string Name,
    string Faction
);

public record MulliganResult(
    List<MatchCard> Deck,
    List<MatchCard> ReplacementCards
);

public record StartingData(
    string AllyFactionLeft,
    string AllyFactionRight,
    string CardBackLeft,
    string CardBackRight,
    List<MatchCard> StartingHandLeft,
    List<MatchCard> StartingHandRight,
    List<MatchCard> DeckLeft,
    List<MatchCard> DeckRight,
    List<string> EquipmentLeft,
    List<string> EquipmentRight,
    bool IsAiMatch,
    string LeftPlayerName,
    bool LeftPlayerOfficer,
    string LeftPlayerTag,
    MatchLocation? LocationCardLeft,
    MatchLocation? LocationCardRight,
    int PlayerIdLeft,
    int PlayerIdRight,
    int PlayerStarsLeft,
    int PlayerStarsRight,
    string RightPlayerName,
    bool RightPlayerOfficer,
    string RightPlayerTag
);

public record MatchData(
    int? ActionPlayerId,
    string ActionSide,
    List<MatchAction> Actions,
    string ActionsUrl,
    int CurrentActionId,
    int CurrentTurn,
    int DeckIdLeft,
    int DeckIdRight,
    int LeftIsOnline,
    int MatchId,
    string MatchType,
    string MatchUrl,
    string ModifyDate,
    List<object> Notifications,
    int PlayerIdLeft,
    int PlayerIdRight,
    string PlayerStatusLeft,
    string PlayerStatusRight,
    int RightIsOnline,
    string StartSide,
    string Status,
    int WinnerId,
    string WinnerSide
);

public record MatchAndStartingData(
    MatchData Match,
    StartingData StartingData
);

public class MatchReconnect
{
    public string[] Actions { get; set; } = Array.Empty<string>();
    public bool LocalSubactions { get; set; }
    public MatchData Match { get; set; } = null!;
    public MulliganResult? MulliganLeft { get; set; }
    public MulliganResult? MulliganRight { get; set; }
    public bool SameTurn { get; set; }
    public StartingData StartingData { get; set; } = null!;
    public int TimeSinceStartOfTurn { get; set; }
    public bool WaitingForSitNGoMatch { get; set; }
}

public record MatchStartingInfo(
    bool LocalSubactions,
    MatchAndStartingData MatchAndStartingData
);

// Player DTOs as records
public record CreateDeck(
    string Name,
    string MainFaction,
    string AllyFaction,
    string DeckCode
);

public record ChangeDeck(
    int Id,
    string Name,
    string Action
);

public record MatchingAction(
    string Action,
    string Value,
    string DeckCode
);

/// <summary>对局运行时状态。由 MatchManagerService 持有。</summary>
public class MatchInfo
{
    public MatchInfo()
    {
    }

    public MatchInfo(int matchId, LobbyPlayer left, LobbyPlayer right, string ex)
    {
        MatchId = matchId;
        Left = left;
        Right = right;
        currentActionId = 1;
        MatchActions = new();
        PlayerStatusLeft = GameConstants.NotDone;
        PlayerStatusRight = GameConstants.NotDone;
        Ex = ex;
    }

    public int MatchId { get; set; }
    public string Ex { get; set; } = "";
    public MatchStartingInfo? MatchStartingInfo { get; set; }
    public LobbyPlayer? Left { get; set; }
    public LobbyPlayer? Right { get; set; }
    public int Turns { get; set; } = 0;
    public List<MatchAction> MatchActions { get; set; } = new();
    public int currentActionId { get; set; } = 1;
    public int EndResolutionActionId { get; set; } = 0;
    public string PlayerStatusLeft { get; set; } = "not_done";
    public string PlayerStatusRight { get; set; } = "not_done";
    public MulliganResult? MulliganLeft { get; set; }
    public MulliganResult? MulliganRight { get; set; }
    public List<MatchCard> LeftDeck { get; set; } = new();
    public List<MatchCard> RightDeck { get; set; } = new();
    public List<MatchCard> LeftHand { get; set; } = new();
    public List<MatchCard> RightHand { get; set; } = new();
    public string? WinnerSide { get; set; }

    public bool HasPlayer(int playerId)
    {
        return Left?.PlayerId == playerId || Right?.PlayerId == playerId;
    }

    public LobbyPlayer? GetPlayerById(int playerId)
    {
        return Left?.PlayerId == playerId ? Left : Right;
    }

    public List<MatchAction> GetActionsByMinActionId(int minActionId)
    {
        if (MatchActions.Count == 0)
            return new List<MatchAction>();

        if (minActionId <= 1)
            return new List<MatchAction>(MatchActions);

        var startIndex = minActionId - 1;
        if (startIndex >= MatchActions.Count)
            return new List<MatchAction>();

        var count = MatchActions.Count - startIndex;
        return MatchActions.GetRange(startIndex, count);
    }
}

public record MatchResponse(
    Dictionary<string, object> Match,
    List<MatchAction> Actions,
    bool OpponentPolling
);

public record PostMatchResponse(
    string Faction,
    bool Winner
);

public record DeckCardsResult(
    List<MatchCard> Cards,
    MatchCard? Location
);

public record CardLookup(
    string Card,
    string DeckCodeId,
    int ID
);
