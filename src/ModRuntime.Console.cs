using System;
using System.Linq;
using NivalisAIChat.Game;
using NivalisAIChat.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NivalisAIChat;

/// <summary>
/// The command console: the ` key (ConsoleKey) opens a window like the chat's, where /help lists every command and
/// settings such as /chatter are changed and saved. You stand still while it's open, as in a conversation.
/// </summary>
public static partial class ModRuntime
{
    private static bool _consoleOpen;
    private static int _consoleOpenedFrame;
    private static Key _consoleKey = Key.Backquote;
    /// <summary>The console key types a character (/, -, a numpad key...): inside the console only Esc closes it (findings #28).</summary>
    private static bool _consoleKeyTypes;

    internal static bool ConsoleOpen => _consoleOpen;

    private static void ParseConsoleKey()
    {
        string k = (_cfg.ConsoleKey.Value ?? "Backquote").Trim();
        if (k == "`" || k == "~") k = "Backquote";
        if (k.Length == 1 && char.IsDigit(k[0])) k = "Digit" + k;
        _consoleKey = Enum.TryParse<Key>(k, true, out var parsed) && parsed != Key.None ? parsed : Key.Backquote;
        // A letter or digit would close the console as soon as you typed it in a command.
        bool typed = _consoleKey is >= Key.A and <= Key.Z || _consoleKey is >= Key.Digit1 and <= Key.Digit0 || _consoleKey == Key.Space;
        if (_consoleKey == _talkKey || typed)
        {
            if (typed) Log.Warn($"ConsoleKey can't be a letter, digit or Space ({_cfg.ConsoleKey.Value}); using Backquote (`).");
            _consoleKey = Key.Backquote;
        }
        _consoleKeyTypes = _consoleKey is Key.Slash or Key.Backslash or Key.Minus or Key.Equals or Key.Period or Key.Comma or Key.Semicolon
            or Key.Quote or Key.LeftBracket or Key.RightBracket or Key.OEM1 or Key.OEM2 or Key.OEM3 or Key.OEM4 or Key.OEM5
            or (>= Key.Numpad0 and <= Key.Numpad9) or Key.NumpadDivide or Key.NumpadMultiply or Key.NumpadPlus or Key.NumpadMinus
            or Key.NumpadPeriod or Key.NumpadEquals;
    }

    /// <summary>The console key, pressed during normal play (not in a menu, not in a conversation).</summary>
    private static bool ConsoleKeyPressed()
    {
        var kb = Keyboard.current;
        return kb != null && Pressed(kb, ref _consoleKey, Key.Backquote, "ConsoleKey");
    }

    /// <summary>Counts each time the console opens (a late answer goes only to the console that asked: /voices).</summary>
    private static int _consoleSession;

    private static void OpenConsole()
    {
        if (ChatOpen || _consoleOpen || _phoneOpen || !Ui.Built) return;
        _consoleOpen = true;
        _consoleSession++;
        _consoleOpenedFrame = Time.frameCount;
        Ui.HideBark();
        Ui.OpenConsole("People of Nivalis", "Console");
        Locks.Engage(false);
        var s = ChatterSettingsNow();
        Ui.AddLine(LineKind.Output, "", $"Type /help to see every command. Residents' conversations are {(s.Enabled ? "on" : "off")} (/chatter).");
        // Anything that went wrong since you last looked (/errors lists the rest).
        var fresh = ProblemLines(unseenOnly: true);
        if (fresh.Count > 0)
        {
            Ui.AddLine(LineKind.Error, "", fresh.Count == 1 ? "Something went wrong since you last looked:" : $"{fresh.Count} problems since you last looked:");
            foreach (var line in fresh.TakeLast(6)) Ui.AddLine(LineKind.Error, "", line);
            if (fresh.Count > 6) Ui.AddLine(LineKind.Output, "", "/errors shows them all.");
        }
        Ui.ScrollToBottom(true);
    }

    private static void CloseConsole()
    {
        if (!_consoleOpen) return;
        _consoleOpen = false;
        try { Ui.Close(); } catch { }
        try { Locks.Release(); } catch (Exception e) { Log.Warn("Releasing player locks failed: " + e.Message); }
    }

    private static void UpdateConsole()
    {
        Locks.KeepCursor();
        var kb = Keyboard.current;
        // A console key that types a character (ConsoleKey = Slash) opens it, but only Esc closes it: you type it in commands.
        if (kb != null && Time.frameCount > _consoleOpenedFrame + 1 && (kb.escapeKey.wasPressedThisFrame || (!_consoleKeyTypes && Pressed(kb, ref _consoleKey, Key.Backquote, "ConsoleKey"))))
        {
            CloseConsole();
            return;
        }
        if (Ui.TakeBack()) { CloseConsole(); return; } // B on the controller
        if (Ui.TakeSettingsClick()) { OpenSettings(line => Ui.AddLine(LineKind.Output, "", line)); return; } // back here when it closes
        if (!Ui.SubmitPressed()) return;
        string text = Ui.TakeInput().Replace("`", "").Trim();
        if (text.Length == 0) return;
        if (!text.StartsWith("/")) text = "/" + text; // "help" works too
        Ui.AddLine(LineKind.Player, "You", text);
        try { Command(text, line => Ui.AddLine(LineKind.Output, "", line)); }
        catch (Exception e) { Ui.AddLine(LineKind.Error, "", "That command failed: " + e.Message); Log.Warn("Console command failed: " + e); }
        Ui.ScrollToBottom(false);
    }
}
