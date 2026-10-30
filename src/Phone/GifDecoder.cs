using System;
using System.Collections.Generic;

namespace NivalisAIChat.Phone;

/// <summary>
/// The phone's wallpaper can be a GIF (the author: "any image extension url or gif extension url"), and Unity reads
/// only PNG and JPG, so this reads GIFs: GIF87a and GIF89a, global and local colour tables, transparency, the three ways a
/// frame is cleared (disposal), interlacing, each frame's delay. Every frame comes out whole (drawn over the ones before,
/// as a browser shows it), shrunk to at most <c>maxSide</c> on its long side, as RGBA rows. Limits keep a huge or broken
/// file from taking the game's memory: too big, too many frames, or unreadable is an error, never a crash. No Unity, tested.
/// </summary>
public static class GifDecoder
{
    public sealed class Frame
    {
        /// <summary>RGBA, 4 bytes a pixel, rows top first (bottom first with <c>bottomUp</c>).</summary>
        public byte[] Pixels = Array.Empty<byte>();
        public int DelayMs;
    }

    public sealed class Image
    {
        public int Width, Height;
        public List<Frame> Frames = new();
    }

    public const int MaxCanvas = 4096, DefaultMaxFrames = 150;
    /// <summary>All frames together (after shrinking): 96 MB of pixels at most.</summary>
    public const long MaxOutputBytes = 96L * 1024 * 1024;
    /// <summary>
    /// Every frame's area together, before shrinking (the full audit, A-006): a small file can claim 150 frames of 4096x4096
    /// that each cost a full pass, so the work is capped too (256M pixels: a long animation at 1000x1000 still fits).
    /// </summary>
    public const long MaxDrawnPixels = 256L * 1024 * 1024;

    public static bool IsGif(byte[] data) =>
        data.Length >= 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8' && (data[4] == '7' || data[4] == '9') && data[5] == 'a';

    /// <summary>Reads <paramref name="data"/>. Throws <see cref="FormatException"/> for a file it can't or won't read.</summary>
    public static Image Decode(byte[] data, int maxSide = 512, int maxFrames = DefaultMaxFrames, bool bottomUp = false)
    {
        if (!IsGif(data)) throw new FormatException("not a GIF");
        var r = new Reader(data, 6);
        int w = r.U16(), h = r.U16();
        if (w <= 0 || h <= 0 || w > MaxCanvas || h > MaxCanvas) throw new FormatException($"a GIF of {w}x{h} is too big");
        int packed = r.U8();
        int bgIndex = r.U8();
        r.U8(); // aspect
        byte[]? global = (packed & 0x80) != 0 ? r.Bytes(3 * (1 << ((packed & 7) + 1))) : null;
        _ = bgIndex; // the background is drawn transparent, as browsers do

        double shrink = Math.Min(1.0, (double)Math.Max(16, maxSide) / Math.Max(w, h));
        int ow = Math.Max(1, (int)Math.Round(w * shrink)), oh = Math.Max(1, (int)Math.Round(h * shrink));
        var image = new Image { Width = ow, Height = oh };

        var canvas = new byte[w * h * 4];
        byte[]? saved = null;
        int delay = 0, transparent = -1, disposal = 0;
        long outBytes = 0, drawn = 0;
        while (true)
        {
            if (r.AtEnd) break; // no trailer: what was read is kept
            int block = r.U8();
            if (block == 0x3B) break;
            if (block == 0x21)
            {
                int label = r.U8();
                if (label == 0xF9)
                {
                    int size = r.U8();
                    var gce = r.Bytes(size);
                    if (size >= 4)
                    {
                        disposal = (gce[0] >> 2) & 7;
                        transparent = (gce[0] & 1) != 0 ? gce[3] : -1;
                        delay = gce[1] | (gce[2] << 8);
                    }
                    r.SkipSubBlocks();
                }
                else r.SkipSubBlocks();
                continue;
            }
            if (block != 0x2C) throw new FormatException($"an unknown block (0x{block:X2})");

            int fx = r.U16(), fy = r.U16(), fw = r.U16(), fh = r.U16();
            int fp = r.U8();
            if (fw <= 0 || fh <= 0 || fw > MaxCanvas || fh > MaxCanvas) throw new FormatException($"a frame of {fw}x{fh}");
            drawn += (long)fw * fh;
            if (drawn > MaxDrawnPixels) throw new FormatException("too much to draw");
            byte[]? local = (fp & 0x80) != 0 ? r.Bytes(3 * (1 << ((fp & 7) + 1))) : null;
            bool interlaced = (fp & 0x40) != 0;
            var palette = local ?? global ?? throw new FormatException("a frame without colours");
            int minCode = r.U8();
            if (minCode < 2 || minCode > 8) throw new FormatException("a broken frame");
            var indices = Lzw(r.SubBlocks(), minCode, fw * fh);

            if (disposal == 3) saved = (byte[])canvas.Clone();
            Draw(canvas, w, h, indices, palette, fx, fy, fw, fh, interlaced, transparent);

            if (image.Frames.Count >= maxFrames) throw new FormatException($"more than {maxFrames} frames");
            outBytes += (long)ow * oh * 4;
            if (outBytes > MaxOutputBytes) throw new FormatException("too many frames at this size");
            image.Frames.Add(new Frame { Pixels = Shrink(canvas, w, h, ow, oh, bottomUp), DelayMs = delay <= 1 ? 100 : delay * 10 });

            // How this frame is cleared before the next one.
            if (disposal == 2) Clear(canvas, w, h, fx, fy, fw, fh);
            else if (disposal == 3 && saved != null) canvas = saved;
            saved = null;
            delay = 0; transparent = -1; disposal = 0;
        }
        if (image.Frames.Count == 0) throw new FormatException("a GIF with no pictures");
        return image;
    }

    private static void Draw(byte[] canvas, int w, int h, byte[] idx, byte[] palette, int fx, int fy, int fw, int fh, bool interlaced, int transparent)
    {
        int colours = palette.Length / 3;
        int row = 0;
        // The interlaced order: every 8th row from 0, every 8th from 4, every 4th from 2, every 2nd from 1.
        int pass = 0, step = interlaced ? 8 : 1, y = 0;
        for (int i = 0; i < fh; i++)
        {
            int ry = interlaced ? y : i;
            int cy = fy + ry;
            if (cy >= 0 && cy < h)
            {
                for (int x = 0; x < fw; x++)
                {
                    int p = row * fw + x;
                    if (p >= idx.Length) break;
                    int c = idx[p];
                    if (c == transparent || c >= colours) continue;
                    int cx = fx + x;
                    if (cx < 0 || cx >= w) continue;
                    int o = (cy * w + cx) * 4;
                    canvas[o] = palette[c * 3];
                    canvas[o + 1] = palette[c * 3 + 1];
                    canvas[o + 2] = palette[c * 3 + 2];
                    canvas[o + 3] = 255;
                }
            }
            row++;
            if (interlaced)
            {
                y += step;
                while (y >= fh && pass < 3)
                {
                    pass++;
                    (y, step) = pass switch { 1 => (4, 8), 2 => (2, 4), _ => (1, 2) };
                }
            }
        }
    }

    private static void Clear(byte[] canvas, int w, int h, int fx, int fy, int fw, int fh)
    {
        int x0 = Math.Max(0, fx), x1 = Math.Min(w, fx + fw);
        if (x1 <= x0) return;
        for (int y = Math.Max(0, fy); y < Math.Min(h, fy + fh); y++)
            Array.Clear(canvas, (y * w + x0) * 4, (x1 - x0) * 4);
    }

    /// <summary>The canvas at the output size (each output pixel the average of the source pixels under it).</summary>
    private static byte[] Shrink(byte[] src, int w, int h, int ow, int oh, bool bottomUp)
    {
        var dst = new byte[ow * oh * 4];
        for (int oy = 0; oy < oh; oy++)
        {
            int y0 = oy * h / oh, y1 = Math.Max(y0 + 1, (oy + 1) * h / oh);
            int dy = bottomUp ? oh - 1 - oy : oy;
            for (int ox = 0; ox < ow; ox++)
            {
                int x0 = ox * w / ow, x1 = Math.Max(x0 + 1, (ox + 1) * w / ow);
                long r = 0, g = 0, b = 0, a = 0;
                int n = 0;
                // At most a 4x4 sample of the source block: enough for a wallpaper, quick for big ones.
                int sy = Math.Max(1, (y1 - y0) / 4), sx = Math.Max(1, (x1 - x0) / 4);
                for (int y = y0; y < y1; y += sy)
                    for (int x = x0; x < x1; x += sx)
                    {
                        int o = (y * w + x) * 4;
                        int al = src[o + 3];
                        r += src[o] * al; g += src[o + 1] * al; b += src[o + 2] * al; a += al;
                        n++;
                    }
                int d = (dy * ow + ox) * 4;
                if (a > 0) { dst[d] = (byte)(r / a); dst[d + 1] = (byte)(g / a); dst[d + 2] = (byte)(b / a); }
                dst[d + 3] = (byte)(a / Math.Max(1, n));
            }
        }
        return dst;
    }

    /// <summary>The GIF flavour of LZW: variable-width codes up to 12 bits, a clear code and an end code.</summary>
    private static byte[] Lzw(byte[] data, int minCode, int pixels)
    {
        var output = new byte[Math.Max(0, pixels)];
        int clear = 1 << minCode, end = clear + 1;
        var prefix = new short[4096];
        var suffix = new byte[4096];
        var stack = new byte[4097];
        int codeSize = minCode + 1, next = end + 1, old = -1;
        byte first = 0;
        int bits = 0, buffer = 0, pos = 0, outPos = 0;
        for (int i = 0; i < clear; i++) { prefix[i] = -1; suffix[i] = (byte)i; }
        while (outPos < output.Length)
        {
            while (bits < codeSize)
            {
                if (pos >= data.Length) return output; // short data: the rest stays the first colour
                buffer |= data[pos++] << bits;
                bits += 8;
            }
            int code = buffer & ((1 << codeSize) - 1);
            buffer >>= codeSize;
            bits -= codeSize;
            if (code == clear) { codeSize = minCode + 1; next = end + 1; old = -1; continue; }
            if (code == end) break;
            if (old == -1)
            {
                if (code >= clear) throw new FormatException("a broken frame");
                output[outPos++] = suffix[code];
                old = code; first = suffix[code];
                continue;
            }
            int sp = 0, c = code;
            if (code >= next)
            {
                if (code > next) throw new FormatException("a broken frame");
                stack[sp++] = first;
                c = old;
            }
            while (c >= clear)
            {
                if (sp >= stack.Length - 1) throw new FormatException("a broken frame");
                stack[sp++] = suffix[c];
                c = prefix[c];
            }
            first = suffix[c];
            stack[sp++] = first;
            while (sp > 0 && outPos < output.Length) output[outPos++] = stack[--sp];
            if (next < 4096)
            {
                prefix[next] = (short)old;
                suffix[next] = first;
                next++;
                if (next == (1 << codeSize) && codeSize < 12) codeSize++;
            }
            old = code;
        }
        return output;
    }

    private sealed class Reader
    {
        private readonly byte[] _d;
        private int _p;
        public Reader(byte[] d, int p) { _d = d; _p = p; }
        public bool AtEnd => _p >= _d.Length;
        public int U8() => _p < _d.Length ? _d[_p++] : throw new FormatException("the file ends early");
        public int U16() { int a = U8(); return a | (U8() << 8); }
        public byte[] Bytes(int n)
        {
            if (n < 0 || _p + n > _d.Length) throw new FormatException("the file ends early");
            var b = new byte[n];
            Buffer.BlockCopy(_d, _p, b, 0, n);
            _p += n;
            return b;
        }
        public void SkipSubBlocks() { int n; while ((n = U8()) != 0) { if (_p + n > _d.Length) throw new FormatException("the file ends early"); _p += n; } }
        public byte[] SubBlocks()
        {
            var parts = new List<(int at, int n)>();
            int total = 0, n;
            while ((n = U8()) != 0)
            {
                if (_p + n > _d.Length) { n = _d.Length - _p; parts.Add((_p, n)); total += n; _p = _d.Length; break; }
                parts.Add((_p, n)); total += n; _p += n;
            }
            var b = new byte[total];
            int o = 0;
            foreach (var (at, len) in parts) { Buffer.BlockCopy(_d, at, b, o, len); o += len; }
            return b;
        }
    }
}
