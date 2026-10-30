using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NivalisAIChat.Game;

namespace NivalisAIChat.Npc;

/// <summary>
/// FB-33 (the author: "anyone that mutters should take their context/what they know, and from it form either a short few
/// word question or a small very small statement to themselves"): what a mutter is about is picked here, from their own life,
/// so it's concrete and different each time; the AI only puts it into a few words. Also when CORA may be said (FB-02).
/// </summary>
public static class MutterSeeds
{
    /// <summary>One thing from their life to mutter about: <see cref="Key"/> tells seeds apart (for not repeating one).</summary>
    public sealed record Seed(string Key, string Text);

    /// <summary>How many of their last seeds aren't picked again.</summary>
    public const int Remember = 3;

    /// <summary>Everything they could mutter about, each with its weight (people in their life and plans weigh most).</summary>
    public static List<(Seed seed, int weight)> Candidates(NpcRecord rec, GameSnapshot snap)
    {
        var list = new List<(Seed, int)>();
        var p = rec.Persona;
        void Add(string key, string text, int weight)
        {
            text = (text ?? "").Trim().TrimEnd('.');
            if (text.Length == 0) return;
            if (text.Length > 160) text = text[..160].TrimEnd() + "...";
            list.Add((new Seed(key, text), weight));
        }
        foreach (var entry in p.People ?? new List<string>())
        {
            var parts = entry.Split(new[] { " — ", " - ", " – ", "—" }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();
            if (parts.Length == 0 || parts[0].Length == 0) continue;
            string about = parts.Length > 1 ? $" ({string.Join(", ", parts.Skip(1))})" : "";
            Add("person:" + parts[0], $"someone in their life: {parts[0]}{about}", 3);
        }
        Add("goal", p.ShortTermGoal.Length > 0 ? "something they want to get done soon: " + p.ShortTermGoal : "", 2);
        Add("worry", p.CurrentWorry.Length > 0 ? "what's been worrying them: " + p.CurrentWorry : "", 2);
        string job = p.Occupation.Length > 0 ? p.Occupation + (p.Workplace.Length > 0 ? " at " + p.Workplace : "") : "";
        Add("job", job.Length > 0 ? "their job: " + job : "", 2);
        if (snap.NpcActivity.Length > 0) Add("now", "what they're doing right now: " + snap.NpcActivity, 2);
        foreach (var t in (rec.Talks ?? new List<Talk>()).Where(t => t.Kind is Talk.InPerson or Talk.Phone && t.Summary.Length > 0).TakeLast(2))
            Add("talk:" + t.GameDay + t.Clock + t.With, $"a recent {(t.Kind == Talk.Phone ? "call" : "talk")} with {t.With}: {t.Summary}", 2);
        foreach (var x in (p.Likes ?? new List<string>()).Take(4)) Add("like:" + x, "something they like: " + x, 1);
        foreach (var x in (p.Hobbies ?? new List<string>()).Take(3)) Add("hobby:" + x, "a hobby of theirs: " + x, 1);
        foreach (var x in (p.Habits ?? new List<string>()).Take(3)) Add("habit:" + x, "a habit of theirs: " + x, 1);
        foreach (var x in (p.Fears ?? new List<string>()).Take(2)) Add("fear:" + x, "something they're afraid of: " + x, 1);
        // The inner-life round: their taste (a favourite they're craving, or a dish they're sick of this week).
        if (Features.InnerLife) Add("food", Tastes.Mutter(rec, snap.GameDay), 2);
        return list;
    }

    /// <summary>
    /// One seed, by weight, leaving out the ones in <paramref name="recent"/> (their last few); when all are recent, any.
    /// Null when there's nothing at all to go on.
    /// </summary>
    public static Seed? Pick(NpcRecord rec, GameSnapshot snap, Random rng, IReadOnlyCollection<string>? recent = null)
    {
        var all = Candidates(rec, snap);
        if (all.Count == 0) return null;
        var fresh = recent == null ? all : all.Where(c => !recent.Contains(c.seed.Key)).ToList();
        if (fresh.Count == 0) fresh = all;
        int total = fresh.Sum(c => c.weight), roll = rng.Next(total);
        foreach (var (seed, weight) in fresh)
        {
            roll -= weight;
            if (roll < 0) return seed;
        }
        return fresh[^1].seed;
    }

    /// <summary>Someone who'd say CORA at all: a believer (not lapsed, not someone who thinks it's a psy-op).</summary>
    public static bool Believer(Persona p)
    {
        string f = (p.Faith ?? "").ToLowerInvariant();
        return f.Contains("cora") && !f.Contains("psy-op") && !f.Contains("gave it up") && !f.Contains("lapsed") && !f.Contains("rarely");
    }

    /// <summary>The odds a believer may say CORA in a mutter (1 in this many): now and then, not every time (FB-02).</summary>
    public const int CoraOdds = 8;

    private static readonly Regex LeadingCora = new(
        @"^\s*(?:[Oo]h,?\s+|[Tt]hank\s+|[Bb]y\s+)?CORA(?:['’]s\s+\w+)?\s*(?:\.\.\.|[,.!?…—–-])+\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant); // case-sensitive: a person called Cora stays (code review 13)

    /// <summary>
    /// A line that opens with CORA as an exclamation ("CORA, it's back already...", "CORA... not again") without it, the next
    /// word capitalised; "" when nothing's left. Lines that use it elsewhere are left as they are.
    /// </summary>
    public static string TrimLeadingCora(string line)
    {
        line ??= "";
        var m = LeadingCora.Match(line);
        if (!m.Success) return line;
        string rest = line[m.Length..].Trim();
        if (rest.Length == 0) return "";
        return char.ToUpperInvariant(rest[0]) + rest[1..];
    }
}
