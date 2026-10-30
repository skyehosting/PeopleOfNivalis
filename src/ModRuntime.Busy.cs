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
/// Walking up to someone who's busy talking. On the phone (or about to be, phone in hand): no window opens; they glance
/// at you, tell you in a word or two over their head and in their voice, and get back to the call, which waits for them
/// (FB-16, the author's design). In a conversation with others: a stranger butting in is usually told "not now" in the
/// window, while someone who knows (and likes) you may break off to talk to you.
/// </summary>
public static partial class ModRuntime
{
    private sealed class Busy
    {
        public string Notice = "", Situation = "";
        public string[] Fallbacks = Array.Empty<string>();
        /// <summary>The phone call they're on (FB-16: answered without the window), or null for a conversation.</summary>
        public Call? Call;
    }

    /// <summary>When each person last answered you from the phone: pressing T again soon after does nothing (no window to stop it).</summary>
    private static readonly Dictionary<string, float> PhoneAnsweredAt = new();
    private const float PhoneAnswerCooldown = 10f;

    private static readonly string[] PhoneBrushOffs = { "Sorry, I'm on a call.", "One sec, I'm on the phone.", "Hang on, I'm on a call.", "Can't talk, I'm on the phone." };
    private static readonly string[] StrangerBrushOffs = { "Sorry, we're in the middle of something.", "Not now, we're talking.", "Give us a minute, yeah?", "Bit busy here." };
    private static readonly string[] FriendBrushOffs = { "Hey, catch me in a bit?", "Give me a minute, we're just finishing up.", "Can it wait a sec?", "Talk later, yeah?" };

    private static bool Same(Character? a, Character? b)
    {
        try { return a != null && b != null && a.Pointer == b.Pointer; } catch { return false; }
    }

    /// <summary>
    /// Whether they're too busy to talk to you right now (on the phone, or in a conversation that doesn't let you in), and
    /// what the window says then. <paramref name="interruptedWith"/>: they were in a conversation but let you in (who with).
    /// <paramref name="rec"/>: their record as the chat opened with it (a glance; null for someone new or unreadable).
    /// </summary>
    private static Busy? BusyWith(Character c, string id, NpcRecord? rec, out string interruptedWith)
    {
        interruptedWith = "";
        bool known = rec != null && (rec.State.TimesMet > 0 || rec.State.MetInStory);
        string first = rec != null && rec.State.NameRevealed ? Prompts.FirstName(DisplayName(rec)) : "They";
        string who = rec != null && rec.State.PlayerName.Length > 0 ? rec.State.PlayerName : known ? "Someone you've talked with before" : "A stranger";

        // The caller is busy from the moment the call starts (phone in hand while it's being written), not only once it
        // plays: a T then opened a normal chat and the call was lost (the author's 0.3.0 test). The far end, once it plays.
        var call = Calls.FirstOrDefault(x => Same(x.C, c) || (x.Playing && Same(x.OtherC, c)));
        if (call != null)
        {
            string other = Same(call.OtherC, c) ? call.Script?.Caller?.Persona?.Name ?? "someone" : call.Script?.Other.Name is { Length: > 0 } o ? o : "someone";
            return new Busy
            {
                Notice = first == "They" ? "They're on the phone." : $"{first} is on the phone.",
                Situation = $"You're on a phone call with {other}. {who} walks up and starts talking to you. Without hanging up, " +
                            "tell them you're on a call, the way you would: a quick word.",
                Fallbacks = PhoneBrushOffs,
                Call = call,
            };
        }

        // Someone who has said their goodbye isn't kept by it: they turn to you, and the others carry on (B3).
        var g = Groups.FirstOrDefault(x => x.Members.Any(m => m.Joined && !m.Gone && !Leaving(m) && Same(m.C, c)));
        if (g == null || !g.Playing || g.Next == 0 || g.WrappingUp) return null; // nothing under way to interrupt
        var others = g.Members.Where(m => m.Joined && !m.Gone && !Same(m.C, c))
            .Select(m => g.Script!.People.TryGetValue(m.Id, out var r) ? r.Persona.Name : "someone").ToList();
        if (others.Count == 0) return null;
        string names = others.Count == 1 ? others[0] : string.Join(", ", others.Take(others.Count - 1)) + " and " + others[^1];
        if (LetsYouIn(rec))
        {
            interruptedWith = names;
            return null;
        }
        return new Busy
        {
            Notice = first == "They" ? $"They're talking with {(known ? names : "someone")}." : $"{first} is talking with {(known ? names : "someone")}.",
            Situation = $"You're in the middle of a conversation with {names}. {who} walks up and tries to talk to you. Tell them it's not a " +
                        $"good time, the way you would{(known ? " with someone you know" : " with a stranger who just butted in")}: a few words.",
            Fallbacks = known ? FriendBrushOffs : StrangerBrushOffs,
        };
    }

    private static bool LetsYouIn(NpcRecord? rec)
    {
        bool known = rec != null && (rec.State.TimesMet > 0 || rec.State.MetInStory);
        double p = ChatterPlan.JoinInChance(known, rec?.State.Affinity ?? 0, rec == null || Moods.Open.Contains(rec.State.Mood));
        return ChatterRng.NextDouble() < p;
    }

    /// <summary>
    /// On the phone (FB-16): no window. They glance at you and tell you in a word or two, over their head and in their
    /// voice, at the end of the line they're saying (the call waits for it: no voice cut off), then get back to the call.
    /// Someone met before says it in their own words and remembers it with your conversations (BarkAsync); someone new
    /// says a plain line in a voice that fits their looks. Pressing T again soon after does nothing.
    /// </summary>
    private static void AnswerFromPhone(Character c, string id, Busy busy, NpcRecord? known)
    {
        var call = busy.Call!;
        float now = Time.unscaledTime;
        if (call.Aside != null) return; // already answering you
        if (PhoneAnsweredAt.TryGetValue(id, out float at) && now - at < PhoneAnswerCooldown) return;
        PhoneAnsweredAt[id] = now;
        foreach (var k in PhoneAnsweredAt.Where(kv => now - kv.Value > 600f).Select(kv => kv.Key).ToList()) PhoneAnsweredAt.Remove(k);
        if (known != null && ActiveBrain.IsKnownMinor(known)) known = null; // never a child's age or looks to the AI or a voice provider
        bool caller = Same(call.C, c);
        string fallback = busy.Fallbacks[ChatterRng.Next(busy.Fallbacks.Length)];
        var aside = new Aside
        {
            Who = c, Id = id, Line = fallback, AskedAt = now,
            Head = caller ? call.Head : call.OtherHead ?? GameContext.HeadOf(c),
            Label = caller ? call.Label : call.OtherLabel,
        };
        call.Aside = aside;
        call.AnsweredYou.Add(id); // FB-27: no talk prompt over them for the rest of this call
        var snap = GameContext.Capture(c);
        snap.PlayerGender = _cfg.PlayerGender.Value;
        bool ai = known != null && _cfg.AiGreetingLines.Value && AiAllowed();
        var brain = ActiveBrain;
        var cid = c; // a stranger's voice is chosen by looks, on this thread
        var stranger = known == null ? SubjectFor(cid, id) : null;
        Log.Debug($"busy: {id} is on the phone: answers you without the window ({busy.Notice})");
        Task.Run(async () =>
        {
            string line = fallback;
            try
            {
                if (known != null) line = await brain.BarkAsync(id, busy.Situation, fallback, known.State.Mood, snap, ai, CancellationToken.None).ConfigureAwait(false) ?? fallback;
            }
            catch (Exception e) { Log.Debug("phone answer line: " + e.Message); }
            VoiceOutcome? voice = null;
            try { voice = await VoiceForAsync(() => known != null ? VoiceSubject.From(known) : stranger!, line, CancellationToken.None, "you").ConfigureAwait(false); }
            catch { }
            MainThread.Enqueue(() => { aside.Line = line; aside.Voice = voice; aside.Ready = true; });
        });
    }

    /// <summary>
    /// They're busy: the window opens with what they're doing, they tell you themselves (in their voice; the AI writes it
    /// when it can, else a plain line), and the window closes on its own. They carry on as they were; they remember it.
    /// </summary>
    /// <param name="known">Their record as the chat opened with it (a glance; the AI line goes through BarkAsync, which reads them properly).</param>
    private static void BrushOff(Character c, string id, Busy busy, NpcRecord? known)
    {
        if (busy.Call != null) { AnswerFromPhone(c, id, busy, known); return; }
        var visual = GameContext.Visual(c);
        if (known != null && ActiveBrain.IsKnownMinor(known)) known = null; // never a child's age or looks to the voice provider: a plain line
        bool revealed = known != null && known.State.NameRevealed;
        // FB-57: a name the game shows over their head is the one on the window, even for someone met before under another
        string shownName = visual.NameShown && !visual.Named ? visual.GameName.Trim() : "";
        string title = shownName.Length > 0 ? shownName : known != null && revealed ? DisplayName(known) : visual.KnownByName() && visual.GameName.Trim().Length > 0 ? visual.GameName : "Stranger";
        string speaker = shownName.Length > 0 ? Prompts.FirstName(shownName) : known != null ? SpeakerName(known) : visual.KnownByName() && visual.GameName.Trim().Length > 0 ? Prompts.FirstName(visual.GameName) : "Stranger";
        Ui.HideBark();
        _npc = c;
        _visual = visual;
        _busy = true;
        _closeAt = Time.unscaledTime + 30f; // nothing to send; it closes once they've said their piece
        int serial = ++_openSerial;
        Locks.Engage(false);
        Ui.Open(title, known != null ? GenderLabel(known) : Population.PerceivedLabel(visual.IsAndroid ? "android" : visual.Body));
        Ui.SetNpcName(speaker);
        Ui.SetInputEnabled(false);
        Ui.AddLine(LineKind.Notice, "", busy.Notice);
        Ui.SetThinking($"{(speaker == "Stranger" ? "They" : speaker)} glance{(speaker == "Stranger" ? "" : "s")} over");
        Ui.ScrollToBottom(true);
        var snap = GameContext.Capture(c);
        snap.PlayerGender = _cfg.PlayerGender.Value;
        string fallback = busy.Fallbacks[ChatterRng.Next(busy.Fallbacks.Length)];
        bool ai = known != null && _cfg.AiGreetingLines.Value && AiAllowed();
        var brain = ActiveBrain;
        Log.Debug($"busy: {id} brushed you off ({busy.Notice})");
        Task.Run(async () =>
        {
            string line = fallback;
            try
            {
                // Someone you've met before says it in their own words (and remembers it); someone new, a plain line.
                if (known != null) line = await brain.BarkAsync(id, busy.Situation, fallback, known.State.Mood, snap, ai, CancellationToken.None).ConfigureAwait(false) ?? fallback;
            }
            catch (Exception e) { Log.Debug("brush-off line: " + e.Message); }
            VoiceOutcome? voice = null;
            if (known != null)
            {
                try { voice = await VoiceForAsync(() => VoiceSubject.From(known), line, CancellationToken.None, "you").ConfigureAwait(false); }
                catch { }
            }
            MainThread.Enqueue(() =>
            {
                if (serial != _openSerial || !Same(_npc, c)) return; // closed meanwhile
                Ui.SetStatus(busy.Notice);
                float seconds = voice?.Audio != null ? VoicePlayer.Play(c, voice.Audio, LineVolume(id), _cfg.HearingDistance.Value) : 0f;
                Ui.SmoothFade = _cfg.SmoothTextFade.Value;
                Ui.Speed = _cfg.TextSpeed.Value;
                Ui.AddLine(LineKind.Npc, speaker, line, typewriter: true, revealSeconds: seconds);
                if (_transcripts != null) RecordLine("too busy for you", $"busy:{id}:{Time.unscaledTime:0.0}", c, id, known, line, "you", voice: voice, seconds: seconds,
                    by: ai ? "ai-background" : "written", where: snap);
                _closeAt = Time.unscaledTime + Mathf.Max(seconds + 1.8f, 2.6f + line.Length / 30f);
            });
        });
    }
}
