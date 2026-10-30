using System;
using System.Linq;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Tests;

/// <summary>CF-16 (a player on 0.6.0: "their bio is absolutely trounced with the same idea over and over").</summary>
internal static partial class Program
{
    private static void Cf16Tests()
    {
        Console.WriteLine("-- CF-16: one idea, said again in other words, is kept once");
        var rec = new NpcRecord { Persona = new Persona { Name = "Mira Sato" } };
        // How a model reports it, a reply at a time: the same worry, reworded.
        NpcBrain.ApplyTold(rec, new[] { ("fears", "worried about making rent this month") }, 3, afterMeeting: false);
        NpcBrain.ApplyTold(rec, new[] { ("fears", "worried she can't make rent this month") }, 3, afterMeeting: false);
        NpcBrain.ApplyTold(rec, new[] { ("fears", "Worried about making the rent this month.") }, 3, afterMeeting: false);
        NpcBrain.ApplyTold(rec, new[] { ("background", "grew up on the docks") }, 3, afterMeeting: false);
        NpcBrain.ApplyTold(rec, new[] { ("background", "she grew up down on the docks") }, 4, afterMeeting: false);
        NpcBrain.ApplyTold(rec, new[] { ("fears", "scared of deep water") }, 4, afterMeeting: false);
        int fears = rec.State.Told.Count(t => t.Topic == "fears"), story = rec.State.Told.Count(t => t.Topic == "background");
        Check(fears == 2 && story == 1, $"the panel keeps each idea once (fears {fears} of 2, story {story} of 1)");
        Check(rec.Persona.EstablishedFacts.Count == 3, $"...and so do the things they've said about themselves (the prompt): {rec.Persona.EstablishedFacts.Count} of 3");
        Check(rec.State.Told.Last(t => t.Topic == "fears" && t.Said.Contains("rent")).GameDay == 3, "the one kept is the first time it was said");

        // A record that's already full of repeats (0.6.0) is tidied up.
        var old = new NpcRecord { Persona = new Persona { Name = "Bo" } };
        foreach (var s in new[] { "loves old jazz records", "Loves old jazz records.", "really loves old jazz records", "into old jazz records", "likes fishing" })
            old.State.Told.Add(new ToldItem { Topic = "likes", Said = s, GameDay = 1 });
        foreach (var s in new[] { "likes: loves old jazz records", "likes: really loves old jazz records", "likes: likes fishing" })
            old.Persona.EstablishedFacts.Add(s);
        int removed = ToldTidy.Tidy(old);
        Check(old.State.Told.Count(t => t.Topic == "likes") == 2 && old.Persona.EstablishedFacts.Count == 2 && removed == 4,
            $"a record from before keeps each idea once ({old.State.Told.Count} told, {old.Persona.EstablishedFacts.Count} facts, {removed} repeats gone)");
        Check(!ToldTidy.SameIdea("worried about rent", "worried about her brother") && !ToldTidy.SameIdea("has a cat", "has a dog")
              && ToldTidy.SameIdea("works nights at the docks", "works the night shift at the docks"), "different ideas stay apart; the same one is seen as the same");
    }
}
