using System.Collections.Generic;

namespace NivalisAIChat.Npc;

/// <summary>
/// FB-75 (the author: "backfill the audio messages ... so I could go into my save and re-play those lines audio", and yes to
/// bringing in the rest of her seen lines): one page of the game's own chat history with a story character, turned into
/// StoryBook lines. Unity-free and tested. Only what the game lists on its history page (lines the player has seen) is
/// read, and of those only the character's own lines and the player's picks: anyone else's line in the scene is left out
/// (it can't be checked for age), and nothing that isn't on the page is ever looked at.
/// </summary>
public static class StoryHistory
{
    /// <summary>A line of a dialogue tree, by its id: its words, whether the character in question says it, its voice file, by text message.</summary>
    public readonly record struct Node(string Text, bool Theirs, string Voice, bool Message);

    /// <summary>A pick of the player's, by its id: its words, by text message.</summary>
    public readonly record struct Choice(string Text, bool Message);

    /// <summary>One voice to fill in: the line's words (as StoryBook keeps them) and the game's voice file for it.</summary>
    public readonly record struct VoiceJob(string Text, string Voice);

    /// <summary>
    /// The page's lines in order: theirs (<see cref="StoryBook.Them"/>) from <paramref name="nodes"/>, yours
    /// (<see cref="StoryBook.You"/>) from <paramref name="choices"/>; an id that's neither (or someone else's line) is
    /// skipped. Their lines with a voice file come back as jobs too.
    /// </summary>
    public static (List<StoryBook.Line> lines, List<VoiceJob> voices) PageLines(IEnumerable<(string id, bool fromPlayer)> page,
        IReadOnlyDictionary<string, Node> nodes, IReadOnlyDictionary<string, Choice> choices)
    {
        var lines = new List<StoryBook.Line>();
        var voices = new List<VoiceJob>();
        foreach (var (id, fromPlayer) in page)
        {
            if (string.IsNullOrEmpty(id)) continue;
            if (fromPlayer)
            {
                if (!choices.TryGetValue(id, out var picked)) continue;
                string said = StoryBook.Clean(picked.Text);
                if (said.Length > 0) lines.Add(new StoryBook.Line { Who = StoryBook.You, Text = said, Message = picked.Message });
                continue;
            }
            if (!nodes.TryGetValue(id, out var n) || !n.Theirs) continue;
            string text = StoryBook.Clean(n.Text);
            if (text.Length == 0) continue;
            lines.Add(new StoryBook.Line { Who = StoryBook.Them, Text = text, Message = n.Message });
            if (!string.IsNullOrWhiteSpace(n.Voice) && n.Voice.Length < 200) voices.Add(new VoiceJob(text, n.Voice));
        }
        return (lines, voices);
    }
}
