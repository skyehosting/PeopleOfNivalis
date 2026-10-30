using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Phone;

/// <summary>
/// FB-80 (the author: "add two new apps, galary (uses users system storage)"): the photos you take with the phone's
/// Camera, as files in a folder on your PC (Pictures\Nivalis Nights by default), and the mod's note of each one: when and
/// where it was taken, and whether someone under 18 was in view (then it's never sent to anyone's AI). A picture in the
/// folder the mod didn't take is shown but never sent either: it was never checked. Global (every save shares the
/// folder). Unity-free and tested.
/// </summary>
public sealed class Gallery
{
    public sealed class Photo
    {
        /// <summary>The file in the gallery folder (a plain name).</summary>
        public string File { get; set; } = "";
        public int Day { get; set; }
        public string Clock { get; set; } = "";
        public string Place { get; set; } = "";
        public long Minute { get; set; } = -1;
        /// <summary>Someone under 18 was in view: kept, posted if you like, but never shown to an AI.</summary>
        public bool Child { get; set; }
        /// <summary>The mod took it (and checked it); false for a picture put in the folder some other way.</summary>
        public bool Ours { get; set; }
        /// <summary>Review F2: the file as it was written (size, time): a file put there later under its name isn't it.</summary>
        public long Size { get; set; }
        public long WrittenUtc { get; set; }
    }

    public sealed class Data { public List<Photo> Photos { get; set; } = new(); }

    public const int MaxKept = 2000;

    private readonly string _path;
    private readonly Action<string>? _warn;
    private readonly object _lock = new();
    private readonly Data _data;

    /// <summary>The folder the photos are in (made when the first is taken).</summary>
    public string Folder { get; }

    public Gallery(string indexPath, string folder, Action<string>? warn = null)
    {
        _path = indexPath;
        _warn = warn;
        Folder = folder;
        _data = DataFile.LoadBook(_path, t => JsonSerializer.Deserialize<Data>(t, NpcStore.Json), _warn, out _) ?? new Data();
        _data.Photos ??= new();
        _data.Photos.RemoveAll(p => p == null || !SafeName(p.File));
        foreach (var p in _data.Photos) { p.Clock ??= ""; p.Place ??= ""; }
    }

    /// <summary>A plain file name of a picture (no folders, nothing odd).</summary>
    public static bool SafeName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name!.Length <= 120 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Contains("..")
        && (name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".png", StringComparison.OrdinalIgnoreCase));

    /// <summary>The name a new photo gets: "Nivalis day 15 09-16-02.jpg" (a number on the end if it's taken).</summary>
    public string NewName(int day, string clock, DateTime now)
    {
        string c = new string((clock ?? "").Where(char.IsLetterOrDigit).ToArray());
        string baseName = $"Nivalis day {Math.Max(0, day)} {(c.Length > 0 ? c + " " : "")}{now:HHmmss}";
        string name = baseName + ".jpg";
        for (int n = 2; Exists(name) && n < 100; n++) name = $"{baseName} {n}.jpg";
        return name;
    }

    private bool Exists(string name)
    {
        lock (_lock) if (_data.Photos.Any(p => string.Equals(p.File, name, StringComparison.OrdinalIgnoreCase))) return true;
        try { return System.IO.File.Exists(Path.Combine(Folder, name)); } catch { return false; }
    }

    /// <summary>A photo the Camera took (its file already written): noted, newest first.</summary>
    public Photo? Add(string file, int day, string clock, string place, long minute, bool child, long size = 0, DateTime writtenUtc = default)
    {
        if (!SafeName(file)) return null;
        var p = new Photo { File = file, Day = day, Clock = clock ?? "", Place = place ?? "", Minute = minute, Child = child, Ours = true,
                            Size = size, WrittenUtc = writtenUtc.Ticks };
        lock (_lock)
        {
            _data.Photos.RemoveAll(x => string.Equals(x.File, file, StringComparison.OrdinalIgnoreCase));
            _data.Photos.Insert(0, p);
            if (_data.Photos.Count > MaxKept) _data.Photos.RemoveRange(MaxKept, _data.Photos.Count - MaxKept);
        }
        Save();
        return Copy(p);
    }

    /// <summary>
    /// The photos, newest first: the ones noted that are still in the folder, then any other picture there (not ours,
    /// so never sent to an AI), by the time the file was written.
    /// </summary>
    public List<Photo> List(IEnumerable<(string name, DateTime written)> filesInFolder)
    {
        var files = filesInFolder.Where(f => SafeName(f.name)).ToList();
        var present = new HashSet<string>(files.Select(f => f.name), StringComparer.OrdinalIgnoreCase);
        List<Photo> noted;
        lock (_lock) noted = _data.Photos.Where(p => present.Contains(p.File)).Select(Copy).ToList();
        var known = new HashSet<string>(noted.Select(p => p.File), StringComparer.OrdinalIgnoreCase);
        var others = files.Where(f => !known.Contains(f.name)).OrderByDescending(f => f.written)
                          .Select(f => new Photo { File = f.name, Ours = false });
        return noted.Concat(others).ToList();
    }

    /// <summary>The photo by its file name, or null.</summary>
    public Photo? Get(string file)
    {
        lock (_lock) return _data.Photos.FirstOrDefault(p => string.Equals(p.File, file, StringComparison.OrdinalIgnoreCase)) is { } p ? Copy(p) : null;
    }

    /// <summary>
    /// Whether it may be shown to an AI: the mod took it, nobody under 18 was in view, and the file is still the one it wrote
    /// (review F2: the same size and time; a picture put there later under its name is never sent).
    /// </summary>
    public bool MayShowAi(string file, long size, DateTime writtenUtc) =>
        Get(file) is { Ours: true, Child: false } p && p.Size > 0 && p.Size == size && Math.Abs(p.WrittenUtc - writtenUtc.Ticks) < TimeSpan.TicksPerSecond * 2;

    /// <summary>Forgotten (the file is deleted by the caller). True when it was noted.</summary>
    public bool Remove(string file)
    {
        bool had;
        lock (_lock) had = _data.Photos.RemoveAll(p => string.Equals(p.File, file, StringComparison.OrdinalIgnoreCase)) > 0;
        if (had) Save();
        return had;
    }

    public int Count { get { lock (_lock) return _data.Photos.Count; } }

    private void Save()
    {
        string text;
        lock (_lock) text = JsonSerializer.Serialize(_data, NpcStore.Json);
        try { DataFile.WriteAtomic(_path, text); }
        catch (Exception e) { _warn?.Invoke("Saving the gallery's notes: " + e.Message); }
    }

    private static Photo Copy(Photo p) => new() { File = p.File, Day = p.Day, Clock = p.Clock, Place = p.Place, Minute = p.Minute, Child = p.Child, Ours = p.Ours,
                                                  Size = p.Size, WrittenUtc = p.WrittenUtc };
}
