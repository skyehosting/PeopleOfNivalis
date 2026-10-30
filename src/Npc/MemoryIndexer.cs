using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;

namespace NivalisAIChat.Npc;

/// <summary>
/// With MemorySearch = Voyage: keeps everyone's memories indexed by meaning (each memory is sent to Voyage once, when
/// it's new; the vectors are kept on this PC), and finds the ones closest to what someone just heard. In the background,
/// one person at a time; children's lives are never sent anywhere.
/// </summary>
public sealed class MemoryIndexer
{
    private readonly VoyageClient _voyage;
    private readonly NpcStore _people;
    private readonly MemoryStore _store;
    private readonly Func<bool> _on;
    private readonly Action<string> _log;
    private readonly Action<string> _problem;
    private readonly ConcurrentQueue<string> _queue = new();
    /// <summary>Whoever you're talking to now: indexed before everyone else.</summary>
    private readonly ConcurrentQueue<string> _first = new();
    private readonly ConcurrentDictionary<string, byte> _queued = new();
    private readonly MinorList? _minors;
    /// <summary>Children skipped this session (each is logged once).</summary>
    private readonly ConcurrentDictionary<string, byte> _skipped = new();
    private int _running;
    private volatile bool _stopped;
    private readonly CancellationTokenSource _stop = new();
    private DateTime _pauseUntil = DateTime.MinValue;
    /// <summary>The key Voyage refused (its fingerprint): nothing is sent with it again until the key changes (audit B3).</summary>
    private volatile string? _refused;

    /// <summary>Voyage refused the key in use: indexing and searching wait for a new one.</summary>
    public bool KeyRefused => _refused != null && _refused == _voyage.KeyMark;
    private int _added;

    /// <summary>How close in meaning a memory must be to come back (vectors of length 1; 1 = the same).</summary>
    public const float MinCloseness = 0.35f;
    private const int Batch = 64;

    /// <param name="minors">The under-18 list (see <see cref="MinorList"/>): nobody on it is ever indexed or searched.</param>
    public MemoryIndexer(VoyageClient voyage, NpcStore people, MemoryStore store, Func<bool> on, Action<string> log, Action<string> problem,
        MinorList? minors = null, Func<int>? indexLimit = null)
    {
        _voyage = voyage;
        _people = people;
        _store = store;
        _on = on;
        _log = log;
        _problem = problem;
        _minors = minors;
        _indexLimit = indexLimit ?? (() => 0);
    }

    /// <summary>[Memory] MemoryIndexLimit: the most memories kept in each person's index (0 = no limit, the default).</summary>
    private readonly Func<int> _indexLimit;

    public bool On => _on();
    public int Waiting => _queued.Count;
    /// <summary>Memories indexed this session.</summary>
    public int Added => Volatile.Read(ref _added);
    public string LastError { get; private set; } = "";
    public MemoryStore Store => _store;

    /// <summary>
    /// A hot reload: indexing stops now. The person in progress is dropped (their Voyage call cancelled), nothing more is
    /// queued, and nothing is written after this (the index store is closed). The new copy indexes them again from their
    /// records (LEAK-2, RACE-9, audit step 7b).
    /// </summary>
    public void Stop()
    {
        _stopped = true;
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
        while (_queue.TryDequeue(out _)) { }
        while (_first.TryDequeue(out _)) { }
        _queued.Clear();
        _store.Close();
    }

    /// <summary>Their memories changed (a conversation, a call, a thought): index what's new.</summary>
    public void Queue(string id)
    {
        if (_stopped || !_on() || string.IsNullOrEmpty(id) || KeyRefused) return;
        if (_queued.TryAdd(id, 0)) _queue.Enqueue(id);
        Kick();
    }

    /// <summary>You've started talking to them: their memories are indexed next, ahead of everyone else.</summary>
    public void QueueFirst(string id)
    {
        if (_stopped || !_on() || string.IsNullOrEmpty(id) || KeyRefused) return;
        _queued.TryAdd(id, 0);
        _first.Enqueue(id);
        Kick();
    }

    /// <summary>Voyage asked to slow down: memory indexing (and searching) waits a moment.</summary>
    public bool Paused => DateTime.UtcNow < _pauseUntil;

    public void QueueAll(IEnumerable<string> ids)
    {
        if (_stopped || !_on() || KeyRefused) return;
        foreach (var id in ids)
            if (!string.IsNullOrEmpty(id) && _queued.TryAdd(id, 0)) _queue.Enqueue(id);
        Kick();
    }

    private void Kick()
    {
        if (!_stopped && Interlocked.CompareExchange(ref _running, 1, 0) == 0) _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stopped && (_first.TryDequeue(out var id) || _queue.TryDequeue(out id)))
            {
                _queued.TryRemove(id, out _);
                if (!_on() || KeyRefused) { while (_queue.TryDequeue(out _)) { } while (_first.TryDequeue(out _)) { } _queued.Clear(); break; }
                var wait = _pauseUntil - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, _stop.Token).ConfigureAwait(false);
                if (_stopped) break; // checked again after the pause
                string mark = _voyage.KeyMark; // the key this try uses (one pasted meanwhile isn't the refused one: review R8)
                try
                {
                    int n = await IndexAsync(id, _stop.Token).ConfigureAwait(false);
                    if (n > 0) Interlocked.Add(ref _added, n);
                    LastError = "";
                }
                catch (VoyageException e) when (e.Status is 401 or 403)
                {
                    LastError = e.Message;
                    _problem("Voyage refused the key, so memories aren't being indexed (VoyageKey in the config): " + e.Message);
                    _refused = mark; // not asked again with this key (a new one in the config starts it again)
                    while (_queue.TryDequeue(out _)) { }
                    while (_first.TryDequeue(out _)) { }
                    _queued.Clear();
                    break;
                }
                catch (VoyageException e) when (e.Status == 429 || e.Status >= 500 || e.Status == 0)
                {
                    LastError = e.Message;
                    _log("Memory indexing paused a moment: " + e.Message);
                    _pauseUntil = DateTime.UtcNow.AddSeconds(e.Status == 429 ? 30 : 15);
                    if (_queued.TryAdd(id, 0)) _queue.Enqueue(id); // tried again after the pause
                }
                catch (OperationCanceledException) when (_stopped) { break; } // stopped mid-call: dropped, not a failure
                catch (Exception e)
                {
                    LastError = e.Message;
                    _log($"Indexing {id}'s memories failed: {e.Message}");
                }
                await Task.Delay(200, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stopped) { } // stopped during a pause
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            if ((!_queue.IsEmpty || !_first.IsEmpty) && _on()) Kick();
        }
    }

    /// <summary>Indexes whatever of their memories isn't indexed yet. Returns how many were added.</summary>
    public async Task<int> IndexAsync(string id, CancellationToken ct)
    {
        long generation = _store.GenerationOf(id); // before anything is read: archived since then (a reset) means nothing is saved
        var rec = _people.Peek(id); // can't be read right now: nothing is indexed (or sent) for them this time
        if (rec == null) return 0;
        // Children's lives are never sent anywhere: on the list, flagged, or a file from an older version that is a
        // child's without saying so. Found that way, they go on the list too.
        var (why, lasting) = _minors?.Contains(id) == true ? ("on the under-18 list", false) : Minors.Judge(rec);
        if (why != null)
        {
            if (lasting) _minors?.Add(id);
            if (_skipped.TryAdd(id, 0)) _log($"Not indexing {id}: under 18 ({why}). Their memories are never sent to Voyage.");
            return 0;
        }
        var file = _store.Load(id);
        if (file == null) return 0; // their index can't be read right now: tried again when they're next queued (nothing sent, nothing written over)
        string sig = _voyage.Signature;
        if (file.Signature != sig) { file.Entries.Clear(); file.Signature = sig; } // another model or size: made again with this one
        var have = file.Entries.Select(e => e.Key).ToHashSet();
        var fresh = Memories.From(rec).Where(e => !have.Contains(e.Key)).GroupBy(e => e.Key).Select(g => g.First()).ToList();
        int limit = Math.Max(0, _indexLimit());
        if (limit > 0)
        {
            // MemoryIndexLimit: the newest are kept, the oldest go. At the same game day (a whole meeting shares one; the
            // clock pauses while you talk), what's indexed already comes first, then a fixed order, so running again with
            // nothing new embeds nothing and drops nothing. Only new memories inside the cut are sent to Voyage.
            var keep = file.Entries.Select(e => (entry: e, indexed: true)).Concat(fresh.Select(e => (entry: e, indexed: false)))
                .OrderByDescending(x => x.entry.Day).ThenByDescending(x => x.indexed).ThenBy(x => x.entry.Key, StringComparer.Ordinal)
                .Take(limit).Select(x => x.entry.Key).ToHashSet();
            int dropped = file.Entries.RemoveAll(e => !keep.Contains(e.Key));
            fresh.RemoveAll(e => !keep.Contains(e.Key));
            if (fresh.Count == 0)
            {
                if (dropped > 0) _store.Save(id, file, generation); // a lower limit applies at once (not saved if a reset archived it)
                return 0;
            }
        }
        if (fresh.Count == 0) return 0;
        for (int i = 0; i < fresh.Count; i += Batch)
        {
            var part = fresh.Skip(i).Take(Batch).ToList();
            var vecs = await _voyage.EmbedAsync(part.Select(e => e.Text).ToList(), query: false, ct).ConfigureAwait(false);
            for (int j = 0; j < part.Count; j++) part[j].Vec = Memories.Pack(vecs[j]);
            file.Entries.AddRange(part);
            if (!_store.Save(id, file, generation)) return i; // a reset archived their index meanwhile: it stays archived
        }
        return fresh.Count;
    }

    /// <summary>
    /// What <paramref name="heard"/> brings back to them: their closest memories by meaning (up to <paramref name="k"/>).
    /// Empty when Voyage is off, slow (4 seconds) or there's nothing indexed for them yet.
    /// </summary>
    public async Task<List<MemoryEntry>> RecallAsync(string id, string heard, int k, Func<MemoryEntry, bool>? skip, CancellationToken ct)
    {
        if (!_on() || string.IsNullOrWhiteSpace(heard) || Paused || KeyRefused) return new(); // slowed down, or the key refused: their words alone will do
        if (_minors?.Contains(id) == true) return new(); // never for a child (they aren't chatted with; this is a second lock)
        var file = _store.Load(id);
        if (file == null || file.Entries.Count == 0 || file.Signature != _voyage.Signature) return new();
        using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quick.CancelAfter(TimeSpan.FromSeconds(4));
        var q = (await _voyage.EmbedAsync(new[] { heard }, query: true, quick.Token).ConfigureAwait(false))[0];
        return Memories.Search(file, q, k, MinCloseness, skip).Select(x => x.entry).ToList();
    }
}
