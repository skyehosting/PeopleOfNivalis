using System;

namespace NivalisAIChat.Mind;

/// <summary>
/// The inner-life round: game time as one number, minutes since day 0 (what everyone's feelings and memories fade by),
/// and how long ago something was, the way a person says it. A save keeps its own clock, and its feelings are kept with
/// it (minds.json, witness.json are per save), so a load never meets a clock from another save. No Unity.
/// </summary>
public static class GameClock
{
    public const int PerDay = 1440;

    /// <summary>Minutes since day 0; -1 when the day can't be read.</summary>
    public static long At(int day, int hour, int minute) =>
        day < 0 ? -1 : (long)day * PerDay + Math.Clamp(hour, 0, 23) * 60 + Math.Clamp(minute, 0, 59);

    public static long Of(Game.GameSnapshot s) => At(s.GameDay, s.Hour, s.Minute);

    public static int DayOf(long at) => at < 0 ? -1 : (int)(at / PerDay);

    public static int HourOf(long at) => at < 0 ? -1 : (int)(at % PerDay / 60);

    /// <summary>"a little while ago", "this morning", "earlier this evening", "last night", "yesterday", "3 days ago".</summary>
    public static string Ago(long then, long now)
    {
        if (then < 0 || now < 0) return "a while back";
        long minutes = Math.Max(0, now - then);
        int days = DayOf(now) - DayOf(then);
        int hour = HourOf(then);
        if (minutes < 60) return "a little while ago";
        if (days <= 0)
            return hour < 5 ? "in the small hours" : hour < 12 ? "this morning" : hour < 18 ? "this afternoon" : "earlier this evening";
        if (days == 1) return hour >= 21 || hour < 5 ? "last night" : hour < 12 ? "yesterday morning" : hour < 18 ? "yesterday afternoon" : "yesterday evening";
        return days < 7 ? $"{days} days ago" : days < 14 ? "last week" : "a few weeks back";
    }
}
