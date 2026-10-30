using System;
using System.Collections.Generic;
using Nivalis.GhostSystem;
using Nivalis.GhostSystem.CustomerLoop;
using UnityEngine;

namespace NivalisAIChat.Game;

/// <summary>
/// FB-63: chairs near the player, for a date: the one you're sitting on (a chair's sitting spot right where you are), and a
/// free one at your table (or the nearest free one) for them. The chair is borrowed the game's way: marked as reserved by
/// your date's ghost (ChairGhost.ReservedBy), so the game's own customers don't take it, and given back after. Read from
/// the game's chair views; looked up at most every couple of seconds.
/// </summary>
internal static class Seats
{
    internal sealed class Seat
    {
        public ChairView View = null!;
        public ChairGhost? Ghost;
        public Vector3 Pos;
        public Quaternion Rot;
        public string Table = "";
        public bool Reserved;
        public GhostReference<Ghost>? Before;
    }

    private static bool _probed;

    /// <summary>The chair you're on (within 0.9 m of you), and a free one for them; either null.</summary>
    public static (Seat? mine, Seat? theirs) Near(Vector3 player)
    {
        Seat? mine = null;
        var free = new List<Seat>();
        int seen = 0;
        try
        {
            var views = UnityEngine.Object.FindObjectsOfType<ChairView>();
            foreach (var v in views)
            {
                if (v == null) continue;
                Seat s;
                try { s = Read(v); } catch { continue; }
                float d = Flat(s.Pos - player).magnitude;
                if (d > 6f) continue;
                seen++;
                if (d < 0.9f && (mine == null || d < Flat(mine.Pos - player).magnitude)) mine = s;
                else if (d <= 3f && IsFree(s)) free.Add(s);
            }
        }
        catch (Exception e) { Log.Debug("seats: " + e.Message); return (null, null); }
        Seat? theirs = null;
        float best = float.MaxValue;
        foreach (var s in free)
        {
            if (mine != null && s.View.Pointer == mine.View.Pointer) continue;
            // At your table first (any distance up to 3 m), else the nearest free chair within 2.5 m.
            bool sameTable = mine != null && mine.Table.Length > 0 && s.Table == mine.Table;
            float d = Flat(s.Pos - (mine?.Pos ?? player)).magnitude - (sameTable ? 10f : 0f);
            if (!sameTable && d > 2.5f) continue;
            if (d < best) { best = d; theirs = s; }
        }
        if (!_probed && mine != null)
        {
            _probed = true;
            Log.Info($"Seats (probe): {seen} chairs within 6 m; yours at {mine.Pos} (table '{mine.Table}'), " +
                     $"{(theirs != null ? $"a free one at {theirs.Pos} (table '{theirs.Table}', {Flat(theirs.Pos - mine.Pos).magnitude:0.0} m from yours)" : "no free one near")}.");
        }
        return (mine, theirs);
    }

    /// <summary>A free chair within <paramref name="radius"/> of <paramref name="near"/> (on the same floor), the nearest; or null.</summary>
    public static Seat? FreeNear(Vector3 near, float radius)
    {
        Seat? best = null;
        try
        {
            foreach (var v in UnityEngine.Object.FindObjectsOfType<ChairView>())
            {
                if (v == null) continue;
                Seat s;
                try { s = Read(v); } catch { continue; }
                if (Flat(s.Pos - near).magnitude > radius || Mathf.Abs(s.Pos.y - near.y) > 1.5f || !IsFree(s)) continue;
                if (best == null || Flat(s.Pos - near).magnitude < Flat(best.Pos - near).magnitude) best = s;
            }
        }
        catch (Exception e) { Log.Debug("free seat: " + e.Message); }
        return best;
    }

    private static Seat Read(ChairView v)
    {
        var s = new Seat { View = v };
        Transform at = v.transform;
        try { if (v.sitSpot != null) at = v.sitSpot.transform; } catch { }
        s.Pos = at.position;
        s.Rot = at.rotation;
        try { s.Ghost = v.BaseGhost?.TryCast<ChairGhost>(); } catch { s.Ghost = null; }
        if (s.Ghost != null) try { s.Table = s.Ghost.Table.ReferencedId ?? ""; } catch { }
        return s;
    }

    private static bool IsFree(Seat s)
    {
        if (s.Ghost == null) return false; // no game chair behind it: not one to take
        try { return !s.Ghost.IsOccupied && s.Ghost.ReservedBy.IsNull && !s.Ghost.IsSeated; }
        catch { return false; }
    }

    /// <summary>Borrowed for <paramref name="by"/>: the game's customers leave it. False if it couldn't be marked (sat on anyway).</summary>
    public static bool Reserve(Seat s, Ghost? by)
    {
        if (s.Ghost == null || by == null) return false;
        try
        {
            s.Before = s.Ghost.ReservedBy;
            s.Ghost.ReservedBy = new GhostReference<Ghost>(by);
            s.Reserved = true;
            return true;
        }
        catch (Exception e) { Log.Info("Seats (probe): a chair couldn't be marked as taken (" + e.GetType().Name + "); they sit on it anyway."); return false; }
    }

    /// <summary>Given back (as it was before, only if it's still ours).</summary>
    public static void Free(Seat? s, Ghost? by)
    {
        if (s == null || !s.Reserved || s.Ghost == null) return;
        s.Reserved = false;
        try
        {
            string mine = by != null ? by.Id ?? "" : "";
            string now = s.Ghost.ReservedBy.ReferencedId ?? "";
            if (mine.Length > 0 && now == mine && s.Before is { } before) s.Ghost.ReservedBy = before;
        }
        catch (Exception e) { Log.Debug("seat free: " + e.Message); }
    }

    private static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);
}
