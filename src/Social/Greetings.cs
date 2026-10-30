using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nivalis;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using UnityEngine;
using Kind = NivalisAIChat.Social.GreetingRules.Kind;

namespace NivalisAIChat.Social;

/// <summary>
/// People who know you react when you pass: friends wave or call you over, acquaintances nod, people who can't stand
/// you snub you (see <see cref="GreetingRules"/>). Scans a few times a second at most, one reaction at a time, with a
/// per-person cooldown. Records are read off the main thread and cached.
/// </summary>
internal static class Greetings
{
    private sealed class Reaction
    {
        public Kind Kind;
        public string Line = "";
        public string Mood = "";
        public float Until;
        /// <summary>The inner-life round: how the chat opens when it isn't a wave or a nod (a meet-up: "you'd agreed to meet them here").</summary>
        public string Custom = "";
    }

    private const float ScanEvery = 1.5f, AfterAnyReaction = 20f, ApproachWindow = 35f;

    private static float _nextScan, _quietUntil;
    private static readonly Dictionary<string, float> CooldownUntil = new();
    private static readonly Dictionary<string, Reaction> Recent = new();
    private static readonly ConcurrentDictionary<string, NpcRecord?> Known = new();
    private static readonly ConcurrentDictionary<string, bool> Loading = new();
    private static NpcStore? _cacheFor;
    private static readonly System.Random Rng = new();

    /// <summary>Drop the cached copy after a conversation or a greeting changed the record.</summary>
    public static void Forget(string id) => Known.TryRemove(id, out _);

    /// <summary>After /resetall: nobody knows the player any more. Main thread only.</summary>
    public static void ForgetAll()
    {
        Known.Clear();
        Recent.Clear();
        CooldownUntil.Clear();
    }

    /// <summary>They waved the player over a moment ago and still want a word.</summary>
    public static bool WantsToTalk(string id) =>
        Recent.TryGetValue(id, out var r) && r.Kind == Kind.WaveOver && r.Line.Length > 0 && Time.unscaledTime < r.Until;

    /// <summary>If they reacted to the player in the last half-minute, how the conversation begins and the mood they're in.</summary>
    public static (string approach, string mood)? TakeApproach(string id, string who)
    {
        if (!Recent.TryGetValue(id, out var r)) return null;
        Recent.Remove(id);
        if (Time.unscaledTime >= r.Until || r.Line.Length == 0) return null;
        return (r.Custom.Length > 0 ? r.Custom.Replace("{line}", r.Line) : GreetingRules.Approach(r.Kind, who, r.Line), r.Mood);
    }

    /// <summary>
    /// The inner-life round: someone who came to meet you as planned greeted you; the next chat with them opens as that
    /// moment (<paramref name="approach"/>, with "{line}" for what they said).
    /// </summary>
    public static void MarkMeetup(string id, string mood, string approach)
    {
        Mark(id, Kind.Wave, mood);
        if (Recent.TryGetValue(id, out var r)) { r.Custom = approach; r.Until = Time.unscaledTime + 60f; }
    }

    /// <summary>The inner-life round: how they feel underneath now (carried), instead of a fresh roll; null to roll as always.</summary>
    public static Func<string, string?>? CarriedMood { get; set; }

    /// <summary>
    /// The inner-life round: what they've seen or heard of the player, -1 (bad) .. +1 (good): it makes a wave less likely
    /// and a snub more likely after seeing you be rude, and the other way round.
    /// </summary>
    public static Func<string, float>? Opinion { get; set; }

    /// <summary>You were just talking to them: they won't "notice you walking past" for a while, and nobody else for a moment.</summary>
    public static void JustTalked(string id, float cooldownSeconds)
    {
        CooldownUntil[id] = Time.unscaledTime + cooldownSeconds;
        Recent.Remove(id);
        _quietUntil = Mathf.Max(_quietUntil, Time.unscaledTime + 15f);
    }

    /// <summary>Remember that they just reacted (for the talk prompt and how the next conversation opens).</summary>
    public static void Mark(string id, Kind kind, string mood)
    {
        if (Recent.Count > 50)
            foreach (var old in Recent.Where(r => Time.unscaledTime >= r.Value.Until).Select(r => r.Key).ToList()) Recent.Remove(old);
        Recent[id] = new Reaction { Kind = kind, Mood = mood, Until = Time.unscaledTime + ApproachWindow };
    }

    public static void Said(string id, string line)
    {
        if (Recent.TryGetValue(id, out var r)) r.Line = line;
    }

    /// <summary>
    /// Called each frame while no chat is open and the player is in free movement. <paramref name="busy"/>: someone
    /// already talking (a conversation, a call, a thought, shop talk) isn't greeted over it; asked with their id, so the
    /// far end of a phone call counts too.
    /// </summary>
    public static void Tick(ModConfig cfg, NpcStore store, Action<Character, NpcRecord, Kind, string> react, Func<Character, string, bool>? busy = null)
    {
        if (!cfg.NpcsGreetYou.Value) return;
        float now = Time.unscaledTime;
        if (now < _nextScan) return;
        _nextScan = now + ScanEvery;
        if (_cacheFor != store || Known.Count > 400) { Known.Clear(); _cacheFor = store; } // mock mode switched stores, or a long session
        if (now < _quietUntil) return;

        var cam = GameContext.PlayerCamera();
        if (cam == null) return;
        var pc = GameContext.PlayerCharacter();
        Vector3 eye = cam.transform.position, fwd = cam.transform.forward;
        float range = Mathf.Clamp(cfg.GreetRange.Value, 3f, 40f);
        float sightDistance = Mathf.Clamp(float.IsFinite(cfg.SightDistance.Value) ? cfg.SightDistance.Value : 10f, 1f, 40f);
        float sightAngle = Mathf.Clamp(float.IsFinite(cfg.SightAngle.Value) ? cfg.SightAngle.Value : 120f, 10f, Sight.MaxAngle);

        var nearby = new List<(Character c, float d)>();
        var all = Character.instances;
        if (all == null) return;
        var it = all.GetEnumerator();
        while (it.MoveNext())
        {
            var c = it.Current;
            if (c == null || !c.isActiveAndEnabled) continue;
            if (pc != null && c.gameObject == pc.gameObject) continue;
            if (NpcHold.IsHeld(c)) continue; // talking already (with you, other residents, on a call); the rest is asked below, by id
            Vector3 to = c.transform.position + Vector3.up * 1.5f - eye;
            float d = to.magnitude;
            if (d > range || d < 2f) continue;
            if (Vector3.Dot(fwd, to / d) < 0.3f) continue; // only people roughly in front of you
            // ...and who can see you: inside their own sight triangle, the way they face (FB-29; the game's own sight untouched).
            if (pc != null)
            {
                Vector3 at = c.transform.position, me = pc.transform.position, face = c.transform.forward;
                if (!Sight.Sees((at.x, at.z), (face.x, face.z), (me.x, me.z), sightDistance, sightAngle)) continue;
            }
            nearby.Add((c, d));
        }
        if (nearby.Count == 0) return;

        GameSnapshot? when = null;
        foreach (var (c, _) in nearby.OrderBy(n => n.d).Take(12))
        {
            string id;
            try { id = GameContext.NpcId(c); } catch { continue; }
            if (busy?.Invoke(c, id) == true) continue; // talking already (a conversation, a call or its far end, a thought, shop talk)
            if (CooldownUntil.TryGetValue(id, out var cd) && now < cd) continue;
            if (!Known.TryGetValue(id, out var rec))
            {
                // First time near them this session: read their file in the background, decide next scan.
                if (Loading.TryAdd(id, true))
                    Task.Run(() => { try { Known[id] = store.Glance(id); } catch { Known[id] = null; } finally { Loading.TryRemove(id, out _); } }); // no history: up to 400 kept
                continue;
            }
            if (rec == null || rec.State.TimesMet < 1) continue; // strangers don't wave at you
            if (NpcTargeting.WhyNot(c, cfg.ChatWithNamedNpcs.Value) != null) continue;
            if (NpcTargeting.Occluded(eye, c.transform.position + Vector3.up * 1.5f, c, pc)) continue;

            CooldownUntil[id] = now + Mathf.Max(1f, cfg.GreetCooldownMinutes.Value) * 60f;
            when ??= GameContext.Capture(null);
            var (mood, _) = Moods.Roll(Rng, rec.Persona, when.Hour, when.Weather, Features.Feeling(rec.State.Affinity), rec.Visual.IsAndroid,
                Math.Clamp(cfg.Friendliness.Value, 0, 100) / 100.0);
            if (Features.InnerLife && CarriedMood?.Invoke(id) is { Length: > 0 } carried) mood = carried; // still feeling what happened lately
            mood = Features.Mood(mood); // FB-45: moods off, none at all: not in the line's prompt, its voice or what's marked
            var (kind, chance) = GreetingRules.Decide(rec.State, mood, cfg.GreetChance.Value, when.GameDay, ModRuntime.MilestonesNow());
            if (kind != null && Features.InnerLife && Opinion?.Invoke(id) is float seen && Math.Abs(seen) > 0.25f)
                chance *= kind == Kind.Snub ? (seen < 0 ? 1.3 : 0.7) : (seen < 0 ? 0.5 : 1.2); // what they saw you do lately
            if (kind == null || Rng.NextDouble() > chance) continue;

            _quietUntil = now + AfterAnyReaction;
            Mark(id, kind.Value, mood);
            Log.Debug($"Greeting: {rec.Persona.Name} {kind} (mood {mood}, affinity {rec.State.Affinity}, chance {chance:0.00})");
            react(c, rec, kind.Value, mood);
            return; // one reaction at a time
        }
    }

    /// <summary>The game's own gesture animations (the same triggers its characters use).</summary>
    public static void Gesture(Character c, Kind kind)
    {
        string trigger = kind switch
        {
            Kind.WaveOver or Kind.Wave => nameof(Character.AnimatorTriggerType.Greeting),
            Kind.Nod => nameof(Character.AnimatorTriggerType.Nod),
            _ => nameof(Character.AnimatorTriggerType.Deny),
        };
        try
        {
            // Someone sitting stays seated: a standing gesture would pull them off their seat (CF-03 review).
            if (GameContext.IsSeated(c)) return;
            var anim = c.Animator;
            if (anim == null) return;
            foreach (var p in anim.parameters)
                if (p.type == AnimatorControllerParameterType.Trigger && p.name == trigger) { anim.SetTrigger(trigger); return; }
            Log.Debug($"gesture: no '{trigger}' trigger on this animator");
        }
        catch (Exception e) { Log.Debug("gesture: " + e.Message); }
    }
}
