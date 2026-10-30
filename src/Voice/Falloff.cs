using System;

namespace NivalisAIChat.Voice;

/// <summary>
/// How loud a voice is with distance (no Unity here, so it can be tested): full volume close by, then fading the way a
/// voice carries (a few decibels quieter each time the distance doubles), tailing off to silence exactly at the edge of
/// your hearing range. Walking toward someone mid-line fades them in; walking away fades them out.
/// </summary>
public static class Falloff
{
    /// <summary>Each line fades in over this long (no hard start), and a line cut off fades out over <see cref="FadeOutSeconds"/>.</summary>
    public const float FadeInSeconds = 0.15f, FadeOutSeconds = 0.2f;

    /// <summary>A low-pass this high changes nothing you can hear (the filter is off).</summary>
    public const float OpenCutoff = 22000f;

    /// <summary>
    /// Volume for someone <paramref name="angle"/> degrees off where you're facing: full in front, a touch less to the side,
    /// <paramref name="behind"/> (0..1) straight behind you, smoothly in between.
    /// </summary>
    public static float BehindGain(float angle, float behind)
    {
        behind = Math.Clamp(behind, 0f, 1f);
        float facing = (1f + MathF.Cos(Math.Clamp(angle, 0f, 180f) * MathF.PI / 180f)) / 2f; // 1 in front, 0 behind
        return behind + (1f - behind) * MathF.Pow(facing, 0.7f);
    }

    /// <summary>
    /// How muffled a voice sounds (a low-pass cutoff in hertz): clear in front and near; behind you your head shades the
    /// highs (down to about 4.5 kHz straight behind); far off the air takes some too (about 8 kHz at the edge of hearing).
    /// </summary>
    public static float MuffleCutoff(float angle, float distance, float edge)
    {
        float t = Math.Clamp((angle - 80f) / 100f, 0f, 1f);
        float behindness = t * t * (3f - 2f * t);
        float byAngle = MathF.Exp(Lerp(MathF.Log(OpenCutoff), MathF.Log(4500f), behindness));
        float far = Math.Clamp((distance / Math.Max(1f, edge) - 0.25f) / 0.75f, 0f, 1f); // nearby: the air takes nothing
        float byAir = MathF.Exp(Lerp(MathF.Log(OpenCutoff), MathF.Log(8000f), MathF.Pow(far, 1.5f)));
        return Math.Min(byAngle, byAir);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Volume (0..1) at <paramref name="distance"/> metres: 1 within <paramref name="full"/>, 0 from <paramref name="edge"/> on.</summary>
    public static float Volume(float distance, float full, float edge)
    {
        if (edge <= 0f) return 0f;
        full = Math.Clamp(full, 0.1f, edge);
        if (distance <= full) return 1f;
        if (distance >= edge) return 0f;
        float t = (distance - full) / (edge - full);
        float carry = MathF.Pow(full / distance, 0.8f); // about 5 dB quieter each time the distance doubles
        float tail = 1f - t * t * t;                     // and fading to nothing at the edge
        return carry * tail;
    }

    /// <summary>Rate of change of <see cref="Volume"/> per metre (just past <paramref name="distance"/>; at the edge, just before it).</summary>
    public static float Slope(float distance, float full, float edge)
    {
        if (edge <= 0f) return 0f;
        full = Math.Clamp(full, 0.1f, edge);
        if (distance < full || distance > edge || edge - full < 1e-4f) return 0f;
        float t = (distance - full) / (edge - full);
        float carry = MathF.Pow(full / distance, 0.8f);
        float tail = 1f - t * t * t;
        return -0.8f * carry / distance * tail + carry * (-3f * t * t / (edge - full));
    }

    /// <summary>
    /// Points for a custom roll-off curve over the distance as a fraction of <paramref name="edge"/> (what Unity's audio
    /// expects): x, volume, and the slope on each side (flat inside the full-volume distance, then the fall).
    /// </summary>
    public static (float X, float V, float SlopeIn, float SlopeOut)[] Curve(float full, float edge, int steps = 10)
    {
        edge = Math.Max(0.5f, edge);
        full = Math.Clamp(full, 0.1f, edge * 0.9f);
        steps = Math.Clamp(steps, 2, 40);
        var pts = new (float, float, float, float)[steps + 2];
        pts[0] = (0f, 1f, 0f, 0f);
        for (int i = 0; i <= steps; i++)
        {
            // closer together near the speaker, where the volume changes fastest
            float d = full * MathF.Pow(edge / full, i / (float)steps);
            if (i == steps) d = edge;
            float slope = Slope(d, full, edge) * edge;
            float slopeIn = i == 0 ? 0f : slope;
            float slopeOut = i == steps ? 0f : slope; // silent from the edge on
            pts[i + 1] = (i == steps ? 1f : d / edge, Volume(d, full, edge), slopeIn, slopeOut);
        }
        return pts;
    }
}
