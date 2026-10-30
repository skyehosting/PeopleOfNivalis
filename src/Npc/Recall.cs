using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NivalisAIChat.Npc;

/// <summary>
/// Finds moments from older conversations (lines the model can't currently see) that matter right now:
/// "the first thing you said", "last time", "on day 2", or simply lines sharing words with the question.
/// Everything stays on disk; this decides what comes back to mind.
/// </summary>
public static class Recall
{
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "you", "your", "you're", "youre", "that", "this", "with", "what", "was", "were", "are", "for", "have", "has", "had",
        "not", "but", "just", "about", "they", "them", "their", "there", "then", "than", "from", "into", "out", "who", "how", "why",
        "when", "where", "which", "will", "would", "could", "should", "can", "can't", "cant", "don't", "dont", "did", "does", "doing",
        "i'm", "im", "i've", "ive", "it's", "its", "me", "my", "mine", "our", "ours", "her", "his", "him", "she", "he", "we", "us",
        "yeah", "yes", "no", "nah", "okay", "ok", "oh", "uh", "um", "like", "really", "very", "too", "also", "some", "any", "all",
        "get", "got", "say", "said", "tell", "told", "know", "think", "remember", "ever", "thing", "things", "time", "one",
    };

    private static readonly Regex Word = new(@"[\p{L}\p{N}']{3,}", RegexOptions.Compiled);

    private static string Stem(string w)
    {
        w = w.Trim('\'').ToLowerInvariant();
        if (w.Length > 5 && w.EndsWith("ing")) return w[..^3];
        if (w.Length > 4 && w.EndsWith("ed")) return w[..^2];
        if (w.Length > 3 && w.EndsWith("s") && !w.EndsWith("ss")) return w[..^1];
        return w;
    }

    public static HashSet<string> Tokens(string text) =>
        Word.Matches(text ?? "").Select(m => m.Value).Where(w => !Stop.Contains(w)).Select(Stem).Where(w => w.Length >= 3).ToHashSet();

    /// <summary>Words that reach back: memory itself, time gone by, and what was said or done between you.</summary>
    private static readonly Regex PastWords = new(
        @"(?ix)\b(?: remember\w* | recall\w* | forg[eo]t\w* | remind\w* | memor(?:y|ies) | recollect\w*
            | last\s+(?:time|week|night|month|year|summer|winter|spring|autumn|fall) | the\s+other\s+day | yesterday | earlier
            | before | back\s+(?:then|when|in) | ago | used\s+to | that\s+time | first\s+time | again | lately | recently | still
            | you\s+(?:said|told|mentioned|promised|asked|were\s+saying|talked\s+about) | you['’]?ve\s+(?:told|said|mentioned)
            | we\s+(?:talked|met|spoke|did|went|had) | when\s+we | have\s+you\s+ever | did\s+you | are\s+you\s+still
            | how['’]s | how\s+is | how\s+did | what\s+happened )\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NameWord = new(@"[\p{L}\p{N}]+(?:['’][\p{L}]+)?", RegexOptions.Compiled);

    /// <summary>
    /// Whether what the player just said reaches into the past, so the person's memories are searched by meaning
    /// (MemorySearch = Voyage; the author's Q3: only then, or when they reach for a memory themselves, "recall"). Memory
    /// words, time gone by, what was said or done between you, and anyone they know by name: someone they've talked or
    /// been on the phone with, or someone from their own life ("How's Mia?"). Plain past tense alone ("I went to the
    /// store") doesn't count. In another language only the names count; the person's own "recall" covers the rest.
    /// </summary>
    public static bool AboutThePast(string text, NpcRecord? rec = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (PastWords.IsMatch(text)) return true;
        if (rec == null) return false;
        var words = NameWord.Matches(text).Select(m => Possessive(m.Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return KnownNames(rec).Any(words.Contains);
    }

    /// <summary>"Mia's" → "Mia".</summary>
    private static string Possessive(string word) =>
        word.EndsWith("'s", StringComparison.OrdinalIgnoreCase) || word.EndsWith("’s", StringComparison.OrdinalIgnoreCase) ? word[..^2] : word;

    /// <summary>The first names of people this person knows: from their talks and calls, and their own people.</summary>
    private static IEnumerable<string> KnownNames(NpcRecord rec)
    {
        string own = (rec.Persona.Name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var talked = (rec.Talks ?? new List<Talk>()).Where(t => t.Kind is not (Talk.Overheard or Talk.Service)).Select(t => t.With ?? ""); // overheard, an order taken: not people they know
        var people = (rec.Persona.People ?? new List<string>()).Select(p => p.Split(new[] { " — ", " - ", " – ", "—" }, StringSplitOptions.None)[0]);
        foreach (var full in talked.Concat(people))
        {
            string first = full.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (first.Length >= 3 && !first.Equals(own, StringComparison.OrdinalIgnoreCase) && char.IsLetter(first[0])) yield return first;
        }
    }

    /// <summary>Spoken lines (no scene notes) that aren't in the live context window.</summary>
    private static List<(int index, ChatLine line)> Unseen(NpcRecord rec, int liveCount)
    {
        var live = new HashSet<ChatLine>(rec.History.Where(l => !l.Folded).TakeLast(Math.Max(0, liveCount)));
        return rec.History.Select((l, i) => (i, l)).Where(p => p.l.Role != "scene" && !live.Contains(p.l)).ToList();
    }

    /// <summary>Meetings as index ranges, split on the scene line that opens each one.</summary>
    private static List<List<int>> Meetings(NpcRecord rec)
    {
        var meetings = new List<List<int>>();
        List<int>? cur = null;
        for (int i = 0; i < rec.History.Count; i++)
        {
            var l = rec.History[i];
            if (l.Role == "scene" && l.Text.StartsWith("[Scene:") && (l.Text.Contains("walks up to you") || cur == null))
            {
                cur = new List<int>();
                meetings.Add(cur);
                continue;
            }
            if (l.Role == "scene") continue;
            if (cur == null) { cur = new List<int>(); meetings.Add(cur); }
            cur.Add(i);
        }
        return meetings.Where(m => m.Count > 0).ToList();
    }

    private static readonly Regex DayRef = new(@"\bday\s*(\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static List<ChatLine> Search(NpcRecord rec, string query, int liveCount, int max = 8)
    {
        var unseen = Unseen(rec, liveCount);
        if (unseen.Count == 0 || string.IsNullOrWhiteSpace(query)) return new();
        var unseenIdx = unseen.Select(u => u.index).ToHashSet();
        var picked = new SortedSet<int>();
        string q = query.ToLowerInvariant();

        // "first thing / first time / when we met"
        if (Regex.IsMatch(q, @"\b(first|earliest|when we (first )?met|very beginning|how we met)\b"))
            foreach (var i in unseen.Select(u => u.index).Take(4)) picked.Add(i);

        // "last time / yesterday / before"
        if (Regex.IsMatch(q, @"\b(last time|yesterday|other day|before|previous|last we)\b"))
        {
            var meetings = Meetings(rec);
            var past = meetings.Where(m => m.Any(unseenIdx.Contains)).ToList();
            if (past.Count > 0) foreach (var i in past[^1].Where(unseenIdx.Contains).TakeLast(6)) picked.Add(i);
        }

        // "on day 2"
        foreach (Match m in DayRef.Matches(q))
            if (int.TryParse(m.Groups[1].Value, out int day))
                foreach (var u in unseen.Where(u => u.line.GameDay == day).Take(6)) picked.Add(u.index);

        // words in common, weighted so rare words count for more
        var qTok = Tokens(query);
        if (qTok.Count > 0)
        {
            var lineTok = unseen.ToDictionary(u => u.index, u => Tokens(u.line.Text));
            var df = new Dictionary<string, int>();
            foreach (var toks in lineTok.Values)
                foreach (var t in toks) df[t] = df.TryGetValue(t, out int c) ? c + 1 : 1;
            double n = lineTok.Count;
            var scored = lineTok
                .Select(kv => (kv.Key, score: kv.Value.Where(qTok.Contains).Sum(t => Math.Log(1 + n / df[t]))))
                .Where(x => x.score >= 1.2)
                .OrderByDescending(x => x.score)
                .Take(Math.Max(1, max / 2));
            foreach (var (idx, _) in scored)
            {
                picked.Add(idx);
                // keep the other half of the exchange for context
                int partner = rec.History[idx].Role == "player" ? idx + 1 : idx - 1;
                if (unseenIdx.Contains(partner)) picked.Add(partner);
            }
        }
        return picked.Take(max).Select(i => rec.History[i]).ToList();
    }

    /// <summary>
    /// As <see cref="Format(IReadOnlyList{ChatLine}, string)"/>, with <paramref name="fades"/>: each line as clearly as its
    /// age in game days allows (Memories.Clarity), the forgotten ones left out, and how people talk about half-memories.
    /// </summary>
    public static string Format(IReadOnlyList<ChatLine> lines, string heading, int today, bool fades, Random rng)
    {
        if (!fades) return Format(lines, heading);
        var entries = lines.Select(l => new MemoryEntry
        {
            Day = l.GameDay, Clock = l.Clock, Kind = "you",
            Text = $"{(l.Role == "player" ? "they said" : "you said")}: \"{l.Text}\"",
        });
        return Memories.Format(entries, today, true, rng, heading);
    }

    public static string Format(IReadOnlyList<ChatLine> lines, string heading)
    {
        if (lines.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine(heading);
        foreach (var l in lines)
            sb.AppendLine($"- Day {l.GameDay}{(l.Clock.Length > 0 ? ", " + l.Clock : "")} — {(l.Role == "player" ? "they said" : "you said")}: \"{l.Text}\"");
        return sb.ToString();
    }
}
