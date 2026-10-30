using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.UI;
using NivalisAIChat.Voice;
using UnityEngine;

namespace NivalisAIChat;

/// <summary>
/// FB-45: the Setup Wizard (the first tab: 1 your AI, 2 voices (optional), 3 your usage, as guided steps) and the Features
/// tab (every feature's switch in one place, the older ones and the [Features] ones side by side).
/// Changes wait for the menu's Save like every page; the test buttons act at once and
/// say what they spend.
/// </summary>
public static partial class ModRuntime
{
    private const string WizardGroup = "Setup Wizard", FeaturesGroup = "Features";

    /// <summary>A provider to pick in step 1: how requests are sent, where, and the model to start with (null: keep yours).</summary>
    private sealed record AiPreset(string Name, ApiFormat Format, string Url, string? Model);

    private static readonly AiPreset[] AiPresets =
    {
        new("OpenAI", ApiFormat.OpenAI, "https://api.openai.com/v1", "gpt-5.4-nano"),
        new("Anthropic (Claude)", ApiFormat.Anthropic, "https://api.anthropic.com/v1/messages", "claude-haiku-5-5"),
        new("Google Gemini", ApiFormat.OpenAI, "https://generativelanguage.googleapis.com/v1beta/openai/", null),
        new("xAI (Grok)", ApiFormat.OpenAI, "https://api.x.ai/v1", null),
        new("DeepSeek", ApiFormat.OpenAI, "https://api.deepseek.com", null),
        new("Mistral", ApiFormat.OpenAI, "https://api.mistral.ai/v1", null),
        new("OpenRouter", ApiFormat.OpenAI, "https://openrouter.ai/api/v1", null),
        new("Groq", ApiFormat.OpenAI, "https://api.groq.com/openai/v1", null),
        new("Ollama (on this PC)", ApiFormat.OpenAI, "http://localhost:11434/v1", null),
        new("LM Studio (on this PC)", ApiFormat.OpenAI, "http://localhost:1234/v1", null),
    };
    private const string OtherProvider = "Something else (type its address below)";

    private static string AiPresetNow()
    {
        string url = _cfg.ApiUrl.Value.Trim().TrimEnd('/');
        return AiPresets.FirstOrDefault(p => string.Equals(p.Url.TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase))?.Name ?? OtherProvider;
    }

    private static bool LocalAi => _cfg.ApiUrl.Value.Contains("localhost", StringComparison.OrdinalIgnoreCase) || _cfg.ApiUrl.Value.Contains("127.0.0.1");
    private static bool AiReady => (_cfg.ApiKey.Value.Trim().Length > 0 || LocalAi) && _cfg.Model.Value.Trim().Length > 0;

    // ------------------------------------------------------------------ the tests (they act at once and say what they cost)

    private static string _aiTest = "", _voiceTest = "";
    private static bool _aiTesting, _voiceTesting;

    private static string? TestConnection()
    {
        if (_aiTesting) return "Already testing…";
        _aiTesting = true;
        _aiTest = "Testing your AI…";
        var client = new LlmClient(LlmSettingsNow, s => Log.Info(s)) { Budget = _budget, Ledger = _voiceLedger };
        Task.Run(async () =>
        {
            string result;
            try
            {
                var req = new LlmRequest { Purpose = "test", MaxTokens = 40 };
                req.System.Add("You are checking a connection. Answer with the single word: ready");
                req.Messages.Add(new LlmMessage("user", "Are you there?"));
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var res = await client.CompleteAsync(req, cts.Token).ConfigureAwait(false);
                result = res.Ok ? $"Works: {_cfg.Model.Value.Trim()} answered." : "Didn't work: " + Redact.Text(res.Error ?? "no answer", _cfg.ApiKey.Value);
            }
            catch (Exception e) { result = "Didn't work: " + Redact.Text(e.Message, _cfg.ApiKey.Value); }
            MainThread.Enqueue(() => { _aiTest = result; _aiTesting = false; });
        });
        return "Testing your AI (one short request)…";
    }

    private static string? TestVoices()
    {
        if (_voiceTesting) return "Already testing…";
        if (_voices == null || !_cfg.VoicesEnabled.Value) return "Voices are off: switch them on and save first.";
        var slots = _cfg.VoiceSlots.ToList();
        if (slots.Count == 0) return "No voice provider is on: switch one on and save first.";
        _voiceTesting = true;
        _voiceTest = "Testing voices…";
        var voices = _voices;
        float volume = _cfg.VoiceVolume.Value / 100f;
        Task.Run(async () =>
        {
            var said = new List<string>();
            foreach (var slot in slots)
            {
                string name = Short(VoiceService.LedgerName(slot));
                try
                {
                    var (audio, error) = await voices.TestAsync(slot, "Hello! This is how I sound in Nivalis.", CancellationToken.None).ConfigureAwait(false);
                    if (audio == null) { said.Add($"{name}: didn't work ({error})"); continue; }
                    float seconds = 0f;
                    var done = new TaskCompletionSource<bool>();
                    MainThread.Enqueue(() =>
                    {
                        seconds = VoicePlayer.PlayFlat(audio, volume); // heard flat, also from a menu
                        done.TrySetResult(true);
                    });
                    await done.Task.ConfigureAwait(false);
                    said.Add($"{name}: works");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds + 0.4f, 0.5f, 8f))).ConfigureAwait(false);
                }
                catch (Exception e) { said.Add($"{name}: didn't work ({Redact.Text(e.Message)})"); }
            }
            string result = string.Join(" · ", said);
            MainThread.Enqueue(() => { _voiceTest = result; _voiceTesting = false; });
        });
        return "Testing voices: listen…";
    }

    // ------------------------------------------------------------------ the wizard's rows

    private static List<object> WizardRowsSafe()
    {
        var rows = new List<object>();
        try { WizardRows(rows); }
        catch (Exception e)
        {
            Log.Warn($"Setup Wizard: part of the tab couldn't be shown ({e.GetType().Name}: {e.Message}) at {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
            rows.Add(Row("note", "Part of this tab couldn't be shown", e.GetType().Name, "Something went wrong building the rest of this tab. LogOutput.log says where."));
        }
        return rows;
    }

    /// <summary>A switch, field or dropdown bound to one setting (staged by the menu until Save, applied as a menu change).</summary>
    private static Dictionary<string, object?> SettingRow<T>(ConfigEntry<T> entry, SettingControl control, string title, string hint, string help)
    {
        var row = SettingsCatalog.ToContract(control);
        row["id"] = IdOf(entry); // another row can fill it in (the provider: its address and model)
        row["title"] = title;
        row["hint"] = hint;
        row["help"] = help;
        row["type"] = typeof(T) == typeof(bool) ? "bool" : typeof(T) == typeof(string) ? "string" : "double";
        row["get"] = (Func<object?>)(() => typeof(T) == typeof(int) ? Convert.ToDouble(entry.Value, CultureInfo.InvariantCulture) : (object?)entry.Value);
        row["set"] = (Func<object, string?>)(v =>
        {
            object value = typeof(T) == typeof(int) ? (int)Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture))
                : typeof(T) == typeof(string) ? (v as string ?? "").Trim() : v;
            entry.BoxedValue = value;
            AfterMenuChange(entry.Definition.Key);
            InvalidateOptimizer();
            return null;
        });
        return row;
    }

    /// <summary>A setting's row id on these tabs: "setting:Section/Key".</summary>
    private static string IdOf(ConfigEntryBase entry) => "setting:" + entry.Definition.Section + "/" + entry.Definition.Key;

    /// <summary>The model after picking <paramref name="p"/>: a model from another listed provider wouldn't answer here, so it
    /// starts with this one's; yours is kept otherwise.</summary>
    private static string PresetModel(AiPreset p, string model)
    {
        model = model.Trim();
        return p.Model != null && (model.Length == 0 || AiPresets.Any(o => o.Model != null && o != p && string.Equals(o.Model, model, StringComparison.OrdinalIgnoreCase)))
            ? p.Model : model;
    }

    private static Dictionary<string, object?> Buttons(string hint, string help, params Dictionary<string, object?>[] buttons) =>
        new(StringComparer.Ordinal) { ["kind"] = "buttons", ["title"] = "", ["hint"] = hint, ["help"] = help, ["buttons"] = buttons.Cast<object>().ToList() };

    private static Dictionary<string, object?> Button(string label, Func<string?> click, bool main = false) =>
        new(StringComparer.Ordinal) { ["label"] = label, ["click"] = click, ["main"] = main };

    private static void WizardRows(List<object> rows)
    {
        // ---- 1: your AI
        rows.Add(StepRow(1, "Your AI", "Who people talk with: pick a provider, then your key and a model. Save when you're done.",
            () => AiReady ? ("ready", "ok") : ("needs a key", "warn"),
            () => AiReady ? "done" : "now"));
        var provider = SettingsCatalog.ToContract(new SettingControl(ControlKind.Choice, Choices: AiPresets.Select(p => p.Name).Append(OtherProvider).ToList()));
        provider["title"] = "Provider";
        provider["hint"] = "Fills in how and where requests go";
        provider["help"] = "Pick where your key is from. It sets ApiFormat and ApiUrl (the AI Connection tab), and for OpenAI and Anthropic a " +
                           "model to start with. \"Something else\" keeps the address below for you to type.";
        provider["type"] = "string";
        provider["get"] = (Func<object?>)AiPresetNow;
        provider["set"] = (Func<object, string?>)(v =>
        {
            var p = AiPresets.FirstOrDefault(x => x.Name == v as string);
            if (p == null) return null; // something else: the address row says where
            _cfg.Format.Value = p.Format;
            _cfg.ApiUrl.Value = p.Url;
            string model = PresetModel(p, _cfg.Model.Value);
            if (model != _cfg.Model.Value.Trim()) _cfg.Model.Value = model;
            AfterMenuChange("ApiUrl");
            return null;
        });
        // Picked: its address and model show at once below, waiting for Save with it (menu 0.4.1).
        provider["fills"] = (Func<object, System.Collections.IDictionary?>)(v =>
        {
            var p = AiPresets.FirstOrDefault(x => x.Name == v as string);
            if (p == null) return null; // something else: the address is typed
            return new Dictionary<string, object?> { [IdOf(_cfg.ApiUrl)] = p.Url, [IdOf(_cfg.Model)] = PresetModel(p, _cfg.Model.Value) };
        });
        rows.Add(provider);
        rows.Add(SettingRow(_cfg.ApiUrl, new SettingControl(ControlKind.Text), "Address", "Where requests go", "ApiUrl: the provider's address. The provider above fills it in; type it for one that isn't listed."));
        var key = ProviderRow("secret", "Key", LocalAi ? "Not needed for a server on this PC" : "From your provider's website",
            "ApiKey: your key from the provider. It's never shown: only whether it's set. Not needed for a server on your own PC.", "string");
        key.Remove("after");
        key["get"] = (Func<object?>)(() => _cfg.ApiKey.Value.Trim().Length > 0 ? "set" : "");
        key["set"] = (Func<object, string?>)(v =>
        {
            _cfg.ApiKey.Value = (v as string ?? "").Trim();
            AfterMenuChange("ApiKey");
            return null;
        });
        rows.Add(key);
        rows.Add(SettingRow(_cfg.Model, new SettingControl(ControlKind.Text), "Model", "The model people talk with",
            "Model: as your provider names it (gpt-5.4-nano, claude-haiku-5-5, gemini flash models...). The author's pick: gpt-5.4-nano on OpenAI."));
        rows.Add(SettingRow(_cfg.BackgroundModel, new SettingControl(ControlKind.Text), "Background model", "Optional: a cheaper one for residents",
            "BackgroundModel: a cheaper model for behind-the-scenes work (new people, diaries, residents' talk). Empty = your model."));
        var test = Buttons("Uses your saved settings. Sends one short request: a few tokens.",
            "Asks your AI one short question with what's saved, and says here whether it answered (and why not, if it didn't).",
            Button("Test connection", TestConnection));
        test["hintLive"] = (Func<string?>)(() => _aiTest.Length > 0 ? _aiTest : "Uses your saved settings. Sends one short request: a few tokens.");
        rows.Add(test);

        // ---- 2: voices (optional)
        rows.Add(StepRow(2, "Voices", "Optional: people speak their lines out loud. Add a provider and its key, or leave voices off.",
            () => !_cfg.VoicesEnabled.Value ? ("off", "") : _cfg.VoiceSlots.Count == 0 ? ("no provider on", "warn") : ($"{_cfg.VoiceSlots.Count} on", "ok"),
            () => !AiReady ? "" : !_cfg.VoicesEnabled.Value || _cfg.VoiceSlots.Count > 0 ? "done" : "now"));
        var voicesOn = SettingRow(_cfg.VoicesEnabled, new SettingControl(ControlKind.Switch), "Voices", "People speak their lines",
            "Voices Enabled: people speak their lines with your voice providers. Off: lines are shown only (nothing spent on voices).");
        voicesOn["rebuild"] = true; // FB-51: the providers below come and go with it
        rows.Add(voicesOn);
        if (_cfg.VoicesEnabled.Value)
        {
            var all = ProviderLines.Read(_cfg.ProviderLinesNow);
            for (int k = 0; k < all.Count; k++)
            {
                int index = k;
                var e = all[k];
                string name = ProviderLines.Name(e.Provider);
                rows.Add(Subsection(name));
                var sw = ProviderRow("switch", "Switched on", e.Provider == VoiceProvider.Windows ? "The voices installed in Windows (free)" : "Use it for people's voices",
                    $"Use {name} for people's voices. With more than one on, each person gets one of them.", "bool", rebuild: true);
                sw.Remove("after");
                sw["get"] = (Func<object?>)(() => ProviderAt(index)?.On ?? false);
                sw["set"] = (Func<object, string?>)(v => SaveProvider(index, p => (p with { On = v is true }, null)));
                rows.Add(sw);
                if (e.On)
                {
                    if (e.Provider is not (VoiceProvider.Windows or VoiceProvider.OpenAICompatible))
                    {
                        var kr = KeyRow(index, e, name);
                        kr.Remove("after");
                        Retitle(kr, name, e);
                        rows.Add(kr);
                    }
                    if (VoiceService.Billed(new VoiceSlot { Provider = e.Provider, Url = e.Endpoint }))
                        foreach (var r in LimitRows(index, e, name))
                        {
                            if (r is not Dictionary<string, object?> d || d.TryGetValue("title", out var t) && t is string ts && ts.EndsWith(" use", StringComparison.Ordinal)) continue;
                            d.Remove("after");
                            Retitle(d, name, e);
                            rows.Add(d);
                        }
                }
                var rm = RemoveRow(index, name); // on or off, it can go
                rm.Remove("after");
                Retitle(rm, name, e);
                rows.Add(rm);
            }
            rows.Add(Subsection("Another provider"));
            var add = AddProviderRow();
            add.Remove("after");
            rows.Add(add);
            var vt = Buttons("Uses your saved providers. Speaks one short line with each: a few characters each.",
                "Says one short line with each provider that's on, at your character, and says here which worked.",
                Button("Test voices", TestVoices));
            vt["hintLive"] = (Func<string?>)(() => _voiceTest.Length > 0 ? _voiceTest : "Uses your saved providers. Speaks one short line with each: a few characters each.");
            rows.Add(vt);
        }

        // ---- 3: your usage
        rows.Add(StepRow(3, "Your usage", "Set what you're happy to spend; the plan below keeps the city as lively as that allows.",
            () =>
            {
                var v = Optimizer();
                if (!v.Best.Limited) return ("no limit: as you set it", ""); // FB-59
                return Changes(v, v.Best).Count == 0 ? ("plan in use", "ok") : ("plan ready", "warn");
            },
            () =>
            {
                if (!AiReady) return "";
                var v = Optimizer();
                return v.Best.Limited && Changes(v, v.Best).Count == 0 ? "done" : "now";
            }));
        rows.Add(SettingRow(_cfg.TokenLimit, new SettingControl(ControlKind.Number, 0, 2_000_000_000), "AI token limit", "The most AI tokens per period (0 = no limit)",
            "TokenLimit: the most tokens the mod may use with your AI per period. The plan keeps everything under it with headroom left."));
        rows.Add(SettingRow(_cfg.TokenLimitPer, new SettingControl(ControlKind.Choice, Choices: new[] { "Day", "Week", "Month" }), "Duration", "How long each limit lasts",
            "TokenLimitPer: day (from midnight), week (from Monday) or month (from its reset day, in the Usage Optimizer)."));
        var plan = Row("note", "Recommended plan", "", "");
        plan["get"] = (Func<object?>)(() =>
        {
            var v = Optimizer();
            // FB-59: with no limit, what your settings come to a day (nothing to keep within).
            if (!v.Best.Limited) return "no limit · about " + string.Join(", ", v.Budgets.Where(b => !b.Free).Select(b => $"{Big(v.PerHourNow[b.Key] * v.HoursPerDay)} {(b.Ai ? "AI tokens" : Short(b.Name))}")) + " a day";
            var parts = v.Budgets.Where(b => b.Capped).Select(b => $"{(b.Ai ? "AI" : Short(b.Name))} {v.Best.AtReset[b.Key]:P0}");
            return $"{v.Best.Activity:P0} lively · " + string.Join(" · ", parts);
        });
        plan["hintLive"] = (Func<string?>)(() =>
        {
            var v = Optimizer();
            if (!v.Best.Limited) return "No limit set: your settings as they are. Set a limit to plan within it";
            int n = Changes(v, v.Best).Count;
            return n == 0 ? "Your settings already follow it" : $"{n} setting{(n == 1 ? "" : "s")} would change";
        });
        plan["helpLive"] = (Func<string?>)(() => PlanSummary(Optimizer()));
        rows.Add(plan);
        rows.Add(Buttons("Fine-tune it any time in the Usage Optimizer, just below.",
            "Writes the recommended settings now (the same as Apply in the Usage Optimizer, at its best level).",
            Button("Use this plan", () => { if (_usagePlan != null) _usagePlan.Dial = null; _dialPreview = null; InvalidateOptimizer(); return ApplyOptimizer(); }, main: true)));
    }

    // ------------------------------------------------------------------ the Features tab

    /// <summary>One feature's switch: a [Features] setting, or an older one from another tab (the same setting either way).</summary>
    private static Dictionary<string, object?> FeatureRow(ConfigEntry<bool> entry, string title, string hint) =>
        SettingRow(entry, new SettingControl(ControlKind.Switch), title, hint, (entry.Description?.Description ?? "") + " (" + entry.Definition.Section + " / " + entry.Definition.Key + ")");

    /// <summary>The Features tab: one row that can't be built leaves a note, not an empty tab (as the wizard's and the optimizer's).</summary>
    private static List<object> FeatureRowsSafe()
    {
        try { return FeatureRows(); }
        catch (Exception e)
        {
            Log.Warn($"Features: the tab couldn't be shown ({e.GetType().Name}: {e.Message}) at {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
            return new List<object> { Row("note", "This tab couldn't be shown", e.GetType().Name, "Something went wrong building it. The switches are still in PeopleOfNivalis.cfg. LogOutput.log says where.") };
        }
    }

    private static List<object> FeatureRows() => new()
    {
        Subsection("Residents"),
        FeatureRow(_cfg.Chatter, "Residents' conversations", "Talk among themselves near you"),
        FeatureRow(_cfg.FeaturePhoneCalls, "Phone calls", "Residents take calls"),
        FeatureRow(_cfg.FeatureMuttering, "Thinking out loud", "Residents mutter to themselves"),
        FeatureRow(_cfg.FeatureTables, "Table conversations", "People at a table talk as their table"),
        FeatureRow(_cfg.OrderTalk, "Order talk at food places", "Staff and customers order out loud"),
        FeatureRow(_cfg.FeatureOverhearing, "Overhearing", "People near a talk remember what they heard"),
        Subsection("Around you"),
        FeatureRow(_cfg.NpcsGreetYou, "Street greetings", "People you know greet you"),
        FeatureRow(_cfg.Gossip, "Gossip", "Talk about you goes around"),
        FeatureRow(_cfg.CityNews, "City news", "The holos' headlines come up"),
        FeatureRow(_cfg.MarketTalk, "Market talk", "Food prices come up"),
        Subsection("People and you"),
        FeatureRow(_cfg.FeatureMoods, "Moods", "People have moods"),
        FeatureRow(_cfg.FeatureRelationships, "Relationships", "How each person feels about you"),
        FeatureRow(_cfg.Favours, "Food orders", "People ask you to cook for them"),
        FeatureRow(_cfg.RememberDoings, "What they did", "People remember where you saw them"),
        FeatureRow(_cfg.FeatureWorkLife, "Your staff's work life", "Pay, hours and long shifts, remembered"),
        FeatureRow(_cfg.FeatureFollowing, "Following", "People come along when you ask"),
        FeatureRow(_cfg.FeatureRomance, "Romance", "Dating, dates, living together"),
        FeatureRow(_cfg.FeatureReminders, "Plans and reminders", "Pop up when they're due"),
        FeatureRow(_cfg.FeatureGroupChat, "Group chat", "Talk with a whole group at once"),
        FeatureRow(_cfg.FeatureWorldChat, "World chat", "Enter: talk to the people in front of you"),
        FeatureRow(_cfg.FeatureVoiceInput, "Voice input", "Talk with your microphone"),
        FeatureRow(_cfg.LookAtNewPeople, "Looking at new people", "A picture of each new person"),
        Subsection("Story characters"),
        FeatureRow(_cfg.ChatWithNamedNpcs, "Chat with story characters", "T talks with them too"),
        FeatureRow(_cfg.FeatureStoryMemory, "Story conversations", "They remember your story with them"),
        FeatureRow(_cfg.FeatureStoryVoice, "Story voice", "They talk the way they do in the story"),
        Subsection("Memory and voices"),
        FeatureRow(_cfg.MemoriesPerSave, "Memories per save", "Kept with each game save"),
        FeatureRow(_cfg.MeetingSummaries, "Meeting summaries", "People write up each talk"),
        FeatureRow(_cfg.LifeUpdates, "Life updates", "Their lives move on between meetings"),
        FeatureRow(_cfg.VoicesEnabled, "Voices", "People speak their lines"),
    };
}
