using System;
using System.Linq;
using NivalisAIChat.Social;
using NivalisAIChat.UI;

namespace NivalisAIChat.Tests;

/// <summary>0.3.5 CF-07/CF-08: the controller's held directions, button names, and the settings menu's rows and ring sliders.</summary>
internal static partial class Program
{
    private static void Fixes035Tests()
    {
        Console.WriteLine("-- 0.3.5 CF-07: the controller in the mod's windows");
        var rep = new PadRepeat();
        Check(rep.Step(1, 0f) == 1 && rep.Step(1, 0.1f) == 0 && rep.Step(1, 0.39f) == 1 && rep.Step(1, 0.42f) == 0 && rep.Step(1, 0.48f) == 1,
              "a held direction: one step at once, again after a wait, then quicker ones");
        Check(rep.Step(0, 0.5f) == 0 && rep.Step(1, 0.51f) == 1 && rep.Step(-1, 0.52f) == -1, "letting go or turning round starts over at once");
        Check(PadRepeat.StickDirection(0.3f, 0.2f) == (0, 0) && PadRepeat.StickDirection(0.9f, 0.6f) == (1, 0) && PadRepeat.StickDirection(-0.2f, -0.7f) == (0, -1),
              "a stick moves only past its dead zone, along its stronger axis");
        Check(PadRepeat.StickDirection(float.NaN, 1f) == (0, 0), "...and a stick reading nothing sensible doesn't move");
        Check(PadRepeat.Label("RT", playStation: true) == "R2" && PadRepeat.Label("LB", true) == "L1" && PadRepeat.Label("A", true) == "X" && PadRepeat.Label("X", true) == "Square"
              && PadRepeat.Label("rt", false) == "RT", "buttons are named as the pad shows them (R2, and X for the cross, on PlayStation)");

        Console.WriteLine("-- 0.3.5 CF-08: the settings menu's rows");
        Check(SettingsCatalog.Title("ChatterPerHour") == "Chatter per hour" && SettingsCatalog.Title("UiScale") == "UI scale"
              && SettingsCatalog.Title("ApiUrl") == "API URL" && SettingsCatalog.Title("AiGreetingLines") == "AI greeting lines"
              && SettingsCatalog.Title("LifeUpdateAfterGameDays") == "Life update after game days", "a setting's name reads as words (API, UI, AI kept in capitals)");
        Check(SettingsCatalog.Hint("How close you need to be, in metres. More text here.") == "How close you need to be, in metres.",
              "the line under a name is the first sentence of its explanation");
        string longHint = SettingsCatalog.Hint(string.Join(" ", Enumerable.Repeat("word", 60)) + ".", 50);
        Check(longHint.Length <= 51 && longHint.EndsWith("…") && !longHint.Contains("wor…"), "...cut at a word when it's long");
        Check(SettingsCatalog.Hint("Sixty characters of explanation that run a little past fifty.", 50).EndsWith("…"),
              "...even when it's only a little too long");
        Check(SettingsCatalog.ParseNumber(SettingsCatalog.For("AI Connection", "TokenLimit", typeof(int)), "12.6", whole: true) == 13,
              "a whole-number setting typed with a fraction is rounded");
        Check(SettingsCatalog.For("Controls", "PadTalkButton", typeof(string)).Choices!.Take(1).Concat(SettingsCatalog.For("Controls", "PadTalkButton", typeof(string)).Choices!.TakeLast(3)).SequenceEqual(new[] { "Interact", "RT", "LT", "off" })
              && SettingsCatalog.For("Controls", "PadConsoleButton", typeof(string)).Kind == ControlKind.Choice
              && SettingsCatalog.For("Controls", "ConsoleKey", typeof(string)).Kind == ControlKind.Key,
              "the talk button is the pad's interact button (or a trigger); the console key is caught as a key");

        Check(SettingsCatalog.AsSetting(typeof(int), 12.6) is 13 && SettingsCatalog.AsSetting(typeof(float), 2.5) is 2.5f
              && SettingsCatalog.AsSetting(typeof(bool), true) is true && SettingsCatalog.AsSetting(typeof(string), "x") is "x",
              "a menu value becomes the setting's own type (a slider's number as an int or a float)");
        Check(SettingsCatalog.AsSetting(typeof(TextSpeedLike), "fast") is TextSpeedLike.Fast && SettingsCatalog.AsSetting(typeof(TextSpeedLike), "Warp") == null,
              "a dropdown's name becomes the enum (any case); one it doesn't have saves nothing");
        Check(SettingsCatalog.AsSetting(typeof(int), double.NaN) == null && SettingsCatalog.AsSetting(typeof(float), "lots") == null
              && SettingsCatalog.AsSetting(typeof(int), "1,5") is 2, "nothing unreadable is saved; a typed comma decimal reads");

        Console.WriteLine("-- 0.3.5 CF-08: the rings' sliders (FB-38)");
        var r = new Rings(15f, 18f, 18.25f);
        Check(RingSliders.Value(r, RingPart.Master) == 18.25f && RingSliders.Value(r, RingPart.Hearing) == 15f && RingSliders.Value(r, RingPart.Voices) == 18f,
              "the master shows the outer ring; each slider its own ring");
        Check(RingSliders.Control(RingPart.Hearing).Step == Rings.Step && RingSliders.Control(RingPart.Master).Min == Rings.MinChatter
              && RingSliders.Control(RingPart.Chatter).Max == Rings.MaxRing, "every ring slider moves in 0.25 m steps within the rings' limits");
        var hearingOut = RingSliders.Set(r, r, RingPart.Hearing, 20f);
        Check(hearingOut.Hearing == 20f && hearingOut.Voices >= 20f && hearingOut.Chatter >= hearingOut.Voices, "the ring moved wins, the others keep in order");
        var anchor = r;
        var step1 = RingSliders.Set(r, anchor, RingPart.Master, 36.5f);
        var back = RingSliders.Set(step1, anchor, RingPart.Master, 18.25f);
        Check(step1.Chatter == 36.5f && step1.Hearing == 30f && back == r.Clamped(), "the master scales all three from its anchor, and back again without drifting");
        var tiny = RingSliders.Set(r, anchor, RingPart.Master, 5f);
        Check(RingSliders.Set(tiny, anchor, RingPart.Master, 18.25f) == r.Clamped() && tiny.ScaledTo(18.25f) != r.Clamped(),
              "...even from the smallest, where scaling from the last step would have lost the proportions");
        Check(RingSliders.Off(new Rings(0f, 3f, 18.25f), RingPart.Voices) && !RingSliders.Off(r, RingPart.Voices)
              && !RingSliders.Off(new Rings(0f, 3f, 18.25f), RingPart.Hearing) && !RingSliders.Off(new Rings(0f, 3f, 18.25f), RingPart.Master),
              "with hearing 0 (no voices) the voices slider shows off (only that one)");
        Check(RingSliders.All.Select(RingSliders.Title).Distinct().Count() == 4 && RingSliders.All.All(p => RingSliders.Help(p).Length > 40),
              "each ring slider has its own name and explanation");
    }

    private enum TextSpeedLike { Slow, Medium, Fast }
}
