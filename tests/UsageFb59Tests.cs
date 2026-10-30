using System;
using System.Collections.Generic;
using System.Linq;
using NivalisAIChat.Ai;
using NivalisAIChat.Social;
using NivalisAIChat.Voice;

namespace NivalisAIChat.Tests;

/// <summary>FB-59: the Usage Optimizer pass (reset days, your own use, the account's remainder, no-limit plans, the mix, pacing, spacing).</summary>
internal static partial class Program
{
    private static void UsageFb59Tests()
    {
        Console.WriteLine("-- FB-59: the Usage Optimizer pass");

        // A monthly limit on its own day (by the PC's calendar).
        var oct16 = new DateTime(2026, 10, 16, 14, 0, 0);
        Check(TokenBudget.Start(oct16, TokenBudget.Span.Month, 11) == new DateTime(2026, 10, 11) && TokenBudget.NextReset(oct16, TokenBudget.Span.Month, 11) == new DateTime(2026, 11, 11),
              "a month from the 11th: Oct 11 to Nov 11");
        Check(TokenBudget.Start(oct16, TokenBudget.Span.Month, 20) == new DateTime(2026, 9, 20) && TokenBudget.NextReset(oct16, TokenBudget.Span.Month, 20) == new DateTime(2026, 10, 20),
              "...from the 20th, before it comes round: Sep 20 to Oct 20");
        var feb = new DateTime(2026, 2, 28, 12, 0, 0);
        Check(TokenBudget.Start(feb, TokenBudget.Span.Month, 31) == new DateTime(2026, 2, 28) && TokenBudget.NextReset(feb, TokenBudget.Span.Month, 31) == new DateTime(2026, 3, 31)
              && TokenBudget.NextReset(new DateTime(2026, 2, 10), TokenBudget.Span.Month, 31) == new DateTime(2026, 2, 28),
              "...the 31st in February is its last day (Feb 28), then Mar 31");
        Check(TokenBudget.PeriodKey(oct16, TokenBudget.Span.Month) == "month 2026-10" && TokenBudget.PeriodKey(oct16, TokenBudget.Span.Month, 1) == "month 2026-10"
              && TokenBudget.PeriodKey(oct16, TokenBudget.Span.Month, 11) == "month 2026-10-11",
              "the 1st keeps its old key (an update doesn't start the count again); another day has its own");
        var budget = new TokenBudget(() => (1000, TokenBudget.Span.Month)) { Now = () => new DateTime(2026, 10, 12), ResetDay = () => 11 };
        budget.Add(300);
        budget.Now = () => new DateTime(2026, 11, 10, 23, 0, 0);
        bool kept = budget.Used == 300;
        budget.Now = () => new DateTime(2026, 11, 11, 0, 30, 0);
        Check(kept && budget.Used == 0 && budget.Describe().Contains("Dec 11"), "the token count starts again on its own day, and says when next");
        int day = 1;
        var moving = new TokenBudget(() => (1000, TokenBudget.Span.Month)) { Now = () => new DateTime(2026, 10, 16), ResetDay = () => day };
        moving.Add(800);
        day = 20;
        Check(moving.Used == 800 && moving.Period == "month 2026-09-20", "moving the reset day mid-month keeps the month's count (review F59-1)");
        moving.Now = () => new DateTime(2026, 10, 21);
        Check(moving.Used == 0, "...and it starts again on the new day");

        // A provider's reset day in its line.
        var slot = VoiceProviderList.Parse(new[] { "    (https://api.elevenlabs.io, el-key, 50, limit: 60000/month, resets: 11, auto)," }, new List<string>()).Single();
        Check(slot.ResetDay == 11 && slot.Limit == 60000 && !slot.Voices.Contains("resets"), "a provider's line takes \"resets: 11\" (not a voice)");
        var problems = new List<string>();
        var bad = VoiceProviderList.Parse(new[] { "    (https://api.elevenlabs.io, el-key, 50, resets: 40, auto)," }, problems).Single();
        Check(bad.ResetDay == 0 && !bad.Voices.Contains("resets") && problems.Any(p => p.Contains("reset day")), "...one it can't read is said, and never a voice");
        var entry = ProviderLines.Read(new[] { "    (https://api.elevenlabs.io, el-key, 50, limit: 60000/month, resets: 11, auto)," }).Single();
        Check(entry.ResetDay == 11 && ProviderLines.Write(entry with { ResetDay = 15 }).Contains("resets: 15") && !ProviderLines.Write(entry with { ResetDay = 0 }).Contains("resets"),
              "...and the menu writes it back (none when it's 0)");
        var now = new DateTime(2026, 10, 16, 12, 0, 0);
        var svc = new VoiceService(() => new VoiceSettings { Enabled = true, Slots = { slot } }, new VoiceBook(null), _ => { }) { Now = () => now };
        Check(svc.PeriodOf(slot) == (new DateTime(2026, 10, 11), new DateTime(2026, 11, 11)), "...and its period follows it");

        // Your own use, not the account's (everyone using the key).
        var ledger = new VoiceLedger { Now = () => now };
        ledger.Record("eleven", "ElevenLabs", "credits", "you", 500);
        ledger.Calibrate("eleven", "ElevenLabs", "credits", 29000, new DateTime(2026, 10, 11));
        ledger.Record("eleven", "ElevenLabs", "credits", "you", 40);
        Check(ledger.OwnUsedSince("eleven", new DateTime(2026, 10, 11)) == 540 && ledger.UsedSince("eleven", new DateTime(2026, 10, 11)) == 29040
              && ledger.SinceAccountOf("eleven") == 40,
              "your use is what this PC spoke (540); the account's count (29,040 with what was spoken since) is apart");

        // F-0172: hours from before 0.5.1 (counted over too few minutes) are dropped once; days and line costs stay.
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pon-fb59-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string file = System.IO.Path.Combine(dir, "voice_ledger.json");
            System.IO.File.WriteAllText(file, "{\"Books\":{\"eleven\":{\"Name\":\"ElevenLabs\",\"Unit\":\"credits\",\"Days\":{\"2026-10-16\":4951},\"Hours\":[7973],\"ThisHour\":120," +
                                              "\"Line\":{\"N\":3,\"Mean\":12},\"Purposes\":{\"you\":{\"N\":3,\"Mean\":12}},\"Recent\":[12,12,12]}},\"PlayMinutes\":{\"2026-10-16\":37},\"MinuteInHour\":18}");
            var old = new VoiceLedger(file) { Now = () => now };
            var st = old.Of("eleven")!;
            Check(st.HoursSeen == 0 && st.HourMean == 0 && st.Lines == 3 && old.OwnUsedSince("eleven", new DateTime(2026, 10, 16)) == 4951 && old.PlayTime(14).daysPlayed == 0,
                  "an old record's hours and play days are learned again (its 1 hour held days of spending); its days of use and lines are kept");
            var again = new VoiceLedger(file) { Now = () => now };
            again.Record("eleven", "ElevenLabs", "credits", "you", 10);
            for (int m = 0; m < 60; m++) again.PlayMinute();
            Check(again.Of("eleven")!.HoursSeen == 1, "...once: the hours learned after it are kept");
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }

        // Newer play-hours count more.
        Check(Math.Abs(VoiceLedger.Recent(new double[] { 100, 100, 100 }) - 100) < 1e-9 && VoiceLedger.Recent(new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 100 }) > 10
              && VoiceLedger.Recent(new double[] { 100, 0, 0, 0, 0, 0, 0, 0, 0, 0 }) < 10 && VoiceLedger.Recent(Array.Empty<double>()) == 0,
              "the hourly average leans on the newest hours (the same hours: the same average)");

        // The account's remainder is a ceiling; your limit is yours.
        var knobs = new Knobs();
        var none = new HashSet<string>();
        var mix = new Dictionary<string, double>();
        BudgetInput Voice(double limit, double accountLeft) => new()
        {
            Key = "v", Name = "ElevenLabs", Limit = limit, Used = 500, Reset = now.AddDays(25), HoursLeft = 50, AccountLeft = accountLeft,
            YoursPerHour = 100, PerStart = 30, GreetPerHour = 0, LineCost = 40, Chance = 50,
        };
        var mine = UsageModel.Best(new List<BudgetInput> { Voice(60000, double.PositiveInfinity) }, knobs, none, 0.3, mix, 0.1);
        var capped = UsageModel.Best(new List<BudgetInput> { Voice(60000, 20000) }, knobs, none, 0.3, mix, 0.1);
        Check(capped.Limited && capped.Activity < mine.Activity, $"with little left on the account the plan is quieter than your limit alone allows ({mine.Activity:P0} -> {capped.Activity:P0})");
        var accountOnly = UsageModel.Best(new List<BudgetInput> { Voice(0, 20000) }, knobs, none, 0.3, mix, 0.1);
        Check(accountOnly.Limited && accountOnly.Activity < 1, "...even with no limit of yours, the account's remainder is planned within");
        Check(UsageModel.ResidentsAllowed(Voice(60000, 20000), 0.1) < UsageModel.ResidentsAllowed(Voice(60000, double.PositiveInfinity), 0.1),
              "...and the pacer allows residents less for it");
        Check(Math.Abs(UsageModel.PerDayLeft(Voice(60000, double.PositiveInfinity), 0.1, now) - (0.9 * 60000 - 500) / 25) < 1e-6
              && Math.Abs(UsageModel.PerDayLeft(Voice(60000, 20000), 0.1, now) - 0.9 * 20000 / 25) < 1e-6
              && double.IsPositiveInfinity(UsageModel.PerDayLeft(Voice(0, double.PositiveInfinity), 0.1, now)),
              "a day's share of what's left until the reset: your limit's, or the account's if that's less; none without a limit");

        // No limit: still a plan.
        var unlimited = UsageModel.Best(new List<BudgetInput> { Voice(0, double.PositiveInfinity) }, knobs, none, 0.3, mix, 0.1);
        Check(!unlimited.Limited && unlimited.Activity == 1 && unlimited.Settings == knobs with { } && unlimited.Notes.Any(n => n.Contains("your plan as your settings have it")),
              "no limit: the plan is your settings as they are, at 100%, said as such");
        var lower = UsageModel.AtLevel(0.5, new List<BudgetInput> { Voice(0, double.PositiveInfinity) }, knobs, none, 0.3, mix, 0.1);
        Check(lower.Settings.Lines < knobs.Lines || lower.Settings.PerHour < knobs.PerHour || lower.Settings.Every > knobs.Every, "...and the dial still plans for less");

        // Kinds switched off leave the estimate at once.
        var recorded = new Dictionary<string, double> { ["talk"] = 60, ["call"] = 30, ["think"] = 10 };
        var noCalls = UsageModel.WithoutKinds(recorded, new[] { "call" });
        Check(noCalls["call"] == 0 && noCalls["talk"] == 60 && noCalls["think"] == 10, "phone calls off: out of the mix");
        var fresh = UsageModel.WithoutKinds(new Dictionary<string, double>(), new[] { "think" });
        Check(fresh["think"] == 0 && fresh["talk"] > 0 && fresh.ContainsKey("call"), "...with nothing recorded yet, from the usual mix");
        double factor = UsageModel.MixCostFactor(recorded, noCalls, new Dictionary<string, double> { ["talk"] = 1000, ["call"] = 3000, ["think"] = 300 });
        Check(factor < 1 && factor > 0.4, $"calls cost more than talk: without them a start costs less ({factor:0.00} of before)");
        Check(UsageModel.MixCostFactor(recorded, recorded, new Dictionary<string, double>()) == 1, "...nothing switched off: unchanged");
        Check(UsageModel.LengthFactor(knobs with { PhoneLines = 1 }, knobs, noCalls) == 1, "...and the phone lines no longer change how long talk is");

        // Spread over the hour: a few back to back, then one at a time (the author: no long gaps).
        var starts = new List<double>();
        for (double t = 0; t < 3600; t += 10)
            if (ChatterPlan.InLastHour(starts, t) < 15 && ChatterPlan.Spaced(starts, t, 15, 2)) starts.Add(t);
        double gap = starts.Zip(starts.Skip(1), (a, b) => b - a).DefaultIfEmpty(0).Max();
        Check(starts.Count >= 14 && starts.Count(t => t >= 1800) >= 6 && gap <= 300,
              $"15 an hour with a start possible every 10 s: {starts.Count} spread over the hour, the longest gap {gap:0} s (not all in the first minutes)");
        Check(ChatterPlan.Spaced(new List<double>(), 0, 15, 2) && !ChatterPlan.Spaced(new List<double>(), 0, 0, 2), "an empty hour has room; a cap of 0 never does");
        var burst = new List<double> { 100, 100.5 };
        Check(!ChatterPlan.Spaced(burst, 101, 15, 2) && ChatterPlan.Spaced(burst, 101 + 240, 15, 2), "...after two back to back, the next waits its turn (4 minutes at 15 an hour)");
    }
}
