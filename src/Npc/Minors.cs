using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NivalisAIChat.Npc;

/// <summary>
/// Who counts as under 18 from what's saved about them (Unity-free; the game-side checks are in GameContext). Used
/// wherever a record could otherwise reach the AI, Voyage or venue talk: being flagged is not the only sign, since a
/// file from an older version can be a child's without saying so.
/// </summary>
public static class Minors
{
    /// <summary>The game's two children, Mala Spinx and Silo Mills, by their Person guid.</summary>
    public static readonly HashSet<string> StoryChildGuids = new(StringComparer.OrdinalIgnoreCase)
    {
        "485995e3-a108-4067-bf25-e7ee954ea677",
        "4a061ccb-e01e-42b5-94d0-ccfb7ebc2478",
    };

    private static readonly HashSet<string> StoryChildNames = new(StringComparer.OrdinalIgnoreCase) { "Mala Spinx", "Silo Mills" };

    /// <summary>
    /// FB-50: a story character as the game shows them in a scene (no record needed): one of the game's children by guid or
    /// name, or a description that says they're under 18. Nothing of theirs is kept from the story.
    /// </summary>
    public static bool IsStoryChild(string? guid, string? name, string? description) =>
        StoryChildGuids.Contains(guid ?? "") || StoryChildNames.Contains((name ?? "").Trim()) || CanonAge.FromDescription(description ?? "")?.Child == true;

    /// <summary>A record of one of the game's children: by id ("person_" + their guid) or, for a story character, by name.</summary>
    public static bool IsStoryChild(NpcRecord rec)
    {
        string id = rec.Id ?? "";
        if (id.StartsWith("person_", StringComparison.OrdinalIgnoreCase) && StoryChildGuids.Contains(id["person_".Length..])) return true;
        var v = rec.Visual ?? new NpcVisual();
        return v.Named && (StoryChildNames.Contains(v.GameName ?? "") || StoryChildNames.Contains(rec.Persona?.Name ?? ""));
    }

    /// <summary>
    /// Why this record is someone under 18, or null if nothing says so:
    /// - flagged (from the game, a picture, or an age under 18 when they were made);
    /// - one of the game's children;
    /// - a story character the game describes as a child (for story characters the game's description decides, as
    ///   <see cref="NpcBrain.FixStoryAge"/> does: a stale age in an old file doesn't make an adult a child);
    /// - anyone else, not an android, whose age reads under 18 ("16", "seventeen", "late teens").
    /// An age that can't be read isn't counted here (the picture check handles that when they're made).
    /// </summary>
    public static string? WhyChild(NpcRecord rec) => Judge(rec).why;

    /// <summary>
    /// <see cref="WhyChild"/>, and whether it's for good (<c>lasting</c>: the person goes on the under-18 list). Everything
    /// is, except a story character judged only by the age in their file because it holds no description from the game:
    /// they're treated as under 18 for now, and once the game's description is known (it's copied in when they're met),
    /// that decides.
    /// </summary>
    public static (string? why, bool lasting) Judge(NpcRecord rec)
    {
        var v = rec.Visual ?? new NpcVisual();
        if (v.Minor) return ("flagged under 18", true);
        if (IsStoryChild(rec)) return ("one of the game's children", true);
        if (v.IsAndroid) return (null, false);
        if (v.Named && CanonAge.FromDescription(v.Description) is { } canon) return canon.Child ? ("the game describes a child", true) : (null, false);
        if (!CanonAge.FromSeen(rec.Persona?.Age, unreadableIsChild: false).child) return (null, false);
        bool undescribedStoryCharacter = v.Named && string.IsNullOrWhiteSpace(v.Description);
        return ("their age reads under 18", !undescribedStoryCharacter);
    }

    /// <summary>
    /// Everyone whose saved record says for good that they're under 18 (see <see cref="Judge"/>), read straight from the
    /// files in <paramref name="dir"/> (for seeding the list at start; nothing is moved or written). Unreadable files are
    /// skipped.
    /// </summary>
    public static List<string> Scan(string dir)
    {
        var ids = new List<string>();
        if (!Directory.Exists(dir)) return ids;
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var rec = JsonSerializer.Deserialize<NpcRecord>(File.ReadAllText(file, Encoding.UTF8), NpcStore.ReadJson);
                if (rec == null) continue;
                rec.Repair(); // read as NpcStore.Load reads it: a bad field never hides a child
                if (string.IsNullOrEmpty(rec.Id)) rec.Id = Path.GetFileNameWithoutExtension(file);
                if (Judge(rec) is { why: not null, lasting: true }) ids.Add(rec.Id);
            }
            catch { /* unreadable: whoever loads it later deals with it */ }
        }
        return ids;
    }
}

/// <summary>
/// Everyone known to be under 18, by id, kept in <c>minors.json</c> in the data folder, apart from their records: a
/// reset, a deleted file or an unreadable one can't make a child an adult again. Ids only. Once on the list, always on
/// it (safety first): a mistake is undone by editing the file by hand while the game is closed. Thread-safe; reading
/// never waits.
/// </summary>
public sealed class MinorList
{
    public const string FileName = "minors.json";

    private sealed class Data
    {
        public string About { get; set; } =
            "People of Nivalis: everyone here is under 18, by their id. They are never chatted with or sent to any AI. " +
            "Remove an id only if it was put here by mistake, and only while the game is closed.";
        public List<string> Ids { get; set; } = new();
    }

    private readonly string? _path;
    private readonly Action<string> _warn;
    private readonly object _write = new();
    private volatile HashSet<string> _ids = new(StringComparer.Ordinal);
    /// <summary>The file was there but couldn't be read or moved aside: it is never written over this session.</summary>
    private readonly bool _readOnly;

    /// <summary>Someone was added for the first time (raised on the thread that added them, after the list is saved).</summary>
    public event Action<string>? Added;

    /// <param name="path">The file; null keeps the list in memory only (tests).</param>
    public MinorList(string? path, Action<string>? warn = null)
    {
        _path = path;
        _warn = warn ?? (_ => { });
        if (_path == null) return;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        // A list that couldn't be read before is kept beside this one until the player deletes it: its ids still count.
        try
        {
            string dir = Path.GetDirectoryName(_path)!;
            if (Directory.Exists(dir))
                foreach (var kept in Directory.GetFiles(dir, Path.GetFileName(_path) + ".unreadable-*"))
                {
                    try { ids.UnionWith(Salvage(File.ReadAllText(kept, Encoding.UTF8))); }
                    catch (Exception e) { _warn($"{Path.GetFileName(kept)} couldn't be read ({e.Message})."); }
                }
        }
        catch (Exception e) { _warn($"Looking for older copies of {FileName} failed ({e.Message})."); }

        var (raw, error) = ReadPatiently(_path);
        if (error != null)
        {
            // There but not readable now (in use, no access): left as it is and never written over this session.
            _readOnly = true;
            _warn($"{FileName} couldn't be read ({error}); it's left as it is and not changed this session. " +
                  "Anyone whose own file says they're under 18 still counts.");
        }
        else if (raw != null)
        {
            try
            {
                var data = JsonSerializer.Deserialize<Data>(raw, NpcStore.Json);
                foreach (var id in data?.Ids ?? new List<string>())
                    if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }
            catch (Exception e)
            {
                // Damaged (a hand edit gone wrong): every id still readable in it is kept, and the file is moved aside for the
                // player, never written over. If it can't be moved, nothing is saved this session.
                int before = ids.Count;
                ids.UnionWith(Salvage(raw));
                string kept = _path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                try { File.Move(_path, kept); }
                catch { _readOnly = true; kept = ""; }
                _warn($"{FileName} couldn't be read ({e.Message}); {ids.Count - before} id(s) recovered from it" +
                      (kept.Length > 0 ? $", and it was kept as {Path.GetFileName(kept)} (its ids keep counting until you delete it)." : ", and it's left as it is (not changed this session)."));
            }
        }
        _ids = ids;
    }

    /// <summary>The file's text, trying again briefly while another program has it open; (null, null) when there's none.</summary>
    private static (string? text, string? error) ReadPatiently(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return (File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null, null); }
            catch (FileNotFoundException) { return (null, null); }
            catch (IOException) when (attempt < 4) { System.Threading.Thread.Sleep(50 * (attempt + 1)); }
            catch (Exception e) { return (null, e.Message); }
        }
    }

    /// <summary>Every id that can still be read in a damaged list: its quoted strings, less the field names and the note.</summary>
    internal static IEnumerable<string> Salvage(string raw)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(raw ?? "", "\"(?:[^\"\\\\\\r\\n]|\\\\.)*\""))
        {
            string? s;
            try { s = JsonSerializer.Deserialize<string>(m.Value); } catch { continue; }
            if (string.IsNullOrEmpty(s) || s.Length > 200 || s.Contains(". ") || s.Equals("about", StringComparison.OrdinalIgnoreCase)
                || s.Equals("ids", StringComparison.OrdinalIgnoreCase)) continue;
            yield return s;
        }
    }

    public bool Contains(string? id) => !string.IsNullOrEmpty(id) && _ids.Contains(id);
    public int Count => _ids.Count;
    public IReadOnlyCollection<string> All => _ids;

    /// <summary>Puts someone on the list (and in the file). True if they weren't on it before.</summary>
    public bool Add(string? id) => AddRange(new[] { id }) > 0;

    /// <summary>Puts everyone given on the list, saving once. Returns how many were new.</summary>
    public int AddRange(IEnumerable<string?> ids)
    {
        var added = new List<string>();
        lock (_write)
        {
            HashSet<string>? next = null;
            foreach (var id in ids)
            {
                if (string.IsNullOrEmpty(id) || _ids.Contains(id) || (next != null && next.Contains(id))) continue;
                next ??= new HashSet<string>(_ids, StringComparer.Ordinal);
                next.Add(id);
                added.Add(id);
            }
            if (next == null) return 0;
            _ids = next;
            Save(next);
        }
        foreach (var id in added)
        {
            try { Added?.Invoke(id); } catch (Exception e) { _warn("Handling someone flagged under 18 failed: " + e.Message); }
        }
        return added.Count;
    }

    /// <summary>
    /// Under <c>_write</c>. What's on disk counts too: another copy of the mod (a hot reload) may have added someone since
    /// this one read the file. The list only ever grows, so neither copy drops the other's additions, and they count here
    /// from now on (review of a0fa75f). A file damaged since the start is kept for the player, as at the start.
    /// </summary>
    private void Save(HashSet<string> ids)
    {
        if (_path == null || _readOnly) return; // in memory only: see the constructor
        try
        {
            var (raw, error) = ReadPatiently(_path);
            if (error != null) { _warn($"Couldn't save {FileName} ({error}); the list still holds for this session."); return; }
            if (raw != null)
            {
                HashSet<string> onDisk;
                try
                {
                    onDisk = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var id in JsonSerializer.Deserialize<Data>(raw, NpcStore.Json)?.Ids ?? new List<string>())
                        if (!string.IsNullOrEmpty(id)) onDisk.Add(id);
                }
                catch (JsonException)
                {
                    onDisk = new HashSet<string>(Salvage(raw), StringComparer.Ordinal);
                    string kept = _path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                    try { File.Move(_path, kept); }
                    catch
                    {
                        _warn($"{FileName} was damaged and couldn't be moved aside, so it's left as it is; the list still holds for this session.");
                        return;
                    }
                }
                if (!onDisk.IsSubsetOf(ids))
                {
                    ids = new HashSet<string>(ids, StringComparer.Ordinal);
                    ids.UnionWith(onDisk);
                    _ids = ids;
                }
            }
            var data = new Data();
            data.Ids.AddRange(ids);
            data.Ids.Sort(StringComparer.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            DataFile.WriteAtomic(_path, JsonSerializer.Serialize(data, NpcStore.Json)); // a temp file of its own, flushed
        }
        catch (Exception e) { _warn($"Couldn't save {FileName} ({e.Message}); the list still holds for this session."); }
    }
}
