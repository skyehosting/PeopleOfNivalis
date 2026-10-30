using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using Nivalis;
using Nivalis.GhostSystem.Ai;
using RootMotion.FinalIK;
using UnityEngine;
using UnityEngine.AI;

namespace NivalisAIChat.Game;

/// <summary>
/// Holds people in place while they talk: the one you're chatting with (facing you), and residents talking to each
/// other (facing whoever speaks). Their own routine is paused, their idle stays alive inside a small band, their head
/// and body turn smoothly, and talk/listen/mood animation plays. For NPC chatter, someone can walk a few metres over to
/// another person first. Everything is restored on release: they pick up what they were doing.
/// </summary>
internal static class NpcHold
{
    /// <summary>One held person: everything needed to keep them in place and to restore them afterwards.</summary>
    internal sealed class Hold
    {
        public Character C = null!;
        public IntPtr CharPtr, GhostPtr;
        /// <summary>FB-53: the game's task for them when the hold began (their ghost's action), to tell on release whether the game moved them on.</summary>
        public IntPtr GhostActionPtr;
        /// <summary>FB-53: the game's own dialogue had them when the hold began (AIAction_DoDialogue): that isn't resumed.</summary>
        public bool InGameDialogue;
        /// <summary>FB-53: at work when the hold began (AIAction_WorkplaceIdle): handed back to the game's own routine, not snapped into a pose.</summary>
        public bool AtWork;
        /// <summary>What they look at: the player's camera, or whoever's talking in their group.</summary>
        public Transform? LookAt;
        /// <summary>Degrees off before their body turns (wide for a chat with you, small in a group so they face each speaker).</summary>
        public float TurnStart = Leash.TurnStart;
        public NavMeshAgent? Nav;
        public bool NavWasStopped;
        public IKSolverLookAt? Look;
        public Transform? LookPrevTarget;
        public float LookPrevWeight, LookWeight;
        public Animator? Anim;
        public float TalkUntil, GestureUntil;
        /// <summary>The gesture their mood calls for (what they go back to after a line).</summary>
        public Character.AnimatorMoodType MoodGesture = Character.AnimatorMoodType.Neutral;
        public bool Talking, Turning, Animate = true;
        /// <summary>
        /// The game's talk and mood layers were put on them (only ever while standing): they're set back on release. Someone
        /// sitting, or held in a pose with a prop, gets none: those layers are a standing body's and pulled a seated one off
        /// its stool, at the start and with every line (CF-03; the author: "leave them sitting how they were").
        /// </summary>
        public bool LayersSet;
        /// <summary>
        /// A glance (FB-16): their head turns to look until then, body still, and goes back to what it was looking at (or,
        /// with nothing before, eases back to the game's own look). 0 = not glancing.
        /// </summary>
        public float GlanceUntil;
        public Transform? GlanceBack;
        public bool Unglancing;
        /// <summary>The last one-off gesture trigger set (reset when the line's over, so it can't fire later).</summary>
        public string LastTrigger = "";
        public CharacterAction? SavedAction;
        public bool SwitchedToIdle, EndBlocked, Standing;
        public Vector3 HoldPos;
        // Stepping out of a pose to talk (off the railing, away from the table) and back into it afterwards.
        public bool Exiting, Returning, Entering;
        public float PhaseUntil, CheckStandAt = -1f;
        public Vector3 OrigPos;
        public Quaternion OrigRot;
        public TransitionAction? Enter;
        // walking over to someone (NPC chatter)
        public bool Walking, WalkFailed, WalkMoved;
        public Vector3 WalkTarget, LastWalkPos;
        public float WalkStop, WalkParam, WalkStarted, ProgressAt, ProgressDist;
        // The game's off-screen stand-in for this person (it keeps the body in step with it): kept with the body while held.
        public AgentGhost? Ghost;
        public bool GhostKept, Seen;
        public Vector3 LastPos;
        public int Jumps, SnapsBlocked, Fakes, FencesLogged;
        /// <summary>Where they stood as each of the game's per-person updates began (see <see cref="Before"/>).</summary>
        public readonly Vector3[] FenceFrom = new Vector3[3];
        public readonly bool[] FenceSet = new bool[3];
        // on the phone (a resident's call): the game's own phone-out, on-the-phone and put-away animations
        public PhonePhase Phone;
        public bool WantsPhone, ReleaseAfterPhone;
        public float PhoneUntil;
        /// <summary>
        /// 0.3.3: a light hold (a customer at a table, mid-visit): their head turns and their mouth moves, and nothing else
        /// is touched: the game's own steps for them run on (ordering, being served, eating, leaving), they're never moved,
        /// stopped or snapped. Not in ByGhost, so their ghost isn't frozen.
        /// </summary>
        public bool Light;
        /// <summary>
        /// FB-62: following someone (the player): on a NavMesh path re-planned as they move, at their pace (walking or
        /// running), never closer than <see cref="FollowDist"/>. Kept in step with their ghost like any hold.
        /// </summary>
        public bool Following;
        public Transform? FollowTarget;
        public float FollowDist = 0.75f, FollowPlanAt, FollowSpeed, FollowParam;
        public Vector3 FollowPlannedFor, FollowLastTarget;
        public Vector3[] FollowPath = Array.Empty<Vector3>();
        public int FollowCorner;
        public bool FollowHasLast;
        /// <summary>FB-86: the gait they're in, the speed they're at (eased), which way you're going, how long you've stood
        /// still, whether they're turning to face you, and their place beside the one behind you (metres to one side).</summary>
        /// <summary>FB-85: let go into a fresh task (the game's own pick, by interrupting what their ghost was frozen on), not back into the old one.</summary>
        public bool FreshAfter;
        public FollowGait.Gait FollowGaitNow;
        public float FollowCurSpeed, FollowStillFor, FollowSide;
        public bool FollowPlayer = true;
        public Vector3 FollowMoveDir;
        public bool FollowFacing;
        /// <summary>
        /// FB-63: a seat (a date at a table): walking over, sitting down the game's own way (its chair-sitting animations),
        /// sitting, standing up. The game's own sitting is the ghost's; this is the body's, while they're held.
        /// </summary>
        public SeatPhase Seat;
        public Vector3 SeatPos;
        public Quaternion SeatRot;
        public float SeatUntil;
        public IdleAction? SeatIdle;
        /// <summary>FB-73: the pose PoseAt asked for (the game's Fishing idle); null = a chair's sitting idle (SitAt).</summary>
        public IdleAction? PoseIdle;
        public bool SeatFailed, ReleaseAfterSeat;
        /// <summary>Seated, and a chat with you opens: the chat takes this hold as it is (they stay seated) and gives it back.</summary>
        public bool KeepForChat;
    }

    internal enum SeatPhase { None, Walking, Entering, Seated, Exiting }

    internal enum PhonePhase { None, Out, On, Away }

    /// <summary>Just let go: watched for a few seconds, for the log (does the game move them once they're free?).</summary>
    /// <summary>
    /// Someone just let go: a jump the game makes (the debug log), what the game does next with someone at work (the debug
    /// log), and their work item back in hand once they're in the pose again (FB-53).
    /// </summary>
    private sealed class Watch
    {
        public Character C = null!; public Vector3 Last; public float Until;
        public bool Jumps, Trace; public IntPtr LastAction;
        public IdleAction? Work; public float WorkCheckAt;
    }
    private static readonly List<Watch> JustReleased = new();
    /// <summary>
    /// FB-51: let go with their phone still out while they couldn't be reached (gone from view as you walked off): the phone is
    /// put away when they're back, and a phone idle still playing is ended, or they'd stand there on the phone (seen in game).
    /// </summary>
    private static readonly List<(Character c, float until)> PhoneLeftOut = new();

    /// <summary>
    /// The game keeps a "ghost" for every resident (where they are in its simulation) and snaps their body onto it now and
    /// then, e.g. out of sight. A held person's ghost could wander off (on to where they were walking), and the body was
    /// yanked after it: a teleport, then dragged back by the hold, again and again. Now the ghost stays with the body while
    /// they are held, those snaps are refused, the game's per-frame updates are fenced, and any jump that still slips through is put back.
    /// </summary>
    private const float JumpMetres = 1f;

    private static Hold? _chat;
    private static readonly Dictionary<IntPtr, Hold> ByChar = new();
    private static readonly Dictionary<IntPtr, Hold> ByGhost = new();

    public static bool Active => _chat != null;
    public static Character? Held => _chat?.C;
    public static bool IsHeld(Character? c) => c != null && ByChar.ContainsKey(c.Pointer);
    private static Hold? Get(Character? c) => c != null && ByChar.TryGetValue(c.Pointer, out var h) ? h : null;

    // ------------------------------------------------------------------ the player's conversation

    public static void Engage(Character c, Transform lookAt, bool animate)
    {
        Release();
        if (Get(c) is { KeepForChat: true, Seat: SeatPhase.Seated } seated)
        {
            // FB-63: your date, seated: the chat takes them as they are (still seated), and gives them back after.
            seated.LookAt = lookAt;
            seated.Animate = animate;
            seated.TurnStart = Leash.TurnStart;
            _chat = seated;
            DbgDoUpdate = DbgSetTask = DbgAi = DbgOnAnimator = 0;
            DbgDrift = 0;
            return;
        }
        if (Get(c) is { } already) Drop(already, now: true); // in a residents' conversation (or still stepping back into a pose): over now
        _chat = Take(c, lookAt, animate);
        DbgDoUpdate = DbgSetTask = DbgAi = DbgOnAnimator = 0;
        DbgDrift = 0;
    }

    public static void Release()
    {
        if (_chat is { KeepForChat: true } kept)
        {
            kept.KeepForChat = false; // FB-63: back to being held seated (the date goes on)
            kept.Talking = false;
            _chat = null;
            return;
        }
        if (_chat != null) Drop(_chat);
    }

    /// <summary>Talking animation and lip-sync for a line; <paramref name="spokenSeconds"/> is the voice's length when there is one.</summary>
    public static void Speak(string text, float spokenSeconds = 0f, string gesture = "")
    {
        if (_chat != null) Speak(_chat, text, spokenSeconds, gesture);
    }

    public static void SetMood(string mood)
    {
        if (_chat != null) SetMood(_chat, mood);
    }

    // ------------------------------------------------------------------ residents talking to each other

    /// <summary>Holds someone for a residents' conversation. False if they're already held (by your chat or another group).</summary>
    public static bool EngageMember(Character c, bool animate)
    {
        if (c == null || IsHeld(c)) return false;
        var h = Take(c, null, animate);
        h.TurnStart = 12f;
        return true;
    }

    /// <summary>
    /// 0.3.3: a light hold for someone the game must keep going (a customer at a table waiting to order, waiting for their
    /// food or eating): head and mouth only (see <see cref="Hold.Light"/>). False if they're already held.
    /// </summary>
    /// <param name="animate">AnimateNpc: off = no lip-sync for them either (final review F6).</param>
    public static bool EngageLight(Character c, bool animate = true)
    {
        if (c == null || IsHeld(c)) return false;
        var h = new Hold { C = c, CharPtr = c.Pointer, Animate = animate, Light = true, TurnStart = 12f };
        try { h.HoldPos = c.transform.position; } catch { }
        try { h.Anim = c.Animator; } catch { h.Anim = null; }
        try
        {
            var lookIk = c.IK != null ? c.IK.lookIK : null;
            h.Look = lookIk != null ? lookIk.solver : null;
            if (h.Look != null)
            {
                h.LookPrevTarget = h.Look.target;
                h.LookPrevWeight = h.Look.GetIKPositionWeight();
                h.LookWeight = h.LookPrevWeight;
            }
        }
        catch (Exception e) { h.Look = null; Log.Debug("light hold look: " + e.Message); }
        h.Standing = false; // never the standing body layers: sitting, or on their way (CF-03)
        ByChar[h.CharPtr] = h; // only here: IsHeld and the look and talk calls see them; the game's patches let them be
        return true;
    }

    /// <param name="fresh">
    /// FB-85: after a long hold (following you, a date, a night at your place) they don't go back to what they were doing
    /// when it began (hours old by now): the game picks them a new task once they're let go (<see cref="Hold.FreshAfter"/>).
    /// </param>
    public static void ReleaseMember(Character? c, bool fresh = false)
    {
        if (Get(c) is not { } h) return;
        if (fresh) h.FreshAfter = true;
        // Stage 2 review F1: the chat has them (seated, kept for it): the chat's own release lets them go, not back to a seat.
        if (h == _chat) { h.KeepForChat = false; return; }
        Drop(h);
    }

    /// <summary>Stage 2 review F3: an old hold on them (still stepping back into a pose) let go at once, so a new one can start.</summary>
    public static void TakeOver(Character? c)
    {
        if (Get(c) is { } h && h != _chat) Drop(h, now: true);
    }

    /// <summary>
    /// On the phone: once they're standing (out of any pose), they take their phone out the game's own way (PhoneEnter),
    /// talk on it (the Phone idle), and put it away (PhoneExit) when they're let go. Standing people only.
    /// </summary>
    public static void OnThePhone(Character c)
    {
        if (Get(c) is { } h && h.Phone == PhonePhase.None) h.WantsPhone = true;
    }

    /// <summary>Who they look (and turn) toward now: the speaker, or whoever they're talking to.</summary>
    public static void LookAtFor(Character c, Transform? target)
    {
        if (Get(c) is { } h) h.LookAt = target;
    }

    /// <summary>
    /// A glance at <paramref name="at"/> for <paramref name="seconds"/>: the head turns, the body stays as it is (someone on
    /// the phone answering you, FB-16), then it looks back. Nothing for someone not held.
    /// </summary>
    public static void Glance(Character c, Transform at, float seconds)
    {
        if (Get(c) is not { } h || at == null) return;
        if (h.GlanceUntil <= 0) h.GlanceBack = h.LookAt;
        h.Unglancing = false;
        h.LookAt = at;
        h.GlanceUntil = Time.unscaledTime + Mathf.Clamp(seconds, 0.5f, 10f);
    }

    public static void SpeakAs(Character c, string text, float seconds, string gesture = "")
    {
        if (Get(c) is { } h) Speak(h, text, seconds, gesture);
    }

    public static void SetMoodFor(Character c, string mood)
    {
        if (Get(c) is { } h) SetMood(h, mood);
    }

    /// <summary>A listener reacts to a line: interested, amused, taken aback (now and then; mostly they just listen).</summary>
    public static void ReactTo(Character c, string heard)
    {
        if (Get(c) is not { Animate: true, Standing: true } h || h.Phone != PhonePhase.None) return; // sitting: no body layers (CF-03)
        double r = Gestures.NextDouble(); // a fraction: the chances below are fractions (Next() gave a whole number, so nobody reacted)
        Character.AnimatorMoodType? react = Gesture(heard) switch
        {
            Character.AnimatorMoodType.Laugh when r < 0.6 => Character.AnimatorMoodType.Laugh,
            Character.AnimatorMoodType.Shocked when r < 0.5 => Character.AnimatorMoodType.Shocked,
            Character.AnimatorMoodType.Argue when r < 0.3 => Character.AnimatorMoodType.Argue,
            _ when r < 0.35 => Character.AnimatorMoodType.Interested,
            _ => null,
        };
        if (react == null) return;
        try { c.ChangeEmotionWeight(react.Value, 0.5f); h.LayersSet = true; } catch { }
        h.GestureUntil = Time.unscaledTime + 2.5f;
    }

    private static readonly System.Random Gestures = new();

    /// <summary>The gesture a line calls for (laughing, arguing, surprised, asking, explaining), as the game's own talking groups do.</summary>
    internal static Character.AnimatorMoodType Gesture(string text)
    {
        string t = (text ?? "").ToLowerInvariant();
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"\b(?:ha(?:ha)+|heh|lol|funny|joke|kidding)\b")) return Character.AnimatorMoodType.Laugh;
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"\b(?:what\?!|seriously|no way|wow|really\?|can't believe|you're joking)")) return Character.AnimatorMoodType.Shocked;
        if (t.Contains('!') && System.Text.RegularExpressions.Regex.IsMatch(t, @"\b(?:shut up|damn|hell|idiot|stupid|ridiculous|never|stop|enough|listen)\b"))
            return Character.AnimatorMoodType.Argue;
        if (t.TrimEnd().EndsWith("?")) return Character.AnimatorMoodType.Interested;
        return Character.AnimatorMoodType.Explain;
    }

    /// <summary>Sitting (or otherwise not standing): they stay put and only turn their head.</summary>
    public static bool IsSeatedHold(Character c) => Get(c) is { } h && !h.Standing;

    /// <summary>
    /// Walk them over to <paramref name="spot"/> (a few metres at most), stopping <paramref name="stop"/> metres short.
    /// Uses their own walk animation; if they don't make progress, the walk is given up (see <see cref="WalkFailed"/>).
    /// </summary>
    public static bool WalkTo(Character c, Vector3 spot, float stop, float walkParam)
    {
        var h = Get(c);
        if (h == null || !h.Standing) return false;
        h.Walking = true;
        h.WalkFailed = false;
        h.WalkTarget = spot;
        h.WalkStop = Mathf.Max(0.5f, stop);
        h.WalkParam = walkParam;
        h.WalkStarted = h.ProgressAt = Time.unscaledTime;
        h.WalkMoved = false;
        h.ProgressDist = Flat(spot - c.transform.position).magnitude;
        return true;
    }

    public static bool IsWalking(Character c) => Get(c)?.Walking == true;

    /// <summary>
    /// FB-62: someone held (EngageMember) follows <paramref name="target"/>: walking or running on the NavMesh at its pace,
    /// stopping <paramref name="distance"/> metres short (0.67 at the least). False if they aren't held standing.
    /// </summary>
    /// <param name="player">Review F86-2: the target is the player (their walk and sprint); a resident following another
    /// resident (jealousy, going somewhere together) walks at a person's own pace and runs only to catch up.</param>
    public static bool Follow(Character c, Transform target, float distance, float side = 0f, bool player = true)
    {
        var h = Get(c);
        if (h == null || h == _chat || h.Light || target == null) return false;
        h.Following = true;
        h.Walking = false;
        h.FollowTarget = target;
        h.FollowDist = Mathf.Max(MinFollowDistance, distance);
        h.FollowPlanAt = 0f;
        h.FollowPath = Array.Empty<Vector3>();
        h.FollowCorner = 0;
        h.FollowHasLast = false;
        h.FollowSpeed = 0f;
        // FB-86: they start standing and set off when you're a step away; a second and third follower walk to either side.
        h.FollowGaitNow = FollowGait.Gait.Stand;
        h.FollowCurSpeed = h.FollowStillFor = 0f;
        h.FollowSide = side;
        h.FollowPlayer = player;
        h.FollowMoveDir = Vector3.zero;
        h.FollowFacing = false;
        if (h.WalkParam <= 0.01f) h.WalkParam = Mathf.Max(0.2f, GameContext.TypicalWalkParam());
        return true;
    }

    /// <summary>The closest a follower comes (the author: "Minimum, 2/3rd's of a meter").</summary>
    public const float MinFollowDistance = 0.67f;

    public static bool IsFollowing(Character? c) => Get(c)?.Following == true;

    public static void StopFollow(Character? c)
    {
        if (Get(c) is { } h) { h.Following = false; h.HoldPos = h.C.transform.position; }
    }

    /// <summary>FB-62: put a follower down at <paramref name="spot"/> at once (a door or a ride took the player there).</summary>
    public static void PlaceAt(Character c, Vector3 spot, Quaternion facing)
    {
        if (Get(c) is not { } h) return;
        try
        {
            if (NavMesh.SamplePosition(spot, out var hit, 2.5f, NavMesh.AllAreas)) spot = hit.position;
            c.transform.SetPositionAndRotation(spot, facing);
            h.HoldPos = h.LastPos = h.LastWalkPos = spot;
            h.FollowPath = Array.Empty<Vector3>();
            h.FollowPlanAt = 0f;
            AfterTick(h, c); // the ghost goes with them
        }
        catch (Exception e) { Log.Debug("place follower: " + e.Message); }
    }

    /// <summary>
    /// FB-63: someone held standing (a follower) walks to <paramref name="seat"/> (a chair's sitting spot) and sits there the
    /// game's own way: its chair-sitting animations (SitChair...). False when they can't (not held, mid-pose, no sitting
    /// animation in this scene). <see cref="SeatFailed"/> tells a walk that didn't get there.
    /// </summary>
    public static bool SitAt(Character c, Vector3 seat, Quaternion facing)
    {
        var h = Get(c);
        if (h == null || h == _chat || h.Light || !h.Standing || h.Seat != SeatPhase.None || h.Phone != PhonePhase.None) return false;
        if (SitIdle() == null) return false;
        h.PoseIdle = null;
        h.Following = false;
        h.SeatPos = seat;
        h.SeatRot = facing;
        h.SeatFailed = false;
        if (h.WalkParam <= 0.01f) h.WalkParam = Mathf.Max(0.2f, GameContext.TypicalWalkParam());
        h.Seat = SeatPhase.Walking;
        h.Walking = true;
        h.WalkFailed = false;
        h.WalkTarget = seat;
        h.WalkStop = 0.5f;
        h.WalkStarted = h.ProgressAt = Time.unscaledTime;
        h.WalkMoved = false;
        h.ProgressDist = Flat(seat - c.transform.position).magnitude;
        return true;
    }

    /// <summary>
    /// FB-73: as <see cref="SitAt"/>, but into another of the game's poses (its Fishing idle, which brings its own rod): they
    /// walk to <paramref name="spot"/>, face <paramref name="facing"/>, and hold <paramref name="idle"/> there (its own way in and
    /// out when the game has one) until <see cref="StandUp"/>. False when they can't (not held standing, mid-pose).
    /// </summary>
    public static bool PoseAt(Character c, Vector3 spot, Quaternion facing, IdleAction idle)
    {
        if (idle == null || idle.WasCollected) return false;
        var h = Get(c);
        if (h == null || h == _chat || h.Light || !h.Standing || h.Seat != SeatPhase.None || h.Phone != PhonePhase.None) return false;
        h.PoseIdle = idle;
        h.Following = false;
        h.SeatPos = spot;
        h.SeatRot = facing;
        h.SeatFailed = false;
        if (h.WalkParam <= 0.01f) h.WalkParam = Mathf.Max(0.2f, GameContext.TypicalWalkParam());
        h.Seat = SeatPhase.Walking;
        h.Walking = true;
        h.WalkFailed = false;
        h.WalkTarget = spot;
        h.WalkStop = 0.4f;
        h.WalkStarted = h.ProgressAt = Time.unscaledTime;
        h.WalkMoved = false;
        h.ProgressDist = Flat(spot - c.transform.position).magnitude;
        return true;
    }

    private static IdleAction? _fishingIdle;
    private static int _fishingScene = -1;
    private static float _noFishUntil = -999f;

    /// <summary>FB-73: the game's own fishing idle ("Fishing", with its rod: docs/research/probe-2026-10-19.md), once per scene.</summary>
    public static IdleAction? FishingIdle()
    {
        if (_fishingIdle != null && !_fishingIdle.WasCollected && _fishingScene == SceneEpoch()) return _fishingIdle;
        if (Time.unscaledTime < _noFishUntil && _fishingScene == SceneEpoch()) return null; // review FB73-2: none here, a while ago
        _fishingIdle = null;
        _fishingScene = SceneEpoch();
        try
        {
            foreach (var a in Resources.FindObjectsOfTypeAll<IdleAction>())
            {
                if (a == null || a.Animation != Character.AnimatorStateType.Fishing) continue;
                _fishingIdle ??= a;
                if (string.Equals((a.name ?? "").Replace("(Clone)", "").Trim(), "Fishing", StringComparison.OrdinalIgnoreCase)) { _fishingIdle = a; break; }
            }
        }
        catch (Exception e) { Log.Debug("fishing idle: " + e.Message); }
        if (_fishingIdle == null) _noFishUntil = Time.unscaledTime + MissingFor;
        return _fishingIdle;
    }

    /// <summary>FB-63: up from the seat the game's way (or the walk to it called off). They're standing again once <see cref="SeatBusy"/> is false.</summary>
    public static void StandUp(Character? c)
    {
        if (Get(c) is not { } h) return;
        BeginStand(h);
    }

    /// <summary>Walking to a seat, sitting down, seated or getting up.</summary>
    public static bool SeatBusy(Character? c) => Get(c) is { } h && h.Seat != SeatPhase.None;

    public static bool IsSeatedInSeat(Character? c) => Get(c)?.Seat == SeatPhase.Seated;

    /// <summary>The last seat asked for couldn't be reached (they're standing, held, where they got to).</summary>
    public static bool SeatFailed(Character? c) => Get(c)?.SeatFailed == true;

    /// <summary>FB-63: a chat with you about to open with them while they sit: it takes the hold as it is.</summary>
    public static void KeepSeatForChat(Character? c)
    {
        if (Get(c) is { Seat: SeatPhase.Seated } h) h.KeepForChat = true;
    }

    private static IdleAction? _sitIdle;
    private static int _sitIdleScene = -1;
    private static float _noSitUntil = -999f;
    private static bool _sitProbed;

    /// <summary>The game's own chair-sitting idle (SitChair), found once per scene; null if this scene has none.</summary>
    private static IdleAction? SitIdle()
    {
        if (_sitIdle != null && !_sitIdle.WasCollected && _sitIdleScene == SceneEpoch()) return _sitIdle;
        if (Time.unscaledTime < _noSitUntil && _sitIdleScene == SceneEpoch()) return null;
        _sitIdle = null;
        _sitIdleScene = SceneEpoch();
        try
        {
            var all = Resources.FindObjectsOfTypeAll<IdleAction>();
            var chairs = new List<IdleAction>();
            foreach (var a in all)
                if (a != null && !a.dance && a.Animation == Character.AnimatorStateType.SitChair) chairs.Add(a);
            // A plain one first: "SitChairIdle"/"SitChair", before an eating or talking variant.
            _sitIdle = chairs.FirstOrDefault(a => Regex.IsMatch((a.name ?? "").Replace("(Clone)", "").Trim(), @"^SitChair(Idle|Loop)?$", RegexOptions.IgnoreCase))
                       ?? chairs.FirstOrDefault();
            if (!_sitProbed)
            {
                _sitProbed = true;
                Log.Info($"Seats (probe): {chairs.Count} chair-sitting idles ({string.Join(", ", chairs.Take(8).Select(a => (a.name ?? "?").Replace("(Clone)", "").Trim()))}); " +
                         $"using '{_sitIdle?.name ?? "none"}', its way in '{(_sitIdle != null ? PoseTransition(_sitIdle, exit: false)?.name ?? "none" : "-")}', " +
                         $"out '{(_sitIdle != null ? PoseTransition(_sitIdle, exit: true)?.name ?? "none" : "-")}'.");
            }
        }
        catch (Exception e) { Log.Debug("sit idle: " + e.Message); }
        if (_sitIdle == null) _noSitUntil = Time.unscaledTime + MissingFor;
        return _sitIdle;
    }

    private static void BeginStand(Hold h)
    {
        switch (h.Seat)
        {
            case SeatPhase.Walking:
                h.Walking = false;
                h.Seat = SeatPhase.None;
                h.HoldPos = h.C.transform.position;
                return;
            case SeatPhase.Entering:
            case SeatPhase.Seated:
                var exit = h.SeatIdle != null ? PoseTransition(h.SeatIdle, exit: true) : null;
                h.EndBlocked = false;
                h.Seat = SeatPhase.Exiting;
                if (exit != null)
                {
                    try { h.C.SetAction(exit, false); } catch (Exception e) { Log.Debug("seat exit: " + e.Message); }
                    h.SeatUntil = Time.unscaledTime + Mathf.Clamp(DurationOf(exit), 0.3f, 4f) + 0.2f;
                }
                else h.SeatUntil = Time.unscaledTime;
                return;
        }
    }

    /// <summary>The seat's phases. True when the seat has the body this frame (nothing else moves them).</summary>
    private static bool TickSeat(Hold h, Character c, float now)
    {
        switch (h.Seat)
        {
            case SeatPhase.Walking:
                if (h.Walking) return false; // TickWalk takes them there
                if (h.WalkFailed || Flat(h.SeatPos - c.transform.position).magnitude > 1.3f)
                {
                    h.Seat = SeatPhase.None;
                    h.SeatFailed = true;
                    h.HoldPos = c.transform.position;
                    return false;
                }
                var idle = h.PoseIdle != null ? (h.PoseIdle.WasCollected ? null : h.PoseIdle) : SitIdle(); // FB-73: PoseAt's pose (gone: failed, never a chair's), else a chair's
                if (idle == null) { h.Seat = SeatPhase.None; h.SeatFailed = true; return false; }
                try
                {
                    c.transform.SetPositionAndRotation(h.SeatPos, h.SeatRot); // the chair's own spot: not moved to the floor
                    h.HoldPos = h.LastPos = h.LastWalkPos = h.SeatPos;
                    AfterTick(h, c);
                    if (h.SavedAction == null && c.state != null && c.state.current != null) { h.SavedAction = c.state.current; h.SwitchedToIdle = true; }
                    if (h.LayersSet)
                    {
                        // A standing body's layers off before sitting (CF-03: they pull a seated one off the chair).
                        try { c.ChangeTalkWeight(Character.AnimatorTalkType.Idle, 0.4f); } catch { }
                        try { c.ChangeEmotionWeight(Character.AnimatorMoodType.Neutral, 0.6f); } catch { }
                        h.LayersSet = false;
                    }
                    h.Standing = false;
                    h.SeatIdle = idle;
                    h.EndBlocked = false;
                    var enter = PoseTransition(idle, exit: false);
                    if (enter != null)
                    {
                        c.SetAction(enter, false);
                        h.Seat = SeatPhase.Entering;
                        h.SeatUntil = now + Mathf.Clamp(DurationOf(enter), 0.3f, 4f) + 0.2f;
                    }
                    else { c.SetAction(idle, false); h.Seat = SeatPhase.Seated; }
                }
                catch (Exception e) { Log.Debug("seat: " + e.Message); h.Seat = SeatPhase.None; h.SeatFailed = true; h.Standing = true; }
                return true;
            case SeatPhase.Entering:
                if (h.EndBlocked || now >= h.SeatUntil)
                {
                    h.EndBlocked = false;
                    try { if (h.SeatIdle != null) c.SetAction(h.SeatIdle, false); } catch (Exception e) { Log.Debug("seated: " + e.Message); }
                    h.Seat = SeatPhase.Seated;
                }
                PinSeat(h, c);
                return true;
            case SeatPhase.Seated:
                h.EndBlocked = false; // seated as long as the date wants
                PinSeat(h, c);
                return true;
            case SeatPhase.Exiting:
                if (h.EndBlocked || now >= h.SeatUntil)
                {
                    h.EndBlocked = false;
                    try { var stand = FindIdle(c); if (stand != null) c.SetAction(stand, false); } catch { }
                    h.Seat = SeatPhase.None;
                    h.Standing = true;
                    h.HoldPos = c.transform.position;
                    h.CheckStandAt = now + 0.3f;
                    if (h.ReleaseAfterSeat)
                    {
                        h.ReleaseAfterSeat = false;
                        Drop(h);
                        return true;
                    }
                }
                return true;
        }
        return false;
    }

    /// <summary>Kept on the chair's spot (the game's own steps would slide them off it).</summary>
    private static void PinSeat(Hold h, Character c)
    {
        try
        {
            var p = c.transform.position;
            if ((p - h.SeatPos).sqrMagnitude > 0.0025f) c.transform.position = h.SeatPos;
        }
        catch { }
    }

    /// <summary>Walking pace (m/s) at the game's usual walk.</summary>
    private const float WalkSpeed = 1.3f;

    /// <summary>
    /// Ready for their picture: done stepping out of any pose and turned toward who they're facing (within 50°), or
    /// sitting (they only turn their head). False for someone not held.
    /// </summary>
    public static bool ReadyForPicture(Character c)
    {
        if (Get(c) is not { } h) return false;
        if (h.Exiting || h.Returning || h.Walking) return false;
        if (!h.Standing || h.LookAt == null) return true;
        var to = Flat(h.LookAt.position - c.transform.position);
        return to.sqrMagnitude < 0.01f || Vector3.Angle(Flat(c.transform.forward), to) < 50f;
    }
    public static bool WalkFailed(Character c) => Get(c)?.WalkFailed == true;

    // ------------------------------------------------------------------ holding and letting go

    private static Hold Take(Character c, Transform? lookAt, bool animate)
    {
        var h = new Hold { C = c, CharPtr = c.Pointer, LookAt = lookAt, Animate = animate, HoldPos = c.transform.position };
        try
        {
            var ghost = c.Agent?.MyGhost;
            h.Ghost = ghost;
            h.GhostPtr = ghost != null ? ghost.Pointer : IntPtr.Zero;
            try { h.GhostActionPtr = ghost?.CurrentAction != null ? ghost.CurrentAction.Pointer : IntPtr.Zero; } catch { h.GhostActionPtr = IntPtr.Zero; }
            try
            {
                string task = ghost?.CurrentAction?.Type?.name ?? "";
                h.InGameDialogue = task == "AIAction_DoDialogue";
                h.AtWork = task == "AIAction_WorkplaceIdle";
            }
            catch { h.InGameDialogue = h.AtWork = false; }
            if (ghost != null)
            {
                float apart = Flat(ghost.Position - c.transform.position).magnitude;
                if (apart > 0.5f) Log.Debug($"hold: {c.name}'s ghost was {apart:0.0} m away; it stays with them while they're held.");
            }
        }
        catch { h.GhostPtr = IntPtr.Zero; h.Ghost = null; }

        try
        {
            h.Nav = c.Movement != null ? c.Movement.Agent : null;
            if (h.Nav != null && h.Nav.isActiveAndEnabled && h.Nav.isOnNavMesh)
            {
                h.NavWasStopped = h.Nav.isStopped;
                h.Nav.isStopped = true;
                h.Nav.velocity = Vector3.zero;
            }
            else h.Nav = null;
        }
        catch (Exception e) { h.Nav = null; Log.Debug("hold nav: " + e.Message); }

        try { h.Anim = c.Animator; } catch { h.Anim = null; }

        try
        {
            var ik = c.IK;
            var lookIk = ik != null ? ik.lookIK : null;
            h.Look = lookIk != null ? lookIk.solver : null;
            if (h.Look != null)
            {
                h.LookPrevTarget = h.Look.target;
                h.LookPrevWeight = h.Look.GetIKPositionWeight();
                h.LookWeight = h.LookPrevWeight;
            }
        }
        catch (Exception e) { h.Look = null; Log.Debug("hold look: " + e.Message); }

        // Someone walking or busy standing (leaning on a railing, wiping a table) stops to talk: out of a pose the game's own
        // way (its exit animation), then one of its standing idles. Their walk or activity is picked up again on release.
        if (UseIdleAction)
        {
            try
            {
                var cur = c.state != null ? c.state.current : null;
                if (cur != null && ShouldStandIdle(cur))
                {
                    h.OrigPos = c.transform.position;
                    h.OrigRot = c.transform.rotation;
                    // At work too: only the game's exit takes them out of the work animation (0.4.8 skipped it, to keep Banor's
                    // rag in hand, and he wiped the air while he talked). The rag it puts away is given back after the chat
                    // (WorkItemBack, FB-53).
                    var exit = cur.TryCast<MoveAction>() == null ? PoseTransition(cur, exit: true) : null;
                    if (exit != null && exit.Pointer != cur.Pointer)
                    {
                        h.SavedAction = cur;
                        h.Enter = cur.TryCast<IdleAction>() != null ? PoseTransition(cur, exit: false) : null;
                        h.SwitchedToIdle = true;
                        h.Exiting = true;
                        h.PhaseUntil = Time.unscaledTime + Mathf.Clamp(DurationOf(exit), 0.3f, 4f) + 1f;
                        c.SetAction(exit, false);
                        Log.Debug($"pose: {cur.name} -> {exit.name}, then standing to talk");
                    }
                    else
                    {
                        var idle = FindIdle(c);
                        if (idle != null && idle.Pointer != cur.Pointer)
                        {
                            h.SavedAction = cur;
                            c.SetAction(idle, false);
                            h.SwitchedToIdle = true;
                            h.CheckStandAt = Time.unscaledTime + 0.3f; // make sure the body really is standing
                        }
                    }
                }
            }
            catch (Exception e) { Log.Debug("idle switch: " + e.Message); }
            if (h.AtWork && Log.Verbose) Log.Debug($"work: {c.name} stood up from '{h.SavedAction?.name ?? "-"}', holding {Hands(c)}{TaskText(c)}");
        }

        try { h.Standing = (h.SwitchedToIdle && !h.Exiting) || IsPlainIdle(c.state != null ? c.state.current : null); }
        catch { h.Standing = h.SwitchedToIdle && !h.Exiting; }

        if (h.Animate && h.Standing) ListenLayer(h); // someone stepping out of a pose gets it once standing (Tick)
        ByChar[h.CharPtr] = h;
        if (h.GhostPtr != IntPtr.Zero) ByGhost[h.GhostPtr] = h;
        AfterTick(h, c);
        return h;
    }

    /// <summary>The body's own ghost (null if none), as a pointer: it changes if the game hands the body to someone else.</summary>
    private static IntPtr GhostPtrOf(Character c)
    {
        try
        {
            var agent = c.Agent;
            var ghost = agent != null ? agent.MyGhost : null;
            return ghost != null ? ghost.Pointer : IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>The game gave this body to someone else (out of sight, bodies are reused): not ours to hold any more.</summary>
    private static bool Relinked(Hold h, Character c)
    {
        if (h.GhostPtr == IntPtr.Zero) return false;
        var now = GhostPtrOf(c);
        return now != IntPtr.Zero && now != h.GhostPtr;
    }

    /// <summary>End of each frame: remember where the body is, and keep their ghost with it.</summary>
    private static void AfterTick(Hold h, Character c)
    {
        try { h.LastPos = c.transform.position; h.Seen = true; } catch { return; }
        if (h.Nav != null)
        {
            // their navigation agent too, so nothing in the game is left thinking they're somewhere else
            try { if (h.Nav.isActiveAndEnabled && h.Nav.isOnNavMesh && (h.Nav.nextPosition - h.LastPos).sqrMagnitude > 0.09f) h.Nav.nextPosition = h.LastPos; }
            catch { }
        }
        var ghost = h.Ghost;
        if (ghost == null) return;
        try
        {
            if (ghost.WasCollected) { h.Ghost = null; return; }
            var p = h.LastPos;
            if ((ghost.Position - p).sqrMagnitude < 0.09f) return; // within 30 cm: leave it be
            ghost.Position = p;
            ghost.Rotation = c.transform.rotation;
            h.GhostKept = true;
        }
        catch (Exception e) { h.Ghost = null; Log.Debug("hold ghost: " + e.Message); }
    }

    /// <summary>Visible on screen, as the game sees it (for the log).</summary>
    private static string SeenText(Character c)
    {
        try { var a = c.Agent; return a == null ? "?" : a.Visible ? "on screen" : "off screen"; } catch { return "?"; }
    }

    /// <summary>
    /// Let go. Someone taken out of a pose turns back, steps back into it (the game's own entry animation) at the spot they
    /// left, and carries on; if their time there is up, the game moves them on as usual. <paramref name="now"/> skips that.
    /// </summary>
    private static void Drop(Hold h, bool now = false)
    {
        h.WantsPhone = false;
        h.KeepForChat = false;
        if (!now && h.Seat is SeatPhase.Entering or SeatPhase.Seated) { h.ReleaseAfterSeat = true; BeginStand(h); return; } // FB-63: up first
        if (!now && h.Seat == SeatPhase.Exiting) { h.ReleaseAfterSeat = true; return; }
        if (h.Seat == SeatPhase.Walking) { h.Seat = SeatPhase.None; h.Walking = false; }
        if (!now && (h.Phone is PhonePhase.Out or PhonePhase.On) && BeginPhoneAway(h)) return; // the phone goes away first
        if (!now && h.Phone == PhonePhase.Away) { h.ReleaseAfterPhone = true; return; }
        if (h.Returning) { Finish(h, resume: false); return; } // taken again (or gone) while stepping back: leave it there
        if (!now && !h.FreshAfter && CanReturn(h)) { BeginReturn(h); return; } // FB-85: a fresh task has no spot to go back to
        Finish(h, resume: true);
    }

    /// <summary>
    /// FB-53: the game gave them another task while they were held (a waiter's order task ended for a customer just sitting,
    /// a story dialogue started): their old pose is stale, so they aren't put back in it (a waiter stood in it until the
    /// customer left; Banor bent back over the bar before going to clean the tables).
    /// </summary>
    private static bool MovedOn(Hold h)
    {
        if (h.GhostActionPtr == IntPtr.Zero) return false;
        try
        {
            var now = h.C?.Agent?.MyGhost?.CurrentAction;
            return now == null || now.Pointer != h.GhostActionPtr;
        }
        catch { return false; }
    }

    /// <summary>
    /// FB-53: a move between poses, not a pose: the way out of one ("CleanBarExit"), into one ("...Enter"), or a turn
    /// ("TurnSpot", the game's own dialogue turning them to you). Played again, it bends or turns them for nothing.
    /// </summary>
    private static bool ExitPose(CharacterAction? a)
    {
        try
        {
            string n = a?.name ?? "";
            return n.EndsWith("Exit", StringComparison.Ordinal) || n.EndsWith("Enter", StringComparison.Ordinal) || n.StartsWith("Turn", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>FB-53: what release does with them, in the log (so a pose that looks wrong can be told from the log).</summary>
    private static void SayRelease(Hold h, string what)
    {
        try { Log.Debug($"hold: {h.C?.name} let go: {what} (saved '{h.SavedAction?.name ?? "-"}'{(h.InGameDialogue ? ", in the game's dialogue" : "")}{(h.AtWork ? ", at work" : "")})."); } catch { }
    }

    private static bool CanReturn(Hold h)
    {
        if (MovedOn(h) || ExitPose(h.SavedAction) || h.InGameDialogue) return false;
        try
        {
            // At work there's no lead-in: walked back, they ease back into the work (FB-53).
            if (!h.SwitchedToIdle || (h.Enter == null && !h.AtWork) || h.SavedAction?.TryCast<IdleAction>() == null) return false;
            var c = h.C;
            if (c == null || c.WasCollected || !c.isActiveAndEnabled) return false;
            return Flat(c.transform.position - h.OrigPos).magnitude < 1.2f; // walked off somewhere (a residents' chat): no going back
        }
        catch { return false; }
    }

    private static void BeginReturn(Hold h)
    {
        if (h == _chat) _chat = null;
        h.Returning = true;
        h.Entering = false;
        h.Exiting = false;
        h.LookAt = null;
        h.PhaseUntil = Time.unscaledTime + 1.2f;
        RestoreLookAndTalk(h);
    }

    /// <summary>Back to their spot and facing, then into the pose, then their activity.</summary>
    private static void TickReturn(Hold h, Character c, float dt, float now)
    {
        if (!h.Entering)
        {
            var off = Flat(h.OrigPos - c.transform.position);
            if (off.magnitude > 0.02f) c.transform.position += Vector3.ClampMagnitude(off, 0.9f * dt);
            c.transform.rotation = Quaternion.RotateTowards(c.transform.rotation, h.OrigRot, 220f * dt);
            bool there = Quaternion.Angle(c.transform.rotation, h.OrigRot) < 4f && off.magnitude < 0.06f;
            if (!there && now < h.PhaseUntil) return;
            c.transform.rotation = h.OrigRot;
            // At work: back at their spot and facing, then eased back into the work (no lead-in), what they work with given
            // back once they're in it; the game ends the step on its own time (FB-53: bent into the bar as the step ended; not
            // walked back at all, cleaning mid air; handed to the game without the rag).
            if (h.AtWork) { c.transform.position = new Vector3(h.OrigPos.x, c.transform.position.y, h.OrigPos.z); Finish(h, resume: true); return; }
            h.Entering = true;
            h.EndBlocked = false;
            h.PhaseUntil = now + Mathf.Clamp(DurationOf(h.Enter), 0.3f, 4f) + 1f;
            try { c.SetAction(h.Enter, false); }
            catch (Exception e) { Log.Debug("pose enter: " + e.Message); Finish(h, resume: true); }
            return;
        }
        if (h.EndBlocked || now >= h.PhaseUntil) Finish(h, resume: true);
    }

    /// <summary>Everyone's routine and animation back as it was (or moved on, if the activity was walked away from).</summary>
    private static void Finish(Hold h, bool resume)
    {
        ByChar.Remove(h.CharPtr);
        if (h.GhostPtr != IntPtr.Zero) ByGhost.Remove(h.GhostPtr);
        if (h == _chat) _chat = null;
        var c = h.C;
        if (c == null) return;
        LastReleased = c;
        bool alive;
        try { alive = !c.WasCollected && c.isActiveAndEnabled; } catch { alive = false; }
        if (h.Phone != PhonePhase.None)
        {
            // Let go before the phone was put away (they were gone, or you started talking to them): no phone left in hand.
            h.Phone = PhonePhase.None;
            try { if (alive) c.RemoveItem(); else PhoneLeftOut.Add((c, Time.unscaledTime + 120f)); } catch (Exception e) { Log.Debug("phone away: " + e.Message); }
        }
        if ((h.Walking || h.Following || h.FollowParam > 0.01f) && alive && h.Anim != null) // review F4: a follower let go mid-run too
        {
            // let go mid-walk: stop the walk animation so they don't keep walking on the spot
            try { h.Anim.SetFloat(CharacterMovement.speedId, 0f); h.Anim.SetFloat(CharacterMovement.turnId, 0f); } catch { }
            h.Walking = false;
        }
        IdleAction? work = null;
        if (resume && alive && h.FreshAfter)
        {
            // FB-85: their ghost has stood frozen on one task all this while (the walk they were on when they came along):
            // it's interrupted now they're let go (ByGhost no longer has them, so the game's own pick goes through), and the
            // body ends the old step: the game gives them something fitting the hour.
            SayRelease(h, "on to a fresh task (the game picks it)");
            try { c.EndAction(); } catch (Exception e) { Log.Debug("fresh task, body: " + e.Message); }
            // Review F85-3: only their own ghost (a body the game gave someone else is left to them).
            try { var g = c.Agent?.MyGhost; var act = g?.CurrentAction; if (g != null && act != null && (h.GhostPtr == IntPtr.Zero || g.Pointer == h.GhostPtr)) act.Interrupt(g); }
            catch (Exception e) { Log.Debug("fresh task, ghost: " + e.Message); }
        }
        else if (resume && alive && h.AtWork && h.Returning && !MovedOn(h) && !ExitPose(h.SavedAction) && !h.InGameDialogue
            && (work = h.SavedAction?.TryCast<IdleAction>()) != null)
        {
            // Walked back to their spot at work: eased back into the work (not snapped, no lead-in), what they work with put
            // back in hand once they're in it (WorkItemBack); the game ends the step on its own time (FB-53).
            SayRelease(h, "eased back into their work at their spot");
            try { c.SetAction(work, false); }
            catch (Exception e) { Log.Debug("resume work: " + e.Message); work = null; try { c.EndAction(); } catch { } }
        }
        else if (resume && alive && (MovedOn(h) || ExitPose(h.SavedAction) || h.InGameDialogue || h.AtWork))
        {
            // The game moved them on meanwhile, what was saved isn't a pose, or they were at work and walked off: on with their
            // task, not snapped back into a pose (FB-53: snapped into his bar-cleaning pose as his work step ended, Banor bent
            // and stood straight up).
            SayRelease(h, MovedOn(h) ? "into their new task (the game moved them on)"
                          : h.AtWork ? "back to their work, the game's own way" : "on with their task (not a pose to go back to)");
            try { c.EndAction(); } catch (Exception e) { Log.Debug("move on: " + e.Message); }
            if (h.AtWork && !MovedOn(h) && !h.InGameDialogue && !ExitPose(h.SavedAction))
                work = h.SavedAction?.TryCast<IdleAction>(); // back in the same work later: the rag too
        }
        else if (!resume) { }
        else if (h.Returning && h.SavedAction != null)
        {
            // Back in their pose at their spot: the activity carries on (snapped exactly into place).
            SayRelease(h, "back into their pose at their spot");
            try { if (alive) c.SetAction(h.SavedAction, true); }
            catch (Exception e) { Log.Debug("resume pose: " + e.Message); try { c.CancelAction(); } catch { } }
        }
        else if (h.SwitchedToIdle && h.SavedAction != null && h.SavedAction.TryCast<IdleAction>() != null && h.Enter != null
                 && alive && Flat(c.transform.position - h.OrigPos).magnitude >= 1.2f)
        {
            // They walked away from where they were leaning or working: that's over, on with their day.
            try { c.EndAction(); } catch (Exception e) { Log.Debug("move on: " + e.Message); }
        }
        else if (h.SwitchedToIdle && h.SavedAction != null)
        {
            // Pick up where they left off: re-enter the activity they were doing (the task step it belongs to is untouched).
            SayRelease(h, "back into what they were doing");
            try { if (alive) c.SetAction(h.SavedAction, false); }
            catch (Exception e) { Log.Debug("resume action: " + e.Message); try { c.CancelAction(); } catch { } }
        }
        else if (h.EndBlocked)
        {
            // Their step finished while held (e.g. sitting down); move on to the next one now.
            try { if (alive) c.EndAction(); }
            catch (Exception e) { Log.Debug("continue task: " + e.Message); }
        }
        try { if (h.Nav != null && h.Nav.isActiveAndEnabled && h.Nav.isOnNavMesh) h.Nav.isStopped = h.NavWasStopped; } catch { }
        if (!h.Returning) RestoreLookAndTalk(h);
        if (alive && (Log.Verbose || work != null))
        {
            try
            {
                float at = Time.unscaledTime;
                JustReleased.Add(new Watch
                {
                    C = c, Last = c.transform.position, Until = at + (h.AtWork ? 8f : 4f), Jumps = Log.Verbose, Trace = Log.Verbose && h.AtWork,
                    Work = work, WorkCheckAt = at + Mathf.Clamp(work != null ? work.blendDuration : 0f, 0.3f, 2f) + 0.5f,
                });
            }
            catch { }
        }
    }

    /// <summary>
    /// FB-53: back in their work pose, what they work with is in hand (the author: Banor went back to cleaning without his
    /// rag). If it isn't, the game's own snap into the pose they're already in puts it there (IdleAction.snapItem).
    /// </summary>
    /// <returns>False while they aren't (yet) in that work: checked again, until the watch ends.</returns>
    private static bool WorkItemBack(Character c, IdleAction work)
    {
        try
        {
            var item = work.snapItem;
            if (item == null) return true;
            var cur = c.state != null ? c.state.current : null;
            if (cur == null || cur.Pointer != work.Pointer) return false; // not in it: elsewhere, the game's routine sees to their things
            var ia = c.Interaction;
            if (ia == null) return true;
            bool Holds(HumanBodyBones b) { var t = ia.GetTarget(b); return t != null && (b == item.bone || (t.Data != null && t.Data.Pointer == item.Pointer)); }
            if (Holds(item.bone) || Holds(HumanBodyBones.LeftHand) || Holds(HumanBodyBones.RightHand)) return true; // in hand (review: either hand)
            c.SetAction(work, true);
            Log.Debug($"hold: {c.name}'s {item.name} put back in hand (the game's snap into '{work.name}'), now holding {Hands(c)}.");
        }
        catch (Exception e) { Log.Debug("work item: " + e.Message); }
        return true;
    }

    /// <summary>What's in their hands (the debug log, FB-53).</summary>
    private static string Hands(Character c)
    {
        try
        {
            var ia = c.Interaction;
            if (ia == null) return "?";
            static string Name(CharacterTarget? t) => t == null ? "nothing" : (t.Data != null ? t.Data.name : t.name);
            return $"left {Name(ia.GetTarget(HumanBodyBones.LeftHand))}, right {Name(ia.GetTarget(HumanBodyBones.RightHand))}";
        }
        catch { return "?"; }
    }

    /// <summary>The game's task for them and its step (the debug log, FB-53).</summary>
    private static string TaskText(Character c)
    {
        try
        {
            var t = c.currentTask;
            var task = t?.task;
            if (task == null) return "";
            static string Name(CharacterAction? a) => a != null ? a.name : "-";
            var acts = task.actions;
            string list = acts == null ? "-" : string.Join(", ", Enumerable.Range(0, acts.Length).Select(i => Name(acts[i])));
            return $"; task '{task.name}': {Name(task.enterAction)} > [{list}] > {Name(task.exitAction)} ({task.type}), step {t!.progress}{(t.isExit ? ", exiting" : "")}";
        }
        catch { return ""; }
    }

    /// <summary>FB-51: the phone put away for someone let go while out of reach, once they're back.</summary>
    private static void TidyPhones()
    {
        if (PhoneLeftOut.Count == 0) return;
        float now = Time.unscaledTime;
        for (int i = PhoneLeftOut.Count - 1; i >= 0; i--)
        {
            var (c, until) = PhoneLeftOut[i];
            try
            {
                if (c == null || c.WasCollected || now > until || IsHeld(c)) { PhoneLeftOut.RemoveAt(i); continue; }
                if (!c.isActiveAndEnabled) continue;
                PhoneLeftOut.RemoveAt(i);
                // Only while they're still in the phone idle: by now they may be carrying something else (a tray, their food).
                var idle = PhoneIdle();
                var current = c.state?.current;
                if (idle == null || current == null || current.Pointer != idle.Pointer) continue;
                try { c.RemoveItem(); } catch { }
                c.EndAction(); // still on the phone: on with their day
                Log.Debug($"hold: {c.name}'s phone put away (let go while out of reach).");
            }
            catch { PhoneLeftOut.RemoveAt(i); }
        }
    }

    private static void WatchReleased()
    {
        if (JustReleased.Count == 0) return;
        float now = Time.unscaledTime;
        for (int i = JustReleased.Count - 1; i >= 0; i--)
        {
            var w = JustReleased[i];
            try
            {
                if (now > w.Until || w.C == null || w.C.WasCollected || !w.C.isActiveAndEnabled || IsHeld(w.C)) { JustReleased.RemoveAt(i); continue; }
                if (w.Trace)
                {
                    var cur = w.C.state != null ? w.C.state.current : null;
                    var ptr = cur != null ? cur.Pointer : IntPtr.Zero;
                    if (ptr != w.LastAction)
                    {
                        w.LastAction = ptr;
                        Log.Debug($"after release: {w.C.name} now in '{(cur != null ? cur.name : "-")}', holding {Hands(w.C)}{TaskText(w.C)}");
                    }
                }
                if (w.Work != null && now >= w.WorkCheckAt && WorkItemBack(w.C, w.Work)) w.Work = null;
                if (w.Jumps)
                {
                    var p = w.C.transform.position;
                    float moved = Flat(p - w.Last).magnitude;
                    w.Last = p;
                    if (moved > JumpMetres)
                    {
                        Log.Debug($"after release: the game moved {w.C.name} {moved:0.0} m in one frame ({SeenText(w.C)}).");
                        w.Jumps = false;
                    }
                }
                if (!w.Jumps && !w.Trace && w.Work == null) JustReleased.RemoveAt(i);
            }
            catch { JustReleased.RemoveAt(i); }
        }
    }

    private static void RestoreLookAndTalk(Hold h)
    {
        var c = h.C;
        bool alive;
        try { alive = c != null && !c.WasCollected && c.isActiveAndEnabled; } catch { alive = false; }
        try
        {
            if (h.Look != null)
            {
                h.Look.target = h.LookPrevTarget;
                h.Look.SetIKPositionWeight(h.LookPrevWeight);
            }
        }
        catch { }
        if (h.Animate && alive)
        {
            if (h.LayersSet)
            {
                try { c!.ChangeTalkWeight(Character.AnimatorTalkType.Idle, 0.4f); } catch { }
                try { c!.ChangeEmotionWeight(Character.AnimatorMoodType.Neutral, 0.6f); } catch { }
            }
            try { c!.Communication?.SalsaText?.Stop(); } catch { }
            MouthAtOwnPace(c!);
            ClearTrigger(h);
        }
    }

    /// <summary>The game's listening layer, for someone standing (see <see cref="Hold.LayersSet"/>).</summary>
    private static void ListenLayer(Hold h)
    {
        try { h.C.ChangeTalkWeight(Character.AnimatorTalkType.Listen, 0.4f); h.LayersSet = true; }
        catch (Exception e) { Log.Debug("listen: " + e.Message); }
    }

    // ------------------------------------------------------------------ poses: the game's own way in and out

    private static Dictionary<string, TransitionAction>? _transitions;
    private static float _transitionsAt = -999f;
    private static int _transitionsScene = -1;

    private static int _sceneHandle, _sceneEpoch;
    private static float _sceneCheckAt = -999f;

    /// <summary>
    /// Goes up whenever the game moves to another scene (checked at most once a second): assets found, or found missing,
    /// before then may not hold (D7, PERF-10). Lookups that remember an answer are asked again in a new scene.
    /// </summary>
    /// <summary>FB-62: the scene count (see <see cref="SceneEpoch"/>), for followers.</summary>
    internal static int SceneEpochNow() => SceneEpoch();

    private static int SceneEpoch()
    {
        if (Time.unscaledTime < _sceneCheckAt) return _sceneEpoch;
        _sceneCheckAt = Time.unscaledTime + 1f;
        try
        {
            int handle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (handle != _sceneHandle) { _sceneHandle = handle; _sceneEpoch++; }
        }
        catch { }
        return _sceneEpoch;
    }

    /// <summary>How long "the game has none" is believed before looking again (within one scene).</summary>
    private const float MissingFor = 60f;
    private static readonly int MovementState = Animator.StringToHash("Movement"); // standing/walking blend: speed 0 = idle

    /// <summary>
    /// The game's entry or exit animation for a pose, by its name: LeanIdle → LeanExit / LeanEnter, CleanTable →
    /// CleanTableExit, CookerLoop → CookerExit, IdleCrossedArms → IdleCrossedArmsExit. Null when it has none.
    /// </summary>
    internal static TransitionAction? PoseTransition(CharacterAction cur, bool exit)
    {
        try
        {
            string n = (cur.name ?? "").Replace("(Clone)", "").Trim();
            foreach (var suffix in new[] { "Enter", "Exit", "Idle", "Loop" })
                if (n.Length > suffix.Length && n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { n = n[..^suffix.Length]; break; }
            if (n.Length == 0) return null;
            string want = n + (exit ? "Exit" : "Enter");
            int scene = SceneEpoch();
            for (int pass = 0; pass < 2; pass++)
            {
                // Built again in a new scene, or (after a miss) once a minute at most.
                if (_transitions == null || _transitionsScene != scene || (pass == 1 && Time.unscaledTime - _transitionsAt > MissingFor))
                {
                    _transitionsScene = scene;
                    _transitions = new Dictionary<string, TransitionAction>(StringComparer.OrdinalIgnoreCase);
                    foreach (var t in Resources.FindObjectsOfTypeAll<TransitionAction>())
                        if (t != null && !string.IsNullOrEmpty(t.name)) _transitions[t.name.Replace("(Clone)", "").Trim()] = t;
                    _transitionsAt = Time.unscaledTime;
                }
                if (_transitions.TryGetValue(want, out var found))
                {
                    if (found != null && !found.WasCollected) return found;
                    _transitions = null; // a cached one was destroyed (a scene unloaded): built again now, not in a minute (6c review)
                }
            }
        }
        catch (Exception e) { Log.Debug("pose transition: " + e.Message); }
        return null;
    }

    private static float DurationOf(TransitionAction? t)
    {
        try { return t != null ? t.duration : 1f; } catch { return 1f; }
    }

    /// <summary>A pose with no way out the game's way: if the body's still in it, blend it to standing.</summary>
    private static void EnsureStanding(Hold h)
    {
        try
        {
            if (h.Anim == null || !h.Anim.HasState(0, MovementState)) return;
            if (h.Anim.GetCurrentAnimatorStateInfo(0).shortNameHash == MovementState) return;
            h.Anim.CrossFadeInFixedTime(MovementState, 0.35f, 0);
            Log.Debug($"pose: {h.C.name} blended to standing");
        }
        catch (Exception e) { Log.Debug("ensure standing: " + e.Message); }
    }

    /// <summary>Lets everyone go (the mod is shutting down).</summary>
    public static void ReleaseAll()
    {
        foreach (var h in ByChar.Values.ToList())
        {
            if (h.Returning) Finish(h, resume: true); // straight back into their pose
            else Drop(h, now: true);
        }
    }

    private static void Speak(Hold h, string text, float spokenSeconds, string cued = "")
    {
        if (!h.Animate) return;
        var c = h.C;
        float seconds = spokenSeconds > 0.3f ? spokenSeconds : Mathf.Clamp(text.Length / 14f, 1.2f, 14f);
        h.TalkUntil = Time.unscaledTime + seconds;
        if (h.Phone != PhonePhase.None)
        {
            // On the phone: their mouth moves; the phone animation keeps their arm and stance (no hand gestures).
            Mouth(c, text, spokenSeconds);
            return;
        }
        if (!h.Standing)
        {
            // Sitting (or in a pose with a prop): their mouth moves and their head turns; the body stays as it was (CF-03).
            Mouth(c, text, spokenSeconds);
            return;
        }
        if (!h.LayersSet) ListenLayer(h);
        h.Talking = true;
        try { c.ChangeTalkWeight(Character.AnimatorTalkType.Talk, 0.25f); h.LayersSet = true; } catch (Exception e) { Log.Debug("talk: " + e.Message); }
        // A gesture for what they're saying: the one the AI cued with the line (CF-04: a one-off like a nod or a wave, or a way
        // of talking like laughing or arguing), else one read from the words; back to their mood's afterwards.
        var gesture = Gesture(text);
        if (gesture == Character.AnimatorMoodType.Explain && h.MoodGesture != Character.AnimatorMoodType.Neutral && Gestures.NextDouble() < 0.5) gesture = h.MoodGesture;
        string name = Npc.GestureLibrary.Normalize(cued);
        if (Npc.GestureLibrary.Mood(name) is { } mood && Enum.TryParse<Character.AnimatorMoodType>(mood, out var cuedMood)) gesture = cuedMood;
        if (Npc.GestureLibrary.Trigger(name) is { } trigger && Trigger(c, trigger)) h.LastTrigger = trigger;
        try { c.ChangeEmotionWeight(gesture, 0.4f); } catch { }
        h.GestureUntil = h.TalkUntil;
        Mouth(c, text, spokenSeconds);
    }

    /// <summary>
    /// Their mouth moves with the line (the game's own text lip-sync, SalsaTextSync), for as long as the voice plays
    /// (CF-04: "mouths should always move when audio is being played"): its words-per-minute is set from the line's words
    /// and the voice's length, so the mouth starts and stops with it. Without a voice, the game's own pace.
    /// </summary>
    public static void Mouth(Character c, string text, float voiceSeconds)
    {
        try
        {
            var salsa = c.Communication != null ? c.Communication.SalsaText : null;
            if (salsa == null) return;
            IntPtr key = salsa.Pointer;
            if (voiceSeconds > 0.3f)
            {
                if (!OwnPace.ContainsKey(key)) OwnPace[key] = salsa.wordsPerMinute;
                int words = Math.Max(1, (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
                salsa.wordsPerMinute = Mathf.Clamp(words * 60f / voiceSeconds, 20f, 600f);
            }
            else if (OwnPace.Remove(key, out float own)) salsa.wordsPerMinute = own;
            salsa.Say(text);
        }
        catch (Exception e) { Log.Debug("salsa: " + e.Message); }
    }

    /// <summary>Each lip-sync's own pace, before a line in a voice changed it: put back when they're let go (the game's own lines keep theirs).</summary>
    private static readonly Dictionary<IntPtr, float> OwnPace = new();

    /// <summary>A one-off gesture by the game's own animator trigger, if this body's animator has it (standing only).</summary>
    private static bool Trigger(Character c, string trigger)
    {
        try
        {
            var anim = c.Animator;
            if (anim == null) return false;
            foreach (var p in anim.parameters)
                if (p.type == AnimatorControllerParameterType.Trigger && p.name == trigger) { anim.SetTrigger(trigger); return true; }
            Log.Debug($"gesture: no '{trigger}' trigger on {c.name}'s animator");
        }
        catch (Exception e) { Log.Debug("gesture: " + e.Message); }
        return false;
    }

    /// <summary>A trigger set for a line and not used yet is cleared (it would otherwise fire later, after release).</summary>
    private static void ClearTrigger(Hold h)
    {
        if (h.LastTrigger.Length == 0) return;
        try { h.C.Animator?.ResetTrigger(h.LastTrigger); } catch { }
        h.LastTrigger = "";
    }

    /// <summary>Their lip-sync back at the game's own pace.</summary>
    public static void MouthAtOwnPace(Character c)
    {
        try
        {
            var salsa = c.Communication != null ? c.Communication.SalsaText : null;
            if (salsa != null && OwnPace.Remove(salsa.Pointer, out float own)) salsa.wordsPerMinute = own;
        }
        catch { }
    }

    private static void SetMood(Hold h, string mood)
    {
        if (!h.Animate) return;
        h.MoodGesture = MoodAnimation(mood);
        if (h.Phone != PhonePhase.None) return; // the phone pose stays as it is
        if (!h.Standing) return; // sitting: no body layers (CF-03); the mood shows in their words and voice
        try { h.C.ChangeEmotionWeight(h.MoodGesture, 0.8f); h.LayersSet = true; } catch (Exception e) { Log.Debug("mood anim: " + e.Message); }
    }

    /// <summary>
    /// Standing activities a person would simply pause to talk (cleaning, cooking, serving, sweeping, walking...).
    /// Sitting/lying poses and activities with a hand prop (umbrella, phone, cigarette, delivery box, fishing rod)
    /// are kept: snapping those to a standing idle looks broken.
    /// </summary>
    private static readonly HashSet<Character.AnimatorStateType> BusyStanding = new()
    {
        Character.AnimatorStateType.Staff, Character.AnimatorStateType.Bar, Character.AnimatorStateType.Waiter,
        Character.AnimatorStateType.CutFood, Character.AnimatorStateType.CleanBar, Character.AnimatorStateType.CleanTable,
        Character.AnimatorStateType.CleanGlasses, Character.AnimatorStateType.Repair, Character.AnimatorStateType.Shop,
        Character.AnimatorStateType.Gardening, Character.AnimatorStateType.Work, Character.AnimatorStateType.Sweep,
        Character.AnimatorStateType.Cooker, Character.AnimatorStateType.Pan, Character.AnimatorStateType.Fryer,
        Character.AnimatorStateType.Blender, Character.AnimatorStateType.Security, Character.AnimatorStateType.Dance,
        Character.AnimatorStateType.Mirror, Character.AnimatorStateType.Beg, Character.AnimatorStateType.LookDown,
    };

    /// <summary>Seats, beds, vehicles and hand props: those poses are kept (and their transitions allowed to finish).</summary>
    private static readonly string[] KeepPoseWords =
        { "sit", "bench", "chair", "stool", "sleep", "bed", "lie", "lying", "sunbed", "hova", "boat", "sail", "ground", "pier", "eat", "umbrella", "phone", "smoke", "fish", "delivery", "dead" };

    private static bool ShouldStandIdle(CharacterAction cur)
    {
        if (cur.TryCast<MoveAction>() != null || cur.TryCast<TurnAction>() != null || cur.TryCast<WanderSpotAction>() != null) return true;
        if (cur.TryCast<TransitionAction>() != null)
        {
            string n = (cur.name ?? "").ToLowerInvariant();
            return !KeepPoseWords.Any(w => n.Contains(w)); // e.g. LeanWallEnter → stand idle; SitChairEnter → let it finish
        }
        var ia = cur.TryCast<IdleAction>();
        if (ia == null) return false;
        return ia.dance || BusyStanding.Contains(ia.Animation) || ia.Animation is Character.AnimatorStateType.Lean or Character.AnimatorStateType.Wall or Character.AnimatorStateType.Arms;
    }

    private static bool IsPlainIdle(CharacterAction? cur)
    {
        var ia = cur?.TryCast<IdleAction>();
        return ia != null && ia.Animation == Character.AnimatorStateType.Idle && !ia.dance;
    }

    internal static bool UseIdleAction = true;
    internal static Character? LastReleased;

    internal static string Describe(Character c)
    {
        try
        {
            var a = c.Animator;
            var cur = c.state != null ? c.state.current : null;
            return $"action={(cur != null ? cur.name : "-")} speed={a.GetFloat(CharacterMovement.speedId):0.00} pos={c.transform.position}";
        }
        catch (Exception e) { return e.Message; }
    }

    private static IdleAction? _idleAsset;
    internal static string IdleName => _idleAsset != null ? _idleAsset.name : "-";

    /// <summary>A plain standing idle: the character's own default if it is one, else the first suitable IdleAction asset.</summary>
    private static IdleAction? FindIdle(Character c)
    {
        static bool Plain(IdleAction? a) =>
            a != null && a.Animation == Character.AnimatorStateType.Idle && !a.dance && a.snapItem == null;
        try
        {
            var own = c.DefaultAction != null ? c.DefaultAction.TryCast<IdleAction>() : null;
            if (Plain(own)) return own;
        }
        catch { }
        if (_idleAsset != null && !_idleAsset.WasCollected) return _idleAsset;
        // None found lately in this scene: not every asset scanned again for each person held (D7, PERF-10).
        if (Time.unscaledTime < _noIdleUntil && _noIdleScene == SceneEpoch()) return null;
        IdleAction? best = null;
        foreach (var a in Resources.FindObjectsOfTypeAll<IdleAction>())
        {
            if (!Plain(a)) continue;
            bool noBark = a.dialogue == null;
            if (best == null || (noBark && best.dialogue != null) || (noBark && (a.name ?? "").Contains("Idle", StringComparison.OrdinalIgnoreCase)))
                best = a;
        }
        _idleAsset = best;
        if (best != null) Log.Debug("Standing idle for chats: " + best.name);
        else { _noIdleUntil = Time.unscaledTime + MissingFor; _noIdleScene = SceneEpoch(); }
        return best;
    }

    private static float _noIdleUntil = -999f;
    private static int _noIdleScene = -1;

    public static Character.AnimatorMoodType MoodAnimation(string mood) => mood switch
    {
        "sad" or "melancholy" or "lonely" or "nostalgic" => Character.AnimatorMoodType.Sad,
        "tipsy" => Character.AnimatorMoodType.Drunk,
        "angry" or "irritated" or "grumpy" => Character.AnimatorMoodType.Argue,
        "amused" or "playful" => Character.AnimatorMoodType.Laugh,
        "bored" or "tired" or "hungover" => Character.AnimatorMoodType.Bored,
        "curious" or "cheerful" or "hopeful" or "excited" => Character.AnimatorMoodType.Interested,
        "flirty" => Character.AnimatorMoodType.Flirting,
        "paranoid" or "anxious" or "nervous" => Character.AnimatorMoodType.Shocked,
        "stressed" or "in a hurry" => Character.AnimatorMoodType.Explain,
        _ => Character.AnimatorMoodType.Neutral,
    };

    // ------------------------------------------------------------------ strategy switches + diagnostics (dev "holdmode")

    internal static bool SkipDoUpdate;            // freezes the animator entirely (statue)
    internal static bool SkipSetTask = true;      // stops the character's task/action machine
    internal static bool IdleParamsInDoUpdate = true;
    internal static bool PinPosition = true;
    internal static int DbgDoUpdate, DbgSetTask, DbgAi;
    internal static float DbgDrift;               // furthest the person you're talking to has swayed from their spot (metres)

    internal static string Diagnostics()
    {
        var h = _chat;
        if (h == null) return "no held NPC";
        var c = h.C;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"flags skipDoUpdate={SkipDoUpdate} skipSetTask={SkipSetTask} idleParams={IdleParamsInDoUpdate} pin={PinPosition} idleSwitch={UseIdleAction} switched={h.SwitchedToIdle} standing={h.Standing} endBlocked={h.EndBlocked} idle={IdleName} saved={(h.SavedAction != null ? h.SavedAction.name : "-")} held={ByChar.Count}");
        sb.AppendLine($"calls doUpdate={DbgDoUpdate} onAnimator={DbgOnAnimator} setTask={DbgSetTask} ai={DbgAi} drift={DbgDrift:0.00}m type={c.Type}");
        try
        {
            var a = c.Animator;
            var st = a.GetCurrentAnimatorStateInfo(0);
            sb.AppendLine($"anim enabled={a.enabled} rootMotion={a.applyRootMotion} speed={a.GetFloat(CharacterMovement.speedId):0.00} turn={a.GetFloat(CharacterMovement.turnId):0.00} state={st.shortNameHash} t={st.normalizedTime:0.00} cull={a.cullingMode} mode={a.updateMode}");
            // FB-53: every layer, to tell which one plays a work animation on (Banor wiped the air while he talked).
            var layers = new List<string>();
            for (int i = 0; i < a.layerCount; i++) layers.Add($"{a.GetLayerName(i)} w={a.GetLayerWeight(i):0.00} state={a.GetCurrentAnimatorStateInfo(i).shortNameHash}");
            sb.AppendLine("layers " + string.Join("; ", layers));
        }
        catch (Exception e) { sb.AppendLine("anim: " + e.Message); }
        try
        {
            var n = c.Movement?.Agent;
            if (n != null) sb.AppendLine($"nav enabled={n.enabled} onMesh={n.isOnNavMesh} stopped={n.isStopped} updPos={n.updatePosition} updRot={n.updateRotation} hasPath={n.hasPath} vel={n.velocity.magnitude:0.00}");
        }
        catch (Exception e) { sb.AppendLine("nav: " + e.Message); }
        try { sb.AppendLine($"task={(c.currentTask != null ? c.currentTask.GetType().Name : "-")} action={(c.state != null && c.state.current != null ? c.state.current.GetIl2CppType().Name + "/" + c.state.current.name : "-")}"); }
        catch (Exception e) { sb.AppendLine("task: " + e.Message); }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ every frame, after animation

    /// <summary>Keeps everyone held in place, turned toward whoever they're facing, eyes on them; walks walkers.</summary>
    public static void LateTick()
    {
        WatchReleased();
        TidyPhones();
        if (ByChar.Count == 0) return;
        float dt = Time.unscaledDeltaTime;
        foreach (var h in ByChar.Values.ToList())
        {
            try
            {
                var c = h.C;
                if (c == null || c.WasCollected || !c.isActiveAndEnabled) { Drop(h, now: true); continue; }
                if (Relinked(h, c))
                {
                    Log.Debug($"hold: {c.name}'s body now belongs to someone else in the game; let go.");
                    Finish(h, resume: false);
                    continue;
                }
                if (!h.Light) PutBackIfMoved(h, c); // a light hold is never moved back (the game moves them as it likes)
                Tick(h, c, dt);
                if (ByChar.TryGetValue(h.CharPtr, out var still) && still == h) AfterTick(h, c);
            }
            catch (Exception e) { Log.Debug("hold tick: " + e.Message); }
        }
    }

    /// <summary>
    /// Moved by the game since last frame by something the fences didn't catch: put back where we held them, every time
    /// (the fences around the game's updates catch the usual culprits; this is the last line).
    /// </summary>
    private static void PutBackIfMoved(Hold h, Character c)
    {
        if (!h.Seen || h.Entering) return;
        var pos = c.transform.position;
        float jump = Flat(pos - h.LastPos).magnitude;
        if (jump <= JumpMetres) return;
        h.Jumps++;
        c.transform.position = h.LastPos;
        if (h.Jumps == 1 || h.Jumps % 600 == 0)
            Log.Debug($"hold: the game moved {c.name} {jump:0.0} m ({SeenText(c)}); put back ({h.Jumps}x). That spot is {Whereabouts(c, pos)}.");
        AfterTick(h, c);
    }

    private static Vector3 Flat(Vector3 v) => new(v.x, 0, v.z);

    private static void Tick(Hold h, Character c, float dt)
    {
        float now = Time.unscaledTime;
        if (h.Returning) { TickReturn(h, c, dt, now); return; }
        if (h.Exiting && (h.EndBlocked || now >= h.PhaseUntil))
        {
            // Out of the pose: now a plain standing idle, facing whoever they're talking to.
            h.Exiting = false;
            h.EndBlocked = false;
            try { var idle = FindIdle(c); if (idle != null) c.SetAction(idle, false); } catch (Exception e) { Log.Debug("after exit: " + e.Message); }
            h.Standing = true;
            if (h.Animate && !h.LayersSet) ListenLayer(h);
            h.HoldPos = c.transform.position;
            h.CheckStandAt = now + 0.3f;
        }
        if (h.CheckStandAt > 0 && now >= h.CheckStandAt) { h.CheckStandAt = -1f; EnsureStanding(h); }
        if (h.Phone != PhonePhase.None || h.WantsPhone) { if (TickPhone(h, c, now)) return; }
        if (h.Seat != SeatPhase.None && TickSeat(h, c, now)) return; // FB-63: the seat has them
        if (h.Following) TickFollow(h, c, dt);
        else if (h.Walking) TickWalk(h, c, dt);
        else if (PinPosition && h.Standing)
        {
            // A soft leash: free to sway inside a small band (their idle stays alive), eased back beyond it, never snapped.
            var p = c.transform.position;
            var off = Flat(p - h.HoldPos);
            float d = off.magnitude;
            if (h == _chat && d > DbgDrift) DbgDrift = d;
            float keep = Leash.Pull(d, dt);
            if (keep < d - 0.0001f)
            {
                var spot = h.HoldPos + off * (keep / d);
                c.transform.position = new Vector3(spot.x, p.y, spot.z);
            }
        }
        if (h.Nav != null)
        {
            try { if (h.Nav.isActiveAndEnabled && h.Nav.isOnNavMesh) { h.Nav.isStopped = true; h.Nav.velocity = Vector3.zero; } } catch { }
        }
        if (h.Anim != null && !h.Walking && !h.Following && !h.Light) // a light hold leaves their walking to the game
        {
            try
            {
                h.Anim.SetFloat(CharacterMovement.speedId, Mathf.MoveTowards(h.Anim.GetFloat(CharacterMovement.speedId), 0f, dt * 3f));
                h.Anim.SetFloat(CharacterMovement.turnId, Mathf.MoveTowards(h.Anim.GetFloat(CharacterMovement.turnId), 0f, dt * 3f));
            }
            catch { }
        }
        if (h.GlanceUntil > 0 && Time.unscaledTime >= h.GlanceUntil)
        {
            // The glance is over: back to what they were looking at, or (nothing before) the head eases back.
            h.GlanceUntil = 0;
            h.LookAt = h.GlanceBack;
            h.GlanceBack = null;
            h.Unglancing = h.LookAt == null;
        }
        if (h.Unglancing && h.Look != null)
        {
            try
            {
                h.LookWeight = Mathf.MoveTowards(h.LookWeight, h.LookPrevWeight, dt * 1.5f);
                h.Look.SetLookAtWeight(h.LookWeight, 0f);
                if (Mathf.Approximately(h.LookWeight, h.LookPrevWeight)) { h.Look.target = h.LookPrevTarget; h.Unglancing = false; }
            }
            catch { h.Unglancing = false; }
        }
        bool glancing = h.GlanceUntil > 0;
        if (h.LookAt != null)
        {
            Vector3 to = Flat(h.LookAt.position - c.transform.position);
            if (h.Standing && !h.Walking && !glancing && to.sqrMagnitude > 0.01f) // seated people only turn their head; a glance is the head alone
            {
                // The body turns only when they're well off to one side, smoothly; small moves are left to the head.
                var want = Quaternion.LookRotation(to.normalized, Vector3.up);
                float degreesOff = Quaternion.Angle(c.transform.rotation, want);
                h.Turning = h.Turning ? degreesOff > Leash.TurnStop : degreesOff > h.TurnStart;
                if (h.Turning) c.transform.rotation = Quaternion.RotateTowards(c.transform.rotation, want, Leash.TurnSpeed(degreesOff) * dt);
            }
            if (h.Look != null)
            {
                try
                {
                    h.Look.target = h.LookAt;
                    h.LookWeight = Mathf.MoveTowards(h.LookWeight, 1f, dt * 1.5f);
                    h.Look.SetLookAtWeight(h.LookWeight, h.Standing && !glancing ? 0.25f : 0f); // seated or glancing: the head turns, the spine doesn't
                }
                catch { }
            }
        }
        if (h.Talking && Time.unscaledTime > h.TalkUntil)
        {
            h.Talking = false;
            try { c.ChangeTalkWeight(Character.AnimatorTalkType.Listen, 0.4f); } catch { }
        }
        if (h.GestureUntil > 0 && Time.unscaledTime > h.GestureUntil)
        {
            h.GestureUntil = 0f;
            ClearTrigger(h);
            try { c.ChangeEmotionWeight(h.MoodGesture, 0.6f); } catch { }
        }
    }

    // ------------------------------------------------------------------ on the phone

    private static IdleAction? _phoneIdle;
    private static float _noPhoneUntil = -999f;
    private static int _noPhoneScene = -1;

    /// <summary>The game's own "on the phone" idle (Phone_Loop), found once. None: looked for again a minute later, or in a new scene (D7).</summary>
    private static IdleAction? PhoneIdle()
    {
        if (_phoneIdle != null && !_phoneIdle.WasCollected) return _phoneIdle;
        if (Time.unscaledTime < _noPhoneUntil && _noPhoneScene == SceneEpoch()) return null;
        _phoneIdle = null; // a destroyed one isn't kept
        try
        {
            foreach (var a in Resources.FindObjectsOfTypeAll<IdleAction>())
                if (a != null && (a.name ?? "").Replace("(Clone)", "").Trim() == "Phone") { _phoneIdle = a; break; }
            _phoneIdle ??= Resources.FindObjectsOfTypeAll<IdleAction>().FirstOrDefault(a => a != null && a.Animation == Character.AnimatorStateType.Phone && !a.dance);
        }
        catch (Exception e) { Log.Debug("phone idle: " + e.Message); }
        if (_phoneIdle == null)
        {
            Log.Debug("No phone animation found; callers just stand.");
            _noPhoneUntil = Time.unscaledTime + MissingFor;
            _noPhoneScene = SceneEpoch();
        }
        return _phoneIdle;
    }

    private static TransitionAction? Transition(string name)
    {
        try
        {
            var idle = PhoneIdle();
            return idle != null ? PoseTransition(idle, exit: name.EndsWith("Exit", StringComparison.Ordinal)) : null;
        }
        catch { return null; }
    }

    /// <summary>The phone's phases: out, on, away (then let go). True when they've just been let go.</summary>
    private static bool TickPhone(Hold h, Character c, float now)
    {
        if (h.WantsPhone && h.Phone == PhonePhase.None)
        {
            if (h.Exiting || h.Returning || h.Walking || !h.Standing) return false; // once they're standing
            h.WantsPhone = false;
            var idle = PhoneIdle();
            if (idle == null) return false;
            try
            {
                // Their own activity comes back on release (a walker walks on), as with any stop to talk.
                if (h.SavedAction == null && c.state != null && c.state.current != null) { h.SavedAction = c.state.current; h.SwitchedToIdle = true; }
                try { c.ChangeTalkWeight(Character.AnimatorTalkType.Idle, 0.3f); } catch { }
                h.EndBlocked = false;
                var enter = Transition("PhoneEnter");
                if (enter != null)
                {
                    h.Phone = PhonePhase.Out;
                    h.PhoneUntil = now + Mathf.Clamp(DurationOf(enter), 0.3f, 4f) + 0.5f;
                    c.SetAction(enter, false);
                }
                else
                {
                    h.Phone = PhonePhase.On;
                    c.SetAction(idle, false);
                }
            }
            catch (Exception e) { Log.Debug("phone out: " + e.Message); h.Phone = PhonePhase.None; }
            return false;
        }
        if (h.Phone == PhonePhase.Out && (h.EndBlocked || now >= h.PhoneUntil))
        {
            h.EndBlocked = false;
            h.Phone = PhonePhase.On;
            try { var idle = PhoneIdle(); if (idle != null) c.SetAction(idle, false); } catch (Exception e) { Log.Debug("on the phone: " + e.Message); }
        }
        else if (h.Phone == PhonePhase.On)
            h.EndBlocked = false; // the call goes on as long as the conversation does, not as long as the game's own phone idle
        else if (h.Phone == PhonePhase.Away && (h.EndBlocked || now >= h.PhoneUntil))
        {
            h.EndBlocked = false;
            h.Phone = PhonePhase.None;
            try { var idle = FindIdle(c); if (idle != null) c.SetAction(idle, false); } catch { }
            if (h.ReleaseAfterPhone)
            {
                h.ReleaseAfterPhone = false;
                Drop(h);
                return !ByChar.ContainsKey(h.CharPtr);
            }
        }
        return false;
    }

    /// <summary>Put the phone away the game's own way (PhoneExit); they're let go once it's done. False if there's no way to.</summary>
    private static bool BeginPhoneAway(Hold h)
    {
        var exit = Transition("PhoneExit");
        if (exit == null) return false;
        try
        {
            h.EndBlocked = false;
            h.Phone = PhonePhase.Away;
            h.ReleaseAfterPhone = true;
            h.PhoneUntil = Time.unscaledTime + Mathf.Clamp(DurationOf(exit), 0.3f, 4f) + 0.5f;
            h.C.SetAction(exit, false);
            return true;
        }
        catch (Exception e) { Log.Debug("phone away: " + e.Message); return false; }
    }

    /// <summary>Steers them toward their spot on their own walk animation; gives up if they don't get anywhere.</summary>
    private static void TickWalk(Hold h, Character c, float dt)
    {
        var pos = c.transform.position;
        var to = Flat(h.WalkTarget - pos);
        float dist = to.magnitude;
        float now = Time.unscaledTime;
        void Stop(bool failed)
        {
            h.Walking = false;
            h.WalkFailed = failed;
            h.HoldPos = c.transform.position;
        }
        if (dist <= h.WalkStop) { Stop(false); return; }
        var want = Quaternion.LookRotation(to / dist, Vector3.up);
        c.transform.rotation = Quaternion.RotateTowards(c.transform.rotation, want, 150f * dt);
        float param = 0f;
        if (h.Anim != null)
        {
            try
            {
                float target = Quaternion.Angle(c.transform.rotation, want) > 60f ? h.WalkParam * 0.4f : h.WalkParam; // slow down to turn
                param = Mathf.MoveTowards(h.Anim.GetFloat(CharacterMovement.speedId), target, dt * 2.5f);
                h.Anim.SetFloat(CharacterMovement.speedId, param);
                h.Anim.SetFloat(CharacterMovement.turnId, 0f);
            }
            catch { }
        }
        // Their walk animation plays, but a held body isn't moved by the game (its own movement is paused): carry them along
        // at walking pace, topping up whatever the animation itself moved them this frame.
        float expected = WalkSpeed * Mathf.Clamp01(h.WalkParam > 0.01f ? param / h.WalkParam : 1f) * dt;
        float moved = h.WalkMoved ? Flat(pos - h.LastWalkPos).magnitude : 0f;
        float extra = Mathf.Min(Mathf.Max(0f, expected - moved), Mathf.Max(0f, dist - h.WalkStop));
        if (extra > 0f)
        {
            var next = pos + (to / dist) * extra;
            // Follow the floor (steps, slopes) smoothly; a floor well above or below is somewhere else, not theirs.
            try
            {
                if (NavMesh.SamplePosition(next, out var hit, 0.5f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - next.y) < 0.4f)
                    next.y = Mathf.MoveTowards(next.y, hit.position.y, 1.5f * dt);
            }
            catch { }
            c.transform.position = next;
        }
        h.LastWalkPos = c.transform.position;
        h.WalkMoved = true;
        if (now - h.ProgressAt > 1.5f)
        {
            if (h.ProgressDist - dist < 0.25f) { Log.Debug($"walk-over: {c.name} isn't getting anywhere ({dist:0.0} m left); they'll talk from here."); Stop(true); return; }
            h.ProgressAt = now;
            h.ProgressDist = dist;
        }
        if (now - h.WalkStarted > 12f) Stop(true);
    }

    /// <summary>
    /// FB-62/FB-86: one frame of following. They head for a spot just behind you (to one side for a second or third
    /// follower), on a NavMesh path planned again every half second or when that spot moves 1.5 m, in one of three fixed
    /// gaits (Game/FollowGait.cs): their own slow walk for the last steps, your walking speed, your sprint. A gait changes
    /// with room either side and the speed eases into it; turns are gentle, and a sharp one slows them as it would anyone.
    /// Standing, they stay put until you're a step away, and turn to you only once you've stood still a moment.
    /// </summary>
    private static void TickFollow(Hold h, Character c, float dt)
    {
        var target = h.FollowTarget;
        if (target == null) { h.Following = false; return; }
        Vector3 tp;
        try { tp = target.position; } catch { h.Following = false; return; }
        var pos = c.transform.position;
        float now = Time.unscaledTime;
        // Your pace (only to tell moving from still) and which way you're going; a jump of a frame (a door, a ride) isn't a pace.
        if (h.FollowHasLast && dt > 0.0001f)
        {
            var step = Flat(tp - h.FollowLastTarget);
            float pace = step.magnitude / dt;
            if (pace < 12f)
            {
                h.FollowSpeed = Mathf.Lerp(h.FollowSpeed, pace, Mathf.Clamp01(dt * 4f));
                if (step.sqrMagnitude > 1e-6f)
                {
                    var dir = step.normalized;
                    var md = h.FollowMoveDir.sqrMagnitude > 0.01f ? Vector3.Slerp(h.FollowMoveDir, dir, Mathf.Clamp01(dt * 3f)) : dir;
                    md.y = 0f; // review F86-3: a turn right round can't tip it up or down
                    h.FollowMoveDir = md.sqrMagnitude > 1e-4f ? md.normalized : dir;
                }
            }
        }
        h.FollowStillFor = h.FollowSpeed < 0.3f ? h.FollowStillFor + dt : 0f;
        h.FollowLastTarget = tp;
        h.FollowHasLast = true;
        var toTarget = Flat(tp - pos);
        float dist = toTarget.magnitude;
        float stop = h.FollowDist;
        // Their spot: behind you the way you're going (kept where it was once you stop, so it doesn't swing round you), on
        // their side of you before you've moved, a little to one side if they're not the first.
        Vector3 back = h.FollowMoveDir.sqrMagnitude > 0.01f ? -h.FollowMoveDir.normalized : (dist > 0.01f ? -toTarget / dist : Vector3.back);
        var slot = tp + back * stop + Vector3.Cross(Vector3.up, back) * h.FollowSide;
        // Review F86-1: the gap is to their spot (a second or third follower's is off to the side), never inside the stop distance.
        float gap = Mathf.Min(Flat(slot - pos).magnitude, Mathf.Max(0f, dist - stop));
        float youWalk = -1f, youSprint = -1f;
        bool sprinting;
        if (h.FollowPlayer) (youWalk, youSprint, sprinting) = GameContext.PlayerSpeeds();
        else sprinting = h.FollowSpeed > 3f; // review F86-2: a resident they follow runs, they run
        var (stroll, walk, run) = h.FollowPlayer ? FollowGait.Speeds(WalkSpeed, youWalk, youSprint) : FollowGait.Speeds(WalkSpeed, WalkSpeed * 1.3f, 4.0f);
        if (h.FollowPlayer && youSprint <= 0f) sprinting = h.FollowSpeed > (walk + run) / 2f; // your controller couldn't be read: told by your pace
        h.FollowGaitNow = FollowGait.Next(h.FollowGaitNow, gap, sprinting, catchUp: h.FollowPlayer);
        float walkP = h.WalkParam > 0.01f ? h.WalkParam : 1f;
        if (h.FollowGaitNow == FollowGait.Gait.Stand)
        {
            // Arrived: the walk eased out; they face you only once you've been still a moment and are well off to one side.
            h.FollowCurSpeed = 0f;
            h.FollowParam = Mathf.MoveTowards(h.FollowParam, 0f, dt * 3f);
            SetWalkParam(h, h.FollowParam);
            if (dist > 0.05f)
            {
                var face = Quaternion.LookRotation(toTarget / dist, Vector3.up);
                float facingOff = Quaternion.Angle(c.transform.rotation, face);
                h.FollowFacing = FollowGait.KeepFacing(h.FollowFacing, h.FollowStillFor, facingOff);
                if (h.FollowFacing) c.transform.rotation = Quaternion.RotateTowards(c.transform.rotation, face, Leash.TurnSpeed(facingOff) * 0.6f * dt);
            }
            h.HoldPos = pos;
            h.WalkMoved = false;
            return;
        }
        h.FollowFacing = false;
        if (now >= h.FollowPlanAt || Flat(slot - h.FollowPlannedFor).magnitude > 1.5f || h.FollowCorner >= h.FollowPath.Length)
        {
            h.FollowPlanAt = now + 0.5f;
            h.FollowPlannedFor = slot;
            h.FollowPath = PathTo(pos, slot);
            h.FollowCorner = h.FollowPath.Length > 1 ? 1 : 0;
        }
        Vector3 next = h.FollowPath.Length > 0 ? h.FollowPath[Mathf.Min(h.FollowCorner, h.FollowPath.Length - 1)] : slot;
        var toNext = Flat(next - pos);
        if (toNext.magnitude < 0.3f && h.FollowCorner < h.FollowPath.Length - 1) { h.FollowCorner++; next = h.FollowPath[h.FollowCorner]; toNext = Flat(next - pos); }
        float leg = toNext.magnitude;
        if (leg < 0.01f) { toNext = toTarget; leg = Mathf.Max(0.01f, dist); }
        // The gait's speed, eased into (a body speeding up or slowing down, not a switch).
        h.FollowCurSpeed = FollowGait.Ease(h.FollowCurSpeed, FollowGait.SpeedOf(h.FollowGaitNow, stroll, walk, run), dt);
        float speed = h.FollowCurSpeed;
        var want = Quaternion.LookRotation(toNext / leg, Vector3.up);
        float off = Quaternion.Angle(c.transform.rotation, want);
        c.transform.rotation = Quaternion.RotateTowards(c.transform.rotation, want, FollowGait.TurnRate(h.FollowGaitNow, off) * dt);
        float along = off > 75f ? 0.35f : off > 40f ? 0.7f : 1f; // a sharp turn slows them down
        // The animation: one value per gait (the run's learned from the game's own runners), eased only when the gait changes.
        float runP = GameContext.TypicalRunParam(walkP);
        float paramWant = h.FollowGaitNow switch
        {
            FollowGait.Gait.Run => runP,
            FollowGait.Gait.Walk => Mathf.Min(walkP * Mathf.Clamp(walk / WalkSpeed, 0.9f, 1.35f), runP * 0.8f),
            _ => walkP * Mathf.Clamp(stroll / WalkSpeed, 0.6f, 1f),
        } * Mathf.Clamp01(speed / Mathf.Max(0.1f, FollowGait.SpeedOf(h.FollowGaitNow, stroll, walk, run)) * 1.25f) * (along < 1f ? 0.75f : 1f);
        h.FollowParam = Mathf.MoveTowards(h.FollowParam, paramWant, dt * 3f);
        SetWalkParam(h, h.FollowParam);
        // Carried along at that speed (topping up what the animation moved them), never past their spot or closer than the stop.
        float expected = speed * along * dt;
        float moved = h.WalkMoved ? Flat(pos - h.LastWalkPos).magnitude : 0f;
        float extra = Mathf.Min(Mathf.Max(0f, expected - moved), Flat(slot - pos).magnitude, Mathf.Max(0f, dist - stop + 0.05f), leg + 0.001f);
        if (extra > 0f)
        {
            var nextPos = pos + (toNext / leg) * extra;
            try
            {
                if (NavMesh.SamplePosition(nextPos, out var hit, 0.6f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - nextPos.y) < 0.6f)
                    nextPos.y = Mathf.MoveTowards(nextPos.y, hit.position.y, 3f * dt);
            }
            catch { }
            c.transform.position = nextPos;
        }
        h.LastWalkPos = c.transform.position;
        h.WalkMoved = true;
        h.HoldPos = c.transform.position;
    }

    private static void SetWalkParam(Hold h, float value)
    {
        if (h.Anim == null) return;
        try
        {
            h.Anim.SetFloat(CharacterMovement.speedId, value);
            h.Anim.SetFloat(CharacterMovement.turnId, 0f);
        }
        catch { }
    }

    private static readonly NavMeshPath FollowPathBuffer = new();

    /// <summary>The NavMesh path's corners from <paramref name="from"/> to <paramref name="to"/>, or the straight line when there's none.</summary>
    private static Vector3[] PathTo(Vector3 from, Vector3 to)
    {
        try
        {
            if (NavMesh.SamplePosition(from, out var a, 1.5f, NavMesh.AllAreas)) from = a.position;
            if (NavMesh.SamplePosition(to, out var b, 2.5f, NavMesh.AllAreas)) to = b.position;
            if (NavMesh.CalculatePath(from, to, NavMesh.AllAreas, FollowPathBuffer) && FollowPathBuffer.status != NavMeshPathStatus.PathInvalid)
            {
                var corners = FollowPathBuffer.corners;
                if (corners != null && corners.Length > 0)
                {
                    var list = new Vector3[corners.Length];
                    for (int i = 0; i < corners.Length; i++) list[i] = corners[i];
                    return list;
                }
            }
        }
        catch (Exception e) { Log.Debug("follow path: " + e.Message); }
        return new[] { from, to };
    }

    // ------------------------------------------------------------------ Harmony: freeze only the held

    [HarmonyPatch(typeof(AgentGhostSimulator), nameof(AgentGhostSimulator.UpdateCurrentAgentAction))]
    private static class FreezeAi
    {
        private static bool Prefix(AgentGhost agent)
        {
            if (ByGhost.Count == 0 || agent == null || !ByGhost.TryGetValue(agent.Pointer, out var h)) return true;
            if (h == _chat) DbgAi++;
            return false;
        }
    }

    [HarmonyPatch(typeof(AgentGhostSimulator), nameof(AgentGhostSimulator.SelectNewAction))]
    private static class FreezeAiChoice
    {
        private static bool Prefix(AgentGhost agent) => ByGhost.Count == 0 || agent == null || !ByGhost.ContainsKey(agent.Pointer);
    }

    /// <summary>For the log: how far <paramref name="spot"/> is from the places the game might be putting them.</summary>
    private static string Whereabouts(Character c, Vector3 spot)
    {
        var parts = new List<string>();
        void Add(string what, Func<Vector3> at)
        {
            try { parts.Add($"{Flat(at() - spot).magnitude:0.0} m from their {what}"); } catch { }
        }
        Add("ghost", () => c.Agent.MyGhost.Position);
        Add("nav agent", () => c.Movement.Agent.nextPosition);
        Add("snap spot", () => c.Movement.SnapPosition);
        Add("movement position", () => c.Movement.Position);
        Add("path point", () => c.Movement.PathPosition(c.Movement.PathProgress));
        try { var cur = c.state != null ? c.state.current : null; parts.Add("action " + (cur != null ? cur.name : "-")); } catch { }
        return string.Join(", ", parts);
    }

    // ------------------------------------------------------------------ the game moving held people, stopped at the source

    /// <summary>
    /// Held people the game mustn't move: standing still or walking over (not while stepping in or out of a pose or
    /// taking the phone out or away, when the game's own animation moves them). Every frame the game set held people back
    /// onto the spot their routine wanted them at (where they'd been walking to, metres away); put back only at the end of
    /// the frame, everything placed in between (speech bubbles) saw them in the wrong place.
    /// </summary>
    private static bool Protected(Hold h) =>
        !h.Light && !(h.Exiting || h.Entering || h.Returning || h.Phone is PhonePhase.Out or PhonePhase.Away) && (h.Standing || h.Walking);

    private static readonly string[] FenceNames = { "the game's character update", "the game's late character update", "the game's animation step" };

    // These run for every person, every frame, while anyone is held (D4, PERF-1). The Character is the one the game is
    // updating, so it's alive: a managed "is null" instead of Unity's == (a call into the game each time), and its hold is
    // looked up once.

    /// <summary>Notes where a held person stands as one of the game's per-person updates begins.</summary>
    private static void Before(Character c, int fence)
    {
        if (ByChar.Count == 0 || c is null || !ByChar.TryGetValue(c.Pointer, out var h)) return;
        Before(c, h, fence);
    }

    private static void Before(Character c, Hold h, int fence)
    {
        if (!Protected(h)) return;
        try { h.FenceFrom[fence] = c.transform.position; h.FenceSet[fence] = true; } catch { }
    }

    /// <summary>...and puts them straight back if that update moved them (their idle's own sway, a few millimetres, is left alone).</summary>
    private static void After(Character c, int fence)
    {
        if (ByChar.Count == 0 || c is null || !ByChar.TryGetValue(c.Pointer, out var h) || !h.FenceSet[fence]) return;
        h.FenceSet[fence] = false;
        if (!Protected(h)) return;
        try
        {
            var now = c.transform.position;
            var was = h.FenceFrom[fence];
            if ((now - was).sqrMagnitude < 0.0025f) return; // under 5 cm
            c.transform.position = was;
            if ((h.FencesLogged & (1 << fence)) == 0 && (now - was).sqrMagnitude > 0.25f)
            {
                h.FencesLogged |= 1 << fence;
                Log.Debug($"hold: {FenceNames[fence]} moved {c.name} {Vector3.Distance(now, was):0.0} m; put back on the spot. That spot is {Whereabouts(c, now)}.");
            }
        }
        catch { }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.DoLateUpdate))]
    private static class FenceLate
    {
        private static void Prefix(Character __instance) => Before(__instance, 1);
        private static void Postfix(Character __instance) => After(__instance, 1);
    }

    [HarmonyPatch(typeof(Character), nameof(Character.OnAnimatorMove))]
    private static class FenceAnimation
    {
        private static void Prefix(Character __instance) => Before(__instance, 2);
        private static void Postfix(Character __instance) => After(__instance, 2);
    }

    /// <summary>The game walks ghosts along their route on its own (for bodies out of sight); a held person's ghost stays put.</summary>
    [HarmonyPatch(typeof(AgentViewSimulator), nameof(AgentViewSimulator.Simulate))]
    private static class HoldGhostWalk
    {
        private static bool Prefix(AgentGhost agentGhost) => ByGhost.Count == 0 || agentGhost == null || !ByGhost.ContainsKey(agentGhost.Pointer);
    }

    /// <summary>The game snapping a body onto its ghost: refused for the held (unless the body now belongs to someone else).</summary>
    [HarmonyPatch(typeof(Agent), nameof(Agent.SnapToGhost))]
    private static class KeepFromGhost
    {
        private static bool Prefix(Agent __instance)
        {
            if (ByChar.Count == 0 || __instance == null) return true;
            Character? c;
            try { c = __instance.Character; } catch { return true; }
            if (c == null || !ByChar.TryGetValue(c.Pointer, out var h) || h.Light || Relinked(h, c)) return true;
            if (++h.SnapsBlocked == 1) Log.Debug($"hold: kept {c.name} from being snapped to their ghost ({SeenText(c)}).");
            return false;
        }
    }

    /// <summary>For the log only: the game switching a held body to its stand-in mode (seen when people jumped around).</summary>
    [HarmonyPatch(typeof(Character), nameof(Character.SetFake))]
    private static class NoteFake
    {
        private static void Prefix(Character __instance, bool move, bool sceneSnap)
        {
            if (ByChar.Count == 0 || __instance == null || !ByChar.TryGetValue(__instance.Pointer, out var h)) return;
            if (++h.Fakes <= 2) Log.Debug($"hold: the game set {__instance.name} to stand-in mode (move {move}, snap {sceneSnap}, {SeenText(__instance)}).");
        }
    }

    /// <summary>DoUpdate is where the game ticks the Animator by hand. Keep it running (idle, talk, lip-sync) with locomotion eased to zero (or to a walk).</summary>
    [HarmonyPatch(typeof(Character), nameof(Character.DoUpdate))]
    private static class HoldBody
    {
        private static void Postfix(Character __instance) => After(__instance, 0);

        private static bool Prefix(Character __instance)
        {
            if (ByChar.Count == 0 || !ByChar.TryGetValue(__instance.Pointer, out var h) || h.Light) return true;
            Before(__instance, h, 0); // the hold already found: not looked up again
            if (h == _chat) DbgDoUpdate++;
            if (IdleParamsInDoUpdate && h.Anim != null)
            {
                try
                {
                    float dt = Math.Max(Time.deltaTime, 0.016f);
                    float target = h.Walking ? h.WalkParam : 0f;
                    h.Anim.SetFloat(CharacterMovement.speedId, Mathf.MoveTowards(h.Anim.GetFloat(CharacterMovement.speedId), target, dt * 4f));
                    h.Anim.SetFloat(CharacterMovement.turnId, 0f);
                }
                catch { }
            }
            return !SkipDoUpdate;
        }
    }

    /// <summary>
    /// Each action's OnAnimator runs on every animation step: MoveAction drives speed/turn and applies the root
    /// motion. For the held we skip it and ease locomotion to zero, so they settle into a standing idle and their
    /// route resumes untouched on release. IdleAction is left alone (already idle).
    /// </summary>
    internal static int DbgOnAnimator;

    private static bool HoldAnimator(Character character)
    {
        // The game hands its own Character in: "is null" (managed) is enough, and costs no call into the game (D4).
        if (ByChar.Count == 0 || character is null || !ByChar.TryGetValue(character.Pointer, out var h) || h.Light) return true;
        if (h == _chat) DbgOnAnimator++;
        if (!IdleParamsInDoUpdate || h.Anim == null) return true;
        try
        {
            float step = Math.Max(Time.deltaTime, 0.016f);
            float target = h.Walking ? h.WalkParam : 0f;
            h.Anim.SetFloat(CharacterMovement.speedId, Mathf.MoveTowards(h.Anim.GetFloat(CharacterMovement.speedId), target, step * 2.5f));
            h.Anim.SetFloat(CharacterMovement.turnId, Mathf.MoveTowards(h.Anim.GetFloat(CharacterMovement.turnId), 0f, step * 4f));
        }
        catch { }
        return false;
    }

    [HarmonyPatch(typeof(MoveAction), nameof(MoveAction.OnAnimator))]
    private static class HoldMove { private static bool Prefix(Character character) => HoldAnimator(character); }

    [HarmonyPatch(typeof(TurnAction), nameof(TurnAction.OnAnimator))]
    private static class HoldTurn { private static bool Prefix(Character character) => HoldAnimator(character); }

    // TransitionAction.OnAnimator is deliberately not held: seat transitions finish naturally; others are swapped for idle.

    // WanderSpotAction.OnAnimator is NOT hooked: its body is empty and IL2CPP shares that one empty function with
    // ~5,800 unrelated methods, so a hook there hijacks all of them (crashed the game on load, 2026-10-10).

    /// <summary>When the idle finishes, EndAction would advance them to their next task step mid-conversation.</summary>
    [HarmonyPatch(typeof(Character), nameof(Character.EndAction))]
    private static class HoldEndAction
    {
        private static bool Prefix(Character __instance)
        {
            if (ByChar.Count == 0 || !ByChar.TryGetValue(__instance.Pointer, out var h) || h.Light) return true;
            h.EndBlocked = true; // their current step finished while held: continue it on release
            return false;
        }
    }

    /// <summary>SetTask drives the character's task/action machine (walking a path, turning, sitting...).</summary>
    [HarmonyPatch(typeof(Character), nameof(Character.SetTask))]
    private static class HoldTask
    {
        private static bool Prefix(Character __instance)
        {
            if (ByChar.Count == 0 || !ByChar.TryGetValue(__instance.Pointer, out var h) || h.Light) return true;
            if (h == _chat) DbgSetTask++;
            return !SkipSetTask;
        }
    }
}
