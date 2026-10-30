using System;
using System.IO;
using System.Linq;

namespace NivalisAIChat.Phone;

/// <summary>
/// Voice notes on texts (the author: "allow for an npc to send an audio attachment beneath their message that can play ...
/// a play/stop button that gets saved to users disk somewhere ... a media bar attachment with the audio waves (an array of
/// rounded top and bottom vertical strips that form an audio wave, and each strips length ... dependent on the actual audio
/// file"). Kept as 16-bit WAV files; the wave is worked out from the samples. No Unity, tested.
/// </summary>
public static class VoiceNote
{
    public const int Bars = 24, MaxHeight = 9;
    /// <summary>The newest this many notes are kept on the PC; older ones go.</summary>
    public const int Kept = 300;

    /// <summary>A 16-bit PCM WAV of the samples (-1..1).</summary>
    public static byte[] ToWav(float[] samples, int rate, int channels)
    {
        channels = Math.Max(1, channels);
        rate = Math.Max(8000, rate);
        using var mem = new MemoryStream();
        using var w = new BinaryWriter(mem);
        int bytes = samples.Length * 2;
        w.Write("RIFF".ToCharArray()); w.Write(36 + bytes); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate);
        w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data".ToCharArray()); w.Write(bytes);
        foreach (var s in samples) w.Write((short)Math.Round(Math.Clamp(float.IsFinite(s) ? s : 0f, -1f, 1f) * 32767));
        w.Flush();
        return mem.ToArray();
    }

    /// <summary>Reads a WAV this wrote (16-bit PCM). Throws <see cref="FormatException"/> for anything else.</summary>
    public static (float[] samples, int rate, int channels) FromWav(byte[] d)
    {
        if (d.Length < 44 || d[0] != 'R' || d[1] != 'I' || d[2] != 'F' || d[3] != 'F' || d[8] != 'W' || d[9] != 'A' || d[10] != 'V' || d[11] != 'E')
            throw new FormatException("not a WAV");
        int pos = 12, channels = 1, rate = 24000, bits = 16;
        while (pos + 8 <= d.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(d, pos, 4);
            int size = BitConverter.ToInt32(d, pos + 4);
            int body = pos + 8;
            if (size < 0 || body + size > d.Length) size = d.Length - body;
            if (id == "fmt " && size >= 16)
            {
                if (BitConverter.ToInt16(d, body) != 1) throw new FormatException("not PCM");
                channels = Math.Max(1, (int)BitConverter.ToInt16(d, body + 2));
                rate = BitConverter.ToInt32(d, body + 4);
                bits = BitConverter.ToInt16(d, body + 14);
            }
            else if (id == "data")
            {
                if (bits != 16) throw new FormatException($"{bits}-bit audio");
                var samples = new float[size / 2];
                for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(d, body + i * 2) / 32768f;
                return (samples, rate, channels);
            }
            pos = body + size + (size & 1);
        }
        throw new FormatException("no audio in it");
    }

    /// <summary>
    /// The wave the bar shows: <see cref="Bars"/> heights, 1 to <see cref="MaxHeight"/>, each from how loud that stretch of
    /// the note really is (its RMS against the loudest stretch, eased so quiet parts still show).
    /// </summary>
    public static int[] Wave(float[] samples, int bars = Bars)
    {
        bars = Math.Max(1, bars);
        var rms = new double[bars];
        if (samples.Length == 0) return Enumerable.Repeat(1, bars).ToArray();
        for (int b = 0; b < bars; b++)
        {
            int from = (int)((long)samples.Length * b / bars), to = Math.Max(from + 1, (int)((long)samples.Length * (b + 1) / bars));
            double sum = 0;
            for (int i = from; i < to && i < samples.Length; i++) sum += samples[i] * samples[i];
            rms[b] = Math.Sqrt(sum / Math.Max(1, to - from));
        }
        double loudest = Math.Max(1e-6, rms.Max());
        return rms.Select(r => Math.Clamp((int)Math.Round(1 + (MaxHeight - 1) * Math.Sqrt(r / loudest)), 1, MaxHeight)).ToArray();
    }

    /// <summary>The wave as kept with the text ("358..."), and back.</summary>
    public static string WaveText(int[] wave) => string.Concat(wave.Select(h => (char)('0' + Math.Clamp(h, 0, 9))));

    public static int[] ParseWave(string text) =>
        (text ?? "").Where(char.IsDigit).Select(c => Math.Clamp(c - '0', 1, MaxHeight)).Take(Bars).ToArray() is { Length: > 0 } w ? w : Enumerable.Repeat(1, Bars).ToArray();

    /// <summary>"0:07".</summary>
    public static string Length(float seconds)
    {
        int s = Math.Max(0, (int)Math.Ceiling(float.IsFinite(seconds) ? seconds : 0));
        return $"{s / 60}:{s % 60:00}";
    }

    /// <summary>Whether an answer comes with a voice note: the setting's chance, only for a text short enough to say.</summary>
    public static bool Attach(Random rng, bool on, int chancePct, string text) =>
        on && (text ?? "").Trim().Length is > 2 and <= 220 && rng.NextDouble() * 100 < Math.Clamp(chancePct, 0, 100);

    /// <summary>A note's file name inside the voice notes folder (only these are ever read back: no paths).</summary>
    public static bool SafeName(string name) =>
        name.Length is > 4 and < 80 && name.EndsWith(".wav", StringComparison.Ordinal) && name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.') && !name.Contains("..");

    /// <summary>Keeps the newest <see cref="Kept"/> notes in the folder; the rest go.</summary>
    public static void Prune(string dir, int kept = Kept, string pattern = "note-*.wav", Func<string, bool>? inUse = null)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in new DirectoryInfo(dir).GetFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).Skip(Math.Max(1, kept)).Where(f => inUse == null || !inUse(f.Name)))
                try { f.Delete(); } catch { }
        }
        catch { }
    }
}
