using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisAIChat.Npc;

/// <summary>Everything the mod remembers about one NPC. Saved as one JSON file.</summary>
public sealed class NpcRecord
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime LastTalkedUtc { get; set; }
    public int CreatedGameDay { get; set; }
    public int LastGameDay { get; set; }
    public int LastLifeUpdateGameDay { get; set; }
    public DateTime LastLifeUpdateUtc { get; set; }
    public NpcVisual Visual { get; set; } = new();
    public Persona Persona { get; set; } = new();
    public NpcState State { get; set; } = new();
    public List<LifeEvent> LifeLog { get; set; } = new();
    /// <summary>First-person digest of older conversations, written by the AI.</summary>
    public string MemorySummary { get; set; } = "";
    /// <summary>One diary entry per meeting, in their own voice, kept forever.</summary>
    public List<Meeting> Meetings { get; set; } = new();
    public List<ChatLine> History { get; set; } = new();
    /// <summary>
    /// Conversations they've had with other residents (face to face or on the phone) and things they've said to
    /// themselves, as actually said, newest last (the last <see cref="MaxTalks"/>).
    /// </summary>
    public List<Talk> Talks { get; set; } = new();
    /// <summary>What the game showed them doing lately (FB-36): meals out, shifts at work. Small, merged: <see cref="Npc.Doings"/>.</summary>
    public List<Doing> Doings { get; set; } = new();
    /// <summary>FB-58: for the player's own staff, what was last seen of their job, and what happened at work they remember.</summary>
    public WorkLifeState? Work { get; set; }
    public List<WorkEvent> WorkEvents { get; set; } = new();

    /// <summary>Read with <see cref="NpcStore.Glance"/>: History and Meetings were left out, so it's never saved. Not in the file.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    internal bool Glanced { get; set; }

    public const int MaxTalks = 30;
    /// <summary>Most overheard snippets kept (oldest go first), apart from <see cref="MaxTalks"/>.</summary>
    public const int MaxOverheard = 5;
    /// <summary>Most order exchanges kept from food places (FB-34), apart from <see cref="MaxTalks"/>: oldest go first.</summary>
    public const int MaxService = 3;
    /// <summary>FB-71: most Nightline visits kept (what they saw, liked, said there), apart from <see cref="MaxTalks"/>.</summary>
    public const int MaxNightline = 6;

    /// <summary>
    /// Defaults back where a file (an older version's, or one edited by hand) left null: the nested parts and the lists,
    /// and no null items in them. Text and lists of text are read without nulls already (<see cref="TolerantJson"/>).
    /// </summary>
    public void Repair()
    {
        Visual ??= new();
        Persona ??= new();
        Persona.Repair();
        State ??= new();
        State.Told ??= new();
        State.Told.RemoveAll(x => x == null);
        State.Favours ??= new();
        State.Favours.RemoveAll(x => x == null);
        State.Meals ??= new();
        State.Meals.RemoveAll(x => x == null);
        foreach (var m in State.Meals) { m.Dish ??= ""; m.Place ??= ""; }
        State.Hurts ??= new();
        LifeLog ??= new();
        LifeLog.RemoveAll(x => x == null);
        Meetings ??= new();
        Meetings.RemoveAll(x => x == null);
        History ??= new();
        History.RemoveAll(x => x == null);
        Talks ??= new();
        Talks.RemoveAll(x => x == null);
        Doings ??= new();
        Doings.RemoveAll(x => x == null);
    }

    public void AddTalk(Talk talk)
    {
        // What was said and its summary go back into later prompts: quoted safely (no brackets, no line breaks).
        talk.Summary = Ai.ReplyParser.Quotable(talk.Summary, 600);
        talk.Lines = (talk.Lines ?? new List<string>()).Select(l => Ai.ReplyParser.Quotable(l, 600)).ToList();
        Talks.Add(talk);
        // Overheard snippets have their own small cap, so they never push out real conversations, calls and thoughts (FB-05).
        while (Talks.Count(t => t.Kind == Talk.Overheard) > MaxOverheard) Talks.Remove(Talks.First(t => t.Kind == Talk.Overheard));
        // Order talk at food places likewise (FB-34): a waiter's day of orders never pushes out what they really talked about.
        while (Talks.Count(t => t.Kind == Talk.Service) > MaxService) Talks.Remove(Talks.First(t => t.Kind == Talk.Service));
        // Nightline visits too (FB-71): scrolling a feed never pushes out a real conversation.
        while (Talks.Count(t => t.Kind == Talk.Nightline) > MaxNightline) Talks.Remove(Talks.First(t => t.Kind == Talk.Nightline));
        while (Talks.Count(t => t.Kind is not (Talk.Overheard or Talk.Service or Talk.Nightline)) > MaxTalks) Talks.Remove(Talks.First(t => t.Kind is not (Talk.Overheard or Talk.Service or Talk.Nightline)));
    }
}

/// <summary>Something they said without the player there: a conversation with others, a phone call, or a thought out loud.</summary>
public sealed class Talk
{
    public const string InPerson = "in person", Phone = "phone", Thought = "thought";
    /// <summary>Something they overheard others saying near them (FB-05): <see cref="With"/> stays empty, who said it is in the lines.</summary>
    public const string Overheard = "overheard";
    /// <summary>Staff taking a customer's order at a food place (FB-34): short, kept apart from conversations.</summary>
    public const string Service = "service";
    /// <summary>FB-71: a visit to Nightline (what they posted, saw, liked or disliked and why, said there).</summary>
    public const string Nightline = "Nightline";
    public int GameDay { get; set; }
    public string Clock { get; set; } = "";
    public string Where { get; set; } = "";
    /// <summary><see cref="InPerson"/>, <see cref="Phone"/> or <see cref="Thought"/>.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Who with ("" for a thought).</summary>
    public string With { get; set; } = "";
    /// <summary>"Name: words", in order.</summary>
    public List<string> Lines { get; set; } = new();
    /// <summary>What it was about, in a sentence (for a thought: what was on their mind).</summary>
    public string Summary { get; set; } = "";
}

/// <summary>What the game shows about this NPC; the persona must agree with it.</summary>
public sealed class NpcVisual
{
    public string Body { get; set; } = "";          // "woman", "man", "android"
    public bool IsAndroid { get; set; }
    public string Demographic { get; set; } = "";
    public string HomeDistrict { get; set; } = "";
    public string Workplace { get; set; } = "";
    public string GameName { get; set; } = "";
    public string Kind { get; set; } = "";          // "simulated" or "background extra"
    public bool DressedForCold { get; set; }
    /// <summary>A story character with a real in-game name (always used as-is).</summary>
    public bool Named { get; set; }
    /// <summary>Works for the player (hired staff).</summary>
    public bool Employee { get; set; }
    /// <summary>The game's own description of a named character.</summary>
    public string Description { get; set; } = "";
    /// <summary>Under 18 (a child in the story, or rolled below 18 when MinAge allows it): never chatted with or greeted.</summary>
    public bool Minor { get; set; }
    /// <summary>Which character model they are (their portrait's name, else the model's): looked up in <see cref="Looks"/>.</summary>
    public string Model { get; set; } = "";
    /// <summary>The AI has seen a picture of them (LookAtNewPeople), so how they're described matches how they look.</summary>
    public bool LookedAt { get; set; }
    /// <summary>
    /// FB-57: the game shows their name over their head (a shop keeper, someone at work): the player can read it, so they're
    /// known by it, never "Stranger". Unlike <see cref="Named"/>, nothing else about them is the game's: they're made like
    /// any resident, with that name.
    /// </summary>
    public bool NameShown { get; set; }
    /// <summary>FB-57: the shop or stall they run (the game's own), and what it sells ("" if they don't run one).</summary>
    public string ShopName { get; set; } = "";
    public string Trade { get; set; } = "";

    /// <summary>The player knows them by name: a story character, staff, or someone whose name the game shows (FB-57).</summary>
    public bool KnownByName() => Named || NameShown;
}

public sealed class Persona
{
    public string Name { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string Age { get; set; } = "";
    public string Pronouns { get; set; } = "";
    public string Occupation { get; set; } = "";
    public string Workplace { get; set; } = "";
    public string LivesIn { get; set; } = "";
    public string Appearance { get; set; } = "";
    public string Background { get; set; } = "";
    public List<string> Personality { get; set; } = new();
    public string Temperament { get; set; } = "";
    public SpeechStyle Speech { get; set; } = new();
    public List<string> Values { get; set; } = new();
    public List<string> Fears { get; set; } = new();
    public string Secret { get; set; } = "";
    /// <summary>FB-57: the name they had before the game's own replaced it ("" if never renamed).</summary>
    public string FormerName { get; set; } = "";
    public List<string> Likes { get; set; } = new();
    public List<string> Dislikes { get; set; } = new();
    public List<string> Hobbies { get; set; } = new();
    public string ShortTermGoal { get; set; } = "";
    public string LongTermGoal { get; set; } = "";
    public string CurrentWorry { get; set; } = "";
    public List<string> People { get; set; } = new();
    public string OpinionOfCorps { get; set; } = "";
    public string OpinionOfAndroids { get; set; } = "";
    public string GenderIdentity { get; set; } = "";
    public bool PronounsFlexible { get; set; }
    public string Orientation { get; set; } = "";
    public string RelationshipStatus { get; set; } = "";
    public string Faith { get; set; } = "";
    public string Politics { get; set; } = "";
    public string Humor { get; set; } = "";
    public string Honesty { get; set; } = "";
    public string ConflictStyle { get; set; } = "";
    public string Education { get; set; } = "";
    public string Health { get; set; } = "";
    public string Voice { get; set; } = "";
    public string Style { get; set; } = "";
    public string DistinctiveMarks { get; set; } = "";
    public List<string> Habits { get; set; } = new();
    /// <summary>How their memory works (sharp, patchy, holds grudges...).</summary>
    public string Memory { get; set; } = "";
    /// <summary>Details they made up about themselves in conversation; kept so they never contradict them.</summary>
    public List<string> EstablishedFacts { get; set; } = new();

    /// <summary>Their speech style back where it was null (the rest is read without nulls, see <see cref="TolerantJson"/>).</summary>
    public void Repair() => Speech ??= new();
}

public sealed class SpeechStyle
{
    /// <summary>0 = none (proper), 1 = light, 2 = moderate, 3 = heavy street slang.</summary>
    public int SlangLevel { get; set; }
    public string Formality { get; set; } = "";
    public string Verbosity { get; set; } = "";
    public string Swearing { get; set; } = "";
    public string Rhythm { get; set; } = "";
    public List<string> Quirks { get; set; } = new();
    public List<string> Catchphrases { get; set; } = new();
    public List<string> FavouriteWords { get; set; } = new();
}

public sealed class NpcState
{
    public string Mood { get; set; } = "";
    public string MoodIntensity { get; set; } = "";
    public string MoodReason { get; set; } = "";
    /// <summary>
    /// -100 hates you .. +100 adores you: the older measure, kept in step with <see cref="Relationship"/> (which the bar
    /// shows). Set on its own (an older file, a test), it moves an explicit Relationship that disagrees with it.
    /// </summary>
    public int Affinity
    {
        get => _affinity;
        set
        {
            _affinity = value;
            if (_relationship >= 0 && Relationships.ToAffinity(_relationship) != value) _relationship = Relationships.FromAffinity(value);
        }
    }
    private int _affinity;

    /// <summary>
    /// How they feel about the player on the 0–100% bar (50% neutral). An older file has only Affinity: it's worked out
    /// from that until it changes. A file is read in its own key order; the mod writes affinity first, so this wins for
    /// any file it wrote. A blank or broken value in a file, or a negative or non-finite one, counts as not set.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(RelationshipJson))]
    public double Relationship
    {
        get => _relationship >= 0 ? _relationship : Relationships.FromAffinity(_affinity);
        set
        {
            if (!double.IsFinite(value) || value < 0) { _relationship = -1; return; }
            _relationship = Math.Round(Math.Clamp(value, 0, 100), Relationships.Decimals);
            _affinity = Relationships.ToAffinity(_relationship);
        }
    }
    private double _relationship = -1;

    /// <summary>Moves the relationship by <paramref name="points"/> on the bar (and Affinity with it).</summary>
    public void Shift(double points) => Relationship = Relationships.Moved(Relationship, points);

    public string Familiarity { get; set; } = "stranger";
    public string PlayerName { get; set; } = "";
    public bool NameRevealed { get; set; }
    /// <summary>The name they actually gave the player (a nickname, or even an alias).</summary>
    public string KnownAs { get; set; } = "";
    /// <summary>They've told the player they're trans / non-binary (until then the UI shows what a passer-by sees).</summary>
    public bool IdentityShared { get; set; }
    /// <summary>What they've told the player about themselves, topic by topic (true or not).</summary>
    public List<ToldItem> Told { get; set; } = new();
    /// <summary>Things they've asked the player for (a dish from the menu, delivered or picked up).</summary>
    public List<Favour> Favours { get; set; } = new();
    public int TimesMet { get; set; }
    /// <summary>Game day of the meeting before the current one.</summary>
    public int PreviousMeetingDay { get; set; }
    /// <summary>When your last conversation ended, whoever ended it (game day and minute of the day; day -1 = never/unknown).</summary>
    public int LastTalkDay { get; set; } = -1;
    public int LastTalkMinute { get; set; }
    /// <summary>Game minutes between the end of the last conversation and the start of this one (-1 = unknown).</summary>
    public int MinutesSinceLastTalk { get; set; } = -1;
    /// <summary>The last time they walked away from the player: why, and when (game day + minute of day).</summary>
    public string LastLeftReason { get; set; } = "";
    public int LastLeftGameDay { get; set; } = -1;
    public int LastLeftMinute { get; set; }
    /// <summary>Facts the NPC learned about the player.</summary>
    public List<string> PlayerFacts { get; set; } = new();
    /// <summary>
    /// They know the player runs a place (and what it serves): the player told them, they work there, the story says
    /// they've met, or they've eaten there often. Until then the player is just a stranger to them.
    /// </summary>
    public bool KnowsYourPlace { get; set; }
    /// <summary>The game says they know the player (met in the story, or they work for the player).</summary>
    public bool MetInStory { get; set; }
    /// <summary>FB-63: you and them: "" (nothing), "dating" (together: you confirmed it), "living" (moved in with you).</summary>
    public string Romance { get; set; } = "";
    public int RomanceSinceDay { get; set; } = -1;
    /// <summary>CF-20: the game day your romance with them last ended (-1 never): their relationship answer may move after it.</summary>
    public int RomanceEndedDay { get; set; } = -1;
    public int DatesHad { get; set; }
    public int LastDateDay { get; set; } = -1;
    public int DatesToday { get; set; }
    /// <summary>FB-63: what they found out you did (seen, or heard through gossip), in their words; the newest few kept.</summary>
    public List<string> Hurts { get; set; } = new();
    /// <summary>The inner-life round: dishes the game showed them eating (Tastes: favourites, and what they're sick of).</summary>
    public List<MealSeen> Meals { get; set; } = new();
}

public sealed class LifeEvent
{
    public int GameDay { get; set; }
    public DateTime Utc { get; set; }
    public string Text { get; set; } = "";
}

public sealed class Favour
{
    public string Dish { get; set; } = "";
    /// <summary>"delivery" (bring it to me) or "visit" (I'll come by your place).</summary>
    public string How { get; set; } = "";
    public string Status { get; set; } = "open"; // open, done, dropped
    public int GameDay { get; set; }
    public int Price { get; set; }
}

public sealed class Meeting
{
    public int GameDay { get; set; }
    public string Clock { get; set; } = "";
    public string Location { get; set; } = "";
    public string Summary { get; set; } = "";
}

public sealed class ToldItem
{
    public string Topic { get; set; } = "";
    public string Said { get; set; } = "";
    public int GameDay { get; set; }
}

public static class ToldTopics
{
    /// <summary>Topics where only the latest answer counts; the rest accumulate.</summary>
    public static readonly string[] Single = { "age", "gender", "pronouns", "orientation", "relationship", "job", "workplace", "home", "faith", "politics", "education" };
    public static readonly string[] Multi = { "family", "background", "health", "hobbies", "likes", "dislikes", "goals", "fears", "other" };
    public static readonly string[] All = Single.Concat(Multi).ToArray();

    public static string Normalize(string topic)
    {
        string t = (topic ?? "").Trim().ToLowerInvariant();
        if (All.Contains(t)) return t;
        return t switch
        {
            "occupation" or "work" => "job",
            "religion" or "spirituality" or "beliefs" => "faith",
            "sexuality" or "sexual orientation" => "orientation",
            "relationship status" or "partner" or "dating" => "relationship",
            "gender identity" => "gender",
            "lives" or "neighbourhood" or "neighborhood" or "district" => "home",
            "hobby" or "interests" or "interest" => "hobbies",
            "like" or "favourite" or "favorite" => "likes",
            "dislike" or "hates" => "dislikes",
            "goal" or "dream" or "dreams" or "plans" => "goals",
            "fear" or "worries" => "fears",
            "past" or "childhood" or "history" => "background",
            "school" => "education",
            "body" or "augments" or "implants" => "health",
            "siblings" or "parents" or "kids" or "children" => "family",
            _ => "other",
        };
    }
}

public sealed class ChatLine
{
    public string Role { get; set; } = "";   // "player", "npc", "scene"
    public string Text { get; set; } = "";
    public int GameDay { get; set; }
    public string Clock { get; set; } = "";
    /// <summary>Already folded into the memory summary: kept for the player to scroll back through, not sent to the model.</summary>
    public bool Folded { get; set; }
    public DateTime Utc { get; set; }
}

/// <summary>Local dice rolled before the AI invents a person, so NPCs don't converge on the same few archetypes.</summary>
public sealed class PersonaSeeds
{
    public string NameFlavour = "";
    public char NameInitial;
    public string AgeHint = "";
    public string Temperament = "";
    public string OccupationHint = "";
    public string Quirk = "";
    public string Obsession = "";
    public int SlangLevel;
    public string Verbosity = "";
    public string Formality = "";
    public string Swearing = "";
    public string LifeSituation = "";
    public int Age;
    public Population.Identity Identity = new();
    public string Relationship = "";
    public string Faith = "";
    public string Politics = "";
    public string Humor = "";
    public string Honesty = "";
    public string ConflictStyle = "";
    public string Education = "";
    public string Health = "";
    public string Memory = "";

    private static readonly string[] Memories =
    {
        "sharp — remembers details, dates and exact words", "good — remembers what matters", "average — the gist, not the details",
        "patchy — small talk slips away fast", "great with faces, terrible with names", "remembers every slight forever, forgets kindnesses",
        "remembers kindnesses, lets slights go", "photographic for things they care about, foggy for the rest",
    };

    private static readonly string[] Faiths =
    {
        "devout CORA believer, prays at the shrine", "was raised on CORA, rarely thinks about it", "lapsed — raised on CORA, gave it up",
        "follows the Binary Gods, a little embarrassed about it", "flatly doesn't believe in anything", "spiritual in their own way, tarot and charms",
        "keeps an old family faith from before the city", "thinks CORA is a corporate psy-op",
        // FB-02: most people don't think about it much; CORA stays possible, not the norm.
        "doesn't think about religion much", "not religious, but superstitious about small things", "goes to the shrine on holidays, mostly for the food",
        "agnostic; whatever gets you through the week", "not religious; believes in paying the rent on time",
    };
    private static readonly string[] PoliticalViews =
    {
        "corp-loyal: the system works if you work it", "hates the corps but has given up fighting them", "quietly anti-corp, signs petitions",
        "sympathises with the Oil Rig movement", "law-and-order, trusts the 'Sec", "apolitical — politics is for people with spare Lims",
        "conspiracy-minded about everything", "angry about the curfew and says so loudly",
    };
    private static readonly string[] Humors =
        { "dry", "dark and morbid", "sarcastic", "goofy", "self-deprecating", "deadpan", "barely any sense of humour", "loves bad puns", "teasing" };
    private static readonly string[] Honesties =
        { "honest to a fault", "mostly honest", "tells white lies to keep the peace", "embellishes every story", "lies easily when it suits them", "guarded: says little, lies rarely" };
    private static readonly string[] ConflictStyles =
        { "avoids conflict, changes the subject", "confrontational", "passive-aggressive", "diplomatic", "sulks and goes cold", "laughs it off", "gets loud, then gets over it" };
    private static readonly string[] Educations =
        { "left school early", "basic schooling", "trade apprenticeship", "some college, dropped out for money", "a degree they don't use", "self-taught from the Nets", "corp training programme" };
    private static readonly string[] Healths =
        { "healthy enough", "smog cough", "bad back from work", "OptimEyes implants", "replacement dermis on one arm", "a lung filter implant", "a cheap prosthetic leg",
          "VascoLink heart implant", "an ad implant they can't afford to remove", "early dermis rot they hide", "chronic insomnia", "a bad knee", "hearing aid implant", "anxiety they manage" };
    private static readonly string[] Relationships =
        { "single", "single", "single", "dating someone new", "in a long relationship", "married", "married", "divorced", "separated, it's messy", "widowed", "it's complicated" };

    private static readonly string[] NameFlavours =
    {
        "Japanese", "Korean", "Mandarin", "Cantonese", "Vietnamese", "Filipino", "Thai", "Indonesian", "Hindi", "Tamil",
        "Persian", "Arabic", "Turkish", "Yoruba", "Igbo", "Akan", "Amharic", "Swahili", "Zulu", "Brazilian Portuguese",
        "Latin American Spanish", "Castilian Spanish", "Polish", "Ukrainian", "Russian", "Czech", "Hungarian", "Greek",
        "Italian", "French", "German", "Dutch", "Swedish", "Norwegian", "Finnish", "Icelandic", "Irish", "Scottish",
        "Welsh", "Samoan", "Maori", "Inuit", "Romani", "Basque", "Armenian", "Georgian", "Hebrew", "Mongolian",
        "Haitian Creole", "Jamaican", "invented street-born", "mixed heritage (two cultures blended)",
        "old-fashioned and plain", "corporate-era made-up (a brand-like name parents thought was classy)",
    };

    private static readonly string[] Temperaments =
    {
        "warm and chatty", "guarded and terse", "cynical and dry", "relentlessly upbeat", "anxious and fidgety",
        "hot-headed", "dreamy and distracted", "pragmatic and blunt", "sarcastic", "shy but sweet", "flirtatious",
        "melancholic", "eccentric", "paranoid", "gentle and patient", "proud and touchy", "world-weary", "nosy and gossipy",
        "deadpan", "earnest and idealistic", "bitter about life", "smug know-it-all", "nervous over-sharer",
        "laid-back and unbothered", "competitive", "theatrical", "suspicious of strangers", "kind but exhausted",
        "mischievous", "stoic", "impatient, always in a hurry", "lonely and glad to talk", "philosophical", "scatterbrained",
    };

    private static readonly string[] Occupations =
    {
        "noodle-stall cook", "trawler deckhand", "fish-market gutter", "rig pipe fitter", "meat-module cleaner",
        "HOVA cab driver", "Cloudpunk courier", "Metamins delivery rider", "bartender", "club bouncer", "DJ",
        "street musician", "holo-ad designer", "data-entry drone at a corp office", "junior Corpster", "insurance claims adjuster",
        "debt collector", "loan shark's runner", "pawnbroker", "flesh-tech's assistant", "back-alley flesh-tech",
        "LifeCorp clinic nurse", "pharmacist", "Thera-Pay bot repair tech", "android maintenance tech", "sanitation worker",
        "recycler / scrap picker", "sewer maintenance crew", "building super", "night security guard", "ex-CorpSec officer",
        "CorpSec trainee studying for the exam", "student", "unemployed", "laid off last month", "retired dock worker",
        "street-food vendor", "vending-machine restocker", "noodle-bar waiter", "hotel cleaner", "tattoo and dermis artist",
        "loudcaster wannabe", "pirate-radio host", "smuggler (keeps it quiet)", "card sharp", "chess hustler",
        "rooftop garden-pass seller", "black-market seed dealer", "metro hub ticket inspector", "shuttle mechanic",
        "mould remediation worker", "landlord's rent collector", "funeral and memory-archive clerk", "night-shift baker",
        "fishmonger", "dock crane operator", "tutor", "retired teacher", "influencer for an implant brand", "street preacher of CORA",
    };

    private static readonly string[] Quirks =
    {
        "uses oddly old-fashioned words", "swears creatively instead of plainly",
        "keeps bringing up their pet", "talks about food constantly", "half-believes a conspiracy theory", "trails off mid-sentence",
        "laughs nervously at their own jokes", "calls everyone 'chief'", "calls everyone 'friend' a little too warmly",
        "quotes their Automata's advice sarcastically", "counts things out loud", "exaggerates every story",
        "answers questions with questions", "very literal, misses jokes", "uses tiny polite phrases even when angry",
        "speaks in short clipped bursts", "rambles with tangents", "drops names of people nobody knows",
        "mispronounces corp brand names on purpose", "sighs before answering", "complains about their implant acting up",
        "obsessed with the weather report", "keeps checking the time", "always mentions how much things cost in Lims",
        "uses dock slang", "makes dark jokes about dying", "very formal, almost stiff", "repeats the last word you said",
        "gives unsolicited advice", "suspicious, asks who sent you", "talks to themselves", "hums between sentences",
        "uses outdated slang from twenty years ago", "overly precise with numbers",
        "brags about a cousin who made it to Luna", "sighs before answering anything", "flatters, then asks for something",
    };

    private static readonly string[] Obsessions =
    {
        "getting a Skyhigh Gardens pass", "saving Lims for a Luna ticket", "clearing their family's generational debt",
        "the Aseptic murders", "a loudcaster they follow", "a chess tournament", "their busted implant", "a lost sibling",
        "an ex who won't leave them alone", "fishing", "pirate radio", "getting their own boat", "a band nobody's heard of",
        "a sick parent", "a stray cat they feed", "the Oil Rig movement", "gambling in Midtown", "real fruit",
        "becoming a CorpSec officer", "a business idea that will surely work this time", "their landlord", "sentient mould in their flat",
        "a rumour about the Old Network", "dermis rot", "a rooftop garden they tend illegally", "an android friend",
        "Byal Lapin's graffiti", "the food scene and noodle bars", "a lawsuit against Curzona", "their kid's schooling",
    };

    private static readonly string[] LifeSituations =
    {
        "barely scraping by", "comfortable but bored", "recently had their life turned upside down", "quietly ambitious",
        "stuck in a rut", "in debt up to the eyeballs", "doing better than most, and a bit guilty about it",
        "hiding something", "newly in love", "grieving someone", "in the middle of a feud", "just got some good news",
    };

    public static PersonaSeeds Roll(Random rng, NpcVisual visual, PopulationSettings? pop = null)
    {
        T Pick<T>(IReadOnlyList<T> list) => list[rng.Next(list.Count)];
        var s = new PersonaSeeds
        {
            NameFlavour = Pick(NameFlavours),
            NameInitial = "ABCDEFGHIJKLMNOPRSTVWYZ"[rng.Next(23)],
            Temperament = Pick(Temperaments),
            OccupationHint = Pick(Occupations),
            Quirk = Pick(Quirks),
            Obsession = Pick(Obsessions),
            LifeSituation = Pick(LifeSituations),
        };
        double r = rng.NextDouble();
        s.SlangLevel = r < 0.20 ? 0 : r < 0.60 ? 1 : r < 0.90 ? 2 : 3; // heavy slang is the exception, not one in five
        s.Verbosity = Pick(new[] { "terse", "terse", "average", "average", "average", "chatty", "rambling" });
        s.Formality = s.SlangLevel switch { 0 => Pick(new[] { "formal", "polite", "neutral" }), 1 => Pick(new[] { "neutral", "casual" }), _ => "casual" };
        s.Swearing = Pick(new[] { "never", "rarely", "rarely", "sometimes", "sometimes", "often" });
        double a = rng.NextDouble();
        pop ??= new PopulationSettings();
        s.Age = Population.RollAge(rng, pop);
        // Their model shows roughly how old they are: pick within that (and the settings' range, where the two overlap).
        if (Looks.Find(visual.Model) is { Machine: false, MinAge: >= 18 } look)
        {
            var (lo, hi) = pop.AgeRange();
            int from = Math.Max(look.MinAge, lo), to = Math.Min(look.MaxAge, hi);
            if (from > to) (from, to) = (look.MinAge, look.MaxAge); // the model clearly looks older or younger than the range: trust the eyes
            s.Age = rng.Next(from, to + 1);
        }
        s.AgeHint = visual.IsAndroid
            ? Pick(new[] { "a fairly new model", "several years in service", "an old model, long in service" })
            : s.Age.ToString();
        s.Identity = Population.RollIdentity(rng, visual.IsAndroid ? "android" : visual.Body, s.Age, pop);
        s.Relationship = s.Age < 23 ? Pick(new[] { "single", "single", "dating someone new", "it's complicated" })
                       : s.Age > 65 ? Pick(new[] { "married", "widowed", "widowed", "single", "divorced" }) : Pick(Relationships);
        s.Faith = Pick(Faiths);
        s.Memory = Pick(Memories);
        s.Politics = Pick(PoliticalViews);
        s.Humor = Pick(Humors);
        s.Honesty = Pick(Honesties);
        s.ConflictStyle = Pick(ConflictStyles);
        s.Education = Pick(Educations);
        s.Health = visual.IsAndroid ? Pick(new[] { "runs fine", "a sticky actuator in one hand", "overdue firmware update", "a cracked chassis panel they patched themselves" }) : Pick(Healths);
        return s;
    }
}

public static class Moods
{
    public static readonly string[] All =
    {
        "content", "cheerful", "playful", "bored", "tired", "irritated", "angry", "anxious", "stressed", "sad",
        "melancholy", "suspicious", "distracted", "curious", "excited", "flirty", "tipsy", "hungover", "nostalgic",
        "paranoid", "grumpy", "hopeful", "lonely", "in a hurry", "amused", "nervous",
    };

    /// <summary>What a passer-by can read from someone in this mood.</summary>
    public static string Cue(string mood) => mood switch
    {
        "content" => "seems relaxed",
        "cheerful" => "seems in good spirits",
        "playful" => "has a mischievous look",
        "bored" => "looks bored",
        "tired" => "looks exhausted",
        "irritated" => "seems irritated",
        "angry" => "looks angry",
        "anxious" => "seems on edge",
        "stressed" => "looks stressed",
        "sad" => "looks down",
        "melancholy" => "seems lost in thought",
        "suspicious" => "eyes you warily",
        "distracted" => "seems distracted",
        "curious" => "looks curious",
        "excited" => "seems excited",
        "flirty" => "gives you a look",
        "tipsy" => "is a little unsteady",
        "hungover" => "looks rough",
        "nostalgic" => "seems wistful",
        "paranoid" => "keeps glancing around",
        "grumpy" => "seems grumpy",
        "hopeful" => "seems upbeat",
        "lonely" => "seems glad of company",
        "in a hurry" => "seems in a hurry",
        "amused" => "seems amused",
        "nervous" => "seems nervous",
        _ => "",
    };

    public static string Normalize(string? mood)
    {
        if (string.IsNullOrWhiteSpace(mood)) return "";
        string m = mood.Trim().ToLowerInvariant();
        if (All.Contains(m)) return m;
        // map common synonyms the model might use
        return m switch
        {
            "happy" or "upbeat" or "joyful" => "cheerful",
            "mad" or "furious" or "annoyed" => m == "annoyed" ? "irritated" : "angry",
            "worried" or "uneasy" => "anxious",
            "drunk" => "tipsy",
            "scared" or "afraid" or "frightened" => "nervous",
            "calm" or "relaxed" or "fine" or "neutral" => "content",
            "wary" or "distrustful" => "suspicious",
            "interested" or "intrigued" => "curious",
            "depressed" or "upset" or "down" => "sad",
            "rushed" or "busy" => "in a hurry",
            "flirtatious" => "flirty",
            _ => All.FirstOrDefault(x => m.Contains(x)) ?? "",
        };
    }

    /// <summary>Roll today's mood from temperament, time of day, weather and how they feel about you.</summary>
    /// <summary>Moods that make someone open to a chat; the rest are guarded or low (busy, tired, wary, upset...).</summary>
    public static readonly HashSet<string> Open = new()
    {
        "content", "cheerful", "playful", "curious", "excited", "flirty", "tipsy", "hopeful", "lonely", "amused", "nostalgic",
    };

    /// <summary>The average open share the raw weights give across a day's people, hours and weather (measured by simulation).</summary>
    private const double AverageOpenShare = 0.39;

    /// <summary>
    /// Today's mood. First a coin weighted by <paramref name="friendliness"/> (0..1; 0.5 = an even mix across the city)
    /// picks open or guarded — their temperament, the hour, the weather and how they feel about the player tip it either
    /// way — then the mood itself is drawn from that side, by the same weights.
    /// </summary>
    public static (string mood, string intensity) Roll(Random rng, Persona p, int hour, string weather, int affinity, bool isAndroid, double friendliness = 0.5)
    {
        var w = All.ToDictionary(m => m, _ => 1.0);
        void Boost(double by, params string[] moods) { foreach (var m in moods) if (w.ContainsKey(m)) w[m] += by; }
        string t = (p.Temperament + " " + string.Join(" ", p.Personality)).ToLowerInvariant();
        if (t.Contains("cynic") || t.Contains("bitter") || t.Contains("dry")) Boost(2, "irritated", "bored", "grumpy", "suspicious");
        if (t.Contains("upbeat") || t.Contains("cheer") || t.Contains("warm") || t.Contains("optimis")) Boost(3, "cheerful", "playful", "content", "hopeful");
        if (t.Contains("anxious") || t.Contains("nervous") || t.Contains("fidget")) Boost(3, "anxious", "stressed", "nervous");
        if (t.Contains("paranoid") || t.Contains("suspicious")) Boost(3, "suspicious", "paranoid");
        if (t.Contains("hot") || t.Contains("touchy") || t.Contains("proud")) Boost(2, "irritated", "angry");
        if (t.Contains("melanchol") || t.Contains("griev") || t.Contains("weary")) Boost(3, "melancholy", "sad", "tired");
        if (t.Contains("flirt")) Boost(3, "flirty", "playful");
        if (t.Contains("lonely")) Boost(3, "lonely", "hopeful");
        if (t.Contains("hurry") || t.Contains("impatient")) Boost(3, "in a hurry", "irritated");
        if (t.Contains("dream") || t.Contains("scatter")) Boost(3, "distracted", "nostalgic");
        if (t.Contains("mischiev") || t.Contains("theatric")) Boost(2, "playful", "amused");
        if (hour >= 0 && hour < 5) Boost(3, "tired", "lonely", "tipsy", "melancholy");
        else if (hour < 10) Boost(2.5, "tired", "grumpy", "hungover", "in a hurry");
        else if (hour < 17) Boost(1.5, "in a hurry", "stressed", "bored", "distracted");
        else Boost(1.5, "tipsy", "playful", "content", "tired");
        string wx = weather.ToLowerInvariant();
        if (wx.Contains("rain") || wx.Contains("snow") || wx.Contains("storm") || wx.Contains("blizzard") || wx.Contains("smog"))
            Boost(1.5, "grumpy", "irritated", "melancholy", "tired");
        // How they feel about you leans the mood: full strength from ±30 as before, and gradually from neutral below that.
        double lean = Relationships.MoodLean(affinity);
        if (lean > 0) Boost(lean, "cheerful", "content", "playful", "hopeful");
        else if (lean < 0) Boost(-lean, "irritated", "suspicious", "angry");
        if (isAndroid) { w["tipsy"] = 0.1; w["hungover"] = 0.1; }

        double total = w.Values.Sum();
        double openShare = w.Where(kv => Open.Contains(kv.Key)).Sum(kv => kv.Value) / total;
        double chanceOpen = Math.Clamp(Math.Clamp(friendliness, 0, 1) + (openShare - AverageOpenShare), 0.03, 0.97);
        bool open = rng.NextDouble() < chanceOpen;
        var side = w.Where(kv => Open.Contains(kv.Key) == open).ToList();
        double roll = rng.NextDouble() * side.Sum(kv => kv.Value);
        string pick = open ? "content" : "bored";
        foreach (var kv in side) { roll -= kv.Value; if (roll <= 0) { pick = kv.Key; break; } }
        double i = rng.NextDouble();
        return (pick, i < 0.45 ? "mild" : i < 0.85 ? "moderate" : "strong");
    }
}
