using System;
using System.Linq;

namespace NivalisAIChat.Npc;

/// <summary>
/// The gestures the AI can cue with a line (CF-04: "a library where the ai can activate" an animation): plain words, each
/// one of the game's own animations a standing person makes, either a one-off (its animator trigger) or a way of
/// talking for the line (its talking mood). Seated people get none (CF-03). Plain strings, not this mod's own enum, so the
/// public build's obfuscation can't rename them. No Unity here, so it can be tested.
/// </summary>
public static class GestureLibrary
{
    /// <summary>Every gesture the AI may name ("" = none).</summary>
    public static readonly string[] Names =
    {
        "nod", "shake head", "wave", "wave goodbye", "point", "look down", "shake off the rain", "sneeze", "celebrate",
        "laugh", "argue", "explain", "listen closely", "shocked", "bored", "sad", "flirt",
    };

    /// <summary>For the prompt: the list, as words.</summary>
    public static string List => string.Join(", ", Names);

    /// <summary>A name the AI wrote, as one of <see cref="Names"/>, or "" (unknown, or none).</summary>
    public static string Normalize(string? name)
    {
        string n = (name ?? "").Trim().Trim('"', '\'', '.').ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
        return Names.Contains(n) ? n : "";
    }

    /// <summary>The game's animator trigger for a one-off gesture (Character.AnimatorTriggerType), or null.</summary>
    public static string? Trigger(string name) => name switch
    {
        "nod" => "Nod",
        "shake head" => "Deny",
        "wave" => "Greeting",
        "wave goodbye" => "Goodbye",
        "point" => "FingerPoint",
        "look down" => "LookDown",
        "shake off the rain" => "ShakeRain",
        "sneeze" => "Sneeze",
        "celebrate" => "Celebration",
        _ => null,
    };

    /// <summary>The game's talking mood for a way of talking (Character.AnimatorMoodType), or null.</summary>
    public static string? Mood(string name) => name switch
    {
        "laugh" => "Laugh",
        "argue" => "Argue",
        "explain" => "Explain",
        "listen closely" => "Interested",
        "shocked" => "Shocked",
        "bored" => "Bored",
        "sad" => "Sad",
        "flirt" => "Flirting",
        _ => null,
    };
}
