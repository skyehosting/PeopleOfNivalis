using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NivalisAIChat.Voice;

/// <summary>
/// FB-41: what a spoken line costs, in its provider's own unit (research/fb41-research-2026-10-14.md, section 3). ElevenLabs
/// says exactly, in its <c>character-cost</c> header, and that's used when it's there; otherwise the line is counted the way
/// each provider bills: ElevenLabs credits (1 a character; half on its fast models), characters (Azure with its SSML markup
/// and Han characters twice, Google, Deepgram, OpenAI's tts-1), or tokens (OpenAI's gpt- voices and Google's Gemini voices:
/// the text, plus about 21 or 25 audio tokens a second). Free voices (Windows, a server on your own PC) cost nothing. No Unity.
/// </summary>
public static class VoiceCost
{
    /// <summary>OpenAI's gpt- voice models, estimated from its "$0.015 a minute" (research 3.3: to be checked against a bill).</summary>
    public const double OpenAiAudioTokensPerSecond = 21;
    /// <summary>Google's Gemini voices: 25 audio tokens a second (Google's pricing page).</summary>
    public const double GeminiAudioTokensPerSecond = 25;

    /// <summary>The provider bills this model by the token (its text in, its audio out).</summary>
    public static bool TokenBilled(VoiceProvider p, string model) =>
        (p == VoiceProvider.OpenAI && model.Trim().StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
        || (p == VoiceProvider.Google && model.IndexOf("gemini", StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>The unit a provider's limit is counted in: "credits" (ElevenLabs), "tokens" or "characters".</summary>
    public static string Unit(VoiceProvider p, string model) =>
        p == VoiceProvider.ElevenLabs ? "credits" : TokenBilled(p, model) ? "tokens" : "characters";

    /// <summary>ElevenLabs' credits a character: half on its fast models (Flash, Turbo, v4 Turbo, v3 Conversational), one otherwise.</summary>
    public static double ElevenLabsRate(string model)
    {
        string m = (model ?? "").ToLowerInvariant();
        return m.Contains("flash") || m.Contains("turbo") || m.Contains("conversational") ? 0.5 : 1.0;
    }

    /// <summary>ElevenLabs' own count of what a line cost (the <c>character-cost</c> header), when it sent one.</summary>
    public static double? FromHeader(string? value) =>
        double.TryParse((value ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) && v >= 0 ? v : null;

    /// <summary>
    /// A line's cost, worked out: <paramref name="text"/> is what was sent to be spoken (an ElevenLabs audio tag included),
    /// <paramref name="extra"/> the acting directions or style prompt sent with it (token-billed models), <paramref name="ssml"/>
    /// Azure's whole request, <paramref name="seconds"/> the audio that came back.
    /// </summary>
    public static double Estimate(VoiceProvider p, string model, string text, string extra = "", string ssml = "", double seconds = 0)
    {
        text ??= "";
        switch (p)
        {
            case VoiceProvider.ElevenLabs:
                return text.Length * ElevenLabsRate(model);
            case VoiceProvider.Azure:
                return AzureCharacters(ssml.Length > 0 ? ssml : text);
            case VoiceProvider.OpenAI when TokenBilled(p, model):
                return Math.Ceiling((text.Length + (extra ?? "").Length) / 4.0) + Math.Ceiling(Math.Max(0, seconds) * OpenAiAudioTokensPerSecond);
            case VoiceProvider.Google when TokenBilled(p, model):
                return Math.Ceiling((text.Length + (extra ?? "").Length) / 4.0) + Math.Ceiling(Math.Max(0, seconds) * GeminiAudioTokensPerSecond);
            case VoiceProvider.Windows:
            case VoiceProvider.None:
                return 0;
            default:
                return text.Length; // characters, spaces included (Google, Deepgram, OpenAI's tts-1, other servers)
        }
    }

    private static readonly Regex SpeakOrVoiceTag = new(@"</?(?:speak|voice)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Azure's billed characters: everything in the request but its &lt;speak&gt; and &lt;voice&gt; tags (the speaking style and
    /// rate markup counts), each Chinese (Han) character twice, kanji and hanja included.
    /// </summary>
    public static int AzureCharacters(string ssml)
    {
        string body = SpeakOrVoiceTag.Replace(ssml ?? "", "");
        int n = 0;
        for (int i = 0; i < body.Length; i++)
        {
            int cp = char.IsHighSurrogate(body[i]) && i + 1 < body.Length && char.IsLowSurrogate(body[i + 1]) ? char.ConvertToUtf32(body[i], body[++i]) : body[i];
            n += Han(cp) ? 2 : 1;
        }
        return n;
    }

    private static bool Han(int cp) =>
        cp is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0x20000 and <= 0x2EBEF or >= 0xF900 and <= 0xFAFF or >= 0x30000 and <= 0x3134F;
}
