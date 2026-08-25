using System.Text.Json;
using fyserver.Models;
using fyserver.Services;

namespace fyserver.Endpoints;

public static class PlayerEndpoints
{
    public static IEndpointRouteBuilder MapPlayerEndpoints(this IEndpointRouteBuilder app)
    {
        // FP 接口 - 前端展示（Front Page）：直接返回原始 JSON 文本，避免序列化 JsonDocument（source-gen 无其 metadata）
        app.MapGet("/fp/", () =>
        {
            var fpt = File.Exists("./config/frontpage.json") ? File.ReadAllText("./config/frontpage.json") : "{}";
            return Results.Text(fpt, "application/json");
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
        app.MapPost("/store/v2/txn", () => Results.Ok());
        app.MapPost("/store/txn", () => Results.Ok());

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
