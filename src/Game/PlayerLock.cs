using System;
using System.Collections.Generic;
using Nivalis;

namespace NivalisAIChat.Game;

/// <summary>
/// Holds the player still while a chat is open, using the game's own override locks so nothing is
/// left in a broken state: movement, camera, interactions, menu shortcuts, saving, the clock, the cursor.
/// </summary>
internal sealed class PlayerLock
{
    private readonly List<OverrideableBool.OverrideLock> _locks = new();
    private readonly Il2CppSystem.Object _owner = new();
    private bool _cursor;

    public bool Engaged { get; private set; }

    public void Engage(bool pauseClock)
    {
        if (Engaged) return;
        Engaged = true;
        var pc = GameContext.PlayerCharacter();
        Try("movement", () => _moveLock = pc?.Controller?.DisableMovement(_owner));
        Try("camera", () => _cameraLock = pc?.Controller?.LockCamera(_owner));
        Try("interaction", () => pc?.Interaction?.DisableInteractions(_owner));
        var input = SafeInput();
        Try("menus", () => input?.PreventAllMenuShortcuts?.Override(_owner));
        Try("saving", () => input?.PreventSaving?.Override(_owner));
        if (pauseClock) Try("clock", () => TimeOfDayManager.Pause(_owner));
        try { HoldingCursor = true; CursorModeManager.RequestUiCursor(); _cursor = true; }
        catch (Exception e) { Log.Debug("cursor: " + e.Message); }
    }

    /// <summary>True while a chat holds the UI cursor; the game's own releases are ignored meanwhile.</summary>
    internal static bool HoldingCursor;
    private static bool _ownRelease;

    private OverrideableBool.OverrideLock? _cameraLock, _moveLock;

    /// <summary>FB-80: the phone's Camera is open: you look around and walk (FB-82), the cursor let go.</summary>
    public bool Looking { get; private set; }

    /// <summary>
    /// FB-80: into the Camera (look around with the mouse or the stick, and walk (FB-82, the author: "make it so movement keys
    /// still work when I have the camera out"): the camera and movement locks and the UI cursor let go; interactions, menus and
    /// saving stay held) or back out of it (all three taken again).
    /// </summary>
    public void LookMode(bool on)
    {
        if (!Engaged || on == Looking) return;
        Looking = on;
        var pc = GameContext.PlayerCharacter();
        if (on)
        {
            foreach (var l in new[] { _cameraLock, _moveLock })
            {
                try { if (l != null && !l.IsReleased) l.Release(); } catch (Exception e) { Log.Debug("camera unlock: " + e.Message); }
                if (l != null) _locks.Remove(l);
            }
            _cameraLock = _moveLock = null;
            if (_cursor)
            {
                _ownRelease = true;
                try { CursorModeManager.ReleaseUiCursor(); } catch (Exception e) { Log.Debug("cursor release: " + e.Message); }
                finally { _ownRelease = false; }
                _cursor = false;
            }
        }
        else
        {
            Try("camera", () => _cameraLock = pc?.Controller?.LockCamera(_owner));
            Try("movement", () => _moveLock = pc?.Controller?.DisableMovement(_owner));
            try { CursorModeManager.RequestUiCursor(); _cursor = true; } catch (Exception e) { Log.Debug("cursor: " + e.Message); }
        }
    }

    /// <summary>Safety net: if the cursor was dropped anyway, ask once more (not every frame — re-requesting resets input).</summary>
    public void KeepCursor()
    {
        if (!Engaged || Looking) return;
        try { if (!CursorModeManager.IsCursorActive) { CursorModeManager.RequestUiCursor(); _cursor = true; } }
        catch { }
    }

    /// <summary>
    /// The game has a single UI-cursor flag. UIManager clears it whenever the input device changes (moving the
    /// mouse after typing) or its own panels change, because it doesn't know about our window. While a chat is
    /// open, those releases are ignored.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(CursorModeManager), nameof(CursorModeManager.ReleaseUiCursor))]
    private static class KeepUiCursor
    {
        private static bool Prefix() => !HoldingCursor || _ownRelease;
    }

    /// <summary>While an AI chat is open, the game's own talk key (E) must not open the NPC's written dialogue.</summary>
    [HarmonyLib.HarmonyPatch(typeof(Nivalis.Dialogue.StartDialogueInteraction), nameof(Nivalis.Dialogue.StartDialogueInteraction.Nivalis_IInteractable_DoInteraction))]
    private static class NoNativeInteraction
    {
        // PadTalkButton = Interact: that press on the person the AI chat opens with doesn't also open their dialogue (CF-07).
        private static bool Prefix(Nivalis.Dialogue.StartDialogueInteraction __instance) => !HoldingCursor && !ModRuntime.PadTalkTakesInteract(__instance);
    }

    /// <summary>
    /// FB-74: right-click is the game's zoom, and LockCamera doesn't stop it, so a right-click on the phone (Delete
    /// message) or in a chat zoomed the view behind it. While one of this mod's windows holds you, the camera doesn't
    /// zoom; it zooms again as soon as the window closes. (Checked against shared native bodies at start, as every hook.)
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(Nivalis.PlayerCameraController), nameof(Nivalis.PlayerCameraController.UpdateCameraZooming))]
    private static class NoZoomWhileHeld { private static bool Prefix() => !HoldingCursor; }

    // CF-12: PadTalkButton on X, Y or B: the press that opens the AI chat with the person under the crosshair isn't also
    // the game's placement mode, pick up or put away (ModRuntime.PadTalkTakes; anywhere else the game acts as usual).
    // Review XR-4: on the input callback itself (the no-argument OnPlacementMode it calls may be compiled into it).
    [HarmonyLib.HarmonyPatch(typeof(Nivalis.UI.PlacementModeUi), nameof(Nivalis.UI.PlacementModeUi.OnPlacementMode_Context))]
    private static class PadTalkNotPlacement { private static bool Prefix() => !ModRuntime.PadTalkTakes("X"); }

    [HarmonyLib.HarmonyPatch(typeof(Nivalis.PlayerObjectHolder), nameof(Nivalis.PlayerObjectHolder.TryPickupObjectInFront))]
    private static class PadTalkNotPickUp { private static bool Prefix() => !ModRuntime.PadTalkTakes("Y"); }

    [HarmonyLib.HarmonyPatch(typeof(Nivalis.PlayerObjectHolder), nameof(Nivalis.PlayerObjectHolder.StoreEntity))]
    private static class PadTalkNotStore
    {
        private static bool Prefix(ref bool __result)
        {
            if (!ModRuntime.PadTalkTakes("B")) return true;
            __result = false;
            return false;
        }
    }

    // The game's own shortcuts (C scanner, J journal, M map, I inventory, L shopping list...) must not fire while typing.
    // TabRightListener is deliberately absent: its native body is shared with three other methods.
    [HarmonyLib.HarmonyPatch(typeof(PlayerInputManager), nameof(PlayerInputManager.ScanListener))]
    private static class NoScan { private static bool Prefix() => !HoldingCursor; }

    [HarmonyLib.HarmonyPatch(typeof(PlayerInputManager), nameof(PlayerInputManager.JournalShortcutListener))]
    private static class NoJournal { private static bool Prefix() => !HoldingCursor; }

    [HarmonyLib.HarmonyPatch(typeof(PlayerInputManager), nameof(PlayerInputManager.MapToggleListener))]
    private static class NoMap { private static bool Prefix() => !HoldingCursor; }

    [HarmonyLib.HarmonyPatch(typeof(PlayerInputManager), nameof(PlayerInputManager.InventoryShortcutListener))]
    private static class NoInventory { private static bool Prefix() => !HoldingCursor; }

    [HarmonyLib.HarmonyPatch(typeof(PlayerInputManager), nameof(PlayerInputManager.ShoppingListShortcutListener))]
    private static class NoShoppingList { private static bool Prefix() => !HoldingCursor; }

    [HarmonyLib.HarmonyPatch(typeof(PlayerInputManager), nameof(PlayerInputManager.ShowMoreShortcutListener))]
    private static class NoShowMore { private static bool Prefix() => !HoldingCursor; }

    [HarmonyLib.HarmonyPatch(typeof(PlayerInputManager), nameof(PlayerInputManager.TabLeftListener))]
    private static class NoTabLeft { private static bool Prefix() => !HoldingCursor; }

    [HarmonyLib.HarmonyPatch(typeof(Nivalis.Dialogue.StartDialogueInteraction), nameof(Nivalis.Dialogue.StartDialogueInteraction.StartDialogue))]
    private static class NoNativeDialogue
    {
        private static bool Prefix(Nivalis.Dialogue.StartDialogueInteraction __instance) => !HoldingCursor && !ModRuntime.PadTalkTakesInteract(__instance);
    }

    public void Release()
    {
        if (!Engaged) return;
        Engaged = false;
        Looking = false;
        _cameraLock = _moveLock = null;
        foreach (var l in _locks)
        {
            try { if (l != null && !l.IsReleased) l.Release(); }
            catch (Exception e) { Log.Debug("release lock: " + e.Message); }
        }
        _locks.Clear();
        HoldingCursor = false;
        if (_cursor)
        {
            _ownRelease = true;
            try { CursorModeManager.ReleaseUiCursor(); } catch (Exception e) { Log.Debug("cursor release: " + e.Message); }
            finally { _ownRelease = false; }
            _cursor = false;
        }
    }

    private void Try(string what, Func<OverrideableBool.OverrideLock?> take)
    {
        try
        {
            var l = take();
            if (l != null) _locks.Add(l);
        }
        catch (Exception e) { Log.Debug($"lock {what}: {e.Message}"); }
    }

    private static PlayerInputManager? SafeInput()
    {
        return GameContext.Existing<PlayerInputManager>();
    }
}
