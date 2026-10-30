using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NivalisAIChat.Npc;

/// <summary>
/// The AI reports what an NPC "just told" the player, and sometimes lists things from their profile they never said
/// (asked "tell me about yourself", it adds their age, gender and faith). Only facts the spoken words actually carry
/// are kept: an age has to be in the line, anything else has to share real words with it.
/// </summary>
public static class ToldCheck
{
    private static readonly Regex Word = new(@"[\p{L}\p{N}']+", RegexOptions.Compiled);
    private static readonly Regex Pronoun = new(@"(?i)\b(?:he|him|his|she|her|hers|they|them|theirs|it|its|xe|xem|ze|zir|any)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NotSaid = new(
        @"(?i)\b(?:not\s+(?:discussed|said|mentioned|stated|shared|specified|given|told)|didn'?t\s+(?:say|mention|share|specify)|unstated|unspecified|no\s+details?)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Decade = new(@"(?i)\b(?:teens|twenties|thirties|forties|fifties|sixties|seventies|eighties|nineties)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NumberWords = new(
        @"(?i)\b(?:\d{1,3}|(?:twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety)(?:[-\s](?:one|two|three|four|five|six|seven|eight|nine))?|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "again", "also", "being", "because", "before", "could", "does", "doesn't", "dont", "don't", "even", "from",
        "have", "here", "into", "just", "kind", "like", "maybe", "mentioned", "more", "most", "much", "only", "over", "really", "said",
        "says", "should", "since", "some", "something", "someone", "sort", "still", "than", "that", "their", "them", "then", "there",
        "these", "they", "thing", "things", "this", "those", "told", "very", "were", "what", "when", "where", "which", "while", "will",
        "with", "would", "your", "yours", "herself", "himself", "themselves", "myself", "person", "people", "stated", "says", "tells",
        "they're", "she's", "he's", "i'm", "it's", "that's", "self", "named",
        // short everyday words that would match almost anything
        "the", "and", "for", "you", "are", "was", "his", "her", "its", "our", "not", "but", "has", "had", "who", "why", "how", "yes",
        "can", "did", "get", "got", "too", "all", "any", "one", "out", "own", "she", "him", "now", "off", "say", "see", "way", "lot",
        "bit", "yet", "ago", "per", "via", "etc", "i've", "i'd", "let", "may", "new", "old", "big", "few", "day", "man", "guy", "use",
        "years", "year", "aged",
    };

    /// <summary>Whether <paramref name="spoken"/> (their own words) actually carries the fact <paramref name="said"/>.</summary>
    public static bool Supported(string topic, string said, string spoken)
    {
        if (string.IsNullOrWhiteSpace(said) || string.IsNullOrWhiteSpace(spoken)) return false;
        if (NotSaid.IsMatch(said)) return false;
        if (topic == "age") return AgeSupported(said, spoken);
        if (topic == "pronouns")
        {
            // pronouns are exactly the short words ignored elsewhere: compare them directly
            var named = Pronoun.Matches(said).Select(m => m.Value.ToLowerInvariant()).ToHashSet();
            var heard = Pronoun.Matches(spoken).Select(m => m.Value.ToLowerInvariant()).ToHashSet();
            return named.Count > 0 && named.Overlaps(heard);
        }
        return Shares(said, spoken, 3);
    }

    /// <summary>A fact about the player that their own words back up (things they said, not things the AI imagined).</summary>
    public static bool AboutPlayerSupported(string fact, IEnumerable<string> playerLines)
    {
        string all = string.Join(" ", playerLines);
        return all.Trim().Length > 0 && Shares(fact, all, 3);
    }

    private static bool AgeSupported(string said, string spoken)
    {
        var decades = Decade.Matches(said).Select(m => m.Value.ToLowerInvariant()).ToList();
        if (decades.Count > 0) return decades.Any(d => spoken.Contains(d, StringComparison.OrdinalIgnoreCase));
        var claimed = Numbers(said);
        if (claimed.Count == 0) return false;
        var spokenNumbers = Numbers(spoken);
        return claimed.Any(spokenNumbers.Contains);
    }

    private static HashSet<int> Numbers(string text)
    {
        var set = new HashSet<int>();
        foreach (Match m in NumberWords.Matches(text))
            if (CanonAge.Number(m.Value) is { } n) set.Add(n);
        return set;
    }

    /// <summary>
    /// The claim's meaningful words turn up in the text. One real word in common is enough ("deckhand", "brother");
    /// if only short words or numbers match ("bar", "2"), the claim has to be little more than those ("cop", "25"),
    /// so "works at a bar" doesn't match "a bar of chocolate" and "has 2 kids" doesn't match "give me 2 minutes".
    /// </summary>
    private static bool Shares(string claim, string text, int minLength)
    {
        var have = Words(text, 2).Select(Stem).ToHashSet();
        var mine = Words(claim, minLength).Select(Stem).Distinct().ToList();
        var shared = mine.Where(have.Contains).ToList();
        if (shared.Count == 0) return false;
        if (shared.Any(w => w.Length >= 4 && !w.All(char.IsDigit))) return true;
        return shared.Count >= Math.Min(2, mine.Count);
    }

    private static IEnumerable<string> Words(string text, int minLength) =>
        Word.Matches(text.ToLowerInvariant()).Select(m => m.Value.Trim('\''))
            .Select(w => w.EndsWith("'s") ? w[..^2] : w) // "dog's" is "dog"
            .Where(w => (w.Length >= minLength || (w.Length > 0 && w.All(char.IsDigit))) && !Stop.Contains(w));

    /// <summary>Crude stem so "studying" meets "study" and "brothers" meets "brother".</summary>
    private static string Stem(string w) => w.Length > 5 ? w[..5] : w.TrimEnd('s');
}
