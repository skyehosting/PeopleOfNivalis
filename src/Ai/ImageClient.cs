using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace NivalisAIChat.Ai;

/// <summary>Where Nightline's photos are made (FB-71): the player's own image service, model and key.</summary>
public sealed class ImageSettings
{
    /// <summary>"OpenAI" (its images API, or any server shaped like it) or "OpenRouter".</summary>
    public string Format = "OpenAI";
    public string Url = "";
    public string Key = "";
    public string Model = "gpt-image-1-mini";
    /// <summary>low, medium or high (OpenAI); passed through to OpenRouter.</summary>
    public string Quality = "low";
    public bool Ready => Key.Trim().Length > 0 && Model.Trim().Length > 0;
}

/// <summary>
/// Makes a photo from words and a few reference pictures (the city's own street views, and the poster when it's of them):
/// OpenAI's /images/edits (multipart, image[]) or /images/generations with no references; OpenRouter's /images (JSON,
/// input_references). Landscape (1536x1024), base64 back (docs/research/strut.md). Redirects are never followed (a key
/// header would go along), a key the service repeats is hidden, and every photo is counted: the ledger by purpose
/// ("nightline photo"), and the token limit. Never sees anyone under 18: the caller sends street views with no people in
/// them and only an adult's own picture.
/// </summary>
public sealed class ImageClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false,
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public const int TimeoutSeconds = 150, MaxReferences = 4, MaxImageBytes = 20 * 1024 * 1024;
    /// <summary>What a photo counts for when the service doesn't say (about a low-quality landscape picture's tokens).</summary>
    public const long EstimateTokens = 1600;
    public const string Purpose = "nightline photo";

    private readonly Func<ImageSettings> _settings;
    private readonly HttpMessageInvoker? _invoker;
    public TokenBudget? Budget;
    public Voice.VoiceLedger? Ledger;

    public ImageClient(Func<ImageSettings> settings, HttpMessageInvoker? invoker = null)
    {
        _settings = settings;
        _invoker = invoker;
    }

    /// <summary>The service's base address: what the player wrote, without an images path on the end, or the format's own.</summary>
    public static string BaseUrl(ImageSettings s)
    {
        string u = (s.Url ?? "").Trim().TrimEnd('/');
        foreach (var tail in new[] { "/images/edits", "/images/generations", "/images" })
            if (u.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) { u = u[..^tail.Length]; break; }
        if (u.Length == 0) u = IsOpenRouter(s) ? "https://openrouter.ai/api/v1" : "https://api.openai.com/v1";
        return u;
    }

    public static bool IsOpenRouter(ImageSettings s) => (s.Format ?? "").Trim().Equals("OpenRouter", StringComparison.OrdinalIgnoreCase);

    /// <summary>The photo (PNG or JPEG bytes), or an error that never holds the key. References are JPEG or PNG bytes.</summary>
    public async Task<(byte[]? image, string error)> MakeAsync(string prompt, IReadOnlyList<byte[]> references, CancellationToken ct)
    {
        var s = _settings();
        if (!s.Ready) return (null, "no image service set up (ImageApiKey and ImageModel)");
        if (Budget != null && !Budget.Allows()) return (null, "the usage limit is reached");
        string key = s.Key.Trim(), model = s.Model.Trim(), baseUrl = BaseUrl(s);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) || (root.Scheme != "https" && root.Scheme != "http"))
            return (null, "ImageApiUrl isn't a web address");
        // A key only ever goes over plain http to a server on this PC or the LAN (review NL-11).
        if (root.Scheme == "http" && !Phone.Wallpaper.LocalHost(root)) return (null, "ImageApiUrl must be https:// (plain http only for a server on this PC or your network)");
        var refs = references.Where(r => r is { Length: > 0 } && r.Length <= MaxImageBytes).Take(MaxReferences).ToList();
        using var req = IsOpenRouter(s) ? OpenRouter(baseUrl, model, prompt, refs, s.Quality) : OpenAi(baseUrl, model, prompt, refs, s.Quality);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        string text;
        int code;
        try
        {
            using var resp = _invoker != null ? await _invoker.SendAsync(req, cts.Token).ConfigureAwait(false) : await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            code = (int)resp.StatusCode;
            if (resp.Content.Headers.ContentLength is long len && len > MaxImageBytes * 2L) return (null, "the image service's answer was too big");
            text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (text.Length > MaxImageBytes * 2) return (null, "the image service's answer was too big");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, $"the image service took longer than {TimeoutSeconds} s"); }
        catch (HttpRequestException e) { return (null, "the image service couldn't be reached: " + Redact.Text(Redact.Hide(e.Message, key), model)); }
        if (code is >= 300 and < 400) return (null, $"HTTP {code}: the address sends requests somewhere else; put the address it moves to in ImageApiUrl");
        if (code < 200 || code >= 300) return (null, $"HTTP {code}: {Redact.Text(Redact.Hide(Detail(text), key), model)}");
        var (bytes, tokens, why) = Read(text);
        Count(baseUrl, model, tokens);
        return bytes != null ? (bytes, "") : (null, Redact.Text(Redact.Hide(why, key), model));
    }

    private void Count(string url, string model, long tokens)
    {
        long spent = tokens > 0 ? tokens : EstimateTokens;
        try { Budget?.Add(spent); } catch { }
        try { Ledger?.Record(LlmClient.LedgerKey(url, model), "AI (" + Redact.Value(model) + ")", "tokens", Purpose, spent); } catch { }
    }

    private static HttpRequestMessage OpenAi(string baseUrl, string model, string prompt, List<byte[]> refs, string quality)
    {
        string q = (quality ?? "").Trim().ToLowerInvariant() is "low" or "medium" or "high" or "auto" ? quality!.Trim().ToLowerInvariant() : "low";
        if (refs.Count == 0)
        {
            var body = new JsonObject { ["model"] = model, ["prompt"] = prompt, ["size"] = "1536x1024", ["quality"] = q, ["n"] = 1 };
            return new HttpRequestMessage(HttpMethod.Post, baseUrl + "/images/generations") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        }
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent(prompt), "prompt");
        form.Add(new StringContent("1536x1024"), "size");
        form.Add(new StringContent(q), "quality");
        form.Add(new StringContent("1"), "n");
        int i = 0;
        foreach (var r in refs)
        {
            bool png = IsPng(r);
            var part = new ByteArrayContent(r);
            part.Headers.ContentType = new MediaTypeHeaderValue(png ? "image/png" : "image/jpeg");
            form.Add(part, "image[]", $"ref{i++}.{(png ? "png" : "jpg")}");
        }
        return new HttpRequestMessage(HttpMethod.Post, baseUrl + "/images/edits") { Content = form };
    }

    private static HttpRequestMessage OpenRouter(string baseUrl, string model, string prompt, List<byte[]> refs, string quality)
    {
        var body = new JsonObject { ["model"] = model, ["prompt"] = prompt, ["n"] = 1, ["aspect_ratio"] = "3:2" };
        if ((quality ?? "").Trim().Length > 0) body["quality"] = quality!.Trim().ToLowerInvariant();
        if (refs.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var r in refs)
                arr.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{(IsPng(r) ? "image/png" : "image/jpeg")};base64,{Convert.ToBase64String(r)}" } });
            body["input_references"] = arr;
        }
        return new HttpRequestMessage(HttpMethod.Post, baseUrl + "/images") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    public static bool IsPng(byte[] b) => b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G';
    public static bool IsJpeg(byte[] b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    /// <summary>
    /// The picture from the answer (data[0].b64_json, both formats), the tokens it says it used (usage.total_tokens, or
    /// input+output), or why there's none (a refusal, an empty answer, something that isn't a picture).
    /// </summary>
    public static (byte[]? image, long tokens, string why) Read(string text)
    {
        JsonObject? o;
        try { o = JsonNode.Parse(text ?? "") as JsonObject; } catch { return (null, 0, "the answer couldn't be read"); }
        if (o == null) return (null, 0, "the answer couldn't be read");
        long tokens = 0;
        if (o["usage"] is JsonObject u)
        {
            long N(string k) => u[k] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
            tokens = N("total_tokens") > 0 ? N("total_tokens") : N("input_tokens") + N("output_tokens");
        }
        if (o["error"] is JsonObject err) return (null, tokens, (err["code"]?.ToString() ?? "") is "moderation_blocked" ? "the image service refused the picture" : Detail(text ?? ""));
        string b64 = o["data"] is JsonArray d && d.Count > 0 && d[0] is JsonObject first && first["b64_json"] is JsonValue bv && bv.TryGetValue<string>(out var s) ? s : "";
        if (b64.Length == 0) return (null, tokens, "the image service sent no picture");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); } catch { return (null, tokens, "the picture couldn't be read"); }
        if (bytes.Length > MaxImageBytes) return (null, tokens, "the picture was too big");
        if (!IsPng(bytes) && !IsJpeg(bytes)) return (null, tokens, "what came back isn't a PNG or JPEG");
        return (bytes, tokens, "");
    }

    private static string Detail(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject o && o["error"] is JsonObject e && e["message"] is JsonValue m && m.TryGetValue<string>(out var msg)) return Shorten(msg);
        }
        catch { }
        return Shorten(body ?? "");
    }

    private static string Shorten(string s) => s.Length > 240 ? s[..240] + "…" : s;
}
