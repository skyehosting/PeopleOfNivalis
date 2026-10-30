using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Phone;
using NivalisAIChat.Social;

namespace NivalisAIChat.Tests;

/// <summary>The phone (the author's request, 2026-10-19): numbers, texts, the signal, the wallpaper (GIF, BMP), the prompts.</summary>
internal static partial class Program
{
    private static void PhoneTests()
    {
        Console.WriteLine("-- the phone: numbers and texts, per save");
        string dir = Path.Combine(TestRoot.Folder, "phone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "phone.json");
        var book = new PhoneBook(path, seed: 7);
        string mine = book.MyNumber;
        Check(PhoneBook.IsNumber(mine) && mine == book.MyNumber, $"your own number is xxx-xxxx and stays the same ({mine})");
        var numbers = Enumerable.Range(0, 400).Select(i => book.NumberOf("npc" + i)).ToList();
        Check(numbers.All(PhoneBook.IsNumber) && numbers.Distinct().Count() == 400 && !numbers.Contains(mine), "everyone's number is xxx-xxxx, none the same, none yours");
        Check(numbers.Select((n, i) => n[..3] == PhoneBook.AreaCodeOf("npc" + i)).All(x => x) && PhoneBook.AreaCodes.Contains(mine[..3])
              && numbers.Select(n => n[..3]).Distinct().Count() == 2, "the first three digits are one of the city's two area codes, always the same one for each person (yours too)");
        var other = new PhoneBook(Path.Combine(dir, "other.json"), seed: 99);
        Check(other.NumberOf("npc1")[..3] == numbers[1][..3] && other.NumberOf("npc1") != numbers[1], "in another save: the same area code, the last four random again");
        other.Close();
        Check(!PhoneBook.IsNumber("012-3456") && !PhoneBook.IsNumber("123-4567") && !PhoneBook.IsNumber("2345678") && PhoneBook.IsNumber("482-1937"), "no number starts with 0 or 1; the dash is where it goes");
        string maraNumber = book.NumberOf("mara");
        var mara = book.Add("mara", "Mara Quill", 9, "8:10 PM", "Meridian Market");
        Check(mara != null && mara.Number == maraNumber, "the number she said in the chat is the one saved (made once, before she gave it)");
        Check(book.Add("mara", "Mara Q.", 10, "", "")!.Number == maraNumber && book.Get("mara")!.Name == "Mara Q." && book.Count == 1, "giving it again keeps her one contact (a new name is kept)");

        book.IsMinor = id => id == "kid";
        Check(book.Add("kid", "Pip", 1, "", "") == null && book.NumberOf("kid") == "" && book.Send("kid", "hi", 1, "", 1, true) == null, "never anyone under 18: no number, no contact, no texts");
        Check(book.Send("dov", "hello?", 1, "", 1, true) == null, "no texts to someone who never gave you their number");
        Check(book.Send("mara", "   ", 1, "", 1, true) == null, "an empty text isn't sent");

        var t1 = book.Send("mara", "omw. 10 minutes", 12, "9:40 PM", 100, signal: true);
        Check(t1!.State == PhoneBook.Sending, "with signal, your text is on its way (sending)");
        Check(book.MarkSent("mara") && book.Texts("mara")[0].State == PhoneBook.Sent && !book.MarkSent("mara"), "then it's sent");
        book.MarkDelivered("mara");
        Check(book.Texts("mara")[0].State == PhoneBook.Delivered, "then it reaches their phone (delivered)");
        book.Receive("mara", "ok!! grabbing the window seat", 12, "9:41 PM", 101, open: false);
        var texts = book.Texts("mara");
        Check(texts[0].State == PhoneBook.Read && texts[1].From == PhoneBook.Them && book.Unread("mara") == 1 && book.UnreadTotal() == 1,
            "her answer means she read yours; it's unread until you open it");
        book.MarkRead("mara");
        Check(book.UnreadTotal() == 0, "opening the conversation reads it");
        var waiting = book.Send("mara", "you were right about the light", 12, "11:19 PM", 200, signal: false);
        Check(waiting!.State == PhoneBook.Waiting && book.AnyWaiting(), "no bars: the text waits");
        var went = book.SendWaiting();
        Check(went.SequenceEqual(new[] { "mara" }) && book.Texts("mara").Last().State == PhoneBook.Sending && !book.AnyWaiting() && book.StillSending().Contains("mara"), "a bar again: it goes on its way");
        book.MarkSent("mara"); book.MarkDelivered("mara");
        book.SetPlan("mara", "later", 250, -1);
        Check(book.PlanOf("mara") == ("later", 250, -1, -1) && book.Planned().Contains("mara"), "what she'll do with it is kept (FB-70)");
        book.MarkSeen("mara", 260);
        var seenText = book.Texts("mara").Last();
        Check(seenText.State == PhoneBook.Read && seenText.SeenAt == 260 && book.PlanOf("mara").lookedAt == 260, "she looked at her phone: seen, and when");
        book.SetPlan("mara", "", -1, -1);
        Check(!book.Planned().Contains("mara") && book.PlanOf("mara").lookedAt == -1, "a plan done is cleared");
        Check(book.Conversations().Single().id == "mara" && book.Conversations().Single().last.Body.Contains("light"), "the conversation list shows her last text");
        book.Receive("mara", new string('a', 900), 12, "", 201, open: true);
        Check(book.Texts("mara").Last().Body.Length == PhoneBook.MaxBody && book.Unread("mara") == 0, "a long text is cut; one that comes while it's open isn't unread");
        book.Receive("mara", "listen to this", 12, "", 202, open: true);
        Check(book.AttachVoice("mara", "listen to this", "note-abc.wav", 3.2f, "123") && book.Texts("mara").Last().Voice == "note-abc.wav"
              && !book.AttachVoice("mara", "listen to this", "..\\evil.wav", 1, "") && !book.AttachVoice("mara", "nope", "note-x.wav", 1, ""), "a voice note goes with her text (a plain file name only)");
        for (int i = 0; i < PhoneBook.MaxTexts + 30; i++) book.Receive("mara", "x" + i, 13, "", 300 + i, open: true);
        Check(book.Texts("mara").Count == PhoneBook.MaxTexts, $"a conversation keeps its last {PhoneBook.MaxTexts} texts");
        book.Close();

        var again = new PhoneBook(path);
        Check(again.MyNumber == mine && again.Get("mara")?.Number == maraNumber && again.Texts("mara").Count == PhoneBook.MaxTexts && again.NumberOf("npc5") == numbers[5],
            "kept with the save: read back the same");
        again.Forget("mara");
        Check(again.Get("mara") == null && again.Texts("mara").Count == 0 && again.NumberOf("mara") != maraNumber, "/resetnpc or under 18: their number and texts go");
        again.Close();
        File.WriteAllText(path, "{\"MyNumber\":null,\"Contacts\":{\"a\":null,\"b\":{\"Name\":null,\"Number\":\"999-0000\"}},\"Threads\":{\"b\":{\"Texts\":[null,{\"From\":\"x\",\"Body\":null}],\"Unread\":9}},\"Numbers\":{\"c\":\"bad\"}}");
        var repaired = new PhoneBook(path);
        Check(repaired.Contacts().Count == 1 && repaired.Texts("b").Count == 1 && repaired.Unread("b") == 1 && PhoneBook.IsNumber(repaired.MyNumber) && repaired.NumberOf("c") != "bad",
            "a hand-edited file: nothing null or unreadable is read back");
        repaired.Close();

        Console.WriteLine("-- the phone: the signal");
        var places = Reception.ParsePlaces("Docks=2, Lowtown = 3; Sewer=0, bad, =4, Pier=9");
        Check(places.Count == 4 && places[2] == ("Sewer", 0) && places[3] == ("Pier", 4), "the place list is read (bad entries skipped, bars 0-4)");
        Check(Reception.WritePlaces(new[] { ("Docks", 2), ("docks", 1), ("Neon, Ramen", 5) }) == "Docks=1, Neon  Ramen=4", "written back: one per place, newest value, no separators in a name");
        var costs = Reception.ParseCosts("rain=2, blizzard=4, hail=3, fog=x");
        Check(costs.Rain == 2 && costs.Blizzard == 4 && costs.Thunder == 1 && costs.Fog == 0 && Reception.ParseCosts(costs.ToString()).Rain == 2, "weather costs are read (anything else keeps its default)");
        long calm = Enumerable.Range(0, 200).Select(i => (long)i * 30).First(m => !Reception.Drifts(m));
        var r = new ReceptionInputs { Minute = calm, Places = places, Costs = new WeatherCosts() };
        Check(Reception.Bars(r).bars == 4, "outdoors in good weather: full bars");
        Check(Reception.Bars(new ReceptionInputs { Simulate = false, Blizzard = 1, Minute = calm }).bars == 4, "signal not simulated: always full");
        Check(Reception.Bars(new ReceptionInputs { AtHome = true, Blizzard = 1, Minute = calm }) == (4, "home"), "at home: your own wifi");
        r.Location = "Lowtown"; r.Indoors = true;
        Check(Reception.Bars(r).bars == 3 && Reception.Bars(r).why.Contains("indoors"), "indoors costs a bar; Lowtown's cap is no lower");
        r.Venue = "Docks Fish Bar";
        Check(Reception.Bars(r).bars == 2, "a place on the list caps it (part of a venue's name is enough)");
        r.Blizzard = 1; r.Thunder = 1;
        Check(Reception.Bars(r) is { bars: 0 } b0 && b0.why.Contains("blizzard"), "a blizzard with thunder on top: no bars");
        r.WeatherMatters = false;
        Check(Reception.Bars(r).bars == 2, "weather switched off: only the place counts");
        r.PlacesMatter = false; r.WeatherMatters = true; r.Blizzard = 0; r.Thunder = 0; r.Rain = 1;
        Check(Reception.Bars(r).bars == 3, "places switched off: only the weather (pouring rain, 1)");
        long drift = Enumerable.Range(0, 400).Select(i => (long)i * 30).First(m => Reception.Drifts(m));
        Check(Reception.Bars(new ReceptionInputs { Minute = drift }).bars == 3 && Reception.Bars(new ReceptionInputs { Minute = drift, Places = new[] { ("x", 1) }, Location = "x" }).bars == 1,
            "now and then a bar comes and goes (never from one bar to none)");
        int drifts = Enumerable.Range(0, 3000).Count(i => Reception.Drifts(i * 30L));
        Check(drifts is > 300 and < 700, $"...about one half hour in six ({drifts} of 3000)");
        Check(Reception.SendSeconds(0) < 0 && Reception.SendSeconds(1) > Reception.SendSeconds(3), "no bars: nothing goes; one bar: slow");

        Console.WriteLine("-- the phone: the wallpaper (GIF, BMP, links)");
        var pal = new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255 }; // black, red, green, blue
        var gif = Gif(4, 4, pal, new[]
        {
            (new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, 0, 0, 4, 4, 0, -1),
            (new byte[] { 2, 3, 3, 2 }, 1, 1, 2, 2, 2, 3),   // a 2x2 in the middle, index 3 transparent; then cleared (disposal 2)
            (new byte[] { 3 }, 0, 0, 1, 1, 1, -1),
        }, delay: 0);
        var img = GifDecoder.Decode(gif);
        Check(img.Width == 4 && img.Height == 4 && img.Frames.Count == 3 && img.Frames.All(f => f.DelayMs == 100), "a GIF's frames, each shown whole (no delay given: 0.1 s, as browsers do)");
        byte[] Px(GifDecoder.Frame f, int x, int y) => f.Pixels.Skip((y * 4 + x) * 4).Take(4).ToArray();
        Check(Px(img.Frames[0], 3, 3).SequenceEqual(new byte[] { 255, 0, 0, 255 }), "frame 1 is all red");
        Check(Px(img.Frames[1], 1, 1).SequenceEqual(new byte[] { 0, 255, 0, 255 }) && Px(img.Frames[1], 2, 1).SequenceEqual(new byte[] { 255, 0, 0, 255 }) && Px(img.Frames[1], 0, 0)[0] == 255,
            "frame 2 is drawn over frame 1 (its transparent pixel shows what was there)");
        Check(Px(img.Frames[2], 1, 1)[3] == 0 && Px(img.Frames[2], 0, 0).SequenceEqual(new byte[] { 0, 0, 255, 255 }) && Px(img.Frames[2], 3, 3)[0] == 255,
            "frame 2 was cleared after it (disposal 2), the rest kept");
        var up = GifDecoder.Decode(gif, bottomUp: true);
        Check(up.Frames[2].Pixels.Skip(12 * 4).Take(4).SequenceEqual(new byte[] { 0, 0, 255, 255 }), "rows bottom first for Unity, when asked");
        var big = Gif(64, 32, pal, new[] { (Enumerable.Range(0, 64 * 32).Select(i => (byte)(i % 64 < 32 ? 1 : 3)).ToArray(), 0, 0, 64, 32, 0, -1) }, delay: 5);
        var small = GifDecoder.Decode(big, maxSide: 32);
        Check(small.Width == 32 && small.Height == 16 && small.Frames[0].DelayMs == 50 && small.Frames[0].Pixels[0] == 255 && small.Frames[0].Pixels[(31) * 4 + 2] == 255,
            "a big GIF is shrunk to fit (its shape kept, each half its colour)");
        Check(Throws(() => GifDecoder.Decode(Encoding.ASCII.GetBytes("GIF89a"))) && Throws(() => GifDecoder.Decode(new byte[] { 1, 2, 3 })), "a broken file is an error, not a crash");
        Check(Throws(() => GifDecoder.Decode(gif, maxFrames: 2)), "too many frames is refused");
        bool truncatedOk; try { GifDecoder.Decode(gif.Take(gif.Length - 6).ToArray()); truncatedOk = true; } catch (FormatException) { truncatedOk = true; } catch { truncatedOk = false; }
        Check(truncatedOk, "a GIF cut short reads what it can or says it can't (never anything but a FormatException)");
        var random = new Random(3);
        int crashes = 0;
        for (int i = 0; i < 300; i++)
        {
            var junk = (byte[])gif.Clone();
            for (int k = 0; k < 6; k++) junk[13 + random.Next(junk.Length - 13)] = (byte)random.Next(256);
            try { GifDecoder.Decode(junk); } catch (FormatException) { } catch (Exception e) { crashes++; if (crashes == 1) Console.WriteLine("    first crash: " + e.GetType().Name + " " + e.Message); }
        }
        Check(crashes == 0, $"300 damaged GIFs: none crash ({crashes})");

        var bmp = Bmp24(2, 2, new byte[,] { { 255, 0, 0 }, { 0, 255, 0 }, { 0, 0, 255 }, { 255, 255, 255 } });
        var (bw, bh, rgba) = BmpDecoder.Decode(bmp);
        Check(bw == 2 && bh == 2 && rgba.Take(4).SequenceEqual(new byte[] { 0, 0, 255, 255 }) && rgba.Skip(4).Take(4).SequenceEqual(new byte[] { 255, 255, 255, 255 })
              && rgba.Skip(8).Take(4).SequenceEqual(new byte[] { 255, 0, 0, 255 }), "a 24-bit BMP is read (bottom row first, for Unity)");
        Check(Wallpaper.Sniff(bmp) == "bmp" && Wallpaper.Sniff(gif) == "gif" && Wallpaper.Sniff(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 }) == "png"
              && Wallpaper.Sniff(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }) == "jpg" && Wallpaper.Sniff(Encoding.ASCII.GetBytes("RIFF0000WEBPVP8 ")) == "webp"
              && Wallpaper.Sniff(Encoding.ASCII.GetBytes("<html>")) == "", "what a download is comes from its first bytes, not the link's ending");
        Check(!Wallpaper.Readable("webp") && Wallpaper.Unreadable("webp").Contains("WEBP") && Wallpaper.Unreadable("").Contains("PNG"), "WEBP is named and refused");
        Check(Wallpaper.Check("https://i.imgur.com/x.gif").uri != null && Wallpaper.Check("file:///C:/x.png").uri == null && Wallpaper.Check("C:\\x.png").uri == null
              && Wallpaper.Check("").error.Length > 0 && Wallpaper.Check("ftp://a/b.png").uri == null, "only web links (http, https): never a file on the PC");
        string wdir = Path.Combine(dir, "wallpaper");
        Check(Wallpaper.Load(wdir) == null, "no wallpaper kept: the default");
        Wallpaper.Keep(wdir, gif, "gif", "https://i.imgur.com/x.gif");
        var kept = Wallpaper.Load(wdir);
        Check(kept is { } k1 && k1.info.Kind == "gif" && k1.data.SequenceEqual(gif) && k1.info.Url.EndsWith("x.gif"), "a downloaded wallpaper is kept on the PC with its link");
        Wallpaper.Keep(wdir, bmp, "bmp", "https://a/b.bmp");
        Check(Wallpaper.Load(wdir)?.info.Kind == "bmp" && !File.Exists(Path.Combine(wdir, "wallpaper.gif")), "a new one replaces the old");
        File.WriteAllText(Path.Combine(wdir, "wallpaper.json"), "{\"File\":\"..\\\\phone.json\",\"Kind\":\"png\"}");
        Check(Wallpaper.Load(wdir) == null, "a record pointing outside its folder isn't followed");
        Wallpaper.Forget(wdir);
        Check(Wallpaper.Load(wdir) == null, "Default: the kept one goes");

        Console.WriteLine("-- the phone: texts in their voice, and the number in a chat");
        var (msgs, aff, mood, _) = Texting.Read("ok!! grabbing the window seat 🍜\n*smiles* my treat :)\n[[affinity: +3]]\n[[mood: happy]]\nthird\nfourth", "Mara Quill", "");
        Check(msgs.SequenceEqual(new[] { "ok!! grabbing the window seat", "my treat :)", "third" }) && aff == 3 && mood == "happy",
            $"their answer: short texts, no emoji, no actions, notes read, at most three ({string.Join(" | ", msgs)})");
        var rec = new NpcRecord { Id = "mara", Persona = new Persona { Name = "Mara Quill", Pronouns = "she/her" } };
        rec.State.PlayerName = "Ash";
        rec.State.Romance = Romance.Dating;
        Check(Texting.Tag(rec.State) == "Partner" && Texting.Tag(new NpcState { Familiarity = "friend" }) == "Friend" && Texting.Tag(new NpcState()) == "", "the contact's tag: Partner, Friend or none");
        var snap = new GameSnapshot { GameDay = 12, Hour = 21, Minute = 41, DayOfWeek = "Thursday", PlayerGender = "woman" };
        var thread = new List<PhoneBook.Text>
        {
            new() { From = PhoneBook.Them, Body = "are you still up?" },
            new() { From = PhoneBook.You, Body = "yes" }, new() { From = PhoneBook.You, Body = "why" },
        };
        var req = Texting.Request(rec, snap, thread, first: false, 0.5, "woman", 900);
        Check(req.Purpose == "text" && req.Messages.Count == 2 && req.Messages[1].Role == "user" && req.Messages[1].Content == "yes\nwhy" && req.Messages[0].Role == "assistant",
            "your texts in a row are one turn; theirs are theirs");
        string sys = string.Join("\n", req.System);
        Check(sys.Contains("[TEXTING]") && sys.Contains("Ash") && sys.Contains("together (dating)") && sys.Contains("no emoji") && sys.Contains("she/her"), "they text as themselves, knowing who you are to them (and your pronouns)");
        var firstReq = Texting.Request(rec, snap, thread, first: true, 0.5, "woman", 900);
        Check(firstReq.Purpose == "text first" && firstReq.Background && firstReq.Messages[^1].Content.Contains("text them first"), "a text they send first is asked as such (counted like a greeting)");
        Check(UsageModel.YoursAi.Contains("text") && UsageModel.ResidentsAi.Contains("text first"), "both are in the usage optimizer's lists (yours; the residents' kinds, by DynamicTextWeight)");
        Check(Texting.SceneLine(12, "9:41 PM", new[] { "yes" }, new[] { "ramen?" }, false).Contains("they texted you \"yes\"; you texted back \"ramen?\""), "what was texted is kept in their record, in their words");

        Check(ReplyParser.Parse("Sure. [[gave_number]]", "Mara", "").GaveNumber && !ReplyParser.Parse("No way.", "Mara", "").GaveNumber, "[[gave_number]] is read");
        Features.Phone = true;
        Check(Prompts.ReplySchema().ToJsonString().Contains("gaveNumber"), "the reply's shape asks whether they gave their number");
        Features.Phone = false;
        Check(!Prompts.ReplySchema().ToJsonString().Contains("gaveNumber"), "...not with the phone off");
        Features.Phone = true;
        var chatSnap = new GameSnapshot { MyNumber = "482-1937" };
        string sit = Prompts.SituationBlock(rec, chatSnap);
        Check(sit.Contains("[YOUR PHONE]") && sit.Contains("482-1937") && sit.Contains("you decide"), "in a chat they know their number and that giving it is theirs to decide");
        chatSnap.HasYourNumber = true; chatSnap.RecentTexts.Add("Day 12, they: are you still up?");
        Check(Prompts.SituationBlock(rec, chatSnap).Contains("are you still up?"), "and once you have it, the last texts are in the chat too");
        Check(!Prompts.SituationBlock(rec, new GameSnapshot()).Contains("[YOUR PHONE]"), "no number made (phone off): nothing about it");
    }


    private static string PhoneTestsHere([CallerFilePath] string here = "") => here;

    private static void PhoneLifeTests()
    {
        Console.WriteLine("-- the phone: texting like real life (FB-70), the battery, voice notes");
        var odds = new ReplyOdds();
        Check(TextLife.Chances(odds, 50, false, false) == (10, 10), "at neutral, the settings' chances as they are");
        var (cn, cl) = TextLife.Chances(odds, 100, false, false);
        var (fn, fl) = TextLife.Chances(odds, 0, false, false);
        Check(cn < 1.5 && cl < 1.5 && fn > 25 && fl > 25, $"close friends almost always answer; people who dislike you often don't ({cn:0.#}/{cl:0.#} vs {fn:0.#}/{fl:0.#})");
        Check(TextLife.Chances(odds, 50, true, false).never < 5 && TextLife.Chances(odds, 50, false, true).never < 3, "a partner, or a conversation already going: they keep answering");
        Check(TextLife.Chances(new ReplyOdds { RealLife = false }, 0, false, false) == (0, 0), "real-life replies off: they always answer");
        Check(TextLife.Chances(new ReplyOdds { NeverSeesPct = 50, LaterPct = 50 }, 0, false, false) is var (an, al) && an + al <= 90.001, "the two together never take every text");
        var rng = new Random(5);
        int never = 0, later = 0;
        for (int i = 0; i < 4000; i++) { var o = TextLife.Decide(rng, odds, 50, false, false); if (o == ReplyOutcome.NeverSees) never++; else if (o == ReplyOutcome.AnswersLater) later++; }
        Check(never is > 300 and < 500 && later is > 300 and < 500, $"about one in ten each at neutral ({never}, {later} of 4000)");
        Check(Enumerable.Range(0, 200).All(_ => TextLife.SeenAfter(rng, 14, 0, momentum: true) <= 2), "mid-conversation they look within a minute or two");
        Check(Enumerable.Range(0, 200).All(_ => { int a = TextLife.SeenAfter(rng, 3, 0, false); return a >= 240 && a <= 330; }), "at 3 AM they're asleep: they see it between 7 and 8:30");
        Check(Enumerable.Range(0, 400).All(_ => { int a = TextLife.SeenAfter(rng, 23, 50, false); int t = (23 * 60 + 50 + a) % 1440; return !TextLife.Asleep(t / 60); }), "a look never lands in the night");
        // FB-78: seen first, then 1 to 10 game minutes before they start typing (Ava too).
        var waits = Enumerable.Range(0, 500).Select(_ => TextLife.AnswerAfter(rng)).ToList();
        Check(waits.All(w => w >= 1 && w <= 10) && waits.Distinct().Count() == 10 && TextLife.AnswerAfter(rng, realLife: false) == 0,
              "FB-78: answering right away still waits 1 to 10 game minutes after seen (any of them); none without real life");
        var src = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(PhoneTestsHere()))!, "src", "ModRuntime.Phone.cs"));
        Check(src.Contains("if (plan == \"answers\" && (lookAt >= now || answerAt >= now))") && src.Contains("if (lookAt < 0) { book.MarkSeen(id, now);")
              && src.Contains("book.SetPlan(id, \"answers\", now, now + TextLife.AnswerAfter(PhoneRng, OddsNow().RealLife))"),
              "...Ava sees it at once and waits too; a text sent meanwhile is answered with the rest (not rolled again)");
        // FB-79: a text to you takes as long as your signal says; with none it waits for a bar.
        Check(Reception.ReceiveSeconds(0) < 0 && Reception.ReceiveSeconds(1) > Reception.ReceiveSeconds(2) && Reception.ReceiveSeconds(2) > Reception.ReceiveSeconds(3)
              && Reception.ReceiveSeconds(3) > Reception.ReceiveSeconds(4) && Reception.ReceiveSeconds(4) <= 1f,
              "FB-79: fewer bars, longer for a text to reach you; none, it waits");
        Check(src.Contains("if (id == _avaId || !_cfg.PhoneSignal.Value) { DeliverText(id, body, generation, readUpTo); return; }")
              && src.Contains("if (_bars <= 0) { foreach (var x in Inbox) x.Held = true; return; }") && src.Contains("DeliverInboxQuietly(); // FB-79")
              && src.Contains("going.Voice = (file, seconds, wave); lock (PendingVoiceFiles) PendingVoiceFiles.Add(file);"),
              "...Ava (in your HUD) and no simulated signal come at once; held texts come in when a bar does, in order, with their voice notes; closing never loses one");
        // FB-78: her texts never repeat what she's said (a story line, a text she sent) word for word.
        var fresh = Texting.Fresh(new[] { "Hello. It's me again.", "Just so you're aware, there's something wrong with the lights.",
                                          "Good call checking in. Just so you're aware: there's something wrong with the lights!", "Yes, Ava." },
                                  new[] { "Just so you're aware - there's something wrong with the lights." });
        Check(fresh.SequenceEqual(new[] { "Hello. It's me again.", "Good call checking in.", "Yes, Ava." }),
              "FB-78: a sentence she's said already (however it's punctuated) is taken out, a text left empty goes, short words may come again");
        Check(Texting.AvaRequest("Ava", "Raine", "", new List<string>(), new List<StoryBook.Scene>(), new GameSnapshot(), new List<PhoneBook.Text>(), false, 500, "", false)
              .System.Any(s => s.Contains("never repeat a line from those conversations")), "...and she's told to say something new");
        Check(TextLife.SeenAfter(rng, 3, 0, false, realLife: false) == 0, "off: at once");
        Check(Enumerable.Range(0, 300).Count(_ => TextLife.Stall(rng, 1) > 0) is > 60 and < 150 && Enumerable.Range(0, 300).All(_ => TextLife.Stall(rng, 4) == 0f), "one bar holds a text now and then; a good signal never");
        string Clock(long m) => $"{m / 60 % 24}:{m % 60:00}";
        Check(TextLife.Note(ReplyOutcome.AnswersLater, 600, 610, 800, true, Clock).Contains("texted again") && TextLife.Note(ReplyOutcome.AnswersLater, 600, 610, 800, false, Clock).Contains("own time")
              && TextLife.Note(ReplyOutcome.Answers, 600, -1, 700, false, Clock).Contains("hadn't looked") && TextLife.Note(ReplyOutcome.Answers, 600, -1, 610, false, Clock) == "",
            "what they're told when they answer: that they saw it and didn't answer (and you texted again), or hadn't looked");

        Check(Battery.Percent(8, 0) == 100 && Battery.Percent(22, 0) == 30 && Battery.Percent(7, 59) == 1 && Battery.Percent(2, 0) is > 1 and < 30
              && Battery.Percent(15, 0) == 65, "the battery: full at 8 AM, 30% at 10 PM, 1% at 7:59, and never dies");
        Check(Enumerable.Range(0, 1440).All(m => Battery.Percent(m / 60, m % 60) is >= 1 and <= 100)
              && Enumerable.Range(8 * 60 + 1, 23 * 60).Select(m => Battery.Percent(m / 60 % 24, m % 60)).Zip(Enumerable.Range(8 * 60, 23 * 60).Select(m => Battery.Percent(m / 60 % 24, m % 60))).All(x => x.First <= x.Second),
            "...it only runs down from 8 AM to 7:59 AM");

        var tone = Enumerable.Range(0, 24000).Select(i => (float)(Math.Sin(i * 0.05) * (i < 12000 ? 0.1 : 0.8))).ToArray();
        var wav = VoiceNote.ToWav(tone, 24000, 1);
        var (back, rate, ch) = VoiceNote.FromWav(wav);
        Check(rate == 24000 && ch == 1 && back.Length == tone.Length && Math.Abs(back[500] - tone[500]) < 0.001, "a voice note is kept as a WAV and read back the same");
        var wave = VoiceNote.Wave(back);
        Check(wave.Length == VoiceNote.Bars && wave.All(h => h is >= 1 and <= VoiceNote.MaxHeight) && wave.Take(10).Max() < wave.Skip(14).Min() && wave.Max() == VoiceNote.MaxHeight,
            $"its wave is the audio's own: quiet half low, loud half high ({VoiceNote.WaveText(wave)})");
        Check(VoiceNote.ParseWave(VoiceNote.WaveText(wave)).SequenceEqual(wave) && VoiceNote.Length(6.2f) == "0:07" && VoiceNote.Length(65) == "1:05", "kept with the text, and its length as the bar shows it");
        Check(Throws(() => VoiceNote.FromWav(new byte[50])) && !VoiceNote.SafeName("../x.wav") && !VoiceNote.SafeName("C:\\x.wav") && VoiceNote.SafeName("note-0a1b.wav"), "only its own files are read back");
        Check(!VoiceNote.Attach(rng, false, 100, "hey") && !VoiceNote.Attach(rng, true, 100, new string('a', 400)) && VoiceNote.Attach(rng, true, 100, "ok you have to hear this"),
            "a voice note only when they're on, and for a short text");

        // Deleting texts (the contact stays; what they remember is in their own record) and the newest window.
        var del = new PhoneBook(Path.Combine(TestRoot.Folder, "del-" + Guid.NewGuid().ToString("N") + ".json"), seed: 3);
        del.Add("m", "Mara", 1, "", "");
        for (int i = 0; i < 50; i++) del.Receive("m", "text " + i, 1, "", 100 + i, open: true);
        del.Receive("m", "with a note", 1, "", 200, open: true);
        del.AttachVoice("m", "with a note", "note-1.wav", 2, "12");
        var (win, first) = del.Newest("m", 40);
        Check(win.Count == 40 && first == 11 && win[^1].Body == "with a note", "a long conversation draws only its newest 40 (Show earlier for the rest)");
        Check(del.DeleteText("m", 50, "with a note") == "note-1.wav" && del.TextCount("m") == 50 && del.Has("m"), "deleting a text gives back its voice note's file to remove; the contact stays");
        Check(del.DeleteText("m", 3, "not what's there") == "" && del.TextCount("m") == 50, "...and never deletes the wrong one (the words must match)");
        del.ClearThread("m");
        Check(del.TextCount("m") == 0 && del.Has("m") && del.NumberOf("m").Length == 8, "clearing the conversation keeps the contact and their number");
        // Ava: story lines mirrored once, with her recording.
        del.Add("ava", "Ava", 1, "", "your HUD");
        del.StoryVoice("ava", "Good morning. Sleep well?", "ava-1.wav", 2.5f, "345");
        var story = new List<(bool, string, int, string, long)> { (true, "Good morning. Sleep well?", 1, "08:00", 480), (false, "Barely.", 1, "08:00", 480) };
        Check(del.MirrorStory("ava", story) == 2 && del.MirrorStory("ava", story) == 0, "her story lines are mirrored into her thread once");
        var ava = del.Texts("ava");
        Check(ava[0].Story && ava[0].From == PhoneBook.Them && ava[0].Voice == "ava-1.wav" && ava[1].From == PhoneBook.You && ava[1].State == PhoneBook.Read, "...hers with her real recording, yours as sent and read");
        Check(Math.Abs(ava[0].VoiceSeconds - 2.5f) < 0.01f && ava[0].Wave == "345",
            "FB-74: a line mirrored after its recording was heard gets its length and wave too (not 0:00 and a flat line)");
        // FB-74: the weather under the date.
        Check(Reception.Sky(0, 0, 0.9f, 0, 0, 14) == ("rain", "Rain") && Reception.Sky(0, 0, 0.9f, 0.6f, 0, 14) == ("storm", "Thunderstorm")
              && Reception.Sky(0.5f, 0.9f, 0, 0, 0, 14) == ("snow", "Blizzard") && Reception.Sky(0, 0, 0.3f, 0, 0, 14) == ("rain", "Light rain")
              && Reception.Sky(0, 0, 0, 0, 0.8f, 14) == ("fog", "Fog") && Reception.Sky(0, 0, 0, 0, 0, 14) == ("sun", "Clear")
              && Reception.Sky(0, 0, 0, 0, 0, 23) == ("moon", "Clear night") && Reception.Sky(float.NaN, 0, 0, 0, 0, 5) == ("moon", "Clear night"),
            "FB-74: the home screen's weather: a storm before rain, the sun by day and the moon by night, nothing unreadable trusted");
        story.Add((true, "Then coffee first.", 1, "08:01", 481));
        Check(del.MirrorStory("ava", story) == 1, "...and only the new ones after that");
        // FB-75: her earlier lines from the game's own history go in at their place in time (not after the newest).
        var earlier = new List<(bool, string, int, string, long)> { (true, "You're awake. Good.", 0, "", 0), (false, "Where am I?", 0, "", 0), (true, "Good morning. Sleep well?", 1, "08:00", 480) };
        int unreadBefore = del.Unread("ava");
        Check(del.MirrorEarlierStory("ava", earlier) == 2 && del.Texts("ava")[0].Body == "You're awake. Good." && del.Texts("ava")[1].Body == "Where am I?"
              && del.Texts("ava").Count(t => t.Body == "Good morning. Sleep well?") == 1, "FB-75: earlier story lines go in at their place, never twice");
        Check(del.Unread("ava") == unreadBefore && del.Texts("ava")[0].Story && del.MirrorEarlierStory("ava", earlier) == 0, "...not as new texts, and only once");
        del.StoryGameVoice("ava", "You're awake. Good.", "[Dlg_0A1B2C3D] Wake_01", 3.5f, "222");
        string gv = PhoneBook.GameVoiceFile("[Dlg_0A1B2C3D] Wake_01");
        Check(PhoneBook.IsGameVoice(gv) && del.Texts("ava")[0].Voice == gv && Math.Abs(del.Texts("ava")[0].VoiceSeconds - 3.5f) < 0.01f && del.GameVoiceOf(gv) == "[Dlg_0A1B2C3D] Wake_01",
              "FB-75: voiced from the game's own recording (its name kept, nothing copied)");
        Check(!PhoneBook.IsGameVoice("ava-1.wav") && !PhoneBook.IsGameVoice("game-../x.wav") && PhoneBook.GameVoiceFile("a") != PhoneBook.GameVoiceFile("b"), "...a game note's name is its own, and safe");
        var gone = del.TextAt("ava", 1, "Where am I?");
        del.DeleteText("ava", 1, "Where am I?");
        Check(del.MirrorEarlierStory("ava", earlier) == 0, "a story line you deleted stays gone");
        if (gone != null) del.PutBack("ava", 1, gone);
        Check(del.Texts("ava")[1].Body == "Where am I?" && del.MirrorEarlierStory("ava", earlier) == 0, "...and Undo puts it back, once");
        for (int i = 0; i < PhoneBook.MaxTexts + 20; i++) del.Receive("ava", "chat " + i, 2, "", 3000 + i, open: true);
        Check(del.Texts("ava").Count(t => t.Story) == 5 && del.Texts("ava").Count(t => !t.Story) == PhoneBook.MaxTexts,
              "FB-75: her story lines aren't pushed out by texting (texts keep their own 200)");
        del.ClearThread("ava");
        Check(del.MirrorStory("ava", story) == 0 && !del.HasStoryVoice("Good morning. Sleep well?"), "cleared, they don't come back (and their recordings go)");
        Check(del.MirrorEarlierStory("ava", earlier) == 0, "FB-75: ...nor her earlier ones");
        del.Close();

        // The review's findings (PH-1, PH-5, PH-6, PH-7), kept fixed.
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 13, 10, 26, 10, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0x75, 0x30, 0, 0, 0x75, 0x30 };
        Check(Wallpaper.Dimensions(png, "png") == (30000, 30000) && !Wallpaper.SizeOk(Wallpaper.Dimensions(png, "png")) && Wallpaper.SizeOk((1920, 1080)) && !Wallpaper.SizeOk(null),
            "a picture that says it's huge isn't decoded (its size is read from its header first)");
        var jpg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xC0, 0, 17, 8, 0x04, 0x38, 0x07, 0x80, 3, 0, 0, 0 };
        Check(Wallpaper.Dimensions(jpg, "jpg") == (1920, 1080), "...a JPG's too (its frame header)");
        Check(Wallpaper.Check("http://localhost/x.png").uri == null && Wallpaper.Check("http://192.168.1.4/x.png").uri == null && Wallpaper.Check("http://10.0.0.2/a.gif").uri == null
              && Wallpaper.Check("https://i.imgur.com/x.png").uri != null, "never a link to this PC or the local network");
        var rb = new PhoneBook(Path.Combine(TestRoot.Folder, "rb-" + Guid.NewGuid().ToString("N") + ".json"), seed: 4);
        rb.Add("k", "Kit", 1, "", "");
        rb.Send("k", "first", 1, "", 100, true); rb.MarkSent("k"); rb.MarkDelivered("k");
        rb.Send("k", "sent while she wrote", 1, "", 105, true); rb.MarkSent("k"); rb.MarkDelivered("k");
        rb.Receive("k", "hey!", 1, "", 106, open: true, readUpTo: 101);
        var rt = rb.Texts("k");
        Check(rt[0].State == PhoneBook.Read && rt[1].State == PhoneBook.Delivered, "her answer marks seen only what was there when she began");
        rb.Receive("k", "listen", 1, "", 107, open: true); rb.AttachVoice("k", "listen", "note-9.wav", 1, "1");
        Check(rb.VoiceFiles().Contains("note-9.wav") && rb.Forget("k").SequenceEqual(new[] { "note-9.wav" }) && !rb.VoiceFiles().Contains("note-9.wav"), "forgetting someone gives back their voice notes to delete");
        rb.Close();
        Check(Wallpaper.LocalAddress(System.Net.IPAddress.Parse("::ffff:192.168.1.1")) && Wallpaper.LocalAddress(System.Net.IPAddress.Parse("169.254.169.254"))
              && !Wallpaper.LocalAddress(System.Net.IPAddress.Parse("151.101.1.1")), "a LAN address written as IPv6, or the link-local one, is local too (checked after the name is looked up)");
        var ub = new PhoneBook(Path.Combine(TestRoot.Folder, "ub-" + Guid.NewGuid().ToString("N") + ".json"), seed: 5);
        ub.Add("z", "Zed", 1, "", "");
        ub.Receive("z", "one", 1, "", 1, open: true); ub.Receive("z", "two", 1, "", 2, open: true); ub.Receive("z", "three", 1, "", 3, open: true);
        var keptText = ub.TextAt("z", 1, "two")!;
        ub.DeleteText("z", 1, "two");
        ub.PutBack("z", 1, keptText);
        Check(ub.Texts("z").Select(x => x.Body).SequenceEqual(new[] { "one", "two", "three" }), "Undo puts a deleted text back where it was");
        ub.Close();
        var lr = new LockedRandom(1);
        Check(System.Threading.Tasks.Parallel.For(0, 2000, _ => lr.NextDouble()).IsCompleted && lr.Next(0, 10) is >= 0 and < 10, "one random, safe from any thread");

        // The News app: the holos' headlines and the talk around town, newest first.
        var world = new Social.WorldMemory(Path.Combine(TestRoot.Folder, "news-" + Guid.NewGuid().ToString("N") + ".json"));
        world.AddHeadline(10, "Uptown tram line reopens", "Late trains every twenty minutes.");
        world.AddHeadline(12, "Pier 9 closed after night fire", "Crews worked until dawn.");
        world.AddRumor("the noodle bar waters its broth", "Meridian Market", 12, "npc-a");
        world.AddRumor("an old rumour", "Lowtown", 1, "npc-b");
        var hl = world.Headlines();
        var talk = world.TalkAroundTown(12);
        Check(hl.Count == 2 && hl[0].title.StartsWith("Pier 9") && talk.Count == 1 && talk[0].where == "Meridian Market", "the News app: the newest headline first; the talk of the last week, with where it was heard");
        world.ForgetSource("npc-a");
        Check(world.TalkAroundTown(12).Count == 0, "...and talk from someone forgotten (under 18, say) is gone from it too");
        world.Close();

        // The Usage Optimizer: a text from someone you know is one of its kinds, with a weight.
        var w = new Social.DynamicChatter.Weights(0, 0, 0, 0, 10);
        Check(Social.DynamicChatter.Order(new Random(1), 0, w).SequenceEqual(new[] { Social.DynamicChatter.Kind.Text }), "a text takes no place among the people talking near you, and is picked by its weight");
        Check(!Social.DynamicChatter.Order(new Random(1), 6, new Social.DynamicChatter.Weights(30, 20, 20, 30)).Contains(Social.DynamicChatter.Kind.Text), "weight 0 (or the phone off): never");
        Check(UsageModel.ResidentsAi.Contains("text first") && UsageModel.YoursVoice.Contains("text voice"), "texting first is the residents' (counted as a start); voice notes are yours");
    }

    private static bool Throws(Action a) { try { a(); return false; } catch (FormatException) { return true; } }

    /// <summary>A GIF for tests: 4 colours, each frame (indices, x, y, w, h, disposal, transparent) in uncompressed LZW.</summary>
    private static byte[] Gif(int w, int h, byte[] palette, (byte[] idx, int x, int y, int fw, int fh, int disposal, int transparent)[] frames, int delay)
    {
        var o = new List<byte>();
        o.AddRange(Encoding.ASCII.GetBytes("GIF89a"));
        void U16(int v) { o.Add((byte)(v & 255)); o.Add((byte)(v >> 8)); }
        U16(w); U16(h); o.Add(0x81); o.Add(0); o.Add(0); o.AddRange(palette);
        foreach (var f in frames)
        {
            o.AddRange(new byte[] { 0x21, 0xF9, 4, (byte)((f.disposal << 2) | (f.transparent >= 0 ? 1 : 0)) }); U16(delay); o.Add((byte)Math.Max(0, f.transparent)); o.Add(0);
            o.Add(0x2C); U16(f.x); U16(f.y); U16(f.fw); U16(f.fh); o.Add(0);
            o.Add(2); // minimum code size: 4 colours; codes are 3 bits, a clear every two pixels keeps them there
            var bits = new List<int>();
            for (int i = 0; i < f.idx.Length; i++) { if (i % 2 == 0) bits.Add(4); bits.Add(f.idx[i]); }
            bits.Add(5);
            var data = new List<byte>();
            int acc = 0, n = 0;
            foreach (int c in bits) { acc |= c << n; n += 3; while (n >= 8) { data.Add((byte)(acc & 255)); acc >>= 8; n -= 8; } }
            if (n > 0) data.Add((byte)acc);
            for (int i = 0; i < data.Count; i += 255) { int len = Math.Min(255, data.Count - i); o.Add((byte)len); o.AddRange(data.Skip(i).Take(len)); }
            o.Add(0);
        }
        o.Add(0x3B);
        return o.ToArray();
    }

    /// <summary>A 24-bit BMP, rows given top first as RGB.</summary>
    private static byte[] Bmp24(int w, int h, byte[,] rgbTopFirst)
    {
        int stride = ((w * 24 + 31) / 32) * 4;
        var d = new byte[54 + stride * h];
        d[0] = (byte)'B'; d[1] = (byte)'M';
        BitConverter.GetBytes(d.Length).CopyTo(d, 2);
        BitConverter.GetBytes(54).CopyTo(d, 10);
        BitConverter.GetBytes(40).CopyTo(d, 14);
        BitConverter.GetBytes(w).CopyTo(d, 18);
        BitConverter.GetBytes(h).CopyTo(d, 22);
        BitConverter.GetBytes((short)1).CopyTo(d, 26);
        BitConverter.GetBytes((short)24).CopyTo(d, 28);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int src = (h - 1 - y) * w + x; // the file's first row is the bottom one
                int p = 54 + y * stride + x * 3;
                d[p] = rgbTopFirst[src, 2]; d[p + 1] = rgbTopFirst[src, 1]; d[p + 2] = rgbTopFirst[src, 0];
            }
        return d;
    }
}
