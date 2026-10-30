using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NivalisAIChat;

/// <summary>
/// Which old defaults have already been moved to their new value, kept in the data folder (settings-upgrades.txt):
/// each upgrade happens once ever. A setting the player sets back to an old default afterwards stays as they chose
/// (before, every launch moved it again). Unity-free (tested).
/// </summary>
public sealed class SettingsUpgrades
{
    public const string FileName = "settings-upgrades.txt";

    private readonly string? _path;
    private readonly HashSet<string> _done;
    private bool _changed;
    // The record is there but couldn't be read: every upgrade counts as already done (a value the player chose is never
    // moved again), and the record isn't written over (ROBUST-4).
    private readonly bool _unreadable;

    /// <param name="dataDir">The mod's data folder; null keeps the record in memory only (tests).</param>
    public SettingsUpgrades(string? dataDir, Action<string>? warn = null)
    {
        _path = dataDir == null ? null : Path.Combine(dataDir, FileName);
        _done = Load(_path, out _unreadable);
        if (_unreadable) warn?.Invoke($"{FileName} couldn't be read, so no setting moves to a new default this session (each is checked again next start).");
    }

    /// <summary>
    /// Decides one upgrade and remembers that it's been decided, whichever way it went. True means the setting still
    /// holds the old default and moves to the new one now.
    /// </summary>
    public bool Decide(string section, string key, string oldDefault, string? onDisk)
    {
        if (_unreadable) return false; // can't tell what was decided before: nothing moves
        string mark = Mark(section, key, oldDefault);
        bool due = Due(mark, onDisk, oldDefault, _done);
        if (_done.Add(mark)) _changed = true;
        return due;
    }

    public static string Mark(string section, string key, string oldDefault) => $"{section}.{key}={oldDefault}";

    /// <summary>
    /// A one-time move (an older layout's values carried into the new one): true only the first time, and remembered, so
    /// what the player later takes out of the new layout isn't carried in again from the old one (review of a0fa75f).
    /// An unreadable record counts it as done: nothing old comes back.
    /// </summary>
    /// <summary>Whether a one-time step was already done (an unreadable record counts everything as done).</summary>
    public bool Done(string mark) => _unreadable || _done.Contains(mark);

    public bool Once(string mark)
    {
        if (_unreadable || _done.Contains(mark)) return false;
        _done.Add(mark);
        _changed = true;
        return true;
    }

    /// <summary>
    /// Whether a setting moves to its new default: only the first time this upgrade is seen, and only if the file
    /// still holds the old default (a value the player chose, or none at all, is left alone).
    /// </summary>
    public static bool Due(string mark, string? onDisk, string oldDefault, HashSet<string> done) =>
        !done.Contains(mark) && onDisk != null && string.Equals(onDisk.Trim(), oldDefault.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes the record if anything new was decided (a failure only means the same decisions are made next time).</summary>
    public void Save()
    {
        if (!_changed || _path == null || _unreadable) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var lines = new List<string>
            {
                "# People of Nivalis: settings already moved from an old default to a new one (each happens once).",
                "# Delete a line and that setting is checked again at the next start.",
            };
            lines.AddRange(_done.OrderBy(x => x, StringComparer.Ordinal));
            Npc.DataFile.WriteAtomic(_path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            _changed = false;
        }
        catch { /* not fatal: the same decisions are simply made again next start */ }
    }

    private static HashSet<string> Load(string? path, out bool unreadable)
    {
        unreadable = false;
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (path == null) return done;
        var (outcome, text, _) = Npc.DataFile.ReadText(path); // a few tries: another program may hold it for a moment
        if (outcome == Npc.DataFile.Outcome.Unreadable) { unreadable = true; return done; }
        if (outcome == Npc.DataFile.Outcome.Missing) return done;
        foreach (var line in text!.Split('\n'))
        {
            string t = line.Trim();
            if (t.Length > 0 && !t.StartsWith("#")) done.Add(t);
        }
        return done;
    }
}
