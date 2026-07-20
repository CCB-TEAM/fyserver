using Newtonsoft.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JsonIgnoreAttribute = System.Text.Json.Serialization.JsonIgnoreAttribute;
namespace fyserver
{
    public class PlayerLibrary
    {
        class Card
        {
            public string card { get; set; }
            public string deck_code_id { get; set; }
            public int ID { get; set; }
        }
        public static LibraryResponse Library = new(
            new List<LibraryItem>
            {
                //new("card_unit_seahawk", 1, 0, 2001, 0),
                //new("card_wildcard_elite",99,0,1065,0),
                //  new("card_wildcard_limited",99,0,1071,0),
                //new("card_wildcard_special",99,0,1076,0)
            },
            new List<object>()
        );

        public static readonly List<Item> Items = new()
        {
        };

        public static Dictionary<string, CardLookup> DeckCodeTable = new();
        public static void InitLibrary(string Path, string path2, string p3)
        {
            List<Card> cs = JsonConvert.DeserializeObject<List<Card>>(File.ReadAllText(Path));
            List<string> emojis = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(path2));
            List<string> cbs = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(p3));
            foreach (var c in cs)
            {
                Library.Cards.Add(new LibraryItem(c.card, 40, 0, c.ID, 0));
                if (!DeckCodeTable.ContainsKey(c.deck_code_id))
                    DeckCodeTable.Add(c.deck_code_id, new CardLookup(c.card, c.deck_code_id, c.ID));
            }
            foreach (var e in emojis)
            {
                Items.Add(new("{}", e, 0));
            }
            foreach (var c in cbs)
            {
                Items.Add(new("{}", c, 0));
            }
        }
    }
    public record DeckAction
    (
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
    //int LocalSubactions = 0
    );
    public record MatchPut(
        int MinActionId = 0,
        int OpponentId = 0,
        long TimeSinceOpponentPing = 0
    );
    /*
    public record ServerMatchAction(
         int TurnNumer,
         List<Object> SubActions,
          string Action = "",
        string ActionType = "",
        int ActionId = 0,
        Dictionary<string, object>? Value = null,
        int? PlayerId = null,
        Dictionary<string, object>? ActionData = null
    );
    */
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

    public class MatchReconnect {
        public string[] Actions { get; set; }
        public bool LocalSubactions { get; set; }
        public MatchData Match { get; set; }
        public MulliganResult MulliganLeft { get; set; }
        public MulliganResult MulliganRight { get; set; }
        public bool SameTurn { get; set; }
        public StartingData StartingData { get; set; }
        public int TimeSinceStartOfTurn { get; set; }
        public bool WaitingForSitNGoMatch { get; set; }
    };

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
    public record ProviderDetail(
        string PaymentProvider
    );
    // Session DTOs as records
    public record Session(
        string Provider,
        ProviderDetail ProviderDetails,
        string ClientType,
        string Build,
        string PlatformType,
        string AppGuid,
        string Version,
        string PlatformInfo,
        string PlatformVersion,
        string AccountLinking,
        string Language,
        bool AutomaticAccountCreation,
        string Username,
        string Password
    );
    public record MiniSitNGo(
        string EndDate,
        [property: JsonPropertyName("hasWon")] bool HasWon,
        int Id,
        string Name,
        string RulesJsonStr,
        string StartDate
    );
    /*
    public record MiniSitNGoReward(
        string type
    );
    public record MiniSitNGoLocalizationItem(
        string[] rules_list,
        string description
    );
    public record MiniSitNGoLocalization(
        [property: JsonPropertyName("ZH-HANS")] MiniSitNGoLocalizationItem zh_hans
    );
    public record MiniSitNGoChooseSpawnCardOnStartOfTurnConfig(
        int cost,
        string[] cards
    );
    public record MiniSitNGoConfig(
        MiniSitNGoReward reward,
        MiniSitNGoLocalization localization,
        int starting_kredits,
        string skip_start_of_turn_draw,
        string draw_card_on_destroy_enemy,
        MiniSitNGoChooseSpawnCardOnStartOfTurnConfig choose_spawn_card_on_start_of_turn
    );
    */
    public record Entitlement(
    string EntitlementType,
    string Name
  );
    public record FriendsReponse(
            List<int> Friends,
            List<int> PreviousOpponents
        );
    // Config records
    public record CurrentUser(
        int ClientId,
        int Exp,
        string ExternalId,
        int Iat,
        int IdentityId,
        string Iss,
        string Jti,
        string Language,
        string Payment,
        string PlayerId,
        string Provider,
        List<string> Roles,
        string Tier,
        int UserId,
        string UserName
    );
    public record Endpoints(
        string Draft,
        string Email,
        string Lobbyplayers,
        string Matches,
        string Matches2,
        string MyDraft,
        string MyItems,
        string MyPlayer,
        string Players,
        string Purchase,
        string Root,
        string Session,
        string Store,
        string Tourneys,
        string Transactions,
        string ViewOffers
    );

    public record Config(
        CurrentUser CurrentUser,
        Endpoints Endpoints
    );

    // Library/Cards records
    public record Card(
        string CardType,
        int Count,
        int GoldCardCount,
        int Id,
        int RecentlyCraftedCount
    );

    public record Library(
        List<Card> Cards,
        List<object> NewCards
    );

    public record ItemsResponse(
        string Date,
        List<EquippedItem> EquippedItems,
        List<Item> Items
    );
    // WebSocket messages as records
    public record WebSocketMessage(
        string Timestamp,
        string Context = "",
        string Message = "",
        string Channel = "",
        string Sender = "",
        string Receiver = "",
        int? MatchId = null
    );

    // Match response types as records
    public record MatchResponse(
        Dictionary<string, object> Match,
        List<MatchAction> Actions,
        bool OpponentPolling
    );

    public record PostMatchResponse(
        string Faction,
        bool Winner
    );

    // Service classes as records
    public record DeckCardsResult(
        List<MatchCard> Cards,
        MatchCard? Location
    );

    public record CardLookup(
        string Card,
        string DeckCodeId,
        int ID
    );

    public record ClientInfo(
        User User,
        System.Net.WebSockets.WebSocket Client
    );
    //// UserStoreService.cs
    //public class UserStoreService : IDisposable
    //{
    //    private readonly LiteDbService _db;
    //    private readonly bool _ownsConnection;

    //    // 构造函数
    //    public UserStoreService(LiteDbService dbService)
    //    {
    //        _db = dbService;
    //        _ownsConnection = false;
    //    }

    //    public UserStoreService() : this(new LiteDbService("Filename=./db/users.db;Connection=Shared"))
    //    {
    //        _ownsConnection = true;
    //    }

    //    public Task<User?> GetByUserNameAsync(string userName)
    //    {
    //        if (string.IsNullOrWhiteSpace(userName)) return Task.FromResult<User?>(null);
    //        var a = _db.Get<User>($"user:username:{userName}");
    //        return Task.FromResult<User?>(a);
    //    }

    //    public Task<User?> GetByIdAsync(int userId)
    //    {
    //        if (userId <= 0) return Task.FromResult<User?>(null);
    //        var u = _db.Get<User>($"user:id:{userId}");
    //        return Task.FromResult<User?>(u);
    //    }

    //    // 核心保存逻辑
    //    public Task SaveUserAsync(User user)
    //    {
    //        if (string.IsNullOrEmpty(user.UserName) || user.Id == 0)
    //            throw new ArgumentException("Invalid user data: Missing UserName or ID");

    //        // 更新修改时间
    //        user.UpdatedAt = DateTime.UtcNow;

    //        // 模拟 LevelDB 的双重索引 (冗余存储)
    //        // LiteDB 的 Batch 操作保证了原子性：要么都成功，要么都失败
    //        var puts = new Dictionary<string, object>
    //        {
    //            [$"user:username:{user.UserName}"] = user,
    //            [$"user:id:{user.Id}"] = user
    //        };

    //        _db.Batch(puts);
    //        return Task.CompletedTask;
    //    }

    //    // 创建用户：处理 ID 生成和冲突
    //    public async Task<User> CreateUserAsync(string userName)
    //    {
    //        // 1. 检查用户名是否存在
    //        var existingUser = await GetByUserNameAsync(userName);
    //        if (existingUser != null)
    //        {
    //            // 如果是幂等设计，可以直接返回旧用户；如果是严格创建，抛异常
    //            // 这里为了安全起见，如果不慎重复调用，返回已存在的
    //            return existingUser;
    //            // 或者: throw new InvalidOperationException ($"User '{userName}' already exists");
    //        }

    //        var user = new User(userName);
    //        // 2. 生成唯一 ID (带冲突重试)
    //        var rnd = Random.Shared;
    //        int newId;
    //        bool idExists;
    //        newId = rnd.Next(100000, 1000000);
    //        user.Id = newId;

    //        // 3. 保存
    //        await SaveUserAsync(user);
    //        return user;
    //    }

    //    public async Task DeleteUserAsync(int userId)
    //    {
    //        var user = await GetByIdAsync(userId);
    //        if (user == null) return;

    //        var deletes = new List<string>
    //        {
    //            $"user:username:{user.UserName}",
    //            $"user:id:{userId}"
    //        };

    //        _db.Batch(null, deletes);
    //        await Task.CompletedTask;
    //    }

    //    public Task<List<User>> GetAllUsersAsync()
    //    {
    //        // 只取一种 Key 前缀，防止数据重复
    //        var list = _db.GetAllByPrefix<User>("user:id:");
    //        return Task.FromResult(list);
    //    }

    //    public void Dispose()
    //    {
    //        if (_ownsConnection)
    //        {
    //            _db?.Dispose();
    //        }
    //    }
    //}
    public class FasterUserStoreService : IDisposable
    {
        public readonly FasterKvService _db;
        private readonly bool _ownsConnection;
        public void Record2()
        {
            _db.Checkpoint();
        }
        public void Record1()
        {
            _db.Checkpoint(FASTER.core.CheckpointType.FoldOver);
        }
        // 构造函数
        public FasterUserStoreService(FasterKvService dbService)
        {
            _db = dbService;
            _ownsConnection = false;
        }

        public FasterUserStoreService() : this(new FasterKvService())
        {
            _ownsConnection = true;
        }
        public Task<User?> GetByUserNameAsync(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return Task.FromResult<User?>(null);
            var a = _db.Get<User>($"user:username:{userName}");
            return Task.FromResult<User?>(a);
        }

        public Task<User?> GetByIdAsync(int userId)
        {
            if (userId <= 0) return Task.FromResult<User?>(null);
            var u = _db.Get<User>($"user:id:{userId}");
            return Task.FromResult<User?>(u);
        }

        // 核心保存逻辑
        public Task SaveUserAsync(User user)
        {
            if (string.IsNullOrEmpty(user.UserName) || user.Id == 0)
                throw new ArgumentException("Invalid user data: Missing UserName or ID");

            // 更新修改时间
            user.UpdatedAt = DateTime.UtcNow;

            // 模拟 LevelDB 的双重索引 (冗余存储)
            // LiteDB 的 Batch 操作保证了原子性：要么都成功，要么都失败
            var puts = new Dictionary<string, object>
            {
                [$"user:username:{user.UserName}"] = user,
                [$"user:id:{user.Id}"] = user
            };

            _db.Batch(puts);
            return Task.CompletedTask;
        }

        // 创建用户：处理 ID 生成和冲突
        public async Task<User> CreateUserAsync(string userName)
        {
            // 1. 检查用户名是否存在
            var existingUser = await GetByUserNameAsync(userName);
            if (existingUser != null)
            {
                // 如果是幂等设计，可以直接返回旧用户；如果是严格创建，抛异常
                // 这里为了安全起见，如果不慎重复调用，返回已存在的
                return existingUser;
                // 或者: throw new InvalidOperationException ($"User '{userName}' already exists");
            }

            var user = new User(userName);
            // 2. 生成唯一 ID (带冲突重试)
            var rnd = Random.Shared;
            int newId;
            bool idExists;
            newId = rnd.Next(100000, 1000000);
            user.Id = newId;

            // 3. 保存
            await SaveUserAsync(user);
            return user;
        }

        public async Task DeleteUserAsync(int userId)
        {
            var user = await GetByIdAsync(userId);
            if (user == null) return;

            var deletes = new List<string>
            {
                $"user:username:{user.UserName}",
                $"user:id:{userId}"
            };

            _db.Batch(null, deletes);
            await Task.CompletedTask;
        }

        public Task<List<User>> GetAllUsersAsync()
        {
            // 只取一种 Key 前缀，防止数据重复
            var list = _db.GetAllByPrefix<User>("user:id:");
            return Task.FromResult(list);
        }

        public void Dispose()
        {
            if (_ownsConnection)
            {
                _db?.Dispose();
            }
        }
    }

    // User.cs
    public class User
    {
        // 无参构造函数对 LiteDB 和 System.Text.Json 都是必须的
        public User()
        {
            // 初始化集合，防止空引用
            Decks = new Dictionary<int, Deck>();
            EquippedItem = new List<EquippedItem>();
        }

        public User(string userName) : this()
        {
            UserName = userName;
            // 默认值
            Name = "XDLG";
            Locale = "zh-Hans";
            Tag = Random.Shared.Next(1000, 9999);
            Banned = false;
        }
        public User(int Id ,string userName) : this()
        {
            UserName = userName;
            // 默认值
            Name = userName;
            Locale = "zh-Hans";
            Tag = Random.Shared.Next(1000, 9999);
            Banned = false;
        }

        public int Id { get; set; }
        public string UserName { get; set; } = "";
        public string Name { get; set; } = "";
        public string Locale { get; set; } = "";
        public int Tag { get; set; }

        // System.Text.Json 支持 Dictionary<int, T> 的序列化
        // 会自动将 int key 转为 string key ("1": {...})
        public Dictionary<int, Deck> Decks { get; set; }

        public List<EquippedItem> EquippedItem { get; set; }
        public bool Banned { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class Deck
    {
        public Deck()
        {
        }

        public Deck(CreateDeck createDeck, int playerId)
        {
            Name = createDeck.Name;
            MainFaction = createDeck.MainFaction;
            AllyFaction = createDeck.AllyFaction;
            CardBack = $"cardback_starter_{createDeck.MainFaction.ToLower()}";
            DeckCode = createDeck.DeckCode;
            Favorite = false;
            Id = Random.Shared.Next(100000, 999999);
            PlayerId = playerId;
            LastPlayed = DateTime.Now;
            CreateDate = DateTime.Now;
            ModifyDate = DateTime.Now;
        }

        public string Name { get; set; } = "";
        public string MainFaction { get; set; } = "";
        public string AllyFaction { get; set; } = "";
        public string CardBack { get; set; } = "";
        public string DeckCode { get; set; } = "";
        public bool Favorite { get; set; }
        public int Id { get; set; }
        public int PlayerId { get; set; }

        [JsonIgnore]
        public DateTime LastPlayed { get; set; }

        public string LastPlayedString
        {
            get => LastPlayed.ToString("o");
            set => LastPlayed = DateTime.Parse(value);
        }

        [JsonIgnore]
        public DateTime CreateDate { get; set; }

        public string CreateDateString
        {
            get => CreateDate.ToString("o");
            set => CreateDate = DateTime.Parse(value);
        }

        [JsonIgnore]
        public DateTime ModifyDate { get; set; }

        public string ModifyDateString
        {
            get => ModifyDate.ToString("o");
            set => ModifyDate = DateTime.Parse(value);
        }
    }
    public class EquippedItem
    {
        public EquippedItem(string faction, string itemId, string slot)
        {
            Faction = faction;
            ItemId = itemId;
            Slot = slot;
        }

        public string Faction { get; set; } = "";
        public string ItemId { get; set; } = "";
        public string Slot { get; set; } = "";
    }
    public record Item(
      string details,
      string item_id,
              int cnt = 0
        );
    
    public record ClaimItemData (
        int Dust,
        [property: JsonPropertyName("isGold")] bool? IsGold,
        [property: JsonPropertyName("itemType")] string ItemType,
        string Name
    );
    public record ClaimItem (
        ClaimItemData Data,
        int Qty
    );
    public record Claim (
        int Dust,
        List<ClaimItem> Items
    );

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
            //RightActions = new ();
            PlayerStatusLeft = GameConstants.NotDone;
            PlayerStatusRight = GameConstants.NotDone;
            Ex = ex;
            /*
            LeftMinactionid = 0;
            RightMinactionid = 0;
            */
            //MatchStartingInfo = new(new(left.PlayerId,"left",new(),config.appconfig.getAddressHttpR()+"/matches/v2/"+matchId+"/actions",0,0,left.DeckId,right.DeckId,1,matchId,"", config.appconfig.getAddressHttpR() + "/matches/v2/" +,DateTimeOffset.UtcNow.ToString(),new(),left.PlayerId,right.PlayerId,GameConstants.R),new());
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
        //public List<MatchAction> RightActions { get; set; } = new();
        public string PlayerStatusLeft { get; set; } = "not_done";
        public string PlayerStatusRight { get; set; } = "not_done";
        public MulliganResult? MulliganLeft { get; set; }
        public MulliganResult? MulliganRight { get; set; }
        public List<MatchCard> LeftDeck { get; set; } = new();
        public List<MatchCard> RightDeck { get; set; } = new();
        public List<MatchCard> LeftHand { get; set; } = new();
        public List<MatchCard> RightHand { get; set; } = new();
        /*
        public int LeftMinactionid { get; set; }
        public int RightMinactionid { get; set; }
        */
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
            //return Left?.PlayerId == playerId&&!Ex.Equals("pw") ? RightActions : LeftActions;
        }
    }
    // 静态常量和工具类
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
        public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false
        };
    }
    // 游戏库和物品相关的record类型
    public record LibraryItem(
        string CardType,
        int Count,
        int GoldCardCount,
        int Id,
        int RecentlyCraftedCount
    );

    public record LibraryResponse(
        List<LibraryItem> Cards,
        List<object> NewCards
    );

    // WebSocket消息相关的record类型
    public record WsNotification(
        string Message,
        string Channel,
        string Context,
        DateTime Timestamp,
        string Sender,
        string Receiver
    );

    // 错误响应相关的record类型
    public record ErrorResponse(
        string Error,
        string Message,
        int StatusCode
    );
    public record BannedResponse(
        Error Error,
        string Message,
        int StatusCode
    );

    public record Error(
        string Code,
        string Description
    );
    public record SessionResponse(
        string AchievementsUrl,
        List<object> AllKnockoutTourneys,
        int BritainLevel,
        int BritainLevelClaimed,
        int BritainXp,
        List<object> CardsBlacklist,
        int ClaimableCrateLevel,
        int ClientId,
        string Currency,
        Dictionary<string, object> CurrentKnockoutTourney,
        MiniSitNGo CurrentMiniSitNGo,
        string DailymissionsUrl,
        Dictionary<string, object> Decks,
        string DecksUrl,
        int Diamonds,
        string DoubleXpEndDate,
        int DraftAdmissions,
        int Dust,
        string? Email,
        bool EmailRewardReceived,
        bool EmailVerified,
        bool ExtendedRewards,
        int GermanyLevel,
        int GermanyLevelClaimed,
        int GermanyXp,
        int Gold,
        bool HasBeenOfficer,
        string HeartbeatUrl,
        bool IsOfficer,
        bool IsOnline,
        int JapanLevel,
        int JapanLevelClaimed,
        int JapanXp,
        string Jti,
        string Jwt,
        string LastCrateClaimedDate,
        string? LastDailyMissionCancel,
        string LastDailyMissionRenewal,
        string LastLogonDate,
        List<object> LaunchMessages,
        string LibraryUrl,
        string LinkerAccount,
        string Locale,
        Dictionary<string, object> Misc,
        List<object> NewCards,
        Dictionary<string, object> NewPlayerLoginReward,
        bool Npc,
        bool OnlineFlag,
        string PacksUrl,
        int PlayerId,
        string PlayerName,
        string PlayerTag,
        List<object> Rewards,
        string SeasonEnd,
        int SeasonWins,
        string ServerOptions,
        string ServerTime,
        int SovietLevel,
        int SovietLevelClaimed,
        int SovietXp,
        int Stars,
        int TutorialsDone,
        List<string> TutorialsFinished,
        int UsaLevel,
        int UsaLevelClaimed,
        int UsaXp,
        int UserId
    );

    // 商店相关的数据模型
    public record StoreItemData(
        [property: JsonPropertyName("itemType")] string ItemType,
        [property: JsonPropertyName("cardCount")] int? CardCount = null,
        [property: JsonPropertyName("cardSet")] string? CardSet = null,
        [property: JsonPropertyName("guaranteedGoldCards")] int? GuaranteedGoldCards = null,
        [property: JsonPropertyName("name")] string? Name = null,
        [property: JsonPropertyName("duration")] int? Duration = null,
        [property: JsonPropertyName("isGold")] bool? IsGold = null,
        [property: JsonPropertyName("month")] int? Month = null,
        [property: JsonPropertyName("year")] int? Year = null
    );

    public record StoreItem(
        [property: JsonPropertyName("data")] StoreItemData Data,
        [property: JsonPropertyName("qty")] int Qty
    );

    public record StoreOffer(
        [property: JsonPropertyName("offerId")] int OfferId,
        [property: JsonPropertyName("offerName")] string OfferName,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string? Description = null,
        [property: JsonPropertyName("items")] List<StoreItem>? Items = null,
        [property: JsonPropertyName("bonusItems")] List<StoreItem>? BonusItems = null,
        [property: JsonPropertyName("diamonds")] int? Diamonds = null,
        [property: JsonPropertyName("gold")] int? Gold = null,
        [property: JsonPropertyName("real")] double? Real = null,
        [property: JsonPropertyName("mainImage")] string? MainImage = null,
        [property: JsonPropertyName("thumbnail")] string? Thumbnail = null,
        [property: JsonPropertyName("smallThumb")] string? SmallThumb = null,
        [property: JsonPropertyName("priority")] int? Priority = null,
        [property: JsonPropertyName("limit")] int? Limit = null,
        [property: JsonPropertyName("slotType")] string? SlotType = null,
        [property: JsonPropertyName("slotValue")] string? SlotValue = null,
        [property: JsonPropertyName("timed")] bool? Timed = null,
        [property: JsonPropertyName("bonus")] bool? Bonus = null,
        [property: JsonPropertyName("fulfilAfter")] string? FulfilAfter = null
    );

    public record StoreGroup(
        [property: JsonPropertyName("groupId")] int GroupId,
        [property: JsonPropertyName("group")] int Group,
        [property: JsonPropertyName("startDate")] string StartDate,
        [property: JsonPropertyName("endDate")] string EndDate,
        [property: JsonPropertyName("offers")] List<StoreOffer> Offers,
        [property: JsonPropertyName("hidden")] bool? Hidden = null
    );

    public record AlwaysFeaturedGroup(
        [property: JsonPropertyName("group")] int Group,
        [property: JsonPropertyName("groupId")] int GroupId,
        [property: JsonPropertyName("startDate")] string StartDate,
        [property: JsonPropertyName("endDate")] string EndDate,
        [property: JsonPropertyName("offers")] List<StoreOffer> Offers
    );

    public record StoreResponse(
        [property: JsonPropertyName("currency")] string Currency,
        [property: JsonPropertyName("groups")] List<StoreGroup> Groups,
        [property: JsonPropertyName("alwaysFeatured")] AlwaysFeaturedGroup AlwaysFeatured,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("status")] int Status,
        [property: JsonPropertyName("ts")] double Ts
    );
}
