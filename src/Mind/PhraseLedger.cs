using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NivalisAIChat.Mind;

/// <summary>
/// The inner-life round, step 3 (the research's phrase ledger, done in code rather than as a "don't say" list, which on
/// small models backfires): each person's last openers and lines (MindBook keeps them, per save). A reply that opens with
/// the same filler they used in two of their last five has the filler trimmed; of several candidate lines (mutters and
/// greetings ask for three), the one least like what they said lately is used. Nothing is asked of the AI again, so it
/// costs nothing. No Unity, tested.
/// </summary>
public static class PhraseLedger
{
    /// <summary>Words people (and models) start lines with, that say nothing.</summary>
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "oh", "well", "ah", "look", "listen", "hey", "yeah", "so", "huh", "hm", "hmm", "mm", "okay", "ok", "right", "honestly", "man",
        "wow", "ugh", "heh", "sure", "uh", "um", "eh", "anyway", "alright", "aye", "ha", "hah", "pfft", "tch",
    };

    public const int Openers = 12, Lines = 16;

    /// <summary>What a line opens with, as a key: a filler word, or its first two words; "" for nothing.</summary>
    public static string OpenerKey(string? line)
    {
        var words = Words(line).Take(2).ToList();
        if (words.Count == 0) return "";
        return Fillers.Contains(words[0]) ? words[0] : string.Join(" ", words);
    }

    /// <summary>
    /// The line without its opening filler, when they used that filler to open two of their last five lines ("Oh, ..."
    /// again): the rest starts with a capital. Unchanged otherwise, or when there'd be too little left.
    /// </summary>
    public static string Trim(string line, IReadOnlyList<string> recentOpeners)
    {
        if (string.IsNullOrWhiteSpace(line)) return line ?? "";
        string key = OpenerKey(line);
        if (key.Length == 0 || !Fillers.Contains(key)) return line;
        if (recentOpeners.TakeLast(5).Count(o => o == key) < 2) return line;
        string t = line.TrimStart();
        int i = 0;
        while (i < t.Length && (char.IsLetter(t[i]) || t[i] == '\'')) i++; // the filler itself
        while (i < t.Length && (t[i] is ',' or '.' or '!' or '-' or '—' or '…' || char.IsWhiteSpace(t[i]))) i++; // and what follows it
        string rest = t[i..];
        if (rest.Count(char.IsLetter) < 3) return line;
        return char.ToUpperInvariant(rest[0]) + rest[1..];
    }

    /// <summary>How alike two lines are, 0..1 (letter trigrams, Jaccard).</summary>
    public static double Similar(string a, string b)
    {
        var x = Trigrams(a);
        var y = Trigrams(b);
        if (x.Count == 0 || y.Count == 0) return 0;
        int both = x.Count(y.Contains);
        return both / (double)(x.Count + y.Count - both);
    }

    /// <summary>
    /// Which of the candidates to use: the one least like their recent lines, with a repeated opener counting against it.
    /// The first one when nothing tells them apart (or there's nothing to compare with). -1 for no candidates.
    /// </summary>
    public static int Pick(IReadOnlyList<string> candidates, IReadOnlyList<string> recentLines, IReadOnlyList<string> recentOpeners)
    {
        if (candidates.Count == 0) return -1;
        int best = 0;
        double bestCost = double.MaxValue;
        for (int i = 0; i < candidates.Count; i++)
        {
            string c = candidates[i];
            if (string.IsNullOrWhiteSpace(c)) continue;
            double like = recentLines.Count == 0 ? 0 : recentLines.Max(r => Similar(c, r));
            string key = OpenerKey(c);
            double opener = key.Length > 0 && recentOpeners.TakeLast(5).Contains(key) ? 0.3 : 0;
            double cost = like + opener;
            if (cost < bestCost - 1e-9) { bestCost = cost; best = i; }
        }
        return best;
    }

    /// <summary>A candidate line without a list's marker in front ("1.", "2)", "-", "•"), as a model sometimes numbers them anyway.</summary>
    public static string Unnumbered(string? line) => ListMarker.Replace((line ?? "").Trim(), "").Trim();

    /// <summary>
    /// Review (core IL-3): only a real list marker, one or two digits and . ) or :, or a dash, bullet or asterisk, each
    /// followed by a space. "*waves* Hi" (an action, cleaned later), "5.5 million" and "1990: ..." keep what they start with.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ListMarker = new(@"^(?:\d{1,2}[.):]|[-•*])\s+",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A line as it's kept for comparing (lower case, letters and spaces, at most 160).</summary>
    public static string Kept(string line)
    {
        string t = string.Join(" ", Words(line));
        return t.Length <= 160 ? t : t[..160];
    }

    private static IEnumerable<string> Words(string? line)
    {
        var sb = new StringBuilder();
        foreach (char ch in (line ?? "").ToLowerInvariant() + " ")
        {
            if (char.IsLetter(ch) || ch == '\'') { sb.Append(ch); continue; }
            string w = sb.ToString().Trim('\'');
            sb.Clear();
            if (w.Length > 0) yield return w;
        }
    }

    private static HashSet<string> Trigrams(string s)
    {
        string t = " " + Kept(s) + " ";
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i + 3 <= t.Length; i++) set.Add(t.Substring(i, 3));
        return set;
    }
}
