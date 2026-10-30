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

public sealed class VoyageSettings
{
    public string Key = "";
    public string Model = "voyage-4-lite";
    /// <summary>Vector size (models of the 4, 3.5 and 3-large families take 256, 512, 1024 or 2048).</summary>
    public int Dimensions = 512;
}

/// <summary>A Voyage AI request that failed (the message says why, never the key).</summary>
public sealed class VoyageException : Exception
{
    public int Status { get; }
    public VoyageException(int status, string message) : base(message) { Status = status; }
}

/// <summary>
/// Voyage AI embeddings: turns memories, and what someone just said, into vectors, so the memories closest in meaning
/// can be found. Voyage keeps nothing; the vectors are stored and searched on this PC (see Npc.Memories).
/// </summary>
public sealed class VoyageClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
        // Never followed (the full audit, A-005): a redirect to another host would carry the key's header there too.
        AllowAutoRedirect = false,
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    private const string Endpoint = "https://api.voyageai.com/v1/embeddings";
    private readonly Func<VoyageSettings> _settings;
    private readonly HttpMessageInvoker? _invoker; // test seam
    private long _tokens;

    public VoyageClient(Func<VoyageSettings> settings, HttpMessageInvoker? invoker = null)
    {
        _settings = settings;
        _invoker = invoker;
    }

    /// <summary>Tokens Voyage counted this session.</summary>
    public long TokensUsed => Interlocked.Read(ref _tokens);

    /// <summary>FB-41: the usage record (memory search's tokens beside the AI and the voices).</summary>
    public Voice.VoiceLedger? Ledger;

    public bool HasKey => _settings().Key.Trim().Length > 0;

    /// <summary>A fingerprint of the key (never the key): tells a key Voyage refused from a new one (audit B3).</summary>
    public string KeyMark
    {
        get
        {
            ulong h = 14695981039346656037UL;
            foreach (char ch in _settings().Key.Trim()) { h ^= ch; h *= 1099511628211UL; }
            return h.ToString("x16");
        }
    }

    /// <summary>
    /// The model and vector size now in use ("voyage-4-lite/512"): an index made with another can't be searched with this
    /// one. Saved in every index file, so a model setting that looks like a key is written as a stand-in.
    /// </summary>
    public string Signature
    {
        get { var s = _settings(); return $"{Redact.Value(ModelOf(s))}/{DimensionsOf(s)}"; }
    }

    private static string ModelOf(VoyageSettings s) => s.Model.Trim().Length > 0 ? s.Model.Trim() : "voyage-4-lite";

    /// <summary>Models that take a vector size; the rest use their own.</summary>
    public static bool Sizeable(string model) =>
        model.StartsWith("voyage-4", StringComparison.OrdinalIgnoreCase) || model.StartsWith("voyage-3.5", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("voyage-3-large", StringComparison.OrdinalIgnoreCase) || model.StartsWith("voyage-code-3", StringComparison.OrdinalIgnoreCase);

    private static int DimensionsOf(VoyageSettings s) => Sizeable(ModelOf(s)) && s.Dimensions is 256 or 512 or 1024 or 2048 ? s.Dimensions : 0;

    /// <summary>
    /// Vectors for <paramref name="texts"/>, in order, each scaled to length 1 (so closeness is a dot product).
    /// <paramref name="query"/>: what someone just said (searched with), rather than a memory (searched for).
    /// </summary>
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool query, CancellationToken ct)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();
        var s = _settings();
        string key = s.Key.Trim();
        if (key.Length == 0) throw new VoyageException(0, "no Voyage key set (VoyageKey in the config)");
        string model = ModelOf(s);
        var body = new JsonObject
        {
            ["input"] = new JsonArray(texts.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            ["model"] = model,
            ["input_type"] = query ? "query" : "document",
            ["truncation"] = true,
        };
        int dims = DimensionsOf(s);
        if (dims > 0) body["output_dimension"] = dims;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        HttpResponseMessage resp;
        string text;
        try
        {
            resp = _invoker != null ? await _invoker.SendAsync(req, timeout.Token).ConfigureAwait(false) : await Http.SendAsync(req, timeout.Token).ConfigureAwait(false);
            text = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new VoyageException(0, "Voyage took too long to answer"); }
        catch (HttpRequestException e) { throw new VoyageException(0, "couldn't reach Voyage: " + e.Message); }
        int code = (int)resp.StatusCode;
        if (!resp.IsSuccessStatusCode)
        {
            string detail = text.Length > 300 ? text[..300] : text;
            throw new VoyageException(code, $"Voyage HTTP {code}: {Redact.Text(Redact.Hide(detail.Trim(), key), model)}"); // it may repeat the model setting
        }
        var o = JsonNode.Parse(text) as JsonObject ?? throw new VoyageException(code, "unreadable answer from Voyage");
        var result = new float[texts.Count][];
        if (o["data"] is JsonArray data)
            foreach (var item in data)
            {
                int index = item?["index"] is JsonValue iv && iv.TryGetValue<int>(out int i) ? i : -1;
                if (index < 0 || index >= result.Length || item?["embedding"] is not JsonArray emb) continue;
                var v = new float[emb.Count];
                for (int k = 0; k < v.Length; k++) v[k] = emb[k] is JsonValue ev && ev.TryGetValue<float>(out float f) ? f : 0f;
                result[index] = Normalized(v);
            }
        if (result.Any(r => r == null)) throw new VoyageException(code, "Voyage left some texts without a vector");
        if (o["usage"]?["total_tokens"] is JsonValue tv && tv.TryGetValue<long>(out long used))
        {
            Interlocked.Add(ref _tokens, used);
            // The model as Signature shows it (review F-0141: a key pasted into VoyageModel never reaches the record).
            string shown = Redact.Value(model);
            try { Ledger?.Record("Voyage|" + shown.ToLowerInvariant(), "Voyage (" + shown + ")", "tokens", query ? "recall" : "index", used); } catch { }
        }
        return result;
    }

    public static float[] Normalized(float[] v)
    {
        double n = 0;
        foreach (var x in v) n += x * x;
        if (n <= 0) return v;
        float inv = (float)(1.0 / Math.Sqrt(n));
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
        return v;
    }
}
