namespace NivalisAIChat.Social;

/// <summary>CF-19: whose name the game may show before you know it (Unity-free, tested).</summary>
public static class NameRules
{
    /// <summary>
    /// A customer's name is left blank only for someone ordinary (not the story cast) whom the game hasn't marked met and who
    /// hasn't told you their name; with the setting off, nobody's is.
    /// </summary>
    public static bool HideCustomerName(bool on, bool storyCast, bool gameMet, bool toldYou) => on && !storyCast && !gameMet && !toldYou;
}
