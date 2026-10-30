using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Social;

namespace NivalisAIChat.Npc;

/// <summary>
/// FB-76, world chat (the author: "walk up to someone, hit enter, then type something, and if someone is right next to them
/// ... they would respond"; "being able to select multiples and they will answer one at a time ... maybe the second won't
/// think it needs to answer ... unless it can understand and distinguish between an open and closed question"): a few words
/// with the people around you, out and about. One request per thing you say: the ones you're talking to and the ones
/// standing in view, who they are and how they know each other and you; the answer is 0-3 lines (the ones you talked to,
/// one at a time; others chiming in only when it's natural), who you were talking to as they read it ("the one in the
/// yellow coat"), how it moved each, and its topic (what the ones who overhear remember). No Unity.
/// </summary>
public sealed class WorldTalk
{
    public sealed class Member
    {
        public NpcRecord Record = null!;
        public GameSnapshot Snap = new();
        /// <summary>How the player sees them on the line's chips ("Mae", or "Stranger (woman)").</summary>
        public string Label = "";
        /// <summary>What they look like (the model's looks), so the player can say "the one in the yellow coat".</summary>
        public string Looks = "";
        /// <summary>The player talked to them at least once in this moment.</summary>
        public bool WasAddressed;
        public bool Gone;
        public int Feel;
        public string Id => Record.Id;
        public string Name => Record.Persona.Name;
    }

    public sealed class Reply
    {
        public bool Ok;
        public string Error = "";
        public List<GroupChat.Line> Lines = new();
        public Dictionary<string, int> Feel = new();
        /// <summary>Who the player was talking to, as the answer read it (ids; empty: as the player chose).</summary>
        public List<string> Addressed = new();
        public string Topic = "";

        public void KeepTo(ICollection<string> speakers)
        {
            if (speakers.Count == 0) return;
            var kept = Lines.Where(l => speakers.Contains(l.SpeakerId)).ToList();
            if (kept.Count > 0) Lines = kept;
        }
    }

    public const string PlayerId = GroupChat.PlayerId;
    public const int MaxLines = 3, TranscriptKept = 20, MaxTopic = 80;

    public readonly List<Member> Members = new();
    public readonly List<(string who, string text)> Transcript = new();
    public string Where = "", Clock = "", Topic = "";
    public int Day = -1;

    public Member? Get(string id) => Members.FirstOrDefault(m => m.Id == id);

    // ------------------------------------------------------------------ the request

    /// <param name="addressed">Who the player is talking to (member ids; empty = everyone in view).</param>
    /// <param name="present">Who's in view to answer or chime in (member ids, the addressed among them).</param>
    public LlmRequest Request(string playerLine, IReadOnlyCollection<string> addressed, IReadOnlyCollection<string> present,
        Func<string, string, PairBook.Pair?> pair, int maxTokens, string language = "", string turns = "")
    {
        var req = new LlmRequest { Purpose = "world", MaxTokens = maxTokens, SchemaName = "world" };
        var here = Members.Where(m => !m.Gone && present.Contains(m.Id)).ToList();
        var people = here.Select(m => new ChatterPrompts.Person { Record = m.Record, Snap = m.Snap, MaxLines = MaxLines }).ToList();
        req.System.Add("You play residents of the city of Nivalis, out and about, with a newcomer (the player) right there by them. It's a " +
                       "moment in passing, not a meeting: a few words, the way people talk standing around. They are real people with their " +
                       "own lives, not characters in a story.\n\n" + ChatterPrompts.City() + ChatterPrompts.Rules());
        var sb = new StringBuilder();
        sb.AppendLine("[THE PEOPLE]");
        for (int i = 0; i < people.Count; i++)
        {
            var m = here[i];
            string looks = m.Looks.Length > 0 ? $" Looks: {ReplyParser.Quotable(m.Looks, 200)}." : "";
            string role = addressed.Count == 0 || addressed.Contains(m.Id) ? " The newcomer is talking to them." : " Standing nearby, hearing it.";
            sb.AppendLine("- " + ChatterPrompts.Describe(people[i]) + looks + role);
        }
        if (people.Count > 1)
        {
            sb.AppendLine();
            sb.AppendLine("[HOW THEY KNOW EACH OTHER]");
            for (int i = 0; i < people.Count; i++)
                for (int j = i + 1; j < people.Count; j++)
                    sb.AppendLine("- " + ChatterPrompts.Between(people[i], people[j], pair(people[i].Record.Id, people[j].Record.Id)));
        }
        sb.AppendLine();
        sb.AppendLine("[THE NEWCOMER]");
        if (Prompts.PlayerPronounsNote() is { Length: > 0 } them) sb.AppendLine(them);
        foreach (var m in here)
        {
            var st = m.Record.State;
            var parts = new List<string> { st.TimesMet == 0 ? "has never talked with them" : $"has talked with them {st.TimesMet} time(s)" };
            if (st.PlayerName.Length > 0) parts.Add($"knows their name is {st.PlayerName}");
            if (Features.Romance && st.Romance.Length > 0) parts.Add(st.Romance == Romance.Living ? "is with them, and they live together" : "is with them (dating)");
            if (st.Hurts.Count > 0) parts.Add("found out: " + st.Hurts[^1]);
            sb.AppendLine($"- {m.Name} {string.Join("; ", parts)}.");
        }
        sb.AppendLine();
        sb.AppendLine("[WHERE AND WHEN]");
        sb.AppendLine($"{(Where.Length > 0 ? Where : "A street in Nivalis")}, day {Day}, {Clock}.");
        req.System.Add(sb.ToString());
        req.System.Add(AnswerFormat(language));
        if (CustomInstructions.Block() is { Length: > 0 } own) req.System.Add(own);
        if (language.Trim().Length == 0 && Prompts.SpeakIn() is { Length: > 0 } speakIn) req.System.Add(speakIn);
        req.System.Add(Prompts.SafetyLast);

        var said = new StringBuilder();
        var recent = Transcript.TakeLast(TranscriptKept).ToList();
        if (recent.Count > 0)
        {
            said.AppendLine("[JUST NOW]");
            foreach (var (who, text) in recent) said.AppendLine($"{NameOf(who)}: {text}");
            said.AppendLine();
        }
        string line = Yours(playerLine);
        if (line != Clean(playerLine)) said.Append(Roleplay.PlayerActionRule + "\n");
        var to = here.Where(m => addressed.Contains(m.Id)).Select(m => m.Name).ToList();
        string toWords = addressed.Count == 0 || to.Count == 0 ? "everyone around" : to.Count == 1 ? to[0] : string.Join(", ", to.Take(to.Count - 1)) + " and " + to[^1];
        said.Append($"The newcomer, to {toWords}: \"{line}\"\n\n");
        said.Append(turns.Length > 0 ? turns + $" What do they say? (at most {MaxLines} lines)" : $"Who answers, and what do they say? (0 to {MaxLines} lines)");
        req.Messages.Add(new LlmMessage("user", said.ToString()));
        req.JsonSchema = Schema();
        return req;
    }

    private string NameOf(string who) => who == PlayerId ? "The newcomer" : Get(who)?.Name ?? "Someone";

    // The 2026-10-20 audit: quoted as the one-to-one chat quotes you (brackets made round, breaks spaces), so typed text can't
    // pass as a [Scene: ...] note or a [[tag]], in this request or in what they remember of it.
    private static string Clean(string s) => ReplyParser.Quotable(s, 600).Replace('"', '\'');

    /// <summary>Your line as it's sent and kept: cleaned, then *actions* as [They do: ...] (CF-20).</summary>
    private static string Yours(string s) => Roleplay.PlayerActions(Clean(s));

    public static string AnswerFormat(string language = "") =>
        "Answer with one JSON object:\n" +
        $"- lines: 0 to {MaxLines} lines, in order. speaker: a name exactly as written above. to: who they're talking to (a name, \"newcomer\", " +
        "or empty for everyone). say: exactly what they say out loud. tone: one mood word. gesture: one only when it fits (" + GestureLibrary.List + "), usually empty. " +
        "leaving: true when, after this line, they walk on (with a reason from their own life in the line)." +
        (Features.Roleplay ? " action: now and then, a short third-person stage direction of what that speaker visibly does as they say it, in their own pronouns; usually empty." : "") + "\n" +
        "- feel: for each person, how what the newcomer just said changed how they feel about the newcomer, -10 to 10 (0 if it didn't).\n" +
        "- addressed: the names (as written above) of whoever the newcomer is talking to now, as you read their words: by name, by what they " +
        "look like (\"the one in the yellow coat\"), \"you two\", \"everyone\". Empty when their words don't say (then it's who you were told).\n" +
        $"- topic: what the newcomer is talking about, in a few words (\"the dumplings at the noodle bar\").\n" +
        "Who answers:\n" +
        "- The people the newcomer is talking to answer, one at a time. A closed question (yes or no, something only one of them can answer, or " +
        "one already answered) gets one answer, and the others stay quiet unless they'd honestly add something different. An open question to " +
        "several of them can get a word from each, never the same thing twice.\n" +
        "- Someone standing nearby who wasn't spoken to chimes in only when it's natural (a friend of theirs or of the newcomer, it's about them, " +
        "they can't help it); a stranger usually keeps to themselves.\n" +
        "- If the newcomer says they were talking to someone else (\"sorry, was talking to her\"), whoever that's said to reacts briefly in " +
        "character (sorry, a shrug) or says nothing.\n" +
        "- How warm they are comes from their mood, how they feel about the newcomer and how they know each other. Nobody speaks for the " +
        "newcomer, nobody repeats what someone else just said, and each person talks the way they talk." +
        (language.Trim().Length > 0 ? $"\nEveryone speaks {language.Trim()}." : "");

    public static JsonObject Schema()
    {
        JsonObject Str() => new() { ["type"] = "string" };
        JsonObject Obj(params (string name, JsonObject schema)[] props)
        {
            var p = new JsonObject();
            var req = new JsonArray();
            foreach (var (name, schema) in props) { p[name] = schema; req.Add(name); }
            return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = req, ["additionalProperties"] = false };
        }
        var line = new List<(string, JsonObject)> { ("speaker", Str()), ("to", Str()), ("say", Str()), ("tone", Str()), ("gesture", Str()), ("leaving", new JsonObject { ["type"] = "boolean" }) };
        if (Features.Roleplay) line.Add(("action", Str()));
        return Obj(
            ("lines", new JsonObject { ["type"] = "array", ["items"] = Obj(line.ToArray()) }),
            ("feel", new JsonObject { ["type"] = "array", ["items"] = Obj(("name", Str()), ("change", new JsonObject { ["type"] = "integer" })) }),
            ("addressed", new JsonObject { ["type"] = "array", ["items"] = Str() }),
            ("topic", Str()));
    }

    // ------------------------------------------------------------------ the answer

    /// <summary>The answer held to who's there: speakers only from <paramref name="present"/>, at most <see cref="MaxLines"/>.</summary>
    public Reply Parse(string text, IReadOnlyCollection<string> present)
    {
        var r = new Reply();
        var o = LlmClient.ExtractJsonObject(text ?? "");
        if (o == null) { r.Error = "the answer wasn't readable"; return r; }
        r.Ok = true;
        string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        if (o["lines"] is JsonArray lines)
            foreach (var item in lines)
            {
                if (item is not JsonObject l || r.Lines.Count >= MaxLines) continue;
                var who = Find(S(l["speaker"]));
                string say = S(l["say"]);
                if (who == null || who.Gone || !present.Contains(who.Id) || say.Length == 0) continue;
                if (say.Length > 600) say = say[..600];
                string to = S(l["to"]);
                var toMember = Find(to);
                r.Lines.Add(new GroupChat.Line
                {
                    SpeakerId = who.Id, Text = say, Mood = Moods.Normalize(S(l["tone"])), Gesture = S(l["gesture"]), Action = Features.Roleplay ? S(l["action"]) : "",
                    ToId = toMember != null ? toMember.Id : to.Equals("newcomer", StringComparison.OrdinalIgnoreCase) ? PlayerId : "",
                    Leaving = l["leaving"] is JsonValue lv && lv.TryGetValue<bool>(out var b) && b,
                });
            }
        if (o["feel"] is JsonArray feel)
            foreach (var item in feel)
            {
                if (item is not JsonObject f) continue;
                var who = Find(S(f["name"]));
                int ch = f["change"] is JsonValue cv && cv.TryGetValue<int>(out var c) ? c : 0;
                if (who != null && present.Contains(who.Id)) r.Feel[who.Id] = Math.Clamp(ch, -10, 10);
            }
        if (o["addressed"] is JsonArray adds)
            foreach (var item in adds)
                if (Find(S(item)) is { } m && present.Contains(m.Id) && !r.Addressed.Contains(m.Id)) r.Addressed.Add(m.Id);
        string topic = ReplyParser.Quotable(S(o["topic"]), MaxTopic);
        r.Topic = topic;
        return r;
    }

    /// <summary>A member by the name the AI used (full, or a first name only one of them has).</summary>
    public Member? Find(string name)
    {
        string n = (name ?? "").Trim().Trim('"', '\'', '.').ToLowerInvariant();
        if (n.Length == 0) return null;
        var full = Members.FirstOrDefault(m => m.Name.ToLowerInvariant() == n);
        if (full != null) return full;
        var first = Members.Where(m => Prompts.FirstName(m.Name).ToLowerInvariant() == n).ToList();
        return first.Count == 1 ? first[0] : null;
    }

    /// <summary>The answer heard: into the moment, who was talked to marked, leavers gone, feelings summed, the topic kept.</summary>
    public void Heard(string playerLine, IReadOnlyCollection<string> addressed, Reply reply, int played)
    {
        Transcript.Add((PlayerId, Yours(playerLine)));
        foreach (var id in addressed.Concat(reply.Addressed)) if (Get(id) is { } m) m.WasAddressed = true;
        foreach (var l in reply.Lines.Take(played))
        {
            Transcript.Add((l.SpeakerId, l.Text));
            if (l.Leaving && Get(l.SpeakerId) is { } m) m.Gone = true;
        }
        foreach (var (id, ch) in reply.Feel)
            if (Get(id) is { } m) m.Feel = Math.Clamp(m.Feel + ch, -30, 30);
        if (reply.Topic.Length > 0) Topic = reply.Topic;
        if (Transcript.Count > 80) Transcript.RemoveRange(0, Transcript.Count - 80);
    }

    // ------------------------------------------------------------------ remembering it

    /// <summary>One of their lines as it's shown: into the moment (and they've walked on, when it says so).</summary>
    public void Said(GroupChat.Line line)
    {
        Transcript.Add((line.SpeakerId, line.Text));
        if (line.Leaving && Get(line.SpeakerId) is { } m) m.Gone = true;
        if (Transcript.Count > 80) Transcript.RemoveRange(0, Transcript.Count - 80);
    }

    /// <summary>Took part: talked to, or said something in it.</summary>
    public bool TookPart(string id) => Get(id) is { } m && (m.WasAddressed || Transcript.Any(t => t.who == id));

    /// <summary>
    /// What one who took part keeps: the lines in their history (theirs as theirs, yours as the player's, others' as a scene),
    /// a meeting, one more time met, how they feel about you. False when there's nothing to keep.
    /// </summary>
    public bool Remember(NpcRecord rec, Func<int, double> step, DateTime now)
    {
        if (!Transcript.Any(t => t.who == PlayerId) || !TookPart(rec.Id)) return false;
        var me = Get(rec.Id)!;
        var others = Members.Where(m => m.Id != rec.Id && TookPart(m.Id)).Select(m => Prompts.FirstName(m.Name)).ToList();
        string with = others.Count == 0 ? "" : " with " + (others.Count == 1 ? others[0] : string.Join(", ", others.Take(others.Count - 1)) + " and " + others[^1]);
        rec.History.Add(new ChatLine { Role = "scene", Text = $"(Out and about{(Where.Length > 0 ? " at " + Where : "")}: the stranger stopped to talk{with}.)", GameDay = Day, Clock = Clock, Utc = now });
        foreach (var (who, text) in Transcript)
        {
            if (who == PlayerId) rec.History.Add(new ChatLine { Role = "player", Text = text, GameDay = Day, Clock = Clock, Utc = now });
            else if (who == rec.Id) rec.History.Add(new ChatLine { Role = "npc", Text = text, GameDay = Day, Clock = Clock, Utc = now });
            else rec.History.Add(new ChatLine { Role = "scene", Text = $"{Prompts.FirstName(NameOf(who))}: {text}", GameDay = Day, Clock = Clock, Utc = now });
        }
        int mine = Transcript.Count(t => t.who == rec.Id);
        rec.Meetings.Add(new Meeting
        {
            GameDay = Day, Clock = Clock, Location = Where,
            Summary = $"The stranger stopped to talk{with}{(Topic.Length > 0 ? ", about " + Topic : "")}{(mine == 0 ? "; I mostly listened" : "")}.",
        });
        rec.State.TimesMet++;
        rec.LastGameDay = Day;
        rec.LastTalkedUtc = now;
        if (Features.Relationships && me.Feel != 0) rec.State.Shift(step(Math.Clamp(me.Feel, -10, 10)));
        return true;
    }

    /// <summary>
    /// What one who only overheard keeps (the author: "any npc's that overhear would ofcourse record the moment as overheard x
    /// (stranger or friend) talking about x"): the last few lines, and a summary by how they know you.
    /// </summary>
    public (List<string> lines, string summary) Overheard(bool knowsYou, string yourName, Func<string, string>? nameFor = null)
    {
        string you = knowsYou ? (yourName.Length > 0 ? yourName : "the newcomer, someone I know") : "a stranger";
        var lines = Transcript.TakeLast(4).Select(t => t.who == PlayerId
            ? $"{(knowsYou && yourName.Length > 0 ? yourName : knowsYou ? "The newcomer" : "A stranger")}: {t.text}"
            : $"{nameFor?.Invoke(t.who) ?? "Someone"}: {t.text}").ToList();
        string summary = $"overheard {you} talking {(Topic.Length > 0 ? "about " + Topic : "with people nearby")}";
        return (lines, summary);
    }
}
