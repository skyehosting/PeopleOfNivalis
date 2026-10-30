using System;
using System.Collections.Generic;

namespace NivalisAIChat.Phone;

/// <summary>
/// Nightline's rolls (FB-71), with no Unity and no AI: when someone next opens the app, whether they post, scroll or both,
/// and which lines they see on the way down (the author: "if scrolling it will start with the newest post, with a 50%
/// chance to react and a 50% chance to comment ... the next one would have a 50% chance as well, and the lower it got the
/// less chance 'to be seen'"). The AI then decides what they actually do with what they saw. Tested.
/// </summary>
public static class LinePlan
{
    public const int MaxWeight = 1000, MaxSeenInOneGo = 8;

    /// <summary>Game minutes until someone next opens the app: random between the two (5 to 1440, in either order).</summary>
    public static long NextGap(Random rng, int minMinutes, int maxMinutes)
    {
        int a = Math.Clamp(minMinutes, 5, 1440), b = Math.Clamp(maxMinutes, 5, 1440);
        if (a > b) (a, b) = (b, a);
        return a + rng.Next(b - a + 1);
    }

    /// <summary>Post, scroll, or both, by the three weights (0 to 1000 each); all 0: neither.</summary>
    public static (bool post, bool scroll) Choose(Random rng, int postWeight, int scrollWeight, int bothWeight)
    {
        int p = Math.Clamp(postWeight, 0, MaxWeight), s = Math.Clamp(scrollWeight, 0, MaxWeight), b = Math.Clamp(bothWeight, 0, MaxWeight);
        int total = p + s + b;
        if (total == 0) return (false, false);
        int roll = rng.Next(total);
        if (roll < p) return (true, false);
        if (roll < p + s) return (false, true);
        return (true, true);
    }

    /// <summary>One line they come to while scrolling: whether they may react and may comment (the AI decides how, or not).</summary>
    public readonly record struct Look(string Id, bool React, bool Comment);

    /// <summary>
    /// Scrolling from the newest down. The first line they come to is always seen; each one further down is seen
    /// <paramref name="falloffPct"/>% as often as the one before, and they stop at the first they'd scroll past unseen (their
    /// attention's gone) or after <see cref="MaxSeenInOneGo"/>. Their own lines and lines they've already seen are passed
    /// over (they don't count against them). Each seen line gets its own react and comment rolls.
    /// </summary>
    public static List<Look> Scroll(Random rng, IReadOnlyList<(string id, string author)> newestFirst, string viewer, Func<string, bool> alreadySeen,
        int reactPct, int commentPct, int falloffPct)
    {
        var looks = new List<Look>();
        double react = Math.Clamp(reactPct, 0, 100) / 100.0, comment = Math.Clamp(commentPct, 0, 100) / 100.0, fall = Math.Clamp(falloffPct, 0, 100) / 100.0;
        double chance = 1.0;
        foreach (var (id, author) in newestFirst)
        {
            if (looks.Count >= MaxSeenInOneGo) break;
            if (author == viewer || alreadySeen(id)) continue;
            if (rng.NextDouble() >= chance) break;
            looks.Add(new Look(id, rng.NextDouble() < react, rng.NextDouble() < comment));
            chance *= fall;
        }
        return looks;
    }

    /// <summary>
    /// Whether they go and look at who reacted to their own line this time (the author: "they won't always check who liked
    /// their post, this would depend if the npc wants to"): the setting's chance, raised for someone who cares what others
    /// think (care 0..1, from who they are), lowered for someone who doesn't.
    /// </summary>
    public static bool LooksAtReactions(Random rng, int checkPct, double care)
    {
        // 0.2x for someone who doesn't care at all, 1x at 0.5, 1.8x for someone who cares a lot.
        double c = Math.Clamp(checkPct, 0, 100) / 100.0 * (0.2 + 1.6 * Math.Clamp(double.IsFinite(care) ? care : 0.5, 0, 1));
        return rng.NextDouble() < Math.Min(1.0, c);
    }

    /// <summary>
    /// How much someone cares what others think, from words in who they are (vain, insecure, attention, influencer...
    /// raise it; aloof, private, doesn't care... lower it). 0.5 when nothing says.
    /// </summary>
    public static double Care(IEnumerable<string> traits)
    {
        double c = 0.5;
        foreach (var raw in traits)
        {
            string t = (raw ?? "").ToLowerInvariant();
            if (t.Contains("vain") || t.Contains("insecure") || t.Contains("attention") || t.Contains("influencer") || t.Contains("needy") || t.Contains("people-pleaser") || t.Contains("approval")) c += 0.2;
            if (t.Contains("aloof") || t.Contains("private") || t.Contains("doesn't care") || t.Contains("indifferent") || t.Contains("stoic") || t.Contains("loner")) c -= 0.2;
        }
        return Math.Clamp(c, 0, 1);
    }

    /// <summary>How they feel about whoever posted it, in words for the AI (relationship 0..100, 50 neutral; -1 never met).</summary>
    public static string Feeling(int relationship) => relationship switch
    {
        < 0 => "you don't know them",
        < 20 => "you can't stand them",
        < 35 => "you don't like them",
        < 45 => "you're cool on them",
        <= 55 => "you're neutral about them",
        < 70 => "you like them",
        < 85 => "you're fond of them",
        _ => "they're one of your favourite people",
    };
}
