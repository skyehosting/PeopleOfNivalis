using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisAIChat.UI;

/// <summary>
/// CF-20 (a player: "should add some kind of notification sounds for new message or phone call. or a tiny hud in the bottom that
/// shows phone notifications"; the author chose a phone pip): while the phone is away, a small
/// phone at the bottom right with your unread count; a new text shows who it's from for a few seconds (never what they said),
/// pulses a while and plays a soft chime (made here: no sound files). PhoneNotify: Pip (this), Card (the old card at the
/// top right, with the words) or Off; PhoneNotifyVolume.
/// </summary>
internal sealed partial class PhoneWindow
{
    /// <summary>"Pip", "Card" or "Off" (PhoneNotify), set by the runtime.</summary>
    public string NotifyStyle = "Pip";
    /// <summary>0..1 (PhoneNotifyVolume / 100).</summary>
    public float NotifyVolume = 0.5f;
    /// <summary>Your unread texts in all, set by the runtime (the badge).</summary>
    public int Unread;

    private RectTransform? _pip, _pipName, _pipBadge, _pipRing;
    private CanvasGroup? _pipFade;
    private TextMeshProUGUI? _pipNameText, _pipBadgeText;
    private Image? _pipRingImg;
    private float _pipNameLeft, _pipPulseLeft, _pipPulseT;
    private bool _chimeOwed; // the 2026-10-20 audit: a chime waits for its pip or card (a chat, the console or the menu open)
    private AudioSource? _chimeSource;
    private AudioClip? _chime;

    private const float PipW = 280, PipH = 56, PhoneW = 30, PhoneH = 46;

    private void BuildPip()
    {
        _pip = new GameObject("PhonePip").AddComponent<RectTransform>();
        _pip.SetParent(_root!.transform, false);
        _pip.anchorMin = _pip.anchorMax = new Vector2(1, 0);
        _pip.pivot = new Vector2(1, 0);
        _pip.anchoredPosition = new Vector2(-24, 24);
        _pip.sizeDelta = new Vector2(PipW, PipH);
        _pipFade = _pip.gameObject.AddComponent<CanvasGroup>();
        _pipFade.blocksRaycasts = false;
        _pipFade.interactable = false;
        float px = PipW - PhoneW - 6, py = PipH - PhoneH - 4;
        // The pulse ring behind the phone.
        _pipRing = Box(_pip, "Pulse", px, py, PhoneW, PhoneH);
        // FB-78 (the author: "only right and down"): it grows from the phone's middle, out on every side.
        _pipRing.pivot = new Vector2(0.5f, 0.5f);
        _pipRing.anchoredPosition = new Vector2(px + PhoneW / 2f, -(py + PhoneH / 2f));
        _pipRingImg = Img(_pipRing, Sprites.Round(8, 2, Clear, NameC), NameC);
        // The phone: gold edge, dark screen, a small home button.
        var phone = Box(_pip, "Phone", px, py, PhoneW, PhoneH);
        Img(phone, Sprites.Round(7, 2, BodyFill, Gold));
        Img(Box(phone, "Screen", 4, 4, PhoneW - 8, PhoneH - 14), Sprites.Round(2, 0, Deep, Deep));
        Img(Box(phone, "Button", PhoneW / 2 - 4, PhoneH - 7, 8, 3), Sprites.Round(1.5f, 0, GoldDim, GoldDim));
        // The unread badge on its top right corner.
        _pipBadge = Box(_pip, "Badge", px + PhoneW - 12, py - 8, 22, 20);
        Img(_pipBadge, Sprites.Round(10, 2, NameC, Dark));
        _pipBadgeText = Label(_pipBadge, "", 12, Dark, 0, 2, 22, 16, TextAlignmentOptions.Top, bold: true, wrap: false);
        // Who it's from, in a pill to its left (for a few seconds).
        _pipName = Box(_pip, "Name", 0, py + 10, 200, 26);
        Img(_pipName, Sprites.Round(13, 1, new Color32(22, 20, 18, 220), GoldDim));
        _pipNameText = Label(_pipName, "", 13, NameC, 10, 4, 180, 18, TextAlignmentOptions.TopRight, bold: true, wrap: false);
        _pip.gameObject.SetActive(false);
    }

    /// <summary>A new text while the phone is away: who it's from (only that), the pulse, the chime.</summary>
    public void Notify(string from)
    {
        if (NotifyStyle == "Off") return;
        if (NotifyStyle == "Card") { ChimeWhenShown(); return; } // the card itself is the runtime's Toast
        if (_pip == null) BuildPip();
        string name = (from ?? "").Trim();
        if (name.Length == 0) name = "A text";
        if (_pipNameText != null && _pipName != null)
        {
            FitLine(_pipNameText, name, 180);
            float w = Mathf.Min(200, _pipNameText.GetPreferredValues(_pipNameText.text).x + 22);
            _pipName.anchoredPosition = new Vector2(PipW - PhoneW - 14 - w, _pipName.anchoredPosition.y);
            _pipName.sizeDelta = new Vector2(w, 26);
            _pipNameText.rectTransform.sizeDelta = new Vector2(w - 20, 18);
        }
        _pipNameLeft = 4f;
        _pipPulseLeft = 20f; // then the pulse rests: only the badge stays
        ChimeWhenShown();
    }

    private void ChimeWhenShown() { if (HoldToasts) _chimeOwed = true; else PlayChime(); }

    private void TickPip(float dt)
    {
        if (_chimeOwed && (_visible || NotifyStyle == "Off" || (NotifyStyle == "Pip" && Unread == 0))) _chimeOwed = false; // seen already, or switched off
        else if (_chimeOwed && !HoldToasts) { _chimeOwed = false; PlayChime(); } // with its pip or card, as it shows
        bool show = NotifyStyle == "Pip" && Unread > 0 && !_visible && !HoldToasts;
        if (_pip == null) { if (!show) return; BuildPip(); }
        if (_pip!.gameObject.activeSelf != show) _pip.gameObject.SetActive(show);
        if (!show) return;
        if (_pipBadgeText != null) _pipBadgeText.text = Unread > 99 ? "99+" : Unread.ToString();
        if (_pipBadge != null) _pipBadge.sizeDelta = new Vector2(Unread > 9 ? 30 : 22, 20);
        _pipNameLeft = Mathf.Max(0, _pipNameLeft - dt);
        if (_pipName != null)
        {
            bool on = _pipNameLeft > 0;
            if (_pipName.gameObject.activeSelf != on) _pipName.gameObject.SetActive(on);
        }
        _pipPulseLeft = Mathf.Max(0, _pipPulseLeft - dt);
        if (_pipRing != null && _pipRingImg != null)
        {
            if (_pipPulseLeft > 0)
            {
                _pipPulseT = (_pipPulseT + dt / 1.6f) % 1f;
                float s = 1f + _pipPulseT * 0.6f;
                _pipRing.localScale = new Vector3(s, s, 1);
                var c = _pipRingImg.color;
                _pipRingImg.color = new Color(c.r, c.g, c.b, 0.55f * (1f - _pipPulseT));
            }
            else if (_pipRingImg.color.a > 0) _pipRingImg.color = Clear;
        }
    }

    /// <summary>A soft two-note chime (E6 then B5, fading), made once; at PhoneNotifyVolume.</summary>
    private void PlayChime()
    {
        if (NotifyVolume <= 0.001f || _root == null) return;
        try
        {
            if (_chime == null)
            {
                const int rate = 44100;
                int n = (int)(rate * 0.6f);
                var data = new float[n];
                for (int i = 0; i < n; i++)
                {
                    float t = i / (float)rate;
                    float a = t < 0.2f ? Mathf.Sin(2 * Mathf.PI * 1318.5f * t) * Mathf.Exp(-t * 14f) : 0f;
                    float b = t >= 0.11f ? Mathf.Sin(2 * Mathf.PI * 987.8f * (t - 0.11f)) * Mathf.Exp(-(t - 0.11f) * 7f) : 0f;
                    float attack = Mathf.Clamp01(t / 0.004f);
                    data[i] = 0.32f * attack * (a + b);
                }
                _chime = AudioClip.Create("PeopleOfNivalis.Chime", n, 1, rate, false);
                _chime.SetData(data, 0);
            }
            if (_chimeSource == null)
            {
                _chimeSource = _root.AddComponent<AudioSource>();
                _chimeSource.spatialBlend = 0f;
                _chimeSource.ignoreListenerPause = true;
                _chimeSource.playOnAwake = false;
            }
            _chimeSource.PlayOneShot(_chime, Mathf.Clamp01(NotifyVolume));
        }
        catch (Exception e) { Log.Debug("phone chime: " + e.Message); }
    }
}
