using System;
using System.Collections.Generic;
using System.Linq;
using NivalisAIChat.Npc;
using NivalisAIChat.Voice;

namespace NivalisAIChat.Mind;

/// <summary>
/// Something that happened and is still with them for a while (RimWorld's "thoughts", the Sims' moodlets): what it was,
/// in their own head ("still sore about what the newcomer said"), how it pulls their feelings (valence, arousal), and
/// until when (game minutes). The newest of the same kind replaces the old one.
/// </summary>
public sealed class Moodlet
{
    public string Key { get; set; } = "";
    public string Text { get; set; } = "";
    public float V { get; set; }
    public float A { get; set; }
    public long Since { get; set; }
    public long Until { get; set; }
    /// <summary>The mood word it brings (angry, sad, hopeful...), or "": the point alone can't tell anger from fear.</summary>
    public string Mood { get; set; } = "";
}

/// <summary>How someone feels underneath, carried from one moment to the next: a point (valence, arousal), their own resting point, what's pulling it.</summary>
public sealed class AffectState
{
    public float V { get; set; }
    public float A { get; set; } = Tone.CalmArousal;
    public float BaseV { get; set; }
    public float BaseA { get; set; } = Tone.CalmArousal;
    /// <summary>The game minute it was last brought up to date (-1: never).</summary>
    public long At { get; set; } = -1;
    /// <summary>Since when they've been really low (a rebound comes after a while), or -1.</summary>
    public long LowSince { get; set; } = -1;
    public List<Moodlet> Moodlets { get; set; } = new();
    /// <summary>The mood word they last felt, and when: angry and anxious sit close as points, the word says which it was.</summary>
    public string Lead { get; set; } = "";
    public long LeadAt { get; set; } = -1;
}

/// <summary>
/// The inner-life round (the research's "lighter persistent mood", ALMA-lite, with Sentipolis's two speeds): what someone
/// lives through moves how they feel at once (the fast layer: a line said feeling it, an event), and it settles back toward
/// their own resting point over game hours (the slow layer), held off by whatever is still with them (moodlets). After a
/// long low they bounce back a little (RimWorld's catharsis), so the city never sinks into gloom. Moods stay the 26 words
/// the rest of the mod knows: a point is read back as the nearest one. No Unity, tested.
/// </summary>
public static class Affect
{
    public const int MaxMoodlets = 8;
    /// <summary>Game hours for most of the way back to where they're pulled (e-folding time).</summary>
    public const float SettleHours = 4f;
    /// <summary>How far one line said feeling a mood moves them (a little less than their voice's tone does).</summary>
    public const float FeelStep = 0.3f;
    /// <summary>How far from their resting point they must be before it's carried into the next meeting.</summary>
    public const float Carried = 0.18f;
    private const float LowAt = -0.55f;
    private const int LowMinutes = 12 * 60;

    /// <summary>Their resting point from their temperament: warm people rest a little happier, anxious ones a little more wound up.</summary>
    public static (float v, float a) Baseline(Persona? p)
    {
        if (p == null) return (0f, Tone.CalmArousal);
        string t = (p.Temperament + " " + string.Join(" ", p.Personality ?? new List<string>())).ToLowerInvariant();
        float v = 0f, a = Tone.CalmArousal;
        if (Has(t, "upbeat", "cheer", "warm", "optimis", "sweet", "kind", "mischiev")) v += 0.18f;
        if (Has(t, "laid-back", "unbothered", "gentle", "patient")) { v += 0.08f; a -= 0.08f; }
        if (Has(t, "cynic", "bitter", "dry", "sarcas", "smug")) v -= 0.1f;
        if (Has(t, "melanchol", "griev", "weary", "exhaust", "lonely")) v -= 0.15f;
        if (Has(t, "anxious", "nervous", "fidget", "paranoid", "suspicious")) { v -= 0.05f; a += 0.15f; }
        if (Has(t, "hot-headed", "touchy", "proud", "impatient", "hurry", "competitive")) a += 0.1f;
        if (Has(t, "stoic", "dreamy", "deadpan")) a -= 0.06f;
        return (Math.Clamp(v, -0.3f, 0.3f), Math.Clamp(a, 0.1f, 0.55f));
    }

    private static bool Has(string text, params string[] words) => words.Any(text.Contains);

    /// <summary>A new state at their resting point.</summary>
    public static AffectState Fresh((float v, float a) baseline, long now) =>
        new() { V = baseline.v, A = baseline.a, BaseV = baseline.v, BaseA = baseline.a, At = now };

    /// <summary>
    /// Brought up to <paramref name="now"/>: gone moodlets drop, and they drift toward where they're pulled (their resting
    /// point plus what's still with them). A clock that went back (a new game) starts them fresh at their resting point.
    /// </summary>
    public static void Settle(AffectState s, long now)
    {
        if (now < 0) return;
        if (s.At < 0 || now < s.At)
        {
            s.V = s.BaseV;
            s.A = s.BaseA;
            s.Moodlets.Clear();
            s.LowSince = -1;
            s.Lead = "";
            s.LeadAt = -1;
            s.At = now;
            return;
        }
        s.Moodlets.RemoveAll(m => m.Until <= now);
        double hours = (now - s.At) / 60.0;
        if (hours <= 0) return;
        var (tv, ta) = Pull(s, now);
        float k = (float)(1 - Math.Exp(-hours / SettleHours));
        s.V += (tv - s.V) * k;
        s.A += (ta - s.A) * k;
        Clamp(s);
        // A long low: they bounce back a little (it's out of their system), never into gloom for good.
        if (s.V < LowAt)
        {
            if (s.LowSince < 0) s.LowSince = now;
            else if (now - s.LowSince >= LowMinutes && s.Moodlets.All(m => m.Key != "rebound"))
            {
                s.Moodlets.Add(new Moodlet { Key = "rebound", Text = "starting to feel a bit more like yourself again", V = 0.35f, A = -0.05f, Since = now, Until = now + 18 * 60 });
                s.LowSince = -1;
            }
        }
        else s.LowSince = -1;
        s.At = now;
    }

    /// <summary>Where they're pulled now: their resting point plus each moodlet (held most of its time, fading near its end).</summary>
    public static (float v, float a) Pull(AffectState s, long now)
    {
        float v = s.BaseV, a = s.BaseA;
        foreach (var m in s.Moodlets)
        {
            if (m.Until <= now) continue;
            float span = Math.Max(1, m.Until - m.Since);
            float left = Math.Clamp((m.Until - now) / span, 0f, 1f);
            float w = 0.4f + 0.6f * left;
            v += m.V * w;
            a += m.A * w;
        }
        return (Math.Clamp(v, -0.9f, 0.9f), Math.Clamp(a, 0f, 1f));
    }

    /// <summary>A line said (or a moment lived) feeling <paramref name="mood"/>: they move part of the way there at once.</summary>
    public static void Feel(AffectState s, string? mood, string? intensity, long now, float step = FeelStep)
    {
        Settle(s, now);
        string m = Moods.Normalize(mood);
        if (m.Length == 0) return;
        var (v, a) = Tone.Of(m, intensity);
        step = Math.Clamp(step, 0f, 1f);
        s.V += (v - s.V) * step;
        s.A += (a - s.A) * step;
        s.Lead = m;
        s.LeadAt = now;
        Clamp(s);
    }

    /// <summary>
    /// Something that stays with them: it replaces one of the same kind, pushes them half its way at once (the moment
    /// itself), and pulls them until it's gone. The weakest go when there are too many.
    /// </summary>
    public static void Add(AffectState s, Moodlet m, long now)
    {
        Settle(s, now);
        if (m.Until <= now) return;
        if (m.Since <= 0 || m.Since > now) m.Since = now;
        s.Moodlets.RemoveAll(x => x.Key == m.Key);
        s.Moodlets.Add(m);
        while (s.Moodlets.Count > MaxMoodlets)
            s.Moodlets.Remove(s.Moodlets.OrderBy(x => Math.Abs(x.V) * (x.Until - now)).First());
        s.V += m.V * 0.5f;
        s.A += m.A * 0.5f;
        Clamp(s);
    }

    /// <summary>
    /// How they feel underneath, as a mood word, how strongly, and why (the strongest thing still with them), when it's
    /// far enough from their resting point (or something strong is still with them) to carry into how they are now; null
    /// otherwise (their mood is then rolled as it always was).
    /// </summary>
    public static (string mood, string intensity, string reason)? Label(AffectState s, long now)
    {
        Settle(s, now);
        float dv = s.V - s.BaseV, da = s.A - s.BaseA;
        float off = MathF.Sqrt(dv * dv + da * da);
        var strongest = Strongest(s, now);
        if (off < Carried && (strongest == null || Math.Abs(strongest.V) < 0.3f)) return null;
        string mood = Word(s, strongest, now);
        float strength = MathF.Sqrt(s.V * s.V + MathF.Pow((s.A - Tone.CalmArousal) / 0.7f, 2));
        string intensity = strength > 0.65f ? "strong" : strength > 0.35f ? "moderate" : "mild";
        return (mood, intensity, strongest?.Text ?? "");
    }

    /// <summary>
    /// The mood word for how they feel: what the strongest thing still with them brings (when it's what's pulling them), else
    /// the word they last felt if it still reads the same way (good or bad) and was within the last six hours, else the
    /// nearest word to their point. The point alone puts someone insulted next to someone frightened.
    /// </summary>
    private static string Word(AffectState s, Moodlet? strongest, long now)
    {
        if (strongest is { Mood.Length: > 0 } m && Math.Abs(m.V) >= 0.3f && Math.Sign(m.V) == Math.Sign(s.V - s.BaseV)) return m.Mood;
        if (s.Lead.Length > 0 && s.LeadAt >= 0 && now - s.LeadAt <= 6 * 60)
        {
            var (lv, _) = Tone.Of(s.Lead, "moderate");
            if (Math.Abs(lv) < 0.1f || Math.Sign(lv) == Math.Sign(s.V - s.BaseV)) return s.Lead;
        }
        return Tone.Nearest(s.V, s.A);
    }

    /// <summary>What's pulling them hardest right now (null when nothing is).</summary>
    public static Moodlet? Strongest(AffectState s, long now) =>
        s.Moodlets.Where(m => m.Until > now && m.Key != "rebound").OrderByDescending(m => Math.Abs(m.V) + Math.Abs(m.A) * 0.3f).FirstOrDefault();

    private static void Clamp(AffectState s)
    {
        if (!float.IsFinite(s.V)) s.V = s.BaseV;
        if (!float.IsFinite(s.A)) s.A = s.BaseA;
        s.V = Math.Clamp(s.V, -0.9f, 0.9f);
        s.A = Math.Clamp(s.A, 0f, 1f);
    }
}
