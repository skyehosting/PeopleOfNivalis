using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Social;

/// <summary>
/// FB-63 (the author: "dating between the player and NPCs, and between NPCs ... cheating spreads as gossip unless the
/// partner saw it ... the cheated partner decides: call out, break up, or jealous acts"): who's together, kept per save
/// (romance.json). Residents' couples, the player's partners (a quick list; each person's own record says it too), and the
/// secrets: someone seen flirting while with someone else. A secret reaches the partner at once if they saw it, or as
/// gossip a day or more later (not always). The code decides each turn of it and the residents' conversation is told
/// (<see cref="Scene"/>), the same way [WHAT YOU'D DO] tells the person you talk to. No Unity.
/// </summary>
public sealed class CoupleBook
{
    public const string Player = "player";

    public sealed class Partner
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public int SinceDay { get; set; } = -1;
    }

    public sealed class Couple
    {
        public string A { get; set; } = "";
        public string B { get; set; } = "";
        public string NameA { get; set; } = "";
        public string NameB { get; set; } = "";
        public int SinceDay { get; set; } = -1;
    }

    /// <summary>Someone flirting while with someone else: who, with whom, who it would hurt.</summary>
    public sealed class Secret
    {
        public string Id { get; set; } = "";
        public string Cheater { get; set; } = "";
        public string CheaterName { get; set; } = "";
        public string With { get; set; } = "";
        public string WithName { get; set; } = "";
        public string Partner { get; set; } = "";
        public int Day { get; set; }
        /// <summary>The partner saw it themselves.</summary>
        public bool Seen { get; set; }
        /// <summary>The partner knows (saw it, or heard).</summary>
        public bool Known { get; set; }
        /// <summary>The partner has had it out with them (a resident cheater), or been told it (the player's partner).</summary>
        public bool Settled { get; set; }
        /// <summary>The partner still has it in for the one they flirted with (a jealous scene to come).</summary>
        public bool Grudge { get; set; }
    }

    public sealed class Data
    {
        public List<Partner> Partners { get; set; } = new();
        public List<Couple> Couples { get; set; } = new();
        public List<Secret> Secrets { get; set; } = new();
    }

    /// <summary>Secrets kept at most (the oldest settled go first).</summary>
    public const int MaxSecrets = 60;
    /// <summary>The chance gossip has reached the partner, once a day or more has passed.</summary>
    public const double GossipChance = 0.6;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    private readonly string _path;
    private readonly Action<string> _warn;
    private readonly object _lock = new();
    private Data _data = new();
    private bool _closed;
    private readonly bool _readOnly;

    public CoupleBook(string path, Action<string> warn)
    {
        _path = path;
        _warn = warn;
        // The full audit (A-002): a file another program holds is left alone for the session (never written over later); a
        // damaged one is set aside first.
        _data = DataFile.LoadBook(path, t => JsonSerializer.Deserialize<Data>(t, Json), m => _warn("Couples: " + m), out _readOnly) ?? new Data();
        _data.Partners ??= new();
        _data.Couples ??= new();
        _data.Secrets ??= new();
        // A hand-edited file (the full audit, A-007): no empty entries, no missing names.
        _data.Partners.RemoveAll(p => p == null);
        _data.Couples.RemoveAll(c => c == null);
        _data.Secrets.RemoveAll(s => s == null);
        foreach (var p in _data.Partners) { p.Id ??= ""; p.Name ??= ""; p.Status ??= ""; }
        foreach (var c in _data.Couples) { c.A ??= ""; c.B ??= ""; c.NameA ??= ""; c.NameB ??= ""; }
        foreach (var s in _data.Secrets) { s.Id ??= ""; s.Cheater ??= ""; s.CheaterName ??= ""; s.With ??= ""; s.WithName ??= ""; s.Partner ??= ""; }
    }

    // ------------------------------------------------------------------ the player's partners

    /// <summary>The player's status with someone ("" = none: they're taken off the list).</summary>
    public void SetPartner(string id, string name, string status, int sinceDay)
    {
        lock (_lock)
        {
            _data.Partners.RemoveAll(p => p.Id == id);
            if (status.Length > 0) _data.Partners.Add(new Partner { Id = id, Name = name, Status = status, SinceDay = sinceDay });
            Save();
        }
    }

    public List<Partner> Partners() { lock (_lock) return _data.Partners.Select(p => new Partner { Id = p.Id, Name = p.Name, Status = p.Status, SinceDay = p.SinceDay }).ToList(); }

    // ------------------------------------------------------------------ residents' couples

    public Couple? CoupleOf(string id) { lock (_lock) return Copy(_data.Couples.FirstOrDefault(c => c.A == id || c.B == id)); }

    public static string Other(Couple c, string id) => c.A == id ? c.B : c.A;
    public static string OtherName(Couple c, string id) => c.A == id ? c.NameB : c.NameA;

    public bool Together(string a, string b) { lock (_lock) return _data.Couples.Any(c => (c.A == a && c.B == b) || (c.A == b && c.B == a)); }

    /// <summary>A new couple (neither already with someone).</summary>
    public bool Start(string a, string nameA, string b, string nameB, int day)
    {
        lock (_lock)
        {
            if (a == b || _data.Couples.Any(c => c.A == a || c.B == a || c.A == b || c.B == b)) return false;
            _data.Couples.Add(new Couple { A = a, B = b, NameA = nameA, NameB = nameB, SinceDay = day });
            Save();
            return true;
        }
    }

    public bool End(string a, string b)
    {
        lock (_lock)
        {
            int n = _data.Couples.RemoveAll(c => (c.A == a && c.B == b) || (c.A == b && c.B == a));
            if (n > 0) Save();
            return n > 0;
        }
    }

    public List<Couple> Couples() { lock (_lock) return _data.Couples.Select(c => Copy(c)!).ToList(); }

    // ------------------------------------------------------------------ secrets

    /// <summary>Someone (a resident, or <see cref="Player"/>) flirting with <paramref name="with"/> while with <paramref name="partner"/>.</summary>
    public Secret? NoteSecret(string cheater, string cheaterName, string with, string withName, string partner, int day, bool seen)
    {
        if (cheater.Length == 0 || with.Length == 0 || partner.Length == 0 || with == partner || day < 0) return null;
        lock (_lock)
        {
            // The same two on the same day: once (seen counts if either time was).
            var same = _data.Secrets.FirstOrDefault(s => s.Cheater == cheater && s.With == with && s.Partner == partner && s.Day == day);
            if (same != null)
            {
                if (seen && !same.Seen) { same.Seen = same.Known = true; Save(); }
                return Copy(same);
            }
            var s = new Secret
            {
                Id = Guid.NewGuid().ToString("N")[..10], Cheater = cheater, CheaterName = Cut(cheaterName), With = with, WithName = Cut(withName),
                Partner = partner, Day = day, Seen = seen, Known = seen,
            };
            _data.Secrets.Add(s);
            while (_data.Secrets.Count > MaxSecrets)
            {
                int settled = _data.Secrets.FindIndex(x => x.Settled && !x.Grudge);
                _data.Secrets.RemoveAt(settled >= 0 ? settled : 0);
            }
            Save();
            return Copy(s);
        }
    }

    /// <summary>
    /// What <paramref name="partner"/> knows now and hasn't had out yet: seen, or heard (a day or more later, with
    /// <see cref="GossipChance"/>, one roll per secret per day). Marks them known.
    /// </summary>
    public List<Secret> KnownBy(string partner, int day)
    {
        var list = new List<Secret>();
        if (day < 0) return list;
        lock (_lock)
        {
            bool changed = false;
            foreach (var s in _data.Secrets.Where(x => x.Partner == partner && !x.Settled))
            {
                if (!s.Known && day > s.Day && Romance.Roll(s.Id, day, "gossip") < GossipChance) { s.Known = true; changed = true; }
                if (s.Known) list.Add(Copy(s));
            }
            if (changed) Save();
        }
        return list;
    }

    public void Settle(string secretId, bool grudge)
    {
        lock (_lock)
        {
            var s = _data.Secrets.FirstOrDefault(x => x.Id == secretId);
            if (s == null) return;
            s.Settled = true;
            s.Grudge = grudge;
            Save();
        }
    }

    public void EndGrudge(string secretId)
    {
        lock (_lock)
        {
            var s = _data.Secrets.FirstOrDefault(x => x.Id == secretId);
            if (s is { Grudge: true }) { s.Grudge = false; Save(); }
        }
    }

    /// <summary>Grudges still held: a partner who has it in for the one their partner flirted with.</summary>
    public List<Secret> Grudges() { lock (_lock) return _data.Secrets.Where(s => s.Grudge).Select(Copy).ToList(); }

    // ------------------------------------------------------------------ book-keeping

    /// <summary>Everything about someone (a reset of them, or someone found to be under 18).</summary>
    public void Forget(string id)
    {
        lock (_lock)
        {
            int n = _data.Partners.RemoveAll(p => p.Id == id) + _data.Couples.RemoveAll(c => c.A == id || c.B == id)
                    + _data.Secrets.RemoveAll(s => s.Cheater == id || s.With == id || s.Partner == id);
            if (n > 0) Save();
        }
    }

    public void Clear()
    {
        lock (_lock) { _data = new Data(); Save(); }
    }

    public void Close() { lock (_lock) _closed = true; }

    private void Save()
    {
        if (_closed || _readOnly) return;
        // durable: false: saved on the game's thread (the full audit, A-009), as WorldMemory's are.
        try { DataFile.WriteAtomic(_path, JsonSerializer.Serialize(_data, Json), durable: false); }
        catch (Exception e) { _warn($"Couples couldn't be saved ({e.GetType().Name})."); }
    }

    private static string Cut(string? s)
    {
        string t = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return t.Length <= 60 ? t : t[..60];
    }

    private static Couple? Copy(Couple? c) => c == null ? null : new Couple { A = c.A, B = c.B, NameA = c.NameA, NameB = c.NameB, SinceDay = c.SinceDay };

    private static Secret Copy(Secret s) => new()
    {
        Id = s.Id, Cheater = s.Cheater, CheaterName = s.CheaterName, With = s.With, WithName = s.WithName, Partner = s.Partner, Day = s.Day,
        Seen = s.Seen, Known = s.Known, Settled = s.Settled, Grudge = s.Grudge,
    };

    // ------------------------------------------------------------------ the residents' conversation

    /// <summary>What the code decided happens between them in this conversation (applied once it has been heard).</summary>
    public sealed class Turn
    {
        public string Kind = ""; // spark, cheat, confront, jealous
        public string A = "", B = "", NameA = "", NameB = "";
        public string Partner = "", PartnerName = "", SecretId = "";
        public bool Breakup;
    }

    public sealed class Person
    {
        public string Id = "", Name = "";
    }

    /// <summary>The chance two close residents who aren't together start dating in a conversation.</summary>
    public const double SparkChance = 0.10;
    /// <summary>The chance someone with a partner (not there) flirts with someone they get on well with.</summary>
    public const double CheatChance = 0.06;

    /// <summary>
    /// For a residents' conversation: the couples among them (said, so they talk like it), and at most one turn of the
    /// story (the code decides): a partner who found out has it out (and maybe breaks up), a jealous partner faces the one
    /// their partner flirted with, someone with a partner flirts with someone else, or two who've grown close get together.
    /// </summary>
    public (List<string> notes, Turn? turn) Scene(IReadOnlyList<Person> people, Func<string, string, PairBook.Pair?> pair, int day, Random rng,
        bool residentsDate, bool jealousy)
    {
        var notes = new List<string>();
        if (people.Count < 2 || day < 0) return (notes, null);
        var ids = people.Select(p => p.Id).ToHashSet();
        string Name(string id) => people.FirstOrDefault(p => p.Id == id)?.Name ?? "";
        List<Couple> couples;
        lock (_lock) couples = _data.Couples.Select(c => Copy(c)!).ToList();
        foreach (var c in couples.Where(c => ids.Contains(c.A) && ids.Contains(c.B)))
            notes.Add($"{Name(c.A)} and {Name(c.B)} are a couple (together since day {c.SinceDay}).");
        foreach (var p in people)
            if (couples.FirstOrDefault(c => (c.A == p.Id || c.B == p.Id) && !(ids.Contains(c.A) && ids.Contains(c.B))) is { } away)
                notes.Add($"{p.Name} is with {OtherName(away, p.Id)} (not here).");

        Turn? turn = null;
        if (jealousy)
        {
            // A partner who found out, with the one who did it, here.
            foreach (var p in people)
            {
                var known = KnownBy(p.Id, day).FirstOrDefault(s => s.Cheater != Player && ids.Contains(s.Cheater) && Together(p.Id, s.Cheater));
                if (known == null) continue;
                var rel = pair(p.Id, known.Cheater);
                bool breakup = rng.NextDouble() < ((rel?.Affinity ?? 0) < 30 ? 0.5 : 0.25);
                turn = new Turn { Kind = "confront", A = p.Id, NameA = p.Name, B = known.Cheater, NameB = Name(known.Cheater), Partner = known.With, PartnerName = known.WithName,
                                  SecretId = known.Id, Breakup = breakup };
                notes.Add($"{p.Name} found out that {turn.NameB} was flirting with {known.WithName} (day {known.Day}{(known.Seen ? ", and saw it" : ", people talk")}). " +
                          $"{p.Name} brings it up here: hurt, and angry about it. " +
                          (breakup ? $"By the end, {p.Name} breaks up with {turn.NameB}." : "They have it out, and stay together, for now."));
                break;
            }
            // A jealous partner, with the one their partner flirted with, here.
            if (turn == null)
                foreach (var g in Grudges().Where(g => ids.Contains(g.Partner) && ids.Contains(g.With)))
                {
                    turn = new Turn { Kind = "jealous", A = g.Partner, NameA = Name(g.Partner), B = g.With, NameB = Name(g.With), SecretId = g.Id };
                    notes.Add($"{turn.NameA} knows {turn.NameB} was flirting with {(g.Cheater == Player ? "their partner (the newcomer)" : g.CheaterName)}. " +
                              $"{turn.NameA} is cold and jealous toward {turn.NameB}: pointed remarks, maybe a scene. Words only, nothing physical.");
                    break;
                }
            // Someone with a partner who isn't here flirts with someone else.
            if (turn == null)
                foreach (var p in people)
                {
                    var mine = couples.FirstOrDefault(c => c.A == p.Id || c.B == p.Id);
                    if (mine == null || ids.Contains(Other(mine, p.Id))) continue;
                    var other = people.FirstOrDefault(o => o.Id != p.Id && (pair(p.Id, o.Id)?.Affinity ?? 0) >= 35 && couples.All(c => c.A != o.Id && c.B != o.Id || !ids.Contains(Other(c, o.Id))));
                    if (other == null || rng.NextDouble() >= CheatChance) continue;
                    turn = new Turn { Kind = "cheat", A = p.Id, NameA = p.Name, B = other.Id, NameB = other.Name, Partner = Other(mine, p.Id), PartnerName = OtherName(mine, p.Id) };
                    notes.Add($"{p.Name} is with {turn.PartnerName} (not here), but flirts with {other.Name} in this conversation; {other.Name} notices. Keep it light, a little guilty.");
                    break;
                }
        }
        // Two who've grown close get together.
        if (turn == null && residentsDate)
        {
            for (int i = 0; i < people.Count && turn == null; i++)
                for (int j = i + 1; j < people.Count && turn == null; j++)
                {
                    var a = people[i];
                    var b = people[j];
                    if (couples.Any(c => c.A == a.Id || c.B == a.Id || c.A == b.Id || c.B == b.Id)) continue;
                    var rel = pair(a.Id, b.Id);
                    if (rel == null || rel.Affinity < 50 || rel.Times < 3 || rng.NextDouble() >= SparkChance) continue;
                    turn = new Turn { Kind = "spark", A = a.Id, NameA = a.Name, B = b.Id, NameB = b.Name };
                    notes.Add($"{a.Name} and {b.Name} have been getting closer. In this conversation they flirt a little, and by the end {a.Name} asks {b.Name} out and {b.Name} says yes.");
                }
        }
        return (notes, turn);
    }

    /// <summary>A turn the conversation played out (enough of it was heard): the book follows it.</summary>
    public void Apply(Turn turn, int day)
    {
        switch (turn.Kind)
        {
            case "spark":
                Start(turn.A, turn.NameA, turn.B, turn.NameB, day);
                break;
            case "cheat":
                NoteSecret(turn.A, turn.NameA, turn.B, turn.NameB, turn.Partner, day, seen: false);
                break;
            case "confront":
                if (turn.Breakup) End(turn.A, turn.B);
                Settle(turn.SecretId, grudge: true);
                break;
            case "jealous":
                EndGrudge(turn.SecretId);
                break;
        }
    }
}
