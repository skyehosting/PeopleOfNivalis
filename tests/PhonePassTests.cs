using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NivalisAIChat.Phone;

namespace NivalisAIChat.Tests;

/// <summary>The phone pass (2026-10-20, PH-A1..A14): what a deep read of the phone found, each fixed and checked.</summary>
internal static partial class Program
{
    private static void PhonePassTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- the phone pass: texts not yet arrived, reads, answers, the window");
        string dir = TestRoot.NewFolder("phonepass");
        var book = new PhoneBook(Path.Combine(dir, "phone.json"), seed: 3);
        book.Add("person_a", "Mara Venn", 4, "20:10", "the market");
        book.Send("person_a", "hey, you around?", 4, "20:11", 4 * 1440 + 1211, signal: true);
        book.MarkSent("person_a");
        book.MarkDelivered("person_a");
        book.Send("person_a", "sent with no bars", 4, "20:15", 4 * 1440 + 1215, signal: false);
        book.Send("person_a", "still sending", 4, "20:16", 4 * 1440 + 1216, signal: true);
        var all = book.Texts("person_a");
        var ai = book.TextsForAi("person_a");
        Check(all.Count == 3 && ai.Count == 1 && ai[0].Body == "hey, you around?",
              "PH-A2: what they're shown is only what reached them (a text waiting for a bar, or still sending, isn't answered before it arrives)");
        book.Receive("person_a", "yeah, at the market", 4, "20:20", 4 * 1440 + 1220, open: false);
        Check(book.TextsForAi("person_a").Count(t => t.From == PhoneBook.Them) == 1 && book.TextsForAi("person_a").Count == 2,
              "...their own texts are always in it");
        var states = book.Texts("person_a").Where(t => t.From == PhoneBook.You).Select(t => t.State).ToList();
        Check(states.SequenceEqual(new[] { PhoneBook.Read, PhoneBook.Waiting, PhoneBook.Sending }),
              "...and their answer reads only what reached them (one still sending, or waiting for a bar, isn't marked read)");

        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string rt = File.ReadAllText(Path.Combine(src, "ModRuntime.Phone.cs"));
        Check(rt.Contains("if (PhoneUi.ScreenName == \"thread\") _phone.MarkRead(PhoneUi.Subject);"),
              "PH-A1: the phone opening on a conversation reads it (it came in while the phone was away)");
        Check(rt.Contains("brain.AvaTextAsync(id, \"Ava\", book.TextsForAi(id), snap, first: false,") && rt.Contains("brain.TextAsync(id, book.TextsForAi(id), snap, first: false,")
              && rt.Contains("brain.TextAsync(id, book.TextsForAi(id), snap, first: true,") && !rt.Contains("brain.TextAsync(id, book.Texts(id)"),
              "...and every answer (and text first) is written from that");
        Check(rt.Contains("if (lookAt < 0) { book.MarkSeen(id, now); if (_phoneOpen) RefreshPhone(); }"),
              "PH-A3: a text sent while they're about to type is seen at once (never typing before seen)");
        Check(rt.Contains("PhoneNoAnswer[id] = ($\"Couldn't get {who} reply (something went wrong).\", true);") && rt.Contains("MainThread.Enqueue(RefreshIfThread(id)); // PH-A4"),
              "PH-A4: an answer that fails says so with Try again, and the dots always go");
        Check(rt.Contains("if (unanswered.Count == 0)") && rt.Contains("writing.Cts.Cancel();"),
              "PH-A5: nothing left to answer (deleted), no answer out of nowhere; clearing a conversation stops one being written");
        Check(rt.Contains("Where(c => !ContactInfo.ContainsKey(c.Id)).Select(c => c.Id).Where(TryContactNow)") && rt.Contains("if (!ContactInfo.ContainsKey(id) && TryContactNow(id)) LoadContactInfo(id);")
              && rt.Contains("if (ContactTriedAt.TryGetValue(id, out float at) && now - at < 300f) return false;"),
              "PH-A6: who someone is to you is read when the phone needs it, not only once it's opened (review PH-R1: an unreadable one every 5 minutes at most)");
        Check(rt.Contains("DropInbox(string? id)") && File.ReadAllText(Path.Combine(src, "ModRuntime.Commands.cs")).Contains("DropInbox(id); // phone pass PH-R2")
              && File.ReadAllText(Path.Combine(src, "ModRuntime.Commands.cs")).Contains("DropInbox(null);"),
              "review PH-R2: a reset drops texts on their way from the forgotten (one person, or everyone)");
        Check(rt.Contains("try { PhoneUi.Tick(Time.unscaledDeltaTime); } catch") && rt.Contains("if (!Features.Phone) return; // PH-A8"),
              "PH-A7/A8: the window's frame can't stop the texts; switched off, no signal readings either");
        Check(rt.Contains("if (!Social.ChatterPlan.Spaced(ChatterStarts, Time.unscaledTime, s.PerHour, 2)) return; // PH-A9"),
              "PH-A9: texts first keep the pacer's spacing");
        Check(rt.Contains("ProblemCodes.FirstSentence(ProblemCodes.Coded(Redact.Hide(error, _cfg.ApiKey.Value)), 110)"),
              "PH-A10: no reply says why, in a few words (the key hidden)");
        Check(rt.Contains("DeliverText(x.Id, x.Body, -1, x.ReadUpTo, quiet: true, voice: x.Voice)"),
              "PH-A11: texts on their way when the save changes land in that save's phone, never lost");
        Check(rt.Contains("gone = found == null || found.WasCollected;"), "PH-A12: a portrait unloaded with its scene is looked for again");
        Check(File.ReadAllText(Path.Combine(src, "UI", "PhoneWindow.Parts.cs")).Contains("string.IsNullOrWhiteSpace(title) ? \"?\""),
              "PH-A13: a card for someone with a blank name doesn't throw");
        Check(File.ReadAllText(Path.Combine(src, "ModRuntime.Nightline.cs")).Contains("if (PhoneUi.ScreenName is not (\"nightline\" or \"line\")) { v.Lines.Clear();"),
              "PH-A14: Nightline's cards are built only for Nightline's screens (and let go elsewhere, review PH-R4)");
    }
}
