using HarmonyLib;

namespace NivalisAIChat.Game;

/// <summary>
/// FB-63: the player going in or out of an apartment (the game's own listener for it, on PlayerManager.Player), followed so a
/// partner who lives with you can be home. An observer only: the game's call runs exactly as it would.
/// </summary>
internal static class HomeHooks
{
    [HarmonyPatch(typeof(Nivalis.PlayerManager.Player), nameof(Nivalis.PlayerManager.Player.PlayerEntersApartmentListener))]
    private static class EnterExit
    {
        private static void Postfix(Nivalis.Apartment.ApartmentController obj, bool enter)
        {
            try { ModRuntime.OnApartment(obj, enter); }
            catch (System.Exception e) { Log.Debug("home: " + e.Message); }
        }
    }
}
