using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Social;
using NivalisAIChat.Voice;

namespace NivalisAIChat.Tests;

/// <summary>0.3.8 FB-41: the Usage Optimizer's model (spending per play-hour, the dial, the best level, chances) and its measuring.</summary>
internal static partial class Program
{
    private static readonly Dictionary<string, double> Mix038 = new() { ["talk"] = 0.6, ["call"] = 0.2, ["think"] = 0.15, ["order"] = 0.05 };

    private static BudgetInput Ai038(double limit, double used = 0, double hoursLeft = 40) => new()
    {
        Key = "ai", Name = "AI", Unit = "tokens", Ai = true, Limit = limit, Used = used, HoursLeft = hoursLeft,
        YoursPerHour = 20000, PerStart = 2500, GreetPerHour = 1500,
    };

    private static BudgetInput Voice038(string key, double limit, double used = 0, double hoursLeft = 40, double chance = 50, double lineCost = 60) => new()
    {
        Key = key, Name = key, Unit = "credits", Limit = limit, Used = used, HoursLeft = hoursLeft,
        YoursPerHour = 3000, PerStart = 400, GreetPerHour = 200, Chance = chance, LineCost = lineCost,
    };

    private static async Task Fixes038Tests()
    {
        var none = new HashSet<string>();
        var now = new Knobs();

        // Learning from little data.
        Check(UsageModel.Shrink(10, 0, 0, 2) == 10 && Math.Abs(UsageModel.Shrink(10, 2000, 100, 2) - 2000.0 / 100 * 100 / 102 - 10 * 2 / 102.0) < 1e-9,
              "an estimate is the prior with nothing seen, and mostly what was seen after a lot");
        Check(UsageModel.Confidence(1) == "low" && UsageModel.Confidence(4) == "medium" && UsageModel.Confidence(10) == "good", "how sure: low, medium, good by play-hours");
        Check(UsageModel.HoursPerDay(6, 0) == 2 && Math.Abs(UsageModel.HoursPerDay(6, 5) - 6) < 1e-9 && UsageModel.HoursPerDay(6, 1, 3) == 3,
              "play-hours a day: 2 with no days played, learned after five, yours when you set it");
        Check(UsageModel.HoursUntil(new DateTime(2026, 10, 14), new DateTime(2026, 10, 24), 2) == 20 && UsageModel.HoursUntil(new DateTime(2026, 10, 14), new DateTime(2026, 10, 14), 2) == 0.5,
              "play-hours to a reset: days left x hours a day, never nothing");
        Check(UsageModel.EveryText(10) == "10s" && UsageModel.EveryText(120) == "2m" && UsageModel.EveryText(60) == "1m" && UsageModel.EveryText(3600) == "1h" && UsageModel.EveryText(11) == "10s",
              "how often, as DynamicEvery writes it (the nearest choice)");

        // Starts per hour.
        double rate = 0.3;
        Check(UsageModel.StartsPerHour(now with { PerHour = 20 }, now, rate) == 20, "starts per hour never pass ChatterPerHour");
        Check(UsageModel.StartsPerHour(now with { PerHour = 1000, Every = 60 }, now, rate) < UsageModel.StartsPerHour(now with { PerHour = 1000, Every = 10 }, now, rate),
              "looking less often starts fewer");
        Check(UsageModel.StartsPerHour(now with { PerHour = 1000, MaxNpcs = 6 }, now, rate) > UsageModel.StartsPerHour(now with { PerHour = 1000 }, now, rate),
              "more people at once starts more");
        Check(Math.Abs(UsageModel.StartRate(0, 0, now) - 0.3) < 1e-9, "how often a look starts one: 0.3 with nothing seen");

        // Length and the voices ring.
        Check(Math.Abs(UsageModel.LengthFactor(now, now, Mix038) - 1) < 1e-9 && UsageModel.LengthFactor(now with { Lines = 2, PhoneLines = 2, ThinkLines = 1 }, now, Mix038) < 0.6,
              "shorter conversations cost less (the same settings: the same)");
        Check(Math.Abs(UsageModel.VoicedFactor(now with { VoiceRing = 9 }, now) - 0.25) < 1e-9 && UsageModel.VoicedFactor(now with { VoiceRing = 60 }, now) <= Math.Pow(18.25 / 18, 2) + 1e-9,
              "half the voices ring voices a quarter; past the chatter ring nothing more is voiced");

        // The dial.
        var top = UsageModel.At(1, 1, now, none);
        var bottom = UsageModel.At(0, 0, now, none);
        Check(top.PerHour >= now.PerHour && top.Lines >= now.Lines && top.Every <= now.Every && top.VoiceRing == now.VoiceRing && top.AiGreetings && top.SpeakGreetings,
              "at 100%: at least as lively as now, the voices ring as yours");
        Check(bottom.PerHour == UsageModel.Quiet.PerHour && bottom.Every == 60 && bottom.Lines == 2 && !bottom.AiOrderTalk && !bottom.AiGreetings && !bottom.SpeakGreetings && bottom.VoiceRing == 6,
              "at 0%: the quiet end, written order lines and stock greetings, no greeting voices");
        var pins = new HashSet<string> { "Lines", "VoiceRing", "AiGreetings" };
        var pinnedBottom = UsageModel.At(0, 0, now, pins);
        Check(pinnedBottom.Lines == now.Lines && pinnedBottom.VoiceRing == now.VoiceRing && pinnedBottom.AiGreetings == now.AiGreetings && pinnedBottom.PerHour == UsageModel.Quiet.PerHour,
              "a pinned setting stays as you set it; the others give way");
        var mid = UsageModel.At(0.85, 1, now, none);
        Check(mid.PerHour == top.PerHour && mid.Every == top.Every && mid.Lines < top.Lines, "coming down from 100%: lines each give way first");
        var ai = Ai038(0);
        bool monotone = true;
        double last = -1;
        for (double lv = 0; lv <= 1.0001; lv += 0.05)
        {
            double ph = UsageModel.PerHour(ai, UsageModel.At(lv, 1, now, none), now, rate, Mix038);
            if (ph + 1e-6 < last) monotone = false;
            last = ph;
        }
        Check(monotone, "a livelier level never costs less (so the search can trust it)");
        var advanced = now with { Advanced = true, Every = 20 };
        Check(UsageModel.At(0, 0, advanced, none).Every == 20 && UsageModel.At(0, 0, advanced, none).MaxNpcs == advanced.MaxNpcs,
              "Advanced mode: how often each kind is looked for stays yours");

        // No limit: nothing to keep within.
        var free = UsageModel.Best(new[] { Ai038(0), Voice038("Eleven", 0) }, now, none, rate, Mix038, 0.1);
        Check(!free.Limited && free.Settings == now && free.Notes.Any(n => n.Contains("No limit")), "no limit set: your settings stay as they are, and it says why");

        // A tight voice limit: the voices give way, the talk stays.
        var tightVoice = Voice038("Eleven", 150000);
        var best = UsageModel.Best(new[] { Ai038(0), tightVoice }, now, none, rate, Mix038, 0.1);
        Check(best.Feasible && best.Limited && best.AtReset["Eleven"] <= 0.9 + 1e-9 && best.AtReset["Eleven"] > 0.6 && best.Binding == "Eleven",
              $"a voice limit: kept under 90% at its reset, and not far under it ({best.AtReset["Eleven"]:P0})");
        Check(best.Voice < 1 || best.Activity < 1, "and something gave way for it");
        var looser = UsageModel.Best(new[] { Ai038(0), Voice038("Eleven", 400000) }, now, none, rate, Mix038, 0.1);
        Check(looser.Activity >= best.Activity && (looser.Activity > best.Activity || looser.Voice >= best.Voice), "a bigger limit is never a quieter plan");

        // A tight AI limit: the talk comes down to fit.
        var aiTight = Ai038(3_000_000, used: 200_000);
        var aiBest = UsageModel.Best(new[] { aiTight }, now, none, rate, Mix038, 0.1);
        Check(aiBest.Feasible && aiBest.AtReset["ai"] <= 0.9 + 1e-9 && aiBest.AtReset["ai"] > 0.75 && aiBest.Activity < 1,
              $"an AI limit: residents' talk comes down until it fits ({aiBest.AtReset["ai"]:P0} at {aiBest.Activity:P0})");
        var headroom = UsageModel.Best(new[] { aiTight }, now, none, rate, Mix038, 0.3);
        Check(headroom.AtReset["ai"] <= 0.7 + 1e-9 && headroom.Activity <= aiBest.Activity, "more headroom: a lower target");

        // Your own conversations alone are too much.
        var over = UsageModel.Best(new[] { Ai038(500_000) }, now, none, rate, Mix038, 0.1);
        Check(!over.Feasible && over.Notes.Any(n => n.Contains("your own conversations")), "your own conversations alone pass the limit: it says so, and moves nothing of yours");
        var pinnedOver = UsageModel.Best(new[] { Ai038(1_000_000) }, now, new HashSet<string>(UsageModel.Levers), rate, Mix038, 0.1);
        Check(!pinnedOver.Feasible && pinnedOver.Notes.Any(n => n.Contains("unpin")), "everything pinned and too much: it says to unpin something");

        // The dial at a level of your choosing.
        var at = UsageModel.AtLevel(best.Activity, new[] { Ai038(0), tightVoice }, now, none, rate, Mix038, 0.1);
        Check(at.Activity == best.Activity && Math.Abs(at.Voice - best.Voice) < 1e-9 && at.AtReset["Eleven"] <= 0.9 + 1e-9, "the dial where you put it: the liveliest voices that fit what is left there");

        // Chances for new people.
        var a = Voice038("A", 100000, used: 10000, hoursLeft: 10, chance: 50, lineCost: 60);
        var b = Voice038("B", 100000, used: 50000, hoursLeft: 10, chance: 50, lineCost: 60);
        var local = Voice038("Local", 0, chance: 20);
        local.Free = true;
        var ch = UsageModel.Chances(new[] { a, b, local }, 0.1);
        Check(ch["A"] > ch["B"] && Math.Abs(ch["A"] + ch["B"] - 100) < 0.2 && ch["Local"] == 20, $"chances: more to the one with more left, the pool kept, free voices as they are ({ch["A"]}/{ch["B"]})");
        var dear = Voice038("Dear", 100000, hoursLeft: 10, chance: 50, lineCost: 200);
        var cheap = Voice038("Cheap", 100000, hoursLeft: 10, chance: 50, lineCost: 50);
        var ch2 = UsageModel.Chances(new[] { dear, cheap }, 0.1);
        Check(ch2["Cheap"] > ch2["Dear"], "the same left: more to the one whose lines cost less");
        Check(UsageModel.Chances(new[] { a }, 0.1)["A"] == 50, "one limited provider keeps its chance");

        Check(UsageModel.Show("Every", now) == "10s" && UsageModel.Show("VoiceRing", now) == "18 m" && UsageModel.Show("AiGreetings", now with { AiGreetings = false }) == "off",
              "a lever's value as its setting writes it");

        // Measuring: the AI's tokens by purpose and model, never its key.
        Check(LlmClient.LedgerKey("https://api.anthropic.com/v1/messages?key=sk-secret", "Claude-X ") == "AI|api.anthropic.com|claude-x",
              "the AI's usage book: host and model only");
        var ledger = new VoiceLedger();
        var ep = new FakeEndpoint { Respond = _ => (200, AnthropicText("hello")) };
        var client = new LlmClient(() => new LlmSettings { ApiKey = "sk-ant-zzz", Model = "claude-x" }, _ => { }, new HttpMessageInvoker(ep)) { Ledger = ledger };
        var req = new LlmRequest { Purpose = "chatter" };
        req.Messages.Add(new LlmMessage("user", "Go."));
        var res = await client.CompleteAsync(req, CancellationToken.None);
        string key = ledger.Keys.FirstOrDefault(k => k.StartsWith("AI|", StringComparison.Ordinal)) ?? "";
        var sum = ledger.Of(key);
        Check(res.Ok && sum != null && sum.Lines == 1 && sum.LineMean > 0 && sum.Purposes.ContainsKey("chatter") && sum.Unit == "tokens" && !key.Contains("sk-") && !sum.Name.Contains("sk-"),
              "an AI reply is counted in the usage record by purpose, without the key");
        ledger.Record(UsageModel.StartsKey, "Residents' conversations", "starts", "talk", 1);
        Check(ledger.Of(UsageModel.StartsKey)?.Purposes["talk"].lines == 1, "a resident conversation started is counted by kind");

        PlanBookTests();
        PacerTests();
        await ReviewF0141Tests();
        var withChances = UsageModel.AllLevers(new[] { "Eleven|https://api.elevenlabs.io|eleven_v4" });
        var pinBook = new UsagePlanBook();
        pinBook.Pin(UsageModel.ChanceLever("Eleven|https://api.elevenlabs.io|eleven_v4"), true);
        Check(withChances.Contains("chance:Eleven|https://api.elevenlabs.io|eleven_v4") && withChances.Take(UsageModel.Levers.Length).SequenceEqual(UsageModel.Levers)
              && pinBook.Kept(withChances, _ => "50").Contains("chance:Eleven|https://api.elevenlabs.io|eleven_v4"),
              "a voice provider's chance is a lever the plan keeps when you pin it (review F-0142)");
    }

    /// <summary>Review F-0141: a key pasted into a model setting never reaches the usage record (its file, its names).</summary>
    private static async Task ReviewF0141Tests()
    {
        const string pasted = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789";
        Check(!LlmClient.LedgerKey("https://api.anthropic.com/v1/messages", pasted).Contains("abcdefghijklmnop"), "the AI's usage book: a key in Model is never its name");
        var slot = new VoiceSlot { Provider = VoiceProvider.ElevenLabs, Url = "https://api.elevenlabs.io", Model = pasted };
        Check(!VoiceService.LedgerKey(slot).Contains("abcdefghijklmnop") && !VoiceService.LedgerName(slot).Contains("abcdefghijklmnop"),
              "a voice provider's usage book: a key in its model is never in its key or name");
        var ledger = new VoiceLedger();
        var voyage = new VoyageClient(() => new VoyageSettings { Key = "pa-x", Model = pasted }, new HttpMessageInvoker(new FakeVoyage())) { Ledger = ledger };
        try { await voyage.EmbedAsync(new[] { "the storm at the docks" }, true, CancellationToken.None); } catch { }
        Check(ledger.Keys.Count == 1 && ledger.Keys.All(k => !k.Contains("abcdefghijklmnop")) && ledger.Keys.All(k => !(ledger.Of(k)?.Name ?? "").Contains("abcdefghijklmnop"))
              && ledger.Of(ledger.Keys[0])?.Purposes.ContainsKey("recall") == true,
              "Voyage's usage book: counted by purpose, a key in VoyageModel never in its key or name");
    }

    private static void PlanBookTests()
    {
        string dir = TestRoot.NewFolder("plan038");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "usage_plan.json");
        var book = new UsagePlanBook(path);
        Check(book.Headroom == 0.10 && book.HoursPerDay == 0 && book.Dial == null && book.Pacing && !book.CanUndo, "a new plan: 10% headroom, play learned, the best level, pacing on, nothing to undo");
        book.Headroom = 0.9;
        book.HoursPerDay = 40;
        book.Dial = 1.5;
        Check(book.Headroom == UsagePlanBook.MaxHeadroom && book.HoursPerDay == 24 && book.Dial == 1, "out-of-range values are kept within bounds");
        book.Headroom = double.NaN;
        Check(book.Headroom == 0.10, "an unreadable headroom is the usual 10%");
        var values = new Dictionary<string, string> { ["PerHour"] = "40", ["Lines"] = "3", ["chance:E"] = "60" };
        book.Pin("Every", true);
        book.Record(new Dictionary<string, string> { ["PerHour"] = "75", ["Lines"] = "4", ["chance:E"] = "50" }, values, new DateTime(2026, 10, 14, 13, 0, 0));
        string Now(string l) => values.TryGetValue(l, out var x) ? x : "";
        Check(!book.Changed("PerHour", "40") && book.Changed("PerHour", "60") && !book.Changed("MaxNpcs", "9"), "changed: only a setting the plan set, and only once it differs");
        values["PerHour"] = "60"; // you changed it after the plan
        var kept = book.Kept(UsageModel.Levers.Append("chance:E"), Now);
        Check(kept.Contains("Every") && kept.Contains("PerHour") && !kept.Contains("Lines") && !kept.Contains("chance:E"), "kept: your pins and what you changed since the plan");
        var undo = book.UndoValues(Now);
        Check(undo.Count == 2 && undo["Lines"] == "4" && undo["chance:E"] == "50" && !undo.ContainsKey("PerHour"), "undo puts back what the plan set, never what you changed since");
        book.Flush();
        var again = new UsagePlanBook(path);
        Check(again.Pinned("Every") && again.AppliedValue("Lines") == "3" && again.CanUndo && again.Headroom == 0.10 && again.AppliedAt == new DateTime(2026, 10, 14, 13, 0, 0),
              "the plan is kept between sessions");
        again.Forget();
        again.Pin("Every", false);
        Check(!again.CanUndo && !again.Changed("Lines", "9") && !again.Pinned("Every"), "after Undo nothing is \"changed\"; a pin comes off");
        again.Flush();
        System.IO.File.WriteAllText(path, "{\"Headroom\":null,\"Pins\":null,\"Applied\":null,\"Before\":null}");
        var broken = new UsagePlanBook(path);
        Check(broken.Headroom == 0.10 && !broken.Pinned("x") && !broken.CanUndo && broken.Kept(UsageModel.Levers, _ => "").Count == 0, "a plan file with parts emptied is repaired, never a failure");
        System.IO.File.WriteAllText(path, "not json");
        var bad = new UsagePlanBook(path, _ => { });
        Check(bad.Headroom == 0.10, "an unreadable plan file: a fresh plan");
    }

    private static void PacerTests()
    {
        var p = new Pacer();
        Check(!p.Step(new[] { ("ai", 1000.0, 900.0) }) && p.M == 1 && p.Binding == null, "under its allowance: nothing paced");
        Check(!p.Step(new[] { ("ai", double.PositiveInfinity, 9000.0) }) && p.M == 1, "no limit: nothing paced");
        p.Step(new[] { ("ai", 500.0, 1000.0), ("eleven", 800.0, 1000.0) });
        Check(Math.Abs(p.M - Math.Sqrt(0.5)) < 1e-9 && p.Binding == "ai", "over: a square-root step toward the tightest budget, which it names");
        for (int i = 0; i < 20; i++) p.Step(new[] { ("ai", 500.0, 1000.0) });
        Check(p.M >= 0.5 * 0.9 - 1e-9 && p.M <= 0.5 * 1.1 + 1e-9, $"it settles within 10% of the share that fits ({p.M:0.###})");
        double settled = p.M;
        p.Step(new[] { ("ai", 520.0, 1000.0) });
        Check(p.M == settled, "inside the dead band, it doesn't move (no hunting)");
        for (int i = 0; i < 20; i++) p.Step(new[] { ("ai", 0.0, 1000.0) });
        Check(p.M >= Pacer.Floor && p.M <= Pacer.Floor * Pacer.High, $"nothing left: down to a fifth, never under ({p.M:0.###})");
        double low = p.M;
        p.Step(new[] { ("ai", 500.0, 1000.0) });
        Check(p.M > low * 1.2 && p.M < 0.5 + 1e-9, $"room again, though not all of it: it comes partway back up toward the share that fits ({low:0.###} -> {p.M:0.###})");
        for (int i = 0; i < 40; i++) p.Step(new[] { ("ai", 5000.0, 1000.0) });
        Check(p.M == 1 && p.Binding == null, "back on track: all the way back to your settings");
        Check(p.Cap(75, 40) == 75, "unpaced: your cap as it is");
        p.Step(new[] { ("ai", 250.0, 1000.0) });
        Check(p.Cap(75, 40) == (int)Math.Round(40 * p.M) && p.Cap(75, 0) == 1 && p.Cap(0, 40) == 0, "paced: a share of what would start, at least one; a cap of 0 stays 0");
        p.Reset();
        Check(p.M == 1 && p.Binding == null, "reset: as your settings have it");
        var b = Ai038(1_000_000, used: 100_000, hoursLeft: 10);
        Check(Math.Abs(UsageModel.ResidentsAllowed(b, 0.1) - (900_000 - 100_000 - 10 * 21500) / 10.0) < 1e-6 && double.IsPositiveInfinity(UsageModel.ResidentsAllowed(Ai038(0), 0.1)),
              "residents' allowance: the target less what's used and your share to the reset, over the hours left");
    }
}
