using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Phone;

/// <summary>
/// Nightline (FB-71), the city's social app on the phone: every line (post) residents and you drop, who liked or disliked
/// it and why, the comments, who has seen it, everyone's handle, and the photos. Per save (nightline.json, part of FB-61's
/// snapshot). Written on a pool thread (BookWriter), read under its lock from any thread. Never anyone under 18 (IsMinor at
/// every entry). No Unity, tested.
/// </summary>
public sealed class LineBook
{
    public sealed class Reaction
    {
        public string Who { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Up { get; set; }
        public long Minute { get; set; }
        /// <summary>Why, in their own words (kept for their memory; never shown).</summary>
        public string Why { get; set; } = "";
    }

    public sealed class Comment
    {
        public string Id { get; set; } = "";
        public string Who { get; set; } = "";
        public string Name { get; set; } = "";
        public string Text { get; set; } = "";
        public long Minute { get; set; }
        /// <summary>The comment this answers ("" none).</summary>
        public string ReplyTo { get; set; } = "";
    }

    public sealed class Line
    {
        public string Id { get; set; } = "";
        /// <summary>A resident's id, or <see cref="You"/>.</summary>
        public string Author { get; set; } = "";
        public string Name { get; set; } = "";
        public string Handle { get; set; } = "";
        public string Text { get; set; } = "";
        /// <summary>Where it was posted from ("Kiko's Noodle Bar · Meridian Market"), "" when unknown.</summary>
        public string Place { get; set; } = "";
        public long Minute { get; set; }
        public int Day { get; set; }
        public string Clock { get; set; } = "";
        /// <summary>Its photo's id ("" none); <see cref="Developing"/> while it's being made.</summary>
        public string Photo { get; set; } = "";
        public bool Developing { get; set; }
        public List<Reaction> Reactions { get; set; } = new();
        public List<Comment> Comments { get; set; } = new();
        public List<string> SeenBy { get; set; } = new();
        /// <summary>Yours, waiting for a bar of signal.</summary>
        public bool Waiting { get; set; }
        /// <summary>The author looked at who reacted, up to this many reactions.</summary>
        public int AuthorLooked { get; set; }
    }

    public sealed class Photo
    {
        public string Id { get; set; } = "";
        public string Author { get; set; } = "";
        public string Line { get; set; } = "";
        /// <summary>The file in the photos folder (a plain name).</summary>
        public string File { get; set; } = "";
        /// <summary>What's in it, as the poster asked for it: what anyone who saw it remembers.</summary>
        public string Shows { get; set; } = "";
        /// <summary>FB-80: a photo you took (from your gallery), not one made by the image service.</summary>
        public bool Mine { get; set; }
        /// <summary>FB-80: someone under 18 was in view, or the mod didn't take it: never shown to an AI.</summary>
        public bool NoAi { get; set; }
    }

    public sealed class Data
    {
        public List<Line> Lines { get; set; } = new();
        public Dictionary<string, string> Handles { get; set; } = new();
        public Dictionary<string, Photo> Photos { get; set; } = new();
        /// <summary>The game minute someone next uses the app (-1: not planned yet).</summary>
        public long NextUse { get; set; } = -1;
        /// <summary>Older files: reactions and comments on your lines already seen, as a count (read once into <see cref="YouSeenAt"/>).</summary>
        public int YouSeen { get; set; }
        /// <summary>
        /// The 2026-10-20 audit: the game minute of the newest reaction or comment on your lines you've seen; the badge counts
        /// what's newer, so news taken away (a line deleted, someone forgotten) never hides news after it. MinValue: an older file.
        /// </summary>
        public long YouSeenAt { get; set; } = long.MinValue;
    }

    public const string You = "you";
    public const int MaxLines = 300, MaxComments = 60, MaxReactions = 200, MaxSeen = 400, MaxText = 280, MaxComment = 220, MaxWhy = 160, MaxShows = 400;

    private readonly string _path;
    private readonly object _lock = new();
    private Data _data;
    private readonly Action<string>? _warn;
    private bool _readOnly, _warnedReadOnly, _dirty;
    private readonly BookWriter _writer;
    private int _serial;
    public Func<string, bool> IsMinor { get; set; } = _ => false;

    public LineBook(string path, Action<string>? warn = null)
    {
        _path = path;
        _warn = warn;
        _data = DataFile.LoadBook(_path, t => JsonSerializer.Deserialize<Data>(t, NpcStore.Json), _warn, out _readOnly) ?? new Data();
        Repair(_data);
        _writer = new BookWriter(_path, Snapshot, _warn, failed: () => { lock (_lock) _dirty = true; });
    }

    /// <summary>A hand-edited or older file: nothing null is read back.</summary>
    private static void Repair(Data d)
    {
        d.Lines ??= new();
        d.Handles ??= new();
        d.Photos ??= new();
        d.Lines.RemoveAll(l => l == null || string.IsNullOrEmpty(l.Id));
        foreach (var k in d.Handles.Where(kv => kv.Value == null || Handle(kv.Value) != kv.Value).Select(kv => kv.Key).ToList()) d.Handles.Remove(k);
        foreach (var k in d.Photos.Where(kv => kv.Value == null || !SafeFile(kv.Value.File)).Select(kv => kv.Key).ToList()) d.Photos.Remove(k);
        foreach (var (key, p) in d.Photos) { p.Id = key; p.Author ??= ""; p.Line ??= ""; p.Shows ??= ""; }
        foreach (var l in d.Lines)
        {
            l.Author ??= ""; l.Name ??= ""; l.Handle ??= ""; l.Text ??= ""; l.Place ??= ""; l.Clock ??= ""; l.Photo ??= "";
            l.Reactions ??= new(); l.Comments ??= new(); l.SeenBy ??= new();
            l.Reactions.RemoveAll(r => r == null || string.IsNullOrEmpty(r.Who));
            l.Comments.RemoveAll(c => c == null || string.IsNullOrEmpty(c.Who));
            l.SeenBy.RemoveAll(s => s == null);
            foreach (var r in l.Reactions) { r.Name ??= ""; r.Why ??= ""; }
            foreach (var c in l.Comments) { c.Id ??= ""; c.Name ??= ""; c.Text ??= ""; c.ReplyTo ??= ""; }
            l.Developing = false; // a photo being made never survives a restart (review NL-2)
            if (l.Photo.Length > 0 && !d.Photos.ContainsKey(l.Photo)) l.Photo = "";
        }
        if (d.YouSeen < 0) d.YouSeen = 0;
        if (d.YouSeenAt == long.MinValue) // an older file: the count read as how far you'd got (the newest of it unseen)
        {
            var minutes = News(d).OrderByDescending(m => m).ToList();
            int unseen = Math.Max(0, minutes.Count - d.YouSeen);
            d.YouSeenAt = unseen < minutes.Count ? minutes[unseen] : -1;
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

    private void Changed() { _dirty = true; _writer.SaveSoon(); }
    public void Flush() { _writer.WaitIdle(); _writer.Write(); }
    public void Close() { try { Flush(); } finally { _writer.Close(); } }

    // ------------------------------------------------------------------ handles

    /// <summary>
    /// A handle as the app shows it, without the @: lower case letters, digits, dots and underscores, 3 to 24 long; "" when
    /// nothing usable is left ("Mara Voss" -> "maravoss", "@Rig.Must.Live!" -> "rig.must.live").
    /// </summary>
    public static string Handle(string? raw)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in (raw ?? "").Trim().TrimStart('@').ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.') sb.Append(c);
            else if (c is ' ' or '-') sb.Append(sb.Length > 0 && sb[^1] != '_' ? "" : "");
            if (sb.Length >= 24) break;
        }
        string h = sb.ToString().Trim('.', '_');
        while (h.Contains("..")) h = h.Replace("..", ".");
        return h.Length >= 3 ? h : "";
    }

    /// <summary>Their handle: the one they chose (or were given), else made from their name; never one someone else has.</summary>
    public string HandleOf(string id, string name)
    {
        if (id == You) return You;
        lock (_lock)
        {
            if (_data.Handles.TryGetValue(id, out var h)) return h;
            return Unique(Handle(name) is { Length: > 0 } n ? n : "resident", id);
        }
    }

    /// <summary>Their own choice of handle (the AI's, from who they are), kept when it's free; returns the one kept.</summary>
    public string SetHandle(string id, string name, string? wanted)
    {
        if (id.Length == 0 || id == You || IsMinor(id)) return "";
        lock (_lock)
        {
            if (_data.Handles.TryGetValue(id, out var had)) return had;
            string h = Unique(Handle(wanted) is { Length: > 0 } w ? w : Handle(name) is { Length: > 0 } n ? n : "resident", id);
            _data.Handles[id] = h;
            Changed();
            return h;
        }
    }

    public bool HasHandle(string id) { lock (_lock) return _data.Handles.ContainsKey(id); }

    private string Unique(string h, string id)
    {
        if (h == You) h = "you_" ;
        string tryH = h; int n = 2;
        while (_data.Handles.Any(kv => kv.Key != id && kv.Value == tryH) || tryH == You) tryH = (h.Length > 21 ? h[..21] : h) + n++;
        return tryH;
    }

    // ------------------------------------------------------------------ lines

    public static string CleanText(string? text, int max)
    {
        var s = (text ?? "").Replace("\r", "").Replace("\n", " ").Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length > max ? s[..max].TrimEnd() : s;
    }

    /// <summary>A new line (theirs or yours). Returns its id, or "" when it can't be (empty, a minor).</summary>
    public string Post(string author, string name, string text, string place, long minute, int day, string clock, bool waiting = false, bool photoComing = false)
    {
        string body = CleanText(text, MaxText);
        // FB-80: a photo of yours may go up with no words.
        if ((body.Length == 0 && !(author == You && photoComing)) || author.Length == 0 || IsMinor(author)) return "";
        lock (_lock)
        {
            string id = $"l{minute}-{++_serial}-{Math.Abs((author + body).GetHashCode()) % 100000}";
            _data.Lines.Add(new Line
            {
                Id = id, Author = author, Name = author == You ? "You" : CleanText(name, 60), Handle = author == You ? You : HandleOf(author, name),
                Text = body, Place = CleanText(place, 80), Minute = minute, Day = day, Clock = clock ?? "", Waiting = waiting, Developing = photoComing,
            });
            Trim();
            Changed();
            return id;
        }
    }

    /// <summary>The oldest lines go past <see cref="MaxLines"/>; their photo files are returned to delete.</summary>
    private List<string> Trim()
    {
        var gone = new List<string>();
        while (_data.Lines.Count > MaxLines)
        {
            var old = _data.Lines[0];
            _data.Lines.RemoveAt(0);
            if (old.Photo.Length > 0 && _data.Photos.Remove(old.Photo, out var p)) gone.Add(p.File);
        }
        _trimmed.AddRange(gone);
        return gone;
    }

    private readonly List<string> _trimmed = new();

    /// <summary>Photo files that went with old lines since last asked (to delete from the PC).</summary>
    public List<string> TakeTrimmedPhotos() { lock (_lock) { var l = _trimmed.ToList(); _trimmed.Clear(); return l; } }

    /// <summary>Your waiting lines go up (a bar of signal came back).</summary>
    public int SendWaiting()
    {
        lock (_lock)
        {
            int n = 0;
            foreach (var l in _data.Lines.Where(l => l.Waiting)) { l.Waiting = false; n++; }
            if (n > 0) Changed();
            return n;
        }
    }

    /// <summary>
    /// A like (up) or dislike from someone, replacing theirs; "none" (null) takes it back. Their own line can't be reacted to
    /// by themselves. Returns whether it changed anything.
    /// </summary>
    public bool React(string lineId, string who, string name, bool? up, long minute, string why = "")
    {
        if (who.Length == 0 || IsMinor(who)) return false;
        lock (_lock)
        {
            var l = Find(lineId);
            if (l == null || l.Author == who) return false;
            var had = l.Reactions.FirstOrDefault(r => r.Who == who);
            if (up == null)
            {
                if (had == null) return false;
                l.Reactions.Remove(had);
            }
            else if (had != null && had.Up == up) return false;
            else
            {
                if (had != null) l.Reactions.Remove(had);
                l.Reactions.Add(new Reaction { Who = who, Name = who == You ? "You" : CleanText(name, 60), Up = up.Value, Minute = minute, Why = CleanText(why, MaxWhy) });
                if (l.Reactions.Count > MaxReactions) l.Reactions.RemoveAt(0);
            }
            See(l, who);
            Changed();
            return true;
        }
    }

    /// <summary>A comment (theirs or yours), maybe answering another. Returns its id or "".</summary>
    public string AddComment(string lineId, string who, string name, string text, long minute, string replyTo = "")
    {
        string body = CleanText(text, MaxComment);
        if (body.Length == 0 || who.Length == 0 || IsMinor(who)) return "";
        lock (_lock)
        {
            var l = Find(lineId);
            if (l == null) return "";
            string id = $"c{minute}-{++_serial}";
            l.Comments.Add(new Comment { Id = id, Who = who, Name = who == You ? "You" : CleanText(name, 60), Text = body, Minute = minute, ReplyTo = l.Comments.Any(c => c.Id == replyTo) ? replyTo : "" });
            if (l.Comments.Count > MaxComments) l.Comments.RemoveAt(0);
            See(l, who);
            Changed();
            return id;
        }
    }

    /// <summary>Someone scrolled past it.</summary>
    public void MarkSeen(string lineId, string who)
    {
        if (who.Length == 0 || IsMinor(who)) return;
        lock (_lock)
        {
            var l = Find(lineId);
            if (l == null || l.SeenBy.Contains(who)) return;
            See(l, who);
            Changed();
        }
    }

    private static void See(Line l, string who)
    {
        if (who == l.Author || l.SeenBy.Contains(who)) return;
        l.SeenBy.Add(who);
        if (l.SeenBy.Count > MaxSeen) l.SeenBy.RemoveAt(0);
    }

    public bool HasSeen(string lineId, string who) { lock (_lock) return Find(lineId)?.SeenBy.Contains(who) == true; }

    /// <summary>The author looked at who reacted: what's new since their last look (and it's marked looked).</summary>
    public List<Reaction> AuthorLooks(string lineId)
    {
        var news = NewSince(lineId, out int count);
        MarkLooked(lineId, count);
        return news;
    }

    /// <summary>Who reacted since the author last looked, without marking it (review NL-9: marked only once the visit worked).</summary>
    public List<Reaction> NewSince(string lineId, out int count)
    {
        lock (_lock)
        {
            var l = Find(lineId);
            count = l?.Reactions.Count ?? 0;
            if (l == null || l.Reactions.Count <= l.AuthorLooked) return new List<Reaction>();
            return l.Reactions.Skip(Math.Min(l.AuthorLooked, l.Reactions.Count)).Select(Copy).ToList();
        }
    }

    public void MarkLooked(string lineId, int count)
    {
        lock (_lock)
        {
            var l = Find(lineId);
            if (l == null || count <= l.AuthorLooked) return;
            l.AuthorLooked = Math.Min(count, l.Reactions.Count);
            Changed();
        }
    }

    // ------------------------------------------------------------------ your deletes (with Undo)

    /// <summary>
    /// One of your own lines taken down (yours only): it and where it was are returned so Undo can put it back; its photo
    /// stays until the Undo lapses. Residents who saw it keep their memory of it.
    /// </summary>
    public (Line? line, int index) TakeDown(string lineId, string who)
    {
        lock (_lock)
        {
            int i = _data.Lines.FindIndex(l => l.Id == lineId && l.Author == who);
            if (i < 0) return (null, -1);
            var l = _data.Lines[i];
            _data.Lines.RemoveAt(i);
            Changed();
            return (l, i);
        }
    }

    /// <summary>Undo: the line back where it was.</summary>
    public void PutBack(Line line, int index)
    {
        lock (_lock)
        {
            if (_data.Lines.Any(l => l.Id == line.Id)) return;
            _data.Lines.Insert(Math.Clamp(index, 0, _data.Lines.Count), line);
            Changed();
        }
    }

    /// <summary>The Undo lapsed: a taken-down line's photo file, if nothing else uses it, to delete.</summary>
    public string DropPhoto(Line line)
    {
        lock (_lock)
        {
            if (line.Photo.Length == 0 || _data.Lines.Any(l => l.Photo == line.Photo) || !_data.Photos.Remove(line.Photo, out var p)) return "";
            Changed();
            return p.File;
        }
    }

    /// <summary>One of your own comments taken down; returned with its place for Undo.</summary>
    public (Comment? comment, int index) TakeDownComment(string lineId, string commentId, string who)
    {
        lock (_lock)
        {
            var l = Find(lineId);
            int i = l?.Comments.FindIndex(c => c.Id == commentId && c.Who == who) ?? -1;
            if (l == null || i < 0) return (null, -1);
            var c = l.Comments[i];
            l.Comments.RemoveAt(i);
            Changed();
            return (c, i);
        }
    }

    public void PutBackComment(string lineId, Comment comment, int index)
    {
        lock (_lock)
        {
            var l = Find(lineId);
            if (l == null || l.Comments.Any(c => c.Id == comment.Id)) return;
            l.Comments.Insert(Math.Clamp(index, 0, l.Comments.Count), comment);
            Changed();
        }
    }

    /// <summary>A line of yours still waiting for signal, cancelled (no Undo: it never went up).</summary>
    public bool CancelWaiting(string lineId)
    {
        lock (_lock)
        {
            int n = _data.Lines.RemoveAll(l => l.Id == lineId && l.Author == You && l.Waiting);
            if (n > 0) Changed();
            return n > 0;
        }
    }

    // ------------------------------------------------------------------ photos

    /// <summary>A photo's file: a plain name in the photos folder (letters, digits, - and _), a .png or .jpg; never a path.</summary>
    public static bool SafeFile(string? file)
    {
        if (string.IsNullOrEmpty(file) || file.Length > 80) return false;
        string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
        if (ext is not (".png" or ".jpg")) return false;
        string stem = file[..^ext.Length];
        return stem.Length > 0 && stem.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');
    }

    /// <summary>A line's photo is ready (its file kept), or couldn't be made (null: it posts without one).</summary>
    /// <summary>Returns whether the file was kept (false: the line's gone, or no file; the caller deletes it).</summary>
    /// <summary>FB-80: your own photo on your line (a copy in the photos folder); <paramref name="noAi"/>: never shown to an AI.</summary>
    public bool MinePhoto(string lineId, string file, bool noAi)
    {
        if (!PhotoDone(lineId, file, "")) return false;
        lock (_lock)
        {
            if (Find(lineId) is { } l && _data.Photos.TryGetValue(l.Photo, out var p)) { p.Mine = true; p.NoAi = noAi; Changed(); }
        }
        return true;
    }

    public bool PhotoDone(string lineId, string? file, string shows)
    {
        lock (_lock)
        {
            var l = Find(lineId);
            if (l == null) return false;
            l.Developing = false;
            bool kept = false;
            if (file != null && SafeFile(file))
            {
                string pid = "p" + lineId;
                _data.Photos[pid] = new Photo { Id = pid, Author = l.Author, Line = lineId, File = file, Shows = CleanText(shows, MaxShows) };
                l.Photo = pid;
                kept = true;
            }
            Changed();
            return kept;
        }
    }

    public Photo? PhotoOf(string photoId) { lock (_lock) return _data.Photos.TryGetValue(photoId, out var p) ? new Photo { Id = p.Id, Author = p.Author, Line = p.Line, File = p.File, Shows = p.Shows, Mine = p.Mine, NoAi = p.NoAi } : null; }

    /// <summary>Every photo file in use (pruning keeps these).</summary>
    public HashSet<string> PhotoFiles() { lock (_lock) return _data.Photos.Values.Select(p => p.File).ToHashSet(StringComparer.OrdinalIgnoreCase); }

    // ------------------------------------------------------------------ reading

    private Line? Find(string id) => _data.Lines.FirstOrDefault(l => l.Id == id);

    public Line? Get(string id) { lock (_lock) { var l = Find(id); return l == null || IsMinor(l.Author) ? null : Copy(l); } }

    /// <summary>The newest lines first (copies), at most <paramref name="count"/>; <paramref name="keep"/> filters by author.</summary>
    public List<Line> Feed(int count, Func<string, bool>? keep = null)
    {
        lock (_lock)
        {
            var list = new List<Line>();
            for (int i = _data.Lines.Count - 1; i >= 0 && list.Count < count; i--)
            {
                var l = _data.Lines[i];
                if (IsMinor(l.Author) || (keep != null && !keep(l.Author))) continue;
                list.Add(Copy(l));
            }
            return list;
        }
    }

    public int Count { get { lock (_lock) return _data.Lines.Count; } }

    /// <summary>Reactions and comments from others on your lines, in all (the badge is this minus what you've seen).</summary>
    /// <summary>When each reaction and comment others left on your lines came (game minutes).</summary>
    private static IEnumerable<long> News(Data d) => d.Lines.Where(l => l.Author == You).SelectMany(l =>
        l.Reactions.Where(r => r.Who != You).Select(r => r.Minute).Concat(l.Comments.Where(c => c.Who != You).Select(c => c.Minute)));

    public int Unseen { get { lock (_lock) return News(_data).Count(m => m > _data.YouSeenAt); } }

    /// <summary>You opened the app: the badge clears.</summary>
    public void SeenYours()
    {
        lock (_lock)
        {
            long newest = News(_data).DefaultIfEmpty(-1).Max();
            if (newest > _data.YouSeenAt) { _data.YouSeenAt = newest; Changed(); }
        }
    }

    public long NextUse { get { lock (_lock) return _data.NextUse; } set { lock (_lock) { _data.NextUse = value; Changed(); } } }

    // ------------------------------------------------------------------ forgetting

    /// <summary>Someone is forgotten (/resetnpc, or found to be under 18): their lines, reactions, comments and handle go. Returns their photo files.</summary>
    public List<string> Forget(string id)
    {
        lock (_lock)
        {
            var files = new List<string>();
            foreach (var l in _data.Lines.Where(l => l.Author == id).ToList())
            {
                if (l.Photo.Length > 0 && _data.Photos.Remove(l.Photo, out var p)) files.Add(p.File);
                _data.Lines.Remove(l);
            }
            foreach (var l in _data.Lines)
            {
                l.Reactions.RemoveAll(r => r.Who == id);
                l.Comments.RemoveAll(c => c.Who == id);
                l.SeenBy.Remove(id);
            }
            _data.Handles.Remove(id);
            Changed();
            return files;
        }
    }

    /// <summary>Everything (/resetall). Returns every photo file.</summary>
    public List<string> Clear()
    {
        lock (_lock)
        {
            var files = _data.Photos.Values.Select(p => p.File).ToList();
            _data = new Data();
            Changed();
            return files;
        }
    }

    private static Reaction Copy(Reaction r) => new() { Who = r.Who, Name = r.Name, Up = r.Up, Minute = r.Minute, Why = r.Why };

    private static Line Copy(Line l) => new()
    {
        Id = l.Id, Author = l.Author, Name = l.Name, Handle = l.Handle, Text = l.Text, Place = l.Place, Minute = l.Minute, Day = l.Day,
        Clock = l.Clock, Photo = l.Photo, Developing = l.Developing, Waiting = l.Waiting, AuthorLooked = l.AuthorLooked,
        Reactions = l.Reactions.Select(Copy).ToList(),
        Comments = l.Comments.Select(c => new Comment { Id = c.Id, Who = c.Who, Name = c.Name, Text = c.Text, Minute = c.Minute, ReplyTo = c.ReplyTo }).ToList(),
        SeenBy = l.SeenBy.ToList(),
    };
}
