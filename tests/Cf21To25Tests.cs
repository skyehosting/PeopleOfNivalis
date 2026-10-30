using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NivalisAIChat.Ai;
using NivalisAIChat.Npc;
using NivalisAIChat.UI;

namespace NivalisAIChat.Tests;

/// <summary>CF-21..CF-25: a beta tester's round (world chat's clock, GIVE, a dialogue log, profiles that note every remark).</summary>
internal static partial class Program
{
    private static void Cf21To25Tests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- CF-21..25: a beta tester's round");
        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string Read(string f) => File.ReadAllText(Path.Combine(src, f));

        // CF-21: world chat's clock, as the chat window's.
        string world = Read("ModRuntime.WorldChat.cs");
        Check(world.Contains("Locks.Engage(_cfg.PauseTimeWhileChatting.Value);") && !world.Contains("Locks.Engage(false)")
              && world.Contains("TimePasses(); // CF-21"),
              "CF-21: world chat stops the clock as the chat window does (PauseTimeWhileChatting), and each answer moves it on (MinutesPerMessage)");

        // CF-25: a remark about the moment isn't a fact about them.
        string[] passing =
        {
            "Ordered the sour broth noodles.", "Chicken noodle soup was fine with me.", "Was only trying to get some soup.",
            "Her food has kept him fed.", "wants extra onions on his noodles", "is starving right now", "came in for a quick bowl",
            "is having the dumplings today",
        };
        string[] lasting =
        {
            "loves noodles", "cooks at a noodle stall", "worried about making rent", "has a sister on the rigs", "can't stand the cold",
            "grew up in the Lower Ring", "wants to open a bar someday",
        };
        Check(passing.All(s => ToldTidy.Passing("other", s)) && passing.All(s => ToldTidy.Passing("likes", s)),
              "CF-25: remarks about the moment (an order, the soup being fine, what they came in for) aren't facts about them (the tester's own examples)");
        Check(lasting.All(s => !ToldTidy.Passing("likes", s) && !ToldTidy.Passing("other", s)) && !ToldTidy.Passing("age", "22 today"),
              "...who they are stays: what they love, their work, their worries, their past (and their age, however it's put)");
        // Review CF25-1: lasting facts with words a passing remark uses too are kept.
        Check(!ToldTidy.Passing("job", "takes orders at the noodle bar") && !ToldTidy.Passing("job", "orders supplies for the bar")
              && !ToldTidy.Passing("likes", "loves having a drink after work") && !ToldTidy.Passing("background", "was good at football as a kid")
              && !ToldTidy.Passing("other", "is here for her sister") && !ToldTidy.Passing("other", "got married today") && !ToldTidy.Passing("goals", "wants more kids")
              && !ToldTidy.Passing("other", "came here for work") && !ToldTidy.Passing("likes", "this time of year") && !ToldTidy.Passing("home", "lives right by the market")
              && ToldTidy.Passing("other", "is eating noodles") && ToldTidy.Passing("likes", "asked for extra chilli"),
              "review CF25-1: a job, the past, why they're here are kept, whatever words they use; only interests, likes and 'other' are looked at");
        var facts = new List<string> { "likes: loves noodles", "other: Ordered the sour broth noodles.", "job: cooks at a noodle stall" };
        for (int i = 0; i < 9; i++) facts.Add($"other: keeps pigeons number {i}");
        var kept = ToldTidy.Lasting(facts);
        Check(!kept.Any(f => f.Contains("sour broth")) && kept.Contains("likes: loves noodles") && kept.Contains("job: cooks at a noodle stall")
              && kept.Count(f => f.StartsWith("other:")) == ToldTidy.OtherKept && kept.Last() == "other: keeps pigeons number 8",
              "...what goes back into their prompt leaves them out, and 'other' keeps only its newest few (no more fixating)");
        var rec = new NpcRecord { Id = "npc_ivo" };
        rec.State.Told.Add(new ToldItem { Topic = "other", Said = "Ordered the sour broth noodles.", GameDay = 3 });
        rec.State.Told.Add(new ToldItem { Topic = "likes", Said = "loves noodles", GameDay = 3 });
        var sheet = InfoSheet.Build(rec, null, "Ivo", "Man", "");
        Check(!sheet.Sections.Any(s => s.heading == "Other things they've mentioned") && sheet.Sections.Any(s => s.heading == "Interests" && s.items.Contains("loves noodles")),
              "...and their profile doesn't show them, saved before the fix or not (nothing in the file is deleted)");
        NpcBrain.ApplyTold(rec, new[] { ("other", "Chicken noodle soup was fine with me."), ("fears", "worried about making rent") }, 4, afterMeeting: false);
        Check(!rec.State.Told.Any(t => t.Said.Contains("soup was fine")) && rec.State.Told.Any(t => t.Said == "worried about making rent")
              && !rec.Persona.EstablishedFacts.Any(f => f.Contains("soup")),
              "...nor is a new one kept");
        string prompts = Read(Path.Combine("Ai", "Prompts.cs"));
        Check(prompts.Contains("never what's just \" +") && prompts.Split("+ LastingRule").Length - 1 == 4 && prompts.Contains("var facts = Npc.ToldTidy.Lasting(p.EstablishedFacts);"),
              "...and the AI is asked for lasting facts only, in each of the four places it notes them");

        // CF-24: /heard.
        var heard = new HeardLog(5);
        heard.Add("9:41 PM", "Mara", "Rough night?");
        heard.Add("9:41 PM", "Mara", "Rough night?");
        heard.Add("9:42 PM", "", "Hm.");
        for (int i = 0; i < 6; i++) heard.Add("9:43 PM", "Ivo", "line " + i);
        var last = heard.Last(10);
        Check(heard.Count == 5 && last.Last().Text == "line 5" && HeardLog.Format(new HeardLog.Line("9:41 PM", "Mara", "Rough night?")) == "9:41 PM  Mara: Rough night?",
              "CF-24: the subtitles shown are kept for /heard (newest last, a bubble shown again not twice, the oldest let go)");
        var two = new HeardLog(); two.Add("", "", "Hello.", "chatter:a"); two.Add("", "", "Hello.", "chatter:b"); two.Add("", "", "Hello.", "chatter:a");
        Check(two.Count == 2, "review CF24-2: two strangers saying the same thing are both kept (told apart by their bubble), one bubble shown again isn't");
        Check(Read("ModRuntime.Social.cs").Contains("heard: false); // review CF24-1") && Read("ModRuntime.cs").Contains("Flat(at.position - pc.transform.position).magnitude > carries) return;"),
              "review CF24-1/2: a child's line is never in /heard, nor a line too far off to hear");
        var one = new HeardLog(); one.Add("", "", "Hm.");
        Check(one.Last(1)[0].Speaker == "Someone" && Read(Path.Combine("UI", "ChatWindow.Extras.cs")).Contains("Heard?.Invoke(speaker ?? \"\", line ?? \"\", key ?? \"\", anchor, maxDistance)")
              && Read("ModRuntime.Commands.cs").Contains("case \"/heard\":") && Read("ModRuntime.Commands.cs").Contains("HeardLines.Clear();")
              && Read("ModRuntime.Saves.cs").Contains("HeardLines.Clear();"),
              "...every subtitle goes in; /heard shows them; a reset or another save empties it");

        // CF-22: what you're holding, in the log.
        string chat = Read("ModRuntime.Chat.cs");
        Check(chat.Contains("string held = GameContext.HeldDescription();") && chat.Contains("GIVE only shows for a plated dish.")
              && Read(Path.Combine("Game", "GameContext.cs")).Contains("(not a plated dish: no meal on it)"),
              "CF-22: opening a chat holding something, the log says what it is and whether GIVE can hand it over");
    }
}
