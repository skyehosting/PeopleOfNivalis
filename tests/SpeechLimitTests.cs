using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NivalisAIChat.Ai;
using NivalisAIChat.Social;

namespace NivalisAIChat.Tests;

/// <summary>
/// FB-75: speech to text has a limit like the AI's and the voices' (SpeechToTextLimit, minutes per SpeechToTextLimitPer),
/// is counted in seconds in the usage record, and shows in the Usage Optimizer. It's yours alone, so it never moves
/// residents' talk.
/// </summary>
internal static partial class Program
{
    private static BudgetInput Speech75(double limitSeconds, double used = 0) => new()
    {
        Key = "STT|api.openai.com|whisper-1", Name = "Speech to text", Unit = "seconds", Limit = limitSeconds, Used = used,
        YoursOnly = true, HoursLeft = 40, YoursPerHour = 60, LineCost = 5,
    };

    private static void SpeechLimitTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- FB-75: the speech-to-text limit");
        long limit = 60;
        var b = new TokenBudget(() => (limit, TokenBudget.Span.Day), null, null, TokenBudget.Wording.Speech);
        Check(b.Allows(), "no use yet: allowed");
        b.Add(59);
        Check(b.Allows() && b.Used == 59, "under the limit: allowed, seconds counted");
        b.Add(2);
        string msg = b.LimitMessage();
        Check(!b.Allows() && msg.StartsWith("[SPEECH-BUDGET] Speech-to-text limit reached") && msg.Contains("SpeechToTextLimit")
              && msg.Contains("seconds of speech to text"), "past it: refused, and the message names the limit and its setting");
        limit = 0;
        Check(b.Allows(), "0 = no limit");

        // The optimizer: shown, never steering.
        var now = new Knobs();
        var none = new HashSet<string>();
        const double rate = 0.5;
        var mix = new Dictionary<string, double> { ["talk"] = 0.6, ["call"] = 0.2, ["think"] = 0.15, ["order"] = 0.05 };
        var over = Speech75(600, used: 590);
        var alone = UsageModel.Best(new[] { Ai038(0) }, now, none, rate, mix, 0.1);
        var with = UsageModel.Best(new[] { Ai038(0), over }, now, none, rate, mix, 0.1);
        Check(!with.Limited && with.Settings == alone.Settings && with.Feasible, "a speech limit alone: residents' talk stays as it is");
        Check(with.AtReset.TryGetValue(over.Key, out var share) && share > 1, "but its forecast is shown (here, past its limit)");
        var aiTight = Ai038(3_000_000, used: 200_000);
        var aiOnly = UsageModel.Best(new[] { aiTight }, now, none, rate, mix, 0.1);
        var aiAndSpeech = UsageModel.Best(new[] { aiTight, over }, now, none, rate, mix, 0.1);
        Check(aiAndSpeech.Activity == aiOnly.Activity && aiAndSpeech.Binding == "ai" && aiAndSpeech.Feasible,
              "with an AI limit too: the plan is the AI's alone, the speech limit never binds");
        Check(double.IsPositiveInfinity(UsageModel.ResidentsAllowed(over, 0.1)), "the pacer never slows residents for it");
        var chances = UsageModel.Chances(new[] { Voice038("Eleven", 100000), Voice038("Cartesia", 100000), over }, 0.1);
        Check(!chances.ContainsKey(over.Key), "and it's no voice to share new people out to");
        Check(over.Capped && !over.Steers, "it's capped (shown as limited), not steering");

        // Wired through: checked before anything is sent, counted after, in the optimizer and the usage report.
        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string vi = File.ReadAllText(Path.Combine(src, "ModRuntime.VoiceInput.cs"));
        int gate = vi.IndexOf("!_speechBudget.Allows()", StringComparison.Ordinal);
        int send = vi.IndexOf("speech.TranscribeAsync(", StringComparison.Ordinal);
        int count = vi.IndexOf("CountSpeech(seconds);", StringComparison.Ordinal);
        Check(gate > 0 && send > gate && count > send, "the limit is checked before the recording is sent, and counted once it's written down");
        Check(vi.Contains("_voiceLedger?.Record(SpeechLedgerKey") && !vi.Contains("SpeechToTextKey.Value)"), "recorded by service and model, never by key");
        string cfg = File.ReadAllText(Path.Combine(src, "ModConfig.cs"));
        Check(cfg.Contains("Bind(\"SpeechToTextLimit\", 0,") && cfg.Contains("Bind(\"SpeechToTextLimitPer\", \"Day\""), "the settings: no limit and a day, by default");
        string cat = File.ReadAllText(Path.Combine(src, "UI", "SettingsCatalog.cs"));
        Check(cat.Contains("[\"SpeechToTextLimit\"] = S(0, 600, 5, \"min\")") && cat.Contains("[\"SpeechToTextLimitPer\"] = C(\"Hour\", \"Day\", \"Week\", \"Month\")"),
              "in the settings menu: a minutes slider and its period");
        string usage = File.ReadAllText(Path.Combine(src, "ModRuntime.Usage.cs"));
        Check(usage.Contains("Name = \"Speech to text\"") && usage.Contains("YoursOnly = true") && usage.Contains("speech[\"title\"] = \"Speech to text\""),
              "in the Usage Optimizer: its forecast and its limit");
        string rt = File.ReadAllText(Path.Combine(src, "ModRuntime.cs"));
        Check(rt.Contains("\"speech_usage.json\"") && rt.Contains("_speechBudget?.Close();"), "its count is kept across restarts and closed at the end");

        // The author (2026-10-20): its own reset day, as the AI's and the voices' have (a monthly limit on the billing day).
        var clock = new DateTime(2026, 10, 20, 12, 0, 0);
        var monthly = new TokenBudget(() => (60, TokenBudget.Span.Month), null, null, TokenBudget.Wording.Speech) { ResetDay = () => 15, Now = () => clock };
        monthly.Add(61);
        clock = new DateTime(2026, 11, 1, 9, 0, 0);
        bool stillOver = !monthly.Allows();
        clock = new DateTime(2026, 11, 15, 0, 1, 0);
        Check(stillOver && monthly.Allows() && monthly.Used == 0, "a monthly speech limit starts again on its own day (the 15th here), not the 1st");
        Check(cfg.Contains("Bind(\"SpeechToTextLimitResetDay\", 1,") && rt.Contains("{ ResetDay = () => _cfg.SpeechToTextLimitResetDay.Value }")
              && usage.Contains("TokenBudget.NextReset(now, span, speechDay)") && usage.Contains("speechDay[\"title\"] = \"Speech to text: resets on day\"")
              && cat.Contains("[\"SpeechToTextLimitResetDay\"] = S(1, 31, 1)"),
              "SpeechToTextLimitResetDay: the setting (1 by default), the budget, the optimizer's period and the Usage page's slider");
    }
}
