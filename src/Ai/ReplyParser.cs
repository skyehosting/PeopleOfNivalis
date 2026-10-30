using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NivalisAIChat.Ai;

public sealed class ParsedReply
{
    public string Spoken = "";
    public string? Mood;
    public string? MoodIntensity;
    public string? MoodReason;
    public int AffinityDelta;
    public string? LearnedName;
    public bool GaveName;
    /// <summary>The name they just introduced themselves by (nickname, full name, or an alias).</summary>
    public string? IntroducedAs;
    public bool SharedIdentity;
    public List<string> SelfFacts = new();
    public List<(string topic, string said)> Told = new();
    /// <summary>They're trying to remember something not in front of them: what to search their past for.</summary>
    public string? RecallQuery;
    /// <summary>A dish they're asking the player to make / agreeing to buy, and how ("delivery" or "visit").</summary>
    public string? WantsFood;
    public string? WantsFoodHow;
    /// <summary>A gesture the AI cued with the line (GestureLibrary), or "".</summary>
    public string Gesture = "";
    /// <summary>Roleplay: what they visibly do as they say it, as the AI wrote it (the action field or [[action: ...]]); cleaned by the caller.</summary>
    public string Action = "";
    /// <summary>Stage directions the model wrote into its words (*rolls her eyes*), taken out of them: Roleplay's fallback.</summary>
    public List<string> Stripped = new();
    public List<string> Facts = new();
    public bool Leave;
    /// <summary>The phone: they just gave the player their number ([[gave_number]], or gaveNumber).</summary>
    public bool GaveNumber;
    /// <summary>The player just told them they own or run a place (restaurant, bar, stall...).</summary>
    public bool LearnedTheyRunAPlace;
    /// <summary>
    /// FB-61..65: what the reply asks to happen, decided by the code beforehand and only recorded here (the prompt said
    /// whether they would): follow ("yes" they come with you, "stop"), romance ("asks" they ask you out, "yes" / "no" to
    /// you asking, "end" they end it), date ("yes" a date now, "over", FB-85 "stay" the night at yours), moveIn ("yes" / "no"), and a reminder or plan.
    /// </summary>
    public ReplyExtras Extras = new();
}

/// <summary>FB-61..65: the reply's requests (see <see cref="ParsedReply.Extras"/>). Every value is one of a few words, or "".</summary>
public sealed class ReplyExtras
{
    public string Follow = "", Romance = "", Date = "", MoveIn = "", Fishing = "";
    public string ReminderWhat = "", ReminderPlace = "";
    public int ReminderInDays, ReminderAtHour = -1;

    public static readonly string[] FollowWords = { "yes", "stop" };
    public static readonly string[] RomanceWords = { "asks", "yes", "no", "end" };
    public static readonly string[] DateWords = { "yes", "over", "stay" }; // FB-85: stay the night
    public static readonly string[] MoveInWords = { "yes", "no" };
    public static readonly string[] FishingWords = { "yes", "stop" }; // FB-73

    /// <summary>One of the words, or "" (anything else, including a placeholder, is nothing).</summary>
    public static string Word(string? value, string[] words)
    {
        string v = (value ?? "").Trim().Trim('"', '\'', '.').ToLowerInvariant();
        return Array.IndexOf(words, v) >= 0 ? v : "";
    }

    /// <summary>FB-67: notes the mod doesn't know itself ([[tag: value]], or the JSON's notes): for plugins' reply tags (at most 8).</summary>
    public Dictionary<string, string> Other = new(StringComparer.OrdinalIgnoreCase);

    public bool Any => Follow.Length + Romance.Length + Date.Length + MoveIn.Length + Fishing.Length + ReminderWhat.Length + Other.Count > 0;
}

/// <summary>Splits an NPC reply into the spoken line and its hidden [[notes]], and catches out-of-character slips.</summary>
public static class ReplyParser
{
    /// <summary>
    /// A whole number the AI wrote ("+3", "-3", "−3"), read the same whatever the player's Windows language: under ar-SA,
    /// he-IL or fa-IR the culture's minus sign isn't "-", so "-3" didn't read and a negative affinity was dropped
    /// (review of a0fa75f).
    /// </summary>
    internal static bool TryNumber(string text, out int value) =>
        int.TryParse(text.Replace("+", "").Replace((char)0x2212, '-').Trim(), System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out value);

    private static readonly Regex Tag =new(@"\[\[\s*([A-Za-z_ ]+?)\s*(?::\s*([^\]]*?))?\s*\]\]", RegexOptions.Compiled);
    private static readonly Regex OpenTag = new(@"\[\[[^\]]*$", RegexOptions.Compiled);
    private static readonly Regex SceneNote = new(@"\[\s*Scene\s*:[^\]]*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Action = new(@"\*[^*\n]{1,120}\*", RegexOptions.Compiled);
    private static readonly Regex Emphasis = new(@"(\*\*|__)(.+?)\1", RegexOptions.Compiled);
    private static readonly Regex ManyNewlines = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex ManySpaces = new(@"[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex SpeakerLabel = new(@"^\s*(\p{L}[\p{L}'\- ]{0,40}?)\s*:\s+", RegexOptions.Compiled);

    private static bool IsNameVariant(string label, string name, string nickname)
    {
        label = label.Trim();
        if (label.Length < 2) return false;
        if (label.Equals("Me", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var n in new[] { name, nickname })
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            if (n.Equals(label, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var part in n.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (label.Length >= 3 && part.StartsWith(label, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static ParsedReply Parse(string raw, string npcName, string nickname = "")
    {
        var r = new ParsedReply();
        string text = raw ?? "";
        foreach (Match m in Tag.Matches(text))
        {
            string key = m.Groups[1].Value.Trim().ToLowerInvariant().Replace(' ', '_');
            string val = m.Groups[2].Success ? Blank(m.Groups[2].Value) : "";
            switch (key)
            {
                case "mood":
                    r.Mood = val;
                    break;
                case "affinity":
                    if (TryNumber(val, out int d)) r.AffinityDelta += Math.Clamp(d, -10, 10);
                    break;
                case "learned_name":
                case "player_name":
                    if (val.Length is > 0 and < 40) r.LearnedName = val.Trim('"', '\'', '.', ' ');
                    break;
                case "gave_name":
                case "name_revealed":
                    r.GaveName = true;
                    if (val.Length is > 0 and < 60) r.IntroducedAs = val.Trim('"', '\'', '.', ' ');
                    break;
                case "gesture":
                    r.Gesture = Npc.GestureLibrary.Normalize(val);
                    break;
                case "action":
                case "do":
                    if (val.Length is > 2 and < 300) r.Action = val; // Roleplay (cleaned by the caller)
                    break;
                case "wants_food":
                    {
                        var parts = val.Split('|');
                        if (parts[0].Trim().Length is > 1 and < 80) r.WantsFood = parts[0].Trim();
                        if (parts.Length > 1) r.WantsFoodHow = parts[1].Trim().ToLowerInvariant();
                        break;
                    }
                case "recall":
                    if (val.Length is > 1 and < 120) r.RecallQuery = val;
                    break;
                case "shared_identity":
                    r.SharedIdentity = true;
                    break;
                case "self":
                    if (val.Length is > 2 and < 200) r.SelfFacts.Add(val);
                    break;
                case "told":
                    {
                        int bar = val.IndexOf('|');
                        if (bar > 0 && val.Length - bar > 2 && val.Length < 240)
                            r.Told.Add((Npc.ToldTopics.Normalize(val[..bar]), val[(bar + 1)..].Trim()));
                        break;
                    }
                case "fact":
                    if (val.Length is > 2 and < 200) r.Facts.Add(val);
                    break;
                case "leave":
                case "end":
                case "end_conversation":
                    r.Leave = true;
                    break;
                case "gave_number":
                case "number":
                case "gave_phone_number":
                    r.GaveNumber = true;
                    break;
                case "runs_place":
                case "runs_a_place":
                    r.LearnedTheyRunAPlace = true;
                    break;
                case "follow":
                    r.Extras.Follow = ReplyExtras.Word(val, ReplyExtras.FollowWords);
                    break;
                case "romance":
                    r.Extras.Romance = ReplyExtras.Word(val, ReplyExtras.RomanceWords);
                    break;
                case "date":
                    r.Extras.Date = ReplyExtras.Word(val, ReplyExtras.DateWords);
                    break;
                case "fishing":
                case "fish":
                    r.Extras.Fishing = ReplyExtras.Word(val, ReplyExtras.FishingWords); // review FB73-7: only a word that says so
                    break;
                case "move_in":
                case "movein":
                    r.Extras.MoveIn = ReplyExtras.Word(val.Length == 0 ? "yes" : val, ReplyExtras.MoveInWords);
                    break;
                case "reminder":
                case "plan":
                    {
                        // [[reminder: what | in N days | at H | place]] (the last three optional, in any order)
                        var parts = val.Split('|').Select(x => x.Trim()).ToArray();
                        if (parts.Length > 0 && Blank(parts[0]).Length is > 2 and < 200)
                        {
                            r.Extras.ReminderWhat = parts[0];
                            foreach (var p in parts.Skip(1))
                            {
                                var days = Regex.Match(p, @"(?i)^in\s+(\d{1,2})\s*days?$");
                                int atHour = HourOf(p);
                                if (days.Success) r.Extras.ReminderInDays = int.Parse(days.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                                else if (p.Equals("tomorrow", StringComparison.OrdinalIgnoreCase)) r.Extras.ReminderInDays = 1;
                                else if (p.Equals("today", StringComparison.OrdinalIgnoreCase)) r.Extras.ReminderInDays = 0;
                                else if (atHour >= 0) r.Extras.ReminderAtHour = atHour;
                                else if (Blank(p).Length > 1) r.Extras.ReminderPlace = p;
                            }
                        }
                        break;
                    }
                default:
                    // FB-67: a note for a plugin's reply tag (handed over only if a plugin registered it).
                    if (key.Length is >= 2 and <= 24 && r.Extras.Other.Count < 8 && !r.Extras.Other.ContainsKey(key)) r.Extras.Other[key] = val.Trim();
                    break;
            }
        }
        text = Tag.Replace(text, "");
        text = OpenTag.Replace(text, "");
        text = SceneNote.Replace(text, "");
        text = Emphasis.Replace(text, "$2");

        // Strip a leading speaker label ("Mara: ..." / "Oda:") the model sometimes adds.
        var label = SpeakerLabel.Match(text);
        if (label.Success && IsNameVariant(label.Groups[1].Value, npcName, nickname))
            text = text[label.Length..];

        // Stage directions: drop them when there's speech left, otherwise keep as a quiet "(shrugs)". Roleplay keeps them aside.
        foreach (Match m in Action.Matches(text)) if (r.Stripped.Count < 3) r.Stripped.Add(m.Value.Trim('*').Trim());
        string withoutActions = Action.Replace(text, "").Trim();
        if (withoutActions.Length > 0) text = withoutActions;
        else text = Action.Replace(text, m => "(" + m.Value.Trim('*').Trim() + ")");

        text = text.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"' && text.Count(c => c == '"') == 2)
            text = text[1..^1];
        text = ManySpaces.Replace(text, " ");
        text = ManyNewlines.Replace(text, "\n\n").Trim();
        r.Spoken = text;

        if (r.IntroducedAs == null && IntroducedName(text, npcName, nickname) is { } said)
        {
            r.GaveName = true;
            r.IntroducedAs = said;
        }
        return r;
    }

    // The phrase is case-insensitive ("Name's", "Call me"; straight or curly apostrophe); the name itself must be capitalised.
    private static readonly Regex Intro = new(@"\b(?i:I['’]m|I am|name['’]s|name is|names|call me|they call me|it['’]s)\s+([A-Z][\p{L}'’\-]+(?:\s+[A-Z][\p{L}'’\-]+)?)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// "Name's Immy Crowe." → "Immy Crowe". Only accepted when it matches part of their real name or nickname,
    /// so "I'm Tired" never becomes a name (aliases come through the reply's introducedAs field instead).
    /// </summary>
    public static string? IntroducedName(string text, string npcName, string nickname)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return null;
        var parts = (npcName + " " + nickname).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Length > 1).Select(p => p.ToLowerInvariant()).ToList();
        foreach (Match m in Intro.Matches(text))
        {
            string cand = m.Groups[1].Value.Trim();
            var words = cand.Split(' ');
            // keep only the leading words that belong to their name ("Name's Oda Brennquist, chief" → "Oda Brennquist")
            var kept = words.TakeWhile(w => parts.Any(p => p.StartsWith(w.ToLowerInvariant().TrimEnd(',', '.', '!', '?')) || w.ToLowerInvariant().StartsWith(p))).ToList();
            if (kept.Count > 0) return string.Join(" ", kept).TrimEnd(',', '.', '!', '?');
        }
        return null;
    }

    /// <summary>A JSON reply ({ say, mood, ... }); null when the text isn't one (the caller then falls back to tags).</summary>
    public static ParsedReply? ParseStructured(string raw, string npcName, string nickname = "")
    {
        var o = LlmClient.ExtractJsonObject(raw);
        if (o == null || o["say"] is not System.Text.Json.Nodes.JsonValue sayNode || !sayNode.TryGetValue<string>(out var say)) return null;
        // Some models fill every field ("none", "N/A", "unknown"): those are empty, not a name, a dish or a memory to look up.
        string S(string k) => o[k] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? Blank(s) : "";
        bool B(string k) => o[k] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<bool>(out var b) && b;
        int I(string k)
        {
            if (o[k] is not System.Text.Json.Nodes.JsonValue v) return 0;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
            return v.TryGetValue<string>(out var s) && TryNumber(s, out var p) ? p : 0;
        }
        var r = Parse(say, npcName, nickname); // same cleaning (labels, actions, stray tags) + name heuristic
        r.Mood = S("mood") is { Length: > 0 } m ? m : r.Mood;
        r.MoodIntensity = S("moodIntensity");
        r.MoodReason = S("moodReason");
        r.AffinityDelta = Math.Clamp(I("affinityChange"), -10, 10);
        string learned = S("learnedPlayerName").Trim('"', '\'', '.', ' ');
        if (learned.Length is > 0 and < 40) r.LearnedName = learned;
        r.GaveName |= B("toldOwnName");
        string intro = S("introducedAs").Trim('"', '\'', '.', ' ');
        if (intro.Length is > 0 and < 60) { r.IntroducedAs = intro; r.GaveName = true; }
        else if (IntroducedName(say, npcName, nickname) is { } named) { r.IntroducedAs = named; r.GaveName = true; } // "Name's Oda." with the field left empty
        r.SharedIdentity |= B("sharedIdentity");
        string food = S("wantsFood");
        if (food.Length is > 1 and < 80) { r.WantsFood = food; r.WantsFoodHow = S("wantsFoodHow").ToLowerInvariant(); }
        string recall = S("recall");
        if (recall.Length is > 1 and < 120) r.RecallQuery = recall;
        r.Gesture = Npc.GestureLibrary.Normalize(S("gesture"));
        if (S("action") is { Length: > 2 and < 300 } act) r.Action = act; // Roleplay (cleaned by the caller)
        if (o["toldAboutMe"] is System.Text.Json.Nodes.JsonArray told)
            foreach (var t in told)
            {
                if (t is not System.Text.Json.Nodes.JsonObject) continue; // a stray string in the list: skipped, the rest kept (ROBUST-2)
                string topic = t["topic"] is System.Text.Json.Nodes.JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : "";
                // Not blanked: "None." can be a real answer ("Got kids?"); ToldCheck keeps it only if their words say it.
                string said = t["said"] is System.Text.Json.Nodes.JsonValue sv && sv.TryGetValue<string>(out var ss) ? ss.Trim() : "";
                if (said.Length is > 1 and < 240) r.Told.Add((Npc.ToldTopics.Normalize(topic), said));
            }
        if (o["newSelfFacts"] is System.Text.Json.Nodes.JsonArray selfFacts)
            foreach (var f in selfFacts)
                if (f is System.Text.Json.Nodes.JsonValue fv && fv.TryGetValue<string>(out var fs) && fs.Trim().Length is > 2 and < 200) r.SelfFacts.Add(fs.Trim());
        if (o["newFacts"] is System.Text.Json.Nodes.JsonArray facts)
            foreach (var f in facts)
                if (f is System.Text.Json.Nodes.JsonValue fv && fv.TryGetValue<string>(out var fs) && Blank(fs).Length is > 2 and < 200) r.Facts.Add(fs.Trim());
        r.Leave |= B("leaving");
        r.GaveNumber |= B("gaveNumber");
        r.LearnedTheyRunAPlace |= B("learnedTheyRunAPlace");
        // The answer words as written: "no" is an answer here, not a placeholder (Word keeps only the listed words).
        string W(string k) => o[k] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var w) ? w : "";
        r.Extras.Follow = ReplyExtras.Word(W("follow"), ReplyExtras.FollowWords);
        r.Extras.Romance = ReplyExtras.Word(W("romance"), ReplyExtras.RomanceWords);
        r.Extras.Date = ReplyExtras.Word(W("date"), ReplyExtras.DateWords);
        r.Extras.MoveIn = ReplyExtras.Word(W("moveIn"), ReplyExtras.MoveInWords);
        r.Extras.Fishing = ReplyExtras.Word(W("fishing"), ReplyExtras.FishingWords); // FB-73
        if (o["notes"] is System.Text.Json.Nodes.JsonArray notes) // FB-67: plugins' reply tags
            foreach (var n in notes)
            {
                if (n is not System.Text.Json.Nodes.JsonObject no || r.Extras.Other.Count >= 8) continue;
                string tag = no["tag"] is System.Text.Json.Nodes.JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts.Trim().ToLowerInvariant() : "";
                string value = no["value"] is System.Text.Json.Nodes.JsonValue vv && vv.TryGetValue<string>(out var vs) ? vs.Trim() : "";
                if (tag.Length is >= 2 and <= 24 && !r.Extras.Other.ContainsKey(tag)) r.Extras.Other[tag] = value;
            }
        if (o["reminder"] is System.Text.Json.Nodes.JsonObject rem)
        {
            string what = rem["what"] is System.Text.Json.Nodes.JsonValue wv && wv.TryGetValue<string>(out var ws) ? Blank(ws) : "";
            if (what.Length is > 2 and < 200)
            {
                r.Extras.ReminderWhat = what;
                int Num(string key) => rem[key] is System.Text.Json.Nodes.JsonValue nv
                    ? (nv.TryGetValue<int>(out var n) ? n : nv.TryGetValue<double>(out var dn) ? (int)Math.Round(dn) : nv.TryGetValue<string>(out var ns) && TryNumber(ns, out var pn) ? pn : -1)
                    : -1;
                r.Extras.ReminderInDays = Math.Max(0, Num("inDays"));
                bool asText = rem["atHour"] is System.Text.Json.Nodes.JsonValue tv0 && tv0.TryGetValue<string>(out var ts0) && !TryNumber(ts0, out _);
                int h = asText ? -1 : Num("atHour");
                if (asText && rem["atHour"] is System.Text.Json.Nodes.JsonValue hv && hv.TryGetValue<string>(out var hs)) h = HourOf(hs.TrimStart().StartsWith("at", StringComparison.OrdinalIgnoreCase) ? hs : "at " + hs);
                // Review F10: 0 is how a model often says "no hour"; it's midnight only when the plan says so (a written "12am" is).
                if (h == 0 && !asText && !what.Contains("midnight", StringComparison.OrdinalIgnoreCase)) h = -1;
                r.Extras.ReminderAtHour = h is >= 0 and <= 23 ? h : -1;
                r.Extras.ReminderPlace = rem["place"] is System.Text.Json.Nodes.JsonValue pv && pv.TryGetValue<string>(out var ps) ? Blank(ps) : "";
            }
        }
        return r;
    }

    /// <summary>"at 8", "at 20:30", "at 8pm", "at 8 a.m.", "at noon", "at midnight" → the hour (0-23); -1 otherwise.</summary>
    internal static int HourOf(string p)
    {
        var t = p.Trim();
        if (Regex.IsMatch(t, @"(?i)^(at\s+)?noon$|^(at\s+)?midday$")) return 12;
        if (Regex.IsMatch(t, @"(?i)^(at\s+)?midnight$")) return 0;
        var m = Regex.Match(t, @"(?i)^at\s+(\d{1,2})(?::\d{2})?\s*(am|pm|a\.m\.?|p\.m\.?)?$");
        if (!m.Success) return -1;
        int h = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        string half = m.Groups[2].Value.ToLowerInvariant();
        if (half.StartsWith("p")) { if (h is < 1 or > 12) return -1; h = h % 12 + 12; }
        else if (half.StartsWith("a")) { if (h is < 1 or > 12) return -1; h %= 12; }
        return h is >= 0 and <= 23 ? h : -1;
    }

    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "n/a", "na", "n.a.", "null", "nil", "nothing", "no", "unknown", "empty", "-", "--", "—", "n", "not applicable",
        "not given", "not provided", "not mentioned", "no name", "no one", "nobody", "nothing yet", "none yet", "none given", "undefined", "blank", "\"\"", "''",
    };

    /// <summary>The value, or "" when it's a placeholder a model wrote instead of leaving the field empty.</summary>
    public static string Blank(string? value)
    {
        string v = (value ?? "").Trim();
        string bare = v.Trim('.', '!', '(', ')', '[', ']', ' ', '\'', '"');
        return bare.Length == 0 || Placeholders.Contains(bare) || Placeholders.Contains(v) ? "" : v;
    }

    // A place someone runs, ending the phrase: "noodle bar", "ramen place", "food stall", "restaurant". Not "bar exam",
    // "bar of soap", "bar tab", "shop door", "stand-up", or aching "joints" (a joint only counts as a food joint).
    private const string PlaceNoun =
        @"(?:(?:ramen|noodle|food|street[-\s]food|burger|dumpling)\s+(?:bar|shop|stall|stand|joint|place|counter)|bar|restaurant|shop|stall|stand|diner|cafe|café|eatery)s?\b" +
        @"(?!-up\b|\s+(?:door|window|sign|tab|stool|counter|bowl|owner|exam|mitzvah|of|fight|crawl|set|up|next\s+door)\b)";
    // Where a "have/got a ..." phrase has to end for it to be about owning one ("I have a ramen bar.", "...on Meridian").
    private const string PhraseEnd = @"(?=\s*(?:$|[.,!?;:—–-]|\s+(?:in|on|at|near|down|by|over|off|now|here|there|too|myself|of\s+my\s+own|and|but|so)\b))";
    private const string Subject = @"\b(?:i|we)(?:'ve|’ve|'m|’m|\s+have|\s+am|\s+are)?\s+(?:just\s+|recently\s+|kinda\s+|kind\s+of\s+|sort\s+of\s+|finally\s+|actually\s+)?";
    // "I own a noodle bar", "I'm running a noodle bar", "I've got a little noodle bar", "I just inherited this ramen bar",
    // "Just inherited this ramen bar" (no "I", as people type), "we opened a stand", "I own this place", "my restaurant",
    // "I'm a restaurant owner", "I'm the new owner of the noodle bar". Not "I bought a ramen bowl", "none of my business",
    // "I opened the shop door", "I'm the owner of that blue car", or a question about theirs ("Run a stall here?").
    private static readonly System.Text.RegularExpressions.Regex RunsPlace = new(
        @"(?i)(?:" + Subject + @"(?:own|run|running|inherited|opened(?:\s+up)?|took\s+over|manage|managing)|^\W*(?:just\s+|recently\s+)?(?:inherited|took\s+over))" +
        @"\s+(?:a|an|the|this|that|my|our)\s+(?:[\w'’-]+\s+){0,2}?" + PlaceNoun +
        @"|" + Subject + @"(?:have|got)\s+(?:got\s+)?(?:a|an|my|our)\s+(?:[\w'’-]+\s+){0,2}?" + PlaceNoun + PhraseEnd +
        @"|" + Subject + @"(?:own|run|running|manage|inherited|took\s+over)\s+(?:this|the)\s+place\b(?!\s+(?:up|down|over)\b)" +
        @"|\bmy\s+(?:own\s+|little\s+|new\s+)?" + PlaceNoun +
        @"|\bi(?:'m|’m|\s+am)\s+(?:a|an|the)\s+(?:[\w'’-]+\s+){0,2}?(?:restaurant|bar|cafe|café|shop|stall|diner|eatery|noodle[-\s]bar|ramen[-\s]bar)\s+owner\b" +
        @"|\bi(?:'m|’m|\s+am)\s+(?:the\s+)?(?:new\s+)?owner\s+(?:of|at)\s+(?:the\s+|this\s+|a\s+|my\s+)?(?:[\w'’-]+\s+){0,2}?(?:" + PlaceNoun + @"|place\b)",
        System.Text.RegularExpressions.RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The line is about owning or running somewhere (backs up the model's own "they run a place" report).</summary>
    public static bool MentionsAPlace(string? playerLine, IEnumerable<string>? venues = null)
    {
        if (string.IsNullOrWhiteSpace(playerLine)) return false;
        if (Regex.IsMatch(playerLine, @"(?i)\b(?:own|owner|run|running|inherited|opened|took\s+over)\b", RegexOptions.CultureInvariant)
            && Regex.IsMatch(playerLine, @"(?i)\b(?:bar|restaurant|stall|stand|shop|diner|cafe|café|ramen|noodles?|place|joint|eatery)\b", RegexOptions.CultureInvariant)) return true;
        return (venues ?? Array.Empty<string>()).Any(v => !string.IsNullOrWhiteSpace(v) && playerLine.Contains(v, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The player said they own or run a place ("I just inherited this ramen bar", "I own Ramen Noir"). Naming one of
    /// their own venues alongside "my", "own" or "run" counts too.
    /// </summary>
    public static bool SaysTheyRunAPlace(string? playerLine, IEnumerable<string>? venues = null)
    {
        if (string.IsNullOrWhiteSpace(playerLine)) return false;
        if (RunsPlace.IsMatch(playerLine)) return true;
        // Naming their own venue as theirs: "I own Ramen Noir", "I run Ramen Noir", "Ramen Noir is mine", "I'm the owner of Ramen Noir".
        foreach (var v in venues ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(v) || !playerLine.Contains(v, StringComparison.OrdinalIgnoreCase)) continue;
            string name = Regex.Escape(v.Trim());
            if (Regex.IsMatch(playerLine, $@"(?i)\b(?:i|we)(?:'ve|’ve|\s+have)?\s+(?:just\s+|recently\s+)?(?:own|run|inherited|took\s+over|opened|manage)\s+(?:the\s+)?{name}" +
                                          $@"|\bowner\s+of\s+(?:the\s+)?{name}|{name}(?:'s|\s+is)\s+(?:mine|ours|my\s+place|my\s+restaurant)|\bmy\s+(?:place|restaurant|bar)\s*,?\s+{name}", RegexOptions.CultureInvariant))
                return true;
        }
        return false;
    }

    private static readonly Regex[] LeakPatterns =
    {
        new(@"\bas an? (ai|a\.i\.|artificial intelligence|language model|assistant|chatbot)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\b(large )?language model\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\bI('m| am) (just |only |actually )?(an? )?(ai|a\.i\.|chatbot|language model|virtual assistant|ai assistant)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\b(system prompt|my instructions|my programming|my guidelines|content polic(y|ies)|openai|anthropic|chatgpt|\bclaude\b|\bllm\b)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\b(role-?play(ing)?|out of character|break(ing)? character|stay(ing)? in character|this (simulation|roleplay)|(video ?game|fictional) character|non-player|npcs?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\b(I can('|no)t (help|assist) with (that|this) request|I('m| am) not able to (help|assist))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    };

    /// <summary>True when the reply talks like an AI assistant instead of a person.</summary>
    public static bool LooksOutOfCharacter(string spoken, bool isAndroid)
    {
        if (string.IsNullOrWhiteSpace(spoken)) return true;
        for (int i = 0; i < LeakPatterns.Length; i++)
        {
            // Androids may honestly say "I'm an AI"-ish things about their body; only their model/assistant talk counts.
            if (isAndroid && i == 2 && !Regex.IsMatch(spoken, @"language model|chatbot|assistant", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
            if (LeakPatterns[i].IsMatch(spoken)) return true;
        }
        return false;
    }

    private static readonly string[][] Deflections =
    {
        new[] // plain speakers
        {
            "I'm sorry, I don't really follow. Are you feeling alright?",
            "That's an odd thing to say to someone you've just met.",
            "I'd rather not get into that, if it's all the same to you.",
            "Hm. I think I'll leave that one alone.",
        },
        new[] // casual
        {
            "Huh? Nah, you lost me there.",
            "Okay... weird thing to say to a stranger, you know that?",
            "Yeah, I'm not touching that one.",
            "Uh. Sure. Anyway.",
        },
        new[] // heavy slang
        {
            "The hell you on about? You been hitting the grunt dust?",
            "Nah, chief. Not getting into all that.",
            "CORA's tears, you're a strange one.",
            "Whatever you're selling, I ain't buying.",
        },
    };

    public static string Deflection(Random rng, int slangLevel)
    {
        var set = Deflections[slangLevel <= 0 ? 0 : slangLevel >= 3 ? 2 : 1];
        return set[rng.Next(set.Length)];
    }

    private static readonly Regex Sentence = new(@"[^.!?…]+(?:[.!?…]+[""')]*|$)\s*", RegexOptions.Compiled);

    /// <summary>Keep a first reaction short: the first sentence, plus more only while the whole stays tiny.</summary>
    public static string Brief(string text, int max = 80)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= max) return text;
        var parts = Sentence.Matches(text).Select(m => m.Value.Trim()).Where(s => s.Length > 0).ToList();
        if (parts.Count == 0) return text;
        string result = parts[0];
        for (int i = 1; i < parts.Count && result.Length + 1 + parts[i].Length <= max; i++) result += " " + parts[i];
        if (result.Length > max + 40)
        {
            int cut = result.LastIndexOf(' ', max);
            result = (cut > 20 ? result[..cut] : result[..max]).TrimEnd(',', ';', ' ') + "…";
        }
        return result;
    }

    /// <summary>Player text can't open or close hidden notes or forge [Scene: ...] lines.</summary>
    public static string EscapePlayerText(string text) => Quotable(text, 600);

    /// <summary>
    /// Text from outside the mod's own wording (the player's, or something an AI wrote that goes into a later prompt),
    /// made safe to quote there: look-alike characters folded (NFKC turns fullwidth brackets into plain ones), every kind
    /// of bracket made round, so it can't open a [Scene: ...] note or a JSON object, and every line break (Unicode's too)
    /// and control character a space. Cut to <paramref name="max"/> characters.
    /// </summary>
    public static string Quotable(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string t;
        try { t = text.Normalize(System.Text.NormalizationForm.FormKC); }
        catch (ArgumentException) { t = text; } // an unpaired surrogate: the brackets and breaks are still handled below
        var sb = new System.Text.StringBuilder(t.Length);
        foreach (char c in t)
        {
            switch (char.GetUnicodeCategory(c))
            {
                case System.Globalization.UnicodeCategory.OpenPunctuation: sb.Append('('); break;
                case System.Globalization.UnicodeCategory.ClosePunctuation: sb.Append(')'); break;
                case System.Globalization.UnicodeCategory.LineSeparator:
                case System.Globalization.UnicodeCategory.ParagraphSeparator:
                case System.Globalization.UnicodeCategory.Control: sb.Append(' '); break;
                default: sb.Append(c); break;
            }
        }
        // A local server's chat template ("<|im_start|>", "<|eot_id|>") never sees one of its own control tokens in here.
        t = sb.ToString().Replace("<|", "< |").Replace("|>", "| >").Trim();
        if (t.Length <= max) return t;
        int cut = max;
        if (cut > 0 && char.IsHighSurrogate(t[cut - 1])) cut--; // never half a character
        return t[..cut].TrimEnd();
    }

    // ------------------------------------------------------------------ what the player just asked about them

    private static readonly Regex QuestionShape = new(@"\?|^\s*(?i:what|where|how|who|why|when|which|do|does|did|are|is|have|has|can|could|would|will|tell\s+me|so|any|ever)\b", RegexOptions.CultureInvariant);

    // Each topic only counts when the question is about *them* ("are you single?", "your brother?"), not "have you seen my brother?".
    private static readonly (string topic, Regex re)[] TopicQuestions =
    {
        ("age", new Regex(@"(?i)\bhow\s+old\s+(?:are|r)\s+(?:you|u|ya)\b|\byour\s+age\b|\bwhat\s+age\s+are\s+you\b|\byour\s+birthday\b|\bwhen\s+were\s+you\s+born\b", RegexOptions.CultureInvariant)),
        ("job", new Regex(@"(?i)\bwhat\s+do\s+you\s+do\b(?!\s+for\s+fun)|\bwhere\s+do\s+you\s+work\b|\bdo\s+you\s+work\b|\byour\s+(?:job|work|boss|line\s+of\s+work|occupation)\b|\bwho\s+do\s+you\s+work\s+for\b|\bwhat\s+do\s+you\s+do\s+for\s+(?:a\s+)?(?:living|work)\b", RegexOptions.CultureInvariant)),
        ("home", new Regex(@"(?i)\bwhere\s+do\s+you\s+live\b|\bdo\s+you\s+live\b|\byour\s+(?:place|apartment|flat|neighbou?rhood|home)\b|\bwhere(?:'s|\s+is)\s+home\b|\bwhich\s+(?:district|part\s+of\s+(?:town|the\s+city))\s+(?:do\s+)?you\b", RegexOptions.CultureInvariant)),
        ("orientation", new Regex(@"(?i)\bare\s+you\s+(?:gay|straight|bi|bisexual|lesbian|queer|pan|pansexual|asexual|ace)\b|\byour\s+(?:orientation|sexuality)\b|\bare\s+you\s+into\s+(?:guys|girls|men|women|dudes)\b|\bdo\s+you\s+(?:like|date)\s+(?:guys|girls|men|women)\b", RegexOptions.CultureInvariant)),
        ("relationship", new Regex(@"(?i)\bare\s+you\s+(?:single|married|taken|engaged|divorced|seeing\s+(?:someone|anyone)|dating)\b|\byour\s+(?:girlfriend|boyfriend|partner|wife|husband|spouse|ex)\b|\bdo\s+you\s+have\s+a\s+(?:girlfriend|boyfriend|partner|wife|husband)\b|\banyone\s+special\b", RegexOptions.CultureInvariant)),
        ("faith", new Regex(@"(?i)\bare\s+you\s+(?:religious|spiritual|an?\s+atheist|a\s+believer)\b|\bdo\s+you\s+(?:believe\s+in|pray|go\s+to\s+(?:church|temple))\b|\byour\s+(?:religion|faith|god)\b", RegexOptions.CultureInvariant)),
        ("politics", new Regex(@"(?i)\bdid\s+you\s+vote\b|\bwho\s+(?:do|did)\s+you\s+vote\b|\bdo\s+you\s+vote\b|\byour\s+politics\b|\bare\s+you\s+(?:political|left|right|conservative|liberal|an?\s+anarchist|a\s+socialist)\b|\bwhat\s+are\s+your\s+(?:politics|views)\b", RegexOptions.CultureInvariant)),
        ("education", new Regex(@"(?i)\bdid\s+you\s+(?:go\s+to|finish|study|graduate|drop\s+out)\b|\bwhere\s+did\s+you\s+study\b|\bwhat\s+did\s+you\s+study\b|\byour\s+(?:school|degree|education)\b|\bdo\s+you\s+have\s+a\s+degree\b", RegexOptions.CultureInvariant)),
        ("gender", new Regex(@"(?i)\byour\s+(?:gender|pronouns)\b|\bare\s+you\s+(?:trans|non[-\s]?binary|enby|a\s+man|a\s+woman|a\s+guy|a\s+girl)\b|\bwhat\s+pronouns\b", RegexOptions.CultureInvariant)),
        ("family", new Regex(@"(?i)\byour\s+(?:family|siblings?|brothers?|sisters?|kids|children|parents|mom|mum|dad|mother|father|son|daughter)\b|\bdo\s+you\s+have\s+(?:any\s+)?(?:kids|children|siblings|brothers?|sisters?|family)\b", RegexOptions.CultureInvariant)),
        ("hobbies", new Regex(@"(?i)\bwhat\s+do\s+you\s+do\s+for\s+fun\b|\byour\s+(?:hobb(?:y|ies)|free\s+time|spare\s+time)\b|\bdo\s+you\s+have\s+(?:any\s+)?hobbies\b|\bin\s+your\s+(?:free|spare|down)\s+time\b", RegexOptions.CultureInvariant)),
        ("likes", new Regex(@"(?i)\byour\s+favou?rite\b|\bwhat\s+do\s+you\s+like\b|\bdo\s+you\s+like\b|\bwhat\s+are\s+you\s+into\b|\bwhat\s+do\s+you\s+enjoy\b", RegexOptions.CultureInvariant)),
        ("dislikes", new Regex(@"(?i)\bwhat\s+do\s+you\s+hate\b|\bdo\s+you\s+hate\b|\bwhat\s+can(?:'t|not)\s+you\s+stand\b|\bwhat\s+do\s+you\s+dislike\b", RegexOptions.CultureInvariant)),
        ("goals", new Regex(@"(?i)\byour\s+(?:dreams?|goals?|plans|ambitions?)\b|\bwhat\s+do\s+you\s+want\s+(?:to\s+be|to\s+do|in\s+life|out\s+of\s+life)\b|\bwhere\s+do\s+you\s+see\s+yourself\b", RegexOptions.CultureInvariant)),
        ("fears", new Regex(@"(?i)\bare\s+you\s+(?:afraid|scared)\b|\bwhat\s+(?:scares|worries)\s+you\b|\bwhat\s+are\s+you\s+(?:afraid|scared)\s+of\b|\byour\s+(?:fears?|worries)\b|\bkeeps?\s+you\s+up\s+at\s+night\b", RegexOptions.CultureInvariant)),
        ("background", new Regex(@"(?i)\bwhere\s+did\s+you\s+grow\s+up\b|\bwhere\s+are\s+you\s+from\b|\byour\s+(?:past|story|history|backstory|childhood)\b|\bhow\s+did\s+you\s+end\s+up\b", RegexOptions.CultureInvariant)),
        ("health", new Regex(@"(?i)\byour\s+(?:implants?|augments?|augmentations?|chrome|cyberware|scars?|health)\b|\bdo\s+you\s+have\s+(?:any\s+)?(?:implants?|augments?|chrome|cyberware)\b", RegexOptions.CultureInvariant)),
    };

    private static readonly Regex AskedNameRe = new(@"(?i)\bwhat(?:'s|\s+is)\s+your\s+name\b|\byour\s+name\s*\?|\bwho\s+are\s+you\s*\?|\bwhat\s+(?:do|should|can)\s+(?:i|people|we)\s+call\s+you\b|\bintroduce\s+yourself\b|\bdo\s+you\s+have\s+a\s+name\b|\bwhat\s+do\s+they\s+call\s+you\b|^\s*name\s*\?\s*$", RegexOptions.CultureInvariant);

    /// <summary>Topics about the NPC the player's line asks about ("how old are you?" → age).</summary>
    public static List<string> TopicsAsked(string? playerLine)
    {
        var topics = new List<string>();
        if (string.IsNullOrWhiteSpace(playerLine) || !QuestionShape.IsMatch(playerLine)) return topics;
        foreach (var (topic, re) in TopicQuestions)
            if (re.IsMatch(playerLine)) topics.Add(topic);
        return topics;
    }

    public static bool AskedForName(string? playerLine) => !string.IsNullOrWhiteSpace(playerLine) && AskedNameRe.IsMatch(playerLine);

    /// <summary>Whether a reported topic answers what was asked (job ↔ workplace, gender ↔ pronouns, background ↔ home/family).</summary>
    public static bool Covers(string reported, string asked) => reported == asked || asked switch
    {
        "job" => reported == "workplace",
        "gender" => reported == "pronouns",
        "background" => reported is "home" or "family",
        "likes" => reported == "hobbies",
        "hobbies" => reported == "likes",
        _ => false,
    };

    /// <summary>"Wittaya. Why?" after "what's your name?" → "Wittaya" (only when it's part of their real name or nickname).</summary>
    public static string? BareName(string spoken, string npcName, string nickname)
    {
        if (string.IsNullOrWhiteSpace(spoken) || string.IsNullOrWhiteSpace(npcName)) return null;
        var parts = (npcName + " " + nickname).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Length > 1).Select(p => p.ToLowerInvariant()).ToHashSet();
        var words = Regex.Matches(spoken, @"[\p{L}'’\-]+").Select(m => m.Value).Take(8).ToList();
        for (int i = 0; i < words.Count; i++)
        {
            if (!char.IsUpper(words[i][0]) || !parts.Contains(words[i].ToLowerInvariant())) continue;
            var kept = new List<string> { words[i] };
            if (i + 1 < words.Count && char.IsUpper(words[i + 1][0]) && parts.Contains(words[i + 1].ToLowerInvariant())) kept.Add(words[i + 1]);
            return string.Join(" ", kept);
        }
        return null;
    }

    // ------------------------------------------------------------------ age, when the model forgets to report it

    // Only *their* age: "how old are you?", never "how old is your brother?" or "what's the drinking age?".
    private static readonly Regex AskedAge = new(@"(?i)\bhow\s+old\s+(?:are|r)\s+(?:you|u|ya)\b|\byour\s+age\b|\bwhat\s+age\s+are\s+you\b|\bhow\s+many\s+years\s+(?:have\s+)?you\b", RegexOptions.CultureInvariant);
    private static readonly Regex AgeRange = new(@"(?i)\b(early|mid|late)[-\s]?(twenties|thirties|forties|fifties|sixties|seventies|eighties)\b", RegexOptions.CultureInvariant);
    // "I'm 22", "I am twenty-two", "I just turned 30", "I'm turning 40" — the speaker's own age only.
    private static readonly Regex StatesAge = new(@"(?i)\b(?:i'?m|i\s+am|i\s+(?:just\s+)?turned|i'?m\s+turning)\s+(?<n>[a-z\d\s-]{1,20}?)(?=\s*(?:years|yrs|$|[.,!?;—–]))", RegexOptions.CultureInvariant);
    // A bare answer has to lead the reply: "Twenty-two. Why?", "31, why?"
    private static readonly Regex LeadingNumber = new(@"(?i)^\W*(?<n>\d{1,3}|(?:twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety)(?:[-\s]?(?:one|two|three|four|five|six|seven|eight|nine))?|eighteen|nineteen)\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// What age they gave, if they just answered "how old are you" (or said "I'm 22" / "I just turned 30") —
    /// whether or not it's their real age. Null when they didn't say. Backs up the model's own toldAboutMe report.
    /// </summary>
    public static string? AgeStated(string? playerLine, string spoken)
    {
        if (string.IsNullOrWhiteSpace(spoken)) return null;
        bool asked = playerLine != null && AskedAge.IsMatch(playerLine);
        if (asked && AgeRange.Match(spoken) is { Success: true } range) return range.Value.ToLowerInvariant();
        foreach (Match m in StatesAge.Matches(spoken))
            if (ParseAge(m.Groups["n"].Value.Trim()) is { } n) return n.ToString();
        if (!asked) return null;
        var lead = LeadingNumber.Match(spoken);
        return lead.Success && ParseAge(lead.Groups["n"].Value) is { } age ? age.ToString() : null;
    }

    private static readonly string[] Units = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };
    private static readonly string[] Tens = { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };

    private static int? ParseAge(string text)
    {
        string t = text.Trim().ToLowerInvariant();
        if (Npc.CanonAge.ReadDigits(t) is int d) return d is >= 16 and <= 120 ? d : null; // digits in any script
        if (t == "eighteen") return 18;
        if (t == "nineteen") return 19;
        var parts = t.Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 2) return null;
        int tens = Array.IndexOf(Tens, parts[0]);
        if (tens < 2) return null;
        int unit = parts.Length == 2 ? Array.IndexOf(Units, parts[1]) : 0;
        return unit < 0 ? null : tens * 10 + unit;
    }
}
