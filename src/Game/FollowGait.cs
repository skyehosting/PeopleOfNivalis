using System;

namespace NivalisAIChat.Game;

/// <summary>
/// FB-86 (the author: "the npc kind of feels like they are aimbotting you when they do start to follow, I think the
/// dynamic speed may be making it feel wierd too. maybe instead allow two speeds, walking and running which matches the
/// users walk and sprint speed. And may also a third, slow walk for when they're already close"): how someone following
/// you moves, decided here (no Unity, so it's tested). Three fixed gaits instead of copying your pace: a slow walk (their
/// own easy pace) for the last steps, a walk at your walking speed, a run at your sprint. Each switch has room either
/// side (hysteresis), so they don't flicker between two; the speed eases into the new gait. Standing, they only turn to
/// you once you've been still a moment and are well off to one side.
/// </summary>
public static class FollowGait
{
    public enum Gait { Stand, Stroll, Walk, Run }

    /// <summary>Metres past their stopping distance before a standing follower sets off (a step, not every sway of yours).</summary>
    public const float StartGap = 1.0f;
    /// <summary>Within this of the stopping point they've arrived.</summary>
    public const float StopGap = 0.15f;
    /// <summary>The slow walk below this gap, a walk beyond <see cref="WalkFrom"/> (between: whichever they're in).</summary>
    public const float StrollUnder = 2.0f, WalkFrom = 3.0f;
    /// <summary>Running: you're sprinting and they're this far behind, or they're this far behind at all (catching up).</summary>
    public const float RunWithYouFrom = 2.5f, CatchUpFrom = 10f;
    /// <summary>Back to a walk: you've stopped sprinting and they're within this, or they've caught up to within <see cref="RunUntil"/>.</summary>
    public const float StopRunUnder = 6f, RunUntil = 1.5f;
    /// <summary>How fast their speed changes, metres per second each second (a body speeding up, not a switch).</summary>
    public const float Accel = 3.0f, Decel = 4.0f;

    /// <summary>
    /// The gait for this frame, from the one they're in, the gap (metres beyond their stopping distance) and whether you're
    /// sprinting.
    /// </summary>
    /// <param name="catchUp">Run to catch up when far behind (following you); off for a resident walking over to another (review F86-2).</param>
    public static Gait Next(Gait now, float gap, bool youSprint, bool catchUp = true)
    {
        if (float.IsNaN(gap)) return Gait.Stand;
        bool run = (catchUp && gap > CatchUpFrom) || (youSprint && gap > RunWithYouFrom);
        if (now == Gait.Stand) return gap > StartGap ? (run ? Gait.Run : gap > WalkFrom ? Gait.Walk : Gait.Stroll) : Gait.Stand;
        if (gap <= StopGap) return Gait.Stand;
        if (run) return Gait.Run;
        if (now == Gait.Run) return gap > RunUntil && (youSprint || (catchUp && gap > StopRunUnder)) ? Gait.Run : gap > WalkFrom ? Gait.Walk : Gait.Stroll;
        if (now == Gait.Walk) return gap < StrollUnder ? Gait.Stroll : Gait.Walk;
        return gap > WalkFrom ? Gait.Walk : Gait.Stroll;
    }

    /// <summary>The speed (m/s) each gait is walked at: their own easy pace, your walk, your sprint.</summary>
    public static float SpeedOf(Gait g, float stroll, float walk, float sprint) => g switch
    {
        Gait.Stroll => stroll,
        Gait.Walk => walk,
        Gait.Run => sprint,
        _ => 0f,
    };

    /// <summary>The speed after this frame, eased toward <paramref name="target"/> (never past the stopping point: the caller caps the step).</summary>
    public static float Ease(float speed, float target, float dt) =>
        speed < target ? Math.Min(target, speed + Accel * dt) : Math.Max(target, speed - Decel * dt);

    /// <summary>
    /// The player's walk and sprint speeds as the follower uses them: the game's own values when they make sense, otherwise
    /// a sensible pair; a sprint is always quicker than the walk, and the slow walk never quicker than the walk.
    /// </summary>
    public static (float stroll, float walk, float sprint) Speeds(float npcWalk, float youWalk, float youSprint)
    {
        float walk = youWalk is > 0.8f and < 6f ? youWalk : 2.0f;
        float sprint = youSprint > walk + 0.3f && youSprint < 14f ? youSprint : Math.Max(walk * 1.8f, 4.0f);
        float stroll = Math.Clamp(npcWalk is > 0.5f and < 3f ? npcWalk : 1.3f, 0.6f, walk * 0.85f);
        return (stroll, walk, sprint);
    }

    /// <summary>Whether a standing follower turns to face you: only after you've been still <see cref="FaceAfter"/> seconds, and you're well off to one side.</summary>
    public const float FaceAfter = 1.0f, FaceStart = 35f, FaceStop = 8f;

    public static bool KeepFacing(bool turning, float stillFor, float degreesOff) =>
        turning ? degreesOff > FaceStop : stillFor >= FaceAfter && degreesOff > FaceStart;

    /// <summary>Turn speed (degrees per second) while walking: gentle when nearly lined up, quicker at a run; never a snap.</summary>
    public static float TurnRate(Gait g, float degreesOff)
    {
        float most = g switch { Gait.Run => 260f, Gait.Walk => 190f, _ => 140f };
        return Math.Clamp(degreesOff * 4f, 30f, most);
    }
}
