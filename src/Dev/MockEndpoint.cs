using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace NivalisAIChat.Dev;

/// <summary>Canned AI answers so the in-game flow can be tested without an API key (dev command "mock on").</summary>
internal sealed class MockEndpoint : HttpMessageHandler
{
    private static readonly string[] Names = { "Odalys Brennquist", "Teodor Vasko", "Mirela Okonkwo-Hart", "Sung-min Pryce", "Wen Abara" };
    private int _n;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Delay(400, ct).ConfigureAwait(false);
        string raw = await request.Content!.ReadAsStringAsync(ct).ConfigureAwait(false);
        var body = JsonNode.Parse(raw)!.AsObject();
        bool anthropic = request.RequestUri!.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal);
        string all = raw;
        string text;
        if (all.Contains("Create the resident"))
        {
            string name = Names[Interlocked.Increment(ref _n) % Names.Length];
            text = new JsonObject
            {
                ["name"] = name, ["nickname"] = name.Split(' ')[0][..3], ["age"] = "38", ["pronouns"] = "they/them",
                ["occupation"] = "noodle-stall cook", ["workplace"] = "a stall in Meridian Market", ["livesIn"] = "Lowtown",
                ["appearance"] = "Rain-slick coat, tired eyes.", ["background"] = "Grew up in the Stacks. Moved out after a fire.",
                ["personality"] = new JsonArray("blunt", "loyal", "petty"), ["temperament"] = "cynical and dry",
                ["speech"] = new JsonObject { ["slangLevel"] = 2, ["formality"] = "casual", ["verbosity"] = "terse", ["swearing"] = "sometimes",
                    ["rhythm"] = "short", ["quirks"] = new JsonArray("calls everyone chief"), ["catchphrases"] = new JsonArray("Broth don't lie."),
                    ["favouriteWords"] = new JsonArray("chief") },
                ["values"] = new JsonArray("family"), ["fears"] = new JsonArray("the 'Sec"), ["secret"] = "Waters down the broth.",
                ["likes"] = new JsonArray("joos"), ["dislikes"] = new JsonArray("Corpsters"), ["hobbies"] = new JsonArray("pirate radio"),
                ["shortTermGoal"] = "Make rent", ["longTermGoal"] = "Own a boat", ["currentWorry"] = "Rent",
                ["people"] = new JsonArray("Teo — brother — works the rig"), ["opinionOfCorps"] = "Leeches.", ["opinionOfAndroids"] = "Fine.",
            }.ToJsonString();
        }
        else if (all.Contains("You quietly narrate"))
            text = "{\"events\":[\"Picked up extra shifts after the rent went up.\"],\"occupation\":\"\",\"workplace\":\"\",\"livesIn\":\"\",\"shortTermGoal\":\"\",\"longTermGoal\":\"\",\"currentWorry\":\"\",\"temperament\":\"\",\"moodReason\":\"tired from double shifts\"}";
        else if (all.Contains("You keep the private memory"))
            text = "I keep running into this stranger. Harmless, mostly.";
        else
        {
            var msgs = anthropic ? body["messages"]!.AsArray() : body["messages"]!.AsArray();
            string last = msgs.Last()!["content"]!.GetValue<string>();
            text = last.StartsWith("[Scene:") ? "Huh? Can I help you, chief? [[mood: curious]]"
                 : last.Contains("name", StringComparison.OrdinalIgnoreCase) ? "Name's Oda. Don't wear it out. [[gave_name]] [[affinity: +2]]"
                 : last.Contains("bye", StringComparison.OrdinalIgnoreCase) ? "Yeah, I got a stall to run. See you. [[leave]]"
                 : $"\"{Trim(last, 60)}\"? Weird thing to say, chief. [[affinity: +1]]";
        }
        string resp = anthropic
            ? new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["stop_reason"] = "end_turn" }.ToJsonString()
            : new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text }, ["finish_reason"] = "stop" }) }.ToJsonString();
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(resp, Encoding.UTF8, "application/json") };
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
