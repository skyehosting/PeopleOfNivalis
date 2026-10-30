using System;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Social;

/// <summary>
/// Who reacts when the player walks past, and how. No Unity here, so it can be tested: how they feel about the player
/// picks the kind of reaction, their mood scales the chance, and the config's base chance sets the overall rate.
/// </summary>
public static class GreetingRules
{
    public enum Kind { WaveOver, Wave, Nod, Snub }

    private static readonly string[] Warm = { "lonely", "cheerful", "excited", "playful", "flirty", "hopeful", "amused", "tipsy" };
    private static readonly string[] Flat = { "tired", "sad", "melancholy", "distracted", "in a hurry", "stressed", "hungover", "anxious", "paranoid" };
    private static readonly string[] Sour = { "angry", "irritated", "grumpy" };

    /// <summary>
    /// The reaction they'd have right now (null = they don't react at all) and the chance it actually happens.
    /// <paramref name="basePercent"/> is the config's GreetChance; <paramref name="m"/> the points on the relationship
    /// bar where they snub you, wave, or call you over (settings; the author's defaults when null).
    /// </summary>
    public static (Kind? kind, double chance) Decide(NpcState st, string mood, double basePercent, int today, Milestones? m = null)
    {
        m ??= new Milestones();
        // FB-45: relationships off: everyone stands at neutral (no waves or snubs by standing); moods off: no mood.
        double rel = Features.Standing(st.Relationship);
        mood = Features.Mood(mood);
        double b = Math.Clamp(basePercent, 0, 100) / 100.0;
        if (st.TimesMet < 1 || b <= 0) return (null, 0);
        double moodFactor = Array.IndexOf(Warm, mood) >= 0 ? 1.5 : Array.IndexOf(Flat, mood) >= 0 ? 0.5 : 1.0;
        bool sour = Array.IndexOf(Sour, mood) >= 0;
        bool fedUpLately = st.LastLeftReason.Contains("enough") && today - st.LastLeftGameDay <= 3;

        if (rel <= m.SnubAt || fedUpLately)
            return (Kind.Snub, Math.Min(0.9, b * (sour ? 1.3 : 0.8)));
        if (rel >= m.WaveAt && !sour)
        {
            double chance = Math.Min(0.9, b * (1 + Features.Feeling(st.Affinity) / 50.0) * moodFactor);
            return (rel >= m.WaveOverAt && moodFactor >= 1.0 ? Kind.WaveOver : Kind.Wave, chance);
        }
        if (st.TimesMet >= 2 && !sour)
            return (Kind.Nod, b * 0.4 * moodFactor);
        return (null, 0);
    }

    /// <summary>The scene they're reacting to, in the NPC's second person (they see "the stranger" or the player's name).</summary>
    public static string Describe(Kind kind, string who) => kind switch
    {
        Kind.WaveOver => $"You spot {who} walking past — someone you like. You wave and call them over; you'd like a word. A short, warm line, a few words.",
        Kind.Wave => $"You spot {who} walking past — someone you know and like. You wave and call out a quick hello. A few words.",
        Kind.Nod => $"You notice {who} walking past — you've met before. A quick nod and a word or two, nothing more.",
        _ => $"You notice {who} walking past — someone you'd rather avoid. A curt, cold remark as you turn away. A few words.",
    };

    /// <summary>When they come over (or the player walks up) right after: how the conversation begins.</summary>
    public static string Approach(Kind kind, string who, string said) => kind switch
    {
        Kind.WaveOver => $"A moment ago you spotted {who} and waved them over, calling out \"{said}\". They've come over to you.",
        Kind.Wave => $"A moment ago you waved at {who} and called out \"{said}\". They've walked over to you.",
        Kind.Nod => $"A moment ago you nodded at {who} in passing (\"{said}\"). Now they've stopped to talk to you.",
        _ => $"A moment ago you saw {who} and turned away with \"{said}\". They've walked up to you anyway.",
    };

    public static string Fallback(Kind kind, int slang, Random rng)
    {
        string[] pool = kind switch
        {
            Kind.WaveOver => slang >= 2 ? new[] { "Oi! Over here!", "Hey! C'mere a sec!", "Yo, got a minute?" } : new[] { "Hey! Got a minute?", "Over here!", "Oh, hey — come here a sec!" },
            Kind.Wave => slang >= 2 ? new[] { "Yo!", "Heyyy!", "Look who it is!" } : new[] { "Oh, hi!", "Hey there!", "Hello again!" },
            Kind.Nod => new[] { "Hey.", "'Sup.", "Evening.", "Mm." },
            _ => slang >= 2 ? new[] { "Ugh. You.", "Not you again.", "Keep walking." } : new[] { "Oh. It's you.", "Hm.", "Not now." },
        };
        return pool[rng.Next(pool.Length)];
    }

    /// <summary>What the bark is shown as above their head while it plays.</summary>
    public static string GestureWord(Kind kind) => kind switch
    {
        Kind.WaveOver => "waves you over",
        Kind.Wave => "waves",
        Kind.Nod => "nods",
        _ => "turns away",
    };
}
