using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisAIChat.Npc;

/// <summary>What the game showed of one of the player's employees at one look (FB-58). No Unity here.</summary>
public sealed class WorkReading
{
    public int Day, Hour;
    /// <summary>Where they work (the venue's name).</summary>
    public string Place = "";
    /// <summary>The game's own job satisfaction: "Happy", "Normal" or "Sad" ("" when it couldn't be read).</summary>
    public string Happiness = "";
    /// <summary>The game's reasons for it: above 0 the pay (or the work) pleases them, below 0 it doesn't.</summary>
    public int WageInfluence, SkillInfluence;
    /// <summary>Lims a shift; 0 when unknown.</summary>
    public int Wage;
    /// <summary>The usual wage for the job (the game's own range); 0 when unknown.</summary>
    public int BaseMin, BaseMax;
    /// <summary>Their shift, as hours of the day (an end before the start runs past midnight); -1 when unknown.</summary>
    public float ShiftStart = -1, ShiftEnd = -1;
    /// <summary>The shift length they'd like, in hours; 0 when unknown.</summary>
    public int PreferredHours;
    /// <summary>Customers at their place right now; -1 when unknown.</summary>
    public int Customers = -1;
    /// <summary>
    /// CF-14: the player's other staff at the same place (id, and a name). Read with no name; only once settled
    /// (<see cref="CoworkerNamesSettled"/>) does one get a name: someone the mod has a record for (an adult, as the minors
    /// rule needs), by the name the mod knows them by. Anyone else is counted, never named.
    /// </summary>
    public List<(string Id, string Name)> Coworkers = new();
    /// <summary>The place's staff list was read (only then is "nobody else works here" a fact).</summary>
    public bool CoworkersRead;
    /// <summary>The coworkers' names as the mod knows them (from their records), once worked out off the game's thread.</summary>
    public bool CoworkerNamesSettled;
}

/// <summary>What the mod last saw of an employee's job, to notice what changed (saved in their record).</summary>
public sealed class WorkLifeState
{
    public int LastWage { get; set; }
    public string LastHappiness { get; set; } = "";
    public bool OnShift { get; set; }
    public int ShiftHours { get; set; }
    public int BusyHours { get; set; }
    /// <summary>The last game hour counted (day * 24 + hour), so one hour is counted once.</summary>
    public int LastHourStamp { get; set; } = -1;
    public int LastWageEventDay { get; set; } = -1;
    /// <summary>
    /// CF-14 (Nexus: an employee hired that day "kept trying to gaslight me into saying she's been working for me for years"):
    /// the game day they started working for the player as far as the mod knows: the day it first saw them on the staff
    /// (or their oldest work memory), or the day it saw them hired. -1 = not known yet.
    /// </summary>
    public int SinceDay { get; set; } = -1;
    /// <summary>True when the mod saw the hire itself (they weren't on the staff, then they were): the day is exact.</summary>
    public bool SinceExact { get; set; }
}

/// <summary>Something that happened at work they remember (saved in their record).</summary>
public sealed class WorkEvent
{
    public int GameDay { get; set; }
    public string Kind { get; set; } = "";
    /// <summary>As they'd remember it, to the player who is their boss: "Your boss cut your pay from 50 to 40 Lims a shift."</summary>
    public string Text { get; set; } = "";
    /// <summary>How far it moved how they feel about the player (the 0-100 bar).</summary>
    public double Mood { get; set; }
}

/// <summary>
/// FB-58: the player's employees and their job: how the game says they feel about it (its own job satisfaction, from pay
/// and skill), their pay against the usual for the job, their hours, and what happened at work (a raise or a cut, growing
/// unhappy, long or packed shifts). The events are remembered and gently move how they feel about the player (the
/// author's choice); the game's own numbers are never written.
/// </summary>
public static class WorkLife
{
    public const string Raise = "raise", Cut = "cut", Unhappy = "unhappy", Better = "better", LongShift = "long shift",
        Packed = "packed", WornOut = "worn out";
    /// <summary>Events kept per person (the newest).</summary>
    public const int MaxEvents = 6;
    /// <summary>Customers at once that make an hour busy, and busy hours that make a shift packed.</summary>
    public const int BusyCustomers = 6, PackedHours = 3;
    /// <summary>A shift this long is long (or 2 hours past the length they'd like, if longer).</summary>
    public const int LongHours = 9, PastPreferred = 2;
    /// <summary>How far each kind moves the bar (one reply moves it at most 0.5 by default).</summary>
    public static double MoodOf(string kind) => kind switch
    {
        Raise => 1.0, Cut => -1.5, Unhappy => -0.5, Better => 0.5, LongShift => -0.2, Packed => -0.1, WornOut => -0.4, _ => 0,
    };

    /// <summary>The state in one string, to tell whether a look changed it.</summary>
    public static string Fingerprint(WorkLifeState st) =>
        $"{st.LastWage}|{st.LastHappiness}|{st.OnShift}|{st.ShiftHours}|{st.BusyHours}|{st.LastWageEventDay}|{st.SinceDay}|{st.SinceExact}";

    /// <summary>
    /// CF-14: since when they've worked for the player, worked out once. Seen hired just now (<paramref name="hiredNow"/>):
    /// that day, exactly. Otherwise the earliest of today and their oldest work memory, as "at the latest".
    /// </summary>
    public static void NoteSince(WorkLifeState st, IReadOnlyList<WorkEvent>? events, int today, bool hiredNow)
    {
        if (today < 0) return;
        if (st.SinceDay >= 0) return; // review CF14-1: once known, a gap in the staff list (or a re-hire) doesn't move it
        if (hiredNow) { st.SinceDay = today; st.SinceExact = true; return; }
        int oldest = (events ?? Array.Empty<WorkEvent>()).Where(e => e.GameDay >= 0).Select(e => e.GameDay).DefaultIfEmpty(today).Min();
        st.SinceDay = Math.Min(today, oldest);
        st.SinceExact = false;
    }

    /// <summary>
    /// CF-14: who else works there, as a fact (the same answer whoever you ask). Names only once settled (see
    /// <see cref="WorkReading.Coworkers"/>); before that, or for someone without a name, they're counted.
    /// </summary>
    public static string Coworkers(WorkReading now)
    {
        int all = now.Coworkers.Count;
        if (all == 0) return "You're their only staff member here (a fact).";
        var named = now.CoworkerNamesSettled
            ? now.Coworkers.Select(c => c.Name).Where(n => n.Length > 0).Distinct().ToList()
            : new List<string>();
        int others = all - named.Count;
        if (named.Count == 0) return all == 1 ? "One other person works here for them too (a fact)." : $"{all} others work here for them too (a fact).";
        return $"Who else works here for them (a fact): {string.Join(", ", named)}" +
               (others > 0 ? $", and {others} more you know less well." : ". Nobody else.");
    }

    /// <summary>Their time here in words, for the chat: exact when the hire was seen, "at the latest" otherwise.</summary>
    public static string Since(WorkLifeState? st, int today)
    {
        if (st == null || st.SinceDay < 0 || today < 0) return "";
        int days = Math.Max(0, today - st.SinceDay);
        string ago = days == 0 ? "today" : days == 1 ? "yesterday" : $"{days} days ago";
        return st.SinceExact
            ? $"You started working here for them on day {st.SinceDay} ({ago}){(days == 0 ? ": this is your first day" : "")}. Never claim to have worked here longer."
            : $"You've worked here for them at least since day {st.SinceDay} ({ago}); you can't say exactly when you started, so don't name a length of time you don't know.";
    }

    /// <summary>Whether <paramref name="hour"/> falls in the shift (an end before the start runs past midnight).</summary>
    public static bool InShift(float start, float end, int hour)
    {
        if (start < 0 || end < 0 || start == end) return false;
        float h = hour + 0.5f;
        return start < end ? h >= start && h < end : h >= start || h < end;
    }

    /// <summary>The shift's length in hours (past midnight too); 0 when unknown.</summary>
    public static float ShiftLength(float start, float end) => start < 0 || end < 0 || start == end ? 0 : start < end ? end - start : 24 - start + end;

    /// <summary>
    /// One look: what changed since the last (kept in <paramref name="st"/>). The first look only takes note. Wage and
    /// happiness are compared whenever they're read; shifts are counted once per game hour.
    /// </summary>
    public static List<WorkEvent> Step(WorkLifeState st, WorkReading r)
    {
        var events = new List<WorkEvent>();
        void Add(string kind, string text) => events.Add(new WorkEvent { GameDay = r.Day, Kind = kind, Text = text, Mood = MoodOf(kind) });
        bool first = st.LastWage == 0 && st.LastHappiness.Length == 0 && st.LastHourStamp < 0;
        string at = r.Place.Length > 0 ? $" at {r.Place}" : "";

        // A raise or a cut: a real change (more than 1 Lim), at most one a game day (review F6: a wage the game moves by itself)
        if (!first && r.Wage > 0 && st.LastWage > 0 && Math.Abs(r.Wage - st.LastWage) > 1 && st.LastWageEventDay != r.Day)
        {
            Add(r.Wage > st.LastWage ? Raise : Cut,
                $"Your boss {(r.Wage > st.LastWage ? "raised" : "cut")} your pay from {st.LastWage} to {r.Wage} Lims a shift.");
            st.LastWageEventDay = r.Day;
        }
        if (!first && r.Happiness.Length > 0 && st.LastHappiness.Length > 0 && r.Happiness != st.LastHappiness)
        {
            if (r.Happiness == "Sad") Add(Unhappy, $"You've grown unhappy with the job{at}: {Why(r, unhappy: true)}.");
            else if (st.LastHappiness == "Sad" || r.Happiness == "Happy") Add(Better, $"The job{at} feels better lately: {Why(r, unhappy: false)}.");
        }

        int stamp = r.Day * 24 + r.Hour;
        if (r.Day >= 0 && stamp != st.LastHourStamp && r.ShiftStart >= 0)
        {
            st.LastHourStamp = stamp;
            bool on = InShift(r.ShiftStart, r.ShiftEnd, r.Hour);
            if (on)
            {
                if (!st.OnShift) { st.OnShift = true; st.ShiftHours = 0; st.BusyHours = 0; }
                st.ShiftHours++;
                if (r.Customers >= BusyCustomers) st.BusyHours++;
            }
            else if (st.OnShift)
            {
                st.OnShift = false;
                int longAt = Math.Max(LongHours, r.PreferredHours > 0 ? r.PreferredHours + PastPreferred : 0);
                bool isLong = st.ShiftHours >= longAt, packed = st.BusyHours >= PackedHours;
                if (isLong && packed) Add(WornOut, $"A long, packed shift{at} ({st.ShiftHours} hours): you went home worn out.");
                else if (isLong) Add(LongShift, $"A long shift{at}: {st.ShiftHours} hours.");
                else if (packed) Add(Packed, $"A packed shift{at}, customers all the time.");
                st.ShiftHours = st.BusyHours = 0;
            }
        }
        else if (first) st.LastHourStamp = stamp;

        if (r.Wage > 0) st.LastWage = r.Wage;
        if (r.Happiness.Length > 0) st.LastHappiness = r.Happiness;
        return events;
    }

    private static string Why(WorkReading r, bool unhappy)
    {
        var why = new List<string>();
        if (unhappy)
        {
            if (r.WageInfluence < 0) why.Add("the pay isn't enough");
            if (r.SkillInfluence < 0) why.Add("the work doesn't use what you're good at");
            return why.Count > 0 ? string.Join(", and ", why) : "it's wearing on you";
        }
        if (r.WageInfluence > 0) why.Add("the pay is good");
        if (r.SkillInfluence > 0) why.Add("the work suits you");
        return why.Count > 0 ? string.Join(", and ", why) : "things have settled";
    }

    /// <summary>Kept in their record (the newest <see cref="MaxEvents"/>), and how they feel about the player moved, gently.</summary>
    public static void Remember(NpcRecord rec, WorkEvent e)
    {
        rec.WorkEvents ??= new List<WorkEvent>();
        rec.WorkEvents.Add(e);
        while (rec.WorkEvents.Count > MaxEvents) rec.WorkEvents.RemoveAt(0);
        if (Features.Relationships && e.Mood != 0) rec.State.Shift(e.Mood);
    }

    /// <summary>Their pay in words, against the usual for the job when the game's range makes sense.</summary>
    public static string Pay(WorkReading r)
    {
        if (r.Wage <= 0) return "";
        string pay = $"{r.Wage} Lims a shift";
        bool range = r.BaseMin > 0 && r.BaseMax >= r.BaseMin && r.Wage * 4 >= r.BaseMin && r.Wage <= r.BaseMax * 4;
        if (!range) return pay;
        string usual = r.BaseMin == r.BaseMax ? $"{r.BaseMin}" : $"{r.BaseMin}-{r.BaseMax}";
        return r.Wage < r.BaseMin ? $"{pay}, less than the usual {usual} for this job"
             : r.Wage > r.BaseMax ? $"{pay}, more than the usual {usual} for this job"
             : $"{pay}, about the usual for this job";
    }

    /// <summary>
    /// The chat's [YOUR JOB HERE] for one of the player's employees: how the game says they feel about the job, their pay
    /// and hours, and what happened at work lately. Empty when there's nothing to tell.
    /// </summary>
    public static List<string> Lines(WorkReading? now, IReadOnlyList<WorkEvent>? events, WorkLifeState? state = null, int today = -1)
    {
        var lines = new List<string>();
        if (Since(state, today) is { Length: > 0 } since) lines.Add(since);
        if (now != null)
        {
            // CF-14: who else works here (the same answer whoever you ask: "I'm the only one here" only when it's true).
            if (now.CoworkersRead && Coworkers(now) is { Length: > 0 } team) lines.Add(team);
            string feel = now.Happiness switch
            {
                "Happy" => $"you're happy in this job ({Why(now, unhappy: false)})",
                "Sad" => $"you're unhappy in this job ({Why(now, unhappy: true)})",
                "Normal" => "the job is all right: nothing to love, nothing to quit over",
                _ => "",
            };
            if (feel.Length > 0) lines.Add($"How the job feels to you (a fact, never contradict it): {feel}.");
            if (Pay(now) is { Length: > 0 } pay) lines.Add($"Your pay: {pay}.");
            float len = ShiftLength(now.ShiftStart, now.ShiftEnd);
            if (len >= 1 && len <= 20)
                lines.Add($"Your hours: {Clock(now.ShiftStart)} to {Clock(now.ShiftEnd)} ({len:0} hours)" +
                          (now.PreferredHours > 0 && Math.Abs(now.PreferredHours - len) >= 2 ? $"; you'd rather work about {now.PreferredHours}." : "."));
        }
        var recent = (events ?? Array.Empty<WorkEvent>()).Where(e => e.Text.Length > 0).TakeLast(4).ToList();
        if (recent.Count > 0)
        {
            lines.Add("Lately at work (oldest first):");
            foreach (var e in recent) lines.Add($"- Day {e.GameDay}: {e.Text}");
        }
        if (lines.Count > 0)
            lines.Add("Let it colour how you talk about work and how you feel about your boss; bring up pay or hours only when it fits, the way an employee would.");
        return lines;
    }

    private static string Clock(float hour)
    {
        int h = (int)Math.Floor(hour) % 24, m = (int)Math.Round((hour - Math.Floor(hour)) * 60) % 60;
        return $"{h:00}:{m:00}";
    }
}
