using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace NivalisAIChat.Ai;

public sealed class SpeechSettings
{
    /// <summary>The service's base address ("https://api.openai.com/v1"); /audio/transcriptions is added.</summary>
    public string Url = "";
    public string Key = "";
    public string Model = "whisper-1";
    /// <summary>Two-letter code, or "" for the service to work it out.</summary>
    public string Language = "";
}

/// <summary>
/// FB-64: what you said into the microphone, as text. One OpenAI-style request (POST {url}/audio/transcriptions, a WAV
/// file in a multipart form): OpenAI's Whisper, Groq's, or an open-source server run on your own PC (whisper.cpp's
/// server, faster-whisper / speaches) all take it. Errors never carry the key. No Unity.
/// </summary>
public sealed class SpeechToText
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
        // Never followed (the full audit, A-005): a redirect to another host would carry the key's header there too.
        AllowAutoRedirect = false,
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    private readonly Func<SpeechSettings> _settings;
    private readonly HttpMessageInvoker? _invoker; // test seam

    public SpeechToText(Func<SpeechSettings> settings, HttpMessageInvoker? invoker = null)
    {
        _settings = settings;
        _invoker = invoker;
    }

    /// <summary>The address the request goes to: the base with /audio/transcriptions (one already ending in it is kept).</summary>
    public static string Endpoint(string url)
    {
        string u = (url ?? "").Trim().TrimEnd('/');
        if (u.EndsWith("/audio/transcriptions", StringComparison.OrdinalIgnoreCase)) return u;
        return u + "/audio/transcriptions";
    }

    /// <summary>
    /// The key to use: SpeechToTextKey; empty, the main ApiKey only when it's the same service (host) as ApiUrl; otherwise
    /// none (a local server takes none). A key never goes to a service it wasn't given for.
    /// </summary>
    public static string KeyFor(string speechUrl, string speechKey, string apiUrl, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(speechKey)) return speechKey.Trim();
        return SameHost(speechUrl, apiUrl) ? (apiKey ?? "").Trim() : "";
    }

    public static bool SameHost(string a, string b) =>
        Uri.TryCreate((a ?? "").Trim(), UriKind.Absolute, out var x) && Uri.TryCreate((b ?? "").Trim(), UriKind.Absolute, out var y)
        && string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase) && x.Scheme == y.Scheme;

    /// <summary>16-bit mono PCM as a WAV file.</summary>
    public static byte[] Wav(short[] samples, int rate)
    {
        using var ms = new MemoryStream(44 + samples.Length * 2);
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        int data = samples.Length * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2);
        w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>How loud the loudest part is (0-1): nearly silent recordings aren't sent.</summary>
    public static double Peak(short[] samples)
    {
        int peak = 0;
        foreach (var s in samples) { int a = s == short.MinValue ? short.MaxValue : Math.Abs((int)s); if (a > peak) peak = a; }
        return peak / (double)short.MaxValue;
    }

    /// <summary>The words in <paramref name="samples"/> (16-bit mono at <paramref name="rate"/>), trimmed; "" for none.</summary>
    public async Task<string> TranscribeAsync(short[] samples, int rate, CancellationToken ct, int timeoutSeconds = 30)
    {
        var s = _settings();
        if (string.IsNullOrWhiteSpace(s.Url) || !Uri.TryCreate(Endpoint(s.Url), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            throw new SpeechException(0, "SpeechToTextUrl isn't a web address (e.g. https://api.openai.com/v1, or http://127.0.0.1:8080/v1 for a server on this PC).");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Wav(samples, rate));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "speech.wav");
        form.Add(new StringContent(string.IsNullOrWhiteSpace(s.Model) ? "whisper-1" : s.Model.Trim()), "model");
        form.Add(new StringContent("json"), "response_format");
        string lang = (s.Language ?? "").Trim().ToLowerInvariant();
        if (lang.Length is >= 2 and <= 3) form.Add(new StringContent(lang), "language");
        using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = form };
        if (!string.IsNullOrWhiteSpace(s.Key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Key.Trim());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 120)));
        HttpResponseMessage res;
        try { res = _invoker != null ? await _invoker.SendAsync(req, timeout.Token).ConfigureAwait(false) : await Http.SendAsync(req, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SpeechException(0, "the speech-to-text service took too long to answer"); }
        catch (HttpRequestException e) { throw new SpeechException(0, "the speech-to-text service couldn't be reached: " + Redact.Text(Hide(e.Message, s.Key), s.Key)); }
        using (res)
        {
            string body = await res.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                string why = (int)res.StatusCode switch
                {
                    401 or 403 => "it refused the key (SpeechToTextKey, or ApiKey for the same service)",
                    404 => "nothing at that address (SpeechToTextUrl) or no such model (SpeechToTextModel)",
                    413 => "the recording was too long",
                    429 => "too many requests, or the account is out of credit",
                    _ => "it answered " + (int)res.StatusCode,
                };
                string detail = Hide(ErrorMessage(body), s.Key);
                throw new SpeechException((int)res.StatusCode, $"speech to text failed: {why}{(detail.Length > 0 ? " (" + Redact.Text(detail, s.Key) + ")" : "")}");
            }
            return TextOf(body);
        }
    }

    /// <summary>The words in a service's answer: {"text": "..."} (or the plain text some servers send).</summary>
    public static string TextOf(string body)
    {
        string b = (body ?? "").Trim();
        if (b.StartsWith("{"))
        {
            try { return JsonNode.Parse(b)?["text"]?.GetValue<string>()?.Trim() ?? ""; }
            catch { return ""; }
        }
        return b.Length < 4000 ? b : "";
    }

    /// <summary>Review F-STT-1: the key itself taken out of any text, whatever it looks like (Redact only knows key-like ones).</summary>
    private static string Hide(string text, string key)
    {
        string k = (key ?? "").Trim();
        return k.Length >= 4 ? (text ?? "").Replace(k, "(hidden)", StringComparison.Ordinal) : text ?? "";
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            var o = JsonNode.Parse(body);
            string m = o?["error"]?["message"]?.GetValue<string>() ?? o?["error"]?.GetValue<string>() ?? o?["detail"]?.GetValue<string>() ?? "";
            return m.Length <= 200 ? m : m[..200] + "…";
        }
        catch { return ""; }
    }
}

public sealed class SpeechException : Exception
{
    public int Status { get; }
    public SpeechException(int status, string message) : base(message) { Status = status; }
}
