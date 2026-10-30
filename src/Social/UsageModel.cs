using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NivalisAIChat.Social;

/// <summary>
/// FB-41: the settings the Usage Optimizer moves (the "levers"), as numbers. Activity levers change how much residents
/// talk (they spend AI and voice); the voice levers only change how much of it is voiced.
/// </summary>
public sealed record Knobs
{
    public double PerHour { get; init; } = 75;       // ChatterPerHour
    public double Every { get; init; } = 10;         // DynamicEvery, seconds
    public double MaxNpcs { get; init; } = 3;        // DynamicMaxNpcs
    public double StartsEach { get; init; } = 2;     // DynamicStartsEach
    public double Lines { get; init; } = 4;          // DynamicLinesEach (Advanced: GroupLinesEach)
    public double PhoneLines { get; init; } = 4;     // DynamicPhoneLinesEach (Advanced: PhoneLinesEach)
    public double ThinkLines { get; init; } = 2;     // DynamicThinkLinesEach (Advanced: ThinkLinesEach)
    public double VoiceRing { get; init; } = 18;     // the voices ring (VoiceDistance + VoiceBeyond), metres
    public bool AiOrderTalk { get; init; } = true;
    public bool AiGreetings { get; init; } = true;   // AiGreetingLines
    public bool SpeakGreetings { get; init; } = true;
    /// <summary>The chatter ring (not moved: the most the voices ring could cover).</summary>
    public double ChatterRing { get; init; } = 18.25;
    /// <summary>Advanced mode: how often each kind is looked for is the player's (not moved, not modelled).</summary>
    public bool Advanced { get; init; }
}

/// <summary>What the optimizer knows of one budget: its limit, its use so far, its period, and what it learned it costs.</summary>
public sealed class BudgetInput
{
    public string Key = "", Name = "", Unit = "";
    /// <summary>The AI's TokenLimit (otherwise a voice provider's own limit).</summary>
    public bool Ai;
    /// <summary>The most it may use this period (0: no limit).</summary>
    public double Limit;
    public double Used;
    public DateTime Reset;
    /// <summary>Costs nothing (a voice server on your own PC, Windows' voices).</summary>
    public bool Free;
    /// <summary>Your own conversations' use per play-hour (never moved by the optimizer).</summary>
    public double YoursPerHour;
    /// <summary>Residents' use per conversation started, at the settings as they are now.</summary>
    public double PerStart;
    /// <summary>Street greetings' use per play-hour, as they are now.</summary>
    public double GreetPerHour;
    /// <summary>A voice provider: what a line costs on average (for sharing new people out).</summary>
    public double LineCost = 1;
    /// <summary>A voice provider: its chance for new people now.</summary>
    public double Chance;
    /// <summary>Play-hours expected before <see cref="Reset"/>.</summary>
    public double HoursLeft = 1;
    /// <summary>
    /// FB-59: what the provider's account itself has left this period (everyone using its key; ElevenLabs says). The plan never
    /// plans past it, whatever your own limit. Infinity: not known.
    /// </summary>
    public double AccountLeft = double.PositiveInfinity;
    /// <summary>
    /// FB-75: used by you alone (speech to text: residents never talk into your microphone). Shown with its forecast, but
    /// it never moves residents' talk: nothing they do changes it.
    /// </summary>
    public bool YoursOnly;
    /// <summary>Limited by something: your own limit, or what the account has left.</summary>
    public bool Capped => !Free && (Limit > 0 || !double.IsPositiveInfinity(AccountLeft));
    /// <summary>Capped, and something the plan can keep within by moving residents' talk.</summary>
    public bool Steers => Capped && !YoursOnly;
}

/// <summary>What the optimizer recommends, and what every budget comes to with it.</summary>
public sealed class UsagePlan
{
    public double Activity, Voice;
    public Knobs Settings = new();
    public bool Feasible = true;
    /// <summary>Some budget has a limit (otherwise there's nothing to keep within).</summary>
    public bool Limited;
    /// <summary>Each budget's use at its reset with this plan, as a share of its limit (0 for none).</summary>
    public Dictionary<string, double> AtReset = new();
    /// <summary>The budget that sets the level (the first to reach its target).</summary>
    public string? Binding;
    /// <summary>Recommended chances for new people, by voice budget key.</summary>
    public Dictionary<string, double> Chances = new();
    public List<string> Notes = new();
}

/// <summary>
/// FB-41: turns settings into spending per play-hour for every budget, and
/// finds the liveliest settings that keep each one under (1 - headroom) of its limit by its reset. Your own conversations'
/// share is never moved; pinned settings stay as they are. No Unity, no files.
/// </summary>
public static class UsageModel
{
    /// <summary>The usage record's book of resident conversations started (one unit each, by kind).</summary>
    public const string StartsKey = "events|starts";
    /// <summary>The usage record's book of what VoiceLimit counts: characters sent to paid voices, all of them together.</summary>
    public const string VoiceCharsKey = "voices|characters";

    /// <summary>
    /// What counts as your side, the residents' side and greetings, by the usage record's purposes. Anything not in the
    /// residents' or greetings' lists is counted as yours (never made smaller), so every purpose a request or a voice
    /// uses must be listed here on purpose: a test fails on one that isn't (step 0 of the research plan, 2026-10-18).
    /// "group" (FB-66) and "story voice" (FB-50) are yours: a group chat is your conversation, a story sheet is made for
    /// your chats. Companions speaking up and asides go through "greet" (BarkAsync), so the AiGreetingLines lever covers them.
    /// </summary>
    public static readonly string[] YoursAi = { "chat", "told", "persona", "look", "diary", "memory", "life", "group", "world", "story voice", "text", "test" }; // FB-76: world chat is your own conversation
    public static readonly string[] ResidentsAi = { "chatter", "phone", "think", "order talk", "text first", "nightline", "nightline photo" }; // the phone: a text someone sends you first is one of the residents' kinds (DynamicTextWeight); FB-71: Nightline visits and their photos
    public static readonly string[] GreetAi = { "greet" };
    public static readonly string[] YoursVoice = { "you", "test", "text voice" }; // the phone: voice notes on their answers to your texts
    /// <summary>Residents' voices by kind: conversations, calls, muttering (its own since step 0; it was counted as calls), orders.</summary>
    public static readonly string[] ResidentsVoice = { "chatter", "call", "think", "order" };
    public static readonly string[] GreetVoice = { "greeting" };

    /// <summary>The quiet and lively ends of each activity lever (lively: a generous level, or yours if it's higher).</summary>
    public static readonly Knobs Quiet = new() { PerHour = 10, Every = 60, MaxNpcs = 2, StartsEach = 1, Lines = 2, PhoneLines = 2, ThinkLines = 1, VoiceRing = 6 };
    public static readonly Knobs Lively = new() { PerHour = 150, Every = 5, MaxNpcs = 6, StartsEach = 3, Lines = 6, PhoneLines = 6, ThinkLines = 3 };

    /// <summary>The DynamicEvery choices the menu offers, in seconds.</summary>
    public static readonly int[] EverySteps = { 3, 5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 300, 600, 900, 1800, 3600 };

    /// <summary>"10s", "2m", "1h" from seconds (the choices DynamicEvery takes).</summary>
    public static string EveryText(double seconds)
    {
        int s = EverySteps.OrderBy(x => Math.Abs(x - seconds)).First();
        return s >= 3600 ? $"{s / 3600}h" : s >= 120 && s % 60 == 0 ? $"{s / 60}m" : s == 60 ? "1m" : $"{s}s";
    }

    // ------------------------------------------------------------------ learning

    /// <summary>An estimate from little data: a prior worth <paramref name="weight"/> of observations, blended with what was seen.</summary>
    public static double Shrink(double prior, double total, double seen, double weight) =>
        !(seen > 0) ? prior : (prior * weight + total) / (weight + seen);

    /// <summary>How sure the numbers are, by play-hours seen.</summary>
    public static string Confidence(double hours) => hours < 2 ? "low" : hours < 8 ? "medium" : "good";

    /// <summary>Play-hours a day to plan with: learned (blended toward 2 h while there are few days of play), or yours if you set it.</summary>
    public static double HoursPerDay(double learned, int daysPlayed, double? yours = null)
    {
        if (yours is double y && y > 0) return Math.Min(24, y);
        double w = Math.Min(1, daysPlayed / 5.0);
        return Math.Clamp(w * learned + (1 - w) * 2.0, 0.25, 24);
    }

    /// <summary>Play-hours expected before a reset (at least half an hour, so near a reset nothing divides by nothing).</summary>
    public static double HoursUntil(DateTime now, DateTime reset, double hoursPerDay) =>
        Math.Max(0.5, (reset - now).TotalDays * hoursPerDay);

    /// <summary>How often a look for a conversation starts one, from what was seen (a prior of 0.3 while there's little).</summary>
    public static double StartRate(double startsPerHourSeen, double hoursSeen, Knobs now)
    {
        double looks = 3600 / Math.Max(3, now.Every) * Math.Max(1, now.StartsEach);
        double seen = hoursSeen > 0 ? startsPerHourSeen / looks : 0.3;
        return Math.Clamp(Shrink(0.3, seen * hoursSeen, hoursSeen, 2), 0.02, 1);
    }

    // ------------------------------------------------------------------ predicting

    /// <summary>Conversations started per play-hour with these settings.</summary>
    public static double StartsPerHour(Knobs s, Knobs now, double startRate)
    {
        if (s.Advanced) return Math.Min(Math.Max(0, s.PerHour), Math.Max(0, startRate * 3600 / Math.Max(3, now.Every) * Math.Max(1, now.StartsEach)));
        double looks = 3600 / Math.Max(3, s.Every) * Math.Max(1, s.StartsEach);
        double crowd = Math.Sqrt(Math.Clamp(s.MaxNpcs / Math.Max(1, now.MaxNpcs), 0.25, 4));
        return Math.Min(Math.Max(0, s.PerHour), startRate * looks * crowd);
    }

    /// <summary>How much longer (or shorter) conversations are than now, by the kinds' mix.</summary>
    public static double LengthFactor(Knobs s, Knobs now, IReadOnlyDictionary<string, double> mix)
    {
        double talk = mix.GetValueOrDefault("talk", 0.6), call = mix.GetValueOrDefault("call", 0.2), think = mix.GetValueOrDefault("think", 0.15), order = mix.GetValueOrDefault("order", 0.05);
        double sum = talk + call + think + order;
        if (!(sum > 0)) return 1;
        static double R(double a, double b) => b > 0 ? a / b : 1;
        return (talk * R(s.Lines, now.Lines) + call * R(s.PhoneLines, now.PhoneLines) + think * R(s.ThinkLines, now.ThinkLines) + order) / sum;
    }

    /// <summary>How much of the residents' talk is voiced compared with now: by the area of the voices ring, at most all of the chatter ring.</summary>
    public static double VoicedFactor(Knobs s, Knobs now)
    {
        double ring = Math.Max(0.5, now.VoiceRing);
        double cap = Math.Max(1, Math.Pow(Math.Max(ring, s.ChatterRing) / ring, 2));
        return Math.Min(cap, Math.Pow(Math.Max(0, s.VoiceRing) / ring, 2));
    }

    /// <summary>A budget's use per play-hour with these settings.</summary>
    public static double PerHour(BudgetInput b, Knobs s, Knobs now, double startRate, IReadOnlyDictionary<string, double> mix)
    {
        double starts = StartsPerHour(s, now, startRate);
        double length = LengthFactor(s, now, mix);
        double residents, greet;
        if (b.Ai)
        {
            // Writing a conversation is part prompt (the same whatever its length) and part lines.
            double orderShare = mix.GetValueOrDefault("order", 0) / Math.Max(1e-9, mix.Values.Sum());
            double orderOff = now.AiOrderTalk && !s.AiOrderTalk ? orderShare : 0;
            residents = starts * b.PerStart * (0.5 + 0.5 * length) * (1 - orderOff);
            greet = s.AiGreetings ? b.GreetPerHour : now.AiGreetings ? 0 : b.GreetPerHour;
        }
        else
        {
            double voiced = VoicedFactor(s, now);
            residents = starts * b.PerStart * length * voiced;
            greet = s.SpeakGreetings ? b.GreetPerHour : now.SpeakGreetings ? 0 : b.GreetPerHour;
        }
        return Math.Max(0, b.YoursPerHour + residents + greet);
    }

    /// <summary>What a budget will have used at its reset, with these settings.</summary>
    public static double AtReset(BudgetInput b, Knobs s, Knobs now, double startRate, IReadOnlyDictionary<string, double> mix) =>
        b.Used + b.HoursLeft * PerHour(b, s, now, startRate, mix);

    // ------------------------------------------------------------------ the dial

    /// <summary>The names of the levers, as the plan keeps pins and the tab shows them.</summary>
    public static readonly string[] Levers =
        { "PerHour", "Every", "MaxNpcs", "StartsEach", "Lines", "PhoneLines", "ThinkLines", "VoiceRing", "AiOrderTalk", "AiGreetings", "SpeakGreetings" };

    /// <summary>A voice provider's chance as a lever (pinned, applied and undone like the others).</summary>
    public static string ChanceLever(string key) => "chance:" + key;

    /// <summary>Every lever the plan may move: the settings, then each voice provider's chance.</summary>
    public static List<string> AllLevers(IEnumerable<string> voiceKeys) => Levers.Concat(voiceKeys.Select(ChanceLever)).ToList();

    private static double Stage(double level, double from, double to) => Math.Clamp((level - from) / (to - from), 0, 1);
    private static double Mix(double quiet, double lively, double t) => quiet + (lively - quiet) * t;

    /// <summary>
    /// The settings at an activity level (0-1) and a voice level (0-1), in the order things give way as the levels come down:
    /// lines each first (from 100% to 70%), then how often and how many (70% to 40%), then the hourly cap (40% to 8%), then
    /// AI-written order talk and greetings (under 8% and 4%). The voice level moves the voices ring (and greetings' voices
    /// under 10%). A pinned lever stays as it is now; the voices ring never goes past yours.
    /// </summary>
    public static Knobs At(double activity, double voice, Knobs now, IReadOnlySet<string> pinned)
    {
        double a = Math.Clamp(activity, 0, 1), v = Math.Clamp(voice, 0, 1);
        double Lv(Func<Knobs, double> f, bool higherIsLivelier = true) => higherIsLivelier ? Math.Max(f(Lively), f(now)) : Math.Min(f(Lively), f(now));
        double lines = Stage(a, 0.70, 1.00), rate = Stage(a, 0.40, 0.70), cap = Stage(a, 0.08, 0.40);
        double P(string name, double value, double current) => pinned.Contains(name) ? current : value;
        // How often: moved as a rate (looks an hour), so the steps feel even.
        double everyRate = Mix(1 / Quiet.Every, 1 / Lv(k => k.Every, higherIsLivelier: false), rate);
        double every = now.Advanced ? now.Every : P("Every", EverySteps.OrderBy(x => Math.Abs(1.0 / x - everyRate)).First(), now.Every);
        return now with
        {
            PerHour = P("PerHour", Math.Round(Mix(Quiet.PerHour, Lv(k => k.PerHour), cap) / 5) * 5, now.PerHour),
            Every = every,
            MaxNpcs = now.Advanced ? now.MaxNpcs : P("MaxNpcs", Math.Round(Mix(Quiet.MaxNpcs, Lv(k => k.MaxNpcs), rate)), now.MaxNpcs),
            StartsEach = now.Advanced ? now.StartsEach : P("StartsEach", Math.Round(Mix(Quiet.StartsEach, Lv(k => k.StartsEach), rate)), now.StartsEach),
            Lines = P("Lines", Math.Round(Mix(Quiet.Lines, Lv(k => k.Lines), lines)), now.Lines),
            PhoneLines = P("PhoneLines", Math.Round(Mix(Quiet.PhoneLines, Lv(k => k.PhoneLines), lines)), now.PhoneLines),
            ThinkLines = P("ThinkLines", Math.Round(Mix(Quiet.ThinkLines, Lv(k => k.ThinkLines), lines)), now.ThinkLines),
            VoiceRing = P("VoiceRing", Rings.Snap((float)Mix(Math.Min(Quiet.VoiceRing, now.VoiceRing), now.VoiceRing, v)), now.VoiceRing),
            AiOrderTalk = pinned.Contains("AiOrderTalk") ? now.AiOrderTalk : now.AiOrderTalk && a >= 0.08,
            AiGreetings = pinned.Contains("AiGreetings") ? now.AiGreetings : now.AiGreetings && a >= 0.04,
            SpeakGreetings = pinned.Contains("SpeakGreetings") ? now.SpeakGreetings : now.SpeakGreetings && v >= 0.10,
        };
    }

    /// <summary>
    /// Within its target: under (1 - headroom) of its limit at its reset, and (FB-59) what's still to come under (1 - headroom)
    /// of what the account has left (no limit and nothing known of the account, or free: always).
    /// </summary>
    private static bool Fits(BudgetInput b, Knobs s, Knobs now, double rate, IReadOnlyDictionary<string, double> mix, double headroom)
    {
        if (b.Free || b.YoursOnly) return true;
        double at = AtReset(b, s, now, rate, mix);
        bool own = !(b.Limit > 0) || at <= (1 - headroom) * b.Limit;
        bool account = double.IsPositiveInfinity(b.AccountLeft) || at - b.Used <= (1 - headroom) * Math.Max(0, b.AccountLeft);
        return own && account;
    }

    /// <summary>
    /// The plan at a given activity level (the dial, moved by you): the liveliest voice level that fits there, every budget's
    /// share at its reset, and the chances for new people.
    /// </summary>
    public static UsagePlan AtLevel(double activity, IReadOnlyList<BudgetInput> budgets, Knobs now, IReadOnlySet<string> pinned,
        double startRate, IReadOnlyDictionary<string, double> mix, double headroom)
    {
        double v = 1;
        for (; v > 0; v = Math.Round(v - 0.05, 2))
            if (budgets.All(b => b.Ai || Fits(b, At(activity, v, now, pinned), now, startRate, mix, headroom))) break;
        return Finish(activity, Math.Max(0, v), budgets, now, pinned, startRate, mix, headroom);
    }

    /// <summary>
    /// The liveliest plan that keeps every limited budget within its target by its reset: the activity level as high as it
    /// can go (with the voices ring given up first where only voices are short), then the voice level as high as it can go
    /// there. Without any limit: the settings as they are. When even the quietest settings (with your pins) don't fit, it
    /// says which budget and what's left to try.
    /// </summary>
    public static UsagePlan Best(IReadOnlyList<BudgetInput> budgets, Knobs now, IReadOnlySet<string> pinned, double startRate,
        IReadOnlyDictionary<string, double> mix, double headroom)
    {
        bool limited = budgets.Any(b => b.Steers);
        if (!limited)
        {
            // FB-59: still a plan: your settings as they are, at 100% (move the dial to plan for less).
            var same = Finish(1, 1, budgets, now, Levers.ToHashSet(), startRate, mix, headroom);
            same.Limited = false;
            same.Notes.Add(budgets.Any(b => b.Capped)
                ? "Only limits of your own use are set (speech to text): residents' talk isn't moved for them, so this is your plan as your settings have it."
                : "No limit is set, so this is your plan as your settings have it: move the dial to plan for less, or set a limit to plan within it.");
            return same;
        }
        for (double a = 1; a >= 0; a = Math.Round(a - 0.01, 2))
        {
            if (!budgets.All(b => Fits(b, At(a, 0, now, pinned), now, startRate, mix, headroom))) continue;
            double v = 1;
            for (; v > 0; v = Math.Round(v - 0.05, 2))
                if (budgets.All(b => Fits(b, At(a, v, now, pinned), now, startRate, mix, headroom))) break;
            return Finish(a, Math.Max(0, v), budgets, now, pinned, startRate, mix, headroom);
        }
        var quiet = Finish(0, 0, budgets, now, pinned, startRate, mix, headroom);
        quiet.Feasible = false;
        foreach (var b in budgets.Where(b => b.Steers && !Fits(b, quiet.Settings, now, startRate, mix, headroom)))
        {
            if (!(b.Limit > 0))
            {
                quiet.Notes.Add($"{b.Name}: the account has only {b.AccountLeft:N0} left before its reset, less than even the quietest settings need.");
                continue;
            }
            double yours = (b.Used + b.HoursLeft * b.YoursPerHour) / b.Limit;
            quiet.Notes.Add(yours > 1 - headroom
                ? $"{b.Name}: your own conversations alone come to {yours:P0} of its limit by its reset. Raise the limit, or set less headroom."
                : $"{b.Name}: even the quietest settings come to {quiet.AtReset[b.Key]:P0} of its limit by its reset{(pinned.Count > 0 ? " with your pins: unpin one to let it give way" : "")}.");
        }
        return quiet;
    }

    private static UsagePlan Finish(double activity, double voice, IReadOnlyList<BudgetInput> budgets, Knobs now, IReadOnlySet<string> pinned,
        double startRate, IReadOnlyDictionary<string, double> mix, double headroom)
    {
        var s = At(activity, voice, now, pinned);
        var plan = new UsagePlan { Activity = activity, Voice = voice, Settings = s, Limited = budgets.Any(b => b.Steers) };
        double tightest = -1;
        foreach (var b in budgets)
        {
            // A share of your limit; with none of yours but an account's remainder (FB-59), a share of what's still to come of that.
            double share = b.Limit > 0 ? AtReset(b, s, now, startRate, mix) / b.Limit
                : !double.IsPositiveInfinity(b.AccountLeft) && !b.Free ? (AtReset(b, s, now, startRate, mix) - b.Used) / Math.Max(1, b.AccountLeft) : 0;
            plan.AtReset[b.Key] = share;
            if (b.Steers && share > tightest) { tightest = share; plan.Binding = b.Key; }
        }
        plan.Chances = Chances(budgets, headroom);
        plan.Feasible = budgets.All(b => Fits(b, s, now, startRate, mix, headroom));
        return plan;
    }

    /// <summary>
    /// Chances for new people: paid voice providers with a limit share what the limited ones have now, in proportion to
    /// what each has left / (play-hours to its reset x what a line costs); providers without a limit, and free ones, keep
    /// theirs. People keep the voice they were given.
    /// </summary>
    public static Dictionary<string, double> Chances(IReadOnlyList<BudgetInput> budgets, double headroom)
    {
        var voices = budgets.Where(b => !b.Ai && !b.YoursOnly).ToList();
        var result = voices.ToDictionary(b => b.Key, b => b.Chance);
        var limited = voices.Where(b => b.Limit > 0 && !b.Free && b.Chance > 0).ToList();
        if (limited.Count < 2) return result;
        double pool = limited.Sum(b => b.Chance);
        var w = limited.ToDictionary(b => b.Key, b => Math.Max(0, (1 - headroom) * b.Limit - b.Used - b.HoursLeft * b.YoursPerHour) / (Math.Max(0.5, b.HoursLeft) * Math.Max(1e-6, b.LineCost)));
        double sum = w.Values.Sum();
        foreach (var b in limited) result[b.Key] = sum > 0 ? Math.Round(pool * w[b.Key] / sum, 1) : b.Chance;
        return result;
    }

    /// <summary>A lever's value as the setting writes it ("10s" for DynamicEvery, metres for the ring, on/off).</summary>
    public static string Show(string lever, Knobs k) => lever switch
    {
        "PerHour" => k.PerHour.ToString("0", CultureInfo.InvariantCulture),
        "Every" => EveryText(k.Every),
        "MaxNpcs" => k.MaxNpcs.ToString("0", CultureInfo.InvariantCulture),
        "StartsEach" => k.StartsEach.ToString("0", CultureInfo.InvariantCulture),
        "Lines" => k.Lines.ToString("0", CultureInfo.InvariantCulture),
        "PhoneLines" => k.PhoneLines.ToString("0", CultureInfo.InvariantCulture),
        "ThinkLines" => k.ThinkLines.ToString("0", CultureInfo.InvariantCulture),
        "VoiceRing" => Rings.M((float)k.VoiceRing),
        "AiOrderTalk" => k.AiOrderTalk ? "on" : "off",
        "AiGreetings" => k.AiGreetings ? "on" : "off",
        "SpeakGreetings" => k.SpeakGreetings ? "on" : "off",
        _ => "",
    };

    /// <summary>
    /// What residents may spend of a budget per play-hour from now on to end at its target: what's left of the target after
    /// your own conversations and greetings to the reset, over the play-hours left (no limit: unlimited).
    /// </summary>
    public static double ResidentsAllowed(BudgetInput b, double headroom)
    {
        if (!b.Steers) return double.PositiveInfinity;
        double hours = Math.Max(0.05, b.HoursLeft), others = b.HoursLeft * (b.YoursPerHour + b.GreetPerHour);
        double own = b.Limit > 0 ? Math.Max(0, (1 - headroom) * b.Limit - b.Used - others) / hours : double.PositiveInfinity;
        double account = double.IsPositiveInfinity(b.AccountLeft) ? double.PositiveInfinity : Math.Max(0, (1 - headroom) * b.AccountLeft - others) / hours;
        return Math.Min(own, account);
    }

    /// <summary>
    /// FB-59: what's left of a budget's target a calendar day until its reset (your limit, less your headroom and what's used,
    /// over the days left; at most what the account has left): "about this much a day". Infinity with no limit.
    /// </summary>
    public static double PerDayLeft(BudgetInput b, double headroom, DateTime now)
    {
        if (!b.Capped) return double.PositiveInfinity;
        double days = Math.Max(1 / 24.0, (b.Reset - now).TotalDays);
        double own = b.Limit > 0 ? Math.Max(0, (1 - headroom) * b.Limit - b.Used) : double.PositiveInfinity;
        double left = Math.Min(own, double.IsPositiveInfinity(b.AccountLeft) ? double.PositiveInfinity : Math.Max(0, (1 - headroom) * b.AccountLeft));
        return left / days;
    }

    /// <summary>The kinds' mix when nothing has been recorded yet.</summary>
    public static readonly IReadOnlyDictionary<string, double> DefaultMix = new Dictionary<string, double> { ["talk"] = 0.6, ["call"] = 0.2, ["think"] = 0.15, ["order"] = 0.05 };

    /// <summary>
    /// FB-59: the mix with kinds switched off (in Features or their own switch) taken out at once, rather than waiting for new
    /// hours to show it: a look that would have started one starts another kind instead, so the starts stay as they are.
    /// </summary>
    public static Dictionary<string, double> WithoutKinds(IReadOnlyDictionary<string, double> mix, IReadOnlyCollection<string> off)
    {
        var from = mix.Count > 0 ? mix : DefaultMix;
        var result = from.ToDictionary(kv => kv.Key, kv => off.Contains(kv.Key) ? 0 : kv.Value);
        foreach (var k in off) result[k] = 0; // a kind missing from the record isn't filled in from the defaults
        return result;
    }

    /// <summary>
    /// FB-59: how a conversation's cost moves when the mix moves (kinds switched off): what a start costs on average with the new
    /// mix, over the old, by what each kind costs (<paramref name="cost"/>; a kind without one costs the average). 1 when unchanged.
    /// </summary>
    public static double MixCostFactor(IReadOnlyDictionary<string, double> before, IReadOnlyDictionary<string, double> after, IReadOnlyDictionary<string, double> cost)
    {
        double known = cost.Count > 0 ? cost.Values.Average() : 1;
        double Avg(IReadOnlyDictionary<string, double> m)
        {
            double w = m.Values.Where(x => x > 0).Sum();
            return w > 0 ? m.Where(kv => kv.Value > 0).Sum(kv => kv.Value * cost.GetValueOrDefault(kv.Key, known)) / w : 0;
        }
        double a = Avg(before), b = Avg(after);
        return a > 0 && b > 0 ? b / a : 1;
    }
}

/// <summary>
/// FB-41 stage 4: keeps residents' talk on pace between plans. Once a play-minute it compares, for every limited budget,
/// what residents may spend per hour from now on with what they spend at the settings as they are, and moves a multiplier
/// on the conversations started an hour toward the lowest ratio: inside 0.9-1.1 of it nothing moves (no hunting), outside
/// it a square-root step, never under 0.2 or over 1. Your own conversations are never paced. No Unity here.
/// </summary>
public sealed class Pacer
{
    public const double Floor = 0.2, Low = 0.9, High = 1.1;

    /// <summary>The share of residents' conversations allowed now (1: as your settings have it).</summary>
    public double M { get; private set; } = 1;
    /// <summary>The budget that holds it back (null: none).</summary>
    public string? Binding { get; private set; }

    /// <summary>A minute of play: each budget's (key, residents allowed per hour, residents' spending per hour at your settings).</summary>
    public bool Step(IEnumerable<(string key, double allowed, double residents)> budgets)
    {
        double target = 1;
        string? binding = null;
        foreach (var (key, allowed, residents) in budgets)
        {
            if (!(residents > 0) || double.IsPositiveInfinity(allowed)) continue;
            double r = Math.Max(0, allowed) / residents;
            if (r < target) { target = r; binding = key; }
        }
        target = Math.Clamp(target, Floor, 1);
        double before = M;
        double ratio = target / M;
        if (ratio < Low || ratio > High || (target >= 1 && M < 1 && ratio > 1)) M = Math.Clamp(M * Math.Sqrt(ratio), Floor, 1);
        if (M > 0.995) M = 1;
        Binding = M < 1 ? binding : null;
        return Math.Abs(M - before) > 1e-9;
    }

    public void Reset()
    {
        M = 1;
        Binding = null;
    }

    /// <summary>
    /// The cap on conversations an hour while paced: the share of what would start at your settings (the lower of your cap
    /// and what the looks would start), at least one. Unpaced: your cap as it is.
    /// </summary>
    public int Cap(int perHour, double wouldStart) =>
        M >= 1 || perHour <= 0 ? perHour : Math.Max(1, (int)Math.Round(Math.Min(perHour, Math.Max(0, wouldStart)) * M));
}
