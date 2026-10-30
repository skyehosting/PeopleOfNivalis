using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NivalisAIChat.Phone;

/// <summary>
/// The phone's wallpaper (the author: "let the user set their background using any image extension url or gif
/// extension url and it would save and cache so when opening up phone again it would load"). A link is downloaded once,
/// with a size cap and a time limit, into a folder of its own; what it is comes from its first bytes, never from the
/// link's ending (PNG, JPG, GIF, BMP; WEBP is named and refused); then it's kept on the PC with the link it came from,
/// so a link that dies later doesn't take the wallpaper with it. The picture is only ever decoded, never run. No Unity.
/// </summary>
public static class Wallpaper
{
    public const long MaxBytes = 15L * 1024 * 1024;
    public const int MaxUrl = 2048;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    public const string Formats = "A PNG, JPG, GIF or BMP link.";

    public sealed class Saved
    {
        public string Url { get; set; } = "";
        public string Kind { get; set; } = "";
        public string File { get; set; } = "";
        public DateTime SavedUtc { get; set; }
    }

    /// <summary>"png", "jpg", "gif", "bmp", "webp" (which can't be read) or "" from a file's first bytes.</summary>
    public static string Sniff(byte[] d)
    {
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G' && d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A) return "png";
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "jpg";
        if (GifDecoder.IsGif(d)) return "gif";
        if (d.Length >= 26 && d[0] == 'B' && d[1] == 'M') return "bmp";
        if (d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return "webp";
        return "";
    }

    public static bool Readable(string kind) => kind is "png" or "jpg" or "gif" or "bmp";

    /// <summary>The largest side a wallpaper may have, and the most pixels (a picture that says it's bigger isn't decoded).</summary>
    public const int MaxSide = 4096;
    public const long MaxPixels = 16L * 1024 * 1024;

    /// <summary>One at a time: keeping, putting aside and bringing back the wallpaper's files (review PH-2).</summary>
    public static readonly object FilesLock = new();

    /// <summary>
    /// A PNG's or JPG's size as its header says (review PH-1: Unity allocates the whole texture before it can be checked),
    /// or null when it can't be read. GIF and BMP are read by the mod's own decoders, which check their own sizes.
    /// </summary>
    public static (int w, int h)? Dimensions(byte[] d, string kind)
    {
        try
        {
            if (kind == "png" && d.Length >= 24)
                return ((d[16] << 24) | (d[17] << 16) | (d[18] << 8) | d[19], (d[20] << 24) | (d[21] << 16) | (d[22] << 8) | d[23]);
            if (kind == "jpg")
            {
                int i = 2;
                while (i + 9 < d.Length)
                {
                    if (d[i] != 0xFF) { i++; continue; }
                    int marker = d[i + 1];
                    if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                    int len = (d[i + 2] << 8) | d[i + 3];
                    if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                        return ((d[i + 7] << 8) | d[i + 8], (d[i + 5] << 8) | d[i + 6]);
                    if (len < 2) return null;
                    i += 2 + len;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Whether a picture of this size may be read (not when its size can't be told).</summary>
    public static bool SizeOk((int w, int h)? size) =>
        size is { } s && s.w > 0 && s.h > 0 && s.w <= MaxSide && s.h <= MaxSide && (long)s.w * s.h <= MaxPixels;

    /// <summary>A host on this PC or the local network (review PH-7: a link or a redirect never goes there).</summary>
    public static bool LocalHost(Uri uri)
    {
        string host = uri.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        return System.Net.IPAddress.TryParse(host, out var ip) && LocalAddress(ip);
    }

    /// <summary>An address on this PC or the local network (IPv4-mapped IPv6 seen as its IPv4: review PH-7c).</summary>
    public static bool LocalAddress(System.Net.IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (System.Net.IPAddress.IsLoopback(ip) || ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any)) return true;
        var b = ip.GetAddressBytes();
        if (b.Length == 4) return b[0] == 10 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC;
    }

    /// <summary>The link, checked: an http or https address and nothing else (no files on the PC, no other schemes).</summary>
    public static (Uri? uri, string error) Check(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return (null, "Paste a link to a picture first.");
        if (text.Length > MaxUrl) return (null, "That link is too long.");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
            return (null, "That isn't a web link (it should start with https://).");
        if (LocalHost(uri)) return (null, "That link points at this PC or your local network.");
        return (uri, "");
    }

    /// <summary>The message for a file that isn't a picture the phone reads.</summary>
    public static string Unreadable(string kind) => kind == "webp"
        ? "WEBP pictures can't be read. Your wallpaper didn't change."
        : "That link isn't a PNG, JPG, GIF or BMP picture. Your wallpaper didn't change.";

    /// <summary>
    /// Downloads <paramref name="uri"/>: at most <see cref="MaxBytes"/> and <see cref="Timeout"/>. Reports (read, total;
    /// total -1 when unknown). Returns the bytes, or an error the phone can show.
    /// </summary>
    public static async Task<(byte[]? data, string error)> DownloadAsync(HttpClient http, Uri uri, Action<long, long>? progress, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            // Redirects followed by hand (at most 5), each one checked like the link itself (review PH-7).
            HttpResponseMessage? first = null;
            var at = uri;
            for (int hop = 0; ; hop++)
            {
                var req = new HttpRequestMessage(HttpMethod.Get, at);
                req.Headers.TryAddWithoutValidation("Accept", "image/png,image/jpeg,image/gif,image/bmp,image/*;q=0.8");
                var r = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                int code = (int)r.StatusCode;
                if (code is >= 300 and < 400 && r.Headers.Location is { } loc && hop < 5)
                {
                    var next = loc.IsAbsoluteUri ? loc : new Uri(at, loc);
                    r.Dispose();
                    if (next.Scheme is not ("http" or "https") || LocalHost(next)) return (null, "That link sends you somewhere it can't go. Your wallpaper didn't change.");
                    at = next;
                    continue;
                }
                first = r;
                break;
            }
            using var res = first;
            if (!res.IsSuccessStatusCode) return (null, $"Couldn't download that link ({(int)res.StatusCode}). Your wallpaper didn't change.");
            long total = res.Content.Headers.ContentLength ?? -1;
            if (total > MaxBytes) return (null, $"That picture is too big ({total / (1024 * 1024)} MB; {MaxBytes / (1024 * 1024)} MB at most).");
            using var stream = await res.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var mem = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int n;
            while ((n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token).ConfigureAwait(false)) > 0)
            {
                mem.Write(buffer, 0, n);
                if (mem.Length > MaxBytes) return (null, $"That picture is too big ({MaxBytes / (1024 * 1024)} MB at most).");
                progress?.Invoke(mem.Length, total);
            }
            return (mem.ToArray(), "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "That link took too long. Your wallpaper didn't change.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return (null, "Couldn't download that link. Your wallpaper didn't change.");
        }
    }

    /// <summary>
    /// Keeps a downloaded picture as the wallpaper: written into <c>incoming</c> first, then moved over the old one in
    /// <paramref name="dir"/>, with wallpaper.json saying which link it came from. Throws on a write that fails.
    /// </summary>
    public static Saved Keep(string dir, byte[] data, string kind, string url)
    {
        if (!Readable(kind)) throw new ArgumentException("not a picture the phone reads", nameof(kind));
        lock (FilesLock) return KeepLocked(dir, data, kind, url);
    }

    private static Saved KeepLocked(string dir, byte[] data, string kind, string url)
    {
        Directory.CreateDirectory(dir);
        string incoming = Path.Combine(dir, "incoming");
        Directory.CreateDirectory(incoming);
        string name = "wallpaper." + kind;
        string temp = Path.Combine(incoming, name + ".part");
        File.WriteAllBytes(temp, data);
        string final = Path.Combine(dir, name);
        File.Move(temp, final, overwrite: true);
        // The record written whole (a crash mid-write never loses the kept picture), then the older pictures go (the full
        // audit, A-010: they went first, before).
        var saved = new Saved { Url = url, Kind = kind, File = name, SavedUtc = DateTime.UtcNow };
        Npc.DataFile.WriteAtomic(Path.Combine(dir, "wallpaper.json"), JsonSerializer.Serialize(saved), durable: false);
        foreach (var old in Directory.GetFiles(dir, "wallpaper.*"))
            if (!old.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !string.Equals(old, final, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(old); } catch { }
        return saved;
    }

    /// <summary>The kept wallpaper (its record and bytes), or null for none (or one that can't be read back).</summary>
    public static (Saved info, byte[] data)? Load(string dir)
    {
        try
        {
            string meta = Path.Combine(dir, "wallpaper.json");
            if (!File.Exists(meta)) return null;
            var info = JsonSerializer.Deserialize<Saved>(File.ReadAllText(meta));
            if (info == null || info.File.Length == 0 || info.File.IndexOfAny(new[] { '/', '\\' }) >= 0 || !info.File.StartsWith("wallpaper.", StringComparison.Ordinal)) return null;
            string path = Path.Combine(dir, info.File);
            if (!File.Exists(path) || new FileInfo(path).Length > MaxBytes) return null;
            var data = File.ReadAllBytes(path);
            string kind = Sniff(data);
            if (!Readable(kind)) return null;
            info.Kind = kind;
            return (info, data);
        }
        catch { return null; }
    }

    /// <summary>
    /// Default with Undo (the usability judge: "delete the saved file only when the Undo lapses"): the kept wallpaper is put
    /// aside in "previous", and comes back with <see cref="Unstash"/>; <see cref="DropStash"/> deletes it.
    /// </summary>
    public static void Stash(string dir)
    {
        lock (FilesLock)
        try
        {
            if (!Directory.Exists(dir)) return;
            string prev = Path.Combine(dir, "previous");
            DropStash(dir);
            Directory.CreateDirectory(prev);
            foreach (var f in Directory.GetFiles(dir, "wallpaper.*")) File.Move(f, Path.Combine(prev, Path.GetFileName(f)), overwrite: true);
        }
        catch { }
    }

    public static void Unstash(string dir)
    {
        lock (FilesLock)
        try
        {
            string prev = Path.Combine(dir, "previous");
            if (!Directory.Exists(prev)) return;
            foreach (var f in Directory.GetFiles(dir, "wallpaper.*")) try { File.Delete(f); } catch { }
            foreach (var f in Directory.GetFiles(prev, "wallpaper.*")) File.Move(f, Path.Combine(dir, Path.GetFileName(f)), overwrite: true);
            Directory.Delete(prev, recursive: true);
        }
        catch { }
    }

    public static void DropStash(string dir)
    {
        lock (FilesLock)
        try { string prev = Path.Combine(dir, "previous"); if (Directory.Exists(prev)) Directory.Delete(prev, recursive: true); } catch { }
    }

    /// <summary>Back to the default wallpaper: the kept one goes.</summary>
    public static void Forget(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "wallpaper.*")) try { File.Delete(f); } catch { }
        }
        catch { }
    }
}

/// <summary>Uncompressed BMPs (8, 24 and 32 bits a pixel), for the wallpaper: Unity reads only PNG and JPG. No Unity, tested.</summary>
public static class BmpDecoder
{
    /// <summary>RGBA, rows bottom first (as Unity's textures want them). Throws <see cref="FormatException"/>.</summary>
    public static (int w, int h, byte[] rgba) Decode(byte[] d)
    {
        if (d.Length < 54 || d[0] != 'B' || d[1] != 'M') throw new FormatException("not a BMP");
        int offset = BitConverter.ToInt32(d, 10);
        int header = BitConverter.ToInt32(d, 14);
        if (header < 40) throw new FormatException("an old kind of BMP");
        int w = BitConverter.ToInt32(d, 18), hRaw = BitConverter.ToInt32(d, 22);
        int bpp = BitConverter.ToUInt16(d, 28), compression = BitConverter.ToInt32(d, 30);
        bool topDown = hRaw < 0;
        int h = Math.Abs(hRaw);
        if (w <= 0 || h <= 0 || w > GifDecoder.MaxCanvas || h > GifDecoder.MaxCanvas) throw new FormatException($"a BMP of {w}x{h} is too big");
        if (bpp is not (8 or 24 or 32)) throw new FormatException($"a {bpp}-bit BMP");
        if (!(compression == 0 || (compression == 3 && bpp == 32))) throw new FormatException("a compressed BMP");
        byte[]? palette = null;
        if (bpp == 8)
        {
            int colours = BitConverter.ToInt32(d, 46);
            if (colours <= 0 || colours > 256) colours = 256;
            int at = 14 + header;
            if (at + colours * 4 > d.Length) throw new FormatException("the file ends early");
            palette = new byte[colours * 4];
            Buffer.BlockCopy(d, at, palette, 0, colours * 4);
        }
        int stride = ((w * bpp + 31) / 32) * 4;
        if (offset < 0 || (long)offset + (long)stride * h > d.Length) throw new FormatException("the file ends early");
        bool alpha = bpp == 32 && compression == 0 ? false : bpp == 32;
        var rgba = new byte[w * h * 4];
        bool anyAlpha = false;
        for (int y = 0; y < h; y++)
        {
            int src = offset + y * stride;
            int row = topDown ? h - 1 - y : y; // bottom first out
            for (int x = 0; x < w; x++)
            {
                int o = (row * w + x) * 4;
                if (bpp == 8)
                {
                    int i = d[src + x] * 4;
                    if (palette == null || i + 2 >= palette.Length) continue;
                    rgba[o] = palette[i + 2]; rgba[o + 1] = palette[i + 1]; rgba[o + 2] = palette[i]; rgba[o + 3] = 255;
                }
                else
                {
                    int p = src + x * (bpp / 8);
                    rgba[o] = d[p + 2]; rgba[o + 1] = d[p + 1]; rgba[o + 2] = d[p];
                    rgba[o + 3] = bpp == 32 ? d[p + 3] : (byte)255;
                    if (bpp == 32 && d[p + 3] != 0) anyAlpha = true;
                }
            }
        }
        // Most 32-bit BMPs leave the fourth byte at 0: then it isn't transparency, and everything is opaque.
        if (bpp == 32 && (!alpha || !anyAlpha)) for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
        return (w, h, rgba);
    }
}
