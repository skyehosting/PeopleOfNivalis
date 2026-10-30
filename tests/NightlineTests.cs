using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NivalisAIChat.Phone;

namespace NivalisAIChat.Tests;

/// <summary>Nightline (FB-71): the book (lines, reactions, comments, handles, photos, per save) and the planner's rolls.</summary>
internal static partial class Program
{
    private static void NightlineTests()
    {
        Console.WriteLine("-- Nightline: the city's social app (FB-71)");
        string dir = TestRoot.NewFolder("nightline");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "nightline.json");

        // Handles: theirs (made safe), or from their name; never two alike, never "you".
        Check(LineBook.Handle("Mara Voss") == "maravoss" && LineBook.Handle("@Rig.Must.Live!") == "rig.must.live" && LineBook.Handle("Zoë") == ""
              && LineBook.Handle("..a_b..") == "a_b" && LineBook.Handle("x") == "" && LineBook.Handle(new string('a', 40)).Length == 24,
            "a handle is lower case letters, digits, dots and underscores, 3 to 24 long, its @ and spaces dropped");
        var book = new LineBook(path) { IsMinor = id => id == "p_kid" };
        string kiko = book.SetHandle("p_kiko", "Kiko", "noodlequeen_kiko"), again = book.SetHandle("p_kiko", "Kiko", "other");
        string twin = book.SetHandle("p_kiko2", "Kiko", "noodlequeen_kiko");
        Check(kiko == "noodlequeen_kiko" && again == kiko && twin == "noodlequeen_kiko2" && book.HandleOf("p_mara", "Mara Voss") == "maravoss"
              && book.SetHandle("p_kid", "Pip", "pip") == "" && book.SetHandle("p_x", "You", "you") != "you",
            "their chosen handle is kept once and stays theirs; a taken one gets a number; never for a child, never 'you'");

        // Lines, newest first; a child never posts.
        string l1 = book.Post("p_kiko", "Kiko", "Rain special tonight.\nExtra egg!", "Kiko's Noodle Bar", 1000, 1, "16:40");
        string l2 = book.Post(LineBook.You, "You", "Down at the boardwalk.", "Seaside Boardwalk", 1010, 1, "16:50");
        string l3 = book.Post("p_mara", "Mara Voss", "Trawler horns all night.", "The Docks", 1020, 1, "17:00", photoComing: true);
        Check(l1.Length > 0 && l2.Length > 0 && l3.Length > 0 && book.Post("p_kid", "Pip", "hi", "", 1030, 1, "17:10") == "" && book.Post("p_mara", "Mara", "   ", "", 1, 1, "") == "",
            "a line is posted (never by a child, never empty)");
        var feed = book.Feed(10);
        Check(feed.Select(l => l.Id).SequenceEqual(new[] { l3, l2, l1 }) && feed[2].Text == "Rain special tonight. Extra egg!" && feed[2].Handle == "noodlequeen_kiko"
              && feed[1].Handle == "you" && feed[0].Developing,
            "the feed is newest first, one line each (breaks folded), with each poster's handle; a photo can be on its way");

        // Reactions: one each, swapped or taken back; never on their own line; never a child's.
        Check(book.React(l1, "p_mara", "Mara Voss", true, 1100, "Hungry") && book.React(l1, LineBook.You, "You", true, 1101)
              && !book.React(l1, "p_mara", "Mara Voss", true, 1102) && book.React(l1, "p_mara", "Mara Voss", false, 1103, "Overpriced")
              && !book.React(l1, "p_kiko", "Kiko", true, 1104) && !book.React(l1, "p_kid", "Pip", true, 1105),
            "a like is one per person, swapped to a dislike, never on your own line, never a child's");
        var k = book.Get(l1)!;
        Check(k.Reactions.Count == 2 && k.Reactions.Single(r => r.Who == "p_mara") is { Up: false, Why: "Overpriced" } && k.SeenBy.Contains("p_mara") && k.SeenBy.Contains(LineBook.You),
            "...the newest reaction stands with its reason, and reacting means they've seen it");
        Check(book.React(l1, LineBook.You, "You", null, 1106) && book.Get(l1)!.Reactions.Count == 1, "...and a like can be taken back");

        // Comments, with a reply; your news counts toward the badge.
        string c1 = book.AddComment(l2, "p_mara", "Mara Voss", "Save me a spot?", 1200);
        string c2 = book.AddComment(l2, LineBook.You, "You", "Always.", 1201, replyTo: c1);
        string c3 = book.AddComment(l2, "p_kiko", "Kiko", "Ha", 1202, replyTo: "no-such-comment");
        var mine = book.Get(l2)!;
        Check(c1.Length > 0 && mine.Comments.Count == 3 && mine.Comments[1].ReplyTo == c1 && mine.Comments[2].ReplyTo == "" && book.AddComment(l2, "p_kid", "Pip", "hi", 1) == "",
            "comments, a reply under the one it answers (an unknown one isn't), never a child's");
        book.React(l2, "p_kiko", "Kiko", true, 1203);
        Check(book.Unseen == 3, $"the badge counts others' reactions and comments on your lines ({book.Unseen})");
        book.SeenYours();
        Check(book.Unseen == 0, "...and clears when you've looked");

        // The 2026-10-20 audit: news taken away (your line deleted, someone forgotten) never hides news that comes after it.
        {
            var b2 = new LineBook(Path.Combine(dir, "nightline-badge.json"));
            string a = b2.Post(LineBook.You, "You", "First.", "", 2000, 2, "09:00"), b = b2.Post(LineBook.You, "You", "Second.", "", 2001, 2, "09:01");
            b2.React(a, "p_mara", "Mara Voss", true, 2002); b2.React(a, "p_kiko", "Kiko", true, 2003); b2.AddComment(a, "p_bo", "Bo", "Nice", 2004);
            b2.SeenYours();
            b2.Forget("p_bo");
            b2.React(b, "p_ida", "Ida", true, 2010);
            Check(b2.Unseen == 1, $"someone forgotten after you looked: the next reaction still shows on the badge ({b2.Unseen})");
            b2.SeenYours();
            b2.Forget("p_mara"); b2.Forget("p_kiko");
            b2.AddComment(b, "p_ida", "Ida", "Morning!", 2020);
            Check(b2.Unseen == 1, $"...however much was taken away ({b2.Unseen})");
            b2.Flush();
            string old = File.ReadAllText(Path.Combine(dir, "nightline-badge.json"));
            var j = System.Text.Json.Nodes.JsonNode.Parse(old)!.AsObject();
            j.Remove("YouSeenAt"); j.Remove("youSeenAt"); j["YouSeen"] = 0; j["youSeen"] = 0;
            File.WriteAllText(Path.Combine(dir, "nightline-badge-old.json"), j.ToJsonString());
            var b3 = new LineBook(Path.Combine(dir, "nightline-badge-old.json"));
            Check(b3.Unseen == 2, $"an older file's count is read once as where you'd got to (nothing seen: both are news, {b3.Unseen})");
        }

        // The author looking at who reacted: only what's new since their last look.
        var looked = book.AuthorLooks(l1);
        Check(looked.Count == 1 && looked[0].Who == "p_mara" && book.AuthorLooks(l1).Count == 0, "the author sees who reacted, once each");

        // Photos.
        book.PhotoDone(l3, "photo-1.png", "the docks at night, rain on the cranes");
        var withPhoto = book.Get(l3)!;
        Check(!withPhoto.Developing && withPhoto.Photo.Length > 0 && book.PhotoOf(withPhoto.Photo)!.Shows.StartsWith("the docks") && book.PhotoFiles().Contains("photo-1.png"),
            "a photo is kept with its line and what it shows");
        string l4 = book.Post("p_mara", "Mara Voss", "Another", "", 1300, 1, "", photoComing: true);
        book.PhotoDone(l4, null, "");
        book.PhotoDone(l4, "../evil.png", "x");
        Check(!book.Get(l4)!.Developing && book.Get(l4)!.Photo == "", "a photo that couldn't be made leaves the line text-only (and a bad file name is refused)");

        // Saved and read back.
        book.Close();
        var back = new LineBook(path);
        Check(back.Feed(10).Count == 4 && back.Get(l1)!.Reactions.Count == 1 && back.HandleOf("p_kiko", "Kiko") == "noodlequeen_kiko" && back.Get(l3)!.Photo.Length > 0,
            "everything is saved and read back");

        // Forgetting someone, and hand-edited files.
        var files = back.Forget("p_mara");
        Check(files.SequenceEqual(new[] { "photo-1.png" }) && back.Feed(10).All(l => l.Author != "p_mara") && back.Get(l2)!.Comments.All(c => c.Who != "p_mara")
              && back.Get(l1)!.Reactions.Count == 0 && !back.HasHandle("p_mara"),
            "forgetting someone takes their lines, reactions, comments, handle and photos");
        back.Close();
        string odd = Path.Combine(dir, "odd.json");
        File.WriteAllText(odd, "{\"Lines\":[null,{\"Id\":\"x\",\"Author\":\"p\",\"Reactions\":[null],\"Comments\":null,\"SeenBy\":[null]}],\"Handles\":{\"p\":\"BAD HANDLE\"},\"Photos\":{\"q\":{\"File\":\"../x\"}}}");
        var oddBook = new LineBook(odd);
        Check(oddBook.Feed(5).Count == 1 && oddBook.Feed(5)[0].Reactions.Count == 0 && !oddBook.HasHandle("p") && oddBook.PhotoFiles().Count == 0,
            "a hand-edited file with nulls, a bad handle and a bad photo file loads clean");
        oddBook.Close();

        // The oldest go past the cap, their photos with them.
        var many = new LineBook(Path.Combine(dir, "many.json"));
        string first = many.Post("p_a", "A", "first", "", 0, 0, "", photoComing: true);
        many.PhotoDone(first, "old.png", "x");
        for (int i = 1; i <= LineBook.MaxLines; i++) many.Post("p_a", "A", "line " + i, "", i, 0, "");
        Check(many.Count == LineBook.MaxLines && many.Get(first) == null && many.TakeTrimmedPhotos().SequenceEqual(new[] { "old.png" }),
            $"at most {LineBook.MaxLines} lines: the oldest go, and their photo files are given back to delete");
        many.Close();

        // The planner.
        var rng = new Random(5);
        var gaps = Enumerable.Range(0, 300).Select(_ => LinePlan.NextGap(rng, 120, 30)).ToList();
        Check(gaps.Min() >= 30 && gaps.Max() <= 120 && gaps.Distinct().Count() > 30 && LinePlan.NextGap(rng, 0, 0) == 5,
            "someone opens the app every 30 to 120 game minutes, at random (either order; never under 5)");
        int posts = 0, scrolls = 0, both = 0;
        for (int i = 0; i < 3000; i++) { var (p, s) = LinePlan.Choose(rng, 50, 50, 25); if (p && s) both++; else if (p) posts++; else if (s) scrolls++; }
        Check(Math.Abs(posts - 1200) < 150 && Math.Abs(scrolls - 1200) < 150 && Math.Abs(both - 600) < 120 && LinePlan.Choose(rng, 0, 0, 0) == (false, false),
            $"post, scroll or both by their weights ({posts}/{scrolls}/{both}); all at 0: nothing");
        var lines = Enumerable.Range(0, 20).Select(i => ("l" + i, i == 1 ? "me" : "p" + i)).ToList();
        int firstSeen = 0, deep = 0, reacts = 0, comments = 0, ownSeen = 0, total = 0;
        for (int i = 0; i < 2000; i++)
        {
            var seen = LinePlan.Scroll(rng, lines, "me", id => id == "l2", 50, 50, 70);
            if (seen.Count > 0 && seen[0].Id == "l0") firstSeen++;
            if (seen.Count >= 4) deep++;
            ownSeen += seen.Count(s => s.Id is "l1" or "l2");
            reacts += seen.Count(s => s.React); comments += seen.Count(s => s.Comment); total += seen.Count;
        }
        Check(firstSeen == 2000 && ownSeen == 0 && deep > 200 && deep < 1200 && Math.Abs(reacts - total / 2) < total / 8 && Math.Abs(comments - total / 2) < total / 8,
            $"scrolling starts at the newest (always seen), passes their own and already-seen lines, sees fewer further down ({deep}/2000 went 4 deep), and rolls 50/50 for each");
        Check(LinePlan.Scroll(rng, lines, "me", _ => false, 50, 50, 100).Count == LinePlan.MaxSeenInOneGo && LinePlan.Scroll(rng, lines, "me", _ => false, 50, 50, 0).Count == 1,
            $"...at most {LinePlan.MaxSeenInOneGo} in one go; with no falloff left, just the newest");
        Check(LinePlan.Care(new[] { "vain", "insecure" }) > 0.8 && LinePlan.Care(new[] { "aloof", "private" }) < 0.2 && LinePlan.Care(Array.Empty<string>()) == 0.5,
            "how much someone cares what others think comes from who they are");
        int lookVain = 0, lookAloof = 0;
        for (int i = 0; i < 2000; i++) { if (LinePlan.LooksAtReactions(rng, 50, 0.9)) lookVain++; if (LinePlan.LooksAtReactions(rng, 50, 0.1)) lookAloof++; }
        Check(lookVain > 1200 && lookAloof < 500 && !LinePlan.LooksAtReactions(rng, 0, 1), $"the vain check who reacted far more often than the aloof ({lookVain} vs {lookAloof})");
        Check(LinePlan.Feeling(10) == "you can't stand them" && LinePlan.Feeling(50) == "you're neutral about them" && LinePlan.Feeling(90).Contains("favourite") && LinePlan.Feeling(-1) == "you don't know them",
            "how they feel about the poster, in words for the AI");
    }
}
