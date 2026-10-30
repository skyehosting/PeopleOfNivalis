using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using NivalisAIChat.Voice;

namespace NivalisAIChat.Tests;

/// <summary>
/// Step 5 of the audit, safety first: an age written in another script's digits ("١٦" is 16) is read as that age
/// everywhere an age decides something. It used to read as no age at all, which the "not unreadable = child" paths took
/// for an adult. Also ROBUST-11 (a phone list with such a digit stopped that person's calls) and the Claude version
/// read from the model name.
/// </summary>
internal static partial class Program
{
    /// <summary>Arabic-Indic digits for the given values (built from code points, so no escape can be mangled).</summary>
    private static string Indic(params int[] digits) => new(digits.Select(d => (char)(0x0660 + d)).ToArray());

    private static async Task AgeDigitsTests(string dir)
    {
        Console.WriteLine("-- audit 2026-10-11: ages in other scripts' digits; phone lists; model versions (step 5, safety first)");

        // How an age the AI gave is read.
        var (seenAge, seenChild) = CanonAge.FromSeen(Indic(1, 6), unreadableIsChild: false);
        Check(seenChild, $"an age written \"{Indic(1, 6)}\" (16) reads as under 18 (age read: {seenAge?.ToString() ?? "none"})");
        var (adultAge, adultChild) = CanonAge.FromSeen(Indic(4, 0), unreadableIsChild: false);
        Check(adultAge == 40 && !adultChild, $"\"{Indic(4, 0)}\" reads as 40, an adult ({adultAge?.ToString() ?? "none"}, child {adultChild})");
        var (ascii, asciiChild) = CanonAge.FromSeen("16", unreadableIsChild: false);
        Check(ascii == 16 && asciiChild, "an ASCII 16 still reads as under 18 (guard)");
        Check(CanonAge.Number(Indic(2, 2)) == 22, $"CanonAge.Number reads \"{Indic(2, 2)}\" as 22");
        Check(CanonAge.FromDescription($"Razlo is a {Indic(1, 5)} year old boy.") is { Exact: 15, Child: true },
            "the game's description \"is a ١٥ year old\" reads as 15, a child");

        // A record's age decides whether they're judged a child (records, the start-up scan, the indexer).
        var undescribed = new NpcRecord { Id = "p_undescribed", Persona = new Persona { Name = "Lira Vance", Age = Indic(1, 6) },
                                          Visual = new NpcVisual { Named = true, GameName = "Lira Vance", Body = "woman" } };
        Check(Minors.Judge(undescribed).why != null, "a story character whose record says \"١٦\" is judged under 18");
        var described = new NpcRecord { Id = "p_described", Persona = new Persona { Name = "Lira Vance", Age = Indic(1, 6) },
                                        Visual = new NpcVisual { Named = true, GameName = "Lira Vance", Body = "woman", Description = "Lira runs the noodle stall by the pier." } };
        Check(Minors.Judge(described).why != null, "...also when the game's description gives no age");

        // Someone new written as "١٦": never created as an adult.
        var ai = new ScriptedAi { PersonaOverride = PersonaJson.Replace("\"age\":\"41\"", "\"age\":\"" + Indic(1, 6) + "\"") };
        var client = new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(ai));
        var store = new NpcStore(Path.Combine(dir, "age-digits"), _ => { });
        var brain = new NpcBrain(client, store, () => new BrainSettings { LifeUpdates = false, MeetingSummaries = false }, _ => { });
        var snap = new GameSnapshot { GameDay = 3, Hour = 12 };
        var (made, _, madeError) = await brain.BeginAsync("p_new_young", new NpcVisual { Body = "woman" }, snap, CancellationToken.None);
        Check(made == null, $"someone new the AI writes as \"{Indic(1, 6)}\" isn't met as an adult ({madeError ?? "a meeting began"})");
        if (made != null) { brain.End(made); await brain.Settled("p_new_young"); }
        Check(!ai.Kinds.Contains("chat"), "...and nothing is asked of the AI as them");

        // A story character the game gives no age, written as "١٦": flagged, never chatted with.
        var storyAi = new ScriptedAi { PersonaOverride = PersonaJson.Replace("\"age\":\"41\"", "\"age\":\"" + Indic(1, 6) + "\"") };
        var storyBrain = new NpcBrain(new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(storyAi)),
            store, () => new BrainSettings { LifeUpdates = false, MeetingSummaries = false }, _ => { });
        var storyVisual = new NpcVisual { Named = true, GameName = "Lira Vance", Body = "woman", Description = "Lira runs the noodle stall by the pier." };
        var (story, _, storyError) = await storyBrain.BeginAsync("p_story_young", storyVisual, snap, CancellationToken.None);
        Check(story == null && storyError == NpcBrain.MinorError, $"a story character written as \"{Indic(1, 6)}\" is never chatted with ({storyError ?? "a meeting began"})");
        if (story != null) { storyBrain.End(story); await storyBrain.Settled("p_story_young"); }
        Check(store.Load("p_story_young") is not { } savedStory || savedStory.Visual.Minor, "...and their record is flagged under 18 if it was saved");
        Check(!storyAi.Kinds.Contains("chat"), "...and nothing is asked of the AI as them");

        // What age they told you, and the age in a voice's description (not safety, the same reading).
        Check(ReplyParser.AgeStated("How old are you?", Indic(2, 2) + ". Why?") == "22", "\"how old are you?\" answered \"٢٢\" is noted as 22");
        var voiced = VoiceSubject.From(new NpcRecord { Id = "p_voice", Persona = new Persona { Name = "Ana", Age = Indic(3, 4) }, Visual = new NpcVisual { Body = "woman" } });
        Check(voiced.Age == 34, $"a voice's description reads \"{Indic(3, 4)}\" as 34 ({voiced.Age})");

        // ROBUST-11: someone's own list of people, read for a phone call.
        var callStore = new NpcStore(Path.Combine(dir, "age-digits-calls"), _ => { });
        var callBrain = new NpcBrain(client, callStore, () => new BrainSettings(), _ => { });
        var chatter = new ChatterBrain(callBrain, client, new PairBook(Path.Combine(dir, "age-digits-pairs.json")), _ => { });
        NpcRecord Caller(params string[] people) =>
            new() { Id = "p_caller", Persona = new Persona { Name = "Rosa Diaz", Age = "41", People = people.ToList() }, Visual = new NpcVisual { Body = "woman" } };
        string? Pick(NpcRecord who)
        {
            try { return chatter.PickCallee(who, new Random(1))?.Name; }
            catch (Exception e) { return "threw " + e.GetType().Name; }
        }
        // Entries are "Name - relation, detail" (PickCallee splits on dashes; the name is the first part).
        string? child = Pick(Caller("Mia - daughter, " + Indic(7)));
        Check(child == null, $"\"Mia - daughter, {Indic(7)}\" (7) isn't phoned, and nothing throws ({child ?? "nobody called"})");
        string? adult = Pick(Caller("Sam - friend, " + Indic(4, 0)));
        Check(adult == "Sam", $"\"Sam - friend, {Indic(4, 0)}\" (40) can be phoned ({adult ?? "nobody called"})");
        string sui = ((char)0x5C81).ToString(); // "years old", written straight after the number
        Check(Pick(Caller("Mia - daughter, 7" + sui)) == null, $"\"7{sui}\" (a number against a word) still counts as an age under 18");
        Check(Pick(Caller("Mia - daughter, 7yo")) == null, "\"7yo\" still counts as an age under 18");
        Check(Pick(Caller("Teo - brother - on the rig")) == "Teo", "someone with no age in their entry can be phoned (guard)");
        Check(Pick(Caller("Ana - neighbour - flat 214")) == "Ana", "a three-digit number isn't an age (guard)");

        // The Claude version read from the model name decides two request fields; a strange name is no version at all.
        foreach (var (model, effortWanted, fallbackWanted) in new[]
                 {
                     ("claude-opus-" + Indic(5), false, false), ("claude-opus-99999999999", false, false),
                     ("claude-opus-5", true, true), ("claude-sonnet-4-6", true, false), ("claude-opus-4-20250514", false, false),
                 })
        {
            var versionAi = new ScriptedAi();
            var versionClient = new LlmClient(() => new LlmSettings { ApiKey = "k", Model = model }, _ => { }, new HttpMessageInvoker(versionAi));
            var rq = new LlmRequest { MaxTokens = 20 };
            rq.System.Add("x");
            rq.Messages.Add(new LlmMessage("user", "hi"));
            string outcome;
            try { outcome = (await versionClient.CompleteAsync(rq, CancellationToken.None)).Ok ? "ok" : "failed"; }
            catch (Exception e) { outcome = "threw " + e.GetType().Name; }
            var body = versionAi.Seen.LastOrDefault().body;
            bool effort = body?["output_config"]?["effort"] != null, fallback = body?["fallbacks"] != null;
            Check(outcome == "ok" && effort == effortWanted && fallback == fallbackWanted,
                $"model \"{model}\": request {outcome}; effort {effort} (want {effortWanted}), fallbacks {fallback} (want {fallbackWanted})");
        }
    }
}
