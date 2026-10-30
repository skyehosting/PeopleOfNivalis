using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.Json;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Voice;

/// <summary>Which voice each person has, for good. One small JSON file (voices.json) next to the NPC memories.</summary>
public sealed class VoiceBook
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, VoiceAssignment> _voices;
    // Goes up when someone's voice is forgotten (/resetnpc; /resetall for everyone): a voice chosen before that isn't saved.
    private readonly Dictionary<string, int> _generations = new();
    private int _allGeneration;
    private readonly Action<string>? _warn;
    // The file couldn't be read at the start (locked, or damaged and couldn't be moved aside): nothing is written over it.
    private bool _readOnly, _warnedReadOnly;

    public VoiceBook(string path, Action<string>? warn = null)
    {
        _path = path;
        _warn = warn;
        _voices = DataFile.LoadBook(_path, t => JsonSerializer.Deserialize<Dictionary<string, VoiceAssignment>>(t, NpcStore.Json), _warn, out _readOnly) ?? new();
    }

    /// <summary>Stop (a hot reload): nothing more is written; a late change is said once (audit step 7b, review of a0fa75f).</summary>
    public void Close()
    {
        lock (_lock) _closed = true;
    }

    private bool _closed, _saidClosed;

    private void Save()
    {
        if (_readOnly) { DataFile.NotSaved(_path, _warn, ref _warnedReadOnly); return; }
        if (_closed) { DataFile.Stopped(Path.GetFileName(_path), _warn, ref _saidClosed); return; }
        DataFile.WriteAtomic(_path, JsonSerializer.Serialize(_voices, NpcStore.Json));
    }

    public VoiceAssignment? Get(string id)
    {
        lock (_lock) return _voices.TryGetValue(id, out var v) ? v : null;
    }

    public void Set(string id, VoiceAssignment voice)
    {
        lock (_lock) { _voices[id] = voice; Save(); }
    }

    /// <summary>Their generation now: taken before choosing a voice for them, and passed to <see cref="Set(string, VoiceAssignment, long)"/>.</summary>
    public long Generation(string id)
    {
        lock (_lock) return ((long)_allGeneration << 32) | (uint)_generations.GetValueOrDefault(id);
    }

    /// <summary>Saves their voice, unless it was forgotten since <paramref name="generation"/> (then it's dropped: false).</summary>
    public bool Set(string id, VoiceAssignment voice, long generation)
    {
        lock (_lock)
        {
            if ((((long)_allGeneration << 32) | (uint)_generations.GetValueOrDefault(id)) != generation) return false;
            _voices[id] = voice;
            Save();
            return true;
        }
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            _generations[id] = _generations.GetValueOrDefault(id) + 1; // a voice being chosen for them right now isn't saved
            if (_voices.Remove(id)) Save();
        }
    }

    /// <summary>Forget every voice; the old file is kept in <paramref name="backupDir"/>.</summary>
    public void Clear(string backupDir)
    {
        lock (_lock)
        {
            if (File.Exists(_path))
            {
                Directory.CreateDirectory(backupDir);
                // Kept in the backup: safe to write over now, even if it couldn't be read at the start.
                try { File.Copy(_path, Path.Combine(backupDir, Path.GetFileName(_path)), true); _readOnly = false; }
                catch when (_readOnly) { } // still locked: forgotten for this session only; the file is left as it is
            }
            _voices = new();
            _allGeneration++;
            Save();
        }
    }

    /// <summary>Everyone's voice (a copy).</summary>
    public List<KeyValuePair<string, VoiceAssignment>> All()
    {
        lock (_lock) return _voices.ToList();
    }

    public int Count
    {
        get { lock (_lock) return _voices.Count; }
    }
}
