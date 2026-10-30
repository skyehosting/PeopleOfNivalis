using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisAIChat.Tests;

/// <summary>
/// Step 5 of the audit, E5: the settings file read the way BepInEx reads it. A voice Providers line BepInEx can't read
/// ("(…, O'Neil:male, model=eleven_v3),") is fixed or switched off before BepInEx sees it; which keys the file holds, and
/// which line BepInEx would stop at, are found the same way it finds them.
/// </summary>
internal static partial class Program
{
    private static void SettingsFileTests()
    {
        Console.WriteLine("-- audit 2026-10-11: the settings file as BepInEx reads it (step 5, E5)");
        string File(string nl, params string[] lines) => string.Join(nl, lines) + nl;
        string head = "[Voices]", enabled = "Enabled = true", open = "Providers = [", close = "]", after = "Volume = 80";
        const string Bad = "    (https://api.elevenlabs.io/v1, k, 50, O'Neil:male, model=eleven_v3),";
        const string Good = "    (https://api.elevenlabs.io/v1, k, 50, O'Neil:male, model: eleven_v3),";

        foreach (var nl in new[] { "\r\n", "\n" })
        {
            string text = File(nl, "# top", head, enabled, open, Bad, "    (https://api.openai.com/v1, , 50, auto),", close, after);
            var problems = new List<string>();
            string fixedText = SettingsFileText.FixProviderLines(text, problems);
            Check(fixedText == File(nl, "# top", head, enabled, open, Good, "    (https://api.openai.com/v1, , 50, auto),", close, after),
                $"\"model=\" after O'Neil becomes \"model: \", nothing else changes, and line endings stay ({(nl == "\n" ? "LF" : "CRLF")})");
            Check(problems.Count == 1 && !problems[0].Contains("O'Neil") && !problems[0].Contains(", k,"), "...and the note says so without quoting the line (it may hold a key)");
            Check(SettingsFileText.FirstBadLine(fixedText) == 0 && SettingsFileText.FirstBadLine(text) == 5, "the fixed file has no line BepInEx stops at; the old one had line 5");
        }

        string tabbed = File("\n", head, open, "    (https://x.example/v1, k, 50,\tmodel=eleven_v3),", close);
        Check(SettingsFileText.FixProviderLines(tabbed, new List<string>()) == File("\n", head, open, "    (https://x.example/v1, k, 50, model: eleven_v3),", close),
            "a tab before model= becomes a space, and model= becomes model:");

        var off = new List<string>();
        string stillBad = File("\n", head, open, "    (https://x.example/v1, k, 50, O'Neil:male, voice=1),", close, after);
        string switchedOff = SettingsFileText.FixProviderLines(stillBad, off);
        Check(switchedOff == File("\n", head, open, "    # (https://x.example/v1, k, 50, O'Neil:male, voice=1),", close, after) && off.Count == 1 && off[0].Contains("switched off"),
            "a line still unreadable after the fix is switched off with \"# \", and the note says so");

        string lastLine = File("\n", head, open, "    (https://x.example/v1, k, 50, O'Neil:male, voice=1)]", after);
        Check(SettingsFileText.FixProviderLines(lastLine, new List<string>()) == File("\n", head, open, "    # (https://x.example/v1, k, 50, O'Neil:male, voice=1)", "    ]", after),
            "switching off the block's last line keeps its closing \"]\" on a line of its own");

        foreach (var (what, fine) in new[]
                 {
                     ("plain model= (BepInEx reads it)", File("\n", head, open, "    (https://x.example/v1, k, 50, model=eleven_v3),", close)),
                     ("?model= inside the address", File("\n", head, open, "    (https://x.example/tts?model=a, k, 50, O'Neil:male),", close)),
                     ("a key ending ==", File("\n", head, open, "    (https://x.example/v1, abc==, 50, auto),", close)),
                     ("a commented-out line", File("\n", head, open, "#   (https://x.example/v1, k, 50, O'Neil:male, model=x),", close)),
                     ("the one-line form", File("\n", head, "Providers = [ (https://x.example/v1, k, 50, O'Neil:male, model=x) ]")),
                     ("the same text in another group", File("\n", "[Debug]", open, Bad, close)),
                 })
            Check(ReferenceEquals(SettingsFileText.FixProviderLines(fine, new List<string>()), fine), $"left exactly as it is: {what}");

        string unclosed = File("\n", head, open, Bad, "[Controls]", "TalkKey = T");
        Check(SettingsFileText.FixProviderLines(unclosed, new List<string>()) == File("\n", head, open, Good, "[Controls]", "TalkKey = T"),
            "a block never closed ends at the next group, and its lines are still fixed");

        var keys = SettingsFileText.KeysIn(File("\n", "[AI Connection]", "ApiKey = x", "# Model = y", "[ Voices ]", "  Enabled   =  true", "[Debug]"), out var sections);
        Check(keys.Contains(("AI Connection", "ApiKey")) && keys.Contains((" Voices ", "Enabled")) && !keys.Contains(("AI Connection", "Model"))
              && sections.SetEquals(new[] { "AI Connection", " Voices ", "Debug" }),
            "the keys in the file as BepInEx sees them: comments aren't keys, section names aren't trimmed");
        Check(SettingsFileText.FirstBadLine(File("\n", "[Voices]", "Enabled = true", "[ Voices ]", "Volume = 80")) == 3,
            "a section name with spaces at its edges is where BepInEx stops (its header line)");
        Check(SettingsFileText.FirstBadLine(File("\n", "[Voices]", "Enabled = true", "[O'Neil]")) == 0,
            "a bad section name with no setting after it doesn't stop BepInEx (as measured)");

        // Step 5 review: lines end where BepInEx's File.ReadAllLines ends them: at "\r\n", "\n" and a lone "\r".
        var split = SettingsFileText.Lines("a\rb\r\nc\nd");
        Check(split.Count == 4 && split[0] == ("a", "\r") && split[1] == ("b", "\r\n") && split[2] == ("c", "\n") && split[3] == ("d", ""),
            "a lone CR ends a line, as it does for BepInEx; each line keeps its own ending");
        var crKeys = SettingsFileText.KeysIn(File("\r", "[AI Connection]", "Model = m", "ApiKey = x"), out var crSections);
        Check(crKeys.Contains(("AI Connection", "ApiKey")) && crKeys.Contains(("AI Connection", "Model")) && crSections.Contains("AI Connection"),
            "a file saved with CR-only line endings: every key is found");
        Check(SettingsFileText.KeysIn("[AI Connection]\r\n# a note\rApiKey = x\r\n", out _).Contains(("AI Connection", "ApiKey")),
            "a lone CR after a comment doesn't hide the key on the next line");
        Check(SettingsFileText.FirstBadLine(File("\r", "[Voices]", "Enabled = true", "Bad'Key = 1")) == 3, "a bad line is found (and counted) in a CR-only file");
        Check(SettingsFileText.FixProviderLines(File("\r", head, open, Bad, close, after), new List<string>()) == File("\r", head, open, Good, close, after),
            "a provider line is fixed in a CR-only file, and its endings stay CR");
        Check(SettingsFileText.FirstBadLine(File("\n", "[A=B]", "Key = 1")) == 1, "an '=' in a section name is where BepInEx stops, as for ' \" \\ [ ]");
        string twoBlocks = File("\n", head, open, Good, "[Debug]", open, Bad, close);
        Check(ReferenceEquals(SettingsFileText.FixProviderLines(twoBlocks, new List<string>()), twoBlocks),
            "the group that ends a never-closed block is read as a group: a Providers block in [Debug] isn't fixed as a voice list");

        // A setting whose line is gone but whose name is still in the file (commented out, moved, retyped) isn't reset.
        bool IsSetting(string s, string k) => (s, k) is ("AI Connection", "ApiKey") or ("AI Connection", "Model") or ("Controls", "TalkKey");
        var strays = SettingsFileText.StrayNames(File("\n", "[AI Connection]", "# ApiKey = x", "Model: z", "[Debug]", "TalkKey = T"), IsSetting);
        Check(strays.Contains("ApiKey") && strays.Contains("Model") && strays.Contains("TalkKey"),
            "names still in the file: a commented-out line, \"Key: value\", and a line in another group");
        Check(SettingsFileText.StrayNames(File("\n", "[AI Connection]", "apikey = x"), IsSetting).Contains("ApiKey"), "...and a name typed in another case");
        Check(SettingsFileText.StrayNames("[AI Connection]\r# ApiKey = x\r", IsSetting).Contains("ApiKey"), "...and a commented-out line in a CR-only file");
        var placed = SettingsFileText.StrayNames(File("\n", "[AI Connection]", "ApiKey = x", "Model = gpt:4", "[Controls]", "TalkKey = T"), IsSetting);
        Check(!placed.Contains("ApiKey") && !placed.Contains("Model") && !placed.Contains("TalkKey"), "a setting on its own line in its own group isn't a stray");

        // v3 (re-review of 110c6c0): names as people retype them, other comment marks, headers as BepInEx matches them.
        Check(SettingsFileText.Norm("Talk Key") == "talkkey" && SettingsFileText.Norm("api_key") == "apikey" && SettingsFileText.Norm("Voyage-Key") == "voyagekey",
            "names compared without case, spaces, '_' or '-'");
        var retyped = SettingsFileText.StrayNames(File("\n", "[Controls]", "Talk Key = G", "; ConsoleKey = F1", "// Model = m"), IsSetting);
        Check(retyped.Contains(SettingsFileText.Norm("TalkKey")) && retyped.Contains(SettingsFileText.Norm("ConsoleKey")) && retyped.Contains(SettingsFileText.Norm("Model")),
            "\"Talk Key = G\", \"; ConsoleKey\" and \"// Model\" count as the setting kept, not deleted");
        string zw = ((char)0x200B).ToString();
        Check(SettingsFileText.FirstBadLine(File("\n", "[Debug]" + zw, "LogPrompts = false")) == 1,
            "a zero-width space after a group's ']' is where BepInEx stops (it reads that header as \"Debug]\")");
        bool IsKey(string s, string k) => (s, k) is ("AI Connection", "ApiKey") or ("Memory", "VoyageKey");
        var keyLines = SettingsFileText.NamedLines(File("\n",
            "[AI Connection]", "# ApiKey = old", "ApiKey = new", "apikey: other", "ApiKey = last",
            "[Debug]", "ApiKey = moved", "[Memory]", "Voyage Key = v"), new[] { "ApiKey", "VoyageKey" }, IsKey);
        Check(keyLines.Count == 6 && keyLines.Count(l => l.read) == 1 && keyLines.Single(l => l.read).value == "last",
            "every line holding a key is found; only the last one BepInEx applies counts as read (" + keyLines.Count + " found)");
        Check(keyLines.Any(l => l.commented && l.value == "old") && keyLines.Any(l => l.value == "other" && !l.read)
              && keyLines.Any(l => l.section == "Debug" && l.value == "moved" && !l.read) && keyLines.Any(l => l.value == "v" && !l.read)
              && keyLines.Any(l => l.value == "new" && !l.read),
            "...a commented-out copy, \"apikey: …\", one in another group, \"Voyage Key\", and a key written twice are copies");
        Check(SettingsFileText.NamedLines(File("\n", "[AI Connection]", "# Your key. If empty, OPENAI_API_KEY is used.", "ApiKey = k"), new[] { "ApiKey" }, IsKey).Count == 1,
            "a comment that only mentions a key's name isn't a copy of it");
        var noted = SettingsFileText.NamedLines(File("\n", "[Voices]", "# Volume: how loud they are", "# Volume = 60", "Volume: 70"), new[] { "Volume" }, (s, k) => s == "Voices" && k == "Volume");
        Check(noted.Count == 3 && !noted[0].equals && noted[1].equals && noted[1].commented && !noted[2].equals && !noted[2].commented,
            "\"# Volume: how loud\" is a note (no '='); \"# Volume = 60\" is a commented-out copy; \"Volume: 70\" is a slip (settings v5)");

        // A voice list that lost its "]": it ends before the setting after it, without that setting's comments.
        var unclosedList = SettingsFileText.ProviderBlock(File("\n", head, open, "    (https://x.example/v1, k, 50, auto),", "#   (https://y.example/v1, k2, 50, auto),",
            "", "# Voice volume, 0 to 100.", "Volume = 80", "UseGameVolume = true"), out bool listClosed);
        Check(!listClosed && unclosedList != null && unclosedList.Count == 2 && unclosedList[1].TrimStart().StartsWith("#"),
            "a list with no ']' ends at the first setting after it, keeping the switched-off provider and dropping that setting's comment");
        var beforeNote = SettingsFileText.ProviderBlock(File("\n", head, open, "    (https://x.example/v1, k, 50, auto),", "Volume = 80", "#   (a note that looks like a provider)"), out _);
        Check(beforeNote != null && beforeNote.Count == 1, "...even when a comment shaped like a provider comes after that setting");
    }
}
