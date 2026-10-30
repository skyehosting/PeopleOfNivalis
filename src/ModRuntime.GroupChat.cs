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

namespace NivalisAIChat;

/// <summary>
/// FB-66 (the author: "when entering a group, it would open a chat window with all members ... voices queued one after
/// another, with a Switchboard override (off by default) ... heads turn to the speaker"): talking to someone in a
/// residents' conversation brings you into it. One window for everyone; each thing you say is one request
/// (Npc/GroupChat.cs) and 0-3 of them answer; their lines play one after another (GroupVoicesOverlap lets them
/// overlap a little), every head turning to whoever speaks. Each remembers it in their own record when it ends.
/// Never anyone under 18.
/// </summary>
public static partial class ModRuntime
{
    private sealed class GroupBody
    {
        public Character C = null!;
        public string Id = "";
        public Transform Head = null!;
        public bool Lead, Light, Released;
        public NpcVisual Visual = new();
        public GameSnapshot Snap = new();
        public long Epoch;
        public float LeaveAt = -1f;
    }

    private sealed class GroupSession
    {
        public readonly GroupChat Chat = new();
        public readonly List<GroupBody> Bodies = new();
        public readonly Queue<(GroupChat.Line line, Task<VoiceOutcome>? voice, float queuedAt)> Queue = new();
        public readonly CancellationTokenSource Cts = new();
        public bool Ready;
        public float NextAt;
        public int Serial;
    }

    private static GroupSession? _group;

    internal static bool InGroupChat => _group != null;

    /// <summary>
    /// OpenChat: <paramref name="c"/> is in a residents' conversation with others: the window opens for all of them.
    /// False when they aren't (or group chat is off): a one-on-one opens as before.
    /// </summary>
    private static bool TryOpenGroupChat(Character c, Camera cam)
    {
        if (!Features.GroupChat || _resetting) return false;
        var g = Groups.FirstOrDefault(x => x.Members.Any(m => Same(m.C, c) && m.Joined && !m.Gone));
        if (g == null) return false;
        var members = g.Members.Where(m => m.Joined && !m.Gone && Alive(m.C)).ToList();
        // The one you spoke to first, then up to three others (never anyone under 18).
        members = members.OrderByDescending(m => Same(m.C, c)).Where(m => !MinorInfo(m.C, m.Id).minor).Take(4).ToList();
        if (members.Count < 2 || !Same(members[0].C, c)) return false;
        var picked = members.Select(m => (m.C, m.Id, m.Head, m.Light)).ToList();
        EndGroup(g, "the newcomer joined in"); // their own conversation stops (they're let go: held again just below)

        var s = new GroupSession { Serial = ++_openSerial };
        var brain = ActiveBrain;
        foreach (var (mc, id, head, light) in picked)
        {
            var body = new GroupBody { C = mc, Id = id, Head = head, Lead = Same(mc, c), Light = light, Epoch = brain.EpochFor(id) };
            try { body.Visual = GameContext.Visual(mc); body.Snap = GameContext.Capture(mc); } catch { }
            s.Bodies.Add(body);
        }
        try
        {
            NpcHold.Engage(c, cam.transform, _cfg.AnimateNpc.Value);
            foreach (var b in s.Bodies.Where(b => !b.Lead))
            {
                NpcHold.TakeOver(b.C); // review F3: still stepping back into a pose from their own talk
                bool held = b.Light ? NpcHold.EngageLight(b.C, _cfg.AnimateNpc.Value) : NpcHold.EngageMember(b.C, _cfg.AnimateNpc.Value);
                if (!held) { b.Released = true; continue; }
                NpcHold.LookAtFor(b.C, cam.transform);
            }
        }
        catch (Exception e) { Log.Debug("group hold: " + e.Message); }
        s.Bodies.RemoveAll(b => b.Released);
        Locks.Engage(_cfg.PauseTimeWhileChatting.Value);
        _group = s;
        _npc = c;
        _visual = s.Bodies[0].Visual;
        _busy = false;
        _closeAt = -1;
        _queued = "";
        _unsent.Clear();
        var place = s.Bodies[0].Snap;
        s.Chat.Where = place.Location;
        s.Chat.Day = place.GameDay;
        s.Chat.Clock = place.Clock;
        s.Chat.PlayerName = place.PlayerName;
        Ui.Open("A group conversation", $"{s.Bodies.Count} people");
        Ui.SetRelationship(null);
        Ui.SetTogether(false);
        Ui.SetNpcName("They");
        Ui.SetInputEnabled(false);
        Ui.SetThinking("They turn to you");
        Ui.MaxLines = Math.Clamp(_cfg.HistoryShown.Value, 0, 2000) + 60;
        Log.Info($"Group chat: you join {s.Bodies.Count} people talking.");

        // Everyone is someone (made now if new): read off the game's thread.
        var ct = s.Cts.Token;
        var wants = s.Bodies.Select(b => (b.Id, b.Visual, b.Snap)).ToList();
        Task.Run(async () =>
        {
            var recs = new List<NpcRecord?>();
            foreach (var (id, visual, snap) in wants)
            {
                try { recs.Add(await brain.EnsurePersonAsync(id, visual, snap, ct).ConfigureAwait(false)); }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { Log.Debug("group person: " + e.Message); recs.Add(null); }
            }
            MainThread.Enqueue(() =>
            {
                if (_group != s || s.Serial != _openSerial) return;
                for (int i = 0; i < s.Bodies.Count && i < recs.Count; i++)
                {
                    var rec = recs[i];
                    if (rec == null || _brain.IsKnownMinor(rec)) continue;
                    s.Chat.Members.Add(new GroupChat.Member { Record = rec, Snap = s.Bodies[i].Snap });
                }
                if (s.Chat.Members.Count == 0) { CloseChat("nobody in the group could be read"); return; }
                Labels(s);
                string names = string.Join(", ", s.Chat.Members.Select(m => m.Label));
                Ui.SetHeader(names, "a group conversation");
                Ui.SetStatus("");
                Ui.AddLine(LineKind.Notice, "", $"You join {names}. Talk to everyone, or to one of them by name.");
                Ui.SetInputEnabled(true);
                s.Ready = true;
            });
        });
        return true;
    }

    /// <summary>How each looks on the window: the name they gave you, or "Stranger (Woman)" (numbered when two look alike).</summary>
    private static void Labels(GroupSession s)
    {
        foreach (var m in s.Chat.Members)
        {
            string shown = DisplayName(m.Record);
            m.Label = shown != "Stranger" ? Prompts.FirstName(shown) : $"Stranger ({Population.PerceivedLabel(m.Record.Visual.IsAndroid ? "android" : m.Record.Visual.Body)})";
        }
        foreach (var same in s.Chat.Members.GroupBy(m => m.Label).Where(g => g.Count() > 1))
        {
            int n = 1;
            foreach (var m in same) m.Label = $"{m.Label} {n++}";
        }
    }

    /// <summary>Send: your words to the group (one request; their answers queue up to be played).</summary>
    private static void GroupSend(string text)
    {
        var s = _group!;
        if (Ui.AddLine(LineKind.Player, "You", text) is { } shown) _unsent.Add(shown);
        if (!s.Ready || _busy) { _queued = _queued.Length > 0 ? _queued + " " + text : text; return; }
        GroupAsk(s, text);
    }

    private static void GroupAsk(GroupSession s, string text)
    {
        _busy = true;
        _idle = 0;
        Ui.SetThinking("They're thinking");
        var cam = GameContext.PlayerCamera();
        foreach (var b in s.Bodies.Where(b => !b.Released)) NpcHold.LookAtFor(b.C, cam != null ? cam.transform : null); // all eyes on you
        int maxTokens = Math.Clamp(_cfg.MaxGenerationTokens.Value, 1024, 64000);
        // The inner-life round: who answers, who may chime in or cut in, decided here (inside the one request).
        Mind.GroupTurns.Plan? turns = null;
        if (Features.InnerLife)
            try
            {
                long now = GameNow();
                var present = s.Chat.Present.ToList();
                var turnMembers = present.Select(m => new Mind.TurnMember
                {
                    Id = m.Id, Name = m.Name, Label = m.Label, Arousal = _minds?.Point(m.Id, now)?.a ?? Tone.CalmArousal,
                    Confronts = (m.Record.Persona.ConflictStyle ?? "").Contains("confront", StringComparison.OrdinalIgnoreCase),
                    Talkative = Mind.Telling.Gossipy(m.Record.Persona),
                }).ToList();
                var last = s.Chat.Transcript.LastOrDefault(t => t.who != GroupChat.PlayerId);
                bool asked = last.text?.TrimEnd().EndsWith("?") == true;
                string lead = s.Bodies.FirstOrDefault(b => b.Lead)?.Id ?? "";
                if (turnMembers.Count > 0) turns = Mind.GroupTurns.Decide(text, turnMembers, last.who ?? "", asked, lead, new System.Random());
            }
            catch (Exception e) { Log.Debug("group turns: " + e.Message); }
        var req = s.Chat.Request(text, (a, b) => _pairs?.Get(a, b), maxTokens, turns: turns?.Note ?? "");
        var ct = s.Cts.Token;
        var llm = _llm;
        var members = s.Chat.Members.ToDictionary(m => m.Id, m => m.Record);
        var shown = _unsent.ToList();
        _unsent.Clear();
        Task.Run(async () =>
        {
            GroupChat.Reply reply;
            try
            {
                var res = await llm.CompleteAsync(req, ct).ConfigureAwait(false);
                reply = res.Ok ? s.Chat.Parse(res.Text) : new GroupChat.Reply { Error = res.Error ?? "no answer" };
                if (reply.Ok && turns != null) reply.KeepTo(turns.Speakers); // held to the turns decided (never down to nothing)
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { reply = new GroupChat.Reply { Error = e.Message }; }
            var voices = new List<Task<VoiceOutcome>?>();
            foreach (var line in reply.Lines)
                voices.Add(members.TryGetValue(line.SpeakerId, out var rec)
                    ? Task.Run(() => VoiceForAsync(() => VoiceSubject.From(rec), line.Text, ct, "you")) // your conversation, not residents' talk
                    : null);
            MainThread.Enqueue(() =>
            {
                if (_group != s || s.Serial != _openSerial) return;
                _busy = false;
                Ui.SetStatus("");
                if (!reply.Ok)
                {
                    foreach (var l in shown) Ui.RemoveLine(l);
                    Problem("The group's answer failed: " + reply.Error);
                    Ui.AddLine(LineKind.Notice, "", "Your message is back in the box: press Enter to send it again.");
                    PutBack(text);
                    return;
                }
                s.Chat.Heard(text, reply, reply.Lines.Count);
                if (reply.Lines.Count == 0) Ui.AddLine(LineKind.Notice, "", "Nobody answers; they wait for you to go on.");
                for (int i = 0; i < reply.Lines.Count; i++) s.Queue.Enqueue((reply.Lines[i], voices[i], Time.unscaledTime));
            });
        });
    }

    /// <summary>UpdateChat, in a group: their lines one after another, heads turning to the speaker; leavers walk off.</summary>
    private static void TickGroupChat()
    {
        var s = _group;
        if (s == null) return;
        float now = Time.unscaledTime;
        foreach (var b in s.Bodies.Where(b => b.LeaveAt > 0 && now >= b.LeaveAt && !b.Released).ToList()) LetGroupMemberGo(s, b);
        if (s.Ready && !s.Chat.Present.Any() && s.Queue.Count == 0 && _closeAt < 0)
        {
            Ui.SetInputEnabled(false);
            Ui.SetStatus("They've all gone.");
            _closeAt = now + 2.5f;
            return;
        }
        if (s.Ready && !_busy && s.Queue.Count == 0 && _queued.Length > 0 && now >= s.NextAt) { string q = _queued; _queued = ""; GroupAsk(s, q); return; }
        if (s.Queue.Count == 0 || now < s.NextAt || Ui.Revealing && !_cfg.GroupVoicesOverlap.Value) return;
        var (line, voiceTask, queuedAt) = s.Queue.Peek();
        if (voiceTask != null && !voiceTask.IsCompleted && now - queuedAt < 8f) return; // their voice a moment longer
        s.Queue.Dequeue();
        var body = s.Bodies.FirstOrDefault(b => b.Id == line.SpeakerId && !b.Released);
        var member = s.Chat.Members.FirstOrDefault(m => m.Id == line.SpeakerId);
        if (body == null || member == null || !Alive(body.C)) return;
        VoiceOutcome? voice = null;
        try { if (voiceTask is { IsCompletedSuccessfully: true }) voice = voiceTask.Result; } catch { }
        float seconds = 0f;
        try { if (voice?.Audio != null) seconds = VoicePlayer.Play(body.C, voice.Audio, LineVolume(body.Id), _cfg.HearingDistance.Value); } catch { }
        float reading = Mathf.Clamp(line.Text.Length / 16f, 1.5f, 12f);
        Ui.Speed = _cfg.TextSpeed.Value;
        Ui.AddLine(LineKind.Npc, member.Label, line.Text, typewriter: true, revealSeconds: seconds > 0 ? seconds : 0f);
        if (Features.Roleplay && line.Action.Length > 0) // what they do as they say it (their pronouns, yours from your Gender)
        {
            string act = Roleplay.Clean(line.Action, Roleplay.Of(member.Record.Persona.Pronouns), Roleplay.Player(_cfg.PlayerGender.Value));
            if (act.Length > 0)
            {
                Ui.AddAfterReveal(LineKind.Action, act);
                if (line.Gesture.Length == 0) line.Gesture = Roleplay.GestureFor(act);
            }
        }
        try { Api.PonApi.RaiseNpcSaid(InfoOf(member.Record), line.Text); } catch { } // FB-67
        TimePasses(); // MinutesPerMessage
        if (Features.InnerLife && Features.Moods && line.Mood.Length > 0 && _minds != null) // the inner-life round: how they felt saying it
            try { _minds.Felt(member.Id, member.Name, Mind.Affect.Baseline(member.Record.Persona), line.Mood, "moderate", GameNow()); } catch { }
        if (body.Lead) NpcHold.Speak(line.Text, seconds, line.Gesture);
        else NpcHold.SpeakAs(body.C, line.Text, seconds, line.Gesture);
        // Heads turn to whoever's speaking; the speaker looks at who they're talking to.
        var cam = GameContext.PlayerCamera();
        foreach (var o in s.Bodies.Where(o => o != body && !o.Released)) NpcHold.LookAtFor(o.C, body.Head);
        var to = s.Bodies.FirstOrDefault(o => o.Id == line.ToId && !o.Released);
        NpcHold.LookAtFor(body.C, to != null ? to.Head : cam != null ? cam.transform : null);
        float dur = Mathf.Max(seconds, reading) + 0.4f;
        s.NextAt = now + (_cfg.GroupVoicesOverlap.Value ? Mathf.Min(dur, 1.2f) : dur);
        if (line.Leaving) { body.LeaveAt = now + dur; Ui.AddLine(LineKind.Notice, "", $"{member.Label} heads off."); }
    }

    private static void LetGroupMemberGo(GroupSession s, GroupBody b)
    {
        b.Released = true;
        try
        {
            if (b.Lead) NpcHold.Release(); // the window stays for the others
            else NpcHold.ReleaseMember(b.C);
        }
        catch (Exception e) { Log.Debug("group leave: " + e.Message); }
    }

    /// <summary>CloseChat, in a group: everyone is let go, and each keeps what was said (in their own record).</summary>
    private static void EndGroupChat()
    {
        var s = _group;
        if (s == null) return;
        _group = null;
        try { s.Cts.Cancel(); } catch { }
        foreach (var b in s.Bodies.Where(b => !b.Lead && !b.Released)) { b.Released = true; try { NpcHold.ReleaseMember(b.C); } catch { } }
        var brain = ActiveBrain;
        Func<int, double> step = rating => Relationships.Step(rating, Amount(_cfg.RelationshipGainMax.Value, 0, 5, 0.5),
            Amount(_cfg.RelationshipLossMin.Value, 0, 5, 0.1), Amount(_cfg.RelationshipLossMax.Value, 0, 5, 0.5));
        var chat = s.Chat;
        foreach (var m in chat.Members)
        {
            var body = s.Bodies.FirstOrDefault(b => b.Id == m.Id);
            long epoch = body?.Epoch ?? brain.EpochFor(m.Id);
            string id = m.Id;
            try { Greetings.JustTalked(id, Mathf.Max(1f, _cfg.GreetCooldownMinutes.Value) * 60f); } catch { }
            _ = brain.Mutate(id, r => brain.EpochFor(id) == epoch && !brain.IsKnownMinor(r) && chat.Remember(r, step, DateTime.UtcNow));
            NoteKnownName(m.Record);
        }
        Log.Info($"Group chat over: {chat.Transcript.Count} lines with {chat.Members.Count} people.");
    }
}
