using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Phone;

namespace NivalisAIChat.Tests;

/// <summary>Nightline (FB-71): the visit's answer held to what was asked, what they remember, the photos' client, the city's shots.</summary>
internal static partial class Program
{
    private sealed class ImageEndpoint : HttpMessageHandler
    {
        public readonly List<(string url, string auth, string contentType, string body)> Requests = new();
        public Func<string, (int code, string body)> Respond = _ => (200, "{}");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            string body = r.Content == null ? "" : await r.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add((r.RequestUri!.ToString(), r.Headers.Authorization?.ToString() ?? "", r.Content?.Headers.ContentType?.MediaType ?? "", body));
            var (code, text) = Respond(body);
            return new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task NightlineMoreTests([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        Console.WriteLine("-- Nightline: the visit's answer, memory, photos, street views (FB-71)");

        // What the AI says is held to what was asked.
        var seen = new List<Lines.Seen>
        {
            new() { Key = "L1", LineId = "a", Author = "p_kiko", AuthorName = "Kiko", Handle = "kiko", Text = "Rain special.", MayReact = true, MayComment = true,
                    Comments = { ("L1.c1", "Mara", "maravoss", "Save me one?") } },
            new() { Key = "L2", LineId = "b", Author = "p_dax", AuthorName = "Dax", Handle = "dax", Text = "Water prices.", MayReact = true, MayComment = false },
            new() { Key = "L3", LineId = "c", Author = "me", AuthorName = "Me", Handle = "me", Text = "My own.", Own = true, MayComment = true,
                    Comments = { ("L3.c1", "Juno", "juno", "Cute!") }, NewReactions = { ("Juno", true), ("Dax", false) } },
        };
        string answer = "{\"handle\":\"@Rain.Lover!\",\"post\":{\"text\":\"Long day 🌧 at [the docks]\",\"photo\":{\"shows\":\"cranes in the rain\",\"area\":\"the docks\",\"me\":true}}," +
                        "\"lines\":[{\"line\":\"L1\",\"react\":\"like\",\"comment\":\"Yes please\",\"reply_to\":\"L1.c1\",\"why\":\"hungry\"}," +
                        "{\"line\":\"L2\",\"react\":\"dislike\",\"comment\":\"Ugh\",\"reply_to\":\"\",\"why\":\"tired of it\"}," +
                        "{\"line\":\"L3\",\"react\":\"like\",\"comment\":\"Thanks!\",\"reply_to\":\"L3.c1\",\"why\":\"\"}," +
                        "{\"line\":\"L9\",\"react\":\"like\",\"comment\":\"ghost\",\"reply_to\":\"\",\"why\":\"\"},{\"line\":\"L1\",\"react\":\"dislike\",\"comment\":\"twice\",\"reply_to\":\"\",\"why\":\"\"}]}";
        var r = Lines.Read(answer, seen, post: true, new[] { "The Docks", "Lowtown" });
        Check(r.Ok && r.Handle == "rain.lover" && r.Post == "Long day at (the docks)" && r.PhotoArea == "The Docks" && r.PhotoShows == "cranes in the rain" && r.PhotoOfMe,
            $"the post read safe (no emoji, no brackets), the handle made a handle, the photo's area one of the city's ({r.Post})");
        var a1 = r.Acts.Single(a => a.Key == "L1");
        var a2 = r.Acts.Single(a => a.Key == "L2");
        var a3 = r.Acts.Single(a => a.Key == "L3");
        Check(r.Acts.Count == 3 && a1.React == true && a1.Comment == "Yes please" && a1.ReplyTo == "L1.c1" && a2.React == false && a2.Comment == ""
              && a3.React == null && a3.Comment == "Thanks!" && a3.ReplyTo == "L3.c1",
            "each line once; a comment only where allowed; no reaction to their own line; a reply only to a comment that's there; an unknown line ignored");
        var noPost = Lines.Read(answer, seen, post: false, new[] { "The Docks" });
        var noArea = Lines.Read(answer, seen, post: true, Array.Empty<string>());
        Check(noPost.Post == "" && noArea.Post.Length > 0 && noArea.PhotoArea == "" && !Lines.Read("not json", seen, true, Array.Empty<string>()).Ok,
            "no post when none was asked for; no photo when no area can be one; an unreadable answer is nothing, never guessed");

        // What they remember.
        var (lines, summary) = Lines.Remembered(r, seen, "cranes in the rain");
        Check(lines.Any(l => l.Contains("You posted") && l.Contains("cranes")) && lines.Any(l => l.Contains("Kiko's line") && l.Contains("liked it") && l.Contains("hungry"))
              && lines.Any(l => l.Contains("Juno liked your line")) && lines.Any(l => l.Contains("Dax disliked your line")) && summary.StartsWith("On Nightline you"),
            "they remember what they posted (and its photo), what they liked and why, and who reacted to theirs");

        // The request: a JSON schema, residents' purpose, their own voice.
        Check(Lines.Schema()["required"]!.ToJsonString().Contains("lines") && Lines.Feed(seen, true).Contains("L1. Kiko (@kiko") && Lines.Feed(seen, false).Contains("post is null")
              && Social.UsageModel.ResidentsAi.Contains("nightline") && Social.UsageModel.ResidentsAi.Contains(ImageClient.Purpose),
            "the request lists what they saw by key; Nightline and its photos are residents' use in the Usage Optimizer");
        Check(Lines.ImagePrompt("cranes", "The Docks", true, "tall, red coat").Contains("first reference picture") && Lines.ImagePrompt("cranes", "The Docks", false, "").Contains("Adults only")
              && !Lines.ImagePrompt("cranes", "The Docks", false, "").Contains("first reference"),
            "the photo's words: the place's look from the street views; them from their own picture only when it's of them; adults only");

        // The image client: OpenAI's edits (multipart) with references, generations without; OpenRouter's JSON; a key never shown.
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");
        string ok = "{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(png) + "\"}],\"usage\":{\"total_tokens\":1234}}";
        var ep = new ImageEndpoint { Respond = _ => (200, ok) };
        var settings = new ImageSettings { Key = "img-key-123", Model = "gpt-image-1-mini" };
        var client = new ImageClient(() => settings, new HttpMessageInvoker(ep));
        var (img, err) = await client.MakeAsync("a street", new[] { png, png }, CancellationToken.None);
        var e1 = ep.Requests.Last();
        Check(img != null && img.SequenceEqual(png) && e1.url == "https://api.openai.com/v1/images/edits" && e1.contentType == "multipart/form-data" && e1.body.Contains("name=\"image[]\"")
              && e1.body.Contains("1536x1024") && e1.auth == "Bearer img-key-123",
            $"OpenAI: references go to /images/edits as image[] parts, landscape, the key as a bearer ({err})");
        await client.MakeAsync("a street", Array.Empty<byte[]>(), CancellationToken.None);
        Check(ep.Requests.Last().url.EndsWith("/images/generations") && ep.Requests.Last().contentType == "application/json", "...no references: /images/generations");
        settings.Format = "OpenRouter"; settings.Url = "https://openrouter.ai/api/v1/images";
        await client.MakeAsync("a street", new[] { png }, CancellationToken.None);
        var e3 = ep.Requests.Last();
        Check(e3.url == "https://openrouter.ai/api/v1/images" && e3.body.Contains("input_references") && e3.body.Contains("data:image/png;base64,"),
            "OpenRouter: one /images address, references as data URLs (an images path pasted in is taken off and put back once)");
        ep.Respond = _ => (401, "{\"error\":{\"message\":\"Bad key img-key-123\"}}");
        var (none, why) = await client.MakeAsync("a street", Array.Empty<byte[]>(), CancellationToken.None);
        Check(none == null && !why.Contains("img-key-123") && why.Contains("(hidden)"), $"a key the service repeats is hidden ({why})");
        ep.Respond = _ => (200, "{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(Encoding.ASCII.GetBytes("not a picture at all")) + "\"}]}");
        Check((await client.MakeAsync("x", Array.Empty<byte[]>(), CancellationToken.None)).image == null, "what isn't a PNG or JPEG is refused");
        ep.Respond = _ => (307, "");
        Check((await client.MakeAsync("x", Array.Empty<byte[]>(), CancellationToken.None)).error.Contains("ImageApiUrl"), "a redirect is never followed (the key would go along)");
        Check((await new ImageClient(() => new ImageSettings()).MakeAsync("x", Array.Empty<byte[]>(), CancellationToken.None)).error.Contains("no image service"), "no key: no request at all");
        Check(File.ReadAllText(Path.Combine(src, "Ai", "ImageClient.cs")).Contains("AllowAutoRedirect = false"), "...and its own client never follows one either");

        // Review fixes (NL-2, NL-4, NL-9, NL-11).
        string nd = TestRoot.NewFolder("nl-review"); Directory.CreateDirectory(nd);
        string stuck = Path.Combine(nd, "stuck.json");
        File.WriteAllText(stuck, "{\"Lines\":[{\"Id\":\"x\",\"Author\":\"p\",\"Text\":\"hi\",\"Developing\":true}]}");
        var sb = new LineBook(stuck);
        Check(!sb.Get("x")!.Developing && !sb.PhotoDone("gone-line", "photo-a.png", "x") && sb.PhotoDone("x", "photo-b.png", "y"),
            "NL-2/NL-4: a photo being made never survives a restart; a photo for a line that's gone isn't kept (its file is the caller's to delete)");
        sb.React("x", "p_b", "B", true, 5);
        var peek = sb.NewSince("x", out int upTo);
        Check(peek.Count == 1 && sb.NewSince("x", out _).Count == 1 && upTo == 1, "NL-9: looking at who reacted marks nothing until the visit worked...");
        sb.MarkLooked("x", upTo);
        Check(sb.NewSince("x", out _).Count == 0, "...and then it does");
        sb.Close();
        var ownNoComment = new List<Lines.Seen> { new() { Key = "L1", LineId = "x", Own = true, MayComment = false, Text = "mine" } };
        Check(Lines.Read("{\"handle\":\"\",\"post\":null,\"lines\":[{\"line\":\"L1\",\"react\":\"none\",\"comment\":\"hi\",\"reply_to\":\"\",\"why\":\"\"}]}", ownNoComment, false, Array.Empty<string>()).Acts.Count == 0,
            "NL-9: their own line gets a comment only when they were asked to answer one");
        var httpClient = new ImageClient(() => new ImageSettings { Key = "k1234", Model = "m", Url = "http://images.example.com/v1" }, new HttpMessageInvoker(new ImageEndpoint()));
        Check((await httpClient.MakeAsync("x", Array.Empty<byte[]>(), CancellationToken.None)).error.Contains("https"), "NL-11: a key never goes over plain http to a server out on the internet");

        // FB-78: a card says when it was posted (it said "now" for everyone).
        long noon15 = 15 * 1440 + 12 * 60;
        Check(Lines.PostedAt(noon15, noon15) == "now" && Lines.PostedAt(noon15, 15 * 1440 + 9 * 60 + 16) == "9:16 AM" && Lines.PostedAt(noon15, 15 * 1440 + 14 * 60 + 12) == "2:12 PM"
              && Lines.PostedAt(noon15, 15 * 1440 + 30) == "12:30 AM" && Lines.PostedAt(noon15, 14 * 1440 + 23 * 60) == "Yesterday" && Lines.PostedAt(noon15, 12 * 1440) == "Day 12",
              "FB-78: posted today shows its time, then Yesterday, then its day; under a minute old, now");
        Check(Lines.PointsAtAChild("me and a little girl.", new string[0]) && Lines.PointsAtAChild("Teenagers at the pier", new string[0]) && Lines.PointsAtAChild("Lunch with Mia!", new[] { "Mia Fern" })
              && Lines.PointsAtAChild("Mia's new bike", new[] { "Mia Fern" }) && !Lines.PointsAtAChild("Rain on the cranes at the docks", new[] { "Mia Fern" })
              && !Lines.PointsAtAChild("Mial and Kiko at the bar", new[] { "Mia Fern" }),
            "NL-7: no photo when its words point at a child or name a known minor (punctuation and possessives too); whole words only");

        // The city's street views: per area, at most so many, the oldest go; references from the place first.
        string dir = TestRoot.NewFolder("shots");
        Directory.CreateDirectory(dir);
        var shots = new ShotIndex(Path.Combine(dir, "shots.json"));
        var gone = new List<string>();
        for (int i = 0; i < 6; i++) gone.AddRange(shots.Add($"shot-{i}.jpg", "The Docks", i == 5 ? "Pier 9" : "", 1, 1000, perArea: 4, maxMb: 300));
        shots.Add("shot-x.jpg", "Lowtown", "", 1, 1000, 4, 300);
        Check(shots.Count("The Docks") == 4 && gone.SequenceEqual(new[] { "shot-0.jpg", "shot-1.jpg" }) && shots.Areas().First() == "The Docks" && shots.Add("../evil.jpg", "x", "", 1, 1, 4, 300).Count == 0 && shots.Total == 5,
            "at most so many street views an area (the oldest go first, their files given back); a bad file name is never kept");
        Check(shots.Pick("the docks", "Pier 9", 2, new Random(1))[0] == "shot-5.jpg" && shots.Pick("Nowhere", "", 3, new Random(1)).Count == 0,
            "a photo's references: the place's own first, then the area's");
        var reread = new ShotIndex(Path.Combine(dir, "shots.json"));
        Check(reread.Total == 5 && reread.Clear().Count == 5 && reread.Total == 0, "kept on the PC and read back; /nightline shots clear forgets them all");
    }
}
