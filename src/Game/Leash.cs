using System;

namespace NivalisAIChat.Game;

/// <summary>
/// How someone you're talking to stays put without looking frozen (no Unity here, so it can be tested). Their idle
/// animation may sway them a few centimetres freely; past that they're eased back, never snapped. Their body only turns
/// to face you when you're well off to one side, smoothly, fast at first and gentle at the end; the head follows the rest.
/// </summary>
public static class Leash
{
    /// <summary>Metres they may sway from their spot before being eased back.</summary>
    public const float Band = 0.06f;
    /// <summary>How quickly a drift past the band fades (per second): about 95% of it gone in half a second.</summary>
    public const float PullPerSecond = 6f;
    /// <summary>The body starts turning when you're more than this many degrees off, and stops when within <see cref="TurnStop"/>.</summary>
    public const float TurnStart = 25f, TurnStop = 4f;

    /// <summary>Their distance from the spot after this frame: unchanged inside the band, beyond it the excess eases back.</summary>
    public static float Pull(float distance, float dt, float band = Band, float rate = PullPerSecond)
    {
        if (distance <= band) return distance;
        return band + (distance - band) * MathF.Exp(-rate * Math.Max(0f, dt));
    }

    /// <summary>Whether the body should be turning toward you, given how far off you are (hysteresis: start wide, finish close).</summary>
    public static bool KeepTurning(bool turning, float degreesOff) => turning ? degreesOff > TurnStop : degreesOff > TurnStart;

    /// <summary>Turn speed in degrees per second: quick when far off, slowing as they line up.</summary>
    public static float TurnSpeed(float degreesOff) => Math.Clamp(degreesOff * 3f, 20f, 150f);
}
