using System;
using System.IO;
using System.Linq;
using NivalisAIChat.Ai;
using NivalisAIChat.Social;

namespace NivalisAIChat.Tests;

/// <summary>FB-65 reminders, and the reply fields FB-62..65 share (follow, romance, date, moving in, a plan).</summary>
internal static partial class Program
{
    private static void RemindersTests()
    {
        Console.WriteLine("-- FB-65: plans and reminders; the reply's new fields");
        string dir = Path.Combine(Path.GetTempPath(), "pon-rem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "reminders.json");
            var book = new ReminderBook(path, _ => { });
            var a = book.Add("meet at the noodle bar", "p_mae", "Mae", today: 10, inDays: 1, atHour: 20, place: "Kiko's Noodle Bar");
            Check(a != null && a.Day == 11 && a.Hour == 20 && a.WhoName == "Mae", "a plan for tomorrow at 20:00 at a place");
            Check(book.Add("meet at the noodle bar", "p_mae", "Mae", 10, 1, 20, "x") == null, "...the same plan isn't added twice");
            Check(book.Add("x", "", "", 10, 0, -1, "") == null && book.Add("pay rent", "", "", -1, 0, -1, "") == null, "nothing kept from an empty plan or an unknown day");

            // FB-78 (the author: "she repeated it a couple of time and it also kept popping up"): the same plan said again in
            // other words, shown or not, is that plan (its place or hour filled in), not a new one.
            var sb = new ReminderBook(Path.Combine(dir, "fb78.json"), _ => { });
            var first = sb.Add("after work", "p_sto", "Stranger", today: 3, inDays: 0, atHour: -1, place: "");
            sb.Due(3, 23, new[] { "Meridian Market" });
            Check(first != null && sb.Add("after work, at Izakaya Stop", "p_sto", "Stranger", 3, 0, -1, "Izakaya Stop") == null
                  && sb.Add("meet at Izakaya Stop after work", "p_sto", "Stranger", 3, 0, -1, "Izakaya Stop") == null
                  && sb.Add("After work at Izakaya Stop", "p_sto", "Stranger", 3, 0, -1, "Izakaya Stop") == null
                  && sb.All().Count == 1 && sb.All()[0].Place == "Izakaya Stop" && sb.All()[0].Shown,
                  "FB-78: four wordings of one plan are one plan (the place it gained kept), and it isn't shown again");
            Check(sb.Add("breakfast", "p_sto", "Stranger", 3, 1, 9, "") != null && sb.Add("drinks at nine", "p_sto", "Stranger", 3, 0, 21, "") != null
                  && sb.Add("lunch", "p_sto", "Stranger", 3, 0, -1, "Kiko's Noodle Bar") != null && sb.Add("after work", "p_mae", "Mae", 3, 0, -1, "") != null,
                  "...another day, another hour, another place or another person is another plan");
            Check(sb.Add("pay the rent", "p_bo", "Bo", 3, 0, 18, "") != null && sb.Add("feed the cat", "p_bo", "Bo", 3, 0, 18, "") != null,
                  "review PLAN-1: the same hour alone doesn't make two plans one");
            sb.SetOutcome(sb.All()[0].Id, "met");
            Check(sb.Add("after work again", "p_sto", "Stranger", 3, 0, -1, "Izakaya Stop") != null, "...and once a plan has happened, a new one can be made");
            var own = book.Add("pay the rent", "", "", today: 10, inDays: 0, atHour: -1, place: "");
            Check(book.Due(10, 9, "Meridian Market").Select(r => r.What).SequenceEqual(new[] { "pay the rent" }), "a reminder for today (any hour) is due today, once");
            Check(book.Due(10, 23, "Meridian Market").Count == 0 && book.Due(11, 19, "Kiko's Noodle Bar").Count == 0, "...not again; tomorrow's 20:00 plan isn't due at 19:00");
            Check(book.Due(11, 20, "Meridian Market").Count == 1, "CF-20: a place plan with an hour reminds you at its hour, wherever you are");
            Check(book.Due(11, 21, "the noodle bar at Kiko's").Count == 0, "...once");
            var late = book.Add("call Bo back", "p_bo", "Bo", 12, 0, 18, "the docks");
            Check(book.Due(13, 0, "").Select(r => r.What).SequenceEqual(new[] { "call Bo back" }), "a place plan whose day has passed is due anyway (not lost)");
            // CF-20 (a player: meet-ups never happened): where you are is the district AND the venue you're in.
            var b2 = new ReminderBook(Path.Combine(dir, "r2.json"), _ => { });
            b2.Add("dinner", "p_mae", "Mae", 10, 0, 20, "the noodle bar");
            b2.Add("browse", "p_jo", "Jo", 10, 0, -1, "the market");
            Check(ReminderBook.PlaceMatches(new[] { "Lowtown", "Kiko's Noodle Bar" }, "the noodle bar") && !ReminderBook.PlaceMatches(new[] { "Lowtown" }, "the noodle bar"),
                  "a plan's place matches the venue you're in, not only the district");
            Check(!ReminderBook.PlaceMatches(new string[0], "the market") && !ReminderBook.PlaceMatches(new[] { "", "Lowtown" }, ""), "nowhere, or no place: no match");
            Check(b2.Due(10, 20, new[] { "Docks" }).Select(r => r.What).SequenceEqual(new[] { "dinner" }), "a plan with an hour: its reminder at that hour, anywhere");
            Check(b2.Due(10, 21, new[] { "Lowtown", "Kiko's Noodle Bar" }).Count == 0, "a place plan with no hour still waits until you're there");
            Check(b2.Due(10, 22, new[] { "Lowtown", "Meridian Market" }).Select(r => r.What).SequenceEqual(new[] { "browse" }), "...and the venue you're in counts as there");
            Check(!b2.NeedsPlace(), "review R7: where you are is looked up only for a place plan with no hour still to come");
            b2.Close();
            Check(book.With("p_mae", 11).Count == 1 && book.With("p_mae", 14).Count == 0, "the person knows your plans with them until a day after");
            var again = new ReminderBook(path, _ => { });
            Check(again.All().Count == 3 && again.All().All(r => r.Shown), "kept in reminders.json (per save: FB-61's snapshot has it)");
            book.Forget("p_bo");
            Check(book.All().All(r => r.WhoId != "p_bo"), "someone forgotten takes their plans with them");
            book.Close();
            Check(book.Add("after close", "", "", 20, 0, -1, "") != null && !new ReminderBook(path, _ => { }).All().Any(r => r.What == "after close"),
                "a closed book (a load swapped it) writes nothing more");
            Check(ReminderBook.When(new ReminderBook.Reminder { Day = 11, Hour = 20, Place = "the market" }, 10) == "tomorrow at 20:00, the market"
                  && ReminderBook.When(new ReminderBook.Reminder { Day = 10, Hour = -1 }, 10) == "today", "when, in words");
            Check(SaveSnapshotsPerSaveHas("reminders.json"), "reminders are per save");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        // The reply's fields, both ways of answering.
        var json = ReplyParser.ParseStructured("{\"say\":\"Sure, I'll come.\",\"follow\":\"yes\",\"romance\":\"asks\",\"date\":\"\",\"moveIn\":\"no\"," +
                                         "\"reminder\":{\"what\":\"meet at the market\",\"inDays\":1,\"atHour\":20,\"place\":\"Meridian Market\"}}", "Mae", "");
        Check(json!.Extras.Follow == "yes" && json.Extras.Romance == "asks" && json.Extras.Date == "" && json.Extras.MoveIn == "no"
              && json.Extras.ReminderWhat == "meet at the market" && json.Extras.ReminderInDays == 1 && json.Extras.ReminderAtHour == 20 && json.Extras.ReminderPlace == "Meridian Market",
            "JSON reply: follow, romance, date, moveIn and a reminder read");
        var odd = ReplyParser.ParseStructured("{\"say\":\"Hm.\",\"follow\":\"maybe\",\"romance\":\"YES.\",\"reminder\":{\"what\":\"none\",\"inDays\":2}}", "Mae", "");
        Check(odd!.Extras.Follow == "" && odd.Extras.Romance == "yes" && odd.Extras.ReminderWhat == "", "anything but the listed words is nothing; a placeholder plan is none");
        var tags = ReplyParser.Parse("See you then. [[reminder: meet at the market | tomorrow | at 20 | Meridian Market]] [[follow: stop]] [[move_in]] [[date: over]]", "Mae", "");
        Check(tags.Spoken == "See you then." && tags.Extras.ReminderWhat == "meet at the market" && tags.Extras.ReminderInDays == 1 && tags.Extras.ReminderAtHour == 20
              && tags.Extras.ReminderPlace == "Meridian Market" && tags.Extras.Follow == "stop" && tags.Extras.MoveIn == "yes" && tags.Extras.Date == "over",
            "hidden notes: the same, never spoken");
        Check(!ReplyParser.Parse("Fine.", "Mae", "").Extras.Any, "a plain reply asks for nothing");
        var schema = Prompts.ReplySchema().ToJsonString();
        Check(schema.Contains("\"follow\"") && schema.Contains("\"romance\"") && schema.Contains("\"moveIn\"") && schema.Contains("\"reminder\"") && schema.Contains("\"inDays\""),
            "the reply's schema asks for them");
    }

    private static bool SaveSnapshotsPerSaveHas(string file) => Npc.SaveSnapshots.PerSave.Contains(file);
}
