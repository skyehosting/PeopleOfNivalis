using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NivalisAIChat.Npc;

/// <summary>
/// FB-50: the story conversations the player has had with each story character, as the game showed them: their lines,
/// the player's choices, and anyone else who spoke in the scene. Only what the player has SEEN is ever here (recorded as
/// the game shows it, or read from the game's own chat history page, which shows only what was seen), so nothing in it can
/// spoil a part of the story the player hasn't reached. Also each character's speech sheet (how they talk), written from
/// these lines. One small JSON file; no Unity here.
/// </summary>
public sealed class StoryBook
{
    /// <summary>Their own lines kept before a speech sheet is written, and how many more before it's written again.</summary>
    public const int SheetAfter = 15, SheetAgainAfter = 60;
    /// <summary>Most lines kept per person (the oldest go first), and the longest line kept.</summary>
    public const int MaxLinesPerPerson = 800, MaxLineChars = 600;
    /// <summary>Who said a line: the story character themselves, or the player (anyone else is kept by their name).</summary>
    public const string Them = "them", You = "you";

    public sealed class Line
    {
        public string Who { get; set; } = "";
        public string Text { get; set; } = "";
        /// <summary>By text message (the phone), not face to face.</summary>
        public bool Message { get; set; }
    }

    public sealed class Scene
    {
        public int GameDay { get; set; }
        public string Clock { get; set; } = "";
        public DateTime Utc { get; set; }
        /// <summary>"live" (recorded as it played), "history" (read from the game's chat history page) or "history2" (read so
        /// since FB-78's fix: the repair never touches those).</summary>
        public string Source { get; set; } = "live";
        public List<Line> Lines { get; set; } = new();
    }

    public sealed class Person
    {
        public string Name { get; set; } = "";
        public List<Scene> Scenes { get; set; } = new();
        /// <summary>How they talk, written from their lines (empty until there are enough).</summary>
        public string Sheet { get; set; } = "";
        /// <summary>How many of their own lines there were when the sheet was written.</summary>
        public int SheetLines { get; set; }
    }

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, Person> _people;
    // The scene being recorded right now, per person (a conversation as it plays).
    private readonly Dictionary<string, Scene> _open = new();
    private bool _dirty;
    private readonly Action<string>? _warn;
    // The file couldn't be read at the start (locked, or damaged and couldn't be moved aside): nothing is written over it.
    private bool _readOnly, _warnedReadOnly;
    private readonly BookWriter _writer;

    public StoryBook(string path, Action<string>? warn = null)
    {
        _path = path;
        _warn = warn;
        _people = DataFile.LoadBook(_path, t => JsonSerializer.Deserialize<Dictionary<string, Person>>(t, NpcStore.Json), _warn, out _readOnly)
                  ?? new Dictionary<string, Person>();
        // A hand-edited file (the full audit, A-007): no empty people, scenes or lines, no missing text.
        foreach (var k in _people.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) _people.Remove(k);
        foreach (var p in _people.Values)
        {
            p.Name ??= ""; p.Sheet ??= "";
            p.Scenes ??= new List<Scene>();
            p.Scenes.RemoveAll(s => s == null);
            foreach (var s in p.Scenes)
            {
                s.Clock ??= ""; s.Source ??= "live";
                s.Lines ??= new List<Line>();
                s.Lines.RemoveAll(l => l == null);
                foreach (var l in s.Lines) { l.Who ??= ""; l.Text ??= ""; }
            }
        }
        _writer = new BookWriter(_path, Snapshot, _warn, failed: () => { lock (_lock) _dirty = true; });
    }

    /// <summary>Its writer (tests: waiting for it).</summary>
    internal BookWriter Writer => _writer;

    private string? Snapshot()
    {
        string text;
        lock (_lock)
        {
            if (!_dirty) return null;
            if (_readOnly) { DataFile.NotSaved(_path, _warn, ref _warnedReadOnly); return null; }
            text = JsonSerializer.Serialize(_people, NpcStore.Json); // under the lock: lines are added to the open scene meanwhile
            _dirty = false;
        }
        return text;
    }

    /// <summary>A line as it's kept: the game's text markup gone, spaces tidied, not too long ("" when nothing is left).</summary>
    public static string Clean(string? text)
    {
        string t = Regex.Replace(text ?? "", @"<[^<>]{1,40}>", ""); // <i>, <color=...>, <b>: the game's text markup
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return t.Length > MaxLineChars ? t[..MaxLineChars].TrimEnd() + "…" : t;
    }

    /// <summary>
    /// Who said a line on the game's chat history page, from the name on its bubble: them (their name, or none), you, or
    /// null: anyone else is left out, since only a name is known there and their age can't be checked (review FB50-1).
    /// </summary>
    public static string? WhoOnPage(string bubbleName, string partnerName, bool isPlayer)
    {
        if (isPlayer) return You;
        string n = (bubbleName ?? "").Trim();
        return n.Length == 0 || n.Equals((partnerName ?? "").Trim(), StringComparison.OrdinalIgnoreCase) ? Them : null;
    }

    private Person PersonFor(string id, string name)
    {
        if (!_people.TryGetValue(id, out var p)) _people[id] = p = new Person();
        if (name.Trim().Length > 0) p.Name = name.Trim();
        return p;
    }

    /// <summary>
    /// A line of a story conversation as the game shows it (who: <see cref="Them"/>, <see cref="You"/>, or another speaker's
    /// name). The first line opens a scene; the same line shown twice in a row is kept once. True when it was kept.
    /// </summary>
    public bool Add(string id, string name, string who, string text, bool message, int gameDay, string clock)
    {
        text = Clean(text);
        who = (who ?? "").Trim();
        if (string.IsNullOrEmpty(id) || text.Length == 0 || who.Length == 0) return false;
        lock (_lock)
        {
            var p = PersonFor(id, name);
            if (!_open.TryGetValue(id, out var scene) || scene.GameDay != gameDay || !p.Scenes.Contains(scene))
            {
                scene = new Scene { GameDay = gameDay, Clock = clock ?? "", Utc = DateTime.UtcNow, Source = "live" };
                p.Scenes.Add(scene);
                _open[id] = scene;
            }
            var last = scene.Lines.Count > 0 ? scene.Lines[^1] : null;
            if (last != null && last.Who == who && last.Text == text) return false;
            scene.Lines.Add(new Line { Who = who, Text = text, Message = message });
            Trim(p);
            _dirty = true;
            return true;
        }
    }

    /// <summary>The conversation is over: the next line starts a new scene. How many lines that scene kept (0: none).</summary>
    public int End(string id)
    {
        int kept;
        lock (_lock)
        {
            kept = _open.TryGetValue(id, out var scene) ? scene.Lines.Count : 0;
            _open.Remove(id);
        }
        SaveSoon();
        return kept;
    }

    /// <summary>
    /// A page of the game's own chat history with them (a day's conversation, as the player saw it). Skipped when that day
    /// already has most of these lines (recorded as they played, or this page read before). True when it was added.
    /// </summary>
    public bool ImportPage(string id, string name, int gameDay, IReadOnlyList<Line> lines)
    {
        var clean = lines.Select(l => new Line { Who = (l.Who ?? "").Trim(), Text = Clean(l.Text), Message = l.Message })
                         .Where(l => l.Text.Length > 0 && l.Who.Length > 0).ToList();
        if (string.IsNullOrEmpty(id) || clean.Count == 0) return false;
        lock (_lock)
        {
            var p = PersonFor(id, name);
            // FB-78: a page with no day (the game's history keeps none: 0) is checked against every day kept.
            var known = new HashSet<string>(p.Scenes.Where(s => gameDay <= 0 || s.GameDay == gameDay).SelectMany(s => s.Lines).Select(l => l.Text), StringComparer.Ordinal);
            int already = clean.Count(l => known.Contains(l.Text));
            if (already * 2 >= clean.Count) return false;
            var scene = new Scene { GameDay = gameDay, Source = "history2", Utc = DateTime.MinValue, Lines = clean }; // FB-78 (review PH-4)
            int at = p.Scenes.FindIndex(s => s.GameDay > gameDay);
            p.Scenes.Insert(at < 0 ? p.Scenes.Count : at, scene);
            Trim(p);
            _dirty = true;
        }
        SaveSoon();
        return true;
    }

    /// <summary>
    /// FB-78: history pages read with none of their lines (0.10.0's first backfill dropped every line of Ava's: her speaker
    /// was another copy of her), let go so the history can be read again. Returns how many.
    /// </summary>
    public int DropHistoryWithoutThem(string id)
    {
        int n;
        lock (_lock)
        {
            if (string.IsNullOrEmpty(id) || !_people.TryGetValue(id, out var p)) return 0;
            n = p.Scenes.RemoveAll(s => s.Source == "history" && !s.Lines.Any(l => l.Who == Them));
            if (n > 0) _dirty = true;
        }
        if (n > 0) SaveSoon();
        return n;
    }

    private static void Trim(Person p)
    {
        int total = p.Scenes.Sum(s => s.Lines.Count);
        while (total > MaxLinesPerPerson && p.Scenes.Count > 0)
        {
            var oldest = p.Scenes[0];
            if (oldest.Lines.Count > 0) { oldest.Lines.RemoveAt(0); total--; }
            if (oldest.Lines.Count == 0) p.Scenes.RemoveAt(0);
        }
    }

    private static Scene Copy(Scene s) => new()
    {
        GameDay = s.GameDay, Clock = s.Clock, Utc = s.Utc, Source = s.Source,
        Lines = s.Lines.Select(l => new Line { Who = l.Who, Text = l.Text, Message = l.Message }).ToList(),
    };

    /// <summary>The id of the story character with this name ("Ava"), or null when they've never been seen.</summary>
    public string? IdNamed(string name)
    {
        lock (_lock) return _people.Where(kv => string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).FirstOrDefault();
    }

    /// <summary>Their story conversations, oldest first (copies: safe to read anywhere).</summary>
    public List<Scene> Scenes(string id)
    {
        lock (_lock) return _people.TryGetValue(id, out var p) ? p.Scenes.Where(s => s.Lines.Count > 0).Select(Copy).ToList() : new List<Scene>();
    }

    /// <summary>What they said themselves, oldest first.</summary>
    public List<string> OwnLines(string id)
    {
        lock (_lock)
            return _people.TryGetValue(id, out var p) ? p.Scenes.SelectMany(s => s.Lines).Where(l => l.Who == Them).Select(l => l.Text).ToList() : new List<string>();
    }

    public int LineCount(string id)
    {
        lock (_lock) return _people.TryGetValue(id, out var p) ? p.Scenes.Sum(s => s.Lines.Count) : 0;
    }

    public string Sheet(string id)
    {
        lock (_lock) return _people.TryGetValue(id, out var p) ? p.Sheet ?? "" : "";
    }

    /// <summary>Enough of their own lines for a first speech sheet, or enough more since the last one.</summary>
    public bool NeedsSheet(string id)
    {
        lock (_lock)
        {
            if (!_people.TryGetValue(id, out var p)) return false;
            int own = p.Scenes.SelectMany(s => s.Lines).Count(l => l.Who == Them);
            return string.IsNullOrWhiteSpace(p.Sheet) ? own >= SheetAfter : own - p.SheetLines >= SheetAgainAfter;
        }
    }

    public void SetSheet(string id, string sheet, int ownLines)
    {
        lock (_lock)
        {
            if (!_people.TryGetValue(id, out var p)) return;
            p.Sheet = (sheet ?? "").Trim();
            p.SheetLines = ownLines;
            _dirty = true;
        }
        SaveSoon();
    }

    /// <summary>Everyone with story conversations kept: id, name, lines, whether they have a sheet (most lines first).</summary>
    public List<(string id, string name, int lines, bool sheet)> Summary()
    {
        lock (_lock)
            return _people.Select(kv => (kv.Key, kv.Value.Name, kv.Value.Scenes.Sum(s => s.Lines.Count), !string.IsNullOrWhiteSpace(kv.Value.Sheet)))
                          .Where(x => x.Item3 > 0).OrderByDescending(x => x.Item3).ToList();
    }

    public void SaveSoon()
    {
        lock (_lock) if (!_dirty) return;
        _writer.SaveSoon();
    }

    public void Save() => _writer.Write();

    /// <summary>A hot reload or the game closing: what's waiting is written, and then nothing more.</summary>
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

    public void Forget(string id)
    {
        lock (_lock)
        {
            _open.Remove(id);
            if (_people.Remove(id)) _dirty = true;
        }
        SaveSoon();
    }

    /// <summary>Forget everything (/resetall); the old file is copied to <paramref name="backupDir"/> first.</summary>
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
                _people = new Dictionary<string, Person>();
                _open.Clear();
                _dirty = true;
            }
            _writer.Write();
        });
    }

    /// <summary>
    /// The chat window's history with their story conversations in it: each scene goes in before the first chat line from
    /// a later game day (or later the same day), so the chats keep their own order. Story lines come as roles
    /// "story-scene" (a scene starts), "story-them", "story-you" and "story-other" (text: the speaker's name, \u001F, the line).
    /// </summary>
    public static List<ChatLine> Timeline(IReadOnlyList<ChatLine> history, IReadOnlyList<Scene> scenes)
    {
        var merged = new List<ChatLine>(history.Count + scenes.Sum(s => s.Lines.Count + 1));
        var ordered = scenes.Where(s => s.Lines.Count > 0).OrderBy(s => s.GameDay).ThenBy(s => s.Utc).ToList();
        int next = 0;
        foreach (var h in history)
        {
            while (next < ordered.Count && (ordered[next].GameDay < h.GameDay || ordered[next].GameDay == h.GameDay && ordered[next].Utc <= h.Utc))
                AddScene(merged, ordered[next++]);
            merged.Add(h);
        }
        while (next < ordered.Count) AddScene(merged, ordered[next++]);
        return merged;
    }

    private static void AddScene(List<ChatLine> to, Scene s)
    {
        to.Add(new ChatLine { Role = "story-scene", GameDay = s.GameDay, Clock = s.Clock, Utc = s.Utc });
        foreach (var l in s.Lines)
            to.Add(new ChatLine
            {
                Role = l.Who == Them ? "story-them" : l.Who == You ? "story-you" : "story-other",
                Text = l.Who is Them or You ? l.Text : l.Who + "\u001F" + l.Text,
                GameDay = s.GameDay, Clock = s.Clock, Utc = s.Utc, Folded = true,
            });
    }
}
