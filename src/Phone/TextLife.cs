using System;

namespace NivalisAIChat.Phone;

/// <summary>A Random that may be used from any thread (review PH-4: System.Random isn't).</summary>
public sealed class LockedRandom : Random
{
    private readonly object _lock = new();
    public LockedRandom() { }
    public LockedRandom(int seed) : base(seed) { }
    public override int Next() { lock (_lock) return base.Next(); }
    public override int Next(int maxValue) { lock (_lock) return base.Next(maxValue); }
    public override int Next(int minValue, int maxValue) { lock (_lock) return base.Next(minValue, maxValue); }
    public override double NextDouble() { lock (_lock) return base.NextDouble(); }
    public override long NextInt64(long maxValue) { lock (_lock) return base.NextInt64(maxValue); }
    protected override double Sample() { lock (_lock) return base.Sample(); }
}

/// <summary>What they do with your text (FB-70): answer, see it and answer later (or not at all), or never see it.</summary>
public enum ReplyOutcome { Answers, AnswersLater, NeverSees }

/// <summary>The settings: TextsRealLife, TextNeverSeesChance, TextAnswersLaterChance (percent, before how they feel about you).</summary>
public sealed class ReplyOdds
{
    public bool RealLife = true;
    public int NeverSeesPct = 10, LaterPct = 10;
}

/// <summary>
/// Texting like real life (FB-70, the author: "I want the messaging to feel fully dynamic just like real life"):
/// people don't look at their phone at once (and sleep at night), sometimes never see a text, sometimes see it and answer
/// later in their own time; the better they feel about you the more they answer, the worse the less; once you're texting
/// back and forth they keep answering. A weak signal stalls a send now and then. All in game minutes (the game's clock
/// sets the pace) except the send itself (real seconds). No Unity, tested.
/// </summary>
public static class TextLife
{
    /// <summary>How much more (or less) likely than the settings they are to not answer: 0.1 at 100%, 1 at 50%, 3 at 0%.</summary>
    public static double Factor(double bond, bool partner, bool momentum)
    {
        double b = double.IsFinite(bond) ? Math.Clamp(bond, 0, 100) : 50;
        double f = b >= 50 ? 1 - 0.9 * (b - 50) / 50 : 1 + 2 * (50 - b) / 50;
        if (partner) f *= 0.4;
        if (momentum) f *= 0.25; // once they're texting with you, they usually keep at it
        return f;
    }

    /// <summary>The two chances (percent) for this person now.</summary>
    public static (double never, double later) Chances(ReplyOdds odds, double bond, bool partner, bool momentum, bool textedAgain = false)
    {
        if (!odds.RealLife) return (0, 0);
        double f = Factor(bond, partner, momentum);
        double never = Math.Clamp(odds.NeverSeesPct, 0, 100) * f * (textedAgain ? 0.5 : 1); // another text gets more notice
        double later = Math.Clamp(odds.LaterPct, 0, 100) * f;
        double sum = never + later;
        if (sum > 90) { never *= 90 / sum; later *= 90 / sum; }
        return (never, later);
    }

    public static ReplyOutcome Decide(Random rng, ReplyOdds odds, double bond, bool partner, bool momentum, bool textedAgain = false)
    {
        var (never, later) = Chances(odds, bond, partner, momentum, textedAgain);
        double roll = rng.NextDouble() * 100;
        return roll < never ? ReplyOutcome.NeverSees : roll < never + later ? ReplyOutcome.AnswersLater : ReplyOutcome.Answers;
    }

    /// <summary>Asleep: from 1 in the morning until 7 (they see it when they wake).</summary>
    public static bool Asleep(int hour) => hour >= 1 && hour < 7;

    /// <summary>
    /// Game minutes until they look at their phone. Mid-conversation: a minute or two. Otherwise often within a few
    /// minutes, sometimes half an hour, now and then a couple of hours. Asleep: when they wake (7 to 8:30).
    /// </summary>
    public static int SeenAfter(Random rng, int hour, int minute, bool momentum, bool realLife = true)
    {
        if (!realLife) return 0;
        int now = Math.Clamp(hour, 0, 23) * 60 + Math.Clamp(minute, 0, 59);
        if (Asleep(hour)) return 7 * 60 - now + rng.Next(0, 91);
        if (momentum) return rng.Next(0, 3);
        double r = rng.NextDouble();
        int after = r < 0.45 ? rng.Next(1, 6) : r < 0.80 ? rng.Next(5, 31) : rng.Next(30, 121);
        // A look that would fall in the night (1 to 7 AM) waits until they wake.
        int at = now + after, t = at % 1440;
        if (Asleep(t / 60)) at += 7 * 60 - t + rng.Next(0, 91);
        return Math.Max(0, at - now);
    }

    /// <summary>Seen, answered later: in their own time, half an hour to four hours on (the game's minutes).</summary>
    public static int LaterAfter(Random rng) => rng.Next(30, 241);

    /// <summary>
    /// FB-78 (the author: "It shouldn't show typing until after seen, and then a random delay between 1 and 10 minutes in
    /// game"): game minutes from seeing your texts to starting to type, for someone who answers right away. 0 without real life.
    /// </summary>
    public static int AnswerAfter(Random rng, bool realLife = true) => realLife ? rng.Next(1, 11) : 0;

    /// <summary>Seen and not answered: whether they come back to it on their own (more likely the better they feel about you).</summary>
    public static bool ComesBack(Random rng, double bond)
    {
        double b = double.IsFinite(bond) ? Math.Clamp(bond, 0, 100) : 50;
        return rng.NextDouble() < 0.45 + 0.5 * b / 100;
    }

    /// <summary>A weak signal now and then holds a text for a while before it goes (real seconds; 0 for none).</summary>
    public static float Stall(Random rng, int bars) => bars switch
    {
        1 => rng.NextDouble() < 0.35 ? (float)(20 + rng.NextDouble() * 60) : 0f,
        2 => rng.NextDouble() < 0.10 ? (float)(5 + rng.NextDouble() * 15) : 0f,
        _ => 0f,
    };

    /// <summary>Now and then the dots show, stop, and come back later (they started to answer and put the phone down).</summary>
    public static bool FalseStart(Random rng, bool momentum) => rng.NextDouble() < (momentum ? 0.04 : 0.08);

    /// <summary>
    /// What they're told about the moment they answer: that they didn't look for a while, or saw it and didn't answer,
    /// in their own terms ("" when it's just an answer).
    /// </summary>
    public static string Note(ReplyOutcome plan, long sentAt, long seenAt, long now, bool textedAgain, Func<long, string> clock)
    {
        if (plan == ReplyOutcome.AnswersLater && seenAt >= 0)
            return textedAgain
                ? $"You saw their earlier texts at {clock(seenAt)} and didn't answer then (you were busy, or didn't feel like it). Now they've texted again. Answer as you would: you might say sorry, explain, or not."
                : $"You saw their texts at {clock(seenAt)} and didn't answer then (you were busy, or didn't feel like it). It's {clock(now)} now: you're answering in your own time, as you would (you might say sorry, explain, or not).";
        if (sentAt >= 0 && now - sentAt >= 90)
            return $"You hadn't looked at your phone since they texted at {clock(sentAt)}; you're seeing it now ({clock(now)}).";
        return "";
    }
}

/// <summary>
/// The phone's battery (the author: "at 8am, it would be full, then by 10pm it could be at somewhere like 30% ... by 7:59
/// the phone is at 1% - then immediately charged again"): never dies. No Unity, tested.
/// </summary>
public static class Battery
{
    public static int Percent(int hour, int minute)
    {
        int now = Math.Clamp(hour, 0, 23) * 60 + Math.Clamp(minute, 0, 59);
        int since8 = (now - 8 * 60 + 1440) % 1440;
        if (since8 <= 14 * 60) return (int)Math.Round(100 - 70.0 * since8 / (14 * 60)); // 8 AM 100% to 10 PM 30%
        return Math.Max(1, (int)Math.Round(30 - 29.0 * (since8 - 14 * 60) / (10 * 60 - 1))); // to 1% at 7:59
    }
}
