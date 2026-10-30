using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NivalisAIChat.Ai;
using NivalisAIChat.UI;

namespace NivalisAIChat;

/// <summary>
/// CF-20 (a player: "please ad AI profiles so I can easily switch providers when my usage changes"): your AI connection saved
/// under a name (AiProfiles, ai_profiles.json in the mod's folder), switched to from the AI Connection tab or /profile.
/// Switching writes those settings as if you'd set each one; no key is ever shown, only whether one is set.
/// </summary>
public static partial class ModRuntime
{
    private static AiProfiles? _aiProfiles;
    private static AiProfiles AiProfilesBook => _aiProfiles ??= new AiProfiles(Path.Combine(_dataDir, "ai_profiles.json"), Log.Warn);

    /// <summary>The AI connection as it's set now, under this name.</summary>
    private static AiProfiles.Profile CurrentProfile(string name) => new()
    {
        Name = name,
        Format = _cfg.Format.Value.ToString(),
        ApiUrl = _cfg.ApiUrl.Value ?? "", ApiKey = _cfg.ApiKey.Value ?? "", Model = _cfg.Model.Value ?? "",
        BackgroundModel = _cfg.BackgroundModel.Value ?? "", BackgroundApiUrl = _cfg.BackgroundApiUrl.Value ?? "",
        BackgroundApiKey = _cfg.BackgroundApiKey.Value ?? "", BackgroundApiFormat = _cfg.BackgroundApiFormat.Value ?? "",
    };

    /// <summary>A profile's settings put in place (each saved as a change of its own). Returns why not, or null.</summary>
    private static string? ApplyProfile(string name)
    {
        var p = AiProfilesBook.Get(name);
        if (p == null) return $"There's no profile called {AiProfiles.CleanName(name)}.";
        if (Enum.TryParse<ApiFormat>(p.Format, true, out var format)) _cfg.Format.Value = format;
        _cfg.ApiUrl.Value = p.ApiUrl;
        _cfg.ApiKey.Value = p.ApiKey;
        _cfg.Model.Value = p.Model;
        _cfg.BackgroundModel.Value = p.BackgroundModel;
        _cfg.BackgroundApiUrl.Value = p.BackgroundApiUrl;
        _cfg.BackgroundApiKey.Value = p.BackgroundApiKey;
        if (p.BackgroundApiFormat.Length > 0) _cfg.BackgroundApiFormat.Value = p.BackgroundApiFormat;
        foreach (var k in new[] { "ApiFormat", "ApiUrl", "ApiKey", "Model", "BackgroundModel", "BackgroundApiUrl", "BackgroundApiKey", "BackgroundApiFormat" })
            AfterMenuChange(k);
        AiProfilesBook.SetActive(p.Name);
        InvalidateOptimizer();
        Log.Info($"AI profile: switched to {p}."); // Profile.ToString never carries a key
        return null;
    }

    /// <summary>The AI Connection tab's profile rows: switch to one, save the connection as one, remove one.</summary>
    private static List<object> ProfileRows()
    {
        var rows = new List<object>
        {
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "subsection", ["title"] = "Profiles", ["before"] = "TokenLimit" },
        };
        var names = AiProfilesBook.Names();
        if (names.Count > 0)
        {
            var use = SettingsCatalog.ToContract(new SettingControl(ControlKind.Choice, Choices: names));
            use["before"] = "TokenLimit";
            use["title"] = "Switch to a profile";
            use["hint"] = AiProfilesBook.Active.Length > 0 ? $"In use: {AiProfilesBook.Active}" : "Choose one: its settings go in";
            use["help"] = "Puts a saved profile's AI connection in place: its format, address, key and model, and its background model's. " +
                          "Everything else stays as it is.";
            use["type"] = "string";
            use["pickOnly"] = true;
            use["rebuild"] = true;
            use["get"] = (Func<object?>)(() => "choose one");
            use["set"] = (Func<object, string?>)(v => v is string n && n.Length > 0 ? ApplyProfile(n) : null);
            rows.Add(use);
        }
        var save = SettingsCatalog.ToContract(new SettingControl(ControlKind.Text));
        save["before"] = "TokenLimit";
        save["title"] = "Save as a profile";
        save["hint"] = "A name for the connection above (the same name replaces it)";
        save["help"] = "Saves the AI connection as it's set above under this name, to switch back to later. It's kept on this PC with your " +
                       "key (ai_profiles.json in the mod's folder), never shown.";
        save["type"] = "string";
        save["rebuild"] = true;
        save["get"] = (Func<object?>)(() => "");
        save["set"] = (Func<object, string?>)(v =>
        {
            string n = AiProfiles.CleanName(v as string);
            if (n.Length == 0) return null;
            var why = AiProfilesBook.Save(CurrentProfile(n));
            if (why == null) Log.Info($"AI profile: saved {CurrentProfile(n)}.");
            return why;
        });
        rows.Add(save);
        if (names.Count > 0)
        {
            var remove = SettingsCatalog.ToContract(new SettingControl(ControlKind.Choice, Choices: names));
            remove["before"] = "TokenLimit";
            remove["title"] = "Remove a profile";
            remove["hint"] = "Choose one: it's taken out (your settings stay)";
            remove["help"] = "Takes a saved profile out of the list. The AI connection you're using now stays as it is.";
            remove["type"] = "string";
            remove["pickOnly"] = true;
            remove["rebuild"] = true;
            remove["get"] = (Func<object?>)(() => "choose one");
            remove["set"] = (Func<object, string?>)(v => v is string n && AiProfilesBook.Remove(n) ? null : "That profile isn't there any more.");
            rows.Add(remove);
        }
        return rows;
    }

    /// <summary>/profile: list, use NAME, save NAME, delete NAME.</summary>
    private static void ProfileCommand(string text, Action<string> say)
    {
        var parts = text.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
        string name = parts.Length > 2 ? parts[2] : "";
        var book = AiProfilesBook;
        switch (sub)
        {
            case "save":
                if (AiProfiles.CleanName(name).Length == 0) { say("/profile save NAME: a name for the AI connection you're using now."); return; }
                say(book.Save(CurrentProfile(name)) ?? $"Saved your AI connection as {AiProfiles.CleanName(name)} (with its key, kept on this PC; never shown).");
                return;
            case "use":
                say(ApplyProfile(name) ?? $"Switched to {AiProfiles.CleanName(name)}: {_cfg.Model.Value} at {HostOf(_cfg.ApiUrl.Value)}.");
                return;
            case "delete" or "remove":
                say(book.Remove(name) ? $"Removed the profile {AiProfiles.CleanName(name)} (your settings stay as they are)." : "There's no profile by that name.");
                return;
            default:
                var names = book.Names();
                say(names.Count == 0
                    ? "No AI profiles yet. /profile save NAME saves the AI connection you're using now; the AI Connection tab in the settings has them too."
                    : $"AI profiles: {string.Join(", ", names.Select(n => n == book.Active ? n + " (in use)" : n))}. /profile use NAME, /profile save NAME, /profile delete NAME.");
                return;
        }
    }

    private static string HostOf(string? url) => Uri.TryCreate(url ?? "", UriKind.Absolute, out var u) ? u.Host : "";
}
