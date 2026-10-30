using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NivalisAIChat.Voice;

/// <summary>
/// Speech for NPC lines. Each person is given a voice the first time they speak — a provider drawn by the configured
/// chances, then a voice that fits how they present — and keeps it for good (VoiceBook). Every provider is asked for
/// plain 16-bit PCM / WAV, so no decoder is needed. No Unity here: the caller turns <see cref="PcmAudio"/> into sound.
/// </summary>
public sealed class VoiceService
{
    private readonly Func<VoiceSettings> _settings;
    private readonly VoiceBook _book;
    private readonly Action<string> _log;
    private readonly HttpMessageInvoker _http;
    private readonly ConcurrentDictionary<string, Task<List<VoiceInfo>>> _catalogs = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastError = new();
    /// <summary>A new voice being chosen for someone: one at a time per person, however many lines ask at once.</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<(VoiceSlot?, VoiceAssignment?)>>> _assigning = new();
    private readonly Random _rng = new();
    private readonly object _pickLock = new();

    public VoiceService(Func<VoiceSettings> settings, VoiceBook book, Action<string> log, HttpMessageInvoker? http = null)
    {
        _settings = settings;
        _book = book;
        _log = log;
        _http = http ?? new HttpMessageInvoker(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false }); // never followed: a key header would go along (the full audit, R-1)
    }

    public bool Enabled
    {
        get { var s = _settings(); return s.Enabled && s.Slots.Any(x => x.Usable); }
    }

    /// <summary>
    /// VoiceLimit (CF-02): the characters sent to providers that bill for them, per hour/day/month. Over it, no line is
    /// asked for (it goes on as text). Null = not counted.
    /// </summary>
    public Ai.TokenBudget? Budget;

    /// <summary>FB-41: what every line cost, per provider (null: not kept).</summary>
    public VoiceLedger? Ledger;

    /// <summary>The clock for limit periods (local time); tests set it.</summary>
    public Func<DateTime> Now = () => DateTime.Now;

    /// <summary>A provider in the usage record: its kind, address and model (never its key).</summary>
    public static string LedgerKey(VoiceSlot s) => $"{s.Provider}|{s.NormalizedUrl}|{Ai.Redact.Value(ModelOf(s)).ToLowerInvariant()}";

    public static string LedgerName(VoiceSlot s) => s.Provider switch
    {
        VoiceProvider.OpenAICompatible => "Voice server",
        VoiceProvider.Windows => "Windows voices",
        _ => s.Provider.ToString(),
    } + (ModelOf(s).Length > 0 ? $" ({Ai.Redact.Value(ModelOf(s))})" : ""); // never a key pasted into the model (review F-0141)

    private static Ai.TokenBudget.Span Span(string per) => Ai.TokenBudget.SpanOf(per) is { } sp && sp != Ai.TokenBudget.Span.Hour ? sp : Ai.TokenBudget.Span.Month;

    /// <summary>
    /// The period a provider's limit counts in: its day, week (from Monday) or month. A month starts again on its own reset
    /// day (FB-59: "resets: 15"), or for ElevenLabs without one, on the account's own day (from its last reset to the next,
    /// as ElevenLabs says) once that's been read; otherwise on the 1st.
    /// </summary>
    public (DateTime start, DateTime reset) PeriodOf(VoiceSlot s)
    {
        var now = Now();
        var span = Span(s.LimitPer);
        if (span == Ai.TokenBudget.Span.Month && s.ResetDay is >= 1 and <= 31)
            return (Ai.TokenBudget.Start(now, span, s.ResetDay), Ai.TokenBudget.NextReset(now, span, s.ResetDay));
        if (span == Ai.TokenBudget.Span.Month && AccountOf(s) is { } acc)
        {
            // The account's own month. A reset date that has passed (not read again yet: set aside, nothing spoken) rolls on a
            // month at a time from that same day, never the calendar's month (review of 5262c60).
            for (int n = 0; n < 36; n++)
            {
                var reset = acc.reset.AddMonths(n);
                if (reset > now) return (n == 0 ? acc.reset.AddMonths(-1) : acc.reset.AddMonths(n - 1), reset);
            }
        }
        return (Ai.TokenBudget.Start(now, span), Ai.TokenBudget.NextReset(now, span));
    }

    /// <summary>
    /// What you've used of a provider in its limit's period, in its own unit: what the mod recorded on this PC (FB-59: your
    /// limit is yours, whoever else uses the same key; the account's own count is <see cref="AccountNow"/>).
    /// </summary>
    public double UsedThisPeriod(VoiceSlot s) => Ledger?.OwnUsedSince(LedgerKey(s), PeriodOf(s).start) ?? 0;

    /// <summary>
    /// FB-59: an ElevenLabs account as it stands now (everyone using its key): used and its plan's limit this month, the count
    /// read last plus what was spoken here since; null when it hasn't been read.
    /// </summary>
    public (double used, double limit)? AccountNow(VoiceSlot s) =>
        AccountOf(s) is { } acc && Now() < acc.reset ? (acc.used + (Ledger?.SinceAccountOf(LedgerKey(s)) ?? 0), acc.limit) : null; // past its reset: not known until read again

    private readonly ConcurrentDictionary<string, string> _limitSaid = new();

    /// <summary>FB-41: a provider at its own limit for the period: its people speak in a stand-in voice until it resets.</summary>
    public bool OverLimit(VoiceSlot s)
    {
        if (Ledger == null || !Billed(s)) return false;
        double used = UsedThisPeriod(s);
        // Review F59-3: the account itself used up (by anyone using its key) sets it aside too, before a call fails.
        bool accountOut = AccountNow(s) is { } a && a.limit > 0 && a.used >= a.limit;
        if (!accountOut && (!(s.Limit > 0) || used < s.Limit)) return false;
        // Set aside, nothing goes through it: its account is read again from time to time, so its new month is seen.
        if (s.Provider == VoiceProvider.ElevenLabs) RefreshAccountSoon(s);
        var (start, reset) = PeriodOf(s);
        string period = start.ToString("o");
        lock (_limitSaid)
        {
            if (_limitSaid.TryGetValue(LedgerKey(s), out var said) && said == period) return true;
            _limitSaid[LedgerKey(s)] = period;
        }
        if (accountOut && !(s.Limit > 0 && used >= s.Limit) && AccountNow(s) is { } full)
            _log($"Voice Provider {s.Number} ({s.Provider}): its account has used up its {full.limit:N0} {VoiceCost.Unit(s.Provider, ModelOf(s))} " +
                 $"({full.used:N0}, everyone using its key): its people speak in a stand-in voice from another provider until it starts again.");
        else
            _log($"Voice Provider {s.Number} ({s.Provider}) reached its limit ({used:N0} of {s.Limit:N0} {VoiceCost.Unit(s.Provider, ModelOf(s))} this " +
                 $"{Ai.TokenBudget.Word(Span(s.LimitPer))}): its people speak in a stand-in voice from another provider until it starts again on {reset:MMM d}.");
        return true;
    }

    /// <summary>Whether a provider's lines cost money: Windows' own voices and a server on this PC or the local network (a private
    /// address, a .local or one-word name like a VM's) don't.</summary>
    public static bool Billed(VoiceSlot slot)
    {
        if (slot.Provider == VoiceProvider.Windows) return false;
        try
        {
            string url = slot.Url.Trim();
            if (url.Length == 0) return true;
            var host = new Uri(url.Contains("://") ? url : "http://" + url).Host;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return false;
            if (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip))
            {
                if (System.Net.IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return false;
                var b = ip.GetAddressBytes();
                bool lan = b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254));
                bool ula = b.Length == 16 && (b[0] & 0xFE) == 0xFC; // fc00::/7
                return !(lan || ula);
            }
            return host.Contains('.'); // a one-word name ("developmentvm") is a machine on your own network
        }
        catch { return true; }
    }

    public VoiceBook Book => _book;

    /// <summary>/resetnpc: their voice is forgotten, and one being chosen for them right now isn't saved.</summary>
    public void Forget(string id)
    {
        _book.Remove(id);
        _assigning.TryRemove(id, out _);
        _standingIn.TryRemove(id, out _);
    }

    /// <summary>/resetall: everyone's voice is forgotten (the old file kept in <paramref name="backupDir"/>).</summary>
    public void ForgetAll(string backupDir)
    {
        _book.Clear(backupDir);
        _assigning.Clear();
        _standingIn.Clear();
    }

    /// <summary>Their line, spoken in their voice. Null when voices are off or the provider fails.</summary>
    public async Task<PcmAudio?> SpeakAsync(VoiceSubject who, string text, CancellationToken ct) =>
        (await SpeakWithOutcomeAsync(who, text, ct).ConfigureAwait(false)).Audio;

    /// <summary>
    /// FB-45 (the Setup Wizard's Test voice): one short line from this provider, in the first voice it offers, counted like
    /// any other line (VoiceLimit, the usage record as "test"). Null audio with the reason when it didn't work.
    /// </summary>
    public async Task<(PcmAudio? audio, string? error)> TestAsync(VoiceSlot slot, string text, CancellationToken ct)
    {
        text = Clean(text);
        if (text.Length == 0) return (null, "nothing to say");
        if (Budget != null && Billed(slot) && !Budget.Allows()) return (null, Budget.LimitMessage());
        if (OverLimit(slot)) return (null, "it's at its own limit for now");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_settings().TimeoutSeconds, 5, 60)));
        try
        {
            var list = await VoicesAsync(slot, timeout.Token).ConfigureAwait(false);
            var pick = list.FirstOrDefault();
            if (pick == null) return (null, "it offered no voices (check its key)");
            var voice = new VoiceAssignment { Provider = slot.Provider.ToString(), Url = slot.NormalizedUrl, VoiceId = pick.Id, VoiceName = pick.Name, Gender = pick.Gender, Locale = pick.Locale };
            var who = new VoiceSubject { Id = "setup-test", Presentation = "any", Purpose = "test" };
            var audio = await SynthesizeAsync(slot, voice, who, text, timeout.Token).ConfigureAwait(false);
            if (audio == null || audio.Samples.Length == 0) return (null, "it sent no audio");
            if (Budget != null && Billed(slot)) Budget.Add(text.Length);
            if (Ledger != null && Billed(slot))
                try
                {
                    string model = ModelOf(slot);
                    Ledger.Record(LedgerKey(slot), LedgerName(slot), VoiceCost.Unit(slot.Provider, model), "test", audio.Cost ?? VoiceCost.Estimate(slot.Provider, model, text, seconds: audio.Seconds));
                    Ledger.Record(Social.UsageModel.VoiceCharsKey, "All voices together (VoiceLimit)", "characters", "test", text.Length);
                }
                catch { /* counting never costs the line */ }
            return (audio, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, "it took too long"); }
        catch (Exception e) { return (null, Ai.Redact.Text(e.Message, slot.ApiKey)); } // its own key too, whatever its shape (the window shows this)
    }

    /// <summary>Their line in their voice, or why there's none (a provider error, or it took too long).</summary>
    public async Task<VoiceOutcome> SpeakWithOutcomeAsync(VoiceSubject who, string text, CancellationToken ct)
    {
        var cfg = _settings();
        if (!cfg.Enabled) return new VoiceOutcome();
        text = Clean(text);
        if (text.Length == 0) return new VoiceOutcome();
        // One deadline for all of it: choosing their voice (a provider's voice list can hang) and saying the line. It
        // runs out before the caller's own give-up, so a provider that's too slow is set aside below (others stand in).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(cfg.TimeoutSeconds, 3, 120)));
        VoiceSlot? slot;
        VoiceAssignment? voice;
        try { (slot, voice) = await ResolveAsync(who, cfg, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new VoiceOutcome { TimedOut = true }; }
        if (slot == null || voice == null) return NoVoice(cfg);
        for (int attempt = 0; ; attempt++)
        {
            // Over VoiceLimit: nothing is sent to a provider that bills for it; the line goes on as text (CF-02).
            if (Budget != null && Billed(slot) && !Budget.Allows()) return new VoiceOutcome { OverBudget = true, Error = Budget.LimitMessage() };
            string failed;
            try
            {
                var audio = await SynthesizeAsync(slot, voice, who, text, timeout.Token).ConfigureAwait(false);
                bool spoken = audio != null && audio.Samples.Length > 0;
                if (spoken && Budget != null && Billed(slot)) Budget.Add(text.Length);
                // Counting it can never cost the line itself (review of 5262c60): a failure here is only noted.
                if (spoken && Ledger != null && Billed(slot))
                    try
                    {
                        string model = ModelOf(slot);
                        Ledger.Record(LedgerKey(slot), LedgerName(slot), VoiceCost.Unit(slot.Provider, model), who.Purpose,
                            audio!.Cost ?? VoiceCost.Estimate(slot.Provider, model, text, seconds: audio.Seconds));
                        // What VoiceLimit counts (characters, all voices together), for the Usage Optimizer (FB-41).
                        Ledger.Record(Social.UsageModel.VoiceCharsKey, "All voices together (VoiceLimit)", "characters", who.Purpose, text.Length);
                        if (slot.Provider == VoiceProvider.ElevenLabs) RefreshAccountSoon(slot);
                    }
                    catch (Exception e) { _log("The voice usage record couldn't note a line: " + e.GetType().Name); }
                return spoken
                    ? Described(new VoiceOutcome { Audio = audio }, slot, voice)
                    : new VoiceOutcome { Error = $"Voice Provider {slot.Number} ({slot.Provider}) sent no audio" };
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Complain(slot, "took too long to answer");
                // Too slow: the next lines use another provider for a minute (this one isn't waited on twice). Windows
                // speech with both of its calls stuck is set aside even alone: every line after it would wait the same.
                if (OtherProvider(cfg, slot) || (slot.Provider == VoiceProvider.Windows && WindowsSpeech.AllBusy)) MarkDown(slot, "", timedOut: true);
                return new VoiceOutcome { TimedOut = true };
            }
            // The 2026-10-20 audit: its own key hidden too, whatever its shape (this goes to the log and the window).
            catch (Exception e) when (e is not OperationCanceledException) { failed = Ai.Redact.Text(Ai.Redact.Hide(e.Message, slot.ApiKey)); }

            // What to do about it, decided outside the catch: choosing again runs under the same deadline.
            // Refused for this voice (not the account): it's set aside for now, and the line goes out in a stand-in voice.
            // The provider itself can't be used (out of credits, a refused key, too many requests, down): it's left alone
            // for a while, and the line goes out in a stand-in voice from another provider.
            bool refused = attempt == 0 && VoiceRefused(failed);
            bool accountWide = !refused && attempt < 4 && AccountDown(failed) && (LongOutage(failed) || OtherProvider(cfg, slot));
            if (refused) SetAside(voice, Reason(failed));
            else if (accountWide) MarkDown(slot, failed, timedOut: false);
            if (refused || accountWide)
            {
                VoiceSlot? s2;
                VoiceAssignment? v2;
                try { (s2, v2) = await ResolveAsync(who, cfg, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new VoiceOutcome { TimedOut = true }; }
                if (s2 != null && v2 != null && (refused ? Key(v2) != Key(voice) : SlotKey(s2) != SlotKey(slot))) { slot = s2; voice = v2; continue; }
            }
            Complain(slot, failed);
            return new VoiceOutcome { Error = $"Voice Provider {slot.Number} ({slot.Provider}): {failed}" };
        }
    }

    /// <summary>No provider could give them a voice: which one is set aside and why, if that's the reason.</summary>
    /// <summary>
    /// FB-30: who spoke a line, for transcripts: the provider and its host, the model, the voice (never a key; ElevenLabs
    /// ids are ids, not keys). Can't fail the line: anything odd just leaves a field empty (code review 10).
    /// </summary>
    private static VoiceOutcome Described(VoiceOutcome outcome, VoiceSlot slot, VoiceAssignment voice)
    {
        try
        {
            string host = "";
            if (Uri.TryCreate(slot.Url ?? "", UriKind.Absolute, out var u)) host = " (" + u.Host + ")";
            string name = (voice.VoiceName ?? "").Length > 0 ? voice.VoiceName ?? "" : voice.VoiceId ?? "";
            outcome.Provider = slot.Provider + host;
            outcome.Model = Ai.Redact.Value(ModelOf(slot));
            outcome.Voice = Ai.Redact.PlainlyKey(name) ? "(hidden)" : name;
        }
        catch { }
        return outcome;
    }

    private VoiceOutcome NoVoice(VoiceSettings cfg)
    {
        var down = cfg.Slots.FirstOrDefault(s => s.Usable && IsDown(s));
        if (down != null && OverLimit(down))
            return new VoiceOutcome
            {
                Error = $"Voice Provider {down.Number} ({down.Provider}) has reached its limit for this {Ai.TokenBudget.Word(Span(down.LimitPer))} " +
                        $"and no other provider could stand in; it starts again on {PeriodOf(down).reset:MMM d}",
            };
        return new VoiceOutcome
        {
            Error = down != null && _down.TryGetValue(SlotKey(down), out var d)
                ? $"Voice Provider {down.Number} ({down.Provider}) can't be used right now ({d.why}) and no other provider could stand in"
                : "none of the voice providers could give them a voice (check the Voice Provider settings and keys)",
        };
    }

    // ------------------------------------------------------------------ providers that can't be used right now (the account)

    /// <summary>
    /// Providers left alone for a while because the account can't use them: out of credits or a refused key (30 minutes),
    /// too many requests, a server problem or too slow (a minute). Everyone whose voice comes from one speaks in a stand-in
    /// from another provider meanwhile; their own voice comes back once it works again.
    /// </summary>
    private readonly ConcurrentDictionary<string, (DateTime until, string why)> _down = new();

    private static string SlotKey(VoiceSlot s) => $"{s.Provider}|{s.NormalizedUrl}|{(s.ApiKey ?? "").GetHashCode()}";

    public bool IsDown(VoiceSlot s) => (_down.TryGetValue(SlotKey(s), out var d) && d.until > DateTime.UtcNow) || OverLimit(s);

    /// <summary>Another provider that's set up and working, to stand in.</summary>
    private bool OtherProvider(VoiceSettings cfg, VoiceSlot slot) =>
        cfg.Slots.Any(s => s.Usable && s.Chance > 0 && SlotKey(s) != SlotKey(slot) && !IsDown(s));

    private static readonly Regex Lasting = new(
        @"(?i)quota|credit|billing|insufficient|payment|exceeded your|\bHTTP 401\b|\bHTTP 403\b|unauthori[sz]ed|invalid api key|api key not valid|unusual activity",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Won't fix itself in a minute (credits, the key, a blocked account).</summary>
    public static bool LongOutage(string message) => Lasting.IsMatch(message ?? "");

    public static TimeSpan DownFor(string message, bool timedOut) =>
        !timedOut && LongOutage(message) ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(1);

    public static string DownReason(string message, bool timedOut)
    {
        string m = message ?? "";
        if (timedOut) return "it was too slow";
        if (Regex.IsMatch(m, "(?i)quota|credit|billing|insufficient|payment|exceeded your", RegexOptions.CultureInvariant)) return "out of credits";
        if (Regex.IsMatch(m, "(?i)unusual activity", RegexOptions.CultureInvariant)) return "the account is blocked";
        if (Regex.IsMatch(m, @"(?i)\bHTTP 40[13]\b|unauthori[sz]ed|invalid api key|api key not valid", RegexOptions.CultureInvariant)) return "the key was refused";
        if (Regex.IsMatch(m, @"(?i)\bHTTP 429\b|rate.?limit|too many", RegexOptions.CultureInvariant)) return "too many requests";
        return "it's having problems";
    }

    private void MarkDown(VoiceSlot slot, string message, bool timedOut)
    {
        bool was = IsDown(slot);
        var span = DownFor(message, timedOut);
        string why = DownReason(message, timedOut);
        _down[SlotKey(slot)] = (DateTime.UtcNow + span, why);
        if (!was)
            _log($"Voice Provider {slot.Number} ({slot.Provider}) can't be used right now ({why}): voices from it come from another provider " +
                 $"(a stand-in of the same gender) until it works again; it's tried again in {span.TotalMinutes:0} minute{(span.TotalMinutes == 1 ? "" : "s")}.");
    }

    // ------------------------------------------------------------------ voices that can't be used right now

    /// <summary>Voices refused this session (for their own sake, not the account's), and why; tried again after a while.</summary>
    private readonly ConcurrentDictionary<string, (DateTime until, string why)> _setAside = new();
    private static readonly TimeSpan SetAsideFor = TimeSpan.FromMinutes(30);

    private static string Key(VoiceAssignment v) => $"{v.Provider}|{v.Url}|{v.VoiceId}";
    private static string Key(VoiceSlot s, string voiceId) => $"{s.Provider}|{s.NormalizedUrl}|{voiceId}";

    private void SetAside(VoiceAssignment v, string why)
    {
        _setAside[Key(v)] = (DateTime.UtcNow + SetAsideFor, why);
        _log($"The voice '{v.VoiceName}' ({v.Provider}) can't be used right now ({why}); anyone with it speaks in a stand-in voice until it can.");
    }

    private bool IsSetAside(string key) => _setAside.TryGetValue(key, out var a) && a.until > DateTime.UtcNow;

    // Refused for this voice: it needs a model or a better plan, or doesn't exist. Not for the account: a bad key, no credit, limits.
    private static readonly Regex VoiceSpecific = new(
        @"(?i)requires? a model|model.?name|voice[^.""]{0,60}(?:not found|not available|does not exist|doesn't exist|isn't available|not supported|is invalid|unknown)|" +
        @"voice_not_found|only_for_creator|professional voice|not permitted to use|upgrade your subscription|requires? (?:a )?(?:paid|higher|creator|pro)\b|" +
        @"payment_required|paid_plan|\bHTTP 402\b|\bHTTP 404\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AccountWide = new(
        @"(?i)\bHTTP 401\b|\bHTTP 429\b|\bHTTP 5\d\d\b|quota|unusual activity|rate.?limit|too many|unauthori[sz]ed|api key not valid|invalid api key|billing|out of credits?|insufficient",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool VoiceRefused(string message) => VoiceSpecific.IsMatch(message ?? "") && !AccountWide.IsMatch(message ?? "");

    /// <summary>
    /// The provider can't be used just now. The 2026-10-20 audit: a bare 403 too (no access, a blocked region), never one about
    /// a voice ("only_for_creator"): that only swaps the voice, also on a retry where the voice check doesn't run (review R1).
    /// </summary>
    public static bool AccountDown(string message)
    {
        string m = message ?? "";
        return AccountWide.IsMatch(m) || (Regex.IsMatch(m, @"\bHTTP 403\b", RegexOptions.CultureInvariant) && !VoiceSpecific.IsMatch(m));
    }

    private static string Reason(string message)
    {
        string m = message ?? "";
        if (Regex.IsMatch(m, "(?i)model.?name|requires? a model", RegexOptions.CultureInvariant)) return "it needs a model name (a Gemini voice)";
        if (Regex.IsMatch(m, "(?i)only_for_creator|professional|paid|upgrade|subscription|payment|creator|permitted", RegexOptions.CultureInvariant)) return "your plan doesn't include it";
        if (Regex.IsMatch(m, "(?i)not found|does not exist|doesn't exist|HTTP 404|unknown", RegexOptions.CultureInvariant)) return "the provider no longer has it";
        return "the provider refused it";
    }

    /// <summary>
    /// /voices: every voice provider, and under each the voices it offers this account right now (by gender), any it
    /// can't use at the moment and why, and how many people are on a stand-in voice.
    /// </summary>
    public async Task<List<string>> ReportAsync(CancellationToken ct)
    {
        var cfg = _settings();
        var lines = new List<string>();
        if (!cfg.Enabled) lines.Add("Voices are off ([Voices] Enabled in the config). What's set up:");
        var slots = cfg.Slots.Where(s => s.Provider != VoiceProvider.None).ToList();
        if (slots.Count == 0) { lines.Add("No voice providers are set up ([Voices] Providers in the config)."); return lines; }
        var book = _book.All();
        foreach (var slot in slots)
        {
            string head = $"VOICE PROVIDER {slot.Number}: {slot.Provider}";
            if (!slot.Usable) { lines.Add($"{head}: not set up yet (its key or address is missing)."); continue; }
            var voices = await VoicesAsync(slot, ct).ConfigureAwait(false);
            var notes = new List<string>();
            string plan = PlanOf(slot);
            if (plan.Length > 0) notes.Add(plan is "free" or "starter" ? $"{plan} plan: voices that need a bigger plan are left out" : $"{plan} plan");
            if (slot.Provider == VoiceProvider.Google && !GeminiModel(slot)) notes.Add("Gemini voices left out (they need a Gemini model in Model)");
            if (slot.Voices.Trim().Length > 0) notes.Add("your own list");
            notes.Add($"chance {slot.Chance:0.#}");
            lines.Add($"{head}: {voices.Count} voice(s) you can use ({string.Join("; ", notes)})");
            if (IsDown(slot) && _down.TryGetValue(SlotKey(slot), out var rest))
                lines.Add($"  Not used right now ({rest.why}); voices from it come from another provider until {rest.until.ToLocalTime():HH:mm}, then it's tried again.");
            if (voices.Count == 0) lines.Add("  None right now: check its key, address and plan (/errors may say why).");
            foreach (var (label, gender) in new[] { ("Women", "female"), ("Men", "male"), ("Other", "") })
            {
                var names = voices.Where(v => gender.Length > 0 ? v.Gender == gender : v.Gender is not ("female" or "male"))
                                  .Select(v => v.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                if (names.Count > 0) lines.Add($"  {label} ({names.Count}): {string.Join(", ", names)}");
            }
            string prefix = Key(slot, "");
            foreach (var kv in _setAside.Where(kv => kv.Value.until > DateTime.UtcNow && kv.Key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                string id = kv.Key[prefix.Length..];
                string name = book.Select(b => b.Value).FirstOrDefault(v => v.VoiceId == id)?.VoiceName ?? id;
                lines.Add($"  Can't be used right now: {name} ({kv.Value.why}); tried again later.");
            }
            int users = book.Count(b => slot.Owns(b.Value));
            if (users > 0) lines.Add($"  People whose own voice is from here: {users}");
        }
        int standIns = StandInsInUse(cfg);
        lines.Add(standIns > 0
            ? $"{standIns} people speak in a stand-in voice right now; each gets their own back as soon as it works again."
            : "Everyone speaks in their own voice.");
        return lines;
    }

    /// <summary>How many people speak in a stand-in voice right now (for /voices).</summary>
    public int StandInsInUse(VoiceSettings cfg) =>
        _book.All().Count(kv => kv.Value.StandIn != null && (IsSetAside(Key(kv.Value)) || !cfg.Slots.Any(s => s.Usable && s.Owns(kv.Value) && !IsDown(s))));

    /// <summary>
    /// The voice they speak with: their own; while that can't be used, a stand-in (their own is kept and comes back as
    /// soon as it works); for someone new, a new voice for good.
    /// </summary>
    public async Task<(VoiceSlot? slot, VoiceAssignment? voice)> ResolveAsync(VoiceSubject who, VoiceSettings cfg, CancellationToken ct)
    {
        long generation = _book.Generation(who.Id); // before their voice is read: forgotten since then means nothing is saved
        var (slot, voice) = KeptVoice(who, cfg);
        if (voice != null) return (slot, voice);
        var own = _book.Get(who.Id);
        if (own != null) return await StandInFor(who, own, cfg, ct, generation).ConfigureAwait(false);
        var lazy = _assigning.GetOrAdd(who.Id, id => new Lazy<Task<(VoiceSlot?, VoiceAssignment?)>>(() => AssignAsync(who, cfg)));
        var task = lazy.Value;
        _ = task.ContinueWith(_ => _assigning.TryRemove(new KeyValuePair<string, Lazy<Task<(VoiceSlot?, VoiceAssignment?)>>>(who.Id, lazy)), TaskScheduler.Default);
        var assigned = await task.WaitAsync(ct).ConfigureAwait(false);
        // Their new voice is theirs for good, but its provider can't be used right now: a stand-in until it can.
        if (assigned.Item1 != null && IsDown(assigned.Item1) && _book.Get(who.Id) is { } fresh)
            return await StandInFor(who, fresh, cfg, ct, generation).ConfigureAwait(false);
        return assigned;
    }

    private async Task<(VoiceSlot?, VoiceAssignment?)> StandInFor(VoiceSubject who, VoiceAssignment own, VoiceSettings cfg, CancellationToken ct, long generation)
    {
        var standIn = _standingIn.GetOrAdd(who.Id, id => new Lazy<Task<(VoiceSlot?, VoiceAssignment?)>>(() => StandInAsync(who, own, cfg, generation)));
        var t = standIn.Value;
        _ = t.ContinueWith(_ => _standingIn.TryRemove(new KeyValuePair<string, Lazy<Task<(VoiceSlot?, VoiceAssignment?)>>>(who.Id, standIn)), TaskScheduler.Default);
        return await t.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Their own voice, if its provider is still set up, still offers it, and hasn't refused it this session. Null
    /// otherwise (the caller gives them a stand-in; their own isn't forgotten). Never waits: the provider's list only
    /// confirms the voice is still offered, and a list that hangs would hold every line. Until the list is there (or
    /// when it failed), their voice is used; one the provider no longer has is refused when it speaks, and set aside.
    /// </summary>
    private (VoiceSlot?, VoiceAssignment?) KeptVoice(VoiceSubject who, VoiceSettings cfg)
    {
        var existing = _book.Get(who.Id);
        if (existing == null) return (null, null);
        var owner = cfg.Slots.FirstOrDefault(s => s.Usable && s.Owns(existing));
        if (owner == null || IsSetAside(Key(existing)) || IsDown(owner)) return (null, null);
        var list = VoicesAsync(owner, CancellationToken.None); // fetched in the background for next time, if it isn't yet
        if (!list.IsCompletedSuccessfully) return (owner, existing);
        var offered = list.Result;
        if (offered.Count == 0 || offered.Any(v => v.Id == existing.VoiceId)) return (owner, existing); // (a failed list isn't proof it's gone)
        if (!_setAside.ContainsKey(Key(existing))) SetAside(existing, "Voice Provider " + owner.Number + " doesn't offer it to your account right now");
        return (null, null);
    }

    private readonly ConcurrentDictionary<string, Lazy<Task<(VoiceSlot?, VoiceAssignment?)>>> _standingIn = new();

    /// <summary>
    /// A stand-in while their own voice can't be used: the one they had last time if it still works, else one of the
    /// same gender, from the same provider when it can (same-sounding world), picked the same way every time for them.
    /// </summary>
    private async Task<(VoiceSlot?, VoiceAssignment?)> StandInAsync(VoiceSubject who, VoiceAssignment own, VoiceSettings cfg, long generation)
    {
        if (own.StandIn is { } last)
        {
            var lastOwner = cfg.Slots.FirstOrDefault(s => s.Usable && s.Owns(last));
            if (lastOwner != null && !IsSetAside(Key(last)) && !IsDown(lastOwner))
            {
                var offered = await VoicesAsync(lastOwner, CancellationToken.None).ConfigureAwait(false);
                if (offered.Count == 0 || offered.Any(v => v.Id == last.VoiceId)) return (lastOwner, last);
            }
        }
        var like = new VoiceSubject
        {
            Id = who.Id, Age = who.Age, Description = who.Description, Mood = who.Mood,
            Presentation = own.Gender is "male" or "female" ? own.Gender : who.Presentation,
        };
        var rng = new Random(StableSeed(who.Id));
        var slots = cfg.Slots.Where(s => s.Usable && s.Chance > 0 && !IsDown(s)).OrderByDescending(s => s.Owns(own)).ThenByDescending(s => s.Chance).ToList();
        foreach (var slot in slots)
        {
            var voices = (await VoicesAsync(slot, CancellationToken.None).ConfigureAwait(false))
                .Where(v => v.Id != own.VoiceId && !IsSetAside(Key(slot, v.Id))).ToList();
            var pick = VoiceCatalog.Pick(voices, like, cfg.MatchGender, rng);
            if (pick == null) continue;
            var stand = new VoiceAssignment
            {
                Provider = slot.Provider.ToString(), Url = slot.NormalizedUrl, VoiceId = pick.Id, VoiceName = pick.Name,
                Gender = pick.Gender, Locale = pick.Locale, Speed = own.Speed,
            };
            own.StandIn = stand;
            _book.Set(who.Id, own, generation); // not saved if their voice was forgotten meanwhile (/resetnpc)
            _log($"{who.Id} speaks as '{pick.Name}' ({slot.Provider}) until their own voice '{own.VoiceName}' can be used again.");
            return (slot, stand);
        }
        return (null, null);
    }

    private static int StableSeed(string s)
    {
        unchecked { int h = 23; foreach (char ch in s ?? "") h = h * 31 + ch; return h; }
    }

    private async Task<(VoiceSlot?, VoiceAssignment?)> AssignAsync(VoiceSubject who, VoiceSettings cfg)
    {
        long generation = _book.Generation(who.Id); // before their voice is read: forgotten since then means nothing is saved
        // Someone else may have just given them a voice.
        var kept = KeptVoice(who, cfg);
        if (kept.Item2 != null) return kept;
        var ct = CancellationToken.None;
        var candidates = cfg.Slots.Where(s => s.Usable && s.Chance > 0).ToList();
        // First choice by the configured chances; a provider without a fitting voice hands over to the next.
        foreach (bool strict in new[] { true, false })
        {
            var left = candidates.ToList();
            while (left.Count > 0)
            {
                VoiceSlot slot;
                lock (_pickLock) slot = WeightedPick(left, _rng);
                left.Remove(slot);
                var voices = await VoicesAsync(slot, ct).ConfigureAwait(false);
                VoiceInfo? pick;
                lock (_pickLock) pick = VoiceCatalog.Pick(voices, who, strict && cfg.MatchGender, _rng);
                if (pick == null) continue;
                double speed;
                lock (_pickLock) speed = Math.Round(0.94 + _rng.NextDouble() * 0.12, 2);
                var voice = new VoiceAssignment
                {
                    Provider = slot.Provider.ToString(), Url = slot.NormalizedUrl, VoiceId = pick.Id, VoiceName = pick.Name,
                    Gender = pick.Gender, Locale = pick.Locale, Speed = speed,
                };
                if (!_book.Set(who.Id, voice, generation)) return (slot, voice); // forgotten meanwhile: this line only
                _log($"{who.Id} now has the {slot.Provider} voice '{pick.Name}'{(pick.Gender.Length > 0 ? $" ({pick.Gender})" : "")}.");
                return (slot, voice);
            }
        }
        return (null, null);
    }

    public static VoiceSlot WeightedPick(IReadOnlyList<VoiceSlot> slots, Random rng)
    {
        double total = slots.Sum(s => Math.Max(0, s.Chance));
        double roll = rng.NextDouble() * total;
        foreach (var s in slots)
        {
            roll -= Math.Max(0, s.Chance);
            if (roll < 0) return s;
        }
        return slots[^1];
    }

    // ------------------------------------------------------------------ voice lists

    /// <summary>When each failed (or empty) voice list was first seen: it's kept a while before it's fetched again.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _listFailedAt = new();
    private static readonly TimeSpan ListRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>
    /// What this provider offers (cached). A failed or empty list is kept for a minute (each line doesn't fetch it again),
    /// then fetched again. <paramref name="ct"/> only limits this caller's wait: the fetch itself is shared and keeps its
    /// own 30 s limit.
    /// </summary>
    public Task<List<VoiceInfo>> VoicesAsync(VoiceSlot slot, CancellationToken ct)
    {
        string key = $"{slot.Provider}|{slot.NormalizedUrl}|{slot.Model}|{slot.Voices}|{slot.ApiKey.GetHashCode()}";
        var task = _catalogs.GetOrAdd(key, _ => FetchVoicesAsync(slot));
        if (task.IsCompleted && (task.IsFaulted || task.IsCanceled || task.Result.Count == 0))
        {
            var since = _listFailedAt.GetOrAdd(key, _ => DateTime.UtcNow);
            // Only this very fetch is replaced (a newer one someone else started stays).
            if (DateTime.UtcNow - since >= ListRetryAfter && _catalogs.TryRemove(new KeyValuePair<string, Task<List<VoiceInfo>>>(key, task)))
            {
                _listFailedAt.TryRemove(key, out _);
                task = _catalogs.GetOrAdd(key, _ => FetchVoicesAsync(slot));
            }
        }
        return ct.CanBeCanceled ? task.WaitAsync(ct) : task;
    }

    private async Task<List<VoiceInfo>> FetchVoicesAsync(VoiceSlot slot)
    {
        if (slot.Voices.Trim().Length > 0) return VoiceCatalog.Parse(slot.Voices);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var list = slot.Provider switch
            {
                VoiceProvider.OpenAI => VoiceCatalog.OpenAI(ModelOf(slot)),
                VoiceProvider.Deepgram => VoiceCatalog.Deepgram(),
                VoiceProvider.ElevenLabs => await ElevenLabsVoicesAsync(slot, cts.Token).ConfigureAwait(false),
                VoiceProvider.Azure => await AzureVoicesAsync(slot, cts.Token).ConfigureAwait(false),
                VoiceProvider.Google => await GoogleVoicesAsync(slot, cts.Token).ConfigureAwait(false),
                VoiceProvider.OpenAICompatible => await CompatibleVoicesAsync(slot, cts.Token).ConfigureAwait(false),
                VoiceProvider.Windows => await WindowsSpeech.VoicesAsync(cts.Token).ConfigureAwait(false),
                _ => new List<VoiceInfo>(),
            };
            if (list.Count == 0) Complain(slot, "offered no voices");
            else _log($"Voice Provider {slot.Number} ({slot.Provider}): {list.Count} voices ({list.Count(v => v.Gender == "female")} female, {list.Count(v => v.Gender == "male")} male).");
            return list;
        }
        catch (Exception e)
        {
            Complain(slot, "couldn't list its voices: " + e.Message);
            return new List<VoiceInfo>();
        }
    }

    /// <summary>What each account's plan is called (ElevenLabs), for /voices.</summary>
    private readonly ConcurrentDictionary<string, string> _plans = new();

    public string PlanOf(VoiceSlot slot) => _plans.TryGetValue(Key(slot, ""), out var p) ? p : "";

    /// <summary>FB-41: an ElevenLabs account as it said last (used and limit this month, in its credits, and its next reset).</summary>
    private readonly ConcurrentDictionary<string, (double used, double limit, DateTime reset, DateTime at)> _accounts = new();
    private readonly ConcurrentDictionary<string, DateTime> _accountAsked = new();

    public (double used, double limit, DateTime reset)? AccountOf(VoiceSlot s) =>
        _accounts.TryGetValue(LedgerKey(s), out var a) ? (a.used, a.limit, a.reset) : null;

    private void NoteAccount(VoiceSlot slot, JsonObject? o)
    {
        if (o == null) return;
        double Num(string k) => o[k] is JsonValue v && v.TryGetValue(out double d) ? d : o[k] is JsonValue w && w.TryGetValue(out long l) ? l : -1;
        double used = Num("character_count"), limit = Num("character_limit"), reset = Num("next_character_count_reset_unix");
        if (used < 0 || limit < 0 || reset <= 0) return;
        var resetAt = DateTimeOffset.FromUnixTimeSeconds((long)reset).LocalDateTime;
        _accounts[LedgerKey(slot)] = (used, limit, resetAt, DateTime.UtcNow);
        Ledger?.Calibrate(LedgerKey(slot), LedgerName(slot), "credits", used, resetAt.AddMonths(-1));
    }

    /// <summary>The account read again every 10 minutes while its voices are used (a key without account access just isn't).</summary>
    private void RefreshAccountSoon(VoiceSlot slot)
    {
        var now = DateTime.UtcNow;
        string key = LedgerKey(slot);
        lock (_accountAsked)
        {
            if (_accountAsked.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(10)) return;
            _accountAsked[key] = now;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var sub = new HttpRequestMessage(HttpMethod.Get, $"{ElevenLabsBase(slot)}/v1/user/subscription");
                sub.Headers.Add("xi-api-key", slot.ApiKey.Trim());
                NoteAccount(slot, await JsonAsync(sub, cts.Token).ConfigureAwait(false));
            }
            catch { } // no account access with this key: counted here alone
        });
    }

    /// <summary>Whether a voice of this ElevenLabs category can be used on this plan (free: premade and designed voices only).</summary>
    public static bool ElevenLabsAllows(string tier, string category)
    {
        string t = (tier ?? "").ToLowerInvariant(), c = (category ?? "").ToLowerInvariant();
        if (t.Length == 0 || c.Length == 0) return true; // unknown: let the provider decide
        if (t == "free") return c is "premade" or "generated";
        if (t == "starter") return c != "professional";
        return true;
    }

    private async Task<List<VoiceInfo>> ElevenLabsVoicesAsync(VoiceSlot slot, CancellationToken ct)
    {
        string tier = "";
        try
        {
            var sub = new HttpRequestMessage(HttpMethod.Get, $"{ElevenLabsBase(slot)}/v1/user/subscription");
            sub.Headers.Add("xi-api-key", slot.ApiKey.Trim());
            var o = await JsonAsync(sub, ct).ConfigureAwait(false);
            tier = Str(o?["tier"]);
            if (tier.Length > 0) _plans[Key(slot, "")] = tier;
            NoteAccount(slot, o);
        }
        catch { } // a key without account access: the provider decides voice by voice
        var list = new List<VoiceInfo>();
        string? token = null;
        for (int page = 0; page < 10; page++)
        {
            string url = $"{ElevenLabsBase(slot)}/v2/voices?page_size=100" + (token != null ? "&next_page_token=" + Uri.EscapeDataString(token) : "");
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("xi-api-key", slot.ApiKey.Trim());
            var o = await JsonAsync(req, ct).ConfigureAwait(false);
            if (o?["voices"] is JsonArray voices)
                foreach (var v in voices)
                {
                    string id = Str(v?["voice_id"]);
                    if (id.Length == 0) continue;
                    var labels = v?["labels"] as JsonObject;
                    var shared = v?["sharing"]?["labels"] as JsonObject;
                    string L(string k) => Str(labels?[k]) is { Length: > 0 } a ? a : Str(shared?[k]);
                    string category = Str(v?["category"]);
                    if (!ElevenLabsAllows(tier, category)) continue; // not on this plan: never handed out
                    list.Add(new VoiceInfo { Id = id, Name = Str(v?["name"]) is { Length: > 0 } n ? n : id, Gender = VoiceCatalog.Gender(L("gender")), Age = L("age"), Category = category });
                }
            bool more = o?["has_more"] is JsonValue hv && hv.TryGetValue<bool>(out var b) && b;
            token = Str(o?["next_page_token"]);
            if (!more || token.Length == 0) break;
        }
        return list;
    }

    private async Task<List<VoiceInfo>> AzureVoicesAsync(VoiceSlot slot, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, AzureBase(slot) + "/cognitiveservices/voices/list");
        req.Headers.Add("Ocp-Apim-Subscription-Key", slot.ApiKey.Trim());
        var arr = await JsonArrayAsync(req, ct).ConfigureAwait(false);
        var list = new List<VoiceInfo>();
        foreach (var v in arr ?? new JsonArray())
        {
            string locale = Str(v?["Locale"]);
            if (!locale.StartsWith("en-", StringComparison.OrdinalIgnoreCase)) continue;
            string shortName = Str(v?["ShortName"]);
            if (shortName.Length == 0) continue;
            list.Add(new VoiceInfo { Id = shortName, Name = Str(v?["DisplayName"]) is { Length: > 0 } d ? $"{d} ({locale})" : shortName, Gender = VoiceCatalog.Gender(Str(v?["Gender"])), Locale = locale });
        }
        return list;
    }

    private async Task<List<VoiceInfo>> GoogleVoicesAsync(VoiceSlot slot, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, GoogleBase(slot) + "/voices?languageCode=en");
        req.Headers.Add("X-Goog-Api-Key", slot.ApiKey.Trim());
        var o = await JsonAsync(req, ct).ConfigureAwait(false);
        var list = new List<VoiceInfo>();
        foreach (var v in o?["voices"] as JsonArray ?? new JsonArray())
        {
            string name = Str(v?["name"]);
            string locale = (v?["languageCodes"] as JsonArray)?.Select(x => Str(x)).FirstOrDefault(x => x.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ?? "";
            if (name.Length == 0 || locale.Length == 0) continue;
            list.Add(new VoiceInfo { Id = name, Name = name, Gender = VoiceCatalog.Gender(Str(v?["ssmlGender"])), Locale = locale });
        }
        // Gemini voices ("Kore", "Puck": no language in the name) only work with a Gemini model named in the provider's Model.
        if (!GeminiModel(slot)) list.RemoveAll(v => GoogleNeedsModel(v.Id));
        // The older "Standard" voices sound robotic; use the natural ones when the account has them.
        var natural = list.Where(v => !v.Id.Contains("Standard", StringComparison.OrdinalIgnoreCase)).ToList();
        return natural.Count >= 4 ? natural : list;
    }

    private async Task<List<VoiceInfo>> CompatibleVoicesAsync(VoiceSlot slot, CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, OpenAiBase(slot) + "/audio/voices");
            if (slot.ApiKey.Trim().Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", slot.ApiKey.Trim());
            var node = await JsonNodeAsync(req, ct).ConfigureAwait(false);
            var arr = node as JsonArray ?? node?["voices"] as JsonArray ?? node?["data"] as JsonArray;
            var list = new List<VoiceInfo>();
            foreach (var v in arr ?? new JsonArray())
            {
                string id = v is JsonValue ? Str(v) : Str(v?["id"]) is { Length: > 0 } i ? i : Str(v?["name"]);
                if (id.Length == 0) continue;
                string g = v is JsonObject ? VoiceCatalog.Gender(Str(v["gender"])) : "";
                list.Add(new VoiceInfo { Id = id, Name = id, Gender = g.Length > 0 ? g : VoiceCatalog.KokoroGender(id) });
            }
            if (list.Count > 0) return list;
        }
        catch (Exception e) { _log($"Voice Provider {slot.Number}: no voice list from the server ({e.Message}); using the standard OpenAI voice names."); }
        return VoiceCatalog.OpenAI(ModelOf(slot));
    }

    // ------------------------------------------------------------------ speaking

    public Task<PcmAudio?> SynthesizeAsync(VoiceSlot slot, VoiceAssignment voice, VoiceSubject who, string text, CancellationToken ct) => slot.Provider switch
    {
        VoiceProvider.OpenAI or VoiceProvider.OpenAICompatible => OpenAiSpeechAsync(slot, voice, who, text, ct),
        VoiceProvider.ElevenLabs => ElevenLabsSpeechAsync(slot, voice, who, text, ct),
        VoiceProvider.Azure => AzureSpeechAsync(slot, voice, who, text, ct),
        VoiceProvider.Google => GoogleSpeechAsync(slot, voice, who, text, ct),
        VoiceProvider.Deepgram => DeepgramSpeechAsync(slot, voice, text, ct),
        VoiceProvider.Windows => WindowsSpeech.SpeakAsync(voice.VoiceId, text, voice.Speed, ct),
        _ => Task.FromResult<PcmAudio?>(null),
    };

    private async Task<PcmAudio?> OpenAiSpeechAsync(VoiceSlot slot, VoiceAssignment voice, VoiceSubject who, string text, CancellationToken ct)
    {
        bool official = slot.Provider == VoiceProvider.OpenAI;
        string model = ModelOf(slot);
        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = text,
            ["voice"] = voice.VoiceId,
            ["response_format"] = official ? "pcm" : "wav",
        };
        if (model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
        {
            // Newer models act the line out: who they are and how they sound. Never for a child: nothing about them is sent.
            if (!who.Child) body["instructions"] = Directions(who, ToneOf(who));
        }
        else
            body["speed"] = voice.Speed;
        var req = new HttpRequestMessage(HttpMethod.Post, OpenAiBase(slot) + "/audio/speech") { Content = JsonContent(body) };
        if (slot.ApiKey.Trim().Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", slot.ApiKey.Trim());
        var bytes = await BytesAsync(req, ct).ConfigureAwait(false);
        var audio = official ? Audio.FromPcm16(bytes, 24000, 1) : Audio.FromWav(bytes) ?? throw NotWav();
        if (audio != null)
            audio.Cost = VoiceCost.Estimate(slot.Provider, model, text, body["instructions"]?.GetValue<string>() ?? "", seconds: audio.Seconds);
        return audio;
    }

    /// <summary>
    /// Their tone for this line, when voice tones are on: the one their conversation has built up to (it moves a little
    /// with each line), or their mood as it is. Null when tones are off.
    /// </summary>
    private ToneState? ToneOf(VoiceSubject who)
    {
        if (!_settings().Tones || who.Child) return null;
        if (who.Tone != null) return who.Tone;
        if (who.Mood.Length == 0) return null;
        var parts = who.Mood.Split(' ', 2);
        return parts.Length == 2 && parts[0] is "mild" or "moderate" or "strong" ? Tone.Start(parts[1], parts[0]) : Tone.Start(who.Mood, "moderate");
    }

    /// <summary>Acting directions for models that take them: who they are, and (with voice tones on) how they sound right now.</summary>
    public static string Directions(VoiceSubject who, ToneState? tone = null)
    {
        string phrase = tone != null ? Tone.Phrase(tone) : "";
        string how = phrase.Length > 0
            ? $" Your tone right now: {phrase}. It has built up over the conversation, so keep it steady through this line; no sudden swings."
            : tone != null ? " You sound calm and natural right now." : "";
        return ((who.Description.Length > 0 ? $"You are {who.Description}." : "") + how +
               " Talk like a real person face to face on a rainy street in a gritty cyberpunk city: natural, casual, conversational. Not a narrator, not an announcer.").TrimStart();
    }

    private async Task<PcmAudio?> ElevenLabsSpeechAsync(VoiceSlot slot, VoiceAssignment voice, VoiceSubject who, string text, CancellationToken ct)
    {
        string model = ModelOf(slot);
        // eleven_v3 / eleven_v4 act out an audio tag at the start ("[annoyed] ..."); older models would read it aloud.
        if (Tone.ElevenTakesTags(model) && ToneOf(who) is { } tone && Tone.ElevenTag(tone) is { Length: > 0 } tag) text = tag + " " + text;
        var body = new JsonObject { ["text"] = text, ["model_id"] = model };
        var req = new HttpRequestMessage(HttpMethod.Post, $"{ElevenLabsBase(slot)}/v1/text-to-speech/{Uri.EscapeDataString(voice.VoiceId)}?output_format=pcm_24000")
        {
            Content = JsonContent(body),
        };
        req.Headers.Add("xi-api-key", slot.ApiKey.Trim());
        string? cost = null;
        var audio = Audio.FromPcm16(await BytesAsync(req, ct, r => cost = Header(r, "character-cost")).ConfigureAwait(false), 24000, 1);
        // ElevenLabs says what the line cost; worked out from its model only when it doesn't (FB-41).
        if (audio != null) audio.Cost = VoiceCost.FromHeader(cost) ?? VoiceCost.Estimate(VoiceProvider.ElevenLabs, model, text);
        return audio;
    }

    /// <summary>
    /// Azure's speaking rate for a speed (1 = normal) as "+10%" or "-10%", written the same whatever the player's Windows
    /// language (sv-SE wrote U+2212, ar-SA a direction mark before the minus; review of a0fa75f).
    /// </summary>
    internal static string ProsodyRate(double speed)
    {
        int rate = (int)Math.Round((speed - 1) * 100);
        return (rate >= 0 ? "+" : "") + rate.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
    }

    private async Task<PcmAudio?> AzureSpeechAsync(VoiceSlot slot, VoiceAssignment voice, VoiceSubject who, string text, CancellationToken ct)
    {
        string locale = voice.Locale.Length > 0 ? voice.Locale : "en-US";
        string spoken = $"<prosody rate='{ProsodyRate(voice.Speed)}'>{SecurityElement.Escape(text)}</prosody>";
        // A speaking style for voices that have them (a voice without that style simply speaks plainly).
        if (ToneOf(who) is { } tone && Tone.Azure(tone) is { style.Length: > 0 } azure)
            spoken = $"<mstts:express-as style='{azure.style}' styledegree='{azure.degree.ToString(System.Globalization.CultureInfo.InvariantCulture)}'>{spoken}</mstts:express-as>";
        string ssml = $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xmlns:mstts='https://www.w3.org/2001/mstts' xml:lang='{locale}'>" +
                      $"<voice name='{SecurityElement.Escape(voice.VoiceId)}'>{spoken}</voice></speak>";
        var req = new HttpRequestMessage(HttpMethod.Post, AzureBase(slot) + "/cognitiveservices/v1")
        {
            Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml"),
        };
        req.Headers.Add("Ocp-Apim-Subscription-Key", slot.ApiKey.Trim());
        req.Headers.Add("X-Microsoft-OutputFormat", "riff-24khz-16bit-mono-pcm");
        req.Headers.UserAgent.ParseAdd("PeopleOfNivalis/1.0");
        var audio = Audio.FromWav(await BytesAsync(req, ct).ConfigureAwait(false)) ?? throw NotWav();
        audio.Cost = VoiceCost.AzureCharacters(ssml);
        return audio;
    }

    private async Task<PcmAudio?> GoogleSpeechAsync(VoiceSlot slot, VoiceAssignment voice, VoiceSubject who, string text, CancellationToken ct)
    {
        var input = new JsonObject { ["text"] = text };
        // Gemini voices take a style prompt alongside the words.
        if (GoogleNeedsModel(voice.VoiceId) && GeminiModel(slot) && ToneOf(who) is { } tone && Tone.Phrase(tone) is { Length: > 0 } phrase)
            input["prompt"] = $"Say the following like a real person talking on the street, sounding {phrase}.";
        var body = new JsonObject
        {
            ["input"] = input,
            ["voice"] = GoogleNeedsModel(voice.VoiceId) && GeminiModel(slot)
                ? new JsonObject { ["languageCode"] = voice.Locale.Length > 0 ? voice.Locale : "en-US", ["name"] = voice.VoiceId, ["modelName"] = slot.Model.Trim() }
                : new JsonObject { ["languageCode"] = voice.Locale.Length > 0 ? voice.Locale : "en-US", ["name"] = voice.VoiceId },
            ["audioConfig"] = new JsonObject { ["audioEncoding"] = "LINEAR16", ["sampleRateHertz"] = 24000 },
        };
        var req = new HttpRequestMessage(HttpMethod.Post, GoogleBase(slot) + "/text:synthesize") { Content = JsonContent(body) };
        req.Headers.Add("X-Goog-Api-Key", slot.ApiKey.Trim());
        var o = await JsonAsync(req, ct).ConfigureAwait(false);
        string b64 = Str(o?["audioContent"]);
        if (b64.Length == 0) return null;
        var audio = Audio.FromWav(Convert.FromBase64String(b64)) ?? throw NotWav();
        audio.Cost = VoiceCost.Estimate(VoiceProvider.Google, GeminiModel(slot) ? slot.Model : "", text, input["prompt"]?.GetValue<string>() ?? "", seconds: audio.Seconds);
        return audio;
    }

    private async Task<PcmAudio?> DeepgramSpeechAsync(VoiceSlot slot, VoiceAssignment voice, string text, CancellationToken ct)
    {
        string baseUrl = slot.Url.Trim().TrimEnd('/');
        if (baseUrl.Length == 0) baseUrl = "https://api.deepgram.com/v1/speak";
        else if (!baseUrl.EndsWith("/speak", StringComparison.OrdinalIgnoreCase)) baseUrl += "/v1/speak";
        var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}?model={Uri.EscapeDataString(voice.VoiceId)}&encoding=linear16&container=wav&sample_rate=24000")
        {
            Content = JsonContent(new JsonObject { ["text"] = text }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Token", slot.ApiKey.Trim());
        return Audio.FromWav(await BytesAsync(req, ct).ConfigureAwait(false)) ?? throw NotWav();
    }

    // ------------------------------------------------------------------ addresses and defaults

    public static string ModelOf(VoiceSlot slot) => slot.Model.Trim().Length > 0 ? slot.Model.Trim() : slot.Provider switch
    {
        VoiceProvider.OpenAI => "gpt-4o-mini-tts",
        VoiceProvider.ElevenLabs => "eleven_flash_v2_5",
        VoiceProvider.OpenAICompatible => "tts-1",
        _ => "",
    };

    public static string OpenAiBase(VoiceSlot slot)
    {
        string u = slot.Url.Trim().TrimEnd('/');
        if (u.Length == 0) return "https://api.openai.com/v1";
        if (u.EndsWith("/audio/speech", StringComparison.OrdinalIgnoreCase)) u = u[..^"/audio/speech".Length];
        return u;
    }

    private static string ElevenLabsBase(VoiceSlot slot)
    {
        string u = slot.Url.Trim().TrimEnd('/');
        if (u.Length == 0) return "https://api.elevenlabs.io";
        return Regex.Replace(u, @"/v\d+(/.*)?$", "");
    }

    /// <summary>Azure's Url setting is the resource's region ("eastus") or a full endpoint.</summary>
    public static string AzureBase(VoiceSlot slot)
    {
        string u = slot.Url.Trim().TrimEnd('/');
        if (!u.Contains("://")) return $"https://{u.ToLowerInvariant()}.tts.speech.microsoft.com";
        int cut = u.IndexOf("/cognitiveservices", StringComparison.OrdinalIgnoreCase);
        return cut > 0 ? u[..cut] : u;
    }

    /// <summary>A Google voice that needs a model with it: the Gemini ones, named without a language ("Kore", not "en-US-Chirp3-HD-Kore").</summary>
    public static bool GoogleNeedsModel(string voiceId) => !Regex.IsMatch(voiceId ?? "", @"^[a-z]{2,3}-[A-Za-z]{2,4}-");

    private static bool GeminiModel(VoiceSlot slot) => slot.Model.Trim().Contains("gemini", StringComparison.OrdinalIgnoreCase);

    private static string GoogleBase(VoiceSlot slot)
    {
        string u = slot.Url.Trim().TrimEnd('/');
        if (u.Length == 0) return "https://texttospeech.googleapis.com/v1";
        return Regex.IsMatch(u, @"/v\d+(beta\d*)?$") ? u : u + "/v1";
    }

    // ------------------------------------------------------------------ plumbing

    private static InvalidDataException NotWav() => new("sent audio in a format other than WAV/PCM (set the server to WAV output)");

    /// <summary>Spoken text only: no stage directions (*sighs*, (shrugs), [laughs]), sane length.</summary>
    public static string Clean(string text)
    {
        string t = Regex.Replace(text ?? "", @"\*[^*]{0,120}\*", " ");
        t = Regex.Replace(t, @"^\s*[\(\[][^\)\]]{0,80}[\)\]]\s*|\s*[\(\[][^\)\]]{0,80}[\)\]]\s*$", " ");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return t.Length > 900 ? t[..900] : t;
    }

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";

    private static StringContent JsonContent(JsonObject o) => new(o.ToJsonString(), Encoding.UTF8, "application/json");

    private static string? Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : r.Content.Headers.TryGetValues(name, out var c) ? c.FirstOrDefault() : null;

    private async Task<byte[]> BytesAsync(HttpRequestMessage req, CancellationToken ct, Action<HttpResponseMessage>? seen = null)
    {
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        try { seen?.Invoke(resp); } catch { }
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException((int)resp.StatusCode is >= 300 and < 400
                ? $"HTTP {(int)resp.StatusCode}: the address sends requests somewhere else; put the address it moves to in the voice's settings (redirects aren't followed, so your key only goes where you put it)"
                : $"HTTP {(int)resp.StatusCode}: {Ai.Redact.Text(Shorten(Encoding.UTF8.GetString(bytes)))}"); // never a key it repeats
        return bytes;
    }

    private async Task<JsonNode?> JsonNodeAsync(HttpRequestMessage req, CancellationToken ct) =>
        JsonNode.Parse(Encoding.UTF8.GetString(await BytesAsync(req, ct).ConfigureAwait(false)));

    private async Task<JsonObject?> JsonAsync(HttpRequestMessage req, CancellationToken ct) => await JsonNodeAsync(req, ct).ConfigureAwait(false) as JsonObject;

    private async Task<JsonArray?> JsonArrayAsync(HttpRequestMessage req, CancellationToken ct) => await JsonNodeAsync(req, ct).ConfigureAwait(false) as JsonArray;

    private static string Shorten(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 300 ? s[..300] + "…" : s;
    }

    /// <summary>One warning per provider per minute, so a bad key doesn't flood the log.</summary>
    private void Complain(VoiceSlot slot, string what)
    {
        string key = $"{slot.Number}|{what}";
        var now = DateTime.UtcNow;
        if (_lastError.Count > 100) _lastError.Clear();
        if (_lastError.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
        _lastError[key] = now;
        _log($"Voice Provider {slot.Number} ({slot.Provider}) {what}");
    }
}

/// <summary>16-bit PCM and WAV → float samples.</summary>
public static class Audio
{
    public static PcmAudio FromPcm16(byte[] data, int sampleRate, int channels)
    {
        int n = data.Length / 2;
        var samples = new float[n];
        for (int i = 0; i < n; i++) samples[i] = (short)(data[2 * i] | (data[2 * i + 1] << 8)) / 32768f;
        return new PcmAudio { Samples = samples, SampleRate = sampleRate, Channels = Math.Max(1, channels) };
    }

    /// <summary>A RIFF/WAV file (16-bit PCM or 32-bit float). Null for anything else (MP3, an error page...).</summary>
    public static PcmAudio? FromWav(byte[] data)
    {
        if (data.Length < 12 || Encoding.ASCII.GetString(data, 0, 4) != "RIFF" || Encoding.ASCII.GetString(data, 8, 4) != "WAVE") return null;
        int pos = 12, rate = 24000, channels = 1, bits = 16, format = 1;
        while (pos + 8 <= data.Length)
        {
            string id = Encoding.ASCII.GetString(data, pos, 4);
            uint size = BitConverter.ToUInt32(data, pos + 4);
            int start = pos + 8;
            if (id == "fmt " && start + 16 <= data.Length)
            {
                format = BitConverter.ToUInt16(data, start);
                channels = BitConverter.ToUInt16(data, start + 2);
                rate = (int)BitConverter.ToUInt32(data, start + 4);
                bits = BitConverter.ToUInt16(data, start + 14);
            }
            else if (id == "data")
            {
                // Streamed WAVs often carry a placeholder size (0 or 0xFFFFFFFF): read to the end then.
                long len = size == 0 || size == uint.MaxValue || start + (long)size > data.Length ? data.Length - start : size;
                if (bits == 32 && format == 3)
                {
                    var floats = new float[len / 4];
                    for (int i = 0; i < floats.Length; i++) floats[i] = BitConverter.ToSingle(data, start + 4 * i);
                    return new PcmAudio { Samples = floats, SampleRate = rate, Channels = Math.Max(1, channels) };
                }
                if (bits != 16) return null;
                var pcm = new byte[len];
                Buffer.BlockCopy(data, start, pcm, 0, (int)len);
                return FromPcm16(pcm, rate, channels);
            }
            pos = start + (int)Math.Min(size + (size & 1), (uint)(data.Length - start));
            if (size == 0 && id != "data") pos = start;
        }
        return null;
    }
}
