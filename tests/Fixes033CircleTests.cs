using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;

namespace NivalisAIChat.Tests;

/// <summary>FB-35: groups of residents who've talked together ("circles").</summary>
internal static partial class Program
{
    private static async Task CircleTests(string dir)
    {
        Console.WriteLine("-- FB-35: a memory of each group conversation (circles)");
        string path = Path.Combine(dir, "circles.json");
        var book = new CircleBook(path);
        var t0 = new DateTime(2026, 10, 13, 2, 0, 0, DateTimeKind.Utc);
        var trio = new List<(string, string, int)> { ("p_c", "Cy Moss", -1), ("p_a", "Ana Reyes", 1), ("p_b", "Bo Lund", 0) };

        Check(!book.Record(trio.Take(2).ToList(), 3, "two of them", () => true, t0), "two people aren't a circle (they're a pair)");
        Check(book.Record(trio, 3, "Rent and the rain.", () => true, t0) && book.Count == 1, "three who spoke are remembered as a group");
        book.Record(trio, 3, "Carried on about the docks.", () => true, t0.AddMinutes(10));
        var c = book.Best(new[] { "p_a", "p_b", "p_c" })!;
        Check(c.Times == 1 && c.LastTalk == "Carried on about the docks.", "parts of the same sitting (a table carrying on) count once; the last talk is kept");
        book.Record(trio, 4, "The new noodle stall.", () => true, t0.AddMinutes(20));
        Check(book.Best(new[] { "p_a", "p_b", "p_c" })!.Times == 2, "...another day is another time");
        Check(string.Join(",", c.Ids) == "p_a,p_b,p_c" && c.How["p_a"] == 1 && c.How["p_c"] == -1, "the same group whatever the order; how it went for each");
        Check(!book.Record(trio, 5, "after a reset", () => false, t0.AddDays(1)) && book.Best(new[] { "p_a", "p_b" })!.LastDay == 4,
              "someone reset since it was written: not kept (checked under the book's lock)");

        // Who's found: the circle most of them are in.
        book.Record(new List<(string, string, int)> { ("p_a", "Ana Reyes", 0), ("p_b", "Bo Lund", 0), ("p_d", "Dee Hart", 0), ("p_e", "Eli Stone", 0) }, 6, "A long night.", () => true, t0.AddDays(2));
        Check(book.Best(new[] { "p_a", "p_b", "p_c" })!.Ids.Contains("p_c"), "the circle with the most of them present is found");
        Check(book.Best(new[] { "p_a", "p_x" }) == null, "...one of them alone finds nothing");

        // What the writer is told.
        string line = CircleBook.GroupLine(book.Best(new[] { "p_a", "p_b", "p_c" })!, new[] { "p_a", "p_b", "p_c" });
        Check(line.StartsWith("[THEIR GROUP] Ana, Bo and Cy have talked together as a group 2 time(s), last on day 4: The new noodle stall.")
              && line.Contains("Ana enjoyed it last time.") && line.Contains("Cy didn't."), "[THEIR GROUP]: who, how often, last time's talk, who enjoyed it: " + line);
        string partial = CircleBook.GroupLine(book.Best(new[] { "p_a", "p_b" })!, new[] { "p_a", "p_b" });
        Check(partial.Contains("Ana and Bo (with") && partial.Contains("not here now"), "two of a group: the others are named as not here");

        ChatterPrompts.Person P(string id, string name) => new() { Record = new NpcRecord { Id = id, Persona = new Persona { Name = name, Age = "40" } }, MaxLines = 3 };
        var people = new List<ChatterPrompts.Person> { P("p_a", "Ana Reyes"), P("p_b", "Bo Lund"), P("p_c", "Cy Moss") };
        var pairs = new PairBook(Path.Combine(dir, "circle-pairs.json"));
        pairs.Record("p_a", "Ana Reyes", "p_b", "Bo Lund", 2, 4, "The new noodle stall.");
        var circle = book.Best(people.Select(p => p.Record.Id).ToList());
        var req = ChatterPrompts.ChatterRequest(people, pairs.Get, false, new List<string>(), new List<string>(), 2000, circle: circle);
        string sys = string.Join("\n", req.System);
        Check(sys.Contains("[THEIR GROUP]") && sys.Split("The new noodle stall.").Length == 2,
              "the request says the group's last talk once (the pair's same 'last time' isn't repeated)");
        var plain = ChatterPrompts.ChatterRequest(people, pairs.Get, false, new List<string>(), new List<string>(), 2000);
        Check(!string.Join("\n", plain.System).Contains("[THEIR GROUP]"), "...and nothing without a circle (guard)");

        // [YOUR GROUP] in a chat with you.
        var rec = new NpcRecord { Id = "p_a", Persona = new Persona { Name = "Ana Reyes", Age = "40" } };
        string block = Prompts.SituationBlock(rec, new GameSnapshot { YourGroup = "You, Bo and Cy have talked together as a group 2 times.", YourGroupTalk = "The new noodle stall." });
        Check(block.Contains("[YOUR GROUP] You, Bo and Cy have talked together as a group 2 times. Last time: The new noodle stall."), "a chat knows the group they talk with");
        rec.LifeLog.Add(new LifeEvent { GameDay = 4, Text = "Talked with Bo and Cy: The new noodle stall." });
        block = Prompts.SituationBlock(rec, new GameSnapshot { YourGroup = "You, Bo and Cy have talked together as a group 2 times.", YourGroupTalk = "The new noodle stall." });
        Check(block.Contains("[YOUR GROUP]") && !block.Contains("Last time: The new noodle stall."), "...without repeating last time's talk when their life lately says it");

        // Kept on disk; forgotten with a person; cleared; capped.
        book.Close();
        var again = new CircleBook(path);
        Check(again.Count == 2 && again.Best(new[] { "p_a", "p_b", "p_c" })?.Times == 2, "kept in circles.json across a restart");
        Check(again.Forget("p_c") && again.Count == 1 && again.Best(new[] { "p_a", "p_b", "p_c" })?.Ids.Contains("p_c") != true,
              "someone reset (or found to be under 18): every group with them goes");
        again.Clear(Path.Combine(dir, "circle-backup"));
        Check(again.Count == 0 && File.Exists(Path.Combine(dir, "circle-backup", "circles.json")), "/resetall: all of them go, the file backed up first");
        for (int i = 0; i < CircleBook.MaxCircles + 5; i++)
            again.Record(new List<(string, string, int)> { ("q" + i, "A", 0), ("r" + i, "B", 0), ("s" + i, "C", 0) }, i, "", () => true, t0);
        Check(again.Count == CircleBook.MaxCircles, $"at most {CircleBook.MaxCircles} groups kept");
        again.Close();
        File.WriteAllText(path, "{ not json");
        var damaged = new CircleBook(path, _ => { });
        Check(damaged.Count == 0, "a damaged circles.json starts empty (moved aside, not thrown)");
        damaged.Close();

        // Recorded by a real conversation: three people who spoke.
        var ai = new ScriptedAi
        {
            TalkScript = "{\"lines\":[{\"speaker\":\"Ann Lee\",\"to\":\"\",\"say\":\"Busy night.\",\"leaving\":false},{\"speaker\":\"Ben Cole\",\"to\":\"\",\"say\":\"Always is.\",\"leaving\":false}," +
                         "{\"speaker\":\"Cal Ruiz\",\"to\":\"\",\"say\":\"Same again tomorrow.\",\"leaving\":false}],\"getAlong\":[{\"a\":\"Ann Lee\",\"b\":\"Ben Cole\",\"change\":4}]," +
                         "\"summary\":\"A busy night.\",\"heardAboutPlayer\":[],\"exits\":[]}",
        };
        var client = new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(ai));
        var store = new NpcStore(Path.Combine(dir, "circle-store"), _ => { });
        foreach (var (id, name) in new[] { ("p_ann", "Ann Lee"), ("p_ben", "Ben Cole"), ("p_cal", "Cal Ruiz") })
            store.Save(new NpcRecord { Id = id, Persona = new Persona { Name = name, Age = "40" }, Visual = new NpcVisual { Body = "woman" } });
        var brain = new NpcBrain(client, store, () => new BrainSettings { LifeUpdates = false, MeetingSummaries = false }, _ => { });
        var live = new CircleBook(Path.Combine(dir, "circles-live.json"));
        var chatter = new ChatterBrain(brain, client, new PairBook(Path.Combine(dir, "circle-pairs2.json")), _ => { }) { Circles = live };
        var members = new[] { "p_ann", "p_ben", "p_cal" }.Select(id => new ChatterMember { Id = id, Visual = new NpcVisual { Body = "woman" }, Snap = new GameSnapshot { GameDay = 7 }, MaxLines = 3, Seated = true }).ToList();
        var script = await chatter.WriteAsync(members, true, new List<string>(), new List<string>(), 2000, CancellationToken.None);
        if (script != null) await chatter.RememberAsync(script, script.Lines.Count);
        var got = live.Best(new[] { "p_ann", "p_ben", "p_cal" });
        Check(got != null && got.Times == 1 && got.LastTalk == "A busy night." && got.How["p_ann"] == 1 && got.How["p_cal"] == 0,
              "a conversation of three is kept as their group, with how it went for each (from the AI's own ratings)");
        live.Close();
    }
}
