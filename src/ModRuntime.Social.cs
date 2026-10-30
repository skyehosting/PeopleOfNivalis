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

/// <summary>Life outside your conversations: people reacting as you pass, and what the city knows (gossip, news, market, food).</summary>
public static partial class ModRuntime
{
    // ------------------------------------------------------------------ people reacting as you pass

    private static void OnGreeting(Character c, NpcRecord rec, GreetingRules.Kind kind, string mood)
    {
        // Children only ever say their written lines: checked on the record in hand too (it's what the voice would describe).
        if (rec.Visual.Minor || ActiveBrain.IsKnownMinor(rec) || MinorInfo(c, rec.Id).minor) return;
        Greetings.Gesture(c, kind);
        var snap = GameContext.Capture(c);
        snap.PlayerGender = _cfg.PlayerGender.Value;
        string who = rec.State.PlayerName.Length > 0 ? rec.State.PlayerName : "the stranger you've talked with before";
        string situation = GreetingRules.Describe(kind, who);
        string fallback = GreetingRules.Fallback(kind, rec.Persona.Speech.SlangLevel, new System.Random());
        string speaker = DisplayName(rec) == "Stranger" ? "" : Prompts.FirstName(DisplayName(rec));
        string id = rec.Id;
        Transform head = GameContext.HeadOf(c);
        var brain = ActiveBrain;
        bool ai = _cfg.AiGreetingLines.Value && AiAllowed(); // over the token limit: their written line
        VoiceSubject Subject() { var v = VoiceSubject.From(rec); v.Mood = mood; return v; }
        bool speak = _cfg.SpeakGreetings.Value;
        Task.Run(async () =>
        {
            string? line;
            try { line = await brain.BarkAsync(id, situation, fallback, mood, snap, ai, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception e) { Log.Debug("greeting line: " + e.Message); line = fallback; }
            Greetings.Forget(id);
            var voice = speak && !string.IsNullOrEmpty(line) ? await VoiceForAsync(Subject, line!, CancellationToken.None, "greeting").ConfigureAwait(false) : null;
            if (voice is { TimedOut: true }) return;
            MainThread.Enqueue(() =>
            {
                if (string.IsNullOrEmpty(line)) return;
                KnownNames.Remove(id);
                if (ChatOpen) return;
                try { if (c == null || c.WasCollected || !c.isActiveAndEnabled) return; } catch { return; }
                if (TalkingNow(c, id)) return; // pulled into a conversation, a call or shop talk meanwhile: no greeting over it (FLOW-5)
                Greetings.Said(id, line!); // kept only once it's shown: the next chat opens as a reply to a line you saw
                float seconds = voice?.Audio != null ? VoicePlayer.Play(c, voice.Audio, LineVolume(id), _cfg.HearingDistance.Value) : 0f;
                VoicePlayer.Release(c); // one line: the voice goes once it has played
                Ui.ShowBark(speaker, line!, head, Mathf.Max(Mathf.Clamp(2.5f + line!.Length / 14f, 3f, 7f), seconds + 1f), id, body: c.transform);
                if (_transcripts != null)
                    RecordLine("greeting", $"greet:{id}:{Time.unscaledTime:0.0}", c, id, rec, line!, "you", $"{rec.State.MoodIntensity} {rec.State.Mood}".Trim(),
                        voice: voice, seconds: seconds, by: ai ? "ai-background" : "written", where: snap, relationship: rec.State.Relationship);
            });
        });
    }

    // ------------------------------------------------------------------ children (anyone under 18): written lines only

    private static readonly System.Random MinorRng = new();
    private static readonly Dictionary<string, (bool minor, string name, string body)> MinorCache = new();
    private static readonly Dictionary<string, float> MinorQuietUntil = new();
    private static float _nextMinorScan, _minorQuietAll, _minorRefusedAt = -10f;

    /// <summary>FB-61 review F4: another save's people: every cached under-18 answer is asked again.</summary>
    private static void ForgetMinorChecks()
    {
        MinorCache.Clear();
        MinorQuietUntil.Clear();
        _nextMinorScan = 0f;
    }

    /// <summary>Whether this person is under 18 (cached per person; the game's data and MinAge decide).</summary>
    /// <param name="record">Their record as the caller already glanced at it (<paramref name="read"/>: it did; null = no file, or unreadable), so it isn't read again.</param>
    private static (bool minor, string name, string body) MinorInfo(Character c, string id, NpcRecord? record = null, bool read = false)
    {
        if (MinorCache.TryGetValue(id, out var known)) return known;
        var v = GameContext.Visual(c);
        bool minor, unsure;
        try { minor = read ? ActiveBrain.IsMinor(id, v, record, out unsure) : ActiveBrain.IsMinor(id, v, out unsure); }
        catch { minor = v.Minor; unsure = !minor; }
        var info = (minor, v.Named ? v.GameName : "", v.Body);
        // "Not a minor" from a record that's there but couldn't be read right now isn't kept for the session: asked again
        // next time, once it can be read (review of step 5, part 2). IsMinor's own glance tells (one read, not two: PERF-3).
        if (!minor && unsure) return info;
        if (MinorCache.Count > 500) MinorCache.Clear();
        MinorCache[id] = info;
        return info;
    }

    /// <summary>A child says a written line over their head (and out loud, when voices are on). Never the AI.</summary>
    private static void MinorSays(Character c, string id, string name, string body, string line)
    {
        Transform head = GameContext.HeadOf(c);
        string speaker = name.Length > 0 ? Prompts.FirstName(name) : "";
        // The voice is picked here, as a child's; nothing about the child (no description, age or directions) is sent with the line.
        var who = new VoiceSubject { Id = id, Presentation = body == "woman" ? "female" : body == "man" ? "male" : "any", Age = 11, Child = true };
        bool speak = _cfg.SpeakGreetings.Value;
        Task.Run(async () =>
        {
            var voice = speak ? await VoiceForAsync(() => who, line, CancellationToken.None, "greeting").ConfigureAwait(false) : null;
            MainThread.Enqueue(() =>
            {
                if (ChatOpen) return;
                try { if (c == null || c.WasCollected || !c.isActiveAndEnabled) return; } catch { return; }
                float seconds = voice?.Audio != null ? VoicePlayer.Play(c, voice.Audio, LineVolume(id), _cfg.HearingDistance.Value) : 0f;
                VoicePlayer.Release(c); // one line: the voice goes once it has played
                Ui.ShowBark(speaker, line, head, Mathf.Max(Mathf.Clamp(2.5f + line.Length / 14f, 3f, 6f), seconds + 1f), body: c.transform, heard: false); // review CF24-1
            });
        });
    }

    /// <summary>Pressing the talk key on a child: a brush-off over their head; the chat window never opens.</summary>
    private static void MinorRefuses(Character c, string id, string name, string body)
    {
        if (Time.unscaledTime - _minorRefusedAt < 2f) return;
        _minorRefusedAt = Time.unscaledTime;
        MinorQuietUntil[id] = Time.unscaledTime + 60f;
        Greetings.Gesture(c, GreetingRules.Kind.Snub);
        MinorSays(c, id, name, body, MinorLines.Refusal(MinorRng));
    }

    /// <summary>Now and then a child in front of the player says something to nobody in particular.</summary>
    private static void TickMinors()
    {
        if (!_cfg.NpcsGreetYou.Value) return;
        float now = Time.unscaledTime;
        if (now < _nextMinorScan) return;
        _nextMinorScan = now + 2.5f;
        if (now < _minorQuietAll) return;
        var cam = GameContext.PlayerCamera();
        if (cam == null) return;
        var pc = GameContext.PlayerCharacter();
        Vector3 eye = cam.transform.position, fwd = cam.transform.forward;
        var all = Character.instances;
        if (all == null) return;
        var it = all.GetEnumerator();
        while (it.MoveNext())
        {
            var c = it.Current;
            try
            {
                if (c == null || !c.isActiveAndEnabled || (pc != null && c.gameObject == pc.gameObject)) continue;
                Vector3 to = c.transform.position + Vector3.up * 1.2f - eye;
                float d = to.magnitude;
                if (d > 10f || d < 1.5f || Vector3.Dot(fwd, to / d) < 0.3f) continue;
                string id = GameContext.NpcId(c);
                if (MinorQuietUntil.TryGetValue(id, out var q) && now < q) continue;
                var (minor, name, body) = MinorInfo(c, id);
                if (!minor) continue;
                if (NpcTargeting.Occluded(eye, c.transform.position + Vector3.up * 1.2f, c, pc)) continue;
                MinorQuietUntil[id] = now + 150f + (float)MinorRng.NextDouble() * 150f; // every few minutes at most
                if (MinorRng.NextDouble() > 0.35) continue;
                _minorQuietAll = now + 25f;
                MinorSays(c, id, name, body, MinorLines.Passing(name, MinorRng));
                return;
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ customers at your place

    private static float _nextVisitScan;

    /// <summary>Counts who eats at the player's places (once per person per game day), so regulars come to know them.</summary>
    private static void TickVisits()
    {
        if (_resetting) return; // FB-61: a save's memory being put in place (the visits book is closed meanwhile)
        if (Time.unscaledTime < _nextVisitScan) return;
        _nextVisitScan = Time.unscaledTime + 5f;
        try
        {
            int n = GameContext.NoteVisitors();
            if (n > 0) Log.Debug($"Visits: {n} customer(s) noted at your place.");
        }
        catch (Exception e) { Log.Debug("visits: " + e.Message); }
    }

    /// <summary>Dev: make someone you've met react to you now, skipping the dice.</summary>
    internal static string ForceGreet(Character c, GreetingRules.Kind kind)
    {
        var rec = ActiveBrain.Store.Glance(GameContext.NpcId(c));
        if (rec == null || rec.State.TimesMet < 1) return "never met";
        string mood = rec.State.Mood.Length > 0 ? rec.State.Mood : "content";
        Greetings.Mark(rec.Id, kind, mood);
        OnGreeting(c, rec, kind, mood);
        return $"{rec.Persona.Name}: {kind} (mood {mood}, affinity {rec.State.Affinity})";
    }

    /// <summary>
    /// City talk and news this person may know, and your menus, attached once per conversation. <paramref name="persona"/>:
    /// who they are, from the record the chat opened with (null for someone new); not read again (PERF-3).
    /// </summary>
    private static void GatherWorld(GameSnapshot snap, string id, Persona? persona)
    {
        _rumors = new List<string>();
        _headlines = new List<string>();
        _menu = new List<MenuEntry>();
        if (_world != null && _cfg.Gossip.Value)
        {
            try { _rumors = ActiveBrain.World?.RumorsFor(snap.Location, snap.GameDay, id, new System.Random()) ?? new List<string>(); }
            catch (Exception e) { Log.Debug("rumors: " + e.Message); }
        }
        if (_cfg.CityNews.Value)
        {
            try
            {
                var world = ActiveBrain.World;
                if (world != null)
                {
                    foreach (var (title, text) in GameContext.ReadNews()) world.AddHeadline(snap.GameDay, title, text);
                    _headlines = world.RecentHeadlines(snap.GameDay);
                }
            }
            catch (Exception e) { Log.Debug("news: " + e.Message); }
        }
        if (_cfg.Favours.Value)
        {
            try { _menu = GameContext.PlayerMenu(); } catch (Exception e) { Log.Debug("menu: " + e.Message); }
        }
        _market = new List<string>();
        _marketOnMind = false;
        _marketUnknown = false;
        _foodPlaces = new List<string>();
        _eatsAtHome = false;
        if (_cfg.MarketTalk.Value || _cfg.PlacesToEat.Value)
        {
            // Who they are decides what they know: food work, temperament, and how closely they follow prices.
            bool food = MarketTalk.WorksWithFood(persona?.Occupation, persona?.Workplace, snap.NpcRoles) || snap.NpcWorksForPlayer;
            double factor = MarketTalk.Factor(food, persona?.Temperament ?? "");
            var detail = FoodScene.DetailFor(id, factor);
            if (_cfg.MarketTalk.Value)
            {
                try
                {
                    var notes = GameContext.ReadMarket();
                    if (notes.Count > 0)
                    {
                        var (known, onMind) = MarketTalk.Roll(id, snap.GameDay, notes, factor, _cfg.MarketKnowChance.Value, _cfg.MarketOnMindChance.Value);
                        _market = known.Select(n => MarketTalk.Describe(n, detail)).ToList();
                        _marketOnMind = onMind;
                        _marketUnknown = known.Count == 0;
                    }
                }
                catch (Exception e) { Log.Debug("market: " + e.Message); }
            }
            if (_cfg.PlacesToEat.Value)
            {
                try
                {
                    if (_townPlacesDay != snap.GameDay) { _townPlaces = GameContext.ReadFoodPlaces(); _townPlacesDay = snap.GameDay; }
                    if (_townPlaces.Count > 0)
                    {
                        _foodPlaces = FoodScene.PlacesFor(id, _townPlaces, detail, snap.Location);
                        _eatsAtHome = _foodPlaces.Count == 0;
                    }
                }
                catch (Exception e) { Log.Debug("food places: " + e.Message); }
            }
        }
        // FB-35: the group they've talked with most lately (met twice or more), first names; never one with a child in it.
        _yourGroup = _yourGroupTalk = "";
        try
        {
            var c = _circles?.NewestFor(id, 2);
            if (c != null && !c.Ids.Any(ActiveBrain.KnownMinors.Contains))
            {
                var others = c.Ids.Where(x => x != id).Select(x => Prompts.FirstName(c.NameOf(x))).Where(n => n.Length > 0 && n != "you").ToList();
                if (others.Count >= 2)
                {
                    _yourGroup = $"You, {string.Join(", ", others.Take(others.Count - 1))} and {others[^1]} have talked together as a group {c.Times} times " +
                                 $"(last on day {c.LastDay}); you know them.";
                    _yourGroupTalk = c.LastTalk;
                }
            }
        }
        catch (Exception e) { Log.Debug("group: " + e.Message); }
        _plans = PlansWith(id); // FB-65
        ApplyWorld(snap);
    }

    private static string _yourGroup = "", _yourGroupTalk = "";

    private static void ApplyWorld(GameSnapshot snap)
    {
        snap.Rumors = _rumors;
        snap.Headlines = _headlines;
        snap.Menu = _menu;
        snap.Market = _market;
        snap.MarketOnMind = _marketOnMind;
        snap.MarketUnknown = _marketUnknown;
        snap.FoodPlaces = _foodPlaces;
        snap.EatsAtHome = _eatsAtHome;
        snap.YourGroup = _yourGroup;
        snap.YourGroupTalk = _yourGroupTalk;
        snap.Plans = _plans;
    }
}
