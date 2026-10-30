using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NivalisAIChat.Ai;

public sealed class LlmSettings
{
    public ApiFormat Format = ApiFormat.Anthropic;
    public string Url = "https://api.anthropic.com/v1/messages";
    public string ApiKey = "";
    public string Model = "claude-opus-5-5";
    public string BackgroundModel = "";
    /// <summary>CF-13: BackgroundModel's own provider (empty: the main one), its key, and its format (null: Format).</summary>
    public string BackgroundUrl = "", BackgroundApiKey = "";
    public ApiFormat? BackgroundFormat;
    public string Effort = "low";
    /// <summary>
    /// OpenAI-format only: how much the model thinks (reasoning_effort: none, minimal, low, medium, high, xhigh). Empty =
    /// not sent, the model's own default (GPT-5.4: none). Never Effort: "low" would switch thinking on for those (CF-06).
    /// </summary>
    public string ReasoningEffort = "";
    public float Temperature = 0.9f;
    public int TimeoutSeconds = 90;
    public string ExtraHeaders = "";
    public bool RefusalFallback = true;
    public bool PromptCaching = true;
    public bool LogPrompts;

    /// <summary>
    /// The settings one request is sent with: a background request goes to BackgroundUrl, with its key, format and
    /// BackgroundModel (Model when that's empty), when BackgroundUrl is set; anything else as before.
    /// </summary>
    public LlmSettings ForRequest(bool background)
    {
        if (!background || string.IsNullOrWhiteSpace(BackgroundUrl)) return this;
        var c = (LlmSettings)MemberwiseClone();
        c.Url = BackgroundUrl.Trim();
        c.ApiKey = BackgroundApiKey;
        if (BackgroundFormat is { } f) c.Format = f;
        if (!string.IsNullOrWhiteSpace(BackgroundModel)) c.Model = BackgroundModel.Trim();
        c.ExtraHeaders = ""; // review CF13-1: the main provider's extra headers can hold credentials; they stay with it
        c.BackgroundModel = "";
        c.BackgroundUrl = "";
        c.BackgroundApiKey = "";
        return c;
    }

    /// <summary>BackgroundApiFormat as written: Same (or anything else) = null, the main format.</summary>
    public static ApiFormat? FormatOf(string? text) =>
        Enum.TryParse<ApiFormat>((text ?? "").Trim(), true, out var f) && Enum.IsDefined(f) ? f : null;
}

public sealed class LlmMessage
{
    public string Role;    // "user" or "assistant"
    public string Content;
    /// <summary>Pictures sent with this message (a snapshot of someone), for models that can see.</summary>
    public List<LlmImage>? Images;
    public LlmMessage(string role, string content) { Role = role; Content = content; }
}

/// <summary>A picture for the AI: its type ("image/jpeg") and the bytes.</summary>
public sealed class LlmImage
{
    public string MediaType = "image/jpeg";
    public byte[] Bytes = Array.Empty<byte>();
    public string Base64 => Convert.ToBase64String(Bytes);
}

public sealed class LlmRequest
{
    public string Purpose = "chat";
    /// <summary>System prompt blocks; put the stable ones first so prompt caching can reuse them.</summary>
    public List<string> System = new();
    public List<LlmMessage> Messages = new();
    public int MaxTokens = 4000;
    /// <summary>When set, ask the endpoint for a JSON object matching this schema.</summary>
    public JsonObject? JsonSchema;
    public string SchemaName = "result";
    /// <summary>An answer in plain words is read too (a chat reply with [[tags]], a diary entry): not asked again for the shape.</summary>
    public bool PlainTextOk;
    /// <summary>Background jobs may run on a different (cheaper) model.</summary>
    public bool Background;
}

public sealed class LlmResult
{
    public bool Ok;
    public string Text = "";
    public string? Error;
    public bool Refused;
    public string StopReason = "";
    /// <summary>The request carried a picture and the endpoint said the model can't take pictures.</summary>
    public bool ImageRejected;
    /// <summary>Tokens the provider says this took (sent + received); 0 when it didn't say.</summary>
    public long Tokens;
    public static LlmResult Fail(string error) => new() { Ok = false, Error = error };

    /// <summary>It stopped because it ran out of room (MaxGenerationTokens), not because it was done.</summary>
    public bool CutOff => StopReason is "length" or "max_tokens" or "max_output_tokens";

    /// <summary>Why an answer that should have been JSON couldn't be read, for the player: cut off, empty, or plain words.</summary>
    public string WhyUnreadable() =>
        CutOff ? "it was cut off before the end (raise MaxGenerationTokens, or lower ReasoningEffort, in AI Fine Tuning)"
        : string.IsNullOrWhiteSpace(Text) ? "the answer was empty (some models only think and never answer: try another model)"
        : "the AI answered in plain words, not in the shape asked for (try another model)";
}

/// <summary>
/// Minimal HTTP client for the two wire formats people actually run: Anthropic Messages and
/// OpenAI-style chat completions. Raw HttpClient + System.Text.Json because the game's CoreCLR is .NET 6.
/// </summary>
public sealed class LlmClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
        // Never followed (the full audit, A-005): a redirect to another host would carry the key's header there too.
        AllowAutoRedirect = false,
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    private readonly Func<LlmSettings> _settings;
    private readonly Action<string> _log;
    private readonly HttpMessageInvoker? _invoker; // test seam

    // Optional request features an endpoint rejected once; not sent again this session.
    private readonly HashSet<string> _disabled = new();
    private readonly object _lock = new();
    // Address + model whose server took the answer's shape (response_format / output_config) without keeping to it (Ollama's
    // cloud models): from then on the shape is also written into each request to it, as for a server that refuses it.
    private readonly HashSet<string> _shapeInWords = new();
    private static string ShapeKey(string url, string model) => url.Trim().ToLowerInvariant() + "\n" + model.Trim();

    /// <summary>The most tokens the mod may use per hour/day/month (null = no limit kept).</summary>
    public TokenBudget? Budget;
    /// <summary>FB-41: the usage record, by purpose and model (null: not kept).</summary>
    public Voice.VoiceLedger? Ledger;

    /// <summary>The AI in the usage record: its host and model (never its key).</summary>
    public static string LedgerKey(string url, string model)
    {
        string host = Uri.TryCreate(url ?? "", UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "";
        return $"AI|{host}|{Redact.Value(model).ToLowerInvariant()}"; // a key pasted into Model is a stand-in (review F-0141)
    }

    public LlmClient(Func<LlmSettings> settings, Action<string> log, HttpMessageInvoker? invoker = null)
    {
        _settings = settings;
        _log = log;
        _invoker = invoker;
    }

    private const string UrlNoScheme = "[AI-URL] ApiUrl must start with http:// or https:// (for example https://api.openai.com/v1).";
    private const string UrlInvalid = "[AI-URL] ApiUrl isn't a valid web address (for example https://api.openai.com/v1).";

    public static string ResolveUrl(ApiFormat format, string url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0)
            url = format == ApiFormat.Anthropic ? "https://api.anthropic.com/v1/messages" : "https://api.openai.com/v1/chat/completions";
        var trimmed = url.TrimEnd('/');
        if (format == ApiFormat.Anthropic)
        {
            if (trimmed.EndsWith("/messages", StringComparison.OrdinalIgnoreCase)) return trimmed;
            return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? trimmed + "/messages" : trimmed + "/v1/messages";
        }
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return trimmed;
        return trimmed + "/chat/completions";
    }

    public async Task<LlmResult> CompleteAsync(LlmRequest req, CancellationToken ct)
    {
        var s = _settings().ForRequest(req.Background); // CF-13: a background request may go to its own provider
        string url = ResolveUrl(s.Format, s.Url);
        // An address without http:// or https:// only failed inside HttpClient, with an unexplained error on every
        // request (ROBUST-8): said as it is, before anything is sent.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target))
            return LlmResult.Fail(url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? UrlInvalid : UrlNoScheme);
        if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps) return LlmResult.Fail(UrlNoScheme);
        // "Never time out" written as a huge number was past what a timer can hold, and threw on every request (ROBUST-6).
        int timeout = Math.Clamp(s.TimeoutSeconds, 10, 3600);
        string model = req.Background && !string.IsNullOrWhiteSpace(s.BackgroundModel) ? s.BackgroundModel.Trim() : s.Model.Trim();
        string key = string.IsNullOrWhiteSpace(s.ApiKey) ? EnvironmentKey(s.Format, url) : s.ApiKey.Trim();
        if (s.Format == ApiFormat.Anthropic && key.Length == 0 && IsOfficialAnthropic(url))
            return LlmResult.Fail("No API key set. Put your key in BepInEx\\config\\PeopleOfNivalis.cfg → [AI Connection] ApiKey.");
        if (Budget != null && !Budget.Allows()) return LlmResult.Fail(Budget.LimitMessage());

        int attempt = 0;
        int featureRetries = 0;
        while (true)
        {
            attempt++;
            JsonObject body;
            List<(string, string)> headers;
            bool shapeInWords;
            lock (_lock)
            {
                (body, headers) = s.Format == ApiFormat.Anthropic ? BuildAnthropic(req, s, url, key, model) : BuildOpenAI(req, s, url, key, model);
                shapeInWords = _shapeInWords.Contains(ShapeKey(url, model));
            }
            string json = body.ToJsonString();
            if (s.LogPrompts)
            {
                // A model setting that holds a key is logged as its stand-in (set on a copy: the request itself is unchanged).
                string shown = json;
                if (Redact.LooksLikeKey(model))
                {
                    var copy = Clone(body).AsObject();
                    copy["model"] = Redact.Value(model);
                    shown = copy.ToJsonString();
                }
                _log($"[{req.Purpose}] → {Redact.Url(url)}\n{shown}");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeout));
            HttpResponseMessage resp;
            string respText;
            try
            {
                using (var msg = new HttpRequestMessage(HttpMethod.Post, target) { Content = new StringContent(json, Encoding.UTF8, "application/json") })
                {
                    foreach (var (name, value) in headers) msg.Headers.TryAddWithoutValidation(name, value);
                    try
                    {
                        resp = _invoker != null ? await _invoker.SendAsync(msg, cts.Token).ConfigureAwait(false)
                                                : await Http.SendAsync(msg, cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is (InvalidOperationException and not ObjectDisposedException) or NotSupportedException or UriFormatException)
                    {
                        // What HttpClient throws for an address (or a request) it can't send: a clear failure, not an exception (ROBUST-8).
                        return LlmResult.Fail($"[AI-URL] The request couldn't be sent to the address in ApiUrl ({e.Message}).");
                    }
                }
                respText = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Someone waiting in a conversation gets one retry at most; background work can afford two.
                if (attempt <= (req.Background ? 2 : 1)) { await Task.Delay(1000 * attempt, ct).ConfigureAwait(false); continue; }
                return LlmResult.Fail($"The AI endpoint timed out after {timeout}s.");
            }
            catch (HttpRequestException e)
            {
                if (attempt <= 2) { await Task.Delay(1500 * attempt, ct).ConfigureAwait(false); continue; }
                return LlmResult.Fail($"Couldn't reach {target.Host}: {e.Message}");
            }

            if (s.LogPrompts) _log($"[{req.Purpose}] ← {(int)resp.StatusCode}\n{Redact.Text(Redact.Hide(respText, key), model)}");

            int code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode)
            {
                var result = s.Format == ApiFormat.Anthropic ? ParseAnthropic(respText) : ParseOpenAI(respText);
                if (result.Error != null) result.Error = Redact.Text(Redact.Hide(result.Error, key), model); // the full audit (A-003)
                long spent = result.Tokens > 0 ? result.Tokens : TokenBudget.Estimate(json.Length, respText.Length);
                Budget?.Add(spent);
                // FB-41: by what it was for and which model, beside the voices (the Usage Optimizer learns from it). Never fails a reply.
                try { Ledger?.Record(LedgerKey(url, model), "AI (" + Redact.Value(model) + ")", "tokens", string.IsNullOrEmpty(req.Purpose) ? "chat" : req.Purpose, spent); }
                catch { }
                // Sent without the shape in words, and the answer isn't JSON: asked again once with it (each request on its own, so
                // two at once both get their second try); said once per address and model.
                if (!shapeInWords && result.Ok && req.JsonSchema != null && !req.PlainTextOk && !result.CutOff && ExtractJsonObject(result.Text) == null)
                {
                    bool first;
                    lock (_lock) first = _shapeInWords.Add(ShapeKey(url, model));
                    if (first)
                        _log("The AI server accepted the answer's shape but didn't keep to it (Ollama's cloud models do this): the shape is " +
                             "written into each request to it from now on, and this one is asked again.");
                    continue;
                }
                return result;
            }

            // A proxy / older model rejected an optional feature: drop it and try again (before blaming the picture).
            if (code == 400 || code == 422)
            {
                string? feature = RejectedFeature(respText, s.Format);
                if (feature != null && !Off(url, model, feature) && featureRetries < 4)
                {
                    lock (_lock) _disabled.Add(FeatureKey(url, model, feature));
                    featureRetries++;
                    attempt--;
                    _log($"Endpoint rejected '{feature}'; retrying without it.");
                    continue;
                }
            }

            // A picture the model can't take: say so plainly (the caller tries again without it). The model's own name
            // is left out of the check ("llama3.2-vision not found" is about the name, not about pictures).
            if (HasImages(req) && !NoPicturesFor(url, model) && code is 400 or 415 or 422
                && ImageRejection(model.Length > 0 ? respText.Replace(model, "", StringComparison.OrdinalIgnoreCase) : respText))
            {
                lock (_lock) _noPictures = PictureKey(url, model); // not sent to this model again this session
                var fail = LlmResult.Fail($"Your AI model ({Redact.Value(model)}) can't look at pictures: {Redact.Text(Redact.Hide(Shorten(ErrorDetail(respText)), key), model)}");
                fail.ImageRejected = true;
                return fail;
            }
            if ((code == 429 || code == 408 || code == 409 || code >= 500) && attempt <= 3)
            {
                double wait = 1.5 * attempt * attempt;
                if (resp.Headers.RetryAfter?.Delta is TimeSpan ra) wait = Math.Min(30, ra.TotalSeconds);
                await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
                continue;
            }
            // Providers repeat the model in errors ("model X does not exist"): one pasted into the wrong setting isn't logged.
            return LlmResult.Fail(Redact.Text(Redact.Hide(DescribeHttpError(code, respText), key), model));
        }
    }

    /// <summary>
    /// A feature this endpoint refused this session, for this model (review XR-7: by endpoint, so BackgroundApiUrl's refusals
    /// stay its own; the full audit, A-001: by model too, so a background model on the same address that refuses one doesn't
    /// take it from the chat's model).
    /// </summary>
    private bool Off(string url, string model, string feature) { lock (_lock) return _disabled.Contains(FeatureKey(url, model, feature)); }
    private static string FeatureKey(string url, string model, string feature) => url.Trim().ToLowerInvariant() + "|" + model.Trim() + "|" + feature;

    /// <summary>The endpoint already said this session that the model can't take pictures (they're left out from now on).</summary>
    public bool PicturesUnsupported
    {
        get
        {
            var s = _settings();
            return NoPicturesFor(ResolveUrl(s.Format, s.Url), s.Model.Trim());
        }
    }

    /// <summary>The endpoint + model that said it can't take pictures (a different model or URL gets asked again).</summary>
    private string _noPictures = "";
    private static string PictureKey(string url, string model) => url.Trim().ToLowerInvariant() + "|" + model.Trim().ToLowerInvariant();
    private bool NoPicturesFor(string url, string model) { lock (_lock) return _noPictures.Length > 0 && _noPictures == PictureKey(url, model); }

    private static bool HasImages(LlmRequest req) => req.Messages.Any(m => m.Images is { Count: > 0 });

    // What endpoints say when a model can't see: "image_url is only supported by certain models", "Image input not
    // supported", "unknown variant `image_url`, expected `text`", "messages[0].content must be a string", "does not support vision"...
    private static readonly Regex ImageError = new(
        @"(?i)image_url|image input|images? (?:are|is) not supported|not support(?:ed)? (?:images?|vision|multimodal)|does not support (?:images?|vision|multimodal)|vision|multimodal|image_?content|content must be a string|unknown variant|expected `?text`?|invalid content type",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool ImageRejection(string body) => ImageError.IsMatch(body ?? "");

    private static string ErrorDetail(string body)
    {
        try
        {
            var root = JsonNode.Parse(body);
            if (root is JsonArray list && list.Count > 0) root = list[0]; // Gemini wraps its error in a list: [{"error":{...}}]
            if (root is not JsonObject o) return Shorten(body);
            var error = o["error"];
            return (error is JsonObject e && e["message"] is JsonValue m && m.TryGetValue<string>(out var text) ? text : null)
                ?? (error is JsonValue ev && ev.TryGetValue<string>(out var plain) ? plain : null)
                ?? (o["message"] is JsonValue mv && mv.TryGetValue<string>(out var top) ? top : null)
                ?? Shorten(body);
        }
        catch { return Shorten(body ?? ""); }
    }

    /// <summary>JsonNode.DeepClone arrived in .NET 8; the game runs .NET 6.</summary>
    private static JsonNode Clone(JsonNode node) => JsonNode.Parse(node.ToJsonString())!;

    private static bool IsOfficialAnthropic(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        string host = u.Host;
        return host.Equals("anthropic.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".anthropic.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether <paramref name="url"/> is https on exactly <paramref name="host"/> (not a look-alike such as api.openai.com.example.net).</summary>
    public static bool IsHost(string? url, string host) =>
        Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// OPENAI_API_KEY / ANTHROPIC_API_KEY, used when ApiKey is empty, but only for that company's own endpoint (exactly
    /// api.openai.com / api.anthropic.com): a key from the environment is never sent anywhere else. "" otherwise.
    /// </summary>
    public static string EnvironmentKey(ApiFormat format, string url)
    {
        bool anthropic = format == ApiFormat.Anthropic;
        if (!IsHost(url, anthropic ? "api.anthropic.com" : "api.openai.com")) return "";
        return (Environment.GetEnvironmentVariable(anthropic ? "ANTHROPIC_API_KEY" : "OPENAI_API_KEY") ?? "").Trim();
    }

    // ASCII digits, bounded on both sides: a model name with other digits, or a huge number ("claude-opus-99999999999"),
    // is no known version (no special features) instead of an exception on every request (ROBUST-11's sibling).
    private static readonly Regex ClaudeVersion = new(@"claude-(opus|sonnet|haiku|fable|mythos)-([0-9]{1,3})(?![0-9])(?:-([0-9]{1,3})(?![0-9]))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Models with server-side refusal fallback in the "default" form.</summary>
    private static bool SupportsDefaultFallback(string model)
    {
        var m = ClaudeVersion.Match(model);
        if (!m.Success || !int.TryParse(m.Groups[2].Value, out int major)) return false;
        string fam = m.Groups[1].Value.ToLowerInvariant();
        if (fam == "haiku" || fam == "mythos") return false;
        return major >= 5;
    }

    /// <summary>output_config.effort exists on 4.6+ (Opus 4.5 too, but only low/medium/high).</summary>
    private static bool SupportsEffort(string model)
    {
        var m = ClaudeVersion.Match(model);
        if (!m.Success || !int.TryParse(m.Groups[2].Value, out int major)) return false;
        string fam = m.Groups[1].Value.ToLowerInvariant();
        int minor = m.Groups[3].Success && m.Groups[3].Value.Length <= 2 && int.TryParse(m.Groups[3].Value, out int mi) ? mi : 0;
        if (major >= 5) return true;
        return major == 4 && minor >= 6 && fam != "haiku";
    }

    private (JsonObject, List<(string, string)>) BuildAnthropic(LlmRequest req, LlmSettings s, string url, string key, string model)
    {
        var headers = new List<(string, string)> { ("anthropic-version", "2023-06-01") };
        if (key.Length > 0) headers.Add(("x-api-key", key));
        AddExtraHeaders(headers, s.ExtraHeaders);

        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = req.MaxTokens,
        };
        var sys = new JsonArray();
        foreach (var block in req.System.Where(b => !string.IsNullOrWhiteSpace(b)))
            sys.Add(new JsonObject { ["type"] = "text", ["text"] = block });
        // The endpoint refused structured output: the answer's shape is asked for in words, as for OpenAI (audit B2).
        if (req.JsonSchema != null && (Off(url, model, "output_config.format") || Off(url, model, "output_config") || _shapeInWords.Contains(ShapeKey(url, model))))
            sys.Add(new JsonObject { ["type"] = "text", ["text"] = "Reply with a single JSON object only, matching this JSON schema exactly:\n" + req.JsonSchema.ToJsonString() });
        // The first block (world + identity) is identical across turns and meetings: cache it on its own.
        if (s.PromptCaching && !Off(url, model, "cache_control") && sys.Count > 1)
            sys[0]!["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        if (sys.Count > 0) body["system"] = sys;
        body["messages"] = MessagesArray(req.Messages, null, anthropic: true, pictures: !NoPicturesFor(url, model), openAiHost: false);

        var output = new JsonObject();
        if (!string.IsNullOrWhiteSpace(s.Effort) && SupportsEffort(model) && !Off(url, model, "effort"))
            output["effort"] = s.Effort.Trim().ToLowerInvariant();
        if (req.JsonSchema != null && !Off(url, model, "output_config.format"))
            output["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = Clone(req.JsonSchema) };
        if (output.Count > 0 && !Off(url, model, "output_config")) body["output_config"] = output;

        if (s.PromptCaching && !Off(url, model, "cache_control"))
            body["cache_control"] = new JsonObject { ["type"] = "ephemeral" };

        if (s.RefusalFallback && IsOfficialAnthropic(url) && SupportsDefaultFallback(model) && !Off(url, model, "fallbacks"))
        {
            body["fallbacks"] = "default";
            headers.Add(("anthropic-beta", "server-side-fallback-2026-07-01"));
        }
        return (body, headers);
    }

    private (JsonObject, List<(string, string)>) BuildOpenAI(LlmRequest req, LlmSettings s, string url, string key, string model)
    {
        var headers = new List<(string, string)>();
        if (key.Length > 0) headers.Add(("Authorization", "Bearer " + key));
        AddExtraHeaders(headers, s.ExtraHeaders);

        string system = string.Join("\n\n", req.System.Where(b => !string.IsNullOrWhiteSpace(b)));
        if (req.JsonSchema != null && (Off(url, model, "response_format") || _shapeInWords.Contains(ShapeKey(url, model))))
            system += "\n\nReply with a single JSON object only, matching this JSON schema exactly:\n" + req.JsonSchema.ToJsonString();

        bool openAiHost = url.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase);
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = MessagesArray(req.Messages, system, anthropic: false, pictures: !NoPicturesFor(url, model), openAiHost),
        };
        bool useCompletionTokens = (openAiHost && !Off(url, model, "max_completion_tokens")) || Off(url, model, "max_tokens");
        body[useCompletionTokens ? "max_completion_tokens" : "max_tokens"] = req.MaxTokens;
        // How much it thinks, only when asked for (a provider that doesn't take it has it switched off, like the others).
        if (s.ReasoningEffort.Trim().Length > 0 && !Off(url, model, "reasoning_effort")) body["reasoning_effort"] = s.ReasoningEffort.Trim().ToLowerInvariant();
        // Only a real number, held at 2 at most: Infinity made every request throw (ROBUST-6), and providers reject above 2.
        if (s.Temperature >= 0 && float.IsFinite(s.Temperature) && !Off(url, model, "temperature")) body["temperature"] = Math.Round(Math.Min(s.Temperature, 2f), 2);
        if (req.JsonSchema != null && !Off(url, model, "response_format"))
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = req.SchemaName,
                    ["strict"] = true,
                    ["schema"] = Clone(req.JsonSchema),
                },
            };
        }
        return (body, headers);
    }

    /// <summary>
    /// Messages with consecutive same-role turns merged, starting with a user turn. A message with pictures becomes a list
    /// of parts (the picture, then the text), in the endpoint's own format; without <paramref name="pictures"/> they're left out.
    /// </summary>
    private static JsonArray MessagesArray(List<LlmMessage> messages, string? openAiSystem, bool anthropic, bool pictures, bool openAiHost)
    {
        var merged = new List<LlmMessage>();
        foreach (var m in messages)
        {
            if (string.IsNullOrWhiteSpace(m.Content) && m.Images is not { Count: > 0 }) continue;
            if (merged.Count > 0 && merged[^1].Role == m.Role)
            {
                var prev = merged[^1];
                var joined = new LlmMessage(m.Role, string.IsNullOrWhiteSpace(m.Content) ? prev.Content : prev.Content + "\n\n" + m.Content);
                if (prev.Images != null || m.Images != null)
                    joined.Images = (prev.Images ?? new List<LlmImage>()).Concat(m.Images ?? new List<LlmImage>()).ToList();
                merged[^1] = joined;
            }
            else
                merged.Add(new LlmMessage(m.Role, m.Content) { Images = m.Images });
        }
        if (merged.Count == 0 || merged[0].Role != "user")
            merged.Insert(0, new LlmMessage("user", "[Scene: The conversation continues.]"));

        var arr = new JsonArray();
        if (openAiSystem != null && openAiSystem.Length > 0)
            arr.Add(new JsonObject { ["role"] = "system", ["content"] = openAiSystem });
        foreach (var m in merged)
        {
            if (!pictures || m.Images is not { Count: > 0 } || m.Role != "user")
            {
                arr.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
                continue;
            }
            var parts = new JsonArray();
            foreach (var img in m.Images)
            {
                if (anthropic)
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = img.MediaType, ["data"] = img.Base64 },
                    });
                else
                {
                    var url = new JsonObject { ["url"] = $"data:{img.MediaType};base64,{img.Base64}" };
                    if (openAiHost) url["detail"] = "low"; // a small picture of one person: the cheap setting is plenty
                    parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = url });
                }
            }
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = m.Content });
            arr.Add(new JsonObject { ["role"] = m.Role, ["content"] = parts });
        }
        return arr;
    }

    private static void AddExtraHeaders(List<(string, string)> headers, string extra)
    {
        if (string.IsNullOrWhiteSpace(extra)) return;
        foreach (var part in extra.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0) continue;
            headers.Add((part[..colon].Trim(), part[(colon + 1)..].Trim()));
        }
    }

    private static LlmResult ParseAnthropic(string text)
    {
        try
        {
            var root = JsonNode.Parse(text)!.AsObject();
            string stop = root["stop_reason"]?.GetValue<string>() ?? "";
            var sb = new StringBuilder();
            if (root["content"] is JsonArray content)
                foreach (var block in content)
                    if (block?["type"]?.GetValue<string>() == "text")
                        sb.Append(block["text"]?.GetValue<string>());
            return new LlmResult { Ok = stop != "refusal", Refused = stop == "refusal", StopReason = stop, Text = sb.ToString().Trim(),
                Error = stop == "refusal" ? "refused" : null, Tokens = UsageOf(root) };
        }
        catch (Exception e)
        {
            return LlmResult.Fail("Unreadable reply from the AI endpoint: " + e.Message);
        }
    }

    /// <summary>
    /// Tokens the answer says it took: OpenAI-style total_tokens (or prompt + completion), Anthropic input + output (+ cache
    /// writes and reads). 0 when it doesn't say.
    /// </summary>
    internal static long UsageOf(JsonObject root)
    {
        try
        {
            if (root["usage"] is not JsonObject u) return 0;
            long N(string k) => u[k] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
            long total = N("total_tokens");
            if (total > 0) return total;
            long openAi = N("prompt_tokens") + N("completion_tokens");
            if (openAi > 0) return openAi;
            return N("input_tokens") + N("output_tokens") + N("cache_creation_input_tokens") + N("cache_read_input_tokens");
        }
        catch { return 0; }
    }

    private static readonly Regex ThinkBlock = new(@"<think(?:ing)?>[\s\S]*?</think(?:ing)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static LlmResult ParseOpenAI(string text)
    {
        try
        {
            var root = JsonNode.Parse(text)!.AsObject();
            var choice = root["choices"]?[0];
            if (choice == null) return LlmResult.Fail("The AI endpoint returned no choices: " + Shorten(text));
            string finish = choice["finish_reason"]?.GetValue<string>() ?? "";
            string content = "";
            var c = choice["message"]?["content"];
            if (c is JsonValue v) content = v.GetValue<string>();
            else if (c is JsonArray parts)
                content = string.Concat(parts.Select(p => p?["text"]?.GetValue<string>() ?? ""));
            content = ThinkBlock.Replace(content, "").Trim();
            bool refused = finish == "content_filter" || (choice["message"]?["refusal"] is JsonValue r && r.GetValue<string>()?.Length > 0);
            return new LlmResult { Ok = !refused, Refused = refused, StopReason = finish, Text = content, Error = refused ? "refused" : null, Tokens = UsageOf(root) };
        }
        catch (Exception e)
        {
            return LlmResult.Fail("Unreadable reply from the AI endpoint: " + e.Message);
        }
    }

    private static string? RejectedFeature(string body, ApiFormat format)
    {
        string b = body.ToLowerInvariant();
        if (format == ApiFormat.Anthropic)
        {
            if (b.Contains("fallback")) return "fallbacks";
            if (b.Contains("effort")) return "effort";
            if (b.Contains("cache_control")) return "cache_control";
            if (b.Contains("output_config.format") || b.Contains("json_schema")) return "output_config.format";
            if (b.Contains("output_config")) return "output_config";
            return null;
        }
        if (b.Contains("max_tokens") && (b.Contains("max_completion_tokens") || b.Contains("not supported"))) return "max_tokens";
        if (b.Contains("max_completion_tokens")) return "max_completion_tokens";
        if (b.Contains("temperature")) return "temperature"; // before reasoning: an error naming both drops the temperature, not the player's ReasoningEffort
        if (b.Contains("reasoning")) return "reasoning_effort"; // reasoning_effort, reasoning.effort, "reasoning is not supported" (review of 0.3.1, L5)
        if (b.Contains("response_format") || b.Contains("json_schema")) return "response_format";
        return null;
    }

    private static string DescribeHttpError(int code, string body)
    {
        string detail = ErrorDetail(body);
        string hint = code switch
        {
            401 => " Check ApiKey in the config.",
            403 => " The key isn't allowed to use this model or endpoint.",
            404 => " Check ApiUrl and Model in the config (model names change over time: check your provider's current model list).",
            429 when detail.Contains("quota", StringComparison.OrdinalIgnoreCase) =>
                " Your provider says you're out of quota (a free tier's limit, or credit used up): wait for it to reset, or check your plan.",
            429 => " Rate limited or out of credit.",
            >= 300 and < 400 => " The address sends requests somewhere else: put the address it moves to in ApiUrl (redirects aren't followed, so your key only goes where you put it).",
            _ => "",
        };
        return $"AI endpoint error {code}: {Shorten(detail)}{hint}";
    }

    private static string Shorten(string s) => s.Length > 300 ? s[..300] + "…" : s;

    /// <summary>Pull the first JSON object out of a reply that may be wrapped in prose or code fences.</summary>
    public static JsonObject? ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var opts = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        try { if (JsonNode.Parse(text.Trim(), documentOptions: opts) is JsonObject direct) return direct; } catch { }
        int start = text.IndexOf('{');
        while (start >= 0)
        {
            int depth = 0; bool inStr = false; bool esc = false;
            for (int i = start; i < text.Length; i++)
            {
                char ch = text[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (ch == '\\') esc = true;
                    else if (ch == '"') inStr = false;
                    continue;
                }
                if (ch == '"') inStr = true;
                else if (ch == '{') depth++;
                else if (ch == '}' && --depth == 0)
                {
                    try { if (JsonNode.Parse(text[start..(i + 1)], documentOptions: opts) is JsonObject o) return o; } catch { }
                    break;
                }
            }
            start = text.IndexOf('{', start + 1);
        }
        return null;
    }
}
