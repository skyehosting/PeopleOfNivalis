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

/// <summary>FB-50: story characters keep the story conversations the player has seen, and talk like their story. Nothing
/// the player hasn't seen ever reaches the AI.</summary>
internal static partial class Program
{
    private static async Task Fixes042Tests()
    {
        Console.WriteLine("-- 0.4.2 FB-50: story conversations and story voices");
        try { await StoryTests(); }
        finally { Features.Reset(); }
    }

    private static StoryBook.Line L(string who, string text) => new() { Who = who, Text = text };

    private static async Task StoryTests()
    {
        string dir = TestRoot.NewFolder("story");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "story.json");
        const string banor = "person_banor-guid";

        // ---- kept as shown
        var book = new StoryBook(path);
        Check(book.Add(banor, "Banor", StoryBook.Them, "You're late. <i>Again.</i>", false, 5, "09:10")
              && book.Add(banor, "Banor", StoryBook.You, "Traffic was bad.", false, 5, "09:10")
              && !book.Add(banor, "Banor", StoryBook.You, "Traffic was bad.", false, 5, "09:10")
              && book.Add(banor, "Banor", "Lois Gyo", "Leave the kid alone, Banor.", false, 5, "09:10"),
              "story lines are kept as shown; the same line twice in a row once; another speaker by name");
        Check(book.Scenes(banor) is [{ GameDay: 5, Lines.Count: 3 } s] && s.Lines[0].Text == "You're late. Again.", "...in one scene, the game's markup gone");
        Check(book.End(banor) == 3 && book.Add(banor, "Banor", StoryBook.Them, "Back again?", false, 5, "18:00") && book.Scenes(banor).Count == 2,
              "a finished conversation is closed: the next line starts a new scene");
        book.End(banor);
        Check(!book.Add(banor, "Banor", StoryBook.Them, "   ", false, 5, "") && !book.Add("", "x", StoryBook.Them, "hi", false, 1, ""), "empty lines and no id: nothing kept");

        // ---- from the game's chat history page
        var page = new List<StoryBook.Line> { L(StoryBook.Them, "First time here?"), L(StoryBook.You, "Yeah, just moved in."), L(StoryBook.Them, "Welcome to Nivalis.") };
        Check(book.ImportPage(banor, "Banor", 2, page) && book.Scenes(banor)[0] is { GameDay: 2, Source: "history2" },
              "a page of the game's chat history is kept, before the later days");
        Check(!book.ImportPage(banor, "Banor", 2, page), "...the same page again: skipped");
        Check(!book.ImportPage(banor, "Banor", 5, new List<StoryBook.Line> { L(StoryBook.Them, "You're late. Again."), L(StoryBook.You, "Traffic was bad.") }),
              "...a page of a day already recorded as it played: skipped");

        // ---- saved and read back
        book.Flush();
        var again = new StoryBook(path);
        Check(again.LineCount(banor) == 7 && again.OwnLines(banor).Count == 4, "saved and read back (7 lines, 4 of them his)");
        again.Close();
        book.Close();

        // ---- speech sheet: due after 15 of their own lines, again after 60 more
        var sb = new StoryBook(Path.Combine(dir, "sheets.json"));
        for (int i = 0; i < 14; i++) sb.Add(banor, "Banor", StoryBook.Them, $"Line number {i} from Banor, said plainly.", false, 1, "");
        sb.Add(banor, "Banor", StoryBook.You, "Player line.", false, 1, "");
        Check(!sb.NeedsSheet(banor), "14 of their lines: no sheet yet (the player's don't count)");
        sb.Add(banor, "Banor", StoryBook.Them, "And the fifteenth line.", false, 1, "");
        Check(sb.NeedsSheet(banor), "15 of their lines: a sheet is due");
        sb.SetSheet(banor, "- Short, dry sentences.", 15);
        for (int i = 0; i < 59; i++) sb.Add(banor, "Banor", StoryBook.Them, $"More from Banor, number {i}.", false, 2, "");
        Check(!sb.NeedsSheet(banor), "...written: not again until 60 more");
        sb.Add(banor, "Banor", StoryBook.Them, "The sixtieth more.", false, 2, "");
        Check(sb.NeedsSheet(banor), "...60 more: due again");

        // ---- the most kept per person
        var cap = new StoryBook(Path.Combine(dir, "cap.json"));
        for (int i = 0; i < StoryBook.MaxLinesPerPerson + 25; i++) cap.Add("p_cap", "Cap", StoryBook.Them, $"Line {i}", false, i / 100, "");
        Check(cap.LineCount("p_cap") == StoryBook.MaxLinesPerPerson && cap.Scenes("p_cap")[0].Lines[0].Text == "Line 25", "past the most kept, the oldest lines go first");
        cap.Forget("p_cap");
        Check(cap.LineCount("p_cap") == 0, "/resetnpc forgets their story");

        // ---- the chat window: story lines among the chats, by game day
        var history = new List<ChatLine>
        {
            new() { Role = "scene", GameDay = 3, Utc = new DateTime(2026, 10, 1, 10, 0, 0) },
            new() { Role = "player", Text = "Hey Banor.", GameDay = 3, Utc = new DateTime(2026, 10, 1, 10, 0, 1) },
            new() { Role = "npc", Text = "What now?", GameDay = 3, Utc = new DateTime(2026, 10, 1, 10, 0, 2) },
        };
        var scenes = new List<StoryBook.Scene>
        {
            new() { GameDay = 2, Source = "history", Utc = DateTime.MinValue, Lines = { L(StoryBook.Them, "Welcome to Nivalis."), L(StoryBook.You, "Thanks.") } },
            new() { GameDay = 3, Utc = new DateTime(2026, 10, 1, 11, 0, 0), Lines = { L("Lois Gyo", "He's always like this.") } },
        };
        var rows = HistoryRows.For(StoryBook.Timeline(history, scenes), 50, 4);
        string Show((HistoryRows.Kind k, string t) r) => $"{r.k}:{r.t}";
        Check(string.Join(" | ", rows.Select(Show)) ==
              "Divider:From the story · Day 2 | StoryThem:Welcome to Nivalis. | StoryYou:Thanks. | Divider:Day 3 | Player:Hey Banor. | Npc:What now? | " +
              "Divider:From the story · Day 3 | StoryOther:Lois Gyo\u001FHe's always like this. | Divider:Day 4",
              "the window: story scenes in among the chats by day, under \"From the story\", the chats in their own order");
        Check(HistoryRows.For(StoryBook.Timeline(new List<ChatLine>(), scenes.Take(1).ToList()), 1, 2).Select(Show).First() == "Divider:From the story · Day 2",
              "...a story scene shown from its middle still says where it's from");
        Check(HistoryRows.For(history, 50, 4).All(r => r.kind is HistoryRows.Kind.Divider or HistoryRows.Kind.Player or HistoryRows.Kind.Npc), "...no story: as before");

        // ---- the prompt blocks
        var told = new List<StoryBook.Scene>
        {
            new() { GameDay = 2, Lines = { L(StoryBook.Them, "Welcome to Nivalis."), L(StoryBook.You, "Thanks, I'm Rin."), L("Lois Gyo", "Don't let him scare you.") } },
            new() { GameDay = 5, Lines = { new StoryBook.Line { Who = StoryBook.Them, Text = "Meet me at the docks.", Message = true } } },
        };
        string block = Prompts.StoryBlock(told, "Rin");
        Check(block.StartsWith("[YOUR STORY WITH RIN]") && block.Contains("nothing after them has happened yet") && block.Contains("Never invent, foretell or hint")
              && block.Contains("Day 2:") && block.Contains("  You: \"Welcome to Nivalis.\"") && block.Contains("  Rin: \"Thanks, I'm Rin.\"")
              && block.Contains("  Lois Gyo: \"Don't let him scare you.\"") && block.Contains("You (by message): \"Meet me at the docks.\"")
              && block.IndexOf("Day 2:") < block.IndexOf("Day 5:"),
              "the story block: what happened, oldest first, who said what, and that nothing after it has happened");
        var many = Enumerable.Range(0, 200).Select(i => new StoryBook.Scene { GameDay = i, Lines = { L(StoryBook.Them, $"Something said on day {i}, at some length.") } }).ToList();
        string cut = Prompts.StoryBlock(many, "Rin", 1000);
        Check(cut.Contains("(Earlier story conversations aren't shown here.)") && cut.Contains("day 199") && !cut.Contains("day 3,") && cut.Length < 1700,
              "...too much: the most recent within the room, saying earlier ones aren't shown");
        Check(Prompts.StoryBlock(new List<StoryBook.Scene>(), "Rin") == "", "...no story: no block");
        var own = new List<string> { "You're late. Again.", "Nobody asked you, kid. Sit down.", "The docks don't sleep, and neither do I.", "Fine. Have it your way, then." };
        string voice = Prompts.StoryVoiceBlock("- Short, dry sentences.\nRole: runs the docks.", own);
        Check(voice.StartsWith("[HOW YOU TALK IN YOUR STORY]") && voice.Contains("Short, dry sentences") && voice.Contains("\"Nobody asked you, kid. Sit down.\""),
              "the voice block: the sheet and a few of their own lines");
        Check(Prompts.StoryVoiceBlock("", own.Take(2).ToList()) == "", "...no sheet and fewer than three lines: no block");
        var sheetReq = Prompts.StorySheetRequest("Banor", own);
        string asked = string.Join("\n", sheetReq.System) + sheetReq.Messages[0].Content;
        Check(sheetReq.Purpose == "story voice" && sheetReq.Background && asked.Contains("never retell events") && own.All(asked.Contains),
              "the sheet request: their lines, how they talk and their role, never events");

        // ---- the game's chat history page: only theirs and yours (anyone else is a name whose age can't be checked)
        Check(StoryBook.WhoOnPage("Banor", "Banor", false) == StoryBook.Them && StoryBook.WhoOnPage("", "Banor", false) == StoryBook.Them
              && StoryBook.WhoOnPage("You", "Banor", true) == StoryBook.You && StoryBook.WhoOnPage("Pip", "Banor", false) == null
              && StoryBook.WhoOnPage("Lois Gyo", "Banor", false) == null,
              "a chat history bubble: their name or none is them, the player is you, anyone else is left out");

        // ---- under 18: nothing of theirs is kept
        Check(Minors.IsStoryChild("485995e3-a108-4067-bf25-e7ee954ea677", "", "") && Minors.IsStoryChild(null, "Silo Mills", null)
              && Minors.IsStoryChild("x", "Pip", "Pip is a 12 year old girl who sells flowers.") && !Minors.IsStoryChild("x", "Banor", "Banor runs the docks."),
              "a story child is known by guid, name or description");

        // ---- in a chat: the blocks, each behind its switch, and nothing the player hasn't seen
        const string unseen = "THE TWIST: Banor sold you out on day 40.";
        var ep = new FakeEndpoint();
        int chats = 0;
        ep.Respond = body =>
        {
            string firstUser = body["messages"]![0]!["content"]!.GetValue<string>();
            if (body["output_config"]?["format"] != null && (firstUser.Contains("Create the resident") || firstUser.Contains("Flesh out"))) return (200, AnthropicText(PersonaJson.Replace("Odalys Brennquist", "Banor Vale")));
            string sys0 = body["system"]?[0]?["text"]?.GetValue<string>() ?? "";
            if (sys0.StartsWith("You read the lines a character speaks")) return (200, AnthropicText("- Short, dry sentences; calls people kid.\nRole: runs the docks."));
            chats++;
            return (200, AnthropicText($"Line {chats}. [[mood: calm]]"));
        };
        var store = new NpcStore(Path.Combine(dir, "npcs"), _ => { });
        var log = new List<string>();
        var brain = new NpcBrain(new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(ep)), store,
                                 () => new BrainSettings { LifeUpdates = false, MeetingSummaries = false }, log.Add);
        var stories = new StoryBook(Path.Combine(dir, "chat-story.json"));
        brain.Story = stories;
        const string bid = "person_banor-chat";
        stories.ImportPage(bid, "Banor Vale", 2, new List<StoryBook.Line> { L(StoryBook.Them, "Welcome to Nivalis, kid."), L(StoryBook.You, "Thanks.") });
        for (int i = 0; i < 15; i++) stories.Add(bid, "Banor Vale", StoryBook.Them, $"Dock line {i}: keep your head down out there.", false, 3, "");
        stories.Add(bid, "Banor Vale", "Lois Gyo", "Ignore him.", false, 3, "");
        stories.End(bid);
        var visual = new NpcVisual { Body = "man", Named = true, GameName = "Banor Vale", Description = "Banor runs the docks." };
        var snap = new GameSnapshot { GameDay = 6, Hour = 12, PlayerName = "Rin" };
        var (session, _, err) = await brain.BeginAsync(bid, visual, snap, CancellationToken.None);
        Check(session != null, "a story character's chat begins: " + err);
        string Sys(JsonObject b) => string.Join("\n", (b["system"] as JsonArray ?? new JsonArray()).Select(x => x?["text"]?.GetValue<string>() ?? ""));
        var personaReq = ep.Requests.FirstOrDefault(r => r.body["output_config"]?["format"] != null).body;
        Check(personaReq != null && personaReq.ToJsonString().Contains("whom the player has met before") && Sys(personaReq).Contains("[YOUR STORY WITH RIN]"),
              "someone with a story with you is written as having met you, to fit that story");
        if (session != null)
        {
            var chatReq = ep.Requests.Last(r => Sys(r.body).Contains("You are Banor Vale")).body;
            Check(Sys(chatReq).Contains("[YOUR STORY WITH RIN]") && Sys(chatReq).Contains("Welcome to Nivalis, kid.") && Sys(chatReq).Contains("[HOW YOU TALK IN YOUR STORY]"),
                  "the chat is told their story with you and how they talk");
            await brain.SayAsync(session, "Remember me?", snap, CancellationToken.None);
            Features.StoryMemory = false;
            await brain.SayAsync(session, "Anything new?", snap, CancellationToken.None);
            var off = ep.Requests.Last(r => r.body.ToJsonString().Contains("Anything new?") && Sys(r.body).Contains("You are Banor Vale")).body;
            Check(!Sys(off).Contains("[YOUR STORY WITH") && Sys(off).Contains("[HOW YOU TALK IN YOUR STORY]"), "StoryMemory off: their story isn't told (their voice still is)");
            Features.StoryMemory = true;
            Features.StoryVoice = false;
            await brain.SayAsync(session, "And now?", snap, CancellationToken.None);
            var voiceOff = ep.Requests.Last(r => r.body.ToJsonString().Contains("And now?") && Sys(r.body).Contains("You are Banor Vale")).body;
            Check(Sys(voiceOff).Contains("[YOUR STORY WITH") && !Sys(voiceOff).Contains("[HOW YOU TALK IN YOUR STORY]"), "StoryVoice off: no voice block (their story still is)");
            Features.StoryVoice = true;
            brain.End(session);
            await brain.Settled(bid);
        }

        // ---- the speech sheet, written from their own seen lines only
        int before = ep.Requests.Count;
        Check(await brain.MaybeWriteStorySheetAsync(bid, "Banor Vale", CancellationToken.None) && stories.Sheet(bid).Contains("calls people kid"),
              "a speech sheet is written once 15 of their lines are kept");
        var sheetBody = ep.Requests[before].body.ToJsonString();
        Check(sheetBody.Contains("Dock line 0") && !sheetBody.Contains("Ignore him.") && !sheetBody.Contains("Thanks."), "...from their own lines only (not the player's, not others')");
        Check(!await brain.MaybeWriteStorySheetAsync(bid, "Banor Vale", CancellationToken.None) && ep.Requests.Count == before + 1, "...not again until 60 more");
        Features.StoryVoice = false;
        stories.SetSheet(bid, "", 0);
        Check(!await brain.MaybeWriteStorySheetAsync(bid, "Banor Vale", CancellationToken.None), "...StoryVoice off: never written");
        Features.StoryVoice = true;

        // The spoiler guarantee: only what's kept (what the player saw) is ever sent.
        Check(ep.Requests.All(r => !r.body.ToJsonString().Contains(unseen)), "a story line the player hasn't seen is in no request");
        stories.Close();
    }
}
