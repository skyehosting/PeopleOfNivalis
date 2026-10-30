using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace NivalisAIChat.Tests;

/// <summary>CF-20: phone notifications, the pip the author chose (notify-3).</summary>
internal static partial class Program
{
    private static void NotifyTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- CF-20: phone notifications (the pip)");
        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string ui = File.ReadAllText(Path.Combine(src, "UI", "PhoneWindow.Notify.cs"));
        string rt = File.ReadAllText(Path.Combine(src, "ModRuntime.Phone.cs"));
        Check(ui.Contains("public void Notify(string from)") && !ui.Contains("Notify(string from, string") && rt.Contains("PhoneUi.Notify(from);"),
              "the pip is told only who it's from, never what they said");
        Check(rt.Contains("if (PhoneUi.NotifyStyle == \"Card\") PhoneUi.Toast(from, body, k.Kind);"), "Card: the old card with the words, only when chosen");
        Check(ui.Contains("bool show = NotifyStyle == \"Pip\" && Unread > 0 && !_visible && !HoldToasts;"), "shown only with unread texts, the phone away, no chat or menu open");
        Check(ui.Contains("AudioClip.Create(\"PeopleOfNivalis.Chime\"") && ui.Contains("if (NotifyVolume <= 0.001f"), "the chime is made in the mod (no sound files); 0 = silent");
        string cfg = File.ReadAllText(Path.Combine(src, "ModConfig.cs"));
        Check(cfg.Contains("Bind(\"PhoneNotify\", \"Pip\"") && cfg.Contains("Bind(\"PhoneNotifyVolume\", 50"), "Pip by default, the chime at half");
        string cat = File.ReadAllText(Path.Combine(src, "UI", "SettingsCatalog.cs"));
        Check(cat.Contains("[\"PhoneNotify\"] = C(\"Pip\", \"Card\", \"Off\")"), "in the settings menu");
        Check(ui.Contains("private void ChimeWhenShown() { if (HoldToasts) _chimeOwed = true; else PlayChime(); }") && !ui.Contains("        PlayChime();\n    }")
              && ui.Contains("else if (_chimeOwed && !HoldToasts) { _chimeOwed = false; PlayChime(); }"),
              "the 2026-10-20 audit: a text during a chat chimes when its pip or card shows, not in the middle of the conversation");
        int ring = ui.IndexOf("_pipRing = Box(_pip, \"Pulse\"", StringComparison.Ordinal);
        Check(ring > 0 && ui.IndexOf("_pipRing.pivot = new Vector2(0.5f, 0.5f);", ring, StringComparison.Ordinal) is > 0 and var pv && pv - ring < 300
              && ui.Contains("_pipRing.anchoredPosition = new Vector2(px + PhoneW / 2f, -(py + PhoneH / 2f));"),
              "FB-78: the pulse ring grows from the phone's middle, out on every side (not only right and down)");
        string win = File.ReadAllText(Path.Combine(src, "UI", "PhoneWindow.cs"));
        Check(win.Contains("if (over && click && (clicked == null || !clicked.Rt.IsChildOf(h.Rt))) clicked = h;"),
              "FB-78: clicking a like or dislike on a Nightline card votes (the pill inside wins over its card, which opens the line)");
        string feed = File.ReadAllText(Path.Combine(src, "UI", "PhoneWindow.Nightline.cs"));
        Check(feed.Contains("float mh = shortLine ? 17 : Mathf.Clamp(Tall(m, width - 20), 17, 17 * 8);"),
              "FB-78: \"No one yet\" is one line, so its popover sits by the pill (not at the phone's bottom)");
    }
}
