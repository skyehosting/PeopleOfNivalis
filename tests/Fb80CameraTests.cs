using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Phone;

namespace NivalisAIChat.Tests;

/// <summary>FB-80: the phone's Camera and Gallery, your photos on Nightline, four apps a row.</summary>
internal static partial class Program
{
    private static void Fb80CameraTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- FB-80: the Camera, the Gallery, your photos on Nightline");
        string dir = TestRoot.NewFolder("fb80");
        Directory.CreateDirectory(dir);
        string folder = Path.Combine(dir, "Pictures");
        var g = new Gallery(Path.Combine(dir, "gallery.json"), folder);
        var now = new DateTime(2026, 10, 20, 9, 16, 2);
        string a = g.NewName(15, "09:16", now);
        Check(a == "Nivalis day 15 0916 091602.jpg" && Gallery.SafeName(a) && !Gallery.SafeName("..\\x.jpg") && !Gallery.SafeName("x.exe") && !Gallery.SafeName(""),
              "a photo's name says the day and time; only plain picture names are ever used");
        g.Add(a, 15, "09:16", "Meridian Market", 15 * 1440 + 556, child: false, size: 1000, writtenUtc: now);
        string b = g.NewName(15, "09:16", now);
        Check(b != a && b.EndsWith(" 2.jpg"), "...two in the same second don't clash");
        g.Add(b, 15, "09:16", "Meridian Market", 15 * 1440 + 556, child: true, size: 1000, writtenUtc: now);
        var files = new List<(string, DateTime)> { (a, now), (b, now), ("my own.jpg", now.AddDays(-1)), ("notes.txt", now) };
        var list = g.List(files);
        Check(list.Select(p => p.File).SequenceEqual(new[] { b, a, "my own.jpg" }) && list[2].Ours == false,
              "newest first; a picture put in the folder some other way is shown too (not ours); anything else isn't");
        Check(g.MayShowAi(a, 1000, now) && !g.MayShowAi(b, 1000, now) && !g.MayShowAi("my own.jpg", 1000, now),
              "only a photo the Camera took with nobody under 18 in view may ever be shown to an AI");
        Check(!g.MayShowAi(a, 2000, now) && !g.MayShowAi(a, 1000, now.AddMinutes(5)),
              "review F2: a file put there later under the same name (another size or time) never is");
        Check(g.List(new List<(string, DateTime)> { (b, now) }).Count == 1, "a noted photo whose file is gone isn't listed");
        var again = new Gallery(Path.Combine(dir, "gallery.json"), folder);
        Check(again.MayShowAi(a, 1000, now) && !again.MayShowAi(b, 1000, now) && again.Get(a)?.Place == "Meridian Market", "the notes are kept across a restart (gallery.json)");
        Check(again.Remove(a) && !again.Remove(a) && again.Get(a) == null, "a deleted photo is forgotten");

        // Your photo on a Nightline line: words optional, flagged as yours, and whether an AI may see it.
        var book = new LineBook(Path.Combine(dir, "nightline.json"));
        string mine = book.Post(LineBook.You, "You", "", "Docks", 2000, 2, "9:00 AM", photoComing: true);
        Check(mine.Length > 0 && book.Post("p_mara", "Mara", "", "", 2001, 2, "9:01 AM", photoComing: true) == "" && book.Post(LineBook.You, "You", "  ", "", 2002, 2, "9:02 AM") == "",
              "a photo of yours may go up with no words (never anyone else's empty line, never an empty one of yours without a photo)");
        Check(book.MinePhoto(mine, "photo-you-x.jpg", noAi: true) && book.PhotoOf(book.Get(mine)!.Photo) is { Mine: true, NoAi: true },
              "your photo is kept as yours, with whether it may be shown to an AI");

        // What a resident is shown: the picture itself (one a look), with the rule; or told it's a photo they can't see.
        NpcRecord rec = new() { Id = "p_ida", Persona = new Persona { Name = "Ida Holm", Pronouns = "she/her", Age = "30", Occupation = "cook" }, Visual = new NpcVisual { Body = "woman" } };
        var seen = new List<Lines.Seen>
        {
            new() { Key = "L1", AuthorName = "Raine", Handle = "you", Text = "Sunset at the docks", YourPhoto = true, Image = new byte[] { 1, 2, 3 }, MayReact = true, MayComment = true },
            new() { Key = "L2", AuthorName = "Raine", Handle = "you", Text = "Again", YourPhoto = true, Image = new byte[] { 4 }, MayReact = true, MayComment = true },
        };
        var req = Lines.Request(rec, new GameSnapshot(), false, seen, false, new List<string>(), false, "", "", 50, "");
        var msg = req.Messages.Last();
        Check(msg.Images is { Count: 1 } && msg.Images[0].Bytes.SequenceEqual(new byte[] { 1, 2, 3 }) && msg.Content.Contains("it's the picture with this message")
              && msg.Content.Contains("never describe the picture or list what's in it") && msg.Content.Contains("react to the point"),
              "FB-80: one picture at most goes with the look, with the rule: a few human words, never a description; a point in the words, the point");
        foreach (var s in seen) s.Image = null;
        var blind = Lines.Request(rec, new GameSnapshot(), false, seen, false, new List<string>(), false, "", "", 50, "").Messages.Last();
        Check(blind.Images == null && blind.Content.Contains("With a photo they took (you can't see it from here): go by the words."),
              "...and with no picture (it can't be shown, or their AI can't see), they go by your words");

        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string Read(string f) => File.ReadAllText(Path.Combine(src, f));
        string home = Read(Path.Combine("UI", "PhoneWindow.Screens.cs")), cam = Read(Path.Combine("UI", "PhoneWindow.Camera.cs")), rt = Read("ModRuntime.Camera.cs");
        string nl = Read("ModRuntime.Nightline.cs"), lk = Read(Path.Combine("Game", "PlayerLock.cs")), brain = Read(Path.Combine("Npc", "NpcBrain.cs")), ph = Read("ModRuntime.Phone.cs");
        Check(home.Contains("App(x0 + i % 4 * 72, y0 + i / 4 * 90,") && home.Contains("\"Camera\"") && home.Contains("\"Gallery\""),
              "FB-80: four apps a row, the Camera and the Gallery among them");
        Check(cam.Contains("raw.uvRect = new Rect((1 - show) / 2f, 0, show, 1);") && Read(Path.Combine("Game", "CameraView.cs")).Contains("_cam.cullingMask &= ~(1 << 5);")
              && Read(Path.Combine("Game", "Snapshot.cs")).Contains("public static (byte[]? jpeg, string? error) TakeView(Camera viewer, int width = 1920)"),
              "the live view fills the phone top to bottom (its sides cut); view and photo leave the HUD out; the photo is the whole screen");
        Check(lk.Contains("foreach (var l in new[] { _cameraLock, _moveLock })") && lk.Contains("Try(\"movement\", () => _moveLock = pc?.Controller?.DisableMovement(_owner));")
              && rt.Contains("else if (BindingPressed(_cameraBack, mouse, kb) && (_cameraBack.Mouse != 0 || !PhoneUi.PointerOnPhone)) PhoneUi.Back();"),
              "FB-82: in the Camera you walk too (the movement lock let go, taken again after), and CameraBackKey leaves it");
        string cfgC = Read("ModConfig.cs");
        Check(cfgC.Contains("CameraShutterKey = Bind(\"CameraShutterKey\", \"Left click\",") && cfgC.Contains("UpgradeDefault(CameraShutterKey, \"Space\");")
              && cfgC.Contains("CameraBackKey = Bind(\"CameraBackKey\", \"Right click\",") && rt.Contains("bool shoot = (BindingPressed(_shutter, mouse, kb) && (_shutter.Mouse < 0 || !PhoneUi.PointerOnPhone)) || Pad.A;")
              && rt.Contains("_cameraBack = _shutter == RightClick ? LeftClick : RightClick; // never both on one button"),
              "FB-82: left click takes the photo (not Space: an old Space is moved once), right click leaves; both can be set (a mouse button or a key), never the same one");
        Check(UI.SettingsCatalog.For("Keybinds", "CameraShutterKey", typeof(string)).Choices!.Take(5).SequenceEqual(new[] { "Left click", "Right click", "Middle click", "Mouse 4", "Mouse 5" })
              && UI.SettingsCatalog.For("Keybinds", "CameraBackKey", typeof(string)).Kind == UI.ControlKind.Choice,
              "...in the settings menu as a list of mouse buttons and keys");
        Check(!UI.SettingsCatalog.CameraBindings.Contains("Space") && !UI.SettingsCatalog.CameraBindings.Contains("LeftShift") && rt.Contains("Log.Warn($\"Camera: {key} already does something else"),
              "review F82-2/3: no walking keys offered; a binding that can't be used says so in the log");
        Check(lk.Contains("public void LookMode(bool on)") && lk.Contains("if (!Engaged || Looking) return;") && ph.Contains("EndCameraMode(); // FB-80")
              && rt.Contains("BindingPressed(_shutter, mouse, kb)"),
              "in the Camera you look around (camera and cursor let go, still not walking); click, Space or A takes it; leaving locks them again");
        Check(rt.Contains("CameraView.ChildInView(cam, c => { string id = GameContext.NpcId(c); return KnownUnder18(id) || MinorInfo(c, id).minor; });")
              && Read(Path.Combine("Game", "CameraView.cs")).Contains("catch (Exception e) { Log.Debug(\"phone camera, who's in view: \" + e.Message); return true; }"),
              "anyone under 18 in view marks the photo (a check that fails says yes)");
        Check(nl.Contains("if (p == null || !p.Mine || p.NoAi || line!.Author != LineBook.You) continue;") && nl.Contains("if (_cfg.LinePhotosSeen.Value)")
              && nl.Contains("noAi = !(_gallery?.MayShowAi(f.Name, f.Length, f.LastWriteTimeUtc) ?? false);") && rt.Contains("!PhoneUi.PointerOnPhone"),
              "only your own photo, never one marked for a child or not taken by the Camera, and only with LinePhotosSeen");
        Check(brain.Contains("if (!res.Ok && res.ImageRejected)") && brain.Contains("foreach (var s in seen) s.Image = null;"),
              "a model that can't take pictures is asked again without it");
    }
}
