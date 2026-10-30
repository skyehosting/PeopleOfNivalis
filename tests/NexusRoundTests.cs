using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.UI;

namespace NivalisAIChat.Tests;

/// <summary>
/// The Nexus round of 2026-10-17 (CF-11..CF-14): talking on more controller buttons, staff at work keep their jobs,
/// BackgroundModel's own provider, the weather as background, and your staff knowing since when and with whom they work.
/// The game-side parts (hooks, the game's icons) are Unity code: their source is read, and the test sheet checks them.
/// </summary>
internal static partial class Program
{
    private static void NexusRoundTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- the Nexus round (CF-11..CF-14)");
        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string Read(string f) => File.ReadAllText(Path.Combine(src, f));

        // CF-12: more talk buttons, by any of their names.
        var choices = SettingsCatalog.For("Controls", "PadTalkButton", typeof(string)).Choices!;
        Check(choices.Select(PadRepeat.ButtonName).SequenceEqual(new[] { "A", "X", "Y", "B", "LB", "RB", "RT", "LT", "OFF" }),
            "PadTalkButton offers Interact, X (Square), Y (Triangle), B (Circle), LB (L1), RB (R1), RT, LT and off, each read as its button");
        Check(PadRepeat.ButtonName("Square") == "X" && PadRepeat.ButtonName("JoystickButton2") == "X" && PadRepeat.ButtonName("buttonWest") == "X"
              && PadRepeat.ButtonName("Cross") == "A" && PadRepeat.ButtonName("interact") == "A" && PadRepeat.ButtonName("triangle") == "Y"
              && PadRepeat.ButtonName("L1") == "LB" && PadRepeat.ButtonName("R2") == "RT" && PadRepeat.ButtonName("rt") == "RT",
            "...by its PlayStation name, its place, or Unity's JoystickButton number (Rev tried JoystickButton2 and buttonWest)");
        Check(PadRepeat.ButtonName("") == "OFF" && PadRepeat.ButtonName("Start") == "OFF" && PadRepeat.ButtonName("off") == "OFF",
            "anything else is off, never a guess");
        Check(PadRepeat.Label("X", playStation: true) == "Square" && PadRepeat.Label("A", playStation: true) == "X" && PadRepeat.Label("X", playStation: false) == "X",
            "the prompt names the button as the pad shows it (Square on PlayStation)");
        Check(new[] { "A", "B", "X", "Y", "LB", "RB", "LT", "RT" }.All(b => PadRepeat.ControlName(b) != null) && PadRepeat.ControlName("OFF") == null
              && PadRepeat.ControlName("X") == "buttonWest",
            "each button has the Input System name the game's icons are keyed by");
        string lockSrc = Read(Path.Combine("Game", "PlayerLock.cs")), settings = Read("ModRuntime.Settings.cs");
        Check(lockSrc.Contains("PlacementModeUi.OnPlacementMode") && lockSrc.Contains("PadTalkTakes(\"X\")")
              && lockSrc.Contains("TryPickupObjectInFront") && lockSrc.Contains("PadTalkTakes(\"Y\")")
              && lockSrc.Contains("StoreEntity") && lockSrc.Contains("PadTalkTakes(\"B\")"),
            "the press that opens a chat on X, Y or B isn't also the game's placement mode, pick up or put away");
        Check(settings.Contains("if (ChatOpen || _consoleOpen || _menuOpen || _resetting || Time.frameCount == _menuClosedFrame) return false;")
              && settings.Contains("if (!InGameplay() || _target == null) return false;") && settings.Contains("if (_padTalkFrame == Time.frameCount) return true;")
              && Read("ModRuntime.cs").Contains("if (!keyTalk) _padTalkFrame = Time.frameCount;"),
            "...exactly where the talk press acts (review CF12-1, RV-1: never a pause, a menu or the press that closed one; a chat it opened keeps it)");
        Check(settings.Contains("if (console is not (\"LT\" or \"RT\") || !Pad.Pressed(console)) return false;"),
            "the console's button stays LT, RT or off (review CF12-4)");
        string icons = Read(Path.Combine("Game", "PadIcons.cs"));
        Check(icons.Contains("GetDeviceAssetSprite") && icons.Contains("GetSpriteIndexFromName") && icons.Contains("Existing<Nivalis.PlayerInputManager>")
              && !icons.Contains("Singleton<"),
            "the game's own controller icons: its lookup, checked against its sprite sheet, the input manager read safely");

        // CF-11: someone at work keeps their job.
        Check(settings.Contains("if (GameContext.AtWork(target)) return false;") && settings.Contains("if (Pad.Normal(_cfg.PadTalkButton.Value) != \"A\") return false;") && settings.Contains("return _atWork = GameContext.AtWork(c);"),
            "the pad's interact button on someone at work is the game's (their order, their shop)");
        Check(Read("ModRuntime.cs").Contains("if (now != null && (keyTalk || !PadInteractIsTheGames(now)))"),
            "...the talk key still talks to them");
        Check(Read("ModRuntime.Calls.cs").Contains("x => x.Holdable && !GameContext.IsBusyWorking(x.C)")
              && Read("ModRuntime.Chatter.cs").Contains("worker = GameContext.IsWorkerTask(c) || GameContext.OnShift(c);"),
            "phone calls make the busy-working check too, and staff on shift (between two jobs) aren't held");
        Check(WorkLife.InShift(9, 17, 12) && !WorkLife.InShift(9, 17, 20) && WorkLife.InShift(22, 6, 2), "a shift's hours (past midnight too)");
        Check(Social.VenueRoles.IsWorkText("taking a customer's order") && Social.VenueRoles.IsWorkText("on shift, between jobs") && !Social.VenueRoles.IsWorkText("walking home")
              && !Social.VenueRoles.IsWorkText("") && !Social.VenueRoles.IsWorkText(null),
            "at work by what they're doing: the same words as before (review CF11-1: being on shift alone, at lunch or on the way, isn't)");
        var rec = new NpcRecord { Id = "p_mae", Persona = new Persona { Name = "Mae Tran", Age = "29" }, Visual = new NpcVisual { Body = "woman" } };
        var working = new GameSnapshot { GameDay = 12, NpcWorkplace = "Noodle Bar", NpcAtWork = true, Weather = "heavy rain" };
        string atWork = Prompts.SituationBlock(rec, working);
        Check(atWork.Contains(Prompts.AtWorkRule) && Prompts.AtWorkRule.Contains("Never promise to bring something right now"),
            "a chat with someone at work: talking makes nothing happen, no promise to bring something now");
        Check(!Prompts.SituationBlock(rec, new GameSnapshot { GameDay = 12 }).Contains(Prompts.AtWorkRule), "...not said to someone off work");

        // CF-14: the weather is background, and never part of how someone looks for good.
        Check(atWork.Contains(Prompts.WeatherRule) && Prompts.WeatherRule.Contains("only background") && Prompts.WeatherRule.Contains("isn't today"),
            "the weather is background, and a rainy first meeting's appearance isn't today's");
        var woman = new NpcVisual { Body = "woman" };
        var seeds = PersonaSeeds.Roll(new Random(3), woman, new PopulationSettings());
        foreach (var req in new[] { Prompts.PersonaRequest(woman, seeds, new GameSnapshot { Weather = "rain" }, 4000),
                                    Prompts.PersonaRequest(woman, seeds, new GameSnapshot { Weather = "rain" }, 4000, picture: new byte[] { 1 }) })
        {
            string ask = req.Messages[0].Content;
            string look = ask.Split('\n').First(l => l.StartsWith("- appearance:"));
            Check(!look.Contains("weather.") && !look.Contains("(and the weather)") && look.Contains(Prompts.LastingLook),
                "creating someone: their lasting appearance has nothing of today's weather" + (req.Messages[0].Images != null ? " (with a picture)" : ""));
        }
        var chatter = ChatterPrompts.ThinkRequest(rec, new GameSnapshot { Weather = "rain" }, 2);
        Check(string.Join("\n", chatter.System.Concat(chatter.Messages.Select(m => m.Content))).Contains(Prompts.WeatherNote),
            "thinking out loud: the weather is background too");

        // CF-14: since when, and who else works there.
        var st = new WorkLifeState();
        WorkLife.NoteSince(st, null, 30, hiredNow: true);
        Check(st.SinceDay == 30 && st.SinceExact && WorkLife.Since(st, 30).Contains("this is your first day") && WorkLife.Since(st, 30).Contains("Never claim"),
            "seen hired: their first day, said as a fact");
        var old = new WorkLifeState();
        WorkLife.NoteSince(old, new List<WorkEvent> { new() { GameDay = 21, Text = "A long shift." } }, 40, hiredNow: false);
        Check(old.SinceDay == 21 && !old.SinceExact && WorkLife.Since(old, 40).Contains("at least since day 21") && WorkLife.Since(old, 40).Contains("don't name a length of time"),
            "on the staff already when the mod first looked: at least since their oldest work memory, no length made up (review XR-8)");
        WorkLife.NoteSince(old, null, 45, hiredNow: false);
        Check(old.SinceDay == 21, "...worked out once, not moved by later looks");
        WorkLife.NoteSince(old, null, 46, hiredNow: true);
        Check(old.SinceDay == 21 && !old.SinceExact, "review CF14-1: once known, a gap in the staff list doesn't make weeks of work a first day");
        Check(WorkLife.Since(new WorkLifeState(), 10) == "" && WorkLife.Since(null, 10) == "", "not known: nothing said");

        var job = new WorkReading { Day = 30, Place = "Noodle Bar", CoworkersRead = true };
        Check(WorkLife.Coworkers(job) == "You're their only staff member here (a fact).", "the only one on the staff: said as it is");
        job.Coworkers.Add(("p_banor", "Banor"));
        job.Coworkers.Add(("p_x", "Somebody"));
        Check(WorkLife.Coworkers(job) == "2 others work here for them too (a fact)." && !WorkLife.Coworkers(job).Contains("Banor"),
            "before the names are settled, coworkers are counted, never named (a game name never reaches the AI)");
        job.CoworkerNamesSettled = true;
        job.Coworkers[1] = ("p_x", "");
        Check(WorkLife.Coworkers(job) == "Who else works here for them (a fact): Banor, and 1 more you know less well.",
            "settled: those the mod knows are named, anyone else counted");
        var unread = new WorkReading { Day = 30 };
        var lines = WorkLife.Lines(unread, null, st, 30);
        Check(lines.Count > 0 && lines[0].Contains("first day") && !lines.Any(l => l.Contains("only staff member")),
            "the job's lines start with their time here; a staff list that couldn't be read says nothing of coworkers");
        string brain = Read(Path.Combine("Npc", "NpcBrain.cs"));
        Check(brain.Contains("bool nameable = other != null && !IsKnownMinor(other)") && brain.Contains("!KnownMinors.Contains(id) ? _store.Glance(id) : null"),
            "a coworker is named only from a record, never a minor (the minors rule)");
        Check(Read("ModRuntime.WorkLife.cs").Contains("WorkLife.NoteSince(rec.Work, rec.WorkEvents, r.Day, hired.Contains(id))")
              && Read("ModRuntime.WorkLife.cs").Contains("if (hired.Count > 2) hired.Clear();"),
            "a hire is seen between two looks in one session (not the first look, not another save's whole staff)");

        // CF-13: BackgroundModel's own provider.
        var main = new LlmSettings { Format = ApiFormat.OpenAI, Url = "https://api.openai.com/v1", ApiKey = "main-key", Model = "gpt-5.4-nano", BackgroundModel = "small" };
        Check(ReferenceEquals(main.ForRequest(true), main) && ReferenceEquals(main.ForRequest(false), main),
            "no BackgroundApiUrl: everything goes where it went (BackgroundModel at the main provider)");
        main.BackgroundUrl = "http://localhost:11434/v1";
        main.BackgroundApiKey = "";
        main.BackgroundFormat = LlmSettings.FormatOf("Same");
        var bg = main.ForRequest(true);
        Check(bg.Url == "http://localhost:11434/v1" && bg.ApiKey == "" && bg.Model == "small" && bg.Format == ApiFormat.OpenAI && bg.BackgroundUrl == "",
            "BackgroundApiUrl set: background requests go there with its own key (the main key is never sent there)");
        main.ExtraHeaders = "x-org: secret-org";
        Check(main.ForRequest(true).ExtraHeaders == "" && main.ExtraHeaders == "x-org: secret-org" && ReferenceEquals(main.ForRequest(false), main),
            "...nor the main provider's ExtraHeaders (they can hold credentials: review CF13-1)");
        Check(ReferenceEquals(main.ForRequest(false), main) && main.ApiKey == "main-key" && main.Url == "https://api.openai.com/v1",
            "...conversations with you stay on the main provider, unchanged");
        main.BackgroundFormat = LlmSettings.FormatOf("anthropic");
        main.BackgroundModel = "";
        var claude = main.ForRequest(true);
        Check(claude.Format == ApiFormat.Anthropic && claude.Model == "gpt-5.4-nano" && LlmSettings.FormatOf("nonsense") == null,
            "its own format; with BackgroundModel empty, the main model's name is asked for there");
        Check(Read("ModConfig.cs").Contains("{ ApiKey, VoyageKey, ExtraHeaders, BackgroundApiKey, SpeechToTextKey, ImageApiKey }")
              && SettingsCatalog.For("AI Connection", "BackgroundApiKey", typeof(string)).Kind == ControlKind.Secret,
            "BackgroundApiKey is a secret: hidden in Switchboard, never logged");
        Check(LlmClient.EnvironmentKey(ApiFormat.OpenAI, "http://localhost:11434/v1") == "", "an environment key only ever goes to its own provider");

        // The cross-check of 2026-10-17 (XR-2..XR-7).
        string ctx = Read(Path.Combine("Game", "GameContext.cs")), client = Read(Path.Combine("Ai", "LlmClient.cs"));
        Check(!icons.Contains("set.references") && !icons.Contains("refs[i]"), "XR-2: the game's icon table (an array of structs) is never read directly");
        Check(ctx.Contains("r.Coworkers.Add((PersonId(other), \"\"));") && !ctx.Contains("other.DisplayedName"),
            "XR-3: no game name is kept for a coworker (only the mod's own name for someone it knows)");
        Check(lockSrc.Contains("nameof(Nivalis.UI.PlacementModeUi.OnPlacementMode_Context)"), "XR-4: placement mode is skipped at its input callback");
        string cfgSrc = Read("ModConfig.cs");
        Check(cfgSrc.Contains("(new ConfigEntryBase[] { ApiUrl, Format }, new[] { ApiKey, ExtraHeaders })")
              && cfgSrc.Contains("(new ConfigEntryBase[] { BackgroundApiUrl, BackgroundApiFormat }, new[] { BackgroundApiKey })")
              && cfgSrc.Contains("changed while {secret.Definition.Key} isn't in the settings file"),
            "XR-6: a damaged save moving ApiUrl or BackgroundApiUrl doesn't take the old key along");
        Check(cfgSrc.Contains("(new ConfigEntryBase[] { SpeechToTextUrl }, new[] { SpeechToTextKey })")
              && cfgSrc.Contains("(new ConfigEntryBase[] { ImageApiUrl, ImageApiFormat }, new[] { ImageApiKey })"),
            "audit 2026-10-20: ...nor one moving SpeechToTextUrl or ImageApiUrl/ImageApiFormat (the speech and picture keys)");
        Check(client.Contains("_disabled.Add(FeatureKey(url, model, feature))") && !client.Contains("Off(\"") && client.Contains("private bool Off(string url, string model, string feature)"),
            "XR-7: a feature one provider refused stays off for that provider only");
    }
}
