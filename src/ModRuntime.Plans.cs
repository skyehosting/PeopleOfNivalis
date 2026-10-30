using System;
using System.Linq;
using Nivalis;
using Nivalis.GhostSystem;
using Nivalis.GhostSystem.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Mind;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using UnityEngine;

namespace NivalisAIChat;

/// <summary>
/// The inner-life round, step 5 (plans that happen; the research's "she actually showed up"): a plan made in a chat to meet
/// someone somewhere (Mind.Meetups). While you're there in its window, they come: their ghost is brought into your scene a
/// few metres behind you (the game's own GhostManager.TransferGhostToScene, as following and the partner at home do: no
/// schedule is changed, nothing is written into the game's save), they walk over and greet you (one greeting line: the
/// AI-greetings lever, the "greeting" voice), and your chat with them opens as that moment. Someone flaky may not make it.
/// You never came: you stood them up, it hurts, they remember it (pinned), and it gets around. Only near you by its nature
/// (they come to where you are). Off with PlansHappen, Reminders or InnerLife.
/// </summary>
public static partial class ModRuntime
{
    private sealed class Rendezvous
    {
        public string ReminderId = "", Id = "", Name = "", Place = "";
        public int Hour = -1;
        public AgentGhost? Ghost;
        public IntPtr GhostPtr;
        public Character? C;
        public float Since, NextWalk, NextLookup, ReleaseAt = -1f;
        public bool Greeted;
        /// <summary>Review (game IL-1): the meet-up took the hold itself; only then does it walk them or let them go.</summary>
        public bool Owns;
        /// <summary>Review (game IL-8): the save the meet-up began in (a load since means its greeting marks nothing).</summary>
        public int Generation;
    }

    private static Rendezvous? _rendezvous;
    private static float _nextPlansLook;

    private static bool PlansOn => Features.InnerLife && Features.Reminders && _cfg.PlansHappen.Value && _reminders != null;

    /// <summary>Every few seconds (TickReminders, before what's due is shown): meet-ups whose window you're in, or that are past.</summary>
    private static void TickPlans()
    {
        if (_rendezvous != null) TickRendezvous(_rendezvous);
        float now = Time.unscaledTime;
        if (now < _nextPlansLook || !PlansOn || _resetting) return;
        _nextPlansLook = now + 3f;
        var open = _reminders!.OpenMeetups();
        if (open.Count == 0) return;
        long game = GameNow();
        if (game < 0) return;
        // CF-20: the district and the venue you're in (a plan's place is usually a venue: "the noodle bar").
        System.Collections.Generic.List<string> where = new();
        try { where = GameContext.PlacesHere(); } catch { }
        foreach (var r in open)
        {
            if (KnownUnder18(r.WhoId)) { _reminders.SetOutcome(r.Id, Meetups.Flaked); continue; }
            var persona = PersonaOf(r.WhoId);
            if (persona == null) continue; // read in the background; next look
            bool comes = Meetups.TheyCome(r, persona);
            if (Meetups.Settle(r, game, comes) is { } outcome) { Settle(r, outcome, persona); continue; }
            var (from, _) = Meetups.Window(r);
            if (game < from || !ReminderBook.PlaceMatches(where, r.Place) || !InGameplay()) continue;
            if (!r.PlayerCame) { _reminders.Came(r.Id); Log.Info($"Plans: you're at {r.Place} ({string.Join(", ", where)}) for '{r.What}' with {r.WhoName}."); }
            if (!comes || _rendezvous != null) continue;
            // With you already (following you, your date, at home with you, or you're talking to them): that's the meet-up.
            // Review OWN-1: someone who came to keep you company (and greeted you) is with you too.
            if (Companion(r.WhoId) != null || _date?.Id == r.WhoId || (ChatOpen && _session?.Record.Id == r.WhoId)
                || Visitors.Any(v => v.Id == r.WhoId && v.Greeted)) { Settle(r, Meetups.Met, persona); continue; }
            StartRendezvous(r, persona);
        }
    }

    private static void StartRendezvous(ReminderBook.Reminder r, Persona persona)
    {
        var pc = GameContext.PlayerCharacter();
        if (pc == null) return;
        var rz = new Rendezvous
        {
            ReminderId = r.Id, Id = r.WhoId, Name = r.WhoName.Length > 0 ? r.WhoName : persona.Name, Place = r.Place, Hour = r.Hour,
            Since = Time.unscaledTime, Generation = _resetGeneration,
        };
        // Near you already (they were about here anyway): that body. Busy with someone else (held, talking): not now, wait.
        foreach (var (c, _, pos) in NearBodies(pc))
        {
            if (Flat(pos - pc.transform.position).magnitude > ChatterSettingsNow().Range) continue;
            string id;
            try { id = GameContext.NpcId(c); } catch { continue; }
            if (id != r.WhoId) continue;
            if (MinorInfo(c, id).minor) { Settle(r, Meetups.Flaked, persona); return; } // review (game IL-3)
            if (NpcHold.IsHeld(c) || TalkingNow(c, id)) return; // review (game IL-1): someone else has them; the next look tries again
            // The game's own life comes first: someone mid-visit at a food place (ordering, eating) or working isn't pulled out of it.
            if (GameContext.VisitOf(c).step.Length > 0 || GameContext.IsWorkerTask(c) || GameContext.OnShift(c) || GameContext.IsSeated(c)) return; // review IL-13: seated too
            rz.C = c;
            try { rz.Ghost = c.Agent?.MyGhost; rz.GhostPtr = rz.Ghost?.Pointer ?? IntPtr.Zero; } catch { }
            break;
        }
        if (rz.C == null)
        {
            // Review (game IL-3): never anyone their record says is under 18, before their ghost is moved.
            try { if (ActiveBrain.Store.Glance(r.WhoId) is not { } rec || ActiveBrain.IsKnownMinor(rec) || rec.Visual.Minor) { Settle(r, Meetups.Flaked, persona); return; } }
            catch { return; }
            var ghost = GhostOfPerson(r.WhoId);
            if (ghost == null) { Log.Info($"Plans: {rz.Name} couldn't be found in the city right now: they don't make it."); Settle(r, Meetups.Flaked, persona); return; }
            string task = "";
            try { task = ghost.CurrentAction?.Type?.name ?? ""; } catch { }
            if (task.Contains("Workplace", StringComparison.OrdinalIgnoreCase)) { Log.Info($"Plans: {rz.Name} is stuck at work ({task}): they don't make it."); Settle(r, Meetups.Flaked, persona); return; }
            // Out at a food place, or on a shift (the game is running it, by its action's type): left alone; the next look tries again.
            if (GameContext.GhostBusy(ghost) || task.Contains("Venue", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                int scene = pc.MyGhost != null ? pc.MyGhost.SceneIndex : -1;
                var t = pc.transform;
                // A few metres behind you (out of view), facing you: they walk up from there.
                if (scene >= 0) GhostManager.TransferGhostToScene(ghost, scene, new PositionRotation(t.position - t.forward * 4f, t.rotation));
            }
            catch (Exception e) { Log.Info($"Plans: {rz.Name} couldn't be brought here ({e.GetType().Name}): they don't make it."); Settle(r, Meetups.Flaked, persona); return; }
            rz.Ghost = ghost;
            rz.GhostPtr = ghost.Pointer;
        }
        _rendezvous = rz;
        Log.Info($"Plans: {rz.Name} is on their way over to you ({r.Place}).");
    }

    private static void TickRendezvous(Rendezvous rz)
    {
        float now = Time.unscaledTime;
        if (!PlansOn || _resetting) { EndRendezvous("plans are off"); return; }
        var pc = GameContext.PlayerCharacter();
        if (pc == null) return;
        if (ChatOpen && rz.C != null && _npc != null && Same(_npc, rz.C)) { _rendezvous = null; return; } // the chat has them now
        if (rz.ReleaseAt > 0 && now >= rz.ReleaseAt) { EndRendezvous("they waited for you to talk"); return; }
        if (rz.C == null || !Alive(rz.C))
        {
            rz.C = null;
            rz.Owns = false;
            // Review (game IL-2): a few looks a second, among the people near you (shared once a frame), not every body every frame.
            if (now < rz.NextLookup) return;
            rz.NextLookup = now + 0.25f;
            foreach (var (c, _, _) in NearBodies(pc))
            {
                IntPtr gp = IntPtr.Zero;
                try { gp = c.Agent?.MyGhost?.Pointer ?? IntPtr.Zero; } catch { }
                if (gp == IntPtr.Zero || gp != rz.GhostPtr) continue;
                rz.C = c;
                break;
            }
            if (rz.C == null)
            {
                if (now - rz.Since > 12f) { Log.Info($"Plans: {rz.Name} didn't arrive after all (no body for them)."); FlakeRendezvous(rz); }
                return;
            }
            if (MinorInfo(rz.C, rz.Id).minor) { FlakeRendezvous(rz); return; } // review (game IL-3): the body the game gave them
        }
        var c0 = rz.C;
        if (rz.Greeted) return;
        if (!rz.Owns)
        {
            // Review (game IL-1): someone else has them (a residents' talk, your date, home): they're busy; never taken from them.
            if (NpcHold.IsHeld(c0) || TalkingNow(c0, rz.Id)) { if (now - rz.Since > 25f) FlakeRendezvous(rz); return; }
            if (!NpcHold.EngageMember(c0, _cfg.AnimateNpc.Value)) { FlakeRendezvous(rz); return; }
            rz.Owns = true;
        }
        Vector3 you = pc.transform.position;
        float d = Flat(c0.transform.position - you).magnitude;
        if (d > 2.4f && now - rz.Since < 25f)
        {
            if (now >= rz.NextWalk && !NpcHold.IsWalking(c0))
            {
                rz.NextWalk = now + 2f;
                var to = you + Flat(c0.transform.position - you).normalized * 1.4f;
                NpcHold.WalkTo(c0, to, 0.3f, GameContext.TypicalWalkParam());
            }
            return;
        }
        // Here: they face you, wave, and greet you (the chat then opens as this moment).
        rz.Greeted = true;
        rz.ReleaseAt = now + 30f;
        var cam = GameContext.PlayerCamera();
        if (cam != null) NpcHold.LookAtFor(c0, cam.transform);
        Greetings.Gesture(c0, GreetingRules.Kind.Wave);
        string time = rz.Hour >= 0 ? $" at {rz.Hour:00}:00" : "";
        string situation = $"You'd agreed to meet them at {rz.Place}{time}, and here they are; you came. Greet them like that.";
        string approach = $"You'd agreed to meet them at {rz.Place}{time}. You came, found them and greeted them with \"{{line}}\". Now you're together.";
        string mood = _minds?.MoodNow(rz.Id, GameNow())?.mood ?? "";
        string[] stock = { "There you are.", "Hey, you made it.", "Right on time.", "Oh, hey! Over here." };
        int gen = rz.Generation;
        SpeakUp(c0, rz.Id, situation, line =>
            {
                if (gen != _resetGeneration) return; // review (game IL-8): another save since; nothing of this one is marked
                Greetings.MarkMeetup(rz.Id, mood, approach);
                Greetings.Said(rz.Id, line);
            },
            fallback: stock[UnityEngine.Random.Range(0, stock.Length)]);
        if (_reminders?.OpenMeetups().FirstOrDefault(x => x.Id == rz.ReminderId) is { } r && PersonaOf(rz.Id) is { } persona) Settle(r, Meetups.Met, persona);
        Log.Info($"Plans: {rz.Name} met you at {rz.Place}, as planned.");
    }

    private static void FlakeRendezvous(Rendezvous rz)
    {
        EndRendezvous("they didn't arrive");
        if (_reminders?.OpenMeetups().FirstOrDefault(x => x.Id == rz.ReminderId) is { } r && PersonaOf(rz.Id) is { } persona) Settle(r, Meetups.Flaked, persona);
    }

    /// <summary>The meet-up is over (it happened, a load, plans off): they're let go to get on with their day (only a hold it took).</summary>
    private static void EndRendezvous(string why)
    {
        var rz = _rendezvous;
        if (rz == null) return;
        _rendezvous = null;
        if (rz.Owns && rz.C != null && Alive(rz.C) && !(ChatOpen && _npc != null && Same(_npc, rz.C)) && Followers.All(f => !Same(f.C, rz.C!)))
            try { NpcHold.ReleaseMember(rz.C); } catch { }
        Log.Debug($"plans: {rz.Name} carries on ({why})");
    }

    /// <summary>OpenChat: talking to the one who came to meet you: the chat takes them from the meet-up.</summary>
    private static void RendezvousHandsOver(Character c)
    {
        if (_rendezvous is { C: { } rc } rz && Same(rc, c))
        {
            _rendezvous = null;
            if (rz.Owns) try { NpcHold.ReleaseMember(c); } catch { }
        }
    }

    /// <summary>
    /// A meet-up's outcome, kept once, and what it does to them: met (glad, the bar up a point), stood up (it hurts for a day
    /// and a half, the bar down two, a hurt they'll bring up, and they remember it pinned: it gets around), flaked (a little
    /// guilty; you're told if you were there).
    /// </summary>
    private static void Settle(ReminderBook.Reminder r, string outcome, Persona persona)
    {
        if (_reminders == null || !_reminders.SetOutcome(r.Id, outcome)) return;
        string id = r.WhoId, name = persona.Name.Length > 0 ? persona.Name : r.WhoName;
        string at = r.Place.Length > 0 ? " at " + r.Place : "";
        long now = GameNow();
        var brain = ActiveBrain;
        long epoch = brain.EpochFor(id);
        Log.Info($"Plans: '{r.What}' with {r.WhoName}: {outcome}.");
        switch (outcome)
        {
            case Meetups.Met:
                Stays(id, name, persona, "plan:" + r.Id, $"met up with the newcomer{at}, as you'd planned", 0.35f, 0.15f, 12, "cheerful");
                if (Features.Relationships)
                    _ = brain.Mutate(id, rec => brain.EpochFor(id) == epoch && !brain.IsKnownMinor(rec) && Shifted(rec, 1.0));
                break;
            case Meetups.StoodUp:
                Stays(id, name, persona, "plan:" + r.Id, $"the newcomer stood you up{at}", -0.5f, 0.15f, 36, "sad");
                _witness?.Lived(id, Happenings.StoodUp, Happenings.Player, "", id, name, r.Place, now, 0.8f);
                string hurt = $"They didn't turn up{at} when you'd agreed to meet (day {r.Day}).";
                _ = brain.Mutate(id, rec =>
                {
                    if (brain.EpochFor(id) != epoch || brain.IsKnownMinor(rec)) return false;
                    if (!rec.State.Hurts.Contains(hurt)) rec.State.Hurts.Add(hurt);
                    while (rec.State.Hurts.Count > 10) rec.State.Hurts.RemoveAt(0);
                    if (Features.Relationships) rec.State.Shift(-2.0);
                    return true;
                });
                break;
            case Meetups.Flaked:
                Stays(id, name, persona, "plan:" + r.Id, $"you didn't make it to meet the newcomer{at}", -0.15f, 0.2f, 24, "anxious");
                if (r.PlayerCame)
                    try
                    {
                        string title = Plain($"{r.WhoName} didn't turn up"), body = Plain($"{Cap(r.What)}{at}");
                        if (!GameToast.Show(title, body)) Ui.ShowToast(title, body);
                    }
                    catch { }
                break;
        }
    }

    private static bool Shifted(NpcRecord rec, double points)
    {
        rec.State.Shift(points);
        return true;
    }
}
