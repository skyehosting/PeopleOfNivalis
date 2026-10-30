using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace NivalisAIChat.Npc;

/// <summary>One thing a person remembers, as it's searched by meaning (with MemorySearch = Voyage).</summary>
public sealed class MemoryEntry
{
    /// <summary>Stable across rebuilds, so each memory is sent to Voyage once.</summary>
    public string Key { get; set; } = "";
    public int Day { get; set; }
    public string Clock { get; set; } = "";
    /// <summary>"you" (with the player), "meeting" (their diary of it), "talk", "phone", "thought", "life".</summary>
    public string Kind { get; set; } = "";
    /// <summary>Who else was in it.</summary>
    public string With { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Its vector (float32, base64).</summary>
    public string Vec { get; set; } = "";
}

/// <summary>A person's searchable memories, and which Voyage model and size made the vectors.</summary>
public sealed class MemoryFile
{
    public string Signature { get; set; } = "";
    public List<MemoryEntry> Entries { get; set; } = new();
}

/// <summary>
/// A person's memories as a timeline: every exchange with the player, their diary of each meeting, their talks with
/// other people (each person a branch of it), phone calls, thoughts and life events, each dated in game days. With
/// MemorySearch = Voyage they're found by meaning; either way, older ones come back less clearly (see <see cref="Clarity"/>).
/// No Unity here, so it can be tested.
/// </summary>
public static class Memories
{
    private const int MaxText = 600;

    /// <summary>Everything worth remembering in their file, as timeline entries with stable keys (no vectors yet).</summary>
    public static List<MemoryEntry> From(NpcRecord rec)
    {
        var list = new List<MemoryEntry>();
        var h = rec.History ?? new List<ChatLine>();
        for (int i = 0; i < h.Count; i++)
        {
            var l = h[i];
            if (l.Role == "scene" || string.IsNullOrWhiteSpace(l.Text)) continue;
            if (l.Role == "player")
            {
                // their words and the answer, as one moment
                var answer = i + 1 < h.Count && h[i + 1].Role == "npc" ? h[i + 1] : null;
                string text = $"They said: \"{l.Text.Trim()}\"" + (answer != null ? $" You said: \"{answer.Text.Trim()}\"" : "");
                list.Add(Entry("you", l.GameDay, l.Clock, "", text, "you:" + Hash($"{l.GameDay}|{l.Clock}|{l.Text}|{answer?.Text}")));
                if (answer != null) i++;
            }
            else
                list.Add(Entry("you", l.GameDay, l.Clock, "", $"You said: \"{l.Text.Trim()}\"", "you:" + Hash($"{l.GameDay}|{l.Clock}|npc|{l.Text}")));
        }
        foreach (var m in rec.Meetings ?? new List<Meeting>())
            if (!string.IsNullOrWhiteSpace(m.Summary))
                list.Add(Entry("meeting", m.GameDay, m.Clock, "", $"After talking with them{(m.Location.Length > 0 ? " in " + m.Location : "")}, you wrote: {m.Summary.Trim()}",
                    "meeting:" + Hash($"{m.GameDay}|{m.Clock}|{m.Summary}")));
        foreach (var t in rec.Talks ?? new List<Talk>())
        {
            string kind = t.Kind == Talk.Phone ? "phone" : t.Kind == Talk.Thought ? "thought" : t.Kind == Talk.Overheard ? "overheard" : t.Kind == Talk.Service ? "service" : "talk";
            string what = kind switch
            {
                "phone" => $"On the phone with {t.With}",
                "thought" => "Thinking out loud to yourself",
                "overheard" => "You overheard people nearby",
                "service" => $"At a food place with {t.With}",
                _ => $"Talking with {t.With}",
            };
            string where = t.Where.Length > 0 && kind != "phone" ? " in " + t.Where : "";
            string lines = t.Lines.Count > 0 ? " (" + string.Join(" / ", t.Lines.Take(6)) + ")" : "";
            list.Add(Entry(kind, t.GameDay, t.Clock, t.With, $"{what}{where}: {t.Summary.Trim()}{lines}", "talk:" + Hash($"{t.GameDay}|{t.Clock}|{t.Kind}|{t.With}|{t.Summary}" + (t.Kind == Talk.Overheard ? "|" + string.Join("/", t.Lines) : ""))));
        }
        foreach (var e in rec.LifeLog ?? new List<LifeEvent>())
            if (!string.IsNullOrWhiteSpace(e.Text))
                list.Add(Entry("life", e.GameDay, "", "", e.Text.Trim(), "life:" + Hash($"{e.GameDay}|{e.Text}")));
        // FB-36: meals out (work lines move to the latest day, so they'd be indexed again and again: left out).
        foreach (var d in rec.Doings ?? new List<Doing>())
            if (d.Kind == Doings.Meal && !string.IsNullOrWhiteSpace(d.Text))
                list.Add(Entry("life", d.GameDay, "", "", d.Text.Trim(), "doing:" + Hash($"{d.GameDay}|{d.Place}|{d.What}")));
        return list;
    }

    private static MemoryEntry Entry(string kind, int day, string clock, string with, string text, string key) => new()
    {
        Key = key, Day = day, Clock = clock ?? "", Kind = kind, With = with ?? "",
        Text = text.Length > MaxText ? text[..MaxText] : text,
    };

    /// <summary>FNV-1a, 64 bits, as hex: the same text always gives the same key.</summary>
    public static string Hash(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in s ?? "") { h ^= c; h *= 1099511628211UL; }
        return h.ToString("x16");
    }

    public static string Pack(float[] v) => Convert.ToBase64String(MemoryMarshal.AsBytes(v.AsSpan()));

    public static float[] Unpack(string b64)
    {
        if (string.IsNullOrEmpty(b64)) return Array.Empty<float>();
        try { return MemoryMarshal.Cast<byte, float>(Convert.FromBase64String(b64)).ToArray(); }
        catch { return Array.Empty<float>(); }
    }

    /// <summary>How close two vectors of length 1 are in meaning: 1 the same, 0 unrelated.</summary>
    public static float Closeness(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0f;
        float d = 0f;
        for (int i = 0; i < a.Length; i++) d += a[i] * b[i];
        return d;
    }

    /// <summary>The <paramref name="k"/> memories closest to <paramref name="query"/> (at least <paramref name="min"/> close).</summary>
    public static List<(MemoryEntry entry, float score)> Search(MemoryFile file, float[] query, int k, float min, Func<MemoryEntry, bool>? skip = null)
    {
        var scored = new List<(MemoryEntry, float)>();
        foreach (var e in file.Entries)
        {
            if (skip != null && skip(e)) continue;
            float s = Closeness(Unpack(e.Vec), query);
            if (s >= min) scored.Add((e, s));
        }
        return scored.OrderByDescending(x => x.Item2).Take(Math.Max(0, k)).ToList();
    }

    /// <summary>
    /// Whether a memory comes back at all, and how clearly, after <paramref name="ageDays"/> in-game days: a few days
    /// ago, clearly; up to two weeks, the gist; up to a month, vaguely (and not always); longer, barely if at all.
    /// <paramref name="roll"/> is a random number 0..1.
    /// </summary>
    public static (bool remembered, string clarity) Clarity(int ageDays, double roll)
    {
        int a = Math.Max(0, ageDays);
        if (a <= 3) return (true, "clear");
        if (a <= 14) return (true, "the gist, some details fuzzy");
        double keep = a <= 30 ? 1.0 - (a - 14) / 16.0 * 0.4 : Math.Max(0.2, 0.6 - (a - 30) / 60.0 * 0.4);
        if (roll > keep) return (false, "");
        return (true, a <= 30 ? "vague: roughly what happened, not the details" : "barely: just a faint sense of it");
    }

    /// <summary>
    /// What comes back to them, ready for the prompt: each memory with how clearly they recall it (with fading on),
    /// the forgotten ones left out, and how people talk about things they only half remember.
    /// </summary>
    public static string Format(IEnumerable<MemoryEntry> found, int today, bool fades, Random rng, string heading)
    {
        var sb = new StringBuilder();
        bool hazy = false;
        foreach (var e in found)
        {
            var (remembered, clarity) = fades ? Clarity(today - e.Day, rng.NextDouble()) : (true, "");
            if (!remembered) continue;
            if (clarity.Length > 0 && clarity != "clear") hazy = true;
            string when = $"Day {e.Day}{(e.Clock.Length > 0 ? ", " + e.Clock : "")}";
            sb.AppendLine($"- {(clarity.Length > 0 ? $"({clarity}) " : "")}{when}: {e.Text}");
        }
        if (sb.Length == 0) return "";
        string guide = hazy
            ? "\nWhat you only half remember, don't recite: say it the way people do (\"do you mean when we...?\", \"yeah, I remember that\", \"remind me?\") and let them fill in. Never repeat old words exactly."
            : "";
        return heading + guide + "\n" + sb.ToString().TrimEnd();
    }
}

/// <summary>Each person's searchable memories, one file apiece beside their record (BepInEx\config\NivalisAIChat\memory).</summary>
public sealed class MemoryStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _dir;
    private readonly object _io = new();
    // Goes up when someone's index is archived (/resetnpc; /resetall for everyone): an index made before that isn't saved.
    private readonly Dictionary<string, int> _generations = new();
    private int _allGeneration;
    private readonly Action<string>? _warn;
    private readonly HashSet<string> _warnedLocked = new();
    private bool _closed, _saidClosed; // under _io

    public MemoryStore(string dir, Action<string>? warn = null)
    {
        _dir = dir;
        _warn = warn;
        Directory.CreateDirectory(_dir);
    }

    private string PathFor(string id) => Path.Combine(_dir, NpcStore.FileNameFor(id));

    /// <summary>
    /// Their index: empty when there's none yet, or when a damaged one was just moved aside (kept; they're indexed
    /// afresh). Null when it's there but can't be read right now (another program holds it, or it's damaged and couldn't
    /// be moved aside): nothing is indexed, sent or written over for them this time (ROBUST-4). Off the main thread.
    /// </summary>
    public MemoryFile? Load(string id)
    {
        var path = PathFor(id);
        for (int attempt = 0; ; attempt++)
        {
            lock (_io)
            {
                var (outcome, text, error) = DataFile.ReadText(path, tries: 1);
                if (outcome == DataFile.Outcome.Missing) return new MemoryFile();
                if (outcome == DataFile.Outcome.Read)
                {
                    try { return JsonSerializer.Deserialize<MemoryFile>(text!, Json) ?? new MemoryFile(); }
                    catch (JsonException e)
                    {
                        if (Refused()) return null; // a stopped copy moves nothing
                        if (DataFile.MoveAside(path, "unreadable") is { } aside)
                        {
                            _warn?.Invoke($"The memory index {Path.GetFileName(path)} was damaged ({e.Message}); it's kept as {Path.GetFileName(aside)} and made again.");
                            return new MemoryFile();
                        }
                        if (_warnedLocked.Add(id)) _warn?.Invoke($"The memory index {Path.GetFileName(path)} is damaged and couldn't be moved aside; it's left as it is.");
                        return null;
                    }
                    catch { return null; } // anything else: left as it is
                }
                if (attempt >= 3)
                {
                    if (_warnedLocked.Add(id)) _warn?.Invoke($"The memory index {Path.GetFileName(path)} couldn't be read ({error}); it's left as it is and tried again later.");
                    return null;
                }
            }
            System.Threading.Thread.Sleep(50 * (attempt + 1)); // outside the lock
        }
    }

    /// <summary>
    /// A hot reload: once this copy has stopped, no index is written or moved here (the new copy owns them; audit step 7b).
    /// Waits for a save in progress; what comes later is dropped, said once.
    /// </summary>
    public void Close()
    {
        lock (_io) _closed = true;
    }

    /// <summary>Under <c>_io</c>: true (and said once) once the store is closed.</summary>
    private bool Refused()
    {
        if (!_closed) return false;
        if (!_saidClosed)
        {
            _saidClosed = true;
            _warn?.Invoke("The mod has stopped, so a late memory index wasn't saved.");
        }
        return true;
    }

    /// <summary>Their generation now: taken before indexing them, and passed to <see cref="Save(string, MemoryFile, long)"/>.</summary>
    public long GenerationOf(string id)
    {
        lock (_io) return ((long)_allGeneration << 32) | (uint)_generations.GetValueOrDefault(id);
    }

    /// <summary>Saves their index, unless it was archived since <paramref name="generation"/> (then nothing is written: false).</summary>
    public bool Save(string id, MemoryFile file, long generation)
    {
        // Written to the disk outside the lock (nobody waits on it); only the swap is under it, after the reset check.
        string path = PathFor(id), tmp = DataFile.WriteTemp(path, JsonSerializer.Serialize(file, Json));
        lock (_io)
        {
            if (Refused() || (((long)_allGeneration << 32) | (uint)_generations.GetValueOrDefault(id)) != generation) { DataFile.TryDelete(tmp); return false; }
            DataFile.Commit(tmp, path);
            return true;
        }
    }

    public void Save(string id, MemoryFile file)
    {
        string path = PathFor(id), tmp = DataFile.WriteTemp(path, JsonSerializer.Serialize(file, Json));
        lock (_io)
        {
            if (Refused()) { DataFile.TryDelete(tmp); return; }
            DataFile.Commit(tmp, path);
        }
    }

    /// <summary>Their memories go with them into a backup (/resetnpc).</summary>
    public void Archive(string id, string backupDir)
    {
        lock (_io)
        {
            if (Refused()) return;
            _generations[id] = _generations.GetValueOrDefault(id) + 1; // indexing in flight for them isn't saved
            string path = PathFor(id);
            if (!File.Exists(path)) return;
            string dest = Path.Combine(backupDir, "memory");
            Directory.CreateDirectory(dest);
            File.Move(path, Path.Combine(dest, Path.GetFileName(path)), true);
        }
    }

    /// <summary>Everyone's memories into a backup (/resetall).</summary>
    public void ArchiveAll(string backupDir)
    {
        lock (_io)
        {
            if (Refused()) return;
            _allGeneration++; // indexing in flight for anyone isn't saved
            var files = Directory.GetFiles(_dir, "*.json");
            if (files.Length == 0) return;
            string dest = Path.Combine(backupDir, "memory");
            Directory.CreateDirectory(dest);
            foreach (var f in files) File.Move(f, Path.Combine(dest, Path.GetFileName(f)), true);
        }
    }

    /// <summary>How many people have memories indexed, and how many memories in all.</summary>
    public (int people, int memories) Count()
    {
        lock (_io)
        {
            int people = 0, memories = 0;
            foreach (var f in Directory.GetFiles(_dir, "*.json"))
            {
                try
                {
                    var m = JsonSerializer.Deserialize<MemoryFile>(File.ReadAllText(f, Encoding.UTF8), Json);
                    if (m == null || m.Entries.Count == 0) continue;
                    people++;
                    memories += m.Entries.Count;
                }
                catch { }
            }
            return (people, memories);
        }
    }
}
