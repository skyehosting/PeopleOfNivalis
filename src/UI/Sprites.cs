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

/// <summary>Procedurally drawn, nine-sliced sprites for the dialogue-box look (no game assets are modified).</summary>
internal static class Sprites
{
    public const int TailW = 34, TailTri = 24, TailRadius = 16;
    public const int TailH = TailTri + TailRadius;
    public const int TailDrop = TailTri;

    private static Sprite? _bubble, _pill, _field, _tail, _phone, _cell, _frown, _smile, _knob, _keyboard, _cog, _heart;

    public static Sprite Bubble => _bubble ??= Rounded(64, 16, 3, new Color32(36, 33, 30, 238), new Color32(196, 160, 98, 255));
    public static Sprite Pill => _pill ??= Rounded(40, 18, 2.5f, new Color32(26, 44, 42, 255), new Color32(196, 160, 98, 255)); // solid: the bubble border must not show through
    public static Sprite Field => _field ??= Rounded(40, 9, 1.5f, new Color32(22, 20, 18, 246), new Color32(118, 97, 62, 255));
    public static Sprite Tail => _tail ??= MakeTail(new Color32(36, 33, 30, 238), new Color32(196, 160, 98, 255), 3);
    /// <summary>A phone handset (someone on a call), white: tinted to the bubble's border colour where it's shown.</summary>
    public static Sprite Phone => _phone ??= MakePhone(64, new Color32(255, 255, 255, 255));
    /// <summary>One cell of the relationship bar, white: tinted to its colour where it's shown.</summary>
    public static Sprite Cell => _cell ??= Rounded(16, 3, 0, new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255));
    /// <summary>FB-53: a key's bold border, white with nothing inside (the on-screen keyboard's chosen key), tinted where it's shown.</summary>
    public static Sprite KeyEdge => _keyEdge ??= Rounded(40, 9, 3f, new Color32(255, 255, 255, 0), new Color32(255, 255, 255, 255));
    private static Sprite? _keyEdge;
    /// <summary>The relationship bar's ends: a red frowning face and a green smiling one.</summary>
    public static Sprite Frown => _frown ??= MakeFace(64, new Color32(204, 70, 64, 255), smile: false);
    public static Sprite Smile => _smile ??= MakeFace(64, new Color32(78, 182, 102, 255), smile: true);
    /// <summary>The chat window's mood circle: a pale face on its coloured disc.</summary>
    public static Sprite PaleSmile => _paleSmile ??= MakeFace(64, new Color32(238, 240, 228, 255), smile: true);
    public static Sprite PaleFrown => _paleFrown ??= MakeFace(64, new Color32(246, 222, 214, 255), smile: false);
    private static Sprite? _paleSmile, _paleFrown;
    /// <summary>FB-63 (the author: "their smiley face icon in the relationship bar would turn into a pink heart"): together.</summary>
    public static Sprite Heart => _heart ??= MakeHeart(64, new Color32(236, 104, 160, 255));
    /// <summary>A white round end (switch tracks and knobs, slider knobs), nine-sliced: tinted where it's shown (CF-08).</summary>
    public static Sprite Knob => _knob ??= Rounded(40, 19.5f, 0, new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255));
    /// <summary>The keyboard and settings icons for the buttons beside the chat (CF-07/CF-08), white: tinted gold.</summary>
    public static Sprite KeyboardIcon => _keyboard ??= MakeIcon(64, KeyboardShape);
    public static Sprite CogIcon => _cog ??= MakeIcon(64, CogShape);

    private static readonly Dictionary<string, Sprite> _bubbles = new();

    // ------------------------------------------------------------------ the phone (drawn at about 2.5x, nine-sliced)

    private static readonly Dictionary<string, Sprite> _phoneShapes = new();

    /// <summary>A rounded rectangle for the phone: radius and border in design units, drawn at 2.5x so it stays crisp when scaled.</summary>
    public static Sprite Round(float radius, float border, Color32 fill, Color32 edge)
    {
        string key = $"{radius}|{border}|{fill.r},{fill.g},{fill.b},{fill.a}|{edge.r},{edge.g},{edge.b},{edge.a}";
        if (_phoneShapes.TryGetValue(key, out var s) && s != null) return s;
        const float k = 2.5f;
        int size = Mathf.Max(16, Mathf.CeilToInt((radius + border) * k * 2 + 8));
        var made = Rounded(size, radius * k, border * k, fill, edge);
        // Shown at design size: the sprite's units are 2.5 pixels each.
        var sliced = Sprite.Create(made.texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f * k, 0, SpriteMeshType.FullRect, made.border);
        sliced.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.Destroy(made);
        return _phoneShapes[key] = sliced;
    }

    private static Sprite? _person, _chat, _chevron, _arrow, _cross, _warn, _noSignal, _check, _arc, _play, _stop, _news, _grip, _camera, _image;

    // ------------------------------------------------------------------ the phone's weather (FB-74), outlines like the app icons

    private static readonly Dictionary<string, Sprite> _sky = new();

    /// <summary>sun, moon, cloud, rain, storm, snow or fog: an outline in the phone's line weight (anything else: a cloud).</summary>
    public static Sprite WeatherIcon(string kind)
    {
        kind ??= "";
        if (_sky.TryGetValue(kind, out var s) && s != null) return s;
        return _sky[kind] = MakeIcon(96, kind switch
        {
            "sun" => (p, u) =>
            {
                var d = p - new Vector2(32, 32);
                float ring = Mathf.Abs(d.magnitude - 11f) - 2.6f, rays = 999f;
                for (int i = 0; i < 8; i++)
                {
                    float a = i * Mathf.PI / 4f;
                    var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    rays = Mathf.Min(rays, Seg(p, new Vector2(32, 32) + dir * 18f, new Vector2(32, 32) + dir * 25f) - 2.6f);
                }
                return Mathf.Min(ring, rays);
            },
            "moon" => (p, u) =>
            {
                float outer = (p - new Vector2(30, 32)).magnitude - 21f, cut = (p - new Vector2(41, 40)).magnitude - 17f;
                return Mathf.Abs(Mathf.Max(outer, -cut)) - 2.6f;
            },
            "rain" => (p, u) => Mathf.Min(CloudLine(p, 10f), Mathf.Min(Seg(p, new Vector2(24, 17), new Vector2(20, 7)), Mathf.Min(Seg(p, new Vector2(34, 17), new Vector2(30, 7)), Seg(p, new Vector2(44, 17), new Vector2(40, 7)))) - 2.6f),
            "storm" => (p, u) => Mathf.Min(CloudLine(p, 10f), Mathf.Min(Seg(p, new Vector2(35, 19), new Vector2(28, 10)), Mathf.Min(Seg(p, new Vector2(28, 10), new Vector2(36, 10)), Seg(p, new Vector2(36, 10), new Vector2(29, 1)))) - 2.6f),
            "snow" => (p, u) => Mathf.Min(CloudLine(p, 10f), Mathf.Min((p - new Vector2(22, 12)).magnitude, Mathf.Min((p - new Vector2(32, 6)).magnitude, (p - new Vector2(42, 12)).magnitude)) - 3.4f),
            "fog" => (p, u) => Mathf.Min(CloudLine(p, 12f), Mathf.Min(Seg(p, new Vector2(12, 14), new Vector2(46, 14)), Seg(p, new Vector2(20, 6), new Vector2(54, 6))) - 2.6f),
            _ => (p, u) => CloudLine(p, 0f),
        });
    }

    // ------------------------------------------------------------------ Nightline (FB-71)

    private static Sprite? _chevUp, _chevDown, _nightline;

    /// <summary>A like (a filled chevron pointing up) and a dislike (down): sprites, never font glyphs (judging/nightline.md).</summary>
    public static Sprite ChevronUp => _chevUp ??= MakeIcon(96, (p, u) => Tri(p, new Vector2(12, 22), new Vector2(52, 22), new Vector2(32, 46)) - 1.5f);
    public static Sprite ChevronDown => _chevDown ??= MakeIcon(96, (p, u) => Tri(p, new Vector2(12, 42), new Vector2(52, 42), new Vector2(32, 18)) - 1.5f);

    /// <summary>Nightline's mark: three towers on the street line and a crescent moon (the 30-unit version: no windows).</summary>
    public static Sprite NightlineIcon => _nightline ??= MakeIcon(96, (p, u) =>
    {
        // The 24-unit design (y down) in this 64-unit field (y up).
        Vector2 P(float x, float y) => new(x * 64f / 24f, 64f - y * 64f / 24f);
        float d = 999f;
        void L(float x1, float y1, float x2, float y2) => d = Mathf.Min(d, Seg(p, P(x1, y1), P(x2, y2)));
        L(3, 20.5f, 21, 20.5f);
        L(4.5f, 20.5f, 4.5f, 11.5f); L(4.5f, 11.5f, 9, 11.5f); L(9, 11.5f, 9, 20.5f);
        L(9, 20.5f, 9, 6.5f); L(9, 6.5f, 14, 6.5f); L(14, 6.5f, 14, 20.5f);
        L(14, 13.5f, 19.5f, 13.5f); L(19.5f, 13.5f, 19.5f, 20.5f);
        float stroke = d - 2.9f;
        float outer = (p - P(19.2f, 6.2f)).magnitude - 3.1f * 64f / 24f, cut = (p - P(20.7f, 5.0f)).magnitude - 2.5f * 64f / 24f;
        float moon = Mathf.Abs(Mathf.Max(outer, -cut)) - 2.6f;
        return Mathf.Min(stroke, moon);
    });

    /// <summary>Signed distance to a filled triangle (negative inside).</summary>
    private static float Tri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float edge = Mathf.Min(Seg(p, a, b), Mathf.Min(Seg(p, b, c), Seg(p, c, a)));
        float s1 = Cross(b - a, p - a), s2 = Cross(c - b, p - b), s3 = Cross(a - c, p - c);
        bool inside = (s1 >= 0 && s2 >= 0 && s3 >= 0) || (s1 <= 0 && s2 <= 0 && s3 <= 0);
        return inside ? -edge : edge;
    }

    private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

    /// <summary>A cloud's outline (three puffs on a flat base), raised by <paramref name="lift"/> to leave room under it.</summary>
    private static float CloudLine(Vector2 p, float lift)
    {
        p.y -= lift;
        float cloud = Mathf.Min(Mathf.Min((p - new Vector2(22, 30)).magnitude - 10f, (p - new Vector2(35, 36)).magnitude - 13f),
            Mathf.Min((p - new Vector2(46, 29)).magnitude - 9f, Box(p, new Vector2(34, 25), new Vector2(20, 5), 5f)));
        return Mathf.Abs(cloud) - 2.6f;
    }

    /// <summary>The mod is stopping (a hot reload): the phone's shapes and icons go with it (the full audit, B-4).</summary>
    public static void FreePhone()
    {
        foreach (var s in _phoneShapes.Values) if (s != null) Free(s);
        _phoneShapes.Clear();
        foreach (var s in _sky.Values) if (s != null) Free(s);
        _sky.Clear();
        foreach (var s in new[] { _chevUp, _chevDown, _nightline }) if (s != null) Free(s);
        _chevUp = _chevDown = _nightline = null;
        foreach (var s in new[] { _person, _chat, _chevron, _arrow, _cross, _warn, _noSignal, _check, _arc, _play, _stop, _news, _grip, _paleSmile, _paleFrown, _camera, _image })
            if (s != null) Free(s);
        _person = _chat = _chevron = _arrow = _cross = _warn = _noSignal = _check = _arc = _play = _stop = _news = _grip = _paleSmile = _paleFrown = _camera = _image = null;
    }

    /// <summary>The chat window's resize grip: two diagonal strokes in the corner.</summary>
    public static Sprite GripIcon => _grip ??= MakeIcon(64, (p, u) => Mathf.Min(Seg(p, new Vector2(58, 18), new Vector2(18, 58)), Seg(p, new Vector2(58, 38), new Vector2(38, 58))) - 3f);
    /// <summary>The News app: a folded paper with lines of print.</summary>
    public static Sprite NewsIcon => _news ??= MakeIcon(96, (p, u) =>
    {
        float page = Mathf.Abs(Box(p, new Vector2(28, 32), new Vector2(16, 18), 3)) - 2.4f;
        float fold = Mathf.Min(Seg(p, new Vector2(44, 40), new Vector2(52, 40)), Seg(p, new Vector2(52, 40), new Vector2(52, 18))) - 2.4f;
        float lines = Mathf.Min(Seg(p, new Vector2(20, 40), new Vector2(36, 40)), Mathf.Min(Seg(p, new Vector2(20, 32), new Vector2(36, 32)), Seg(p, new Vector2(20, 24), new Vector2(30, 24)))) - 2.2f;
        return Mathf.Min(page, Mathf.Min(fold, lines));
    });
    /// <summary>FB-80: the Camera app: a camera body with its hump and lens.</summary>
    public static Sprite CameraIcon => _camera ??= MakeIcon(96, (p, u) =>
    {
        float body = Mathf.Abs(Box(p, new Vector2(32, 29), new Vector2(24, 16), 5)) - 2.4f;
        float hump = Mathf.Abs(Box(p, new Vector2(24, 47), new Vector2(7, 3.5f), 2)) - 2.2f;
        float lens = Mathf.Abs((p - new Vector2(32, 29)).magnitude - 8.5f) - 2.4f;
        return Mathf.Min(body, Mathf.Min(hump, lens));
    });
    /// <summary>FB-80: the Gallery (and a photo to attach): a framed picture of hills under a sun.</summary>
    public static Sprite ImageIcon => _image ??= MakeIcon(96, (p, u) =>
    {
        float frame = Mathf.Abs(Box(p, new Vector2(32, 32), new Vector2(24, 19), 4)) - 2.4f;
        float hills = Mathf.Min(Seg(p, new Vector2(13, 20), new Vector2(26, 34)), Mathf.Min(Seg(p, new Vector2(26, 34), new Vector2(35, 25)),
                      Mathf.Min(Seg(p, new Vector2(35, 25), new Vector2(42, 31)), Seg(p, new Vector2(42, 31), new Vector2(51, 21))))) - 2.2f;
        float sun = (p - new Vector2(43, 41)).magnitude - 4f;
        return Mathf.Min(frame, Mathf.Min(hills, sun));
    });
    /// <summary>A voice note's play (a triangle) and stop (a rounded square).</summary>
    public static Sprite PlayIcon => _play ??= MakeIcon(96, (p, u) =>
    {
        // A triangle pointing right: inside all three edges.
        float a = -(p.x - 20f), b = Vector2.Dot(p - new Vector2(20, 12), new Vector2(-20, 32).normalized), c = Vector2.Dot(p - new Vector2(20, 52), new Vector2(-20, -32).normalized);
        return Mathf.Max(a, Mathf.Max(b, c)) - 1f;
    });
    public static Sprite StopIcon => _stop ??= MakeIcon(96, (p, u) => Box(p, new Vector2(32, 32), new Vector2(15, 15), 3f));
    /// <summary>FB-70: a receipt's tick, and the sending ring (three quarters of a ring: it turns while sending).</summary>
    public static Sprite CheckIcon => _check ??= MakeIcon(96, (p, u) => Mathf.Min(Seg(p, new Vector2(16, 33), new Vector2(27, 22)), Seg(p, new Vector2(27, 22), new Vector2(48, 44))) - 5f);
    public static Sprite ArcIcon => _arc ??= MakeIcon(96, (p, u) =>
    {
        var d = p - new Vector2(32, 32);
        float ring = Mathf.Abs(d.magnitude - 25f) - 3.2f;
        return d.x > 0 && d.y > 0 ? Mathf.Max(ring, 0.6f) : ring; // a quarter left open
    });
    public static Sprite PersonIcon => _person ??= MakeIcon(96, PersonShape);
    public static Sprite ChatIcon => _chat ??= MakeIcon(96, ChatShape);
    public static Sprite ChevronIcon => _chevron ??= MakeIcon(96, (p, u) => Mathf.Min(Seg(p, new Vector2(40, 50), new Vector2(23, 32)), Seg(p, new Vector2(23, 32), new Vector2(40, 14))) - 3.6f);
    public static Sprite ArrowIcon => _arrow ??= MakeIcon(96, (p, u) => Mathf.Min(Seg(p, new Vector2(12, 32), new Vector2(50, 32)), Mathf.Min(Seg(p, new Vector2(36, 18), new Vector2(50, 32)), Seg(p, new Vector2(36, 46), new Vector2(50, 32)))) - 3.6f);
    public static Sprite CrossIcon => _cross ??= MakeIcon(96, (p, u) => Mathf.Min(Seg(p, new Vector2(18, 18), new Vector2(46, 46)), Seg(p, new Vector2(46, 18), new Vector2(18, 46))) - 3.4f);
    public static Sprite WarnIcon => _warn ??= MakeIcon(96, (p, u) =>
    {
        float ring = Mathf.Abs((p - new Vector2(32, 32)).magnitude - 24f) - 3f;
        float bar = Seg(p, new Vector2(32, 42), new Vector2(32, 28)) - 3.4f;
        float dot = (p - new Vector2(32, 19)).magnitude - 3.6f;
        return Mathf.Min(ring, Mathf.Min(bar, dot));
    });
    public static Sprite NoSignalIcon => _noSignal ??= MakeIcon(96, (p, u) =>
    {
        float b = 999f;
        for (int i = 0; i < 4; i++) b = Mathf.Min(b, Box(p, new Vector2(12 + i * 13, 12 + (6 + i * 7) / 2f), new Vector2(3.2f, (6 + i * 7) / 2f), 1f));
        return Mathf.Min(b, Seg(p, new Vector2(8, 54), new Vector2(56, 8)) - 3f);
    });

    /// <summary>Distance from p to the segment a-b.</summary>
    private static float Seg(Vector2 p, Vector2 a, Vector2 b)
    {
        var pa = p - a; var ba = b - a;
        float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Mathf.Max(1e-5f, Vector2.Dot(ba, ba)));
        return (pa - ba * h).magnitude;
    }

    /// <summary>A person: a round head and shoulders (64-unit space, rows from the bottom).</summary>
    private static float PersonShape(Vector2 p, float u)
    {
        float head = Mathf.Abs((p - new Vector2(32, 42)).magnitude - 9f) - 2.6f;
        var d = p - new Vector2(32, 10);
        float shoulders = p.y > 10 && p.y < 32 ? Mathf.Abs(new Vector2(d.x / 1.15f, d.y).magnitude - 20f) - 2.6f : 999f;
        return Mathf.Min(head, shoulders);
    }

    /// <summary>A speech bubble with a tail at the bottom left, as an outline.</summary>
    private static float ChatShape(Vector2 p, float u)
    {
        float box = Mathf.Abs(Box(p, new Vector2(32, 36), new Vector2(23, 16), 6)) - 2.6f;
        float tail = Mathf.Min(Seg(p, new Vector2(18, 21), new Vector2(15, 10)), Seg(p, new Vector2(15, 10), new Vector2(28, 20))) - 2.6f;
        return Mathf.Min(box, tail);
    }

    /// <summary>A speech bubble in these colours (made once per colour pair).</summary>
    /// <param name="inUse">Whether a bubble still shows this sprite: when the cache is cleared, only the others are freed.</param>
    public static Sprite BubbleFor(Color32 fill, Color32 edge, Func<Sprite, bool> inUse)
    {
        string key = $"{fill.r},{fill.g},{fill.b},{fill.a}|{edge.r},{edge.g},{edge.b},{edge.a}";
        if (_bubbles.TryGetValue(key, out var s) && s != null) return s;
        if (_bubbles.Count > 16)
        {
            // Looks no bubble shows any more are freed with their textures, not just forgotten (LEAK-9). One a bubble still
            // shows stays cached, so it's freed in a later clear instead of being orphaned (6c review).
            foreach (var (oldKey, old) in _bubbles.ToList())
            {
                if (old != null && inUse(old)) continue;
                if (old != null) Free(old);
                _bubbles.Remove(oldKey);
            }
        }
        return _bubbles[key] = Rounded(64, 16, 3, fill, edge);
    }

    /// <summary>A sprite made here and its own texture (each has one of its own).</summary>
    private static void Free(Sprite s)
    {
        try
        {
            var tex = s.texture;
            UnityEngine.Object.Destroy(s);
            if (tex != null) UnityEngine.Object.Destroy(tex);
        }
        catch { }
    }

    private static Texture2D NewTex(int w, int h)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };
        return tex;
    }

    private static Color32 Mix(Color32 fill, Color32 edge, float edgeAmount, float alpha)
    {
        float t = Mathf.Clamp01(edgeAmount);
        var c = Color32.Lerp(fill, edge, t);
        c.a = (byte)Mathf.RoundToInt(c.a * Mathf.Clamp01(alpha));
        return c;
    }

    /// <summary>Rounded rectangle with a border, anti-aliased from a signed distance field.</summary>
    private static Sprite Rounded(int size, float radius, float border, Color32 fill, Color32 edge)
    {
        var tex = NewTex(size, size);
        var px = new Color32[size * size];
        float half = size / 2f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float qx = Mathf.Abs(x + 0.5f - half) - (half - radius);
                float qy = Mathf.Abs(y + 0.5f - half) - (half - radius);
                float outside = new Vector2(Mathf.Max(qx, 0), Mathf.Max(qy, 0)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0) - radius;
                px[y * size + x] = Mix(fill, edge, outside + border + 0.5f, 0.5f - outside);
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        float slice = radius + border + 2;
        var s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(slice, slice, slice, slice));
        s.hideFlags = HideFlags.HideAndDontSave;
        return s;
    }

    /// <summary>A round face drawn from distance fields: two eyes and a mouth curving up (smiling) or down (frowning).</summary>
    private static Sprite MakeFace(int size, Color32 face, bool smile)
    {
        var tex = NewTex(size, size);
        var px = new Color32[size * size];
        float u = size / 64f;
        var c = new Vector2(32, 32) * u;
        var ink = new Color32(30, 24, 22, 255);
        var eyeL = c + new Vector2(-10, 8) * u;
        var eyeR = c + new Vector2(10, 8) * u;
        // The mouth: part of a ring. Smiling, the lower part of one centred near the middle; frowning, the upper part of one
        // centred low (rows count up from the bottom of the texture).
        var mouthCentre = smile ? c + new Vector2(0, 2) * u : c + new Vector2(0, -22) * u;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float disk = (p - c).magnitude - 29f * u;
                float eyes = Mathf.Min((p - eyeL).magnitude, (p - eyeR).magnitude) - 4.2f * u;
                bool mouthPart = Mathf.Abs(p.x - c.x) < 12f * u && (smile ? p.y < c.y - 3f * u : p.y > c.y - 14f * u);
                float mouth = mouthPart ? Mathf.Abs((p - mouthCentre).magnitude - 14f * u) - 2.4f * u : 999f;
                float ink01 = Mathf.Clamp01(0.5f - Mathf.Min(eyes, mouth));
                var col = Color32.Lerp(face, ink, ink01);
                col.a = (byte)Mathf.RoundToInt(255 * Mathf.Clamp01(0.5f - disk));
                px[y * size + x] = col;
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        var s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        s.hideFlags = HideFlags.HideAndDontSave;
        return s;
    }

    /// <summary>A filled heart (two discs and a point, anti-aliased) with a soft dark rim, like the faces' ink.</summary>
    private static Sprite MakeHeart(int size, Color32 fill)
    {
        var tex = NewTex(size, size);
        var px = new Color32[size * size];
        float u = size / 64f;
        var ink = new Color32(30, 24, 22, 255);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // The classic heart curve, in a box -1.2..1.2 (rows count up from the bottom).
                float hx = ((x + 0.5f) / u - 32f) / 24f, hy = ((y + 0.5f) / u - 30f) / 24f;
                float a = hx * hx + hy * hy - 1f;
                float f = a * a * a - hx * hx * hy * hy * hy; // < 0 inside
                float inside = Mathf.Clamp01(0.5f - f * 6f);
                float rim = Mathf.Clamp01(0.5f - Mathf.Abs(f) * 14f);
                var col = Color32.Lerp(fill, ink, rim * 0.6f);
                col.a = (byte)Mathf.RoundToInt(255 * Mathf.Clamp01(inside + rim * 0.5f));
                px[y * size + x] = col;
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        var s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        s.hideFlags = HideFlags.HideAndDontSave;
        return s;
    }

    /// <summary>An icon from a distance function (negative inside), anti-aliased, in white.</summary>
    private static Sprite MakeIcon(int size, Func<Vector2, float, float> shape)
    {
        var tex = NewTex(size, size);
        var px = new Color32[size * size];
        float u = size / 64f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = shape(new Vector2(x + 0.5f, y + 0.5f) / u, u) * u;
                px[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(255 * Mathf.Clamp01(0.5f - d)));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        var s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        s.hideFlags = HideFlags.HideAndDontSave;
        return s;
    }

    private static float Box(Vector2 p, Vector2 centre, Vector2 half, float r)
    {
        var q = new Vector2(Mathf.Abs(p.x - centre.x), Mathf.Abs(p.y - centre.y)) - half + new Vector2(r, r);
        return new Vector2(Mathf.Max(q.x, 0), Mathf.Max(q.y, 0)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0) - r;
    }

    /// <summary>A keyboard: a rounded outline, two rows of keys and a space bar (64-unit space).</summary>
    private static float KeyboardShape(Vector2 p, float u)
    {
        float outline = Mathf.Abs(Box(p, new Vector2(32, 32), new Vector2(25, 15), 5)) - 2.2f;
        float keys = 999f;
        for (int i = 0; i < 5; i++)
        {
            keys = Mathf.Min(keys, Box(p, new Vector2(16 + i * 8, 39), new Vector2(2.4f, 2.4f), 0.8f));
            keys = Mathf.Min(keys, Box(p, new Vector2(16 + i * 8, 31), new Vector2(2.4f, 2.4f), 0.8f));
        }
        float space = Box(p, new Vector2(32, 23), new Vector2(11, 2.2f), 1.2f);
        return Mathf.Min(outline, Mathf.Min(keys, space));
    }

    /// <summary>A cogwheel: eight teeth around a ring, with a hole in the middle (64-unit space).</summary>
    private static float CogShape(Vector2 p, float u)
    {
        var d = p - new Vector2(32, 32);
        float r = d.magnitude;
        float body = r - 17f;
        float a = Mathf.Atan2(d.y, d.x);
        float sector = Mathf.PI * 2f / 8f;
        float local = Mathf.Repeat(a + sector / 2f, sector) - sector / 2f;
        var t = new Vector2(Mathf.Cos(local) * r, Mathf.Sin(local) * r); // this point, turned to the nearest tooth
        float tooth = Box(t, new Vector2(20f, 0f), new Vector2(6.5f, 4.6f), 1.5f);
        float wheel = Mathf.Min(body, tooth);
        float hole = 7.5f - r;
        return Mathf.Max(wheel, hole);
    }

    /// <summary>
    /// A classic handset drawn from distance fields: earpiece top-left, mouthpiece bottom-right, joined by a curved grip.
    /// </summary>
    private static Sprite MakePhone(int size, Color32 colour)
    {
        var tex = NewTex(size, size);
        var px = new Color32[size * size];
        float u = size / 64f;
        var ear = new Vector2(17, 46) * u;
        var mouth = new Vector2(46, 17) * u;
        var centre = new Vector2(46, 46) * u;
        float grip = 29f * u, thick = 5f * u, cup = 10f * u;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                // The grip: a quarter ring around the top-right corner, bulging toward the bottom-left.
                var d = p - centre;
                float ring = d.x <= 0 && d.y <= 0 ? Mathf.Abs(d.magnitude - grip) - thick : 999f;
                // The two cups: rounded ends, a little longer along the grip.
                float earD = (new Vector2((p.x - ear.x) * 0.85f, p.y - ear.y)).magnitude - cup;
                float mouthD = (new Vector2(p.x - mouth.x, (p.y - mouth.y) * 0.85f)).magnitude - cup;
                float dist = Mathf.Min(ring, Mathf.Min(earD, mouthD));
                var c = colour;
                c.a = (byte)Mathf.RoundToInt(colour.a * Mathf.Clamp01(0.5f - dist));
                px[y * size + x] = c;
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        var s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        s.hideFlags = HideFlags.HideAndDontSave;
        return s;
    }

    /// <summary>
    /// Speech-bubble tail for the bottom-right corner. The top band covers the bubble's rounded corner (keeping its
    /// right border straight); below it a triangle points down to the right with borders on the slope and right edge.
    /// </summary>
    private static Sprite MakeTail(Color32 fill, Color32 edge, float border)
    {
        int w = TailW, h = TailH, t = TailTri;
        var tex = NewTex(w, h);
        var px = new Color32[w * h];
        float diag = Mathf.Sqrt(t * t + w * w);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float cx = x + 0.5f, cy = y + 0.5f;
                float toRight = w - cx;
                if (cy >= t)
                {
                    px[y * w + x] = Mix(fill, edge, border - toRight + 0.5f, 1f);
                    continue;
                }
                float inside = (t * cx + w * cy - w * t) / diag; // distance inside the slope
                float nearest = Mathf.Min(inside, toRight);
                px[y * w + x] = Mix(fill, edge, border - nearest + 0.5f, inside + 0.5f);
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        var s = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(1f, 0f), 100f, 0, SpriteMeshType.FullRect);
        s.hideFlags = HideFlags.HideAndDontSave;
        return s;
    }
}
