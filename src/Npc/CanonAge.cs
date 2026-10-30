using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NivalisAIChat.Npc;

/// <summary>
/// A story character's age as the game itself describes them ("Razlo is a 22 year old male", "a cheerful old man",
/// "a young girl"). Named characters keep this age: it isn't rolled, and the player's age range doesn't squeeze it.
/// </summary>
public static class CanonAge
{
    public sealed class Reading
    {
        /// <summary>The age the game states outright, if it does.</summary>
        public int? Exact;
        /// <summary>The range the description points to (Exact..Exact when stated).</summary>
        public int Min, Max;
        /// <summary>The words it came from, e.g. "old man".</summary>
        public string Cue = "";
        /// <summary>The game describes them as a child (or states an age under 18).</summary>
        public bool Child;
    }

    // Only about the person themselves: "Razlo is a 22 year old male", "she is 79 years old", "aged 79" — never "her 8-year-old
    // brother", "since the age of thirteen" or "for sixty-nine years".
    private static readonly Regex YearsOld = new(
        @"(?i)\b(?:is|she's|he's|they're|they\s+are)\s+(?:a\s+|an\s+|now\s+|only\s+|just\s+)?(?<n>\d{1,3}|[a-z]+(?:[-\s][a-z]+)?)[-\s]+(?:years?|yrs?)[-\s]+old\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Aged = new(@"(?i)(?:^|[.;,]\s*|\b(?:is|was)\s+)aged\s+(?<n>\d{1,3})\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // "Mala is a young girl", "he's just a kid", "is a schoolgirl" — not "raising a kid alone" or "since he was a teenager".
    private static readonly Regex ChildWords = new(
        @"(?i)\b(?:is|she's|he's|they're|they\s+are)\s+(?:just\s+|only\s+|still\s+)?(?:a|an)\s+(?:young\s+|little\s+|small\s+)?(?:girl|boy|kid|child|toddler|teenager|teen|schoolgirl|schoolboy)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // The same words opening a sentence with no verb: "A young girl who sells flowers." (audit 2026-10-13). Not "A girl's best
    // friend"; "the kid" or "a kid of her own" mid-sentence stays as it was.
    private static readonly Regex ChildOpening = new(
        // Any such opening is a child ("A young girl in a yellow raincoat.", "A teen on a skateboard."), except the few adult
        // compounds ("A child psychologist", "A kid at heart", "A boy band's manager"): review R7, final review F2. When in
        // doubt it stays a child (the safe side).
        @"(?i)(?:^|[.!?]\s+)(?:a|an)\s+(?:young\s+|little\s+|small\s+)?(?:girl|boy|kid|child|toddler|teenager|teen|schoolgirl|schoolboy)\b(?!['\u2019])" +
        @"(?!\s+(?:psychologist|psychiatrist|therapist|actor|actress|star|prodigy|band|labou?r|care|carer|minder|support|protection|services?|at\s+heart)\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly (Regex re, int min, int max)[] Bands =
    {
        (new Regex(@"(?i)\b(?:elderly|ancient|very old)\b", RegexOptions.CultureInvariant), 68, 85),
        (new Regex(@"(?i)\bold\s+(?:man|woman|lady|timer|fella|fellow|codger|sailor|fisherman)\b|\bretired\b|\bpensioner\b|\bgrand(?:mother|father|ma|pa)\b|\baged and worn\b|\bsenior citizen\b", RegexOptions.CultureInvariant), 60, 75),
        (new Regex(@"(?i)\bmiddle[-\s]aged\b", RegexOptions.CultureInvariant), 42, 55),
        (new Regex(@"(?i)\byoung\s+(?:man|woman|lady|guy|business\s+owner|entrepreneur|artist|singer|rock\s+star|""?rock\s+star""?|professional|worker|mother|father|couple)\b|\bcheery young\b|\battractive young\b", RegexOptions.CultureInvariant), 20, 28),
        (new Regex(@"(?i)\b(?:a|the)\s+(?:talented\s+)?(?:university\s+|college\s+)?student\b", RegexOptions.CultureInvariant), 19, 25),
    };

    private static readonly string[] Units = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };
    private static readonly string[] Teens = { "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
    private static readonly string[] Tens = { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };

    // Any word for a young person in an age answer ("late teens", "a kid", "a minor", "teenaged") means under 18.
    private static readonly Regex SeenChild = new(
        @"(?i)\b(?:teen\w*|pre-?teen\w*|tweens?|adolescen\w*|pubescent|child\w*|kids?|kiddo|minors?|under[-\s]?age\w*|school(?:girl|boy|kid)s?|high[-\s]?schoolers?|youngsters?|juveniles?|boy|girl)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // "under 18", "younger than eighteen", "not yet 18", "below 20": young enough to be a minor.
    private static readonly Regex SeenUnder = new(
        @"(?i)\b(?:under|below|younger\s+than|less\s+than|not\s+(?:yet|quite)|barely|almost|nearly)\s+(?<n>\d{1,2}|[a-z]+(?:-[a-z]+)?)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SeenAdult = new(
        @"(?i)\b(?:adult|grown|grown-up|middle[-\s]?aged|elderly|old|older|senior|retired|pensioner|mature|aged)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SeenDecade = new(
        @"(?i)\b(?:(?<part>early|mid|late)[-\s]?)?(?<decade>twenties|thirties|forties|fifties|sixties|seventies|eighties|nineties)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SeenDigits = new(@"\d{1,3}", RegexOptions.Compiled);
    // Narrower, for a description of how they look or what they do: "minor scars" and "kid-leather gloves" are adults.
    private static readonly Regex LooksYoung = new(
        @"(?i)\b(?:teen\w*|pre-?teen\w*|adolescen\w*|pubescent|school(?:girl|boy|kid)s?|high[-\s]?schoolers?|under[-\s]?age\w*|child-?like|(?:young|little|small)\s+(?:girl|boy|kid|child))\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PupilJob = new(
        @"(?i)\b(?:(?:high|middle|secondary|primary|elementary|junior)[-\s]?school(?:er|\s+(?:student|pupil|kid))|school(?:girl|boy|kid)s?|pupil|teen\w*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The age an AI gave after looking at a picture of someone, however it wrote it ("52", "about fifty", "late
    /// twenties", "seventeen-ish", "a teenager", "under 18"). <c>Child</c> is true if ANY age it mentions is under 18,
    /// it uses any word for a young person, or (with <paramref name="unreadableIsChild"/>) no age can be read at all
    /// and nothing says they're grown up: an unclear answer is never read as an adult. Age = the first age mentioned.
    /// </summary>
    public static (int? age, bool child) FromSeen(string? text, bool unreadableIsChild = true)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return (null, unreadableIsChild);
        bool young = SeenChild.IsMatch(t);
        foreach (Match u in SeenUnder.Matches(t))
            if (ReadNumber(u.Groups["n"].Value) is int limit && limit <= 21) young = true;

        var ages = new List<(int at, int age)>();
        foreach (Match d in SeenDigits.Matches(t))
            if (ReadDigits(d.Value) is int n) ages.Add((d.Index, n));
        foreach (Match dec in SeenDecade.Matches(t))
        {
            int decade = dec.Groups["decade"].Value.ToLowerInvariant() switch
            {
                "twenties" => 20, "thirties" => 30, "forties" => 40, "fifties" => 50,
                "sixties" => 60, "seventies" => 70, "eighties" => 80, _ => 90,
            };
            ages.Add((dec.Index, decade + dec.Groups["part"].Value.ToLowerInvariant() switch { "early" => 2, "late" => 8, _ => 5 }));
        }
        // number words ("about fifty-two", "seventeen-ish"); a lone "one" or "five" only counts as "five years old"
        bool years = Regex.IsMatch(t, @"(?i)\byears?\b|\byrs?\b", RegexOptions.CultureInvariant);
        var words = Regex.Matches(t.ToLowerInvariant(), @"[a-z]+(?:-[a-z]+)*").Cast<Match>().ToList();
        for (int i = 0; i < words.Count; i++)
        {
            string one = Bare(words[i].Value);
            int? pair = i + 1 < words.Count ? Number(one + " " + Bare(words[i + 1].Value)) : null;
            int? w = pair ?? Number(one);
            if (w is >= 10 || (w != null && years)) ages.Add((words[i].Index, w.Value));
            if (pair != null) i++; // "fifty two": the "two" isn't an age of its own
        }

        int? first = ages.Count > 0 ? ages.OrderBy(a => a.at).First().age : null;
        bool unreadable = ages.Count == 0 && !SeenAdult.IsMatch(t);
        return (first, young || ages.Any(a => a.age < Population.AdultAge) || (unreadable && unreadableIsChild));
    }

    /// <summary>"seventeen-ish" → "seventeen", "16ish" → "16".</summary>
    private static string Bare(string word) => Regex.Replace(word, @"-?ish$", "");

    private static int? ReadNumber(string text)
    {
        string b = Bare((text ?? "").Trim().ToLowerInvariant());
        return ReadDigits(b) ?? Number(b);
    }

    /// <summary>
    /// Digits read as a number in any script: "16", "١٦" and "१६" are all 16 (the digits .NET's \d matches). Null if it
    /// isn't only digits, or is longer than 3 (no age is). int.TryParse reads ASCII digits only, so an age under 18
    /// written in another script used to read as no age at all, which the paths that treat "unreadable" as an adult
    /// took for one (Q4: safety wins).
    /// </summary>
    public static int? ReadDigits(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length is 0 or > 3) return null;
        int n = 0;
        foreach (char c in t)
        {
            int d = char.IsDigit(c) ? (int)char.GetNumericValue(c) : -1;
            if (d is < 0 or > 9) return null;
            n = n * 10 + d;
        }
        return n;
    }

    /// <summary>Whether a description of how someone looks calls them a teenager, a child or similar.</summary>
    public static bool SoundsYoung(string? text) => LooksYoung.IsMatch(text ?? "");

    /// <summary>Whether a job describes a school pupil or a teenager.</summary>
    public static bool SchoolPupil(string? occupation) => PupilJob.IsMatch(occupation ?? "");

    /// <summary>"22" / "twenty-two" / "seventeen" → the number; null if it isn't one.</summary>
    public static int? Number(string text)
    {
        string t = (text ?? "").Trim().ToLowerInvariant();
        if (ReadDigits(t) is int d) return d;
        int u = Array.IndexOf(Units, t);
        if (u >= 0) return u;
        int teen = Array.IndexOf(Teens, t);
        if (teen >= 0) return 10 + teen;
        var parts = t.Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 2) return null;
        int tens = Array.IndexOf(Tens, parts[0]);
        if (tens < 2) return null;
        if (parts.Length == 1) return tens * 10;
        int unit = Array.IndexOf(Units, parts[1]);
        return unit <= 0 ? null : tens * 10 + unit;
    }

    /// <summary>What the game's description says about their age; null when it says nothing.</summary>
    public static Reading? FromDescription(string? description)
    {
        string d = description ?? "";
        if (d.Trim().Length == 0) return null;

        foreach (Match m in YearsOld.Matches(d))
            if (Number(m.Groups["n"].Value) is { } n and > 0 and < 130)
                return new Reading { Exact = n, Min = n, Max = n, Cue = m.Value.Trim(), Child = n < Population.AdultAge };
        if (Aged.Match(d) is { Success: true } aged && ReadDigits(aged.Groups["n"].Value) is int a && a is > 0 and < 130)
            return new Reading { Exact = a, Min = a, Max = a, Cue = aged.Value.Trim(), Child = a < Population.AdultAge };

        if (ChildWords.Match(d) is { Success: true } child)
            return new Reading { Min = 6, Max = 14, Cue = child.Value.Trim(), Child = true };
        if (ChildOpening.Match(d) is { Success: true } opening)
            return new Reading { Min = 6, Max = 14, Cue = opening.Value.Trim(' ', '.', '!', '?'), Child = true };

        foreach (var (re, min, max) in Bands)
            if (re.Match(d) is { Success: true } m)
                return new Reading { Min = min, Max = max, Cue = m.Value.Trim() };
        return null;
    }
}
