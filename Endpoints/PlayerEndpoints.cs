using System.Text.Json;
using System.Text.Json.Nodes;
using fyserver.Models;
using fyserver.Serialization;
using fyserver.Services;

namespace fyserver.Endpoints;

public static class PlayerEndpoints
{
    public static IEndpointRouteBuilder MapPlayerEndpoints(this IEndpointRouteBuilder app)
    {
        // FP 接口 - 前端展示（Front Page）：直接返回原始 JSON 文本，避免序列化 JsonDocument（source-gen 无其 metadata）
        app.MapGet("/fp/", (ContentEntriesService content) =>
        {
            return Results.Text(content.ReadPublishedFrontpage(), "application/json");
        });

        // Store 接口 - 商店数据
        app.MapGet("/store/v2/", (StoreConfigService storeConfig) =>
        {
            return Results.Ok(BuildStoreResponse(storeConfig));
        });
        // 兼容旧客户端：部分版本会请求 /store/ 和 /store/txn
        app.MapGet("/store/", (StoreConfigService storeConfig) =>
        {
            return Results.Ok(BuildStoreResponse(storeConfig));
        });
        app.MapPost("/store/v2/txn", BuyOfferAsync);
        app.MapPost("/store/txn", BuyOfferAsync);

        app.MapGet("/players/{id:int}/resources", async (int id, HttpContext context, AuthService auth, UserStoreService users) =>
        {
            if (await auth.GetPlayerIdFromAuthAsync(context) != id) return Results.Unauthorized();
            var user = await users.GetByIdAsync(id);
            if (user == null) return Results.NotFound();
            return Json(new JsonObject { ["diamonds"] = user.Diamonds, ["gold"] = user.Gold, ["dust"] = user.Dust });
        });

        app.MapGet("/players/{id:int}/packs", async (int id, HttpContext context, AuthService auth, UserStoreService users) =>
        {
            if (await auth.GetPlayerIdFromAuthAsync(context) != id) return Results.Unauthorized();
            var user = await users.GetByIdAsync(id);
            return user == null ? Results.NotFound() : Results.Json(user.Packs, FyJsonContext.Default.ListPlayerPack);
        });

        // 客户端改名接口；内部 UserName 是登录索引，改名只更新公开昵称与 Tag。
        app.MapPut("/players/{id:int}", async (int id, HttpContext context, AuthService auth, UserStoreService users) =>
        {
            if (await auth.GetPlayerIdFromAuthAsync(context) != id) return Results.Unauthorized();
            var body = await ReadBodyAsync(context);
            if (body == null) return Failure("Invalid JSON");
            if (body["action"]?.GetValue<string>() != "set-name") return Failure("Unsupported action");
            return await RenameAsync(id, body["value"]?.GetValue<string>(), users);
        });

        // 兼容旧客户端首次命名请求。非首次命名请走 PUT /players/{id}。
        app.MapPost("/players/{id:int}/friends", async (int id, HttpContext context, AuthService auth, UserStoreService users) =>
        {
            if (await auth.GetPlayerIdFromAuthAsync(context) != id) return Results.Unauthorized();
            var body = await ReadBodyAsync(context);
            if (body == null) return Failure("Invalid JSON");
            if (body["friend_tag"]?.GetValue<int>() != 0) return Results.Text("OK");
            return await RenameAsync(id, body["friend_name"]?.GetValue<string>(), users, legacyResponse: true);
        });

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

        // 卡牌库
        app.MapGet("/players/{id}/librarynew", (string id, PlayerLibraryService playerLibrary) =>
            Results.Ok(playerLibrary.Library));
        app.MapGet("/players/{id}/library", (string id, PlayerLibraryService playerLibrary) =>
            Results.Ok(playerLibrary.Library));

        // 物品装备
        app.MapGet("/items/{id}", async (string id, UserStoreService users, PlayerLibraryService playerLibrary) =>
        {
            if (!int.TryParse(id, out var userId) || userId <= 0)
                return Results.BadRequest($"Invalid user ID: {id}");
            var user = await users.GetByIdAsync(userId);
            if (user == null)
                return Results.NotFound($"User with ID {id} not found");
            var response = new ItemsResponse(
                Date: DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                EquippedItems: user.EquippedItem,
                Items: playerLibrary.Items.ToList()
            );
            return Results.Ok(response);
        });

        app.MapPost("/items/{id}", async (string id, EquippedItem item, UserStoreService users) =>
        {
            if (!int.TryParse(id, out var userId) || userId <= 0)
                return Results.BadRequest($"Invalid user ID: {id}");
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

    private static async Task<IResult> RenameAsync(int id, string? requestedName, UserStoreService users, bool legacyResponse = false)
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
            return legacyResponse ? Results.Text("OK") : Json(new JsonObject { ["player_name"] = user.Name, ["player_tag"] = user.Tag });
        });
        return result ?? Results.NotFound();
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
            .SelectMany(group => group.Offers).FirstOrDefault(item => item.OfferId == offerId);
        if (offer == null) return Failure("Offer not found or not currently available");
        if (offer.Real is > 0) return Failure("Real-money offers require a payment provider");
        var priceGold = offer.Gold.GetValueOrDefault();
        var priceDiamonds = offer.Diamonds.GetValueOrDefault();
        if (transactionType == 0)
            transactionType = priceGold > 0 && priceDiamonds == 0 ? 1 : priceDiamonds > 0 && priceGold == 0 ? 3 : 0;
        if (transactionType != 1 && transactionType != 3) return Failure("Choose transactionType 1 (gold) or 3 (diamonds)");
        var price = transactionType == 1 ? priceGold : priceDiamonds;
        if (price <= 0) return Failure("Offer cannot be purchased with selected currency");
        var items = (offer.Items ?? new List<StoreItem>()).Concat(offer.BonusItems ?? new List<StoreItem>()).ToList();
        if (items.Count == 0 || items.Any(item => item.Data == null || item.Qty <= 0 || item.Qty > 10000 ||
            item.Data.ItemType is not ("gold" or "diamonds" or "dust" or "pack")))
            return Failure("This offer contains unsupported rewards");
        if (items.Any(item => item.Data.ItemType == "pack" &&
            (item.Data.CardCount is not > 0 || string.IsNullOrWhiteSpace(item.Data.CardSet))))
            return Failure("Pack reward configuration is incomplete");
        if (items.Where(item => item.Data.ItemType == "pack").Sum(item => (long)item.Qty) > 1000)
            return Failure("Too many packs in one transaction");

        var result = await users.WithUserLockAsync<IResult>(player.Id, async user =>
        {
            if (offer.Limit is > 0 && user.PurchasedOffers.GetValueOrDefault(offerId) >= offer.Limit.Value)
                return Failure("Offer purchase limit reached");
            if (transactionType == 1 && user.Gold < price) return Failure("Insufficient gold");
            if (transactionType == 3 && user.Diamonds < price) return Failure("Insufficient diamonds");
            long gold = user.Gold - (transactionType == 1 ? price : 0);
            long diamonds = user.Diamonds - (transactionType == 3 ? price : 0);
            long dust = user.Dust;
            foreach (var item in items)
            {
                switch (item.Data.ItemType)
                {
                    case "gold": gold += item.Qty; break;
                    case "diamonds": diamonds += item.Qty; break;
                    case "dust": dust += item.Qty; break;
                }
            }
            if (gold > int.MaxValue || diamonds > int.MaxValue || dust > int.MaxValue)
                return Failure("Currency balance limit reached");
            user.Gold = (int)gold;
            user.Diamonds = (int)diamonds;
            user.Dust = (int)dust;
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
                ["newDiamonds"] = user.Diamonds, ["newDust"] = user.Dust,
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

    private static bool TryInt(JsonNode? node, out int value)
    {
        value = 0;
        return node is JsonValue json && json.TryGetValue<int>(out value);
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

    private static StoreResponse BuildStoreResponse(StoreConfigService storeConfig)
    {
        var config = storeConfig.GetStoreConfig();
        var now = DateTime.UtcNow;
        var timestamp = (now - new DateTime(1970, 1, 1)).TotalSeconds;
        return new StoreResponse(
            Currency: config.Currency,
            Groups: config.Groups,
            AlwaysFeatured: config.AlwaysFeatured,
            Message: $"Offers for {now:yyyy-MM-ddTHH:mm:ss.ffffffZ}",
            Status: 200,
            Ts: timestamp
        );
    }
}
