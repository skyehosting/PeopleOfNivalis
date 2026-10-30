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
using NivalisAIChat.UI;
using NivalisAIChat.Voice;
using UnityEngine;
using UnityEngine.InputSystem;
using Character = Nivalis.Character;

namespace NivalisAIChat;

/// <summary>
/// FB-76, world chat (the author). Press
/// WorldChatKey (Enter) out in the world: a thin line at the bottom centre, everything else stops while you type, and above
/// it a chip for each person around you (in your view cone within WorldChatRange, or within a metre: those only overhear).
/// You talk to whoever's gold (the one nearest the middle of your view to start; click chips to add or take away, All for
/// everyone, Tab to move on). Each thing you say is one request (Npc/WorldTalk.cs, "world": your own use) for the ones you're
/// talking to and the others in view; their answers show over their heads one after another, voiced. A moment ends when you
/// walk away or it goes quiet: who took part keeps it as a conversation, who only overheard keeps "overheard ...". Never
/// anyone under 18: not a chip, not in a request, never told anything. FeatureWorldChat.
/// </summary>
public static partial class ModRuntime
{
    private sealed class WorldPerson
    {
        public Character C = null!;
        public string Id = "", Label = "", Known = "";
        public ViewCone.Place Place;
        public float Off, Dist;
    }

    private sealed class WorldMoment
    {
        public readonly WorldTalk Talk = new();
        public readonly Dictionary<string, Character> Bodies = new();
        public readonly HashSet<string> Held = new(), Overhearers = new();
        public readonly Queue<(GroupChat.Line line, Task<VoiceOutcome>? voice, float queuedAt)> Queue = new();
        public readonly CancellationTokenSource Cts = new();
        public Vector3 Centre;
        public float LastAt, NextAt;
        public bool Busy;
        public int Generation;
    }

    private static readonly WorldChatLine WorldLine = new();
    private static bool _worldOpen;
    private static WorldMoment? _moment;
    private static List<WorldPerson> _worldPeople = new();
    private static readonly HashSet<string> WorldSelected = new();
    private static float _nextWorldLook;
    private static Key _worldKey = Key.Enter;
    /// <summary>Who's who on the chips, read once a moment (a glance at their record: their name if you know it).</summary>
    private static readonly Dictionary<string, (string label, string known)> WorldLabels = new();
    private const float WorldQuietSeconds = 60f, WorldOverhear = 1f;

    private static float WorldRange => Mathf.Clamp(_cfg.WorldChatRange.Value, 1f, 12f);

    private static void ParseWorldKey()
    {
        string k = (_cfg.WorldChatKey.Value ?? "Enter").Trim();
        if (k.Length == 1 && char.IsDigit(k[0])) k = "Digit" + k;
        _worldKey = Enum.TryParse<Key>(k, true, out var parsed) && parsed != Key.None && Enum.IsDefined(parsed) ? parsed : Key.Enter;
        if (_worldKey == _talkKey || _worldKey == _consoleKey || _worldKey == _phoneKey)
        {
            // Review WC-8: the first of these the other keys don't use.
            var free = new[] { Key.Enter, Key.Y, Key.F2 }.FirstOrDefault(k => k != _talkKey && k != _consoleKey && k != _phoneKey);
            Log.Warn($"WorldChatKey ({_cfg.WorldChatKey.Value}) is the talk, console or phone key; using {free}.");
            _worldKey = free;
        }
        // FB-81: who you're talking to, moved on (Tab): a key that doesn't type in the line, and not the line's own key.
        string n = (_cfg.WorldChatNextKey.Value ?? "Tab").Trim();
        var next = Enum.TryParse<Key>(n, true, out var nk) && nk != Key.None && Enum.IsDefined(nk) ? nk : Key.Tab;
        if (!NonTypingKey(next) || next == _worldKey || next == _phoneKey || next == _talkKey || next == _consoleKey) next = Key.Tab; // reviews F81-3/4
        WorldLine.NextKey = next;
    }

    private static bool WorldKeyPressed()
    {
        var kb = Keyboard.current;
        return kb != null && Features.WorldChat && Pressed(kb, ref _worldKey, Key.Enter, "WorldChatKey");
    }

    // ------------------------------------------------------------------ the line

    private static void OpenWorldLine()
    {
        if (_worldOpen || ChatOpen || _consoleOpen || _phoneOpen || _resetting || !Ui.Built) return;
        WorldLine.Build(Ui.Fonts);
        RefreshWorldPeople();
        // To start with: whoever you were talking to, if they're still in view; else the one nearest the middle of your view.
        WorldSelected.RemoveWhere(id => _worldPeople.All(p => p.Id != id || p.Place != ViewCone.Place.InView));
        if (WorldSelected.Count == 0 && _worldPeople.FirstOrDefault(p => p.Place == ViewCone.Place.InView) is { } first) WorldSelected.Add(first.Id);
        // The author: "Everything stops" while you type. CF-21 (a tester: "make world chat use the same settings as regular
        // chat"): the clock too, as the chat window does, when PauseTimeWhileChatting is on.
        Locks.Engage(_cfg.PauseTimeWhileChatting.Value);
        _worldOpen = true;
        WorldLine.Open();
        ShowWorldChips();
        if (_worldPeople.All(p => p.Place != ViewCone.Place.InView)) WorldLine.SetStatus("Nobody in front of you to talk to (turn to them, or come closer).");
    }

    private static void CloseWorldLine()
    {
        if (!_worldOpen) return;
        _worldOpen = false;
        WorldLine.Close();
        try { Locks.Release(); } catch (Exception e) { Log.Warn("Releasing player locks failed: " + e.Message); }
        if (_moment != null) _moment.LastAt = Time.unscaledTime;
        else WorldLabels.Clear(); // review WC-7: names learned since are read again next time
    }

    /// <summary>Every frame while the line is open: who's around, your clicks and Tab, what you send.</summary>
    private static void UpdateWorldLine()
    {
        // Review WC-1: the lock's own cursor makes InGameplay() false: only a paused game, a reset or the switch close it here.
        Locks.KeepCursor();
        if (!Features.WorldChat || _resetting || Time.timeScale <= 0f) { CloseWorldLine(); return; }
        var kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame) { CloseWorldLine(); return; }
        WorldLine.Tick();
        if (Time.unscaledTime >= _nextWorldLook) { RefreshWorldPeople(); ShowWorldChips(); }
        if (WorldLine.TakeClick() is { } clicked)
        {
            var inView = _worldPeople.Where(p => p.Place == ViewCone.Place.InView).Select(p => p.Id).ToList();
            if (clicked == WorldChatLine.AllId)
            {
                if (inView.All(WorldSelected.Contains)) { WorldSelected.Clear(); if (inView.Count > 0) WorldSelected.Add(inView[0]); }
                else WorldSelected.UnionWith(inView);
            }
            else if (inView.Contains(clicked)) { if (!WorldSelected.Remove(clicked)) WorldSelected.Add(clicked); }
            ShowWorldChips();
        }
        if (WorldLine.TakeCycle())
        {
            var inView = _worldPeople.Where(p => p.Place == ViewCone.Place.InView).Select(p => p.Id).ToList();
            if (inView.Count > 0)
            {
                int at = WorldSelected.Count == 1 ? inView.IndexOf(WorldSelected.First()) : -1;
                WorldSelected.Clear();
                WorldSelected.Add(inView[(at + 1) % inView.Count]);
                ShowWorldChips();
            }
        }
        if (WorldLine.TakeSubmit() is { } said)
        {
            if (said.Length == 0) { CloseWorldLine(); return; } // Enter on an empty line closes it
            if (said.StartsWith("/")) { WorldLine.SetStatus("Commands go in the console (` key)."); WorldLine.PutBack(said); return; }
            if (_moment is { Busy: true }) { WorldLine.SetStatus("They're still answering…"); WorldLine.PutBack(said); return; } // review WC-6
            WorldSend(said);
        }
    }

    /// <summary>The adults around you, by your view cone: in view (within the range), or close enough to overhear.</summary>
    private static void RefreshWorldPeople()
    {
        _nextWorldLook = Time.unscaledTime + 0.4f;
        var list = new List<WorldPerson>();
        var pc = GameContext.PlayerCharacter();
        var cam = GameContext.PlayerCamera();
        if (pc == null || cam == null) { _worldPeople = list; return; }
        Vector3 pp = pc.transform.position, fwd = cam.transform.forward;
        float half = ViewCone.HalfAngle(cam.fieldOfView, cam.aspect, 0.7f);
        foreach (var (c, _, pos) in NearBodies(pc))
        {
            if (Flat(pos - pp).magnitude > Mathf.Max(WorldRange, WorldOverhear) + 0.5f) continue;
            string id;
            try { id = GameContext.NpcId(c); } catch { continue; }
            if (id.Length == 0 || MinorInfo(c, id).minor) continue; // never anyone under 18
            if (NpcTargeting.WhyNot(c, _cfg.ChatWithNamedNpcs.Value) != null) continue;
            if (_moment?.Talk.Get(id) is { Gone: true }) continue; // review WC-5: they walked on
            bool ours = _moment != null && _moment.Held.Contains(id);
            if (!ours && TalkingNow(c, id)) continue; // in a talk of their own, a call, held by something else
            var place = ViewCone.Classify((pp.x, pp.z), (fwd.x, fwd.z), (pos.x, pos.z), half, WorldRange, WorldOverhear);
            if (place == ViewCone.Place.None) continue;
            var (label, known) = WorldLabelOf(c, id);
            list.Add(new WorldPerson { C = c, Id = id, Label = label, Known = known, Place = place,
                                       Off = ViewCone.Off((pp.x, pp.z), (fwd.x, fwd.z), (pos.x, pos.z)), Dist = Flat(pos - pp).magnitude });
        }
        // In view first, the middle of your view first; numbered when two look alike.
        list = list.OrderBy(p => p.Place == ViewCone.Place.InView ? 0 : 1).ThenBy(p => p.Off).ToList();
        // The 2026-10-20 audit: only as many as one request holds can be talked to (chips, All, Tab); the rest in view hear it.
        var places = ViewCone.Talkable(list.Select(p => p.Place).ToArray());
        for (int i = 0; i < list.Count; i++) list[i].Place = places[i];
        list = list.OrderBy(p => p.Place == ViewCone.Place.InView ? 0 : 1).ThenBy(p => p.Off).ToList();
        foreach (var same in list.GroupBy(p => p.Label).Where(g => g.Count() > 1)) { int n = 1; foreach (var p in same) p.Label = $"{p.Label} {n++}"; }
        _worldPeople = list;
        WorldSelected.RemoveWhere(id => list.All(p => p.Id != id || p.Place != ViewCone.Place.InView));
        if (_moment != null) foreach (var p in list.Where(p => p.Place == ViewCone.Place.Overhears)) _moment.Overhearers.Add(p.Id);
    }

    private static (string label, string known) WorldLabelOf(Character c, string id)
    {
        if (WorldLabels.TryGetValue(id, out var had)) return had;
        string known = "";
        NpcRecord? rec = null;
        try { rec = ActiveBrain.Store.Glance(id); } catch { }
        if (rec != null) { string shown = DisplayName(rec); if (shown != "Stranger") known = Prompts.FirstName(shown); }
        if (known.Length == 0)
            try { var person = GameContext.PersonOf(c); if (person != null && GameContext.IsNamed(person)) known = Prompts.FirstName(person.DisplayedName ?? ""); } catch { }
        string body = "";
        try { body = GameContext.Visual(c) is { } v ? (v.IsAndroid ? "android" : v.Body) : ""; } catch { }
        string label = known.Length > 0 ? known : $"Stranger ({Population.PerceivedLabel(body)})";
        if (WorldLabels.Count > 200) WorldLabels.Clear();
        return WorldLabels[id] = (label, known);
    }

    private static void ShowWorldChips()
    {
        WorldLine.SetChips(_worldPeople.Select(p => new WorldChatLine.Chip
        {
            Id = p.Id, Label = p.Label, Selected = WorldSelected.Contains(p.Id),
            State = p.Place == ViewCone.Place.InView ? WorldChatLine.ChipState.InView : WorldChatLine.ChipState.Overhears,
        }).ToList());
    }

    // ------------------------------------------------------------------ saying something

    private static void WorldSend(string text)
    {
        if (_resetting || _moment is { Busy: true }) { WorldLine.SetStatus("They're still answering…"); WorldLine.PutBack(text); return; } // review WC-3
        if (!AiAllowed()) { WorldLine.SetStatus("Your AI limit is reached (TokenLimit): nobody answers until it starts again."); return; }
        var present = _worldPeople.Where(p => p.Place == ViewCone.Place.InView)
            .OrderByDescending(p => WorldSelected.Contains(p.Id)).ThenBy(p => p.Off).Take(ViewCone.MaxTalking).ToList();
        if (present.Count == 0) { WorldLine.SetStatus("Nobody in front of you to talk to."); return; }
        var pc = GameContext.PlayerCharacter();
        if (pc == null) return;
        // A new moment when there's none, or you've moved on from the last one.
        if (_moment != null && Flat(pc.transform.position - _moment.Centre).magnitude > WorldRange + 3f) EndWorldMoment("you moved on");
        if (_moment == null)
        {
            var snap0 = GameContext.Capture(null);
            _moment = new WorldMoment { Centre = pc.transform.position, Generation = _resetGeneration };
            _moment.Talk.Where = string.Join(", ", GameContext.PlacesHere().AsEnumerable().Reverse());
            _moment.Talk.Day = snap0.GameDay;
            _moment.Talk.Clock = snap0.Clock;
        }
        var m = _moment;
        m.Busy = true;
        m.LastAt = Time.unscaledTime;
        var addressed = present.Where(p => WorldSelected.Contains(p.Id)).Select(p => p.Id).ToList();
        var presentIds = present.Select(p => p.Id).ToList();
        foreach (var p in _worldPeople.Where(p => p.Place == ViewCone.Place.InView && !presentIds.Contains(p.Id))) m.Overhearers.Add(p.Id); // review WC-7
        WorldLine.SetStatus("You: " + text);
        // Who's new to the moment is read (or made: someone new) off the game's thread; the request is built back here.
        var brain = ActiveBrain;
        var wants = new List<(string id, NpcVisual visual, GameSnapshot snap, string label)>();
        foreach (var p in present)
        {
            m.Bodies[p.Id] = p.C;
            if (m.Talk.Get(p.Id) != null) continue;
            NpcVisual visual = new();
            GameSnapshot snap = new();
            try { visual = GameContext.Visual(p.C); snap = GameContext.Capture(p.C); } catch { }
            wants.Add((p.Id, visual, snap, p.Label));
        }
        NoteWorldHold(m, addressed);
        var ct = m.Cts.Token;
        Task.Run(async () =>
        {
            var got = new List<(NpcRecord rec, GameSnapshot snap, string label, string looks)>();
            string? why = null; // FB-84: why someone couldn't be made (out of credit...), said if nobody answers
            foreach (var (id, visual, snap, label) in wants)
            {
                try
                {
                    var rec = await brain.EnsurePersonAsync(id, visual, snap, ct, e => why ??= e).ConfigureAwait(false);
                    if (rec != null && !brain.IsKnownMinor(rec) && !rec.Visual.Minor) got.Add((rec, snap, label, Looks.Find(visual.Model)?.Text ?? ""));
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { Log.Debug("world person: " + e.Message); }
            }
            MainThread.Enqueue(() => WorldAsk(m, text, addressed, presentIds, got, why));
        });
    }

    /// <summary>FB-84: why nobody answers, logged and kept for /errors in full ("[AI-LIMIT] ..."), and its first sentence
    /// for the line under the box (one line, it can't wrap).</summary>
    private static string WorldProblem(string what, string error) =>
        ProblemCodes.FirstSentence(Problem($"World chat: {what}: {Redact.Hide(error, _cfg.ApiKey.Value)}"));

    /// <summary>The ones you talk to turn and face you (a light hold, let go when the moment ends).</summary>
    private static void NoteWorldHold(WorldMoment m, IReadOnlyCollection<string> addressed)
    {
        var cam = GameContext.PlayerCamera();
        foreach (var id in addressed)
        {
            if (m.Held.Contains(id) || m.Talk.Get(id) is { Gone: true } || !m.Bodies.TryGetValue(id, out var c) || !Alive(c)) continue; // review WC-5
            try
            {
                if (NpcHold.IsHeld(c) || !NpcHold.EngageLight(c, _cfg.AnimateNpc.Value)) continue;
                m.Held.Add(id);
                NpcHold.LookAtFor(c, cam != null ? cam.transform : null);
            }
            catch (Exception e) { Log.Debug("world hold: " + e.Message); }
        }
    }

    private static void WorldAsk(WorldMoment m, string text, List<string> addressed, List<string> presentIds,
        List<(NpcRecord rec, GameSnapshot snap, string label, string looks)> got, string? why)
    {
        try { WorldAskInner(m, text, addressed, presentIds, got, why); }
        catch (Exception e) { m.Busy = false; WorldLine.SetStatus("Something went wrong: nobody answers."); Log.Debug("world ask: " + e); } // review WC-4
    }

    private static void WorldAskInner(WorldMoment m, string text, List<string> addressed, List<string> presentIds,
        List<(NpcRecord rec, GameSnapshot snap, string label, string looks)> got, string? why)
    {
        if (_moment != m || m.Generation != _resetGeneration) { m.Busy = false; return; }
        foreach (var (rec, snap, label, looks) in got)
            if (m.Talk.Get(rec.Id) == null) m.Talk.Members.Add(new WorldTalk.Member { Record = rec, Snap = snap, Label = label, Looks = looks });
        presentIds = presentIds.Where(id => m.Talk.Get(id) is { Gone: false }).ToList();
        addressed = addressed.Where(presentIds.Contains).ToList();
        if (presentIds.Count == 0)
        {
            m.Busy = false;
            WorldLine.SetStatus(why is { Length: > 0 } ? WorldProblem("nobody could answer", why) : "They couldn't be read: nobody answers.");
            return;
        }
        int maxTokens = Math.Clamp(_cfg.MaxGenerationTokens.Value, 1024, 64000);
        Mind.GroupTurns.Plan? turns = null;
        if (Features.InnerLife)
            try
            {
                long now = GameNow();
                var turnMembers = presentIds.Select(id => m.Talk.Get(id)!).Select(x => new Mind.TurnMember
                {
                    Id = x.Id, Name = x.Name, Label = x.Label, Arousal = _minds?.Point(x.Id, now)?.a ?? Tone.CalmArousal,
                    Confronts = (x.Record.Persona.ConflictStyle ?? "").Contains("confront", StringComparison.OrdinalIgnoreCase),
                    Talkative = Mind.Telling.Gossipy(x.Record.Persona),
                }).ToList();
                var last = m.Talk.Transcript.LastOrDefault(t => t.who != WorldTalk.PlayerId);
                bool asked = last.text?.TrimEnd().EndsWith("?") == true;
                turns = Mind.GroupTurns.Decide(text, turnMembers, last.who ?? "", asked, addressed.FirstOrDefault() ?? "", new System.Random());
            }
            catch (Exception e) { Log.Debug("world turns: " + e.Message); }
        var req = m.Talk.Request(text, addressed, presentIds, (a, b) => _pairs?.Get(a, b), maxTokens, turns: turns?.Note ?? "");
        var ct = m.Cts.Token;
        var llm = _llm;
        var records = presentIds.ToDictionary(id => id, id => m.Talk.Get(id)!.Record);
        Task.Run(async () =>
        {
            WorldTalk.Reply reply;
            try
            {
                var res = await llm.CompleteAsync(req, ct).ConfigureAwait(false);
                reply = res.Ok ? m.Talk.Parse(res.Text, presentIds) : new WorldTalk.Reply { Error = res.Error ?? "no answer" };
                if (reply.Ok && turns != null) reply.KeepTo(turns.Speakers);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { reply = new WorldTalk.Reply { Error = e.Message }; }
            var voices = reply.Lines.Select(line => records.TryGetValue(line.SpeakerId, out var rec)
                ? Task.Run(() => VoiceForAsync(() => VoiceSubject.From(rec), line.Text, ct, "you")) // your own conversation
                : null).ToList();
            MainThread.Enqueue(() =>
            {
                m.Busy = false; // review WC-4: whatever happens next
                if (_moment != m || m.Generation != _resetGeneration) return;
                m.LastAt = Time.unscaledTime;
                if (!reply.Ok) { WorldLine.SetStatus(WorldProblem("no answer", reply.Error ?? "no answer")); return; } // FB-84: logged too
                m.Talk.Heard(text, addressed, reply, 0); // review WC-6: their lines go in as they're shown (TickWorldMoment)
                // Who you were talking to, as they read it ("the one in the yellow coat"): the chips follow.
                if (reply.Addressed.Count > 0) { WorldSelected.Clear(); WorldSelected.UnionWith(reply.Addressed); NoteWorldHold(m, reply.Addressed); if (_worldOpen) ShowWorldChips(); }
                WorldLine.SetStatus(reply.Lines.Count == 0 ? "Nobody answers." : "");
                for (int i = 0; i < reply.Lines.Count; i++) m.Queue.Enqueue((reply.Lines[i], voices[i], Time.unscaledTime));
                Log.Info($"World chat: you said something to {(addressed.Count == 0 ? "everyone around" : string.Join(", ", addressed.Select(id => m.Talk.Get(id)?.Label ?? "")))}; {reply.Lines.Count} answer(s).");
            });
        });
    }

    // ------------------------------------------------------------------ their answers, and the moment ending

    /// <summary>Every frame: answers shown one after another over their heads; the moment ends when it's quiet or you've gone.</summary>
    private static void TickWorldMoment()
    {
        var m = _moment;
        if (m == null) return;
        if (m.Generation != _resetGeneration || !Features.WorldChat || _resetting) { EndWorldMoment("a load, or world chat off", quiet: true); return; }
        float now = Time.unscaledTime;
        var pc = GameContext.PlayerCharacter();
        if (m.Busy && now - m.LastAt > 90f) { m.Busy = false; WorldLine.SetStatus("No answer came."); } // review WC-4
        if (!_worldOpen && !m.Busy && m.Queue.Count == 0)
        {
            bool away = pc != null && Flat(pc.transform.position - m.Centre).magnitude > WorldRange + 3f;
            if (away || now - m.LastAt > WorldQuietSeconds) { EndWorldMoment(away ? "you walked on" : "it went quiet"); return; }
        }
        if (m.Queue.Count == 0 || now < m.NextAt) return;
        var (line, voiceTask, queuedAt) = m.Queue.Peek();
        if (voiceTask != null && !voiceTask.IsCompleted && now - queuedAt < 8f) return; // their voice a moment longer
        m.Queue.Dequeue();
        var member = m.Talk.Get(line.SpeakerId);
        if (member == null || !m.Bodies.TryGetValue(line.SpeakerId, out var c) || !Alive(c)) return;
        VoiceOutcome? voice = null;
        try { if (voiceTask is { IsCompletedSuccessfully: true }) voice = voiceTask.Result; } catch { }
        float seconds = 0f;
        try { if (voice?.Audio != null) seconds = VoicePlayer.Play(c, voice.Audio, LineVolume(line.SpeakerId), _cfg.HearingDistance.Value); } catch { }
        float shown = Mathf.Max(Mathf.Clamp(2.5f + line.Text.Length / 14f, 3f, 7f), seconds + 1f);
        string name = WorldLabels.TryGetValue(line.SpeakerId, out var l) ? l.known : "";
        try { Ui.ShowBark(name, line.Text, GameContext.HeadOf(c), shown, "world:" + line.SpeakerId, body: c.transform); } catch (Exception e) { Log.Debug("world bubble: " + e.Message); }
        try { VoicePlayer.Release(c); } catch { }
        if (line.Gesture.Length > 0 && m.Held.Contains(line.SpeakerId)) try { NpcHold.SpeakAs(c, line.Text, seconds, line.Gesture); } catch { }
        try { Api.PonApi.RaiseNpcSaid(InfoOf(member.Record), line.Text); } catch { }
        TimePasses(); // CF-21: MinutesPerMessage, each answer, as in the chat window and group chats
        if (Features.InnerLife && Features.Moods && line.Mood.Length > 0 && _minds != null)
            try { _minds.Felt(member.Id, member.Name, Mind.Affect.Baseline(member.Record.Persona), line.Mood, "moderate", GameNow()); } catch { }
        // The one speaking looks at who it's to (when they're held: the ones you talk to).
        if (m.Held.Contains(line.SpeakerId))
        {
            var cam = GameContext.PlayerCamera();
            Transform? to = line.ToId.Length > 0 && line.ToId != WorldTalk.PlayerId && m.Bodies.TryGetValue(line.ToId, out var tc) && Alive(tc) ? GameContext.HeadOf(tc) : cam != null ? cam.transform : null;
            try { NpcHold.LookAtFor(c, to); } catch { }
        }
        m.Talk.Said(line); // review WC-6: only what was shown is remembered as said
        m.NextAt = now + Mathf.Max(seconds, Mathf.Clamp(line.Text.Length / 16f, 1.5f, 8f)) + 0.4f;
        m.LastAt = now;
        if (line.Leaving && m.Held.Remove(line.SpeakerId)) try { NpcHold.ReleaseMember(c); } catch { }
    }

    /// <summary>
    /// The moment over: everyone let go; who took part keeps it as a conversation; who only overheard (in view and never
    /// talked to, or within a metre) keeps "overheard ... talking about ...". OpenChat ends it first too.
    /// </summary>
    private static void EndWorldMoment(string why, bool quiet = false)
    {
        var m = _moment;
        if (m == null) return;
        _moment = null;
        try { m.Cts.Cancel(); } catch { }
        foreach (var id in m.Held)
            if (m.Bodies.TryGetValue(id, out var c) && Alive(c) && !(ChatOpen && _npc != null && Same(_npc, c))) try { NpcHold.ReleaseMember(c); } catch { }
        m.Held.Clear();
        var talk = m.Talk;
        if (!talk.Transcript.Any(t => t.who == WorldTalk.PlayerId) || m.Generation != _resetGeneration) { WorldLabels.Clear(); return; }
        var brain = ActiveBrain;
        Func<int, double> step = rating => Relationships.Step(rating, Amount(_cfg.RelationshipGainMax.Value, 0, 5, 0.5),
            Amount(_cfg.RelationshipLossMin.Value, 0, 5, 0.1), Amount(_cfg.RelationshipLossMax.Value, 0, 5, 0.5));
        var snap = GameContext.Capture(null);
        int took = 0, heard = 0;
        foreach (var member in talk.Members)
        {
            string id = member.Id;
            if (talk.TookPart(id))
            {
                took++;
                long epoch = brain.EpochFor(id);
                try { Greetings.JustTalked(id, Mathf.Max(1f, _cfg.GreetCooldownMinutes.Value) * 60f); } catch { }
                _ = brain.Mutate(id, r => brain.EpochFor(id) == epoch && !brain.IsKnownMinor(r) && talk.Remember(r, step, DateTime.UtcNow));
                NoteKnownName(member.Record);
            }
            else m.Overhearers.Add(id);
        }
        if (Features.Overhearing)
            foreach (var id in m.Overhearers.Where(id => !talk.TookPart(id)).Take(6))
            {
                NpcRecord? rec = talk.Get(id)?.Record;
                try { rec ??= brain.Store.Glance(id); } catch { }
                if (rec == null || brain.IsKnownMinor(rec) || rec.Visual.Minor) continue; // nothing for someone with no record of their own
                bool knows = rec.State.TimesMet > 0;
                var (lines, summary) = talk.Overheard(knows, rec.State.PlayerName, who => talk.Get(who) is { } x && (x.Record.State.NameRevealed || x.Record.Visual.Named) ? Prompts.FirstName(x.Name) : "Someone");
                _ = brain.RememberOverheardYou(id, lines, summary, snap);
                heard++;
            }
        WorldLabels.Clear();
        if (!quiet) Log.Info($"World chat over ({why}): {took} took part, {heard} overheard.");
    }
}
