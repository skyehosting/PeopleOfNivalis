using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Mind;

/// <summary>
/// The inner-life round: each resident's inner life as the code keeps it, per save (minds.json, part of FB-61's snapshot):
/// how they feel underneath (Affect), what's on their mind (Preoccupation), and their phrase ledger (PhraseLedger). Written
/// on a pool thread (BookWriter), read under its lock from any thread. Never anyone under 18. No Unity, tested.
/// </summary>
public sealed class MindBook
{
    public sealed class Mind
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public AffectState Affect { get; set; } = new();
        public Thought? Current { get; set; }
        public List<string> Openers { get; set; } = new();
        public List<string> Lines { get; set; } = new();
        /// <summary>The game minute they were last thought about (who goes first when there are too many).</summary>
        public long Touched { get; set; }
    }

    public sealed class Data
    {
        public Dictionary<string, Mind> People { get; set; } = new();
    }

    public const int MaxPeople = 600;

    private readonly string _path;
    private readonly object _lock = new();
    private Data _data;
    private readonly Action<string>? _warn;
    private bool _readOnly, _warnedReadOnly, _dirty;
    private readonly BookWriter _writer;
    public Func<string, bool> IsMinor { get; set; } = _ => false;

    public MindBook(string path, Action<string>? warn = null)
    {
        _path = path;
        _warn = warn;
        _data = DataFile.LoadBook(_path, t => JsonSerializer.Deserialize<Data>(t, NpcStore.Json), _warn, out _readOnly) ?? new Data();
        _data.People ??= new();
        foreach (var m in _data.People.Values.Where(m => m != null))
        {
            // A hand-edited or older file: nothing null, nothing unreadable is read back.
            m.Id ??= ""; m.Name ??= "";
            m.Affect ??= new AffectState();
            m.Affect.Moodlets ??= new();
            m.Affect.Moodlets.RemoveAll(x => x == null);
            foreach (var x in m.Affect.Moodlets) { x.Key ??= ""; x.Text ??= ""; x.Mood ??= ""; if (!float.IsFinite(x.V)) x.V = 0; if (!float.IsFinite(x.A)) x.A = 0; }
            m.Affect.Lead ??= "";
            if (!float.IsFinite(m.Affect.V) || !float.IsFinite(m.Affect.A) || !float.IsFinite(m.Affect.BaseV) || !float.IsFinite(m.Affect.BaseA))
                m.Affect = new AffectState();
            if (m.Current != null) { m.Current.Kind ??= ""; m.Current.Key ??= ""; m.Current.Text ??= ""; }
            m.Openers ??= new();
            m.Openers.RemoveAll(x => x == null);
            m.Lines ??= new();
            m.Lines.RemoveAll(x => x == null);
        }
        foreach (var k in _data.People.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) _data.People.Remove(k);
        _writer = new BookWriter(_path, Snapshot, _warn, failed: () => { lock (_lock) _dirty = true; });
    }

    internal BookWriter Writer => _writer;

    private string? Snapshot()
    {
        string text;
        lock (_lock)
        {
            if (!_dirty) return null;
            if (_readOnly) { DataFile.NotSaved(_path, _warn, ref _warnedReadOnly); return null; }
            text = JsonSerializer.Serialize(_data, NpcStore.Json); // written whole under the lock: a few hundred small entries
            _dirty = false;
        }
        return text;
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

    public int Count { get { lock (_lock) return _data.People.Count; } }

    public bool Has(string id) { lock (_lock) return _data.People.ContainsKey(id); }

    /// <summary>Their entry (made at their resting point when it's new), their resting point kept up to date. Under the lock.</summary>
    private Mind? Get(string id, string name, (float v, float a)? baseline, long now)
    {
        if (id.Length == 0 || IsMinor(id) || now < 0) return null;
        if (!_data.People.TryGetValue(id, out var m))
        {
            var b = baseline ?? (0f, Voice.Tone.CalmArousal);
            // Review (core IL-5): touched now before anyone goes, and never the one just made.
            _data.People[id] = m = new Mind { Id = id, Name = WitnessBook.Cut(name, 60), Affect = Affect.Fresh(b, now), Touched = now };
            Evict(keep: id);
        }
        if (name.Length > 0) m.Name = WitnessBook.Cut(name, 60);
        if (baseline is { } bl) { m.Affect.BaseV = bl.v; m.Affect.BaseA = bl.a; }
        m.Touched = Math.Max(m.Touched, now);
        return m;
    }

    // ------------------------------------------------------------------ how they feel

    /// <summary>A line said (or a moment lived) feeling <paramref name="mood"/>.</summary>
    public void Felt(string id, string name, (float v, float a)? baseline, string mood, string intensity, long now, float step = Affect.FeelStep)
    {
        lock (_lock)
        {
            var m = Get(id, name, baseline, now);
            if (m == null) return;
            Affect.Feel(m.Affect, mood, intensity, now, step);
            Changed();
        }
    }

    /// <summary>Something that stays with them a while.</summary>
    public void Add(string id, string name, (float v, float a)? baseline, Moodlet moodlet, long now)
    {
        lock (_lock)
        {
            var m = Get(id, name, baseline, now);
            if (m == null) return;
            moodlet.Text = WitnessBook.Cut(moodlet.Text, 120);
            Affect.Add(m.Affect, moodlet, now);
            Changed();
        }
    }

    /// <summary>How they feel underneath now, when it's carried into how they are (Affect.Label); null when it isn't, or for someone new.</summary>
    public (string mood, string intensity, string reason)? MoodNow(string id, long now)
    {
        lock (_lock)
        {
            if (now < 0 || IsMinor(id) || !_data.People.TryGetValue(id, out var m)) return null;
            long before = m.Affect.At;
            var label = Affect.Label(m.Affect, now);
            if (m.Affect.At != before) _dirty = true; // settled: written with the next change
            return label;
        }
    }

    /// <summary>Their feelings' point and resting point now (body language, turn-taking); null for someone new.</summary>
    public (float v, float a, float baseV, float baseA)? Point(string id, long now)
    {
        lock (_lock)
        {
            if (now < 0 || IsMinor(id) || !_data.People.TryGetValue(id, out var m)) return null;
            Affect.Settle(m.Affect, now);
            return (m.Affect.V, m.Affect.A, m.Affect.BaseV, m.Affect.BaseA);
        }
    }

    /// <summary>What's still with them (copies).</summary>
    public List<Moodlet> Moodlets(string id, long now)
    {
        lock (_lock)
        {
            if (now < 0 || IsMinor(id) || !_data.People.TryGetValue(id, out var m)) return new List<Moodlet>();
            Affect.Settle(m.Affect, now);
            return m.Affect.Moodlets.Select(x => new Moodlet { Key = x.Key, Text = x.Text, V = x.V, A = x.A, Since = x.Since, Until = x.Until, Mood = x.Mood }).ToList();
        }
    }

    // ------------------------------------------------------------------ what's on their mind

    public Thought? Current(string id)
    {
        lock (_lock)
        {
            if (IsMinor(id) || !_data.People.TryGetValue(id, out var m) || m.Current == null) return null;
            var t = m.Current;
            return new Thought { Kind = t.Kind, Key = t.Key, Text = t.Text, Score = t.Score, Since = t.Since };
        }
    }

    /// <summary>What's on their mind now (from Preoccupation.Choose; null: nothing).</summary>
    public void Think(string id, string name, Thought? thought, long now)
    {
        lock (_lock)
        {
            var m = Get(id, name, null, now);
            if (m == null) return;
            var old = m.Current;
            m.Current = thought == null ? null : new Thought { Kind = thought.Kind, Key = thought.Key, Text = WitnessBook.Cut(thought.Text, 140), Score = thought.Score, Since = thought.Since };
            if (old?.Key != m.Current?.Key || old?.Text != m.Current?.Text) Changed();
        }
    }

    // ------------------------------------------------------------------ the phrase ledger

    /// <summary>A line they said: its opener and the line, kept for the ledger.</summary>
    public void Said(string id, string name, string line, long now)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_lock)
        {
            var m = Get(id, name, null, now);
            if (m == null) return;
            string key = PhraseLedger.OpenerKey(line);
            if (key.Length > 0) { m.Openers.Add(key); while (m.Openers.Count > PhraseLedger.Openers) m.Openers.RemoveAt(0); }
            m.Lines.Add(PhraseLedger.Kept(line));
            while (m.Lines.Count > PhraseLedger.Lines) m.Lines.RemoveAt(0);
            Changed();
        }
    }

    public (List<string> openers, List<string> lines) Phrases(string id)
    {
        lock (_lock)
        {
            if (IsMinor(id) || !_data.People.TryGetValue(id, out var m)) return (new List<string>(), new List<string>());
            return (m.Openers.ToList(), m.Lines.ToList());
        }
    }

    // ------------------------------------------------------------------ book-keeping

    /// <summary>
    /// Their entry goes, and (review core IL-4: someone found to be under 18 leaves no trace) anything in anyone else's mind
    /// about them: a moodlet kept with them (its key ends in their id), or a thought or moodlet that names them by
    /// <paramref name="name"/> (their first name, as a word).
    /// </summary>
    public void Forget(string id, string name = "")
    {
        if (id.Length == 0) return;
        string first = Ai.Prompts.FirstName(name ?? "");
        var named = first.Length >= 2 ? new System.Text.RegularExpressions.Regex($@"(?i)(?<![\p{{L}}]){System.Text.RegularExpressions.Regex.Escape(first)}(?![\p{{L}}])") : null;
        bool Names(string? text) => named != null && text != null && named.IsMatch(text);
        lock (_lock)
        {
            bool any = _data.People.Remove(id);
            foreach (var m in _data.People.Values)
            {
                any |= m.Affect.Moodlets.RemoveAll(x => x.Key.EndsWith(":" + id, StringComparison.Ordinal) || Names(x.Text)) > 0;
                if (m.Current != null && (m.Current.Key.EndsWith(":" + id, StringComparison.Ordinal) || Names(m.Current.Text))) { m.Current = null; any = true; } // review IL-4b: exact
                any |= m.Lines.RemoveAll(l => Names(l)) > 0;
            }
            if (any) Changed();
        }
    }

    public void Clear()
    {
        lock (_lock) { _data = new Data(); Changed(); }
    }

    /// <summary>Everyone, for /inner stats (copies of what matters).</summary>
    public List<(string id, string name, float v, float a, int moodlets, string thought)> All(long now)
    {
        lock (_lock)
        {
            return _data.People.Values.Select(m =>
            {
                Affect.Settle(m.Affect, now);
                return (m.Id, m.Name, m.Affect.V, m.Affect.A, m.Affect.Moodlets.Count, m.Current?.Text ?? "");
            }).ToList();
        }
    }

    private void Evict(string keep)
    {
        if (_data.People.Count <= MaxPeople) return;
        foreach (var k in _data.People.Where(kv => kv.Key != keep).OrderBy(kv => kv.Value.Touched).Take(_data.People.Count - MaxPeople).Select(kv => kv.Key).ToList())
            _data.People.Remove(k);
    }
}
