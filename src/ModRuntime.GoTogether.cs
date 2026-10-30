using System;
using System.Collections.Generic;
using System.Linq;
using Nivalis;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using UnityEngine;

namespace NivalisAIChat;

/// <summary>
/// FB-62 (the author: "NPC to NPC as well, following someone who is actually going somewhere, like a food bar, to sit
/// and dine"): when a residents' conversation ends and one of them is on their way to eat (the game's own visit to a
/// food place), a friend from it sometimes goes along: they follow (NavMesh, as a follower of yours does), and once the
/// one they're with has sat down, they take a free chair at the same table and stay while their friend eats. They're
/// let go when their friend gets up (or leaves, or it takes too long). ResidentsGoTogether; near you only (bodies).
/// </summary>
public static partial class ModRuntime
{
    private sealed class Tagalong
    {
        public Character Walker = null!, Leader = null!;
        public string WalkerId = "", LeaderId = "";
        public float Since, SatAt = -1f;
        public Seats.Seat? Seat;
        public Nivalis.GhostSystem.Ai.AgentGhost? Ghost;
    }

    private static readonly List<Tagalong> Tagalongs = new();
    private const int MaxTagalongs = 2;

    /// <summary>EndGroup, before they're let go: maybe one of them goes along with a friend who's off to eat.</summary>
    private static void MaybeGoTogether(IReadOnlyList<(Character c, string id)> members)
    {
        if (!Features.Following || !_cfg.ResidentsGoTogether.Value || Tagalongs.Count >= MaxTagalongs || members.Count < 2 || _resetting) return;
        foreach (var leader in members)
        {
            if (!Alive(leader.c) || GameContext.GhostTask(leader.c) != "AIAction_VisitVenue") continue;
            var (step, _) = GameContext.VisitOf(leader.c);
            if (step != "ComingToVenue") continue; // on their way there, not already sitting
            foreach (var walker in members)
            {
                if (walker.id == leader.id || !Alive(walker.c) || Tagalongs.Any(t => t.WalkerId == walker.id || t.LeaderId == walker.id)) continue;
                if (GameContext.AtWork(walker.c) || GameContext.IsWorkerTask(walker.c) || GameContext.VisitOf(walker.c).step.Length > 0) continue;
                if (MinorInfo(walker.c, walker.id).minor || MinorInfo(leader.c, leader.id).minor) continue;
                var pair = _pairs?.Get(walker.id, leader.id);
                if ((pair?.Affinity ?? 0) < 20 || UnityEngine.Random.value > 0.35f) continue;
                StartTagalong(walker.c, walker.id, leader.c, leader.id);
                return;
            }
        }
    }

    private static void StartTagalong(Character walker, string walkerId, Character leader, string leaderId)
    {
        // Held once the conversation lets them go (the next frame), then walked after their friend.
        MainThread.Enqueue(() =>
        {
            if (!Alive(walker) || !Alive(leader) || NpcHold.IsHeld(walker) || TalkingNow(walker, walkerId)) return;
            if (!NpcHold.EngageMember(walker, _cfg.AnimateNpc.Value)) return;
            if (!NpcHold.Follow(walker, leader.transform, 1.1f, player: false)) { NpcHold.ReleaseMember(walker); return; }
            var t = new Tagalong { Walker = walker, WalkerId = walkerId, Leader = leader, LeaderId = leaderId, Since = Time.unscaledTime };
            try { t.Ghost = walker.Agent?.MyGhost; } catch { }
            Tagalongs.Add(t);
            Log.Info($"Going together: {walkerId} goes along with {leaderId}, who's off to eat.");
        });
    }

    private static void TickTagalongs()
    {
        if (Tagalongs.Count == 0) return;
        float now = Time.unscaledTime;
        foreach (var t in Tagalongs.ToList())
        {
            bool alive = Alive(t.Walker) && Alive(t.Leader);
            if (!alive || _resetting || !_cfg.ResidentsGoTogether.Value || !Features.Following) { EndTagalong(t, "gone"); continue; }
            if (ChatOpen && _npc != null && (Same(_npc, t.Walker) || Same(_npc, t.Leader))) continue; // you're talking to one of them
            var (step, _) = GameContext.VisitOf(t.Leader);
            bool leaderSeated = GameContext.IsSeated(t.Leader) || step is "WaitingToOrder" or "WaitingForOrder" or "Eating";
            if (t.Seat == null && !NpcHold.SeatBusy(t.Walker))
            {
                if (now - t.Since > 180f || step.Length == 0 && !leaderSeated) { EndTagalong(t, step.Length == 0 ? "their friend went elsewhere" : "it took too long"); continue; }
                if (leaderSeated && Flat(t.Walker.transform.position - t.Leader.transform.position).magnitude < 4f)
                {
                    var chair = Seats.FreeNear(t.Leader.transform.position, 2.5f);
                    if (chair == null) { EndTagalong(t, "no free chair at their table"); continue; }
                    Seats.Reserve(chair, t.Ghost);
                    NpcHold.StopFollow(t.Walker);
                    if (!NpcHold.SitAt(t.Walker, chair.Pos, chair.Rot)) { Seats.Free(chair, t.Ghost); EndTagalong(t, "they couldn't sit"); continue; }
                    t.Seat = chair;
                    t.SatAt = now;
                    NpcHold.LookAtFor(t.Walker, GameContext.HeadOf(t.Leader));
                    Log.Info($"Going together: {t.WalkerId} sits down with {t.LeaderId}.");
                }
                continue;
            }
            if (NpcHold.SeatFailed(t.Walker) && !NpcHold.SeatBusy(t.Walker)) { EndTagalong(t, "they couldn't get to the chair"); continue; }
            // Seated together: until their friend's meal is over (they get up and go), or ten minutes.
            bool leaderDone = !leaderSeated && step is "" or "Leaving" or "Complaining";
            if (leaderDone || t.SatAt > 0 && now - t.SatAt > 600f) EndTagalong(t, "their friend's done");
        }
    }

    private static void EndTagalong(Tagalong t, string why)
    {
        Tagalongs.Remove(t);
        try { Seats.Free(t.Seat, t.Ghost); } catch { }
        try { if (Alive(t.Walker)) { NpcHold.StopFollow(t.Walker); NpcHold.ReleaseMember(t.Walker); } } catch { }
        Log.Debug($"going together over ({why}): {t.WalkerId}");
    }

    private static void EndAllTagalongs()
    {
        foreach (var t in Tagalongs.ToList()) EndTagalong(t, "everything stopped");
    }
}
