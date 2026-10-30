using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NivalisAIChat.Npc;

/// <summary>
/// FB-61 (the author: "memories and conversations were tied to a specific save and would only fully save when the user
/// saves on the save they are playing on"): what the mod remembers of the city, kept per game save.
///
/// The mod keeps working on its files where they always were (the working copy). When the game saves a slot, every
/// per-save file is taken into <c>saves\</c>: each version once, compressed, by its content's hash (blobs), and the slot's
/// manifest lists which version each file was. Loading a slot puts its versions back (what was said since it was saved
/// is gone, as asked); saving it again keeps everything. Global files (usage, the under-18 list, voices, settings) are
/// never touched. No Unity, tested.
/// </summary>
public sealed class SaveSnapshots
{
    /// <summary>The per-save files and folders, relative to the data folder (everything else is the player's for good).</summary>
    public static readonly string[] PerSave =
    {
        "npcs", "memory", "world.json", "pairs.json", "circles.json", "visits.json", "story.json",
        "romance.json", "reminders.json",
        "minds.json", "witness.json", // the inner-life round: how people feel, what's on their minds, what they saw
        "phone.json", // the phone: your contacts and texts
        "nightline.json", // Nightline: the city's lines, reactions and comments (FB-71)
    };

    /// <summary>The snapshot taken the first time this runs: the city as it was before memories were per save.</summary>
    public const string Legacy = "_legacy";
    /// <summary>The working copy as it was before a load replaced it ("_before-load-&lt;time&gt;"), the newest few kept.</summary>
    public const string BeforeLoadPrefix = "_before-load-";
    public const int BeforeLoadsKept = 5;
    /// <summary>A slot's previous snapshot, kept when a save replaces it ("_prev-&lt;slot&gt;"): one back, for each slot (review F7).</summary>
    public const string PrevPrefix = "_prev-";

    public sealed class Manifest
    {
        public string Slot { get; set; } = "";
        /// <summary>The game's save file's own hash when it was taken: a copy of that file is the same save.</summary>
        public string Fingerprint { get; set; } = "";
        public DateTime Utc { get; set; }
        public string Version { get; set; } = "";
        /// <summary>Relative path (with '/') → blob hash.</summary>
        public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    private readonly string _data, _root, _blobs, _slots;
    private readonly Action<string> _warn;
    private readonly object _gate = new();

    public SaveSnapshots(string dataDir, Action<string> warn)
    {
        _data = dataDir;
        _root = Path.Combine(dataDir, "saves");
        _blobs = Path.Combine(_root, "blobs");
        _slots = Path.Combine(_root, "slots");
        _warn = warn;
    }

    public string Root => _root;

    /// <summary>Whether anything was ever taken (the first run of FB-61 takes the legacy snapshot).</summary>
    public bool HasLegacy => File.Exists(ManifestPath(Legacy));

    // ------------------------------------------------------------------ taking

    /// <summary>
    /// Takes the working copy as the slot's (a save that worked). Only versions not kept yet are written; the manifest
    /// goes last, atomically, so a crash leaves the slot's previous snapshot. Returns it, or null when it couldn't.
    /// </summary>
    public Manifest? Take(string slot, string fingerprint, string version)
    {
        if (!SafeSlot(slot)) { _warn($"Saves: '{slot}' isn't a name a snapshot can have; not taken."); return null; }
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_blobs);
                Directory.CreateDirectory(_slots);
                var m = new Manifest { Slot = slot, Fingerprint = fingerprint ?? "", Utc = DateTime.UtcNow, Version = version ?? "" };
                foreach (var (rel, full) in WorkingFiles())
                {
                    byte[] bytes;
                    try { bytes = ReadShared(full); }
                    catch (FileNotFoundException) { continue; } // gone between the listing and the read
                    string hash = Hash(bytes);
                    string blob = BlobPath(hash);
                    if (!File.Exists(blob)) WriteBlob(blob, bytes);
                    m.Files[rel] = hash;
                }
                // The slot's snapshot before this one is kept one step back (a save over a mixed copy can be undone).
                string current = ManifestPath(slot);
                // Renamed inside too: a copy still calling itself the slot would be found as the slot (or a copy of it).
                if (!slot.StartsWith("_", StringComparison.Ordinal) && SafeSlot(PrevPrefix + slot) && Find(slot) is { } was)
                    try
                    {
                        was.Slot = PrevPrefix + slot;
                        DataFile.WriteAtomic(ManifestPath(was.Slot), JsonSerializer.Serialize(was, Json), durable: true);
                    }
                    catch { }
                DataFile.WriteAtomic(current, JsonSerializer.Serialize(m, Json), durable: true);
                return m;
            }
            catch (Exception e) { _warn($"Saves: the snapshot for '{slot}' couldn't be taken ({e.GetType().Name}: {e.Message})."); return null; }
        }
    }

    // ------------------------------------------------------------------ finding

    public Manifest? Find(string slot)
    {
        if (!SafeSlot(slot)) return null;
        string p = ManifestPath(slot);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(p), Json); }
        catch (Exception e) { _warn($"Saves: the snapshot for '{slot}' can't be read ({e.GetType().Name})."); return null; }
    }

    /// <summary>A slot snapshot taken from a save file with this content (a copy of that file is the same save).</summary>
    public Manifest? FindByFingerprint(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return null;
        return All().Where(m => m.Fingerprint == fingerprint && !m.Slot.StartsWith("_", StringComparison.Ordinal))
                    .OrderByDescending(m => m.Utc).FirstOrDefault();
    }

    public List<Manifest> All()
    {
        var list = new List<Manifest>();
        if (!Directory.Exists(_slots)) return list;
        foreach (var f in Directory.GetFiles(_slots, "*.json"))
        {
            try { if (JsonSerializer.Deserialize<Manifest>(File.ReadAllText(f), Json) is { } m) list.Add(m); }
            catch { }
        }
        return list;
    }

    /// <summary>
    /// Which snapshot a slot being loaded starts from: its own; else one taken from an identical save file (a copy made
    /// outside the game); else the legacy one (a save from before memories were per save); else none (an empty city).
    /// </summary>
    public (Manifest? from, string why) SourceFor(string slot, string fingerprint)
    {
        if (Find(slot) is { } own) return (own, "its own");
        if (FindByFingerprint(fingerprint) is { } copy) return (copy, $"the same save as '{copy.Slot}' (a copy of it)");
        if (Find(Legacy) is { } legacy) return (legacy, "the people you'd met before memories were kept per save");
        return (null, "none: nobody has met you yet");
    }

    // ------------------------------------------------------------------ putting back

    /// <summary>
    /// Makes the working copy what <paramref name="from"/> holds (null: no per-save files at all). Files whose content is
    /// already right are left alone; files the snapshot doesn't have are removed, but only once they're kept in a blob
    /// (the caller takes a before-load snapshot first). False when anything couldn't be put back (the log says what).
    /// </summary>
    public bool Restore(Manifest? from)
    {
        lock (_gate)
        {
            bool ok = true;
            var want = from?.Files ?? new Dictionary<string, string>(StringComparer.Ordinal);
            // Review F13 / F-SNAP-1: only paths inside the per-save files and real hashes, checked first (before any is used).
            foreach (var (rel, h) in want)
                if (!SafeRel(rel) || !SafeHash(h)) { _warn($"Saves: the snapshot names a file outside the per-save ones, or a damaged one; nothing was put back."); return false; }
            // Every blob it needs is there before anything changes.
            foreach (var hash in want.Values.Distinct())
                if (!File.Exists(BlobPath(hash))) { _warn($"Saves: a kept file ({hash[..12]}…) is missing; nothing was put back."); return false; }
            var have = WorkingFiles().ToDictionary(x => x.rel, x => x.full, StringComparer.Ordinal);
            foreach (var (rel, full) in have)
            {
                if (want.ContainsKey(rel)) continue;
                try
                {
                    byte[] bytes = ReadShared(full);
                    if (!File.Exists(BlobPath(Hash(bytes)))) { _warn($"Saves: {rel} isn't kept anywhere; left as it is."); ok = false; continue; }
                    File.Delete(full);
                }
                catch (FileNotFoundException) { }
                catch (Exception e) { _warn($"Saves: {rel} couldn't be removed ({e.GetType().Name})."); ok = false; }
            }
            foreach (var (rel, hash) in want)
            {
                string full = Path.Combine(_data, rel.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    if (File.Exists(full))
                    {
                        string now = Hash(ReadShared(full));
                        if (now == hash) continue;
                        // Review F1: never written over unless what's there is kept in a blob.
                        if (!File.Exists(BlobPath(now))) { _warn($"Saves: {rel} isn't kept anywhere; left as it is."); ok = false; continue; }
                    }
                    byte[] bytes = ReadBlob(BlobPath(hash));
                    if (Hash(bytes) != hash) { _warn($"Saves: a kept file for {rel} is damaged; left as it is."); ok = false; continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    DataFile.WriteAtomic(full, bytes, durable: true);
                }
                catch (Exception e) { _warn($"Saves: {rel} couldn't be put back ({e.GetType().Name}: {e.Message})."); ok = false; }
            }
            return ok;
        }
    }

    // ------------------------------------------------------------------ tidying

    /// <summary>
    /// Removes the snapshots of slots whose save file is gone (<paramref name="slotExists"/>), all but the newest
    /// before-load ones, and every blob nothing uses any more. The legacy snapshot is kept for good.
    /// </summary>
    public (int slots, int blobs) Collect(Func<string, bool> slotExists)
    {
        lock (_gate)
        {
            int slots = 0, blobs = 0;
            var all = All();
            var before = all.Where(m => m.Slot.StartsWith(BeforeLoadPrefix, StringComparison.Ordinal)).OrderByDescending(m => m.Utc).ToList();
            foreach (var m in all)
            {
                bool keep = m.Slot == Legacy
                            || (m.Slot.StartsWith(BeforeLoadPrefix, StringComparison.Ordinal) ? before.IndexOf(m) < BeforeLoadsKept
                                : m.Slot.StartsWith(PrevPrefix, StringComparison.Ordinal) ? SafeExists(slotExists, m.Slot[PrevPrefix.Length..])
                                : SafeExists(slotExists, m.Slot));
                if (keep) continue;
                try { File.Delete(ManifestPath(m.Slot)); slots++; } catch { }
            }
            var used = new HashSet<string>(All().SelectMany(m => m.Files.Values), StringComparer.Ordinal);
            if (Directory.Exists(_blobs))
                foreach (var f in Directory.GetFiles(_blobs, "*.gz"))
                {
                    if (used.Contains(Path.GetFileNameWithoutExtension(f))) continue;
                    try { File.Delete(f); blobs++; } catch { }
                }
            return (slots, blobs);
        }
    }

    private static bool SafeExists(Func<string, bool> exists, string slot)
    {
        try { return exists(slot); } catch { return true; } // unsure: kept
    }

    // ------------------------------------------------------------------ files

    /// <summary>The per-save files there are now (relative with '/', full path); temp and set-aside files left out.</summary>
    public IEnumerable<(string rel, string full)> WorkingFiles()
    {
        foreach (var entry in PerSave)
        {
            string full = Path.Combine(_data, entry);
            if (File.Exists(full)) { yield return (entry, full); continue; }
            if (!Directory.Exists(full)) continue;
            foreach (var f in Directory.GetFiles(full, "*.json", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
            {
                string rel = Path.GetRelativePath(_data, f).Replace(Path.DirectorySeparatorChar, '/');
                yield return (rel, f);
            }
        }
    }

    /// <summary>A save file's content hash (the fingerprint of a copy of it), or "" when it can't be read.</summary>
    public static string FingerprintOf(string? saveFile)
    {
        try
        {
            if (string.IsNullOrEmpty(saveFile) || !File.Exists(saveFile)) return "";
            using var s = new FileStream(saveFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(s)).ToLowerInvariant();
        }
        catch { return ""; }
    }

    /// <summary>A slot name a manifest can be stored under: the game's own names (save_…, AUTOSAVE_…) or ours (_…).</summary>
    public static bool SafeSlot(string? slot) =>
        !string.IsNullOrWhiteSpace(slot) && slot.Length <= 120 && slot.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ' ')
        && slot.Trim('.', ' ').Length > 0 && !slot.Contains("..", StringComparison.Ordinal);

    private string ManifestPath(string slot) => Path.Combine(_slots, slot + ".json");

    /// <summary>A blob's name: 64 lowercase hex characters (a sha256), never a path.</summary>
    private static bool SafeHash(string? h) => h is { Length: 64 } && h.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>A manifest's file path: under one of the per-save entries, relative, no "..".</summary>
    private static bool SafeRel(string rel) =>
        !string.IsNullOrEmpty(rel) && !Path.IsPathRooted(rel) && !rel.Contains("..", StringComparison.Ordinal) && !rel.Contains('\\')
        && !rel.Contains(':') && !rel.EndsWith('.') && !rel.EndsWith(' ')
        && PerSave.Any(p => rel == p || rel.StartsWith(p + "/", StringComparison.Ordinal));
    private string BlobPath(string hash) => Path.Combine(_blobs, hash + ".gz");

    private static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private static byte[] ReadShared(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static void WriteBlob(string path, byte[] bytes)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(bytes, 0, bytes.Length);
        DataFile.WriteAtomic(path, ms.ToArray(), durable: true);
    }

    private static byte[] ReadBlob(string path)
    {
        using var s = File.OpenRead(path);
        using var gz = new GZipStream(s, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>For /saves: a one-line description of a snapshot.</summary>
    public static string Describe(Manifest m) =>
        $"{m.Slot}: {m.Files.Count} files, taken {m.Utc.ToLocalTime():yyyy-MM-dd HH:mm}{(m.Version.Length > 0 ? " by " + m.Version : "")}";

    internal static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);
}
