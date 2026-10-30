using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nivalis;
using NivalisAIChat.Game;
using NivalisAIChat.Mind;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using NivalisAIChat.Voice;
using UnityEngine;

namespace NivalisAIChat;

/// <summary>
/// The inner-life round (0.7.0, the author: "taking it one step further at a time and making sure everything is tied
/// together perfectly"): residents carry how they feel (Mind.Affect), what's on their mind (Mind.Preoccupation) and what
/// they saw others do (Mind.WitnessBook) from one moment to the next, kept with each save (minds.json, witness.json). The
/// code keeps all of it and spends no tokens on it; it reaches the AI only through prompts it already sends (the chat's
/// [INSIDE YOU RIGHT NOW] and turn card, residents' talk, mutters, greetings). Everything that looks at bodies (who saw
/// what, body language, meet-ups) works only inside the chatter ring around you: the city lives where you are.
/// </summary>
public static partial class ModRuntime
{
    private static MindBook? _minds;
    private static WitnessBook? _witness;
    /// <summary>How the game's needs run (learned this session: Mind.NeedsCalibration).</summary>
    private static readonly NeedsCalibration NeedsSeen = new();
    private static bool _needsSaid;
    private static float _nextMindTick;
    /// <summary>Who people are (their persona), read off the main thread once and kept for the session (a small cache).</summary>
    private static readonly ConcurrentDictionary<string, Persona?> PersonaCache = new();
    private static readonly ConcurrentDictionary<string, bool> PersonaLoading = new();
    /// <summary>The relationship bar when this chat began (what the meeting did to them, at the end).</summary>
    private static double _barAtOpen = -1;

    /// <summary>Game time now in minutes (Mind.GameClock), -1 when the day can't be read.</summary>
    private static long GameNow()
    {
        int day = GameContext.Day();
        return day < 0 ? -1 : GameClock.At(day, Math.Max(0, GameContext.Hour()), GameContext.Minute());
    }

    private static bool KnownUnder18(string id) => id.Length > 0 && (_minors?.Contains(id) ?? false);

    /// <summary>
    /// VaryLines as it applies now: the usage rule (anything that spends more is tied into the Usage Optimizer). Its few extra
    /// words come back on greetings and mutters, so the optimizer's learned cost of each already counts them; and while the
    /// pacer is holding residents' talk back to keep within a limit, the three-ways asking stops first, before anything you'd see.
    /// </summary>
    private static bool VaryNow => _cfg.VaryLines.Value && !(PacingOn && _pacer.M < 1);

    // ------------------------------------------------------------------ the books, per save

    /// <summary>OpenSaveData: the inner life of this save's people (after the brain and the chatter brain are made).</summary>
    private static void OpenMindBooks(string dataDir)
    {
        try { _minds = new MindBook(Path.Combine(dataDir, "minds.json"), Log.Warn) { IsMinor = KnownUnder18 }; }
        catch (Exception e) { Log.Warn("Inner life (feelings) unavailable: " + e.Message); _minds = null; }
        try { _witness = new WitnessBook(Path.Combine(dataDir, "witness.json"), Log.Warn) { IsMinor = KnownUnder18 }; }
        catch (Exception e) { Log.Warn("Inner life (what people saw) unavailable: " + e.Message); _witness = null; }
        _brain.Minds = _minds;
        if (_chatter != null)
        {
            _chatter.Minds = _minds;
            _chatter.Witness = _witness;
            _chatter.InnerRules = () => (_cfg.GossipSpreads.Value, VaryNow);
        }
        PersonaCache.Clear();
        OpinionCache.Clear();
        Greetings.CarriedMood = id => Features.InnerLife ? _minds?.MoodNow(id, GameNow())?.mood : null;
        Greetings.Opinion = OpinionOfYou;
    }

    /// <summary>CloseSaveData and Stop: what's waiting is written, then nothing more (a late write would land on the next save's).</summary>
    private static void CloseMindBooks()
    {
        try { _minds?.Close(); } catch (Exception e) { Log.Warn("Saving the inner life: " + e.Message); }
        try { _witness?.Close(); } catch (Exception e) { Log.Warn("Saving what people saw: " + e.Message); }
        EndRendezvous("the save's memory is being put away");
        ForgetBodyLanguage();
    }

    /// <summary>OnGameSaved: written now, so the save's snapshot has them.</summary>
    private static void FlushMindBooks()
    {
        try { _minds?.Flush(); } catch { }
        try { _witness?.Flush(); } catch { }
    }

    /// <summary>Their persona if it's been read this session; otherwise it's read in the background (null until then).</summary>
    private static Persona? PersonaOf(string id)
    {
        if (PersonaCache.TryGetValue(id, out var p) && p != null) return p;
        if (PersonaLoading.TryAdd(id, true))
        {
            var store = ActiveBrain.Store;
            int generation = _resetGeneration; // review (game IL-4): a load or a reset since means it's another save's person
            Task.Run(() =>
            {
                try
                {
                    var persona = store.Glance(id)?.Persona;
                    // Nobody (no record) isn't kept: looked for again next time (review game IL-4).
                    if (persona != null && generation == _resetGeneration) PersonaCache[id] = persona;
                }
                catch { }
                finally { PersonaLoading.TryRemove(id, out _); }
            });
        }
        if (PersonaCache.Count > 500) PersonaCache.Clear();
        return null;
    }

    // ------------------------------------------------------------------ noticing (the witness layer)

    /// <summary>
    /// Something happened at <paramref name="at"/>: adults near it who saw it (inside their own sight cone) or heard it keep
    /// it, by how much it stands out to them (Mind.WitnessBook). Only people inside the chatter ring around you are looked
    /// at, never anyone under 18, never the two it's about. Bodies are read here (the game's thread); who they are to the
    /// people in it is read on the pool.
    /// </summary>
    private static void Witness(string kind, Vector3 at, string actor, string actorName, string target, string targetName, string place, float intensity,
                                string detail = "", bool secret = false)
    {
        if (!Features.InnerLife || _witness == null || _resetting) return;
        if (KnownUnder18(actor) || KnownUnder18(target)) return;
        long now = GameNow();
        var pc = GameContext.PlayerCharacter();
        if (now < 0 || pc == null) return;
        try
        {
            Vector3 me = pc.transform.position;
            float ring = ChatterSettingsNow().Range;
            float hear = Happenings.Loud(kind) ? 12f : 6f;
            float sightD = Mathf.Clamp(float.IsFinite(_cfg.SightDistance.Value) ? _cfg.SightDistance.Value : 10f, 1f, 40f);
            float angle = Mathf.Clamp(float.IsFinite(_cfg.SightAngle.Value) ? _cfg.SightAngle.Value : 120f, 10f, Sight.MaxAngle);
            var seen = new List<(string id, float d, bool sees)>();
            foreach (var (c, _, pos) in NearBodies(pc))
            {
                if (seen.Count >= 10) break;
                if (Flat(pos - me).magnitude > ring) continue; // the city lives where you are
                float d = Flat(pos - at).magnitude;
                if (d > Mathf.Max(hear, sightD)) continue;
                string id;
                try { id = GameContext.NpcId(c); } catch { continue; }
                if (id.Length == 0 || id.StartsWith("extra_", StringComparison.Ordinal) || id == actor || id == target) continue; // extras share ids
                if (MinorInfo(c, id).minor) continue;
                var face = c.transform.forward;
                bool sees = Sight.Sees((pos.x, pos.z), (face.x, face.z), (at.x, at.z), sightD, angle);
                if (!sees && d > hear) continue;
                seen.Add((id, d, sees));
            }
            if (seen.Count == 0) return;
            var book = _witness;
            var store = ActiveBrain.Store;
            var pairs = _pairs;
            var minds = _minds;
            Task.Run(() =>
            {
                try
                {
                    // Someone's name as their record has it, when the caller only had "" or "Stranger" (the window's label).
                    string NameOf(string id, string name) =>
                        id.Length == 0 || id == Happenings.Player || (name.Length > 0 && name != "Stranger") ? name : store.Glance(id)?.Persona.Name ?? "";
                    actorName = NameOf(actor, actorName);
                    targetName = NameOf(target, targetName);
                    var observers = seen.Select(o => new Observer
                    {
                        Id = o.id, Distance = o.d, Sees = o.sees, Relation = RelationFactor(store, pairs, o.id, actor, target),
                        Arousal = minds?.Point(o.id, now)?.a ?? Tone.CalmArousal,
                    }).ToList();
                    int n = book.Witness(kind, actor, actorName, target, targetName, place, now, intensity, observers, secret, detail);
                    if (n > 0) Log.Debug($"witness: {kind} ({(actor == Happenings.Player ? "you" : actorName)}{(target.Length > 0 ? ", " + (target == Happenings.Player ? "you" : targetName) : "")}) kept by {n} of {observers.Count} near it");
                }
                catch (Exception e) { Log.Debug("witness: " + e.Message); }
            });
        }
        catch (Exception e) { Log.Debug("witness: " + e.Message); }
    }

    /// <summary>
    /// Talk of the Town's relation factor for an observer: a partner or close friend of someone in it 1.6, someone they
    /// dislike 1.4 (people watch their enemies), someone they know 1.2, strangers 0.6. The player counts by the observer's bar.
    /// </summary>
    private static float RelationFactor(NpcStore store, PairBook? pairs, string observer, string actor, string target)
    {
        float best = 0.6f;
        foreach (var who in new[] { actor, target })
        {
            if (who.Length == 0) continue;
            try
            {
                if (who == Happenings.Player)
                {
                    var rec = store.Glance(observer);
                    if (rec is { State.TimesMet: > 0 }) best = Math.Max(best, rec.State.Relationship >= 70 ? 1.6f : rec.State.Relationship <= 25 ? 1.4f : 1.2f);
                }
                else if (pairs?.Get(observer, who) is { Times: > 0 } p)
                    best = Math.Max(best, p.Affinity >= 40 ? 1.6f : p.Affinity <= -50 ? 1.4f : 1.2f);
            }
            catch { }
        }
        return best;
    }

    /// <summary>What they've seen or heard of the player lately, -1..1 (greetings lean on it): good and bad, as strongly as it's with them.</summary>
    private static float OpinionOfYou(string id)
    {
        if (!Features.InnerLife || _witness == null) return 0f;
        float t = Time.unscaledTime;
        if (OpinionCache.TryGetValue(id, out var c) && t < c.until) return c.v; // review (game IL-9): once every few seconds each
        try
        {
            long now = GameNow();
            double sum = 0;
            foreach (var b in _witness.About(id, Happenings.Player, now).Take(6)) sum += Happenings.Valence(b.Kind) * WitnessBook.Score(b, now) * 2;
            float v = (float)Math.Clamp(sum, -1, 1);
            if (OpinionCache.Count > 300) OpinionCache.Clear();
            OpinionCache[id] = (v, t + 5f);
            return v;
        }
        catch { return 0f; }
    }

    private static readonly Dictionary<string, (float v, float until)> OpinionCache = new();

    /// <summary>
    /// A residents' conversation near you ended (what was actually said): a romance turn it played out, a row (two or more
    /// angry lines), or a good laugh is seen by the people near them, who may pass it on later.
    /// </summary>
    private static void WitnessConversation(ChatterScript script, int played, Vector3 at)
    {
        if (!Features.InnerLife || played < 2) return;
        var lines = script.Lines.Take(played).ToList();
        string Name(string id) => script.People.TryGetValue(id, out var r) ? r.Persona.Name : "";
        string place = script.Persons.FirstOrDefault()?.Snap.Location ?? "";
        bool BothSpoke(string a, string b) => lines.Any(l => l.SpeakerId == a) && lines.Any(l => l.SpeakerId == b);
        if (script.Turn is { } turn && BothSpoke(turn.A, turn.B))
        {
            switch (turn.Kind)
            {
                case "spark": Witness(Happenings.GotTogether, at, turn.A, turn.NameA, turn.B, turn.NameB, place, 0.7f); return;
                case "confront":
                    Witness(turn.Breakup ? Happenings.BrokeUp : Happenings.Confronted, at, turn.A, turn.NameA, turn.B, turn.NameB, place, turn.Breakup ? 0.9f : 0.8f,
                        detail: Ai.Prompts.FirstName(turn.PartnerName));
                    return;
                case "jealous": Witness(Happenings.Jealous, at, turn.A, turn.NameA, turn.B, turn.NameB, place, 0.7f); return;
                case "cheat": Witness(Happenings.Flirted, at, turn.A, turn.NameA, turn.B, turn.NameB, place, 0.6f); return;
            }
        }
        var hot = lines.Where(l => l.Mood is "angry" or "irritated").ToList();
        if (hot.Count >= 2)
        {
            string a = hot[0].SpeakerId;
            string b = hot.FirstOrDefault(l => l.SpeakerId != a)?.SpeakerId ?? (hot[0].ToId.Length > 0 ? hot[0].ToId : lines.FirstOrDefault(l => l.SpeakerId != a)?.SpeakerId ?? "");
            if (b.Length > 0 && b != ChatterScript.FarEndId) Witness(Happenings.Argued, at, a, Name(a), b, Name(b), place, Math.Min(1f, 0.5f + 0.1f * hot.Count));
            return;
        }
        if (lines.Count(l => l.Mood is "amused" or "playful" or "cheerful") >= 3)
        {
            var two = lines.Select(l => l.SpeakerId).Where(id => id != ChatterScript.FarEndId).Distinct().Take(2).ToList();
            if (two.Count == 2) Witness(Happenings.Laughed, at, two[0], Name(two[0]), two[1], Name(two[1]), place, 0.4f);
        }
    }

    // ------------------------------------------------------------------ feelings

    /// <summary>A line they said in a chat with you, feeling what they felt (the fast layer).</summary>
    private static void FeltInChat(NpcRecord rec)
    {
        if (!Features.InnerLife || !Features.Moods || _minds == null || rec.State.Mood.Length == 0) return;
        _minds.Felt(rec.Id, rec.Persona.Name, Affect.Baseline(rec.Persona), rec.State.Mood, rec.State.MoodIntensity, GameNow());
    }

    /// <summary>Something that stays with them a while (no-op when inner life is off, or for anyone under 18).</summary>
    private static void Stays(string id, string name, Persona? persona, string key, string text, float v, float a, int hours, string mood)
    {
        if (!Features.InnerLife || _minds == null || id.Length == 0) return;
        long now = GameNow();
        if (now < 0) return;
        _minds.Add(id, name, persona != null ? Affect.Baseline(persona) : null,
            new Moodlet { Key = key, Text = text, V = v, A = a, Until = now + hours * 60, Mood = mood }, now);
    }

    /// <summary>
    /// CloseChat: what the meeting did to them stays a while (the bar moved a point or more either way), and anyone near who
    /// saw you two talk keeps it (you upset them, or you were deep in conversation).
    /// </summary>
    private static void AfterChatInnerLife(ChatSession? s, Character? c)
    {
        if (!Features.InnerLife || s == null || s.Exchanges < 1) return;
        var rec = s.Record;
        try
        {
            double moved = _barAtOpen >= 0 && Features.Relationships ? rec.State.Relationship - _barAtOpen : 0;
            bool fedUp = rec.State.LastLeftGameDay == s.Snapshot.GameDay && rec.State.LastLeftReason.Contains("enough", StringComparison.OrdinalIgnoreCase);
            string you = rec.State.PlayerName.Length > 0 ? rec.State.PlayerName : "the newcomer";
            if (fedUp || moved <= -1.0) Stays(rec.Id, rec.Persona.Name, rec.Persona, "you:sore", $"still sore at {you} after that last talk", -0.3f, 0.25f, 3, "irritated");
            else if (moved >= 1.0) Stays(rec.Id, rec.Persona.Name, rec.Persona, "you:glad", $"glad of that talk with {you}", 0.2f, 0.05f, 4, "content");
            if (c != null && Alive(c))
            {
                string place = s.Snapshot.Location;
                string name = rec.Persona.Name;
                if (fedUp || moved <= -1.0) Witness(Happenings.PlayerUpset, c.transform.position, Happenings.Player, "", rec.Id, name, place, fedUp ? 0.85f : 0.6f);
                else if (s.Exchanges >= 2) Witness(Happenings.PlayerTalked, c.transform.position, Happenings.Player, "", rec.Id, name, place, s.Exchanges >= 6 ? 0.65f : 0.35f);
            }
        }
        catch (Exception e) { Log.Debug("inner life after a chat: " + e.Message); }
        finally { _barAtOpen = -1; }
    }

    // ------------------------------------------------------------------ the card ([INSIDE YOU RIGHT NOW])

    /// <summary>
    /// OpenChat and every turn: how they feel underneath carried in (CarriedMood), and the card: a plan's outcome, what's on
    /// their mind, their body, their love life, people they know nearby, what they saw. Main thread (bodies are read).
    /// </summary>
    private static void FillInner(GameSnapshot snap, Character? c, string id, NpcRecord? rec)
    {
        snap.Inner = new List<string>();
        snap.CarriedMood = snap.CarriedIntensity = snap.CarriedReason = "";
        if (!Features.InnerLife || id.Length == 0 || _minds == null || KnownUnder18(id)) return;
        try
        {
            long now = GameClock.Of(snap);
            if (Features.Moods && _minds.MoodNow(id, now) is { } m) { snap.CarriedMood = m.mood; snap.CarriedIntensity = m.intensity; snap.CarriedReason = m.reason; }
            var input = new InnerInputs();
            if (_reminders != null && Features.Reminders)
                foreach (var r in _reminders.Outcomes(id, snap.GameDay).OrderByDescending(r => r.Day))
                    if (Meetups.Line(r, now) is { Length: > 0 } line) input.Plans.Add(line);
            var (hunger, rest) = NeedsOf(c);
            input.Thought = ThinkFor(id, rec, hunger, rest, now);
            input.Body = hunger >= 0.9f ? "You're starving." : hunger >= 0.75f ? "You're getting hungry." : rest >= 0.92f ? "You're dead on your feet." : rest >= 0.8f ? "You're tired." : "";
            if (rec != null) input.Food = Tastes.Line(rec, snap.GameDay);
            if (_couples?.CoupleOf(id) is { } couple)
                input.Together = $"You're with {Ai.Prompts.FirstName(CoupleBook.OtherName(couple, id))} (since day {couple.SinceDay}).";
            input.PeopleHere = PeopleHere(c, id);
            if (_witness != null)
            {
                var mine = _witness.Of(id, now).Where(b => !b.ShownDays.Contains(snap.GameDay) || b.ShownDays.Count < 2).ToList();
                var open = mine.Where(b => !b.Secret).Take(2).ToList();
                var keep = mine.Where(b => b.Secret).Take(1).ToList();
                input.Seen = open.Select(b => Happenings.Words(b, id, "the person in front of you", now)).ToList();
                input.Keep = keep.Select(b => Happenings.Words(b, id, "the person in front of you", now)).ToList();
                _witness.Shown(id, open.Concat(keep).Select(b => b.Id), snap.GameDay);
            }
            snap.Inner = InnerCard.Lines(input);
        }
        catch (Exception e) { Log.Debug("inner card: " + e.Message); }
    }

    /// <summary>What's on their mind now (chosen afresh, kept in their mind; Mind.Preoccupation).</summary>
    private static Mind.Thought? ThinkFor(string id, NpcRecord? rec, float hunger, float rest, long now) // Mind's (ModRuntime has its own Thought: a mutter)
    {
        if (_minds == null || now < 0) return null;
        var persona = rec?.Persona ?? PersonaOf(id);
        if (persona == null) return _minds.Current(id);
        var input = new MindInputs { Persona = persona, Hunger = hunger, Rest = rest, Moodlets = _minds.Moodlets(id, now) };
        if (_witness != null)
            input.Seen = _witness.Of(id, now).Where(b => !b.Secret).Take(3)
                .Select(b => ("can't stop thinking about it: " + Happenings.Words(b, id, "the newcomer", now, how: false), WitnessBook.Score(b, now), b.Id)).ToList();
        if (_reminders != null && Features.Reminders)
            foreach (var r in _reminders.OpenMeetups().Where(r => r.WhoId == id))
            {
                var (from, to) = Meetups.Window(r);
                if (now >= from - 180 && now <= to) { input.PlanSoon = $"meeting the newcomer{(r.Place.Length > 0 ? " at " + r.Place : "")}{(r.Hour >= 0 ? $" at {r.Hour:00}:00" : "")}"; break; }
            }
        var thought = Preoccupation.Choose(input, _minds.Current(id), now);
        _minds.Think(id, persona.Name, thought, now);
        return thought;
    }

    /// <summary>People they know standing near them (8 m), with how they feel about them: "Rina (a good friend)". Adults only.</summary>
    private static List<string> PeopleHere(Character? c, string id)
    {
        var list = new List<string>();
        var pc = GameContext.PlayerCharacter();
        if (c == null || pc == null || _pairs == null || !Alive(c)) return list;
        try
        {
            Vector3 at = c.transform.position;
            string partner = _couples?.CoupleOf(id) is { } couple ? CoupleBook.Other(couple, id) : "";
            foreach (var (o, _, pos) in NearBodies(pc).OrderBy(b => Flat(b.pos - at).sqrMagnitude))
            {
                if (list.Count >= 4) break;
                if (Same(o, c) || Flat(pos - at).magnitude > 8f) continue;
                string other;
                try { other = GameContext.NpcId(o); } catch { continue; }
                if (other.Length == 0 || other == id || other.StartsWith("extra_", StringComparison.Ordinal) || MinorInfo(o, other).minor) continue;
                var pair = _pairs.Get(id, other);
                if (pair is not { Times: > 0 }) continue;
                string name = Ai.Prompts.FirstName(pair.A == id ? pair.NameB : pair.NameA);
                if (name.Length == 0) continue;
                string how = other == partner ? "your partner" : pair.Affinity >= 50 ? "a good friend" : pair.Affinity >= 20 ? "you get on" : pair.Affinity > -20 ? "you know them"
                    : pair.Affinity > -50 ? "you don't much like them" : "you can't stand them";
                list.Add($"{name} ({how})");
            }
        }
        catch (Exception e) { Log.Debug("people nearby: " + e.Message); }
        return list;
    }

    /// <summary>How much they need food and rest, 0..1 (-1 each until the game's needs are understood: NeedsCalibration).</summary>
    private static (float hunger, float rest) NeedsOf(Character? c)
    {
        if (c == null || !NeedsSeen.Ready) return (-1f, -1f);
        try
        {
            var g = c.Agent?.MyGhost;
            if (g == null) return (-1f, -1f);
            var needs = g.State.Needs;
            return (NeedsSeen.Hunger(needs.Hunger), NeedsSeen.Rest(needs.Rest));
        }
        catch { return (-1f, -1f); }
    }

    /// <summary>
    /// Every few seconds of play: the game's needs read on a few people near you (until it's clear which way they run), and
    /// what's on the mind of the people near you brought up to date (only people who have an inner life kept already).
    /// </summary>
    private static void TickMind()
    {
        float now = Time.unscaledTime;
        if (now < _nextMindTick) return;
        _nextMindTick = now + 5f;
        if (!Features.InnerLife || _minds == null || _resetting || !InGameplay()) return;
        var pc = GameContext.PlayerCharacter();
        if (pc == null) return;
        long game = GameNow();
        if (game < 0) return;
        try
        {
            Vector3 me = pc.transform.position;
            float ring = ChatterSettingsNow().Range;
            int sampled = 0, thought = 0;
            foreach (var (c, _, pos) in NearBodies(pc))
            {
                if (Flat(pos - me).magnitude > ring) continue;
                if (!NeedsSeen.Ready && sampled < 8)
                {
                    try
                    {
                        var g = c.Agent?.MyGhost;
                        if (g != null)
                        {
                            var needs = g.State.Needs;
                            string task = "";
                            try { task = g.CurrentAction?.Type?.name ?? ""; } catch { }
                            bool eating = GameContext.VisitOf(c).step == "Eating" || task.Contains("Eat", StringComparison.OrdinalIgnoreCase);
                            bool sleeping = task.Contains("Sleep", StringComparison.OrdinalIgnoreCase) || task.Contains("Bed", StringComparison.OrdinalIgnoreCase);
                            NeedsSeen.Observe(c.Pointer.ToString(), needs.Hunger, needs.Rest, now, eating, sleeping);
                            sampled++;
                        }
                    }
                    catch { }
                }
                if (thought >= 6) continue;
                string id;
                try { id = GameContext.NpcId(c); } catch { continue; }
                if (id.Length == 0 || !_minds.Has(id) || KnownUnder18(id)) continue;
                var (hunger, rest) = NeedsOf(c);
                ThinkFor(id, null, hunger, rest, game);
                thought++;
            }
            if (NeedsSeen.Ready && !_needsSaid) { _needsSaid = true; Log.Info("Inner life (probe): the game's needs are understood now: " + NeedsSeen.Describe() + "."); }
        }
        catch (Exception e) { Log.Debug("inner life tick: " + e.Message); }
    }

    // ------------------------------------------------------------------ /inner (dev)

    /// <summary>/inner: what the inner life holds (counts, the needs); /inner name: one person's feelings, mind and what they saw.</summary>
    private static void InnerCommand(string arg, Action<string> say)
    {
        if (_minds == null) { say("The inner life isn't available this session (the log says why)."); return; }
        long now = GameNow();
        string q = arg.Trim();
        if (q.Length == 0)
        {
            var all = _minds.All(now);
            say($"Inner life: {(Features.InnerLife ? "on" : "off (Features > InnerLife)")}. {all.Count} people with feelings kept, {_witness?.Count ?? 0} things seen or heard. " +
                $"Body language {(_cfg.BodyLanguage.Value ? "on" : "off")}, plans that happen {(_cfg.PlansHappen.Value ? "on" : "off")}, gossip told {(_cfg.GossipSpreads.Value ? "on" : "off")}.");
            if (all.Count > 0)
                say($"  How the city feels: {all.Average(a => a.v):0.00} on average (-1 low .. +1 high); {all.Count(a => a.v < -0.5f)} low, {all.Count(a => a.v > 0.5f)} high.");
            say("  The game's needs: " + NeedsSeen.Describe() + ".");
            foreach (var a in all.Where(a => a.thought.Length > 0).OrderByDescending(a => Math.Abs(a.v)).Take(8))
                say($"  {a.name}: {Tone.Nearest(a.v, a.a)} ({a.v:0.00}, {a.a:0.00}); on their mind: {a.thought}");
            say("/inner <name> shows one person.");
            return;
        }
        var who = _minds.All(now).FirstOrDefault(a => a.name.StartsWith(q, StringComparison.OrdinalIgnoreCase) || a.id == q);
        if (who.id == null) { say($"Nobody called '{q}' has an inner life kept yet (talk to them, or let them talk near you)."); return; }
        var label = _minds.MoodNow(who.id, now);
        say($"{who.name}: feels {Tone.Nearest(who.v, who.a)} underneath ({who.v:0.00}, {who.a:0.00}){(label is { } l ? $", carried into how they are: {l.intensity} {l.mood}{(l.reason.Length > 0 ? " — " + l.reason : "")}" : "")}.");
        foreach (var m in _minds.Moodlets(who.id, now)) say($"  still with them: {m.Text} ({m.V:+0.00;-0.00}, {Math.Max(0, (m.Until - now) / 60.0):0.#} game hours left)");
        if (_minds.Current(who.id) is { } t) say($"  on their mind: {t.Text} ({t.Kind})");
        var (openers, lines) = _minds.Phrases(who.id);
        if (openers.Count > 0) say($"  how they've opened lately: {string.Join(", ", openers.TakeLast(6))}");
        try
        {
            // Their taste (a dev look: their record read once, here).
            if (ActiveBrain.Store.Glance(who.id) is { } rec && rec.State.Meals.Count > 0)
            {
                int day = GameContext.Day();
                string taste = Tastes.Line(rec, day);
                say($"  eaten lately: {string.Join(", ", rec.State.Meals.TakeLast(6).Select(m => $"{m.Dish} (day {m.Day})"))}{(taste.Length > 0 ? "  ·  " + taste : "")}");
            }
        }
        catch { }
        foreach (var b in _witness?.Of(who.id, now).Take(6) ?? new List<Belief>())
            say($"  knows: {Happenings.Words(b, who.id, "you", now)} (salience {b.Salience:0.00}, {WitnessBook.Retention(b, now):P0} left{(b.Pinned ? ", pinned" : "")})");
    }
}
