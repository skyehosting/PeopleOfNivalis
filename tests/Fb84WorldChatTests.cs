using System;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Tests;

/// <summary>FB-84: world chat says why nobody answers (out of credit...), and logs it, instead of "They couldn't be read".</summary>
internal static partial class Program
{
    private static async Task Fb84WorldChatTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- FB-84: world chat says why nobody answers");
        string dir = TestRoot.NewFolder("fb84");
        var broke = new FakeEndpoint();
        broke.Respond = _ => (429, "{\"error\":{\"message\":\"You have no credits remaining. Add credits to continue.\"}}");
        var store = new NpcStore(Path.Combine(dir, "npcs"), _ => { });
        var brain = new NpcBrain(new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(broke)), store, () => new BrainSettings(), _ => { });
        string? why = null;
        var nobody = await brain.EnsurePersonAsync("extra_9_Woman_a", new NpcVisual { Body = "woman" }, new GameSnapshot(), CancellationToken.None, e => why = e);
        Check(nobody == null && why != null && why.Contains("429") && ProblemCodes.CodeFor(why) == "AI-LIMIT" && !store.Has("extra_9_Woman_a"),
              "FB-84: someone who couldn't be made (the AI out of credit) says why: an AI-LIMIT error, nothing saved");
        var older = await brain.EnsurePersonAsync("extra_9_Woman_b", new NpcVisual { Body = "woman" }, new GameSnapshot(), CancellationToken.None);
        Check(older == null, "...and the callers that don't ask why are as before");

        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string wc = File.ReadAllText(Path.Combine(src, "ModRuntime.WorldChat.cs"));
        Check(wc.Contains("brain.EnsurePersonAsync(id, visual, snap, ct, e => why ??= e)") && wc.Contains("WorldAsk(m, text, addressed, presentIds, got, why)")
              && wc.Contains("why is { Length: > 0 } ? WorldProblem(\"nobody could answer\", why) : \"They couldn't be read: nobody answers.\""),
              "...world chat carries it through: when nobody could be made, the line under the box says why");
        Check(wc.Contains("WorldLine.SetStatus(WorldProblem(\"no answer\", reply.Error ?? \"no answer\"));") && !wc.Contains("SetStatus(\"No answer: \"")
              && wc.Contains("ProblemCodes.FirstSentence(Problem($\"World chat: {what}: {Redact.Hide(error, _cfg.ApiKey.Value)}\"));"),
              "...a refused reply too; both are logged and kept for /errors with their code, the key hidden");
        string full = ProblemCodes.Coded("World chat: nobody could answer: AI endpoint error 429: You have no credits remaining. Add credits to continue.");
        Check(ProblemCodes.FirstSentence(full) == "[AI-LIMIT] World chat: nobody could answer: AI endpoint error 429: You have no credits remaining.",
              "...the line under the box is its first sentence, with the code: \"[AI-LIMIT] ... no credits remaining.\"");
        string longOne = ProblemCodes.FirstSentence("[AI-SERVER] " + new string('x', 300), 140);
        Check(longOne.Length == 140 && longOne.EndsWith("…") && ProblemCodes.FirstSentence("Short one.") == "Short one." && ProblemCodes.FirstSentence("") == "",
              "...and never longer than the line (review FB84-1: one with no full stop is cut with \"…\")");
        Check(Redact.Hide("AI endpoint error 401: bad key my-odd-key-123", "my-odd-key-123") == "AI endpoint error 401: bad key (hidden)",
              "...the key, whatever its shape, is hidden before it's logged or shown");
    }
}
