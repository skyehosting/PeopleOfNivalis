using System;
using System.Collections.Generic;

namespace NivalisAIChat.Npc;

/// <summary>
/// The player's population settings: age range and how often people are trans, non-binary or not straight.
/// Defaults follow recent US survey data (Gallup 2024/2025, Pew 2022); younger people are likelier to identify
/// as trans, non-binary or queer, scaled so the overall rates match the settings.
/// </summary>
public sealed class PopulationSettings
{
    public int MinAge = 18;
    public int MaxAge = 60;
    /// <summary>Percent of people who are trans (trans women + trans men). Gallup 2024: 1.3%.</summary>
    public double TransPercent = 1.3;
    /// <summary>Percent who are non-binary, genderfluid or agender. Pew 2022: ~1%.</summary>
    public double NonBinaryPercent = 1.0;
    /// <summary>Percent whose orientation isn't straight (gay, lesbian, bi, pan, ace...). Gallup: ~8%.</summary>
    public double QueerPercent = 8.0;
    public bool AgeShapesIdentity = true;

    /// <summary>The adult ages people are created with: never below 18, whatever MinAge says.</summary>
    public (int min, int max) AgeRange()
    {
        int lo = Math.Max(Population.AdultAge, MinAge), hi = Math.Max(Population.AdultAge, MaxAge);
        if (hi < lo) (lo, hi) = (hi, lo);
        return (lo, Math.Min(hi, 110));
    }

    /// <summary>The full range MinAge..MaxAge, which may start below 18 (those people are never chatted with).</summary>
    public (int min, int max) FullRange()
    {
        int lo = Math.Clamp(MinAge, 0, 110), hi = Math.Clamp(MaxAge, 0, 110);
        if (hi < lo) (lo, hi) = (hi, lo);
        return (lo, hi);
    }
}

public static class Population
{
    /// <summary>Nobody under this age is ever chatted with, greeted by or given an AI profile.</summary>
    public const int AdultAge = 18;

    /// <summary>Rough age mix of a city street: (from, to, share). Under-18s only matter when MinAge is set below 18.</summary>
    private static readonly (int from, int to, double share)[] AgeBands =
        { (0, 17, 0.20), (18, 24, 0.20), (25, 34, 0.28), (35, 49, 0.27), (50, 64, 0.17), (65, 110, 0.08) };

    private static double Density(int age)
    {
        foreach (var (from, to, share) in AgeBands)
            if (age >= from && age <= to) return share / (to - from + 1);
        return 0.0005;
    }

    public static int RollAge(Random rng, PopulationSettings pop)
    {
        var (lo, hi) = pop.AgeRange();
        double total = 0;
        for (int a = lo; a <= hi; a++) total += Density(a);
        double r = rng.NextDouble() * total;
        for (int a = lo; a <= hi; a++) { r -= Density(a); if (r <= 0) return a; }
        return hi;
    }

    /// <summary>
    /// Chance a newly met person is under 18: zero unless MinAge is set below 18, then the share of MinAge..17 in
    /// the whole MinAge..MaxAge range. Those people are never chatted with.
    /// </summary>
    public static double MinorShare(PopulationSettings pop)
    {
        var (lo, hi) = pop.FullRange();
        if (lo >= AdultAge) return 0;
        double minors = 0, total = 0;
        for (int a = lo; a <= hi; a++)
        {
            total += Density(a);
            if (a < AdultAge) minors += Density(a);
        }
        return total > 0 ? minors / total : 0;
    }

    // Relative likelihood by age (survey generations mapped to ages): Gen Z ≈ 18-27, millennials ≈ 28-43.
    private static double TransWeight(int a) => a <= 27 ? 3.2 : a <= 43 ? 1.3 : a <= 59 ? 0.45 : 0.3;
    private static double NonBinaryWeight(int a) => a <= 29 ? 3.0 : a <= 44 ? 0.8 : 0.3;
    private static double QueerWeight(int a) => a <= 27 ? 2.5 : a <= 43 ? 1.3 : a <= 59 ? 0.5 : 0.22;

    /// <summary>Chance (0-1) at this age, so the population-wide rate over the age range equals the setting.</summary>
    public static double ChanceAt(int age, double percent, Func<int, double> weight, PopulationSettings pop)
    {
        double p = Math.Clamp(percent, 0, 100) / 100.0;
        if (!pop.AgeShapesIdentity) return p;
        var (lo, hi) = pop.AgeRange();
        double num = 0, den = 0;
        for (int a = lo; a <= hi; a++) { num += Density(a) * weight(a); den += Density(a); }
        double mean = den > 0 ? num / den : 1;
        return Math.Min(0.95, p * weight(age) / Math.Max(mean, 1e-6));
    }

    public sealed class Identity
    {
        public string Gender = "";      // woman, man, trans woman, trans man, non-binary, genderfluid, agender
        public string Pronouns = "";
        public string Orientation = "";
        public bool PronounsFlexible;
    }

    /// <summary>Gender identity, pronouns and orientation for someone whose body the player sees as man/woman/android.</summary>
    public static Identity RollIdentity(Random rng, string body, int age, PopulationSettings pop)
    {
        T Pick<T>(params T[] xs) => xs[rng.Next(xs.Length)];
        var id = new Identity();
        bool android = body == "android";
        bool presentsFemale = body == "woman";
        double trans = android ? 0 : ChanceAt(age, pop.TransPercent, TransWeight, pop);
        double nb = ChanceAt(age, pop.NonBinaryPercent, NonBinaryWeight, pop);
        double r = rng.NextDouble();
        if (r < trans) id.Gender = presentsFemale ? "trans woman" : "trans man";
        else if (r < trans + nb) id.Gender = Pick("non-binary", "non-binary", "non-binary", "non-binary", "genderfluid", "agender");
        else id.Gender = android ? Pick("woman", "man", "agender", "non-binary") : presentsFemale ? "woman" : "man";

        double pr = rng.NextDouble();
        id.Pronouns = id.Gender switch
        {
            "woman" or "trans woman" => pr < (id.Gender == "woman" ? 0.94 : 0.85) ? "she/her" : "she/they",
            "man" or "trans man" => pr < (id.Gender == "man" ? 0.94 : 0.85) ? "he/him" : "he/they",
            "agender" => pr < 0.6 ? "they/them" : pr < 0.85 && android ? "it/its" : "any pronouns",
            _ => pr < 0.6 ? "they/them" : pr < 0.85 ? (presentsFemale ? "she/they" : "he/they") : "any pronouns",
        };
        id.PronounsFlexible = id.Pronouns == "any pronouns" || rng.NextDouble() < 0.08; // "call me whatever"

        double q = ChanceAt(age, pop.QueerPercent, QueerWeight, pop);
        id.Orientation = rng.NextDouble() < q
            ? Pick("bisexual", "bisexual", "bisexual", "bisexual", "bisexual", "gay", "gay", "lesbian", "pansexual", "asexual", "queer")
            : "straight";
        if (id.Orientation == "lesbian" && !(id.Gender is "woman" or "trans woman")) id.Orientation = "gay";
        if (id.Orientation == "gay" && id.Gender is "woman" or "trans woman" && rng.NextDouble() < 0.7) id.Orientation = "lesbian";
        return id;
    }

    /// <summary>What a passer-by sees, until the person shares more.</summary>
    public static string PerceivedLabel(string body) => body switch
    {
        "woman" => "Woman",
        "man" => "Man",
        "android" => "Android",
        _ => "",
    };

    /// <summary>The label shown once they've told you who they are.</summary>
    public static string SharedLabel(string gender, string body) => gender switch
    {
        "trans woman" => "Trans Woman",
        "trans man" => "Trans Man",
        "non-binary" => "Non-binary",
        "genderfluid" => "Genderfluid",
        "agender" => "Agender",
        "woman" => body == "android" ? "Android" : "Woman",
        "man" => body == "android" ? "Android" : "Man",
        _ => PerceivedLabel(body),
    };

    public static bool IsTransOrNonBinary(string gender) =>
        gender is "trans woman" or "trans man" or "non-binary" or "genderfluid" or "agender";

    public static bool IsNonBinary(string gender) => gender is "non-binary" or "genderfluid" or "agender";
}
