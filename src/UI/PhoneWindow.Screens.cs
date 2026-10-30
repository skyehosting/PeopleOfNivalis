using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisAIChat.UI;

/// <summary>The phone's screens, drawn from <see cref="PhoneWindow.View"/> (phone-6.html, screen by screen).</summary>
internal sealed partial class PhoneWindow
{
    private RectTransform? _viewport, _scrollContent, _bar, _thumb;
    private RawImage? _fadeTop, _fadeBottom;
    private float _scrollPos, _scrollTarget, _scrollMax;
    /// <summary>The texts on screen (right-click to delete one): their rect, place and words.</summary>
    private readonly List<(RectTransform rt, int index, string body)> _lineRects = new();
    private RectTransform? _menuFor, _menuMark;
    private (int index, string body) _menuLine;
    /// <summary>The card's "Clear conversation": asked once more before it happens.</summary>
    private string _clearAsk = "";
    private bool _bottomUp;
    private const float AreaH = ScreenH - StatusH;

    /// <summary>Draws the current screen again from <see cref="View"/> (typed text and the scroll place are kept).</summary>
    public void Render()
    {
        if (_area == null) return;
        RenderStatus();
        ParkInputs();
        for (int i = _area.childCount - 1; i >= 0; i--)
        {
            var child = _area.GetChild(i).gameObject;
            child.SetActive(false);
            UnityEngine.Object.Destroy(child);
        }
        _hits.Clear();
        _lineRects.Clear();
        _pops.Clear(); _mineRects.Clear(); _popShown = (null, null); _lineMenu = null; _dim = null; // FB-71
        _menuFor = null;
        _viewport = _scrollContent = _bar = _thumb = null;
        _fadeTop = _fadeBottom = null;
        _dots.Clear();
        _spinners.Clear();
        _noteBars.Clear();
        _wallImgs.Clear();
        _sendImg = null; _sendIcon = null; _setBtn = null;
        _flash = null; // FB-80
        _homeClock = _homeDate = null; // FB-83
        _scrollMax = 0;
        _bottomUp = false;
        try
        {
            switch (ScreenName)
            {
                case "contacts": RenderContacts(); break;
                case "contact": RenderContact(); break;
                case "messages": RenderMessages(); break;
                case "thread": RenderThread(); break;
                case "settings": RenderSettings(); break;
                case "weather": RenderWeather(); break;
                case "places": RenderPlaces(); break;
                case "news": RenderNews(); break;
                case "nightline": RenderNightline(); break; // FB-71
                case "line": RenderLine(); break;
                case "camera": RenderCamera(); break; // FB-80
                case "gallery": RenderGallery(); break;
                case "photo": RenderPhoto(); break;
                default: ScreenName = "home"; RenderHome(); break;
            }
        }
        catch (Exception e) { Log.Warn("The phone couldn't draw " + ScreenName + ": " + e.Message); }
        _scrollPos = Mathf.Clamp(_scrollPos, 0, _scrollMax);
        _scrollTarget = Mathf.Clamp(_scrollTarget, 0, _scrollMax);
        ApplyScroll();
        UpdateHint();
    }

    // ------------------------------------------------------------------ home

    private void RenderHome()
    {
        var wall = Box(_area!, "Wallpaper", 0, 0, ScreenW, AreaH);
        var raw = wall.gameObject.AddComponent<RawImage>();
        raw.raycastTarget = false;
        _wallImgs.Add((raw, ScreenW / AreaH));
        ShowWallTexture();
        if (HasCustomWall) Raw(_area!, Scrim, 0, 0, ScreenW, AreaH);
        var clock = Label(_area!, View.Clock, 56, new Color32(244, 239, 231, 255), 0, 34, ScreenW, 60, TextAlignmentOptions.Top, bold: true, wrap: false);
        clock.fontStyle = FontStyles.Normal;
        _homeClock = clock;
        _homeDate = Label(_area!, View.Date, 16, new Color32(227, 219, 207, 255), 0, 98, ScreenW, 22, TextAlignmentOptions.Top, wrap: false);
        // FB-74: today's weather under the date, an outline icon in the apps' gold with its words beside it, centred.
        if (View.SkyWords.Length > 0)
        {
            var sky = Label(_area!, View.SkyWords, 15, new Color32(227, 219, 207, 255), 0, 124, ScreenW, 20, TextAlignmentOptions.Left, wrap: false);
            float tw = Mathf.Min(sky.GetPreferredValues(View.SkyWords).x, ScreenW - 60), left = (ScreenW - (20 + 6 + tw)) / 2f;
            Icon(_area!, Sprites.WeatherIcon(View.Sky), left, 124, 20, NameC);
            sky.rectTransform.anchoredPosition = new Vector2(left + 26, -124);
            sky.rectTransform.sizeDelta = new Vector2(tw + 4, 20);
        }
        if (View.FirstTime)
        {
            var card = Box(_area!, "FirstTime", G, 154, ScreenW - 2 * G, 10);
            Img(card, Sprites.Round(10, 1, new Color32(22, 20, 18, 210), GoldDim));
            var t = Label(card, $"<color=#D9A55A><b>Your phone.</b></color> Ask people for their number when you talk to them; then text them from Messages. <color=#D9A55A><b>{Escape(View.Key)}</b></color> puts it away.",
                13, TextC, 12, 9, ScreenW - 2 * G - 24, 10, rich: true);
            float th = Tall(t, ScreenW - 2 * G - 24);
            t.rectTransform.sizeDelta = new Vector2(ScreenW - 2 * G - 24, th);
            card.sizeDelta = new Vector2(ScreenW - 2 * G, th + 18);
        }
        // The apps, centred, 24 above the bottom: four a row (FB-80, the author: "we should have 4 apps per row on the
        // phone"), filled from the top row.
        var apps = new System.Collections.Generic.List<(Sprite icon, string name, int badge, Action open)>
        {
            (Sprites.PersonIcon, "Contacts", 0, () => Go("contacts")),
            (Sprites.ChatIcon, "Messages", View.Unread, () => Go("messages")),
            (Sprites.NewsIcon, "News", 0, () => Go("news")),
        };
        if (View.NightlineOn) apps.Add((Sprites.NightlineIcon, "Nightline", View.LineUnseen, () => Go("nightline")));
        apps.Add((Sprites.CameraIcon, "Camera", 0, () => Go("camera")));
        apps.Add((Sprites.ImageIcon, "Gallery", 0, () => { View.GalleryPick = false; Go("gallery"); }));
        apps.Add((Sprites.CogIcon, "Settings", 0, () => Go("settings")));
        int rows = (apps.Count + 3) / 4;
        float x0 = (ScreenW - (4 * 64 + 3 * 8)) / 2f, y0 = AreaH - 24 - 78 - (rows - 1) * 90;
        for (int i = 0; i < apps.Count; i++)
            App(x0 + i % 4 * 72, y0 + i / 4 * 90, apps[i].icon, apps[i].name, apps[i].badge, apps[i].open);
    }

    private void App(float x, float y, Sprite icon, string name, int badge, Action open)
    {
        var app = Box(_area!, name, x, y, 64, 78);
        var ico = Box(app, "Icon", 4, 0, 56, 56);
        Img(ico, Sprites.Round(15, 1.5f, new Color32(48, 43, 37, 255), GoldDim));
        Icon(ico, icon, 14, 14, 28, NameC);
        if (badge > 0)
        {
            var b = Box(ico, "Badge", 56 - 22 + 7, -7, 22, 22);
            Img(b, Sprites.Round(11, 2, new Color32(217, 96, 79, 255), new Color32(27, 24, 21, 255)));
            Label(b, badge > 99 ? "99+" : badge.ToString(), 13, White, 0, 2, 22, 18, TextAlignmentOptions.Top, bold: true, wrap: false);
        }
        var label = Label(app, name, 13, new Color32(241, 236, 228, 255), 0, 62, 64, 16, TextAlignmentOptions.Top, wrap: false);
        label.fontStyle = FontStyles.Normal;
        Clickable(ico, open, radius: 16);
    }

    // ------------------------------------------------------------------ app chrome

    /// <summary>The title bar: [back] [title, its subtitle] [ ]; the body starts under it.</summary>
    private void AppBar(string title, string sub = "", bool back = false, Action? info = null)
    {
        var bar = Box(_area!, "Bar", 0, 0, ScreenW, BarH);
        Img(bar, Sprites.Round(0.5f, 0, White, White), Panel);
        Solid(bar, 0, BarH - 1, ScreenW, 1, LineC);
        var t = Label(bar, title.ToUpperInvariant(), 16, NameC, 48, sub.Length > 0 ? 6 : 14, ScreenW - 96, 20, TextAlignmentOptions.Top, bold: true, wrap: false);
        t.characterSpacing = 8;
        FitLine(t, title.ToUpperInvariant(), ScreenW - 96);
        if (sub.Length > 0) Label(bar, sub, 13, Muted, 48, 27, ScreenW - 96, 17, TextAlignmentOptions.Top, wrap: false);
        if (back)
        {
            var b = Box(bar, "Back", 8, 8, 32, 32);
            Icon(b, Sprites.ChevronIcon, 6, 6, 20, Gold);
            Clickable(b, Back, radius: 16);
        }
        if (info != null)
        {
            // The friend's suggestion: their card (who they are, how they feel about you), as the chat window's (i).
            var ib = Box(bar, "Info", ScreenW - 8 - 32, 8, 32, 32);
            Img(Box(ib, "Ring", 5, 5, 22, 22), Sprites.Round(11, 2, Clear, Gold));
            Label(ib, "i", 15, Gold, 0, 6, 32, 20, TextAlignmentOptions.Top, bold: true, wrap: false);
            Clickable(ib, info, radius: 16);
        }
    }

    /// <summary>A scrolled list under the bar: returns its content (top-left coordinates); <see cref="EndScroll"/> sets its height.</summary>
    private RectTransform BeginScroll(float y, float h, bool bottomUp = false)
    {
        _viewport = Box(_area!, "List", 0, y, ScreenW, h);
        _viewport.gameObject.AddComponent<RectMask2D>();
        var content = Box(_viewport, "Content", 0, 0, ScreenW, h);
        if (bottomUp)
        {
            content.anchorMin = content.anchorMax = new Vector2(0, 0);
            content.pivot = new Vector2(0, 0);
            content.anchoredPosition = Vector2.zero;
        }
        _bottomUp = bottomUp;
        _scrollContent = content;
        return content;
    }

    private void EndScroll(float total)
    {
        if (_viewport == null || _scrollContent == null) return;
        float h = _viewport.rect.height;
        _scrollContent.sizeDelta = new Vector2(ScreenW, Mathf.Max(total, _bottomUp ? total : h));
        _scrollMax = Mathf.Max(0, total - h);
        // The fades and the gold bar, over the list (shown from where it is scrolled: ApplyScroll).
        _fadeTop = Raw(_viewport, FadeDown, 0, 0, ScreenW, 28);
        _fadeBottom = Raw(_viewport, FadeUp, 0, h - 34, ScreenW, 34);
        _bar = Box(_viewport, "Bar", ScreenW - 7, 6, 4, h - 12);
        Img(_bar, Sprites.Round(2, 0, White, White), new Color32(118, 97, 62, 64));
        _thumb = Box(_bar, "Thumb", 0, 0, 4, 10);
        Img(_thumb, Sprites.Round(2, 0, White, White), Gold);
    }

    private void ApplyScroll()
    {
        if (_scrollContent == null || _viewport == null) return;
        _scrollContent.anchoredPosition = new Vector2(0, _bottomUp ? -_scrollPos : _scrollPos);
        bool scrolls = _scrollMax > 1;
        bool hiddenAbove = _bottomUp ? _scrollPos < _scrollMax - 1 : _scrollPos > 1;
        bool hiddenBelow = _bottomUp ? _scrollPos > 1 : _scrollPos < _scrollMax - 1;
        if (_fadeTop != null) _fadeTop.gameObject.SetActive(scrolls && hiddenAbove);
        if (_fadeBottom != null) _fadeBottom.gameObject.SetActive(scrolls && hiddenBelow);
        if (_bar != null && _thumb != null)
        {
            _bar.gameObject.SetActive(scrolls);
            float track = _bar.rect.height, view = _viewport.rect.height, total = view + _scrollMax;
            float th = Mathf.Max(24, track * view / Mathf.Max(1, total));
            float frac = _scrollMax > 0 ? _scrollPos / _scrollMax : 0;
            if (_bottomUp) frac = 1 - frac;
            _thumb.sizeDelta = new Vector2(4, th);
            _thumb.anchoredPosition = new Vector2(0, -(track - th) * frac);
        }
    }

    /// <summary>Scrolls so the content between y and y + h (top-left, in the list) is in view (a new Undo, say).</summary>
    private void ScrollIntoView(float y, float h)
    {
        if (_viewport == null || _bottomUp) return;
        float view = _viewport.rect.height;
        if (y + h > _scrollPos + view) _scrollPos = Mathf.Min(_scrollMax, y + h - view + 4);
        if (y < _scrollPos) _scrollPos = Mathf.Max(0, y - 4);
    }

    private static Color32 AvatarColour(string kind) => kind switch
    {
        "ava" => new Color32(120, 196, 222, 255),
        "partner" => new Color32(229, 154, 166, 255),
        "friend" => PillC,
        _ => Gold,
    };

    /// <summary>
    /// Their avatar: the game's own portrait of them in a round frame (the author: "We should use character portraits for
    /// their avatar in the users contacts"), cut to fill it from the top (where the face is); their initial when there's none.
    /// </summary>
    private void Avatar(RectTransform parent, float x, float y, float size, string name, string kind, Sprite? portrait = null)
    {
        var a = Box(parent, "Avatar", x, y, size, size);
        var disc = Img(a, Sprites.Round(size / 2, 0, White, White), AvatarColour(kind));
        if (portrait != null)
        {
            try
            {
                a.gameObject.AddComponent<Mask>().showMaskGraphic = true;
                float pw = portrait.rect.width, ph = portrait.rect.height;
                float w = size, h = size;
                if (pw > 0 && ph > 0) { if (ph / pw >= 1) h = size * ph / pw; else w = size * pw / ph; }
                var face = Box(a, "Portrait", (size - w) / 2, 0, w, h);
                var img = face.gameObject.AddComponent<Image>();
                img.sprite = portrait;
                img.type = Image.Type.Simple;
                img.raycastTarget = false;
                Img(Box(a, "Rim", 0, 0, size, size), Sprites.Round(size / 2, 2f, Clear, AvatarColour(kind)));
                return;
            }
            catch { /* the initial instead */ }
        }
        // No portrait: their initial on a neutral disc, in the same frame.
        disc.color = new Color32(58, 52, 45, 255);
        Img(Box(a, "Rim", 0, 0, size, size), Sprites.Round(size / 2, 2f, Clear, AvatarColour(kind)));
        string initial = name.Trim().Length > 0 ? char.ToUpperInvariant(name.Trim()[0]).ToString() : "?";
        Label(a, initial, size * 0.42f, AvatarColour(kind), 0, size * 0.22f, size, size * 0.6f, TextAlignmentOptions.Top, bold: true, wrap: false);
    }

    private float Section(RectTransform list, float y, string text)
    {
        var t = Label(list, text.ToUpperInvariant(), 13, Muted, G, y + 12, ScreenW - 2 * G, 16, TextAlignmentOptions.TopLeft, bold: true, wrap: false);
        t.characterSpacing = 8;
        return y + 12 + 16 + 4;
    }

    private void Empty(RectTransform parent, float top, Sprite icon, string title, string text)
    {
        float h = AreaH - top;
        var box = Box(parent, "Empty", 28, top + h / 2 - 80, ScreenW - 56, 160);
        Icon(box, icon, (ScreenW - 56) / 2 - 22, 0, 44, GoldDim);
        Label(box, title, 16, TextC, 0, 56, ScreenW - 56, 22, TextAlignmentOptions.Top, bold: true);
        Label(box, text, 14, Muted, 0, 80, ScreenW - 56, 80, TextAlignmentOptions.Top);
    }

    /// <summary>A list row: avatar, name (+ tag or time) on one line, a second line (+ unread dot).</summary>
    private float Row(RectTransform list, float y, string name, string kind, string tag, string when, string sub, bool unread, Action open, Sprite? portrait = null)
    {
        const float h = 60;
        float right = ScreenW - G - (_scrollMax > 0 || true ? 4 : 0);
        var row = Box(list, "Row", 0, y, ScreenW, h);
        var bg = Img(row, Sprites.Round(0.5f, 0, White, White), Clear);
        Avatar(row, G, 10, 38, name, kind, portrait);
        float mx = G + 38 + 11, mw = right - mx;
        float tagW = 0;
        if (tag.Length > 0)
        {
            var tg = Label(row, tag, 13, PillC, 0, 0, 100, 18, TextAlignmentOptions.Top, wrap: false);
            tagW = Wide(tg) + 16;
            UnityEngine.Object.Destroy(tg.gameObject);
            var pill = Box(row, "Tag", right - tagW, 11, tagW, 18);
            Img(pill, Sprites.Round(9, 1, PillFill, GoldDim));
            Label(pill, tag, 13, PillC, 0, 0, tagW, 18, TextAlignmentOptions.Center, wrap: false);
            tagW += 6;
        }
        float whenW = 0;
        if (when.Length > 0)
        {
            var w = Label(row, when, 13, Muted, 0, 13, 90, 17, TextAlignmentOptions.TopRight, wrap: false);
            whenW = Mathf.Min(90, Wide(w));
            w.rectTransform.anchoredPosition = new Vector2(right - 90, -13);
            whenW += 6;
        }
        var nm = Label(row, "", 16, TextC, mx, 10, mw - tagW - whenW, 21, TextAlignmentOptions.TopLeft, bold: true, wrap: false);
        FitLine(nm, name, mw - tagW - whenW);
        float dotW = unread ? 17 : 0;
        var s = Label(row, "", 14, unread ? TextC : Muted, mx, 33, mw - dotW, 18, TextAlignmentOptions.TopLeft, wrap: false);
        FitLine(s, sub, mw - dotW);
        if (unread)
        {
            s.fontStyle = FontStyles.Bold;
            Img(Box(row, "Unread", right - 9, 38, 9, 9), Sprites.Round(4.5f, 0, White, White), You);
        }
        Solid(row, 0, h - 1, ScreenW, 1, RowLine);
        var hit = Clickable(row, open, ring: false);
        hit.Tint = bg; hit.IdleColor = Clear; hit.HoverColor = Hover;
        return y + h;
    }

    // ------------------------------------------------------------------ contacts

    private void RenderContacts()
    {
        AppBar("Contacts");
        if (View.Contacts.Count == 0)
        {
            Empty(_area!, BarH, Sprites.PersonIcon, "No numbers yet", "Ask someone for their number when you're talking. If they like you enough, they'll give it.");
            return;
        }
        var list = BeginScroll(BarH, AreaH - BarH);
        float y = Section(list, 0, View.Contacts.Count == 1 ? "1 person" : $"{View.Contacts.Count} people");
        foreach (var c in View.Contacts)
        {
            string id = c.Id;
            y = Row(list, y, c.Name, c.Kind, c.Tag, "", c.Number, false, () => Go("contact", id), c.Portrait);
        }
        EndScroll(y);
    }

    private void RenderContact()
    {
        AppBar("Contact", back: true);
        var c = View.Contacts.FirstOrDefault(x => x.Id == Subject);
        if (c == null) { Empty(_area!, BarH, Sprites.PersonIcon, "Not in your contacts", "They're not in your phone any more."); return; }
        var body = BeginScroll(BarH, AreaH - BarH);
        Avatar(body, (ScreenW - 76) / 2, 20, 76, c.Name, c.Kind, c.Portrait);
        var nm = Label(body, "", 20, TextC, G, 106, ScreenW - 2 * G, 26, TextAlignmentOptions.Top, bold: true, wrap: false);
        FitLine(nm, c.Name, ScreenW - 2 * G);
        Label(body, c.Number, 16, BodyC, 0, 134, ScreenW, 20, TextAlignmentOptions.Top, wrap: false).characterSpacing = 4;
        int on = Mathf.Clamp(Mathf.RoundToInt((float)(c.Bond / 100.0 * 6)), 0, 6);
        float bx = (ScreenW - (6 * 16 + 5 * 3)) / 2f;
        for (int i = 0; i < 6; i++) Img(Box(body, "Bond", bx + i * 19, 164, 16, 8), Sprites.Round(2, 0, White, White), i < on ? PillC : new Color32(58, 53, 48, 255));
        string rel = (c.Tag.Length > 0 ? c.Tag : "Someone you know") + (c.TimesMet > 0 ? $" · met {c.TimesMet} time{(c.TimesMet == 1 ? "" : "s")}" : "");
        Label(body, rel, 14, PillC, 0, 178, ScreenW, 18, TextAlignmentOptions.Top, wrap: false);
        var btn = Box(body, "Message", (ScreenW - 130) / 2, 208, 130, 36);
        Img(btn, Sprites.Round(18, 1.5f, NameC, Gold));
        Icon(btn, Sprites.ChatIcon, 18, 10, 16, Dark);
        Label(btn, "Message", 15, Dark, 40, 8, 80, 20, TextAlignmentOptions.TopLeft, bold: true, wrap: false);
        string id = c.Id;
        Clickable(btn, () => Go("thread", id), radius: 18);
        Solid(body, G, 262, ScreenW - 2 * G, 1, LineC);
        float y = 262;
        // One style for every fact on the card: the label on the left, the value on the right.
        void Fact(string label, string value, bool known = true)
        {
            Label(body, label, 14, Muted, G, y + 5, 120, 18, wrap: false);
            var v = Label(body, "", 14, known ? TextC : new Color32(109, 104, 96, 255), G + 124, y + 5, ScreenW - 2 * G - 124, 18, TextAlignmentOptions.TopRight, wrap: false);
            FitLine(v, value, ScreenW - 2 * G - 124);
            y += 28;
            Solid(body, G, y - 1, ScreenW - 2 * G, 1, RowLine);
        }
        Fact($"Got {c.Their} number", c.Got);
        if (c.LastTexted.Length > 0) Fact("Last texted", c.LastTexted);
        y += 6;
        // Clear the conversation (to save space): the contact stays, and so does everything they remember.
        if (c.LastTexted.Length > 0 || _clearAsk == id)
        {
            if (_clearAsk == id)
            {
                var ask = Box(body, "ClearAsk", G, y + 4, ScreenW - 2 * G, 66);
                Img(ask, Sprites.Round(8, 1, new Color32(38, 35, 31, 255), LineC));
                Label(ask, $"Clear every text with {Prompts1(c.Name)}? {char.ToUpper(c.Their[0]) + c.Their[1..]} memories stay.", 13, TextC, 12, 7, ScreenW - 2 * G - 24, 20);
                var yes = Box(ask, "Yes", ScreenW - 2 * G - 12 - 70, 32, 70, 26);
                Img(yes, Sprites.Round(8, 0, new Color32(217, 96, 79, 255), new Color32(217, 96, 79, 255)));
                Label(yes, "Clear", 13, White, 0, 4, 70, 18, TextAlignmentOptions.Top, bold: true, wrap: false);
                Clickable(yes, () => { _clearAsk = ""; ClearThread?.Invoke(id); }, radius: 8);
                var no = Box(ask, "No", ScreenW - 2 * G - 12 - 70 - 8 - 70, 32, 70, 26);
                Img(no, Sprites.Round(8, 1, Clear, GoldDim));
                Label(no, "Cancel", 13, NameC, 0, 4, 70, 18, TextAlignmentOptions.Top, bold: true, wrap: false);
                Clickable(no, () => { _clearAsk = ""; Render(); }, radius: 8);
                y += 4 + 66 + 8;
            }
            else
            {
                var clear = Box(body, "Clear", G, y + 4, 170, 30);
                Img(clear, Sprites.Round(8, 1, Clear, new Color32(217, 96, 79, 160)));
                Label(clear, "Clear conversation", 13, Err, 0, 6, 170, 18, TextAlignmentOptions.Top, bold: true, wrap: false);
                Clickable(clear, () => { _clearAsk = id; Render(); }, radius: 8);
                y += 4 + 30 + 8;
            }
        }
        // About them: the chat window's info sheet (only what they told you; "?" where they haven't).
        if (c.About.Count > 0 || c.AboutSections.Count > 0)
        {
            Solid(body, G, y, ScreenW - 2 * G, 1, LineC);
            y = Section(body, y, c.Their == "her" ? "About her" : c.Their == "his" ? "About him" : "About them");
            foreach (var (label, value, known) in c.About.Where(r => r.known || r.label is "Age" or "Work" or "Lives in")) Fact(label, value, known);
            foreach (var (heading, all) in c.AboutSections)
            {
                // "Talked N times" is on the card already (met N times).
                var items = all.Where(x => !x.StartsWith("Talked ", StringComparison.Ordinal) && !x.StartsWith("First time you", StringComparison.Ordinal)).ToList();
                if (items.Count == 0) continue;
                y = Section(body, y, heading);
                foreach (var item in items.Take(8))
                {
                    var t = Label(body, "• " + item, 14, BodyC, G, y, ScreenW - 2 * G, 10);
                    float th = Tall(t, ScreenW - 2 * G);
                    t.rectTransform.sizeDelta = new Vector2(ScreenW - 2 * G, th);
                    y += th + 3;
                }
                y += 4;
            }
        }
        EndScroll(y + 12);
    }

    // ------------------------------------------------------------------ news

    private void RenderNews()
    {
        AppBar("News");
        if (View.News.Count == 0 && View.Talk.Count == 0)
        {
            Empty(_area!, BarH, Sprites.NewsIcon, "No news yet", "Headlines and talk around town show here as the city has them.");
            return;
        }
        var list = BeginScroll(BarH, AreaH - BarH);
        float y = 0, w = ScreenW - 2 * G - 4;
        float Item(string when, string title, string summary, bool rumor, string where)
        {
            float top = y + 10;
            // One order for both: the when (or where) line above, then what it says.
            Label(list, rumor ? (where.Length > 0 ? "Heard at " + where : "Heard around town") : when, 13, Muted, G, top, w, 17, wrap: false);
            top += 18;
            var hl = Label(list, title, 15, TextC, G, top, w, 10, bold: !rumor);
            if (rumor) hl.fontStyle = FontStyles.Italic;
            float hh = Tall(hl, w);
            hl.rectTransform.sizeDelta = new Vector2(w, hh);
            top += hh + 2;
            string second = rumor ? "" : summary;
            if (second.Length > 0)
            {
                var sm = Label(list, second, rumor ? 13 : 14, rumor ? Muted : BodyC, G, top, w, 10);
                float sh = Tall(sm, w);
                sm.rectTransform.sizeDelta = new Vector2(w, sh);
                top += sh;
            }
            top += 10;
            Solid(list, 0, top - 1, ScreenW, 1, RowLine);
            return top;
        }
        if (View.News.Count > 0)
        {
            y = Section(list, y, "On the holos");
            foreach (var (when, title, summary) in View.News) y = Item(when, title, summary, false, "");
        }
        if (View.Talk.Count > 0)
        {
            y = Section(list, y, "Talk around town");
            foreach (var (text, where) in View.Talk) y = Item("", text, "", true, where);
        }
        EndScroll(y + 8);
    }

    // ------------------------------------------------------------------ messages

    private void RenderMessages()
    {
        AppBar("Messages");
        if (View.Conversations.Count == 0)
        {
            Empty(_area!, BarH, Sprites.ChatIcon, "No messages", "Open a contact and click Message. Friends text when they think of you.");
            return;
        }
        var list = BeginScroll(BarH, AreaH - BarH);
        float y = Section(list, 0, View.Conversations.Count == 1 ? "1 conversation" : $"{View.Conversations.Count} conversations");
        foreach (var c in View.Conversations)
        {
            string id = c.Id;
            y = Row(list, y, c.Name, c.Kind, "", c.When, c.Preview, c.Unread, () => Go("thread", id), c.Portrait);
        }
        EndScroll(y);
    }

    private readonly List<Image> _dots = new();

    private void RenderThread()
    {
        string threadId = View.ThreadId;
        AppBar(View.ThreadName, View.ThreadNumber, back: true, info: View.Contacts.Any(c => c.Id == threadId) ? () => Go("contact", threadId) : null);
        float top = BarH;
        if (View.NoSignal.Length > 0)
        {
            var strip = Box(_area!, "NoSignal", G, BarH + 8, ScreenW - 2 * G, 10);
            Img(strip, Sprites.Round(10, 1, new Color32(217, 96, 79, 41), new Color32(217, 96, 79, 140)));
            Icon(strip, Sprites.NoSignalIcon, 10, 8, 16, new Color32(240, 184, 174, 255));
            var t = Label(strip, View.NoSignal, 13, new Color32(244, 195, 186, 255), 34, 7, ScreenW - 2 * G - 44, 10);
            float th = Tall(t, ScreenW - 2 * G - 44);
            t.rectTransform.sizeDelta = new Vector2(ScreenW - 2 * G - 44, th);
            strip.sizeDelta = new Vector2(ScreenW - 2 * G, th + 14);
            top = BarH + 8 + th + 14;
        }
        const float composeH = 54;
        var list = BeginScroll(top, AreaH - top - composeH, bottomUp: true);
        // Laid out top to bottom, then the whole stack sits on the bottom of the list (newest at the bottom).
        float y = 8, maxW = (ScreenW - 2 * G) * 0.78f;
        if (View.Earlier > 0)
        {
            var more = Box(list, "Earlier", (ScreenW - 140) / 2, y, 140, 26);
            Img(more, Sprites.Round(13, 1, Clear, GoldDim));
            Label(more, $"Show earlier ({View.Earlier})", 13, NameC, 0, 4, 140, 18, TextAlignmentOptions.Top, bold: true, wrap: false);
            Clickable(more, () => ShowEarlier?.Invoke(), radius: 13);
            y += 26 + 8;
        }
        foreach (var l in View.Thread)
        {
            if (l.From == "stamp")
            {
                y += 6;
                Label(list, l.Text, 13, Muted, 0, y, ScreenW, 16, TextAlignmentOptions.Top, wrap: false).fontStyle = FontStyles.Normal;
                y += 16 + 2 + 5;
                continue;
            }
            bool yours = l.From == "you";
            var probe = Label(list, l.Text, 15, yours ? YouText : TextC, 0, 0, maxW - 24, 10);
            float tw = Mathf.Min(maxW - 24, Wide(probe)), th = Tall(probe, tw + 1);
            float bw = tw + 24, bh = th + 14, bx = yours ? ScreenW - G - bw : G;
            var bubble = Box(list, yours ? "Yours" : "Theirs", bx, y, bw, bh);
            bubble.SetAsFirstSibling();
            if (yours && l.Failed) Img(bubble, Sprites.Round(16, 1.5f, Clear, new Color32(217, 96, 79, 255)));
            else if (l.Story && !yours) Img(bubble, Sprites.Round(16, 1, Theirs, GoldDim)); // a story line: faintly outlined (the stamp says it too)
            else Img(bubble, Sprites.Round(16, 0, yours ? You : Theirs, yours ? You : Theirs));
            if (l.Index >= 0) _lineRects.Add((bubble, l.Index, l.Text));
            probe.rectTransform.SetParent(bubble, false);
            probe.rectTransform.anchoredPosition = new Vector2(12, -7);
            probe.rectTransform.sizeDelta = new Vector2(tw + 1, th);
            if (yours && l.Failed) probe.color = TextC;
            y += bh + 5;
            if (!yours && l.Voice.Length > 0) { VoiceBar(list, G, y - 3, l); y += 40 + 7; }
            if (l.Meta.Length > 0)
            {
                // The author: the circles "on the same line as delivered, under the message instead of the side so it
                // doesn't inset the message": [circle] Delivered, right under the text, the bubble at full width.
                var m = Label(list, l.Meta, 13, l.Failed ? Err : Muted, 0, y - 1, ScreenW - G - 4, 17, TextAlignmentOptions.TopRight, wrap: false);
                if (yours && l.Receipt.Length > 0) Receipt(list, ScreenW - G - 4 - Mathf.Min(ScreenW - 60, Wide(m)) - 5 - 16, y - 0.5f, l.Receipt);
                y += 17 + 2;
            }
        }
        if (View.Typing)
        {
            var b = Box(list, "Typing", G, y, 48, 28);
            Img(b, Sprites.Round(14, 0, Theirs, Theirs));
            for (int i = 0; i < 3; i++) _dots.Add(Img(Box(b, "Dot", 12 + i * 10, 11, 6, 6), Sprites.Round(3, 0, White, White), new Color32(189, 181, 169, 255)));
            y += 28 + 5;
        }
        if (View.NoAnswer.Length > 0)
        {
            var na = Label(list, View.NoAnswer, 13, Muted, 16, y + 4, ScreenW - 32, 10, TextAlignmentOptions.Top);
            float nh = Tall(na, ScreenW - 32);
            na.rectTransform.sizeDelta = new Vector2(ScreenW - 32, nh);
            y += nh + 6;
            if (View.CanRetry)
            {
                var tr = Box(list, "TryAgain", (ScreenW - 80) / 2, y, 80, 22);
                var tl = Label(tr, "<u>Try again</u>", 13, NameC, 0, 2, 80, 18, TextAlignmentOptions.Top, bold: true, rich: true, wrap: false);
                string id = View.ThreadId;
                var hit = Clickable(tr, () => Retry?.Invoke(id), ring: false);
                y += 24;
            }
        }
        y += 8;
        // Everything above was placed from the top of a list as tall as the stack: sit it on the bottom.
        EndScroll(y);
        if (View.Undo.Length > 0 && View.UndoScreen == "thread")
        {
            var strip = Box(_area!, "Undo", G, AreaH - composeH - 44, ScreenW - 2 * G, 36);
            Img(strip, Sprites.Round(8, 1, new Color32(38, 35, 31, 250), LineC));
            Label(strip, View.Undo, 14, TextC, 12, 8, ScreenW - 2 * G - 80, 20, wrap: false);
            var link = Box(strip, "UndoLink", ScreenW - 2 * G - 12 - 40, 8, 40, 20);
            Label(link, "Undo", 14, NameC, 0, 0, 40, 20, TextAlignmentOptions.TopRight, bold: true, wrap: false);
            Clickable(link, () => Undo?.Invoke(), ring: false);
        }
        var compose = Box(_area!, "Compose", 0, AreaH - composeH, ScreenW, composeH);
        Img(compose, Sprites.Round(0.5f, 0, White, White), Panel);
        Solid(compose, 0, 0, ScreenW, 1, LineC);
        PlaceCompose(compose);
    }

    private readonly List<RectTransform> _spinners = new();

    /// <summary>Right-click on a text: a small Delete by the cursor (only from your phone: they still remember it).</summary>
    private void OpenTextMenu(int index, string body, Vector2 screenPos)
    {
        CloseTextMenu();
        if (_area == null) return;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(_area, screenPos, null, out var local);
        float x = Mathf.Clamp(local.x + 4, 8, ScreenW - 8 - 150), y = Mathf.Clamp(-local.y + 4, 8, AreaH - 8 - 40);
        // The text it's for, marked while the menu is open.
        var target = _lineRects.FirstOrDefault(l => l.index == index && l.body == body).rt;
        if (target != null)
        {
            var mark = Box(target, "Marked", -2, -2, target.rect.width + 4, target.rect.height + 4);
            Img(mark, Sprites.Round(18, 2, Clear, new Color32(217, 96, 79, 200)));
            _menuMark = mark;
        }
        var menu = Box(_area, "TextMenu", x, y, 150, 40);
        Img(menu, Sprites.Round(10, 1, new Color32(29, 26, 23, 250), GoldDim));
        var del = Box(menu, "Delete", 4, 4, 142, 32);
        var bg = Img(del, Sprites.Round(8, 0, White, White), Clear);
        Label(del, "Delete message", 14, Err, 12, 7, 120, 18, wrap: false);
        string id = Subject;
        var h = Clickable(del, () => { CloseTextMenu(); DeleteText?.Invoke(id, index, body); }, ring: false);
        h.Tint = bg; h.IdleColor = Clear; h.HoverColor = new Color32(217, 96, 79, 40);
        _menuFor = menu;
        _menuLine = (index, body);
    }

    private void CloseTextMenu()
    {
        if (_menuFor == null) return;
        var m = _menuFor;
        _menuFor = null;
        if (_menuMark != null) { try { UnityEngine.Object.Destroy(_menuMark.gameObject); } catch { } _menuMark = null; }
        _hits.RemoveAll(h => h.Rt != null && h.Rt.IsChildOf(m));
        try { m.gameObject.SetActive(false); UnityEngine.Object.Destroy(m.gameObject); } catch { }
    }

    private static string Prompts1(string name) => string.IsNullOrWhiteSpace(name) ? "them" : name.Trim().Split(' ')[0];

    /// <summary>
    /// FB-70: the circle beside your text, by its shape first (colour second): a turning ring while sending, a ring with a
    /// tick once sent, a filled tick when it reached them, their initial on the last they've seen, a red ring with ! waiting.
    /// </summary>
    private void Receipt(RectTransform list, float x, float y, string state, float size = 16)
    {
        var c = Box(list, "Receipt", x, y, size, size);
        c.localScale = Vector3.one; // drawn at 16, shown at size
        if (!Mathf.Approximately(size, 16)) { c.sizeDelta = new Vector2(16, 16); c.localScale = new Vector3(size / 16f, size / 16f, 1); }
        var grey = new Color32(154, 148, 139, 255);
        switch (state)
        {
            case "sending":
                var spin = Box(c, "Spin", 0, 0, 16, 16);
                spin.pivot = new Vector2(0.5f, 0.5f);
                spin.anchoredPosition = new Vector2(8, -8);
                var si = spin.gameObject.AddComponent<Image>();
                si.sprite = Sprites.ArcIcon; si.color = new Color32(125, 119, 110, 255); si.raycastTarget = false;
                _spinners.Add(spin);
                break;
            case "sent":
                Img(c, Sprites.Round(8, 1.5f, Clear, grey));
                Icon(c, Sprites.CheckIcon, 3.5f, 3.5f, 9, grey);
                break;
            case "delivered":
                Img(c, Sprites.Round(8, 0, grey, grey));
                Icon(c, Sprites.CheckIcon, 3.5f, 3.5f, 9, Deep);
                break;
            case "seen":
                Img(c, Sprites.Round(8, 0, PillC, PillC));
                Label(c, View.ThreadInitial, 11, Dark, 0, 1, 16, 14, TextAlignmentOptions.Top, bold: true, wrap: false);
                break;
            case "fail":
                Img(c, Sprites.Round(8, 1.5f, Clear, new Color32(217, 96, 79, 255)));
                Label(c, "!", 11, Err, 0, 1, 16, 14, TextAlignmentOptions.Top, bold: true, wrap: false);
                break;
        }
    }

    private void TickTyping()
    {
        if (_spinners.Count > 0)
        {
            float a = -Time.unscaledTime * 300f;
            foreach (var sp in _spinners) if (sp != null) sp.localRotation = Quaternion.Euler(0, 0, a);
        }
        if (_dots.Count == 0) return;
        float t = Time.unscaledTime * 3f;
        for (int i = 0; i < _dots.Count; i++)
        {
            float a = 0.35f + 0.65f * Mathf.Clamp01(0.5f + 0.5f * Mathf.Sin(t - i * 0.9f));
            if (_dots[i] != null) _dots[i].color = new Color(0.74f, 0.71f, 0.66f, a);
        }
    }
}
