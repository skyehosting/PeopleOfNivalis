using System;
using System.Collections.Generic;
using System.Linq;
using Nivalis;
using Nivalis.GhostSystem;
using Nivalis.GhostSystem.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using UnityEngine;
using Character = Nivalis.Character;

namespace NivalisAIChat;

/// <summary>
/// FB-72 (the author: "if the user said they are down at the docks and could use some company, npc's would be able to
/// come down, if one wanted to. no more than 2 and they would greet the user when they got their. The npc wouldn't just
/// greet them by name unless they knew the user by name or username at least."; chosen: by text and on Nightline, they stay
/// a while, they come separately). Someone who says they're coming ([[coming]] in a text, comingToYou on Nightline) turns up
/// near you a minute or so later (their ghost brought over with the game's own transfer, as a meet-up's is: nothing of the
/// game's schedule changed), walks up and greets you (the meet-up's greeting: AI-greetings lever and pacer), then stays:
/// standing near you, sitting beside you if you sit. You can talk to them any time. They leave when you walk off, or after
/// about an in-game hour. At most <see cref="MaxVisitors"/>, never anyone under 18, at work or busy with the game's life.
/// No AI call decides it: it rides on the reply already made. ComeKeepYouCompany.
/// </summary>
public static partial class ModRuntime
{
    private sealed class Visitor
    {
        public string Id = "", Name = "", Via = "", Place = "";
        public float ArriveAt, Since, NextLookup, NextWalk, AwayFor, NextFace, NextSeat;
        public int Generation;
        public Character? C;
        public AgentGhost? Ghost;
        public IntPtr GhostPtr;
        public bool Owns, Arrived, Greeted, InChat;
        public long StayUntil = -1;
        public Vector3 Spot;
        public Seats.Seat? Seat;
    }

    private static readonly List<Visitor> Visitors = new();
    public const int MaxVisitors = 2;

    private static bool CompanyOn => _cfg != null && _cfg.ComeKeepYouCompany.Value && Features.Phone;

    /// <summary>Room for them to come: the switch on, fewer than two coming or here, and they aren't with you already.</summary>
    private static bool CompanyRoom(string id) =>
        CompanyOn && !_resetting && Visitors.Count < MaxVisitors && Visitors.All(v => v.Id != id) && Companion(id) == null
        && _date?.Id != id && _rendezvous?.Id != id && !KnownUnder18(id);

    /// <summary>Why they couldn't come right now ("" when they could): told to them before they answer, so they don't promise.</summary>
    private static string CantComeNow(string id)
    {
        int hour = GameContext.Hour();
        if (hour is >= 2 and < 6) return "it's the middle of the night and you're in for the night";
        var ghost = GhostOfPerson(id);
        if (ghost == null) return "";
        string task = "";
        try { task = ghost.CurrentAction?.Type?.name ?? ""; } catch { }
        if (task.Contains("Workplace", StringComparison.OrdinalIgnoreCase) || GameContext.GhostBusy(ghost)) return "you're at work (or busy with something you can't leave)";
        return "";
    }

    /// <summary>StartAnswer: what they're told about coming over, added to the texting note (nothing when it's off or it's Ava).</summary>
    private static string CompanyNote(string id, string note)
    {
        if (!CompanyOn || id == _avaId) return note;
        string line;
        if (!CompanyRoom(id)) line = "If they ask you to come over or keep them company right now: you can't make it right now (say so in your own way).";
        else
        {
            string why = CantComeNow(id);
            line = why.Length > 0
                ? $"If they ask you to come over or keep them company right now: you can't, {why} (say so in your own way)."
                : "If they've asked you to come keep them company (or come over) where they are right now, and you want to and can go now: " +
                  "your texts say you're on your way, and put [[coming]] on its own line after them. Only then; never otherwise.";
        }
        return note.Length > 0 ? note + "\n" + line : line;
    }

    /// <summary>
    /// They said they're coming (a text, or Nightline): they turn up a minute or so from now (a second one later than the
    /// first). No room after all (a race): an apology by text instead, when they came by text.
    /// </summary>
    private static void QueueVisitor(string id, string name, string via)
    {
        if (!CompanyRoom(id) || CantComeNow(id).Length > 0)
        {
            Log.Info($"Company: {name} said they'd come, but can't now.");
            if (via == "text" && _phone != null) TextArrived(id, "Ah wait, something came up. Rain check?", _resetGeneration);
            return;
        }
        float now = Time.unscaledTime;
        float at = now + UnityEngine.Random.Range(35f, 90f);
        foreach (var other in Visitors) at = Mathf.Max(at, other.ArriveAt + UnityEngine.Random.Range(20f, 45f)); // separately
        string place = "";
        try { place = string.Join(", ", GameContext.PlacesHere().AsEnumerable().Reverse()); } catch { }
        Visitors.Add(new Visitor { Id = id, Name = name, Via = via, ArriveAt = at, Since = now, Generation = _resetGeneration, Place = place });
        Log.Info($"Company: {name} is coming to keep you company ({via}), in about {at - now:0} s.");
    }

    /// <summary>Every frame: who's coming arrives, walks up, greets you, stays, and goes.</summary>
    private static void TickCompany()
    {
        if (Visitors.Count == 0) return;
        if (!CompanyOn || _resetting) { EndAllCompany("company is off"); return; }
        var pc = GameContext.PlayerCharacter();
        if (pc == null) return;
        foreach (var v in Visitors.ToList())
        {
            try
            {
                if (v.Generation != _resetGeneration) { EndCompany(v, "another save", quiet: true); continue; }
                TickVisitor(v, pc);
            }
            catch (Exception e) { Log.Debug("company: " + e.Message); EndCompany(v, "an error", quiet: true); }
        }
    }

    private static void TickVisitor(Visitor v, PlayerCharacter pc)
    {
        float now = Time.unscaledTime;
        if (!v.Arrived)
        {
            if (now < v.ArriveAt || !InGameplay()) return;
            v.Arrived = true;
            v.Since = now;
            if (!BringVisitor(v, pc)) { if (v.Via == "text") TextArrived(v.Id, "Sorry, couldn't make it after all. Another time?", _resetGeneration); EndCompany(v, "they couldn't come", quiet: true); }
            return;
        }
        // In a chat with you: the chat has them; afterwards they stay on if they're still about.
        if (ChatOpen && v.C != null && _npc != null && Same(_npc, v.C)) { v.InChat = true; v.Owns = false; return; }
        if (v.InChat)
        {
            v.InChat = false;
            if (v.C == null || !Alive(v.C) || Flat(v.C.transform.position - pc.transform.position).magnitude > 6f) { EndCompany(v, "they went on their way after you talked", quiet: true); return; }
            if (!NpcHold.IsHeld(v.C) && NpcHold.EngageMember(v.C, _cfg.AnimateNpc.Value)) v.Owns = true;
            v.Spot = pc.transform.position;
            v.Seat = null;
        }
        if (v.C == null || !Alive(v.C))
        {
            v.C = null;
            v.Owns = false;
            if (now < v.NextLookup) return;
            v.NextLookup = now + 0.25f;
            foreach (var (c, _, _) in NearBodies(pc))
            {
                IntPtr gp = IntPtr.Zero;
                try { gp = c.Agent?.MyGhost?.Pointer ?? IntPtr.Zero; } catch { }
                if (gp == IntPtr.Zero || gp != v.GhostPtr) continue;
                v.C = c;
                break;
            }
            if (v.C == null) { if (now - v.Since > 15f) EndCompany(v, "no body for them", quiet: true); return; }
            if (MinorInfo(v.C, v.Id).minor) { EndCompany(v, "", quiet: true); return; }
        }
        var c0 = v.C;
        if (!v.Owns)
        {
            if (NpcHold.IsHeld(c0) || TalkingNow(c0, v.Id)) { if (now - v.Since > 30f) EndCompany(v, "someone else had them", quiet: true); return; }
            if (!NpcHold.EngageMember(c0, _cfg.AnimateNpc.Value)) { EndCompany(v, "they couldn't be held", quiet: true); return; }
            v.Owns = true;
        }
        Vector3 you = pc.transform.position;
        if (!v.Greeted)
        {
            float d = Flat(c0.transform.position - you).magnitude;
            if (d > 2.4f && now - v.Since < 30f)
            {
                if (now >= v.NextWalk && !NpcHold.IsWalking(c0))
                {
                    v.NextWalk = now + 2f;
                    NpcHold.WalkTo(c0, you + Flat(c0.transform.position - you).normalized * 1.5f, 0.3f, GameContext.TypicalWalkParam());
                }
                return;
            }
            GreetVisitor(v, c0);
            v.Spot = you;
            return;
        }
        // Staying: they leave when you walk off (out of sight of where they came to, for a few seconds) or after a while.
        float away = Flat(you - v.Spot).magnitude;
        if (away > 18f) { v.AwayFor += Time.unscaledDeltaTime; if (v.AwayFor > 8f) { EndCompany(v, "you walked off"); return; } }
        else v.AwayFor = 0f;
        long game = GameNow();
        if (v.StayUntil >= 0 && game >= v.StayUntil) { Greetings.Gesture(c0, GreetingRules.Kind.Wave); EndCompany(v, "it's been a while"); return; }
        // FB-69: their own day comes first: the game wanting them at work (or busy with its life) ends the visit.
        if (now >= v.NextLookup)
        {
            v.NextLookup = now + 10f;
            string task = "";
            try { task = v.Ghost?.CurrentAction?.Type?.name ?? ""; } catch { }
            if (task.Contains("Workplace", StringComparison.OrdinalIgnoreCase)) { Greetings.Gesture(c0, GreetingRules.Kind.Wave); EndCompany(v, "they have to get to work"); return; }
        }
        if (now >= v.NextFace && v.Seat == null)
        {
            v.NextFace = now + 6f;
            var cam = GameContext.PlayerCamera();
            if (cam != null) NpcHold.LookAtFor(c0, cam.transform);
        }
        // You sit down (still a couple of seconds on a chair): they take a free chair beside you, as a date does.
        if (v.Seat == null && now >= v.NextSeat && away < 3f && !NpcHold.SeatBusy(c0))
        {
            v.NextSeat = now + 3f;
            var (mine, theirs) = Seats.Near(you);
            if (mine != null && theirs != null && Visitors.All(o => o.Seat == null || o.Seat.View.Pointer != theirs.View.Pointer))
            {
                Seats.Reserve(theirs, v.Ghost);
                if (NpcHold.SitAt(c0, theirs.Pos, theirs.Rot)) { v.Seat = theirs; Log.Info($"Company: {v.Name} sits down beside you."); }
                else Seats.Free(theirs, v.Ghost);
            }
        }
        else if (v.Seat != null && Flat(you - v.Seat.Pos).magnitude > 2.5f)
        {
            // You got up: so do they.
            try { NpcHold.StandUp(c0); } catch { }
            Seats.Free(v.Seat, v.Ghost);
            v.Seat = null;
            v.Spot = you;
        }
    }

    /// <summary>Near you already: that body. Otherwise their ghost is brought a few metres behind you (the game's own transfer).</summary>
    private static bool BringVisitor(Visitor v, PlayerCharacter pc)
    {
        foreach (var (c, _, pos) in NearBodies(pc))
        {
            if (Flat(pos - pc.transform.position).magnitude > ChatterSettingsNow().Range) continue;
            string id;
            try { id = GameContext.NpcId(c); } catch { continue; }
            if (id != v.Id) continue;
            if (MinorInfo(c, id).minor) return false;
            if (NpcHold.IsHeld(c) || TalkingNow(c, id) || GameContext.VisitOf(c).step.Length > 0 || GameContext.IsWorkerTask(c) || GameContext.OnShift(c)) return false;
            v.C = c;
            try { v.Ghost = c.Agent?.MyGhost; v.GhostPtr = v.Ghost?.Pointer ?? IntPtr.Zero; } catch { }
            Log.Info($"Company: {v.Name} was nearby and comes over.");
            return true;
        }
        try { if (ActiveBrain.Store.Glance(v.Id) is not { } rec || ActiveBrain.IsKnownMinor(rec) || rec.Visual.Minor) return false; }
        catch { return false; }
        var ghost = GhostOfPerson(v.Id);
        if (ghost == null || CantComeNow(v.Id).Length > 0) return false;
        try
        {
            int scene = pc.MyGhost != null ? pc.MyGhost.SceneIndex : -1;
            var t = pc.transform;
            if (scene < 0) return false;
            GhostManager.TransferGhostToScene(ghost, scene, new PositionRotation(t.position - t.forward * 5f, t.rotation));
        }
        catch (Exception e) { Log.Info($"Company: {v.Name} couldn't be brought here ({e.GetType().Name})."); return false; }
        v.Ghost = ghost;
        v.GhostPtr = ghost.Pointer;
        Log.Info($"Company: {v.Name} has arrived and is walking over to you.");
        return true;
    }

    /// <summary>They face you, wave and greet you, by your name only if they know it (or your Nightline name, if that's how).</summary>
    private static void GreetVisitor(Visitor v, Character c)
    {
        v.Greeted = true;
        v.StayUntil = GameNow() is var g and >= 0 ? g + 60 : -1; // about an in-game hour
        var cam = GameContext.PlayerCamera();
        if (cam != null) NpcHold.LookAtFor(c, cam.transform);
        Greetings.Gesture(c, GreetingRules.Kind.Wave);
        string name = "";
        try { name = ActiveBrain.Store.Glance(v.Id)?.State.PlayerName ?? ""; } catch { }
        string handle = v.Via == "nightline" ? YourHandle : "";
        string how = v.Via == "nightline" ? "saw your post on Nightline" : "got your text";
        string where = v.Place.Length > 0 ? $" at {v.Place}" : "";
        string nameRule = Social.CompanyRules.NameRule(name, handle);
        string situation = $"They said they could use some company{where}; you {how} and came to keep them company, and here you are. " +
                           $"Greet them like that, warmly and briefly. {nameRule}";
        string approach = $"They could use some company{where}; you came to keep them company and greeted them with \"{{line}}\". Now you're together.";
        string mood = _minds?.MoodNow(v.Id, GameNow())?.mood ?? "";
        string[] stock = { "Hey. Heard you could use some company.", "There you are. Mind if I join you?", "Hey you. I came as quick as I could.", "Room for one more?" };
        int gen = v.Generation;
        SpeakUp(c, v.Id, situation, line =>
            {
                if (gen != _resetGeneration) return;
                Greetings.MarkMeetup(v.Id, mood, approach);
                Greetings.Said(v.Id, line);
            },
            fallback: stock[UnityEngine.Random.Range(0, stock.Length)]);
        try { Stays(v.Id, v.Name, PersonaOf(v.Id), "you:company", "came to keep the newcomer company when they asked", 0.25f, 0.1f, 12, "content"); } catch { }
        Log.Info($"Company: {v.Name} is here and keeps you company.");
    }

    /// <summary>OpenChat: talking to one who came to keep you company: the chat takes them (they stay on after, if you're still there).</summary>
    private static void CompanyHandsOver(Character c)
    {
        foreach (var v in Visitors)
        {
            if (v.C == null || !Same(v.C, c)) continue;
            if (v.Seat != null) { try { Seats.Free(v.Seat, v.Ghost); } catch { } v.Seat = null; }
            if (v.Owns) try { NpcHold.ReleaseMember(c); } catch { }
            v.Owns = false;
            v.InChat = true;
        }
    }

    private static void EndCompany(Visitor v, string why, bool quiet = false)
    {
        Visitors.Remove(v);
        if (v.Seat != null) { try { Seats.Free(v.Seat, v.Ghost); } catch { } v.Seat = null; }
        if (v.Owns && v.C != null && Alive(v.C) && !(ChatOpen && _npc != null && Same(_npc, v.C)) && Followers.All(f => !Same(f.C, v.C!)))
            try { NpcHold.ReleaseMember(v.C); } catch { }
        if (quiet) Log.Debug($"company: {v.Name} ({why})");
        else Log.Info($"Company: {v.Name} heads off ({why}).");
    }

    private static void EndAllCompany(string why) { foreach (var v in Visitors.ToList()) EndCompany(v, why, quiet: true); }
}
