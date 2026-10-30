using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Phone;

/// <summary>
/// The phone (the author's request): your contacts and your texts, per save (phone.json, part of FB-61's snapshot). A
/// contact is someone who gave you their number in a chat; each has a random xxx-xxxx number of their own, and so do you.
/// Written on a pool thread (BookWriter), read under its lock from any thread. Never anyone under 18. No Unity, tested.
/// </summary>
public sealed class PhoneBook
{
    public sealed class Contact
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Number { get; set; } = "";
        public int GotDay { get; set; }
        public string GotClock { get; set; } = "";
        public string GotWhere { get; set; } = "";
    }

    public sealed class Text
    {
        /// <summary>"you" or "them".</summary>
        public string From { get; set; } = "";
        public string Body { get; set; } = "";
        public int GameDay { get; set; }
        public string Clock { get; set; } = "";
        /// <summary>Game minutes since day 0 (the order texts came in; GameClock).</summary>
        public long Minute { get; set; }
        /// <summary>Yours: "waiting" (no signal), "sending", "sent", "delivered", "read". Theirs: "".</summary>
        public string State { get; set; } = "";
        /// <summary>Yours: the game minute they saw it (-1 not yet).</summary>
        public long SeenAt { get; set; } = -1;
        /// <summary>Theirs: a voice note with it (its file in the voice notes folder, "" none), how long, its wave ("358...").</summary>
        public string Voice { get; set; } = "";
        public float VoiceSeconds { get; set; }
        public string Wave { get; set; } = "";
        /// <summary>A line from a story conversation (Ava's), mirrored into the phone: no receipts, never sent as a text.</summary>
        public bool Story { get; set; }
    }

    public sealed class Thread
    {
        public string Id { get; set; } = "";
        public List<Text> Texts { get; set; } = new();
        public int Unread { get; set; }
        /// <summary>FB-70: what they'll do with your texts ("" nothing pending, "answers", "later", "never"), when they look, when they answer (game minutes, -1 none).</summary>
        public string Plan { get; set; } = "";
        public long LookAt { get; set; } = -1;
        public long AnswerAt { get; set; } = -1;
        /// <summary>When they looked (the left-on-read moment, told to them when they answer later).</summary>
        public long LookedAt { get; set; } = -1;
        /// <summary>How many story lines are mirrored into this conversation so far, and the last one ("minute|words").</summary>
        public int StoryLines { get; set; }
        public string StoryLast { get; set; } = "";
        /// <summary>FB-75: story lines you deleted ("day|words"), so the earlier lines' mirror never brings them back.</summary>
        public List<string> StoryGone { get; set; } = new();
        /// <summary>FB-75: you cleared this conversation: earlier story lines aren't brought into it again.</summary>
        public bool StoryCleared { get; set; }
    }

    public sealed class Data
    {
        public string MyNumber { get; set; } = "";
        public Dictionary<string, Contact> Contacts { get; set; } = new();
        public Dictionary<string, Thread> Threads { get; set; } = new();
        /// <summary>Everyone's own number, made the first time it's needed (so what they say in a chat is what's saved).</summary>
        public Dictionary<string, string> Numbers { get; set; } = new();
        /// <summary>The game minute someone last texted you first (how often it happens).</summary>
        public long LastTextFirst { get; set; } = -1;
        /// <summary>The phone has been out once (the first-time card is shown only then).</summary>
        public bool Seen { get; set; }
        /// <summary>Ava's recorded voice for her story lines, by the line's words (kept as they were heard).</summary>
        public Dictionary<string, string> StoryVoices { get; set; } = new();
        /// <summary>Each recording's length and wave ("seconds|wave", by file), so a line mirrored after it was heard gets both (FB-74).</summary>
        public Dictionary<string, string> StoryVoiceMeta { get; set; } = new();
        /// <summary>
        /// FB-75: Ava's story lines from before her voice was kept, voiced from the game's own recording ("game-....wav" by the
        /// game's voice file name): nothing copied to disk; played from the game when you press play.
        /// </summary>
        public Dictionary<string, string> StoryGameVoices { get; set; } = new();
    }

    public const int MaxContacts = 300, MaxTexts = 200, MaxBody = 600, MaxNumbers = 3000;
    /// <summary>FB-75: story lines in a conversation have their own cap (StoryBook's per person), so texting never pushes them out.</summary>
    public const int MaxStoryTexts = 800;
    public const string You = "you", Them = "them";
    public const string Waiting = "waiting", Sending = "sending", Sent = "sent", Delivered = "delivered", Read = "read";

    private readonly string _path;
    private readonly object _lock = new();
    private Data _data;
    private readonly Action<string>? _warn;
    private bool _readOnly, _warnedReadOnly, _dirty;
    private readonly BookWriter _writer;
    private readonly Random _random;
    public Func<string, bool> IsMinor { get; set; } = _ => false;

    public PhoneBook(string path, Action<string>? warn = null, int? seed = null)
    {
        _path = path;
        _warn = warn;
        _random = seed is int s ? new Random(s) : new Random();
        _data = DataFile.LoadBook(_path, t => JsonSerializer.Deserialize<Data>(t, NpcStore.Json), _warn, out _readOnly) ?? new Data();
        Repair(_data);
        _writer = new BookWriter(_path, Snapshot, _warn, failed: () => { lock (_lock) _dirty = true; });
    }

    /// <summary>A hand-edited or older file: nothing null is read back, and a number that isn't one is made again.</summary>
    private static void Repair(Data d)
    {
        d.MyNumber ??= "";
        d.Contacts ??= new();
        d.Threads ??= new();
        d.Numbers ??= new();
        d.StoryVoices ??= new();
        d.StoryVoiceMeta ??= new();
        d.StoryGameVoices ??= new();
        foreach (var k in d.StoryGameVoices.Where(kv => !IsGameVoice(kv.Key) || string.IsNullOrWhiteSpace(kv.Value) || kv.Value.Length >= 200 || !d.StoryVoices.ContainsValue(kv.Key))
                     .Select(kv => kv.Key).ToList()) d.StoryGameVoices.Remove(k);
        foreach (var k in d.StoryVoiceMeta.Where(kv => kv.Value == null || !d.StoryVoices.ContainsValue(kv.Key)).Select(kv => kv.Key).ToList()) d.StoryVoiceMeta.Remove(k);
        foreach (var k in d.StoryVoices.Where(kv => kv.Value == null || !VoiceNote.SafeName(kv.Value)).Select(kv => kv.Key).ToList()) d.StoryVoices.Remove(k);
        foreach (var k in d.Numbers.Where(kv => kv.Value == null || !IsNumber(kv.Value)).Select(kv => kv.Key).ToList()) d.Numbers.Remove(k);
        foreach (var k in d.Contacts.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) d.Contacts.Remove(k);
        foreach (var k in d.Threads.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) d.Threads.Remove(k);
        foreach (var (key, c) in d.Contacts) { c.Id = key; c.Name ??= ""; c.Number ??= ""; c.GotClock ??= ""; c.GotWhere ??= ""; }
        foreach (var (key, t) in d.Threads)
        {
            t.Id = key;
            t.Texts ??= new();
            t.Texts.RemoveAll(x => x == null);
            foreach (var x in t.Texts)
            {
                x.From = x.From == You ? You : Them; x.Body ??= ""; x.Clock ??= ""; x.State ??= ""; x.Wave ??= "";
                x.Voice = x.Voice != null && VoiceNote.SafeName(x.Voice) ? x.Voice : "";
                if (!float.IsFinite(x.VoiceSeconds) || x.VoiceSeconds < 0) x.VoiceSeconds = 0;
            }
            t.Plan = t.Plan is "answers" or "later" or "never" ? t.Plan : "";
            t.Unread = Math.Clamp(t.Unread, 0, t.Texts.Count);
        }
    }

    internal BookWriter Writer => _writer;

    private string? Snapshot()
    {
        string text;
        lock (_lock)
        {
            if (!_dirty) return null;
            if (_readOnly) { DataFile.NotSaved(_path, _warn, ref _warnedReadOnly); return null; }
            text = JsonSerializer.Serialize(_data, NpcStore.Json);
            _dirty = false;
        }
        return text;
    }

    private void Changed()
    {
        _dirty = true;
        _writer.SaveSoon();
    }

    public void Flush() { _writer.WaitIdle(); _writer.Write(); }

    public void Close()
    {
        try { Flush(); }
        finally { _writer.Close(); }
    }

    // ------------------------------------------------------------------ numbers

    /// <summary>
    /// The city's two area codes (the author: "the first three digits should be an area code each npc will receive one
    /// out of two variants of an area code, then the last four digits are randomized per save"). Each person always has the
    /// same one (from their id); the four digits after it are random, per save.
    /// </summary>
    public static readonly string[] AreaCodes = { "482", "736" };

    public static string AreaCodeOf(string id)
    {
        uint h = 2166136261;
        foreach (char ch in id ?? "") { h ^= ch; h *= 16777619; }
        return AreaCodes[h % (uint)AreaCodes.Length];
    }

    /// <summary>"xxx-xxxx": the first digit 2-9 (no number starts with 0 or 1), never one that's taken.</summary>
    public static bool IsNumber(string s) =>
        s.Length == 8 && s[3] == '-' && s[0] is >= '2' and <= '9' && s.Where((c, i) => i != 3).All(c => c is >= '0' and <= '9');

    /// <summary>A new number in this area code: the last four digits random, never one that's taken (the other code if this one is full).</summary>
    private string NewNumber(string area, Func<string, bool> taken)
    {
        foreach (var code in new[] { area }.Concat(AreaCodes.Where(a => a != area)))
            for (int i = 0; i < 400; i++)
            {
                string n = $"{code}-{_random.Next(0, 10000):0000}";
                if (!taken(n)) return n;
            }
        return $"{area}-{_random.Next(0, 10000):0000}";
    }

    private bool TakenLocked(string n) => n == _data.MyNumber || _data.Numbers.ContainsValue(n) || _data.Contacts.Values.Any(c => c.Number == n);

    /// <summary>Their own number (made now if they have none yet); "" for anyone under 18 or nobody.</summary>
    public string NumberOf(string id)
    {
        if (id.Length == 0 || IsMinor(id)) return "";
        lock (_lock) return NumberLocked(id);
    }

    private string NumberLocked(string id)
    {
        if (_data.Contacts.TryGetValue(id, out var c) && IsNumber(c.Number)) return c.Number;
        if (_data.Numbers.TryGetValue(id, out var n)) return n;
        if (_data.Numbers.Count >= MaxNumbers)
            foreach (var k in _data.Numbers.Keys.Where(k => !_data.Contacts.ContainsKey(k)).Take(MaxNumbers / 10).ToList()) _data.Numbers.Remove(k);
        n = NewNumber(AreaCodeOf(id), TakenLocked);
        _data.Numbers[id] = n;
        Changed();
        return n;
    }

    /// <summary>Your own number, made the first time it's asked for.</summary>
    public string MyNumber
    {
        get
        {
            lock (_lock)
            {
                if (IsNumber(_data.MyNumber)) return _data.MyNumber;
                _data.MyNumber = NewNumber(AreaCodes[_random.Next(AreaCodes.Length)], TakenLocked);
                Changed();
                return _data.MyNumber;
            }
        }
    }

    // ------------------------------------------------------------------ contacts

    /// <summary>
    /// They gave you their number: they're in your contacts (once; a new name is kept). Returns their contact, or null for
    /// anyone under 18, nobody, or a full book.
    /// </summary>
    public Contact? Add(string id, string name, int day, string clock, string where)
    {
        if (id.Length == 0 || IsMinor(id)) return null;
        lock (_lock)
        {
            if (_data.Contacts.TryGetValue(id, out var have))
            {
                if (name.Length > 0 && have.Name != name) { have.Name = name; Changed(); }
                return Copy(have);
            }
            if (_data.Contacts.Count >= MaxContacts) return null;
            var c = new Contact { Id = id, Name = name, Number = NumberLocked(id), GotDay = day, GotClock = clock ?? "", GotWhere = where ?? "" };
            _data.Contacts[id] = c;
            Changed();
            return Copy(c);
        }
    }

    public bool Has(string id) { lock (_lock) return _data.Contacts.ContainsKey(id); }

    public Contact? Get(string id) { lock (_lock) return _data.Contacts.TryGetValue(id, out var c) ? Copy(c) : null; }

    public List<Contact> Contacts() { lock (_lock) return _data.Contacts.Values.Where(c => !IsMinor(c.Id)).Select(Copy).ToList(); }

    public int Count { get { lock (_lock) return _data.Contacts.Count; } }

    private static Contact Copy(Contact c) => new() { Id = c.Id, Name = c.Name, Number = c.Number, GotDay = c.GotDay, GotClock = c.GotClock, GotWhere = c.GotWhere };

    // ------------------------------------------------------------------ texts

    private Thread ThreadLocked(string id)
    {
        if (!_data.Threads.TryGetValue(id, out var t)) { t = new Thread { Id = id }; _data.Threads[id] = t; }
        return t;
    }

    private static void Trim(Thread t)
    {
        // Texts keep their newest MaxTexts; story lines (FB-75) their newest MaxStoryTexts, each counted on its own.
        int texts = t.Texts.Count(x => !x.Story) - MaxTexts, story = t.Texts.Count(x => x.Story) - MaxStoryTexts;
        for (int i = 0; i < t.Texts.Count && (texts > 0 || story > 0);)
        {
            bool s = t.Texts[i].Story;
            if (s ? story > 0 : texts > 0) { t.Texts.RemoveAt(i); if (s) story--; else texts--; }
            else i++;
        }
        t.Unread = Math.Clamp(t.Unread, 0, t.Texts.Count);
        t.StoryGone ??= new();
        if (t.StoryGone.Count > 1000) t.StoryGone.RemoveRange(0, t.StoryGone.Count - 1000);
    }

    private static string GoneKey(int day, string body) => day + "|" + body;

    public static string Clean(string body)
    {
        var s = (body ?? "").Replace("\r", "").Trim();
        while (s.Contains("\n\n\n")) s = s.Replace("\n\n\n", "\n\n");
        return s.Length > MaxBody ? s[..MaxBody].TrimEnd() : s;
    }

    /// <summary>
    /// You text a contact: "sent" with signal, "waiting" without (it goes out when there's a bar, <see cref="Waiting"/>).
    /// Null when they aren't a contact, are under 18, or the text is empty.
    /// </summary>
    public Text? Send(string id, string body, int day, string clock, long minute, bool signal)
    {
        body = Clean(body);
        if (body.Length == 0 || IsMinor(id)) return null;
        lock (_lock)
        {
            if (!_data.Contacts.ContainsKey(id)) return null;
            var t = ThreadLocked(id);
            var text = new Text { From = You, Body = body, GameDay = day, Clock = clock ?? "", Minute = minute, State = signal ? Sending : Waiting };
            t.Texts.Add(text);
            Trim(t);
            Changed();
            return text;
        }
    }

    /// <summary>They text you. <paramref name="open"/>: the conversation is on screen (it isn't counted unread).</summary>
    public Text? Receive(string id, string body, int day, string clock, long minute, bool open, long readUpTo = long.MaxValue)
    {
        body = Clean(body);
        if (body.Length == 0 || IsMinor(id)) return null;
        lock (_lock)
        {
            if (!_data.Contacts.ContainsKey(id)) return null;
            var t = ThreadLocked(id);
            var text = new Text { From = Them, Body = body, GameDay = day, Clock = clock ?? "", Minute = minute };
            t.Texts.Add(text);
            // They answered: everything of yours before it was read.
            // Only what was there when they began answering (review PH-5: a text sent while they wrote isn't seen yet).
            // Phone pass PH-A2: one still sending (or waiting for a bar) hasn't reached them, so it isn't read either.
            foreach (var x in t.Texts.Where(x => x.From == You && x.State is Sent or Delivered && x.Minute <= readUpTo)) { x.State = Read; if (x.SeenAt < 0) x.SeenAt = minute; }
            if (!open) t.Unread++;
            Trim(t);
            Changed();
            return text;
        }
    }

    /// <summary>Your texts that were sending have gone out (sent). True when one did.</summary>
    public bool MarkSent(string id)
    {
        lock (_lock)
        {
            if (!_data.Threads.TryGetValue(id, out var t)) return false;
            bool any = false;
            foreach (var x in t.Texts.Where(x => x.From == You && x.State == Sending)) { x.State = Sent; any = true; }
            if (any) Changed();
            return any;
        }
    }

    /// <summary>They looked at their phone: your texts are seen (read) now.</summary>
    public void MarkSeen(string id, long minute)
    {
        lock (_lock)
        {
            if (!_data.Threads.TryGetValue(id, out var t)) return;
            bool any = false;
            foreach (var x in t.Texts.Where(x => x.From == You && x.State is Sent or Delivered)) { x.State = Read; x.SeenAt = minute; any = true; }
            t.LookedAt = minute;
            if (any) Changed();
        }
    }

    /// <summary>FB-70: what they'll do with your texts, as (plan, look at, answer at, looked at).</summary>
    public (string plan, long lookAt, long answerAt, long lookedAt) PlanOf(string id)
    {
        lock (_lock) return _data.Threads.TryGetValue(id, out var t) ? (t.Plan, t.LookAt, t.AnswerAt, t.LookedAt) : ("", -1, -1, -1);
    }

    public void SetPlan(string id, string plan, long lookAt, long answerAt)
    {
        lock (_lock)
        {
            if (!_data.Threads.TryGetValue(id, out var t)) return;
            t.Plan = plan; t.LookAt = lookAt; t.AnswerAt = answerAt;
            if (plan.Length == 0) t.LookedAt = -1;
            Changed();
        }
    }

    /// <summary>Every conversation with a plan pending (the runtime's tick).</summary>
    public List<string> Planned()
    {
        lock (_lock) return _data.Threads.Values.Where(t => t.Plan.Length > 0 && !IsMinor(t.Id)).Select(t => t.Id).ToList();
    }

    /// <summary>Every conversation with a text of yours still sending.</summary>
    public List<string> StillSending()
    {
        lock (_lock) return _data.Threads.Values.Where(t => t.Texts.Any(x => x.From == You && x.State == Sending)).Select(t => t.Id).ToList();
    }

    /// <summary>Your sent texts to them reach their phone (they'll read them when they answer).</summary>
    public void MarkDelivered(string id)
    {
        lock (_lock)
        {
            if (!_data.Threads.TryGetValue(id, out var t)) return;
            bool any = false;
            foreach (var x in t.Texts.Where(x => x.From == You && x.State == Sent)) { x.State = Delivered; any = true; }
            if (any) Changed();
        }
    }

    /// <summary>
    /// There's a bar again: every text waiting for signal goes out ("sent"). Returns whose conversations got one, so each
    /// can be answered.
    /// </summary>
    public List<string> SendWaiting()
    {
        var who = new List<string>();
        lock (_lock)
        {
            foreach (var t in _data.Threads.Values)
            {
                bool any = false;
                foreach (var x in t.Texts.Where(x => x.From == You && x.State == Waiting)) { x.State = Sending; any = true; }
                if (any && !IsMinor(t.Id)) who.Add(t.Id);
            }
            if (who.Count > 0) Changed();
        }
        return who;
    }

    public bool AnyWaiting() { lock (_lock) return _data.Threads.Values.Any(t => t.Texts.Any(x => x.From == You && x.State == Waiting)); }

    /// <summary>You opened their conversation.</summary>
    public void MarkRead(string id)
    {
        lock (_lock)
        {
            if (_data.Threads.TryGetValue(id, out var t) && t.Unread > 0) { t.Unread = 0; Changed(); }
        }
    }

    public int UnreadTotal() { lock (_lock) return _data.Threads.Values.Where(t => !IsMinor(t.Id)).Sum(t => t.Unread); }

    /// <summary>Their conversation, oldest first (copies).</summary>
    public List<Text> Texts(string id)
    {
        lock (_lock)
            return _data.Threads.TryGetValue(id, out var t) && !IsMinor(id) ? t.Texts.Select(CopyText).ToList() : new List<Text>();
    }

    /// <summary>
    /// Phone pass PH-A2: the conversation as they have it: yours that haven't reached them yet (waiting for a bar, or still
    /// sending) aren't in it, so they're never answered before they arrive (nor twice, once they do).
    /// </summary>
    public List<Text> TextsForAi(string id) => Texts(id).Where(x => x.From != You || x.State is not (Waiting or Sending)).ToList();

    public int Unread(string id) { lock (_lock) return _data.Threads.TryGetValue(id, out var t) ? t.Unread : 0; }

    private static Text CopyText(Text x) => new() { From = x.From, Body = x.Body, GameDay = x.GameDay, Clock = x.Clock, Minute = x.Minute, State = x.State, SeenAt = x.SeenAt, Voice = x.Voice, VoiceSeconds = x.VoiceSeconds, Wave = x.Wave, Story = x.Story };

    /// <summary>
    /// Story lines (oldest first) mirrored into their conversation: only the ones not mirrored yet (by count), each a text
    /// from them or from you, marked as from the story, with its recording when one was kept. Returns how many were added.
    /// </summary>
    public int MirrorStory(string id, IReadOnlyList<(bool theirs, string text, int day, string clock, long minute)> lines)
    {
        if (IsMinor(id)) return 0;
        lock (_lock)
        {
            if (!_data.Contacts.ContainsKey(id)) return 0;
            var t = ThreadLocked(id);
            int added = 0;
            // Where the last mirrored line is now (the story book may have let its oldest lines go): after it, else by count.
            int from = Math.Max(0, t.StoryLines);
            t.StoryLast ??= "";
            if (t.StoryLast.Length > 0)
            {
                int at = -1;
                for (int i = lines.Count - 1; i >= 0; i--) if ($"{lines[i].minute}|{Clean(lines[i].text)}" == t.StoryLast) { at = i; break; }
                if (at >= 0) from = at + 1;
                else if (from > lines.Count) from = lines.Count;
            }
            for (int i = from; i < lines.Count; i++)
            {
                var (theirs, text, day, clock, minute) = lines[i];
                string body = Clean(text);
                if (body.Length == 0) continue;
                var x = new Text { From = theirs ? Them : You, Body = body, GameDay = day, Clock = clock ?? "", Minute = minute, State = theirs ? "" : Read, Story = true };
                if (theirs && _data.StoryVoices.TryGetValue(body, out var file)) { x.Voice = file; Meta(file, x); } // its length and wave too (FB-74)
                t.Texts.Add(x);
                added++;
            }
            t.StoryLines = lines.Count;
            if (lines.Count > 0) t.StoryLast = $"{lines[^1].minute}|{Clean(lines[^1].text)}";
            if (added > 0) { Trim(t); Changed(); }
            return added;
        }
    }

    /// <summary>
    /// FB-75: story lines from before the conversation's first mirrored one (the game's own history, read in later), each
    /// put in at its place in time; none twice (by its words that day), none you deleted, none once you cleared the
    /// conversation, and none as new (no unread). Returns how many went in.
    /// </summary>
    public int MirrorEarlierStory(string id, IReadOnlyList<(bool theirs, string text, int day, string clock, long minute)> lines)
    {
        if (IsMinor(id)) return 0;
        lock (_lock)
        {
            if (!_data.Contacts.ContainsKey(id)) return 0;
            var t = ThreadLocked(id);
            t.StoryGone ??= new();
            if (t.StoryCleared) return 0;
            var have = new HashSet<string>(t.Texts.Where(x => x.Story).Select(x => GoneKey(x.GameDay, x.Body)), StringComparer.Ordinal);
            var gone = new HashSet<string>(t.StoryGone, StringComparer.Ordinal);
            int added = 0;
            foreach (var (theirs, text, day, clock, minute) in lines)
            {
                string body = Clean(text);
                string key = GoneKey(day, body);
                if (body.Length == 0 || have.Contains(key) || gone.Contains(key)) continue;
                var x = new Text { From = theirs ? Them : You, Body = body, GameDay = day, Clock = clock ?? "", Minute = minute, State = theirs ? "" : Read, Story = true };
                if (theirs && _data.StoryVoices.TryGetValue(body, out var file)) { x.Voice = file; Meta(file, x); }
                int at = t.Texts.FindIndex(o => o.Minute > minute || (o.Minute < 0 && o.GameDay > day));
                t.Texts.Insert(at < 0 ? t.Texts.Count : at, x);
                have.Add(key);
                added++;
            }
            if (added > 0) { Trim(t); Changed(); }
            return added;
        }
    }

    /// <summary>
    /// FB-78: the story lines with no time (those from the game's history: as you played, every line has its clock) taken
    /// out, so a history read again goes back in in order. What you deleted stays gone. Returns how many.
    /// </summary>
    public int DropUntimedStory(string id)
    {
        lock (_lock)
        {
            if (!_data.Threads.TryGetValue(id, out var t)) return 0;
            int n = t.Texts.RemoveAll(x => x.Story && string.IsNullOrEmpty(x.Clock));
            if (n > 0) Changed();
            return n;
        }
    }

    /// <summary>A story line's recording, kept as it was heard (and put on the mirrored line if it's there already).</summary>
    public void StoryVoice(string id, string text, string file, float seconds, string wave)
    {
        if (!VoiceNote.SafeName(file) || IsMinor(id)) return;
        string body = Clean(text);
        lock (_lock)
        {
            _data.StoryVoices[body] = file;
            _data.StoryVoiceMeta[file] = seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "|" + (wave ?? "");
            if (_data.StoryVoices.Count > 600)
                foreach (var k in _data.StoryVoices.Keys.Take(100).ToList()) { _data.StoryVoiceMeta.Remove(_data.StoryVoices[k]); _data.StoryVoices.Remove(k); }
            if (_data.Threads.TryGetValue(id, out var t))
                foreach (var x in t.Texts.Where(x => x.Story && x.From == Them && x.Body == body && x.Voice.Length == 0)) { x.Voice = file; x.VoiceSeconds = seconds; x.Wave = wave; }
            Changed();
        }
    }

    public bool HasStoryVoice(string text) { lock (_lock) return _data.StoryVoices.ContainsKey(Clean(text)); }

    /// <summary>FB-75: the name a game recording goes by on the phone ("game-" and 16 hex of its voice file's name, ".wav").</summary>
    public static string GameVoiceFile(string voice)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(voice ?? ""));
        return "game-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant() + ".wav";
    }

    /// <summary>A note that's the game's own recording, not a file of ours.</summary>
    public static bool IsGameVoice(string file) => file.StartsWith("game-", StringComparison.Ordinal) && VoiceNote.SafeName(file);

    /// <summary>FB-75: a story line voiced by the game's own recording of it (its voice file's name kept; nothing copied).</summary>
    public void StoryGameVoice(string id, string text, string voice, float seconds, string wave)
    {
        if (string.IsNullOrWhiteSpace(voice) || voice.Length >= 200 || IsMinor(id)) return;
        string file = GameVoiceFile(voice);
        lock (_lock)
        {
            _data.StoryGameVoices[file] = voice;
            StoryVoice(id, text, file, seconds, wave);
        }
    }

    /// <summary>The game's voice file behind a game note (null: not one, or forgotten).</summary>
    public string? GameVoiceOf(string file) { lock (_lock) return _data.StoryGameVoices.TryGetValue(file, out var v) ? v : null; }

    /// <summary>A story recording's length and wave onto a mirrored line (under the lock).</summary>
    private void Meta(string file, Text x)
    {
        if (!_data.StoryVoiceMeta.TryGetValue(file, out var m) || m == null) return;
        int bar = m.IndexOf('|');
        if (bar > 0 && float.TryParse(m[..bar], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var secs) && float.IsFinite(secs) && secs > 0) x.VoiceSeconds = secs;
        if (bar >= 0) x.Wave = m[(bar + 1)..];
    }

    /// <summary>Their last text either way (a copy), null for none: the lists show it without copying the whole conversation.</summary>
    public Text? LastText(string id) { lock (_lock) return _data.Threads.TryGetValue(id, out var t) && t.Texts.Count > 0 ? CopyText(t.Texts[^1]) : null; }

    /// <summary>How many texts are in the conversation.</summary>
    public int TextCount(string id) { lock (_lock) return _data.Threads.TryGetValue(id, out var t) ? t.Texts.Count : 0; }

    /// <summary>
    /// Deletes one text (the author: "deleting them won't delete the contact, and the contact will still remember
    /// everything"): only from your phone; what they remember is in their own record. Returns its voice note's file to
    /// delete ("" none). A mirrored story line stays gone (it isn't mirrored again).
    /// </summary>
    public string DeleteText(string id, int index, string body)
    {
        lock (_lock)
        {
            if (!_data.Threads.TryGetValue(id, out var t) || index < 0 || index >= t.Texts.Count || t.Texts[index].Body != body) return "";
            var x = t.Texts[index];
            t.Texts.RemoveAt(index);
            if (x.Story) { t.StoryGone ??= new(); t.StoryGone.Add(GoneKey(x.GameDay, x.Body)); } // FB-75: never mirrored back
            t.Unread = Math.Clamp(t.Unread, 0, t.Texts.Count);
            Changed();
            // Its recording goes only when nothing else uses it (another of Ava's identical lines, say); its story-voice entry
            // stays until the file does (DropVoice), so an Undo brings everything back (review PH-10).
            if (x.Voice.Length == 0 || _data.Threads.Values.Any(o => o.Texts.Any(y => y.Voice == x.Voice))) return "";
            return x.Voice;
        }
    }

    /// <summary>A voice file is gone for good: no story line points at it any more.</summary>
    public void DropVoice(string file)
    {
        lock (_lock)
        {
            if (_data.Threads.Values.Any(t => t.Texts.Any(x => x.Voice == file))) return;
            var keys = _data.StoryVoices.Where(kv => kv.Value == file).Select(kv => kv.Key).ToList();
            foreach (var k in keys) _data.StoryVoices.Remove(k);
            _data.StoryVoiceMeta.Remove(file);
            _data.StoryGameVoices.Remove(file);
            if (keys.Count > 0) Changed();
        }
    }

    /// <summary>A deleted text put back where it was (Undo).</summary>
    public void PutBack(string id, int index, Text text)
    {
        if (IsMinor(id)) return;
        lock (_lock)
        {
            if (!_data.Contacts.ContainsKey(id)) return;
            var t = ThreadLocked(id);
            t.Texts.Insert(Math.Clamp(index, 0, t.Texts.Count), CopyText(text));
            if (text.Story) t.StoryGone?.Remove(GoneKey(text.GameDay, text.Body));
            Trim(t);
            Changed();
        }
    }

    /// <summary>The text at this place, if it still has these words (a copy), for Undo.</summary>
    public Text? TextAt(string id, int index, string body)
    {
        lock (_lock) return _data.Threads.TryGetValue(id, out var t) && index >= 0 && index < t.Texts.Count && t.Texts[index].Body == body ? CopyText(t.Texts[index]) : null;
    }

    /// <summary>Clears the whole conversation (the contact stays, and so do their memories). Returns the voice notes' files to delete.</summary>
    public List<string> ClearThread(string id)
    {
        lock (_lock)
        {
            if (!_data.Threads.TryGetValue(id, out var t)) return new List<string>();
            var files = t.Texts.Where(x => x.Voice.Length > 0).Select(x => x.Voice).Distinct().ToList();
            foreach (var k in _data.StoryVoices.Where(kv => files.Contains(kv.Value)).Select(kv => kv.Key).ToList()) _data.StoryVoices.Remove(k);
            foreach (var f in files) _data.StoryVoiceMeta.Remove(f); // review R82-2
            t.Texts.Clear();
            files = files.Where(f => !_data.Threads.Values.Any(o => o.Texts.Any(y => y.Voice == f))).ToList();
            t.Unread = 0;
            t.Plan = ""; t.LookAt = t.AnswerAt = t.LookedAt = -1; // StoryLines stays: story lines aren't mirrored again
            t.StoryCleared = true; // FB-75: nor earlier ones
            Changed();
            return files;
        }
    }

    /// <summary>The newest <paramref name="count"/> texts (copies) and the index of the first of them.</summary>
    public (List<Text> texts, int first) Newest(string id, int count)
    {
        lock (_lock)
        {
            if (!_data.Threads.TryGetValue(id, out var t) || IsMinor(id)) return (new List<Text>(), 0);
            int first = Math.Max(0, t.Texts.Count - Math.Max(1, count));
            return (t.Texts.Skip(first).Select(CopyText).ToList(), first);
        }
    }

    /// <summary>A voice note comes with their text (the newest of theirs with these words).</summary>
    public bool AttachVoice(string id, string body, string file, float seconds, string wave)
    {
        if (!VoiceNote.SafeName(file) || IsMinor(id)) return false;
        lock (_lock)
        {
            if (!_data.Threads.TryGetValue(id, out var t)) return false;
            var x = t.Texts.LastOrDefault(x => x.From == Them && x.Body == Clean(body) && x.Voice.Length == 0);
            if (x == null) return false;
            x.Voice = file; x.VoiceSeconds = seconds; x.Wave = wave ?? "";
            Changed();
            return true;
        }
    }

    /// <summary>Every conversation with a text in it, newest first: (id, name, last text, unread).</summary>
    public List<(string id, string name, Text last, int unread)> Conversations()
    {
        lock (_lock)
            return _data.Threads.Values
                .Where(t => t.Texts.Count > 0 && _data.Contacts.ContainsKey(t.Id) && !IsMinor(t.Id))
                .Select(t => (t.Id, _data.Contacts[t.Id].Name, CopyText(t.Texts[^1]), t.Unread))
                .OrderByDescending(x => x.Item3.Minute).ThenBy(x => x.Name, StringComparer.Ordinal)
                .ToList();
    }

    /// <summary>The game minute of the last text either way with them, -1 for none.</summary>
    public long LastMinute(string id) { lock (_lock) return _data.Threads.TryGetValue(id, out var t) && t.Texts.Count > 0 ? t.Texts[^1].Minute : -1; }

    public long LastTextFirst { get { lock (_lock) return _data.LastTextFirst; } }

    /// <summary>True the first time it's asked (the first-time card), false after.</summary>
    public bool FirstLook()
    {
        lock (_lock)
        {
            if (_data.Seen) return false;
            _data.Seen = true;
            Changed();
            return true;
        }
    }

    public void TextedFirst(long minute) { lock (_lock) { _data.LastTextFirst = minute; Changed(); } }

    // ------------------------------------------------------------------ forgetting

    /// <summary>Someone under 18, or /resetnpc: their number and texts go. Returns their voice notes' files to delete (review PH-6).</summary>
    public List<string> Forget(string id)
    {
        lock (_lock)
        {
            var files = _data.Threads.TryGetValue(id, out var t) ? t.Texts.Where(x => x.Voice.Length > 0).Select(x => x.Voice).Distinct().ToList() : new List<string>();
            bool a = _data.Contacts.Remove(id), b = _data.Threads.Remove(id), n = _data.Numbers.Remove(id);
            if (a || b || n) Changed();
            return files.Where(f => !ReferencedLocked(f)).ToList();
        }
    }

    /// <summary>Whether any text still uses this voice note (a recording shared by two of Ava's identical lines stays).</summary>
    private bool ReferencedLocked(string file) => _data.StoryVoices.ContainsValue(file) || _data.Threads.Values.Any(t => t.Texts.Any(x => x.Voice == file));

    /// <summary>Every voice note any text uses (pruning never takes one of these: review PH-6).</summary>
    public HashSet<string> VoiceFiles()
    {
        lock (_lock)
            return _data.Threads.Values.SelectMany(t => t.Texts).Where(x => x.Voice.Length > 0).Select(x => x.Voice).Concat(_data.StoryVoices.Values).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>/resetall: everyone's numbers and texts (your own number stays). Returns the voice notes' files to delete.</summary>
    public List<string> Clear()
    {
        lock (_lock)
        {
            var files = VoiceFilesLocked();
            _data.Contacts.Clear();
            _data.Threads.Clear();
            _data.Numbers.Clear();
            _data.StoryVoices.Clear();
            _data.StoryVoiceMeta.Clear();
            _data.StoryGameVoices.Clear();
            _data.LastTextFirst = -1;
            Changed();
            return files;
        }
    }

    private List<string> VoiceFilesLocked() => _data.Threads.Values.SelectMany(t => t.Texts).Where(x => x.Voice.Length > 0).Select(x => x.Voice).Concat(_data.StoryVoices.Values).Distinct().ToList();
}
