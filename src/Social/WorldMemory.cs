using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Social;

/// <summary>Talk that travels: something memorable about the player, started by one person in one district.</summary>
public sealed class Rumor
{
    public string Text { get; set; } = "";
    public string Location { get; set; } = "";
    public int GameDay { get; set; }
    public string SourceId { get; set; } = "";
}

public sealed class Headline
{
    public int GameDay { get; set; }
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
}

public sealed class WorldData
{
    public List<Rumor> Rumors { get; set; } = new();
    public List<Headline> News { get; set; } = new();
}

/// <summary>City-wide memory shared by every NPC: gossip about the player and recent headlines. One JSON file.</summary>
public sealed class WorldMemory
{
    private readonly string _path;
    private readonly object _lock = new();
    private WorldData _data;
    private readonly Action<string>? _warn;
    // The file couldn't be read at the start (locked, or damaged and couldn't be moved aside): nothing is written over it.
    private bool _readOnly, _warnedReadOnly;

    public WorldMemory(string path, Action<string>? warn = null)
    {
        _path = path;
        _warn = warn;
        _data = DataFile.LoadBook(_path, t => JsonSerializer.Deserialize<WorldData>(t, NpcStore.Json), _warn, out _readOnly) ?? new WorldData();
        // A hand-edited file (the full audit, A-007): no empty lists or entries, no missing text.
        _data.Rumors ??= new List<Rumor>();
        _data.News ??= new List<Headline>();
        _data.Rumors.RemoveAll(r => r == null);
        _data.News.RemoveAll(n => n == null);
        foreach (var r in _data.Rumors) { r.Text ??= ""; r.Location ??= ""; r.SourceId ??= ""; }
        foreach (var n in _data.News) { n.Title ??= ""; n.Summary ??= ""; }
    }

    /// <summary>Stop (a hot reload): nothing more is written; a late change is said once (audit step 7b, review of a0fa75f).</summary>
    public void Close()
    {
        lock (_lock) _closed = true;
    }

    private bool _closed, _saidClosed;

    private void Save()
    {
        if (_readOnly) { DataFile.NotSaved(_path, _warn, ref _warnedReadOnly); return; }
        if (_closed) { DataFile.Stopped(Path.GetFileName(_path), _warn, ref _saidClosed); return; }
        DataFile.WriteAtomic(_path, JsonSerializer.Serialize(_data, NpcStore.Json), durable: false); // headlines are added on the main thread
    }

    /// <summary>Forget all gossip and headlines; the old file is copied to <paramref name="backupDir"/> first.</summary>
    public void Reset(string backupDir)
    {
        lock (_lock)
        {
            if (File.Exists(_path))
            {
                Directory.CreateDirectory(backupDir);
                // Kept in the backup: safe to write over now, even if it couldn't be read at the start.
                try { File.Copy(_path, Path.Combine(backupDir, Path.GetFileName(_path)), true); _readOnly = false; }
                catch when (_readOnly) { } // still locked: forgotten for this session only; the file is left as it is
            }
            _data = new WorldData();
            Save();
        }
    }

    public void AddRumor(string text, string location, int day, string sourceId)
    {
        text = Ai.ReplyParser.Quotable(text, 400); // an AI wrote it, and it goes into other residents' prompts
        if (text.Length is < 8 or > 300) return;
        lock (_lock)
        {
            if (_data.Rumors.Any(r => r.SourceId == sourceId && r.GameDay == day && string.Equals(r.Text, text, StringComparison.OrdinalIgnoreCase))) return;
            _data.Rumors.Add(new Rumor { Text = text, Location = location ?? "", GameDay = day, SourceId = sourceId });
            if (_data.Rumors.Count > 200) _data.Rumors.RemoveRange(0, _data.Rumors.Count - 200);
            Save();
        }
    }

    /// <summary>Everything someone started goes (they turned out to be under 18). True if anything was dropped.</summary>
    public bool ForgetSource(string sourceId)
    {
        if (string.IsNullOrEmpty(sourceId)) return false;
        lock (_lock)
        {
            int dropped = _data.Rumors.RemoveAll(r => r.SourceId == sourceId);
            if (dropped == 0) return false;
            Save();
            return true;
        }
    }

    /// <summary>
    /// What someone in this district may have heard: fresher talk first, local talk more likely than talk from
    /// elsewhere, never their own story, and fading after a couple of weeks.
    /// </summary>
    public List<string> RumorsFor(string location, int today, string listenerId, Random rng, int max = 3, int maxAgeDays = 14)
    {
        lock (_lock)
        {
            var pool = _data.Rumors.Where(r => r.SourceId != listenerId && today - r.GameDay >= 0 && today - r.GameDay <= maxAgeDays).ToList();
            var picked = new List<string>();
            foreach (var r in pool.OrderByDescending(r => r.GameDay))
            {
                bool local = location.Length > 0 && string.Equals(r.Location, location, StringComparison.OrdinalIgnoreCase);
                double age = Math.Max(0, today - r.GameDay);
                double chance = (local ? 0.75 : 0.25) * Math.Exp(-age / 6.0); // talk spreads locally first, then fades
                if (rng.NextDouble() < chance) picked.Add(local || r.Location.Length == 0 ? r.Text : $"{r.Text} (word from {r.Location})");
                if (picked.Count >= max) break;
            }
            return picked;
        }
    }

    public void AddHeadline(int day, string title, string text)
    {
        title = (title ?? "").Trim();
        if (title.Length == 0) return;
        lock (_lock)
        {
            if (_data.News.Any(h => string.Equals(h.Title, title, StringComparison.OrdinalIgnoreCase))) return;
            _data.News.Add(new Headline { GameDay = day, Title = title, Summary = FirstSentences(text ?? "", 220) });
            if (_data.News.Count > 40) _data.News.RemoveRange(0, _data.News.Count - 40);
            Save();
        }
    }

    public List<string> RecentHeadlines(int today, int max = 4, int maxAgeDays = 7)
    {
        lock (_lock)
            return _data.News.Where(h => today - h.GameDay >= 0 && today - h.GameDay <= maxAgeDays)
                .OrderByDescending(h => h.GameDay).Take(max)
                .Select(h => $"{(h.GameDay == today ? "today" : today - h.GameDay == 1 ? "yesterday" : $"{today - h.GameDay} days ago")}: {h.Title}{(h.Summary.Length > 0 ? " — " + h.Summary : "")}")
                .ToList();
    }

    /// <summary>The phone's News app: the holo headlines, newest first (day, title, summary).</summary>
    public List<(int day, string title, string summary)> Headlines(int max = 20)
    {
        lock (_lock) return _data.News.OrderByDescending(h => h.GameDay).Take(max).Select(h => (h.GameDay, h.Title, h.Summary)).ToList();
    }

    /// <summary>The phone's News app: the talk around town lately, newest first (day, what was said, where).</summary>
    public List<(int day, string text, string where)> TalkAroundTown(int today, int max = 12, int maxAgeDays = 7)
    {
        lock (_lock)
            return _data.Rumors.Where(r => today - r.GameDay is >= 0 && today - r.GameDay <= maxAgeDays)
                .OrderByDescending(r => r.GameDay).Take(max).Select(r => (r.GameDay, r.Text, r.Location)).ToList();
    }

    public static string FirstSentences(string text, int max)
    {
        string t = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", "").Replace('\n', ' ').Trim();
        if (t.Length <= max) return t;
        int cut = t.LastIndexOf(". ", max, StringComparison.Ordinal);
        return cut > 40 ? t[..(cut + 1)] : t[..max].TrimEnd() + "…";
    }
}
