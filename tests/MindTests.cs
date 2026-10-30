using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NivalisAIChat.Mind;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;

namespace NivalisAIChat.Tests;

/// <summary>The inner-life round (0.7.0): feelings carried over, what's on their mind, what they saw, gossip, the phrase ledger, turn cards, plans.</summary>
internal static partial class Program
{
    private static Persona P(string temperament, string honesty = "mostly honest", string verbosity = "average", int slang = 1, string worry = "") => new()
    {
        Name = "Mira Sato", Temperament = temperament, Honesty = honesty, CurrentWorry = worry,
        Speech = new SpeechStyle { Verbosity = verbosity, SlangLevel = slang, Rhythm = "short sentences" },
    };

    private static void MindTests()
    {
        Console.WriteLine("-- inner life: feelings carried over (Affect)");
        long day3 = GameClock.At(3, 12, 0);
        var warm = Affect.Baseline(P("warm and chatty"));
        var anxious = Affect.Baseline(P("anxious and fidgety"));
        Check(warm.v > 0.1f && anxious.a > Voice.Tone.CalmArousal + 0.1f, "a warm person rests happier; an anxious one more wound up");
        var s = Affect.Fresh(warm, day3);
        Check(Affect.Label(s, day3) == null, "at their resting point, nothing is carried (their mood is rolled as always)");
        for (int i = 0; i < 3; i++) Affect.Feel(s, "angry", "strong", day3);
        var label = Affect.Label(s, day3 + 30);
        Check(label is { mood: "angry" or "irritated" or "stressed" or "grumpy" }, $"insulted half an hour ago: still {label?.mood}");
        Check(Affect.Label(s, day3 + 24 * 60) == null, "a day later, back to themselves");
        var hurt = Affect.Fresh(warm, day3);
        Affect.Add(hurt, new Moodlet { Key = "stood", Text = "they stood you up", V = -0.5f, A = 0.1f, Until = day3 + 36 * 60 }, day3);
        Check(Affect.Label(hurt, day3 + 20 * 60) is { reason: "they stood you up" }, "something that stays with them holds the mood, and says why");
        Check(Affect.Label(hurt, day3 + 60 * 60) == null, "...until it's gone");
        var low = Affect.Fresh((0f, 0.3f), day3);
        Affect.Add(low, new Moodlet { Key = "grief", Text = "a breakup", V = -0.9f, A = 0f, Until = day3 + 6 * 24 * 60 }, day3);
        for (long t = day3; t < day3 + 30 * 60; t += 60) Affect.Settle(low, t);
        Check(low.Moodlets.Any(m => m.Key == "rebound"), "after a long low they bounce back a little (catharsis)");
        var back = Affect.Fresh(warm, day3);
        Affect.Feel(back, "sad", "strong", day3);
        Affect.Settle(back, day3 - 500);
        Check(back.V == back.BaseV && back.At == day3 - 500, "a clock that went back (a new game) starts them fresh");

        // The city never sinks into gloom: 40 residents, two game weeks of everyday ups and downs.
        var rng = new Random(7);
        var city = Enumerable.Range(0, 40).Select(i => Affect.Fresh(Affect.Baseline(P(i % 5 == 0 ? "melancholic" : i % 3 == 0 ? "relentlessly upbeat" : "pragmatic and blunt")), 0)).ToList();
        string[] good = { "cheerful", "content", "amused", "hopeful" }, bad = { "irritated", "sad", "anxious", "angry" };
        for (long t = 0; t < 14 * 1440; t += 30)
            foreach (var p in city)
            {
                if (rng.NextDouble() < 0.02) Affect.Feel(p, rng.NextDouble() < 0.5 ? good[rng.Next(4)] : bad[rng.Next(4)], "moderate", t);
                if (rng.NextDouble() < 0.002) Affect.Add(p, new Moodlet { Key = "e" + rng.Next(3), Text = "something", V = rng.NextDouble() < 0.5 ? -0.5f : 0.4f, Until = t + 600 + rng.Next(2000) }, t);
                Affect.Settle(p, t);
            }
        double mean = city.Average(p => p.V), gloomy = city.Count(p => p.V < -0.6f) / 40.0;
        Check(mean is > -0.25 and < 0.4 && gloomy < 0.15, $"two weeks on, the city's mean feeling {mean:0.00}, {gloomy:P0} very low");

        Console.WriteLine("-- inner life: what's on their mind (Preoccupation)");
        var mi = new MindInputs { Persona = P("pragmatic and blunt", worry: "rent due on Friday") };
        var t1 = Preoccupation.Choose(mi, null, day3);
        Check(t1 is { Kind: "worry", Text: "rent due on Friday" }, "with nothing else going on, their own worry");
        mi.Moodlets.Add(new Moodlet { Key = "row", Text = "still stewing over the row with Doran", V = -0.6f, Since = day3, Until = day3 + 600 });
        var t2 = Preoccupation.Choose(mi, t1, day3 + 5);
        Check(t2 is { Kind: "feeling" }, "something that just happened takes over clearly");
        mi.PlanSoon = "meeting the newcomer at the noodle bar at 20:00";
        var t3 = Preoccupation.Choose(mi, t2, day3 + 10);
        Check(t3?.Key == t2?.Key && t3?.Since == t2?.Since, "it sticks: a thought not clearly better doesn't push it out");
        mi.Moodlets.Clear();
        var t4 = Preoccupation.Choose(mi, t3, day3 + 20);
        Check(t4?.Kind == "plan", "once it's passed, the next thing on their mind");
        Check(Preoccupation.Choose(new MindInputs { Hunger = 0.95f, Persona = P("stoic") }, null, day3)?.Key == "hungry", "a calibrated need: starving");

        Console.WriteLine("-- inner life: what they saw (WitnessBook)");
        string dir = Path.Combine(Path.GetTempPath(), "pon-mind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string wfile = Path.Combine(dir, "witness.json");
            var book = new WitnessBook(wfile, _ => { });
            var minors = new HashSet<string> { "kid" };
            book.IsMinor = minors.Contains;
            var near = new Observer { Id = "rina", Name = "Rina Vale", Relation = 1.2f, Distance = 2f, Sees = true };
            var far = new Observer { Id = "teo", Name = "Teo", Relation = 1.2f, Distance = 12f, Sees = false }; // knows them, heard it from the next table
            int kept = book.Witness(Happenings.Argued, "doran", "Doran Pike", "waiter", "a waiter", "Neon Ramen", day3, 0.7f, new[] { near, far, new Observer { Id = "kid", Relation = 1f, Distance = 1f, Sees = true } });
            Check(kept == 2 && book.Of("kid", day3).Count == 0, "two adults kept the row (one saw it, one heard it); nobody under 18 keeps anything");
            Check(book.Witness(Happenings.PlayerAte, Happenings.Player, "", "", "", "Izakaya", day3, 0.5f, new[] { far }) == 0, "a trivial thing heard from afar isn't kept");
            Check(book.Witness(Happenings.Argued, "kid", "Kid", "doran", "Doran", "", day3, 1f, new[] { near }) == 0, "nothing with someone under 18 in it");
            var rinas = book.Of("rina", day3);
            Check(rinas.Count == 1 && rinas[0].How == Belief.Saw && book.Of("teo", day3)[0].How == Belief.Heard, "how they know it: saw it, heard it");
            string words = Happenings.Words(rinas[0], "rina", "the newcomer", day3 + 600);
            Check(words.StartsWith("Doran had a row with a waiter at Neon Ramen") && words.EndsWith("(you saw it)"), "put the way they'd put it: " + words);
            Check(book.Of("doran", day3).Count == 0, "the one who did it isn't told about themselves");
            book.Witness(Happenings.BrokeUp, "ana", "Ana", "bo", "Bo", "", day3, 1f, new[] { new Observer { Id = "rina", Relation = 1.6f, Distance = 1f, Sees = true } });
            Check(book.Of("rina", day3).First().Kind == Happenings.BrokeUp && book.Of("rina", day3).First().Pinned, "a friend's breakup seen up close: the strongest, pinned");
            for (int i = 0; i < 60; i++)
                book.Witness(Happenings.Argued, "x" + i, "X", "y" + i, "Y", "", day3 + i, 0.9f, new[] { new Observer { Id = "nosy", Relation = 1.4f, Distance = 1f, Sees = true } });
            Check(book.Of("nosy", day3 + 60).Count <= WitnessBook.MaxLive + WitnessBook.MaxPinned, "at most 24 live and 8 pinned each");
            var trivial = new Observer { Id = "pat", Relation = 1.2f, Distance = 1f, Sees = true };
            book.Witness(Happenings.Laughed, "ana", "Ana", "bo", "Bo", "", day3, 0.5f, new[] { trivial });
            Check(book.Of("pat", day3).Count == 1 && book.Of("pat", day3 + 30 * 1440).Count == 0, "a small thing fades and goes (MemoryBank's curve)");

            // Passed on: hearsay, one telling away, from whom, changed now and then.
            var told = book.Of("rina", day3).First(b => b.Kind == Happenings.Argued);
            var heard = book.Hear("lou", "Lou", told, "rina", "Rina Vale", Telling.Straight, 0.8f, day3 + 60, new Random(1));
            Check(heard is { How: Belief.Told, Hops: 1, Source: "rina" } && Happenings.Words(heard, "lou", "the newcomer", day3 + 70).Contains("(heard from Rina)"), "heard from Rina, and says so");
            var twice = book.Hear("max", "Max", heard!, "lou", "Lou", Telling.Embellished, 0.8f, day3 + 90, new Random(2));
            Check(twice is { Hops: 2 } && Happenings.Words(twice, "max", "x", day3 + 100).Contains("the way Lou told it; you're not sure"), "two tellings away: unsure, and it shows");
            Check(book.Of("rina", day3 + 61).First(b => b.EventId == told.EventId).ToldTo.Contains("lou"), "the teller won't tell Lou again");
            Check(book.Hear("doran", "Doran", told, "rina", "Rina", Telling.Straight, 1f, day3, new Random(3)) == null, "nobody hears gossip about themselves");
            int bigger = 0;
            for (int i = 0; i < 200; i++) if (book.Hear("l" + i, "L", told, "rina", "Rina", Telling.Embellished, 1f, day3 + 90, new Random(i))!.Intensity > told.Intensity) bigger++;
            Check(bigger is > 90 and < 150, $"made worse when they embellish (about 60%: {bigger}/200)");

            // Kept per save: written, read back.
            book.Flush();
            var again = new WitnessBook(wfile, _ => { });
            Check(again.Of("rina", day3).Count == book.Of("rina", day3).Count && again.Of("lou", day3 + 60).Count == 1, "witness.json is read back as it was");
            book.Forget("doran");
            Check(book.Of("rina", day3).All(b => !b.Involves("doran")) && book.Of("lou", day3 + 60).Count == 0, "forgetting someone takes them out of what everyone knows");

            Console.WriteLine("-- inner life: gossip told in code (Telling)");
            var gb = new WitnessBook(Path.Combine(dir, "g.json"), _ => { });
            gb.Witness(Happenings.Argued, "doran", "Doran Pike", "waiter", "a waiter", "Neon Ramen", day3, 0.8f, new[] { new Observer { Id = "rina", Name = "Rina", Relation = 1.4f, Distance = 1f, Sees = true } });
            var pairs = new PairBook(Path.Combine(dir, "pairs.json"));
            for (int i = 0; i < 8; i++) pairs.Record("rina", "Rina", "lou", "Lou", 10, 3, "talked");
            var people = new List<Telling.Speaker>
            {
                new() { Id = "rina", Name = "Rina Vale", Persona = P("nosy and gossipy") },
                new() { Id = "lou", Name = "Lou Park", Persona = P("laid-back and unbothered") },
            };
            int picked = 0;
            for (int i = 0; i < 50; i++) if (Telling.Pick(gb, people, pairs.Get, day3 + 60, new Random(i)).Any(p => p.TellerId == "rina")) picked++;
            Check(picked > 15, $"a gossip with something to tell a friend usually tells it ({picked}/50)");
            var plan = Enumerable.Range(0, 50).Select(i => Telling.Pick(gb, people, pairs.Get, day3 + 60, new Random(i))).First(l => l.Count > 0)[0];
            Check(plan.Note.StartsWith("Rina may bring up, if the talk allows: Doran had a") && plan.Note.Contains("they saw it themselves"), "the writer is told what, and how she knows: " + plan.Note);
            var withDoran = people.Append(new Telling.Speaker { Id = "doran", Name = "Doran Pike", Persona = P("hot-headed") }).ToList();
            Check(Enumerable.Range(0, 50).All(i => Telling.Pick(gb, withDoran, pairs.Get, day3 + 60, new Random(i)).All(p => !p.Belief.Involves("doran"))), "never behind someone's back while they're there");
            for (int i = 0; i < 40; i++) pairs.Record("rina", "Rina", "doran", "Doran", 10, 3, "talked"); // she gets on well with him (affinity 40)
            Check(Telling.StanceFor(people[0], plan.Belief, people.Skip(1).ToList(), pairs.Get, new Random(1)) == Telling.Softened, "she likes Doran: she goes easy on him");
            Check(Telling.Heard(gb, plan, people, pairs.Get, day3 + 90, new Random(4)) == 1 && gb.Of("lou", day3 + 90).Any(b => b.Source == "rina"), "once said, Lou knows it, from Rina");
            Check(Telling.Pick(gb, people, pairs.Get, day3 + 95, new Random(5)).All(p => p.TellerId != "rina"), "...and Rina won't tell Lou the same thing twice");

            Console.WriteLine("-- inner life: the phrase ledger, the turn card, the card");
            Check(PhraseLedger.OpenerKey("Oh, sure.") == "oh" && PhraseLedger.OpenerKey("Rain again, huh") == "rain again", "what a line opens with");
            var openers = new List<string> { "oh", "yeah", "oh" };
            Check(PhraseLedger.Trim("Oh, that's rough.", openers) == "That's rough." && PhraseLedger.Trim("Oh, that's rough.", new List<string> { "oh" }) == "Oh, that's rough.",
                "a filler they keep opening with is trimmed (only then)");
            Check(PhraseLedger.Trim("Oh!", openers) == "Oh!", "...never down to nothing");
            int pick = PhraseLedger.Pick(new[] { "Did I lock the back door?", "Need to feed the cat.", "Did I lock the door?" }, new[] { "did i lock the back door" }, Array.Empty<string>());
            Check(pick == 1, "of three candidates, the one least like what they said lately");
            var card = TurnCard.For(new TurnInputs { Name = "Mira Sato", Persona = P("cynical and dry", verbosity: "terse"), Mood = "irritated", NoQuestion = true, TimesMet = 1, Bar = 50 }, new Random(3));
            Check(card.StartsWith("[This reply: About ") && card.Contains("End on a statement.") && card.EndsWith("Sound like Mira: cynical and dry, short sentences.]") && card.Length < 320,
                "the turn card: words, a statement this time, and who they sound like, last: " + card);
            var (lo, hi) = TurnCard.Words(P("x", verbosity: "terse"), "angry", wrapUp: true);
            Check(hi <= 6 && lo >= 2, $"terse, angry and wrapping up: very short ({lo}-{hi} words)");
            Check(TurnCard.Depth(85, 5).Contains("only tell a friend") && TurnCard.Depth(50, 1).Contains("nothing personal"), "how much of themselves they share, by closeness");
            var inner = InnerCard.Lines(new InnerInputs
            {
                Plans = { "You'd agreed to meet them at the noodle bar (last night), and they never showed." },
                Thought = new Thought { Text = "rent due on Friday" }, Body = "You're hungry.",
                Together = "You're with Teo (since day 12).", PeopleHere = { "Rina (a good friend)", "Bo (you can't stand him)" },
                Seen = { "doran had a row with a waiter [at] Neon Ramen", "x", "y" }, Keep = { "Rina and Teo split up" },
            });
            Check(inner.Count == InnerCard.MaxLines && inner[0].Contains("never showed") && inner.All(l => !l.Contains('[')) && inner.Sum(l => l.Length) <= InnerCard.MaxChars,
                "the card: a plan's outcome first, capped, nothing that reads as a section of its own");

            Console.WriteLine("-- inner life: turns in a group, what each wants");
            var members = new List<TurnMember>
            {
                new() { Id = "a", Name = "Ana Ruiz", Label = "Ana" }, new() { Id = "b", Name = "Bo Lind", Label = "Bo", Talkative = 1.5 },
                new() { Id = "c", Name = "Cy Moss", Label = "Cy", Confronts = true, Arousal = 0.9f },
            };
            var gp = GroupTurns.Decide("Bo, what do you think?", members, "a", false, "a", new Random(1));
            Check(gp.First == "b" && gp.Note.StartsWith("[Turns, decided: Bo answers."), "named: Bo answers");
            Check(GroupTurns.Decide("I'm not sure.", members, "a", true, "a", new Random(1)).First == "a", "answering Ana's question: Ana goes on");
            int cut = 0;
            for (int i = 0; i < 100; i++) if (GroupTurns.Decide("hi", members, "", false, "a", new Random(i)).CutIn == "c") cut++;
            Check(cut is > 30 and < 70 && Enumerable.Range(0, 50).All(i => GroupTurns.Decide("hi", members, "", false, "a", new Random(i)).CutIn != "b"),
                $"only someone confrontational and worked up cuts in ({cut}/100)");
            var friends = new PairBook(Path.Combine(dir, "friends.json"));
            for (int i = 0; i < 60; i++) friends.Record("rina", "Rina", "lou", "Lou", 10, 3, "talked");
            var wants = Volitions.For(new List<Volitions.Who> { new() { Id = "rina", Name = "Rina", Mood = "in a hurry" }, new() { Id = "lou", Name = "Lou", Mood = "content" } }, friends.Get);
            Check(wants.Count == 2 && wants[0] == "Rina wants to get away soon." && wants[1] == "Lou is glad to see Rina.", "what each wants: " + string.Join(" ", wants));

            Console.WriteLine("-- inner life: needs understood before they're used");
            var cal = new NeedsCalibration();
            for (int i = 0; i < 10; i++) cal.Observe("p" + i, 0.3f, 0.6f, 0, false, false);
            Check(cal.Hunger(0.9f) < 0, "not known yet: not used");
            for (int i = 0; i < 10; i++) cal.Observe("p" + i, 0.35f, 0.55f, 30, false, false);
            Check(cal.Ready && cal.HungerSign == 1 && cal.RestSign == -1 && Math.Abs(cal.Hunger(0.9f) - 0.9f) < 1e-4 && Math.Abs(cal.Rest(0.1f) - 0.9f) < 1e-4,
                "hunger builds up, rest runs down: each read its own way (" + cal.Describe() + ")");
            var cal2 = new NeedsCalibration();
            for (int i = 0; i < 10; i++) { cal2.Observe("e" + i, 50f, 50f, 0, true, false); cal2.Observe("e" + i, 40f, 40f, 30, true, false); }
            Check(!cal2.Ready, "people eating or sleeping don't count");

            Console.WriteLine("-- inner life: plans that happen (meet-ups)");
            var rbook = new ReminderBook(Path.Combine(dir, "reminders.json"), _ => { });
            var r = rbook.Add("meet at the noodle bar", "mira", "Mira", 3, 0, 20, "noodle bar")!;
            var rem2 = rbook.Add("pay the rent", "", "", 3, 1, -1, "")!;
            Check(Meetups.IsMeetup(r) && !Meetups.IsMeetup(rem2) && rbook.OpenMeetups().Count == 1, "a plan with someone at a place is a meet-up; a reminder isn't");
            var (from, to) = Meetups.Window(r);
            Check(from == GameClock.At(3, 19, 30) && to == GameClock.At(3, 22, 0), "they're there from half an hour before to two hours after");
            Check(Meetups.Settle(r, GameClock.At(3, 21, 0), true) == null && Meetups.Settle(r, GameClock.At(3, 23, 0), true) == Meetups.StoodUp
                  && Meetups.Settle(r, GameClock.At(3, 23, 0), false) == Meetups.Flaked, "open in its window; past it, stood up (you never came) or flaked (they didn't)");
            rbook.Came(r.Id);
            Check(Meetups.Settle(rbook.OpenMeetups()[0], GameClock.At(3, 23, 0), true) == Meetups.Met, "you came: met");
            Check(rbook.SetOutcome(r.Id, Meetups.StoodUp) && !rbook.SetOutcome(r.Id, Meetups.Met) && rbook.OpenMeetups().Count == 0
                  && rbook.Outcomes("mira", 4)[0].Outcome == Meetups.StoodUp, "an outcome is set once and kept");
            Check(Meetups.Line(rbook.Outcomes("mira", 4)[0], GameClock.At(4, 12, 0)).StartsWith("You'd agreed to meet them at noodle bar (yesterday evening), and they never showed"), "their chat knows they were stood up");
            Check(Meetups.FlakeChance(P("scatterbrained")) > Meetups.FlakeChance(P("earnest and idealistic")) && Meetups.TheyCome(r, P("x")) == Meetups.TheyCome(r, P("x")),
                "the scatterbrained flake more; each plan's answer is the same every time it's asked");
            File.WriteAllText(Path.Combine(dir, "old.json"), "{\"Items\":[{\"Id\":\"a\",\"What\":\"x y z\",\"WhoId\":\"m\",\"Place\":\"p\",\"Day\":1,\"Outcome\":null}]}");
            Check(new ReminderBook(Path.Combine(dir, "old.json"), _ => { }).OpenMeetups().Count == 1, "an older file without outcomes reads fine");

            Console.WriteLine("-- inner life: the mind book (minds.json)");
            string mfile = Path.Combine(dir, "minds.json");
            var minds = new MindBook(mfile, _ => { }) { IsMinor = minors.Contains };
            minds.Felt("mira", "Mira", warm, "angry", "strong", day3);
            minds.Felt("mira", "Mira", warm, "angry", "strong", day3);
            Check(minds.MoodNow("mira", day3 + 20) is { mood: "angry" or "irritated" or "grumpy" or "stressed" }, "how she felt a moment ago is carried into how she is");
            minds.Add("mira", "Mira", warm, new Moodlet { Key = "gift", Text = "touched by the newcomer's gift", V = 0.5f, Until = day3 + 900 }, day3 + 30);
            minds.Think("mira", "Mira", new Thought { Kind = "worry", Key = "worry", Text = "rent [due]", Since = day3 }, day3);
            minds.Said("mira", "Mira", "Oh, sure.", day3);
            minds.Said("mira", "Mira", "Oh, fine.", day3);
            Check(minds.Phrases("mira").openers.SequenceEqual(new[] { "oh", "oh" }) && minds.Current("mira")?.Text == "rent due", "openers kept; what's on her mind kept (no brackets)");
            minds.Felt("kid", "Kid", null, "angry", "strong", day3);
            Check(!minds.Has("kid") && minds.MoodNow("kid", day3) == null, "nobody under 18 has an inner life kept");
            minds.Flush();
            var mread = new MindBook(mfile, _ => { });
            Check(mread.Moodlets("mira", day3 + 40).Any(m => m.Key == "gift") && mread.Current("mira")?.Key == "worry", "minds.json is read back as it was");
            mread.Forget("mira");
            Check(!mread.Has("mira"), "forgotten");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
