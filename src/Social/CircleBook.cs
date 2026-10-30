using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NivalisAIChat.Social;

/// <summary>
/// FB-35: groups of three or four residents who've talked together ("circles"), remembered as a group: who, how many times,
/// what they talked about last, and how it went for each. Two people are a pair (<see cref="PairBook"/>). One small JSON
/// file, written like the pairs.
/// </summary>
public sealed class CircleBook
{
    public sealed class Circle
    {
        /// <summary>Their ids, sorted (the key, joined with "|").</summary>
        public List<string> Ids { get; set; } = new();
        /// <summary>Their names, in the same order.</summary>
        public List<string> Names { get; set; } = new();
        public int Times { get; set; }
        public int LastDay { get; set; } = -1;
        public DateTime LastUtc { get; set; }
        /// <summary>What they talked about last time, in a sentence.</summary>
        public string LastTalk { get; set; } = "";
        /// <summary>How it went for each of them last time (by id): +1 they enjoyed it, 0 alright, -1 they didn't.</summary>
        public Dictionary<string, int> How { get; set; } = new();

        public Circle Copy() => new()
        {
            Ids = Ids.ToList(), Names = Names.ToList(), Times = Times, LastDay = LastDay, LastUtc = LastUtc, LastTalk = LastTalk, How = new Dictionary<string, int>(How),
        };

        public string NameOf(string id) { int i = Ids.IndexOf(id); return i >= 0 && i < Names.Count ? Names[i] : ""; }
    }

    /// <summary>Most circles kept: past it, the ones met once go first, then the longest ago.</summary>
    public const int MaxCircles = 200;
    /// <summary>Parts of one evening at a table aren't separate times: another time counts on another day or after this long.</summary>
    public static readonly TimeSpan SameSitting = TimeSpan.FromMinutes(30);

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, Circle> _circles;
    private readonly Action<string>? _warn;
    private bool _readOnly, _warnedReadOnly, _dirty;
    private readonly Npc.BookWriter _writer;

    public CircleBook(string path, Action<string>? warn = null)
    {
        _path = path;
        _warn = warn;
        _circles = Npc.DataFile.LoadBook(_path, t => JsonSerializer.Deserialize<Dictionary<string, Circle>>(t, Npc.NpcStore.Json), _warn, out _readOnly)
                   ?? new Dictionary<string, Circle>();
        foreach (var k in _circles.Where(kv => kv.Value == null || kv.Value.Ids == null || kv.Value.Ids.Count < 3).Select(kv => kv.Key).ToList()) _circles.Remove(k);
        foreach (var c in _circles.Values) { c.Names ??= new(); c.How ??= new(); c.LastTalk ??= ""; }
        _writer = new Npc.BookWriter(_path, Snapshot, _warn, failed: () => { lock (_lock) _dirty = true; });
    }

    internal Npc.BookWriter Writer => _writer;

    public static string Key(IEnumerable<string> ids) => string.Join("|", ids.OrderBy(x => x, StringComparer.Ordinal));

    private string? Snapshot()
    {
        Dictionary<string, Circle> copy;
        lock (_lock)
        {
            if (!_dirty) return null;
            if (_readOnly) { Npc.DataFile.NotSaved(_path, _warn, ref _warnedReadOnly); return null; }
            copy = _circles.ToDictionary(kv => kv.Key, kv => kv.Value.Copy());
            _dirty = false;
        }
        return JsonSerializer.Serialize(copy, Npc.NpcStore.Json);
    }

    /// <summary>A hot reload: what's waiting is written, and then nothing more.</summary>
    public void Close()
    {
        try { Flush(); }
        finally { _writer.Close(); }
    }

    public void Flush()
    {
        _writer.WaitIdle();
        _writer.Write();
    }

    /// <summary>
    /// They talked as a group (3 or 4 of them who spoke): counted (once a sitting), what it was about, how it went for each.
    /// <paramref name="allCurrent"/> is asked under the book's lock: false (someone was reset since it was written) = not kept.
    /// </summary>
    public bool Record(IReadOnlyList<(string id, string name, int how)> members, int day, string summary, Func<bool> allCurrent, DateTime? nowUtc = null)
    {
        var people = members.Where(m => m.id.Length > 0).GroupBy(m => m.id).Select(g => g.First()).OrderBy(m => m.id, StringComparer.Ordinal).ToList();
        if (people.Count is < 3 or > 6) return false;
        var now = nowUtc ?? DateTime.UtcNow;
        string key = Key(people.Select(m => m.id));
        lock (_lock)
        {
            if (!allCurrent()) return false;
            if (!_circles.TryGetValue(key, out var c)) _circles[key] = c = new Circle { Ids = people.Select(m => m.id).ToList() };
            if (c.Times == 0 || c.LastDay != day || now - c.LastUtc > SameSitting) c.Times++;
            c.Names = people.Select(m => m.name).ToList();
            c.LastDay = day;
            c.LastUtc = now;
            if (!string.IsNullOrWhiteSpace(summary)) c.LastTalk = Ai.ReplyParser.Quotable(summary, 400); // goes back into their prompts
            c.How = people.ToDictionary(m => m.id, m => Math.Sign(m.how));
            while (_circles.Count > MaxCircles)
                _circles.Remove(_circles.OrderBy(kv => kv.Value.Times > 1).ThenBy(kv => kv.Value.LastDay).ThenBy(kv => kv.Value.LastUtc).First().Key);
            _dirty = true;
        }
        _writer.SaveSoon();
        return true;
    }

    /// <summary>
    /// The circle most of these people are in (at least two of them): the most of them present, then one that's exactly
    /// them, then the newest. A copy; null if none.
    /// </summary>
    public Circle? Best(ICollection<string> present)
    {
        var set = present as HashSet<string> ?? new HashSet<string>(present);
        lock (_lock)
        {
            return _circles.Values
                .Select(c => (c, here: c.Ids.Count(set.Contains)))
                .Where(x => x.here >= 2)
                .OrderByDescending(x => x.here).ThenByDescending(x => x.here == x.c.Ids.Count && x.c.Ids.Count == set.Count)
                .ThenByDescending(x => x.c.LastDay).ThenByDescending(x => x.c.LastUtc)
                .Select(x => x.c.Copy()).FirstOrDefault();
        }
    }

    /// <summary>The newest circle this person is in that has met at least <paramref name="minTimes"/> times (a copy), or null.</summary>
    public Circle? NewestFor(string id, int minTimes = 2)
    {
        lock (_lock)
            return _circles.Values.Where(c => c.Ids.Contains(id) && c.Times >= minTimes)
                .OrderByDescending(c => c.LastDay).ThenByDescending(c => c.LastUtc).Select(c => c.Copy()).FirstOrDefault();
    }

    /// <summary>Every circle with this person goes (their names are in what it says). True if there were any.</summary>
    public bool Forget(string id)
    {
        lock (_lock)
        {
            var gone = _circles.Where(kv => kv.Value.Ids.Contains(id)).Select(kv => kv.Key).ToList();
            if (gone.Count == 0) return false;
            foreach (var k in gone) _circles.Remove(k);
            _dirty = true;
        }
        _writer.SaveSoon();
        return true;
    }

    /// <summary>Forget every circle; the file is copied to <paramref name="backupDir"/> first (as <see cref="PairBook.Clear"/>).</summary>
    public void Clear(string backupDir)
    {
        _writer.Exclusive(() =>
        {
            bool readOnly;
            lock (_lock) readOnly = _readOnly;
            if (!readOnly) try { _writer.Write(); } catch { }
            lock (_lock)
            {
                if (File.Exists(_path))
                {
                    Directory.CreateDirectory(backupDir);
                    try { File.Copy(_path, Path.Combine(backupDir, Path.GetFileName(_path)), true); _readOnly = false; }
                    catch when (_readOnly) { }
                }
                _circles = new Dictionary<string, Circle>();
                _dirty = true;
            }
            _writer.Write();
        });
    }

    public int Count { get { lock (_lock) return _circles.Count; } }

    /// <summary>
    /// "[THEIR GROUP]" for a conversation (FB-35): who of them have talked together as a group before, how often, last time's
    /// talk and who enjoyed it. "" when there's nothing to say.
    /// </summary>
    public static string GroupLine(Circle c, ICollection<string> present)
    {
        string First(string name) => (name ?? "").Trim().Split(' ')[0];
        var here = c.Ids.Where(present.Contains).Select(id => First(c.NameOf(id))).Where(n => n.Length > 0).ToList();
        var away = c.Ids.Where(id => !present.Contains(id)).Select(id => First(c.NameOf(id))).Where(n => n.Length > 0).ToList();
        if (here.Count < 2) return "";
        string Listed(List<string> n) => n.Count == 1 ? n[0] : string.Join(", ", n.Take(n.Count - 1)) + " and " + n[^1];
        var sb = new System.Text.StringBuilder("[THEIR GROUP] ");
        sb.Append($"{Listed(here)} {(away.Count > 0 ? $"(with {Listed(away)}, not here now) " : "")}have talked together as a group {c.Times} time(s)");
        sb.Append(c.LastDay >= 0 ? $", last on day {c.LastDay}" : "");
        sb.Append(c.LastTalk.Length > 0 ? $": {c.LastTalk.TrimEnd('.')}." : ".");
        var liked = c.Ids.Where(id => c.How.TryGetValue(id, out var h) && h > 0).Select(id => First(c.NameOf(id))).Where(n => n.Length > 0).ToList();
        var not = c.Ids.Where(id => c.How.TryGetValue(id, out var h) && h < 0).Select(id => First(c.NameOf(id))).Where(n => n.Length > 0).ToList();
        if (liked.Count > 0) sb.Append($" {Listed(liked)} enjoyed it last time.");
        if (not.Count > 0) sb.Append($" {Listed(not)} didn't.");
        return sb.ToString();
    }
}
