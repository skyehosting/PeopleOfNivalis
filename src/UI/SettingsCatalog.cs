using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NivalisAIChat.UI;

/// <summary>How a setting is shown in the settings menu (CF-08).</summary>
public enum ControlKind
{
    /// <summary>A pill switch (on/off).</summary>
    Switch,
    /// <summary>A slider with a range and a step.</summary>
    Slider,
    /// <summary>One of a fixed list (a dropdown).</summary>
    Choice,
    /// <summary>Free text (typed, or with the on-screen keyboard).</summary>
    Text,
    /// <summary>A number typed in (a range too wide for a slider: token limits).</summary>
    Number,
    /// <summary>A key or header: shown only as "set / not set", typed in hidden. Never shown, never logged.</summary>
    Secret,
    /// <summary>A keyboard key name (TalkKey, ConsoleKey).</summary>
    Key,
    /// <summary>Part of the three rings (FB-38): drawn as the rings' master + three sliders, not on its own.</summary>
    Ring,
    /// <summary>Kept in the file but no longer read: not shown.</summary>
    Hidden,
    /// <summary>A button (add or remove a voice provider); its label is the control's Unit.</summary>
    Button,
}

/// <summary>One setting's control: its kind, range and step (sliders), choices (dropdowns), and the unit shown.</summary>
public sealed record SettingControl(ControlKind Kind, double Min = 0, double Max = 0, double Step = 1, IReadOnlyList<string>? Choices = null, string Unit = "")
{
    /// <summary>A slider's value on its step and within its range.</summary>
    public double Snap(double value)
    {
        if (!double.IsFinite(value)) value = Min;
        double v = Math.Clamp(value, Min, Max);
        if (Step > 0) v = Min + Math.Round((v - Min) / Step, MidpointRounding.AwayFromZero) * Step;
        return Math.Round(Math.Clamp(v, Min, Max), 4);
    }

    /// <summary>A slider moved one step up (+1) or down (-1).</summary>
    public double Stepped(double value, int direction) => Snap(Snap(value) + Math.Sign(direction) * Step);

    /// <summary>The value as shown next to the slider: "15 m", "0.5", "35%".</summary>
    public string Format(double value)
    {
        string n = Snap(value).ToString(Step < 1 ? "0.##" : "0", CultureInfo.InvariantCulture);
        return Unit.Length == 0 ? n : Unit == "%" ? n + "%" : n + " " + Unit;
    }

    /// <summary>A dropdown's choices with the current value in them (a hand-edited value that isn't one of them stays choosable).</summary>
    public IReadOnlyList<string> ChoicesWith(string current)
    {
        var list = (Choices ?? Array.Empty<string>()).ToList();
        if (!list.Any(c => string.Equals(c, current, StringComparison.OrdinalIgnoreCase))) list.Insert(0, current);
        return list;
    }
}

/// <summary>
/// CF-08: which control each setting gets in the settings menu, from its group, name and type. Ranges and steps follow what
/// the mod itself accepts (the clamps where it reads them, the console's /chatter limits); anything not listed gets the
/// plain control for its type (a switch, a number, a text field). One tab per settings group, in the file's order. No Unity.
/// </summary>
public static class SettingsCatalog
{
    private static readonly string[] Intervals = { "3s", "5s", "10s", "15s", "20s", "30s", "45s", "1m", "90s", "2m", "3m", "5m", "10m", "15m", "30m", "1h" };
    private static readonly string[] Periods = { "Hour", "Day", "Week", "Month" };

    private static SettingControl Pct(double step = 5) => new(ControlKind.Slider, 0, 100, step, Unit: "%");
    private static SettingControl S(double min, double max, double step, string unit = "") => new(ControlKind.Slider, min, max, step, Unit: unit);
    private static SettingControl C(params string[] choices) => new(ControlKind.Choice, Choices: choices);

    /// <summary>FB-82: what the Camera's shutter and back can be.</summary>
    /// Review F82-2: no Space, Shift, E or Q (walking keys, or the game's own, now that you walk with the Camera out).
    public static readonly string[] CameraBindings = { "Left click", "Right click", "Middle click", "Mouse 4", "Mouse 5", "F", "R", "Enter", "Tab" };

    /// <summary>The settings whose control isn't simply what their type suggests, by name (names are unique across groups).</summary>
    private static readonly Dictionary<string, SettingControl> Known = new(StringComparer.Ordinal)
    {
        // AI Connection
        ["ApiFormat"] = C("OpenAI", "Anthropic"),
        ["ApiKey"] = new(ControlKind.Secret),
        ["BackgroundApiKey"] = new(ControlKind.Secret),
        ["BackgroundApiFormat"] = C("Same", "OpenAI", "Anthropic"),
        ["TokenLimit"] = new(ControlKind.Number, 0, 100_000_000),
        ["TokenLimitPer"] = C(Periods),
        // Voices
        ["VoiceLimit"] = new(ControlKind.Number, 0, 100_000_000),
        ["VoiceLimitPer"] = C(Periods),
        ["Volume"] = Pct(),
        ["HearingDistance"] = S(1, 60, 0.5, "m"),
        ["VoiceBehind"] = Pct(),
        ["VoiceTimeoutSeconds"] = S(3, 60, 1, "s"),
        // Controls
        ["TalkKey"] = new(ControlKind.Key),
        ["ConsoleKey"] = new(ControlKind.Key),
        ["PhoneKey"] = new(ControlKind.Key),
        ["WorldChatKey"] = new(ControlKind.Key), // FB-76
        ["WorldChatRange"] = S(1, 12, 0.25, "m"),
        ["PadTalkButton"] = C("Interact", "X (Square)", "Y (Triangle)", "B (Circle)", "LB (L1)", "RB (R1)", "RT", "LT", "off"),
        ["PadConsoleButton"] = C("LT", "RT", "off"),
        ["MaxTalkDistance"] = S(1, 10, 0.5, "m"),
        // City Life
        ["GreetChance"] = Pct(),
        ["GreetRange"] = S(2, 40, 1, "m"),
        ["SightDistance"] = S(1, 40, 1, "m"),
        ["SightAngle"] = S(10, 170, 5, "deg"),
        ["GreetCooldownMinutes"] = S(0, 120, 1, "min"),
        ["MarketKnowChance"] = Pct(),
        ["MarketOnMindChance"] = Pct(),
        ["OverhearChance"] = Pct(1),
        ["OverhearDistance"] = S(0.5, 6, 0.5, "m"),
        // NPC Chatter
        ["ChatterMode"] = C("Dynamic", "Advanced"),
        ["DynamicEvery"] = C(Intervals),
        ["DynamicMaxNpcs"] = S(1, 12, 1),
        ["DynamicLinesEach"] = S(1, 12, 1),
        ["DynamicPhoneLinesEach"] = S(1, 12, 1),
        ["DynamicThinkLinesEach"] = S(1, 4, 1),
        ["DynamicStartsEach"] = S(1, 6, 1),
        ["DynamicStagger"] = S(0, 60, 1, "s"),
        ["DynamicGossipWeight"] = S(0, 100, 5),
        ["DynamicShopWeight"] = S(0, 100, 5),
        ["DynamicPhoneWeight"] = S(0, 100, 5),
        ["DynamicTextWeight"] = S(0, 100, 5),
        ["DynamicThinkWeight"] = S(0, 100, 5),
        ["ChatterPerHour"] = S(0, 500, 5),
        ["ChatterRange"] = new(ControlKind.Ring),
        ["VoiceDistance"] = new(ControlKind.Ring),
        ["VoiceBeyond"] = new(ControlKind.Ring),
        ["TextDistance"] = S(0, 100, 1, "m"),
        ["TableCallChance"] = Pct(),
        // Advanced Chatter
        ["GroupChance"] = Pct(), ["PhoneChance"] = Pct(), ["ThinkChance"] = Pct(),
        ["GroupEvery"] = C(Intervals), ["PhoneEvery"] = C(Intervals), ["ThinkEvery"] = C(Intervals),
        ["GroupMaxActive"] = S(0, 6, 1), ["PhoneMaxActive"] = S(0, 6, 1), ["ThinkMaxActive"] = S(0, 6, 1),
        ["GroupLinesEach"] = S(1, 12, 1), ["PhoneLinesEach"] = S(1, 12, 1), ["ThinkLinesEach"] = S(1, 4, 1),
        ["GroupSize"] = S(2, 4, 1),
        ["GroupJoinChance"] = Pct(), ["GroupKnownPairs"] = Pct(),
        ["GroupTables"] = new(ControlKind.Hidden), // not used since 0.3.2
        // Food Orders
        ["FriendDiscountPercent"] = Pct(),
        ["FriendDiscountAt"] = S(0, 100, 0.5, "%"),
        ["DeliveryFee"] = S(0, 100, 1),
        // Conversations
        ["Patience"] = S(0.25, 3, 0.05),
        ["IdleCheckSeconds"] = S(0, 600, 5, "s"),
        ["IdleLeaveSeconds"] = S(5, 300, 5, "s"),
        // People
        ["Friendliness"] = S(0, 100, 1),
        ["MinAge"] = S(10, 60, 1),
        ["MaxAge"] = S(18, 99, 1),
        ["TransChance"] = S(0, 100, 0.1, "%"),
        ["NonBinaryChance"] = S(0, 100, 0.1, "%"),
        ["QueerChance"] = S(0, 100, 0.1, "%"),
        // Relationships
        ["RelationshipGainMax"] = S(0, 5, 0.05),
        ["RelationshipLossMin"] = S(0, 5, 0.05),
        ["RelationshipLossMax"] = S(0, 5, 0.05),
        ["WaveAt"] = S(0, 100, 0.5, "%"), ["WaveOverAt"] = S(0, 100, 0.5, "%"), ["FriendAt"] = S(0, 100, 0.5, "%"), ["SnubAt"] = S(0, 100, 0.5, "%"),
        ["FoodGiftBonus"] = S(0, 5, 0.1),
        ["FoodFavourBonus"] = S(0, 5, 0.1),
        // Memory
        ["HistoryLimit"] = S(0, 1000, 10),
        ["LifeUpdateAfterGameDays"] = S(0, 30, 1, "days"),
        ["LifeUpdateAfterRealHours"] = S(0, 168, 1, "h"),
        ["RecentMessagesSent"] = S(6, 400, 2),
        ["SummarizeAfterMessages"] = S(20, 2000, 10),
        ["MemorySearch"] = C("Voyage", "Local"),
        ["VoyageKey"] = new(ControlKind.Secret),
        ["VoyageModel"] = C("voyage-4-lite", "voyage-4", "voyage-4-large"),
        ["MemoryIndexLimit"] = new(ControlKind.Number, 0, 1_000_000),
        // Speech Bubbles
        ["BubbleBackgroundOpacity"] = Pct(), ["BubbleBorderOpacity"] = Pct(),
        ["BubbleTextSize"] = S(50, 200, 5, "%"),
        // Interface
        ["TextSpeed"] = C("Slow", "Medium", "Fast", "Instant"),
        ["UiScale"] = S(0.6, 2, 0.05),
        ["HistoryShown"] = S(0, 2000, 10),
        // AI Fine Tuning
        ["Effort"] = C("", "low", "medium", "high", "xhigh", "max"),
        ["ReasoningEffort"] = C("", "none", "minimal", "low", "medium", "high", "xhigh"),
        ["Temperature"] = S(0, 2, 0.05),
        ["MaxReplyTokens"] = S(256, 64000, 256),
        ["MaxGenerationTokens"] = S(1024, 64000, 512),
        ["TimeoutSeconds"] = S(10, 600, 5, "s"),
        ["GiveUpAfterSeconds"] = S(-1, 600, 1, "s"),
        ["ExtraHeaders"] = new(ControlKind.Secret),
        ["ReplyLanguage"] = C("", "English", "Français", "Deutsch", "Español", "Italiano", "Português", "Nederlands", "Polski", "Русский", "日本語", "한국어", "中文"),
        // Following (FB-62)
        ["FollowDistance"] = S(0.67, 3, 0.01, "m"),
        ["FollowMinutes"] = S(0, 240, 5, "min"),
        // Romance (FB-63)
        ["DatesPerDay"] = S(0, 5, 1),
        ["StayOverUntil"] = S(6, 12, 1, "h"), // FB-85
        ["MinutesPerMessage"] = S(0, 30, 1, "min"), // time passing as you talk (0 = off)
        ["PhoneIndoorsCost"] = S(0, 2, 1),
        ["PhoneScale"] = S(0.8, 1.25, 0.05),
        ["TextNeverSees"] = S(0, 50, 5, "%"),
        ["TextAnswersLater"] = S(0, 50, 5, "%"),
        ["VoiceNoteChance"] = S(0, 100, 10, "%"),
        ["TextFirstHours"] = S(1, 24, 1, "h"), // the phone: game hours at least between texts sent first
        ["PartnerHomeChance"] = Pct(),
        // Voice Input (FB-64)
        ["VoiceInputKey"] = new(ControlKind.Key),
        ["PadVoiceButton"] = C("off", "Y (Triangle)", "RB (R1)", "LB (L1)", "X (Square)", "B (Circle)"),
        ["SpeechToTextKey"] = new(ControlKind.Secret),
        ["SpeechToTextLimit"] = S(0, 600, 5, "min"), // FB-75
        ["PhoneNotify"] = C("Pip", "Card", "Off"), // CF-20
        ["PhoneNotifyVolume"] = S(0, 100, 5, "%"),
        ["SpeechToTextLimitPer"] = C("Hour", "Day", "Week", "Month"),
        ["SpeechToTextLimitResetDay"] = S(1, 31, 1),
        ["WorldChatNextKey"] = new(ControlKind.Key), // FB-81
        // FB-82: a mouse button or a key (Switchboard's key capture takes keys only, so these are a list).
        ["CameraShutterKey"] = C(CameraBindings),
        ["CameraBackKey"] = C(CameraBindings),
        // Nightline (FB-71)
        ["LineEveryMin"] = S(5, 1440, 5, "min"),
        ["LineEveryMax"] = S(5, 1440, 5, "min"),
        ["LinePostWeight"] = S(0, 1000, 5),
        ["LineScrollWeight"] = S(0, 1000, 5),
        ["LineBothWeight"] = S(0, 1000, 5),
        ["LineReactChance"] = Pct(),
        ["LineCommentChance"] = Pct(),
        ["LineSeenFalloff"] = Pct(),
        ["LineReplyChance"] = Pct(),
        ["LineReactToYouWeight"] = S(0, 300, 10),
        ["LineDislikeBelow"] = S(0, 100, 5),
        ["LineCheckOwnLikes"] = Pct(),
        ["LineHandleStyle"] = S(0, 100, 10),
        ["LineOffWorldWeight"] = S(0, 100, 10),
        ["LineDoingsWeight"] = Pct(),
        ["LinePostsPerDay"] = S(0, 200, 1),
        ["LineReactionsPerDay"] = S(0, 1000, 10),
        ["LineCommentsPerDay"] = S(0, 500, 5),
        ["LineNightDim"] = S(0, 30, 1, "%"),
        ["LinePhotoChance"] = Pct(),
        ["LinePhotosPerDay"] = S(0, 50, 1),
        ["ImageApiFormat"] = C("OpenAI", "OpenRouter"),
        ["ImageQuality"] = C("low", "medium", "high"),
        ["ImageApiKey"] = new(ControlKind.Secret),
        ["ShotEveryHours"] = S(1, 24, 1, "h"),
        ["ShotsPerArea"] = S(10, 500, 10),
        ["ShotsDiskMB"] = S(50, 2000, 50, "MB"),
    };

    /// <summary>The control for one setting. <paramref name="valueType"/> is the setting's own type (bool, int, float, string, an enum).</summary>
    public static SettingControl For(string section, string key, Type valueType)
    {
        if (Known.TryGetValue(key, out var known)) return known;
        if (valueType == typeof(bool)) return new SettingControl(ControlKind.Switch);
        if (valueType.IsEnum) return new SettingControl(ControlKind.Choice, Choices: Enum.GetNames(valueType));
        if (valueType == typeof(int) || valueType == typeof(float) || valueType == typeof(double)) return new SettingControl(ControlKind.Number, -1_000_000, 1_000_000);
        return new SettingControl(ControlKind.Text);
    }

    /// <summary>Shown in the menu at all (a setting nothing reads any more isn't).</summary>
    public static bool Shown(string section, string key, Type valueType) => For(section, key, valueType).Kind != ControlKind.Hidden;

    private static readonly Dictionary<string, string> Words = new(StringComparer.Ordinal)
    {
        ["api"] = "API", ["ai"] = "AI", ["ui"] = "UI", ["url"] = "URL", ["npc"] = "NPC", ["npcs"] = "NPCs", ["tts"] = "TTS",
    };

    /// <summary>A setting's name as the menu shows it: "ChatterPerHour" is "Chatter per hour", "UiScale" is "UI scale".</summary>
    public static string Title(string key)
    {
        var words = new List<string>();
        int start = 0;
        for (int i = 1; i <= key.Length; i++)
        {
            bool cut = i == key.Length
                       || (char.IsUpper(key[i]) && (char.IsLower(key[i - 1]) || (i + 1 < key.Length && char.IsLower(key[i + 1]) && char.IsUpper(key[i - 1]))))
                       || (char.IsDigit(key[i]) != char.IsDigit(key[i - 1]));
            if (!cut) continue;
            words.Add(key[start..i]);
            start = i;
        }
        for (int i = 0; i < words.Count; i++)
        {
            string lower = words[i].ToLowerInvariant();
            words[i] = Words.TryGetValue(lower, out var w) ? w : i == 0 ? char.ToUpperInvariant(lower[0]) + lower[1..] : lower;
        }
        return string.Join(" ", words);
    }

    /// <summary>The line under a setting's name: the first sentence of its explanation, cut at a word if it's long.</summary>
    public static string Hint(string description, int max = 110)
    {
        string d = (description ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        int end = -1;
        for (int i = 0; i < d.Length - 1; i++)
            if (d[i] is '.' or '!' or '?' && d[i + 1] == ' ') { end = i + 1; break; }
        string first = end > 0 ? d[..end] : d;
        if (first.Length <= max) return first;
        int cut = first.LastIndexOf(' ', max - 1);
        return (cut > max / 2 ? first[..cut] : first[..(max - 1)]).TrimEnd(',', ';', ':', ' ') + "…";
    }

    /// <summary>
    /// A control as Nivalis Switchboard reads it (FB-40: its CONTRACT.md): "kind" and, as it has them, its range and step,
    /// unit (a button's label) and choices. A ring or hidden setting is "hidden" (the rings are their own rows).
    /// </summary>
    public static Dictionary<string, object?> ToContract(SettingControl c)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = c.Kind switch
            {
                ControlKind.Switch => "switch", ControlKind.Slider => "slider", ControlKind.Choice => "choice", ControlKind.Text => "text",
                ControlKind.Number => "number", ControlKind.Secret => "secret", ControlKind.Key => "key", ControlKind.Button => "button",
                _ => "hidden",
            },
        };
        if (c.Kind is ControlKind.Slider or ControlKind.Number) { d["min"] = c.Min; d["max"] = c.Max; d["step"] = c.Step; }
        if (c.Unit.Length > 0) d[c.Kind == ControlKind.Button ? "label" : "unit"] = c.Unit;
        if (c.Choices != null) d["choices"] = c.Choices.ToList();
        return d;
    }

    /// <summary>What a secret shows: never the value, only whether one is set.</summary>
    public static string SecretLabel(string? value) => string.IsNullOrWhiteSpace(value) ? "not set" : "set";

    /// <summary>
    /// The text typed for a number setting, read for its type and range: null when it isn't a number (the field keeps the old
    /// value and says so). Decimals with a dot or a comma.
    /// </summary>
    public static double? ParseNumber(SettingControl c, string text, bool whole)
    {
        string t = (text ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v)) return null;
        if (whole) v = Math.Round(v);
        if (c.Kind == ControlKind.Slider) return c.Snap(v);
        return c.Max > c.Min ? Math.Clamp(v, c.Min, c.Max) : v;
    }

    /// <summary>
    /// A value from the menu as the setting's own type (a slider's number as an int or a float, a dropdown's name as its
    /// enum): null when it can't be one (nothing is saved then).
    /// </summary>
    public static object? AsSetting(Type type, object? value)
    {
        try
        {
            if (value == null) return null;
            if (type == typeof(string)) return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            if (type == typeof(bool)) return value is bool b ? b : bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var p) ? p : null;
            if (type.IsEnum)
            {
                string name = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                return Enum.GetNames(type).FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) is { } found ? Enum.Parse(type, found) : null;
            }
            double d = value is string s ? double.Parse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture) : Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (!double.IsFinite(d)) return null;
            if (type == typeof(int)) return (int)Math.Clamp(Math.Round(d), int.MinValue, int.MaxValue);
            if (type == typeof(float)) return (float)d;
            if (type == typeof(double)) return d;
        }
        catch { }
        return null;
    }
}

/// <summary>The four ring sliders in the settings menu (FB-38): the master (the outer ring, scaling the others), then each ring.</summary>
public enum RingPart { Master, Hearing, Voices, Chatter }

/// <summary>
/// CF-08: the rings' sliders, on <see cref="Social.Rings"/>: every one moves in 0.25 m steps, the ring moved wins and the
/// others keep in order; the master scales all three from where its run of moves began (the anchor), so they keep their
/// proportions. No Unity.
/// </summary>
public static class RingSliders
{
    public static readonly RingPart[] All = { RingPart.Master, RingPart.Hearing, RingPart.Voices, RingPart.Chatter };

    public static SettingControl Control(RingPart part) => part is RingPart.Master or RingPart.Chatter
        ? new SettingControl(ControlKind.Slider, Social.Rings.MinChatter, Social.Rings.MaxRing, Social.Rings.Step, Unit: "m")
        : new SettingControl(ControlKind.Slider, 0, Social.Rings.MaxRing, Social.Rings.Step, Unit: "m");

    /// <summary>The slider's value for these rings (the master shows the outer ring).</summary>
    public static float Value(Social.Rings r, RingPart part) => part switch
    {
        RingPart.Hearing => r.Hearing,
        RingPart.Voices => r.Voices,
        _ => r.Chatter,
    };

    /// <summary>The rings after a slider moved to <paramref name="metres"/> (the master from <paramref name="anchor"/>).</summary>
    public static Social.Rings Set(Social.Rings now, Social.Rings anchor, RingPart part, float metres) => part switch
    {
        RingPart.Master => anchor.ScaledTo(metres),
        RingPart.Hearing => now.WithHearing(metres),
        RingPart.Voices => now.WithVoices(metres),
        _ => now.WithChatter(metres),
    };

    /// <summary>The voices slider means nothing while hearing is 0 (no voices): it's shown off.</summary>
    public static bool Off(Social.Rings r, RingPart part) => part == RingPart.Voices && r.Hearing == 0f;

    public static string Title(RingPart part) => part switch
    {
        RingPart.Master => "All three rings",
        RingPart.Hearing => "Hearing ring",
        RingPart.Voices => "Voices ring",
        _ => "Chatter ring",
    };

    public static string Hint(RingPart part) => part switch
    {
        RingPart.Master => "The outer ring: the other two scale with it",
        RingPart.Hearing => "VoiceDistance: voices fade to silence here (0 = no voices)",
        RingPart.Voices => "VoiceDistance + VoiceBeyond: no voice is made past it",
        _ => "ChatterRange: people within it start talking",
    };

    public static string Help(RingPart part) => part switch
    {
        RingPart.Master => "Your three rings at once: this moves the outer one (chatter) and scales hearing and voices with it, " +
                           "keeping their proportions. They're drawn at your feet while you change them: green hearing, gold " +
                           "voices, blue chatter (/rings on shows them any time).",
        RingPart.Hearing => "Residents' voices fade to silence at this ring (0 = no voices at all). The voices and chatter " +
                            "rings move out if they'd end up inside it.",
        RingPart.Voices => "Past this ring no audio is made at all; between it and the hearing ring a line plays silently and " +
                           "fades in as you come closer. At most 30 m past hearing.",
        _ => "People within this ring start talking to each other; past it (or past the voices ring) they wrap up. The inner " +
             "rings come in if they'd end up outside it.",
    };
}
