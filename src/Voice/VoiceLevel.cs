using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NivalisAIChat.Voice;

/// <summary>
/// How loud each person talks. Everyone gets their own level inside a range (PersonVolume; MumbleVolume when they're
/// thinking out loud), fixed by who they are: the same person is always as loud, or as quiet, as last time, with nothing
/// to store. Unity-free (tested).
/// </summary>
public static class VoiceLevel
{
    /// <summary>A loudness range as fractions of the Volume setting, 0..1.</summary>
    public readonly record struct Range(float Min, float Max);

    public static readonly Range PersonDefault = new(0.7f, 1f);
    public static readonly Range MumbleDefault = new(0.5f, 0.7f);

    // [0-9], not \d: \d also matches other scripts' digits (full-width "８０" from an IME), which float.Parse can't read.
    private static readonly Regex Pattern = new(
        @"^\s*([0-9]+(?:\.[0-9]+)?)\s*%?\s*(?:(?:-|–|to)\s*([0-9]+(?:\.[0-9]+)?)\s*%?)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"70-100" (percent; either order; "80" = exactly 80) as fractions, each held to 0..100%. Anything unreadable = <paramref name="fallback"/>.</summary>
    public static Range Parse(string? text, Range fallback)
    {
        var m = Pattern.Match(text ?? "");
        if (!m.Success || Percent(m.Groups[1].Value) is not { } a) return fallback;
        float b = a;
        if (m.Groups[2].Success)
        {
            if (Percent(m.Groups[2].Value) is not { } second) return fallback;
            b = second;
        }
        return a <= b ? new Range(a, b) : new Range(b, a);
    }

    private static float? Percent(string digits) =>
        float.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && !float.IsNaN(v)
            ? Math.Clamp(v, 0f, 100f) / 100f
            : null;

    /// <summary>
    /// Where this person sits in any range: 0..1, the same for the same id on every run (FNV-1a; string.GetHashCode
    /// changes every launch). No id = the middle.
    /// </summary>
    public static float Fraction(string? id)
    {
        if (string.IsNullOrEmpty(id)) return 0.5f;
        uint h = 2166136261;
        foreach (char ch in "loudness|" + id)
        {
            h ^= ch;
            h *= 16777619;
        }
        return (h % 10007) / 10006f;
    }

    /// <summary>This person's level in <paramref name="range"/>.</summary>
    public static float For(string? id, Range range) => range.Min + (range.Max - range.Min) * Fraction(id);
}
