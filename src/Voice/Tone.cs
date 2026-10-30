using System;
using System.Collections.Generic;

namespace NivalisAIChat.Voice;

/// <summary>
/// How someone sounds right now, as it moves through a conversation: a point between unhappy and happy (valence) and
/// between calm and worked up (arousal). Each line they say nudges it toward how they feel at that moment, so the tone
/// builds and fades over a few lines like a person's, instead of jumping with every reply (cheerful, then furious,
/// then cheerful again).
/// </summary>
public sealed class ToneState
{
    public float Valence;
    public float Arousal = Tone.CalmArousal;
    /// <summary>The mood word they felt most recently (it colours the tone when it agrees with it).</summary>
    public string Lead = "";
    public int Lines;

    public ToneState Copy() => (ToneState)MemberwiseClone();
}

/// <summary>Turning moods into a tone of voice, and that tone into each voice provider's own terms (no Unity here; tested).</summary>
public static class Tone
{
    /// <summary>How far one line moves their tone toward how they feel right then (about three lines to settle).</summary>
    public const float Step = 0.35f;
    /// <summary>The arousal of someone simply calm (neither flat nor worked up).</summary>
    public const float CalmArousal = 0.3f;

    // Where each mood sits: valence (-1 unhappy .. +1 happy), arousal (0 calm .. 1 worked up).
    private static readonly Dictionary<string, (float v, float a)> Where = new(StringComparer.OrdinalIgnoreCase)
    {
        ["content"] = (0.3f, 0.25f), ["cheerful"] = (0.7f, 0.55f), ["playful"] = (0.6f, 0.6f), ["bored"] = (-0.15f, 0.1f),
        ["tired"] = (-0.2f, 0.05f), ["irritated"] = (-0.5f, 0.6f), ["angry"] = (-0.8f, 0.9f), ["anxious"] = (-0.45f, 0.7f),
        ["stressed"] = (-0.45f, 0.75f), ["sad"] = (-0.65f, 0.2f), ["melancholy"] = (-0.45f, 0.15f), ["suspicious"] = (-0.3f, 0.45f),
        ["distracted"] = (-0.05f, 0.3f), ["curious"] = (0.35f, 0.55f), ["excited"] = (0.75f, 0.9f), ["flirty"] = (0.6f, 0.5f),
        ["tipsy"] = (0.45f, 0.6f), ["hungover"] = (-0.35f, 0.1f), ["nostalgic"] = (0.1f, 0.2f), ["paranoid"] = (-0.55f, 0.75f),
        ["grumpy"] = (-0.45f, 0.4f), ["hopeful"] = (0.5f, 0.45f), ["lonely"] = (-0.3f, 0.2f), ["in a hurry"] = (-0.05f, 0.8f),
        ["amused"] = (0.6f, 0.5f), ["nervous"] = (-0.35f, 0.7f),
    };

    private static readonly HashSet<string> Uneasy = new(StringComparer.OrdinalIgnoreCase) { "anxious", "nervous", "paranoid", "stressed" };

    /// <summary>Where a mood sits, scaled by how strongly it's felt (mild moods sit nearer calm).</summary>
    public static (float v, float a) Of(string? mood, string? intensity)
    {
        if (string.IsNullOrWhiteSpace(mood) || !Where.TryGetValue(mood.Trim(), out var p)) return (0f, CalmArousal);
        float k = (intensity ?? "").Trim().ToLowerInvariant() switch { "mild" => 0.55f, "strong" => 1f, _ => 0.8f };
        return (p.v * k, CalmArousal + (p.a - CalmArousal) * k);
    }

    /// <summary>
    /// The inner-life round: the mood word that sits nearest a valence/arousal point (how someone feels underneath, carried
    /// from one moment to the next, put back into words). "content" for a point that can't be read.
    /// </summary>
    public static string Nearest(float valence, float arousal)
    {
        if (!float.IsFinite(valence) || !float.IsFinite(arousal)) return "content";
        string best = "content";
        float bestD = float.MaxValue;
        foreach (var (mood, p) in Where)
        {
            float dv = p.v - valence, da = p.a - arousal, d = dv * dv + da * da;
            if (d < bestD) { bestD = d; best = mood; }
        }
        return best;
    }

    /// <summary>The tone they start a conversation in: their mood as it is.</summary>
    public static ToneState Start(string? mood, string? intensity)
    {
        var (v, a) = Of(mood, intensity);
        return new ToneState { Valence = v, Arousal = a, Lead = (mood ?? "").Trim().ToLowerInvariant() };
    }

    /// <summary>One more line said feeling <paramref name="mood"/>: the tone moves part of the way there.</summary>
    public static void Feel(ToneState s, string? mood, string? intensity, float step = Step)
    {
        if (string.IsNullOrWhiteSpace(mood)) return;
        var (v, a) = Of(mood, intensity);
        step = Math.Clamp(step, 0f, 1f);
        s.Valence += (v - s.Valence) * step;
        s.Arousal += (a - s.Arousal) * step;
        s.Lead = mood.Trim().ToLowerInvariant();
        s.Lines++;
    }

    /// <summary>How far from plain calm they sound, 0..1.</summary>
    public static float Strength(ToneState s)
    {
        float dv = s.Valence, da = (s.Arousal - CalmArousal) / 0.7f;
        return Math.Clamp(MathF.Sqrt(dv * dv + da * da), 0f, 1f);
    }

    private enum Kind { Plain, Excited, Cheerful, Lively, Warm, Hurried, Flat, Uneasy, Irritable, Subdued, Cool, Anxious, Angry, Cold, Sad }

    private static Kind KindOf(ToneState s)
    {
        float v = s.Valence, a = s.Arousal;
        bool uneasy = Uneasy.Contains(s.Lead);
        if (v >= 0.45f) return a >= 0.65f ? Kind.Excited : Kind.Cheerful;
        if (v >= 0.15f) return a >= 0.6f ? Kind.Lively : Kind.Warm;
        if (v > -0.15f) return a >= 0.65f ? Kind.Hurried : a <= 0.18f ? Kind.Flat : Kind.Plain;
        if (v > -0.45f)
        {
            if (uneasy && a >= 0.55f) return Kind.Uneasy;
            if (a >= 0.5f) return Kind.Irritable;
            return a < 0.3f ? Kind.Subdued : Kind.Cool;
        }
        if (uneasy && a >= 0.6f) return Kind.Anxious;
        if (a >= 0.65f) return Kind.Angry;
        return a >= 0.4f ? Kind.Cold : Kind.Sad;
    }

    /// <summary>
    /// The tone in words, for providers that take directions: "warm and cheerful", "slightly short and irritable",
    /// "very angry and sharp (angry)". Empty when they simply sound calm.
    /// </summary>
    public static string Phrase(ToneState s)
    {
        var kind = KindOf(s);
        string words = kind switch
        {
            Kind.Excited => "excited and upbeat",
            Kind.Cheerful => "warm and cheerful",
            Kind.Lively => "lively and interested",
            Kind.Warm => "friendly and relaxed",
            Kind.Hurried => "brisk and hurried",
            Kind.Flat => "flat and tired",
            Kind.Uneasy => "uneasy, a little nervous",
            Kind.Irritable => "short and irritable",
            Kind.Subdued => "low and subdued",
            Kind.Cool => "guarded and a bit cool",
            Kind.Anxious => "anxious and on edge",
            Kind.Angry => "angry and sharp",
            Kind.Cold => "cold and irritated",
            Kind.Sad => "sad and heavy",
            _ => "",
        };
        if (words.Length == 0) return "";
        float k = Strength(s);
        string degree = k < 0.25f ? "slightly " : k > 0.65f ? "very " : "";
        // their latest mood word, when it agrees with where the tone has got to
        string lead = s.Lead.Length > 0 && Where.TryGetValue(s.Lead, out var p) && Math.Sign(p.v) == Math.Sign(s.Valence) && Math.Abs(s.Valence) >= 0.15f ? $" ({s.Lead})" : "";
        return degree + words + lead;
    }

    /// <summary>An Azure speaking style for the tone ("" = their plain voice), and how strongly (0.5 to 1.6).</summary>
    public static (string style, float degree) Azure(ToneState s)
    {
        string style = KindOf(s) switch
        {
            Kind.Excited => "excited",
            Kind.Cheerful => "cheerful",
            Kind.Lively or Kind.Warm => "friendly",
            Kind.Hurried => "serious",
            Kind.Uneasy or Kind.Anxious => "fearful",
            Kind.Irritable => "disgruntled",
            Kind.Cool or Kind.Cold => "unfriendly",
            Kind.Angry => "angry",
            Kind.Subdued or Kind.Sad => "sad",
            _ => "",
        };
        return (style, MathF.Round(0.5f + Strength(s) * 1.1f, 2));
    }

    /// <summary>An ElevenLabs audio tag for the tone (eleven_v3 / eleven_v4 only), or "" when it's just calm or barely there.</summary>
    public static string ElevenTag(ToneState s)
    {
        if (Strength(s) < 0.25f) return "";
        return KindOf(s) switch
        {
            Kind.Excited => "[excitedly]",
            Kind.Cheerful => "[cheerfully]",
            Kind.Lively => "[curious]",
            Kind.Warm => "[warmly]",
            Kind.Hurried => "[hurriedly]",
            Kind.Flat => "[tiredly]",
            Kind.Uneasy or Kind.Anxious => "[nervously]",
            Kind.Irritable or Kind.Cold => "[annoyed]",
            Kind.Cool => "[flatly]",
            Kind.Angry => "[angrily]",
            Kind.Subdued or Kind.Sad => "[sadly]",
            _ => "",
        };
    }

    /// <summary>Models that act out ElevenLabs audio tags; older ones would read the brackets aloud.</summary>
    public static bool ElevenTakesTags(string model) =>
        model.Contains("v3", StringComparison.OrdinalIgnoreCase) || model.Contains("v4", StringComparison.OrdinalIgnoreCase);
}
