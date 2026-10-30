using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using UnityEngine;

namespace NivalisAIChat;

/// <summary>
/// FB-58: your staff's work life, noticed as it happens, wherever you are: once every game hour, each of your employees
/// who already has a record is read (the game's job satisfaction, pay, hours, how busy their place is) and what changed is
/// remembered (<see cref="WorkLife.Step"/>), gently moving how they feel about you. No AI, no hooks into the game's own pay
/// code (only reads); never anyone under 18.
/// </summary>
public static partial class ModRuntime
{
    private static float _nextWorkLook;
    private static int _lastWorkHourStamp = -1;

    private static void TickWorkLife()
    {
        float now = Time.unscaledTime;
        if (now < _nextWorkLook) return;
        _nextWorkLook = now + 5f;
        try { NoteWorkLife(); }
        catch (Exception e) { Log.Debug("work life: " + e.Message); }
    }

    private static void NoteWorkLife()
    {
        if (!Features.WorkLife || _resetting) return;
        if (!ChatOpen && !InGameplay()) return;
        int day = GameContext.Day(), hour = GameContext.Hour();
        if (day < 0 || hour < 0) return;
        int stamp = day * 24 + hour;
        if (stamp == _lastWorkHourStamp) return; // once a game hour
        var brain = ActiveBrain;
        var readings = new List<(string id, WorkReading r)>();
        var onStaff = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (person, venue) in GameContext.PlayerStaff())
        {
            try
            {
                string id = GameContext.PersonId(person);
                if (id.Length > 0) onStaff.Add(id);
                if (id.Length == 0 || brain.KnownMinors.Contains(id) || !brain.Store.Has(id)) continue; // nobody is created for it
                if (GameContext.JobOf(person, venue) is { } r) readings.Add((id, r));
            }
            catch { }
        }
        var hired = NewlyHired(onStaff, stamp);
        if (readings.Count == 0) return;
        _lastWorkHourStamp = stamp; // taken only once they've been read (review F4)
        foreach (var (id, r) in readings)
        {
            long epoch = brain.EpochFor(id); // now, on the game's thread: a reset of them since means nothing is kept
            Task.Run(async () =>
            {
                try
                {
                    var noted = new List<WorkEvent>();
                    await brain.Mutate(id, rec =>
                    {
                        if (brain.EpochFor(id) != epoch || brain.IsKnownMinor(rec)) return false;
                        rec.Work ??= new WorkLifeState();
                        string before = WorkLife.Fingerprint(rec.Work);
                        WorkLife.NoteSince(rec.Work, rec.WorkEvents, r.Day, hired.Contains(id)); // CF-14: since when
                        noted = WorkLife.Step(rec.Work, r);
                        foreach (var e in noted) WorkLife.Remember(rec, e);
                        return noted.Count > 0 || WorkLife.Fingerprint(rec.Work) != before; // saved only when something moved (review F4)
                    }, skipIfLive: true).ConfigureAwait(false); // mid-chat: read again next hour, nothing piled up
                    foreach (var e in noted) Log.Debug($"work life: {id}: {e.Text} (feeling {e.Mood:+0.0;-0.0})");
                }
                catch (Exception e) { Log.Debug("noting work life: " + e.Message); }
            });
        }
        Log.Debug($"work life: day {day} {hour:00}:00, {readings.Count} of your staff looked at" +
                  (readings.Count > 0 ? $" (e.g. {readings[0].r.Place}: {Or(readings[0].r.Happiness, "?")}, wage {readings[0].r.Wage} of usual {readings[0].r.BaseMin}-{readings[0].r.BaseMax}, " +
                   $"shift {readings[0].r.ShiftStart:0.#}-{readings[0].r.ShiftEnd:0.#}, would like {readings[0].r.PreferredHours} h, {readings[0].r.Customers} customers)" : ""));
    }

    private static HashSet<string>? _staffSeen;
    private static int _staffSeenStamp = -1;

    /// <summary>
    /// CF-14: who joined the player's staff since the last look in this session (seen hired: their start day is exact). The
    /// first look of a session, a clock that went back (another save loaded) or many at once (another save) is nobody.
    /// </summary>
    private static HashSet<string> NewlyHired(HashSet<string> onStaff, int stamp)
    {
        var hired = new HashSet<string>(StringComparer.Ordinal);
        if (_staffSeen != null && stamp >= _staffSeenStamp)
            foreach (var id in onStaff)
                if (!_staffSeen.Contains(id)) hired.Add(id);
        if (hired.Count > 2) hired.Clear();
        _staffSeen = onStaff;
        _staffSeenStamp = stamp;
        foreach (var id in hired) Log.Debug($"work life: {id} joined your staff (their first day)");
        return hired;
    }

    private static string Or(string s, string fallback) => s.Length > 0 ? s : fallback;
}
