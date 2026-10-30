using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using NivalisAIChat.Ai;
using NivalisAIChat.Social;
using NivalisAIChat.UI;
using NivalisAIChat.Voice;
using UnityEngine;

namespace NivalisAIChat;

/// <summary>
/// FB-41: the Usage Optimizer tab. It reads every budget the mod spends from
/// (the AI's TokenLimit, each paid voice provider's own limit, VoiceLimit, Voyage for information), what the usage record
/// learned each one costs per play-hour (your conversations, residents' talk, greetings), and finds the liveliest settings
/// that end each period under its limit with your headroom left (<see cref="UsageModel"/>). Your own conversations are never
/// made smaller; settings you pinned or changed since the plan stay as you set them. Nothing changes until you press Apply.
/// </summary>
public static partial class ModRuntime
{
    private const string OptimizerGroup = "Usage Optimizer";

    private static UsagePlanBook? _usagePlan;
    /// <summary>The dial while it's moving (null: not moving).</summary>
    private static double? _dialPreview;
    private static OptimizerView? _optView;
    private static float _optAt = -10f;
    private static double? _optFor;

    /// <summary>Everything the tab shows, worked out at most a few times a second (the menu reads its rows twice a second, and on every move).</summary>
    private sealed class OptimizerView
    {
        public Knobs Now = new();
        public readonly List<BudgetInput> Budgets = new();
        /// <summary>Voice budgets' provider slots by key (to set their chances).</summary>
        public readonly Dictionary<string, VoiceSlot> Slots = new();
        /// <summary>Spending per play-hour with the settings as they are now, by budget.</summary>
        public readonly Dictionary<string, double> PerHourNow = new();
        public readonly Dictionary<string, (DateTime start, DateTime reset)> Periods = new();
        public HashSet<string> Kept = new();
        public UsagePlan Best = new(), Shown = new();
        public double Rate, HoursPerDay, LearnedHours, HoursSeen, Headroom;
        public int DaysPlayed;
        public Dictionary<string, double> Mix = new();
        public (string key, string name, double used)? Voyage;
        public double ReplyTokens;
        /// <summary>FB-59: kinds switched off (out of the estimate at once), and accounts as their provider says (everyone using the key).</summary>
        public List<string> KindsOff = new();
        public readonly Dictionary<string, (double used, double limit)> Accounts = new();
    }

    private static void InvalidateOptimizer() => _optView = null;

    private static OptimizerView Optimizer()
    {
        if (_optView != null && Time.unscaledTime - _optAt < 0.3f && _optFor == _dialPreview) return _optView;
        _optView = BuildOptimizer();
        _optAt = Time.unscaledTime;
        _optFor = _dialPreview;
        return _optView;
    }

    // ------------------------------------------------------------------ the levers as settings

    private static bool AdvancedMode => string.Equals(_cfg.ChatterMode.Value.Trim(), "Advanced", StringComparison.OrdinalIgnoreCase);

    private static Knobs KnobsNow()
    {
        var r = CurrentRings().Clamped();
        bool adv = AdvancedMode;
        return new Knobs
        {
            PerHour = _cfg.ChatterPerHour.Value,
            Every = ChatterSettings.Interval(_cfg.DynamicEvery.Value) ?? 15,
            MaxNpcs = _cfg.DynamicMaxNpcs.Value,
            StartsEach = _cfg.DynamicStartsEach.Value,
            Lines = adv ? _cfg.ChatterLinesEach.Value : _cfg.DynamicLinesEach.Value,
            PhoneLines = adv ? _cfg.PhoneLinesEach.Value : _cfg.DynamicPhoneLinesEach.Value,
            ThinkLines = adv ? _cfg.ThinkLinesEach.Value : _cfg.DynamicThinkLinesEach.Value,
            VoiceRing = r.Hearing == 0f ? 0 : r.Voices,
            ChatterRing = r.Chatter,
            AiOrderTalk = _cfg.AiOrderTalk.Value,
            AiGreetings = _cfg.AiGreetingLines.Value,
            SpeakGreetings = _cfg.SpeakGreetings.Value,
            Advanced = adv,
        };
    }

    /// <summary>The setting behind a lever (null: the voices ring, which is its own rows).</summary>
    private static ConfigEntryBase? LeverEntry(string lever)
    {
        bool adv = AdvancedMode;
        return lever switch
        {
            "PerHour" => _cfg.ChatterPerHour,
            "Every" => _cfg.DynamicEvery,
            "MaxNpcs" => _cfg.DynamicMaxNpcs,
            "StartsEach" => _cfg.DynamicStartsEach,
            "Lines" => adv ? _cfg.ChatterLinesEach : _cfg.DynamicLinesEach,
            "PhoneLines" => adv ? _cfg.PhoneLinesEach : _cfg.DynamicPhoneLinesEach,
            "ThinkLines" => adv ? _cfg.ThinkLinesEach : _cfg.DynamicThinkLinesEach,
            "AiOrderTalk" => _cfg.AiOrderTalk,
            "AiGreetings" => _cfg.AiGreetingLines,
            "SpeakGreetings" => _cfg.SpeakGreetings,
            _ => null,
        };
    }

    /// <summary>The lever a setting is (for its "changed" tag in its own tab), or null.</summary>
    private static string? LeverOfKey(string key) => UsageModel.Levers.FirstOrDefault(l => LeverEntry(l)?.Definition.Key == key);

    /// <summary>Levers that mean nothing in this mode (Advanced: how often each kind is looked for is set per kind).</summary>
    private static bool LeverInUse(string lever) => !(AdvancedMode && lever is "Every" or "MaxNpcs" or "StartsEach");

    private static string LeverTitle(string lever) => lever switch
    {
        "PerHour" => "Conversations an hour, at most",
        "Every" => "How often something may start",
        "MaxNpcs" => "People talking at once",
        "StartsEach" => "Starts each time",
        "Lines" => "Lines each in a conversation",
        "PhoneLines" => "Lines each side of a call",
        "ThinkLines" => "Lines muttered",
        "VoiceRing" => "Voices ring",
        "AiOrderTalk" => "AI-written order talk",
        "AiGreetings" => "AI-written greetings",
        "SpeakGreetings" => "Greetings spoken",
        _ => lever,
    };

    private static string ChanceLever(string key) => UsageModel.ChanceLever(key);

    /// <summary>A lever's value now, as the plan record keeps it.</summary>
    private static string LeverNow(string lever)
    {
        if (lever.StartsWith("chance:", StringComparison.Ordinal))
        {
            string key = lever.Substring(7);
            var slot = _cfg.VoiceSlots.FirstOrDefault(s => VoiceService.LedgerKey(s) == key);
            return slot == null ? "" : slot.Chance.ToString("0.#", CultureInfo.InvariantCulture);
        }
        return UsageModel.Show(lever, KnobsNow());
    }

    /// <summary>Sets a lever to a value as the plan record writes it; the setting's key when the runtime needs telling (null: done).</summary>
    private static string? SetLever(string lever, string value)
    {
        if (lever.StartsWith("chance:", StringComparison.Ordinal))
        {
            string key = lever.Substring(7);
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double chance)) return null;
            var all = ProviderLines.Read(_cfg.ProviderLinesNow);
            int index = all.FindIndex(e => e.On && SlotFor(e) is { } s && VoiceService.LedgerKey(s) == key);
            if (index >= 0) SaveProvider(index, p => (p with { Chance = Math.Clamp(chance, 0, 100) }, null));
            return null;
        }
        if (lever == "VoiceRing")
        {
            if (!float.TryParse(value.Replace(" m", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out float m)) return null;
            var r = CurrentRings().Clamped();
            if (r.Hearing > 0f) ApplyRings(r.WithVoices(m));
            _menuRingAnchor = null;
            return "ChatterRange";
        }
        var entry = LeverEntry(lever);
        if (entry == null) return null;
        if (entry.SettingType == typeof(bool)) entry.BoxedValue = value == "on";
        else if (entry.SettingType == typeof(int) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) entry.BoxedValue = n;
        else if (entry.SettingType == typeof(string)) entry.BoxedValue = value;
        return entry.Definition.Key;
    }

    // ------------------------------------------------------------------ what's known

    /// <summary>What the usage record says of some books: per play-hour, by whose it is (yours, residents', greetings).</summary>
    private sealed record Learned(double Yours, double Residents, double Greet, double LineMean, double Hours, Dictionary<string, (long n, double mean)> Purposes);

    private static Learned LearnFrom(IEnumerable<string> keys, string[] residents, string[] greet)
    {
        double hourMean = 0, hours = 0, lineSum = 0;
        long lines = 0;
        var by = new Dictionary<string, (long n, double mean)>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (_voiceLedger?.Of(key) is not { } s) continue;
            hourMean += s.HourMean;
            hours = Math.Max(hours, s.HoursSeen);
            lines += s.Lines;
            lineSum += s.Lines * s.LineMean;
            foreach (var (p, v) in s.Purposes)
            {
                var had = by.TryGetValue(p, out var h) ? h : (0, 0);
                long n = had.n + v.lines;
                by[p] = (n, n > 0 ? (had.n * had.mean + v.lines * v.mean) / n : 0);
            }
        }
        double total = by.Values.Sum(v => v.n * v.mean);
        double Share(string[] set) => total > 0 ? by.Where(kv => set.Contains(kv.Key)).Sum(kv => kv.Value.n * kv.Value.mean) / total : 0;
        double res = Share(residents), gr = Share(greet);
        // Anything that isn't residents' talk or greetings is yours (never made smaller).
        return new Learned(hourMean * Math.Max(0, 1 - res - gr), hourMean * res, hourMean * gr, lines > 0 ? lineSum / lines : 0, hours, by);
    }

    private const string SampleLine = "Oh, you're back! I was just telling Mira about the market this morning.";

    private static OptimizerView BuildOptimizer()
    {
        var v = new OptimizerView { Now = KnobsNow(), Headroom = _usagePlan?.Headroom ?? 0.10 };
        var now = DateTime.Now;
        var (learnedHours, days) = _voiceLedger?.PlayTime() ?? (0, 0);
        v.LearnedHours = learnedHours;
        v.DaysPlayed = days;
        double? yours = _usagePlan?.HoursPerDay is > 0 ? _usagePlan.HoursPerDay : null;
        v.HoursPerDay = UsageModel.HoursPerDay(learnedHours, days, yours);
        double HoursLeft(DateTime reset, TokenBudget.Span? span) =>
            span == TokenBudget.Span.Hour ? Math.Max(0.05, (reset - now).TotalHours) : UsageModel.HoursUntil(now, reset, v.HoursPerDay);

        // Residents' conversations: how many start an hour, how often a look starts one, of which kinds.
        var starts = _voiceLedger?.Of(UsageModel.StartsKey);
        double startsPerHour = starts?.HourMean ?? 0, startHours = starts?.HoursSeen ?? 0;
        v.HoursSeen = startHours;
        v.Rate = UsageModel.StartRate(startsPerHour, startHours, v.Now);
        var recorded = starts?.Purposes.Count > 0 ? starts.Purposes.ToDictionary(kv => kv.Key, kv => (double)kv.Value.lines) : new Dictionary<string, double>();
        // FB-59: a kind switched off is out of the estimate at once (its looks start another kind instead), not hours later.
        var off = new List<string>();
        if (!Npc.Features.PhoneCalls) off.Add("call");
        if (!Npc.Features.Muttering) off.Add("think");
        if (!_cfg.OrderTalk.Value) off.Add("order");
        if (!Npc.Features.Nightline || !Npc.Features.Phone) off.Add("nightline"); // FB-71
        v.Mix = off.Count > 0 ? UsageModel.WithoutKinds(recorded, off) : recorded;
        v.KindsOff = off;
        bool chatter = _cfg.Chatter.Value;
        bool greets = _cfg.NpcsGreetYou.Value;
        long startsSeen = starts?.Lines ?? 0;

        // What a start costs: residents' spending over the starts it came from (FB-59: totals, not hourly averages, so hours the
        // pacer held talk down don't make a start look cheaper), by the mix as it is now.
        double PerStart(Learned l, double prior, IReadOnlyDictionary<string, string> purposeOf, string[] residentsSet)
        {
            if (!chatter) return 0;
            double total = l.Purposes.Where(kv => residentsSet.Contains(kv.Key)).Sum(kv => kv.Value.n * kv.Value.mean);
            double perStart = UsageModel.Shrink(prior, total, startsSeen, 10);
            var cost = purposeOf.Where(kv => l.Purposes.ContainsKey(kv.Value)).ToDictionary(kv => kv.Key, kv => l.Purposes[kv.Value].mean);
            return off.Count > 0 ? perStart * UsageModel.MixCostFactor(recorded.Count > 0 ? recorded : UsageModel.DefaultMix, v.Mix, cost) : perStart;
        }
        var aiKinds = new Dictionary<string, string> { ["talk"] = "chatter", ["call"] = "phone", ["think"] = "think", ["order"] = "order talk", ["text"] = "text first", ["nightline"] = "nightline" };
        var voiceKinds = new Dictionary<string, string> { ["talk"] = "chatter", ["call"] = "call", ["think"] = "think", ["order"] = "order" };

        // The AI.
        var ai = LearnFrom(_voiceLedger?.Keys.Where(k => k.StartsWith("AI|", StringComparison.Ordinal)) ?? Enumerable.Empty<string>(), UsageModel.ResidentsAi, UsageModel.GreetAi);
        var aiSpan = TokenBudget.SpanOf(_cfg.TokenLimitPer.Value) ?? TokenBudget.Span.Day;
        int aiDay = Math.Clamp(_cfg.TokenLimitResetDay.Value, 1, 31);
        var aiStart = TokenBudget.Start(now, aiSpan, aiDay);
        var aiReset = TokenBudget.NextReset(now, aiSpan, aiDay);
        v.Budgets.Add(new BudgetInput
        {
            Key = "ai", Name = "AI", Unit = "tokens", Ai = true, Limit = Math.Max(0, _cfg.TokenLimit.Value), Used = _budget?.Used ?? 0, Reset = aiReset,
            YoursPerHour = UsageModel.Shrink(12000, ai.Yours * ai.Hours, ai.Hours, 2),
            PerStart = PerStart(ai, 2500, aiKinds, UsageModel.ResidentsAi),
            GreetPerHour = UsageModel.Shrink(greets && _cfg.AiGreetingLines.Value ? 1500 : 0, ai.Greet * ai.Hours, ai.Hours, 2),
            HoursLeft = HoursLeft(aiReset, aiSpan),
        });
        v.Periods["ai"] = (aiStart, aiReset);
        v.ReplyTokens = ai.Purposes.TryGetValue("chat", out var chat) ? chat.mean : 0;

        // Each voice provider that's on.
        if (_cfg.VoicesEnabled.Value && _voices != null)
        {
            var slots = _cfg.VoiceSlots.ToList();
            double chances = slots.Sum(s => Math.Max(0, s.Chance));
            double billedShare = 0;
            foreach (var slot in slots)
            {
                string key = VoiceService.LedgerKey(slot);
                if (v.Slots.ContainsKey(key)) continue;
                string model = VoiceService.ModelOf(slot);
                bool billed = VoiceService.Billed(slot);
                double share = chances > 0 ? Math.Max(0, slot.Chance) / chances : 1.0 / Math.Max(1, slots.Count);
                if (billed) billedShare += share;
                var l = LearnFrom(new[] { key }, UsageModel.ResidentsVoice, UsageModel.GreetVoice);
                double line = l.LineMean > 0 ? l.LineMean : Math.Max(0.01, VoiceCost.Estimate(slot.Provider, model, SampleLine, seconds: 4));
                var (start, reset) = _voices.PeriodOf(slot);
                var span = TokenBudget.SpanOf(slot.LimitPer);
                // FB-59: what the account itself has left, when its month is the one counted (within a day of the same reset).
                double accountLeft = double.PositiveInfinity;
                if (billed && _voices.AccountNow(slot) is { } acc && _voices.AccountOf(slot) is { } read && Math.Abs((read.reset - reset).TotalDays) < 1.01)
                {
                    accountLeft = Math.Max(0, acc.limit - acc.used);
                    v.Accounts[key] = (acc.used, acc.limit);
                }
                v.Budgets.Add(new BudgetInput
                {
                    Key = key, Name = VoiceService.LedgerName(slot), Unit = VoiceCost.Unit(slot.Provider, model), Free = !billed,
                    Limit = billed ? Math.Max(0, slot.Limit) : 0, Used = billed ? _voices.UsedThisPeriod(slot) : 0, Reset = reset, AccountLeft = accountLeft,
                    YoursPerHour = UsageModel.Shrink(25 * line * share, l.Yours * l.Hours, l.Hours, 2),
                    PerStart = PerStart(l, 3 * line * share, voiceKinds, UsageModel.ResidentsVoice),
                    GreetPerHour = UsageModel.Shrink(greets && _cfg.SpeakGreetings.Value ? 6 * line * share : 0, l.Greet * l.Hours, l.Hours, 2),
                    LineCost = line, Chance = slot.Chance, HoursLeft = HoursLeft(reset, span),
                });
                v.Slots[key] = slot;
                v.Periods[key] = (start, reset);
            }
            // VoiceLimit: characters sent to paid voices, all together.
            if (_cfg.VoiceLimit.Value > 0 && _voiceBudget != null)
            {
                var span = TokenBudget.SpanOf(_cfg.VoiceLimitPer.Value) ?? TokenBudget.Span.Day;
                int voiceDay = Math.Clamp(_cfg.VoiceLimitResetDay.Value, 1, 31);
                var reset = TokenBudget.NextReset(now, span, voiceDay);
                var l = LearnFrom(new[] { UsageModel.VoiceCharsKey }, UsageModel.ResidentsVoice, UsageModel.GreetVoice);
                double line = l.LineMean > 0 ? l.LineMean : SampleLine.Length;
                v.Budgets.Add(new BudgetInput
                {
                    Key = UsageModel.VoiceCharsKey, Name = "All voices together", Unit = "characters", Limit = _cfg.VoiceLimit.Value,
                    Used = _voiceBudget.Used, Reset = reset,
                    YoursPerHour = UsageModel.Shrink(25 * line * billedShare, l.Yours * l.Hours, l.Hours, 2),
                    PerStart = PerStart(l, 3 * line * billedShare, voiceKinds, UsageModel.ResidentsVoice),
                    GreetPerHour = UsageModel.Shrink(greets && _cfg.SpeakGreetings.Value ? 6 * line * billedShare : 0, l.Greet * l.Hours, l.Hours, 2),
                    LineCost = line, HoursLeft = HoursLeft(reset, span),
                });
                v.Periods[UsageModel.VoiceCharsKey] = (TokenBudget.Start(now, span, voiceDay), reset);
            }
        }

        // FB-75: speech to text, when it has a limit: your own use only (residents never talk into your microphone).
        if (Npc.Features.VoiceInput && _cfg.SpeechToTextLimit.Value > 0 && _speechBudget != null)
        {
            var span = TokenBudget.SpanOf(_cfg.SpeechToTextLimitPer.Value) ?? TokenBudget.Span.Day;
            int speechDay = Math.Clamp(_cfg.SpeechToTextLimitResetDay.Value, 1, 31);
            var reset = TokenBudget.NextReset(now, span, speechDay);
            var l = LearnFrom(new[] { SpeechLedgerKey }, Array.Empty<string>(), Array.Empty<string>());
            v.Budgets.Add(new BudgetInput
            {
                Key = SpeechLedgerKey, Name = "Speech to text", Unit = "seconds", Limit = _cfg.SpeechToTextLimit.Value * 60L, YoursOnly = true,
                Used = _speechBudget.Used, Reset = reset,
                // Until it's learned: a minute of talking an hour (about a dozen short things said).
                YoursPerHour = UsageModel.Shrink(60, l.Yours * l.Hours, l.Hours, 2),
                LineCost = l.LineMean > 0 ? l.LineMean : 5, HoursLeft = HoursLeft(reset, span),
            });
            v.Periods[SpeechLedgerKey] = (TokenBudget.Start(now, span, speechDay), reset);
        }

        // Voyage: shown, not limited (it has no limit setting).
        if (VoyageOn && _voiceLedger != null)
        {
            string? key = _voiceLedger.Keys.FirstOrDefault(k => k.StartsWith("Voyage|", StringComparison.Ordinal));
            if (key != null) v.Voyage = (key, _voiceLedger.Of(key)?.Name ?? "Voyage", _voiceLedger.UsedSince(key, TokenBudget.Start(now, TokenBudget.Span.Month)));
        }

        // What stays as it is: your pins, settings changed since the plan, and levers with nothing to move.
        // Each voice provider's chance too (review F-0142: a chance you pinned or changed was overwritten by Apply).
        v.Kept = _usagePlan?.Kept(UsageModel.AllLevers(v.Slots.Keys), LeverNow) ?? new HashSet<string>();
        foreach (var l in UsageModel.Levers.Where(l => !LeverInUse(l))) v.Kept.Add(l);
        if (!chatter) foreach (var l in UsageModel.Levers.Where(l => l is not ("AiGreetings" or "SpeakGreetings" or "VoiceRing"))) v.Kept.Add(l);
        if (!_cfg.VoicesEnabled.Value || v.Now.VoiceRing <= 0) { v.Kept.Add("VoiceRing"); v.Kept.Add("SpeakGreetings"); }
        if (!greets) { v.Kept.Add("AiGreetings"); v.Kept.Add("SpeakGreetings"); }
        // A kind switched off in Features moves nothing (FB-45).
        if (!Npc.Features.PhoneCalls) v.Kept.Add("PhoneLines");
        if (!Npc.Features.Muttering) v.Kept.Add("ThinkLines");

        foreach (var b in v.Budgets) v.PerHourNow[b.Key] = UsageModel.PerHour(b, v.Now, v.Now, v.Rate, v.Mix);
        v.Best = UsageModel.Best(v.Budgets, v.Now, v.Kept, v.Rate, v.Mix, v.Headroom);
        double? dial = _dialPreview ?? _usagePlan?.Dial;
        v.Shown = dial is double d ? UsageModel.AtLevel(d, v.Budgets, v.Now, v.Kept, v.Rate, v.Mix, v.Headroom) : v.Best;
        return v;
    }

    /// <summary>"1.2M", "340k", "950".</summary>
    private static string Big(double n)
    {
        n = Math.Max(0, n);
        return n >= 9_950_000 ? (n / 1e6).ToString("0", CultureInfo.InvariantCulture) + "M"
            : n >= 999_500 ? (n / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + "M"
            : n >= 9_950 ? (n / 1e3).ToString("0", CultureInfo.InvariantCulture) + "k"
            : n >= 1000 ? (n / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "k"
            : n.ToString("0", CultureInfo.InvariantCulture);
    }

    private static string Day(DateTime t) => t.ToString("MMM d", CultureInfo.InvariantCulture);

    /// <summary>The plan's changes against the settings now: (lever, now, recommended), only for levers it may move.</summary>
    private static List<(string lever, string now, string next)> Changes(OptimizerView v, UsagePlan plan)
    {
        var list = new List<(string, string, string)>();
        foreach (var l in UsageModel.Levers)
        {
            if (v.Kept.Contains(l)) continue;
            string a = UsageModel.Show(l, v.Now), b = UsageModel.Show(l, plan.Settings);
            if (a != b) list.Add((l, a, b));
        }
        foreach (var (key, chance) in plan.Chances)
        {
            if (!v.Slots.TryGetValue(key, out var slot) || v.Kept.Contains(ChanceLever(key)) || Math.Abs(slot.Chance - chance) < 1) continue;
            list.Add((ChanceLever(key), slot.Chance.ToString("0.#", CultureInfo.InvariantCulture), chance.ToString("0.#", CultureInfo.InvariantCulture)));
        }
        return list;
    }

    private static string? ApplyOptimizer()
    {
        var v = Optimizer();
        var changes = Changes(v, v.Shown);
        if (changes.Count == 0) return "Your settings are already the plan's.";
        var before = new Dictionary<string, string>(StringComparer.Ordinal);
        var after = new Dictionary<string, string>(StringComparer.Ordinal);
        var keys = new List<string>();
        foreach (var (lever, now, next) in changes)
        {
            try
            {
                if (SetLever(lever, next) is { } key) keys.Add(key);
                before[lever] = now;
                after[lever] = LeverNow(lever); // as it reads back (a ring snapped, a chance rounded)
            }
            catch (Exception e) { Log.Warn($"Usage Optimizer: {lever} wasn't set ({e.GetType().Name})."); }
        }
        _usagePlan?.Record(before, after, DateTime.Now);
        foreach (var k in keys.Distinct()) AfterMenuChange(k);
        _usagePlan!.Dial = v.Shown.Activity;
        InvalidateOptimizer();
        Log.Info($"Usage Optimizer applied: {string.Join(", ", changes.Select(c => $"{c.lever} {c.now} -> {c.next}"))}.");
        return null;
    }

    private static string? UndoOptimizer()
    {
        if (_usagePlan is not { CanUndo: true } plan) return "There's nothing to undo.";
        var back = plan.UndoValues(LeverNow);
        var keys = new List<string>();
        foreach (var (lever, value) in back)
            try { if (SetLever(lever, value) is { } key) keys.Add(key); }
            catch (Exception e) { Log.Warn($"Usage Optimizer: {lever} wasn't put back ({e.GetType().Name})."); }
        plan.Forget();
        plan.Dial = null;
        foreach (var k in keys.Distinct()) AfterMenuChange(k);
        InvalidateOptimizer();
        return null;
    }

    // ------------------------------------------------------------------ the tab's rows (guided steps: the author's choice)

    private static Dictionary<string, object?> Row(string kind, string title, string hint, string help)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = kind, ["title"] = title, ["hint"] = hint, ["help"] = help };
        return d;
    }

    private static Dictionary<string, object?> Subsection(string title) => Row("subsection", title, "", "");

    /// <summary>What a budget comes to at its reset with the settings as they are now (a share of its limit).</summary>
    private static double ProjectedNow(OptimizerView v, BudgetInput b) => b.Limit > 0 ? (b.Used + b.HoursLeft * v.PerHourNow[b.Key]) / b.Limit : 0;

    /// <summary>How a budget is doing, in a few words.</summary>
    private static string StatusOf(OptimizerView v, BudgetInput b)
    {
        if (b.Free) return "free · no limit";
        double perHour = v.PerHourNow[b.Key];
        string account = v.Accounts.TryGetValue(b.Key, out var acc) ? $" · account {Big(acc.used)} of {Big(acc.limit)}" : "";
        if (!(b.Limit > 0)) return $"no limit · about {Big(perHour * v.HoursPerDay)} a day{account}";
        double atReset = b.Used + b.HoursLeft * perHour, target = (1 - v.Headroom) * b.Limit;
        double perDay = UsageModel.PerDayLeft(b, v.Headroom, DateTime.Now);
        string head = $"{Big(b.Used)} of {Big(b.Limit)} by {Day(b.Reset)} · ~{Big(perDay)} a day left{account}";
        if (atReset <= target) return head + " · on track";
        if (atReset <= b.Limit) return head + " · into your headroom";
        double hoursToLimit = perHour > 0 ? Math.Max(0, b.Limit - b.Used) / perHour : double.PositiveInfinity;
        var when = DateTime.Now.AddDays(hoursToLimit / Math.Max(0.25, v.HoursPerDay));
        return head + $" · runs out ~{Day(when < b.Reset ? when : b.Reset)}";
    }

    /// <summary>The status strip: one tile per budget (name, a meter, its % at the reset right after it, a line).</summary>
    private static Dictionary<string, object?> TilesRow()
    {
        var row = Row("tiles", "", "", "");
        row["tiles"] = (Func<IEnumerable?>)(() =>
        {
            var v = Optimizer();
            var list = new List<object>();
            foreach (var b in v.Budgets)
            {
                double[] meter = { 0, -1, 0 };
                string value = "—";
                bool warn = false;
                if (b.Limit > 0 && !b.Free)
                {
                    var (start, reset) = v.Periods[b.Key]; // a tile's bar is against your own limit
                    double elapsed = Math.Clamp((DateTime.Now - start).TotalSeconds / Math.Max(1, (reset - start).TotalSeconds), 0, 1);
                    double proj = ProjectedNow(v, b);
                    meter = new[] { Math.Min(1, b.Used / b.Limit), (1 - v.Headroom) * elapsed, proj };
                    value = $"{Math.Min(999, proj * 100):0}%";
                    warn = proj > 1 - v.Headroom;
                }
                list.Add(new Dictionary<string, object?> { ["title"] = b.Ai ? "AI" : Short(b.Name), ["value"] = value, ["caption"] = StatusOf(v, b), ["meter"] = meter, ["warn"] = warn });
            }
            if (v.Voyage is { } y)
                list.Add(new Dictionary<string, object?> { ["title"] = "Voyage memory search", ["value"] = "—", ["caption"] = $"{Big(y.used)} tokens this month", ["meter"] = new double[] { 0, -1, 0 }, ["warn"] = false });
            return list;
        });
        row["helpLive"] = (Func<string?>)(() =>
        {
            var v = Optimizer();
            var parts = v.Budgets.Select(b =>
            {
                double residents = Math.Max(0, v.PerHourNow[b.Key] - b.YoursPerHour - b.GreetPerHour);
                return $"{(b.Ai ? "AI" : Short(b.Name))}: {StatusOf(v, b)} (an hour of play: {Big(b.YoursPerHour)} yours, {Big(residents)} residents', {Big(b.GreetPerHour)} greetings)";
            });
            return string.Join(". ", parts) + $". You play about {v.HoursPerDay:0.#} h a day. On each bar the tick is where you'd be by now with your headroom kept, the light part where you'll be at the reset with your settings now.";
        });
        return row;
    }

    /// <summary>A step's heading: its number, title, line, chip and whether it's done or the one you're on.</summary>
    private static Dictionary<string, object?> StepRow(int number, string title, string line, Func<(string chip, string tone)> chip, Func<string> state, Func<string?>? lineLive = null)
    {
        var row = Row("step", title, line, "");
        row["number"] = number;
        row["chip"] = (Func<string?>)(() => chip().chip);
        row["chipTone"] = (Func<string?>)(() => chip().tone);
        row["state"] = (Func<string?>)(() => state());
        if (lineLive != null) row["hintLive"] = lineLive;
        return row;
    }

    /// <summary>
    /// The tab's rows. Whatever goes wrong building them, the rows built so far still show, with a note saying what's
    /// missing (it was empty in game once: the menu drops every row when the page throws).
    /// </summary>
    private static List<object> OptimizerRowsSafe()
    {
        var rows = new List<object>();
        try { OptimizerRows(rows); }
        catch (Exception e)
        {
            Log.Warn($"Usage Optimizer: part of the tab couldn't be shown ({e.GetType().Name}: {e.Message}) at {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
            rows.Add(Row("note", "Part of this tab couldn't be shown", e.GetType().Name, "Something went wrong building the rest of this tab. LogOutput.log says where; send it to the mod's author."));
        }
        return rows;
    }

    private static void OptimizerRows(List<object> rows)
    {
        if (_voiceLedger == null || _usagePlan == null)
        {
            rows.Add(Row("note", "Not available", "The usage record couldn't be opened", "The usage record (voice_ledger.json) or the plan (usage_plan.json) couldn't be opened: see LogOutput.log."));
            return;
        }
        var v0 = Optimizer();
        rows.Add(TilesRow());

        // ---- Step 1: your limits.
        rows.Add(StepRow(1, "Your limits", "What each may use, and for how long (a day, a week or a month). 0 means no limit.",
            () => Optimizer().Best.Limited ? ("set", "ok") : ("needs a limit", "warn"),
            () => Optimizer().Best.Limited ? "done" : "now"));
        rows.Add(Subsection("AI · " + (_cfg.Model.Value.Trim().Length > 0 ? Ai.Redact.Value(_cfg.Model.Value) : "your model")));
        var aiLimit = SettingsCatalog.ToContract(new SettingControl(ControlKind.Number, 0, 2_000_000_000));
        aiLimit["title"] = "Token limit";
        aiLimit["hint"] = "Tokens per period";
        aiLimit["help"] = "TokenLimit: the most tokens the mod may use with your AI per period. Once it's reached, residents go quiet and your own " +
                          "chats wait until it starts again. The plan keeps everything under it with your headroom left.";
        aiLimit["type"] = "double";
        aiLimit["get"] = (Func<object?>)(() => (double)_cfg.TokenLimit.Value);
        aiLimit["set"] = (Func<object, string?>)(x =>
        {
            _cfg.TokenLimit.Value = (int)Math.Clamp(Math.Round(Convert.ToDouble(x, CultureInfo.InvariantCulture)), 0, int.MaxValue);
            AfterMenuChange("TokenLimit");
            InvalidateOptimizer();
            return null;
        });
        rows.Add(aiLimit);
        var aiPer = SettingsCatalog.ToContract(new SettingControl(ControlKind.Choice, Choices: new[] { "Day", "Week", "Month" }));
        aiPer["title"] = "Duration";
        aiPer["hint"] = "How long each limit lasts";
        aiPer["help"] = "TokenLimitPer: day (from midnight), week (from Monday) or month (the 1st). Hour is in the AI tab.";
        aiPer["type"] = "string";
        aiPer["get"] = (Func<object?>)(() => _cfg.TokenLimitPer.Value);
        aiPer["set"] = (Func<object, string?>)(x =>
        {
            _cfg.TokenLimitPer.Value = x as string ?? "Day";
            AfterMenuChange("TokenLimitPer");
            InvalidateOptimizer();
            return null;
        });
        rows.Add(aiPer);
        var aiDayRow = SettingsCatalog.ToContract(new SettingControl(ControlKind.Slider, 1, 31, 1));
        aiDayRow["title"] = "Resets on day";
        aiDayRow["hint"] = "Your provider's billing day";
        aiDayRow["help"] = "TokenLimitResetDay: with a monthly limit, the day of the month it starts again (past a short month's end, its last day), " +
                           "at midnight by your PC's calendar, so the plan knows how many days are left.";
        aiDayRow["type"] = "double";
        aiDayRow["get"] = (Func<object?>)(() => (double)Math.Clamp(_cfg.TokenLimitResetDay.Value, 1, 31));
        aiDayRow["set"] = (Func<object, string?>)(x =>
        {
            _cfg.TokenLimitResetDay.Value = (int)Math.Clamp(Math.Round(Convert.ToDouble(x, CultureInfo.InvariantCulture)), 1, 31);
            AfterMenuChange("TokenLimitResetDay");
            InvalidateOptimizer();
            return null;
        });
        aiDayRow["visible"] = (Func<bool>)(() => (TokenBudget.SpanOf(_cfg.TokenLimitPer.Value) ?? TokenBudget.Span.Day) == TokenBudget.Span.Month);
        rows.Add(aiDayRow);
        var yours = Row("note", "Your conversations", "", "Your own conversations are never made smaller by the plan: what they cost comes off each limit first. " +
                                                          "RecentMessagesSent and MaxReplyTokens (AI tab) set how much each reply sends and may say.");
        yours["get"] = (Func<object?>)(() => $"about {Big(Optimizer().Budgets.First(b => b.Ai).YoursPerHour)} an hour");
        yours["hintLive"] = (Func<string?>)(() => Optimizer().ReplyTokens is > 0 and var t ? $"A reply costs about {Big(t)} tokens; never made smaller" : "Never made smaller by the plan");
        rows.Add(yours);
        if (_cfg.BackgroundModel.Value.Trim().Length == 0)
            rows.Add(Row("note", "Background model", "None: residents use your main model", "BackgroundModel (AI tab): a cheaper, faster model for residents' talk, diaries and " +
                                                                                           "memories. Your own conversations keep your main model. It can cut residents' cost a lot."));
        if (!_cfg.PromptCaching.Value)
            rows.Add(Row("note", "Prompt caching", "Off", "PromptCaching (AI tab): with providers that support it, the unchanging part of each request is sent " +
                                                         "cheaper after the first time. Off, every request pays in full."));
        if (_cfg.VoicesEnabled.Value)
        {
            var all = ProviderLines.Read(_cfg.ProviderLinesNow);
            for (int k = 0; k < all.Count; k++)
            {
                var e = all[k];
                if (!e.On) continue;
                string name = ProviderLines.Name(e.Provider);
                bool billed = VoiceService.Billed(new VoiceSlot { Provider = e.Provider, Url = e.Endpoint });
                string model = e.Model.Length > 0 ? Ai.Redact.Value(e.Model) : VoiceService.ModelOf(new VoiceSlot { Provider = e.Provider });
                rows.Add(Subsection(name + (model.Length > 0 ? " · " + model : "")));
                if (e.Provider != VoiceProvider.Windows && !HasVoiceKey(e) && e.Provider != VoiceProvider.OpenAICompatible) rows.Add(KeyRow(k, e, name));
                if (!billed) rows.Add(Row("note", "Free", "Runs on your own PC: no limit needed", "This provider costs nothing, so it has no limit; the plan leaves it as it is."));
                else foreach (var r in LimitRows(k, e, name))
                {
                    // Read with TryGetValue: a limit row has set but no click, the plan's button click but no set (the tab was empty
                    // in game once: a KeyNotFoundException here took every row with it).
                    if (r is not Dictionary<string, object?> d || d.TryGetValue("title", out var title) && title is string t && t.EndsWith(" use", StringComparison.Ordinal)) continue;
                    d.Remove("after");
                    // Under the provider's own heading its name needn't be repeated.
                    if (d.TryGetValue("title", out var tt) && tt is string ts)
                        d["title"] = ts == $"{name} limit" ? "Limit" : ts == $"{name} limit per" ? "Duration" : ts;
                    if (d.TryGetValue("set", out var s0) && s0 is Func<object, string?> set) d["set"] = (Func<object, string?>)(x => { var why = set(x); InvalidateOptimizer(); return why; });
                    if (d.TryGetValue("click", out var c0) && c0 is Func<string?> click) d["click"] = (Func<string?>)(() => { var why = click(); InvalidateOptimizer(); return why; });
                    rows.Add(d);
                }
                var rm = RemoveRow(k, name);
                rm.Remove("after");
                Retitle(rm, name, e);
                if (rm["click"] is Func<string?> remove) rm["click"] = (Func<string?>)(() => { var why = remove(); InvalidateOptimizer(); return why; });
                rows.Add(rm);
            }
        }
        rows.Add(Subsection("For all of them"));
        if (_cfg.VoicesEnabled.Value)
        {
            var voiceLimit = SettingsCatalog.ToContract(new SettingControl(ControlKind.Number, 0, 2_000_000_000));
            voiceLimit["title"] = "All voices together";
            voiceLimit["hint"] = "Characters to paid voices (0 = none)";
            voiceLimit["help"] = "VoiceLimit: the most characters sent to every paid voice provider together, per VoiceLimitPer (in the Voices tab). " +
                                 "Each provider's own limit above is usually what you want; this one is a single cap on them all.";
            voiceLimit["type"] = "double";
            voiceLimit["get"] = (Func<object?>)(() => (double)_cfg.VoiceLimit.Value);
            voiceLimit["set"] = (Func<object, string?>)(x =>
            {
                _cfg.VoiceLimit.Value = (int)Math.Clamp(Math.Round(Convert.ToDouble(x, CultureInfo.InvariantCulture)), 0, int.MaxValue);
                AfterMenuChange("VoiceLimit");
                InvalidateOptimizer();
                return null;
            });
            voiceLimit["rebuild"] = true;
            rows.Add(voiceLimit);
            var voiceDay = SettingsCatalog.ToContract(new SettingControl(ControlKind.Slider, 1, 31, 1));
            voiceDay["title"] = "All voices: resets on day";
            voiceDay["hint"] = "With a monthly VoiceLimit";
            voiceDay["help"] = "VoiceLimitResetDay: the day of the month VoiceLimit starts again (past a short month's end, its last day), at midnight.";
            voiceDay["type"] = "double";
            voiceDay["get"] = (Func<object?>)(() => (double)Math.Clamp(_cfg.VoiceLimitResetDay.Value, 1, 31));
            voiceDay["set"] = (Func<object, string?>)(x =>
            {
                _cfg.VoiceLimitResetDay.Value = (int)Math.Clamp(Math.Round(Convert.ToDouble(x, CultureInfo.InvariantCulture)), 1, 31);
                AfterMenuChange("VoiceLimitResetDay");
                InvalidateOptimizer();
                return null;
            });
            voiceDay["visible"] = (Func<bool>)(() => _cfg.VoiceLimit.Value > 0 && (TokenBudget.SpanOf(_cfg.VoiceLimitPer.Value) ?? TokenBudget.Span.Day) == TokenBudget.Span.Month);
            rows.Add(voiceDay);
        }
        if (Npc.Features.VoiceInput)
        {
            // FB-75: speech to text has a limit like the others (your own use: it never moves residents' talk).
            var speech = SettingsCatalog.ToContract(new SettingControl(ControlKind.Slider, 0, 600, 5, Unit: "min"));
            speech["title"] = "Speech to text";
            speech["hint"] = "Minutes of your voice input (0 = none)";
            speech["help"] = "SpeechToTextLimit: the most minutes of what you say sent to the speech-to-text service per " +
                             "SpeechToTextLimitPer (in the Voice Input tab). Past it, you type until it starts again.";
            speech["type"] = "double";
            speech["get"] = (Func<object?>)(() => (double)Math.Max(0, _cfg.SpeechToTextLimit.Value));
            speech["set"] = (Func<object, string?>)(x =>
            {
                _cfg.SpeechToTextLimit.Value = (int)Math.Clamp(Math.Round(Convert.ToDouble(x, CultureInfo.InvariantCulture)), 0, 600);
                AfterMenuChange("SpeechToTextLimit");
                InvalidateOptimizer();
                return null;
            });
            speech["rebuild"] = true;
            rows.Add(speech);
            var speechDay = SettingsCatalog.ToContract(new SettingControl(ControlKind.Slider, 1, 31, 1));
            speechDay["title"] = "Speech to text: resets on day";
            speechDay["hint"] = "With a monthly SpeechToTextLimit";
            speechDay["help"] = "SpeechToTextLimitResetDay: the day of the month SpeechToTextLimit starts again (past a short month's end, its last day), " +
                                "at midnight. Your speech service's billing day (often the same as the AI's).";
            speechDay["type"] = "double";
            speechDay["get"] = (Func<object?>)(() => (double)Math.Clamp(_cfg.SpeechToTextLimitResetDay.Value, 1, 31));
            speechDay["set"] = (Func<object, string?>)(x =>
            {
                _cfg.SpeechToTextLimitResetDay.Value = (int)Math.Clamp(Math.Round(Convert.ToDouble(x, CultureInfo.InvariantCulture)), 1, 31);
                AfterMenuChange("SpeechToTextLimitResetDay");
                InvalidateOptimizer();
                return null;
            });
            speechDay["visible"] = (Func<bool>)(() => _cfg.SpeechToTextLimit.Value > 0 && (TokenBudget.SpanOf(_cfg.SpeechToTextLimitPer.Value) ?? TokenBudget.Span.Day) == TokenBudget.Span.Month);
            rows.Add(speechDay);
        }
        var headroom = SettingsCatalog.ToContract(new SettingControl(ControlKind.Slider, 0, 50, 5, Unit: "%"));
        headroom["title"] = "Headroom";
        headroom["hint"] = "Kept back for busy days";
        headroom["help"] = "How much of each limit the plan leaves unused at its reset, for a long session or a chatty evening. 10% plans to end " +
                           "each period at 90%.";
        headroom["type"] = "double";
        headroom["get"] = (Func<object?>)(() => Math.Round(_usagePlan.Headroom * 100));
        headroom["set"] = (Func<object, string?>)(x => { _usagePlan.Headroom = Convert.ToDouble(x, CultureInfo.InvariantCulture) / 100; InvalidateOptimizer(); return null; });
        rows.Add(headroom);
        var hours = SettingsCatalog.ToContract(new SettingControl(ControlKind.Slider, 0, 16, 0.5, Unit: "h"));
        hours["title"] = "Play a day";
        hours["hint"] = "0 = learned from your play";
        hours["hintLive"] = (Func<string?>)(() => _usagePlan.HoursPerDay > 0 ? "As you set it (0 = learned)" : $"0 = learned (about {Optimizer().HoursPerDay:0.#} h now)");
        hours["help"] = "How many hours a day you play, to spread each limit over. 0 learns it from your play over the last two weeks.";
        hours["helpLive"] = (Func<string?>)(() =>
        {
            var v = Optimizer();
            return $"How many hours a day you play, to spread each limit over. 0 learns it: {v.LearnedHours:0.#} h a day over the last two weeks " +
                   $"({v.DaysPlayed} days played). Costs are learned from {v.HoursSeen:0} hour{(v.HoursSeen == 1 ? "" : "s")} of play so far " +
                   $"(estimates {UsageModel.Confidence(v.HoursSeen)}; sure after about 8).";
        });
        hours["type"] = "double";
        hours["get"] = (Func<object?>)(() => _usagePlan.HoursPerDay);
        hours["set"] = (Func<object, string?>)(x => { _usagePlan.HoursPerDay = Convert.ToDouble(x, CultureInfo.InvariantCulture); InvalidateOptimizer(); return null; });
        rows.Add(hours);
        if (_cfg.VoicesEnabled.Value)
        {
            var add = AddProviderRow();
            add.Remove("after");
            rows.Add(add);
        }

        // ---- Step 2: how lively.
        rows.Add(StepRow(2, "How lively", "Move it and watch each limit at its reset.",
            () =>
            {
                var v = Optimizer();
                return !v.Shown.Limited ? ("no limit: as you like", "") : v.Shown.Feasible ? ("fits your limits", "ok") : ("over a limit", "warn");
            },
            () => Changes(Optimizer(), Optimizer().Shown).Count == 0 ? "done" : "now",
            () =>
            {
                var v = Optimizer();
                if (!v.Best.Limited) return "No limit set: 100% is your settings as they are. Move it to plan for less.";
                return !v.Best.Feasible ? "Your limits don't fit even the quietest level: see the footer." : $"Your limits allow up to {v.Best.Activity:P0}. Move it and watch each limit at its reset.";
            }));
        var dial = SettingsCatalog.ToContract(new SettingControl(ControlKind.Slider, 0, 100, 1, Unit: "%"));
        dial["title"] = "Residents' talk";
        dial["hint"] = "";
        dial["hintLive"] = (Func<string?>)(() =>
        {
            var v = Optimizer();
            var parts = v.Budgets.Where(b => b.Capped).Select(b => $"{(b.Ai ? "AI" : Short(b.Name))} {v.Shown.AtReset[b.Key]:P0}").ToList();
            // FB-51: with no limit the dial has nothing to keep within, so it stays at 100%: said here, not just snapped back.
            return parts.Count == 0 ? $"No limit set · about {string.Join(", ", v.Budgets.Where(b => !b.Free).Select(b => $"{Big(UsageModel.PerHour(b, v.Shown.Settings, v.Now, v.Rate, v.Mix) * v.HoursPerDay)} {(b.Ai ? "AI tokens" : Short(b.Name))}"))} a day"
                                    : string.Join(" · ", parts) + " at their resets";
        });
        dial["help"] = "How lively the city is: at 100%, the liveliest settings; lower, residents talk less (shorter first, then less often, then " +
                       "fewer at once). Voices give way first where only a voice limit is short. Move it to see what each limit comes to; Apply writes it.";
        dial["helpLive"] = (Func<string?>)(() => PlanSummary(Optimizer()));
        dial["type"] = "double";
        dial["immediate"] = true; // a what-if: it never waits for Save
        dial["get"] = (Func<object?>)(() => Math.Round(Optimizer().Shown.Activity * 100));
        dial["preview"] = (Action<object?>)(x =>
        {
            double? next = x is double d ? Math.Clamp(d / 100, 0, 1) : null;
            if (next != _dialPreview) _dialPreview = next;
        });
        dial["set"] = (Func<object, string?>)(x => { _usagePlan.Dial = Convert.ToDouble(x, CultureInfo.InvariantCulture) / 100; _dialPreview = null; InvalidateOptimizer(); return null; });
        rows.Add(dial);
        rows.Add(new Dictionary<string, object?>
        {
            ["kind"] = "buttons", ["title"] = "", ["help"] = "Moves the dial to the liveliest level that keeps every limit under its target (your headroom kept).",
            ["buttons"] = new List<object>
            {
                new Dictionary<string, object?> { ["label"] = "Find the best level", ["click"] = (Func<string?>)(() => { _usagePlan.Dial = null; _dialPreview = null; InvalidateOptimizer(); return null; }) },
            },
        });
        // FB-59: what's been learned, a limit, a reset or a switch moved the best level well away from the one you applied:
        // a new plan is offered (nothing changes until you take it and Apply).
        var fresh = Row("note", "A new plan is ready", "", "Since you applied your plan, what the mod has learned of your play, a limit, a reset day or a " +
                                                             "switch you turned off moved the best level. Use it, then Apply; or keep yours.");
        fresh["hintLive"] = (Func<string?>)(() => { var v = Optimizer(); return _usagePlan.Dial is double d ? $"Best now {v.Best.Activity:P0}, yours {d:P0}" : ""; });
        fresh["visible"] = (Func<bool>)(() => NewPlanReady(Optimizer()));
        rows.Add(fresh);
        rows.Add(new Dictionary<string, object?>
        {
            ["kind"] = "buttons", ["title"] = "", ["help"] = "Moves the dial to the best level for how things stand now; Apply writes it.",
            ["visible"] = (Func<bool>)(() => NewPlanReady(Optimizer())),
            ["buttons"] = new List<object>
            {
                new Dictionary<string, object?> { ["label"] = "Use the new plan", ["main"] = true, ["click"] = (Func<string?>)(() => { _usagePlan.Dial = null; _dialPreview = null; InvalidateOptimizer(); return null; }) },
            },
        });

        // ---- Step 3: review and apply.
        rows.Add(StepRow(3, "Review and apply", "What Apply writes. Pin one to keep it as it is.",
            () =>
            {
                var v = Optimizer();
                int n = Changes(v, v.Shown).Count;
                if (!v.Shown.Limited && n == 0) return ("no limit: as you set it", "");
                return n == 0 ? ("up to date", "ok") : ($"{n} change{(n == 1 ? "" : "s")}", "warn");
            },
            () =>
            {
                var v = Optimizer();
                return Changes(v, v.Shown).Count == 0 ? "done" : "now";
            }));
        rows.Add(new Dictionary<string, object?> { ["kind"] = "changes", ["title"] = "Setting", ["fromLabel"] = "Now", ["toLabel"] = "Recommended", ["endLabel"] = "Pin" });
        // The levers listed: "Nothing to change" asks about exactly these (review: asking about more could leave the step blank).
        var listed = new List<string>();
        // FB-51: nothing would change (after an Apply, say): the plan as it stands is still shown, each setting at its value.
        bool NothingToChange() { var v = Optimizer(); return !listed.Any(l => ChangeShown(v, l)); } // with a limit or not (FB-59)
        foreach (var lever in UsageModel.Levers.Where(LeverInUse))
        {
            listed.Add(lever);
            rows.Add(ChangeRow(lever, LeverTitle(lever), LeverHelp(lever), NothingToChange));
        }
        foreach (var b in v0.Budgets.Where(b => !b.Ai && v0.Slots.ContainsKey(b.Key) && b.Limit > 0 && !b.Free))
        {
            listed.Add(ChanceLever(b.Key));
            rows.Add(ChangeRow(ChanceLever(b.Key), $"{Short(b.Name)} chance", showAnyway: NothingToChange, help:
                "How often a newly met person gets this provider's voices. With more than one limited provider, the plan shares new people out by " +
                "what each has left for the play-hours to its reset and what a line costs there. People keep the voice they have."));
        }
        var none = Row("note", "Nothing to change", "Your settings already match this level: your plan as it stands is below",
            "Every setting the plan moves is already where this level puts it. Each is listed with its value; step one with - and + (or left " +
            "and right) to set it yourself, which pins it, or pin it to keep it as it is.");
        none["hintLive"] = (Func<string?>)(() => Optimizer().Best.Limited ? "Your settings already match this level: your plan as it stands is below"
                                                                            : "No limit set: these are your settings as they are");
        none["visible"] = (Func<bool>)(() => { var v = Optimizer(); return !listed.Any(l => ChangeShown(v, l)); });
        rows.Add(none);
        rows.Add(new Dictionary<string, object?>
        {
            ["kind"] = "buttons", ["title"] = "",
            ["helpLive"] = (Func<string?>)(() =>
            {
                var v = Optimizer();
                var ch = Changes(v, v.Shown);
                return ch.Count == 0 ? "Your settings are already this plan's." :
                    "Apply writes: " + string.Join("; ", ch.Select(c => $"{ChangeTitle(c.lever)} {c.now} → {c.next}")) +
                    ". A setting you change afterwards is tagged CHANGED and kept as you set it. Undo puts back what the last Apply changed.";
            }),
            ["buttons"] = new List<object>
            {
                new Dictionary<string, object?> { ["label"] = "Undo last plan", ["click"] = (Func<string?>)UndoOptimizer, ["visible"] = (Func<bool>)(() => _usagePlan.CanUndo) },
                new Dictionary<string, object?>
                {
                    ["label"] = "Apply", ["main"] = true, ["click"] = (Func<string?>)ApplyOptimizer,
                    // FB-51: greyed out while there's nothing to apply (no limit, or the settings are already the plan's): menu 0.4.2.
                    ["enabled"] = (Func<bool>)(() => Changes(Optimizer(), Optimizer().Shown).Count > 0), // FB-59: with a limit or not
                    ["labelLive"] = (Func<string?>)(() => { var v = Optimizer(); int n = Changes(v, v.Shown).Count; return n == 0 ? "Apply" : $"Apply {n} change{(n == 1 ? "" : "s")}"; }),
                },
            },
        });
        rows.Add(Subsection("Between plans"));
        rows.AddRange(PacerRows());
    }

    /// <summary>A lever the plan moves is in the review list: when it would change, or when you've kept it (pinned, or changed since the plan).</summary>
    private static bool ChangeShown(OptimizerView v, string lever)
    {
        if (_usagePlan?.Pinned(lever) == true || _usagePlan?.Changed(lever, LeverNow(lever)) == true) return true;
        if (v.Kept.Contains(lever)) return false;
        return Recommended(v, lever) is { } rec && rec != LeverNow(lever);
    }

    /// <summary>What the shown plan would set a lever to (null: it doesn't set it).</summary>
    private static string? Recommended(OptimizerView v, string lever)
    {
        if (lever.StartsWith("chance:", StringComparison.Ordinal))
            return v.Shown.Chances.TryGetValue(lever.Substring(7), out var c) ? c.ToString("0.#", CultureInfo.InvariantCulture) : null;
        return UsageModel.Show(lever, v.Shown.Settings);
    }

    /// <summary>One line of the review: the setting, now -> recommended (or "kept"), and its pin.</summary>
    private static Dictionary<string, object?> ChangeRow(string lever, string title, string help, Func<bool>? showAnyway = null)
    {
        var row = Row("change", title, "", help + " Pin it to keep it as it is (unpin it to let the plan move it).");
        row["from"] = (Func<string?>)(() => LeverNow(lever));
        row["to"] = (Func<string?>)(() =>
        {
            var v = Optimizer();
            if (_usagePlan?.Pinned(lever) == true || _usagePlan?.Changed(lever, LeverNow(lever)) == true) return "kept";
            return Recommended(v, lever) ?? LeverNow(lever);
        });
        row["toTone"] = (Func<string?>)(() =>
        {
            if (_usagePlan?.Pinned(lever) == true || _usagePlan?.Changed(lever, LeverNow(lever)) == true) return "kept";
            return Recommended(Optimizer(), lever) == LeverNow(lever) ? "same" : "";
        });
        row["visible"] = (Func<bool>)(() => ChangeShown(Optimizer(), lever) || (showAnyway?.Invoke() == true && Recommended(Optimizer(), lever) != null));
        row["pinned"] = (Func<bool>)(() => _usagePlan?.Pinned(lever) == true);
        row["pin"] = (Action<bool>)(on => { _usagePlan?.Pin(lever, on); InvalidateOptimizer(); });
        row["badge"] = (Func<string?>)(() => _usagePlan?.Changed(lever, LeverNow(lever)) == true ? "changed" : null);
        row["badgeHelp"] = (Func<string?>)(() => ChangedHelp(lever));
        // FB-59: its value set right here (- and +, or left and right): it's pinned, and the rest is planned around it.
        row["step"] = (Func<string?, int, string?>)((now, dx) => StepLever(lever, now ?? LeverNow(lever), dx));
        row["set"] = (Func<object, string?>)(x =>
        {
            if (x is not string value) return null;
            string before = LeverNow(lever);
            if (value == before) return null; // already so: nothing to do, nothing pinned
            string? key;
            try { key = SetLever(lever, value); }
            catch (Exception e) { return $"That couldn't be set ({e.GetType().Name})."; }
            if (LeverNow(lever) == before) return "That couldn't be changed here: change it in its own tab."; // nothing moved: not pinned (F59-4)
            _usagePlan?.Pin(lever, true);
            if (key != null) AfterMenuChange(key);
            InvalidateOptimizer();
            return null;
        });
        return row;
    }

    /// <summary>A lever's value a step down or up (as the setting writes it), within its setting's range; null at its end.</summary>
    private static string? StepLever(string lever, string now, int dx)
    {
        int dir = Math.Sign(dx);
        if (dir == 0) return null;
        if (lever.StartsWith("chance:", StringComparison.Ordinal))
        {
            double.TryParse(now, NumberStyles.Float, CultureInfo.InvariantCulture, out double c);
            // The next multiple of 5 that way (12 -> 15 or 10; review F59-4: it skipped 10).
            double next = Math.Clamp(dir > 0 ? Math.Floor(c / 5 + 1e-9) * 5 + 5 : Math.Ceiling(c / 5 - 1e-9) * 5 - 5, 0, 100);
            return Math.Abs(next - c) < 1e-9 ? null : next.ToString("0.#", CultureInfo.InvariantCulture);
        }
        switch (lever)
        {
            case "AiOrderTalk":
            case "AiGreetings":
            case "SpeakGreetings":
                return now == "on" ? "off" : "on";
            case "Every":
            {
                double secs = ChatterSettings.Interval(now) ?? 15;
                var steps = UsageModel.EverySteps;
                // The next choice that way from where it is, on a choice or between two (review F59-4).
                int j = dir > 0 ? Array.FindIndex(steps, x => x > secs + 0.5) : Array.FindLastIndex(steps, x => x < secs - 0.5);
                return j < 0 ? null : UsageModel.EveryText(steps[j]);
            }
            case "VoiceRing":
            {
                float.TryParse(now.Replace(" m", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out float m);
                var r = CurrentRings().Clamped();
                float next = Rings.Snap(Mathf.Clamp(m + dir * 1f, 0f, r.Chatter));
                return Math.Abs(next - m) < 0.01f ? null : Rings.M(next);
            }
        }
        var entry = LeverEntry(lever);
        if (entry == null || !double.TryParse(now, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return null;
        var control = SettingsCatalog.For(entry.Definition.Section, entry.Definition.Key, entry.SettingType);
        double stepped = control.Stepped(n, dir);
        return Math.Abs(stepped - n) < 1e-9 ? null : stepped.ToString("0", CultureInfo.InvariantCulture);
    }

    private static bool _newPlanShown;

    /// <summary>
    /// FB-59: the best level now is at least 5 points from the one you applied (and the costs are known well enough to say so);
    /// once shown, it stays until it's back within 3 (review F59-5: it flickered at the edge).
    /// </summary>
    private static bool NewPlanReady(OptimizerView v)
    {
        bool can = _usagePlan?.Dial is double && _dialPreview == null && v.Best.Feasible && UsageModel.Confidence(v.HoursSeen) != "low";
        double gap = _usagePlan?.Dial is double d ? Math.Abs(v.Best.Activity - d) : 0;
        _newPlanShown = can && (gap >= 0.05 || (_newPlanShown && gap >= 0.03));
        return _newPlanShown;
    }

    private static string LeverHelp(string lever) => lever switch
    {
        "PerHour" => "ChatterPerHour: the most conversations residents start in an hour, of every kind together. The plan's last step down before order talk and greetings.",
        "Every" => "DynamicEvery: how often something new may start near you. Moved after the lines each.",
        "MaxNpcs" => "DynamicMaxNpcs: the most people talking near you at once.",
        "StartsEach" => "DynamicStartsEach: how many new things may start each time.",
        "Lines" => "The most lines each person says in a conversation. The first thing to give way: shorter talk keeps the city lively for less.",
        "PhoneLines" => "The most lines each side of a phone call says.",
        "ThinkLines" => "The most lines someone mutters to themselves in one go.",
        "VoiceRing" => "How far away a resident's line is still voiced (the voices ring). Fewer distant lines voiced spends less on voices; " +
                       "everything nearer sounds the same. The first thing to give way when only a voice limit is short.",
        "AiOrderTalk" => "AiOrderTalk: shop and restaurant orders written by the AI. Off, they use written lines (no AI).",
        "AiGreetings" => "AiGreetingLines: street greetings written by the AI. Off, stock lines are used (no AI).",
        "SpeakGreetings" => "SpeakGreetings: street greetings are spoken too. Off, they're shown only.",
        _ => "",
    } + " Change it in its own tab whenever you like.";

    private static string Short(string name)
    {
        int b = name.IndexOf(" (", StringComparison.Ordinal);
        return b > 0 ? name.Substring(0, b) : name;
    }

    private static string ChangeTitle(string lever) =>
        lever.StartsWith("chance:", StringComparison.Ordinal) && Optimizer().Budgets.FirstOrDefault(b => b.Key == lever.Substring(7)) is { } b
            ? Short(b.Name) + " chance" : LeverTitle(lever).ToLowerInvariant();

    private static string ChangedHelp(string lever) =>
        _usagePlan?.AppliedValue(lever) is { } was
            ? $"Changed after the plan set it to {was}: it's kept as you set it until you Apply again or Undo."
            : "";

    /// <summary>The footer's account of the plan shown: each limit at its reset, what gives way, and anything it couldn't do.</summary>
    private static string PlanSummary(OptimizerView v)
    {
        var p = v.Shown;
        if (!p.Limited) return string.Join(" ", p.Notes);
        var parts = v.Budgets.Where(b => b.Capped)
            .Select(b => $"{Short(b.Name)} ends at {p.AtReset[b.Key]:P0} on {Day(b.Reset)}" + (b.Key == p.Binding ? " (the one that sets the level)" : "")).ToList();
        string s = $"At {p.Activity:P0}: {string.Join("; ", parts)}.";
        if (p.Voice < 1) s += $" Voices reach {p.Voice:P0} of your voices ring's area and greetings{(p.Settings.SpeakGreetings ? " stay spoken" : " go quiet")}.";
        var mine = v.Kept.Where(l => LeverInUse(l) && UsageModel.Levers.Contains(l) && (_usagePlan?.Pinned(l) == true || _usagePlan?.Changed(l, LeverNow(l)) == true)).ToList();
        if (mine.Count > 0) s += $" Kept as you set them: {string.Join(", ", mine.Select(l => LeverTitle(l).ToLowerInvariant()))}.";
        if (p.Notes.Count > 0) s += " " + string.Join(" ", p.Notes);
        if (UsageModel.Confidence(v.HoursSeen) == "low") s += " The costs are still being learned: expect this to move after a few hours of play.";
        return s;
    }

    /// <summary>For a setting in its own tab that the plan moves: its "changed" tag (FB-41).</summary>
    private static void AddLeverBadge(string key, Dictionary<string, object?> hint)
    {
        hint["badge"] = (Func<string?>)(() => LeverOfKey(key) is { } l && _usagePlan?.Changed(l, LeverNow(l)) == true ? "changed" : null);
        hint["badgeHelp"] = (Func<string?>)(() => LeverOfKey(key) is { } l ? ChangedHelp(l) : null);
    }

    // ------------------------------------------------------------------ stage 4: the pacer

    private static readonly Pacer _pacer = new();
    /// <summary>Pacing paused until the game is closed.</summary>
    private static bool _pacePaused;
    /// <summary>What would start an hour at your settings (the pacer's base), as last worked out.</summary>
    private static double _wouldStart = double.PositiveInfinity;

    private static bool PacingOn => _usagePlan?.Pacing == true && !_pacePaused;

    /// <summary>ChatterPerHour as the chatter uses it: yours, or the pacer's share of what would start while a limit needs it.</summary>
    private static int PacedPerHour()
    {
        int mine = _cfg.ChatterPerHour.Value;
        return PacingOn ? _pacer.Cap(mine, _wouldStart) : mine;
    }

    /// <summary>A play-minute: the pacer looks at every limited budget again (never your own conversations).</summary>
    private static void PaceMinute()
    {
        if (_usagePlan == null || _voiceLedger == null) return;
        bool changed;
        if (!PacingOn || !_cfg.Chatter.Value)
        {
            changed = _pacer.M < 1;
            _pacer.Reset();
        }
        else
        {
            var v = BuildOptimizer();
            // Taken while unpaced only: starts seen while paced are fewer, and a base learned from them would pace itself lower and lower.
            if (_pacer.M >= 1 || double.IsPositiveInfinity(_wouldStart)) _wouldStart = UsageModel.StartsPerHour(v.Now, v.Now, v.Rate);
            changed = _pacer.Step(v.Budgets.Select(b => (b.Key, UsageModel.ResidentsAllowed(b, v.Headroom),
                Math.Max(0, v.PerHourNow[b.Key] - b.YoursPerHour - b.GreetPerHour))));
            if (changed) Log.Info($"Pacing: residents' talk at {_pacer.M:P0}" + (_pacer.Binding != null ? $" to stay on track ({Short(v.Budgets.First(b => b.Key == _pacer.Binding).Name)})." : "."));
        }
        if (changed) _chatterSettings?.Invalidate();
    }

    private static IEnumerable<object> PacerRows()
    {
        var on = SettingsCatalog.ToContract(new SettingControl(ControlKind.Switch));
        on["title"] = "Keep on pace";
        on["hint"] = "";
        on["hintLive"] = (Func<string?>)(() =>
        {
            if (_usagePlan?.Pacing != true) return "Off: only the plan's settings";
            if (_pacePaused) return "Paused until you close the game";
            if (_pacer.M >= 1) return "Residents' talk as you set it";
            string who = _pacer.Binding != null && Optimizer().Budgets.FirstOrDefault(b => b.Key == _pacer.Binding) is { } b ? $" for {Short(b.Name)}" : "";
            return $"Residents' talk at {_pacer.M:P0} to stay on track{who}";
        });
        on["help"] = "Between plans, a busy evening can run ahead of a limit. With this on, once a minute the mod checks each limit and, when " +
                     "residents' talk would run past its target, starts fewer of their conversations until it's back on track (never fewer than " +
                     "a fifth). Your own conversations are never slowed.";
        on["type"] = "bool";
        on["get"] = (Func<object?>)(() => _usagePlan?.Pacing == true);
        on["set"] = (Func<object, string?>)(x =>
        {
            if (_usagePlan != null) _usagePlan.Pacing = x is true;
            Guarded("pacing", PaceMinute);
            return null;
        });
        yield return on;
        var pause = Row("button", "Pause pacing", "", "Lets residents talk as your settings say until you close the game (pacing starts again next time).");
        pause["label"] = "Pause";
        pause["hintLive"] = (Func<string?>)(() => _pacePaused ? "Paused: press to resume" : "Until you close the game");
        pause["click"] = (Func<string?>)(() =>
        {
            _pacePaused = !_pacePaused;
            Guarded("pacing", PaceMinute);
            return null;
        });
        yield return pause;
    }
}
