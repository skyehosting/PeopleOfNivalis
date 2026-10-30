using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Mind;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;

namespace NivalisAIChat.Tests;

/// <summary>The inner-life round: tied in everywhere it must be (per save, prompts, residents' talk, mutters, groups, a meeting's mood).</summary>
internal static partial class Program
{
    private static string Body(string text, string signature)
    {
        int at = text.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return "";
        int open = text.IndexOf('{', at), depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text.Substring(open, i - open + 1);
        }
        return "";
    }

    private static async Task MindWiringTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- inner life: every book is the save's (the author: \"everything still maintains per-save\")");
        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string runtime = File.ReadAllText(Path.Combine(src, "ModRuntime.cs")), mind = File.ReadAllText(Path.Combine(src, "ModRuntime.Mind.cs"));
        string open = Body(runtime, "private static void OpenSaveData(") + Body(mind, "private static void OpenMindBooks(");
        var files = Regex.Matches(open, @"Path\.Combine\(dataDir,\s*""([^""]+)""").Select(m => m.Groups[1].Value).Distinct().ToList();
        // npcs-mock is only read there by the under-18 scan: the dev commands' mock people, not a book of the save's.
        var notPerSave = files.Where(f => !SaveSnapshots.PerSave.Contains(f) && f != "npcs-mock").ToList();
        Check(files.Count >= 10 && files.Contains("minds.json") && files.Contains("witness.json") && notPerSave.Count == 0,
            $"every book a save's memory opens ({files.Count}) is kept per save{(notPerSave.Count > 0 ? "; not: " + string.Join(", ", notPerSave) : "")}");
        string saves = File.ReadAllText(Path.Combine(src, "ModRuntime.Saves.cs")), commands = File.ReadAllText(Path.Combine(src, "ModRuntime.Commands.cs"));
        Check(Body(runtime, "private static void CloseSaveData(").Contains("CloseMindBooks()") && Body(runtime, "public static void Stop(").Contains("CloseMindBooks()")
              && Body(saves, "internal static void OnGameSaved(").Contains("FlushMindBooks()"),
            "...closed when a load swaps the save and on a stop, written before a save is taken");
        Check(Body(commands, "private static void ResetEveryone(").Contains("_minds?.Clear(); _witness?.Clear();")
              && Body(runtime, "private static void ForgetUnder18(").Contains("_minds?.Forget(id, name);") && Body(runtime, "private static void ForgetUnder18(").Contains("_witness?.Forget(id, name);"),
            "...emptied by /resetall, and anyone found to be under 18 is forgotten from both");

        Console.WriteLine("-- inner life: in the prompts it reaches (and nowhere it shouldn't)");
        var rec = new NpcRecord { Id = "mira", Persona = P("cynical and dry"), Visual = new NpcVisual { Body = "woman" } };
        var snap = new GameSnapshot { GameDay = 5, Hour = 20, Location = "Neon Ramen" };
        Check(!Prompts.SituationBlock(rec, snap).Contains(InnerCard.Heading), "no card when the code has nothing to say");
        snap.Inner = new List<string> { "On your mind: rent due on Friday.", "People you know nearby: Rina (a good friend)." };
        string sit = Prompts.SituationBlock(rec, snap);
        Check(sit.Contains(InnerCard.Heading + Environment.NewLine + "- On your mind: rent due on Friday.") && sit.Contains("- People you know nearby: Rina (a good friend)."),
            "the chat's [INSIDE YOU RIGHT NOW] holds the card's lines");
        Check(Prompts.IdentityBlock(rec).Contains(Prompts.LetThemGo), "every chat: when you go, they let you go (no guilt at goodbye)");
        var people = new List<ChatterPrompts.Person>
        {
            new() { Record = rec, Snap = snap, MaxLines = 3, Mood = "irritated", MoodReason = "a row earlier", OnMind = "the rent" },
            new() { Record = new NpcRecord { Id = "lou", Persona = P("laid-back and unbothered") }, Snap = snap, MaxLines = 3 },
        };
        var chatter = ChatterPrompts.ChatterRequest(people, (_, _) => null, false, new List<string>(), new List<string>(), 2000,
            tellings: new List<string> { "Mira may bring up, if the talk allows: Doran had a row with a waiter." }, wants: new List<string> { "Mira wants to get away soon." });
        string sys = string.Join("\n", chatter.System);
        Check(sys.Contains("Mood: irritated (a row earlier).") && sys.Contains("On their mind: the rent.") && sys.Contains(Volitions.Heading)
              && sys.Contains("- Mira wants to get away soon.") && sys.Contains("[ON THEIR MINDS]") && sys.Contains("Doran had a row with a waiter"),
            "residents' talk: how each feels now, what's on their mind, what each wants, what one may pass on");
        var think = ChatterPrompts.ThinkRequest(rec, snap, 1, null, false, candidates: 3, mood: "sad", moodReason: "stood up");
        string ask = think.Messages[0].Content;
        Check(ask.Contains("lines = 3 different ways they might say it") && ask.Contains("How they feel right now: sad (stood up)."), "a one-line mutter: three ways to say it, in how they feel now");
        Check(!ChatterPrompts.ThinkRequest(rec, snap, 2, null, false, candidates: 3).Messages[0].Content.Contains("different ways"), "...a longer mutter is asked as before");

        Console.WriteLine("-- inner life: a group's turns held to the plan");
        var gc = new GroupChat();
        gc.Members.Add(new GroupChat.Member { Record = new NpcRecord { Id = "a", Persona = new Persona { Name = "Ana Ruiz" } } });
        gc.Members.Add(new GroupChat.Member { Record = new NpcRecord { Id = "b", Persona = new Persona { Name = "Bo Lind" } } });
        var greq = gc.Request("Bo, you in?", (_, _) => null, 2000, turns: "[Turns, decided: Bo answers. Ana listens this time.]");
        Check(greq.Messages[0].Content.EndsWith("[Turns, decided: Bo answers. Ana listens this time.] What do they say? (at most 3 lines)"), "the turns go in as decided");
        var reply = gc.Parse("{\"lines\":[{\"speaker\":\"Ana Ruiz\",\"to\":\"\",\"say\":\"Me first.\",\"tone\":\"\",\"gesture\":\"\",\"leaving\":false},{\"speaker\":\"Bo Lind\",\"to\":\"\",\"say\":\"Sure.\",\"tone\":\"\",\"gesture\":\"\",\"leaving\":false}],\"feel\":[]}");
        reply.KeepTo(new HashSet<string> { "b" });
        Check(reply.Lines.Count == 1 && reply.Lines[0].SpeakerId == "b", "...only those given a turn speak");
        var all = gc.Parse("{\"lines\":[{\"speaker\":\"Ana Ruiz\",\"to\":\"\",\"say\":\"Hm.\",\"tone\":\"\",\"gesture\":\"\",\"leaving\":false}],\"feel\":[]}");
        all.KeepTo(new HashSet<string> { "b" });
        Check(all.Lines.Count == 1, "...and an answer that ignored them isn't silenced into nothing");

        Console.WriteLine("-- inner life: a hand-edited book reads back without breaking");
        string bad = TestRoot.NewFolder("pon-inner-bad");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "witness.json"), "{\"By\":{\"a\":[{\"Kind\":null,\"Place\":null,\"ToldTo\":null,\"ShownDays\":null,\"Salience\":0.5,\"Pinned\":true},null],\"b\":null}}");
        File.WriteAllText(Path.Combine(bad, "minds.json"), "{\"People\":{\"a\":{\"Id\":null,\"Affect\":{\"V\":\"NaN\",\"Moodlets\":[null,{\"Key\":null,\"Text\":null,\"Until\":99999999}]},\"Openers\":null,\"Current\":{\"Text\":null}},\"b\":null}}");
        bool readOk = true;
        try
        {
            var wb = new WitnessBook(Path.Combine(bad, "witness.json"), _ => { });
            var list = wb.Of("a", 100);
            _ = list.Select(b => Happenings.Words(b, "a", "x", 100)).ToList();
            var mb = new MindBook(Path.Combine(bad, "minds.json"), _ => { });
            _ = mb.MoodNow("a", 100);
            _ = mb.Phrases("a");
            _ = mb.Current("a");
        }
        catch (Exception e) { readOk = false; Console.WriteLine("    " + e); }
        Check(readOk, "witness.json and minds.json with nulls and NaN in them read back without breaking");

        Console.WriteLine("-- inner life: the review's fixes");
        Check(PhraseLedger.Unnumbered("1. Hey there") == "Hey there" && PhraseLedger.Unnumbered("- Yo") == "Yo" && PhraseLedger.Unnumbered("*waves* Hi") == "*waves* Hi"
              && PhraseLedger.Unnumbered("5.5 million, can you believe it") == "5.5 million, can you believe it" && PhraseLedger.Unnumbered("1990: good year") == "1990: good year",
            "only a real list marker comes off a candidate (an action, a number, a year stay)");
        string fx = TestRoot.NewFolder("pon-inner-fix");
        Directory.CreateDirectory(fx);
        var fb = new WitnessBook(Path.Combine(fx, "w.json"), _ => { });
        long t0 = GameClock.At(2, 20, 0);
        fb.Witness(Happenings.Argued, "doran", "Doran Pike", "waiter", "a waiter", "Neon Ramen", t0, 0.8f,
            new[] { new Observer { Id = "rina", Relation = 1.4f, Distance = 1f, Sees = true }, new Observer { Id = "lou", Relation = 1.4f, Distance = 1f, Sees = true } });
        var ppl = new List<Telling.Speaker> { new() { Id = "rina", Name = "Rina Vale", Persona = P("nosy and gossipy") }, new() { Id = "lou", Name = "Lou Park", Persona = P("warm and chatty") } };
        var fpairs = new PairBook(Path.Combine(fx, "p.json"));
        for (int i = 0; i < 10; i++) fpairs.Record("rina", "Rina", "lou", "Lou", 10, 2, "talked");
        Check(Enumerable.Range(0, 60).All(i => Telling.Pick(fb, ppl, fpairs.Get, t0 + 60, new Random(i)).Count == 0), "nothing is told to someone who saw it too");
        var pl = new Telling.Plan { TellerId = "rina", TellerName = "Rina Vale", Belief = fb.Of("rina", t0)[0] };
        Check(Telling.Said(pl, new[] { "Did you hear about Doran last night?" }) && Telling.Said(pl, new[] { "There was a proper row at Neon Ramen." })
              && !Telling.Said(pl, new[] { "Rain again.", "Yeah, typical." }), "told only when her lines brought it up (a name, the place, or what happened)");
        fb.Hear("lou", "Lou", pl.Belief, "rina", "Rina Vale", Telling.Straight, 1f, t0 + 90, new Random(1));
        Check(fb.Of("rina", t0 + 91)[0].ToldTo.Contains("lou"), "told to someone who knew already, it still counts as told");
        Check(fb.Of("rina", t0 - 600).All(b => b.Touched <= t0 - 600), "a clock that went back can't leave a memory that never fades");
        var fm = new MindBook(Path.Combine(fx, "m.json"), _ => { });
        fm.Add("lou", "Lou", null, new Moodlet { Key = "row:kim", Text = "still annoyed after words with Kim", V = -0.3f, Until = t0 + 600 }, t0);
        fm.Add("lou", "Lou", null, new Moodlet { Key = "x", Text = "Kim's party was loud", V = -0.2f, Until = t0 + 600 }, t0);
        fm.Add("lou", "Lou", null, new Moodlet { Key = "y", Text = "the rent", V = -0.2f, Until = t0 + 600 }, t0);
        fm.Forget("kim", "Kim Ito");
        Check(fm.Moodlets("lou", t0 + 1).Select(m => m.Key).SequenceEqual(new[] { "y" }), "someone forgotten (found to be under 18) leaves nothing in anyone else's mind");
        for (int i = 0; i < MindBook.MaxPeople + 5; i++) fm.Think("p" + i, "P", null, t0 + i);
        Check(fm.Count == MindBook.MaxPeople && fm.Has("p" + (MindBook.MaxPeople + 4)), "past 600 people the newest is kept, the longest unthought-of goes");

        Console.WriteLine("-- inner life: tastes (the author: favourite foods, and never a dish more than three times)");
        var eater = new NpcRecord { Id = "eater", Persona = P("warm and chatty") };
        Check(Tastes.Note(eater, "Spicy Ramen", "Ramen Noir", 10) && !Tastes.Note(eater, "spicy ramen", "Ramen Noir", 10), "a dish eaten is kept once per day and place");
        Tastes.Note(eater, "Kelp Soup", "Izakaya", 2); Tastes.Note(eater, "Kelp Soup", "Izakaya", 6); Tastes.Note(eater, "Kelp Soup", "Izakaya", 9);
        Check(Tastes.Favourite(eater, 10) == "Kelp Soup" && Tastes.SickOf(eater, 10).Count == 0, "on three different days: their favourite");
        Tastes.Note(eater, "Spicy Ramen", "Ramen Noir", 11); Tastes.Note(eater, "Spicy Ramen", "Noodle Hut", 12);
        Check(Tastes.SickOf(eater, 12).SequenceEqual(new[] { "Spicy Ramen" }) && Tastes.MenuNote(eater, "Spicy Ramen", 12).Contains("want something else")
              && Tastes.MenuNote(eater, "Kelp Soup", 10) == " (your favourite)", "three times this week: sick of it, and your menu says so; a favourite is marked");
        Check(Tastes.Line(eater, 12) == "Food: you've had the Spicy Ramen three times this week and you're sick of it; you'd go for something else; the Spicy Ramen is your favourite, but even you need a break from it."
              && Tastes.SickOf(eater, 19).Count == 0, "their card says it (even a favourite needs a break); a week later they'd have it again");
        Check(Tastes.Mutter(eater, 12).StartsWith("sick of Spicy Ramen") && Tastes.Mutter(new NpcRecord(), 12) == "", "it's on their mind now and then");
        var menuRec = new NpcRecord { Id = "eater", Persona = P("warm and chatty"), State = eater.State };
        menuRec.State.KnowsYourPlace = true;
        var menuSnap = new GameSnapshot { GameDay = 12, PlayerVenues = { "Ramen Noir" }, Menu = { new MenuEntry { Venue = "Ramen Noir", Dish = "Spicy Ramen", Price = 40 } } };
        Check(Prompts.SituationBlock(menuRec, menuSnap).Contains("Spicy Ramen (40 Lims) (you've had it three times this week: you want something else)"),
            "what they ask you to cook follows their taste (your menu, as they see it)");

        Console.WriteLine("-- inner life: a meeting carries how they still feel");
        string dir = TestRoot.NewFolder("pon-inner-wiring");
        var store = new NpcStore(dir, _ => { });
        var ep = new FakeEndpoint();
        ep.Respond = body =>
        {
            string firstUser = body["messages"]![0]!["content"]!.GetValue<string>();
            if (firstUser.Contains("Create the resident")) return (200, AnthropicText(PersonaJson));
            return (200, AnthropicText("{\"say\":\"Oh, it's you.\",\"mood\":\"\",\"moodIntensity\":\"\",\"moodReason\":\"\",\"affinityChange\":0,\"learnedPlayerName\":\"\",\"newFacts\":[],\"leaving\":false}"));
        };
        var client = new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(ep));
        var minds = new MindBook(Path.Combine(dir, "minds.json"), _ => { });
        var brain = new NpcBrain(client, store, () => new BrainSettings(), _ => { }) { Minds = minds };
        var meet = new GameSnapshot { GameDay = 6, Hour = 12, CarriedMood = "angry", CarriedIntensity = "strong", CarriedReason = "still sore at the newcomer after that last talk" };
        var (s, _, _) = await brain.BeginAsync("person_sore", new NpcVisual { Body = "woman" }, meet, CancellationToken.None);
        Check(s != null && s.Record.State.Mood == "angry" && s.Record.State.MoodIntensity == "strong" && s.Record.State.MoodReason.StartsWith("still sore"),
            "still sore from an hour ago: the meeting starts angry, and says why (no fresh roll)");
        var waved = new GameSnapshot { GameDay = 6, Hour = 13, CarriedMood = "angry", ApproachMood = "cheerful", Approach = "they waved" };
        var (s2, _, _) = await brain.BeginAsync("person_waved", new NpcVisual { Body = "man" }, waved, CancellationToken.None);
        Check(s2?.Record.State.Mood == "cheerful", "...the mood they just greeted you in still comes first");
        if (s != null)
        {
            await brain.SayAsync(s, "hi", meet, CancellationToken.None);
            await brain.SayAsync(s, "hello?", meet, CancellationToken.None);
            var req = ep.Requests.Last().body["system"]!.AsArray().Select(x => x!["text"]!.GetValue<string>()).ToList();
            Check(req.Any(t => t.StartsWith("[This reply: About ")) && req.Last() == Prompts.SafetyLast, "each reply gets its turn card, the safety line still last");
            Check(minds.Phrases("person_sore").openers.Count(o => o == "oh") >= 1, "what they said goes into their phrase ledger");
        }
        brain.End(s!);
        brain.End(s2!);
        await brain.SettledAll();
        minds.Close();
    }
}
