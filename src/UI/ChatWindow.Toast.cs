using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisAIChat.UI;

/// <summary>
/// FB-65: the mod's own pop-up, used only when the game's toast can't be (Game/GameToast.cs): a small card at the top right
/// with a gold title and a line, shown for a few seconds and faded out; up to three queued. Built the first time it's
/// needed, on the window's own canvas, and never takes input.
/// </summary>
internal sealed partial class ChatWindow
{
    private RectTransform? _toast;
    private TextMeshProUGUI? _toastTitle, _toastBody;
    private CanvasGroup? _toastGroup;
    private readonly Queue<(string title, string body)> _toasts = new();
    private float _toastUntil = -1f;
    private const float ToastSeconds = 7f, ToastFade = 0.6f;

    /// <summary>Shows a card (queued behind one showing). Text is escaped: nothing in it is read as markup.</summary>
    public void ShowToast(string title, string body)
    {
        if (_root == null) return;
        if (_toasts.Count >= 3) _toasts.Dequeue();
        _toasts.Enqueue((title ?? "", body ?? ""));
        if (_toastUntil < 0f) NextToast();
    }

    private void BuildToast()
    {
        var go = new GameObject("Toast");
        _toast = go.AddComponent<RectTransform>();
        _toast.SetParent(_root!.transform, false);
        _toast.anchorMin = _toast.anchorMax = new Vector2(1f, 1f);
        _toast.pivot = new Vector2(1f, 1f);
        _toast.anchoredPosition = new Vector2(-36, -120);
        _toast.sizeDelta = new Vector2(420, 96);
        var bg = go.AddComponent<Image>();
        bg.color = Fill;
        bg.raycastTarget = false;
        _toastGroup = go.AddComponent<CanvasGroup>();
        _toastGroup.blocksRaycasts = false;
        _toastGroup.interactable = false;
        var bar = Rect(_toast, "Bar", new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0), new Vector2(4, 0));
        var barImg = bar.gameObject.AddComponent<Image>();
        barImg.color = Gold;
        barImg.raycastTarget = false;
        _toastTitle = Text(_toast, "Title", 22, NameColor, TextAlignmentOptions.TopLeft, _titleFont);
        Place(_toastTitle.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(18, -40), new Vector2(-14, -10));
        _toastBody = Text(_toast, "Body", 19, BodyColor, TextAlignmentOptions.TopLeft, _bodyFont);
        Place(_toastBody.rectTransform, new Vector2(0, 0), new Vector2(1, 1), new Vector2(18, 10), new Vector2(-14, -42));
        _toastBody.enableWordWrapping = true;
        go.SetActive(false);
    }

    private void NextToast()
    {
        if (_toasts.Count == 0) { _toastUntil = -1f; if (_toast != null) _toast.gameObject.SetActive(false); return; }
        try
        {
            if (_toast == null) BuildToast();
            var (title, body) = _toasts.Dequeue();
            _toastTitle!.text = Escape(title);
            _toastBody!.text = Escape(body);
            _toastGroup!.alpha = 1f;
            _toast!.gameObject.SetActive(true);
            _toast.SetAsLastSibling();
            _toastUntil = Time.unscaledTime + ToastSeconds;
        }
        catch (Exception e) { Log.Debug("toast: " + e.Message); _toastUntil = -1f; }
    }

    /// <summary>Every frame (chat open or not): fades the card out at its end and shows the next one.</summary>
    public void TickToast()
    {
        if (_toastUntil < 0f || _toast == null) return;
        float left = _toastUntil - Time.unscaledTime;
        if (left <= 0f) { NextToast(); return; }
        if (_toastGroup != null) _toastGroup.alpha = Mathf.Clamp01(left / ToastFade);
    }
}
