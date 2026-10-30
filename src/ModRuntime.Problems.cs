using System;
using System.Collections.Generic;
using System.Linq;
using NivalisAIChat.Ai;
using NivalisAIChat.UI;
using UnityEngine;

namespace NivalisAIChat;

/// <summary>
/// Problems the player should know about (the AI failing, pictures it can't look at, voices that didn't come), each with
/// a short code in brackets. Shown where they happened (the chat) and kept for the console: the newest ones show when
/// it opens, and /errors lists them all.
/// </summary>
public static partial class ModRuntime
{
    private const int ProblemsKept = 30;
    private static readonly object _problemsLock = new();
    private static readonly List<(DateTime at, string text, int times)> _problems = new();
    private static int _problemsUnseen;

    /// <summary>
    /// Note a problem: it's logged, kept for the console, and returned with its code for showing in the chat. The code
    /// comes from the message unless one is given.
    /// </summary>
    internal static string Problem(string message, string? code = null, bool log = true, bool show = true)
    {
        string text = Coded(message, code);
        lock (_problemsLock)
        {
            // The same problem again (a wrong key fails every request): counted on the last entry, not repeated.
            if (_problems.Count > 0 && _problems[^1].text == text)
            {
                _problems[^1] = (DateTime.Now, text, _problems[^1].times + 1);
                if (_problemsUnseen == 0) _problemsUnseen = 1;
            }
            else
            {
                _problems.Add((DateTime.Now, text, 1));
                if (_problems.Count > ProblemsKept) _problems.RemoveAt(0);
                _problemsUnseen = Math.Min(_problemsUnseen + 1, _problems.Count);
            }
        }
        if (log) Log.Warn(text);
        if (show)
        {
            if (Environment.CurrentManagedThreadId == _mainThreadId) ShowProblem(text, background: false);
            else MainThread.Enqueue(() => ShowProblem(text, background: true));
        }
        return text;
    }

    private static string _lastShown = "";
    private static float _lastShownAt = -100f;

    /// <summary>
    /// A problem shows where you are: in your chat as a SYSTEM line (never part of what they remember), or live in the
    /// console. Otherwise it waits for the console's next opening. Something going wrong in the background again within
    /// half a minute isn't repeated; anything about what you just did always shows.
    /// </summary>
    private static void ShowProblem(string text, bool background)
    {
        try
        {
            if (!ChatOpen && !ConsoleOpen) return;
            if (background && text == _lastShown && Time.unscaledTime - _lastShownAt < 30f) return;
            _lastShown = text;
            _lastShownAt = Time.unscaledTime;
            Ui.AddLine(LineKind.Error, "", text);
            Ui.ScrollToBottom(false);
            if (ConsoleOpen) lock (_problemsLock) _problemsUnseen = Math.Max(0, _problemsUnseen - 1); // seen as it happened
        }
        catch (Exception e) { Log.Debug("showing a problem: " + e.Message); }
    }

    /// <summary>"[CODE] message" (see <see cref="ProblemCodes"/>).</summary>
    public static string Coded(string message, string? code = null) => ProblemCodes.Coded(message, code);

    public static string CodeFor(string message) => ProblemCodes.CodeFor(message);

    /// <summary>The kept problems, oldest first (for /errors); marks them seen.</summary>
    private static List<string> ProblemLines(bool unseenOnly)
    {
        lock (_problemsLock)
        {
            var list = unseenOnly ? _problems.Skip(Math.Max(0, _problems.Count - _problemsUnseen)) : _problems;
            var lines = list.Select(p => $"{p.at:HH:mm}  {p.text}{(p.times > 1 ? $" (×{p.times})" : "")}").ToList();
            _problemsUnseen = 0;
            return lines;
        }
    }

    private static void ClearProblems()
    {
        lock (_problemsLock) { _problems.Clear(); _problemsUnseen = 0; }
    }
}
