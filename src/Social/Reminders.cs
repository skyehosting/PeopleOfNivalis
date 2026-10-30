using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Social;

/// <summary>
/// FB-65 (the author: "Maybe they will appear the same way the toast appear when you havent payed a worker, just with
/// custom text"): reminders and plans made in a chat ("remind me to pay my rent tomorrow", "meet me at the market at 8").
/// Kept per save (reminders.json, part of FB-61's snapshot), checked against the game's day and hour and where you are, and
/// shown once as the game's toast. The person you made the plan with knows about it until it's done or past. No Unity.
/// </summary>
public sealed class ReminderBook
{
    public sealed class Reminder
    {
        public string Id { get; set; } = "";
        /// <summary>What it's about, as the person said it ("pay the rent", "meet Mae at the market").</summary>
        public string What { get; set; } = "";
        /// <summary>Who you made it with (their id and name as you know them), or "" for one of your own.</summary>
        public string WhoId { get; set; } = "";
        public string WhoName { get; set; } = "";
        /// <summary>The game day it's for, and the hour (0-23; -1 = any time that day).</summary>
        public int Day { get; set; }
        public int Hour { get; set; } = -1;
        /// <summary>Where (a place's name, matched loosely against where you are); "" = anywhere.</summary>
        public string Place { get; set; } = "";
        public int MadeOnDay { get; set; }
        public bool Shown { get; set; }
        /// <summary>The inner-life round (plans that happen): a meet-up's outcome ("" open, met, stood up, flaked; Mind.Meetups).</summary>
        public string Outcome { get; set; } = "";
        /// <summary>You were at the place in its window.</summary>
        public bool PlayerCame { get; set; }
    }

    public sealed class Data
    {
        public List<Reminder> Items { get; set; } = new();
    }

    public const int MaxKept = 40;
    /// <summary>A reminder this many days past its day is let go (shown or not).</summary>
    public const int KeptDaysAfter = 3;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    private readonly string _path;
    private readonly Action<string> _warn;
    private readonly object _lock = new();
    private Data _data = new();
    private bool _closed;
    private readonly bool _readOnly;

    public ReminderBook(string path, Action<string> warn)
    {
        _path = path;
        _warn = warn;
        // The full audit (A-002): a file another program holds is left alone for the session (never written over later); a
        // damaged one is set aside first.
        _data = DataFile.LoadBook(path, t => JsonSerializer.Deserialize<Data>(t, Json), m => _warn("Reminders: " + m), out _readOnly) ?? new Data();
        _data.Items ??= new List<Reminder>();
        _data.Items.RemoveAll(r => r == null);
        foreach (var r in _data.Items) { r.Outcome ??= ""; r.What ??= ""; r.WhoId ??= ""; r.WhoName ??= ""; r.Place ??= ""; r.Id ??= ""; } // an older or hand-edited file
    }

    /// <summary>
    /// A new reminder (the AI's reply asked for one). Days are clamped to the next 30, hours to 0-23 (or -1), text cut to
    /// 160 characters; one the same as an open one (same who, day and text) isn't added twice. Returns it, or null.
    /// </summary>
    public Reminder? Add(string what, string whoId, string whoName, int today, int inDays, int atHour, string place)
    {
        what = ReplyCut(what, 160);
        if (what.Length < 3 || today < 0) return null;
        var r = new Reminder
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            What = what, WhoId = whoId ?? "", WhoName = ReplyCut(whoName, 60),
            Day = today + Math.Clamp(inDays, 0, 30), Hour = atHour is >= 0 and <= 23 ? atHour : -1,
            Place = ReplyCut(place, 80), MadeOnDay = today,
        };
        lock (_lock)
        {
            if (_data.Items.Any(x => !x.Shown && x.WhoId == r.WhoId && x.Day == r.Day && string.Equals(x.What, r.What, StringComparison.OrdinalIgnoreCase)))
                return null;
            // FB-78 (the author: "she repeated it a couple of time and it also kept popping up"): the same plan with them,
            // said again in other words, shown or not: that plan, with a place or hour it lacked filled in. Never a new one.
            if (r.WhoId.Length > 0 && _data.Items.FirstOrDefault(x => x.WhoId == r.WhoId && x.Day == r.Day && x.Outcome.Length == 0 && SamePlan(x, r)) is { } had)
            {
                bool changed = false;
                if (had.Place.Length == 0 && r.Place.Length > 0) { had.Place = r.Place; changed = true; }
                if (had.Hour < 0 && r.Hour >= 0) { had.Hour = r.Hour; changed = true; }
                if (changed) Save();
                return null;
            }
            _data.Items.Add(r);
            // Review F10: what's been shown goes first; only then the oldest still to come.
            while (_data.Items.Count > MaxKept)
            {
                // Review R7 (CF-20 shows a plan at its hour): a meet-up still to happen isn't one to drop for that.
                int shown = _data.Items.FindIndex(x => x.Shown && (x.Outcome.Length > 0 || x.WhoId.Length == 0 || x.Place.Length == 0));
                if (shown < 0) shown = _data.Items.FindIndex(x => x.Shown);
                _data.Items.RemoveAt(shown >= 0 ? shown : 0);
            }
            Save();
        }
        return r;
    }

    private static readonly HashSet<string> PlanFiller = new(StringComparer.Ordinal) { "meet", "with", "plan", "about", "some", "them", "they", "have", "there", "then" };

    private static HashSet<string> PlanWords(string s) => new(
        System.Text.RegularExpressions.Regex.Split((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{Nd}]+").Where(w => w.Length >= 4 && !PlanFiller.Contains(w)),
        StringComparer.Ordinal);

    /// <summary>
    /// FB-78: two plans with the same person on the same day are one when nothing tells them apart (no two hours, no two
    /// places) and something ties them: the same place, the same hour, or a word of what it is ("after work").
    /// </summary>
    internal static bool SamePlan(Reminder a, Reminder b)
    {
        if (a.Hour >= 0 && b.Hour >= 0 && a.Hour != b.Hour) return false;
        bool placeA = a.Place.Length > 0, placeB = b.Place.Length > 0;
        if (placeA && placeB && !string.Equals(a.Place, b.Place, StringComparison.OrdinalIgnoreCase)) return false;
        if (placeA && placeB) return true;
        // Review PLAN-1: the same hour alone doesn't tie two plans ("pay the rent" and "feed the cat", both at six).
        return PlanWords(a.What).Overlaps(PlanWords(b.What));
    }

    /// <summary>
    /// The reminders due now, marked shown (each is shown once): its day reached and, if it has an hour, that hour reached
    /// (on a later day, at any hour). CF-20: a plan with an hour reminds you at its hour wherever you are; a place plan
    /// with no hour is due once you're there that day (the district or the venue you're in), or past its day.
    /// </summary>
    public List<Reminder> Due(int day, int hour, string where) => Due(day, hour, new[] { where });

    /// <summary>As <see cref="Due(int,int,string)"/>, where you are being every name for it (the district, the venue).</summary>
    public List<Reminder> Due(int day, int hour, IReadOnlyList<string> wheres)
    {
        var due = new List<Reminder>();
        if (day < 0) return due;
        lock (_lock)
        {
            foreach (var r in _data.Items)
            {
                if (r.Shown) continue;
                bool timeOk = day > r.Day || (day == r.Day && (r.Hour < 0 || hour >= r.Hour));
                bool placeOk = r.Place.Length == 0 || r.Hour >= 0 || PlaceMatches(wheres, r.Place) || day > r.Day;
                if (!timeOk || !placeOk) continue;
                r.Shown = true;
                due.Add(r);
            }
            int before = _data.Items.Count;
            _data.Items.RemoveAll(r => day - r.Day > KeptDaysAfter);
            if (due.Count > 0 || _data.Items.Count != before) Save();
        }
        return due;
    }

    /// <summary>Whether any reminder still to show is about a place (where you are only matters then).</summary>
    public bool NeedsPlace() { lock (_lock) return _data.Items.Any(r => !r.Shown && r.Place.Length > 0 && r.Hour < 0); } // review R7: an hour decides the rest

    /// <summary>A reminder that couldn't be shown after all: shown again next time.</summary>
    public void Unshow(string id)
    {
        lock (_lock)
        {
            var r = _data.Items.FirstOrDefault(x => x.Id == id);
            if (r is { Shown: true }) { r.Shown = false; Save(); }
        }
    }

    /// <summary>The open plans with someone (for their prompt: they know what you agreed), newest last.</summary>
    public List<Reminder> With(string whoId, int today)
    {
        lock (_lock) return _data.Items.Where(r => r.WhoId == whoId && today - r.Day <= 1).Select(Copy).ToList();
    }

    /// <summary>The inner-life round: meet-ups (a plan with someone at a place) whose outcome isn't settled yet.</summary>
    public List<Reminder> OpenMeetups()
    {
        lock (_lock) return _data.Items.Where(r => r.Outcome.Length == 0 && r.WhoId.Length > 0 && r.Place.Length > 0).Select(Copy).ToList();
    }

    /// <summary>A meet-up's outcome, once (an outcome already set is kept). False when there's no such open plan.</summary>
    public bool SetOutcome(string id, string outcome)
    {
        lock (_lock)
        {
            var r = _data.Items.FirstOrDefault(x => x.Id == id);
            if (r == null || r.Outcome.Length > 0 || outcome.Length == 0) return false;
            r.Outcome = outcome;
            r.Shown = true; // it's past: no pop-up for it any more
            Save();
            return true;
        }
    }

    /// <summary>You were at a meet-up's place in its window.</summary>
    public void Came(string id)
    {
        lock (_lock)
        {
            var r = _data.Items.FirstOrDefault(x => x.Id == id);
            if (r is { PlayerCame: false }) { r.PlayerCame = true; Save(); }
        }
    }

    /// <summary>Meet-ups with someone whose outcome is set, within the last few days (their chat knows what happened).</summary>
    public List<Reminder> Outcomes(string whoId, int today)
    {
        lock (_lock) return _data.Items.Where(r => r.WhoId == whoId && r.Outcome.Length > 0 && today - r.Day <= KeptDaysAfter).Select(Copy).ToList();
    }

    public List<Reminder> All() { lock (_lock) return _data.Items.Select(Copy).ToList(); }

    public int Clear()
    {
        lock (_lock)
        {
            int n = _data.Items.Count;
            _data.Items.Clear();
            Save();
            return n;
        }
    }

    /// <summary>Everything about someone (a reset of them, or someone found to be under 18).</summary>
    public void Forget(string whoId)
    {
        lock (_lock) { if (_data.Items.RemoveAll(r => r.WhoId == whoId) > 0) Save(); }
    }

    /// <summary>No more writes (FB-61: a load swaps the book; a late write would land on the other save's).</summary>
    public void Close() { lock (_lock) _closed = true; }

    private void Save()
    {
        if (_closed || _readOnly) return;
        // durable: false: saved on the game's thread (the full audit, A-009), as WorldMemory's are.
        try { DataFile.WriteAtomic(_path, JsonSerializer.Serialize(_data, Json), durable: false); }
        catch (Exception e) { _warn($"Reminders couldn't be saved ({e.GetType().Name})."); }
    }

    /// <summary>CF-20: any of the names for where you are (the district, the venue you're in).</summary>
    public static bool PlaceMatches(IEnumerable<string> wheres, string? place) => wheres.Any(w => PlaceMatches(w, place));

    /// <summary>Loosely: "the market" is at "Meridian Market", "noodle bar" at "Kiko's Noodle Bar".</summary>
    public static bool PlaceMatches(string? where, string? place)
    {
        string w = Norm(where), p = Norm(place);
        if (w.Length == 0 || p.Length == 0) return false;
        if (w.Contains(p, StringComparison.Ordinal) || p.Contains(w, StringComparison.Ordinal)) return true;
        var words = p.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length > 3).ToList();
        return words.Count > 0 && words.All(x => w.Contains(x, StringComparison.Ordinal));
    }

    private static string Norm(string? s)
    {
        string t = new string((s ?? "").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
        foreach (var stop in new[] { " the ", " a ", " at ", " to " }) t = (" " + t + " ").Replace(stop, " ").Trim();
        return string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string ReplyCut(string? s, int max)
    {
        string t = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return t.Length <= max ? t : t[..max].TrimEnd() + "…";
    }

    private static Reminder Copy(Reminder r) => new()
    {
        Id = r.Id, What = r.What, WhoId = r.WhoId, WhoName = r.WhoName, Day = r.Day, Hour = r.Hour, Place = r.Place,
        MadeOnDay = r.MadeOnDay, Shown = r.Shown, Outcome = r.Outcome, PlayerCame = r.PlayerCame,
    };

    /// <summary>"tomorrow at 20:00 at the market" for a toast or a prompt.</summary>
    public static string When(Reminder r, int today)
    {
        int d = r.Day - today;
        string day = d switch { < 0 => $"day {r.Day}", 0 => "today", 1 => "tomorrow", _ => $"in {d} days (day {r.Day})" };
        return day + (r.Hour >= 0 ? $" at {r.Hour:00}:00" : "") + (r.Place.Length > 0 ? $", {r.Place}" : "");
    }
}
