using System;
using System.Collections.Generic;
using System.Linq;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using NivalisAIChat.Voice;

namespace NivalisAIChat.Tests;

/// <summary>0.4.0 FB-45: a feature switched off in the Features tab is skipped everywhere it acts (and comes back as it was).</summary>
internal static partial class Program
{
    private static void Fixes040Tests()
    {
        Console.WriteLine("-- 0.4.0 FB-45: features switched off");
        try { FeatureGates(); }
        finally { Features.Reset(); }
    }

    private static void FeatureGates()
    {
        var rec = new NpcRecord { Id = "p_feat", Persona = new Persona { Name = "Ada Venn", Age = "40", Temperament = "warm" }, Visual = new NpcVisual { Body = "woman" } };
        rec.State.Mood = "cheerful";
        rec.State.MoodIntensity = "strong";
        rec.State.MoodReason = "payday";
        rec.State.Affinity = 80; // a firm friend
        rec.State.TimesMet = 5;
        var snap = new GameSnapshot { Hour = 14, GameDay = 3, Menu = new List<MenuEntry> { new() { Dish = "Spicy Ramen", Price = 40 } } };

        // Everything on: as before.
        Features.Reset();
        string schemaOn = Prompts.ReplySchema().ToJsonString(), idOn = Prompts.IdentityBlock(rec), notesOn = Prompts.IdentityBlock(rec, structured: false);
        string sitOn = Prompts.SituationBlock(rec, snap, new List<string> { "calm", "cheerful" });
        Check(schemaOn.Contains("\"mood\"") && schemaOn.Contains("affinityChange") && idOn.Contains("- mood:") && idOn.Contains("affinityChange")
              && notesOn.Contains("[[mood:") && notesOn.Contains("[[affinity:") && sitOn.Contains("Right now you feel cheerful") && sitOn.Contains("Your mood so far"),
              "all on: moods and how they feel about you are asked for and told, as before");

        // Moods off.
        Features.Moods = false;
        string schema = Prompts.ReplySchema().ToJsonString(), id = Prompts.IdentityBlock(rec), notes = Prompts.IdentityBlock(rec, structured: false);
        string sit = Prompts.SituationBlock(rec, snap, new List<string> { "calm", "cheerful" });
        string described = ChatterPrompts.Describe(new ChatterPrompts.Person { Record = rec, Snap = snap });
        Check(!schema.Contains("\"mood\"") && !schema.Contains("moodIntensity") && !id.Contains("- mood:") && !notes.Contains("[[mood:") && !notes.Contains("Always give your mood")
              && !sit.Contains("you feel") && !sit.Contains("Your mood so far") && !described.Contains("Mood:"),
              "moods off: no mood is asked for, and none is told (chat and residents' talk)");
        Check(schema.Contains("affinityChange") && sit.Contains(Prompts.AffinityWords(rec.State.Affinity)), "...how they feel about you still is");
        Check(VoiceSubject.From(rec).Mood == "", "...their voice gets no mood's tone");
        int budgetOff = Patience.Budget(rec, snap, Features.Mood("in a hurry"), 1.0);
        Features.Moods = true;
        int budgetHurry = Patience.Budget(rec, snap, Features.Mood("in a hurry"), 1.0);
        Features.Moods = false;
        Check(budgetOff > budgetHurry, "...a hurried mood no longer shortens their patience");
        Check(ChatterPlan.JoinInChance(true, 0, openMood: false) == ChatterPlan.JoinInChance(true, 0, openMood: true), "...nor a guarded mood their joining in");
        var (gk, _) = GreetingRules.Decide(rec.State, "angry", 100, 3);
        Check(gk is GreetingRules.Kind.Wave or GreetingRules.Kind.WaveOver, "...an angry roll doesn't stop a friend waving");
        Check(rec.State.Mood == "cheerful" && rec.State.MoodReason == "payday", "...and their mood is kept for when moods are back");
        Features.Moods = true;

        // Relationships off.
        Features.Relationships = false;
        schema = Prompts.ReplySchema().ToJsonString();
        id = Prompts.IdentityBlock(rec);
        notes = Prompts.IdentityBlock(rec, structured: false);
        sit = Prompts.SituationBlock(rec, snap);
        described = ChatterPrompts.Describe(new ChatterPrompts.Person { Record = rec, Snap = snap });
        Check(!schema.Contains("affinityChange") && !id.Contains("affinityChange") && !notes.Contains("[[affinity:") && !sit.Contains(Prompts.AffinityWords(rec.State.Affinity))
              && schema.Contains("\"mood\""), "relationships off: how they feel about you is neither asked for nor told (moods still are)");
        var (kind, _) = GreetingRules.Decide(rec.State, "cheerful", 100, 3);
        Check(kind is not (GreetingRules.Kind.Wave or GreetingRules.Kind.WaveOver), "...a friend no longer waves you over for being a friend");
        rec.State.Affinity = -90;
        var (snub, _) = GreetingRules.Decide(rec.State, "cheerful", 100, 3);
        Check(snub != GreetingRules.Kind.Snub, "...nor does someone who can't stand you snub you");
        rec.State.Affinity = 80;
        rec.State.Favours.Add(new Favour { Dish = "Spicy Ramen", Price = 40, How = "delivery", Status = "open" });
        var offer = FoodOffer.For(rec, snap, "Spicy Ramen", 30, 20, 70, 0);
        Check(offer.Paid && offer.Price == 40 && !offer.Note.Contains("discount"), "...no friend's discount");
        Check(ChatterPlan.JoinInChance(true, 80, true) == ChatterPlan.JoinInChance(true, 0, true), "...liking you doesn't make them join in more");
        int pOff = Patience.Budget(rec, snap, "", 1.0);
        Features.Relationships = true;
        int pOn = Patience.Budget(rec, snap, "", 1.0);
        Features.Relationships = false;
        Check(pOn > pOff, "...nor gives them more patience for you");
        var sheet = InfoSheet.Build(rec, null, "Ada", "Woman", "");
        Check(!sheet.Sections.Any(s => s.items.Any(i => i.Contains("relationship bar"))), "...and the info panel doesn't show the bar's standing");
        Check(rec.State.Affinity == 80, "...the standing itself is kept for when relationships are back");
        Features.Relationships = true;
        Check(FoodOffer.For(rec, snap, "Spicy Ramen", 30, 20, 70, 0).Note.Contains("discount"), "relationships back on: the discount is back");
    }
}
