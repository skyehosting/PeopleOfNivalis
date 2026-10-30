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

namespace NivalisAIChat.Tests;

/// <summary>The bug hunt of 2026-10-13: the fixes that can be checked here.</summary>
internal static partial class Program
{
    private static async Task Audit1013Tests(string dir)
    {
        Console.WriteLine("-- audit 2026-10-13: the verified findings");
        EncodingKeptTests(Path.Combine(dir, "encodings"));

        // B1: a table call from someone in the holder's own life is said to be an adult.
        var call = new ChatterPrompts.TableCall { HolderId = "p_h", HolderName = "Rosa Diaz", Answered = true, Callee = new ChatterPrompts.Callee { Name = "Mia", Relation = "daughter" } };
        Check(ChatterPrompts.TableCallNote(call).Contains("Mia (daughter, an adult)"), "a caller from their own life (\"Mia, daughter\") is written as an adult");

        // B11: "Teo" on the phone isn't Teo Marsh at the table.
        ChatterPrompts.Person P(string id, string name) => new() { Record = new NpcRecord { Id = id, Persona = new Persona { Name = name, Age = "40" } }, Seated = true, MaxLines = 5 };
        var people = new List<ChatterPrompts.Person> { P("p_rosa", "Rosa Diaz"), P("p_teo", "Teo Marsh") };
        var teoCall = new ChatterPrompts.TableCall { HolderId = "p_rosa", HolderName = "Rosa Diaz", Answered = true, Callee = new ChatterPrompts.Callee { Name = "Teo", Relation = "brother" } };
        string json = "{\"lines\":[" +
                      "{\"speaker\":\"Teo Marsh\",\"to\":\"Rosa Diaz\",\"say\":\"Long day?\",\"tone\":\"\",\"gesture\":\"\",\"leaving\":false,\"phone\":false,\"speakerphone\":false}," +
                      "{\"speaker\":\"Rosa Diaz\",\"to\":\"\",\"say\":\"One moment.\",\"tone\":\"\",\"gesture\":\"\",\"leaving\":false,\"phone\":true,\"speakerphone\":false}," +
                      "{\"speaker\":\"Teo\",\"to\":\"Rosa Diaz\",\"say\":\"Hey sis, it's me.\",\"tone\":\"\",\"gesture\":\"\",\"leaving\":false,\"phone\":false,\"speakerphone\":false}," +
                      "{\"speaker\":\"Rosa Diaz\",\"to\":\"\",\"say\":\"Call you back.\",\"tone\":\"\",\"gesture\":\"\",\"leaving\":false,\"phone\":true,\"speakerphone\":false}]," +
                      "\"getAlong\":[],\"summary\":\"A call.\",\"heardAboutPlayer\":[],\"exits\":[]}";
        var parsed = ChatterBrain.ParseTableCall(json, people, teoCall);
        Check(parsed != null && parsed.Lines.Any(l => l.Text == "Hey sis, it's me." && l.FarEnd) && !parsed.Lines.Any(l => l.Text == "Hey sis, it's me." && l.SpeakerId == "p_teo"),
              "a line from \"Teo\" on the phone is the caller's, not Teo Marsh's at the table");
        var twoTeos = new List<ChatterPrompts.Person> { P("p_teo1", "Teo Marsh"), P("p_teo2", "Teo Vance") };
        string plain = "{\"lines\":[{\"speaker\":\"Teo Marsh\",\"to\":\"\",\"say\":\"Hi.\",\"tone\":\"\",\"gesture\":\"\",\"leaving\":false}," +
                       "{\"speaker\":\"Teo\",\"to\":\"\",\"say\":\"Which of us?\",\"tone\":\"\",\"gesture\":\"\",\"leaving\":false}," +
                       "{\"speaker\":\"Teo Vance\",\"to\":\"\",\"say\":\"Hey.\",\"tone\":\"\",\"gesture\":\"\",\"leaving\":false}]," +
                       "\"getAlong\":[],\"summary\":\"Hi.\",\"heardAboutPlayer\":[],\"exits\":[]}";
        var two = ChatterBrain.ParseScript(plain, twoTeos, stay: true);
        Check(two != null && !two.Lines.Any(l => l.Text == "Which of us?"), "two people with the same first name: a line under that name alone is dropped, not given to the first");

        // B2: an Anthropic-format endpoint that refuses structured output is asked for JSON in words.
        var ep = new FakeEndpoint
        {
            Respond = body => body["output_config"]?["format"] != null
                ? (400, "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"output_config.format: Extra inputs are not permitted\"}}")
                : (200, AnthropicText("{\"ok\":true}")),
        };
        var client = new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(ep));
        var req = new LlmRequest { Purpose = "persona", JsonSchema = new JsonObject { ["type"] = "object" } };
        req.System.Add("Create the resident.");
        req.Messages.Add(new LlmMessage("user", "Go."));
        var res = await client.CompleteAsync(req, CancellationToken.None);
        string lastSystem = ep.Requests.Count > 0 ? ep.Requests[^1].body["system"]?.ToJsonString() ?? "" : "";
        Check(res.Ok && ep.Requests.Count >= 2 && lastSystem.Contains("Reply with a single JSON object only"),
              $"structured output refused: the request asks for the JSON shape in words ({ep.Requests.Count} requests)");

        // B3: a key Voyage refused isn't used again.
        var people2 = new NpcStore(Path.Combine(dir, "a1013-people"), _ => { });
        foreach (var id in new[] { "p_m1", "p_m2", "p_m3" })
            people2.Save(new NpcRecord { Id = id, Persona = new Persona { Name = "Ana " + id, Age = "40" }, History = { new ChatLine { Role = "npc", Text = "The docks flooded again.", GameDay = 1 } } });
        var refusing = new FakeVoyage { Status = 401 };
        var index = new MemoryIndexer(new VoyageClient(() => new VoyageSettings { Key = "pa-bad" }, new HttpMessageInvoker(refusing)),
            people2, new MemoryStore(Path.Combine(dir, "a1013-memory")), () => true, _ => { }, _ => { });
        index.QueueFirst("p_m1");
        await WaitUntil(() => refusing.Requests.Count >= 1, 3000);
        await Task.Delay(400);
        index.QueueFirst("p_m2");
        index.Queue("p_m3");
        await Task.Delay(800);
        var recall = await index.RecallAsync("p_m1", "the docks", 2, null, CancellationToken.None);
        Check(refusing.Requests.Count == 1 && recall.Count == 0, $"after Voyage refuses the key, nothing more is sent with it ({refusing.Requests.Count} request(s))");
        // Someone indexed with a good key, then the key refused: their memories aren't searched with it either.
        string key = "pa-good";
        var switching = new FakeVoyage();
        var index2 = new MemoryIndexer(new VoyageClient(() => new VoyageSettings { Key = key }, new HttpMessageInvoker(switching)),
            people2, new MemoryStore(Path.Combine(dir, "a1013-memory2")), () => true, _ => { }, _ => { });
        await index2.IndexAsync("p_m1", CancellationToken.None);
        key = "pa-revoked";
        switching.Status = 401;
        index2.QueueFirst("p_m2");
        await WaitUntil(() => index2.KeyRefused, 3000);
        int before = switching.Requests.Count;
        try { await index2.RecallAsync("p_m1", "the docks flooded", 2, null, CancellationToken.None); } catch (VoyageException) { } // asked anyway: counted below
        Check(index2.KeyRefused && switching.Requests.Count == before, "...and searching someone already indexed doesn't ask Voyage with the refused key");

        // B4: a transcript batch that couldn't be written is written when the file frees up, at the latest when it closes.
        string tdir = Path.Combine(dir, "a1013-transcript");
        var log = new TranscriptLog(tdir, _ => { }, new DateTime(2026, 10, 13, 4, 0, 0));
        log.Header(new JsonObject { ["chat model"] = "x" });
        await WaitUntil(() => File.Exists(log.CurrentFile), 3000);
        using (new FileStream(log.CurrentFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) // another program has it, a moment
        {
            for (int i = 0; i < 3; i++) log.Line(new JsonObject { ["kind"] = "chat", ["text"] = "kept " + i });
            await Task.Delay(700);
        }
        log.Close();
        int kept = File.ReadAllLines(log.CurrentFile).Count(l => l.Contains("\"kept "));
        Check(kept == 3, $"lines that couldn't be written while the file was busy are written once it's free (by the time it closes): {kept} of 3");

        // B5: a voice list closed on its last provider line still lets the explanations after it be refreshed.
        string nl = "\r\n";
        string cfg = string.Join(nl, new[] { "[Voices]", "Providers = [", "    (https://api.openai.com/v1, test-voice-X, 100, auto)]", "", "[Group]", "# old desc", "Key = 5", "" });
        var now = new SettingsFileText.Template();
        now.Blocks[("Group", "Key")] = new() { "# new desc" };
        string refreshed = SettingsFileText.Refresh(cfg, now, (kind, block) => kind == "Group|Key" && block == "# old desc", out var changedB5);
        Check(refreshed.Contains("# new desc" + nl + "Key = 5") && changedB5.Contains("Key") && refreshed.Contains("test-voice-X, 100, auto)]"),
              "a voice list closed on its last line: the explanations after it still get today's wording, the list untouched");
        string unclosed = string.Join(nl, new[] { "[Voices]", "Providers = [", "    (https://api.openai.com/v1, test-voice-X, 100, auto),", "", "[Group]", "# old desc", "Key = 5", "" });
        string same = SettingsFileText.Refresh(unclosed, now, (kind, block) => kind == "Group|Key" && block == "# old desc", out var c3, out bool refusedR3);
        Check(same == unclosed && c3.Count == 0 && refusedR3, "a voice list left open before the next group: the file is left alone and the refresh is tried again (review R3)");

        // B8: only the mod's own codes are carried.
        Check(ProblemCodes.Coded("Voyage said: upstream [ERR-500] went away", "MEM-VOYAGE").StartsWith("[MEM-VOYAGE] "),
              "a provider's own \"[ERR-500]\" inside a message isn't taken for the problem's code");
        Check(ProblemCodes.Coded("Voyage said: upstream [AI-KEY] whatever", "MEM-VOYAGE").StartsWith("[MEM-VOYAGE] "),
              "...nor one of the mod's own codes in the middle of quoted text (mutation check 2026-10-13)");
        Check(ProblemCodes.Coded("A phone call couldn't be written: [AI-LIMIT] Too many requests.") == "[AI-LIMIT] A phone call couldn't be written: Too many requests.",
              "a code passed along after \"...: \" is still carried (guard)");
        Check(ProblemCodes.Coded("[DATA-READ] Someone's file couldn't be read.") == "[DATA-READ] Someone's file couldn't be read.", "...and one at the start (guard)");

        // Slice 1 #7: a child described without a verb.
        Check(CanonAge.FromDescription("A young girl who sells flowers by the pier.") is { Child: true }, "\"A young girl who sells flowers\" reads as a child");
        Check(CanonAge.FromDescription("A little girl, maybe ten, with a red umbrella.") is { Child: true }, "...\"A little girl, maybe ten\" too");
        Check(new[] { "A young girl in a yellow raincoat.", "A little boy sitting by the fountain.", "A schoolgirl waiting for the bus.", "A teen on a skateboard." }
                  .All(d => CanonAge.FromDescription(d) is { Child: true }), "...and any other child opening (\"A young girl in a yellow raincoat.\", \"A teen on a skateboard.\"): final review F2");
        Check(CanonAge.FromDescription("A child psychologist who runs the clinic.") is not { Child: true }
              && CanonAge.FromDescription("An elderly man. A kid at heart.") is not { Child: true }, "...not \"A child psychologist\" or \"A kid at heart\" (review R7)");
        Check(CanonAge.FromDescription("A girl's best friend is her bike, she says.") is not { Child: true }, "...not \"A girl's best friend\" (guard)");
        Check(CanonAge.FromDescription("A dockworker raising a kid alone.") is not { Child: true }, "...nor \"raising a kid\" mid-sentence (guard)");

        // B9: a very long id's file name is the same in every run.
        string longId = "extra_" + new string('x', 200);
        ulong h = 14695981039346656037UL;
        foreach (char ch in longId) { h ^= ch; h *= 1099511628211UL; }
        Check(NpcStore.FileNameFor(longId).EndsWith("_" + h.ToString("x16") + ".json"), "a very long id's file name doesn't change from one session to the next");
    }

    /// <summary>findings #25: a settings file is written back in the encoding it was saved in, byte-order mark included.</summary>
    private static void EncodingKeptTests(string dir)
    {
        Directory.CreateDirectory(dir);
        string text = "[Group]\r\nKey = caf\u00e9\r\n";
        void Case(string name, System.Text.Encoding saved, byte[] mark)
        {
            string path = Path.Combine(dir, name + ".cfg");
            File.WriteAllBytes(path, mark.Concat(saved.GetBytes(text)).ToArray());
            var (read, enc) = NivalisAIChat.Npc.DataFile.ReadKeepingBytes(path);
            NivalisAIChat.Npc.DataFile.WriteAtomic(path, read + "Other = 1\r\n", encoding: enc);
            byte[] after = File.ReadAllBytes(path);
            bool markKept = after.Take(mark.Length).SequenceEqual(mark) && (mark.Length > 0 || !(after.Length > 2 && after[0] == 0xEF));
            var back = NivalisAIChat.Npc.DataFile.ReadKeepingBytes(path).text;
            Check(read == text && markKept && back == text + "Other = 1\r\n" && NivalisAIChat.Npc.DataFile.EncodingOfFile(path).GetPreamble().SequenceEqual(mark),
                  $"a file saved as {name} is written back as {name} (its byte-order mark kept, the text the same)");
        }
        Case("UTF-8 with a byte-order mark", new System.Text.UTF8Encoding(false), new byte[] { 0xEF, 0xBB, 0xBF });
        Case("UTF-16", new System.Text.UnicodeEncoding(false, false), new byte[] { 0xFF, 0xFE });
        Case("UTF-8 without a mark", new System.Text.UTF8Encoding(false), Array.Empty<byte>());
    }

    private static async Task WaitUntil(Func<bool> done, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!done() && DateTime.UtcNow < until) await Task.Delay(25);
    }
}
