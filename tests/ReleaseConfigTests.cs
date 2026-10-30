using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using NivalisAIChat.Voice;

namespace NivalisAIChat.Tests;

/// <summary>
/// Audit step 7 (7a, #5): release/obfuscar.xml makes a public copy that still works. Its paths sit beside it (no folder
/// of this PC), every Harmony patch class is kept, and so is every type a saved file reaches (renaming a property would
/// make every saved file unreadable: the under-18 list would start empty).
/// </summary>
internal static partial class Program
{
    /// <summary>
    /// The types the mod saves with System.Text.Json, as their files' roots. A new saved type goes here; everything it
    /// reaches through its properties is then checked too.
    /// </summary>
    private static readonly Type[] SavedRoots =
    {
        typeof(NpcRecord),                                                            // npcs\*.json
        typeof(MemoryFile),                                                           // the memory index
        typeof(MinorList).GetNestedType("Data", BindingFlags.NonPublic)!,             // minors.json, the under-18 list
        typeof(WorldData),                                                            // world.json
        typeof(PairBook.Pair),                                                        // pairs.json
        typeof(CircleBook.Circle),                                                    // circles.json (FB-35)
        typeof(VisitBook.Entry),                                                      // visits.json
        typeof(VoiceAssignment),                                                      // voices.json
        // Missed until 0.5.0: the public 0.4.9 wrote these as {} (story memory, the usage record and the plan lost on save).
        typeof(StoryBook.Person),                                                     // story.json (FB-50)
        typeof(UsagePlanBook.Data),                                                   // usage_plan.json (FB-41)
        typeof(SaveSnapshots.Manifest),                                               // saves\slots\*.json (FB-61)
        typeof(Social.CoupleBook.Data),                                               // romance.json (FB-63)
        typeof(Social.ReminderBook.Data),                                             // reminders.json (FB-65)
        typeof(VoiceLedger).GetNestedType("Data", BindingFlags.NonPublic)!,           // voice_ledger.json (FB-41)
        typeof(Mind.MindBook.Data),                                                   // minds.json (the inner-life round)
        typeof(Mind.WitnessBook.Data),                                                // witness.json (the inner-life round)
        typeof(Phone.PhoneBook.Data),
        typeof(Phone.LineBook.Data),
        typeof(Ai.AiProfiles.Data),                                                   // ai_profiles.json (CF-20)
        typeof(Phone.ShotIndex.Data),                                                 // nightline-shots/shots.json (the city's street views)                                                  // nightline.json (Nightline, per save)                                                 // phone.json (the phone, per save)
        typeof(Phone.Wallpaper.Saved),                                                // phone-wallpaper/wallpaper.json
        typeof(Phone.Gallery.Data),                                                   // gallery.json (FB-80: the Camera's photos)
    };

    private static void ReleaseConfigTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("Release: the obfuscation config (audit step 7a)");
        string root = Path.GetDirectoryName(Path.GetDirectoryName(here))!;
        string file = Path.Combine(root, "release", "obfuscar.xml");
        Check(File.Exists(file), "release/obfuscar.xml found from the test's own source path");
        if (!File.Exists(file)) return;
        var xml = XDocument.Load(file);
        string text = File.ReadAllText(file);

        string Var(string name) => xml.Root!.Elements("Var").FirstOrDefault(v => (string?)v.Attribute("name") == name)?.Attribute("value")?.Value ?? "";
        var module = xml.Root!.Element("Module");
        Check(Var("InPath").StartsWith("$(ProjectFileDirectory)", StringComparison.Ordinal) && Var("OutPath").StartsWith("$(ProjectFileDirectory)", StringComparison.Ordinal)
              && ((string?)module?.Attribute("file") ?? "").StartsWith("$(InPath)", StringComparison.Ordinal),
            "In, Out and the module sit beside obfuscar.xml ($(ProjectFileDirectory)), not in a folder of one PC");
        Check(!text.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase) && !text.Contains("scratchpad", StringComparison.OrdinalIgnoreCase),
            "no user folder anywhere in obfuscar.xml");

        var skips = module?.Elements("SkipType").ToList() ?? new List<XElement>();
        bool On(XElement e, string flag) => string.Equals((string?)e.Attribute(flag), "true", StringComparison.OrdinalIgnoreCase);
        Check(skips.Any(s => (string?)s.Attribute("name") == "*" && (string?)s.Attribute("decorator") == "HarmonyLib.HarmonyPatch"
                             && On(s, "skipMethods") && On(s, "skipFields") && On(s, "skipProperties")),
            "every class carrying [HarmonyPatch] is kept whole, by attribute (Harmony finds Prefix, Postfix and __instance by name)");
        // BepInEx loads Plugin by name, and Unity calls Pump's Update and LateUpdate by name: renamed, the mod would load and
        // do nothing (review of a0fa75f).
        Check(skips.Any(s => (string?)s.Attribute("name") == "NivalisAIChat.Plugin" && On(s, "skipMethods") && On(s, "skipStringHiding"))
              && skips.Any(s => (string?)s.Attribute("name") == "NivalisAIChat.Pump" && On(s, "skipMethods")),
            "Plugin and Pump keep their names and methods");
        // Nivalis Switchboard looks for a static class named SettingsMenuPage with Describe(): renamed, the public copy's
        // settings page would be lost (only the plain controls).
        Check(skips.Any(s => (string?)s.Attribute("name") == "NivalisAIChat.SettingsMenuPage" && On(s, "skipMethods"))
              && File.ReadAllText(Path.Combine(root, "src", "ModRuntime.Settings.cs")).Contains("public static class SettingsMenuPage", StringComparison.Ordinal),
            "SettingsMenuPage keeps its name and Describe (Switchboard finds the page by name)");
        // 0.5.2: other menus find "don't list this" by the tag's type name and field names (ConfigGuard.cs declares it).
        string guard = File.ReadAllText(Path.Combine(root, "src", "ConfigGuard.cs"));
        Check(skips.Any(s => (string?)s.Attribute("name") == "NivalisAIChat.ConfigurationManagerAttributes" && On(s, "skipFields"))
              && guard.Contains("internal sealed class ConfigurationManagerAttributes", StringComparison.Ordinal)
              && guard.Contains("public bool? Browsable;", StringComparison.Ordinal) && guard.Contains("public bool? ReadOnly;", StringComparison.Ordinal)
              && guard.Contains("public int? Order;", StringComparison.Ordinal),
            "ConfigurationManagerAttributes keeps its name and fields (other menus read Browsable, ReadOnly and Order by name)");
        // The patch classes themselves are Unity code (not built here): found in the source instead.
        string src = Path.Combine(root, "src");
        var patchFiles = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("HarmonyPatch(", StringComparison.Ordinal)).Select(Path.GetFileNameWithoutExtension).ToList();
        Check(patchFiles.Count > 0 && patchFiles.All(f => f == "ModRuntime" || skips.Any(s => ((string?)s.Attribute("name") ?? "").Contains($@"\.{f}/", StringComparison.Ordinal))),
            "...and each file holding patches is kept by name as well (" + string.Join(", ", patchFiles) + ")");

        // Every type a saved file reaches keeps its property names.
        var reached = new List<Type>();
        void Walk(Type t)
        {
            if (t.IsArray) { Walk(t.GetElementType()!); return; }
            if (t.IsGenericType) { foreach (var a in t.GetGenericArguments()) Walk(a); return; }
            if (t.IsEnum || t.Namespace == null || !t.Namespace.StartsWith("NivalisAIChat", StringComparison.Ordinal) || reached.Contains(t)) return;
            reached.Add(t);
            if (t.BaseType != null) Walk(t.BaseType); // inherited properties are saved too
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (p.GetCustomAttribute<JsonIgnoreAttribute>() == null && (p.GetMethod?.IsPublic == true || p.GetCustomAttribute<JsonIncludeAttribute>() != null))
                    Walk(p.PropertyType);
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (f.GetCustomAttribute<JsonIncludeAttribute>() != null) Walk(f.FieldType); // fields are saved only when marked
        }
        foreach (var r in SavedRoots) Walk(r);
        var missing = reached.Where(t => !skips.Any(s => (string?)s.Attribute("name") == t.FullName!.Replace('+', '/') && On(s, "skipProperties")))
            .Select(t => t.FullName!.Replace('+', '/')).ToList();
        Check(reached.Count >= 15 && missing.Count == 0,
            $"every type the saved files reach ({reached.Count}) keeps its property names" + (missing.Count > 0 ? "; missing: " + string.Join(", ", missing) : ""));

        // Every type the mod reads from JSON is one of SavedRoots' (or reached from one): a new saved file can't be missed
        // again (the public 0.4.9 lost story.json, usage_plan.json and voice_ledger.json that way).
        // (by name, and for a nested one such as "Data" by the class around it in that file: three types are called Data)
        var loaded = new HashSet<(string name, string file)>();
        var plain = new[] { "string", "int", "double", "bool", "JsonNode", "JsonObject", "JsonArray", "JsonElement", "List" };
        foreach (var f in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f),
                         @"Deserialize<(?:Dictionary<string,\s*)?([A-Za-z_][\w.]*)>?>"))
                if (m.Groups[1].Value.Split('.').Last() is var n && !plain.Contains(n)) loaded.Add((n, f));
        bool Covered((string name, string file) l) => reached.Any(t => t.Name == l.name
            && (t.DeclaringType == null || File.ReadAllText(l.file).Contains("class " + t.DeclaringType.Name, StringComparison.Ordinal)));
        var unknown = loaded.Where(l => !Covered(l)).Select(l => $"{l.name} ({Path.GetFileName(l.file)})").OrderBy(n => n).ToList();
        Check(loaded.Count >= 8 && unknown.Count == 0,
            $"every type read from a saved file ({loaded.Count} in src) is a saved root here" + (unknown.Count > 0 ? "; not covered: " + string.Join(", ", unknown) : ""));

        // Choices written or read by name.
        foreach (var e in new[] { "NivalisAIChat.ApiFormat", "NivalisAIChat.UI.TextSpeed", "NivalisAIChat.Voice.VoiceProvider", "NivalisAIChat.Social.GreetingRules/Kind" })
            Check(skips.Any(s => (string?)s.Attribute("name") == e && On(s, "skipFields")), $"{e} keeps its names (written or read by name)");
    }
}
