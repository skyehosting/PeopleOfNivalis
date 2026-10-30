using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NivalisAIChat.Voice;

/// <summary>
/// FB-41: what every spoken line cost, per voice provider, in its own unit, kept so limits can be tracked exactly and the
/// calculator can learn: totals per day (a period's use is the sum of its days), the cost of a line (on average, and its
/// highs: the 90th percentile of the last 200 lines), what an hour of play costs (on average and its highs), per purpose
/// (your chats, residents talking, calls...), and how long you play each day. Where the provider can say what the account
/// has used this period (ElevenLabs), its count is the one used, plus what was spoken since it was read.
/// A provider is kept by its endpoint and model, never its key. Thread-safe; written at most every 30 s. No Unity.
/// </summary>
public sealed class VoiceLedger
{
    /// <summary>A running average and spread (Welford), with the largest seen.</summary>
    public sealed class Stat
    {
        public long N { get; set; }
        public double Mean { get; set; }
        public double M2 { get; set; }
        public double Max { get; set; }

        public void Add(double x)
        {
            N++;
            double d = x - Mean;
            Mean += d / N;
            M2 += d * (x - Mean);
            if (x > Max) Max = x;
        }

        [JsonIgnore] public double Sd => N > 1 ? Math.Sqrt(M2 / (N - 1)) : 0;
    }

    private sealed class Book
    {
        public string Name { get; set; } = "";
        public string Unit { get; set; } = "";
        public Dictionary<string, double> Days { get; set; } = new();
        public Stat Line { get; set; } = new();
        public Dictionary<string, Stat> Purposes { get; set; } = new();
        public List<double> Recent { get; set; } = new();
        public List<double> Hours { get; set; } = new();
        public double ThisHour { get; set; }
        public double? AccountUsed { get; set; }
        public DateTime AccountPeriodStart { get; set; }
        public double SinceAccount { get; set; }
    }

    private sealed class Data
    {
        public Dictionary<string, Book> Books { get; set; } = new();
        public Dictionary<string, int> PlayMinutes { get; set; } = new();
        public int MinuteInHour { get; set; }
        /// <summary>FB-59: 2 once play-hours are counted with the window behind others too (F-0172); older hours were far too long.</summary>
        public int HoursVersion { get; set; }
    }

    public const int RecentLines = 200, KeptHours = 100, KeptDays = 400;

    /// <summary>What's known of one provider's spending, for /usage, the menu and the calculator.</summary>
    public sealed record Summary(string Name, string Unit, long Lines, double LineMean, double LineHigh, double LineMax,
        double HourMean, double HourHigh, int HoursSeen, IReadOnlyDictionary<string, (long lines, double mean)> Purposes);

    private readonly object _lock = new();
    private readonly string? _path;
    private readonly Action<string>? _warn;
    private Data _data = new();
    private DateTime _lastSave = DateTime.MinValue;
    private bool _dirty, _readOnly, _warnedReadOnly, _closed, _saidClosed;

    /// <summary>The clock (local time); tests set it.</summary>
    public Func<DateTime> Now = () => DateTime.Now;

    public VoiceLedger(string? path = null, Action<string>? warn = null)
    {
        _path = path;
        _warn = warn;
        Load();
    }

    private static string DayKey(DateTime t) => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A line was spoken: what it cost (<paramref name="units"/>, in <paramref name="unit"/>) and what it was for.</summary>
    public void Record(string key, string name, string unit, string purpose, double units)
    {
        if (string.IsNullOrEmpty(key) || !(units >= 0) || !double.IsFinite(units)) return;
        lock (_lock)
        {
            if (!_data.Books.TryGetValue(key, out var b)) _data.Books[key] = b = new Book();
            b.Name = name;
            b.Unit = unit;
            string day = DayKey(Now());
            b.Days[day] = (b.Days.TryGetValue(day, out var d) ? d : 0) + units;
            if (b.Days.Count > KeptDays) foreach (var old in b.Days.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(b.Days.Count - KeptDays).ToList()) b.Days.Remove(old);
            b.Line.Add(units);
            string p = string.IsNullOrWhiteSpace(purpose) ? "other" : purpose;
            if (!b.Purposes.TryGetValue(p, out var ps)) b.Purposes[p] = ps = new Stat();
            ps.Add(units);
            b.Recent.Add(units);
            if (b.Recent.Count > RecentLines) b.Recent.RemoveRange(0, b.Recent.Count - RecentLines);
            b.ThisHour += units;
            if (b.AccountUsed != null) b.SinceAccount += units;
            _dirty = true;
            SaveSoon();
        }
    }

    /// <summary>
    /// What this provider has used since <paramref name="start"/> (a period's start): the provider's own count when it gave one
    /// for this period (plus what's been spoken since), otherwise the sum of the days from that day on.
    /// </summary>
    public double UsedSince(string key, DateTime start)
    {
        lock (_lock)
        {
            if (!_data.Books.TryGetValue(key, out var b)) return 0;
            if (b.AccountUsed is double acc && Math.Abs((b.AccountPeriodStart - start).TotalHours) < 1) return acc + b.SinceAccount;
            string from = DayKey(start);
            return b.Days.Where(kv => string.CompareOrdinal(kv.Key, from) >= 0).Sum(kv => kv.Value);
        }
    }

    /// <summary>
    /// FB-59: what this PC used of a provider since <paramref name="start"/>: the sum of the days from that day on (never the
    /// account's own count, which takes in everyone sharing the key).
    /// </summary>
    public double OwnUsedSince(string key, DateTime start)
    {
        lock (_lock)
        {
            if (!_data.Books.TryGetValue(key, out var b)) return 0;
            string from = DayKey(start);
            return b.Days.Where(kv => string.CompareOrdinal(kv.Key, from) >= 0).Sum(kv => kv.Value);
        }
    }

    /// <summary>FB-59: what was spoken here since the account was last read (0 when it never was).</summary>
    public double SinceAccountOf(string key)
    {
        lock (_lock) return _data.Books.TryGetValue(key, out var b) && b.AccountUsed != null ? b.SinceAccount : 0;
    }

    /// <summary>The provider says what the account has used this period (ElevenLabs' subscription): that's the count from now.</summary>
    public void Calibrate(string key, string name, string unit, double used, DateTime periodStart)
    {
        if (!(used >= 0) || !double.IsFinite(used)) return;
        lock (_lock)
        {
            if (!_data.Books.TryGetValue(key, out var b)) _data.Books[key] = b = new Book { Name = name, Unit = unit };
            // Read again in the same period: never lower than what's known (the account may not have counted the last lines yet).
            if (b.AccountUsed is double before && Math.Abs((b.AccountPeriodStart - periodStart).TotalHours) < 1) used = Math.Max(used, before + b.SinceAccount);
            b.AccountUsed = used;
            b.AccountPeriodStart = periodStart;
            b.SinceAccount = 0;
            _dirty = true;
            SaveSoon();
        }
    }

    /// <summary>A minute of play went by (in the world, not paused): the play time, and an hour's spending once 60 have.</summary>
    public void PlayMinute()
    {
        lock (_lock)
        {
            string day = DayKey(Now());
            _data.PlayMinutes[day] = (_data.PlayMinutes.TryGetValue(day, out var m) ? m : 0) + 1;
            if (_data.PlayMinutes.Count > KeptDays)
                foreach (var old in _data.PlayMinutes.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(_data.PlayMinutes.Count - KeptDays).ToList()) _data.PlayMinutes.Remove(old);
            if (++_data.MinuteInHour >= 60)
            {
                _data.MinuteInHour = 0;
                foreach (var b in _data.Books.Values)
                {
                    b.Hours.Add(b.ThisHour);
                    if (b.Hours.Count > KeptHours) b.Hours.RemoveRange(0, b.Hours.Count - KeptHours);
                    b.ThisHour = 0;
                }
                _dirty = true;
                SaveNow();
                return;
            }
            _dirty = true;
            SaveSoon();
        }
    }

    /// <summary>Average play-hours a day over the last <paramref name="days"/> days (days without play count as none), and how many were played.</summary>
    public (double hoursPerDay, int daysPlayed) PlayTime(int days = 14)
    {
        lock (_lock)
        {
            var today = Now().Date;
            int minutes = 0, played = 0;
            for (int i = 0; i < days; i++)
                if (_data.PlayMinutes.TryGetValue(DayKey(today.AddDays(-i)), out var m) && m > 0) { minutes += m; played++; }
            return (minutes / 60.0 / Math.Max(1, days), played);
        }
    }

    /// <summary>
    /// FB-59: the hours' average with newer ones counting more (each hour back weighs 3% less: an hour 23 play-hours ago
    /// counts half), so the plan follows how you play now; 0 with none.
    /// </summary>
    public static double Recent(IReadOnlyList<double> hours)
    {
        if (hours.Count == 0) return 0;
        double sum = 0, weights = 0, w = 1;
        for (int i = hours.Count - 1; i >= 0; i--, w *= 0.97) { sum += w * hours[i]; weights += w; }
        return sum / weights;
    }

    /// <summary>The 90th percentile of a list (its "highs"): what a busy line or a busy hour costs.</summary>
    public static double High(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int i = (int)Math.Ceiling(0.9 * sorted.Count) - 1;
        return sorted[Math.Clamp(i, 0, sorted.Count - 1)];
    }

    public Summary? Of(string key)
    {
        lock (_lock)
        {
            if (!_data.Books.TryGetValue(key, out var b)) return null;
            return new Summary(b.Name, b.Unit, b.Line.N, b.Line.Mean, High(b.Recent), b.Line.Max,
                Recent(b.Hours), High(b.Hours), b.Hours.Count,
                b.Purposes.ToDictionary(kv => kv.Key, kv => (kv.Value.N, kv.Value.Mean)));
        }
    }

    public IReadOnlyList<string> Keys { get { lock (_lock) return _data.Books.Keys.ToList(); } }

    // ------------------------------------------------------------------ the file

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private void Load()
    {
        if (_path == null) return;
        var read = Npc.DataFile.LoadBook(_path, t => JsonSerializer.Deserialize<Data>(t, Json), _warn, out _readOnly);
        if (read != null) _data = read;
        // A file with a part emptied ("Days": null, by hand or a later version) is repaired, never a failure later (review of 5262c60).
        _data.Books ??= new();
        _data.PlayMinutes ??= new();
        foreach (var key in _data.Books.Keys.ToList())
        {
            var b = _data.Books[key] ??= new Book();
            b.Name ??= "";
            b.Unit ??= "";
            b.Days ??= new();
            b.Line ??= new();
            b.Purposes ??= new();
            foreach (var p in b.Purposes.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) b.Purposes[p] = new Stat();
            b.Recent ??= new();
            b.Hours ??= new();
        }
        // FB-59 (F-0172): play-hours counted before 0.5.1 only while the window was in front, so one "hour" could hold days of
        // spending. Those hours are dropped once and learned again; the days, lines and what each purpose costs stay.
        if (_data.HoursVersion < 2)
        {
            bool had = _data.Books.Values.Any(b => b.Hours.Count > 0 || b.ThisHour > 0) || _data.PlayMinutes.Count > 0;
            foreach (var b in _data.Books.Values) { b.Hours.Clear(); b.ThisHour = 0; }
            _data.PlayMinutes.Clear(); // the play days were counted the same way: learned again too (review F59-2)
            _data.MinuteInHour = 0;
            _data.HoursVersion = 2;
            if (had) _dirty = true; // written with the next line or minute (not a problem to report)
        }
    }

    private void SaveSoon()
    {
        if ((Now() - _lastSave).TotalSeconds >= 30) SaveNow();
    }

    private string? _toWrite;
    private int _writing;

    /// <summary>
    /// Saved off the game's thread (review of 5262c60: never the disk on the main thread, nor under the lock lines are recorded
    /// with): the record is turned into text here, under the lock, and a pool thread writes the newest text there is.
    /// </summary>
    private void SaveNow()
    {
        if (_path == null || !_dirty) return;
        if (_readOnly) { Npc.DataFile.NotSaved(_path, _warn, ref _warnedReadOnly); return; }
        if (_closed) { Npc.DataFile.Stopped(Path.GetFileName(_path), _warn, ref _saidClosed); return; }
        _toWrite = JsonSerializer.Serialize(_data, Json);
        _dirty = false;
        _lastSave = Now();
        if (Interlocked.Exchange(ref _writing, 1) == 0) Task.Run(WriteLoop);
    }

    private void WriteLoop()
    {
        while (true)
        {
            string? text;
            lock (_lock)
            {
                text = _toWrite;
                _toWrite = null;
                if (text == null) { Volatile.Write(ref _writing, 0); return; }
            }
            try { Npc.DataFile.WriteAtomic(_path!, text, durable: true); } // off the game's thread: to the disk first (FB-51)
            catch { lock (_lock) _dirty = true; } // tried again at the next save
        }
    }

    /// <summary>Written now (the game closing); nothing more after (a hot reload).</summary>
    public void Close()
    {
        lock (_lock) SaveNow();
        // The last text written before anything else may be: the writer finishes first (a few milliseconds at most).
        for (int i = 0; i < 200 && Volatile.Read(ref _writing) != 0; i++) Thread.Sleep(10);
        lock (_lock) _closed = true;
    }
}
