using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.UI;
using NivalisAIChat.Voice;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NivalisAIChat;

/// <summary>
/// FB-64 (the author: "voice input ... if we were to bundle, which would be the best open source option for local,
/// otherwise we can use an API"): talking with your microphone in a chat. Hold VoiceInputKey (or PadVoiceButton) while
/// you speak; on letting go, the recording goes to the speech-to-text service (Ai/SpeechToText.cs: OpenAI-style, so
/// Whisper in the cloud or an open-source server on your own PC) and what you said is sent (VoiceInputAutoSend) or typed
/// into the box. The microphone is Windows' own (Voice/MicCapture.cs). Nothing is bundled.
/// </summary>
public static partial class ModRuntime
{
    private const float VoiceMaxSeconds = 30f, VoiceMinSeconds = 0.35f;
    private static MicCapture? _mic;
    private static readonly List<short> _micSamples = new();
    private static float _micSince;
    private static bool _transcribing;
    private static Key _voiceKey = Key.F4;
    private static string _voiceKeyParsed = "", _voiceProblemSaid = "";
    private static CancellationTokenSource? _speechCts;
    private static int _speechAsked;

    /// <summary>The service as the settings are now (read here, on the game's thread, and handed to the request).</summary>
    private static SpeechToText SpeechNow()
    {
        var s = new SpeechSettings
        {
            Url = _cfg.SpeechToTextUrl.Value,
            Key = SpeechToText.KeyFor(_cfg.SpeechToTextUrl.Value, _cfg.SpeechToTextKey.Value, _cfg.ApiUrl.Value, _cfg.ApiKey.Value),
            Model = _cfg.SpeechToTextModel.Value,
            Language = _cfg.SpeechLanguage.Value,
        };
        return new SpeechToText(() => s);
    }

    /// <summary>UpdateChat: the key held starts the microphone, letting go sends what was said.</summary>
    private static void TickVoiceInput()
    {
        if (!Features.VoiceInput) { if (_mic is { Running: true }) StopVoiceInput(); return; }
        bool held = VoiceHeld();
        bool canTalk = (_session != null || _group is { Ready: true }) && _closeAt < 0 && !_resetting;
        if (_mic is { Running: true })
        {
            _micSamples.AddRange(_mic.Read());
            _idle = 0; // you're talking: they wait
            float seconds = Time.unscaledTime - _micSince;
            if (!canTalk) { StopVoiceInput(); return; }
            if (!held || seconds >= VoiceMaxSeconds) FinishRecording();
            return;
        }
        if (held && canTalk && !_transcribing && VoicePressedThisFrame()) StartRecording();
    }

    private static void StartRecording()
    {
        _mic ??= new MicCapture();
        string? why = _mic.Start(_cfg.Microphone.Value);
        if (why != null) { VoiceProblem("The microphone: " + why + "."); return; }
        _micSamples.Clear();
        _micSince = Time.unscaledTime;
        Ui.SetStatus($"Listening… (let go of {VoiceKeyLabel()} when you're done)");
    }

    private static void FinishRecording()
    {
        if (_mic == null) return;
        _micSamples.AddRange(_mic.Read());
        _mic.Stop();
        var samples = _micSamples.ToArray();
        _micSamples.Clear();
        float seconds = samples.Length / (float)MicCapture.Rate;
        if (seconds < VoiceMinSeconds) { Ui.SetStatus(""); return; } // a tap: nothing said
        if (SpeechToText.Peak(samples) < 0.015) { Ui.SetStatus("Nothing was heard (is the right Microphone picked, and not muted?)."); return; }
        // FB-75: the speech-to-text limit, like the AI's and the voices'.
        if (_speechBudget != null && !_speechBudget.Allows()) { VoiceProblem(_speechBudget.LimitMessage()); return; }
        _transcribing = true;
        int asked = ++_speechAsked;
        Ui.SetStatus("Writing down what you said…");
        int serial = _openSerial;
        _speechCts?.Dispose();
        _speechCts = new CancellationTokenSource();
        var ct = _speechCts.Token;
        var speech = SpeechNow();
        string key1 = _cfg.SpeechToTextKey.Value, key2 = _cfg.ApiKey.Value;
        Task.Run(async () =>
        {
            string text = "", error = "";
            try
            {
                text = await speech.TranscribeAsync(samples, MicCapture.Rate, ct).ConfigureAwait(false);
                CountSpeech(seconds);
            }
            catch (OperationCanceledException) { }
            catch (SpeechException e) { error = e.Message; }
            catch (Exception e) { error = "speech to text failed: " + Redact.Text(e.Message, key1, key2); }
            MainThread.Enqueue(() =>
            {
                if (asked == _speechAsked) _transcribing = false; // review F7: only this request's own answer clears it
                if (serial != _openSerial || !ChatOpen || ct.IsCancellationRequested) return;
                Ui.SetStatus("");
                if (error.Length > 0) { VoiceProblem(Cap(error) + "."); return; }
                text = text.Trim();
                if (text.Length == 0) { Ui.SetStatus("Nothing was made out of that. Try again, a little closer to the microphone."); return; }
                if (text.Length > 400) text = text[..400];
                if (_cfg.VoiceInputAutoSend.Value && _closeAt < 0) Send(text);
                else
                {
                    string now = Ui.InputText.Trim();
                    Ui.SetInputText(now.Length == 0 ? text : now + " " + text);
                }
            });
        });
    }

    /// <summary>
    /// FB-75: what was sent to speech to text, in seconds: against SpeechToTextLimit, and in the usage record by service and
    /// model (its key never). A service on this PC is counted too (free, but you can see how much you talk).
    /// </summary>
    private static void CountSpeech(float seconds)
    {
        long s = (long)Math.Ceiling(Math.Max(0, seconds));
        try { _speechBudget?.Add(s); } catch { }
        try { _voiceLedger?.Record(SpeechLedgerKey, "Speech to text (" + Redact.Value(_cfg.SpeechToTextModel.Value ?? "") + ")", "seconds", "voice input", s); } catch { }
    }

    private static string SpeechLedgerKey
    {
        get
        {
            string host = Uri.TryCreate(_cfg.SpeechToTextUrl.Value ?? "", UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "";
            return $"STT|{host}|{Redact.Value(_cfg.SpeechToTextModel.Value ?? "").ToLowerInvariant()}";
        }
    }

    private static TokenBudget? _speechBudget;

    /// <summary>The chat closed or voice input went off: the microphone lets go, a transcription on its way is dropped.</summary>
    private static void StopVoiceInput()
    {
        try { _mic?.Stop(); } catch { }
        _micSamples.Clear();
        try { _speechCts?.Cancel(); } catch { }
        _transcribing = false;
    }

    private static void VoiceProblem(string text)
    {
        Ui.SetStatus("");
        try { Ui.AddLine(LineKind.Error, "", text); } catch { }
        if (_voiceProblemSaid != text) { _voiceProblemSaid = text; Log.Warn("Voice input: " + text); }
    }

    private static bool VoiceHeld()
    {
        ParseVoiceKey();
        bool key = false, pad = false;
        try { var kb = Keyboard.current; key = kb != null && kb[_voiceKey].isPressed; } catch { }
        string button = Pad.Normal(_cfg.PadVoiceButton.Value);
        if (button != "OFF")
            try { var gp = Gamepad.current; pad = gp != null && Pad.HeldNow(gp, button); } catch { }
        return key || pad;
    }

    private static bool VoicePressedThisFrame()
    {
        bool key = false, pad = false;
        try { var kb = Keyboard.current; key = kb != null && kb[_voiceKey].wasPressedThisFrame; } catch { }
        string button = Pad.Normal(_cfg.PadVoiceButton.Value);
        if (button != "OFF")
            try { var gp = Gamepad.current; pad = gp != null && Pad.PressedNow(gp, button); } catch { }
        return key || pad;
    }

    /// <summary>VoiceInputKey as a key: never one that types (a letter, digit or Space would land in the box); F4 then.</summary>
    private static void ParseVoiceKey()
    {
        string k = (_cfg.VoiceInputKey.Value ?? "F4").Trim();
        if (k == _voiceKeyParsed) return;
        _voiceKeyParsed = k;
        bool ok = VoiceKeyFrom(k, out var parsed);
        _voiceKey = ok ? parsed : Key.F4;
        if (!ok && k.Length > 0)
            Log.Warn($"VoiceInputKey '{Redact.Value(k)}' can't be used (a key that types, like a letter, a digit or Space, would land in the chat box), so F4 is used.");
    }

    /// <summary>A key name as a key that doesn't type into the chat box (F1-F12, Ctrl, Alt, Tab, Insert...).</summary>
    internal static bool VoiceKeyFrom(string name, out Key key)
    {
        key = Key.None;
        string k = (name ?? "").Trim();
        if (k.Length == 0 || int.TryParse(k, out _)) return false;
        if (!Enum.TryParse(k, true, out key) || key == Key.None || !Enum.IsDefined(key)) { key = Key.None; return false; }
        bool types = key is >= Key.A and <= Key.Z or >= Key.Digit1 and <= Key.Digit0 or Key.Space or Key.Enter or Key.Backspace
            or Key.Escape or Key.Slash or Key.Backslash or Key.Minus or Key.Equals or Key.Period or Key.Comma or Key.Semicolon
            or Key.Quote or Key.LeftBracket or Key.RightBracket or Key.Backquote or (>= Key.Numpad0 and <= Key.Numpad9)
            or Key.NumpadDivide or Key.NumpadMultiply or Key.NumpadPlus or Key.NumpadMinus or Key.NumpadPeriod or Key.NumpadEquals
            or Key.NumpadEnter or Key.LeftArrow or Key.RightArrow or Key.UpArrow or Key.DownArrow;
        if (types) { key = Key.None; return false; }
        return true;
    }

    private static string VoiceKeyLabel()
    {
        string button = Pad.Normal(_cfg.PadVoiceButton.Value);
        return Pad.Active && button != "OFF" ? Pad.Label(button) : _voiceKey.ToString();
    }
}
