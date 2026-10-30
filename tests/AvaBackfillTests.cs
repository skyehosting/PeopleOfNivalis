using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Tests;

/// <summary>FB-75: Ava's earlier lines from the game's own history, and their voices from the game's recordings.</summary>
internal static partial class Program
{
    private static void AvaBackfillTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- FB-75: Ava's earlier lines and voices from the game's history");
        var nodes = new Dictionary<string, StoryHistory.Node>
        {
            ["DFr_1"] = new("You're <b>awake</b>. Good.", true, "[Dlg_AAAA0001] Wake_01", false),
            ["DFr_2"] = new("Kid's line in her scene.", false, "[Dlg_AAAA0001] Wake_02", false),
            ["DFr_3"] = new("Unseen line further in the tree.", true, "[Dlg_AAAA0001] Wake_09", false),
            ["DFr_4"] = new("No voice for this one.", true, "", true),
        };
        var choices = new Dictionary<string, StoryHistory.Choice> { ["DFr_c1"] = new("Where am I?", false) };
        var page = new List<(string, bool)> { ("DFr_1", false), ("DFr_c1", true), ("DFr_2", false), ("DFr_4", false), ("DFr_missing", false), ("", false) };
        var (lines, voices) = StoryHistory.PageLines(page, nodes, choices);
        Check(lines.Select(l => l.Who + ":" + l.Text).SequenceEqual(new[] { "them:You're awake. Good.", "you:Where am I?", "them:No voice for this one." }),
              "a history page: her lines and your picks, in order, cleaned; someone else's line in her scene left out");
        Check(!lines.Any(l => l.Text.Contains("Unseen")), "only ids on the page (seen) are read: the rest of the tree never");
        Check(voices.Count == 1 && voices[0].Text == "You're awake. Good." && voices[0].Voice == "[Dlg_AAAA0001] Wake_01" && lines[2].Message,
              "her voiced lines come back as voice jobs (by the game's voice file); by-message kept");

        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string rt = File.ReadAllText(Path.Combine(src, "ModRuntime.AvaBackfill.cs"));
        Check(rt.Contains("KnownUnder18(id)") && rt.Contains("data.TreesGuids") && rt.Contains("StoryHistory.PageLines(ids, nodes, choices)") && rt.Contains("nodes.Clear(); choices.Clear();"),
              "only the trees the game lists as seen, only lines on its pages, nothing kept of the rest; never anyone under 18");
        Check(rt.Contains("if (InStoryDialogue()) return;") && rt.Contains("Resources.UnloadAsset(clip)") && rt.Contains("!PhoneUi.PlayingGameVoice"),
              "voiced one at a time, never during the game's own conversation; the clip let go after (not while it plays)");
        Check(!rt.Contains("Log.Info($\"Story: \" + job.Text") && !System.Text.RegularExpressions.Regex.IsMatch(rt, @"Log\.(Info|Warn)\([^;]*job\.Text"), "a line's words are never logged");
        string ui = File.ReadAllText(Path.Combine(src, "UI", "PhoneWindow.Parts.cs"));
        Check(ui.Contains("if (_noteClip != null && !_noteGame)") && ui.Contains("PhoneBook.IsGameVoice(file) && GameClip != null"), "a game recording is played from the game and never destroyed by the phone");

        // FB-78: every one of her history lines was dropped (her speaker was another copy of her than the one found), so her
        // thread opened with a long run of only your picks. Hers is now by her id, and what the broken read left is repaired.
        Check(rt.Contains("speaker.Pointer == avaPtr || GameContext.PersonId(speaker) == id"), "FB-78: a line is hers by her id, whichever copy of her the game's tree points to");
        string dir = TestRoot.NewFolder("fb78");
        Directory.CreateDirectory(dir);
        const string ava = "person_ava";
        // As 0.10.0's first read left a save: a page from the history with only your picks.
        File.WriteAllText(Path.Combine(dir, "story.json"), "{\"person_ava\":{\"name\":\"Ava\",\"scenes\":[{\"gameDay\":0,\"clock\":\"\",\"source\":\"history\"," +
            "\"lines\":[{\"who\":\"you\",\"text\":\"Where am I?\"},{\"who\":\"you\",\"text\":\"What's a pirate?\"}]}]}}");
        var book = new StoryBook(Path.Combine(dir, "story.json"));
        Check(book.Scenes(ava).Count == 1 && book.Scenes(ava)[0].Source == "history", "(the old page is read back)");
        book.Add(ava, "Ava", StoryBook.Them, "Good morning.", false, 2, "08:00");
        book.Add(ava, "Ava", StoryBook.You, "Morning.", false, 2, "08:00");
        book.End(ava);
        Check(book.DropHistoryWithoutThem(ava) == 1 && book.Scenes(ava).Count == 1 && book.Scenes(ava)[0].Source == "live",
              "FB-78: a history page with none of her lines (the broken read) is let go; what was kept as you played stays");
        var again = new List<StoryBook.Line> { new() { Who = StoryBook.Them, Text = "You're awake. Good." }, new() { Who = StoryBook.You, Text = "Where am I?" } };
        Check(book.ImportPage(ava, "Ava", 0, again) && book.Scenes(ava)[0].Source == "history2" && book.Scenes(ava)[0].Lines.Select(l => l.Who).SequenceEqual(new[] { StoryBook.Them, StoryBook.You }),
              "...and read again: her lines and yours in turn, before what was kept as you played");
        var known = new List<StoryBook.Line> { new() { Who = StoryBook.Them, Text = "Good morning." }, new() { Who = StoryBook.You, Text = "Morning." } };
        Check(!book.ImportPage(ava, "Ava", 0, known), "a page with no day that's already kept from a day you played isn't added twice");
        book.ImportPage(ava, "Ava", 0, new List<StoryBook.Line> { new() { Who = StoryBook.You, Text = "Hm." }, new() { Who = StoryBook.You, Text = "Okay then." } });
        Check(book.DropHistoryWithoutThem(ava) == 0, "...and nothing good is let go, nor a page read since that's only yours (review PH-4: the repair runs once)");

        var phone = new Phone.PhoneBook(Path.Combine(dir, "phone.json"), seed: 3);
        phone.Add(ava, "Ava", 1, "", "your HUD");
        phone.MirrorEarlierStory(ava, new List<(bool, string, int, string, long)> { (false, "Where am I?", 0, "", 0), (false, "What's a pirate?", 0, "", 0) });
        phone.MirrorStory(ava, new List<(bool, string, int, string, long)> { (true, "Good morning.", 2, "08:00", 2 * 1440 + 480), (false, "Morning.", 2, "08:00", 2 * 1440 + 480) });
        Check(phone.DropUntimedStory(ava) == 2 && phone.Texts(ava).Select(t => t.Body).SequenceEqual(new[] { "Good morning.", "Morning." }),
              "FB-78: on her phone, the story lines with no time (the history's) go; those from your play stay");
        var mirrored = new List<(bool, string, int, string, long)> { (true, "You're awake. Good.", 0, "", 0), (false, "Where am I?", 0, "", 0),
                                                                  (true, "Good morning.", 2, "08:00", 2 * 1440 + 480), (false, "Morning.", 2, "08:00", 2 * 1440 + 480) };
        Check(phone.MirrorEarlierStory(ava, mirrored) == 2 && phone.Texts(ava).Select(t => (t.From, t.Body)).SequenceEqual(new[]
              { (Phone.PhoneBook.Them, "You're awake. Good."), (Phone.PhoneBook.You, "Where am I?"), (Phone.PhoneBook.Them, "Good morning."), (Phone.PhoneBook.You, "Morning.") }),
              "...and the history read again goes in first, hers and yours in turn");
        Check(rt.Contains("_story!.DropHistoryWithoutThem(id) is int broken && broken > 0") && rt.Contains("_phone?.DropUntimedStory(id)"),
              "the repair runs before the history is read again, once (only while a broken page is there)");
    }
}
