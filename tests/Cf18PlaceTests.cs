using System;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Tests;

/// <summary>CF-18: they know where they are (moved by another mod, or walked off together), and it's in the history.</summary>
internal static partial class Program
{
    private static void Cf18PlaceTests()
    {
        Console.WriteLine("-- CF-18: where they are, and the place in the history");
        var at = new GameSnapshot { Location = "Lowtown", NpcPlace = "Kiko's Noodle Bar", GameDay = 3, Hour = 20, Minute = 10 };
        var docks = new GameSnapshot { Location = "Docks", NpcPlace = "", GameDay = 3, Hour = 20, Minute = 40 };
        Check(Prompts.PlaceWords(at) == "Kiko's Noodle Bar, Lowtown" && Prompts.PlaceWords(docks) == "Docks" && Prompts.PlaceWords(new GameSnapshot()) == "",
              "where they are: the venue and its district, or the district alone");
        string? line = Prompts.PlaceChangeLine(at, docks);
        Check(line != null && line.Contains("You're somewhere else now: Docks") && line.Contains("you were at Kiko's Noodle Bar, Lowtown") && line.StartsWith("[Scene: Day 3, 20:40."),
              "moved since the last turn: a scene line says where they are now, and where they were");
        Check(Prompts.PlaceChangeLine(at, at) == null && Prompts.PlaceChangeLine(new GameSnapshot(), docks) == null, "same place, or unknown: nothing");
        var rec = new NpcRecord { Persona = new Persona { Name = "Mae" } };
        string sit = Prompts.SituationBlock(rec, at);
        Check(sit.Contains("You're in Lowtown.") && sit.Contains("Right now you're at Kiko's Noodle Bar."), "every turn: the venue their body is at now");
        Check(!Prompts.SituationBlock(rec, docks).Contains("Right now you're at"), "...only when they're at one");
    }
}
