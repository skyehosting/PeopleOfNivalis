using System;
using System.Linq;
using System.Text.Json;
using NivalisAIChat.Ai;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;

namespace NivalisAIChat.Tests;

/// <summary>
/// The relationship bar (the author's request, 2026-10-12): 0–100%, 50% neutral, moving slowly per reply, with the
/// milestones and amounts as settings.
/// </summary>
internal static partial class Program
{
    /// <summary>Through NpcBrain: the friendship milestone (FriendAt, a setting) and a free gift's +1.5 (review of 919404e).</summary>
    private static async System.Threading.Tasks.Task RelationshipBrainTests(string dir)
    {
        var ep = new FakeEndpoint
        {
            Respond = _ => (200, AnthropicText("{\"say\":\"Oh, hey.\",\"mood\":\"content\",\"moodIntensity\":\"mild\",\"moodReason\":\"\",\"affinityChange\":0," +
                "\"learnedPlayerName\":\"\",\"recall\":\"\",\"wantsFood\":\"\",\"wantsFoodHow\":\"\",\"introducedAs\":\"\",\"sharedIdentity\":false," +
                "\"toldAboutMe\":[],\"newSelfFacts\":[],\"leaving\":false}")),
        };
        var snap = new Game.GameSnapshot { GameDay = 4, Hour = 12 };
        async System.Threading.Tasks.Task<NpcState> MeetThird(string id, double friendAt)
        {
            var store = new NpcStore(System.IO.Path.Combine(dir, "rel-" + id), _ => { });
            var known = Person(id, "40");
            known.State.TimesMet = 2;
            known.State.Familiarity = "acquaintance";
            known.State.Relationship = 75;
            store.Save(known);
            var brain = new NpcBrain(new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new System.Net.Http.HttpMessageInvoker(ep)), store,
                () => new BrainSettings { NpcsCanLeave = false, Milestones = new Milestones { FriendAt = friendAt } }, _ => { });
            var (s, _, _) = await brain.BeginAsync(id, known.Visual, snap, System.Threading.CancellationToken.None);
            var state = s!.Record.State;
            if (id == "p_gift")
            {
                double before = state.Relationship;
                await brain.HandOverAsync(s, "Gyoza", 0, "", true, snap, System.Threading.CancellationToken.None);
                Check(state.Relationship == before + 1.5, $"a free gift moves it +1.5 (FoodGiftBonus; {before} -> {state.Relationship})");
            }
            brain.End(s);
            await brain.Settled(id);
            return state;
        }
        Check((await MeetThird("p_friend", 70)).Familiarity == "friend", "met a third time at 75%: a friend (FriendAt 70)");
        Check((await MeetThird("p_notyet", 80)).Familiarity == "acquaintance", "...but not with FriendAt at 80: the milestone is a setting");
        await MeetThird("p_gift", 70);
    }

    private static void RelationshipTests()
    {
        Console.WriteLine("The relationship bar: 0-100%, 50% neutral, slow, with settings (feature)");

        // The step from the AI's -10..10 rating, at the author's defaults (+0.5 at most; -0.1 to -0.5).
        double S(int r) => Relationships.Step(r, 0.5, 0.1, 0.5);
        Check(S(0) == 0 && S(10) == 0.5 && S(1) == 0.05 && Math.Abs(S(5) - 0.25) < 1e-9, "a liked reply moves it up a tenth of the most per point (+0.05 .. +0.5)");
        Check(S(-1) == -0.1 && Math.Abs(S(-10) + 0.5) < 1e-9 && S(-5) < S(-4) && S(-4) < S(-1), "a disliked one moves it down from -0.1 to -0.5, more the worse it was");
        Check(S(99) == S(10) && S(-99) == S(-10), "a rating past -10..10 counts as the end");
        Check(Relationships.Step(10, 0.3, 0.1, 0.3) == 0.3 && Math.Abs(Relationships.Step(-10, 0.3, 0.1, 0.3) + 0.3) < 1e-9 && Relationships.Step(-1, 0.3, 0.1, 0.3) == -0.1,
            "the amounts are settings (the first defaults: +0.3, -0.1 .. -0.3)");
        Check(Relationships.Step(-1, 0.5, 0.9, 0.5) == -0.5 && Relationships.Step(10, 99, 0.1, 0.5) == 5,
            "a smallest drop above the largest is held at it; amounts are held to 0..5");

        // The stored value: Relationship, with the older Affinity in step.
        var st = new NpcState();
        Check(st.Relationship == 50 && st.Affinity == 0, "everyone starts at 50%, neutral");
        st.Shift(0.25);
        st.Shift(0.25);
        Check(st.Relationship == 50.5 && st.Affinity == 1, "it moves by fractions, and the older -100..100 follows (50.5% = +1)");
        st.Shift(80);
        Check(st.Relationship == 100 && st.Affinity == 100, "held at 100%");
        st.Shift(-500);
        Check(st.Relationship == 0 && st.Affinity == -100, "held at 0%");
        st.Shift(0.123456);
        Check(st.Relationship == 0.1235, "kept to four decimals");
        foreach (var (old, pct) in new[] { (-100, 0.0), (-40, 30.0), (0, 50.0), (40, 70.0), (100, 100.0), (15, 57.5) })
            Check(new NpcState { Affinity = old }.Relationship == pct, $"an older file's affinity {old} reads as {pct}% (everyone keeps where they stand)");
        var set = new NpcState();
        set.Shift(10);
        set.Affinity = -40;
        Check(set.Relationship == 30, "Affinity set on its own (older code, a test) moves the relationship with it");

        // Files: written with both; an older one (no relationship) reads from its affinity; a new one keeps the fraction.
        var written = new NpcRecord { Id = "p_bar" };
        written.State.Shift(12.34);
        string json = JsonSerializer.Serialize(written, NpcStore.Json);
        var back = JsonSerializer.Deserialize<NpcRecord>(json, NpcStore.ReadJson)!;
        Check(json.Contains("\"relationship\": 62.34") && json.Contains("\"affinity\": 25") && back.State.Relationship == 62.34 && back.State.Affinity == 25,
            "a file holds the relationship (62.34) and the older affinity in step (25); both read back");
        var older = JsonSerializer.Deserialize<NpcRecord>("{\"id\":\"p_old\",\"state\":{\"affinity\":40}}", NpcStore.ReadJson)!;
        Check(older.State.Relationship == 70, "a file from before the bar (affinity 40) reads as 70%");
        var pairJson = JsonSerializer.Serialize(new PairBook.Pair { A = "p_a", B = "p_b", Relationship = 61.5 }, NpcStore.Json);
        var pairBack = JsonSerializer.Deserialize<PairBook.Pair>(pairJson, NpcStore.Json)!;
        var pairOld = JsonSerializer.Deserialize<PairBook.Pair>("{\"a\":\"p_a\",\"b\":\"p_b\",\"affinity\":-60}", NpcStore.Json)!;
        Check(pairBack.Relationship == 61.5 && pairBack.Affinity == 23 && pairOld.Relationship == 20, "residents' pairs too: 61.5% round-trips, an older -60 reads as 20%");

        // The pill: ten cells red -> white -> green, and the marker.
        var cells = Enumerable.Range(0, Relationships.Cells).Select(Relationships.CellColour).ToList();
        Check(cells[0].r > 150 && cells[0].g < 100 && cells[^1].g > 150 && cells[^1].r < 100 && cells[4].r > 200 && cells[4].g > 200 && cells[5].g > 200 && cells[5].b > 200,
            "the cells run red on the left, white in the middle, green on the right");
        Check(cells.Zip(cells.Skip(1)).All(p => p.Second.g - p.Second.r > p.First.g - p.First.r)
              && cells.Zip(cells.Skip(1)).All(p => Math.Abs(p.Second.r - p.First.r) < 60 && Math.Abs(p.Second.g - p.First.g) < 60 && Math.Abs(p.Second.b - p.First.b) < 60),
            "...and blend smoothly, each step greener than the last (each cell a single colour)");
        Check(Relationships.MarkerAt(0) == 0 && Relationships.MarkerAt(50) == 0.5 && Relationships.MarkerAt(100) == 1 && Relationships.MarkerAt(150) == 1,
            "the marker sits at the far left at 0%, the middle at 50%, the far right at 100%");

        // The mood rolled at a meeting leans with it: full strength from +-30 (as before), gradually below.
        Check(Relationships.MoodLean(0) == 0 && Relationships.MoodLean(15) == 1.5 && Relationships.MoodLean(30) == 3 && Relationships.MoodLean(100) == 3
              && Relationships.MoodLean(-15) == -1.5, "how they feel about you leans the mood they meet you in, gradually");
        var persona = new Persona { Temperament = "even" };
        int Warm(int affinity)
        {
            var rng = new Random(7);
            int n = 0;
            for (int i = 0; i < 4000; i++) if (Moods.Roll(rng, persona, 13, "Clear", affinity, false).mood is "cheerful" or "content" or "playful" or "hopeful") n++;
            return n;
        }
        Check(Warm(15) > Warm(0) && Warm(-15) < Warm(0), $"a slight liking already warms the mood a little, a slight dislike cools it ({Warm(-15)} < {Warm(0)} < {Warm(15)})");

        // The milestones (settings): waving from 60% (a new friend), calling you over from 70%, snubs at 37.5% or below.
        NpcState Met(double pct) { var s = new NpcState { TimesMet = 3 }; s.Relationship = pct; return s; }
        GreetingRules.Kind? K(double pct, string mood = "content", Milestones? m = null) => GreetingRules.Decide(Met(pct), mood, 35, 10, m).kind;
        Check(K(59.99) == GreetingRules.Kind.Nod && K(60) == GreetingRules.Kind.Wave && K(69.99, "cheerful") == GreetingRules.Kind.Wave
              && K(70, "cheerful") == GreetingRules.Kind.WaveOver, "they wave from 60% and call you over from 70% (in a good mood)");
        Check(K(37.5) == GreetingRules.Kind.Snub && K(37.51) == GreetingRules.Kind.Nod, "they snub you at 37.5% or below");
        var mine = new Milestones { WaveAt = 52, WaveOverAt = 55, SnubAt = 45 };
        Check(K(52, m: mine) == GreetingRules.Kind.Wave && K(55, "cheerful", mine) == GreetingRules.Kind.WaveOver && K(45, m: mine) == GreetingRules.Kind.Snub,
            "the milestones are settings");

        // Review of 919404e.
        // A blank or broken relationship in a file isn't 0% ("can't stand you"): the standing comes from affinity. A real 0 stays 0.
        foreach (var bad in new[] { "null", "\"\"", "\"62%\"", "true", "{}", "[]" })
        {
            var rec = JsonSerializer.Deserialize<NpcRecord>("{\"id\":\"p_x\",\"state\":{\"affinity\":40,\"relationship\":" + bad + "}}", NpcStore.ReadJson)!;
            Check(rec.State.Relationship == 70 && rec.State.Affinity == 40, $"a relationship of {bad} in a file keeps their standing (70% from affinity 40), not 0%");
        }
        Check(JsonSerializer.Deserialize<NpcRecord>("{\"id\":\"p_x\",\"state\":{\"affinity\":-100,\"relationship\":0}}", NpcStore.ReadJson)!.State.Relationship == 0
              && JsonSerializer.Deserialize<NpcRecord>("{\"id\":\"p_x\",\"state\":{\"relationship\":\"61.5\"}}", NpcStore.ReadJson)!.State.Relationship == 61.5,
            "a real 0 stays 0, and a number written as text reads");
        Check(JsonSerializer.Deserialize<PairBook.Pair>("{\"a\":\"p_a\",\"b\":\"p_b\",\"affinity\":20,\"relationship\":null}", NpcStore.Json)!.Relationship == 60,
            "...residents' pairs too (pairs.json isn't moved aside for it)");
        // Amounts that aren't numbers ("NaN" in the settings) fall back to the defaults; a change that isn't one moves nothing.
        Check(Relationships.Step(10, double.NaN, 0.1, 0.5) == 0.5 && Relationships.Step(-10, 0.5, double.NaN, double.PositiveInfinity) == -0.5,
            "a setting that isn't a number falls back to its default");
        var steady = new NpcState { Relationship = 62 };
        steady.Shift(double.NaN);
        steady.Shift(double.PositiveInfinity);
        Check(steady.Relationship == 62 && steady.Affinity == 24 && Relationships.ToAffinity(double.NaN) == 0, "...and someone never drops to 0% (or out of -100..100) for it");
        var tiny = new NpcState();
        for (int i = 0; i < 100; i++) tiny.Shift(Relationships.Step(1, 0.004, 0.1, 0.5));
        Check(Math.Abs(tiny.Relationship - 50.04) < 1e-9, $"a very small amount from the settings still adds up ({tiny.Relationship})");
        // Affinity set to what it already is (it agrees) keeps the fraction.
        var agree = new NpcState();
        agree.Shift(0.3);
        agree.Affinity = 1;
        var agreePair = new PairBook.Pair { Relationship = 50.3 };
        agreePair.Affinity = 1;
        Check(agree.Relationship == 50.3 && agreePair.Relationship == 50.3, "setting Affinity to what it already reads keeps the fraction (people and pairs)");
        // The info panel shows the exact %.
        var shown = new NpcRecord { Id = "p_shown", Persona = new Persona { Name = "Ilsa" } };
        shown.State.TimesMet = 2;
        shown.State.Relationship = 62.34;
        string want = $"{62.34:0.#}% on the relationship bar"; // in the player's own number format
        Check(InfoSheet.Build(shown, null, "Ilsa", "Woman", "").Sections.SelectMany(x => x.items).Any(l => l.Contains(want)),
            "the info panel says where they stand (" + want + ")");

        // The discount: from where the setting says, on the bar.
        var buyer = new NpcRecord { Id = "p_buyer" };
        buyer.State.Favours.Add(new Favour { Dish = "Spicy Ramen", Price = 40, Status = "open" });
        var snap = new Game.GameSnapshot { Menu = { new Game.MenuEntry { Dish = "Spicy Ramen", Price = 40 } } };
        buyer.State.Relationship = 69.99;
        bool under = Social.FoodOffer.For(buyer, snap, "Spicy Ramen", 40, 20, 70, 0).Price == 40;
        buyer.State.Relationship = 70;
        bool at = Social.FoodOffer.For(buyer, snap, "Spicy Ramen", 40, 20, 70, 0).Price == 32;
        Check(under && at, "the friend discount from FriendDiscountAt (70%) on the bar");
    }
}
