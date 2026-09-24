using System.Text.Json.Nodes;
using fyserver.Models;

namespace fyserver.Services;

/// <summary>Implements the existing NestJS player-library and pack-opening game protocol.</summary>
public sealed class PlayerCardService(CardCatalogService catalog)
{
    private static readonly double[] RarityWeights = [0.66, 0.22, 0.08, 0.04];
    private static readonly int[] CollectionLimits = [4, 3, 2, 1];
    private static readonly int[] DustByRarity = [5, 10, 50, 100];
    private const int DustCap = 1000;
    private static readonly string[] Wildcards =
    [
        "card_wildcard_standard", "card_wildcard_limited", "card_wildcard_special", "card_wildcard_elite"
    ];

    public JsonObject OpenPack(User user, int packId)
    {
        user.Packs ??= [];
        var packIndex = user.Packs.FindIndex(pack => pack.Id == packId);
        if (packIndex < 0) throw new CardOperationException("No packs of this type");
        var ownedPack = user.Packs[packIndex];
        var separator = ownedPack.CardSet.IndexOf('|');
        if (separator <= 0 || !int.TryParse(ownedPack.CardSet[..separator], out var cardCount) || cardCount < 1)
            throw new CardOperationException("Invalid pack configuration");
        var setName = ownedPack.CardSet[(separator + 1)..];
        var pool = catalog.GetPackPool(setName, string.Equals(setName, "Core", StringComparison.OrdinalIgnoreCase) || string.Equals(setName, "Basic", StringComparison.OrdinalIgnoreCase));
        if (pool.Values.All(cards => cards.Count == 0)) throw new CardOperationException($"Card set \"{setName}\" not found");

        EnsureUserCards(user);
        // Nest's Map.set semantics preserve the last row when legacy collections contain
        // duplicate card_type entries; ToDictionary would throw and make such accounts unusable.
        var normal = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var gold = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in user.UserCards.Cards)
        {
            normal[card.CardType] = card.Count;
            gold[card.CardType] = card.GoldCardCount;
        }
        var availableCache = new Dictionary<int, List<string>>();
        List<string> Available(int rarity)
        {
            if (availableCache.TryGetValue(rarity, out var cached)) return cached;
            cached = pool[rarity].Where(id => Wildcards.Contains(id, StringComparer.OrdinalIgnoreCase) ||
                normal.GetValueOrDefault(id) + gold.GetValueOrDefault(id) < CollectionLimits[rarity]).ToList();
            availableCache[rarity] = cached;
            return cached;
        }

        var draws = new List<DrawnCard>(cardCount);
        for (var slot = 0; slot < cardCount; slot++)
        {
            var rarity = RollRarity();
            var available = Available(rarity);
            while (available.Count == 0 && rarity > 0) available = Available(--rarity);
            if (available.Count == 0)
            {
                available = pool[rarity];
                while (available.Count == 0 && rarity > 0) available = pool[--rarity];
            }
            if (available.Count == 0) throw new CardOperationException($"No cards available for rarity {rarity} in set {setName}");
            var cardId = available[Random.Shared.Next(available.Count)];
            var isGold = cardCount != 5 && Random.Shared.NextDouble() < 0.15 && gold.GetValueOrDefault(cardId) < CollectionLimits[rarity];
            draws.Add(new DrawnCard(rarity, cardId, isGold, false));
        }

        ApplyGuarantees(draws, cardCount, pool, Available);
        for (var i = 0; i < draws.Count; i++)
        {
            var draw = draws[i];
            if (cardCount != 5 && Random.Shared.NextDouble() < 0.15)
            {
                draws[i] = draw with { CardId = Wildcards[draw.Rarity], IsGold = false, IsWildcard = true };
            }
        }

        user.Dust = Math.Clamp(user.Dust, 0, DustCap);
        var resultCards = new JsonArray();
        var totalDust = 0;
        foreach (var draw in draws)
        {
            var awardedDust = 0;
            if (draw.IsWildcard)
            {
                normal[draw.CardId] = normal.GetValueOrDefault(draw.CardId) + 1;
            }
            else
            {
                var normalCount = normal.GetValueOrDefault(draw.CardId);
                var goldCount = gold.GetValueOrDefault(draw.CardId);
                if (normalCount + goldCount < CollectionLimits[draw.Rarity])
                {
                    if (draw.IsGold && goldCount < CollectionLimits[draw.Rarity]) gold[draw.CardId] = goldCount + 1;
                    else if (normalCount < CollectionLimits[draw.Rarity]) normal[draw.CardId] = normalCount + 1;
                    else awardedDust = AddDust(user, DustByRarity[draw.Rarity]);
                }
                else awardedDust = AddDust(user, DustByRarity[draw.Rarity]);
                totalDust += awardedDust;
            }
            resultCards.Add(new JsonObject
            {
                ["card_name"] = draw.CardId,
                ["dust"] = awardedDust,
                ["is_gold_card"] = draw.IsGold,
                ["new_card"] = false,
                ["recycled"] = false
            });
        }

        user.UserCards.Cards = normal.Keys.Concat(gold.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => new UserCard(id, normal.GetValueOrDefault(id), Wildcards.Contains(id, StringComparer.OrdinalIgnoreCase) ? 0 : gold.GetValueOrDefault(id), 0, 0))
            .Where(card => card.Count > 0 || card.GoldCardCount > 0)
            .ToList();
        user.Packs.RemoveAt(packIndex);
        return new JsonObject { ["cards"] = resultCards, ["total_dust"] = totalDust };
    }

    public JsonObject CraftFromWildcard(User user, string targetId, string wildcardId)
    {
        if (string.IsNullOrWhiteSpace(targetId) || string.IsNullOrWhiteSpace(wildcardId) || targetId == wildcardId)
            throw new CardOperationException("Invalid wildcard craft value");
        var target = catalog.GetCard(targetId);
        var wildcard = catalog.GetCard(wildcardId);
        var targetRarity = GetString(target, "rarity");
        if (target == null || wildcard == null || string.Equals(GetString(target, "type"), "wildcard", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(GetString(target, "type"), "location", StringComparison.OrdinalIgnoreCase) ||
            targetId.StartsWith("card_location", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(GetString(wildcard, "type"), "wildcard", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(targetRarity) || !string.Equals(targetRarity, GetString(wildcard, "rarity"), StringComparison.Ordinal))
            throw new CardOperationException("Invalid wildcard craft cards");

        var wildcardFaction = GetString(wildcard, "faction");
        if (!string.IsNullOrWhiteSpace(wildcardFaction) && !string.Equals(wildcardFaction, GetString(target, "faction"), StringComparison.Ordinal))
            throw new CardOperationException("Wildcard faction mismatch");
        EnsureUserCards(user);
        var wildcardEntry = user.UserCards.Cards.FirstOrDefault(card => string.Equals(card.CardType, wildcardId, StringComparison.OrdinalIgnoreCase));
        if (wildcardEntry == null || wildcardEntry.Count < 1) throw new CardOperationException("Wildcard not owned");
        var limits = new Dictionary<string, int>(StringComparer.Ordinal) { ["Common"] = 4, ["Uncommon"] = 3, ["Rare"] = 2, ["Unique"] = 1 };
        var targetEntry = user.UserCards.Cards.FirstOrDefault(card => string.Equals(card.CardType, targetId, StringComparison.OrdinalIgnoreCase));
        if (limits.TryGetValue(targetRarity, out var limit) && (targetEntry?.Count ?? 0) >= limit)
            throw new CardOperationException("Card collection limit reached");

        wildcardEntry.Count--;
        if (targetEntry == null)
        {
            targetEntry = new UserCard(targetId, 0, 0, 0, 0);
            user.UserCards.Cards.Add(targetEntry);
        }
        targetEntry.Count++;
        user.UserCards.Cards = user.UserCards.Cards.Where(card => card.Count > 0 || card.GoldCardCount > 0).ToList();
        return new JsonObject { ["success"] = true };
    }

    private static void ApplyGuarantees(List<DrawnCard> cards, int cardCount, Dictionary<int, List<string>> pool, Func<int, List<string>> available)
    {
        if (cardCount == 5 && cards.All(card => card.Rarity == 0))
        {
            var index = Random.Shared.Next(cards.Count);
            var rarity = pool[1].Count > 0 ? 1 : 0;
            var choices = available(rarity).Count > 0 ? available(rarity) : pool[rarity];
            if (choices.Count > 0) cards[index] = cards[index] with { Rarity = rarity, CardId = choices[Random.Shared.Next(choices.Count)] };
        }
        else if (cardCount == 7)
        {
            var rare = cards.FindIndex(card => card.Rarity >= 2);
            if (rare < 0)
            {
                var candidates = Enumerable.Range(0, cards.Count).Where(index => cards[index].Rarity < 2).ToArray();
                if (candidates.Length > 0)
                {
                    var index = candidates[Random.Shared.Next(candidates.Length)];
                    var rarity = pool[2].Count > 0 ? 2 : pool[1].Count > 0 ? 1 : 0;
                    var choices = available(rarity).Count > 0 ? available(rarity) : pool[rarity];
                    if (choices.Count > 0) cards[index] = cards[index] with { Rarity = rarity, CardId = choices[Random.Shared.Next(choices.Count)] };
                    rare = cards.FindIndex(card => card.Rarity >= 2);
                }
            }
            var uncommonOthers = cards.Count(card => card.Rarity >= 1) - (rare >= 0 ? 1 : 0);
            if (uncommonOthers < 2)
            {
                var indices = Enumerable.Range(0, cards.Count).Where(index => index != rare && cards[index].Rarity == 0)
                    .OrderBy(_ => Random.Shared.Next()).Take(2 - uncommonOthers).ToArray();
                foreach (var index in indices)
                {
                    var rarity = pool[1].Count > 0 ? 1 : 0;
                    var choices = available(rarity).Count > 0 ? available(rarity) : pool[rarity];
                    if (choices.Count > 0) cards[index] = cards[index] with { Rarity = rarity, CardId = choices[Random.Shared.Next(choices.Count)] };
                }
            }
        }
    }

    private static int RollRarity()
    {
        var roll = Random.Shared.NextDouble();
        var cumulative = 0d;
        for (var rarity = 3; rarity >= 0; rarity--)
        {
            cumulative += RarityWeights[rarity];
            if (roll <= cumulative) return rarity;
        }
        return 0;
    }

    private static int AddDust(User user, int amount)
    {
        var current = Math.Clamp(user.Dust, 0, DustCap);
        var awarded = Math.Min(amount, DustCap - current);
        user.Dust = current + awarded;
        return awarded;
    }

    private static void EnsureUserCards(User user)
    {
        user.UserCards ??= new UserCardCollection();
        user.UserCards.Cards ??= [];
    }

    private static string? GetString(JsonObject? row, string key) => row?[key] is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
    private sealed record DrawnCard(int Rarity, string CardId, bool IsGold, bool IsWildcard);
}

public sealed class CardOperationException(string message) : Exception(message);
