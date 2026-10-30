using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Nivalis;
using NivalisAIChat.Ai;
using NivalisAIChat.Dev;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using NivalisAIChat.UI;
using NivalisAIChat.Voice;
using UnityEngine;
using UnityEngine.InputSystem;


namespace NivalisAIChat;

/// <summary>Chat commands typed into the chat box: /help, /resetnpc, /resetall.</summary>
public static partial class ModRuntime
{
    // ------------------------------------------------------------------ chat commands (/help, /resetnpc, /resetall)

    /// <summary>A command typed in a conversation: /resetnpc is about this person; the rest are the console's commands.</summary>
    private static void RunCommand(string text)
    {
        try
        {
            string cmd = text.Split(' ', 2)[0].ToLowerInvariant();
            if (cmd == "/resetnpc") { ResetCurrentNpc(); return; }
            if (cmd == "/memories") { MemoriesCommand(); return; }
            if (cmd == "/forget") { ForgetCommand(text.Split(' ', 2).ElementAtOrDefault(1) ?? ""); return; }
            Command(text, line => Ui.AddLine(LineKind.Notice, "", line));
        }
        catch (Exception e) // as the console does: the window says so, instead of the error escaping the frame (#14b)
        {
            Ui.AddLine(LineKind.Error, "", "That command failed: " + e.Message);
            Log.Warn("Chat-box command failed: " + e);
        }
    }

    /// <summary>
    /// The commands shared by the console (` key) and the chat box: /help, /chatter (and all its settings), /mumble,
    /// /resetall. Settings changed here are saved to the settings file straight away.
    /// </summary>
    private static void Command(string text, Action<string> say)
    {
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
        string arg = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
        switch (cmd)
        {
            case "/help":
            case "/?":
                foreach (var l in HelpLines()) say(l);
                break;
            case "/chatter":
                ChatterCommand(parts.Skip(1).Select(x => x.ToLowerInvariant()).ToArray(), say);
                break;
            case "/mumble":
                SetPercent(_cfg.MumbleChance, sub, say, "Thinking out loud: chance per roll");
                break;
            case "/heard":
            case "/subtitles":
            {
                // CF-24 (a tester: "pls add dialogue log because sometimes I miss over the head subtitles").
                int n = int.TryParse(sub, out int asked) ? Math.Clamp(asked, 1, 80) : 25;
                var heard = HeardLines.Last(n);
                if (heard.Count == 0) { say("Nothing said over anyone's head yet this session."); break; }
                say($"What was said over people's heads (the last {heard.Count}, newest last; /heard 80 for more):");
                foreach (var h in heard) say(UI.HeardLog.Format(h));
                break;
            }
            case "/errors":
            case "/error":
                if (sub == "clear") { ClearProblems(); say("Cleared."); break; }
                var problems = ProblemLines(unseenOnly: false);
                if (problems.Count == 0) { say("Nothing has gone wrong this session."); break; }
                say($"Problems this session (newest last, up to {ProblemsKept}). The code in brackets says what kind:");
                foreach (var line in problems) say(line);
                say("/errors clear empties the list. The full details are in BepInEx\\LogOutput.log.");
                break;
            case "/instructions":
                InstructionsCommand(say);
                break;
            case "/settings":
            case "/menu":
                // Next frame, as the cog does it: the window this was typed in waits underneath and comes back on close.
                if (!MenuInstalled) { say(NoMenu); break; }
                say("Opening the settings (the cog beside this window opens them too; every change there is saved at once).");
                MainThread.Enqueue(() => OpenSettings(null));
                break;
            case "/reload":
            {
                var changed = ReloadSettings(out string? error);
                if (error != null) say($"The settings file couldn't be read ({error}). Nothing from it was applied.");
                else
                    say(changed.Count == 0 ? "Settings read again from the file: nothing had changed."
                        : $"Settings read again from the file; now in effect: {string.Join(", ", changed)}.");
                if (changed.Contains("UiScale")) say("The window's new size shows the next time it opens.");
                break;
            }
            case "/memory":
            case "/memories":
            {
                if (sub == "reindex" && _memory != null && VoyageOn)
                {
                    var ids = _store.Ids();
                    _memory.QueueAll(ids);
                    say($"Checking everyone's memories ({ids.Count} people) for anything not yet indexed, in the background.");
                    break;
                }
                var (people, mems) = _memory != null ? _memory.Store.Count() : (0, 0);
                say(VoyageOn
                    ? $"Memories are found by meaning (Voyage, {Redact.Value(_cfg.VoyageModel.Value)}): {mems} memories of {people} people indexed, on your PC; " +
                      $"{_memory?.Waiting ?? 0} people waiting to be indexed; {_memory?.Added ?? 0} added this session; Voyage tokens this session: {_voyage?.TokensUsed ?? 0:N0}."
                    : "Memories are found by matching words (MemorySearch = Local). Set MemorySearch = Voyage and a VoyageKey to find them by meaning.");
                if (_memory?.LastError is { Length: > 0 } err) say("Last problem: " + err);
                if (VoyageOn)
                    say(_cfg.MemoryIndexLimit.Value > 0
                        ? $"Each person's index keeps their {_cfg.MemoryIndexLimit.Value} newest memories (MemoryIndexLimit). Searched by meaning only when the past comes up."
                        : "Each person's index keeps everything (MemoryIndexLimit 0). Searched by meaning only when the past comes up.");
                say($"Older memories fade: {(_cfg.RecallFades.Value ? "on" : "off")} (RecallFades). /memory reindex checks everyone again.");
                break;
            }
            case "/usage":
            case "/tokens":
                foreach (var l in UsageReport()) say(l); // FB-51: a heading per budget, a line per figure, in colour
                break;
            case "/voices":
            case "/voice":
                say("Asking your voice providers what they offer your account...");
                // The answer can take a while: it goes only to the window that asked, if that's still open (RACE-11).
                int askedInConsole = _consoleOpen ? _consoleSession : -1, askedInChat = ChatOpen ? _openSerial : -1;
                Task.Run(async () =>
                {
                    List<string> lines;
                    try { lines = await _voices.ReportAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception e) { lines = new List<string> { "Couldn't list the voices: " + e.Message }; }
                    MainThread.Enqueue(() =>
                    {
                        bool stillThere = (askedInConsole >= 0 && _consoleOpen && _consoleSession == askedInConsole)
                                          || (askedInChat >= 0 && ChatOpen && _openSerial == askedInChat);
                        if (stillThere) foreach (var l in lines) say(l);
                        Log.Info("/voices:\n" + string.Join("\n", lines));
                    });
                });
                break;
            case "/profile" or "/profiles": // CF-20: AI profiles
                ProfileCommand(text, say);
                break;
            case "/nearby":
                NearbyCommand(say);
                break;
            case "/probe": // research: what the game lets the mod do (Dev/CapabilityProbe.cs; nothing is saved)
                Dev.CapabilityProbe.Run(text.Trim().Length > 6 ? text.Trim()[6..] : "", say);
                break;
            case "/chatwindow": // the chat window back to its usual place and size
                if (sub == "reset") { Ui.ResetLayout(); _cfg.ChatWindowLayout.Value = ""; say("The chat window is back in its usual place and size."); }
                else say("/chatwindow reset: the chat window back to its usual place and size. Move it by its top strip, resize it by its corner.");
                break;
            case "/phone": // the phone: the signal here and why (for PhoneWeakPlaces), contacts
                PhoneCommand(say);
                break;
            case "/nightline": // FB-71: where Nightline stands; "now" (someone opens it now), "shots clear"
                NightlineCommand(text.Trim().Length > 10 ? text.Trim()[10..].Trim() : "", say);
                break;
            case "/inner": // the inner-life round: what people feel, have on their minds and saw (read only)
                InnerCommand(text.Trim().Length > 6 ? text.Trim()[6..] : "", say);
                break;
            case "/stopfollow":
                StopFollowCommand(say);
                break;
            case "/reminders":
                RemindersCommand(sub, say);
                break;
            case "/saves":
                SavesCommand(say);
                break;
            case "/story":
                StoryCommand(say);
                break;
            case "/transcripts":
            case "/transcript":
                TranscriptsCommand(sub, arg, say);
                break;
            case "/rings":
            case "/ring":
                RingsCommand(sub, arg, say);
                break;
            case "/sight":
                _sightOn = sub switch { "on" => true, "off" => false, _ => !_sightOn };
                say(_sightOn
                    ? $"Sight triangles on: people within 15 m, green when they can see you, grey when not (SightDistance {_cfg.SightDistance.Value:0.#} m, SightAngle {_cfg.SightAngle.Value:0}). /sight off to hide them."
                    : "Sight triangles off.");
                break;
            case "/resetnpc":
                say("/resetnpc works in a conversation: talk to someone, then type it there.");
                break;
            case "/resetall":
                if (Time.unscaledTime - _resetAllAskedAt > 20f)
                {
                    _resetAllAskedAt = Time.unscaledTime;
                    say("This makes EVERYONE in Nivalis a stranger again: every person, conversation, memory, relationship, rumour and voice. " +
                        "Your settings stay. Type /resetall again to confirm.");
                }
                else
                {
                    _resetAllAskedAt = -100f;
                    ResetEveryone();
                }
                break;
            default:
                say($"There's no {cmd} command. Type /help for the list.");
                break;
        }
    }

    private static void InstructionsCommand(Action<string> say)
    {
        string text = CustomInstructions.Text;
        string where = CustomInstructions.FilePath;
        if (text.Length == 0)
        {
            say("No instructions of your own are loaded. Write them at the top of this file (lines starting with # are ignored):");
            say(where);
        }
        else
        {
            say($"Your instructions are loaded ({text.Length} characters{(CustomInstructions.WasCut ? $", cut at the {CustomInstructions.MaxChars}-character limit" : "")}) " +
                "and given to everyone who talks or is created. From:");
            say(where);
            foreach (var line in text.Split('\n').Take(12)) say("  " + line);
            if (text.Split('\n').Length > 12) say("  …");
        }
        // The mod's own instructions, kept in the same file as fields you can change or empty.
        say("The mod's own instructions (the fields lower down in that file):");
        foreach (var (name, state) in CustomInstructions.FieldStates()) say($"  {name}: {state}");
    }

    /// <summary>CF-24: the subtitles shown this session (and save), for /heard.</summary>
    private static readonly UI.HeardLog HeardLines = new();

    private static IEnumerable<string> HelpLines()
    {
        var s = ChatterSettingsAsSet();
        string key = _cfg.ConsoleKey.Value.Trim().Equals("Backquote", StringComparison.OrdinalIgnoreCase) ? "`" : _cfg.ConsoleKey.Value.Trim();
        yield return $"Commands (type them here, in a conversation, or in the console: the {key} key):";
        yield return "/help: this list";
        yield return MenuInstalled
            ? "/settings: every setting, in Nivalis Switchboard (also the cog beside this window, its own key, or its button in the game's pause menu)"
            : "/settings: where the settings are (an in-game menu comes with Nivalis Switchboard, a separate mod)";
        yield return "/resetnpc: (in a conversation) this person forgets you completely; next time they're someone new";
        yield return "/memories: (in a conversation) what this person keeps about you, numbered  ·  /forget N: they forget that one";
        yield return "/resetall: everyone in Nivalis forgets you (type it twice). Your settings stay.";
        yield return "/inner: how the people near you feel, what's on their minds, what they've seen  ·  /inner <name>: one person";
        yield return "/heard: the subtitles you might have missed (what was said over people's heads, newest last)  ·  /heard 80: more"; // CF-24
        yield return "/chatwindow reset: the chat window back to its usual place and size (drag its top to move it, its corner to resize it)";
        yield return "/phone: your phone's signal here and why (the place and scene names PhoneWeakPlaces matches), your contacts";
        yield return "/nightline: the city's social app (lines kept, today's posts and photos, street views); /nightline now, /nightline shots clear";
        yield return "/profile: your saved AI connections  ·  /profile save NAME, /profile use NAME, /profile delete NAME";
        yield return "/errors: what went wrong this session, with a code for each (AI-KEY, AI-MODEL, PIC-UNSUPPORTED...)  ·  /errors clear";
        yield return "/instructions: your own rules for every character (custom_instructions.txt): whether they're loaded, and where the file is";
        yield return "/voices: every voice provider, with the voices it offers your account listed under it (and any it can't use right now)";
        yield return $"/chatter: everything about residents' chatter, as it's set now ({(s.Enabled ? "on" : "off")})";
        yield return "/chatter on  |  off  ·  /chatter stop: end everything going on";
        yield return $"/chatter mode dynamic|advanced (now {(s.Dynamic ? "Dynamic" : "Advanced")})";
        yield return $"/chatter dynamic every <time> ({ChatterSettings.Pretty(s.DynamicEverySeconds)}) · npcs <1-{ChatterSettings.MaxDynamicNpcs}> ({s.DynamicMaxNpcs}, most people talking near you) · lines <n> ({s.DynamicLines} each in a conversation) · phone <n> ({s.DynamicPhoneLines} a side) · think <n> ({s.DynamicThinkLines} muttered)";
        yield return $"/chatter dynamic starts <1-{DynamicChatter.MaxStarts}> (1 to {s.DynamicStarts} each time) · stagger <seconds> ({s.DynamicStagger:0.#}) · " +
                     $"weight gossip|shop|phone|think <n> (now {s.WeightGossip}/{s.WeightShop}/{s.WeightPhone}/{s.WeightThink})";
        yield return "Advanced mode only: three kinds, each with every <time> (how often it's rolled: 30s, 2m, 1h) · chance <0-100> · max <n> (at once) · lines <n>";
        yield return $"/chatter groups ...: residents talking (now every {ChatterSettings.Pretty(s.CheckSeconds)}, {s.ChancePercent}%, max {s.MaxGroups}, {s.ClampedLines} lines each)";
        yield return $"   also: size <2-4> ({s.ClampedGroupSize}) · join <0-100> ({s.JoinPercent}%) · known <0-100> ({s.KnownPairPercent}%) · walk on|off ({(s.WalkUp ? "on" : "off")})";
        yield return $"/chatter tablecall <0-100>: how often a table conversation gets a phone call (now {_cfg.TableCallChance.Value}%)";
        yield return $"/chatter orderai on|off: the AI writes staff taking an order, in their own words (now {(_cfg.AiOrderTalk.Value ? "on" : "off")}; order talk itself: {(_cfg.OrderTalk.Value ? "on" : "off")})";
        yield return $"/chatter phone ...: phone calls (now every {ChatterSettings.Pretty(s.PhoneEverySeconds)}, {s.PhonePercent}%, max {s.PhoneMax}, {s.ClampedPhoneLines} lines a side)";
        yield return $"/chatter think ...: thinking out loud (now every {ChatterSettings.Pretty(s.ThinkEverySeconds)}, {s.MumblePercent}%, max {s.ThinkMax}, {s.ClampedThinkLines} lines)";
        yield return "   e.g. /chatter groups every 20s  ·  /chatter phone chance 50  ·  /chatter think lines 3";
        yield return "/chatter now · /chatter call · /chatter think now: start one right away";
        yield return $"/chatter hourly <n>: most per hour, all kinds together, the cap on AI use (now {s.PerHour})";
        yield return $"/chatter range <m>: the chatter ring, how far from you they start (now {Rings.M(s.Range)})  ·  voice <m>: the hearing ring, heard within, fading with distance (now {Rings.M(s.VoiceDistance)})  ·  text <m>: words shown within (now {(s.TextDistance > 0 ? Rings.M(s.TextDistance) : "any distance")})";
        yield return $"/chatter beyond <m>: also voiced this far past the hearing ring, silent until you come closer (now {Rings.M(s.VoiceBeyond)}); see /rings";
        yield return $"/chatter game on|off: silence the game's own background chatter while ours is on (now {(s.ReplaceGameChatter ? "on" : "off")})";
        yield return "/usage: AI tokens and voice characters used this hour/day/month, and the limits (TokenLimit, VoiceLimit in the config)";
        yield return "/memory: how memories are found (by words, or by meaning with Voyage), how many are indexed  ·  /memory reindex";
        yield return "/reload: read the settings file again (after editing it outside the game; AutoReloadSettings does it by itself)";
        yield return "/transcripts on|off · export (this session) · export all: keep a transcript of everything said, with what shaped each line (for research)";
        yield return "/sight on|off: draw who can see you (people only wave when you're inside their triangle: SightDistance, SightAngle)";
        yield return $"/rings on|off: draw your three rings at your feet (now {CurrentRings()}) · /rings hearing|voices|chatter <m|+|-> · /rings scale <m|+|->: the outer ring, the others scale with it (0.25 m steps)";
        yield return "/nearby: everyone within 10 m: what the game has them doing, their role at a food place, and what they're in (for checking order talk)";
        yield return "/story: the story conversations kept for each story character (only what you've seen), and who has a way of talking written";
        yield return "/saves: which save you're playing and the memory kept with each save (MemoriesPerSave)";
        yield return "/reminders: plans and reminders coming up (made in chats); /reminders clear empties the list";
        yield return "/stopfollow: everyone following you stops (or tell them in a chat)";
    }

    private static void ChatterCommand(string[] a, Action<string> say)
    {
        string sub = a.Length > 0 ? a[0] : "", arg = a.Length > 1 ? a[1] : "", arg2 = a.Length > 2 ? a[2] : "";
        switch (sub)
        {
            case "":
            case "status":
            {
                var s = ChatterSettingsAsSet();
                var now = s.Effective();
                int hour = ChatterPlan.InLastHour(ChatterStarts, Time.unscaledTime);
                say($"Residents' chatter is {(s.Enabled ? "ON" : "OFF")}. Started this hour: {hour} of {s.PerHour} (all kinds).");
                if (s.Dynamic)
                {
                    say($"Mode: Dynamic. Every {ChatterSettings.Pretty(now.DynamicEverySeconds)}, one kind at random (gossip, a phone call, thinking out loud) with someone near you who fits, " +
                        $"while fewer than {now.DynamicMaxNpcs} people are talking near you (now {PeopleTalking()}; anyone wrapping up doesn't count). Lines: {now.ClampedLines} each in a conversation, {now.ClampedPhoneLines} a side on the phone, {now.ClampedThinkLines} muttered.");
                    say($"Each time, 1 to {Math.Clamp(now.DynamicStarts, 1, DynamicChatter.MaxStarts)} start, up to {now.DynamicStagger:0.#} s apart. " +
                        $"Weights: gossip {now.WeightGossip}, shop talk {now.WeightShop}{(_cfg.OrderTalk.Value ? "" : " (OrderTalk is off)")}, phone calls {now.WeightPhone}, muttering {now.WeightThink}.");
                    say("Going on now: " + (Groups.Count + Calls.Count + Thoughts.Count == 0 ? "nothing." :
                        $"{Groups.Count} conversation(s){(Groups.Any(g => g.WrappingUp) ? $" ({Groups.Count(g => g.WrappingUp)} wrapping up)" : "")}, {Calls.Count} call(s), {Thoughts.Count} thinking out loud."));
                    say("The Advanced settings (below) apply only in Advanced mode (/chatter mode advanced):");
                }
                else say("Mode: Advanced (each kind on its own timer, chance and pool). /chatter mode dynamic picks at random as you walk around.");
                say($"Conversations: every {ChatterSettings.Pretty(s.CheckSeconds)}, {s.ChancePercent}% chance, up to {s.MaxGroups} at once (now {Groups.Count}), {s.ClampedLines} lines each. " +
                    $"Groups of up to {s.ClampedGroupSize}, {s.JoinPercent}% join, tables {s.TablePercent}%, known pairs {s.KnownPairPercent}%, walking over {(s.WalkUp ? "on" : "off")}.");
                say($"Phone calls: every {ChatterSettings.Pretty(s.PhoneEverySeconds)}, {s.PhonePercent}% chance, up to {s.PhoneMax} at once (now {Calls.Count}), {s.ClampedPhoneLines} lines a side.");
                say($"Thinking out loud: every {ChatterSettings.Pretty(s.ThinkEverySeconds)}, {s.MumblePercent}% chance, up to {s.ThinkMax} at once (now {Thoughts.Count}), {s.ClampedThinkLines} lines.");
                say($"They start within {Rings.M(s.Range)} of you (the chatter ring); heard within {Rings.M(s.VoiceDistance)} (voiced {Rings.M(s.VoiceBeyond)} past that, fading in as you come closer); " +
                    $"they wrap it up past {Rings.M(s.WrapUpDistance)}; words shown within {(s.TextDistance > 0 ? Rings.M(s.TextDistance) : "any distance")} (/rings shows them). " +
                    $"The game's own chatter: {(s.ReplaceGameChatter ? "silenced while this is on" : "also on")}. Pairs remembered: {_pairs?.Count ?? 0}; groups: {_circles?.Count ?? 0}.");
                say($"Order talk at food places: {(_cfg.OrderTalk.Value ? "on" : "off")}; written by the AI: {(_cfg.AiOrderTalk.Value ? "on" : "off")} " +
                    $"({ChatterPlan.InLastHour(OrderTalkAiStarts, Time.unscaledTime)} this hour, at most {Math.Max(1, s.PerHour / 3)}).");
                if (_budget != null && _budget.Limit > 0) say(_budget.Describe());
                say("Type /help for every setting.");
                break;
            }
            case "on":
                _cfg.Chatter.Value = true;
                _nextGroupRoll = _nextPhoneRoll = _nextThinkRoll = Time.unscaledTime + 5f;
                _nextDynamicLook = Time.unscaledTime + 3f;
                say("Residents' chatter is ON (saved). Something may start within the next roll or two; /chatter now starts a conversation right away.");
                break;
            case "off":
                _cfg.Chatter.Value = false;
                EndAllChatter("switched off");
                say("Residents' chatter is OFF (saved).");
                break;
            case "now":
                if (!_cfg.Chatter.Value) { say("Chatter is off. Turn it on first: /chatter on"); break; }
                say(ChatterNow());
                break;
            case "stop":
                int n = Groups.Count + Calls.Count + Thoughts.Count;
                EndAllChatter("stopped");
                say(n == 0 ? "Nobody's talking right now." : $"Ended {n} conversation(s), call(s) and thought(s).");
                break;
            case "call":
                StartNow(say, "phone");
                break;
            case "groups":
            case "group":
                if (Whole(arg, out _)) { SetInt(_cfg.ChatterMaxGroups, arg, 0, ChatterSettings.MaxAtOnce, say, "Conversations at once"); break; } // older "/chatter groups <n>"
                KindSetting("groups", arg, arg2, say);
                break;
            case "phone":
                if (Whole(arg.TrimEnd('%'), out _)) { SetPercent(_cfg.PhoneChance, arg, say, "Phone calls: chance per roll"); break; } // older "/chatter phone <n>"
                if (arg == "now") { StartNow(say, "phone"); break; }
                KindSetting("phone", arg, arg2, say);
                break;
            case "think":
                if (arg is "" or "now") { StartNow(say, "think"); break; }
                KindSetting("think", arg, arg2, say);
                break;
            // older names for the conversation settings
            case "every": case "lines": case "max": case "npcs":
                if (ChatterSettingsAsSet().Dynamic) DynamicSetting(sub, arg, say);
                else if (sub != "npcs") KindSetting("groups", sub, arg, say);
                else say("/chatter dynamic npcs is for Dynamic mode (/chatter mode dynamic). Advanced: /chatter groups max <n>.");
                break;
            case "chance": KindSetting("groups", sub, arg, say); break;
            case "dynamic": DynamicSetting(arg, arg2, say, a.Length > 3 ? a[3] : ""); break;
            case "size": SetInt(_cfg.ChatterGroupSize, arg, 2, 4, say, "Biggest group"); break;
            case "join": SetPercent(_cfg.ChatterJoinChance, arg, say, "Join chance"); break;
            case "tablecall": case "tablecalls": SetPercent(_cfg.TableCallChance, arg, say, "Phone calls at a table (percent of table conversations)"); break;
            case "orderai":
                if (arg is "on" or "off") { _cfg.AiOrderTalk.Value = arg == "on"; say($"Order talk written by the AI: {arg} (saved).{(arg == "on" && !_cfg.OrderTalk.Value ? " Order talk itself is off (OrderTalk in the settings file)." : "")}"); }
                else say($"/chatter orderai on | off. Now {(_cfg.AiOrderTalk.Value ? "on" : "off")}.");
                break;
            case "known": SetPercent(_cfg.ChatterKnownPairs, arg, say, "Known pairs"); break;
            case "tables": case "table": say("People sharing a table now always talk as their table, and a table near you goes first (0.3.2): nothing to set."); break;
            case "walk":
                if (arg is "on" or "off") { _cfg.ChatterWalkUp.Value = arg == "on"; say($"Walking over: {arg} (saved)."); }
                else say($"/chatter walk on | off. Now {(_cfg.ChatterWalkUp.Value ? "on" : "off")}.");
                break;
            case "hourly": SetInt(_cfg.ChatterPerHour, arg, 0, 500, say, "Most per hour (all kinds)"); break;
            case "range": SetMetres(_cfg.ChatterRange, arg, 5f, 60f, say, "Range"); break;
            case "voice": SetMetres(_cfg.VoiceDistance, arg, 0f, 100f, say, "Heard within (0 = no voices)"); break;
            case "beyond": SetMetres(_cfg.VoiceBeyond, arg, 0f, 30f, say, "Also voiced past that (silent until you come closer)"); break;
            case "text": SetMetres(_cfg.TextDistance, arg, 0f, 100f, say, "Words shown within (0 = any distance)"); break;
            case "mode":
                if (arg is "dynamic" or "advanced")
                {
                    _cfg.ChatterMode.Value = arg == "dynamic" ? "Dynamic" : "Advanced";
                    var d = ChatterSettingsNow();
                    say(arg == "dynamic"
                        ? $"Chatter mode: Dynamic (saved). Every {ChatterSettings.Pretty(d.DynamicEverySeconds)}, something new may start near you, up to {d.DynamicMaxNpcs} people talking at once (/chatter dynamic every|npcs|lines)."
                        : "Chatter mode: Advanced (saved). Each kind runs on its own timer, chance and pool (/chatter groups|phone|think ...).");
                }
                else say($"/chatter mode dynamic | advanced. Now {(ChatterSettingsAsSet().Dynamic ? "Dynamic" : "Advanced")}.");
                break;
            case "game":
                if (arg is "on" or "off") { _cfg.ReplaceGameChatter.Value = arg == "on"; say($"Silencing the game's own chatter while ours is on: {arg} (saved)."); }
                else say($"/chatter game on | off. Now {(_cfg.ReplaceGameChatter.Value ? "on" : "off")}.");
                break;
            default:
                say($"There's no /chatter {sub}. Type /help for the list.");
                break;
        }
    }

    /// <summary>"/chatter groups|phone|think every|chance|max|lines value".</summary>
    private static void KindSetting(string kind, string what, string value, Action<string> say)
    {
        string name = kind switch { "phone" => "Phone calls", "think" => "Thinking out loud", _ => "Conversations" };
        switch (what)
        {
            case "every":
            {
                var entry = kind switch { "phone" => _cfg.PhoneEvery, "think" => _cfg.ThinkEvery, _ => _cfg.GroupEvery };
                if (ChatterSettings.Interval(value) is not { } secs) { say($"{name}: how often it's rolled, like 30s, 2m or 1h. Now {entry.Value}."); return; }
                entry.Value = ChatterSettings.Pretty(secs);
                if (kind == "phone") _nextPhoneRoll = Math.Min(_nextPhoneRoll, Time.unscaledTime + secs);
                else if (kind == "think") _nextThinkRoll = Math.Min(_nextThinkRoll, Time.unscaledTime + secs);
                else _nextGroupRoll = Math.Min(_nextGroupRoll, Time.unscaledTime + secs);
                say($"{name}: rolled every {entry.Value} (saved).");
                return;
            }
            case "chance":
                SetPercent(kind switch { "phone" => _cfg.PhoneChance, "think" => _cfg.MumbleChance, _ => _cfg.ChatterChance }, value, say, $"{name}: chance per roll");
                return;
            case "max":
                SetInt(kind switch { "phone" => _cfg.PhoneMaxActive, "think" => _cfg.ThinkMaxActive, _ => _cfg.ChatterMaxGroups }, value, 0, ChatterSettings.MaxAtOnce, say, $"{name}: at once");
                return;
            case "lines":
                if (kind == "think") SetInt(_cfg.ThinkLinesEach, value, 1, ChatterSettings.MaxThinkLines, say, $"{name}: lines in one go");
                else SetInt(kind == "phone" ? _cfg.PhoneLinesEach : _cfg.ChatterLinesEach, value, 1, ChatterSettings.MaxLinesEach, say, kind == "phone" ? $"{name}: lines a side" : $"{name}: lines each");
                return;
            default:
                say($"/chatter {kind} every <time> | chance <0-100> | max <n> | lines <n>");
                return;
        }
    }

    /// <summary>/chatter call, /chatter think: one right away (within the hourly cap and the pool).</summary>
    private static void StartNow(Action<string> say, string kind)
    {
        if (_chatter == null) { say("Chatter isn't available."); return; }
        var s = ChatterSettingsNow();
        if (!s.Enabled) { say("Residents' chatter is off. /chatter on first."); return; }
        if (ChatterPlan.InLastHour(ChatterStarts, Time.unscaledTime) >= s.PerHour) { say($"This hour's limit ({s.PerHour}) is used up. Raise it with /chatter hourly <n>."); return; }
        if (kind == "phone")
            say(TryPhoneCall(s, out var noCall) ? "Someone near you is about to be on the phone (close the console to watch)." : "No phone call: " + noCall);
        else
            say(TryThink(s, out var noThought) ? "Someone near you is about to think out loud (close the console to watch)." : "Nobody: " + noThought);
    }

    /// <summary>/chatter dynamic every &lt;time&gt; | npcs &lt;n&gt; | lines &lt;n&gt;.</summary>
    private static void DynamicSetting(string what, string value, Action<string> say, string value2 = "")
    {
        var s = ChatterSettingsAsSet();
        string note = s.Dynamic ? "" : " (Used in Dynamic mode: /chatter mode dynamic.)";
        switch (what)
        {
            case "every":
                if (ChatterSettings.Interval(value) is { } secs)
                {
                    _cfg.DynamicEvery.Value = ChatterSettings.Pretty(Math.Max(3, secs));
                    _nextDynamicLook = Math.Min(_nextDynamicLook, Time.unscaledTime + Math.Max(3, secs));
                    say($"Dynamic: something new may start every {_cfg.DynamicEvery.Value} (saved).{note}");
                }
                else say($"/chatter dynamic every <time>: 5s, 30s, 2m... Now {ChatterSettings.Pretty(s.Effective().DynamicEverySeconds)}.");
                break;
            case "npcs":
            case "max":
            case "people":
                if (SetInt(_cfg.DynamicMaxNpcs, value, 1, ChatterSettings.MaxDynamicNpcs, say, "Dynamic: most people talking near you at once") && note.Length > 0) say(note.Trim());
                break;
            case "lines":
                if (SetInt(_cfg.DynamicLinesEach, value, 1, ChatterSettings.MaxLinesEach, say, "Dynamic: most lines each person says in a conversation") && note.Length > 0) say(note.Trim());
                break;
            case "phone":
                if (SetInt(_cfg.DynamicPhoneLinesEach, value, 1, ChatterSettings.MaxLinesEach, say, "Dynamic: most lines each side of a phone call says") && note.Length > 0) say(note.Trim());
                break;
            case "think":
            case "mumble":
                if (SetInt(_cfg.DynamicThinkLinesEach, value, 1, ChatterSettings.MaxThinkLines, say, "Dynamic: most lines someone mutters to themselves") && note.Length > 0) say(note.Trim());
                break;
            case "starts":
                if (SetInt(_cfg.DynamicStartsEach, value, 1, DynamicChatter.MaxStarts, say, "Dynamic: most that start each time") && note.Length > 0) say(note.Trim());
                break;
            case "stagger":
                if (float.TryParse(value.TrimEnd('s'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float gap) && gap >= 0f)
                {
                    _cfg.DynamicStagger.Value = Math.Min(gap, 60f);
                    say($"Dynamic: starts in the same round up to {_cfg.DynamicStagger.Value:0.#} s apart (saved).{note}");
                }
                else say($"/chatter dynamic stagger <seconds>: how far apart starts in the same round can be (0 = together). Now {s.DynamicStagger:0.#}.");
                break;
            case "weight":
            case "weights":
            {
                var entry = value switch
                {
                    "gossip" or "groups" or "group" or "talk" => _cfg.DynamicGossipWeight,
                    "shop" or "shops" or "food" or "venue" => _cfg.DynamicShopWeight,
                    "phone" or "call" or "calls" => _cfg.DynamicPhoneWeight,
                    "think" or "mumble" or "self" => _cfg.DynamicThinkWeight,
                    _ => null,
                };
                if (entry == null || value2.Length == 0)
                    say($"/chatter dynamic weight gossip|shop|phone|think <n>: how often each kind is picked, relative to the others (0 = never; shop talk is written lines with its own look, so for it any number just means on). " +
                        $"Now gossip {s.WeightGossip}, shop {_cfg.DynamicShopWeight.Value}, phone {s.WeightPhone}, think {s.WeightThink}.");
                else if (SetInt(entry, value2, 0, ChatterSettings.MaxWeight, say, $"Dynamic: weight of {value}") && note.Length > 0) say(note.Trim());
                break;
            }
            default:
                var e = s.Effective();
                say($"Dynamic: every {ChatterSettings.Pretty(e.DynamicEverySeconds)}, up to {e.DynamicMaxNpcs} people talking near you; {e.ClampedLines} lines each in a conversation, " +
                    $"{e.ClampedPhoneLines} a side on the phone, {e.ClampedThinkLines} muttered.{note}");
                say("/chatter dynamic every <time> · npcs <n> · starts <n> · stagger <s> · lines <n> · phone <n> · think <n> · weight <kind> <n>");
                break;
        }
    }

    /// <summary>
    /// The master scales from where a run of /rings scale steps began, so the inner rings keep their proportions (a run ends
    /// when the rings change any other way). The settings menu's slider will do the same with its drag.
    /// </summary>
    private static Rings? _ringsAnchor, _ringsScaled;

    private static Rings ScaleAnchor(Rings now)
    {
        if (_ringsAnchor == null || _ringsScaled == null || _ringsScaled.Value.Clamped() != now.Clamped()) _ringsAnchor = now.Clamped();
        return _ringsAnchor.Value;
    }

    /// <summary>FB-38: the three rings as the settings have them now.</summary>
    private static Rings CurrentRings() => Rings.From(_cfg.VoiceDistance.Value, _cfg.VoiceBeyond.Value, _cfg.ChatterRange.Value);

    /// <summary>Saves the rings (only the settings that changed are written).</summary>
    private static void ApplyRings(Rings r)
    {
        var (vd, vb, cr) = r.ToSettings();
        if (Math.Abs(_cfg.VoiceDistance.Value - vd) > 0.001f) _cfg.VoiceDistance.Value = vd;
        if (Math.Abs(_cfg.VoiceBeyond.Value - vb) > 0.001f) _cfg.VoiceBeyond.Value = vb;
        if (Math.Abs(_cfg.ChatterRange.Value - cr) > 0.001f) _cfg.ChatterRange.Value = cr;
    }

    /// <summary>
    /// FB-38 /rings: show them at your feet, or set one (metres, or + / - for one 0.25 m step). The ring you set wins, the
    /// others move to stay in order; scale sets the outer ring and scales the others with it (the settings menu's master).
    /// </summary>
    private static void RingsCommand(string sub, string arg, Action<string> say)
    {
        var now = CurrentRings();
        string Shown() => _ringsOn ? "shown at your feet" : "not shown (/rings on)";
        switch (sub)
        {
            case "":
            case "status":
                say($"Your rings: hearing {Rings.M(now.Hearing)} (voices fade to silence here) · voices {Rings.M(now.Voices)} (no voice is made past it) · " +
                    $"chatter {Rings.M(now.Chatter)} (people within it start talking; past it, or the voices ring, they wrap up). {Shown()}.");
                say("/rings on|off · /rings hearing|voices|chatter <m|+|-> · /rings scale <m|+|->: the outer ring, the others scale with it (0.25 m steps).");
                return;
            case "on":
            case "off":
            case "show":
            case "hide":
                _ringsOn = sub is "on" or "show";
                say(_ringsOn ? $"Rings on, at your feet: green = hearing ({Rings.M(now.Hearing)}), gold = voices ({Rings.M(now.Voices)}), blue = chatter ({Rings.M(now.Chatter)}). /rings off to hide them."
                             : "Rings off.");
                return;
        }
        float current = sub switch { "hearing" or "hear" => now.Hearing, "voices" or "voice" or "voiced" => now.Voices, "chatter" or "chat" => now.Chatter, _ => now.Chatter };
        // "+" / "-" = one 0.25 m step; "+2" / "-1.5" = that much nearer or farther; a plain number = that many metres.
        string a = arg.TrimEnd('m', ' ');
        bool relative = a.StartsWith("+") || a.StartsWith("-");
        float? m = a switch
        {
            "+" => current + Rings.Step,
            "-" => current - Rings.Step,
            _ => float.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) && float.IsFinite(v)
                ? (relative ? current + v : v) : null,
        };
        if (m == null || sub is not ("hearing" or "hear" or "voices" or "voice" or "voiced" or "chatter" or "chat" or "scale" or "master" or "all"))
        {
            say("/rings hearing|voices|chatter <metres, or + / - for 0.25 m> · /rings scale <metres for the outer ring, or + / -> · /rings on|off");
            return;
        }
        var next = sub switch
        {
            "hearing" or "hear" => now.WithHearing(m.Value),
            "voices" or "voice" or "voiced" => now.WithVoices(m.Value),
            "chatter" or "chat" => now.WithChatter(m.Value),
            _ => ScaleAnchor(now).ScaledTo(m.Value),
        };
        if (sub is "scale" or "master" or "all") _ringsScaled = next; else _ringsAnchor = null;
        ApplyRings(next);
        say($"Rings: {next} (saved).");
    }

    private static void SetMetres(BepInEx.Configuration.ConfigEntry<float> entry, string arg, float min, float max, Action<string> say, string what)
    {
        if (float.TryParse(arg.TrimEnd('m'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
        {
            entry.Value = Mathf.Clamp(v, min, max);
            say($"{what}: {entry.Value:0} m (saved).");
        }
        else say($"{what}: give metres ({min:0}-{max:0}). Now {entry.Value:0}.");
    }

    private static bool SetInt(BepInEx.Configuration.ConfigEntry<int> entry, string arg, int min, int max, Action<string> say, string what)
    {
        if (!Whole(arg, out int v)) { say($"{what}: give a number from {min} to {max}. Now {entry.Value}."); return false; }
        entry.Value = Math.Clamp(v, min, max);
        say($"{what}: {entry.Value} (saved).");
        return true;
    }

    private static bool SetPercent(BepInEx.Configuration.ConfigEntry<int> entry, string arg, Action<string> say, string what)
    {
        if (!Whole(arg.TrimEnd('%'), out int v)) { say($"{what}: give a percent from 0 to 100. Now {entry.Value}%."); return false; }
        entry.Value = Math.Clamp(v, 0, 100);
        say($"{what}: {entry.Value}% (saved).");
        return true;
    }

    /// <summary>A whole number typed in the console, read the same whatever the Windows language ("-1" under ar-SA too).</summary>
    private static bool Whole(string s, out int v) =>
        int.TryParse(s.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out v);

    private static string NewBackupDir() => Path.Combine(_dataDir, "backups", "reset-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The person you're talking to forgets you: their file (identity, memories, relationship) and voice go. A backup is kept.</summary>
    private static void ResetCurrentNpc()
    {
        var s = _session;
        if (s == null) { Ui.AddLine(LineKind.Notice, "", "Wait until they've said something, then try again."); return; }
        string id = s.Record.Id;
        string who = DisplayName(s.Record) == "Stranger" ? "This person" : Prompts.FirstName(DisplayName(s.Record));
        s.Ended = true; // nothing from this meeting is saved or written in their diary
        try { _cts?.Cancel(); } catch { }
        _openSerial++; // a reply still on its way is not shown
        _busy = false;
        _resetting = true;
        _resetGeneration++;
        DropInbox(id); // phone pass PH-R2: their texts on the way never land later (the save's close delivers what's left)
        // Only this person's work in flight is dropped (everyone else's conversations and memories carry on).
        _brain.NewEpoch(id);
        _mockBrain?.NewEpoch(id);
        s.Owner?.Release(s, ReleaseMode.Discard);
        ForgetFollowOf(id); // FB-62/63: they stop coming along, a date with them is over
        if (_home?.Id == id) LeaveHome("they forgot you");
        Greetings.Forget(id);
        Ui.AddLine(LineKind.Notice, "", $"{who} has forgotten you completely. Next time you talk, they'll be someone new: new name, new story, new voice.");
        Ui.SetInputEnabled(false);
        Ui.SetStatus("");
        _queued = "";
        _closeAt = Time.unscaledTime + 3f;
        var pending = _inflight;
        var brain = s.Owner ?? ActiveBrain;
        string backup = NewBackupDir();
        Task.Run(async () =>
        {
            try { await _minorScan.ConfigureAwait(false); } catch { } // everyone under 18 is on the list before files move
            if (pending != null) { try { await pending.ConfigureAwait(false); } catch { } }
            try { await brain.Settled(id).ConfigureAwait(false); } catch { }
            try
            {
                brain.Store.Archive(id, backup);
                try { _memory?.Store.Archive(id, backup); } catch { }
                _voices.Forget(id);
                GameContext.Visits?.Forget(id);
                _story?.Forget(id);
                _pairs?.Forget(id);
                _circles?.Forget(id);
                _reminders?.Forget(id); // review F8
                _couples?.Forget(id);
                _minds?.Forget(id); // the inner-life round: their feelings, and everything anyone saw them do
                _witness?.Forget(id);
                if (_phone != null) foreach (var f in _phone.Forget(id)) DeleteVoiceFile(f); // the phone: their number, texts and voice notes
                ForgetOnNightline(id); // FB-71
                PersonaCache.TryRemove(id, out _);
                Greetings.Forget(id);
                Log.Info($"/resetnpc: {id} forgotten (backup: {backup}).");
            }
            catch (Exception e) { Log.Warn("/resetnpc failed: " + e.Message); }
            MainThread.Enqueue(() => { KnownNames.Remove(id); ForgetDoingsNoted(); _resetting = false; });
        });
    }

    /// <summary>Everyone forgets the player: all NPC files, voices, gossip and headlines. Settings stay. A backup is kept.</summary>
    private static void ResetEveryone()
    {
        EndAllChatter(ResetReason);
        EndAllFollowers("everyone was reset");
        EndJealousWalk();
        LeaveHome("everyone was reset");
        EndAllTagalongs();
        _date = null;
        _stay = null; // FB-85
        HeardLines.Clear(); // CF-24: nobody remembers saying them
        _romanceConfirm = null;
        if (_session != null)
        {
            _session.Ended = true;
            _session.Owner?.Release(_session, ReleaseMode.Discard);
        }
        try { _cts?.Cancel(); } catch { }
        _openSerial++;
        _busy = false;
        _resetting = true;
        _resetGeneration++;
        DropInbox(null); // phone pass PH-R2: nobody's texts on the way land after everyone forgot you
        _brain.NewEpoch();
        _mockBrain?.NewEpoch();
        Greetings.ForgetAll();
        Ui.AddLine(ConsoleOpen ? LineKind.Output : LineKind.Notice, "", "Everyone in Nivalis has forgotten you. From now on, every face is a stranger again. " +
                                        "(A backup was saved in BepInEx\\config\\NivalisAIChat\\backups.)");
        if (!ConsoleOpen)
        {
            Ui.SetInputEnabled(false);
            Ui.SetStatus("");
            _queued = "";
            _closeAt = Time.unscaledTime + 3.5f;
        }
        var pending = _inflight;
        var brains = new[] { _brain, _mockBrain }.Where(b => b != null).Select(b => b!).ToList();
        string backup = NewBackupDir();
        Task.Run(async () =>
        {
            try { await _minorScan.ConfigureAwait(false); } catch { } // everyone under 18 is on the list before files move
            if (pending != null) { try { await pending.ConfigureAwait(false); } catch { } }
            int people = 0;
            foreach (var b in brains)
            {
                try { await b.SettledAll().ConfigureAwait(false); } catch { }
                try
                {
                    people += b.Store.ArchiveAll(Path.Combine(backup, b == _brain ? "npcs" : "npcs-mock"));
                    b.World?.Reset(backup);
                }
                catch (Exception e) { Log.Warn("/resetall: " + e.Message); }
            }
            try { _voices.ForgetAll(backup); } catch (Exception e) { Log.Warn("/resetall voices: " + e.Message); }
            try { _memory?.Store.ArchiveAll(backup); } catch (Exception e) { Log.Warn("/resetall memory index: " + e.Message); }
            try { GameContext.Visits?.Clear(backup); } catch (Exception e) { Log.Warn("/resetall visits: " + e.Message); }
            try { _story?.Clear(backup); } catch (Exception e) { Log.Warn("/resetall story: " + e.Message); }
            try { _pairs?.Clear(backup); } catch (Exception e) { Log.Warn("/resetall pairs: " + e.Message); }
            try { _circles?.Clear(backup); } catch (Exception e) { Log.Warn("/resetall groups: " + e.Message); }
            try { _reminders?.Clear(); } catch (Exception e) { Log.Warn("/resetall reminders: " + e.Message); } // review F8
            try { _couples?.Clear(); } catch (Exception e) { Log.Warn("/resetall couples: " + e.Message); }
            try { if (_phone != null) foreach (var f in _phone.Clear()) DeleteVoiceFile(f); } catch (Exception e) { Log.Warn("/resetall phone: " + e.Message); } // the phone
            ClearNightline(); // FB-71
            try { _minds?.Clear(); _witness?.Clear(); PersonaCache.Clear(); } catch (Exception e) { Log.Warn("/resetall inner life: " + e.Message); } // the inner-life round
            Log.Info($"/resetall: {people} people forgotten (backup: {backup}).");
            MainThread.Enqueue(() => { KnownNames.Clear(); Greetings.ForgetAll(); ForgetDoingsNoted(); _resetting = false; });
        });
    }

    /// <summary>FB-50: /story: what's kept of the story conversations, per story character.</summary>
    private static void StoryCommand(Action<string> say)
    {
        if (_story == null) { say("Story conversations aren't available this session (the log says why)."); return; }
        var all = _story.Summary();
        say($"Story conversations: {(Npc.Features.StoryMemory ? "kept and used" : "off (StoryMemory)")}; their way of talking: " +
            $"{(Npc.Features.StoryVoice ? "on" : "off (StoryVoice)")}. Only lines you've seen are kept.");
        if (all.Count == 0)
        {
            say("Nothing kept yet. Talk with a story character in the game's own dialogue, or open someone's chat history in the " +
                "game's Character window and page through it: what it shows is kept.");
            return;
        }
        foreach (var (_, name, lines, sheet) in all.Take(12))
            say($"  {(name.Length > 0 ? name : "someone")}: {lines} line{(lines == 1 ? "" : "s")}{(sheet ? ", way of talking written" : "")}");
        if (all.Count > 12) say($"  ...and {all.Count - 12} more.");
    }
}
