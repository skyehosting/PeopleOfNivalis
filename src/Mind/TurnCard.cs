using System;
using System.Collections.Generic;
using System.Linq;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Mind;

/// <summary>What the code knows for one reply: who, how they feel, where the conversation is, how close they are, what they opened with lately.</summary>
public sealed class TurnInputs
{
    public string Name = "";
    public Persona Persona = new();
    public string Mood = "", Intensity = "";
    /// <summary>Which exchange of this meeting it is (1 = the first answer).</summary>
    public int Exchange;
    /// <summary>Their last two replies both asked something (FB-14): this one doesn't.</summary>
    public bool NoQuestion;
    /// <summary>Patience is running out: they're wrapping up.</summary>
    public bool WrapUp;
    public double Bar = 50;
    public int TimesMet;
    public IReadOnlyList<string> RecentOpeners = Array.Empty<string>();
}

/// <summary>
/// The inner-life round, step 3 (the research's turn card and voice reminder): one short note right before each reply,
/// filled in by the code: about how many words (their talkativeness, their mood, whether they're wrapping up), a statement
/// when the last two asked something, how much of themselves they share (social penetration: the surface with strangers,
/// one small personal thing with someone they know and like, more with a friend), their slang allowance this line, a fresh
/// start when they keep opening the same way, and who they sound like, last (persona drift sets in after a few rounds; a
/// reminder near the end counters it). Everything is said as what to do, never as a list of don'ts. One field is left out
/// now and then, so no two turns read alike. About 30 tokens; no Unity, tested.
/// </summary>
public static class TurnCard
{
    /// <summary>The word range a reply aims at: by talkativeness, shorter when hostile, hurried or wrapping up, a little longer when they're enjoying it.</summary>
    public static (int lo, int hi) Words(Persona p, string mood, bool wrapUp)
    {
        (int lo, int hi) r = (p.Speech?.Verbosity ?? "").Trim().ToLowerInvariant() switch
        {
            "terse" => (4, 10),
            "chatty" => (12, 26),
            "rambling" => (16, 34),
            _ => (8, 18),
        };
        double k = Moods.Normalize(mood) switch
        {
            "angry" or "irritated" or "in a hurry" or "stressed" or "grumpy" or "suspicious" => 0.7,
            "tired" or "bored" or "hungover" or "sad" or "melancholy" => 0.8,
            "cheerful" or "playful" or "excited" or "tipsy" or "lonely" or "amused" or "flirty" => 1.15,
            _ => 1.0,
        };
        if (wrapUp) k *= 0.7;
        return (Math.Max(2, (int)Math.Round(r.lo * k)), Math.Max(4, (int)Math.Round(r.hi * k)));
    }

    /// <summary>How much of themselves they share with this person (Altman and Taylor's ladder, as the author's bar shows it).</summary>
    public static string Depth(double bar, int timesMet)
    {
        if (bar >= 80 && timesMet >= 3) return "You can share something you'd only tell a friend, if it comes up.";
        if (bar >= 60 && timesMet >= 3) return "You can share one small personal thing, if it comes up.";
        if (timesMet <= 1) return "Keep it to the here and now: ordinary things, nothing personal yet.";
        return "Mostly everyday things; a little about yourself only if they ask.";
    }

    /// <summary>The note for this reply: "[This reply: about 9 words. End on a statement. ... Sound like Mira: dry, short sentences.]"</summary>
    public static string For(TurnInputs t, Random rng)
    {
        var parts = new List<string>();
        var (lo, hi) = Words(t.Persona, t.Mood, t.WrapUp);
        int target = lo + rng.Next(Math.Max(1, hi - lo + 1));
        parts.Add($"About {target} words.");
        if (t.NoQuestion) parts.Add("End on a statement.");
        if (t.WrapUp) parts.Add("You're winding the conversation down.");
        var optional = new List<string>();
        optional.Add(Depth(t.Bar, t.TimesMet));
        int slang = t.Persona.Speech?.SlangLevel ?? 1;
        optional.Add(slang <= 0 ? "Plain, proper words." : slang == 1 ? (t.Exchange % 3 == 0 ? "One of your own words, if it fits." : "Mostly plain words.")
            : slang == 2 ? (t.Exchange % 2 == 0 ? "A bit of your street talk." : "Your own words, lightly.") : "Your street words come easily.");
        // The same filler opening again and again: start with the point.
        var recent = t.RecentOpeners.TakeLast(5).ToList();
        if (recent.Count >= 2 && recent.GroupBy(o => o).Any(g => g.Count() >= 2)) optional.Add("Start straight in with what you mean.");
        // Now and then one of these is left out, so no two turns read the same.
        if (optional.Count > 1 && rng.NextDouble() < 0.35) optional.RemoveAt(rng.Next(optional.Count));
        parts.AddRange(optional);
        if (Voice(t) is { Length: > 0 } voice) parts.Add(voice);
        return "[This reply: " + string.Join(" ", parts) + "]";
    }

    /// <summary>"Sound like Mira: dry, short sentences." — the reminder that keeps them themselves, from their temperament and rhythm.</summary>
    public static string Voice(TurnInputs t)
    {
        string first = Ai.Prompts.FirstName(t.Name.Length > 0 ? t.Name : t.Persona.Name);
        if (first.Length == 0) return "";
        var bits = new List<string>();
        if (t.Persona.Temperament.Length > 0) bits.Add(Short(t.Persona.Temperament, 40));
        string rhythm = (t.Persona.Speech?.Rhythm ?? "").Trim();
        if (rhythm.Length > 0) bits.Add(Short(rhythm, 50));
        else if ((t.Persona.Speech?.Verbosity ?? "").Length > 0) bits.Add(t.Persona.Speech!.Verbosity);
        return bits.Count == 0 ? $"Sound like {first}." : $"Sound like {first}: {string.Join(", ", bits)}.";
    }

    /// <summary>Review (core IL-6): cleaned as everything else the round puts in a prompt (no brackets, braces, tags or quotes; one line).</summary>
    private static string Short(string s, int max) => WitnessBook.Cut(s, max).TrimEnd('.');
}
