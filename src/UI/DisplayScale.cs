using System;
using System.Runtime.InteropServices;

namespace NivalisAIChat.UI;

/// <summary>
/// Windows' display scale for the game's window (100% = 1, 150% = 1.5), read from the window's DPI: the phone's smallest
/// size follows it. Read at most every few seconds (it only changes when the window moves to another screen or the
/// setting changes); 1 when it can't be read (older Windows, no window).
/// </summary>
internal static class DisplayScale
{
    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    private static float _value = 1f;
    private static float _nextRead;
    private static bool _broken;

    public static float Windows()
    {
        if (_broken) return 1f;
        float now = UnityEngine.Time.unscaledTime;
        if (now < _nextRead) return _value;
        _nextRead = now + 3f;
        try
        {
            var hwnd = GetActiveWindow();
            if (hwnd == IntPtr.Zero) return _value;
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi >= 48 && dpi <= 960) _value = dpi / 96f;
        }
        catch (Exception) { _broken = true; _value = 1f; }
        return _value;
    }
}
