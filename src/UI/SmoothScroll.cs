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

/// <summary>
/// Smooth mouse-wheel scrolling for a viewport/content pair (ScrollRect's wheel scrolls in hard steps).
/// Sticks to the bottom for new lines until you scroll up; scrolling back down to the end re-sticks.
/// </summary>
internal sealed class SmoothScroll
{
    private readonly RectTransform _viewport, _content;
    private float _target, _pos;
    private bool _stickBottom = true;
    private int _snapFrames;

    public SmoothScroll(RectTransform viewport, RectTransform content)
    {
        _viewport = viewport;
        _content = content;
    }

    private float Max => Mathf.Max(0f, _content.rect.height - _viewport.rect.height);

    public void ToBottom(bool instant)
    {
        _stickBottom = true;
        _target = Max;
        if (instant) { _pos = _target; _snapFrames = 3; Apply(); } // layout settles over the next frames
    }

    /// <summary>Following new lines at the bottom (not scrolled up to read).</summary>
    public bool AtBottom => _stickBottom;

    /// <summary>
    /// Lines <paramref name="height"/> tall were put above what's shown: the view moves down with them, so the lines being
    /// read stay where they are (PERF-4's history added a batch at a time).
    /// </summary>
    public void Shift(float height)
    {
        if (height <= 0f) return;
        _pos += height;
        _target += height;
        Apply();
    }

    public void ToTop()
    {
        _stickBottom = false;
        _target = _pos = 0;
        Apply();
    }

    /// <summary>Scrolls by this many pixels toward the top (negative: toward the bottom), wherever the mouse is: the right stick (CF-07).</summary>
    public void Push(float pixels)
    {
        if (pixels == 0f) return;
        _target = Mathf.Clamp(_target - pixels, 0f, Max);
        _stickBottom = _target >= Max;
    }

    public void Tick(Vector2 mouse, float wheel, float dt)
    {
        float max = Max;
        if (wheel != 0 && RectTransformUtility.RectangleContainsScreenPoint(_viewport, mouse, null))
        {
            _target -= wheel * 0.45f; // ~54 px per notch, eased below
            _stickBottom = _target >= max;
        }
        if (_stickBottom) _target = max;
        _target = Mathf.Clamp(_target, 0f, max);
        if (_snapFrames > 0) { _snapFrames--; _pos = _target; }
        _pos = Mathf.Lerp(_pos, _target, 1f - Mathf.Exp(-dt * 14f));
        if (Mathf.Abs(_pos - _target) < 0.5f) _pos = _target;
        Apply();
    }

    private void Apply() => _content.anchoredPosition = new Vector2(_content.anchoredPosition.x, _pos);
}
