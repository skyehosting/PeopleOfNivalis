using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NivalisAIChat.Voice;

/// <summary>One provider written on a line of the Providers block, switched on or off (a "#" in front).</summary>
public sealed record ProviderEntry(int Line, bool On, VoiceProvider Provider, string Endpoint, string Key, double Chance, string Voices, string Model,
    double Limit = 0, string Per = "month", int ResetDay = 0);

/// <summary>
/// CF-09: the voice Providers block as the settings menu edits it. Each line holding one provider, on or switched off with
/// a "#", is an entry; notes and the ready-made lists' comments stay as they are. A change rewrites only that provider's
/// own line (switching it off or on just adds or takes away the "#"), so the block stays as the player wrote it. No Unity.
/// </summary>
public static class ProviderLines
{
    private const string OnPrefix = "    ", OffPrefix = "#   ";

    /// <summary>The providers in the block, in order (a line with more than one provider on it isn't editable here, so it's left out).</summary>
    public static List<ProviderEntry> Read(IReadOnlyList<string> lines)
    {
        var list = new List<ProviderEntry>();
        for (int i = 0; i < lines.Count; i++)
        {
            string t = (lines[i] ?? "").Trim();
            bool on = !t.StartsWith("#");
            string body = on ? t : t.TrimStart('#').Trim();
            if (!body.StartsWith("(")) continue;
            var tuples = VoiceProviderList.Tuples(body).ToList();
            if (tuples.Count != 1) continue;
            var f = VoiceProviderList.SplitTopLevel(tuples[0]);
            if (f.Count < 3) continue;
            var provider = VoiceProviderList.Detect(f[0]);
            if (provider == VoiceProvider.None) continue;
            double.TryParse(f[2].TrimEnd('%').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double chance);
            string model = "";
            double limit = 0;
            string per = "month";
            int resetDay = 0;
            var voices = new List<string>();
            foreach (var item in f.Skip(3).Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                if (item.StartsWith("model:", StringComparison.OrdinalIgnoreCase) || item.StartsWith("model=", StringComparison.OrdinalIgnoreCase)) model = item[6..].Trim();
                else if (VoiceProviderList.Limit(item) is { } l) (limit, per) = l;
                else if (VoiceProviderList.LooksLikeLimit(item)) { } // one it can't read: not a voice either
                else if (VoiceProviderList.ResetDay(item) is { } d) resetDay = d;
                else if (VoiceProviderList.LooksLikeReset(item)) { }
                else if (!item.Equals("auto", StringComparison.OrdinalIgnoreCase) && !item.Equals("all", StringComparison.OrdinalIgnoreCase)) voices.Add(item);
            }
            list.Add(new ProviderEntry(i, on, provider, f[0].Trim(), f[1].Trim(), double.IsFinite(chance) && chance >= 0 ? chance : 0,
                voices.Count > 0 ? string.Join(", ", voices) : "auto", model, limit, per, resetDay));
        }
        return list;
    }

    /// <summary>How an entry is written: "    (endpoint, key, chance, model: m, voices)," with a "#" in front when it's off.</summary>
    public static string Write(ProviderEntry e)
    {
        var tail = new List<string>();
        if (e.Model.Trim().Length > 0) tail.Add("model: " + e.Model.Trim());
        if (e.Limit > 0) tail.Add($"limit: {Math.Round(e.Limit).ToString(CultureInfo.InvariantCulture)}/{(e.Per is "day" or "week" ? e.Per : "month")}");
        if (e.ResetDay is >= 1 and <= 31) tail.Add($"resets: {e.ResetDay.ToString(CultureInfo.InvariantCulture)}"); // FB-59
        tail.Add(e.Voices.Trim().Length > 0 ? e.Voices.Trim() : "auto");
        string chance = Math.Round(Math.Max(0, e.Chance), 2).ToString(CultureInfo.InvariantCulture);
        return $"{(e.On ? OnPrefix : OffPrefix)}({e.Endpoint.Trim()}, {e.Key.Trim()}, {chance}, {string.Join(", ", tail)}),";
    }

    /// <summary>The block with one entry's line written again (every other line as it was).</summary>
    public static List<string> With(IReadOnlyList<string> lines, ProviderEntry e)
    {
        var next = lines.ToList();
        if (e.Line >= 0 && e.Line < next.Count) next[e.Line] = Write(e);
        return next;
    }

    /// <summary>A new provider, switched on with its usual address and every voice, after the last provider that's switched on.</summary>
    public static List<string> Added(IReadOnlyList<string> lines, VoiceProvider provider, string url = "")
    {
        var next = lines.ToList();
        var entries = Read(lines);
        // After the last one switched on; with none on, before the first (the switched-off examples stay under it).
        int at = entries.Any(x => x.On) ? entries.Where(x => x.On).Max(x => x.Line) + 1 : entries.Count > 0 ? entries.Min(x => x.Line) : 0;
        string endpoint = provider == VoiceProvider.OpenAICompatible && url.Trim().Length == 0 ? "http://localhost:8880/v1" : VoiceProviderList.DefaultEndpoint(provider, url);
        next.Insert(Math.Clamp(at, 0, next.Count), Write(new ProviderEntry(-1, true, provider, endpoint, "", 50, "auto", "")));
        return next;
    }

    /// <summary>The block without this entry's line.</summary>
    public static List<string> Removed(IReadOnlyList<string> lines, ProviderEntry e)
    {
        var next = lines.ToList();
        if (e.Line >= 0 && e.Line < next.Count) next.RemoveAt(e.Line);
        return next;
    }

    /// <summary>A real key is there (not empty, not the example's "your-...-key").</summary>
    public static bool KeySet(string key)
    {
        string k = (key ?? "").Trim();
        return k.Length > 0 && !k.StartsWith("your-", StringComparison.OrdinalIgnoreCase) && !k.StartsWith("paste", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The provider's name as the menu shows it.</summary>
    public static string Name(VoiceProvider p) => p switch
    {
        VoiceProvider.OpenAI => "OpenAI",
        VoiceProvider.ElevenLabs => "ElevenLabs",
        VoiceProvider.Azure => "Azure",
        VoiceProvider.Google => "Google",
        VoiceProvider.Deepgram => "Deepgram",
        VoiceProvider.Windows => "Windows voices",
        _ => "Local or other server",
    };

    /// <summary>The providers the menu can add, by name.</summary>
    public static readonly (string name, VoiceProvider provider)[] Addable =
    {
        ("OpenAI", VoiceProvider.OpenAI), ("ElevenLabs", VoiceProvider.ElevenLabs), ("Azure", VoiceProvider.Azure),
        ("Google", VoiceProvider.Google), ("Deepgram", VoiceProvider.Deepgram), ("Local or other server", VoiceProvider.OpenAICompatible),
        ("Windows voices", VoiceProvider.Windows),
    };
}
