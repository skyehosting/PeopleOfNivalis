using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisAIChat.UI;

/// <summary>The phone's Settings and its two sub-screens (Weather costs, Places), as phone-6.html draws them.</summary>
internal sealed partial class PhoneWindow
{
    /// <summary>A group of rows on a settings screen, built top to bottom.</summary>
    private sealed class Group
    {
        public RectTransform Rt = null!;
        public float Y;
        public bool First = true;
    }

    private const float GroupW = ScreenW - 2 * G, Inner = GroupW - 2 * G;

    private Group BeginGroup(RectTransform list, ref float y, string title)
    {
        var g = new Group { Rt = Box(list, "Group", G, y, GroupW, 10) };
        Img(g.Rt, Sprites.Round(10, 1, Panel, LineC));
        if (title.Length > 0)
        {
            var t = Label(g.Rt, title.ToUpperInvariant(), 13, Muted, G, 12, Inner, 16, TextAlignmentOptions.TopLeft, bold: true, wrap: false);
            t.characterSpacing = 8;
            g.Y = 12 + 16 + 4;
        }
        return g;
    }

    private void EndGroup(Group g, ref float y, float after = 10)
    {
        g.Rt.sizeDelta = new Vector2(GroupW, g.Y);
        y += g.Y + after;
    }

    /// <summary>
    /// A row: label (and hint) on the left, a control <paramref name="controlW"/> wide on the right, vertically centred.
    /// Returns the row and where the control goes. Dimmed rows (a switch above them is off) are drawn at 45% and do nothing.
    /// </summary>
    private (RectTransform row, float cx, float h) SetRow(Group g, string label, string hint, float controlW, bool dim = false, bool richLabel = false)
    {
        float textW = Inner - controlW - (controlW > 0 ? 10 : 0);
        var row = Box(g.Rt, "Row", 0, g.Y, GroupW, 52);
        if (!g.First) Solid(row, 0, 0, GroupW, 1, new Color32(118, 97, 62, 51));
        g.First = false;
        var lab = Label(row, label, 15, TextC, G, 0, textW, 20, rich: richLabel);
        float lh = Tall(lab, textW);
        float hh = 0;
        TextMeshProUGUI? hl = null;
        if (hint.Length > 0) { hl = Label(row, hint, 13, Muted, G, 0, textW, 17); hh = Tall(hl, textW); }
        float h = Mathf.Max(52, 9 + lh + hh + 9);
        float ty = (h - lh - hh) / 2;
        lab.rectTransform.anchoredPosition = new Vector2(G, -ty);
        lab.rectTransform.sizeDelta = new Vector2(textW, lh);
        if (hl != null) { hl.rectTransform.anchoredPosition = new Vector2(G, -(ty + lh)); hl.rectTransform.sizeDelta = new Vector2(textW, hh); }
        row.sizeDelta = new Vector2(GroupW, h);
        if (dim) row.gameObject.AddComponent<CanvasGroup>().alpha = 0.45f;
        g.Y += h;
        return (row, GroupW - G - controlW, h);
    }

    private void Switch(RectTransform row, float x, float h, bool on, bool enabled, Action flip)
    {
        var sw = Box(row, "Switch", x, (h - 24) / 2, 42, 24);
        Img(sw, Sprites.Round(12, 0, White, White), on ? new Color32(127, 191, 106, 140) : Off);
        Img(Box(sw, "Knob", on ? 21 : 3, 3, 18, 18), Sprites.Round(9, 0, White, White), on ? new Color32(232, 242, 228, 255) : new Color32(138, 133, 125, 255));
        Clickable(sw, flip, radius: 12, enabled: enabled);
    }

    /// <summary>A stepper: [−] value [+], one frame; the ends grey out at the limits.</summary>
    private void Stepper(RectTransform row, float x, float h, int value, int min, int max, bool enabled, string key, string zero = "", string unit = "")
    {
        float vw = unit.Length > 0 ? 40 : 24, w = 60 + vw;
        var st = Box(row, "Stepper", x - (w - 84), (h - 32) / 2, w, 32);
        Img(st, Sprites.Round(8, 1, InputFill, GoldDim));
        Solid(st, 30, 6, 1, 20, new Color32(118, 97, 62, 90));
        Solid(st, 30 + vw, 6, 1, 20, new Color32(118, 97, 62, 90));
        var minus = Box(st, "Minus", 0, 0, 30, 32);
        var mImg = Img(minus, Sprites.Round(8, 0, White, White), Clear);
        Label(minus, "−", 17, value > min ? NameC : new Color32(90, 85, 78, 255), 0, 4, 30, 24, TextAlignmentOptions.Top, bold: true, wrap: false);
        Label(st, value == 0 && zero.Length > 0 ? zero : value + unit, value == 0 && zero.Length > 0 ? 12 : 15, TextC, 30, 6, vw, 20, TextAlignmentOptions.Top, bold: true, wrap: false);
        var plus = Box(st, "Plus", 30 + vw, 0, 30, 32);
        var pImg = Img(plus, Sprites.Round(8, 0, White, White), Clear);
        Label(plus, "+", 17, value < max ? NameC : new Color32(90, 85, 78, 255), 0, 4, 30, 24, TextAlignmentOptions.Top, bold: true, wrap: false);
        var hm = Clickable(minus, () => Step?.Invoke(key, -1), ring: false, enabled: enabled && value > min);
        hm.Tint = mImg; hm.IdleColor = Clear; hm.HoverColor = new Color32(196, 160, 98, 51);
        var hp = Clickable(plus, () => Step?.Invoke(key, +1), ring: false, enabled: enabled && value < max);
        hp.Tint = pImg; hp.IdleColor = Clear; hp.HoverColor = new Color32(196, 160, 98, 51);
    }

    /// <summary>A small button: "fill" (gold), "ghost" (outlined), "off" (greyed). Returns its width.</summary>
    private float Mini(RectTransform parent, float rightX, float y, string text, string style, Action? act)
    {
        var probe = Label(parent, text, 14, Dark, 0, 0, 200, 20, bold: true, wrap: false);
        float w = Mathf.Max(54, Wide(probe) + 24);
        UnityEngine.Object.Destroy(probe.gameObject);
        var b = Box(parent, text, rightX - w, y, w, 34);
        Img(b, style == "fill" ? Sprites.Round(8, 0, NameC, NameC) : style == "off" ? Sprites.Round(8, 0, Off, Off) : Sprites.Round(8, 1, Clear, GoldDim));
        Label(b, text, 14, style == "fill" ? Dark : style == "off" ? OffText : NameC, 0, 8, w, 20, TextAlignmentOptions.Top, bold: true, wrap: false);
        if (act != null) Clickable(b, act, radius: 8, enabled: style != "off");
        return w;
    }

    /// <summary>A row that opens a sub-screen: label, its value, a gold ›; hovered as a fill inside the group.</summary>
    private void LinkRow(Group g, string label, string value, bool dim, Action open)
    {
        var (row, _, h) = SetRow(g, label, "", 150, dim);
        var v = Label(row, "", 14, Muted, GroupW - G - 150, (h - 18) / 2, 136, 18, TextAlignmentOptions.TopRight, wrap: false);
        FitLine(v, value, 136);
        Label(row, "›", 18, Gold, GroupW - G - 12, (h - 24) / 2, 12, 24, TextAlignmentOptions.TopRight, bold: true, wrap: false);
        var bg = Box(row, "Hover", 1, 1, GroupW - 2, h - 2);
        var bgImg = Img(bg, Sprites.Round(0.5f, 0, White, White), Clear);
        bg.SetAsFirstSibling();
        var hit = Clickable(row, open, ring: false, enabled: !dim);
        hit.Tint = bgImg; hit.IdleColor = Clear; hit.HoverColor = new Color32(196, 160, 98, 26);
    }

    private void Note(Group g, string text, Color color, float padTop = 2)
    {
        var t = Label(g.Rt, text, 13, color, G, g.Y + padTop, Inner, 10);
        float th = Tall(t, Inner);
        t.rectTransform.sizeDelta = new Vector2(Inner, th);
        g.Y += padTop + th + 10;
    }

    /// <summary>The Undo strip, inside its group as its last child; scrolled into view.</summary>
    private void UndoStrip(Group g, float groupTopInList)
    {
        if (View.Undo.Length == 0 || View.UndoScreen != ScreenName) return;
        const float linkW = 40;
        var strip = Box(g.Rt, "Undo", G, g.Y + 4, Inner, 10);
        Img(strip, Sprites.Round(8, 1, new Color32(38, 35, 31, 255), LineC));
        var t = Label(strip, View.Undo, 14, TextC, 12, 8, Inner - 24 - linkW - 12, 10);
        float th = Tall(t, Inner - 24 - linkW - 12);
        t.rectTransform.sizeDelta = new Vector2(Inner - 24 - linkW - 12, th);
        float sh = Mathf.Max(36, th + 16);
        strip.sizeDelta = new Vector2(Inner, sh);
        var link = Box(strip, "UndoLink", Inner - 12 - linkW, (sh - 20) / 2, linkW, 20);
        Label(link, "Undo", 14, NameC, 0, 0, linkW, 20, TextAlignmentOptions.TopRight, bold: true, wrap: false);
        Clickable(link, () => Undo?.Invoke(), ring: false);
        float stripTop = groupTopInList + g.Y + 4;
        g.Y += 4 + sh + 12;
        _pendingIntoView = (stripTop, sh + 12);
    }

    private (float y, float h)? _pendingIntoView;

    // ------------------------------------------------------------------ Settings

    private void RenderSettings()
    {
        AppBar("Settings");
        var list = BeginScroll(BarH, AreaH - BarH);
        float y = 0;
        _pendingIntoView = null;

        // Wallpaper: a link (Set), what's kept, Default.
        float wallTop = y + 10;
        y += 10;
        var w = BeginGroup(list, ref y, "Wallpaper");
        float setW = 54;
        var fieldRow = Box(w.Rt, "Link", G, w.Y + 4, Inner, 34);
        PlaceUrl(fieldRow, Inner - setW - 6);
        Mini(fieldRow, Inner, 0, "Set", View.WallpaperBusy ? "off" : "fill", View.WallpaperBusy ? null : SetNow);
        w.Y += 4 + 34 + 6;
        Note(w, "A PNG, JPG, GIF or BMP link, up to 15 MB (Ctrl+V pastes). Read by what it is, not how the link ends.", Muted, 0);
        if (View.WallpaperNote.Length > 0 || View.WallpaperKept)
        {
            var note = Box(w.Rt, "Kept", G, w.Y - 4, Inner, 46);
            float textX = 0;
            if (View.WallpaperKept && !View.WallpaperError && !View.WallpaperBusy)
            {
                var thumb = Box(note, "Thumb", 0, 0, 30, 46);
                var raw = thumb.gameObject.AddComponent<RawImage>();
                raw.raycastTarget = false;
                _wallImgs.Add((raw, 30f / 46f));
                ShowWallTexture();
                textX = 38;
            }
            float defW = View.WallpaperKept && !View.WallpaperBusy ? 82 : 0;
            var t = Label(note, View.WallpaperNote, 13, View.WallpaperError ? Err : PillC, textX, 0, Inner - textX - defW - 8, 46, TextAlignmentOptions.MidlineLeft);
            if (defW > 0) Mini(note, Inner, 6, "Default", "ghost", () => WallpaperDefault?.Invoke());
            float nh = Mathf.Max(View.WallpaperKept ? 46 : 0, Tall(t, Inner - textX - defW - 8));
            note.sizeDelta = new Vector2(Inner, nh);
            t.rectTransform.sizeDelta = new Vector2(Inner - textX - defW - 8, nh);
            w.Y += nh + 6;
        }
        UndoStrip(w, wallTop);
        EndGroup(w, ref y);

        // Signal.
        float sigTop = y;
        var s = BeginGroup(list, ref y, "Signal");
        var (r1, x1, h1) = SetRow(s, "Simulate signal", "Off: always full bars", 42);
        Switch(r1, x1, h1, View.Signal, true, () => Toggle?.Invoke("signal", !View.Signal));
        bool off = !View.Signal;
        var (r2, x2, h2) = SetRow(s, "Weather matters", "Storms and snow cost bars", 42, off);
        Switch(r2, x2, h2, View.Weather, !off, () => Toggle?.Invoke("weather", !View.Weather));
        LinkRow(s, "Weather costs", View.CostsSummary, off || !View.Weather, () => Go("weather"));
        var (r3, x3, h3) = SetRow(s, "Places matter", "Indoors and weak spots", 42, off);
        Switch(r3, x3, h3, View.Places, !off, () => Toggle?.Invoke("places", !View.Places));
        string here = View.HerePlace.Length > 0 ? View.HerePlace : "here";
        var (r4, x4, h4) = SetRow(s, "Here: " + here, "Most bars here; 0 = none", 84, off || !View.Places || View.HerePlace.Length == 0);
        Stepper(r4, x4, h4, View.HereBars, 0, 4, !off && View.Places && View.HerePlace.Length > 0, "here");
        LinkRow(s, "Places", View.PlacesSummary, off || !View.Places, () => Go("places"));
        var (r5, _, h5) = SetRow(s, "Signal now", "", 150);
        Readout(r5, GroupW - G, h5);
        Note(s, "Also in PeopleOfNivalis.cfg.", Muted, 0);
        EndGroup(s, ref y);

        // Texts.
        var t2 = BeginGroup(list, ref y, "Texts");
        var (r6, x6, h6) = SetRow(t2, "People text you", "How often: the Usage Optimizer", 42);
        Switch(r6, x6, h6, View.PeopleText, true, () => Toggle?.Invoke("texts", !View.PeopleText));
        var (r7, _, h7) = SetRow(t2, "Your number", "", 100);
        Label(r7, View.MyNumber, 14, Muted, GroupW - G - 100, (h7 - 18) / 2, 100, 18, TextAlignmentOptions.TopRight, wrap: false).characterSpacing = 4;
        EndGroup(t2, ref y);
        // FB-70: texting like real life, and its two starting chances (the closer you are, the smaller).
        var rg = BeginGroup(list, ref y, "Replies");
        var (rr, xr, hr) = SetRow(rg, "Real-life replies", "Off: they always answer", 42);
        Switch(rr, xr, hr, View.RealLife, true, () => Toggle?.Invoke("real", !View.RealLife));
        var (rn, xn, hn) = SetRow(rg, "Never sees it", "Rarer for friends", 100, !View.RealLife);
        Stepper(rn, xn + 16, hn, View.NeverSees, 0, 50, View.RealLife, "never", unit: "%");
        var (rl, xl, hl) = SetRow(rg, "Answers later", "Seen, answered later", 100, !View.RealLife);
        Stepper(rl, xl + 16, hl, View.AnswersLater, 0, 50, View.RealLife, "later", unit: "%");
        Note(rg, "Near 0 for close friends, higher for people who dislike you. Mid-conversation, they keep answering.", Muted, 2);
        EndGroup(rg, ref y);
        // Voice notes: off by default; also in the mod's settings (Switchboard).
        var vg = BeginGroup(list, ref y, "Voice notes");
        var (rv, xv, hv) = SetRow(vg, "Voice notes", "A voice clip under some texts", 42);
        Switch(rv, xv, hv, View.VoiceNotes, true, () => Toggle?.Invoke("voice", !View.VoiceNotes));
        var (rc, xc, hc) = SetRow(vg, "How often", "Share of their answers", 100, !View.VoiceNotes);
        Stepper(rc, xc + 16, hc, View.VoiceChance, 0, 100, View.VoiceNotes, "voicechance", unit: "%");
        var (ra, xa, ha) = SetRow(vg, "Ava's made voice", "Only when she wants", 42);
        Switch(ra, xa, ha, View.AvaVoice, true, () => Toggle?.Invoke("ava", !View.AvaVoice));
        Note(vg, "Kept on this PC. Ava's recordings always play. Also in the mod's settings.", Muted, 2);
        EndGroup(vg, ref y, after: 12);
        EndScroll(y);
        if (_pendingIntoView is { } v) { ScrollIntoView(v.y, v.h); _pendingIntoView = null; }
        _ = sigTop;
    }

    private void Readout(RectTransform row, float right, float h)
    {
        var t = Label(row, "", 14, BodyC, 0, (h - 18) / 2, 130, 18, TextAlignmentOptions.TopRight, wrap: false);
        FitLine(t, View.Readout, 130);
        float tw = Mathf.Min(130, Wide(t));
        t.rectTransform.anchoredPosition = new Vector2(right - 130, -(h - 18) / 2);
        float bx = right - tw - 8 - 18;
        for (int i = 0; i < 4; i++)
        {
            float bh = new[] { 3f, 5f, 8f, 11f }[i];
            Solid(row, bx + i * 5, (h - 11) / 2 + (11 - bh), 3, bh, i < View.ReadoutBars ? NameC : Off);
        }
    }

    // ------------------------------------------------------------------ Weather costs

    private static readonly string[] WeatherNames = { "Pouring rain", "Thunder", "Heavy snow", "Blizzard", "Thick fog" };
    private static readonly string[] WeatherKeys = { "rain", "thunder", "snow", "blizzard", "fog" };

    private void RenderWeather()
    {
        AppBar("Weather costs", back: true);
        var list = BeginScroll(BarH, AreaH - BarH);
        float y = 10;
        var g = BeginGroup(list, ref y, "Bars lost in");
        for (int i = 0; i < 5; i++)
        {
            string label = WeatherNames[i] + (View.WeatherNow == i ? "  <color=#1A1714><mark=#96C4A8 padding=\"6,6,2,2\"><size=13><b>now</b></size></mark></color>" : "");
            var (row, x, h) = SetRow(g, label, "", 84, richLabel: true);
            Stepper(row, x, h, View.Costs[i], 0, 4, true, "cost:" + WeatherKeys[i]);
        }
        EndGroup(g, ref y);
        float footTop = y;
        var f = BeginGroup(list, ref y, "");
        var (fr, _, fh) = SetRow(f, "", "They add up: a blizzard with thunder costs 3.", 82);
        Mini(fr, GroupW - G, (fh - 34) / 2, "Defaults", "ghost", () => CostsDefaults?.Invoke());
        UndoStrip(f, footTop);
        EndGroup(f, ref y, after: 12);
        EndScroll(y);
        if (_pendingIntoView is { } v) { ScrollIntoView(v.y, v.h); _pendingIntoView = null; }
    }

    // ------------------------------------------------------------------ Places

    private void RenderPlaces()
    {
        AppBar("Places", back: true);
        var list = BeginScroll(BarH, AreaH - BarH);
        float y = 10;
        const float ctl = 84 + 8 + 32; // stepper, gap, the remove button's slot: every stepper in one column
        var ind = BeginGroup(list, ref y, "Indoors (not at home)");
        var (ir, ix, ih) = SetRow(ind, "Bars lost", "", ctl);
        Stepper(ir, ix, ih, View.Indoors, 0, 2, true, "indoors");
        EndGroup(ind, ref y);
        float listTop = y;
        var g = BeginGroup(list, ref y, "Bars at most (0 = no signal)");
        foreach (var (place, bars) in View.PlaceList)
        {
            var (row, x, h) = SetRow(g, place, "", ctl);
            Stepper(row, x, h, bars, 0, 4, true, "place:" + place);
            var xb = Box(row, "Remove", GroupW - G - 32, (h - 32) / 2, 32, 32);
            Img(xb, Sprites.Round(8, 1, InputFill, GoldDim));
            Icon(xb, Sprites.CrossIcon, 9, 9, 14, NameC);
            string name = place;
            Clickable(xb, () => RemovePlace?.Invoke(name), radius: 8);
        }
        if (View.HerePlace.Length > 0 && !View.HereListed)
        {
            var (row, _, h) = SetRow(g, "Where you are", $"{View.HerePlace} · 4 bars", 54);
            Mini(row, GroupW - G, (h - 34) / 2, "Add", "fill", () => AddHere?.Invoke());
            Note(g, "Matches the district, venue or scene.", Muted, 0);
        }
        var (dr, _, dh) = SetRow(g, "", "Back to the starting list.", 82);
        Mini(dr, GroupW - G, (dh - 34) / 2, "Defaults", "ghost", () => PlacesDefaults?.Invoke());
        UndoStrip(g, listTop);
        EndGroup(g, ref y, after: 12);
        EndScroll(y);
        if (_pendingIntoView is { } v) { ScrollIntoView(v.y, v.h); _pendingIntoView = null; }
    }
}
