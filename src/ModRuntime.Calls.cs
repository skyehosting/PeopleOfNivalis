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
/// People on their own: thinking out loud (remembered as their own thoughts) and phone calls (the game's own phone animation) with
/// someone they know (a phone icon on the bubble; you only hear their side). Both people keep the call, wherever the
/// other one is. Part of NPC chatter, under the same switch, hourly cap and line limits.
/// </summary>
public static partial class ModRuntime
{
    private sealed class Call
    {
        public Character C = null!;
        public string Id = "";
        public Transform Head = null!;
        public string Label = "";
        /// <summary>The other end, when they're a resident: their id (busy while the call lasts), and them, if they're around.</summary>
        /// <summary>Held where they stand for the call (released when it ends).</summary>
        public bool Held;
        public string OtherId = "";
        public Character? OtherC;
        public Transform? OtherHead;
        public string OtherLabel = "";
        public PhoneScript? Script;
        public bool Playing;
        /// <summary>You walked off: the caller ends the call with a reason of their own. No longer counts in the Dynamic pool.</summary>
        public bool WrappingUp;
        public int Next, WaitFor = -1;
        public float NextAt, StartedAt, PausedSince = -1f, WaitSince;
        public readonly Dictionary<int, Task<VoiceOutcome>> Voices = new();
        public readonly CancellationTokenSource Cts = new();
        /// <summary>You walked up to one of them (FB-16): their quick word to you, played at the end of the current line.</summary>
        public Aside? Aside;
        /// <summary>Their word to you is being said until then (the call's next line waits; an ending call doesn't erase it).</summary>
        public float AsideUntil = -1f;
        public Character? AsideWho;
        /// <summary>FB-27: who has answered you during this call; no talk prompt over them until it ends.</summary>
        public readonly HashSet<string> AnsweredYou = new();
        /// <summary>Who overheard what of the call (FB-05), written when it ends.</summary>
        public readonly Overhearing Heard = new();
        /// <summary>FB-05 (review): the call is about the player somewhere: none of it is overheard.</summary>
        public bool AboutPlayer;
    }

    /// <summary>A word to you from someone on the phone, without the chat window (FB-16).</summary>
    private sealed class Aside
    {
        public Character Who = null!;
        public string Id = "", Label = "", Line = "";
        public Transform Head = null!;
        public VoiceOutcome? Voice;
        /// <summary>Written and voiced (until then <see cref="Line"/> is the plain fallback).</summary>
        public bool Ready;
        public float AskedAt;
    }

    /// <summary>The call waits this long at most for their word to you to be written and voiced, then they say the plain line.</summary>
    private const float AsideWaitLimit = 6f;

    private static readonly List<Call> Calls = new();

    /// <summary>Near the player and not with anyone: nobody else within a couple of metres.</summary>
    private static List<Cand> OnTheirOwn(ChatterSettings s, float within, Func<Cand, bool>? keep = null)
    {
        var pc = GameContext.PlayerCharacter();
        if (pc == null) return new List<Cand>();
        var all = ChatterCandidates(s);
        var shopTalk = VenueTalkerSpots(); // not candidates (B2), but company all the same: beside an ordering customer isn't alone
        var bodies = NearBodies(pc);
        // In front of you and the nearest few first, so it's someone you'll see and hear.
        return FrontFirst(all.Where(x => x.GameGroup == IntPtr.Zero && Flat(x.Pos - pc.transform.position).magnitude <= within
                                         && !HasCompany(x, bodies)
                                         && !shopTalk.Any(p => Flat(p - x.Pos).magnitude < ChatterPlan.AloneRadius)
                                         && (keep == null || keep(x))), pc.transform.position) // before the nearest four are taken (review L4)
                  .Take(4).ToList();
    }

    /// <summary>
    /// Anyone near them, not only people free to chat (FB-18): someone across the table who's busy, or a customer at the
    /// counter of someone at work (FB-20), is company all the same. The player isn't (muttering near you is the point).
    /// </summary>
    private static bool HasCompany(Cand x, List<(Character c, IntPtr ptr, Vector3 pos)> bodies)
    {
        var near = new List<(float, bool, bool)>();
        bool meAtWork = AtWork(x.C);
        foreach (var (c, ptr, pos) in bodies)
        {
            if (ptr == x.C.Pointer) continue;
            float d = Flat(pos - x.Pos).magnitude;
            if (d >= ChatterPlan.TableRadius) continue;
            bool seated = false, work = meAtWork;
            try { seated = x.Seated && GameContext.IsSeated(c); work |= AtWork(c); } catch { }
            near.Add((d, seated, work));
        }
        return ChatterPlan.HasCompany(near);
    }

    /// <summary>At work at a food place or shop (the staff actions order talk looks for).</summary>
    private static bool AtWork(Character c)
    {
        try { return StaffActions.Contains(GameContext.ActionName(c)) || GameContext.IsWorkerTask(c); } catch { return false; } // a waiter at a table too (FB-21)
    }

    private static string LabelFor(NpcRecord rec) => DisplayName(rec) == "Stranger" ? "" : Prompts.FirstName(DisplayName(rec));

    // ------------------------------------------------------------------ thinking out loud

    private sealed class Thought
    {
        public Character C = null!;
        public string Id = "";
        public Transform Head = null!;
        public string Label = "";
        public GameSnapshot Snap = new();
        public NpcRecord? Rec;
        public List<string> Lines = new();
        /// <summary>Each line's tone of voice (FB-04), by position.</summary>
        public List<ToneState> Tones = new();
        public string About = "";
        /// <summary>Their epoch when the thought was written: a reset of them since then means it isn't remembered.</summary>
        public long Epoch;
        public bool Ready;
        public int Next, WaitFor = -1;
        public float NextAt, StartedAt, PausedSince = -1f, WaitSince;
        public readonly Dictionary<int, Task<VoiceOutcome>> Voices = new();
        public readonly CancellationTokenSource Cts = new();
    }

    private static readonly List<Thought> Thoughts = new();

    private static bool Thinking(Character c)
    {
        try { return Thoughts.Any(t => t.C != null && t.C.Pointer == c.Pointer); } catch { return false; }
    }

    /// <summary>When each person last started muttering (this session), and how long before they may again (FB-02).</summary>
    private static readonly Dictionary<string, float> LastMuttered = new();
    private const float MutterRest = 600f;

    /// <summary>Someone on their own says what's on their mind, a line or a few; once said, it's kept in their record.</summary>
    private static bool TryThink(ChatterSettings s, out string why)
    {
        why = "";
        if (Thoughts.Count >= Math.Clamp(s.ThinkMax, 1, ChatterSettings.MaxAtOnce)) { why = "enough people are thinking out loud already"; return false; }
        float now = Time.unscaledTime;
        // The same person doesn't mutter again for a while (FB-02: one of them muttered 8 times in an evening).
        var near = OnTheirOwn(s, Math.Max(8f, s.Range), x => !LastMuttered.TryGetValue(x.Id, out var at) || now - at >= MutterRest);
        if (near.Count == 0) { why = "nobody near you is on their own (or they muttered a moment ago)"; return false; }
        var who = near[ChatterRng.Next(near.Count)];
        LastMuttered[who.Id] = now;
        if (LastMuttered.Count > 300) foreach (var k in LastMuttered.Where(kv => now - kv.Value >= MutterRest).Select(kv => kv.Key).ToList()) LastMuttered.Remove(k);
        var t = new Thought { C = who.C, Id = who.Id, Head = GameContext.HeadOf(who.C), Snap = Snapshot(who.C), StartedAt = Time.unscaledTime };
        var visual = GameContext.Visual(who.C);
        int lines = s.ClampedThinkLines;
        Thoughts.Add(t);
        NoteStart("think");
        var chatter = _chatter!;
        var ct = t.Cts.Token;
        Task.Run(async () =>
        {
            (NpcRecord rec, List<string> lines, string about, long epoch, List<ToneState> tones)? thought = null;
            try { thought = await chatter.ThinkAsync(t.Id, visual, t.Snap, ct, lines).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log.Debug("thinking out loud: " + e.Message); }
            MainThread.Enqueue(() =>
            {
                if (!Thoughts.Contains(t)) return;
                if (thought == null) { EndThought(t, "nothing written"); return; }
                t.Rec = thought.Value.rec;
                t.Lines = thought.Value.lines;
                t.About = thought.Value.about;
                t.Epoch = thought.Value.epoch;
                t.Tones = thought.Value.tones;
                t.Label = LabelFor(t.Rec);
                t.Ready = true;
                t.NextAt = Time.unscaledTime + 0.2f;
                for (int i = 0; i < 2; i++) PrefetchThoughtVoice(t, i, ChatterSettingsNow());
            });
        });
        return true;
    }

    private static void PrefetchThoughtVoice(Thought t, int index, ChatterSettings s)
    {
        if (t.Rec == null || index >= t.Lines.Count || t.Voices.ContainsKey(index) || !WithinVoice(t.C, s, s.VoiceBeyond + 5f)) return;
        var rec = t.Rec;
        string text = t.Lines[index];
        var ct = t.Cts.Token;
        var tone = index < t.Tones.Count ? t.Tones[index] : null; // how they sound on this line: their mood, moving with the thought (FB-04)
        t.Voices[index] = Task.Run(() => VoiceForAsync(() => { var v = VoiceSubject.From(rec); if (tone != null) v.Tone = tone; return v; }, text, ct, "think"));
    }

    private static void TickThought(Thought t, ChatterSettings s, bool paused)
    {
        float now = Time.unscaledTime;
        if (!Alive(t.C)) { EndThought(t, "they're gone"); return; }
        var pc = GameContext.PlayerCharacter();
        if (pc != null && Flat(t.C.transform.position - pc.transform.position).magnitude > s.WrapUpDistance)
        {
            if (!t.Ready) try { t.Cts.Cancel(); } catch { }
            EndThought(t, "you walked away");
            return;
        }
        if (!t.Ready)
        {
            if (now - t.StartedAt > 90f) EndThought(t, "took too long to write");
            return;
        }
        if (paused)
        {
            if (t.PausedSince < 0) t.PausedSince = now;
            else if (now - t.PausedSince > PauseLimitSeconds) EndThought(t, "paused too long");
            return;
        }
        t.PausedSince = -1f;
        if (now < t.NextAt) return;
        if (t.Next >= t.Lines.Count) { EndThought(t, "done"); return; }
        bool hearable = WithinVoice(t.C, s, s.VoiceBeyond);
        if (hearable && !t.Voices.ContainsKey(t.Next)) PrefetchThoughtVoice(t, t.Next, s);
        if (t.Voices.TryGetValue(t.Next, out var vt) && !vt.IsCompleted)
        {
            if (!hearable) vt = null;
            else
            {
                if (t.WaitFor != t.Next) { t.WaitFor = t.Next; t.WaitSince = now; }
                if (now - t.WaitSince < VoiceTimeout + 5f) return;
                vt = null;
            }
        }
        VoiceOutcome? voice = null;
        try { voice = vt != null && vt.IsCompletedSuccessfully ? vt.Result : null; } catch { }
        string line = t.Lines[t.Next];
        float volume = LineVolume(t.Id, mumble: true, overheard: true);
        float seconds = voice?.Audio != null ? VoicePlayer.Play(t.C, voice.Audio, volume, VoiceReach(s), 3f) : 0f;
        float shown = Mathf.Max(seconds, Mathf.Clamp(2.2f + line.Length / 15f, 2.5f, 7f));
        Ui.ShowBark(t.Label, line, t.Head, shown + 0.6f, t.Id, maxDistance: s.TextDistance, body: t.C.transform);
        if (_transcripts != null)
            RecordLine("thought", $"thought:{t.Id}:{t.StartedAt:0.0}", t.C, t.Id, t.Rec, line, "",
                t.Rec != null ? $"{t.Rec.State.MoodIntensity} {t.Rec.State.Mood}".Trim() : "", t.Next < t.Tones.Count ? t.Tones[t.Next] : null, "", voice, seconds, by: "ai-background", where: t.Snap);
        if (voice?.Error != null) Problem($"No voice for someone thinking out loud: {voice.Error.TrimEnd('.')}.", "VOICE-FAIL", log: false);
        Log.Debug($"Thinking out loud ({t.Rec?.Persona.Name}) {t.Next + 1}/{t.Lines.Count}: {line}{(seconds > 0 ? "" : hearable ? " (no voice)" : " (too far to hear)")}");
        t.Next++;
        t.NextAt = now + shown + 0.6f + (float)ChatterRng.NextDouble() * 0.8f;
        PrefetchThoughtVoice(t, t.Next, s);
    }

    /// <summary>Over: what they actually said is kept as their thought (nothing, if they never got a word out).</summary>
    private static void EndThought(Thought t, string why)
    {
        try { t.Cts.Cancel(); } catch { }
        Thoughts.Remove(t);
        // Never got a word out because you left or turned to them: they may mutter again (audit A7). Not after a failure,
        // which keeps its rest so someone whose thoughts can't be written isn't asked again on every look.
        if (t.Next == 0 && why is "you walked away" or "you started talking to them") LastMuttered.Remove(t.Id);
        if (why != ResetReason && t.Next > 0 && t.Rec != null && _chatter != null)
        {
            var said = t.Lines.Take(t.Next).ToList();
            var chatter = _chatter;
            Task.Run(async () =>
            {
                try { await chatter.RememberThoughtAsync(t.Id, t.Snap, said, t.About, t.Epoch).ConfigureAwait(false); }
                catch (Exception e) { Log.Debug("remembering a thought: " + e.Message); }
            });
        }
        if (why is not ("done" or "they're gone"))
        {
            try { Ui.HideBark(t.Id); } catch { }
            try { VoicePlayer.Stop(t.C); } catch { }
        }
        try { VoicePlayer.Release(t.C); } catch { }
    }

    // ------------------------------------------------------------------ phone calls

    /// <summary>Someone on their own takes or makes a call with someone they know.</summary>
    private static bool TryPhoneCall(ChatterSettings s, out string why)
    {
        why = "";
        if (Calls.Count >= Math.Clamp(s.PhoneMax, 1, ChatterSettings.MaxAtOnce)) { why = "enough people are on the phone already"; return false; }
        // Never someone at work or in the middle of a visit: holding them for the call would stall the game's steps for them (0.3.3).
        // CF-11: the same "busy working" check the conversations make (it wasn't made for calls).
        var near = OnTheirOwn(s, Mathf.Clamp(s.Range, 5f, 60f), x => x.Holdable && !GameContext.IsBusyWorking(x.C));
        if (near.Count == 0) { why = "nobody near you is on their own and free"; return false; }
        var who = near[ChatterRng.Next(near.Count)];
        var c = who.C;
        // They stop where they are to take the call (they don't turn to anyone).
        var visual = GameContext.Visual(c);
        var snap = Snapshot(c);
        if (!NpcHold.EngageMember(c, _cfg.AnimateNpc.Value)) { why = "they were busy"; return false; }
        NpcHold.LookAtFor(c, null);
        if (_cfg.AnimateNpc.Value) NpcHold.OnThePhone(c); // phone out, on the phone, phone away: the game's own animations
        var call = new Call { C = c, Id = who.Id, Head = GameContext.HeadOf(c), StartedAt = Time.unscaledTime, Held = true };
        Calls.Add(call);
        NoteStart("call");
        int lines = s.ClampedPhoneLines;
        int maxTokens = Math.Clamp(_cfg.MaxGenerationTokens.Value, 1024, 64000);
        var chatter = _chatter!;
        var ct = call.Cts.Token;
        var busy = BusyIds();
        Log.Info($"Phone call starting: {c.name}.");
        Task.Run(async () =>
        {
            PhoneScript? script = null;
            try { script = await chatter.PhoneAsync(call.Id, visual, snap, lines, maxTokens, ct, busy).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log.Warn("A phone call failed: " + e.Message); }
            MainThread.Enqueue(() =>
            {
                if (!Calls.Contains(call)) return;
                if (script == null) { EndCall(call, "nothing written", remember: false); return; }
                call.Script = script;
                call.AboutPlayer = ChatterBrain.AboutThePlayer(script);
                call.Label = LabelFor(script.Caller);
                if (!script.Failed && script.Other.Record is { } other)
                {
                    // They're reserved for the call; if they're around, you see (and hear) their side too. In Dynamic mode
                    // only while the pool of people talking near you has room for them (FLOW-7). Someone around you who's
                    // busy already (shop talk, a conversation, thinking out loud, your chat) isn't taken from it: the call
                    // stays one-sided, as with someone far away, and what they're doing carries on.
                    call.OtherLabel = LabelFor(other);
                    var settings = ChatterSettingsNow();
                    var them = FindAround(other.Id, settings);
                    if (them != null && TalkingNow(them, other.Id, except: call))
                        Log.Debug($"{other.Persona.Name} is busy near you: the call stays one-sided.");
                    else
                    {
                        call.OtherId = other.Id;
                        call.OtherC = them != null && (!settings.Dynamic || PeopleTalking() < settings.DynamicMaxNpcs) ? them : null;
                        if (call.OtherC != null)
                        {
                            call.OtherHead = GameContext.HeadOf(call.OtherC);
                            Ui.HideBark(other.Id);
                            Log.Debug($"{other.Persona.Name} is around: both sides of the call show.");
                        }
                    }
                }
                call.Playing = true;
                call.NextAt = Mathf.Max(Time.unscaledTime + 0.3f, call.AsideUntil); // after their word to you, if they're saying it
                for (int i = 0; i < 3; i++) PrefetchCallVoice(call, i);
            });
        });
        return true;
    }

    private static void PrefetchCallVoice(Call call, int index)
    {
        if (call.Script == null || index >= call.Script.Lines.Count || call.Voices.ContainsKey(index)) return;
        var line = call.Script.Lines[index];
        if (!line.Caller && (call.OtherC == null || call.Script.Other.Record == null)) return; // the other end isn't heard
        var s = ChatterSettingsNow();
        if (!WithinVoice(line.Caller ? call.C : call.OtherC!, s, s.VoiceBeyond + 5f)) return; // too far to hear: no voice asked for
        var rec = line.Caller ? call.Script.Caller : call.Script.Other.Record!;
        var ct = call.Cts.Token;
        call.Voices[index] = Task.Run(() => VoiceForAsync(() => { var v = VoiceSubject.From(rec); v.Tone = line.Tone; return v; }, line.Text, ct, "call"));
    }

    /// <summary>Every frame: the caller's side, line by line, with pauses while the other end talks. They keep doing what they were doing.</summary>
    /// <summary>
    /// You walked out of the voiced range: the caller ends the call with a reason of their own (written with the call).
    /// Not started yet: it's dropped (a call still being written is cancelled). True when it ended right away.
    /// </summary>
    private static bool WrapUpCall(Call call)
    {
        call.WrappingUp = true;
        if (!call.Playing || call.Script == null)
        {
            try { call.Cts.Cancel(); } catch { }
            EndCall(call, "you walked away before it started", remember: false);
            return true;
        }
        if (call.Next == 0) { EndCall(call, "you walked away before they said anything", remember: false); return true; }
        var script = call.Script;
        // The voices fetched ahead were for the lines being replaced (FLOW-6): dropped, so the goodbye is voiced afresh.
        foreach (var k in call.Voices.Keys.Where(k => k >= call.Next).ToList()) call.Voices.Remove(k);
        if (call.Next < script.Lines.Count) script.Lines.RemoveRange(call.Next, script.Lines.Count - call.Next);
        string bye = script.ExitLine(ChatterRng);
        script.Lines.Add(new PhoneLine { Caller = true, Text = bye, Tone = script.Lines.LastOrDefault(l => l.Caller)?.Tone });
        Log.Debug($"wrap-up (phone, {call.C.name}): you walked off; they say: {bye}");
        return false;
    }

    private static void TickCall(Call call, ChatterSettings s, bool paused)
    {
        float now = Time.unscaledTime;
        // Out of sight or out of earshot, the call goes on without you: they still have it, all of it.
        if (!Alive(call.C) || (call.Held && !NpcHold.IsHeld(call.C))) { EndCall(call, "they're gone", wholeCall: true); return; }
        var pc = GameContext.PlayerCharacter();
        if (pc != null)
        {
            // Walked out of the voiced range: they end the call (far, far away: it simply goes on without you).
            float away = Flat(call.C.transform.position - pc.transform.position).magnitude;
            if (away > s.WrapUpDistance + 40f) { EndCall(call, "out of earshot", wholeCall: true); return; }
            if (!call.WrappingUp && away > s.WrapUpDistance && WrapUpCall(call)) return;
        }
        if (call.Aside != null && !paused && TickAside(call, now)) return; // not while the game is paused (console, a menu)
        if (!call.Playing)
        {
            if (now - call.StartedAt > 90f) EndCall(call, "took too long to write", remember: false);
            return;
        }
        if (paused)
        {
            if (call.PausedSince < 0) call.PausedSince = now;
            else if (now - call.PausedSince > PauseLimitSeconds) EndCall(call, "paused too long", wholeCall: true);
            return;
        }
        call.PausedSince = -1f;
        if (now < call.NextAt) return;
        var script = call.Script!;
        if (call.Next >= script.Lines.Count) { EndCall(call, "hung up", wholeCall: true); return; }
        var line = script.Lines[call.Next];
        if (call.OtherC != null && !Alive(call.OtherC)) call.OtherC = null; // they walked off: back to one side
        if (!line.Caller && call.OtherC == null)
        {
            // The other end is talking: they listen for about as long as it takes to say.
            call.NextAt = now + Mathf.Clamp(1.0f + line.Text.Length / 16f, 1.4f, 6f);
            call.Next++;
            PrefetchCallVoice(call, call.Next);
            PrefetchCallVoice(call, call.Next + 1);
            return;
        }
        // You walked up and they brushed you off (FLOW-4): their next line waits until that window closes.
        if (ChatNpcIs(line.Caller ? call.C : call.OtherC!)) { call.NextAt = now + 0.5f; return; }
        bool hearable = WithinVoice(line.Caller ? call.C : call.OtherC!, s, s.VoiceBeyond);
        if (hearable && !call.Voices.ContainsKey(call.Next)) PrefetchCallVoice(call, call.Next);
        if (call.Voices.TryGetValue(call.Next, out var vt) && !vt.IsCompleted)
        {
            if (!hearable) vt = null;
            else
            {
                if (call.WaitFor != call.Next) { call.WaitFor = call.Next; call.WaitSince = now; }
                if (now - call.WaitSince < VoiceTimeout + 5f) return;
                vt = null;
            }
        }
        VoiceOutcome? voice = null;
        try { voice = vt != null && vt.IsCompletedSuccessfully ? vt.Result : null; } catch { }
        float reach = VoiceReach(s);
        var speaker = line.Caller ? call.C : call.OtherC!;
        float volume = LineVolume(line.Caller ? call.Id : call.OtherId, overheard: true);
        float seconds = voice?.Audio != null ? VoicePlayer.Play(speaker, voice.Audio, volume, reach, 3f) : 0f;
        float shown = Mathf.Max(seconds, Mathf.Clamp(1.8f + line.Text.Length / 15f, 2.2f, 8f));
        if (line.Caller)
        {
            Ui.ShowBark(call.Label, line.Text, call.Head, shown + 0.6f, call.Id, phone: true, maxDistance: s.TextDistance, body: call.C.transform);
            if (call.Held) NpcHold.SpeakAs(call.C, line.Text, seconds);
        }
        else
        {
            Ui.ShowBark(call.OtherLabel, line.Text, call.OtherHead!, shown + 0.6f, call.OtherId, phone: true, maxDistance: s.TextDistance, body: call.OtherC!.transform);
            MouthFor(call.OtherC!, line.Text, seconds); // never held, so nothing else moves their mouth (audit A5, CF-04)
        }
        if (_transcripts != null && call.Script != null)
        {
            var who = line.Caller ? call.Script.Caller : call.Script.Other.Record;
            string other = line.Caller ? call.Script.Other.Name : call.Script.Caller?.Persona.Name ?? "";
            RecordLine("phone", $"phone:{call.Id}:{call.StartedAt:0.0}", speaker, line.Caller ? call.Id : call.OtherId, who, line.Text, other,
                line.Mood, line.Tone, "", voice, seconds, by: "ai-background", where: call.Script.Snap, speakerName: line.Caller ? "" : call.Script.Other.Name);
        }
        // Someone standing nearby may catch this side of the call (FB-05), unless it's about the player anywhere.
        if (!call.AboutPlayer)
        try
        {
            var said = line.Caller ? script.Caller : script.Other.Record;
            Overhear(call.Heard, speaker, line.Caller ? call.Id : call.OtherId, said?.Persona.Name ?? "", line.Text, new[] { call.Id, call.OtherId },
                new[] { script.Caller.State.PlayerName, script.Other.Record?.State.PlayerName ?? "" }, script.Snap.PlayerVenues);
        }
        catch (Exception e) { Log.Debug("overhearing a call: " + e.Message); }
        if (voice?.Error != null) Problem($"No voice for a phone call line: {voice.Error.TrimEnd('.')}.", "VOICE-FAIL", log: false);
        Log.Debug($"phone line {call.Next + 1}/{script.Lines.Count}: {(seconds > 0 ? $"voiced, {seconds:0.0} s" : "no voice")}");
        call.NextAt = now + shown + 0.35f;
        call.Next++;
        PrefetchCallVoice(call, call.Next);
        PrefetchCallVoice(call, call.Next + 1);
    }

    /// <summary>
    /// The call is over. They remember it: all of it if it simply went on out of your sight, only what was said if it was
    /// cut short (you started talking to them), nothing after a reset.
    /// </summary>
    /// <summary>
    /// Their word to you, once the line being said has finished (nothing is cut off) and it's ready (or the wait is up: the
    /// plain line then). True when the call has to wait this frame.
    /// </summary>
    private static bool TickAside(Call call, float now)
    {
        var a = call.Aside!;
        if (call.Playing && now < call.NextAt) return false; // the current line finishes first
        if (!a.Ready && now - a.AskedAt < AsideWaitLimit) return call.Playing; // the call (both ends) waits for them
        call.Aside = null;
        float shown = PlayAside(a);
        call.AsideUntil = now + shown + 0.5f;
        call.AsideWho = a.Who;
        if (call.Playing) call.NextAt = Mathf.Max(call.NextAt, call.AsideUntil); // then the call picks up where it was
        return call.Playing;
    }

    /// <summary>They say it: over their head (whatever TextDistance), in their voice, glancing at you; then back to the call.</summary>
    private static float PlayAside(Aside a)
    {
        if (!Alive(a.Who)) return 0f;
        float seconds = a.Voice?.Audio != null ? VoicePlayer.Play(a.Who, a.Voice.Audio, LineVolume(a.Id), _cfg.HearingDistance.Value) : 0f;
        float shown = Mathf.Max(seconds, Mathf.Clamp(1.8f + a.Line.Length / 15f, 2.2f, 6f));
        // Said to you, not into the phone: a plain bubble (FB-28; the phone icon stays on the call's own lines).
        Ui.ShowBark(a.Label, a.Line, a.Head, shown + 0.6f, a.Id, phone: false, maxDistance: 0f, body: a.Who.transform);
        if (_transcripts != null) RecordLine("on the phone, to you", $"aside:{a.Id}:{a.AskedAt:0.0}", a.Who, a.Id, ActiveBrain.Store.Glance(a.Id), a.Line, "you", voice: a.Voice, seconds: seconds, by: a.Ready ? "ai-background" : "written");
        var cam = GameContext.PlayerCamera();
        if (cam != null) NpcHold.Glance(a.Who, cam.transform, shown);
        if (NpcHold.IsHeld(a.Who)) NpcHold.SpeakAs(a.Who, a.Line, seconds); // their mouth moves (on the phone: the pose stays)
        else MouthFor(a.Who, a.Line, seconds); // the other end of a call isn't held (audit A5)
        Log.Debug($"phone: {a.Id} answered you{(seconds > 0 ? $", voiced {seconds:0.0} s" : "")}{(a.Ready ? "" : " (the plain line: the written one took too long)")}");
        return shown;
    }

    private static void EndCall(Call call, string why, bool wholeCall = false, bool remember = true)
    {
        // A word to you still waiting is said once the call's own bubbles and voices are cleared (below), so it isn't erased.
        // Not while the mod stops: its voices were stopped already, and one started now would outlive it (audit A6).
        var pending = why != ResetReason && why != StopReason ? call.Aside : null;
        call.Aside = null;
        bool answering = call.AsideWho != null && Time.unscaledTime < call.AsideUntil; // their word to you is being said now
        try { call.Cts.Cancel(); } catch { }
        Calls.Remove(call);
        if (call.Held) { try { NpcHold.ReleaseMember(call.C); } catch { } call.Held = false; }
        if (remember && why != ResetReason && call.Script != null && _chatter != null)
        {
            int heard = wholeCall ? call.Script.Lines.Count : call.Next;
            RememberOverheard(call.Heard, call.Script.Snap); // only the lines actually said near them
            var script = call.Script;
            var chatter = _chatter;
            Task.Run(async () =>
            {
                try { await chatter.RememberCallAsync(script, heard).ConfigureAwait(false); }
                catch (Exception e) { Log.Debug("remembering a phone call: " + e.Message); }
            });
        }
        if (why != "hung up" && why != "out of earshot")
        {
            // Not the one who's saying their word to you (FB-16 review): that stays until it's said.
            if (!(answering && Same(call.AsideWho, call.C)))
            {
                try { Ui.HideBark(call.Id); } catch { }
                try { VoicePlayer.Stop(call.C); } catch { }
            }
            if (call.OtherC != null && !(answering && Same(call.AsideWho, call.OtherC)))
            {
                try { Ui.HideBark(call.OtherId); } catch { }
                try { VoicePlayer.Stop(call.OtherC); } catch { }
            }
        }
        // Their word to you, still to be said: now, after the call's own bubbles went (they don't leave you hanging).
        if (pending != null) { try { PlayAside(pending); } catch (Exception e) { Log.Debug("phone answer: " + e.Message); } }
        try { VoicePlayer.Release(call.C); VoicePlayer.Release(call.OtherC); } catch { }
        Log.Debug($"Phone call over ({why}): {(call.C != null ? call.C.name : call.Id)}");
    }

    /// <summary>
    /// FB-27: on a call and already answered you once: no talk prompt over them, and T does nothing, until the call ends
    /// (the call going away brings the prompt back by itself). Matched by body: extras share ids (review L1). Someone taking
    /// a call at a table is hushed while that call is going on (review L2).
    /// </summary>
    private static bool HushedOnCall(Character c)
    {
        try
        {
            foreach (var k in Calls)
                if ((k.AnsweredYou.Contains(k.Id) && Same(k.C, c)) || (k.OtherId.Length > 0 && k.AnsweredYou.Contains(k.OtherId) && Same(k.OtherC, c)))
                    return true;
            foreach (var g in Groups)
            {
                var call = g.Script?.Call;
                if (call == null || !call.Answered || !g.Playing) continue;
                var lines = g.Script!.Lines;
                int first = lines.FindIndex(l => l.Phone || l.FarEnd), last = lines.FindLastIndex(l => l.Phone || l.FarEnd);
                if (first < 0 || g.Next <= first || g.Next > last + 1) continue; // the call part: from its first line to its last
                if (g.Members.Any(m => m.Id == call.HolderId && Same(m.C, c))) return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>On a call: making it, or the one on the other end (around here or not).</summary>
    private static bool OnACall(Character c, string id = "")
    {
        try
        {
            return Calls.Any(k => (k.C != null && k.C.Pointer == c.Pointer) || (k.OtherC != null && k.OtherC.Pointer == c.Pointer)
                                  || (id.Length > 0 && k.OtherId == id))
                   || (id.Length > 0 && Groups.Any(g => g.Script?.Call?.Callee.Record?.Id == id)); // on the phone to someone at a table (review M2)
        }
        catch { return false; }
    }

    /// <summary>
    /// Everyone who wouldn't pick up a call right now: in a residents' conversation (or due to join one), on a call,
    /// thinking out loud, in shop talk, or talking to you (B2).
    /// </summary>
    private static HashSet<string> BusyIds()
    {
        var busy = new HashSet<string>();
        foreach (var g in Groups)
        {
            foreach (var m in g.Members) if (!m.Gone) busy.Add(m.Id);
            if (g.Script?.Call?.Callee.Record?.Id is { Length: > 0 } rung) busy.Add(rung); // on the phone to someone at a table (FB-19)
        }
        foreach (var k in Calls)
        {
            busy.Add(k.Id);
            if (k.OtherId.Length > 0) busy.Add(k.OtherId);
            if (k.Script?.Other.Record?.Id is { Length: > 0 } callee) busy.Add(callee); // also one left unclaimed (busy near you)
        }
        foreach (var t in Thoughts) busy.Add(t.Id);
        foreach (var t in VenueTalks) if (!t.Cancelled) { busy.Add(t.StaffId); busy.Add(t.CustomerId); }
        try { if (ChatOpen && _npc != null) busy.Add(_session?.Record.Id ?? GameContext.NpcId(_npc)); } catch { }
        return busy;
    }

    /// <summary>This resident, if they're in the world near you right now (busy or not: the call decides what that means).</summary>
    private static Character? FindAround(string id, ChatterSettings s)
    {
        try
        {
            var pc = GameContext.PlayerCharacter();
            var all = Character.instances;
            if (pc == null || all == null) return null;
            var it = all.GetEnumerator();
            while (it.MoveNext())
            {
                var c = it.Current;
                if (!Alive(c) || c.gameObject == pc.gameObject) continue;
                if (Flat(c.transform.position - pc.transform.position).magnitude > Math.Max(15f, s.Range * 1.6f)) continue;
                if (GameContext.NpcId(c) == id) return c;
            }
        }
        catch { }
        return null;
    }
}
