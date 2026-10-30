using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Tests;

/// <summary>FB-85: dates and staying the night at your place, kept from the game (and the mod) cutting them short.</summary>
internal static partial class Program
{
    private static void Fb85StayOverTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- FB-85: dates, and staying the night at yours");
        Features.Reset();
        Check(Romance.StayHour(20) && Romance.StayHour(23) && Romance.StayHour(0) && Romance.StayHour(5) && !Romance.StayHour(6) && !Romance.StayHour(19),
              "FB-85: staying the night is on the table from 8 in the evening until 6 in the morning");
        Check(Romance.MinutesToMorning(22, 0, 8, 0) == 600 && Romance.MinutesToMorning(1, 30, 8, 30) == 420 && Romance.MinutesToMorning(23, 0, 8, 60) == 600
              && Romance.MinutesToMorning(7, 50, 8, 0) == 60 && Romance.MinutesToMorning(20, 0, 30, 0) == 16 * 60,
              "...until the morning they head off (StayOverUntil, give or take an hour); at least an hour; the hour kept to 5-12");

        var rec = new NpcRecord { Id = "npc_ivy" };
        rec.State.Relationship = Relationships.Neutral;
        rec.State.Romance = Romance.Dating; rec.State.RomanceSinceDay = 1;
        var offer = Romance.Lines(rec, 3, 1, onDate: true, "Sam", stayingOver: false, canStay: true);
        Check(offer.Any(l => l.Contains("stay the night") && l.Contains("date \"stay\"")) && offer.Any(l => l.Contains("it being late isn't a reason on its own")),
              "...they're told they can come home with you and stay (date \"stay\"), and that it being late is no reason to end a date");
        var staying = Romance.Lines(rec, 3, 1, onDate: true, "Sam", stayingOver: true, canStay: true);
        Check(staying.Any(l => l.Contains("You're staying the night at Sam's place")) && !staying.Any(l => l.Contains("date \"stay\"")) && !staying.Any(l => l.Contains("on a date with them right now")),
              "...and once they're staying: it's not a night to head home");
        Check(!Romance.Lines(rec, 3, 1, onDate: false, "Sam").Any(l => l.Contains("date \"stay\"")), "...never offered when the code didn't allow it");

        Check(ReplyParser.Parse("I'd love to stay. [[date: stay]]", "Ivy").Extras.Date == "stay" && ReplyParser.ParseStructured("{\"say\":\"Okay.\",\"date\":\"stay\"}", "Ivy")?.Extras.Date == "stay"
              && Prompts.ReplySchema().ToJsonString().Contains("\"stay\""),
              "...the reply carries it, in both ways of answering, and the schema asks for it");
        var snap = new GameSnapshot { NpcActivity = "going home to sleep", OutWithPlayer = true };
        Check(Patience.WhatTheyNeedToDo(snap) == Patience.OutTogether && Patience.WhatTheyNeedToDo(new GameSnapshot { NpcActivity = "going home to sleep" }) == "get home",
              "...out with you, running out of things to say isn't \"get home\": the talk rests, the evening goes on");

        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string Read(string f) => File.ReadAllText(Path.Combine(src, f));
        string follow = Read("ModRuntime.Follow.cs"), romance = Read("ModRuntime.Romance.cs"), home = Read("ModRuntime.Home.cs"), hold = Read(Path.Combine("Game", "NpcHold.cs"));
        Check(follow.Contains("if ((_date?.Id == rec.Id || _stay?.Id == rec.Id) && !storming) { if (_romanceConfirm?.id == rec.Id) _romanceConfirm = null; return; }")
              && follow.Contains("snap.OutWithPlayer = _date?.Id == rec.Id || _stay?.Id == rec.Id;"),
              "...a chat running out on a date or a night at yours doesn't end the date or the follow");
        Check(follow.Contains("f.Id != _date?.Id && f.Id != _stay?.Id) { EndFollow(f, \"they need to get on with their day\")")
              && romance.Contains("else if (_stay?.Id != _date.Id && GameContext.Day() is int d"),
              "...nor does FollowMinutes, nor the new day (a night at yours ends in the morning)");
        Check(romance.Contains("long until = since + Romance.MinutesToMorning(hour, minute, _cfg.StayOverUntil.Value,")
              && romance.Contains("if (_home?.Id == s.Id) LeaveHome(\"it's morning\", fresh: true);") && romance.Contains("brain.RecordScene(s.Id, scene, snap)"),
              "...in the morning they say goodbye and go (a fresh task), and remember the night");
        Check(home.Contains("Followers.FirstOrDefault(f => f.Id == _stay?.Id) ?? Followers.FirstOrDefault(f => LivesWithYou(f.Id));")
              && home.Contains("ComesHomeWithYou(withYou, ctrl, spot, facing)") && home.Contains("h.NextFetch = now + 3f; FetchHome(h); }")
              && home.Contains("now - h.LostAt > (h.Guest ? 45f : 10f)"),
              "...coming in with you, they're at home (the partner you live with too); taken by the game, they're brought back");
        Check(home.Contains("if (h.Guest && _stay?.Id == h.Id && why == \"you went out\"") && home.Contains("StartFollow(h.C, h.Id, h.Name);"),
              "...and going out at night, your guest comes along");
        Check(hold.Contains("if (resume && alive && h.FreshAfter)") && hold.Contains("act.Interrupt(g);") && hold.Contains("if (!now && !h.FreshAfter && CanReturn(h))")
              && follow.Contains("NpcHold.ReleaseMember(f.C, fresh);") && follow.Contains("EndFollow(f, why, quiet: true, fresh: false);"),
              "...let go after a long hold, they start a fresh task (the game's own pick), not the one hours old; not on a load or reset");
        // The review's findings (F85-1..7, F86-1..3), each kept fixed.
        Check(romance.Contains("if (now < 0 || GameContext.Hour() < 0) return; // review F85-1/1b") && romance.Contains("&& Romance.StayHour(GameContext.Hour()) && GameNow() >= 0")
              && romance.Contains("else if (now < s.Since - 60) MorningAfter(s);"),
              "review F85-1: the clock unread (a door, a load, a sleep) is never the morning; the clock going back is measured from the evening it began");
        Check(Romance.StayChance(30) == 0.15 && Math.Abs(Romance.StayChance(50) - 0.55) < 1e-9 && Math.Abs(Romance.StayChance(100) - 0.95) < 1e-9
              && Romance.WouldStay("npc_ivy", 4, 80) == Romance.WouldStay("npc_ivy", 4, 80) && !Romance.WouldStay("npc_ivy", -1, 100)
              && romance.Contains("Romance.WouldStay(rec.Id, Romance.EveningOf(GameContext.Day(), GameContext.Hour()), Features.Standing(rec.State.Relationship))")
              && Romance.EveningOf(5, 23) == 5 && Romance.EveningOf(6, 1) == 5 && Romance.EveningOf(6, 7) == 6 && Romance.EveningOf(-1, 2) == -1
              && romance.Contains("if (now < 0 || GameContext.Hour() < 0) return;") && follow.Contains("|| !Alive(c)) continue; // review F85-2b"),
              "review F85-6: whether they'd stay is their own, decided before they answer (by the bar, one roll a night)");
        int stays = Enumerable.Range(0, 2000).Count(i => Romance.WouldStay("npc_" + i, 9, 90));
        Check(stays is > 1640 and < 1840, $"...and the rolls follow it (90 → {stays}/2000)");
        Check(home.Contains("Followers.Add(new Follower { C = h.C!, Ghost = h.Ghost, GhostPtr = h.GhostPtr") && home.Contains("Followers.Count < MaxFollowers")
              && home.Contains("_inApartment == 2 && _apartmentEvent == null"),
              "review F85-2/F85-4: your guest comes out with you even when their body stayed behind; they're fetched only while you're in");
        Check(hold.Contains("(h.GhostPtr == IntPtr.Zero || g.Pointer == h.GhostPtr)) act.Interrupt(g);") && follow.Contains("EndFollow(f, \"the game needed them\", fresh: false)"),
              "review F85-3: a fresh task only for their own ghost, and never when the game took them back itself");
        Check(follow.Contains("bool storming = _session != null && _session.Annoyance >= _session.AnnoyanceThreshold;"),
              "review F85-5: someone storming off still ends the date (only a calm end of the talk keeps the evening)");
        Check(hold.Contains("float gap = Mathf.Min(Flat(slot - pos).magnitude, Mathf.Max(0f, dist - stop));") && hold.Contains("md.y = 0f;")
              && Read("ModRuntime.Jealousy.cs").Contains("player: false") && Read("ModRuntime.GoTogether.cs").Contains("player: false")
              && FollowGait.Next(FollowGait.Gait.Walk, 20f, false, catchUp: false) == FollowGait.Gait.Walk && FollowGait.Next(FollowGait.Gait.Walk, 20f, false) == FollowGait.Gait.Run,
              "review F86-1/2/3: a follower to one side stands once at their spot; a resident following another walks at a person's pace (no sprinting with you)");
        Check(follow.Contains("_stopWhy = \"they stopped, in the conversation\";") && follow.Contains("_stopWhy.Length > 0 ? _stopWhy : \"the conversation ended it\"")
              && !follow.Contains("EndFollow(f, \"you told them to stop\", quiet: true)"),
              "...and the log says why a follow stopped (no more \"you told them to stop\" for their own choice)");
    }
}
