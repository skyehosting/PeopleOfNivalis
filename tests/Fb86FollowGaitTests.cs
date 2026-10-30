using System;
using System.IO;
using System.Runtime.CompilerServices;
using NivalisAIChat.Game;
using static NivalisAIChat.Game.FollowGait;

namespace NivalisAIChat.Tests;

/// <summary>FB-86: following with three fixed gaits (slow walk, your walk, your sprint), no flicker, no "aimbot" turning.</summary>
internal static partial class Program
{
    private static void Fb86FollowGaitTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- FB-86: following walks, runs and strolls like a person");
        Check(Next(Gait.Stand, 0.6f, false) == Gait.Stand && Next(Gait.Stand, 1.2f, false) == Gait.Stroll && Next(Gait.Stand, 4f, false) == Gait.Walk,
              "FB-86: standing, they wait until you're a step away; then a slow walk, or a walk if you're further");
        Check(Next(Gait.Stand, 3f, true) == Gait.Run && Next(Gait.Walk, 3f, true) == Gait.Run && Next(Gait.Walk, 11f, false) == Gait.Run,
              "...they run when you sprint off, or when they've fallen well behind");
        // A walk holds until well inside the slow-walk range, and the slow walk until past the walk's (no flicker in between).
        bool steady = true;
        var g = Gait.Walk;
        foreach (float gap in new[] { 2.9f, 2.4f, 2.1f, 2.6f, 2.95f }) { g = Next(g, gap, false); steady &= g == Gait.Walk; }
        g = Next(g, 1.9f, false);
        bool strolled = g == Gait.Stroll;
        foreach (float gap in new[] { 2.2f, 2.8f, 2.99f }) { g = Next(g, gap, false); steady &= g == Gait.Stroll; }
        Check(steady && strolled && Next(Gait.Stroll, 3.1f, false) == Gait.Walk,
              "...between a walk and a slow walk there's room either side: no flicking back and forth");
        Check(Next(Gait.Run, 5f, true) == Gait.Run && Next(Gait.Run, 5f, false) == Gait.Walk && Next(Gait.Run, 7f, false) == Gait.Run
              && Next(Gait.Run, 1.2f, true) == Gait.Stroll && Next(Gait.Stroll, 0.1f, false) == Gait.Stand,
              "...you stop sprinting: they walk once they're close, run on while far; caught up, they slow; arrived, they stand");
        var (stroll, walk, sprint) = Speeds(1.3f, 2.6f, 5.2f);
        Check(walk == 2.6f && sprint == 5.2f && stroll == 1.3f && SpeedOf(Gait.Walk, stroll, walk, sprint) == 2.6f && SpeedOf(Gait.Run, stroll, walk, sprint) == 5.2f
              && SpeedOf(Gait.Stand, stroll, walk, sprint) == 0f,
              "...their walk is your walking speed, their run your sprint, and the slow walk their own easy pace");
        var (s2, w2, r2) = Speeds(1.3f, -1f, -1f);
        var (s3, w3, r3) = Speeds(1.3f, 1.2f, 1.0f);
        Check(w2 == 2.0f && r2 > w2 && s2 < w2 && r3 > w3 && s3 <= w3 * 0.85f + 0.0001f,
              "...your speeds unreadable or odd: a sensible pair, a run always quicker than the walk, the slow walk never quicker");
        float sp = 0f;
        for (int i = 0; i < 10; i++) sp = Ease(sp, 2.6f, 0.1f);
        Check(Math.Abs(Ease(0f, 2.6f, 0.1f) - 0.3f) < 0.001f && Math.Abs(sp - 2.6f) < 0.001f && Ease(2.6f, 0f, 0.1f) < 2.6f,
              "...the speed eases into a gait (about a second to a walk), never jumps");
        Check(!KeepFacing(false, 0.5f, 90f) && KeepFacing(false, 1.2f, 40f) && !KeepFacing(false, 3f, 20f) && KeepFacing(true, 3f, 10f) && !KeepFacing(true, 3f, 6f),
              "...standing, they turn to you only once you've been still a moment and you're well off to one side (not every step of yours)");
        Check(TurnRate(Gait.Walk, 5f) == 30f && TurnRate(Gait.Walk, 180f) == 190f && TurnRate(Gait.Run, 180f) == 260f && TurnRate(Gait.Stroll, 180f) == 140f,
              "...turning while they walk is gentle near lined up and never a snap (at most 140-260 degrees a second)");

        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string hold = File.ReadAllText(Path.Combine(src, "Game", "NpcHold.cs"));
        Check(hold.Contains("h.FollowGaitNow = FollowGait.Next(h.FollowGaitNow, gap, sprinting, catchUp: h.FollowPlayer);") && hold.Contains("(youWalk, youSprint, sprinting) = GameContext.PlayerSpeeds();")
              && hold.Contains("var slot = tp + back * stop + Vector3.Cross(Vector3.up, back) * h.FollowSide;") && !hold.Contains("speed * 1.2f, WalkSpeed * 1.3f"),
              "...the follower uses them: your own speeds, a spot just behind you (to one side for a second or third), no more copying your pace");
        string gc = File.ReadAllText(Path.Combine(src, "Game", "GameContext.cs"));
        Check(gc.Contains("float walk = ctl.defaultMoveSpeed, sprint = ctl.sprintSpeed") && gc.Contains("sprinting = ctl._isSprinting;") && gc.Contains("Following (probe): your walk"),
              "...read from your own controller (walk, sprint, whether you're sprinting), and logged once");
    }
}
