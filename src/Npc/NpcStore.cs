using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace NivalisAIChat.Npc;

/// <summary>One JSON file per NPC under BepInEx\config\NivalisAIChat\npcs. Writes are atomic.</summary>
public sealed class NpcStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// For reading people (their files, and who the AI says they are): a wrong shape or a null never breaks them (see
    /// <see cref="TolerantJson"/>). Only for records: the other files keep <see cref="Json"/>. Never used to write.
    /// </summary>
    public static readonly JsonSerializerOptions ReadJson = TolerantJson.For(Json);

    /// <summary>
    /// For <see cref="Glance"/>: <see cref="ReadJson"/>, with History and Meetings skipped while parsing (the only lists
    /// that grow for good: the author's Q2). Their types are stored nowhere else in a record. Never used to write.
    /// </summary>
    private static readonly JsonSerializerOptions GlanceJson = Glancing(ReadJson);

    private static JsonSerializerOptions Glancing(JsonSerializerOptions read)
    {
        var o = new JsonSerializerOptions(read);
        o.Converters.Add(new Skipped<ChatLine>());
        o.Converters.Add(new Skipped<Meeting>());
        return o;
    }

    /// <summary>
    /// A list left out while parsing: the reader steps over it (building nothing) and the list is empty. Read only. It must
    /// still be a list of records (or null), as <see cref="Peek"/> and <see cref="Load"/> require: a file they'd call
    /// damaged isn't glanced at as someone (6a review).
    /// </summary>
    private sealed class Skipped<T> : JsonConverter<List<T>>
    {
        public override bool HandleNull => true;
        public override List<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return new List<T>();
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("not a list");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.Null)) throw new JsonException("not a list of records");
                reader.Skip(); // the whole record, unread
            }
            return new List<T>();
        }
        public override void Write(Utf8JsonWriter writer, List<T> value, JsonSerializerOptions options) =>
            throw new NotSupportedException("A glanced record is never written.");
    }

    private readonly string _dir;
    private readonly Action<string> _warn;
    private readonly object _io = new();
    private int _reads;
    private bool _closed, _saidClosed; // under _io

    /// <summary>How many times a record's file has been read (tests: what a caller costs).</summary>
    internal int Reads => Volatile.Read(ref _reads);

    /// <summary>Tests only: runs between finding a file damaged and checking it again before it's moved aside.</summary>
    internal Action<string>? BeforeDamagedMove { get; set; }

    /// <summary>Tests only: runs inside the lock, just before a save is committed (a save held there, Close must wait).</summary>
    internal Action<string>? BeforeCommit { get; set; }

    public NpcStore(string dir, Action<string> warn)
    {
        _dir = dir;
        _warn = warn;
        Directory.CreateDirectory(_dir);
    }

    public string Directory_ => _dir;

    /// <summary>Everyone with a file (their ids, as their files are named), the most recently changed first.</summary>
    public List<string> Ids()
    {
        lock (_io)
            return Directory.GetFiles(_dir, "*.json").OrderByDescending(File.GetLastWriteTimeUtc)
                .Select(f => Path.GetFileNameWithoutExtension(f)).Where(n => n.Length > 0).ToList();
    }

    /// <summary>Whether this person has a file (cheap: no read).</summary>
    public bool Has(string id) => File.Exists(Path.Combine(_dir, FileNameFor(id)));

    /// <summary>FNV-1a of the id, as 16 hex digits: unlike string.GetHashCode, the same in every run.</summary>
    private static string StableHash(string id)
    {
        ulong h = 14695981039346656037UL;
        foreach (char ch in id) { h ^= ch; h *= 1099511628211UL; }
        return h.ToString("x16");
    }

    public static string FileNameFor(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (char c in id)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        string name = sb.ToString();
        if (name.Length > 120) name = name[..80] + "_" + StableHash(id); // the same name every session (audit B9)
        return name + ".json";
    }

    /// <summary>
    /// Their record; null when they have no file (or a damaged one was just moved aside, so they start afresh). A file
    /// that's there but can't be read (another program holds it through a few tries, or it's damaged and couldn't be
    /// moved aside) throws <see cref="NpcFileUnreadableException"/>: it's never taken for "nobody", which would let a new
    /// person be written over them (ROBUST-4). Off the main thread only: it can wait about half a second.
    /// The file is read under the lock and parsed outside it, so nobody else's read or save waits on a big file (PERF-3).
    /// </summary>
    public NpcRecord? Load(string id)
    {
        string path = Path.Combine(_dir, FileNameFor(id));
        for (int attempt = 0, changed = 0; ;)
        {
            var (outcome, text, error) = ReadUnderLock(path);
            if (outcome == DataFile.Outcome.Missing) return null;
            if (outcome == DataFile.Outcome.Unreadable)
            {
                if (attempt >= 3)
                    throw new NpcFileUnreadableException($"[DATA-READ] Couldn't read {Path.GetFileName(path)} right now ({error}). Another program may have it open; try again in a moment.");
                Thread.Sleep(50 * ++attempt); // outside the lock: everyone else's reads and saves carry on meanwhile
                continue;
            }
            var (rec, damage, cause) = Parse(id, path, text!);
            if (damage == null) return rec;
            BeforeDamagedMove?.Invoke(path);
            DataFile.Outcome now;
            string error2;
            lock (_io)
            {
                // Moved aside only if it's still the file that was read: a save that landed meanwhile is read instead.
                (now, var nowText, error2) = DataFile.ReadText(path, tries: 1);
                if (now == DataFile.Outcome.Missing) return null;
                if (now == DataFile.Outcome.Read && nowText == text) return Damaged(path, damage, cause);
            }
            if (now == DataFile.Outcome.Unreadable)
            {
                // Held by another program just now (a virus scan): waited for like any other read, not taken for a change.
                if (attempt >= 3)
                    throw new NpcFileUnreadableException($"[DATA-READ] Couldn't read {Path.GetFileName(path)} right now ({error2}). Another program may have it open; try again in a moment.");
                Thread.Sleep(50 * ++attempt);
                continue;
            }
            if (++changed > 2)
                throw new NpcFileUnreadableException($"[DATA-READ] {Path.GetFileName(path)} kept changing while it was read; it's left as it is. Try again in a moment.");
        }
    }

    /// <summary>The file's text, read under the lock (one try).</summary>
    private (DataFile.Outcome outcome, string? text, string error) ReadUnderLock(string path)
    {
        lock (_io)
        {
            Interlocked.Increment(ref _reads);
            return DataFile.ReadText(path, tries: 1);
        }
    }

    /// <summary>
    /// Their record from its text. Damaged JSON (or no record at all) comes back as <c>damage</c>, for the caller to move
    /// aside; anything else fails and leaves the file alone.
    /// </summary>
    private static (NpcRecord? rec, string? damage, Exception? cause) Parse(string id, string path, string text)
    {
        NpcRecord? rec;
        try { rec = JsonSerializer.Deserialize<NpcRecord>(text, ReadJson); }
        catch (JsonException e) { return (null, e.Message, e); }
        catch (Exception e) { throw new NpcFileUnreadableException($"[DATA-READ] {Path.GetFileName(path)} couldn't be read ({e.Message}); it's left as it is.", e); }
        if (rec == null) return (null, "it holds no record", null);
        try { rec.Repair(); } // a file with nulls in it (an older version, or edited by hand) loads whole
        catch (Exception e) { throw new NpcFileUnreadableException($"[DATA-READ] {Path.GetFileName(path)} couldn't be read ({e.Message}); it's left as it is.", e); }
        if (string.IsNullOrEmpty(rec.Id)) rec.Id = id;
        return (rec, null, null);
    }

    /// <summary>A damaged record is kept for the user (moved aside) and they start afresh; if it can't be moved, nothing is written over it.</summary>
    private NpcRecord? Damaged(string path, string why, Exception? e)
    {
        if (Refused(Path.GetFileName(path))) // under _io (Load's re-check): a stopped copy moves nothing
            throw new NpcFileUnreadableException($"[DATA-READ] {Path.GetFileName(path)} is damaged ({why}); it's left as it is.", e);
        if (DataFile.MoveAside(path, "corrupt") is { } bad)
        {
            _warn($"NPC file {Path.GetFileName(path)} could not be read ({why}); moved to {Path.GetFileName(bad)} and starting fresh.");
            return null;
        }
        throw new NpcFileUnreadableException($"[DATA-READ] {Path.GetFileName(path)} is damaged ({why}) and couldn't be moved aside, so it's left as it is.", e);
    }

    /// <summary>
    /// Their record if it can be read right now: one try, no waiting (fine on the main thread), nothing moved or written.
    /// Null when there's no file, or it's locked or damaged. For looking only: choosing someone, a name, a label.
    /// Safety rule: nothing about a person goes to the AI or Voyage on a Peek alone, unless the record it returned was
    /// cleared as an adult (IsKnownMinor or Minors.Judge); everything that writes, or creates someone, goes through Load.
    /// </summary>
    /// <param name="tries">More than 1 only off the main thread (it waits 50·n ms between tries, outside the lock).</param>
    public NpcRecord? Peek(string id, int tries = 1) => Look(id, ReadJson, tries, glanced: false);

    /// <summary>
    /// <see cref="Peek"/> without their History and Meetings, which are skipped while parsing: a regular's file is mostly
    /// those (kept for good, the author's Q2), so this costs a few milliseconds instead of tens (PERF-3/4). For the main
    /// thread and anything that only looks: a name, a label, who's under 18. A fresh copy every time, and never saved
    /// (<see cref="Save"/> and <see cref="Create"/> refuse one). The same safety rule as Peek.
    /// </summary>
    public NpcRecord? Glance(string id) => Look(id, GlanceJson, 1, glanced: true);

    private NpcRecord? Look(string id, JsonSerializerOptions options, int tries, bool glanced)
    {
        string path = Path.Combine(_dir, FileNameFor(id));
        string? text;
        for (int attempt = 1; ; attempt++)
        {
            var (outcome, read, _) = ReadUnderLock(path);
            if (outcome == DataFile.Outcome.Read) { text = read; break; }
            if (outcome == DataFile.Outcome.Missing || attempt >= tries) return null;
            Thread.Sleep(50 * attempt);
        }
        try // parsed outside the lock (PERF-3)
        {
            var rec = JsonSerializer.Deserialize<NpcRecord>(text!, options);
            if (rec == null) return null;
            rec.Repair();
            if (string.IsNullOrEmpty(rec.Id)) rec.Id = id;
            rec.Glanced = glanced;
            return rec;
        }
        catch { return null; }
    }

    /// <summary>
    /// A hot reload: once this copy has stopped, nothing more is written, moved or created here. The new copy owns the
    /// files now, and a late save of this copy's (a diary still being written) would write over a conversation had in the
    /// new one (LEAK-2, RACE-9, audit step 7b). Waits for a save in progress; what comes later is dropped, said once.
    /// </summary>
    public void Close()
    {
        lock (_io) _closed = true;
    }

    /// <summary>Under <c>_io</c>: true (and said once) once the store is closed.</summary>
    private bool Refused(string what)
    {
        if (!_closed) return false;
        if (!_saidClosed)
        {
            _saidClosed = true;
            _warn($"The mod has stopped, so a late change ({what}) wasn't saved.");
        }
        return true;
    }

    /// <summary>Moves one person's file into <paramref name="backupDir"/> (they become a stranger).</summary>
    public bool Archive(string id, string backupDir)
    {
        string path = Path.Combine(_dir, FileNameFor(id));
        lock (_io)
        {
            if (Refused(Path.GetFileName(path))) return false;
            if (!File.Exists(path)) return false;
            Directory.CreateDirectory(backupDir);
            File.Move(path, Path.Combine(backupDir, Path.GetFileName(path)), true);
            return true;
        }
    }

    /// <summary>Moves every person's file into <paramref name="backupDir"/>. Returns how many.</summary>
    public int ArchiveAll(string backupDir)
    {
        lock (_io)
        {
            if (Refused("everyone's files")) return 0;
            var files = Directory.GetFiles(_dir, "*.json");
            if (files.Length == 0) return 0;
            Directory.CreateDirectory(backupDir);
            foreach (var f in files) File.Move(f, Path.Combine(backupDir, Path.GetFileName(f)), true);
            return files.Length;
        }
    }

    /// <summary>
    /// Builds the JSON readers and writers once, off the main thread (D8, PERF-11): with an empty data folder the first chat
    /// would otherwise pay for it on the main thread (the start-up scan does it for anyone with saved people).
    /// </summary>
    public static void WarmUp()
    {
        JsonSerializer.Serialize(new NpcRecord(), Json);
        JsonSerializer.Deserialize<NpcRecord>("{}", ReadJson);
        JsonSerializer.Deserialize<NpcRecord>("{\"history\":[],\"meetings\":[]}", GlanceJson);
    }

    /// <summary>A glanced record has no History or Meetings: saving it would wipe theirs.</summary>
    private static void NeverGlanced(NpcRecord rec)
    {
        if (rec.Glanced) throw new InvalidOperationException($"A record read with Glance (no history) is never saved ({rec.Id}).");
    }

    public void Save(NpcRecord rec)
    {
        NeverGlanced(rec);
        string path = Path.Combine(_dir, FileNameFor(rec.Id));
        // Written to the disk outside the lock (main-thread Peeks read through it); only the swap is under it.
        string tmp = DataFile.WriteTemp(path, JsonSerializer.Serialize(rec, Json));
        lock (_io)
        {
            if (Refused(Path.GetFileName(path))) { DataFile.TryDelete(tmp); return; }
            BeforeCommit?.Invoke(path);
            DataFile.Commit(tmp, path);
        }
    }

    /// <summary>
    /// Saves someone just created. Refused while a file for them exists: it wasn't read, so it must not be written over by
    /// a new person (ROBUST-4), whatever the load before it did.
    /// </summary>
    public void Create(NpcRecord rec)
    {
        NeverGlanced(rec);
        string path = Path.Combine(_dir, FileNameFor(rec.Id));
        string tmp = DataFile.WriteTemp(path, JsonSerializer.Serialize(rec, Json));
        lock (_io)
        {
            if (Refused(Path.GetFileName(path))) { DataFile.TryDelete(tmp); return; }
            if (File.Exists(path))
            {
                DataFile.TryDelete(tmp);
                throw new NpcFileUnreadableException($"[DATA-READ] {Path.GetFileName(path)} is there but wasn't read, so no new person is written over it. Try again in a moment.");
            }
            DataFile.Commit(tmp, path);
        }
    }
}

/// <summary>
/// A person's file is there but couldn't be read (another program had it open, or it's damaged and couldn't be moved
/// aside). Nothing is created or written over it; the message carries [DATA-READ] for the player.
/// </summary>
public sealed class NpcFileUnreadableException : IOException
{
    public NpcFileUnreadableException(string message, Exception? inner = null) : base(message, inner) { }
}
