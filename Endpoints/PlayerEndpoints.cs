using System.Text.Json;
using System.Text.Json.Nodes;
using fyserver.Models;
using fyserver.Serialization;
using fyserver.Services;

namespace fyserver.Endpoints;

public static class PlayerEndpoints
{
    private static readonly HashSet<int> ReferenceLimitedOfferIds =
        [6715394, 6754571, 6762899, 6762900];

    public static IEndpointRouteBuilder MapPlayerEndpoints(this IEndpointRouteBuilder app)
    {
        // FP 接口 - 前端展示（Front Page）：直接返回原始 JSON 文本，避免序列化 JsonDocument（source-gen 无其 metadata）
        app.MapGet("/fp/", (ContentEntriesService content) =>
        {
            return Results.Text(content.ReadPublishedFrontpage(), "application/json");
        });

        // Store 接口 - 商店数据
        app.MapGet("/store/v2/", async (HttpContext context, StoreConfigService storeConfig, AuthService auth) =>
        {
            var player = await auth.GetUserFromAuthAsync(context);
            if (player == null) return Results.Unauthorized();
            return Results.Ok(BuildStoreResponse(storeConfig, player));
        });
        // 兼容旧客户端：部分版本会请求 /store/ 和 /store/txn
        app.MapGet("/store/", async (HttpContext context, StoreConfigService storeConfig, AuthService auth) =>
        {
            var player = await auth.GetUserFromAuthAsync(context);
            if (player == null) return Results.Unauthorized();
            return Results.Ok(BuildStoreResponse(storeConfig, player));
        });
        app.MapPost("/store/v2/txn", BuyOfferAsync);
        app.MapPost("/store/txn", BuyOfferAsync);

        app.MapGet("/players/{id:int}/resources", async (int id, HttpContext context, AuthService auth, UserStoreService users) =>
        {
            if (await auth.GetPlayerIdFromAuthAsync(context) != id) return Results.Unauthorized();
            var user = await users.GetByIdAsync(id);
            if (user == null) return Results.NotFound();
            return Json(new JsonObject { ["diamonds"] = user.Diamonds, ["gold"] = user.Gold });
        });

        app.MapGet("/players/{id:int}/packs", async (int id, HttpContext context, AuthService auth, UserStoreService users) =>
        {
            if (await auth.GetPlayerIdFromAuthAsync(context) != id) return Results.Unauthorized();
            var user = await users.GetByIdAsync(id);
            return user == null ? Results.NotFound() : Results.Json(user.Packs, FyJsonContext.Default.ListPlayerPack);
        });

        // 客户端改名接口；内部 UserName 是登录索引，改名只更新公开昵称与 Tag。
        app.MapMethods("/players/{id:int}", new[] { "PUT", "POST" },
            (int id, HttpContext context, AuthService auth, UserStoreService users) =>
                RenameRequestAsync(id, context, auth, users));

        // 兼容旧版 / 根配置曾经错误生成的 token 地址。codec token 使用 Base64，
        // 其中的 '/' 会把原本的 /players/{token} 拆成多个路径段；客户端仍可能
        // 在缓存的配置中请求 /players/{first}/{rest}。认证头才是可信身份来源，
        // 因此这里仅按认证用户处理 set-name，并保留数值 ID 路由的鉴权语义。
        app.MapMethods("/players/{**path}", new[] { "PUT", "POST" },
            (string path, HttpContext context, AuthService auth, UserStoreService users) =>
                RenameEncodedPathRequestAsync(path, context, auth, users));

        app.MapGet("/entitlements/{id}", (string id) =>
        {
            List<Entitlement> e = new()
            {
                new Entitlement(
                    EntitlementType: "emote",
                    Name: "emote_appreciate"
                )
            };
            return Results.Ok(e);
        });

        app.MapGet("/{a}/players/{player_id}/friends", (string a, string player_id) =>
        {
            List<int> nil = new();
            return Results.Ok(new FriendsReponse(Friends: nil, PreviousOpponents: nil));
        });

        app.MapMethods("/players/{id}/heartbeat", new[] { "PUT", "DELETE" }, (string id) => Results.Ok(new EmptyResponseDto()));
        app.MapMethods("/players/notifications/{id}", new[] { "PUT", "DELETE" }, (string id) => Results.Ok(new EmptyResponseDto()));

        // 卡牌目录（保留旧 /library 路由）；用户拥有卡牌见 /librarynew。
        app.MapGet("/players/{id}/library", (string id, PlayerLibraryService playerLibrary) =>
            Results.Ok(playerLibrary.Library));

        // 用户卡牌收藏：对齐 NestJS 的 /players/:id/librarynew。
        app.MapGet("/players/{id}/librarynew", async (string id, UserStoreService users) =>
        {
            if (!int.TryParse(id, out var userId) || userId <= 0) return Results.BadRequest();
            var user = await users.GetByIdAsync(userId);
            if (user == null) return Results.NotFound();
            EnsureUserData(user);
            return Results.Json(user.UserCards, FyJsonContext.Default.UserCardCollection);
        });

        // 物品装备
        app.MapGet("/items/{id}", async (string id, HttpContext context, AuthService auth, UserStoreService users, PlayerLibraryService playerLibrary) =>
        {
            if (!int.TryParse(id, out var userId) || userId <= 0)
                return Results.BadRequest($"Invalid user ID: {id}");
            if (await auth.GetPlayerIdFromAuthAsync(context) != userId) return Results.Unauthorized();
            var user = await users.GetByIdAsync(userId);
            if (user == null)
                return Results.NotFound($"User with ID {id} not found");
            EnsureUserData(user);
            var response = new ItemsResponse(
                Date: DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                EquippedItems: user.EquippedItem,
                Items: playerLibrary.Items.Concat(user.Items).ToList()
            );
            return Results.Ok(response);
        });

        app.MapPost("/items/{id}", async (string id, HttpContext context, AuthService auth, EquippedItem item, UserStoreService users) =>
        {
            if (!int.TryParse(id, out var userId) || userId <= 0)
                return Results.BadRequest($"Invalid user ID: {id}");
            if (await auth.GetPlayerIdFromAuthAsync(context) != userId) return Results.Unauthorized();
            var user = await users.GetByIdAsync(userId);
            if (user == null)
                return Results.NotFound($"User with ID {id} not found");
            if (user.EquippedItem == null)
                user.EquippedItem = new List<EquippedItem>();
            // 移除相同槽位的装备
            user.EquippedItem.RemoveAll(i => i.Slot == item.Slot);
            // 添加新装备
            user.EquippedItem.Add(item);
            await users.SaveUserAsync(user);
            return Results.Created();
        });

        app.MapPut("/crate/claim", () =>
        {
            Claim c = new(
                1000,
                new()
                {
                    new(
                        new(
                            1000,
                            null,
                            "card",
                            "card_wildcard_elite"
                        ),
                        5
                    ),
                    new(
                        new(
                            1000,
                            null,
                            "gold",
                            ""
                        ),
                        30
                    )
                }
            );
            return Results.Ok(c);
        });

        return app;
    }

    private static async Task<IResult> RenameAsync(int id, string? requestedName, UserStoreService users)
    {
        var name = requestedName?.Trim() ?? "";
        if (name.Length is < 1 or > 32 || name.Any(char.IsControl))
            return Failure("Player name must be 1–32 characters and contain no control characters");
        var result = await users.WithUserLockAsync<IResult>(id, async user =>
        {
            user.Name = name;
            user.Tag = Random.Shared.Next(1000, 10000);
            await users.SaveUserAsync(user);
            users.RecordIncremental();
            return Json(new JsonObject { ["player_name"] = user.Name, ["player_tag"] = user.Tag });
        });
        return result ?? Results.NotFound();
    }

    private static async Task<IResult> RenameRequestAsync(int id, HttpContext context, AuthService auth, UserStoreService users)
    {
        if (await auth.GetPlayerIdFromAuthAsync(context) != id) return Results.Unauthorized();
        return await ReadRenameRequestAsync(id, context, users);
    }

    private static async Task<IResult> RenameEncodedPathRequestAsync(string path, HttpContext context, AuthService auth, UserStoreService users)
    {
        var authenticatedId = await auth.GetPlayerIdFromAuthAsync(context);
        if (authenticatedId <= 0) return Results.Unauthorized();

        // 若是其它更具体的玩家子路由（例如 /players/{id}/decks），不要吞掉它。
        var firstSegment = path.Split('/', 2)[0];
        if (int.TryParse(firstSegment, out _)) return Results.NotFound();
        return await ReadRenameRequestAsync(authenticatedId, context, users);
    }

    private static async Task<IResult> ReadRenameRequestAsync(int id, HttpContext context, UserStoreService users)
    {
        var body = await ReadBodyAsync(context);
        if (body == null) return Failure("Invalid JSON");
        if (!TryString(body["action"], out var action) || action != "set-name") return Failure("Unsupported action");
        if (!TryString(body["value"], out var value)) return Failure("Player name is required");
        return await RenameAsync(id, value, users);
    }

    private static async Task<IResult> BuyOfferAsync(HttpContext context, AuthService auth, UserStoreService users, StoreConfigService storeConfig)
    {
        var player = await auth.GetUserFromAuthAsync(context);
        if (player == null) return Results.Unauthorized();
        var body = await ReadBodyAsync(context);
        if (body == null || !TryInt(body["offerId"], out var offerId)) return Failure("offerId is required");
        if (!TryInt(body["transactionType"], out var transactionType)) transactionType = 0;
        var config = storeConfig.GetStoreConfig();
        var now = DateTime.UtcNow;
        var groups = config.Groups.Cast<StoreGroup>().Append(new StoreGroup(config.AlwaysFeatured.GroupId,
            config.AlwaysFeatured.Group, config.AlwaysFeatured.StartDate, config.AlwaysFeatured.EndDate,
            config.AlwaysFeatured.Offers));
        var offer = groups.Where(group => IsActive(group.StartDate, group.EndDate, now))
            .SelectMany(group => group.Offers.Where(item => !IsGiftOffer(group, item)))
            .FirstOrDefault(item => item.OfferId == offerId);
        if (offer == null) return Failure("Offer not found or not currently available");
        var priceGold = offer.Gold.GetValueOrDefault();
        var priceDiamonds = offer.Diamonds.GetValueOrDefault();
        // NestJS 的实现支持 transactionType=2（Xsolla/真实货币）直接发放
        // 商品奖励；支付由外部提供商完成，服务端不应把它当作失败请求。
        // transactionType=1/3 分别扣除金币/钻石，0/2 不扣除游戏内货币。
        if (transactionType is not (0 or 1 or 2 or 3)) return Failure("Unsupported transaction type");
        var price = transactionType == 1 ? priceGold : transactionType == 3 ? priceDiamonds : 0;
        if (transactionType == 1 && priceGold <= 0) return Failure("This offer cannot be purchased with gold");
        if (transactionType == 3 && priceDiamonds <= 0) return Failure("This offer cannot be purchased with diamonds");
        // 与 NestJS 保持一致：bonusItems 不是本次收据的发放内容。
        var items = (offer.Items ?? new List<StoreItem>()).ToList();
        if (items.Count == 0 || items.Any(item => item.Data == null || item.Qty <= 0 || item.Qty > 10000 ||
            !IsSupportedReward(item.Data.ItemType)))
            return Failure("This offer contains unsupported rewards");
        if (items.Any(item => item.Data.ItemType == "pack" &&
            (item.Data.CardCount is not > 0 || string.IsNullOrWhiteSpace(item.Data.CardSet))))
            return Failure("Pack reward configuration is incomplete");
        if (items.Where(item => item.Data.ItemType == "pack").Sum(item => (long)item.Qty) > 1000)
            return Failure("Too many packs in one transaction");
        if (items.Any(item => RequiresRewardName(item.Data.ItemType) && string.IsNullOrWhiteSpace(item.Data.Name)))
            return Failure("A reward is missing its name");

        var result = await users.WithUserLockAsync<IResult>(player.Id, async user =>
        {
            EnsureUserData(user);
            if (IsLimitedOffer(offer) && user.PurchasedOffers.GetValueOrDefault(offerId) >= OfferLimit(offer))
                return Failure("Offer purchase limit reached");
            if (transactionType == 1 && user.Gold < price) return Failure("Insufficient gold");
            if (transactionType == 3 && user.Diamonds < price) return Failure("Insufficient diamonds");
            long gold = user.Gold - (transactionType == 1 ? price : 0);
            long diamonds = user.Diamonds - (transactionType == 3 ? price : 0);
            foreach (var item in items)
            {
                switch (item.Data.ItemType)
                {
                    case "gold": gold += item.Qty; break;
                    case "diamonds": diamonds += item.Qty; break;
                    case "card":
                        AddUserCard(user, item.Data, item.Qty);
                        break;
                    case "pack": break;
                    case "draft":
                        user.DraftTickets = checked(user.DraftTickets + item.Qty);
                        break;
                    case "medkit":
                        for (var i = 0; i < item.Qty; i++)
                            user.Medkits.Add(new Medkit { Duration = item.Data.Duration.GetValueOrDefault() });
                        break;
                    case "prop" or "alt_art" or "avatar" or "cardback" or "emote" or "deck" or "equipment":
                        for (var i = 0; i < item.Qty; i++)
                            user.Items.Add(new Item("{}", item.Data.Name!, 0));
                        break;
                    case "token":
                        user.Tokens[item.Data.Name!] = checked(user.Tokens.GetValueOrDefault(item.Data.Name!) + item.Qty);
                        break;
                    default:
                        return Failure("Unsupported reward type");
                }
            }
            if (gold > int.MaxValue || diamonds > int.MaxValue)
                return Failure("Currency balance limit reached");
            user.Gold = (int)gold;
            user.Diamonds = (int)diamonds;
            var nextPackId = user.Packs.Count == 0 ? 1 : user.Packs.Max(pack => pack.Id) + 1;
            foreach (var item in items.Where(item => item.Data.ItemType == "pack"))
            {
                for (var i = 0; i < item.Qty; i++)
                    user.Packs.Add(new PlayerPack
                    {
                        Id = nextPackId++, CardSet = $"{item.Data.CardCount}|{item.Data.CardSet}",
                        CreateDate = now, ModifyDate = now, PlayerId = user.Id
                    });
            }
            if (IsLimitedOffer(offer))
                user.PurchasedOffers[offerId] = user.PurchasedOffers.GetValueOrDefault(offerId) + 1;
            await users.SaveUserAsync(user);
            users.RecordIncremental();
            var receiptId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var receiptItems = new JsonArray();
            foreach (var item in items)
            {
                var receiptItem = JsonSerializer.SerializeToNode(item, FyJsonContext.Default.StoreItem);
                if (item.Data.ItemType == "pack" && receiptItem?["data"] is JsonObject data)
                    data["packIds"] = 0;
                receiptItems.Add(receiptItem);
            }
            return Json(new JsonObject
            {
                ["message"] = "OK", ["status"] = 200, ["newGold"] = user.Gold,
                ["newDiamonds"] = user.Diamonds,
                ["purchasedCount"] = 1, ["receiptId"] = receiptId,
                ["receipts"] = new JsonArray(new JsonObject
                {
                    ["items"] = receiptItems, ["offerName"] = offer.OfferName, ["receiptId"] = receiptId
                })
            });
        });
        return result ?? Results.NotFound();
    }

    private static bool IsActive(string start, string end, DateTime now) =>
        (!DateTimeOffset.TryParse(start, out var from) || from.UtcDateTime <= now) &&
        (!DateTimeOffset.TryParse(end, out var until) || now < until.UtcDateTime);

    private static bool IsGiftOffer(StoreGroup group, StoreOffer offer) => IsGiftOffer(group.Group, offer);
    private static bool IsGiftOffer(AlwaysFeaturedGroup group, StoreOffer offer) => IsGiftOffer(group.Group, offer);

    private static bool IsGiftOffer(int groupNumber, StoreOffer offer)
    {
        if (groupNumber != 1) return false;
        var name = (offer.OfferName ?? "").ToLowerInvariant();
        return (offer.Items?.Count > 1) || name.Contains("weekend") || name.Contains("bundle") ||
               name.Contains("2_for_1") || name.Contains("gold_edition");
    }

    private static bool IsLimitedOffer(StoreOffer offer) => offer.Limit is > 0 || ReferenceLimitedOfferIds.Contains(offer.OfferId);
    private static int OfferLimit(StoreOffer offer) => offer.Limit is > 0 ? offer.Limit.Value : 1;

    private static void EnsureUserData(User user)
    {
        user.UserCards ??= new UserCardCollection();
        user.UserCards.Cards ??= new List<UserCard>();
        user.UserCards.NewCards ??= new List<System.Text.Json.JsonElement>();
        user.Items ??= new List<Item>();
        user.Medkits ??= new List<Medkit>();
        user.Tokens ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        user.CardCollection ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        user.Inventory ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // 将早期 fyserver 版本的字典卡牌数据迁移到 NestJS 兼容结构。
        if (user.UserCards.Cards.Count == 0 && user.CardCollection.Count > 0)
        {
            foreach (var pair in user.CardCollection)
            {
                var gold = pair.Key.EndsWith("#gold", StringComparison.OrdinalIgnoreCase);
                user.UserCards.Cards.Add(new UserCard(gold ? pair.Key[..^5] : pair.Key,
                    gold ? 0 : pair.Value, gold ? pair.Value : 0, 0, 0));
            }
        }
    }

    private static void AddUserCard(User user, StoreItemData data, int quantity)
    {
        EnsureUserData(user);
        var name = data.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("卡牌奖励缺少名称");
        var isGold = data.IsGold == true || data.IsGoldCard == true || data.GoldCard == true;
        var card = user.UserCards.Cards.FirstOrDefault(c =>
            string.Equals(c.CardType, name, StringComparison.OrdinalIgnoreCase));
        if (card == null)
        {
            card = new UserCard(name, 0, 0, 0, 0);
            user.UserCards.Cards.Add(card);
        }
        if (isGold) card.GoldCardCount = checked(card.GoldCardCount + quantity);
        else card.Count = checked(card.Count + quantity);

        // 保留旧管理端/旧客户端所使用的字典索引。
        var key = name + (isGold ? "#gold" : "");
        user.CardCollection[key] = checked(user.CardCollection.GetValueOrDefault(key) + quantity);
    }

    private static bool TryInt(JsonNode? node, out int value)
    {
        value = 0;
        return node is JsonValue json && json.TryGetValue<int>(out value);
    }

    private static bool TryString(JsonNode? node, out string value)
    {
        if (node is JsonValue json && json.TryGetValue<string>(out var text) && text != null)
        {
            value = text;
            return true;
        }
        value = "";
        return false;
    }

    private static async Task<JsonObject?> ReadBodyAsync(HttpContext context)
    {
        try { return JsonNode.Parse(await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted)) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static IResult Json(JsonObject payload) => Results.Text(payload.ToJsonString(), "application/json");
    private static IResult Failure(string message) => Results.Text(
        new JsonObject { ["message"] = message, ["status"] = 400 }.ToJsonString(),
        "application/json", statusCode: 400);

    private static StoreResponse BuildStoreResponse(StoreConfigService storeConfig, User? player)
    {
        var config = storeConfig.GetStoreConfig();
        var now = DateTime.UtcNow;
        var timestamp = (now - new DateTime(1970, 1, 1)).TotalSeconds;
        var purchased = player?.PurchasedOffers ?? new Dictionary<int, int>();
        var groups = config.Groups.Select(group => group with
        {
            Offers = group.Offers.Where(offer => !IsGiftOffer(group, offer)).Select(offer =>
                offer with { Purchased = IsLimitedOffer(offer) && purchased.ContainsKey(offer.OfferId) && purchased[offer.OfferId] >= OfferLimit(offer) }).ToList()
        }).ToList();
        var featured = config.AlwaysFeatured with
        {
            Offers = config.AlwaysFeatured.Offers.Where(offer => !IsGiftOffer(config.AlwaysFeatured, offer)).Select(offer =>
                offer with { Purchased = IsLimitedOffer(offer) && purchased.ContainsKey(offer.OfferId) && purchased[offer.OfferId] >= OfferLimit(offer) }).ToList()
        };
        return new StoreResponse(
            Currency: config.Currency,
            Groups: groups,
            AlwaysFeatured: featured,
            Message: $"Offers for {now:yyyy-MM-ddTHH:mm:ss.ffffffZ}",
            Status: 200,
            Ts: timestamp
        );
    }

    private static bool IsSupportedReward(string type) => type is
        "gold" or "diamonds" or "pack" or "card" or "draft" or "medkit" or
        "prop" or "alt_art" or "avatar" or "cardback" or "emote" or "deck" or "token" or
        "equipment";

    private static bool RequiresRewardName(string type) => type is not ("gold" or "diamonds" or "pack");
}
