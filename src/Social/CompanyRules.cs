using System.Linq;

namespace NivalisAIChat.Social;

/// <summary>FB-72: how someone who came to keep you company may call you (Unity-free, tested).</summary>
public static class CompanyRules
{
    /// <summary>
    /// The author: "The npc wouldn't just greet them by name unless they knew the user by name or username at least."
    /// Your name if they know it; else your Nightline name if that's how they know you; else no name at all.
    /// </summary>
    public static string NameRule(string playerName, string handle)
    {
        string n = Clean(playerName), h = Clean(handle).TrimStart('@');
        if (n.Length > 0) return $"Their name is {n}: you may use it.";
        if (h.Length > 0) return $"You don't know their real name; on Nightline they're @{h}, which you may call them. Never make up a name.";
        return "You don't know their name: don't use one, and never make one up.";
    }

    private static string Clean(string? s)
    {
        var t = new string((s ?? "").Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '\'').ToArray()).Trim();
        return t.Length > 40 ? t[..40] : t;
    }
}
