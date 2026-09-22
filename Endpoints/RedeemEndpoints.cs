using System.Text.Json.Nodes;
using fyserver.Models;
using fyserver.Services;

namespace fyserver.Endpoints;

/// <summary>游戏侧兑换码接口；协议与 NestJS 的 GET /redeem/:code 保持一致。</summary>
public static class RedeemEndpoints
{
    public static IEndpointRouteBuilder MapRedeemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/redeem/{code}", async (string code, HttpContext context, AuthService auth,
            UserStoreService users, RedeemCodeService redeemCodes) =>
        {
            var player = await auth.GetUserFromAuthAsync(context);
            if (player == null)
                return Results.Text(new JsonObject { ["message"] = "Authorization required" }.ToJsonString(), "application/json", statusCode: 401);

            code = code.Split('?', 2)[0];
            var claim = await redeemCodes.ClaimAsync(code, player.Id, async redeem =>
                await users.WithUserLockAsync<List<RedeemReward>>(player.Id, async user =>
                {
                    EnsureUserData(user);
                    var claimed = new List<RedeemReward>();
                    foreach (var reward in redeem.Rewards)
                    {
                        ApplyReward(user, reward);
                        claimed.Add(new RedeemReward
                        {
                            Data = (JsonObject)reward.Data.DeepClone(),
                            Qty = reward.Qty
                        });
                    }
                    await users.SaveUserAsync(user);
                    users.RecordIncremental();
                    return claimed;
                }));

            if (claim.Status == "error")
                return Results.Text(new JsonObject { ["message"] = "Failed to grant redeem rewards" }.ToJsonString(), "application/json", statusCode: 500);
            if (claim.Status == "userNotFound")
                return Results.NotFound();
            var response = new JsonObject { ["status"] = claim.Status };
            if (claim.Items != null)
                response["items"] = new JsonArray(claim.Items.Select(item => new JsonObject
                {
                    ["data"] = item.Data.DeepClone(),
                    ["qty"] = item.Qty
                }).ToArray());
            return Results.Text(response.ToJsonString(), "application/json");
        });
        return app;
    }

    private static void EnsureUserData(User user)
    {
        user.UserCards ??= new UserCardCollection();
        user.UserCards.Cards ??= [];
        user.UserCards.NewCards ??= [];
        user.Items ??= [];
        user.Medkits ??= [];
        user.Tokens ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        user.CardCollection ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        user.Packs ??= [];
    }

    private static void ApplyReward(User user, RedeemReward reward)
    {
        var data = reward.Data;
        var type = data["itemType"]?.GetValue<string>() ?? "";
        var qty = reward.Qty;
        switch (type)
        {
            case "diamonds": user.Diamonds = checked(user.Diamonds + qty); break;
            case "gold": user.Gold = checked(user.Gold + qty); break;
            case "pack":
                var nextId = user.Packs.Count == 0 ? 0 : user.Packs.Max(pack => pack.Id) + 1;
                var cardSet = $"{ReadInt(data["cardCount"])}|{data["cardSet"]?.GetValue<string>()}";
                for (var i = 0; i < qty; i++)
                    user.Packs.Add(new PlayerPack
                    {
                        Id = nextId++, CardSet = cardSet, CreateDate = DateTime.UtcNow,
                        ModifyDate = DateTime.UtcNow, PlayerId = user.Id
                    });
                break;
            case "card":
                var cardName = data["name"]!.GetValue<string>();
                var gold = ReadBool(data["isGold"]) || ReadBool(data["is_gold_card"]) || ReadBool(data["gold_card"]);
                var card = user.UserCards.Cards.FirstOrDefault(item =>
                    string.Equals(item.CardType, cardName, StringComparison.OrdinalIgnoreCase));
                if (card == null)
                {
                    card = new UserCard(cardName, 0, 0, 0, 0);
                    user.UserCards.Cards.Add(card);
                }
                if (gold) card.GoldCardCount = checked(card.GoldCardCount + qty);
                else card.Count = checked(card.Count + qty);
                user.CardCollection[cardName + (gold ? "#gold" : "")] =
                    user.CardCollection.GetValueOrDefault(cardName + (gold ? "#gold" : "")) + qty;
                break;
            case "draft": user.DraftTickets = checked(user.DraftTickets + qty); break;
            case "medkit":
                var duration = ReadInt(data["duration"]);
                for (var i = 0; i < qty; i++) user.Medkits.Add(new Medkit { Duration = duration });
                break;
            case "prop":
            case "alt_art":
            case "avatar":
            case "cardback":
            case "emote":
            case "deck":
                var itemName = data["name"]!.GetValue<string>();
                for (var i = 0; i < qty; i++) user.Items.Add(new Item("{}", itemName, 0));
                break;
            case "token":
                var token = data["name"]!.GetValue<string>();
                user.Tokens[token] = checked(user.Tokens.GetValueOrDefault(token) + qty);
                break;
            default: throw new InvalidOperationException("Unsupported redeem reward");
        }
    }

    private static int ReadInt(JsonNode? value) => value is JsonValue json && json.TryGetValue<int>(out var number) ? number : -1;
    private static bool ReadBool(JsonNode? value)
    {
        if (value is not JsonValue json) return false;
        if (json.TryGetValue<bool>(out var flag)) return flag;
        return json.TryGetValue<int>(out var number) && number != 0;
    }
}
