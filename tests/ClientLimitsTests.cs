using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;

namespace NivalisAIChat.Tests;

/// <summary>
/// Step 5 of the audit, E2 and E3: a huge TimeoutSeconds ("never time out"), a Temperature that isn't a number, or an
/// ApiUrl without http(s):// used to throw on every request (or send something the provider rejects). Now the setting is
/// held in range, or the request fails with a clear message and nothing is sent.
/// </summary>
internal static partial class Program
{
    /// <summary>What HttpClient itself throws for an address it can't use (a fake invoker never would).</summary>
    private sealed class ThrowingEndpoint : HttpMessageHandler
    {
        public Exception Throw = new NotSupportedException("The 'localhost' scheme is not supported.");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw Throw;
    }

    private static async Task ClientLimitsTests()
    {
        Console.WriteLine("-- audit 2026-10-11: AI settings out of range; an ApiUrl without http(s):// (step 5, E2/E3)");
        LlmRequest Ask()
        {
            var rq = new LlmRequest { MaxTokens = 20 };
            rq.System.Add("x");
            rq.Messages.Add(new LlmMessage("user", "hi"));
            return rq;
        }
        async Task<string> Outcome(LlmClient client)
        {
            try { var r = await client.CompleteAsync(Ask(), CancellationToken.None); return r.Ok ? "ok" : "failed: " + r.Error; }
            catch (Exception e) { return "threw " + e.GetType().Name; }
        }

        // E2: "never time out", written as a huge number.
        var fake = new FakeEndpoint { Respond = _ => (200, AnthropicText("Mm.")) };
        string huge = await Outcome(new LlmClient(() => new LlmSettings { ApiKey = "k", TimeoutSeconds = 99999999 }, _ => { }, new HttpMessageInvoker(fake)));
        Check(huge == "ok", $"TimeoutSeconds 99999999 doesn't break every request ({huge})");

        // E2: a Temperature that isn't a number is left out; one too high is held at 2.
        foreach (var (temp, want) in new[] { (float.PositiveInfinity, (double?)null), (float.NaN, null), (5f, 2.0), (0.7f, 0.7) })
        {
            var openai = new FakeEndpoint { Respond = _ => (200, OpenAiText("Mm.")) };
            string o = await Outcome(new LlmClient(() => new LlmSettings { ApiKey = "k", Format = ApiFormat.OpenAI, Temperature = temp }, _ => { }, new HttpMessageInvoker(openai)));
            var body = openai.Requests.Count > 0 ? openai.Requests[^1].body : null;
            double? sent = body?["temperature"]?.GetValue<double>();
            Check(o == "ok" && sent == want, $"Temperature {temp}: request {o}, sent {sent?.ToString() ?? "none"} (want {want?.ToString() ?? "none"})");
        }

        // E3: an ApiUrl without http(s):// fails clearly, and nothing is sent.
        foreach (var url in new[] { "api.example.com/v1", "localhost:11434/v1" })
        {
            var endpoint = new FakeEndpoint { Respond = _ => (200, AnthropicText("Mm.")) };
            string o = await Outcome(new LlmClient(() => new LlmSettings { ApiKey = "k", Url = url }, _ => { }, new HttpMessageInvoker(endpoint)));
            Check(o.StartsWith("failed: [AI-URL]") && endpoint.Requests.Count == 0,
                $"ApiUrl \"{url}\": a clear [AI-URL] message and no request ({o}; {endpoint.Requests.Count} request(s))");
        }
        foreach (var url in new[] { "https://api.example.com/v1", "http://localhost:11434/v1", "HTTPS://API.EXAMPLE.COM/v1" })
        {
            var endpoint = new FakeEndpoint { Respond = _ => (200, AnthropicText("Mm.")) };
            string o = await Outcome(new LlmClient(() => new LlmSettings { ApiKey = "k", Url = url }, _ => { }, new HttpMessageInvoker(endpoint)));
            Check(o == "ok" && endpoint.Requests.Count == 1, $"ApiUrl \"{url}\" works as before (guard): {o}");
        }

        // What the real HttpClient throws for an address it can't use is a failure, never an escape.
        foreach (var thrown in new Exception[] { new NotSupportedException("The 'localhost' scheme is not supported."), new InvalidOperationException("An invalid request URI was provided.") })
        {
            string o = await Outcome(new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(new ThrowingEndpoint { Throw = thrown })));
            Check(o.StartsWith("failed: [AI-URL]"), $"{thrown.GetType().Name} while sending is a clear failure, not an exception ({o})");
        }
    }
}
