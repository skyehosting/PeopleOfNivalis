using BepInEx.Logging;

namespace NivalisAIChat;

internal static class Log
{
    public static ManualLogSource? Source;
    public static bool Verbose;

    public static void Info(string msg) => Source?.LogInfo(msg);
    public static void Warn(string msg) => Source?.LogWarning(msg);
    public static void Error(string msg) => Source?.LogError(msg);

    public static void Debug(string msg)
    {
        if (Verbose) Source?.LogInfo("[debug] " + msg);
        else Source?.LogDebug(msg);
    }
}
