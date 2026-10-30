using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using NivalisAIChat.Npc;
using NivalisAIChat.Voice;

namespace NivalisAIChat;

/// <summary>
/// Every setting the mod reads. Lives in BepInEx\config\PeopleOfNivalis.cfg.
/// The mod writes that file itself (BepInEx still reads it normally) so it reads like a settings page: what most
/// people change sits at the top, advanced tuning at the bottom, related settings grouped, and no type boilerplate.
/// Values from older layouts of the file are carried over by setting name.
/// </summary>
internal sealed class ModConfig
{
    // [AI Connection]
    public readonly ConfigEntry<ApiFormat> Format;
    public readonly ConfigEntry<string> ApiUrl;
    public readonly ConfigEntry<string> ApiKey;
    public readonly ConfigEntry<string> Model;
    public readonly ConfigEntry<string> BackgroundModel;
    /// <summary>CF-13: an optional provider of its own for BackgroundModel (empty: the main provider, as before).</summary>
    public readonly ConfigEntry<string> BackgroundApiUrl, BackgroundApiKey, BackgroundApiFormat;

    // [Voices]
    /// <summary>
    /// The mod's version (set by ModRuntime before it reads the settings): the first start of a new version brings the file's
    /// own explanations up to date (FB-23). "" = never (tests, a hot-reloaded dev copy).
    /// </summary>
    public static string RefreshForVersion = "";

    public readonly ConfigEntry<bool> VoicesEnabled;
    public readonly ConfigEntry<int> VoiceLimit;
    public readonly ConfigEntry<string> VoiceLimitPer;
    public readonly ConfigEntry<int> VoiceVolume;
    public readonly ConfigEntry<bool> UseGameVolume;
    public readonly ConfigEntry<float> HearingDistance;
    public readonly ConfigEntry<int> VoiceBehind;
    public readonly ConfigEntry<bool> VoiceMuffle;
    public readonly ConfigEntry<bool> VoiceTones;
    public readonly ConfigEntry<string> PersonVolume;
    public readonly ConfigEntry<string> MumbleVolume;
    public readonly ConfigEntry<string> MemorySearch;
    public readonly ConfigEntry<string> VoyageKey;
    public readonly ConfigEntry<string> VoyageModel;
    public readonly ConfigEntry<int> MemoryIndexLimit;
    public readonly ConfigEntry<bool> RecallFades;
    public readonly ConfigEntry<bool> AutoReloadSettings;
    public readonly ConfigEntry<bool> MatchVoiceToGender;
    public readonly ConfigEntry<bool> SpeakGreetings;
    public readonly ConfigEntry<float> VoiceTimeoutSeconds;

    // [Voices] Providers = [ ... ] block (read and written by the mod itself; see VoiceProviderList)
    /// <summary>
    /// The voice providers as set, a snapshot never changed in place: a reload swaps in a new list, so a reader on
    /// another thread always goes through one whole list (RACE-5).
    /// </summary>
    public IReadOnlyList<VoiceSlot> VoiceSlots => _voiceSlots;
    private volatile List<VoiceSlot> _voiceSlots;
    public readonly List<string> VoiceProblems = new();

    /// <summary>
    /// Held while the file is read again (every setting changes one by one) and while the AI and voice settings are
    /// taken for a request, so a request never gets a new ApiUrl with the old ApiKey (RACE-8).
    /// </summary>
    public readonly object Sync = new();
    private readonly List<string> _providerLines;
    /// <summary>When the file was last written (by the mod, or read after a reload): a newer time means someone edited it.</summary>
    private DateTime _knownWrite;

    // [Keybinds] (FB-81: was [Controls]; MaxTalkDistance, PauseTimeWhileChatting and MinutesPerMessage are in [Conversations])
    public readonly ConfigEntry<string> TalkKey;
    public readonly ConfigEntry<float> MaxTalkDistance;
    public readonly ConfigEntry<bool> PauseTimeWhileChatting;
    /// <summary>Time passing as you talk: game minutes each of their lines moves the clock on (0 = off).</summary>
    public readonly ConfigEntry<int> MinutesPerMessage;
    public readonly ConfigEntry<string> ConsoleKey;
    /// <summary>The phone: the key that brings it out and puts it away.</summary>
    public readonly ConfigEntry<string> PhoneKey;
    /// <summary>FB-76: the key that opens world chat's line (Enter).</summary>
    public readonly ConfigEntry<string> WorldChatKey, WorldChatNextKey, CameraShutterKey, CameraBackKey; // FB-81, FB-82
    /// <summary>FB-76: how far in front of you people can be talked to in world chat (metres).</summary>
    public readonly ConfigEntry<float> WorldChatRange;
    public readonly ConfigEntry<bool> FeatureWorldChat;
    /// <summary>FB-73: fishing with residents (ask them to come, or fish beside someone at it).</summary>
    public readonly ConfigEntry<bool> FishTogether;
    public readonly ConfigEntry<string> PadTalkButton;
    public readonly ConfigEntry<string> PadConsoleButton;

    // [You]
    public readonly ConfigEntry<string> PlayerGender;

    // [City Life]
    public readonly ConfigEntry<bool> NpcsGreetYou;
    public readonly ConfigEntry<float> GreetChance;
    public readonly ConfigEntry<float> GreetRange;
    public readonly ConfigEntry<float> SightDistance;
    public readonly ConfigEntry<float> SightAngle;
    public readonly ConfigEntry<float> GreetCooldownMinutes;
    public readonly ConfigEntry<bool> AiGreetingLines;
    public readonly ConfigEntry<bool> Gossip;
    public readonly ConfigEntry<bool> CityNews;
    public readonly ConfigEntry<bool> MarketTalk;
    public readonly ConfigEntry<int> MarketKnowChance;
    public readonly ConfigEntry<int> MarketOnMindChance;
    public readonly ConfigEntry<bool> PlacesToEat;
    public readonly ConfigEntry<int> OverhearChance;
    public readonly ConfigEntry<float> OverhearDistance;

    // [NPC Chatter]
    public readonly ConfigEntry<bool> Chatter;
    public readonly ConfigEntry<string> ChatterMode;
    public readonly ConfigEntry<bool> OrderTalk;
    public readonly ConfigEntry<bool> AiOrderTalk;
    public readonly ConfigEntry<bool> JustSittingTellsWaiter;
    /// <summary>FB-45: [Features], the switches that had none before (the Features tab lists the older ones beside them).</summary>
    public readonly ConfigEntry<bool> FeatureMoods, FeatureRelationships, FeaturePhoneCalls, FeatureMuttering, FeatureTables, FeatureOverhearing,
        FeatureStoryMemory, FeatureStoryVoice, FeatureWorkLife,
        FeatureFollowing, FeatureRomance, FeatureReminders, FeatureGroupChat, FeatureVoiceInput, FeatureInnerLife, FeatureRoleplay, FeaturePhone, FeatureNightline;
    /// <summary>The inner-life round (0.7.0): bodies that show how people feel, plans that happen, gossip told in code, three ways to say a short line.</summary>
    public readonly ConfigEntry<bool> BodyLanguage, PlansHappen, GossipSpreads, VaryLines;
    /// <summary>[Phone]: the signal (and what takes it), people texting you first, the wallpaper's link.</summary>
    public readonly ConfigEntry<bool> PhoneSignal, PhoneWeather, PhonePlaces, PeopleTextYou, TextsRealLife;
    /// <summary>FB-70: the starting chances (percent) that a text is never seen, or seen and answered later.</summary>
    public readonly ConfigEntry<int> TextNeverSees, TextAnswersLater;
    /// <summary>The phone: voice notes on their answers now and then (off by default), and how often.</summary>
    public readonly ConfigEntry<bool> TextVoiceNotes, AvaVoice;
    public readonly ConfigEntry<int> VoiceNoteChance;
    public readonly ConfigEntry<string> PhoneWeakPlaces, PhoneWeatherCosts, PhoneWallpaper, GalleryFolder;
    public readonly ConfigEntry<int> TextFirstHours, PhoneIndoorsCost;
    /// <summary>FB-72: people you've told you could use some company may come and keep you company.</summary>
    public readonly ConfigEntry<bool> ComeKeepYouCompany;
    /// <summary>CF-20: how a new text shows while the phone is away (Pip, Card, Off), and the chime's volume.</summary>
    public readonly ConfigEntry<string> PhoneNotify;
    public readonly ConfigEntry<int> PhoneNotifyVolume;
    public readonly ConfigEntry<float> PhoneScale;
    /// <summary>FB-71 Nightline, the city's social app: how often residents use it and what they do there, your name on it, photos.</summary>
    public readonly ConfigEntry<int> LineEveryMin, LineEveryMax, LinePostWeight, LineScrollWeight, LineBothWeight, LineReactChance, LineCommentChance,
        LineSeenFalloff, LineReplyChance, LineReactToYouWeight, LineDislikeBelow, LineCheckOwnLikes, LineHandleStyle, LineOffWorldWeight,
        LineDoingsWeight, LinePostsPerDay, LineReactionsPerDay, LineCommentsPerDay, LineNightDim,
        LinePhotoChance, LinePhotosPerDay, ShotEveryHours, ShotsPerArea, ShotsDiskMB;
    public readonly ConfigEntry<bool> LineRemember, LineToasts, LineSelfInPhotos, CityShots, LinePhotosSeen;
    public readonly ConfigEntry<string> LineYourName, LineYourHandle, ImageApiFormat, ImageApiUrl, ImageApiKey, ImageModel, ImageQuality;
    /// <summary>FB-62 following, FB-63 romance, FB-66 group chat, FB-64 voice input, and the reply language.</summary>
    public readonly ConfigEntry<float> FollowDistance;
    public readonly ConfigEntry<int> FollowMinutes, DatesPerDay, PartnerHomeChance, StayOverUntil;
    public readonly ConfigEntry<bool> StayOver; // FB-85
    public readonly ConfigEntry<bool> ResidentsGoTogether, ResidentsDate, Jealousy, GroupVoicesOverlap, VoiceInputAutoSend, LoadPlugins;
    public readonly ConfigEntry<string> DisabledPlugins;
    public readonly ConfigEntry<string> VoiceInputKey, PadVoiceButton, SpeechToTextUrl, SpeechToTextKey, SpeechToTextModel, SpeechLanguage,
        Microphone, ReplyLanguage, SpeechToTextLimitPer;
    /// <summary>FB-75: the most minutes of speech to text per SpeechToTextLimitPer (0 = no limit).</summary>
    public readonly ConfigEntry<int> SpeechToTextLimit, SpeechToTextLimitResetDay;
    public readonly ConfigEntry<int> TableCallChance;
    public readonly ConfigEntry<int> TokenLimit;
    public readonly ConfigEntry<string> TokenLimitPer;
    /// <summary>Read only: where the settings are, for other settings menus (Switchboard doesn't show it).</summary>
    public readonly ConfigEntry<string> SettingsMenu;
    /// <summary>FB-61: the city's memory belongs to the game save being played.</summary>
    public readonly ConfigEntry<bool> MemoriesPerSave;
    /// <summary>FB-59: the day of the month a monthly TokenLimit / VoiceLimit starts again on.</summary>
    public readonly ConfigEntry<int> TokenLimitResetDay, VoiceLimitResetDay;
    public readonly ConfigEntry<string> DynamicEvery;
    public readonly ConfigEntry<int> DynamicMaxNpcs;
    public readonly ConfigEntry<int> DynamicLinesEach;
    public readonly ConfigEntry<int> DynamicPhoneLinesEach;
    public readonly ConfigEntry<int> DynamicThinkLinesEach;
    public readonly ConfigEntry<int> DynamicStartsEach;
    public readonly ConfigEntry<float> DynamicStagger;
    public readonly ConfigEntry<int> DynamicGossipWeight;
    public readonly ConfigEntry<int> DynamicShopWeight;
    public readonly ConfigEntry<int> DynamicPhoneWeight;
    public readonly ConfigEntry<int> DynamicThinkWeight;
    /// <summary>The phone: a friend or partner texting you, as a Dynamic kind.</summary>
    public readonly ConfigEntry<int> DynamicTextWeight;
    public readonly ConfigEntry<float> VoiceDistance;
    public readonly ConfigEntry<float> VoiceBeyond;
    public readonly ConfigEntry<float> TextDistance;
    public readonly ConfigEntry<bool> ReplaceGameChatter;
    public readonly ConfigEntry<string> GroupEvery;
    public readonly ConfigEntry<string> PhoneEvery;
    public readonly ConfigEntry<int> PhoneMaxActive;
    public readonly ConfigEntry<int> PhoneLinesEach;
    public readonly ConfigEntry<string> ThinkEvery;
    public readonly ConfigEntry<int> ThinkMaxActive;
    public readonly ConfigEntry<int> ThinkLinesEach;
    public readonly ConfigEntry<int> ChatterChance;
    public readonly ConfigEntry<int> ChatterMaxGroups;
    public readonly ConfigEntry<int> ChatterPerHour;
    public readonly ConfigEntry<int> ChatterGroupSize;
    public readonly ConfigEntry<int> ChatterJoinChance;
    public readonly ConfigEntry<int> ChatterLinesEach;
    public readonly ConfigEntry<int> ChatterKnownPairs;
    public readonly ConfigEntry<int> ChatterTables;
    public readonly ConfigEntry<float> ChatterRange;
    public readonly ConfigEntry<bool> ChatterWalkUp;
    public readonly ConfigEntry<int> MumbleChance;
    public readonly ConfigEntry<int> PhoneChance;

    // [Food Orders]
    public readonly ConfigEntry<bool> Favours;
    public readonly ConfigEntry<int> FriendDiscountPercent;
    public readonly ConfigEntry<float> FriendDiscountAt;
    public readonly ConfigEntry<bool> ShowRelationshipBar;
    public readonly ConfigEntry<float> RelationshipGainMax, RelationshipLossMin, RelationshipLossMax;
    public readonly ConfigEntry<float> WaveAt, WaveOverAt, FriendAt, SnubAt;
    public readonly ConfigEntry<float> FoodGiftBonus, FoodFavourBonus;
    public readonly ConfigEntry<int> DeliveryFee;

    // [Conversations]
    public readonly ConfigEntry<string> OpeningLine;
    public readonly ConfigEntry<bool> NpcsCanEndConversations;
    public readonly ConfigEntry<float> Patience;
    public readonly ConfigEntry<int> IdleCheckSeconds;
    public readonly ConfigEntry<int> IdleLeaveSeconds;
    public readonly ConfigEntry<bool> AnimateNpc;

    // [People]
    public readonly ConfigEntry<int> Friendliness;
    public readonly ConfigEntry<bool> ChatWithNamedNpcs;
    public readonly ConfigEntry<bool> KeepGameAssignedNames, HideUnmetCustomerNames;
    public readonly ConfigEntry<int> MinAge;
    public readonly ConfigEntry<int> MaxAge;
    public readonly ConfigEntry<float> TransChance;
    public readonly ConfigEntry<float> NonBinaryChance;
    public readonly ConfigEntry<float> QueerChance;
    public readonly ConfigEntry<bool> AgeShapesIdentity;
    public readonly ConfigEntry<bool> LookAtNewPeople;
    public readonly ConfigEntry<bool> RememberDoings;

    // [Memory]
    public readonly ConfigEntry<int> HistoryLimit;
    public readonly ConfigEntry<bool> MeetingSummaries;
    public readonly ConfigEntry<bool> LifeUpdates;
    public readonly ConfigEntry<int> LifeUpdateAfterGameDays;
    public readonly ConfigEntry<float> LifeUpdateAfterRealHours;
    public readonly ConfigEntry<int> RecentMessagesSent;
    public readonly ConfigEntry<int> SummarizeAfterMessages;

    // [Speech Bubbles]
    public readonly ConfigEntry<bool> BubbleFocus;
    public readonly ConfigEntry<string> BubbleBackground;
    public readonly ConfigEntry<int> BubbleBackgroundOpacity;
    public readonly ConfigEntry<string> BubbleBorder;
    public readonly ConfigEntry<int> BubbleBorderOpacity;
    public readonly ConfigEntry<string> BubbleText;
    public readonly ConfigEntry<string> BubbleName;
    public readonly ConfigEntry<int> BubbleTextSize;

    // [Interface]
    public readonly ConfigEntry<float> UiScale;
    /// <summary>Where the chat window was moved and its size ("dx,dy,w,h"; empty = the default).</summary>
    public readonly ConfigEntry<string> ChatWindowLayout;
    public readonly ConfigEntry<bool> ShowTalkPrompt;
    public readonly ConfigEntry<bool> ShowMoodHint;
    public readonly ConfigEntry<int> HistoryShown;
    public readonly ConfigEntry<bool> SmoothTextFade;
    public readonly ConfigEntry<NivalisAIChat.UI.TextSpeed> TextSpeed;

    // [AI Fine Tuning]
    public readonly ConfigEntry<bool> StructuredReplies;
    public readonly ConfigEntry<string> Effort;
    public readonly ConfigEntry<string> ReasoningEffort;
    public readonly ConfigEntry<float> Temperature;
    public readonly ConfigEntry<bool> PromptCaching;
    public readonly ConfigEntry<bool> RefusalFallback;
    public readonly ConfigEntry<int> MaxReplyTokens;
    public readonly ConfigEntry<int> MaxGenerationTokens;
    public readonly ConfigEntry<int> TimeoutSeconds;
    public readonly ConfigEntry<int> GiveUpAfterSeconds;
    public readonly ConfigEntry<string> ExtraHeaders;

    // [Debug]
    public readonly ConfigEntry<bool> LogPrompts;
    public readonly ConfigEntry<bool> DevCommands;
    public readonly ConfigEntry<bool> KeepTranscripts;

    private readonly ConfigFile _file;
    /// <summary>Which old defaults were already moved to new ones (each only once): the data folder's settings-upgrades.txt.</summary>
    private readonly SettingsUpgrades _upgrades;
    private readonly List<(string name, string intro, List<ConfigEntryBase> entries)> _sections = new();
    private readonly Dictionary<string, List<(string section, string value)>> _onDisk;
    /// <summary>Every group in the file at the start, the mod's own and older layouts' alike.</summary>
    private readonly HashSet<string> _sectionsOnDisk = new(StringComparer.Ordinal);
    /// <summary>Extra lines written right after a setting (the voice Providers block follows [Voices] Enabled).</summary>
    private readonly Dictionary<ConfigEntryBase, Action<StringBuilder>> _after = new();
    /// <summary>The older names each setting had: a reload never resets a setting an older layout of the file still holds.</summary>
    private readonly Dictionary<ConfigEntryBase, string[]> _oldKeys = new();
    // The file couldn't be read at the start (another program held it): it isn't written this session, so nothing the
    // player has in it is replaced by defaults (audit step 5).
    private bool _holdWrites, _warnedHold;
    /// <summary>A reload resets at most this many deleted lines to their defaults; more missing looks like a damaged file.</summary>
    private const int MaxDeletedReset = 5;
    /// <summary>The keys (ApiKey, VoyageKey, ExtraHeaders): no rewrite of the file may lose a copy of one (re-review W2).</summary>
    private readonly HashSet<ConfigEntryBase> _secrets = new();
    // While a reload applies the file, each change doesn't rewrite it: it's written once at the end, and never when the
    // read failed, so a line BepInEx stopped at, and every edit after it, stays in the file (audit step 5 review).
    private bool _reloading;
    private readonly List<string> _unreported = new();
    private Action<string>? _report;

    /// <summary>The last <see cref="Reload"/> found the file held by another program: it isn't marked seen, so it's read again at the next check.</summary>
    public bool ReloadBusy { get; private set; }

    /// <summary>
    /// Where problems the player should see in the game go (the mod's [CFG-READ] notes about the settings file). Anything
    /// said before it's set is passed on when it is.
    /// </summary>
    public Action<string>? Report
    {
        get => _report;
        set
        {
            _report = value;
            if (value == null) return;
            foreach (var m in _unreported) value(m);
            _unreported.Clear();
        }
    }

    /// <summary>Called after a reload that an in-game change made first (<see cref="SaveChange"/>): its changes, and its error.</summary>
    public Action<List<string>, string?>? Reloaded { get; set; }

    private void Tell(string message)
    {
        if (_report != null) _report(message);
        else _unreported.Add(message);
    }

    /// <summary>The settings file the mod writes itself (ConfigGuard lets any other save of it go).</summary>
    private static string? _ownPath;
    private static int _refusedSaves;

    /// <summary>
    /// Whether this is People of Nivalis's own settings file. By its path, not the object: a hot-reloaded copy (dev loader)
    /// reads the same file through a ConfigFile of its own, and the first one is still there for other menus to find.
    /// </summary>
    internal static bool Owns(ConfigFile? file)
    {
        if (file == null || _ownPath == null) return false;
        try { return string.Equals(Path.GetFullPath(file.ConfigFilePath), _ownPath, StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; } // a path it can't read isn't this one
    }

    /// <summary>
    /// Another tool asked BepInEx to save the file (another settings menu's Apply): let go, said once. What it changed was
    /// already written by the mod's own writer (every change is, as it happens), so nothing is lost either way.
    /// </summary>
    internal static void SaveRefused()
    {
        if (_refusedSaves++ == 0)
            Log.Info("Another tool asked BepInEx to save PeopleOfNivalis.cfg; People of Nivalis writes its own file (the voice Providers list " +
                     "and your notes stay), so that save was let go. Its settings are in Switchboard (F9).");
    }

    /// <summary>
    /// Every setting carries this: other settings menus (Mod Settings Menu, BepInEx's ConfigurationManager) don't list them,
    /// as they'd save the file through BepInEx and show the keys in plain text; Switchboard lists them (the page's listHidden).
    /// </summary>
    private static readonly ConfigurationManagerAttributes NotInOtherMenus = new() { Browsable = false };

    public ModConfig(ConfigFile cfg)
    {
        _file = cfg;
        try { _ownPath = Path.GetFullPath(cfg.ConfigFilePath); } catch (Exception) { _ownPath = null; }
        cfg.SaveOnConfigSet = false; // the mod writes the file itself, see Write()
        DateTime readAt = default;
        try { readAt = File.GetLastWriteTimeUtc(cfg.ConfigFilePath); } catch { }
        _onDisk = ReadRaw(cfg.ConfigFilePath, out _holdWrites, _sectionsOnDisk);
        MarkRead(readAt); // what was just read counts as read: an in-game change before the next save doesn't read it again
        _upgrades = new SettingsUpgrades(Path.Combine(Path.GetDirectoryName(cfg.ConfigFilePath) ?? ".", "NivalisAIChat"), Log.Warn);

        // ------------------------------------------------------------------ the essentials

        Section("AI Connection", "Start here. Pick a provider from the list under ApiUrl, paste its URL, your key and a model name.");
        // The one line other settings menus show (read only): where People of Nivalis's settings are.
        SettingsMenu = Bind("SettingsMenu", "Switchboard (F9)",
            "Where People of Nivalis's settings are changed in the game: Switchboard (F9, or its button on the pause menu). Other settings " +
            "menus don't list them: they would save this file in a way that loses the voice Providers list and show your keys. " +
            "Or change them right here in this file.",
            new ConfigurationManagerAttributes { Browsable = true, ReadOnly = true, Order = 1000 });
        Format = Bind("ApiFormat", ApiFormat.OpenAI,
            "Anthropic for Claude (api.anthropic.com). OpenAI for everything else: OpenAI, Gemini, Grok, DeepSeek, Mistral, OpenRouter, Groq, " +
            "and local servers like Ollama or LM Studio.");
        ApiUrl = Bind("ApiUrl", "https://api.openai.com/v1",
            "Where requests go. Paste a provider's URL from this list as-is.\n" +
            "\n" +
            "AUTHOR'S NOTE: I recommend OpenAI (https://api.openai.com/v1) with Model = gpt-5.4-nano.\n" +
            "It's extremely cost-efficient, and NPCs still sound like real people.\n" +
            "\n" +
            "Popular cloud providers (get a key on their website):\n" +
            "  OpenAI ............ https://api.openai.com/v1                           (ApiFormat = OpenAI)  <- author's pick\n" +
            "  Anthropic (Claude)  https://api.anthropic.com/v1/messages               (ApiFormat = Anthropic)\n" +
            "  Google Gemini ..... https://generativelanguage.googleapis.com/v1beta/openai/\n" +
            "  xAI (Grok) ........ https://api.x.ai/v1\n" +
            "  DeepSeek .......... https://api.deepseek.com\n" +
            "  Mistral ........... https://api.mistral.ai/v1\n" +
            "  Qwen (Alibaba) .... https://dashscope-intl.aliyuncs.com/compatible-mode/v1  (or your region's URL from the Model Studio console)\n" +
            "  Moonshot (Kimi) ... https://api.moonshot.ai/v1\n" +
            "  OpenRouter ........ https://openrouter.ai/api/v1                        (hundreds of models from every provider, one key)\n" +
            "  Groq .............. https://api.groq.com/openai/v1                      (very fast open models)\n" +
            "  Cerebras .......... https://api.cerebras.ai/v1\n" +
            "  Together AI ....... https://api.together.xyz/v1\n" +
            "  Fireworks AI ...... https://api.fireworks.ai/inference/v1\n" +
            "Local, free, runs on your own PC (no key needed; ApiFormat = OpenAI):\n" +
            "  Ollama ............ http://localhost:11434/v1\n" +
            "  LM Studio ......... http://localhost:1234/v1\n" +
            "  KoboldCpp ......... http://localhost:5001/v1\n" +
            "  text-generation-webui  http://127.0.0.1:5000/v1\n" +
            "  vLLM .............. http://localhost:8000/v1");
        ApiKey = Bind("ApiKey", "",
            "Your key from that provider. Leave empty for local servers. " +
            "If empty, the ANTHROPIC_API_KEY or OPENAI_API_KEY environment variable is used when set.");
        Model = Bind("Model", "gpt-5.4-nano",
            "The model NPCs talk with, as the provider names it. Author's pick: gpt-5.4-nano (OpenAI), extremely cost-efficient.\n" +
            "Claude: claude-opus-5-5 (best), claude-sonnet-5-5, claude-haiku-5-5 (far cheaper). " +
            "Others: whatever their model list shows (gemini flash models, grok, deepseek-flash, llama3.1:8b on Ollama...).");
        BackgroundModel = Bind("BackgroundModel", "",
            "Optional cheaper model for behind-the-scenes work: creating people, moving their lives on, diaries, memory summaries " +
            "and greeting lines. Empty = use Model. Doesn't change how NPCs talk to you. It's asked at ApiUrl with ApiKey, " +
            "unless BackgroundApiUrl names another provider.");
        BackgroundApiUrl = Bind("BackgroundApiUrl", "",
            "Optional: another provider for BackgroundModel (same list as ApiUrl), so behind-the-scenes work can go to a different " +
            "account or a local server. Empty = the same provider and key as above. Set BackgroundModel too: the model's name at " +
            "that provider. Talking to people, and someone new met with a picture, always use the main provider.");
        BackgroundApiKey = Bind("BackgroundApiKey", "",
            "The key for BackgroundApiUrl (leave empty for a local server). Used only when BackgroundApiUrl is set.");
        BackgroundApiFormat = Bind("BackgroundApiFormat", "Same",
            "BackgroundApiUrl's format: Same (as ApiFormat), OpenAI or Anthropic. Used only when BackgroundApiUrl is set.");
        TokenLimit = Bind("TokenLimit", 0,
            "The most AI tokens the mod may use per TokenLimitPer: everything it asks for (your chats, residents' chatter, new people, " +
            "memories), sent and received, as your provider counts it. Once reached, nothing more is asked until the next hour/day/month " +
            "(you'll see why). 0 = no limit. In the game, /usage shows what's been used. To cap each single reply instead, see " +
            "MaxReplyTokens in [AI Fine Tuning]. Voices are counted by the voice provider, not here.");
        TokenLimitPer = Bind("TokenLimitPer", "Day", "How often the TokenLimit starts again: Hour, Day, Week (from Monday) or Month.");
        TokenLimitResetDay = Bind("TokenLimitResetDay", 1,
            "With TokenLimitPer = Month: the day of the month it starts again, 1 to 31, to match your provider's billing day (past a short " +
            "month's end, its last day). By your PC's calendar, at midnight.");

        Section("Voices", "People speak their lines out loud. Each person gets a voice once and keeps it (until /resetnpc or /resetall).");
        VoicesEnabled = Bind("Enabled", true,
            "Turn voices on. The OpenAI line below works with your chat's OpenAI key as it is, and its voices are paid from your OpenAI " +
            "credit. For free voices, use the windows line instead: delete the # in front of it, and put one in front of the OpenAI " +
            "line (Windows' own voices, on your PC).");
        // The short-lived [Voice Provider N] groups are carried into the list once only: a list the player takes out later
        // brings back the defaults, not those groups' keys (review of a0fa75f).
        _providerLines = ReadProviderBlock(cfg.ConfigFilePath, out bool blockUnread, out bool blockUnclosed)
                         ?? (MigratedProviderLines() is { } migrated && _upgrades.Once("Voices.Providers=[Voice Provider N]") ? migrated : null)
                         ?? VoiceProviderList.DefaultLines();
        _holdWrites |= blockUnread; // never the defaults written over a voice list that wasn't read
        _blockUnclosed = blockUnclosed;
        if (blockUnclosed)
            Tell("[CFG-READ] The voice Providers list in the settings file has no closing ']': it was read up to the setting after it. " +
                 "Put the ']' back on its own line after your last voice line and save, and check they're all still there.");
        _voiceSlots = VoiceProviderList.Parse(_providerLines, VoiceProblems);
        _after[VoicesEnabled] = WriteProviderBlock;
        VoiceLimit = Bind("VoiceLimit", 0,
            "The most characters of speech your voice providers may be asked for per VoiceLimitPer: every spoken line, yours to talk to " +
            "and residents' chatter alike (most voice providers bill by the character). Once reached, lines are shown without voices " +
            "until the next hour/day/month, and you're told why. 0 = no limit. Windows' voices and a voice server on your own PC cost " +
            "nothing and aren't counted. In the game, /usage shows what's been used.");
        VoiceLimitPer = Bind("VoiceLimitPer", "Day", "How often the VoiceLimit starts again: Hour, Day, Week (from Monday) or Month. " +
            "Each voice provider can have its own limit too (its limit in the settings menu, or \"limit: 125000/month\" in its line).");
        VoiceLimitResetDay = Bind("VoiceLimitResetDay", 1,
            "With VoiceLimitPer = Month: the day of the month it starts again, 1 to 31 (past a short month's end, its last day). " +
            "Each provider's own monthly limit has its own day (\"resets: 15\" in its line, or in the settings menu).");
        VoiceVolume = Bind("Volume", 80, "Voice volume, 0 to 100.");
        UseGameVolume = Bind("UseGameVolume", true, "Voices go through the game's own audio, so the game's volume settings apply to them too.");
        HearingDistance = Bind("HearingDistance", 20f, "Voices come from the person in 3D and fade out at this distance (metres).");
        VoiceBehind = Bind("VoiceBehind", 50,
            "How loud someone straight behind you sounds, as a percent of someone in front (to the side is in between). 100 = no difference.");
        VoiceMuffle = Bind("VoiceMuffle", true,
            "Voices behind you and far away sound a little muffled, the way your head and the air soften them in real life.");
        VoiceTones = Bind("VoiceTones", true,
            "Voices that can take it get a tone that follows how the conversation is going: OpenAI (gpt-4o-mini-tts), ElevenLabs " +
            "(only the eleven_v3 and eleven_v4 models: put model: eleven_v4 in its Providers line), Azure voices with speaking styles, " +
            "and Google's Gemini voices. The tone builds and fades over a few lines as their mood does, never jumping from one reply " +
            "to the next. Off = their plain voice.");
        PersonVolume = Bind("PersonVolume", "70-100",
            "Everyone talks at their own volume, the way some people are just quieter: each person gets a level in this range " +
            "(percent of Volume) and keeps it. 100-100 = everyone the same. Applies wherever they speak.");
        MumbleVolume = Bind("MumbleVolume", "50-70",
            "Thinking out loud is quieter: someone muttering to themselves speaks at a level in this range instead (percent of " +
            "Volume; whoever talks louder also mutters louder). Both chatter modes.");
        MatchVoiceToGender = Bind("MatchVoiceToGender", true, "Men get men's voices and women get women's voices, where the provider says which is which.");
        SpeakGreetings = Bind("SpeakGreetings", true, "Street greetings are spoken too (each a short line from your voice provider).");
        VoiceTimeoutSeconds = Bind("VoiceTimeoutSeconds", 15f,
            "Text waits for the voice so both start together. After this many seconds without it, the reply is cancelled with an error.");

        // FB-81 (the author: "move the "controls" out of the advanced sub tab ... And label it keybinds ... a keybinding for all
        // the keys related with people of nivalis"): every key and button the mod listens for, in one group.
        Section("Keybinds", "Every key and button People of Nivalis listens for, on a keyboard or a controller.");
        TalkKey = Bind("TalkKey", "T",
            "Key that starts a conversation with the person under your crosshair. Examples: T, G, F2.");
        ConsoleKey = Bind("ConsoleKey", "Backquote",
            "Opens the command console (type /help in it). Backquote is the ` key, usually under Esc. Examples: Backquote, F8, Insert.");
        WorldChatKey = Bind("WorldChatKey", "Enter",
            "Opens world chat's line (Features > WorldChat): type to the people in front of you, no chat window. Enter sends, Enter on an " +
            "empty line or Esc closes it. Examples: Enter, Y, F2.");
        PhoneKey = Bind("PhoneKey", "N",
            "Brings your phone out and puts it away (Features > Phone). Not during a chat, and no chat starts while it's out. " +
            "While you type in it, every key types. Examples: N, P, F3, Tab.");
        WorldChatNextKey = Bind("WorldChatNextKey", "Tab",
            "In world chat's line: moves who you're talking to on to the next person in front of you. A key that doesn't type " +
            "(it would land in the line): Tab, F1-F12, Insert, PageDown...");
        // FB-82 (the author: "Left clicking should snap a photo, space shouldn't. these should also be keybinds that can be set").
        CameraShutterKey = Bind("CameraShutterKey", "Left click",
            "In the phone's Camera: takes a photo (A on a controller does too). Left click, Right click, Middle click, Mouse 4, Mouse 5, or a key (F, E, Enter...).");
        UpgradeDefault(CameraShutterKey, "Space"); // 0.10.0's first build had Space
        CameraBackKey = Bind("CameraBackKey", "Right click",
            "In the phone's Camera: leaves it (Esc does too). Left click, Right click, Middle click, Mouse 4, Mouse 5, or a key.");
        VoiceInputKey = Bind("VoiceInputKey", "F4",
            "Hold this key while you speak in a chat (Features > VoiceInput). A key that doesn't type: F1-F12, RightCtrl, RightAlt, Tab, Insert... (a letter would land in the chat box).");
        PadTalkButton = Bind("PadTalkButton", "Interact",
            "Controller button that starts a conversation with the person under your crosshair. Interact = the game's own " +
            "interact button on whichever controller is plugged in (A on Xbox, Cross on PlayStation). Or X (Square), Y (Triangle), " +
            "B (Circle), LB (L1), RB (R1), RT (R2), LT (L2), or off. Only while someone you can talk to is under your crosshair: " +
            "the game's own use of that button (its dialogue, placement mode, picking up, putting away) is skipped for that one " +
            "press, and everywhere else the button does what it always does. In a chat the d-pad reaches the buttons beside it " +
            "(the keyboard, the settings), A / Cross presses one, B / Circle leaves.");
        UpgradeDefault(PadTalkButton, "RT"); // the author, 2026-10-13: the interact button of whatever pad is plugged in
        PadConsoleButton = Bind("PadConsoleButton", "LT",
            "Controller button that opens the command console (its cog opens the settings): LT (L2 on PlayStation), RT (R2), or " +
            "off. The same button as PadTalkButton talks when you're looking at someone you can talk to, and opens the console " +
            "otherwise.");
        PadVoiceButton = Bind("PadVoiceButton", "off",
            "Controller button to hold while you speak: Y (Triangle), RB (R1), LB (L1), or off. (LB is backspace while the on-screen keyboard is up.)");

        Section("You", "Who your character is, as NPCs see it.");
        PlayerGender = Bind("Gender", "",
            "How your character identifies, in your own words (man, woman, trans woman, trans man, non-binary...). Empty = not set. " +
            "NPCs only see how you come across and learn the rest if you tell them; their orientation and views decide whether they're into you, or okay with it.");

        // ------------------------------------------------------------------ features you can switch on and off

        Section("Features", "Switch off anything you don't want: it's skipped everywhere, nothing of it is asked of the AI, and what it " +
                            "saved is kept for when you switch it back on. The menu's Features tab shows these beside the older switches " +
                            "(Chatter, OrderTalk, NpcsGreetYou, Gossip...).");
        FeatureMoods = Bind("Moods", true,
            "People have moods: rolled each time you meet, changed by what's said, heard in their voice, shown by their name, and " +
            "felt in how patient they are and how they greet you. Off = nobody has a mood.");
        FeatureRelationships = Bind("Relationships", true,
            "How each person feels about you: the bar, how warmly they talk to you, waving or snubbing you, friends' discounts, " +
            "becoming friends. Off = everyone treats you as neutral and nothing you say moves it (the bar is kept for later).");
        FeaturePhoneCalls = Bind("ResidentPhoneCalls", true, "Residents near you take phone calls (also a phone going off at a table). Off = no calls.");
        FeatureMuttering = Bind("Muttering", true, "Residents think out loud to themselves now and then. Off = they don't.");
        FeatureTables = Bind("TableConversations", true, "People sharing a table at a food place talk as their table. Off = they don't talk as a table.");
        FeatureOverhearing = Bind("Overhearing", true,
            "People standing near a conversation remember what they overheard, and may bring it up with you later. Off = nobody overhears.");
        FeatureStoryMemory = Bind("StoryMemory", true,
            "Story characters keep the story conversations you have with them (only what you've seen: nothing you haven't reached), show " +
            "them in the chat and talk about them as things you both remember. Off = they're not kept or used (what's kept stays).");
        FeatureStoryVoice = Bind("StoryVoice", true,
            "Story characters talk the way they do in their story: a short note on how they speak, written once from the lines you've seen. " +
            "Off = they talk as the AI imagines them.");
        FeatureWorkLife = Bind("WorkLife", true,
            "Your staff know how they feel about the job (the game's own satisfaction, their pay against the usual, their hours) and " +
            "remember what happens at work: a raise or a pay cut, growing unhappy, long or packed shifts. It gently changes how " +
            "they feel about you. Off = none of it is noted or told (what's kept stays).");
        FeatureFollowing = Bind("Following", true,
            "Ask someone to come with you and, if they're willing (and free), they follow you: walking or running as you do, never " +
            "closer than FollowDistance, through a cab ride or into a building with you. Off = nobody follows.");
        FeatureRomance = Bind("Romance", true,
            "Dating: ask someone out (how they feel about you decides how likely a yes is), confirm it, go on dates, move in together; " +
            "residents date each other too, and cheating can come out through gossip. Adults only. Off = none of it (what's kept stays).");
        FeatureReminders = Bind("Reminders", true,
            "Plans and reminders made in a chat (\"meet me at the market tomorrow at 8\", \"remind me to pay the rent\") show as the " +
            "game's own pop-up when they're due, and the person remembers what you agreed. Off = none are kept.");
        FeatureGroupChat = Bind("GroupChat", true,
            "Talk to someone in a group and the whole group is in the chat: each of them can answer, one after another, turning to " +
            "whoever speaks. Off = talking to one of them takes just that one aside.");
        FeatureInnerLife = Bind("InnerLife", true,
            "People have an inner life the code keeps: how they feel carries over (insult someone and they're still sore an hour later), " +
            "something is on their mind, they remember what they saw others do, and it shows in what they say. Kept with each save. " +
            "No extra AI requests. Off = moods are rolled fresh each time, as before (what's kept stays).");
        FeatureRoleplay = Bind("Roleplay", true,
            "Now and then, under what someone says, what they do as they say it, in slanted gold text: \"She rolls her eyes.\", " +
            "\"He glances at you, then away.\" The AI chooses when it adds something (not every line), from what you just said and how " +
            "they feel; a nod, a wave or looking down is played by their body too. You're called by your Gender (they/them when it's " +
            "empty). A few more words back from the AI on those lines. Off = only their words.");
        FeaturePhone = Bind("Phone", true,
            "Your phone (PhoneKey): ask people for their number in a chat, and if they like you enough they give it; then text them " +
            "from the phone and they answer in their own voice, remembering you (and it); friends and partners text you first now and " +
            "then. Contacts, Messages and Settings (your wallpaper from a picture link, the signal). Kept with each save. Each answer " +
            "is an AI request, like a line in a chat. Off = no phone, nobody gives a number.");
        FeatureWorldChat = Bind("WorldChat", true,
            "World chat (FB-76): press WorldChatKey (Enter) and type to the people in front of you; they answer over their heads, one after " +
            "another, and the ones nearby overhear and remember it. Chips above the line show who's around: click to pick who you talk " +
            "to (several, or All), Tab moves on. Each thing you say is one AI request (your own use). Off = no line.");
        FeatureNightline = Bind("Nightline", true,
            "Nightline, the city's social app on your phone (FB-71): residents (even ones nowhere near you) drop a line about their day " +
            "now and then, scroll, like, dislike and comment, and remember what they saw and why; you can post and comment too. With an " +
            "image service set up ([Nightline Photos]) some posts come with a photo made from the city's own street views. Each visit is " +
            "one AI request (residents' use, in the Usage Optimizer). Kept with each save. Off = no app, nobody posts.");
        FeatureVoiceInput = Bind("VoiceInput", false,
            "Talk with your microphone: hold VoiceInputKey (or PadVoiceButton) while you speak, and what you said is typed into the " +
            "chat (sent at once with VoiceInputAutoSend). Needs a speech-to-text service: see [Voice Input]. Off by default."); 

        Section("Following", "People who come along with you (Features > Following).");
        FishTogether = Bind("FishTogether", true,
            "Fishing together (FB-73): ask someone to go fishing and they come along; at the water, when you fish, they fish beside you " +
            "(the game's own pose and rod). Or fish beside a resident who's at it. Now and then one says something (a good catch, a long " +
            "quiet spell): a greeting-style line, counted in the Usage Optimizer. They remember going fishing with you.");
        FollowDistance = Bind("FollowDistance", 0.75f,
            "How close (metres) someone following you comes: never closer than this (0.67 at the least), so they don't crowd you.");
        FollowMinutes = Bind("FollowMinutes", 60,
            "How long (real minutes) someone keeps following before they need to get on with their day. 0 = until you tell them to stop.");
        ResidentsGoTogether = Bind("ResidentsGoTogether", true,
            "Residents who agree in a conversation to go somewhere together (to eat, say) walk there together. Experimental.");

        Section("Romance", "Dating, dates and living together (Features > Romance). Adults only.");
        DatesPerDay = Bind("DatesPerDay", 1, "Dates a day with the same person (0 = no dates).");
        PartnerHomeChance = Bind("PartnerHomeChance", 70,
            "When you come home, the chance (%) the person you live with is there too, getting on with things around the place.");
        // FB-85 (the author: a tester "was still having trouble when it came to dates, having an npc stay over her place").
        StayOver = Bind("StayOver", true,
            "From 8 in the evening, someone you're seeing can come home with you and stay the night (they say so in the chat). They " +
            "stay at your place until the morning, even if you sleep, then head off.");
        StayOverUntil = Bind("StayOverUntil", 8, "The hour (6 to 12) someone who stayed the night heads off, give or take an hour.");
        ResidentsDate = Bind("ResidentsDate", true, "Residents can start dating each other, and break up.");
        Jealousy = Bind("Jealousy", true,
            "Someone who finds out (by seeing it, or through gossip) that their partner flirted with someone else reacts: they call it " +
            "out, end it, or get back at them. With you too, if you're seeing someone.");

        Section("Inner Life", "People carry what happens to them from one moment to the next (Features > InnerLife), kept with each save. " +
                              "It all works near you only (inside your chatter ring), and none of it asks the AI for more than it already does.");
        BodyLanguage = Bind("BodyLanguage", true,
            "People near you show how they feel: a happy stride, a slow sad walk, hurrying when they're wound up; friends who spot each other " +
            "wave, people who can't stand each other look away. The game's own walks and gestures; staff at work are left alone.");
        PlansHappen = Bind("PlansHappen", true,
            "A plan to meet someone somewhere (\"see you at the noodle bar at 8\") really happens: be there in time and they turn up and come " +
            "over to you (someone flaky may not make it). Don't show up and you stood them up: they remember it, and it gets around.");
        GossipSpreads = Bind("GossipSpreads", true,
            "What people see each other do (a row in a bar, a breakup, you being rude to someone) gets passed on in their conversations, " +
            "changing a little each time it's told, and they say where they heard it. Off = they keep what they saw to themselves.");
        VaryLines = Bind("VaryLines", true,
            "Greetings and one-line mutters are asked for three ways to say it, and the one least like what that person said lately is " +
            "used, so nobody keeps saying the same thing. A few more words back from the AI on those short lines; no extra requests. " +
            "While the Usage Optimizer's pacer is holding residents' talk back to keep within a limit, this stops first.");

        Section("Phone", "Your phone (Features > Phone, PhoneKey). Its own Settings app changes these too.");
        PhoneScale = Bind("PhoneScale", 1f,
            "The phone's size, 0.8 to 1.25. It's always its own shape (never stretched), about three quarters of the screen's height " +
            "at 1, never smaller than your Windows display scale allows for readable text, and never taller than the screen.");
        PhoneSignal = Bind("PhoneSignal", true,
            "Simulate the signal: bars come and go with where you are and the weather, and with no bars a text waits until there's " +
            "one. Off = always full bars.");
        PhoneWeather = Bind("PhoneWeather", true, "The weather takes bars (how many: PhoneWeatherCosts).");
        PhoneWeatherCosts = Bind("PhoneWeatherCosts", Phone.Reception.DefaultCosts,
            "Bars each kind of weather takes, 0 to 4: rain (pouring), thunder, snow (heavy), blizzard (instead of snow), fog (thick). " +
            "They add up.");
        PhonePlaces = Bind("PhonePlaces", true,
            "Places matter: indoors costs bars (PhoneIndoorsCost; never at home: your own wifi), and the places in PhoneWeakPlaces have a weak signal.");
        PhoneIndoorsCost = Bind("PhoneIndoorsCost", 1, "Bars lost indoors (0 to 2), anywhere but home.");
        PhoneWeakPlaces = Bind("PhoneWeakPlaces", Phone.Reception.DefaultPlaces,
            "Places with a weak signal and their bars at most, separated by commas: Docks=2, Lowtown=3. A name matches the district, " +
            "the venue or the scene you're in (part of its name is enough). /phone shows where you are.");
        PeopleTextYou = Bind("PeopleTextYou", true,
            "Friends and partners who gave you their number text you first now and then (an AI request each; the Usage Optimizer's " +
            "AI-greetings lever and its pacer hold them back too).");
        TextsRealLife = Bind("TextsRealLife", true,
            "Texting like real life: people look at their phone when they do (asleep at night, so in the morning), sometimes never see " +
            "a text, sometimes see it and answer later in their own time (they remember not answering); the closer you are, the more " +
            "they answer; once you're texting back and forth they keep answering. Off = they always see it and answer at once.");
        TextNeverSees = Bind("TextNeverSees", 10,
            "The starting chance (%, 0 to 50) that a text of yours is never seen: near 0 for close friends and partners, more for " +
            "people who dislike you. Another text gets another chance.");
        TextAnswersLater = Bind("TextAnswersLater", 10,
            "The starting chance (%, 0 to 50) that they see it and answer later (or not at all): rarer the closer you are. They " +
            "remember not answering, and say so (or not) when they do.");
        TextVoiceNotes = Bind("TextVoiceNotes", false,
            "Voice notes: now and then an answer to your texts comes with a voice note under it (their voice, through a phone), to " +
            "play and stop on your phone; kept on your PC. Texts are never read out otherwise. Uses voice characters like a line " +
            "in a chat (counted as yours by the Usage Optimizer). Off by default.");
        AvaVoice = Bind("AvaVoice", false,
            "Ava on your phone: her story lines always play in her own recorded voice, where the game has one. With this on, a text " +
            "of hers with no recording may come as a voice note in a made voice, when she wants it to (she decides). Off by default; " +
            "VoiceNoteChance doesn't apply to her.");
        VoiceNoteChance = Bind("VoiceNoteChance", 20,
            "Voice notes: the chance (%, 0 to 100) an answer comes with one, when TextVoiceNotes is on. Only short texts.");
        TextFirstHours = Bind("TextFirstHours", 3,
            "Game hours at least between texts someone sends you first (anyone; then it's a roll, so it's often longer).");
        PhoneNotify = Bind("PhoneNotify", "Pip",
            "A new text while the phone is away: Pip = a small phone at the bottom right with your unread count, and who it's from " +
            "for a few seconds (never what they said); Card = a card at the top right with their first words; Off = nothing shown.");
        PhoneNotifyVolume = Bind("PhoneNotifyVolume", 50, "The soft chime for a new text, 0 to 100 (0 = silent).");
        ComeKeepYouCompany = Bind("ComeKeepYouCompany", true,
            "Text someone (or post on Nightline) that you could use some company where you are, and they may come: at most two, each on " +
            "their own time. They greet you (by your name only if they know it), stay a while, sit with you if you sit, and go when " +
            "you walk off. Nothing extra is asked of the AI to decide it.");
        PhoneWallpaper = Bind("PhoneWallpaper", "",
            "The link your phone's wallpaper came from (set in the phone's Settings; it's kept on your PC, so it stays if the link dies). " +
            "Empty = the default.");
        GalleryFolder = Bind("GalleryFolder", "",
            "FB-80: where the phone's Camera keeps your photos (the Gallery shows them). Empty = your Pictures folder, in \"Nivalis Nights\".");

        Section("Nightline", "The city's social app on the phone (Features > Nightline). Every weight at 0 turns that part off.");
        LineEveryMin = Bind("LineEveryMin", 30, "Game minutes at least between someone opening Nightline (5 to 1440).");
        LineEveryMax = Bind("LineEveryMax", 120, "Game minutes at most between someone opening Nightline (a random time between the two).");
        LinePostWeight = Bind("LinePostWeight", 50, "When someone opens the app: how often they only post a line (0 to 1000, against the next two).");
        LineScrollWeight = Bind("LineScrollWeight", 50, "...how often they only scroll (react and comment).");
        LineBothWeight = Bind("LineBothWeight", 25, "...how often they scroll and post.");
        LineReactChance = Bind("LineReactChance", 50, "Scrolling: the chance (%) they may react to each line they see (like or dislike; they decide which, or neither).");
        LineCommentChance = Bind("LineCommentChance", 50, "Scrolling: the chance (%) they may comment on each line they see.");
        LineSeenFalloff = Bind("LineSeenFalloff", 70, "Each line further down is seen this % as often as the one above (the newest is always seen; at most 8 in one go).");
        LineReplyChance = Bind("LineReplyChance", 40, "The chance (%) someone looks back at their own line with new comments and may answer them.");
        LineReactToYouWeight = Bind("LineReactToYouWeight", 100, "How often your lines are among what residents see, against others' (0 = never; 100 = like anyone's; up to 300).");
        LineDislikeBelow = Bind("LineDislikeBelow", 35, "How they feel about whoever posted it (0 to 100) below which a dislike becomes likely.");
        LineCheckOwnLikes = Bind("LineCheckOwnLikes", 50, "The chance (%) someone checks who reacted to their own line when they open the app (more for the vain, less for the aloof).");
        LineHandleStyle = Bind("LineHandleStyle", 50, "Handles: 0 = always from their name, 100 = always one the AI chooses for who they are, between = a mix.");
        LineYourName = Bind("LineYourName", "", "Your name on Nightline (empty = You).");
        LineYourHandle = Bind("LineYourHandle", "you", "Your @handle on Nightline (letters, digits, dots, underscores).");
        LineOffWorldWeight = Bind("LineOffWorldWeight", 50, "How often it's someone who isn't near you (0 to 100; the rest: someone around you).");
        LineDoingsWeight = Bind("LineDoingsWeight", 60, "The chance (%) a post is about what they're doing right now when that's known (a meal, work, a date with you).");
        LinePostsPerDay = Bind("LinePostsPerDay", 24, "Posts from residents per game day at most (0 = none).");
        LineReactionsPerDay = Bind("LineReactionsPerDay", 120, "Reactions from residents per game day at most.");
        LineCommentsPerDay = Bind("LineCommentsPerDay", 60, "Comments from residents per game day at most.");
        LineRemember = Bind("LineRemember", true, "What they saw, liked, disliked or said on Nightline (and why) goes into their memory, so they can bring it up.");
        LineToasts = Bind("LineToasts", true, "A card when someone reacts to or comments on your line (off = the badge only).");
        LineNightDim = Bind("LineNightDim", 13, "How much the light app is dimmed at night (%, 0 to 30), so it doesn't glare in a dark scene.");

        Section("Nightline Photos", "Photos on residents' lines, made by an image service of yours from the city's own street views. Nothing leaves your PC unless a key is set.");
        LinePhotoChance = Bind("LinePhotoChance", 15, "The chance (%) a post may come with a photo (they decide if it fits).");
        LinePhotosPerDay = Bind("LinePhotosPerDay", 6, "Photos per game day at most (each one is an image request: in the Usage Optimizer, residents' use).");
        LinePhotosSeen = Bind("LinePhotosSeen", true,
            "FB-80: residents whose AI can see pictures look at the photos you post (one picture each time someone sees your line; it's " +
            "their Nightline use). Never a photo with someone under 18 in view, or one the Camera didn't take. Off = they go by your words only.");
        LineSelfInPhotos = Bind("LineSelfInPhotos", true, "A photo that's of the poster uses their own picture so they look like themselves. " +
            "That picture is taken the first time they're near you (adults only) and kept on this PC; it's also their round picture on Nightline, with this on or off.");
        ImageApiFormat = Bind("ImageApiFormat", "OpenAI", "OpenAI (its images API, or a server shaped like it) or OpenRouter.");
        ImageApiUrl = Bind("ImageApiUrl", "", "The image service's address (empty = the format's own: api.openai.com/v1 or openrouter.ai/api/v1).");
        ImageApiKey = Bind("ImageApiKey", "", "Its key. Kept here only; never logged.");
        ImageModel = Bind("ImageModel", "gpt-image-1-mini", "The image model (gpt-image-1-mini, gpt-image-1; on OpenRouter for example openai/gpt-image-1 or google/gemini-2.5-flash-image).");
        ImageQuality = Bind("ImageQuality", "low", "low, medium or high (low is cheapest).");
        CityShots = Bind("CityShots", true, "Street views taken in the background now and then, for photos: the game's view only (no UI), with everyone hidden (no people in them, never anyone under 18). Kept on your PC.");
        ShotEveryHours = Bind("ShotEveryHours", 1, "Game hours between street views (1 to 24).");
        ShotsPerArea = Bind("ShotsPerArea", 100, "Street views kept per area at most (the oldest go).");
        ShotsDiskMB = Bind("ShotsDiskMB", 300, "MB of street views kept in all at most.");

        Section("Group Chat", "Talking with a group at once (Features > GroupChat).");
        GroupVoicesOverlap = Bind("GroupVoicesOverlap", false,
            "Let the group's voices overlap. Off = each line is spoken after the last one ends.");

        Section("Plugins", "Other people's add-ons for People of Nivalis: DLLs in BepInEx\\plugins\\PeopleOfNivalis\\plugins (see PLUGINS.md). " +
                           "A plugin runs in the game like any mod: only add ones you trust.");
        LoadPlugins = Bind("LoadPlugins", true, "Load the plugins in that folder when the game starts (nothing there, nothing loaded).");
        DisabledPlugins = Bind("DisabledPlugins", "", "Plugins to leave out, by id or file name, separated by commas.");

        Section("Voice Input", "Talking with your microphone (Features > VoiceInput). Speech to text goes to any OpenAI-style service: " +
                               "OpenAI or Groq (a key), or a free one running on your PC (whisper.cpp's server, faster-whisper / speaches).");
        SpeechToTextUrl = Bind("SpeechToTextUrl", "https://api.openai.com/v1",
            "The speech-to-text service (its /audio/transcriptions is used). OpenAI: https://api.openai.com/v1 · Groq: https://api.groq.com/openai/v1 · " +
            "on your PC, e.g. http://localhost:8080/v1 (whisper.cpp server) or http://localhost:8000/v1 (speaches).");
        SpeechToTextKey = Bind("SpeechToTextKey", "", "Its key. Empty = the main ApiKey when it's the same service as ApiUrl, none otherwise (a local server).");
        SpeechToTextModel = Bind("SpeechToTextModel", "whisper-1", "The model: whisper-1 (OpenAI), whisper-large-v3-turbo (Groq), or what your server serves.");
        SpeechLanguage = Bind("SpeechLanguage", "", "The language you speak, as a two-letter code (en, fr, de...). Empty = worked out from what you say.");
        Microphone = Bind("Microphone", "", "The microphone's name as Windows shows it. Empty = Windows' default.");
        VoiceInputAutoSend = Bind("VoiceInputAutoSend", true, "Send what you said at once. Off = it's typed into the box for you to check first.");
        SpeechToTextLimit = Bind("SpeechToTextLimit", 0,
            "The most minutes of your speech the speech-to-text service may be sent per SpeechToTextLimitPer (0 = no limit). " +
            "Past it, voice input says so and you type until it starts again. A service on your PC is free: no limit needed.");
        SpeechToTextLimitPer = Bind("SpeechToTextLimitPer", "Day", "How often the SpeechToTextLimit starts again: Hour, Day, Week (from Monday) or Month.");
        SpeechToTextLimitResetDay = Bind("SpeechToTextLimitResetDay", 1,
            "With SpeechToTextLimitPer = Month: the day of the month it starts again, 1 to 31 (past a short month's end, its last day). " +
            "Your speech service's billing day: on the AI's own account (OpenAI), usually the same as TokenLimitResetDay.");

        Section("City Life", "People outside your conversations: waving at you in the street, gossip about you, and the news.");
        NpcsGreetYou = Bind("NpcsGreetYou", true,
            "People you've talked to react when you pass: friends wave or call you over, acquaintances nod, people who can't stand you snub you. " +
            "Depends on how they feel about you and their mood, then a chance roll. A speech bubble shows what they say.");
        GreetChance = Bind("GreetChance", 35f,
            "Base percent chance someone who knows you reacts as you pass. Higher the more they like you and the better their mood.");
        GreetRange = Bind("GreetRange", 12f, "How close (metres) someone must be to notice you passing (they also need to see you: SightDistance, SightAngle).");
        SightDistance = Bind("SightDistance", 10f,
            "People only wave at you (or nod, or snub you) when they can see you: two lines this long (metres) go out from their " +
            "head the way they face, SightAngle apart, and you're seen inside the triangle they make. Straight ahead that reaches " +
            "SightDistance x cos(half the angle): 5 m with 10 m and 120. Type /sight on in the console to see the triangles.");
        SightAngle = Bind("SightAngle", 120f, "The angle (degrees) between those two lines: how wide people see. 120 = 60 to each side (at most 170).");
        GreetCooldownMinutes = Bind("GreetCooldownMinutes", 10f, "Real minutes before the same person reacts to you again.");
        AiGreetingLines = Bind("AiGreetingLines", true,
            "Their greeting is written by the AI in their own voice (one tiny request each, on BackgroundModel if set). Off = short stock lines, free.");
        Gossip = Bind("Gossip", true,
            "People talk. After a memorable conversation someone may tell others about you (rude, kind, flirty, generous...). " +
            "People in the same district may have heard it; it fades after a couple of weeks. No extra cost.");
        CityNews = Bind("CityNews", true, "People know the game's current news headlines and may bring them up.");
        MarketTalk = Bind("MarketTalk", true,
            "People notice the market's shortages and surpluses (a bread shortage pushing burger prices up...). Only some do, and fewer bring it up.");
        MarketKnowChance = Bind("MarketKnowChance", 40,
            "Percent of people who know about the current shortage or surplus. Food workers are twice as likely; nosy, practical types more, " +
            "dreamers less. The rest don't know, even if you ask.");
        MarketOnMindChance = Bind("MarketOnMindChance", 10,
            "Percent who have it on their mind and may bring it up themselves (\"Can you believe the price of bread today?\").");
        PlacesToEat = Bind("PlacesToEat", true,
            "People have real places in town they like to eat, with their own sense of the prices: some know the menu to the Lim, " +
            "some just think it's a bit pricey, some rarely eat out at all.");
        OverhearChance = Bind("OverhearChance", 15,
            "Percent chance that someone standing near residents who are talking (or on the phone) catches what's said, once per " +
            "conversation. They remember it and may pass it on later, more freely to someone they count as a friend, saying " +
            "where they heard it. Never anything about you. 0 = nobody overhears. No AI writes it; with MemorySearch = Voyage, what " +
            "they heard is indexed like any other memory.");
        OverhearDistance = Bind("OverhearDistance", 2.5f, "How close (metres) someone has to be to overhear.");

        Section("NPC Chatter", "Residents near you talk to each other, take phone calls and think out loud. Pick the mode first: Dynamic " +
                               "needs only the Dynamic settings below; Advanced uses the [Advanced Chatter] group instead. Each one " +
                               "is one AI request (plus a voice per line you can hear), so it's off by default and capped per hour. " +
                               "Change any of these in the game with the console (` key): /chatter shows them all.");
        Chatter = Bind("Chatter", true,
            "Turn residents' conversations, phone calls and people thinking out loud on. Each one is an AI request, and its lines are " +
            "voiced when voices are on; ChatterPerHour caps how many start, and TokenLimit caps all AI use. In the game: /chatter on, " +
            "/chatter off.");
        ChatterMode = Bind("ChatterMode", "Dynamic",
            "Dynamic = every DynamicEvery, one kind is picked at random (gossip between people near you, a phone call, someone thinking " +
            "out loud) and someone near you who fits it, people in front of you first; a group the game gathered gets the gossip when " +
            "there is one. Advanced = each kind on its own timer, chance and limit ([Advanced Chatter] below; only used in this mode).");
        DynamicEvery = Bind("DynamicEvery", "10s", "Dynamic mode: how often something new may start near you (5s, 30s, 2m...).");
        DynamicMaxNpcs = Bind("DynamicMaxNpcs", 3,
            "Dynamic mode: most people talking near you at once, 1 to 12 (a conversation of three counts three). A soft limit: when " +
            "you walk away from people talking, they wrap it up and no longer count, so new people can start right away.");
        DynamicLinesEach = Bind("DynamicLinesEach", 4,
            "Dynamic mode: most lines each person says in a conversation, 1 to 12. The last one is a goodbye.");
        DynamicPhoneLinesEach = Bind("DynamicPhoneLinesEach", 4,
            "Dynamic mode: most lines each side of a phone call says, 1 to 12. The caller's last one hangs up.");
        DynamicThinkLinesEach = Bind("DynamicThinkLinesEach", 2,
            "Dynamic mode: most lines someone mutters to themselves in one go, 1 to 4 (each just a few words).");
        DynamicStartsEach = Bind("DynamicStartsEach", 2,
            "Dynamic mode: each time (DynamicEvery), between 1 and this many new things may start, 1 to 6, as long as " +
            "DynamicMaxNpcs has room.");
        DynamicStagger = Bind("DynamicStagger", 4f,
            "Dynamic mode: when more than one starts at the same time, each waits a random moment, up to this many seconds " +
            "after the one before, so people don't all begin talking together. 0 = together.");
        DynamicGossipWeight = Bind("DynamicGossipWeight", 30,
            "Dynamic mode: how often each kind is picked, relative to the others (0 = never). This one: residents talking to each other.");
        DynamicShopWeight = Bind("DynamicShopWeight", 30,
            "Dynamic mode: staff and customers at food places (ordering, being asked how the food is, a little small talk). They're " +
            "looked for on their own every few seconds and don't take a place among the people talking near you: 0 = never, any " +
            "other number = on. Needs OrderTalk (AiOrderTalk has the AI write the ordering).");
        DynamicPhoneWeight = Bind("DynamicPhoneWeight", 20, "Dynamic mode: phone calls.");
        DynamicThinkWeight = Bind("DynamicThinkWeight", 20, "Dynamic mode: someone muttering to themselves.");
        DynamicTextWeight = Bind("DynamicTextWeight", 10,
            "Dynamic mode: a friend or partner who has your number texting you (Features > Phone, PeopleTextYou). They're wherever " +
            "they are, so it takes no place among the people talking near you; at most once every TextFirstHours, never at night.");
        ChatterPerHour = Bind("ChatterPerHour", 75,
            "Most conversations, phone calls and thoughts out loud started per real hour, all kinds together (orders the AI writes, " +
            "AiOrderTalk, count too, at most a third of it). This is the cap on how much AI they use.");
        UpgradeDefault(ChatterPerHour, "12");
        ChatterRange = Bind("ChatterRange", 18.25f,
            "The chatter ring: only people within this many metres of you start one (the nearest are picked first); once you're 3 m " +
            "past it (or past the voices ring, whichever is farther) they wrap it up. By default just past the voices ring. " +
            "In the game: /rings shows the three rings at your feet and sets them.");
        UpgradeDefault(ChatterRange, "25");
        UpgradeDefault(ChatterRange, "12"); // FB-38: the chatter ring moved out past the voices ring
        VoiceDistance = Bind("VoiceDistance", 15f,
            "The hearing ring: how far you hear them, full volume close by, fading smoothly with distance to silence at this many " +
            "metres. Nothing is sent to the voice provider for people you couldn't hear anyway (see VoiceBeyond).");
        VoiceBeyond = Bind("VoiceBeyond", 3f,
            "The voices ring is this many metres past the hearing ring: lines are voiced up to it, starting silent, so if you walk " +
            "closer mid-line you begin hearing them, fading in. Past it no voice is made. A few metres past the chatter ring " +
            "(ChatterRange) they wrap it up: the next one to speak gives a reason they need to go, the others say goodbye. " +
            "0 = only within VoiceDistance.");
        TextDistance = Bind("TextDistance", 5f,
            "Their words show over their heads only while you're within this many metres. 0 = any distance.");
        OrderTalk = Bind("OrderTalk", true,
            "At other places that serve food, staff and customers talk: the waiter takes someone's order at their table (or they " +
            "order at the counter), saying what they really ordered once they have; staff bring the meal; now and then staff ask " +
            "someone eating how it is, or make a little small talk. Short spoken exchanges. Not at your own places.");
        AiOrderTalk = Bind("AiOrderTalk", true,
            "Taking an order is written by the AI, in the staff member's and the customer's own words, shaped by their moods and how " +
            "they feel about each other (not ready yet, then ready, politely or not; \"okay then\" if they walk off, and the staff " +
            "remember it). One small request each time, only for a customer you've met or seen talking, only near you; counted in " +
            "ChatterPerHour (at most a third of it) and your token limit. Bringing the meal and check-ons stay short written lines. " +
            "Off = written lines only, free.");
        JustSittingTellsWaiter = Bind("JustSittingTellsWaiter", true,
            "Someone who came into a food place only to sit for a while (the game has them not eating) tells the staff so when they " +
            "come to take an order (\"just sitting a while, thanks\"), and the staff go on with their work instead of waiting at the " +
            "table. Off = as the game has it: the staff wait there, and the customer says they're not ready yet.");
        TableCallChance = Bind("TableCallChance", 20,
            "Percent of conversations at a table where someone's phone goes off partway through. Half the time they answer (\"one " +
            "moment\") and talk for a bit while the others wait or ask who it was; otherwise they tell the table they'll call back. When " +
            "everyone at the table knows the caller, it may go on speaker, and the caller is heard through the phone. Part of the " +
            "same conversation: no extra AI request. 0 = never.");
        ReplaceGameChatter = Bind("ReplaceGameChatter", true,
            "The game has its own background chatter: it puts people together at talk spots and plays short written lines in plain text " +
            "plates, without voices. On = while this mod's chatter is on, the game's lines are silenced and the people it gathers get this " +
            "mod's conversations first (voices, gestures, memory). Off = both run.");

        Section("Advanced Chatter", "Only used when ChatterMode = Advanced. Three kinds, each with its own timer (how often it's rolled: " +
                                    "30s, 2m, 1h), chance, line limit and how many can go on at once; all share ChatterPerHour.");
        ChatterChance = BindRenamed("GroupChance", 100, "Conversations between residents (two or more): percent chance each roll.", "ChatterChance", "35");
        GroupEvery = BindRenamed("GroupEvery", "30s", "How often a conversation is rolled for: 30s, 2m, 1h...", "ChatterEverySeconds", "45");
        ChatterMaxGroups = BindRenamed("GroupMaxActive", 3, "Conversations going on at once (a new one starts only when there's room).", "ChatterMaxGroups", "1");
        ChatterLinesEach = BindRenamed("GroupLinesEach", 4, "Most lines each person says, 1 to 12. They end with a goodbye.", "ChatterLinesEach", "3");
        ChatterGroupSize = BindRenamed("GroupSize", 3, "Biggest group, 2 to 4. Every conversation starts with two; others may join.", "ChatterGroupSize", "3");
        ChatterJoinChance = BindRenamed("GroupJoinChance", 33, "Percent chance, for each free place in the group, that someone joins partway through.", "ChatterJoinChance", "33");
        ChatterKnownPairs = BindRenamed("GroupKnownPairs", 50,
            "Percent of conversations between two people who've talked before (friends talk more often, people who don't get on rarely).", "ChatterKnownPairs", "50");
        ChatterTables = BindRenamed("GroupTables", 65, "Not used since 0.3.2: people sharing a table always talk as their table, and a table near you goes first.", "ChatterTables", "65");
        ChatterWalkUp = BindRenamed("GroupWalkUp", true, "One person walks over to the other (a few metres at most). Off = only people already close together talk.", "ChatterWalkUp", "true");

        PhoneChance = Bind("PhoneChance", 100,
            "Phone calls: percent chance each roll that someone on their own is on the phone with someone they know: another " +
            "resident they've talked with, or someone from their own life. You hear their side (both, if the other one's near). Both remember it.");
        UpgradeDefault(PhoneChance, "10");
        PhoneEvery = Bind("PhoneEvery", "60s", "How often a phone call is rolled for: 30s, 2m, 1h...");
        PhoneMaxActive = Bind("PhoneMaxActive", 2, "Phone calls going on at once.");
        PhoneLinesEach = Bind("PhoneLinesEach", 4, "Most lines each side of a call says, 1 to 12. The caller's last one hangs up.");

        MumbleChance = BindRenamed("ThinkChance", 100,
            "Thinking out loud: percent chance each roll that someone near you who's on their own says what's on their mind. They remember it.", "MumbleChance", "15");
        ThinkEvery = Bind("ThinkEvery", "40s", "How often thinking out loud is rolled for: 30s, 2m, 1h...");
        ThinkMaxActive = Bind("ThinkMaxActive", 3, "People thinking out loud at once.");
        ThinkLinesEach = Bind("ThinkLinesEach", 2, "Most lines someone mutters to themselves in one go, 1 to 4 (each just a few words).");

        Section("Food Orders", "People who know you run a place can ask you to cook for them, and pay you for it.");
        Favours = Bind("FoodOrders", true,
            "They can ask for a dish from your menu. Carry the plated dish to them, open a chat and click GIVE: they pay the menu price. " +
            "Food they didn't ask for is a gift.", "Favours");
        FriendDiscountPercent = Bind("FriendDiscountPercent", 15, "Discount friends get, in percent. 0 = none.");
        FriendDiscountAt = Bind("FriendDiscountAt", 70f,
            "Where on the relationship bar someone must be to get the discount, 0 to 100 (50 = neutral; 70 = friends).");
        // The older setting (-100..100) carries over in the bar's units, once only: a FriendDiscountAt deleted later is the
        // default again. It comes from [Food Orders], or from [Social] (the oldest layout) while [Food Orders] isn't in the
        // file (review of 919404e).
        if (_onDisk.TryGetValue("FriendDiscountAffinity", out var oldDiscount))
        {
            bool hasNew = _onDisk.TryGetValue("FriendDiscountAt", out var discountHere) && discountHere.Any(h => h.section == "Food Orders");
            var old = oldDiscount.LastOrDefault(h => h.section == "Food Orders");
            if (old.section == null && !_sectionsOnDisk.Contains("Food Orders")) old = oldDiscount.LastOrDefault(h => h.section == "Social");
            if (!hasNew && old.section != null
                && int.TryParse(old.value.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int oldAffinity)
                && _upgrades.Once("Food Orders.FriendDiscountAt=from FriendDiscountAffinity"))
                FriendDiscountAt.Value = (float)Npc.Relationships.FromAffinity(Math.Clamp(oldAffinity, -100, 100));
            Log.Info("The settings file still has FriendDiscountAffinity, which is no longer read: FriendDiscountAt (0 to 100 on the relationship bar) " +
                     "took its place. You can delete the old line.");
        }
        DeliveryFee = Bind("DeliveryFee", 5,
            "Lims added when you bring the food to them anywhere other than your own place. 0 = free delivery.");

        Section("Conversations", "How people behave while you're talking to them.");
        WorldChatRange = Bind("WorldChatRange", 4f,
            "World chat: how far in front of you (in your view) people can be talked to, in metres. Anyone within a metre, even behind you, overhears.");
        // FB-81: moved here from [Controls] (values carried over once).
        MaxTalkDistance = Bind("MaxTalkDistance", 4.5f, "How close you need to be to start a conversation (TalkKey), in metres.", "Controls/MaxTalkDistance");
        PauseTimeWhileChatting = Bind("PauseTimeWhileChatting", true,
            "Stop the in-game clock while a chat is open (and while you type in world chat), like the game's own dialogues.", "Controls/PauseTimeWhileChatting");
        MinutesPerMessage = Bind("MinutesPerMessage", 0,
            "Time passes as you talk: each line they say (in a chat, a group or world chat) moves the game's clock on by this many minutes, so a long talk or a date " +
            "takes its share of the evening (like Disco Elysium). 0 = off: with PauseTimeWhileChatting on, the clock stays still while you talk.",
            "Controls/MinutesPerMessage");
        OpeningLine = Bind("OpeningLine", "Hey,",
            "What you say automatically as you walk up to someone, and they answer it. Empty = you type the first line yourself. " +
            "Someone who just waved you over speaks first instead.");
        // Older files: NpcGreetsFirst = false meant "nobody says anything until I type" — that's an empty OpeningLine now.
        if (!_onDisk.ContainsKey("OpeningLine") && _onDisk.TryGetValue("NpcGreetsFirst", out var greet)
            && greet.Any(g => g.value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)))
            OpeningLine.Value = "";
        NpcsCanEndConversations = Bind("NpcsCanEndConversations", true,
            "People end conversations like people do: out of time, fed up with you, or simply done. " +
            "How long they'll talk depends on their temperament, mood, what they were doing and how much they like you.");
        Patience = Bind("Patience", 1f, "How long everyone is willing to talk: 0.5 = everyone's in a rush, 2 = everyone has time to kill.");
        IdleCheckSeconds = Bind("IdleCheckSeconds", 60,
            "Stand there this many seconds without typing and they react (\"Hello? You okay?\"). 0 = never.");
        IdleLeaveSeconds = Bind("IdleLeaveSeconds", 30, "After that, this many more silent seconds and they leave with a parting line.");
        AnimateNpc = Bind("AnimateNpc", true, "Talking, listening and mood animations and lip-sync while chatting.");

        Section("People", "Who you can chat with, and who lives in the city.");
        Friendliness = Bind("Friendliness", 50,
            "How open the city is, 0 to 100. At 50 it's a fair coin: about half the people you meet are in an open mood " +
            "(cheerful, curious, playful...) and half guarded (busy, tired, wary...), with each person's temperament, the time of day, " +
            "the weather and how they feel about you tipping it. Lower = a colder city, higher = a warmer one. " +
            "It also decides how readily people give a stranger their name.");
        ChatWithNamedNpcs = Bind("ChatWithNamedNpcs", true,
            "Story characters (Banor, Lois Gyo...) can be AI-chatted with too, under their real name. " +
            "Their written dialogue still uses the game's own talk key, which does nothing while an AI chat is open.");
        KeepGameAssignedNames = Bind("KeepGameAssignedNames", false,
            "Use the name the game already gave someone (e.g. hireable staff) instead of an AI-made one.");
        HideUnmetCustomerNames = Bind("HideUnmetCustomerNames", true,
            "The name over a customer at your place, and in the customer pop-up, stays blank until you know it: they told you, or " +
            "the game has you met. Story characters and shop people keep their names. Off = the game shows every name.");
        MinAge = Bind("MinAge", 18,
            "Youngest a newly met person can be. Nobody under 18 can be chatted with: set this lower and anyone younger just tells you " +
            "they can't talk. Children in the game's story are always treated that way.");
        MaxAge = Bind("MaxAge", 60, "Oldest a newly met person can be. Story characters keep their real age from the game.");
        TransChance = Bind("TransChance", 1.3f,
            "Percent of people who are trans (1.3 = Gallup 2024, US adults). Not visible: they show as Man/Woman until they tell you.");
        NonBinaryChance = Bind("NonBinaryChance", 1.0f, "Percent who are non-binary, genderfluid or agender (1.0 = Pew 2022, US adults).");
        QueerChance = Bind("QueerChance", 8.0f, "Percent who aren't straight: gay, lesbian, bi, pan, ace... (8 = Gallup 2024-25, US adults).");
        AgeShapesIdentity = Bind("AgeShapesIdentity", true,
            "Younger people are likelier to be trans, non-binary or queer, as in the surveys; overall rates still match the numbers above.");
        LookAtNewPeople = Bind("LookAtNewPeople", true,
            "The first time you talk to someone new, take one quick picture of them (just them, not your screen's menus) and show it to the AI, " +
            "so who they are fits how they look and what they're wearing. Without it, people are written from the game's own description of " +
            "each character model. Needs a model that can see pictures: if yours can't, the chat and the console (` key) say so and " +
            "they're written without it. Once per person; that one request costs a little more.");
        RememberDoings = Bind("RememberDoings", true,
            "People you've met remember what the game saw them doing near you, not only what they said: a meal at a food place " +
            "(and what they had), a shift at work. They may bring it up when you chat. No AI is asked; not at your own places.");

        Section("Relationships", "How each person feels about you, on a bar from 0 to 100: 50 is neutral, where everyone starts; 0 they " +
                "can't stand you, 100 they adore you. It moves a little with each reply (their own judgement of what you said), so it takes " +
                "time to really get to know someone. Residents have one with each other too, moving the same way.");
        ShowRelationshipBar = Bind("ShowRelationshipBar", true,
            "Show it in the chat window: a frowning face on the left, ten cells from red through white to green, a smiling face on the right, " +
            "and a line where they stand.");
        RelationshipGainMax = Bind("RelationshipGainMax", 0.5f, "The most a reply they liked moves it up, 0 to 5 (decimals with a dot: 0.5).");
        RelationshipLossMin = Bind("RelationshipLossMin", 0.1f, "How far a reply they didn't like moves it down at the least, 0 to 5 (with a dot).");
        RelationshipLossMax = Bind("RelationshipLossMax", 0.5f, "...and at the most, 0 to 5 (with a dot).");
        WaveAt = Bind("WaveAt", 60f, "From here they wave when you pass (a new friend). 0 to 100.");
        WaveOverAt = Bind("WaveOverAt", 70f, "From here they wave and call you over. 0 to 100.");
        FriendAt = Bind("FriendAt", 70f, "From here (and three meetings) they count you as a friend. 0 to 100.");
        SnubAt = Bind("SnubAt", 37.5f, "At or below this they snub you when you pass. 0 to 100 (decimals with a dot: 37.5).");
        FoodGiftBonus = Bind("FoodGiftBonus", 1.5f, "How far it moves up when you give them food they didn't ask for (with a dot: 1.5).");
        FoodFavourBonus = Bind("FoodFavourBonus", 1f, "How far it moves up when you bring them food they asked for.");

        // ------------------------------------------------------------------ rarely changed

        Section("Memory", "What people remember about you, and how their lives move on between meetings.");
        MemoriesPerSave = Bind("MemoriesPerSave", true,
            "Everyone's memory of you belongs to the game save you're playing: it's kept when you save (or the game autosaves), " +
            "and loading a save puts back the city as it was then, so what was said since that save is gone. Saving over the " +
            "same save keeps everything. A new game starts with nobody knowing you; a save from before this was added starts " +
            "with everyone you'd met. Off = one memory for every save, as before. Kept in BepInEx\\config\\NivalisAIChat\\saves.");
        HistoryLimit = Bind("HistoryLimit", 0,
            "Lines of conversation kept per person. 0 = everything, forever: nothing is lost, and old moments come back when relevant.");
        MeetingSummaries = Bind("MeetingSummaries", true,
            "After each conversation they write a short diary entry about it, so they remember every meeting. One small background request each.");
        LifeUpdates = Bind("LifeUpdates", true,
            "Between meetings their lives move on: new jobs, events, progress on goals. One extra request when it happens.");
        LifeUpdateAfterGameDays = Bind("LifeUpdateAfterGameDays", 2, "In-game days since you last talked before their life moves on.");
        LifeUpdateAfterRealHours = Bind("LifeUpdateAfterRealHours", 24f, "Real hours that also count as enough time (0 = only in-game days count).");
        RecentMessagesSent = Bind("RecentMessagesSent", 40,
            "Recent lines sent to the AI word for word; older ones live on in their memory summary and come back when relevant. More " +
            "lines means they remember more of your talk word for word, and every reply costs more.");
        SummarizeAfterMessages = Bind("SummarizeAfterMessages", 60,
            "Once this many lines pile up, the oldest are folded into their memory summary.");
        RecallFades = Bind("RecallFades", true,
            "Older memories come back less clearly and less often, by in-game days: from a few days ago, clearly; a couple of weeks, " +
            "the gist; a month or more, only vaguely or not at all. When they half remember, they say so the way people do " +
            "(\"do you mean when we...?\", \"yeah, I remember\").");
        MemorySearch = Bind("MemorySearch", "Voyage",
            "How people find old memories when something brings them up. Voyage = by meaning, across everything they remember " +
            "(you, other people, their own life): each memory is sent to Voyage AI once to be indexed, and the index is kept and " +
            "searched on your PC. It's used only once you've set VoyageKey; until then, and with Local, it's by matching words and " +
            "nothing leaves your PC. Your memories stay in your files either way; switching to Voyage indexes everyone's existing " +
            "memories in the background.");
        VoyageKey = Bind("VoyageKey", "", "Your Voyage AI key (dashboard.voyageai.com), for MemorySearch = Voyage. The first 200 million tokens are free.");
        VoyageModel = Bind("VoyageModel", "voyage-4-lite", "The Voyage model memories are indexed with. Changing it indexes them again.");
        MemoryIndexLimit = Bind("MemoryIndexLimit", 0,
            "The most memories kept for each person in their Voyage index (MemorySearch = Voyage). With more, the oldest go first. " +
            "0 = no limit: everything they remember can come back by meaning. Their memories stay in their files either way.");

        Section("Speech Bubbles", "How the words over people's heads look (residents talking, phone calls, thoughts, greetings). " +
                                  "Colours as #RRGGBB; opacity 0 (invisible) to 100 (solid).");
        BubbleBackground = Bind("BubbleBackground", "#24211E", "Background colour.");
        BubbleFocus = Bind("BubbleFocus", true,
            "Bubbles fade in and out with distance and with where you're looking: the person you face is clear, people off to " +
            "the side are fainter, and when bubbles overlap the nearest stays clear on top. Off = every bubble fully shown.");
        BubbleBackgroundOpacity = Bind("BubbleBackgroundOpacity", 93, "Background opacity, 0 to 100.");
        BubbleBorder = Bind("BubbleBorder", "#C4A062", "Border colour (also the phone icon).");
        BubbleBorderOpacity = Bind("BubbleBorderOpacity", 100, "Border opacity, 0 to 100. 0 = no border.");
        BubbleText = Bind("BubbleText", "#E9E2D2", "Colour of their words.");
        BubbleName = Bind("BubbleName", "#D9A55A", "Colour of the name over their words (shown once you know it).");
        BubbleTextSize = Bind("BubbleTextSize", 100, "Text size, percent (50 to 200).");

        Section("Interface", "How the chat window looks.");
        SmoothTextFade = Bind("SmoothTextFade", true,
            "Their words fade in letter by letter, smoothly. Off = letters pop in one at a time. " +
            "With voices on, the text starts as they start talking and finishes as they finish.");
        TextSpeed = Bind("TextSpeed", NivalisAIChat.UI.TextSpeed.Medium,
            "How fast their words appear when there's no voice to follow (spoken lines always keep pace with the voice).");
        UiScale = Bind("UiScale", 1f, "Size of the chat window (0.6 to 2).");
        ChatWindowLayout = Bind("ChatWindowLayout", "",
            "Where you moved the chat window and its size: drag its top strip to move it, its corner to resize it (600x320 to " +
            "1400x760). Kept here as you do. Empty = its usual place and size (or double-click the corner, or /chatwindow reset).");
        ShowTalkPrompt = Bind("ShowTalkPrompt", true, "Show '[T] Talk' under the crosshair when someone can be talked to.");
        ShowMoodHint = Bind("ShowMoodHint", true, "Show how they seem (e.g. 'seems irritated') next to their name.");
        HistoryShown = Bind("HistoryShown", 150,
            "Past lines shown (and scrollable) when you talk to someone again. 0 = only this conversation.");

        AutoReloadSettings = Bind("AutoReloadSettings", true,
            "When this file is saved while the game runs (in a text editor), the mod reads it again within a couple of seconds, so " +
            "your changes take effect right away. /reload in the console does the same any time.");

        Section("AI Fine Tuning", "Advanced. The defaults work for almost everyone.");
        ReplyLanguage = Bind("ReplyLanguage", "",
            "The language people talk to you in (English, Français, Deutsch, 日本語...). Empty = whatever language you write in.");
        StructuredReplies = Bind("StructuredReplies", true,
            "Replies come back as JSON (their line plus mood and feelings), which keeps moods reacting to the conversation. " +
            "Turn off only if your server can't do JSON and replies look broken.");
        Effort = Bind("Effort", "low", "Claude only: low, medium, high, xhigh or max. 'low' keeps replies quick and cheap; empty = model default.");
        ReasoningEffort = Bind("ReasoningEffort", "",
            "OpenAI-format only: how much the model thinks before it answers: none, minimal, low, medium, high or xhigh (which ones a " +
            "model takes is up to its provider). Thinking makes replies slower and costs more, and MaxReplyTokens caps it with the " +
            "reply: set high, it can use up a short line's tokens (greetings, a quick word) before it answers. Empty = the model's own " +
            "default (OpenAI's GPT-5.4 models: none, no thinking at all).");
        Temperature = Bind("Temperature", 0.9f,
            "OpenAI-format only: randomness. -1 = don't send it (some reasoning models reject it). Never sent to Claude.");
        PromptCaching = Bind("PromptCaching", true, "Claude only: cache the unchanging part of each prompt so repeat turns cost less.");
        RefusalFallback = Bind("RefusalFallback", true,
            "Claude only: if a model's safety check declines a request, let Anthropic re-run it on their recommended fallback model.");
        MaxReplyTokens = Bind("MaxReplyTokens", 4000,
            "The per-response limit: the most one reply may use, thinking included. Replies are short; this mostly stops runaways. " +
            "For a limit per hour, day or month, see TokenLimit at the top.");
        MaxGenerationTokens = Bind("MaxGenerationTokens", 8000,
            "The same for each job in the background: creating someone, a life update, a memory summary.");
        TimeoutSeconds = Bind("TimeoutSeconds", 90, "Seconds to wait for the provider before giving up on one request.");
        GiveUpAfterSeconds = Bind("GiveUpAfterSeconds", 0,
            "Longest you wait for an answer in a chat, retries included. After that it stops waiting, says so, and puts your words back " +
            "in the box (\"Esc to step away\" shows after 15 seconds anyway). 0 = automatic: TimeoutSeconds plus 15. -1 = no limit.");
        ExtraHeaders = Bind("ExtraHeaders", "",
            "Extra HTTP headers as 'Name: value' pairs separated by ';'. OpenRouter example: HTTP-Referer: https://localhost; X-Title: People of Nivalis");

        Section("Debug", "For troubleshooting and research. Leave these off for normal play.");
        LogPrompts = Bind("LogPrompts", false, "Write every request and response to BepInEx\\LogOutput.log (your key is never logged).");
        DevCommands = Bind("DevCommands", false, "Developer test channel (dev_commands.txt). Leave off.");
        KeepTranscripts = Bind("KeepTranscripts", false,
            "Keep a transcript of everything said near you (your chats, residents talking, calls, mutters, order talk, greetings), with " +
            "what shaped each line: mood, tone of voice, gesture, how they feel about each other, the voice and what wrote it. One file " +
            "per session in BepInEx\\config\\NivalisAIChat\\transcripts (the newest 30 kept; /resetall doesn't remove them). It holds " +
            "what you typed, never your keys. /transcripts export makes a readable copy for research.");

        _secrets.UnionWith(new ConfigEntryBase[] { ApiKey, VoyageKey, ExtraHeaders, BackgroundApiKey, SpeechToTextKey, ImageApiKey });
        // What changed here, at the start (an old default moved to a new one, a value BepInEx couldn't read), is written
        // into its line; settings missing from the file are added. Nothing else in the file changes.
        foreach (var e in _sections.SelectMany(s => s.entries))
        {
            _known[e] = e.BoxedValue;
            var here = _onDisk.TryGetValue(e.Definition.Key, out var all) ? all.Where(h => h.section == e.Definition.Section).ToList() : null;
            if (here == null || here.Count == 0) continue;
            object? onFile;
            try { onFile = TomlTypeConverter.ConvertToValue(here[^1].value, e.SettingType); } catch { onFile = null; }
            if (!Equals(onFile, e.BoxedValue)) { _pending[e] = e.BoxedValue; _known[e] = onFile; }
        }
        bool written = WriteNow(); // the file was just read
        if (!_holdWrites && written) _upgrades.Save(); // an upgrade counts as done only once it's in the file
        if (written) RefreshExplanations(cfg.ConfigFilePath);
        if (DataFile.ReadText(cfg.ConfigFilePath, tries: 1) is { outcome: DataFile.Outcome.Read, text: { } now })
        {
            NotUnderstood(now);
            // An older layout's groups stay in the file (rewriting it whole could lose a line), but they aren't read.
            if (OlderLayout(now))
                Tell("[CFG-READ] The settings file still has groups from an older version of the mod ([API], [Behaviour], [Voice Provider 1]…): " +
                     "their values were moved to the new groups, and the old ones aren't read any more. You can delete them.");
        }
        cfg.SettingChanged += (_, e) => SaveChange(e.ChangedSetting);
    }

    /// <summary>
    /// Where each group's settings lived in older layouts of the file. Values are only carried over along these
    /// routes, so a same-named setting in another group (the voice providers' ApiKey and Model) never picks one up.
    /// </summary>
    private static readonly Dictionary<string, string[]> LegacySections = new(StringComparer.Ordinal)
    {
        ["AI Connection"] = new[] { "API" },
        ["AI Fine Tuning"] = new[] { "API" },
        ["Keybinds"] = new[] { "Controls", "Behaviour", "Voice Input" }, // FB-81: [Controls] and the voice input keys, carried over
        ["You"] = new[] { "Player" },
        ["City Life"] = new[] { "Social" },
        ["Food Orders"] = new[] { "Social" },
        ["Conversations"] = new[] { "Behaviour" },
        ["People"] = new[] { "Behaviour", "Population" },
        ["Memory"] = new[] { "Behaviour" },
        ["Interface"] = new[] { "Behaviour" },
        ["NPC Chatter"] = new[] { "NPC Chatter" }, // renamed within the group (ChatterChance → GroupChance...)
        ["Advanced Chatter"] = new[] { "NPC Chatter" }, // the per-kind settings moved out when Dynamic mode came
    };

    // ------------------------------------------------------------------ binding

    private void Section(string name, string intro) => _sections.Add((name, intro, new List<ConfigEntryBase>()));

    /// <summary>
    /// A setting that used to have another name in the same group: a value you'd changed carries over; one that was just
    /// the old default gets the new default.
    /// </summary>
    private ConfigEntry<T> BindRenamed<T>(string key, T defaultValue, string description, string oldKey, string oldDefault)
    {
        var entry = Bind(key, defaultValue, description, oldKey);
        if (!_onDisk.ContainsKey(key) && _onDisk.TryGetValue(oldKey, out var old)
            && old.Any(o => string.Equals(o.value.Trim(), oldDefault, StringComparison.OrdinalIgnoreCase)))
            entry.Value = defaultValue;
        return entry;
    }

    /// <summary>
    /// A setting still at an old default moves to the new one, once ever (see <see cref="SettingsUpgrades"/>): a player
    /// who sets the old value again afterwards keeps it.
    /// </summary>
    private void UpgradeDefault<T>(ConfigEntry<T> entry, string oldDefault)
    {
        if (_holdWrites) return; // the file wasn't read: what it holds is unknown, so nothing is decided (or remembered as decided)
        string section = entry.Definition.Section, key = entry.Definition.Key;
        string? onDisk = _onDisk.TryGetValue(key, out var here) ? here.Where(h => h.section == section).Select(h => h.value).FirstOrDefault() : null;
        if (_upgrades.Decide(section, key, oldDefault, onDisk)) entry.Value = (T)entry.DefaultValue;
    }

    private ConfigEntry<T> Bind<T>(string key, T defaultValue, string description, params string[] oldKeys) =>
        Bind(key, defaultValue, description, NotInOtherMenus, oldKeys);

    private ConfigEntry<T> Bind<T>(string key, T defaultValue, string description, ConfigurationManagerAttributes menus, params string[] oldKeys)
    {
        string section = _sections[^1].name;
        var entry = _file.Bind(section, key, defaultValue, new ConfigDescription(description, null, menus));
        _sections[^1].entries.Add(entry);
        _oldKeys[entry] = oldKeys;
        // Not in this section on disk yet: carry the value over from wherever an older layout kept it.
        // BepInEx has already read this file; only values it couldn't place need carrying over.
        bool inPlace = _onDisk.TryGetValue(key, out var here) && here.Any(h => h.section == section);
        if (!inPlace)
            foreach (var k in oldKeys.Prepend(key))
            {
                int slash = k.IndexOf('/');
                if (slash > 0)
                {
                    // FB-81: "Section/Key", where this setting was before it moved into a group the file already has.
                    if (_onDisk.TryGetValue(k[(slash + 1)..], out var moved) && moved.FirstOrDefault(o => o.section == k[..slash]) is var m && m.section != null
                        && _upgrades.Once($"{section}.{key}=from {k}")) // review F81-2: once
                    {
                        try { entry.SetSerializedValue(m.value); } catch { }
                        break;
                    }
                    continue;
                }
                if (!_onDisk.TryGetValue(k, out var old)) continue;
                // only from the group's own older names — never another current group's same-named key. Another group's
                // only while this setting's own group isn't in the file (a file never upgraded): once it is, the older
                // groups the file still has are left alone, so a key the player took out stays out (review of a0fa75f).
                (string section, string value) found = default;
                if (LegacySections.TryGetValue(section, out var routes))
                    found = old.FirstOrDefault(o => routes.Contains(o.section) && (o.section == section || !_sectionsOnDisk.Contains(section)));
                if (found.section == null) continue;
                try { entry.SetSerializedValue(found.value); } catch { }
                break;
            }
        return entry;
    }

    // ------------------------------------------------------------------ the voice Providers block

    private void WriteProviderBlock(StringBuilder sb)
    {
        sb.AppendLine("# Where voices come from: one provider per line, as (endpoint, api key, chance, voices).");
        sb.AppendLine("#   TO USE A PROVIDER: delete the # at the start of its line and replace your-...-key with your own key.");
        sb.AppendLine("#   To stop using one, put a # back at the start of its line. Use as many as you like at once.");
        sb.AppendLine("#   chance: how often a newly met person gets that provider, compared with the others (50/50, 70/30...).");
        sb.AppendLine("#   voices: auto = all of the provider's voices, or a list like nova:female, onyx:male. \"model: name\" picks a model.");
        sb.AppendLine("#   OpenAI with an empty key uses your chat key.");
        sb.AppendLine("Providers = [");
        foreach (var l in _providerLines) sb.AppendLine(l);
        sb.AppendLine("]");
        sb.AppendLine();
    }

    /// <summary>
    /// The voice Providers block in the file (see <see cref="SettingsFileText.ProviderBlock"/>); <paramref name="unreadable"/>
    /// when it's there but couldn't be read. At the start a block that was never closed is still used: what's in it beats
    /// the default list (a reload doesn't use one over the list it already has).
    /// </summary>
    private static List<string>? ReadProviderBlock(string path, out bool unreadable, out bool unclosed)
    {
        unreadable = unclosed = false;
        var (outcome, text, error) = DataFile.ReadText(path);
        if (outcome == DataFile.Outcome.Missing) return null;
        if (outcome == DataFile.Outcome.Unreadable) { unreadable = true; Log.Warn("Reading the voice providers failed: " + error); return null; }
        var block = SettingsFileText.ProviderBlock(text!, out bool closed);
        unclosed = block != null && !closed;
        return block;
    }

    /// <summary>Settings from the short-lived "Voice Provider 1-4" groups, turned into block lines.</summary>
    private List<string>? MigratedProviderLines()
    {
        string V(int n, string key) =>
            _onDisk.TryGetValue(key, out var all) ? all.FirstOrDefault(x => x.section == $"Voice Provider {n}").value ?? "" : "";
        bool found = false;
        var lines = new List<string>();
        for (int n = 1; n <= 4; n++)
        {
            string prov = V(n, "Provider");
            if (prov.Length == 0) continue;
            found = true;
            if (!Enum.TryParse<VoiceProvider>(prov, true, out var p) || p == VoiceProvider.None) continue;
            double.TryParse(V(n, "Chance"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double chance);
            string key = V(n, "ApiKey"), voices = V(n, "Voices"), model = V(n, "Model"), url = V(n, "Url");
            if (chance <= 0 && key.Length == 0 && voices.Length == 0) continue; // an untouched example
            lines.Add("    " + VoiceProviderList.Format(p, url, key, chance, voices, model));
        }
        if (!found) return null;
        lines.AddRange(VoiceProviderList.DefaultLines().Where(l => l.TrimStart().StartsWith("#")));
        return lines;
    }

    /// <summary>Every "Key = value" in the file as it is on disk, by key, with the section it sits in; every group into <paramref name="sections"/>.</summary>
    private static Dictionary<string, List<(string section, string value)>> ReadRaw(string path, out bool unreadable, HashSet<string> sections)
    {
        var map = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
        unreadable = false;
        try
        {
            var (outcome, text, error) = DataFile.ReadText(path);
            if (outcome == DataFile.Outcome.Missing) return map;
            if (outcome == DataFile.Outcome.Unreadable) { unreadable = true; Log.Warn("Reading the settings file for migration failed: " + error); return map; }
            string section = "";
            foreach (var (raw, _) in SettingsFileText.Lines(text!)) // split where BepInEx splits: a lone CR ends a line too
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]")) { section = line[1..^1].Trim(); sections.Add(section); continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim();
                if (!map.TryGetValue(key, out var list)) map[key] = list = new List<(string, string)>();
                list.Add((section, line[(eq + 1)..].Trim()));
            }
        }
        catch (Exception e) { Log.Warn("Reading the settings file for migration failed: " + e.Message); }
        return map;
    }

    // ------------------------------------------------------------------ writing the file

    private const int Width = 100;
    private static readonly string Rule = "# " + new string('=', Width - 2);

    /// <summary>
    /// The settings file was changed outside the game since the mod last wrote or read it (and has settled for a moment).
    /// A deleted file isn't a change: its defaults apply at the next start (its "time" used to read as a change every 2 s).
    /// </summary>
    public bool ChangedOnDisk()
    {
        try
        {
            if (!File.Exists(_file.ConfigFilePath)) return false;
            var t = File.GetLastWriteTimeUtc(_file.ConfigFilePath);
            return t != _seenWrite && (DateTime.UtcNow - t).TotalSeconds > 0.75; // let an editor finish saving
        }
        catch { return false; }
    }

    /// <summary>
    /// The file as it is now counts as seen: auto-reload doesn't try it again until it changes again. It doesn't count as
    /// read: the mod still reads it before writing anything (auto-reload's give-up on a held file; review of a0fa75f).
    /// </summary>
    public void MarkSeen()
    {
        try { _seenWrite = File.GetLastWriteTimeUtc(_file.ConfigFilePath); } catch { }
    }

    /// <summary>The file as it was at <paramref name="stamp"/> has been read (or written) by the mod: seen, and read.</summary>
    private void MarkRead(DateTime stamp) => _knownWrite = _seenWrite = stamp;

    /// <summary>When auto-reload last saw the file (see <see cref="ChangedOnDisk"/>).</summary>
    private DateTime _seenWrite;

    /// <summary>
    /// Reads the settings file again, so changes made outside the game take effect now (in the game: /reload; automatic
    /// with AutoReloadSettings). Returns the names of the settings that changed ("Providers" for the voice list). If the
    /// file couldn't be read, <paramref name="error"/> says why (and which line), and nothing from it applies (the file
    /// isn't touched either). A file with a bad line is marked seen (auto-reload doesn't try it again until it changes);
    /// one held by another program, at any point of the reload, isn't (<see cref="ReloadBusy"/>), so it's read again
    /// once it's free (audit step 5, #12, and its reviews). In-game changes not yet in the file go back on top, unless
    /// the file changed that same setting meanwhile.
    /// </summary>
    public List<string> Reload(out string? error)
    {
        error = null;
        ReloadBusy = false;
        lock (Sync) // requests wait the few milliseconds this takes, then see all of the new settings at once (RACE-8)
        {
            var changed = new List<string>();
            string path = _file.ConfigFilePath;
            // Its time is taken before it's read: a save landing in between then reads as a change, not as read (S1).
            DateTime stamp = default;
            try { stamp = File.GetLastWriteTimeUtc(path); } catch { }
            // One try and no waiting: this runs on the game's main thread, inside the lock requests wait on.
            var (outcome, text, readError) = DataFile.ReadText(path, tries: 1);
            if (outcome == DataFile.Outcome.Missing) { error = "the file isn't there"; return changed; }
            if (outcome == DataFile.Outcome.Unreadable) { Busy(readError, out error); return changed; }
            bool seen = true;
            try
            {
                // A voice provider line BepInEx can't read is fixed in the file first, or its own reload throws (ROBUST-12).
                var notes = new List<string>();
                string fixedText = SettingsFileText.FixProviderLines(text!, notes);
                if (!ReferenceEquals(fixedText, text))
                {
                    try
                    {
                        // Written back byte for byte but the fixed lines: a file saved as "ANSI" keeps its accented letters (review of a0fa75f).
                        var (raw, encoding) = DataFile.ReadKeepingBytes(path);
                        DataFile.WriteAtomic(path, SettingsFileText.FixProviderLines(raw, new List<string>()), durable: true, encoding); // your keys are in it: to the disk first (FB-51)
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { seen = false; Busy(Clean(e.Message), out error); return changed; }
                    try { stamp = File.GetLastWriteTimeUtc(path); } catch { } // our own fix: not a change of the player's
                    text = fixedText;
                    foreach (var note in notes) Log.Warn(note);
                }
                // A line BepInEx would stop at: said before anything applies (it would set the settings above the line).
                int bad = SettingsFileText.FirstBadLine(text!);
                if (bad > 0)
                {
                    error = $"line {bad}: a setting or group name there has one of ' \" \\ [ ] = or a tab in it, or a space at its start or end; fix it and save again";
                    DropPendingChangedIn(text!); // not applied, but a setting it changed still beats an older in-game change
                    return changed;
                }
                seen = Apply(text!, changed, ref error);
            }
            catch (Exception e) when (error == null) { error = Clean(e.Message); }
            finally { if (seen) MarkRead(stamp); }
            return changed;
        }

        void Busy(string why, out string? message)
        {
            ReloadBusy = true; // an editor still saving, a virus scan: read again at the next check
            message = "another program has it open, or Windows refused access: " + why + "; it's read again in a moment";
        }

        // Folder names out of a message shown to the player (the path can hold their Windows user name).
        string Clean(string message)
        {
            string dir = Path.GetDirectoryName(_file.ConfigFilePath) ?? "";
            return dir.Length == 0 ? message : message.Replace(dir + Path.DirectorySeparatorChar, "").Replace(dir, "the settings folder");
        }

        // False: the file was held by another program partway (not marked seen; read again at the next check).
        bool Apply(string text, List<string> changed, ref string? error)
        {
            var all = _sections.SelectMany(s => s.entries).ToList();
            var before = all.ToDictionary(e => e, e => (text: e.GetSerializedValue(), value: e.BoxedValue));
            // What the file holds, read before BepInEx applies it.
            var present = SettingsFileText.KeysIn(text, out var sections);
            var bound = all.Select(e => (e.Definition.Section, e.Definition.Key)).ToHashSet();
            var strays = SettingsFileText.StrayNames(text, (s, k) => bound.Contains((s, k)));
            var missing = all.Where(e => !present.Contains((e.Definition.Section, e.Definition.Key))).ToList();
            bool damaged = missing.Count > MaxDeletedReset; // cut short, emptied: nothing is reset, the voice list isn't replaced
            var block = SettingsFileText.ProviderBlock(text, out bool closed);
            bool providers = false;
            _reloading = true;
            try
            {
                try { _file.Reload(); } // BepInEx sets each setting found in the file
                catch (Exception e)
                {
                    // It stopped partway: what it set is undone, so nothing from this save half-applies.
                    foreach (var en in all)
                        if (en.GetSerializedValue() != before[en].text) en.BoxedValue = before[en].value;
                    if (e is IOException or UnauthorizedAccessException) { Busy(Clean(e.Message), out error); return false; }
                    int line = SettingsFileText.FirstBadLine(text);
                    error = (line > 0 ? $"line {line}: " : "") + Clean(e.Message);
                    return true;
                }
                // The voice list. One that isn't read again (no "]", a badly damaged file) is left as it is in the file:
                // the mod only ever writes the lines of settings it changed (re-review, in-place writes).
                if (block != null && !closed)
                    Tell("[CFG-READ] The voice Providers list in the settings file has no closing ']', so it wasn't read again (voices stay as " +
                         "they were). Put the ']' back on its own line after the last provider and save.");
                else if (block != null && !damaged && string.Join("\n", block) != string.Join("\n", _providerLines))
                    UseProviders(block);
                else if (block == null && sections.Contains("Voices") && !damaged
                         && string.Join("\n", _providerLines) != string.Join("\n", VoiceProviderList.DefaultLines()))
                {
                    // The whole list taken out: the defaults (no keys in them), as for any deleted setting; its old keys go.
                    UseProviders(VoiceProviderList.DefaultLines());
                    Tell("[CFG-READ] The voice Providers list was taken out of the settings file, so the default list is back (it has no keys in it).");
                }
                ResetDeleted(missing, present, sections, strays, damaged);
                // A damaged save that moves a service to another address must not take its old key (or a header carrying one) with
                // it. Review XR-6 added BackgroundModel's own provider (CF-13); the 2026-10-20 audit, speech to text and pictures.
                if (damaged)
                    foreach (var (moved, secrets) in new (ConfigEntryBase[] moved, ConfigEntry<string>[] secrets)[]
                             {
                                 (new ConfigEntryBase[] { ApiUrl, Format }, new[] { ApiKey, ExtraHeaders }),
                                 (new ConfigEntryBase[] { BackgroundApiUrl, BackgroundApiFormat }, new[] { BackgroundApiKey }),
                                 (new ConfigEntryBase[] { SpeechToTextUrl }, new[] { SpeechToTextKey }),
                                 (new ConfigEntryBase[] { ImageApiUrl, ImageApiFormat }, new[] { ImageApiKey }),
                             })
                    {
                        if (moved.All(m => m.GetSerializedValue() == before[m].text)) continue;
                        foreach (var secret in secrets)
                        {
                            if (present.Contains((secret.Definition.Section, secret.Definition.Key)) || secret.Value.Length == 0) continue;
                            secret.Value = "";
                            Tell($"[CFG-READ] {moved[0].Definition.Key} changed while {secret.Definition.Key} isn't in the settings file, so the old one " +
                                 $"isn't sent to the new address. Put it back on its own line ({secret.Definition.Key} = …).");
                        }
                    }
                // In-game changes the file doesn't have yet go back on top, unless the file changed that setting itself.
                // What the file holds is noted first (before they go back on top): a later reload compares with that.
                var inFile = all.ToDictionary(e => e, e => e.BoxedValue);
                foreach (var (e, value) in _pending.ToList())
                {
                    if (!Equals(inFile[e], _known.GetValueOrDefault(e))) { _pending.Remove(e); continue; } // the file's change wins
                    if (!Equals(e.BoxedValue, value)) e.BoxedValue = value;
                }
                foreach (var e in all) _known[e] = inFile[e];
                NotUnderstood(text);
            }
            finally { _reloading = false; }
            foreach (var e in all)
                if (before[e].text != e.GetSerializedValue()) changed.Add(e.Definition.Key);
            if (providers) changed.Add("Providers");
            WriteNow(); // only what's missing from the file, or changed in the game, is written (often nothing)
            return true;

            void UseProviders(List<string> lines)
            {
                _providerLines.Clear();
                _providerLines.AddRange(lines);
                VoiceProblems.Clear();
                _voiceSlots = VoiceProviderList.Parse(_providerLines, VoiceProblems); // a new list, swapped in whole (RACE-5)
                providers = true;
            }
        }
    }

    /// <summary>
    /// A save that couldn't be applied (a bad line elsewhere) still decides the in-game changes waiting to be written: one
    /// whose setting that save changed (its line holds another value than the mod last knew, or is gone) gives way to it,
    /// as after a reload that applied. The rest are written; nothing else in the file changes (review of a0fa75f).
    /// </summary>
    private void DropPendingChangedIn(string text)
    {
        if (_pending.Count == 0) return;
        var last = new Dictionary<(string, string), string>(); // the line BepInEx would read for each setting: the last
        string section = "";
        foreach (var (raw, _) in SettingsFileText.Lines(text))
        {
            string t = raw.Trim();
            if (t.Length == 0 || t.StartsWith("#")) continue;
            if (SettingsFileText.IsHeader(t, out var name)) { section = name; continue; }
            int eq = t.IndexOf('=');
            if (eq > 0) last[(section, t[..eq].Trim())] = t[(eq + 1)..].Trim();
        }
        foreach (var e in _pending.Keys.ToList())
        {
            object? inFile = null;
            bool there = last.TryGetValue((e.Definition.Section, e.Definition.Key), out var value);
            if (there) { try { inFile = TomlTypeConverter.ConvertToValue(value!, e.SettingType); } catch { there = false; } }
            if (!there || !Equals(inFile, _known.GetValueOrDefault(e))) _pending.Remove(e);
        }
    }

    /// <summary>
    /// Lines BepInEx doesn't read although they carry a setting's name (typed in another case or with spaces, in another
    /// group, written "Name: value", or the same setting twice): said in the game, with their line numbers, so a key typed
    /// as "Apikey" isn't silently unused. Commented-out lines are meant, and left out.
    /// </summary>
    private void NotUnderstood(string text)
    {
        var all = _sections.SelectMany(s => s.entries).ToList();
        var bound = all.Select(e => (e.Definition.Section, e.Definition.Key)).ToHashSet();
        var byName = new Dictionary<string, ConfigEntryBase>(StringComparer.Ordinal);
        foreach (var e in all) byName.TryAdd(SettingsFileText.Norm(e.Definition.Key), e);
        var mine = _sections.Select(s => s.name).ToHashSet(StringComparer.Ordinal);
        var said = new List<string>();
        foreach (var l in SettingsFileText.NamedLines(text, byName.Keys, (s, k) => bound.Contains((s, k))))
        {
            if (l.read || l.commented || !byName.TryGetValue(SettingsFileText.Norm(l.name), out var e)) continue;
            if (IsOlderGroup(l.section)) continue;
            if (SettingsHistory.Retired.Contains((l.section, l.name))) continue; // FB-81: moved to another group (noted "no longer read") // an older layout's group: its values were carried over at the start (said once then)
            // A group's name is only quoted when it's one of the mod's own: a header the player typed could hold anything,
            // a key included (review of c19b1dc).
            string where = l.section.Length == 0 ? "the top" : mine.Contains(l.section) ? $"[{l.section}]" : "a group the mod doesn't have";
            if (bound.Contains((l.section, l.name))) said.Add($"line {l.line}: {l.name} is in {where} more than once, and only the last one is read");
            else if (l.name != e.Definition.Key && l.section == e.Definition.Section) said.Add($"line {l.line}: '{l.name}' isn't read (it's written {e.Definition.Key}, exactly)");
            else if (l.section != e.Definition.Section) said.Add($"line {l.line}: {e.Definition.Key} is read from [{e.Definition.Section}], not {where}");
            else said.Add($"line {l.line}: {e.Definition.Key} is written '{e.Definition.Key} = value'");
            if (said.Count == 3) break;
        }
        if (said.Count > 0) Tell("[CFG-READ] Some lines in the settings file aren't read: " + string.Join("; ", said) + ".");
    }

    /// <summary>
    /// An in-game change (a command, the console): written into the file, only that line. If the player has saved the
    /// file since the mod last read or wrote it, it's read first, and this change then goes on top (re-review W5).
    /// </summary>
    private void SaveChange(ConfigEntryBase? changed)
    {
        if (_reloading) return;
        if (changed == null) { Write(); return; }
        object keep = changed.BoxedValue;
        _pending[changed] = keep;
        if (UnreadEdits())
        {
            var reloaded = Reload(out string? error);
            try { Reloaded?.Invoke(reloaded, error); } catch (Exception e) { Log.Warn("After reading the settings file: " + e.Message); }
            if (ReloadBusy) return; // another program has the file: written once it's read (auto-reload) or by the retry
            // This change is newer than anything in that save, the same setting included: it goes on top (review of
            // c19b1dc). Setting it again runs this once more, and writes it.
            if (!Equals(changed.BoxedValue, keep)) { changed.BoxedValue = keep; return; }
            if (_pending.ContainsKey(changed)) WriteNow();
            return;
        }
        WriteNow();
    }

    /// <summary>The file has changed on disk since the mod last read or wrote it (no settling wait: before any write).</summary>
    private bool UnreadEdits()
    {
        try
        {
            string path = _file.ConfigFilePath;
            return File.Exists(path) && File.GetLastWriteTimeUtc(path) != _knownWrite;
        }
        catch { return false; }
    }

    /// <summary>In-game changes the file doesn't have yet (another program held it, say): written at the next chance.</summary>
    public bool Unsaved => _pending.Count > 0;

    /// <summary>The settings groups in the file's order, each with its settings in order (the settings menu's tabs, CF-08).</summary>
    public IReadOnlyList<(string name, string intro, IReadOnlyList<ConfigEntryBase> entries)> Groups =>
        _sections.Select(s => (s.name, s.intro, (IReadOnlyList<ConfigEntryBase>)s.entries)).ToList();

    /// <summary>A key or header (ApiKey, VoyageKey, ExtraHeaders): never shown or logged anywhere.</summary>
    public bool IsSecret(ConfigEntryBase e) => _secrets.Contains(e);

    /// <summary>The voice Providers block's lines as the mod read or last wrote them (CF-09: the settings menu edits these).</summary>
    public IReadOnlyList<string> ProviderLinesNow { get { lock (Sync) return _providerLines.ToList(); } }

    /// <summary>
    /// CF-09: the settings menu changed the voice Providers list. Only the block's lines between "Providers = [" and "]"
    /// are written (every other line of the file as it was), and the voices use the new list at once. Refused, and
    /// said why (never with a line's text: it may hold a key), when the file changed the list meanwhile, can't be read
    /// or rewritten, or its list isn't written as such a block. <paramref name="basedOn"/> is the list the change was made to.
    /// </summary>
    public bool SetProviderLines(IReadOnlyList<string> basedOn, IReadOnlyList<string> lines, out string? error)
    {
        error = null;
        lock (Sync)
        {
            if (_holdWrites) { error = "the settings file couldn't be read at the start, so it isn't written this session"; return false; }
            // Each new or changed line is one provider "(…)" (or a "#" note): anything else in the block would be read as a
            // setting or end the list, and a second "(…)" on a line would be a provider the menu can't show or remove
            // (review of a8971f9). The player's own lines already in the file are left as they are.
            var unchanged = new HashSet<string>(basedOn);
            static bool Bad(string l)
            {
                string t = l.Trim(), body = t.TrimStart('#').Trim();
                if (t.Length > 0 && !t.StartsWith("#") && !t.StartsWith("(")) return true;
                if (body.StartsWith("(") && VoiceProviderList.Tuples(body).Count() != 1) return true;
                return SettingsFileText.BreaksBepInEx(l);
            }
            if (lines.Any(l => !unchanged.Contains(l ?? "") && Bad(l ?? "")))
            {
                error = "a line in the list would be read as something else (each line is a provider in brackets, or a # note)";
                return false;
            }
            if (UnreadEdits())
            {
                var reloaded = Reload(out string? readError);
                try { Reloaded?.Invoke(reloaded, readError); } catch (Exception e) { Log.Warn("After reading the settings file: " + e.Message); }
                if (readError != null || ReloadBusy) { error = "the settings file changed outside the game and couldn't be read first"; return false; }
            }
            if (!_providerLines.SequenceEqual(basedOn)) { error = "the voice list changed in the settings file meanwhile; it's shown as it is now"; return false; }
            string path = _file.ConfigFilePath;
            var (outcome, text, _) = DataFile.ReadText(path, tries: 1);
            if (outcome != DataFile.Outcome.Read || text == null)
            {
                error = outcome == DataFile.Outcome.Missing ? "the settings file isn't there" : "another program has the settings file open";
                return false;
            }
            if (!SavedAsUtf8(path)) { error = "the settings file isn't saved as UTF-8, so the mod doesn't rewrite it"; return false; }
            var all = SettingsFileText.Lines(text);
            if (SettingsFileText.ProviderRange(text) is not { } r || r.start >= r.end || !all[r.end].text.Trim().StartsWith("]"))
            {
                error = "the Providers list in the file isn't written from \"Providers = [\" to a \"]\" on its own line, so it's left for you to edit there";
                return false;
            }
            string nl = all[r.start].end.Length > 0 ? all[r.start].end : Environment.NewLine;
            var sb = new StringBuilder();
            for (int i = 0; i <= r.start; i++) sb.Append(all[i].text).Append(all[i].end);
            foreach (var l in lines) sb.Append(l).Append(nl);
            for (int i = r.end; i < all.Count; i++) sb.Append(all[i].text).Append(all[i].end);
            try
            {
                string tmp = DataFile.WriteTemp(path, sb.ToString(), durable: true, DataFile.EncodingOfFile(path)); // your keys are in it: to the disk first (FB-51)
                DataFile.Commit(tmp, path);
                MarkRead(File.GetLastWriteTimeUtc(path));
            }
            catch (Exception e) { error = "the settings file couldn't be written (" + e.GetType().Name + ")"; return false; }
            _providerLines.Clear();
            _providerLines.AddRange(lines);
            VoiceProblems.Clear();
            _voiceSlots = VoiceProviderList.Parse(_providerLines, VoiceProblems); // a new list, swapped in whole (RACE-5)
            return true;
        }
    }

    /// <summary>
    /// A setting whose line was deleted goes back to its default, as the file's header says. Only a deliberate deletion:
    /// its group is still in the file, no older layout of it is (the next start moves those), and no more than a few
    /// settings are missing in all (an empty or cut-short file resets nothing).
    /// - A key (ApiKey, VoyageKey, ExtraHeaders) is cleared too, so a key the player took out is never sent again, to a
    ///   new ApiUrl least of all (re-review W1). Any copy of it left in the file stays there as the player wrote it: the
    ///   mod only writes the lines of settings it changed.
    /// - Any other setting whose name is still in the file (commented out, moved to another group, retyped in another
    ///   case or with spaces) was kept, so it keeps its value.
    /// </summary>
    private void ResetDeleted(List<ConfigEntryBase> missing, HashSet<(string section, string key)> present, HashSet<string> sections,
        HashSet<string> strays, bool damaged)
    {
        if (missing.Count == 0) return;
        if (damaged)
        {
            Tell($"[CFG-READ] {missing.Count} settings are missing from the settings file, so none were reset and the voice list wasn't read " +
                 "again (everything keeps its current value, and the missing lines are written back). To put one setting back to its " +
                 "default, delete only its line.");
            return;
        }
        // Only an older name of the setting in its own group still counts (NPC Chatter's renames): another group of an
        // older layout is never read once the setting's own group is in the file (review of a0fa75f).
        bool OlderLayout(ConfigEntryBase e) =>
            LegacySections.TryGetValue(e.Definition.Section, out var routes) && routes.Contains(e.Definition.Section)
            && (_oldKeys.TryGetValue(e, out var old) ? old : Array.Empty<string>()).Any(k => present.Contains((e.Definition.Section, k)));
        foreach (var e in missing.Where(e => sections.Contains(e.Definition.Section) && !OlderLayout(e)))
        {
            if (Equals(e.BoxedValue, e.DefaultValue)) continue;
            string name = $"[{e.Definition.Section}] {e.Definition.Key}";
            if (_secrets.Contains(e))
            {
                e.BoxedValue = e.DefaultValue;
                Tell($"[CFG-READ] {name} isn't on its own line in the settings file any more (deleted, commented out, or not where it's " +
                     "read from), so that key isn't used any more. Any other line you kept it on is left as you wrote it.");
                continue;
            }
            if (strays.Contains(SettingsFileText.Norm(e.Definition.Key)))
            {
                Log.Warn($"{name}'s line is gone, but its name is still in the settings file (commented out, in another group, or spelled differently), " +
                         "so it keeps its value. To put it back to its default, delete that line too.");
                continue;
            }
            e.BoxedValue = e.DefaultValue;
            Log.Info($"{name} was deleted from the settings file: back to its default.");
        }
    }

    /// <summary>
    /// Writes the settings file, in place: only the lines of settings changed in the game, and any setting (or group, or
    /// the voice list) missing from the file, are written. Every other line stays exactly as the player wrote it:
    /// comments, a key kept for later, a line the mod doesn't understand, a voice list with no "]" (the re-reviews of
    /// step 5: rebuilding the whole file kept losing keys). A new file, or one in an older layout (when nothing in it
    /// would be lost), is written whole in the mod's own layout.
    /// </summary>
    public bool Write()
    {
        if (_reloading) return false; // a reload writes once, when it's done (see Reload)
        // Never over edits the mod hasn't read (the retry of an in-game change, say): they're read first, and that reload
        // writes what's still pending on top of them (settings v5, after the review of c19b1dc).
        if (UnreadEdits())
        {
            var reloaded = Reload(out string? error);
            try { Reloaded?.Invoke(reloaded, error); } catch (Exception e) { Log.Warn("After reading the settings file: " + e.Message); }
            return !ReloadBusy && error == null && !Unsaved;
        }
        return WriteNow();
    }

    /// <summary>The write itself, on a file the mod has just read (a reload, the start) or knows it holds.</summary>
    private bool WriteNow()
    {
        if (_reloading) return false;
        if (_holdWrites)
        {
            if (!_warnedHold)
            {
                _warnedHold = true;
                Log.Warn("The settings file couldn't be read at the start, so it isn't written this session (nothing in it is replaced). " +
                         "Changes made in the game last until you close it.");
            }
            return false;
        }
        string path = _file.ConfigFilePath;
        try
        {
            string? onDisk = null, next;
            if (File.Exists(path))
            {
                var (outcome, read, _) = DataFile.ReadText(path, tries: 1);
                if (outcome == DataFile.Outcome.Unreadable) return false; // another program has it: tried again later (Unsaved)
                onDisk = read;
                if (onDisk != null) _fileSeen = true; // seen, even if this write fails: deleted later, it isn't made again (review of a0fa75f)
            }
            if (onDisk == null)
            {
                if (_fileSeen)
                {
                    // Deleted while the game runs: its defaults apply at the next start, so it isn't made again (with the
                    // keys) by an in-game change.
                    if (!_warnedGone) { _warnedGone = true; Tell("[CFG-READ] The settings file was deleted: it's written again, with the defaults, at the next start. Changes made in the game until then last until you close it."); }
                    return false;
                }
                next = Generate();
            }
            else next = InPlace(onDisk); // an older layout too: its groups stay (their values were carried over), nothing is lost
            if (next != onDisk)
            {
                if (onDisk != null && !SavedAsUtf8(path))
                {
                    // Rewriting it would turn every other line's accented letters into question marks (review of c19b1dc).
                    if (!_warnedEncoding)
                    {
                        _warnedEncoding = true;
                        Tell("[CFG-READ] The settings file isn't saved as UTF-8 (an older \"ANSI\" save?), so the mod doesn't rewrite it: that would " +
                             "change your other lines. Changes made in the game last until you close it. Save the file as UTF-8 to let the mod write it again.");
                    }
                    return false;
                }
                // In the encoding it was saved in, byte-order mark included (findings #25).
                string tmp = DataFile.WriteTemp(path, next, durable: true, File.Exists(path) ? DataFile.EncodingOfFile(path) : null); // your keys are in it: to the disk first (FB-51)
                DataFile.Commit(tmp, path);
            }
            // Only once it's in the file: a write that failed leaves the change pending, tried again (review of c19b1dc).
            MarkRead(File.GetLastWriteTimeUtc(path)); // our own write (or the file as just read): not "changed outside"
            _pending.Clear();
            foreach (var e in _sections.SelectMany(s => s.entries)) _known[e] = e.BoxedValue;
            _fileSeen = true;
            _warnedWrite = false;
            return true;
        }
        catch (Exception e)
        {
            // Said once until a write works: the retry runs every 2 s (a read-only file, say).
            if (!_warnedWrite) { _warnedWrite = true; Log.Warn("Could not write the settings file (tried again every few seconds): " + e.Message); }
            return false;
        }
    }

    private bool _fileSeen, _warnedGone, _warnedEncoding, _warnedWrite;

    /// <summary>The file is UTF-8 (with or without a BOM), or UTF-16/32 with a BOM: anything else isn't rewritten.</summary>
    private static bool SavedAsUtf8(string path)
    {
        try { return DataFile.IsUtf8(File.ReadAllBytes(path)); }
        catch { return true; } // couldn't look: the write itself says why
    }
    /// <summary>In-game changes not yet in the file, with the value each was changed to.</summary>
    private readonly Dictionary<ConfigEntryBase, object?> _pending = new();
    /// <summary>What the file holds for each setting, as far as the mod last read or wrote it.</summary>
    private readonly Dictionary<ConfigEntryBase, object?> _known = new();

    /// <summary>A file with groups from an older layout (renamed or split since): their values are carried over at the start.</summary>
    private static bool OlderLayout(string text)
    {
        SettingsFileText.KeysIn(text, out var sections);
        // Review F81-1: [Controls] (FB-81) is left behind with only retired lines: the refresh still runs (and notes them).
        return sections.Any(s => IsOlderGroup(s) && s != "Controls");
    }

    private static bool IsOlderGroup(string section) =>
        section is "API" or "Behaviour" or "Player" or "Social" or "Population" or "Controls" || section.StartsWith("Voice Provider ", StringComparison.Ordinal);

    /// <summary>
    /// The file with the in-game changes written into their own lines, and what's missing added: a setting just before the
    /// player's own copy of it in its group (commented out, or typed in another case), otherwise after the last setting of
    /// its group (with its description); a group at the end of the file; the voice list after [Voices] Enabled. Nothing
    /// else changes, line endings included.
    /// </summary>
    private string InPlace(string text)
    {
        var lines = SettingsFileText.Lines(text);
        string nl = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : text.Contains('\r') ? "\r" : Environment.NewLine;
        var at = new Dictionary<(string, string), int>();   // the line BepInEx reads each setting from (the last one)
        var groupEnd = new Dictionary<string, int>(StringComparer.Ordinal); // per group: its header, or its last line that isn't blank or a comment
        bool hasProviders = false;
        string section = "";
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].text.Trim();
            if (t.Length == 0) continue;
            if (SettingsFileText.IsHeader(t, out var name)) { section = name; groupEnd[section] = i; continue; }
            if (t.StartsWith("#")) continue; // comments go with what follows them: the next group's banner isn't this group's end (review of c19b1dc)
            groupEnd[section] = i;
            if (!t.Contains('=')) continue;
            string key = t[..t.IndexOf('=')].Trim();
            at[(section, key)] = i;
            if (section == "Voices" && key == "Providers") hasProviders = true;
        }
        // The player's own copy of a setting in its group, which BepInEx doesn't read: "# TalkKey = Y", "apikey = ...",
        // "ApiKey: ...". A missing setting goes back just before it, so that the copy, once put right, comes after the
        // mod's line and is the one BepInEx reads (BepInEx reads the last; review of c19b1dc).
        var all = _sections.SelectMany(s => s.entries).ToList();
        var bound = all.Select(e => (e.Definition.Section, e.Definition.Key)).ToHashSet();
        var copyAt = new Dictionary<(string, string), int>();
        var list = SettingsFileText.ProviderRange(text); // a note inside the voice list is part of the list, never a copy (review of a0fa75f)
        foreach (var l in SettingsFileText.NamedLines(text, all.Select(e => e.Definition.Key), (s, k) => bound.Contains((s, k))))
        {
            if (l.read || (l.commented && !l.equals)) continue; // "# Volume: how loud" is prose, not a copy
            if (list is { } r && l.line - 1 > r.start && l.line - 1 <= r.end) continue;
            foreach (var e in all)
                if (e.Definition.Section == l.section && SettingsFileText.Norm(e.Definition.Key) == SettingsFileText.Norm(l.name))
                    copyAt.TryAdd((l.section, e.Definition.Key), l.line - 1);
        }
        var replace = new Dictionary<int, List<string>>();
        var before = new Dictionary<int, List<string>>();
        var after = new Dictionary<int, List<string>>();
        void Add(int i, IEnumerable<string> more) { if (!after.TryGetValue(i, out var list)) after[i] = list = new(); list.AddRange(more); }
        // A setting put back above the player's copy: its line (and its default, once changed); the description is above the copy.
        IEnumerable<string> Restored(ConfigEntryBase e, int copy)
        {
            string def = TomlTypeConverter.ConvertToString(e.DefaultValue, e.SettingType), cur = Written(e);
            bool noteThere = copy > 0 && lines[copy - 1].text.Trim().StartsWith("# Default:", StringComparison.Ordinal);
            if (cur != def && e.SettingType != typeof(string) && !noteThere) yield return $"# Default: {def}";
            yield return $"{e.Definition.Key} = {cur}";
            if (hasProviders) yield break;
            var extra = ExtraLines(e).ToList();
            if (extra.Count > 0) { yield return ""; foreach (var l in extra) yield return l; }
        }
        // In-game changes: their own line's value, and its "# Default:" note.
        foreach (var e in _pending.Keys)
        {
            if (!at.TryGetValue((e.Definition.Section, e.Definition.Key), out int i)) continue; // missing: added below
            string raw = lines[i].text, cur = Written(e), def = TomlTypeConverter.ConvertToString(e.DefaultValue, e.SettingType);
            var mine = new List<string>();
            bool noteAbove = i > 0 && lines[i - 1].text.Trim().StartsWith("# Default:", StringComparison.Ordinal);
            bool wantNote = cur != def && e.SettingType != typeof(string);
            if (noteAbove && !wantNote) replace[i - 1] = new List<string>();
            else if (noteAbove) replace[i - 1] = new List<string> { $"# Default: {def}" };
            else if (wantNote) mine.Add($"# Default: {def}");
            mine.Add(raw[..(raw.IndexOf('=') + 1)] + " " + cur);
            replace[i] = mine;
        }
        // What's missing, in the mod's own order.
        var appended = new List<string>();
        foreach (var (name, intro, entries) in _sections)
        {
            var absent = entries.Where(e => !at.ContainsKey((name, e.Definition.Key))).ToList();
            bool groupThere = groupEnd.TryGetValue(name, out int end);
            if (!groupThere && absent.Count == 0) continue;
            if (!groupThere)
            {
                appended.AddRange(SectionHead(name, intro));
                foreach (var e in entries) appended.AddRange(EntryLines(e, withExtra: true));
                continue;
            }
            foreach (var e in absent)
            {
                if (copyAt.TryGetValue((name, e.Definition.Key), out int copy)) before[copy] = Restored(e, copy).ToList();
                else Add(end, new[] { "" }.Concat(EntryLines(e, withExtra: !hasProviders)).SkipLast(1));
            }
            // [Voices] Enabled is there but the list isn't: it goes after Enabled, as the mod writes it.
            if (name == "Voices" && !hasProviders && at.TryGetValue(("Voices", VoicesEnabled.Definition.Key), out int enabled) && !absent.Contains(VoicesEnabled))
                Add(enabled, new[] { "" }.Concat(ExtraLines(VoicesEnabled)).SkipLast(1));
        }
        var sb = new StringBuilder(text.Length + 256);
        for (int i = 0; i < lines.Count; i++)
        {
            var (line, end) = lines[i];
            if (before.TryGetValue(i, out var above)) foreach (var l in above) sb.Append(l).Append(nl);
            bool more = after.ContainsKey(i) || (i == lines.Count - 1 && appended.Count > 0);
            string ending = end.Length > 0 ? end : more ? nl : "";
            if (replace.TryGetValue(i, out var instead)) foreach (var l in instead) sb.Append(l).Append(l == instead[^1] ? ending : nl);
            else sb.Append(line).Append(ending);
            if (after.TryGetValue(i, out var extra))
                for (int k = 0; k < extra.Count; k++) sb.Append(extra[k]).Append(k < extra.Count - 1 || end.Length > 0 || i < lines.Count - 1 ? nl : "");
        }
        if (appended.Count > 0)
        {
            if (lines.Count > 0 && lines[^1].text.Trim().Length > 0) sb.Append(nl);
            foreach (var l in appended) sb.Append(l).Append(nl);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A setting's value as the mod writes it: BepInEx's own text, except that a backslash in a text setting is written
    /// doubled. BepInEx writes it single (TomlTypeConverter.Escape) and reads a backslash and the letter after it (b, n,
    /// t, ...) back as a control character, so a relative folder or a pattern didn't survive a save (only text starting
    /// with a drive letter is read as typed; review of c19b1dc). Each piece between backslashes goes through BepInEx's own
    /// escaping; a doubled backslash is read back as one, whatever the text starts with.
    /// </summary>
    private static string Written(ConfigEntryBase e)
    {
        if (e.SettingType != typeof(string)) return e.GetSerializedValue();
        string bs = ((char)92).ToString(), value = (string?)e.BoxedValue ?? "";
        string doubled = string.Join(bs + bs, value.Split(bs).Select(p => TomlTypeConverter.ConvertToString(p, typeof(string))));
        // BepInEx reads text starting "X:" followed by a backslash as typed (a drive path), so an escape right after the
        // colon ('I:"hi"') would come back with its backslash: the value as typed, or BepInEx's own form, is used then,
        // whichever reads back whole. Spaces at either end can't be kept: BepInEx trims every value (review of a0fa75f).
        foreach (var form in new[] { doubled, value, e.GetSerializedValue() })
        {
            if (form.IndexOfAny(new[] { '\r', '\n' }) >= 0) continue; // one line, always
            try { if ((string)TomlTypeConverter.ConvertToValue(form.Trim(), typeof(string)) == value) return form; }
            catch { }
        }
        return doubled;
    }

    private IEnumerable<string> SectionHead(string name, string intro)
    {
        yield return Rule;
        yield return "#  " + name.ToUpperInvariant();
        foreach (var l in Wrap(intro, Width - 3)) yield return "#  " + l;
        yield return Rule;
        yield return $"[{name}]";
        yield return "";
    }

    /// <summary>One setting as the mod writes it: its description, its choices, its default once changed, the line, a blank line (and the voice list after Enabled).</summary>
    private IEnumerable<string> EntryLines(ConfigEntryBase e, bool withExtra)
    {
        foreach (var l in Wrap(e.Description?.Description ?? "", Width - 2)) yield return l.Length > 0 ? "# " + l : "#";
        if (e.SettingType.IsEnum) yield return "# Choices: " + string.Join(", ", Enum.GetNames(e.SettingType));
        string def = TomlTypeConverter.ConvertToString(e.DefaultValue, e.SettingType);
        string cur = Written(e);
        // numbers, switches and choices show their default once you've changed them; text (URLs, keys) doesn't need it
        if (cur != def && e.SettingType != typeof(string)) yield return $"# Default: {def}";
        yield return $"{e.Definition.Key} = {cur}";
        yield return "";
        if (withExtra) foreach (var l in ExtraLines(e)) yield return l;
    }

    /// <summary>The lines written right after a setting (the voice list after [Voices] Enabled), ending with a blank line.</summary>
    private IEnumerable<string> ExtraLines(ConfigEntryBase e)
    {
        if (!_after.TryGetValue(e, out var extra)) yield break;
        var sb = new StringBuilder();
        extra(sb);
        foreach (var l in sb.ToString().Replace("\r\n", "\n").Split('\n').SkipLast(1)) yield return l;
    }

    /// <summary>The whole file in the mod's own layout (a new file, or an older layout being moved over).</summary>
    private string Generate()
    {
        var sb = new StringBuilder();
        foreach (var l in HeaderLines) sb.AppendLine(l);
        sb.AppendLine();
        foreach (var (name, intro, entries) in _sections)
        {
            foreach (var l in SectionHead(name, intro)) sb.AppendLine(l);
            foreach (var e in entries) foreach (var l in EntryLines(e, withExtra: true)) sb.AppendLine(l);
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>The comment lines at the top of the file.</summary>
    private static readonly string[] HeaderLines =
    {
        "# PEOPLE OF NIVALIS - SETTINGS",
        "#",
        "# Change a value after its '=' and save. While you're in the game world, the change takes effect a moment",
        "# after you save (AutoReloadSettings, on by default); /reload does it any time.",
        "# The most-used settings are at the top; advanced ones are further down.",
        "# Delete a line to put that setting back to its default. To put everything back to the defaults,",
        "# close the game and delete this file.",
    };

    private bool _blockUnclosed;

    /// <summary>What the mod writes now: the header, each group's banner, each setting's explanation (with its "# Default:" for its value).</summary>
    private SettingsFileText.Template CurrentTemplate()
    {
        var t = new SettingsFileText.Template { Header = HeaderLines.ToList() };
        foreach (var (name, intro, entries) in _sections)
        {
            t.Banners[name] = SectionHead(name, intro).TakeWhile(l => l != $"[{name}]").ToList();
            foreach (var e in entries)
                t.Blocks[(e.Definition.Section, e.Definition.Key)] = EntryLines(e, withExtra: false).TakeWhile(l => !l.StartsWith(e.Definition.Key + " = ", StringComparison.Ordinal)).ToList();
        }
        var bound = new HashSet<(string, string)>(_sections.SelectMany(x => x.entries).Select(e => (e.Definition.Section, e.Definition.Key)));
        foreach (var r in SettingsHistory.Retired) if (!bound.Contains(r)) t.Retired.Add(r);
        return t;
    }

    /// <summary>
    /// FB-23 (the author: an update shouldn't mean setting things up again, and the file should keep up): once per version,
    /// at the start, the file's own explanations, group banners and header that an earlier version wrote word for word are
    /// brought up to date, stale "# Default:" lines put right, and a setting nothing reads gets a note. Nothing else in the
    /// file changes: every value and key, the voice list, the player's own comments and line endings stay exactly as they are
    /// (<see cref="SettingsFileText.Refresh"/> checks it). Not when anything about the file is uncertain: it couldn't be read
    /// or written this session, its voice list has no ']', it isn't UTF-8, it's read-only, it has a line BepInEx would stop
    /// at or an older layout's groups, a change is still waiting, or it changed while being looked at (tried again at the
    /// next start). The file as it was is kept in NivalisAIChat\backups first (the newest three are kept).
    /// </summary>
    private void RefreshExplanations(string path)
    {
        string version = RefreshForVersion;
        string mark = "Refresh=" + version;
        if (version.Length == 0 || _holdWrites || _blockUnclosed || _pending.Count > 0 || _upgrades.Done(mark)) return;
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0 || !SavedAsUtf8(path)) return;
            // Written back as it was saved: a byte-order mark kept; a UTF-16 file is left alone (review of 0.3.1, L8).
            byte[] start = new byte[3];
            using (var fs = File.OpenRead(path)) { int got = fs.Read(start, 0, 3); if (got < 3) Array.Clear(start, got, 3 - got); }
            if ((start[0] == 0xFF && start[1] == 0xFE) || (start[0] == 0xFE && start[1] == 0xFF)) return;
            bool bom = start[0] == 0xEF && start[1] == 0xBB && start[2] == 0xBF;
            var (outcome, text, _) = DataFile.ReadText(path, tries: 1);
            if (outcome != DataFile.Outcome.Read || text == null || OlderLayout(text) || SettingsFileText.FirstBadLine(text) != 0) return;
            string next = SettingsFileText.Refresh(text, CurrentTemplate(), SettingsHistory.Wrote, out var changed, out bool refused);
            if (refused) { Log.Warn("The settings file's explanations weren't brought up to date: its [Voices] Providers list has no closing \"]\". Tried again at the next start."); return; } // not marked done (audit B5)
            if (changed.Count > 0 && next != text)
            {
                var (again, now, _) = DataFile.ReadText(path, tries: 1);
                if (again != DataFile.Outcome.Read || now != text) return; // changed meanwhile: next start
                string dir = Path.Combine(Path.GetDirectoryName(path) ?? ".", "NivalisAIChat", "backups");
                Directory.CreateDirectory(dir);
                File.Copy(path, Path.Combine(dir, $"settings-before-{version}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}.cfg"), overwrite: false);
                DataFile.Commit(DataFile.WriteTemp(path, next, durable: true, bom ? new UTF8Encoding(true) : null), path);
                // The older copies go: they hold the keys too, and a key changed since shouldn't linger (review of 0.3.1, L2).
                foreach (var old in new DirectoryInfo(dir).GetFiles("settings-before-*.cfg").OrderByDescending(f => f.LastWriteTimeUtc).Skip(3))
                    try { old.Delete(); } catch { }
                MarkRead(File.GetLastWriteTimeUtc(path));
                Log.Info($"Settings file brought up to date for {version}: {Shortly(changed)}. Your values, keys and own notes weren't touched; the file as it " +
                         "was is kept in BepInEx\\config\\NivalisAIChat\\backups.");
            }
            if (_upgrades.Once(mark)) _upgrades.Save();
        }
        catch (Exception e) { Log.Warn("Bringing the settings file's explanations up to date didn't work (the file is as it was): " + e.GetType().Name); } // no path in the log
    }

    /// <summary>"12 settings' explanations, [Voices], the header" for the log.</summary>
    private static string Shortly(List<string> changed)
    {
        var settings = changed.Where(c => !c.StartsWith("[") && c != "the header" && !c.EndsWith("(no longer read)")).ToList();
        var parts = new List<string>();
        if (settings.Count > 0) parts.Add(settings.Count == 1 ? $"{settings[0]}'s explanation" : $"{settings.Count} settings' explanations");
        int groups = changed.Count(c => c.StartsWith("["));
        if (groups > 0) parts.Add(groups == 1 ? "a group's banner" : $"{groups} groups' banners");
        if (changed.Contains("the header")) parts.Add("the header");
        parts.AddRange(changed.Where(c => c.EndsWith("(no longer read)")).Select(c => "a note on " + c.Replace(" (no longer read)", "") + ", no longer read"));
        return string.Join(", ", parts);
    }

    /// <summary>Wraps prose at the width; lines starting with spaces (lists, URLs) are kept as written.</summary>
    private static IEnumerable<string> Wrap(string text, int width)
    {
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (raw.Length <= width || raw.StartsWith("  ")) { yield return raw; continue; }
            var line = new StringBuilder();
            foreach (var word in raw.Split(' '))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width) { yield return line.ToString(); line.Clear(); }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            if (line.Length > 0) yield return line.ToString();
        }
    }
}
