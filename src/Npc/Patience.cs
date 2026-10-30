using System;
using System.Linq;
using NivalisAIChat.Game;

namespace NivalisAIChat.Npc;

/// <summary>
/// How long someone is willing to stand and talk, and how much nonsense they'll take, for one meeting.
/// Drives the "[THIS CONVERSATION]" part of the prompt so NPCs end conversations the way people do:
/// out of time, fed up, or simply done.
/// </summary>
public static class Patience
{
    /// <summary>Exchanges (player line + reply) they can spare this meeting, before scaling.</summary>
    public static int Budget(NpcRecord rec, GameSnapshot snap, string mood, double scale)
    {
        var p = rec.Persona;
        string t = (p.Temperament + " " + string.Join(" ", p.Personality) + " " + string.Join(" ", p.Speech.Quirks)).ToLowerInvariant();
        double b = 6;
        if (Has(t, "impatient", "hurry", "hot-headed", "curt", "terse", "guarded", "cynical", "suspicious", "stoic", "blunt", "bitter")) b -= 2;
        if (Has(t, "warm", "chatty", "lonely", "nosy", "gossip", "over-sharer", "flirt", "theatrical", "philosoph", "earnest", "glad to talk")) b += 3;
        if (Has(t, "shy")) b -= 1;
        string v = p.Speech.Verbosity.ToLowerInvariant();
        if (v.Contains("terse")) b -= 1;
        if (v.Contains("chatty") || v.Contains("rambl")) b += 2;

        b += mood switch
        {
            "in a hurry" => -3,
            "irritated" or "angry" or "stressed" or "tired" or "grumpy" or "hungover" => -2,
            "anxious" or "nervous" or "paranoid" or "suspicious" or "distracted" => -1,
            "bored" => 1,
            "lonely" or "curious" or "excited" or "flirty" or "cheerful" or "playful" or "amused" or "hopeful" => 2,
            _ => 0,
        };

        string act = snap.NpcActivity.ToLowerInvariant();
        bool working = IsWorking(snap);
        if (working) b -= snap.NpcWorksForPlayer ? 1 : 2; // the boss gets a bit more time
        else if (act.Contains("wander") || act.Contains("killing time") || act.Contains("standing around") || act.Contains("leaning")) b += 1;
        if (act.Contains("home to sleep")) b -= 1;

        int a = Features.Feeling(rec.State.Affinity); // relationships off: neutral
        if (a >= 60) b += 4;
        else if (a >= 30) b += 2;
        else if (a <= -30) b -= 2;
        if (rec.State.TimesMet > 3) b += 1;
        if (snap.Hour < 5) b -= 1;
        if (Recent(rec, snap) is { minutes: < 60 }) b -= 2;

        return (int)Math.Round(Math.Clamp(b, 2, 16) * Math.Clamp(scale, 0.3, 4));
    }

    /// <summary>How much annoyance they tolerate before they've had enough.</summary>
    public static double Threshold(NpcRecord rec)
    {
        var p = rec.Persona;
        string t = (p.Temperament + " " + string.Join(" ", p.Personality)).ToLowerInvariant();
        if (Has(t, "hot-headed", "touchy", "proud", "short fuse", "aggressive", "bitter")) return 5;
        if (Has(t, "gentle", "patient", "kind", "shy", "laid-back", "unbothered", "stoic")) return 9;
        return 7;
    }

    /// <summary>Annoyance change for one exchange, from the reply's feelings.</summary>
    public static double AnnoyanceDelta(int affinityChange, string mood, string intensity)
    {
        double d = affinityChange < 0 ? -affinityChange : -affinityChange / 2.0;
        double w = intensity == "strong" ? 1.5 : intensity == "mild" ? 0.5 : 1;
        d += mood switch
        {
            "angry" => 2 * w,
            "irritated" or "suspicious" or "grumpy" => 1 * w,
            "cheerful" or "amused" or "playful" or "content" or "flirty" => -0.5,
            _ => 0,
        };
        return d;
    }

    public static bool IsWorking(GameSnapshot snap)
    {
        string a = snap.NpcActivity.ToLowerInvariant();
        return snap.NpcWorkplace.Length > 0 &&
               Has(a, "order", "cook", "prep", "serv", "clean", "wiping", "polish", "chopping", "sweep", "shift", "bar", "trash", "dishes", "storeroom", "hauling", "running the place", "complaint");
    }

    public static string WhatTheyNeedToDo(GameSnapshot snap)
    {
        if (snap.OutWithPlayer) return OutTogether; // FB-85: a date, a night at theirs: you're not going anywhere
        if (IsWorking(snap)) return "get back to work";
        string a = snap.NpcActivity.ToLowerInvariant();
        if (a.Contains("home") || a.Contains("sleep")) return "get home";
        if (a.Contains("eat") || snap.NpcVisiting.Length > 0) return "get back to your food";
        return "be on your way";
    }

    /// <summary>FB-85: what "leaving" means while out with the player: the talk rests, the two of you carry on together.</summary>
    public const string OutTogether = "let the conversation rest for a while (you're still out with them; you don't leave)";

    /// <summary>Minutes of game time since they last walked away from the player, if it was today.</summary>
    public static (int minutes, string reason)? Recent(NpcRecord rec, GameSnapshot snap)
    {
        var st = rec.State;
        if (st.LastLeftReason.Length == 0 || st.LastLeftGameDay != snap.GameDay) return null;
        int now = snap.Hour * 60 + snap.Minute;
        int mins = now - st.LastLeftMinute;
        if (mins < 0) return null;
        return (mins, st.LastLeftReason);
    }

    public static string Block(NpcRecord rec, GameSnapshot snap, ChatSession s)
    {
        int k = s.Exchanges;
        int left = s.PatienceBudget - k;
        string need = WhatTheyNeedToDo(snap);
        string label = s.PatienceBudget <= 3 ? "very little" : s.PatienceBudget <= 6 ? "a few minutes' worth" : s.PatienceBudget <= 10 ? "some" : "plenty";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[THIS CONVERSATION]");
        sb.AppendLine($"- Time you have for this: {label}. So far you've exchanged {k} line(s) with them.");
        if (left > 2) sb.AppendLine($"- You can spare roughly {left} more exchange(s) before you need to {need}, if it stays worth your while.");
        else if (left > 0) sb.AppendLine($"- You're nearly out of time: start wrapping up — you need to {need} soon.");
        else sb.AppendLine($"- You're out of time. Wrap it up now with a natural parting line (you need to {need}) and set leaving to true.");
        if (s.LastChance)
            sb.AppendLine("- You held off leaving to hear their answer to your question. Answer it or react to it, then wrap up and set leaving to true (unless they've truly changed your mind).");
        double th = s.AnnoyanceThreshold;
        if (s.Annoyance >= th)
            sb.AppendLine("- You've had enough of them. End it now — a curt, cold or exasperated parting line, whatever fits you — and set leaving to true.");
        else if (s.Annoyance >= th * 0.6)
            sb.AppendLine("- They're getting on your nerves. One more push and you're walking off.");
        if (Recent(rec, snap) is { } r && s.Exchanges == 0)
            sb.AppendLine($"- You walked away from them {(r.minutes < 2 ? "just now" : $"about {r.minutes} minutes ago")} because {r.reason}. Now they're back.");
        sb.AppendLine("- Ending a conversation is normal. When you're busy, annoyed, bored, uncomfortable or simply done, say a parting line the way you would and set leaving to true. A parting line never asks them anything — you won't be there for the answer; if you still want to ask something, ask it and stay (leaving false). A conversation you're enjoying can run long if you have the time.");
        return sb.ToString();
    }

    public static string LeaveReason(ChatSession s) =>
        s.SilentStage >= 2 ? "they just stood there staring at you without a word"
        : s.Annoyance >= s.AnnoyanceThreshold ? "you'd had enough of them"
        : s.Exchanges >= s.PatienceBudget ? "you didn't have time to keep chatting"
        : "you were done talking";

    private static bool Has(string text, params string[] words) => words.Any(text.Contains);
}
