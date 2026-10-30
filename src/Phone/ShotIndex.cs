using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Phone;

/// <summary>
/// Nightline's city shots (FB-71): street views taken now and then in the background (the game's view only: no UI, and
/// everyone hidden, so no one is in them), each tagged with the area and the place you were at, at most so many per area
/// and so many MB in all. They're the look of the city for photos residents post. Not per save (the city looks the same
/// in every save): shots.json beside the pictures. No Unity, tested.
/// </summary>
public sealed class ShotIndex
{
    public sealed class Shot
    {
        public string File { get; set; } = "";
        public string Area { get; set; } = "";
        public string Place { get; set; } = "";
        public int Day { get; set; }
        public long Bytes { get; set; }
        public DateTime Utc { get; set; }
    }

    public sealed class Data
    {
        public List<Shot> Shots { get; set; } = new();
    }

    private readonly string _path;
    private readonly object _lock = new();
    private Data _data;
    private readonly bool _readOnly;

    public ShotIndex(string path, Action<string>? warn = null)
    {
        _path = path;
        _data = DataFile.LoadBook(path, t => JsonSerializer.Deserialize<Data>(t, NpcStore.Json), warn, out _readOnly) ?? new Data();
        _data.Shots ??= new();
        _data.Shots.RemoveAll(s => s == null || !SafeFile(s.File) || string.IsNullOrWhiteSpace(s.Area));
        foreach (var s in _data.Shots) { s.Place ??= ""; s.Area = s.Area.Trim(); }
    }

    /// <summary>A shot's file: a plain "shot-...jpg" name in the shots folder.</summary>
    public static bool SafeFile(string? f) => f != null && f.StartsWith("shot-", StringComparison.Ordinal) && LineBook.SafeFile(f) && f.EndsWith(".jpg", StringComparison.Ordinal);

    /// <summary>
    /// A new shot. The oldest of its area go past <paramref name="perArea"/>, then the oldest anywhere past <paramref name="maxMb"/>
    /// in all; their files are returned to delete. A bad name or an empty area adds nothing.
    /// </summary>
    public List<string> Add(string file, string area, string place, int day, long bytes, int perArea, int maxMb)
    {
        var gone = new List<string>();
        area = (area ?? "").Trim();
        if (!SafeFile(file) || area.Length == 0) return gone;
        lock (_lock)
        {
            _data.Shots.Add(new Shot { File = file, Area = area, Place = (place ?? "").Trim(), Day = day, Bytes = Math.Max(0, bytes), Utc = DateTime.UtcNow });
            int keep = Math.Clamp(perArea, 1, 1000);
            var mine = _data.Shots.Where(s => s.Area.Equals(area, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var old in mine.Take(Math.Max(0, mine.Count - keep))) { _data.Shots.Remove(old); gone.Add(old.File); }
            long cap = Math.Clamp(maxMb, 10, 10000) * 1024L * 1024L;
            while (_data.Shots.Count > 1 && _data.Shots.Sum(s => s.Bytes) > cap) { gone.Add(_data.Shots[0].File); _data.Shots.RemoveAt(0); }
            Save();
        }
        return gone;
    }

    /// <summary>The areas there are shots of (most shots first).</summary>
    public List<string> Areas()
    {
        lock (_lock)
            return _data.Shots.GroupBy(s => s.Area, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.First().Area).ToList();
    }

    public int Count(string area) { lock (_lock) return _data.Shots.Count(s => s.Area.Equals(area, StringComparison.OrdinalIgnoreCase)); }
    public int Total { get { lock (_lock) return _data.Shots.Count; } }

    /// <summary>
    /// Up to <paramref name="count"/> shots of an area for a photo's references: the place's own first when one is named,
    /// then the rest of the area, at random within each.
    /// </summary>
    public List<string> Pick(string area, string place, int count, Random rng)
    {
        lock (_lock)
        {
            var mine = _data.Shots.Where(s => s.Area.Equals((area ?? "").Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            var atPlace = mine.Where(s => place.Length > 0 && s.Place.Equals(place.Trim(), StringComparison.OrdinalIgnoreCase)).OrderBy(_ => rng.Next()).ToList();
            var rest = mine.Except(atPlace).OrderBy(_ => rng.Next());
            return atPlace.Concat(rest).Take(Math.Max(0, count)).Select(s => s.File).ToList();
        }
    }

    /// <summary>Every shot's file (the folder's tidying keeps these).</summary>
    public HashSet<string> Files() { lock (_lock) return _data.Shots.Select(s => s.File).ToHashSet(StringComparer.OrdinalIgnoreCase); }

    /// <summary>All shots forgotten (/nightline shots clear); their files returned to delete.</summary>
    public List<string> Clear() { lock (_lock) { var f = _data.Shots.Select(s => s.File).ToList(); _data.Shots.Clear(); Save(); return f; } }

    private void Save()
    {
        if (_readOnly) return;
        try { DataFile.WriteAtomic(_path, JsonSerializer.Serialize(_data, NpcStore.Json), durable: false); } catch { }
    }
}
