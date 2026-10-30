using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nivalis;
using NivalisAIChat.Game;
using NivalisAIChat.Phone;
using NivalisAIChat.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NivalisAIChat;

/// <summary>
/// FB-80 (the author: "add two new apps, galary (uses users system storage) it creates a little camera display on the
/// phone by using the players display (minus the UI) ... it will still capture the full screen and store it to gallery"):
/// the phone's Camera and Gallery at run time. In the Camera you look around (the phone lets go of the camera lock and the
/// cursor; you don't walk); click, Space or the pad's A takes your whole view without the HUD as a JPEG into the gallery
/// folder (Pictures\Nivalis Nights, or GalleryFolder). Whether anyone under 18 was in view is noted with it: such a photo is
/// never shown to an AI. A gallery photo can go with your next Nightline line.
/// </summary>
public static partial class ModRuntime
{
    private static Gallery? _gallery;
    /// <summary>The photo going with your next Nightline line (a path in the gallery folder), "" none.</summary>
    private static string _lineAttach = "";
    private static bool _cameraLive;
    private static float _nextShot, _cameraNoteUntil = -1f, _galleryLookAt = -100f;
    private static List<(string name, DateTime written)> _galleryFiles = new();
    private const int GalleryMore = 48;
    /// <summary>FB-82: a mouse button (0 left, 1 right, 2 middle, 3 Mouse 4, 4 Mouse 5) or a key.</summary>
    private readonly record struct CameraBinding(int Mouse, Key Key);
    private static readonly CameraBinding LeftClick = new(0, Key.None), RightClick = new(1, Key.None), MiddleClick = new(2, Key.None);
    private static CameraBinding _shutter = LeftClick, _cameraBack = RightClick;

    private static CameraBinding ParseBinding(string? name, CameraBinding fallback)
    {
        string n = (name ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
        switch (n)
        {
            case "leftclick" or "leftmouse" or "lmb" or "mouseleft": return LeftClick;
            case "rightclick" or "rightmouse" or "rmb" or "mouseright": return RightClick;
            case "middleclick" or "middlemouse" or "mmb" or "mousemiddle": return MiddleClick;
            case "mouse4" or "mouseback": return new(3, Key.None);
            case "mouse5" or "mouseforward": return new(4, Key.None);
        }
        string k = (name ?? "").Trim();
        if (k.Length == 1 && char.IsDigit(k[0])) k = "Digit" + k;
        if (!Enum.TryParse<Key>(k, true, out var key) || key == Key.None || !Enum.IsDefined(key))
        {
            if (k.Length > 0) Log.Warn($"Camera: '{k}' isn't a mouse button or a key the mod knows; using {BindingName(fallback, fallback == LeftClick)}."); // review F82-3
            return fallback;
        }
        // Never a key that already does something with the phone out (review F81-4): Esc, the phone, talk, the console.
        if (key == Key.Escape || key == _phoneKey || key == _talkKey || key == _consoleKey)
        {
            Log.Warn($"Camera: {key} already does something else (Esc, the phone, talk or the console); using {BindingName(fallback, fallback == LeftClick)}.");
            return fallback;
        }
        return new(-1, key);
    }

    private static string BindingName(CameraBinding b, bool shutter) => b.Mouse switch
    {
        0 => shutter ? "Click" : "Left-click", 1 => "Right-click", 2 => "Middle-click", 3 => "Mouse 4", 4 => "Mouse 5", _ => b.Key.ToString(),
    };

    private static bool BindingPressed(CameraBinding b, Mouse? m, Keyboard? kb) => b.Mouse switch
    {
        0 => m != null && m.leftButton.wasPressedThisFrame, 1 => m != null && m.rightButton.wasPressedThisFrame,
        2 => m != null && m.middleButton.wasPressedThisFrame, 3 => m != null && m.backButton.wasPressedThisFrame,
        4 => m != null && m.forwardButton.wasPressedThisFrame, _ => kb != null && b.Key != Key.None && kb[b.Key].wasPressedThisFrame,
    };

    /// <summary>FB-81/82: the Camera's shutter (CameraShutterKey; left click) and its way back (CameraBackKey; right click).</summary>
    private static void ParseCameraKey()
    {
        _shutter = ParseBinding(_cfg.CameraShutterKey.Value, LeftClick);
        _cameraBack = ParseBinding(_cfg.CameraBackKey.Value, RightClick);
        if (_cameraBack == _shutter)
        {
            _cameraBack = _shutter == RightClick ? LeftClick : RightClick; // never both on one button
            Log.Warn($"Camera: CameraShutterKey and CameraBackKey are the same; leaving is {BindingName(_cameraBack, false)}.");
        }
        PhoneUi.View.CameraKey = BindingName(_shutter, shutter: true);
        PhoneUi.View.CameraBack = BindingName(_cameraBack, shutter: false);
    }

    private static string GalleryFolderNow()
    {
        string set = (_cfg.GalleryFolder.Value ?? "").Trim();
        if (set.Length > 0) return set;
        try
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (pictures.Length > 0) return Path.Combine(pictures, "Nivalis Nights");
        }
        catch { }
        return Path.Combine(_dataDir, "gallery");
    }

    /// <summary>The gallery's notes (global: every save shares the folder), opened with the phone's window.</summary>
    private static Gallery? GalleryNow()
    {
        string folder = GalleryFolderNow();
        if (_gallery == null || !string.Equals(_gallery.Folder, folder, StringComparison.OrdinalIgnoreCase))
        {
            try { _gallery = new Gallery(Path.Combine(_dataDir, "gallery.json"), folder, Log.Warn); _galleryLookAt = -100f; }
            catch (Exception e) { Log.Warn("The gallery couldn't be opened: " + e.Message); _gallery = null; }
        }
        return _gallery;
    }

    private static void WireCamera()
    {
        PhoneUi.TakePhoto = TakePhoneShot;
        PhoneUi.MoreGallery = () => { PhoneUi.View.GalleryShown += GalleryMore; RefreshPhone(); };
        PhoneUi.AttachPhoto = () => { PhoneUi.View.GalleryPick = true; PhoneUi.Go("gallery"); };
        PhoneUi.PickPhoto = file =>
        {
            var g = GalleryNow();
            if (g == null || !Gallery.SafeName(file)) return;
            _lineAttach = Path.Combine(g.Folder, file);
            PhoneUi.View.GalleryPick = false;
            PhoneUi.Back(); // to the feed, the photo above the box
        };
        PhoneUi.Unattach = () => { _lineAttach = ""; RefreshPhone(); };
        PhoneUi.DeletePhoto = DeleteGalleryPhoto;
        PhoneUi.PostPhoto = file =>
        {
            var g = GalleryNow();
            if (g == null || !Gallery.SafeName(file) || !NightlineOn) return;
            _lineAttach = Path.Combine(g.Folder, file);
            PhoneUi.View.GalleryPick = false;
            PhoneUi.Home();
            PhoneUi.Go("nightline");
        };
    }

    /// <summary>Every frame while the phone is out: the Camera on or off with its screen, and its shutter keys.</summary>
    private static void TickCameraMode()
    {
        bool want = _phoneOpen && PhoneUi.IsCamera && Npc.Features.Phone;
        if (want != _cameraLive) { if (want) StartCameraMode(); else EndCameraMode(); }
        if (!_cameraLive) return;
        CameraView.Tick();
        if (_cameraNoteUntil > 0 && Time.unscaledTime >= _cameraNoteUntil) { _cameraNoteUntil = -1f; PhoneUi.View.CameraNote = ""; RefreshPhone(); }
        var mouse = Mouse.current;
        var kb = Keyboard.current;
        // Review F1: a click on the phone itself (its thumbnail; the shutter takes it on its own) isn't the shutter.
        bool shoot = (BindingPressed(_shutter, mouse, kb) && (_shutter.Mouse < 0 || !PhoneUi.PointerOnPhone)) || Pad.A;
        if (shoot) TakePhoneShot();
        // FB-82 (the author: "I should also be able to right click to exit"): CameraBackKey leaves the Camera, as Esc does.
        else if (BindingPressed(_cameraBack, mouse, kb) && (_cameraBack.Mouse != 0 || !PhoneUi.PointerOnPhone)) PhoneUi.Back(); // review F82-1
    }

    private static void StartCameraMode()
    {
        _cameraLive = true;
        if (!CameraView.Start(GameContext.PlayerCamera())) CameraNote("The camera can't see right now.");
        Locks.LookMode(true);
        RefreshPhone();
    }

    /// <summary>Out of the Camera (another screen, the phone put away, the mod stopping): the view let go, the camera locked again.</summary>
    private static void EndCameraMode()
    {
        _cameraLive = false;
        CameraView.Stop();
        try { Locks.LookMode(false); } catch { }
        PhoneUi.View.CameraFeed = null;
    }

    private static void CameraNote(string text)
    {
        PhoneUi.View.CameraNote = text;
        _cameraNoteUntil = Time.unscaledTime + 2.5f;
    }

    /// <summary>A photo: your whole view without the HUD, into the gallery folder; who's in view checked for children.</summary>
    private static void TakePhoneShot()
    {
        if (!_cameraLive || Time.unscaledTime < _nextShot) return;
        _nextShot = Time.unscaledTime + 0.5f;
        var g = GalleryNow();
        var cam = GameContext.PlayerCamera();
        if (g == null || cam == null) { CameraNote("The camera can't take a photo right now."); RefreshPhone(); return; }
        var (jpeg, error) = Game.Snapshot.TakeView(cam);
        if (jpeg == null) { CameraNote("Couldn't take it" + (error is { Length: > 0 } ? ": " + error : ".")); RefreshPhone(); return; }
        bool child = CameraView.ChildInView(cam, c => { string id = GameContext.NpcId(c); return KnownUnder18(id) || MinorInfo(c, id).minor; });
        int day = Math.Max(0, GameContext.Day());
        string clock = $"{Math.Max(0, GameContext.Hour()):00}:{Math.Max(0, GameContext.Minute()):00}";
        string place = _here.place;
        long minute = GameNow();
        string file = g.NewName(day, clock, DateTime.Now), folder = g.Folder, path = Path.Combine(folder, file);
        PhoneUi.Flash();
        CameraNote("Saved to your gallery.");
        RefreshPhone();
        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                Npc.DataFile.WriteAtomic(path, jpeg, durable: false);
                var written = new FileInfo(path);
                long size = written.Length;
                DateTime at = written.LastWriteTimeUtc;
                MainThread.Enqueue(() =>
                {
                    g.Add(file, day, clock, place, minute, child, size, at);
                    _galleryLookAt = -100f;
                    Log.Info($"Camera: a photo saved to your gallery ({file}{(child ? "; someone under 18 is in view, so it's never shown to an AI" : "")}).");
                    if (_phoneOpen) RefreshPhone();
                });
            }
            catch (Exception e) { MainThread.Enqueue(() => { CameraNote("Couldn't save it: " + e.Message); if (_phoneOpen) RefreshPhone(); }); }
        });
    }

    private static void DeleteGalleryPhoto(string file)
    {
        var g = GalleryNow();
        if (g == null || !Gallery.SafeName(file)) return;
        string path = Path.Combine(g.Folder, file);
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) { Log.Warn("The photo couldn't be deleted: " + e.Message); return; }
        g.Remove(file);
        if (string.Equals(_lineAttach, path, StringComparison.OrdinalIgnoreCase)) _lineAttach = "";
        _galleryLookAt = -100f;
        Log.Info($"Camera: {file} deleted from your gallery.");
        PhoneUi.Back();
    }

    /// <summary>The Camera's and the Gallery's part of the phone's view (the folder looked at again every few seconds).</summary>
    private static void FillGallery(PhoneView v)
    {
        v.CameraFeed = _cameraLive ? CameraView.Feed : null;
        v.CameraAspect = CameraView.Aspect;
        var g = GalleryNow();
        if (_lineAttach.Length > 0 && !File.Exists(_lineAttach)) _lineAttach = "";
        v.LineAttach = _lineAttach;
        bool needed = PhoneUi.ScreenName is "gallery" or "photo" or "camera";
        if (g == null || !needed) return;
        if (Time.unscaledTime - _galleryLookAt > 3f)
        {
            _galleryLookAt = Time.unscaledTime;
            try
            {
                _galleryFiles = Directory.Exists(g.Folder)
                    ? new DirectoryInfo(g.Folder).EnumerateFiles().Where(f => Gallery.SafeName(f.Name)).Take(5000).Select(f => (f.Name, f.LastWriteTimeUtc)).ToList()
                    : new List<(string, DateTime)>();
            }
            catch (Exception e) { Log.Debug("gallery folder: " + e.Message); _galleryFiles = new(); }
        }
        v.Gallery = g.List(_galleryFiles).Select(p => new PhoneView.GalleryItem
        {
            Path = Path.Combine(g.Folder, p.File), File = p.File, Where = p.Place, Child = p.Child, Ours = p.Ours,
            When = p.Ours ? $"Day {p.Day} · {TwelveHour(p.Clock)}" : "",
        }).ToList();
    }
}
