using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NivalisAIChat.Social;

namespace NivalisAIChat.Tests;

/// <summary>
/// FB-63: who's together (Social/Couples.cs): the player's partners, residents' couples, secrets that reach the partner
/// (seen at once, gossip later and not always), and what the residents' conversation is told (one turn of the story
/// at most, decided by the code), applied once heard.
/// </summary>
internal static partial class Program
{
    private static void CouplesTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- FB-63: who's together, secrets, residents' love lives");
        string dir = Path.Combine(Path.GetTempPath(), "pon-couples-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "romance.json");
        try
        {
            var book = new CoupleBook(file, _ => { });
            book.SetPartner("npc_mae", "Mae", "dating", 3);
            book.SetPartner("npc_mae", "Mae", "living", 3);
            Check(book.Partners().Count == 1 && book.Partners()[0].Status == "living", "your partner kept once, as they are now");
            Check(book.Start("a", "Ana", "b", "Bo", 5) && !book.Start("a", "Ana", "c", "Cy", 5) && book.Together("b", "a") && book.CoupleOf("b")?.A == "a",
                "residents get together, one partner at a time");

            // Secrets: seen = known at once; heard = a day or more later, and not always.
            var seen = book.NoteSecret(CoupleBook.Player, "you", "npc_kit", "Kit", "npc_mae", 7, seen: true);
            Check(seen != null && book.KnownBy("npc_mae", 7).Any(s => s.Id == seen.Id), "a partner who saw it knows at once");
            int heard = 0;
            for (int i = 0; i < 200; i++)
            {
                var b2 = new CoupleBook(Path.Combine(dir, $"g{i}.json"), _ => { });
                var s = b2.NoteSecret("x" + i, "X", "y", "Y", "p", 10, seen: false)!;
                if (b2.KnownBy("p", 10).Count > 0) { heard = -999; break; } // never on the same day
                if (b2.KnownBy("p", 11).Any(k => k.Id == s.Id)) heard++;
            }
            Check(heard is > 90 and < 150, $"gossip reaches the partner a day later about {CoupleBook.GossipChance:P0} of the time ({heard}/200), never the same day");
            book.Settle(seen!.Id, grudge: true);
            Check(!book.KnownBy("npc_mae", 8).Any() && book.Grudges().Any(g => g.Id == seen.Id), "had out: no longer news, a grudge stays");

            // The residents' conversation.
            var pairs = new Dictionary<string, PairBook.Pair>();
            PairBook.Pair? Pair(string x, string y) => pairs.TryGetValue(PairBook.Key(x, y), out var p) ? p : null;
            void Close(string x, string y, int aff, int times) => pairs[PairBook.Key(x, y)] = new PairBook.Pair { A = x, B = y, Affinity = aff, Times = times };
            var people = new List<CoupleBook.Person> { new() { Id = "a", Name = "Ana" }, new() { Id = "b", Name = "Bo" } };
            var (notes, turn) = book.Scene(people, Pair, 6, new Random(1), residentsDate: true, jealousy: true);
            Check(notes.Any(n => n.Contains("Ana and Bo are a couple")) && turn == null, "a couple talking: said, nothing else decided");

            Close("c", "d", 60, 5);
            var cd = new List<CoupleBook.Person> { new() { Id = "c", Name = "Cy" }, new() { Id = "d", Name = "Dee" } };
            int sparks = 0;
            for (int i = 0; i < 400; i++) if (book.Scene(cd, Pair, 6, new Random(i), true, true).turn?.Kind == "spark") sparks++;
            Check(sparks is > 15 and < 70, $"two who've grown close sometimes get together ({sparks}/400)");
            Check(book.Scene(cd, Pair, 6, new Random(3), residentsDate: false, jealousy: true).turn == null, "...never with ResidentsDate off");
            Close("c", "d", 30, 5);
            Check(Enumerable.Range(0, 200).All(i => book.Scene(cd, Pair, 6, new Random(i), true, true).turn == null), "...and not unless they're close");

            // Cheating: Ana (with Bo, not here) flirts with Cy, whom she gets on well with.
            Close("a", "c", 40, 4);
            var ac = new List<CoupleBook.Person> { new() { Id = "a", Name = "Ana" }, new() { Id = "c", Name = "Cy" } };
            CoupleBook.Turn? cheat = null;
            for (int i = 0; i < 400 && cheat == null; i++) { var t = book.Scene(ac, Pair, 8, new Random(i), true, true).turn; if (t?.Kind == "cheat") cheat = t; }
            Check(cheat != null && cheat.A == "a" && cheat.B == "c" && cheat.Partner == "b", "someone with a partner who isn't there may flirt with someone else");
            Check(book.Scene(ac, Pair, 8, new Random(1), true, jealousy: false).notes.Any(n => n.Contains("Ana is with Bo (not here)")),
                "...their partner is said even when nothing happens");
            book.Apply(cheat!, 8);
            // Over 16 days (the secret's id is random: four days left it unheard 2.6% of runs).
            var secret = Enumerable.Range(9, 16).SelectMany(d => book.KnownBy("b", d)).FirstOrDefault();
            Check(secret != null && secret.Cheater == "a" && secret.With == "c", "...it becomes a secret that reaches Bo (in time)");

            // Bo, who knows, with Ana: they have it out.
            pairs[PairBook.Key("a", "b")] = new PairBook.Pair { A = "a", B = "b", Affinity = 10, Times = 9 };
            var (confrontNotes, confront) = book.Scene(people, Pair, 13, new Random(2), true, true);
            Check(confront?.Kind == "confront" && confront.A == "b" && confront.B == "a" && confrontNotes.Any(n => n.Contains("Bo found out that Ana was flirting with Cy")),
                "the partner who found out has it out with them");
            confront!.Breakup = true;
            book.Apply(confront, 13);
            Check(!book.Together("a", "b") && book.Grudges().Any(g => g.Partner == "b" && g.With == "c"), "...a breakup ends the couple; the grudge against Cy stays");

            // Bo meets Cy: a jealous scene, then the grudge is spent.
            var bc = new List<CoupleBook.Person> { new() { Id = "b", Name = "Bo" }, new() { Id = "c", Name = "Cy" } };
            var (jealousNotes, jealous) = book.Scene(bc, Pair, 14, new Random(4), true, true);
            Check(jealous?.Kind == "jealous" && jealousNotes.Any(n => n.Contains("cold and jealous") && n.Contains("nothing physical")), "the jealous partner faces the one their partner flirted with (words only)");
            book.Apply(jealous!, 14);
            Check(!book.Grudges().Any(g => g.Partner == "b" && g.With == "c"), "...and then it's spent");

            // Kept per save, forgotten with someone, cleared.
            var again = new CoupleBook(file, _ => { });
            Check(again.Partners().Count == 1 && again.Couples().Count == 0 && again.Grudges().Count >= 1, "kept in romance.json");
            again.Forget("npc_mae");
            Check(again.Partners().Count == 0 && again.Grudges().All(g => g.Partner != "npc_mae"), "someone forgotten (a reset, under 18) is gone from it");
            again.Clear();
            Check(new CoupleBook(file, _ => { }).Grudges().Count == 0, "/resetall clears it");
            Check(Npc.SaveSnapshots.PerSave.Contains("romance.json"), "romance.json belongs to the save (FB-61)");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string brain = File.ReadAllText(Path.Combine(src, "Npc", "ChatterBrain.cs"));
        string prompts = File.ReadAllText(Path.Combine(src, "Ai", "ChatterPrompts.cs"));
        Check(brain.Contains("people.Any(p => _brain.KnownMinors.Contains(p.Record.Id))") && brain.Contains("!_brain.KnownMinors.Contains(turn.A)"),
            "never anyone under 18 in a love life");
        Check(brain.Contains("spoke.Any(p => p.Record.Id == turn.A) && spoke.Any(p => p.Record.Id == turn.B)") && prompts.Contains("[LOVE LIVES] (decided"),
            "the residents are told what happens, and it's kept only once both of them spoke");
    }
}
