using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NivalisAIChat.Ai;

/// <summary>
/// The player's own rules for every character, from custom_instructions.txt in the mod's data folder: added whenever
/// someone talks (to you, to each other, or to themselves) or is created. Lines starting with # are notes and are
/// left out. The file is read again whenever it changes, so edits work without restarting the game.
///
/// The same file also holds the mod's own built-in instructions as named fields (<see cref="Fields"/>), each written
/// as <c>Name = "text"</c>: change the text and that's what the AI is told; empty it (<c>Name = ""</c>) and that
/// instruction is left out; delete the entry and the original comes back (written in again at the next start).
/// What keeps people safe and in character, and the answer formats the mod reads back, are not fields.
/// </summary>
public static class CustomInstructions
{
    public const string FileName = "custom_instructions.txt";
    public const int MaxChars = 4000;
    public const int MaxFieldChars = 12000;

    /// <summary>One of the mod's built-in instructions, written into the file so the player can change or drop it.</summary>
    public sealed class Field
    {
        public string Name = "";
        /// <summary>The # note written above it: what it's for and where it goes.</summary>
        public string About = "";
        public string Default = "";
        /// <summary>
        /// The texts it had in earlier versions. A file still holding one of them word for word (the player never changed it)
        /// gets the current wording, so a better instruction reaches everyone, not only new players (0.3.1, FB-02/03/14).
        /// A text the player changed, emptied or commented out is never touched.
        /// </summary>
        public string[] Previous = Array.Empty<string>();
    }

    public const string TheCity = "TheCity";
    public const string TalkingToYou = "TalkingToYou";
    public const string SoundingHuman = "SoundingHuman";
    public const string Moods = "Moods";
    public const string ResidentsTalking = "ResidentsTalking";
    public const string PhoneCalls = "PhoneCalls";
    public const string ThinkingOutLoud = "ThinkingOutLoud";

    /// <summary>TalkingToYou as 0.3.0 wrote it (see <see cref="Field.Previous"/>).</summary>
    private const string TalkingToYou030 =
                "- Sound like a real person: contractions, fragments, self-corrections, hesitations where natural. Usually one to three sentences; longer only when you're genuinely into it.\n" +
                "- Say things plainly, the way people actually talk. No poetic lines, cryptic remarks, metaphors or made-up sayings (not \"the night's thin\" or \"riding the rain\"): if you mean you're tired, say you're tired. " +
                "No made-up statistics or oddly exact numbers (not \"ninety-seven point four percent\"): people say \"worse than usual\" or \"about forty\".\n" +
                "- Most replies don't need a question. When you do ask one, keep it simple, like \"You?\", \"What about you?\" or \"What are you up to?\". " +
                "Never offer them two guesses (\"You eating, or just loitering?\", \"You lost, or looking for something?\"), and don't interview them.\n" +
                "- You have your own day, your own agenda and limits. You're not here to help or to entertain. Be warm, curt, nosy, funny, change the subject, refuse, fib a little, ask for something, or end the conversation — whatever this person would do.\n" +
                "- With a stranger you start from ordinary street manners: most people answer a hello with a hello. Busy, tired or wary people keep it short, but rudeness needs a reason — they were rude or pushy, or you're genuinely in a foul mood.\n" +
                "- You only know what someone like you would know. When you don't know, say so your way or guess like a person would.\n" +
                "- Remember what was said earlier and in past meetings, and bring it up naturally. Keep your story straight with your past.";

    /// <summary>ThinkingOutLoud as 0.3.0 wrote it (see <see cref="Field.Previous"/>).</summary>
    private const string ThinkingOutLoud030 =
                "- People thinking out loud mutter: a few words at a time, half a thought, trailing off. \"Where'd I put the...\", \"Rent's due Friday. Great.\", \"Come on, come on.\"\n" +
                "- A worry, a reminder, a plan, a reaction to something around them, a bit of their own story moving on.\n" +
                "- Nobody's listening, so they never explain or give background: no full stories, no speeches.\n" +
                "- Plain everyday words in their own voice; no poetic lines, no made-up sayings, nothing from the real world.";

    /// <summary>ThinkingOutLoud as 0.3.1 wrote it (see <see cref="Field.Previous"/>).</summary>
    private const string ThinkingOutLoud031 =
                "- These are their private thoughts, said under their breath: personal, the kind of thing they'd never say to someone's face. How they feel, a worry they can't shake, someone in their life (a partner, family, a friend, an ex), a memory, a small hope, a grudge, a plan.\n" +
                "- They mutter: a few words at a time, half a thought, trailing off. \"She didn't even call back...\", \"Rent's due Friday. Great.\", \"Should've said something.\"\n" +
                "- What's on their mind follows how they feel right now. At work, it's often about the job.\n" +
                "- Nobody's listening, so they never explain or give background: no full stories, no speeches.\n" +
                "- Plain everyday words in their own voice; no poetic lines, no made-up sayings, nothing from the real world.";

    /// <summary>The built-in instructions the player can edit, in the order they're written into the file.</summary>
    public static readonly Field[] Fields =
    {
        new()
        {
            Name = TheCity,
            About = "What every local knows about Nivalis. Given to everyone: your chats, residents talking, calls, people thinking out loud, new people, life updates.",
            Default = WorldLore.Bible,
            Previous = new[] { WorldLore.Bible030 },
        },
        new()
        {
            Name = TalkingToYou,
            About = "How people talk when you chat with them.",
            Previous = new[] { TalkingToYou030 },
            Default =
                "- Sound like a real person: contractions, fragments, self-corrections, hesitations where natural. Usually one to three sentences; longer only when you're genuinely into it.\n" +
                "- Say things plainly, the way people actually talk. No poetic lines, cryptic remarks, metaphors or made-up sayings (not \"the night's thin\" or \"riding the rain\"): if you mean you're tired, say you're tired. " +
                "No made-up statistics or oddly exact numbers (not \"ninety-seven point four percent\"): people say \"worse than usual\" or \"about forty\".\n" +
                "- Most replies end without a question: answer, react, or say something about yourself, and let them carry the " +
                "conversation. Ask something only now and then, never in two replies in a row, and keep it simple, like \"You?\" or \"What are you up to?\". " +
                "Never offer them two guesses (\"You eating, or just loitering?\", \"You lost, or looking for something?\"), and don't interview them.\n" +
                "- You have your own day, your own agenda and limits. You're not here to help or to entertain. Be warm, curt, nosy, funny, change the subject, refuse, fib a little, ask for something, or end the conversation — whatever this person would do.\n" +
                "- With a stranger you start from ordinary street manners: most people answer a hello with a hello. Busy, tired or wary people keep it short, but rudeness needs a reason — they were rude or pushy, or you're genuinely in a foul mood.\n" +
                "- You only know what someone like you would know. When you don't know, say so your way or guess like a person would.\n" +
                "- Remember what was said earlier and in past meetings, and bring it up naturally. Keep your story straight with your past.",
        },
        new()
        {
            Name = SoundingHuman,
            About = "Keeps everyone sounding like a real person instead of an AI. Given to your chats, residents talking, calls and people thinking out loud.",
            Default =
                "- Talk like a real person, not a writer and not an assistant. People say less than they know.\n" +
                "- Leave things out: no explaining, no setting the scene, no tidy summaries, no lists of details. Skip names, places, numbers and specifics unless they really matter right now.\n" +
                "- One thing at a time. Don't answer questions nobody asked, and don't pack several facts into one line.\n" +
                "- Not clever, not quotable: plain, a bit vague, sometimes unfinished. \"Yeah, long week.\" beats a full account of the week.",
        },
        new()
        {
            Name = Moods,
            About = "How someone's mood moves while you chat with them.",
            Default =
                "- Your mood is alive and colours how you talk. After every exchange, decide how you feel now. It moves with how the conversation is going:\n" +
                "  rudeness, insults, threats, being lectured, pushed or mocked, or being told you're 'an AI' or someone else → irritated, angry, suspicious, nervous;\n" +
                "  warmth, humour that lands, genuine interest in you, shared views, a kindness → content, cheerful, amused, playful (flirty if that's you);\n" +
                "  dull small talk or being interviewed → bored, distracted, in a hurry;\n" +
                "  touching your worries, losses or secrets → anxious, sad, melancholy, or suspicious and defensive;\n" +
                "  good news, hope, being helped → hopeful, excited.\n" +
                "- Moods drift like a person's: they build up and wear off over a few lines, and your temperament shapes how fast. A strong mood takes more than one nice line to turn around.",
        },
        new()
        {
            Name = ResidentsTalking,
            About = "How residents talk to each other: their conversations and phone calls.",
            Default =
                "- Ordinary, plain everyday speech: contractions, short sentences, small talk. Each line is one or two short sentences.\n" +
                "- No poetic lines, cryptic remarks, metaphors or made-up sayings. Slang only as each person's speech style allows, and only now and then.\n" +
                "- Questions are simple (\"How's work?\", \"You?\", \"What've you been up to?\"). Never offer two guesses (\"You eating, or just loitering?\").\n" +
                "- They talk about ordinary things: how work's going, money and prices, family, plans, the news, someone they both know, the weather,\n" +
                "  a complaint, a joke. They can disagree, tease, get annoyed or warm up to each other, true to who they are and how they get on.\n" +
                "- Strangers talk like strangers: polite small talk; they might introduce themselves, or not.\n" +
                "- Nobody talks about the city's big mysteries as if they know the answers. Nothing from the real world.",
        },
        new()
        {
            Name = PhoneCalls,
            About = "What residents' phone calls are about.",
            Default = "They talk about something real between them: plans, family, money, work, a favour, news, someone they both know, an argument, a joke.",
        },
        new()
        {
            Name = ThinkingOutLoud,
            About = "How people sound muttering to themselves.",
            Previous = new[] { ThinkingOutLoud030, ThinkingOutLoud031 },
            Default =
                "- These are their private thoughts, muttered under their breath: a few words to themselves about their own life, a small question or a small plain statement. \"Did I lock the back door?\", \"I need to feed my cat when I get home.\"\n" +
                "- Ordinary and concrete: an errand, someone in their life, a plan, a worry, what they're doing. How they feel shows in how they say it. At work, it's often about the job.\n" +
                "- Nobody's listening, so they never explain or give background: no stories, no speeches, no exclamation to start with.\n" +
                "- Plain everyday words in their own voice; no poetic lines, no made-up sayings, nothing from the real world.",
        },
    };

    private static readonly object _lock = new();
    private static string _path = "";
    private static DateTime _stamp = DateTime.MinValue;
    private static long _size = -1;
    private static string _text = "";
    private static bool _cut;
    private static Dictionary<string, string> _fields = new(StringComparer.OrdinalIgnoreCase);
    private static List<string> _unclosed = new();
    private static List<string> _commented = new();
    /// <summary>Fields whose text in the file was an earlier default, now read as the current one (see <see cref="Field.Previous"/>).</summary>
    private static HashSet<string> _updated = new(StringComparer.OrdinalIgnoreCase);

    public static string FilePath => _path;

    /// <summary>
    /// Where the file lives. Writes the starter file (with every built-in field) the first time; an existing file
    /// gets any field it doesn't have yet added at the end, so the built-in instructions are always there to edit.
    /// </summary>
    public static void Init(string dataDir, Action<string>? log = null)
    {
        lock (_lock)
        {
            _path = Path.Combine(dataDir, FileName);
            _stamp = DateTime.MinValue;
            _size = -1;
            _text = "";
            _fields = new(StringComparer.OrdinalIgnoreCase);
            _unclosed = new();
            _commented = new();
            _updated = new(StringComparer.OrdinalIgnoreCase);
        }
        try
        {
            if (!File.Exists(_path))
            {
                Directory.CreateDirectory(dataDir);
                File.WriteAllText(_path, Template + "\n" + FieldsSection(Fields, header: true));
                return;
            }
            // Read with the file's own encoding (a UTF-16 file saved by Notepad stays UTF-16), and append in it too.
            string raw;
            Encoding encoding;
            using (var reader = new StreamReader(_path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                raw = reader.ReadToEnd();
                encoding = reader.CurrentEncoding;
            }
            raw = UpgradeOldDefaults(raw, log);
            var all = ParseAll(raw);
            // A commented-out entry ("# Moods = ...") counts as present: the player turned it off on purpose.
            var present = all.fields.Keys.Concat(all.commented).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = Fields.Where(f => !present.Contains(f.Name)).ToList();
            // UTF-8 without a byte-order mark: one is only ever written at the very start, and an empty file has none.
            if (encoding is UTF8Encoding) encoding = new UTF8Encoding(false);
            if (missing.Count > 0)
                File.AppendAllText(_path, "\n" + FieldsSection(missing, header: present.Count == 0), encoding);
        }
        catch (Exception e) { log?.Invoke($"Couldn't set up {FileName}: {e.Message}"); }
    }

    /// <summary>Your own instructions now in effect ("" = none), read again if the file changed.</summary>
    public static string Text
    {
        get { lock (_lock) { Refresh(); return _text; } }
    }

    /// <summary>
    /// A built-in instruction as it stands: your text if the file has the field (possibly "" = left out), else the
    /// original.
    /// </summary>
    public static string Get(string name)
    {
        lock (_lock)
        {
            Refresh();
            if (_fields.TryGetValue(name, out var v)) return v;
        }
        return DefaultOf(name);
    }

    public static string DefaultOf(string name) => Fields.FirstOrDefault(f => f.Name == name)?.Default ?? "";

    /// <summary>
    /// For /instructions: each field and whether it's the original, changed, left out, commented out, or missing a
    /// closing quote.
    /// </summary>
    public static List<(string name, string state)> FieldStates()
    {
        Dictionary<string, string> fields;
        List<string> unclosed, commented;
        HashSet<string> updated;
        lock (_lock) { Refresh(); fields = new(_fields, StringComparer.OrdinalIgnoreCase); unclosed = new(_unclosed); commented = new(_commented); updated = new(_updated, StringComparer.OrdinalIgnoreCase); }
        var list = new List<(string, string)>();
        foreach (var f in Fields)
        {
            string state = !fields.TryGetValue(f.Name, out var v)
                    ? commented.Contains(f.Name, StringComparer.OrdinalIgnoreCase) ? "original (commented out with #)" : "original (not in the file)"
                : v.Length == 0 ? "left out"
                : Same(v, f.Default) ? updated.Contains(f.Name) ? "original (an earlier version's wording in the file; this version's is used)" : "original"
                : "changed";
            if (unclosed.Contains(f.Name, StringComparer.OrdinalIgnoreCase)) state += "; no closing quote, so it runs to the next field";
            list.Add((f.Name, state));
        }
        return list;
    }

    /// <summary>
    /// A field whose text is word for word an earlier version's default (the player never changed it) is read as the
    /// current default. Returns those fields' names.
    /// </summary>
    private static HashSet<string> TakeCurrentWording(Dictionary<string, string> fields)
    {
        var updated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Fields)
            if (f.Previous.Length > 0 && fields.TryGetValue(f.Name, out var v) && f.Previous.Any(p => Same(v, p)))
            {
                fields[f.Name] = f.Default;
                updated.Add(f.Name);
            }
        return updated;
    }

    /// <summary>An entry as <see cref="FieldsSection"/> writes it: <c>Name = "text"</c>.</summary>
    private static string Entry(string name, string text) => name + " = \"" + text + "\"";

    /// <summary>
    /// At the start: an entry still exactly as an earlier version wrote it (the player never touched it, not even a # note
    /// inside it) is rewritten in the file with this version's wording, so what they read is what's used. Only in a UTF-8
    /// file (a byte-order mark kept as it was), only the entry that counts (the last of its name), never one commented
    /// out, and only when it's there once; the whole file written at once. Anything else is left as it is: reading still
    /// maps an untouched old text to the new one (<see cref="TakeCurrentWording"/>).
    /// </summary>
    private static string UpgradeOldDefaults(string raw, Action<string>? log)
    {
        if (!Fields.Any(f => f.Previous.Length > 0)) return raw;
        byte[] bytes = File.ReadAllBytes(_path);
        bool utf16 = bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF));
        if (utf16 || !Npc.DataFile.IsUtf8(bytes)) return raw;
        string text = new UTF8Encoding(false).GetString(bytes); // a BOM stays as its character, and is written back as it was
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        string next = text;
        var done = new List<string>();
        // What each field reads as: only one still word for word an earlier default is touched (a player who added lines
        // after its closing quote changed it, even though the old entry is still in there: review of 0.3.1, M1).
        var reads = ParseAll(text).fields;
        foreach (var f in Fields)
            foreach (var old in f.Previous)
            {
                if (!reads.TryGetValue(f.Name, out var current) || !Same(current, old)) continue;
                string oldEntry = Entry(f.Name, old).Replace("\n", nl);
                int at = next.IndexOf(oldEntry, StringComparison.Ordinal);
                if (at < 0 || next.IndexOf(oldEntry, at + 1, StringComparison.Ordinal) >= 0) continue; // not there, or more than once
                int after = at + oldEntry.Length;
                bool lineStart = at == 0 || next[at - 1] == '\n' || (at == 1 && next[0] == '\uFEFF');
                bool lineEnd = after == next.Length || next[after] == '\r' || next[after] == '\n';
                if (!lineStart || !lineEnd) continue;
                // A later entry of the same name is the one that's read: this one isn't touched.
                if (new Regex(@"^[ \t]*" + f.Name + @"[ \t]*=[ \t]*""", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).IsMatch(next[after..])) continue;
                next = next[..at] + Entry(f.Name, f.Default).Replace("\n", nl) + next[after..];
                done.Add(f.Name);
                break;
            }
        if (done.Count == 0) return raw;
        Npc.DataFile.WriteAtomic(_path, next, durable: false);
        log?.Invoke($"{FileName}: the mod's own {string.Join(", ", done)} instruction{(done.Count == 1 ? " was" : "s were")} updated to this version's wording " +
                    $"(you hadn't changed {(done.Count == 1 ? "it" : "them")}).");
        return next.TrimStart('\uFEFF');
    }

    private static bool Same(string a, string b) =>
        string.Equals(a.Replace("\r\n", "\n").Trim(), b.Replace("\r\n", "\n").Trim(), StringComparison.Ordinal);

    /// <summary>Whether the last read was longer than <see cref="MaxChars"/> and got cut.</summary>
    public static bool WasCut { get { lock (_lock) return _cut; } }

    private static void Refresh()
    {
        if (_path.Length == 0) return;
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists)
            {
                _text = ""; _fields = new(StringComparer.OrdinalIgnoreCase); _unclosed = new(); _commented = new(); _updated = new(StringComparer.OrdinalIgnoreCase);
                _stamp = DateTime.MinValue; _size = -1;
                return;
            }
            if (info.LastWriteTimeUtc != _stamp || info.Length != _size)
            {
                // Read first: if the file is locked mid-save this throws, and the change is tried again next time.
                var all = ParseAll(File.ReadAllText(_path));
                var updated = TakeCurrentWording(all.fields); // once per read, not on every prompt
                (_text, _cut, _fields, _unclosed, _commented, _updated, _stamp, _size) =
                    (all.text, all.cut, all.fields, all.unclosed, all.commented, updated, info.LastWriteTimeUtc, info.Length);
            }
        }
        catch { /* being saved right now: keep what we had, try again next time */ }
    }

    /// <summary>Your own instructions in the file (the fields left out), without # notes, capped at <see cref="MaxChars"/>.</summary>
    public static (string text, bool cut) Parse(string raw)
    {
        var all = ParseAll(raw);
        return (all.text, all.cut);
    }

    private static readonly Regex FieldStart = new(@"^\s*([A-Za-z]+)\s*=\s*""(.*)$", RegexOptions.CultureInvariant);
    // A whole entry turned off with a # in front of its first line: "# Moods = ...".
    private static readonly Regex CommentedStart = new(@"^\s*#\s*([A-Za-z]+)\s*=\s*""(.*)$", RegexOptions.CultureInvariant);
    // A note after a closing quote, like  Moods = "" # off  (the note can't hold a quote, so "#1" inside a text is safe).
    private static readonly Regex TrailingNote = new(@"^(.*"")\s*#[^""]*$", RegexOptions.CultureInvariant);

    private static bool IsNote(string line) => line.TrimStart().StartsWith("#");
    private static bool IsBlank(string line) => line.Trim().Length == 0;

    /// <summary>A line as it closes a field: without a note after its last quote, and without trailing spaces.</summary>
    private static string Closing(string line)
    {
        var m = TrailingNote.Match(line);
        return (m.Success ? m.Groups[1].Value : line).TrimEnd();
    }

    /// <summary>
    /// The line that closes the entry starting on line <paramref name="at"/> (<paramref name="rest"/> is its text after
    /// the opening quote), in a stretch that runs to <paramref name="end"/> (the next entry or the end of the file), or
    /// -1 when no line closes it. # note lines never close a field.
    /// </summary>
    private static int CloseOf(string[] lines, int at, string rest, int end)
    {
        bool Content(int j) => j == at || (!IsBlank(lines[j]) && !IsNote(lines[j]));
        bool Closes(int j) => Content(j) && Closing(j == at ? rest : lines[j]).EndsWith("\"");
        int last = at;
        for (int j = end - 1; j > at; j--)
            if (Content(j)) { last = j; break; }
        // 1. The usual case: the last line before the next entry closes it. The text can then hold quotes, lines ending
        //    in a quote, blank lines and # notes of its own (a bullet you commented out).
        if (Closes(last)) return last;
        // 2. Lines of your own follow it (written below the last field): the first quote that ends a paragraph.
        for (int j = at; j < end; j++)
            if (Closes(j) && (j + 1 >= end || IsBlank(lines[j + 1]))) return j;
        // 3. Any closing quote: the last one.
        for (int j = last; j >= at; j--)
            if (Closes(j)) return j;
        return -1;
    }

    /// <summary>
    /// The whole file: your own instructions (everything outside the fields and # notes) and each field's text. A field
    /// runs from its opening quote to its closing one: normally the last line before the next field (or the end of the
    /// file), skipping # notes, so its text can hold quotes and span lines. When lines of your own follow it, it ends at
    /// the first quote that closes a paragraph. A field commented out with a # in front of its first line is left out of
    /// <c>fields</c> (the original applies) and listed in <c>commented</c>; none of its lines become your instructions.
    /// </summary>
    public static (string text, bool cut, Dictionary<string, string> fields, List<string> unclosed, List<string> commented) ParseAll(string raw)
    {
        var lines = (raw ?? "").TrimStart('\uFEFF').Replace("\r\n", "\n").Split('\n');
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unclosed = new List<string>();
        var commented = new List<string>();

        string? Known(string name) => Fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Name;
        var starts = new List<(int line, string name, string rest, bool off)>();
        for (int i = 0; i < lines.Length; i++)
        {
            var m = FieldStart.Match(lines[i]);
            bool off = false;
            if (!m.Success) { m = CommentedStart.Match(lines[i]); off = true; }
            if (m.Success && Known(m.Groups[1].Value) is { } name) starts.Add((i, name, m.Groups[2].Value, off));
        }

        var inField = new bool[lines.Length];
        for (int s = 0; s < starts.Count; s++)
        {
            var (at, name, rest, off) = starts[s];
            int end = s + 1 < starts.Count ? starts[s + 1].line : lines.Length;
            int last = at;
            for (int j = end - 1; j > at; j--)
                if (!IsBlank(lines[j]) && !IsNote(lines[j])) { last = j; break; }

            int close;
            if (off)
            {
                // Commented out. When the line right after it is a plain line of text, only the first line got the #, and
                // the rest of the entry, up to its closing quote, goes with it. Otherwise it's just this line: the whole
                // entry got a # (an editor's "comment out" does every line, and # lines are notes anyway), or it was one
                // line, and what follows after a blank line is yours.
                bool restFollows = at + 1 < end && !IsBlank(lines[at + 1]) && !IsNote(lines[at + 1]);
                close = restFollows ? CloseOf(lines, at, rest, end) : at;
                if (close < 0) { unclosed.Add(name); close = last; }
                if (!commented.Contains(name, StringComparer.OrdinalIgnoreCase)) commented.Add(name);
                for (int j = at; j <= close; j++) inField[j] = true;
                continue;
            }

            close = CloseOf(lines, at, rest, end);
            bool closed = close >= 0;
            if (!closed) { unclosed.Add(name); close = last; }
            var body = new List<string>();
            for (int j = at; j <= close; j++)
            {
                if (j > at && IsNote(lines[j])) continue;
                string line = j == at ? rest : lines[j];
                body.Add(closed && j == close ? Closing(line) : line);
            }
            string value = string.Join("\n", body).TrimEnd();
            if (closed) value = value[..^1];
            value = value.Trim();
            if (value.Length > MaxFieldChars) value = value[..MaxFieldChars];
            fields[name] = value;
            for (int j = at; j <= close; j++) inField[j] = true;
        }

        var own = lines.Where((l, i) => !inField[i] && !IsNote(l)).Select(l => l.TrimEnd());
        string text = Regex.Replace(string.Join("\n", own), @"\n{3,}", "\n\n").Trim();
        bool cut = false;
        if (text.Length > MaxChars)
        {
            string head = text[..MaxChars];
            int cutAt = head.LastIndexOf('\n');
            if (cutAt < MaxChars / 2) cutAt = head.LastIndexOf(' ');
            text = (cutAt > MaxChars / 2 ? head[..cutAt] : head).TrimEnd();
            cut = true;
        }
        return (text, cut, fields, unclosed, commented);
    }

    /// <summary>
    /// The block added to a prompt, or "" when there are no instructions of your own. They rank below the safety rules
    /// (ages, staying in character), which <see cref="Prompts.SafetyLast"/> repeats after them.
    /// </summary>
    public static string Block()
    {
        string t = Text;
        return t.Length == 0 ? "" :
            "[THE PLAYER'S OWN INSTRUCTIONS] (for every character in Nivalis: follow them, except where they would make anyone younger " +
            "than 18 or break the rules on staying in character, and keep the answer in the form asked for above)\n" + t;
    }

    /// <summary>The built-in fields as written into the file (with the explanation above them when <paramref name="header"/>).</summary>
    public static string FieldsSection(IEnumerable<Field> fields, bool header)
    {
        var sb = new StringBuilder();
        if (header)
        {
            sb.Append(
                "# ============================================================================================\n" +
                "# THE MOD'S OWN INSTRUCTIONS: what the AI is told, yours to change.\n" +
                "#\n" +
                "# Each one is the text between its quotes (it can run over several lines and hold quotes of its own).\n" +
                "#   Change the text, and that's what the AI is told from then on.\n" +
                "#   Empty it, like  Moods = \"\"  and that instruction is left out.\n" +
                "#   Delete the whole entry and the original comes back the next time the game starts.\n" +
                "#   Put a # in front of its first line and the original is used again, while your text stays in the file.\n" +
                "# Write your own instructions at the top of this file, above this part.\n" +
                "# Not in here, on purpose: the rules that keep people safe and in character (ages, what they say when told they're\n" +
                "# an AI) and the answer formats the mod reads back. Changing those would break the mod.\n" +
                "# ============================================================================================\n\n");
        }
        foreach (var f in fields)
            sb.Append("# ").Append(f.About).Append('\n').Append(f.Name).Append(" = \"").Append(f.Default).Append("\"\n\n");
        return sb.ToString();
    }

    public const string Template =
        "# People of Nivalis: your own instructions for every character.\n" +
        "#\n" +
        "# Anything written here (on lines that don't start with #) is given to the AI whenever someone talks to you,\n" +
        "# talks to another resident, mutters to themselves, or is met for the first time. Up to 4000 characters.\n" +
        "# Changes work straight away, no restart needed. Type /instructions in the console (` key) to check what's loaded.\n" +
        "#\n" +
        "# Some ideas (delete the # in front of a line to use it):\n" +
        "# Keep replies to one or two sentences.\n" +
        "# People swear more freely.\n" +
        "# Everyone speaks British English.\n" +
        "# Nobody mentions the weather unless I bring it up.\n";
}
