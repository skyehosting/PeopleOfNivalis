using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NivalisAIChat.Ai;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;

namespace NivalisAIChat.Tests;

/// <summary>
/// FB-63: the rules of dating someone (Npc/Romance.cs), and the stage 1 review's reminder fixes (F8-F12): hours with
/// am/pm, 0 isn't midnight unless said, shown reminders go first when the list is full, a reminder that couldn't be shown
/// is shown again, a place only looked up when needed, the plan's words quoted for the prompt.
/// </summary>
internal static partial class Program
{
    private static void RomanceTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- FB-63: dating; the reminders' review fixes");

        // How open they are, by the bar.
        Check(Romance.OpenChance(10) == 0.05 && Romance.OpenChance(30) == 0.05 && Math.Abs(Romance.OpenChance(50) - 0.25) < 1e-9
              && Math.Abs(Romance.OpenChance(100) - 0.90) < 1e-9 && Romance.OpenChance(75) > Romance.OpenChance(60),
            "the bar decides how open they are: rarely below the middle, 25% at it, most of the time near the top");
        int open = 0, low = 0;
        for (int i = 0; i < 2000; i++)
        {
            if (Romance.OpenToday("npc_" + i, 7, 90)) open++;
            if (Romance.OpenToday("npc_" + i, 7, 20)) low++;
        }
        Check(open is > 1440 and < 1640 && low is > 50 and < 160, $"...and the rolls follow it (90 → {open}/2000, 20 → {low}/2000)");
        Check(Romance.OpenToday("npc_x", 7, 60) == Romance.OpenToday("npc_x", 7, 60), "asking again the same day gets the same answer");

        // Dates: one a day (DatesPerDay), only when together.
        var st = new NpcState();
        Check(!Romance.CanDate(st, 5, 1), "no dates before you're together");
        st.Romance = Romance.Dating; st.RomanceSinceDay = 5;
        Check(Romance.CanDate(st, 5, 1), "together: a date today");
        st.LastDateDay = 5; st.DatesToday = 1;
        Check(!Romance.CanDate(st, 5, 1) && Romance.CanDate(st, 6, 1) && Romance.CanDate(st, 5, 2) && !Romance.CanDate(st, 6, 0),
            "one date a day (DatesPerDay), the next day again; 0 = no dates");

        // Moving in: together 3 days or more, and the bar at 75.
        Check(!Romance.CanMoveIn(st, 7, 90) && Romance.CanMoveIn(st, 8, 80) && !Romance.CanMoveIn(st, 8, 60),
            "moving in: only after 3 days together and with the bar at 75 or more");
        st.Romance = Romance.Living;
        Check(!Romance.CanMoveIn(st, 20, 100), "...and not twice");

        // [WHAT YOU'D DO]
        var rec = new NpcRecord { Id = "npc_mae" };
        rec.State.Relationship = Relationships.Neutral;
        var lines = Romance.Lines(rec, 3, 1, onDate: false, "Sam");
        Check(lines.Count == 1 && (lines[0].Contains("not today") || lines[0].Contains("open to it today")), "a stranger: one line, open today or not");
        rec.State.Romance = Romance.Dating; rec.State.RomanceSinceDay = 1;
        rec.State.Hurts.Add("Sam was seen kissing someone else.");
        lines = Romance.Lines(rec, 3, 1, onDate: true, "Sam");
        Check(lines.Any(l => l.Contains("You and Sam are together")) && lines.Any(l => l.Contains("on a date with them right now"))
              && lines.Any(l => l.Contains("too soon")) && lines.Any(l => l.Contains("kissing someone else")),
            "together: their side of it, the date going on, too soon to move in, and what they found out");

        // Wiring: the typed yes, the heart, the open-time lines.
        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string chat = File.ReadAllText(Path.Combine(src, "ModRuntime.Chat.cs"));
        string rom = File.ReadAllText(Path.Combine(src, "ModRuntime.Romance.cs"));
        Check(chat.Contains("ConfirmRomanceFrom(text)") && rom.Contains("if (_romanceConfirm is not { } wait) return false;")
              && rom.Contains("_romanceConfirm = null;"), "their yes becomes official only with your typed yes (anything else lets it pass)");
        Check(chat.Contains("FillWouldDo(snap, c, known)") && chat.Contains("FillWouldDo(snap, _npc, _session?.Record)"),
            "what they'd do is decided at the window's opening and before each line");
        Check(chat.Contains("Ui.SetTogether(Features.Romance && known != null && known.State.Romance.Length > 0)")
              && chat.Contains("Ui.SetTogether(Features.Romance && rec.State.Romance.Length > 0)"), "the bar's heart at opening and after each line");

        // Reminders (review F9-F12).
        Check(ReplyParser.HourOf("at 8pm") == 20 && ReplyParser.HourOf("at 8 a.m.") == 8 && ReplyParser.HourOf("at 12am") == 0
              && ReplyParser.HourOf("at 12 pm") == 12 && ReplyParser.HourOf("at noon") == 12 && ReplyParser.HourOf("at midnight") == 0
              && ReplyParser.HourOf("at 20:30") == 20 && ReplyParser.HourOf("at 13pm") == -1 && ReplyParser.HourOf("at 25") == -1,
            "plan hours: am/pm, noon and midnight read (review F10)");
        var tag = ReplyParser.Parse("Sure. [[reminder: dinner | tomorrow | at 7pm]]", "Mae", "");
        Check(tag.Extras.ReminderAtHour == 19, "...in the hidden notes too");
        var zero = ReplyParser.ParseStructured("{\"say\":\"Ok.\",\"reminder\":{\"what\":\"pay the rent\",\"inDays\":1,\"atHour\":0,\"place\":\"\"}}", "Mae", "");
        var mid = ReplyParser.ParseStructured("{\"say\":\"Ok.\",\"reminder\":{\"what\":\"meet at midnight\",\"inDays\":0,\"atHour\":0,\"place\":\"\"}}", "Mae", "");
        var pm = ReplyParser.ParseStructured("{\"say\":\"Ok.\",\"reminder\":{\"what\":\"lunch\",\"inDays\":0,\"atHour\":\"1pm\",\"place\":\"\"}}", "Mae", "");
        Check(zero!.Extras.ReminderAtHour == -1 && mid!.Extras.ReminderAtHour == 0 && pm!.Extras.ReminderAtHour == 13,
            "a JSON hour of 0 is no hour unless the plan says midnight; \"1pm\" is 13");

        string dir = Path.Combine(Path.GetTempPath(), "pon-rem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var book = new ReminderBook(Path.Combine(dir, "reminders.json"), _ => { });
            var first = book.Add("call the landlord", "", "", 1, 0, -1, "");
            Check(!book.NeedsPlace(), "no place in any reminder: where you are isn't looked up (review F11)");
            book.Add("buy flowers", "", "", 1, 1, -1, "Meridian Market");
            Check(book.NeedsPlace(), "...one with a place: it is");
            var due = book.Due(1, 10, "");
            Check(due.Count == 1 && due[0].Id == first!.Id, "(set up) the first is due");
            book.Unshow(first.Id);
            Check(book.Due(1, 11, "").Any(r => r.Id == first.Id), "a reminder that couldn't be shown is shown next time (review F9)");
            for (int i = 0; i < ReminderBook.MaxKept - 1; i++) book.Add($"thing number {i}", "", "", 1, 5, -1, "");
            var all = book.All();
            Check(all.Count == ReminderBook.MaxKept && all.All(r => !r.Shown) && all.Any(r => r.What == "buy flowers"),
                "a full list lets the shown ones go first (review F10)");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        string remRuntime = File.ReadAllText(Path.Combine(src, "ModRuntime.Reminders.cs"));
        Check(remRuntime.Contains("string title = Plain(") && remRuntime.Contains("string body = Plain("), "the AI's words carry no rich text into a pop-up (review F9)");
        Check(remRuntime.Contains("$\"\\\"{Bare(r.What)}\\\""), "a plan's words are quoted, without brackets, in their prompt (review F12)");
        string runtime = File.ReadAllText(Path.Combine(src, "ModRuntime.cs"));
        string commands = File.ReadAllText(Path.Combine(src, "ModRuntime.Commands.cs"));
        Check(runtime.Contains("_reminders?.Close(); } catch (Exception e) { Log.Warn(\"Saving reminders on stop")
              && runtime.Contains("_reminders?.Forget(id); } catch (Exception e) { Log.Debug(\"under 18, reminders")
              && commands.Contains("_reminders?.Forget(id); // review F8") && commands.Contains("_reminders?.Clear(); }"),
            "reminders: closed on stop, forgotten with someone under 18 or /resetnpc, cleared by /resetall (review F8)");
    }
}
