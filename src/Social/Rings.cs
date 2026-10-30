using System;

namespace NivalisAIChat.Social;

/// <summary>
/// FB-38: the three rings around you, as metres from you. Hearing (VoiceDistance): residents' voices fade to silence at it;
/// 0 = no voices. Voices (VoiceDistance + VoiceBeyond): no audio is made past it; between it and hearing a line plays
/// silently and fades in as you come closer. Chatter (ChatterRange): people within it start talking; talk wraps up a few
/// metres past it (<see cref="ChatterSettings.WrapUpDistance"/>). Every value moves in 0.25 m steps and the rings stay in
/// order (hearing &lt;= voices &lt;= chatter), all within 60 m; the master (the settings menu's first slider, /rings scale)
/// sets the outer ring and scales the others with it. No Unity here.
/// </summary>
/// <remarks>With hearing 0 (no voices), <see cref="Voices"/> holds how far past hearing the voices ring would be (VoiceBeyond),
/// so turning voices back on finds it as it was.</remarks>
public readonly record struct Rings(float Hearing, float Voices, float Chatter)
{
    /// <summary>How far one step moves a ring.</summary>
    public const float Step = 0.25f;
    /// <summary>The farthest any ring goes (the chatter scan's own limit), the most the voices ring is past hearing, the nearest chatter ring.</summary>
    public const float MaxRing = 60f, MaxBeyond = 30f, MinChatter = 5f;

    /// <summary>From the settings as they're stored (VoiceBeyond is how far the voices ring is past hearing). Not yet in order.</summary>
    public static Rings From(float voiceDistance, float voiceBeyond, float chatterRange) =>
        new(Safe(voiceDistance), Safe(voiceDistance) + Safe(voiceBeyond), Safe(chatterRange));

    /// <summary>Back to the stored settings: VoiceDistance, VoiceBeyond, ChatterRange.</summary>
    public (float voiceDistance, float voiceBeyond, float chatterRange) ToSettings() => (Hearing, Snap(Voices - Hearing), Chatter);

    /// <summary>To the nearest 0.25 m (nothing negative or unreadable).</summary>
    public static float Snap(float metres) => (float)(Math.Round(Safe(metres) / Step, MidpointRounding.AwayFromZero) * Step);

    private static float Safe(float m) => float.IsFinite(m) && m > 0f ? m : 0f;

    /// <summary>Each value on a 0.25 m step, in order, within the limits (whatever state it was in: a hand edit, /chatter voice).</summary>
    public Rings Clamped()
    {
        float h = Math.Clamp(Snap(Hearing), 0f, MaxRing);
        if (h == 0f) return new Rings(0f, Math.Clamp(Snap(Voices), 0f, MaxBeyond), Math.Clamp(Snap(Chatter), MinChatter, MaxRing));
        float v = Math.Clamp(Snap(Voices), h, Math.Min(h + MaxBeyond, MaxRing));
        float c = Math.Clamp(Snap(Chatter), Math.Max(MinChatter, v), MaxRing);
        return new Rings(h, v, c);
    }

    /// <summary>The hearing ring moved; the voices ring follows if it would end up inside it (or too far past it), then chatter.</summary>
    public Rings WithHearing(float metres)
    {
        var r = Clamped();
        float h = Math.Clamp(Snap(metres), 0f, MaxRing);
        float beyond = r.Hearing == 0f ? r.Voices : r.Voices - r.Hearing;
        if (h == 0f) return new Rings(0f, beyond, r.Chatter).Clamped(); // no voices: the voices ring's margin is kept for later
        float v = r.Hearing == 0f ? h + beyond : Math.Max(r.Voices, h);
        return new Rings(h, v, Math.Max(r.Chatter, v)).Clamped();
    }

    /// <summary>The voices ring moved: hearing comes in if it was outside it, chatter goes out if it was inside it.</summary>
    public Rings WithVoices(float metres)
    {
        var r = Clamped();
        float v = Math.Clamp(Snap(metres), 0f, MaxRing);
        if (r.Hearing == 0f) return new Rings(0f, Math.Min(v, MaxBeyond), r.Chatter).Clamped(); // voices off: only the margin
        float h = Math.Max(Math.Min(r.Hearing, v), v - MaxBeyond);
        return new Rings(h, v, Math.Max(r.Chatter, v)).Clamped();
    }

    /// <summary>The chatter ring moved: the inner rings come in if they were outside it.</summary>
    public Rings WithChatter(float metres)
    {
        var r = Clamped();
        float c = Math.Clamp(Snap(metres), MinChatter, MaxRing);
        if (r.Hearing == 0f) return new Rings(0f, r.Voices, c).Clamped();
        float v = Math.Min(r.Voices, c);
        return new Rings(Math.Min(r.Hearing, v), v, c).Clamped();
    }

    /// <summary>
    /// The master: the outer ring set to <paramref name="outer"/>, the other two scaled by the same factor from these rings
    /// (the anchor: a slider drag or a run of steps scales from where it began, so steps don't drift).
    /// </summary>
    public Rings ScaledTo(float outer)
    {
        var r = Clamped();
        float c = Math.Clamp(Snap(outer), MinChatter, MaxRing);
        float f = c / Math.Max(r.Chatter, 0.01f);
        if (r.Hearing == 0f) return new Rings(0f, r.Voices, c).Clamped();
        float h = Math.Clamp(Snap(r.Hearing * f), 0f, c);
        if (h == 0f) h = Step; // scaled down, voices stay on
        float v = Math.Clamp(Snap(r.Voices * f), h, Math.Min(c, h + MaxBeyond));
        return new Rings(h, v, c).Clamped();
    }

    public override string ToString() => Hearing == 0f
        ? $"hearing off (no voices) · chatter {M(Chatter)}"
        : $"hearing {M(Hearing)} · voices {M(Voices)} · chatter {M(Chatter)}";

    /// <summary>"18.25 m", "15 m".</summary>
    public static string M(float metres) => metres.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " m";
}
