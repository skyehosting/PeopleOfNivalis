using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace NivalisAIChat.UI;

/// <summary>
/// The phone (the author's request, 2026-10-19): an older handset on the right of the screen with a real pill home
/// button under the screen, and three apps: Contacts, Messages, Settings. Built from code at its design size (330x660)
/// on a canvas of its own, scaled with the screen's height (never below 0.9, never taller than the screen), anchored
/// right-centre. Every control is polled each frame (hover, click, wheel), like the chat window's. It keeps no game state:
/// the runtime fills <see cref="View"/> and calls <see cref="Render"/>.
/// </summary>
internal sealed partial class PhoneWindow
{
    // ------------------------------------------------------------------ the look (phone-6.html :root)
    private static readonly Color32 BodyFill = new(36, 33, 30, 255), Deep = new(22, 20, 18, 255), Panel = new(29, 26, 23, 255),
        InputFill = new(22, 20, 18, 247), Gold = new(196, 160, 98, 255), GoldDim = new(118, 97, 62, 255), NameC = new(217, 165, 90, 255),
        TextC = new(238, 232, 223, 255), BodyC = new(207, 203, 196, 255), Muted = new(154, 148, 139, 255), PillC = new(150, 196, 168, 255),
        PillFill = new(26, 44, 42, 240), You = new(95, 168, 232, 255), YouText = new(12, 24, 35, 255), Theirs = new(59, 53, 46, 255),
        Err = new(229, 131, 116, 255), LineC = new(118, 97, 62, 115), RowLine = new(118, 97, 62, 56), Off = new(58, 53, 48, 255),
        OffText = new(125, 119, 110, 255), Dark = new(26, 23, 20, 255), Clear = new(0, 0, 0, 0), White = new(255, 255, 255, 255),
        Hover = new(196, 160, 98, 31);

    public const float W = 330, H = 660, ScreenX = 16, ScreenY = 56, ScreenW = 298, ScreenH = 536, StatusH = 26, BarH = 48, G = 12;
    private const float RefHeight = 892f, Slide = 0.18f;

    private GameObject? _root;
    private CanvasScaler? _scaler;
    private RectTransform? _phone, _screen, _area, _holder;
    private TMP_FontAsset? _title, _body;
    private TextMeshProUGUI? _keyhint;
    private Image? _homeBtn;
    private RectTransform? _homeRt;
    private float _slideT, _lastHeight = -1, _lastUser = 1f;
    private bool _visible;

    /// <summary>What to show; the runtime fills it before <see cref="Render"/>.</summary>
    public PhoneView View = new();
    /// <summary>"home", "contacts", "contact", "messages", "thread", "settings", "weather", "places".</summary>
    public string ScreenName { get; private set; } = "home";
    /// <summary>The contact or conversation open (contact, thread).</summary>
    public string Subject { get; private set; } = "";
    private readonly Stack<(string screen, string subject)> _back = new();

    public bool Built => _root != null;
    public bool Visible => _visible;

    // ------------------------------------------------------------------ what the runtime is told (the phone does nothing itself)
    /// <summary>The screen changed (or needs fresh data): the runtime refills View and calls Render.</summary>
    public Action? Changed;
    public Action? CloseRequested;
    public Action<string, string>? Send;
    public Action<string>? Retry;
    public Action<string, bool>? Toggle;
    public Action<string, int>? Step;
    public Action<string>? RemovePlace, SetWallpaper;
    public Action? AddHere, PlacesDefaults, CostsDefaults, Undo, WallpaperDefault;
    public Action? ShowEarlier;
    public Action<string, int, string>? DeleteText;
    public Action<string>? ClearThread;
    /// <summary>Where voice notes are kept on the PC (the runtime's folder).</summary>
    public Func<string>? VoiceDir;
    /// <summary>FB-75: a game recording's clip, by its note's name ("game-....wav"); null when the game hasn't got it.</summary>
    public Func<string, AudioClip?>? GameClip;

    // ------------------------------------------------------------------ build

    public void Build((TMP_FontAsset? title, TMP_FontAsset? body) fonts)
    {
        if (_root != null || _gone) return;
        (_title, _body) = fonts;
        _root = new GameObject("NivalisAIChat.Phone");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 31990; // under the chat window's (32000): the two are never open together
        _scaler = _root.AddComponent<CanvasScaler>();
        _scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        _root.AddComponent<GraphicRaycaster>();

        // Parked here (inactive) when no screen uses them: the two text boxes keep what's typed across redraws.
        _holder = Box(_root.transform, "Parked", 0, 0, 1, 1);
        _holder.gameObject.SetActive(false);

        BuildToast();

        _phone = new GameObject("Phone").AddComponent<RectTransform>();
        _phone.SetParent(_root.transform, false);
        _phone.anchorMin = _phone.anchorMax = new Vector2(1, 0.5f);
        _phone.pivot = new Vector2(1, 0.5f);
        _phone.sizeDelta = new Vector2(W, H);
        var body = _phone.gameObject.AddComponent<Image>();
        body.sprite = Sprites.Round(44, 2, BodyFill, GoldDim);
        body.type = Image.Type.Sliced;
        var ring = Box(_phone, "Ring", 5, 5, W - 10, H - 10);
        Img(ring, Sprites.Round(39, 1, Clear, new Color32(196, 160, 98, 64)));
        Img(Box(_phone, "Speaker", (W - 58) / 2, 27, 58, 7), Sprites.Round(3.5f, 0, new Color32(13, 12, 11, 255), new Color32(13, 12, 11, 255)));
        Img(Box(_phone, "Camera", W / 2 - 56, 24, 12, 12), Sprites.Round(6, 3, new Color32(13, 12, 11, 255), new Color32(27, 25, 23, 255)));

        _screen = Box(_phone, "Screen", ScreenX, ScreenY, ScreenW, ScreenH);
        Img(_screen, Sprites.Round(8, 1, Deep, new Color32(11, 10, 9, 255)));
        _screen.gameObject.AddComponent<RectMask2D>();

        // The pill home button under the screen (not a touch screen: a real button). The author (2026-10-19): the bottom of
        // the phone about as thick as the top (68 against 56), one pill (no pill inside it); pass 7: 8 from the screen to
        // the pill, the key line 4 clear of the rim.
        _homeRt = Box(_phone, "Home", (W - 70) / 2, H - 32 - 28, 70, 28);
        _homeBtn = Img(_homeRt, Sprites.Round(14, 2, new Color32(21, 19, 17, 255), GoldDim)); // no "Home" tip beside it (FB-74): its rim lights on hover
        _keyhint = Label(_phone, "", 13, Muted, 8, H - 9 - 16, W - 16, 16, TextAlignmentOptions.Center, bold: false, rich: true);
        _keyhint.overflowMode = TextOverflowModes.Ellipsis;
        _keyhint.enableWordWrapping = false;

        BuildStatus();
        _area = Box(_screen, "Area", 0, StatusH, ScreenW, ScreenH - StatusH);
        BuildInputs();
        _phone.gameObject.SetActive(false);
        Fit(force: true);
    }

    /// <summary>
    /// The phone's scale (the author: "works with all aspect ratios, has the proper element aspect ratio constraints, and
    /// adapts to the users dpi and windows scale value"): one factor for both sides, so it never stretches. 74% of the
    /// screen's height, times PhoneScale; never below 0.9 times your Windows display scale (so its smallest text stays
    /// readable on a small high-DPI screen or in a small window), and never taller than 95% of the screen.
    /// </summary>
    public static float ScaleFor(float height, float userScale = 1f, float windowsScale = 1f)
    {
        float user = Mathf.Clamp(float.IsFinite(userScale) ? userScale : 1f, 0.8f, 1.25f);
        float dpi = Mathf.Clamp(float.IsFinite(windowsScale) ? windowsScale : 1f, 1f, 3f);
        return Mathf.Min(Mathf.Max(height / RefHeight * user, 0.9f * dpi), height * 0.95f / H);
    }

    /// <summary>PhoneScale (0.8 to 1.25), set by the runtime.</summary>
    public float UserScale = 1f;

    private void Fit(bool force = false)
    {
        if (_scaler == null || _phone == null) return;
        float h = UnityEngine.Screen.height;
        if (!force && Mathf.Approximately(h, _lastHeight) && Mathf.Approximately(UserScale, _lastUser)) return;
        _lastUser = UserScale;
        _lastHeight = h;
        float s = ScaleFor(h, UserScale, DisplayScale.Windows());
        _scaler.scaleFactor = s;
        _phone.sizeDelta = new Vector2(W, H);
        PlacePhone();
    }

    private float Inset => _lastHeight > 0 && _scaler != null ? _lastHeight * 0.03f / _scaler.scaleFactor : 27f;

    private void PlacePhone()
    {
        if (_phone == null) return;
        float t = Mathf.Clamp01(_slideT / Slide);
        float eased = 1f - (1f - t) * (1f - t);
        _phone.anchoredPosition = new Vector2(Mathf.Lerp(W + 40f, -Inset, eased), 0);
    }

    public void Show()
    {
        if (_phone == null) return;
        _visible = true;
        // Put away and taken out again mid-slide: it comes back from where it is (FB-74).
        if (!_leaving) _slideT = 0;
        _leaving = false;
        _phone.gameObject.SetActive(true);
        Fit(force: true);
        Render();
    }

    /// <summary>Put away: it slides back out to the right, the way it came in (FB-74); <paramref name="now"/> skips the slide.</summary>
    public void Hide(bool now = false)
    {
        if (_phone == null) return;
        _visible = false;
        try { if (_compose != null && _compose.isFocused) _compose.DeactivateInputField(); } catch { }
        try { if (_url != null && _url.isFocused) _url.DeactivateInputField(); } catch { }
        _composeHadFocus = _urlHadFocus = false;
        StopNote();
        CloseTextMenu();
        _slideT = Mathf.Min(_slideT, Slide);
        _leaving = !now && _phone.gameObject.activeSelf && _slideT > 0;
        if (!_leaving) _phone.gameObject.SetActive(false);
    }

    // Sliding out after Hide: drawn, never taking input.
    private bool _leaving;

    /// <summary>
    /// The mod is stopping (a hot reload starts a new copy with its own phone): the canvas, the wallpaper's textures and the
    /// note's clip are destroyed, never left behind (the full audit, B-4). This window isn't built again afterwards.
    /// </summary>
    public void Destroy()
    {
        try { Hide(now: true); } catch { }
        foreach (var t in new[] { _still, _gifTex, _default, _scrim, _fadeDownTex, _fadeUpTex })
            if (t != null) try { UnityEngine.Object.Destroy(t); } catch { }
        _still = _gifTex = _default = _scrim = _fadeDownTex = _fadeUpTex = null;
        _frames = null;
        FreePhotos(); // FB-71
        if (_root != null) try { UnityEngine.Object.Destroy(_root); } catch { }
        _root = null;
        _gone = true;
    }

    private bool _gone;

    // ------------------------------------------------------------------ moving between screens

    public void Go(string screen, string subject = "")
    {
        if (screen == ScreenName && subject == Subject) return;
        View.ReplyTo = View.ReplyHandle = View.ReplyQuote = ""; // review NL-5: replying belongs to one opened line
        _deleteAsk = ""; // FB-80: "tap again to delete" is for the photo it was asked on
        _scroll[Key] = _scrollPos;
        _back.Push((ScreenName, Subject));
        ScreenName = screen;
        Subject = subject;
        _scrollPos = _scroll.TryGetValue(Key, out var p) && screen != "thread" ? p : 0f;
        _scrollTarget = _scrollPos;
        CloseTextMenu();
        Changed?.Invoke();
    }

    public void Home()
    {
        _scroll[Key] = _scrollPos;
        _back.Clear();
        ScreenName = "home";
        Subject = "";
        _scrollPos = _scrollTarget = 0;
        Changed?.Invoke();
    }

    /// <summary>Back a screen (Esc, the chevron): to where you were, at the same place in the list.</summary>
    public void Back()
    {
        if (_back.Count == 0) { Home(); return; }
        _scroll[Key] = _scrollPos;
        (ScreenName, Subject) = _back.Pop();
        _scrollPos = _scrollTarget = _scroll.TryGetValue(Key, out var p) ? p : 0f;
        CloseTextMenu();
        Changed?.Invoke();
    }

    /// <summary>Straight to a conversation (a text's card): home, Messages, then it.</summary>
    public void OpenThread(string id)
    {
        _back.Clear();
        _back.Push(("home", ""));
        _back.Push(("messages", ""));
        ScreenName = "thread";
        Subject = id;
        _scrollPos = _scrollTarget = 0;
        Changed?.Invoke();
    }

    private string Key => ScreenName + ":" + Subject;
    private readonly Dictionary<string, float> _scroll = new();

    // ------------------------------------------------------------------ the frame

    /// <summary>A text box on the phone has the keyboard: every key types (the phone key too).</summary>
    public bool Typing => (_compose != null && _compose.isFocused) || (_url != null && _url.isFocused);

    public void Tick(float dt)
    {
        TickToast(dt);
        try { TickPip(dt); } catch (Exception e) { Log.Debug("phone pip: " + e.Message); } // CF-20
        if (_leaving && _phone != null)
        {
            _slideT -= dt;
            PlacePhone();
            if (_slideT <= 0) { _leaving = false; _phone.gameObject.SetActive(false); }
            return;
        }
        if (!_visible || _phone == null) return;
        TickPhotos(); // FB-71: one Nightline photo made a texture a frame
        TickCamera(dt); // FB-80: the shutter's flash
        Fit();
        if (_slideT < Slide) { _slideT += dt; PlacePhone(); }
        var mouse = Mouse.current;
        var kb = Keyboard.current;
        Vector2 pos = mouse != null ? mouse.position.ReadValue() : new Vector2(-1, -1);
        bool click = mouse != null && mouse.leftButton.wasPressedThisFrame;
        bool rightClick = mouse != null && mouse.rightButton.wasPressedThisFrame;

        // Hover and clicks: the home pill, then the screen's own controls.
        bool overHome = Over(_homeRt, pos);
        if (_homeBtn != null) _homeBtn.sprite = overHome ? Sprites.Round(14, 2, new Color32(21, 19, 17, 255), Gold) : Sprites.Round(14, 2, new Color32(21, 19, 17, 255), GoldDim);
        Hit? clicked = null;
        foreach (var h in _hits)
        {
            if (h.Rt == null || !h.Rt.gameObject.activeInHierarchy) continue;
            bool over = h.Enabled && Over(h.Rt, pos) && (h.Clip == null || Over(h.Clip, pos));
            if (h.Ring != null) h.Ring.SetActive(over);
            if (h.Tint != null) h.Tint.color = over ? h.HoverColor : h.IdleColor;
            // FB-78: a control inside another (a like pill on its card) wins over it, whichever was made first; a layer
            // made later on top (a menu) still wins over what's under it.
            if (over && click && (clicked == null || !clicked.Rt.IsChildOf(h.Rt))) clicked = h;
        }
        // Over a text: the hint line says how to delete it.
        bool overText = _menuFor == null && _viewport != null && Over(_viewport, pos) && _lineRects.Any(l => l.rt != null && Over(l.rt, pos));
        if (overText != _hintDelete) { _hintDelete = overText; UpdateHint(); }
        if (TickLineUi(pos, click, rightClick)) return; // FB-71: popovers and the right-click menu
        if (click && overHome) { Home(); return; }
        if (clicked?.Act != null) { _menuFor = null; clicked.Act(); return; }
        // Right-click a text: a small Delete under the cursor (any other click closes it).
        if (rightClick && _viewport != null && Over(_viewport, pos))
        {
            var hitLine = _lineRects.FirstOrDefault(l => l.rt != null && Over(l.rt, pos));
            if (hitLine.rt != null) { OpenTextMenu(hitLine.index, hitLine.body, pos); return; }
        }
        if ((click || rightClick) && _menuFor != null) { CloseTextMenu(); }

        // The wheel scrolls the list under the mouse.
        // The wheel moves a target; the list eases to it (as the chat window's does).
        float wheel = mouse != null ? mouse.scroll.ReadValue().y : 0f;
        if (wheel != 0 && _viewport != null && Over(_viewport, pos))
        {
            _scrollTarget = Mathf.Clamp(_scrollTarget + (_bottomUp ? wheel : -wheel) * 0.45f, 0f, _scrollMax);
            CloseTextMenu();
        }
        if (Mathf.Abs(_scrollPos - _scrollTarget) > 0.3f) _scrollPos = Mathf.Lerp(_scrollPos, _scrollTarget, 1f - Mathf.Exp(-dt * 14f));
        else _scrollPos = _scrollTarget;
        ApplyScroll();

        // Keys: Esc leaves a box, then goes back a screen, then puts the phone away; Enter sends or sets.
        // A text box ends its own edit on Enter or Esc (TMP's single-line field submits and lets go in the EventSystem's
        // update), which can run before this: the box counts as in use if it was at the end of the last frame too
        // (FB-74: Enter didn't send, F-20261019-0180).
        bool inCompose = _compose != null && (_compose.isFocused || _composeHadFocus);
        bool inUrl = _url != null && (_url.isFocused || _urlHadFocus);
        _composeHadFocus = _compose != null && _compose.isFocused;
        _urlHadFocus = _url != null && _url.isFocused;
        if (kb != null)
        {
            bool enter = kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame;
            if (inCompose && enter) { SendNow(); return; }
            if (inUrl && enter) { SetNow(); return; }
            if (kb.escapeKey.wasPressedThisFrame)
            {
                if (View.ReplyTo.Length > 0 && ScreenName == "line") { StopReplying(); return; } // Esc stops replying first
                if (inCompose) { if (_compose!.isFocused) _compose.DeactivateInputField(); _composeHadFocus = false; _composeLeft = true; UpdateHint(); }
                else if (inUrl) { if (_url!.isFocused) _url.DeactivateInputField(); _urlHadFocus = false; UpdateHint(); }
                else if (ScreenName == "home") CloseRequested?.Invoke();
                else Back();
                return;
            }
        }
        TickInputs(pos, click);
        TickTyping();
        TickWallpaper(dt);
        TickNotes();
    }

    private static bool Over(RectTransform? rt, Vector2 screenPos) =>
        rt != null && rt.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(rt, screenPos, null);

    // ------------------------------------------------------------------ the controls a screen registers

    private sealed class Hit
    {
        public RectTransform? Rt, Clip;
        public Action? Act;
        public GameObject? Ring;
        public Image? Tint;
        public Color IdleColor, HoverColor;
        public bool Enabled = true;
    }

    private readonly List<Hit> _hits = new();

    /// <summary>A clickable area; <paramref name="ring"/> a gold ring shown on hover (outside the control, so nothing moves).</summary>
    private Hit Clickable(RectTransform rt, Action act, float radius = 8, bool ring = true, bool enabled = true)
    {
        var h = new Hit { Rt = rt, Act = act, Enabled = enabled, Clip = _viewport != null && rt.IsChildOf(_viewport) ? _viewport : null };
        if (ring && enabled)
        {
            var r = Box(rt, "Hover", -3, -3, rt.rect.width + 6, rt.rect.height + 6);
            Img(r, Sprites.Round(radius + 3, 3, Clear, new Color32(196, 160, 98, 100)));
            r.gameObject.SetActive(false);
            h.Ring = r.gameObject;
        }
        _hits.Add(h);
        return h;
    }

    // ------------------------------------------------------------------ building blocks (top-left coordinates, design units)

    private static RectTransform Box(Transform parent, string name, float x, float y, float w, float h)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(Mathf.Max(0, w), Mathf.Max(0, h));
        return rt;
    }

    private static Image Img(RectTransform rt, Sprite sprite, Color? tint = null)
    {
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = sprite;
        img.type = Image.Type.Sliced;
        img.color = tint ?? Color.white;
        img.raycastTarget = false;
        return img;
    }

    private static Image Icon(RectTransform parent, Sprite sprite, float x, float y, float size, Color tint)
    {
        var img = Box(parent, "Icon", x, y, size, size).gameObject.AddComponent<Image>();
        img.sprite = sprite;
        img.type = Image.Type.Simple;
        img.preserveAspect = true;
        img.color = tint;
        img.raycastTarget = false;
        return img;
    }

    private static Image Solid(RectTransform parent, float x, float y, float w, float h, Color tint) =>
        Img(Box(parent, "Fill", x, y, w, h), Sprites.Round(0.5f, 0, White, White), tint);

    /// <summary>A text: <paramref name="bold"/> is the ExtraBold title face; <paramref name="rich"/> only for the mod's own words.</summary>
    private TextMeshProUGUI Label(Transform parent, string text, float size, Color color, float x, float y, float w, float h,
        TextAlignmentOptions align = TextAlignmentOptions.TopLeft, bool bold = false, bool rich = false, bool wrap = true)
    {
        var rt = Box(parent, "Text", x, y, w, h);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        var font = bold ? _title ?? _body : _body;
        if (font != null) t.font = font;
        t.fontSize = size;
        t.color = color;
        t.alignment = align;
        t.raycastTarget = false;
        t.richText = rich;
        t.parseCtrlCharacters = false;
        t.enableAutoSizing = false;
        t.enableWordWrapping = wrap;
        t.overflowMode = wrap ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
        t.text = text ?? "";
        return t;
    }

    /// <summary>How tall a text is at this width (wrapping), in design units.</summary>
    private static float Tall(TextMeshProUGUI t, float width) => Mathf.Ceil(t.GetPreferredValues(t.text, width, 0).y);

    /// <summary>How wide a one-line text is, in design units.</summary>
    private static float Wide(TextMeshProUGUI t) => Mathf.Ceil(t.GetPreferredValues(t.text, 9999, 0).x);

    /// <summary>A one-line text cut to fit: no space left before the ellipsis (the display judge's note).</summary>
    private static void FitLine(TextMeshProUGUI t, string text, float width)
    {
        t.text = text;
        if (Wide(t) <= width) return;
        int n = text.Length;
        while (n > 1)
        {
            n--;
            t.text = text[..n].TrimEnd() + "…";
            if (Wide(t) <= width) return;
        }
    }

    /// <summary>A link's tail fitted to the field: "…street-at-night-loop.gif" (measured, never a fixed count).</summary>
    private static void FitTail(TextMeshProUGUI t, string text, float width)
    {
        t.text = text;
        if (Wide(t) <= width) return;
        for (int n = text.Length - 1; n > 4; n--)
        {
            t.text = "…" + text[^n..];
            if (Wide(t) <= width) return;
        }
    }

    // ------------------------------------------------------------------ the status bar (the same on every screen)

    private TextMeshProUGUI? _carrier, _time, _battPct;
    private readonly Image[] _barImgs = new Image[4];
    private RectTransform? _battFill;
    private Image? _battFillImg;

    private void BuildStatus()
    {
        var bar = Box(_screen!, "Status", 0, 0, ScreenW, StatusH);
        Img(bar, Sprites.Round(0.5f, 0, White, White), new Color32(0, 0, 0, 90));
        for (int i = 0; i < 4; i++)
        {
            float h = new[] { 3f, 5f, 8f, 11f }[i];
            _barImgs[i] = Solid(bar, G + i * 5, 7.5f + (11 - h), 3, h, new Color32(237, 232, 224, 255));
        }
        _carrier = Label(bar, "NIVA", 13, new Color32(216, 210, 200, 255), G + 26, 5, 90, 17, TextAlignmentOptions.TopLeft, wrap: false);
        _time = Label(bar, "", 13, new Color32(237, 232, 224, 255), 0, 5, ScreenW, 17, TextAlignmentOptions.Top, bold: true, wrap: false);
        var batt = Box(bar, "Battery", ScreenW - G - 26, 7.5f, 22, 11);
        Img(batt, Sprites.Round(3, 1.5f, Clear, new Color32(237, 232, 224, 255)));
        _battFillImg = Solid(batt, 2.5f, 2.5f, 17, 6, new Color32(159, 209, 138, 255));
        _battFill = _battFillImg.rectTransform;
        Solid(bar, ScreenW - G - 3.5f, 11, 2, 4, new Color32(237, 232, 224, 255));
        // FB-70: the battery runs down through the day (full at 8 AM) and never dies.
        _battPct = Label(bar, "100%", 13, new Color32(237, 232, 224, 255), ScreenW - G - 26 - 6 - 40, 5, 40, 17, TextAlignmentOptions.TopRight, wrap: false);
    }

    private TextMeshProUGUI? _homeClock, _homeDate;

    /// <summary>FB-83: the status bar and the home screen's clock and date again from <see cref="View"/>, nothing else redrawn
    /// (typing, the scroll place and the Camera are left alone).</summary>
    public void RenderClock()
    {
        RenderStatus();
        if (_homeClock != null) _homeClock.text = View.Clock;
        if (_homeDate != null) _homeDate.text = View.Date;
    }

    private void RenderStatus()
    {
        if (_time != null) _time.text = View.Time;
        if (_carrier != null)
        {
            _carrier.text = View.Carrier;
            _carrier.color = View.Bars <= 0 ? new Color32(240, 184, 174, 255) : new Color32(216, 210, 200, 255);
        }
        for (int i = 0; i < 4; i++)
            if (_barImgs[i] != null) _barImgs[i].color = i < View.Bars ? new Color32(237, 232, 224, 255) : new Color32(237, 232, 224, 64);
        int pct = Mathf.Clamp(View.Battery, 1, 100);
        if (_battPct != null) _battPct.text = pct + "%";
        if (_battFill != null) _battFill.sizeDelta = new Vector2(Mathf.Max(2f, 17f * pct / 100f), 6); // never thinner than 2
        if (_battFillImg != null) _battFillImg.color = pct > 50 ? new Color32(159, 209, 138, 255) : pct > 20 ? new Color32(227, 196, 107, 255) : new Color32(224, 115, 95, 255);
    }

    private void UpdateHint()
    {
        if (_keyhint == null) return;
        string k = View.Key.Length > 0 ? View.Key : "N";
        _keyhint.text = _hintDelete ? "<b>Right-click</b> · delete this text"
            : (_compose != null && _compose.isFocused) ? "<b>Enter</b> · send · <b>Esc</b> · leave the box"
            : (_url != null && _url.isFocused) ? "<b>Enter</b> · set · <b>Esc</b> · leave the box"
            : ScreenName == "home" ? $"<b>{Escape(k)}</b> or <b>Esc</b> · put away"
            : ScreenName == "nightline" ? "<b>Wheel</b> · scroll · <b>Click</b> · open · <b>Hover</b> likes · who"
            : ScreenName == "line" ? (View.ReplyTo.Length > 0 ? "<b>Enter</b> · send · <b>Esc</b> · stop replying" : "<b>Esc</b> · back · <b>Hover</b> a comment · reply")
            : $"<b>Esc</b> · back · <b>{Escape(k)}</b> · put away";
    }

    private bool _hintDelete;

    private static string Escape(string s) => (s ?? "").Replace("<", "<​");
}
