using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NivalisAIChat.Phone;

/// <summary>What the phone's signal is worked out from (read on the main thread, worked out anywhere).</summary>
public sealed class ReceptionInputs
{
    /// <summary>PhoneSignal: off = always full bars.</summary>
    public bool Simulate = true;
    public bool WeatherMatters = true, PlacesMatter = true;
    public bool AtHome;
    /// <summary>Inside somewhere that isn't home (a venue, a shop): walls cost a bar.</summary>
    public bool Indoors;
    /// <summary>PhoneIndoorsCost: bars lost indoors (0-2).</summary>
    public int IndoorsCost = 1;
    /// <summary>The district, the venue and the scene's name: each is matched against the place list.</summary>
    public string Location = "", Venue = "", Scene = "";
    public float Blizzard, Snow, Rain, Thunder, Fog;
    /// <summary>Game minutes (for the slow drift: a bar comes and goes now and then).</summary>
    public long Minute;
    /// <summary>PhoneWeakPlaces, read by <see cref="Reception.ParsePlaces"/>.</summary>
    public IReadOnlyList<(string place, int bars)> Places = Array.Empty<(string, int)>();
    /// <summary>PhoneWeatherCosts, read by <see cref="Reception.ParseCosts"/>: bars each kind of weather takes.</summary>
    public WeatherCosts Costs = new();
}

/// <summary>How many bars each kind of weather takes (0-4 each; they add up, a blizzard counting instead of heavy snow).</summary>
public sealed class WeatherCosts
{
    public int Rain = 1, Thunder = 1, Snow = 1, Blizzard = 2, Fog = 0;

    public static readonly string[] Kinds = { "rain", "thunder", "snow", "blizzard", "fog" };

    public int Get(string kind) => kind switch { "rain" => Rain, "thunder" => Thunder, "snow" => Snow, "blizzard" => Blizzard, "fog" => Fog, _ => 0 };

    public void Set(string kind, int bars)
    {
        bars = Math.Clamp(bars, 0, Reception.Full);
        switch (kind) { case "rain": Rain = bars; break; case "thunder": Thunder = bars; break; case "snow": Snow = bars; break; case "blizzard": Blizzard = bars; break; case "fog": Fog = bars; break; }
    }

    public override string ToString() => string.Join(", ", Kinds.Select(k => $"{k}={Get(k)}"));
}

/// <summary>
/// The phone's signal (the author: "simulate reception based on weather conditions and locations (scenes), all
/// configurable"): 0-4 bars. Full outdoors; a bar less indoors; at home always full (your own wifi); a place on the list
/// caps it (PhoneWeakPlaces: "Docks=2, Sewer=0"); storms and snow take bars; and now and then a bar comes and goes. No
/// Unity, tested.
/// </summary>
public static class Reception
{
    public const int Full = 4;
    public const string DefaultPlaces = "Docks=1, Lowtown=2";
    public const string DefaultCosts = "rain=1, thunder=1, snow=1, blizzard=2, fog=0";

    /// <summary>
    /// The home screen's weather (FB-74): which icon and the words beside it, from the game's weather amounts (0..1) and the
    /// hour. A storm before rain (it rains in one too); clear is the sun by day (6 AM to 7 PM), the moon by night.
    /// </summary>
    public static (string icon, string words) Sky(float blizzard, float snow, float rain, float thunder, float fog, int hour)
    {
        static float N(float v) => float.IsFinite(v) ? v : 0f;
        blizzard = N(blizzard); snow = N(snow); rain = N(rain); thunder = N(thunder); fog = N(fog);
        if (blizzard > 0.3f) return ("snow", "Blizzard");
        if (thunder > 0.3f) return ("storm", "Thunderstorm");
        if (snow > 0.5f) return ("snow", "Snow");
        if (rain > 0.6f) return ("rain", "Rain");
        if (rain > 0.2f || snow > 0.2f) return (rain >= snow ? "rain" : "snow", rain >= snow ? "Light rain" : "Light snow");
        if (fog > 0.5f) return ("fog", "Fog");
        if (fog > 0.2f) return ("cloud", "Hazy");
        bool day = hour >= 6 && hour < 19;
        return day ? ("sun", "Clear") : ("moon", "Clear night");
    }

    /// <summary>"rain=1, blizzard=2" -> the costs (anything missing or unreadable keeps its default).</summary>
    public static WeatherCosts ParseCosts(string text)
    {
        var c = new WeatherCosts();
        foreach (var (k, v) in ParsePlaces(text))
        {
            string kind = k.Trim().ToLowerInvariant();
            if (Array.IndexOf(WeatherCosts.Kinds, kind) >= 0) c.Set(kind, v);
        }
        return c;
    }

    /// <summary>The place list written back ("Docks=2, Lowtown=3"): one entry per place, case kept, newest value kept.</summary>
    public static string WritePlaces(IEnumerable<(string place, int bars)> places)
    {
        var seen = new List<(string, int)>();
        foreach (var (p, b) in places)
        {
            string name = (p ?? "").Replace(",", " ").Replace(";", " ").Replace("=", " ").Trim();
            if (name.Length == 0) continue;
            int at = seen.FindIndex(x => string.Equals(x.Item1, name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) seen[at] = (seen[at].Item1, Math.Clamp(b, 0, Full));
            else seen.Add((name, Math.Clamp(b, 0, Full)));
        }
        return string.Join(", ", seen.Select(x => $"{x.Item1}={x.Item2}"));
    }

    /// <summary>"Docks=2, Lowtown = 3; Sewer=0" -> (place, bars), bars 0-4; anything unreadable is skipped.</summary>
    public static List<(string place, int bars)> ParsePlaces(string text)
    {
        var list = new List<(string, int)>();
        foreach (var part in (text ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.LastIndexOf('=');
            if (eq <= 0) continue;
            string place = part[..eq].Trim();
            if (place.Length == 0 || !int.TryParse(part[(eq + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int bars)) continue;
            list.Add((place, Math.Clamp(bars, 0, Full)));
        }
        return list;
    }

    /// <summary>The bars, and why (short words for the phone: "Lowtown, light snow"; "" when nothing takes any).</summary>
    public static (int bars, string why) Bars(ReceptionInputs r)
    {
        if (!r.Simulate) return (Full, "");
        if (r.AtHome) return (Full, "home");
        int bars = Full;
        var why = new List<string>();
        if (r.PlacesMatter)
        {
            if (r.Indoors && r.IndoorsCost > 0) { bars -= Math.Clamp(r.IndoorsCost, 0, 2); why.Add("indoors"); }
            foreach (var (place, cap) in r.Places)
            {
                if (!Matches(place, r.Location) && !Matches(place, r.Venue) && !Matches(place, r.Scene)) continue;
                if (cap < bars) { bars = cap; why.Add(place); }
            }
        }
        if (r.WeatherMatters)
        {
            var c = r.Costs ?? new WeatherCosts();
            int cost = 0;
            var w = new List<string>();
            void Take(bool when, int bars, string word) { if (when && bars > 0) { cost += bars; w.Add(word); } }
            if (r.Blizzard > 0.3f) Take(true, c.Blizzard, "blizzard");
            else Take(r.Snow > 0.5f, c.Snow, "heavy snow");
            Take(r.Rain > 0.6f, c.Rain, "pouring rain");
            Take(r.Thunder > 0.3f, c.Thunder, "thunder");
            Take(r.Fog > 0.5f, c.Fog, "thick fog");
            if (cost > 0) { bars -= cost; why.Add(string.Join(", ", w)); }
        }
        // The drift: in each half hour of game time, one chance in six of a bar less (never from nothing, never to nothing).
        if (bars is >= 2 and < Full + 1 && Drifts(r.Minute)) bars -= 1;
        bars = Math.Clamp(bars, 0, Full);
        return (bars, string.Join(", ", why.Distinct()));
    }

    private static bool Matches(string place, string name) =>
        place.Length > 0 && name.Length > 0 && name.Contains(place, StringComparison.OrdinalIgnoreCase);

    /// <summary>A steady pseudo-random pick per half hour of game time (the same in every call within it).</summary>
    public static bool Drifts(long minute)
    {
        long slot = Math.Max(0, minute) / 30;
        uint h = (uint)(slot * 2654435761L) ^ 0x9E3779B9u;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
        return h % 6 == 0;
    }

    /// <summary>How long a text takes to go out with these bars (seconds), and whether it goes at all.</summary>
    public static float SendSeconds(int bars) => bars switch { <= 0 => -1f, 1 => 5f, 2 => 2.2f, _ => 1f };

    /// <summary>
    /// FB-79 (the author: "depending on the singal level on the users phone, also depends on the time it takes to receive"):
    /// seconds for a text to reach you by the bars you have; -1 = it waits for a bar.
    /// </summary>
    public static float ReceiveSeconds(int bars) => bars switch { <= 0 => -1f, 1 => 6f, 2 => 2.5f, 3 => 1.4f, _ => 0.8f };

    /// <summary>The carrier's word in the status bar.</summary>
    public static string Carrier(int bars, bool home) => bars <= 0 ? "No signal" : home ? "HOME" : "NIVA";
}
