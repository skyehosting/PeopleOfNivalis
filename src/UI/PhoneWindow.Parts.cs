using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisAIChat.UI;

/// <summary>The phone's text boxes, its wallpaper (a still or a playing GIF) and the card a text comes as while it's away.</summary>
internal sealed partial class PhoneWindow
{
    // ------------------------------------------------------------------ the two text boxes (kept across redraws)

    private TMP_InputField? _compose, _url;
    private RectTransform? _composeRt, _urlRt;
    private TextMeshProUGUI? _urlTail, _urlText;
    private Image? _sendImg, _sendIcon, _setBtn;
    private RectTransform? _sendRt;
    /// <summary>You left the box with Esc: it isn't given the keyboard back on its own until you click it.</summary>
    private bool _composeLeft;
    // The boxes had the keyboard at the end of the last frame (Enter and Esc are read after the box may have let go).
    private bool _composeHadFocus, _urlHadFocus;
    private int _focusIn = -1;

    private void BuildInputs()
    {
        (_compose, _composeRt, _) = MakeInput("Compose", 15, 18, "Text message", 400);
        (_url, _urlRt, _urlText) = MakeInput("Link", 13, 8, "Paste a picture link", 2048);
        _urlTail = Label(_urlRt!.Find("Text Area"), "", 13, TextC, 0, 0, 100, 20, TextAlignmentOptions.MidlineLeft, wrap: false);
        _urlTail.rectTransform.anchorMin = Vector2.zero;
        _urlTail.rectTransform.anchorMax = Vector2.one;
        _urlTail.rectTransform.offsetMin = _urlTail.rectTransform.offsetMax = Vector2.zero;
    }

    private (TMP_InputField input, RectTransform rt, TextMeshProUGUI text) MakeInput(string name, float size, float radius, string placeholder, int limit)
    {
        var go = new GameObject(name);
        go.SetActive(false);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(_holder!, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        var bg = go.AddComponent<Image>();
        bg.sprite = Sprites.Round(radius, 1, InputFill, GoldDim);
        bg.type = Image.Type.Sliced;
        var area = new GameObject("Text Area").AddComponent<RectTransform>();
        area.SetParent(rt, false);
        area.anchorMin = Vector2.zero; area.anchorMax = Vector2.one;
        area.offsetMin = new Vector2(radius > 10 ? 14 : 10, 4); area.offsetMax = new Vector2(radius > 10 ? -14 : -10, -4);
        area.gameObject.AddComponent<RectMask2D>();
        TextMeshProUGUI Part(string n, Color c)
        {
            var t = new GameObject(n).AddComponent<TextMeshProUGUI>();
            t.rectTransform.SetParent(area, false);
            t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
            t.rectTransform.offsetMin = t.rectTransform.offsetMax = Vector2.zero;
            if (_body != null) t.font = _body;
            t.fontSize = size; t.color = c; t.alignment = TextAlignmentOptions.MidlineLeft;
            t.enableWordWrapping = false; t.richText = false; t.raycastTarget = false;
            return t;
        }
        var text = Part("Text", TextC);
        var ph = Part("Placeholder", new Color32(125, 119, 110, 255));
        ph.text = placeholder;
        var input = go.AddComponent<TMP_InputField>();
        input.textViewport = area;
        input.textComponent = text;
        input.placeholder = ph;
        if (_body != null) input.fontAsset = _body;
        input.pointSize = size;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.characterLimit = limit;
        input.richText = false;
        input.onFocusSelectAll = false;
        input.resetOnDeActivation = false;
        input.caretWidth = 2;
        input.customCaretColor = true;
        input.caretColor = NameC;
        input.selectionColor = new Color(NameC.r / 255f, NameC.g / 255f, NameC.b / 255f, 0.3f);
        input.targetGraphic = bg;
        var nav = input.navigation;
        nav.mode = Navigation.Mode.None;
        input.navigation = nav;
        try { go.AddComponent<Nivalis.InputFieldPreventExternalInput>(); } catch (Exception e) { Log.Debug("phone input: " + e.Message); }
        go.SetActive(true);
        return (input, rt, text);
    }

    /// <summary>Before a redraw: the boxes back to where they're kept (the screen they were on is about to go).</summary>
    private void ParkInputs()
    {
        if (_composeRt != null && _holder != null) _composeRt.SetParent(_holder, false);
        if (_urlRt != null && _holder != null) _urlRt.SetParent(_holder, false);
    }

    private void PlaceCompose(RectTransform bar)
    {
        if (_composeRt == null || _compose == null) return;
        float fieldW = ScreenW - 2 * G - 36 - 8;
        _composeRt.SetParent(bar, false);
        _composeRt.anchoredPosition = new Vector2(G, -9);
        _composeRt.sizeDelta = new Vector2(fieldW, 36);
        _sendRt = Box(bar, "Send", ScreenW - G - 36, 9, 36, 36);
        _sendImg = Img(_sendRt, Sprites.Round(18, 0, White, White), NameC);
        _sendIcon = Icon(_sendRt, Sprites.ArrowIcon, 9.5f, 9.5f, 17, Dark);
        Clickable(_sendRt, SendNow, radius: 18);
        SkinCompose(light: false, "Text message"); // the phone's own skin (Nightline's screens put on the light one after)
        TintSend();
        // A conversation opens with the box ready to type (unless you left it with Esc).
        if (!_composeLeft) _focusIn = 2;
    }

    private void PlaceUrl(RectTransform row, float width)
    {
        if (_urlRt == null || _url == null) return;
        _urlRt.SetParent(row, false);
        _urlRt.anchoredPosition = Vector2.zero;
        _urlRt.sizeDelta = new Vector2(width, 34);
        if (!_url.isFocused && _url.text != View.WallpaperUrl && !View.WallpaperBusy) _url.text = View.WallpaperUrl;
    }

    private bool ComposeEmpty => _compose == null || _compose.text.Trim().Length == 0;

    private void TintSend()
    {
        bool can = !ComposeEmpty || (IsNightline && ScreenName == "nightline" && View.LineAttach.Length > 0); // FB-80: a photo alone
        if (_sendImg != null) _sendImg.color = can ? NameC : Off;
        if (_sendIcon != null) _sendIcon.color = can ? Dark : OffText;
    }

    private void TickInputs(Vector2 pos, bool click)
    {
        if (_compose != null && _composeRt != null && _composeRt.gameObject.activeInHierarchy)
        {
            TintSend();
            // CF-20 (a player): a click on the box gives it the keys itself, as the chat window's does since CF-15 (the game's own
            // UI click can't be counted on while the mod holds the cursor).
            if (click && Over(_composeRt, pos)) { _composeLeft = false; if (!_compose.isFocused) _focusIn = 1; }
            if (_focusIn > 0 && --_focusIn == 0 && !_compose.isFocused) { Focus(_compose); UpdateHint(); }
        }
        // The link box: unfocused, the end of the link fitted to the box (…street-loop.gif); focused, all of it.
        if (_url != null && _urlTail != null && _urlText != null && _urlRt != null && _urlRt.gameObject.activeInHierarchy)
        {
            // CF-20 (a player: "cant click into the wallpaper box to paste a link"): a click on it (where the list shows it) focuses it.
            if (click && !_url.isFocused && Over(_urlRt, pos) && (_viewport == null || Over(_viewport, pos))) Focus(_url);
            bool focused = _url.isFocused;
            _urlTail.gameObject.SetActive(!focused && _url.text.Length > 0);
            _urlText.alpha = focused ? 1f : 0f;
            if (!focused && _url.text.Length > 0)
            {
                float w = _urlTail.rectTransform.rect.width;
                if (_urlTail.text.Length == 0 || _lastTail != _url.text + "|" + w) { FitTail(_urlTail, _url.text, w); _lastTail = _url.text + "|" + w; }
            }
            if (click) UpdateHint();
        }
    }

    private string _lastTail = "";

    /// <summary>A text box takes the keys: selected in the game's EventSystem and activated (CF-15's way).</summary>
    private static void Focus(TMP_InputField box)
    {
        try { UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(box.gameObject); } catch { }
        try { box.ActivateInputField(); box.Select(); } catch (Exception e) { Log.Debug("phone focus: " + e.Message); }
    }

    private void SendNow()
    {
        if (_compose == null) return;
        // FB-80: on Nightline's feed a photo can go up with no words.
        if (ComposeEmpty && !(IsNightline && ScreenName == "nightline" && View.LineAttach.Length > 0)) return;
        if (IsNightline)
        {
            string line = _compose.text.Trim();
            _compose.text = "";
            SendLine(line);
            _composeLeft = false;
            _focusIn = 2;
            return;
        }
        if (ScreenName != "thread") return;
        string text = _compose.text.Trim();
        _compose.text = "";
        Send?.Invoke(Subject, text);
        // The box keeps the keyboard after a send (as the chat box does).
        _composeLeft = false;
        _focusIn = 2;
    }

    private void SetNow()
    {
        if (_url == null || View.WallpaperBusy) return;
        string link = _url.text.Trim();
        try { _url.DeactivateInputField(); } catch { }
        SetWallpaper?.Invoke(link);
    }

    /// <summary>The link box shows this (after Default, or a link that was set).</summary>
    public void SetLinkText(string link) { if (_url != null && !_url.isFocused) _url.text = link ?? ""; }

    // ------------------------------------------------------------------ the wallpaper

    private readonly List<(RawImage img, float aspect)> _wallImgs = new();
    private Texture2D? _still, _gifTex, _default, _scrim, _fadeDownTex, _fadeUpTex;
    private List<(Il2CppStructArray<byte> px, float seconds)>? _frames;
    private int _frame;
    private float _frameLeft;

    private bool HasCustomWall => _still != null || _gifTex != null;

    private Texture2D Current => _gifTex ?? _still ?? (_default ??= DefaultWall());

    /// <summary>A picture as the wallpaper (PNG, JPG or BMP read into a texture by the runtime); null = the default.</summary>
    public void SetStill(Texture2D? tex)
    {
        ClearWall();
        _still = tex;
        ShowWallTexture();
    }

    /// <summary>A GIF as the wallpaper: its frames (RGBA, bottom row first), played at their own times.</summary>
    public void SetGif(int w, int h, List<(byte[] px, int ms)> frames)
    {
        ClearWall();
        if (frames.Count == 0) { ShowWallTexture(); return; }
        _gifTex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        _frames = new List<(Il2CppStructArray<byte>, float)>();
        foreach (var (px, ms) in frames) _frames.Add((new Il2CppStructArray<byte>(px), Mathf.Max(0.02f, ms / 1000f)));
        _frame = 0;
        _frameLeft = _frames[0].seconds;
        _gifTex.LoadRawTextureData(_frames[0].px);
        _gifTex.Apply(false, false);
        ShowWallTexture();
    }

    private void ClearWall()
    {
        if (_still != null) { UnityEngine.Object.Destroy(_still); _still = null; }
        if (_gifTex != null) { UnityEngine.Object.Destroy(_gifTex); _gifTex = null; }
        _frames = null;
    }

    private void ShowWallTexture()
    {
        var tex = Current;
        foreach (var (img, aspect) in _wallImgs)
        {
            if (img == null) continue;
            img.texture = tex;
            // Cover: the picture fills the area, centred, cut on the long side (never stretched).
            float ta = tex.height > 0 ? (float)tex.width / tex.height : 1f;
            img.uvRect = ta > aspect ? new Rect((1 - aspect / ta) / 2, 0, aspect / ta, 1) : new Rect(0, (1 - ta / aspect) / 2, 1, ta / aspect);
        }
    }

    private void TickWallpaper(float dt)
    {
        if (_frames == null || _gifTex == null || _frames.Count < 2 || _wallImgs.Count == 0) return;
        _frameLeft -= dt;
        if (_frameLeft > 0) return;
        int guard = 0;
        while (_frameLeft <= 0 && guard++ < _frames.Count) { _frame = (_frame + 1) % _frames.Count; _frameLeft += _frames[_frame].seconds; }
        if (_frameLeft <= 0) _frameLeft = _frames[_frame].seconds;
        _gifTex.LoadRawTextureData(_frames[_frame].px);
        _gifTex.Apply(false, false);
    }

    private static Texture2D NewTex(int w, int h) =>
        new(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };

    /// <summary>The default wallpaper: warm gold light at the top left, a little blue at the bottom right, on dark brown.</summary>
    private static Texture2D DefaultWall()
    {
        const int w = 120, h = 194;
        var tex = NewTex(w, h);
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = x / (float)(w - 1), v = 1f - y / (float)(h - 1); // v = 0 at the top
                var c = Color.Lerp(new Color(0.165f, 0.149f, 0.133f), new Color(0.078f, 0.071f, 0.063f), v * 0.9f + u * 0.1f);
                float gold = Mathf.Clamp01(1f - new Vector2((u - 0.2f) / 1.2f, (v - 0.1f) / 0.7f).magnitude) * 0.35f;
                float blue = Mathf.Clamp01(1f - new Vector2((u - 0.9f) / 0.9f, (v - 0.9f) / 0.6f).magnitude) * 0.28f;
                c = Color.Lerp(c, new Color(0.85f, 0.65f, 0.35f), gold);
                c = Color.Lerp(c, new Color(0.37f, 0.66f, 0.91f), blue);
                px[y * w + x] = c;
            }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        return tex;
    }

    /// <summary>A column fading from <paramref name="top"/> to <paramref name="bottom"/> alpha in the deep screen colour (or black).</summary>
    private static Texture2D Gradient(Func<float, float> alphaAtTop0, Color32 colour)
    {
        const int h = 64;
        var tex = NewTex(1, h);
        var px = new Color32[h];
        for (int y = 0; y < h; y++)
        {
            float fromTop = 1f - y / (float)(h - 1);
            var c = colour;
            c.a = (byte)Mathf.RoundToInt(255 * Mathf.Clamp01(alphaAtTop0(fromTop)));
            px[y] = c;
        }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        return tex;
    }

    private Texture2D Scrim => _scrim ??= Gradient(t => t < 0.32f ? 0.45f * (1 - t / 0.32f) : t > 0.62f ? 0.55f * (t - 0.62f) / 0.38f : 0f, new Color32(0, 0, 0, 255));
    private Texture2D FadeDown => _fadeDownTex ??= Gradient(t => 1f - t, Deep);
    private Texture2D FadeUp => _fadeUpTex ??= Gradient(t => t, Deep);

    private static RawImage Raw(RectTransform parent, Texture2D tex, float x, float y, float w, float h)
    {
        var raw = Box(parent, "Shade", x, y, w, h).gameObject.AddComponent<RawImage>();
        raw.texture = tex;
        raw.raycastTarget = false;
        return raw;
    }

    // ------------------------------------------------------------------ the card a text comes as (while the phone is away)

    private RectTransform? _toast;
    private TextMeshProUGUI? _toastTitle, _toastText, _toastKey, _toastInitial;
    private Image? _toastAvatar, _toastSys;
    private CanvasGroup? _toastFade;
    private float _toastLeft;
    private readonly Queue<(string title, string text, string initial, string kind, bool system)> _toasts = new();
    /// <summary>Cards wait while this is true (a chat is open: they show once it's over).</summary>
    public bool HoldToasts;

    private void BuildToast()
    {
        _toast = new GameObject("PhoneCard").AddComponent<RectTransform>();
        _toast.SetParent(_root!.transform, false);
        _toast.anchorMin = _toast.anchorMax = new Vector2(1, 1);
        _toast.pivot = new Vector2(1, 1);
        _toast.anchoredPosition = new Vector2(-24, -24);
        _toast.sizeDelta = new Vector2(340, 58);
        Img(_toast, Sprites.Round(14, 1.5f, new Color32(36, 33, 30, 245), GoldDim));
        _toastFade = _toast.gameObject.AddComponent<CanvasGroup>();
        var av = Box(_toast, "Avatar", 14, 10, 38, 38);
        _toastAvatar = Img(av, Sprites.Round(19, 0, White, White), Gold);
        _toastInitial = Label(av, "", 16, Dark, 0, 8, 38, 22, TextAlignmentOptions.Top, bold: true, wrap: false);
        var sys = Box(_toast, "System", 14, 10, 38, 38);
        _toastSys = Img(sys, Sprites.Round(19, 1, new Color32(58, 52, 45, 255), GoldDim));
        Icon(sys, Sprites.ChatIcon, 9, 9, 20, NameC);
        _toastTitle = Label(_toast, "", 14, NameC, 62, 10, 220, 18, TextAlignmentOptions.TopLeft, bold: true, wrap: false);
        _toastTitle.characterSpacing = 7;
        _toastText = Label(_toast, "", 14, BodyC, 62, 29, 220, 18, TextAlignmentOptions.TopLeft, wrap: false);
        var key = Box(_toast, "Key", 340 - 14 - 26, 19, 26, 20);
        Img(key, Sprites.Round(5, 1, Clear, GoldDim));
        _toastKey = Label(key, "N", 13, TextC, 0, 1, 26, 18, TextAlignmentOptions.Top, bold: true, wrap: false);
        _toast.gameObject.SetActive(false);
    }

    /// <summary>A card at the top right: a text from someone (their initial), or the phone saying something (system).</summary>
    public void Toast(string title, string text, string kind = "known", bool system = false)
    {
        string initial = string.IsNullOrWhiteSpace(title) ? "?" : char.ToUpperInvariant(title.TrimStart()[0]).ToString(); // PH-A13: a blank name
        if (_toasts.Count < 6) _toasts.Enqueue((title, text, initial, kind, system));
    }

    private void TickToast(float dt)
    {
        if (_toast == null || _toastFade == null) return;
        if (_toastLeft > 0)
        {
            _toastLeft -= dt;
            _toastFade.alpha = Mathf.Clamp01(Mathf.Min(_toastLeft / 0.4f, (5.4f - _toastLeft) / 0.25f));
            if (_toastLeft <= 0 || _visible) { _toast.gameObject.SetActive(false); _toastLeft = 0; }
            return;
        }
        if (_toasts.Count == 0 || HoldToasts || _visible) return;
        var (title, text, initial, kind, system) = _toasts.Dequeue();
        if (_toastTitle != null) FitLine(_toastTitle, title.ToUpperInvariant(), 220);
        if (_toastText != null)
        {
            _toastText.enableWordWrapping = system;
            _toastText.overflowMode = TextOverflowModes.Ellipsis;
            _toastText.rectTransform.sizeDelta = new Vector2(220, system ? 36 : 18);
            if (system) _toastText.text = text; else FitLine(_toastText, text, 220);
        }
        if (_toastKey != null) _toastKey.text = View.Key;
        if (_toastAvatar != null) { _toastAvatar.gameObject.SetActive(!system); _toastAvatar.color = AvatarColour(kind); }
        if (_toastInitial != null) _toastInitial.text = initial;
        if (_toastSys != null) _toastSys.gameObject.SetActive(system);
        _toast.sizeDelta = new Vector2(340, system ? 74 : 58);
        _toast.gameObject.SetActive(true);
        _toastLeft = 5.4f;
    }

    // ------------------------------------------------------------------ voice notes (play and stop, the wave)

    private readonly List<(string file, List<Image> bars, Image icon, RectTransform button, TextMeshProUGUI len, float seconds)> _noteBars = new();
    private AudioSource? _noteSource;
    private AudioClip? _noteClip;
    /// <summary>The playing clip is the game's (FB-75): never destroyed by the phone.</summary>
    private bool _noteGame;

    /// <summary>A game recording is playing (its clip isn't to be let go of meanwhile).</summary>
    public bool PlayingGameVoice => _noteGame && _noteSource != null && _noteSource.isPlaying;
    private string _noteFile = "";
    private float _noteStarted, _noteLength;

    /// <summary>
    /// A voice note under their text: a round play/stop button, the wave as rounded strips (their heights from the note
    /// itself), its length; the strips fill gold as it plays.
    /// </summary>
    private void VoiceBar(RectTransform list, float x, float y, PhoneView.Line l)
    {
        const float w = 208, h = 40;
        var bar = Box(list, "VoiceNote", x, y, w, h);
        string dir = VoiceDir?.Invoke() ?? "";
        bool there = (GameClip != null && Phone.PhoneBook.IsGameVoice(l.Voice)) // FB-75: the game's own recording, played from the game
                     || (dir.Length > 0 && Phone.VoiceNote.SafeName(l.Voice) && System.IO.File.Exists(System.IO.Path.Combine(dir, l.Voice)));
        Img(bar, Sprites.Round(14, 1, new Color32(48, 43, 37, 255), GoldDim));
        var btn = Box(bar, "PlayStop", 6, 6, 28, 28);
        Img(btn, Sprites.Round(14, 0, there ? NameC : Off, there ? NameC : Off));
        bool playing = _noteFile == l.Voice && _noteSource != null && _noteSource.isPlaying;
        var icon = Icon(btn, playing ? Sprites.StopIcon : Sprites.PlayIcon, 7, 7, 14, there ? Dark : OffText);
        var strips = new List<Image>();
        // The strips spread evenly over the track between the button and the length (their heights from the note itself).
        float sx = 42, track = w - 42 - 44, maxH = 22, pitch = l.Wave.Length > 1 ? (track - 3) / (l.Wave.Length - 1) : 0;
        for (int i = 0; i < l.Wave.Length; i++)
        {
            float bh = Mathf.Max(3, maxH * l.Wave[i] / VoiceNoteMax);
            strips.Add(Img(Box(bar, "Strip", Mathf.Round(sx + i * pitch), (h - bh) / 2, 3, bh), Sprites.Round(1.5f, 0, White, White), there ? new Color32(154, 148, 139, 255) : new Color32(90, 85, 78, 255)));
        }
        var len = Label(bar, there ? Phone.VoiceNote.Length(l.VoiceSeconds) : "gone", 13, Muted, w - 42, 12, 36, 17, TextAlignmentOptions.TopRight, wrap: false);
        // Ava's made voice (with AvaVoice on) is marked, so it's never taken for one of her recordings.
        if (l.MadeVoice) Label(list, "made voice", 13, Muted, x + w + 6, y + 12, 80, 17, wrap: false);
        string file = l.Voice;
        _noteBars.Add((file, strips, icon, btn, len, l.VoiceSeconds));
        if (there) Clickable(btn, () => ToggleNote(file), radius: 14);
    }

    private const float VoiceNoteMax = Phone.VoiceNote.MaxHeight;

    private void ToggleNote(string file)
    {
        if (_noteFile == file && _noteSource != null && _noteSource.isPlaying) { StopNote(); return; }
        StopNote();
        if (Phone.PhoneBook.IsGameVoice(file) && GameClip != null)
        {
            try
            {
                var clip = GameClip(file);
                if (clip == null) { Toast("Phone", "The game hasn't got that line's voice right now.", system: true); return; }
                EnsureNoteSource();
                _noteGame = true;
                _noteClip = clip;
                _noteSource!.clip = clip;
                _noteSource.volume = 0.9f;
                _noteSource.Play();
                _noteFile = file;
                _noteStarted = Time.unscaledTime;
                _noteLength = clip.length;
            }
            catch (Exception e) { Log.Debug("voice note (game): " + e.Message); StopNote(); }
            TickNotes();
            return;
        }
        try
        {
            string dir = VoiceDir?.Invoke() ?? "";
            if (dir.Length == 0 || !Phone.VoiceNote.SafeName(file)) return;
            string path = System.IO.Path.Combine(dir, file);
            if (!System.IO.File.Exists(path)) { Toast("Phone", "That voice note isn't on this PC any more.", system: true); return; }
            var (samples, rate, channels) = Phone.VoiceNote.FromWav(System.IO.File.ReadAllBytes(path));
            if (samples.Length == 0) return;
            EnsureNoteSource();
            _noteGame = false;
            _noteClip = AudioClip.Create("PeopleOfNivalis.VoiceNote", samples.Length / Math.Max(1, channels), Math.Max(1, channels), rate, false);
            _noteClip.SetData(samples, 0);
            _noteSource!.clip = _noteClip;
            _noteSource.volume = 0.9f;
            _noteSource.Play();
            _noteFile = file;
            _noteStarted = Time.unscaledTime;
            _noteLength = _noteClip.length;
        }
        catch (Exception e) { Log.Debug("voice note: " + e.Message); StopNote(); }
        TickNotes();
    }

    private void EnsureNoteSource()
    {
        if (_noteSource != null) return;
        _noteSource = _root!.AddComponent<AudioSource>();
        _noteSource.spatialBlend = 0f;
        _noteSource.ignoreListenerPause = true;
        _noteSource.playOnAwake = false;
    }

    /// <summary>The note's clip let go: ours destroyed, the game's only let go of (it's the game's own asset).</summary>
    private void FreeNoteClip()
    {
        if (_noteClip != null && !_noteGame) { try { UnityEngine.Object.Destroy(_noteClip); } catch { } }
        try { if (_noteGame && _noteSource != null) _noteSource.clip = null; } catch { }
        _noteClip = null;
        _noteGame = false;
    }

    private void StopNote()
    {
        try { if (_noteSource != null) _noteSource.Stop(); } catch { }
        FreeNoteClip();
        _noteFile = "";
        TickNotes();
    }

    /// <summary>Every frame: the playing note's strips fill gold as it goes; its button shows stop, the others play.</summary>
    private void TickNotes()
    {
        bool playing = _noteFile.Length > 0 && _noteSource != null && _noteSource.isPlaying;
        if (!playing && _noteFile.Length > 0 && Time.unscaledTime - _noteStarted > _noteLength + 0.2f) { _noteFile = ""; FreeNoteClip(); }
        float done = playing && _noteLength > 0 ? Mathf.Clamp01((Time.unscaledTime - _noteStarted) / _noteLength) : 0f;
        foreach (var (file, bars, icon, _, len, seconds) in _noteBars)
        {
            bool mine = playing && file == _noteFile;
            if (icon != null) icon.sprite = mine ? Sprites.StopIcon : Sprites.PlayIcon;
            // While it plays: how far in ("0:02"); otherwise its length.
            if (len != null && len.text != "gone") len.text = mine ? Phone.VoiceNote.Length(done * _noteLength) : Phone.VoiceNote.Length(seconds);
            for (int i = 0; i < bars.Count; i++)
                if (bars[i] != null) bars[i].color = mine && (i + 0.5f) / bars.Count <= done ? (Color)NameC : new Color32(154, 148, 139, 255);
        }
    }
}
