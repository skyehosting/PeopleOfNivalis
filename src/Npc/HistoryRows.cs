using System;
using System.Collections.Generic;

namespace NivalisAIChat.Npc;

/// <summary>
/// What a chat window shows of everything you've said to each other before: up to HistoryShown spoken lines, a "Day N"
/// divider for each day, "Later" where a new meeting began the same day, and the day you're in last ("Now" when the
/// last line was today). Pure, so the window can build it off the main thread and add it a batch at a time (PERF-4).
/// </summary>
public static class HistoryRows
{
    /// <summary>FB-50: StoryThem / StoryYou / StoryOther are lines from the game's story (StoryOther's text: name, , line).</summary>
    public enum Kind { Divider, Player, Npc, StoryThem, StoryYou, StoryOther }

    private static bool IsStory(string role) => role.StartsWith("story-", StringComparison.Ordinal);

    /// <summary>Not a spoken line: a meeting starting, or a story scene starting.</summary>
    private static bool IsMarker(string role) => role is "scene" or "story-scene";

    /// <summary>
    /// The rows, oldest first. <paramref name="before"/>: only lines from before then (the moment the window opened: the
    /// meeting starting now saves its own lines meanwhile, and they show below as they're said, not here too).
    /// </summary>
    public static List<(Kind kind, string text)> For(IReadOnlyList<ChatLine> history, int max, int today, DateTime? before = null)
    {
        var rows = new List<(Kind, string)>();
        if (max <= 0) return rows;
        int count = history.Count;
        if (before is { } cut)
            for (int i = 0; i < history.Count; i++)
                if (history[i].Utc >= cut) { count = i; break; }
        // Where the last <paramref name="max"/> spoken lines start (scenes aren't lines, they start meetings).
        int start = -1, spoken = 0;
        for (int i = count - 1; i >= 0 && spoken < max; i--)
            if (!IsMarker(history[i].Role)) { start = i; spoken++; }
        if (start < 0) return rows;
        int day = int.MinValue;
        bool newMeeting = false, inStory = false;
        for (int i = start; i < count; i++)
        {
            var l = history[i];
            if (l.Role == "scene") { newMeeting = true; continue; }
            if (l.Role == "story-scene") { rows.Add((Kind.Divider, $"From the story · Day {l.GameDay}")); day = l.GameDay; inStory = true; continue; }
            if (IsStory(l.Role))
            {
                // A story scene shown from its middle (the oldest lines past HistoryShown): it still says where it's from.
                if (!inStory) { rows.Add((Kind.Divider, $"From the story · Day {l.GameDay}")); day = l.GameDay; inStory = true; }
                rows.Add((l.Role == "story-you" ? Kind.StoryYou : l.Role == "story-other" ? Kind.StoryOther : Kind.StoryThem, l.Text));
                continue;
            }
            if (l.GameDay != day) { rows.Add((Kind.Divider, $"Day {l.GameDay}")); day = l.GameDay; }
            else if (newMeeting || inStory) rows.Add((Kind.Divider, "Later"));
            newMeeting = inStory = false;
            rows.Add((l.Role == "player" ? Kind.Player : Kind.Npc, l.Text));
        }
        rows.Add((Kind.Divider, day == today ? "Now" : $"Day {today}"));
        return rows;
    }
}
