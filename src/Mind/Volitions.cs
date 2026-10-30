using System;
using System.Collections.Generic;
using System.Linq;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;

namespace NivalisAIChat.Mind;

/// <summary>
/// The inner-life round (the research: "ResidentsTalking is generic: every pair sounds alike because the prompt doesn't say
/// what the pair wants from each other", Comme il Faut's volitions): one line per person on what they want from this talk,
/// from how they feel and how they get on with the others there, and what's on their mind. The conversation shows it; it's
/// never said as a rule. No Unity, tested.
/// </summary>
public static class Volitions
{
    public sealed class Who
    {
        public string Id = "", Name = "", Mood = "", OnMind = "";
    }

    public const string Heading = "[WHAT EACH OF THEM WANTS] (decided; it shows in how they talk, never said outright)";

    public static List<string> For(IReadOnlyList<Who> people, Func<string, string, PairBook.Pair?> pair, int max = 4)
    {
        var lines = new List<string>();
        foreach (var p in people)
        {
            if (lines.Count >= max) break;
            string first = Ai.Prompts.FirstName(p.Name);
            if (first.Length == 0) continue;
            var others = people.Where(o => o.Id != p.Id).Select(o => (o, rel: pair(p.Id, o.Id))).ToList();
            var foe = others.Where(x => x.rel is { Times: > 0 } r && r.Affinity <= -20).OrderBy(x => x.rel!.Affinity).Select(x => x.o).FirstOrDefault();
            var friend = others.Where(x => x.rel is { Times: > 0 } r && r.Affinity >= 50).OrderByDescending(x => x.rel!.Affinity).Select(x => x.o).FirstOrDefault();
            var fancied = others.Where(x => x.rel is { Times: > 0 } r && r.Affinity >= 35).Select(x => x.o).FirstOrDefault();
            string mood = Moods.Normalize(p.Mood);
            string? want = mood switch
            {
                "in a hurry" or "stressed" => "wants to get away soon",
                "angry" or "irritated" or "grumpy" => "is short-tempered today and doesn't want a long chat",
                "lonely" => "wants the company and keeps the talk going",
                "flirty" when fancied != null => $"is a bit flirty with {Ai.Prompts.FirstName(fancied.Name)}",
                _ => null,
            };
            want ??= foe != null ? $"wants to needle {Ai.Prompts.FirstName(foe.Name)} a little"
                : friend != null ? $"is glad to see {Ai.Prompts.FirstName(friend.Name)}"
                : p.OnMind.Length > 0 ? $"has this on their mind: {p.OnMind.TrimEnd('.')}"
                : null;
            if (want != null) lines.Add($"{first} {want}.");
        }
        return lines;
    }
}
