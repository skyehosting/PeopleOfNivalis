using System;
using System.Collections.Generic;
using NivalisAIChat.Game;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace NivalisAIChat.UI;

/// <summary>
/// CF-07: the on-screen keyboard (<see cref="KeyboardLayout"/>) drawn into a window: the chat, the console and the settings
/// menu each have one. The d-pad or the left stick moves the highlight, A types the key, LB/L1 deletes, LT/L2 held makes
/// capitals; the mouse can click keys too. It doesn't own the text: each frame the window hands its text in and gets it back.
/// </summary>
internal sealed class KeyboardPanel
{
    private static readonly Color KeyText = new Color32(207, 203, 196, 255);
    private static readonly Color KeyTextOn = Color.white;
    // FB-53: the chosen key: a bold gold border and a blue letter over a light fill (FB-51's solid gold hid the letter).
    private static readonly Color Glow = new Color32(196, 160, 98, 70);
    private static readonly Color KeyTextChosen = new Color32(111, 180, 255, 255);
    private static readonly Color EdgeChosen = new Color32(226, 186, 110, 255);
    private static readonly Color GlowHover = new Color32(196, 160, 98, 45);

    private sealed class KeyView
    {
        public RectTransform Rt = null!;
        public Image Glow = null!;
        public Image? Edge;
        public TextMeshProUGUI Label = null!;
        public PadKey Key = null!;
        public (int row, int index) At;
    }

    private RectTransform? _root;
    private readonly RectTransform?[] _layers = new RectTransform?[2];
    private readonly List<KeyView>[] _views = { new(), new() };
    private bool _symbols, _shiftShown;
    private (int row, int index) _at = (1, 4);
    /// <summary>What the Send key says (the settings menu's says Done).</summary>
    public string SendLabel = "Send";

    public RectTransform? Root => _root;
    public bool Built => _root != null;

    /// <summary>Builds the keyboard filling <paramref name="parent"/> (its four rows share the height).</summary>
    public void Build(RectTransform parent, TMP_FontAsset? font)
    {
        var go = new GameObject("Keyboard");
        _root = go.AddComponent<RectTransform>();
        _root.SetParent(parent, false);
        _root.anchorMin = Vector2.zero;
        _root.anchorMax = Vector2.one;
        _root.offsetMin = _root.offsetMax = Vector2.zero;
        for (int layer = 0; layer < 2; layer++)
        {
            var lgo = new GameObject(layer == 0 ? "Letters" : "Symbols");
            var lrt = lgo.AddComponent<RectTransform>();
            lrt.SetParent(_root, false);
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = lrt.offsetMax = Vector2.zero;
            _layers[layer] = lrt;
            var rows = KeyboardLayout.Rows(layer == 1);
            for (int r = 0; r < rows.Count; r++)
            {
                int start = 0;
                for (int i = 0; i < rows[r].Count; i++)
                {
                    var key = rows[r][i];
                    _views[layer].Add(MakeKey(lrt, key, r, i, start, rows.Count, font));
                    start += key.Span;
                }
            }
            lgo.SetActive(layer == 0);
        }
        Refresh(force: true);
    }

    private KeyView MakeKey(RectTransform parent, PadKey key, int row, int index, int start, int rowCount, TMP_FontAsset? font)
    {
        var go = new GameObject("Key " + key.Label);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(start / (float)KeyboardLayout.Width, 1f - (row + 1) / (float)rowCount);
        rt.anchorMax = new Vector2((start + key.Span) / (float)KeyboardLayout.Width, 1f - row / (float)rowCount);
        rt.offsetMin = new Vector2(3, 3);
        rt.offsetMax = new Vector2(-3, -3);
        var bg = go.AddComponent<Image>();
        bg.sprite = Sprites.Field;
        bg.type = Image.Type.Sliced;
        var glowGo = new GameObject("Glow");
        var grt = glowGo.AddComponent<RectTransform>();
        grt.SetParent(rt, false);
        grt.anchorMin = Vector2.zero;
        grt.anchorMax = Vector2.one;
        grt.offsetMin = grt.offsetMax = Vector2.zero;
        var glow = glowGo.AddComponent<Image>();
        glow.sprite = Sprites.Field;
        glow.type = Image.Type.Sliced;
        glow.color = Glow;
        glow.raycastTarget = false;
        glow.enabled = false;
        var edgeGo = new GameObject("Edge");
        var ert = edgeGo.AddComponent<RectTransform>();
        ert.SetParent(rt, false);
        ert.anchorMin = Vector2.zero;
        ert.anchorMax = Vector2.one;
        ert.offsetMin = ert.offsetMax = Vector2.zero;
        var edge = edgeGo.AddComponent<Image>();
        edge.sprite = Sprites.KeyEdge;
        edge.type = Image.Type.Sliced;
        edge.color = EdgeChosen;
        edge.raycastTarget = false;
        edge.enabled = false;
        var lgo = new GameObject("Label");
        var lrt = lgo.AddComponent<RectTransform>();
        lrt.SetParent(rt, false);
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.offsetMin = lrt.offsetMax = Vector2.zero;
        var label = lgo.AddComponent<TextMeshProUGUI>();
        if (font != null) label.font = font;
        label.fontSize = key.Action == KeyAction.Char ? 21 : 16;
        label.fontStyle = FontStyles.Bold;
        label.alignment = TextAlignmentOptions.Center;
        label.color = key.Action == KeyAction.Char ? KeyText : new Color32(150, 145, 136, 255);
        label.raycastTarget = false;
        label.richText = false;
        label.text = key.Label;
        return new KeyView { Rt = rt, Glow = glow, Edge = edge, Label = label, Key = key, At = (row, index) };
    }

    /// <summary>Back to the letters, the highlight on the middle row.</summary>
    public void Reset()
    {
        _symbols = false;
        _at = (1, 4);
        if (_layers[0] != null) _layers[0]!.gameObject.SetActive(true);
        if (_layers[1] != null) _layers[1]!.gameObject.SetActive(false);
        Refresh(force: true);
    }

    /// <summary>
    /// One frame: moves, types and deletes on <paramref name="text"/> (up to <paramref name="maxLength"/> characters).
    /// Returns Send or Close when one of those keys was chosen this frame, otherwise null. Uses up the pad's presses it acts on.
    /// </summary>
    public KeyAction? Tick(ref string text, int maxLength)
    {
        if (_root == null || !_root.gameObject.activeInHierarchy) return null;
        text ??= "";
        bool shift = Pad.LTHeld || ShiftKeyHeld();
        KeyAction? result = null;
        PadKey? typed = null;
        bool layerChanged = false;

        if (Pad.Dx != 0 || Pad.Dy != 0) _at = KeyboardLayout.Move(_symbols, _at, Pad.Dx, Pad.Dy);
        if (Pad.A) typed = KeyboardLayout.At(_symbols, _at.row, _at.index);
        if (Pad.LB) text = KeyboardLayout.Backspace(text);

        var mouse = Mouse.current;
        KeyView? hover = null;
        if (mouse != null)
        {
            Vector2 pos = mouse.position.ReadValue();
            foreach (var v in _views[_symbols ? 1 : 0])
                if (RectTransformUtility.RectangleContainsScreenPoint(v.Rt, pos, null)) { hover = v; break; }
            if (hover != null && mouse.delta.ReadValue().sqrMagnitude > 0.5f) _at = hover.At;
            if (hover != null && mouse.leftButton.wasPressedThisFrame) { _at = hover.At; typed = hover.Key; }
        }

        if (typed != null)
        {
            switch (typed.Action)
            {
                case KeyAction.Layer:
                    _symbols = !_symbols;
                    layerChanged = true;
                    _at = KeyboardLayout.Move(_symbols, (KeyboardLayout.Rows(_symbols).Count - 1, 0), 0, 0);
                    _layers[0]!.gameObject.SetActive(!_symbols);
                    _layers[1]!.gameObject.SetActive(_symbols);
                    break;
                case KeyAction.Send:
                case KeyAction.Close:
                    result = typed.Action;
                    break;
                default:
                    string next = KeyboardLayout.Type(text, typed, shift);
                    if (next.Length <= maxLength) text = next;
                    break;
            }
        }
        if (Pad.Dx != 0 || Pad.Dy != 0 || Pad.A || Pad.LB) Pad.Consume();
        Refresh(force: shift != _shiftShown || layerChanged, hover);
        _shiftShown = shift;
        return result;
    }

    private static bool ShiftKeyHeld()
    {
        try { var kb = Keyboard.current; return kb != null && kb.shiftKey.isPressed; } catch { return false; }
    }

    private KeyView? _lastHover;
    private (int, int) _lastAt = (-1, -1);
    private bool _lastPad;

    private void Refresh(bool force, KeyView? hover = null)
    {
        bool pad = Pad.Active;
        if (!force && hover == _lastHover && _lastAt == _at && pad == _lastPad) return;
        _lastHover = hover;
        _lastAt = _at;
        _lastPad = pad;
        bool shift = Pad.LTHeld || ShiftKeyHeld();
        foreach (var v in _views[_symbols ? 1 : 0])
        {
            bool focused = pad && v.At == _at;
            v.Glow.enabled = focused || v == hover;
            if (v.Edge != null) v.Edge.enabled = focused;
            v.Glow.color = focused ? Glow : GlowHover;
            v.Label.color = focused ? KeyTextChosen : v == hover ? KeyTextOn : v.Key.Action == KeyAction.Char ? KeyText : new Color32(150, 145, 136, 255);
            string label = v.Key.Action == KeyAction.Send ? SendLabel : KeyboardLayout.Label(v.Key, shift);
            if (v.Label.text != label) v.Label.text = label;
        }
    }

    public void Destroy()
    {
        if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
        _root = null;
        _layers[0] = _layers[1] = null;
        _views[0].Clear();
        _views[1].Clear();
    }
}
