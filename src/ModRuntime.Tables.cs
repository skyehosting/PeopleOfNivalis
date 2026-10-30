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
/// FB-18 (the author: "if a table has 2 or more people, those that share a table will be considered a group"): people
/// sharing a table talk as one group, a table near you goes first, the talk carries on in parts (two in a row, then the
/// table rests), and when someone gets up to leave they say goodbye the way the chat went for them.
/// </summary>
public static partial class ModRuntime
{
    /// <summary>What happened at a table lately: parts in a row, when the last ended, what it was about, who was in it.</summary>
    private sealed class TablePart
    {
        public int InARow;
        public float LastEnd = -10000f, RestUntil = -1f;
        public string Summary = "";
        public HashSet<string> Members = new();
    }

    /// <summary>Someone from a table conversation, watched for a while: when they get up, they say goodbye (by how it went).</summary>
    private sealed class TableLeaver
    {
        public Character C = null!;
        public string Id = "", TableKey = "";
        public IntPtr Body;
        public int How;
        public Vector3 Seat;
        public float Until, StoodSince = -1f;
        public long Epoch;
    }

    /// <summary>A table chosen for a conversation.</summary>
    private sealed class TablePick
    {
        public List<Cand> People = new();
        public string Key = "";
        public bool InView;
        public HashSet<string> JustSat = new();
        public string CarryOn = "";
        /// <summary>How many places in the pool it needs: everyone at it (up to ChatterGroupSize); never split (code review 1).</summary>
        public int Needed => People.Count;
    }

    private static readonly Dictionary<string, TablePart> TableParts = new();
    private static readonly List<TableLeaver> TableWatch = new();
    private static readonly List<(float at, Character c, string id, string text)> PendingByes = new();
    private static float _tableWaitSince = -1f, _tablesSkippedUntil = -1f, _nextTableWatch;

    /// <summary>A pause between two parts at one table; the rest after two in a row (review H1); how long a part is remembered.</summary>
    private const float TablePause = 12f, TableRest = 240f, TableMemory = 300f, TableWatchFor = 240f;
    /// <summary>The farthest two people at one table can be and still talk across it (a long table, by the game's table id).</summary>
    private const float TableReach = 4.5f;

    private static readonly string[] WarmPartings =
    {
        "Nice chatting. I've got to head off.", "Good talking to you. I should get going.", "Right, I'm off. This was nice.",
        "Gotta run. Good catching up.", "I'd better go. Nice seeing you.",
    };
    private static readonly string[] PlainPartings = { "Right, I should get moving.", "I've got to get going.", "Anyway, I'd better go." };
    private static readonly string[] TableByes = { "See you.", "Take care.", "Later.", "See you around." };

    /// <summary>The key a table's parts are kept under: the game's own table when known, else who's sitting there.</summary>
    private static string TableKeyFor(string table, IEnumerable<string> ids) =>
        table.Length > 0 ? "t:" + table : "p:" + string.Join("|", ids.OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>The last parts at this table: by its key, or (no table id) one where two of these people sat together lately.</summary>
    private static TablePart? PartFor(string key, IEnumerable<string> ids, float now)
    {
        if (TableParts.TryGetValue(key, out var part)) return part;
        if (key.StartsWith("t:", StringComparison.Ordinal)) return null;
        var set = ids.ToHashSet();
        return TableParts.Values.FirstOrDefault(p => now - p.LastEnd < TableMemory && p.Members.Count(set.Contains) >= 2);
    }

    /// <summary>At one of your own places (order talk and table talk leave your customers alone).</summary>
    private static bool AtYourPlace(Character c)
    {
        try { var place = c.Agent != null ? c.Agent.VisitingLocale : null; return place != null && GameContext.PlayersPlace(place); }
        catch { return false; }
    }

    /// <summary>
    /// The tables near you that could talk, best first: two or more people sitting together (by the game's table, else
    /// everyone within reach of everyone else), at a food place only people eating (nobody waiting to order or for their food
    /// is held), never at your own place, never someone at work; not resting, not right after its last part, and not a table
    /// already talking (its other people stay out: a table is never split into two conversations, code review 1). In front
    /// of you first, then one that was just talking, then the nearest. Each with everyone at it, up to ChatterGroupSize.
    /// </summary>
    private static List<TablePick> FindTables(ChatterSettings s, List<Cand> cands)
    {
        var picks = new List<(TablePick pick, int score, float dist)>();
        float now = Time.unscaledTime;
        var pc = GameContext.PlayerCharacter();
        // FB-45: table conversations off: no table is ever found (and nothing waits for one).
        if (pc == null || !Features.TableConversations) return new List<TablePick>();
        Vector3 me = pc.transform.position;
        var seated = new List<(Cand c, string table)>();
        foreach (var x in cands)
        {
            if (!x.Seated || x.Worker) continue; // staff sitting down on the job aren't a table (review L9)
            try
            {
                // At a food place: waiting to order, waiting for food or eating (held lightly: their visit runs on, 0.3.3);
                // never at your own place.
                if (!ChatterPlan.TableStep(x.VisitStep) || (x.VisitStep.Length > 0 && AtYourPlace(x.C))) continue;
                seated.Add((x, x.Table));
            }
            catch { }
        }
        if (seated.Count < 2) return new List<TablePick>();
        // Tables already talking: by their key, and anyone sitting within reach of someone in a table conversation.
        var talking = Groups.Where(g => g.Table && !g.WrappingUp).ToList();
        var busyKeys = talking.Select(g => g.TableKey).Where(k => k.Length > 0).ToHashSet();
        var busySeats = talking.SelectMany(g => g.Members.Where(m => !m.Gone && m.C != null)).Select(m => { try { return m.C.transform.position; } catch { return Vector3.positiveInfinity; } }).ToList();
        // ...and anyone at a table being served right now (final review F3: the table would be split, two talks at once).
        foreach (var vt in VenueTalks.Where(v => !v.Cancelled && v.Table == null))
            try { if (Alive(vt.Customer) && GameContext.IsSeated(vt.Customer)) busySeats.Add(vt.Customer.transform.position); } catch { }
        foreach (var g in ChatterPlan.Tables(seated.Select(t => (t.c.Pos.x, t.c.Pos.z, t.table)).ToList(), ChatterPlan.TableRadius))
        {
            var people = g.Select(k => seated[k].c).GroupBy(c => c.Id).Select(c => c.First()).ToList();
            if (people.Count < 2) continue;
            string key = TableKeyFor(seated[g[0]].table, people.Select(p => p.Id));
            if (busyKeys.Contains(key) || people.Any(p => busySeats.Any(b => Flat(b - p.Pos).magnitude <= ChatterPlan.TableRadius))) continue;
            var part = PartFor(key, people.Select(p => p.Id), now);
            if (part != null && (now < part.RestUntil || now - part.LastEnd < TablePause)) continue;
            bool inView = people.Any(p => p.InView);
            bool recent = part != null && now - part.LastEnd < TableMemory;
            int score = (inView ? 2 : 0) + (recent ? 1 : 0);
            float dist = people.Min(p => Flat(p.Pos - me).magnitude);
            // The two sitting closest, then whoever's nearest them, as many as there's room for.
            (Cand a, Cand b) pair = (people[0], people[1]);
            float close = float.MaxValue;
            for (int i = 0; i < people.Count; i++)
                for (int j = i + 1; j < people.Count; j++)
                {
                    float d = Flat(people[i].Pos - people[j].Pos).magnitude;
                    if (d < close) { close = d; pair = (people[i], people[j]); }
                }
            if (close > TableReach) continue; // too far apart to talk across (a long table): never tried (code review 2)
            var mid = (pair.a.Pos + pair.b.Pos) * 0.5f;
            var picked = new List<Cand> { pair.a, pair.b };
            picked.AddRange(people.Where(p => p != pair.a && p != pair.b && Flat(p.Pos - mid).magnitude <= TableReach)
                                  .OrderBy(p => Flat(p.Pos - mid).magnitude).Take(s.ClampedGroupSize - 2));
            picks.Add((new TablePick
            {
                People = picked, Key = key, InView = inView,
                JustSat = recent ? picked.Select(p => p.Id).Where(id => !part!.Members.Contains(id)).ToHashSet() : new HashSet<string>(),
                CarryOn = recent ? part!.Summary : "",
            }, score, dist));
        }
        return picks.OrderByDescending(p => p.score).ThenBy(p => p.dist).Select(p => p.pick).ToList();
    }

    /// <summary>A table that couldn't start rests a minute, so it isn't tried (and waited for) again and again (code review 2).</summary>
    private static void RestTable(string key, float seconds)
    {
        if (!TableParts.TryGetValue(key, out var part)) TableParts[key] = part = new TablePart();
        part.RestUntil = Mathf.Max(part.RestUntil, Time.unscaledTime + seconds);
    }

    /// <summary>A table near you starts talking: the best one there's room for, everyone at it (never split); one that fails rests a minute.</summary>
    private static bool TryTable(ChatterSettings s, List<Cand> cands, out string why)
    {
        why = "no free table near you";
        int room = s.Dynamic ? s.DynamicMaxNpcs - PeopleTalking() : s.ClampedGroupSize;
        foreach (var pick in FindTables(s, cands))
        {
            if (pick.Needed > room) { why = "a table near you is waiting for room"; continue; } // not split: it waits (code review 1)
            if (StartGroup(s, pick.People[0], pick.People[1], true, cands, out why, pick.People.Skip(2).ToList(), pick.Key, pick.JustSat, pick.CarryOn))
                return true;
            RestTable(pick.Key, 60f);
        }
        return false;
    }

    /// <summary>
    /// FB-18, Dynamic: a free table in front of you goes first (review M4): gossip moves to the front. With no room for it
    /// yet, nothing else starts for a while so the pool drains (at most 45 s; then tables wait 2 minutes). False = don't
    /// start anything this time.
    /// </summary>
    private static bool MakeRoomForTables(ChatterSettings d, List<DynamicChatter.Kind> order, int free, float now, ref List<Cand>? cands)
    {
        if (now < _tablesSkippedUntil || d.WeightGossip <= 0) return true; // no conversations at all: nothing to make room for
        cands ??= ChatterCandidates(d);
        var table = FindTables(d, cands).FirstOrDefault(t => t.InView);
        if (table == null) { _tableWaitSince = -1f; return true; }
        if (free >= table.Needed)
        {
            if (order.Remove(DynamicChatter.Kind.Gossip)) order.Insert(0, DynamicChatter.Kind.Gossip);
            _tableWaitSince = -1f;
            return true;
        }
        if (_tableWaitSince < 0f) _tableWaitSince = now;
        if (now - _tableWaitSince <= 45f) return false;
        _tablesSkippedUntil = now + 120f;
        _tableWaitSince = -1f;
        return true;
    }

    /// <summary>
    /// A table conversation is over: it counts as a part (two in a row, then the table rests), what it was about is kept
    /// for the next one, and everyone in it is watched for a while, to say goodbye when they get up.
    /// </summary>
    private static void NoteTableEnd(Group g, string why)
    {
        if (!g.Table || g.TableKey.Length == 0 || why == ResetReason || g.Script == null || g.Next == 0) return;
        float now = Time.unscaledTime;
        var ids = g.Members.Where(m => m.Joined).Select(m => m.Id).ToList();
        // A table known by who sits there keeps its count when someone joins (code review 14): the old entry moves to the new key.
        var part = PartFor(g.TableKey, ids, now);
        if (part == null) part = new TablePart();
        foreach (var k in TableParts.Where(kv => kv.Value == part && kv.Key != g.TableKey).Select(kv => kv.Key).ToList()) TableParts.Remove(k);
        TableParts[g.TableKey] = part;
        part.InARow = now - part.LastEnd < TableMemory ? part.InARow + 1 : 1;
        part.LastEnd = now;
        part.Summary = g.Script.Summary;
        part.Members = g.Members.Where(m => m.Joined).Select(m => m.Id).ToHashSet();
        if (part.InARow >= 2) { part.RestUntil = now + TableRest; part.InARow = 0; }
        foreach (var m in g.Members.Where(m => m.Joined && !m.Gone && m.C != null))
        {
            if (!Alive(m.C)) continue;
            TableWatch.RemoveAll(w => w.Id == m.Id);
            TableWatch.Add(new TableLeaver
            {
                C = m.C, Id = m.Id, Body = m.C.Pointer, TableKey = g.TableKey, How = ChatterBrain.HowItWent(g.Script, m.Id),
                Seat = m.C.transform.position, Until = now + TableWatchFor, Epoch = ActiveBrain.EpochFor(m.Id),
            });
        }
        if (TableParts.Count > 60)
            foreach (var k in TableParts.Where(kv => now - kv.Value.LastEnd > 1800f && now > kv.Value.RestUntil).Select(kv => kv.Key).ToList()) TableParts.Remove(k);
        Log.Debug($"table part over ({why}): {part.Summary}{(part.RestUntil > now ? " (the table rests now)" : "")}");
    }

    /// <summary>
    /// Someone from a table conversation gets up to leave (the game's step "Leaving", or a second standing away from their
    /// seat): within your voice distance they say goodbye: warm if the talk went well, plain if it was alright, nothing if
    /// it went badly. One of the others may answer. Written lines, no AI.
    /// </summary>
    private static void TickTableWatch(ChatterSettings s, bool paused)
    {
        if (TableWatch.Count == 0 && PendingByes.Count == 0) return;
        float now = Time.unscaledTime;
        for (int i = PendingByes.Count - 1; i >= 0; i--)
        {
            var p = PendingByes[i];
            if (now < p.at) continue;
            PendingByes.RemoveAt(i);
            if (!paused && Alive(p.c) && !TalkingNow(p.c, p.id)) SayWritten(p.c, p.id, p.text, s);
        }
        if (paused || now < _nextTableWatch) return;
        _nextTableWatch = now + 0.25f; // a few looks a second is enough to catch someone standing up
        foreach (var w in TableWatch.ToList())
        {
            if (now > w.Until || !Alive(w.C) || GameContext.NpcId(w.C) != w.Id) { TableWatch.Remove(w); continue; } // gone, or the body is someone else's now
            if (TalkingNow(w.C, w.Id)) { w.StoodSince = -1f; continue; } // in the next part, or busy with something else
            string step = GameContext.VisitOf(w.C).step;
            bool up = step == "Leaving" || (!GameContext.IsSeated(w.C) && Flat(w.C.transform.position - w.Seat).magnitude > 0.6f);
            if (!up) { w.StoodSince = -1f; continue; }
            if (w.StoodSince < 0f) w.StoodSince = now;
            if (step != "Leaving" && now - w.StoodSince < 1f) continue; // a moment standing, not a flicker
            TableWatch.Remove(w);
            if (w.How < 0 || !WithinVoice(w.C, s) || ActiveBrain.EpochFor(w.Id) != w.Epoch) continue;
            SayWritten(w.C, w.Id, Pick(w.How > 0 ? WarmPartings : PlainPartings), s);
            Log.Debug($"table: {w.C.name} gets up and says goodbye ({(w.How > 0 ? "it was a nice chat" : "it was alright")})");
            if (w.How > 0 && ChatterRng.NextDouble() < 0.5)
            {
                var mate = TableWatch.FirstOrDefault(o => o.TableKey == w.TableKey && Alive(o.C) && GameContext.IsSeated(o.C) && !TalkingNow(o.C, o.Id));
                if (mate != null) PendingByes.Add((now + 1.8f, mate.C, mate.Id, Pick(TableByes)));
            }
        }
    }

    /// <summary>A written line, over their head and in their voice (no AI; never a child; not while they're talking elsewhere).</summary>
    private static void SayWritten(Character who, string id, string text, ChatterSettings s)
    {
        var rec = ActiveBrain.Store.Glance(id); // main thread: one try, no waiting, no history
        if (ActiveBrain.KnownMinors.Contains(id) || (rec != null && ActiveBrain.IsKnownMinor(rec)) || MinorInfo(who, id).minor) return;
        string label = rec != null && DisplayName(rec) != "Stranger" ? Prompts.FirstName(DisplayName(rec)) : "";
        float shown = Mathf.Clamp(1.6f + text.Length / 15f, 2f, 6f);
        Ui.ShowBark(label, text, GameContext.HeadOf(who), shown + 0.4f, id, maxDistance: s.TextDistance, body: who.transform);
        NpcHold.Mouth(who, text, 0f);
        RecordWritten(who, id, rec, text, "table-goodbye");
        if (!WithinVoice(who, s, s.VoiceBeyond) || !_voices.Enabled) return;
        var subject = rec != null ? VoiceSubject.From(rec) : SubjectFor(who, id);
        Task.Run(async () =>
        {
            var voice = await VoiceForAsync(() => subject, text, CancellationToken.None, "chatter").ConfigureAwait(false);
            if (voice.Audio == null) return;
            MainThread.Enqueue(() =>
            {
                if (!Alive(who) || TalkingNow(who, id)) return; // one voice each
                float spoken = VoicePlayer.Play(who, voice.Audio, LineVolume(id, overheard: true), VoiceReach(s), 3f);
                NpcHold.Mouth(who, text, spoken);
                Later(spoken + 0.3f, () => { if (Alive(who) && !NpcHold.IsHeld(who)) NpcHold.MouthAtOwnPace(who); }); // the game's own pace back (code review 9)
                VoicePlayer.Release(who);
            });
        });
    }

    /// <summary>Everything about tables forgotten (chatter stopped, everyone reset).</summary>
    private static void ForgetTables()
    {
        TableParts.Clear();
        TableWatch.Clear();
        PendingByes.Clear();
        _tableWaitSince = -1f;
        _tablesSkippedUntil = -1f;
    }
}
