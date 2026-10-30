using System;
using NivalisAIChat.Npc;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisAIChat.UI;

/// <summary>
/// The relationship pill on the top border, left of the mood pill (the author's relationship bar). A frowning face on
/// the left, ten cells from red through white to green, a smiling face on the right, and a line where they stand that
/// glides when it changes. The maths (colours, where the line sits) is in <see cref="Relationships"/>, and tested.
/// </summary>
internal sealed partial class ChatWindow
{
    private RectTransform? _bond, _bondCells, _bondMarker;
    private Image? _bondCore;
    /// <summary>Where the line is drawn now and where it's heading (0–1 across the cells); -1 = not placed yet.</summary>
    private float _bondShown = -1, _bondTarget = -1;
    /// <summary>
    /// A change flashes the line green (up) or red (down) and lifts it a little, fading out: at the author's pace a
    /// reply moves it under a pixel, so the flash is what shows it changed (review of 919404e). 1 = just changed.
    /// </summary>
    private float _bondFlash;
    private Color _bondFlashColour;
    /// <summary>
    /// The change itself, floating up over the line (FB-15, the author's design): fades in quickly, slides up a little,
    /// fades out after a second or so. -1 = not showing. <see cref="_bondPercent"/>: the last value set (NaN = none yet).
    /// </summary>
    private TMPro.TextMeshProUGUI? _bondDelta;
    private float _bondDeltaAge = -1;
    private double _bondPercent = double.NaN;
    private const float BondDeltaSeconds = 1.6f, BondDeltaIn = 0.12f, BondDeltaOut = 0.5f, BondDeltaRise = 12f;
    private static readonly Color BondUp = new Color32(110, 220, 130, 255), BondDown = new Color32(235, 90, 80, 255);
    private static readonly Color BondCoreColour = new Color32(255, 250, 238, 255);
    private const float BondMarkerHeight = 26, BondFlashLift = 8, BondFlashSeconds = 1.2f;

    private const float BondWidth = 236, BondHeight = 38, BondGap = 10;

    private void BuildRelationshipPill()
    {
        if (_panel == null) return;
        var go = new GameObject("RelationshipPill");
        _bond = go.AddComponent<RectTransform>();
        _bond.SetParent(_panel, false);
        _bond.anchorMin = _bond.anchorMax = new Vector2(1, 1);
        _bond.pivot = new Vector2(1, 0.5f);
        _bond.sizeDelta = new Vector2(BondWidth, BondHeight);
        var bg = go.AddComponent<Image>();
        bg.sprite = Sprites.Pill;
        bg.type = Image.Type.Sliced;
        bg.raycastTarget = false;

        Icon(_bond, "Frown", Sprites.Frown, new Vector2(0, 0.5f), new Vector2(12, 0));
        _bondSmile = Icon(_bond, "Smile", Sprites.Smile, new Vector2(1, 0.5f), new Vector2(-12, 0));

        // The ten cells, between the faces: one colour each, red to white to green.
        _bondCells = Rect(_bond, "Cells", new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(44, -7), new Vector2(-44, 7));
        float gap = 2;
        for (int i = 0; i < Relationships.Cells; i++)
        {
            var cell = Rect(_bondCells, "Cell" + i, new Vector2(i / (float)Relationships.Cells, 0), new Vector2((i + 1) / (float)Relationships.Cells, 1),
                new Vector2(i == 0 ? 0 : gap / 2, 0), new Vector2(i == Relationships.Cells - 1 ? 0 : -gap / 2, 0));
            var img = cell.gameObject.AddComponent<Image>();
            img.sprite = Sprites.Cell;
            img.type = Image.Type.Sliced;
            var (r, g, b) = Relationships.CellColour(i);
            img.color = new Color32(r, g, b, 255);
            img.raycastTarget = false;
        }

        // The line where they stand: dark edges around a bright core, so it shows on red, white and green alike.
        _bondMarker = Rect(_bondCells, "Marker", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        _bondMarker.pivot = new Vector2(0.5f, 0.5f);
        _bondMarker.sizeDelta = new Vector2(6, BondMarkerHeight);
        var edge = _bondMarker.gameObject.AddComponent<Image>();
        edge.color = new Color32(20, 18, 16, 235);
        edge.raycastTarget = false;
        var core = Rect(_bondMarker, "Core", Vector2.zero, Vector2.one, new Vector2(2, 2), new Vector2(-2, -2));
        _bondCore = core.gameObject.AddComponent<Image>();
        _bondCore.color = BondCoreColour;
        _bondCore.raycastTarget = false;

        // The change, over the line (placed and faded in TickRelationship).
        _bondDelta = Text(_bondCells, "Change", 20, BondUp, TMPro.TextAlignmentOptions.Bottom, _titleFont);
        var drt = _bondDelta.rectTransform;
        drt.anchorMin = drt.anchorMax = new Vector2(0.5f, 1);
        drt.pivot = new Vector2(0.5f, 0);
        drt.sizeDelta = new Vector2(90, 26);
        _bondDelta.gameObject.SetActive(false);

        _bond.gameObject.SetActive(false);
    }

    private Image? _bondSmile;

    /// <summary>FB-63: the bar's smile becomes a pink heart while you're together (and back when you're not).</summary>
    public void SetTogether(bool together)
    {
        try { if (_bondSmile != null) _bondSmile.sprite = together ? Sprites.Heart : Sprites.Smile; }
        catch (System.Exception e) { Log.Debug("heart: " + e.Message); }
    }

    private static Image Icon(RectTransform parent, string name, Sprite sprite, Vector2 anchor, Vector2 position)
    {
        var rt = Rect(parent, name, anchor, anchor, Vector2.zero, Vector2.zero);
        rt.pivot = new Vector2(anchor.x, 0.5f);
        rt.anchoredPosition = position;
        rt.sizeDelta = new Vector2(24, 24);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = sprite;
        img.preserveAspect = true;
        img.raycastTarget = false;
        return img;
    }

    /// <summary>
    /// Where they stand on the relationship bar (0–100), or null to hide the pill: a chat without a person (the console, a
    /// brush-off) or ShowRelationshipBar off. The first value shows at once; a change glides there. With
    /// <paramref name="announce"/> (a reply or food moved it), the change also floats up over the line ("+0.35"); without
    /// it (the window opening, the conversation's own value arriving), it's placed quietly.
    /// </summary>
    public void SetRelationship(double? percent, bool announce = false)
    {
        if (_bond == null) return;
        bool show = percent.HasValue;
        if (show)
        {
            float next = (float)Relationships.MarkerAt(percent!.Value);
            if (_bondShown < 0 || !_bond.gameObject.activeSelf) { _bondTarget = _bondShown = next; _bondFlash = 0; HideChange(); PlaceMarker(); }
            else
            {
                if (next != _bondTarget && announce)
                {
                    _bondFlash = 1;
                    _bondFlashColour = next > _bondTarget ? BondUp : BondDown;
                }
                _bondTarget = next;
                if (announce && !double.IsNaN(_bondPercent) && Relationships.ChangeLabel(percent.Value - _bondPercent) is { } label) ShowChange(label, percent.Value > _bondPercent);
            }
            _bondPercent = percent.Value;
        }
        else { _bondShown = _bondTarget = -1; _bondFlash = 0; _bondPercent = double.NaN; HideChange(); }
        _bondWanted = show;
        if (_bond.gameObject.activeSelf != (show && !_collapsed)) _bond.gameObject.SetActive(show && !_collapsed);
        PlaceRelationshipPill();
        UpdateCollapse();
    }

    private void ShowChange(string label, bool up)
    {
        if (_bondDelta == null) return;
        _bondDelta.text = label;
        _bondDelta.color = up ? BondUp : BondDown;
        _bondDeltaAge = 0;
        _bondDelta.alpha = 0;
        _bondDelta.gameObject.SetActive(true);
        PlaceChange();
    }

    private void HideChange()
    {
        _bondDeltaAge = -1;
        if (_bondDelta != null && _bondDelta.gameObject.activeSelf) _bondDelta.gameObject.SetActive(false);
    }

    /// <summary>Over where the line is heading, lifting as it fades.</summary>
    private void PlaceChange()
    {
        if (_bondDelta == null || _bondDeltaAge < 0) return;
        float t = Mathf.Clamp01(_bondDeltaAge / BondDeltaSeconds);
        float rise = BondDeltaRise * (1 - (1 - t) * (1 - t)); // eases out
        var rt = _bondDelta.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(Mathf.Clamp(_bondTarget, 0.08f, 0.92f), 1);
        rt.anchoredPosition = new Vector2(0, BondMarkerHeight / 2f - 2 + rise);
    }

    /// <summary>Left of the mood pill when that shows, in its place when it doesn't.</summary>
    private void PlaceRelationshipPill()
    {
        if (_bond == null) return;
        float x = -46;
        if (_pill != null && _pill.gameObject.activeSelf) x -= _pill.sizeDelta.x + BondGap;
        _bond.anchoredPosition = new Vector2(x, 0);
    }

    private void TickRelationship(float dt)
    {
        if (_bond == null || _bondMarker == null || _bondTarget < 0 || !_bond.gameObject.activeSelf) return;
        if (_bondDeltaAge >= 0 && _bondDelta != null)
        {
            _bondDeltaAge += dt;
            if (_bondDeltaAge >= BondDeltaSeconds) HideChange();
            else
            {
                float a = _bondDeltaAge < BondDeltaIn ? _bondDeltaAge / BondDeltaIn
                        : _bondDeltaAge > BondDeltaSeconds - BondDeltaOut ? (BondDeltaSeconds - _bondDeltaAge) / BondDeltaOut : 1f;
                _bondDelta.alpha = Mathf.Clamp01(a);
                PlaceChange();
            }
        }
        if (_bondFlash > 0)
        {
            _bondFlash = Mathf.Max(0, _bondFlash - dt / BondFlashSeconds);
            if (_bondCore != null) _bondCore.color = Color.Lerp(BondCoreColour, _bondFlashColour, _bondFlash);
            _bondMarker.sizeDelta = new Vector2(6, BondMarkerHeight + BondFlashLift * _bondFlash);
        }
        if (Mathf.Approximately(_bondShown, _bondTarget)) return;
        _bondShown = Mathf.MoveTowards(_bondShown, _bondTarget, Mathf.Max(0.0005f, Mathf.Abs(_bondTarget - _bondShown)) * dt / 0.4f + dt * 0.02f);
        PlaceMarker();
    }

    private void PlaceMarker()
    {
        if (_bondMarker == null || _bondShown < 0) return;
        _bondMarker.anchorMin = _bondMarker.anchorMax = new Vector2(Mathf.Clamp01(_bondShown), 0.5f);
        _bondMarker.anchoredPosition = Vector2.zero;
    }
}
