using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using Nivalis;
using Nivalis.Fishing;
using Nivalis.InventorySystem;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.UI;
using UnityEngine;
using Character = Nivalis.Character;

namespace NivalisAIChat;

/// <summary>
/// FB-73 (the author: "it would be cool to go fishing with an npc, by the docks near water"; chosen: start it "Both" ways, and
/// they talk "Now and then"). Ask someone to go fishing (the reply's fishing "yes", gated as following is) and they come
/// along; when you fish near water (a rod in your hands), they take the game's own Fishing pose beside you (NpcHold.PoseAt,
/// its own rod). Or fish within a few metres of a resident who's fishing already: they keep you company where they are (never
/// moved). Now and then one of them says something: a catch (the game's OnItemCollected, fish only) or a long quiet spell, at
/// most every three minutes, through SpeakUp (greeting lines: AiGreetingLines, counted in the Usage Optimizer as "greet").
/// When you stop, the ones who fished with you a while remember it. Never anyone under 18. FishTogether.
/// Evidence: docs/research/probe-2026-10-19.md (the Fishing idle and its rod, the water on layer 4, the events).
/// </summary>
public static partial class ModRuntime
{
    private sealed class FishSession
    {
        public float Since, LastLineAt, LastCatchAt, NotSince = -1f;
        public Vector3 Spot;
        public readonly List<string> Catches = new(), Pending = new();
        /// <summary>Who fished with you, and for how long (seconds).</summary>
        public readonly Dictionary<string, float> With = new();
        public readonly Dictionary<string, string> Names = new();
        public int Generation;
    }

    private static readonly HashSet<string> FishBuddies = new();
    private static int _fishBuddiesGen = int.MinValue;
    private static FishSession? _fishing;
    private static float _nextFishLook;
    private static readonly Dictionary<string, float> FishPoseGaveUp = new();
    private static Il2CppSystem.Action<ItemType>? _onCatch;
    private static FishingManager? _catchManager;
    private static int _waterMask;
    private const float FishLineEvery = 180f, FishQuietAfter = 420f, FishNeighbour = 6f;

    /// <summary>The chat's reply: fishing "yes" (they come along, and fish with you at the water) or "stop".</summary>
    private static void NoteFishing(Character c, NpcRecord rec, ReplyExtras extras)
    {
        if (!Features.Following || !_cfg.FishTogether.Value) return;
        if (extras.Fishing == "yes")
        {
            bool following = Followers.Any(f => Same(f.C, c)) || _followAfterChat?.c == c;
            if (!following)
            {
                var (ok, why) = WouldFollow(c, rec);
                if (!ok) { Log.Info($"Fishing: {rec.Persona.Name} said yes, but they can't now ({why})."); return; }
                _followAfterChat = (c, rec.Id, SpeakerName(rec));
                _stopAfterChat = false;
            }
            FishBuddies.Add(rec.Id);
            try { Ui.AddLine(LineKind.Notice, "", $"{SpeakerName(rec)} will go fishing with you: get your rod out down at the water."); } catch { }
            Log.Info($"Fishing: {rec.Persona.Name} is coming fishing with you.");
        }
        else if (extras.Fishing == "stop" && FishBuddies.Remove(rec.Id))
        {
            var f = Followers.FirstOrDefault(x => x.Id == rec.Id);
            if (f is { Fishing: true }) { try { NpcHold.StandUp(f.C); } catch { } f.Fishing = false; f.StandThenFollow = true; }
            Log.Info($"Fishing: {rec.Persona.Name} is done fishing.");
        }
    }

    /// <summary>Twice a second: are you fishing (a rod in your hands, near water); who's with you; a word now and then.</summary>
    private static void TickFishing()
    {
        if (!_cfg.FishTogether.Value || _resetting)
        {
            if (_fishing != null) EndFishing("fishing together is off");
            if (!_cfg.FishTogether.Value && _onCatch != null) UnhookCatches(); // review FB73-8
            return;
        }
        float now = Time.unscaledTime;
        if (now < _nextFishLook) return;
        _nextFishLook = now + 0.5f;
        var pc = GameContext.PlayerCharacter();
        if (pc == null) return;
        if (_fishBuddiesGen != _resetGeneration) { FishBuddies.Clear(); _fishBuddiesGen = _resetGeneration; } // another save: nobody's coming
        if (_fishing != null && _fishing.Generation != _resetGeneration) { EndFishing("another save", quiet: true); return; }
        if (ChatOpen) return; // talking (with one of them, say): fishing waits, nobody gets up
        FishBuddies.RemoveWhere(id => Followers.All(f => f.Id != id) && _followAfterChat?.id != id); // review FB73-7: they never came
        HookCatches();
        bool fishing = PlayerFishing(pc);
        Vector3 pp = pc.transform.position;
        if (fishing)
        {
            if (_fishing == null) { _fishing = new FishSession { Since = now, LastLineAt = now, LastCatchAt = now, Spot = pp, Generation = _resetGeneration }; Log.Info("Fishing: you're fishing."); }
            var s = _fishing;
            s.NotSince = -1f;
            if (Flat(pp - s.Spot).magnitude > 4f) s.Spot = pp; // a few steps along the pier
            PoseBuddies(pc, s, now);
            NoteNeighbours(pc, s);
            MaybeFishLine(s, now);
            return;
        }
        if (_fishing == null) return;
        var fs = _fishing;
        if (fs.NotSince < 0f) fs.NotSince = now;
        // Rod away a few seconds: buddies stand up and come along again; a minute, or you've gone: it's over.
        if (now - fs.NotSince > 8f) StandBuddies();
        if (now - fs.NotSince > 60f || Flat(pp - fs.Spot).magnitude > 20f) EndFishing("you stopped fishing");
    }

    /// <summary>A rod in your hands (the game's FishingEquipment on what you're holding), within 12 m of water.</summary>
    private static bool PlayerFishing(PlayerCharacter pc)
    {
        try
        {
            var held = pc.ObjectHolder?.HeldObject;
            if (held == null || held.GetComponent<FishingEquipment>() == null) return false;
            if (_waterMask == 0)
            {
                foreach (var rod in Resources.FindObjectsOfTypeAll<PlayerFishingRod>()) { if (rod != null) { _waterMask = rod.waterLayer.value; break; } }
                if (_waterMask == 0) _waterMask = 1 << 4; // Unity's Water layer (the probe saw mask 16)
            }
            return Physics.CheckSphere(pc.transform.position, 12f, _waterMask, QueryTriggerInteraction.Collide);
        }
        catch (Exception e) { Log.Debug("fishing check: " + e.Message); return false; }
    }

    /// <summary>Who came fishing with you: into the game's Fishing pose beside you (right first, then left), facing as you face.</summary>
    private static void PoseBuddies(PlayerCharacter pc, FishSession s, float now)
    {
        var buddies = Followers.Where(f => FishBuddies.Contains(f.Id) && !f.Paused && Alive(f.C)).ToList();
        if (buddies.Count == 0) return;
        var idle = NpcHold.FishingIdle(); // only looked up with someone to pose (review FB73-2)
        int side = 0;
        foreach (var f in buddies)
        {
            side++;
            s.Names[f.Id] = f.Name;
            float dist = Flat(f.C.transform.position - pc.transform.position).magnitude;
            if (dist < 8f) s.With[f.Id] = s.With.GetValueOrDefault(f.Id) + 0.5f;
            bool gaveUp = FishPoseGaveUp.TryGetValue(f.Id, out var gave) && now - gave < 60f;
            bool failed = NpcHold.SeatFailed(f.C);
            switch (Social.FishPlan.Decide(f.Fishing, NpcHold.SeatBusy(f.C), failed, dist, idle != null && !f.Seated && !gaveUp))
            {
                case Social.FishPlan.Step.BackToFollowing:
                    f.Fishing = false;
                    if (failed) { FishPoseGaveUp[f.Id] = now; Log.Info($"Fishing: {f.Name} couldn't get to a spot beside you; they stay with you."); }
                    Resume(f);
                    break;
                case Social.FishPlan.Step.StandUp:
                    try { NpcHold.StandUp(f.C); } catch { }
                    f.Fishing = false;
                    f.StandThenFollow = true;
                    break;
                case Social.FishPlan.Step.Pose:
                    var t = pc.transform;
                    // A spot beside you on the walkable ground (never over the water): one side, else the other.
                    Vector3? spot = null;
                    foreach (float sideM in side % 2 == 1 ? new[] { 1.8f, -1.8f } : new[] { -1.8f, 1.8f })
                    {
                        Vector3 want = t.position + t.right * sideM;
                        if (UnityEngine.AI.NavMesh.SamplePosition(want, out var hit, 0.6f, UnityEngine.AI.NavMesh.AllAreas) && Flat(hit.position - want).magnitude < 0.5f)
                        { spot = hit.position; break; }
                    }
                    if (spot == null) { FishPoseGaveUp[f.Id] = now; break; } // nowhere to stand beside you: they stay as they are
                    try { NpcHold.StopFollow(f.C); } catch { }
                    if (NpcHold.PoseAt(f.C, spot.Value, t.rotation, idle!)) { f.Fishing = true; Log.Info($"Fishing: {f.Name} fishes beside you."); }
                    else Resume(f);
                    break;
            }
        }
    }

    /// <summary>Residents already fishing a few metres from you keep you company where they are (never moved, never held).</summary>
    private static void NoteNeighbours(PlayerCharacter pc, FishSession s)
    {
        foreach (var (c, _, pos) in NearBodies(pc))
        {
            if (Flat(pos - pc.transform.position).magnitude > FishNeighbour) continue;
            string id;
            try { id = GameContext.NpcId(c); } catch { continue; }
            if (FishBuddies.Contains(id) || MinorInfo(c, id).minor) continue;
            bool fishingThere = false;
            try { fishingThere = c.state?.current?.TryCast<IdleAction>() is { } ia && ia.Animation == Character.AnimatorStateType.Fishing; } catch { }
            if (!fishingThere) continue;
            s.With[id] = s.With.GetValueOrDefault(id) + 0.5f;
            if (!s.Names.ContainsKey(id)) s.Names[id] = WorldLabelOf(c, id).known;
        }
    }

    /// <summary>A word now and then (at most every three minutes): a catch, or a long quiet spell, from one who's with you.</summary>
    private static void MaybeFishLine(FishSession s, float now)
    {
        string? caught = s.Pending.Count > 0 ? s.Pending[^1] : null;
        s.Pending.Clear();
        if (caught != null) { s.Catches.Add(caught); s.LastCatchAt = now; }
        if (now - s.LastLineAt < FishLineEvery || s.With.Count == 0) return; // review FB73-6: fishing alone, nobody to look for
        bool quiet = caught == null && now - Mathf.Max(s.LastCatchAt, s.LastLineAt) > FishQuietAfter;
        if (caught == null && !quiet) return;
        var pc = GameContext.PlayerCharacter();
        if (pc == null) return;
        // The one nearest you who's fishing with you (a buddy first).
        Character? who = null;
        string id = "";
        float best = float.MaxValue;
        foreach (var (c, _, pos) in NearBodies(pc))
        {
            string cid;
            try { cid = GameContext.NpcId(c); } catch { continue; }
            if (!s.With.ContainsKey(cid) || MinorInfo(c, cid).minor) continue;
            float d = Flat(pos - pc.transform.position).magnitude - (FishBuddies.Contains(cid) ? 5f : 0f);
            if (d < best) { best = d; who = c; id = cid; }
        }
        if (who == null) { s.LastLineAt = now - FishLineEvery + 30f; return; } // nobody close just now: look again in half a minute
        s.LastLineAt = now;
        string situation = caught != null
            ? $"You're fishing beside the newcomer down at the water. They just reeled in {caught}. React to it briefly, the way you would (a word or two, maybe a tease)."
            : "You've been fishing beside the newcomer down at the water a good while and nothing's biting. Say something brief, the way you would.";
        string[] stock = caught != null ? new[] { "Nice one.", "Ha, look at that.", "Not bad at all.", "Beginner's luck." } : new[] { "Quiet today.", "They're not biting.", "Peaceful, though." };
        SpeakUp(who, id, situation, fallback: stock[UnityEngine.Random.Range(0, stock.Length)]);
    }

    /// <summary>Your rod away: the ones fishing beside you get up (and come along again once they're standing).</summary>
    private static void StandBuddies()
    {
        foreach (var f in Followers.Where(f => f.Fishing))
        {
            try { NpcHold.StandUp(f.C); } catch { }
            f.Fishing = false;
            f.StandThenFollow = true; // TickFollowers walks them on once they're up
        }
    }

    /// <summary>It's over: who fished with you a while (a minute or more) remembers it, and how it went.</summary>
    private static void EndFishing(string why, bool quiet = false)
    {
        var s = _fishing;
        if (s == null) return;
        _fishing = null;
        StandBuddies();
        if (s.Generation != _resetGeneration) return;
        var brain = ActiveBrain;
        var snap = GameContext.Capture(null);
        string caught = s.Catches.Count == 0 ? "" : s.Catches.Count == 1 ? $"; they caught {s.Catches[0]}" : $"; they caught {s.Catches.Count} things ({string.Join(", ", s.Catches.Distinct().Take(4))})";
        string scene = $"[Day {snap.GameDay}, {snap.Clock}, {(snap.Location.Length > 0 ? snap.Location : "by the water")}: you went fishing with the newcomer for a while{caught}.]";
        int kept = 0;
        foreach (var (id, seconds) in s.With)
        {
            if (seconds < 60f || KnownUnder18(id)) continue;
            try { if (brain.Store.Glance(id) is not { } r || brain.IsKnownMinor(r) || r.Visual.Minor) continue; } catch { continue; } // review FB73-8
            _ = brain.RecordScene(id, scene, snap); // never anyone under 18 (RecordScene checks their record too)
            try { Stays(id, s.Names.GetValueOrDefault(id, ""), PersonaOf(id), "you:fishing", "went fishing with the newcomer", 0.25f, 0.1f, 10, "content"); } catch { }
            kept++;
        }
        if (!quiet) Log.Info($"Fishing over ({why}): {s.Catches.Count} caught, {kept} remember fishing with you.");
    }

    /// <summary>The game's catch event (fish only), on its own fishing manager; again when it's a new one (a new scene).</summary>
    private static void HookCatches()
    {
        try
        {
            var fm = GameContext.Existing<FishingManager>();
            if (fm == null || (_catchManager != null && _catchManager.Pointer == fm.Pointer && !_catchManager.WasCollected)) return;
            UnhookCatches();
            _onCatch = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ItemType>>(new Action<ItemType>(item =>
            {
                try
                {
                    if (item == null || !item.isFish || _fishing == null) return;
                    string name = (item.Name ?? item.ShortName ?? "").Trim();
                    _fishing.Pending.Add(name.Length > 0 ? "a " + name.ToLowerInvariant() : "a fish");
                }
                catch { }
            }));
            fm.add_OnItemCollected(_onCatch);
            _catchManager = fm;
        }
        catch (Exception e) { Log.Debug("fishing catches: " + e.Message); }
    }

    private static void UnhookCatches()
    {
        try { if (_onCatch != null && _catchManager != null && !_catchManager.WasCollected) _catchManager.remove_OnItemCollected(_onCatch); } catch { }
        _onCatch = null;
        _catchManager = null;
    }
}
