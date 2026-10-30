using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Nivalis;
using NivalisAIChat.Ai;
using NivalisAIChat.Dev;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using NivalisAIChat.UI;
using NivalisAIChat.Voice;
using UnityEngine;
using UnityEngine.InputSystem;


namespace NivalisAIChat;

/// <summary>Voices for NPC lines: waiting for them, timing out, telling the player when they fail.</summary>
public static partial class ModRuntime
{
    private static float VoiceTimeout => Math.Clamp(_cfg.VoiceTimeoutSeconds.Value, 3f, 60f);

    /// <summary>
    /// How loud one of this person's lines plays: your Volume, times their own level (each person gets one inside
    /// PersonVolume and keeps it; inside MumbleVolume when they're thinking out loud), times 0.6 for lines you overhear
    /// while your own chat is open.
    /// </summary>
    private static float LineVolume(string? id, bool mumble = false, bool overheard = false)
    {
        var range = mumble ? VoiceLevel.Parse(_cfg.MumbleVolume.Value, VoiceLevel.MumbleDefault)
                           : VoiceLevel.Parse(_cfg.PersonVolume.Value, VoiceLevel.PersonDefault);
        float v = _cfg.VoiceVolume.Value / 100f * VoiceLevel.For(id, range);
        return overheard && ChatOpen ? v * 0.6f : v;
    }

    /// <summary>How the person you're talking to sounds, as your conversation has gone so far (one per open chat).</summary>
    private static readonly Dictionary<string, ToneState> ChatTones = new();

    /// <summary>
    /// Their voice for a reply in your chat: the tone starts from their mood when the chat opens, then each reply moves it
    /// part of the way toward how they feel now (Tone.Feel), so it changes with the conversation, not jumps with each line.
    /// </summary>
    private static VoiceSubject Toned(NpcRecord rec, bool start)
    {
        var v = VoiceSubject.From(rec);
        var st = rec.State ?? new NpcState();
        lock (ChatTones)
        {
            if (start || !ChatTones.TryGetValue(rec.Id ?? "", out var t))
            {
                t = Tone.Start(Features.Mood(st.Mood), st.MoodIntensity);
                ChatTones[rec.Id ?? ""] = t;
            }
            else Tone.Feel(t, Features.Mood(st.Mood), st.MoodIntensity);
            v.Tone = t.Copy();
        }
        return v;
    }

    /// <summary>The chat is over: its tone isn't needed any more.</summary>
    private static void ForgetTone(string? id)
    {
        if (id == null) return;
        lock (ChatTones) ChatTones.Remove(id);
    }

    /// <summary>
    /// Their line in their own voice, when voices are on. The line waits for its voice so text and speech start
    /// together; after VoiceTimeoutSeconds it's reported as timed out (the caller cancels the line).
    /// </summary>
    /// <summary>VoiceLimit (CF-02): the voice characters counted per hour/day/month, in voice_usage.json.</summary>
    private static TokenBudget? _voiceBudget;
    private static VoiceLedger? _voiceLedger;
    private static float _playSeconds;

    /// <summary>
    /// FB-41: a minute of play (in the world, the game not paused; a chat counts) goes into the usage record, so the calculator
    /// knows how long you play and what an hour of play costs. FB-59 (F-0172): whether the window is in front or not, as long as
    /// the city runs: residents talk (and spend) behind another window too, and those minutes weren't counted (37 a day against
    /// 290 conversations), so an hour of play read many times dearer than it is and the plan held the city far too quiet.
    /// </summary>
    private static void TickPlayTime()
    {
        if (_voiceLedger == null || Time.timeScale <= 0f || GameContext.PlayerCharacter() == null) return;
        if (ConsoleOpen || (!ChatOpen && !InGameplay())) return; // as residents' talk is paused (the console, the game's menus)
        _playSeconds += Math.Min(Time.unscaledDeltaTime, 5f); // a PC waking from sleep isn't minutes of play (review F59-2)
        if (_playSeconds < 60f) return;
        _playSeconds -= 60f;
        _voiceLedger.PlayMinute();
        Guarded("pacing", PaceMinute);
    }

    /// <summary>
    /// FB-51: /usage, a heading per budget and a line per figure (the console colours headings, labels and how close each is
    /// to its limit): your AI, all voices together, each paid voice provider, Voyage, how much you play, and what to set.
    /// </summary>
    internal static List<string> UsageReport()
    {
        var lines = new List<string>();
        void Budget(string heading, TokenBudget? b)
        {
            if (b == null) return;
            var (used, again) = b.Status();
            lines.Add(heading + ":");
            lines.Add("  Used: " + used);
            if (again.Length > 0) lines.Add("  Starts again: " + again);
        }
        Budget("Your AI", _budget);
        if (_budget == null) lines.Add("Token use isn't being counted.");
        Budget("All voices together (VoiceLimit)", _voiceBudget);
        if (Npc.Features.VoiceInput) Budget("Speech to text (SpeechToTextLimit, seconds)", _speechBudget); // FB-75
        if (_voiceLedger != null)
            foreach (var slot in _cfg.VoiceSlots.Where(s => s.Usable && VoiceService.Billed(s)))
            {
                string key = VoiceService.LedgerKey(slot), unit = VoiceCost.Unit(slot.Provider, VoiceService.ModelOf(slot));
                var (_, reset) = _voices.PeriodOf(slot);
                double used = _voices.UsedThisPeriod(slot);
                string word = TokenBudget.Word(TokenBudget.SpanOf(slot.LimitPer) ?? TokenBudget.Span.Month);
                lines.Add($"Voice Provider {slot.Number}, {VoiceService.LedgerName(slot)}:");
                lines.Add(slot.Limit > 0
                    ? $"  Used: {used:N0} of {slot.Limit:N0} {unit} this {word} ({Math.Min(999, used * 100 / slot.Limit):0}%; yours, on this PC)"
                    : $"  Used: {used:N0} {unit} this {word} (no limit of its own; yours, on this PC)");
                lines.Add($"  Starts again: {reset:MMM d}");
                if (_voices.AccountOf(slot) is { } acc && _voices.AccountNow(slot) is { } accNow)
                    lines.Add($"  The ElevenLabs account (everyone using its key): {accNow.used:N0} of {acc.limit:N0} used, resets {acc.reset:MMM d}");
                if (_voiceLedger.Of(key) is { Lines: > 0 } st)
                {
                    lines.Add($"  A line: about {st.LineMean:N0} (busy ones {st.LineHigh:N0}, from {st.Lines:N0} lines)");
                    if (st.HoursSeen > 0) lines.Add($"  An hour of play: about {st.HourMean:N0} (busy hours {st.HourHigh:N0}, from {st.HoursSeen} h)");
                }
            }
        if (_voyage != null && _voyage.TokensUsed > 0)
        {
            lines.Add("Voyage (memory search):");
            lines.Add($"  This session: {_voyage.TokensUsed:N0} tokens");
        }
        if (_voiceLedger != null && _voiceLedger.PlayTime() is var (perDay, days) && days > 0)
            lines.Add($"You play about {perDay:0.#} h a day (the last 14 days; {days} of them played).");
        if (_budget != null && _budget.Limit <= 0) lines.Add("No AI limit: set TokenLimit and TokenLimitPer in the settings menu (Setup Wizard, step 3).");
        if (_voiceBudget != null && _voiceBudget.Limit <= 0) lines.Add("No voice limit: each voice provider can have its own, in the Usage Optimizer (step 1).");
        return lines;
    }

    /// <summary>The period the voice limit was last announced in (said once per period).</summary>
    private static string _voiceLimitSaid = "", _voiceLimitLogged = "";

    /// <summary>A line in their voice. <paramref name="purpose"/> is what it's for, kept in the usage record (FB-41).</summary>
    private static async Task<VoiceOutcome> VoiceForAsync(Func<VoiceSubject> who, string text, CancellationToken ct, string purpose)
    {
        var outcome = new VoiceOutcome();
        try
        {
            if (!_voices.Enabled || string.IsNullOrWhiteSpace(text)) return outcome;
            if (ct.IsCancellationRequested) return outcome; // closed meanwhile: who() isn't asked (it would note a tone for a chat that's over)
            using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // A backstop: the voice service's own deadline (VoiceTimeoutSeconds, see VoiceSettingsNow) comes first, and
            // only it can set a slow provider aside so the next lines come from another one.
            giveUp.CancelAfter(TimeSpan.FromSeconds(VoiceTimeout + 2f));
            try
            {
                var subject = who();
                subject.Purpose = purpose;
                outcome = await _voices.SpeakWithOutcomeAsync(subject, text, giveUp.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { outcome = new VoiceOutcome { TimedOut = true }; }
            if (outcome.OverBudget)
            {
                // VoiceLimit reached (CF-02): said once each hour/day/month, in an open chat and the console; every line
                // after it goes on as text, without a "no voice" note of its own.
                string period = _voiceBudget?.Period ?? "";
                // Said where you'll see it (a chat or the console open); a background line only logs it, so the notice
                // isn't used up while nobody's looking (review of 0.3.1, L3).
                if (outcome.Error != null)
                {
                    if (ChatOpen || _consoleOpen) { if (Interlocked.Exchange(ref _voiceLimitSaid, period) != period) Problem(outcome.Error, "VOICE-BUDGET"); }
                    else if (Interlocked.Exchange(ref _voiceLimitLogged, period) != period) Problem(outcome.Error, "VOICE-BUDGET", show: false);
                }
                return new VoiceOutcome();
            }
            if (outcome.TimedOut) Log.Warn($"A voice wasn't ready after {VoiceTimeout:0} seconds; that line was cancelled.");
            else if (outcome.Error != null) Log.Warn("No voice for that line: " + outcome.Error);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { outcome = new VoiceOutcome { Error = e.Message }; Log.Warn("Voice failed: " + e.Message); }
        return outcome;
    }

    /// <summary>
    /// A voice failed: their line still shows, and this note under it tells you which provider failed and why. It's a
    /// system note in your window only, never part of the conversation they remember.
    /// </summary>
    private static void ShowVoiceError(string error) => Problem($"No voice for that line: {error.TrimEnd('.')}.", "VOICE-FAIL", log: false);

    private static void ShowVoiceTimeout(string? sent)
    {
        Problem($"Their voice wasn't ready after {VoiceTimeout:0} seconds, so that reply was cancelled (VoiceTimeoutSeconds in the config).", "VOICE-SLOW", log: false);
        if (sent == null) return;
        Ui.AddLine(LineKind.Notice, "", "Your message is back in the box: press Enter to send it again.");
        PutBack(sent);
    }

    /// <summary>Dev: a free Windows-voice line from this person, to check sound plays in the world.</summary>
    internal static void DevSpeak(Character c, string text)
    {
        Task.Run(async () =>
        {
            try
            {
                var voices = await WindowsSpeech.VoicesAsync().ConfigureAwait(false);
                var audio = voices.Count > 0 ? await WindowsSpeech.SpeakAsync(voices[0].Id, text, 1.0).ConfigureAwait(false) : null;
                MainThread.Enqueue(() =>
                {
                    float seconds = audio != null ? VoicePlayer.Play(c, audio, _cfg.VoiceVolume.Value / 100f, _cfg.HearingDistance.Value) : 0f;
                    Log.Info($"dev speak: {(audio == null ? "no audio" : $"{audio.Seconds:0.0}s decoded, clip {seconds:0.0}s playing")}");
                });
            }
            catch (Exception e) { Log.Warn("dev speak: " + e.Message); }
        });
    }
}
