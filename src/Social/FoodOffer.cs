using System;
using System.Collections.Generic;
using System.Linq;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Social;

/// <summary>What handing someone a dish is worth: the price they pay (or nothing, for a gift) and why.</summary>
public sealed class FoodOffer
{
    private static readonly System.Text.RegularExpressions.Regex Giving = new(@"\b(give|gives|giving|gave|hand|hands|handing|handed)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// CF-20 (a player: "How to give food? I put (give) in dialogue but that's just roleplay"): the player's words say they're
    /// giving something (a whole word: "forgive" and "handsome" don't count).
    /// </summary>
    public static bool MentionsGiving(string? text) => !string.IsNullOrEmpty(text) && Giving.IsMatch(text);

    /// <summary>They asked for this dish, so they pay.</summary>
    public bool Paid;
    public int Price;
    /// <summary>e.g. "20% friend discount, 5 delivery".</summary>
    public string Note = "";
    /// <summary>Brought to them rather than served at the player's own place.</summary>
    public bool Delivered;

    public string Label => Paid ? $"{Price} Lims" : "gift";

    /// <summary>
    /// Only food they asked for is paid for: the menu price (or the dish's base price), less the friend discount when they
    /// like the player enough, plus the delivery fee unless they're at the player's own place (as a customer or staff).
    /// Anything else is a gift.
    /// </summary>
    /// <param name="discountAt">Where on the relationship bar (0–100%) they get the friend discount (a setting).</param>
    public static FoodOffer For(NpcRecord rec, GameSnapshot snap, string dish, int basePrice, int discountPercent, double discountAt, int deliveryFee)
    {
        var offer = new FoodOffer { Delivered = !(snap.NpcVisitingPlayerVenue || snap.NpcWorksForPlayer) };
        var fav = NpcBrain.MatchingFavour(rec, dish);
        if (fav == null) return offer;
        offer.Paid = true;
        int price = fav.Price > 0 ? fav.Price
            : snap.Menu.FirstOrDefault(m => NpcBrain.DishMatches(m.Dish, dish))?.Price ?? Math.Max(0, basePrice);
        var notes = new List<string>();
        int pct = Math.Clamp(discountPercent, 0, 100);
        if (pct > 0 && Features.Relationships && rec.State.Relationship >= discountAt) // FB-45: no friends' discount while relationships are off
        {
            price = (int)Math.Round(price * (100 - pct) / 100.0, MidpointRounding.AwayFromZero);
            notes.Add($"{pct}% friend discount");
        }
        if (offer.Delivered && deliveryFee > 0)
        {
            price += deliveryFee;
            notes.Add($"{deliveryFee} delivery");
        }
        offer.Price = Math.Max(0, price);
        offer.Note = string.Join(", ", notes);
        return offer;
    }
}
