using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisAIChat.Npc;

/// <summary>A dish the game showed them eating: which, where, on which game day. Kept in their record (per save).</summary>
public sealed class MealSeen
{
    public int Day { get; set; }
    public string Dish { get; set; } = "";
    public string Place { get; set; } = "";
}

/// <summary>
/// The inner-life round (the author: "over time they build favorite foods, over time if they eat too much they'd obviously
/// want something else and they wouldn't eat a dish more than three times"): what each resident has eaten, as the game
/// showed it (at any food place, yours too), becomes their taste: a dish they keep coming back to on different days is their
/// favourite; one they've had three times in a week they're sick of for a while and want something else. It's said in their
/// chats, marked on your menu when they look at it (so what they ask you for follows it), and on their mind now and then.
/// The game's own visits and orders are never changed by this. No Unity, tested.
/// </summary>
public static class Tastes
{
    public const int MaxKept = 40;
    /// <summary>Three times within a week and they've had enough of it.</summary>
    public const int SickAfter = 3, SickDays = 7;
    /// <summary>On three different days within a month: a favourite (unless they're sick of it right now).</summary>
    public const int FavouriteAfter = 3, FavouriteDays = 30;

    /// <summary>A dish eaten (once per dish, place and day). False when it's kept already or there's nothing to keep.</summary>
    public static bool Note(NpcRecord rec, string dish, string place, int day)
    {
        string d = Clean(dish), p = Clean(place);
        if (d.Length == 0 || day < 0) return false;
        rec.State.Meals ??= new List<MealSeen>();
        var list = rec.State.Meals;
        if (list.Any(m => m.Day == day && Same(m.Dish, d) && Same(m.Place, p))) return false;
        list.Add(new MealSeen { Day = day, Dish = d, Place = p });
        while (list.Count > MaxKept) list.RemoveAt(0);
        return true;
    }

    /// <summary>Dishes they've had <see cref="SickAfter"/> times or more in the last week (they'd rather have something else).</summary>
    public static List<string> SickOf(NpcRecord rec, int today) =>
        (rec.State.Meals ?? new List<MealSeen>()).Where(m => today - m.Day is >= 0 and < SickDays)
            .GroupBy(m => m.Dish, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= SickAfter).Select(g => g.First().Dish).ToList();

    /// <summary>Their favourite: the dish eaten on the most different days in the last month (at least three), or "".</summary>
    public static string Favourite(NpcRecord rec, int today)
    {
        var best = (rec.State.Meals ?? new List<MealSeen>()).Where(m => today - m.Day is >= 0 and < FavouriteDays)
            .GroupBy(m => m.Dish, StringComparer.OrdinalIgnoreCase)
            .Select(g => (dish: g.First().Dish, days: g.Select(m => m.Day).Distinct().Count(), last: g.Max(m => m.Day)))
            .Where(x => x.days >= FavouriteAfter).OrderByDescending(x => x.days).ThenByDescending(x => x.last).FirstOrDefault();
        return best.dish ?? "";
    }

    /// <summary>For their chat's card: what they love and what they're sick of ("" when there's nothing yet).</summary>
    public static string Line(NpcRecord rec, int today)
    {
        var sick = SickOf(rec, today);
        string fav = Favourite(rec, today);
        bool favSick = fav.Length > 0 && sick.Any(s => Same(s, fav));
        var parts = new List<string>();
        if (sick.Count > 0) parts.Add($"you've had {Listed(sick)} three times this week and you're sick of it; you'd go for something else");
        if (fav.Length > 0 && !favSick) parts.Add($"your favourite lately is the {fav}");
        else if (favSick) parts.Add($"the {fav} is your favourite, but even you need a break from it");
        return parts.Count == 0 ? "" : "Food: " + string.Join("; ", parts) + ".";
    }

    /// <summary>A menu dish as they see it: " (your favourite)", " (you're sick of it this week)", or "".</summary>
    public static string MenuNote(NpcRecord rec, string dish, int today)
    {
        if (SickOf(rec, today).Any(s => Matches(s, dish))) return " (you've had it three times this week: you want something else)";
        string fav = Favourite(rec, today);
        return fav.Length > 0 && Matches(fav, dish) ? " (your favourite)" : "";
    }

    /// <summary>What's on their mind about food, for a mutter now and then: craving a favourite, or sick of something ("" when nothing).</summary>
    public static string Mutter(NpcRecord rec, int today)
    {
        var sick = SickOf(rec, today);
        if (sick.Count > 0) return $"sick of {sick[0]}; something else tonight";
        string fav = Favourite(rec, today);
        return fav.Length > 0 ? $"craving the {fav} again" : "";
    }

    private static bool Matches(string a, string b) => Same(a, b) || NpcBrain.DishMatches(a, b);

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Listed(List<string> dishes) => dishes.Count == 1 ? "the " + dishes[0] : "the " + string.Join(" and the ", dishes.Take(2));

    /// <summary>A dish or place name from the game, one line, no brackets or quotes (it goes back into prompts).</summary>
    private static string Clean(string? s)
    {
        string t = new string((s ?? "").Where(c => c is not ('[' or ']' or '{' or '}' or '<' or '>' or '"' or '\n' or '\r')).ToArray()).Trim();
        return t.Length <= 60 ? t : t[..60].TrimEnd();
    }
}
