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
using NivalisAIChat.Mind;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using NivalisAIChat.UI;
using NivalisAIChat.Voice;
using UnityEngine;
using UnityEngine.InputSystem;


namespace NivalisAIChat;

/// <summary>One conversation from opening to closing: starting it, replies, silence, handing over food, the info panel.</summary>
public static partial class ModRuntime
{
    // ------------------------------------------------------------------ chat lifecycle

    /// <summary>Who you're talking to right now (null when no chat is open).</summary>
    internal static Character? ChatPartner => ChatOpen ? _npc : null;

    internal static void OpenChat(Character c)
    {
        if (ChatOpen || _resetting || _consoleOpen || _phoneOpen) return; // the phone: no chat while it's out
        if (_worldOpen) CloseWorldLine();
        EndWorldMoment("you started a chat", quiet: true); // FB-76: a world chat moment hands over to the window
        var cam = GameContext.PlayerCamera();
        if (cam == null) return;
        string id = GameContext.NpcId(c);
        var openedUtc = DateTime.UtcNow; // this meeting's own lines are saved after this: not shown again as history
        // One look at their record serves the whole window, the under-18 check included: a glance, their history skipped
        // (a regular's file is mostly that; it's read on a pool thread below). BeginAsync reads them properly, or says it
        // can't (PERF-3/4; one read, not two: 6a review).
        var known = ActiveBrain.Store.Glance(id);
        // Nobody under 18 is chatted with: they brush you off over their head and the window never opens.
        var (isMinor, minorName, minorBody) = MinorInfo(c, id, known, read: true);
        if (isMinor) { MinorRefuses(c, id, minorName, minorBody); return; }
        // On the phone, or in a conversation that doesn't let you in: they tell you they're busy and carry on.
        if (BusyWith(c, id, known, out string interruptedWith) is { } busy) { BrushOff(c, id, busy, known); return; }
        if (TryOpenGroupChat(c, cam)) return; // FB-66: in a residents' conversation with others: you join all of them
        BreakUpChatterWith(c); // talking to someone in a residents' conversation breaks it up (they let you in)
        var visual = GameContext.Visual(c);
        var snap = GameContext.Capture(c);
        snap.PlayerGender = _cfg.PlayerGender.Value;
        snap.Interrupted = interruptedWith; // they broke off a conversation with these people to talk to you
        _openActivity = snap.NpcActivity; // captured before they are switched to idle
        GatherWorld(snap, id, known?.Persona);
        FillWouldDo(snap, c, known); // FB-62/63: what they'd do if asked, decided before they answer
        FillOtherMods(snap, known); // FB-67
        FillInner(snap, c, id, known); // the inner-life round: how they still feel, what's on their mind, what they saw
        FillPhone(snap, id); // the phone: their number, whether you have it, your last texts
        RendezvousHandsOver(c); // the one who came to meet you: the chat takes them from the meet-up
        CompanyHandsOver(c); // FB-72: one who came to keep you company
        Ui.HideBark();
        _npc = c;
        _visual = visual;
        _busy = true;
        _diagAt = Time.unscaledTime + 2f;
        _closeAt = -1;
        int serial = ++_openSerial;
        int generation = _resetGeneration;
        PauseFollowerFor(c); // FB-62: a follower stops walking while you talk
        PauseHomeFor(c); // FB-63: your partner at home (seated: they stay seated)
        NpcHold.Engage(c, cam.transform, _cfg.AnimateNpc.Value);
        Locks.Engage(_cfg.PauseTimeWhileChatting.Value);

        if (known != null)
        {
            string who = known.State.PlayerName.Length > 0 ? known.State.PlayerName : "the stranger you've talked with before";
            if (Greetings.TakeApproach(id, who) is { } ap) { snap.Approach = ap.approach; snap.ApproachMood = ap.mood; }
        }
        bool revealed = known != null && known.State.NameRevealed;
        // FB-57: a name the game shows over their head is the one on the window, even for someone met before under another
        string shownName = visual.NameShown && !visual.Named ? visual.GameName.Trim() : "";
        string title = shownName.Length > 0 ? shownName : known != null && revealed ? DisplayName(known) : visual.KnownByName() && visual.GameName.Trim().Length > 0 ? visual.GameName : "Stranger";
        Ui.Open(title, known != null ? GenderLabel(known) : Population.PerceivedLabel(visual.IsAndroid ? "android" : visual.Body));
        if (!_windowTipSaid && _cfg.ChatWindowLayout.Value.Length == 0) { _windowTipSaid = true; Ui.AddLine(LineKind.Notice, "", "Drag the top of this window to move it, its corner to resize it."); }
        Ui.SetNpcName(shownName.Length > 0 ? Prompts.FirstName(shownName) : known != null ? SpeakerName(known) : visual.KnownByName() && visual.GameName.Trim().Length > 0 ? Prompts.FirstName(visual.GameName) : "Stranger");
        // The bar at once (FB-24): where they stood last time, or neutral for someone new; their own value follows quietly.
        Ui.SetRelationship(_cfg.ShowRelationshipBar.Value && Features.Relationships ? known?.State.Relationship ?? Relationships.Neutral : null);
        Ui.SetTogether(Features.Romance && known != null && known.State.Romance.Length > 0); // FB-63: the heart
        _queued = "";
        _idle = 0;
        _silentStage = 0;
        _lastInputText = "";
        if (known == null) Ui.SetThinking("They turn to look at you");
        else Ui.SetThinking($"{(revealed || title != "Stranger" ? Prompts.FirstName(title) : "Stranger")} is thinking");
        Ui.MaxLines = Math.Clamp(_cfg.HistoryShown.Value, 0, 2000) + 60;
        if (known != null || (visual.Named && Features.StoryMemory && _story?.LineCount(id) > 0)) ShowHistoryLater(id, serial, snap.GameDay, openedUtc);
        if (visual.Named) StorySheetSoon(id, visual.GameName); // FB-50: their way of talking, once enough of their story is kept
        // You open with your line ("Hey,") and they answer it; someone who just waved you over speaks first instead.
        string opener = snap.Approach.Length > 0 ? "" : NpcBrain.OpeningLineFor(BrainSettingsNow());
        _openerLine = opener.Length > 0 ? Ui.AddLine(LineKind.Player, "You", opener) : null;
        _unsent.Clear();
        Ui.ScrollToBottom(true);
        RefreshInfo(known ?? new NpcRecord { Visual = visual, Persona = new Persona { Name = visual.GameName } }, snap);

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var brain = ActiveBrain;
        // LookAtNewPeople: someone the AI has never seen gets one picture, taken a moment in (once they've turned to you): someone new
        // is written from it, someone met before has how they look brought up to date.
        TaskCompletionSource<byte[]?>? picture = null;
        if (_cfg.LookAtNewPeople.Value && !visual.Minor && (known == null || !known.Visual.LookedAt))
        {
            if (!brain.PicturesUnsupported)
            {
                picture = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _picture = picture;
                _pictureAt = Time.unscaledTime + PictureDelaySeconds;
            }
            else if (!_pictureSkipNoted)
            {
                _pictureSkipNoted = true; // said once per game session; the console's /errors keeps it
                Problem(($"LookAtNewPeople is on, but your AI model ({Redact.Value(_cfg.Model.Value)}) can't look at pictures, so new people are written " +
                                                       "from the game's description instead. Turn LookAtNewPeople off, or use a model that can see images."), "PIC-SKIPPED");
            }
        }
        Log.Debug($"Chat open: {id} ({visual.Body}, {visual.Demographic}, {visual.Kind}, model '{visual.Model}') at {snap.Location} day {snap.GameDay} {snap.Clock}");
        // Their memories are indexed first (MemorySearch = Voyage), ready for what you bring up. With a look coming, only once
        // they've passed it: the picture can show they're under 18, and then nothing of theirs goes to Voyage.
        if (picture == null) _memory?.QueueFirst(id);
        _inflight = Task.Run(async () =>
        {
            (ChatSession?, NpcReply?, string?) result;
            byte[]? pic = null;
            if (picture != null)
            {
                try { pic = await picture.Task.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            // Each step has its own waiting limit inside (GiveUpAfterSeconds): a slow first line still lets you talk.
            try { result = await brain.BeginAsync(id, visual, snap, ct, pic).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (NpcFileUnreadableException e) // their file couldn't be read right now: said as it is, and nothing was written over it
            {
                Log.Warn($"Starting a conversation with {id}: {e.Message}");
                result = (null, null, e.Message);
            }
            catch (Exception e)
            {
                Log.Error($"Starting a conversation with {id} failed: {e}");
                result = (null, null, "Something went wrong starting this conversation (details in BepInEx\\LogOutput.log).");
            }
            if (picture != null && result.Item1 != null) _memory?.QueueFirst(id); // they passed the look
            VoiceOutcome? voice = null;
            try
            {
                if (result.Item1 != null && result.Item2 is { Ok: true } first)
                {
                    voice = await VoiceForAsync(() => Toned(result.Item1.Record, start: true), first.Spoken, ct, "you").ConfigureAwait(false);
                    if (voice.TimedOut) brain.Retract(result.Item1, first, withPlayerLine: result.Item1.OpenerLine != null); // the "Hey," goes too
                }
            }
            catch (OperationCanceledException) { } // closed meanwhile: OnBegun still ends the meeting
            catch (Exception e) { Log.Warn($"Their first line with {id}: {e.Message}"); }
            finally
            {
                // Always handed on: a meeting that began must be ended (or it would keep their record to itself).
                MainThread.Enqueue(() =>
                {
                    // A reset while it was starting: nothing of this meeting is kept (the file was just cleared).
                    if (generation != _resetGeneration && result.Item1 != null)
                    {
                        result.Item1.Ended = true;
                        result.Item1.Owner?.Release(result.Item1, ReleaseMode.Discard);
                    }
                    OnBegun(serial, result, voice);
                });
            }
        });
    }

    /// <summary>
    /// Everything you've said to each other before (up to HistoryShown lines, with a divider per day: <see cref="HistoryRows"/>).
    /// Read on a pool thread, since a regular's file is mostly this (kept for good, Q2), and shown above the first line a
    /// batch at a time, the newest first, so the window never waits on it (PERF-4). Dropped if the chat closes meanwhile.
    /// </summary>
    private static void ShowHistoryLater(string id, int serial, int today, DateTime openedUtc)
    {
        int max = Math.Clamp(_cfg.HistoryShown.Value, 0, 2000);
        if (max == 0) return;
        var store = ActiveBrain.Store;
        Task.Run(() =>
        {
            List<(HistoryRows.Kind kind, string text)> rows;
            // The pool can wait. Lines this meeting saved since the window opened aren't history (6a review: shown twice).
            // FB-50: their story conversations go in among the chats, by game day.
            var scenes = Features.StoryMemory && _story != null ? _story.Scenes(id) : new List<StoryBook.Scene>();
            try
            {
                var history = store.Peek(id, tries: 4)?.History ?? new List<ChatLine>();
                rows = history.Count > 0 || scenes.Count > 0 ? HistoryRows.For(StoryBook.Timeline(history, scenes), max, today, before: openedUtc) : new();
            }
            catch (Exception e) { Log.Debug("history: " + e.Message); return; }
            if (rows.Count == 0) return;
            var lines = rows.Select(r => (r.kind switch
            {
                HistoryRows.Kind.Divider => LineKind.Divider,
                HistoryRows.Kind.Player => LineKind.PastPlayer,
                HistoryRows.Kind.StoryThem => LineKind.StoryNpc,
                HistoryRows.Kind.StoryYou => LineKind.StoryPlayer,
                HistoryRows.Kind.StoryOther => LineKind.StoryOther,
                _ => LineKind.PastNpc,
            }, r.text)).ToList();
            MainThread.Enqueue(() => AddHistoryBatch(lines, lines.Count, serial));
        });
    }

    private const int HistoryBatch = 25;

    /// <summary>The newest batch of the history not shown yet, then the next one a frame later; only while the same chat is open.</summary>
    private static void AddHistoryBatch(List<(LineKind kind, string text)> lines, int end, int serial)
    {
        if (serial != _openSerial || !ChatOpen) return;
        int start = Math.Max(0, end - HistoryBatch);
        bool room = Ui.InsertAtTop(lines.GetRange(start, end - start));
        // Next frame (a delay of nothing would run it in this frame's pass; the clock doesn't move within a frame).
        if (room && start > 0) Later(0.001f, () => AddHistoryBatch(lines, start, serial));
    }

    private static void OnBegun(int serial, (ChatSession? session, NpcReply? opening, string? error) r, VoiceOutcome? voice)
    {
        if (serial != _openSerial || _npc == null) { if (r.session != null) (r.session.Owner ?? ActiveBrain).End(r.session); return; }
        if (r.session != null) _session = r.session; // first: from now on closing the chat ends this meeting, whatever fails below
        _busy = false;
        Ui.SetStatus("");
        if (r.session == null && r.error == NpcBrain.MinorError)
        {
            // Only found out while creating them (their record says they're under 18): no chat after all.
            var kid = _npc;
            string kidId = GameContext.NpcId(kid);
            var v = _visual ?? new NpcVisual();
            CloseChat(null);
            MinorCache[kidId] = (true, v.Named ? v.GameName : "", v.Body);
            MinorRefuses(kid, kidId, v.Named ? v.GameName : "", v.Body);
            return;
        }
        if (r.session == null)
        {
            TakeBackOpener();
            Problem("Couldn't start the conversation: " + (r.error ?? "something went wrong."), CodeFor(r.error ?? "") == "MOD-ERROR" ? "CHAT-START" : null);
            Ui.AddLine(LineKind.Notice, "", "Press Esc to step away.");
            _queued = "";
            return;
        }
        _session = r.session;
        _barAtOpen = _session.Record.State.Relationship; // the inner-life round: what this meeting does to them, at the end
        try { FoundOutAboutYou(_session.Record); } catch (Exception e) { Log.Debug("found out: " + e.Message); } // FB-63
        try { PluginsChatOpened(_session.Record); } catch (Exception e) { Log.Debug("plugins: " + e.Message); } // FB-67
        RefreshHeader(announce: false); // their own value takes over from the one shown at open, quietly
        NpcHold.SetMood(Features.Mood(_session.Record.State.Mood));
        foreach (var notice in r.session.Notices) Problem(notice);
        if (r.opening != null)
        {
            if (r.opening.Ok && voice is { TimedOut: true }) { TakeBackOpener(); ShowVoiceTimeout(null); }
            else if (r.opening.Ok) ShowNpcLine(r.opening, voice);
            else
            {
                TakeBackOpener();
                Problem("They didn't answer your opening line: " + (r.opening.Error ?? "no reply."));
                Ui.AddLine(LineKind.Notice, "", "You can still talk to them: type something and press Enter.");
            }
        }
    }

    /// <summary>
    /// Time passing as you talk (MinutesPerMessage, off by default): each line they say moves the game's clock on, so a date
    /// takes its share of the evening. The first time it's used, the log says whether the game's clock took it.
    /// </summary>
    private static void TimePasses()
    {
        int m = Math.Clamp(_cfg.MinutesPerMessage.Value, 0, 30);
        if (m == 0) return;
        int before = GameContext.Hour() * 60 + GameContext.Minute();
        bool ok = GameContext.AddGameMinutes(m);
        if (!_timeSaid)
        {
            _timeSaid = true;
            int after = GameContext.Hour() * 60 + GameContext.Minute();
            Log.Info(ok ? $"Time passes as you talk: {m} min a line (the clock went from {before / 60:00}:{before % 60:00} to {after / 60:00}:{after % 60:00})."
                        : "Time passes as you talk: the game's clock couldn't be moved here.");
        }
    }

    private static bool _timeSaid, _windowTipSaid;

    private static void ShowNpcLine(NpcReply reply, VoiceOutcome? voice = null)
    {
        var rec = _session!.Record;
        RefreshHeader();
        // With a voice, the words appear at the speed they're spoken: starting with the voice, finishing with it.
        float seconds = voice?.Audio != null && _npc != null ? VoicePlayer.Play(_npc, voice.Audio, LineVolume(rec.Id), _cfg.HearingDistance.Value) : 0f;
        Ui.SmoothFade = _cfg.SmoothTextFade.Value;
        Ui.Speed = _cfg.TextSpeed.Value;
        Ui.AddLine(LineKind.Npc, SpeakerName(rec), reply.Spoken, typewriter: true, revealSeconds: seconds);
        if (Features.Roleplay && reply.Action.Length > 0) Ui.AddAfterReveal(LineKind.Action, reply.Action); // what they do as they say it, once it's said
        TimePasses(); // MinutesPerMessage: the clock moves on with the talk
        if (_transcripts != null)
        {
            ToneState? tone = null;
            lock (ChatTones) if (ChatTones.TryGetValue(rec.Id ?? "", out var t)) tone = t.Copy();
            RecordLine("chat", $"chat:{rec.Id}:{_openSerial}", _npc, rec.Id ?? "", rec, reply.Spoken, "you",
                $"{rec.State.MoodIntensity} {rec.State.Mood}".Trim(), tone, reply.Gesture, voice, seconds, where: _session.Snapshot, relationship: rec.State.Relationship);
        }
        NpcHold.SetMood(Features.Mood(rec.State.Mood)); // first: the line's own gesture (below) is what shows while they say it (review M3)
        NpcHold.Speak(reply.Spoken, seconds, reply.Gesture);
        if (voice?.Error != null) ShowVoiceError(voice.Error);
        NoteKnownName(rec);
        try { FeltInChat(rec); } catch (Exception e) { Log.Debug("inner life: " + e.Message); } // the inner-life round: how they felt saying it stays a while
        try { NoteReminder(rec, reply.Extras); } catch (Exception e) { Log.Debug("reminder: " + e.Message); } // FB-65
        try { NoteNumber(rec, reply); } catch (Exception e) { Log.Debug("phone number: " + e.Message); } // the phone: they gave you their number
        try { if (_npc != null) NoteFollow(_npc, rec, reply.Extras); } catch (Exception e) { Log.Debug("follow: " + e.Message); } // FB-62
        try { if (_npc != null) NoteFishing(_npc, rec, reply.Extras); } catch (Exception e) { Log.Debug("fishing: " + e.Message); } // FB-73
        try { if (_npc != null) NoteRomance(_npc, rec, reply.Extras); } catch (Exception e) { Log.Debug("romance: " + e.Message); } // FB-63
        try { PluginsLineShown(rec, reply.Spoken, reply.Extras); } catch (Exception e) { Log.Debug("plugins: " + e.Message); } // FB-67
        if (reply.Leave)
        {
            try { if (_npc != null) LeavingYou(_npc, rec); } catch (Exception e) { Log.Debug("leaving: " + e.Message); } // FB-62/63
            Ui.SetInputEnabled(false);
            string where = Patience.WhatTheyNeedToDo(_session.Snapshot) switch
            {
                "get back to work" => "goes back to work",
                "get home" => "heads home",
                "get back to your food" => "goes back to their food",
                Patience.OutTogether => "is happy just being with you for a while", // FB-85
                _ => "walks off",
            };
            Ui.SetStatus($"{SpeakerName(rec)} {where}.");
            _closeAt = Time.unscaledTime + Mathf.Max(Mathf.Clamp(reply.Spoken.Length / 50f, 1f, 6f) + 2.5f, seconds + 1.5f);
        }
    }

    /// <summary>The name they actually gave you (nickname or alias included); "Stranger" until then.</summary>
    private static string DisplayName(NpcRecord rec) =>
        rec.State.KnownAs.Length > 0 ? rec.State.KnownAs : rec.Visual.Named || rec.State.NameRevealed ? rec.Persona.Name : "Stranger";

    private static string SpeakerName(NpcRecord rec) => DisplayName(rec) == "Stranger" ? "Stranger" : Prompts.FirstName(DisplayName(rec));

    /// <summary>What a passer-by sees (Man/Woman/Android) until they tell you more — then Trans Woman, Non-binary...</summary>
    private static string GenderLabel(NpcRecord rec)
    {
        string body = rec.Visual.IsAndroid ? "android" : rec.Visual.Body;
        if (!rec.State.IdentityShared) return Population.PerceivedLabel(body);
        string label = Population.SharedLabel(rec.Persona.GenderIdentity, body);
        bool showPronouns = rec.Persona.GenderIdentity is "non-binary" or "genderfluid" or "agender" || rec.Visual.IsAndroid;
        return showPronouns && rec.Persona.Pronouns.Length > 0 ? $"{label} ({rec.Persona.Pronouns})" : label;
    }

    /// <param name="announce">A reply or food moved the bar: the change floats up over it (FB-15).</param>
    private static void RefreshHeader(bool announce = true)
    {
        if (_session == null) return;
        var rec = _session.Record;
        string title = DisplayName(rec);
        string cue = _cfg.ShowMoodHint.Value && Features.Moods ? Moods.Cue(rec.State.Mood) : "";
        string who = GenderLabel(rec);
        string sub = string.Join("  ·  ", new[] { who, cue }.Where(s => s.Length > 0));
        Ui.SetHeader(title, sub);
        Ui.SetRelationship(_cfg.ShowRelationshipBar.Value && Features.Relationships ? rec.State.Relationship : null, announce); // the relationship bar, after each reply too
        Ui.SetTogether(Features.Romance && rec.State.Romance.Length > 0); // FB-63
        Ui.SetNpcName(SpeakerName(rec));
        RefreshInfo(rec, _session.Snapshot);
    }

    /// <summary>The "what you know" panel: only what they've told you, plus what the game itself shows you.</summary>
    private static void RefreshInfo(NpcRecord rec, GameSnapshot snap)
    {
        try { Ui.SetInfo(InfoSheet.Build(rec, snap, DisplayName(rec), GenderLabel(rec), _cfg.ShowMoodHint.Value && Features.Moods ? Moods.Cue(rec.State.Mood) : "")); }
        catch (Exception e) { Log.Debug("info panel: " + e.Message); }
    }

    private static GameSnapshot CaptureNow()
    {
        var snap = GameContext.Capture(_npc);
        if (_openActivity.Length > 0) snap.NpcActivity = _openActivity;
        snap.PlayerGender = _cfg.PlayerGender.Value;
        ApplyWorld(snap);
        FillWouldDo(snap, _npc, _session?.Record); // FB-62/63
        FillOtherMods(snap, _session?.Record); // FB-67
        FillInner(snap, _npc, _session?.Record.Id ?? "", _session?.Record); // the inner-life round
        FillPhone(snap, _session?.Record.Id ?? "");
        return snap;
    }

    /// <summary>
    /// Silence: counted only while the game has focus, nobody is mid-reply and you aren't typing. First they react,
    /// then they leave. Typing or sending resets it.
    /// </summary>
    private static void TickSilence()
    {
        string text = Ui.InputText;
        bool typing = text != _lastInputText;
        _lastInputText = text;
        if (typing || _busy || Ui.Revealing || _queued.Length > 0 || _session == null || _closeAt > 0)
        {
            _idle = 0;
            if (typing && text.Length > 0) _silentStage = 0;
            return;
        }
        if (!Application.isFocused) return;
        _idle += Time.unscaledDeltaTime;
        int check = _cfg.IdleCheckSeconds.Value;
        if (check <= 0) return;
        if (_silentStage == 0 && _idle >= check) { _silentStage = 1; _idle = 0; SendSilence(1, check); }
        else if (_silentStage == 1 && _idle >= Math.Max(5, _cfg.IdleLeaveSeconds.Value)) { _silentStage = 2; _idle = 0; SendSilence(2, check); }
    }

    private static void SendSilence(int stage, int seconds)
    {
        if (_session == null || _busy) return;
        var s = _session;
        _busy = true;
        Ui.SetStatus("");
        var snap = CaptureNow();
        var ct = _cts?.Token ?? CancellationToken.None;
        var brain = ActiveBrain;
        int serial = _openSerial;
        Log.Debug($"Silence stage {stage} with {s.Record.Id}");
        _inflight = Task.Run(async () =>
        {
            NpcReply reply;
            try { reply = await brain.SilenceAsync(s, stage, seconds, snap, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { reply = new NpcReply { Ok = false, Error = e.Message }; }
            var voice = reply.Ok ? await VoiceForAsync(() => Toned(s.Record, start: false), reply.Spoken, ct, "you").ConfigureAwait(false) : null;
            if (voice is { TimedOut: true }) brain.Retract(s, reply, withPlayerLine: false);
            MainThread.Enqueue(() =>
            {
                if (serial != _openSerial || _session == null) return;
                _busy = false;
                if (!reply.Ok) { Problem("Their reaction to your silence failed: " + reply.Error); if (stage >= 2) CloseChat("silent"); return; }
                if (voice is { TimedOut: true }) { ShowVoiceTimeout(null); if (stage >= 2) _closeAt = Time.unscaledTime + 3f; return; }
                ShowNpcLine(reply, voice);
                if (stage >= 2 && !reply.Leave) _closeAt = Time.unscaledTime + 3f;
            });
        });
    }

    private static void UpdateChat()
    {
        Locks.KeepCursor();
        TickSilence();
        if (_diagAt > 0 && Time.unscaledTime >= _diagAt)
        {
            _diagAt = -1;
            if (_cfg.DevCommands.Value) Log.Info("Hold check (2s into chat): " + NpcHold.Diagnostics().Replace(Environment.NewLine, " | "));
        }
        var kb = Keyboard.current;
        if (_npc == null || _npc.WasCollected || !_npc.isActiveAndEnabled)
        {
            CloseChat("They're gone.");
            return;
        }
        if (_closeAt > 0 && Time.unscaledTime >= _closeAt && !Ui.Revealing) { CloseChat(null); return; }
        if (kb != null && kb.escapeKey.wasPressedThisFrame) { CloseChat(null); return; }
        if (Ui.TakeBack()) { CloseChat(null); return; } // B on the controller (CF-07)
        if (Ui.TakeSettingsClick()) { OpenSettings(line => Ui.AddLine(LineKind.Notice, "", line)); return; } // the chat waits, and comes back as it was
        // Their picture once they've turned to you (out of any pose first): a back or a side says little, and misleads.
        if (_picture != null && Time.unscaledTime >= _pictureAt && (NpcHold.ReadyForPicture(_npc) || Time.unscaledTime >= _pictureAt + PictureWaitSeconds)) TakePicture();
        TickGive();
        try { TickVoiceInput(); } catch (Exception e) { Log.Debug("voice input: " + e.Message); } // FB-64: hold the key, speak
        if (_group != null)
        {
            // FB-66: a group conversation: its own lines and input (no one-on-one session).
            try { TickGroupChat(); } catch (Exception e) { Log.Debug("group chat: " + e.Message); }
            if (_closeAt < 0 && Ui.SubmitPressed()) Send(Ui.TakeInput());
            return;
        }
        // Type-ahead: lines sent while they're still thinking are queued and go out together when they finish.
        if (_closeAt < 0 && Ui.SubmitPressed()) Send(Ui.TakeInput());
        if (!_busy && _session != null && _closeAt < 0 && _queued.Length > 0)
        {
            string q = _queued;
            _queued = "";
            SendNow(q);
        }
    }

    // ------------------------------------------------------------------ handing over food

    private static bool _saidHowToGive;

    /// <summary>Shows the Give button while you're carrying a plated dish, and hands it over when clicked.</summary>
    private static int _giveSaidFor = -1;

    private static void TickGive()
    {
        // CF-22: once a chat, what you're holding and whether GIVE can hand it over (the log says why it doesn't show).
        if (_session != null && _giveSaidFor != _openSerial)
        {
            _giveSaidFor = _openSerial;
            string held = GameContext.HeldDescription();
            if (held.Length > 0)
                Log.Info(!_cfg.Favours.Value
                    ? $"Give: you're holding {held}, but FoodOrders is off, so there's no GIVE button."
                    : $"Give: you're holding {held}{(GameContext.HeldMeal() != null ? ": GIVE shows in the chat." : ": GIVE only shows for a plated dish.")}");
        }
        if (!_cfg.Favours.Value || _session == null || _closeAt > 0) { Ui.SetGive(null, ""); return; }
        if (Ui.TakeGiveClick() && !_busy) { Give(); return; }
        if (Time.unscaledTime < _nextGivePoll) return;
        _nextGivePoll = Time.unscaledTime + 0.3f;
        var meal = GameContext.HeldMeal();
        if (meal == null || _busy) { Ui.SetGive(null, ""); return; }
        var offer = OfferFor(_session.Record, _session.Snapshot, meal);
        Ui.SetGive(meal.Dish, offer.Label);
    }

    private static FoodOffer OfferFor(NpcRecord rec, GameSnapshot snap, GameContext.HeldMealInfo meal) =>
        FoodOffer.For(rec, snap, meal.Dish, meal.BasePrice,
            _cfg.FriendDiscountPercent.Value, Amount(_cfg.FriendDiscountAt.Value, 0, 100, 70), Math.Max(0, _cfg.DeliveryFee.Value));

    internal static void Give()
    {
        if (_session == null || _busy || _closeAt > 0) return;
        var meal = GameContext.HeldMeal();
        if (meal == null) return;
        var s = _session;
        var snap = CaptureNow();
        var offer = OfferFor(s.Record, snap, meal);
        if (!GameContext.ConsumeHeldMeal(meal)) { Ui.AddLine(LineKind.Error, "", "You couldn't hand it over."); return; }
        Ui.SetGive(null, "");
        bool paid = offer.Paid && offer.Price > 0 && GameContext.PayPlayer(offer.Price);
        // They paid (a 100% friend discount still counts as their order); what your wallet got is said as it is (#14a).
        bool walletFailed = offer.Paid && offer.Price > 0 && !paid;
        if (walletFailed) Log.Warn($"Payment of {offer.Price} Lims could not be added to your wallet.");
        string dish = meal.Dish;
        Ui.AddLine(LineKind.Notice, "", !offer.Paid ? $"You hand over the {dish} — on the house."
            : walletFailed ? $"You hand over the {dish}. They paid, but the {offer.Price} Lims couldn't be added to your wallet."
            : $"You hand over the {dish}. +{offer.Price} Lims{(offer.Note.Length > 0 ? $" ({offer.Note})" : "")}.");
        Log.Info($"Gave {dish} to {s.Record.Persona.Name}: {(offer.Paid ? offer.Price + " Lims" + (walletFailed ? " (not added to your wallet)" : "") : "gift")}{(offer.Note.Length > 0 ? " (" + offer.Note + ")" : "")}.");
        try
        {
            // The inner-life round: it stays with them a while, and anyone near sees you hand it over.
            string you = s.Record.State.PlayerName.Length > 0 ? s.Record.State.PlayerName : "the newcomer";
            if (offer.Paid) Stays(s.Record.Id, s.Record.Persona.Name, s.Record.Persona, "food:" + dish, $"got the {dish} you'd asked {you} for", 0.15f, 0.05f, 6, "content");
            else Stays(s.Record.Id, s.Record.Persona.Name, s.Record.Persona, "gift:" + dish, $"touched that {you} gave you {dish}", 0.3f, 0.1f, 10, "cheerful");
            if (_npc != null) Witness(Happenings.PlayerKind, _npc.transform.position, Happenings.Player, "", s.Record.Id, s.Record.Persona.Name, snap.Location, offer.Paid ? 0.4f : 0.6f,
                detail: offer.Paid ? "" : dish);
        }
        catch (Exception e) { Log.Debug("inner life, food: " + e.Message); }
        _busy = true;
        _idle = 0;
        Ui.SetThinking(SpeakerName(s.Record) == "Stranger" ? "They take it" : $"{SpeakerName(s.Record)} takes it");
        var ct = _cts?.Token ?? CancellationToken.None;
        var brain = ActiveBrain;
        int serial = _openSerial;
        _inflight = Task.Run(async () =>
        {
            NpcReply reply;
            try { reply = await brain.HandOverAsync(s, dish, offer.Paid ? offer.Price : 0, offer.Note, offer.Delivered, snap, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { reply = new NpcReply { Ok = false, Error = e.Message }; }
            var voice = reply.Ok ? await VoiceForAsync(() => Toned(s.Record, start: false), reply.Spoken, ct, "you").ConfigureAwait(false) : null;
            if (voice is { TimedOut: true }) brain.Retract(s, reply, withPlayerLine: false);
            MainThread.Enqueue(() =>
            {
                if (serial != _openSerial || _session == null) return;
                _busy = false;
                Ui.SetStatus("");
                RefreshHeader(); // the food moved the relationship bar, whatever became of their words (review of 919404e)
                if (!reply.Ok) { Problem("Their reaction to the food failed: " + reply.Error); Ui.AddLine(LineKind.Notice, "", "They take it without a word."); return; }
                if (voice is { TimedOut: true }) { ShowVoiceTimeout(null); Ui.AddLine(LineKind.Notice, "", "They take it."); return; }
                ShowNpcLine(reply, voice);
            });
        });
    }

    internal static void Send(string text)
    {
        if (!ChatOpen || string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        if (text.StartsWith("/")) { RunCommand(text); return; }
        if (_group != null) { GroupSend(text); return; } // FB-66
        if (Ui.AddLine(LineKind.Player, "You", text) is { } shown) _unsent.Add(shown);
        try { if (_session != null) Api.PonApi.RaisePlayerSaid(InfoOf(_session.Record), text); } catch { } // FB-67
        try { ConfirmRomanceFrom(text); } catch (Exception e) { Log.Debug("romance confirm: " + e.Message); } // FB-63: your yes to theirs
        // CF-20: giving them something in words, with no dish in your hands: how GIVE works, once a game session.
        if (!_saidHowToGive && _cfg.Favours.Value && FoodOffer.MentionsGiving(text) && GameContext.HeldMeal() == null)
        {
            _saidHowToGive = true;
            Ui.AddLine(LineKind.Notice, "", "To hand someone food, carry a plated dish over to them: a GIVE button shows in the chat while you hold it.");
        }
        _queued = _queued.Length > 0 ? _queued + " " + text : text;
        _idle = 0;
        _silentStage = 0;
    }

    private static void SendNow(string text)
    {
        if (_session == null || _busy) return;
        var s = _session;
        Ui.SetThinking($"{SpeakerName(s.Record)} is thinking");
        _busy = true;
        var snap = CaptureNow();
        var ct = _cts?.Token ?? CancellationToken.None;
        var brain = ActiveBrain;
        int serial = _openSerial;
        var shown = _unsent.ToList(); // the lines on screen for these words, taken back out if they don't go through
        _unsent.Clear();
        _inflight = Task.Run(async () =>
        {
            NpcReply reply;
            try { reply = await brain.SayAsync(s, text, snap, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { reply = new NpcReply { Ok = false, Error = e.Message }; }
            // FB-67: plugins' line filters (on this worker thread), before the line is voiced and shown.
            if (reply.Ok && reply.Spoken.Length > 0) try { reply.Spoken = Api.PonApi.FilterLine(InfoOf(s.Record), reply.Spoken); } catch { }
            var voice = reply.Ok ? await VoiceForAsync(() => Toned(s.Record, start: false), reply.Spoken, ct, "you").ConfigureAwait(false) : null;
            if (voice is { TimedOut: true }) brain.Retract(s, reply, withPlayerLine: true);
            MainThread.Enqueue(() => OnReply(serial, text, reply, voice, shown));
        });
    }

    private static void OnReply(int serial, string sent, NpcReply reply, VoiceOutcome? voice, List<GameObject> shown)
    {
        if (serial != _openSerial || _session == null) return;
        _busy = false;
        Ui.SetStatus("");
        if (!reply.Ok || voice is { TimedOut: true })
        {
            // It didn't go through: your message comes back out of the chat and into the box (they never heard it).
            foreach (var line in shown) Ui.RemoveLine(line);
            if (!reply.Ok)
            {
                Problem(string.IsNullOrEmpty(reply.Error) ? "That message couldn't be sent." : reply.Error!);
                Ui.AddLine(LineKind.Notice, "", "Your message is back in the box: press Enter to send it again.");
                PutBack(sent);
            }
            else ShowVoiceTimeout(sent);
            return;
        }
        // Your words in the transcript once they went through (audit B7: a resend after a failed reply isn't recorded twice).
        if (_transcripts != null)
            RecordLine("chat", $"chat:{_session.Record.Id}:{_openSerial}", null, "player", null, sent, _session.Record.Persona.Name, by: "you", where: _session.Snapshot);
        ShowNpcLine(reply, voice);
    }

    private static readonly List<GameObject> _unsent = new();
    private static GameObject? _openerLine;

    /// <summary>Your automatic opening line ("Hey,") didn't get an answer: it's taken back out, as it is from their memory.</summary>
    private static void TakeBackOpener()
    {
        Ui.RemoveLine(_openerLine);
        _openerLine = null;
    }

    /// <summary>Words that didn't go through, back in the box (in front of anything typed since).</summary>
    private static void PutBack(string sent)
    {
        string now = Ui.InputText;
        Ui.SetInputText(now.Trim().Length == 0 ? sent : sent + " " + now);
    }

    // ------------------------------------------------------------------ pictures (LookAtNewPeople)

    /// <summary>
    /// How long a conversation waits for one answer (GiveUpAfterSeconds): 0 = automatically one full request's wait
    /// (TimeoutSeconds) and 15 seconds more; below 0 = no limit.
    /// </summary>
    internal static int AnswerLimitSeconds()
    {
        int set = _cfg.GiveUpAfterSeconds.Value;
        if (set < 0) return 0;
        if (set == 0) return Math.Clamp(_cfg.TimeoutSeconds.Value, 10, 3600) + 15;
        return Math.Clamp(set, 10, 3600);
    }

    private const float PictureDelaySeconds = 0.6f, PictureWaitSeconds = 4f;
    private static TaskCompletionSource<byte[]?>? _picture;
    private static float _pictureAt;
    private static bool _pictureSkipNoted;

    /// <summary>LookAtNewPeople: the one picture of someone new, handed to the waiting start of the conversation.</summary>
    private static void TakePicture()
    {
        var waiting = _picture;
        _picture = null;
        if (waiting == null) return;
        byte[]? jpeg = null;
        try
        {
            var cam = GameContext.PlayerCamera();
            string? error;
            (jpeg, error) = _npc != null && cam != null ? Game.Snapshot.Take(_npc, cam) : (null, "no camera to take it with");
            if (jpeg != null) Log.Info($"Took a picture of them for the AI ({jpeg.Length / 1024} KB).");
            else Problem($"Couldn't take a picture of them ({error}), so they're written from the game's description instead.", "PIC-CAPTURE");
        }
        catch (Exception e) { Problem($"Couldn't take a picture of them ({e.Message}), so they're written from the game's description instead.", "PIC-CAPTURE"); }
        finally { waiting.TrySetResult(jpeg); }
    }

    internal static void CloseChat(string? notice)
    {
        if (!ChatOpen) return;
        if (notice != null) Log.Debug("Chat closed: " + notice);
        try { EndGroupChat(); } catch (Exception e) { Log.Warn("Ending the group conversation: " + e.Message); } // FB-66
        try
        {
            string? leftId = _session?.Record.Id ?? (_npc != null ? GameContext.NpcId(_npc) : null);
            if (leftId != null) Greetings.JustTalked(leftId, Mathf.Max(1f, _cfg.GreetCooldownMinutes.Value) * 60f);
        }
        catch { }
        _openSerial++;
        _picture?.TrySetResult(null);
        _picture = null;
        try { _cts?.Cancel(); } catch { }
        var s = _session;
        var pending = _inflight;
        var brain = ActiveBrain;
        if (s != null) try { Api.PonApi.RaiseChatClosed(InfoOf(s.Record)); } catch { } // FB-67
        if (s != null)
        {
            // Let an in-flight reply settle before saving and summarising the memory. Kept, so Stop waits for it (7b).
            var saving = Task.Run(async () =>
            {
                if (pending != null) { try { await pending.ConfigureAwait(false); } catch { } }
                try { brain.End(s); } catch (Exception e) { Log.Warn("Saving the conversation failed: " + e.Message); }
                Greetings.Forget(s.Record.Id);
            });
            lock (_closing) { _closing.RemoveAll(t => t.IsCompleted); _closing.Add(saving); }
            // After /resetnpc or /resetall (their epoch moved on) they're a stranger now: their old name isn't put back on
            // the talk prompt (6a review).
            if ((s.Owner ?? brain).EpochFor(s.Record.Id) != s.Epoch) KnownNames.Remove(s.Record.Id);
            else NoteKnownName(s.Record);
        }
        _session = null;
        try { VoicePlayer.Release(_npc); } catch { } // their voice goes once their last line has played
        ForgetTone(s?.Record.Id);
        _npc = null;
        _visual = null;
        _unsent.Clear();
        _openerLine = null;
        _busy = false;
        _closeAt = -1;
        try { StopVoiceInput(); } catch { } // FB-64: the microphone lets go
        var partner = NpcHold.Held;
        try { NpcHold.Release(); } catch (Exception e) { Log.Warn("Releasing the NPC failed: " + e.Message); }
        try { Locks.Release(); } catch (Exception e) { Log.Warn("Releasing player locks failed: " + e.Message); }
        try { Ui.Close(); } catch { }
        try { AfterChatFollow(partner); } catch (Exception e) { Log.Debug("follow after chat: " + e.Message); } // FB-62
        try { AfterChatRomance(partner); } catch (Exception e) { Log.Debug("romance after chat: " + e.Message); } // FB-63
        try { AfterChatInnerLife(s, partner); } catch (Exception e) { Log.Debug("inner life after chat: " + e.Message); } // the inner-life round
    }
}
