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

/// <summary>The "what you know about them" panel next to the chat, and the mouse handling for the chat's buttons.</summary>
internal sealed partial class ChatWindow
{
    // ------------------------------------------------------------------ info panel ("what you know about them")

    private RectTransform? _infoButton, _infoPanel, _infoClose, _infoContent;
    private Image? _infoButtonImg, _infoCloseImg;
    private TextMeshProUGUI? _infoTitle, _infoBody;

    private float _scale = 1f;
    private const float InfoW = 430f, InfoGap = 14f, PanelHalf = 450f;
    private static readonly Color ButtonIdle = Color.white, ButtonHover = new(1f, 0.86f, 0.62f, 1f);

    public bool InfoOpen => _infoPanel != null && _infoPanel.gameObject.activeSelf;

    private void BuildInfo(float scale)
    {
        if (_panel == null || _root == null) return;
        _scale = scale;

        // the "i" button on the chat's right edge
        _infoButton = Rect(_panel, "InfoButton", new Vector2(1, 1), new Vector2(1, 1), Vector2.zero, Vector2.zero);
        _infoButton.pivot = new Vector2(0, 1);
        _infoButton.sizeDelta = new Vector2(46, 46);
        _infoButton.anchoredPosition = new Vector2(12, -20);
        _infoButtonImg = _infoButton.gameObject.AddComponent<Image>();
        _infoButtonImg.sprite = Sprites.Pill;
        _infoButtonImg.type = Image.Type.Sliced;
        var i = Text(_infoButton, "i", 26, NameColor, TextAlignmentOptions.Center, _titleFont);
        Place(i.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        i.text = "i";

        // the panel itself, same bubble style without the tail
        var go = new GameObject("InfoPanel");
        _infoPanel = go.AddComponent<RectTransform>();
        _infoPanel.SetParent(_root.transform, false);
        _infoPanel.anchorMin = _infoPanel.anchorMax = new Vector2(0.5f, 0f);
        _infoPanel.pivot = new Vector2(0f, 0f);
        _infoPanel.sizeDelta = new Vector2(InfoW, 470);
        _infoPanel.localScale = Vector3.one * scale;
        var bg = go.AddComponent<Image>();
        bg.sprite = Sprites.Bubble;
        bg.type = Image.Type.Sliced;

        var heading = Text(_infoPanel, "Heading", 15, new Color32(138, 133, 125, 255), TextAlignmentOptions.TopLeft, _bodyFont);
        Place(heading.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(26, -40), new Vector2(-70, -16));
        heading.fontStyle = FontStyles.UpperCase;
        heading.characterSpacing = 2f;
        heading.text = "What you know";
        _infoTitle = Text(_infoPanel, "Name", 26, NameColor, TextAlignmentOptions.TopLeft, _titleFont);
        Place(_infoTitle.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(26, -74), new Vector2(-70, -38));
        _infoTitle.fontStyle = FontStyles.UpperCase;
        _infoTitle.enableWordWrapping = false;

        _infoClose = Rect(_infoPanel, "Close", new Vector2(1, 1), new Vector2(1, 1), Vector2.zero, Vector2.zero);
        _infoClose.pivot = new Vector2(1, 1);
        _infoClose.sizeDelta = new Vector2(40, 40);
        _infoClose.anchoredPosition = new Vector2(-16, -16);
        _infoCloseImg = _infoClose.gameObject.AddComponent<Image>();
        _infoCloseImg.sprite = Sprites.Pill;
        _infoCloseImg.type = Image.Type.Sliced;
        var x = Text(_infoClose, "X", 20, NameColor, TextAlignmentOptions.Center, _titleFont);
        Place(x.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        x.text = "X";

        var scrollRt = Rect(_infoPanel, "Scroll", Vector2.zero, Vector2.one, new Vector2(26, 22), new Vector2(-18, -86));
        var viewport = Rect(scrollRt, "Viewport", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        viewport.gameObject.AddComponent<RectMask2D>();
        _infoContent = Rect(viewport, "Content", new Vector2(0, 1), new Vector2(1, 1), Vector2.zero, Vector2.zero);
        _infoContent.pivot = new Vector2(0.5f, 1f);
        var layout = _infoContent.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.padding = new RectOffset(0, 10, 0, 6);
        _infoContent.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        _infoScroller = new SmoothScroll(viewport, _infoContent);
        _infoBody = Text(_infoContent, "Body", 19, BodyColor, TextAlignmentOptions.TopLeft, _bodyFont);
        _infoBody.enableWordWrapping = true;
        _infoBody.lineSpacing = 6f;

        go.SetActive(false);
    }

    public void ShowInfo(bool show)
    {
        if (_infoPanel == null || _panel == null) return;
        // Open: the chat and the panel sit side by side, centred as a pair. Closed: the chat is centred alone.
        // Moved or resized (ChatWindow.Layout): beside it wherever it is, on its left when the right has no room.
        bool moved = _offset != Vector2.zero || _size != new Vector2(DefaultW, DefaultH);
        float half = _size.x / 2f;
        float shift = show && !moved ? (InfoW + InfoGap) * _scale / 2f : 0f;
        _panel.anchoredPosition = new Vector2(_offset.x - shift, BaseY + _offset.y);
        bool left = show && moved && InfoOnLeft();
        _infoPanel.sizeDelta = new Vector2(InfoW, _size.y);
        _infoPanel.anchoredPosition = left
            ? new Vector2(_panel.anchoredPosition.x - (half + InfoGap + InfoW) * _scale, _panel.anchoredPosition.y)
            : new Vector2(_panel.anchoredPosition.x + (half + InfoGap) * _scale, _panel.anchoredPosition.y);
        if (_infoPanel.gameObject.activeSelf != show) _infoPanel.gameObject.SetActive(show);
        // the panel takes the button's place; the button comes back when the panel closes
        if (_infoButton != null && _infoButton.gameObject.activeSelf == show) _infoButton.gameObject.SetActive(!show);
        PlaceColumn(); // the keyboard and settings buttons go right of the panel while it's open (CF-07/CF-08)
        if (show) _infoScroller?.ToTop();
    }

    private const string LabelHex = "#8A857D", UnknownHex = "#5E5953", SectionHex = "#D9A55A";

    public void SetInfo(Npc.InfoSheet sheet)
    {
        if (_infoBody == null || _infoTitle == null) return;
        _infoTitle.text = Escape(sheet.Title);
        var sb = new System.Text.StringBuilder();
        foreach (var r in sheet.Rows)
        {
            string value = r.Known ? Escape(r.Value) : $"<color={UnknownHex}>?</color>";
            // The answer sits in its own column: a long one wraps under its first word, not back under the label.
            sb.Append($"<color={LabelHex}>{Escape(r.Label)}</color><indent=34%>{value}</indent>\n");
        }
        foreach (var (heading, items) in sheet.Sections)
        {
            sb.Append($"\n<color={SectionHex}><size=16><b>{Escape(heading.ToUpperInvariant())}</b></size></color>\n");
            foreach (var item in items) sb.Append(Hanging(_infoBody, "• ", Escape(item))).Append('\n');
        }
        _infoBody.text = sb.ToString().TrimEnd();
    }

    private static bool Over(RectTransform? rt, Vector2 screenPos) =>
        rt != null && rt.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(rt, screenPos, null);

    /// <summary>Mouse handling for the two buttons, polled each frame (no event wiring needed).</summary>
    private void TickInfoButtons()
    {
        var mouse = Mouse.current;
        if (mouse == null) return;
        Vector2 pos = mouse.position.ReadValue();
        bool overInfo = Over(_infoButton, pos), overClose = Over(_infoClose, pos), overGive = Over(_give, pos);
        if (_infoButtonImg != null) _infoButtonImg.color = overInfo || InfoOpen ? ButtonHover : ButtonIdle;
        if (_infoCloseImg != null) _infoCloseImg.color = overClose ? ButtonHover : ButtonIdle;
        if (_giveImg != null) _giveImg.color = overGive ? ButtonHover : ButtonIdle;
        if (!mouse.leftButton.wasPressedThisFrame) return;
        if (overInfo) ShowInfo(!InfoOpen);
        else if (overClose) ShowInfo(false);
        else if (overGive) _giveClicked = true;
    }
}
