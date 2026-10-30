using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;

namespace NivalisAIChat.Npc;

public sealed class BrainSettings
{
    /// <summary>Older memories come back less clearly and less often, by in-game days since (see Memories.Clarity).</summary>
    public bool RecallFades = true;
    /// <summary>What the player says automatically when a conversation starts ("Hey,"); empty = the player types first.</summary>
    public string OpeningLine = "Hey,";
    /// <summary>
    /// Longest a conversation waits for one answer, retries included (seconds; 0 = no limit). Optional extras (noting
    /// what they told you, a better parting line) get less, and are skipped when slow rather than failing the reply.
    /// </summary>
    public int AnswerLimitSeconds;
    public bool LifeUpdates = true;
    public int LifeUpdateAfterGameDays = 2;
    public double LifeUpdateAfterRealHours = 24;
    public int RecentMessages = 40;
    public int SummarizeAfter = 60;
    public int MaxReplyTokens = 4000;
    public int MaxGenerationTokens = 8000;
    public bool KeepGameNames;
    public bool StructuredReplies = true;
    public bool NpcsCanLeave = true;
    public double PatienceScale = 1.0;
    public PopulationSettings Population = new();
    /// <summary>Lines kept per NPC (0 = everything, forever).</summary>
    public int MaxStoredLines = 0;
    public bool MeetingSummaries = true;
    public bool Gossip = true;
    /// <summary>0..1: how open the city is (0.5 = an even mix of open and guarded moods).</summary>
    public double Friendliness = 0.5;
    public bool Favours = true;
    /// <summary>The relationship bar's amounts per reply, in points on the 0–100% bar (see <see cref="Relationships.Step"/>).</summary>
    public double RelationshipGainMax = 0.5, RelationshipLossMin = 0.1, RelationshipLossMax = 0.5;
    /// <summary>Food handed over: a free gift, and food they asked for (points on the bar).</summary>
    public double FoodGiftBonus = 1.5, FoodFavourBonus = 1.0;
    /// <summary>Where on the bar people start doing things (friendship here; greetings and the discount elsewhere).</summary>
    public Milestones Milestones = new();
    /// <summary>The inner-life round (VaryLines): greetings are asked for three ways to say it; the one least like their recent lines is used.</summary>
    public bool VaryLines = true;
}

public sealed class ChatSession
{
    public NpcRecord Record = null!;
    public GameSnapshot Snapshot = null!;
    public bool FirstMeeting;
    public bool Ended;
    /// <summary>Lines added during this meeting (for the UI to show).</summary>
    public int StartIndex;
    /// <summary>How their mood moved during this meeting, oldest first.</summary>
    public List<string> MoodTrail = new();
    /// <summary>Player lines so far, exchanges they can spare, and how fed up they are.</summary>
    public int Exchanges;
    public int PatienceBudget = 6;
    public double Annoyance;
    public double AnnoyanceThreshold = 7;
    /// <summary>0 = talking; 1 = they've reacted to your silence; 2 = they gave up and left.</summary>
    public int SilentStage;
    /// <summary>They held off leaving to hear your answer to a question they asked; after it, they go.</summary>
    public bool LastChance;
    /// <summary>The automatic opening line ("Hey,") the player said for this meeting, if any (it doesn't count as them talking).</summary>
    public ChatLine? OpenerLine;
    /// <summary>Things that went wrong on the way in but didn't stop the meeting (a picture the model couldn't look at): shown in the chat.</summary>
    public List<string> Notices = new();
    /// <summary>Roleplay: what they did in their last few lines (so the next is something else), this meeting only.</summary>
    public List<string> Actions = new();

    // ---- ownership of the record (see NpcBrain: the live-session registry)
    /// <summary>The brain that began this meeting: its saves, its end and its release go through it.</summary>
    public NpcBrain? Owner { get; internal set; }
    /// <summary>Their epoch when the meeting began: a reset since then means nothing of it is saved.</summary>
    public long Epoch { get; internal set; }
    /// <summary>No longer the owner of their record: never saves again.</summary>
    public bool Released { get; internal set; }
    internal readonly object Gate = new();
    internal readonly TaskCompletionSource<bool> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Changes from outside the meeting that its record took in at a save that failed: kept until a save works (under Gate).</summary>
    internal readonly List<Func<NpcRecord, bool>> Unsaved = new();
}

/// <summary>How a meeting lets go of the record: see <see cref="NpcBrain.Release"/>.</summary>
public enum ReleaseMode
{
    /// <summary>The normal close: changes waiting for them are added to the meeting's record, and it is saved.</summary>
    End,
    /// <summary>Never handed over (Esc while they answer, an error): only the changes waiting for them are saved, onto their file.</summary>
    Abort,
    /// <summary>A reset: nothing is saved, and nothing waiting for them is kept.</summary>
    Discard,
}

public sealed class NpcReply
{
    public bool Ok = true;
    public string Spoken = "";
    public bool Leave;
    public string? Error;
    public bool NameJustRevealed;
    /// <summary>A gesture the AI cued with the line (GestureLibrary), or "" (CF-04).</summary>
    public string Gesture = "";
    /// <summary>FB-62..65: what the reply asked to happen (follow, romance, a date, moving in, a reminder).</summary>
    public ReplyExtras Extras = new();
    /// <summary>Roleplay: what they visibly do as they say it ("She rolls her eyes."), cleaned; "" for none.</summary>
    public string Action = "";
    /// <summary>The phone: they gave you their number in this reply.</summary>
    public bool GaveNumber;
}

/// <summary>
/// Conversation logic with no Unity dependency: creates people, advances their lives, runs a meeting,
/// keeps memory. All methods are safe to call from the thread pool.
/// </summary>
public sealed class NpcBrain
{
    private readonly LlmClient _llm;
    private readonly NpcStore _store;
    private readonly Func<BrainSettings> _settings;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, Task> _maintenance = new();
    /// <summary>Shared city memory (gossip); optional.</summary>
    public Social.WorldMemory? World;

    /// <param name="minors">The under-18 list (minors.json), shared by every brain and the memory indexer; none = in memory only.</param>
    public NpcBrain(LlmClient llm, NpcStore store, Func<BrainSettings> settings, Action<string> log, MinorList? minors = null)
    {
        _llm = llm;
        _store = store;
        _settings = settings;
        _log = log;
        KnownMinors = minors ?? new MinorList(null);
    }

    /// <summary>Everyone known to be under 18, kept apart from their records so a reset or a lost file can't undo it.</summary>
    public MinorList KnownMinors { get; }

    public NpcRecord? Peek(string id) => _store.Peek(id);
    public NpcStore Store => _store;

    /// <summary>With MemorySearch = Voyage: memories found by meaning, and kept indexed as they're made (null = by words only).</summary>
    public MemoryIndexer? Memory { get; set; }

    /// <summary>FB-50: the story conversations kept per story character, and their speech sheets (null = none kept).</summary>
    public StoryBook? Story { get; set; }

    /// <summary>The inner-life round: each person's phrase ledger and feelings (minds.json, per save; null = none kept).</summary>
    public Mind.MindBook? Minds { get; set; }

    // Speech sheets being written right now (one at a time per person).
    private readonly ConcurrentDictionary<string, bool> _sheetsWriting = new();

    /// <summary>
    /// FB-50: writes (or rewrites) a story character's speech sheet in the background once enough of their own lines are kept
    /// (only lines the player has seen). Never for anyone under 18. True when a sheet was written.
    /// </summary>
    public async Task<bool> MaybeWriteStorySheetAsync(string id, string name, CancellationToken ct)
    {
        var book = Story;
        if (book == null || !Features.StoryVoice || !book.NeedsSheet(id) || KnownMinors.Contains(id)) return false;
        if (!_sheetsWriting.TryAdd(id, true)) return false;
        try
        {
            var own = book.OwnLines(id);
            var res = await _llm.CompleteAsync(Prompts.StorySheetRequest(name, own), ct).ConfigureAwait(false);
            if (!res.Ok || res.Text.Trim().Length < 20) { _log($"Story: {name}'s way of talking wasn't written ({res.Error ?? "too short an answer"})."); return false; }
            string sheet = res.Text.Trim();
            if (sheet.Length > 1500) sheet = sheet[..1500].TrimEnd() + "…";
            book.SetSheet(id, sheet, own.Count);
            _log($"Story: {name}'s way of talking, written from {own.Count} of their lines.");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception e) { _log($"Story: {name}'s way of talking wasn't written ({e.Message})."); return false; }
        finally { _sheetsWriting.TryRemove(id, out _); }
    }

    /// <summary>This session's AI already said it can't look at pictures (they're left out from now on).</summary>
    public bool PicturesUnsupported => _llm.PicturesUnsupported;

    private int _epoch;
    private readonly ConcurrentDictionary<string, int> _personEpochs = new();
    /// <summary>Goes up on every /resetall: work that started before it must not write anything after it.</summary>
    public int Epoch => Volatile.Read(ref _epoch);
    /// <summary>/resetall: everyone's work in flight is dropped.</summary>
    public void NewEpoch() => Interlocked.Increment(ref _epoch);
    /// <summary>/resetnpc: only this person's work in flight is dropped (before, everyone's was).</summary>
    public void NewEpoch(string id) => _personEpochs.AddOrUpdate(id, 1, (_, n) => n + 1);
    /// <summary>
    /// Their epoch: changes on /resetall and on /resetnpc for them. Captured before work reads their record, and checked
    /// before it writes: a reset in between means it writes nothing.
    /// </summary>
    public long EpochFor(string id) => ((long)Volatile.Read(ref _epoch) << 32) | (uint)_personEpochs.GetValueOrDefault(id);

    /// <summary>The caller's token plus the answer limit (times <paramref name="answers"/>): see <see cref="BrainSettings.AnswerLimitSeconds"/>.</summary>
    private static CancellationTokenSource AnswerLimit(CancellationToken ct, BrainSettings cfg, int answers = 1)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (cfg.AnswerLimitSeconds > 0) cts.CancelAfter(TimeSpan.FromSeconds(cfg.AnswerLimitSeconds * (double)Math.Max(1, answers)));
        return cts;
    }

    /// <summary>For optional extras: half the answer limit, 10 to 30 seconds, never more than the limit itself (none without one).</summary>
    private static CancellationTokenSource ExtraLimit(CancellationToken ct, BrainSettings cfg)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (cfg.AnswerLimitSeconds > 0) cts.CancelAfter(TimeSpan.FromSeconds(Math.Min(cfg.AnswerLimitSeconds, Math.Clamp(cfg.AnswerLimitSeconds / 2, 10, 30))));
        return cts;
    }

    /// <summary>BeginAsync's error when the person turns out to be under 18: no conversation, the caller shows a bubble instead.</summary>
    public const string MinorError = "minor";

    /// <summary>
    /// Under 18, so never chatted with: on the under-18 list, a child in the story (the game shows it), a saved record
    /// that is a child's (see <see cref="Minors.WhyChild"/>), or — only when MinAge is set below 18 — someone new whose
    /// stable per-person roll lands below 18 (that roll follows the setting, so it never goes on the list). No AI is involved.
    /// </summary>
    public bool IsMinor(string id, NpcVisual visual) => IsMinor(id, visual, inQueue: false, out _);

    /// <summary>
    /// <see cref="IsMinor(string, NpcVisual)"/>, and whether the answer was made without their record because it's there
    /// but couldn't be read right now (<paramref name="unsure"/>: not one to keep for the session).
    /// </summary>
    public bool IsMinor(string id, NpcVisual visual, out bool unsure) => IsMinor(id, visual, inQueue: false, out unsure);

    /// <summary>
    /// The same, judged from a record the caller has already glanced at (null: no file, or it couldn't be read), so it
    /// isn't read twice (6a review: opening a chat).
    /// </summary>
    public bool IsMinor(string id, NpcVisual visual, NpcRecord? glanced, out bool unsure) => IsMinor(id, visual, inQueue: false, out unsure, () => glanced);

    /// <param name="inQueue">Called from work already running in this person's queue (it flags their file directly).</param>
    private bool IsMinor(string id, NpcVisual visual, bool inQueue) => IsMinor(id, visual, inQueue, out _);

    private bool IsMinor(string id, NpcVisual visual, bool inQueue, out bool unsure, Func<NpcRecord?>? read = null)
    {
        unsure = false;
        if (KnownMinors.Contains(id)) return true;
        if (visual.Minor) { MarkMinor(id, inQueue); return true; }
        double share = Population.MinorShare(_settings().Population);
        // Usually no file read is needed: only a story character (whose age the AI read from their record) or a MinAge
        // below 18 can make someone a minor.
        // ...except someone whose record was flagged later (a picture showed a child): read it when there is one.
        // A glance (one try, no waiting, history skipped: this runs on the main thread too). An unreadable file is judged
        // like no file here; that's safe because nothing reaches the AI or Voyage for that person until a Load reads them
        // (or refuses).
        var rec = read != null ? read() : _store.Glance(id);
        unsure = rec == null && _store.Has(id);
        if (share <= 0 && !visual.Named) return rec != null && IsKnownMinor(rec);
        if (rec != null) return IsKnownMinor(WithGameDescription(rec, visual));
        // A model that plainly looks grown up (or a machine) isn't rolled as a teenager.
        if (Looks.Find(visual.Model) is { } look && (look.Machine || look.MinAge >= YouthfulLook)) return false;
        return share > 0 && Social.FoodScene.Unit(Social.FoodScene.Hash(id + "|minor")) < share;
    }

    /// <summary>
    /// Whether this saved person is under 18: on the list, or their record says so (<see cref="Minors.WhyChild"/>). Anyone
    /// found by their record goes on the list for good.
    /// </summary>
    public bool IsKnownMinor(NpcRecord rec)
    {
        if (KnownMinors.Contains(rec.Id)) return true;
        var (why, lasting) = Minors.Judge(rec);
        if (why == null) return false;
        if (lasting && KnownMinors.Add(rec.Id)) _log($"{rec.Id} is under 18 ({why}): never chatted with or sent to any AI.");
        return true; // not lasting: a story character judged by an old file's age, until the game's description is known
    }

    /// <summary>
    /// For judging a story character's age, the description the game shows now (copied into the loaded record, as
    /// BeginAsync and EnsurePersonAsync do anyway), not one an old file may lack.
    /// </summary>
    private static NpcRecord WithGameDescription(NpcRecord rec, NpcVisual visual)
    {
        if (visual.Named && visual.Description.Length > 0) rec.Visual.Description = visual.Description;
        return rec;
    }

    /// <summary>Models whose youngest plausible age is below this can pass for a teenager (only matters when MinAge is under 18).</summary>
    public const int YouthfulLook = 22;

    /// <summary>
    /// Someone the game shows is a child: on the list for good, and a file from an older version is flagged too, so
    /// nothing (greetings included) uses it.
    /// </summary>
    private void MarkMinor(string id, bool inQueue)
    {
        KnownMinors.Add(id);
        bool Flag(NpcRecord r)
        {
            if (r.Visual.Minor) return false;
            r.Visual.Minor = true;
            _log($"{id} is a child: their old file is flagged and never used for AI again.");
            return true;
        }
        // Like any change to them (see Mutate): never a save over a meeting's record.
        if (inQueue) ApplyNow(id, Flag);
        else _ = Mutate(id, Flag);
    }

    // ------------------------------------------------------------------ meeting start

    /// <summary>Longest a new meeting waits for the last one with the same person to let go of their record.</summary>
    public const int SupersedeAfterSeconds = 15;
    /// <summary><see cref="SupersedeAfterSeconds"/> as used (tests shorten it).</summary>
    internal TimeSpan SupersedeAfter { get; set; } = TimeSpan.FromSeconds(SupersedeAfterSeconds);

    public async Task<(ChatSession? session, NpcReply? opening, string? error)> BeginAsync(
        string id, NpcVisual visual, GameSnapshot snap, CancellationToken ct, byte[]? picture = null)
    {
        var cfg = _settings();
        if (IsMinor(id, visual)) return (null, null, MinorError);
        long epoch = EpochFor(id); // before their record is read: a reset from here on means nothing of this meeting is saved
        // The last meeting with them lets go of their record first (closing waits for its last reply), and its diary is in
        // their record before this one begins. One that never lets go (it shouldn't happen) is taken over after a while.
        if (_live.TryGetValue(id, out var previous))
        {
            try { await previous.Done.Task.WaitAsync(SupersedeAfter, ct).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        await AfterSettled(id, ct).ConfigureAwait(false);

        // Inside their queue: load them (or write who they are, the first time) and take ownership of their record.
        var notices = new List<string>();
        var (session, isNew, error) = await Serialized(id, async () =>
        {
            if (_live.TryGetValue(id, out var stale)) Supersede(id, stale);
            var rec = _store.Load(id);
            if (rec != null) rec.Id = id; // the meeting is known by this id (a file can hold an older spelling of it)
            if (rec != null && IsKnownMinor(WithGameDescription(rec, visual))) return ((ChatSession?)null, false, (string?)MinorError); // before anything else
            bool created = rec == null;
            if (rec == null)
            {
                // Writing who they are gets its own wait (twice that with a picture, which may need a second try without it).
                int answers = picture != null ? 2 : 1;
                (NpcRecord? created, string? err) made;
                using (var limit = AnswerLimit(ct, cfg, answers))
                {
                    try { made = await CreatePersonAsync(id, visual, snap, cfg, limit.Token, picture, notices).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { made = (null, ProblemCodes.Slow(cfg.AnswerLimitSeconds * answers)); }
                }
                if (made.created == null) return (null, false, made.err);
                rec = made.created;
            }
            var owned = new ChatSession { Record = rec, Snapshot = snap, Notices = notices, Owner = this, Epoch = epoch };
            _live[id] = owned;
            return ((ChatSession?)owned, created, (string?)null);
        }).ConfigureAwait(false);
        if (session == null) return (null, null, error);

        // From here the meeting owns their record. Not handed to the caller (Esc while they answer, under 18, an error):
        // it lets go, keeping only what was saved and what's waiting for them (never an unanswered "Hey,").
        bool handedOver = false;
        try
        {
            var rec = session.Record;
            if (!isNew)
            {
                if (rec.Visual.Model.Length == 0 && visual.Model.Length > 0) rec.Visual.Model = visual.Model;
                // A story character's file from an older version can carry an age the game contradicts: the game wins.
                if (visual.Named && visual.Description.Length > 0) rec.Visual.Description = visual.Description;
                if (FixStoryAge(rec)) SaveSession(session);
                // LookAtNewPeople: someone met before the AI ever saw them gets one look now — before their life moves on
                // (an AI call about them), since the picture can show they're under 18.
                if (picture != null && !rec.Visual.LookedAt && !IsKnownMinor(rec) && !_llm.PicturesUnsupported)
                {
                    using var limit = AnswerLimit(ct, cfg);
                    try { await LookAgainAsync(rec, picture, notices, limit.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { _log($"Looking at {rec.Persona.Name} took too long; next time."); }
                    SaveSession(session);
                }
                if (IsKnownMinor(rec)) return (null, null, MinorError);
                if (cfg.LifeUpdates && rec.State.TimesMet > 0)
                {
                    // Optional: if it's slow, their life moves on next time instead.
                    using var limit = AnswerLimit(ct, cfg);
                    try { await MaybeAdvanceLifeAsync(rec, snap, cfg, limit.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { _log($"Life update for {rec.Persona.Name} took too long; skipped this time."); }
                    SaveSession(session);
                }
            }
            if (IsKnownMinor(rec)) return (null, null, MinorError);

            var st = rec.State;
            if (st.PlayerName.Equals("You", StringComparison.OrdinalIgnoreCase)) st.PlayerName = ""; // older builds stored the game's placeholder
            if (st.NameRevealed && st.KnownAs.Length == 0) st.KnownAs = rec.Persona.Name;      // records from before KnownAs existed
            bool first = st.TimesMet == 0;
            if (!rec.Visual.Named && !visual.Named && (visual.NameShown || visual.ShopName.Length > 0 || visual.Trade.Length > 0)
                && Shops.KnownByShownName(rec, visual) is { Length: > 0 } renamed)
                _log($"{id}: {renamed}; what they remember is kept."); // FB-57
            if (rec.Visual.Named || visual.Named)
            {
                // Named characters: their name is on screen in the game, so the player knows it. They only know the player
                // if the game says they've met (or they work for the player); otherwise the player is a stranger to them.
                rec.Visual.Named = true;
                if (visual.Description.Length > 0) rec.Visual.Description = visual.Description;
                rec.Visual.Employee = visual.Employee;
                st.NameRevealed = true;
                st.KnownAs = rec.Persona.Name;
                if (snap.NpcMetPlayer || visual.Employee || snap.NpcWorksForPlayer)
                {
                    st.MetInStory = true;
                    if (st.PlayerName.Length == 0 && snap.PlayerName.Length > 0) st.PlayerName = ReplyParser.Quotable(snap.PlayerName, 60);
                    if (st.Familiarity == "stranger") st.Familiarity = "acquaintance";
                }
            }
            // They know the player runs a place only from what they've actually got to know (or told, see ReplyAsync).
            if (snap.NpcWorksForPlayer || st.MetInStory || snap.NpcVisitsToYourPlace >= Social.VisitBook.RegularAfter) st.KnowsYourPlace = true;
            st.PreviousMeetingDay = first ? snap.GameDay : rec.LastGameDay;
            // How long since you last talked, in game minutes, whoever ended it (unknown for older records or an older save).
            int now = snap.GameDay * 1440 + snap.Hour * 60 + snap.Minute, then = st.LastTalkDay * 1440 + st.LastTalkMinute;
            st.MinutesSinceLastTalk = !first && st.LastTalkDay >= 0 && now >= then ? now - then : -1;
            st.TimesMet++;
            if (st.TimesMet >= 2 && st.Familiarity == "stranger") st.Familiarity = "acquaintance";
            if (Features.Relationships && st.TimesMet >= 3 && st.Relationship >= cfg.Milestones.FriendAt) st.Familiarity = "friend";
            rec.LastGameDay = snap.GameDay;
            rec.LastTalkedUtc = DateTime.UtcNow;
            // CF-16: what they told you before this fix, one idea many times over, is kept once (saved with this meeting).
            int tidied = ToldTidy.Tidy(rec);
            if (tidied > 0) _log($"{rec.Persona.Name}: {tidied} repeats of what they've said about themselves tidied away (CF-16).");

            // Moods off (FB-45): no roll; what they had is kept for when they're on again.
            if (Features.Moods)
            {
                var (mood, intensity) = Moods.Roll(Random.Shared, rec.Persona, snap.Hour, snap.Weather, Features.Feeling(st.Affinity), rec.Visual.IsAndroid, cfg.Friendliness);
                string carriedReason = "";
                // The inner-life round: how they still feel from what happened lately (insulted an hour ago, stood up last
                // night) is carried in, instead of a fresh roll; the greeting's mood a moment ago still comes first.
                if (Features.InnerLife && Moods.Normalize(snap.CarriedMood) is { Length: > 0 } carried)
                {
                    mood = carried;
                    if (snap.CarriedIntensity is "mild" or "moderate" or "strong") intensity = snap.CarriedIntensity;
                    carriedReason = ReplyParser.Quotable(snap.CarriedReason, 160);
                }
                if (Moods.Normalize(snap.ApproachMood) is { Length: > 0 } waved) { mood = waved; carriedReason = ""; } // the mood they greeted you in a moment ago
                st.Mood = mood;
                st.MoodIntensity = intensity;
                if (string.IsNullOrWhiteSpace(st.MoodReason) || first) st.MoodReason = "";
                if (carriedReason.Length > 0) st.MoodReason = carriedReason;
            }

            session.FirstMeeting = first;
            session.StartIndex = rec.History.Count;
            if (Features.Moods) session.MoodTrail.Add(st.Mood);
            session.PatienceBudget = Patience.Budget(rec, snap, Features.Mood(st.Mood), cfg.PatienceScale);
            session.AnnoyanceThreshold = Patience.Threshold(rec);
            if (Patience.Recent(rec, snap) is { } recent && recent.reason.Contains("enough") && recent.minutes < 120)
                session.Annoyance = session.AnnoyanceThreshold * 0.5; // still sore about it
            AddLine(rec, "scene", Prompts.SceneOpening(rec, snap, first), snap);
            SaveSession(session);

            // Who speaks first: someone who waved the player over does; otherwise the player says the opening line
            // ("Hey,") and they answer it. With no opening line, nobody says anything until the player types.
            bool theyCalledYou = snap.Approach.Length > 0;
            string opener = theyCalledYou ? "" : OpeningLineFor(cfg);
            if (!theyCalledYou && opener.Length == 0) { handedOver = true; return (session, null, null); }
            var hello = opener.Length > 0 ? AddLine(rec, "player", opener, snap) : null;
            session.OpenerLine = hello;

            var opening = await ReplyAsync(session, cfg, ct, opening: true).ConfigureAwait(false);
            if (!opening.Ok)
            {
                // The meeting can still go ahead; the player just speaks first.
                _log("Opening line failed: " + opening.Error);
                if (hello != null) { rec.History.Remove(hello); session.OpenerLine = null; }
            }
            handedOver = true;
            return (session, opening, null);
        }
        finally
        {
            if (!handedOver) await Release(session, ReleaseMode.Abort).ConfigureAwait(false);
        }
    }

    /// <summary>A meeting that never let go of their record (it shouldn't happen): taken over; the changes waiting for them stay.</summary>
    private void Supersede(string id, ChatSession stale)
    {
        lock (stale.Gate)
        {
            stale.Released = true;
            Requeue(id, stale.Unsaved); // what its record took in at a save that failed goes to the next owner
            stale.Unsaved.Clear();
        }
        _live.TryRemove(new KeyValuePair<string, ChatSession>(id, stale));
        stale.Done.TrySetResult(false);
        _log($"A conversation with {id} that never let go of their record was taken over by a new one (its last unsaved moments are lost).");
    }

    /// <summary>
    /// The person behind an id for something other than talking to the player (NPC chatter): created by the AI if they
    /// don't exist yet, but nothing about meeting the player changes. Runs after anything pending for them, and a chat
    /// the player starts with them waits for it. Null for anyone under 18, or if they couldn't be created (then
    /// <paramref name="failed"/> hears why: FB-84, world chat says so).
    /// </summary>
    public Task<NpcRecord?> EnsurePersonAsync(string id, NpcVisual visual, GameSnapshot snap, CancellationToken ct, Action<string>? failed = null) =>
        Serialized(id, async () =>
        {
            if (IsMinor(id, visual, inQueue: true)) return (NpcRecord?)null;
            if (_live.ContainsKey(id))
            {
                // Talking to you right now: what was last saved, as a copy of its own (the meeting keeps changing its record).
                var saved = _store.Load(id);
                return saved == null || IsKnownMinor(saved) ? null : saved;
            }
            var rec = _store.Load(id);
            if (rec != null)
            {
                if (visual.Named && visual.Description.Length > 0) rec.Visual.Description = visual.Description;
                if (FixStoryAge(rec)) _store.Save(rec);
                return IsKnownMinor(rec) ? null : rec;
            }
            var (created, err) = await CreatePersonAsync(id, visual, snap, _settings(), ct).ConfigureAwait(false);
            if (created == null) { _log($"Couldn't create {id} for a conversation: {err}"); failed?.Invoke(err ?? ""); return null; }
            return IsKnownMinor(created) ? null : created;
        });

    /// <summary>The player's automatic opening line, cleaned like anything else they say ("" = none).</summary>
    public static string OpeningLineFor(BrainSettings cfg) => ReplyParser.EscapePlayerText(cfg.OpeningLine ?? "").Trim();

    private async Task<(NpcRecord?, string?)> CreatePersonAsync(string id, NpcVisual visual, GameSnapshot snap, BrainSettings cfg,
        CancellationToken ct, byte[]? picture = null, List<string>? notices = null)
    {
        if (!cfg.KeepGameNames && !visual.KnownByName()) visual.GameName = ""; // FB-57: a name the game shows is kept
        var seeds = PersonaSeeds.Roll(Random.Shared, visual, cfg.Population);
        if (picture != null && _llm.PicturesUnsupported) picture = null; // already found out this session: the caller said so once
        // FB-50: a story character the player has story conversations with has met them, and is written to fit that story.
        var story = visual.Named && Story is { } book && book.LineCount(id) > 0 ? book : null;
        if (story != null && Features.StoryMemory) snap.NpcMetPlayer = true; // FB-51: only while their story is used
        LlmRequest Persona(byte[]? pic)
        {
            var req = Prompts.PersonaRequest(visual, seeds, snap, cfg.MaxGenerationTokens, pic);
            if (story != null)
            {
                if (Features.StoryVoice && Prompts.StoryVoiceBlock(story.Sheet(id), story.OwnLines(id)) is { Length: > 0 } voice) req.System.Add(voice);
                if (Features.StoryMemory && Prompts.StoryBlock(story.Scenes(id), snap.PlayerName) is { Length: > 0 } told)
                    req.System.Add("What follows is what they have said to the player in the game's story: who you write must fit it.\n" + told);
            }
            return req;
        }
        var res = await _llm.CompleteAsync(Persona(picture), ct).ConfigureAwait(false);
        if (!res.Ok && res.ImageRejected)
        {
            // The model can't look at pictures: say so, and write them from the game's description instead.
            notices?.Add(res.Error + " They were written without it; LookAtNewPeople can be turned off, or use a model that can see.");
            _log("Snapshot not accepted: " + res.Error);
            picture = null;
            res = await _llm.CompleteAsync(Persona(null), ct).ConfigureAwait(false);
        }
        if (!res.Ok) return (null, res.Refused ? "The AI declined to create this person; try someone else." : res.Error);

        var persona = ParsePersona(res.Text);
        if (persona == null || string.IsNullOrWhiteSpace(persona.Name))
            return (null, $"The AI's description of this person couldn't be read: {(persona != null || LlmClient.ExtractJsonObject(res.Text) != null ? "it gave them no name" : res.WhyUnreadable())}.");
        // Without a picture nothing here can show a child, so someone written as one (the player's own instructions can ask
        // for that) is asked for once more, and never saved as an adult living a child's life.
        if (picture == null && !visual.Named && !visual.IsAndroid && WrittenYoung(persona))
        {
            _log($"The AI wrote {id} as someone under 18; asking again.");
            var again = await _llm.CompleteAsync(Prompts.PersonaRequest(visual, seeds, snap, cfg.MaxGenerationTokens), ct).ConfigureAwait(false);
            persona = again.Ok ? ParsePersona(again.Text) : null;
            if (persona == null || string.IsNullOrWhiteSpace(persona.Name) || WrittenYoung(persona))
                return (null, "The AI kept writing this person as someone under 18, so they weren't created (your own instructions may be asking for it). Try someone else.");
        }
        persona.Speech.SlangLevel = persona.Speech.SlangLevel is < 0 or > 3 ? seeds.SlangLevel : persona.Speech.SlangLevel;
        if (persona.Temperament.Length == 0) persona.Temperament = seeds.Temperament;
        if (visual.Named || visual.GameName.Length > 0) persona.Name = visual.GameName; // the game's name is canon
        if (snap.NpcWorkplace.Length > 0) persona.Workplace = snap.NpcWorkplace;
        ApplySeeds(persona, seeds, visual, cfg.Population, sawThem: picture != null);
        visual.LookedAt = picture != null;

        var rec = new NpcRecord
        {
            Id = id,
            CreatedUtc = DateTime.UtcNow,
            CreatedGameDay = snap.GameDay,
            LastGameDay = snap.GameDay,
            LastLifeUpdateGameDay = snap.GameDay,
            LastLifeUpdateUtc = DateTime.UtcNow,
            Visual = visual,
            Persona = persona,
        };
        if (visual.Minor) KnownMinors.Add(id); // the picture, or the game's own description, showed a child (before the file: a refused Create can't lose it)
        _store.Create(rec); // never over a file that's there but wasn't read (ROBUST-4)
        _log($"New resident {id}: {persona.Name}, {persona.Age}, {persona.Occupation} (slang {persona.Speech.SlangLevel}, {persona.Temperament}).");
        return (rec, null);
    }

    /// <summary>
    /// A story character's age as the game describes them (Banor: "a cheerful old man"), for a file written before ages
    /// came from the game: if what's saved doesn't fit, the game's age wins, and an age they "told" you that doesn't fit is
    /// taken off the panel. True if anything changed.
    /// </summary>
    internal bool FixStoryAge(NpcRecord rec)
    {
        var v = rec.Visual;
        if (!v.Named || v.IsAndroid) return false;
        var canon = CanonAge.FromDescription(v.Description);
        if (canon == null) return false;
        if (canon.Child) { KnownMinors.Add(rec.Id); if (v.Minor) return false; v.Minor = true; return true; }
        var p = rec.Persona;
        var (said, _) = CanonAge.FromSeen(p.Age, unreadableIsChild: false);
        bool fits = said is { } a && (canon.Exact is { } x ? a == x : a >= canon.Min && a <= canon.Max);
        // int.TryParse on purpose: it reads only ASCII digits, so an age that fits but is written another way ("٣٥", "35 years")
        // is rewritten as plain digits below, the one form every later check reads the same way.
        if (fits && int.TryParse(p.Age, out _)) return false;
        int age = fits ? said!.Value : canon.Exact ?? (canon.Min + canon.Max) / 2;
        string before = p.Age;
        p.Age = age.ToString();
        int dropped = rec.State.Told.RemoveAll(t => ToldTopics.Normalize(t.Topic) == "age"
                                                    && CanonAge.FromSeen(t.Said, unreadableIsChild: false).age is { } toldAge && Math.Abs(toldAge - age) > 3);
        _log($"{p.Name}'s age is now {age}, from the game's own description (\"{canon.Cue}\"); their file said \"{before}\"" +
             (dropped > 0 ? " (the age they'd told you was taken off the panel)." : "."));
        return true;
    }

    /// <summary>
    /// LookAtNewPeople for someone met before the AI saw them: how they look (and, for people the game doesn't give an
    /// age, how old they look) is brought up to date from one picture. Who they are and what they remember stay.
    /// </summary>
    private async Task LookAgainAsync(NpcRecord rec, byte[] picture, List<string> notices, CancellationToken ct)
    {
        var res = await _llm.CompleteAsync(Prompts.LookAgainRequest(rec, picture), ct).ConfigureAwait(false);
        if (!res.Ok)
        {
            if (res.ImageRejected) notices.Add(res.Error + " Nothing about them changed. LookAtNewPeople can be turned off, or use a model that can see.");
            else _log($"Looking at {rec.Persona.Name} failed: {res.Error}");
            return;
        }
        var o = LlmClient.ExtractJsonObject(res.Text);
        if (o == null) { _log($"Looking at {rec.Persona.Name}: the answer couldn't be read ({res.WhyUnreadable()})."); return; }
        string S(string k) => o[k] is JsonValue jv && jv.TryGetValue<string>(out var s) ? ReplyParser.Quotable(ReplyParser.Blank(s), 600) : ""; // the full audit (A-004)
        var p = rec.Persona;
        var v = rec.Visual;
        if (S("appearance") is { Length: > 10 and < 500 } look) p.Appearance = look;
        if (S("style") is { Length: > 2 and < 200 } style) p.Style = style;
        if (S("distinctiveMarks") is { Length: > 2 and < 200 } marks) p.DistinctiveMarks = marks;
        var canon = v.Named ? CanonAge.FromDescription(v.Description) : null;
        if (!v.IsAndroid && canon == null)
        {
            // People the game gives no age: the picture decides how old they look, and anyone it puts under 18 isn't chatted with.
            string ageText = o["apparentAge"] switch { JsonValue jv when jv.TryGetValue<string>(out var s) => s, JsonValue jv => jv.ToString(), _ => "" };
            var (seen, young) = CanonAge.FromSeen(ageText);
            if ((young || CanonAge.SoundsYoung(p.Appearance)) && AdultModel(v))
            {
                // An adult character model: the picture (a back, a bad angle) is what's wrong. Their age stays.
                _log($"{p.Name}'s picture made them look under 18, but their character model is an adult's: their age stays {p.Age}.");
            }
            else if (young || CanonAge.SoundsYoung(p.Appearance))
            {
                v.Minor = true;
                KnownMinors.Add(rec.Id);
                _log($"{p.Name} looks under 18 in the picture: never chatted with from now on.");
            }
            else if (seen is > 0 and < 111)
            {
                var (now, _) = CanonAge.FromSeen(p.Age, unreadableIsChild: false);
                if (now == null || Math.Abs(now.Value - seen.Value) > 8)
                {
                    _log($"{p.Name} looks about {seen} in the picture (their file said {p.Age}).");
                    p.Age = seen.Value.ToString();
                    rec.State.Told.RemoveAll(t => ToldTopics.Normalize(t.Topic) == "age"
                                                  && CanonAge.FromSeen(t.Said, unreadableIsChild: false).age is { } told && Math.Abs(told - seen.Value) > 3);
                }
            }
        }
        v.LookedAt = true; // saved by the caller (the meeting owns their record)
        _log($"Looked at {p.Name}: {p.Appearance}");
    }

    /// <summary>A new person written as under 18: by their age, how they look, or a school pupil's "job".</summary>
    private static bool WrittenYoung(Persona p) =>
        CanonAge.FromSeen(p.Age, unreadableIsChild: false).child || CanonAge.SoundsYoung(p.Appearance) || CanonAge.SchoolPupil(p.Occupation);

    /// <summary>
    /// A character model known to be an adult's (its portrait look puts it at 18 or over). The game's children are other
    /// models (and flagged from the game itself), so a picture can't turn one of these into a child.
    /// </summary>
    internal static bool AdultModel(NpcVisual v) => !v.IsAndroid && Looks.Find(v.Model) is { Machine: false, MinAge: >= Population.AdultAge };

    /// <summary>The dice are canon: age, gender identity and pronouns don't drift with the model's taste.</summary>
    public static void ApplySeeds(Persona persona, PersonaSeeds seeds, NpcVisual visual, PopulationSettings pop, bool sawThem = false)
    {
        var id = seeds.Identity;
        if (!visual.Named)
        {
            if (!visual.IsAndroid)
            {
                // With a picture the AI judges the age it can see, however it writes it. Anyone it puts under 18, calls
                // a teenager or a kid (in their age, how they look or their "job"), or whose age can't be read at all,
                // is never chatted with.
                var (seen, young) = sawThem ? CanonAge.FromSeen(persona.Age) : (null, false);
                bool looksYoung = sawThem && (young || CanonAge.SoundsYoung(persona.Appearance) || CanonAge.SchoolPupil(persona.Occupation));
                if (looksYoung && AdultModel(visual))
                {
                    // An adult character model (the game's children have their own, caught elsewhere): a picture taken
                    // from a bad angle doesn't make them a child. The age comes from their look instead.
                    seen = null;
                }
                else if (looksYoung) visual.Minor = true;
                persona.Age = (seen is > 0 and < 111 ? seen.Value : seeds.Age).ToString();
            }
            persona.GenderIdentity = id.Gender;
            persona.Pronouns = id.Pronouns;
            persona.PronounsFlexible = id.PronounsFlexible;
        }
        else
        {
            if (persona.GenderIdentity.Length == 0) persona.GenderIdentity = visual.IsAndroid ? "agender" : visual.Body;
            // A story character keeps the age the game gives them (the player's age range is only for generated people).
            if (!visual.IsAndroid)
            {
                var canon = CanonAge.FromDescription(visual.Description);
                // The model's own reading of their record, however it wrote it ("34", "seventeen", "late teens").
                var (said, saidYoung) = CanonAge.FromSeen(persona.Age, unreadableIsChild: false);
                if (canon?.Exact is { } exact) persona.Age = exact.ToString();
                else if (canon != null) persona.Age = (said is { } s && s >= canon.Min && s <= canon.Max ? s : (canon.Min + canon.Max) / 2).ToString();
                else if (said is { } s2) persona.Age = s2.ToString();
                // Safety net: a story character the game (or the model, reading their record) puts under 18 is never chatted with.
                if (canon?.Child == true || (canon == null && saidYoung)
                    || (CanonAge.ReadDigits(persona.Age) is int final && final < Population.AdultAge)) visual.Minor = true; // digits in any script
            }
        }
        if (persona.Orientation.Length == 0) persona.Orientation = id.Orientation;
        if (persona.RelationshipStatus.Length == 0) persona.RelationshipStatus = seeds.Relationship;
        if (persona.Faith.Length == 0) persona.Faith = seeds.Faith;
        if (persona.Politics.Length == 0) persona.Politics = seeds.Politics;
        if (persona.Humor.Length == 0) persona.Humor = seeds.Humor;
        if (persona.Honesty.Length == 0) persona.Honesty = seeds.Honesty;
        if (persona.ConflictStyle.Length == 0) persona.ConflictStyle = seeds.ConflictStyle;
        if (persona.Education.Length == 0) persona.Education = seeds.Education;
        if (persona.Health.Length == 0) persona.Health = seeds.Health;
        if (persona.Memory.Length == 0) persona.Memory = seeds.Memory;
    }

    public static Persona? ParsePersona(string text)
    {
        var obj = LlmClient.ExtractJsonObject(text);
        if (obj == null) return null;
        if (!obj.ContainsKey("name") && obj.Count == 1 && obj.First().Value is JsonObject inner) obj = inner;
        // Their speech style can only be read from an object: anything else there is left out (they get the default style).
        foreach (var key in obj.Where(kv => kv.Key.Equals("speech", StringComparison.OrdinalIgnoreCase) && kv.Value is not JsonObject).Select(kv => kv.Key).ToList())
            obj.Remove(key);
        try
        {
            // A wrong shape (a number for their age, a list written as text, a null) is read, not refused (ROBUST-1).
            var persona = obj.Deserialize<Persona>(NpcStore.ReadJson);
            persona?.Repair();
            if (persona != null) Quote(persona);
            return persona;
        }
        catch { return null; }
    }

    /// <summary>
    /// Everything the AI wrote about who they are goes into their later prompts, so it's made safe to quote there, as their
    /// life updates and diaries are (the full audit, A-004): no brackets that could open a [Scene: ...] or [[note]], no line
    /// breaks. Every text field and list, the speech style's too (by reflection, so a field added later is covered).
    /// </summary>
    internal static void Quote(object o)
    {
        foreach (var prop in o.GetType().GetProperties())
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            if (prop.PropertyType == typeof(string)) prop.SetValue(o, ReplyParser.Quotable((string?)prop.GetValue(o), 4000));
            else if (prop.GetValue(o) is List<string> list)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    string q = ReplyParser.Quotable(list[i], 600);
                    if (q.Length == 0) list.RemoveAt(i); else list[i] = q;
                }
            }
            else if (prop.PropertyType == typeof(SpeechStyle) && prop.GetValue(o) is { } speech) Quote(speech);
        }
    }

    private async Task MaybeAdvanceLifeAsync(NpcRecord rec, GameSnapshot snap, BrainSettings cfg, CancellationToken ct)
    {
        int days = snap.GameDay - rec.LastLifeUpdateGameDay;
        double hours = (DateTime.UtcNow - rec.LastLifeUpdateUtc).TotalHours;
        if (days < 0)
        {
            // An older save or a new game: re-anchor instead of inventing negative time.
            rec.LastLifeUpdateGameDay = snap.GameDay;
            days = 0;
        }
        bool due = days >= Math.Max(1, cfg.LifeUpdateAfterGameDays) || (cfg.LifeUpdateAfterRealHours > 0 && hours >= cfg.LifeUpdateAfterRealHours);
        if (!due) return;

        var res = await _llm.CompleteAsync(Prompts.LifeUpdateRequest(rec, snap, Math.Max(days, 1), hours, cfg.MaxGenerationTokens), ct).ConfigureAwait(false);
        if (!res.Ok) { _log($"Life update for {rec.Persona.Name} skipped: {res.Error}"); return; }
        var o = LlmClient.ExtractJsonObject(res.Text);
        if (o == null) { _log($"Life update for {rec.Persona.Name} couldn't be read ({res.WhyUnreadable()})."); return; }

        string S(string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        var p = rec.Persona;
        // What the AI writes here goes back into every later prompt: quoted safely (no brackets, no line breaks).
        void Set(string key, Action<string> apply) { var s = S(key); if (s.Length > 0 && !s.Equals("unchanged", StringComparison.OrdinalIgnoreCase)) apply(ReplyParser.Quotable(s, 300)); }
        if (snap.NpcWorkplace.Length == 0) // a real job from the game is never rewritten
        {
            Set("occupation", s => p.Occupation = s);
            Set("workplace", s => p.Workplace = s);
        }
        Set("livesIn", s => p.LivesIn = s);
        Set("shortTermGoal", s => p.ShortTermGoal = s);
        Set("longTermGoal", s => p.LongTermGoal = s);
        Set("currentWorry", s => p.CurrentWorry = s);
        Set("temperament", s => p.Temperament = s);
        rec.State.MoodReason = ReplyParser.Quotable(S("moodReason"), 160);
        if (o["events"] is JsonArray evs)
            foreach (var e in evs)
                if (e is JsonValue ev && ev.TryGetValue<string>(out var t) && t.Trim().Length > 0)
                    rec.LifeLog.Add(new LifeEvent { GameDay = snap.GameDay, Utc = DateTime.UtcNow, Text = ReplyParser.Quotable(t, 600) });
        if (rec.LifeLog.Count > 40) rec.LifeLog.RemoveRange(0, rec.LifeLog.Count - 40);
        rec.LastLifeUpdateGameDay = snap.GameDay;
        rec.LastLifeUpdateUtc = DateTime.UtcNow; // saved by the caller (the meeting owns their record)
        _log($"{p.Name}'s life moved on: {string.Join(" ", rec.LifeLog.TakeLast(3).Select(e => e.Text))}");
    }

    // ------------------------------------------------------------------ talking

    public async Task<NpcReply> SayAsync(ChatSession s, string playerText, GameSnapshot snap, CancellationToken ct)
    {
        if (s.Owner != null && s.Owner != this) return await s.Owner.SayAsync(s, playerText, snap, ct).ConfigureAwait(false);
        string text = Roleplay.PlayerActions(ReplyParser.EscapePlayerText(playerText)); // CF-20: *your actions* as [They do: ...]
        if (text.Length == 0) return new NpcReply { Ok = false, Error = "" };
        // CF-18: moved since the last turn (taken somewhere, or walked off together): said in the conversation, and kept.
        if (s.Snapshot != null && Prompts.PlaceChangeLine(s.Snapshot, snap) is { } moved) AddLine(s.Record, "scene", moved, snap);
        s.Snapshot = snap;
        s.Exchanges++;
        var line = AddLine(s.Record, "player", text, snap);
        NpcReply reply;
        try { reply = await ReplyAsync(s, _settings(), ct).ConfigureAwait(false); }
        catch
        {
            // Gave up waiting, the chat closed, or something failed: their words weren't answered, so they aren't
            // remembered either (and can be sent again without being there twice).
            s.Record.History.Remove(line);
            s.Exchanges--;
            throw;
        }
        if (!reply.Ok)
        {
            s.Record.History.Remove(line); // let the player resend
            s.Exchanges--;
            return reply;
        }
        return reply;
    }

    /// <summary>The open favour this dish fills, if any (same dish, allowing for "Ramen" vs "Spicy Ramen").</summary>
    public static Favour? MatchingFavour(NpcRecord rec, string dish) =>
        rec.State.Favours.FirstOrDefault(f => f.Status == "open" && DishMatches(f.Dish, dish));

    public static bool DishMatches(string a, string b)
    {
        a = (a ?? "").Trim();
        b = (b ?? "").Trim();
        if (a.Length == 0 || b.Length == 0) return false;
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return System.Text.RegularExpressions.Regex.IsMatch(longer, $@"(?i)(?<![\p{{L}}\p{{N}}]){System.Text.RegularExpressions.Regex.Escape(shorter)}(?![\p{{L}}\p{{N}}])", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// The player hands over food. If it's what they asked for, they pay <paramref name="paid"/> (already worked out by the
    /// caller, discount and delivery included); otherwise it's a gift. They react in their own way.
    /// </summary>
    public async Task<NpcReply> HandOverAsync(ChatSession s, string dish, int paid, string priceNote, bool delivered, GameSnapshot snap, CancellationToken ct)
    {
        if (s.Owner != null && s.Owner != this) return await s.Owner.HandOverAsync(s, dish, paid, priceNote, delivered, snap, ct).ConfigureAwait(false);
        s.Snapshot = snap;
        var st = s.Record.State;
        var fav = MatchingFavour(s.Record, dish);
        var other = fav == null ? st.Favours.FirstOrDefault(f => f.Status == "open") : null;
        if (fav != null) fav.Status = "done";
        string what = fav != null ? " It's what you asked them for."
            : other != null ? $" It isn't what you asked for (you wanted {other.Dish}) — they're giving you this for free."
            : " You didn't ask for anything — it's a gift, free.";
        string pay = fav != null ? $" You pay them {paid} Lims{(priceNote.Length > 0 ? $" ({priceNote})" : "")}." : "";
        string scene = $"[Scene: They hand you {WithArticleOrNot(dish)}{(delivered ? ", brought all the way over to you" : "")}.{what}{pay}]";
        AddLine(s.Record, "scene", scene, snap);
        string note = fav != null
            ? "[They just brought you the food you asked for and you paid. React the way you would — try it, thank them, comment on the price or the wait, whatever fits you.]"
            : "[They just handed you food for free. React the way you would — touched, suspicious, delighted, awkward, whatever fits you and how you feel about them.]";
        var reply = await ReplyAsync(s, _settings(), ct, extraNote: note).ConfigureAwait(false);
        // The food changed hands whatever the AI did; only their words can be missing. Nothing is undone.
        var bonus = _settings();
        if (Features.Relationships) st.Shift(fav != null ? bonus.FoodFavourBonus : bonus.FoodGiftBonus); // the author: a free gift 1.5, food they asked for 1
        SaveSession(s);
        return reply;
    }

    private static string WithArticleOrNot(string dish) =>
        dish.StartsWith("a ", StringComparison.OrdinalIgnoreCase) || dish.StartsWith("an ", StringComparison.OrdinalIgnoreCase)
        || dish.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? dish : "a " + dish;

    /// <summary>
    /// A passing reaction outside a conversation — a wave, a call-out, a nod, a snub. One short line, remembered as its own
    /// little moment so they know they did it. Without the AI (or if it fails), <paramref name="fallback"/> is used.
    /// </summary>
    public async Task<string?> BarkAsync(string id, string situation, string fallback, string mood, GameSnapshot snap, bool useAi, CancellationToken ct)
    {
        // Talking to you right now: no passing greeting. The AI call runs outside their queue (it can be slow), on a copy
        // of their record to read; the greeting is then added as a change (see Mutate).
        if (_live.ContainsKey(id)) return null;
        long epoch = EpochFor(id);
        NpcRecord? rec;
        // Their file can't be read right now: no line at all, so nothing is shown or voiced from an older copy of them
        // (one that may not show they're under 18; review of step 5, part 2).
        try { rec = _store.Load(id); }
        catch (NpcFileUnreadableException) { return null; }
        if (rec == null || IsKnownMinor(rec)) return null;
        var cfg = _settings();
        string greetMood = Moods.Normalize(mood);
        if (Features.Moods && greetMood.Length > 0) { rec.State.Mood = greetMood; rec.State.MoodReason = ""; } // the prompt sees the greeting's mood
        string spoken = "";
        if (useAi)
        {
            var req = new LlmRequest { Purpose = "greet", MaxTokens = Math.Min(cfg.MaxReplyTokens, 1500), Background = true };
            req.System.Add(Prompts.IdentityBlock(rec, structured: false, cfg.Friendliness));
            if (CustomInstructions.Block() is { Length: > 0 } own) req.System.Add(own);
            req.System.Add(Prompts.SituationBlock(rec, snap));
            if (Prompts.SpeakIn() is { Length: > 0 } speak) req.System.Add(speak);
            req.System.Add(Prompts.SafetyLast);
            // The inner-life round (VaryLines): three ways to say it, one per line; the code uses the one least like what they said lately.
            bool vary = cfg.VaryLines && Features.InnerLife && Minds != null;
            req.Messages.Add(new LlmMessage("user", vary
                ? $"[Scene: {situation} Answer with three different ways you might say it, one per line, each only the words you'd say out loud: a few words, no tags, no actions, no quotation marks, no numbering.]"
                : $"[Scene: {situation} Answer with only the words you say out loud: a few words, no tags, no actions, no quotation marks.]"));
            var res = await _llm.CompleteAsync(req, ct).ConfigureAwait(false);
            if (res.Ok)
            {
                var candidates = new List<string>();
                foreach (var raw in vary ? res.Text.Split('\n') : new[] { res.Text })
                {
                    var p = ReplyParser.Parse(Mind.PhraseLedger.Unnumbered(raw), rec.Persona.Name, rec.Persona.Nickname);
                    string said = ReplyParser.Brief(p.Spoken).Trim().Trim('"', '“', '”');
                    if (said.Length is > 0 and < 140 && !ReplyParser.LooksOutOfCharacter(said, rec.Visual.IsAndroid)) candidates.Add(said);
                    if (candidates.Count >= 3) break;
                }
                if (candidates.Count > 0)
                {
                    var (openers, lines) = Minds?.Phrases(id) ?? (new List<string>(), new List<string>());
                    spoken = candidates[Math.Max(0, Mind.PhraseLedger.Pick(candidates, lines, openers))];
                    if (Features.InnerLife) Minds?.Said(id, rec.Persona.Name, spoken, Mind.GameClock.Of(snap));
                }
            }
            else _log($"Greeting line for {rec.Persona.Name} fell back to a stock line: {res.Error}");
        }
        if (spoken.Length == 0) spoken = fallback;
        if (spoken.Length == 0) return null;
        string scene = $"[Scene: Day {snap.GameDay}, {snap.Clock}, {(snap.Location.Length > 0 ? snap.Location : "the city")}. {situation}]";
        await Mutate(id, r =>
        {
            if (EpochFor(id) != epoch) return false;
            if (Features.Moods && greetMood.Length > 0) { r.State.Mood = greetMood; r.State.MoodReason = ""; }
            AddLine(r, "scene", scene, snap);
            AddLine(r, "npc", spoken, snap);
            return true;
        }, skipIfLive: true).ConfigureAwait(false);
        return spoken;
    }

    /// <summary>
    /// The phone: their answer to your texts (<paramref name="first"/> false), or a text they send you first. Read from a
    /// copy of their record (the AI call can be slow), then kept in it as a change (Mutate): a scene line saying what was
    /// texted (so a chat in person knows), how it made them feel. Null for anyone under 18, nobody, or a failed call
    /// (<paramref name="error"/> says why); an empty list when they had nothing to say.
    /// </summary>
    public async Task<(List<string>? texts, bool coming, string? error)> TextAsync(string id, IReadOnlyList<Phone.PhoneBook.Text> thread, GameSnapshot snap, bool first, CancellationToken ct, string note = "")
    {
        if (first && _live.ContainsKey(id)) return (null, false, "talking to you");
        long epoch = EpochFor(id);
        NpcRecord? rec;
        try { rec = _store.Load(id); }
        catch (NpcFileUnreadableException) { return (null, false, "their file can't be read"); }
        if (rec == null || IsKnownMinor(rec)) return (null, false, "nobody");
        var cfg = _settings();
        var req = Phone.Texting.Request(rec, snap, thread, first, cfg.Friendliness, snap.PlayerGender, cfg.MaxReplyTokens, note);
        var res = await _llm.CompleteAsync(req, ct).ConfigureAwait(false);
        if (!res.Ok) return (null, false, res.Error ?? "no answer");
        var (texts, affinity, mood, coming) = Phone.Texting.Read(res.Text, rec.Persona.Name, rec.Persona.Nickname);
        if (texts.Any(t => ReplyParser.LooksOutOfCharacter(t, rec.Visual.IsAndroid))) return (null, false, "an answer out of character");
        var yours = first ? new List<string>() : thread.Reverse().TakeWhile(t => t.From == Phone.PhoneBook.You).Reverse().Select(t => t.Body).ToList();
        string scene = Phone.Texting.SceneLine(snap.GameDay, snap.Clock, yours, texts, first);
        string normalized = Moods.Normalize(mood ?? "");
        await Mutate(id, r =>
        {
            if (EpochFor(id) != epoch || texts.Count == 0) return false;
            AddLine(r, "scene", scene, snap);
            if (Features.Relationships && affinity != 0) r.State.Shift(Relationships.Step(affinity, cfg.RelationshipGainMax, cfg.RelationshipLossMin, cfg.RelationshipLossMax));
            if (Features.Moods && normalized.Length > 0) { r.State.Mood = normalized; r.State.MoodReason = "texting"; }
            return true;
        }).ConfigureAwait(false);
        if (EpochFor(id) != epoch) return (null, false, "a reset");
        return (texts, coming && !first, null);
    }

    /// <summary>
    /// FB-71 Nightline: one visit to the app (their post, what they make of the lines they scrolled to), in their own voice and
    /// memory. Returns what they did (held to what was asked: Lines.Read), their name, and an error. Nothing for anyone under
    /// 18, anyone in a chat with you, or a reset in between.
    /// </summary>
    public async Task<(Phone.Lines.Result? result, string name, string? error)> LineAsync(string id, GameSnapshot snap, bool post, IReadOnlyList<Phone.Lines.Seen> seen,
        bool wantHandle, IReadOnlyList<string> photoAreas, bool hasOwnPicture, string doing, string withYou, CancellationToken ct)
    {
        if (_live.ContainsKey(id)) return (null, "", "talking to you");
        long epoch = EpochFor(id);
        NpcRecord? rec;
        try { rec = _store.Load(id); }
        catch (NpcFileUnreadableException) { return (null, "", "their file can't be read"); }
        if (rec == null || IsKnownMinor(rec) || rec.Persona.Name.Length == 0) return (null, "", "nobody");
        var cfg = _settings();
        var req = Phone.Lines.Request(rec, snap, post, seen, wantHandle, photoAreas, hasOwnPicture, doing, withYou, cfg.Friendliness, snap.PlayerGender);
        var res = await _llm.CompleteAsync(req, ct).ConfigureAwait(false);
        if (!res.Ok && res.ImageRejected)
        {
            // FB-80: their AI can't take pictures: asked again, your photo described as one they can't see (they go by your words).
            foreach (var s in seen) s.Image = null;
            req = Phone.Lines.Request(rec, snap, post, seen, wantHandle, photoAreas, hasOwnPicture, doing, withYou, cfg.Friendliness, snap.PlayerGender);
            res = await _llm.CompleteAsync(req, ct).ConfigureAwait(false);
        }
        if (!res.Ok) return (null, rec.Persona.Name, res.Error ?? "no answer");
        var result = Phone.Lines.Read(res.Text, seen, post, photoAreas);
        if (!result.Ok) return (null, rec.Persona.Name, "the answer couldn't be read");
        if ((result.Post.Length > 0 && ReplyParser.LooksOutOfCharacter(result.Post, rec.Visual.IsAndroid)) || result.Acts.Any(a => ReplyParser.LooksOutOfCharacter(a.Comment, rec.Visual.IsAndroid)))
            return (null, rec.Persona.Name, "an answer out of character");
        if (EpochFor(id) != epoch) return (null, rec.Persona.Name, "a reset");
        return (result, rec.Persona.Name, null);
    }

    /// <summary>FB-71: a Nightline visit kept in their memory (a talk of its own kind: never pushes out real conversations).</summary>
    /// <summary>
    /// FB-76: they overheard you talking with people near them (world chat): a talk of the overheard kind (never pushes out
    /// real conversations; only a few kept). Nothing for anyone under 18, or without a record of their own.
    /// </summary>
    public Task RememberOverheardYou(string id, IReadOnlyList<string> lines, string summary, GameSnapshot snap)
    {
        long epoch = EpochFor(id);
        if (lines.Count == 0 || summary.Length == 0) return Task.CompletedTask;
        return Mutate(id, r =>
        {
            if (EpochFor(id) != epoch || IsKnownMinor(r)) return false;
            r.AddTalk(new Talk { GameDay = snap.GameDay, Clock = snap.Clock ?? "", Where = snap.Location ?? "", Kind = Talk.Overheard,
                                 Lines = lines.Take(6).Select(l => ReplyParser.Quotable(l, 300)).ToList(), Summary = ReplyParser.Quotable(summary, 200) });
            return true;
        });
    }

    public Task RememberLine(string id, IReadOnlyList<string> lines, string summary, GameSnapshot snap)
    {
        long epoch = EpochFor(id);
        if (lines.Count == 0 || summary.Length == 0) return Task.CompletedTask;
        return Mutate(id, r =>
        {
            if (EpochFor(id) != epoch || IsKnownMinor(r)) return false;
            r.AddTalk(new Talk { GameDay = snap.GameDay, Clock = snap.Clock ?? "", Where = "Nightline", Kind = Talk.Nightline, Lines = lines.Take(12).ToList(), Summary = summary });
            return true;
        });
    }

    /// <summary>
    /// The phone: Ava's answer (or a text she sends first), from her seen story lines only (Story, the speech sheet). Returns
    /// her texts, whether she asked for a voice note, and an error. Nothing when story memory is off or she's unknown.
    /// </summary>
    public async Task<(List<string>? texts, bool voice, string? error)> AvaTextAsync(string id, string name, IReadOnlyList<Phone.PhoneBook.Text> thread, GameSnapshot snap, bool first, bool voiceOffer, CancellationToken ct, string note = "")
    {
        var book = Story;
        if (book == null || !Features.StoryMemory) return (null, false, "story memory is off");
        var cfg = _settings();
        var own = book.OwnLines(id);
        // FB-78: what she's said already (her story lines, her recent texts): never sent again word for word.
        var said = own.Concat(thread.Where(x => x.From == Phone.PhoneBook.Them).TakeLast(40).Select(x => x.Body)).ToList();
        for (int attempt = 0; ; attempt++)
        {
            string asked = attempt == 0 ? note : (note + " You just repeated something you'd already said, word for word: say something new.").Trim();
            var req = Phone.Texting.AvaRequest(name, snap.PlayerName, book.Sheet(id), own, book.Scenes(id), snap, thread, first, cfg.MaxReplyTokens, asked, voiceOffer);
            var res = await _llm.CompleteAsync(req, ct).ConfigureAwait(false);
            if (!res.Ok) return (null, false, res.Error ?? "no answer");
            bool voice = voiceOffer && Phone.Texting.WantsVoice(res.Text);
            var (texts, _, _, _) = Phone.Texting.Read(res.Text.Replace("[[voice]]", "", StringComparison.OrdinalIgnoreCase), name, ""); // she's in your HUD: never "coming"
            if (texts.Any(t => ReplyParser.LooksOutOfCharacter(t, isAndroid: false))) return (null, false, "an answer out of character");
            var fresh = Phone.Texting.Fresh(texts, said);
            if (fresh.Count > 0) return (fresh, voice, null);
            if (texts.Count == 0 || attempt >= 1) return (null, false, "an answer that only repeated what she'd said");
        }
    }

    /// <summary>
    /// The phone (FB-70): something that happened by text, kept in their record as a scene line (they saw your text and
    /// chose not to answer yet, say), so they remember it in a chat and in the texts that follow. Never for anyone under 18.
    /// </summary>
    public Task RecordScene(string id, string scene, GameSnapshot snap)
    {
        long epoch = EpochFor(id);
        return Mutate(id, r =>
        {
            if (EpochFor(id) != epoch || IsKnownMinor(r) || scene.Length == 0) return false;
            AddLine(r, "scene", scene, snap);
            return true;
        });
    }

    /// <summary>
    /// Runs work on one person's record after anything already pending for them, never alongside it. Nothing in that
    /// work may await this person's queue again (it would wait for itself): <see cref="Settled"/> refuses to.
    /// </summary>
    private Task<T> Serialized<T>(string id, Func<Task<T>> work)
    {
        Task<T> task;
        lock (_serialLock)
        {
            _maintenance.TryGetValue(id, out var before);
            task = Task.Run(async () =>
            {
                if (before != null) { try { await before.ConfigureAwait(false); } catch { } }
                _inQueue.Value = id;
                try { return await work().ConfigureAwait(false); }
                finally { _inQueue.Value = null; }
            });
            _maintenance[id] = task;
        }
        task.ContinueWith(_ => _maintenance.TryRemove(new KeyValuePair<string, Task>(id, task)));
        return task;
    }

    private readonly object _serialLock = new();
    /// <summary>Whose queue the current work runs in (for the one guard that matters: Settled from inside it).</summary>
    private static readonly AsyncLocal<string?> _inQueue = new();

    // ------------------------------------------------------------------ who owns a record: the live-session registry
    //
    // While you talk to someone, the meeting (its ChatSession) owns their record: it alone saves it. Everything else that
    // changes them (residents' talk, calls, thoughts, greetings, the diary and summary after a meeting, being flagged
    // under 18) goes through Mutate: applied at once when nobody is talking to them, otherwise queued for them and
    // added to the meeting's record at its next save. So a meeting's save never erases what happened meanwhile.

    /// <summary>Whose record is owned by a meeting right now (registered inside their queue; released inside it).</summary>
    private readonly ConcurrentDictionary<string, ChatSession> _live = new();
    /// <summary>Changes waiting for a meeting's next save, per person (never per meeting: ending one in any way can't lose them).</summary>
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<NpcRecord, bool>>> _pending = new();
    /// <summary>Work after a meeting that runs outside the queue (the diary and summary AI calls), chained per person.</summary>
    private readonly ConcurrentDictionary<string, Task> _after = new();
    private readonly object _afterLock = new();

    /// <summary>Whether a meeting owns this person's record now (tests, and the greeting check).</summary>
    public bool IsLive(string id) => _live.ContainsKey(id);

    private ConcurrentQueue<Func<NpcRecord, bool>> PendingFor(string id) => _pending.GetOrAdd(id, _ => new ConcurrentQueue<Func<NpcRecord, bool>>());

    /// <summary>Takes everything waiting for them, oldest first.</summary>
    private List<Func<NpcRecord, bool>> TakePending(string id)
    {
        var taken = new List<Func<NpcRecord, bool>>();
        if (_pending.TryGetValue(id, out var queue))
            while (queue.TryDequeue(out var change)) taken.Add(change);
        return taken;
    }

    /// <summary>Applies <paramref name="changes"/> to <paramref name="rec"/> in order. Returns how many changed it.</summary>
    private int ApplyAll(string id, NpcRecord rec, IEnumerable<Func<NpcRecord, bool>> changes)
    {
        int changed = 0;
        foreach (var change in changes)
        {
            try { if (change(rec)) changed++; }
            catch (Exception e) { _log($"A change to {id}'s record failed: {e.Message}"); }
        }
        return changed;
    }

    /// <summary>
    /// Puts changes back at the front of their queue, ahead of anything newer (a save failed, or a meeting was taken
    /// over): the next save of their record adds them. Only called inside their queue, where nothing else adds to it.
    /// </summary>
    private void Requeue(string id, List<Func<NpcRecord, bool>> changes)
    {
        if (changes.Count == 0) return;
        var later = TakePending(id);
        var queue = PendingFor(id);
        foreach (var change in changes) queue.Enqueue(change);
        foreach (var change in later) queue.Enqueue(change);
    }

    /// <summary>
    /// A change to someone's record from outside your conversation with them: applied once anything already pending for
    /// them is done (load, change, save) — or, while you're talking to them, queued and added to the meeting's record at
    /// its next save. The task completes when it has been applied or queued; it never waits for a meeting to end.
    /// <paramref name="change"/> must be quick (no AI, no waiting) and do its own epoch check; it returns whether it changed
    /// anything. <paramref name="skipIfLive"/>: dropped instead while a meeting owns them (a passing greeting).
    /// </summary>
    public Task Mutate(string id, Func<NpcRecord, bool> change, bool skipIfLive = false) =>
        Serialized(id, () => { ApplyNow(id, change, skipIfLive); return Task.FromResult(true); });

    /// <summary>Mutate's body, for work already running in this person's queue.</summary>
    private void ApplyNow(string id, Func<NpcRecord, bool> change, bool skipIfLive = false)
    {
        if (_live.ContainsKey(id))
        {
            if (!skipIfLive) PendingFor(id).Enqueue(change);
            return;
        }
        // Anything left waiting by a meeting that couldn't save it (or was taken over) goes first, in order.
        var all = TakePending(id);
        int stranded = all.Count;
        all.Add(change);
        try
        {
            var rec = _store.Load(id);
            if (rec == null || ApplyAll(id, rec, all) == 0) return;
            _store.Save(rec);
            Memory?.Queue(id);
        }
        catch (Exception e)
        {
            // Kept for the next save of their record (a passing greeting isn't: it would land in a later conversation).
            Requeue(id, skipIfLive ? all.Take(stranded).ToList() : all);
            _log($"Changing {id}'s record failed: {e.Message}");
        }
    }

    /// <summary>
    /// Every save of a meeting's record: with the changes waiting for them added first. Nothing is saved once the meeting
    /// is released, superseded by another, or a reset happened since it began.
    /// </summary>
    private void SaveSession(ChatSession s)
    {
        string id = s.Record.Id;
        lock (s.Gate)
        {
            if (s.Released || EpochFor(id) != s.Epoch || !_live.TryGetValue(id, out var owner) || owner != s) return;
            var taken = TakePending(id);
            ApplyAll(id, s.Record, taken);
            try
            {
                _store.Save(s.Record);
                s.Unsaved.Clear();
            }
            catch (Exception e)
            {
                s.Unsaved.AddRange(taken); // in the meeting's record, but not on disk yet: kept until a save works
                _log($"Saving {s.Record.Persona.Name}'s record failed: {e.Message}");
            }
        }
    }

    /// <summary>
    /// The meeting lets go of the record, inside the person's queue (see <see cref="ReleaseMode"/>). True only for
    /// <see cref="ReleaseMode.End"/> when this meeting still owned the record and no reset happened since it began, so
    /// its record was saved: what follows a meeting (the diary) runs only then.
    /// </summary>
    public Task<bool> Release(ChatSession s, ReleaseMode mode)
    {
        if (s.Owner != null && s.Owner != this) return s.Owner.Release(s, mode);
        string id = s.Record.Id;
        return Serialized(id, () =>
        {
            bool saved = false, queueIndex = false;
            List<Func<NpcRecord, bool>>? abortWaiting = null;
            lock (s.Gate)
            {
                if (!s.Released)
                {
                    s.Released = true;
                    bool owner = _live.TryGetValue(id, out var cur) && cur == s;
                    if (owner) _live.TryRemove(new KeyValuePair<string, ChatSession>(id, s));
                    bool current = owner && EpochFor(id) == s.Epoch;
                    try
                    {
                        // A reset: what's waiting for them goes too (only as the owner: a newer meeting's own queue stays).
                        if (owner && (mode == ReleaseMode.Discard || !current))
                        {
                            PendingFor(id).Clear();
                            s.Unsaved.Clear();
                        }
                        else if (current && mode == ReleaseMode.End)
                        {
                            var taken = TakePending(id);
                            ApplyAll(id, s.Record, taken);
                            try { _store.Save(s.Record); saved = queueIndex = true; }
                            catch
                            {
                                // Not on disk: what came from outside the meeting waits for the next save of their record.
                                Requeue(id, s.Unsaved.Concat(taken).ToList());
                                throw;
                            }
                            finally { s.Unsaved.Clear(); }
                        }
                        else if (current && mode == ReleaseMode.Abort)
                        {
                            // Only what was saved already, plus what's waiting for them: never the half-finished meeting.
                            // Applied once the Gate is let go (reading their file can wait for another program's lock).
                            abortWaiting = s.Unsaved.Concat(TakePending(id)).ToList();
                            s.Unsaved.Clear();
                        }
                    }
                    catch (Exception e) { _log($"Saving {s.Record.Persona.Name}'s record at the end failed: {e.Message}"); }
                }
            }
            if (abortWaiting != null)
            {
                try
                {
                    var fresh = _store.Load(id);
                    if (fresh != null && ApplyAll(id, fresh, abortWaiting) > 0) { _store.Save(fresh); queueIndex = true; }
                }
                catch (Exception e)
                {
                    Requeue(id, abortWaiting); // what came from outside the meeting waits for the next save of their record
                    _log($"Saving {s.Record.Persona.Name}'s record at the end failed: {e.Message}");
                }
            }
            s.Done.TrySetResult(saved);
            if (queueIndex) Memory?.Queue(id);
            return Task.FromResult(saved);
        });
    }

    /// <summary>Work after a meeting that runs outside the queue (AI calls), one after another per person; Settled waits for it.</summary>
    private void TrackAfter(string id, Func<Task> work)
    {
        lock (_afterLock)
        {
            _after.TryGetValue(id, out var before);
            Task task = Task.Run(async () =>
            {
                if (before != null) { try { await before.ConfigureAwait(false); } catch { } }
                try { await work().ConfigureAwait(false); }
                catch (Exception e) { _log($"Work after a meeting with {id} failed: {e.Message}"); }
            });
            _after[id] = task;
            task.ContinueWith(_ => { lock (_afterLock) _after.TryRemove(new KeyValuePair<string, Task>(id, task)); });
        }
    }

    /// <summary>
    /// The player has stood there saying nothing. Stage 1: they react (check on you, prod you). Stage 2: they're done
    /// waiting and leave with a parting line of their own choosing. The silence is remembered.
    /// </summary>
    public async Task<NpcReply> SilenceAsync(ChatSession s, int stage, int seconds, GameSnapshot snap, CancellationToken ct)
    {
        if (s.Owner != null && s.Owner != this) return await s.Owner.SilenceAsync(s, stage, seconds, snap, ct).ConfigureAwait(false);
        s.Snapshot = snap;
        s.SilentStage = stage;
        string scene = stage <= 1
            ? $"[Scene: About {seconds} seconds pass. The stranger just stands there in front of you, not saying a word, not moving.]"
            : "[Scene: Still nothing. They're just standing there, staring at you, silent.]";
        var line = AddLine(s.Record, "scene", scene, snap);
        string note = stage <= 1
            ? "[The stranger has gone silent. React the way you would — check if they're okay, prod them, joke, or get impatient — in one short line. Don't leave yet unless you were already about to.]"
            : "[You're done waiting. Say a short parting line that fits you and how you feel right now — worried enough to fetch help, weirded out, annoyed, whatever is true to you — and set leaving to true.]";
        NpcReply reply;
        try { reply = await ReplyAsync(s, _settings(), ct, extraNote: note, forceLeave: stage >= 2).ConfigureAwait(false); }
        catch { s.Record.History.Remove(line); throw; } // not answered (cancelled or failed): the silence isn't remembered
        if (!reply.Ok) s.Record.History.Remove(line);
        return reply;
    }

    /// <summary>
    /// Memories closest in meaning to <paramref name="heard"/> (MemorySearch = Voyage), as clearly as their age allows; the
    /// conversation going on now is left out (it's in front of them). Empty when nothing fits or Voyage is off or slow.
    /// </summary>
    private async Task<string> RecallByMeaningAsync(NpcRecord rec, string heard, BrainSettings cfg, int today, int k, string heading, CancellationToken ct)
    {
        if (Memory == null) return "";
        try
        {
            var live = rec.History.TakeLast(Math.Max(1, cfg.RecentMessages)).Where(l => l.Role != "scene" && l.Text.Trim().Length > 0)
                .Select(l => l.Text.Trim()).ToList();
            var found = await Memory.RecallAsync(rec.Id, heard, k, e => e.Kind == "you" && live.Any(t => e.Text.Contains(t)), ct).ConfigureAwait(false);
            if (found.Count > 0) _log($"{rec.Persona.Name}: {found.Count} memory(ies) come back by meaning.");
            return Memories.Format(found, today, cfg.RecallFades, Random.Shared, heading);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            _log("Memory search skipped this time: " + e.Message);
            return "";
        }
    }

    private async Task<NpcReply> ReplyAsync(ChatSession s, BrainSettings cfg, CancellationToken ct, bool opening = false, string? extraNote = null, bool forceLeave = false)
    {
        var rec = s.Record;
        // Moments from older conversations that what they just said brings back.
        var lastPlayer = rec.History.LastOrDefault();
        int today = s.Snapshot.GameDay;
        string memories = lastPlayer != null && lastPlayer.Role == "player"
            ? Recall.Format(Recall.Search(rec, lastPlayer.Text, cfg.RecentMessages),
                "[THINGS THAT COME BACK TO YOU] (older moments with them their words just reminded you of; recall them the way your memory works)",
                today, cfg.RecallFades, Random.Shared)
            : "";
        // With MemorySearch = Voyage: what their words bring back by meaning, from all your memories (with them, with other
        // people, your own life), as clearly as time allows. Only when the past comes up (the author's Q3): their words
        // reach back, or name someone you know. Otherwise you can still reach for a memory yourself ("recall", below).
        if (Memory != null && lastPlayer?.Role == "player" && Recall.AboutThePast(lastPlayer.Text, rec))
        {
            string byMeaning = await RecallByMeaningAsync(rec, lastPlayer.Text, cfg, today, 4,
                "[IT REMINDS YOU OF] (your own memories that what they just said brings to mind; only if it fits, the way memory works)", ct).ConfigureAwait(false);
            if (byMeaning.Length > 0) memories = string.Join("\n", new[] { memories, byMeaning }.Where(x => x.Length > 0));
        }
        // Their last two replies both asked something: not a third time (FB-14).
        string questions = opening ? "" : Prompts.QuestionNote(rec.History, s.StartIndex);
        // The inner-life round: the turn card (words, a statement when due, depth, slang, who they sound like) is the last
        // note; it says "End on a statement" itself, so the question note goes in it.
        string turn = !opening && Features.InnerLife ? TurnCardFor(s, cfg, questions.Length > 0) : "";
        // Roleplay: what they did lately this meeting, so a move isn't repeated (stated as what to do).
        string moves = Features.Roleplay && s.Actions.Count > 0
            ? $"[What you did in your last lines: {string.Join(" ", s.Actions.TakeLast(3))} If you show something this time, make it something new.]" : "";
        string Notes() => string.Join("\n", new[] { memories, extraNote ?? "", moves, turn.Length > 0 ? turn : questions }.Where(x => x.Length > 0));
        var req = BuildChat(s, cfg, Notes().Length > 0 ? Notes() : null);
        // Their answer (with any memory look-ups and an out-of-character retry) must come within the limit; nothing
        // about them has changed yet if it doesn't. The optional extras after it have their own, shorter limit.
        using var answer = AnswerLimit(ct, cfg);
        LlmResult res;
        try { res = await _llm.CompleteAsync(req, answer.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new NpcReply { Ok = false, Error = ProblemCodes.Slow(cfg.AnswerLimitSeconds) }; }
        // They reach for a memory that isn't in front of them: look it up and let them answer (at most twice).
        for (int tries = 0; tries < 2 && res.Ok; tries++)
        {
            var peek = ParseReply(res.Text, rec, cfg);
            if (string.IsNullOrWhiteSpace(peek.RecallQuery)) break;
            var found = Recall.Search(rec, peek.RecallQuery!, cfg.RecentMessages, 10);
            string heading = $"[YOU THINK BACK: \"{peek.RecallQuery}\"] This is what's there in your memory (as clear or as hazy as your memory makes it). Now answer them; leave recall empty.";
            string thought = Recall.Format(found, heading, today, cfg.RecallFades, Random.Shared);
            if (Memory != null)
            {
                string more = await RecallByMeaningAsync(rec, peek.RecallQuery!, cfg, today, 6, thought.Length > 0 ? "[AND BY WHAT IT REMINDS YOU OF]" : heading, ct).ConfigureAwait(false);
                if (more.Length > 0) thought = string.Join("\n", new[] { thought, more }.Where(x => x.Length > 0));
            }
            if (thought.Length == 0)
                thought = $"[YOU THINK BACK: \"{peek.RecallQuery}\"] Nothing specific comes back to you. Answer the way you would when you can't remember; leave recall empty.";
            memories = string.Join("\n", new[] { memories, thought }.Where(x => x.Length > 0));
            _log($"{rec.Persona.Name} tries to remember \"{peek.RecallQuery}\" ({found.Count} lines found).");
            try { res = await _llm.CompleteAsync(BuildChat(s, cfg, Notes()), answer.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Their first answer was only "let me think" (it has no words of its own): nothing has changed yet, so say so.
                return new NpcReply { Ok = false, Error = ProblemCodes.Slow(cfg.AnswerLimitSeconds) };
            }
        }
        if (!res.Ok && !res.Refused) return new NpcReply { Ok = false, Error = res.Error };

        ParsedReply parsed;
        if (res.Refused)
            parsed = new ParsedReply { Spoken = ReplyParser.Deflection(Random.Shared, rec.Persona.Speech.SlangLevel) };
        else
        {
            parsed = ParseReply(res.Text, rec, cfg);
            if (ReplyParser.LooksOutOfCharacter(parsed.Spoken, rec.Visual.IsAndroid))
            {
                _log($"{rec.Persona.Name} slipped out of character; retrying. Text was: {parsed.Spoken}");
                LlmResult? retry = null;
                try { retry = await _llm.CompleteAsync(BuildChat(s, cfg, (Notes().Length > 0 ? Notes() + "\n" : "") + Prompts.OutOfCharacterNudge(rec.Persona.Name)), answer.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { _log("The retry took too long; they deflect instead."); }
                var p2 = retry is { Ok: true } ? ParseReply(retry.Text, rec, cfg) : null;
                parsed = p2 != null && !ReplyParser.LooksOutOfCharacter(p2.Spoken, rec.Visual.IsAndroid)
                    ? p2
                    : new ParsedReply { Spoken = ReplyParser.Deflection(Random.Shared, rec.Persona.Speech.SlangLevel) };
            }
        }

        if (opening) parsed.Spoken = ReplyParser.Brief(parsed.Spoken);
        // Only what their words actually carry reaches the panel (the model sometimes lists their whole profile).
        parsed.Told = parsed.Told.Where(t => ToldCheck.Supported(t.topic, t.said, parsed.Spoken)).ToList();
        string? playerLine = lastPlayer?.Role == "player" ? lastPlayer.Text : null;
        if (!res.Refused) await CatchWhatTheyToldAsync(rec, playerLine, parsed, cfg, ct).ConfigureAwait(false);
        var st = rec.State;
        // What they learn about the player has to come from the player's own words this meeting.
        var playerSaid = rec.History.Skip(Math.Min(s.StartIndex, rec.History.Count)).Where(l => l.Role == "player").Select(l => l.Text).ToList();
        parsed.Facts = parsed.Facts.Where(f => ToldCheck.AboutPlayerSupported(f, playerSaid)).ToList();
        if (playerLine != null && !st.KnowsYourPlace
            && (ReplyParser.SaysTheyRunAPlace(playerLine, s.Snapshot.PlayerVenues)
                || (parsed.LearnedTheyRunAPlace && ReplyParser.MentionsAPlace(playerLine, s.Snapshot.PlayerVenues))))
        {
            st.KnowsYourPlace = true;
            _log($"{rec.Persona.Name} now knows you run a place.");
        }
        if (Features.Moods) // FB-45: off, nothing of a mood is written (what they had is kept)
        {
            var mood = Moods.Normalize(parsed.Mood);
            if (mood.Length > 0) st.Mood = mood;
            if (parsed.MoodIntensity is "mild" or "moderate" or "strong") st.MoodIntensity = parsed.MoodIntensity;
            if (!string.IsNullOrWhiteSpace(parsed.MoodReason) && parsed.MoodReason!.Length < 160) st.MoodReason = ReplyParser.Quotable(parsed.MoodReason, 160);
            if (s.MoodTrail.Count == 0 || s.MoodTrail[^1] != st.Mood) s.MoodTrail.Add(st.Mood);
        }
        // The relationship bar moves slowly: the AI's −10..10 rating becomes a fraction of a point (settings). Off: it stays.
        if (Features.Relationships) st.Shift(Relationships.Step(parsed.AffinityDelta, cfg.RelationshipGainMax, cfg.RelationshipLossMin, cfg.RelationshipLossMax));
        // Everything the AI noted here goes back into later prompts: quoted safely (no brackets, no line breaks).
        if (!string.IsNullOrWhiteSpace(parsed.LearnedName)) st.PlayerName = ReplyParser.Quotable(parsed.LearnedName, 60);
        foreach (var f in parsed.Facts.Select(f => ReplyParser.Quotable(f, 300)).Where(f => f.Length > 0))
            if (!st.PlayerFacts.Any(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase))) st.PlayerFacts.Add(f);
        if (st.PlayerFacts.Count > 30) st.PlayerFacts.RemoveRange(0, st.PlayerFacts.Count - 30);
        bool revealed = false;
        string intro = ReplyParser.Quotable(parsed.IntroducedAs, 60);
        if (intro.Length is > 0 and < 60 && !rec.Visual.Named)
        {
            revealed = !st.NameRevealed || !intro.Equals(st.KnownAs, StringComparison.OrdinalIgnoreCase);
            st.KnownAs = intro; // exactly what they told you, nickname or alias included
            st.NameRevealed = true;
        }
        else if (parsed.GaveName && !st.NameRevealed)
        {
            revealed = true;
            st.NameRevealed = true;
            if (st.KnownAs.Length == 0) st.KnownAs = rec.Persona.Name;
        }
        if (parsed.SharedIdentity && !st.IdentityShared) st.IdentityShared = true;
        if (cfg.Favours && st.KnowsYourPlace && !string.IsNullOrWhiteSpace(parsed.WantsFood))
        {
            string dish = parsed.WantsFood!.Trim();
            var item = s.Snapshot.Menu.FirstOrDefault(m => m.Dish.Equals(dish, StringComparison.OrdinalIgnoreCase))
                    ?? s.Snapshot.Menu.FirstOrDefault(m => DishMatches(m.Dish, dish));
            st.Favours.RemoveAll(f => f.Status == "open"); // one standing order at a time
            st.Favours.Add(new Favour
            {
                Dish = item?.Dish ?? dish,
                How = parsed.WantsFoodHow == "visit" ? "visit" : "delivery",
                GameDay = s.Snapshot.GameDay,
                Price = item?.Price ?? 0,
            });
            _log($"{rec.Persona.Name} wants {(item?.Dish ?? dish)} ({parsed.WantsFoodHow}).");
        }
        ApplyTold(rec, parsed.Told, s.Snapshot.GameDay, afterMeeting: false);
        foreach (var f in parsed.SelfFacts.Select(f => ReplyParser.Quotable(f, 200)).Where(f => f.Length > 0))
            if (!rec.Persona.EstablishedFacts.Any(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase))) rec.Persona.EstablishedFacts.Add(f);
        if (rec.Persona.EstablishedFacts.Count > 80) rec.Persona.EstablishedFacts.RemoveRange(0, rec.Persona.EstablishedFacts.Count - 80);
        if (Features.Relationships && st.TimesMet >= 3 && st.Relationship >= cfg.Milestones.FriendAt) st.Familiarity = "friend";

        if (cfg.NpcsCanLeave)
        {
            s.Annoyance = Math.Max(0, s.Annoyance + Patience.AnnoyanceDelta(parsed.AffinityDelta, Features.Mood(st.Mood), st.MoodIntensity));
            // Backstop for models that never take the hint: well past their time or far past fed up, they go.
            if (forceLeave) parsed.Leave = true;
            if (!parsed.Leave && !opening && (s.Exchanges >= s.PatienceBudget + 4 || s.Annoyance >= s.AnnoyanceThreshold * 1.7))
                parsed.Leave = true;
            if (parsed.Leave && !forceLeave && !s.LastChance && EndsWithQuestion(parsed.Spoken))
            {
                // "Make it quick — what's the question?" then walking off is wrong: hear the answer, then go.
                parsed.Leave = false;
                s.LastChance = true;
                s.PatienceBudget = Math.Max(s.PatienceBudget, s.Exchanges + 1);
                _log($"{rec.Persona.Name} wanted to leave but asked a question; waiting for the answer first.");
            }
        }
        else parsed.Leave = forceLeave;
        if (parsed.Leave && EndsWithQuestion(parsed.Spoken))
        {
            // Their one wait for an answer is used up (or they're leaving regardless): asking something and walking off
            // before the answer reads as a bug, so they get a proper parting line instead.
            string asked = parsed.Spoken;
            parsed.Spoken = await PartingLineAsync(s, cfg, Notes(), asked, ct).ConfigureAwait(false);
            parsed.Gesture = ""; // the gesture went with the line that was replaced
            _log($"{rec.Persona.Name} was leaving on a question (\"{asked}\"); parting line now: \"{parsed.Spoken}\"");
        }
        if (parsed.Leave && cfg.NpcsCanLeave)
        {
            st.LastLeftReason = Patience.LeaveReason(s);
            st.LastLeftGameDay = s.Snapshot.GameDay;
            st.LastLeftMinute = s.Snapshot.Hour * 60 + s.Snapshot.Minute;
            _log($"{rec.Persona.Name} leaves: {st.LastLeftReason} ({s.Exchanges}/{s.PatienceBudget} exchanges, annoyance {s.Annoyance:0.#}/{s.AnnoyanceThreshold:0.#}{(s.LastChance ? ", after hearing their answer" : "")}).");
        }

        // The inner-life round: a filler they keep opening with is trimmed, and the line goes into their phrase ledger.
        if (Features.InnerLife && Minds is { } minds && !res.Refused)
        {
            parsed.Spoken = Mind.PhraseLedger.Trim(parsed.Spoken, minds.Phrases(rec.Id).openers);
            minds.Said(rec.Id, rec.Persona.Name, parsed.Spoken, Mind.GameClock.Of(s.Snapshot));
        }
        // Roleplay: what they visibly do as they say it (the AI's call), one clean sentence in their pronouns and yours;
        // the *stage direction* a model put in its words when it gave none; never the same move twice running; a gesture the
        // game has (a nod, a wave) is played too when the AI named none.
        string action = "";
        if (Features.Roleplay && !res.Refused)
        {
            var np = Roleplay.Of(rec.Persona.Pronouns);
            var pp = Roleplay.Player(s.Snapshot.PlayerGender);
            action = Roleplay.Clean(parsed.Action, np, pp);
            if (action.Length == 0 && parsed.Stripped.Count > 0) action = Roleplay.Clean(parsed.Stripped[0], np, pp);
            if (action.Length > 0 && s.Actions.Any(a => Mind.PhraseLedger.Similar(a, action) > 0.6)) action = "";
            if (action.Length > 0)
            {
                s.Actions.Add(action);
                if (s.Actions.Count > 4) s.Actions.RemoveAt(0);
                if (parsed.Gesture.Length == 0) parsed.Gesture = Roleplay.GestureFor(action);
            }
        }
        AddLine(rec, "npc", parsed.Spoken, s.Snapshot);
        SaveSession(s);
        return new NpcReply { Spoken = parsed.Spoken, Leave = parsed.Leave, NameJustRevealed = revealed, Gesture = parsed.Gesture, Extras = parsed.Extras, Action = action, GaveNumber = Features.Phone && parsed.GaveNumber };
    }

    /// <summary>The inner-life round: this reply's turn card, from who they are, how they feel, where the conversation is and how close you are.</summary>
    private string TurnCardFor(ChatSession s, BrainSettings cfg, bool noQuestion)
    {
        var rec = s.Record;
        var st = rec.State;
        bool wrapUp = cfg.NpcsCanLeave && s.Exchanges >= Math.Max(1, s.PatienceBudget - 1);
        var openers = Minds?.Phrases(rec.Id).openers ?? new List<string>();
        return Mind.TurnCard.For(new Mind.TurnInputs
        {
            Name = rec.Persona.Name, Persona = rec.Persona, Mood = Features.Mood(st.Mood), Intensity = st.MoodIntensity, Exchange = s.Exchanges,
            NoQuestion = noQuestion, WrapUp = wrapUp, Bar = Features.Standing(st.Relationship), TimesMet = st.TimesMet, RecentOpeners = openers,
        }, Random.Shared);
    }

    /// <summary>
    /// Makes sure the "what you know" panel hears everything they answered. Smaller models often answer a question
    /// ("Twenty-two. Why?") without reporting it, so: free checks first (their age, their name), then — only if a
    /// question about them is still unaccounted for — one small background read of the exchange.
    /// </summary>
    private async Task CatchWhatTheyToldAsync(NpcRecord rec, string? playerLine, ParsedReply parsed, BrainSettings cfg, CancellationToken ct)
    {
        if (playerLine == null || parsed.Spoken.Trim().Length == 0) return;
        if (!parsed.Told.Any(t => t.topic == "age") && ReplyParser.AgeStated(playerLine, parsed.Spoken) is { } age)
            parsed.Told.Add(("age", age));
        bool askedName = !rec.Visual.Named && parsed.IntroducedAs == null && ReplyParser.AskedForName(playerLine);
        if (askedName && ReplyParser.BareName(parsed.Spoken, rec.Persona.Name, rec.Persona.Nickname) is { } bare)
        {
            parsed.IntroducedAs = bare;
            parsed.GaveName = true;
            askedName = false;
        }
        var missing = ReplyParser.TopicsAsked(playerLine).Where(t => !parsed.Told.Any(x => ReplyParser.Covers(x.topic, t))).ToList();
        if (missing.Count == 0 && !askedName) return;
        using var extra = ExtraLimit(ct, cfg);
        try
        {
            var res = await _llm.CompleteAsync(Prompts.ToldRequest(rec, playerLine, parsed.Spoken, missing, askedName, Math.Min(cfg.MaxGenerationTokens, 2000)), extra.Token).ConfigureAwait(false);
            if (!res.Ok || LlmClient.ExtractJsonObject(res.Text) is not { } o) return;
            var found = ReadTold(o["told"]).Where(t => ToldCheck.Supported(t.topic, t.said, parsed.Spoken)).ToList();
            parsed.Told.AddRange(found);
            string name = o["name"] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n.Trim().Trim('"', '\'', '.') : "";
            if (askedName && name.Length is > 0 and < 40 && parsed.Spoken.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                parsed.IntroducedAs = name;
                parsed.GaveName = true;
            }
            if (found.Count > 0 || parsed.IntroducedAs != null)
                _log($"Noted what {rec.Persona.Name} told them: {string.Join("; ", found.Select(f => f.topic + " = " + f.said))}{(parsed.IntroducedAs != null && askedName ? " (name: " + parsed.IntroducedAs + ")" : "")}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { _log("Noting what they told you took too long; skipped."); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { _log("Noting what they told you failed: " + e.Message); }
    }

    internal static List<(string topic, string said)> ReadTold(JsonNode? node)
    {
        var list = new List<(string, string)>();
        if (node is not JsonArray arr) return list;
        foreach (var t in arr)
        {
            if (t is not JsonObject) continue; // a stray string in the list: skipped, the rest kept (ROBUST-2)
            string topic = t["topic"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : "";
            string said = t["said"] is JsonValue sv && sv.TryGetValue<string>(out var ss) ? ss.Trim() : "";
            if (said.Length is > 0 and < 240) list.Add((ToldTopics.Normalize(topic), said));
        }
        return list;
    }

    /// <summary>
    /// Records what they told the player about themselves (shown in the "what you know" panel and kept consistent in
    /// their own story). Single-answer topics (age, job...) keep their first answer: CF-20 (a player: "I can convince someone
    /// they work somewhere else, have a different romance partner, are a different orientation"; the author: "Firm, only
    /// real events"), a different later answer is only words, unless something real changed it (<see cref="RealChange"/>).
    /// After a meeting, the diary's sweep fills gaps without overriding what was caught live.
    /// </summary>
    internal static void ApplyTold(NpcRecord rec, IEnumerable<(string topic, string said)> told, int day, bool afterMeeting)
    {
        var st = rec.State;
        foreach (var (topic, raw) in told)
        {
            string said = ReplyParser.Quotable(raw, 240);
            if (topic.Length == 0 || said.Length == 0) continue;
            if (ToldTidy.Passing(topic, said)) continue; // CF-25: about the moment (an order, the food being fine), not about them
            bool single = ToldTopics.Single.Contains(topic);
            if (afterMeeting && single && st.Told.Any(t => t.Topic == topic && t.GameDay == day)) continue;
            // CF-20: firm. Their first answer on a single topic stands; a different one later counts only after a real change.
            if (single && st.Told.FirstOrDefault(t => t.Topic == topic) is { } had && !string.Equals(had.Said, said, StringComparison.OrdinalIgnoreCase)
                && !RealChange(rec, topic, had, said)) continue;
            // CF-16: a topic that builds up keeps each idea once, however it was worded this time (in a chat as after it).
            if (!single && st.Told.Any(t => t.Topic == topic
                    && (t.Said.Contains(said, StringComparison.OrdinalIgnoreCase) || said.Contains(t.Said, StringComparison.OrdinalIgnoreCase) || ToldTidy.SameIdea(t.Said, said)))) continue;
            if (single) st.Told.RemoveAll(t => t.Topic == topic);
            if (!st.Told.Any(t => t.Topic == topic && string.Equals(t.Said, said, StringComparison.OrdinalIgnoreCase)))
                st.Told.Add(new ToldItem { Topic = topic, Said = said, GameDay = day });
            string fact = $"{topic}: {said}"; // what they've said stays consistent too
            if (single) rec.Persona.EstablishedFacts.RemoveAll(x => ToldTidy.Split(x).topic == topic); // only the latest counts: no stale ones beside it
            if (!rec.Persona.EstablishedFacts.Any(x => string.Equals(x, fact, StringComparison.OrdinalIgnoreCase)
                    || (ToldTidy.Split(x) is var (ft, fs) && ft == topic && ToldTidy.SameIdea(fs, said))))
                rec.Persona.EstablishedFacts.Add(fact);
            // Telling you they're trans / non-binary is the identity reveal, however the model reported it.
            if (topic is "gender" or "pronouns" && Population.IsTransOrNonBinary(rec.Persona.GenderIdentity)
                && System.Text.RegularExpressions.Regex.IsMatch(said, @"(?i)\btrans|non[-\s]?binary|enby|genderfluid|agender|they/them", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                st.IdentityShared = true;
        }
        if (st.Told.Count > 120) st.Told.RemoveRange(0, st.Told.Count - 120);
        if (rec.Persona.EstablishedFacts.Count > 80) rec.Persona.EstablishedFacts.RemoveRange(0, rec.Persona.EstablishedFacts.Count - 80);
    }

    /// <summary>
    /// CF-20: what really happened, so a single topic's answer may move: a romance with the player since that answer
    /// (their relationship, and where they live once you live together), that romance ending since (their relationship,
    /// and home), or someone trans / non-binary telling you who they are.
    /// </summary>
    internal static bool RealChange(NpcRecord rec, string topic, ToldItem had, string said)
    {
        var st = rec.State;
        if (topic is "relationship" or "home" && st.Romance is Romance.Dating or Romance.Living && st.RomanceSinceDay >= had.GameDay
            && (topic == "relationship" || st.Romance == Romance.Living)) return true;
        if (topic is "relationship" or "home" && st.Romance.Length == 0 && st.RomanceEndedDay >= 0 && st.RomanceEndedDay >= had.GameDay) return true;
        return topic is "gender" or "pronouns" && Population.IsTransOrNonBinary(rec.Persona.GenderIdentity)
            && System.Text.RegularExpressions.Regex.IsMatch(said, @"(?i)\btrans|non[-\s]?binary|enby|genderfluid|agender|they/them", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static ParsedReply ParseReply(string text, NpcRecord rec, BrainSettings cfg) =>
        (cfg.StructuredReplies ? ReplyParser.ParseStructured(text, rec.Persona.Name, rec.Persona.Nickname) : null)
        ?? ReplyParser.Parse(text, rec.Persona.Name, rec.Persona.Nickname);

    /// <summary>
    /// A goodbye that doesn't ask anything. Asks the model once more; if it still won't, the trailing question is cut
    /// ("Fine. I'll remember that. So what are we doing?" → "Fine. I'll remember that.").
    /// </summary>
    private async Task<string> PartingLineAsync(ChatSession s, BrainSettings cfg, string notes, string questionLine, CancellationToken ct)
    {
        var rec = s.Record;
        string nudge = $"[You're leaving right now and won't stay for an answer. Say the parting line {Prompts.FirstName(rec.Persona.Name)} would actually say on the way out — " +
                       "it must not ask them anything or end with a question. Set leaving to true.]";
        LlmResult res;
        using (var extra = ExtraLimit(ct, cfg))
        {
            try { res = await _llm.CompleteAsync(BuildChat(s, cfg, (notes.Length > 0 ? notes + "\n" : "") + nudge), extra.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return DropTrailingQuestion(questionLine); }
        }
        if (res.Ok)
        {
            var p = ParseReply(res.Text, rec, cfg);
            if (p.Spoken.Trim().Length > 0 && !EndsWithQuestion(p.Spoken) && !ReplyParser.LooksOutOfCharacter(p.Spoken, rec.Visual.IsAndroid))
                return p.Spoken;
        }
        return DropTrailingQuestion(questionLine);
    }

    internal static string DropTrailingQuestion(string text)
    {
        var parts = System.Text.RegularExpressions.Regex.Split((text ?? "").Trim(), @"(?<=[.!?…])\s+").Where(x => x.Length > 0).ToList();
        while (parts.Count > 1 && EndsWithQuestion(parts[^1])) parts.RemoveAt(parts.Count - 1);
        return string.Join(" ", parts);
    }

    private static bool EndsWithQuestion(string text)
    {
        string t = (text ?? "").TrimEnd(' ', '"', '\'', ')', '…', '.');
        return t.EndsWith("?");
    }

    /// <summary>
    /// CF-14: a coworker the mod has a record for is named as it knows them (the name their own chat uses); anyone else,
    /// and any minor, is counted but not named (the game's name never reaches the AI). Once per chat (the chat's snapshot).
    /// </summary>
    private void SettleCoworkerNames(WorkReading? job)
    {
        if (job == null || job.CoworkerNamesSettled) return;
        job.CoworkerNamesSettled = true;
        for (int i = job.Coworkers.Count - 1; i >= 0; i--)
        {
            var (id, _) = job.Coworkers[i];
            try
            {
                // Named only from a record (someone the mod knows is an adult), never a minor; counted either way.
                var other = id.Length > 0 && !KnownMinors.Contains(id) ? _store.Glance(id) : null;
                bool nameable = other != null && !IsKnownMinor(other) && other.Persona.Name.Length > 0;
                job.Coworkers[i] = (id, nameable ? other!.Persona.Name : "");
            }
            catch { job.Coworkers[i] = (id, ""); } // review XR-3: on any doubt, counted, not named
        }
    }

    private LlmRequest BuildChat(ChatSession s, BrainSettings cfg, string? nudge)
    {
        var rec = s.Record;
        var req = new LlmRequest { Purpose = "chat", MaxTokens = cfg.MaxReplyTokens, SchemaName = "reply", PlainTextOk = true }; // plain words with [[tags]] are read too
        req.System.Add(Prompts.IdentityBlock(rec, cfg.StructuredReplies, cfg.Friendliness, s.Snapshot.PlayerGender));
        // FB-50: a story character talks like their story, and knows the story conversations you've had (only what you've seen).
        if (Story is { } book && rec.Visual.Named)
        {
            if (Features.StoryVoice && Prompts.StoryVoiceBlock(book.Sheet(rec.Id), book.OwnLines(rec.Id)) is { Length: > 0 } voice) req.System.Add(voice);
            // Your last chat line with them before this meeting: a story scene after it is "just now" (FB-53).
            var before = rec.History.Take(Math.Min(s.StartIndex, rec.History.Count)).LastOrDefault(l => l.Role is "player" or "npc");
            if (Features.StoryMemory && Prompts.StoryBlock(book.Scenes(rec.Id), s.Snapshot.PlayerName, lastChatUtc: before?.Utc) is { Length: > 0 } story) req.System.Add(story);
        }
        if (CustomInstructions.Block() is { Length: > 0 } own) req.System.Add(own);
        SettleCoworkerNames(s.Snapshot.Job);
        req.System.Add(Prompts.SituationBlock(rec, s.Snapshot, s.MoodTrail));
        if (cfg.NpcsCanLeave) req.System.Add(Patience.Block(rec, s.Snapshot, s));
        if (cfg.StructuredReplies) req.JsonSchema = Prompts.ReplySchema();
        if (nudge != null) req.System.Add(nudge);
        if (Prompts.SpeakIn() is { Length: > 0 } speakIn) req.System.Add(speakIn); // ReplyLanguage
        req.System.Add(Prompts.SafetyLast); // the last word, after everything the player can edit (their instructions, the fields, their name)
        foreach (var l in rec.History.Where(l => !l.Folded).TakeLast(Math.Max(6, cfg.RecentMessages)))
        {
            string role = l.Role == "npc" ? "assistant" : "user";
            req.Messages.Add(new LlmMessage(role, l.Text));
        }
        return req;
    }

    private static ChatLine AddLine(NpcRecord rec, string role, string text, GameSnapshot snap)
    {
        var line = new ChatLine { Role = role, Text = text, GameDay = snap.GameDay, Clock = snap.Clock, Utc = DateTime.UtcNow };
        rec.History.Add(line);
        return line;
    }

    // ------------------------------------------------------------------ meeting end

    /// <summary>
    /// The conversation is over: its record is saved for the last time (with whatever happened to them meanwhile) inside
    /// their queue, and the meeting lets go of it. The diary and the summary of old lines are written afterwards, outside
    /// the queue, and their results are applied like any other change (<see cref="Mutate"/>). A meeting a reset let go of
    /// leaves nothing behind.
    /// </summary>
    public void End(ChatSession s)
    {
        if (s.Owner != null && s.Owner != this) { s.Owner.End(s); return; }
        if (s.Ended) return;
        s.Ended = true;
        if (s.Released) return; // a reset let go of it: nothing of this meeting is kept, no diary either
        var rec = s.Record;
        string id = rec.Id;
        var meetingLines = new List<ChatLine>();
        var unfolded = new List<ChatLine>();
        bool diary = false, fold = false;
        var cfg = new BrainSettings();
        var kept = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // Remember when this conversation ended (the clock is paused while you talk, so the last snapshot is close enough).
            rec.State.LastTalkDay = s.Snapshot.GameDay;
            rec.State.LastTalkMinute = s.Snapshot.Hour * 60 + s.Snapshot.Minute;
            cfg = _settings();
            meetingLines = rec.History.Skip(Math.Min(s.StartIndex, rec.History.Count)).Where(l => l.Role != "scene").ToList();
            TrimStored(rec, cfg);
            // A diary entry only once the player actually said something (the automatic "Hey," alone isn't a conversation).
            diary = cfg.MeetingSummaries && meetingLines.Count(l => l.Role == "player" && l != s.OpenerLine) >= 1 && meetingLines.Count >= 2;
            unfolded = rec.History.Where(l => !l.Folded).ToList();
            fold = unfolded.Count > cfg.SummarizeAfter;
            if (diary || fold)
            {
                // Registered before the meeting lets go, so a new meeting with them (which waits for this) can't miss it.
                var snap = s.Snapshot;
                long epoch = s.Epoch; // from when the meeting began: a reset since then means none of this is written
                TrackAfter(id, async () =>
                {
                    if (!await kept.Task.ConfigureAwait(false)) return; // not kept (a reset, or another meeting took over): no diary
                    if (diary) await WriteDiaryAsync(rec, meetingLines, snap, cfg, epoch).ConfigureAwait(false);
                    if (fold) await SummarizeAsync(rec, unfolded, cfg, epoch).ConfigureAwait(false);
                });
            }
        }
        finally
        {
            // Whatever went wrong above, the meeting lets go of the record.
            Release(s, ReleaseMode.End).ContinueWith(t => kept.TrySetResult(t.Status == TaskStatus.RanToCompletion && t.Result), TaskScheduler.Default);
        }
    }

    /// <summary>
    /// A reply the player never got to see (its voice timed out) is taken back out of their memory, with the player's
    /// line that prompted it when <paramref name="withPlayerLine"/> (those words go back in the box to send again).
    /// </summary>
    public void Retract(ChatSession s, NpcReply reply, bool withPlayerLine)
    {
        if (s.Owner != null && s.Owner != this) { s.Owner.Retract(s, reply, withPlayerLine); return; }
        var h = s.Record.History;
        int i = h.FindLastIndex(l => l.Role == "npc" && l.Text == reply.Spoken);
        if (i < 0) return;
        h.RemoveAt(i);
        if (withPlayerLine && i - 1 >= 0 && h[i - 1].Role == "player")
        {
            h.RemoveAt(i - 1);
            s.Exchanges = Math.Max(0, s.Exchanges - 1);
        }
        SaveSession(s);
    }

    /// <summary>
    /// Waits until nothing is going on for this person: their queue, and the work after a meeting (tests, shutdown,
    /// resets). It never fails (it's only a wait), and it must not be called from inside their own queue: that would wait
    /// for itself.
    /// </summary>
    public async Task Settled(string id)
    {
        if (_inQueue.Value == id) throw new InvalidOperationException($"Settled({id}) was called from inside {id}'s own queue and would wait for itself.");
        for (int round = 0; round < 1000; round++)
        {
            var waits = new List<Task>();
            if (_maintenance.TryGetValue(id, out var q) && !q.IsCompleted) waits.Add(q);
            lock (_afterLock) { if (_after.TryGetValue(id, out var a) && !a.IsCompleted) waits.Add(a); }
            if (waits.Count == 0) return;
            try { await Task.WhenAll(waits).ConfigureAwait(false); } catch { /* a failed step is someone else's to report */ }
        }
    }

    /// <summary><see cref="Settled"/> for everyone.</summary>
    public async Task SettledAll()
    {
        if (_inQueue.Value != null) throw new InvalidOperationException("SettledAll was called from inside a person's queue and would wait for itself.");
        for (int round = 0; round < 1000; round++)
        {
            var waits = _maintenance.Values.Where(t => !t.IsCompleted).ToList();
            lock (_afterLock) waits.AddRange(_after.Values.Where(t => !t.IsCompleted));
            if (waits.Count == 0) return;
            try { await Task.WhenAll(waits).ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>Waits (outside the queue) for the work after their last meeting: its diary is in their record before the next one begins.</summary>
    private async Task AfterSettled(string id, CancellationToken ct)
    {
        Task? after;
        lock (_afterLock) _after.TryGetValue(id, out after);
        if (after == null || after.IsCompleted) return;
        try { await after.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* it failed: reported where it ran */ }
    }

    private static void TrimStored(NpcRecord rec, BrainSettings cfg)
    {
        if (cfg.MaxStoredLines <= 0) return; // keep everything
        int max = Math.Max(50, cfg.MaxStoredLines);
        if (rec.History.Count <= max) return;
        // drop the oldest already-summarised lines first; never the live context
        int excess = rec.History.Count - max;
        for (int i = 0; i < rec.History.Count && excess > 0;)
        {
            if (rec.History[i].Folded) { rec.History.RemoveAt(i); excess--; }
            else i++;
        }
    }

    /// <summary>
    /// The diary entry for a meeting that just ended, written from what was said (<paramref name="rec"/> is the ended
    /// meeting's record: nobody changes it any more). The entry, and anything they told you that the meeting missed, are
    /// applied to their record as a change (<see cref="Mutate"/>), unless a reset happened since <paramref name="epoch"/>.
    /// </summary>
    private async Task WriteDiaryAsync(NpcRecord rec, List<ChatLine> lines, GameSnapshot snap, BrainSettings cfg, long epoch)
    {
        string id = rec.Id, name = rec.Persona.Name;
        if (IsKnownMinor(rec)) return; // found to be under 18 during the meeting: nothing more about them goes to any AI
        try
        {
            bool gossip = cfg.Gossip && World != null;
            var res = await _llm.CompleteAsync(Prompts.MeetingRequest(rec, lines, Math.Min(cfg.MaxGenerationTokens, 2000), gossip), CancellationToken.None).ConfigureAwait(false);
            if (!res.Ok || res.Text.Trim().Length < 10) { _log($"Diary entry for {name} skipped: {res.Error}"); return; }
            if (EpochFor(id) != epoch) return; // forgotten meanwhile (/resetnpc, /resetall)
            string diary = res.Text.Trim();
            var told = new List<(string topic, string said)>();
            if (LlmClient.ExtractJsonObject(res.Text) is { } o)
            {
                diary = o["diary"]?.ToString().Trim() ?? diary;
                string rumor = o["gossip"]?.ToString().Trim() ?? "";
                if (gossip && rumor.Length > 8 && EpochFor(id) == epoch)
                {
                    World!.AddRumor(rumor, snap.Location, lines[0].GameDay, id);
                    _log($"{name} will tell people: {rumor}");
                }
                // Anything about themselves they mentioned without being asked (or that slipped past live): into the panel.
                string theySaid = string.Join(" ", lines.Where(l => l.Role == "npc").Select(l => l.Text));
                told = ReadTold(o["told"]).Where(t => ToldCheck.Supported(t.topic, t.said, theySaid)).ToList();
            }
            var meeting = new Meeting { GameDay = lines[0].GameDay, Clock = lines[0].Clock, Location = Prompts.PlaceWords(snap) /* CF-18: the venue too */, Summary = ReplyParser.Quotable(diary, 2000) };
            int toldDay = lines[^1].GameDay;
            await Mutate(id, r =>
            {
                if (EpochFor(id) != epoch) return false;
                int before = r.State.Told.Count;
                ApplyTold(r, told, toldDay, afterMeeting: true);
                if (r.State.Told.Count > before) _log($"{name}: {r.State.Told.Count - before} more thing(s) they told you noted after the conversation.");
                r.Meetings.Add(meeting);
                return true;
            }).ConfigureAwait(false);
        }
        catch (Exception e) { _log($"Diary entry for {name} failed: {e.Message}"); }
    }

    /// <summary>
    /// Folds their oldest lines into a memory summary once there are too many (they stay for scrolling back, but are no
    /// longer sent to the model). Worked out from <paramref name="unfolded"/> (the ended meeting's lines), applied as a
    /// change that folds exactly those lines. No trimming here: a meeting going on now counts lines from its start.
    /// </summary>
    private async Task SummarizeAsync(NpcRecord rec, List<ChatLine> unfolded, BrainSettings cfg, long epoch)
    {
        string id = rec.Id, name = rec.Persona.Name;
        if (IsKnownMinor(rec)) return; // found to be under 18 during the meeting: nothing more about them goes to any AI
        try
        {
            int keep = Math.Max(10, Math.Min(cfg.RecentMessages, cfg.SummarizeAfter - 10));
            int fold = unfolded.Count - keep;
            if (fold <= 0) return;
            var old = unfolded.Take(fold).ToList();
            var res = await _llm.CompleteAsync(Prompts.MemoryRequest(rec, old, cfg.MaxGenerationTokens), CancellationToken.None).ConfigureAwait(false);
            if (!res.Ok || res.Text.Trim().Length < 20) { _log($"Memory summary for {name} skipped: {res.Error}"); return; }
            string summary = ReplyParser.Quotable(res.Text, 8000);
            var keys = old.Select(l => (l.Utc, l.Role, l.Text)).ToHashSet();
            await Mutate(id, r =>
            {
                if (EpochFor(id) != epoch) return false;
                int folded = 0;
                foreach (var l in r.History)
                    if (!l.Folded && keys.Contains((l.Utc, l.Role, l.Text))) { l.Folded = true; folded++; }
                r.MemorySummary = summary;
                _log($"Folded {folded} old lines into {name}'s memory.");
                return true;
            }).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log($"Memory summary for {name} failed: {e.Message}");
        }
    }
}
