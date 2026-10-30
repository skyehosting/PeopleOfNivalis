using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisAIChat.Npc;

/// <summary>
/// CF-16 (a player on 0.6.0: "their bio is absolutely trounced with the same idea over and over"): what someone told you
/// about themselves is kept once per idea, not once per wording. The reply reports every fact in each line, so a worry they
/// mention five times came back five ways ("worried about making rent", "can't make rent this month"...), each its own row
/// on the info panel and its own "things you've said about yourself" line in their prompt (which made them say it more).
/// <see cref="SameIdea"/> compares the words that carry the idea (no small words, rough stems, a "not" on one side keeps
/// them apart); <see cref="Tidy"/> collapses a record that already has repeats. No Unity, tested.
/// </summary>
public static class ToldTidy
{
    private static readonly HashSet<string> Small = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "to", "of", "at", "on", "in", "into", "for", "with", "by", "from", "about", "and", "or", "but", "so", "as",
        "she", "he", "they", "her", "his", "their", "them", "him", "i", "my", "me", "mine", "it", "its", "it's", "is", "are", "was", "were",
        "be", "been", "being", "has", "have", "had", "having", "this", "that", "these", "those", "there", "here", "really", "very", "just",
        "down", "up", "also", "still", "always", "some", "any", "much", "lot", "lots", "kind", "sort", "thing", "things", "one", "out",
        "says", "said", "say", "told", "mentions", "mentioned", "who", "which", "what", "when", "where", "like", "likes", "liked", "loves",
        "love", "loved", "enjoys", "enjoy", "into", "big", "quite", "pretty", "s", "d", "ll", "re", "ve", "m",
    };

    private static readonly HashSet<string> Negations = new(StringComparer.Ordinal) { "not", "never", "no", "doesnt", "dont", "isnt", "wasnt", "hates", "hate", "hated", "nobody", "nothing" };

    /// <summary>The words that carry what a told item is about, roughly stemmed ("working" and "works" are one).</summary>
    public static HashSet<string> Words(string? text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in (text ?? "").ToLowerInvariant().Replace("'", "").Replace("’", "")
                     .Split(new[] { ' ', ',', '.', ';', ':', '!', '?', '-', '(', ')', '/', '"' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Small.Contains(raw)) continue;
            set.Add(Stem(raw));
        }
        return set;
    }

    private static string Stem(string w)
    {
        foreach (var end in new[] { "ing", "ed", "es", "s", "e", "y" })
            if (w.Length - end.Length >= 3 && w.EndsWith(end, StringComparison.Ordinal)) { w = w[..^end.Length]; break; }
        if (w.EndsWith("i", StringComparison.Ordinal) && w.Length >= 4) w = w[..^1]; // worried/worry -> worr
        return w;
    }

    /// <summary>
    /// Two told items say the same thing: most of the words that carry them are shared (at least 60% of the shorter, and at
    /// least two when both have two), and neither says "not" where the other doesn't. Containing one another counts too.
    /// </summary>
    public static bool SameIdea(string? a, string? b)
    {
        string x = (a ?? "").Trim(), y = (b ?? "").Trim();
        if (x.Length == 0 || y.Length == 0) return false;
        if (string.Equals(x.TrimEnd('.'), y.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)) return true;
        var wa = Words(x);
        var wb = Words(y);
        if (wa.Count == 0 || wb.Count == 0) return false;
        bool na = wa.Any(Negations.Contains) || x.Contains("n't", StringComparison.OrdinalIgnoreCase) && !x.Contains("can't", StringComparison.OrdinalIgnoreCase);
        bool nb = wb.Any(Negations.Contains) || y.Contains("n't", StringComparison.OrdinalIgnoreCase) && !y.Contains("can't", StringComparison.OrdinalIgnoreCase);
        if (na != nb) return false;
        int both = wa.Count(wb.Contains);
        int small = Math.Min(wa.Count, wb.Count);
        return both >= Math.Min(2, small) && both >= 0.6 * small;
    }

    /// <summary>
    /// A record's told items and established facts with each idea once (the first time it was said is kept): for the topics
    /// that build up; the ones where only the latest counts are left as they are. Returns how many repeats went.
    /// </summary>
    public static int Tidy(NpcRecord rec)
    {
        int removed = 0;
        var told = rec.State.Told ?? new List<ToldItem>();
        var keep = new List<ToldItem>();
        foreach (var t in told)
        {
            bool repeat = !ToldTopics.Single.Contains(t.Topic) && keep.Any(k => k.Topic == t.Topic && SameIdea(k.Said, t.Said));
            if (repeat) removed++;
            else keep.Add(t);
        }
        rec.State.Told = keep;
        var facts = rec.Persona.EstablishedFacts ?? new List<string>();
        var kept = new List<string>();
        for (int i = 0; i < facts.Count; i++)
        {
            var (topic, said) = Split(facts[i]);
            // A topic where only the latest answer counts (age, job...): only its latest fact stays.
            bool stale = ToldTopics.Single.Contains(topic) && facts.Skip(i + 1).Any(f => Split(f).topic == topic);
            bool repeat = stale || kept.Any(k => { var (kt, ks) = Split(k); return kt == topic && SameIdea(ks, said); });
            if (repeat) removed++;
            else kept.Add(facts[i]);
        }
        rec.Persona.EstablishedFacts = kept;
        return removed;
    }

    /// <summary>
    /// CF-25 (a tester: "the profile does things like put "wants extra onions on his noodles" in interests when just chatting
    /// about ordering ... rather than thinking about what's actually significant enough to form their personality biography.
    /// Otherwise they seem to get fixated"): a remark about the moment, not about who they are: what they're ordering or eating,
    /// that the food was fine, what they came in for, how they feel right now. Never kept as a fact about them, never shown on
    /// their profile, never put back in their prompt (which is what made them keep bringing it up). Who they are stays:
    /// "loves noodles", "cooks at a noodle stall", "worried about rent".
    /// </summary>
    public static bool Passing(string? topic, string? said)
    {
        // Review CF25-1: only where a passing remark lands (the tester's were in interests and "other"); a job, a home, the
        // people in their life, their past or their plans are lasting however they're put ("takes orders at the noodle bar").
        string t = (topic ?? "").Trim().ToLowerInvariant();
        if (t is not ("other" or "likes" or "hobbies" or "dislikes" or "health")) return false;
        string s = (said ?? "").Trim();
        return s.Length > 0 && PassingWords.IsMatch(s);
    }

    private static readonly System.Text.RegularExpressions.Regex PassingWords = new(
        @"\b(ordered|ordering|(his|her|their|my|an|the) order\b|order of|having (the|some|his|her|their|my) " +
        @"|grab(bed|bing)? (a|the|some)|(was|were) (fine|okay|ok|alright|tasty|delicious|decent)|(is|tastes|looks) (fine|okay|ok|alright|tasty)" +
        @"|(only|just) (trying|wanted|came|looking|after|getting|having|grabbing|asked)|trying to (get|grab|order)" +
        @"|wants? (extra|another|a bit more|some more)|extra \w+ on|asked for (the|a|some|extra|more)|came in for" +
        @"|(kept|keeps) (him|her|them|me) fed|(is|was|feels?|felt) (hungry|starving|thirsty)|(is|was) (eating|sipping|drinking|waiting for)" +
        @"|right now|at the moment|tonight|this (morning|afternoon|evening)\b)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>CF-25: the "other" section and its prompt lines keep only their newest few (it gathered every stray remark).</summary>
    public const int OtherKept = 6;

    /// <summary>CF-25: their established facts ("topic: said") without remarks about the moment, and only the newest few "other" ones.</summary>
    public static List<string> Lasting(IEnumerable<string> facts)
    {
        var kept = (facts ?? Enumerable.Empty<string>()).Where(f => { var (t, s) = Split(f); return !Passing(t, s); }).ToList();
        var others = kept.Where(f => Split(f).topic == "other").ToList();
        if (others.Count <= OtherKept) return kept;
        var dropped = new HashSet<string>(others.Take(others.Count - OtherKept), StringComparer.Ordinal); // the oldest ones
        return kept.Where(f => !dropped.Contains(f)).ToList();
    }

    /// <summary>"fears: worried about rent" as (fears, worried about rent); a fact with no topic is ("", the fact).</summary>
    public static (string topic, string said) Split(string fact)
    {
        int c = (fact ?? "").IndexOf(':');
        return c > 0 && c < 20 ? (fact![..c].Trim().ToLowerInvariant(), fact[(c + 1)..].Trim()) : ("", (fact ?? "").Trim());
    }
}
