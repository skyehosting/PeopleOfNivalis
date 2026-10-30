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

/// <summary>The GIVE button for food in your hands, and the speech bubble over someone's head.</summary>
internal sealed partial class ChatWindow
{
    // ------------------------------------------------------------------ "give" button (food in your hands)

    private RectTransform? _give;
    private Image? _giveImg;
    private TextMeshProUGUI? _giveText;
    private bool _giveClicked;
    private string _giveKey = "";

    private void BuildGive()
    {
        if (_panel == null) return;
        _give = Rect(_panel, "Give", new Vector2(1, 0), new Vector2(1, 0), Vector2.zero, Vector2.zero);
        _give.pivot = new Vector2(1, 0);
        _give.anchoredPosition = new Vector2(-24, 70);
        _give.sizeDelta = new Vector2(220, 36);
        _giveImg = _give.gameObject.AddComponent<Image>();
        _giveImg.sprite = Sprites.Pill;
        _giveImg.type = Image.Type.Sliced;
        _giveText = Text(_give, "Label", 18, PillText, TextAlignmentOptions.Center, _bodyFont);
        Place(_giveText.rectTransform, Vector2.zero, Vector2.one, new Vector2(14, 2), new Vector2(-14, -2));
        _giveText.enableWordWrapping = false;
        _give.gameObject.SetActive(false);
    }

    /// <summary>Shows "GIVE {dish} · {price}" on the chat (null hides it). Clicks are read with <see cref="TakeGiveClick"/>.</summary>
    public void SetGive(string? dish, string price)
    {
        if (_give == null || _giveText == null) return;
        bool show = !string.IsNullOrEmpty(dish);
        string key = show ? dish + "|" + price : "";
        if (show && key != _giveKey)
        {
            _giveText.text = $"<color={KeyHex}><b>GIVE</b></color>   {Escape(dish!)}   <color={KeyHex}>{Escape(price)}</color>";
            _giveText.ForceMeshUpdate();
            _give.sizeDelta = new Vector2(Mathf.Clamp(_giveText.preferredWidth + 34, 120, 520), 36);
        }
        _giveKey = key;
        if (_give.gameObject.activeSelf != show) _give.gameObject.SetActive(show);
        if (!show) _giveClicked = false;
    }

    public bool TakeGiveClick()
    {
        bool c = _giveClicked;
        _giveClicked = false;
        return c;
    }

    // ------------------------------------------------------------------ speech bubbles over people's heads (greetings, chatter)

    private sealed class Bubble
    {
        public RectTransform Rt = null!;
        public CanvasGroup Fade = null!;
        public TextMeshProUGUI Text = null!;
        /// <summary>The phone icon, shown when they're on a call.</summary>
        public Image Icon = null!;
        public Image Background = null!;
        public Transform? Anchor;
        /// <summary>Their body: the bubble stays over it even if the head marker is somewhere else.</summary>
        public Transform? Body;
        /// <summary>Shown only while the player is within this many metres of them (0 = always).</summary>
        public float MaxDistance;
        public float Until;
        /// <summary>How clear it is right now (distance, where you're looking, overlap), eased so it fades rather than pops.</summary>
        public float Focus, Over = 1f;
        /// <summary>Whose bubble it is: a new line from the same person replaces it.</summary>
        public string Key = "";
        public bool Showing => Rt != null && Rt.gameObject.activeSelf;
    }

    private readonly List<Bubble> _bubbles = new();
    /// <summary>No bubble is up: <see cref="TickBark"/> does nothing until <see cref="ShowBark"/> (the only thing that puts one up) runs (D4, PERF-12).</summary>
    private bool _barkIdle = true;
    private float _bubbleScale = 1f;
    private const float BarkMaxW = 380f;
    private const int MaxBubbles = 8;

    private void BuildBark(float scale) => _bubbleScale = scale;

    private Bubble NewBubble()
    {
        var go = new GameObject("Bark");
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(_root!.transform, false);
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0f);
        rt.localScale = Vector3.one * _bubbleScale * 0.9f;
        var bg = go.AddComponent<Image>();
        bg.sprite = Sprites.Bubble;
        bg.type = Image.Type.Sliced;
        bg.raycastTarget = false;
        var fade = go.AddComponent<CanvasGroup>();
        fade.blocksRaycasts = false;
        fade.interactable = false;
        var text = Text(rt, "Text", 21, BodyColor, TextAlignmentOptions.Center, _bodyFont);
        Place(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(18, 10), new Vector2(-18, -10));
        text.enableWordWrapping = true;
        text.lineSpacing = 2f;
        var iconGo = new GameObject("Icon");
        var irt = iconGo.AddComponent<RectTransform>();
        irt.SetParent(rt, false);
        irt.anchorMin = irt.anchorMax = new Vector2(0f, 0.5f);
        irt.pivot = new Vector2(0f, 0.5f);
        irt.sizeDelta = new Vector2(PhoneIcon, PhoneIcon);
        irt.anchoredPosition = new Vector2(16f, 0f);
        var icon = iconGo.AddComponent<Image>();
        icon.sprite = Sprites.Phone;
        icon.preserveAspect = true;
        icon.raycastTarget = false;
        iconGo.SetActive(false);
        go.SetActive(false);
        var b = new Bubble { Rt = rt, Fade = fade, Text = text, Icon = icon, Background = bg };
        _bubbles.Add(b);
        return b;
    }

    /// <summary>
    /// A short line over <paramref name="anchor"/> (their head) for a few seconds. Several people can have one at once;
    /// <paramref name="key"/> (the person) makes a new line replace their previous bubble. Hidden while a window is open.
    /// </summary>
    private const float PhoneIcon = 26f;

    /// <summary>How bubbles look (colours, text size): read from the settings each time a bubble is shown.</summary>
    public Func<BubbleLook>? BubbleStyle;
    private static readonly BubbleLook DefaultLook = new();

    /// <summary>
    /// <paramref name="maxDistance"/>: shown only while you're within that many metres of them (residents' talk); 0 =
    /// at any distance (someone greeting you).
    /// </summary>
    /// <summary>CF-24: every subtitle shown (who, what, its key, where, how far it carries) goes here too, for the console's /heard.</summary>
    public Action<string, string, string, Transform, float>? Heard;

    /// <param name="heard">CF-24 (review CF24-1): false keeps it out of /heard (a child's line: never kept anywhere).</param>
    public void ShowBark(string speaker, string line, Transform anchor, float seconds, string key = "", bool phone = false, float maxDistance = 0f,
        Transform? body = null, bool heard = true)
    {
        if (_root == null) return;
        if (heard) try { Heard?.Invoke(speaker ?? "", line ?? "", key ?? "", anchor, maxDistance); } catch { }
        _barkIdle = false;
        Bubble? b = key.Length > 0 ? _bubbles.FirstOrDefault(x => x.Showing && x.Key == key) : null;
        b ??= _bubbles.FirstOrDefault(x => !x.Showing);
        b ??= _bubbles.Count < MaxBubbles ? NewBubble() : _bubbles.OrderBy(x => x.Until).First();
        var look = BubbleStyle?.Invoke() ?? DefaultLook;
        var mine = b;
        b.Background.sprite = Sprites.BubbleFor(look.Fill, look.Edge, s => _bubbles.Any(x => x != mine && x.Background.sprite == s));
        b.Text.color = look.Text;
        b.Text.fontSize = 21f * Mathf.Clamp(look.Size, 0.5f, 2f);
        b.Icon.color = new Color32(look.Edge.r, look.Edge.g, look.Edge.b, 255);
        if (b.Key != key) { b.Focus = 0f; b.Over = 1f; } // someone new in this bubble: it fades in
        b.MaxDistance = Mathf.Max(0f, maxDistance);
        string head = speaker.Length > 0 ? $"<color={look.NameHex}><size=76%><b>{Escape(speaker.ToUpperInvariant())}</b></size></color>\n" : "";
        b.Text.text = head + Escape(line);
        // On the phone: a handset to the left of their words.
        float iconRoom = phone ? PhoneIcon + 10f : 0f;
        if (b.Icon.gameObject.activeSelf != phone) b.Icon.gameObject.SetActive(phone);
        Place(b.Text.rectTransform, Vector2.zero, Vector2.one, new Vector2(18 + iconRoom, 10), new Vector2(-18, -10));
        var pref = b.Text.GetPreferredValues(b.Text.text, BarkMaxW, 0);
        float w = Mathf.Min(pref.x, BarkMaxW);
        float h = b.Text.GetPreferredValues(b.Text.text, w + 1, 0).y;
        b.Rt.sizeDelta = new Vector2(w + 36 + iconRoom, Mathf.Max(h, phone ? PhoneIcon : 0f) + 20);
        b.Anchor = anchor;
        b.Body = body;
        b.Until = Time.unscaledTime + seconds;
        b.Key = key;
        b.Rt.gameObject.SetActive(true);
        TickBark(null);
    }

    /// <summary>Hides every bubble.</summary>
    public void HideBark()
    {
        foreach (var b in _bubbles) Hide(b);
    }

    /// <summary>Hides this person's bubble, if they have one up.</summary>
    public void HideBark(string key)
    {
        foreach (var b in _bubbles) if (b.Key == key) Hide(b);
    }

    private static void Hide(Bubble b)
    {
        b.Anchor = null;
        b.Body = null;
        if (b.Showing) b.Rt.gameObject.SetActive(false);
    }

    /// <summary>Bubbles of people you're not looking at fade back; the nearer of two overlapping stays clear (config BubbleFocus).</summary>
    public Func<bool>? FocusFadeOn;

    private readonly List<(Bubble b, float dist)> _placed = new();
    private readonly List<int> _slots = new();

    private static float Smooth01(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Keeps each bubble over its person's head as they (and you) move. Residents' bubbles fade in and out with distance
    /// (fully clear well inside TextDistance, gone at it) and with where you're looking (the person you face is clear,
    /// people off to the side fainter); when bubbles overlap, the nearest stays on top and clear, the ones behind it fade
    /// back. Every change is eased, so turning your head fades them rather than switching them. Fades out at the end.
    /// </summary>
    public void TickBark(Camera? cam)
    {
        if (_bubbles.Count == 0 || _barkIdle) return;
        cam ??= Game.GameContext.PlayerCamera();
        float now = Time.unscaledTime, dt = Mathf.Max(0f, Time.unscaledDeltaTime);
        bool focus = FocusFadeOn?.Invoke() ?? true;
        Vector3? me = null;
        try { var pc = Game.GameContext.PlayerCharacter(); if (pc != null) me = pc.transform.position; } catch { }
        _placed.Clear();
        bool anyUp = false;
        foreach (var b in _bubbles)
        {
            if (!b.Showing) continue;
            anyUp = true;
            float left = b.Until - now;
            if (Visible || cam == null || b.Anchor == null || left <= 0) { Hide(b); continue; }
            Vector3 head, world, sp;
            try
            {
                head = Game.GameContext.HeadPoint(b.Anchor, b.Body);
                world = head + Vector3.up * 0.38f;
                sp = cam.WorldToScreenPoint(world);
            }
            catch { Hide(b); continue; }
            float dist = me.HasValue ? Vector3.Distance(me.Value, head) : 0f;
            float want = sp.z <= 0.2f ? 0f : 1f;
            if (b.MaxDistance > 0f && want > 0f)
            {
                // Far: fades out toward TextDistance (and back in as you walk closer, mid-line).
                float band = Mathf.Clamp(b.MaxDistance * 0.3f, 0.75f, 2.5f);
                want *= 1f - Smooth01((dist - (b.MaxDistance - band)) / band);
                if (focus)
                {
                    // Off to the side: fainter (still there to notice), clear when you look at them.
                    float angle = Vector3.Angle(cam.transform.forward, world - cam.transform.position);
                    want *= Mathf.Lerp(1f, 0.3f, Smooth01((angle - 12f) / 28f));
                }
            }
            // Behind the camera: gone at once (its screen position would be mirrored); everything else eases.
            b.Focus = sp.z <= 0.2f ? 0f : Mathf.MoveTowards(b.Focus, want, dt * 4f);
            b.Rt.position = new Vector3(sp.x, sp.y, 0f);
            _placed.Add((b, dist));
        }
        if (!anyUp) _barkIdle = true; // none was up this frame: nothing to do until the next ShowBark
        if (_placed.Count == 0) return;

        // Overlaps: nearest first; a bubble covering a nearer, visible one fades back and sits beneath it.
        _placed.Sort((x, y) => x.dist.CompareTo(y.dist));
        for (int i = 0; i < _placed.Count; i++)
        {
            var b = _placed[i].b;
            bool covered = false;
            if (focus && b.MaxDistance > 0f && b.Focus > 0.05f)
            {
                var r = ScreenRect(b);
                for (int j = 0; j < i && !covered; j++)
                {
                    var n = _placed[j].b;
                    if (n.Focus > 0.3f && r.Overlaps(ScreenRect(n))) covered = true;
                }
            }
            b.Over = Mathf.MoveTowards(b.Over, covered ? 0.3f : 1f, dt * 4f);
            b.Fade.alpha = b.Focus * b.Over * Mathf.Clamp01((b.Until - now) / 0.6f);
        }
        // Draw order: the nearest last (on top), within the places the bubbles already have.
        _slots.Clear();
        foreach (var (b, _) in _placed) _slots.Add(b.Rt.GetSiblingIndex());
        _slots.Sort();
        for (int i = 0; i < _placed.Count; i++)
        {
            var rt = _placed[_placed.Count - 1 - i].b.Rt; // farthest gets the lowest place
            if (rt.GetSiblingIndex() != _slots[i]) rt.SetSiblingIndex(_slots[i]);
        }
    }

    /// <summary>Where a bubble sits on screen (its pivot is the bottom middle).</summary>
    private static Rect ScreenRect(Bubble b)
    {
        var size = b.Rt.sizeDelta;
        var scale = b.Rt.lossyScale;
        float w = size.x * scale.x, h = size.y * scale.y;
        var p = b.Rt.position;
        return new Rect(p.x - w / 2f, p.y, w, h);
    }
}

/// <summary>How the bubbles over people's heads look (from the Speech Bubbles settings).</summary>
public sealed class BubbleLook
{
    public Color32 Fill = new(36, 33, 30, 238);
    public Color32 Edge = new(196, 160, 98, 255);
    public Color32 Text = new(233, 226, 210, 255);
    public string NameHex = "#D9A55A";
    public float Size = 1f;

    /// <summary>"#RRGGBB" (or "RRGGBB") → a colour; <paramref name="fallback"/> if it can't be read.</summary>
    public static Color32 Parse(string? hex, Color32 fallback, int opacityPercent = 100)
    {
        string h = (hex ?? "").Trim().TrimStart('#');
        byte a = (byte)Mathf.RoundToInt(Mathf.Clamp(opacityPercent, 0, 100) * 2.55f);
        if (h.Length == 6 && int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out int v))
            return new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, a);
        return new Color32(fallback.r, fallback.g, fallback.b, a);
    }

    public static string Hex(string? hex, string fallback)
    {
        string h = (hex ?? "").Trim().TrimStart('#');
        return h.Length == 6 && int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out _) ? "#" + h.ToUpperInvariant() : fallback;
    }
}
