using System;

namespace NivalisAIChat.UI;

/// <summary>
/// CF-07: a d-pad direction or a stick held over: one step at once, then (still held) another after a short wait, then
/// steadily faster ones, the way a held arrow key repeats. Letting go, or turning to the other way, starts over. No Unity.
/// </summary>
public sealed class PadRepeat
{
    /// <summary>The wait before a held direction repeats, and between the repeats after it (seconds).</summary>
    public float First = 0.38f, Every = 0.085f;
    private int _dir;
    private float _next;

    /// <summary>The step to take this frame (-1, 0 or +1) for the direction held now (-1, 0 or +1).</summary>
    public int Step(int held, float now)
    {
        held = Math.Sign(held);
        if (held == 0) { _dir = 0; return 0; }
        if (held != _dir) { _dir = held; _next = now + First; return held; }
        if (now < _next) return 0;
        _next = now + Every;
        return held;
    }

    /// <summary>
    /// The direction a stick points (-1, 0, +1 on each axis): the stronger axis only, once it's past the dead zone, so a
    /// slightly diagonal push moves one way. Y up is +1 (as the stick reads).
    /// </summary>
    public static (int x, int y) StickDirection(float x, float y, float deadZone = 0.5f)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) return (0, 0);
        if (Math.Abs(x) < deadZone && Math.Abs(y) < deadZone) return (0, 0);
        return Math.Abs(x) >= Math.Abs(y) ? (Math.Sign(x), 0) : (0, Math.Sign(y));
    }

    /// <summary>
    /// CF-12: a button setting as A, B, X, Y, LB, RB, LT, RT or OFF (anything else: off), by its Xbox name, its PlayStation
    /// name, its place on the pad or Unity's old JoystickButton number ("X (Square)", "Square", "West", "JoystickButton2" are
    /// all X). Interact is the game's own interact button, the bottom face button (A on Xbox, Cross on PlayStation). A bare
    /// "X" is the Xbox X (the left face button): PlayStation's bottom button is Cross.
    /// </summary>
    public static string ButtonName(string? setting)
    {
        string s = (setting ?? "").Trim().ToUpperInvariant();
        int paren = s.IndexOf('(');
        if (paren > 0) s = s[..paren].Trim(); // "X (Square)": the Xbox name first
        return s switch
        {
            "INTERACT" or "AUTO" or "A" or "CROSS" or "SOUTH" or "BUTTONSOUTH" or "JOYSTICKBUTTON0" => "A",
            "B" or "CIRCLE" or "EAST" or "BUTTONEAST" or "JOYSTICKBUTTON1" => "B",
            "X" or "SQUARE" or "WEST" or "BUTTONWEST" or "JOYSTICKBUTTON2" => "X",
            "Y" or "TRIANGLE" or "NORTH" or "BUTTONNORTH" or "JOYSTICKBUTTON3" => "Y",
            "LB" or "L1" or "LEFTSHOULDER" or "JOYSTICKBUTTON4" => "LB",
            "RB" or "R1" or "RIGHTSHOULDER" or "JOYSTICKBUTTON5" => "RB",
            "RT" or "R2" or "RIGHTTRIGGER" => "RT",
            "LT" or "L2" or "LEFTTRIGGER" => "LT",
            _ => "OFF",
        };
    }

    /// <summary>The Input System's name for a button as <see cref="ButtonName"/> names it (the game's icons are keyed by it).</summary>
    public static string? ControlName(string button) => button switch
    {
        "A" => "buttonSouth", "B" => "buttonEast", "X" => "buttonWest", "Y" => "buttonNorth",
        "LB" => "leftShoulder", "RB" => "rightShoulder", "LT" => "leftTrigger", "RT" => "rightTrigger",
        _ => null,
    };

    /// <summary>
    /// A pad button's name as that pad shows it. PlayStation: R2, L2, R1, L1, and X (the cross), Circle, Square, Triangle;
    /// Xbox and others: RT, LT, RB, LB, A, B, X, Y. The two layouts sit in the same places.
    /// </summary>
    public static string Label(string button, bool playStation)
    {
        string b = (button ?? "").Trim().ToUpperInvariant();
        if (!playStation) return b;
        return b switch
        {
            "RT" => "R2", "LT" => "L2", "RB" => "R1", "LB" => "L1",
            "A" => "X", "B" => "Circle", "X" => "Square", "Y" => "Triangle",
            _ => b,
        };
    }
}
