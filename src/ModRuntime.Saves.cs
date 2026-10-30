using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using UnityEngine;

namespace NivalisAIChat;

/// <summary>
/// FB-61: the city's memory belongs to the game save being played (see <see cref="SaveSnapshots"/>). A save that worked
/// takes the working copy into that slot's snapshot; the first moment of play after a load puts that slot's snapshot back
/// (what was said since it was saved is gone), and a new game starts with nobody knowing you. Before anything is put back,
/// the working copy as it was is kept (a before-load snapshot), so nothing is ever lost to this.
///
/// The game keeps no "current slot": it's followed here from its own Load and Save calls (Game/SaveHooks.cs). The swap
/// itself is the /resetall path's: talk ends, a new epoch refuses late writes, work settles, the per-save books close,
/// the files are put back on the pool, and the books open again on the game's thread.
/// </summary>
public static partial class ModRuntime
{
    private static SaveSnapshots? _saves;
    /// <summary>The slot being played, as far as the mod knows ("" = not known yet: before the first load or save).</summary>
    private static string _activeSlot = "";
    /// <summary>A game starting: a load was asked for (its slot), or a new game. Decided at the first moment of play.</summary>
    private static bool _gameStarting = true;
    private static string? _pendingLoad;
    private static bool _newGameAsked;
    private static bool _swapping;
    private static Task _lastTake = Task.CompletedTask;
    /// <summary>Review F2: the scene count when a load or new game was asked for: play must be in a new scene before it counts.</summary>
    private static int _askedAtScene = -1, _askedDay = -1;
    private static IntPtr _askedPlayer;
    private static float _askedAt = -1f;
    /// <summary>Review F14: a save made while a swap was going on, taken once it's done.</summary>
    private static (string slot, bool autosave)? _saveAfterSwap;
    private static Task _legacyTake = Task.CompletedTask;

    /// <summary>Set up at start: the legacy snapshot the first time, and old snapshots tidied a little later.</summary>
    private static void StartSaves(string dataDir, bool hotReloaded)
    {
        _saves = new SaveSnapshots(dataDir, Log.Warn);
        // A hot-reloaded copy starts in the middle of play: what's in the working copy is the slot being played.
        _gameStarting = !hotReloaded;
        var saves = _saves;
        string version = Plugin.Version;
        string folder = SaveFolder; // review F6: read on the game's thread, here
        var legacy = new TaskCompletionSource<bool>();
        _legacyTake = legacy.Task;
        Task.Run(async () =>
        {
            try
            {
                if (!saves.HasLegacy)
                {
                    var m = saves.Take(SaveSnapshots.Legacy, "", version);
                    if (m != null) Log.Info($"Saves: the people you've met so far are kept ({m.Files.Count} files): every save from before this update starts with them.");
                }
            }
            catch (Exception e) { Log.Warn("Saves: " + e.Message); }
            finally { legacy.TrySetResult(true); }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
                if (folder.Length == 0 || !Directory.Exists(folder)) return; // unsure where the saves are: nothing is tidied
                var (slots, blobs) = saves.Collect(slot => File.Exists(Path.Combine(folder, slot + ".sav")));
                if (slots + blobs > 0) Log.Info($"Saves: {slots} snapshots of saves that are gone, and {blobs} files nothing uses, tidied away.");
            }
            catch (Exception e) { Log.Warn("Saves: " + e.Message); }
        });
    }

    /// <summary>The game's save folder (the .sav files), read on the game's thread once.</summary>
    private static string SaveFolder
    {
        get
        {
            if (_saveFolder != null) return _saveFolder;
            try { _saveFolder = Application.persistentDataPath ?? ""; } catch { _saveFolder = ""; }
            return _saveFolder;
        }
    }
    private static string? _saveFolder;

    private static bool PerSave => _cfg.MemoriesPerSave.Value;

    // ------------------------------------------------------------------ the game's own calls (Game/SaveHooks.cs)

    /// <summary>The game saved a slot (and it worked): the working copy is taken as that slot's.</summary>
    internal static void OnGameSaved(string slot, bool autosave)
    {
        if (!Running || _saves == null) return;
        if (_swapping) { _saveAfterSwap = (slot, autosave); Log.Info($"Saves: '{slot}' saved while a save's memory was being put in place; kept once that's done."); return; }
        if (!SaveSnapshots.SafeSlot(slot)) { Log.Warn($"Saves: the game saved '{slot}', which isn't a name a snapshot can have."); return; }
        _activeSlot = slot;
        // What's waiting to be written goes now, so the snapshot has it (the people's files are written at once already).
        try { _pairs?.Flush(); } catch { }
        try { _circles?.Flush(); } catch { }
        try { GameContext.Visits?.Flush(); } catch { }
        try { _story?.Flush(); } catch { }
        FlushMindBooks(); // the inner-life round: minds.json, witness.json
        FlushPhoneBook(); // the phone: phone.json
        var saves = _saves;
        string file = SaveFolder.Length > 0 ? Path.Combine(SaveFolder, slot + ".sav") : "";
        string version = Plugin.Version;
        var before = _lastTake;
        _lastTake = Task.Run(async () =>
        {
            try { await before.ConfigureAwait(false); } catch { }
            var m = saves.Take(slot, SaveSnapshots.FingerprintOf(file), version);
            if (m != null) Log.Info($"Saves: {(autosave ? "autosave" : "save")} '{slot}': {m.Files.Count} files kept with it.");
        });
    }

    /// <summary>The game is loading a slot: decided at the first moment of play (the load itself can take a while).</summary>
    internal static void OnGameLoading(string slot)
    {
        if (!Running) return;
        GameContext.ForgetDayCount(); // FB-78: learned again from this save's own daytime
        _pendingLoad = slot;
        _newGameAsked = false;
        _gameStarting = true;
        _askedDay = GameContext.Day();
        try { _askedPlayer = GameContext.PlayerCharacter()?.Pointer ?? IntPtr.Zero; } catch { _askedPlayer = IntPtr.Zero; }
        _askedAtScene = NpcHold.SceneEpochNow();
        _askedAt = Time.unscaledTime;
        Log.Info($"Saves: loading '{slot}'.");
    }

    /// <summary>
    /// FB-78 (review PH-2): what the day count learned is kept in clock.txt (global), so a session or load at night counts
    /// from midnight at once; the count only uses it when it gives the game's own day or the next.
    /// </summary>
    private static void KeepDayCount(string dataDir)
    {
        string path = Path.Combine(dataDir, "clock.txt");
        try
        {
            var (outcome, text, _) = DataFile.ReadText(path, 1);
            if (outcome == DataFile.Outcome.Read && long.TryParse((text ?? "").Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long kept))
                GameContext.RememberDayCount(kept);
        }
        catch { }
        GameContext.DayLearned = learned =>
        {
            GameContext.RememberDayCount(learned);
            string text = learned.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Task.Run(() => { try { DataFile.WriteAtomic(path, text, durable: false); } catch { } });
        };
    }

    /// <summary>A new game asked for (perhaps cancelled after): decided at the first moment of play, by its first day.</summary>
    internal static void OnNewGameAsked()
    {
        if (!Running) return;
        GameContext.ForgetDayCount(newGame: true); // FB-78: a new game has its own clock (review PH-1, DAY-R1: nothing kept from before)
        _newGameAsked = true;
        _pendingLoad = null; // review F3: a load asked for before and dropped doesn't win over this
        _gameStarting = true;
        _askedDay = GameContext.Day();
        try { _askedPlayer = GameContext.PlayerCharacter()?.Pointer ?? IntPtr.Zero; } catch { _askedPlayer = IntPtr.Zero; }
        _askedAtScene = NpcHold.SceneEpochNow();
        _askedAt = Time.unscaledTime;
    }

    /// <summary>Every frame: once a game has started (the player in the world), its memory is put in place.</summary>
    private static void TickSaves()
    {
        if (!_gameStarting || _swapping || _saves == null) return;
        if (!InGameplay()) return;
        try { if (GameContext.PlayerCharacter() == null) return; } catch { return; }
        // Review F2: a load or new game counts once play is in a new scene (the game loaded it); one that never got there
        // (cancelled, failed) is dropped after a while of play in the same scene.
        // Stage 2 review F6 (re-check V-4): a load that keeps the same scene still makes a new player character; a day rolling
        // over alone isn't a load.
        IntPtr playerNow = IntPtr.Zero;
        try { playerNow = GameContext.PlayerCharacter()?.Pointer ?? IntPtr.Zero; } catch { }
        bool newPlayer = _askedPlayer != IntPtr.Zero && playerNow != IntPtr.Zero && playerNow != _askedPlayer;
        if (_askedAtScene >= 0 && NpcHold.SceneEpochNow() == _askedAtScene && !newPlayer)
        {
            if (Time.unscaledTime - _askedAt < 30f) return;
            Log.Info($"Saves: {(_pendingLoad != null ? $"the load of '{_pendingLoad}'" : "the new game")} didn't happen (still in the same scene); the memory stays as it is.");
            _pendingLoad = null;
            _newGameAsked = false;
            _askedAtScene = -1;
            _gameStarting = false;
            return;
        }
        Log.Info($"Saves (probe): play started after {(_pendingLoad != null ? "a load" : _newGameAsked ? "a new game" : "start-up")} (scene {_askedAtScene} -> {NpcHold.SceneEpochNow()}, day {_askedDay} -> {GameContext.Day()}).");
        _askedAtScene = -1;
        _gameStarting = false;
        string? slot = _pendingLoad;
        bool newGame = _newGameAsked;
        _pendingLoad = null;
        _newGameAsked = false;
        int day = -1;
        try { day = GameContext.Day(); } catch { }
        // Neither a load nor a new game (the mod started with the game; a new game asked for and cancelled): as it is.
        // A new game is on its first day: a cancelled one in a game under way never empties the city.
        if (slot == null && !(newGame && day is >= 0 and <= 1)) // review F3: a day that can't be read isn't a first day
        {
            if (newGame) Log.Info($"Saves: a new game was asked for but play went on (day {day}); the memory stays as it is.");
            return;
        }
        if (!PerSave)
        {
            _activeSlot = slot ?? "";
            Log.Info($"Saves: {(slot != null ? $"'{slot}' loaded" : "a new game")}; MemoriesPerSave is off, so every save shares one memory.");
            return;
        }
        Swap(slot); // the same slot again too: what was said since its save is gone (files that match are left alone)
    }

    /// <summary>
    /// Puts the memory of <paramref name="slot"/> in place (null: a new game, nobody knows you). The /resetall path's
    /// steps: talk ends, late writes are refused, work settles, the books close, the files are put back, the books open.
    /// </summary>
    private static void Swap(string? slot)
    {
        if (_swapping || _saves == null) return;
        _swapping = true;
        var saves = _saves;
        string file = slot != null && SaveFolder.Length > 0 ? Path.Combine(SaveFolder, slot + ".sav") : "";
        // On the game's thread, at once: nothing more is said, heard or remembered from the other save.
        EndAllChatter(ResetReason);
        EndAllFollowers("another save was loaded");
        EndJealousWalk();
        LeaveHome("another save was loaded");
        EndAllTagalongs();
        _date = null;
        _stay = null; // FB-85
        HeardLines.Clear(); // CF-24: another save's people
        _romanceConfirm = null;
        if (_session != null)
        {
            _session.Ended = true;
            _session.Owner?.Release(_session, ReleaseMode.Discard);
        }
        try { _cts?.Cancel(); } catch { }
        try { if (ChatOpen) CloseChat(null); } catch (Exception e) { Log.Warn("Saves: closing the chat: " + e.Message); }
        try { ClosePhone(); } catch (Exception e) { Log.Warn("Saves: putting the phone away: " + e.Message); } // the full audit (B-1)
        try { CloseWorldLine(); EndWorldMoment("another save", quiet: true); } catch (Exception e) { Log.Warn("Saves: world chat: " + e.Message); } // review WC-3
        _openSerial++;
        _busy = false;
        _resetting = true;
        _resetGeneration++;
        _brain.NewEpoch();
        _mockBrain?.NewEpoch(); // review F15
        Greetings.ForgetAll();
        var pending = _inflight;
        var brain = _brain;
        var take = _lastTake;
        var legacyTake = _legacyTake;
        string version = Plugin.Version;
        Task.Run(async () =>
        {
            try { await _minorScan.ConfigureAwait(false); } catch { }
            try { await legacyTake.ConfigureAwait(false); } catch { } // review F15: the legacy snapshot is of the copy as it was
            try { await take.ConfigureAwait(false); } catch { }
            if (pending != null) { try { await pending.ConfigureAwait(false); } catch { } }
            try { await Task.WhenAny(brain.SettledAll(), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false); } catch { }
            var closed = new TaskCompletionSource<bool>();
            MainThread.Enqueue(() => { try { CloseSaveData(); } finally { closed.TrySetResult(true); } });
            await closed.Task.ConfigureAwait(false);
            string what;
            bool ok;
            bool kept = false;
            try
            {
                // Review F1: the memory as it is now is kept first; if that can't be done (twice), nothing is put back.
                string name = SaveSnapshots.BeforeLoadPrefix + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
                kept = saves.Take(name, "", version) != null;
                if (!kept) { await Task.Delay(500).ConfigureAwait(false); kept = saves.Take(name, "", version) != null; }
                if (!kept)
                {
                    ok = false;
                    what = "the memory as it is couldn't be kept first, so nothing was put back (this save's people are as they were)";
                }
                else
                {
                    var (from, why) = slot != null ? saves.SourceFor(slot, SaveSnapshots.FingerprintOf(file)) : (null, "a new game");
                    ok = saves.Restore(from);
                    what = slot != null ? $"'{slot}' loaded: the city as it was when it was saved ({why})" : "a new game: nobody in Nivalis knows you yet";
                }
            }
            catch (Exception e) { ok = false; what = "putting the save's memory back failed: " + e.Message; }
            var opened = new TaskCompletionSource<bool>();
            MainThread.Enqueue(() =>
            {
                try { OpenSaveData(_dataDir); }
                catch (Exception e) { Log.Warn("Saves: opening the memory again: " + e.Message); }
                KnownNames.Clear();
                Greetings.ForgetAll();
                ForgetDoingsNoted();
                ForgetMinorChecks(); // review F4: who's under 18 is checked again against this save's people
                _activeSlot = slot ?? "";
                if (ok) Log.Info("Saves: " + what + ".");
                else Log.Warn("Saves: " + what + ". The log says what, and the memory as it was is kept in BepInEx\\config\\NivalisAIChat\\saves.");
                opened.TrySetResult(true);
            });
            await opened.Task.ConfigureAwait(false);
            // Review F5: nothing is said until this save's people have been checked for anyone under 18.
            try { await _minorScan.ConfigureAwait(false); } catch { }
            MainThread.Enqueue(() =>
            {
                _resetting = false;
                _swapping = false;
                if (_saveAfterSwap is { } late) { _saveAfterSwap = null; OnGameSaved(late.slot, late.autosave); }
            });
        });
    }

    /// <summary>/saves: the slot being played and what's kept per save.</summary>
    private static void SavesCommand(Action<string> say)
    {
        if (_saves == null) { say("Per-save memory isn't available this session (the log says why)."); return; }
        say($"Memories per save: {(PerSave ? "on" : "off (MemoriesPerSave)")}. Playing: {(_activeSlot.Length > 0 ? _activeSlot : "not known yet (no load or save since the game started)")}.");
        var all = _saves.All().OrderByDescending(m => m.Utc).ToList();
        if (all.Count == 0) { say("Nothing kept yet: the first save keeps everyone you've met with it."); return; }
        foreach (var m in all.Take(12)) say("  " + SaveSnapshots.Describe(m));
        if (all.Count > 12) say($"  ...and {all.Count - 12} more.");
    }
}
