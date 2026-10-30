using System;
using System.Collections.Generic;
using System.Linq;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Tests;

/// <summary>FB-36: residents remember what the game showed them doing.</summary>
internal static partial class Program
{
    private static void DoingsTests()
    {
        Console.WriteLine("-- FB-36: what residents were seen doing (meals out, shifts at work)");
        var rec = new NpcRecord { Id = "p_doer", Persona = new Persona { Name = "Ana Reyes", Age = "33" } };
        Doing Meal(int day, string place, string dish = "") => new() { GameDay = day, Kind = Doings.Meal, Place = place, What = dish };
        Doing Work(int day, string place, string role = "") => new() { GameDay = day, Kind = Doings.Work, Place = place, What = role };

        Check(Doings.Note(rec, Meal(3, "Izakaya Stop")) && rec.Doings.Single().Text == "Ate at Izakaya Stop.", "a meal seen before the dish is known: \"Ate at Izakaya Stop.\"");
        Check(Doings.Note(rec, Meal(3, "Izakaya Stop", "noodle soup")) && rec.Doings.Single().Text == "Had noodle soup at Izakaya Stop.",
              "...becomes \"Had noodle soup at Izakaya Stop.\" when the dish turns up (one line, not two)");
        Check(!Doings.Note(rec, Meal(3, "izakaya stop", "noodle soup")) && !Doings.Note(rec, Meal(3, "Izakaya Stop")), "the same meal again is nothing new");
        for (int d = 4; d <= 8; d++) Doings.Note(rec, Meal(d, "Ancient Spirit", "rice"));
        Check(rec.Doings.Count(x => x.Kind == Doings.Meal) == Doings.MaxMeals && rec.Doings.Where(x => x.Kind == Doings.Meal).Min(x => x.GameDay) == 5,
              $"at most {Doings.MaxMeals} meals kept, the newest");

        Check(Doings.Note(rec, Work(5, "Ancient Spirit", "cook")) && rec.Doings.Any(x => x.Text == "On shift at Ancient Spirit (cook)."), "a shift: \"On shift at Ancient Spirit (cook).\"");
        Check(!Doings.Note(rec, Work(5, "Ancient Spirit", "cook")), "...the same day again is nothing new");
        Check(Doings.Note(rec, Work(7, "Ancient Spirit", "cook")) && rec.Doings.Count(x => x.Kind == Doings.Work) == 1 && rec.Doings.First(x => x.Kind == Doings.Work).GameDay == 7,
              "...a later day moves the one line along (a workplace isn't listed once a day)");
        for (int i = 0; i < 4; i++) Doings.Note(rec, Work(10 + i, "Place " + i));
        Check(rec.Doings.Count(x => x.Kind == Doings.Work) == Doings.MaxWork, $"at most {Doings.MaxWork} workplaces");

        var ordered = new NpcRecord { Id = "p_ordered", Talks = { new Talk { Kind = Talk.Service, GameDay = 2, Where = "Izakaya Stop", Summary = "Ordered ramen at Izakaya Stop." } } };
        Check(!Doings.Note(ordered, Meal(2, "Izakaya Stop", "ramen")) && ordered.Doings.Count == 0, "a meal already kept as their order in their own words isn't noted twice (FB-34)");
        Check(!Doings.Note(rec, Meal(9, "")) && !Doings.Note(rec, new Doing { GameDay = 9, Kind = "nap", Place = "Home" }), "nothing without a place, nothing of another kind");

        // Where it shows: the chat's [YOUR LIFE LATELY], at most two, with their life updates (never pushing them out).
        var life = new NpcRecord { Id = "p_life", Persona = new Persona { Name = "Bo Lund", Age = "40" } };
        for (int i = 1; i <= 6; i++) life.LifeLog.Add(new LifeEvent { GameDay = i, Text = "Life event " + i + "." });
        Doings.Note(life, Meal(6, "Izakaya Stop", "dumplings"));
        Doings.Note(life, Work(6, "The Docks"));
        Doings.Note(life, Meal(2, "Old Place", "tea"));
        string block = Prompts.SituationBlock(life, new GameSnapshot { GameDay = 6 });
        Check(block.Contains("- Day 6: Had dumplings at Izakaya Stop.") && block.Contains("- Day 6: On shift at The Docks.") && !block.Contains("Old Place"),
              "the chat knows the newest two things they were seen doing");
        Check(Enumerable.Range(1, 6).All(i => block.Contains("Life event " + i + ".")), "...and every life update it showed before is still there");
        Check(life.LifeLog.Count == 6, "their life log isn't touched");

        // Memory search: meals, not shifts (a shift line moves every day).
        var mem = Memories.From(life);
        Check(mem.Any(e => e.Text == "Had dumplings at Izakaya Stop.") && !mem.Any(e => e.Text.StartsWith("On shift")), "memory search finds meals out, not shifts");
        Check(Memories.From(life).Select(e => e.Key).SequenceEqual(mem.Select(e => e.Key)), "...with the same keys each time (indexed once)");

        // A record from before 0.3.3 (no "Doings" in the file) reads fine.
        var old = System.Text.Json.JsonSerializer.Deserialize<NpcRecord>("{\"Id\":\"p_old\",\"Doings\":null}", NpcStore.Json)!;
        old.Repair();
        Check(old.Doings != null && Doings.Note(old, Meal(1, "Stall")), "an older record (no doings, or null) is repaired and takes one");
    }
}
