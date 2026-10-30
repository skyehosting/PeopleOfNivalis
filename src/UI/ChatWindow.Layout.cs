using System;
using System.Globalization;
using System.Linq;
using NivalisAIChat.Npc;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace NivalisAIChat.UI;

/// <summary>
/// The chat window moved and resized (the author: "have the chat window so that it can be moved and maybe resized of
/// course with minimum limits to preserve elements like the mood bar and such, unless you'd like to make that dynamically
/// collapsable so if I resize the window smaller, it will show just a smiley face in a circle, and temp hide the mood or
/// display it as a color associated with it within a circle"). Drag it by its top strip; resize it by the grip at its
/// bottom-right corner (600x320 to 1400x760); under 820 wide the relationship bar and the mood pill become circles (the
/// bar's face in a ring of its colour; the mood's colour with a face), their words on hover. Kept in ChatWindowLayout;
/// a double-click on the grip puts it back.
/// </summary>
internal sealed partial class ChatWindow
{
    public const float DefaultW = 900, DefaultH = 470, MinW = 600, MinH = 320, MaxW = 1400, MaxH = 760, CollapseBelow = 820;
    private const float StripH = 58, BaseY = 90;

    /// <summary>Where it's been moved (canvas units from its default place) and its size (its own units).</summary>
    private Vector2 _offset = Vector2.zero, _size = new(DefaultW, DefaultH);
    private RectTransform? _strip, _handle, _grip, _bondDot, _moodDot, _dotTip;
    private Image? _handleImg, _gripImg, _bondDotRing, _bondDotFace, _moodDotFill, _moodDotFace;
    private TextMeshProUGUI? _dotTipText;
    private string _tipShown = "";
    private bool _dragging, _resizing, _collapsed;
    private Vector2 _dragFrom, _offsetFrom, _sizeFrom;
    private float _lastGripClick = -10f;
    private string _moodWords = "";
    /// <summary>The relationship bar is wanted (a conversation with someone), whether drawn as the pill or as its circle.</summary>
    private bool _bondWanted;

    /// <summary>Said when the window was moved or resized: "dx,dy,w,h" for ChatWindowLayout.</summary>
    public Action<string>? LayoutChanged;

    /// <summary>"dx,dy,w,h" (invariant numbers), or "" for the default.</summary>
    public string Layout
    {
        get => _offset == Vector2.zero && _size == new Vector2(DefaultW, DefaultH) ? "" :
            string.Join(",", new[] { _offset.x, _offset.y, _size.x, _size.y }.Select(f => f.ToString("0", CultureInfo.InvariantCulture)));
        set
        {
            var (o, sz) = ParseLayout(value);
            _offset = o; _size = sz;
            ApplyLayout();
        }
    }

    /// <summary>"dx,dy,w,h" read back, the size held to its limits; the default for anything unreadable.</summary>
    public static (Vector2 offset, Vector2 size) ParseLayout(string text)
    {
        var parts = (text ?? "").Split(',');
        float F(int i, float d) => parts.Length > i && float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) ? v : d;
        if (parts.Length != 4) return (Vector2.zero, new Vector2(DefaultW, DefaultH));
        return (new Vector2(F(0, 0), F(1, 0)), new Vector2(Mathf.Clamp(F(2, DefaultW), MinW, MaxW), Mathf.Clamp(F(3, DefaultH), MinH, MaxH)));
    }

    /// <summary>The window closed mid-drag: the next time it opens, nothing is still being dragged.</summary>
    private void EndLayoutDrag()
    {
        if (_dragging || _resizing) LayoutChanged?.Invoke(Layout);
        _dragging = _resizing = false;
        if (_dotTip != null) _dotTip.gameObject.SetActive(false);
    }

    public void ResetLayout()
    {
        _offset = Vector2.zero;
        _size = new Vector2(DefaultW, DefaultH);
        ApplyLayout();
        LayoutChanged?.Invoke("");
    }

    private void BuildLayoutControls()
    {
        if (_panel == null) return;
        // The top strip: a short handle shows on hover, at its left above the name (CF-20: in the middle it sat under the
        // relationship pill, which the drag skips).
        _strip = Rect(_panel, "DragStrip", new Vector2(0, 1), new Vector2(1, 1), new Vector2(18, -StripH), new Vector2(-18, 0));
        _handle = Rect(_strip, "Handle", new Vector2(0, 1), new Vector2(0, 1), new Vector2(12, -12), new Vector2(56, -7));
        _handleImg = _handle.gameObject.AddComponent<Image>();
        _handleImg.sprite = Sprites.Round(2.5f, 0, new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255));
        _handleImg.type = Image.Type.Sliced;
        _handleImg.color = new Color32(196, 160, 98, 0);
        _handleImg.raycastTarget = false;
        // The grip at the bottom-right corner.
        _grip = Rect(_panel, "ResizeGrip", new Vector2(1, 0), new Vector2(1, 0), new Vector2(-38, 0), new Vector2(0, 38));
        var gripIcon = Rect(_grip, "Icon", new Vector2(1, 0), new Vector2(1, 0), new Vector2(-26, 6), new Vector2(-6, 26));
        _gripImg = gripIcon.gameObject.AddComponent<Image>();
        _gripImg.sprite = Sprites.GripIcon;
        _gripImg.color = new Color32(196, 160, 98, 140);
        _gripImg.raycastTarget = false;
        // The two circles the pills become when it's narrow.
        _bondDot = Dot("BondDot", out _bondDotRing, out _bondDotFace);
        _moodDot = Dot("MoodDot", out _moodDotFill, out _moodDotFace);
        // The mood circle is filled with its colour; the relationship circle is a ring of its colour (told apart by shape too).
        _moodDotFill.sprite = Sprites.Round(21, 0, new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255));
        var rim = Rect(_moodDot, "Rim", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        var rimImg = rim.gameObject.AddComponent<Image>();
        rimImg.sprite = Sprites.Round(21, 2, new Color32(0, 0, 0, 0), new Color32(196, 160, 98, 255));
        rimImg.type = Image.Type.Sliced;
        rimImg.raycastTarget = false;
        _moodDotFace.transform.SetAsLastSibling();
        _tipShown = "";
        _dotTip = Rect(_panel, "DotTip", new Vector2(1, 1), new Vector2(1, 1), Vector2.zero, Vector2.zero);
        _dotTip.pivot = new Vector2(0.5f, 1);
        _dotTip.sizeDelta = new Vector2(150, 28);
        var tipBg = _dotTip.gameObject.AddComponent<Image>();
        tipBg.sprite = Sprites.Round(6, 1, new Color32(13, 12, 11, 245), new Color32(118, 97, 62, 255));
        tipBg.type = Image.Type.Sliced;
        tipBg.raycastTarget = false;
        _dotTipText = Text(_dotTip, "Text", 17, BodyColor, TextAlignmentOptions.Center, _bodyFont);
        Place(_dotTipText.rectTransform, Vector2.zero, Vector2.one, new Vector2(6, 0), new Vector2(-6, 0));
        _dotTipText.enableWordWrapping = false;
        _dotTip.gameObject.SetActive(false);
        ApplyLayout();
    }

    private RectTransform Dot(string name, out Image ring, out Image face)
    {
        var rt = Rect(_panel!, name, new Vector2(1, 1), new Vector2(1, 1), Vector2.zero, Vector2.zero);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(42, 42);
        ring = rt.gameObject.AddComponent<Image>();
        ring.sprite = Sprites.Round(21, 2.5f, new Color32(26, 44, 42, 245), new Color32(255, 255, 255, 255));
        ring.type = Image.Type.Sliced;
        ring.raycastTarget = false;
        var f = Rect(rt, "Face", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-15, -15), new Vector2(15, 15));
        face = f.gameObject.AddComponent<Image>();
        face.preserveAspect = true;
        face.raycastTarget = false;
        rt.gameObject.SetActive(false);
        return rt;
    }

    /// <summary>The size, the place (the (i) panel beside it, on the left when the right has no room), the pills or circles.</summary>
    private void ApplyLayout()
    {
        if (_panel == null) return;
        // Never bigger than the screen (a large UiScale on a small screen): the size gives way first.
        var cs = CanvasSize();
        float sc = Mathf.Max(0.01f, _panel.localScale.x);
        _size = new Vector2(Mathf.Clamp(_size.x, MinW, Mathf.Max(MinW, cs.x / sc - 40)), Mathf.Clamp(_size.y, MinH, Mathf.Max(MinH, cs.y / sc - BaseY / sc - 60)));
        _panel.sizeDelta = _size;
        ClampOffset();
        ShowInfo(InfoOpen);
        UpdateCollapse(force: true);
    }

    /// <summary>The canvas's size in its own units (the chat canvas: 1920x1080, matched halfway).</summary>
    private Vector2 CanvasSize() => _root != null && _root.TryGetComponent<RectTransform>(out var rt) ? rt.rect.size : new Vector2(1920, 1080);

    private float CanvasScale() => _root != null && _root.TryGetComponent<Canvas>(out var c) && c.scaleFactor > 0 ? c.scaleFactor : 1f;

    /// <summary>Wholly on the screen: its edges (and the pills above it) inside the canvas.</summary>
    private void ClampOffset()
    {
        if (_panel == null) return;
        var cs = CanvasSize();
        float s = _panel.localScale.x, w = _size.x * s, h = _size.y * s;
        float infoW = InfoOpen ? (InfoW + InfoGap) * s : 0;
        float halfRoom = Mathf.Max(0, (cs.x - w - infoW) / 2);
        _offset.x = Mathf.Clamp(_offset.x, -halfRoom, halfRoom);
        _offset.y = Mathf.Clamp(_offset.y, -BaseY + 8, Mathf.Max(-BaseY + 8, cs.y - BaseY - h - 30 * s));
    }

    private bool InfoOnLeft()
    {
        if (_panel == null) return false;
        var cs = CanvasSize();
        float s = _panel.localScale.x;
        float right = _offset.x + _size.x * s / 2 + (InfoGap + InfoW) * s;
        return right > cs.x / 2 - 4;
    }

    /// <summary>Under <see cref="CollapseBelow"/> wide, the pills are circles; otherwise as they were.</summary>
    private void UpdateCollapse(bool force = false)
    {
        if (_panel == null || _bondDot == null || _moodDot == null) return;
        bool collapsed = _collapsed ? _size.x < CollapseBelow + 20 : _size.x < CollapseBelow; // no flicker round 820
        bool bondOn = _bond != null && (collapsed ? _bondWanted : _bond.gameObject.activeSelf || _bondWanted);
        bool moodOn = _moodWords.Length > 0;
        if (collapsed != _collapsed || force)
        {
            _collapsed = collapsed;
            if (_bond != null) _bond.gameObject.SetActive(!collapsed && _bondWanted);
            if (_pill != null) _pill.gameObject.SetActive(!collapsed && moodOn);
            PlaceRelationshipPill();
        }
        if (_bondDot.gameObject.activeSelf != (collapsed && _bondWanted)) _bondDot.gameObject.SetActive(collapsed && _bondWanted);
        if (_moodDot.gameObject.activeSelf != (collapsed && moodOn)) _moodDot.gameObject.SetActive(collapsed && moodOn);
        if (!collapsed) return;
        // Right to left along the top edge: the mood, then the relationship (as the pills sit).
        float x = -46 - 21;
        if (moodOn) { _moodDot.anchoredPosition = new Vector2(x, 0); x -= 52; }
        _bondDot.anchoredPosition = new Vector2(x, 0);
        // The relationship: its face (a smile from the middle up), ringed in the colour of the bar where they stand.
        if (!double.IsNaN(_bondPercent) && _bondDotRing != null && _bondDotFace != null)
        {
            int cell = Mathf.Clamp((int)(_bondPercent / 100.0 * Relationships.Cells), 0, Relationships.Cells - 1);
            var (r, g, b) = Relationships.CellColour(cell);
            _bondDotRing.color = new Color32(r, g, b, 255);
            _bondDotFace.sprite = _bondPercent >= 50 ? Sprites.Smile : Sprites.Frown;
        }
        if (moodOn && _moodDotFill != null && _moodDotFace != null)
        {
            var (fill, good) = MoodColour(_moodWords);
            _moodDotFill.color = fill;
            _moodDotFace.sprite = good ? Sprites.PaleSmile : Sprites.PaleFrown;
        }
    }

    /// <summary>A mood's colour (and whether it's a good one), from its first word: the circle the mood pill becomes.</summary>
    public static (Color32 colour, bool good) MoodColour(string mood)
    {
        string m = (mood ?? "").Trim().ToLowerInvariant();
        static bool Has(string m, params string[] words) { foreach (var w in words) if (m.Contains(w)) return true; return false; }
        if (Has(m, "angry", "furious", "annoy", "irritat", "hostile", "bitter")) return (new Color32(217, 96, 79, 255), false);
        if (Has(m, "sad", "down", "lonely", "hurt", "gloom", "nervous", "anxious", "tense", "worried", "scared", "afraid", "uneasy")) return (new Color32(122, 150, 184, 255), false);
        if (Has(m, "tired", "bored", "weary", "sleepy", "flat")) return (new Color32(150, 146, 140, 255), false);
        if (Has(m, "curious", "excited", "playful", "flirt", "amused", "proud")) return (new Color32(217, 165, 90, 255), true);
        if (Has(m, "friendly", "warm", "calm", "relaxed")) return (new Color32(110, 186, 170, 255), true);
        return (new Color32(127, 191, 106, 255), true); // happy, content, cheerful and the rest
    }

    /// <summary>Every frame while open: the strip and grip (hover, drag, resize, a double-click to reset), the circles' words.</summary>
    private void TickLayout(Mouse? mouse, Vector2 pos)
    {
        if (_panel == null || _strip == null || _grip == null) return;
        bool down = mouse != null && mouse.leftButton.isPressed, pressed = mouse != null && mouse.leftButton.wasPressedThisFrame;
        bool overStrip = Over(_strip, pos) && !OverAnyPill(pos), overGrip = Over(_grip, pos);
        if (pressed && overGrip)
        {
            if (Time.unscaledTime - _lastGripClick < 0.35f) { ResetLayout(); _lastGripClick = -10f; return; }
            _lastGripClick = Time.unscaledTime;
            _resizing = true; _dragFrom = pos; _sizeFrom = _size; _offsetFrom = _offset;
        }
        else if (pressed && overStrip) { _dragging = true; _dragFrom = pos; _offsetFrom = _offset; }
        if ((_dragging || _resizing) && !down)
        {
            _dragging = _resizing = false;
            LayoutChanged?.Invoke(Layout);
        }
        float canvas = CanvasScale(), s = _panel.localScale.x;
        if (_dragging)
        {
            _offset = _offsetFrom + (pos - _dragFrom) / canvas;
            ClampOffset();
            ShowInfo(InfoOpen);
        }
        else if (_resizing)
        {
            // The top-left corner stays where it is; the grip follows the mouse.
            var d = (pos - _dragFrom) / canvas;
            float w = Mathf.Clamp(_sizeFrom.x + d.x / s, MinW, MaxW), h = Mathf.Clamp(_sizeFrom.y - d.y / s, MinH, MaxH);
            var cs = CanvasSize();
            w = Mathf.Min(w, Mathf.Max(MinW, cs.x / s - 40)); h = Mathf.Min(h, Mathf.Max(MinH, cs.y / s - BaseY / s - 60)); // held to the screen
            _size = new Vector2(w, h);
            _offset = new Vector2(_offsetFrom.x + (w - _sizeFrom.x) * s / 2, _offsetFrom.y - (h - _sizeFrom.y) * s);
            _panel.sizeDelta = _size;
            ClampOffset();
            ShowInfo(InfoOpen);
            UpdateCollapse();
        }
        if (_handleImg != null) _handleImg.color = new Color32(196, 160, 98, (byte)(overStrip || _dragging ? 210 : 70));
        if (_gripImg != null) _gripImg.color = new Color32(196, 160, 98, (byte)(overGrip || _resizing ? 255 : 140));
        // The circles' words, on hover.
        string tip = "";
        RectTransform? over = null;
        if (_collapsed && _bondDot != null && _bondDot.gameObject.activeSelf && !double.IsNaN(_bondPercent) && Over(_bondDot, pos)) { over = _bondDot; tip = $"Bond: {InfoSheet.Feeling(Relationships.ToAffinity(_bondPercent))} · {_bondPercent:0}%"; }
        else if (_collapsed && _moodDot != null && _moodDot.gameObject.activeSelf && Over(_moodDot, pos)) { over = _moodDot; tip = "Mood: " + _moodWords; }
        else if (overGrip && !_resizing) { over = _grip; tip = "Drag to resize · double-click to reset"; }
        if (_dotTip != null && _dotTipText != null)
        {
            _dotTip.gameObject.SetActive(over != null);
            if (over != null)
            {
                if (tip != _tipShown) // laid out again only when the words change (the full audit, B-12)
                {
                    _tipShown = tip;
                    _dotTipText.text = Escape(tip);
                    _dotTipText.ForceMeshUpdate();
                    _dotTip.sizeDelta = new Vector2(Mathf.Clamp(_dotTipText.preferredWidth + 18, 60, 360), 28);
                }
                // Above the circle (never over the name row); the grip's to its left.
                bool roomAbove = _offset.y + BaseY + _size.y * _panel.localScale.x + 70 < CanvasSize().y;
                _dotTip.pivot = new Vector2(0.5f, roomAbove || over == _grip ? 0 : 1);
                _dotTip.anchoredPosition = over == _grip ? new Vector2(-150, -(_size.y - 96))
                    : over.anchoredPosition + new Vector2(0, roomAbove ? 26 : -26);
            }
        }
    }

    private bool OverAnyPill(Vector2 pos) =>
        (_pill != null && Over(_pill, pos)) || (_bond != null && Over(_bond, pos)) || (_bondDot != null && Over(_bondDot, pos)) || (_moodDot != null && Over(_moodDot, pos));
}
