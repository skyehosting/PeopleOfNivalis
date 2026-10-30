using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NivalisAIChat.Game;
using NivalisAIChat.Social;
using NivalisAIChat.UI;
using NivalisAIChat.Voice;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NivalisAIChat;

/// <summary>
/// FB-40: the in-game settings live in a separate mod, Nivalis Switchboard, which finds this one by its marker and reads
/// its page (<see cref="SettingsMenuPage"/>). Neither needs the other: without it, the settings are in the file and the cog
/// isn't shown. With it, the cog and /settings open it on this mod's settings; while it's up, this mod takes no input, and a
/// chat or the console it was opened over is hidden and comes back as it was. A change there is applied the way the
/// console's commands apply one, so the two always agree. Also CF-07's controller buttons in play (PadTalkButton,
/// PadConsoleButton).
/// </summary>
public static partial class ModRuntime
{
    /// <summary>The settings menu (the other mod) is up.</summary>
    private static bool _menuOpen, _menuOverWindow, _menuHolds;
    private static int _menuClosedFrame = -1;
    /// <summary>The rings the menu is changing, drawn at your feet meanwhile (null: none).</summary>
    private static Rings? _ringsPreview;
    /// <summary>Where the menu's master ring slider scales from (a run of its moves; another ring moving ends it).</summary>
    private static Rings? _menuRingAnchor;

    internal static bool MenuOpen => _menuOpen;

    private static Type? _menuApi;
    private static float _menuApiLookedAt = -100f;

    /// <summary>The settings menu mod's public class, when it's installed (looked for again every few seconds while it isn't).</summary>
    private static Type? MenuApi()
    {
        if (_menuApi != null || Time.unscaledTime - _menuApiLookedAt < 5f) return _menuApi;
        _menuApiLookedAt = Time.unscaledTime;
        try
        {
            // Among the DLLs already loaded (BepInEx loads plugins from their paths, so a name lookup alone may not find it).
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "NivalisSwitchboard");
            _menuApi = asm?.GetType("NivalisSwitchboard.SettingsMenu", throwOnError: false);
        }
        catch { _menuApi = null; }
        return _menuApi;
    }

    internal static bool MenuInstalled => MenuApi() != null;

    private const string NoMenu =
        "The in-game settings come with Nivalis Switchboard, a separate mod. Without it, every setting is in " +
        "BepInEx\\config\\PeopleOfNivalis.cfg (a change there applies as soon as you save it).";

    /// <summary>Opens the settings menu on this mod's settings; without it, <paramref name="say"/> says where the settings are.</summary>
    private static void OpenSettings(Action<string>? say)
    {
        var api = MenuApi();
        if (api == null) { say?.Invoke(NoMenu); return; }
        try { api.GetMethod("Open")?.Invoke(null, new object?[] { Plugin.Guid }); }
        catch (Exception e) { Log.Warn("Opening the settings menu: " + (e.InnerException ?? e).Message); }
    }

    /// <summary>The menu opened (its page's "opened"): a chat or the console under it waits, hidden.</summary>
    internal static void MenuOpened()
    {
        try { CloseWorldLine(); } catch { } // review WC-8: the settings menu over an open line
        _menuOpen = true;
        try { StopVoiceInput(); } catch { } // review F7: the microphone isn't left open under the menu
        // The full audit (B-2): the phone goes away first, so its hold is released here and the menu takes its own (else
        // closing the menu would free you with the phone still out, and the phone would take clicks under the menu).
        try { ClosePhone(); } catch { }
        if (ChatOpen || _consoleOpen) { _menuOverWindow = true; Ui.Suspend(true); return; }
        Ui.ShowPrompt(null);
        // Opened from play (its key): the player is held, and the clock paused with PauseTimeWhileChatting, as in a chat
        // (review of d6138be). Not from the game's pause or title menu: the game holds itself there.
        if (GameContext.PlayerCharacter() != null && InGameplay())
        {
            Locks.Engage(_cfg.PauseTimeWhileChatting.Value);
            _menuHolds = true;
        }
    }

    /// <summary>The menu closed: the chat or console comes back as it was; the press that closed it doesn't act here.</summary>
    internal static void MenuClosed()
    {
        _menuOpen = false;
        _menuClosedFrame = Time.frameCount;
        _ringsPreview = null;
        _menuRingAnchor = null;
        Pad.Consume();
        if (_menuHolds)
        {
            _menuHolds = false;
            try { Locks.Release(); } catch (Exception e) { Log.Warn("Releasing player locks failed: " + e.Message); }
        }
        if (!_menuOverWindow) return;
        _menuOverWindow = false;
        if (ChatOpen || _consoleOpen)
        {
            Ui.Suspend(false);
            _idle = 0; // time in the menu isn't time spent silent with them
        }
    }

    /// <summary>FB-81: the Keybinds tab, beside the Usage Optimizer (the settings file's [Keybinds]).</summary>
    private const string KeybindsGroup = "Keybinds";

    /// <summary>Review F81-3: a key that never types or edits text (for a key pressed while a text line has the keyboard).</summary>
    private static bool NonTypingKey(Key k) =>
        k is Key.Tab or Key.Insert or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Pause or Key.ScrollLock
            or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt || k is >= Key.F1 and <= Key.F12;

    private static bool TypesCharacter(Key k) => k is >= Key.A and <= Key.Z || k is >= Key.Digit1 and <= Key.Digit0 || k == Key.Space;

    private static Key KeyNamed(string? name, Key fallback)
    {
        string k = (name ?? "").Trim();
        if (k == "`" || k == "~") k = "Backquote";
        if (k.Length == 1 && char.IsDigit(k[0])) k = "Digit" + k;
        return Enum.TryParse<Key>(k, true, out var parsed) && parsed != Key.None ? parsed : fallback;
    }

    /// <summary>Why a key the menu caught can't be used for this setting (null: it can). The same rules the keys are read with.</summary>
    private static string? KeyProblem(string setting, string name)
    {
        var k = KeyNamed(name, Key.None);
        if (k == Key.None) return $"{name} isn't a key the mod can use.";
        switch (setting)
        {
            case "WorldChatNextKey": // FB-81
                if (!NonTypingKey(k)) return "It would type in world chat's line: pick one that doesn't (Tab, F1-F12, Insert, Home, PageUp...).";
                return k == _worldKey || k == _phoneKey || k == _talkKey || k == _consoleKey ? $"{name} already does something else (world chat, the phone, talk or the console)." : null;
            case "TalkKey":
                return k == _consoleKey ? $"{name} already opens the console." : null;
            case "ConsoleKey":
                if (TypesCharacter(k)) return "The console key can't be a letter, a digit or Space: you type those in commands.";
                return k == _talkKey ? $"{name} already starts a conversation." : null;
        }
        return null;
    }

    /// <summary>
    /// A change from the menu has what any change of that setting needs done (the same as after /reload): memory search
    /// switched over, the window rebuilt at a new UiScale once it's closed, a shorter interval applied now...
    /// </summary>
    private static void AfterMenuChange(string key) => AfterReload(new List<string> { key }, null, auto: false);

    // ------------------------------------------------------------------ the page the menu reads

    /// <summary>What the settings menu shows of this mod (see the menu's CONTRACT.md): plain .NET types only.</summary>
    internal static IDictionary<string, object?> DescribeForMenu()
    {
        var settings = new Dictionary<string, object?>(StringComparer.Ordinal);
        var groups = new List<object>();
        string featuresIntro = "", keybindsIntro = "";
        foreach (var (name, intro, entries) in _cfg.Groups)
        {
            // FB-45: every group of the settings file is under "Advanced" (Features has its own place, near the top). FB-81:
            // Keybinds too ("move the "controls" out of the advanced sub tab so it sits next to usage optimizer").
            if (name == FeaturesGroup) featuresIntro = intro;
            else if (name == KeybindsGroup) keybindsIntro = intro;
            else groups.Add(new Dictionary<string, object?> { ["name"] = name, ["intro"] = intro, ["category"] = "Advanced" });
            foreach (var e in entries)
            {
                string key = e.Definition.Key;
                var control = _cfg.IsSecret(e) ? new SettingControl(ControlKind.Secret) : SettingsCatalog.For(name, key, e.SettingType);
                var hint = SettingsCatalog.ToContract(control);
                if (name == FeaturesGroup) hint["kind"] = "hidden"; // the Features tab shows them as its own rows, in its order
                if (ReferenceEquals(e, _cfg.SettingsMenu)) hint["kind"] = "hidden"; // only for other menus: "it's in Switchboard"
                if (control.Kind == ControlKind.Key) hint["validate"] = (Func<string, string?>)(k => KeyProblem(key, k));
                if (LeverOfKey(key) != null) AddLeverBadge(key, hint); // FB-41: changed after the Usage Optimizer's plan
                settings[name + "/" + key] = hint;
            }
        }
        // FB-45: the Setup Wizard first, then the Usage Optimizer (FB-41) and Features; then the Advanced category.
        groups.InsertRange(0, new object[]
        {
            new Dictionary<string, object?>
            {
                ["name"] = WizardGroup,
                ["intro"] = "Three steps to get going: your AI, voices if you want them, and what you're happy to spend. Save each page as you go.",
            },
            new Dictionary<string, object?>
            {
                ["name"] = OptimizerGroup,
                ["intro"] = "Your limits, where each is heading, and the liveliest settings that stay within them. Nothing changes until you press Apply.",
            },
            new Dictionary<string, object?> { ["name"] = FeaturesGroup, ["intro"] = featuresIntro },
            new Dictionary<string, object?> { ["name"] = KeybindsGroup, ["intro"] = keybindsIntro },
        });
        InvalidateOptimizer();
        return new Dictionary<string, object?>
        {
            ["name"] = Plugin.Name,
            ["savesItself"] = true, // its own in-place writer, on every SettingChanged: never ConfigFile.Save()
            // Every setting tells other menus not to list it (ModConfig: they'd save the file through BepInEx, losing the
            // Providers list, and show the keys). Switchboard lists one that has a hint with a kind (each above has, from
            // ToContract) in any version; from 0.5.5 this lists the rest too.
            ["listHidden"] = true,
            ["groups"] = groups,
            ["settings"] = settings,
            // Each tab's own rows, after the small headings that divide the longer tabs into parts.
            // CF-20: the AI Connection tab's profiles come before its "Spending" heading (rows placed before one setting go in order).
            ["rows"] = (Func<string, IEnumerable>)(group => (group == "AI Connection" ? ProfileRowsSafe() : new List<object>()).Concat(TabParts(group)).Concat(group switch
            {
                "NPC Chatter" => RingRows(),
                "Voices" => ProviderRows(),
                OptimizerGroup => OptimizerRowsSafe(),
                WizardGroup => WizardRowsSafe(),
                FeaturesGroup => FeatureRowsSafe(),
                _ => new List<object>(),
            }).ToList()),
            ["opened"] = (Action)MenuOpened,
            ["closed"] = (Action)MenuClosed,
            ["changed"] = (Action<string>)AfterMenuChange,
        };
    }

    private static List<object> ProfileRowsSafe()
    {
        try { return ProfileRows(); }
        catch (Exception e) { Log.Debug("profile rows: " + e.Message); return new List<object>(); }
    }

    /// <summary>
    /// The parts of the longer tabs: a small heading before the setting each part starts with (the author: "make sure their
    /// designs look nice"). In file order; a heading placed before the rings comes before the rings' own rows.
    /// </summary>
    private static readonly Dictionary<string, (string before, string title)[]> Parts = new(StringComparer.Ordinal)
    {
        ["AI Connection"] = new[] { ("ApiFormat", "Connection"), ("TokenLimit", "Spending") },
        ["Voices"] = new[] { ("VoiceLimit", "Limit for all voices"), ("Volume", "Sound"), ("MatchVoiceToGender", "Voices for people") },
        ["Keybinds"] = new[] { ("TalkKey", "Keyboard"), ("PadTalkButton", "Controller") }, // FB-81
        ["City Life"] = new[] { ("NpcsGreetYou", "Greetings"), ("Gossip", "Talk of the town"), ("OverhearChance", "Overhearing") },
        ["NPC Chatter"] = new[]
        {
            ("Chatter", "Residents' talk"), ("DynamicEvery", "Dynamic mode"), ("ChatterPerHour", "Pace"), ("ChatterRange", "Rings"),
            ("OrderTalk", "Food places"), ("TableCallChance", "More"),
        },
        ["Advanced Chatter"] = new[] { ("GroupChance", "Conversations"), ("PhoneChance", "Phone calls"), ("ThinkChance", "Thinking out loud") },
        ["People"] = new[] { ("Friendliness", "Who you can talk to"), ("TransChance", "Who they are"), ("LookAtNewPeople", "What they notice") },
        ["Relationships"] = new[] { ("ShowRelationshipBar", "The bar"), ("WaveAt", "How they act"), ("FoodGiftBonus", "Food") },
        ["Memory"] = new[] { ("HistoryLimit", "History"), ("LifeUpdates", "Life updates"), ("RecentMessagesSent", "What's sent to the AI"), ("MemorySearch", "Memory search") },
        ["Speech Bubbles"] = new[] { ("BubbleBackground", "Colours"), ("BubbleText", "Text") },
        ["Interface"] = new[] { ("SmoothTextFade", "Text"), ("ShowTalkPrompt", "On screen"), ("AutoReloadSettings", "Settings file") },
        ["AI Fine Tuning"] = new[] { ("StructuredReplies", "How it replies"), ("PromptCaching", "Requests") },
    };

    private static IEnumerable<object> TabParts(string group) =>
        Parts.TryGetValue(group, out var parts)
            ? parts.Select(p => (object)new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "subsection", ["title"] = p.title, ["before"] = p.before })
            : Enumerable.Empty<object>();

    /// <summary>FB-38's four ring sliders, in place of the three ring settings: the master first, then each ring.</summary>
    private static List<object> RingRows()
    {
        var rows = new List<object>();
        foreach (var part in RingSliders.All)
        {
            var c = SettingsCatalog.ToContract(RingSliders.Control(part));
            c["before"] = "ChatterRange";
            c["title"] = RingSliders.Title(part);
            c["hint"] = RingSliders.Hint(part);
            c["help"] = RingSliders.Help(part);
            c["type"] = "double";
            c["seeThrough"] = true;
            c["get"] = (Func<object?>)(() =>
            {
                var r = (_ringsPreview ?? CurrentRings()).Clamped();
                return RingSliders.Off(r, part) ? "off" : (object)(double)RingSliders.Value(r, part);
            });
            c["preview"] = (Action<object?>)(v =>
            {
                var now = CurrentRings().Clamped();
                if (v == null) { _ringsPreview = null; return; } // not looked at any more
                if (v is not double m) { _ringsPreview = now; return; } // looked at, nothing moved (the voices ring while it's off)
                // Only a saved move starts the master's run (set): looking at it, or moving it, scales from there or from now.
                _ringsPreview = RingSliders.Set(now, _menuRingAnchor ?? now, part, (float)m);
            });
            c["set"] = (Func<object, string?>)(v =>
            {
                if (v is not double m) return "That isn't a distance.";
                var now = CurrentRings().Clamped();
                if (part == RingPart.Master) _menuRingAnchor ??= now;
                var next = RingSliders.Set(now, _menuRingAnchor ?? now, part, (float)m);
                if (part != RingPart.Master) _menuRingAnchor = null; // another ring moved: the master's next run starts here
                _ringsAnchor = null; // /rings scale starts its own run after the menu's
                ApplyRings(next);
                AfterMenuChange("ChatterRange");
                return null;
            });
            rows.Add(c);
        }
        return rows;
    }

    // ------------------------------------------------------------------ CF-09: the voice Providers list as rows

    private static ProviderEntry? ProviderAt(int index)
    {
        var all = ProviderLines.Read(_cfg.ProviderLinesNow);
        return index >= 0 && index < all.Count ? all[index] : null;
    }

    /// <summary>Saves one provider's change (its line only): null when saved, otherwise why not.</summary>
    private static string? SaveProvider(int index, Func<ProviderEntry, (ProviderEntry? next, string? why)> change)
    {
        var before = _cfg.ProviderLinesNow;
        var all = ProviderLines.Read(before);
        if (index < 0 || index >= all.Count) return "That provider isn't in the list any more.";
        var (next, why) = change(all[index]);
        return next == null ? why : SaveProviderLines(before, ProviderLines.With(before, next));
    }

    private static string? SaveProviderLines(IReadOnlyList<string> before, IReadOnlyList<string> lines)
    {
        if (!_cfg.SetProviderLines(before, lines, out string? error)) return "The voice list wasn't saved: " + (error ?? "it couldn't be written") + ".";
        AfterMenuChange("Providers"); // the voice list's problems said, as after a reload
        return null;
    }

    /// <summary>The provider in use for this line of the list (its limit, its use), when it's switched on and read.</summary>
    private static VoiceSlot? SlotFor(ProviderEntry e)
    {
        var probe = new VoiceSlot { Provider = e.Provider, Url = e.Provider == VoiceProvider.Windows ? "windows" : e.Endpoint, Model = e.Model };
        string key = VoiceService.LedgerKey(probe);
        return _cfg.VoiceSlots.FirstOrDefault(s => VoiceService.LedgerKey(s) == key);
    }

    /// <summary>
    /// FB-41: a paid provider's own limit (in its unit; 0 = none) and its period, what it has used, and for ElevenLabs a button
    /// to take the account's own monthly amount.
    /// </summary>
    private static List<object> LimitRows(int index, ProviderEntry e, string name)
    {
        var rows = new List<object>();
        string unit = VoiceCost.Unit(e.Provider, e.Model.Length > 0 ? e.Model : VoiceService.ModelOf(new VoiceSlot { Provider = e.Provider }));
        var limit = SettingsCatalog.ToContract(new SettingControl(ControlKind.Number, 0, 1_000_000_000));
        limit["after"] = "Enabled";
        limit["title"] = $"{name} limit";
        limit["hint"] = $"The most it may use, in {unit} (0 = no limit)";
        limit["help"] = $"The most {name} may be asked for per period, in its own unit ({unit}: what your plan counts). Once it's reached, the " +
                        "people with its voices speak in a stand-in voice from another provider until the period starts again (or as text, with " +
                        "no other provider). Every line it speaks is counted here; ElevenLabs also tells the mod what your account has used.";
        limit["type"] = "double";
        limit["get"] = (Func<object?>)(() => ProviderAt(index)?.Limit ?? 0);
        limit["set"] = (Func<object, string?>)(v => SaveProvider(index, p => (p with { Limit = Math.Max(0, Math.Round(Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture))) }, null)));
        rows.Add(limit);
        var per = SettingsCatalog.ToContract(new SettingControl(ControlKind.Choice, Choices: new[] { "day", "week", "month" }));
        per["after"] = "Enabled";
        per["title"] = $"{name} limit per";
        per["hint"] = "How long each limit lasts";
        per["help"] = "Day (from midnight), week (from Monday) or month (from its reset day below; for ElevenLabs without one, your account's " +
                      "own month, from its reset date).";
        per["type"] = "string";
        per["get"] = (Func<object?>)(() => ProviderAt(index)?.Per ?? "month");
        per["set"] = (Func<object, string?>)(v => SaveProvider(index, p => (p with { Per = (v as string ?? "month").ToLowerInvariant() is "day" or "week" ? ((string)v).ToLowerInvariant() : "month" }, null)));
        rows.Add(per);
        // FB-59: the day of the month it starts again on (your billing day), by your PC's calendar.
        var day = SettingsCatalog.ToContract(new SettingControl(ControlKind.Slider, 0, 31, 1));
        day["after"] = "Enabled";
        day["title"] = $"{name} resets on day";
        day["hint"] = e.Provider == VoiceProvider.ElevenLabs ? "0 = your account's own day" : "0 = the 1st";
        day["help"] = "With a monthly limit: the day of the month it starts again, to match your plan's billing day (past a short month's end, its " +
                      "last day), at midnight by your PC's calendar. 0: the 1st" + (e.Provider == VoiceProvider.ElevenLabs ? ", or your ElevenLabs account's own reset date once it's been read." : ".");
        day["type"] = "double";
        day["get"] = (Func<object?>)(() => (double)(ProviderAt(index)?.ResetDay ?? 0));
        day["set"] = (Func<object, string?>)(v => SaveProvider(index, p => (p with { ResetDay = (int)Math.Clamp(Math.Round(Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture)), 0, 31) }, null)));
        day["visible"] = (Func<bool>)(() => (ProviderAt(index)?.Per ?? "month") == "month");
        rows.Add(day);
        var use = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = "note", ["after"] = "Enabled", ["title"] = $"{name} use", ["hint"] = "Yours, counted line by line",
            ["help"] = "What you've used this period, from every line it spoke on this PC: that's what your limit counts. With ElevenLabs, the " +
                       "account's own count (everyone using its key) is shown after it. Sharing a key: set your limit to your share. /usage in " +
                       "the console also shows what a line and an hour of play cost on average, and on busy days.",
            ["get"] = (Func<object?>)(() =>
            {
                var slot = SlotFor(ProviderAt(index) ?? e);
                if (slot == null) return "";
                double used = _voices.UsedThisPeriod(slot);
                var (_, reset) = _voices.PeriodOf(slot);
                string mine = slot.Limit > 0 ? $"{used:N0} of {slot.Limit:N0} ({Math.Min(999, used * 100 / slot.Limit):0}%) · again {reset:MMM d}" : $"{used:N0} {unit} so far";
                return _voices.AccountNow(slot) is { } acc ? mine + $" · account {acc.used:N0} of {acc.limit:N0}" : mine;
            }),
        };
        rows.Add(use);
        if (e.Provider == VoiceProvider.ElevenLabs && SlotFor(e) is { } now && _voices.AccountNow(now) is { } left && left.limit > left.used)
        {
            // FB-59 (the author): take what the account has left as your limit; the plan spreads it over the days to the reset.
            double mineSoFar = _voices.UsedThisPeriod(now), rest = left.limit - left.used;
            var leftRow = ProviderRow("button", "Use what's left on the account", $"{rest:N0} credits left: your limit {mineSoFar + rest:N0}",
                "Sets your limit to what you've used this month plus what the account has left, so the plan spreads the rest over the days " +
                "to the reset. Sharing the key: set half of what's left (or your share) yourself.", "string", rebuild: true);
            leftRow["label"] = "Use it";
            leftRow["click"] = (Func<string?>)(() =>
            {
                // Read when pressed (review F59-7: a menu left open would have written an old figure).
                var live = SlotFor(ProviderAt(index) ?? e);
                if (live == null || _voices.AccountNow(live) is not { } a || !(a.limit > a.used)) return "The account's figures aren't known right now.";
                double limitNow = _voices.UsedThisPeriod(live) + (a.limit - a.used);
                return SaveProvider(index, p => (p with { Limit = Math.Round(limitNow), Per = "month", ResetDay = 0 }, null)); // the account's own month
            });
            rows.Add(leftRow);
        }
        if (e.Provider == VoiceProvider.ElevenLabs && SlotFor(e) is { } live && _voices.AccountOf(live) is { } acc && Math.Abs(acc.limit - e.Limit) > 0.5)
        {
            var plan = ProviderRow("button", $"Use your ElevenLabs plan's amount", $"{acc.limit:N0} credits a month, as your account says",
                "Sets this provider's limit to your ElevenLabs plan's monthly amount, counted from your account's own reset date.", "string", rebuild: true);
            plan["label"] = "Use it";
            plan["click"] = (Func<string?>)(() => SaveProvider(index, p => (p with { Limit = Math.Round(acc.limit), Per = "month", ResetDay = 0 }, null))); // the account's own day
            rows.Add(plan);
        }
        return rows;
    }

    /// <summary>A key, model or address can't hold a comma or a bracket: that would split its line in the file.</summary>
    private static string? Splits(string value) =>
        value.IndexOfAny(new[] { ',', '(', ')' }) >= 0 ? "That can't have a comma or a bracket in it (they separate the parts of the provider's line)." : null;

    /// <summary>Every "(" closed by a ")" after it: a voice like "Aria (9BWts...):female" is fine, a stray ")" isn't (review of a8971f9).</summary>
    internal static bool Balanced(string s)
    {
        int depth = 0;
        foreach (char ch in s)
        {
            if (ch == '(') depth++;
            else if (ch == ')' && --depth < 0) return false;
        }
        return depth == 0;
    }

    private static Dictionary<string, object?> ProviderRow(string kind, string title, string hint, string help, string type, bool rebuild = false)
    {
        var c = SettingsCatalog.ToContract(new SettingControl(kind switch
        {
            "switch" => ControlKind.Switch, "secret" => ControlKind.Secret, "button" => ControlKind.Button, _ => ControlKind.Text,
        }));
        c["after"] = "Enabled";
        c["title"] = title;
        c["hint"] = hint;
        c["help"] = help;
        c["type"] = type;
        c["rebuild"] = rebuild;
        return c;
    }

    /// <summary>
    /// The providers written in the list, after [Voices] Enabled (where the list sits in the file): a switch each; one that's
    /// on shows its key (only whether it's set), chance, voices, model, address and Remove; "Add a voice provider" adds one.
    /// </summary>
    private static List<object> ProviderRows()
    {
        var rows = new List<object>();
        var all = ProviderLines.Read(_cfg.ProviderLinesNow);
        for (int k = 0; k < all.Count; k++)
        {
            int index = k;
            var e = all[k];
            string name = ProviderLines.Name(e.Provider);
            string where = e.Provider == VoiceProvider.Windows ? "the voices installed in Windows" : Ai.Redact.Url(e.Endpoint);
            rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["kind"] = "subsection", ["after"] = "Enabled",
                ["title"] = name + (e.Provider is VoiceProvider.OpenAICompatible or VoiceProvider.Azure ? "  ·  " + where : ""),
            });
            int first = rows.Count;
            var sw = ProviderRow("switch", "Switched on", $"Voice provider {k + 1}  ·  {where}",
                $"Use {name} for people's voices. Off puts a # in front of its line in the settings file (it stays there to switch on " +
                "again). With more than one on, each person gets one of them, by their chances.", "bool", rebuild: true);
            sw["get"] = (Func<object?>)(() => ProviderAt(index)?.On ?? false);
            sw["set"] = (Func<object, string?>)(v => SaveProvider(index, p => (p with { On = v is true }, null)));
            rows.Add(sw);
            if (!e.On) continue;
            if (e.Provider != VoiceProvider.Windows) rows.Add(KeyRow(index, e, name));
            var chance = SettingsCatalog.ToContract(new SettingControl(ControlKind.Slider, 0, 100, 1));
            chance["after"] = "Enabled";
            chance["title"] = $"{name} chance";
            chance["hint"] = "How often a newly met person gets it";
            chance["help"] = "How often a person you meet for the first time gets a voice from this provider, compared with the others that are on " +
                             "(50 and 50: half each; 70 and 30...). People keep the voice they were given.";
            chance["type"] = "double";
            chance["get"] = (Func<object?>)(() => ProviderAt(index)?.Chance ?? 0);
            chance["set"] = (Func<object, string?>)(v => SaveProvider(index, p => (p with { Chance = Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture) }, null)));
            rows.Add(chance);
            if (VoiceService.Billed(new VoiceSlot { Provider = e.Provider, Url = e.Endpoint })) rows.AddRange(LimitRows(index, e, name));
            var voices = ProviderRow("text", $"{name} voices", "auto = every voice it offers you, or a list",
                "auto = all of the provider's voices your account can use, or a list like nova:female, onyx:male (a voice, then who it " +
                "suits). /voices in the console lists what each provider offers you.", "string");
            voices["get"] = (Func<object?>)(() => ProviderAt(index)?.Voices ?? "");
            voices["set"] = (Func<object, string?>)(v =>
            {
                string s = (v as string ?? "").Trim();
                if (Ai.Redact.PlainlyKey(s)) return "That looks like a key: keys go in the key field, not among the voices.";
                if (!Balanced(s)) return "The brackets in that don't pair up (each \"(\" needs its \")\" after it).";
                return SaveProvider(index, p => (p with { Voices = s.Length > 0 ? s : "auto" }, null));
            });
            rows.Add(voices);
            if (e.Provider != VoiceProvider.Windows)
            {
                var model = ProviderRow("text", $"{name} model", "Empty = the provider's usual one",
                    "Which of the provider's models makes the voices (gpt-4o-mini-tts, eleven_v4, kokoro...). Empty uses its usual one.", "string");
                model["get"] = (Func<object?>)(() => ProviderAt(index)?.Model ?? "");
                model["set"] = (Func<object, string?>)(v =>
                {
                    string s = (v as string ?? "").Trim();
                    if (Ai.Redact.PlainlyKey(s)) return "That looks like a key: keys go in the key field.";
                    return Splits(s) ?? SaveProvider(index, p => (p with { Model = s }, null));
                });
                rows.Add(model);
            }
            if (e.Provider is VoiceProvider.OpenAICompatible or VoiceProvider.Azure)
            {
                var address = ProviderRow("text", $"{name} address", e.Provider == VoiceProvider.Azure ? "Your Azure region's address" : "Your server's address",
                    e.Provider == VoiceProvider.Azure
                        ? "The address for your Azure region, like https://eastus.tts.speech.microsoft.com."
                        : "Your voice server's address, like http://localhost:8880/v1 (Kokoro) or any OpenAI-compatible server.", "string", rebuild: true);
                address["get"] = (Func<object?>)(() => ProviderAt(index)?.Endpoint ?? "");
                address["set"] = (Func<object, string?>)(v =>
                {
                    string s = (v as string ?? "").Trim();
                    if (VoiceProviderList.Detect(s) == VoiceProvider.None) return "That isn't an address (it starts with http:// or https://).";
                    return Splits(s) ?? SaveProvider(index, p => (p with { Endpoint = s }, null));
                });
                rows.Add(address);
            }
            rows.Add(RemoveRow(index, name));
            for (int i = first + 1; i < rows.Count; i++) if (rows[i] is Dictionary<string, object?> d) Retitle(d, name, e);
        }
        rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "subsection", ["after"] = "Enabled", ["title"] = "Another provider" });
        rows.Add(AddProviderRow());
        return rows;
    }

    /// <summary>A short fingerprint of a provider's address for its rows' ids (never shown; the address itself isn't kept in it).</summary>
    private static string AddressPrint(string endpoint)
    {
        ulong h = 0xcbf29ce484222325;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(endpoint.Trim().ToLowerInvariant())) { h ^= b; h *= 0x100000001b3; }
        return h.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>"Remove": takes a provider's line out of the list (the Voices tab, the Setup Wizard and the Usage Optimizer).</summary>
    private static Dictionary<string, object?> RemoveRow(int index, string name)
    {
        var remove = ProviderRow("button", $"Remove {name}", "Takes its line out of the list",
            "Takes this provider's line out of the settings file's list. People who had one of its voices get another the next time they " +
            "speak. To keep it for later instead, switch it off.", "string", rebuild: true);
        remove["label"] = "Remove";
        remove["click"] = (Func<string?>)(() =>
        {
            var before = _cfg.ProviderLinesNow;
            var now = ProviderLines.Read(before);
            return index < now.Count ? SaveProviderLines(before, ProviderLines.Removed(before, now[index])) : "That provider isn't in the list any more.";
        });
        return remove;
    }

    /// <summary>A provider's row under its own heading: "ElevenLabs chance" reads "Chance", "Remove ElevenLabs" reads "Remove".</summary>
    private static void Retitle(Dictionary<string, object?> d, string name, ProviderEntry e)
    {
        if (!d.TryGetValue("title", out var t) || t is not string s) return;
        // FB-45: its full name stays as its id, so a change waiting for Save stays on this provider's row when one before it goes;
        // with a fingerprint of its address, so two of one kind (two local servers) don't share it (review of 0.4.1).
        if (!d.ContainsKey("id")) d["id"] = $"voice:{s}@{AddressPrint(e.Endpoint)}";
        if (s == $"Remove {name}") s = "Remove";
        else if (s == $"{name} limit per") s = "Duration";
        else if (s == $"{name} use") s = "Used";
        else if (s == $"Use your {name} plan's amount") s = "Use your plan's amount";
        else if (s.StartsWith(name + " ", StringComparison.Ordinal) && s.Length > name.Length + 1)
        {
            string rest = s.Substring(name.Length + 1);
            s = char.ToUpperInvariant(rest[0]) + rest.Substring(1);
        }
        d["title"] = s;
    }

    /// <summary>A provider's key: only whether it's set is ever shown.</summary>
    private static Dictionary<string, object?> KeyRow(int index, ProviderEntry e, string name)
    {
        var key = ProviderRow("secret", $"{name} key",
            e.Provider == VoiceProvider.OpenAI ? "Empty = your chat key (ApiKey)" : e.Provider == VoiceProvider.OpenAICompatible ? "Only if your server asks for one" : "Your key from their website",
            $"The key {name} gave you. It's never shown: only whether it's set." + (e.Provider == VoiceProvider.OpenAI ? " Left empty, OpenAI voices use your chat key (ApiKey)." : ""),
            "string");
        // Only whether there's one (never the key itself crosses to the menu). OpenAI with none of its own uses your chat key
        // (ApiKey): that's set too (the author saw "not set" with ApiKey in their file).
        key["get"] = (Func<object?>)(() => ProviderAt(index) is { } p && HasVoiceKey(p) ? "set" : "");
        key["set"] = (Func<object, string?>)(v =>
        {
            string s = (v as string ?? "").Trim();
            return SaveProvider(index, p => Splits(s) is { } why ? (null, why) : (p with { Key = s }, null));
        });
        return key;
    }

    /// <summary>A provider's line has a key, or (OpenAI) your chat key stands in for it.</summary>
    private static bool HasVoiceKey(ProviderEntry p) =>
        ProviderLines.KeySet(p.Key) || (p.Provider == VoiceProvider.OpenAI && _cfg.ApiKey.Value.Trim().Length > 0);

    /// <summary>"Add a voice provider": a dropdown whose choice adds that provider, switched on.</summary>
    private static Dictionary<string, object?> AddProviderRow()
    {
        // A free provider says so in the list, as its card does.
        static string Label((string name, VoiceProvider provider) a) => VoiceService.Billed(new VoiceSlot { Provider = a.provider }) ? a.name : a.name + " (free)";
        var add = SettingsCatalog.ToContract(new SettingControl(ControlKind.Choice, Choices: ProviderLines.Addable.Select(Label).ToList()));
        add["after"] = "Enabled";
        add["title"] = "Add a voice provider";
        add["hint"] = "Choose one: it's added, switched on";
        add["help"] = "Adds a provider to the list, switched on, with its usual address and all of its voices. Then set its key (most need one).";
        add["type"] = "string";
        add["pickOnly"] = true;
        add["rebuild"] = true;
        add["get"] = (Func<object?>)(() => "choose one");
        add["set"] = (Func<object, string?>)(v =>
        {
            var pick = ProviderLines.Addable.FirstOrDefault(a => Label(a) == v as string);
            if (pick.name == null) return null;
            var before = _cfg.ProviderLinesNow;
            return SaveProviderLines(before, ProviderLines.Added(before, pick.provider));
        });
        return add;
    }

    // ------------------------------------------------------------------ CF-07: the controller's buttons in play

    /// <summary>The controller's console button in play (when it's also the talk button, it talks while you look at someone).</summary>
    private static bool PadConsolePressed()
    {
        string console = Pad.Normal(_cfg.PadConsoleButton.Value);
        if (console is not ("LT" or "RT") || !Pad.Pressed(console)) return false; // review CF12-4: LT, RT or off, as offered
        return !(console == Pad.Normal(_cfg.PadTalkButton.Value) && _target != null);
    }

    private static bool PadTalkPressed() => Pad.Pressed(_cfg.PadTalkButton.Value);

    /// <summary>
    /// CF-11 (Nexus: waiters "simply stand there", no order could be placed): PadTalkButton is the game's interact button
    /// and the person is at work, so that button stays the game's (their order, their shop). T, or another PadTalkButton,
    /// still talks to them.
    /// </summary>
    private static bool PadInteractIsTheGames(Nivalis.Character c)
    {
        if (Pad.Normal(_cfg.PadTalkButton.Value) != "A") return false;
        float now = Time.unscaledTime;
        if (c.Pointer == _atWorkFor && now - _atWorkAt < 0.5f) return _atWork; // review RV-2: not every scan (and its log line)
        _atWorkFor = c.Pointer;
        _atWorkAt = now;
        return _atWork = GameContext.AtWork(c);
    }

    private static IntPtr _atWorkFor;
    private static float _atWorkAt = -10f;
    private static bool _atWork;

    /// <summary>
    /// PadTalkButton = Interact: that press on someone you can talk to opens the AI chat, and the game's own dialogue with
    /// that same person is skipped for it. Only for that person: the game's interaction is checked against who's under the
    /// crosshair now (review of a8971f9), so turning to someone else and pressing it lets the game act as usual. The pad is
    /// read straight away: the game may act on the press before the mod's frame.
    /// </summary>
    internal static bool PadTalkTakesInteract(Nivalis.Dialogue.StartDialogueInteraction? dialogue)
    {
        try
        {
            if (!Running || dialogue == null || Pad.Normal(_cfg.PadTalkButton.Value) != "A") return false;
            var pad = Gamepad.current;
            if (pad == null || !pad.buttonSouth.wasPressedThisFrame) return false;
            var target = TalkableNow();
            if (target == null) return false;
            // CF-11: someone at work: the game's own interaction with them (taking your order, their shop) goes ahead.
            if (GameContext.AtWork(target)) return false;
            var body = dialogue.GetComponentInParent<Nivalis.Character>();
            if (body != null && body.Pointer == target.Pointer) return true;
            var person = dialogue._character;
            var theirs = GameContext.PersonOf(target);
            return person != null && theirs != null && person.Pointer == theirs.Pointer;
        }
        catch { return false; }
    }

    /// <summary>
    /// CF-12: PadTalkButton is X, Y or B (the game's placement mode, pick up and put away): that press, while someone you can
    /// talk to is under the crosshair, opens the AI chat and the game's own action is skipped for it. Anywhere else, and with
    /// any other button set, the game acts as usual. Read straight from the pad: the game may act before the mod's frame.
    /// </summary>
    internal static bool PadTalkTakes(string button)
    {
        try
        {
            if (!Running || Pad.Normal(_cfg.PadTalkButton.Value) != button) return false;
            var pad = Gamepad.current;
            if (pad == null || !Pad.PressedNow(pad, button)) return false;
            // Review RV-1: the game acting on this press after the mod's frame, which opened a chat with it: still ours.
            if (_padTalkFrame == Time.frameCount) return true;
            if (ChatOpen || _consoleOpen || _menuOpen || _resetting || Time.frameCount == _menuClosedFrame) return false;
            // Review CF12-1 / RV-1: exactly where the talk press acts (in play, the prompt up, someone under the crosshair
            // at the press), never in a menu, a pause or the press that closed one: the game keeps that press.
            if (!InGameplay() || _target == null) return false;
            return TalkableNow() != null;
        }
        catch { return false; }
    }

    /// <summary>The frame a pad press opened a chat (review RV-1: the game's own action for that press is skipped too).</summary>
    private static int _padTalkFrame = -1;

    /// <summary>The talk prompt's button: the pad's while it's being used (and has one), the key otherwise.</summary>
    private static string TalkLabel()
    {
        string pad = Pad.Normal(_cfg.PadTalkButton.Value);
        return Pad.Active && pad != "OFF" ? Pad.Label(pad) : _cfg.TalkKey.Value.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// CF-12: the talk prompt's button as the game's own icon for this controller (Xbox or PlayStation), with the sprites it
    /// needs; null when the pad isn't in use or the game has no icon for it (the text label is shown instead).
    /// </summary>
    private static (string tag, TMPro.TMP_SpriteAsset sprites)? TalkIcon()
    {
        string pad = Pad.Normal(_cfg.PadTalkButton.Value);
        if (!Pad.Active || pad == "OFF") return null;
        return PadIcons.Try(pad, out var sprites, out var tag) ? (tag, sprites) : null;
    }
}

/// <summary>
/// FB-40: what Nivalis Switchboard reads of this mod (found by the marker on the assembly in Plugin.cs). Plain .NET types
/// only: neither mod references the other.
/// </summary>
public static class SettingsMenuPage
{
    public static IDictionary<string, object?> Describe() => ModRuntime.DescribeForMenu();
}
