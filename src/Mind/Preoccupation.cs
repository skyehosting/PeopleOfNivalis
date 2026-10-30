using System;
using System.Collections.Generic;
using System.Linq;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Mind;

/// <summary>What's on someone's mind right now: what kind of thing, a key that says which one, how it's put, how strongly, since when.</summary>
public sealed class Thought
{
    public string Kind { get; set; } = "";
    public string Key { get; set; } = "";
    public string Text { get; set; } = "";
    public float Score { get; set; }
    public long Since { get; set; }
}

/// <summary>What the code knows about someone this moment, for picking what's on their mind.</summary>
public sealed class MindInputs
{
    public Persona Persona = new();
    /// <summary>How much they need food and rest, 0..1 (-1: not known; the game's needs aren't read until they're understood).</summary>
    public float Hunger = -1f, Rest = -1f;
    public List<Moodlet> Moodlets = new();
    /// <summary>What they saw or heard, as they'd put it, with how much it's with them (a belief's score) and its key.</summary>
    public List<(string text, double score, string key)> Seen = new();
    /// <summary>A plan with the player coming up soon, in words ("meeting the newcomer at the noodle bar at 20:00"), or "".</summary>
    public string PlanSoon = "";
    /// <summary>Their partner, when they haven't been together for a while ("Teo"), or "".</summary>
    public string Missing = "";
    public bool AtWork, WorkUnhappy;
}

/// <summary>
/// The inner-life round (the second research doc's slim "thought loop": LIDA's competing thoughts, Inner Thoughts): a few
/// candidate thoughts, scored by how strongly each is with them and who they are; the winner is what's on their mind. It
/// sticks for a while (a new one has to beat it clearly, or the old one has been there long), so their mind doesn't flicker.
/// It feeds their chat's [INSIDE YOU RIGHT NOW], what they mutter, and the residents' talk. Zero tokens; no Unity, tested.
/// </summary>
public static class Preoccupation
{
    /// <summary>How much better a new thought must be to take over.</summary>
    public const float SwitchMargin = 0.15f;
    /// <summary>After this long (game minutes) any better thought may take over.</summary>
    public const int HoldMinutes = 90;

    public static List<Thought> Candidates(MindInputs i, long now)
    {
        var list = new List<Thought>();
        string t = (i.Persona.Temperament + " " + string.Join(" ", i.Persona.Personality ?? new List<string>())).ToLowerInvariant();
        bool worrier = new[] { "anxious", "nervous", "paranoid", "melanchol", "fidget", "over-sharer" }.Any(t.Contains);
        bool social = new[] { "warm", "chatty", "lonely", "nosy", "flirt", "gossip" }.Any(t.Contains);
        if (i.Hunger >= 0.75f)
            list.Add(new Thought { Kind = "need", Key = "hungry", Text = i.Hunger >= 0.9f ? "starving: you need to eat" : "getting hungry", Score = 0.45f + (i.Hunger - 0.75f) * 2f });
        if (i.Rest >= 0.8f)
            list.Add(new Thought { Kind = "need", Key = "tired", Text = i.Rest >= 0.92f ? "dead on your feet: you need to sleep" : "tired, flagging a bit", Score = 0.4f + (i.Rest - 0.8f) * 2f });
        foreach (var m in i.Moodlets.Where(m => m.Until > now && m.Key != "rebound" && m.Text.Length > 0))
        {
            float span = Math.Max(1, m.Until - m.Since);
            float left = Math.Clamp((m.Until - now) / span, 0f, 1f);
            float score = Math.Abs(m.V) * (0.6f + 0.4f * left) + Math.Abs(m.A) * 0.2f;
            if (worrier && m.V < 0) score *= 1.2f;
            list.Add(new Thought { Kind = "feeling", Key = "m:" + m.Key, Text = m.Text, Score = score });
        }
        foreach (var (text, score, key) in i.Seen.Where(s => s.text.Length > 0))
            list.Add(new Thought { Kind = "seen", Key = "b:" + key, Text = text, Score = (float)Math.Clamp(score * 0.9, 0, 1.2) });
        if (i.PlanSoon.Length > 0)
            list.Add(new Thought { Kind = "plan", Key = "plan", Text = i.PlanSoon, Score = social ? 0.62f : 0.55f });
        if (i.Missing.Length > 0)
            list.Add(new Thought { Kind = "missing", Key = "missing", Text = $"missing {i.Missing}", Score = social ? 0.42f : 0.35f });
        if (i.AtWork && i.WorkUnhappy)
            list.Add(new Thought { Kind = "work", Key = "work", Text = "fed up with this job", Score = 0.4f });
        // When nothing else is: their own worry, the mind wandering (more for worriers).
        string worry = (i.Persona.CurrentWorry ?? "").Trim();
        if (worry.Length > 0)
            list.Add(new Thought { Kind = "worry", Key = "worry", Text = worry.TrimEnd('.'), Score = worrier ? 0.4f : 0.25f });
        return list;
    }

    /// <summary>
    /// What's on their mind now: the best candidate, unless what was on it is still there and not clearly beaten (and
    /// hasn't been held long). A thought that's no longer a candidate (it passed) gives way. Null when nothing is.
    /// </summary>
    public static Thought? Choose(MindInputs i, Thought? current, long now)
    {
        var candidates = Candidates(i, now);
        if (candidates.Count == 0) return null;
        var best = candidates.OrderByDescending(c => c.Score).First();
        var still = current != null ? candidates.FirstOrDefault(c => c.Key == current.Key) : null;
        if (still != null && current != null && now >= current.Since)
        {
            bool held = now - current.Since >= HoldMinutes;
            bool beaten = best.Key != still.Key && (best.Score > still.Score + SwitchMargin || (held && best.Score > still.Score));
            if (!beaten) { still.Since = current.Since; return still; }
        }
        best.Since = still != null && still.Key == best.Key && current != null ? current.Since : now;
        return best;
    }
}
