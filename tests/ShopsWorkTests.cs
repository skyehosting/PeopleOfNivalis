using System;
using System.Collections.Generic;
using System.Linq;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Tests;

/// <summary>FB-57 shop people (what they sell, known by the name the game shows) and FB-58 your staff's work life.</summary>
internal static partial class Program
{
    private static void ShopsWorkTests()
    {
        Console.WriteLine("-- FB-57/58: shop people and your staff's work life");
        try { ShopsWork(); }
        finally { Features.Reset(); }
    }

    private static void ShopsWork()
    {
        // Names as the game stores them, said the way people say them.
        Check(Shops.Humanize("Item_FreshTomato") == "Fresh Tomato" && Shops.Humanize("fish_mackerel") == "fish mackerel" && Shops.Humanize("SEAWEED") == "seaweed"
              && Shops.Humanize("Kelp Noodles") == "Kelp Noodles", "item names made readable (asset-style ones), real names kept");
        Check(Shops.StockWord(0, 20) == "sold out" && Shops.StockWord(4, 20) == "running low" && Shops.StockWord(15, 20) == "in stock", "stock in words");

        var shop = new ShopFacts
        {
            Name = "Mara's Greens", Trade = "Produce", Regular = 0.5, Deal = "Fresh Tomato",
            Items =
            {
                new ShopItem { Name = "Kelp", Price = 4, Stock = 18, MaxStock = 20 },
                new ShopItem { Name = "Fresh Tomato", Price = 3, Stock = 10, MaxStock = 20, Market = "plenty" },
                new ShopItem { Name = "Mushrooms", Price = 9, Stock = 2, MaxStock = 20, Market = "short" },
                new ShopItem { Name = "Chili", Price = 5, Stock = 0, MaxStock = 10 },
            },
        };
        var lines = Shops.Lines(shop);
        string all = string.Join("\n", lines);
        Check(all.Contains("You run Mara's Greens, selling produce") && all.Contains("never contradict"), "a shop keeper is told what they run and sell, as a fact");
        Check(all.Contains("Kelp 4 Lims") && all.Contains("Mushrooms 9 Lims, running low") && !all.Contains("Chili 5"), "...what's for sale with prices, low stock said, nothing sold out offered");
        Check(all.Contains("Sold out today: Chili") && all.Contains("Today's deal: Fresh Tomato"), "...what's sold out, and today's deal");
        Check(lines.First(l => l.StartsWith("For sale")).IndexOf("Fresh Tomato", StringComparison.Ordinal) < lines.First(l => l.StartsWith("For sale")).IndexOf("Kelp", StringComparison.Ordinal),
              "...the deal named first");
        Check(all.Contains("short of Mushrooms") && all.Contains("plenty of Fresh Tomato") && all.Contains("a regular of yours"), "...the market's shortage and surplus on their goods, and the player as a regular");
        Check(all.Contains("Never read out the whole list"), "...and to answer like a vendor, not read the list");
        Check(Shops.Lines(null).Count == 0 && Shops.Lines(new ShopFacts()).Count == 0, "no shop: nothing told");
        var many = new ShopFacts { Trade = "Fish" };
        for (int i = 0; i < 20; i++) many.Items.Add(new ShopItem { Name = "Fish " + i, Price = 5, Stock = 5, MaxStock = 10 });
        Check(Shops.Lines(many).Any(l => l.EndsWith("and more.")) && Shops.Lines(many).First(l => l.StartsWith("For sale")).Split(';').Length == Shops.MaxItems + 1,
              "a long stock list is cut to the first dozen, 'and more'");

        // The chat carries it.
        var rec = new NpcRecord { Id = "p_shop", Persona = new Persona { Name = "Mara Venn", Age = "35" }, Visual = new NpcVisual { Body = "woman", NameShown = true, GameName = "Mara Venn" } };
        var snap = new GameSnapshot { GameDay = 3, Hour = 14, Shop = shop };
        string sit = Prompts.SituationBlock(rec, snap);
        Check(sit.Contains("[YOUR SHOP]") && sit.Contains("For sale right now"), "the chat has [YOUR SHOP] for a shop keeper");
        Check(!Prompts.SituationBlock(rec, new GameSnapshot { GameDay = 3, Hour = 14 }).Contains("[YOUR SHOP]"), "...and not for anyone else");

        // Known by the name the game shows: someone met before under an invented identity takes it, memories kept.
        var met = new NpcRecord
        {
            Id = "p_met", Persona = new Persona { Name = "Jingwei Han", Occupation = "Night security guard", Background = "Jingwei Han grew up by the docks." },
            MemorySummary = "Jingwei talked with the stranger about the docks.", Visual = new NpcVisual { Body = "woman" },
        };
        met.Talks.Add(new Talk { Kind = Talk.InPerson, GameDay = 2, Summary = "a chat", Lines = { "Jingwei: Evening." } });
        var seen = new NpcVisual { Body = "woman", NameShown = true, GameName = "Lin Okafor", ShopName = "Docks Fish Stall", Trade = "Fish" };
        string changed = Shops.KnownByShownName(met, seen);
        Check(met.Persona.Name == "Lin Okafor" && met.State.NameRevealed && met.State.KnownAs == "Lin Okafor" && met.Visual.NameShown && met.Visual.GameName == "Lin Okafor",
              "someone met as 'Jingwei Han' is now known by the game's name, Lin Okafor (never Stranger)");
        Check(met.Persona.Occupation == "runs Docks Fish Stall (fish)" && met.Persona.Workplace == "Docks Fish Stall", "...their job is their shop");
        Check(met.MemorySummary == "Lin talked with the stranger about the docks." && met.Persona.Background.StartsWith("Lin Okafor grew up") && met.Talks.Count == 1,
              "...what they remember is kept, under their real name");
        Check(changed.Contains("was Jingwei Han") && changed.Contains("was Night security guard"), "...and the log says what changed");
        Check(Shops.KnownByShownName(met, seen) == "", "...once, not every meeting");
        Check(met.Persona.FormerName == "Jingwei Han", "...the old name kept, so a wrong rename can be undone");
        var sam = new NpcRecord { Persona = new Persona { Name = "Sam", Background = "Sam works with Samantha; Sam's sister." }, MemorySummary = "Samuel met Sam." };
        Shops.KnownByShownName(sam, new NpcVisual { NameShown = true, GameName = "Sam Rivera$1" });
        Check(sam.Persona.Name == "Sam Rivera$1" && sam.Persona.Background == "Sam Rivera$1 works with Samantha; Sam Rivera$1's sister." && sam.MemorySummary == "Samuel met Sam Rivera$1.",
              "the old name swapped as a whole word only (Samantha, Samuel stay), and a '$' in a name does no harm");
        var ana = new NpcRecord { Persona = new Persona { Name = "Anabel Cruz", Background = "Anabel Cruz grew up with Anabella." }, MemorySummary = "Anabel laughed. Anabella didn't." };
        Shops.KnownByShownName(ana, new NpcVisual { NameShown = true, GameName = "Ines Moro" });
        Check(ana.Persona.Background == "Ines Moro grew up with Anabella." && ana.MemorySummary == "Ines laughed. Anabella didn't.",
              "...whole words only, the first name in the background too");
        var plain = new NpcRecord { Persona = new Persona { Name = "Ada" } };
        Check(Shops.KnownByShownName(plain, new NpcVisual { GameName = "Female026" }) == "" && plain.Persona.Name == "Ada" && !plain.State.NameRevealed,
              "a passer-by the game doesn't name keeps the mod's name (still a stranger)");
        Check(new NpcVisual { NameShown = true }.KnownByName() && new NpcVisual { Named = true }.KnownByName() && !new NpcVisual().KnownByName(), "known by name: story, staff, or shown by the game");
        var shownRec = new NpcRecord { Persona = new Persona { Name = "Lin Okafor", Age = "30" }, Visual = new NpcVisual { NameShown = true, GameName = "Lin Okafor" } };
        string sys = Prompts.IdentityBlock(shownRec) + Prompts.SituationBlock(shownRec, new GameSnapshot { Hour = 12 });
        Check(!sys.Contains("You haven't told them your name") && sys.Contains("Your name is no secret"), "...and one the game names isn't told to keep their name back");

        // FB-58: your staff's work life.
        Check(WorkLife.InShift(9, 17, 9) && WorkLife.InShift(9, 17, 16) && !WorkLife.InShift(9, 17, 17) && WorkLife.InShift(22, 6, 23) && WorkLife.InShift(22, 6, 3) && !WorkLife.InShift(22, 6, 12),
              "a shift's hours, past midnight too");
        Check(WorkLife.ShiftLength(9, 17) == 8 && WorkLife.ShiftLength(22, 6) == 8 && WorkLife.ShiftLength(-1, 5) == 0, "...and its length");
        var st = new WorkLifeState();
        WorkReading R(int day, int hour, int wage = 40, string happy = "Normal", int customers = 0, int wageInf = 0) =>
            new() { Day = day, Hour = hour, Place = "Ancient Spirit", Happiness = happy, Wage = wage, BaseMin = 35, BaseMax = 50, ShiftStart = 10, ShiftEnd = 22, PreferredHours = 8, Customers = customers, WageInfluence = wageInf };
        Check(WorkLife.Step(st, R(1, 8)).Count == 0 && st.LastWage == 40 && st.LastHappiness == "Normal", "the first look only takes note");
        var cut = WorkLife.Step(st, R(1, 9, wage: 30));
        Check(cut.Count == 1 && cut[0].Kind == WorkLife.Cut && cut[0].Text.Contains("from 40 to 30") && cut[0].Mood < 0, "a pay cut is remembered, and sours them on you");
        Check(WorkLife.Step(st, R(1, 9, wage: 45)).Count == 0 && st.LastWage == 45, "...a second change the same day isn't another event (the wage is followed)");
        WorkLife.Step(st, R(1, 9, wage: 30));
        var flap = new WorkLifeState();
        WorkLife.Step(flap, R(4, 8, wage: 40));
        Check(WorkLife.Step(flap, R(4, 9, wage: 41)).Count == 0 && WorkLife.Step(flap, R(4, 10, wage: 40)).Count == 0, "a wage that wobbles by 1 Lim isn't a raise or a cut");
        var sad = WorkLife.Step(st, R(1, 9, wage: 30, happy: "Sad", wageInf: -2));
        Check(sad.Count == 1 && sad[0].Kind == WorkLife.Unhappy && sad[0].Text.Contains("the pay isn't enough"), "...growing unhappy, with the game's reason");
        Check(WorkLife.Step(st, R(1, 9, wage: 30, happy: "Sad")).Count == 0, "...the same hour again: nothing new");
        var raise = WorkLife.Step(st, R(2, 10, wage: 45, happy: "Normal"));
        Check(raise.Any(e => e.Kind == WorkLife.Raise && e.Mood > 0) && raise.Any(e => e.Kind == WorkLife.Better), "a raise, and the job feeling better");
        for (int h = 11; h < 22; h++) WorkLife.Step(st, R(2, h, wage: 45, customers: h < 16 ? 8 : 2));
        var ended = WorkLife.Step(st, R(2, 22, wage: 45));
        Check(ended.Count == 1 && ended[0].Kind == WorkLife.WornOut && ended[0].Text.Contains("12 hours"), "a 12-hour shift with 5 packed hours: they went home worn out");
        var st2 = new WorkLifeState();
        WorkLife.Step(st2, R(2, 9));
        for (int h = 10; h < 18; h++) WorkLife.Step(st2, R(2, h));
        var shortShift = new WorkReading { Day = 2, Hour = 18, Place = "Ancient Spirit", Happiness = "Normal", Wage = 40, ShiftStart = 10, ShiftEnd = 18, PreferredHours = 8 };
        Check(WorkLife.Step(st2, shortShift).Count == 0, "an ordinary 8-hour shift is nothing to remember");

        var worker = new NpcRecord { Id = "p_staff", Persona = new Persona { Name = "Banor", Age = "40" }, Visual = new NpcVisual { Named = true, Employee = true } };
        double before = worker.State.Relationship;
        foreach (var e in cut.Concat(sad)) WorkLife.Remember(worker, e);
        Check(worker.State.Relationship < before && worker.WorkEvents.Count == 2, "remembered events move how they feel about you, gently");
        Features.Relationships = false;
        double frozen = worker.State.Relationship;
        WorkLife.Remember(worker, raise[0]);
        Check(worker.State.Relationship == frozen && worker.WorkEvents.Count == 3, "...not with Relationships off (the event is still remembered)");
        Features.Relationships = true;
        for (int i = 0; i < 10; i++) WorkLife.Remember(worker, new WorkEvent { GameDay = 5, Kind = WorkLife.LongShift, Text = "A long shift.", Mood = 0 });
        Check(worker.WorkEvents.Count == WorkLife.MaxEvents, "only the newest few are kept");

        Check(WorkLife.Pay(R(1, 1, wage: 30)).Contains("less than the usual 35-50") && WorkLife.Pay(R(1, 1, wage: 60)).Contains("more than the usual")
              && WorkLife.Pay(R(1, 1, wage: 40)).Contains("about the usual"), "pay against the usual for the job");
        Check(WorkLife.Pay(new WorkReading { Wage = 40, BaseMin = 4000, BaseMax = 9000 }) == "40 Lims a shift", "...a range that makes no sense isn't told");
        var jobLines = string.Join("\n", WorkLife.Lines(R(3, 12, wage: 30, happy: "Sad", wageInf: -1), worker.WorkEvents));
        Check(jobLines.Contains("you're unhappy in this job (the pay isn't enough)") && jobLines.Contains("10:00 to 22:00 (12 hours); you'd rather work about 8")
              && jobLines.Contains("Lately at work"), "the chat's job facts: how it feels, the pay, the hours, what happened");
        var staffSnap = new GameSnapshot { GameDay = 3, Hour = 12, NpcWorkplace = "Ancient Spirit", NpcWorksForPlayer = true, Job = R(3, 12) };
        Check(Prompts.SituationBlock(worker, staffSnap).Contains("[YOUR JOB HERE]"), "your staff's chat has [YOUR JOB HERE]");
        Features.WorkLife = false;
        Check(!Prompts.SituationBlock(worker, staffSnap).Contains("[YOUR JOB HERE]"), "...not with WorkLife off");
        Features.WorkLife = true;
        Check(!Prompts.SituationBlock(worker, new GameSnapshot { GameDay = 3, Hour = 12, NpcWorkplace = "Somewhere", Job = R(3, 12) }).Contains("[YOUR JOB HERE]"),
              "...and not for someone else's staff");
    }
}
