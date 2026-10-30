using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Il2CppInterop.Runtime;
using Nivalis;
using Nivalis.Apartment;
using Nivalis.GhostSystem;
using Nivalis.GhostSystem.Ai;
using Nivalis.GhostSystem.CustomerLoop;
using NivalisAIChat.Game;
using UnityEngine;

namespace NivalisAIChat.Dev;

/// <summary>
/// Research probe (2026-10-18). The research plan (witness layer, bodies, plans that happen) leans on game abilities
/// nobody has tried from this mod yet. `/probe` answers each one in the log ("Probe:" lines), with nothing written to
/// the save:
/// - /probe: needs (hunger, rest), radius queries, free chairs at a table, the wander network, schedules and the
///   named points a schedule could target;
/// - /probe events [minutes]: subscribes to the game's own events (a meal delivered, the player eating, a pop-up,
///   an apartment door) and logs each one;
/// - /probe walk: tries a walk style on someone walking by, and watches whether the game puts its own back;
/// - /probe schedule: adds a runtime schedule entry for the nearest resident (saving blocked meanwhile), watches
///   what their ghost does, then takes it away again;
/// - /probe stop.
/// Every step is guarded: a failure is logged as the answer.
/// </summary>
internal static class CapabilityProbe
{
    private static readonly Il2CppSystem.Object Owner = new();
    private static Il2CppSystem.Action<Order>? _meal;
    private static Il2CppSystem.Action<NotificationMessage>? _note;
    private static Il2CppSystem.Action<ApartmentController, bool>? _door;
    private static Il2CppSystem.Action<MealGhost, Venue>? _eat;
    private static PlayerManager.Player? _eatPlayer;
    private static float _eventsUntil = -1f;
    private static readonly Dictionary<string, int> Counts = new();
    // What a walk or schedule probe changed, so a stop puts it back even if its own timer never runs (the 2026-10-20 audit:
    // ModRuntime.Later jobs die with the mod, which left saving blocked until a restart).
    private static (Character who, Character.AnimatorWalkType before)? _walk;
    private static (Person person, string target, bool added, OverrideableBool.OverrideLock? noSave)? _schedule;
    private static int _scheduleRun, _walkRun;

    private static void P(string s) => Log.Info("Probe: " + s);

    public static void Run(string arg, Action<string> say)
    {
        string a = (arg ?? "").Trim().ToLowerInvariant();
        if (a.StartsWith("events")) { StartEvents(a, say); return; }
        if (a == "walk") { Walk(say); return; }
        if (a == "schedule") { Schedule(say); return; }
        if (a == "stop") { StopAll("stopped"); say("Probe: stopped."); return; }
        if (ResearchProbe.Run(a, say)) return; // names, ava, fish, watch (2026-10-19)
        Snapshot();
        say("Probe: written to LogOutput.log (lines starting \"Probe:\"). Also: /probe events 10, /probe walk, /probe schedule, /probe names, /probe ava, /probe fish, /probe place, /probe watch.");
    }

    // ------------------------------------------------------------------ the snapshot

    private static void Snapshot()
    {
        var pc = GameContext.PlayerCharacter();
        if (pc == null) { P("no player"); return; }
        Vector3 pp = pc.transform.position;
        int scene = -1;
        try { scene = pc.MyGhost != null ? pc.MyGhost.SceneIndex : -1; } catch { }
        P($"--- snapshot at {pp} scene {scene}, day {GameContext.Day()} {GameContext.Hour()}h ---");

        // 1. Needs (hunger, rest) on the nearest residents.
        Guard("needs", () =>
        {
            int n = 0;
            foreach (var c in Character.instances)
            {
                if (c == null || !c.isActiveAndEnabled || (c.transform.position - pp).magnitude > 25f) continue;
                var g = c.Agent?.MyGhost;
                if (g == null) continue;
                var needs = g.State.Needs;
                string task = "";
                try { task = g.CurrentAction?.Type?.name ?? ""; } catch { }
                P($"needs: {c.name} hunger {needs.Hunger:0.00} rest {needs.Rest:0.00} patience {Safe(() => g.Patience.ToString("0.00"))} task {task}");
                if (++n >= 6) break;
            }
            if (n == 0) P("needs: nobody within 25 m");
        });

        // 2. Radius queries through the ghost manager vs scene searches.
        Guard("query", () =>
        {
            var gm = GhostManager._instance;
            if (gm == null) { P("query: no GhostManager._instance"); return; }
            var sw = Stopwatch.StartNew();
            var chairs = new Il2CppSystem.Collections.Generic.List<ChairGhost>();
            gm.QueryGhosts<ChairGhost>(chairs, scene, pp, 10f);
            double tq = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            int views = UnityEngine.Object.FindObjectsOfType<ChairView>().Count(v => v != null && (v.transform.position - pp).magnitude <= 10f);
            double tf = sw.Elapsed.TotalMilliseconds;
            P($"query: QueryGhosts<ChairGhost> r10 = {chairs.Count} in {tq:0.00} ms; FindObjectsOfType<ChairView> r10 = {views} in {tf:0.00} ms");
            var agents = new Il2CppSystem.Collections.Generic.List<AgentGhost>();
            sw.Restart();
            gm.QueryGhosts<AgentGhost>(agents, scene, pp, 15f);
            P($"query: QueryGhosts<AgentGhost> r15 = {agents.Count} in {sw.Elapsed.TotalMilliseconds:0.00} ms (bodies counted the usual way: {BodiesWithin(pp, 15f)})");
            var all = new Il2CppSystem.Collections.Generic.List<AgentGhost>();
            sw.Restart();
            gm.QueryGhosts<AgentGhost>(all, scene);
            P($"query: QueryGhosts<AgentGhost> whole scene = {all.Count} in {sw.Elapsed.TotalMilliseconds:0.00} ms");

            // 3. A table's own free chair.
            ChairGhost? near = null;
            float best = 99f;
            for (int i = 0; i < chairs.Count; i++)
            {
                var ch = chairs[i];
                if (ch == null) continue;
                float d = (ch.Position - pp).magnitude;
                if (d < best) { best = d; near = ch; }
            }
            if (near == null) { P("table: no chair within 10 m"); return; }
            string tableId = Safe(() => near.Table.ReferencedId ?? "");
            P($"table: nearest chair {best:0.0} m, table '{tableId}', occupied {near.IsOccupied}, cover {Safe(() => near.Cover.ToString())}");
            var table = tableId.Length > 0 ? gm.GetGhostById(tableId)?.TryCast<TableGhost>() : null;
            if (table == null) { P("table: the table ghost couldn't be found by id"); return; }
            var free = table.GetFreeChair(true);
            P($"table: GetFreeChair -> '{Safe(() => free.ReferencedId ?? "")}', HasFreeSeat {table.HasFreeSeat(true)}, PlayerReserved {Safe(() => table.PlayerReserved.ToString())}");
        });

        // 4. The wander network (walking away somewhere sensible).
        Guard("wander", () =>
        {
            var wm = WanderPointManager.Instance;
            if (wm == null) { P("wander: no WanderPointManager.Instance"); return; }
            var node = wm.GetClosestNode(pp, scene, pc.transform.forward, false);
            P(node == null ? "wander: GetClosestNode -> null" : $"wander: GetClosestNode -> {node.Position} ({(node.Position - pp).magnitude:0.0} m), id {node.Id}");
        });

        // 5. Schedules, and the named points a schedule could point at.
        Guard("schedule list", () =>
        {
            var c = Nearest(pp, 12f, held: false);
            var person = c != null ? GameContext.PersonOf(c) : null;
            if (person == null) { P("schedule: no resident within 12 m"); return; }
            var list = person.RuntimeData?.Schedule;
            P($"schedule: {c!.name} has {list?.Count ?? -1} runtime entries; person schedule {Safe(() => person.Schedule.Count.ToString())}");
            if (list != null)
                for (int i = 0; i < list.Count && i < 6; i++)
                {
                    var s = list[i];
                    P($"schedule:   [{i}] id '{s.Id}' articy '{s.ArticyId}' action '{Safe(() => s.action?.name ?? "")}' target '{Safe(() => s.target.ReferencedId ?? "")}' scene {s.sceneIndex} stay {s.stayOnSpot} apartment {s.Apartment}");
                }
        });
        Guard("points", () =>
        {
            var points = UnityEngine.Object.FindObjectsOfType<IdlePoint>().Where(p => p != null).OrderBy(p => (p.transform.position - pp).magnitude).Take(6).ToList();
            foreach (var p in points)
                P($"points: IdlePoint '{p.name}' id '{Safe(() => p.Id)}' {(p.transform.position - pp).magnitude:0.0} m");
            if (points.Count == 0) P("points: no IdlePoint in the scene");
        });
        Guard("arrival", () =>
        {
            var tm = GameContext.Existing<TravelManager>();
            bool pending = tm != null && tm.TryGetPendingArrivalPosition(out var at);
            P($"travel: TravelManager {(tm != null ? "found" : "not found")}, pending arrival {pending}");
        });
    }

    // ------------------------------------------------------------------ the game's own events

    private static void StartEvents(string a, Action<string> say)
    {
        int minutes = int.TryParse(a.Replace("events", "").Trim(), out int m) ? Math.Clamp(m, 1, 60) : 10;
        StopEvents("restarted");
        Counts.Clear();
        Guard("subscribe meal", () =>
        {
            _meal = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Order>>(new System.Action<Order>(o => Seen("meal delivered", () =>
                $"customer '{Safe(() => o.ForCustomer.ReferencedId ?? "")}' table '{Safe(() => o.ForTable.ReferencedId ?? "")}' quality {Safe(() => o.ServiceQuality.ToString("0.00"))}")));
            Order.add_OnMealDelivered(_meal);
            P("events: subscribed to Order.OnMealDelivered");
        });
        Guard("subscribe note", () =>
        {
            _note = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<NotificationMessage>>(new System.Action<NotificationMessage>(n => Seen("pop-up", () =>
                $"id '{Safe(() => n.notificationId ?? "")}' header '{Safe(() => n.header ?? "")}'")));
            NotificationManager.add_OnNewMessage(_note);
            P("events: subscribed to NotificationManager.OnNewMessage");
        });
        Guard("subscribe door", () =>
        {
            _door = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ApartmentController, bool>>(new System.Action<ApartmentController, bool>((ctrl, enter) =>
                Seen("apartment door", () => $"{(enter ? "in" : "out")} '{Safe(() => ctrl.name)}'")));
            ApartmentController.OnPlayerEnterExit.Add(_door);
            P("events: subscribed to ApartmentController.OnPlayerEnterExit");
        });
        Guard("subscribe eat", () =>
        {
            _eatPlayer = GameContext.LocalPlayer();
            if (_eatPlayer == null) { P("events: no local player for OnVenueMealEat"); return; }
            _eat = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<MealGhost, Venue>>(new System.Action<MealGhost, Venue>((meal, venue) =>
                Seen("player ate", () => $"at '{Safe(() => GameContext.PlaceName(venue))}'")));
            _eatPlayer.add_OnVenueMealEat(_eat);
            P("events: subscribed to Player.OnVenueMealEat");
        });
        _eventsUntil = Time.unscaledTime + minutes * 60f;
        ModRuntime.Later(minutes * 60f + 1f, () => { if (_eventsUntil > 0 && Time.unscaledTime >= _eventsUntil) StopEvents("time's up"); });
        say($"Probe: listening to the game's events for {minutes} min (log lines \"Probe: event\"). /probe stop ends it.");
    }

    private static void Seen(string kind, Func<string> what)
    {
        try
        {
            Counts[kind] = Counts.GetValueOrDefault(kind) + 1;
            if (Counts[kind] <= 5) P($"event {kind} #{Counts[kind]}: {what()}");
            else if (Counts[kind] % 25 == 0) P($"event {kind}: {Counts[kind]} so far");
        }
        catch (Exception e) { P($"event {kind}: handler failed {e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>Everything a probe left running or changed, put back: /probe stop, and ModRuntime.Stop (a stop or hot reload).</summary>
    public static void StopAll(string why)
    {
        try { StopEvents(why); } catch { }
        try { EndWalk(why); } catch { }
        try { EndSchedule(why); } catch { }
        try { ResearchProbe.StopWatch(why); } catch { }
    }

    private static void StopEvents(string why)
    {
        if (_meal == null && _note == null && _door == null && _eat == null) return;
        Guard("unsubscribe", () =>
        {
            if (_meal != null) Order.remove_OnMealDelivered(_meal);
            if (_note != null) NotificationManager.remove_OnNewMessage(_note);
            if (_door != null) ApartmentController.OnPlayerEnterExit.Remove(_door);
            if (_eat != null && _eatPlayer != null) _eatPlayer.remove_OnVenueMealEat(_eat);
        });
        P($"events: unsubscribed ({why}); counts: {string.Join(", ", Counts.Select(kv => kv.Key + " " + kv.Value))}");
        _meal = null; _note = null; _door = null; _eat = null; _eatPlayer = null;
        _eventsUntil = -1f;
    }

    // ------------------------------------------------------------------ walk styles

    private static void Walk(Action<string> say)
    {
        var pc = GameContext.PlayerCharacter();
        if (pc == null) return;
        Vector3 pp = pc.transform.position;
        Character? who = null;
        foreach (var c in Character.instances)
        {
            if (c == null || !c.isActiveAndEnabled || NpcHold.IsHeld(c) || (c.transform.position - pp).magnitude > 20f) continue;
            float speed = 0f;
            try { speed = c.Movement != null ? c.Movement.speed : 0f; } catch { }
            if (speed > 0.4f) { who = c; break; }
        }
        if (who == null) { say("Probe: nobody walking within 20 m; try again on a busy street."); return; }
        EndWalk("restarted");
        var target = who;
        Character.AnimatorWalkType before = Character.AnimatorWalkType.Walk;
        Guard("walk read", () => before = target.currentWalk);
        P($"walk: {target.name} walks as {before}; switching to WalkSad and watching for 20 s");
        _walk = (target, before);
        int run = ++_walkRun;
        Guard("walk set", () => target.ChangeWalkWeight(Character.AnimatorWalkType.WalkSad, 0.5f));
        for (int i = 1; i <= 20; i++)
        {
            int t = i;
            ModRuntime.Later(t, () => Guard("walk watch", () =>
            {
                if (target == null || target.WasCollected) { P($"walk: +{t}s the body is gone"); return; }
                float speed = 0f;
                try { speed = target.Movement != null ? target.Movement.speed : 0f; } catch { }
                string action = Safe(() => target.state?.current?.name ?? "");
                if (t % 2 == 0) P($"walk: +{t}s currentWalk {target.currentWalk}, speed {speed:0.00}, action '{action}'");
                if (t == 20 && _walkRun == run) EndWalk("done"); // review R3: an earlier run's timer never ends a newer one
            }));
        }
        say($"Probe: trying a sad walk on {target.name} (watch them for 20 s; the log says what the game did).");
    }

    private static void EndWalk(string why)
    {
        if (_walk is not { } w) return;
        _walk = null;
        if (w.who == null || w.who.WasCollected) return;
        Guard("walk put back", () => { w.who.ChangeWalkWeight(w.before, 0.5f); P($"walk: put back to {w.before} ({why})"); });
    }

    // ------------------------------------------------------------------ a runtime schedule entry

    private static void Schedule(Action<string> say)
    {
        var pc = GameContext.PlayerCharacter();
        if (pc == null) return;
        Vector3 pp = pc.transform.position;
        var c = Nearest(pp, 12f, held: false);
        var person = c != null ? GameContext.PersonOf(c) : null;
        if (person == null) { say("Probe: no resident within 12 m."); return; }
        var point = UnityEngine.Object.FindObjectsOfType<IdlePoint>().Where(p => p != null).OrderBy(p => (p.transform.position - pp).magnitude).FirstOrDefault();
        string targetId = point != null ? Safe(() => point.Id) : "";
        if (targetId.Length == 0) { say("Probe: no IdlePoint with an id nearby to point a schedule at."); return; }
        int scene = -1;
        try { scene = pc.MyGhost != null ? pc.MyGhost.SceneIndex : -1; } catch { }
        EndSchedule("restarted");
        OverrideableBool.OverrideLock? noSave = null;
        Guard("no saving", () => noSave = GameContext.Existing<PlayerInputManager>()?.PreventSaving?.Override(Owner));
        var ghost = c!.Agent?.MyGhost;
        int before = person.RuntimeData?.Schedule?.Count ?? -1;
        bool added = false;
        Guard("schedule add", () => { person.AddRuntimeScheduleAction("pon_probe", targetId, scene, true, false, false); added = true; });
        _schedule = (person, targetId, added, noSave);
        int run = ++_scheduleRun;
        int after = person.RuntimeData?.Schedule?.Count ?? -1;
        P($"schedule: {c.name} entries {before} -> {after} after AddRuntimeScheduleAction(target '{targetId}' = IdlePoint '{point!.name}' {(point.transform.position - pp).magnitude:0.0} m, scene {scene}); saving blocked meanwhile: {noSave != null}");
        for (int i = 1; i <= 25; i++)
        {
            int t = i;
            ModRuntime.Later(t, () => Guard("schedule watch", () =>
            {
                if (t % 5 == 0 || t == 1)
                {
                    string task = Safe(() => ghost?.CurrentAction?.Type?.name ?? "");
                    float dist = ghost != null ? (ghost.Position - point.transform.position).magnitude : -1f;
                    P($"schedule: +{t}s task '{task}', {dist:0.0} m from the point");
                }
                if (t == 25 && _scheduleRun == run) EndSchedule("done");
            }));
        }
        say($"Probe: {c.name} has a schedule entry to the nearest idle point for 25 s (saving is blocked meanwhile).");
    }

    private static void EndSchedule(string why)
    {
        if (_schedule is not { } s) return;
        _schedule = null;
        if (s.added) Guard("schedule reset", () =>
        {
            s.person.ResetRuntimeSchedule("pon_probe", s.target);
            P($"schedule: reset ({why}); entries now {s.person.RuntimeData?.Schedule?.Count ?? -1}");
        });
        try { if (s.noSave != null && !s.noSave.IsReleased) s.noSave.Release(); } catch { }
        P("schedule: saving allowed again");
    }

    // ------------------------------------------------------------------ helpers

    private static Character? Nearest(Vector3 pp, float within, bool held)
    {
        Character? best = null;
        float bd = within;
        foreach (var c in Character.instances)
        {
            if (c == null || !c.isActiveAndEnabled || (!held && NpcHold.IsHeld(c)) || GameContext.PersonOf(c) == null) continue;
            float d = (c.transform.position - pp).magnitude;
            if (d < bd) { bd = d; best = c; }
        }
        return best;
    }

    private static int BodiesWithin(Vector3 pp, float r)
    {
        int n = 0;
        foreach (var c in Character.instances) if (c != null && c.isActiveAndEnabled && (c.transform.position - pp).magnitude <= r) n++;
        return n;
    }

    private static void Guard(string what, Action act)
    {
        try { act(); }
        catch (Exception e) { P($"{what}: FAILED {e.GetType().Name}: {e.Message}"); }
    }

    private static string Safe(Func<string> f)
    {
        try { return f(); } catch (Exception e) { return "<" + e.GetType().Name + ">"; }
    }
}
