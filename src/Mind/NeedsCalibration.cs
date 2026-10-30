using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisAIChat.Mind;

/// <summary>
/// The inner-life round, step 2 (needs into words): the game keeps each resident's Hunger and Rest (Needs, a float each),
/// but its stubs don't say which way they run (does a high Hunger mean hungry, or fed?) or on what scale. So the code
/// finds out: on people who aren't eating or sleeping, a need builds up over time, and the direction it drifts is the
/// direction of "needing it". Until enough drifts agree for both, needs aren't used at all (nobody is told they're
/// starving on a guess). Kept for the session only; no Unity, tested.
/// </summary>
public sealed class NeedsCalibration
{
    private readonly Dictionary<string, (float hunger, float rest, double at)> _last = new();
    private int _hungerUp, _hungerDown, _restUp, _restDown;
    private float _max;

    /// <summary>Drifts each way that must agree before a direction is trusted.</summary>
    public const int Needed = 6;
    /// <summary>Seconds between two readings of one person for a drift to count.</summary>
    public const double Gap = 20;

    /// <summary>One reading of someone (real seconds since start): drifts only count while they're doing neither.</summary>
    public void Observe(string id, float hunger, float rest, double seconds, bool eating, bool sleeping)
    {
        if (id.Length == 0 || !float.IsFinite(hunger) || !float.IsFinite(rest)) return;
        _max = Math.Max(_max, Math.Max(Math.Abs(hunger), Math.Abs(rest)));
        if (eating || sleeping) { _last.Remove(id); return; }
        if (_last.TryGetValue(id, out var was))
        {
            if (seconds - was.at < Gap) return;
            float dh = hunger - was.hunger, dr = rest - was.rest;
            if (Math.Abs(dh) > 1e-4f) { if (dh > 0) _hungerUp++; else _hungerDown++; }
            if (Math.Abs(dr) > 1e-4f) { if (dr > 0) _restUp++; else _restDown++; }
        }
        _last[id] = (hunger, rest, seconds);
        if (_last.Count > 300) _last.Clear();
    }

    /// <summary>+1: a higher value is more need; -1: a higher value is less need; 0: not known yet.</summary>
    public int HungerSign => Sign(_hungerUp, _hungerDown);
    public int RestSign => Sign(_restUp, _restDown);
    public bool Ready => HungerSign != 0 && RestSign != 0;

    private static int Sign(int up, int down)
    {
        if (up + down < Needed) return 0;
        if (up >= down * 3) return 1;
        if (down >= up * 3) return -1;
        return 0;
    }

    /// <summary>The scale the game uses (0..1, or 0..100 when anything read was above 1.5).</summary>
    public float Scale => _max > 1.5f ? 100f : 1f;

    /// <summary>How much they need food, 0..1, or -1 while it isn't known.</summary>
    public float Hunger(float raw) => Need(raw, HungerSign);
    public float Rest(float raw) => Need(raw, RestSign);

    private float Need(float raw, int sign)
    {
        if (sign == 0 || !Ready || !float.IsFinite(raw)) return -1f;
        float x = Math.Clamp(raw / Scale, 0f, 1f);
        return sign > 0 ? x : 1f - x;
    }

    public string Describe() =>
        $"hunger {(HungerSign == 0 ? "not known yet" : HungerSign > 0 ? "builds up (higher = hungrier)" : "runs down (higher = fuller)")} ({_hungerUp} up, {_hungerDown} down), " +
        $"rest {(RestSign == 0 ? "not known yet" : RestSign > 0 ? "builds up (higher = more tired)" : "runs down (higher = more rested)")} ({_restUp} up, {_restDown} down), scale 0-{Scale:0}";
}
