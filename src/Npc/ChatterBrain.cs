using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Social;

namespace NivalisAIChat.Npc;

/// <summary>Someone taking part in an NPC conversation: who, what the game showed of them, and their seat in the plan.</summary>
public sealed class ChatterMember
{
    public string Id = "";
    public NpcVisual Visual = new();
    public GameSnapshot Snap = new();
    public int JoinAt;
    public int MaxLines = 3;
    public bool Seated;
    public bool WalkedOver;
    /// <summary>FB-18: sat down at the table since the last part of their talk.</summary>
    public bool JustSat;
}

public sealed class ChatterLine
{
    public string SpeakerId = "";
    public string ToId = "";
    public string Text = "";
    /// <summary>Their goodbye: after this line they leave the group.</summary>
    public bool Leaving;
    /// <summary>How they feel saying it (a mood word, as written).</summary>
    public string Mood = "";
    /// <summary>A gesture the AI cued with the line (GestureLibrary), or "" (CF-04).</summary>
    public string Gesture = "";
    /// <summary>FB-19: said into the phone (a call at a table).</summary>
    public bool Phone;
    /// <summary>FB-19: the other end of the call speaking (<see cref="ChatterScript.FarEndId"/>); heard only on speaker.</summary>
    public bool FarEnd;
    /// <summary>FB-19b: the other end, heard by everyone on speaker.</summary>
    public bool Speakerphone;
    /// <summary>How they sound by this line: their tone so far in the conversation, moved a little by this line's mood.</summary>
    public Voice.ToneState? Tone;
}

/// <summary>One line of a phone call: the caller's (overheard) or the other side's (not heard, only waited through).</summary>
public sealed class PhoneLine
{
    public bool Caller;
    public string Text = "";
    public string Mood = "";
    public Voice.ToneState? Tone;
}

/// <summary>Each speaker's tone through a conversation: from their mood as it began, moved a little by each line they say.</summary>
public static class LineTones
{
    public static void Apply<T>(IEnumerable<T> lines, Func<T, string> speaker, Func<T, string> mood, Action<T, Voice.ToneState> set, Func<string, NpcState?> start)
    {
        var tones = new Dictionary<string, Voice.ToneState>();
        foreach (var l in lines)
        {
            string who = speaker(l);
            if (!tones.TryGetValue(who, out var t))
            {
                var st = start(who);
                t = Voice.Tone.Start(Features.Mood(st?.Mood), st?.MoodIntensity);
                tones[who] = t;
            }
            Voice.Tone.Feel(t, mood(l).Length > 0 ? mood(l) : t.Lead, "moderate");
            set(l, t.Copy());
        }
    }
}

/// <summary>A phone call between a resident near the player and someone they know, written in one go.</summary>
public sealed class PhoneScript
{
    public NpcRecord Caller = null!;
    public ChatterPrompts.Callee Other = new();
    public GameSnapshot Snap = new();
    public List<PhoneLine> Lines = new();
    public string Summary = "";
    public int Change;
    /// <summary>The person they rang was busy (talking to someone, on another call): nobody picked up.</summary>
    public bool Failed;
    /// <summary>What the caller says if they end the call early (you walked off), with a reason from their own life.</summary>
    public string Exit = "";
    /// <summary>Each resident's epoch when the call was written: a reset of one of them since then means nothing is kept for them.</summary>
    public Dictionary<string, long> Epochs = new();

    private static readonly string[] PlainExits =
    {
        "Look, I've got to go. I'll call you back.",
        "Sorry, something's come up. Talk later.",
        "I need to run. Call you tonight.",
        "Hang on, I have to go. Speak soon.",
    };

    /// <summary>Ending the call early: their own reason if it was written, else a plain one.</summary>
    public string ExitLine(Random rng) => Exit.Length > 0 ? Exit : PlainExits[rng.Next(PlainExits.Length)];

    private static readonly (string first, string last)[] NoAnswer =
    {
        ("Come on, pick up...", "Voicemail. Of course."),
        ("Ringing... ringing...", "Nothing. Typical."),
        ("Pick up, pick up.", "Straight to voicemail. Great."),
        ("Busy again?", "Fine. Call me back, then."),
        ("Hey, it's me... oh. Voicemail.", "I'll try later."),
    };

    /// <summary>The caller rings, waits, gives up: no AI needed.</summary>
    public static PhoneScript Unanswered(NpcRecord caller, ChatterPrompts.Callee other, GameSnapshot snap, Random rng)
    {
        var (first, last) = NoAnswer[rng.Next(NoAnswer.Length)];
        return new PhoneScript
        {
            Caller = caller, Other = other, Snap = snap, Failed = true,
            Summary = $"They tried to phone {other.Name}, who didn't pick up.",
            Lines = new List<PhoneLine>
            {
                new() { Caller = true, Text = first },
                new() { Caller = false, Text = "(ringing, no answer)" },
                new() { Caller = true, Text = last },
            },
        };
    }
}

public sealed class ChatterScript
{
    public List<ChatterLine> Lines = new();
    public string Summary = "";
    /// <summary>The speaker id of the other end of a call at a table (FB-19): not anyone at the table.</summary>
    public const string FarEndId = "~phone";
    /// <summary>FB-19: the call at this table, if there is one.</summary>
    public ChatterPrompts.TableCall? Call;
    /// <summary>
    /// For each person: what they'd say if they had to go right now (a reason from their own life), and a short goodbye
    /// when someone else goes. Used when the player walks off mid-conversation and they wrap it up.
    /// </summary>
    public Dictionary<string, (string Leave, string Bye)> Exits = new();
    /// <summary>Each speaker's epoch when it was written: a reset of one of them since then means nothing is kept for them.</summary>
    public Dictionary<string, long> Epochs = new();

    private static readonly string[] PlainLeaves =
    {
        "Anyway, I've got to get going. Catch you later.",
        "Listen, I need to run. We'll talk soon.",
        "I should head off, I'm already late. See you.",
        "Right, I've got somewhere to be. Later.",
    };
    private static readonly string[] PlainByes = { "Yeah, see you.", "Alright, take care.", "Sure, later.", "Go on, see you around." };

    public string LeaveLine(string id, Random rng) =>
        Exits.TryGetValue(id, out var e) && e.Leave.Length > 0 ? e.Leave : PlainLeaves[rng.Next(PlainLeaves.Length)];

    public string ByeLine(string id, Random rng) =>
        Exits.TryGetValue(id, out var e) && e.Bye.Length > 0 ? e.Bye : PlainByes[rng.Next(PlainByes.Length)];
    /// <summary>Everyone in it, as they were when it was written (for names, voices and the bubbles).</summary>
    public Dictionary<string, NpcRecord> People = new();
    /// <summary>The writer's whole answer and the people as planned, kept for remembering it once it has been played.</summary>
    internal string Raw = "";
    internal List<ChatterPrompts.Person> Persons = new();
    /// <summary>The inner-life round: what the code decided someone may pass on (heard by the others once it's been said).</summary>
    internal List<Mind.Telling.Plan> Tellings = new();
    /// <summary>FB-63: the turn of a romance the code decided this conversation plays out (kept once it's been heard).</summary>
    internal CoupleBook.Turn? Turn;
}

/// <summary>
/// Conversations between residents (no Unity here): makes sure everyone is someone, has the whole exchange written in
/// one request, checks it against the plan (who may speak when, how many lines, a goodbye to finish), and remembers it:
/// a line in each person's life, how the pair gets on, and anything they heard about the player.
/// </summary>
public sealed partial class ChatterBrain
{
    private readonly NpcBrain _brain;
    private readonly LlmClient _llm;
    private readonly PairBook _pairs;
    private readonly Action<string> _log;

    public ChatterBrain(NpcBrain brain, LlmClient llm, PairBook pairs, Action<string> log, Action<string>? problem = null)
    {
        _brain = brain;
        _llm = llm;
        _pairs = pairs;
        _log = log;
        _problem = problem ?? log;
    }

    /// <summary>An AI failure the player should be able to find (the console's /errors).</summary>
    private readonly Action<string> _problem;

    public PairBook Pairs => _pairs;

    /// <summary>FB-35: groups of 3-4 who've talked together (set by the mod; none in tests that don't need it).</summary>
    public CircleBook? Circles { get; set; }

    /// <summary>FB-63: residents' couples and secrets (set by the mod; none in tests that don't need it).</summary>
    public CoupleBook? Couples { get; set; }

    /// <summary>FB-63: whether residents may get together (ResidentsDate) and be jealous (Jealousy), read when a conversation is written.</summary>
    public Func<(bool date, bool jealousy)> RomanceRules { get; set; } = () => (false, false);

    /// <summary>The inner-life round: feelings and what's on their minds (minds.json), what they saw (witness.json); null in tests that don't need them.</summary>
    public Mind.MindBook? Minds { get; set; }
    public Mind.WitnessBook? Witness { get; set; }
    /// <summary>Whether gossip is told in code (GossipSpreads) and one-line mutters are asked for three ways (VaryLines); read when it's written.</summary>
    public Func<(bool gossip, bool vary)> InnerRules { get; set; } = () => (false, false);
    private readonly Random _tellRng = new();

    /// <summary>
    /// The inner-life round: each person's feelings now and what's on their mind, what each wants from this talk, and what
    /// someone may pass on (never with anyone under 18). Nothing when InnerLife is off.
    /// </summary>
    private (List<string> wants, List<Mind.Telling.Plan> tellings) InnerFor(List<ChatterPrompts.Person> people)
    {
        var wants = new List<string>();
        var tellings = new List<Mind.Telling.Plan>();
        if (!Features.InnerLife || people.Count < 2 || people.Any(p => _brain.KnownMinors.Contains(p.Record.Id))) return (wants, tellings);
        try
        {
            long now = Mind.GameClock.Of(people[0].Snap);
            foreach (var p in people)
            {
                if (Minds?.MoodNow(p.Record.Id, now) is { } carried) { p.Mood = carried.mood; p.MoodReason = carried.reason; }
                p.OnMind = Minds?.Current(p.Record.Id)?.Text ?? "";
            }
            wants = Mind.Volitions.For(people.Select(p => new Mind.Volitions.Who
            {
                Id = p.Record.Id, Name = p.Name, Mood = Features.Mood(p.Mood.Length > 0 ? p.Mood : p.Record.State.Mood), OnMind = p.OnMind,
            }).ToList(), _pairs.Get);
            var (gossip, _) = InnerRules();
            if (gossip && Witness != null)
                lock (_tellRng)
                    tellings = Mind.Telling.Pick(Witness, people.Select(p => new Mind.Telling.Speaker { Id = p.Record.Id, Name = p.Name, Persona = p.Record.Persona }).ToList(),
                        _pairs.Get, now, _tellRng);
        }
        catch (Exception e) { _log("Reading what's on their minds failed: " + e.Message); }
        return (wants, tellings);
    }

    private readonly Random _romanceRng = new();

    /// <summary>The couples among them and the turn of a romance, decided before it's written (never with anyone under 18).</summary>
    private (List<string> notes, CoupleBook.Turn? turn) RomanceFor(IReadOnlyList<ChatterPrompts.Person> people)
    {
        var book = Couples;
        if (book == null || people.Count < 2 || people.Any(p => _brain.KnownMinors.Contains(p.Record.Id))) return (new List<string>(), null);
        try
        {
            var (date, jealousy) = RomanceRules();
            var who = people.Select(p => new CoupleBook.Person { Id = p.Record.Id, Name = p.Name }).ToList();
            lock (_romanceRng) return book.Scene(who, _pairs.Get, people[0].Snap.GameDay, _romanceRng, date, jealousy);
        }
        catch (Exception e) { _log("Reading who's together failed: " + e.Message); return (new List<string>(), null); }
    }

    /// <summary>The circle these people are in, for the writer (never one with someone known to be under 18).</summary>
    private CircleBook.Circle? CircleOf(IReadOnlyList<ChatterPrompts.Person> people)
    {
        if (Circles == null || people.Count < 2) return null;
        try
        {
            var c = Circles.Best(people.Select(p => p.Record.Id).ToList());
            return c != null && !c.Ids.Any(_brain.KnownMinors.Contains) ? c : null;
        }
        catch (Exception e) { _log("Reading the group's memory failed: " + e.Message); return null; }
    }

    /// <param name="carryOn">FB-18: what the same table was just talking about (the next part carries on from it).</param>
    public async Task<ChatterScript?> WriteAsync(IReadOnlyList<ChatterMember> members, bool table, IReadOnlyList<string> headlines,
        IReadOnlyList<string> rumors, int maxTokens, CancellationToken ct, string carryOn = "")
    {
        if (members.Count < 2) return null;
        var epochs = EpochsOf(members.Select(m => m.Id)); // before anyone's record is read
        var recs = await Task.WhenAll(members.Select(m => _brain.EnsurePersonAsync(m.Id, m.Visual, m.Snap, ct))).ConfigureAwait(false);
        if (recs.Any(r => r == null)) { _log("A conversation between residents was dropped: someone couldn't be created."); return null; }
        var people = members.Select((m, i) => new ChatterPrompts.Person
        {
            Record = recs[i]!, Snap = m.Snap, JoinAt = m.JoinAt, MaxLines = m.MaxLines, Seated = m.Seated, WalkedOver = m.WalkedOver, JustSat = m.JustSat,
        }).ToList();

        var (romance, turn) = RomanceFor(people);
        var (wants, tellings) = InnerFor(people);
        var req = ChatterPrompts.ChatterRequest(people, _pairs.Get, table, headlines, rumors, maxTokens, carryOn: carryOn, circle: CircleOf(people), romance: romance,
            tellings: tellings.Select(t => t.Note).ToList(), wants: wants);
        var res = await _llm.CompleteAsync(req, ct).ConfigureAwait(false);
        if (!res.Ok) { _problem("A conversation between residents couldn't be written: " + res.Error); return null; }
        var script = ParseScript(res.Text, people, stay: table); // at a table nobody leaves (FB-18)
        if (script != null) { script.Turn = turn; script.Tellings = tellings; }
        if (script == null) { _log("A conversation between residents came back unusable; skipped."); return null; }
        if (ct.IsCancellationRequested) return null; // broken up (or everyone was reset) while it was being written
        script.Raw = res.Text;
        script.Persons = people;
        script.Epochs = epochs;
        _log($"Residents talking ({string.Join(", ", people.Select(p => p.Name))}): {script.Lines.Count} lines. {script.Summary}");
        return script;
    }

    /// <summary>
    /// FB-19: a table conversation a phone call interrupts. The code decides who gets it (one of them, at random), who's
    /// calling (someone they know: <see cref="PickCallee"/>), whether they answer (50/50, the author's), and whether it may
    /// go on speaker (they all know the caller, a resident). Nobody to call, or the caller busy: an ordinary conversation.
    /// </summary>
    public async Task<ChatterScript?> WriteTableCallAsync(IReadOnlyList<ChatterMember> members, IReadOnlyList<string> headlines,
        IReadOnlyList<string> rumors, int maxTokens, IReadOnlyCollection<string> busy, Random rng, CancellationToken ct, string carryOn = "")
    {
        if (members.Count < 2) return null;
        var epochs = EpochsOf(members.Select(m => m.Id));
        var recs = await Task.WhenAll(members.Select(m => _brain.EnsurePersonAsync(m.Id, m.Visual, m.Snap, ct))).ConfigureAwait(false);
        if (recs.Any(r => r == null)) { _log("A conversation between residents was dropped: someone couldn't be created."); return null; }
        var people = members.Select((m, i) => new ChatterPrompts.Person
        {
            Record = recs[i]!, Snap = m.Snap, JoinAt = m.JoinAt, MaxLines = m.MaxLines, Seated = m.Seated, WalkedOver = m.WalkedOver, JustSat = m.JustSat,
        }).ToList();
        // The phone is someone's at the table from the start (review: never a joiner still on their way, or someone standing).
        var atTable = people.Where(p => p.JoinAt == 0 && p.Seated).ToList();
        var holderPerson = (atTable.Count > 0 ? atTable : people.Take(2).ToList())[rng.Next(atTable.Count > 0 ? atTable.Count : Math.Min(2, people.Count))];
        var holder = holderPerson.Record;
        holderPerson.MaxLines += 3; // their lines into the phone come on top of their share of the table talk
        foreach (var known in _pairs.For(holder.Id)) // everyone they might be rung by, before PickCallee reads their records (review)
        {
            string them = known.A == holder.Id ? known.B : known.A;
            epochs.TryAdd(them, _brain.EpochFor(them));
        }
        var callee = PickCallee(holder, rng);
        if (callee == null || (callee.Record != null && (busy.Contains(callee.Record.Id) || people.Any(p => p.Record.Id == callee.Record.Id) || _brain.IsKnownMinor(callee.Record))))
            return await WriteAsync(members, true, headlines, rumors, maxTokens, ct, carryOn).ConfigureAwait(false); // nobody to ring them: just talk
        if (callee.Record != null) epochs.TryAdd(callee.Record.Id, _brain.EpochFor(callee.Record.Id));
        bool answered = rng.NextDouble() < 0.5;
        bool speaker = answered && callee.Record != null && people.Where(p => p.Record.Id != holder.Id).All(p => _pairs.Get(p.Record.Id, callee.Record.Id) != null);
        var call = new ChatterPrompts.TableCall { HolderId = holder.Id, HolderName = holder.Persona.Name, Callee = callee, Answered = answered, Speaker = speaker };
        var (wants, tellings) = InnerFor(people);
        var res = await _llm.CompleteAsync(ChatterPrompts.ChatterRequest(people, _pairs.Get, true, headlines, rumors, maxTokens, call, carryOn, CircleOf(people),
            tellings: tellings.Select(t => t.Note).ToList(), wants: wants), ct).ConfigureAwait(false);
        if (!res.Ok) { _problem("A conversation between residents couldn't be written: " + res.Error); return null; }
        if (callee.Record != null && _brain.EpochFor(callee.Record.Id) != epochs[callee.Record.Id]) return null; // reset meanwhile
        var script = ParseTableCall(res.Text, people, call);
        if (script == null) { _log("A conversation with a phone call came back unusable; skipped."); return null; }
        if (ct.IsCancellationRequested) return null;
        script.Raw = res.Text;
        script.Persons = people;
        script.Epochs = epochs;
        script.Tellings = tellings;
        _log($"Residents talking ({string.Join(", ", people.Select(p => p.Name))}), and {holder.Persona.Name}'s phone goes off: {callee.Name} " +
             $"({(answered ? speaker ? "answered, may go on speaker" : "answered" : "not answered")}). {script.Lines.Count} lines. {script.Summary}");
        return script;
    }

    /// <summary>
    /// FB-19: the table conversation with its call, read the way the code decided it: the caller's lines (by their name)
    /// become the far end's, heard only on speaker when it was allowed; no call lines when they didn't answer; nobody
    /// leaves the table (no goodbye is guessed from someone's last line, unlike an ordinary conversation, where "talk later"
    /// to the caller would have them walk off). Null unless at least two people at the table say something.
    /// </summary>
    public static ChatterScript? ParseTableCall(string text, IReadOnlyList<ChatterPrompts.Person> people, ChatterPrompts.TableCall call)
    {
        var o = LlmClient.ExtractJsonObject(text);
        if (o?["lines"] is not JsonArray arr) return null;
        string calleeName = call.Callee.Name;
        string? Who(string name)
        {
            string n = (name ?? "").Trim().Trim('"', '\'', '.').ToLowerInvariant();
            if (n.Length == 0) return null;
            // Full names (and nicknames) first; a first name alone only when it fits exactly one person, the caller included
            // (audit B11: "Teo" on the phone isn't Teo Marsh at the table).
            if (calleeName.Length > 0 && calleeName.ToLowerInvariant() == n) return ChatterScript.FarEndId;
            var full = SpeakerByName(people, n);
            if (full != null) return full;
            var byFirst = people.Where(p => Prompts.FirstName(p.Record.Persona.Name).ToLowerInvariant() == n).Select(p => p.Record.Id).ToList();
            if (calleeName.Length > 0 && Prompts.FirstName(calleeName).ToLowerInvariant() == n) byFirst.Add(ChatterScript.FarEndId);
            return byFirst.Count == 1 ? byFirst[0] : null;
        }
        var lines = new List<ChatterLine>();
        foreach (var item in arr)
        {
            if (item is not JsonObject || lines.Count >= 30) continue;
            string S(string k) => item[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            bool Flag(string k) => item[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            var id = Who(S("speaker"));
            if (id == null) continue;
            bool far = id == ChatterScript.FarEndId;
            if (far && !call.Answered) continue; // nothing is said on a call nobody took
            var rec = far ? call.Callee.Record : people.First(p => p.Record.Id == id).Record;
            string name = rec?.Persona.Name ?? calleeName, nick = rec?.Persona.Nickname ?? "";
            var cleaned = ReplyParser.Parse(S("say"), name, nick).Spoken.Trim().Trim('"', '\u201C', '\u201D');
            if (cleaned.Length == 0 || cleaned.Length > 400 || ReplyParser.LooksOutOfCharacter(cleaned, rec?.Visual.IsAndroid ?? false)) continue;
            lines.Add(new ChatterLine
            {
                SpeakerId = id, ToId = far ? "" : Who(S("to")) ?? "", Text = cleaned, Leaving = false, Mood = Moods.Normalize(S("tone")),
                Gesture = far ? "" : GestureLibrary.Normalize(S("gesture")), FarEnd = far,
                Phone = !far && id == call.HolderId && call.Answered && Flag("phone"),
                Speakerphone = far && call.Speaker && Flag("speakerphone"),
            });
        }
        // Each person's share, as in any conversation (review: LinesEach applies), and nobody before they've come over; the far
        // end at most as many lines as the holder said into the phone, plus one.
        var kept = new List<ChatterLine>();
        foreach (var l in lines)
        {
            var person = l.FarEnd ? null : people.First(p => p.Record.Id == l.SpeakerId);
            if (person != null && kept.Count < person.JoinAt) continue;
            int max = person == null ? lines.Count(x => x.Phone) + 1 : Math.Max(1, person.MaxLines);
            if (kept.Count(x => x.SpeakerId == l.SpeakerId) < max) kept.Add(l);
        }
        lines = kept;
        if (lines.Where(l => !l.FarEnd).Select(l => l.SpeakerId).Distinct().Count() < 2) return null;
        var script = new ChatterScript
        {
            Lines = lines,
            Summary = o["summary"] is JsonValue sv && sv.TryGetValue<string>(out var sum) ? sum.Trim() : "",
            Call = call,
        };
        foreach (var p in people) script.People[p.Record.Id] = p.Record;
        LineTones.Apply(lines, l => l.SpeakerId, l => l.Mood, (l, t) => l.Tone = t,
            id => id == ChatterScript.FarEndId ? call.Callee.Record?.State : script.People.TryGetValue(id, out var r) ? r.State : null);
        if (o["exits"] is JsonArray exits)
            foreach (var e in exits)
            {
                if (e is not JsonObject) continue;
                string S(string k) => e[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
                var id = Who(S("speaker"));
                if (id == null || id == ChatterScript.FarEndId || script.Exits.ContainsKey(id)) continue;
                var rec = people.First(p => p.Record.Id == id).Record;
                string Clean(string t)
                {
                    var c = ReplyParser.Parse(t, rec.Persona.Name, rec.Persona.Nickname).Spoken.Trim().Trim('"', '\u201C', '\u201D');
                    return c.Length is > 0 and <= 240 && !ReplyParser.LooksOutOfCharacter(c, rec.Visual.IsAndroid) ? c : "";
                }
                script.Exits[id] = (Clean(S("leave")), Clean(S("bye")));
            }
        return script;
    }

    /// <summary>
    /// FB-19: the one who took the call keeps it as a call (what they said into the phone and what came back), as their
    /// other calls are kept. Only what was actually said; not after a reset of them; never for someone under 18.
    /// </summary>
    public Task RememberTableCallAsync(ChatterScript script, int linesPlayed)
    {
        var call = script.Call;
        if (call == null || !call.Answered || linesPlayed <= 0) return Task.CompletedTask;
        var said = script.Lines.Take(linesPlayed).Where(l => l.Phone || l.FarEnd).ToList();
        if (said.Count == 0) return Task.CompletedTask;
        var place = script.Persons.FirstOrDefault()?.Snap ?? new GameSnapshot();
        long epoch = script.Epochs.TryGetValue(call.HolderId, out var e) ? e : _brain.EpochFor(call.HolderId);
        return _brain.Mutate(call.HolderId, rec =>
        {
            if (_brain.EpochFor(call.HolderId) != epoch || _brain.IsKnownMinor(rec)) return false;
            rec.AddTalk(new Talk
            {
                GameDay = place.GameDay, Clock = place.Clock, Where = place.Location, Kind = Talk.Phone, With = call.Callee.Name,
                Lines = said.Select(l => $"{(l.FarEnd ? Prompts.FirstName(call.Callee.Name) : Prompts.FirstName(rec.Persona.Name))}: {l.Text}").ToList(),
                Summary = $"took a call from {call.Callee.Name} while at a table with friends",
            });
            return true;
        });
    }

    /// <summary>
    /// What stays with them, once it has actually been said: only the first <paramref name="linesPlayed"/> lines count
    /// (a conversation broken up before anyone spoke leaves no memory).
    /// </summary>
    public Task RememberAsync(ChatterScript script, int linesPlayed)
    {
        if (linesPlayed <= 0 || script.Persons.Count == 0) return Task.CompletedTask;
        var played = new ChatterScript { Lines = script.Lines.Take(linesPlayed).ToList(), Summary = script.Summary, People = script.People, Epochs = script.Epochs,
                                         Persons = script.Persons, Raw = script.Raw, Turn = script.Turn, Tellings = script.Tellings }; // how it went for each (FB-35) reads these
        return RememberAsync(script.Persons, played, script.Raw);
    }

    /// <summary>
    /// The written conversation, checked against the plan: unknown speakers and anyone speaking before they've joined are
    /// dropped; someone with too many lines keeps their first ones and their goodbye; each person's last line is their
    /// goodbye. Null unless at least two people say something.
    /// </summary>
    /// <param name="stay">At a table (FB-18): nobody leaves; no line is taken for a goodbye.</param>
    public static ChatterScript? ParseScript(string text, IReadOnlyList<ChatterPrompts.Person> people, bool stay = false)
    {
        var o = LlmClient.ExtractJsonObject(text);
        if (o?["lines"] is not JsonArray arr) return null;
        string? Who(string name)
        {
            string n = (name ?? "").Trim().Trim('"', '\'', '.').ToLowerInvariant();
            if (n.Length == 0) return null;
            var full = SpeakerByName(people, n);
            if (full != null) return full;
            var byFirst = people.Where(p => Prompts.FirstName(p.Record.Persona.Name).ToLowerInvariant() == n).ToList();
            return byFirst.Count == 1 ? byFirst[0].Record.Id : null; // two people with that first name: whose line it is can't be told (audit B11)
        }

        var raw = new List<ChatterLine>();
        foreach (var item in arr)
        {
            if (item is not JsonObject) continue; // a stray string among the lines: skipped, the rest kept (ROBUST-2)
            string S(string k) => item[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            bool leaving = !stay && item["leaving"] is JsonValue lv && lv.TryGetValue<bool>(out var b) && b;
            var id = Who(S("speaker"));
            if (id == null) continue;
            var rec = people.First(p => p.Record.Id == id).Record;
            var cleaned = ReplyParser.Parse(S("say"), rec.Persona.Name, rec.Persona.Nickname).Spoken.Trim().Trim('"', '“', '”');
            if (cleaned.Length == 0 || cleaned.Length > 400 || ReplyParser.LooksOutOfCharacter(cleaned, rec.Visual.IsAndroid)) continue;
            raw.Add(new ChatterLine { SpeakerId = id, ToId = Who(S("to")) ?? "", Text = cleaned, Leaving = leaving, Mood = Moods.Normalize(S("tone")), Gesture = GestureLibrary.Normalize(S("gesture")) });
        }

        int JoinAt(string id) => people.First(x => x.Record.Id == id).JoinAt;

        // Nobody speaks before they've joined.
        var joined = new List<ChatterLine>();
        foreach (var l in raw)
            if (joined.Count >= JoinAt(l.SpeakerId)) joined.Add(l);

        // Too many lines for someone: keep their first ones and their goodbye (the last thing they say).
        foreach (var p in people)
        {
            var theirs = joined.Where(l => l.SpeakerId == p.Record.Id).ToList();
            int max = Math.Max(1, p.MaxLines);
            if (theirs.Count <= max) continue;
            foreach (var extra in theirs.Skip(max - 1).Take(theirs.Count - max)) joined.Remove(extra);
        }

        var lines = new List<ChatterLine>();
        var gone = new HashSet<string>();
        foreach (var l in joined)
        {
            if (gone.Contains(l.SpeakerId)) continue;              // they already said goodbye
            if (lines.Count < JoinAt(l.SpeakerId)) continue;       // trimming moved them before their turn
            lines.Add(l);
            if (l.Leaving) gone.Add(l.SpeakerId);
        }
        // Someone's last line is their goodbye if it was written as one, sounds like one, or ends the conversation. Any
        // other last line ("Shut up, want to get us both in trouble?") isn't a reason to walk off: they stay and listen
        // until the conversation is over, and everyone goes their own way then.
        foreach (var id in lines.Select(l => l.SpeakerId).Distinct().ToList())
        {
            var last = lines.Last(l => l.SpeakerId == id);
            if (!stay && (last == lines[^1] || SoundsLikeGoodbye(last.Text))) last.Leaving = true;
        }
        if (lines.Select(l => l.SpeakerId).Distinct().Count() < 2) return null;

        var script = new ChatterScript
        {
            Lines = lines,
            Summary = o["summary"] is JsonValue sv && sv.TryGetValue<string>(out var sum) ? sum.Trim() : "",
        };
        foreach (var p in people) script.People[p.Record.Id] = p.Record;
        // How each of them sounds, line by line: their tone moves with the conversation, not with each line alone.
        LineTones.Apply(lines, l => l.SpeakerId, l => l.Mood, (l, t) => l.Tone = t,
            id => script.People.TryGetValue(id, out var r) ? r.State : null);
        if (o["exits"] is JsonArray exits)
            foreach (var e in exits)
            {
                if (e is not JsonObject) continue;
                string S(string k) => e[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
                var id = Who(S("speaker"));
                if (id == null || script.Exits.ContainsKey(id)) continue;
                var rec = people.First(p => p.Record.Id == id).Record;
                string Clean(string t)
                {
                    var c = ReplyParser.Parse(t, rec.Persona.Name, rec.Persona.Nickname).Spoken.Trim().Trim('"', '\u201C', '\u201D');
                    return c.Length is > 0 and <= 240 && !ReplyParser.LooksOutOfCharacter(c, rec.Visual.IsAndroid) ? c : "";
                }
                script.Exits[id] = (Clean(S("leave")), Clean(S("bye")));
            }
        return script;
    }

    /// <summary>
    /// What stays with them: a line in each speaker's life ("Talked with Daria near the stalls about rent"), how each
    /// pair gets on now, and anything they heard about the player — only when someone there actually knew it.
    /// </summary>
    private async Task RememberAsync(IReadOnlyList<ChatterPrompts.Person> people, ChatterScript script, string raw)
    {
        // Each speaker's epoch from when it was written: a reset of one of them since then means nothing is kept for them.
        long EpochOf(string id) => script.Epochs.TryGetValue(id, out var e) ? e : _brain.EpochFor(id);
        bool Current(string id) => _brain.EpochFor(id) == EpochOf(id);
        var o = LlmClient.ExtractJsonObject(raw);
        var spoke = people.Where(p => script.Lines.Any(l => l.SpeakerId == p.Record.Id)).ToList();
        if (spoke.Count < 2) return; // one line before it broke up isn't a conversation
        var place = people[0].Snap;
        int day = place.GameDay;

        // How each pair gets on.
        var changes = new Dictionary<string, int>();
        if (o?["getAlong"] is JsonArray along)
            foreach (var g in along)
            {
                if (g is not JsonObject) continue;
                string A(string k) => g[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
                int ch = g["change"] is JsonValue cv && cv.TryGetValue<int>(out var n) ? n : 0;
                var a = spoke.FirstOrDefault(p => SameName(p, A("a")));
                var b = spoke.FirstOrDefault(p => SameName(p, A("b")));
                if (a != null && b != null && a != b) changes[PairBook.Key(a.Record.Id, b.Record.Id)] = Math.Clamp(ch, -10, 10);
            }
        for (int i = 0; i < spoke.Count; i++)
            for (int j = i + 1; j < spoke.Count; j++)
            {
                var a = spoke[i].Record;
                var b = spoke[j].Record;
                changes.TryGetValue(PairBook.Key(a.Id, b.Id), out int ch);
                if (Current(a.Id) && Current(b.Id)) _pairs.Record(a.Id, a.Persona.Name, b.Id, b.Persona.Name, ch, day, script.Summary);
            }

        // FB-63: the turn of a romance it played out, once both of them spoke (and neither was reset since).
        CoupleBook.Turn? applied = null;
        if (script.Turn is { } turn && Couples != null && spoke.Any(p => p.Record.Id == turn.A) && spoke.Any(p => p.Record.Id == turn.B)
            && Current(turn.A) && Current(turn.B) && !_brain.KnownMinors.Contains(turn.A) && !_brain.KnownMinors.Contains(turn.B))
        {
            try { Couples.Apply(turn, day); applied = turn; _log($"Romance between residents: {turn.Kind} ({turn.NameA}, {turn.NameB}{(turn.Breakup ? ", a breakup" : "")})."); }
            catch (Exception e) { _log("Keeping who's together failed: " + e.Message); }
        }

        // The inner-life round: what was passed on is known to the others now (from whoever said it), and each of them
        // comes away feeling how it went; a row, a good talk, getting together or breaking up stays with them a while.
        if (Features.InnerLife && spoke.All(p => !_brain.KnownMinors.Contains(p.Record.Id)))
        {
            try
            {
                long now = Mind.GameClock.Of(place);
                var (gossip, _) = InnerRules();
                if (gossip && Witness != null && script.Lines.Count >= 3)
                {
                    // Review (core IL-1): only what the teller's lines actually brought up counts as told.
                    string playerName = spoke.Select(p => p.Record.State.PlayerName).FirstOrDefault(n => n.Length > 0) ?? "";
                    foreach (var plan in script.Tellings.Where(t => Current(t.TellerId)
                                 && Mind.Telling.Said(t, script.Lines.Where(l => l.SpeakerId == t.TellerId).Select(l => l.Text), playerName)))
                    {
                        var listeners = spoke.Where(p => p.Record.Id != plan.TellerId && Current(p.Record.Id))
                            .Select(p => new Mind.Telling.Speaker { Id = p.Record.Id, Name = p.Name, Persona = p.Record.Persona }).ToList();
                        int n;
                        lock (_tellRng) n = Mind.Telling.Heard(Witness, plan, listeners, _pairs.Get, now, _tellRng);
                        if (n > 0) _log($"Gossip: {plan.TellerName} passed on what they {(plan.Belief.How == Mind.Belief.Told ? "heard" : "saw")} ({plan.Belief.Kind}, {plan.Stance}) to {n}.");
                    }
                }
                if (Minds is { } minds) RememberFeelings(minds, spoke.Where(p => Current(p.Record.Id)).ToList(), script, changes, applied, now);
            }
            catch (Exception e) { _log("Remembering how it went for them failed: " + e.Message); }
        }

        // FB-35: three or four who spoke are remembered as a group too, with how it went for each (checked under the book's
        // lock: a reset of any of them since it was written means it isn't kept).
        if (Circles != null && spoke.Count >= 3)
        {
            try
            {
                var members = spoke.Select(p => (p.Record.Id, p.Record.Persona.Name, HowItWent(script, p.Record.Id))).ToList();
                Circles.Record(members, day, script.Summary, () => spoke.All(p => Current(p.Record.Id) && !_brain.KnownMinors.Contains(p.Record.Id)));
            }
            catch (Exception e) { _log("Remembering the group failed: " + e.Message); }
        }

        // What they heard about the player: only if someone else in the group knew something to tell.
        var heard = new List<(ChatterPrompts.Person listener, string fact)>();
        if (o?["heardAboutPlayer"] is JsonArray hp)
            foreach (var h in hp)
            {
                if (h is not JsonObject) continue;
                string L(string k) => h[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
                var listener = spoke.FirstOrDefault(p => SameName(p, L("listener")));
                string fact = L("fact");
                if (listener == null || fact.Length is < 4 or > 200) continue;
                var tellerStates = spoke.Where(p => p != listener).Select(p => p.Record.State).ToList();
                // Anything about running a place can only come from someone who knows they run it.
                if (AboutAPlace(fact, place.PlayerVenues) && !tellerStates.Any(t => t.KnowsYourPlace)) continue;
                // Otherwise it must match what someone else there actually knew: their name or "the newcomer" alone isn't enough.
                var names = tellerStates.Select(t => t.PlayerName).Where(n => n.Length > 0).ToList();
                var knowledge = tellerStates.Select(t => Without(Knowledge(t, place.PlayerVenues), names)).Where(k => k.Trim().Length > 0).ToList();
                if (knowledge.Count == 0 || !ToldCheck.AboutPlayerSupported(Without(fact, names), knowledge)) continue;
                heard.Add((listener, fact));
            }

        var said = script.Lines
            .Select(l => (who: people.FirstOrDefault(x => x.Record.Id == l.SpeakerId), l.Text))
            .Where(l => l.who != null)
            .Select(l => $"{Prompts.FirstName(l.who!.Name)}: {l.Text}")
            .ToList();
        foreach (var p in spoke)
        {
            var others = spoke.Where(x => x != p).Select(x => x.Name).ToList();
            string with = others.Count == 1 ? others[0] : string.Join(", ", others.Take(others.Count - 1)) + " and " + others[^1];
            string where = place.Location.Length > 0 ? $" in {place.Location}" : "";
            string what = script.Summary.Length > 0 ? ": " + script.Summary.TrimEnd('.') + "." : ".";
            string entry = $"Talked with {with}{where}{what}";
            var talk = new Talk { GameDay = day, Clock = place.Clock, Where = place.Location, Kind = Talk.InPerson, With = with, Lines = said, Summary = script.Summary };
            var mine = heard.Where(h => h.listener == p).Select(h => h.fact).ToList();
            var tellers = spoke.Where(x => x != p && KnowsSomething(x.Record.State)).ToList();
            string id = p.Record.Id;
            long epoch = EpochOf(id);
            try
            {
                await _brain.Mutate(id, rec =>
                {
                    if (_brain.EpochFor(id) != epoch) return false;
                    rec.LifeLog.Add(new LifeEvent { GameDay = day, Utc = DateTime.UtcNow, Text = ReplyParser.Quotable(entry, 600) });
                    if (rec.LifeLog.Count > 40) rec.LifeLog.RemoveRange(0, rec.LifeLog.Count - 40);
                    rec.AddTalk(talk);
                    foreach (var fact in mine)
                    {
                        string from = tellers.Count > 0 ? tellers[0].Name : "someone";
                        string note = $"Heard from {Prompts.FirstName(from)}: {ReplyParser.Quotable(fact, 300)}";
                        if (!rec.State.PlayerFacts.Any(f => string.Equals(f, note, StringComparison.OrdinalIgnoreCase))) rec.State.PlayerFacts.Add(note);
                        if (place.PlayerVenues.Any(v => fact.Contains(v, StringComparison.OrdinalIgnoreCase)) && tellers.Any(t => t.Record.State.KnowsYourPlace))
                            rec.State.KnowsYourPlace = true;
                        var named = tellers.Select(t => t.Record.State.PlayerName).FirstOrDefault(n => n.Length > 0 && fact.Contains(n, StringComparison.OrdinalIgnoreCase));
                        if (named != null && rec.State.PlayerName.Length == 0) rec.State.PlayerName = named;
                    }
                    if (rec.State.PlayerFacts.Count > 30) rec.State.PlayerFacts.RemoveRange(0, rec.State.PlayerFacts.Count - 30);
                    return true;
                }).ConfigureAwait(false);
            }
            catch (Exception e) { _log($"Remembering a conversation for {p.Name} failed: {e.Message}"); }
        }
    }

    /// <summary>
    /// The inner-life round: each speaker comes away feeling the way their last line sounded; a row (the pair's change -5 or
    /// worse) or a good talk (+5 or better) stays with both a few hours; a romance turn that played out stays for days.
    /// </summary>
    private static void RememberFeelings(Mind.MindBook minds, List<ChatterPrompts.Person> spoke, ChatterScript script, Dictionary<string, int> changes,
                                         CoupleBook.Turn? turn, long now)
    {
        var byId = spoke.ToDictionary(p => p.Record.Id);
        (float, float) Base(string id) => Mind.Affect.Baseline(byId[id].Record.Persona);
        void Stays(string id, string key, string text, float v, float a, int hours, string mood)
        {
            if (!byId.ContainsKey(id)) return;
            minds.Add(id, byId[id].Name, Base(id), new Mind.Moodlet { Key = key, Text = text, V = v, A = a, Until = now + hours * 60, Mood = mood }, now);
        }
        foreach (var p in spoke)
            if (script.Lines.LastOrDefault(l => l.SpeakerId == p.Record.Id) is { Mood.Length: > 0 } last)
                minds.Felt(p.Record.Id, p.Name, Base(p.Record.Id), last.Mood, "moderate", now, 0.25f);
        foreach (var (key, ch) in changes)
        {
            var ids = key.Split('|');
            if (ids.Length != 2 || !byId.ContainsKey(ids[0]) || !byId.ContainsKey(ids[1])) continue;
            string First(string id) => Prompts.FirstName(byId[id].Name);
            if (ch <= -5)
            {
                Stays(ids[0], "row:" + ids[1], $"still annoyed after words with {First(ids[1])}", -0.3f, 0.25f, 6, "irritated");
                Stays(ids[1], "row:" + ids[0], $"still annoyed after words with {First(ids[0])}", -0.3f, 0.25f, 6, "irritated");
            }
            else if (ch >= 5)
            {
                Stays(ids[0], "talk:" + ids[1], $"glad of the chat with {First(ids[1])}", 0.15f, 0.05f, 4, "content");
                Stays(ids[1], "talk:" + ids[0], $"glad of the chat with {First(ids[0])}", 0.15f, 0.05f, 4, "content");
            }
        }
        if (turn == null) return;
        string fa = Prompts.FirstName(turn.NameA), fb = Prompts.FirstName(turn.NameB);
        switch (turn.Kind)
        {
            case "spark":
                Stays(turn.A, "love:" + turn.B, $"you and {fb} are together now", 0.45f, 0.2f, 72, "hopeful");
                Stays(turn.B, "love:" + turn.A, $"you and {fa} are together now", 0.45f, 0.2f, 72, "hopeful");
                break;
            case "confront" when turn.Breakup:
                Stays(turn.A, "breakup:" + turn.B, $"you broke up with {fb}", -0.6f, 0.1f, 96, "sad");
                Stays(turn.B, "breakup:" + turn.A, $"{fa} broke up with you", -0.6f, 0.1f, 96, "sad");
                break;
            case "confront":
                Stays(turn.A, "had it out:" + turn.B, $"had it out with {fb} about {Prompts.FirstName(turn.PartnerName)}", -0.35f, 0.35f, 24, "angry");
                Stays(turn.B, "caught:" + turn.A, $"{fa} found out about {Prompts.FirstName(turn.PartnerName)}", -0.3f, 0.3f, 24, "anxious");
                break;
            case "jealous":
                Stays(turn.A, "jealous:" + turn.B, $"jealous of {fb}", -0.2f, 0.3f, 6, "irritated");
                break;
        }
    }

    /// <summary>Whose line it is by their full name, nickname or the name they gave you (lower case in); null if nobody's.</summary>
    private static string? SpeakerByName(IReadOnlyList<ChatterPrompts.Person> people, string n)
    {
        foreach (var p in people)
        {
            var per = p.Record.Persona;
            if (per.Name.ToLowerInvariant() == n || (per.Nickname.Length > 0 && per.Nickname.ToLowerInvariant() == n)
                || (p.Record.State.KnownAs.Length > 0 && p.Record.State.KnownAs.ToLowerInvariant() == n))
                return p.Record.Id;
        }
        return null;
    }

    private static bool SameName(ChatterPrompts.Person p, string name)
    {
        string n = (name ?? "").Trim().ToLowerInvariant();
        var per = p.Record.Persona;
        return n.Length > 0 && (per.Name.ToLowerInvariant() == n || Prompts.FirstName(per.Name).ToLowerInvariant() == n || per.Nickname.ToLowerInvariant() == n);
    }

    private static bool KnowsSomething(NpcState st) => st.KnowsYourPlace || st.PlayerName.Length > 0 || st.PlayerFacts.Count > 0;

    private static readonly System.Text.RegularExpressions.Regex PlaceTalk = new(
        @"(?i)\b(?:runs?|running|owns?|owner|inherited|bar|restaurant|stall|stand|shop|diner|cafe|café|noodles?|ramen|eatery|menu|cook(?:s|ing)?|chef)\b",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool AboutAPlace(string fact, IReadOnlyList<string> venues) =>
        PlaceTalk.IsMatch(fact) || venues.Any(v => v.Length > 0 && fact.Contains(v, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] Generic = { "stranger", "newcomer", "player", "new", "town", "guy", "girl", "person", "someone", "around" };

    /// <summary>The text without the player's name and words that describe anybody ("stranger", "newcomer"...), so a match means a real fact.</summary>
    private static string Without(string text, IEnumerable<string> names)
    {
        foreach (var w in names.SelectMany(n => n.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Concat(Generic))
            if (w.Length > 0) text = System.Text.RegularExpressions.Regex.Replace(text, $@"(?i)\b{System.Text.RegularExpressions.Regex.Escape(w)}\b", " ", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return text;
    }

    /// <summary>Everything this person knows about the player, as one text (empty if nothing).</summary>
    private static string Knowledge(NpcState st, IReadOnlyList<string> venues)
    {
        var parts = new List<string>();
        if (st.PlayerName.Length > 0) parts.Add(st.PlayerName);
        if (st.KnowsYourPlace) parts.AddRange(venues.Select(v => "runs " + v));
        parts.AddRange(st.PlayerFacts);
        return string.Join(". ", parts);
    }

    private static readonly System.Text.RegularExpressions.Regex Goodbye = new(
        @"(?i)\b(?:bye|goodbye|see\s+(?:you|ya)|later\b|take\s+care|catch\s+you|gotta\s+go|got\s+to\s+go|have\s+to\s+go|need\s+to\s+go|must\s+go|" +
        @"better\s+(?:go|get\s+going|head)|get\s+going|head(?:ing)?\s+(?:off|out|home|back)|off\s+to\b|back\s+to\s+(?:work|it|the)|i'?m\s+off|" +
        @"that'?s\s+me\b|g'?night|good\s*night|until\s+next\s+time|gotta\s+run|got\s+to\s+run|i\s+should\s+go)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Whether a line reads as someone taking their leave ("Right, I'd better get going.", "See you around.").</summary>
    public static bool SoundsLikeGoodbye(string text) => Goodbye.IsMatch(text ?? "");

    // ------------------------------------------------------------------ thinking out loud

    /// <summary>
    /// Someone on their own says what's on their mind (their profile is created if it doesn't exist yet). Null for anyone
    /// under 18, or if nothing usable came back. Nothing is remembered until it has actually been said (<see cref="RememberThoughtAsync"/>).
    /// </summary>
    public async Task<(NpcRecord rec, List<string> lines, string about, long epoch, List<Voice.ToneState> tones)?> ThinkAsync(string id, NpcVisual visual, GameSnapshot snap, CancellationToken ct, int maxLines = 1)
    {
        long epoch = _brain.EpochFor(id); // before their record is read: carried to RememberThoughtAsync
        var rec = await _brain.EnsurePersonAsync(id, visual, snap, ct).ConfigureAwait(false);
        if (rec == null || _brain.IsKnownMinor(rec)) return null;
        // FB-33: one thing from their own life, not one of the last few; FB-02: CORA only for believers, now and then.
        MutterSeeds.Seed? seed;
        bool cora;
        long now = Mind.GameClock.Of(snap);
        // The inner-life round: what's on their mind now is what they mutter about, more often than not (not again and again).
        var onMind = Features.InnerLife ? Minds?.Current(id) : null;
        var carried = Features.InnerLife ? Minds?.MoodNow(id, now) : null;
        var (_, vary) = InnerRules();
        lock (_recentSeeds)
        {
            _recentSeeds.TryGetValue(id, out var recent);
            seed = onMind is { Text.Length: > 0 } && !(recent?.Contains("mind:" + onMind.Key) ?? false) && _seedRng.NextDouble() < 0.6
                ? new MutterSeeds.Seed("mind:" + onMind.Key, onMind.Text)
                : MutterSeeds.Pick(rec, snap, _seedRng, recent);
            cora = MutterSeeds.Believer(rec.Persona) && _seedRng.Next(MutterSeeds.CoraOdds) == 0;
            if (seed != null)
            {
                recent ??= new List<string>();
                recent.Add(seed.Key);
                if (recent.Count > MutterSeeds.Remember) recent.RemoveAt(0);
                _recentSeeds[id] = recent;
                if (_recentSeeds.Count > 400) _recentSeeds.Clear();
            }
        }
        int candidates = maxLines == 1 && vary && Features.InnerLife && Minds != null ? 3 : 1;
        var res = await _llm.CompleteAsync(ChatterPrompts.ThinkRequest(rec, snap, maxLines, seed, cora, candidates, carried?.mood ?? "", carried?.reason ?? ""), ct).ConfigureAwait(false);
        if (!res.Ok) { _problem("Someone's thought couldn't be written: " + res.Error); return null; }
        var (said, about, words) = ParseThought(res.Text, rec, Math.Max(maxLines, candidates));
        if (candidates > 1 && said.Count > 1)
        {
            // The one least like what they've said lately (the phrase ledger), with its tone.
            var (openers, recentLines) = Minds!.Phrases(id);
            int pick = Math.Max(0, Mind.PhraseLedger.Pick(said, recentLines, openers));
            said = new List<string> { said[pick] };
            words = new List<string> { pick < words.Count ? words[pick] : "" };
        }
        else if (said.Count > maxLines) { said = said.Take(maxLines).ToList(); words = words.Take(maxLines).ToList(); }
        if (Features.InnerLife && said.Count > 0) Minds?.Said(id, rec.Persona.Name, said[0], now);
        if (!cora) // "CORA, it's back already..." -> "It's back already..." when it wasn't theirs to say this time
        {
            for (int i = 0; i < said.Count; i++) said[i] = MutterSeeds.TrimLeadingCora(said[i]);
            for (int i = said.Count - 1; i >= 0; i--) if (said[i].Length == 0) { said.RemoveAt(i); if (i < words.Count) words.RemoveAt(i); }
        }
        var kept = said.Select((l, i) => (line: l, tone: i < words.Count ? words[i] : "")).Where(x => !ReplyParser.LooksOutOfCharacter(x.line, rec.Visual.IsAndroid)).ToList();
        if (kept.Count == 0) return null;
        // Their voice starts from how they feel and moves with each line, as in conversations (FB-04).
        var tones = new List<Voice.ToneState>();
        LineTones.Apply(kept, _ => rec.Id, x => x.tone, (_, t) => tones.Add(t), _ => rec.State);
        return (rec, kept.Select(x => x.line).ToList(), about, epoch, tones);
    }

    /// <summary>
    /// The lines and what they were about, from the JSON answer ("lines", or an older-style "say"), or plain words from a
    /// model that ignores JSON. At most <paramref name="maxLines"/>.
    /// </summary>
    public static (List<string> lines, string about, List<string> tones) ParseThought(string text, NpcRecord rec, int maxLines = 1)
    {
        var o = LlmClient.ExtractJsonObject(text);
        string S(string k) => o?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? ReplyParser.Blank(s) : "";
        var raw = new List<string>();
        if (o?["lines"] is JsonArray arr)
            foreach (var item in arr)
                if (item is JsonValue iv && iv.TryGetValue<string>(out var s)) raw.Add(s);
        if (raw.Count == 0) raw.Add(o != null ? S("say") : text ?? "");
        // Each line's tone word, by position (an answer without them, or an older style, has none: their mood leads).
        var toneWords = new List<string>();
        if (o?["tones"] is JsonArray tarr)
            foreach (var item in tarr)
                toneWords.Add(item is JsonValue tv && tv.TryGetValue<string>(out var w) ? Moods.Normalize(ReplyParser.Blank(w)) : "");
        var lines = new List<string>();
        var tones = new List<string>();
        for (int i = 0; i < raw.Count; i++)
        {
            var p = ReplyParser.Parse(ReplyParser.Blank(raw[i]), rec.Persona.Name, rec.Persona.Nickname);
            string line = Mutter(ReplyParser.Brief(p.Spoken, 160).Trim().Trim('"', '\u201C', '\u201D'));
            if (line.Length is > 0 and <= 220) { lines.Add(line); tones.Add(i < toneWords.Count ? toneWords[i] : ""); }
            if (lines.Count >= Math.Max(1, maxLines)) break;
        }
        string about = S("about").Trim().TrimEnd('.');
        string first = lines.FirstOrDefault() ?? "";
        if (about.Length is 0 or > 120) about = first.Length > 60 ? first[..60] + "…" : first;
        return (lines, about, tones);
    }

    /// <summary>Each person's last few mutter seeds (this session), so the next is about something else (FB-33).</summary>
    private readonly Dictionary<string, List<string>> _recentSeeds = new();
    private readonly Random _seedRng = new();

    /// <summary>Most words in one muttered line: people thinking out loud say a few words, not a speech.</summary>
    public const int MutterWords = 12;

    /// <summary>
    /// A thought out loud the way people mutter it: a line the writer made longer than <see cref="MutterWords"/> words is
    /// cut back to the last sentence end inside that, else to the last comma (trailing off), else it trails off there.
    /// </summary>
    public static string Mutter(string line, int maxWords = MutterWords)
    {
        var words = (line ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= maxWords) return (line ?? "").Trim();
        string head = string.Join(" ", words.Take(maxWords));
        int end = head.LastIndexOfAny(new[] { '.', '!', '?', '\u2026' });
        if (end > 0) return head[..(end + 1)];
        int pause = head.LastIndexOfAny(new[] { ',', ';', '\u2014', '\u2013' });
        if (pause > 0) return head[..pause].TrimEnd() + "...";
        return head.TrimEnd(',', ';', ':', '-', ' ') + "...";
    }

    /// <summary>What they actually said to themselves (the lines shown): kept in their record, and it colours their next conversations.</summary>
    public Task RememberThoughtAsync(string id, GameSnapshot snap, string said, string about, long? epoch = null) =>
        RememberThoughtAsync(id, snap, new[] { said }, about, epoch);

    /// <param name="epoch">Their epoch when the thought was written (ThinkAsync gives it): a reset since then means it isn't kept.</param>
    public Task RememberThoughtAsync(string id, GameSnapshot snap, IReadOnlyList<string> said, string about, long? epoch = null)
    {
        if (said.Count == 0) return Task.CompletedTask;
        long written = epoch ?? _brain.EpochFor(id);
        return _brain.Mutate(id, rec =>
        {
            if (_brain.EpochFor(id) != written || _brain.IsKnownMinor(rec)) return false;
            rec.AddTalk(new Talk
            {
                GameDay = snap.GameDay, Clock = snap.Clock, Where = snap.Location, Kind = Talk.Thought,
                Lines = said.Select(l => $"{Prompts.FirstName(rec.Persona.Name)}: {l}").ToList(), Summary = about,
            });
            return true;
        });
    }

    /// <summary>
    /// FB-05: what someone overheard near them, kept with their talks as one snippet of kind "overheard" (a name if they know
    /// who said it, else "someone they don't know"). Only for someone who already has a record (nobody is created for it),
    /// never after a reset since they heard it (<paramref name="epoch"/>), never for someone under 18.
    /// </summary>
    public Task RememberOverheardAsync(string listenerId, long epoch, GameSnapshot place, IReadOnlyList<(string speakerId, string speakerName, string line)> heard)
    {
        if (heard.Count == 0) return Task.CompletedTask;
        return _brain.Mutate(listenerId, rec =>
        {
            if (_brain.EpochFor(listenerId) != epoch || _brain.IsKnownMinor(rec)) return false;
            string Who(string id, string name) => name.Length > 0 && (_pairs.Get(listenerId, id) != null) ? Prompts.FirstName(name) : "Someone they don't know";
            var lines = heard.Take(4).Select(h => $"{Who(h.speakerId, h.speakerName)}: {h.line}").ToList();
            var known = heard.Where(h => h.speakerName.Length > 0 && _pairs.Get(listenerId, h.speakerId) != null).Select(h => Prompts.FirstName(h.speakerName)).Distinct().ToList();
            string about = known.Count > 0 ? $"overheard {string.Join(" and ", known)} talking nearby" : "overheard people they don't know talking nearby";
            rec.AddTalk(new Talk { GameDay = place.GameDay, Clock = place.Clock, Where = place.Location, Kind = Talk.Overheard, Lines = lines, Summary = about });
            return true;
        });
    }

    /// <summary>
    /// FB-18: how a conversation went for someone, for their parting line when they get up from the table: +1 warm, 0
    /// neutral, -1 sour. From the AI's own "getAlong" for them, else the tone of their last line.
    /// </summary>
    public static int HowItWent(ChatterScript script, string id)
    {
        var me = script.Persons.FirstOrDefault(p => p.Record.Id == id);
        var o = LlmClient.ExtractJsonObject(script.Raw ?? "");
        if (me != null && o?["getAlong"] is JsonArray along)
        {
            int sum = 0;
            bool found = false;
            foreach (var g in along)
            {
                if (g is not JsonObject) continue;
                string A(string k) => g[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
                if (!SameName(me, A("a")) && !SameName(me, A("b"))) continue;
                found = true;
                sum += g["change"] is JsonValue cv && cv.TryGetValue<int>(out var n) ? n : 0;
            }
            if (found && sum != 0) return Math.Sign(sum);
        }
        float valence = script.Lines.LastOrDefault(l => l.SpeakerId == id)?.Tone?.Valence ?? 0f;
        return valence > 0.15f ? 1 : valence < -0.15f ? -1 : 0;
    }

    /// <summary>A person's epoch, from the brain this works with (the mock one in mock mode).</summary>
    public long EpochFor(string id) => _brain.EpochFor(id);

    /// <summary>
    /// FB-05 (review): whether a conversation is about the player at all: any line names them, one of their places or the
    /// newcomer, or someone told another something about them. Then none of it is overheard (one line about them gives the
    /// lines around it away).
    /// </summary>
    public static bool AboutThePlayer(ChatterScript script)
    {
        var names = script.People.Values.Select(r => r.State.PlayerName).ToList();
        var places = script.Persons.FirstOrDefault()?.Snap.PlayerVenues ?? new List<string>();
        if (script.Lines.Any(l => Social.ChatterPlan.AboutThePlayer(l.Text, names, places))) return true;
        var o = LlmClient.ExtractJsonObject(script.Raw ?? "");
        return o?["heardAboutPlayer"] is JsonArray told && told.Count > 0;
    }

    /// <summary>The same for a phone call: any line about the player (by either side).</summary>
    public static bool AboutThePlayer(PhoneScript script)
    {
        var names = new[] { script.Caller?.State.PlayerName ?? "", script.Other.Record?.State.PlayerName ?? "" };
        return script.Lines.Any(l => Social.ChatterPlan.AboutThePlayer(l.Text, names, script.Snap.PlayerVenues));
    }

    /// <summary>Everyone's epoch now (for work about to read their records).</summary>
    private Dictionary<string, long> EpochsOf(IEnumerable<string> ids)
    {
        var epochs = new Dictionary<string, long>();
        foreach (var id in ids) epochs[id] = _brain.EpochFor(id);
        return epochs;
    }

    // ------------------------------------------------------------------ phone calls

    /// <summary>A one- or two-digit number, in any script, even straight against a word ("7yo"): read as a possible age.</summary>
    private static readonly System.Text.RegularExpressions.Regex ShortNumbers = new(@"(?<!\d)\d{1,2}(?!\d)", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex YoungRelation = new(
        @"(?i)\b(?:kid|kids|child|children|baby|toddler|teen\w*|schoolgirl|schoolboy|little\s+(?:brother|sister|one)|grandchild\w*|grandkid\w*|pupil|student)\b",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Someone they know, to call: usually a resident they've talked with before (friends more often), else someone from
    /// their own life ("Teo — brother — on the rig"). Never someone under 18. Null if there's nobody.
    /// </summary>
    public ChatterPrompts.Callee? PickCallee(NpcRecord caller, Random rng)
    {
        // Residents they've talked with, by weight. Only the one picked is read (LEAK-4, PERF-3): one glance, not a full
        // parse of everyone they ever talked with. Never anyone on the under-18 list, who isn't even read.
        var known = new List<(string id, double weight)>();
        foreach (var pair in _pairs.For(caller.Id))
        {
            string other = pair.A == caller.Id ? pair.B : pair.A;
            if (pair.Affinity <= -40 || _brain.KnownMinors.Contains(other)) continue; // you don't ring someone you can't stand
            known.Add((other, PairBook.Weight(pair)));
        }
        var theirs = new List<ChatterPrompts.Callee>();
        foreach (var entry in caller.Persona.People)
        {
            var parts = entry.Split(new[] { " — ", " - ", " – ", "—" }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();
            if (parts.Length == 0 || parts[0].Length is 0 or > 40 || YoungRelation.IsMatch(entry) || CanonAge.SoundsYoung(entry)) continue;
            // "Mia — daughter, 7": any age under 18 in it, and they aren't called (a street number may cost an adult a call; fine).
            // Digits in any script count ("٧" is 7), and so does a number written against a word ("7yo"): ROBUST-11.
            if (ShortNumbers.Matches(entry).Any(m => (CanonAge.ReadDigits(m.Value) ?? 0) < 18)) continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(entry, @"\b(?:late|dead|died|passed away|deceased|killed)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)) continue;
            theirs.Add(new ChatterPrompts.Callee { Name = parts[0], Relation = parts.Length > 1 ? parts[1] : "", Detail = parts.Length > 2 ? parts[2] : "" });
        }
        if (known.Count > 0 && (theirs.Count == 0 || rng.NextDouble() < 0.6))
        {
            // The one picked can turn out unreadable right now, nameless or under 18 (an adult only once cleared here):
            // then another is picked, at most three times in all.
            for (int tries = 0; tries < 3 && known.Count > 0; tries++)
            {
                double total = known.Sum(k => k.weight), roll = rng.NextDouble() * total;
                int at = known.Count - 1;
                for (int i = 0; i < known.Count; i++) { roll -= known[i].weight; if (roll <= 0) { at = i; break; } }
                string id = known[at].id;
                known.RemoveAt(at);
                var rec = _brain.Store.Glance(id);
                if (rec == null || _brain.IsKnownMinor(rec) || rec.Persona.Name.Length == 0) continue;
                return new ChatterPrompts.Callee { Record = rec, Name = rec.Persona.Name, Relation = "someone they know" };
            }
        }
        return theirs.Count > 0 ? theirs[rng.Next(theirs.Count)] : null;
    }

    /// <summary>
    /// A phone call for this resident (created first if they don't exist yet): who they call, and the whole call written in
    /// one request. Null if they're under 18, have nobody to call, or the call couldn't be written.
    /// </summary>
    public async Task<PhoneScript?> PhoneAsync(string id, NpcVisual visual, GameSnapshot snap, int linesEach, int maxTokens, CancellationToken ct,
        IReadOnlyCollection<string>? busy = null)
    {
        var epochs = EpochsOf(new[] { id }); // before their record is read
        var caller = await _brain.EnsurePersonAsync(id, visual, snap, ct).ConfigureAwait(false);
        if (caller == null || _brain.IsKnownMinor(caller)) return null;
        foreach (var known in _pairs.For(caller.Id)) // everyone they might call, before PickCallee reads their records
        {
            string them = known.A == caller.Id ? known.B : known.A;
            epochs.TryAdd(them, _brain.EpochFor(them));
        }
        var other = PickCallee(caller, Random.Shared);
        if (other == null) { _log($"{caller.Persona.Name} has nobody to call yet."); return null; }
        if (other.Record != null) epochs.TryAdd(other.Record.Id, _brain.EpochFor(other.Record.Id)); // only if missed above
        // Someone already in a conversation or on a call (or talking to the player) doesn't pick up.
        if (other.Record != null && busy != null && busy.Contains(other.Record.Id))
        {
            _log($"{caller.Persona.Name} tries to phone {other.Name}, who's busy: no answer.");
            var unanswered = PhoneScript.Unanswered(caller, other, snap, Random.Shared);
            unanswered.Epochs = epochs;
            return unanswered;
        }
        var pair = other.Record != null ? _pairs.Get(caller.Id, other.Record.Id) : null;
        var res = await _llm.CompleteAsync(ChatterPrompts.PhoneRequest(caller, snap, other, pair, linesEach, maxTokens), ct).ConfigureAwait(false);
        if (!res.Ok) { _problem("A phone call couldn't be written: " + res.Error); return null; }
        var script = ParsePhone(res.Text, linesEach);
        if (script == null) { _log("A phone call came back unusable; skipped."); return null; }
        if (ct.IsCancellationRequested) return null;
        script.Caller = caller;
        script.Other = other;
        script.Snap = snap;
        script.Epochs = epochs;
        LineTones.Apply(script.Lines, l => l.Caller ? "caller" : "other", l => l.Mood, (l, t) => l.Tone = t,
            side => side == "caller" ? caller.State : other.Record?.State);
        _log($"{caller.Persona.Name} is on the phone with {other.Name}{(other.Record == null && other.Relation.Length > 0 ? $" ({other.Relation})" : "")}: {script.Lines.Count} lines. {script.Summary}");
        return script;
    }

    /// <summary>
    /// The call as written, checked: at most <paramref name="linesEach"/> lines a side (the caller keeps their first ones and
    /// their last, which hangs up), and nothing after the caller's last line. Null unless both sides say something.
    /// </summary>
    public static PhoneScript? ParsePhone(string text, int linesEach)
    {
        var o = LlmClient.ExtractJsonObject(text);
        if (o?["lines"] is not JsonArray arr) return null;
        var lines = new List<PhoneLine>();
        foreach (var item in arr)
        {
            if (item is not JsonObject) continue; // a stray string among the lines: skipped, the rest kept (ROBUST-2)
            string side = item["side"] is JsonValue sv && sv.TryGetValue<string>(out var s1) ? s1.Trim().ToLowerInvariant() : "";
            string say = item["say"] is JsonValue yv && yv.TryGetValue<string>(out var s2) ? s2 : "";
            string tone = item["tone"] is JsonValue tv && tv.TryGetValue<string>(out var s3) ? Moods.Normalize(s3) : "";
            string cleaned = ReplyParser.Parse(say, "", "").Spoken.Trim().Trim('"', '\u201C', '\u201D');
            if (cleaned.Length == 0 || side is not ("caller" or "other")) continue;
            lines.Add(new PhoneLine { Caller = side == "caller", Text = cleaned, Mood = tone });
        }
        int lastCaller = lines.FindLastIndex(l => l.Caller);
        if (lastCaller < 0) return null;
        lines = lines.Take(lastCaller + 1).ToList(); // they hang up on their last line: nothing after it
        int max = Math.Max(1, linesEach);
        var callerLines = lines.Where(l => l.Caller).ToList();
        if (callerLines.Count > max) // their first lines and the hang-up
            foreach (var extra in callerLines.Skip(max - 1).Take(callerLines.Count - max)) lines.Remove(extra);
        var otherLines = lines.Where(l => !l.Caller).ToList();
        foreach (var extra in otherLines.Skip(max)) lines.Remove(extra); // the other end: their first lines
        if (!lines.Any(l => !l.Caller)) return null;
        string exit = o["exit"] is JsonValue ev && ev.TryGetValue<string>(out var ex) ? ReplyParser.Parse(ex, "", "").Spoken.Trim().Trim('"', '\u201C', '\u201D') : "";
        return new PhoneScript
        {
            Lines = lines,
            Summary = o["summary"] is JsonValue smv && smv.TryGetValue<string>(out var sum) ? sum.Trim() : "",
            Change = o["change"] is JsonValue cv && cv.TryGetValue<int>(out var ch) ? Math.Clamp(ch, -10, 10) : 0,
            Exit = exit.Length <= 240 ? exit : "",
        };
    }

    /// <summary>
    /// What stays with them after a call: both of them (if the other end is a resident too, wherever they are) keep the
    /// call as it was said, a line in their life, and how they get on. <paramref name="linesHeard"/> lines, or all of it.
    /// </summary>
    public Task RememberCallAsync(PhoneScript call, int linesHeard)
    {
        // Each person's epoch from when the call was written: a reset of one of them since then means nothing is kept for them.
        long EpochOf(string id) => call.Epochs.TryGetValue(id, out var e) ? e : _brain.EpochFor(id);
        if (call.Failed)
        {
            if (linesHeard <= 0) return Task.CompletedTask;
            string callerId = call.Caller.Id;
            long tried = EpochOf(callerId);
            return _brain.Mutate(callerId, rec =>
            {
                if (_brain.EpochFor(callerId) != tried) return false;
                rec.LifeLog.Add(new LifeEvent { GameDay = call.Snap.GameDay, Utc = DateTime.UtcNow, Text = ReplyParser.Quotable($"Tried to phone {call.Other.Name}; no answer.", 600) });
                if (rec.LifeLog.Count > 40) rec.LifeLog.RemoveRange(0, rec.LifeLog.Count - 40);
                return true;
            });
        }
        var said = call.Lines.Take(Math.Max(0, linesHeard)).ToList();
        if (!said.Any(l => l.Caller) || !said.Any(l => !l.Caller)) return Task.CompletedTask;
        var caller = call.Caller;
        string callerName = caller.Persona.Name, otherName = call.Other.Name;
        var text = said.Select(l => $"{Prompts.FirstName(l.Caller ? callerName : otherName)}: {l.Text}").ToList();
        var snap = call.Snap;
        string about = call.Summary.Length > 0 ? ": " + call.Summary.TrimEnd('.') + "." : ".";
        bool Current(string id) => _brain.EpochFor(id) == EpochOf(id);
        if (call.Other.Record is { } o && Current(caller.Id) && Current(o.Id))
            _pairs.Record(caller.Id, callerName, o.Id, otherName, call.Change, snap.GameDay, call.Summary);

        Task Keep(string id, string with, string relation)
        {
            long epoch = EpochOf(id);
            return _brain.Mutate(id, rec =>
            {
                if (_brain.EpochFor(id) != epoch || _brain.IsKnownMinor(rec)) return false;
                rec.LifeLog.Add(new LifeEvent { GameDay = snap.GameDay, Utc = DateTime.UtcNow, Text = ReplyParser.Quotable($"On the phone with {with}{relation}{about}", 600) });
                if (rec.LifeLog.Count > 40) rec.LifeLog.RemoveRange(0, rec.LifeLog.Count - 40);
                rec.AddTalk(new Talk { GameDay = snap.GameDay, Clock = snap.Clock, Where = snap.Location, Kind = Talk.Phone, With = with, Lines = text, Summary = call.Summary });
                return true;
            });
        }

        var jobs = new List<Task> { Keep(caller.Id, otherName, call.Other.Record == null && call.Other.Relation.Length > 0 ? $" (their {call.Other.Relation})" : "") };
        if (call.Other.Record != null) jobs.Add(Keep(call.Other.Record.Id, callerName, ""));
        return Task.WhenAll(jobs);
    }
}
