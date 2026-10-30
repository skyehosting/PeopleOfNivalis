using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nivalis;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using NivalisAIChat.Voice;
using UnityEngine;

namespace NivalisAIChat;

/// <summary>
/// Residents talking to each other near the player (NPC chatter): picking who (people at the same table, two who've
/// talked before, or anyone), holding them together (one may walk over), having the conversation written, then playing
/// it line by line — a bubble over the speaker, their voice, the others turning to them, each leaving after their
/// goodbye. Also people muttering to themselves. Off by default; everything is set with /chatter.
/// </summary>
public static partial class ModRuntime
{
    private sealed class Member
    {
        public Character C = null!;
        public string Id = "";
        public Transform Head = null!;
        public bool Seated;
        public int JoinAt;
        public bool Joined, Gone, Held, LeaveAfterReply;
        /// <summary>0.3.3: held lightly (a customer at a table mid-visit: the game's steps run on); where they sat.</summary>
        public bool Light;
        public Vector3 Seat;
        public float LeaveAt = -1f;
    }

    private sealed class Group
    {
        public readonly List<Member> Members = new();
        public ChatterScript? Script;
        public bool Playing, Table;
        /// <summary>FB-18: which table (its parts and rests are kept under this).</summary>
        public string TableKey = "";
        /// <summary>FB-19: a phone call interrupts this table conversation (decided at the start).</summary>
        public bool TableCall;
        /// <summary>FB-05 (review): the conversation is about the player somewhere: none of it is overheard.</summary>
        public bool AboutPlayer;
        /// <summary>You walked off: the next one to speak gives a reason to go, the others say goodbye. No longer counts in the Dynamic pool.</summary>
        public bool WrappingUp;
        public int Next, WaitFor = -1;
        public float NextAt, PausedSince = -1f, StartedAt, WaitSince, NextLeaveCheck;
        public Member? Walker;
        /// <summary>Who overheard what (FB-05), written when it ends.</summary>
        public readonly Overhearing Heard = new();
        public readonly Dictionary<int, Task<VoiceOutcome>> Voices = new();
        public readonly CancellationTokenSource Cts = new();
        public string Names => string.Join(", ", Members.Select(m => m.C != null ? m.C.name : m.Id));
    }

    /// <summary>
    /// FB-05: people near residents who are talking, and what they caught (main thread only). Each listener rolls once per
    /// conversation; one who caught it hears what's said while they're near (at most 4 lines). Written once, at the end.
    /// </summary>
    private sealed class Overhearing
    {
        public readonly Dictionary<string, bool> Rolled = new();
        public readonly Dictionary<string, (long epoch, List<(string speakerId, string speakerName, string line)> lines)> By = new();
    }

    /// <summary>
    /// A line was just said: anyone standing near the speaker who isn't in the conversation may catch it (OverhearChance,
    /// OverhearDistance). Adults only, never a line about the player, nobody in <paramref name="present"/>.
    /// </summary>
    private static void Overhear(Overhearing o, Character speaker, string speakerId, string speakerName, string line, ICollection<string> present,
                                 IEnumerable<string> playerNames, IEnumerable<string> playerPlaces)
    {
        int chance = Math.Clamp(_cfg.OverhearChance.Value, 0, 100);
        if (chance <= 0 || line.Length == 0 || !Features.Overhearing) return;
        float near = Mathf.Clamp(float.IsFinite(_cfg.OverhearDistance.Value) ? _cfg.OverhearDistance.Value : 2.5f, 0.5f, 6f);
        if (ChatterPlan.AboutThePlayer(line, playerNames, playerPlaces)) return;
        var pc = GameContext.PlayerCharacter();
        if (pc == null || !Alive(speaker)) return;
        Vector3 at = speaker.transform.position;
        foreach (var (c, ptr, pos) in NearBodies(pc))
        {
            if (ptr == speaker.Pointer || Flat(pos - at).magnitude > near) continue;
            string id;
            try { id = GameContext.NpcId(c); } catch { continue; }
            if (id.Length == 0 || id == speakerId) continue;
            bool inIt = present.Contains(id), busy = NpcHold.IsHeld(c);
            if (inIt || busy) continue; // in this talk, or busy in one of their own (or with you): no roll yet, they may catch a later line once free
            if (!o.Rolled.TryGetValue(id, out bool caught))
            {
                // Under 18, or the roll: they don't catch it (ChatterPlan.Overhears).
                caught = ChatterPlan.Overhears(Flat(pos - at).magnitude, near, inIt, busy, MinorInfo(c, id).minor, ChatterRng.Next(100), chance);
                o.Rolled[id] = caught;
                if (caught && _chatter != null) o.By[id] = (_chatter.EpochFor(id), new()); // the brain that will write it (mock mode too)
                else if (caught) o.Rolled[id] = false;
            }
            if (caught && o.By.TryGetValue(id, out var e) && e.lines.Count < 4) e.lines.Add((speakerId, speakerName, line));
        }
    }

    /// <summary>What each listener caught, kept with their talks (only someone who already has a record; nothing after a reset).</summary>
    private static void RememberOverheard(Overhearing o, GameSnapshot place)
    {
        if (o.By.Count == 0 || _chatter == null) return;
        var chatter = _chatter;
        foreach (var (id, (epoch, lines)) in o.By.Where(kv => kv.Value.lines.Count > 0).ToList())
        {
            var heard = lines.ToList();
            Task.Run(async () =>
            {
                try { await chatter.RememberOverheardAsync(id, epoch, place, heard).ConfigureAwait(false); }
                catch (Exception e) { Log.Debug("remembering what someone overheard: " + e.Message); }
            });
        }
        Log.Debug($"overheard: {o.By.Count(kv => kv.Value.lines.Count > 0)} of {o.Rolled.Count} nearby caught something");
    }

    private static readonly List<Group> Groups = new();
    /// <summary>CF-26: when each body was last taken into one of this mod's conversations (venue talk leaves their order to the game a while).</summary>
    private static readonly Dictionary<IntPtr, float> PulledIn = new();
    private static readonly List<double> ChatterStarts = new();

    /// <summary>
    /// A resident conversation started (counted against ChatterPerHour), and kept in the usage record by kind (talk, call,
    /// think, order): the Usage Optimizer learns what a start costs from it (FB-41).
    /// </summary>
    private static void NoteStart(string kind)
    {
        ChatterStarts.Add(Time.unscaledTime);
        // FB-59: while the pacer holds talk to a share of what would start, each start stands for 1/share of them, so the start
        // rate learned from paced hours is what your settings start (it read low, and the pacer let go, then held on again).
        double weight = PacingOn && _pacer.M < 1 ? 1 / Math.Max(Pacer.Floor, _pacer.M) : 1;
        try { _voiceLedger?.Record(UsageModel.StartsKey, "Residents' conversations", "starts", kind, weight); } catch { }
    }
    private static readonly System.Random ChatterRng = new();
    private static float _nextGroupRoll, _nextPhoneRoll, _nextThinkRoll;
    private static PairBook? _pairs;
    /// <summary>FB-35: groups of residents who've talked together.</summary>
    private static CircleBook? _circles;
    private static ChatterBrain? _chatter;

    /// <summary>Built once per change of settings (D3): cleared by its own SettingChanged handler, wired in Start.</summary>
    private static ChatterSettingsCache? _chatterSettings;

    /// <summary>
    /// The chatter settings as they apply now (in Dynamic mode, its own line limit and habits; see ChatterSettings.Effective).
    /// Shared until a setting changes: never changed by a caller (PERF-6).
    /// </summary>
    private static ChatterSettings ChatterSettingsNow() => (_chatterSettings ??= new ChatterSettingsCache(() => ChatterSettingsAsSet().Effective())).Now;

    /// <summary>The chatter settings exactly as configured (for showing the Advanced ones).</summary>
    private static ChatterSettings ChatterSettingsAsSet() => new()
    {
        Enabled = _cfg.Chatter.Value,
        Dynamic = !string.Equals(_cfg.ChatterMode.Value.Trim(), "Advanced", StringComparison.OrdinalIgnoreCase),
        DynamicEverySeconds = ChatterSettings.Interval(_cfg.DynamicEvery.Value) ?? 15,
        DynamicMaxNpcs = _cfg.DynamicMaxNpcs.Value,
        DynamicLines = _cfg.DynamicLinesEach.Value,
        DynamicPhoneLines = _cfg.DynamicPhoneLinesEach.Value,
        DynamicThinkLines = _cfg.DynamicThinkLinesEach.Value,
        DynamicStarts = _cfg.DynamicStartsEach.Value,
        DynamicStagger = Mathf.Clamp(_cfg.DynamicStagger.Value, 0f, 60f),
        WeightGossip = _cfg.DynamicGossipWeight.Value,
        WeightPhone = Features.PhoneCalls ? _cfg.DynamicPhoneWeight.Value : 0, // FB-45: a kind switched off is never picked
        WeightThink = Features.Muttering ? _cfg.DynamicThinkWeight.Value : 0,
        WeightShop = _cfg.OrderTalk.Value ? _cfg.DynamicShopWeight.Value : 0,
        WeightText = Features.Phone && _cfg.PeopleTextYou.Value ? _cfg.DynamicTextWeight.Value : 0, // the phone: someone texting you
        CheckSeconds = ChatterSettings.Interval(_cfg.GroupEvery.Value) ?? 30,
        PhoneEverySeconds = ChatterSettings.Interval(_cfg.PhoneEvery.Value) ?? 60,
        PhoneMax = _cfg.PhoneMaxActive.Value,
        PhoneLines = _cfg.PhoneLinesEach.Value,
        ThinkEverySeconds = ChatterSettings.Interval(_cfg.ThinkEvery.Value) ?? 45,
        ThinkMax = _cfg.ThinkMaxActive.Value,
        ThinkLines = _cfg.ThinkLinesEach.Value,
        VoiceDistance = Mathf.Clamp(_cfg.VoiceDistance.Value, 0f, 200f),
        VoiceBeyond = Mathf.Clamp(_cfg.VoiceBeyond.Value, 0f, 30f),
        TextDistance = Mathf.Clamp(_cfg.TextDistance.Value, 0f, 200f),
        ReplaceGameChatter = _cfg.ReplaceGameChatter.Value,
        ChancePercent = _cfg.ChatterChance.Value,
        MaxGroups = _cfg.ChatterMaxGroups.Value,
        PerHour = PacedPerHour(), // FB-41: the pacer's share while a limit needs it
        GroupSize = _cfg.ChatterGroupSize.Value,
        JoinPercent = _cfg.ChatterJoinChance.Value,
        LinesEach = _cfg.ChatterLinesEach.Value,
        KnownPairPercent = _cfg.ChatterKnownPairs.Value,
        TablePercent = _cfg.ChatterTables.Value,
        // Within what every scan reads (5-60 m); a hand edit of NaN or 500 can't stop talk from ever wrapping up (FB-38 review).
        Range = float.IsFinite(_cfg.ChatterRange.Value) ? Mathf.Clamp(_cfg.ChatterRange.Value, 5f, 60f) : 18.25f,
        WalkUp = _cfg.ChatterWalkUp.Value,
        MumblePercent = Features.Muttering ? _cfg.MumbleChance.Value : 0,
        PhonePercent = Features.PhoneCalls ? _cfg.PhoneChance.Value : 0,
    };

    private static bool Alive(Character? c)
    {
        try { return c != null && !c.WasCollected && c.isActiveAndEnabled; } catch { return false; }
    }

    private static Vector3 Flat(Vector3 v) => new(v.x, 0, v.z);

    /// <summary>The person you're talking to (never released by the residents' conversation code).</summary>
    private static bool ChatNpcIs(Character c)
    {
        try { return _npc != null && c != null && _npc.Pointer == c.Pointer; } catch { return false; }
    }

    /// <summary>
    /// Someone already using their voice: held (in a conversation, on a call, in your chat), in a residents' conversation
    /// (joined and still there), either end of a call (<paramref name="id"/> also matches a call's other end who isn't
    /// around), thinking out loud, in shop talk, or the one your chat or brush-off window is with. Each person has one
    /// voice, so nothing else starts with them or speaks over them (B2). <paramref name="except"/>: the call or shop
    /// talk asking, which doesn't count against its own people.
    /// </summary>
    private static bool TalkingNow(Character? c, string id = "", object? except = null)
    {
        if (c == null) return false;
        try
        {
            if (ChatNpcIs(c) || Thinking(c) || NpcHold.IsHeld(c)) return true;
            if (Groups.Any(g => g.Members.Any(m => m.Joined && !m.Gone && Same(m.C, c)))) return true;
            if (Calls.Any(k => k != except && (Same(k.C, c) || Same(k.OtherC, c) || (id.Length > 0 && k.OtherId == id)))) return true;
            if (id.Length > 0 && Groups.Any(g => g != except && g.Script?.Call?.Callee.Record?.Id == id)) return true; // on the phone to a table (review M2)
            return VenueTalks.Any(t => t != except && !t.Cancelled && (Same(t.Staff, c) || Same(t.Customer, c)));
        }
        catch { return false; }
    }

    /// <summary>In shop talk right now (staff or customer).</summary>
    private static bool InVenueTalk(Character c) => VenueTalks.Any(t => !t.Cancelled && (Same(t.Staff, c) || Same(t.Customer, c)));

    /// <summary>Every frame: plays the conversations going on, and now and then starts a new one or a mutter.</summary>
    private static void TickChatter()
    {
        if (_chatter == null) return;
        if (!_cfg.Chatter.Value) // off: the game's own chatter is back, and nothing else is built or scanned (PERF-6)
        {
            GameChatter.Muted = false;
            if (Groups.Count > 0 || Calls.Count > 0 || Thoughts.Count > 0 || VenueTalks.Count > 0) EndAllChatter("switched off");
            NearBodiesNow.Clear(); // the last scan's people aren't kept alive while chatter is off (6b review)
            _nearBodiesFrame = -1;
            return;
        }
        var s = ChatterSettingsNow();
        GameChatter.Muted = s.ReplaceGameChatter;
        // Your own chat doesn't stop the city: conversations around you carry on (a little quieter, so the person in front
        // of you stays clear). The console, the game's menus and a stopped game pause them. New ones start only in normal play.
        bool stopped = false;
        try { stopped = Time.timeScale <= 0f; } catch { }
        bool paused = ConsoleOpen || _resetting || stopped || (!ChatOpen && !InGameplay());
        // A copy of each list only when there's something in it (no garbage every frame while nothing's going on).
        if (Groups.Count > 0)
            foreach (var g in Groups.ToList())
            {
                try { TickGroup(g, s, paused); }
                catch (Exception e) { Log.Debug("chatter: " + e.Message); EndGroup(g, "error"); }
            }
        if (Calls.Count > 0)
            foreach (var call in Calls.ToList())
            {
                try { TickCall(call, s, paused); }
                catch (Exception e) { Log.Debug("phone call: " + e.Message); EndCall(call, "error"); }
            }
        if (Thoughts.Count > 0)
            foreach (var t in Thoughts.ToList())
            {
                try { TickThought(t, s, paused); }
                catch (Exception e) { Log.Debug("thinking out loud: " + e.Message); EndThought(t, "error"); }
            }
        TickVenues(s, paused);
        try { TickTableWatch(s, paused); } catch (Exception e) { Log.Debug("table goodbyes: " + e.Message); }
        if (paused || ChatOpen) return;
        float now = Time.unscaledTime;
        if (now >= _nextStartsPrune) // only the last hour matters; InLastHour counts that anyway, this just keeps the list short
        {
            _nextStartsPrune = now + 60f;
            ChatterStarts.RemoveAll(t => now - t > 3600);
        }
        if (s.Dynamic) { TickDynamic(s, now); return; }
        // Advanced: each kind on its own timer, chance and pool (a new one only when there's room); all share the hourly cap.
        bool Room() => ChatterPlan.InLastHour(ChatterStarts, now) < s.PerHour && ChatterPlan.Spaced(ChatterStarts, now, s.PerHour, 2) && AiAllowed();
        if (now >= _nextGroupRoll)
        {
            _nextGroupRoll = now + Math.Max(5, s.CheckSeconds);
            if (Room() && Groups.Count < Math.Clamp(s.MaxGroups, 0, ChatterSettings.MaxAtOnce) && ChatterRng.NextDouble() * 100 < s.ChancePercent) TryStartGroup(s, out _);
        }
        if (now >= _nextPhoneRoll)
        {
            _nextPhoneRoll = now + Math.Max(5, s.PhoneEverySeconds);
            if (Room() && Calls.Count < Math.Clamp(s.PhoneMax, 0, ChatterSettings.MaxAtOnce) && ChatterRng.NextDouble() * 100 < s.PhonePercent) TryPhoneCall(s, out _);
        }
        if (now >= _nextThinkRoll)
        {
            _nextThinkRoll = now + Math.Max(5, s.ThinkEverySeconds);
            if (Room() && Thoughts.Count < Math.Clamp(s.ThinkMax, 0, ChatterSettings.MaxAtOnce) && ChatterRng.NextDouble() * 100 < s.MumblePercent) TryThink(s, out _);
        }
    }

    private static float _nextDynamicLook, _nextStartsPrune;

    /// <summary>
    /// People talking near you, for the Dynamic pool: everyone in a conversation (and anyone due to join), a caller (and the
    /// other end, if they're here), someone thinking out loud. Anyone wrapping up because you walked off doesn't count.
    /// </summary>
    private static int PeopleTalking() =>
        Groups.Where(g => !g.WrappingUp).Sum(g => Math.Max(2, g.Members.Count(m => !m.Gone)))
        + Calls.Where(c => !c.WrappingUp).Sum(c => 1 + (c.OtherC != null ? 1 : 0))
        + Thoughts.Count; // order talk isn't counted: written lines with its own look and cap (FB-21 review)

    /// <summary>This round's starts still to come (when each is due), so the ones in one round don't all begin at once.</summary>
    private static readonly List<float> DynamicDue = new();

    /// <summary>
    /// Dynamic chatter: every DynamicEvery, a round of 1 to DynamicStartsEach starts, each a random moment (up to
    /// DynamicStagger) after the one before. Each start, while fewer than DynamicMaxNpcs people are talking near you, picks a
    /// kind by its weight (gossip, shop talk, a phone call, someone muttering; the next if nobody fits) and someone near you
    /// who fits it. Gossip goes to a group the game gathered when there is one, else to people near each other.
    /// </summary>
    private static void TickDynamic(ChatterSettings s, float now)
    {
        if (now >= _nextDynamicLook)
        {
            _nextDynamicLook = now + s.DynamicEverySeconds;
            DynamicDue.Clear(); // a new round decides afresh
            // Fitted into the round: a start still waiting when the next round begins would be dropped.
            foreach (float wait in DynamicChatter.Stagger(ChatterRng, DynamicChatter.Starts(ChatterRng, s.DynamicStarts), s.DynamicStagger, s.DynamicEverySeconds))
                DynamicDue.Add(now + wait);
        }
        while (DynamicDue.Count > 0 && DynamicDue[0] <= now)
        {
            DynamicDue.RemoveAt(0);
            DynamicStart(s, now);
        }
    }

    /// <summary>One Dynamic start: a kind by weight, and someone who fits it (nothing when the pool is full).</summary>
    private static void DynamicStart(ChatterSettings s, float now)
    {
        bool aiRoom = ChatterPlan.InLastHour(ChatterStarts, now) < s.PerHour && ChatterPlan.Spaced(ChatterStarts, now, s.PerHour, Math.Max(2, s.DynamicStarts)) && AiAllowed();
        int free = s.DynamicMaxNpcs - PeopleTalking();
        var order = DynamicChatter.Order(ChatterRng, free, s.DynamicWeights);
        // Shop talk is written lines, no AI: it has its own look (TickVenues) and takes no place in this pool (FB-21).
        order.Remove(DynamicChatter.Kind.Shop);
        if (!aiRoom) return;
        var d = s.Copy();
        d.GroupSize = Math.Clamp(free, 2, 4); // a group (and anyone joining) fits in what's left of the pool
        List<Cand>? cands = null;
        if (!MakeRoomForTables(d, order, free, now, ref cands)) return; // FB-18: a table in front of you goes first
        if (order.Count == 0) return;
        foreach (var kind in order)
        {
            string why = "";
            bool started;
            try
            {
                switch (kind)
                {
                    case DynamicChatter.Kind.Gossip:
                        var all = cands ??= ChatterCandidates(d);
                        // A table first (FB-18), then a group the game gathered, then two people standing near each other.
                        var holdable = all.Where(x => x.Holdable).ToList(); // never someone at work or mid-visit (0.3.3)
                        started = TryTable(d, all, out why)
                                  || (d.ReplaceGameChatter && TryGatheredGroup(d, holdable, out why))
                                  || TryPair(d, holdable.Where(x => x.GameGroup == IntPtr.Zero && !x.Seated).ToList(), out why);
                        break;
                    case DynamicChatter.Kind.Phone: started = TryPhoneCall(d, out why); break;
                    case DynamicChatter.Kind.Shop: started = TryVenueTalk(d, now, out why); break;
                    case DynamicChatter.Kind.Text: started = TryTextFirst(out why); break;
                    default: started = TryThink(d, out why); break;
                }
            }
            catch (Exception e) { Log.Debug("dynamic chatter: " + e.Message); continue; }
            if (!started) continue;
            Log.Debug($"dynamic chatter: {kind} ({PeopleTalking()} of {s.DynamicMaxNpcs} people talking near you)");
            return;
        }
    }

    /// <summary>Dev channel: "chatter" = status of each conversation; "chatter now" = start one; anything else = a /chatter command.</summary>
    internal static string DevChatter(string arg)
    {
        var lines = new List<string>();
        if (arg.Trim().Length > 0) Command("/chatter " + arg, lines.Add);
        else
        {
            ChatterCommand(Array.Empty<string>(), lines.Add);
            foreach (var g in Groups)
                lines.Add($"- {g.Names}: {(g.Playing ? $"line {g.Next}/{g.Script?.Lines.Count}" : "being written")}{(g.Walker != null ? ", someone walking over" : "")}" +
                          $"{(g.PausedSince > 0 ? ", paused" : "")}; members held: {g.Members.Count(m => m.Held)}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>For /chatter now: start one right away (still within the group and hourly limits).</summary>
    private static string ChatterNow()
    {
        if (_chatter == null) return "Chatter isn't available.";
        var s = ChatterSettingsNow();
        if (Groups.Count >= Math.Clamp(Math.Max(1, s.MaxGroups), 1, ChatterSettings.MaxAtOnce)) return $"There's already {Groups.Count} conversation going (the limit is {s.MaxGroups}).";
        if (ChatterPlan.InLastHour(ChatterStarts, Time.unscaledTime) >= s.PerHour) return $"This hour's limit ({s.PerHour}) is used up. Raise it with /chatter hourly <n>.";
        return TryStartGroup(s, out var why) ? "Two people near you are about to start talking (close the console to watch)." : "Nobody to start a conversation: " + why;
    }

    // ------------------------------------------------------------------ who

    private sealed class Cand
    {
        public Character C = null!;
        public string Id = "";
        public Vector3 Pos;
        public bool Seated;
        /// <summary>The game's own talk group they're standing in (the game put them together to chat), if any.</summary>
        public IntPtr GameGroup;
        /// <summary>In front of you (roughly where you're looking): picked before people behind you.</summary>
        public bool InView;
        /// <summary>0.3.3: at work (a worker's task), their step at a food place ("" = not visiting one), which table.</summary>
        public bool Worker;
        public string VisitStep = "", Table = "";
        /// <summary>0.3.3: may be held for a call or a conversation (not at work, not mid-visit): ChatterPlan.MayHold.</summary>
        public bool Holdable = true;
    }

    /// <summary>Order for picking: people in front of you first, then the nearest.</summary>
    private static IEnumerable<Cand> FrontFirst(IEnumerable<Cand> cands, Vector3 me) =>
        cands.OrderByDescending(x => x.InView).ThenBy(x => Flat(x.Pos - me).magnitude);

    /// <summary>The farthest any chatter scan looks (<see cref="ChatterCandidates"/>: ChatterRange up to 60 m).</summary>
    private const float NearBodiesRange = 60f;
    private static readonly List<(Character c, IntPtr ptr, Vector3 pos)> NearBodiesNow = new();
    private static int _nearBodiesFrame = -1;

    /// <summary>
    /// Everyone alive within 60 m of you (flat), you left out, in the game's own order: read once a frame and shared (D2,
    /// PERF-2). A Dynamic start can look for gossip, a phone call, shop talk and a mutter in one frame, and each used to
    /// walk every body in the city, about 14 calls into the game per body before its range test. Here the position comes
    /// first and the range is plain arithmetic; each caller still tests its own range. Never changed by a caller. Everyone's
    /// position is as it was when first asked for in the frame.
    /// </summary>
    private static List<(Character c, IntPtr ptr, Vector3 pos)> NearBodies(PlayerCharacter pc)
    {
        int frame = Time.frameCount;
        if (frame == _nearBodiesFrame) return NearBodiesNow;
        _nearBodiesFrame = frame;
        NearBodiesNow.Clear();
        var all = Character.instances;
        if (all == null) return NearBodiesNow;
        Vector3 at;
        GameObject? myBody;
        try { at = pc.transform.position; myBody = pc.gameObject; }
        catch { return NearBodiesNow; }
        var it = all.GetEnumerator();
        while (it.MoveNext())
        {
            var c = it.Current;
            try
            {
                if (c is null || c.WasCollected) continue; // cheap checks first (a GC-handle lookup, not a call into the game's code)
                var pos = c.transform.position;
                if (Flat(pos - at).sqrMagnitude > NearBodiesRange * NearBodiesRange) continue;
                // As Alive and the old player test, for the few in range. The player is a PlayerCharacter, not a Character:
                // only its GameObject tells their body apart (a pointer can't).
                if (!c.isActiveAndEnabled || c.gameObject == myBody) continue;
                NearBodiesNow.Add((c, c.Pointer, pos));
            }
            catch { }
        }
        return NearBodiesNow;
    }

    /// <summary>People near the player who could talk: not held, not in a conversation, not children, not in the game's own dialogue.</summary>
    private static List<Cand> ChatterCandidates(ChatterSettings s)
    {
        var list = new List<Cand>();
        var pc = GameContext.PlayerCharacter();
        if (pc == null) return list;
        Vector3 me = pc.transform.position;
        Vector3 eye = me, look = Vector3.zero;
        try
        {
            var cam = GameContext.PlayerCamera();
            if (cam != null) { eye = cam.transform.position; look = Flat(cam.transform.forward).normalized; }
        }
        catch { }
        var inGroups = new HashSet<IntPtr>(Groups.SelectMany(g => g.Members).Where(m => m.C != null).Select(m => m.C.Pointer));
        float range = Mathf.Clamp(s.Range, 5f, 60f);
        foreach (var (c, ptr, pos) in NearBodies(pc))
        {
            try
            {
                if (Flat(pos - me).magnitude > range || c.Agent == null) continue;
                if (NpcHold.IsHeld(c) || inGroups.Contains(ptr)) continue;
                if (NpcTargeting.WhyNot(c, _cfg.ChatWithNamedNpcs.Value) != null) continue;
                string id = GameContext.NpcId(c);
                if (OnACall(c, id)) continue; // reserved: on a call, or the one being called
                if (MinorInfo(c, id).minor) continue;
                bool staff = false;
                try { staff = GameContext.PersonOf(c)?.RuntimeData?.PlayerHired == true; } catch { }
                if (staff) continue;
                if (Thinking(c) || InVenueTalk(c)) continue;
                IntPtr gameGroup = IntPtr.Zero;
                try { var grp = c.Communication != null ? c.Communication.Group : null; if (grp != null) gameGroup = grp.Pointer; } catch { }
                var toThem = Flat(pos - eye);
                bool inView = look == Vector3.zero || toThem.sqrMagnitude < 4f || Vector3.Dot(look, toThem.normalized) > 0.35f; // about 70° either side
                bool worker = false;
                string step = "", table = "";
                // CF-11: on shift counts too (staff between two jobs, whose game task this moment isn't a staff one).
                try { worker = GameContext.IsWorkerTask(c) || GameContext.OnShift(c); (step, table) = GameContext.VisitOf(c); } catch { }
                list.Add(new Cand
                {
                    C = c, Id = id, Pos = pos, Seated = GameContext.IsSeated(c), GameGroup = gameGroup, InView = inView,
                    Worker = worker, VisitStep = step, Table = table, Holdable = ChatterPlan.MayHold(worker, step),
                });
            }
            catch { }
        }
        return list;
    }

    /// <summary>
    /// Farther apart than <see cref="TalkDistance"/> and the one who starts it walks up to the other (to about a metre),
    /// up to <see cref="WalkUpTo"/>. With walking off, only people within <see cref="CloseEnough"/> talk, from where they are.
    /// </summary>
    private const float TalkDistance = 1.6f, CloseEnough = 3.2f, WalkUpTo = 8f, TableDistance = ChatterPlan.TableRadius;

    private static bool TryStartGroup(ChatterSettings s, out string why)
    {
        why = "";
        var cands = ChatterCandidates(s);
        if (cands.Count < 2) { why = "fewer than two people around"; return false; }
        // People the game itself gathered to chat (its talk spots) always go first.
        if (TryTable(s, cands, out why)) return true; // a table near you first (FB-18)
        var free = cands.Where(x => x.Holdable).ToList(); // never someone at work or mid-visit (0.3.3)
        if (s.ReplaceGameChatter && TryGatheredGroup(s, free, out why)) return true;
        return TryPair(s, free.Where(x => !x.Seated).ToList(), out why); // people at tables talk as their table (FB-18)
    }

    /// <summary>
    /// A group the game gathered at one of its talk spots gets this mod's conversation (everyone in it), in front of you
    /// and nearest first: they're already facing each other, and the game's own lines are silenced.
    /// </summary>
    private static bool TryGatheredGroup(ChatterSettings s, List<Cand> cands, out string why)
    {
        why = "";
        var pc0 = GameContext.PlayerCharacter();
        var me = pc0 != null ? pc0.transform.position : Vector3.zero;
        var gathered = cands.Where(x => x.GameGroup != IntPtr.Zero).GroupBy(x => x.GameGroup)
            .Select(g => FrontFirst(g.GroupBy(x => x.Id).Select(x => x.First()), me).ToList()).Where(g => g.Count >= 2)
            .OrderByDescending(g => g.Any(x => x.InView)).ThenBy(g => g.Min(x => Flat(x.Pos - me).magnitude)).ToList();
        if (gathered.Count == 0) { why = "no group the game gathered near you"; return false; }
        foreach (var grp in gathered.Take(3))
        {
            if (grp.Any(x => GameContext.IsBusyWorking(x.C))) continue;
            // CF-26: the whole group the game gathered, not only who's free here: with someone at work or on a visit in it (a
            // customer at a table, the waiter at it), it stays the game's, and their order or work goes on (a player's log:
            // a seated customer taken into a conversation, and the waiter never came back).
            string busy = "";
            try { var gg = grp[0].C.Communication != null ? grp[0].C.Communication.Group : null; if (gg != null) busy = GameChatter.ServiceMember(gg); } catch { busy = "a member who can't be read"; }
            if (busy.Length > 0) { Log.Debug($"chatter: the group with {grp[0].C.name} is the game's ({busy} in it)"); why = "the groups near you are the game's"; continue; }
            if (StartGroup(s, grp[0], grp[1], false, cands, out why, grp.Skip(2).ToList())) return true;
        }
        if (why.Length == 0) why = "the groups near you are busy";
        return false;
    }

    /// <summary>Two people near each other start talking (one walks over to the other): a table, two who know each other, or anyone.</summary>
    private static bool TryPair(ChatterSettings s, List<Cand> cands, out string why)
    {
        why = "";
        if (cands.Count < 2) { why = "fewer than two people around"; return false; }
        float reach = s.WalkUp ? WalkUpTo : CloseEnough;
        var byId = cands.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());

        var tables = new List<(Cand a, Cand b)>();
        for (int i = 0; i < cands.Count; i++)
            for (int j = i + 1; j < cands.Count; j++)
                if (cands[i].Seated && cands[j].Seated && cands[i].Id != cands[j].Id && Flat(cands[i].Pos - cands[j].Pos).magnitude <= TableDistance)
                    tables.Add((cands[i], cands[j]));
        var known = _pairs!.AmongThese(byId.Keys).Where(p => Flat(byId[p.A].Pos - byId[p.B].Pos).magnitude <= reach).ToList();

        // The inner-life round: two friends who just spotted each other (body language) get talking first, if they still can.
        if (_preferPair is { } hint && Time.unscaledTime < hint.until && byId.TryGetValue(hint.a, out var ha) && byId.TryGetValue(hint.b, out var hb))
        {
            _preferPair = null;
            // Never a customer mid-visit or anyone at work (Holdable): the game's own visits and shifts carry on untouched.
            if (Flat(ha.Pos - hb.Pos).magnitude <= reach && ha.Holdable && hb.Holdable && !ha.Seated && !hb.Seated
                && !GameContext.IsBusyWorking(ha.C) && !GameContext.IsBusyWorking(hb.C)
                && StartGroup(s, ha, hb, false, cands, out why)) return true;
        }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            var kind = ChatterPlan.Pick(ChatterRng, s, tables.Count > 0, known.Count > 0);
            Cand? a = null, b = null;
            if (kind == ChatterPlan.PairKind.Table)
            {
                var t = ChatterPlan.Weighted(ChatterRng, tables.Select(x => new Tuple<Cand, Cand>(x.a, x.b)).ToList(), x => PairBook.Weight(_pairs.Get(x.Item1.Id, x.Item2.Id)));
                if (t != null) (a, b) = (t.Item1, t.Item2);
            }
            else if (kind == ChatterPlan.PairKind.Known)
            {
                var p = ChatterPlan.Weighted(ChatterRng, known, PairBook.Weight);
                if (p != null) (a, b) = (byId[p.A], byId[p.B]);
            }
            else
            {
                // Someone near you starts it (in front of you, the nearest few), so it's a conversation you can see and hear.
                var pcN = GameContext.PlayerCharacter();
                var nearest = pcN != null ? FrontFirst(cands, pcN.transform.position).Take(4).ToList() : cands;
                a = nearest[ChatterRng.Next(nearest.Count)];
                var near = cands.Where(x => x != a && x.Id != a.Id && Flat(x.Pos - a.Pos).magnitude <= reach)
                    .OrderBy(x => Flat(x.Pos - a.Pos).magnitude).Take(3).ToList();
                b = near.Count > 0 ? near[ChatterRng.Next(near.Count)] : null;
                // people who don't get on mostly walk on by (never quite always)
                if (b != null && ChatterRng.NextDouble() > Math.Min(1.0, PairBook.Weight(_pairs.Get(a.Id, b.Id)))) b = null;
            }
            if (a == null || b == null) continue;
            if (GameContext.IsBusyWorking(a.C) || GameContext.IsBusyWorking(b.C)) continue;
            if (StartGroup(s, a, b, kind == ChatterPlan.PairKind.Table, cands, out why)) return true;
        }
        if (why.Length == 0) why = "no pair close enough, free and not working";
        return false;
    }

    private static bool StartGroup(ChatterSettings s, Cand a, Cand b, bool table, List<Cand> cands, out string why, List<Cand>? mates = null,
                                   string tableKey = "", HashSet<string>? justSat = null, string carryOn = "")
    {
        why = "";
        float dist = Flat(a.Pos - b.Pos).magnitude;
        // Someone standing walks up to the other (no shouting across a gap); the one sitting (or the anchor) stays put.
        if (b.Seated && !a.Seated) (a, b) = (b, a);
        bool walk = s.WalkUp && !b.Seated && dist > TalkDistance;
        if (walk && dist > WalkUpTo) { why = "they're too far apart"; return false; }
        if (!walk && dist > (table ? TableReach : a.Seated && b.Seated ? TableDistance : CloseEnough)) { why = "they're too far apart"; return false; }
        Vector3 spot = a.Pos + Flat(b.Pos - a.Pos).normalized * 1.1f;
        if (walk && PathBlocked(b.C, b.Pos, spot)) { why = "something's in the way"; return false; }

        // What they're doing, read before they're held (holding switches walkers to a standing idle).
        var before = new Dictionary<string, (NpcVisual visual, GameSnapshot snap)>
        {
            [a.Id] = (GameContext.Visual(a.C), Snapshot(a.C)),
            [b.Id] = (GameContext.Visual(b.C), Snapshot(b.C)),
        };
        var g = new Group { Table = table, StartedAt = Time.unscaledTime, TableKey = tableKey };
        // FB-19: now and then, someone at the table gets a call partway through (one request all the same).
        g.TableCall = table && Features.PhoneCalls && ChatterRng.Next(100) < Math.Clamp(_cfg.TableCallChance.Value, 0, 100);
        List<ChatterMember> members;
        List<string> headlines = new(), rumors = new();
        try
        {
            foreach (var c in new[] { a, b })
            {
                var head = GameContext.HeadOf(c.C);
                // A customer at a table mid-visit is held lightly: the game's own steps for them run on (0.3.3).
                bool light = table && !string.IsNullOrEmpty(c.VisitStep);
                if (!(light ? NpcHold.EngageLight(c.C, _cfg.AnimateNpc.Value) : NpcHold.EngageMember(c.C, _cfg.AnimateNpc.Value))) { ReleaseMembers(g); why = "someone was busy"; return false; }
                g.Members.Add(new Member { C = c.C, Id = c.Id, Head = head, Seated = c.Seated, Joined = true, Held = true, Light = light, Seat = c.Pos });
            }
            var ma = g.Members[0];
            var mb = g.Members[1];
            NpcHold.LookAtFor(ma.C, mb.Head);
            NpcHold.LookAtFor(mb.C, ma.Head);
            if (walk)
            {
                if (NpcHold.WalkTo(mb.C, spot, 0.15f, GameContext.TypicalWalkParam())) g.Walker = mb;
                else { ReleaseMembers(g); why = "they couldn't walk over"; return false; }
            }

            // Others nearby may join partway through (held only when their moment comes).
            var joinable = cands.Where(x => x.Id != a.Id && x.Id != b.Id && !x.Seated && x.Holdable && Flat(x.Pos - a.Pos).magnitude <= (s.WalkUp ? 7f : 3.5f))
                .GroupBy(x => x.Id).Select(x => x.First()).ToList();
            if (mates is { Count: > 0 })
            {
                // The rest of a group the game gathered (already standing there: in it from the first line on), or everyone else
                // at the table (FB-18), held from the start like the first two (review M1).
                foreach (var m in mates.Take(Math.Max(0, s.ClampedGroupSize - 2)))
                {
                    before[m.Id] = (GameContext.Visual(m.C), Snapshot(m.C));
                    if (!table) { g.Members.Add(new Member { C = m.C, Id = m.Id, Head = GameContext.HeadOf(m.C), JoinAt = 1 }); continue; }
                    bool lightly = !string.IsNullOrEmpty(m.VisitStep); // a customer mid-visit: the game's steps run on (0.3.3)
                    if (!(lightly ? NpcHold.EngageLight(m.C, _cfg.AnimateNpc.Value) : NpcHold.EngageMember(m.C, _cfg.AnimateNpc.Value))) { before.Remove(m.Id); continue; }
                    var held = new Member { C = m.C, Id = m.Id, Head = GameContext.HeadOf(m.C), Seated = m.Seated, Joined = true, Held = true, Light = lightly, Seat = m.Pos };
                    g.Members.Add(held);
                    NpcHold.LookAtFor(held.C, ma.Head);
                }
            }
            else if (!table) // nobody walks over to join a table (FB-18: the table is the group)
            {
                var seats = ChatterPlan.Roll(ChatterRng, s, a.Id, b.Id, joinable.Select(j => j.Id).ToList());
                foreach (var seat in seats.Skip(2))
                {
                    var j = joinable.First(x => x.Id == seat.Id);
                    before[j.Id] = (GameContext.Visual(j.C), Snapshot(j.C));
                    g.Members.Add(new Member { C = j.C, Id = j.Id, Head = GameContext.HeadOf(j.C), JoinAt = seat.JoinAt });
                }
            }

            members = g.Members.Select(m => new ChatterMember
            {
                Id = m.Id, Visual = before[m.Id].visual, Snap = before[m.Id].snap, JoinAt = m.JoinAt, MaxLines = s.ClampedLines,
                Seated = m.Seated, WalkedOver = m == g.Walker, JustSat = justSat?.Contains(m.Id) == true,
            }).ToList();
            var place = members[0].Snap;
            try { if (_cfg.CityNews.Value && _world != null) headlines = _world.RecentHeadlines(place.GameDay); } catch { }
            try { if (_cfg.Gossip.Value && _world != null) rumors = _world.RumorsFor(place.Location, place.GameDay, a.Id, ChatterRng); } catch { }
        }
        catch (Exception e)
        {
            ReleaseMembers(g); // never leave anyone frozen
            if (NpcHold.IsHeld(a.C) && !ChatNpcIs(a.C)) NpcHold.ReleaseMember(a.C); // held but not yet listed in the group
            if (NpcHold.IsHeld(b.C) && !ChatNpcIs(b.C)) NpcHold.ReleaseMember(b.C);
            why = "something went wrong";
            Log.Debug("chatter start: " + e);
            return false;
        }

        Groups.Add(g);
        if (!table) // CF-26 (a table's own talk is FB-34's); kept small here too, venue talk may be off (review CF26-3)
        {
            float t0 = Time.unscaledTime;
            if (PulledIn.Count > 200) foreach (var k in PulledIn.Where(kv => t0 - kv.Value > PulledInFor).Select(kv => kv.Key).ToList()) PulledIn.Remove(k);
            foreach (var m in g.Members) if (m.C != null) PulledIn[m.C.Pointer] = t0;
        }
        if (table) TableWatch.RemoveAll(w => g.Members.Any(m => m.Id == w.Id)); // the live part decides their goodbyes now (F1)
        NoteStart("talk");
        Log.Info($"Residents' conversation starting: {g.Names}{(table ? " (same table)" : walk ? $" (one walks over, {dist:0.0} m)" : "")}" +
                 $"{(g.Members.Count > 2 ? table ? $", {g.Members.Count} at the table" : $", {g.Members.Count - 2} may join" : "")}" +
                 $"{(carryOn.Length > 0 ? ", carrying on" : "")}{(justSat is { Count: > 0 } ? $", {justSat.Count} just sat down" : "")}.");
        var chatter = _chatter!;
        var ct = g.Cts.Token;
        int maxTokens = Math.Clamp(_cfg.MaxGenerationTokens.Value, 1024, 64000);
        var busyNow = g.TableCall ? BusyIds() : new HashSet<string>(); // the caller isn't someone already talking near you
        var rng = new System.Random(ChatterRng.Next());
        Task.Run(async () =>
        {
            ChatterScript? script = null;
            try
            {
                script = g.TableCall
                    ? await chatter.WriteTableCallAsync(members, headlines, rumors, maxTokens, busyNow, rng, ct, carryOn).ConfigureAwait(false)
                    : await chatter.WriteAsync(members, table, headlines, rumors, maxTokens, ct, carryOn).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log.Warn("A residents' conversation failed: " + e.Message); }
            MainThread.Enqueue(() =>
            {
                if (!Groups.Contains(g)) return;
                if (script == null) { EndGroup(g, "nothing written"); return; }
                g.Script = script;
                g.AboutPlayer = ChatterBrain.AboutThePlayer(script);
                if (script.Call?.Callee.Record is { } rung)
                {
                    // The one on the other end, now: busy elsewhere, or right here near you, isn't heard on a speaker (review M2).
                    bool elsewhere = Groups.Any(o => o != g && o.Members.Any(m => m.Id == rung.Id && !m.Gone)) || Calls.Any(k => k.Id == rung.Id || k.OtherId == rung.Id)
                                     || Thoughts.Any(t => t.Id == rung.Id) || (ChatOpen && _session?.Record.Id == rung.Id) || FindAround(rung.Id, ChatterSettingsNow()) != null;
                    if (elsewhere && script.Call.Speaker)
                    {
                        script.Call.Speaker = false;
                        foreach (var l in script.Lines) l.Speakerphone = false;
                        Log.Debug($"call at a table: {rung.Persona.Name} is around or busy, so not on speaker");
                    }
                }
                g.Playing = true;
                g.NextAt = Time.unscaledTime + 0.4f;
                for (int i = 0; i < Math.Min(2, script.Lines.Count); i++) PrefetchVoice(g, i);
            });
        });
        return true;
    }

    private static GameSnapshot Snapshot(Character c)
    {
        var snap = GameContext.Capture(c);
        snap.PlayerGender = _cfg.PlayerGender.Value;
        return snap;
    }

    /// <summary>A wall, table or railing between them at waist or chest height (other people don't count).</summary>
    private static bool PathBlocked(Character walker, Vector3 from, Vector3 to)
    {
        foreach (float h in new[] { 0.5f, 1.3f })
        {
            Vector3 p0 = from + Vector3.up * h, p1 = to + Vector3.up * h;
            var dir = p1 - p0;
            foreach (var hit in Physics.RaycastAll(p0, dir.normalized, dir.magnitude, ~0, QueryTriggerInteraction.Ignore))
            {
                var t = hit.collider != null ? hit.collider.transform : null;
                if (t == null || t.IsChildOf(walker.transform)) continue;
                if (t.GetComponentInParent<Character>() != null) continue;
                return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ playing it out

    private static void PrefetchVoice(Group g, int index)
    {
        if (g.Script == null || index >= g.Script.Lines.Count || g.Voices.ContainsKey(index)) return;
        var line = g.Script.Lines[index];
        // FB-19b: the other end of a call is voiced only when it's on speaker, and only for a resident (they have a voice).
        NpcRecord? rec;
        if (line.FarEnd) { rec = line.Speakerphone ? g.Script.Call?.Callee.Record : null; if (rec == null || _brain.IsKnownMinor(rec)) return; }
        else if (!g.Script.People.TryGetValue(line.SpeakerId, out rec)) return;
        // Too far to hear them: nothing is sent to the voice provider (asked again at their turn, if you've come closer).
        var who = g.Members.FirstOrDefault(m => m.Id == (line.FarEnd ? g.Script.Call!.HolderId : line.SpeakerId));
        var s = ChatterSettingsNow();
        if (who?.C == null || !WithinVoice(who.C, s, s.VoiceBeyond + 5f)) return;
        var ct = g.Cts.Token;
        if (line.FarEnd)
        {
            // Through the phone speaker: filtered here, off the game's thread (review L3).
            g.Voices[index] = Task.Run(async () =>
            {
                var o = await VoiceForAsync(() => { var v = VoiceSubject.From(rec); v.Tone = line.Tone; return v; }, line.Text, ct, "chatter").ConfigureAwait(false); // a call at a table: part of that table's talk start
                if (o.Audio != null) o.Audio = PhoneSpeaker.Filter(o.Audio);
                return o;
            });
            return;
        }
        g.Voices[index] = Task.Run(() => VoiceForAsync(() => { var v = VoiceSubject.From(rec); v.Tone = line.Tone; return v; }, line.Text, ct, "chatter"));
    }

    /// <summary>
    /// FB-19: a line from the other end of a call at a table. On speaker (b), it's heard by everyone: their voice through the
    /// phone (PhoneSpeaker), from the one holding it, quieter, with the phone icon over them. Otherwise only the one on the
    /// phone hears it: the table waits about as long as it takes to say.
    /// </summary>
    private static void TickFarEnd(Group g, ChatterLine line, ChatterSettings s, float now)
    {
        var call = g.Script!.Call;
        var holder = call != null ? g.Members.FirstOrDefault(m => m.Id == call.HolderId && !m.Gone) : null;
        if (call == null || holder?.C == null || !line.Speakerphone)
        {
            g.NextAt = now + Mathf.Clamp(1.0f + line.Text.Length / 16f, 1.4f, 6f);
            g.Next++;
            PrefetchVoice(g, g.Next);
            return;
        }
        if (ChatNpcIs(holder.C)) { g.NextAt = now + 0.5f; return; } // you walked up to them: their answer to you first (review L1)
        if (!g.Voices.ContainsKey(g.Next)) PrefetchVoice(g, g.Next);
        if (g.Voices.TryGetValue(g.Next, out var vt) && !vt.IsCompleted)
        {
            if (g.WaitFor != g.Next) { g.WaitFor = g.Next; g.WaitSince = now; }
            if (now - g.WaitSince < VoiceTimeout + 5f) return;
            vt = null;
        }
        VoiceOutcome? voice = null;
        try { voice = vt != null && vt.IsCompletedSuccessfully ? vt.Result : null; } catch { }
        float seconds = 0f;
        if (voice?.Audio != null)
        {
            seconds = VoicePlayer.Play(holder.C, voice.Audio, LineVolume(call.Callee.Record?.Id ?? holder.Id, overheard: true) * 0.8f, VoiceReach(s) * 0.6f, 2f); // filtered already
        }
        float shown = Mathf.Max(seconds, Mathf.Clamp(1.8f + line.Text.Length / 15f, 2.2f, 8f));
        // A name over the bubble only once you know them; someone from the caller's own life has none (review L2).
        Ui.ShowBark(call.Callee.Record != null ? LabelFor(call.Callee.Record) : "", line.Text, holder.Head,
            shown + 0.6f, holder.Id, phone: true, maxDistance: s.TextDistance, body: holder.C.transform);
        if (_transcripts != null && (call.Callee.Record == null || !ActiveBrain.IsKnownMinor(call.Callee.Record)))
            RecordLine("table call (on speaker)", $"talk:{g.StartedAt:0.0}", null, call.Callee.Record?.Id ?? "", call.Callee.Record, line.Text,
                g.Script.People.TryGetValue(holder.Id, out var held) ? held.Persona.Name : "", line.Mood, line.Tone, "", voice, seconds,
                where: g.Script.Persons.FirstOrDefault()?.Snap, speakerName: call.Callee.Name);
        Log.Debug($"call at a table, on speaker {g.Next + 1}/{g.Script.Lines.Count}: {(seconds > 0 ? $"voiced, {seconds:0.0} s" : "no voice")}");
        g.NextAt = now + shown + 0.35f;
        g.Next++;
        PrefetchVoice(g, g.Next);
    }

    /// <summary>How long a conversation waits while you're in the console or a menu before they give up on it.</summary>
    private const float PauseLimitSeconds = 300f;

    private static void TickGroup(Group g, ChatterSettings s, bool paused)
    {
        float now = Time.unscaledTime;
        foreach (var m in g.Members)
            if (m.Held && !m.Gone && m.LeaveAt > 0 && now >= m.LeaveAt) { NpcHold.ReleaseMember(m.C); m.Held = false; m.Gone = true; }
        // gone, or their body handed to someone else by the game (out of sight)
        if (g.Members.Any(m => m.Held && (!Alive(m.C) || !NpcHold.IsHeld(m.C)))) { EndGroup(g, "someone left the area"); return; }
        // 0.3.3: a customer at the table (held lightly, so the game's visit runs on) who gets up leaves the conversation,
        // saying goodbye the way it went for them (nothing if badly); the others carry on, or it's over with fewer than two.
        if (g.Table && Time.unscaledTime >= g.NextLeaveCheck)
        {
            g.NextLeaveCheck = Time.unscaledTime + 0.5f;
            foreach (var m in g.Members.Where(m => m.Light && m.Held && !m.Gone).ToList())
            {
                // The game handed their body to someone else (audit A3: a light hold keeps no ghost, so nothing else notices).
                bool someoneElse = GameContext.NpcId(m.C) != m.Id;
                string step = someoneElse ? "" : GameContext.VisitOf(m.C).step;
                bool up = someoneElse || step == "Leaving" || (!GameContext.IsSeated(m.C) && Flat(m.C.transform.position - m.Seat).magnitude > 0.6f);
                if (!up) continue;
                int how = g.Script != null ? ChatterBrain.HowItWent(g.Script, m.Id) : 0;
                TableWatch.RemoveAll(w => w.Id == m.Id); // this goodbye is the one (an earlier part's watch would say a second: final review F1)
                LetGo(m);
                if (!someoneElse && how >= 0 && g.Script != null && g.Next > 0) SayWritten(m.C, m.Id, Pick(how > 0 ? WarmPartings : PlainPartings), s);
                Log.Debug($"table: {m.C.name} {(someoneElse ? "is someone else now (the game reused the body)" : $"got up ({(step.Length > 0 ? step : "standing")})")} and left the conversation");
            }
            if (g.Members.Count(m => m.Joined && !m.Gone) < 2) { EndGroup(g, "the others left the table"); return; }
        }
        var here = g.Members.FirstOrDefault(m => m.Held && !m.Gone);
        if (here == null) { EndGroup(g, "everyone's gone"); return; }
        var pc = GameContext.PlayerCharacter();
        if (pc != null)
        {
            // Walked out of the voiced range: they wrap it up (far, far away: it's simply over).
            float nearest = g.Members.Where(m => m.Held && !m.Gone && Alive(m.C))
                .Select(m => Flat(m.C.transform.position - pc.transform.position).magnitude).DefaultIfEmpty(float.MaxValue).Min();
            if (nearest > s.WrapUpDistance + 40f) { EndGroup(g, "you're far away now"); return; }
            if (!g.WrappingUp && nearest > s.WrapUpDistance && WrapUp(g)) return;
        }
        if (!g.Playing)
        {
            if (now - g.StartedAt > 90f) EndGroup(g, "took too long to write");
            return;
        }
        if (paused)
        {
            if (g.PausedSince < 0) g.PausedSince = now;
            else if (now - g.PausedSince > PauseLimitSeconds) EndGroup(g, "paused too long");
            return;
        }
        g.PausedSince = -1f;
        if (g.Walker != null && NpcHold.IsWalking(g.Walker.C)) return; // they start once the walker arrives
        if (g.Walker != null)
        {
            bool failed = NpcHold.WalkFailed(g.Walker.C);
            float gap = Flat(g.Walker.C.transform.position - g.Members[0].C.transform.position).magnitude;
            Log.Debug($"walk-over {(failed ? "gave up" : "arrived")}: {gap:0.0} m apart.");
            g.Walker = null;
            if (failed && gap > 2.5f) { EndGroup(g, "couldn't get close enough"); return; } // nobody talks across a gap
        }
        if (now < g.NextAt) return;
        if (ServingTable(g, now)) return; // FB-34: staff are taking an order (or bringing a meal) at the table: it waits

        var script = g.Script!;
        if (g.Next >= script.Lines.Count) { EndGroup(g, "finished"); return; }
        var line = script.Lines[g.Next];
        if (line.FarEnd) { TickFarEnd(g, line, s, now); return; }
        var speaker = g.Members.FirstOrDefault(m => m.Id == line.SpeakerId);
        if (speaker == null || speaker.Gone) { g.Next++; return; }
        if (!speaker.Joined && !Join(g, speaker))
        {
            speaker.Gone = true; // they wandered off before their turn: their lines are skipped
            g.Next++;
            return;
        }
        if (speaker.C != null && NpcHold.IsWalking(speaker.C)) return; // a joiner still on their way over
        // You walked up and they brushed you off (FLOW-4): their next line waits until that window closes, so neither
        // cuts the other off on their one voice.
        if (speaker.C != null && ChatNpcIs(speaker.C)) { g.NextAt = now + 0.5f; return; }

        bool hearable = WithinVoice(speaker.C!, s, s.VoiceBeyond);
        if (hearable && !g.Voices.ContainsKey(g.Next)) PrefetchVoice(g, g.Next);
        if (g.Voices.TryGetValue(g.Next, out var vt) && !vt.IsCompleted)
        {
            if (!hearable) vt = null; // you've walked off: don't wait for it
            else
            {
                if (g.WaitFor != g.Next) { g.WaitFor = g.Next; g.WaitSince = now; }
                if (now - g.WaitSince < VoiceTimeout + 5f) return; // waiting for their voice
                vt = null; // it never came: they say it without one
            }
        }
        VoiceOutcome? voice = null;
        try { voice = vt != null && vt.IsCompletedSuccessfully ? vt.Result : null; } catch { }

        // Heard within the voice distance, fading with distance; voiced a little past it too (silent there until you come closer).
        float reach = VoiceReach(s);
        float volume = LineVolume(speaker.Id, overheard: true);
        float seconds = voice?.Audio != null ? VoicePlayer.Play(speaker.C!, voice.Audio, volume, reach, 3f) : 0f;
        float shown = Mathf.Max(seconds, Mathf.Clamp(1.8f + line.Text.Length / 15f, 2.2f, 8f));
        if (voice?.Error != null) Problem($"No voice for a resident's line: {voice.Error.TrimEnd('.')}.", "VOICE-FAIL", log: false);
        try
        {
            float away = pc != null ? Flat(speaker.C!.transform.position - pc.transform.position).magnitude : -1f;
            Log.Debug($"chatter line {g.Next + 1}/{script.Lines.Count}: {(seconds > 0 ? $"voiced, {seconds:0.0} s" : voice?.Error != null ? "no voice (" + voice.Error + ")" : !_voices.Enabled ? "voices off" : !hearable ? "not voiced (too far to hear)" : "no voice (not ready in time)")}, " +
                      $"{away:0} m from you (heard up to {reach:0} m{(seconds > 0 && away > reach ? ", silent until you come closer" : "")}){(line.Leaving ? ", their goodbye" : "")}");
        }
        catch { }
        Ui.ShowBark(BubbleName(script, speaker.Id), line.Text, speaker.Head, shown + 0.6f, speaker.Id, phone: line.Phone, maxDistance: s.TextDistance, body: speaker.C!.transform);
        // Their body follows how this line is said (the AI's tone word for it), else their mood (CF-04a), set before the line's
        // own gesture so a cued one (laughing, arguing) is what shows (review M3).
        string lineMood = line.Mood.Length > 0 ? line.Mood : script.People.TryGetValue(speaker.Id, out var rec) ? Npc.Features.Mood(rec.State.Mood) : ""; // their saved mood only with moods on (FB-51)
        if (lineMood.Length > 0) NpcHold.SetMoodFor(speaker.C!, lineMood);
        NpcHold.SpeakAs(speaker.C!, line.Text, seconds, line.Gesture);
        // Someone standing nearby may catch it (FB-05), unless the conversation is about the player anywhere.
        if (!g.AboutPlayer)
            try
            {
                string speakerName = script.People.TryGetValue(speaker.Id, out var said) ? said.Persona.Name : "";
                Overhear(g.Heard, speaker.C!, speaker.Id, speakerName, line.Text, g.Members.Select(m => m.Id).ToList(),
                    script.People.Values.Select(r => r.State.PlayerName), script.Persons.FirstOrDefault()?.Snap.PlayerVenues ?? new List<string>());
            }
            catch (Exception e) { Log.Debug("overhearing: " + e.Message); }
        // Everyone faces whoever's talking; the speaker faces who they're talking to (or the last person who spoke).
        var to = g.Members.FirstOrDefault(m => m.Id == line.ToId && m.Joined && !m.Gone && m != speaker)
                 ?? g.Members.FirstOrDefault(m => m.Joined && !m.Gone && m != speaker);
        foreach (var m in g.Members.Where(m => m.Joined && !m.Gone && m.Held && m != speaker))
        {
            NpcHold.LookAtFor(m.C, speaker.Head);
            NpcHold.ReactTo(m.C, line.Text);
        }
        if (to != null) NpcHold.LookAtFor(speaker.C!, to.Head);
        if (_transcripts != null)
        {
            script.People.TryGetValue(speaker.Id, out var saidBy);
            string toName = to != null && script.People.TryGetValue(to.Id, out var heard) ? heard.Persona.Name : "";
            RecordLine(script.Call != null ? "table call" : g.Table ? "table" : "conversation", $"talk:{g.StartedAt:0.0}", speaker.C, speaker.Id, saidBy, line.Text, toName,
                line.Mood, line.Tone, line.Gesture, voice, seconds, where: script.Persons.FirstOrDefault()?.Snap,
                relationship: to != null ? PairRelationship(speaker.Id, to.Id) : null);
        }

        g.NextAt = Time.unscaledTime + shown + 0.35f + (float)ChatterRng.NextDouble() * 0.5f;
        // Someone who asked something on their way out hears the answer first, then goes.
        foreach (var m in g.Members.Where(m => m.LeaveAfterReply && m != speaker)) { m.LeaveAfterReply = false; m.LeaveAt = g.NextAt; }
        if (line.Leaving)
        {
            if (line.Text.TrimEnd(' ', '"', '\'', ')', '…', '.').EndsWith("?")) speaker.LeaveAfterReply = true;
            else speaker.LeaveAt = g.NextAt; // they head off after their goodbye
        }
        g.Next++;
        for (int i = g.Next; i < g.Next + 2; i++) PrefetchVoice(g, i);
    }

    /// <summary>
    /// Close enough to voice them: within VoiceDistance plus <paramref name="margin"/> (VoiceBeyond for a line starting now,
    /// a few metres more for voices asked for ahead). VoiceDistance 0 = no voices.
    /// </summary>
    private static bool WithinVoice(Character c, ChatterSettings s, float margin = 0f)
    {
        if (s.VoiceDistance <= 0f) return false;
        try
        {
            var pc = GameContext.PlayerCharacter();
            return pc == null || Vector3.Distance(pc.transform.position, c.transform.position) <= s.VoiceDistance + margin;
        }
        catch { return true; }
    }

    /// <summary>How far an overheard line carries: silent from the voice distance on (never less than 3 m).</summary>
    private static float VoiceReach(ChatterSettings s) => Mathf.Max(3f, s.VoiceDistance);

    /// <summary>
    /// You walked out of the voiced range: the one due to speak next gives a reason they need to go (written with the
    /// conversation, from their own life), the others say goodbye, and they go their ways. Not started yet: it's dropped
    /// (a conversation still being written is cancelled). True when it ended right away.
    /// </summary>
    private static bool WrapUp(Group g)
    {
        g.WrappingUp = true;
        if (g.Table) { EndGroup(g, "you walked away (at a table they just stop: no goodbyes)"); return true; } // review M2
        if (!g.Playing || g.Script == null)
        {
            try { g.Cts.Cancel(); } catch { }
            EndGroup(g, "you walked away before they started");
            return true;
        }
        if (g.Walker != null || g.Next == 0) { EndGroup(g, "you walked away before anyone spoke"); return true; }
        var script = g.Script;
        var present = g.Members.Where(m => m.Joined && !m.Gone && m.Held && m.LeaveAt <= 0).ToList();
        if (present.Count < 2) { EndGroup(g, "you walked away"); return true; }
        string nextId = g.Next < script.Lines.Count ? script.Lines[g.Next].SpeakerId : "";
        var leaver = present.FirstOrDefault(m => m.Id == nextId) ?? present[ChatterRng.Next(present.Count)];
        // The voices fetched ahead were for the lines being replaced (FLOW-6): dropped, so the new ones are voiced afresh.
        foreach (var k in g.Voices.Keys.Where(k => k >= g.Next).ToList()) g.Voices.Remove(k);
        if (g.Next < script.Lines.Count) script.Lines.RemoveRange(g.Next, script.Lines.Count - g.Next);
        string reason = script.LeaveLine(leaver.Id, ChatterRng);
        Voice.ToneState? LastTone(string id) => script.Lines.LastOrDefault(l => l.SpeakerId == id)?.Tone; // they sound as they did a moment ago
        script.Lines.Add(new ChatterLine { SpeakerId = leaver.Id, Text = reason, Leaving = true, Tone = LastTone(leaver.Id) });
        foreach (var o in present.Where(m => m != leaver).Take(2))
            script.Lines.Add(new ChatterLine { SpeakerId = o.Id, ToId = leaver.Id, Text = script.ByeLine(o.Id, ChatterRng), Leaving = true, Tone = LastTone(o.Id) });
        Log.Debug($"wrap-up ({g.Names}): you walked off; {leaver.C.name} says: {reason}");
        return false;
    }

    /// <summary>A joiner's moment: if they're still around and free, they're held and walk up to the group.</summary>
    private static bool Join(Group g, Member m)
    {
        if (!Alive(m.C) || TalkingNow(m.C, m.Id) || GameContext.IsBusyWorking(m.C)) return false; // busy by now (B2): they don't join
        if (!ChatterPlan.MayHold(GameContext.IsWorkerTask(m.C), GameContext.VisitOf(m.C).step)) return false; // at work or mid-visit now (0.3.3)
        var anchor = g.Members[0];
        var pos = m.C.transform.position;
        var centre = g.Members.Where(x => x.Joined && !x.Gone).Select(x => x.C.transform.position).Aggregate(Vector3.zero, (acc, p) => acc + p)
                     / Math.Max(1, g.Members.Count(x => x.Joined && !x.Gone));
        float dist = Flat(pos - centre).magnitude;
        if (dist > 9f) return false;
        if (!NpcHold.EngageMember(m.C, _cfg.AnimateNpc.Value)) return false;
        m.Held = true;
        m.Joined = true;
        m.Seated = NpcHold.IsSeatedHold(m.C);
        NpcHold.LookAtFor(m.C, anchor.Head);
        if (dist > TalkDistance && !m.Seated)
        {
            var spot = centre + Flat(pos - centre).normalized * 1.3f;
            if (!PathBlocked(m.C, pos, spot)) NpcHold.WalkTo(m.C, spot, 0.15f, GameContext.TypicalWalkParam());
        }
        return true;
    }

    /// <summary>The name over their bubble: only if you know it (they told you, or the game shows it); otherwise none.</summary>
    private static string BubbleName(ChatterScript script, string id)
    {
        if (!script.People.TryGetValue(id, out var rec)) return "";
        string shown = DisplayName(rec);
        return shown == "Stranger" ? "" : Prompts.FirstName(shown);
    }

    /// <summary>Said their goodbye (or asked something on their way out): they go after the next line.</summary>
    private static bool Leaving(Member m) => m.LeaveAt > 0 || m.LeaveAfterReply;

    /// <summary>One member goes now and the others carry on: their bubble and their line stop, and they're let go.</summary>
    private static void LetGo(Member m)
    {
        try { Ui.HideBark(m.Id); } catch { }
        VoicePlayer.Stop(m.C);
        VoicePlayer.Release(m.C);
        if (m.Held) { try { NpcHold.ReleaseMember(m.C); } catch { } m.Held = false; }
        m.Gone = true;
    }

    private static void ReleaseMembers(Group g)
    {
        foreach (var m in g.Members)
            if (m.Held) { try { NpcHold.ReleaseMember(m.C); } catch { } m.Held = false; }
    }

    private const string ResetReason = "everyone was reset";
    /// <summary>The mod stopping (a hot reload or quitting): voices are stopped already, nothing new starts (audit A6).</summary>
    private const string StopReason = "the mod is stopping";

    /// <summary>
    /// The mouth of someone who isn't held, moving while their line is said (CF-04), and back to the game's own pace after
    /// (audit A4/A5): held people get theirs from their hold.
    /// </summary>
    private static void MouthFor(Character c, string text, float seconds)
    {
        NpcHold.Mouth(c, text, seconds);
        Later(Mathf.Max(seconds, 2f) + 0.3f, () => { if (Alive(c) && !NpcHold.IsHeld(c)) NpcHold.MouthAtOwnPace(c); });
    }

    private static void EndGroup(Group g, string why)
    {
        try { g.Cts.Cancel(); } catch { }
        try { NoteTableEnd(g, why); } catch (Exception e) { Log.Debug("table part: " + e.Message); } // FB-18
        // They remember what was actually said (nothing, if it broke up before anyone spoke; nothing after a reset).
        if (why != ResetReason && g.Script != null && g.Next > 0 && _chatter != null)
        {
            var script = g.Script;
            int played = Math.Min(g.Next, script.Lines.Count);
            var chatter = _chatter;
            Task.Run(async () =>
            {
                try { await chatter.RememberAsync(script, played).ConfigureAwait(false); }
                catch (Exception e) { Log.Debug("remembering a conversation: " + e.Message); }
            });
            RememberOverheard(g.Heard, script.Persons.FirstOrDefault()?.Snap ?? new GameSnapshot()); // only what was said before it ended
            try
            {
                // The inner-life round: people near them saw how it went (a row, a laugh, a breakup), where they stood.
                var at = g.Members.Where(m => m.C != null && Alive(m.C)).Select(m => m.C.transform.position).ToList();
                if (at.Count > 0) WitnessConversation(script, played, at.Aggregate(Vector3.zero, (s, p) => s + p) / at.Count);
            }
            catch (Exception e) { Log.Debug("witnessing a conversation: " + e.Message); }
            if (script.Call != null)
                Task.Run(async () =>
                {
                    try { await chatter.RememberTableCallAsync(script, played).ConfigureAwait(false); }
                    catch (Exception e) { Log.Debug("remembering a call at a table: " + e.Message); }
                });
        }
        if (why != "finished")
            foreach (var m in g.Members.Where(m => m.Joined && !m.Gone)) // broken up: nobody carries on talking over you (anyone who left is elsewhere now)
            {
                try { Ui.HideBark(m.Id); } catch { }
                VoicePlayer.Stop(m.C);
            }
        foreach (var m in g.Members) VoicePlayer.Release(m.C); // their voices go once the last line has played
        // FB-62: a friend may go along with one of them who's off to eat (not after a reset, or when you broke it up).
        var together = why is "finished" ? g.Members.Where(m => m.Joined && m.C != null).Select(m => (m.C, m.Id)).ToList() : null;
        ReleaseMembers(g);
        if (together != null) try { MaybeGoTogether(together); } catch (Exception e) { Log.Debug("going together: " + e.Message); }
        Groups.Remove(g);
        Log.Debug($"Residents' conversation over ({why}): {g.Names}");
    }

    private static void EndAllChatter(string why)
    {
        EndAllVenueTalk();
        foreach (var g in Groups.ToList()) EndGroup(g, why);
        foreach (var call in Calls.ToList()) EndCall(call, why);
        foreach (var t in Thoughts.ToList()) EndThought(t, why);
        ForgetTables(); // after the ends above, which note their parts (code review 5)
        VenueHandled.Clear();
    }

    /// <summary>
    /// The player wants to talk to someone in a residents' conversation, a call, a thought or shop talk: theirs breaks up
    /// first. Only a conversation they're actually in (joined, not gone) ends; someone due to join later just won't, and
    /// someone who has said their goodbye goes now while the others carry on (B3).
    /// </summary>
    private static void BreakUpChatterWith(Character c)
    {
        string id = "";
        try { id = GameContext.NpcId(c); } catch { }
        foreach (var g in Groups.ToList())
        {
            var member = g.Members.FirstOrDefault(m => Same(m.C, c));
            if (member == null || member.Gone) continue; // not in it, or already gone: the others carry on
            if (!member.Joined) member.Gone = true; // due to join later: they won't now (their lines are skipped)
            else if (Leaving(member)) LetGo(member); // on their way out already: they turn to you, the others carry on
            else EndGroup(g, "you started talking to one of them");
        }
        foreach (var call in Calls.ToList())
            if (Same(call.C, c) || Same(call.OtherC, c)
                || (id.Length > 0 && (call.OtherId == id || (call.Script is { Failed: false } sc && sc.Other.Record?.Id == id))))
                EndCall(call, "you started talking to them"); // they hang up for you (also on someone left unclaimed: busy near you; not a call they never answered)
        foreach (var t in Thoughts.ToList())
            if (Same(t.C, c)) EndThought(t, "you started talking to them");
        foreach (var t in VenueTalks.ToList()) // shop talk with them stops at once (FLOW-1): no late line over your chat
            if (Same(t.Staff, c) || Same(t.Customer, c)) EndVenueTalk(t, cancel: true);
    }
}
