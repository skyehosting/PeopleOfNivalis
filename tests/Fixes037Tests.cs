using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Voice;

namespace NivalisAIChat.Tests;

/// <summary>0.3.7 FB-41: what each voice line costs, the usage record, and a limit per voice provider.</summary>
internal static partial class Program
{
    /// <summary>ElevenLabs as the tests need it: a voice list, an account, and lines that say what they cost.</summary>
    private sealed class ElevenFake : HttpMessageHandler
    {
        public int Speech;
        public double Cost = 37, AccountUsed = 5000, AccountLimit = 10000;
        public DateTime Reset = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Local);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage Json(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s) };
            if (path.Contains("/user/subscription"))
                return Task.FromResult(Json($"{{\"tier\":\"creator\",\"character_count\":{AccountUsed},\"character_limit\":{AccountLimit}," +
                                            $"\"next_character_count_reset_unix\":{new DateTimeOffset(Reset).ToUnixTimeSeconds()}}}"));
            if (path.Contains("/voices"))
                return Task.FromResult(Json("{\"voices\":[{\"voice_id\":\"v_ann\",\"name\":\"Ann\",\"category\":\"premade\",\"labels\":{\"gender\":\"female\"}}],\"has_more\":false}"));
            if (path.Contains("text-to-speech"))
            {
                Interlocked.Increment(ref Speech);
                lock (this) AccountUsed += Cost; // the account counts it too, as ElevenLabs does
                var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4800]) };
                r.Headers.Add("character-cost", Cost.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return Task.FromResult(r);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static async Task Fixes037Tests()
    {
        Console.WriteLine("-- 0.3.7 FB-41: what a voice line costs, in each provider's own unit");
        Check(VoiceCost.ElevenLabsRate("eleven_flash_v2_5") == 0.5 && VoiceCost.ElevenLabsRate("eleven_v4_turbo") == 0.5
              && VoiceCost.ElevenLabsRate("eleven_v3") == 1 && VoiceCost.ElevenLabsRate("eleven_multilingual_v2") == 1,
              "ElevenLabs: a credit a character, half on its fast models");
        Check(VoiceCost.Estimate(VoiceProvider.ElevenLabs, "eleven_v3", "[calm] Hello") == 12 && VoiceCost.Estimate(VoiceProvider.ElevenLabs, "eleven_flash_v2_5", "abcd") == 2,
              "...counted on what was sent, its audio tag included");
        Check(VoiceCost.FromHeader("37") == 37 && VoiceCost.FromHeader("x") == null && VoiceCost.FromHeader(null) == null && VoiceCost.FromHeader("-4") == null,
              "ElevenLabs' own character-cost header is read when it's there");
        string inner = "<prosody rate='+0%'>Hi</prosody>";
        Check(VoiceCost.AzureCharacters($"<speak version='1.0' xml:lang='en-US'><voice name='x'>{inner}</voice></speak>") == inner.Length,
              "Azure: everything but its speak and voice tags (the rate and style markup counts)");
        Check(VoiceCost.AzureCharacters("你好") == 4 && VoiceCost.AzureCharacters("こんにちは") == 5, "...Chinese characters twice, kana once");
        Check(VoiceCost.Estimate(VoiceProvider.OpenAI, "gpt-4o-mini-tts", "12345678", "", seconds: 1) == 2 + 21
              && VoiceCost.Estimate(VoiceProvider.OpenAI, "tts-1", "12345678") == 8
              && VoiceCost.Estimate(VoiceProvider.Google, "gemini-2.5-flash-tts", "1234", "abcd", seconds: 2) == 2 + 50
              && VoiceCost.Estimate(VoiceProvider.Windows, "", "free") == 0,
              "token-billed voices count their text and audio tokens; characters otherwise; Windows' voices nothing");
        Check(VoiceCost.Unit(VoiceProvider.ElevenLabs, "") == "credits" && VoiceCost.Unit(VoiceProvider.OpenAI, "gpt-4o-mini-tts") == "tokens"
              && VoiceCost.Unit(VoiceProvider.Azure, "") == "characters", "each limit is in its provider's unit");

        Console.WriteLine("-- 0.3.7 FB-41: a limit in a provider's own line");
        Check(VoiceProviderList.Limit("limit: 125,000/month") == (125000, "month") && VoiceProviderList.Limit("limit=500 per week") == (500, "week")
              && VoiceProviderList.Limit("limit: 10") == (10, "month") && VoiceProviderList.Limit("limit: 2000/day") == (2000, "day")
              && VoiceProviderList.Limit("nova:female") == null && VoiceProviderList.Limit("limit: lots") == null,
              "\"limit: 125000/month\" is read (a month when no period is said); anything else isn't a limit");
        var slots = VoiceProviderList.Parse(new[] { "    (https://api.elevenlabs.io, el-key, 50, model: eleven_v3, limit: 125000/month, auto)," }, new List<string>());
        Check(slots.Single().Limit == 125000 && slots[0].LimitPer == "month" && slots[0].Voices == "" && slots[0].Model == "eleven_v3",
              "the voices read it as the provider's limit, not as a voice");
        var entry = ProviderLines.Read(new[] { "    (https://api.elevenlabs.io, el-key, 50, limit: 125000/month, auto)," }).Single();
        Check(entry.Limit == 125000 && entry.Per == "month" && ProviderLines.Write(entry) == "    (https://api.elevenlabs.io, el-key, 50, limit: 125000/month, auto),"
              && !ProviderLines.Write(entry with { Limit = 0 }).Contains("limit"), "the menu reads and writes it back the same (no limit: not written)");
        Check(ProviderLines.Write(entry with { Per = "week", Model = "eleven_v3" }) == "    (https://api.elevenlabs.io, el-key, 50, model: eleven_v3, limit: 125000/week, auto),",
              "...after the model, with its period");

        Console.WriteLine("-- 0.3.7 FB-41: Week");
        var wed = new DateTime(2026, 10, 14, 15, 30, 0);
        Check(TokenBudget.SpanOf("Week") == TokenBudget.Span.Week && TokenBudget.PeriodKey(wed, TokenBudget.Span.Week) == "week 2026-10-12"
              && TokenBudget.NextReset(wed, TokenBudget.Span.Week) == new DateTime(2026, 10, 19) && TokenBudget.Start(wed, TokenBudget.Span.Week) == new DateTime(2026, 10, 12),
              "a week runs from Monday 00:00");
        Check(TokenBudget.WeekStart(new DateTime(2026, 10, 18, 23, 0, 0)) == new DateTime(2026, 10, 12) && TokenBudget.WeekStart(new DateTime(2026, 10, 12)) == new DateTime(2026, 10, 12),
              "...Sunday night is still that week; Monday starts the next");

        Console.WriteLine("-- 0.3.7 FB-41: the usage record");
        string dir = TestRoot.NewFolder("fixes037");
        Directory.CreateDirectory(dir);
        var clock = new DateTime(2026, 10, 14, 20, 0, 0);
        string file = Path.Combine(dir, "voice_ledger.json");
        var ledger = new VoiceLedger(file) { Now = () => clock };
        for (int i = 1; i <= 10; i++) ledger.Record("eleven", "ElevenLabs", "credits", i <= 6 ? "chatter" : "you", i * 10);
        var s = ledger.Of("eleven")!;
        Check(s.Lines == 10 && Math.Abs(s.LineMean - 55) < 1e-9 && s.LineHigh == 90 && s.LineMax == 100, "a line's cost: its average, its highs (90th percentile) and the most");
        Check(s.Purposes["chatter"].lines == 6 && Math.Abs(s.Purposes["chatter"].mean - 35) < 1e-9 && Math.Abs(s.Purposes["you"].mean - 85) < 1e-9,
              "...kept apart by what the lines were for (your chats, residents' talk)");
        Check(ledger.UsedSince("eleven", clock.Date) == 550 && ledger.UsedSince("eleven", clock.Date.AddDays(1)) == 0 && ledger.UsedSince("nobody", clock.Date) == 0,
              "a period's use is what was spoken from its first day on");
        for (int m = 0; m < 60; m++) ledger.PlayMinute();
        ledger.Record("eleven", "ElevenLabs", "credits", "chatter", 5);
        var h = ledger.Of("eleven")!;
        Check(h.HoursSeen == 1 && h.HourMean == 550 && ledger.PlayTime(14) == (1.0 / 14, 1), "an hour of play's cost is kept once it's played; play time per day");
        ledger.Close();
        var again = new VoiceLedger(file) { Now = () => clock };
        Check(again.UsedSince("eleven", clock.Date) == 555 && again.Of("eleven")!.Lines == 11 && again.PlayTime(14).daysPlayed == 1,
              "it's all there after the game starts again");
        var start = new DateTime(2026, 10, 3, 9, 0, 0);
        again.Calibrate("eleven", "ElevenLabs", "credits", 4000, start);
        again.Record("eleven", "ElevenLabs", "credits", "you", 20);
        Check(again.UsedSince("eleven", start) == 4020 && again.UsedSince("eleven", clock.Date) == 575,
              "the account's own count is used for its period, plus what was spoken since; other periods count the days");
        again.Calibrate("eleven", "ElevenLabs", "credits", 4005, start);
        Check(again.UsedSince("eleven", start) == 4020, "a fresh read that hasn't counted the last lines yet never lowers it");
        again.Calibrate("eleven", "ElevenLabs", "credits", 4100, start);
        Check(again.UsedSince("eleven", start) == 4100, "...one that counted more (the website, another app) raises it");
        clock = clock.AddDays(400);
        again.Record("eleven", "ElevenLabs", "credits", "you", 1);
        Check(again.UsedSince("eleven", clock.Date) == 1, "a year on, the old days have gone and a new day counts alone");

        // Review of 5262c60: a damaged record (an emptied list in it) neither throws nor stops anything.
        string damaged = Path.Combine(dir, "voice_ledger_damaged.json");
        File.WriteAllText(damaged, "{\"Books\":{\"eleven\":{\"Name\":\"ElevenLabs\",\"Unit\":\"credits\",\"Days\":null,\"Line\":null,\"Purposes\":null,\"Recent\":null,\"Hours\":null}},\"PlayMinutes\":null}");
        var hurt = new VoiceLedger(damaged) { Now = () => clock };
        bool ok = true;
        try { hurt.Record("eleven", "ElevenLabs", "credits", "you", 12); for (int m = 0; m < 60; m++) hurt.PlayMinute(); }
        catch { ok = false; }
        Check(ok && hurt.UsedSince("eleven", clock.Date) == 12 && hurt.Of("eleven")!.Lines == 1, "a damaged record with emptied parts is repaired, not a failure");
        Check(!(VoiceProviderList.Parse(new[] { "    (https://api.elevenlabs.io, el-key, 50, limit: lots, nova:female)," }, new List<string>()).Single().Voices.Contains("limit")),
              "a limit that isn't understood never becomes a voice name");

        Console.WriteLine("-- 0.3.7 FB-41: a provider's own limit, and its account");
        var fake = new ElevenFake();
        var now = new DateTime(2026, 10, 20, 12, 0, 0);
        var eleven = new VoiceSlot { Number = 1, Provider = VoiceProvider.ElevenLabs, Chance = 1, ApiKey = "el-test", Url = "https://api.elevenlabs.io", Limit = 5070, LimitPer = "month" };
        var vledger = new VoiceLedger() { Now = () => now };
        var svc = new VoiceService(() => new VoiceSettings { Enabled = true, TimeoutSeconds = 5, Slots = { eleven } },
            new VoiceBook(Path.Combine(dir, "voices037.json")), _ => { }, new HttpMessageInvoker(fake)) { Ledger = vledger, Now = () => now };
        var who = new VoiceSubject { Id = "p_037", Presentation = "female", Purpose = "you" };
        var a = await svc.SpeakWithOutcomeAsync(who, "Hello there, how are you?", CancellationToken.None);
        var acc = svc.AccountOf(eleven);
        Check(a.Audio != null && acc != null && acc.Value.used == 5000 && acc.Value.limit == 10000 && svc.PeriodOf(eleven).reset == fake.Reset,
              "ElevenLabs' account is read: what it has used, its plan's amount, its reset; the month counts from it");
        Check(svc.UsedThisPeriod(eleven) == 37 && svc.AccountNow(eleven) is { } accNow && accNow.used == 5037 && accNow.limit == 10000
              && vledger.Of(VoiceService.LedgerKey(eleven))!.Purposes.ContainsKey("you"),
              "the line cost what ElevenLabs said (37): yours, counted on this PC (FB-59); the account's own count (everyone using its key) is 5037");
        eleven.Limit = 70; // FB-59: your limit counts your own use: two lines of 37 pass it
        var b = await svc.SpeakWithOutcomeAsync(who, "Still here.", CancellationToken.None);
        int asked = fake.Speech;
        var c = await svc.SpeakWithOutcomeAsync(who, "One more?", CancellationToken.None);
        Check(b.Audio != null && c.Audio == null && fake.Speech == asked && c.Error != null && c.Error.Contains("reached its limit") && c.Error.Contains("Nov 3"),
              "at its limit nothing more is asked of it; with no other provider the line goes on as text, and says why and when it starts again: " + c.Error);
        Check(svc.OverLimit(eleven) && svc.IsDown(eleven), "...it counts as set aside until its period starts again");
        now = new DateTime(2026, 11, 4, 10, 0, 0);
        fake.AccountUsed = 0;
        // Its account isn't read again in time (it's set aside, nothing is spoken through it): the period still follows the
        // account's own reset day, a month on, not the calendar (review of 5262c60).
        Check(svc.PeriodOf(eleven).start == fake.Reset && svc.PeriodOf(eleven).reset == fake.Reset.AddMonths(1),
              "a reset date that has passed rolls on a month: the account's own month, not the calendar's");
        Check(!svc.OverLimit(eleven) && svc.UsedThisPeriod(eleven) < eleven.Limit, "a new period: it speaks again");

        // Two providers: the one at its limit has its people speak in a stand-in voice from the other.
        now = new DateTime(2026, 10, 20, 12, 0, 0);
        var openai = new VoiceSlot { Number = 2, Provider = VoiceProvider.OpenAI, Chance = 0.001, ApiKey = "sk-test", Url = "https://api.openai.com/v1" };
        var counting = new CountingSpeech();
        var both = new VoiceService(() => new VoiceSettings { Enabled = true, TimeoutSeconds = 5, Slots = { eleven, openai } },
            new VoiceBook(Path.Combine(dir, "voices037b.json")), _ => { }, new HttpMessageInvoker(new Router(fake, counting))) { Ledger = new VoiceLedger() { Now = () => now }, Now = () => now };
        var who2 = new VoiceSubject { Id = "p_037b", Presentation = "female", Purpose = "chatter" };
        var first = await both.SpeakWithOutcomeAsync(who2, "First line here.", CancellationToken.None);
        eleven.Limit = 1;
        var second = await both.SpeakWithOutcomeAsync(who2, "Second line here.", CancellationToken.None);
        Check(first.Audio != null && first.Provider.StartsWith("ElevenLabs") && second.Audio != null && second.Provider.StartsWith("OpenAI") && counting.Speech == 1,
              $"over its limit, its people speak in a stand-in voice from the other provider ({first.Provider} -> {second.Provider})");
    }

    /// <summary>Sends ElevenLabs' requests to one fake and everything else to another.</summary>
    private sealed class Router : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _eleven, _rest;
        public Router(HttpMessageHandler eleven, HttpMessageHandler rest) { _eleven = new HttpMessageInvoker(eleven); _rest = new HttpMessageInvoker(rest); }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            request.RequestUri!.Host.Contains("elevenlabs") ? _eleven.SendAsync(request, ct) : _rest.SendAsync(request, ct);
    }
}
