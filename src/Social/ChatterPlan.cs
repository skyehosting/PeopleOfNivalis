using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisAIChat.Social;

/// <summary>
/// The NPC chatter settings, as plain data (from the config; changed in-game with /chatter). Three kinds, each on its
/// own timer, chance, line limit and pool of how many can go on at once: residents' conversations (2 or more), phone
/// calls, and someone thinking out loud. All of them share the hourly cap.
/// </summary>
public sealed class ChatterSettings
{
    public bool Enabled;
    /// <summary>
    /// Dynamic: every <see cref="DynamicEverySeconds"/> a kind is picked at random (gossip, a phone call, thinking out
    /// loud) and someone near you who fits, while fewer than <see cref="DynamicMaxNpcs"/> people are talking near you
    /// (see <see cref="DynamicChatter"/>). Advanced (false): each kind on its own timer, chance and pool below.
    /// </summary>
    public bool Dynamic = true;
    public int DynamicEverySeconds = 15;
    /// <summary>Most people talking near you at once in Dynamic mode; anyone wrapping up because you walked off no longer counts.</summary>
    public int DynamicMaxNpcs = 4;
    /// <summary>Most lines each person says in a conversation in Dynamic mode.</summary>
    public int DynamicLines = 4;
    /// <summary>Most lines each side of a phone call says in Dynamic mode.</summary>
    public int DynamicPhoneLines = 4;
    /// <summary>Most lines someone mutters to themselves in one go in Dynamic mode (at most <see cref="MaxThinkLines"/>).</summary>
    public int DynamicThinkLines = 2;
    /// <summary>Dynamic: each round, between 1 and this many new things may start (room in the pool permitting).</summary>
    public int DynamicStarts = 2;
    /// <summary>Dynamic: most seconds between two starts in the same round, so they don't all begin at once (0 = together).</summary>
    public float DynamicStagger = 4f;
    /// <summary>Dynamic: how often each kind is picked, relative to the others (0 = never). Shop talk also needs OrderTalk.</summary>
    public int WeightGossip = 30, WeightPhone = 20, WeightThink = 20, WeightShop = 30;
    /// <summary>The phone (the author: "a new option that can happen, receiving a text from someone you know"): its weight.</summary>
    public int WeightText = 0;
    /// <summary>The weights as used, each held to 0..<see cref="MaxWeight"/> (a hand-edited huge number can't swamp the others).</summary>
    public DynamicChatter.Weights DynamicWeights => new(Weight(WeightGossip), Weight(WeightPhone), Weight(WeightThink), Weight(WeightShop), Weight(WeightText));
    public const int MaxWeight = 1000;
    private static int Weight(int w) => Math.Clamp(w, 0, MaxWeight);
    // ---- conversations between residents
    /// <summary>How often (seconds) the city rolls for a new conversation near the player.</summary>
    public int CheckSeconds = 30;
    /// <summary>Percent chance per roll that a conversation starts.</summary>
    public int ChancePercent = 50;
    /// <summary>Conversations going on at the same time.</summary>
    public int MaxGroups = 2;
    // ---- phone calls
    public int PhoneEverySeconds = 60;
    /// <summary>Percent chance, per roll, that someone on their own takes or makes a phone call.</summary>
    public int PhonePercent = 30;
    public int PhoneMax = 1;
    /// <summary>Most lines each side of a call says (the caller's last one hangs up).</summary>
    public int PhoneLines = 4;
    // ---- thinking out loud
    public int ThinkEverySeconds = 45;
    /// <summary>Percent chance per roll that someone near the player on their own thinks out loud.</summary>
    public int MumblePercent = 30;
    public int ThinkMax = 2;
    /// <summary>Most lines someone says to themselves in one go.</summary>
    public int ThinkLines = 2;
    // ---- all kinds
    /// <summary>Most started per real hour, all kinds together (keeps AI use predictable).</summary>
    public int PerHour = 30;
    /// <summary>Lines are voiced only when the player is within this many metres of the speaker (saves the voice provider).</summary>
    public float VoiceDistance = 15f;
    /// <summary>Lines are also voiced this many metres past <see cref="VoiceDistance"/>, silent until you come within it (then they fade in).</summary>
    public float VoiceBeyond = 3f;
    /// <summary>Their words show over their heads only within this many metres of the player.</summary>
    public float TextDistance = 5f;
    /// <summary>The game's own written background chatter is silenced while ours is on.</summary>
    public bool ReplaceGameChatter = true;
    /// <summary>Biggest group (2 to 4).</summary>
    public int GroupSize = 3;
    /// <summary>Percent chance, per free seat, that someone joins partway through.</summary>
    public int JoinPercent = 33;
    /// <summary>Most lines each person says; their last one is a goodbye.</summary>
    public int LinesEach = 4;
    /// <summary>Percent of conversations between two people who've talked before (when there are any around).</summary>
    public int KnownPairPercent = 50;
    /// <summary>Percent of conversations between people sitting at the same table (when there are any around).</summary>
    public int TablePercent = 65;
    /// <summary>Only people within this many metres of the player.</summary>
    public float Range = 18.25f; // FB-38: the chatter ring, 0.25 m past the voices ring (as ChatterRange)
    /// <summary>One person walks over to the other (otherwise only people already close together talk).</summary>
    public bool WalkUp = true;

    public int ClampedGroupSize => Math.Clamp(GroupSize, 2, 4);
    public int ClampedLines => Math.Clamp(LinesEach, 1, MaxLinesEach);
    public int ClampedPhoneLines => Math.Clamp(PhoneLines, 1, MaxLinesEach);
    public int ClampedThinkLines => Math.Clamp(ThinkLines, 1, MaxThinkLines);
    public const int MaxLinesEach = 12, MaxThinkLines = 4, MaxAtOnce = 6;

    public const int MaxDynamicNpcs = 12;

    public ChatterSettings Copy() => (ChatterSettings)MemberwiseClone();

    /// <summary>
    /// The settings as they apply right now: in Dynamic mode, its own line limit for each kind and the usual group habits
    /// (the Advanced settings don't apply); how many go on at once is up to the people pool, not each kind's pool.
    /// </summary>
    public ChatterSettings Effective()
    {
        if (!Dynamic) return this;
        var d = Copy();
        d.LinesEach = Math.Clamp(DynamicLines, 1, MaxLinesEach);
        d.PhoneLines = Math.Clamp(DynamicPhoneLines, 1, MaxLinesEach);
        d.ThinkLines = Math.Clamp(DynamicThinkLines, 1, MaxThinkLines);
        d.GroupSize = 4;
        d.JoinPercent = 33;
        d.KnownPairPercent = 50;
        d.TablePercent = 65;
        d.WalkUp = true;
        d.MaxGroups = d.PhoneMax = d.ThinkMax = MaxAtOnce;
        d.DynamicMaxNpcs = Math.Clamp(DynamicMaxNpcs, 1, MaxDynamicNpcs);
        d.DynamicEverySeconds = Math.Max(3, DynamicEverySeconds);
        return d;
    }

    /// <summary>
    /// Past this distance from you, people talking wrap it up: <see cref="WrapMargin"/> past the chatter ring (ChatterRange,
    /// where they start: never the same distance, or one step back would end a talk just paid for), or the voices ring
    /// (VoiceDistance + VoiceBeyond) if that is farther (FB-38). With voices off, a little past where they started.
    /// </summary>
    public float WrapUpDistance => VoiceDistance > 0 ? Math.Max(VoiceDistance + Math.Max(0f, VoiceBeyond), Range + WrapMargin) : Math.Max(15f, Range * 1.6f);

    /// <summary>How far past the chatter ring talk carries on before it wraps up (the start and the end aren't the same line).</summary>
    public const float WrapMargin = 3f;

    // One part of a time: a number and its unit (longest units first, so "min" isn't read as "m" + "in").
    private static readonly System.Text.RegularExpressions.Regex IntervalPart = new(
        @"\G\s*([0-9]+(?:\.[0-9]+)?)\s*(seconds|second|secs|sec|s|minutes|minute|mins|min|m|hours|hour|hrs|hr|h)?",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// "45", "45s", "2m", "1.5m", "1h", and parts together as <see cref="Pretty"/> writes them ("1h30m", "2m 30s") →
    /// seconds (5 seconds to a day); null if it can't be read. A plain number (seconds) only stands alone.
    /// </summary>
    public static int? Interval(string? text)
    {
        string t = (text ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return null;
        double total = 0;
        int pos = 0, parts = 0;
        while (pos < t.Length)
        {
            var m = IntervalPart.Match(t, pos);
            if (!m.Success || m.Length == 0) return null;
            if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n)) return null;
            string unit = m.Groups[2].Value;
            if (unit.Length == 0 && (parts > 0 || pos + m.Length < t.Length)) return null;
            total += n * unit switch { "" or "s" or "sec" or "secs" or "second" or "seconds" => 1, "h" or "hr" or "hrs" or "hour" or "hours" => 3600, _ => 60 };
            parts++;
            pos += m.Length;
        }
        return (int)Math.Clamp(Math.Round(total), 5, 86400);
    }

    /// <summary>Seconds written the short way, exactly (<see cref="Interval"/> reads it back): 45s, 2m, 1h, 1h30m, 2m30s.</summary>
    public static string Pretty(int seconds)
    {
        if (seconds <= 0) return "0s";
        int h = seconds / 3600, m = seconds % 3600 / 60, s = seconds % 60;
        return (h > 0 ? h + "h" : "") + (m > 0 ? m + "m" : "") + (s > 0 ? s + "s" : "");
    }
}

/// <summary>
/// The chatter settings as they apply now, built once and kept until a setting changes (D3, PERF-6): they used to be built
/// from about 35 settings, with four intervals parsed, every frame, even with chatter off. The one kept is shared, so no
/// caller changes it (Dynamic starts work on their own <see cref="ChatterSettings.Copy"/>). Main thread only.
/// </summary>
public sealed class ChatterSettingsCache
{
    private readonly Func<ChatterSettings> _build;
    private ChatterSettings? _now;

    public ChatterSettingsCache(Func<ChatterSettings> build) => _build = build;

    public ChatterSettings Now => _now ??= _build();

    /// <summary>A setting changed (in the file, by a command, or by a reload): built again the next time it's asked for.</summary>
    public void Invalidate() => _now = null;
}

/// <summary>
/// The dice for one NPC conversation, rolled before the AI writes it: who's there from the start, who joins and when,
/// and how many lines each person gets. No Unity here, so it can be tested.
/// </summary>
public static class ChatterPlan
{
    public sealed class Seat
    {
        public string Id = "";
        /// <summary>The line (0-based) at which they join; 0 = there from the start.</summary>
        public int JoinAt;
        public int MaxLines;
    }

    /// <summary>
    /// The two who start talking plus up to <see cref="ChatterSettings.GroupSize"/>-2 joiners from <paramref name="possibleJoiners"/>,
    /// each seat filled with <see cref="ChatterSettings.JoinPercent"/> chance. A joiner arrives after a line or two.
    /// </summary>
    public static List<Seat> Roll(Random rng, ChatterSettings s, string first, string second, IReadOnlyList<string> possibleJoiners)
    {
        int lines = s.ClampedLines;
        var seats = new List<Seat>
        {
            new() { Id = first, JoinAt = 0, MaxLines = lines },
            new() { Id = second, JoinAt = 0, MaxLines = lines },
        };
        var pool = possibleJoiners.Where(j => j != first && j != second).Distinct().ToList();
        int lastStart = Math.Max(2, 2 * lines - 1); // join while the first two still have something to say
        for (int seat = 2; seat < s.ClampedGroupSize && pool.Count > 0; seat++)
        {
            if (rng.NextDouble() * 100 >= s.JoinPercent) continue;
            var who = pool[rng.Next(pool.Count)];
            pool.Remove(who);
            int at = rng.Next(2, lastStart + 1);
            seats.Add(new Seat { Id = who, JoinAt = at, MaxLines = lines });
        }
        return seats;
    }

    /// <summary>Which kind of pair to look for this time: a table, two who've talked before, or anyone.</summary>
    public enum PairKind { Table, Known, Random }

    public static PairKind Pick(Random rng, ChatterSettings s, bool tablesAround, bool knownAround)
    {
        if (tablesAround && rng.NextDouble() * 100 < s.TablePercent) return PairKind.Table;
        if (knownAround && rng.NextDouble() * 100 < s.KnownPairPercent) return PairKind.Known;
        return PairKind.Random;
    }

    /// <summary>A pair weighted by how well they get on (friends often, enemies rarely). Null when the list is empty.</summary>
    public static T? Weighted<T>(Random rng, IReadOnlyList<T> items, Func<T, double> weight) where T : class
    {
        if (items.Count == 0) return null;
        double total = items.Sum(i => Math.Max(0, weight(i)));
        if (total <= 0) return items[rng.Next(items.Count)];
        double r = rng.NextDouble() * total;
        foreach (var i in items)
        {
            r -= Math.Max(0, weight(i));
            if (r <= 0) return i;
        }
        return items[^1];
    }

    /// <summary>
    /// The chance someone breaks off a conversation to talk to you: a stranger butting in, rarely (15%); someone who knows
    /// you, more often the better they like you (5% to 85%); less when they're in a guarded mood.
    /// </summary>
    public static double JoinInChance(bool known, int affinity, bool openMood)
    {
        affinity = Npc.Features.Feeling(affinity); // FB-45: neutral while relationships are off
        if (!Npc.Features.Moods) openMood = true;
        double p = known ? 0.45 + Math.Clamp(affinity, -100, 100) / 250.0 : 0.15;
        if (!openMood) p -= 0.1;
        return Math.Clamp(p, 0.05, 0.85);
    }

    /// <summary>Conversations started within the last hour, for the hourly cap (times in real seconds).</summary>
    public static int InLastHour(IEnumerable<double> startedAt, double now) => startedAt.Count(t => now - t < 3600);

    /// <summary>
    /// FB-59: room for a start with the hour's allowance spread over the hour (the author: no long gaps). A bucket holds
    /// <paramref name="burst"/> starts and fills at <paramref name="perHour"/> an hour: a few may start back to back, then one
    /// every 3600/perHour seconds, so a low cap (the pacer's, say 15) isn't all used in the first minutes and then silence.
    /// </summary>
    public static bool Spaced(IEnumerable<double> startedAt, double now, int perHour, int burst)
    {
        if (perHour <= 0) return false;
        double rate = perHour / 3600.0, cap = Math.Max(1, burst), tokens = cap, last = now - 3600;
        foreach (var t in startedAt.Where(t => now - t < 3600).OrderBy(t => t))
        {
            tokens = Math.Min(cap, tokens + (t - last) * rate) - 1; // below 0 after a burst from before (or /chatter now)
            last = t;
        }
        return Math.Min(cap, tokens + (now - last) * rate) >= 1 - 1e-9;
    }

    /// <summary>
    /// Whether an overheard line is about the player (FB-05: never kept): it names them (any name a speaker knows them by),
    /// one of their places, or calls someone the newcomer, the stranger or the new one. Whole words, any case.
    /// </summary>
    public static bool AboutThePlayer(string line, IEnumerable<string> names, IEnumerable<string> places)
    {
        string t = line ?? "";
        bool Has(string w) => w.Length >= 2 && System.Text.RegularExpressions.Regex.IsMatch(t, @"(?i)\b" + System.Text.RegularExpressions.Regex.Escape(w) + @"\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (names.Any(n => (n ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(Has)) || places.Any(p => Has((p ?? "").Trim()))) return true;
        return System.Text.RegularExpressions.Regex.IsMatch(t, @"(?i)\b(?:newcomer|stranger|new (?:guy|girl|woman|man|one|face|kid|owner))\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// FB-05: whether someone near a line being said catches it: close enough, not in that conversation, not busy in a talk of
    /// their own, an adult, and their roll (0-99) under the chance. Each listener rolls once per conversation.
    /// </summary>
    public static bool Overhears(float distance, float within, bool inTheConversation, bool busy, bool minor, int roll, int chance) =>
        distance <= within && !inTheConversation && !busy && !minor && roll < Math.Clamp(chance, 0, 100);

    /// <summary>Within this of someone, a body counts as company (standing beside them).</summary>
    public const float AloneRadius = 1.5f;
    /// <summary>
    /// FB-18 (review M5): people sitting near you, grouped by table: by the game's own table when it's known, else everyone
    /// within <paramref name="radius"/> of everyone else (chained links would merge neighbouring tables). Indices into
    /// <paramref name="seats"/>; groups of one included (the caller wants two or more).
    /// </summary>
    public static List<List<int>> Tables(IReadOnlyList<(float x, float z, string table)> seats, float radius)
    {
        var groups = new List<List<int>>();
        var byTable = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var loose = new List<int>();
        for (int i = 0; i < seats.Count; i++)
        {
            if (string.IsNullOrEmpty(seats[i].table)) { loose.Add(i); continue; }
            if (!byTable.TryGetValue(seats[i].table, out var list)) byTable[seats[i].table] = list = new List<int>();
            list.Add(i);
        }
        groups.AddRange(byTable.Values);
        float D(int a, int b) => MathF.Sqrt((seats[a].x - seats[b].x) * (seats[a].x - seats[b].x) + (seats[a].z - seats[b].z) * (seats[a].z - seats[b].z));
        var used = new HashSet<int>();
        foreach (int i in loose)
        {
            if (!used.Add(i)) continue;
            var g = new List<int> { i };
            foreach (int j in loose)
                if (!used.Contains(j) && g.All(k => D(k, j) <= radius)) { g.Add(j); used.Add(j); }
            groups.Add(g);
        }
        return groups;
    }

    /// <summary>
    /// 0.3.3 (the author's test: customers left unserved): whether someone may be HELD for a call or a conversation. A
    /// hold pauses the game's own steps for them, so never someone at work (taking orders, cooking, serving, idling at work)
    /// or someone in the middle of a visit to a food place (<paramref name="visitStep"/> is the game's step: waiting to
    /// order, waiting for food, eating, leaving). Muttering holds nobody.
    /// </summary>
    public static bool MayHold(bool workerTask, string visitStep) => !workerTask && string.IsNullOrEmpty(visitStep);

    /// <summary>
    /// 0.3.3: a customer at a table who may join its conversation (with a light hold that lets the game's visit run on):
    /// waiting to order, waiting for their food, or eating. Not arriving, complaining or leaving. "" = not at a food place.
    /// </summary>
    public static bool TableStep(string visitStep) =>
        string.IsNullOrEmpty(visitStep) || visitStep is "WaitingToOrder" or "WaitingForOrder" or "Eating";

    /// <summary>Both sitting this close: the same table (FB-18).</summary>
    public const float TableRadius = 2.6f;
    /// <summary>One of them at work this close: across a counter (FB-20).</summary>
    public const float CounterRadius = 2f;

    /// <summary>
    /// Whether someone has company, so they don't mutter to themselves or take a call as if alone: anyone within 1.5 m, someone
    /// sitting at the same table (both seated, within 2.6 m: the author's FB-18, table-mates talk to each other instead),
    /// or across a counter (one of them at work, within 2 m: FB-20, "they probably shouldn't chatter mumble to themselves if
    /// they are within 1 meter and working"). <paramref name="nearby"/>: every other body near them, the player left out.
    /// </summary>
    public static bool HasCompany(IEnumerable<(float distance, bool bothSeated, bool atWork)> nearby) =>
        nearby.Any(o => o.distance < AloneRadius || (o.bothSeated && o.distance < TableRadius) || (o.atWork && o.distance < CounterRadius));
}

/// <summary>
/// Dynamic chatter: every so often a kind is picked at random (gossip between people near you, a phone call, someone
/// thinking out loud) and then people who fit it, while there's room in the pool of people talking near you. The pool is
/// a soft limit: people wrapping up because you walked off no longer count. No Unity here, so it can be tested.
/// </summary>
public static class DynamicChatter
{
    /// <summary>Gossip: residents talking to each other. Shop: staff and a customer at a food place (written lines, no AI).</summary>
    public enum Kind { Gossip, Phone, Self, Shop, Text }

    /// <summary>How often each kind is picked, relative to the others (0 = never).</summary>
    public readonly record struct Weights(int Gossip, int Phone, int Self, int Shop, int Text = 0)
    {
        /// <summary>The three AI kinds alike, no shop talk (how Dynamic mode picked before it had weights).</summary>
        public static readonly Weights Even = new(1, 1, 1, 0);
        public int Of(Kind k) => Math.Max(0, k switch { Kind.Gossip => Gossip, Kind.Phone => Phone, Kind.Self => Self, Kind.Text => Text, _ => Shop });
    }

    /// <summary>Most things one round may start.</summary>
    public const int MaxStarts = 6;

    /// <summary>People a kind needs from the pool (gossip and shop talk at least two; a call or a thought one; a text none: they're elsewhere).</summary>
    public static int Needs(Kind kind) => kind is Kind.Gossip or Kind.Shop ? 2 : kind == Kind.Text ? 0 : 1;

    /// <summary>The three AI kinds in random order, alike (see the weighted overload).</summary>
    public static List<Kind> Order(Random rng, int free) => Order(rng, free, Weights.Even);

    /// <summary>
    /// The kinds in a weighted random order (each tried once, the next if nobody fits): the first is picked in proportion
    /// to its weight, the next among those left, and so on. Kinds with no weight, or that the pool has no room for, are
    /// left out.
    /// </summary>
    public static List<Kind> Order(Random rng, int free, Weights weights)
    {
        var left = new List<Kind> { Kind.Gossip, Kind.Phone, Kind.Self, Kind.Shop, Kind.Text }
            .Where(k => weights.Of(k) > 0 && Needs(k) <= free).ToList();
        var order = new List<Kind>();
        while (left.Count > 0)
        {
            // Summed as a long: four huge weights would overflow an int (and Random.Next throws on a negative).
            long total = left.Sum(k => (long)weights.Of(k));
            long roll = total <= int.MaxValue ? rng.Next((int)total) : rng.NextInt64(total);
            int i = 0;
            for (; i < left.Count - 1; i++)
            {
                roll -= weights.Of(left[i]);
                if (roll < 0) break;
            }
            order.Add(left[i]);
            left.RemoveAt(i);
        }
        return order;
    }

    /// <summary>How many may start this round: 1 to <paramref name="most"/> (at most <see cref="MaxStarts"/>), at random.</summary>
    public static int Starts(Random rng, int most) => 1 + rng.Next(Math.Clamp(most, 1, MaxStarts));

    /// <summary>
    /// When each of this round's starts happens, in seconds from now: the first at once, each next one a random moment
    /// (0.8 s up to <paramref name="stagger"/>) after the one before, so people don't all start talking together.
    /// No stagger: all at once.
    /// </summary>
    public static List<float> Stagger(Random rng, int count, float stagger)
    {
        var at = new List<float>();
        float t = 0f;
        for (int i = 0; i < count; i++)
        {
            if (i > 0 && stagger > 0f)
            {
                float least = Math.Min(0.8f, stagger);
                t += least + (float)rng.NextDouble() * (stagger - least);
            }
            at.Add(t);
        }
        return at;
    }

    /// <summary>
    /// <see cref="Stagger(Random, int, float)"/> fitted into a round of <paramref name="roundSeconds"/>: when the last
    /// start would come later than 90% of the round (the next round clears what's still waiting), every gap shrinks
    /// alike so it doesn't. No round length: as is.
    /// </summary>
    public static List<float> Stagger(Random rng, int count, float stagger, float roundSeconds)
    {
        var at = Stagger(rng, count, stagger);
        float latest = at.Count > 0 ? at[^1] : 0f;
        float room = 0.9f * roundSeconds;
        if (roundSeconds > 0f && latest > room)
            for (int i = 0; i < at.Count; i++) at[i] *= room / latest;
        return at;
    }
}
