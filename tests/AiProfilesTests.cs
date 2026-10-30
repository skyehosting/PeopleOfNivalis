using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NivalisAIChat.Ai;

namespace NivalisAIChat.Tests;

/// <summary>CF-20 (a player: "AI profiles so I can easily switch providers when my usage changes").</summary>
internal static partial class Program
{
    private static void AiProfilesTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- CF-20: AI profiles");
        string dir = TestRoot.NewFolder("pon-profiles");
        string path = Path.Combine(dir, "ai_profiles.json");
        var book = new AiProfiles(path, _ => { });
        Check(book.Names().Count == 0 && book.Active == "", "none to start with");
        var cheap = new AiProfiles.Profile { Name = "Cheap  nights!", Format = "OpenAI", ApiUrl = "https://openrouter.ai/api/v1", ApiKey = "test-key-not-real", Model = "deepseek-flash" };
        Check(book.Save(cheap) == null && book.Names().SequenceEqual(new[] { "Cheap nights" }) && book.Active == "Cheap nights", "saved under a clean name, and it's the one in use");
        Check(book.Get("cheap NIGHTS")?.ApiKey == "test-key-not-real" && book.Get("nope") == null, "found by name in any case, with its key");
        Check(!cheap.ToString().Contains("test-key") && cheap.ToString().Contains("key set") && cheap.ToString().Contains("openrouter.ai"), "shown or logged, a profile never carries its key");
        var best = new AiProfiles.Profile { Name = "Best", Format = "Anthropic", ApiUrl = "https://api.anthropic.com/v1", Model = "claude-opus-5-5" };
        book.Save(best);
        book.Save(new AiProfiles.Profile { Name = "best", Model = "claude-sonnet-5-5" });
        Check(book.Names().Count == 2 && book.Get("Best")?.Model == "claude-sonnet-5-5", "the same name replaces it");
        var again = new AiProfiles(path, _ => { });
        Check(again.Names().Count == 2 && again.Active == "best" && again.Get("Cheap nights")?.Model == "deepseek-flash", "kept on this PC, read back the same");
        Check(again.Remove("cheap nights") && !again.Remove("cheap nights") && again.Names().SequenceEqual(new[] { "best" }), "removed once");
        Check(book.Save(new AiProfiles.Profile { Name = "!!!" }) != null && AiProfiles.CleanName(new string('a', 50)).Length == AiProfiles.MaxName, "no name, no profile; a long one cut");
        var full = new AiProfiles(Path.Combine(dir, "full.json"), _ => { });
        for (int i = 0; i < AiProfiles.MaxProfiles; i++) full.Save(new AiProfiles.Profile { Name = "p" + i });
        Check(full.Save(new AiProfiles.Profile { Name = "one more" }) != null && full.Save(new AiProfiles.Profile { Name = "p3", Model = "x" }) == null, $"at most {AiProfiles.MaxProfiles} (replacing one still works)");

        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string rt = File.ReadAllText(Path.Combine(src, "ModRuntime.Profiles.cs"));
        Check(rt.Contains("save[\"get\"] = (Func<object?>)(() => \"\")") && !rt.Contains("ApiKey.Value)") && rt.Contains("Log.Info($\"AI profile: switched to {p}.\")"),
              "the menu never shows a key, and the log only the profile's safe form");
        Check(File.ReadAllText(Path.Combine(src, "ModRuntime.Commands.cs")).Contains("case \"/profile\" or \"/profiles\""), "/profile in the console");
    }
}
