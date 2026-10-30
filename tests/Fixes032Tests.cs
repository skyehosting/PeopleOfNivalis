using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NivalisAIChat.Ai;

namespace NivalisAIChat.Tests;

/// <summary>0.3.2: the fixes from the author's test of 0.3.1.</summary>
internal static partial class Program
{
    private static async Task Fixes032Tests()
    {
        string dir = TestRoot.NewFolder("fixes032");
        Directory.CreateDirectory(dir);
        MutterTests(dir);
        VenueRoleTests();
        SightTests();
        TableTests();
        TranscriptTests(dir);
        await CodeReview032Tests(dir);
        await Task.CompletedTask;
    }

    /// <summary>The 0.3.2 code review: the CORA trim's edges, export file selection, sharing the live file, rollover, seed rotation.</summary>
    private static async Task CodeReview032Tests(string dir)
    {
        Console.WriteLine("-- 0.3.2 code review fixes");
        Check(Npc.MutterSeeds.TrimLeadingCora("CORA\u2019s tears! Not again.") == "Not again." && Npc.MutterSeeds.TrimLeadingCora("thank CORA, made it.") == "Made it.",
              "a curly apostrophe (CORA\u2019s) and a lower-case 'thank' are trimmed too");
        Check(Npc.MutterSeeds.TrimLeadingCora("Cora, did I lock up?") == "Cora, did I lock up?", "someone called Cora isn't (it's case-sensitive)");

        // This session's files only, by the whole name (not every file of the year).
        string sdir = Path.Combine(dir, "sessions");
        Directory.CreateDirectory(sdir);
        foreach (var n in new[] { "2026-10-13_200000", "2026-10-13_200000-2", "2026-10-13_210000", "2026-10-12_200000" }) File.WriteAllText(Path.Combine(sdir, n + ".jsonl"), "{}");
        var mine = Npc.TranscriptLog.SessionFiles(sdir, "2026-10-13_200000").Select(Path.GetFileNameWithoutExtension).ToList();
        Check(mine.SequenceEqual(new[] { "2026-10-13_200000", "2026-10-13_200000-2" }), "an export takes this session's files: " + string.Join(", ", mine));

        // Over the size cap: the session goes on in a second file that starts with the same settings; read while written, nothing is lost.
        string ldir = Path.Combine(dir, "live");
        var log = new Npc.TranscriptLog(ldir, _ => { }, new DateTime(2026, 10, 13, 22, 0, 0), maxBytes: 1024);
        log.Header(new System.Text.Json.Nodes.JsonObject { ["chat model"] = "x" });
        string first = log.CurrentFile;
        for (int i = 0; i < 20; i++) log.Line(new System.Text.Json.Nodes.JsonObject { ["kind"] = "chat", ["text"] = "line " + i + new string('.', 60) });
        await Task.Delay(400);
        using (var reader = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) // an export reading it meanwhile
        {
            for (int i = 20; i < 40; i++) log.Line(new System.Text.Json.Nodes.JsonObject { ["kind"] = "chat", ["text"] = "line " + i + new string('.', 60) });
            await Task.Delay(400);
        }
        log.Close();
        var parts = Npc.TranscriptLog.SessionFiles(ldir, log.Stamp);
        int kept = parts.Sum(f => File.ReadAllLines(f).Count(l => l.Contains("\"type\":\"line\"")));
        // How many files it takes depends on how the writer batched the lines (each file past the cap starts the next), so:
        // more than one, every one after the first starting with the settings, and every line kept (a timing flake, 2026-10-13).
        Check(parts.Count >= 2 && parts.Skip(1).All(p => File.ReadAllLines(p)[0].Contains("\"type\":\"session\"")) && kept == 40,
              $"past the cap a second file starts with the settings again; while it was being read nothing was lost ({parts.Count} files, {kept} of 40 lines)");

        // Each mutter is about something else than the last few (through the real writer).
        string pdir = Path.Combine(dir, "mutter-npcs");
        var store = new Npc.NpcStore(pdir, _ => { });
        store.Create(new Npc.NpcRecord
        {
            Id = "p_mut", Visual = new Npc.NpcVisual { Body = "woman" },
            Persona = new Npc.Persona { Name = "Dana Holt", Age = "30", Occupation = "cook", ShortTermGoal = "fix the sink", CurrentWorry = "rent",
                                        People = { "Teo - brother", "Mira - friend" }, Likes = { "noodles" } },
        });
        var ep = new FakeEndpoint { Respond = _ => (200, AnthropicText("{\"lines\":[\"Hm.\"],\"about\":\"x\",\"tones\":[\"\"]}")) };
        var client = new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new System.Net.Http.HttpMessageInvoker(ep));
        var brain = new Npc.NpcBrain(client, store, () => new Npc.BrainSettings(), _ => { });
        var chatter = new Npc.ChatterBrain(brain, client, new Social.PairBook(Path.Combine(dir, "mutter-pairs.json")), _ => { });
        var seeds = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            await chatter.ThinkAsync("p_mut", new Npc.NpcVisual { Body = "woman" }, new Game.GameSnapshot(), System.Threading.CancellationToken.None);
            var text = Texts(ep.Requests.Last().body).FirstOrDefault(t => t.Contains("What's on their mind this time:")) ?? "";
            int at = text.IndexOf("What's on their mind this time:", StringComparison.Ordinal);
            seeds.Add(at < 0 ? "" : text[at..].Split('\n')[0]);
        }
        Check(seeds.All(x => x.Length > 0) && seeds.Distinct().Count() == 3, "three mutters in a row are about three different things: " + string.Join(" | ", seeds));
    }

    /// <summary>Every string inside a JSON request body.</summary>
    private static IEnumerable<string> Texts(System.Text.Json.Nodes.JsonNode? n)
    {
        switch (n)
        {
            case System.Text.Json.Nodes.JsonValue v when v.TryGetValue<string>(out var s): yield return s; break;
            case System.Text.Json.Nodes.JsonArray a: foreach (var x in a) foreach (var t in Texts(x)) yield return t; break;
            case System.Text.Json.Nodes.JsonObject o: foreach (var kv in o) foreach (var t in Texts(kv.Value)) yield return t; break;
        }
    }

    /// <summary>FB-18: a table is a group: grouped by table, everyone in it, nobody leaves, the next part carries on.</summary>
    private static void TableTests()
    {
        Console.WriteLine("-- 0.3.2 FB-18: tables talk as one group");
        // Four people in a row 1.5 m apart, no table ids: links would chain them into one; everyone-within-reach makes two tables.
        var row = new List<(float, float, string)> { (0f, 0f, ""), (1.5f, 0f, ""), (3f, 0f, ""), (4.5f, 0f, "") };
        var g = Social.ChatterPlan.Tables(row, Social.ChatterPlan.TableRadius);
        Check(g.Count == 2 && g.All(x => x.Count == 2), $"neighbouring tables aren't merged by a chain of people ({string.Join(" | ", g.Select(x => string.Join(",", x)))})");
        var byId = Social.ChatterPlan.Tables(new List<(float, float, string)> { (0f, 0f, "T1"), (5f, 0f, "T1"), (0.5f, 0f, "T2"), (9f, 9f, "") }, 2.6f);
        Check(byId.Any(x => x.SequenceEqual(new[] { 0, 1 })) && byId.Any(x => x.SequenceEqual(new[] { 2 })) && byId.Any(x => x.SequenceEqual(new[] { 3 })),
              "the game's own table decides when it's known (even a wide table), whatever the distance");

        var ana = new Npc.NpcRecord { Id = "p_ana", Persona = new Npc.Persona { Name = "Ana Reyes" } };
        var bo = new Npc.NpcRecord { Id = "p_bo", Persona = new Npc.Persona { Name = "Bo Lin" } };
        var cy = new Npc.NpcRecord { Id = "p_cy", Persona = new Npc.Persona { Name = "Cy Park" } };
        var people = new List<ChatterPrompts.Person> { new() { Record = ana, MaxLines = 3 }, new() { Record = bo, MaxLines = 3 }, new() { Record = cy, MaxLines = 3, JustSat = true } };
        string plan = ChatterPrompts.Plan(people, true);
        Check(plan.Contains("Ana Reyes, Bo Lin and Cy Park are sitting at the same table") && !plan.Contains("comes over and joins") && !plan.Contains("natural goodbye")
              && plan.Contains("nobody says goodbye or leaves") && plan.Contains("Cy Park has just sat down with them; the others say hi"),
              "everyone at the table is in it from the start; a newcomer is greeted; nobody says goodbye");
        string next = ChatterPrompts.Plan(people.Take(2).ToList(), true, carryOn: "the late trawler.");
        Check(next.Contains("They were just talking (the late trawler) and carry on from there: no hellos.") && !next.Contains("Small talk across the table"),
              "the next part carries on from the last one, no hellos");

        string json = "{\"lines\":[{\"speaker\":\"Ana\",\"to\":\"Bo\",\"say\":\"Long day.\",\"leaving\":false},{\"speaker\":\"Bo\",\"to\":\"Ana\",\"say\":\"Right, see you.\",\"leaving\":true}," +
                      "{\"speaker\":\"Ana\",\"to\":\"Bo\",\"say\":\"Bye then.\",\"leaving\":true}],\"summary\":\"x\"," +
                      "\"getAlong\":[{\"a\":\"Ana\",\"b\":\"Bo\",\"change\":3}]}";
        var two = people.Take(2).ToList();
        var atTable = Npc.ChatterBrain.ParseScript(json, two, stay: true)!;
        var standing = Npc.ChatterBrain.ParseScript(json, two)!;
        Check(atTable.Lines.Count == 3 && !atTable.Lines.Any(l => l.Leaving) && standing.Lines.Any(l => l.Leaving), "at a table no line is a goodbye (standing, they still leave)");
        atTable.Raw = json;
        atTable.Persons = two;
        Check(Npc.ChatterBrain.HowItWent(atTable, "p_ana") == 1 && Npc.ChatterBrain.HowItWent(atTable, "p_bo") == 1, "how it went for them, from the AI's own get-along");
        atTable.Raw = json.Replace("\"change\":3", "\"change\":-4");
        Check(Npc.ChatterBrain.HowItWent(atTable, "p_ana") == -1, "...and when it went badly");
        atTable.Raw = "{}";
        atTable.Lines[^1].Tone = new Voice.ToneState { Valence = 0.5f };
        Check(Npc.ChatterBrain.HowItWent(atTable, "p_ana") == 1 && Npc.ChatterBrain.HowItWent(atTable, "p_bo") == 0, "no get-along: the tone of their last line decides");
    }

    /// <summary>FB-30: transcripts: a header, each person once, every line (keys hidden), an export to read, rotation.</summary>
    private static void TranscriptTests(string dir)
    {
        Console.WriteLine("-- 0.3.2 FB-30: transcripts");
        string tdir = Path.Combine(dir, "transcripts");
        var warned = new List<string>();
        var log = new Npc.TranscriptLog(tdir, warned.Add, new DateTime(2026, 10, 13, 20, 0, 0));
        log.Header(new System.Text.Json.Nodes.JsonObject { ["chat model"] = "gpt-5.4-nano", ["instructions"] = new System.Text.Json.Nodes.JsonObject { ["TheCity"] = "default" } });
        int described = 0;
        System.Text.Json.Nodes.JsonObject Who() { described++; return new System.Text.Json.Nodes.JsonObject { ["name"] = "Ana Reyes", ["speech"] = "slang 1/3" }; }
        log.Person("p_ana", Who);
        log.Person("p_ana", Who);
        log.Line(new System.Text.Json.Nodes.JsonObject { ["kind"] = "chat", ["group"] = "chat:p_ana:1", ["speaker"] = "You", ["to"] = "Ana Reyes", ["text"] = "my key is sk-abcdefghijklmnopqrstuv, lol" });
        log.Line(new System.Text.Json.Nodes.JsonObject { ["kind"] = "chat", ["group"] = "chat:p_ana:1", ["speaker"] = "Ana Reyes", ["to"] = "you", ["text"] = "Don't tell me that.", ["mood"] = "mild amused", ["voice"] = "OpenAI gpt-4o-mini-tts coral, 1.2 s" });
        string file = log.CurrentFile;
        log.Close();
        log.Line(new System.Text.Json.Nodes.JsonObject { ["kind"] = "chat", ["text"] = "after close" });
        var lines = File.ReadAllLines(file);
        Check(lines.Length == 4 && lines[0].Contains("\"type\":\"session\"") && lines[1].Contains("\"type\":\"person\"") && described == 1,
              "one file: the session's settings first, each person described once, then the lines");
        string all = File.ReadAllText(file);
        Check(!all.Contains("sk-abcdefghijklmnopqrstuv") && !all.Contains("after close"), "a key in what was typed is hidden; nothing is written after it closes");
        string md = Npc.TranscriptLog.Export(new[] { file }, new DateTime(2026, 10, 13, 21, 0, 0));
        Check(md.Contains("## Session 2026-10-13_200000") && md.Contains("chat model: gpt-5.4-nano") && md.Contains("TheCity: default") && md.Contains("Ana Reyes")
              && md.Contains("**Ana Reyes** → you: Don't tell me that.") && md.Contains("mood: mild amused") && md.Contains("voice: OpenAI gpt-4o-mini-tts coral"),
              "the export reads as settings, people, then each line with what shaped it");
        string rdir = Path.Combine(dir, "rotate");
        Directory.CreateDirectory(rdir);
        for (int i = 0; i < 31; i++)
        {
            string f = Path.Combine(rdir, $"s{i:00}.jsonl");
            File.WriteAllText(f, "{}");
            File.SetLastWriteTimeUtc(f, new DateTime(2026, 1, 1).AddMinutes(i));
        }
        File.WriteAllText(Path.Combine(rdir, "export-x.md"), "keep");
        Npc.TranscriptLog.Rotate(rdir, Npc.TranscriptLog.KeepFiles);
        Check(Directory.GetFiles(rdir, "*.jsonl").Length == Npc.TranscriptLog.KeepFiles - 1 && !File.Exists(Path.Combine(rdir, "s00.jsonl")) && File.Exists(Path.Combine(rdir, "export-x.md")),
              "the oldest sessions go so 30 are kept with the new one; exports stay");
    }

    /// <summary>FB-29: the sight triangle, as the author drew it (two lines SightDistance long, SightAngle apart).</summary>
    private static void SightTests()
    {
        Console.WriteLine("-- 0.3.2 FB-29: people see you inside their triangle");
        var eye = (0f, 0f);
        var north = (0f, 1f);
        bool S(float x, float z, float dist = 10f, float angle = 120f) => Social.Sight.Sees(eye, north, (x, z), dist, angle);
        Check(S(0, 4) && S(3, 4) && S(-3, 4.9f), "in front of them, inside the triangle: seen");
        Check(!S(0, 6) && !S(4, 6), "past the edge between the two lines' ends (5 m straight ahead at 10 m and 120): not seen");
        Check(!S(5, 1.8f) && !S(-5, 1.8f) && !S(0, -2), "outside the two lines (70 off to a side), or behind them: not seen");
        Check(S(0, 9, 10f, 30f) && !S(3, 9, 10f, 30f), "a narrow angle reaches farther ahead, less to the sides");
        Check(!Social.Sight.Sees(eye, (0f, 0f), (0f, 1f), 10f, 120f) && !S(0, 1, 0f) && !S(0, 1, 10f, 0f), "no facing, no distance or no angle: sees nothing");
        var (l, r) = Social.Sight.Corners(eye, north, 10f, 120f);
        float Len((float x, float z) p) => MathF.Sqrt(p.x * p.x + p.z * p.z);
        Check(MathF.Abs(Len(l) - 10f) < 0.01f && MathF.Abs(Len(r) - 10f) < 0.01f && MathF.Abs(l.z - 5f) < 0.01f && MathF.Abs(r.z - 5f) < 0.01f
              && l.x < -8.6f && r.x > 8.6f && S(l.x * 0.99f, l.z * 0.99f),
              $"each line is SightDistance long, 60 to each side (ends at {l.x:0.00},{l.z:0.00} and {r.x:0.00},{r.z:0.00})");
        Check(Social.Sight.Sees(eye, north, (0f, 0.4f), 10f, 360f) && !Social.Sight.Sees(eye, north, (0f, -1f), 10f, 360f),
              "an angle past 170 is held at 170 (a triangle needs depth)");
    }

    /// <summary>FB-21: who's who at a food place, from the game's own task (the waiter at a table takes the order).</summary>
    private static void VenueRoleTests()
    {
        Console.WriteLine("-- 0.3.2 FB-21: order talk knows waiters from customers");
        Social.VenueRoles.Role R(string task, string action, bool worker, bool seated) => Social.VenueRoles.Classify(task, action, worker, seated);
        Check(R("AIAction_TakeOrder", "TalkOrder", true, false) == Social.VenueRoles.Role.StaffTakingOrder
              && R("AIAction_TakeOrder", "TalkOrder", false, false) == Social.VenueRoles.Role.StaffTakingOrder,
              "a waiter taking an order at a table (the game's TakeOrder is a worker's task), also known by its name alone");
        Check(R("AIAction_VisitVenue", "TalkOrder", false, false) == Social.VenueRoles.Role.CustomerOrdering, "a customer ordering at a counter is still a customer");
        Check(R("AIAction_VisitVenue", "SitChair", false, true) == Social.VenueRoles.Role.CustomerWaiting
              && R("AIAction_VisitVenue", "Eat", false, true) == Social.VenueRoles.Role.CustomerEating
              && R("AIAction_VisitVenue", "Drink", false, true) == Social.VenueRoles.Role.CustomerEating,
              "a seated customer waiting, and one eating or drinking");
        Check(R("AIAction_ServeMeal", "Walk", true, false) == Social.VenueRoles.Role.StaffServing
              && R("", "IdleStaff", false, false) == Social.VenueRoles.Role.StaffFree
              && R("AIAction_IdleAtWorkplace", "IdleCrossedArms", true, false) == Social.VenueRoles.Role.StaffFree
              && R("AIAction_MakeMeal", "CookerLoop", true, false) == Social.VenueRoles.Role.StaffBusy,
              "staff serving, free (behind the counter or idle at work) and busy (cooking)");
        Check(R("AIAction_Wander", "WalkWander", false, false) == Social.VenueRoles.Role.Other
              && R("AIAction_Schedule_Idle", "IdleCrossedArms", false, false) == Social.VenueRoles.Role.Other
              && R("AIAction_VisitVenue", "Eat", false, false) == Social.VenueRoles.Role.CustomerWaiting,
              "a passer-by isn't part of it, and eating standing up isn't a seated diner");
        var rng = new Random(3);
        var (give, thanks) = Social.VenueRoles.ServeLines("Noodle Soup", rng);
        var (plain, _) = Social.VenueRoles.ServeLines("", rng);
        Check(give.Contains("Noodle Soup") && thanks.Length > 0 && !plain.Contains("{0}") && plain.Length > 0, $"bringing a meal names the dish when it's known ({give} / {thanks}; {plain})");
    }

    /// <summary>FB-33 + FB-02: a mutter is a few words about one thing from their own life; CORA only now and then.</summary>
    private static void MutterTests(string dir)
    {
        Console.WriteLine("-- 0.3.2 FB-33, FB-02: mutters from their own life, CORA now and then");
        var rec = new Npc.NpcRecord { Id = "p_mutter", Persona = new Npc.Persona
        {
            Name = "Kezia Sefadu", Occupation = "ledger clerk", Workplace = "Curzona annex", ShortTermGoal = "pay the water bill",
            CurrentWorry = "the late fee", People = { "Teo — brother — on the rig", "Mira — neighbour" }, Likes = { "hot noodles" },
            Faith = "devout CORA believer, prays at the shrine",
        } };
        var snap = new Game.GameSnapshot { NpcActivity = "waiting for a cab" };
        var all = Npc.MutterSeeds.Candidates(rec, snap);
        Check(all.Any(c => c.seed.Key == "person:Teo" && c.seed.Text.Contains("brother")) && all.Any(c => c.seed.Key == "goal") && all.Any(c => c.seed.Key == "worry")
              && all.Any(c => c.seed.Key == "job" && c.seed.Text.Contains("ledger clerk at Curzona annex")) && all.Any(c => c.seed.Key == "now") && all.Any(c => c.seed.Key == "like:hot noodles"),
              "what they could mutter about comes from their own life: people, plans, worries, job, what they're doing, likes");
        var rng = new Random(7);
        var recent = all.Select(c => c.seed.Key).Where(k => k != "goal").ToList();
        Check(Enumerable.Range(0, 20).All(_ => Npc.MutterSeeds.Pick(rec, snap, rng, recent)!.Key == "goal"), "the last few seeds aren't picked again");
        Check(Npc.MutterSeeds.Pick(rec, snap, rng, all.Select(c => c.seed.Key).ToList()) != null, "...unless every one is recent: then any");
        Check(Npc.MutterSeeds.Pick(new Npc.NpcRecord(), new Game.GameSnapshot(), rng) == null, "nothing to go on: no seed (the writer gets none)");

        Check(Npc.MutterSeeds.Believer(rec.Persona)
              && !Npc.MutterSeeds.Believer(new Npc.Persona { Faith = "thinks CORA is a corporate psy-op" })
              && !Npc.MutterSeeds.Believer(new Npc.Persona { Faith = "lapsed — raised on CORA, gave it up" })
              && !Npc.MutterSeeds.Believer(new Npc.Persona { Faith = "was raised on CORA, rarely thinks about it" })
              && !Npc.MutterSeeds.Believer(new Npc.Persona { Faith = "doesn't think about religion much" }),
              "only believers would say CORA at all");

        Check(Npc.MutterSeeds.TrimLeadingCora("CORA, it’s back already… I can smell it.") == "It’s back already… I can smell it."
              && Npc.MutterSeeds.TrimLeadingCora("CORA… ledger’s blinking again.") == "Ledger’s blinking again."
              && Npc.MutterSeeds.TrimLeadingCora("CORA, ledger—don’t watch me.") == "Ledger—don’t watch me."
              && Npc.MutterSeeds.TrimLeadingCora("Thank CORA, made it.") == "Made it."
              && Npc.MutterSeeds.TrimLeadingCora("CORA's tears! Not again.") == "Not again."
              && Npc.MutterSeeds.TrimLeadingCora("CORA...") == "",
              "a line opening with CORA as an exclamation loses it (and one that's only that is dropped)");
        Check(Npc.MutterSeeds.TrimLeadingCora("No, CORA, don't stutter now.") == "No, CORA, don't stutter now." && Npc.MutterSeeds.TrimLeadingCora("Coral reef soup again.") == "Coral reef soup again.",
              "CORA later in a line, or a word that only starts like it, stays");

        var seed = new Npc.MutterSeeds.Seed("person:Teo", "someone in their life: Teo (brother, on the rig)");
        string ask = ChatterPrompts.ThinkRequest(rec, snap, 2, seed, cora: false).Messages[0].Content;
        string allowed = ChatterPrompts.ThinkRequest(rec, snap, 1, seed, cora: true).Messages[0].Content;
        Check(ask.Contains("What's on their mind this time: someone in their life: Teo (brother, on the rig).") && ask.Contains("3 to 10 words")
              && ask.Contains("I need to feed my cat when I get home.") && ask.Contains("Don't say CORA.") && ask.Contains("Don't open with an exclamation")
              && ask.Contains("1 to 2 lines") && ask.Contains("just a few words"),
              "the writer is told the one thing it's about, a few words (a small question or statement), no exclamation, no CORA");
        Check(allowed.Contains("They may say CORA once") && !allowed.Contains("Don't say CORA."), "...or that CORA's allowed this time");
        Check(string.Join("\n", ChatterPrompts.ThinkRequest(rec, snap, 1).System).Contains("Did I lock the back door?"), "the mutter rules give the small question and statement");

        // An untouched 0.3.1 instructions file gets the new mutter wording; a changed one keeps the player's.
        var field = CustomInstructions.Fields.First(f => f.Name == CustomInstructions.ThinkingOutLoud);
        string old031 = field.Previous[^1];
        string file = CustomInstructions.FieldsSection(CustomInstructions.Fields.Select(f => f.Name == field.Name
            ? new CustomInstructions.Field { Name = f.Name, About = f.About, Default = old031 } : f), header: true);
        string d1 = Path.Combine(dir, "instr031"), d2 = Path.Combine(dir, "instr031-mine");
        Directory.CreateDirectory(d1);
        Directory.CreateDirectory(d2);
        File.WriteAllText(Path.Combine(d1, CustomInstructions.FileName), file);
        File.WriteAllText(Path.Combine(d2, CustomInstructions.FileName), file.Replace(old031, old031 + "\n- Mutter in rhymes."));
        CustomInstructions.Init(d1);
        bool upgraded = CustomInstructions.Get(CustomInstructions.ThinkingOutLoud) == field.Default && File.ReadAllText(Path.Combine(d1, CustomInstructions.FileName)).Contains(field.Default);
        CustomInstructions.Init(d2);
        bool kept = CustomInstructions.Get(CustomInstructions.ThinkingOutLoud).EndsWith("- Mutter in rhymes.");
        CustomInstructions.Init(Path.Combine(dir, "instr-none"));
        Check(upgraded && kept, "an untouched 0.3.1 mutter text is brought up to date; one the player changed is left alone");
    }
}
