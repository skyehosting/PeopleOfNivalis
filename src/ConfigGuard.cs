using BepInEx.Configuration;
using HarmonyLib;

namespace NivalisAIChat;

/// <summary>
/// The tag other settings menus read (BepInEx's ConfigurationManager and the menus that follow it, like Mod Settings Menu):
/// found by its name and its fields' names, so both are kept as they are in the public build (release/obfuscar.xml).
/// </summary>
internal sealed class ConfigurationManagerAttributes
{
    /// <summary>False: not listed in other settings menus (Switchboard still lists it: People of Nivalis's page asks it to).</summary>
    public bool? Browsable;
    /// <summary>True: shown, not editable.</summary>
    public bool? ReadOnly;
    /// <summary>Higher is listed first.</summary>
    public int? Order;
}

/// <summary>
/// Review of the Nexus reports (2026-10-17): another settings menu's Apply ends in BepInEx's <c>ConfigFile.Save()</c>, which
/// rewrites PeopleOfNivalis.cfg in BepInEx's own layout: the voice Providers block (its lines aren't BepInEx settings), the
/// mod's explanations and the player's notes were lost (830 lines to 365 in a test; a player's "Providers list has no
/// closing ']'"). People of Nivalis writes its own file on every change (<see cref="ModConfig"/>), so a save of that one file
/// from anywhere else is let go. Every other mod's file saves as before. BepInEx's ConfigFile is plain .NET, not the game's
/// native code: no shared-body risk.
/// </summary>
internal static class ConfigGuard
{
    [HarmonyPatch(typeof(ConfigFile), nameof(ConfigFile.Save))]
    private static class OwnFileSavesItself
    {
        private static bool Prefix(ConfigFile __instance)
        {
            if (!ModConfig.Owns(__instance)) return true;
            ModConfig.SaveRefused();
            return false;
        }
    }
}
