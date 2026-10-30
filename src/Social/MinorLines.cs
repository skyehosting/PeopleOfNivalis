using System;
using System.Collections.Generic;

namespace NivalisAIChat.Social;

/// <summary>
/// What children (and anyone under 18) say: a fixed line now and then as the player passes, and a brush-off when the
/// player tries to talk to them. Written lines only: no AI, no profile, and the chat window never opens for them.
/// </summary>
public static class MinorLines
{
    private static readonly string[] Refusals =
    {
        "I don't really wanna talk.",
        "Go away.",
        "I'm not supposed to talk to strangers.",
        "Leave me alone, I'm busy.",
        "Nope. Stranger danger.",
        "I'm not allowed to talk to people I don't know.",
        "Not now.",
        "Ugh. No thanks.",
    };

    private static readonly string[] Anyone =
    {
        "Ugh, it's raining again.",
        "I'm so bored.",
        "When I grow up I'm getting out of this city.",
        "I bet I could climb that.",
        "It always smells like noodles around here.",
        "I'm not lost. I know exactly where I am.",
        "Hmm hmm hmmm...",
        "One more minute, then I'm going home. Probably.",
    };

    private static readonly Dictionary<string, string[]> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mala Spinx"] = new[]
        {
            "Supply and demand. Supply and demand...",
            "One day I'm gonna own this whole street.",
            "My school project is going to be the best one. Obviously.",
            "Profit margins... what even is a margin?",
            "Do you think a lemonade stand works without lemons?",
            "I'm going to be a tycoon. Write that down.",
        },
        ["Silo Mills"] = new[]
        {
            "Wanna play hide-and-seek? I'm really good at hiding.",
            "Ready or not, here I come...",
            "My toys never talk back. I like that.",
            "Everyone's scared of the dark. Not me.",
            "I found something in the sewer. I'm not telling.",
            "Did you know some monsters just look like normal people?",
        },
    };

    /// <summary>The brush-off when the player tries to start a conversation.</summary>
    public static string Refusal(Random rng) => Refusals[rng.Next(Refusals.Length)];

    /// <summary>Something they say to nobody in particular as the player passes (their own lines first, if they have any).</summary>
    public static string Passing(string name, Random rng)
    {
        if (ByName.TryGetValue((name ?? "").Trim(), out var own) && rng.NextDouble() < 0.75) return own[rng.Next(own.Length)];
        return Anyone[rng.Next(Anyone.Length)];
    }
}
