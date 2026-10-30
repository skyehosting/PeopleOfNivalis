using HarmonyLib;

namespace NivalisAIChat.Game;

/// <summary>
/// FB-61: the game's own save and load calls, followed so the city's memory belongs to the save being played (the game
/// keeps no "current slot" of its own). Observers only: the game's calls run exactly as they would.
/// </summary>
internal static class SaveHooks
{
    /// <summary>A save that worked (Save returns true): its slot keeps the memory as it is now.</summary>
    [HarmonyPatch(typeof(Nivalis.SerializationManager), nameof(Nivalis.SerializationManager.Save))]
    private static class Saved
    {
        private static void Postfix(string saveName, bool isAutoSave, bool __result)
        {
            try { if (__result) ModRuntime.OnGameSaved(saveName, isAutoSave); }
            catch (System.Exception e) { Log.Warn("Saves: after the game's save: " + e.Message); }
        }
    }

    /// <summary>A slot is being loaded (from the title, Continue, or a load in play).</summary>
    [HarmonyPatch(typeof(Nivalis.SerializationManager), nameof(Nivalis.SerializationManager.Load))]
    private static class Loading
    {
        private static void Prefix(string saveName)
        {
            try { ModRuntime.OnGameLoading(saveName); }
            catch (System.Exception e) { Log.Warn("Saves: before the game's load: " + e.Message); }
        }
    }

    /// <summary>
    /// A new game asked for (it may still be cancelled: its confirm runs elsewhere). Whether it was is decided when play
    /// starts: a new game is on its first day (ModRuntime.TickSaves), so a cancelled one in a game under way changes nothing.
    /// </summary>
    [HarmonyPatch(typeof(Nivalis.MainMenuUI), nameof(Nivalis.MainMenuUI.NewGameRequest))]
    private static class NewGame
    {
        private static void Prefix()
        {
            try { ModRuntime.OnNewGameAsked(); }
            catch (System.Exception e) { Log.Warn("Saves: a new game asked for: " + e.Message); }
        }
    }
}
