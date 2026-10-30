using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisAIChat.Social;

/// <summary>
/// The market's shortages and surpluses as people experience them ("can you believe the price of bread?"). Each one is
/// known or not person by person (the shortage everyone's complaining about more often than a quiet surplus), and how
/// much they know varies: the actual numbers, the gist, or just something they heard. Fewer still have it on their
/// mind, so it surfaces now and then rather than in every conversation. People who work with food notice more.
/// </summary>
public static class MarketTalk
{
    public sealed class Note
    {
        /// <summary>"shortage" or "surplus".</summary>
        public string Kind = "";
        public string Goods = "";
        /// <summary>How far prices are from normal, in percent (+40 = 40% dearer).</summary>
        public int PricePercent;
        public int SinceDays;
    }

    /// <summary>The note as this person knows it.</summary>
    public static string Describe(Note n, FoodScene.Detail detail = FoodScene.Detail.Precise)
    {
        string goods = n.Goods.Trim().ToLowerInvariant();
        bool shortage = n.Kind == "shortage";
        switch (detail)
        {
            case FoodScene.Detail.Vague:
                return shortage ? $"You've heard {goods} is hard to get at the market lately." : $"You've heard there's loads of {goods} about at the market.";
            case FoodScene.Detail.Rough:
                string how = Math.Abs(n.PricePercent) >= 25 ? "a lot" : "a bit";
                return shortage
                    ? $"There's a {goods} shortage at the market; it's gone up {how}. You don't know the exact prices."
                    : $"{Cap(goods)} is going cheap at the market right now. You don't know the exact prices.";
            default:
                string since = n.SinceDays <= 0 ? "it started today" : n.SinceDays == 1 ? "since yesterday" : $"for {n.SinceDays} days now";
                string price = n.PricePercent > 0 ? $", prices up about {n.PricePercent}%" : n.PricePercent < 0 ? $", prices down about {-n.PricePercent}%" : "";
                return shortage
                    ? $"There's a {goods} shortage at the market ({since}{price})."
                    : $"There's more {goods} at the market than anyone can sell ({since}{price}).";
        }
    }

    /// <summary>
    /// Which of today's notes this person knows, and whether it's on their mind. Each note is its own roll (a surplus is
    /// noticed less than a shortage), scaled by who they are (<paramref name="factor"/>, see <see cref="Factor"/>). The
    /// same all day for the same person, so they don't change their tune between two chats.
    /// </summary>
    public static (List<Note> known, bool onMind) Roll(string npcId, int day, IReadOnlyList<Note> notes, double factor, double knowPercent, double onMindPercent)
    {
        double know = Math.Clamp(knowPercent / 100.0 * factor, 0, 0.97);
        double Chance(Note n) => know * (n.Kind == "shortage" ? 1.0 : 0.6);
        var known = notes.Where(n => FoodScene.Unit(FoodScene.Hash($"{npcId}|{day}|{n.Kind}|{n.Goods}")) < Chance(n)).ToList();
        // On their mind only if they know something; scaled so the overall rate matches the setting.
        double knowsAny = 1 - notes.Aggregate(1.0, (p, n) => p * (1 - Chance(n)));
        double mind = Math.Clamp(onMindPercent / 100.0 * factor, 0, 0.9);
        bool onMind = known.Count > 0 && FoodScene.Unit(FoodScene.Hash($"{npcId}|{day}|mind")) < mind / Math.Max(knowsAny, 0.01);
        return (known, onMind);
    }

    private static readonly string[] Keen = { "nosy", "gossip", "pragmatic", "blunt", "hustle", "competitive", "know-it-all", "smug", "earnest", "anxious" };
    private static readonly string[] Absent = { "dreamy", "distracted", "scatter", "philosoph", "melanchol", "laid-back", "unbothered", "world-weary" };

    /// <summary>How likely this kind of person is to follow prices: food work doubles it; nosy, practical types notice more,
    /// dreamers and the unbothered less.</summary>
    public static double Factor(bool worksWithFood, string temperament)
    {
        double f = worksWithFood ? 2.0 : 1.0;
        string t = temperament ?? "";
        if (Keen.Any(k => t.Contains(k, StringComparison.OrdinalIgnoreCase))) f *= 1.3;
        if (Absent.Any(k => t.Contains(k, StringComparison.OrdinalIgnoreCase))) f *= 0.6;
        return f;
    }

    /// <summary>For someone who hasn't heard: they don't know, even if asked straight out.</summary>
    public static readonly string DoesNotKnow =
        "You haven't been following market prices lately. If they bring up shortages, surpluses or what things cost at the market, you don't know " +
        "the details (you might shrug, guess, or ask) — don't pretend to know, and don't just take their word for it.";

    // Whole job words only: "noodle-stall cook", "bartender", "fish-market gutter" — not "barber" or "embarrassed".
    private static readonly System.Text.RegularExpressions.Regex FoodWork = new(
        @"(?i)\b(cooks?|cooking|chefs?|kitchen|noodles?|ramen|food|stall|vendors?|servers?|serving|waiters?|waitress|barista|bartender|bar\s+staff|" +
        @"bakers?|bakery|butchers?|fish(?:monger|erman|ermen|-market)?|trawler|deckhand|grocer|grocery|diner|restaurant|caf[eé]|farmers?|greenhouse|market\s+(?:trader|stall))\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Their job, workplace or roles involve food.</summary>
    public static bool WorksWithFood(params string?[] about) =>
        about.Where(s => !string.IsNullOrWhiteSpace(s)).Any(s => FoodWork.IsMatch(s!));

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
