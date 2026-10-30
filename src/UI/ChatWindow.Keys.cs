using System;
using System.Collections.Generic;
using NivalisAIChat.Game;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace NivalisAIChat.UI;

/// <summary>
/// CF-07/CF-08: the round buttons beside the chat and the console (the "i", the keyboard and the settings cog, in one column
/// outside the right edge; right of the info panel while it's open), the controller in these windows (the d-pad moves
/// between the buttons, A presses one, B leaves), and keyboard mode: their last line at the top, the on-screen keyboard in
/// the bottom half, with a short slide between the two.
/// </summary>
internal sealed partial class ChatWindow
{
    private RectTransform? _kbButton, _cogButton, _logRt, _inputRt, _lastLineRt, _kbArea;
    private Image? _kbButtonImg, _cogButtonImg;
    private CanvasGroup? _logFade, _lastLineFade, _kbFade;
    private TextMeshProUGUI? _lastLine, _kbLegend;
    private readonly KeyboardPanel _keys = new();
    private string _lastNpcBody = "";
    private bool _keyboardMode, _settingsClicked, _backPressed, _padSubmit, _suspended, _infoBeforeSuspend;
    /// <summary>0 = the normal chat, 1 = keyboard mode (eased between the two).</summary>
    private float _kbT;
    /// <summary>The button the controller is on (index into the visible ones), the keyboard button to begin with.</summary>
    private int _columnFocus = -1;
    private const float ColumnStep = 56f, SlideSeconds = 0.18f;

    public bool KeyboardMode => _keyboardMode;
    /// <summary>The settings cog is shown (only with Nivalis Switchboard installed: FB-40).</summary>
    public Func<bool> CogShown = () => true;

    private void BuildColumn()
    {
        if (_panel == null) return;
        (_kbButton, _kbButtonImg) = RoundButton("KeyboardButton", Sprites.KeyboardIcon);
        (_cogButton, _cogButtonImg) = RoundButton("SettingsButton", Sprites.CogIcon);
        PlaceColumn();
    }

    private (RectTransform rt, Image img) RoundButton(string name, Sprite icon)
    {
        var rt = Rect(_panel!, name, new Vector2(1, 1), new Vector2(1, 1), Vector2.zero, Vector2.zero);
        rt.pivot = new Vector2(0, 1);
        rt.sizeDelta = new Vector2(46, 46);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = Sprites.Pill;
        img.type = Image.Type.Sliced;
        var iconRt = Rect(rt, "Icon", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        iconRt.sizeDelta = new Vector2(28, 28);
        var iconImg = iconRt.gameObject.AddComponent<Image>();
        iconImg.sprite = icon;
        iconImg.color = NameColor;
        iconImg.raycastTarget = false;
        return (rt, img);
    }

    /// <summary>The buttons shown, top to bottom: the "i" (a chat, while its panel is closed), the keyboard, the cog, Give.</summary>
    private List<RectTransform> ColumnButtons()
    {
        var list = new List<RectTransform>(4);
        if (_infoButton != null && _infoButton.gameObject.activeSelf) list.Add(_infoButton);
        if (_kbButton != null) list.Add(_kbButton);
        if (_cogButton != null && _cogButton.gameObject.activeSelf) list.Add(_cogButton);
        if (_give != null && _give.gameObject.activeSelf) list.Add(_give);
        return list;
    }

    /// <summary>
    /// The column sits just outside the chat's right edge, under the "i"; while the info panel is open it moves to the right of
    /// the panel (the author, 2026-10-13: "not in between panels"), the keyboard button taking the "i"'s place at the top.
    /// </summary>
    private void PlaceColumn()
    {
        float x = 12f + (InfoOpen ? InfoGap + InfoW : 0f);
        float y = -20f;
        if (_infoButton != null && _infoButton.gameObject.activeSelf) { _infoButton.anchoredPosition = new Vector2(12, -20); y -= ColumnStep; }
        if (_kbButton != null) { _kbButton.anchoredPosition = new Vector2(x, y); y -= ColumnStep; }
        if (_cogButton != null)
        {
            bool cog = CogShown();
            if (_cogButton.gameObject.activeSelf != cog) _cogButton.gameObject.SetActive(cog);
            _cogButton.anchoredPosition = new Vector2(x, y);
        }
    }

    private void BuildKeyboardMode()
    {
        if (_panel == null) return;
        // Their last line, at the top of the window in keyboard mode.
        _lastLineRt = Rect(_panel, "LastLine", new Vector2(0, 0), new Vector2(1, 1), new Vector2(30, 314), new Vector2(-30, -74));
        _lastLineFade = _lastLineRt.gameObject.AddComponent<CanvasGroup>();
        _lastLineFade.alpha = 0f;
        _lastLineFade.blocksRaycasts = false;
        _lastLine = Text(_lastLineRt, "Text", 22, BodyColor, TextAlignmentOptions.BottomLeft, _bodyFont);
        Place(_lastLine.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        _lastLine.enableWordWrapping = true;
        _lastLine.overflowMode = TextOverflowModes.Ellipsis;
        _lastLine.enableAutoSizing = true;
        _lastLine.fontSizeMin = 16;
        _lastLine.fontSizeMax = 22;
        _lastLine.lineSpacing = 4f;
        _lastLineRt.gameObject.SetActive(false);

        // The keyboard, in the bottom half, with what the buttons do under it.
        _kbArea = Rect(_panel, "KeyboardArea", new Vector2(0, 0), new Vector2(1, 0), new Vector2(22, 34), new Vector2(-22, 214));
        _kbFade = _kbArea.gameObject.AddComponent<CanvasGroup>();
        _kbFade.alpha = 0f;
        var grid = Rect(_kbArea, "Grid", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        _keys.Build(grid, _bodyFont);
        _kbLegend = Text(_panel, "KeyboardLegend", 14, new Color32(138, 133, 125, 255), TextAlignmentOptions.Center, _bodyFont);
        Place(_kbLegend.rectTransform, new Vector2(0, 0), new Vector2(1, 0), new Vector2(30, 10), new Vector2(-30, 32));
        _kbLegend.enableWordWrapping = false;
        _kbLegend.gameObject.SetActive(false);
        _kbArea.gameObject.SetActive(false);
    }

    /// <summary>Into or out of keyboard mode (the keyboard button, the "close" key, B); instantly when the window opens or closes.</summary>
    public void SetKeyboardMode(bool on, bool instant = false)
    {
        if (_lastLineRt == null || _kbArea == null) return;
        if (on && !_keyboardMode)
        {
            _keys.Reset();
            _keys.SendLabel = "Send";
            if (_lastLine != null) _lastLine.text = _lastNpcBody.Length > 0 ? _lastNpcBody : $"<color={NoticeHex}><i>Nothing said yet.</i></color>";
        }
        _keyboardMode = on;
        if (_kbLegend != null)
            _kbLegend.text = $"D-pad / stick  move  ·  {Pad.Label("A")}  type  ·  {Pad.Label("LB")}  delete  ·  hold {Pad.Label("LT")}  capitals  ·  {Pad.Label("B")}  close the keyboard";
        if (instant) _kbT = on ? 1f : 0f;
        _focusInFrames = 0;
        LayoutKeyboardMode();
    }

    private static float Ease(float t) => t * t * (3f - 2f * t);

    /// <summary>Puts each part where it is at this point of the slide (0 = the normal chat, 1 = keyboard mode).</summary>
    private void LayoutKeyboardMode()
    {
        if (_logRt == null || _inputRt == null || _status == null || _kbArea == null || _lastLineRt == null) return;
        float t = Ease(Mathf.Clamp01(_kbT));
        bool any = t > 0.001f;
        if (_kbArea.gameObject.activeSelf != any) _kbArea.gameObject.SetActive(any);
        if (_kbLegend != null && _kbLegend.gameObject.activeSelf != any) _kbLegend.gameObject.SetActive(any);
        // The console keeps its log (shorter, above the keyboard); a chat shows only their last line.
        bool lastLine = any && !ConsoleMode;
        if (_lastLineRt.gameObject.activeSelf != lastLine) _lastLineRt.gameObject.SetActive(lastLine);
        static Vector2 L(Vector2 a, Vector2 b, float k) => Vector2.LerpUnclamped(a, b, k);
        _inputRt.offsetMin = L(new Vector2(24, 18), new Vector2(24, 226), t);
        _inputRt.offsetMax = L(new Vector2(-24, 64), new Vector2(-24, 272), t);
        _status.rectTransform.offsetMin = L(new Vector2(30, 72), new Vector2(30, 274), t);
        _status.rectTransform.offsetMax = L(new Vector2(-30, 100), new Vector2(-30, 300), t);
        if (ConsoleMode)
        {
            _logRt.offsetMin = L(new Vector2(26, 104), new Vector2(26, 302), t);
            if (_logFade != null) _logFade.alpha = 1f;
        }
        else
        {
            _logRt.offsetMin = new Vector2(26, 104);
            if (_logFade != null) _logFade.alpha = 1f - t;
            if (_lastLineFade != null) _lastLineFade.alpha = t;
        }
        if (_kbFade != null) _kbFade.alpha = t;
        _kbArea.offsetMin = new Vector2(22, 34 - 40f * (1f - t));
        _kbArea.offsetMax = new Vector2(-22, 214 - 40f * (1f - t));
        if (_give != null) _give.anchoredPosition = L(new Vector2(-24, 70), new Vector2(-24, 276), t);
    }

    /// <summary>The newest line they said (shown at the top in keyboard mode).</summary>
    private void NoteNpcLine(string body, bool fromHistory)
    {
        if (fromHistory && _lastNpcBody.Length > 0) return;
        _lastNpcBody = body;
        if (_keyboardMode && _lastLine != null) _lastLine.text = body;
    }

    /// <summary>The cog was clicked (or pressed with the controller) since this was last asked.</summary>
    public bool TakeSettingsClick()
    {
        bool c = _settingsClicked;
        _settingsClicked = false;
        return c;
    }

    /// <summary>B on the controller, outside keyboard mode with the info panel closed: leave the chat or close the console.</summary>
    public bool TakeBack()
    {
        bool b = _backPressed;
        _backPressed = false;
        return b;
    }

    /// <summary>
    /// The settings menu opened over this window (true), or closed again (false): hidden meanwhile with its info panel, and
    /// back as it was, keyboard mode included, the box focused again.
    /// </summary>
    public void Suspend(bool on)
    {
        if (_panel == null || on == _suspended) return;
        _suspended = on;
        if (on)
        {
            _infoBeforeSuspend = InfoOpen;
            try { _input?.DeactivateInputField(); } catch { }
            try { UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null); } catch { }
            if (_infoPanel != null) _infoPanel.gameObject.SetActive(false);
            _panel.gameObject.SetActive(false);
        }
        else
        {
            _panel.gameObject.SetActive(true);
            if (_infoBeforeSuspend) ShowInfo(true);
            _focusInFrames = 2;
            _settingsClicked = _backPressed = _padSubmit = false;
        }
    }

    /// <summary>The column's buttons with the mouse and the controller, and keyboard mode, once a frame.</summary>
    private void TickColumn()
    {
        _padSubmit = false;
        var mouse = Mouse.current;
        Vector2 pos = mouse != null ? mouse.position.ReadValue() : Vector2.zero;
        bool click = mouse != null && mouse.leftButton.wasPressedThisFrame;
        var buttons = ColumnButtons();
        if (_columnFocus < 0 || _columnFocus >= buttons.Count) _columnFocus = Math.Max(0, buttons.IndexOf(_kbButton!));
        bool pad = Pad.Active && !_keyboardMode;

        if (_keyboardMode)
        {
            if (Pad.B) { Pad.Consume(); SetKeyboardMode(false); }
            else if (_input != null && _input.interactable)
            {
                string text = _input.text ?? "";
                string before = text;
                var act = _keys.Tick(ref text, _input.characterLimit > 0 ? _input.characterLimit : 400);
                if (text != before) { _input.text = text; try { _input.MoveTextEnd(false); } catch { } }
                if (act == KeyAction.Send) _padSubmit = true;
                else if (act == KeyAction.Close) SetKeyboardMode(false);
            }
        }
        else if (Pad.Active)
        {
            if (Pad.Dy != 0 && buttons.Count > 0) _columnFocus = Math.Clamp(_columnFocus + Pad.Dy, 0, buttons.Count - 1);
            if (Pad.A && _columnFocus < buttons.Count) Press(buttons[_columnFocus]);
            else if (Pad.B)
            {
                if (InfoOpen) ShowInfo(false);
                else _backPressed = true;
            }
            if (Pad.Dy != 0 || Pad.A || Pad.B) Pad.Consume();
        }

        // Hover and focus look the same: the gold tint the "i" already uses.
        void Tint(Image? img, RectTransform? rt, bool on)
        {
            if (img == null || rt == null) return;
            bool focused = pad && _columnFocus < buttons.Count && buttons[_columnFocus] == rt;
            img.color = on || Over(rt, pos) || focused ? ButtonHover : ButtonIdle;
        }
        Tint(_kbButtonImg, _kbButton, _keyboardMode);
        Tint(_cogButtonImg, _cogButton, false);
        if (pad && _infoButtonImg != null && _infoButton != null && _columnFocus < buttons.Count && buttons[_columnFocus] == _infoButton) _infoButtonImg.color = ButtonHover;
        if (pad && _giveImg != null && _give != null && _columnFocus < buttons.Count && buttons[_columnFocus] == _give) _giveImg.color = ButtonHover;

        if (click && Over(_kbButton, pos)) SetKeyboardMode(!_keyboardMode);
        else if (click && Over(_cogButton, pos)) _settingsClicked = true;

        // The slide.
        float target = _keyboardMode ? 1f : 0f;
        if (!Mathf.Approximately(_kbT, target))
        {
            _kbT = Mathf.MoveTowards(_kbT, target, Time.unscaledDeltaTime / SlideSeconds);
            LayoutKeyboardMode();
            if (ConsoleMode) _chatScroll?.ToBottom(true);
        }
    }

    /// <summary>A column button pressed with the controller does what a click on it does.</summary>
    private void Press(RectTransform button)
    {
        if (button == _kbButton) SetKeyboardMode(!_keyboardMode);
        else if (button == _cogButton) _settingsClicked = true;
        else if (button == _infoButton) ShowInfo(!InfoOpen);
        else if (button == _give) _giveClicked = true;
    }

    /// <summary>The hint in the box: the keyboard's keys, or the controller's buttons while it's being used.</summary>
    private void RefreshHint()
    {
        if (_placeholder == null) return;
        string hint = !Pad.Active
            ? (ConsoleMode ? ConsoleHint : ChatHint)
            : ConsoleMode ? $"Type a command…      the keyboard button to type  ·  {Pad.Label("B")} to close"
            : $"Say something…      the keyboard button to type  ·  {Pad.Label("B")} to leave";
        if (_placeholder.text != hint) _placeholder.text = hint;
    }
}
