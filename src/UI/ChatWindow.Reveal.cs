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

/// <summary>Their words appearing over time: letter-by-letter fade, timed to the voice when there is one.</summary>
internal sealed partial class ChatWindow
{
    private TextMeshProUGUI? _reveal;
    private float _revealPos;
    private float _revealRate = 45f;
    private int _revealTotal;
    private int _revealStart, _fadeFrom;
    private bool _fading;
    /// <summary>Every letter past the reveal point has been set to clear (and still is, as far as the sentinel shows).</summary>
    private bool _tailClear;

    /// <summary>Letters fade in smoothly (true) or pop in one by one (false).</summary>
    public bool SmoothFade = true;
    /// <summary>Speed for lines without a voice; spoken lines follow the voice instead.</summary>
    public TextSpeed Speed = TextSpeed.Medium;
    /// <summary>How many letters are mid-fade at once: the soft edge of the reveal.</summary>
    private const float FadeWidth = 7f;

    private static float CharsPerSecond(TextSpeed s) => s switch { TextSpeed.Slow => 24f, TextSpeed.Fast => 85f, _ => 45f };

    public bool Revealing => _reveal != null;

    /// <summary>
    /// Their words appear over time. A spoken line is timed to its voice: it starts as they start talking and the last
    /// letter settles as they finish. Otherwise it follows the TextSpeed setting. The name label is there at once.
    /// </summary>
    private void StartReveal(TextMeshProUGUI t, float spokenSeconds)
    {
        bool spoken = spokenSeconds > 0.3f;
        if (!spoken && Speed == TextSpeed.Instant) return;
        try
        {
            t.ForceMeshUpdate();
            _revealTotal = t.textInfo.characterCount;
            _revealStart = Mathf.Min(_npcName.Length + 3, _revealTotal);
            _fading = SmoothFade;
            float span = _revealTotal - _revealStart + (_fading ? FadeWidth : 0f);
            _revealRate = spoken ? Mathf.Clamp(span / spokenSeconds, 4f, 400f) : CharsPerSecond(Speed);
            _revealPos = _revealStart;
            _fadeFrom = _revealStart;
            _tailClear = false;
            _reveal = t;
            if (_fading) { t.maxVisibleCharacters = 99999; ApplyFade(); }
            else t.maxVisibleCharacters = _revealStart;
        }
        catch (Exception e)
        {
            Log.Debug("reveal: " + e.Message);
            _reveal = null;
            t.maxVisibleCharacters = 99999;
        }
    }

    private void TickReveal(float dt)
    {
        if (_reveal == null) return;
        _revealPos += dt * _revealRate;
        try
        {
            if (_fading)
            {
                ApplyFade();
                if (_revealPos >= _revealTotal + FadeWidth) FinishReveal();
            }
            else
            {
                int shown = Mathf.Min((int)_revealPos, _revealTotal);
                _reveal.maxVisibleCharacters = shown;
                if (shown >= _revealTotal) FinishReveal();
            }
        }
        catch (Exception e)
        {
            Log.Debug("reveal tick: " + e.Message);
            FinishReveal();
        }
    }

    /// <summary>
    /// Each letter's alpha rises from 0 to full over FadeWidth letters behind the reveal point. The text's arrays are read
    /// once a frame (each read is a call into the game), and only the letters fading in are touched: the ones past the
    /// reveal point were set clear once (PERF-5). TMP rebuilds the mesh at full opacity after a layout pass or a font
    /// atlas change, so one letter past the point is checked each frame; if it shows, the rest are set clear again.
    /// </summary>
    private void ApplyFade()
    {
        var t = _reveal;
        if (t == null) return;
        var info = t.textInfo;
        if (info == null) return;
        var chars = info.characterInfo;
        var meshes = info.meshInfo;
        if (chars == null || meshes == null || meshes.Length == 0) return;
        int n = Mathf.Min(info.characterCount, chars.Length);
        int edge = Mathf.Clamp(Mathf.CeilToInt(_revealPos), 0, n); // from here on every letter is still clear
        int mat = 0;
        var colors = meshes[0].colors32; // one material in nearly every line; looked up again only when it changes

        if (_tailClear)
        {
            // The sentinel: the first visible letter past the reveal point. Shown means the mesh was rebuilt.
            for (int i = edge; i < n; i++)
            {
                var ch = chars[i];
                if (!ch.isVisible) continue;
                if (ch.materialReferenceIndex != mat) { mat = ch.materialReferenceIndex; colors = meshes[mat].colors32; }
                if (colors != null && ch.vertexIndex < colors.Length && colors[ch.vertexIndex].a != 0) _tailClear = false;
                break;
            }
        }
        int end = _tailClear ? edge : n;
        bool changed = false;
        for (int i = _fadeFrom; i < end; i++)
        {
            var ch = chars[i];
            if (!ch.isVisible) continue;
            byte a = (byte)(255f * Mathf.Clamp01((_revealPos - i) / FadeWidth));
            if (ch.materialReferenceIndex != mat) { mat = ch.materialReferenceIndex; colors = meshes[mat].colors32; }
            if (colors == null) continue;
            int v = ch.vertexIndex;
            for (int k = 0; k < 4 && v + k < colors.Length; k++)
            {
                var c = colors[v + k];
                if (c.a == a) continue;
                c.a = a;
                colors[v + k] = c;
                changed = true;
            }
        }
        if (end == n) _tailClear = true;
        // letters that have fully settled never need touching again
        while (_fadeFrom < n && _revealPos - _fadeFrom >= FadeWidth) _fadeFrom++;
        if (changed) t.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);
    }

    private void FinishReveal()
    {
        if (_reveal == null) return;
        var t = _reveal;
        _reveal = null;
        try
        {
            t.maxVisibleCharacters = 99999;
            if (_fading) t.ForceMeshUpdate(); // rebuilds the mesh with every letter at full opacity
        }
        catch { }
        _fading = false;
        // Roleplay: what they did as they said it shows once their words are out (and before any next line).
        if (_afterReveal.Count > 0)
        {
            var waiting = _afterReveal.ToList();
            _afterReveal.Clear();
            foreach (var (kind, text) in waiting) AddLine(kind, "", text);
        }
    }

    private readonly List<(LineKind kind, string text)> _afterReveal = new();

    /// <summary>A line that follows the one being revealed (a roleplay action): shown when it's done, or now if nothing is revealing.</summary>
    public void AddAfterReveal(LineKind kind, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_reveal == null) { AddLine(kind, "", text); return; }
        _afterReveal.Add((kind, text));
    }
}
