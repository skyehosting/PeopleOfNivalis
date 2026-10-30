using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Voice;

namespace NivalisAIChat.Tests;

/// <summary>
/// Step 4b of the audit (B6: #1, RACE-3/ROBUST-9, LEAK-6): a voice is never waited on past its deadline, even while the
/// provider's voice list hangs, and a provider that's too slow is set aside so the next line comes from another one.
/// </summary>
internal static partial class Program
{
    /// <summary>ElevenLabs whose voice list (or speech) can hang until cancelled; OpenAI speech answers at once.</summary>
    private sealed class SlowVoices : HttpMessageHandler
    {
        public bool ListHangs, ListEmpty, ElevenSpeechHangs;
        public int ListAsked;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.ToString();
            byte[] body;
            if (url.Contains("/v2/voices"))
            {
                Interlocked.Increment(ref ListAsked);
                if (ListHangs) await Task.Delay(Timeout.Infinite, ct);
                body = Encoding.UTF8.GetBytes(ListEmpty ? "{\"voices\":[],\"has_more\":false}"
                    : "{\"voices\":[{\"voice_id\":\"v-rachel\",\"name\":\"Rachel\",\"category\":\"premade\",\"labels\":{\"gender\":\"female\"}}],\"has_more\":false}");
            }
            else if (url.Contains("/v1/user/subscription")) body = Encoding.UTF8.GetBytes("{\"tier\":\"creator\"}");
            else if (url.Contains("/v1/text-to-speech/"))
            {
                if (ElevenSpeechHangs) await Task.Delay(Timeout.Infinite, ct);
                body = new byte[4800];
            }
            else body = new byte[4800]; // OpenAI /audio/speech: pcm
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }
    }

    /// <summary>Within <paramref name="seconds"/> (null if it took longer: the test doesn't wait out a 30 s hang).</summary>
    private static async Task<(VoiceOutcome? outcome, double took)> Timed(Task<VoiceOutcome> speak, double seconds)
    {
        var clock = Stopwatch.StartNew();
        var done = await Task.WhenAny(speak, Task.Delay(TimeSpan.FromSeconds(seconds)));
        return done == speak ? (await speak, clock.Elapsed.TotalSeconds) : (null, clock.Elapsed.TotalSeconds);
    }

    private static async Task VoiceTimeoutTests(string dir)
    {
        Console.WriteLine("-- audit 2026-10-11: voices never wait past their deadline; a slow provider is set aside (step 4b)");
        var eleven = new VoiceSlot { Number = 1, Provider = VoiceProvider.ElevenLabs, Chance = 1, ApiKey = "xi" };

        // ROBUST-9: someone with a voice of their own, while the provider's voice list hangs: their lines are spoken at once,
        // in their own voice (the list only confirms the voice is still offered; it's never waited on), line after line.
        {
            var http = new SlowVoices { ListHangs = true };
            var book = new VoiceBook(Path.Combine(dir, "voice-kept.json"));
            var svc = new VoiceService(() => new VoiceSettings { Enabled = true, TimeoutSeconds = 3, Slots = { eleven } }, book, _ => { }, new HttpMessageInvoker(http));
            book.Set("p_kept", new VoiceAssignment { Provider = "ElevenLabs", Url = eleven.NormalizedUrl, VoiceId = "v-rachel", VoiceName = "Rachel", Gender = "female" }, book.Generation("p_kept"));
            var who = new VoiceSubject { Id = "p_kept", Presentation = "female" };
            var (first, took1) = await Timed(svc.SpeakWithOutcomeAsync(who, "Hello there.", CancellationToken.None), 10);
            var (second, took2) = await Timed(svc.SpeakWithOutcomeAsync(who, "Still here.", CancellationToken.None), 10);
            Check(first?.Audio != null && second?.Audio != null && took1 < 2 && took2 < 2,
                  $"their voice list hangs: their lines are spoken at once in their own voice, line after line, not after the list's 30 s (ROBUST-9: {took1:0.0} s, {took2:0.0} s)");
        }

        // Someone being given a voice for the first time needs the list to choose from: the line gives up at its deadline.
        {
            var http = new SlowVoices { ListHangs = true };
            var book = new VoiceBook(Path.Combine(dir, "voice-new.json"));
            var svc = new VoiceService(() => new VoiceSettings { Enabled = true, TimeoutSeconds = 3, Slots = { eleven } }, book, _ => { }, new HttpMessageInvoker(http));
            var (outcome, took) = await Timed(svc.SpeakWithOutcomeAsync(new VoiceSubject { Id = "p_new", Presentation = "female" }, "Hello there.", CancellationToken.None), 10);
            Check(outcome is { TimedOut: true } && took < 5, $"...and while a voice is being chosen for someone new, the line gives up at its deadline (3 s), not the list's 30 s ({took:0.0} s)");
        }

        // A list that came back empty isn't fetched again for every line: it's kept a minute.
        {
            var http = new SlowVoices { ListEmpty = true };
            var book = new VoiceBook(Path.Combine(dir, "voice-empty.json"));
            var svc = new VoiceService(() => new VoiceSettings { Enabled = true, TimeoutSeconds = 3, Slots = { eleven } }, book, _ => { }, new HttpMessageInvoker(http));
            for (int i = 0; i < 3; i++) await Timed(svc.SpeakWithOutcomeAsync(new VoiceSubject { Id = "p_empty" + i, Presentation = "female" }, "Hi.", CancellationToken.None), 10);
            Check(http.ListAsked == 1, $"a voice list that came back empty is kept a minute, not fetched again for each line (asked {http.ListAsked} time(s) for 3 lines)");
        }

        // A provider too slow to speak is set aside: the next line comes in a stand-in voice from the other provider.
        {
            var http = new SlowVoices { ElevenSpeechHangs = true };
            var openai = new VoiceSlot { Number = 2, Provider = VoiceProvider.OpenAI, Chance = 1, ApiKey = "sk-test" };
            var book = new VoiceBook(Path.Combine(dir, "voice-failover.json"));
            var svc = new VoiceService(() => new VoiceSettings { Enabled = true, TimeoutSeconds = 3, Slots = { eleven, openai } }, book, _ => { }, new HttpMessageInvoker(http));
            book.Set("p_fail", new VoiceAssignment { Provider = "ElevenLabs", Url = eleven.NormalizedUrl, VoiceId = "v-rachel", VoiceName = "Rachel", Gender = "female" }, book.Generation("p_fail"));
            var who = new VoiceSubject { Id = "p_fail", Presentation = "female" };
            var (first, _) = await Timed(svc.SpeakWithOutcomeAsync(who, "Hello there.", CancellationToken.None), 10);
            var (second, _) = await Timed(svc.SpeakWithOutcomeAsync(who, "Still here.", CancellationToken.None), 10);
            Check(first is { TimedOut: true } && svc.IsDown(eleven) && second?.Audio != null && book.Get("p_fail")?.StandIn?.Provider == "OpenAI",
                  "a provider too slow to speak is set aside, and the next line comes from the other one (a stand-in of the same gender)");
        }
    }
}
