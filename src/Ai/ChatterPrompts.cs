using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Ai;

/// <summary>Requests for conversations between residents (NPC chatter), phone calls, and someone thinking out loud.</summary>
public static class ChatterPrompts
{
    /// <summary>One person in an NPC conversation, as the writer sees them.</summary>
    public sealed class Person
    {
        public NpcRecord Record = null!;
        public GameSnapshot Snap = new();
        public int JoinAt;
        public int MaxLines;
        public bool Seated;
        public bool WalkedOver;
        /// <summary>FB-18: sat down at the table since the last part of their talk: the others say hi.</summary>
        public bool JustSat;
        /// <summary>The inner-life round: how they feel underneath now, and what's on their mind (empty: their record's mood and worry).</summary>
        public string Mood = "", MoodReason = "", OnMind = "";
        public string Name => Record.Persona.Name;
    }

    /// <summary>
    /// How residents talk, for conversations and phone calls. The style comes from the player's editable fields
    /// (ResidentsTalking, SoundingHuman in custom_instructions.txt); the form rule and who-knows-what stay built in.
    /// </summary>
    public static string Rules()
    {
        var sb = new StringBuilder("HOW THEY TALK\n");
        sb.Append("- No narration, no actions, no asterisks, no emojis, no quotation marks around the words.\n");
        AddField(sb, CustomInstructions.ResidentsTalking);
        AddField(sb, CustomInstructions.SoundingHuman);
        sb.Append("- The newcomer (a stranger new to the area) only comes up if someone below actually knows something about them, and then only what they know.");
        if (Prompts.PlayerPronounsNote() is { Length: > 0 } them) sb.Append(" " + them); // CF-17
        return sb.ToString();
    }

    /// <summary>One editable field, as lines of its own (nothing when the player emptied it).</summary>
    private static void AddField(StringBuilder sb, string name)
    {
        string text = CustomInstructions.Get(name);
        if (text.Length > 0) sb.Append(text).Append('\n');
    }

    /// <summary>The city, then a blank line (nothing when the player emptied TheCity).</summary>
    internal static string City() => WorldLore.Now is { Length: > 0 } c ? c + "\n\n" : "";

    /// <summary>The whole conversation, written in one go from everyone's profiles, their history together and the plan.</summary>
    /// <summary>
    /// FB-19: a phone call at a table, as the code decided it: who gets it, who's calling, whether they answer (the
    /// author's 50/50), and whether it may go on speaker (they all know the caller).
    /// </summary>
    public sealed class TableCall
    {
        public string HolderId = "", HolderName = "";
        public Callee Callee = new();
        public bool Answered, Speaker;
    }

    /// <summary>The part of the request that tells the writer about the call.</summary>
    public static string TableCallNote(TableCall call)
    {
        string holder = Prompts.FirstName(call.HolderName), callee = call.Callee.Name;
        // Someone from the holder's own life is said to be an adult, as a phone call says it (audit B1: "Mia (daughter)").
        string relation = call.Callee.Relation.Length > 0 ? $" ({call.Callee.Relation}{(call.Callee.Record == null ? ", an adult" : "")})"
                        : call.Callee.Record == null ? " (an adult)" : "";
        var sb = new StringBuilder("[A PHONE CALL] Partway through, " + holder + "'s phone goes off: it's " + callee + relation + ". ");
        sb.Append(call.Answered
            ? holder + " takes it: says \"one moment\" (or the like) to the table, then talks on the phone for a few lines. The others wait, talk " +
              "quietly, or ask \"who is it?\" afterwards; anyone who knows " + Prompts.FirstName(callee) + " may ask how they are. Lines " + holder +
              " says into the phone: phone = true. What " + callee + " says on the other end: speaker = \"" + callee + "\". "
            : holder + " doesn't answer: glances at it and tells the table, out loud, that they'll call back later (maybe saying who it was); " +
              "the others may ask about it. Nothing is said on the phone. ");
        if (call.Answered && call.Speaker)
            sb.Append("They all know " + Prompts.FirstName(callee) + ": someone may ask to put it on speaker, and from then on " +
                      "what " + Prompts.FirstName(callee) + " says is heard by everyone: speakerphone = true on those lines. ");
        sb.Append("Afterwards the table conversation carries on to a natural end. Nobody leaves the table (leaving = false).");
        return sb.ToString();
    }

    public static LlmRequest ChatterRequest(IReadOnlyList<Person> people, Func<string, string, Social.PairBook.Pair?> pair, bool table,
        IReadOnlyList<string> headlines, IReadOnlyList<string> rumors, int maxTokens, TableCall? call = null, string carryOn = "",
        Social.CircleBook.Circle? circle = null, IReadOnlyList<string>? romance = null, IReadOnlyList<string>? tellings = null, IReadOnlyList<string>? wants = null)
    {
        var req = new LlmRequest { Purpose = "chatter", MaxTokens = maxTokens, SchemaName = "chatter" };
        req.System.Add("You write short conversations between residents of the city of Nivalis: the kind a passer-by overhears on the street " +
                       "or from the next table. They are real people with their own lives, not characters in a story.\n\n" +
                       City() + Rules() + "\n\n" + AnswerFormat);

        var place = people[0].Snap;
        var sb = new StringBuilder();
        sb.AppendLine("[THE PEOPLE]");
        foreach (var p in people) sb.AppendLine("- " + Describe(p));
        sb.AppendLine();
        sb.AppendLine("[HOW THEY KNOW EACH OTHER]");
        for (int i = 0; i < people.Count; i++)
            for (int j = i + 1; j < people.Count; j++)
                sb.AppendLine("- " + Between(people[i], people[j], pair(people[i].Record.Id, people[j].Record.Id), circle?.LastTalk ?? ""));
        // FB-35: the group they make (said once, so the pairs above don't each repeat it).
        if (circle != null && Social.CircleBook.GroupLine(circle, people.Select(p => p.Record.Id).ToList()) is { Length: > 0 } group) sb.AppendLine(group);
        // FB-63: who's together, and what happens between them this time (decided; the conversation plays it out).
        if (romance is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("[LOVE LIVES] (decided; the conversation plays it out naturally, never spelled out as a rule)");
            foreach (var line in romance) sb.AppendLine("- " + line);
        }
        // The inner-life round: what each wants from this talk, and what they may pass on (decided in code; Mind.Volitions, Mind.Telling).
        if (wants is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine(Mind.Volitions.Heading);
            foreach (var line in wants) sb.AppendLine("- " + line);
        }
        if (tellings is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("[ON THEIR MINDS] (decided; said in their own words, only where the talk allows it, as a passing remark, never a report)");
            foreach (var line in tellings) sb.AppendLine("- " + line);
        }
        sb.AppendLine();
        sb.AppendLine("[WHERE AND WHEN]");
        sb.AppendLine($"{(place.Location.Length > 0 ? place.Location : "A street in Nivalis")}, {place.PartOfDay} ({place.Clock}), day {place.GameDay}. Weather: {(place.Weather.Length > 0 ? place.Weather : "grey and damp")}. {Prompts.WeatherNote}");
        if (headlines.Count > 0)
        {
            sb.AppendLine("On the Holos lately (they may have seen it): " + string.Join("; ", headlines.Take(3)));
        }
        if (rumors.Count > 0)
        {
            sb.AppendLine("Talk around here lately (about someone who may or may not be the newcomer): " + string.Join("; ", rumors.Take(3)));
        }
        req.System.Add(sb.ToString());
        if (CustomInstructions.Block() is { Length: > 0 } own) req.System.Add(own);
        if (Prompts.SpeakIn() is { Length: > 0 } speakIn) req.System.Add(speakIn); // ReplyLanguage
        req.System.Add(Prompts.SafetyLast);

        req.Messages.Add(new LlmMessage("user", call == null ? Plan(people, table, carryOn: carryOn) : Plan(people, table, call: true, carryOn: carryOn) + "\n\n" + TableCallNote(call)));
        req.JsonSchema = Schema(call != null);
        return req;
    }

    /// <summary>The stage directions: who's there, who walks over, who joins when, and how many lines each.</summary>
    /// <param name="carryOn">FB-18 (review M3): what the same table was just talking about; they carry on, no hellos.</param>
    public static string Plan(IReadOnlyList<Person> people, bool table, bool call = false, string carryOn = "")
    {
        var a = people[0];
        var b = people[1];
        var sb = new StringBuilder("[Scene: ");
        if (table)
        {
            // Everyone at the table is in it from the first line (FB-18: a table is a group).
            sb.Append($"{Listed(people.Where(p => p.JoinAt == 0).Select(p => p.Name).ToList())} are sitting at the same table. ");
            var newcomers = people.Where(p => p.JustSat).Select(p => p.Name).ToList();
            if (newcomers.Count > 0) sb.Append($"{Listed(newcomers)} {(newcomers.Count == 1 ? "has" : "have")} just sat down with them; the others say hi. ");
            sb.Append(carryOn.Length > 0
                ? $"They were just talking ({carryOn.TrimEnd('.')}) and carry on from there{(newcomers.Count > 0 ? ": hellos only for whoever just sat down" : ": no hellos")}. "
                : "Small talk across the table: how's work, how are things, what's new. ");
        }
        else if (b.WalkedOver) sb.Append($"{b.Name} walks over to {a.Name} and they get talking. ");
        else sb.Append($"{a.Name} and {b.Name} are standing near each other and get talking. ");
        foreach (var j in people.Skip(2).Where(p => !table || p.JoinAt > 0))
            sb.Append($"{j.Name} comes over and joins them after line {j.JoinAt} (their first line greets the others). ");
        sb.Append("\nThe most lines each person can say: " + string.Join(", ", people.Select(p => $"{p.Name} {p.MaxLines}")) + ". ");
        sb.Append(call
            ? "They stay at the table: nobody says goodbye or leaves. Write the whole conversation, call included.]"
            : table
                ? "They stay at the table: nobody says goodbye or leaves; the talk just winds down. Write the whole conversation.]"
                : "Each person's LAST line is a natural goodbye in their own words (back to work, home, wherever they're headed), and after it " +
                  "they leave and say nothing more. When only one person is left, the conversation is over. Write the whole conversation.]");
        return sb.ToString();
    }

    /// <summary>"Ana", "Ana and Bo", "Ana, Bo and Cy".</summary>
    private static string Listed(List<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    internal static string Describe(Person p)
    {
        var per = p.Record.Persona;
        var st = p.Record.State;
        var v = p.Record.Visual;
        var sb = new StringBuilder();
        sb.Append($"{per.Name}{(per.Nickname.Length > 0 ? $" (\"{per.Nickname}\")" : "")}: {(v.IsAndroid ? "android" : per.Age + "-year-old " + (v.Body.Length > 0 ? v.Body : "person"))}");
        if (per.Pronouns.Length > 0) sb.Append($" ({per.Pronouns})");
        sb.Append($", {Or(per.Occupation, "no steady work")}{(per.Workplace.Length > 0 ? " at " + per.Workplace : "")}.");
        sb.Append($" Temperament: {Or(per.Temperament, "ordinary")}.");
        var s = per.Speech;
        sb.Append($" Talks: {Prompts.SlangMeaning(s.SlangLevel)}; {Or(s.Verbosity, "average")}; swears {Or(s.Swearing, "sometimes")}.");
        string mood = p.Mood.Length > 0 ? p.Mood : st.Mood;
        if (Features.Moods && mood.Length > 0) sb.Append($" Mood: {mood}{(p.Mood.Length > 0 && p.MoodReason.Length > 0 ? $" ({p.MoodReason.TrimEnd('.')})" : "")}.");
        if (p.OnMind.Length > 0) sb.Append($" On their mind: {p.OnMind.TrimEnd('.')}.");
        else if (per.CurrentWorry.Length > 0) sb.Append($" On their mind: {per.CurrentWorry}");
        var lately = p.Record.LifeLog.TakeLast(2).Select(e => e.Text).ToList();
        if (lately.Count > 0) sb.Append($" Lately: {string.Join(" ", lately)}");
        if (p.Snap.NpcActivity.Length > 0) sb.Append($" Right now: {p.Snap.NpcActivity}{(p.Seated ? " (sitting)" : "")}.");
        var knows = new List<string>();
        if (st.PlayerName.Length > 0) knows.Add($"their name is {st.PlayerName}");
        if (st.KnowsYourPlace && p.Snap.PlayerVenues.Count > 0) knows.Add("they run " + string.Join(", ", p.Snap.PlayerVenues.Take(2)));
        knows.AddRange(st.PlayerFacts.TakeLast(3));
        if (st.TimesMet > 0) knows.Add(Features.Relationships ? $"has talked with them {st.TimesMet} time(s); {Prompts.AffinityWords(st.Affinity).ToLowerInvariant()}" : $"has talked with them {st.TimesMet} time(s)");
        if (knows.Count > 0) sb.Append($" Knows about the newcomer: {string.Join("; ", knows)}.");
        return sb.ToString();
    }

    /// <param name="saidAlready">Their group's last talk (FB-35): a pair's "last time" that's the same isn't said again.</param>
    internal static string Between(Person a, Person b, Social.PairBook.Pair? pair, string saidAlready = "")
    {
        if (pair == null || pair.Times == 0) return $"{a.Name} and {b.Name} have never talked before.";
        string feel = pair.Affinity switch
        {
            >= 50 => "they're good friends",
            >= 20 => "they get on well",
            > -20 => "they get on fine",
            > -50 => "they don't much like each other",
            _ => "they can't stand each other",
        };
        return $"{a.Name} and {b.Name} have talked {pair.Times} time(s); {feel}." +
               (pair.LastTalk.Length > 0 && pair.LastTalk != saidAlready ? $" Last time (day {pair.LastDay}): {pair.LastTalk}" : "");
    }

    public static JsonObject Schema(bool call = false)
    {
        JsonObject Str() => new() { ["type"] = "string" };
        JsonObject Obj(params (string name, JsonObject schema)[] props)
        {
            var p = new JsonObject();
            var req = new JsonArray();
            foreach (var (name, schema) in props) { p[name] = schema; req.Add(name); }
            return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = req, ["additionalProperties"] = false };
        }
        JsonObject Arr(JsonObject item) => new() { ["type"] = "array", ["items"] = item };
        return Obj(
            ("lines", Arr(call
                ? Obj(("speaker", Str()), ("to", Str()), ("say", Str()), ("tone", Str()), ("gesture", Str()), ("leaving", new JsonObject { ["type"] = "boolean" }),
                      ("phone", new JsonObject { ["type"] = "boolean" }), ("speakerphone", new JsonObject { ["type"] = "boolean" }))
                : Obj(("speaker", Str()), ("to", Str()), ("say", Str()), ("tone", Str()), ("gesture", Str()), ("leaving", new JsonObject { ["type"] = "boolean" })))),
            ("getAlong", Arr(Obj(("a", Str()), ("b", Str()), ("change", new JsonObject { ["type"] = "integer" })))),
            ("summary", Str()),
            ("heardAboutPlayer", Arr(Obj(("listener", Str()), ("fact", Str())))),
            ("exits", Arr(Obj(("speaker", Str()), ("leave", Str()), ("bye", Str())))));
    }

    /// <summary>The mood words a line's tone is given in.</summary>
    private static readonly string MoodWords = string.Join(", ", NivalisAIChat.Npc.Moods.All);

    public static readonly string AnswerFormat =
        "Answer with one JSON object:\n" +
        "- lines: the conversation in order. speaker: the person's name exactly as written above. to: who they're talking to (a name), or empty for everyone. " +
        "say: exactly what they say out loud. tone: how they sound saying it, one mood word (" + MoodWords + "); a person's tone moves " +
        "gradually through the conversation with how it's going, no sudden swings unless something sets them off. gesture: one they make as they " +
        "say it, only when it fits (one of: " + GestureLibrary.List + "), usually empty. leaving: true on each person's goodbye line.\n" +
        "- getAlong: for each pair who talked, how this conversation changed how they get on, -10 to 10 (0 if it didn't).\n" +
        "- summary: one plain sentence on what they talked about.\n" +
        "- heardAboutPlayer: only facts about the newcomer that someone actually told another person in this conversation (listener, fact). Usually empty.\n" +
        "- exits: for each person (speaker = their name), in case they have to cut it short: leave = what they'd say if they suddenly had to go " +
        "right now, with a real reason from their own life (a shift starting, someone waiting, an errand, a call to make), one short sentence; " +
        "bye = a short goodbye they'd say if someone else had to go.";

    // ------------------------------------------------------------------ thinking out loud

    /// <summary>Someone alone says what's on their mind out loud: from their own life, and remembered afterwards.</summary>
    /// <param name="seed">FB-33: the one thing from their life it's about (picked by the code, <see cref="MutterSeeds"/>).</param>
    /// <param name="cora">FB-02: whether they may say CORA this time (believers, now and then); otherwise they're told not to.</param>
    /// <param name="candidates">The inner-life round (VaryLines): a one-line mutter is asked for this many ways to say it (the code picks one).</param>
    /// <param name="mood">How they feel underneath now (the inner-life round), instead of their record's mood from their last chat.</param>
    public static LlmRequest ThinkRequest(NpcRecord rec, GameSnapshot snap, int maxLines = 1, MutterSeeds.Seed? seed = null, bool cora = false,
                                          int candidates = 1, string mood = "", string moodReason = "")
    {
        maxLines = Math.Clamp(maxLines, 1, 4);
        var req = new LlmRequest { Purpose = "think", MaxTokens = 1500, Background = true, SchemaName = "thought" };
        var how = new StringBuilder("You write what a resident of the city of Nivalis says to themselves, out loud, while they're on their own.\n");
        AddField(how, CustomInstructions.ThinkingOutLoud);
        AddField(how, CustomInstructions.SoundingHuman);
        req.System.Add(how + "\n" + City().TrimEnd());
        if (CustomInstructions.Block() is { Length: > 0 } own) req.System.Add(own);
        if (Prompts.SpeakIn() is { Length: > 0 } speakIn) req.System.Add(speakIn); // ReplyLanguage
        req.System.Add(Prompts.SafetyLast);
        var sb = new StringBuilder();
        sb.AppendLine("The person: " + Describe(new Person { Record = rec, Snap = snap }));
        var p = rec.Persona;
        // How they feel right now leads what they mutter and how it sounds (FB-04).
        var st = rec.State;
        string feel = mood.Length > 0 ? mood : st.Mood, why = mood.Length > 0 ? moodReason : st.MoodReason;
        if (Features.Moods && feel.Length > 0) sb.AppendLine($"How they feel right now: {feel}{(why.Length > 0 ? $" ({why.TrimEnd('.')})" : "")}.");
        if (p.ShortTermGoal.Length > 0 || p.LongTermGoal.Length > 0) sb.AppendLine($"Wants right now: {Or(p.ShortTermGoal, "nothing much")}. Dream: {Or(p.LongTermGoal, "none they'd admit")}.");
        var thoughts = rec.Talks.Where(t => t.Kind == Talk.Thought).TakeLast(3).ToList();
        if (thoughts.Count > 0)
            sb.AppendLine("Already on their mind lately (move on from these, don't repeat them): " +
                          string.Join("; ", thoughts.Select(t => $"day {t.GameDay}: {t.Summary}")));
        var talks = rec.Talks.Where(t => t.Kind is not (Talk.Thought or Talk.Overheard or Talk.Service)).TakeLast(2).ToList();
        if (talks.Count > 0)
            sb.AppendLine("Recent conversations: " + string.Join(" ", talks.Select(t => $"Day {t.GameDay}, {(t.Kind == Talk.Phone ? "on the phone" : "talking")} with {t.With}: {t.Summary}")));
        sb.AppendLine($"[Scene: {Cap(snap.PartOfDay)} in {Or(snap.Location, "the city")}, {Or(snap.Weather, "grey and damp")}." +
                      $"{(snap.NpcActivity.Length > 0 ? $" They're {snap.NpcActivity}." : "")} Nobody's with them; they say something to themselves.] {Prompts.WeatherNote}");
        if (seed != null) sb.AppendLine($"What's on their mind this time: {seed.Text}. What they say to themselves is about that.");
        sb.AppendLine("Don't open with an exclamation. " + (cora ? "They may say CORA once if it comes naturally (it's how people here say 'God')." : "Don't say CORA.") +
                      " Leave the LUZ, the 'Sec and the city's troubles out unless that's what's on their mind.");
        const string Small = "a small question to themselves (\"Did I lock the back door?\") or a small plain statement (\"I need to feed my cat when I get home.\")";
        sb.AppendLine(maxLines == 1 && candidates > 1
            ? $"Answer with one JSON object: lines = {candidates} different ways they might say it, each one line of 3 to 10 words, just a few words: " + Small +
              "; about = what was on their mind, a few words (\"rent due on Friday\"); tones = for each line, how they sound saying it, one mood word (" + MoodWords + "), following how they feel."
            : maxLines == 1
            ? "Answer with one JSON object: lines = one line of 3 to 10 words, just a few words: " + Small + "; about = what was on their mind, a few words " +
              "(\"rent due on Friday\"); tones = for that line, how they sound saying it, one mood word (" + MoodWords + "), following how they feel."
            : $"Answer with one JSON object: lines = 1 to {maxLines} lines: the first one line of 3 to 10 words, " + Small + "; any more only if the thought " +
              "runs on (just a few words each, \"...No. Tomorrow.\"); about = what was on their mind, a few words (\"rent due on Friday\"); tones = for each " +
              "line, how they sound saying it, one mood word (" + MoodWords + "), following how they feel and what the line is about.");
        req.Messages.Add(new LlmMessage("user", sb.ToString()));
        req.JsonSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["lines"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["about"] = new JsonObject { ["type"] = "string" },
                ["tones"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            },
            ["required"] = new JsonArray("lines", "about", "tones"),
            ["additionalProperties"] = false,
        };
        return req;
    }

    // ------------------------------------------------------------------ phone calls

    /// <summary>Who's on the other end: another resident (with a record of their own) or someone from the caller's own life.</summary>
    public sealed class Callee
    {
        public NpcRecord? Record;
        public string Name = "";
        /// <summary>How they're related to the caller ("brother", "old friend from the docks").</summary>
        public string Relation = "";
        public string Detail = "";
    }

    /// <summary>A phone call, both sides written; only the caller's side is overheard (the other end is in their phone).</summary>
    public static LlmRequest PhoneRequest(NpcRecord caller, GameSnapshot snap, Callee other, Social.PairBook.Pair? pair, int linesEach, int maxTokens)
    {
        var req = new LlmRequest { Purpose = "phone", MaxTokens = maxTokens, Background = true, SchemaName = "phone_call" };
        req.System.Add("You write ordinary phone calls between residents of the city of Nivalis and people they know. Someone walking past only " +
                       "hears the caller's side, so the caller's lines must make sense on their own, but you write both sides.\n\n" + Rules() + "\n\n" + City().TrimEnd());
        if (CustomInstructions.Block() is { Length: > 0 } own) req.System.Add(own);
        if (Prompts.SpeakIn() is { Length: > 0 } speakIn) req.System.Add(speakIn); // ReplyLanguage
        req.System.Add(Prompts.SafetyLast);
        var sb = new StringBuilder();
        var me = new Person { Record = caller, Snap = snap };
        sb.AppendLine("THE CALLER (the one overheard): " + Describe(me));
        if (other.Record != null)
            sb.AppendLine("ON THE OTHER END: " + Describe(new Person { Record = other.Record, Snap = new GameSnapshot() })); // no snapshot: nothing about where they are (audit B6)
        else
            sb.AppendLine($"ON THE OTHER END: {other.Name}, the caller's {Or(other.Relation, "acquaintance")}{(other.Detail.Length > 0 ? " (" + other.Detail + ")" : "")}. An adult.");
        if (other.Record != null)
            sb.AppendLine(Between(me, new Person { Record = other.Record }, pair));
        sb.AppendLine();
        sb.AppendLine($"[Scene: {Cap(snap.PartOfDay)} in {Or(snap.Location, "the city")}, {Or(snap.Weather, "grey and damp")} ({Prompts.WeatherNote.TrimEnd('.').ToLowerInvariant()}). {caller.Persona.Name} is " +
                      $"{(snap.NpcActivity.Length > 0 ? snap.NpcActivity : "out and about")}, talking to {other.Name} on the phone. " +
                      "Either of them may have made the call. " + CustomInstructions.Get(CustomInstructions.PhoneCalls));
        sb.AppendLine($"The most lines each can say: {caller.Persona.Name} {linesEach}, {other.Name} {linesEach}. The caller's LAST line ends the call in their own words " +
                      "(\"gotta go, talk later\", \"love you, bye\", \"I'll call you back\").]");
        sb.AppendLine("Answer with one JSON object: lines = the call in order, each with side (\"caller\" or \"other\"), say (exactly what they say) and tone " +
                      $"(how they sound saying it, one mood word: {MoodWords}; it moves gradually through the call, no sudden swings); " +
                      "summary = one plain sentence on what the call was about; change = how it changed how they feel about each other, -10 to 10; " +
                      "exit = what the caller would say to end the call early if they suddenly had to go, with a real reason from their own life, one short sentence.");
        if (Prompts.SpeakIn() is { Length: > 0 } speakInCall) sb.AppendLine(speakInCall); // ReplyLanguage
        sb.AppendLine(Prompts.SafetyLast); // after the PhoneCalls field above, which is the player's to edit
        req.Messages.Add(new LlmMessage("user", sb.ToString()));
        JsonObject S() => new() { ["type"] = "string" };
        req.JsonSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["lines"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["side"] = S(), ["say"] = S(), ["tone"] = S() },
                        ["required"] = new JsonArray("side", "say", "tone"),
                        ["additionalProperties"] = false,
                    },
                },
                ["summary"] = S(),
                ["change"] = new JsonObject { ["type"] = "integer" },
                ["exit"] = S(),
            },
            ["required"] = new JsonArray("lines", "summary", "change", "exit"),
            ["additionalProperties"] = false,
        };
        return req;
    }

    private static string Or(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;
    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
