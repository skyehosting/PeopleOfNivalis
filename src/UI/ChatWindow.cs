using System;
using System.Collections.Generic;
using System.Linq;
using Nivalis;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using NivalisAIChat.Game;

namespace NivalisAIChat.UI;

// StoryNpc / StoryPlayer / StoryOther (FB-50): lines from the game's story in the history (StoryOther's text: name, \u001F, line).
internal enum LineKind { Npc, Player, Notice, Error, Divider, PastNpc, PastPlayer, Output, StoryNpc, StoryPlayer, StoryOther, Action }

/// <summary>How fast their words appear when there's no voice to follow.</summary>
public enum TextSpeed { Slow, Medium, Fast, Instant }

/// <summary>
/// The chat panel and the "[T] Talk" prompt, built from uGUI + TextMeshPro at runtime and styled after the
/// game's own dialogue box: warm charcoal bubble, tan/gold rounded border, tail at the bottom right, gold
/// uppercase speaker name, light grey Barlow text, and a bordered pill (here: their mood) on the top edge.
/// </summary>
internal sealed partial class ChatWindow
{
    private static readonly Color32 Fill = new(36, 33, 30, 238);
    private static readonly Color32 Gold = new(196, 160, 98, 255);
    private static readonly Color32 GoldDim = new(118, 97, 62, 255);
    private static readonly Color32 InputFill = new(22, 20, 18, 246);
    private static readonly Color32 PillFill = new(26, 44, 42, 240);
    private static readonly Color NameColor = new Color32(217, 165, 90, 255);
    private static readonly Color BodyColor = new Color32(207, 203, 196, 255);
    private static readonly Color PillText = new Color32(150, 196, 168, 255);
    private const string PlayerLabelHex = "#5FA8E8";                       // you: blue
    private const string NpcLabelHex = "#C4A062", PastNpcLabelHex = "#8C7550"; // them: the border's gold
    private const string PlayerTextHex = "#ABA79F";
    private const string NoticeHex = "#8A857D";
    /// <summary>Roleplay: what they do as they speak, in a gold-brown, slanted, centred, never bold (the author's spec).</summary>
    private const string ActionHex = "#B48A52";
    private const string ErrorHex = "#E0806E";
    private const string PastHex = "#8F8A82", PastLabelHex = "#4C7FA8", PastPlayerHex = "#7E7A73", DividerHex = "#7A6A4E";
    private const string KeyHex = "#D9A55A";

    private GameObject? _root;
    private RectTransform? _panel, _pill;
    private TextMeshProUGUI? _title, _pillText, _status, _prompt;
    private SmoothScroll? _chatScroll, _infoScroller;
    /// <summary>Most lines kept in the window (config HistoryShown).</summary>
    public int MaxLines = 150;
    private RectTransform? _content;
    private TMP_InputField? _input;
    private TMP_FontAsset? _titleFont, _bodyFont;
    private readonly List<GameObject> _lines = new();
    /// <summary>Their lines, kept so the label can change from STRANGER to their name once they tell you.</summary>
    private readonly List<(GameObject go, TextMeshProUGUI text, bool past, string body)> _npcLines = new();
    private string _npcName = "Stranger";
    /// <summary><see cref="Hanging"/>'s measured label widths, by (font, size, character spacing, label).</summary>
    private static readonly Dictionary<(IntPtr font, float size, float spacing, string label), float> LabelWidths = new();

    private int _focusInFrames = -1;
    private bool _scrollPending;

    public bool Visible => _panel != null && _panel.gameObject.activeSelf;
    public bool Built => _root != null;
    public bool InputFocused => _input != null && _input.isFocused;
    public static string KeyColor => KeyHex;
    /// <summary>The fonts the window picked (the settings menu uses the same ones).</summary>
    public (TMP_FontAsset? title, TMP_FontAsset? body) Fonts => (_titleFont, _bodyFont);

    public void Build(float scale)
    {
        if (_root != null) return;
        LabelWidths.Clear();
        (_titleFont, _bodyFont) = PickFonts();

        _root = new GameObject("NivalisAIChat.UI");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32000;
        var scaler = _root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        _root.AddComponent<GraphicRaycaster>();

        // --- talk prompt under the crosshair
        _prompt = Text(_root.transform, "TalkPrompt", 24, BodyColor, TextAlignmentOptions.Center, _bodyFont);
        var prt = _prompt.rectTransform;
        prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
        prt.pivot = new Vector2(0.5f, 1f);
        prt.anchoredPosition = new Vector2(0, -46);
        prt.sizeDelta = new Vector2(600, 40);
        _prompt.outlineWidth = 0.2f;
        _prompt.outlineColor = new Color32(0, 0, 0, 210);
        _prompt.gameObject.SetActive(false);

        // --- the bubble
        var panelGo = new GameObject("Panel");
        _panel = panelGo.AddComponent<RectTransform>();
        _panel.SetParent(_root.transform, false);
        _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 0f);
        _panel.pivot = new Vector2(0.5f, 0f);
        _panel.sizeDelta = new Vector2(900, 470);
        _panel.anchoredPosition = new Vector2(0, 90);
        _panel.localScale = Vector3.one * Mathf.Clamp(scale, 0.6f, 2f);
        var bg = panelGo.AddComponent<Image>();
        bg.sprite = Sprites.Bubble;
        bg.type = Image.Type.Sliced;
        bg.color = Color.white;

        var tail = Rect(_panel, "Tail", new Vector2(1, 0), new Vector2(1, 0), Vector2.zero, Vector2.zero);
        tail.pivot = new Vector2(1, 0);
        tail.sizeDelta = new Vector2(Sprites.TailW, Sprites.TailH);
        tail.anchoredPosition = new Vector2(0, -Sprites.TailDrop);
        var tailImg = tail.gameObject.AddComponent<Image>();
        tailImg.sprite = Sprites.Tail;
        tailImg.raycastTarget = false;
        tail.SetAsFirstSibling();

        // --- speaker name, gold uppercase like the game's dialogue box
        _title = Text(_panel, "Name", 32, NameColor, TextAlignmentOptions.TopLeft, _titleFont);
        Place(_title.rectTransform, new Vector2(0, 1), new Vector2(0.72f, 1), new Vector2(30, -66), new Vector2(0, -20));
        _title.fontStyle = FontStyles.UpperCase;
        _title.characterSpacing = 1.5f;
        // One line, cut with an ellipsis (a small window, a long name: never onto the conversation; the (i) panel has it whole).
        _title.enableWordWrapping = false;
        _title.overflowMode = TextOverflowModes.Ellipsis;

        // --- mood pill sitting on the top border
        var pillGo = new GameObject("MoodPill");
        _pill = pillGo.AddComponent<RectTransform>();
        _pill.SetParent(_panel, false);
        _pill.anchorMin = _pill.anchorMax = new Vector2(1, 1);
        _pill.pivot = new Vector2(1, 0.5f);
        _pill.anchoredPosition = new Vector2(-46, 0);
        _pill.sizeDelta = new Vector2(220, 38);
        var pillImg = pillGo.AddComponent<Image>();
        pillImg.sprite = Sprites.Pill;
        pillImg.type = Image.Type.Sliced;
        pillImg.raycastTarget = false;
        _pillText = Text(_pill, "Mood", 19, PillText, TextAlignmentOptions.Center, _bodyFont);
        Place(_pillText.rectTransform, Vector2.zero, Vector2.one, new Vector2(16, 2), new Vector2(-16, -2));
        _pillText.enableWordWrapping = false;
        _pill.gameObject.SetActive(false);
        BuildRelationshipPill(); // the relationship bar, beside it

        // --- scrolling conversation
        var scrollRt = Rect(_panel, "Log", new Vector2(0, 0), new Vector2(1, 1), new Vector2(26, 104), new Vector2(-22, -74));
        _logRt = scrollRt;
        _logFade = scrollRt.gameObject.AddComponent<CanvasGroup>(); // fades out for their last line in keyboard mode (CF-07)
        var viewport = Rect(scrollRt, "Viewport", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        viewport.gameObject.AddComponent<RectMask2D>();
        _content = Rect(viewport, "Content", new Vector2(0, 1), new Vector2(1, 1), Vector2.zero, Vector2.zero);
        _content.pivot = new Vector2(0.5f, 1f);
        var layout = _content.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        layout.spacing = 10f;
        layout.padding = new RectOffset(4, 12, 4, 6);
        var fitter = _content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        _chatScroll = new SmoothScroll(viewport, _content);

        // --- "thinking…" line
        _status = Text(_panel, "Status", 18, new Color32(138, 133, 125, 255), TextAlignmentOptions.BottomLeft, _bodyFont);
        Place(_status.rectTransform, new Vector2(0, 0), new Vector2(1, 0), new Vector2(30, 72), new Vector2(-30, 100));
        _status.fontStyle = FontStyles.Italic;

        _input = BuildInput(_panel);
        BuildInfo(Mathf.Clamp(scale, 0.6f, 2f));
        BuildGive();
        BuildBark(Mathf.Clamp(scale, 0.6f, 2f));
        BuildColumn();       // the keyboard and settings buttons under the "i" (CF-07/CF-08)
        BuildKeyboardMode(); // their last line and the on-screen keyboard (CF-07)
        BuildLayoutControls(); // moved by its top strip, resized by its corner; small, the pills are circles

        _panel.gameObject.SetActive(false);
        if (EventSystem.current == null) Log.Warn("No EventSystem found; the chat box may not take keyboard focus.");
    }

    private TMP_InputField BuildInput(RectTransform parent)
    {
        var go = new GameObject("Input");
        go.SetActive(false);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        Place(rt, new Vector2(0, 0), new Vector2(1, 0), new Vector2(24, 18), new Vector2(-24, 64));
        _inputRt = rt;
        var bg = go.AddComponent<Image>();
        bg.sprite = Sprites.Field;
        bg.type = Image.Type.Sliced;
        bg.color = Color.white;

        var area = Rect(rt, "Text Area", Vector2.zero, Vector2.one, new Vector2(16, 6), new Vector2(-16, -6));
        area.gameObject.AddComponent<RectMask2D>();
        var text = Text(area, "Text", 21, BodyColor, TextAlignmentOptions.MidlineLeft, _bodyFont);
        Place(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        text.enableWordWrapping = false;
        text.richText = false;
        var ph = Text(area, "Placeholder", 21, new Color32(110, 105, 98, 255), TextAlignmentOptions.MidlineLeft, _bodyFont);
        Place(ph.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        ph.fontStyle = FontStyles.Italic;
        ph.text = ChatHint;
        ph.enableWordWrapping = false;
        _placeholder = ph;

        var input = go.AddComponent<TMP_InputField>();
        input.textViewport = area;
        input.textComponent = text;
        input.placeholder = ph;
        if (_bodyFont != null) input.fontAsset = _bodyFont;
        input.pointSize = 21;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.characterLimit = 400;
        input.richText = false;
        input.onFocusSelectAll = false;
        input.resetOnDeActivation = false;
        input.caretWidth = 2;
        input.customCaretColor = true;
        input.caretColor = NameColor;
        input.selectionColor = new Color(NameColor.r, NameColor.g, NameColor.b, 0.3f);
        input.targetGraphic = bg;
        // The d-pad drives the mod's own highlight: it never moves the game's UI selection off this box (CF-07).
        var nav = input.navigation;
        nav.mode = Navigation.Mode.None;
        input.navigation = nav;
        try { go.AddComponent<InputFieldPreventExternalInput>(); }
        catch (Exception e) { Log.Debug("prevent-input component: " + e.Message); }
        go.SetActive(true);
        return input;
    }

    // ------------------------------------------------------------------ public surface

    /// <param name="sprites">The sprites a &lt;sprite&gt; in the text comes from (the game's controller icons, CF-12).</param>
    public void ShowPrompt(string? text, TMP_SpriteAsset? sprites = null)
    {
        if (_prompt == null) return;
        bool show = !string.IsNullOrEmpty(text) && !Visible;
        if (show && sprites != null)
            try { if (_prompt.spriteAsset != sprites) _prompt.spriteAsset = sprites; }
            catch { } // review CF12-3: a sheet that can't be set leaves the prompt as it was (its words still show)
        if (show && _prompt.text != text) _prompt.text = text!;
        if (_prompt.gameObject.activeSelf != show) _prompt.gameObject.SetActive(show);
    }

    private TextMeshProUGUI? _placeholder;
    private const string ChatHint = "Say something…      Enter to say  ·  Esc to leave";
    private const string ConsoleHint = "Type a command…      /help for the list  ·  Esc to close";

    /// <summary>The window is showing the command console (not a conversation).</summary>
    public bool ConsoleMode { get; private set; }

    public void Open(string title, string subtitle)
    {
        if (_panel == null) return;
        ConsoleMode = false;
        Clear();
        SetRelationship(null); // shown again only for a conversation with someone (RefreshHeader)
        SetHeader(title, subtitle);
        SetStatus("");
        if (_placeholder != null) _placeholder.text = ChatHint;
        if (_input != null) { _input.text = ""; _input.interactable = true; }
        _lapseSince = -1f; // CF-15: a new window, no lapse yet
        _focusLapseSaid = false;
        _lastNpcBody = "";
        _suspended = false;
        _columnFocus = -1;
        _settingsClicked = _backPressed = _padSubmit = false;
        SetKeyboardMode(false, instant: true);
        _panel.gameObject.SetActive(true);
        PlaceColumn();
        ShowPrompt(null);
        _focusInFrames = 2; // not this frame: the talk key must not land in the box
    }

    /// <summary>The same window as a command console: no info button, a command hint in the box.</summary>
    public void OpenConsole(string title, string subtitle)
    {
        if (_panel == null) return;
        Open(title, subtitle);
        ConsoleMode = true;
        _npcName = "";
        if (_placeholder != null) _placeholder.text = ConsoleHint;
        if (_infoButton != null) _infoButton.gameObject.SetActive(false);
        PlaceColumn();
        RefreshHint();
    }

    public void Close()
    {
        if (_panel == null) return;
        try { if (_input != null) _input.DeactivateInputField(); } catch { }
        try { EventSystem.current?.SetSelectedGameObject(null); } catch { }
        ShowInfo(false);
        SetGive(null, "");
        SetKeyboardMode(false, instant: true);
        _suspended = false;
        EndLayoutDrag();
        _panel.gameObject.SetActive(false);
        _reveal = null;
        _afterReveal.Clear();
    }

    /// <summary>Name in the header; the subtitle (how they seem) goes in the pill on the top border.</summary>
    public void SetHeader(string title, string subtitle)
    {
        if (_title != null) _title.text = Escape(title);
        if (_pill == null || _pillText == null) return;
        bool show = !string.IsNullOrWhiteSpace(subtitle);
        _moodWords = show ? subtitle.Trim() : "";
        if (show)
        {
            _pillText.text = Escape(subtitle);
            _pillText.ForceMeshUpdate();
            _pill.sizeDelta = new Vector2(Mathf.Clamp(_pillText.preferredWidth + 40, 90, 460), 38);
        }
        if (_pill.gameObject.activeSelf != (show && !_collapsed)) _pill.gameObject.SetActive(show && !_collapsed);
        PlaceRelationshipPill();
        UpdateCollapse();
    }

    public void SetStatus(string status)
    {
        _thinking = null;
        if (_status == null) return;
        _status.text = Escape(status);
        _status.alpha = 1f;
    }

    private string? _thinking;
    private float _thinkingSince;
    private const float SlowAfterSeconds = 15f;

    /// <summary>"Oda is thinking" with dots that come and go and a gentle pulse, until the next SetStatus.</summary>
    public void SetThinking(string label)
    {
        _thinking = label;
        _thinkingSince = Time.unscaledTime;
        TickThinking();
    }

    private void TickThinking()
    {
        if (_thinking == null || _status == null) return;
        float t = Time.unscaledTime - _thinkingSince;
        int dots = (int)(t * 2.5f) % 4;
        // the dots take space even when hidden, so the text doesn't jiggle
        _status.text = Escape(_thinking) + "<color=#00000000>" + new string('.', 3 - dots) + "</color>";
        if (dots > 0) _status.text = Escape(_thinking) + new string('.', dots) + "<color=#00000000>" + new string('.', 3 - dots) + "</color>";
        // A slow answer: say it's still coming, and how to get out of waiting.
        if (t >= SlowAfterSeconds) _status.text += "  <size=80%><color=#9AA3B5>(still waiting on the AI · Esc to step away)</color></size>";
        _status.alpha = 0.6f + 0.4f * (0.5f + 0.5f * Mathf.Sin(t * 3.2f));
    }

    public void SetInputEnabled(bool enabled)
    {
        if (_input == null) return;
        _input.interactable = enabled;
        if (enabled) _focusInFrames = 1;
    }

    public string InputText => _input != null ? _input.text ?? "" : "";

    public void SetInputText(string text)
    {
        if (_input != null) _input.text = text;
    }

    /// <summary>Adds a line; returns it, so a message that didn't go through can be taken back out (<see cref="RemoveLine"/>).</summary>
    public GameObject? AddLine(LineKind kind, string speaker, string text, bool typewriter = false, float revealSeconds = 0f)
    {
        if (_content == null) return null;
        FinishReveal();
        var t = BuildLine(kind, text);
        _lines.Add(t.gameObject);
        if (_lines.Count > Math.Max(20, MaxLines))
        {
            var oldest = _lines[0];
            _npcLines.RemoveAll(l => l.go == oldest);
            UnityEngine.Object.Destroy(oldest);
            _lines.RemoveAt(0);
        }
        if (typewriter && kind == LineKind.Npc) StartReveal(t, revealSeconds);
        _scrollPending = true;
        return t.gameObject;
    }

    /// <summary>
    /// Lines put above everything in the window, in the order given: the earlier history, a batch at a time once it's
    /// been read off the main thread (PERF-4). Built as <see cref="AddLine"/> builds them, without finishing a reveal in
    /// progress. Only while the window holds fewer than MaxLines (the oldest of a batch are left out): false once it's
    /// full, so no older batch follows. Someone reading further up stays on the same lines; at the bottom, it stays there.
    /// </summary>
    public bool InsertAtTop(IReadOnlyList<(LineKind kind, string text)> lines)
    {
        if (_content == null || _chatScroll == null) return false;
        int room = Math.Max(20, MaxLines) - _lines.Count;
        if (room <= 0) return false;
        int skip = Math.Max(0, lines.Count - room);
        bool atBottom = _chatScroll.AtBottom;
        float before = _content.rect.height;
        for (int i = lines.Count - 1; i >= skip; i--) // the newest first, each put at the very top: they end up in order
        {
            var t = BuildLine(lines[i].kind, lines[i].text);
            t.transform.SetSiblingIndex(0);
            _lines.Insert(0, t.gameObject);
        }
        LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
        if (atBottom) _chatScroll.ToBottom(instant: true);
        else _chatScroll.Shift(_content.rect.height - before);
        return skip == 0;
    }

    /// <summary>One line of the conversation, its text escaped (nothing anyone wrote is read as markup); NPC lines are registered for renaming.</summary>
    private TextMeshProUGUI BuildLine(LineKind kind, string text)
    {
        string body = Escape(text);
        if (kind == LineKind.StoryOther)
        {
            int cut = text.IndexOf('\u001F');
            string other = cut > 0 ? text[..cut] : "";
            body = Escape(cut >= 0 ? text[(cut + 1)..] : text);
            var o = Text(_content!, "Line", 22, BodyColor, TextAlignmentOptions.TopLeft, _bodyFont);
            o.enableWordWrapping = true;
            o.lineSpacing = 4f;
            o.text = Hanging(o, $"<size=88%><color={PastLabelHex}><b>{Escape(other.ToUpperInvariant())}</b></color></size>   ", $"<color={PastHex}><i>{body}</i></color>");
            return o;
        }
        var t = Text(_content!, "Line", kind is LineKind.Npc or LineKind.PastNpc or LineKind.StoryNpc ? 25 : kind == LineKind.Divider ? 17 : kind == LineKind.Action ? 21 : 22, BodyColor,
            kind is LineKind.Divider or LineKind.Action ? TextAlignmentOptions.Top : TextAlignmentOptions.TopLeft, _bodyFont);
        t.enableWordWrapping = true;
        t.lineSpacing = 4f;
        if (kind == LineKind.Divider) t.characterSpacing = 3f;
        string line = kind switch
        {
            LineKind.Npc => NpcLine(t, false, body),
            LineKind.Player => Hanging(t, $"<color={PlayerLabelHex}><b>YOU</b></color>   ", $"<color={PlayerTextHex}>{body}</color>"),
            LineKind.PastNpc => NpcLine(t, true, body),
            LineKind.PastPlayer => Hanging(t, $"<color={PastLabelHex}><b>YOU</b></color>   ", $"<color={PastPlayerHex}>{body}</color>"),
            LineKind.StoryNpc => NpcLine(t, true, $"<i>{body}</i>"),
            LineKind.StoryPlayer => Hanging(t, $"<color={PastLabelHex}><b>YOU</b></color>   ", $"<color={PastPlayerHex}><i>{body}</i></color>"),
            LineKind.Divider => $"\n<color={DividerHex}>————————   <b>{Escape(text.ToUpperInvariant())}</b>   ————————</color>",
            LineKind.Error => Hanging(t, $"<color={ErrorHex}><b>SYSTEM</b></color>   ", $"<color={ErrorHex}><i>{body}</i></color>"),
            LineKind.Output => ConsoleText.Format(text), // console output: headings, commands and labels in colour, all escaped (FB-17)
            LineKind.Action => $"<color={ActionHex}><i>{body}</i></color>", // roleplay: centred (above), slanted, gold-brown, not bold
            _ => $"<color={NoticeHex}><i>{body}</i></color>",
        };
        t.text = line;
        if (kind is LineKind.Npc or LineKind.PastNpc or LineKind.StoryNpc)
        {
            bool past = kind != LineKind.Npc;
            _npcLines.Add((t.gameObject, t, past, kind == LineKind.StoryNpc ? $"<i>{body}</i>" : body));
            NoteNpcLine(body, fromHistory: past); // history comes newest first: only the newest counts
        }
        return t;
    }

    /// <summary>Takes a line back out of the window (your message, when it didn't go through).</summary>
    public void RemoveLine(GameObject? go)
    {
        if (go == null) return;
        try
        {
            if (_reveal != null && _reveal.gameObject == go) FinishReveal();
            _lines.Remove(go);
            _npcLines.RemoveAll(l => l.go == go);
            UnityEngine.Object.Destroy(go);
            _scrollPending = true;
        }
        catch { }
    }

    private string NpcLine(TextMeshProUGUI t, bool past, string body) =>
        Hanging(t, $"<size=88%><color={(past ? PastNpcLabelHex : NpcLabelHex)}><b>{Escape(_npcName.ToUpperInvariant())}</b></color></size>   ",
            past ? $"<color={PastHex}>{body}</color>" : body);

    /// <summary>
    /// "LABEL   their words": when the words wrap, the next line starts under the first letter of the words, not under
    /// the label. The label's width is measured in this line's own font and size.
    /// </summary>
    internal static string Hanging(TextMeshProUGUI t, string label, string body)
    {
        // Measured once per label in each font, size and spacing (two layout passes each): not again for every line,
        // bullet and rename (PERF-4). A failed measurement isn't kept. Cleared when the window is built or destroyed.
        float w = 0f;
        try
        {
            var key = (t.font != null ? t.font.Pointer : IntPtr.Zero, t.fontSize, t.characterSpacing, label);
            if (!LabelWidths.TryGetValue(key, out w))
            {
                w = t.GetPreferredValues(label + "X").x - t.GetPreferredValues("X").x;
                if (w > 1f) { if (LabelWidths.Count > 200) LabelWidths.Clear(); LabelWidths[key] = w; }
            }
        }
        catch { }
        // An unmeasurable or very wide label (a long name in a narrow window) just wraps the plain way.
        float room = t.rectTransform.rect.width > 1f ? t.rectTransform.rect.width : 9999f;
        return w > 1f && w < room * 0.45f
            ? $"{label}<indent={w.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}px>{body}</indent>"
            : label + body;
    }

    /// <summary>Who their lines are labelled as: "Stranger" until they tell you their name; every line updates.</summary>
    public void SetNpcName(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Stranger" : name.Trim();
        if (name == _npcName) return;
        FinishReveal();
        _npcName = name;
        _npcLines.RemoveAll(l => l.go == null);
        foreach (var (_, text, past, body) in _npcLines) text.text = NpcLine(text, past, body);
    }

    public void ScrollToBottom(bool instant) => _chatScroll?.ToBottom(instant);

    public string TakeInput()
    {
        if (_input == null) return "";
        string t = _input.text ?? "";
        _input.text = "";
        // CF-15: sent with Enter, the box let go of the keyboard; it takes it back next frame, so the next key you type lands
        // in it (the game's own InputFieldPreventExternalInput keeps keys from the game only while it's focused).
        if (_input.interactable) _focusInFrames = 1;
        return t.Trim();
    }

    public bool SubmitPressed()
    {
        var kb = Keyboard.current;
        if (_input == null || !_input.interactable) return false;
        // Enter, or the on-screen keyboard's Send (CF-07: a controller with no keyboard plugged in too).
        bool enter = (kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)) || _padSubmit;
        return enter && !string.IsNullOrWhiteSpace(_input.text);
    }

    public void Tick()
    {
        if (!Visible) return;
        try { TickInfoButtons(); } catch (Exception e) { Log.Debug("info buttons: " + e.Message); }
        try { TickColumn(); RefreshHint(); } catch (Exception e) { Log.Debug("column: " + e.Message); }
        if (_focusInFrames >= 0 && --_focusInFrames < 0 && _input != null && _input.interactable)
        {
            try
            {
                EventSystem.current?.SetSelectedGameObject(_input.gameObject);
                _input.ActivateInputField();
            }
            catch (Exception e) { Log.Debug("focus: " + e.Message); }
        }
        if (_reveal != null) TickReveal(Time.unscaledDeltaTime);
        TickRelationship(Time.unscaledDeltaTime);
        if (_thinking != null) TickThinking();
        var mouse = Mouse.current;
        Vector2 mpos = mouse != null ? mouse.position.ReadValue() : Vector2.zero;
        float wheel = mouse != null ? mouse.scroll.ReadValue().y : 0f;
        try { TickLayout(mouse, mpos); } catch (Exception e) { Log.Debug("chat window layout: " + e.Message); }
        if (_scrollPending) { _chatScroll?.ToBottom(false); _scrollPending = false; }
        // The right stick scrolls the info panel while it's open, the conversation otherwise (CF-07).
        if (Pad.ScrollY != 0f) (InfoOpen ? _infoScroller : _chatScroll)?.Push(Pad.ScrollY * 900f * Time.unscaledDeltaTime);
        _chatScroll?.Tick(mpos, wheel, Time.unscaledDeltaTime);
        if (InfoOpen) _infoScroller?.Tick(mpos, wheel, Time.unscaledDeltaTime);
        // Keep the box focused while the chat is open (clicking elsewhere would otherwise strand the player).
        // CF-15 (a player on 0.6.0: "I cannot click into the AI chat after sending one message, pressing an in-game hotkey,
        // or alt-tabbing"): a key press alone gave it back, and that first key reached the game too. Now a click anywhere
        // and coming back to the game (alt-tab) take it back as well, and a lapse is logged with what had the focus instead.
        bool appFocused = Application.isFocused;
        if (_input != null && _input.interactable && !_input.isFocused && _focusInFrames < 0)
        {
            var kb = Keyboard.current;
            if (kb != null && kb.anyKey.wasPressedThisFrame && !kb.escapeKey.wasPressedThisFrame) _focusInFrames = 0;
            else if (_keyboardMode) _focusInFrames = 0; // typing on the on-screen keyboard: the caret shows where it goes
            else if (mouse != null && mouse.leftButton.wasPressedThisFrame) _focusInFrames = 0;
            else if (appFocused && !_appFocused) _focusInFrames = 1; // back from another window
            // A lapse that didn't mend itself within a second and a half is said (at most a few a session): what had the focus.
            float t = Time.unscaledTime;
            if (_lapseSince < 0f) _lapseSince = t;
            else if (appFocused && t - _lapseSince > 1.5f && !_focusLapseSaid && _lapsesSaid < 5)
            {
                _focusLapseSaid = true;
                _lapsesSaid++;
                string selected = "";
                try { selected = EventSystem.current?.currentSelectedGameObject?.name ?? "nothing"; } catch { }
                Log.Info($"Chat (CF-15 probe): the chat box hasn't had the keyboard for {t - _lapseSince:0.#} s (selected: {selected}, cursor shown: {Cursor.visible}). Click the box or press a key.");
            }
        }
        // Focused, switched off, or a refocus on its way: no lapse being timed (review game IL-7: a stale start read as a long lapse).
        else { _lapseSince = -1f; if (_input != null && _input.isFocused) _focusLapseSaid = false; }
        _appFocused = appFocused;
    }

    /// <summary>CF-15: the game window had the focus last frame; since when the box hasn't had the keyboard; lapses said.</summary>
    private bool _appFocused = true, _focusLapseSaid;
    private float _lapseSince = -1f;
    private int _lapsesSaid;

    private void Clear()
    {
        foreach (var go in _lines) if (go != null) UnityEngine.Object.Destroy(go);
        _lines.Clear();
        _npcLines.Clear();
        _npcName = "Stranger";
        _reveal = null;
        _afterReveal.Clear();
    }

    public void Destroy()
    {
        if (_root != null) UnityEngine.Object.Destroy(_root);
        HideSight();
        _root = null; _panel = null; _input = null; _content = null; _chatScroll = null; _infoScroller = null; _prompt = null; _pill = null;
        _bond = null; _bondCells = null; _bondMarker = null; _bondCore = null; _bondShown = _bondTarget = -1; _bondFlash = 0;
        _infoPanel = null; _infoButton = null; _infoClose = null; _infoBody = null; _infoTitle = null;
        _kbButton = _cogButton = _logRt = _inputRt = _lastLineRt = _kbArea = null; _kbButtonImg = _cogButtonImg = null;
        _logFade = _lastLineFade = _kbFade = null; _lastLine = _kbLegend = null; _keys.Destroy();
        _keyboardMode = _suspended = false; _kbT = 0f;
        _give = null; _giveImg = null; _giveText = null; _bubbles.Clear();
        _lines.Clear();
        LabelWidths.Clear();
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Text from the AI or the player is never interpreted as TextMeshPro markup.</summary>
    internal static string Escape(string s) =>
        ConsoleText.Escape(s); // one escape for chat and console: a closing tag in any case stays text (review of 0.3.1)

    internal static TextMeshProUGUI Text(Transform parent, string name, float size, Color color, TextAlignmentOptions align, TMP_FontAsset? font)
    {
        var go = new GameObject(name);
        go.AddComponent<RectTransform>().SetParent(parent, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        if (font != null) t.font = font;
        t.fontSize = size;
        t.color = color;
        t.alignment = align;
        t.raycastTarget = false;
        t.richText = true;
        t.parseCtrlCharacters = false; // a typed "<" stays text, so it can't spell a tag past the escape (review of 0.3.1, L7)
        return t;
    }

    internal static RectTransform Rect(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        Place(rt, anchorMin, anchorMax, offsetMin, offsetMax);
        return rt;
    }

    internal static void Place(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
    }

    /// <summary>The game's Barlow family: ExtraBold for names, a regular weight for speech. Falls back to the HUD's font.</summary>
    private static (TMP_FontAsset? title, TMP_FontAsset? body) PickFonts()
    {
        TMP_FontAsset? hud = null;
        try
        {
            var counts = new Dictionary<IntPtr, (TMP_FontAsset font, int n)>();
            foreach (var t in UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>())
            {
                var f = t.font;
                if (f == null) continue;
                counts[f.Pointer] = counts.TryGetValue(f.Pointer, out var e) ? (f, e.n + 1) : (f, 1);
            }
            if (counts.Count > 0) hud = counts.Values.OrderByDescending(v => v.n).First().font;
        }
        catch (Exception e) { Log.Debug("font scan: " + e.Message); }

        TMP_FontAsset? title = null, body = null;
        try
        {
            var all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().Where(f => f != null).ToList();
            TMP_FontAsset? Find(params string[] prefixes)
            {
                foreach (var p in prefixes)
                    foreach (var f in all)
                        if ((f.name ?? "").StartsWith(p, StringComparison.OrdinalIgnoreCase)) return f;
                return null;
            }
            title = Find("Barlow-ExtraBold", "Barlow-Bold", "Barlow-Black", "Barlow-SemiBold");
            body = Find("Barlow-Medium", "Barlow-Regular", "Barlow-SemiBold", "Barlow SDF", "Barlow");
            Log.Debug("Fonts available: " + string.Join(", ", all.Select(f => f.name).Distinct().Take(40)));
        }
        catch (Exception e) { Log.Debug("font list: " + e.Message); }
        try { hud ??= TMP_Settings.defaultFontAsset; } catch { }
        title ??= hud;
        body ??= hud;
        Log.Debug($"Chat fonts: name={title?.name}, text={body?.name}");
        return (title, body);
    }
}
