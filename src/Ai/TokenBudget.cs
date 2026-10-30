using System;
using System.IO;
using System.Text.Json.Nodes;

namespace NivalisAIChat.Ai;

/// <summary>
/// The most AI tokens the mod may use per hour, day or month (TokenLimit, TokenLimitPer): everything it asks the AI for,
/// sent and received, as the provider counts it (estimated from the text when it doesn't say). Once the limit is reached,
/// nothing more is asked until the next hour/day/month starts. Kept in a small file so a day's use survives a restart.
/// </summary>
public sealed class TokenBudget
{
    public enum Span { Hour, Day, Week, Month }

    /// <summary>What a budget counts and how it talks about it: AI tokens (TokenLimit), or voice characters (VoiceLimit, CF-02).</summary>
    public sealed record Wording(string Unit, string Setting, string PerSetting, string Title, string Stops, string Code)
    {
        public static readonly Wording Ai = new("AI tokens", "TokenLimit", "TokenLimitPer", "Token limit reached",
            "Nothing more is asked of the AI until then", "AI-BUDGET");
        public static readonly Wording Voice = new("voice characters", "VoiceLimit", "VoiceLimitPer", "Voice limit reached",
            "Lines are shown without voices until then", "VOICE-BUDGET");
        /// <summary>FB-75: speech to text (voice input), counted in seconds of what you said.</summary>
        public static readonly Wording Speech = new("seconds of speech to text", "SpeechToTextLimit", "SpeechToTextLimitPer", "Speech-to-text limit reached",
            "Type in the chat box until then", "SPEECH-BUDGET");
    }

    private readonly Wording _words;
    private readonly string? _path;
    private readonly Func<(long limit, Span per)> _settings;
    private readonly object _lock = new();
    private string _period = "";
    private long _used;
    private readonly Action<string>? _warn;
    // usage.json couldn't be read at the start (locked, or damaged and couldn't be moved aside): counted in memory only.
    private bool _readOnly, _warnedReadOnly;

    /// <summary>The clock (local time); tests set it.</summary>
    public Func<DateTime> Now = () => DateTime.Now;
    /// <summary>FB-59: the day of the month a monthly limit starts again on (1: the 1st, as before; past a short month's end, its last day).</summary>
    public Func<int> ResetDay = () => 1;

    public TokenBudget(Func<(long limit, Span per)> settings, string? path = null, Action<string>? warn = null, Wording? wording = null)
    {
        _words = wording ?? Wording.Ai;
        _settings = settings;
        _path = path;
        _warn = warn;
        Load();
    }

    public static Span? SpanOf(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "hour" or "hourly" or "h" => Span.Hour,
        "day" or "daily" or "d" => Span.Day,
        "week" or "weekly" or "w" => Span.Week,
        "month" or "monthly" or "m" => Span.Month,
        _ => null,
    };

    /// <summary>The period a time falls in, in the Gregorian calendar whatever the player's Windows language (a Hijri or Buddhist year changed it).</summary>
    public static string PeriodKey(DateTime t, Span per, int resetDay = 1) => per switch
    {
        Span.Hour => "hour " + t.ToString("yyyy-MM-dd HH", System.Globalization.CultureInfo.InvariantCulture),
        // A month from the 1st keeps its old key ("month 2026-10"), so an update doesn't start the count again.
        Span.Month => resetDay <= 1 ? "month " + t.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)
                                    : "month " + MonthStart(t, resetDay).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        Span.Week => "week " + WeekStart(t).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        _ => "day " + t.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>When the current hour/day/month ends.</summary>
    public static DateTime NextReset(DateTime t, Span per, int resetDay = 1) => per switch
    {
        Span.Hour => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0).AddHours(1),
        Span.Month => MonthAfter(MonthStart(t, resetDay), resetDay),
        Span.Week => WeekStart(t).AddDays(7),
        _ => t.Date.AddDays(1),
    };

    /// <summary>When the current hour/day/week/month began.</summary>
    public static DateTime Start(DateTime t, Span per, int resetDay = 1) => per switch
    {
        Span.Hour => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0),
        Span.Month => MonthStart(t, resetDay),
        Span.Week => WeekStart(t),
        _ => t.Date,
    };

    /// <summary>
    /// FB-59: when the month counted now began, for a limit that starts again on <paramref name="resetDay"/> (1-31; in a
    /// shorter month, its last day), at 00:00 by the Windows clock.
    /// </summary>
    public static DateTime MonthStart(DateTime t, int resetDay)
    {
        var here = OnDay(t.Year, t.Month, resetDay);
        if (t >= here) return here;
        var before = new DateTime(t.Year, t.Month, 1).AddMonths(-1);
        return OnDay(before.Year, before.Month, resetDay);
    }

    private static DateTime MonthAfter(DateTime start, int resetDay)
    {
        var next = new DateTime(start.Year, start.Month, 1).AddMonths(1);
        return OnDay(next.Year, next.Month, resetDay);
    }

    private static DateTime OnDay(int year, int month, int day) => new(year, month, Math.Min(Math.Clamp(day, 1, 31), DateTime.DaysInMonth(year, month)));

    /// <summary>The Monday a week starts on (00:00).</summary>
    public static DateTime WeekStart(DateTime t) => t.Date.AddDays(-(((int)t.DayOfWeek + 6) % 7));

    public long Limit => Math.Max(0, _settings().limit);
    public Span Per => _settings().per;

    /// <summary>Tokens used in the current hour/day/month.</summary>
    public long Used
    {
        get { lock (_lock) { Roll(); return _used; } }
    }

    /// <summary>The hour/day/month counted now ("day 2026-10-13"): a message said once per period goes by it.</summary>
    public string Period
    {
        get { lock (_lock) { Roll(); return _period; } }
    }

    /// <summary>Room for another request (always, with no limit set).</summary>
    public bool Allows()
    {
        long limit = Limit;
        return limit <= 0 || Used < limit;
    }

    public void Add(long tokens)
    {
        if (tokens <= 0) return;
        string? text;
        long seq;
        lock (_lock)
        {
            Roll();
            _used += tokens;
            text = SaveText();
            seq = ++_saveSeq;
        }
        if (text != null) Write(text, seq);
    }

    /// <summary>A rough count for an endpoint that doesn't report usage: about four characters a token.</summary>
    public static long Estimate(int sentChars, int receivedChars) => (Math.Max(0, sentChars) + Math.Max(0, receivedChars) + 3) / 4;

    public static string Word(Span per) => per switch { Span.Hour => "hour", Span.Week => "week", Span.Month => "month", _ => "day" };

    /// <summary>"12,345 of 200,000 AI tokens used this day (starts again at 00:00)", or with no limit, just the use.</summary>
    public string Describe()
    {
        var (limit, per) = _settings();
        long used = Used;
        if (limit <= 0) return $"{used:N0} {_words.Unit} used this {Word(per)} (no limit set: {_words.Setting} in the config).";
        var reset = NextReset(Now(), per, ResetDay());
        string when = per == Span.Hour || reset.Date == Now().Date.AddDays(1) ? reset.ToString("HH:mm") : reset.ToString("MMM d");
        return $"{used:N0} of {limit:N0} {_words.Unit} used this {Word(per)} ({Math.Min(100, used * 100 / Math.Max(1, limit))}%; starts again at {when}).";
    }

    /// <summary>FB-51: Describe in parts, for /usage: "12,345 of 200,000 tokens this day (6%)" and when it starts again ("" with no limit).</summary>
    public (string used, string again) Status()
    {
        var (limit, per) = _settings();
        long used = Used;
        if (limit <= 0) return ($"{used:N0} {_words.Unit} this {Word(per)} (no limit set)", "");
        var reset = NextReset(Now(), per, ResetDay());
        string when = per == Span.Hour || reset.Date == Now().Date.AddDays(1) ? reset.ToString("HH:mm") : reset.ToString("MMM d");
        return ($"{used:N0} of {limit:N0} {_words.Unit} this {Word(per)} ({Math.Min(999, used * 100 / Math.Max(1, limit))}%)", when);
    }

    /// <summary>The message when the limit stops a request (the voice one carries its code: no other text says "voice limit").</summary>
    public string LimitMessage() =>
        (_words == Wording.Ai ? "" : $"[{_words.Code}] ") +
        $"{_words.Title}: {Describe()} {_words.Stops}; raise {_words.Setting} in the config (0 = no limit) to go on now.";

    private void Roll()
    {
        var now = Now();
        string key = PeriodKey(now, _settings().per, ResetDay());
        if (key == _period) return;
        // Review F59-1: a monthly limit's reset day moved while its month is still on: the same month goes on, its count kept
        // (only the key changes), so moving the day can't wipe what was used.
        bool keep = _period.StartsWith("month ", StringComparison.Ordinal) && key.StartsWith("month ", StringComparison.Ordinal) && StillOn(_period, now);
        _period = key;
        if (!keep) _used = 0;
    }

    /// <summary>Whether the month a key names ("month 2026-10" from the 1st, "month 2026-10-11" from the 11th) hasn't ended by <paramref name="now"/>.</summary>
    private static bool StillOn(string monthKey, DateTime now)
    {
        string d = monthKey.Substring(6);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (DateTime.TryParseExact(d, "yyyy-MM-dd", inv, System.Globalization.DateTimeStyles.None, out var start))
            return now < MonthAfter(start, start.Day) && now >= start;
        if (DateTime.TryParseExact(d, "yyyy-MM", inv, System.Globalization.DateTimeStyles.None, out var first))
            return now < first.AddMonths(1) && now >= first;
        return false;
    }

    private void Load()
    {
        if (_path == null) return;
        // Damaged: kept aside and counting starts again; locked: counted in memory only, the file left as it is (ROBUST-4).
        var read = Npc.DataFile.LoadBook(_path, t =>
        {
            if (JsonNode.Parse(t) is not JsonObject o) return null;
            try
            {
                string period = o["period"] is JsonValue p && p.TryGetValue(out string? s) ? s ?? "" : "";
                long used = o["used"] is JsonValue u && u.TryGetValue(out long n) ? n : 0;
                return Tuple.Create(period, used);
            }
            catch (ArgumentException e) { throw new System.Text.Json.JsonException(e.Message, e); } // a key written twice: damaged
        }, _warn, out _readOnly);
        if (read == null) return;
        (_period, _used) = (read.Item1, read.Item2);
    }

    /// <summary>Stop (a hot reload): nothing more is written; a late change is said once (audit step 7b, review of a0fa75f).</summary>
    public void Close()
    {
        lock (_lock) _closed = true;
    }

    private volatile bool _closed; // read by Write outside the lock
    private bool _saidClosed;

    // FB-51: written to the disk before it replaces the old file (a power cut or a reboot left usage.json all zeros), outside
    // the lock AiAllowed reads on the game's thread; a newer count is never written over by an older one.
    private readonly object _writeLock = new();
    private long _saveSeq, _writtenSeq;

    /// <summary>What to write (taken under the lock), or null when nothing is written.</summary>
    private string? SaveText()
    {
        if (_path == null) return null;
        if (_readOnly) { Npc.DataFile.NotSaved(_path, _warn, ref _warnedReadOnly); return null; }
        if (_closed) { Npc.DataFile.Stopped(Path.GetFileName(_path), _warn, ref _saidClosed); return null; }
        return new JsonObject { ["period"] = _period, ["used"] = _used }.ToJsonString();
    }

    private void Write(string text, long seq)
    {
        lock (_writeLock)
        {
            if (seq <= _writtenSeq || _closed) return; // closed meanwhile (a hot reload): the new copy has the file
            try { Npc.DataFile.WriteAtomic(_path!, text, durable: true); _writtenSeq = seq; }
            catch { }
        }
    }
}
