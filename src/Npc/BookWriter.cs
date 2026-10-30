using System;
using System.Diagnostics;
using System.Threading;

namespace NivalisAIChat.Npc;

/// <summary>
/// Writes one small book (pairs, visits) off the main thread, the newest state always last (audit step 6c: D6, D10;
/// PERF-9, LEAK-4). A change asks for a save (<see cref="SaveSoon"/>) and one pool thread writes, taking in the changes
/// that come in meanwhile. Every write, the pool's and a caller's own (<see cref="Write"/>, or a reset in
/// <see cref="Exclusive"/>), holds the gate while it takes its snapshot and commits it, so an older state never lands
/// after a newer one. No timers: BepInEx never calls Unload when the game quits, so nothing is left waiting to be written.
/// </summary>
public sealed class BookWriter
{
    private readonly string _path;
    private readonly Func<string?> _snapshot;
    private readonly Action<string>? _warn;
    private readonly Action? _failed;
    private readonly object _gate = new(), _sync = new();
    private bool _requested, _running, _warned, _saidClosed;
    private volatile bool _closed;

    /// <param name="snapshot">The book's text now, taken under its own lock (null: nothing to write, or it's read-only).</param>
    /// <param name="failed">After a write that failed (the book marks itself changed again).</param>
    public BookWriter(string path, Func<string?> snapshot, Action<string>? warn, Action? failed = null)
    {
        _path = path;
        _snapshot = snapshot;
        _warn = warn;
        _failed = failed;
    }

    /// <summary>Tests only: runs inside the gate, after the snapshot and before it's written.</summary>
    internal Action? BeforeWrite { get; set; }

    /// <summary>How the pool's writing is started (tests: held back, to run later).</summary>
    internal Action<Action> Schedule { get; set; } = work => ThreadPool.QueueUserWorkItem(_ => work());

    /// <summary>Something changed: it's written on a pool thread now, or right after the write in progress.</summary>
    public void SaveSoon()
    {
        if (_closed) { SayClosed(); return; }
        lock (_sync)
        {
            _requested = true;
            if (_running) return;
            _running = true;
        }
        Schedule(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            lock (_sync)
            {
                if (!_requested) { _running = false; return; }
                _requested = false;
            }
            try { Write(); }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; _warn?.Invoke($"Couldn't save {System.IO.Path.GetFileName(_path)} ({e.Message}); it's tried again with the next change."); }
            }
        }
    }

    /// <summary>Writes the book now, on the caller's thread, after any write in progress. A failure is the caller's to see.</summary>
    public void Write()
    {
        lock (_gate)
        {
            if (_closed) { SayClosed(); return; }
            string? text = _snapshot();
            if (text == null) return;
            BeforeWrite?.Invoke();
            // Flushed to the disk: this runs on a pool thread (or a reset), so nobody waits on it, and a power cut can't
            // leave a zero-filled book that would be moved aside and start empty (6c review).
            try { DataFile.WriteAtomic(_path, text, durable: true); }
            catch { _failed?.Invoke(); throw; } // not written: the book counts as changed again
        }
    }

    /// <summary>
    /// A hot reload: after the last flush, nothing more is written. The new copy owns the file now, and a late change of
    /// this copy's would write over it (audit step 7b). Waits for a write in progress.
    /// </summary>
    public void Close()
    {
        lock (_gate) _closed = true;
    }

    private void SayClosed()
    {
        if (_saidClosed) return;
        _saidClosed = true;
        _warn?.Invoke($"The mod has stopped, so a late change to {System.IO.Path.GetFileName(_path)} wasn't saved.");
    }

    /// <summary>Holds the gate for work that must not interleave with a write (a reset: write, back up, then clear).</summary>
    public void Exclusive(Action work)
    {
        lock (_gate) work();
    }

    /// <summary>Waits (up to <paramref name="ms"/>) until nothing is waiting to be written. Tests and a hot reload.</summary>
    public bool WaitIdle(int ms = 2000)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            lock (_sync) if (!_running) return true;
            if (clock.ElapsedMilliseconds > ms) return false;
            Thread.Sleep(5);
        }
    }
}
