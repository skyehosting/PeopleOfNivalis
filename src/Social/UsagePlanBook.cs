using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace NivalisAIChat.Social;

/// <summary>
/// FB-41: what the Usage Optimizer keeps between sessions (usage_plan.json): your headroom, your play-hours a day if you set
/// them, the settings you pinned, and the last plan applied (each setting's value then, and before, for Undo). A setting that
/// no longer has the value the plan gave it was changed after it: shown with an amber "changed" tag, and kept as you set it
/// (pinned) until the next Apply or Undo. No Unity here.
/// </summary>
public sealed class UsagePlanBook
{
    public sealed class Data
    {
        public double Headroom { get; set; } = 0.10;
        /// <summary>Play-hours a day to plan with (0: learned from your play).</summary>
        public double HoursPerDay { get; set; }
        public List<string> Pins { get; set; } = new();
        /// <summary>The level the dial was left at (null: the best level, worked out each time).</summary>
        public double? Dial { get; set; }
        /// <summary>The last Apply: each setting's value it wrote, and the value before (for Undo). Voice chances by provider too.</summary>
        public Dictionary<string, string> Applied { get; set; } = new();
        public Dictionary<string, string> Before { get; set; } = new();
        public DateTime? AppliedAt { get; set; }
        /// <summary>Residents' talk kept on pace between plans (stage 4).</summary>
        public bool Pacing { get; set; } = true;
    }

    public const double MinHeadroom = 0, MaxHeadroom = 0.5;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string? _path;
    private readonly Action<string>? _warn;
    private Data _data = new();
    private bool _readOnly, _warnedReadOnly;

    public UsagePlanBook(string? path = null, Action<string>? warn = null)
    {
        _path = path;
        _warn = warn;
        if (path == null) return;
        var read = Npc.DataFile.LoadBook(path, t => JsonSerializer.Deserialize<Data>(t, Json), warn, out _readOnly);
        if (read != null) _data = read;
        _data.Pins ??= new();
        _data.Applied ??= new();
        _data.Before ??= new();
        _data.Pins.RemoveAll(p => p == null);
        _data.Headroom = Clean(_data.Headroom, 0.10, MinHeadroom, MaxHeadroom);
        _data.HoursPerDay = Clean(_data.HoursPerDay, 0, 0, 24);
        if (_data.Dial is double d) _data.Dial = Clean(d, 1, 0, 1);
    }

    private static double Clean(double v, double fallback, double min, double max) => double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

    public double Headroom
    {
        get { lock (_lock) return _data.Headroom; }
        set { lock (_lock) _data.Headroom = Clean(value, 0.10, MinHeadroom, MaxHeadroom); Save(); }
    }

    public double HoursPerDay
    {
        get { lock (_lock) return _data.HoursPerDay; }
        set { lock (_lock) _data.HoursPerDay = Clean(value, 0, 0, 24); Save(); }
    }

    public double? Dial
    {
        get { lock (_lock) return _data.Dial; }
        set { lock (_lock) _data.Dial = value is double d ? Clean(d, 1, 0, 1) : null; Save(); }
    }

    public DateTime? AppliedAt { get { lock (_lock) return _data.AppliedAt; } }

    public bool Pacing
    {
        get { lock (_lock) return _data.Pacing; }
        set { lock (_lock) _data.Pacing = value; Save(); }
    }

    public bool Pinned(string lever) { lock (_lock) return _data.Pins.Contains(lever); }

    public void Pin(string lever, bool on)
    {
        lock (_lock)
        {
            _data.Pins.Remove(lever);
            if (on) _data.Pins.Add(lever);
        }
        Save();
    }

    /// <summary>The value the last Apply gave this setting (null: it didn't set it).</summary>
    public string? AppliedValue(string lever) { lock (_lock) return _data.Applied.TryGetValue(lever, out var v) ? v : null; }

    /// <summary>The setting was changed after the plan set it (its value isn't the plan's any more).</summary>
    public bool Changed(string lever, string now) { lock (_lock) return _data.Applied.TryGetValue(lever, out var v) && v != now; }

    /// <summary>What the optimizer leaves as it is: your pins, and every setting changed since the plan.</summary>
    public HashSet<string> Kept(IEnumerable<string> levers, Func<string, string> now)
    {
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in levers) if (Pinned(l) || Changed(l, now(l))) kept.Add(l);
        return kept;
    }

    /// <summary>An Apply: what it wrote, and what each was before (only the settings it changed).</summary>
    public void Record(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after, DateTime when)
    {
        lock (_lock)
        {
            _data.Applied = new Dictionary<string, string>(after);
            _data.Before = new Dictionary<string, string>(before);
            _data.AppliedAt = when;
        }
        Save();
    }

    /// <summary>What Undo puts back: the values before the last Apply, for settings still as it left them (a setting changed since stays yours).</summary>
    public Dictionary<string, string> UndoValues(Func<string, string> now)
    {
        lock (_lock)
            return _data.Before.Where(kv => _data.Applied.TryGetValue(kv.Key, out var a) && a == now(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public bool CanUndo { get { lock (_lock) return _data.Before.Count > 0; } }

    /// <summary>The plan forgotten (after Undo): nothing is "changed" any more.</summary>
    public void Forget()
    {
        lock (_lock)
        {
            _data.Applied.Clear();
            _data.Before.Clear();
            _data.AppliedAt = null;
        }
        Save();
    }

    private void Save()
    {
        if (_path == null) return;
        if (_readOnly) { Npc.DataFile.NotSaved(_path, _warn, ref _warnedReadOnly); return; }
        // Off the game's thread; a newer save waits for this one, so the last one written is the newest. The snapshot is taken
        // and its write queued under one lock (audit 2026-10-14: taken apart, two saves at once could queue the older one last).
        lock (_writeLock)
        {
            string text;
            lock (_lock) text = JsonSerializer.Serialize(_data, Json);
            _pending = _pending.ContinueWith(_ =>
            {
                try { Npc.DataFile.WriteAtomic(_path, text, durable: true); } // off the game's thread: to the disk first (FB-51)
                catch (Exception e) { _warn?.Invoke("The usage plan couldn't be saved: " + e.GetType().Name); }
            });
        }
    }

    private readonly object _writeLock = new();
    private Task _pending = Task.CompletedTask;

    /// <summary>Every save written (the game closing; tests).</summary>
    public void Flush()
    {
        Task t;
        lock (_writeLock) t = _pending;
        try { t.Wait(2000); } catch { }
    }
}
