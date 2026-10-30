using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace NivalisAIChat.Voice;

/// <summary>
/// The Providers block of the settings file: one voice provider per line, as (endpoint, api key, chance, voices).
/// <code>
/// Providers = [
///     (https://api.openai.com/v1, , 50, auto),
/// #   (https://api.elevenlabs.io, your-elevenlabs-key, 50, auto),
/// ]
/// </code>
/// The provider is recognised from the endpoint. Lines starting with # are switched off. "voices" is "auto" (all of
/// the provider's voices) or a list like "nova:female, onyx:male"; an item "model: name" picks the model.
/// </summary>
public static class VoiceProviderList
{
    /// <summary>
    /// The provider, from the endpoint's host (exactly, so a look-alike such as api.openai.com.example.net is just an
    /// OpenAI-compatible server and never borrows an OpenAI key). None when the endpoint isn't "windows"/"sapi" or an
    /// http(s) address: then it may be a key pasted in the wrong place.
    /// </summary>
    public static VoiceProvider Detect(string endpoint)
    {
        string e = (endpoint ?? "").Trim();
        if (e.Length == 0) return VoiceProvider.None;
        if (e.Equals("windows", StringComparison.OrdinalIgnoreCase) || e.Equals("sapi", StringComparison.OrdinalIgnoreCase)) return VoiceProvider.Windows;
        if (!Uri.TryCreate(e, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return VoiceProvider.None;
        string host = u.Host.ToLowerInvariant();
        bool On(string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
        if (host == "api.openai.com") return VoiceProvider.OpenAI;
        if (On("elevenlabs.io")) return VoiceProvider.ElevenLabs;
        if (On("tts.speech.microsoft.com") || On("cognitiveservices.azure.com")) return VoiceProvider.Azure;
        if (host == "texttospeech.googleapis.com") return VoiceProvider.Google;
        if (On("deepgram.com")) return VoiceProvider.Deepgram;
        return VoiceProvider.OpenAICompatible; // Kokoro and other local or self-hosted servers
    }

    public static string DefaultEndpoint(VoiceProvider p, string url = "") => p switch
    {
        VoiceProvider.OpenAI => "https://api.openai.com/v1",
        VoiceProvider.ElevenLabs => "https://api.elevenlabs.io",
        VoiceProvider.Azure => url.Contains("://") ? url : $"https://{(url.Trim().Length > 0 ? url.Trim() : "eastus")}.tts.speech.microsoft.com",
        VoiceProvider.Google => "https://texttospeech.googleapis.com/v1",
        VoiceProvider.Deepgram => "https://api.deepgram.com/v1/speak",
        VoiceProvider.Windows => "windows",
        _ => url,
    };

    /// <summary>The block's lines → providers. Lines it can't read are skipped and explained in <paramref name="problems"/>.</summary>
    public static List<VoiceSlot> Parse(IEnumerable<string> lines, List<string> problems)
    {
        var slots = new List<VoiceSlot>();
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            foreach (var tuple in Tuples(line))
            {
                var fields = SplitTopLevel(tuple);
                if (fields.Count < 3)
                {
                    problems.Add($"Voices: couldn't read the Providers line {Where(tuple)}; it needs (endpoint, api key, chance, voices).");
                    continue;
                }
                string endpoint = fields[0];
                var provider = Detect(endpoint);
                if (provider == VoiceProvider.None)
                {
                    problems.Add($"Voices: a Providers line {Where(tuple)} doesn't start with an endpoint (an http(s) address, or windows), so it was skipped.");
                    continue;
                }
                string key = fields[1];
                if (key.StartsWith("your-", StringComparison.OrdinalIgnoreCase) || key.StartsWith("paste", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"Voices: the {provider} line still has the example key; paste your own key there.");
                    key = "";
                }
                string chanceText = fields[2].TrimEnd('%').Trim();
                if (!double.TryParse(chanceText, NumberStyles.Float, CultureInfo.InvariantCulture, out double chance) || chance < 0)
                {
                    problems.Add($"Voices: the {provider} line's chance (its third item) isn't a number; using 1."); // never repeated: it may be a key
                    chance = 1;
                }
                string model = "";
                double limit = 0;
                string per = "month";
                int resetDay = 0;
                var voices = new List<string>();
                foreach (var item in fields.Skip(3))
                {
                    string v = item.Trim();
                    if (v.Length == 0 || v.Equals("auto", StringComparison.OrdinalIgnoreCase) || v.Equals("all", StringComparison.OrdinalIgnoreCase)) continue;
                    bool isModel = v.StartsWith("model:", StringComparison.OrdinalIgnoreCase) || v.StartsWith("model=", StringComparison.OrdinalIgnoreCase);
                    // A key typed here would become a voice (or model) that is logged, saved in voices.json and sent out.
                    if (Ai.Redact.PlainlyKey(isModel ? v[6..] : v))
                    {
                        problems.Add($"Voices: the {provider} line has a key among its voices (not repeated here); it was left out. The key goes second in the line.");
                        continue;
                    }
                    if (isModel) model = v[6..].Trim();
                    else if (Limit(v) is { } l) (limit, per) = l;
                    else if (LooksLikeLimit(v))
                        problems.Add($"Voices: the {provider} line's limit isn't one the mod can read (write it like limit: 125000/month); it was left out.");
                    else if (ResetDay(v) is { } d) resetDay = d;
                    else if (LooksLikeReset(v))
                        problems.Add($"Voices: the {provider} line's reset day isn't one the mod can read (write it like resets: 15, a day 1 to 31); it was left out.");
                    else voices.Add(v);
                }
                slots.Add(new VoiceSlot
                {
                    Number = slots.Count + 1, Provider = provider, Url = provider == VoiceProvider.Windows ? "windows" : endpoint,
                    ApiKey = key, Chance = chance, Model = model, Voices = string.Join(", ", voices), Limit = limit, LimitPer = per, ResetDay = resetDay,
                });
            }
        }
        return slots;
    }

    /// <summary>
    /// FB-41: a provider's own limit written in its line, "limit: 125000/month" (or "limit=125000 per week"): the amount in its
    /// unit and its period (day, week or month; month when not said). Null when the item isn't one.
    /// </summary>
    /// <summary>An item meant as a limit ("limit: ..."), read or not: never taken for a voice name.</summary>
    public static bool LooksLikeLimit(string item) =>
        System.Text.RegularExpressions.Regex.IsMatch(item ?? "", @"^\s*limit\s*[:=]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>FB-59: "resets: 15" — the day of the month a monthly limit starts again on (1-31).</summary>
    public static int? ResetDay(string item)
    {
        var m = System.Text.RegularExpressions.Regex.Match(item ?? "", @"^\s*resets?\s*(?:on\s*)?[:=]\s*(?:day\s*)?([0-9]{1,2})\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) && d >= 1 && d <= 31 ? d : null;
    }

    public static bool LooksLikeReset(string item) =>
        System.Text.RegularExpressions.Regex.IsMatch(item ?? "", @"^\s*resets?\s*(?:on\s*)?[:=]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static (double limit, string per)? Limit(string item)
    {
        var m = System.Text.RegularExpressions.Regex.Match(item ?? "", @"^\s*limit\s*[:=]\s*([0-9][0-9_,. ]*)\s*(?:/|per\s+|a\s+)?\s*(day|week|month)?\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!m.Success) return null;
        string digits = m.Groups[1].Value.Replace("_", "").Replace(",", "").Replace(" ", "");
        if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n) || n < 0) return null;
        return (n, m.Groups[2].Success ? m.Groups[2].Value.ToLowerInvariant() : "month");
    }

    /// <summary>Each top-level "( ... )" on the line; a line with no brackets is read as one tuple.</summary>
    internal static IEnumerable<string> Tuples(string line)
    {
        line = line.TrimEnd(',', ' ');
        if (!line.StartsWith("(")) { yield return line; yield break; }
        int depth = 0, start = -1;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '(') { if (depth == 0) start = i + 1; depth++; }
            else if (c == ')' && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0) { yield return line[start..i]; start = -1; }
            }
        }
        if (depth > 0 && start >= 0) yield return line[start..]; // a missing ")" at the end is forgiven
    }

    /// <summary>Splits on commas that aren't inside brackets ("Aria (9BWts...):female" stays whole).</summary>
    /// <summary>
    /// Which line a problem is about, safe to log: only its endpoint, and only when that plainly is one (a key pasted in
    /// the wrong place would otherwise end up in the log). The rest of the line (the API key) is never repeated.
    /// </summary>
    private static string Where(string tuple)
    {
        string first = SplitTopLevel(tuple).FirstOrDefault()?.Trim() ?? "";
        if (first.Equals("windows", StringComparison.OrdinalIgnoreCase) || first.Equals("sapi", StringComparison.OrdinalIgnoreCase)) return "for " + first;
        string shown = Ai.Redact.Url(first); // scheme://host/path only: no user name, password or query
        return shown.Length > 0 && shown != "(not shown)" ? $"for {(shown.Length > 80 ? shown[..80] + "..." : shown)}" : "(not repeated here: it may hold a key)";
    }

    internal static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        int depth = 0;
        foreach (char c in s)
        {
            if (c == '(') depth++;
            else if (c == ')' && depth > 0) depth--;
            if (c == ',' && depth == 0) { parts.Add(cur.ToString().Trim()); cur.Clear(); }
            else cur.Append(c);
        }
        parts.Add(cur.ToString().Trim());
        return parts;
    }

    /// <summary>How a provider is written as a line of the block.</summary>
    public static string Format(VoiceProvider provider, string url, string key, double chance, string voices, string model)
    {
        string endpoint = DefaultEndpoint(provider, url);
        var tail = new List<string>();
        if (model.Trim().Length > 0) tail.Add("model: " + model.Trim());
        tail.Add(voices.Trim().Length > 0 ? voices.Trim() : "auto");
        return $"({endpoint}, {key.Trim()}, {chance.ToString(CultureInfo.InvariantCulture)}, {string.Join(", ", tail)}),";
    }

    /// <summary>The block a new settings file starts with: OpenAI on, every other option ready to switch on.</summary>
    public static List<string> DefaultLines()
    {
        var lines = new List<string>
        {
            "    (https://api.openai.com/v1, , 100, auto),",
            "#   (https://api.elevenlabs.io, your-elevenlabs-key, 50, auto),",
            "#   (https://eastus.tts.speech.microsoft.com, your-azure-key, 50, auto),",
            "#   (https://texttospeech.googleapis.com/v1, your-google-key, 50, auto),",
            "#   (https://api.deepgram.com/v1/speak, your-deepgram-key, 50, auto),",
            "#   (http://localhost:8880/v1, , 50, model: kokoro, auto),",
            "#   (windows, , 50, auto),",
            "#",
            "#   Ready-made voice lists, to use instead of auto. To use one, delete the # at the start of its line",
            "#   (and put a # in front of that provider's auto line above, so it isn't listed twice):",
        };
        var openAi = VoiceCatalog.ReadyMade(VoiceProvider.OpenAI);
        var eleven = VoiceCatalog.ReadyMade(VoiceProvider.ElevenLabs);
        if (openAi.Count > 0)
        {
            lines.Add("#   OpenAI's 13 voices:");
            lines.Add($"#   (https://api.openai.com/v1, , 100, {openAi[0].list}),");
        }
        if (eleven.Count > 1)
        {
            lines.Add("#   ElevenLabs default voices (ElevenLabs retires these on December 31, 2026):");
            lines.Add($"#   (https://api.elevenlabs.io, your-elevenlabs-key, 50, {eleven[0].list}),");
            lines.Add("#   ElevenLabs newer voices (using them through the API needs a paid ElevenLabs plan):");
            lines.Add($"#   (https://api.elevenlabs.io, your-elevenlabs-key, 50, {eleven[1].list}),");
        }
        return lines;
    }
}
