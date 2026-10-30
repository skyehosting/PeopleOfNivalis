using System;
using System.Collections.Generic;
using System.Linq;
using Nivalis;
using NivalisAIChat.Game;
using NivalisAIChat.Mind;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using UnityEngine;

namespace NivalisAIChat;

/// <summary>
/// The inner-life round, step 2/3 (bodies show what's inside, the research's "mood and weather in the body" and "NPC to
/// NPC reactions"): people near you walk the way they feel (the game's own walk styles: happy, strolling, sad, hurried),
/// friends who spot each other wave (and may get talking: a hint to the chatter director, within its own budget), people
/// who can't stand each other look away, and someone who saw you do something bad looks away as you pass. Only inside the
/// chatter ring, never anyone held, seated, at work or under 18, at most one reaction every few seconds. Zero tokens.
/// Whether the game keeps a walk style we set is unknown (the probe): one it keeps putting back is left alone.
/// </summary>
public static partial class ModRuntime
{
    private sealed class WalkMark
    {
        public Character C = null!;
        /// <summary>Who it was set for (review game IL-12: a reused body address is someone else).</summary>
        public string Id = "";
        public Character.AnimatorWalkType Style, Before;
        public int Reverts;
        public bool GaveUp;
    }

    private static readonly Dictionary<IntPtr, WalkMark> Walks = new();
    private static readonly Dictionary<string, float> ReactedUntil = new();
    private static float _nextBodyTick, _nextReaction;
    private static bool _walkBroken, _walkRevertSaid, _walkSetSaid;
    /// <summary>Two friends who just spotted each other: TryPair starts them first, until then.</summary>
    private static (string a, string b, float until)? _preferPair;

    /// <summary>The game's own mood walks: ones we may set, and ones we may put back from.</summary>
    private static readonly HashSet<Character.AnimatorWalkType> MoodWalks = new()
    {
        Character.AnimatorWalkType.Walk, Character.AnimatorWalkType.WalkHappy, Character.AnimatorWalkType.WalkSad,
        Character.AnimatorWalkType.WalkStroll, Character.AnimatorWalkType.WalkHurry,
    };

    private static void TickBody()
    {
        float now = Time.unscaledTime;
        if (now < _nextBodyTick) return;
        _nextBodyTick = now + 1.5f;
        bool on = Features.InnerLife && _cfg.BodyLanguage.Value && _minds != null && !_resetting && InGameplay();
        if (!on) { if (Walks.Count > 0 || ReactedUntil.Count > 0) ForgetBodyLanguage(); return; }
        var pc = GameContext.PlayerCharacter();
        if (pc == null) return;
        long game = GameNow();
        if (game < 0) return;
        try
        {
            Vector3 me = pc.transform.position;
            float range = Mathf.Min(ChatterSettingsNow().Range, 30f); // the chatter ring, and close enough to be seen
            var near = new List<(Character c, string id, Vector3 pos)>();
            var here = new HashSet<IntPtr>();
            foreach (var (c, ptr, pos) in NearBodies(pc))
            {
                if (near.Count >= 14) break;
                if (Flat(pos - me).magnitude > range) continue;
                string id;
                try { id = GameContext.NpcId(c); } catch { continue; }
                if (id.Length == 0 || id.StartsWith("extra_", StringComparison.Ordinal) || KnownUnder18(id) || MinorInfo(c, id).minor) continue;
                // Review (game IL-10; the author: "don't break any of the base mechanics"): a customer mid-visit (on their way
                // to a table, queuing, ordering, eating) is the game's: no walk changes, no gestures (their walk goes back, below).
                if (GameContext.VisitOf(c).step.Length > 0) continue;
                near.Add((c, id, pos));
                here.Add(ptr);
            }
            if (!_walkBroken) foreach (var (c, id, _) in near) MoodWalk(c, id, game);
            foreach (var kv in Walks.Where(kv => !here.Contains(kv.Key)).ToList()) { RestoreWalk(kv.Value); Walks.Remove(kv.Key); }
            if (now >= _nextReaction && !ChatOpen) React(near, pc, now);
            if (ReactedUntil.Count > 200) foreach (var k in ReactedUntil.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList()) ReactedUntil.Remove(k);
        }
        catch (Exception e) { Log.Debug("body language: " + e.Message); }
    }

    /// <summary>Which walk shows how they feel underneath: none when they're near their resting point (the game's own walk then).</summary>
    internal static Character.AnimatorWalkType? WalkFor(float v, float a, float baseV, float baseA)
    {
        if (Math.Abs(v - baseV) < 0.2f && Math.Abs(a - baseA) < 0.2f) return null;
        if (v >= 0.35f && a >= 0.5f) return Character.AnimatorWalkType.WalkHappy;
        if (v >= 0.2f && a < 0.35f) return Character.AnimatorWalkType.WalkStroll;
        if (v <= -0.35f && a < 0.4f) return Character.AnimatorWalkType.WalkSad;
        if (v < -0.2f && a >= 0.6f) return Character.AnimatorWalkType.WalkHurry;
        return null;
    }

    private static void MoodWalk(Character c, string id, long game)
    {
        IntPtr ptr = c.Pointer;
        Walks.TryGetValue(ptr, out var mark);
        if (mark != null && (mark.Id != id || !Alive(mark.C))) { Walks.Remove(ptr); mark = null; } // review (game IL-6/12): a reused address is someone else
        // Held (the mod has them), sitting, or at work (staff walks are the game's): their own walk back, and forgotten (review game IL-5).
        if (NpcHold.IsHeld(c) || GameContext.IsSeated(c) || GameContext.IsWorkerTask(c) || GameContext.OnShift(c)) { if (mark != null) { RestoreWalk(mark); Walks.Remove(ptr); } return; }
        if (_minds!.Point(id, game) is not { } p) return;
        var want = WalkFor(p.v, p.a, p.baseV, p.baseA);
        if (want == null) { if (mark != null) { RestoreWalk(mark); Walks.Remove(ptr); } return; }
        if (mark is { GaveUp: true }) return;
        Character.AnimatorWalkType cur;
        try { cur = c.currentWalk; } catch { return; }
        if (mark != null && mark.Style == want.Value)
        {
            if (cur == want.Value) { mark.Reverts = 0; return; }
            // The game put its own walk back: a few times, and it's theirs to keep.
            if (++mark.Reverts >= 4)
            {
                mark.GaveUp = true;
                if (!_walkRevertSaid) { _walkRevertSaid = true; Log.Info("Body language (probe): the game puts its own walk back on some people; they're left with it."); }
                return;
            }
        }
        else if (cur == want.Value || !MoodWalks.Contains(cur)) return; // already walking that way, or the game's own special walk (staff, cold, wind)
        try { c.ChangeWalkWeight(want.Value, 0.6f); }
        catch (Exception e)
        {
            _walkBroken = true;
            Log.Info($"Body language: walk styles can't be set here ({e.GetType().Name}); left alone this session.");
            return;
        }
        if (!_walkSetSaid) { _walkSetSaid = true; Log.Info($"Body language (probe): someone near you walks {want.Value} (they were {cur})."); }
        Walks[ptr] = new WalkMark { C = c, Id = id, Style = want.Value, Before = mark?.Before ?? cur, Reverts = mark?.Reverts ?? 0 };
    }

    /// <summary>The walk they had before, back (only when it's still ours showing).</summary>
    private static void RestoreWalk(WalkMark m)
    {
        try
        {
            if (!m.GaveUp && Alive(m.C) && m.C.currentWalk == m.Style)
                m.C.ChangeWalkWeight(MoodWalks.Contains(m.Before) ? m.Before : Character.AnimatorWalkType.Walk, 0.6f);
        }
        catch { }
    }

    /// <summary>Everyone's own walk back, every reaction forgotten (off, a load, a stop).</summary>
    private static void ForgetBodyLanguage()
    {
        foreach (var m in Walks.Values) RestoreWalk(m);
        Walks.Clear();
        ReactedUntil.Clear();
        _preferPair = null;
    }

    /// <summary>One reaction, at most, a moment later: someone wary of you looks away, or two residents who know each other react.</summary>
    private static void React(List<(Character c, string id, Vector3 pos)> near, PlayerCharacter pc, float now)
    {
        float sightD = Mathf.Clamp(float.IsFinite(_cfg.SightDistance.Value) ? _cfg.SightDistance.Value : 10f, 1f, 40f);
        float angle = Mathf.Clamp(float.IsFinite(_cfg.SightAngle.Value) ? _cfg.SightAngle.Value : 120f, 10f, Sight.MaxAngle);
        bool Sees(Character c, Vector3 from, Vector3 to)
        {
            var f = c.transform.forward;
            return Sight.Sees((from.x, from.z), (f.x, f.z), (to.x, to.z), sightD, angle);
        }
        bool Free(Character c, string id) => !NpcHold.IsHeld(c) && !GameContext.IsSeated(c) && !TalkingNow(c, id) && !GameContext.IsWorkerTask(c)
                                             && !GameContext.OnShift(c) && GameContext.VisitOf(c).step.Length == 0;
        Vector3 you = pc.transform.position;
        // Someone who saw you do something bad lately sees you now: they look away.
        foreach (var (c, id, pos) in near)
        {
            if (Flat(pos - you).magnitude > 10f || ReactedUntil.TryGetValue("you:" + id, out var u) && now < u) continue;
            if (OpinionOfYou(id) > -0.3f || !Free(c, id) || !Sees(c, pos, you)) continue;
            ReactedUntil["you:" + id] = now + 600f;
            _nextReaction = now + 4f;
            Later(UnityEngine.Random.Range(0.3f, 1.2f), () => { if (Alive(c)) BodyTrigger(c, nameof(Character.AnimatorTriggerType.LookDown)); });
            Log.Debug($"body language: {id} looks away from you (what they saw you do)");
            return;
        }
        if (_pairs == null) return;
        var grudges = _couples?.Grudges() ?? new List<CoupleBook.Secret>();
        for (int i = 0; i < near.Count; i++)
            for (int j = i + 1; j < near.Count; j++)
            {
                var (ci, ii, pi) = near[i];
                var (cj, ij, pj) = near[j];
                float d = Flat(pi - pj).magnitude;
                if (d < 4f || d > 15f) continue;
                var pair = _pairs.Get(ii, ij);
                bool grudge = grudges.Any(g => (g.Partner == ii && g.With == ij) || (g.Partner == ij && g.With == ii));
                bool friends = pair is { Times: > 0, Affinity: >= 40 } && !grudge;
                bool foes = grudge || pair is { Times: > 0, Affinity: <= -40 };
                if (!friends && !foes) continue;
                string key = PairBook.Key(ii, ij);
                if (ReactedUntil.TryGetValue(key, out var until) && now < until) continue;
                Character a, b;
                string ia, ib;
                if (Sees(ci, pi, pj)) { (a, ia, b, ib) = (ci, ii, cj, ij); }
                else if (Sees(cj, pj, pi)) { (a, ia, b, ib) = (cj, ij, ci, ii); }
                else continue;
                if (!Free(a, ia)) continue;
                ReactedUntil[key] = now + 600f;
                _nextReaction = now + 4f;
                string trigger = friends ? nameof(Character.AnimatorTriggerType.Greeting) : nameof(Character.AnimatorTriggerType.LookDown);
                Later(UnityEngine.Random.Range(0.3f, 1.5f), () => { if (Alive(a)) BodyTrigger(a, trigger); });
                if (friends && Free(b, ib) && UnityEngine.Random.value < 0.35f) _preferPair = (ia, ib, now + 20f); // they may stop and talk (the chatter's own budget)
                Log.Debug($"body language: {ia} {(friends ? "waves at" : "looks away from")} {ib}");
                return;
            }
    }

    /// <summary>One of the game's own animator triggers on someone standing (a seated person would be pulled off their seat).</summary>
    private static void BodyTrigger(Character c, string trigger)
    {
        try
        {
            if (GameContext.IsSeated(c) || NpcHold.IsHeld(c)) return;
            var anim = c.Animator;
            if (anim == null) return;
            foreach (var p in anim.parameters)
                if (p.type == AnimatorControllerParameterType.Trigger && p.name == trigger) { anim.SetTrigger(trigger); return; }
        }
        catch (Exception e) { Log.Debug("body trigger: " + e.Message); }
    }
}
