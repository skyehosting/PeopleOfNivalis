using System;
using System.Text.RegularExpressions;

namespace NivalisAIChat.Ai;

/// <summary>
/// Short codes for problems the player sees ("[AI-KEY] ..."), so a message in the chat, the console's /errors and the
/// log all say what kind of trouble it is at a glance: AI-KEY, AI-ACCESS, AI-MODEL, AI-LIMIT, AI-SERVER, AI-TIMEOUT,
/// AI-SLOW, AI-NET, AI-READ, AI-REFUSED, AI-BUDGET, PIC-UNSUPPORTED, PIC-SKIPPED, PIC-CAPTURE, VOICE-FAIL, VOICE-SLOW,
/// VOICE-BUDGET (VoiceLimit reached: carried in the message), CHAT-START,
/// DATA-READ (a person's file couldn't be read right now: carried in the message), AI-URL (ApiUrl can't be used: carried),
/// CFG-READ (the settings file couldn't be read after it changed, or some of its lines aren't read: carried), MOD-ERROR.
/// </summary>
public static class ProblemCodes
{
    /// <summary>"[CODE] message", the code read from what kind of problem it is.</summary>
    public static string Coded(string message, string? code = null)
    {
        message = (message ?? "").Trim();
        if (message.Length == 0) message = "Something went wrong.";
        // A message that already carries a code (passed along from where it happened) keeps it, moved to the front.
        // Only one of the mod's own codes, at the start or after "...: " (audit B8: a provider's "[ERR-500]" isn't one).
        var carried = Carried.Match(message);
        if (carried.Success) return $"[{carried.Groups[1].Value}] {message.Remove(carried.Index, carried.Length).Trim()}";
        return $"[{code ?? CodeFor(message)}] {message}";
    }

    private static readonly Regex Carried = new(@"(?:^|(?<=:\s))\[((?:AI|VOICE|PIC|MEM)-[A-Z0-9]+|DATA-READ|CFG-READ|CHAT-START|MOD-ERROR)\]\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>FB-84: a problem for one line that can't wrap (world chat's status): its first sentence, at most
    /// <paramref name="max"/> characters (review FB84-1: an error with no ". " in it is cut too).</summary>
    public static string FirstSentence(string text, int max = 140)
    {
        string t = (text ?? "").Trim();
        int stop = t.IndexOf(". ", StringComparison.Ordinal);
        if (stop > 0) t = t[..(stop + 1)];
        return t.Length <= max ? t : t[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>The message when a conversation stops waiting for an answer (GiveUpAfterSeconds).</summary>
    public static string Slow(int seconds) =>
        $"[AI-SLOW] No answer from the AI after {seconds} seconds, so it stopped waiting. The provider may be slow or busy: " +
        $"try again, or set GiveUpAfterSeconds in the config above {seconds} (-1 = no limit).";

    /// <summary>The code for a problem, from its message (the AI client's errors say what happened).</summary>
    public static string CodeFor(string message)
    {
        string m = message ?? "";
        bool Has(string s) => m.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("can't look at pictures")) return "PIC-UNSUPPORTED";
        if (Has("No API key")) return "AI-KEY";
        if (Has("Token limit reached")) return "AI-BUDGET";
        var http = Regex.Match(m, @"AI endpoint error (\d{3})");
        if (http.Success)
            return http.Groups[1].Value switch
            {
                "401" => "AI-KEY",
                "403" => "AI-ACCESS",
                "404" => "AI-MODEL",
                "429" => "AI-LIMIT",
                var c when c.StartsWith("5") => "AI-SERVER",
                var c => "AI-" + c,
            };
        if (Has("timed out")) return "AI-TIMEOUT";
        if (Has("Couldn't reach")) return "AI-NET";
        if (Has("declined") || Has("refused")) return "AI-REFUSED";
        if (Has("Unreadable reply") || Has("couldn't be read") || Has("returned no choices")) return "AI-READ";
        if (Has("voice")) return "VOICE-FAIL";
        return "MOD-ERROR";
    }
}
