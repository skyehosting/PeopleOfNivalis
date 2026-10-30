using System;
using System.Linq;

namespace NivalisAIChat.Social;

/// <summary>
/// FB-76 (the author: "only let other npc's know based on who my cone has eyesight on ... an fov cone of like 70% of their
/// width, and short distance like 1 meter this way npc's behide walking can still overhear"): who's in front of you to talk
/// to, and who's only close enough to overhear. On the ground plane (x, z), Unity-free and tested.
/// </summary>
public static class ViewCone
{
    public enum Place { None, InView, Overhears }

    /// <summary>The camera's horizontal view angle (degrees) from its vertical one and the screen's shape.</summary>
    public static float HorizontalFov(float verticalDeg, float aspect)
    {
        double v = Math.Clamp(verticalDeg, 1, 179) * Math.PI / 180;
        return (float)(2 * Math.Atan(Math.Tan(v / 2) * Math.Max(0.1, aspect)) * 180 / Math.PI);
    }

    /// <summary>The cone's half angle: <paramref name="share"/> of the screen's width (0.7 = 70%).</summary>
    public static float HalfAngle(float verticalDeg, float aspect, float share = 0.7f) =>
        Math.Clamp(HorizontalFov(verticalDeg, aspect) * Math.Clamp(share, 0.05f, 1f) / 2f, 2f, 89f);

    /// <summary>The angle (degrees) between where you face and the way to them, on the ground.</summary>
    public static float Off((float x, float z) eye, (float x, float z) facing, (float x, float z) target)
    {
        double dx = target.x - eye.x, dz = target.z - eye.z, fl = Math.Sqrt(facing.x * facing.x + facing.z * facing.z), dl = Math.Sqrt(dx * dx + dz * dz);
        if (fl < 1e-6 || dl < 1e-6) return 0f;
        double cos = Math.Clamp((facing.x * dx + facing.z * dz) / (fl * dl), -1, 1);
        return (float)(Math.Acos(cos) * 180 / Math.PI);
    }

    /// <summary>
    /// In view: inside the cone within <paramref name="range"/> m. Overhears: outside it, but within <paramref name="overhear"/> m
    /// (behind you too: the author's "1 meter"). None: anyone else (out of your view and not that close).
    /// </summary>
    public static Place Classify((float x, float z) eye, (float x, float z) facing, (float x, float z) target, float halfAngle, float range, float overhear = 1f)
    {
        double dx = target.x - eye.x, dz = target.z - eye.z;
        double d = Math.Sqrt(dx * dx + dz * dz);
        if (d <= range && Off(eye, facing, target) <= halfAngle) return Place.InView;
        return d <= overhear ? Place.Overhears : Place.None;
    }

    /// <summary>How many in view can be talked to at once (one request holds this many).</summary>
    public const int MaxTalking = 4;

    /// <summary>
    /// The 2026-10-20 audit: past the first <paramref name="max"/> in view (in the order given, middle of your view first),
    /// the rest only hear it, so you can only pick someone who can answer.
    /// </summary>
    public static Place[] Talkable(Place[] ordered, int max = MaxTalking)
    {
        var res = (Place[])ordered.Clone();
        int n = 0;
        for (int i = 0; i < res.Length; i++)
            if (res[i] == Place.InView && ++n > max) res[i] = Place.Overhears;
        return res;
    }

    /// <summary>Chip widths made to fit one row of <paramref name="row"/> (each squeezed alike, never under <paramref name="least"/>).</summary>
    public static float[] Fit(float[] wants, float row, float gap, float least = 40f)
    {
        var res = (float[])wants.Clone();
        float left = row - gap * (res.Length - 1), cap = float.MaxValue;
        int count = res.Length;
        // Narrow chips keep their width; the room left is shared alike by the wider ones.
        foreach (int i in Enumerable.Range(0, res.Length).OrderBy(k => res[k]))
        {
            if (res[i] <= left / count) { left -= res[i]; count--; continue; }
            cap = Math.Max(least, left / count);
            break;
        }
        for (int i = 0; i < res.Length; i++) res[i] = Math.Min(res[i], cap);
        return res;
    }
}
