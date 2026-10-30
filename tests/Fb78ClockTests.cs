using System;
using System.IO;
using System.Runtime.CompilerServices;
using NivalisAIChat.Mind;

namespace NivalisAIChat.Tests;

/// <summary>FB-78: the mod's day turns at midnight, so its clock never goes back (every Nightline line said "now").</summary>
internal static partial class Program
{
    private static void Fb78ClockTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- FB-78: the day counted from midnight");
        const long m = 14 * 86400 + 5000; // the game's total time at day 15's midnight (any offset)
        var d = new DayCount();
        Check(d.Read(15, m + 12 * 3600, 12 * 3600, 12) == 15 && d.Learned, "learned at midday, when the gameplay day is the calendar day");
        Check(d.Read(15, m + 23 * 3600 + 18 * 60, 23 * 3600 + 18 * 60, 23) == 15, "...23:18 is still day 15");
        Check(d.Read(15, m + 86400 + 2 * 3600 + 120, 2 * 3600 + 120, 2) == 16,
              "FB-78: at 2:02 AM, while the game still says day 15, it's day 16: the clock never goes back (the case in the author's save)");
        Check(d.Read(16, m + 86400 + 7 * 3600, 7 * 3600, 7) == 16 && d.Read(16, m + 86400 + 12 * 3600, 12 * 3600, 12) == 16,
              "...and when the game turns its day too, the two agree");
        Check(d.Read(16, m + 86400 + 12 * 3600 + 500, 12 * 3600, 12) == 16 && !d.Untrusted && d.Read(16, m + 86400 + 3 * 3600, 3 * 3600, 3) == 16,
              "review DAY-2: one odd reading keeps the last day (no flicker), and the count goes on");
        for (int i = 0; i < 20; i++) d.Read(16, m + 86400 + 12 * 3600 + 500, 12 * 3600, 12);
        Check(!d.Untrusted, "...the same odd reading many times in a frame counts once");
        d.Read(16, m + 86400 + 12 * 3600 + 500, 12 * 3600, 12); d.Read(16, m + 86400 + 12 * 3600 + 900, 12 * 3600, 12); d.Read(16, m + 86400 + 12 * 3600 + 1300, 12 * 3600, 12);
        Check(d.Untrusted && d.Read(16, m + 2 * 86400 + 3600, 3600, 1) == 16, "three in a row that don't move like a clock: not trusted, the gameplay day from then on");
        d.Forget();
        Check(!d.Untrusted && d.Read(16, m + 86400 + 12 * 3600, 12 * 3600, 12) == 16 && d.Learned, "review PH-1: a load or a new game trusts the count again");
        var night = new DayCount();
        Check(night.Read(15, m + 86400 + 3600, 3600, 1) == 15 && !night.Learned, "started at night with nothing kept: the gameplay day until it's learned");
        Check(night.Read(15, m + 12 * 3600, 7 * 3600, 12) == 15 && !night.Learned, "...and a time into the day that isn't the clock's hour is never used");
        var late = new DayCount();
        Check(late.Read(15, m + 22 * 3600, 22 * 3600, 22) == 15 && late.Learned && late.Read(15, m + 86400 + 1800, 1800, 0) == 16,
              "review DAY-1: started at 22:00, it's learned at once (the game's day turns after midnight), so 00:30 is the next day");
        var turn = new DayCount();
        turn.Read(15, m + 86400 + 3600, 3600, 1);
        Check(turn.Read(16, m + 86400 + 6 * 3600, 6 * 3600, 6) == 16 && turn.Learned, "...or the moment the game turns its day in the night");
        var kept = new DayCount { Remembered = 15 - (long)Math.Round(m / 86400.0) };
        Check(kept.Read(15, m + 86400 + 2 * 3600, 2 * 3600, 2) == 16 && !kept.Learned, "review PH-2: a line-up kept from before counts a night start from midnight at once");
        var wrong = new DayCount { Remembered = 15 - (long)Math.Round(m / 86400.0) + 5 };
        Check(wrong.Read(15, m + 86400 + 2 * 3600, 2 * 3600, 2) == 15, "...but only when it gives the game's day or the next");
        kept.Forget(newGame: true);
        Check(kept.Remembered == null && kept.Read(1, 3600, 3600, 1) == 1, "review DAY-R1: a new game keeps nothing from before (its first night is day 1, as the new-game check needs)");
        var loaded = new DayCount { Remembered = 7 };
        loaded.Forget();
        Check(loaded.Remembered == 7, "...while a load keeps it (still only used when it gives the game's day or the next)");
        var load = new DayCount();
        load.Read(15, m + 12 * 3600, 12 * 3600, 12);
        load.Forget();
        Check(load.Read(12, m - 3 * 86400 + 2 * 3600, 2 * 3600, 2) == 12, "a save loaded: learned again from its own daytime (an earlier day stays earlier)");

        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string ctx = File.ReadAllText(Path.Combine(src, "Game", "GameContext.cs"));
        string rt = File.ReadAllText(Path.Combine(src, "ModRuntime.Saves.cs"));
        Check(rt.Contains("GameContext.ForgetDayCount(newGame: true);") && rt.Contains("\"clock.txt\"") && ctx.Contains("DayLearned?.Invoke(learned)"),
              "a new game forgets it too; what was learned is kept in clock.txt for a night start");
        Check(ctx.Contains("Days.Read(gameplay, TimeOfDayManager.TotalGameSeconds, TimeOfDayManager.TotalDaySeconds, TimeOfDayManager.ClockHour)")
              && rt.Contains("GameContext.ForgetDayCount();"), "every reading of the day goes through it, and a load forgets what was learned");
    }
}
