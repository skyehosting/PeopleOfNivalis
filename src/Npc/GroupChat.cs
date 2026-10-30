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
/// FB-66 (the author: "the npc's would be able to decide if they are being addressed, and who, and if they want to
/// chime in"): you join residents who are talking. One request per thing you say: everyone in it (who they are, how they
/// know each other and you), the conversation so far, and your words; the answer is 0-3 lines from them, in order, each
/// saying who it's to, plus how what you said moved each of them. What each remembers is written into their own record
/// when it ends: the lines in their history (so a one-on-one later knows it), a meeting, how they feel about you. No Unity.
/// </summary>
public sealed class GroupChat
{
    public sealed class Member
    {
        public NpcRecord Record = null!;
        public GameSnapshot Snap = new();
        /// <summary>How the player sees them on the window ("Ana", or "Stranger (woman)" for someone not yet known).</summary>
        public string Label = "";
        public bool Gone;
        /// <summary>How what you said moved them, summed over the conversation (-10..10 each time).</summary>
        public int Feel;
        public string Id => Record.Id;
        public string Name => Record.Persona.Name;
    }

    public sealed class Line
    {
        public string SpeakerId = "", ToId = "", Text = "", Mood = "", Gesture = "";
        /// <summary>Roleplay: what they visibly do as they say it, as the AI wrote it (cleaned when it's shown).</summary>
        public string Action = "";
        public bool Leaving;
    }

    public sealed class Reply
    {
        public bool Ok;
        public string Error = "";
        public List<Line> Lines = new();
        public Dictionary<string, int> Feel = new();

        /// <summary>
        /// The inner-life round: only the people the turn plan gave a turn (Mind.GroupTurns) speak, in the order written.
        /// When none of them did (the model ignored it), the answer stays as it was: nobody is silenced into nothing.
        /// </summary>
        public void KeepTo(ICollection<string> speakers)
        {
            if (speakers.Count == 0) return;
            var kept = Lines.Where(l => speakers.Contains(l.SpeakerId)).ToList();
            if (kept.Count > 0) Lines = kept;
        }
    }

    public const string PlayerId = "player";
    /// <summary>The most lines in one answer (the author: they decide whether to chime in; nobody holds a speech).</summary>
    public const int MaxLines = 3;
    /// <summary>How many lines of the conversation so far go into each request.</summary>
    public const int TranscriptKept = 30;

    public readonly List<Member> Members = new();
    /// <summary>(who: <see cref="PlayerId"/> or a member's id, what they said), in order.</summary>
    public readonly List<(string who, string text)> Transcript = new();
    public string PlayerName = "";
    public string Where = "";
    public int Day = -1;
    public string Clock = "";

    public IEnumerable<Member> Present => Members.Where(m => !m.Gone);

    // ------------------------------------------------------------------ the request

    /// <param name="turns">The inner-life round: who answers, who may chime in or cut in, decided in code (Mind.GroupTurns), or "".</param>
    public LlmRequest Request(string playerLine, Func<string, string, PairBook.Pair?> pair, int maxTokens, string language = "", string turns = "")
    {
        var req = new LlmRequest { Purpose = "group", MaxTokens = maxTokens, SchemaName = "group" };
        var people = Present.Select(m => new ChatterPrompts.Person { Record = m.Record, Snap = m.Snap, MaxLines = MaxLines }).ToList();
        req.System.Add("You play residents of the city of Nivalis having a conversation that a newcomer (the player) has just joined. " +
                       "They are real people with their own lives, not characters in a story.\n\n" + ChatterPrompts.City() + ChatterPrompts.Rules());
        var sb = new StringBuilder();
        sb.AppendLine("[THE PEOPLE]");
        foreach (var p in people) sb.AppendLine("- " + ChatterPrompts.Describe(p));
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
        if (Prompts.PlayerPronounsNote() is { Length: > 0 } them) sb.AppendLine(them); // CF-17
        foreach (var m in Present)
        {
            var st = m.Record.State;
            string name = st.PlayerName.Length > 0 ? st.PlayerName : "";
            var parts = new List<string>();
            parts.Add(st.TimesMet == 0 ? "has never talked with them" : $"has talked with them {st.TimesMet} time(s)");
            if (name.Length > 0) parts.Add($"knows their name is {name}");
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
        if (language.Trim().Length == 0 && Prompts.SpeakIn() is { Length: > 0 } speakIn) req.System.Add(speakIn); // ReplyLanguage
        req.System.Add(Prompts.SafetyLast);

        var said = new StringBuilder();
        var recent = Transcript.TakeLast(TranscriptKept).ToList();
        if (recent.Count > 0)
        {
            said.AppendLine("[THE CONVERSATION SO FAR]");
            foreach (var (who, text) in recent) said.AppendLine($"{NameOf(who)}: {text}");
            said.AppendLine();
        }
        // CF-20: what they write in *asterisks* is something they do (with Roleplay on), told to everyone as such.
        string line = Yours(playerLine);
        if (line != Clean(playerLine)) said.Append(Roleplay.PlayerActionRule + "\n");
        said.Append($"The newcomer says: \"{line}\"\n\n");
        said.Append(turns.Length > 0 ? turns + $" What do they say? (at most {MaxLines} lines)" : $"Who answers, and what do they say? (0 to {MaxLines} lines)");
        req.Messages.Add(new LlmMessage("user", said.ToString()));
        req.JsonSchema = Schema();
        return req;
    }

    private string NameOf(string who) => who == PlayerId ? "The newcomer" : Members.FirstOrDefault(m => m.Id == who)?.Name ?? "Someone";

    // The 2026-10-20 audit: quoted as the one-to-one chat quotes you (brackets made round, breaks spaces), so typed text can't
    // pass as a [Scene: ...] note or a [[tag]], in this request or in what they remember of it.
    private static string Clean(string s) => ReplyParser.Quotable(s, 600).Replace('"', '\'');

    /// <summary>Your line as it's sent and kept: cleaned, then *actions* as [They do: ...] (CF-20).</summary>
    private static string Yours(string s) => Roleplay.PlayerActions(Clean(s));

    public static string AnswerFormat(string language = "") =>
        "Answer with one JSON object:\n" +
        $"- lines: 0 to {MaxLines} lines, in order. speaker: a name exactly as written above. to: who they're talking to (a name, \"newcomer\", " +
        "or empty for everyone). say: exactly what they say out loud. tone: one mood word. gesture: one only when it fits (" + GestureLibrary.List + "), usually empty. " +
        "leaving: true when, after this line, they leave the conversation (with a reason from their own life in the line)." +
        (Features.Roleplay ? " action: now and then, a short third-person stage direction of what that speaker visibly does as they say it (\"She rolls her eyes.\"), in their own pronouns; usually empty." : "") + "\n" +
        "- feel: for each person, how what the newcomer just said changed how they feel about the newcomer, -10 to 10 (0 if it didn't).\n" +
        "Who answers: whoever the newcomer spoke to (by name, or plainly by what they said); the others chime in only when it's natural " +
        "(a joke, a correction, something they care about), and stay quiet otherwise. If the newcomer spoke to everyone, one or two answer. " +
        "Nobody speaks for the newcomer, nobody repeats what someone else just said, and each person talks the way they talk. They can talk " +
        "to each other too, as people in a group do." +
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
        if (Features.Roleplay) line.Add(("action", Str())); // what they visibly do as they say it
        return Obj(
            ("lines", new JsonObject { ["type"] = "array", ["items"] = Obj(line.ToArray()) }),
            ("feel", new JsonObject { ["type"] = "array", ["items"] = Obj(("name", Str()), ("change", new JsonObject { ["type"] = "integer" })) }));
    }

    // ------------------------------------------------------------------ the answer

    /// <summary>The answer's lines from people in it (unknown speakers dropped, at most <see cref="MaxLines"/>), and how each was moved.</summary>
    public Reply Parse(string text)
    {
        var r = new Reply();
        var o = LlmClient.ExtractJsonObject(text);
        if (o == null) { r.Error = "the answer wasn't readable"; return r; }
        r.Ok = true;
        if (o["lines"] is JsonArray lines)
            foreach (var item in lines)
            {
                if (item is not JsonObject l || r.Lines.Count >= MaxLines) continue;
                string S(string k) => l[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
                var who = Find(S("speaker"));
                string say = S("say");
                if (who == null || who.Gone || say.Length == 0) continue;
                if (say.Length > 600) say = say[..600];
                string to = S("to");
                var toMember = Find(to);
                r.Lines.Add(new Line
                {
                    SpeakerId = who.Id, Text = say, Mood = Moods.Normalize(S("tone")), Gesture = S("gesture"), Action = Features.Roleplay ? S("action") : "",
                    ToId = toMember != null ? toMember.Id : to.Equals("newcomer", StringComparison.OrdinalIgnoreCase) ? PlayerId : "",
                    Leaving = l["leaving"] is JsonValue lv && lv.TryGetValue<bool>(out var b) && b,
                });
            }
        if (o["feel"] is JsonArray feel)
            foreach (var item in feel)
            {
                if (item is not JsonObject f) continue;
                var who = Find(f["name"] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n : "");
                int ch = f["change"] is JsonValue cv && cv.TryGetValue<int>(out var c) ? c : 0;
                if (who != null) r.Feel[who.Id] = Math.Clamp(ch, -10, 10);
            }
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

    /// <summary>The answer heard: its lines into the conversation, how each was moved kept for the end, leavers gone.</summary>
    public void Heard(string playerLine, Reply reply, int played)
    {
        Transcript.Add((PlayerId, Yours(playerLine)));
        foreach (var l in reply.Lines.Take(played))
        {
            Transcript.Add((l.SpeakerId, l.Text));
            if (l.Leaving && Members.FirstOrDefault(m => m.Id == l.SpeakerId) is { } m) m.Gone = true;
        }
        foreach (var (id, ch) in reply.Feel)
            if (Members.FirstOrDefault(m => m.Id == id) is { } m) m.Feel = Math.Clamp(m.Feel + ch, -30, 30);
    }

    // ------------------------------------------------------------------ remembering it

    /// <summary>
    /// What <paramref name="rec"/> (one of the members) keeps of it: the lines in their history (theirs as theirs, the
    /// newcomer's as the player's, the others' as a scene), a meeting, one more time met, and how they feel about you.
    /// False when there's nothing to keep (the newcomer never said anything).
    /// </summary>
    public bool Remember(NpcRecord rec, Func<int, double> step, DateTime now)
    {
        if (!Transcript.Any(t => t.who == PlayerId)) return false;
        var me = Members.FirstOrDefault(m => m.Id == rec.Id);
        if (me == null) return false;
        var others = Members.Where(m => m.Id != rec.Id).Select(m => Prompts.FirstName(m.Name)).ToList();
        string withWhom = others.Count == 0 ? "" : others.Count == 1 ? others[0] : string.Join(", ", others.Take(others.Count - 1)) + " and " + others[^1];
        rec.History.Add(new ChatLine { Role = "scene", Text = $"(A conversation with {withWhom} that the stranger joined.)", GameDay = Day, Clock = Clock, Utc = now });
        foreach (var (who, text) in Transcript)
        {
            if (who == PlayerId) rec.History.Add(new ChatLine { Role = "player", Text = text, GameDay = Day, Clock = Clock, Utc = now });
            else if (who == rec.Id) rec.History.Add(new ChatLine { Role = "npc", Text = text, GameDay = Day, Clock = Clock, Utc = now });
            else rec.History.Add(new ChatLine { Role = "scene", Text = $"{Prompts.FirstName(NameOf(who))}: {text}", GameDay = Day, Clock = Clock, Utc = now });
        }
        int theirLines = Transcript.Count(t => t.who == rec.Id);
        rec.Meetings.Add(new Meeting
        {
            GameDay = Day, Clock = Clock, Location = Where,
            Summary = $"The stranger joined me and {withWhom}{(theirLines == 0 ? "; I mostly listened" : "")}.",
        });
        rec.State.TimesMet++;
        rec.LastGameDay = Day;
        rec.LastTalkedUtc = now;
        if (Features.Relationships && me.Feel != 0) rec.State.Shift(step(Math.Clamp(me.Feel, -10, 10)));
        return true;
    }
}
