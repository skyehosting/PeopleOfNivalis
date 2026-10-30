using System;
using System.Collections.Generic;

namespace NivalisAIChat.Npc;

/// <summary>
/// FB-63 (the author: "even if you don't get to know the npc that well, there's still a chance they will accept, but it
/// would ofcourse be lower than when you have the relationship bar higher, and when under the middle threshold, they will
/// often say no"; "one date a day"): the rules of dating someone, decided here before the AI answers and told to them in
/// [WHAT YOU'D DO], so what they say and what happens agree. Adults only (the chat never opens with anyone else). No Unity.
/// </summary>
public static class Romance
{
    /// <summary>NpcState.Romance: together with you (confirmed by you), and living together.</summary>
    public const string Dating = "dating", Living = "living";

    /// <summary>
    /// How likely they're open to dating you today, by the bar (0-100): 5% at 30 or below, 25% at the middle (50), rising
    /// to 90% at 100.
    /// </summary>
    public static double OpenChance(double bar)
    {
        if (double.IsNaN(bar)) return 0.05;
        if (bar <= 30) return 0.05;
        if (bar <= 50) return 0.05 + (bar - 30) / 20 * 0.20;
        return 0.25 + Math.Min(1, (bar - 50) / 50) * 0.65;
    }

    /// <summary>Whether they're open to it today: one roll a day per person (asking again the same day gets the same answer).</summary>
    public static bool OpenToday(string id, int day, double bar) => Roll(id, day, "open") < OpenChance(bar);

    /// <summary>A date now is possible: together, dates allowed, and fewer than <paramref name="perDay"/> today.</summary>
    public static bool CanDate(NpcState st, int day, int perDay) =>
        st.Romance.Length > 0 && perDay > 0 && day >= 0 && (st.LastDateDay != day || st.DatesToday < perDay);

    /// <summary>Moving in is on the table: together for 3 game days or more and the bar at 75 or more.</summary>
    public static bool CanMoveIn(NpcState st, int day, double bar) =>
        st.Romance == Dating && st.RomanceSinceDay >= 0 && day - st.RomanceSinceDay >= 3 && bar >= 75;

    /// <summary>A stable number in [0, 1) for this person, day and purpose.</summary>
    public static double Roll(string id, int day, string salt)
    {
        ulong h = 1469598103934665603UL;
        foreach (char ch in $"{id}|{day}|{salt}") { h ^= ch; h *= 1099511628211UL; }
        return (h >> 11) / (double)(1UL << 53);
    }

    /// <summary>
    /// [WHAT YOU'D DO]'s romance lines for someone (the code's decisions, in their words). <paramref name="onDate"/>: a date
    /// is going on right now.
    /// </summary>
    /// <summary>
    /// FB-85 (review F85-6): how likely they'd stay the night tonight, by the bar: 15% below the middle, 55% at it, rising to
    /// 95% at the top. Like everything else in romance it's decided before they answer (one roll a night).
    /// </summary>
    public static double StayChance(double bar) =>
        double.IsNaN(bar) ? 0 : bar < 50 ? 0.15 : 0.55 + Math.Min(1, (bar - 50) / 50) * 0.40;

    /// <summary>Review F85-6b: the evening a night belongs to (after midnight, until 6, it's the day before's).</summary>
    public static int EveningOf(int day, int hour) => day >= 0 && hour is >= 0 and < 6 ? day - 1 : day;

    /// <summary>Whether they'd stay tonight (one roll an evening: pass <see cref="EveningOf"/>).</summary>
    public static bool WouldStay(string id, int day, double bar) => day >= 0 && Roll(id, day, "stay") < StayChance(bar);

    /// <summary>FB-85: the hours a stay over can start (from 8 in the evening until 6 in the morning).</summary>
    public static bool StayHour(int hour) => hour >= 20 || hour is >= 0 and < 6;

    /// <summary>
    /// FB-85: game minutes from <paramref name="hour"/>:<paramref name="minute"/> until the morning they head off
    /// (<paramref name="morningHour"/>:00 plus <paramref name="jitter"/> minutes), at least an hour.
    /// </summary>
    public static int MinutesToMorning(int hour, int minute, int morningHour, int jitter)
    {
        int nowMin = hour * 60 + minute, morning = Math.Clamp(morningHour, 5, 12) * 60 + Math.Max(0, jitter);
        int until = ((morning - nowMin) % 1440 + 1440) % 1440;
        return Math.Max(60, until);
    }

    public static List<string> Lines(NpcRecord rec, int day, int datesPerDay, bool onDate, string playerName, bool stayingOver = false, bool canStay = false)
    {
        var lines = new List<string>();
        var st = rec.State;
        double bar = Features.Standing(st.Relationship);
        string them = playerName.Length > 0 ? playerName : "the stranger";
        if (st.Romance == Living)
            lines.Add($"You and {them} are together and live together (since day {st.RomanceSinceDay}). If you ever want to end it, set romance \"end\".");
        else if (st.Romance == Dating)
            lines.Add($"You and {them} are together (since day {st.RomanceSinceDay}; {st.DatesHad} date{(st.DatesHad == 1 ? "" : "s")} so far). " +
                      "If you want to end it, set romance \"end\".");
        else if (OpenToday(rec.Id, day, bar))
            lines.Add("If they ask you out, you're open to it today: say yes only if you're really into them (romance \"yes\"), otherwise " +
                      "no (romance \"no\"). Rarely, if you're really into them, you might ask them out yourself (romance \"asks\").");
        else
            lines.Add("If they ask you out: not today. Turn them down in your own way (romance \"no\").");
        if (st.Romance.Length > 0)
        {
            // FB-85: a late hour isn't a reason to end it (it was: dates ended at bedtime, and the game took them back).
            if (stayingOver) lines.Add($"You're staying the night at {them}'s place (you came home with them). It's not a night to head home: " +
                                       "only if something's really wrong would you leave (then set date \"over\").");
            else if (onDate) lines.Add("You're on a date with them right now. Enjoy it; set date \"over\" only when you really want the evening to end " +
                                       "(it being late isn't a reason on its own).");
            else if (CanDate(st, day, datesPerDay)) lines.Add("If they ask you on a date now, you'd go: set date \"yes\" (they'll take you somewhere to eat).");
            else lines.Add("If they ask you on a date: not again today.");
            if (canStay && !stayingOver)
                lines.Add($"It's late: if they ask you to come home with them and stay the night (or you'd like to and say so), you can: say yes your way and set date \"stay\".");
            if (st.Romance == Dating)
                lines.Add(CanMoveIn(st, day, bar)
                    ? "If they ask you to move in together, you'd say yes (moveIn \"yes\")."
                    : "If they ask you to move in together: it's too soon (moveIn \"no\").");
        }
        foreach (var hurt in st.Hurts.TakeLast(3)) lines.Add($"You found out: {hurt} You haven't forgotten it.");
        return lines;
    }

    private static IEnumerable<string> TakeLast(this List<string> list, int n) => list.GetRange(Math.Max(0, list.Count - n), Math.Min(n, list.Count));
}
