using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisAIChat.Npc;

/// <summary>Something the game showed them doing (FB-36): a meal at a food place, or a shift at work. Saved in their record.</summary>
public sealed class Doing
{
    public int GameDay { get; set; }
    /// <summary><see cref="Doings.Meal"/> or <see cref="Doings.Work"/>.</summary>
    public string Kind { get; set; } = "";
    public string Place { get; set; } = "";
    /// <summary>The dish (a meal) or their role (work); "" when the game didn't say.</summary>
    public string What { get; set; } = "";
    /// <summary>As they'd remember it: "Had the noodle soup at Izakaya Stop.", "On shift at Ancient Spirit (cook)."</summary>
    public string Text { get; set; } = "";
}

/// <summary>
/// FB-36: what residents did in the game, not only what they said, for people who already have a record. Kept apart from
/// their life updates (so those are never pushed out), small and merged: one meal per place a day (the dish filled in when it
/// turns up), one line per workplace (moved to the latest day). No Unity here, so it can be tested.
/// </summary>
public static class Doings
{
    public const string Meal = "meal", Work = "work";
    /// <summary>Most meals kept (the newest), and most workplaces.</summary>
    public const int MaxMeals = 4, MaxWork = 3;

    public static string MealLine(string place, string dish) => dish.Length > 0 ? $"Had {dish} at {place}." : $"Ate at {place}.";

    public static string WorkLine(string place, string role) => $"On shift at {place}{(role.Length > 0 ? $" ({role})" : "")}.";

    /// <summary>
    /// Notes it in their record. False when there's nothing new: that meal is already kept (with its dish, or the dish is
    /// still unknown), that shift is already today's, or the meal is kept already as their order at that place (FB-34).
    /// </summary>
    public static bool Note(NpcRecord rec, Doing d)
    {
        if (d.Place.Length == 0 || d.Kind is not (Meal or Work)) return false;
        rec.Doings ??= new List<Doing>();
        var list = rec.Doings;
        string text = Ai.ReplyParser.Quotable(d.Kind == Meal ? MealLine(d.Place, d.What) : WorkLine(d.Place, d.What), 300);
        if (d.Kind == Meal)
        {
            var same = list.FirstOrDefault(x => x.Kind == Meal && x.GameDay == d.GameDay && Same(x.Place, d.Place));
            if (same != null)
            {
                if (same.What.Length > 0 || d.What.Length == 0) return false;
                same.What = d.What; // "Ate at" becomes "Had <dish> at"
                same.Text = text;
                return true;
            }
            if ((rec.Talks ?? new List<Talk>()).Any(t => t.Kind == Talk.Service && t.GameDay == d.GameDay && Same(t.Where, d.Place) && !t.Summary.Contains("left before ordering")))
                return false; // their order there is kept already, in their own words
            list.Add(new Doing { GameDay = d.GameDay, Kind = Meal, Place = d.Place, What = d.What, Text = text });
            Trim(list, Meal, MaxMeals);
            return true;
        }
        var job = list.FirstOrDefault(x => x.Kind == Work && Same(x.Place, d.Place));
        if (job != null)
        {
            if (job.GameDay >= d.GameDay && (job.What == d.What || d.What.Length == 0)) return false;
            job.GameDay = Math.Max(job.GameDay, d.GameDay);
            if (d.What.Length > 0) job.What = d.What;
            job.Text = Ai.ReplyParser.Quotable(WorkLine(job.Place, job.What), 300);
            return true;
        }
        list.Add(new Doing { GameDay = d.GameDay, Kind = Work, Place = d.Place, What = d.What, Text = text });
        Trim(list, Work, MaxWork);
        return true;
    }

    /// <summary>Their newest doings, oldest first (for the chat's [YOUR LIFE LATELY]).</summary>
    public static List<Doing> Recent(NpcRecord rec, int count) =>
        (rec.Doings ?? new List<Doing>()).Where(d => d.Text.Length > 0).OrderBy(d => d.GameDay).TakeLast(count).ToList();

    private static void Trim(List<Doing> list, string kind, int max)
    {
        while (list.Count(x => x.Kind == kind) > max)
            list.Remove(list.Where(x => x.Kind == kind).OrderBy(x => x.GameDay).First());
    }

    private static bool Same(string a, string b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}
