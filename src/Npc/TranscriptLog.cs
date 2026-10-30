using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;

namespace NivalisAIChat.Npc;

/// <summary>
/// FB-30 (the author: "begin logging conversations between npc's, including between me and other npc's ... a transcript
/// that involves our parameters along with what changes the way they speak"): one JSON-lines file per play session in
/// the data folder's transcripts folder. A "session" entry first (the settings and instructions in force), a "person"
/// entry the first time someone appears (how they talk), then a "line" for everything said, with what shaped it.
/// Written by one background writer that only appends; never throws into the game; stops for good on Close (a hot reload
/// starts a new copy). Key-shaped text is hidden (Redact.Text). Unity-free, so it's tested.
/// </summary>
public sealed class TranscriptLog
{
    public const long MaxBytes = 20L * 1024 * 1024;
    public const int KeepFiles = 30, KeepExports = 10, MaxParts = 5;
    public const string Extension = ".jsonl";

    private readonly string _dir, _stamp;
    private readonly Action<string> _warn;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Task _writer;
    private readonly object _lock = new();
    private readonly HashSet<string> _people = new(StringComparer.Ordinal);
    private string _header = "", _retry = "";
    private int _part = 1;
    private long _size;
    private readonly long _maxBytes;
    private bool _full;
    private volatile bool _closed;

    public TranscriptLog(string dir, Action<string> warn, DateTime? started = null, long maxBytes = MaxBytes)
    {
        _dir = dir;
        _warn = warn;
        _maxBytes = Math.Max(1024, maxBytes);
        _stamp = (started ?? DateTime.Now).ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(dir);
        Rotate(dir, KeepFiles);
        RotateExports(dir, KeepExports);
        _writer = Task.Run(WriteLoop);
    }

    /// <summary>This session's name ("2026-10-13_200000"): its files are that, then "-2", "-3"... past the size cap.</summary>
    public string Stamp => _stamp;

    /// <summary>A session's files in <paramref name="dir"/>, in order (code review 3: by its whole name, not a prefix of the date).</summary>
    public static List<string> SessionFiles(string dir, string stamp)
    {
        try
        {
            return Directory.GetFiles(dir, "*" + Extension)
                .Where(f => { string n = Path.GetFileNameWithoutExtension(f); return n == stamp || n.StartsWith(stamp + "-", StringComparison.Ordinal); })
                .OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal).ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>The newest <paramref name="keep"/> exports are kept.</summary>
    public static void RotateExports(string dir, int keep)
    {
        try
        {
            var files = Directory.GetFiles(dir, "export-*.md").OrderByDescending(File.GetLastWriteTimeUtc).ToList();
            foreach (var f in files.Skip(keep)) try { File.Delete(f); } catch { }
        }
        catch { }
    }

    /// <summary>The file being written now (a session over the size cap goes on in a "-2", "-3"... file).</summary>
    public string CurrentFile { get { lock (_lock) return FileFor(_part); } }

    private string FileFor(int part) => Path.Combine(_dir, _stamp + (part > 1 ? "-" + part : "") + Extension);

    /// <summary>The oldest session files go, so at most <paramref name="keep"/> - 1 are left before a new one starts (exports stay).</summary>
    public static void Rotate(string dir, int keep)
    {
        try
        {
            var files = Directory.GetFiles(dir, "*" + Extension).OrderBy(File.GetLastWriteTimeUtc).ToList();
            while (files.Count >= keep)
            {
                try { File.Delete(files[0]); } catch { }
                files.RemoveAt(0);
            }
        }
        catch { }
    }

    /// <summary>The settings and instructions in force: written first (and again at the top of any later part).</summary>
    public void Header(JsonObject header)
    {
        string json = Wrap("session", header);
        lock (_lock) _header = json;
        Enqueue(json);
    }

    /// <summary>Who someone is and how they talk, the first time they appear in this file.</summary>
    public void Person(string id, Func<JsonObject> describe)
    {
        lock (_lock) if (!_people.Add(id)) return;
        try { Enqueue(Wrap("person", describe())); } catch (Exception e) { _warn("transcript person: " + e.Message); }
    }

    /// <summary>Something said. Its "text" has any key-shaped token hidden.</summary>
    public void Line(JsonObject line)
    {
        if (line["text"] is JsonValue v && v.TryGetValue<string>(out var t)) line["text"] = Redact.Text(t);
        Enqueue(Wrap("line", line));
    }

    private static string Wrap(string type, JsonObject o)
    {
        var w = new JsonObject { ["type"] = type };
        foreach (var kv in o.ToList()) { o.Remove(kv.Key); w[kv.Key] = kv.Value; }
        return w.ToJsonString();
    }

    private void Enqueue(string json)
    {
        if (_closed) return;
        _queue.Enqueue(json);
        try { _signal.Release(); } catch { }
    }

    private async Task WriteLoop()
    {
        while (true)
        {
            try { await _signal.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            // A batch that failed is tried again on its own too, and once more when it closes (audit B4).
            if (!_queue.IsEmpty || _retry.Length > 0) Flush();
            if (_closed && _queue.IsEmpty) { if (_retry.Length > 0) Flush(); return; }
        }
    }

    private void Flush()
    {
        var sb = new StringBuilder(_retry);
        _retry = "";
        while (_queue.TryDequeue(out var s)) sb.Append(s).Append('\n');
        if (sb.Length == 0) return;
        lock (_lock)
        {
            if (_full) return;
            if (_size > _maxBytes)
            {
                if (_part >= MaxParts)
                {
                    // A whole session has its limit too: past it, nothing more is kept (said once).
                    _full = true;
                    _warn($"The transcript for this session reached {MaxParts} files of {_maxBytes / (1024 * 1024)} MB: nothing more is kept until the next session.");
                    return;
                }
                // Over the cap: the session goes on in a new part, which starts with the same header; people are described again.
                _part++;
                _size = 0;
                _people.Clear();
                if (_header.Length > 0) sb.Insert(0, _header + "\n");
            }
            string text = sb.ToString();
            try
            {
                // Shared, so an export reading this file meanwhile doesn't make the write fail (code review 4).
                using (var fs = new FileStream(FileFor(_part), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                    w.Write(text);
                _size += Encoding.UTF8.GetByteCount(text);
            }
            catch (Exception e)
            {
                if (text.Length < 4 * 1024 * 1024) _retry = text; // tried again with the next batch, not lost
                _warn("Couldn't write the transcript (trying again): " + e.Message);
            }
        }
    }

    /// <summary>Stops: what's waiting is written (up to 3 seconds), and nothing after.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        try { _signal.Release(); } catch { }
        try { _writer.Wait(3000); } catch { }
    }

    // ------------------------------------------------------------------ export

    /// <summary>
    /// The sessions in <paramref name="files"/> as one readable document for research: per session its settings, the people
    /// in it and how they talk, then every conversation in order, each line with what shaped it.
    /// </summary>
    public static string Export(IEnumerable<string> files, DateTime now)
    {
        var sb = new StringBuilder();
        var sessions = new List<(string file, List<JsonObject> entries)>();
        foreach (var f in files.OrderBy(x => x, StringComparer.Ordinal))
        {
            var entries = new List<JsonObject>();
            try
            {
                // Shared, so the session being written now can be read (code review 4).
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs, Encoding.UTF8);
                string? l;
                while ((l = r.ReadLine()) != null)
                    if (l.Trim().Length > 0 && TolerantParse(l) is { } o) entries.Add(o);
            }
            catch { continue; }
            sessions.Add((Path.GetFileNameWithoutExtension(f), entries));
        }
        int lines = sessions.Sum(s => s.entries.Count(e => S(e, "type") == "line"));
        sb.AppendLine("# People of Nivalis: transcripts");
        sb.AppendLine();
        sb.AppendLine($"Exported {now:yyyy-MM-dd HH:mm}. {sessions.Count} session(s), {lines} line(s). Every line shows who said it, to whom, and " +
                      "what shaped it: their mood, the tone of voice, a gesture, how they feel about the other (0-100%, 50 = neutral), the voice that " +
                      "spoke it, and what wrote it (an AI model, or the mod's written lines).");
        foreach (var (name, entries) in sessions)
        {
            sb.AppendLine();
            sb.AppendLine($"## Session {name}");
            foreach (var h in entries.Where(e => S(e, "type") == "session").Take(1))
            {
                sb.AppendLine();
                sb.AppendLine("Settings in force:");
                foreach (var kv in h)
                {
                    if (kv.Key == "type" || kv.Key == "instructions") continue;
                    sb.AppendLine($"- {kv.Key}: {Flat(kv.Value)}");
                }
                if (h["instructions"] is JsonObject ins)
                {
                    sb.AppendLine();
                    sb.AppendLine("Instructions in force (\"default\" = the mod's own, unchanged):");
                    foreach (var kv in ins) sb.AppendLine($"- {kv.Key}: {Flat(kv.Value)}");
                }
            }
            var people = entries.Where(e => S(e, "type") == "person").ToList();
            if (people.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### People");
                foreach (var p in people)
                    sb.AppendLine("- " + string.Join("; ", p.Where(kv => kv.Key != "type").Select(kv => $"{kv.Key}: {Flat(kv.Value)}")));
            }
            var said = entries.Where(e => S(e, "type") == "line").ToList();
            if (said.Count == 0) continue;
            sb.AppendLine();
            sb.AppendLine("### What was said");
            foreach (var group in said.GroupBy(e => S(e, "group")))
            {
                var first = group.First();
                sb.AppendLine();
                string where = string.Join(", ", new[] { S(first, "place"), S(first, "day").Length > 0 ? "day " + S(first, "day") : "" }.Where(x => x.Length > 0));
                sb.AppendLine($"#### {S(first, "kind")}{(where.Length > 0 ? " · " + where : "")} · {S(first, "t").Replace('T', ' ')[..Math.Min(16, S(first, "t").Length)]}");
                foreach (var e in group)
                {
                    var notes = new List<string>();
                    foreach (var k in new[] { "mood", "tone", "gesture", "relationship", "voice", "by" })
                        if (S(e, k) is { Length: > 0 } v) notes.Add($"{k}: {v}");
                    string clock = S(e, "clock");
                    sb.AppendLine($"- {(clock.Length > 0 ? $"[{clock}] " : "")}**{S(e, "speaker")}**{(S(e, "to").Length > 0 ? " → " + S(e, "to") : "")}: {S(e, "text")}" +
                                  (notes.Count > 0 ? $"  _({string.Join("; ", notes)})_" : ""));
                }
            }
        }
        return sb.ToString();
    }

    private static JsonObject? TolerantParse(string line)
    {
        try { return JsonNode.Parse(line) as JsonObject; } catch { return null; }
    }

    private static string S(JsonObject o, string key) => o[key] switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonNode n => n.ToJsonString(),
    };

    private static string Flat(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s.Replace("\r", " ").Replace("\n", " / "),
        JsonArray a => string.Join(", ", a.Select(Flat)),
        JsonObject o => string.Join(", ", o.Select(kv => $"{kv.Key} {Flat(kv.Value)}")),
        _ => n.ToJsonString(),
    };
}
