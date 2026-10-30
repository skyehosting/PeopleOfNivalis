using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NivalisAIChat.Npc;

/// <summary>
/// How someone feels about the player, or two residents about each other, on a 0–100% bar (the author's
/// relationship bar, 2026-10-12):
/// - 50% is neutral, and everyone starts there.
/// - 0% means they can't stand them; 100% means they adore them.
/// - It moves slowly, a fraction of a point per reply. The amounts and the milestones are settings.
/// The older −100..100 "affinity" is kept in step with it, so everything that reads that still means the same.
/// No Unity here: it's tested.
/// </summary>
public static class Relationships
{
    public const double Neutral = 50;
    /// <summary>The pill's cells, red on the left to green on the right.</summary>
    public const int Cells = 10;

    /// <summary>The bar's value for an older −100..100 affinity.</summary>
    public static double FromAffinity(int affinity) => Math.Clamp(Neutral + affinity / 2.0, 0, 100);

    /// <summary>The older −100..100 affinity for a bar value (what the rest of the mod reads).</summary>
    public static int ToAffinity(double relationship) =>
        double.IsFinite(relationship) ? (int)Math.Round((Math.Clamp(relationship, 0, 100) - Neutral) * 2, MidpointRounding.AwayFromZero) : 0;

    /// <summary>
    /// How far one reply moves the bar, from the AI's −10..10 rating of it:
    /// - +1..+10 is a tenth of <paramref name="gainMax"/> per point;
    /// - 0 moves nothing;
    /// - −1..−10 runs from <paramref name="lossMin"/> to <paramref name="lossMax"/>.
    /// The amounts are settings (0.5, 0.1 and 0.5 by default): it takes time to really get to know someone.
    /// </summary>
    public static double Step(int rating, double gainMax, double lossMin, double lossMax)
    {
        int r = Math.Clamp(rating, -10, 10);
        // A setting that isn't a number ("NaN") falls back to the default, never to 0% (review of 919404e).
        if (!double.IsFinite(gainMax)) gainMax = 0.5;
        if (!double.IsFinite(lossMin)) lossMin = 0.1;
        if (!double.IsFinite(lossMax)) lossMax = 0.5;
        gainMax = Math.Clamp(gainMax, 0, 5);
        lossMax = Math.Clamp(lossMax, 0, 5);
        lossMin = Math.Clamp(lossMin, 0, lossMax);
        if (r > 0) return gainMax * r / 10.0;
        if (r < 0) return -(lossMin + (lossMax - lossMin) * (-r - 1) / 9.0);
        return 0;
    }

    /// <summary>
    /// A bar value moved by <paramref name="points"/>, kept on the bar and to four decimals (two swallowed a tiny amount
    /// from the settings). A change that isn't a number moves nothing.
    /// </summary>
    public static double Moved(double relationship, double points) =>
        double.IsFinite(points) ? Math.Round(Math.Clamp(relationship + points, 0, 100), Decimals) : relationship;

    /// <summary>How finely the bar is kept.</summary>
    public const int Decimals = 4;

    /// <summary>Cell <paramref name="i"/> (0–9) of the pill: one colour each, red on the left, white in the middle, green on the right.</summary>
    public static (byte r, byte g, byte b) CellColour(int i)
    {
        double t = Math.Clamp(i, 0, Cells - 1) / (double)(Cells - 1);
        (double r, double g, double b) red = (196, 62, 58), white = (236, 232, 222), green = (74, 178, 98);
        var (from, to, u) = t < 0.5 ? (red, white, t / 0.5) : (white, green, (t - 0.5) / 0.5);
        byte Mix(double a, double b) => (byte)Math.Round(a + (b - a) * u);
        return (Mix(from.r, to.r), Mix(from.g, to.g), Mix(from.b, to.b));
    }

    /// <summary>Where the pill's marker sits across the cells: 0 at the far left (0%), 1 at the far right (100%).</summary>
    public static double MarkerAt(double relationship) => Math.Clamp(relationship, 0, 100) / 100.0;

    /// <summary>
    /// What floats up over the bar when it moves (FB-15): "+0.35", "-0.1"; null for no real change (under half a hundredth,
    /// or not a number). Points on the 0–100% bar, at most two decimals, the same whatever the Windows language.
    /// </summary>
    public static string? ChangeLabel(double delta)
    {
        if (!double.IsFinite(delta) || Math.Abs(delta) < 0.005) return null;
        string n = Math.Abs(delta).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return n == "0" ? null : (delta > 0 ? "+" : "-") + n;
    }

    /// <summary>
    /// How strongly the mood rolled at a meeting leans warm (positive) or sour (negative), from the older affinity. It's
    /// at full strength from ±30, as before, and grows gradually from neutral below that, so a slight dislike already shows.
    /// </summary>
    public static double MoodLean(int affinity) => Math.Clamp(affinity / 30.0, -1, 1) * 3;
}

/// <summary>
/// Reads a relationship from a file: a number, or a number written as text. Anything else (null, a blank, garbage, NaN)
/// counts as "not set", so the standing comes from the older Affinity. The forgiving reader used to make it 0, which is
/// "can't stand you" (review of 919404e). A real 0 stays 0.
/// </summary>
public sealed class RelationshipJson : JsonConverter<double>
{
    public override bool HandleNull => true;

    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetDouble(out double d) && double.IsFinite(d) ? d : -1;
            case JsonTokenType.String:
                return double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && double.IsFinite(s) ? s : -1;
            default:
                reader.Skip(); // null, true/false, an object or a list
                return -1;
        }
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

/// <summary>The points on the bar where people start doing things (settings; the author's defaults).</summary>
public sealed class Milestones
{
    /// <summary>They wave when you pass: "+20", a new friend.</summary>
    public double WaveAt = 60;
    /// <summary>They wave and call you over.</summary>
    public double WaveOverAt = 70;
    /// <summary>They count you as a friend (after three meetings).</summary>
    public double FriendAt = 70;
    /// <summary>They snub you when you pass.</summary>
    public double SnubAt = 37.5;
}
