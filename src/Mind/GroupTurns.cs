using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NivalisAIChat.Mind;

/// <summary>One person in a group chat, for deciding turns.</summary>
public sealed class TurnMember
{
    public string Id = "", Name = "", Label = "";
    /// <summary>How wound up they are (0 calm .. 1), from their feelings underneath.</summary>
    public float Arousal = Voice.Tone.CalmArousal;
    /// <summary>Confrontational: they cut in when they're wound up.</summary>
    public bool Confronts;
    /// <summary>How much of a talker they are (about 0.3 .. 1.6).</summary>
    public double Talkative = 1;
}

/// <summary>
/// The inner-life round, step 6 (group turn-taking in code, inside the one call the group chat already makes): who you
/// spoke to answers (named, or the one whose question you're answering, or who spoke last), one other may chime in (the
/// more of a talker and the more wound up, the likelier), and someone confrontational and worked up may cut in (Sacks,
/// Schegloff and Jefferson's turn-taking; LLMs are near chance at telling who's addressed, so the code says it). The plan
/// goes into the request as decided, and the answer is held to it. Saves tokens, never adds a call. No Unity, tested.
/// </summary>
public static class GroupTurns
{
    public sealed class Plan
    {
        public string First = "";
        public string? Chime, CutIn;
        public string Note = "";

        /// <summary>Who may speak in this answer.</summary>
        public HashSet<string> Speakers => new[] { First, Chime, CutIn }.Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToHashSet();
    }

    private static readonly Regex Everyone = new(@"(?i)\b(?:you all|y'all|everyone|everybody|guys|you two|you three|both of you|all of you)\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// The turns for this answer. <paramref name="lastSpeaker"/> is who spoke last ("" when nobody has yet);
    /// <paramref name="lastAsked"/>: their line asked something (the newcomer is answering them).
    /// </summary>
    public static Plan Decide(string playerLine, IReadOnlyList<TurnMember> members, string lastSpeaker, bool lastAsked, string leadId, Random rng)
    {
        var plan = new Plan();
        if (members.Count == 0) return plan;
        string line = playerLine ?? "";
        // Named: the first one mentioned by their first name or label.
        TurnMember? named = null;
        int at = int.MaxValue;
        foreach (var m in members)
            foreach (var n in new[] { Ai.Prompts.FirstName(m.Name), m.Label }.Where(x => x.Length > 1).Distinct())
            {
                var hit = Regex.Match(line, $@"(?i)(?<![\p{{L}}]){Regex.Escape(n)}(?![\p{{L}}])", RegexOptions.CultureInvariant);
                if (hit.Success && hit.Index < at) { at = hit.Index; named = m; }
            }
        bool toAll = named == null && Everyone.IsMatch(line);
        var first = named
                    ?? (toAll ? members.OrderByDescending(m => m.Talkative + m.Arousal).First() : null)
                    ?? (lastAsked ? members.FirstOrDefault(m => m.Id == lastSpeaker) : null)
                    ?? members.FirstOrDefault(m => m.Id == lastSpeaker)
                    ?? members.FirstOrDefault(m => m.Id == leadId)
                    ?? members[0];
        plan.First = first.Id;
        var others = members.Where(m => m.Id != first.Id).ToList();
        // Someone confrontational and worked up may cut in.
        var hot = others.Where(m => m.Confronts && m.Arousal > 0.6f).OrderByDescending(m => m.Arousal).FirstOrDefault();
        if (hot != null && rng.NextDouble() < 0.5) plan.CutIn = hot.Id;
        // One other may chime in: to everyone, likely; otherwise now and then, the talkers and the wound-up more.
        var chimer = others.Where(m => m.Id != plan.CutIn).OrderByDescending(m => m.Talkative * (0.6 + m.Arousal) + rng.NextDouble() * 0.3).FirstOrDefault();
        if (chimer != null)
        {
            double p = toAll ? 0.75 : Math.Clamp(0.2 + 0.2 * chimer.Talkative + 0.2 * chimer.Arousal, 0.15, 0.6);
            if (rng.NextDouble() < p) plan.Chime = chimer.Id;
        }
        string N(string id) => Ai.Prompts.FirstName(members.First(m => m.Id == id).Name);
        var note = new List<string> { $"{N(plan.First)} answers{(toAll ? " first" : "")}." };
        if (plan.CutIn != null) note.Add($"{N(plan.CutIn)} cuts in partway, short and heated.");
        if (plan.Chime != null) note.Add($"{N(plan.Chime)} may add one short line after.");
        var quiet = members.Where(m => m.Id != plan.First && m.Id != plan.Chime && m.Id != plan.CutIn).Select(m => N(m.Id)).ToList();
        if (quiet.Count > 0) note.Add($"{string.Join(" and ", quiet)} {(quiet.Count == 1 ? "listens" : "listen")} this time.");
        plan.Note = "[Turns, decided: " + string.Join(" ", note) + "]";
        return plan;
    }
}
