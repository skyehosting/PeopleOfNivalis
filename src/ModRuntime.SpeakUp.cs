using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nivalis;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Voice;
using UnityEngine;

namespace NivalisAIChat;

/// <summary>
/// From RiftSpeak's list ("NPCs speaking up on events"): the people with you (following you, on a date, at home) say
/// something when something happens: you arrive somewhere new together, the weather turns, night falls, a plan you made
/// with them comes due. One short line over their head (the same as a passing greeting, voiced if greetings are), at
/// most one a minute and a half for each, never while a chat is open.
/// </summary>
public static partial class ModRuntime
{
    private static float _nextSpeakUpCheck;
    private static int _speakUpScene = -1;
    private static string _speakUpWeather = "";
    private static int _speakUpHour = -1;
    private static float _arrivedAt = -1f;
    private static readonly Dictionary<string, float> _spokeUpAt = new();
    private static readonly Queue<(string id, string situation)> _speakUpDue = new();

    /// <summary>A plan with someone came due (FB-65): if they're with you, they bring it up.</summary>
    private static void SpeakUpAboutPlan(string id, string what)
    {
        if (id.Length > 0 && Companion(id) != null) _speakUpDue.Enqueue((id, $"It's time for what you two planned: {what}. You bring it up."));
    }

    private static void TickSpeakUp()
    {
        float now = Time.unscaledTime;
        if (now < _nextSpeakUpCheck) return;
        _nextSpeakUpCheck = now + 2f;
        if (_resetting || ChatOpen || !InGameplay()) return;
        var with = Companions();
        if (with.Count == 0) { _speakUpScene = NpcHold.SceneEpochNow(); return; }
        GameSnapshot place;
        try { place = GameContext.Capture(null); } catch { return; }
        // Arriving somewhere new (a new scene): once they're there with you, a moment later.
        int scene = NpcHold.SceneEpochNow();
        if (_speakUpScene >= 0 && scene != _speakUpScene) _arrivedAt = now + 4f;
        _speakUpScene = scene;
        if (_arrivedAt > 0 && now >= _arrivedAt)
        {
            _arrivedAt = -1f;
            var who = with[UnityEngine.Random.Range(0, with.Count)];
            _speakUpDue.Enqueue((who.id, $"You've just arrived {(place.Location.Length > 0 ? "at " + place.Location : "somewhere new")} together. Say something about it."));
        }
        // The weather turning.
        if (_speakUpWeather.Length > 0 && place.Weather.Length > 0 && place.Weather != _speakUpWeather)
        {
            var who = with[UnityEngine.Random.Range(0, with.Count)];
            _speakUpDue.Enqueue((who.id, $"The weather just turned: now it's {place.Weather}. React to it."));
        }
        if (place.Weather.Length > 0) _speakUpWeather = place.Weather;
        // Night falling (21:00), morning coming (06:00).
        int hour = GameContext.Hour();
        if (_speakUpHour >= 0 && hour != _speakUpHour && hour is 21 or 6)
        {
            var who = with[UnityEngine.Random.Range(0, with.Count)];
            _speakUpDue.Enqueue((who.id, hour == 21 ? "It's getting late; night is falling. Say something about it." : "The sun's coming up; you've been out all night together."));
        }
        if (hour >= 0) _speakUpHour = hour;
        while (_speakUpDue.Count > 0)
        {
            var (id, situation) = _speakUpDue.Dequeue();
            if (_spokeUpAt.TryGetValue(id, out float at) && now - at < 90f) continue;
            if (Companion(id) is not { } c) continue;
            _spokeUpAt[id] = now;
            SpeakUp(c, id, situation);
            break; // one at a time
        }
    }

    /// <summary>Who's with you now: followers (a date among them) and your partner at home.</summary>
    private static List<(Character c, string id)> Companions()
    {
        var list = Followers.Where(f => !f.Paused && Alive(f.C)).Select(f => (f.C, f.Id)).ToList();
        if (_home?.C is { } hc && Alive(hc) && list.All(x => x.Id != _home.Id)) list.Add((hc, _home.Id));
        return list;
    }

    private static Character? Companion(string id) => Companions().FirstOrDefault(x => x.id == id).c;

    /// <param name="said">The inner-life round: told the line once it's shown (a meet-up's greeting opens the next chat as that moment).</param>
    /// <param name="fallback">A stock line when the AI isn't writing greetings (AiGreetingLines off, or it failed); "" = nothing then.</param>
    private static void SpeakUp(Character c, string id, string situation, Action<string>? said = null, string fallback = "")
    {
        var brain = ActiveBrain;
        var known = brain.Store.Glance(id);
        if (known == null || brain.IsKnownMinor(known) || MinorInfo(c, id).minor) return;
        var snap = GameContext.Capture(c);
        snap.PlayerGender = _cfg.PlayerGender.Value;
        string speaker = DisplayName(known) == "Stranger" ? "" : Prompts.FirstName(DisplayName(known));
        Transform head = GameContext.HeadOf(c);
        bool ai = AiAllowed() && _cfg.AiGreetingLines.Value; // step 0: the optimizer's AI-greetings lever covers these lines too
        bool speak = _cfg.SpeakGreetings.Value;
        string mood = known.State.Mood;
        Task.Run(async () =>
        {
            string? line;
            try { line = await brain.BarkAsync(id, situation, fallback, mood, snap, ai, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception e) { Log.Debug("speak up: " + e.Message); return; }
            if (string.IsNullOrWhiteSpace(line)) return;
            var voice = speak ? await VoiceForAsync(() => VoiceSubject.From(known), line!, CancellationToken.None, "greeting").ConfigureAwait(false) : null;
            MainThread.Enqueue(() =>
            {
                if (ChatOpen || !Alive(c)) return;
                float seconds = voice?.Audio != null ? VoicePlayer.Play(c, voice.Audio, LineVolume(id), _cfg.HearingDistance.Value) : 0f;
                VoicePlayer.Release(c);
                Ui.ShowBark(speaker, line!, head, Mathf.Max(Mathf.Clamp(2.5f + line!.Length / 14f, 3f, 7f), seconds + 1f), "speakup:" + id, body: c.transform);
                if (NpcHold.IsHeld(c)) NpcHold.SpeakAs(c, line!, seconds);
                try { said?.Invoke(line!); } catch (Exception e) { Log.Debug("speak up said: " + e.Message); }
                Log.Debug($"speak up: {id}: {situation}");
            });
        });
    }
}
