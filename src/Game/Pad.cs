using System;
using NivalisAIChat.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NivalisAIChat.Game;

/// <summary>
/// CF-07: the controller, read once at the start of each frame for the mod's windows: a move (the d-pad or the left stick,
/// repeating while held), the buttons pressed this frame, LT held (capitals on the keyboard), the right stick (scrolling),
/// and whether the controller was used last (so the windows show their highlight and the talk prompt shows the pad's
/// button). A window that acts on a press calls <see cref="Consume"/>, so nothing opened by it acts on the same press.
/// </summary>
internal static class Pad
{
    private static readonly PadRepeat RepeatX = new(), RepeatY = new();

    /// <summary>This frame's move: -1/+1 across (left/right) and down (+1) / up (-1), screen-wise.</summary>
    public static int Dx, Dy;
    public static bool A, B, X, Y, LB, RB, LT, RT, Start;
    public static bool LTHeld;
    /// <summary>The right stick up (+) or down (-), for scrolling.</summary>
    public static float ScrollY;
    /// <summary>The controller was used last (a key or the mouse since turns this off).</summary>
    public static bool Active { get; private set; }
    public static bool Connected { get; private set; }
    public static bool PlayStation { get; private set; }

    private static int _frame = -1;
    private static IntPtr _padSeen;

    public static void Tick()
    {
        if (_frame == Time.frameCount) return;
        _frame = Time.frameCount;
        Clear();
        LTHeld = false;
        ScrollY = 0f;
        Gamepad? pad = null;
        try { pad = Gamepad.current; } catch { }
        Connected = pad != null;
        if (pad == null) { RepeatX.Step(0, 0); RepeatY.Step(0, 0); Active = false; return; }
        try
        {
            if (pad.Pointer != _padSeen)
            {
                _padSeen = pad.Pointer;
                try { PlayStation = pad.TryCast<UnityEngine.InputSystem.DualShock.DualShockGamepad>() != null; } catch { PlayStation = false; }
            }
            float now = Time.unscaledTime;
            var stick = pad.leftStick.ReadValue();
            var (sx, sy) = PadRepeat.StickDirection(stick.x, stick.y);
            int hx = pad.dpad.left.isPressed ? -1 : pad.dpad.right.isPressed ? 1 : sx;
            int hy = pad.dpad.up.isPressed ? -1 : pad.dpad.down.isPressed ? 1 : -sy; // the stick's up is +y; a move up the screen is -1
            Dx = RepeatX.Step(hx, now);
            Dy = RepeatY.Step(hy, now);
            A = pad.buttonSouth.wasPressedThisFrame;
            B = pad.buttonEast.wasPressedThisFrame;
            X = pad.buttonWest.wasPressedThisFrame;
            Y = pad.buttonNorth.wasPressedThisFrame;
            LB = pad.leftShoulder.wasPressedThisFrame;
            RB = pad.rightShoulder.wasPressedThisFrame;
            LT = pad.leftTrigger.wasPressedThisFrame;
            RT = pad.rightTrigger.wasPressedThisFrame;
            Start = pad.startButton.wasPressedThisFrame;
            LTHeld = pad.leftTrigger.isPressed;
            float ry = pad.rightStick.ReadValue().y;
            ScrollY = Mathf.Abs(ry) > 0.25f ? ry : 0f;
            if (Dx != 0 || Dy != 0 || A || B || X || Y || LB || RB || LT || RT || Start || ScrollY != 0f) Active = true;
        }
        catch { Clear(); }
        try
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if ((kb != null && kb.anyKey.wasPressedThisFrame)
                || (mouse != null && (mouse.leftButton.wasPressedThisFrame || mouse.delta.ReadValue().sqrMagnitude > 9f)))
                Active = false;
        }
        catch { }
    }

    /// <summary>This frame's presses are used up (a window acted on them): nothing else acts on them this frame.</summary>
    public static void Consume() => Clear();

    private static void Clear()
    {
        Dx = Dy = 0;
        A = B = X = Y = LB = RB = LT = RT = Start = false;
    }

    /// <summary>A button named in the settings (PadTalkButton, PadConsoleButton) pressed this frame; off never.</summary>
    public static bool Pressed(string? setting) => Normal(setting) switch
    {
        "RT" => RT,
        "LT" => LT,
        "A" => A,
        "B" => B,
        "X" => X,
        "Y" => Y,
        "LB" => LB,
        "RB" => RB,
        _ => false,
    };

    /// <summary>
    /// A button setting as A, B, X, Y, LB, RB, LT, RT or OFF (anything else: off), by its Xbox name, its PlayStation name,
    /// its place on the pad or Unity's old JoystickButton number ("X (Square)", "Square", "West", "JoystickButton2" are all
    /// X). Interact is the game's own interact button, the bottom face button on any pad (A on Xbox, Cross on PlayStation).
    /// A bare "X" is the Xbox X (the left face button): PlayStation's bottom button is Cross.
    /// </summary>
    public static string Normal(string? setting) => PadRepeat.ButtonName(setting);

    /// <summary>The button (as <see cref="Normal"/> names it) held down on this controller this frame, read straight from it.</summary>
    public static bool PressedNow(Gamepad pad, string button) => button switch
    {
        "A" => pad.buttonSouth.wasPressedThisFrame,
        "B" => pad.buttonEast.wasPressedThisFrame,
        "X" => pad.buttonWest.wasPressedThisFrame,
        "Y" => pad.buttonNorth.wasPressedThisFrame,
        "LB" => pad.leftShoulder.wasPressedThisFrame,
        "RB" => pad.rightShoulder.wasPressedThisFrame,
        "LT" => pad.leftTrigger.wasPressedThisFrame,
        "RT" => pad.rightTrigger.wasPressedThisFrame,
        _ => false,
    };

    /// <summary>FB-64: the button (as <see cref="Normal"/> names it) held down on this controller now.</summary>
    public static bool HeldNow(Gamepad pad, string button) => button switch
    {
        "A" => pad.buttonSouth.isPressed,
        "B" => pad.buttonEast.isPressed,
        "X" => pad.buttonWest.isPressed,
        "Y" => pad.buttonNorth.isPressed,
        "LB" => pad.leftShoulder.isPressed,
        "RB" => pad.rightShoulder.isPressed,
        "LT" => pad.leftTrigger.isPressed,
        "RT" => pad.rightTrigger.isPressed,
        _ => false,
    };

    /// <summary>A button as this controller shows it (R2 on a PlayStation pad, RT on others).</summary>
    public static string Label(string button) => PadRepeat.Label(button, PlayStation);
}
