using System;
using System.Collections.Generic;
using System.Linq;
using Nivalis;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace NivalisAIChat.UI;

/// <summary>
/// FB-76, world chat's line (the author: "a line plus who's listening so I can select
/// who I want to talk to ... being able to select multiples ... an all option"): a thin line at the bottom centre with, above
/// it, a chip for each person around you: gold = who you're talking to (click to add or take away; Tab moves it; All picks
/// everyone in view), plain = in view (may chip in), faded = only close enough to overhear. Its own canvas: the chat
/// window's showing hides every speech bubble, and this must not. Polled each frame like the other windows.
/// </summary>
internal sealed class WorldChatLine
{
    public enum ChipState { InView, Overhears }

    private Key _next = Key.Tab;
    private TextMeshProUGUI? _keys;

    /// <summary>FB-81: the key that moves who you're talking to (WorldChatNextKey; Tab), as the line says it too.</summary>
    public Key NextKey
    {
        get => _next;
        set { _next = value; if (_keys != null) _keys.text = KeysText(); }
    }

    private string KeysText() => $"Enter say · {(_next == Key.Tab ? "Tab" : _next.ToString())} who · Esc";

    public sealed class Chip
    {
        public string Id = "", Label = "";
        public ChipState State;
        public bool Selected;
    }

    public const string AllId = "*all*";
    private static readonly Color32 Fill = new(22, 20, 18, 235), Gold = new(196, 160, 98, 255), GoldDim = new(118, 97, 62, 255), NameC = new(217, 165, 90, 255),
        TextC = new(238, 232, 223, 255), BodyC = new(207, 203, 196, 255), Muted = new(154, 148, 139, 255), Edge = new(58, 52, 45, 255), White = new(255, 255, 255, 255);
    private const float W = 560, LineH = 36, ChipH = 24, Gap = 6;

    private GameObject? _root;
    private CanvasScaler? _scaler;
    private RectTransform? _wrap, _chipRow;
    private TMP_InputField? _input;
    private TextMeshProUGUI? _status;
    private TMP_FontAsset? _title, _body;
    private readonly List<(RectTransform rt, Image img, TextMeshProUGUI text, CanvasGroup fade)> _chipViews = new();
    private List<Chip> _chips = new();
    private int _openedFrame = -1;
    private string? _submit, _click;
    private bool _cycle;

    public bool Visible => _root != null && _root.activeSelf;

    /// <summary>Your typed text's box has the keys (every key types).</summary>
    public bool Typing => Visible && _input != null && _input.isFocused;

    public void Build((TMP_FontAsset? title, TMP_FontAsset? body) fonts)
    {
        if (_root != null) return;
        (_title, _body) = fonts;
        _root = new GameObject("NivalisAIChat.WorldChat");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 31980; // under the phone (31990) and the chat window (32000)
        _scaler = _root.AddComponent<CanvasScaler>();
        _scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        _root.AddComponent<GraphicRaycaster>();

        _wrap = new GameObject("Wrap").AddComponent<RectTransform>();
        _wrap.SetParent(_root.transform, false);
        _wrap.anchorMin = _wrap.anchorMax = new Vector2(0.5f, 0);
        _wrap.pivot = new Vector2(0.5f, 0);
        _wrap.anchoredPosition = new Vector2(0, 46);
        _wrap.sizeDelta = new Vector2(W, LineH + Gap + ChipH + 20);

        _status = Text(_wrap, "Status", 13, Muted, TextAlignmentOptions.Bottom);
        Place(_status.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -18), new Vector2(0, 0));

        _chipRow = new GameObject("Chips").AddComponent<RectTransform>();
        _chipRow.SetParent(_wrap, false);
        Place(_chipRow, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, LineH + Gap), new Vector2(0, LineH + Gap + ChipH));

        var line = new GameObject("Line").AddComponent<RectTransform>();
        line.SetParent(_wrap, false);
        Place(line, new Vector2(0, 0), new Vector2(1, 0), Vector2.zero, new Vector2(0, LineH));
        var bg = line.gameObject.AddComponent<Image>();
        bg.sprite = Sprites.Round(LineH / 2, 1.5f, Fill, Gold);
        bg.type = Image.Type.Sliced;
        bg.raycastTarget = false;
        _input = BuildInput(line);
        var keys = Text(line, "Keys", 12, Muted, TextAlignmentOptions.MidlineRight);
        Place(keys.rectTransform, new Vector2(1, 0), new Vector2(1, 1), new Vector2(-150, 0), new Vector2(-14, 0));
        _keys = keys;
        keys.text = KeysText();
        _root.SetActive(false);
    }

    private TMP_InputField BuildInput(RectTransform parent)
    {
        var go = new GameObject("Input");
        go.SetActive(false);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        Place(rt, Vector2.zero, Vector2.one, new Vector2(16, 0), new Vector2(-156, 0));
        var area = new GameObject("Text Area").AddComponent<RectTransform>();
        area.SetParent(rt, false);
        Place(area, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        area.gameObject.AddComponent<RectMask2D>();
        var text = Text(area, "Text", 15, TextC, TextAlignmentOptions.MidlineLeft);
        Place(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        text.enableWordWrapping = false;
        var ph = Text(area, "Placeholder", 15, Muted, TextAlignmentOptions.MidlineLeft);
        Place(ph.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        ph.fontStyle = FontStyles.Italic;
        ph.text = "Say something…";
        ph.enableWordWrapping = false;
        var input = go.AddComponent<TMP_InputField>();
        input.textViewport = area;
        input.textComponent = text;
        input.placeholder = ph;
        if (_body != null) input.fontAsset = _body;
        input.pointSize = 15;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.characterLimit = 300;
        input.richText = false;
        input.onFocusSelectAll = false;
        input.resetOnDeActivation = false;
        input.caretWidth = 2;
        input.customCaretColor = true;
        input.caretColor = NameC;
        input.selectionColor = new Color(NameC.r / 255f, NameC.g / 255f, NameC.b / 255f, 0.3f);
        var nav = input.navigation;
        nav.mode = Navigation.Mode.None;
        input.navigation = nav;
        try { go.AddComponent<InputFieldPreventExternalInput>(); }
        catch (Exception e) { Log.Debug("world chat prevent-input: " + e.Message); }
        go.SetActive(true);
        return input;
    }

    public void Open()
    {
        if (_root == null) return;
        _root.SetActive(true);
        _openedFrame = Time.frameCount;
        if (_input != null) _input.text = "";
        _submit = null; _click = null; _cycle = false;
        SetStatus("");
        Focus();
    }

    public void Close()
    {
        if (_root == null) return;
        try { if (_input != null && _input.isFocused) _input.DeactivateInputField(); } catch { }
        try { EventSystem.current?.SetSelectedGameObject(null); } catch { }
        _root.SetActive(false);
    }

    public void SetStatus(string text) { if (_status != null) _status.text = text ?? ""; }

    /// <summary>Your words back in the box (they couldn't go yet).</summary>
    public void PutBack(string text) { if (_input != null) { _input.text = text ?? ""; try { _input.caretPosition = _input.text.Length; } catch { } } }

    /// <summary>The people around you, as chips (in view first); an All chip after them when two or more are in view.</summary>
    public void SetChips(List<Chip> chips)
    {
        _chips = chips;
        if (_chipRow == null) return;
        var shown = chips.Take(6).ToList();
        int inView = shown.Count(c => c.State == ChipState.InView);
        bool allChip = inView >= 2;
        int n = shown.Count + (allChip ? 1 : 0);
        while (_chipViews.Count < n) _chipViews.Add(MakeChip());
        float x = 0, total = 0;
        var widths = new List<float>();
        for (int i = 0; i < n; i++)
        {
            bool isAll = allChip && i == shown.Count;
            string label = isAll ? "All" : shown[i].Label;
            var (rt, img, text, fade) = _chipViews[i];
            text.text = label;
            widths.Add(Mathf.Clamp(text.GetPreferredValues(label).x + 22, 40, 180));
        }
        // The 2026-10-20 audit: squeezed to one row of the line's width (a long label ends in "…").
        var fitted = Social.ViewCone.Fit(widths.ToArray(), W, 6);
        for (int i = 0; i < n; i++) { widths[i] = fitted[i]; total += fitted[i] + (i > 0 ? 6 : 0); }
        x = Mathf.Max(0, (W - total) / 2);
        for (int i = 0; i < _chipViews.Count; i++)
        {
            var (rt, img, text, fade) = _chipViews[i];
            bool on = i < n;
            rt.gameObject.SetActive(on);
            if (!on) continue;
            bool isAll = allChip && i == shown.Count;
            bool selected = isAll ? shown.Where(c => c.State == ChipState.InView).All(c => c.Selected) : shown[i].Selected;
            bool overhears = !isAll && shown[i].State == ChipState.Overhears;
            rt.anchoredPosition = new Vector2(x, 0);
            rt.sizeDelta = new Vector2(widths[i], ChipH);
            img.sprite = Sprites.Round(ChipH / 2, selected ? 1.5f : 1f, Fill, selected ? Gold : Edge);
            text.color = selected ? NameC : BodyC;
            text.fontStyle = selected ? FontStyles.Bold : FontStyles.Normal;
            fade.alpha = overhears ? 0.55f : 1f;
            rt.name = isAll ? AllId : shown[i].Id;
            x += widths[i] + 6;
        }
    }

    private (RectTransform, Image, TextMeshProUGUI, CanvasGroup) MakeChip()
    {
        var rt = new GameObject("Chip").AddComponent<RectTransform>();
        rt.SetParent(_chipRow!, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0, 0);
        rt.pivot = new Vector2(0, 0);
        var img = rt.gameObject.AddComponent<Image>();
        img.type = Image.Type.Sliced;
        img.raycastTarget = false;
        var fade = rt.gameObject.AddComponent<CanvasGroup>();
        fade.blocksRaycasts = false;
        var text = Text(rt, "Label", 13, BodyC, TextAlignmentOptions.Center);
        Place(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(8, 0), new Vector2(-8, 0));
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        return (rt, img, text, fade);
    }

    /// <summary>Your line when you pressed Enter ("" = Enter on an empty line: close); null when nothing was sent.</summary>
    public string? TakeSubmit() { var s = _submit; _submit = null; return s; }

    /// <summary>A chip you clicked (its id, or <see cref="AllId"/>); null for none.</summary>
    public string? TakeClick() { var c = _click; _click = null; return c; }

    /// <summary>Tab was pressed (move who you're talking to, to the next one in view).</summary>
    public bool TakeCycle() { bool c = _cycle; _cycle = false; return c; }

    public void Tick()
    {
        if (!Visible || _input == null) return;
        if (_scaler != null) _scaler.scaleFactor = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2.5f);
        var kb = Keyboard.current;
        if (!_input.isFocused) Focus(); // CF-15's lesson: it gives the keys back to the box itself
        if (kb != null && Time.frameCount != _openedFrame)
        {
            if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
            {
                _submit = _input.text.Trim();
                _input.text = "";
            }
            if (kb[NextKey].wasPressedThisFrame) _cycle = true; // FB-81: WorldChatNextKey
        }
        var mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame)
        {
            Vector2 p = mouse.position.ReadValue();
            foreach (var (rt, _, _, _) in _chipViews)
                if (rt.gameObject.activeSelf && RectTransformUtility.RectangleContainsScreenPoint(rt, p, null)) { _click = rt.name; break; }
        }
    }

    private void Focus()
    {
        try { EventSystem.current?.SetSelectedGameObject(_input!.gameObject); } catch { }
        try { _input!.ActivateInputField(); } catch { }
    }

    private TextMeshProUGUI Text(Transform parent, string name, float size, Color color, TextAlignmentOptions align)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        if (_body != null) t.font = _body;
        t.fontSize = size;
        t.color = color;
        t.alignment = align;
        t.raycastTarget = false;
        t.richText = false;
        t.parseCtrlCharacters = false;
        return t;
    }

    private static void Place(RectTransform rt, Vector2 min, Vector2 max, Vector2 offMin, Vector2 offMax)
    {
        rt.anchorMin = min; rt.anchorMax = max; rt.offsetMin = offMin; rt.offsetMax = offMax;
    }

    public void Destroy()
    {
        if (_root != null) try { UnityEngine.Object.Destroy(_root); } catch { }
        _root = null;
        _chipViews.Clear();
    }
}
