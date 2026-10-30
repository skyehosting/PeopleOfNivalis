using System;

namespace NivalisAIChat.Mind;

/// <summary>
/// FB-78 (the author: "everyones post displays as \"now\" in nightline"): the game's GameplayGameDay doesn't turn at
/// midnight (after 23:18 on day 15, 2:02 AM was still day 15), so the mod's clock went back almost a day and every later
/// line looked newer than now. The day is counted from midnight instead: the game's total time less the time into the day
/// steps by exactly a day at midnight. It's learned against the gameplay day from 08:00 to midnight (when the two agree:
/// the game's own day turns after midnight), or the moment the game turns its day; at night before that, a line-up kept
/// from before is used if it gives the gameplay day or the next. Trusted only while it behaves (three odd readings in a
/// row and it isn't); an odd reading keeps the last day. Otherwise the gameplay day, as before. No Unity, tested.
/// </summary>
public sealed class DayCount
{
    private const long Day = 86400;
    private long _base = long.MinValue, _lastMark = long.MinValue;
    private int _lastGameplay = -1, _lastDay = -1, _odd;
    private long _lastOdd = long.MinValue;

    /// <summary>The game's numbers didn't behave like a clock: the gameplay day (until the next load or new game).</summary>
    public bool Untrusted { get; private set; }

    /// <summary>Counting from midnight (learned, and trusted).</summary>
    public bool Learned => _base != long.MinValue && !Untrusted;

    /// <summary>What was learned (to keep for a night start), or null.</summary>
    public long? Base => Learned ? _base : null;

    /// <summary>A line-up kept from before: used at night until this session learns its own.</summary>
    public long? Remembered { get; set; }

    /// <summary>The day to use: counted from midnight once learned; the gameplay day otherwise.</summary>
    public int Read(int gameplayDay, long totalSeconds, int daySeconds, int hour)
    {
        if (gameplayDay < 0 || Untrusted) return gameplayDay;
        if (totalSeconds < 0 || daySeconds < 0 || daySeconds >= Day || daySeconds / 3600 != hour) return Odd(gameplayDay, totalSeconds);
        long mark = totalSeconds - daySeconds; // the same all day; a whole day more after midnight
        if (_lastMark != long.MinValue && mark != _lastMark)
        {
            long off = Math.Abs(mark - _lastMark) % Day;
            if (off > 2 && off < Day - 2) return Odd(gameplayDay, totalSeconds); // not whole days: not a day count (this time)
        }
        _odd = 0; _lastOdd = long.MinValue;
        long midnights = (long)Math.Round(mark / (double)Day);
        bool turned = _lastGameplay >= 0 && gameplayDay == _lastGameplay + 1 && mark == _lastMark; // the game's day just turned
        _lastMark = mark;
        _lastGameplay = gameplayDay;
        if (hour >= 8 || turned) _base = gameplayDay - midnights; // from 08:00 to midnight, or as it turns: the same day
        int day = gameplayDay;
        if (_base != long.MinValue) day = (int)(midnights + _base);
        else if (Remembered is long kept && midnights + kept is var d && (d == gameplayDay || d == gameplayDay + 1)) day = (int)d;
        return _lastDay = day;
    }

    /// <summary>A reading that doesn't fit: the last day stands; three different ones in a row and the count isn't trusted
    /// (the day is read many times a frame: the same reading counts once).</summary>
    private int Odd(int gameplayDay, long totalSeconds)
    {
        if (totalSeconds != _lastOdd) { _lastOdd = totalSeconds; _odd++; }
        if (_odd >= 3) { Untrusted = true; return gameplayDay; }
        return _lastDay >= 0 ? _lastDay : gameplayDay;
    }

    /// <summary>A save is being loaded, or a new game started: learned again from its own clock.</summary>
    public void Forget(bool newGame = false) { _base = long.MinValue; _lastMark = long.MinValue; _lastGameplay = -1; _lastDay = -1; _odd = 0; _lastOdd = long.MinValue; Untrusted = false; if (newGame) Remembered = null; } // review DAY-R1: a new game has its own clock
}
