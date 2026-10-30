using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Voice;

namespace NivalisAIChat.Tests;

/// <summary>0.3.1: the fixes from the 0.3.0 test and the feedback file.</summary>
internal static partial class Program
{
    /// <summary>Every speech request answered with a little audio; counts what was asked.</summary>
    private sealed class CountingSpeech : HttpMessageHandler
    {
        public int Speech;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.Contains("speech")) Interlocked.Increment(ref Speech);
            // OpenAI itself sends raw PCM; a server on this PC (OpenAI-compatible) sends WAV.
            byte[] body = request.RequestUri.IsLoopback ? Wav(2400) : new byte[4800];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    /// <summary>A silent 16-bit mono WAV of <paramref name="samples"/> samples at 24 kHz.</summary>
    private static byte[] Wav(int samples)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        int data = samples * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + data); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(24000); w.Write(48000); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(data); w.Write(new byte[data]);
        return ms.ToArray();
    }

    private static async Task Fixes031Tests()
    {
        string dir = TestRoot.NewFolder("fixes031");
        Directory.CreateDirectory(dir);
        await VoiceLimitTests(dir);
        await ReasoningEffortTests();
        ChangeLabelTests();
        ConsoleTextTests();
        CompanyTests();
        InstructionsUpgradeTests(dir);
        QuestionNoteTests();
        ThoughtToneTests();
        SettingsRefreshTests();
        await OverhearingTests(dir);
        GestureTests();
        TableCallTests();
        await Batch2ReviewTests();
    }

    /// <summary>FB-19: a phone call at a table, read as the code decided it, and the far end through a phone speaker.</summary>
    private static void TableCallTests()
    {
        Console.WriteLine("-- FB-19: a phone call at a table");
        // The speakerphone filter: the telephone band passes, rumble and hiss don't, nothing louder than its level.
        float[] Sine(double hz) => Enumerable.Range(0, 24000).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / 24000))).ToArray();
        double Rms(float[] s) => Math.Sqrt(s.Skip(2400).Select(x => (double)x * x).Average()); // after the filters settle
        double Pass(double hz) { var x = Sine(hz); return Rms(Voice.PhoneSpeaker.Filter(new Voice.PcmAudio { Samples = x, SampleRate = 24000 }).Samples) / Rms(x); }
        Check(Pass(1000) > 0.6 && Pass(80) < 0.25 && Pass(9000) < 0.25, $"through the phone: 1 kHz passes ({Pass(1000):0.00}), 80 Hz ({Pass(80):0.00}) and 9 kHz ({Pass(9000):0.00}) don't");
        var loud = Voice.PhoneSpeaker.Filter(new Voice.PcmAudio { Samples = Sine(1000).Select(x => x * 2f).ToArray(), SampleRate = 24000 });
        Check(loud.Samples.Max(Math.Abs) <= Voice.PhoneSpeaker.Level / Voice.PhoneSpeaker.Drive + 1e-6, "a loud voice is softly held under the speaker's level");

        var ana = new Npc.NpcRecord { Id = "p_ana", Persona = new Npc.Persona { Name = "Ana Reyes" } };
        var bo = new Npc.NpcRecord { Id = "p_bo", Persona = new Npc.Persona { Name = "Bo Lin" } };
        var people = new System.Collections.Generic.List<ChatterPrompts.Person> { new() { Record = ana, MaxLines = 4 }, new() { Record = bo, MaxLines = 4 } };
        var callee = new ChatterPrompts.Callee { Name = "Mira Sol", Relation = "sister" };
        var call = new ChatterPrompts.TableCall { HolderId = "p_ana", HolderName = "Ana Reyes", Callee = callee, Answered = true, Speaker = false };
        string json = "{\"lines\":[" +
            "{\"speaker\":\"Ana\",\"to\":\"Bo\",\"say\":\"Long day.\",\"tone\":\"tired\",\"gesture\":\"\",\"leaving\":false,\"phone\":false,\"speakerphone\":false}," +
            "{\"speaker\":\"Ana\",\"to\":\"\",\"say\":\"One sec. Hey, Mira.\",\"tone\":\"\",\"gesture\":\"\",\"leaving\":false,\"phone\":true,\"speakerphone\":false}," +
            "{\"speaker\":\"Mira Sol\",\"to\":\"\",\"say\":\"Are you coming tonight?\",\"tone\":\"\",\"gesture\":\"\",\"leaving\":false,\"phone\":false,\"speakerphone\":true}," +
            "{\"speaker\":\"Ana\",\"to\":\"\",\"say\":\"Talk later, okay?\",\"tone\":\"\",\"gesture\":\"\",\"leaving\":true,\"phone\":true,\"speakerphone\":false}," +
            "{\"speaker\":\"Bo\",\"to\":\"Ana\",\"say\":\"Who was that?\",\"tone\":\"\",\"gesture\":\"\",\"leaving\":false,\"phone\":false,\"speakerphone\":false}],\"summary\":\"x\"}";
        var script = Npc.ChatterBrain.ParseTableCall(json, people, call)!;
        Check(script != null && script.Lines.Count == 5 && script.Lines[2].FarEnd && script.Lines[2].SpeakerId == Npc.ChatterScript.FarEndId && script.Lines[1].Phone,
              "the caller's lines are the far end's; lines into the phone are marked");
        Check(!script!.Lines.Any(l => l.Leaving), "'talk later' to the caller doesn't walk anyone off: nobody leaves the table");
        Check(!script.Lines[2].Speakerphone, "not allowed on speaker (not everyone knows the caller): the far end isn't heard aloud");
        call.Speaker = true;
        Check(Npc.ChatterBrain.ParseTableCall(json, people, call)!.Lines[2].Speakerphone, "allowed: the far end is heard on speaker");
        call.Answered = false;
        var unanswered = Npc.ChatterBrain.ParseTableCall(json, people, call)!;
        Check(!unanswered.Lines.Any(l => l.FarEnd || l.Phone), "not answered: nothing is said on the phone");

        call.Answered = true;
        var req = ChatterPrompts.ChatterRequest(people, (_, _) => null, true, new string[0], new string[0], 2000, call);
        var items = req.JsonSchema!["properties"]!["lines"]!["items"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Check(req.Messages[0].Content.Contains("[A PHONE CALL]") && req.Messages[0].Content.Contains("Mira Sol") && items.Contains("phone") && items.Contains("speakerphone"),
              "the writer is told about the call, and each line says if it's into the phone or on speaker");
        var plain = ChatterPrompts.ChatterRequest(people, (_, _) => null, true, new string[0], new string[0], 2000);
        Check(!plain.Messages[0].Content.Contains("[A PHONE CALL]") && !plain.JsonSchema!["properties"]!["lines"]!["items"]!["required"]!.AsArray().Any(n => n!.GetValue<string>() == "phone"),
              "an ordinary conversation is asked as before");
    }

    /// <summary>CF-04b: the AI cues a gesture with a line, from a fixed list of the game's own animations.</summary>
    private static void GestureTests()
    {
        Console.WriteLine("-- CF-04b: gestures the AI can cue");
        Check(Npc.GestureLibrary.Normalize(" Nod. ") == "nod" && Npc.GestureLibrary.Normalize("shake_head") == "shake head"
              && Npc.GestureLibrary.Normalize("backflip") == "" && Npc.GestureLibrary.Normalize(null) == "",
              "a name the AI writes is read as one of the list, or none");
        Check(Npc.GestureLibrary.Names.All(n => (Npc.GestureLibrary.Trigger(n) != null) ^ (Npc.GestureLibrary.Mood(n) != null)),
              "every gesture is exactly one of: a one-off (animator trigger) or a way of talking (talking mood)");
        var moods = new[] { "Neutral", "Sad", "Drunk", "Argue", "Laugh", "Bored", "Interested", "Explain", "Flirting", "Shocked" };
        var triggers = new[] { "Greeting", "Goodbye", "Nod", "Deny", "FingerPoint", "LookDown", "ShakeRain", "Sneeze", "Celebration" };
        Check(Npc.GestureLibrary.Names.All(n => Npc.GestureLibrary.Mood(n) is not { } m || moods.Contains(m))
              && Npc.GestureLibrary.Names.All(n => Npc.GestureLibrary.Trigger(n) is not { } t || triggers.Contains(t)),
              "each names one of the game's own moods or triggers (as the game's code spells them)");
        var structured = ReplyParser.ParseStructured("{\"say\":\"Sure.\",\"gesture\":\"nod\"}", "Ana", "");
        var hidden = ReplyParser.Parse("Ha, no way.\n[[gesture: laugh]]", "Ana", "");
        Check(structured?.Gesture == "nod" && hidden.Gesture == "laugh" && hidden.Spoken == "Ha, no way.", "read from both answer forms, never spoken");
        Check(Prompts.ReplySchema()["required"]!.AsArray().Any(n => n!.GetValue<string>() == "gesture"), "the reply schema asks for it (strict: required)");
    }

    /// <summary>FB-05: overhearing, kept apart from conversations, never about the player, passed on more freely to a friend.</summary>
    private static async Task OverhearingTests(string dir)
    {
        Console.WriteLine("-- FB-05: overhearing");
        var names = new[] { "Mira Vale", "" };
        var places = new[] { "Ramen Noir" };
        Check(Social.ChatterPlan.AboutThePlayer("Did you see Mira at the market?", names, places)
              && Social.ChatterPlan.AboutThePlayer("The ramen noir place was packed", names, places)
              && Social.ChatterPlan.AboutThePlayer("Some stranger kept asking questions.", names, places)
              && Social.ChatterPlan.AboutThePlayer("The new guy at the stall?", names, places),
              "a line naming the player, one of their places, or 'the stranger' / 'the new guy' isn't kept");
        Check(!Social.ChatterPlan.AboutThePlayer("Rent's up again, can you believe it?", names, places)
              && !Social.ChatterPlan.AboutThePlayer("Miraculous, honestly.", names, places),
              "an ordinary line is (and a word that only starts with their name doesn't count)");

        // Overheard snippets have their own cap: they never push out conversations.
        var rec = new Npc.NpcRecord { Id = "p_ear", Persona = new Npc.Persona { Name = "Dana Holt" } };
        for (int i = 0; i < Npc.NpcRecord.MaxTalks; i++) rec.AddTalk(new Npc.Talk { Kind = Npc.Talk.InPerson, With = "Mo", Summary = "talk " + i, GameDay = i });
        for (int i = 0; i < 8; i++) rec.AddTalk(new Npc.Talk { Kind = Npc.Talk.Overheard, Summary = "heard " + i, Lines = { "Someone they don't know: line " + i }, GameDay = 40 });
        Check(rec.Talks.Count(t => t.Kind == Npc.Talk.Overheard) == Npc.NpcRecord.MaxOverheard && rec.Talks.Count(t => t.Kind == Npc.Talk.InPerson) == Npc.NpcRecord.MaxTalks
              && rec.Talks.Last().Summary == "heard 7" && rec.Talks.First(t => t.Kind == Npc.Talk.InPerson).Summary == "talk 0",
              "overheard snippets keep their own 5 (oldest go), and all 30 conversations stay");

        // Memories and recall read it as overheard, not as a conversation with someone called "Someone".
        var memories = Npc.Memories.From(rec);
        Check(memories.Any(m => m.Text.StartsWith("You overheard people nearby")) && !memories.Any(m => m.Text.Contains("Talking with  ")),
              "memory search indexes it as something overheard");
        Check(!Npc.Recall.AboutThePast("someone said hi", rec), "'someone' isn't taken for a name they know");

        // In a chat: what they overheard, and how freely they'd pass it on.
        var snap = new Game.GameSnapshot { GameDay = 41, Location = "Meridian Market" };
        string stranger = Prompts.SituationBlock(rec, snap);
        rec.State.Familiarity = "friend";
        string friend = Prompts.SituationBlock(rec, snap);
        Check(stranger.Contains("[OVERHEARD LATELY]") && stranger.Contains("only with a real reason") && friend.Contains("you may pass one on") && friend.Contains("line 7"),
              "the chat knows what they overheard: kept to themselves with a stranger, passed on more freely to a friend");
        snap.GameDay = 60;
        Check(!Prompts.SituationBlock(rec, snap).Contains("[OVERHEARD LATELY]"), "after a week it's no longer on their mind");

        // Saved through the brain: only for someone with a record, not after a reset, never for a child.
        var store = new Npc.NpcStore(Path.Combine(dir, "overhear-npcs"), _ => { });
        var listener = new Npc.NpcRecord { Id = "p_listener", Persona = new Npc.Persona { Name = "Kai Ito", Age = "34" }, Visual = new Npc.NpcVisual { Body = "man" } };
        store.Create(listener);
        var ep = new FakeEndpoint();
        var client = new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(ep));
        var brain = new Npc.NpcBrain(client, store, () => new Npc.BrainSettings(), _ => { });
        var pairs = new Social.PairBook(Path.Combine(dir, "overhear-pairs.json"));
        var chatter = new Npc.ChatterBrain(brain, client, pairs, _ => { });
        var place = new Game.GameSnapshot { GameDay = 9, Hour = 14, Location = "Docks" };
        var heard = new System.Collections.Generic.List<(string, string, string)> { ("p_speaker", "Lena Morr", "The trawler's late again."), ("p_speaker", "Lena Morr", "Third time this week.") };
        await chatter.RememberOverheardAsync("p_listener", brain.EpochFor("p_listener"), place, heard);
        var kept = store.Load("p_listener")!.Talks.SingleOrDefault(t => t.Kind == Npc.Talk.Overheard);
        Check(kept != null && kept.With == "" && kept.Lines.Count == 2 && kept.Lines[0].StartsWith("Someone they don't know: ") && kept.Where == "Docks",
              "kept with their talks: who said it is 'someone they don't know' when they don't know them");
        await chatter.RememberOverheardAsync("p_nobody", brain.EpochFor("p_nobody"), place, heard);
        Check(store.Load("p_nobody") == null, "nobody is created to remember it");
        long before = brain.EpochFor("p_listener");
        brain.NewEpoch("p_listener"); // a reset of them meanwhile
        await chatter.RememberOverheardAsync("p_listener", before, place, heard);
        Check(store.Load("p_listener")?.Talks.Count(t => t.Kind == Npc.Talk.Overheard) == 1, "not after a reset since they heard it (still just the first one)");
    }

    /// <summary>FB-23: an update brings the settings file's own explanations up to date and nothing else.</summary>
    private static void SettingsRefreshTests()
    {
        Console.WriteLine("-- 0.3.1 FB-23: the settings file's explanations brought up to date");
        string nl = "\r\n";
        string file = string.Join(nl, new[]
        {
            "# OLD HEADER", "#", "",
            "# old banner", "[Group]", "",
            "# old desc", "# Default: 3", "Key = 5", "",
            "# my own note", "# old desc", "Other = test-key-X", "",
            "Gone = 1", "",
            "[Voices]", "", "# old list desc", "Providers = [", "#   (windows, , 50, auto),", "    (https://api.openai.com/v1, test-voice-X, 100, auto),", "]", "",
        });
        var now = new NivalisAIChat.SettingsFileText.Template { Header = new() { "# NEW HEADER", "#" } };
        now.Banners["Group"] = new() { "# new banner" };
        now.Blocks[("Group", "Key")] = new() { "# new desc", "# Default: 4" };
        now.Blocks[("Group", "Other")] = new() { "# new other" };
        now.Retired.Add(("Group", "Gone"));
        var old = new System.Collections.Generic.HashSet<string> { "HEADER|# OLD HEADER\n#", "[Group]|# old banner", "Group|Key|# old desc", "Group|Other|# old desc" };
        bool Wrote(string kind, string block) => old.Contains(kind + "|" + block);
        string next = NivalisAIChat.SettingsFileText.Refresh(file, now, Wrote, out var changed);
        Check(next.StartsWith("# NEW HEADER" + nl + "#" + nl) && next.Contains("# new banner" + nl + "[Group]") && next.Contains("# new desc" + nl + "# Default: 4" + nl + "Key = 5"),
              "the header, a banner and an explanation an earlier version wrote get today's wording, with the right '# Default:'");
        Check(next.Contains("# my own note" + nl + "# old desc" + nl + "Other = test-key-X"), "a block with a line of the player's in it is left exactly as it is");
        Check(next.Contains(NivalisAIChat.SettingsFileText.RetiredNote + nl + "Gone = 1"), "a setting nothing reads gets the note above it, and stays");
        Check(next.Contains("# old list desc" + nl + "Providers = [" + nl + "#   (windows, , 50, auto)," + nl + "    (https://api.openai.com/v1, test-voice-X, 100, auto)," + nl + "]"),
              "the voice list (and what's above it) isn't touched");
        Check(!next.Replace("\r\n", "").Contains('\n') && changed.Count == 4, $"Windows line endings kept; four things refreshed: {string.Join(", ", changed)}");
        Check(NivalisAIChat.SettingsFileText.Refresh(next, now, Wrote, out var twice) == next && twice.Count == 0, "run again: nothing more to do");

        string unclosed = file.Replace(nl + "]" + nl, nl);
        Check(NivalisAIChat.SettingsFileText.Refresh(unclosed, now, Wrote, out var c2) == unclosed && c2.Count == 0, "a voice list with no ']': the whole file is left alone");

        // The safety net: whatever a template says, the lines that aren't comments come out the same, or nothing changes.
        var evil = new NivalisAIChat.SettingsFileText.Template();
        evil.Blocks[("Group", "Key")] = new() { "# new desc", "ApiKey = stolen" };
        Check(NivalisAIChat.SettingsFileText.Refresh(file, evil, Wrote, out var c3) == file && c3.Count == 0,
              "a template that would add or change a setting line changes nothing at all");

        // The real file 0.3.0 wrote: with a template where every block differs, every block in it is recognised as one the
        // mod wrote (the generator's hashes and Refresh's own block finding agree) and replaced; every other line kept.
        {
            string real = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "settings_030.cfg"));
            var lines = real.Replace("\r\n", "\n").Split('\n');
            var other = new NivalisAIChat.SettingsFileText.Template { Header = new() { "# another header" } };
            int expected = 1, top = 0;
            while (top < lines.Length && lines[top].TrimStart().StartsWith("#")) top++;
            string section = "";
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t.Length == 0 || t.StartsWith("#")) continue;
                bool hasBlock = i > top && lines[i - 1].TrimStart().StartsWith("#");
                if (t.StartsWith("[") && t.EndsWith("]"))
                {
                    section = t[1..^1];
                    other.Banners[section] = new() { "# another banner" };
                    if (hasBlock) expected++;
                    continue;
                }
                int eq = t.IndexOf('=');
                if (eq < 0) continue;
                string key = t[..eq].Trim();
                if (section == "Voices" && key == "Providers") { while (i + 1 < lines.Length && !lines[i + 1].Trim().StartsWith("]")) i++; i++; continue; }
                other.Blocks[(section, key)] = new() { "# another explanation" };
                if (hasBlock) expected++;
            }
            string refreshed = NivalisAIChat.SettingsFileText.Refresh(real, other, NivalisAIChat.SettingsHistory.Wrote, out var all);
            Check(all.Count == expected && expected > 150,
                  $"on the real 0.3.0 file, every block the mod wrote is recognised and replaced ({all.Count} of {expected})");
            static System.Collections.Generic.List<string> Kept(string s) =>
                s.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("#")).ToList();
            Check(Kept(real).SequenceEqual(Kept(refreshed)), "...and every line that isn't a comment is exactly as it was");
        }

        // The history table: the block hash matches what tools/settings-history.py writes (FNV-1a 64 of kind \\u0001 block).
        Check(NivalisAIChat.SettingsHistory.Hash("") == 0xcbf29ce484222325UL && NivalisAIChat.SettingsHistory.Hash("a") == 0xaf63dc4c8601ec8cUL,
              "the hash is FNV-1a 64 (as the generator's)");
        Check(NivalisAIChat.SettingsHistory.Retired.Contains(("Food Orders", "FriendDiscountAffinity")), "settings no longer read are listed (FriendDiscountAffinity)");
    }

    /// <summary>FB-03/FB-04: thoughts follow their mood, and each line has a tone of voice.</summary>
    private static void ThoughtToneTests()
    {
        Console.WriteLine("-- 0.3.1 FB-03, FB-04: inner thoughts in their mood's tone");
        var rec = new Npc.NpcRecord { Id = "p_thinker", Persona = new Npc.Persona { Name = "Ana Reyes" } };
        rec.State.Mood = Npc.Moods.All[0];
        rec.State.MoodReason = "the landlord again";
        var req = ChatterPrompts.ThinkRequest(rec, new Game.GameSnapshot(), 2);
        string ask = req.Messages[0].Content;
        var required = req.JsonSchema!["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Check(ask.Contains($"How they feel right now: {Npc.Moods.All[0]} (the landlord again).") && ask.Contains("tones = for each line") && required.Contains("tones"),
              "the writer is told how they feel and why, and asked for a tone per line (strict schema: tones required)");
        Check(string.Join("\n", req.System).Contains("private thoughts"), "the mutter rules ask for private, personal thoughts");

        string a = Npc.Moods.All[0], b = Npc.Moods.All[^1];
        var parsed = Npc.ChatterBrain.ParseThought($"{{\"lines\":[\"Not again.\",\"\",\"Fine. Fine.\"],\"about\":\"rent\",\"tones\":[\"{a}\",\"x\",\"{b.ToUpperInvariant()}\"]}}", rec, 3);
        Check(parsed.lines.SequenceEqual(new[] { "Not again.", "Fine. Fine." }) && parsed.tones.Count == 2 && parsed.tones[0] == a && parsed.tones[1] == b,
              $"each kept line keeps its own tone word, an empty line's left out with it: {string.Join("|", parsed.tones)}");
        var older = Npc.ChatterBrain.ParseThought("{\"lines\":[\"Hm.\"],\"about\":\"x\"}", rec);
        Check(older.lines.Count == 1 && older.tones.Count == 1 && older.tones[0] == "", "an answer without tones still reads (their mood leads)");
    }

    /// <summary>FB-14: after two replies that both asked something, the next is told not to.</summary>
    private static void QuestionNoteTests()
    {
        Console.WriteLine("-- 0.3.1 FB-14: not every reply a question");
        Npc.ChatLine L(string role, string text) => new() { Role = role, Text = text };
        var talk = new System.Collections.Generic.List<Npc.ChatLine>
        {
            L("npc", "Old meeting. You?"), L("player", "hi"), L("npc", "Hey. How's it going?"), L("player", "fine"), L("npc", "Busy day?"),
        };
        Check(Prompts.QuestionNote(talk, 0).Length > 0, "two questions in a row: the next reply is told not to ask");
        Check(Prompts.QuestionNote(talk, 2) .Length > 0 && Prompts.QuestionNote(talk, 4).Length == 0, "only this meeting's replies count");
        talk.Add(L("player", "yeah")); talk.Add(L("npc", "Same here. Long shift."));
        Check(Prompts.QuestionNote(talk, 0).Length == 0, "an answer without a question resets it");
        talk.Add(L("player", "oh")); talk.Add(L("npc", "“Really?”"));
        Check(Prompts.QuestionNote(talk, 0).Length == 0, "one question isn't two");
        talk.Add(L("player", "yes")); talk.Add(L("npc", "Why, though?”"));
        Check(Prompts.QuestionNote(talk, 0).Length > 0, "a question in quotes still counts");
    }

    /// <summary>
    /// D (FB-02/03/14): the mod's own instructions in a player's custom_instructions.txt, still as 0.3.0 wrote them, get
    /// this version's wording; anything the player changed is left alone. Run on the real file 0.3.0 wrote
    /// (tests/fixtures/custom_instructions_030.txt, made from the v0.3.0 sources).
    /// </summary>
    private static void InstructionsUpgradeTests(string dir)
    {
        Console.WriteLine("-- 0.3.1: the mod's own instructions reach existing players");
        string fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "custom_instructions_030.txt"));
        string Field(string name) => CustomInstructions.DefaultOf(name);
        var changed = new[] { CustomInstructions.TheCity, CustomInstructions.TalkingToYou, CustomInstructions.ThinkingOutLoud };

        // Every earlier default round-trips: written as 0.3.0 did, it reads back as that text.
        bool roundTrips = true;
        foreach (var f in CustomInstructions.Fields)
            foreach (var old in f.Previous)
            {
                var parsed = CustomInstructions.ParseAll(CustomInstructions.FieldsSection(new[] { new CustomInstructions.Field { Name = f.Name, About = f.About, Default = old } }, header: false));
                roundTrips &= parsed.fields.TryGetValue(f.Name, out var v) && v == old.Trim();
            }
        Check(roundTrips, "every earlier default reads back word for word from the way the file writes it");
        Check(changed.All(n => fixture.Contains(CustomInstructions.Fields.First(f => f.Name == n).Previous[0])),
              "the 0.3.0 file holds each changed field's earlier text, as Previous has it");

        string Setup(string name, string text, System.Text.Encoding? enc = null)
        {
            string d = Path.Combine(dir, "instr-" + name);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, CustomInstructions.FileName), text, enc ?? new System.Text.UTF8Encoding(false));
            return d;
        }
        string FileText(string d) => File.ReadAllText(Path.Combine(d, CustomInstructions.FileName));

        // 1. Untouched: each changed field gets the new wording, in the file and as read; nothing else moves.
        {
            string d = Setup("untouched", fixture);
            var said = new System.Collections.Generic.List<string>();
            CustomInstructions.Init(d, said.Add);
            string now = FileText(d);
            Check(changed.All(n => CustomInstructions.Get(n) == Field(n) && now.Contains(Field(n))) && !now.Contains(CustomInstructions.Fields.First(f => f.Name == CustomInstructions.TalkingToYou).Previous[0]),
                  "an untouched 0.3.0 file: the changed fields have this version's wording, in the file and as read");
            string unchangedPart = fixture;
            foreach (var f in CustomInstructions.Fields.Where(f => f.Previous.Length > 0)) unchangedPart = unchangedPart.Replace(f.Previous[0], f.Default);
            Check(now == unchangedPart, "...and every other byte of the file is as it was");
            Check(CustomInstructions.FieldStates().All(s => s.state == "original") && said.Count == 1 && said[0].Contains("TheCity, TalkingToYou, ThinkingOutLoud"),
                  "/instructions says original for all; the log says which were updated: " + string.Join(" | ", said));
            var again = new System.Collections.Generic.List<string>();
            CustomInstructions.Init(d, again.Add);
            Check(FileText(d) == now && again.Count == 0, "the next start changes nothing");
        }

        // 2. A field the player changed stays theirs; the others are updated.
        {
            var old = CustomInstructions.Fields.First(f => f.Name == CustomInstructions.TalkingToYou).Previous[0];
            string mine = fixture.Replace(old, old + "\n- Swear a lot.");
            string d = Setup("changed", mine);
            CustomInstructions.Init(d);
            Check(FileText(d).Contains("- Swear a lot.") && CustomInstructions.Get(CustomInstructions.TalkingToYou).EndsWith("- Swear a lot.")
                  && CustomInstructions.Get(CustomInstructions.ThinkingOutLoud) == Field(CustomInstructions.ThinkingOutLoud),
                  "a field the player changed is kept as they wrote it; the untouched ones are updated");
        }

        // 2b. A line of the player's after the old closing quote, ending in a quote of its own: the field is theirs now (M1).
        {
            var old = CustomInstructions.Fields.First(f => f.Name == CustomInstructions.TalkingToYou).Previous[0];
            string extended = fixture.Replace(old + "\"", old + "\"\n- Never mention the weather.\"");
            string d = Setup("extended", extended);
            CustomInstructions.Init(d);
            Check(FileText(d).Contains(old + "\"\n- Never mention the weather.\"") && CustomInstructions.Get(CustomInstructions.TalkingToYou).Contains("Never mention the weather"),
                  "a field extended past its old closing quote isn't rewritten: it reads as the player's text");
        }

        // 3. Commented out with a # (the player turned the entry off to use the original): left as it is.
        {
            string off = fixture.Replace("ThinkingOutLoud = \"", "# ThinkingOutLoud = \"");
            string d = Setup("commented", off);
            CustomInstructions.Init(d);
            Check(FileText(d).Contains("# ThinkingOutLoud = \"" + CustomInstructions.Fields.First(f => f.Name == CustomInstructions.ThinkingOutLoud).Previous[0].Split('\n')[0])
                  && CustomInstructions.Get(CustomInstructions.ThinkingOutLoud) == Field(CustomInstructions.ThinkingOutLoud),
                  "an entry commented out stays commented out (and the current original is what's used)");
        }

        // 4. A note of the player's inside an untouched field: the file isn't rewritten there, but the new wording is used.
        {
            var old = CustomInstructions.Fields.First(f => f.Name == CustomInstructions.TalkingToYou).Previous[0];
            int cut = old.IndexOf('\n');
            string noted = fixture.Replace(old, old[..cut] + "\n# my note: keep this" + old[cut..]);
            string d = Setup("note", noted);
            CustomInstructions.Init(d);
            Check(FileText(d).Contains("# my note: keep this") && CustomInstructions.Get(CustomInstructions.TalkingToYou) == Field(CustomInstructions.TalkingToYou)
                  && CustomInstructions.FieldStates().Any(s => s.name == CustomInstructions.TalkingToYou && s.state.StartsWith("original (an earlier")),
                  "a note inside an untouched field: the note stays in the file, the new wording is used, /instructions says so");
        }

        // 5. Not UTF-8 ("ANSI"): never rewritten (their accented letters would change), but the new wording is still used.
        {
            var latin1 = System.Text.Encoding.Latin1;
            string ansi = "Café talk only." + "\n" + fixture;
            string d = Setup("ansi", ansi, latin1);
            byte[] before = File.ReadAllBytes(Path.Combine(d, CustomInstructions.FileName));
            CustomInstructions.Init(d);
            byte[] after = File.ReadAllBytes(Path.Combine(d, CustomInstructions.FileName));
            Check(before.SequenceEqual(after), "an ANSI file isn't rewritten (byte for byte)");
            // Its plain-ASCII fields still read as the new wording (one with an em dash saved as ANSI no longer matches word
            // for word, and stays as the file has it: that's the player's file now).
            Check(CustomInstructions.Get(CustomInstructions.ThinkingOutLoud) == Field(CustomInstructions.ThinkingOutLoud),
                  "...and an untouched plain-ASCII field in it is read with the new wording all the same");
        }

        // 6. A byte-order mark and Windows line endings are kept.
        {
            string crlf = fixture.Replace("\n", "\r\n");
            string d = Setup("bom-crlf", crlf, new System.Text.UTF8Encoding(true));
            CustomInstructions.Init(d);
            byte[] b = File.ReadAllBytes(Path.Combine(d, CustomInstructions.FileName));
            string now = new System.Text.UTF8Encoding(false).GetString(b);
            Check(b.Length > 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF && !now.Replace("\r\n", "").Contains('\n') && now.Contains(Field(CustomInstructions.ThinkingOutLoud).Replace("\n", "\r\n")),
                  "a file with a byte-order mark and Windows line endings keeps both");
        }
    }

    /// <summary>FB-18, FB-20: who counts as company, so people don't mutter to themselves beside someone.</summary>
    private static void CompanyTests()
    {
        Console.WriteLine("-- 0.3.1 FB-18, FB-20: company");
        bool Has(params (float d, bool seated, bool work)[] near) => Social.ChatterPlan.HasCompany(near);
        Check(!Has() && !Has((3f, true, true)), "nobody near (or only further than a table): on their own");
        Check(Has((1.2f, false, false)), "someone standing beside them: company");
        Check(Has((2.2f, true, false)) && !Has((2.2f, false, false)), "a table-mate across the table (both seated, 2.2 m): company; two people standing 2.2 m apart aren't");
        Check(Has((1.8f, false, true)) && !Has((2.3f, false, true)), "across a counter from someone at work (1.8 m): company; further off isn't");
    }

    /// <summary>FB-17: the console's lines in colour, everything still escaped.</summary>
    private static void ConsoleTextTests()
    {
        Console.WriteLine("-- 0.3.1 FB-17: console colours");
        string C(string hex, string t) => $"<color={hex}><noparse>{t}</noparse></color>";
        string E(string t) => $"<noparse>{t}</noparse>";
        Check(UI.ConsoleText.Format("3 problems since you last looked:") == C(UI.ConsoleText.HeaderHex, "3 problems since you last looked:"), "a heading in the section colour");
        string help = UI.ConsoleText.Format("/errors: what went wrong this session  ·  /errors clear");
        Check(help == C(UI.ConsoleText.CommandHex, "/errors") + E(":") + E(" what went wrong this session  ·  ") + C(UI.ConsoleText.CommandHex, "/errors") + E(" clear"),
              "a command line: the command, then its description, any other /command coloured too: " + help);
        Check(UI.ConsoleText.Format("Mode: Dynamic") == C(UI.ConsoleText.LabelHex, "Mode") + E(":") + E(" Dynamic"), "a label and its value");
        Check(UI.ConsoleText.Format("   also: size <2-4>").StartsWith(E("   ") + C(UI.ConsoleText.LabelHex, "also")), "indented lines keep their indent");
        Check(UI.ConsoleText.Format("Prices are up, and/or down. See https://x.io/a") == E("Prices are up, and/or down. See https://x.io/a"),
              "a sentence stays as it is: no label in it, and 'and/or' or a URL isn't a command");
        string evil = UI.ConsoleText.Format("Voice Provider 1: <color=#f00>x</noparse><b>y</b> /help");
        Check(!evil.Contains("</noparse><b>") && evil.Contains("</ noparse>") && evil.Contains(C(UI.ConsoleText.CommandHex, "/help")),
              "markup from a provider's message is never read (it stays inside noparse): " + evil);
        Check(UI.ConsoleText.Format("") == "" && UI.ConsoleText.Format("   ") == "", "an empty line stays empty");
        string upper = UI.ConsoleText.Escape("x</NOPARSE><size=300>y</ noparse ><b>z");
        Check(!System.Text.RegularExpressions.Regex.IsMatch(upper[..^"</noparse>".Length], @"</noparse>", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
              "a closing tag in another letter case (or spaced) can't end the escape early either: " + upper);
    }

    /// <summary>FB-15: the change that floats up over the relationship bar.</summary>
    private static void ChangeLabelTests()
    {
        Console.WriteLine("-- 0.3.1 FB-15: the bar's change label");
        Check(Npc.Relationships.ChangeLabel(0.35) == "+0.35" && Npc.Relationships.ChangeLabel(-0.1) == "-0.1"
              && Npc.Relationships.ChangeLabel(0.5) == "+0.5" && Npc.Relationships.ChangeLabel(1.5) == "+1.5",
              "up and down, at most two decimals: +0.35, -0.1, +0.5, +1.5");
        Check(Npc.Relationships.ChangeLabel(0.004) == null && Npc.Relationships.ChangeLabel(0) == null
              && Npc.Relationships.ChangeLabel(double.NaN) == null && Npc.Relationships.ChangeLabel(double.PositiveInfinity) == null,
              "no label for no real change, or a value that isn't a number");
        Check(Npc.Relationships.ChangeLabel(0.0051) == "+0.01" && Npc.Relationships.ChangeLabel(50.35 - 50) == "+0.35",
              "a tiny change still reads as one, and a bar value's own rounding doesn't show (50.35 - 50)");
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Check(Npc.Relationships.ChangeLabel(0.25) == "+0.25", "the same under a Windows language with a decimal comma");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    /// <summary>CF-06: how much OpenAI-format models think, only when set (GPT-5.4's own default is none).</summary>
    private static async Task ReasoningEffortTests()
    {
        Console.WriteLine("-- 0.3.1 CF-06: ReasoningEffort");
        var ep = new FakeEndpoint { Respond = _ => (200, OpenAiText("Sure.")) };
        var settings = new LlmSettings { Format = ApiFormat.OpenAI, Url = "https://api.openai.com/v1", ApiKey = "sk-t", Model = "gpt-5.4-nano", Effort = "low" };
        var client = new LlmClient(() => settings, _ => { }, new HttpMessageInvoker(ep));
        var req = new LlmRequest { MaxTokens = 400 };
        req.Messages.Add(new LlmMessage("user", "hi"));
        await client.CompleteAsync(req, CancellationToken.None);
        Check(ep.Requests[0].body["reasoning_effort"] == null && ep.Requests[0].body["effort"] == null,
              "empty: nothing sent, the model's own default (Claude's Effort is never sent to OpenAI)");
        settings.ReasoningEffort = " Low ";
        await client.CompleteAsync(req, CancellationToken.None);
        Check(ep.Requests[1].body["reasoning_effort"]?.GetValue<string>() == "low" && ep.Requests[1].body["max_completion_tokens"]?.GetValue<int>() == 400,
              "set: sent as reasoning_effort, beside the per-reply cap");

        int calls = 0;
        var ep2 = new FakeEndpoint();
        ep2.Respond = body =>
        {
            calls++;
            return body["reasoning_effort"] != null
                ? (400, @"{""error"":{""message"":""Unrecognized request argument supplied: reasoning_effort""}}")
                : (200, OpenAiText("ok"));
        };
        var c2 = new LlmClient(() => new LlmSettings { Format = ApiFormat.OpenAI, Url = "http://localhost:1234/v1", Model = "local", ReasoningEffort = "high" }, _ => { }, new HttpMessageInvoker(ep2));
        var r = await c2.CompleteAsync(req, CancellationToken.None);
        calls = 0;
        await c2.CompleteAsync(req, CancellationToken.None);
        Check(r.Ok && calls == 1, "a provider that rejects it has it switched off, and later requests go without it");
    }

    /// <summary>CF-02: a spending limit for voices, said once it's reached; lines go on as text.</summary>
    private static async Task VoiceLimitTests(string dir)
    {
        Console.WriteLine("-- 0.3.1 CF-02: VoiceLimit");
        var clock = new DateTime(2026, 10, 13, 12, 0, 0);
        long limit = 20;
        var budget = new TokenBudget(() => (limit, TokenBudget.Span.Day), Path.Combine(dir, "voice_usage.json"), null, TokenBudget.Wording.Voice) { Now = () => clock };
        var ai = new TokenBudget(() => (5, TokenBudget.Span.Day)) { Now = () => clock };
        ai.Add(10);
        Check(ai.LimitMessage().StartsWith("Token limit reached: ") && ProblemCodes.CodeFor(ai.LimitMessage()) == "AI-BUDGET",
              "the AI token limit's message is as before: " + ai.LimitMessage());

        var openai = new VoiceSlot { Number = 1, Provider = VoiceProvider.OpenAI, Chance = 1, ApiKey = "sk-test", Url = "https://api.openai.com/v1" };
        var http = new CountingSpeech();
        var svc = new VoiceService(() => new VoiceSettings { Enabled = true, TimeoutSeconds = 5, Slots = { openai } },
            new VoiceBook(Path.Combine(dir, "voices-limit.json")), _ => { }, new HttpMessageInvoker(http)) { Budget = budget };
        var who = new VoiceSubject { Id = "p_limit", Presentation = "female" };
        var a = await svc.SpeakWithOutcomeAsync(who, "Hello there.", CancellationToken.None);
        var b = await svc.SpeakWithOutcomeAsync(who, "Still here.", CancellationToken.None);
        Check(a.Audio != null && b.Audio != null && budget.Used == "Hello there.".Length + "Still here.".Length,
              $"each spoken line's characters are counted ({budget.Used})");
        int asked = http.Speech;
        var c = await svc.SpeakWithOutcomeAsync(who, "One more?", CancellationToken.None);
        Check(c.OverBudget && c.Audio == null && http.Speech == asked, "over the limit: the provider isn't asked, and the outcome says why");
        Check(c.Error != null && ProblemCodes.Coded(c.Error).StartsWith("[VOICE-BUDGET] Voice limit reached:") && c.Error.Contains("VoiceLimit"),
              "its message carries VOICE-BUDGET and names the setting: " + c.Error);
        Check(budget.Describe().Contains("voice characters"), "/usage says voice characters: " + budget.Describe());
        string before = budget.Period;
        clock = clock.AddDays(1);
        Check(budget.Period != before && budget.Allows(), "a new day starts again (and the once-a-period message can be said again)");

        Check(!VoiceService.Billed(new VoiceSlot { Provider = VoiceProvider.Windows, Url = "windows" })
              && !VoiceService.Billed(new VoiceSlot { Provider = VoiceProvider.OpenAICompatible, Url = "http://localhost:8880/v1" })
              && !VoiceService.Billed(new VoiceSlot { Provider = VoiceProvider.OpenAICompatible, Url = "http://127.0.0.1:5000/v1" })
              && !VoiceService.Billed(new VoiceSlot { Provider = VoiceProvider.OpenAICompatible, Url = "http://[::1]:8880/v1" })
              && VoiceService.Billed(openai)
              && VoiceService.Billed(new VoiceSlot { Provider = VoiceProvider.ElevenLabs, Url = "https://api.elevenlabs.io" }),
              "Windows' voices and a server on this PC aren't counted; providers that bill are");
        Check(!VoiceService.Billed(new VoiceSlot { Provider = VoiceProvider.OpenAICompatible, Url = "http://192.168.1.20:8880/v1" })
              && !VoiceService.Billed(new VoiceSlot { Provider = VoiceProvider.OpenAICompatible, Url = "http://10.0.0.246:8880/v1" })
              && !VoiceService.Billed(new VoiceSlot { Provider = VoiceProvider.OpenAICompatible, Url = "http://developmentvm:8880/v1" })
              && !VoiceService.Billed(new VoiceSlot { Provider = VoiceProvider.OpenAICompatible, Url = "http://kokoro.local/v1" })
              && VoiceService.Billed(new VoiceSlot { Provider = VoiceProvider.OpenAICompatible, Url = "https://tts.example.com/v1" })
              && VoiceService.Billed(new VoiceSlot { Provider = VoiceProvider.OpenAICompatible, Url = "http://8.8.8.8/v1" }),
              "a server on your own network (a private address, a one-word or .local name) isn't counted either; one on the internet is");

        // A local server keeps speaking past the limit: it costs nothing.
        limit = 1;
        var local = new VoiceSlot { Number = 1, Provider = VoiceProvider.OpenAICompatible, Chance = 1, Url = "http://localhost:8880/v1", Voices = "af_bella:female" };
        var free = new TokenBudget(() => (1, TokenBudget.Span.Day), null, null, TokenBudget.Wording.Voice) { Now = () => clock };
        free.Add(5);
        var svc2 = new VoiceService(() => new VoiceSettings { Enabled = true, TimeoutSeconds = 5, Slots = { local } },
            new VoiceBook(Path.Combine(dir, "voices-local.json")), _ => { }, new HttpMessageInvoker(new CountingSpeech())) { Budget = free };
        var d = await svc2.SpeakWithOutcomeAsync(new VoiceSubject { Id = "p_local", Presentation = "female" }, "Free to say.", CancellationToken.None);
        Check(d.Audio != null && !d.OverBudget && free.Used == 5, "a voice server on this PC speaks past the limit and isn't counted");
    }
}
