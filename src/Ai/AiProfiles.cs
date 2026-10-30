using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Ai;

/// <summary>
/// CF-20 (a player: "please ad AI profiles so I can easily switch providers when my usage changes"): your AI connection
/// (format, address, key, model, and the background model's) saved under a name, to switch back to in one go. Kept on this
/// PC in the mod's folder (ai_profiles.json), for every save; a key in it is never shown, logged or sent anywhere but its
/// own provider, as the settings file's is. Unity-free and tested.
/// </summary>
public sealed class AiProfiles
{
    public sealed class Profile
    {
        public string Name { get; set; } = "";
        public string Format { get; set; } = "";
        public string ApiUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "";
        public string BackgroundModel { get; set; } = "";
        public string BackgroundApiUrl { get; set; } = "";
        public string BackgroundApiKey { get; set; } = "";
        public string BackgroundApiFormat { get; set; } = "";

        public Profile Copy() => (Profile)MemberwiseClone();

        /// <summary>Never the keys: only whether there are any (a profile can be logged or shown safely as this).</summary>
        public override string ToString() => $"{Name}: {Model} at {Host(ApiUrl)}{(ApiKey.Length > 0 ? " (key set)" : "")}";
    }

    public sealed class Data
    {
        public List<Profile> Profiles { get; set; } = new();
        /// <summary>The profile last switched to or saved ("" none).</summary>
        public string Active { get; set; } = "";
    }

    public const int MaxProfiles = 20, MaxName = 32;
    private readonly string _path;
    private readonly Action<string>? _warn;
    private readonly object _lock = new();
    private readonly Data _data;
    private readonly bool _readOnly;

    public AiProfiles(string path, Action<string>? warn = null)
    {
        _path = path;
        _warn = warn;
        _data = DataFile.LoadBook(path, t => JsonSerializer.Deserialize<Data>(t, NpcStore.Json), warn, out _readOnly) ?? new Data();
        _data.Profiles ??= new();
        _data.Profiles.RemoveAll(p => p == null || CleanName(p.Name).Length == 0);
        foreach (var p in _data.Profiles)
        {
            p.Name = CleanName(p.Name);
            p.Format ??= ""; p.ApiUrl ??= ""; p.ApiKey ??= ""; p.Model ??= "";
            p.BackgroundModel ??= ""; p.BackgroundApiUrl ??= ""; p.BackgroundApiKey ??= ""; p.BackgroundApiFormat ??= "";
        }
        _data.Active ??= "";
    }

    /// <summary>A profile's name as it's kept: letters, digits, spaces and - _ . only, at most <see cref="MaxName"/>.</summary>
    public static string CleanName(string? name)
    {
        var s = new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.').ToArray()).Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length > MaxName ? s[..MaxName].Trim() : s;
    }

    public List<string> Names() { lock (_lock) return _data.Profiles.Select(p => p.Name).ToList(); }

    public string Active { get { lock (_lock) return _data.Active; } }

    /// <summary>A copy of the profile by this name (any case), or null.</summary>
    public Profile? Get(string name)
    {
        string n = CleanName(name);
        lock (_lock) return _data.Profiles.FirstOrDefault(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))?.Copy();
    }

    /// <summary>Saves (or replaces, by name) a profile and makes it the active one. Returns why not, or null.</summary>
    public string? Save(Profile profile)
    {
        string n = CleanName(profile.Name);
        if (n.Length == 0) return "Give the profile a name (letters and digits).";
        var p = profile.Copy();
        p.Name = n;
        lock (_lock)
        {
            int at = _data.Profiles.FindIndex(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
            if (at < 0 && _data.Profiles.Count >= MaxProfiles) return $"There are {MaxProfiles} profiles already: remove one first.";
            if (at >= 0) _data.Profiles[at] = p;
            else _data.Profiles.Add(p);
            _data.Active = n;
            return Write();
        }
    }

    /// <summary>Takes a profile out (the settings in use stay as they are). True when there was one.</summary>
    public bool Remove(string name)
    {
        string n = CleanName(name);
        lock (_lock)
        {
            int removed = _data.Profiles.RemoveAll(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            if (string.Equals(_data.Active, n, StringComparison.OrdinalIgnoreCase)) _data.Active = "";
            Write();
            return true;
        }
    }

    /// <summary>The profile switched to (its settings applied by the caller).</summary>
    public void SetActive(string name)
    {
        lock (_lock)
        {
            var p = _data.Profiles.FirstOrDefault(x => string.Equals(x.Name, CleanName(name), StringComparison.OrdinalIgnoreCase));
            _data.Active = p?.Name ?? "";
            Write();
        }
    }

    private string? Write()
    {
        if (_readOnly) return "The profiles file couldn't be read, so it isn't written over (see the log).";
        try
        {
            if (System.IO.Path.GetDirectoryName(_path) is { Length: > 0 } dir) System.IO.Directory.CreateDirectory(dir);
            DataFile.WriteAtomic(_path, JsonSerializer.Serialize(_data, NpcStore.Json));
            return null;
        }
        catch (Exception e) { _warn?.Invoke($"AI profiles couldn't be saved ({e.GetType().Name})."); return "The profiles couldn't be saved (see the log)."; }
    }

    private static string Host(string url) => Uri.TryCreate(url ?? "", UriKind.Absolute, out var u) ? u.Host : "";
}
