using System;
using System.IO;
using System.Runtime.CompilerServices;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using System.Linq;

namespace NivalisAIChat.Tests;

/// <summary>CF-20 (a player's round): the small UI fixes.</summary>
internal static partial class Program
{
    private static void Cf20UiTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- CF-20: drag handle, Interests, GIVE hint, a second date");
        Check(FoodOffer.MentionsGiving("(give)") && FoodOffer.MentionsGiving("I hand her the noodles") && FoodOffer.MentionsGiving("Here, I'm giving you this")
              && !FoodOffer.MentionsGiving("Forgive me") && !FoodOffer.MentionsGiving("handsome jacket") && !FoodOffer.MentionsGiving(""),
              "giving something is told apart from words that only contain it");
        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string info = File.ReadAllText(Path.Combine(src, "Npc", "InfoSheet.cs"));
        Check(info.Contains("Section(\"Interests\"") && !info.Contains("Section(\"Into\""), "the (i) panel's 'Into' heading reads 'Interests' (it showed as INTO, read as a typo of INFO)");
        string layout = File.ReadAllText(Path.Combine(src, "UI", "ChatWindow.Layout.cs"));
        Check(layout.Contains("Rect(_strip, \"Handle\", new Vector2(0, 1), new Vector2(0, 1)"), "the drag handle sits at the strip's left, above the name, clear of the pills");
        string chat = File.ReadAllText(Path.Combine(src, "ModRuntime.Chat.cs"));
        Check(chat.Contains("FoodOffer.MentionsGiving(") && chat.Contains("_saidHowToGive"), "saying you give them something with no dish in hand: told how GIVE works, once");
        string phone = File.ReadAllText(Path.Combine(src, "UI", "PhoneWindow.Parts.cs"));
        Check(phone.Contains("Over(_urlRt, pos) && (_viewport == null || Over(_viewport, pos))) Focus(_url)") && phone.Contains("if (!_compose.isFocused) _focusIn = 1;")
              && phone.Contains("SetSelectedGameObject(box.gameObject)"), "a click on the wallpaper link box or the text box gives it the keys (CF-15's way)");
        string romance = File.ReadAllText(Path.Combine(src, "ModRuntime.Romance.cs"));
        Check(romance.Contains("already had a date today"), "a second date the same day: you're told it can't happen, instead of nothing");
        Check(romance.Contains("_date == null && st.Romance.Length > 0 && _cfg.DatesPerDay.Value > 0 && day >= 0"), "review R1: only for a couple whose dates today are used up (not a friend, nor dates off)");
        var plan = UsageModel.Best(new[] { new BudgetInput { Key = "STT", Name = "Speech to text", Limit = 600, YoursOnly = true, YoursPerHour = 60, HoursLeft = 10 } },
            new Knobs(), new System.Collections.Generic.HashSet<string>(), 0.5, new System.Collections.Generic.Dictionary<string, double> { ["talk"] = 1 }, 0.1);
        Check(!plan.Limited && plan.Notes.Any(n => n.StartsWith("Only limits of your own use")), "review R5: only your own limits set: the plan says so, not 'no limit'");
    }
}
