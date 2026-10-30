using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace NivalisAIChat.Npc;

/// <summary>
/// Reading and writing the mod's own files so that nothing is lost (audit step 5, ROBUST-4): a file that couldn't be
/// read is never written over, a damaged one is kept, and a write never leaves half a file behind.
/// </summary>
public static class DataFile
{
    public enum Outcome { Missing, Read, Unreadable }

    /// <summary>
    /// The file's text. Missing: there's no file (or it went while being read). Unreadable: another program kept it
    /// locked through <paramref name="tries"/> tries (50·n ms apart), or access was refused. One try, without waiting,
    /// for callers on the main thread or inside a lock.
    /// </summary>
    public static (Outcome outcome, string? text, string error) ReadText(string path, int tries = 4)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return (Outcome.Missing, null, "");
                return (Outcome.Read, File.ReadAllText(path, Encoding.UTF8), "");
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return (Outcome.Missing, null, ""); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The reason without the folder: it's shown to the player, and the path can hold their Windows user name.
                if (attempt + 1 >= Math.Max(1, tries)) return (Outcome.Unreadable, null, e.Message.Replace(path, Path.GetFileName(path)));
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    /// <summary>
    /// A damaged file moved aside as "&lt;file&gt;.&lt;tag&gt;-&lt;time&gt;", kept for the player. The new path, or null if it
    /// couldn't be moved: then the caller must not write over it either.
    /// </summary>
    public static string? MoveAside(string path, string tag)
    {
        try
        {
            string aside = $"{path}.{tag}-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            if (File.Exists(aside)) aside += "-" + Guid.NewGuid().ToString("N")[..6];
            File.Move(path, aside);
            return aside;
        }
        catch { return null; }
    }

    /// <summary>
    /// The first half of a write: the text in a temp file of its own beside <paramref name="path"/>, flushed to the disk
    /// when <paramref name="durable"/> (a power cut can't leave a zero-filled file behind). Needs no lock: callers write
    /// it outside theirs and hold the lock only for <see cref="Commit"/>, so nobody waits on the disk (the main thread
    /// reads through those locks).
    /// </summary>
    public static string WriteTemp(string path, string text, bool durable = true, Encoding? encoding = null)
    {
        string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
            var enc = encoding ?? new UTF8Encoding(false);
            var preamble = enc.GetPreamble(); // a byte-order mark, when the encoding has one (findings #25)
            if (preamble.Length > 0) fs.Write(preamble, 0, preamble.Length);
            var bytes = enc.GetBytes(text);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(durable);
        }
        catch { TryDelete(tmp); throw; }
        return tmp;
    }

    /// <summary>The second half: the temp file swapped in for <paramref name="path"/> (or removed if that fails).</summary>
    public static void Commit(string tmp, string path)
    {
        try
        {
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch { TryDelete(tmp); throw; }
    }

    /// <summary>Writes the whole file or nothing (see <see cref="WriteTemp"/>): <paramref name="durable"/> = false for files written on the main thread.</summary>
    public static void WriteAtomic(string path, string text, bool durable = true, Encoding? encoding = null) =>
        Commit(WriteTemp(path, text, durable, encoding), path);

    /// <summary>The same for bytes as they are (FB-61's snapshots: compressed blobs, files put back exactly).</summary>
    public static void WriteAtomic(string path, byte[] bytes, bool durable = true)
    {
        string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(durable);
            }
        }
        catch { TryDelete(tmp); throw; }
        Commit(tmp, path);
    }

    /// <summary>The bytes are UTF-8 (with or without a BOM), or UTF-16/32 with a BOM: text any editor saved as such.</summary>
    public static bool IsUtf8(byte[] bytes)
    {
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF))) return true;
        try { new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    /// <summary>
    /// A text file and how to write it back without changing a byte the caller didn't change: UTF-8 when it is UTF-8,
    /// otherwise Latin-1, which maps every byte to one character and back (a file saved as "ANSI" keeps its accented
    /// letters). Throws like File.ReadAllBytes.
    /// </summary>
    public static (string text, Encoding encoding) ReadKeepingBytes(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (IsUtf8(bytes))
        {
            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return (reader.ReadToEnd(), EncodingOf(bytes)); // UTF-16 stays UTF-16, a byte-order mark stays (findings #25)
        }
        return (Encoding.Latin1.GetString(bytes), Encoding.Latin1);
    }

    /// <summary>
    /// How to write text back the way these bytes were saved: UTF-16 (either order) or UTF-8 with its byte-order mark when
    /// they start with one, else UTF-8 without (findings #25).
    /// </summary>
    public static Encoding EncodingOf(byte[] start)
    {
        if (start.Length >= 2 && start[0] == 0xFF && start[1] == 0xFE) return new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        if (start.Length >= 2 && start[0] == 0xFE && start[1] == 0xFF) return new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        if (start.Length >= 3 && start[0] == 0xEF && start[1] == 0xBB && start[2] == 0xBF) return new UTF8Encoding(true);
        return new UTF8Encoding(false);
    }

    /// <summary>The encoding a file on disk was saved in (its first bytes; UTF-8 without a mark if it can't be read).</summary>
    public static Encoding EncodingOfFile(string path)
    {
        try
        {
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = new byte[3];
            int got = fs.Read(start, 0, 3);
            return EncodingOf(start.AsSpan(0, got).ToArray());
        }
        catch { return new UTF8Encoding(false); }
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    /// <summary>The one warning a read-only book gives, the first time it would have saved.</summary>
    public static void NotSaved(string path, Action<string>? warn, ref bool warned)
    {
        if (warned) return;
        warned = true;
        warn?.Invoke($"{Path.GetFileName(path)} isn't saved this session: it couldn't be read at the start, so nothing is written over it.");
    }

    /// <summary>
    /// Said once when a file closed by Stop (a hot reload, or a window that couldn't be built) gets a late change: it isn't
    /// written, so this copy of the mod can't write over what a new one saved (audit step 7b and its review).
    /// </summary>
    public static void Stopped(string what, Action<string>? warn, ref bool said)
    {
        if (said) return;
        said = true;
        warn?.Invoke($"The mod has stopped, so a late change to {what} wasn't saved.");
    }

    /// <summary>
    /// A small book's file (voices, pairs, visits, gossip, usage) read at start: what <paramref name="parse"/> made of it,
    /// or null to start empty.
    /// <list type="bullet">
    /// <item>Damaged (a JsonException): moved aside and kept, and the book starts empty; its saves go on.</item>
    /// <item>Locked through the retries, damaged but couldn't be moved, or anything else going wrong: the book starts empty
    /// and <paramref name="readOnly"/> for the session, so the file is never written over (read again at the next start).</item>
    /// </list>
    /// </summary>
    public static T? LoadBook<T>(string path, Func<string, T?> parse, Action<string>? warn, out bool readOnly) where T : class
    {
        readOnly = false;
        string name = Path.GetFileName(path);
        var (outcome, text, error) = ReadText(path);
        if (outcome == Outcome.Missing) return null;
        if (outcome == Outcome.Unreadable)
        {
            readOnly = true;
            warn?.Invoke($"{name} couldn't be read ({error}). It's left as it is: nothing is saved to it until the next start.");
            return null;
        }
        try { return parse(text!); }
        catch (JsonException e)
        {
            if (MoveAside(path, "unreadable") is { } aside)
            {
                warn?.Invoke($"{name} was damaged ({e.Message}); it's kept as {Path.GetFileName(aside)}, and a new one is started.");
                return null;
            }
            readOnly = true;
            warn?.Invoke($"{name} was damaged ({e.Message}) and couldn't be moved aside. It's left as it is: nothing is saved to it until the next start.");
            return null;
        }
        catch (Exception e)
        {
            readOnly = true;
            warn?.Invoke($"{name} couldn't be read ({e.Message}). It's left as it is: nothing is saved to it until the next start.");
            return null;
        }
    }
}
