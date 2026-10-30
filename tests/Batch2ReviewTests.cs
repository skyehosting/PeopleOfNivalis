using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;

namespace NivalisAIChat.Tests;

/// <summary>The code review of the batch after 0.3.1 (FB-05 overhearing, CF-04 gestures, FB-19 table calls): M1-M3, L1-L9.</summary>
internal static partial class Program
{
    private static async Task Batch2ReviewTests()
    {
        string dir = TestRoot.NewFolder("batch2review");
        Directory.CreateDirectory(dir);
        Console.WriteLine("-- after 0.3.1, review fixes: overhearing");

        // The rule itself: close enough, not in it, not busy, an adult, and the roll.
        Check(Social.ChatterPlan.Overhears(2f, 2.5f, false, false, false, 10, 15), "near, free, an adult, a good roll: they catch it");
        Check(!Social.ChatterPlan.Overhears(3f, 2.5f, false, false, false, 10, 15)
              && !Social.ChatterPlan.Overhears(2f, 2.5f, true, false, false, 10, 15)
              && !Social.ChatterPlan.Overhears(2f, 2.5f, false, true, false, 10, 15)
              && !Social.ChatterPlan.Overhears(2f, 2.5f, false, false, true, 10, 15)
              && !Social.ChatterPlan.Overhears(2f, 2.5f, false, false, false, 15, 15)
              && !Social.ChatterPlan.Overhears(2f, 2.5f, false, false, false, 0, 0),
              "too far, in that conversation, busy in their own, under 18, a roll at the chance, or chance 0: they don't");
        Check(Social.ChatterPlan.Overhears(2f, 2.5f, false, false, false, 99, 150), "a chance over 100 is 100");

        // M1: a conversation that mentions the player anywhere isn't overheard at all (one line gives the rest away).
        Check(Social.ChatterPlan.AboutThePlayer("Saw Bo at the docks.", new[] { "Bo" }, new string[0]), "a two-letter name counts too");
        var ana = new Npc.NpcRecord { Id = "p_ana", Persona = new Npc.Persona { Name = "Ana Reyes" } };
        ana.State.PlayerName = "Mira Vale";
        var bo = new Npc.NpcRecord { Id = "p_bo", Persona = new Npc.Persona { Name = "Bo Lin" } };
        Npc.ChatterScript Script(string raw, params string[] said)
        {
            var s = new Npc.ChatterScript { Raw = raw };
            s.People[ana.Id] = ana; s.People[bo.Id] = bo;
            for (int i = 0; i < said.Length; i++) s.Lines.Add(new Npc.ChatterLine { SpeakerId = i % 2 == 0 ? ana.Id : bo.Id, Text = said[i] });
            return s;
        }
        Check(Npc.ChatterBrain.AboutThePlayer(Script("", "Rent's up again.", "Mira said the same, you know.", "Typical.")),
              "one line about the player: the whole conversation is kept from listeners");
        Check(!Npc.ChatterBrain.AboutThePlayer(Script("", "Rent's up again.", "Typical.")), "an ordinary one isn't");
        Check(Npc.ChatterBrain.AboutThePlayer(Script("{\"lines\":[],\"heardAboutPlayer\":[{\"from\":\"Ana\",\"to\":\"Bo\",\"what\":\"x\"}]}", "Rent's up again.", "Typical.")),
              "nor one where someone told another something about the player");
        var call = new Npc.PhoneScript { Caller = ana, Lines = { new Npc.PhoneLine { Caller = true, Text = "Did Mira call you back?" } } };
        Check(Npc.ChatterBrain.AboutThePlayer(call), "the same for a phone call");

        // L9: two overheard snippets alike but for what was heard are two memories, not one.
        var ear = new Npc.NpcRecord { Id = "p_ear", Persona = new Npc.Persona { Name = "Dana Holt" } };
        ear.AddTalk(new Npc.Talk { Kind = Npc.Talk.Overheard, GameDay = 3, Clock = "14:00", Summary = "overheard people they don't know talking nearby", Lines = { "Someone they don't know: The trawler's late." } });
        ear.AddTalk(new Npc.Talk { Kind = Npc.Talk.Overheard, GameDay = 3, Clock = "14:00", Summary = "overheard people they don't know talking nearby", Lines = { "Someone they don't know: The rent's up." } });
        var keys = Npc.Memories.From(ear).Where(m => m.Text.StartsWith("You overheard")).Select(m => m.Key).ToList();
        Check(keys.Count == 2 && keys.Distinct().Count() == 2, "each overheard snippet is its own memory (memory search keeps both)");

        // Recall: a name only heard in passing isn't someone they know (the overheard talk's names don't count).
        var heardOnly = new Npc.NpcRecord { Id = "p_h", Persona = new Npc.Persona { Name = "Kai Ito" } };
        heardOnly.AddTalk(new Npc.Talk { Kind = Npc.Talk.Overheard, With = "Lena Morr", Summary = "overheard Lena talking nearby" });
        var metHer = new Npc.NpcRecord { Id = "p_m", Persona = new Npc.Persona { Name = "Kai Ito" } };
        metHer.AddTalk(new Npc.Talk { Kind = Npc.Talk.InPerson, With = "Lena Morr", Summary = "talked about boats" });
        Check(Npc.Recall.AboutThePast("Lena is here", metHer) && !Npc.Recall.AboutThePast("Lena is here", heardOnly),
              "someone they talked with is a name they know; someone they only overheard isn't");

        // Thinking out loud is about their own talks, not what they caught in passing.
        var thinker = new Npc.NpcRecord { Id = "p_t", Persona = new Npc.Persona { Name = "Kai Ito" } };
        thinker.AddTalk(new Npc.Talk { Kind = Npc.Talk.InPerson, With = "Mo", Summary = "TALK-THEY-HAD" });
        thinker.AddTalk(new Npc.Talk { Kind = Npc.Talk.Overheard, Summary = "SNIPPET-THEY-HEARD", Lines = { "Someone they don't know: hi" } });
        string think = string.Join("\n", ChatterPrompts.ThinkRequest(thinker, new Game.GameSnapshot()).Messages.Select(m => m.Content));
        Check(think.Contains("TALK-THEY-HAD") && !think.Contains("SNIPPET-THEY-HEARD"), "thinking out loud leaves overheard snippets out");

        // Never kept for someone under 18.
        var store = new Npc.NpcStore(Path.Combine(dir, "npcs"), _ => { });
        store.Create(new Npc.NpcRecord { Id = "p_kid", Persona = new Npc.Persona { Name = "Pip Ito", Age = "15" }, Visual = new Npc.NpcVisual { Body = "boy" } });
        var ep = new FakeEndpoint();
        var client = new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(ep));
        var brain = new Npc.NpcBrain(client, store, () => new Npc.BrainSettings(), _ => { });
        var chatter = new Npc.ChatterBrain(brain, client, new Social.PairBook(Path.Combine(dir, "pairs.json")), _ => { });
        await chatter.RememberOverheardAsync("p_kid", chatter.EpochFor("p_kid"), new Game.GameSnapshot { GameDay = 2 }, new List<(string, string, string)> { ("p_x", "Lena Morr", "Hi.") });
        Check(store.Load("p_kid")?.Talks.Any(t => t.Kind == Npc.Talk.Overheard) == false, "someone under 18 keeps nothing they overheard");

        await TableCallReviewTests(dir);
    }

    private static async Task TableCallReviewTests(string dir)
    {
        Console.WriteLine("-- after 0.3.1, review fixes: calls at a table");
        // L3: through the phone speaker a voice is a bit quieter (about Level), and loud parts are squashed well under it.
        float[] Sine(double hz, double amp) => Enumerable.Range(0, 24000).Select(i => (float)(amp * Math.Sin(2 * Math.PI * hz * i / 24000))).ToArray();
        double Rms(float[] s) => Math.Sqrt(s.Skip(2400).Select(x => (double)x * x).Average());
        var quiet = Sine(1000, 0.05);
        double gain = Rms(Voice.PhoneSpeaker.Filter(new Voice.PcmAudio { Samples = quiet, SampleRate = 24000 }).Samples) / Rms(quiet);
        Check(gain > 0.75 && gain < 0.9, $"an ordinary voice comes through a little quieter ({gain:0.00}, about {Voice.PhoneSpeaker.Level})");
        var loud = Voice.PhoneSpeaker.Filter(new Voice.PcmAudio { Samples = Sine(1000, 1.0), SampleRate = 24000 });
        Check(loud.Samples.Max(Math.Abs) <= Voice.PhoneSpeaker.Level / Voice.PhoneSpeaker.Drive + 1e-6, "a loud one never gets past Level / Drive");

        // L5: no goodbyes asked for when there's a call (everyone stays at the table).
        var ana = new Npc.NpcRecord { Id = "p_ana", Persona = new Npc.Persona { Name = "Ana Reyes" } };
        var bo = new Npc.NpcRecord { Id = "p_bo", Persona = new Npc.Persona { Name = "Bo Lin" } };
        var cy = new Npc.NpcRecord { Id = "p_cy", Persona = new Npc.Persona { Name = "Cy Park" } };
        var people = new List<ChatterPrompts.Person> { new() { Record = ana, MaxLines = 3 }, new() { Record = bo, MaxLines = 1 }, new() { Record = cy, MaxLines = 2, JoinAt = 3 } };
        Check(ChatterPrompts.Plan(people, false).Contains("natural goodbye") && !ChatterPrompts.Plan(people, true, call: true).Contains("natural goodbye"),
              "with a call, the plan asks for no goodbyes");

        // L5: each person's share holds at a table with a call too; nobody speaks before they've come over; the far end is kept short.
        var tc = new ChatterPrompts.TableCall { HolderId = "p_ana", HolderName = "Ana Reyes", Callee = new ChatterPrompts.Callee { Name = "Mira Sol" }, Answered = true };
        string L(string who, string say, bool phone = false) =>
            $"{{\"speaker\":\"{who}\",\"to\":\"\",\"say\":\"{say}\",\"tone\":\"\",\"gesture\":\"\",\"leaving\":false,\"phone\":{(phone ? "true" : "false")},\"speakerphone\":false}}";
        string json = "{\"lines\":[" + string.Join(",", L("Cy", "Too early."), L("Ana", "Hey."), L("Bo", "Hi."), L("Bo", "Again."), L("Ana", "One sec, Mira.", true),
            L("Mira", "One."), L("Mira", "Two."), L("Mira", "Three."), L("Cy", "Made it."), L("Bo", "Third.")) + "],\"summary\":\"x\"}";
        var script = Npc.ChatterBrain.ParseTableCall(json, people, tc)!;
        Check(script.Lines.Count(l => l.SpeakerId == "p_bo") == 1, "someone with one line says one");
        Check(script.Lines.First().SpeakerId != "p_cy" && script.Lines.Count(l => l.SpeakerId == "p_cy") == 1, "someone still on their way says nothing until they've come over");
        Check(script.Lines.Count(l => l.FarEnd) == 2, "the caller says at most one more line than was said into the phone");

        // L6 and the fallback: the phone is someone's seated at the table from the start; nobody to ring them, it's a plain talk.
        var store = new Npc.NpcStore(Path.Combine(dir, "table-npcs"), _ => { });
        foreach (var (id, name) in new[] { ("p_t1", "Ana Reyes"), ("p_t2", "Bo Lin"), ("p_t3", "Cy Park") })
            store.Create(new Npc.NpcRecord { Id = id, Persona = new Npc.Persona { Name = name, Age = "34", People = { "Teo — brother — on the rig" } }, Visual = new Npc.NpcVisual { Body = "man" } });
        store.Create(new Npc.NpcRecord { Id = "p_lone1", Persona = new Npc.Persona { Name = "Ana Reyes", Age = "34" }, Visual = new Npc.NpcVisual { Body = "man" } });
        store.Create(new Npc.NpcRecord { Id = "p_lone2", Persona = new Npc.Persona { Name = "Bo Lin", Age = "34" }, Visual = new Npc.NpcVisual { Body = "man" } });
        string reply = "{\"lines\":[" + string.Join(",", L("Ana", "Long day."), L("Bo", "Tell me about it."), L("Ana", "See you."), L("Bo", "Later.")) + "],\"summary\":\"x\"}";
        var ep = new FakeEndpoint { Respond = _ => (200, AnthropicText(reply)) };
        var client = new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(ep));
        var brain = new Npc.NpcBrain(client, store, () => new Npc.BrainSettings(), _ => { });
        var chatter = new Npc.ChatterBrain(brain, client, new Social.PairBook(Path.Combine(dir, "table-pairs.json")), _ => { });
        var members = new List<Npc.ChatterMember>
        {
            new() { Id = "p_t1", Seated = true, MaxLines = 3, Visual = new Npc.NpcVisual { Body = "man" } },
            new() { Id = "p_t2", Seated = true, MaxLines = 3, Visual = new Npc.NpcVisual { Body = "man" } },
            new() { Id = "p_t3", Seated = false, JoinAt = 2, MaxLines = 3, Visual = new Npc.NpcVisual { Body = "man" } },
        };
        var holders = new HashSet<string>();
        for (int seed = 0; seed < 24; seed++)
        {
            var s = await chatter.WriteTableCallAsync(members, new string[0], new string[0], 2000, new HashSet<string>(), new Random(seed), CancellationToken.None);
            if (s?.Call != null) holders.Add(s.Call.HolderId);
        }
        Check(holders.Count > 0 && !holders.Contains("p_t3"), $"the one walking over never has the phone (holders: {string.Join(", ", holders)})");
        var lone = new List<Npc.ChatterMember>
        {
            new() { Id = "p_lone1", Seated = true, MaxLines = 3, Visual = new Npc.NpcVisual { Body = "man" } },
            new() { Id = "p_lone2", Seated = true, MaxLines = 3, Visual = new Npc.NpcVisual { Body = "man" } },
        };
        var plain = await chatter.WriteTableCallAsync(lone, new string[0], new string[0], 2000, new HashSet<string>(), new Random(1), CancellationToken.None);
        Check(plain != null && plain.Call == null && plain.Lines.Count >= 2 && !ep.Requests.Last().body.ToJsonString().Contains("[A PHONE CALL]"),
              "nobody they know to ring them: an ordinary conversation, asked and played as before");
    }
}
