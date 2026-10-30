using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace NivalisAIChat.Social;

/// <summary>
/// How pairs of residents get on with each other, from the conversations they've had (NPC chatter). People who like
/// each other end up talking more often; people who don't, less, though it can still happen. One small JSON file.
/// </summary>
public sealed class PairBook
{
    public sealed class Pair
    {
        public string A { get; set; } = "";
        public string B { get; set; } = "";
        public string NameA { get; set; } = "";
        public string NameB { get; set; } = "";
        /// <summary>-100 can't stand each other .. +100 close friends: kept in step with <see cref="Relationship"/>.</summary>
        public int Affinity
        {
            get => _affinity;
            set
            {
                _affinity = value;
                if (_relationship >= 0 && Npc.Relationships.ToAffinity(_relationship) != value) _relationship = Npc.Relationships.FromAffinity(value);
            }
        }
        private int _affinity;

        /// <summary>
        /// How they get on, on the same 0–100% bar as the player's (50% neutral); worked out from Affinity for an older
        /// file, and when the value in a file is blank or broken.
        /// </summary>
        [System.Text.Json.Serialization.JsonConverter(typeof(Npc.RelationshipJson))]
        public double Relationship
        {
            get => _relationship >= 0 ? _relationship : Npc.Relationships.FromAffinity(_affinity);
            set
            {
                if (!double.IsFinite(value) || value < 0) { _relationship = -1; return; }
                _relationship = Math.Round(Math.Clamp(value, 0, 100), Npc.Relationships.Decimals);
                _affinity = Npc.Relationships.ToAffinity(_relationship);
            }
        }
        private double _relationship = -1;

        public int Times { get; set; }
        public int LastDay { get; set; } = -1;
        /// <summary>What they talked about last time, in a sentence.</summary>
        public string LastTalk { get; set; } = "";

        public Pair Copy() => (Pair)MemberwiseClone();
    }

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, Pair> _pairs;
    private readonly Action<string>? _warn;
    // The file couldn't be read at the start (locked, or damaged and couldn't be moved aside): nothing is written over it.
    private bool _readOnly, _warnedReadOnly;
    // Changed since the last write: Flush (a hot reload) writes only then, so a hand edit isn't written over for nothing.
    private bool _dirty;
    private readonly Npc.BookWriter _writer;

    public PairBook(string path, Action<string>? warn = null)
    {
        _path = path;
        _warn = warn;
        _pairs = Npc.DataFile.LoadBook(_path, t => JsonSerializer.Deserialize<Dictionary<string, Pair>>(t, Npc.NpcStore.Json), _warn, out _readOnly)
                 ?? new Dictionary<string, Pair>();
        _writer = new Npc.BookWriter(_path, Snapshot, _warn, failed: () => { lock (_lock) _dirty = true; });
    }

    /// <summary>Its writer (tests: waiting for it, and holding a write).</summary>
    internal Npc.BookWriter Writer => _writer;

    public static string Key(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;

    /// <summary>
    /// The book as it is now, for its writer: copied under the lock (the main thread reads through it), written to text
    /// outside it. Null while it's read-only.
    /// </summary>
    private string? Snapshot()
    {
        Dictionary<string, Pair> copy;
        lock (_lock)
        {
            if (!_dirty) return null;
            if (_readOnly) { Npc.DataFile.NotSaved(_path, _warn, ref _warnedReadOnly); return null; }
            copy = _pairs.ToDictionary(kv => kv.Key, kv => kv.Value.Copy());
            _dirty = false;
        }
        return JsonSerializer.Serialize(copy, Npc.NpcStore.Json);
    }

    /// <summary>A hot reload: what's waiting is written, and then nothing more (the new copy has the file; audit step 7b).</summary>
    public void Close()
    {
        try { Flush(); }
        finally { _writer.Close(); }
    }

    /// <summary>Writes what's waiting now (a hot reload, tests).</summary>
    public void Flush()
    {
        _writer.WaitIdle();
        _writer.Write();
    }

    public Pair? Get(string a, string b)
    {
        lock (_lock) return _pairs.TryGetValue(Key(a, b), out var p) ? p : null;
    }

    /// <summary>
    /// How far a conversation moves a pair's bar, from the AI's −10..10 rating of it: the player's per-reply amounts, once
    /// per conversation (the author: the same slow pace). Set from the settings by the mod; these are its defaults.
    /// </summary>
    public Func<int, double> Step { get; set; } = rating => Npc.Relationships.Step(rating, 0.5, 0.1, 0.5);

    /// <summary>They just talked: count it, nudge how they get on (<paramref name="change"/> is the AI's −10..10), remember what it was about.</summary>
    public void Record(string a, string nameA, string b, string nameB, int change, int day, string summary)
    {
        if (a == b) return;
        lock (_lock)
        {
            string k = Key(a, b);
            if (!_pairs.TryGetValue(k, out var p))
            {
                bool aFirst = string.CompareOrdinal(a, b) <= 0;
                _pairs[k] = p = new Pair { A = aFirst ? a : b, B = aFirst ? b : a };
            }
            if (p.A == a) { p.NameA = nameA; p.NameB = nameB; } else { p.NameA = nameB; p.NameB = nameA; }
            p.Relationship = Npc.Relationships.Moved(p.Relationship, Step(change));
            p.Times++;
            p.LastDay = day;
            if (!string.IsNullOrWhiteSpace(summary)) p.LastTalk = Ai.ReplyParser.Quotable(summary, 400); // goes back into their prompts
            _dirty = true;
        }
        _writer.SaveSoon(); // on a pool thread: nobody waits on the disk (LEAK-4)
    }

    /// <summary>Pairs where both people are among <paramref name="ids"/> (the people near the player right now).</summary>
    public List<Pair> AmongThese(ICollection<string> ids)
    {
        var set = ids as HashSet<string> ?? new HashSet<string>(ids);
        lock (_lock) return _pairs.Values.Where(p => set.Contains(p.A) && set.Contains(p.B)).ToList();
    }

    /// <summary>Everyone this person has talked with, as pairs.</summary>
    public List<Pair> For(string id)
    {
        lock (_lock) return _pairs.Values.Where(p => p.A == id || p.B == id).ToList();
    }

    /// <summary>How likely a pair is picked to talk again: friends often, enemies rarely (never quite zero).</summary>
    public static double Weight(Pair? p) => p == null ? 1.0 : Math.Clamp(1.0 + p.Affinity / 40.0, 0.15, 3.0);

    /// <summary>Everyone's pairs with this person go. True if there were any (the file is saved only then).</summary>
    public bool Forget(string id)
    {
        lock (_lock)
        {
            var gone = _pairs.Where(kv => kv.Value.A == id || kv.Value.B == id).Select(kv => kv.Key).ToList();
            if (gone.Count == 0) return false;
            foreach (var k in gone) _pairs.Remove(k);
            _dirty = true;
        }
        _writer.SaveSoon(); // written after any write in progress, so an older state can't bring them back
        return true;
    }

    /// <summary>
    /// Forget every pair (everyone forgets each other); the file is copied to <paramref name="backupDir"/> first, with
    /// every change so far written into it. Nothing else writes meanwhile, so the empty book is the last word.
    /// </summary>
    public void Clear(string backupDir)
    {
        _writer.Exclusive(() =>
        {
            bool readOnly;
            lock (_lock) readOnly = _readOnly;
            if (!readOnly) try { _writer.Write(); } catch { } // what's only in memory so far goes into the backup too
            lock (_lock)
            {
                if (File.Exists(_path))
                {
                    Directory.CreateDirectory(backupDir);
                    // Kept in the backup: safe to write over now, even if it couldn't be read at the start.
                    try { File.Copy(_path, Path.Combine(backupDir, Path.GetFileName(_path)), true); _readOnly = false; }
                    catch when (_readOnly) { } // still locked: forgotten for this session only; the file is left as it is
                }
                _pairs = new Dictionary<string, Pair>();
                _dirty = true;
            }
            _writer.Write();
        });
    }

    public int Count { get { lock (_lock) return _pairs.Count; } }
}
