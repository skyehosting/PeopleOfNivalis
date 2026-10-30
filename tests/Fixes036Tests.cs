using System;
using System.Collections.Generic;
using System.Linq;
using NivalisAIChat.Voice;

namespace NivalisAIChat.Tests;

/// <summary>0.3.6 CF-09: the voice Providers list as the settings menu edits it.</summary>
internal static partial class Program
{
    private static void Fixes036Tests()
    {
        Console.WriteLine("-- 0.3.6 CF-09: the voice Providers list in the settings menu");
        var block = new List<string>
        {
            "    (https://api.openai.com/v1, , 100, auto),",
            "#   (https://api.elevenlabs.io, your-elevenlabs-key, 50, auto),",
            "#   (http://localhost:8880/v1, , 50, model: kokoro, auto),",
            "#",
            "#   Ready-made voice lists, to use instead of auto:",
            "#   (https://api.openai.com/v1, , 100, nova:female, onyx:male),",
        };
        var all = ProviderLines.Read(block);
        Check(all.Count == 4 && all[0].On && !all[1].On && all.Select(e => e.Line).SequenceEqual(new[] { 0, 1, 2, 5 }),
              "every provider line is one, on or switched off with a #; notes aren't");
        Check(all[2].Provider == VoiceProvider.OpenAICompatible && all[2].Model == "kokoro" && all[2].Voices == "auto"
              && all[3].Voices == "nova:female, onyx:male" && all[0].Chance == 100, "its model, voices and chance are read");
        Check(!ProviderLines.KeySet(all[1].Key) && !ProviderLines.KeySet("") && ProviderLines.KeySet("sk-real"),
              "the example's \"your-...-key\" counts as no key");

        var on = ProviderLines.With(block, all[1] with { On = true, Key = "el-key" });
        Check(on[1] == "    (https://api.elevenlabs.io, el-key, 50, auto)," && on.Where((l, i) => i != 1).SequenceEqual(block.Where((l, i) => i != 1)),
              "switching one on rewrites only its line (without the #); every other line stays as written");
        var off = ProviderLines.With(block, all[0] with { On = false });
        Check(off[0].StartsWith("#") && ProviderLines.Read(off)[0] == all[0] with { On = false }, "switched off, it keeps everything for later");
        var roundTrip = ProviderLines.Read(block.Select((l, i) => all.FirstOrDefault(e => e.Line == i) is { } e ? ProviderLines.Write(e) : l).ToList());
        Check(roundTrip.SequenceEqual(all), "written back, every provider reads the same");
        Check(ProviderLines.Write(all[2] with { Model = "", Voices = "" }).EndsWith(", 50, auto),"), "no voices listed is written auto");
        Check(VoiceProviderList.Parse(on, new List<string>()).Count == 2, "the voices read the edited list as usual (two on)");
        Check(on.All(l => !NivalisAIChat.SettingsFileText.BreaksBepInEx(l)), "a written line never stops BepInEx reading the file");

        var added = ProviderLines.Added(block, VoiceProvider.ElevenLabs);
        Check(added.Count == block.Count + 1 && added[1].StartsWith("    (https://api.elevenlabs.io, , 50, auto)"),
              "an added provider goes after the last one that's on, switched on, with its usual address");
        var local = ProviderLines.Added(off, VoiceProvider.OpenAICompatible);
        Check(local[0].StartsWith("    (http://localhost:8880/v1,") && ProviderLines.Read(local).Count == 5, "...before the switched-off ones when none is on");
        Console.WriteLine("-- 0.3.6 FB-40: this mod's settings as Nivalis Switchboard reads them");
        var bar = NivalisAIChat.UI.SettingsCatalog.ToContract(NivalisAIChat.UI.SettingsCatalog.For("City Life", "GreetChance", typeof(float)));
        Check((string)bar["kind"]! == "slider" && (double)bar["min"]! == 0 && (double)bar["max"]! == 100 && (double)bar["step"]! == 5 && (string)bar["unit"]! == "%",
              "a bar goes over with its kind, range, step and unit");
        var choice = NivalisAIChat.UI.SettingsCatalog.ToContract(NivalisAIChat.UI.SettingsCatalog.For("NPC Chatter", "ChatterMode", typeof(string)));
        Check((string)choice["kind"]! == "choice" && ((List<string>)choice["choices"]!).SequenceEqual(new[] { "Dynamic", "Advanced" }), "a dropdown with its choices");
        Check((string)NivalisAIChat.UI.SettingsCatalog.ToContract(NivalisAIChat.UI.SettingsCatalog.For("NPC Chatter", "ChatterRange", typeof(float)))["kind"]! == "hidden"
              && (string)NivalisAIChat.UI.SettingsCatalog.ToContract(NivalisAIChat.UI.SettingsCatalog.For("AI Connection", "ApiKey", typeof(string)))["kind"]! == "secret"
              && (string)NivalisAIChat.UI.SettingsCatalog.ToContract(new NivalisAIChat.UI.SettingControl(NivalisAIChat.UI.ControlKind.Button, Unit: "Remove"))["label"]! == "Remove",
              "the rings' settings are hidden (their own rows show them), keys stay secrets, a button carries its label");
        var removed = ProviderLines.Removed(block, all[1]);
        Check(removed.Count == block.Count - 1 && !removed.Any(l => l.Contains("elevenlabs")) && removed[0] == block[0], "removing takes out only its line");
    }
}
