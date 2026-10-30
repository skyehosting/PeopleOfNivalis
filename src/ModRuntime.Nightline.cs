using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Il2CppInterop.Runtime;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Phone;
using NivalisAIChat.UI;
using UnityEngine;
using Character = Nivalis.Character;
using Random = System.Random;

namespace NivalisAIChat;

/// <summary>
/// Nightline (FB-71), the city's social app on the phone: someone (near you or anywhere in the city) opens it every 30 to
/// 120 game minutes (LineEveryMin/Max), posts, scrolls, or both (LinePlan), and one AI request decides what they post and
/// what they make of what they saw (Lines); it's kept in the book (LineBook, per save) and in their memory. Street views
/// are taken in the background now and then (no UI, nobody in them) and a resident's own picture the first time they're
/// near you (adults only); a post may come with a photo made from those (ImageClient, your own image service). Every visit
/// and photo goes through the pacer (AiAllowed) and the Usage Optimizer (residents' "nightline", "nightline photo").
/// </summary>
public static partial class ModRuntime
{
    private static LineBook? _lines;
    private static ShotIndex? _shots;
    private static ImageClient? _images;
    private static float _nextShotLook, _nextFaceLook;
    private static long _nextShotAt = -1;
    private static int _lineDay = -1, _postsToday, _reactsToday, _commentsToday, _photosToday;
    private static readonly HashSet<string> FacesTried = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> LineVisits = new();
    private static readonly LockedRandom LineRng = new();

    private static string ShotsDir => Path.Combine(_dataDir, "nightline-shots");
    private static string FacesDir => Path.Combine(_dataDir, "nightline-faces");
    private static string PhotosDir => Path.Combine(_dataDir, "nightline-photos");

    // ------------------------------------------------------------------ the book's life

    private static void OpenLineBook(string dataDir)
    {
        try { _lines = new LineBook(Path.Combine(dataDir, "nightline.json"), Log.Warn) { IsMinor = KnownUnder18 }; }
        catch (Exception e) { Log.Warn("Nightline couldn't be opened: " + e.Message); _lines = null; }
        _lineDay = -1;
        LineVisits.Clear();
        try { PruneNightlineFiles(); } catch { }
    }

    private static void CloseLineBook()
    {
        try { FreeFaceSprites(); LineModels.Clear(); } catch { } // FB-78: another save has other people
        try { _lines?.Close(); } catch (Exception e) { Log.Warn("Saving Nightline: " + e.Message); }
    }
    private static void FlushLineBook() { try { _lines?.Flush(); } catch { } }

    private static ImageSettings ImageSettingsNow() => new()
    {
        Format = _cfg.ImageApiFormat.Value ?? "OpenAI", Url = _cfg.ImageApiUrl.Value ?? "", Key = _cfg.ImageApiKey.Value ?? "",
        Model = _cfg.ImageModel.Value ?? "", Quality = _cfg.ImageQuality.Value ?? "low",
    };

    private static ImageClient Images => _images ??= new ImageClient(ImageSettingsNow) { Budget = _budget, Ledger = _voiceLedger };
    private static ShotIndex Shots => _shots ??= new ShotIndex(Path.Combine(ShotsDir, "shots.json"), Log.Warn);

    private static bool NightlineOn => Features.Phone && Features.Nightline && _lines != null && !_resetting;

    // ------------------------------------------------------------------ every frame (from the phone window's tick)

    private static void TickNightline()
    {
        if (!NightlineOn) return;
        float now = Time.unscaledTime;
        int day = GameContext.Day();
        if (day != _lineDay) { _lineDay = day; _postsToday = _reactsToday = _commentsToday = _photosToday = 0; }
        if (now >= _nextShotLook) { _nextShotLook = now + 7f; try { MaybeCityShot(); } catch (Exception e) { Log.Debug("city shot: " + e.Message); } }
        if (now >= _nextFaceLook) { _nextFaceLook = now + 11f; try { MaybeFace(); } catch (Exception e) { Log.Debug("face: " + e.Message); } }
        try { TickFaceSprites(); } catch (Exception e) { Log.Debug("face picture: " + e.Message); } // FB-78
        long minute = GameNow();
        if (minute < 0) return;
        var book = _lines!;
        if (book.NextUse < 0 || book.NextUse - minute > 1440 * 3) { book.NextUse = minute + LinePlan.NextGap(LineRng, _cfg.LineEveryMin.Value, _cfg.LineEveryMax.Value); return; }
        if (minute < book.NextUse) return;
        book.NextUse = minute + LinePlan.NextGap(LineRng, _cfg.LineEveryMin.Value, _cfg.LineEveryMax.Value);
        try { StartVisit(out string why); if (why.Length > 0) Log.Debug("nightline: " + why); }
        catch (Exception e) { Log.Debug("nightline: " + e.Message); }
    }

    // ------------------------------------------------------------------ a resident opens the app

    private static bool StartVisit(out string why)
    {
        why = "";
        if (!AiAllowed()) { why = "the AI limit"; return false; }
        // The residents' hourly cap and its pacer, as everything residents start (review NL-3).
        var cs = ChatterSettingsNow();
        if (Social.ChatterPlan.InLastHour(ChatterStarts, Time.unscaledTime) >= cs.PerHour || !Social.ChatterPlan.Spaced(ChatterStarts, Time.unscaledTime, cs.PerHour, 2))
        { why = "the residents' hourly cap, or the pacer's spacing"; return false; } // review NL-3b
        var rng = LineRng;
        var (post, scroll) = LinePlan.Choose(rng, _cfg.LinePostWeight.Value, _cfg.LineScrollWeight.Value, _cfg.LineBothWeight.Value);
        if (post && _postsToday >= Math.Max(0, _cfg.LinePostsPerDay.Value)) post = false;
        if (!post && !scroll) { why = "nothing to do (weights, or today's posts are used up)"; return false; }
        var brain = ActiveBrain;
        var (id, name, rec, body) = PickVisitor(rng);
        if (id.Length == 0 || rec == null) { why = "nobody free to use the app"; return false; }
        if (!LineVisits.TryAdd(id, 0)) { why = "already on it"; return false; }
        try { return Launch(id, name, rec, body, post, scroll, rng, out why); }
        catch { LineVisits.TryRemove(id, out _); throw; } // review NL-8
    }

    private static bool Launch(string id, string name, NpcRecord rec, Character? body, bool post, bool scroll, Random rng, out string why)
    {
        why = "";
        var brain = ActiveBrain;
        var book = _lines!;
        var snap = GameContext.Capture(null);
        snap.PlayerGender = _cfg.PlayerGender.Value;
        long minute = GameNow();
        string doing = body != null && rng.Next(100) < _cfg.LineDoingsWeight.Value ? DoingOf(body) : "";
        string withYou = _date != null && _date.Id == id ? "Right now you're out on a date with the newcomer (you can post about it if you like)." : "";
        var seen = scroll ? BuildSeen(id, rec, rng) : new List<Lines.Seen>();
        if (!post && seen.Count == 0) { LineVisits.TryRemove(id, out _); why = "nothing new to scroll"; return false; }
        bool photo = post && Images != null && ImageSettingsNow().Ready && _photosToday < Math.Max(0, _cfg.LinePhotosPerDay.Value) && rng.Next(100) < _cfg.LinePhotoChance.Value;
        var areas = photo ? Shots.Areas().Where(a => Shots.Count(a) >= 2).ToList() : new List<string>();
        bool wantHandle = !book.HasHandle(id) && rng.Next(100) < Math.Clamp(_cfg.LineHandleStyle.Value, 0, 100);
        if (!book.HasHandle(id) && !wantHandle) book.SetHandle(id, name, null);
        bool hasFace = _cfg.LineSelfInPhotos.Value && File.Exists(FacePath(id)); // FB-78: a face kept for their picture alone isn't for photos
        string place = body != null ? (GameContext.PlaceName(GameContext.VisitPlace(body)) is { Length: > 0 } v ? v : _here.place) : "";
        int generation = _resetGeneration;
        NoteStart("nightline");
        Task.Run(async () =>
        {
            try
            {
                var (result, nm, error) = await brain.LineAsync(id, snap, post, seen, wantHandle, areas, hasFace, doing, withYou, CancellationToken.None).ConfigureAwait(false);
                if (result == null) { if (error != null) Log.Debug($"nightline ({id}): {error}"); return; }
                MainThread.Enqueue(() => ApplyVisit(id, nm, result, seen, snap, minute, place, generation));
            }
            catch (Exception e) { Log.Debug("nightline: " + e.Message); }
            finally { LineVisits.TryRemove(id, out _); }
        });
        return true;
    }

    /// <summary>
    /// Who opens the app: someone near you (a resident the mod knows, at least 18, not in a chat or busy with you) or, by
    /// LineOffWorldWeight, anyone the mod knows elsewhere in the city.
    /// </summary>
    private static (string id, string name, NpcRecord? rec, Character? body) PickVisitor(Random rng)
    {
        var brain = ActiveBrain;
        string? talking = _session?.Record.Id;
        bool offWorld = rng.Next(100) < Math.Clamp(_cfg.LineOffWorldWeight.Value, 0, 100);
        var pc = GameContext.PlayerCharacter();
        if (!offWorld && pc != null)
        {
            Vector3 me = pc.transform.position;
            var near = NearBodies(pc).Where(b => Flat(b.pos - me).magnitude <= 30f).OrderBy(_ => rng.Next()).Take(12).ToList();
            foreach (var (c, _, _) in near)
            {
                try
                {
                    string id = GameContext.NpcId(c);
                    if (id.Length == 0 || id.StartsWith("extra_", StringComparison.Ordinal) || id == talking || KnownUnder18(id) || LineVisits.ContainsKey(id)) continue;
                    if (MinorInfo(c, id).minor || !brain.Store.Has(id)) continue;
                    var rec = brain.Store.Glance(id);
                    if (rec == null || brain.IsKnownMinor(rec) || rec.Persona.Name.Length == 0) continue;
                    return (id, rec.Persona.Name, rec, c);
                }
                catch { }
            }
        }
        var ids = brain.Store.Ids();
        for (int tries = 0; tries < 12 && ids.Count > 0; tries++)
        {
            string id = ids[rng.Next(Math.Min(ids.Count, 300))];
            if (id == talking || KnownUnder18(id) || LineVisits.ContainsKey(id) || id.StartsWith("extra_", StringComparison.Ordinal)) continue;
            var rec = brain.Store.Glance(id);
            if (rec == null || brain.IsKnownMinor(rec) || rec.Persona.Name.Length == 0) continue;
            return (id, rec.Persona.Name, rec, null);
        }
        return ("", "", null, null);
    }

    /// <summary>What someone near you is doing, in words (eating at a place, at work, or out in the district).</summary>
    private static string DoingOf(Character c)
    {
        try
        {
            if (GameContext.VisitOf(c).step == "Eating" && GameContext.PlaceName(GameContext.VisitPlace(c)) is { Length: > 0 } at) return "eating at " + at;
            if (GameContext.IsWorkerTask(c) && GameContext.PlaceName(GameContext.WorkplaceOf(c).place) is { Length: > 0 } work) return "working at " + work;
            if (_here.place.Length > 0) return "out in " + _here.place;
        }
        catch { }
        return "";
    }

    /// <summary>The lines they come to while scrolling (LinePlan), put to them with how they feel about each poster.</summary>
    private static List<Lines.Seen> BuildSeen(string viewer, NpcRecord rec, Random rng)
    {
        var book = _lines!;
        int youWeight = Math.Clamp(_cfg.LineReactToYouWeight.Value, 0, 300);
        var feed = book.Feed(80).Where(l => !l.Waiting && !KnownUnder18(l.Author)).ToList();
        // Your lines: shown as often as anyone's at 100, never at 0, a little ahead at more than 100.
        feed = feed.Where(l => l.Author != LineBook.You || rng.Next(100) < Math.Min(100, youWeight)).ToList();
        if (youWeight > 100) feed = feed.OrderByDescending(l => l.Author == LineBook.You && rng.Next(100) < youWeight - 100).ThenByDescending(l => l.Minute).ToList();
        bool reactsLeft = _reactsToday < Math.Max(0, _cfg.LineReactionsPerDay.Value), commentsLeft = _commentsToday < Math.Max(0, _cfg.LineCommentsPerDay.Value);
        var looks = LinePlan.Scroll(rng, feed.Select(l => (l.Id, l.Author)).ToList(), viewer, lid => feed.First(x => x.Id == lid).SeenBy.Contains(viewer),
            _cfg.LineReactChance.Value, _cfg.LineCommentChance.Value, _cfg.LineSeenFalloff.Value);
        var seen = new List<Lines.Seen>();
        int k = 0;
        long now = GameNow();
        foreach (var look in looks)
        {
            var l = feed.First(x => x.Id == look.Id);
            seen.Add(SeenOf(l, ++k, viewer, rec, now, look.React && reactsLeft, look.Comment && commentsLeft, own: false));
        }
        // Their own newest line, now and then: who reacted since they last looked (LineCheckOwnLikes), comments to answer (LineReplyChance).
        var mine = book.Feed(40, a => a == viewer).FirstOrDefault();
        if (mine != null)
        {
            bool looksAtLikes = LinePlan.LooksAtReactions(rng, _cfg.LineCheckOwnLikes.Value, LinePlan.Care(rec.Persona.Personality.Append(rec.Persona.Temperament)));
            bool answers = commentsLeft && mine.Comments.Any(c => c.Who != viewer) && rng.Next(100) < _cfg.LineReplyChance.Value;
            if (looksAtLikes || answers)
            {
                var s = SeenOf(mine, ++k, viewer, rec, now, false, answers, own: true);
                if (looksAtLikes) { s.NewReactions = book.NewSince(mine.Id, out int count).Select(r => (r.Name, r.Up)).ToList(); s.LookedUpTo = count; }
                if (s.NewReactions.Count > 0 || answers) seen.Add(s);
            }
        }
        // FB-80: the newest photo of yours they're shown, as the picture itself (LinePhotosSeen; never one with someone under 18
        // in view, or one the Camera didn't take): one a look, so a photo costs one picture per resident who sees it.
        if (_cfg.LinePhotosSeen.Value)
            foreach (var s in seen.Where(s => s.YourPhoto && !s.Own))
            {
                var line = book.Get(s.LineId);
                var p = line != null && line.Photo.Length > 0 ? book.PhotoOf(line.Photo) : null;
                if (p == null || !p.Mine || p.NoAi || line!.Author != LineBook.You) continue;
                try
                {
                    string path = Path.Combine(PhotosDir, p.File);
                    if (File.Exists(path) && new FileInfo(path).Length <= 2 * 1024 * 1024) { s.Image = File.ReadAllBytes(path); break; }
                }
                catch { }
            }
        return seen;
    }

    private static Lines.Seen SeenOf(LineBook.Line l, int k, string viewer, NpcRecord rec, long now, bool mayReact, bool mayComment, bool own)
    {
        var photo = l.Photo.Length > 0 ? _lines!.PhotoOf(l.Photo) : null;
        string photoShows = photo?.Shows ?? "";
        var s = new Lines.Seen
        {
            Key = "L" + k, LineId = l.Id, Author = l.Author, AuthorName = l.Author == LineBook.You ? YourLineName(rec) : l.Name,
            Handle = l.Handle == LineBook.You ? YourHandle : l.Handle, Text = l.Text, Place = l.Place, Ago = AgoWords(now, l.Minute), PhotoShows = photoShows,
            Own = own, MayReact = mayReact, MayComment = mayComment,
            Had = l.Reactions.FirstOrDefault(r => r.Who == viewer) is { } had ? had.Up : null,
            Feeling = own ? "it's your own line" : FeelingWords(FeelingAbout(viewer, rec, l.Author)),
            YourPhoto = photo is { Mine: true }, // FB-80
        };
        // FB-72: your line from the last two game hours, read by someone who knows you and could come now.
        try
        {
            s.MayCome = !own && l.Author == LineBook.You && l.Minute >= 0 && now - l.Minute <= 120 && rec.State.TimesMet > 0
                        && CompanyRoom(viewer) && CantComeNow(viewer).Length == 0;
        }
        catch { s.MayCome = false; }
        int c = 0;
        foreach (var cm in l.Comments.TakeLast(Lines.CommentsShown))
        {
            string key = $"{s.Key}.c{++c}";
            s.Comments.Add((key, cm.Who == LineBook.You ? YourLineName(rec) : cm.Name, cm.Who == LineBook.You ? YourHandle : _lines!.HandleOf(cm.Who, cm.Name), cm.Text));
            s.CommentIds[key] = cm.Id;
            s.CommentWho[key] = cm.Who;
        }
        return s;
    }

    /// <summary>How they feel about the poster, in words; below LineDislikeBelow, a dislike would come naturally.</summary>
    private static string FeelingWords(int feeling)
    {
        string w = LinePlan.Feeling(feeling);
        return feeling >= 0 && feeling < Math.Clamp(_cfg.LineDislikeBelow.Value, 0, 100) ? w + "; a dislike from you would be natural" : w;
    }

    /// <summary>You, as a resident sees you on Nightline: your name if they know it, else your handle.</summary>
    private static string YourLineName(NpcRecord rec) => rec.State.PlayerName.Length > 0 ? rec.State.PlayerName : "@" + YourHandle;
    private static string YourHandle => LineBook.Handle(_cfg.LineYourHandle.Value) is { Length: > 0 } h ? h : LineBook.You;

    /// <summary>How the viewer feels about the poster, 0-100 (-1 they've never met).</summary>
    private static int FeelingAbout(string viewer, NpcRecord rec, string poster)
    {
        if (poster == LineBook.You) return rec.State.TimesMet > 0 ? (int)Math.Round(rec.State.Relationship) : -1;
        try { var pair = _pairs?.Get(viewer, poster); if (pair != null && pair.Times > 0) return (int)Math.Round(pair.Relationship); } catch { }
        try { if (_couples?.Together(viewer, poster) == true) return 90; } catch { }
        return -1;
    }

    private static string AgoWords(long now, long then)
    {
        long m = Math.Max(0, now - then);
        return m < 2 ? "just now" : m < 60 ? $"{m} minutes ago" : m < 1440 ? $"{m / 60} hours ago" : $"{m / 1440} days ago";
    }

    /// <summary>The visit's answer, on the main thread: the handle, the post (and its photo), reactions, comments, memory, your news.</summary>
    private static void ApplyVisit(string id, string name, Lines.Result r, List<Lines.Seen> seen, GameSnapshot snap, long minute, string place, int generation)
    {
        if (generation != _resetGeneration || _lines == null || KnownUnder18(id)) return;
        var book = _lines;
        if (r.Handle.Length > 0 && !book.HasHandle(id)) book.SetHandle(id, name, r.Handle);
        if (r.Acts.Any(a => a.Coming)) QueueVisitor(id, name, "nightline"); // FB-72: on their way to keep you company
        string posted = "";
        if (r.Post.Length > 0 && _postsToday < Math.Max(0, _cfg.LinePostsPerDay.Value))
        {
            bool photo = r.PhotoArea.Length > 0 && ImageSettingsNow().Ready && _photosToday < Math.Max(0, _cfg.LinePhotosPerDay.Value)
                         && !PointsAtAChild(r.PhotoShows + " " + r.Post); // review NL-7
            posted = book.Post(id, name, r.Post, place, minute, snap.GameDay, snap.Clock, photoComing: photo);
            if (posted.Length > 0)
            {
                _postsToday++;
                Log.Info($"Nightline: {name} posted a line{(photo ? " (a photo on its way)" : "")}.");
                if (photo) { _photosToday++; MakePhoto(posted, id, r.PhotoShows, r.PhotoArea, r.PhotoOfMe, place, generation); }
            }
        }
        var news = new List<string>();
        foreach (var a in r.Acts)
        {
            var s = seen.First(x => x.Key == a.Key);
            if (a.React is bool up && _reactsToday < Math.Max(0, _cfg.LineReactionsPerDay.Value) && book.React(s.LineId, id, name, up, minute, a.Why))
            {
                _reactsToday++;
                if (s.Author == LineBook.You) news.Add($"{name} {(up ? "liked" : "disliked")} your line");
            }
            if (a.Comment.Length > 0 && _commentsToday < Math.Max(0, _cfg.LineCommentsPerDay.Value))
            {
                // The comment it answers, by the id taken when they were shown it (review NL-1); gone since: a plain comment.
                string replyTo = a.ReplyTo.Length > 0 && s.CommentIds.TryGetValue(a.ReplyTo, out var cid) ? cid : "";
                bool toYou = replyTo.Length > 0 && s.CommentWho.TryGetValue(a.ReplyTo, out var who) && who == LineBook.You;
                if (book.AddComment(s.LineId, id, name, a.Comment, minute, replyTo).Length > 0)
                {
                    _commentsToday++;
                    if (s.Author == LineBook.You || toYou) news.Add($"{name} {(toYou ? "answered you" : "commented")}");
                }
            }
        }
        foreach (var s in seen)
        {
            book.MarkSeen(s.LineId, id);
            if (s.Own && s.LookedUpTo > 0) book.MarkLooked(s.LineId, s.LookedUpTo); // review NL-9: only now the visit worked
        }
        if (_cfg.LineRemember.Value)
        {
            var photoShows = posted.Length > 0 ? r.PhotoShows : "";
            var (lines, summary) = Lines.Remembered(r, seen, photoShows);
            if (lines.Count > 0) _ = ActiveBrain.RememberLine(id, lines, summary, snap);
        }
        if (news.Count > 0 && _cfg.LineToasts.Value && !_phoneOpen) PhoneUi.Toast("Nightline", string.Join(" · ", news.Take(2)), "known", system: true);
        if (_phoneOpen) RefreshPhone();
    }

    // ------------------------------------------------------------------ photos

    private static string FacePath(string id) => Path.Combine(FacesDir, SafeId(id) + ".jpg");
    private static string SafeId(string id) => new string(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    /// <summary>A post's photo: their picture first when it's of them, then a few street views of the area; made off the main thread.</summary>
    private static void MakePhoto(string lineId, string author, string shows, string area, bool ofThem, string place, int generation)
    {
        var refs = new List<byte[]>();
        var rng = LineRng;
        string face = FacePath(author);
        bool withFace = ofThem && _cfg.LineSelfInPhotos.Value && File.Exists(face);
        var shots = Shots.Pick(area, place, withFace ? 2 : 3, new Random(rng.Next()));
        string look = "";
        try { look = ActiveBrain.Store.Glance(author)?.Persona.Appearance ?? ""; } catch { }
        string prompt = Lines.ImagePrompt(shows, area, withFace, ReplyParser.Quotable(look, 300));
        var client = Images;
        Task.Run(async () =>
        {
            string? file = null;
            try
            {
                if (withFace) refs.Add(await File.ReadAllBytesAsync(face).ConfigureAwait(false));
                foreach (var s in shots) { string p = Path.Combine(ShotsDir, s); if (File.Exists(p)) refs.Add(await File.ReadAllBytesAsync(p).ConfigureAwait(false)); }
                if (KnownUnder18(author)) return;
                var (image, error) = await client.MakeAsync(prompt, refs, CancellationToken.None).ConfigureAwait(false);
                if (image == null) { Log.Info("Nightline: a photo couldn't be made (" + error + "); the line goes up without it."); return; }
                Directory.CreateDirectory(PhotosDir);
                string name = $"photo-{SafeId(lineId)}{(ImageClient.IsPng(image) ? ".png" : ".jpg")}";
                await File.WriteAllBytesAsync(Path.Combine(PhotosDir, name), image).ConfigureAwait(false);
                file = name;
            }
            catch (Exception e) { Log.Debug("nightline photo: " + e.Message); }
            finally
            {
                string? kept = file;
                MainThread.Enqueue(() =>
                {
                    bool adopted = generation == _resetGeneration && _lines != null && _lines.PhotoDone(lineId, kept, shows);
                    if (!adopted && kept != null) DeletePhotoFile(kept); // review NL-4: never left behind
                    if (_lines == null) return;
                    foreach (var gone in _lines.TakeTrimmedPhotos()) DeletePhotoFile(gone);
                    if (_phoneOpen) RefreshPhone();
                });
            }
        });
    }

    /// <summary>
    /// A photo's words that name someone the mod knows is under 18, or point at a child at all ("my kid", "a little girl"):
    /// no photo is made (the line goes up without one). The image service never gets asked for one.
    /// </summary>
    private static bool PointsAtAChild(string text)
    {
        try
        {
            var names = ActiveBrain.KnownMinors.All.Take(200).Select(id => ActiveBrain.Store.Glance(id)?.Persona.Name ?? "").ToList();
            return Lines.PointsAtAChild(text, names);
        }
        catch { return true; } // can't tell: no photo
    }

    /// <summary>
    /// Photos no line keeps: the newest 400 files stay (other saves' lines point at theirs; the folder is shared), along
    /// with every one this save uses; street views the index doesn't list go (review NL-4). On a pool thread, at open.
    /// </summary>
    private static void PruneNightlineFiles()
    {
        var keep = _lines?.PhotoFiles() ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shotsKept = Shots.Files();
        string photos = PhotosDir, shots = ShotsDir;
        Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(photos))
                    foreach (var f in new DirectoryInfo(photos).GetFiles("photo-*").OrderByDescending(f => f.LastWriteTimeUtc).Skip(400))
                        if (!keep.Contains(f.Name)) try { f.Delete(); } catch { }
                if (Directory.Exists(shots))
                    foreach (var f in new DirectoryInfo(shots).GetFiles("shot-*.jpg"))
                        if (!shotsKept.Contains(f.Name) && DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromMinutes(5)) try { f.Delete(); } catch { }
            }
            catch (Exception e) { Log.Debug("nightline files: " + e.Message); }
        });
    }

    private static void DeletePhotoFile(string file)
    {
        if (!LineBook.SafeFile(file)) return;
        try { File.Delete(Path.Combine(PhotosDir, file)); } catch { }
    }

    /// <summary>
    /// A street view (CityShots, every ShotEveryHours game hours): only in the world, outside, with no window of ours
    /// covering anything that matters (the render is the camera's own, so no UI is in it anyway), never on a loading screen.
    /// </summary>
    private static void MaybeCityShot()
    {
        if (!_cfg.CityShots.Value || !InGameplay() || ChatOpen || _menuOpen || _consoleOpen || _inApartment > 0) return;
        long minute = GameNow();
        if (minute < 0) return;
        long every = Math.Clamp(_cfg.ShotEveryHours.Value, 1, 24) * 60L;
        if (_nextShotAt < 0) { _nextShotAt = minute + LineRng.Next(10, (int)every); return; }
        if (minute < _nextShotAt) return;
        string area = _here.place;
        if (area.Length == 0) return;
        var cam = GameContext.PlayerCamera();
        if (cam == null) return;
        _nextShotAt = minute + every;
        var (jpeg, error) = Game.Snapshot.TakeCity(cam);
        if (jpeg == null) { Log.Debug("city shot: " + error); return; }
        int day = GameContext.Day();
        int perArea = _cfg.ShotsPerArea.Value, mb = _cfg.ShotsDiskMB.Value;
        string dir = ShotsDir, file = $"shot-{DateTime.UtcNow:yyyyMMddHHmmss}-{LineRng.Next(1000, 9999)}.jpg";
        var index = Shots;
        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, file), jpeg);
                foreach (var gone in index.Add(file, area, "", day, jpeg.Length, perArea, mb))
                    if (ShotIndex.SafeFile(gone)) try { File.Delete(Path.Combine(dir, gone)); } catch { }
            }
            catch (Exception e) { Log.Debug("city shot: " + e.Message); }
        });
    }

    /// <summary>
    /// A resident's own picture for their photos (LineSelfInPhotos), taken once, the first time they're close and the mod
    /// knows them; never anyone under 18. Only when photos are set up (nothing is kept otherwise).
    /// </summary>
    private static void MaybeFace()
    {
        // FB-78: a face is kept for everyone's round picture too, so it needs no image service (it's taken here, on this PC).
        if (!InGameplay() || ChatOpen || _menuOpen) return;
        var pc = GameContext.PlayerCharacter();
        var cam = GameContext.PlayerCamera();
        if (pc == null || cam == null) return;
        Vector3 me = pc.transform.position;
        var brain = ActiveBrain;
        foreach (var (c, _, pos) in NearBodies(pc).Where(b => Flat(b.pos - me).magnitude <= 5f).OrderBy(b => Flat(b.pos - me).sqrMagnitude).Take(3))
        {
            try
            {
                string id = GameContext.NpcId(c);
                if (id.Length == 0 || id.StartsWith("extra_", StringComparison.Ordinal) || !FacesTried.Add(id) || KnownUnder18(id)) continue;
                if (File.Exists(FacePath(id)) || MinorInfo(c, id).minor || !brain.Store.Has(id)) continue;
                var rec = brain.Store.Glance(id);
                if (rec == null || brain.IsKnownMinor(rec)) continue;
                var (jpeg, _) = Game.Snapshot.Take(c, cam);
                if (jpeg == null) continue;
                string path = FacePath(id), dir = FacesDir;
                Task.Run(() =>
                {
                    try { Directory.CreateDirectory(dir); File.WriteAllBytes(path, jpeg); } catch { }
                    MainThread.Enqueue(() => FaceLoading.Remove(id)); // FB-78: their round picture is read again, with the face
                });
                return; // one a look
            }
            catch { }
        }
    }

    /// <summary>Someone forgotten (under 18, /resetnpc): their lines, photos and picture go.</summary>
    private static void ForgetOnNightline(string id)
    {
        try
        {
            if (_lines != null) foreach (var f in _lines.Forget(id)) DeletePhotoFile(f);
            string face = FacePath(id);
            if (File.Exists(face)) File.Delete(face);
            DropFace(id); // FB-78
            FaceLoading.Remove(id);
        }
        catch (Exception e) { Log.Debug("nightline forget: " + e.Message); }
    }

    private static void ClearNightline()
    {
        try { if (_lines != null) foreach (var f in _lines.Clear()) DeletePhotoFile(f); } catch { }
    }

    // ------------------------------------------------------------------ you, on Nightline

    private static void LinePost(string text)
    {
        if (!NightlineOn) return;
        long minute = GameNow();
        bool waiting = _bars <= 0;
        // FB-80: a photo from your gallery goes with it: a copy (1024 wide at most) in the photos folder.
        string attach = _lineAttach;
        byte[]? photo = null;
        if (attach.Length > 0)
        {
            try { if (File.Exists(attach)) photo = Game.CameraView.Shrink(File.ReadAllBytes(attach), 1024); } catch (Exception e) { Log.Debug("photo to post: " + e.Message); }
            if (photo == null) { Log.Info("Nightline: that photo couldn't be read; the line goes up without it."); }
        }
        if (text.Trim().Length == 0 && photo == null) { RefreshPhone(); return; }
        string id = _lines!.Post(LineBook.You, "You", text, _here.place, Math.Max(0, minute), Math.Max(0, GameContext.Day()), TwelveHour($"{Math.Max(0, GameContext.Hour()):00}:{Math.Max(0, GameContext.Minute()):00}"), waiting: waiting, photoComing: photo != null);
        if (id.Length > 0 && photo != null)
        {
            string name = $"photo-you-{SafeId(id)}.jpg", path = Path.Combine(PhotosDir, name);
            bool noAi = true;
            try { var f = new FileInfo(attach); noAi = !(_gallery?.MayShowAi(f.Name, f.Length, f.LastWriteTimeUtc) ?? false); } catch { }
            try { Directory.CreateDirectory(PhotosDir); DataFile.WriteAtomic(path, photo, durable: false); _lines.MinePhoto(id, name, noAi); }
            catch (Exception e) { Log.Warn("Nightline: your photo couldn't be kept: " + e.Message); _lines.PhotoDone(id, null, ""); }
        }
        if (id.Length > 0)
        {
            _lineAttach = "";
            Log.Info((waiting ? "Nightline: your line waits for a bar of signal" : "Nightline: your line is up") + (photo != null ? ", with your photo." : "."));
        }
        RefreshPhone();
    }

    private static void LineReact(string lineId, bool up)
    {
        if (!NightlineOn) return;
        var line = _lines!.Get(lineId);
        if (line == null || line.Author == LineBook.You) return;
        bool? now = line.Reactions.FirstOrDefault(r => r.Who == LineBook.You) is { } had && had.Up == up ? null : up; // the lit one again: taken back
        _lines.React(lineId, LineBook.You, "You", now, Math.Max(0, GameNow()));
        RefreshPhone();
    }

    private static void LineComment(string lineId, string text, string replyTo)
    {
        if (!NightlineOn) return;
        _lines!.AddComment(lineId, LineBook.You, "You", text, Math.Max(0, GameNow()), replyTo);
        RefreshPhone();
    }

    private static void LineDelete(string lineId)
    {
        if (!NightlineOn) return;
        var book = _lines!;
        var (line, index) = book.TakeDown(lineId, LineBook.You);
        if (line == null) return;
        if (PhoneUi.Subject == lineId && PhoneUi.ScreenName == "line") PhoneUi.Back();
        SetUndo("Line deleted. (Anyone who saw it remembers.)", "nightline", undo: () => book.PutBack(line, index), commit: () => DeletePhotoFile(book.DropPhoto(line)));
        RefreshPhone();
    }

    private static void LineDeleteComment(string lineId, string commentId)
    {
        if (!NightlineOn) return;
        var book = _lines!;
        var (c, index) = book.TakeDownComment(lineId, commentId, LineBook.You);
        if (c == null) return;
        SetUndo("Comment deleted.", "line", undo: () => book.PutBackComment(lineId, c, index), commit: () => { });
        RefreshPhone();
    }

    private static void LineCancel(string lineId)
    {
        if (!NightlineOn) return;
        _lines!.CancelWaiting(lineId);
        RefreshPhone();
    }

    /// <summary>A bar came back: your waiting lines go up.</summary>
    private static void SendWaitingLines()
    {
        if (_lines != null && _lines.SendWaiting() > 0 && _phoneOpen) RefreshPhone();
    }

    // ------------------------------------------------------------------ /nightline

    private static void NightlineCommand(string arg, Action<string> say)
    {
        if (!Features.Phone || !Features.Nightline) { say("Nightline is off (Features > Nightline, and the phone)."); return; }
        if (_lines == null) { say("Nightline opens with a save."); return; }
        if (arg.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            say(StartVisit(out string why) ? "Someone is opening Nightline now." : "Nobody opened it: " + why + ".");
            return;
        }
        if (arg.Equals("shots clear", StringComparison.OrdinalIgnoreCase))
        {
            int n = 0;
            foreach (var f in Shots.Clear()) if (ShotIndex.SafeFile(f)) { try { File.Delete(Path.Combine(ShotsDir, f)); n++; } catch { } }
            say($"{n} street views deleted (CityShots takes new ones as you play).");
            return;
        }
        long now = GameNow();
        say($"Nightline: {_lines.Count} lines kept; someone opens it {(_lines.NextUse < 0 ? "soon" : _lines.NextUse <= now ? "any moment" : $"in about {_lines.NextUse - now} game minutes")}.");
        say($"Today: {_postsToday} posts (of {_cfg.LinePostsPerDay.Value}), {_reactsToday} reactions, {_commentsToday} comments, {_photosToday} photos (of {_cfg.LinePhotosPerDay.Value}).");
        var areas = Shots.Areas();
        say($"Street views: {Shots.Total}{(areas.Count > 0 ? " (" + string.Join(", ", areas.Take(6).Select(a => $"{a} {Shots.Count(a)}")) + ")" : "")}. Photos: {(ImageSettingsNow().Ready ? "set up (" + Redact.Value(_cfg.ImageModel.Value ?? "") + ")" : "not set up ([Nightline Photos] ImageApiKey, ImageModel)")}.");
        say("/nightline now: someone opens it now. /nightline shots clear: forget the street views.");
    }

    // ------------------------------------------------------------------ the view

    private static void FillNightline(PhoneView v)
    {
        v.NightlineOn = NightlineOn;
        if (!NightlineOn) { v.Lines.Clear(); v.LineOpen = null; v.LineComments.Clear(); v.LineUnseen = 0; return; }
        var book = _lines!;
        long now = GameNow();
        int hour = GameContext.Hour();
        v.LineDim = hour >= 20 || hour is >= 0 and < 6 ? Math.Clamp(_cfg.LineNightDim.Value, 0, 30) / 100f : 0f;
        v.YourName = (_cfg.LineYourName.Value ?? "").Trim() is { Length: > 0 } yn ? LineBook.CleanText(yn, 40) : "You";
        v.YourHandle = YourHandle;
        if (v.LineTab == "you" || PhoneUi.ScreenName == "line" && (book.Get(PhoneUi.Subject)?.Author == LineBook.You)) book.SeenYours();
        v.LineUnseen = book.Unseen;
        // Phone pass PH-A14: the feed's cards (photos, portraits, names) are built only for Nightline's own screens; every
        // other redraw (a text landing, the bars) keeps just the count for the icon.
        if (PhoneUi.ScreenName is not ("nightline" or "line")) { v.Lines.Clear(); v.LineOpen = null; v.LineComments.Clear(); return; } // review PH-R4
        Func<string, bool>? keep = v.LineTab switch
        {
            "you" => a => a == LineBook.You,
            "people" => a => a == LineBook.You || (_phone?.Has(a) ?? false) || (ContactInfo.TryGetValue(a, out var k) && k.TimesMet > 0),
            _ => null,
        };
        v.Lines = book.Feed(60, keep).Select(l => Card(l, now)).ToList();
        v.LineNews = v.LineUnseen > 0 && v.LineTab != "you" ? NewsLine(book) : "";
        if (PhoneUi.ScreenName == "line" && book.Get(PhoneUi.Subject) is { } open)
        {
            v.LineOpen = Card(open, now);
            var reacted = open.Reactions.ToDictionary(r => r.Who, r => r.Up);
            v.LineComments = open.Comments.Select(c => new PhoneView.LineComment
            {
                Id = c.Id, Name = c.Who == LineBook.You ? v.YourName : c.Name, Handle = c.Who == LineBook.You ? v.YourHandle : book.HandleOf(c.Who, c.Name),
                Text = c.Text, Ago = AgoShort(now, c.Minute), Kind = KindOf(c.Who), Portrait = PortraitFor(c.Who), Yours = c.Who == LineBook.You,
                Reply = c.ReplyTo.Length > 0, Up = reacted.TryGetValue(c.Who, out var u) ? u : null,
            }).ToList();
        }
        else { v.LineOpen = null; v.LineComments.Clear(); }
    }

    private static string NewsLine(LineBook book)
    {
        var mine = book.Feed(20, a => a == LineBook.You);
        var bits = new List<string>();
        foreach (var l in mine)
        {
            foreach (var r in l.Reactions.Where(r => r.Who != LineBook.You).OrderByDescending(r => r.Minute).Take(2)) bits.Add($"{r.Name} {(r.Up ? "liked" : "disliked")} your line");
            foreach (var c in l.Comments.Where(c => c.Who != LineBook.You).TakeLast(1)) bits.Add($"{c.Name} commented");
            if (bits.Count >= 2) break;
        }
        return string.Join(" · ", bits.Take(2));
    }

    private static PhoneView.LineCard Card(LineBook.Line l, long now)
    {
        var v = PhoneUi.View;
        bool yours = l.Author == LineBook.You;
        var card = new PhoneView.LineCard
        {
            Id = l.Id, Name = yours ? v.YourName : l.Name, Handle = yours ? v.YourHandle : l.Handle, Place = l.Place, Text = l.Text,
            Ago = l.Waiting ? "waiting" : Lines.PostedAt(now, l.Minute), Kind = KindOf(l.Author), Portrait = PortraitFor(l.Author), // FB-78: when it was posted
            Developing = l.Developing, Up = l.Reactions.Count(r => r.Up), Down = l.Reactions.Count(r => !r.Up), Comments = l.Comments.Count,
            Mine = l.Reactions.FirstOrDefault(r => r.Who == LineBook.You) is { } m ? m.Up : null, Yours = yours, Waiting = l.Waiting,
            SeenCount = l.SeenBy.Count(s => s != LineBook.You),
        };
        if (l.Photo.Length > 0 && _lines!.PhotoOf(l.Photo) is { } p)
        {
            string path = Path.Combine(PhotosDir, p.File);
            if (File.Exists(path))
            {
                card.Photo = path;
                // FB-80: a photo you took is yours (no "made photo" note); one the image service made says so.
                if (!p.Mine) card.PhotoNote = $"Made from {(yours ? "your" : FirstName(l.Name) + "'s")} words and the city's own shots{(l.Place.Length > 0 ? " of " + l.Place : "")}";
            }
        }
        PhoneView.Who W(string who, string name) => new() { Name = who == LineBook.You ? v.YourName : name, Kind = KindOf(who), Portrait = PortraitFor(who) };
        foreach (var r in l.Reactions.OrderByDescending(r => r.Who == LineBook.You).ThenByDescending(r => r.Minute))
            (r.Up ? card.Likers : card.Dislikers).Add(W(r.Who, r.Name));
        if (yours) foreach (var s in l.SeenBy.Where(s => s != LineBook.You).Reverse().Take(3)) card.Seen.Add(W(s, NameOf(s)));
        return card;
    }

    private static string FirstName(string n) => n.Split(' ', 2)[0];

    private static string NameOf(string id)
    {
        if (ContactInfo.TryGetValue(id, out var k) && k.Their.Length >= 0) { }
        try { return ActiveBrain.Store.Glance(id)?.Persona.Name ?? "Someone"; } catch { return "Someone"; }
    }

    private static string AgoShort(long now, long then)
    {
        long m = Math.Max(0, now - then);
        return m < 2 ? "now" : m < 60 ? $"{m}m" : m < 1440 ? $"{m / 60}h" : $"{m / 1440}d";
    }

    /// <summary>Their ring on Nightline: your partner, a friend, someone you've met, or a stranger; you.</summary>
    private static string KindOf(string id)
    {
        if (id == LineBook.You) return "you";
        if (ContactInfo.TryGetValue(id, out var k)) return k.Kind is "partner" or "friend" ? k.Kind : k.TimesMet > 0 ? "known" : "stranger";
        return "stranger";
    }

    private static Sprite? PortraitFor(string id)
    {
        if (id == LineBook.You || KnownUnder18(id)) return null;
        // FB-78 (the author: "theres no portraits of the npc's profiles"): anyone's portrait, not only a contact's: the
        // game's own (by their model), else the face Nightline keeps of them.
        try
        {
            string model = ContactInfo.TryGetValue(id, out var k) && k.Model.Length > 0 ? k.Model : LineModel(id);
            return PortraitOf(id, model) ?? FaceSprite(id);
        }
        catch { return null; }
    }

    private static readonly Dictionary<string, string> LineModels = new(StringComparer.Ordinal);

    private static string LineModel(string id)
    {
        if (LineModels.TryGetValue(id, out var m)) return m;
        if (LineModels.Count > 600) LineModels.Clear();
        try { m = ActiveBrain.Store.Glance(id)?.Visual?.Model ?? ""; } catch { m = ""; }
        return LineModels[id] = m;
    }

    // FB-78: the faces kept of people (nightline-faces), as their round picture: read off the main thread, one a tick, cut to
    // a small square of the head; the least lately shown go first past the cap (review PH-5: never all at once).
    private static readonly Dictionary<string, Sprite> FaceSprites = new(StringComparer.Ordinal);
    private static readonly List<string> FaceOrder = new();
    private static readonly HashSet<string> FaceLoading = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(string id, byte[] bytes)> FaceRead = new();
    private const int FacesCached = 120, FaceSide = 160;
    private static float _faceRefreshAt = -1f;

    private static Sprite? FaceSprite(string id)
    {
        if (FaceSprites.TryGetValue(id, out var s))
        {
            try { if (s != null && !s.WasCollected) { FaceOrder.Remove(id); FaceOrder.Add(id); return s; } } catch { }
            FaceSprites.Remove(id); FaceOrder.Remove(id);
        }
        string path = FacePath(id);
        if (!FaceLoading.Add(id)) return null;
        Task.Run(() =>
        {
            byte[]? b = null;
            try { var f = new FileInfo(path); if (f.Exists && f.Length <= 4 * 1024 * 1024) b = File.ReadAllBytes(path); } catch { }
            FaceRead.Enqueue((id, b ?? Array.Empty<byte>()));
        });
        return null;
    }

    /// <summary>One face read in, as a sprite: its top middle (the head; a picture is taken from about the knees up).</summary>
    private static void TickFaceSprites()
    {
        if (_faceRefreshAt > 0 && Time.unscaledTime >= _faceRefreshAt)
        {
            _faceRefreshAt = -1f;
            if (_phoneOpen && PhoneUi.ScreenName is "nightline" or "line") RefreshPhone(); // at most once a second (review PH-5)
        }
        if (!FaceRead.TryDequeue(out var item)) return;
        // No face kept: stays in FaceLoading (not read again). Forgotten, or another save, meanwhile: dropped (review PH-5).
        if (item.bytes.Length == 0 || !FaceLoading.Contains(item.id) || KnownUnder18(item.id)) return;
        Texture2D? tex = null, small = null;
        try
        {
            tex = new Texture2D(2, 2, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
            if (!ImageConversion.LoadImage(tex, item.bytes, false)) return;
            if (tex.width < 16 || tex.height < 16) { FaceLoading.Remove(item.id); return; } // review FACE-R1: nothing to cut from
            int side = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(tex.width, tex.height) * 0.5f), 8, Mathf.Min(tex.width, tex.height));
            int x = (tex.width - side) / 2, y = Mathf.Max(0, tex.height - side - Mathf.RoundToInt(tex.height * 0.05f));
            int to = Mathf.Min(FaceSide, side);
            var src = tex.GetPixels(x, y, side, side);
            var px = new Color[to * to];
            for (int r = 0; r < to; r++)
                for (int c = 0; c < to; c++) px[r * to + c] = src[(r * side / to) * side + c * side / to];
            small = new Texture2D(to, to, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
            small.SetPixels(px);
            small.Apply(false, true);
            var sprite = Sprite.Create(small, new Rect(0, 0, to, to), new Vector2(0.5f, 0.5f), 100f);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            small = null; // the sprite owns it now
            while (FaceOrder.Count >= FacesCached) DropFace(FaceOrder[0]);
            FaceSprites[item.id] = sprite;
            FaceOrder.Add(item.id);
            FaceLoading.Remove(item.id);
            if (_faceRefreshAt < 0) _faceRefreshAt = Time.unscaledTime + 1f;
        }
        catch { }
        finally
        {
            if (tex != null) UnityEngine.Object.Destroy(tex);
            if (small != null) UnityEngine.Object.Destroy(small);
        }
    }

    private static void DropFace(string id)
    {
        FaceOrder.Remove(id);
        if (FaceSprites.Remove(id, out var s))
            try { if (s != null && !s.WasCollected) { var t = s.texture; UnityEngine.Object.Destroy(s); if (t != null) UnityEngine.Object.Destroy(t); } } catch { }
    }

    private static void FreeFaceSprites()
    {
        foreach (var id in FaceSprites.Keys.ToList()) DropFace(id);
        FaceOrder.Clear();
        FaceLoading.Clear();
    }
}
