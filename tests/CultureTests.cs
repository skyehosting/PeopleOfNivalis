using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NivalisAIChat.Ai;
using NivalisAIChat.Npc;
using NivalisAIChat.Voice;

namespace NivalisAIChat.Tests;

/// <summary>
/// Audit step 7g: under Turkish or Azerbaijani Windows settings, a case-insensitive regex without CultureInvariant no
/// longer matches a capital I ("A CHILD", "As an AI"). Every one now has it. These run the safety-relevant matches under
/// tr-TR within the normal run; the whole suite also runs under any culture with TEST_CULTURE.
/// </summary>
internal static partial class Program
{
    private static void CultureTests()
    {
        Console.WriteLine("-- audit 2026-10-11: matching that doesn't change with the player's Windows language (step 7g)");
        var before = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            foreach (var name in new[] { "tr-TR", "az-Latn-AZ" })
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
                foreach (var said in new[] { "A CHILD", "KID", "MINOR", "UNDER EIGHTEEN", "Is a CHILD." })
                    Check(CanonAge.FromSeen(said, unreadableIsChild: false).child, $"{name}: \"{said}\" reads as under 18");
                var described = new NpcRecord { Id = "p_story", Persona = new Persona { Name = "Ilsa", Age = "" },
                    Visual = new NpcVisual { Named = true, Description = "Is a child. Lives with IRIS." } };
                Check(Minors.WhyChild(described) != null, $"{name}: a story character the game describes as \"Is a child.\" is under 18");
                Check(ReplyParser.LooksOutOfCharacter("As an AI, I cannot pretend to be Oda.", isAndroid: false), $"{name}: \"As an AI...\" is caught as out of character");
                Check(ReplyParser.LooksOutOfCharacter("I'M JUST AN AI ASSISTANT.", isAndroid: false), $"{name}: \"I'M JUST AN AI ASSISTANT\" is caught too");
            }

            // Every case-insensitive pattern kept in a field, in every class built here (ToldCheck, Recall and ChatterBrain's
            // YoungRelation included), is built under tr-TR now and carries CultureInvariant (review of a0fa75f).
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var notInvariant = new List<string>();
            int patterns = 0;
            foreach (var type in typeof(CanonAge).Assembly.GetTypes())
            {
                if (type.Namespace == null || !type.Namespace.StartsWith("NivalisAIChat", StringComparison.Ordinal) || type.Namespace.EndsWith("Tests", StringComparison.Ordinal)
                    || type.ContainsGenericParameters) continue;
                foreach (var f in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(f => f.FieldType == typeof(Regex)))
                {
                    Regex? rx;
                    try { rx = f.GetValue(null) as Regex; } catch { continue; }
                    if (rx == null || !(rx.Options.HasFlag(RegexOptions.IgnoreCase) || rx.ToString().Contains("(?i"))) continue;
                    patterns++;
                    if (!rx.Options.HasFlag(RegexOptions.CultureInvariant)) notInvariant.Add(type.Name + "." + f.Name);
                }
            }
            Check(patterns >= 20 && notInvariant.Count == 0,
                $"tr-TR: every case-insensitive pattern in a field ({patterns}) matches the same in any language" + (notInvariant.Count > 0 ? ": not " + string.Join(", ", notInvariant) : ""));
            Regex Field(Type t, string name) => (Regex)t.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Check(Field(typeof(ChatterBrain), "YoungRelation").IsMatch("Mia — KID SISTER") && Recall.AboutThePast("REMEMBER WHEN I SAID IT?"),
                "tr-TR: \"KID\" is a young relation (nobody under 18 gets a call), and \"REMEMBER\" reaches back");

            // Numbers the AI writes and numbers sent to a voice provider: the same in every culture (review of a0fa75f).
            foreach (var name in new[] { "ar-SA", "he-IL", "fa-IR", "sv-SE" })
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
                var parsed = ReplyParser.Parse("Fine. [[affinity: -3]]", "Oda");
                var structured = ReplyParser.ParseStructured("{\"say\":\"Fine.\",\"mood\":\"content\",\"affinityChange\":\"-4\",\"leaving\":false}", "Oda");
                Check(parsed.AffinityDelta == -3 && structured?.AffinityDelta == -4,
                    $"{name}: a negative affinity the AI writes reads as one ({parsed.AffinityDelta}, {structured?.AffinityDelta})");
                Check(VoiceService.ProsodyRate(0.9) == "-10%" && VoiceService.ProsodyRate(1.2) == "+20%",
                    $"{name}: Azure's speaking rate is written \"-10%\" ({VoiceService.ProsodyRate(0.9)})");
            }
            foreach (var name in new[] { "th-TH", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
                Check(TokenBudget.PeriodKey(new DateTime(2026, 10, 12, 9, 0, 0), TokenBudget.Span.Day) == "day 2026-10-12",
                    $"{name}: the usage period is a Gregorian date ({TokenBudget.PeriodKey(new DateTime(2026, 10, 12, 9, 0, 0), TokenBudget.Span.Day)})");
            }
        }
        finally { (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = before; }
    }
}
