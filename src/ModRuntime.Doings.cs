using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using UnityEngine;

namespace NivalisAIChat;

/// <summary>
/// FB-36: residents you've met remember what the game showed them doing near you (a meal at a food place, a shift at work),
/// so they can bring it up later ("I had the noodle soup at Izakaya Stop earlier"). No AI; only people who already have a
/// record; never anyone under 18; not at your own places. A cheap look every few seconds.
/// </summary>
public static partial class ModRuntime
{
    private static float _nextDoingsLook;
    /// <summary>Noted this session, by person, kind and game day: true when there's nothing more to add (a meal's dish known).</summary>
    private static readonly Dictionary<(string id, string kind, int day, string place), bool> DoingsNoted = new();
    /// <summary>People with no record, not looked at again until then (one file check, not one every look).</summary>
    private static readonly Dictionary<string, float> NoRecordUntil = new();

    private static void TickDoings()
    {
        float now = Time.unscaledTime;
        if (now < _nextDoingsLook) return;
        _nextDoingsLook = now + 5f;
        try { NoteDoings(now); }
        catch (Exception e) { Log.Debug("doings: " + e.Message); }
    }

    private static void NoteDoings(float now)
    {
        if (!_cfg.RememberDoings.Value || _resetting || ConsoleOpen) return;
        try { if (Time.timeScale <= 0f) return; } catch { }
        if (!ChatOpen && !InGameplay()) return;
        var pc = GameContext.PlayerCharacter();
        if (pc == null) return;
        int day = GameContext.Day();
        if (day < 0) return;
        if (DoingsNoted.Count > 400) foreach (var k in DoingsNoted.Keys.Where(k => k.day < day).ToList()) DoingsNoted.Remove(k);
        if (NoRecordUntil.Count > 400) foreach (var k in NoRecordUntil.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList()) NoRecordUntil.Remove(k);
        var brain = ActiveBrain;
        Vector3 me = pc.transform.position;
        int noted = 0;
        foreach (var (c, _, pos) in NearBodies(pc))
        {
            if (noted >= 4) break; // a few a look is plenty
            try
            {
                if (Flat(pos - me).magnitude > 25f) continue;
                // What the game has them doing first (cheap), then who they are.
                bool eating = GameContext.VisitOf(c).step == "Eating";
                bool working = !eating && GameContext.IsWorkerTask(c);
                if (!eating && !working) continue;
                string id = GameContext.NpcId(c);
                if (id.Length == 0 || id.StartsWith("extra_", StringComparison.Ordinal)) continue; // extras share ids between bodies
                string kind = eating ? Doings.Meal : Doings.Work;
                // A meal is one per place (lunch here, dinner there: final review F5); work is one a day.
                var mealPlace = eating ? GameContext.VisitPlace(c) : null;
                var key = (id, kind, day, eating ? GameContext.PlaceName(mealPlace) : "");
                bool seen = DoingsNoted.TryGetValue(key, out bool complete);
                if (seen && complete) continue;
                if (NoRecordUntil.TryGetValue(id, out float until) && now < until) continue;
                if (brain.KnownMinors.Contains(id) || MinorInfo(c, id).minor) { DoingsNoted[key] = true; continue; }
                if (!brain.Store.Has(id)) { NoRecordUntil[id] = now + 60f; continue; } // nobody is created for it
                Doing d;
                var dishes = new List<string>();
                if (eating)
                {
                    var place = mealPlace;
                    string name = GameContext.PlaceName(place);
                    if (place == null || name.Length == 0) { DoingsNoted[key] = true; continue; }
                    dishes = Ordered(c).Distinct().ToList();
                    if (GameContext.PlayersPlace(place))
                    {
                        // Your own place: no doing (your visits are counted apart), but what they ate there is part of their taste.
                        if (dishes.Count == 0) continue; // waiting for the dish
                        DoingsNoted[key] = true;
                        long ep = brain.EpochFor(id);
                        noted++;
                        Task.Run(async () =>
                        {
                            try { await brain.Mutate(id, rec => brain.EpochFor(id) == ep && !brain.IsKnownMinor(rec) && dishes.Select(x => Tastes.Note(rec, x, name, day)).ToList().Any(b => b)).ConfigureAwait(false); }
                            catch (Exception e) { Log.Debug("noting what someone ate: " + e.Message); }
                        });
                        continue;
                    }
                    string dish = Listed(dishes);
                    if (seen && dish.Length == 0) continue; // noted already; waiting for the dish
                    d = new Doing { GameDay = day, Kind = Doings.Meal, Place = name, What = dish };
                    DoingsNoted[key] = dish.Length > 0;
                }
                else
                {
                    var (place, players, role) = GameContext.WorkplaceOf(c);
                    string name = GameContext.PlaceName(place);
                    if (place == null || players || name.Length == 0) { DoingsNoted[key] = true; continue; } // your own staff are yours
                    d = new Doing { GameDay = day, Kind = Doings.Work, Place = name, What = role };
                    DoingsNoted[key] = true;
                }
                long epoch = brain.EpochFor(id); // now, on the game's thread: a reset of them since means it isn't kept
                noted++;
                Task.Run(async () =>
                {
                    try
                    {
                        // The meal goes into their taste too (the inner-life round: favourites, and what they're sick of).
                        await brain.Mutate(id, rec => brain.EpochFor(id) == epoch && !brain.IsKnownMinor(rec)
                            && (Doings.Note(rec, d) | dishes.Select(x => Tastes.Note(rec, x, d.Place, d.GameDay)).ToList().Any(b => b))).ConfigureAwait(false);
                    }
                    catch (Exception e) { Log.Debug("noting what someone did: " + e.Message); }
                });
                Log.Debug($"doings: {id} {(d.Kind == Doings.Meal ? "eating" : "working")} at {d.Place}{(d.What.Length > 0 ? $" ({d.What})" : "")}");
            }
            catch { }
        }
    }

    /// <summary>A reset: what was noted this session is forgotten too.</summary>
    private static void ForgetDoingsNoted()
    {
        DoingsNoted.Clear();
        NoRecordUntil.Clear();
    }
}
