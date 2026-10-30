using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisAIChat.UI;

/// <summary>
/// Nightline's screens (FB-71): the
/// light app inside the dark phone. The feed with three tabs (The city · Your people · You), each line its own card (the
/// portrait in its ring, the @handle and place on one line, the photo cover-cropped from the top, likes and dislikes that
/// toggle, the comment count), popovers for who reacted and who saw it on a top layer (above the list's mask, flipped when
/// there's no room, held 12 from the screen's edges), a line opened with its comments and replies, right-click to delete
/// your own line or comment (with Undo), and a dim at night over all of it.
/// </summary>
internal sealed partial class PhoneWindow
{
    // The light palette (nightline-5.html :root).
    private static readonly Color32 Paper = new(245, 241, 234, 255), CardC = new(255, 253, 249, 255), Ink = new(34, 30, 26, 255), Ink2 = new(74, 68, 61, 255),
        Ink3 = new(107, 99, 90, 255), Rule = new(227, 220, 209, 255), Accent = new(181, 131, 46, 255), GoldText = new(138, 95, 26, 255), Accent2 = new(233, 215, 181, 255),
        LikeC = new(35, 112, 78, 255), LikeFill = new(230, 242, 235, 255), DislikeC = new(169, 59, 45, 255), DislikeFill = new(249, 232, 229, 255), PopC = new(34, 30, 26, 250);

    public Action<string>? LinePost, LineDelete, LineCancel, LineTab;
    public Action<string, bool>? LineReact;
    public Action<string, string, string>? LineComment;
    public Action<string, string>? LineDeleteComment;

    /// <summary>Hover popovers: the trigger, what to build, and the one showing.</summary>
    private readonly List<(RectTransform trigger, Func<RectTransform> make)> _pops = new();
    private (RectTransform? trigger, RectTransform? pop) _popShown;
    /// <summary>Your own lines and comments on screen (right-click to delete): their rect, line, comment ("" the line).</summary>
    private readonly List<(RectTransform rt, string line, string comment)> _mineRects = new();
    private RectTransform? _lineMenu;

    private bool IsNightline => ScreenName is "nightline" or "line";

    // ------------------------------------------------------------------ the feed

    private void RenderNightline()
    {
        Solid(_area!, 0, 0, ScreenW, AreaH, Paper);
        LightBar("nightline", "", back: true);
        float y = BarH;
        // The tabs: equal thirds, the underline the cell's width.
        var tabs = Box(_area!, "Tabs", 0, y, ScreenW, 36);
        Solid(tabs, 0, 0, ScreenW, 36, CardC);
        Solid(tabs, 0, 35, ScreenW, 1.5f, Rule);
        string[] keys = { "city", "people", "you" }, names = { "The city", "Your people", "You" };
        for (int i = 0; i < 3; i++)
        {
            float w = ScreenW / 3f;
            var t = Box(tabs, "Tab", i * w, 0, w, 36);
            bool on = View.LineTab == keys[i];
            Label(t, names[i], 14, on ? Ink : Ink3, 0, 9, w, 18, TextAlignmentOptions.Top, bold: true, wrap: false);
            if (on) Solid(t, 0, 33, w, 3, Accent);
            if (i == 2 && View.LineUnseen > 0 && !on) Img(Box(t, "Dot", w / 2 + 18, 14, 8, 8), Sprites.Round(4, 0, White, White), new Color32(217, 96, 79, 255));
            string k = keys[i];
            Clickable(t, () => { View.LineTab = k; _scrollPos = _scrollTarget = 0; LineTab?.Invoke(k); }, ring: false);
        }
        y += 36;
        // A strip in its own row: no signal, or news on your lines (clicking it opens You).
        if (View.Bars <= 0) y = Strip(y, "No signal. Your line goes up when a bar comes back.", nosig: true, null);
        else if (View.LineNews.Length > 0 && View.LineTab != "you") y = Strip(y, View.LineNews, nosig: false, () => { View.LineTab = "you"; LineTab?.Invoke("you"); });
        float composeH = 54 + (View.LineAttach.Length > 0 ? 46 : 0); // FB-80: your photo above the box
        var list = BeginScroll(y, AreaH - y - composeH);
        float cy = 8;
        if (View.Lines.Count == 0)
        {
            string text = View.LineTab switch
            {
                "people" => "No one you know has posted yet. Ask people for their number, or drop a line yourself.",
                "you" => "You haven't dropped a line yet. Type one below.",
                _ => "No one's posted yet. People drop a line now and then through the day, or be the first.",
            };
            EmptyLight(list, AreaH - y - composeH, "Quiet on the line", text);
        }
        foreach (var l in View.Lines) cy = Card(list, cy, l, opened: false) + 8;
        EndScroll(cy + 4);
        LightCompose(composeH, "Drop a line…");
        Dim();
    }

    private float Strip(float y, string text, bool nosig, Action? open)
    {
        var s = Box(_area!, "Strip", G, y + 8, ScreenW - 2 * G, 10);
        Img(s, Sprites.Round(12, 1.5f, nosig ? DislikeFill : new Color32(255, 246, 227, 255), nosig ? new Color32(169, 59, 45, 110) : Accent2));
        Icon(s, nosig ? Sprites.NoSignalIcon : Sprites.ChatIcon, 10, 8, 16, nosig ? DislikeC : GoldText);
        var t = Label(s, text, 13, nosig ? new Color32(126, 42, 31, 255) : GoldText, 32, 7, ScreenW - 2 * G - 42 - (open != null ? 16 : 0), 10, bold: !nosig);
        float h = Mathf.Max(16, Tall(t, ScreenW - 2 * G - 42 - (open != null ? 16 : 0)));
        t.rectTransform.sizeDelta = new Vector2(t.rectTransform.sizeDelta.x, h);
        s.sizeDelta = new Vector2(ScreenW - 2 * G, h + 14);
        if (open != null) { Icon(s, Sprites.ChevronIcon, ScreenW - 2 * G - 22, (h + 14) / 2 - 7, 14, GoldText).rectTransform.localScale = new Vector3(-1, 1, 1); Clickable(s, open, radius: 12, ring: false); }
        return y + 8 + h + 14;
    }

    // ------------------------------------------------------------------ a card

    /// <summary>One line's card at <paramref name="y"/> in the list; returns its bottom.</summary>
    private float Card(RectTransform list, float y, PhoneView.LineCard l, bool opened)
    {
        float x = opened ? 0 : G, w = opened ? ScreenW : ScreenW - 2 * G, pad = 12, inner = w - 2 * pad;
        var card = Box(list, "Line", x, y, w, 10);
        var bg = Img(card, opened ? Sprites.Round(0.5f, 0, CardC, CardC) : Sprites.Round(12, l.Waiting ? 1.5f : 1, CardC, l.Waiting ? new Color32(205, 191, 168, 255) : l.New > 0 ? new Color32(231, 201, 143, 255) : Rule));
        float av = opened ? 42 : 38;
        Avatar(card, pad, 10, av, l.Name, l.Kind == "stranger" ? "known" : l.Kind == "you" ? "ava" : l.Kind, l.Kind == "you" ? null : l.Portrait);
        float mx = pad + av + 9, mw = w - mx - pad;
        var ago = Label(card, l.Ago, 13, l.Waiting ? GoldText : Ink3, 0, 12, 70, 17, TextAlignmentOptions.TopRight, bold: true, wrap: false);
        float agoW = Mathf.Min(70, Wide(ago));
        ago.rectTransform.anchoredPosition = new Vector2(w - pad - 70, -12);
        float cancelW = 0;
        if (l.Waiting && l.Yours)
        {
            var c = Box(card, "Cancel", w - pad - agoW - 8 - 56, 8, 56, 24);
            Img(c, Sprites.Round(12, 1, DislikeFill, new Color32(169, 59, 45, 110)));
            Label(c, "Cancel", 13, DislikeC, 0, 4, 56, 16, TextAlignmentOptions.Top, bold: true, wrap: false);
            string lid = l.Id;
            Clickable(c, () => LineCancel?.Invoke(lid), radius: 12);
            cancelW = 64;
        }
        var nm = Label(card, l.Name, opened ? 16 : 15, Ink, mx, 10, mw - agoW - 8 - cancelW, 20, bold: true, wrap: false);
        FitLine(nm, l.Name, mw - agoW - 8 - cancelW);
        string second = "@" + l.Handle + (l.Place.Length > 0 ? " · " : "");
        var r2 = Label(card, second, 13, Ink3, mx, 30, mw, 17, wrap: false);
        float hw = Mathf.Min(Wide(r2), mw * 0.6f);
        FitLine(r2, second, hw);
        if (l.Place.Length > 0)
        {
            var pl = Label(card, l.Place, 13, GoldText, mx + hw, 30, mw - hw, 17, bold: true, wrap: false);
            FitLine(pl, l.Place, mw - hw);
        }
        float cy = 10 + av + 6;
        var text = Label(card, l.Text, 15, Ink, pad, cy, inner, 20);
        float th = Tall(text, inner);
        text.rectTransform.sizeDelta = new Vector2(inner, th);
        cy += th + 2;
        if (l.Developing || l.Photo.Length > 0)
        {
            float ph = opened ? 120 : 150;
            var p = Box(card, "Photo", pad, cy + 6, inner, ph);
            if (l.Developing) { Img(p, Sprites.Round(10, 0, new Color32(228, 220, 205, 255), new Color32(228, 220, 205, 255))); Label(p, "Developing the photo…", 13, Ink2, 0, ph / 2 - 9, inner, 18, TextAlignmentOptions.Top, bold: true, wrap: false); }
            else
            {
                Img(p, Sprites.Round(opened ? 0.5f : 10, 0, new Color32(60, 70, 82, 255), new Color32(60, 70, 82, 255)));
                p.gameObject.AddComponent<Mask>().showMaskGraphic = true; // its rounded corners cut the photo
                var tex = PhotoTex(l.Photo);
                if (tex != null)
                {
                    var raw = Raw(p, tex, 0, 0, inner, ph);
                    raw.uvRect = Cover(tex.width, tex.height, inner, ph);
                }
                string note = l.PhotoNote;
                if (note.Length > 0) // FB-80: only a photo the image service made says so (yours are yours)
                {
                    var chip = Box(p, "Made", 6, ph - 24, 84, 18);
                    Img(chip, Sprites.Round(6, 0, new Color32(20, 18, 16, 168), new Color32(20, 18, 16, 168)));
                    Label(chip, "made photo", 13, White, 0, 0, 84, 18, TextAlignmentOptions.Center, bold: true, wrap: false);
                    _pops.Add((chip, () => Popover("", new List<PhoneView.Who>(), note, 210)));
                }
            }
            cy += 6 + ph;
        }
        if (!l.Waiting)
        {
            cy += 8;
            string lid = l.Id;
            var up = Pill(card, pad, cy, UpIcon, l.Up.ToString(), l.Mine == true ? LikeC : Ink2, l.Mine == true ? LikeFill : CardC, l.Mine == true ? new Color32(35, 112, 78, 128) : Rule);
            if (!l.Yours) Clickable(up, () => LineReact?.Invoke(lid, true), radius: 14);
            _pops.Add((up, () => Popover("Liked by", l.Likers, l.Likers.Count == 0 ? "No one yet" : l.Likers.Count > 3 ? $"and {l.Likers.Count - 3} more" : "", 200)));
            var down = Pill(card, pad + up.rect.width + 6, cy, DownIcon, l.Down.ToString(), l.Mine == false ? DislikeC : Ink2, l.Mine == false ? DislikeFill : CardC, l.Mine == false ? new Color32(169, 59, 45, 128) : Rule);
            if (!l.Yours) Clickable(down, () => LineReact?.Invoke(lid, false), radius: 14);
            _pops.Add((down, () => Popover("Disliked by", l.Dislikers, l.Dislikers.Count == 0 ? "No one yet" : l.Dislikers.Count > 3 ? $"and {l.Dislikers.Count - 3} more" : "", 200)));
            if (l.New > 0) Label(card, $"+{l.New} new", 13, new Color32(178, 70, 47, 255), pad + up.rect.width + down.rect.width + 16, cy + 6, 70, 17, bold: true, wrap: false);
            if (!opened)
            {
                var cm = Pill(card, 0, cy, Sprites.ChatIcon, l.Comments.ToString(), Ink2, CardC, Rule);
                cm.anchoredPosition = new Vector2(w - pad - cm.rect.width, -cy);
            }
            cy += 28;
        }
        if (l.Yours && l.SeenCount > 0 && !l.Waiting)
        {
            string names = string.Join(", ", l.Seen.Take(2).Select(s => s.Name));
            string seen = l.SeenCount > 2 ? $"Seen by {names} and {l.SeenCount - 2} more" : $"Seen by {names}";
            var sl = Label(card, seen, 13, Ink3, pad, cy + 6, inner, 17, wrap: false);
            FitLine(sl, seen, inner);
            var trigger = Box(card, "SeenBy", pad, cy + 6, Mathf.Min(inner, Wide(sl) + 2), 17);
            _pops.Add((trigger, () => Popover("Seen by", l.Seen, l.SeenCount > l.Seen.Count ? $"and {l.SeenCount - l.Seen.Count} more" : "", 200)));
            cy += 6 + 17;
        }
        float h = cy + 10;
        card.sizeDelta = new Vector2(w, h);
        if (!opened) Clickable(card, () => Go("line", l.Id), radius: 12, ring: false);
        if (l.Yours) _mineRects.Add((card, l.Id, ""));
        return y + h;
    }

    private static Sprite UpIcon => Sprites.ChevronUp;
    private static Sprite DownIcon => Sprites.ChevronDown;

    private RectTransform Pill(RectTransform parent, float x, float y, Sprite icon, string count, Color32 ink, Color32 fill, Color32 edge)
    {
        var tmp = Label(parent, count, 13, ink, 0, 0, 60, 17, bold: true, wrap: false);
        float cw = Wide(tmp);
        UnityEngine.Object.Destroy(tmp.gameObject);
        float pw = 10 + 15 + 5 + cw + 10;
        var p = Box(parent, "Pill", x, y, pw, 28);
        Img(p, Sprites.Round(14, 1, fill, edge));
        Icon(p, icon, 10, 6.5f, 15, ink);
        Label(p, count, 13, ink, 30, 5, cw + 2, 17, bold: true, wrap: false);
        return p;
    }

    // ------------------------------------------------------------------ a line opened

    private void RenderLine()
    {
        Solid(_area!, 0, 0, ScreenW, AreaH, Paper);
        var l = View.LineOpen;
        if (l == null) { LightBar("nightline", "", back: true); EmptyLight(_area!, AreaH - BarH, "Gone", "This line isn't there any more."); return; }
        LightBar($"{(l.Yours ? "Your" : FirstWord(l.Name) + "'s")} line", l.Comments == 0 ? "" : l.Comments == 1 ? "1 comment" : $"{l.Comments} comments", back: true);
        float composeH = View.ReplyTo.Length > 0 ? 54 + 22 : 54; // only this screen draws the replying bar
        var list = BeginScroll(BarH, AreaH - BarH - composeH);
        float y = Card(list, 0, l, opened: true);
        Solid(list, 0, y, ScreenW, 1.5f, Rule);
        if (View.LineComments.Count == 0)
        {
            Label(list, "No comments yet", 16, Ink, 0, y + 24, ScreenW, 22, TextAlignmentOptions.Top, bold: true, wrap: false);
            Label(list, "Say something.", 14, Ink3, 0, y + 48, ScreenW, 20, TextAlignmentOptions.Top, wrap: false);
            y += 80;
        }
        else
        {
            var head = Label(list, "COMMENTS", 13, Ink3, G, y + 12, ScreenW - 2 * G, 16, bold: true, wrap: false);
            head.characterSpacing = 8;
            y += 34;
            float target = -1;
            foreach (var c in View.LineComments)
            {
                if (c.Id == View.ReplyTo) target = y;
                y = Comment(list, y, c);
            }
            if (target >= 0) ScrollIntoView(target, 60);
        }
        EndScroll(y + 8);
        LightCompose(composeH, $"Comment as @{View.YourHandle}…");
        Dim();
    }

    private float Comment(RectTransform list, float y, PhoneView.LineComment c)
    {
        float x = c.Reply ? G + 38 : G, av = 30, mx = x + av + 8, mw = ScreenW - G - mx;
        var row = Box(list, "Comment", 0, y, ScreenW, 10);
        var bg = Img(row, Sprites.Round(0.5f, 0, White, White), c.Id == View.ReplyTo ? new Color32(251, 246, 236, 255) : Clear);
        if (c.Id == View.ReplyTo) Solid(row, 0, 0, 3, 10, Accent);
        Avatar(row, x, 9, av, c.Name, c.Kind == "stranger" ? "known" : c.Kind == "you" ? "ava" : c.Kind, c.Kind == "you" ? null : c.Portrait);
        var ago = Label(row, c.Ago, 13, Ink3, ScreenW - G - 50, 10, 50, 17, TextAlignmentOptions.TopRight, wrap: false);
        float agoW = Mathf.Min(50, Wide(ago));
        var nm = Label(row, c.Name, 14, Ink, mx, 9, mw, 18, bold: true, wrap: false);
        float nw = Mathf.Min(Wide(nm), mw * 0.45f);
        FitLine(nm, c.Name, nw);
        float hx = mx + nw + 5;
        if (c.Up is bool up) { Icon(row, up ? Sprites.ChevronUp : Sprites.ChevronDown, hx, 11, 13, up ? LikeC : DislikeC); hx += 17; }
        var h = Label(row, "@" + c.Handle, 13, Ink3, hx, 10, ScreenW - G - agoW - 8 - hx, 17, wrap: false);
        FitLine(h, "@" + c.Handle, ScreenW - G - agoW - 8 - hx);
        var t = Label(row, c.Text, 14, Ink, mx, 29, mw, 20);
        float th = Tall(t, mw);
        t.rectTransform.sizeDelta = new Vector2(mw, th);
        float rh = 29 + th + 4;
        if (!c.Yours)
        {
            var rp = Box(row, "Reply", mx, rh, 46, 22);
            Label(rp, "Reply", 13, GoldText, 0, 2, 46, 18, bold: true, wrap: false);
            string id = c.Id, handle = c.Handle, quote = c.Text;
            Clickable(rp, () => { View.ReplyTo = id; View.ReplyHandle = handle; View.ReplyQuote = quote; Render(); _focusIn = 2; }, radius: 8);
            rh += 22;
        }
        rh += 6;
        row.sizeDelta = new Vector2(ScreenW, rh);
        bg.rectTransform.sizeDelta = new Vector2(ScreenW, rh);
        Solid(row, 0, rh - 1.5f, ScreenW, 1.5f, Rule);
        if (c.Yours && View.LineOpen != null) _mineRects.Add((row, View.LineOpen.Id, c.Id));
        return y + rh;
    }

    // ------------------------------------------------------------------ chrome, light

    private void LightBar(string title, string sub, bool back)
    {
        var bar = Box(_area!, "Bar", 0, 0, ScreenW, BarH);
        Solid(bar, 0, 0, ScreenW, BarH, CardC);
        Solid(bar, 0, BarH - 1.5f, ScreenW, 1.5f, Rule);
        var t = Label(bar, title, 18, Ink, 48, sub.Length > 0 ? 6 : 13, ScreenW - 96, 22, TextAlignmentOptions.Top, bold: true, wrap: false);
        FitLine(t, title, ScreenW - 96);
        if (sub.Length > 0) Label(bar, sub, 13, Ink3, 48, 28, ScreenW - 96, 17, TextAlignmentOptions.Top, wrap: false);
        if (back)
        {
            var b = Box(bar, "Back", 8, 8, 32, 32);
            Icon(b, Sprites.ChevronIcon, 6, 6, 20, Accent);
            Clickable(b, Back, radius: 16);
        }
    }

    private void LightCompose(float h, string placeholder)
    {
        var compose = Box(_area!, "Compose", 0, AreaH - h, ScreenW, h);
        Solid(compose, 0, 0, ScreenW, h, CardC);
        Solid(compose, 0, 0, ScreenW, 1.5f, Rule);
        float top = 0;
        if (View.ReplyTo.Length > 0 && ScreenName == "line")
        {
            var r = Box(compose, "Replying", G, 6, ScreenW - 2 * G, 18);
            var a = Label(r, "Replying to", 13, Ink2, 0, 0, 80, 18, wrap: false);
            float aw = Wide(a);
            var hd = Label(r, "@" + View.ReplyHandle, 13, GoldText, aw + 5, 0, (ScreenW - 2 * G) * 0.4f, 18, bold: true, wrap: false);
            FitLine(hd, "@" + View.ReplyHandle, (ScreenW - 2 * G) * 0.4f);
            float hx = aw + 5 + Mathf.Min(Wide(hd), (ScreenW - 2 * G) * 0.4f) + 6, qw = ScreenW - 2 * G - hx - 28;
            var q = Label(r, View.ReplyQuote, 13, Ink3, hx, 0, qw, 18, wrap: false);
            FitLine(q, View.ReplyQuote, qw);
            var x = Box(r, "Stop", ScreenW - 2 * G - 24, -5, 28, 28);
            Icon(x, Sprites.CrossIcon, 8, 8, 12, Ink3);
            Clickable(x, StopReplying, radius: 14);
            top = 22;
        }
        // FB-80: the photo going with your line, and the button that picks one (the main feed only: comments carry none).
        bool attaching = ScreenName == "nightline";
        if (attaching && View.LineAttach.Length > 0)
        {
            var chip = Box(compose, "Attached", G, top + 6, ScreenW - 2 * G, 38);
            Img(chip, Sprites.Round(10, 1, Paper, Rule));
            var th = Box(chip, "Thumb", 4, 4, 30, 30);
            Thumb(th, View.LineAttach, 30, 30, 6);
            Label(chip, "Your photo goes with this line", 13, Ink2, 42, 10, ScreenW - 2 * G - 82, 18, wrap: false);
            var x = Box(chip, "Remove", ScreenW - 2 * G - 34, 5, 28, 28);
            Icon(x, Sprites.CrossIcon, 8, 8, 12, Ink3);
            Clickable(x, () => Unattach?.Invoke(), radius: 14);
            top += 46;
        }
        var row = Box(compose, "Row", 0, top, ScreenW, 54);
        PlaceCompose(row);
        if (attaching && _composeRt != null)
        {
            _composeRt.anchoredPosition = new Vector2(G + 42, _composeRt.anchoredPosition.y);
            _composeRt.sizeDelta = new Vector2(_composeRt.sizeDelta.x - 42, _composeRt.sizeDelta.y);
            var pick = Box(row, "Photo", G, 9, 36, 36);
            Img(pick, Sprites.Round(18, 1.5f, CardC, Rule));
            Icon(pick, Sprites.ImageIcon, 8, 8, 20, Ink2);
            Clickable(pick, () => AttachPhoto?.Invoke(), radius: 18);
        }
        SkinCompose(light: true, placeholder);
    }

    private void StopReplying() { View.ReplyTo = View.ReplyHandle = View.ReplyQuote = ""; Render(); }

    /// <summary>The compose box in the light app's skin, or back to the phone's own.</summary>
    private void SkinCompose(bool light, string placeholder)
    {
        if (_composeRt == null || _compose == null) return;
        var bg = _composeRt.GetComponent<Image>();
        if (bg != null) bg.sprite = light ? Sprites.Round(18, 1.5f, new Color32(251, 248, 243, 255), Rule) : Sprites.Round(18, 1, InputFill, GoldDim);
        if (_compose.textComponent != null) _compose.textComponent.color = light ? Ink : TextC;
        if (_compose.placeholder is TextMeshProUGUI ph) { ph.text = placeholder; ph.color = light ? Ink3 : new Color32(125, 119, 110, 255); }
        _compose.caretColor = light ? Accent : NameC;
    }

    private void EmptyLight(RectTransform parent, float h, string title, string text)
    {
        var box = Box(parent, "Empty", 28, h / 2 - 80, ScreenW - 56, 160);
        Icon(box, Sprites.NightlineIcon, (ScreenW - 56) / 2 - 22, 0, 44, new Color32(185, 165, 127, 255));
        Label(box, title, 16, Ink, 0, 56, ScreenW - 56, 22, TextAlignmentOptions.Top, bold: true);
        Label(box, text, 14, Ink3, 0, 80, ScreenW - 56, 80, TextAlignmentOptions.Top);
    }

    /// <summary>The dim at night, over everything in the app (popovers and menus too: they're drawn under it as they open).</summary>
    private void Dim()
    {
        if (View.LineDim <= 0) return;
        var d = Solid(_area!, 0, 0, ScreenW, AreaH, new Color(0, 0, 0, View.LineDim));
        d.raycastTarget = false;
        _dim = d.rectTransform;
    }

    private RectTransform? _dim;

    // ------------------------------------------------------------------ popovers and the menu (a top layer)

    private RectTransform Popover(string title, List<PhoneView.Who> who, string more, float width)
    {
        var pop = Box(_area!, "Popover", 0, 0, width, 10);
        Img(pop, Sprites.Round(8, 0, PopC, PopC));
        float y = 8;
        if (title.Length > 0) { Label(pop, title, 13, new Color32(233, 201, 142, 255), 10, y, width - 20, 17, bold: true, wrap: false); y += 20; }
        foreach (var p in who.Take(3))
        {
            Avatar(pop, 10, y + 1, 16, p.Name, p.Kind == "stranger" ? "known" : p.Kind == "you" ? "ava" : p.Kind, p.Kind == "you" ? null : p.Portrait);
            var n = Label(pop, p.Name, 13, new Color32(243, 238, 230, 255), 33, y, width - 43, 17, wrap: false);
            FitLine(n, p.Name, width - 43);
            y += 20;
        }
        if (more.Length > 0)
        {
            // FB-78: a short line ("No one yet") is one line; a measured one is held to a sane height (a new label can
            // measure far too tall, and the popover was pushed down to the screen's bottom).
            bool shortLine = more.Length <= 32;
            var m = Label(pop, more, 13, new Color32(189, 180, 168, 255), 10, y, width - 20, 17, wrap: !shortLine);
            float mh = shortLine ? 17 : Mathf.Clamp(Tall(m, width - 20), 17, 17 * 8);
            m.rectTransform.sizeDelta = new Vector2(width - 20, mh);
            y += mh + 2;
        }
        pop.sizeDelta = new Vector2(width, y + 8);
        return pop;
    }

    /// <summary>Hover popovers and the right-click menu (Nightline only). True when the press was used here.</summary>
    private bool TickLineUi(Vector2 pos, bool click, bool rightClick)
    {
        if (!IsNightline || _area == null) return false;
        // The popover under the pointer: shown above its trigger (below when there's no room), inside the screen by 12.
        RectTransform? over = null;
        Func<RectTransform>? make = null;
        foreach (var (trigger, mk) in _pops)
        {
            if (trigger == null || !trigger.gameObject.activeInHierarchy) continue;
            if (Over(trigger, pos) && (_viewport == null || Over(_viewport, pos))) { over = trigger; make = mk; break; }
        }
        if (_popShown.trigger != over)
        {
            if (_popShown.pop != null) UnityEngine.Object.Destroy(_popShown.pop.gameObject);
            _popShown = (over, null);
            if (over != null && make != null && _lineMenu == null)
            {
                var pop = make();
                Place(pop, over);
                if (_dim != null) _dim.SetAsLastSibling();
                _popShown = (over, pop);
            }
        }
        // Right-click your own line or comment: Copy text / Delete (any other press closes it).
        if (rightClick && _viewport != null && Over(_viewport, pos))
        {
            var hit = _mineRects.LastOrDefault(m => m.rt != null && Over(m.rt, pos));
            if (hit.rt != null) { OpenLineMenu(hit.line, hit.comment, pos); return true; }
        }
        if ((click || rightClick) && _lineMenu != null)
        {
            bool inside = Over(_lineMenu, pos);
            if (!inside) { UnityEngine.Object.Destroy(_lineMenu.gameObject); _lineMenu = null; return rightClick; }
        }
        return false;
    }

    /// <summary>A floating layer next to its trigger: above it, or below when it wouldn't fit; x held 12 from the screen's edges.</summary>
    private void Place(RectTransform pop, RectTransform trigger)
    {
        var corners = new Vector3[4];
        trigger.GetWorldCorners(corners);
        Vector2 tl = _area!.InverseTransformPoint(corners[1]), br = _area.InverseTransformPoint(corners[3]);
        float top = -tl.y, bottom = -br.y, cx = (tl.x + br.x) / 2f;
        float w = pop.rect.width, h = pop.rect.height;
        float y = top - 8 - h;
        if (y < BarH + 4) y = bottom + 8;
        if (y + h > AreaH - 4) y = Mathf.Max(BarH + 4, AreaH - 4 - h);
        float x = Mathf.Clamp(cx - 22, G, ScreenW - G - w);
        pop.anchoredPosition = new Vector2(x, -y);
        // Its tail over the trigger's centre.
        var tail = Box(pop, "Tail", Mathf.Clamp(cx - x - 5, 8, w - 18), y < top ? h - 5 : -5, 10, 10);
        Img(tail, Sprites.Round(1, 0, PopC, PopC));
        tail.localRotation = Quaternion.Euler(0, 0, 45);
        tail.SetAsFirstSibling();
    }

    private void OpenLineMenu(string line, string comment, Vector2 screenPos)
    {
        if (_lineMenu != null) UnityEngine.Object.Destroy(_lineMenu.gameObject);
        RectTransformUtility.ScreenPointToLocalPointInRectangle(_area!, screenPos, null, out var local);
        const float w = 168, h = 72;
        float x = Mathf.Clamp(local.x + 4, G, ScreenW - G - w), y = Mathf.Clamp(-local.y + 4, BarH + 4, AreaH - 8 - h);
        var menu = Box(_area!, "LineMenu", x, y, w, h);
        Img(menu, Sprites.Round(10, 1.5f, CardC, Rule));
        string text = comment.Length > 0 ? View.LineComments.FirstOrDefault(c => c.Id == comment)?.Text ?? "" : View.Lines.Concat(View.LineOpen != null ? new[] { View.LineOpen } : Array.Empty<PhoneView.LineCard>()).FirstOrDefault(l => l.Id == line)?.Text ?? "";
        MenuRow(menu, 4, "Copy text", Ink, () => { GUIUtility.systemCopyBuffer = text; CloseLineMenu(); });
        MenuRow(menu, 36, comment.Length > 0 ? "Delete comment" : "Delete line", DislikeC, () =>
        {
            CloseLineMenu();
            if (comment.Length > 0) LineDeleteComment?.Invoke(line, comment); else LineDelete?.Invoke(line);
        });
        if (_dim != null) _dim.SetAsLastSibling();
        _lineMenu = menu;
    }

    private void MenuRow(RectTransform menu, float y, string text, Color32 ink, Action act)
    {
        var r = Box(menu, "Item", 4, y, 160, 32);
        var bg = Img(r, Sprites.Round(8, 0, White, White), Clear);
        Label(r, text, 14, ink, 12, 7, 140, 18, bold: true, wrap: false);
        var hit = Clickable(r, act, ring: false);
        hit.Tint = bg; hit.IdleColor = Clear; hit.HoverColor = new Color32(243, 233, 214, 255);
    }

    private void CloseLineMenu() { if (_lineMenu != null) UnityEngine.Object.Destroy(_lineMenu.gameObject); _lineMenu = null; }

    /// <summary>Sending from the box on a Nightline screen: a line, or a comment (a reply when one's chosen).</summary>
    private bool SendLine(string text)
    {
        if (ScreenName == "nightline") { LinePost?.Invoke(text); _scrollPos = _scrollTarget = 0; return true; }
        if (ScreenName == "line" && View.LineOpen != null)
        {
            string to = View.ReplyTo;
            View.ReplyTo = View.ReplyHandle = View.ReplyQuote = "";
            LineComment?.Invoke(View.LineOpen.Id, text, to);
            return true;
        }
        return false;
    }

    private static string FirstWord(string n) => (n ?? "").Split(' ', 2)[0];

    // ------------------------------------------------------------------ photos (read off the main thread, shrunk, a small cache)

    private const int PhotosCached = 40, PhotoMaxW = 600;
    private readonly Dictionary<string, Texture2D> _photoTex = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _photoOrder = new();
    private readonly HashSet<string> _photoLoading = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string path, byte[] bytes)> _photoRead = new();

    /// <summary>The photo's texture if it's loaded (its use made it the newest); else it's read in the background and the screen redrawn.</summary>
    private Texture2D? PhotoTex(string path)
    {
        if (_photoTex.TryGetValue(path, out var t) && t != null) { _photoOrder.Remove(path); _photoOrder.Add(path); return t; }
        if (_photoLoading.Add(path))
            Task.Run(() =>
            {
                byte[]? b = null;
                try { var f = new FileInfo(path); if (f.Exists && f.Length <= 20 * 1024 * 1024) b = File.ReadAllBytes(path); } catch { }
                _photoRead.Enqueue((path, b ?? Array.Empty<byte>())); // an empty read lets the path go (review NL-11)
            });
        return null;
    }

    /// <summary>Every frame: at most one photo turned into a texture (shrunk to 600 wide), the oldest off screen let go past 40.</summary>
    private void TickPhotos()
    {
        if (!_photoRead.TryDequeue(out var item)) return;
        _photoLoading.Remove(item.path);
        if (item.bytes.Length == 0) return;
        Texture2D? tex = null;
        try
        {
            tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            if (!ImageConversion.LoadImage(tex, item.bytes, false)) { UnityEngine.Object.Destroy(tex); return; }
            if (tex.width > PhotoMaxW)
            {
                int w = PhotoMaxW, h = Mathf.Max(1, Mathf.RoundToInt(tex.height * (float)PhotoMaxW / tex.width));
                var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                var prev = RenderTexture.active;
                Texture2D? small = null;
                try
                {
                    Graphics.Blit(tex, rt);
                    RenderTexture.active = rt;
                    small = new Texture2D(w, h, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
                    small.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                    small.Apply(false, true);
                }
                finally { RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt); } // review NL-11
                UnityEngine.Object.Destroy(tex);
                tex = small;
            }
            _photoTex[item.path] = tex;
            _photoOrder.Add(item.path);
            // The 2026-10-20 audit: never one that's on screen (it would be asked for again at once: a read and redraw every
            // frame); past the cap only photos off screen go, so with more on screen the cache holds them all (60 at most).
            var onScreen = new HashSet<string>(View.Lines.Select(l => l.Photo).Append(View.LineOpen?.Photo).Append(View.LineAttach).Where(p => !string.IsNullOrEmpty(p))!, StringComparer.OrdinalIgnoreCase);
            if (IsGallery) foreach (var g in View.Gallery.Take(Math.Max(3, View.GalleryShown))) onScreen.Add(g.Path); // FB-80
            for (int i = 0; _photoOrder.Count > PhotosCached && i < _photoOrder.Count;)
            {
                var old = _photoOrder[i];
                if (onScreen.Contains(old)) { i++; continue; }
                _photoOrder.RemoveAt(i);
                if (_photoTex.Remove(old, out var o) && o != null) UnityEngine.Object.Destroy(o);
            }
            bool shown = onScreen.Contains(item.path);
            if (_visible && (IsNightline || IsGallery) && shown) Render(); // review NL-6: only a photo that's on screen redraws it
        }
        catch { if (tex != null) UnityEngine.Object.Destroy(tex); }
    }

    private void FreePhotos()
    {
        foreach (var t in _photoTex.Values) if (t != null) try { UnityEngine.Object.Destroy(t); } catch { }
        _photoTex.Clear();
        _photoOrder.Clear();
    }

    /// <summary>The texture's part that fills a box, cut from the top (where faces and skylines are) and the middle across.</summary>
    private static Rect Cover(int tw, int th, float bw, float bh)
    {
        if (tw <= 0 || th <= 0 || bw <= 0 || bh <= 0) return new Rect(0, 0, 1, 1);
        float ta = (float)tw / th, ba = bw / bh;
        if (ta > ba) { float u = ba / ta; return new Rect((1 - u) / 2, 0, u, 1); }
        float v = ta / ba;
        return new Rect(0, 1 - v - (1 - v) * 0.4f, 1, v); // keep the top 40% of the overflow
    }
}
