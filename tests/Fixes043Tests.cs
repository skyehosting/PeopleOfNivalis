using System;
using System.Collections.Generic;
using System.Linq;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.UI;

namespace NivalisAIChat.Tests;

/// <summary>0.4.3 FB-51: fixes from the checklist round (the parts that can be tested without the game).</summary>
internal static partial class Program
{
    private static void Fixes043Tests()
    {
        Console.WriteLine("-- 0.4.3 FB-51: fixes from testing");
        try { Fixes043(); }
        finally { Features.Reset(); }
    }

    private static void Fixes043()
    {
        // /usage: a "Used:" line's percentage in the colour of how close it is to its limit.
        string ok = ConsoleText.Format("  Used: 1,200 of 200,000 tokens this day (1%)");
        string near = ConsoleText.Format("  Used: 160,000 of 200,000 tokens this day (80%)");
        string over = ConsoleText.Format("  Used: 210,000 of 200,000 tokens this day (105%)");
        Check(ok.Contains($"<color={ConsoleText.LabelHex}>") && ok.Contains($"<color={ConsoleText.OkHex}>") && near.Contains($"<color={ConsoleText.NearHex}>")
              && over.Contains($"<color={ConsoleText.OverHex}>") && over.Contains("105%"),
              "/usage: each Used line's % in green, amber from 75%, red at or past its limit");
        Check(!ConsoleText.Format("Chance: 50%").Contains(ConsoleText.OkHex) && !ConsoleText.Format("  Starts again: 00:00").Contains(ConsoleText.OkHex),
              "...only on Used lines (a chance or another figure keeps its colour)");
        Check(ConsoleText.Format("Your AI:").Contains(ConsoleText.HeaderHex), "...each budget under its own heading");

        // Muttering / Overhearing off: what was muttered or overheard before isn't told either.
        var rec = new NpcRecord { Id = "p_fb51", Persona = new Persona { Name = "Ada Venn", Age = "40" }, Visual = new NpcVisual { Body = "woman" } };
        rec.Talks.Add(new Talk { Kind = Talk.Thought, GameDay = 4, Summary = "worried about rent", Lines = { "Ada: Rent's due again." } });
        rec.Talks.Add(new Talk { Kind = Talk.Overheard, GameDay = 5, Where = "the market", Lines = { "Rin: The docks close early tonight." } });
        var snap = new GameSnapshot { GameDay = 6, Hour = 12 };
        Features.Reset();
        string on = Prompts.SituationBlock(rec, snap);
        Check(on.Contains("[ON YOUR MIND LATELY]") && on.Contains("[OVERHEARD LATELY]"), "on: what they muttered and overheard is told, as before");
        Features.Muttering = false;
        string noMutter = Prompts.SituationBlock(rec, snap);
        Features.Muttering = true;
        Features.Overhearing = false;
        string noOverhear = Prompts.SituationBlock(rec, snap);
        Check(!noMutter.Contains("[ON YOUR MIND LATELY]") && noMutter.Contains("[OVERHEARD LATELY]")
              && !noOverhear.Contains("[OVERHEARD LATELY]") && noOverhear.Contains("[ON YOUR MIND LATELY]"),
              "Muttering off: their old thoughts aren't told; Overhearing off: nothing overheard is told (each on its own)");
        Features.Reset();

        // FB-53: a story scene after your last chat with them is "just now" (Banor answered from the older AI chats).
        var lastChat = DateTime.UtcNow.AddMinutes(-8);
        var newer = new List<StoryBook.Scene> { new() { GameDay = 2, Utc = lastChat.AddMinutes(5), Lines = { new StoryBook.Line { Who = StoryBook.Them, Text = "The bar won't clean itself." } } } };
        var older = new List<StoryBook.Scene> { new() { GameDay = 2, Utc = lastChat.AddMinutes(-5), Lines = { new StoryBook.Line { Who = StoryBook.Them, Text = "The bar won't clean itself." } } } };
        var fromHistory = new List<StoryBook.Scene> { new() { GameDay = 2, Utc = DateTime.MinValue, Lines = { new StoryBook.Line { Who = StoryBook.Them, Text = "The bar won't clean itself." } } } };
        Check(Prompts.StoryBlock(newer, "Rin", lastChatUtc: lastChat) is var nb && nb.Contains("Day 2 (just now):") && nb.Contains("it's what you were just talking about"),
              "a story scene after your last chat is told as just now, and as what you were just talking about");
        Check(!Prompts.StoryBlock(older, "Rin", lastChatUtc: lastChat).Contains("just now") && !Prompts.StoryBlock(fromHistory, "Rin", lastChatUtc: lastChat).Contains("just now"),
              "...not one before your last chat, nor one read from the game's chat history");
        Check(Prompts.StoryBlock(newer, "Rin").Contains("(just now)"), "...and with no chat before, the newest scene is just now");
        var daysAgo = new List<StoryBook.Scene> { new() { GameDay = 1, Utc = DateTime.UtcNow.AddDays(-3), Lines = { new StoryBook.Line { Who = StoryBook.Them, Text = "Old news." } } } };
        Check(!Prompts.StoryBlock(daysAgo, "Rin").Contains("just now") && !Prompts.StoryBlock(daysAgo, "Rin", lastChatUtc: DateTime.UtcNow.AddDays(-5)).Contains("just now"),
              "...but a scene from days ago never is (only the last 20 minutes)");
    }
}
