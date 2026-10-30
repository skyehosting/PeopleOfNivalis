using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NivalisAIChat.UI;

namespace NivalisAIChat.Tests;

/// <summary>
/// 0.5.2 (Nexus: another settings menu's Apply lost a player's voice Providers): People of Nivalis's settings aren't listed
/// by other menus, its file is saved only by itself, Switchboard lists them all the same (each hint has a kind),
/// and a TalkKey it can't use is said. ModConfig and the hook are BepInEx/Unity code (not built here): their source is read;
/// the save itself was proved against the real BepInEx.
/// </summary>
internal static partial class Program
{
    private static void MenuHideTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- 0.5.2: not in other settings menus, the file saved only by People of Nivalis");
        // Any Switchboard (0.5.4 too) lists a hidden-tagged setting whose hint has a kind: every setting's hint comes from here.
        Check(Enum.GetValues<ControlKind>().All(k => SettingsCatalog.ToContract(new SettingControl(k)).TryGetValue("kind", out var v) && v is string { Length: > 0 }),
            "every setting's hint has a kind, so Switchboard lists it in any version");

        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string cfg = File.ReadAllText(Path.Combine(src, "ModConfig.cs"));
        string guard = File.ReadAllText(Path.Combine(src, "ConfigGuard.cs"));
        string settings = File.ReadAllText(Path.Combine(src, "ModRuntime.Settings.cs"));
        string runtime = File.ReadAllText(Path.Combine(src, "ModRuntime.cs"));
        int binds = cfg.Split("_file.Bind(").Length - 1;
        Check(binds == 1 && cfg.Contains("_file.Bind(section, key, defaultValue, new ConfigDescription(description, null, menus))", StringComparison.Ordinal)
              && cfg.Contains("NotInOtherMenus = new() { Browsable = false }", StringComparison.Ordinal)
              && cfg.Contains("Bind(key, defaultValue, description, NotInOtherMenus, oldKeys)", StringComparison.Ordinal),
            "every setting is bound in one place, tagged Browsable = false unless it says otherwise");
        Check(cfg.Contains("Bind(\"SettingsMenu\"", StringComparison.Ordinal) && cfg.Contains("Browsable = true, ReadOnly = true", StringComparison.Ordinal),
            "one read-only line stays in other menus: where the settings are");
        Check(guard.Contains("[HarmonyPatch(typeof(ConfigFile), nameof(ConfigFile.Save))]", StringComparison.Ordinal)
              && guard.Contains("if (!ModConfig.Owns(__instance)) return true;", StringComparison.Ordinal) && guard.Contains("return false;", StringComparison.Ordinal),
            "BepInEx's Save of People of Nivalis's file is let go; every other mod's file saves as before");
        Check(cfg.Contains("Path.GetFullPath(file.ConfigFilePath), _ownPath, StringComparison.OrdinalIgnoreCase", StringComparison.Ordinal),
            "the own file is known by its path (a hot-reloaded copy's ConfigFile on the same file counts)");
        Check(settings.Contains("[\"listHidden\"] = true", StringComparison.Ordinal) && settings.Contains("ReferenceEquals(e, _cfg.SettingsMenu)) hint[\"kind\"] = \"hidden\"", StringComparison.Ordinal),
            "the page asks Switchboard to list them, and leaves out the 'it's in Switchboard' line");
        Check(runtime.Contains("isn't a keyboard key People of Nivalis can use", StringComparison.Ordinal) && runtime.Contains("PadTalkButton", StringComparison.Ordinal),
            "a TalkKey it can't use (a controller button) is said once, pointing to PadTalkButton");
    }
}
