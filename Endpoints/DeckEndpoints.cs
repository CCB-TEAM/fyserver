using Microsoft.AspNetCore.Mvc;
using fyserver.Models;
using fyserver.Services;

namespace fyserver.Endpoints;

public static class DeckEndpoints
{
    public static IEndpointRouteBuilder MapDeckEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/players/{id}/decks", async (string id, CreateDeck createDeck, UserStoreService users) =>
        {
            if (!int.TryParse(id, out var playerId) || playerId <= 0)
                return Results.BadRequest($"Invalid user ID: {id}");
            var user = await users.GetByIdAsync(playerId);
            if (user == null)
                return Results.NotFound($"User with ID {id} not found");
            var deck = new Deck(createDeck, user.Id);
            user.Decks[deck.Id] = deck;

            await users.SaveUserAsync(user);

            return Results.Ok(new DeckSummaryDto(
                deck.Name,
                deck.MainFaction,
                deck.AllyFaction,
                deck.CardBack,
                deck.DeckCode,
                deck.Favorite,
                deck.Id,
                deck.PlayerId,
                deck.LastPlayed.ToString("o"),
                deck.CreateDate.ToString("o"),
                deck.ModifyDate.ToString("o")
            ));
        });

        app.MapPut("/players/{player_id}/decks/{deck_id}", async (string player_id, int deck_id, [FromBody] DeckAction action, UserStoreService users) =>
        {
            if (!int.TryParse(player_id, out var playerId) || playerId <= 0)
                return Results.BadRequest($"Invalid user ID: {player_id}");
            var user = await users.GetByIdAsync(playerId);
            if (user == null)
                return Results.NotFound($"User with ID {player_id} not found");
            if (user.Decks.TryGetValue(deck_id, out var deck))
            {
                Console.WriteLine(user.Decks[deck_id].Name);
                Console.WriteLine(action.ToString());
                switch (action.Action)
                {
                    case "fill":
                        Console.WriteLine(deck.Name);
                        Console.WriteLine(action.DeckCode);
                        Console.WriteLine(action.ToString());
                        deck.DeckCode = action.DeckCode;
                        deck.ModifyDate = DateTime.Now;
                        break;
                }
                await users.SaveUserAsync(user);
            }

            var savedUser = await users.GetByIdAsync(playerId);
            Console.WriteLine(savedUser?.Decks.GetValueOrDefault(deck_id)?.DeckCode);
            return Results.Ok(new EmptyResponseDto());
        });

        app.MapPut("/players/{player_id}/decks/", async (string player_id, ChangeDeck changeDeck, UserStoreService users) =>
        {
            if (!int.TryParse(player_id, out var playerId) || playerId <= 0)
                return Results.BadRequest($"Invalid user ID: {player_id}");
            var user = await users.GetByIdAsync(playerId);
            if (user == null)
                return Results.NotFound($"User with ID {player_id} not found");
            if (user.Decks.TryGetValue(changeDeck.Id, out var deck))
            {
                switch (changeDeck.Action)
                {
                    case "rename":
                        deck.Name = changeDeck.Name;
                        deck.ModifyDate = DateTime.Now;
                        break;
                    case "change_card_back":
                        deck.CardBack = changeDeck.Name;
                        deck.ModifyDate = DateTime.Now;
                        break;
                    case "make_favorite":
                        user.Name = deck.Name;
                        deck.Favorite = true;
                        deck.ModifyDate = DateTime.Now;
                        break;
                }

                await users.SaveUserAsync(user);
            }

            return Results.Ok(new EmptyResponseDto());
        });

        app.MapDelete("/players/{player_id}/decks/{deck_id}", async (string player_id, int deck_id, UserStoreService users) =>
        {
            if (!int.TryParse(player_id, out var playerId) || playerId <= 0)
                return Results.BadRequest($"Invalid user ID: {player_id}");
            var user = await users.GetByIdAsync(playerId);
            if (user == null)
                return Results.NotFound($"User with ID {player_id} not found");
            user.Decks.Remove(deck_id);
            await users.SaveUserAsync(user);

            return Results.Ok(new EmptyResponseDto());
        });

        // 这个端点获取卡组详情（预制卡组），暂未使用
        app.MapGet("/items/decks/{id}", (string id) => Results.Ok(""));

        return app;
    }
}

