using System;
using Nivalis;
using Nivalis.GhostSystem;
using Nivalis.GhostSystem.Ai;

namespace NivalisAIChat.Game;

/// <summary>
/// CF-19: the game's hooks for names you don't know yet (the rule and the bookkeeping are in ModRuntime.Names.cs). Each is
/// on its own native body (checked in the game, 2026-10-19), and each is display only.
/// </summary>
internal static class NameHooks
{
    [HarmonyLib.HarmonyPatch(typeof(PlayerCustomerARUi), nameof(PlayerCustomerARUi.SetCustomerName))]
    private static class BlankCustomerOverlay
    {
        private static void Prefix(PlayerCustomerARUi __instance, ref string __0)
        {
            try { if (__instance != null && !string.IsNullOrEmpty(__0) && ModRuntime.BlankOverlayName(__instance, __0)) __0 = ""; }
            catch (Exception e) { Log.Debug("customer name: " + e.Message); }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(CustomerPopup), nameof(CustomerPopup.Show))]
    private static class BlankCustomerPopup
    {
        private static void Postfix(CustomerPopup __instance, AgentGhost __0)
        {
            try { if (__instance != null && __0 != null && __instance.customerName != null && ModRuntime.PopupNameHidden(__0)) __instance.customerName.text = ""; }
            catch (Exception e) { Log.Debug("customer pop-up name: " + e.Message); }
        }
    }
}
