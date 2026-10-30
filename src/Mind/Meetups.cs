using System;
using System.Collections.Generic;
using System.Linq;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;

namespace NivalisAIChat.Mind;

/// <summary>
/// The inner-life round, step 5 (plans that happen): a plan made in a chat to meet someone somewhere becomes a meet-up.
/// When you're there in its window, they come (the code brings them to you; nothing is written into the game's own save,
/// no schedule is changed); someone flaky may not make it. If you never turn up, they were stood up: it hurts, they
/// remember it, and it gets around. Each outcome is kept with the plan (reminders.json, per save). No Unity, tested.
/// </summary>
public static class Meetups
{
    public const string Met = "met", StoodUp = "stood up", Flaked = "flaked";
    /// <summary>How long after the hour they wait for you (and you can still make it), in game minutes; from half an hour before.</summary>
    public const int WaitMinutes = 120, EarlyMinutes = 30;

    /// <summary>A plan with someone at a place (only those are meet-ups; "remind me to pay the rent" isn't).</summary>
    public static bool IsMeetup(ReminderBook.Reminder r) => r.WhoId.Length > 0 && r.Place.Length > 0;

    /// <summary>When they'd be there: from half an hour before the hour to two hours after; a plan with no hour, from 08:00 to the end of its day.</summary>
    public static (long from, long to) Window(ReminderBook.Reminder r)
    {
        long day = (long)r.Day * GameClock.PerDay;
        return r.Hour >= 0 ? (day + r.Hour * 60 - EarlyMinutes, day + r.Hour * 60 + WaitMinutes) : (day + 8 * 60, day + GameClock.PerDay - 1);
    }

    /// <summary>How likely they don't make it, from who they are: the scatterbrained and the dreamy more, the earnest and pragmatic less.</summary>
    public static double FlakeChance(Persona p)
    {
        string t = (p.Temperament + " " + string.Join(" ", p.Personality ?? new List<string>()) + " " + p.Honesty).ToLowerInvariant();
        double c = 0.08;
        if (new[] { "scatter", "dreamy", "distracted", "flaky", "lazy", "chaotic", "forgetful", "unreliable" }.Any(t.Contains)) c = 0.3;
        else if (new[] { "earnest", "pragmatic", "reliable", "punctual", "stoic", "loyal", "kind but exhausted" }.Any(t.Contains)) c = 0.04;
        if (t.Contains("lies easily")) c += 0.08;
        return Math.Clamp(c, 0.02, 0.45);
    }

    /// <summary>Whether they make this one (decided once per plan: the same answer every time it's asked).</summary>
    public static bool TheyCome(ReminderBook.Reminder r, Persona p) => Romance.Roll(r.Id, r.Day, "meetup") >= FlakeChance(p);

    /// <summary>
    /// What a plan's outcome is now, or null while it's still open: met once they came; past the window, stood up if you
    /// never came, flaked if you came and they didn't (or neither did).
    /// </summary>
    public static string? Settle(ReminderBook.Reminder r, long now, bool theyCome)
    {
        if (r.Outcome.Length > 0) return r.Outcome;
        var (_, to) = Window(r);
        if (now <= to) return null;
        if (!theyCome) return Flaked;
        return r.PlayerCame ? Met : StoodUp;
    }

    /// <summary>For their chat: what happened with a plan, in their own head ("" while it's open).</summary>
    public static string Line(ReminderBook.Reminder r, long now)
    {
        string where = r.Place.Length > 0 ? " at " + r.Place : "";
        string when = GameClock.Ago((long)r.Day * GameClock.PerDay + Math.Max(0, r.Hour) * 60, now);
        return r.Outcome switch
        {
            StoodUp => $"You'd agreed to meet them{where} ({when}), and they never showed: you waited. It still stings.",
            Flaked => $"You'd agreed to meet them{where} ({when}) and you didn't make it. You know it; own up or make your excuse, your way.",
            Met => $"You met up with them{where} as you'd planned ({when}).",
            _ => "",
        };
    }
}
