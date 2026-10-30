using System;
using System.IO;
using System.Runtime.CompilerServices;
using NivalisAIChat.Ai;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;

namespace NivalisAIChat.Tests;

/// <summary>FB-73: fishing together (ask someone, or fish beside someone at it).</summary>
internal static partial class Program
{
    private static void FishingTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- FB-73: fishing together");
        Features.Reset();
        var tag = ReplyParser.Parse("Sure, I'll grab my rod. [[fishing: yes]]", "Mae");
        Check(tag.Spoken == "Sure, I'll grab my rod." && tag.Extras.Fishing == "yes" && tag.Extras.Any, "the plain reply's [[fishing: yes]]");
        Check(ReplyParser.Parse("Done for today. [[fishing: stop]]", "Mae").Extras.Fishing == "stop" && ReplyParser.Parse("Hm. [[fishing: maybe]]", "Mae").Extras.Fishing == "",
              "...stop, and anything else is nothing");
        var json = ReplyParser.ParseStructured("{\"say\":\"Let's go.\",\"fishing\":\"yes\"}", "Mae");
        Check(json?.Extras.Fishing == "yes", "the JSON reply's fishing field");
        Check(Prompts.ReplySchema().ToJsonString().Contains("\"fishing\""), "asked for in the schema");

        Check(ReplyParser.Parse("Fish? [[fishing]]", "Mae").Extras.Fishing == "", "review FB73-7: a bare [[fishing]] isn't a yes");
        Prompts.FishTogether = false;
        Check(!Prompts.ReplySchema().ToJsonString().Contains("\"fishing\""), "...and with fishing together off it isn't asked for at all");
        Prompts.FishTogether = true;

        // Review FB73-1/5/9: each look, the hold's own state decides what happens to a buddy.
        Check(FishPlan.Decide(false, false, false, 2, true) == FishPlan.Step.Pose && FishPlan.Decide(false, false, false, 2, false) == FishPlan.Step.Nothing
              && FishPlan.Decide(false, true, false, 2, true) == FishPlan.Step.Nothing, "not fishing yet: posed when they may and nothing's under way");
        Check(FishPlan.Decide(true, true, false, 3, true) == FishPlan.Step.Nothing, "posed (or walking there): left as they are");
        Check(FishPlan.Decide(true, false, true, 3, true) == FishPlan.Step.BackToFollowing, "couldn't get there: back to following you");
        Check(FishPlan.Decide(true, false, false, 3, true) == FishPlan.Step.BackToFollowing, "the pose lost (a chat mid-walk, a drop): the flag never outlives it");
        Check(FishPlan.Decide(true, true, false, 14, true) == FishPlan.Step.StandUp, "you wandered off with your rod: they get up and come");

        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string follow = File.ReadAllText(Path.Combine(src, "ModRuntime.Follow.cs"));
        Check(follow.Contains("If they ask you to go fishing with them (down at the water), you'd go") && follow.Contains("WouldFollow(c, rec)")
              && follow.Contains("if (f.Fishing) continue; // FB-73"), "they're told whether they'd go (gated as following is); a fishing follower isn't walked on");
        string rt = File.ReadAllText(Path.Combine(src, "ModRuntime.Fishing.cs"));
        Check(rt.Contains("NpcHold.PoseAt(f.C, spot.Value, t.rotation, idle!)") && rt.Contains("NavMesh.SamplePosition(want") && rt.Contains("held.GetComponent<FishingEquipment>() == null") && rt.Contains("Physics.CheckSphere(pc.transform.position, 12f, _waterMask"),
              "a rod in your hands near water: buddies take the game's Fishing pose beside you");
        Check(rt.Contains("if (FishBuddies.Contains(id) || MinorInfo(c, id).minor) continue;") && rt.Contains("if (seconds < 60f || KnownUnder18(id)) continue;") && rt.Contains("MinorInfo(c, cid).minor"),
              "never anyone under 18: not company, not a line, not a memory");
        Check(rt.Contains("FishLineEvery = 180f") && rt.Contains("SpeakUp(who, id, situation") && rt.Contains("!item.isFish"),
              "a word now and then (at most every three minutes, a fish caught or a long quiet), as a greeting line (counted as greet)");
        Check(rt.Contains("if (ChatOpen) return;") && rt.Contains("_fishBuddiesGen != _resetGeneration") && follow.Contains("FishBuddies.Remove(f.Id); f.Fishing = false;"),
              "a chat pauses it (nobody gets up); another save or the end of following forgets who's coming");
        string hold = File.ReadAllText(Path.Combine(src, "Game", "NpcHold.cs"));
        Check(hold.Contains("public static bool PoseAt(Character c, Vector3 spot, Quaternion facing, IdleAction idle)") && hold.Contains("h.PoseIdle != null ? (h.PoseIdle.WasCollected ? null : h.PoseIdle) : SitIdle()") && hold.Contains("_noFishUntil = Time.unscaledTime + MissingFor"),
              "the seat's way, with the game's Fishing pose instead of a chair's");
    }
}
