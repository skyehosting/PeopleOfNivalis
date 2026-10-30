using System;
using System.Collections.Generic;
using System.Linq;
using Nivalis;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.UI;
using UnityEngine;

namespace NivalisAIChat;

/// <summary>
/// FB-62 (the author: "Minimum, 2/3rd's of a meter is the closes they will walk to you and their speed should
/// dynamically adjust and they should continue to navpath to follow you. So if you run, they begin to run too. If you use
/// a cab to a different area or enter a building, maybe they do as well"): people who come along with you.
///
/// It starts from a chat: they agreed (the reply's follow "yes", only when [WHAT YOU'D DO] said they would), and they
/// start once the chat closes. NpcHold walks them (Game/NpcHold.cs, Follow). Here: a door or a ride that puts you
/// somewhere else in the same scene puts them behind you; a ride to another part of the city takes their ghost there
/// (the game's own GhostManager.TransferGhostToScene) and holds the body it gets; far behind and out of sight, they're
/// put behind you. It ends when they're told to stop, after FollowMinutes, when they can't be found, on a load or a reset.
/// </summary>
public static partial class ModRuntime
{
    private sealed class Follower
    {
        public Character C = null!;
        public IntPtr GhostPtr;
        public Nivalis.GhostSystem.Ai.AgentGhost? Ghost;
        public string Id = "", Name = "";
        public float Since, LostSince = -1f, FarSince = -1f;
        public bool Paused;
        /// <summary>FB-63: sitting with you on a date (a borrowed chair): not walked on until they stand.</summary>
        public bool Seated;
        /// <summary>Getting up from a chair (at home) to come along: they follow once they're standing.</summary>
        public bool StandThenFollow;
        /// <summary>FB-73: fishing beside you (the game's Fishing pose): not walked on until TickFishing stands them up.</summary>
        public bool Fishing;
        public int Scene = -1;
        /// <summary>FB-86: their place beside the one just behind you (metres to one side), kept while they follow.</summary>
        public float Side;
        public bool SideSet;
    }

    private static readonly List<Follower> Followers = new();
    /// <summary>Who agreed to come along in the chat that's open: they start once it closes.</summary>
    private static (Character c, string id, string name)? _followAfterChat;
    private static bool _stopAfterChat;
    /// <summary>FB-85: why a follow stops after the chat, as the log says it (it used to say "you told them to stop" for their choice too).</summary>
    private static string _stopWhy = "";
    private static Vector3 _followPlayerLast;
    private static bool _followPlayerSeen;
    private static int _followSceneEpoch = -1;
    private static float _placeFollowersAt = -1f;
    private const int MaxFollowers = 3;
    private static bool _saidTransfer;

    /// <summary>Whether someone would come along if asked (the code's decision, told to them in [WHAT YOU'D DO]).</summary>
    private static (bool ok, string why) WouldFollow(Character c, NpcRecord rec)
    {
        if (!Features.Following) return (false, "");
        if (Followers.Any(f => Same(f.C, c))) return (true, "already");
        if (Followers.Count >= MaxFollowers) return (false, "you've got enough people with you already");
        if (GameContext.AtWork(c)) return (false, "you're at work");
        bool close = Features.Standing(rec.State.Relationship) >= 50 || rec.State.Romance.Length > 0;
        if (!close) return (false, "you don't know them well enough to go off with them");
        return (true, "");
    }

    /// <summary>[WHAT YOU'D DO] for the person you're talking to: coming along, and (FB-63) dating, dates, moving in.</summary>
    private static void FillWouldDo(GameSnapshot snap, Character? c, NpcRecord? rec)
    {
        snap.WouldDo = new List<string>();
        if (c == null || rec == null) return;
        // FB-85: out with you (a date, a night at yours): running out of things to say ends the conversation, not the evening.
        snap.OutWithPlayer = _date?.Id == rec.Id || _stay?.Id == rec.Id;
        try
        {
            if (Features.Following)
            {
                if (Followers.Any(f => Same(f.C, c)) || _followAfterChat?.c == c)
                    snap.WouldDo.Add("You're coming along with them (following them around). If they tell you to stop following, or you need to go, set follow \"stop\".");
                else
                {
                    var (ok, why) = WouldFollow(c, rec);
                    snap.WouldDo.Add(ok
                        ? "If they ask you to come along with them now, you'd go (for a while): say yes your way and set follow \"yes\"."
                        : $"If they ask you to come along with them: you can't now ({why}); say so your way.");
                }
                // FB-73: fishing together (they come along, and fish beside them at the water).
                if (_cfg.FishTogether.Value)
                {
                    if (FishBuddies.Contains(rec.Id)) snap.WouldDo.Add("You're going fishing with them. When you're done, or they say so, set fishing \"stop\".");
                    else
                    {
                        var (ok, why) = Followers.Any(f => Same(f.C, c)) ? (true, "") : WouldFollow(c, rec);
                        snap.WouldDo.Add(ok
                            ? "If they ask you to go fishing with them (down at the water), you'd go: say yes your way and set fishing \"yes\"."
                            : $"If they ask you to go fishing: you can't now ({why}); say so your way.");
                    }
                }
            }
            if (Features.Romance) snap.WouldDo.AddRange(RomanceLines(rec, snap));
        }
        catch (Exception e) { Log.Debug("would do: " + e.Message); }
    }

    /// <summary>They walk off at the end of a chat (out of patience, or off to do something): no following after, no date.</summary>
    private static void LeavingYou(Character c, NpcRecord rec)
    {
        // FB-85: on a date with you, or staying the night: the conversation rests, they stay with you (it was the end of the
        // date and the follow, at bedtime most of all, and the game took them back).
        // Review F85-5: but someone storming off (annoyed past their limit) still ends it.
        bool storming = _session != null && _session.Annoyance >= _session.AnnoyanceThreshold;
        if ((_date?.Id == rec.Id || _stay?.Id == rec.Id) && !storming) { if (_romanceConfirm?.id == rec.Id) _romanceConfirm = null; return; }
        if (_stay?.Id == rec.Id) EndStayOver("they stormed off");
        if (Followers.Any(f => Same(f.C, c)) || _followAfterChat?.c == c) { _stopAfterChat = true; _stopWhy = "they had to go"; _followAfterChat = null; }
        if (_date?.Id == rec.Id) EndDate("they had to go", quiet: true);
        if (_romanceConfirm?.id == rec.Id) _romanceConfirm = null;
    }

    /// <summary>/resetnpc: someone forgotten stops coming along (and a date with them is over).</summary>
    private static void ForgetFollowOf(string id)
    {
        foreach (var f in Followers.Where(x => x.Id == id).ToList()) EndFollow(f, "they forgot you", quiet: true);
        if (_followAfterChat?.id == id) _followAfterChat = null;
        if (_date?.Id == id) _date = null;
        if (_stay?.Id == id) _stay = null; // FB-85
        if (_romanceConfirm?.id == id) _romanceConfirm = null;
    }

    /// <summary>The reply's follow: "yes" starts them after the chat, "stop" ends a follow.</summary>
    private static void NoteFollow(Character c, NpcRecord rec, ReplyExtras extras)
    {
        if (!Features.Following) return;
        if (extras.Follow == "yes")
        {
            var (ok, why) = WouldFollow(c, rec);
            if (!ok) { Log.Info($"Following: {rec.Persona.Name} said yes, but they can't now ({why})."); return; }
            if (Followers.Any(f => Same(f.C, c))) return;
            _followAfterChat = (c, rec.Id, SpeakerName(rec));
            _stopAfterChat = false;
            try { Ui.AddLine(LineKind.Notice, "", $"{SpeakerName(rec)} will come with you when you're done talking."); } catch { }
        }
        else if (extras.Follow == "stop" && (Followers.Any(f => Same(f.C, c)) || _followAfterChat?.c == c))
        {
            _stopAfterChat = true;
            _stopWhy = "they stopped, in the conversation"; // FB-85: theirs or asked for, the chat shows which
            _followAfterChat = null;
            try { Ui.AddLine(LineKind.Notice, "", $"{SpeakerName(rec)} stops following you."); } catch { }
        }
    }

    /// <summary>OpenChat: a follower stops walking while you talk (their hold gives way to the chat's).</summary>
    private static void PauseFollowerFor(Character c)
    {
        var f = Followers.FirstOrDefault(x => Same(x.C, c));
        if (f == null) return;
        f.Paused = true;
        // FB-63: your date, seated: the chat takes them as they sit, and they stay seated after.
        if ((f.Seated || f.Fishing) && NpcHold.IsSeatedInSeat(c)) { NpcHold.KeepSeatForChat(c); return; } // FB-73: fishing too
        f.Fishing = false; // review FB73-1: not posed yet (still walking there): the pose is off, they follow after the chat
        NpcHold.StopFollow(c);
        NpcHold.ReleaseMember(c);
    }

    /// <summary>CloseChat: a paused follower walks on (unless told to stop), someone who agreed starts.</summary>
    private static void AfterChatFollow(Character? partner)
    {
        if (!Features.Following) { _followAfterChat = null; return; }
        var f = partner != null ? Followers.FirstOrDefault(x => Same(x.C, partner)) : null;
        if (f != null && _stopAfterChat) { EndFollow(f, _stopWhy.Length > 0 ? _stopWhy : "the conversation ended it", quiet: true); f = null; }
        _stopAfterChat = false;
        _stopWhy = "";
        if (f != null && f.Paused)
        {
            f.Paused = false;
            bool stillSeated = (f.Seated || f.Fishing) && NpcHold.IsHeld(f.C) && NpcHold.SeatBusy(f.C);
            if (!stillSeated) f.Fishing = false; // review FB73-1
            if (!stillSeated && !Resume(f)) EndFollow(f, "they couldn't walk on", fresh: false); // review F85-3: the game has them
        }
        if (_followAfterChat is { } start)
        {
            _followAfterChat = null;
            if (Alive(start.c)) StartFollow(start.c, start.id, start.name);
        }
    }

    private static void StartFollow(Character c, string id, string name)
    {
        if (Followers.Count >= MaxFollowers || Followers.Any(x => Same(x.C, c))) return;
        var f = new Follower { C = c, Id = id, Name = name, Since = Time.unscaledTime };
        try { f.Ghost = c.Agent?.MyGhost; f.GhostPtr = f.Ghost?.Pointer ?? IntPtr.Zero; } catch { }
        try { f.Scene = f.Ghost?.SceneIndex ?? -1; } catch { }
        Followers.Add(f);
        if (NpcHold.SeatBusy(c))
        {
            // On a chair: up first (the game's way), then they come along.
            HomeHandsOver(id); // FB-63: at home, they come along from there
            NpcHold.StandUp(c);
            f.Seated = f.StandThenFollow = true;
            Log.Info($"Following: {name} gets up to come with you.");
            return;
        }
        if (!Resume(f)) { Followers.Remove(f); Log.Info($"Following: {name} couldn't start following (the game has them)."); return; }
        HomeHandsOver(id); // review F8: only once they're following
        Log.Info($"Following: {name} is following you.");
        try { Witness(Mind.Happenings.PlayerFollowed, c.transform.position, id, "", Mind.Happenings.Player, "", GameContext.Capture(null).Location, 0.5f); } catch { } // the inner-life round (their name is looked up)
    }

    private static bool Resume(Follower f)
    {
        var player = GameContext.PlayerCharacter();
        if (player == null || !Alive(f.C)) return false;
        if (!NpcHold.IsHeld(f.C) && !NpcHold.EngageMember(f.C, _cfg.AnimateNpc.Value)) return false;
        // FB-86: the first walks just behind you; a second and a third a little to either side (review F86-3: a free place).
        if (f.Side is not (0f or 0.7f or -0.7f) || Followers.Any(o => o != f && o.Side == f.Side && o.SideSet))
            f.Side = new[] { 0f, 0.7f, -0.7f }.FirstOrDefault(s => !Followers.Any(o => o != f && o.SideSet && o.Side == s));
        f.SideSet = true;
        return NpcHold.Follow(f.C, player.transform, Mathf.Clamp(_cfg.FollowDistance.Value, NpcHold.MinFollowDistance, 3f), f.Side);
    }

    /// <param name="fresh">FB-85: let go into a fresh task (the game's pick for the hour), not the one they were frozen on when they came along.</param>
    private static void EndFollow(Follower f, string why, bool quiet = false, bool fresh = true)
    {
        Followers.Remove(f);
        if (f.Seated) { FreeDateSeat(f); f.Seated = false; } // FB-63: the borrowed chair goes back (they stand up first)
        FishBuddies.Remove(f.Id); f.Fishing = false; // FB-73: no longer coming fishing
        try { NpcHold.StopFollow(f.C); NpcHold.ReleaseMember(f.C, fresh); } catch { }
        Log.Info($"Following: {f.Name} stopped following you ({why}).");
        if (!quiet && Alive(f.C) && Ui.Built)
            try { Ui.ShowBark(f.Name, why.StartsWith("they need", StringComparison.Ordinal) ? "I'd better get going. See you around." : "Lost you there!", f.C.transform, 3f, "follow:" + f.Id); }
            catch { }
    }

    /// <summary>Everyone stops following (a load, a reset, the mod stopping).</summary>
    private static void EndAllFollowers(string why)
    {
        _followAfterChat = null;
        foreach (var f in Followers.ToList()) EndFollow(f, why, quiet: true, fresh: false); // a load or a reset: the city starts again anyway
    }

    /// <summary>
    /// Every frame of play: followers kept with you. A jump of the player (a door, a ride within the scene) puts them
    /// behind you; a new scene takes their ghost there; far behind and out of sight, they're put behind you.
    /// </summary>
    private static void TickFollowers()
    {
        if (Followers.Count == 0) { _followPlayerSeen = false; return; }
        if (_resetting || !Features.Following) { EndAllFollowers("following is off or the city is being reset"); return; }
        var player = GameContext.PlayerCharacter();
        if (player == null) return; // loading: wait
        Vector3 pp;
        try { pp = player.transform.position; } catch { return; }
        float now = Time.unscaledTime;
        bool jumped = _followPlayerSeen && Flat(pp - _followPlayerLast).magnitude > 8f;
        _followPlayerLast = pp;
        _followPlayerSeen = true;
        int epoch = NpcHold.SceneEpochNow();
        bool newScene = _followSceneEpoch >= 0 && epoch != _followSceneEpoch;
        _followSceneEpoch = epoch;
        if (jumped || newScene) _placeFollowersAt = now + 0.6f; // once the move has settled
        int minutes = Math.Max(0, _cfg.FollowMinutes.Value);
        foreach (var f in Followers.ToList())
        {
            if (f.Paused) continue;
            // FB-85: not on a date with you, nor a night at yours (those end their own way).
            if (minutes > 0 && now - f.Since > minutes * 60f && f.Id != _date?.Id && f.Id != _stay?.Id) { EndFollow(f, "they need to get on with their day"); continue; }
            bool alive = Alive(f.C) && SameGhost(f);
            if (!alive)
            {
                if (f.LostSince < 0f) f.LostSince = now;
                if (TryFindBody(f, pp, player)) { f.LostSince = -1f; continue; }
                if (now - f.LostSince > 10f) EndFollow(f, "they couldn't come along", quiet: true);
                continue;
            }
            f.LostSince = -1f;
            if (f.StandThenFollow)
            {
                if (NpcHold.SeatBusy(f.C)) continue;
                f.StandThenFollow = f.Seated = false;
            }
            if (f.Fishing) continue; // FB-73: TickFishing stands them up when you stop
            if (f.Seated)
            {
                // Stage 2 review F2: nobody's date any more (ended, a new day, romance off): up, and they come along.
                if (_date?.Id != f.Id || !Features.Romance)
                {
                    if (_date == null) FreeDateSeat(f); // re-check V-3: the seat is only ever the date's (none going on now)
                    NpcHold.StandUp(f.C);
                    f.StandThenFollow = true;
                }
                continue; // FB-63: sitting with you (TickDateSeat stands them up when you go)
            }
            if (!NpcHold.IsFollowing(f.C) && !Resume(f)) { EndFollow(f, "the game needed them", fresh: false); continue; } // review F85-3
            float dist = Flat(f.C.transform.position - pp).magnitude;
            if (_placeFollowersAt > 0f && now >= _placeFollowersAt && dist > 6f) { PlaceBehind(f, player); continue; }
            // Far behind (stuck behind something, or you ran off): out of sight, they catch up behind you.
            if (dist > 25f)
            {
                if (f.FarSince < 0f) f.FarSince = now;
                if (now - f.FarSince > 4f && !OnScreen(f.C)) { PlaceBehind(f, player); f.FarSince = -1f; }
                if (now - f.FarSince > 60f) EndFollow(f, "they lost you");
            }
            else f.FarSince = -1f;
        }
        if (_placeFollowersAt > 0f && now >= _placeFollowersAt) _placeFollowersAt = -1f;
    }

    private static bool SameGhost(Follower f)
    {
        try
        {
            var g = f.C.Agent?.MyGhost;
            // FB-78 (the author: "she kind of went a different way like she forgot"): a body with no ghost (one of the
            // game's background people, or one whose ghost let go) is still them while it's alive; only a body the game
            // gave to someone else (another ghost) isn't.
            // Review FOL-1/FOL-R1: still their body unless it now reads as someone else (another person's id).
            if (g == null) return f.GhostPtr == IntPtr.Zero || GameContext.NpcId(f.C) is var now && (now == f.Id || !now.StartsWith("person_", StringComparison.Ordinal));
            return f.GhostPtr == IntPtr.Zero || g.Pointer == f.GhostPtr;
        }
        catch { return false; }
    }

    /// <summary>A spot about 1.5 m behind the player, on the floor, facing the way they face.</summary>
    private static void PlaceBehind(Follower f, PlayerCharacter player)
    {
        try
        {
            var t = player.transform;
            int i = Followers.IndexOf(f);
            var side = t.right * ((i % 2 == 0 ? 1 : -1) * 0.6f * (1 + i / 2));
            var spot = t.position - t.forward * 1.5f + side;
            NpcHold.PlaceAt(f.C, spot, t.rotation);
            Log.Debug($"following: {f.Name} put behind you.");
        }
        catch (Exception e) { Log.Debug("follow place: " + e.Message); }
    }

    /// <summary>
    /// Their body is gone (another scene, or the game reused it): their ghost is taken to the player's scene, next to the
    /// player, and the body the game gives it is held again. Each step is logged once (a probe: does the game make a body?).
    /// </summary>
    private static bool TryFindBody(Follower f, Vector3 pp, PlayerCharacter player)
    {
        if (f.Ghost == null) return false;
        try
        {
            int scene = -1;
            try { scene = player.MyGhost != null ? player.MyGhost.SceneIndex : -1; } catch { }
            int theirs = -1;
            try { theirs = f.Ghost.SceneIndex; } catch { }
            if (scene >= 0 && theirs != scene && !f.Paused)
            {
                var t = player.transform;
                var pr = new PositionRotation(t.position - t.forward * 1.5f, t.rotation);
                Nivalis.GhostSystem.GhostManager.TransferGhostToScene(f.Ghost, scene, pr);
                if (!_saidTransfer) { _saidTransfer = true; Log.Info($"Following (probe): {f.Name}'s ghost taken from scene {theirs} to {scene}."); }
            }
            foreach (var c in Character.instances)
            {
                if (c == null) continue;
                IntPtr gp = IntPtr.Zero;
                try { gp = c.Agent?.MyGhost?.Pointer ?? IntPtr.Zero; } catch { }
                if (gp == IntPtr.Zero || gp != f.GhostPtr || !Alive(c)) continue; // review F85-2b: never a stale body left in an unloaded place
                f.C = c;
                if (Resume(f))
                {
                    PlaceBehind(f, player);
                    Log.Info($"Following (probe): {f.Name} came along (their body found again).");
                    return true;
                }
            }
        }
        catch (Exception e) { Log.Debug("follow find: " + e.Message); }
        return false;
    }

    private static bool OnScreen(Character c)
    {
        try
        {
            var cam = GameContext.PlayerCamera();
            if (cam == null) return false;
            var v = cam.WorldToViewportPoint(c.transform.position + Vector3.up);
            return v.z > 0 && v.x > -0.05f && v.x < 1.05f && v.y > -0.05f && v.y < 1.05f;
        }
        catch { return false; }
    }

    /// <summary>/stopfollow: everyone following you stops.</summary>
    private static void StopFollowCommand(Action<string> say)
    {
        if (Followers.Count == 0) { say("Nobody is following you."); return; }
        string names = string.Join(", ", Followers.Select(f => f.Name));
        EndAllFollowers("you told them to stop");
        say($"{names} stopped following you.");
    }
}
