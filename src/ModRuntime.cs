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

/// <summary>
/// The whole mod behind four static entry points (Start / Update / LateUpdate / Stop), so the
/// BepInEx plugin — or a development hot-loader — only has to pump it.
/// </summary>
public static partial class ModRuntime
{
    private static ModConfig _cfg = null!;
    private static Harmony? _harmony;
    private static NpcStore _store = null!;
    private static NpcBrain _brain = null!;
    /// <summary>The AI client every brain uses (FB-61: a brain opened again after a load takes the same one).</summary>
    private static LlmClient _llm = null!;
    private static NpcBrain? _mockBrain;
    private static readonly ChatWindow Ui = new();
    private static readonly PlayerLock Locks = new();
    private static readonly ConcurrentQueue<Action> MainThread = new();
    private static DevCommands? _dev;
    private static bool _useMock;

    private static ChatSession? _session;
    private static Character? _npc;
    private static NpcVisual? _visual;
    private static CancellationTokenSource? _cts;
    private static Task? _inflight;
    /// <summary>Chats still being saved after they closed (each waits for its last reply first): Stop waits for all of them.</summary>
    private static readonly List<Task> _closing = new();
    private static bool _busy;
    private static float _closeAt = -1;
    private static float _diagAt = -1;
    private static string _queued = "";
    private static string _openActivity = "";
    private static float _idle;
    private static int _silentStage;
    private static string _lastInputText = "";
    private static int _openSerial;

    private static float _nextScan;
    private static Character? _target;
    private static string? _targetPrompt;
    /// <summary>The sprites the talk prompt's controller icon comes from (CF-12); null with a text label.</summary>
    private static TMPro.TMP_SpriteAsset? _targetSprites;
    private static readonly Dictionary<string, string> KnownNames = new();
    private static Key _talkKey = Key.T;
    private static string _minorAges = "";
    /// <summary>Goes up on every reset, so a conversation that was still starting is dropped instead of saved.</summary>
    private static int _resetGeneration;
    private static string _dataDir = "";

    // social: city memory, what this conversation knows about the world, food in your hands, delayed jobs
    private static WorldMemory? _world;
    private static List<string> _rumors = new(), _headlines = new();
    private static List<MenuEntry> _menu = new();
    private static List<string> _market = new();
    private static bool _marketOnMind, _marketUnknown, _eatsAtHome;
    private static List<string> _foodPlaces = new();
    private static List<FoodScene.Place> _townPlaces = new();
    private static int _townPlacesDay = int.MinValue;
    private static float _nextGivePoll;
    private static readonly List<(float at, Action run)> Delayed = new();

    // voices: who sounds like whom (voices.json) and the providers
    private static VoiceService _voices = null!;
    private static float _resetAllAskedAt = -100f;
    /// <summary>A /resetnpc or /resetall is moving files: no new chats or greetings until it's done.</summary>
    private static volatile bool _resetting;

    public static bool Running { get; private set; }
    internal static ModConfig Config => _cfg;
    internal static bool ChatOpen => _session != null || _npc != null;
    internal static ChatSession? Session => _session;
    internal static Character? ChatNpc => _npc;
    internal static NpcStore Store => _store;

    public static void Start(ConfigFile file, ManualLogSource log, string dataDir)
    {
        if (Running) return;
        Log.Source = log;
        // A hot-reloaded copy (dev loader) has its own enum types, so config entries bound by an earlier copy can't be
        // shared: read the same file through a fresh ConfigFile instead.
        bool hotReloaded = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(ModRuntime).Assembly) != System.Runtime.Loader.AssemblyLoadContext.Default;
        if (hotReloaded)
            file = new ConfigFile(Path.Combine(Path.GetDirectoryName(file.ConfigFilePath)!, Plugin.Guid + ".cfg"), false);
        // The first start of a new version brings the settings file's explanations up to date (FB-23); never a dev copy.
        ModConfig.RefreshForVersion = hotReloaded ? "" : Plugin.Version;
        _cfg = new ModConfig(file);
        SyncFeatures();
        foreach (var note in Plugin.EarlyNotes) Log.Warn("Settings file: " + note); // fixed before BepInEx read it (ROBUST-12)
        // The settings file's own problems ([CFG-READ]: lines it doesn't read, a key it stopped using) are said in the game;
        // a reload an in-game change made first gets the same follow-up as any other (step 5 re-reviews).
        _cfg.Report = message => Problem(message);
        _cfg.Reloaded = (changed, error) => AfterReload(changed, error, auto: true);
        WarnVoiceProblems();
        VoicePlayer.UseGameVolume = _cfg.UseGameVolume.Value;
        VoicePlayer.BehindVolume = Mathf.Clamp(_cfg.VoiceBehind.Value, 0, 100) / 100f;
        VoicePlayer.MuffleOn = _cfg.VoiceMuffle.Value;
        _dataDir = dataDir;
        CustomInstructions.Init(dataDir, s => Log.Warn(s));
        Directory.CreateDirectory(dataDir);
        Log.Verbose = _cfg.DevCommands.Value;
        ParseKey();
        // Its own handler (BepInEx runs each one in its own try): the chatter settings are built again after any change,
        // whatever another handler does (D3).
        _chatterSettings = null;
        file.SettingChanged += (_, _) => { SyncFeatures(); _chatterSettings?.Invalidate(); };
        file.SettingChanged += (_, _) =>
        {
            ParseKey();
            ParseConsoleKey();
            ParsePhoneKey();
            ParseWorldKey();
            ParseCameraKey(); // FB-81
            Log.Verbose = _cfg.DevCommands.Value;
            VoicePlayer.BehindVolume = Mathf.Clamp(_cfg.VoiceBehind.Value, 0, 100) / 100f;
            VoicePlayer.MuffleOn = _cfg.VoiceMuffle.Value;
            if (VoicePlayer.UseGameVolume != _cfg.UseGameVolume.Value) VoicePlayer.GameVolumeChanged(_cfg.UseGameVolume.Value); // applies now (#13b)
            string ages = $"{_cfg.MinAge.Value}|{_cfg.MaxAge.Value}|{_cfg.ChatWithNamedNpcs.Value}";
            if (ages != _minorAges) { _minorAges = ages; MinorCache.Clear(); } // who counts as a minor only changes with these
        };
        ParseConsoleKey();
        ParsePhoneKey(); // found with FB-76: a PhoneKey of your own only took effect after some setting changed
        ParseWorldKey();
        ParseCameraKey(); // FB-81
        _minorAges = $"{_cfg.MinAge.Value}|{_cfg.MaxAge.Value}|{_cfg.ChatWithNamedNpcs.Value}";

        // Everyone known to be under 18, kept apart from their records (a reset or a lost file can't undo it).
        _minors = new MinorList(Path.Combine(dataDir, MinorList.FileName), Log.Warn);
        _minors.Added += OnMinorAdded;
        var llm = new LlmClient(LlmSettingsNow, s => Log.Info(s));
        _llm = llm;
        _budget = new TokenBudget(() => (_cfg.TokenLimit.Value, TokenBudget.SpanOf(_cfg.TokenLimitPer.Value) ?? TokenBudget.Span.Day),
            Path.Combine(dataDir, "usage.json"), Log.Warn) { ResetDay = () => _cfg.TokenLimitResetDay.Value };
        llm.Budget = _budget;
        _voyage = new VoyageClient(() => new VoyageSettings { Key = _cfg.VoyageKey.Value, Model = _cfg.VoyageModel.Value });
        // FB-61: the per-save books (people, memory, pairs, groups, gossip, visits, story), opened again after a load.
        OpenSaveData(dataDir);
        StartSaves(dataDir, hotReloaded);
        KeepDayCount(dataDir); // FB-78: the day count's line-up, for a session that starts at night
        _voices = new VoiceService(VoiceSettingsNow, new VoiceBook(Path.Combine(dataDir, "voices.json"), Log.Warn), s => Log.Info(s));
        _voiceBudget = new TokenBudget(() => (_cfg.VoiceLimit.Value, TokenBudget.SpanOf(_cfg.VoiceLimitPer.Value) ?? TokenBudget.Span.Day),
            Path.Combine(dataDir, "voice_usage.json"), Log.Warn, TokenBudget.Wording.Voice) { ResetDay = () => _cfg.VoiceLimitResetDay.Value };
        _voices.Budget = _voiceBudget;
        // FB-75: speech to text counted (seconds) and limited like the AI and the voices.
        _speechBudget = new TokenBudget(() => (Math.Max(0, _cfg.SpeechToTextLimit.Value) * 60L, TokenBudget.SpanOf(_cfg.SpeechToTextLimitPer.Value) ?? TokenBudget.Span.Day),
            Path.Combine(dataDir, "speech_usage.json"), Log.Warn, TokenBudget.Wording.Speech) { ResetDay = () => _cfg.SpeechToTextLimitResetDay.Value };
        _voiceLedger = new VoiceLedger(Path.Combine(dataDir, "voice_ledger.json"), Log.Warn); // FB-41: what every line cost, per provider
        _voices.Ledger = _voiceLedger;
        try { _usagePlan = new UsagePlanBook(Path.Combine(dataDir, "usage_plan.json"), Log.Warn); } // FB-41: the Usage Optimizer's plan
        catch (Exception e) { Log.Warn("The Usage Optimizer's plan couldn't be opened: " + e.Message); }
        llm.Ledger = _voiceLedger; // the AI's tokens by purpose too
        if (_voyage != null) _voyage.Ledger = _voiceLedger;

        _harmony = new Harmony(Plugin.Guid);
        // One patch at a time: if a game update renames a method, only that hook is lost.
        foreach (var t in typeof(ModRuntime).Assembly.GetTypes())
        {
            if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
            try
            {
                if (SharedNativeBody(t) is { } why) { Log.Warn($"Hook {t.Name} skipped: {why}"); continue; }
                _harmony.CreateClassProcessor(t).Patch();
            }
            catch (Exception e)
            {
                Log.Warn(t.DeclaringType == typeof(ConfigGuard)
                    ? $"Settings file guard not installed ({e.GetBaseException().Message}): another settings menu's Apply could rewrite PeopleOfNivalis.cfg (keep a copy of its Providers list)."
                    : $"Hook {t.Name} unavailable ({e.GetBaseException().Message}); NPCs may not hold perfectly still.");
            }
        }
        if (_cfg.DevCommands.Value) _dev = new DevCommands(dataDir);
        Running = true;
        try { StartPlugins(); } catch (Exception e) { Log.Warn("Plugins: " + e.Message); } // FB-67

        string key = _cfg.ApiKey.Value.Trim();
        Log.Info($"People of Nivalis {Plugin.Version} (beta) ready. Talk key: {_talkKey}. Endpoint: {_cfg.Format.Value} {Redact.Url(LlmClient.ResolveUrl(_cfg.Format.Value, _cfg.ApiUrl.Value))}, " +
                 $"model {Redact.Value(_cfg.Model.Value)}{(_cfg.BackgroundModel.Value.Trim().Length > 0 ? " / background " + Redact.Value(_cfg.BackgroundModel.Value) : "")}" +
                 $"{(_cfg.BackgroundApiUrl.Value.Trim().Length > 0 ? " at " + Redact.Url(_cfg.BackgroundApiUrl.Value) + $" (key {(_cfg.BackgroundApiKey.Value.Trim().Length > 0 ? "set" : "NOT set")})" : "")}, " +
                 $"key {(key.Length > 0 ? "set" : "NOT set")}. NPC memories: {Path.Combine(dataDir, "npcs")}");
    }

    /// <summary>
    /// FB-61: the per-save books: the people's files, their memory index, pairs, groups, gossip and news, visits, and story
    /// conversations, from the working copy in <paramref name="dataDir"/>. Start opens them; a load (ModRuntime.Saves)
    /// closes them, puts the slot's files back and opens them again, so nothing of the other save stays in memory.
    /// </summary>
    private static void OpenSaveData(string dataDir)
    {
        try { _reminders = new ReminderBook(Path.Combine(dataDir, "reminders.json"), Log.Warn); } // FB-65
        catch (Exception e) { Log.Warn("Reminders unavailable: " + e.Message); }
        try { _couples = new CoupleBook(Path.Combine(dataDir, "romance.json"), Log.Warn); } // FB-63
        catch (Exception e) { Log.Warn("Who's together (romance) unavailable: " + e.Message); _couples = null; }
        _store = new NpcStore(Path.Combine(dataDir, "npcs"), Log.Warn);
        _brain = new NpcBrain(_llm, _store, BrainSettingsNow, s => Log.Info(s), _minors);
        _memory = new MemoryIndexer(_voyage!, _store, new MemoryStore(Path.Combine(dataDir, "memory"), Log.Warn), () => VoyageOn,
            s => Log.Info(s), s => Problem(s, "MEM-VOYAGE"), _minors, () => Math.Max(0, _cfg.MemoryIndexLimit.Value));
        _brain.Memory = _memory;
        try
        {
            _pairs = new PairBook(Path.Combine(dataDir, "pairs.json"), Log.Warn)
            {
                // Residents move at the player's pace, one step per conversation (the author's choice), from the settings now.
                Step = rating => Relationships.Step(rating, Amount(_cfg.RelationshipGainMax.Value, 0, 5, 0.5), Amount(_cfg.RelationshipLossMin.Value, 0, 5, 0.1),
                    Amount(_cfg.RelationshipLossMax.Value, 0, 5, 0.5)),
            };
            _chatter = new ChatterBrain(_brain, _llm, _pairs, s => Log.Info(s), s => Problem(s));
            try { _circles = new CircleBook(Path.Combine(dataDir, "circles.json"), Log.Warn); _chatter.Circles = _circles; } // before the under-18 scan
            catch (Exception e) { Log.Warn("Group memory unavailable: " + e.Message); }
            _chatter.Couples = _couples; // FB-63: residents' love lives
            _chatter.RomanceRules = () => (Features.Romance && _cfg.ResidentsDate.Value, Features.Romance && _cfg.Jealousy.Value);
        }
        catch (Exception e) { Log.Warn("Residents' conversations unavailable: " + e.Message); }
        try { _world = new WorldMemory(Path.Combine(dataDir, "world.json"), Log.Warn); _brain.World = _world; }
        catch (Exception e) { Log.Warn("City memory (gossip, news) unavailable: " + e.Message); }
        try { GameContext.Visits = new VisitBook(Path.Combine(dataDir, "visits.json"), Log.Warn); }
        catch (Exception e) { Log.Warn("Customer visits unavailable: " + e.Message); }
        // FB-50: the story conversations the player has seen, per story character.
        try
        {
            _story = new StoryBook(Path.Combine(dataDir, "story.json"), Log.Warn);
            _brain.Story = _story;
            StoryDialogue.Book = _story;
            StoryDialogue.PlayerName = () => GameContext.Capture(null).PlayerName;
            StoryDialogue.KnownMinor = id => _brain?.KnownMinors.Contains(id) == true;
            StoryDialogue.Now = () => { var now = GameContext.Capture(null); return (now.GameDay, now.Clock); };
            StoryDialogue.Later = Later;
            StoryDialogue.Ended = (id, name, _) => StorySheetSoon(id, name);
        }
        catch (Exception e) { Log.Warn("Story conversations unavailable: " + e.Message); }
        OpenMindBooks(dataDir); // the inner-life round: feelings and what people saw (minds.json, witness.json), per save
        OpenPhoneBook(dataDir); // the phone: contacts and texts (phone.json), per save
        // Only now (the indexer can flag someone, and what others kept about them needs the pairs and gossip in place).
        CheckMemoryMode(startup: true);
        // Seeded in the background from every saved record that says they're under 18. Then everyone on the list has what
        // others kept about them cleared again (a no-op when it's gone already), in case the game closed before that ran
        // last time. A reset waits for all of this first.
        var minors = _minors!;
        _minorScan = Task.Run(() =>
        {
            try
            {
                int found = minors.AddRange(Minors.Scan(Path.Combine(dataDir, "npcs")).Concat(Minors.Scan(Path.Combine(dataDir, "npcs-mock"))));
                if (found > 0) Log.Info($"{found} people found to be under 18 in their saved files: never chatted with or sent to any AI.");
                foreach (var id in minors.All) ForgetUnder18(id);
            }
            catch (Exception e) { Log.Warn("Checking saved people for anyone under 18 failed: " + e.Message); }
            // Warm-ups for a data folder with nobody in it yet (the scan above did these for everyone else): the first
            // chat and the first named character don't pay for them on the main thread (D8, PERF-11).
            try
            {
                NpcStore.WarmUp();
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(CanonAge).TypeHandle);
                CanonAge.FromSeen("34", unreadableIsChild: false);
            }
            catch (Exception e) { Log.Debug("warm-up: " + e.Message); }
        });
    }

    /// <summary>
    /// FB-61: closes the per-save books (what's waiting is written, and then nothing more), as Stop always did: a late
    /// write from the old ones is refused, so the next save's files are never written over.
    /// </summary>
    private static void CloseSaveData()
    {
        try { _memory?.Stop(); } catch (Exception e) { Log.Warn("Stopping memory indexing: " + e.Message); }
        try { _pairs?.Close(); } catch (Exception e) { Log.Warn("Saving pairs: " + e.Message); }
        try { _circles?.Close(); } catch (Exception e) { Log.Warn("Saving groups: " + e.Message); }
        try { GameContext.Visits?.Close(); } catch (Exception e) { Log.Warn("Saving visits: " + e.Message); }
        StoryDialogue.Book = null;
        try { _story?.Close(); } catch (Exception e) { Log.Warn("Saving story conversations: " + e.Message); }
        try { _store?.Close(); } catch (Exception e) { Log.Warn("Closing the people's files: " + e.Message); }
        try { _world?.Close(); } catch (Exception e) { Log.Warn("Closing gossip and news: " + e.Message); }
        try { _reminders?.Close(); } catch (Exception e) { Log.Warn("Closing reminders: " + e.Message); }
        try { _couples?.Close(); } catch (Exception e) { Log.Warn("Closing who's together: " + e.Message); }
        CloseMindBooks(); // the inner-life round
        ClosePhoneBook(); // the phone
    }

    /// <summary>
    /// IL2CPP folds identical method bodies, so an empty or trivial method shares its native code with thousands
    /// of unrelated methods. Hooking one of those hooks them all and crashes the runtime. Refuse such targets.
    /// </summary>
    private static unsafe string? SharedNativeBody(Type patchClass)
    {
        var info = HarmonyMethod.Merge(HarmonyMethodExtensions.GetFromType(patchClass));
        if (info?.declaringType == null || info.methodName == null) return null;
        var target = AccessTools.Method(info.declaringType, info.methodName);
        if (target == null) return null;
        var field = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(target);
        if (field == null) return null;
        var methodInfo = (IntPtr)field.GetValue(null)!;
        if (methodInfo == IntPtr.Zero) return null;
        byte* code = *(byte**)methodInfo; // Il2CppMethodInfo.methodPointer is the first field
        if (code == null) return "no native code";
        // ret (C3), ret imm16 (C2 xx xx — what this game's empty methods compile to), rep ret (F3 C3)
        if (code[0] == 0xC3 || code[0] == 0xC2 || (code[0] == 0xF3 && code[1] == 0xC3)) return "empty native body shared with other methods";
        if ((code[0] == 0x31 || code[0] == 0x33) && code[1] == 0xC0 && code[2] == 0xC3) return "trivial native body shared with other methods";
        if (code[0] == 0xB0 && code[2] == 0xC3) return "trivial native body shared with other methods";
        return null;
    }

    public static void Stop()
    {
        if (!Running) return;
        try { VoicePlayer.StopAll(); } catch { }
        try { EndAllChatter(StopReason); } catch { }
        try { EndAllFollowers("the mod is stopping"); } catch { }
        try { EndJealousWalk(); } catch { }
        try { LeaveHome("the mod is stopping"); } catch { }
        try { StopPlugins(); } catch { }
        try { EndAllTagalongs(); } catch { }
        try { EndAllCompany("the mod is stopping"); } catch { }
        try { CloseWorldLine(); EndWorldMoment("the mod is stopping", quiet: true); WorldLine.Destroy(); } catch { }
        try { EndFishing("the mod is stopping"); UnhookCatches(); } catch { }
        try { StopVoiceInput(); } catch { }
        try { Dev.CapabilityProbe.StopAll("the mod is stopping"); } catch { }
        try { Game.CameraView.Stop(); } catch { } // FB-80: the phone Camera's live view // the probes' delegates on the game's events go first, and what they changed is put back
        try { CloseConsole(); } catch { }
        try { CloseChat(null); } catch (Exception e) { Log.Warn("Close on stop: " + e.Message); }
        try { NpcHold.ReleaseAll(); } catch { }
        // A hot reload starts a new copy beside this one, on the same files: this copy finishes what it can, and then writes
        // nothing more (LEAK-2, RACE-9, audit step 7b). Memory indexing just stops (the new copy indexes again).
        try { _memory?.Stop(); } catch (Exception e) { Log.Warn("Stopping memory indexing: " + e.Message); }
        try { StopTranscripts(); } catch (Exception e) { Log.Warn("Closing the transcript: " + e.Message); }
        Settle();
        // Books written on the pool (pairs, visits): what's waiting is written now, and then nothing more.
        try { _pairs?.Close(); } catch (Exception e) { Log.Warn("Saving pairs on stop: " + e.Message); }
        try { _circles?.Close(); } catch (Exception e) { Log.Warn("Saving groups on stop: " + e.Message); }
        try { GameContext.Visits?.Close(); } catch (Exception e) { Log.Warn("Saving visits on stop: " + e.Message); }
        StoryDialogue.Book = null;
        try { if (_ringRoot != null && !_ringRoot.WasCollected) UnityEngine.Object.Destroy(_ringRoot); } catch { }
        _ringRoot = null;
        try { if (_ringMaterial != null) UnityEngine.Object.Destroy(_ringMaterial); } catch { }
        _ringMaterial = null;
        _ringShaderTried = false;
        try { _story?.Close(); } catch (Exception e) { Log.Warn("Saving story conversations on stop: " + e.Message); }
        try { _reminders?.Close(); } catch (Exception e) { Log.Warn("Saving reminders on stop: " + e.Message); } // review F8
        try { _couples?.Close(); } catch (Exception e) { Log.Warn("Saving who's together on stop: " + e.Message); }
        CloseMindBooks(); // the inner-life round: everyone's own walk back too
        try { ClosePhone(); } catch { }
        // The full audit (B-3): a hold the settings menu took isn't left behind with no one to release it.
        _menuHolds = _menuOpen = _menuOverWindow = false;
        try { Locks.Release(); } catch (Exception e) { Log.Warn("Releasing player locks on stop: " + e.Message); }
        ClosePhoneBook(); // the phone
        try { _store?.Close(); _mockBrain?.Store.Close(); } catch (Exception e) { Log.Warn("Closing the people's files: " + e.Message); }
        // The other books written whole (gossip and news, usage, voices): a late change would write over the new copy's.
        // The under-18 list is never closed: it merges with the file when it saves, so an addition is never lost.
        try { _world?.Close(); _mockBrain?.World?.Close(); _budget?.Close(); _voiceBudget?.Close(); _speechBudget?.Close(); _voiceLedger?.Close(); _usagePlan?.Flush(); _voices?.Book.Close(); }
        catch (Exception e) { Log.Warn("Closing the mod's books: " + e.Message); }
        try { _harmony?.UnpatchSelf(); } catch (Exception e) { Log.Warn("Unpatch: " + e.Message); }
        try { Ui.Destroy(); } catch { }
        try { PhoneUi.Destroy(); Sprites.FreePhone(); } catch { } // the full audit (B-4)
        _harmony = null;
        Running = false;
    }

    /// <summary>
    /// Waits, 3 seconds at most in all, for this copy's work to finish, in order: the open request, every chat still being
    /// saved, then everyone's queued and after-meeting work (a diary), the dev mock's too. On the main thread: Stop only
    /// runs on a hot reload or a failed window build, never at quit (BepInEx doesn't call Unload). What's still running
    /// after that is dropped: the files are closed to it.
    /// </summary>
    private static void Settle()
    {
        var inflight = _inflight;
        var minorScan = _minorScan;
        Task[] closing;
        lock (_closing) closing = _closing.ToArray();
        var brains = new[] { _brain, _mockBrain }.Where(b => b != null).Select(b => b!).ToList();
        var all = Task.Run(async () =>
        {
            if (minorScan != null) { try { await minorScan.ConfigureAwait(false); } catch { } } // the under-18 list first
            if (inflight != null) { try { await inflight.ConfigureAwait(false); } catch { } }
            try { await Task.WhenAll(closing).ConfigureAwait(false); } catch { }
            foreach (var b in brains) { try { await b.SettledAll().ConfigureAwait(false); } catch { } }
        });
        bool done;
        try { done = all.Wait(TimeSpan.FromSeconds(3)); } catch { done = true; }
        if (!done) Log.Warn("Stopping: this copy was still saving after 3 seconds (a diary being written, say). What it finishes now isn't saved; the new copy starts anyway.");
    }

    /// <summary>The AI settings for one request, taken together (never half before and half after a reload: RACE-8).</summary>
    private static LlmSettings LlmSettingsNow()
    {
        lock (_cfg.Sync)
        {
            return new LlmSettings
            {
                Format = _cfg.Format.Value,
                Url = _cfg.ApiUrl.Value,
                ApiKey = _cfg.ApiKey.Value,
                Model = _cfg.Model.Value,
                BackgroundModel = _cfg.BackgroundModel.Value,
                BackgroundUrl = _cfg.BackgroundApiUrl.Value ?? "",
                BackgroundApiKey = _cfg.BackgroundApiKey.Value ?? "",
                BackgroundFormat = LlmSettings.FormatOf(_cfg.BackgroundApiFormat.Value),
                Effort = _cfg.Effort.Value,
                ReasoningEffort = _cfg.ReasoningEffort.Value ?? "",
                // Held in range (ROBUST-6): "never time out" written as a huge number, or a temperature that isn't a number.
                Temperature = float.IsFinite(_cfg.Temperature.Value) ? Math.Min(_cfg.Temperature.Value, 2f) : -1f,
                TimeoutSeconds = Math.Clamp(_cfg.TimeoutSeconds.Value, 10, 3600),
                ExtraHeaders = _cfg.ExtraHeaders.Value,
                RefusalFallback = _cfg.RefusalFallback.Value,
                PromptCaching = _cfg.PromptCaching.Value,
                LogPrompts = _cfg.LogPrompts.Value,
            };
        }
    }

    /// <summary>Where on the relationship bar people start doing things, from the settings (each 0–100).</summary>
    internal static Milestones MilestonesNow() => new()
    {
        WaveAt = Amount(_cfg.WaveAt.Value, 0, 100, 60),
        WaveOverAt = Amount(_cfg.WaveOverAt.Value, 0, 100, 70),
        FriendAt = Amount(_cfg.FriendAt.Value, 0, 100, 70),
        SnubAt = Amount(_cfg.SnubAt.Value, 0, 100, 37.5),
    };

    /// <summary>
    /// A fractional setting as the mod uses it: rounded to 3 decimals, so 37.6 is 37.6 and not 37.5999985. It's held to
    /// its range, and becomes its default when it isn't a number ("NaN": review of 919404e).
    /// </summary>
    internal static double Amount(float value, double min, double max, double fallback) =>
        float.IsFinite(value) ? Math.Clamp(Math.Round((double)value, 3), min, max) : fallback;

    private static BrainSettings BrainSettingsNow() => new()
    {
        OpeningLine = _cfg.OpeningLine.Value ?? "",
        AnswerLimitSeconds = AnswerLimitSeconds(),
        LifeUpdates = _cfg.LifeUpdates.Value,
        RecallFades = _cfg.RecallFades.Value,
        LifeUpdateAfterGameDays = _cfg.LifeUpdateAfterGameDays.Value,
        LifeUpdateAfterRealHours = _cfg.LifeUpdateAfterRealHours.Value,
        RecentMessages = Math.Clamp(_cfg.RecentMessagesSent.Value, 6, 400),
        SummarizeAfter = Math.Clamp(_cfg.SummarizeAfterMessages.Value, 20, 2000),
        MaxReplyTokens = Math.Clamp(_cfg.MaxReplyTokens.Value, 256, 64000),
        MaxGenerationTokens = Math.Clamp(_cfg.MaxGenerationTokens.Value, 1024, 64000),
        KeepGameNames = _cfg.KeepGameAssignedNames.Value,
        StructuredReplies = _cfg.StructuredReplies.Value,
        NpcsCanLeave = _cfg.NpcsCanEndConversations.Value,
        PatienceScale = _cfg.Patience.Value,
        MaxStoredLines = Math.Max(0, _cfg.HistoryLimit.Value),
        MeetingSummaries = _cfg.MeetingSummaries.Value,
        Gossip = _cfg.Gossip.Value,
        Friendliness = Math.Clamp(_cfg.Friendliness.Value, 0, 100) / 100.0,
        Favours = _cfg.Favours.Value,
        RelationshipGainMax = Amount(_cfg.RelationshipGainMax.Value, 0, 5, 0.5),
        RelationshipLossMin = Amount(_cfg.RelationshipLossMin.Value, 0, 5, 0.1),
        RelationshipLossMax = Amount(_cfg.RelationshipLossMax.Value, 0, 5, 0.5),
        FoodGiftBonus = Amount(_cfg.FoodGiftBonus.Value, 0, 100, 1.5),
        FoodFavourBonus = Amount(_cfg.FoodFavourBonus.Value, 0, 100, 1),
        Milestones = MilestonesNow(),
        VaryLines = VaryNow, // the inner-life round: off while the pacer holds talk back (ModRuntime.Mind.cs)
        Population = new PopulationSettings
        {
            MinAge = _cfg.MinAge.Value,
            MaxAge = _cfg.MaxAge.Value,
            TransPercent = _cfg.TransChance.Value,
            NonBinaryPercent = _cfg.NonBinaryChance.Value,
            QueerPercent = _cfg.QueerChance.Value,
            AgeShapesIdentity = _cfg.AgeShapesIdentity.Value,
        },
    };

    /// <summary>How speech bubbles look, from the Speech Bubbles settings (read each time one is shown, so edits apply straight away).</summary>
    private static BubbleLook BubbleLookNow()
    {
        var d = new BubbleLook();
        return new BubbleLook
        {
            Fill = BubbleLook.Parse(_cfg.BubbleBackground.Value, d.Fill, _cfg.BubbleBackgroundOpacity.Value),
            Edge = BubbleLook.Parse(_cfg.BubbleBorder.Value, d.Edge, _cfg.BubbleBorderOpacity.Value),
            Text = BubbleLook.Parse(_cfg.BubbleText.Value, d.Text),
            NameHex = BubbleLook.Hex(_cfg.BubbleName.Value, d.NameHex),
            Size = Math.Clamp(_cfg.BubbleTextSize.Value, 50, 200) / 100f,
        };
    }

    /// <summary>The voice settings for one line, taken together (never half before and half after a reload: RACE-8).</summary>
    private static VoiceSettings VoiceSettingsNow()
    {
        lock (_cfg.Sync)
        {
            var s = new VoiceSettings
            {
                Enabled = _cfg.VoicesEnabled.Value,
                MatchGender = _cfg.MatchVoiceToGender.Value,
                Tones = _cfg.VoiceTones.Value,
                // The voice's own deadline (VoiceTimeoutSeconds, not the AI's): the service sets a slow provider aside when it
                // runs out, and VoiceForAsync's give-up is only a backstop a little later.
                TimeoutSeconds = (int)Math.Ceiling(VoiceTimeout),
            };
            // Only when both ends are exactly OpenAI's own endpoint (a look-alike host never gets a borrowed key).
            bool chatOnOpenAi = _cfg.Format.Value == ApiFormat.OpenAI && LlmClient.IsHost(LlmClient.ResolveUrl(ApiFormat.OpenAI, _cfg.ApiUrl.Value), "api.openai.com");
            foreach (var p in _cfg.VoiceSlots)
            {
                string key = p.ApiKey.Trim();
                // OpenAI voices with no key of their own borrow the chat's OpenAI key (or OPENAI_API_KEY).
                if (key.Length == 0 && p.Provider == VoiceProvider.OpenAI && LlmClient.IsHost(p.Url, "api.openai.com"))
                    key = chatOnOpenAi && _cfg.ApiKey.Value.Trim().Length > 0 ? _cfg.ApiKey.Value.Trim() : (Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "").Trim();
                s.Slots.Add(new VoiceSlot
                {
                    Number = p.Number, Provider = p.Provider, Chance = p.Chance, ApiKey = key, Url = p.Url, Model = p.Model, Voices = p.Voices,
                });
            }
            return s;
        }
    }

    /// <summary>Last error each guarded part of the frame logged, and when: said again only if it changes or after 30 s.</summary>
    private static readonly Dictionary<string, (string message, float at)> FrameErrors = new();

    /// <summary>A part of the frame that may fail on its own: its failure is logged (not every frame), the rest goes on.</summary>
    private static void Guarded(string what, Action run)
    {
        try { run(); }
        catch (Exception e)
        {
            float now = Time.unscaledTime;
            if (FrameErrors.TryGetValue(what, out var last) && last.message == e.Message && now - last.at < 30f) return;
            FrameErrors[what] = (e.Message, now);
            Log.Warn($"{char.ToUpperInvariant(what[0])}{what[1..]} failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Whether a key was pressed this frame. A key name the game's keyboard doesn't have (the setting parses, the keyboard
    /// refuses it) is said once and the default is used from then on (findings #27).
    /// </summary>
    private static bool Pressed(Keyboard kb, ref Key key, Key fallback, string setting)
    {
        try { return kb[key].wasPressedThisFrame; }
        catch (Exception)
        {
            Log.Warn($"{setting} = {key} isn't a key the game's keyboard has; using {fallback} instead.");
            key = fallback;
            return false;
        }
    }

    /// <summary>The voice list's problems as last warned (findings #26).</summary>
    private static string _voiceProblemsSaid = "";

    private static void WarnVoiceProblems()
    {
        string now = string.Join("|", _cfg.VoiceProblems);
        if (now == _voiceProblemsSaid) return;
        _voiceProblemsSaid = now;
        foreach (var problem in _cfg.VoiceProblems) Log.Warn(problem);
    }

    private static string _talkKeySaid = "";

    private static void ParseKey()
    {
        string k = (_cfg.TalkKey.Value ?? "T").Trim();
        if (k.Length == 1 && char.IsDigit(k[0])) k = "Digit" + k;
        bool ok = Enum.TryParse<Key>(k, true, out var parsed) && parsed != Key.None && !int.TryParse(k, out _) && Enum.IsDefined(parsed);
        _talkKey = ok ? parsed : Key.T;
        // A value it can't use (a controller button saved by another menu: "JoystickButton2") said once, not silently T.
        if (!ok && k.Length > 0 && _talkKeySaid != k)
        {
            _talkKeySaid = k;
            string shown = Ai.Redact.PlainlyKey(k) ? "(not shown)" : $"'{Ai.Redact.Value(k)}'";
            Log.Warn($"TalkKey {shown} isn't a keyboard key People of Nivalis can use, so T is used. It takes keyboard keys (T, G, F2...); " +
                     "talking with a controller is PadTalkButton (the game's interact button by default, or X (Square), Y, B, LB, RB, RT, LT).");
        }
    }

    private static TokenBudget? _budget;
    private static VoyageClient? _voyage;
    private static MemoryIndexer? _memory;
    private static MinorList? _minors;
    /// <summary>The start-up check of saved people for anyone under 18 (resets wait for it).</summary>
    private static Task _minorScan = Task.CompletedTask;

    /// <summary>
    /// Someone was just found to be under 18 (any thread): the cache that greetings, chatter and venue talk read is told
    /// on the main thread, and what others kept about them goes: their pairs, gossip they started, their memory index.
    /// Other people's own memories of them stay (matching them by name isn't safe).
    /// </summary>
    private static void OnMinorAdded(string id)
    {
        MainThread.Enqueue(() => MinorCache.Remove(id));
        Task.Run(() => ForgetUnder18(id));
    }

    /// <summary>What others kept about someone under 18 goes: their pairs, gossip they started, their memory index. Safe to repeat.</summary>
    private static void ForgetUnder18(string id)
    {
        string backup = Path.Combine(_dataDir, "backups", "under-18");
        try { _pairs?.Forget(id); } catch (Exception e) { Log.Debug("under 18, pairs: " + e.Message); }
        try { _circles?.Forget(id); } catch (Exception e) { Log.Debug("under 18, groups: " + e.Message); }
        try { _world?.ForgetSource(id); _mockBrain?.World?.ForgetSource(id); } catch (Exception e) { Log.Debug("under 18, gossip: " + e.Message); }
        try { _memory?.Store.Archive(id, backup); } catch (Exception e) { Log.Debug("under 18, memory index: " + e.Message); }
        try { _reminders?.Forget(id); } catch (Exception e) { Log.Debug("under 18, reminders: " + e.Message); } // review F8
        try { _couples?.Forget(id); } catch (Exception e) { Log.Debug("under 18, couples: " + e.Message); }
        // The inner-life round: theirs, and anything in anyone else's mind that names them (review core IL-4).
        try
        {
            string name = "";
            try { name = _brain?.Store.Glance(id)?.Persona.Name ?? ""; } catch { }
            _minds?.Forget(id, name);
            _witness?.Forget(id, name);
        }
        catch (Exception e) { Log.Debug("under 18, inner life: " + e.Message); }
        try { if (_phone != null) foreach (var f in _phone.Forget(id)) DeleteVoiceFile(f); } catch (Exception e) { Log.Debug("under 18, phone: " + e.Message); } // the phone: their number, texts and voice notes
        ForgetOnNightline(id); // FB-71: their lines, photos and picture
    }

    /// <summary>Memories are searched by meaning (MemorySearch = Voyage, with a key).</summary>
    private static bool VoyageOn =>
        string.Equals(_cfg.MemorySearch.Value.Trim(), "Voyage", StringComparison.OrdinalIgnoreCase) && _cfg.VoyageKey.Value.Trim().Length > 0;

    /// <summary>
    /// Memory search switched (at start, or the setting changed): to Voyage, everyone's existing memories are indexed in
    /// the background (only what isn't yet); back to Local, nothing needs bringing back, since memories never leave your
    /// files (the index is kept for next time). On start with Voyage on, anything new since last time is indexed.
    /// </summary>
    private static void CheckMemoryMode(bool startup)
    {
        try
        {
            string now = VoyageOn ? "Voyage" : "Local";
            string file = Path.Combine(_dataDir, "memory", "mode.txt");
            string before = File.Exists(file) ? File.ReadAllText(file).Trim() : "Local";
            if (before != now)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, now);
                Log.Info(now == "Voyage"
                    ? "Memory search is now by meaning (Voyage): indexing everyone's memories in the background."
                    : "Memory search is back to matching words (Local). Every memory is still in your files; the Voyage index is kept for next time.");
            }
            if (now == "Voyage" && (startup || before != now)) _memory?.QueueAll(_store.Ids());
            else if (string.Equals(_cfg.MemorySearch.Value.Trim(), "Voyage", StringComparison.OrdinalIgnoreCase) && _cfg.VoyageKey.Value.Trim().Length == 0)
                // Voyage is the default but needs a key: without one it's matching words, quietly (not a problem to report).
                Log.Info("Memory search: by matching words (MemorySearch = Voyage is used once you set VoyageKey).");
        }
        catch (Exception e) { Log.Debug("memory mode: " + e.Message); }
    }
    private static float _nextReloadCheck, _nextUnsavedTry;
    /// <summary>Since when auto-reload has found the settings file held by another program (-1: it isn't).</summary>
    private static float _settingsBusySince = -1;
    /// <summary>The held-file notice was said for this spell (the Unsaved retry keeps trying to read it meanwhile).</summary>
    private static bool _saidBusy;
    private static bool _rebuildUi;

    /// <summary>
    /// Reads the settings file again (/reload, or by itself when it's saved outside the game): everything that's read as
    /// it's used (AI, voices, chatter, limits) changes at once; the window is rebuilt to a new size when it's next closed.
    /// A file that couldn't be read is said so ([CFG-READ], with the line), and nothing from it applies (#12). One held by
    /// another program is read again at each check (<paramref name="auto"/>), and said only if it's still held 10 s on.
    /// </summary>
    private static List<string> ReloadSettings(out string? error, bool auto = false)
    {
        var changed = _cfg.Reload(out error);
        AfterReload(changed, error, auto);
        return changed;
    }

    /// <summary>
    /// What follows a reload, whoever started it (/reload, auto-reload, or an in-game change that read the file first):
    /// the problem said, and the settings read as they're used brought up to date.
    /// </summary>
    /// <summary>FB-45: [Features] as the code reads it (every gate checks <see cref="Npc.Features"/>).</summary>
    private static void SyncFeatures()
    {
        if (_cfg == null) return;
        Npc.Features.Moods = _cfg.FeatureMoods.Value;
        Npc.Features.Relationships = _cfg.FeatureRelationships.Value;
        Npc.Features.PhoneCalls = _cfg.FeaturePhoneCalls.Value;
        Npc.Features.Muttering = _cfg.FeatureMuttering.Value;
        Npc.Features.TableConversations = _cfg.FeatureTables.Value;
        Npc.Features.Overhearing = _cfg.FeatureOverhearing.Value;
        Npc.Features.StoryMemory = _cfg.FeatureStoryMemory.Value;
        Npc.Features.StoryVoice = _cfg.FeatureStoryVoice.Value;
        Npc.Features.WorkLife = _cfg.FeatureWorkLife.Value;
        Npc.Features.Following = _cfg.FeatureFollowing.Value;
        Npc.Features.Romance = _cfg.FeatureRomance.Value;
        Npc.Features.Reminders = _cfg.FeatureReminders.Value;
        Npc.Features.GroupChat = _cfg.FeatureGroupChat.Value;
        Npc.Features.VoiceInput = _cfg.FeatureVoiceInput.Value;
        Npc.Features.InnerLife = _cfg.FeatureInnerLife.Value; // the inner-life round
        Npc.Features.Roleplay = _cfg.FeatureRoleplay.Value; // what they do as they speak, under their line
        Npc.Features.Phone = _cfg.FeaturePhone.Value; // the phone: numbers, texts
        Npc.Features.Nightline = _cfg.FeatureNightline.Value; // FB-71: the city's social app
        Npc.Features.WorldChat = _cfg.FeatureWorldChat.Value; // FB-76
        Prompts.FishTogether = _cfg.FishTogether.Value && _cfg.FeatureFollowing.Value; // FB-73
        Ai.Prompts.Language = _cfg.ReplyLanguage.Value ?? ""; // "characters reply in" (from RiftSpeak's list)
        Ai.Prompts.PlayerGender = _cfg.PlayerGender.Value ?? ""; // CF-17: diaries, memories, residents' talk know how to refer to you
    }

    private static void AfterReload(List<string> changed, string? error, bool auto)
    {
        SyncFeatures();
        if (auto && _cfg.ReloadBusy)
        {
            if (_settingsBusySince < 0) _settingsBusySince = Time.unscaledTime;
            if (Time.unscaledTime - _settingsBusySince < 10f || _saidBusy) return;
            _saidBusy = true;
            // Said once. Auto-reload doesn't try again until it changes; the mod still reads it before it writes anything
            // (an in-game change waiting is written only on top of what the player saved: review of a0fa75f).
            _cfg.MarkSeen();
            error = "another program kept it open, so it wasn't read; save it again, or type /reload once it's free";
        }
        else _saidBusy = false;
        _settingsBusySince = -1;
        if (error != null) Problem($"[CFG-READ] The settings file couldn't be read after it changed ({error}). Nothing from that save has been applied.");
        WarnVoiceProblems(); // only when they changed (findings #26: not again on every reload)
        if (changed.Contains("MemorySearch") || changed.Contains("VoyageKey") || changed.Contains("VoyageModel")) CheckMemoryMode(startup: false);
        else if (changed.Contains("MemoryIndexLimit") && VoyageOn) _memory?.QueueAll(_store.Ids()); // the new limit applies to everyone now
        if (changed.Contains("UiScale")) _rebuildUi = true; // once no window is open: built again at the new size
        // A shorter interval applies now, not after the old one runs out (FLOW-8), the way /chatter does it.
        float now = Time.unscaledTime;
        if (changed.Contains("DynamicEvery")) _nextDynamicLook = Math.Min(_nextDynamicLook, now + Math.Max(3, ChatterSettings.Interval(_cfg.DynamicEvery.Value) ?? 15));
        if (changed.Contains("GroupEvery")) _nextGroupRoll = Math.Min(_nextGroupRoll, now + Math.Max(5, ChatterSettings.Interval(_cfg.GroupEvery.Value) ?? 30));
        if (changed.Contains("PhoneEvery")) _nextPhoneRoll = Math.Min(_nextPhoneRoll, now + Math.Max(5, ChatterSettings.Interval(_cfg.PhoneEvery.Value) ?? 60));
        if (changed.Contains("ThinkEvery")) _nextThinkRoll = Math.Min(_nextThinkRoll, now + Math.Max(5, ChatterSettings.Interval(_cfg.ThinkEvery.Value) ?? 45));
    }

    /// <summary>Room left under the token limit (always, with none set).</summary>
    private static bool AiAllowed() => _budget == null || _budget.Allows();

    internal static NpcBrain ActiveBrain => _useMock && _mockBrain != null ? _mockBrain : _brain;

    internal static void SetMock(bool on)
    {
        if (on && _mockBrain == null)
        {
            var llm = new LlmClient(LlmSettingsNow, s => Log.Info(s), new System.Net.Http.HttpMessageInvoker(new MockEndpoint()));
            _mockBrain = new NpcBrain(llm, new NpcStore(Path.Combine(_dataDir, "npcs-mock"), Log.Warn), BrainSettingsNow, s => Log.Info("[mock] " + s), _minors);
            try { _mockBrain.World = new WorldMemory(Path.Combine(_dataDir, "world-mock.json"), Log.Warn); } catch (Exception e) { Log.Warn("Mock gossip unavailable: " + e.Message); }
        }
        // Names and under-18 answers read from one store don't hold for the other (6a review: mock names on real people).
        if (_useMock != on) { KnownNames.Clear(); MinorCache.Clear(); }
        _useMock = on;
    }

    /// <summary>Run something on the main thread after a short delay (real seconds).</summary>
    internal static void Later(float seconds, Action run) => Delayed.Add((Time.unscaledTime + seconds, run));

    /// <summary>FB-50: the story conversations kept (null: unavailable).</summary>
    private static StoryBook? _story;

    /// <summary>FB-50: a story character's speech sheet, written in the background once enough of their story is kept (within the token limit).</summary>
    internal static void StorySheetSoon(string id, string name)
    {
        if (!Npc.Features.StoryVoice || _story == null || !_story.NeedsSheet(id) || !AiAllowed()) return;
        var brain = ActiveBrain;
        Task.Run(async () =>
        {
            try { await brain.MaybeWriteStorySheetAsync(id, name, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception e) { Log.Debug("story sheet: " + e.Message); }
        });
    }

    // ------------------------------------------------------------------ frame pump

    /// <summary>The game's main thread (problems raised on it show at once, in order; others are queued to it).</summary>
    private static int _mainThreadId = -1;

    public static void Update()
    {
        _mainThreadId = Environment.CurrentManagedThreadId;
        if (!Running) return;
        try { Pad.Tick(); } catch (Exception e) { Log.Debug("controller: " + e.Message); } // once, for every window this frame (CF-07)
        while (MainThread.TryDequeue(out var a))
        {
            try { a(); } catch (Exception e) { Log.Error("Main-thread task failed: " + e); }
        }
        for (int i = Delayed.Count - 1; i >= 0; i--)
        {
            if (Time.unscaledTime < Delayed[i].at) continue;
            var run = Delayed[i].run;
            Delayed.RemoveAt(i);
            try { run(); } catch (Exception e) { Log.Debug("delayed: " + e.Message); }
        }
        try { _dev?.Tick(); } catch (Exception e) { Log.Error("Dev commands: " + e.Message); }
        try { SyncTranscripts(); } catch (Exception e) { Log.Debug("transcripts: " + e.Message); }

        var pc = GameContext.PlayerCharacter();
        if (pc == null)
        {
            // The settings menu up while you left the world (audit 2026-10-14): the chat it hid stops being hidden (the close
            // below then hides it properly), and its own hold on you goes, or the next chat couldn't hold you at all.
            if (_menuOverWindow) { _menuOverWindow = false; try { Ui.Suspend(false); } catch { } }
            if (_menuHolds)
            {
                _menuHolds = false;
                try { Locks.Release(); } catch (Exception e) { Log.Warn("Releasing player locks failed: " + e.Message); }
            }
            if (ChatOpen) CloseChat(null);
            CloseConsole();
            try { ClosePhone(); } catch { } // the full audit (B-1): the phone out as you left, its hold released too
            try { CloseWorldLine(); EndWorldMoment("you left the game world", quiet: true); } catch { } // review WC-2
            if (Groups.Count + Calls.Count + Thoughts.Count + VenueTalks.Count > 0) EndAllChatter("you left the game world"); // every kind (#13a)
            Ui.HideBark(); // no bubbles left hanging over the menu
            return;
        }
        if (_rebuildUi && Ui.Built && !ChatOpen && !_consoleOpen && !_menuOpen)
        {
            _rebuildUi = false;
            try { Ui.Destroy(); } catch { }
        }
        if (!Ui.Built)
        {
            Ui.BubbleStyle = BubbleLookNow;
            // CF-24: each subtitle into /heard, with the game's time it was said.
            Ui.Heard = (who, line, key, at, carries) =>
            {
                // Review CF24-2: only what you could hear (a residents' line carries as far as its bubble shows).
                if (carries > 0f && at != null && GameContext.PlayerCharacter() is { } pc && Flat(at.position - pc.transform.position).magnitude > carries) return;
                HeardLines.Add(TwelveHour($"{Math.Max(0, GameContext.Hour()):00}:{Math.Max(0, GameContext.Minute()):00}"), who, line, key);
            };
            Ui.FocusFadeOn = () => _cfg.BubbleFocus.Value;
            Ui.CogShown = () => MenuInstalled; // the settings menu is a separate mod (FB-40)
            try
            {
                Ui.Build(_cfg.UiScale.Value);
                Ui.Layout = _cfg.ChatWindowLayout.Value; // where you put it, and its size
                Ui.LayoutChanged = v => { if (_cfg.ChatWindowLayout.Value != v) _cfg.ChatWindowLayout.Value = v; };
            }
            catch (Exception e) { Log.Error("Chat window could not be built: " + e); Stop(); return; } // everyone held is let go, the hooks come off (#13c)
        }
        Guarded("the chat window", Ui.Tick);
        Guarded("the phone window", TickPhoneWindow); // the phone (and its cards while it's away)
        Guarded("play time", TickPlayTime);
        TickVisits();
        TickDoings();
        TickWorkLife();
        if (_cfg.AutoReloadSettings.Value && Time.unscaledTime >= _nextReloadCheck)
        {
            _nextReloadCheck = Time.unscaledTime + 2f;
            // Never out of the frame: a file that keeps failing is marked seen and said once ([CFG-READ]), not every 2 s (#12).
            try
            {
                if (_cfg.ChangedOnDisk())
                {
                    var changed = ReloadSettings(out _, auto: true);
                    if (changed.Count > 0)
                    {
                        string note = "Settings file saved outside the game; now in effect: " + string.Join(", ", changed) + ".";
                        Log.Info(note);
                        if (_consoleOpen) Ui.AddLine(LineKind.Output, "", note);
                    }
                }
            }
            catch (Exception e) { _cfg.MarkSeen(); Log.Warn("Reading the settings file again failed: " + e.Message); }
        }
        // An in-game change the file couldn't take (another program had it): tried again every couple of seconds. Only its
        // own line is written, so nothing the player saved meanwhile is lost.
        if (_cfg.Unsaved && Time.unscaledTime >= _nextUnsavedTry)
        {
            _nextUnsavedTry = Time.unscaledTime + 2f;
            try { _cfg.Write(); } catch (Exception e) { Log.Debug("settings write: " + e.Message); }
        }
        try { TickChatter(); } catch (Exception e) { Log.Debug("chatter tick: " + e.Message); }
        try { TickSaves(); } catch (Exception e) { Log.Warn("Saves: " + e.Message); } // FB-61: a loaded save's memory put in place
        try { TickReminders(); } catch (Exception e) { Log.Debug("reminders: " + e.Message); } // FB-65
        try { TickCustomerNames(); } catch (Exception e) { Log.Debug("customer names: " + e.Message); } // CF-19
        try { TickAvaBackfill(); } catch (Exception e) { Log.Debug("ava backfill: " + e.Message); } // FB-75
        try { TickCompany(); } catch (Exception e) { Log.Debug("company: " + e.Message); } // FB-72
        try { TickWorldMoment(); } catch (Exception e) { Log.Debug("world chat: " + e.Message); } // FB-76
        try { TickFishing(); } catch (Exception e) { Log.Debug("fishing: " + e.Message); } // FB-73
        try { TickFollowers(); } catch (Exception e) { Log.Debug("followers: " + e.Message); } // FB-62
        try { TickRomance(); } catch (Exception e) { Log.Debug("romance: " + e.Message); } // FB-63
        try { TickJealousy(); } catch (Exception e) { Log.Debug("jealousy: " + e.Message); } // FB-63
        try { TickHome(); } catch (Exception e) { Log.Debug("home: " + e.Message); } // FB-63: a partner at home
        try { TickSpeakUp(); } catch (Exception e) { Log.Debug("speak up: " + e.Message); } // the people with you speak up on events
        try { TickTagalongs(); } catch (Exception e) { Log.Debug("going together: " + e.Message); } // FB-62: residents going together
        try { TickMind(); } catch (Exception e) { Log.Debug("inner life: " + e.Message); } // the inner-life round: what's on their minds, the game's needs
        try { TickBody(); } catch (Exception e) { Log.Debug("body language: " + e.Message); } // the inner-life round: walks and reactions

        // Each guarded on its own (findings #27): one that keeps failing can't take the frame (and the rest of the mod) with it.
        // One window takes input at a time: the settings menu (another mod; over a chat or the console it hides), the chat,
        // the console. The press that closed the menu doesn't act here.
        if (_menuOpen) { if (Locks.Engaged) Locks.KeepCursor(); return; }
        if (Time.frameCount == _menuClosedFrame) return;
        if (ChatOpen) { Guarded("the chat", UpdateChat); Guarded("the phone key in a chat", PhoneKeyInChat); return; }
        if (_consoleOpen) { Guarded("the console", UpdateConsole); return; }
        if (_phoneOpen) { Guarded("the phone", UpdatePhone); return; } // the phone: no chat, no console while it's out
        if (_worldOpen) { Guarded("world chat", UpdateWorldLine); return; } // FB-76: everything stops while you type
        bool openConsole = false;
        Guarded("the console key", () => openConsole = InGameplay() && (ConsoleKeyPressed() || PadConsolePressed()));
        if (openConsole) { Guarded("opening the console", OpenConsole); return; }
        bool openPhone = false;
        Guarded("the phone key", () => openPhone = InGameplay() && PhoneKeyPressed());
        if (openPhone) { Guarded("opening the phone", OpenPhone); return; }
        bool openWorld = false;
        Guarded("the world chat key", () => openWorld = InGameplay() && !_resetting && WorldKeyPressed());
        if (openWorld) { Guarded("opening world chat", OpenWorldLine); return; }
        Guarded("looking at people", UpdateTargeting);
        if (InGameplay() && !_resetting)
        {
            try { Greetings.Tick(_cfg, ActiveBrain.Store, OnGreeting, (c, id) => TalkingNow(c, id)); }
            catch (Exception e) { Log.Debug("greetings: " + e.Message); }
            try { TickMinors(); }
            catch (Exception e) { Log.Debug("children: " + e.Message); }
        }
    }

    public static void LateUpdate()
    {
        if (!Running) return;
        try { NpcHold.LateTick(); } catch (Exception e) { Log.Debug("hold tick: " + e.Message); }
        try { VoicePlayer.Tick(); } catch (Exception e) { Log.Debug("voice fades: " + e.Message); }
        try { TickSight(); } catch (Exception e) { Log.Debug("sight lines: " + e.Message); }
        // Bubbles last: everyone is where they'll be drawn this frame (held people put back, heads checked against bodies).
        try { if (Ui.Built) Ui.TickBark(null); } catch (Exception e) { Log.Debug("bubbles: " + e.Message); }
        try { if (Ui.Built) Ui.TickToast(); } catch (Exception e) { Log.Debug("toast: " + e.Message); } // FB-65
    }

    /// <summary>FB-29 /sight on: draw nearby people's sight triangles (never saved; off again with /sight off).</summary>
    private static bool _sightOn, _sightShown;
    /// <summary>FB-38 /rings on: your three rings drawn at your feet (with the same lines as /sight).</summary>
    private static bool _ringsOn;

    private static void TickSight()
    {
        if ((!_sightOn && !_ringsOn && _ringsPreview == null) || !Ui.Built)
        {
            if (_sightShown) { Ui.HideSight(); _sightShown = false; }
            HideRingsInWorld();
            return;
        }
        var cam = GameContext.PlayerCamera();
        var pc = GameContext.PlayerCharacter();
        // A loading screen, the main menu, a paused game or the game's menus: nothing left frozen on screen (FB-38 review).
        // The settings menu changing the rings draws them over a game it holds still (its cursor is up): only then.
        if (cam == null || pc == null || (!InGameplay() && !(_ringsPreview != null && _menuOpen && Time.timeScale > 0f)))
        {
            if (_sightShown) { Ui.HideSight(); _sightShown = false; }
            HideRingsInWorld();
            return;
        }
        float sd = Mathf.Clamp(float.IsFinite(_cfg.SightDistance.Value) ? _cfg.SightDistance.Value : 10f, 1f, 40f);
        float sa = Mathf.Clamp(float.IsFinite(_cfg.SightAngle.Value) ? _cfg.SightAngle.Value : 120f, 10f, Sight.MaxAngle);
        Vector3 me = pc.transform.position;
        var segments = OverlaySegments;
        segments.Clear();
        // FB-53: drawn in the world when they can be (what's in front simply covers them), else over the screen as before.
        if (_ringsOn || _ringsPreview != null) { if (!RingsInWorld(me)) AddRings(segments, me, cam.transform.position, pc.transform); }
        else HideRingsInWorld();
        if (_sightOn && _ringsPreview == null)
        foreach (var (c, _, pos) in NearBodies(pc).Where(b => Flat(b.pos - me).magnitude <= 15f).OrderBy(b => Flat(b.pos - me).sqrMagnitude).Take(12))
        {
            try
            {
                var head = GameContext.HeadPoint(GameContext.HeadOf(c), c.transform);
                var face = c.transform.forward;
                var (l, r) = Sight.Corners((pos.x, pos.z), (face.x, face.z), sd, sa);
                bool sees = Sight.Sees((pos.x, pos.z), (face.x, face.z), (me.x, me.z), sd, sa);
                var colour = sees ? new Color(0.35f, 1f, 0.45f, 0.95f) : new Color(0.78f, 0.78f, 0.78f, 0.6f);
                var apex = new Vector3(pos.x, head.y, pos.z);
                var left = new Vector3(l.x, head.y, l.z);
                var right = new Vector3(r.x, head.y, r.z);
                segments.Add((apex, left, colour));
                segments.Add((apex, right, colour));
                segments.Add((left, right, colour));
            }
            catch { }
        }
        Ui.DrawSight(cam, segments);
        _sightShown = true;
    }

    /// <summary>This frame's overlay lines (kept, not made anew each frame).</summary>
    private static readonly List<(Vector3, Vector3, Color)> OverlaySegments = new();

    /// <summary>FB-38: the three rings as flat circles about 6 inches above your feet: hearing green, voices gold, chatter blue.</summary>
    private static void AddRings(List<(Vector3, Vector3, Color)> segments, Vector3 feet, Vector3 eye, Transform me)
    {
        var r = (_ringsPreview ?? Rings.From(_cfg.VoiceDistance.Value, _cfg.VoiceBeyond.Value, _cfg.ChatterRange.Value)).Clamped();
        float y = feet.y + RingHeight;
        void Circle(float radius, Color colour)
        {
            if (!(radius > 0.05f)) return;
            const int Parts = 64;
            var prev = new Vector3(feet.x + radius, y, feet.z);
            for (int i = 1; i <= Parts; i++)
            {
                float a = i * Mathf.PI * 2f / Parts;
                var p = new Vector3(feet.x + Mathf.Cos(a) * radius, y, feet.z + Mathf.Sin(a) * radius);
                if (!Behind(eye, (prev + p) * 0.5f, me)) segments.Add((prev, p, colour)); // FB-51: hidden behind what's in front of it
                prev = p;
            }
        }
        if (r.Hearing > 0f)
        {
            Circle(r.Hearing, new Color(0.35f, 1f, 0.45f, 0.85f));
            if (r.Voices > r.Hearing + 0.01f) Circle(r.Voices, new Color(0.95f, 0.72f, 0.3f, 0.85f));
        }
        Circle(r.Chatter, new Color(0.4f, 0.7f, 1f, 0.85f));
    }

    /// <summary>How far above your feet the rings are drawn: 5 cm (FB-53: they were 15 cm, the author wanted them 4 inches lower).</summary>
    private const float RingHeight = 0.05f;

    // FB-53: the rings as lines in the world, depth-tested by the game's own rendering (the screen overlay's look-through
    // test, one ray a piece, flickered: "it should behave simply as a rendering layer").
    private static GameObject? _ringRoot;
    private static readonly LineRenderer?[] RingLines = new LineRenderer?[3];
    private static Material? _ringMaterial;
    private static bool _ringShaderTried;
    private const int RingParts = 72;

    private static bool RingsInWorld(Vector3 feet)
    {
        if (!_ringShaderTried)
        {
            _ringShaderTried = true;
            try
            {
                var shader = Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Sprites/Default");
                if (shader != null)
                {
                    _ringMaterial = new Material(shader) { hideFlags = HideFlags.DontUnloadUnusedAsset };
                    try { _ringMaterial.SetInt("_ZTest", 4); _ringMaterial.SetInt("_ZWrite", 0); _ringMaterial.SetInt("_Cull", 0); _ringMaterial.SetInt("_SrcBlend", 5); _ringMaterial.SetInt("_DstBlend", 10); } catch { }
                    Log.Info($"Rings: drawn in the world ({shader.name}): hidden behind whatever is in front of them.");
                }
                else Log.Info("Rings: no shader to draw them in the world with; drawn over the screen instead.");
            }
            catch (Exception e) { Log.Info("Rings: drawn over the screen (" + e.Message + ")."); _ringMaterial = null; }
        }
        if (_ringMaterial == null) return false;
        try
        {
            if (_ringRoot == null || _ringRoot.WasCollected)
            {
                _ringRoot = new GameObject("NivalisAIChat.Rings");
                UnityEngine.Object.DontDestroyOnLoad(_ringRoot);
                for (int i = 0; i < RingLines.Length; i++)
                {
                    var go = new GameObject("Ring" + i);
                    go.transform.SetParent(_ringRoot.transform, false);
                    var lr = go.AddComponent<LineRenderer>();
                    lr.useWorldSpace = true;
                    lr.loop = true;
                    lr.positionCount = RingParts;
                    lr.startWidth = lr.endWidth = 0.035f;
                    lr.material = _ringMaterial;
                    lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    lr.receiveShadows = false;
                    RingLines[i] = lr;
                }
            }
            if (!_ringRoot.activeSelf) _ringRoot.SetActive(true);
            var r = (_ringsPreview ?? Rings.From(_cfg.VoiceDistance.Value, _cfg.VoiceBeyond.Value, _cfg.ChatterRange.Value)).Clamped();
            Ring(0, r.Hearing, new Color(0.35f, 1f, 0.45f, 0.85f), feet);
            Ring(1, r.Hearing > 0f && r.Voices > r.Hearing + 0.01f ? r.Voices : 0f, new Color(0.95f, 0.72f, 0.3f, 0.85f), feet);
            Ring(2, r.Chatter, new Color(0.4f, 0.7f, 1f, 0.85f), feet);
            return true;
        }
        catch (Exception e)
        {
            Log.Debug("rings in the world: " + e.Message);
            _ringMaterial = null; // over the screen from now on
            HideRingsInWorld();
            return false;
        }
    }

    // Where each ring was last drawn: its points are set again only when you or it moved (review of 0.4.4).
    private static readonly (Vector3 feet, float radius)[] RingDrawn = new (Vector3, float)[3];

    private static void Ring(int i, float radius, Color colour, Vector3 feet)
    {
        var lr = RingLines[i];
        if (lr == null) return;
        if (!(radius > 0.05f)) { lr.enabled = false; return; }
        if (!lr.enabled) { lr.enabled = true; RingDrawn[i] = default; }
        if ((RingDrawn[i].feet - feet).sqrMagnitude < 0.0004f && Mathf.Abs(RingDrawn[i].radius - radius) < 0.001f) return;
        RingDrawn[i] = (feet, radius);
        lr.startColor = lr.endColor = colour;
        float y = feet.y + RingHeight;
        for (int k = 0; k < RingParts; k++)
        {
            float a = k * Mathf.PI * 2f / RingParts;
            lr.SetPosition(k, new Vector3(feet.x + Mathf.Cos(a) * radius, y, feet.z + Mathf.Sin(a) * radius));
        }
    }

    private static void HideRingsInWorld()
    {
        try { if (_ringRoot != null && !_ringRoot.WasCollected && _ringRoot.activeSelf) _ringRoot.SetActive(false); } catch { }
    }

    // FB-51: one hit buffer for the rings' look-through test (no allocation each frame).
    private static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<RaycastHit> RingHits = new(8);

    /// <summary>
    /// Something stands between the camera and this point of a ring (a wall, a counter, a person): that piece isn't drawn,
    /// so the rings read as lying on the ground, behind what's in front of them. Your own body never hides them.
    /// </summary>
    private static bool Behind(Vector3 eye, Vector3 point, Transform me)
    {
        try
        {
            Vector3 dir = point - eye;
            float dist = dir.magnitude;
            if (dist < 0.5f) return false;
            int n = Physics.RaycastNonAlloc(eye, dir / dist, RingHits, dist - 0.25f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var col = RingHits[i].collider;
                if (col == null || col.transform.IsChildOf(me)) continue;
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool InGameplay()
    {
        try
        {
            if (Time.timeScale <= 0f) return false;
            if (CursorModeManager.IsCursorActive) return false;
        }
        catch { }
        return true;
    }

    private static void UpdateTargeting()
    {
        if (!InGameplay()) { Ui.ShowPrompt(null); _target = null; return; }
        if (Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + 0.1f;
            var c = TalkableNow();
            _target = c;
            _targetPrompt = c == null ? null : PromptFor(c);
        }
        Ui.ShowPrompt(_cfg.ShowTalkPrompt.Value ? _targetPrompt : null, _targetSprites);

        var kb = Keyboard.current;
        bool keyTalk = kb != null && _target != null && Pressed(kb, ref _talkKey, Key.T, "TalkKey");
        if (_target != null && (keyTalk || PadTalkPressed()))
        {
            // Who's under the crosshair at the press, not at the last scan (up to 0.1 s ago: review of a8971f9).
            var now = TalkableNow();
            // CF-11: the pad's interact button on someone at work is the game's (their order); the talk key still talks.
            if (now != null && (keyTalk || !PadInteractIsTheGames(now)))
            {
                if (!keyTalk) _padTalkFrame = Time.frameCount; // review RV-1: the game's action for this press is skipped too
                OpenChat(now);
            }
        }
    }

    private static int _talkableFrame = -1;
    private static Character? _talkable;

    /// <summary>Someone under the crosshair the mod may talk with, looked for once a frame at most.</summary>
    private static Character? TalkableNow()
    {
        if (_talkableFrame == Time.frameCount) return _talkable;
        _talkableFrame = Time.frameCount;
        var c = NpcTargeting.LookedAt(_cfg.MaxTalkDistance.Value);
        if (c != null && NpcTargeting.WhyNot(c, _cfg.ChatWithNamedNpcs.Value) != null) c = null;
        if (c != null && HushedOnCall(c)) c = null; // FB-27: they've told you they're on a call
        return _talkable = c;
    }

    private static string PromptFor(Character c)
    {
        string id = GameContext.NpcId(c);
        if (!KnownNames.TryGetValue(id, out var name))
        {
            var rec = ActiveBrain.Store.Glance(id); // main thread: one try, no waiting, no history; the store in use (the mock's too)
            name = rec != null && rec.State.NameRevealed ? Prompts.FirstName(DisplayName(rec)) : "";
            if (name.Length == 0)
            {
                var person = GameContext.PersonOf(c);
                try { if (person != null && GameContext.IsNamed(person)) name = Prompts.FirstName(person.DisplayedName ?? ""); } catch { }
                if (name == "you") name = "";
            }
            if (KnownNames.Count > 500) KnownNames.Clear();
            KnownNames[id] = name;
        }
        string label = TalkLabel(); // the controller's button while it's in use (CF-07)
        var icon = TalkIcon(); // CF-12: the game's own Xbox / PlayStation icon for it, when it has one
        _targetSprites = icon?.sprites;
        string key = icon is { } i ? i.tag : $"<color={ChatWindow.KeyColor}><b>[{label}]</b></color>";
        // CF-11: on a pad whose talk button is the game's interact, someone at work is the game's to talk to (an order).
        if (Pad.Active && PadInteractIsTheGames(c))
            return $"{key}  <color=#8A857D>{(name.Length > 0 ? name + " is" : "They're")} at work: this button is the game's here " +
                   "(another PadTalkButton talks to them)</color>";
        string waving = Greetings.WantsToTalk(id) ? "  <color=#8A857D><i>· waving you over</i></color>" : "";
        return (name.Length > 0 ? $"{key}  Talk to {name}" : $"{key}  Talk") + waving;
    }

    /// <summary>
    /// The talk prompt's name for them, from a record already in hand, so it isn't read again after every reply (PERF-3).
    /// A name they haven't told you is worked out again from the game the next time it's shown.
    /// </summary>
    private static void NoteKnownName(NpcRecord rec)
    {
        if (!rec.State.NameRevealed) { KnownNames.Remove(rec.Id); return; }
        if (KnownNames.Count > 500) KnownNames.Clear();
        KnownNames[rec.Id] = Prompts.FirstName(DisplayName(rec));
    }

    private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
