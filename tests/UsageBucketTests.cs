using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NivalisAIChat.Social;

namespace NivalisAIChat.Tests;

/// <summary>
/// Step 0 of the research plan (2026-10-18), the author's rule: anything that spends AI tokens or voice characters is
/// wired into the Usage Optimizer. The optimizer counts any purpose it doesn't list as yours (never made smaller), so a
/// new residents' purpose left out of the lists would be spent unchecked. This reads every request purpose and every
/// voice purpose in the source and fails on one that isn't in a list on purpose.
/// </summary>
internal static partial class Program
{
    /// <summary>The last argument of each VoiceForAsync(...) call when it's a string literal ("" when it isn't), by counting brackets.</summary>
    private static IEnumerable<string> VoicePurposes(string text)
    {
        int at = 0;
        while ((at = text.IndexOf("VoiceForAsync(", at, StringComparison.Ordinal)) >= 0)
        {
            int i = at + "VoiceForAsync(".Length, depth = 1;
            while (i < text.Length && depth > 0)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')') depth--;
                i++;
            }
            string args = text.Substring(at, i - at);
            at = i;
            if (args.Contains("Func<VoiceSubject>")) continue; // the definition
            var m = Regex.Match(args, @",\s*""([^""]+)""\)$");
            yield return m.Success ? m.Groups[1].Value : "";
        }
    }

    private static void UsageBucketTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- the usage optimizer counts every purpose (step 0)");
        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        var files = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories);
        var aiKnown = UsageModel.YoursAi.Concat(UsageModel.ResidentsAi).Concat(UsageModel.GreetAi).ToHashSet();
        var voiceKnown = UsageModel.YoursVoice.Concat(UsageModel.ResidentsVoice).Concat(UsageModel.GreetVoice).ToHashSet();

        // VoiceSubject.Purpose (VoiceModels.cs) is a voice's field, not a request.
        var ai = files.Where(f => Path.GetFileName(f) is not ("UsageModel.cs" or "VoiceModels.cs"))
                      .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"Purpose\s*=\s*""([^""]*)""").Select(m => (f: Path.GetFileName(f), p: m.Groups[1].Value)))
                      .ToList();
        var unbucketedAi = ai.Where(x => !aiKnown.Contains(x.p)).Select(x => $"'{x.p}' ({x.f})").Distinct().ToList();
        Check(ai.Count >= 15 && unbucketedAi.Count == 0,
            $"every AI request purpose is in one of the optimizer's lists ({ai.Count} found{(unbucketedAi.Count > 0 ? "; not listed: " + string.Join(", ", unbucketedAi) : "")})");

        // Every voice line says what it's for (no default), and that is a known purpose.
        string voices = File.ReadAllText(Path.Combine(src, "ModRuntime.Voices.cs"));
        Check(voices.Contains("CancellationToken ct, string purpose)"), "a voice line always says its purpose (no default that could count it as residents' talk)");
        var calls = files.SelectMany(f => VoicePurposes(File.ReadAllText(f)).Select(p => (f: Path.GetFileName(f), p))).ToList();
        var unbucketedVoice = calls.Where(x => !voiceKnown.Contains(x.p)).Select(x => $"'{x.p}' ({x.f})").Distinct().ToList();
        Check(calls.Count >= 15 && unbucketedVoice.Count == 0,
            $"every voice purpose is in one of the optimizer's lists ({calls.Count} call sites{(unbucketedVoice.Count > 0 ? "; not listed: " + string.Join(", ", unbucketedVoice) : "")})");

        Check(calls.Any(x => x.f == "ModRuntime.GroupChat.cs" && x.p == "you") && calls.Any(x => x.f == "ModRuntime.Calls.cs" && x.p == "think"),
            "group chat voices count as yours; muttering as its own kind (not as calls)");
        Check(File.ReadAllText(Path.Combine(src, "ModRuntime.SpeakUp.cs")).Contains("AiAllowed() && _cfg.AiGreetingLines.Value"),
            "companions speaking up follow the AI-greetings lever");
        Check(File.ReadAllText(Path.Combine(src, "ModRuntime.Usage.cs")).Contains("[\"think\"] = \"think\", [\"order\"] = \"order\" };"),
            "the optimizer learns muttering's voice cost per start");
    }
}
