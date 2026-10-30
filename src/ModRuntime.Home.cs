using System;
using System.Collections.Generic;
using System.Linq;
using Nivalis;
using Nivalis.Apartment;
using Nivalis.GhostSystem;
using Nivalis.GhostSystem.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using UnityEngine;

namespace NivalisAIChat;

/// <summary>
/// FB-63 (the author: "moving in, and when entering the apartment, there's a configurable 70% chance the partner is
/// there, walking around and using furniture"): someone who lives with you can be home when you come in. Once a game
/// day it's decided whether they're in (PartnerHomeChance, never while they're at work); if they are, their ghost is
/// brought to your apartment (the game's own GhostManager.TransferGhostToScene, as following does), the body the game
/// gives it is held, and they potter about: walking between spots near the apartment's own character spot, now and
/// then sitting on a free chair. You can talk to them as to anyone. They're let go when you leave.
/// </summary>
public static partial class ModRuntime
{
    private sealed class HomeVisit
    {
        public string Id = "", Name = "";
        public AgentGhost Ghost = null!;
        public IntPtr GhostPtr;
        public Character? C;
        public ApartmentController Ctrl = null!;
        public Vector3 Spot;
        public Quaternion Facing;
        public float Since, NextMove;
        public Seats.Seat? Seat;
        public bool Paused;
        /// <summary>FB-85: came in with you to stay the night (not someone who lives here).</summary>
        public bool Guest;
        /// <summary>FB-85: when their body went (-1: here), and when their ghost is next brought here again.</summary>
        public float LostAt = -1f, NextFetch;
    }

    private static bool _saidFetch;

    private static HomeVisit? _home;
    private static (ApartmentController ctrl, bool enter, float at)? _apartmentEvent;
    private static bool _homeProbed;
    /// <summary>The phone: 0 outside an apartment, 1 in someone else's, 2 in yours.</summary>
    private static int _inApartment;

    /// <summary>Game/HomeHooks.cs: the player went in or out of an apartment (handled a moment later, once it has settled).</summary>
    internal static void OnApartment(ApartmentController ctrl, bool enter)
    {
        if (!Running) return;
        _apartmentEvent = (ctrl, enter, Time.unscaledTime);
    }

    private static void TickHome()
    {
        float now = Time.unscaledTime;
        if (_apartmentEvent is { } ev && now - ev.at >= 0.8f)
        {
            _apartmentEvent = null;
            // The phone: at home the signal is full (your own wifi); in someone else's place it's indoors.
            bool own = false;
            if (ev.enter) { try { own = ev.ctrl._isPlayerInsideOwnApartment; } catch { } if (!own) try { own = GameContext.Existing<ApartmentManager>()?.OwnedEntered == true; } catch { } }
            _inApartment = ev.enter ? (own ? 2 : 1) : 0;
            if (ev.enter) ArriveHome(ev.ctrl);
            else LeaveHome("you went out", fresh: true);
        }
        if (_home != null) TickHomeVisit(_home, now);
    }

    private static void ArriveHome(ApartmentController ctrl)
    {
        if (!Features.Romance || _couples == null || _home != null || _resetting) return;
        bool own = false;
        try { own = ctrl._isPlayerInsideOwnApartment; } catch { }
        if (!own) try { own = GameContext.Existing<ApartmentManager>()?.OwnedEntered == true; } catch { }
        Vector3? spot = null;
        Quaternion facing = Quaternion.identity;
        try { if (ctrl.CharacterSpot != null) { spot = ctrl.CharacterSpot.transform.position; facing = ctrl.CharacterSpot.transform.rotation; } } catch { }
        if (!_homeProbed)
        {
            _homeProbed = true;
            Log.Info($"Home (probe): an apartment ({(own ? "yours" : "not yours")}), its character spot {(spot.HasValue ? spot.Value.ToString() : "none")}.");
        }
        if (!own) return;
        // FB-85: someone coming in with you, your guest for the night or the partner you live with: home has them now
        // (no roll, no transfer: they're already here, or on their way in through the door).
        var withYou = Followers.FirstOrDefault(f => f.Id == _stay?.Id) ?? Followers.FirstOrDefault(f => LivesWithYou(f.Id));
        if (withYou != null && withYou.Ghost != null) { ComesHomeWithYou(withYou, ctrl, spot, facing); return; }
        int day = GameContext.Day();
        var living = _couples.Partners().Where(p => p.Status == Romance.Living && Followers.All(f => f.Id != p.Id)).ToList();
        if (living.Count == 0 || day < 0) return;
        var partner = living.OrderBy(p => Romance.Roll(p.Id, day, "who-home")).First();
        int chance = Math.Clamp(_cfg.PartnerHomeChance.Value, 0, 100);
        if (Romance.Roll(partner.Id, day, "home") * 100 >= chance) { Log.Info($"Home: {partner.Name} isn't in right now."); return; }
        var ghost = GhostOfPerson(partner.Id);
        if (ghost == null) { Log.Info($"Home: {partner.Name} couldn't be found in the city right now."); return; }
        string task = "";
        try { task = ghost.CurrentAction?.Type?.name ?? ""; } catch { }
        if (task.Contains("Workplace", StringComparison.OrdinalIgnoreCase))
        {
            Log.Info($"Home: {partner.Name} is at work ({task}).");
            return;
        }
        var pc = GameContext.PlayerCharacter();
        if (pc == null) return;
        Vector3 at = spot ?? pc.transform.position + pc.transform.forward * 2f;
        int scene = -1;
        try { scene = pc.MyGhost != null ? pc.MyGhost.SceneIndex : -1; } catch { }
        try
        {
            if (scene >= 0) GhostManager.TransferGhostToScene(ghost, scene, new PositionRotation(at, facing));
        }
        catch (Exception e) { Log.Info($"Home: {partner.Name} couldn't be brought home ({e.GetType().Name})."); return; }
        _home = new HomeVisit
        {
            Id = partner.Id, Name = partner.Name, Ghost = ghost, GhostPtr = ghost.Pointer, Ctrl = ctrl, Spot = at, Facing = facing, Since = Time.unscaledTime,
        };
        Log.Info($"Home: {partner.Name} is home (taken to scene {scene}).");
    }

    private static bool LivesWithYou(string id) => _couples?.Partners().Any(p => p.Id == id && p.Status == Romance.Living) == true;

    /// <summary>FB-85: a follower who came in with you is at home now: still held, they potter about the place (TickHomeVisit).</summary>
    private static void ComesHomeWithYou(Follower f, ApartmentController ctrl, Vector3? spot, Quaternion facing)
    {
        Followers.Remove(f);
        if (f.Seated) { FreeDateSeat(f); f.Seated = false; }
        FishBuddies.Remove(f.Id);
        bool here = Alive(f.C) && SameGhost(f) && NpcHold.IsHeld(f.C);
        if (here) NpcHold.StopFollow(f.C);
        float now = Time.unscaledTime;
        var pc = GameContext.PlayerCharacter();
        Vector3 at = spot ?? (here ? f.C.transform.position : pc != null ? pc.transform.position : Vector3.zero);
        _home = new HomeVisit
        {
            Id = f.Id, Name = f.Name, Ghost = f.Ghost!, GhostPtr = f.GhostPtr, C = here ? f.C : null, Ctrl = ctrl, Spot = at, Facing = facing,
            Since = now, NextMove = now + 8f, Guest = _stay?.Id == f.Id, LostAt = here ? -1f : now,
        };
        Log.Info($"Home: {f.Name} comes in with you{(_home.Guest ? " for the night" : "")}.");
    }

    /// <summary>
    /// FB-85: their ghost is somewhere else (the game took them: the door's scene change, the curfew, a night's sleep): it's
    /// brought to your apartment again, at its spot, and the body it gets there is held (TickHomeVisit finds it).
    /// </summary>
    private static void FetchHome(HomeVisit h)
    {
        try
        {
            var pc = GameContext.PlayerCharacter();
            int scene = -1, theirs = -1;
            try { scene = pc?.MyGhost != null ? pc.MyGhost.SceneIndex : -1; } catch { }
            try { theirs = h.Ghost.SceneIndex; } catch { }
            if (scene < 0 || theirs == scene) return;
            GhostManager.TransferGhostToScene(h.Ghost, scene, new PositionRotation(h.Spot, h.Facing));
            if (!_saidFetch) { _saidFetch = true; Log.Info($"Home (probe): {h.Name}'s ghost brought back from scene {theirs} to {scene}."); }
        }
        catch (Exception e) { Log.Debug("home fetch: " + e.Message); }
    }

    /// <summary>A resident's ghost by the mod's id for them (person_&lt;guid&gt;); null if they aren't in the city.</summary>
    private static AgentGhost? GhostOfPerson(string id)
    {
        try
        {
            var list = GhostManager._instance?._agentGhosts;
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                var g = list[i];
                Person? person = null;
                try { person = g?.Person; } catch { }
                if (person != null && GameContext.PersonId(person) == id) return g;
            }
        }
        catch (Exception e) { Log.Debug("ghost of person: " + e.Message); }
        return null;
    }

    private static void TickHomeVisit(HomeVisit h, float now)
    {
        if (!Features.Romance || _resetting) { LeaveHome("romance is off or the city is being reset"); return; }
        var pc = GameContext.PlayerCharacter();
        if (pc == null) return;
        Vector3 pp;
        try { pp = pc.transform.position; } catch { return; }
        if (Flat(pp - h.Spot).magnitude > 40f) { LeaveHome("you went out", fresh: true); return; }
        if (ChatOpen && h.C != null && _npc != null && Same(_npc, h.C)) return; // the chat has them
        if (h.C == null || !Alive(h.C))
        {
            h.C = null;
            if (h.LostAt < 0f) h.LostAt = now;
            // FB-85: brought back when the game has them elsewhere (review F85-4: only while you're in, the door settled).
            if (now >= h.NextFetch && _inApartment == 2 && _apartmentEvent == null) { h.NextFetch = now + 3f; FetchHome(h); }
            foreach (var c in Character.instances)
            {
                if (c == null) continue;
                IntPtr gp = IntPtr.Zero;
                try { gp = c.Agent?.MyGhost?.Pointer ?? IntPtr.Zero; } catch { }
                if (gp != h.GhostPtr) continue;
                if (!NpcHold.IsHeld(c) && !NpcHold.EngageMember(c, _cfg.AnimateNpc.Value)) break;
                h.C = c;
                NpcHold.PlaceAt(c, h.Spot, h.Facing);
                h.NextMove = now + 6f;
                h.LostAt = -1f;
                Log.Info($"Home (probe): {h.Name}'s body is here.");
                break;
            }
            // FB-85: a guest for the night is waited for longer (a door, a sleep), and the night ends if they can't be brought back.
            if (h.C == null && now - h.LostAt > (h.Guest ? 45f : 10f))
            {
                Log.Info($"Home: {h.Name} didn't come in after all (no body for them).");
                _home = null;
                if (h.Guest) EndStayOver("the game had them somewhere else");
            }
            return;
        }
        if (h.Paused)
        {
            h.Paused = false;
            if (!NpcHold.IsHeld(h.C) && !NpcHold.EngageMember(h.C, _cfg.AnimateNpc.Value)) { _home = null; return; }
            h.NextMove = now + 10f;
        }
        if (NpcHold.SeatFailed(h.C) && h.Seat != null && !NpcHold.SeatBusy(h.C)) { Seats.Free(h.Seat, h.Ghost); h.Seat = null; }
        if (now < h.NextMove || NpcHold.IsWalking(h.C)) return;
        h.NextMove = now + UnityEngine.Random.Range(18f, 40f);
        // Up from a chair first; otherwise a walk to another spot, or now and then a sit.
        if (h.Seat != null)
        {
            NpcHold.StandUp(h.C);
            Seats.Free(h.Seat, h.Ghost);
            h.Seat = null;
            h.NextMove = now + 4f;
            return;
        }
        if (NpcHold.SeatBusy(h.C)) return;
        if (UnityEngine.Random.value < 0.4f && Seats.FreeNear(h.Spot, 8f) is { } chair)
        {
            Seats.Reserve(chair, h.Ghost);
            if (NpcHold.SitAt(h.C, chair.Pos, chair.Rot)) { h.Seat = chair; return; }
            Seats.Free(chair, h.Ghost);
        }
        if (SpotNear(h.Spot, h.C.transform.position, 8f) is { } to) NpcHold.WalkTo(h.C, to, 0.4f, GameContext.TypicalWalkParam());
    }

    /// <summary>One of the game's own spots (where people stand, sit, use things) within <paramref name="radius"/> of the home spot.</summary>
    private static Vector3? SpotNear(Vector3 home, Vector3 now, float radius)
    {
        try
        {
            var picks = new List<Vector3>();
            foreach (var s in UnityEngine.Object.FindObjectsOfType<AISpot>())
            {
                if (s == null) continue;
                var p = s.transform.position;
                if (Flat(p - home).magnitude > radius || Mathf.Abs(p.y - home.y) > 1.5f || Flat(p - now).magnitude < 1.5f) continue;
                picks.Add(p);
                if (picks.Count >= 40) break;
            }
            if (picks.Count > 0) return picks[UnityEngine.Random.Range(0, picks.Count)];
        }
        catch (Exception e) { Log.Debug("home spots: " + e.Message); }
        return null;
    }

    /// <param name="fresh">FB-85: they're let go into a fresh task (the game's pick for the hour), not back to what they were doing before.</param>
    private static void LeaveHome(string why, bool fresh = false)
    {
        var h = _home;
        if (h == null) return;
        _home = null;
        try { Seats.Free(h.Seat, h.Ghost); } catch { }
        // FB-85: your guest for the night comes along when you go out (it's still their night with you).
        if (h.Guest && _stay?.Id == h.Id && why == "you went out" && Features.Following && Followers.Count < MaxFollowers)
        {
            if (h.C != null && Alive(h.C)) StartFollow(h.C, h.Id, h.Name);
            else
            {
                // Review F85-2: their body stayed behind with the apartment: a follower by their ghost, whose body the follow
                // finds where you are (TryFindBody takes the ghost to your scene).
                Followers.Add(new Follower { C = h.C!, Ghost = h.Ghost, GhostPtr = h.GhostPtr, Id = h.Id, Name = h.Name, Since = Time.unscaledTime, LostSince = Time.unscaledTime });
            }
            if (Followers.Any(f => f.Id == h.Id)) { Log.Info($"Home: {h.Name} comes out with you."); return; }
        }
        if (h.C != null && Alive(h.C) && Followers.All(f => !Same(f.C, h.C!))) try { NpcHold.ReleaseMember(h.C, fresh); } catch { }
        Log.Info($"Home: {h.Name} carries on with their day ({why}).");
    }

    /// <summary>OpenChat: talking to your partner at home (seated: they stay seated through it).</summary>
    private static void PauseHomeFor(Character c)
    {
        if (_home?.C is not { } hc || !Same(hc, c)) return;
        _home.Paused = true;
        if (NpcHold.IsSeatedInSeat(c)) NpcHold.KeepSeatForChat(c);
    }

    /// <summary>[WHAT YOU'D DO]: at home together.</summary>
    private static string? HomeLine(NpcRecord rec) =>
        _home?.Id != rec.Id ? null
        : _home.Guest ? "You're at their apartment: you came home with them for the night. Make yourself at home; you're staying until the morning." // FB-85
        : "You're at home, in the apartment you share with them: relaxed, the way you are at home.";

    /// <summary>They're coming along with you (FB-62) from home: home lets them go to the follow.</summary>
    private static void HomeHandsOver(string id)
    {
        if (_home?.Id != id) return;
        try { Seats.Free(_home.Seat, _home.Ghost); } catch { }
        _home = null;
    }
}
