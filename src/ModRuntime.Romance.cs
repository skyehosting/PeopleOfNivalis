using System;
using System.Linq;
using System.Text.RegularExpressions;
using Nivalis;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using NivalisAIChat.UI;
using UnityEngine;

namespace NivalisAIChat;

/// <summary>
/// FB-63 (the author: "Have a way to confirm the relationship via text ... the smiley face changes into a pink heart";
/// "One date a day"; "if under the middle threshold, they will often say no"): being together with someone.
///
/// The code decides (Npc/Romance.cs: whether they're open to it today, a date now, moving in) and tells them in
/// [WHAT YOU'D DO]; their reply records what they said (romance, date, moveIn). A yes from them is made official by
/// your typed yes; a date has them come along with you (FB-62's following) until they call it a night or the follow ends.
/// </summary>
public static partial class ModRuntime
{
    /// <summary>FB-63: who's together (per save): residents' couples, your partners, who was seen flirting.</summary>
    private static CoupleBook? _couples;

    /// <summary>A yes from them waiting for yours: who, and to what (Romance.Dating or Romance.Living).</summary>
    private static (string id, string kind)? _romanceConfirm;

    private sealed class DateOut
    {
        public string Id = "", Name = "";
        public int Day;
        public float Since;
    }

    /// <summary>The date going on now (one at a time), or null.</summary>
    private static DateOut? _date;

    /// <summary>
    /// FB-85 (the author: a tester "was still having trouble when it came to dates, having an npc stay over her place"):
    /// someone you're seeing staying the night at your place: who, and the game minute they head off in the morning.
    /// While it lasts nothing cuts it short: not FollowMinutes, not a new day, not running out of things to say; at home
    /// they're kept there (brought back if the game takes them), and you going out takes them along.
    /// </summary>
    private sealed class StayNight
    {
        public string Id = "", Name = "";
        public long Since, Until;
    }

    private static StayNight? _stay;

    /// <summary>A stay over is on the table: someone you're seeing (not yet living with you), from 8 in the evening, able to come along.</summary>
    private static bool CanStayOver(Character c, NpcRecord rec) =>
        Features.Romance && Features.Following && _cfg.StayOver.Value && rec.State.Romance == Romance.Dating && _stay == null
        && Romance.StayHour(GameContext.Hour()) && GameNow() >= 0
        && Romance.WouldStay(rec.Id, Romance.EveningOf(GameContext.Day(), GameContext.Hour()), Features.Standing(rec.State.Relationship)) // review F85-6/6b: their own willingness tonight (the same roll either side of midnight)
        && (_date?.Id == rec.Id || Followers.Any(f => Same(f.C, c)) || WouldFollowForDate(c));

    // The date's seat: the chair borrowed for them, and where you sat when they sat down.
    private static Seats.Seat? _dateSeat;
    private static Vector3 _stillPos, _seatedFrom;
    private static float _stillSince, _nextSeatLook, _seatGiveUpUntil;
    private static bool _dateStanding;

    private static readonly Regex TypedYes = new(@"^\s*(yes|yeah|yea|yep|yup|sure|ok|okay|of course|absolutely|definitely|i do|i'd love (that|to))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static System.Collections.Generic.List<string> RomanceLines(NpcRecord rec, GameSnapshot snap)
    {
        var lines = Romance.Lines(rec, GameContext.Day(), Math.Max(0, _cfg.DatesPerDay.Value), onDate: _date?.Id == rec.Id, snap.PlayerName,
            stayingOver: _stay?.Id == rec.Id, canStay: _npc != null && CanStayOver(_npc, rec)); // FB-85
        // Review F5: a date needs them free to come along now.
        if (_npc != null && !Followers.Any(f => Same(f.C, _npc)) && !WouldFollowForDate(_npc))
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].StartsWith("If they ask you on a date now, you'd go", StringComparison.Ordinal))
                    lines[i] = "If they ask you on a date: you can't right now (you're busy); say so your way.";
        if (HomeLine(rec) is { } home) lines.Insert(0, home);
        return lines;
    }

    /// <summary>The reply's romance, date and moveIn: what the code allowed happens, the rest is only words.</summary>
    private static void NoteRomance(Character c, NpcRecord rec, ReplyExtras extras)
    {
        if (!Features.Romance) return;
        int day = GameContext.Day();
        var st = rec.State;
        string name = SpeakerName(rec);
        double bar = Features.Standing(st.Relationship);

        if (extras.Romance is "yes" or "asks" && st.Romance.Length == 0)
        {
            if (Romance.OpenToday(rec.Id, day, bar))
            {
                NoteYourFlirting(rec, day);
                _romanceConfirm = (rec.Id, Romance.Dating);
                Notice(extras.Romance == "asks"
                    ? $"{name} is asking you out. Type yes to be together (or keep talking)."
                    : $"{name} said yes. Type yes to make it official (or keep talking).");
            }
            else Log.Info($"Romance: {rec.Persona.Name} said {extras.Romance}, but they weren't open to it today; only words.");
        }
        else if (extras.Romance == "end" && st.Romance.Length > 0)
        {
            SetRomance(rec, s => { s.Romance = ""; s.RomanceSinceDay = -1; s.RomanceEndedDay = day; }); // CF-20: a real change (firm profiles)
            _romanceConfirm = null;
            if (_date?.Id == rec.Id) EndDate("they ended things", quiet: true);
            if (_stay?.Id == rec.Id) EndStayOver("they ended things");
            if (Features.Following && (Followers.Any(f => Same(f.C, c)) || _followAfterChat?.c == c)) { _stopAfterChat = true; _stopWhy = "they ended things"; _followAfterChat = null; }
            Notice($"{name} ended things with you.");
            Ui.SetTogether(false);
            Log.Info($"Romance: {rec.Persona.Name} ended it.");
            Stays(rec.Id, rec.Persona.Name, rec.Persona, "you:love", "you ended things with the newcomer", -0.4f, 0.1f, 48, "sad"); // the inner-life round
        }

        if (extras.Date == "yes" && _date == null && Romance.CanDate(st, day, Math.Max(0, _cfg.DatesPerDay.Value)))
        {
            if (Followers.Any(f => Same(f.C, c)) || WouldFollowForDate(c)) StartDate(c, rec, day);
            else Log.Info($"Romance: {rec.Persona.Name} said yes to a date, but they can't go now (at work, or enough people with you).");
        }
        else if (extras.Date == "yes" && _date == null && st.Romance.Length > 0 && _cfg.DatesPerDay.Value > 0 && day >= 0)
        {
            // CF-20 (a player: "It doesn't trigger the "date" but partner agrees"): DatesPerDay used up; said, not silent.
            Notice($"You've already had a date today: {name} would love another, tomorrow (DatesPerDay).");
            Log.Info($"Romance: {rec.Persona.Name} said yes to another date today, but DatesPerDay ({_cfg.DatesPerDay.Value}) is used up.");
        }
        else if (extras.Date == "over" && _date?.Id == rec.Id)
        {
            EndDate("they called it a night", quiet: true);
            if (_stay?.Id == rec.Id) EndStayOver("they called it a night");
            if (Features.Following && (Followers.Any(f => Same(f.C, c)) || _followAfterChat?.c == c)) { _stopAfterChat = true; _stopWhy = "the date ended"; _followAfterChat = null; }
            Notice($"The date with {name} is over.");
        }
        else if (extras.Date == "stay")
        {
            if (CanStayOver(c, rec)) StartStayOver(c, rec, day);
            else Log.Info($"Stay over: {rec.Persona.Name} said they'd stay the night, but it can't be now (not seeing you, too early, can't come along, or StayOver off); only words.");
        }

        if (extras.MoveIn == "yes" && Romance.CanMoveIn(st, day, bar))
        {
            _romanceConfirm = (rec.Id, Romance.Living);
            Notice($"{name} would move in with you. Type yes to live together (or keep talking).");
        }
    }

    /// <summary>
    /// Send: your typed yes to their yes makes it official (their words still go to them). Anything else lets the moment
    /// pass. Returns whether it confirmed something.
    /// </summary>
    private static bool ConfirmRomanceFrom(string text)
    {
        if (_romanceConfirm is not { } wait) return false;
        _romanceConfirm = null;
        var rec = _session?.Record;
        if (rec == null || rec.Id != wait.id || !Features.Romance || !TypedYes.IsMatch(text)) return false;
        int day = GameContext.Day();
        string name = SpeakerName(rec);
        if (wait.kind == Romance.Living)
        {
            SetRomance(rec, s => { s.Romance = Romance.Living; });
            Notice($"You and {name} live together now.");
            Log.Info($"Romance: {rec.Persona.Name} moved in with you (day {day}).");
        }
        else
        {
            SetRomance(rec, s => { s.Romance = Romance.Dating; s.RomanceSinceDay = day; });
            Notice($"You and {name} are together now.");
            Log.Info($"Romance: together with {rec.Persona.Name} (day {day}).");
            // The inner-life round: it stays with them for days, and anyone near sees the two of you get together.
            Stays(rec.Id, rec.Persona.Name, rec.Persona, "you:love", "you and the newcomer are together now", 0.5f, 0.25f, 72, "hopeful");
            if (_npc != null) Witness(Mind.Happenings.PlayerCouple, _npc.transform.position, Mind.Happenings.Player, "", rec.Id, rec.Persona.Name, _session?.Snapshot.Location ?? "", 0.7f);
        }
        Ui.SetTogether(true);
        return true;
    }

    private static void StartDate(Character c, NpcRecord rec, int day)
    {
        var st = rec.State;
        int today = st.LastDateDay == day ? st.DatesToday + 1 : 1;
        int had = st.DatesHad + 1;
        SetRomance(rec, s => { s.LastDateDay = day; s.DatesToday = today; s.DatesHad = had; });
        _date = new DateOut { Id = rec.Id, Name = SpeakerName(rec), Day = day, Since = Time.unscaledTime };
        bool following = Followers.Any(f => Same(f.C, c));
        if (Features.Following && !following && WouldFollowForDate(c))
        {
            _followAfterChat = (c, rec.Id, SpeakerName(rec));
            _stopAfterChat = false;
            Notice($"{SpeakerName(rec)} is up for a date: they'll come with you when you're done talking. Take them somewhere to eat.");
        }
        else Notice($"{SpeakerName(rec)} is up for a date.");
        Log.Info($"Romance: a date with {rec.Persona.Name} (day {day}, date {had}).");
        Stays(rec.Id, rec.Persona.Name, rec.Persona, "you:date", "out on a date with the newcomer", 0.35f, 0.25f, 10, "excited"); // the inner-life round
        Witness(Mind.Happenings.PlayerDate, c.transform.position, Mind.Happenings.Player, "", rec.Id, rec.Persona.Name, _session?.Snapshot.Location ?? "", 0.6f);
    }

    /// <summary>
    /// FB-85: they're staying the night: the evening is a date (its own, not one of DatesPerDay), they come along with you
    /// (after the chat, if they aren't already), and they head off in the morning (StayOverUntil, give or take an hour).
    /// </summary>
    private static void StartStayOver(Character c, NpcRecord rec, int day)
    {
        string name = SpeakerName(rec);
        if (_date?.Id != rec.Id)
        {
            if (_date != null) EndDate("another date began", quiet: true);
            _date = new DateOut { Id = rec.Id, Name = name, Day = day, Since = Time.unscaledTime };
        }
        int hour = Math.Max(0, GameContext.Hour()), minute = Math.Max(0, GameContext.Minute());
        long since = GameNow(); // review F85-1: CanStayOver made sure the clock can be read
        long until = since + Romance.MinutesToMorning(hour, minute, _cfg.StayOverUntil.Value, UnityEngine.Random.Range(0, 61));
        _stay = new StayNight { Id = rec.Id, Name = name, Since = since, Until = until };
        if (!Followers.Any(f => Same(f.C, c)) && _followAfterChat?.c != c) { _followAfterChat = (c, rec.Id, name); _stopAfterChat = false; }
        Notice($"{name} is coming home with you for the night.");
        Log.Info($"Stay over: {rec.Persona.Name} is staying the night (until about {ClockOf(until)}).");
        Stays(rec.Id, rec.Persona.Name, rec.Persona, "you:stay", "staying the night at the newcomer's place", 0.45f, 0.25f, 14, "happy"); // the inner-life round
    }

    private static void EndStayOver(string why)
    {
        if (_stay == null) return;
        Log.Info($"Stay over: {_stay.Name} isn't staying the night after all ({why}).");
        _stay = null;
    }

    /// <summary>FB-85: the morning after: a goodbye, the date over, and they're on their way (a fresh task for the day).</summary>
    private static void MorningAfter(StayNight s)
    {
        _stay = null;
        var f = Followers.FirstOrDefault(x => x.Id == s.Id);
        Character? c = _home?.Id == s.Id ? _home.C : f?.C;
        if (c != null && Alive(c) && Ui.Built)
            try { Ui.ShowBark(s.Name, "Morning. I should head home and get ready for the day. Last night was really nice.", c.transform, 4f, "stay:" + s.Id); } catch { }
        Log.Info($"Stay over: {s.Name} heads off in the morning.");
        // Kept in their record (no AI asked): they remember the night in the chats and texts that follow.
        try
        {
            var snap = GameContext.Capture(null);
            string scene = $"[Day {snap.GameDay}, {snap.Clock}: you stayed the night at the newcomer's place, and headed off in the morning.]";
            var brain = ActiveBrain;
            if (brain != null) System.Threading.Tasks.Task.Run(() => brain.RecordScene(s.Id, scene, snap)); // review F85-7
        }
        catch (Exception e) { Log.Debug("stay over, memory: " + e.Message); }
        if (_date?.Id == s.Id) EndDate("the night's over", quiet: true);
        if (_home?.Id == s.Id) LeaveHome("it's morning", fresh: true);
        else if (f != null) EndFollow(f, "it's morning", quiet: true);
    }

    /// <summary>Each frame (cheap): the stay over goes on while they're with you or at your place; in the morning they head off.</summary>
    private static void TickStayOver()
    {
        var s = _stay;
        if (s == null) return;
        if (!Features.Romance || !_cfg.StayOver.Value) { _stay = null; return; }
        if (ChatOpen && _session?.Record.Id == s.Id) return;
        bool with = Followers.Any(f => f.Id == s.Id) || _followAfterChat?.id == s.Id || _home?.Id == s.Id;
        if (!with) { EndStayOver("they went their own way"); return; }
        long now = GameNow();
        if (now < 0 || GameContext.Hour() < 0) return; // review F85-1/1b: the clock can't be read (a door, a load, a sleep): not the morning
        if (now >= s.Until) MorningAfter(s);
        else if (now < s.Since - 60) MorningAfter(s); // the clock went back (another save's): over
    }

    /// <summary>On a date they come along even when following wouldn't otherwise be open (at most MaxFollowers, never at work).</summary>
    private static bool WouldFollowForDate(Character c) =>
        Followers.Count < MaxFollowers && !GameContext.AtWork(c);

    private static void EndDate(string why, bool quiet = false)
    {
        if (_date == null) return;
        if (!quiet) try { Ui.ShowToast("Date over", $"{_date.Name}: {why}."); } catch { }
        Log.Info($"Romance: the date with {_date.Name} is over ({why}).");
        _date = null;
    }

    /// <summary>CloseChat: a yes left waiting goes; a date whose partner isn't coming along any more is over.</summary>
    private static void AfterChatRomance(Character? partner)
    {
        _romanceConfirm = null;
        TickRomance();
    }

    /// <summary>Each frame (cheap): the date ends with the follow (told to stop, FollowMinutes, lost), or a new day.</summary>
    private static void TickRomance()
    {
        if (_date == null) { if (_stay != null) TickStayOver(); return; }
        if (!Features.Romance) { _date = null; _stay = null; return; }
        if (ChatOpen && _session?.Record.Id == _date.Id) return;
        TickStayOver(); // FB-85
        if (_date == null) return; // the morning after ended it
        // FB-85: at your place (come home with you) is still with you.
        bool with = Followers.Any(f => f.Id == _date.Id) || _followAfterChat?.id == _date.Id || _home?.Id == _date.Id;
        if (!with) EndDate("they went their own way");
        // FB-78 (review DAY-3): the day turns at midnight now, but a date runs on into the night: it ends on a later day only
        // from 06:00 (as when the game's own day turned), or on an earlier day (another save's clock). FB-85: a night at
        // your place ends in the morning instead (TickStayOver).
        else if (_stay?.Id != _date.Id && GameContext.Day() is int d && d >= 0 && d != _date.Day && (d < _date.Day || GameContext.Hour() >= 6)) EndDate("it's a new day");
        else if (!ChatOpen) TickDateSeat();
    }

    /// <summary>
    /// FB-63 (the author: "sit together, order food, and stay until the end of patience"): you sit down somewhere to eat
    /// (still for a couple of seconds, on a chair's spot), and your date takes a free chair at your table (or the nearest
    /// free one); you get up and walk off, and they get up and come along.
    /// </summary>
    private static void TickDateSeat()
    {
        var f = _date != null ? Followers.FirstOrDefault(x => x.Id == _date.Id) : null;
        if (f == null || f.Paused || !Alive(f.C)) return;
        var player = GameContext.PlayerCharacter();
        if (player == null) return;
        Vector3 pp;
        try { pp = player.transform.position; } catch { return; }
        float now = Time.unscaledTime;
        if (_dateSeat == null)
        {
            if (Flat(pp - _stillPos).magnitude > 0.35f) { _stillPos = pp; _stillSince = now; return; }
            if (now - _stillSince < 2f || now < _nextSeatLook || now < _seatGiveUpUntil) return;
            _nextSeatLook = now + 2.5f;
            if (Flat(f.C.transform.position - pp).magnitude > 8f || NpcHold.SeatBusy(f.C)) return;
            var (mine, theirs) = Seats.Near(pp);
            if (mine == null || theirs == null) return;
            bool marked = Seats.Reserve(theirs, f.Ghost);
            if (!NpcHold.SitAt(f.C, theirs.Pos, theirs.Rot))
            {
                Seats.Free(theirs, f.Ghost);
                _seatGiveUpUntil = now + 20f;
                Log.Info($"Date: {f.Name} couldn't sit down with you (no way to sit here); they stay standing.");
                return;
            }
            _dateSeat = theirs;
            _seatedFrom = pp;
            _dateStanding = false;
            f.Seated = true;
            Log.Info($"Date: {f.Name} sits down with you{(marked ? "" : " (the chair couldn't be marked as taken)")}.");
            return;
        }
        if (NpcHold.SeatFailed(f.C) && !NpcHold.SeatBusy(f.C))
        {
            FreeDateSeat(f);
            f.Seated = false;
            _seatGiveUpUntil = now + 30f;
            Log.Info($"Date: {f.Name} couldn't get to the chair; they stay with you standing.");
            Resume(f);
            return;
        }
        if (!_dateStanding && Flat(pp - _seatedFrom).magnitude > 2.5f)
        {
            _dateStanding = true;
            NpcHold.StandUp(f.C);
        }
        if (_dateStanding && !NpcHold.SeatBusy(f.C))
        {
            FreeDateSeat(f);
            f.Seated = false;
            _dateStanding = false;
            _stillPos = pp;
            _stillSince = now;
            if (!Resume(f)) EndFollow(f, "they couldn't walk on");
        }
    }

    /// <summary>The chair borrowed for the date goes back.</summary>
    private static void FreeDateSeat(Follower? f)
    {
        if (_dateSeat == null) return;
        try { Seats.Free(_dateSeat, f?.Ghost); } catch { }
        _dateSeat = null;
        _dateStanding = false;
    }

    /// <summary>A change to their romance: on the record this chat holds (so the rest of it knows), and saved with it.</summary>
    private static void SetRomance(NpcRecord rec, Action<NpcState> set)
    {
        set(rec.State);
        string id = rec.Id;
        _ = ActiveBrain.Mutate(id, r => { set(r.State); return true; });
        try { _couples?.SetPartner(id, rec.Persona.Name, rec.State.Romance, rec.State.RomanceSinceDay); } catch (Exception e) { Log.Debug("partners: " + e.Message); }
    }

    /// <summary>
    /// FB-63: you're with someone, and someone else said yes to you (or went on a date with you): a secret your partner may
    /// learn. Seen at once if they're near enough to see or hear it; otherwise as gossip, a day or more later (not always).
    /// </summary>
    private static void NoteYourFlirting(NpcRecord with, int day)
    {
        if (_couples == null || !_cfg.Jealousy.Value) return;
        foreach (var p in _couples.Partners().Where(p => p.Id != with.Id))
        {
            bool seen = NearYou(p.Id, 12f);
            var s = _couples.NoteSecret(CoupleBook.Player, "you", with.Id, with.Persona.Name, p.Id, day, seen);
            if (s != null) Log.Info($"Romance: {p.Name} {(seen ? "saw you" : "may hear about you")} flirting with {with.Persona.Name}.");
        }
    }

    /// <summary>Whether someone (by id) has a body within <paramref name="metres"/> of you now.</summary>
    private static bool NearYou(string id, float metres)
    {
        try
        {
            var pc = GameContext.PlayerCharacter();
            if (pc == null) return false;
            var pp = pc.transform.position;
            foreach (var c in Character.instances)
            {
                if (c == null || !c.isActiveAndEnabled) continue;
                if (Flat(c.transform.position - pp).magnitude > metres) continue;
                if (GameContext.NpcId(c) == id) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// FB-63: the chat with your partner opens: what they've found out about you (seen, or heard) and haven't had out with
    /// you yet becomes theirs (a hurt in [WHAT YOU'D DO]), and the bar drops. They decide what to do about it.
    /// </summary>
    private static void FoundOutAboutYou(NpcRecord rec)
    {
        if (_couples == null || !Features.Romance || !_cfg.Jealousy.Value || rec.State.Romance.Length == 0) return;
        int day = GameContext.Day();
        var known = _couples.KnownBy(rec.Id, day).Where(s => s.Cheater == CoupleBook.Player).ToList();
        if (known.Count == 0) return;
        var hurts = known.Select(s => $"{(s.Seen ? "You saw" : "People told you")} they were flirting with {s.WithName} (day {s.Day}).").ToList();
        // Set to a value (not "minus"): the chat's record and its saved copy both get the same (applied twice, it's the same).
        double after = Math.Max(0, rec.State.Relationship - 8 * known.Count);
        SetRomance(rec, st =>
        {
            foreach (var h in hurts) if (!st.Hurts.Contains(h)) st.Hurts.Add(h);
            while (st.Hurts.Count > 10) st.Hurts.RemoveAt(0);
            st.Relationship = after;
        });
        foreach (var s in known) _couples.Settle(s.Id, grudge: true);
        Log.Info($"Romance: {rec.Persona.Name} knows about {known.Count} time(s) you flirted with someone else.");
        Stays(rec.Id, rec.Persona.Name, rec.Persona, "you:cheat", $"found out the newcomer was flirting with {known[0].WithName}", -0.6f, 0.35f, 48, "angry"); // the inner-life round
    }

    private static void Notice(string text)
    {
        try { Ui.AddLine(LineKind.Notice, "", text); } catch { }
    }
}
