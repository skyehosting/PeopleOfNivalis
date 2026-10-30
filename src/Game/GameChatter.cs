using System.Collections.Generic;
using HarmonyLib;
using Nivalis;
using UnityEngine;

namespace NivalisAIChat.Game;

/// <summary>
/// The game has its own background chatter: it puts people together at talk spots (CharacterGroup) and plays short
/// written exchanges from its script in plain text plates, without voices. While this mod's chatter is on (and
/// ReplaceGameChatter), the game still gathers people (that's how a city fills its corners), but its lines are silenced and
/// this mod's conversations go to those groups first. CF-26: never a group with someone at work or on a visit in it (a
/// waiter, a customer at a table): its lines play as the game wrote them, and this mod leaves it be.
/// </summary>
internal static class GameChatter
{
    /// <summary>Set every frame by the chatter director.</summary>
    internal static bool Muted;
    private static bool _said;
    private static int _letSaid;
    /// <summary>Each group's answer for a few seconds, while its size stays the same (Talk comes once a line, for every group in town).</summary>
    private static readonly Dictionary<System.IntPtr, (float at, int count, string who)> Checked = new();
    private static float _pruneAt;

    /// <summary>
    /// CF-26: the first member of the group the game has in its own service (at work, or on a visit to a place), as "name
    /// (why)"; "" when there's none. A group that can't be read counts as one (the game's way stands).
    /// </summary>
    internal static string ServiceMember(CharacterGroup g)
    {
        try
        {
            var list = g.Characters;
            if (list == null) return "";
            for (int i = 0; i < list.Count; i++)
            {
                // Review CF26-1: the game's own checked read (a slot let go back to the pool is no one now).
                if (!list[i].TryGetExistingValue(out Character c) || c == null) continue;
                string why = GameContext.ServiceWhy(c);
                if (why.Length > 0) return $"{c.name} ({why})";
            }
            return "";
        }
        catch { return "a member who can't be read"; }
    }

    /// <summary><see cref="ServiceMember"/>, kept a few seconds per group.</summary>
    internal static string ServiceMemberCached(CharacterGroup g)
    {
        float now = Time.unscaledTime;
        int count = -1;
        try { count = g.Characters?.Count ?? 0; } catch { }
        // Review CF26-2: someone joining or leaving reads it again at once.
        if (Checked.TryGetValue(g.Pointer, out var seen) && now - seen.at < 3f && seen.count == count) return seen.who;
        if (now > _pruneAt)
        {
            _pruneAt = now + 60f;
            var old = new List<System.IntPtr>();
            foreach (var kv in Checked) if (now - kv.Value.at > 30f) old.Add(kv.Key);
            foreach (var k in old) Checked.Remove(k);
        }
        string who = ServiceMember(g);
        Checked[g.Pointer] = (now, count, who);
        return who;
    }

    [HarmonyPatch(typeof(CharacterGroup), nameof(CharacterGroup.Talk))]
    private static class MuteGroupTalk
    {
        private static bool Prefix(CharacterGroup __instance)
        {
            if (!Muted) { if (Checked.Count > 0) Checked.Clear(); return true; }
            string who;
            try { who = ServiceMemberCached(__instance); } catch { return true; }
            if (who.Length > 0)
            {
                // CF-26 (a player: waiters stopped taking orders with ReplaceGameChatter on): their visit or work waits on it.
                if (_letSaid < 3) { _letSaid++; Log.Info($"The game's own lines play for a group with {who} in it: their visit or work goes on as the game has it (said 3 times)."); }
                return true;
            }
            if (!_said)
            {
                _said = true;
                Log.Info("The game's own background lines are silenced while this mod's chatter is on; its groups get this mod's conversations (ReplaceGameChatter). Never a group with someone at work or on a visit in it.");
            }
            return false;
        }
    }
}
