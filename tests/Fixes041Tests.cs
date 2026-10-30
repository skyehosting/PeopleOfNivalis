using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Tests;

/// <summary>0.4.1: a server that takes the answer's shape without keeping to it (Ollama's cloud models) is asked again with
/// the shape in words, and from then on always; an answer that still can't be read says why.</summary>
internal static partial class Program
{
    private const string ShapeWords = "Reply with a single JSON object only";

    private static string OpenAiAnswer(string content, string finish = "stop") =>
        new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = content }, ["finish_reason"] = finish }) }.ToJsonString();

    private static bool AsksInWords(JsonObject body) => body.ToJsonString().Contains(ShapeWords);

    private static async Task Fixes041Tests()
    {
        Console.WriteLine("-- 0.4.1: a server that doesn't keep to the answer's shape");
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string" } } };
        LlmRequest Ask() => new() { Purpose = "persona", System = { "Describe them." }, Messages = { new LlmMessage("user", "Who is this?") }, JsonSchema = schema };
        var settings = new LlmSettings { Format = ApiFormat.OpenAI, Url = "http://pi-247:11434/v1/chat/completions", Model = "gemma4:31b-cloud", ApiKey = "" };

        // Ollama's cloud: response_format taken, plain words back, unless the shape is in the request's words.
        var cloud = new FakeEndpoint { Respond = b => (200, OpenAiAnswer(AsksInWords(b) ? "{\"name\":\"Mara Quill\"}" : "Mara Quill is a tired dock worker with a quick laugh.")) };
        var logged = new List<string>();
        var client = new LlmClient(() => settings, logged.Add, new HttpMessageInvoker(cloud));
        var first = await client.CompleteAsync(Ask(), CancellationToken.None);
        Check(first.Ok && LlmClient.ExtractJsonObject(first.Text)?["name"]?.GetValue<string>() == "Mara Quill",
              $"plain words for a JSON answer: asked again with the shape in words, and that answer is used ({first.Text})");
        Check(cloud.Requests.Count == 2 && !AsksInWords(cloud.Requests[0].body) && AsksInWords(cloud.Requests[1].body) && cloud.Requests[1].body["response_format"] != null,
              "...the first request as before, the second with the shape in words too (response_format still sent)");
        Check(logged.Count(l => l.Contains("didn't keep to it")) == 1, "...said once in the log");
        await client.CompleteAsync(Ask(), CancellationToken.None);
        Check(cloud.Requests.Count == 3 && AsksInWords(cloud.Requests[2].body), "from then on every request has the shape in words: one request each");

        // Kept per address and model: another model on the same client starts without the words (review of 0.4.1).
        var other = new LlmSettings { Format = ApiFormat.OpenAI, Url = settings.Url, Model = "gemma4:31b", ApiKey = "" };
        var current = settings;
        var switching = new FakeEndpoint { Respond = b => (200, OpenAiAnswer("{\"name\":\"Ivo Brand\"}")) };
        var switchClient = new LlmClient(() => current, _ => { }, new HttpMessageInvoker(new ShapeOnce(switching)));
        await switchClient.CompleteAsync(Ask(), CancellationToken.None); // plain words once: the words for this model from now on
        current = other;
        await switchClient.CompleteAsync(Ask(), CancellationToken.None);
        Check(switching.Requests.Count >= 1 && !AsksInWords(switching.Requests.Last().body), "another model on the same address starts without the shape in words");

        // Two at once, both answered in plain words before either learned it: each is asked again once.
        var pair = new PairedCloud();
        var pairClient = new LlmClient(() => settings, _ => { }, new HttpMessageInvoker(pair));
        var both = await Task.WhenAll(pairClient.CompleteAsync(Ask(), CancellationToken.None), pairClient.CompleteAsync(Ask(), CancellationToken.None));
        Check(both.All(r => LlmClient.ExtractJsonObject(r.Text) != null) && pair.Count == 4, $"two requests at once in plain words: both asked again, both read ({pair.Count} requests)");

        // A server that keeps to it: nothing changes (one request, no words added).
        var kept = new FakeEndpoint { Respond = _ => (200, OpenAiAnswer("{\"name\":\"Ivo Brand\"}")) };
        var keptClient = new LlmClient(() => settings, _ => { }, new HttpMessageInvoker(kept));
        await keptClient.CompleteAsync(Ask(), CancellationToken.None);
        await keptClient.CompleteAsync(Ask(), CancellationToken.None);
        Check(kept.Requests.Count == 2 && !kept.Requests.Any(r => AsksInWords(r.body)), "a server that keeps to the shape: one request each, no words added");

        // Cut off: not asked again (more of the same wouldn't fit either), and it says so.
        var cut = new FakeEndpoint { Respond = _ => (200, OpenAiAnswer("{\"name\":\"Ivo Br", "length")) };
        var cutResult = await new LlmClient(() => settings, _ => { }, new HttpMessageInvoker(cut)).CompleteAsync(Ask(), CancellationToken.None);
        Check(cut.Requests.Count == 1 && cutResult.CutOff && cutResult.WhyUnreadable().Contains("MaxGenerationTokens"), "an answer cut off isn't asked again; it says to raise MaxGenerationTokens");
        Check(new LlmResult { Ok = true, Text = "" }.WhyUnreadable().Contains("empty") && new LlmResult { Ok = true, Text = "Hello there" }.WhyUnreadable().Contains("plain words"),
              "...an empty answer and plain words each say so");
        // A request without a shape (a plain line of chatter): never asked again.
        var plain = new FakeEndpoint { Respond = _ => (200, OpenAiAnswer("Just a line.")) };
        await new LlmClient(() => settings, _ => { }, new HttpMessageInvoker(plain)).CompleteAsync(new LlmRequest { Messages = { new LlmMessage("user", "Say something.") } }, CancellationToken.None);
        Check(plain.Requests.Count == 1, "a request that asks for no shape is never asked again");
        // A chat reply (plain words with [[tags]] are read too): never asked again, even for a shape.
        var chatty = new FakeEndpoint { Respond = _ => (200, OpenAiAnswer("Long day, chief. [[mood: tired]]")) };
        var chatReq = Ask(); chatReq.Purpose = "chat"; chatReq.PlainTextOk = true;
        var chatAnswer = await new LlmClient(() => settings, _ => { }, new HttpMessageInvoker(chatty)).CompleteAsync(chatReq, CancellationToken.None);
        Check(chatty.Requests.Count == 1 && chatAnswer.Text.StartsWith("Long day"), "a chat reply in plain words is used as it is: one request");
        // Anthropic format: the shape in words too once it isn't kept to.
        var anthroSettings = new LlmSettings { Format = ApiFormat.Anthropic, Url = "http://localhost:8080/v1/messages", Model = "some-model", ApiKey = "k" };
        string AnthropicAnswer(string text) => new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["stop_reason"] = "end_turn" }.ToJsonString();
        var proxy = new FakeEndpoint { Respond = b => (200, AnthropicAnswer(AsksInWords(b) ? "{\"name\":\"Lena Hart\"}" : "Lena Hart, a baker.")) };
        var viaProxy = await new LlmClient(() => anthroSettings, _ => { }, new HttpMessageInvoker(proxy)).CompleteAsync(Ask(), CancellationToken.None);
        Check(proxy.Requests.Count == 2 && LlmClient.ExtractJsonObject(viaProxy.Text) != null, "an Anthropic-format server that doesn't keep to it: asked again with the shape in words");

        // Someone new, from a server that only ever answers in plain words: the reason is in the message (still AI-READ).
        var stubborn = new FakeEndpoint { Respond = _ => (200, OpenAiAnswer("A tall woman with a red coat.")) };
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pon-tests-041-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new NpcStore(dir, _ => { });
            var brain = new NpcBrain(new LlmClient(() => settings, _ => { }, new HttpMessageInvoker(stubborn)), store,
                                     () => new BrainSettings { LifeUpdates = false, MeetingSummaries = false }, _ => { });
            var (made, _, error) = await brain.BeginAsync("p_plain_words", new NpcVisual { Body = "woman" }, new GameSnapshot { GameDay = 3, Hour = 12 }, CancellationToken.None);
            Check(made == null && error != null && error.Contains("couldn't be read") && error.Contains("plain words") && ProblemCodes.CodeFor(error) == "AI-READ",
                  $"someone new from plain words only: not created, and the message says why ({error})");
            if (made != null) { brain.End(made); await brain.Settled("p_plain_words"); }
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>The very first request is answered in plain words; the rest go to <c>inner</c>.</summary>
    private sealed class ShapeOnce : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _inner;
        private int _n;
        public ShapeOnce(HttpMessageHandler inner) { _inner = new HttpMessageInvoker(inner); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _n) == 1)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OpenAiAnswer("Plain words."), Encoding.UTF8, "application/json") };
            return await _inner.SendAsync(request, ct);
        }
    }

    /// <summary>Ollama's cloud, answering two requests at once: both first answers (plain words) wait until both have arrived.</summary>
    private sealed class PairedCloud : HttpMessageHandler
    {
        private int _count, _plain;
        private readonly TaskCompletionSource<bool> _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count => Volatile.Read(ref _count);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _count);
            bool words = (await request.Content!.ReadAsStringAsync(ct)).Contains(ShapeWords);
            if (!words)
            {
                if (Interlocked.Increment(ref _plain) == 2) _both.TrySetResult(true);
                await Task.WhenAny(_both.Task, Task.Delay(5000, ct));
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OpenAiAnswer(words ? "{\"name\":\"Ada Venn\"}" : "Ada Venn is kind."), Encoding.UTF8, "application/json") };
        }
    }
}
