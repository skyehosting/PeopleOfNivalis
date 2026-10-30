using System;
using System.IO;
using System.Runtime.CompilerServices;
using NivalisAIChat.Social;

namespace NivalisAIChat.Tests;

/// <summary>CF-19: customers' names stay blank until you know them; the story cast and shop people keep theirs.</summary>
internal static partial class Program
{
    private static void NamesTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- CF-19: names until you've met them");
        Check(NameRules.HideCustomerName(true, false, false, false), "an ordinary customer you haven't met: blank");
        Check(!NameRules.HideCustomerName(true, true, false, false), "the story cast: their name, always");
        Check(!NameRules.HideCustomerName(true, false, true, false), "the game has you met: their name");
        Check(!NameRules.HideCustomerName(true, false, false, true), "they told you their name: their name");
        Check(!NameRules.HideCustomerName(false, false, false, false), "the setting off: every name, as the game shows it");

        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string hooks = File.ReadAllText(Path.Combine(src, "Game", "NameHooks.cs"));
        Check(hooks.Contains("typeof(PlayerCustomerARUi), nameof(PlayerCustomerARUi.SetCustomerName)") && hooks.Contains("typeof(CustomerPopup), nameof(CustomerPopup.Show)")
              && !hooks.Contains("ChatRequestIndicator") && !hooks.Contains("CharacterNameDisplay"),
              "only the customer overlay and the pop-up are touched (name plates keep the game's names; ChatRequestIndicator.SetText shares its code)");
        string names = File.ReadAllText(Path.Combine(src, "ModRuntime.Names.cs"));
        Check(names.Contains("GameContext.IsNamed(person)) return false") && names.Contains("HasBeenMet") && names.Contains("NameRevealed"),
              "story cast, the game's met and a name told you each show the name");
        Check(names.Contains("now?.Pointer != person.Pointer") && names.Contains("(now == null || NameHidden(now))"), "a pooled overlay given to someone else never gets the old name back");
        string cfg = File.ReadAllText(Path.Combine(src, "ModConfig.cs"));
        Check(cfg.Contains("Bind(\"HideUnmetCustomerNames\", true,"), "on by default");
        string obf = File.ReadAllText(Path.Combine(Path.GetDirectoryName(src)!, "release", "obfuscar.xml"));
        Check(obf.Contains(@"^NivalisAIChat\.Game\.NameHooks/.*"), "the public build keeps the hooks' names (Harmony finds them by name)");
        string rt = File.ReadAllText(Path.Combine(src, "ModRuntime.cs"));
        Check(rt.Contains("TickCustomerNames();"), "a name you've come to know is shown again");
    }
}
