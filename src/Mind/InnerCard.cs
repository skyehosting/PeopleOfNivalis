using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisAIChat.Mind;

/// <summary>What goes on the card, as the code found it (each already in the person's own terms).</summary>
public sealed class InnerInputs
{
    /// <summary>What happened with a plan you made (stood up, met, didn't make it), newest first: said before anything else.</summary>
    public List<string> Plans = new();
    public Thought? Thought;
    /// <summary>Their body ("you're hungry"), or "".</summary>
    public string Body = "";
    /// <summary>Their taste (Npc.Tastes: a favourite, and what they're sick of this week), or "".</summary>
    public string Food = "";
    /// <summary>Their love life now, from the couples book ("You're with Teo, since day 12"), or "".</summary>
    public string Together = "";
    /// <summary>People they know standing nearby, with how they feel about them ("Rina (a good friend)").</summary>
    public List<string> PeopleHere = new();
    /// <summary>What they saw or heard (their own beliefs, as they'd put it), strongest first.</summary>
    public List<string> Seen = new();
    /// <summary>What they know but keep to themselves unless pushed.</summary>
    public List<string> Keep = new();
}

/// <summary>
/// The inner-life round, step 2 (the second research doc's "inner state card", merged with the first doc's social
/// briefing): a few short lines in a chat's prompt, only what isn't said anywhere else, capped (6 lines, about 150 tokens),
/// stated as facts about them, never as "don't mention" (the pink-elephant effect). Their mood, memories and plans have
/// their own sections already. No Unity, tested.
/// </summary>
public static class InnerCard
{
    public const int MaxLines = 6, MaxLine = 180, MaxChars = 780;
    public const string Heading = "[INSIDE YOU RIGHT NOW] (yours alone: it colours how you are; bring something up only when it fits)";

    public static List<string> Lines(InnerInputs i)
    {
        var lines = new List<string>();
        foreach (var p in i.Plans.Take(2)) lines.Add(p);
        if (i.Thought is { Text.Length: > 0 } t) lines.Add($"On your mind: {t.Text.TrimEnd('.')}.");
        if (i.Body.Length > 0) lines.Add(i.Body);
        if (i.Food.Length > 0) lines.Add(i.Food);
        if (i.Together.Length > 0) lines.Add(i.Together);
        if (i.PeopleHere.Count > 0) lines.Add("People you know nearby: " + string.Join(", ", i.PeopleHere.Take(4)) + ".");
        foreach (var s in i.Seen.Take(2)) lines.Add(Cap(s.TrimEnd('.')) + ".");
        foreach (var k in i.Keep.Take(1)) lines.Add("You know, and keep it to yourself unless someone you trust pushes: " + k.TrimEnd('.') + ".");
        var kept = new List<string>();
        int chars = 0;
        foreach (var l in lines.Select(Clean).Where(l => l.Length > 0))
        {
            string line = l.Length <= MaxLine ? l : l[..(MaxLine - 1)].TrimEnd() + "…";
            if (kept.Count >= MaxLines || chars + line.Length > MaxChars) break;
            kept.Add(line);
            chars += line.Length;
        }
        return kept;
    }

    /// <summary>One line, nothing in it that could read as a prompt section of its own.</summary>
    private static string Clean(string s) =>
        new string((s ?? "").Where(c => c is not ('[' or ']' or '{' or '}' or '<' or '>' or '\n' or '\r')).ToArray()).Trim();

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
