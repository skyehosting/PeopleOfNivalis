using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisAIChat.UI;

/// <summary>
/// FB-80 (the author: "add two new apps, galary ... it creates a little camera display on the phone by using the players
/// display (minus the UI) and replicates it onto the phone, but expands it so that it takes up the top and bottom of the
/// screen"): the Camera (the live view filling the phone top to bottom, its sides cut; the shutter; your newest photo) and
/// the Gallery (your photos, three a row; one big, with Post and Delete; or picked for a Nightline line).
/// </summary>
internal sealed partial class PhoneWindow
{
    public Action? TakePhoto, AttachPhoto, Unattach, MoreGallery;
    public Action<string>? PickPhoto, DeletePhoto, PostPhoto;

    private Image? _flash;
    private float _flashT;
    private string _deleteAsk = "";
    private const float FlashFor = 0.35f;

    /// <summary>Review F1: the mouse is over the phone (a click there is the phone's, not the shutter key).</summary>
    public bool PointerOnPhone
    {
        get
        {
            try { var m = UnityEngine.InputSystem.Mouse.current; return _visible && _phone != null && m != null && Over(_phone, m.position.ReadValue()); }
            catch { return false; }
        }
    }

    /// <summary>The Camera is on screen (the runtime lets you look around and takes the shutter keys).</summary>
    public bool IsCamera => _visible && ScreenName == "camera";

    /// <summary>The white flash of a photo taken, and the shutter's click (made in the mod: no sound files).</summary>
    public void Flash()
    {
        _flashT = FlashFor;
        if (_root == null) return;
        try
        {
            if (_shutter == null)
            {
                const int rate = 44100;
                int n = (int)(rate * 0.16f);
                var data = new float[n];
                var rng = new System.Random(7);
                for (int i = 0; i < n; i++)
                {
                    float t = i / (float)rate;
                    float click = (t < 0.03f ? Mathf.Exp(-t * 160f) : 0f) + (t >= 0.07f ? 0.7f * Mathf.Exp(-(t - 0.07f) * 120f) : 0f);
                    data[i] = 0.5f * click * (float)(rng.NextDouble() * 2 - 1);
                }
                _shutter = AudioClip.Create("PeopleOfNivalis.Shutter", n, 1, rate, false);
                _shutter.SetData(data, 0);
            }
            if (_chimeSource == null)
            {
                _chimeSource = _root.AddComponent<AudioSource>();
                _chimeSource.spatialBlend = 0f;
                _chimeSource.ignoreListenerPause = true;
                _chimeSource.playOnAwake = false;
            }
            _chimeSource.PlayOneShot(_shutter, 0.6f);
        }
        catch (Exception e) { Log.Debug("phone shutter: " + e.Message); }
    }

    private AudioClip? _shutter;

    private bool IsGallery => ScreenName is "gallery" or "photo" or "camera";

    private void TickCamera(float dt)
    {
        if (_flash == null) return;
        if (_flashT > 0) _flashT = Mathf.Max(0, _flashT - dt);
        _flash.color = new Color(1, 1, 1, 0.85f * Mathf.Clamp01(_flashT / FlashFor));
    }

    private void RenderCamera()
    {
        Solid(_area!, 0, 0, ScreenW, AreaH, new Color32(0, 0, 0, 255));
        if (View.CameraFeed != null)
        {
            var raw = Box(_area!, "View", 0, 0, ScreenW, AreaH).gameObject.AddComponent<RawImage>();
            raw.texture = View.CameraFeed;
            raw.raycastTarget = false;
            // Top to bottom, the sides cut off (the phone stands upright); the photo itself is the whole screen.
            float show = Mathf.Clamp01(ScreenW / AreaH / Mathf.Max(0.1f, View.CameraAspect));
            raw.uvRect = new Rect((1 - show) / 2f, 0, show, 1);
        }
        else Label(_area!, "The camera can't see right now.", 14, TextC, G, AreaH / 2 - 10, ScreenW - 2 * G, 20, TextAlignmentOptions.Top, wrap: false);

        var top = Box(_area!, "Hint", G, 10, ScreenW - 2 * G, 40);
        Img(top, Sprites.Round(10, 0, new Color32(0, 0, 0, 140), new Color32(0, 0, 0, 140)));
        var hint = Label(top, $"Walk and look around to aim. {View.CameraKey} or A takes a photo; {View.CameraBack} or Esc goes back.", 12, TextC, 10, 5, ScreenW - 2 * G - 20, 30, TextAlignmentOptions.Top);
        hint.enableWordWrapping = true;
        if (View.CameraNote.Length > 0)
        {
            var note = Box(_area!, "Note", G, AreaH - 130, ScreenW - 2 * G, 28);
            Img(note, Sprites.Round(9, 0, new Color32(0, 0, 0, 150), new Color32(0, 0, 0, 150)));
            var n = Label(note, View.CameraNote, 13, TextC, 8, 5, ScreenW - 2 * G - 16, 18, TextAlignmentOptions.Top, wrap: false);
            FitLine(n, View.CameraNote, ScreenW - 2 * G - 16);
        }

        float by = AreaH - 92;
        Solid(_area!, 0, by, ScreenW, 92, new Color32(0, 0, 0, 120));
        var shutter = Box(_area!, "Shutter", ScreenW / 2f - 32, by + 14, 64, 64);
        Img(shutter, Sprites.Round(32, 4, new Color32(0, 0, 0, 0), White));
        Img(Box(shutter, "Disc", 8, 8, 48, 48), Sprites.Round(24, 0, White, White));
        Clickable(shutter, () => TakePhoto?.Invoke(), radius: 32);
        if (View.Gallery.Count > 0)
        {
            var last = Box(_area!, "Last", G + 6, by + 22, 48, 48);
            Thumb(last, View.Gallery[0].Path, 48, 48, 9);
            Clickable(last, () => Go("gallery"), radius: 10);
        }
        _flash = Solid(_area!, 0, 0, ScreenW, AreaH, new Color(1, 1, 1, 0));
        _flash.raycastTarget = false;
    }

    private void RenderGallery()
    {
        string sub = View.GalleryPick ? "Pick a photo for your line" : View.Gallery.Count == 1 ? "1 photo" : $"{View.Gallery.Count} photos";
        AppBar("Gallery", sub, back: true);
        if (View.Gallery.Count == 0)
        {
            Empty(_area!, BarH, Sprites.ImageIcon, "No photos yet", "Take some with the Camera: they're kept on your PC, in your Pictures folder.");
            return;
        }
        var list = BeginScroll(BarH, AreaH - BarH);
        const float gap = 6;
        float cell = (ScreenW - 2 * G - 2 * gap) / 3f, y = 8;
        int shown = Math.Min(View.Gallery.Count, Math.Max(3, View.GalleryShown));
        for (int i = 0; i < shown; i++)
        {
            var item = View.Gallery[i];
            float x = G + i % 3 * (cell + gap);
            if (i > 0 && i % 3 == 0) y += cell + gap;
            var t = Box(list, "Photo", x, y, cell, cell);
            Thumb(t, item.Path, cell, cell, 8);
            string file = item.File;
            Clickable(t, () => { if (View.GalleryPick) PickPhoto?.Invoke(file); else Go("photo", file); }, radius: 8);
        }
        y += cell + 12;
        if (shown < View.Gallery.Count)
        {
            var more = Box(list, "More", G, y, ScreenW - 2 * G, 34);
            Img(more, Sprites.Round(10, 1.5f, new Color32(48, 43, 37, 255), GoldDim));
            Label(more, "Show more", 14, NameC, 0, 8, ScreenW - 2 * G, 18, TextAlignmentOptions.Top, bold: true, wrap: false);
            Clickable(more, () => MoreGallery?.Invoke(), radius: 10);
            y += 46;
        }
        EndScroll(y);
    }

    private void RenderPhoto()
    {
        var item = View.Gallery.FirstOrDefault(g => g.File == Subject);
        AppBar("Photo", item == null ? "" : item.When, back: true);
        if (item == null) { Empty(_area!, BarH, Sprites.ImageIcon, "That photo's gone", "It isn't in your gallery folder any more."); return; }
        float w = ScreenW - 2 * G, y = BarH + 12;
        var tex = PhotoTex(item.Path);
        float h = tex != null && tex.width > 0 ? Mathf.Min(w * tex.height / tex.width, AreaH - BarH - 150) : w * 9f / 16f;
        var frame = Box(_area!, "Frame", G, y, w, h);
        Img(frame, Sprites.Round(10, 0, new Color32(40, 37, 33, 255), new Color32(40, 37, 33, 255)));
        frame.gameObject.AddComponent<Mask>().showMaskGraphic = true;
        if (tex != null) { var raw = Raw(frame, tex, 0, 0, w, h); raw.uvRect = Cover(tex.width, tex.height, w, h); }
        y += h + 10;
        string where = item.Where.Length > 0 ? item.Where : item.Ours ? "" : "Put in the gallery folder";
        if (where.Length > 0) { var wl = Label(_area!, where, 13, Muted, G, y, w, 17, wrap: false); FitLine(wl, where, w); y += 20; }
        if (item.Child || !item.Ours)
        {
            var nl = Label(_area!, item.Ours ? "Someone under 18 is in it: it's never shown to anyone's AI." : "Not taken with the Camera: never shown to anyone's AI.", 12, Muted, G, y, w, 32);
            nl.enableWordWrapping = true;
            y += 36;
        }
        float bw = (w - 8) / 2f, by = AreaH - 56;
        string file = item.File;
        if (View.NightlineOn)
        {
            var post = Box(_area!, "Post", G, by, bw, 40);
            Img(post, Sprites.Round(12, 0, NameC, NameC));
            Label(post, "Post on Nightline", 14, Dark, 0, 10, bw, 20, TextAlignmentOptions.Top, bold: true, wrap: false);
            Clickable(post, () => PostPhoto?.Invoke(file), radius: 12);
        }
        var del = Box(_area!, "Delete", View.NightlineOn ? G + bw + 8 : G, by, View.NightlineOn ? bw : w, 40);
        bool asked = _deleteAsk == file;
        Img(del, Sprites.Round(12, 1.5f, asked ? Err : new Color32(48, 43, 37, 255), asked ? Err : GoldDim));
        Label(del, asked ? "Tap again to delete" : "Delete", 14, asked ? Dark : TextC, 0, 10, View.NightlineOn ? bw : w, 20, TextAlignmentOptions.Top, bold: true, wrap: false);
        Clickable(del, () =>
        {
            if (_deleteAsk != file) { _deleteAsk = file; Render(); return; }
            _deleteAsk = "";
            DeletePhoto?.Invoke(file);
        }, radius: 12);
    }

    /// <summary>A photo's thumbnail: the picture cover-cropped into a rounded box (a plain box until it's read in).</summary>
    private void Thumb(RectTransform box, string path, float w, float h, float radius)
    {
        Img(box, Sprites.Round(radius, 0, new Color32(48, 43, 37, 255), new Color32(48, 43, 37, 255)));
        box.gameObject.AddComponent<Mask>().showMaskGraphic = true;
        var tex = PhotoTex(path);
        if (tex == null) return;
        var raw = Raw(box, tex, 0, 0, w, h);
        raw.uvRect = Cover(tex.width, tex.height, w, h);
    }
}
