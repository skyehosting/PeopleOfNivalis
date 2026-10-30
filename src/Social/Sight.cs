using System;

namespace NivalisAIChat.Social;

/// <summary>
/// FB-29: what a resident can see, as the author drew it: two lines from the middle of their head, each SightDistance
/// long, pushed apart until the angle between them is SightAngle, the way they face; the triangle the two lines and the
/// edge between their ends make is what they see. Our own layer: the game's own perception isn't touched. On the ground
/// plane (x, z); height doesn't matter. Unity-free, so it's tested.
/// </summary>
public static class Sight
{
    /// <summary>The widest a triangle can open (at 180° it would have no depth left).</summary>
    public const float MaxAngle = 170f;

    /// <summary>
    /// Whether <paramref name="target"/> is inside the sight triangle of someone at <paramref name="eye"/> facing
    /// <paramref name="facing"/>: within half of <paramref name="angleDeg"/> to either side of where they face, and on their
    /// side of the far edge (straight ahead that edge is distance x cos(half the angle) away). Facing of zero length, a
    /// distance or angle of zero: sees nothing.
    /// </summary>
    public static bool Sees((float x, float z) eye, (float x, float z) facing, (float x, float z) target, float distance, float angleDeg)
    {
        if (!(distance > 0f) || !(angleDeg > 0f)) return false;
        float fl = MathF.Sqrt(facing.x * facing.x + facing.z * facing.z);
        if (fl < 1e-5f) return false;
        float fx = facing.x / fl, fz = facing.z / fl;
        float dx = target.x - eye.x, dz = target.z - eye.z;
        float d = MathF.Sqrt(dx * dx + dz * dz);
        if (d < 1e-4f) return true; // right on top of them
        float half = Math.Min(angleDeg, MaxAngle) * 0.5f * MathF.PI / 180f;
        float along = dx * fx + dz * fz;
        if (along / d < MathF.Cos(half) - 1e-6f) return false; // outside the two lines
        return along <= distance * MathF.Cos(half) + 1e-4f;      // and not past the edge between their ends
    }

    /// <summary>The ends of the two lines (left, right): SightDistance out from the eye, half the angle to each side.</summary>
    public static ((float x, float z) left, (float x, float z) right) Corners((float x, float z) eye, (float x, float z) facing, float distance, float angleDeg)
    {
        float fl = MathF.Sqrt(facing.x * facing.x + facing.z * facing.z);
        if (fl < 1e-5f || !(distance > 0f)) return (eye, eye);
        float fx = facing.x / fl, fz = facing.z / fl;
        float half = Math.Clamp(angleDeg, 0f, MaxAngle) * 0.5f * MathF.PI / 180f;
        (float x, float z) Turn(float a) => (eye.x + distance * (fx * MathF.Cos(a) - fz * MathF.Sin(a)), eye.z + distance * (fx * MathF.Sin(a) + fz * MathF.Cos(a)));
        return (Turn(half), Turn(-half));
    }
}
