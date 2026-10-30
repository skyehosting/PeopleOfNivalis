using System;
using System.Linq;
using NivalisAIChat.UI;

namespace NivalisAIChat.Tests;

/// <summary>0.3.4 CF-08: which control each setting gets in the settings menu.</summary>
internal static partial class Program
{
    private enum Pace { Slow, Quick }

    private static void SettingsCatalogTests()
    {
        Console.WriteLine("-- 0.3.4 CF-08: the settings menu's controls");
        Check(SettingsCatalog.For("Voices", "Enabled", typeof(bool)).Kind == ControlKind.Switch, "an on/off setting is a pill switch");
        Check(SettingsCatalog.For("Interface", "TextSpeed", typeof(string)).Choices!.SequenceEqual(new[] { "Slow", "Medium", "Fast", "Instant" })
              && SettingsCatalog.For("Somewhere", "SomeChoice", typeof(Pace)).Choices!.SequenceEqual(new[] { "Slow", "Quick" }),
              "a setting with a fixed list is a dropdown of it (an enum one too)");
        Check(SettingsCatalog.For("AI Connection", "ApiKey", typeof(string)).Kind == ControlKind.Secret
              && SettingsCatalog.For("Memory", "VoyageKey", typeof(string)).Kind == ControlKind.Secret
              && SettingsCatalog.For("AI Fine Tuning", "ExtraHeaders", typeof(string)).Kind == ControlKind.Secret,
              "keys and headers are secrets (never shown)");
        Check(SettingsCatalog.SecretLabel("") == "not set" && SettingsCatalog.SecretLabel("sk-anything") == "set", "...which show only whether one is set");
        Check(new[] { "ChatterRange", "VoiceDistance", "VoiceBeyond" }.All(k => SettingsCatalog.For("NPC Chatter", k, typeof(float)).Kind == ControlKind.Ring),
              "the three ring settings are drawn as the rings' master and sliders (FB-38), not on their own");
        Check(!SettingsCatalog.Shown("Advanced Chatter", "GroupTables", typeof(int)), "a setting nothing reads any more isn't shown");
        Check(SettingsCatalog.For("Somewhere", "SomethingNew", typeof(string)).Kind == ControlKind.Text
              && SettingsCatalog.For("Somewhere", "SomeNumber", typeof(int)).Kind == ControlKind.Number, "anything not listed gets the plain control for its type");

        var pct = SettingsCatalog.For("City Life", "GreetChance", typeof(float));
        Check(pct.Kind == ControlKind.Slider && pct.Snap(37) == 35 && pct.Snap(120) == 100 && pct.Snap(-4) == 0 && pct.Format(35) == "35%",
              "a percent slider moves in 5s, from 0 to 100, shown as 35%");
        var dist = SettingsCatalog.For("City Life", "OverhearDistance", typeof(float));
        Check(dist.Stepped(2.5, +1) == 3 && dist.Stepped(0.5, -1) == 0.5 && dist.Format(2.5) == "2.5 m", "a distance slider steps 0.5 m and stays within its range");
        Check(SettingsCatalog.For("AI Fine Tuning", "GiveUpAfterSeconds", typeof(int)).Snap(-1) == -1, "-1 (no limit) is still on GiveUpAfterSeconds' slider");
        var every = SettingsCatalog.For("NPC Chatter", "DynamicEvery", typeof(string));
        Check(every.Kind == ControlKind.Choice && every.ChoicesWith("10s").Count == every.Choices!.Count && every.ChoicesWith("1h30m")[0] == "1h30m",
              "an interval is a dropdown; a hand-edited one not on the list stays choosable");
        var tokens = SettingsCatalog.For("AI Connection", "TokenLimit", typeof(int));
        Check(SettingsCatalog.ParseNumber(tokens, " 250,000 ".Replace(",", ""), whole: true) == 250000 && SettingsCatalog.ParseNumber(tokens, "lots", true) == null
              && SettingsCatalog.ParseNumber(pct, "12,4", false) == 10, "a typed number is read with a dot or comma, snapped for a slider; words are refused");
        Check(SettingsCatalog.ParseNumber(tokens, "-5", true) == 0 && SettingsCatalog.ParseNumber(tokens, "NaN", true) == null, "...and kept within its range, never NaN");
    }
}
