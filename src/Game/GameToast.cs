using System;
using System.Collections.Generic;

namespace NivalisAIChat.Game;

/// <summary>
/// FB-65: the game's own pop-up (the one that tells you a worker wasn't paid), with the mod's text in it:
/// <c>NotificationManager.CreateMessage(header, info, context)</c>, read through the singleton's instance (never its
/// Instance property). The first time, the game's own message templates are logged once (they say which one is the
/// unpaid-worker pop-up, for a later match). If the game's pop-up can't be used, false: the caller shows the mod's own.
/// </summary>
internal static class GameToast
{
    private static bool _broken, _probed, _saidUsed;

    public static bool Show(string header, string info)
    {
        if (_broken) return false;
        try
        {
            var manager = GameContext.Existing<Nivalis.NotificationManager>();
            if (manager == null) return false;
            if (!_probed) { _probed = true; Probe(manager); }
            manager.CreateMessage(header, info, null!);
            if (!_saidUsed) { _saidUsed = true; Log.Info("Reminders: shown with the game's own pop-up."); }
            return true;
        }
        catch (Exception e)
        {
            _broken = true;
            Log.Info($"Reminders: the game's pop-up can't be used ({e.GetType().Name}: {e.Message}); the mod's own is shown instead.");
            return false;
        }
    }

    /// <summary>One look at the game's message templates (ids, headers), for the log: which is the unpaid-worker one.</summary>
    private static void Probe(Nivalis.NotificationManager manager)
    {
        try
        {
            var list = manager.messages;
            if (list == null) { Log.Info("Toasts (probe): the game keeps no message templates."); return; }
            var seen = new List<string>();
            for (int i = 0; i < list.Count && seen.Count < 40; i++)
            {
                var m = list[i];
                if (m == null) continue;
                string id = "", head = "";
                try { id = m.notificationId ?? ""; } catch { }
                try { head = m.header ?? ""; } catch { }
                seen.Add(head.Length > 0 ? $"{id} ({Cut(head)})" : id);
            }
            Log.Info($"Toasts (probe): {list.Count} game message templates: {string.Join(", ", seen)}");
        }
        catch (Exception e) { Log.Info("Toasts (probe): " + e.Message); }
    }

    private static string Cut(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
