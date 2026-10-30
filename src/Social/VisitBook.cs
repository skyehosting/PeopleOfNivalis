using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace NivalisAIChat.Social;

/// <summary>
/// How often each person has eaten at the player's places: one visit per game day at most. A regular (about ten visits)
/// knows who runs the place; anyone else doesn't, until the player tells them. One small JSON file.
/// </summary>
public sealed class VisitBook
{
    /// <summary>Visits after which a customer knows the player runs the place.</summary>
    public const int RegularAfter = 10;

    public sealed class Entry
    {
        public int Count { get; set; }
        public int LastDay { get; set; } = int.MinValue;
    }

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, Entry> _visits;
    private bool _dirty;
    private readonly Action<string>? _warn;
    // The file couldn't be read at the start (locked, or damaged and couldn't be moved aside): nothing is written over it.
    private bool _readOnly, _warnedReadOnly;
    private readonly Npc.BookWriter _writer;

    public VisitBook(string path, Action<string>? warn = null)
    {
        _path = path;
        _warn = warn;
        _visits = Npc.DataFile.LoadBook(_path, t => JsonSerializer.Deserialize<Dictionary<string, Entry>>(t, Npc.NpcStore.Json), _warn, out _readOnly)
                  ?? new Dictionary<string, Entry>();
        foreach (var k in _visits.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) _visits.Remove(k); // the full audit (A-007)
        _writer = new Npc.BookWriter(_path, Snapshot, _warn, failed: () => { lock (_lock) _dirty = true; });
    }

    /// <summary>Its writer (tests: waiting for it, and holding a write).</summary>
    internal Npc.BookWriter Writer => _writer;

    /// <summary>
    /// The visits as they are now, for the writer, if anything changed: copied under the lock (a few numbers each),
    /// written to text outside it (PERF-9). Null when nothing changed, or while it's read-only.
    /// </summary>
    private string? Snapshot()
    {
        Dictionary<string, Entry> copy;
        lock (_lock)
        {
            if (!_dirty) return null;
            if (_readOnly) { Npc.DataFile.NotSaved(_path, _warn, ref _warnedReadOnly); return null; }
            copy = _visits.ToDictionary(kv => kv.Key, kv => new Entry { Count = kv.Value.Count, LastDay = kv.Value.LastDay });
            _dirty = false;
        }
        return JsonSerializer.Serialize(copy, Npc.NpcStore.Json);
    }

    /// <summary>They're eating at the player's place today. Counts once per game day; true when it counted.</summary>
    public bool Note(string id, int gameDay)
    {
        if (string.IsNullOrEmpty(id)) return false;
        lock (_lock)
        {
            if (!_visits.TryGetValue(id, out var e)) _visits[id] = e = new Entry();
            if (e.LastDay == gameDay) return false;
            if (gameDay < e.LastDay) { e.LastDay = gameDay; return false; } // an older save: don't count backwards
            e.Count++;
            e.LastDay = gameDay;
            _dirty = true;
            return true;
        }
    }

    public int Count(string id)
    {
        lock (_lock) return _visits.TryGetValue(id, out var e) ? e.Count : 0;
    }

    /// <summary>If anything changed since the last save, it's written on a pool thread: the main thread never waits on the disk (PERF-9).</summary>
    public void SaveSoon()
    {
        lock (_lock) if (!_dirty) return;
        _writer.SaveSoon();
    }

    /// <summary>Writes the file now if anything changed since the last save (after any write in progress).</summary>
    public void Save() => _writer.Write();

    /// <summary>A hot reload: what's waiting is written, and then nothing more (the new copy has the file; audit step 7b).</summary>
    public void Close()
    {
        try { Flush(); }
        finally { _writer.Close(); }
    }

    /// <summary>Waits for the pool's write, then writes what's still waiting (a hot reload, tests).</summary>
    public void Flush()
    {
        _writer.WaitIdle();
        _writer.Write();
    }

    public void Forget(string id)
    {
        lock (_lock) { if (_visits.Remove(id)) _dirty = true; }
        SaveSoon(); // after any write in progress, so an older state can't bring them back
    }

    /// <summary>
    /// Forget every visit (everyone forgets the player); the old file is copied to <paramref name="backupDir"/> first, with
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
                _visits = new Dictionary<string, Entry>();
                _dirty = true;
            }
            _writer.Write();
        });
    }

    public int People { get { lock (_lock) return _visits.Count; } }
    public int Regulars { get { lock (_lock) return _visits.Values.Count(v => v.Count >= RegularAfter); } }
}
