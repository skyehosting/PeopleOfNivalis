using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Mind;

/// <summary>
/// The kinds of things people notice each other doing (the witness layer), how much each stands out, how it reads, and
/// how each is put into words. "player" is the player's id here: the newcomer.
/// </summary>
public static class Happenings
{
    public const string Player = "player";

    public const string Argued = "argued", Laughed = "laughed", Flirted = "flirted", GotTogether = "together", BrokeUp = "broke up",
        Confronted = "confronted", Jealous = "jealous", WalkedOut = "walked out",
        PlayerTalked = "talked", PlayerUpset = "upset", PlayerKind = "kind", PlayerDate = "date", PlayerCouple = "couple",
        PlayerFollowed = "followed", StoodUp = "stood up", PlayerAte = "ate";

    /// <summary>How much it stands out to anyone (Talk of the Town's "base" salience): trivial 0.1, notable 0.4, major 0.8, flashbulb 1.</summary>
    public static float Base(string kind) => kind switch
    {
        BrokeUp => 1.0f,
        Confronted => 0.9f,
        Argued or GotTogether or PlayerCouple or Jealous or StoodUp => 0.8f,
        Flirted or PlayerUpset => 0.6f,
        PlayerDate => 0.55f,
        PlayerKind or WalkedOut => 0.4f,
        PlayerFollowed => 0.3f,
        Laughed => 0.25f,
        PlayerTalked => 0.2f,
        PlayerAte => 0.12f,
        _ => 0.2f,
    };

    /// <summary>How it reads in general (bad -1 .. good +1), before who it's about.</summary>
    public static float Valence(string kind) => kind switch
    {
        BrokeUp or StoodUp => -0.6f,
        Argued or Confronted or Jealous or PlayerUpset => -0.5f,
        WalkedOut => -0.3f,
        GotTogether => 0.5f,
        PlayerCouple or PlayerKind => 0.4f,
        PlayerDate or Laughed => 0.3f,
        Flirted => 0.1f,
        _ => 0f,
    };

    /// <summary>Things heard from further off (raised voices, laughter), not only seen.</summary>
    public static bool Loud(string kind) => kind is Argued or Confronted or Jealous or BrokeUp or Laughed;

    /// <summary>Kept for a long while when this strong (game hours before most of it fades, at full strength).</summary>
    public static double TauHours(float salience) => salience < 0.2f ? 6 : salience < 0.5f ? 48 : salience < 0.9f ? 240 : 1440;

    /// <summary>
    /// What happened, the way <paramref name="ownerId"/> would put it: "Doran had a row with a waiter at Neon Ramen last
    /// night (you saw it)", "the newcomer stood you up at the noodle bar (yesterday)", "the way Mika told it, ...".
    /// <paramref name="player"/> is how the player is named here ("the newcomer", "the person in front of you").
    /// </summary>
    public static string Words(Belief b, string ownerId, string player, long now, bool how = true)
    {
        // A name is said by its first name; a description ("a waiter") as it is.
        string N(string id, string name) =>
            id == ownerId ? "you" : id == Player ? player : name.Length == 0 ? "someone" : char.IsUpper(name[0]) ? Ai.Prompts.FirstName(name) : name;
        string a = N(b.Actor, b.ActorName), t = N(b.Target, b.TargetName);
        float i = b.Intensity;
        string what = b.Kind switch
        {
            Argued => $"{a} {(i >= 0.85f ? "had a proper shouting match with" : i >= 0.6f ? "had a row with" : "had words with")} {t}",
            Laughed => $"{a} and {t} were having a good laugh",
            Flirted => $"{a} {(i >= 0.85f ? "was all over" : "was flirting with")} {t}",
            GotTogether => $"{a} and {t} got together",
            BrokeUp => $"{a} {(i >= 0.85f ? "had a messy breakup with" : "broke up with")} {t}",
            Confronted => $"{a} had it out with {t}{(b.Detail.Length > 0 ? " over " + b.Detail : "")}",
            Jealous => $"{a} had a jealous go at {t}",
            WalkedOut => $"{a} walked out{(b.Place.Length > 0 ? " of " + b.Place : "")}",
            PlayerTalked => $"{a} {(i >= 0.6f ? "was deep in conversation with" : "was talking with")} {t}",
            PlayerUpset => $"{a} {(i >= 0.85f ? "really upset" : "annoyed")} {t}",
            PlayerKind => b.Detail.Length > 0 ? $"{a} gave {t} {b.Detail}" : $"{a} did something kind for {t}",
            PlayerDate => $"{a} was out on a date with {t}",
            PlayerCouple => $"{a} and {t} are together now",
            PlayerFollowed => $"{a} was tagging along with {t}",
            StoodUp => b.Target == ownerId ? $"{a} stood you up: you waited and they never came" : $"{a} stood {t} up",
            PlayerAte => $"{a} ate{(b.Place.Length > 0 ? " at " + b.Place : "")}",
            _ => $"{a} and {t}",
        };
        bool placeSaid = b.Kind is WalkedOut or PlayerAte;
        string where = !placeSaid && b.Place.Length > 0 ? " at " + b.Place : "";
        string when = " " + GameClock.Ago(b.At, now);
        string source = b.How switch
        {
            Belief.Saw => " (you saw it)",
            Belief.Heard => " (you heard it nearby)",
            Belief.Told when b.Hops <= 1 => $" (heard from {First(b.SourceName)})",
            Belief.Told => $" (the way {First(b.SourceName)} told it; you're not sure)",
            _ => "",
        };
        return what + where + when + (how ? source : "");
    }

    private static string First(string name) => name.Length > 0 ? Ai.Prompts.FirstName(name) : "someone";
}

/// <summary>Something one person knows happened (Talk of the Town's belief): who did what to whom, where and when, how they know it, how strongly it's with them.</summary>
public sealed class Belief
{
    public const string Saw = "saw", Heard = "heard", Told = "told", Lived = "lived";
    public string Id { get; set; } = "";
    /// <summary>The happening it's about (the same for everyone who knows it, however it reached them).</summary>
    public string EventId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Actor { get; set; } = "";
    public string ActorName { get; set; } = "";
    public string Target { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string Place { get; set; } = "";
    public string Detail { get; set; } = "";
    /// <summary>When it happened (game minutes).</summary>
    public long At { get; set; }
    public string How { get; set; } = Saw;
    /// <summary>Who told them (a hearsay belief) and how many tellings away from someone who saw it.</summary>
    public string Source { get; set; } = "";
    public string SourceName { get; set; } = "";
    public int Hops { get; set; }
    public float Intensity { get; set; } = 0.6f;
    public float Salience { get; set; }
    /// <summary>MemoryBank's S: up by one each time it's recalled or retold.</summary>
    public float Strength { get; set; } = 1f;
    public long Touched { get; set; }
    public bool Pinned { get; set; }
    public bool Secret { get; set; }
    /// <summary>Who they've told it to (never twice to the same person).</summary>
    public List<string> ToldTo { get; set; } = new();
    /// <summary>The game days it was in a chat with the player (it comes up, then rests).</summary>
    public List<int> ShownDays { get; set; } = new();

    public Belief Copy()
    {
        var b = (Belief)MemberwiseClone();
        b.ToldTo = ToldTo.ToList();
        b.ShownDays = ShownDays.ToList();
        return b;
    }

    public bool Involves(string id) => Actor == id || Target == id;
}

/// <summary>Someone near a happening: who, how they know the people in it (a salience factor), how close, whether they saw it, how wound up they are.</summary>
public sealed class Observer
{
    public string Id = "", Name = "";
    /// <summary>Talk of the Town's relation factor: they're the target 2, partner or close friend 1.6, someone they dislike 1.4, known 1.2, stranger 0.6.</summary>
    public float Relation = 0.6f;
    public float Distance;
    public bool Sees;
    public float Arousal = Voice.Tone.CalmArousal;
}

/// <summary>
/// The inner-life round, step 1 (the research's witness layer, Talk of the Town's knowledge model): what each resident saw
/// or heard others do, kept per person and per save (witness.json, part of FB-61's snapshot). Each belief has a salience
/// (what it is, who it's about, how close they were, how wound up they were) and fades by MemoryBank's forgetting curve
/// (R = e^(-t/S), with S up each time it's recalled or retold); the strongest are pinned. 24 live and 8 pinned each. It's
/// only ever read for its owner (nobody knows the world's truth), never holds anyone under 18 (the gate is the caller's
/// and is checked again here), and costs no tokens. No Unity, tested.
/// </summary>
public sealed class WitnessBook
{
    public sealed class Data
    {
        public Dictionary<string, List<Belief>> By { get; set; } = new();
        public long Clock { get; set; } = -1;
    }

    public const int MaxLive = 24, MaxPinned = 8, MaxOwners = 500;
    /// <summary>Below this it isn't kept at all; at or above <see cref="PinAt"/> it's pinned.</summary>
    public const float StoreFloor = 0.15f, PinAt = 0.9f;
    /// <summary>A belief this faded goes (unless pinned).</summary>
    public const double ForgetBelow = 0.15;

    private readonly string _path;
    private readonly object _lock = new();
    private Data _data;
    private readonly Action<string>? _warn;
    private bool _readOnly, _warnedReadOnly, _dirty;
    private readonly BookWriter _writer;
    /// <summary>Anyone known to be under 18: nothing about them is ever kept or told (set by the mod).</summary>
    public Func<string, bool> IsMinor { get; set; } = _ => false;

    public WitnessBook(string path, Action<string>? warn = null)
    {
        _path = path;
        _warn = warn;
        _data = DataFile.LoadBook(_path, t => JsonSerializer.Deserialize<Data>(t, NpcStore.Json), _warn, out _readOnly) ?? new Data();
        _data.By ??= new();
        foreach (var k in _data.By.Keys.ToList())
        {
            _data.By[k] = (_data.By[k] ?? new()).Where(b => b != null).ToList();
            foreach (var b in _data.By[k]) Repair(b); // a hand-edited or older file: nothing null is read back
        }
        _writer = new BookWriter(_path, Snapshot, _warn, failed: () => { lock (_lock) _dirty = true; });
    }

    internal BookWriter Writer => _writer;

    private string? Snapshot()
    {
        Data copy;
        lock (_lock)
        {
            if (!_dirty) return null;
            if (_readOnly) { DataFile.NotSaved(_path, _warn, ref _warnedReadOnly); return null; }
            copy = new Data { Clock = _data.Clock, By = _data.By.ToDictionary(kv => kv.Key, kv => kv.Value.Select(b => b.Copy()).ToList()) };
            _dirty = false;
        }
        return JsonSerializer.Serialize(copy, NpcStore.Json);
    }

    private void Changed()
    {
        _dirty = true;
        _writer.SaveSoon();
    }

    public void Flush() { _writer.WaitIdle(); _writer.Write(); }

    public void Close()
    {
        try { Flush(); }
        finally { _writer.Close(); }
    }

    public int Count { get { lock (_lock) return _data.By.Values.Sum(l => l.Count); } }

    // ------------------------------------------------------------------ noticing

    /// <summary>
    /// A happening near people: each observer who notices it enough (salience at least <see cref="StoreFloor"/>) keeps it.
    /// Never anything with someone under 18 in it, never an observer under 18, never one of the two it's about.
    /// Returns how many kept it.
    /// </summary>
    public int Witness(string kind, string actor, string actorName, string target, string targetName, string place, long now, float intensity,
                       IEnumerable<Observer> observers, bool secret = false, string detail = "")
    {
        if (now < 0 || kind.Length == 0 || IsMinor(actor) || IsMinor(target)) return 0;
        string eventId = Guid.NewGuid().ToString("N")[..12];
        int kept = 0;
        lock (_lock)
        {
            _data.Clock = Math.Max(_data.Clock, now);
            foreach (var o in observers)
            {
                if (o.Id.Length == 0 || o.Id == actor || o.Id == target || IsMinor(o.Id)) continue;
                var mine = Own(o.Id);
                int same = mine.Count(b => b.Kind == kind && b.Actor == actor && now - b.At < GameClock.PerDay);
                float proximity = o.Distance <= 3f ? 1f : o.Sees ? 0.6f : 0.3f;
                float salience = Happenings.Base(kind) * Math.Clamp(o.Relation, 0.3f, 2f) * (1f / (1 + same)) * proximity * (0.8f + 0.4f * Math.Clamp(o.Arousal, 0f, 1f));
                salience *= 0.75f + 0.5f * Math.Clamp(intensity, 0f, 1f);
                if (salience < StoreFloor) continue;
                mine.Add(new Belief
                {
                    Id = Guid.NewGuid().ToString("N")[..12], EventId = eventId, Kind = kind, Actor = actor, ActorName = Cut(actorName), Target = target, TargetName = Cut(targetName),
                    Place = Cut(place), Detail = Cut(detail), At = now, How = o.Sees || o.Distance <= 3f ? Belief.Saw : Belief.Heard,
                    Intensity = Math.Clamp(intensity, 0f, 1f), Salience = Math.Min(salience, 1.5f), Touched = now, Pinned = salience >= PinAt, Secret = secret,
                });
                Trim(mine, now);
                kept++;
            }
            EvictOwners();
            if (kept > 0) Changed();
        }
        return kept;
    }

    /// <summary>
    /// Something that happened to them (they were there): kept pinned, like being stood up. Not for anyone under 18.
    /// </summary>
    public Belief? Lived(string owner, string kind, string actor, string actorName, string target, string targetName, string place, long now, float intensity, string detail = "")
    {
        if (now < 0 || owner.Length == 0 || IsMinor(owner) || IsMinor(actor) || IsMinor(target)) return null;
        lock (_lock)
        {
            _data.Clock = Math.Max(_data.Clock, now);
            var mine = Own(owner);
            var b = new Belief
            {
                Id = Guid.NewGuid().ToString("N")[..12], EventId = Guid.NewGuid().ToString("N")[..12], Kind = kind, Actor = actor, ActorName = Cut(actorName),
                Target = target, TargetName = Cut(targetName), Place = Cut(place), Detail = Cut(detail), At = now, How = Belief.Lived,
                Intensity = Math.Clamp(intensity, 0f, 1f), Salience = Math.Max(Happenings.Base(kind) * 1.3f, PinAt), Touched = now, Pinned = true,
            };
            mine.Add(b);
            Trim(mine, now);
            EvictOwners();
            Changed();
            return b.Copy();
        }
    }

    // ------------------------------------------------------------------ passing it on

    /// <summary>
    /// <paramref name="listener"/> hears <paramref name="told"/> from <paramref name="teller"/>, told with
    /// <paramref name="stance"/> (Telling): a hearsay belief, one more telling away, held as strongly as the teller's
    /// trust allows (Talk of the Town), and changed the way stories change from mouth to mouth (Allport and Postman):
    /// a detail dropped now and then (levelling), made a bit bigger (sharpening; more when they embellish), or smaller when
    /// they go easy. Someone who already knows it holds it more firmly instead. Nobody hears about themselves.
    /// </summary>
    public Belief? Hear(string listener, string listenerName, Belief told, string teller, string tellerName, string stance, float trust, long now, Random rng)
    {
        if (listener.Length == 0 || listener == teller || told.Involves(listener) || IsMinor(listener) || IsMinor(told.Actor) || IsMinor(told.Target) || IsMinor(teller)) return null;
        lock (_lock)
        {
            _data.Clock = Math.Max(_data.Clock, now);
            var mine = Own(listener);
            var known = mine.FirstOrDefault(b => b.EventId == told.EventId);
            if (known != null)
            {
                known.Strength += 0.5f;
                known.Touched = now;
                // Review (core IL-2): told to someone who knew it already, it's still told: not again to them.
                if (_data.By.TryGetValue(teller, out var tellers) && tellers.FirstOrDefault(x => x.EventId == told.EventId) is { } tb && !tb.ToldTo.Contains(listener))
                    tb.ToldTo.Add(listener);
                Changed();
                return known.Copy();
            }
            var b = told.Copy();
            b.Id = Guid.NewGuid().ToString("N")[..12];
            b.How = Belief.Told;
            b.Source = teller;
            b.SourceName = Cut(tellerName);
            b.Hops = Math.Max(1, told.How == Belief.Told ? told.Hops + 1 : 1);
            b.Strength = Math.Clamp(told.Strength * Math.Clamp(trust, 0.1f, 1f) * 0.7f, 0.3f, 3f);
            b.Salience = Math.Clamp(told.Salience * 0.85f, StoreFloor, 1.5f);
            b.Touched = now;
            b.Pinned = false;
            b.ToldTo = new List<string>();
            b.ShownDays = new List<int>();
            // Mouth to mouth: what changes, decided here (the AI only says the result).
            if (b.Detail.Length > 0 && rng.NextDouble() < 0.3) b.Detail = "";
            double sharpen = stance == Telling.Embellished ? 0.6 : stance == Telling.Softened ? 0 : 0.25;
            if (rng.NextDouble() < sharpen) b.Intensity = Math.Min(1f, b.Intensity * (stance == Telling.Embellished ? 1.3f : 1.15f));
            if (stance == Telling.Softened) b.Intensity *= 0.8f;
            mine.Add(b);
            Trim(mine, now);
            // The teller holds it more firmly for having told it, and won't tell this one the same thing again.
            if (_data.By.TryGetValue(teller, out var theirs) && theirs.FirstOrDefault(x => x.EventId == told.EventId) is { } t)
            {
                t.Strength += 1f;
                t.Touched = now;
                if (!t.ToldTo.Contains(listener)) t.ToldTo.Add(listener);
            }
            EvictOwners();
            Changed();
            return b.Copy();
        }
    }

    // ------------------------------------------------------------------ reading (only ever one owner's own)

    /// <summary>How much of it is still there: MemoryBank's R = e^(-t/S), S = the kind's time × strength × (0.5 + salience).</summary>
    public static double Retention(Belief b, long now)
    {
        double tau = Happenings.TauHours(b.Salience) * Math.Max(0.3, b.Strength) * (0.5 + b.Salience) * 60;
        return Math.Exp(-Math.Max(0, now - b.Touched) / Math.Max(1, tau));
    }

    /// <summary>How much it's with them now (Generative Agents' recency × importance): what's told, shown and thought about first.</summary>
    public static double Score(Belief b, long now) => Retention(b, now) * b.Salience * (b.Pinned ? 1.2 : 1);

    /// <summary>What <paramref name="owner"/> knows, the faded gone, the strongest first (copies).</summary>
    public List<Belief> Of(string owner, long now)
    {
        if (owner.Length == 0 || IsMinor(owner)) return new List<Belief>();
        lock (_lock)
        {
            if (!_data.By.TryGetValue(owner, out var mine)) return new List<Belief>();
            // Review (core IL-8): a clock that went back (a new game, an earlier save) can't leave a memory that never fades.
            foreach (var b in mine) { if (b.Touched > now) b.Touched = now; if (b.At > now) b.At = now; }
            int before = mine.Count;
            mine.RemoveAll(b => IsMinor(b.Actor) || IsMinor(b.Target) || IsMinor(b.Source) || (!b.Pinned && Retention(b, now) < ForgetBelow));
            if (mine.Count != before) Changed();
            return mine.OrderByDescending(b => Score(b, now)).Select(b => b.Copy()).ToList();
        }
    }

    /// <summary>What <paramref name="owner"/> knows about <paramref name="subject"/> (the player, or a resident), strongest first.</summary>
    public List<Belief> About(string owner, string subject, long now) => Of(owner, now).Where(b => b.Involves(subject)).ToList();

    /// <summary>It came up in a chat with the player today (it rests after a couple of days of that).</summary>
    public void Shown(string owner, IEnumerable<string> beliefIds, int day)
    {
        lock (_lock)
        {
            if (!_data.By.TryGetValue(owner, out var mine)) return;
            bool any = false;
            foreach (var id in beliefIds)
                if (mine.FirstOrDefault(b => b.Id == id) is { } b && !b.ShownDays.Contains(day))
                {
                    b.ShownDays.Add(day);
                    if (b.ShownDays.Count > 6) b.ShownDays.RemoveAt(0);
                    any = true;
                }
            if (any) Changed();
        }
    }

    // ------------------------------------------------------------------ book-keeping

    /// <summary>
    /// Everything they know, and everything anyone knows with them in it (a reset of them, or found to be under 18): as the
    /// one who did it, had it done to them, told it, or is named in its detail by <paramref name="name"/>; and they're off
    /// everyone's list of who's been told (review core IL-4).
    /// </summary>
    public void Forget(string id, string name = "")
    {
        if (id.Length == 0) return;
        string first = Ai.Prompts.FirstName(name ?? "");
        // Review (core IL-4b): their first name anywhere in a detail or a place, as a word.
        var named = first.Length >= 2 ? new System.Text.RegularExpressions.Regex($@"(?i)(?<![\p{{L}}]){System.Text.RegularExpressions.Regex.Escape(first)}(?![\p{{L}}])") : null;
        lock (_lock)
        {
            bool any = _data.By.Remove(id);
            foreach (var list in _data.By.Values)
            {
                any |= list.RemoveAll(b => b.Involves(id) || b.Source == id || (named != null && (named.IsMatch(b.Detail) || named.IsMatch(b.Place)))) > 0;
                foreach (var b in list) any |= b.ToldTo.Remove(id);
            }
            if (any) Changed();
        }
    }

    /// <summary>Everyone forgets everything (/resetall).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _data = new Data();
            Changed();
        }
    }

    /// <summary>A belief's owner's list (made when it's new). Under the lock.</summary>
    private List<Belief> Own(string owner)
    {
        if (!_data.By.TryGetValue(owner, out var list)) _data.By[owner] = list = new List<Belief>();
        return list;
    }

    /// <summary>24 live and 8 pinned: the faded first, then the least salient (MemoryBank: lowest R × salience goes).</summary>
    private static void Trim(List<Belief> list, long now)
    {
        list.RemoveAll(b => !b.Pinned && Retention(b, now) < ForgetBelow);
        while (list.Count(b => b.Pinned) > MaxPinned) { var p = list.Where(b => b.Pinned).OrderBy(b => Score(b, now)).First(); p.Pinned = false; }
        while (list.Count(b => !b.Pinned) > MaxLive) list.Remove(list.Where(b => !b.Pinned).OrderBy(b => Score(b, now)).First());
    }

    /// <summary>At most <see cref="MaxOwners"/> people: whoever's newest belief is oldest goes first. Under the lock.</summary>
    private void EvictOwners()
    {
        if (_data.By.Count <= MaxOwners) return;
        foreach (var k in _data.By.OrderBy(kv => kv.Value.Count == 0 ? long.MinValue : kv.Value.Max(b => b.Touched)).Take(_data.By.Count - MaxOwners).Select(kv => kv.Key).ToList())
            _data.By.Remove(k);
    }

    private static void Repair(Belief b)
    {
        b.Id ??= ""; b.EventId ??= ""; b.Kind ??= ""; b.Actor ??= ""; b.ActorName ??= ""; b.Target ??= ""; b.TargetName ??= "";
        b.Place ??= ""; b.Detail ??= ""; b.How ??= Belief.Saw; b.Source ??= ""; b.SourceName ??= "";
        b.ToldTo ??= new(); b.ShownDays ??= new();
        b.ToldTo.RemoveAll(x => x == null);
        if (!float.IsFinite(b.Salience)) b.Salience = StoreFloor;
        if (!float.IsFinite(b.Strength) || b.Strength <= 0) b.Strength = 1f;
        if (!float.IsFinite(b.Intensity)) b.Intensity = 0.6f;
    }

    /// <summary>Text from the game or the AI, kept short and on one line, with no brackets that could read as a prompt's own section.</summary>
    internal static string Cut(string? s, int max = 80)
    {
        string t = new string((s ?? "").Where(c => c is not ('[' or ']' or '{' or '}' or '<' or '>' or '"' or '\n' or '\r')).ToArray()).Trim();
        return t.Length <= max ? t : t[..max].TrimEnd();
    }
}
