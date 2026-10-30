using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;

namespace NivalisAIChat.Tests;

/// <summary>
/// Step 6 of the audit, D1 (PERF-3, PERF-4, LEAK-1): opening a chat with a regular parsed their whole file (every line
/// ever said, kept for good: the author's Q2) several times on the main thread. Looking at someone now skips their
/// history and meetings while parsing (<see cref="NpcStore.Glance"/>), a phone call reads only the partner it picked,
/// and the history shown in the window comes from a pure function, built off the main thread.
/// </summary>
internal static partial class Program
{
    private static ChatLine Said(string role, int day, string text) => new() { Role = role, GameDay = day, Text = text };

    private static int ReadsOf(NpcStore store) =>
        (int?)typeof(NpcStore).GetProperty("Reads", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(store) ?? -1;

    private static async Task PerformanceTests(string dir)
    {
        Console.WriteLine("-- audit 2026-10-11: looking at someone skips their history (step 6, D1)");

        // ---- Glance: everything but History and Meetings, a fresh copy each time, never saved.
        var store = new NpcStore(Path.Combine(dir, "perf-glance"), _ => { });
        var ivy = MetBefore("p_ivy", "Ivy Moss");
        ivy.History = new List<ChatLine> { Said("player", 1, "hi"), Said("npc", 1, "hey"), Said("player", 2, "again") };
        ivy.Meetings = new List<Meeting> { new() { GameDay = 1, Summary = "met at the docks" }, new() { GameDay = 2, Summary = "again" } };
        ivy.LifeLog = new List<LifeEvent> { new() { GameDay = 2, Text = "Started a new shift." } };
        ivy.AddTalk(new Talk { Kind = Talk.Thought, Summary = "rent" });
        ivy.MemorySummary = "We talked about the docks.";
        store.Save(ivy);
        var g = store.Glance("p_ivy");
        Check(g != null && g.History.Count == 0 && g.Meetings.Count == 0, "a glance has no history and no meetings");
        var full = store.Peek("p_ivy")!;
        full.History.Clear();
        full.Meetings.Clear();
        Check(g != null && JsonSerializer.Serialize(g, NpcStore.Json) == JsonSerializer.Serialize(full, NpcStore.Json),
            "...and everything else is the record as saved (persona, state, life, talks, memory summary)");
        var g2 = store.Glance("p_ivy");
        if (g2 != null) g2.Persona.Name = "Changed";
        Check(g2 != null && !ReferenceEquals(g, g2) && store.Glance("p_ivy")?.Persona.Name == "Ivy Moss", "each glance is a copy of its own (changing one changes nothing else)");
        string ivyPath = Path.Combine(dir, "perf-glance", NpcStore.FileNameFor("p_ivy"));
        using (Hold(ivyPath, FileShare.None)) Check(store.Glance("p_ivy") == null, "a file held by another program: no glance, and no waiting");
        string badPath = Path.Combine(dir, "perf-glance", NpcStore.FileNameFor("p_bad"));
        File.WriteAllText(badPath, "{ \"id\": \"p_bad\", \"persona\": ");
        byte[] bad = File.ReadAllBytes(badPath);
        Check(store.Glance("p_bad") == null && Same(badPath, bad) && KeptAside(badPath) == null, "a damaged file: no glance, and the file is left exactly as it is");
        string? saveGlance = null;
        try { if (g != null) store.Save(g); saveGlance = "saved"; } catch (InvalidOperationException) { saveGlance = "refused"; }
        Check(saveGlance == "refused" && store.Peek("p_ivy")?.History.Count == 3, $"a glanced record is never saved: its history stays in the file ({saveGlance})");
        string? createGlance = null;
        try { if (g != null) { g.Id = "p_ivy_copy"; store.Create(g); } createGlance = "created"; } catch (InvalidOperationException) { createGlance = "refused"; }
        Check(createGlance == "refused" && !store.Has("p_ivy_copy"), $"...nor created as someone new ({createGlance})");

        // ---- A damaged file is moved aside only if it's still the same file: a save that lands meanwhile is read instead.
        var raceStore = new NpcStore(Path.Combine(dir, "perf-race"), _ => { });
        string racePath = Path.Combine(dir, "perf-race", NpcStore.FileNameFor("p_race"));
        File.WriteAllText(racePath, "{ \"id\": \"p_race\", ");
        var hook = typeof(NpcStore).GetProperty("BeforeDamagedMove", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        hook?.SetValue(raceStore, new Action<string>(p => File.WriteAllText(p, JsonSerializer.Serialize(MetBefore("p_race", "Rae Lund"), NpcStore.Json))));
        NpcRecord? raced = null;
        try { raced = raceStore.Load("p_race"); } catch { }
        Check(hook != null && raced?.Persona.Name == "Rae Lund" && KeptAside(racePath) == null,
            $"a save that lands between reading a damaged file and moving it: the new record is read, nothing moved ({raced?.Persona.Name ?? "nobody"})");

        // ---- 6a review: a glance refuses what a full read refuses (History or Meetings in the wrong shape).
        var shapes = new NpcStore(Path.Combine(dir, "perf-shapes"), _ => { });
        int shape = 0;
        foreach (var (what, json, ok) in new[]
                 {
                     ("a text", "\"abc\"", false), ("a number", "5", false), ("an object", "{}", false), ("a list of text", "[\"x\"]", false),
                     ("a list with nulls and records", "[null, {}]", true), ("null", "null", true),
                 })
        {
            string sid = "p_shape" + shape++;
            string spath = Path.Combine(dir, "perf-shapes", NpcStore.FileNameFor(sid));
            File.WriteAllText(spath, "{ \"id\": \"" + sid + "\", \"persona\": { \"name\": \"Sam\" }, \"history\": " + json + " }");
            bool glanced = shapes.Glance(sid) != null, peeked = shapes.Peek(sid) != null;
            Check(glanced == peeked && peeked == ok, $"history as {what}: a glance and a full read agree ({(ok ? "read" : "refused")}; glance {glanced}, peek {peeked})");
        }

        // ---- 6a review: the under-18 check's "unsure" (the file is there but couldn't be read right now).
        var unsureStore = new NpcStore(Path.Combine(dir, "perf-unsure"), _ => { });
        var unsureBrain = new NpcBrain(new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(new ScriptedAi())), unsureStore, () => new BrainSettings(), _ => { });
        unsureStore.Save(MetBefore("p_readable", "Ray Dunn"));
        unsureStore.Save(MetBefore("p_held", "Hal Moss"));
        string damagedUnsure = Path.Combine(dir, "perf-unsure", NpcStore.FileNameFor("p_damaged"));
        File.WriteAllText(damagedUnsure, "{ \"id\": ");
        var adult = new NpcVisual { Body = "man" };
        bool m1 = unsureBrain.IsMinor("p_readable", adult, out bool u1), m2 = unsureBrain.IsMinor("p_nobody", adult, out bool u2);
        bool m3, u3;
        using (Hold(Path.Combine(dir, "perf-unsure", NpcStore.FileNameFor("p_held")), FileShare.None)) m3 = unsureBrain.IsMinor("p_held", adult, out u3);
        bool m4 = unsureBrain.IsMinor("p_damaged", adult, out bool u4);
        bool m5 = unsureBrain.IsMinor("p_held", adult, glanced: null, out bool u5);
        Check(!m1 && !u1 && !m2 && !u2, "the under-18 check is sure for a readable record, and for nobody at all");
        Check(!m3 && u3 && !m4 && u4 && !m5 && u5, "...and unsure (not kept for the session) when the file is held, damaged, or the caller's glance found nothing");

        // ---- 6a review: a damaged file that keeps changing isn't moved aside; one held for a moment is waited for.
        var changing = new NpcStore(Path.Combine(dir, "perf-changing"), _ => { });
        string changingPath = Path.Combine(dir, "perf-changing", NpcStore.FileNameFor("p_changing"));
        File.WriteAllText(changingPath, "{ \"id\": ");
        int rewrites = 0;
        changing.BeforeDamagedMove = p => File.WriteAllText(p, "{ \"id\": " + new string(' ', ++rewrites));
        string changingOutcome = "returned";
        try { changing.Load("p_changing"); } catch (NpcFileUnreadableException e) { changingOutcome = e.Message.Contains("kept changing") ? "kept changing" : "other: " + e.Message; }
        Check(changingOutcome == "kept changing" && File.Exists(changingPath) && KeptAside(changingPath) == null,
            $"a damaged file that changes every time it's read: left as it is, said as such ({changingOutcome})");
        var heldOnce = new NpcStore(Path.Combine(dir, "perf-held-once"), _ => { });
        string heldOncePath = Path.Combine(dir, "perf-held-once", NpcStore.FileNameFor("p_held_once"));
        File.WriteAllText(heldOncePath, "{ \"id\": ");
        int heldCalls = 0;
        heldOnce.BeforeDamagedMove = p =>
        {
            if (++heldCalls != 1) return;
            var stream = Hold(p, FileShare.None);
            Task.Delay(120).ContinueWith(_ => stream.Dispose());
        };
        string heldOutcome = "returned";
        try { heldOutcome = heldOnce.Load("p_held_once") == null ? "moved aside" : "a record"; } catch (Exception e) { heldOutcome = "threw: " + e.Message; }
        Check(heldOutcome == "moved aside" && KeptAside(heldOncePath) != null,
            $"a damaged file held by another program for a moment during the re-check is waited for, then moved aside ({heldOutcome})");

        // ---- The history shown in the window: the same lines and dividers as before, from a pure function.
        var history = new List<ChatLine>
        {
            Said("scene", 1, "At the docks."), Said("player", 1, "hi"), Said("npc", 1, "hey"), Said("scene", 1, "Later, at the bar."),
            Said("player", 1, "again"), Said("npc", 2, "next day"), Said("player", 2, "ok"),
        };
        string Rows(IReadOnlyList<ChatLine> h, int max, int today) =>
            string.Join(" | ", HistoryRows.For(h, max, today).Select(r => r.kind.ToString()[0] + ":" + r.text));
        Check(Rows(history, 150, 3) == "D:Day 1 | P:hi | N:hey | D:Later | P:again | D:Day 2 | N:next day | P:ok | D:Day 3",
            "the rows: a divider per day, \"Later\" after a new meeting, and the day you're in last (" + Rows(history, 150, 3) + ")");
        Check(Rows(history, 150, 2) == "D:Day 1 | P:hi | N:hey | D:Later | P:again | D:Day 2 | N:next day | P:ok | D:Now", "...\"Now\" when the last line was today");
        Check(Rows(history, 2, 3) == "D:Day 2 | N:next day | P:ok | D:Day 3", "...only the last HistoryShown spoken lines");
        Check(Rows(history, 0, 3) == "" && Rows(new List<ChatLine> { Said("scene", 1, "x") }, 150, 3) == "", "...nothing for HistoryShown 0, or a history of scenes only");
        var opened = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        var withNow = new List<ChatLine> { Said("player", 1, "hi"), Said("npc", 1, "hey"), Said("scene", 3, "Now, at the bar."), Said("player", 3, "Hey,"), Said("npc", 3, "You again.") };
        withNow[0].Utc = opened.AddDays(-2); // older lines may not be stamped at all (default); these are
        for (int i = 2; i < withNow.Count; i++) withNow[i].Utc = opened.AddSeconds(i);
        string cut = string.Join(" | ", HistoryRows.For(withNow, 150, 3, before: opened).Select(r => r.kind.ToString()[0] + ":" + r.text));
        Check(cut == "D:Day 1 | P:hi | N:hey | D:Day 3", "lines this meeting saved after the window opened aren't history (they show below as they're said): " + cut);

        // ---- A phone call reads the partner it picked, not everyone the caller ever talked with.
        var callStore = new NpcStore(Path.Combine(dir, "perf-calls"), _ => { });
        var client = new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(new ScriptedAi()));
        var brain = new NpcBrain(client, callStore, () => new BrainSettings(), _ => { });
        var pairs = new PairBook(Path.Combine(dir, "perf-pairs.json"));
        var chatter = new ChatterBrain(brain, client, pairs, _ => { });
        NpcRecord Caller(string id) => new() { Id = id, Persona = new Persona { Name = "Rosa Diaz", Age = "41" }, Visual = new NpcVisual { Body = "woman" } };
        void Partner(string caller, string id, string name, Action<NpcRecord>? change = null)
        {
            var r = MetBefore(id, name);
            change?.Invoke(r);
            callStore.Save(r);
            pairs.Record(caller, "Rosa Diaz", id, name, 5, 1, "talked");
        }
        foreach (var (id, name) in new[] { ("p_one", "Una Berg"), ("p_two", "Tam Ruiz"), ("p_three", "Thea Kim"), ("p_four", "Fen Ode") }) Partner("p_rosa", id, name);
        int before = ReadsOf(callStore);
        var picked = chatter.PickCallee(Caller("p_rosa"), new Random(3));
        int reads = ReadsOf(callStore) - before;
        Check(before >= 0 && reads == 1 && picked?.Record != null, $"four people they've talked with: one file is read, the one called ({reads} read; {picked?.Name ?? "nobody"})");

        Partner("p_ada", "p_held", "Hal Dorn");
        Partner("p_ada", "p_free", "Fay Moor");
        var names = new List<string?>();
        using (Hold(Path.Combine(dir, "perf-calls", NpcStore.FileNameFor("p_held")), FileShare.None))
            for (int seed = 1; seed <= 8; seed++) names.Add(chatter.PickCallee(Caller("p_ada"), new Random(seed))?.Name);
        Check(names.All(n => n == "Fay Moor"), "a partner whose file can't be read right now: another is called instead (" + string.Join(", ", names) + ")");

        Partner("p_bea", "p_kid", "Kit Hale");
        brain.KnownMinors.Add("p_kid");
        int kidBefore = ReadsOf(callStore);
        var kid = chatter.PickCallee(Caller("p_bea"), new Random(1));
        Check(kid == null && ReadsOf(callStore) == kidBefore, "someone on the under-18 list is never called, and their file isn't even read");
        Partner("p_cy", "p_teen", "Tia Vale", r => r.Visual.Minor = true);
        var teen = chatter.PickCallee(Caller("p_cy"), new Random(1));
        Check(teen == null && brain.KnownMinors.Contains("p_teen"), "someone whose record says under 18 is never called, and goes on the list");

        // ---- Step 6b, D3 (PERF-6): the chatter settings, built once until a setting changes.
        Console.WriteLine("-- audit 2026-10-11: chatter settings built once, not every frame (step 6b, D3)");
        int builds = 0;
        var cache = new ChatterSettingsCache(() => { builds++; return new ChatterSettings { Enabled = true, Dynamic = true, DynamicLines = 3 }.Effective(); });
        var first = cache.Now;
        var again = cache.Now;
        Check(builds == 1 && ReferenceEquals(first, again), $"asked for twice with nothing changed: built once ({builds})");
        cache.Invalidate();
        var rebuilt = cache.Now;
        Check(builds == 2 && !ReferenceEquals(first, rebuilt), "a setting changed: built again");
        var asSet = new ChatterSettings { Enabled = true, Dynamic = true, DynamicLines = 3, GroupSize = 2 };
        var effective = asSet.Effective();
        var start = effective.Copy();
        start.GroupSize = 2;
        Check(!ReferenceEquals(asSet, effective) && effective.GroupSize == 4 && asSet.GroupSize == 2,
            "Dynamic mode's settings are a copy of their own, and a start's change to its copy leaves the kept ones alone");

        // ---- Step 6c, D6/D10 (PERF-9, LEAK-4): pairs and visits written on a pool thread, the newest state always last.
        Console.WriteLine("-- audit 2026-10-11: pairs and visits saved off the main thread (step 6c, D6/D10)");
        string books = Path.Combine(dir, "perf-books");
        Directory.CreateDirectory(books);
        string visitsPath = Path.Combine(books, "visits.json");
        var visitBook = new VisitBook(visitsPath);
        visitBook.Note("p_v1", 3);
        visitBook.SaveSoon();
        Check(visitBook.Writer.WaitIdle() && new VisitBook(visitsPath).Count("p_v1") == 1, "a visit noted is in the file once the pool has written it");
        visitBook.Note("p_v2", 3);
        visitBook.SaveSoon();
        visitBook.Note("p_v3", 3);
        visitBook.SaveSoon();
        visitBook.Writer.WaitIdle();
        var visitsBack = new VisitBook(visitsPath);
        Check(visitsBack.Count("p_v2") == 1 && visitsBack.Count("p_v3") == 1, "two quick changes: both end up in the file");

        // A write in progress (its snapshot already taken) never lands after a forget or a reset.
        PairBook HeldPairs(string name, out System.Threading.ManualResetEventSlim entered, out System.Threading.ManualResetEventSlim release)
        {
            var book = new PairBook(Path.Combine(books, name));
            var e = new System.Threading.ManualResetEventSlim();
            var r = new System.Threading.ManualResetEventSlim();
            int writes = 0;
            book.Writer.BeforeWrite = () => { if (System.Threading.Interlocked.Increment(ref writes) == 1) { e.Set(); r.Wait(5000); } };
            entered = e;
            release = r;
            return book;
        }
        var forgetting = HeldPairs("forget-pairs.json", out var forgetEntered, out var forgetRelease);
        forgetting.Record("p_x", "Xi", "p_y", "Yu", 5, 2, "Rain.");
        bool forgetHeld = forgetEntered.Wait(5000);
        forgetting.Forget("p_x");
        forgetRelease.Set();
        forgetting.Writer.WaitIdle();
        Check(forgetHeld && new PairBook(Path.Combine(books, "forget-pairs.json")).Count == 0,
            "someone forgotten while an older save was being written: they're not in the file afterwards");

        var resetting = HeldPairs("reset-pairs.json", out var resetEntered, out var resetRelease);
        resetting.Record("p_x", "Xi", "p_y", "Yu", 5, 2, "Rain.");
        bool resetHeld = resetEntered.Wait(5000);
        var clearing = Task.Run(() => resetting.Clear(Path.Combine(books, "reset-backup")));
        await Task.Delay(100); // the reset waits for the write in progress
        resetRelease.Set();
        bool cleared = clearing.Wait(5000);
        resetting.Writer.WaitIdle();
        Check(resetHeld && cleared && new PairBook(Path.Combine(books, "reset-pairs.json")).Count == 0,
            "a reset while an older save is being written: the empty book is what's left in the file");

        // A change the pool hasn't written yet still goes into the reset's backup, and the pool's late write doesn't undo the reset.
        var paused = new List<Action>();
        var unwritten = new PairBook(Path.Combine(books, "unwritten-pairs.json"));
        unwritten.Writer.Schedule = paused.Add;
        unwritten.Record("p_x", "Xi", "p_y", "Yu", 5, 2, "Rain.");
        unwritten.Clear(Path.Combine(books, "unwritten-backup"));
        string unwrittenBackup = Path.Combine(books, "unwritten-backup", "unwritten-pairs.json");
        Check(File.Exists(unwrittenBackup) && new PairBook(unwrittenBackup).Count == 1, "a reset's backup holds the change the pool hadn't written yet");
        foreach (var late in paused) late();
        Check(new PairBook(Path.Combine(books, "unwritten-pairs.json")).Count == 0, "...and the pool's write, coming late, leaves the book empty");

        // 6b/6c review: a pool write that starts while a reset holds the gate takes its snapshot after the reset (inside the
        // gate), so it can't bring the old pairs back. Red if the snapshot is taken outside the gate.
        {
            string poolRacePath = Path.Combine(books, "race-pairs.json");
            var race = new PairBook(poolRacePath);
            var held = new List<Action>();
            race.Writer.Schedule = held.Add;
            race.Record("p_a", "Ana", "p_b", "Ben", 1, 1, "before the reset");
            var inClear = new System.Threading.ManualResetEventSlim();
            var letGo = new System.Threading.ManualResetEventSlim();
            int raceWrites = 0;
            race.Writer.BeforeWrite = () => { if (System.Threading.Interlocked.Increment(ref raceWrites) == 1) { inClear.Set(); letGo.Wait(5000); } };
            var resetting2 = Task.Run(() => race.Clear(Path.Combine(books, "race-backup")));
            bool entered2 = inClear.Wait(5000);
            var pool = Task.Run(() => { foreach (var d in held.ToList()) d(); }); // the pool's write starts while the reset holds the gate
            await Task.Delay(200);
            letGo.Set();
            bool done = resetting2.Wait(5000) & pool.Wait(5000);
            Check(entered2 && done && new PairBook(poolRacePath).Count == 0, "a pool write that began during a reset doesn't bring the old pairs back");
        }
        // Many threads changing the same pairs: once idle, the file is exactly what's in memory (the newest of each pair).
        {
            string stressPath = Path.Combine(books, "stress-pairs.json");
            var stress = new PairBook(stressPath);
            var workers = Enumerable.Range(0, 6).Select(t => Task.Run(() =>
            {
                var rng = new Random(t * 31 + 7);
                for (int i = 0; i < 300; i++)
                {
                    string a = "p_" + rng.Next(10), b = "p_" + rng.Next(10);
                    if (rng.Next(20) == 0) stress.Forget(a);
                    else stress.Record(a, a, b, b, rng.Next(-5, 6), i, "x" + i);
                }
            })).ToArray();
            Task.WaitAll(workers);
            bool idle = stress.Writer.WaitIdle(5000);
            var disk = new PairBook(stressPath);
            bool same = disk.Count == stress.Count && stress.AmongThese(Enumerable.Range(0, 10).Select(i => "p_" + i).ToList())
                .All(m => disk.Get(m.A, m.B) is { } d && d.Times == m.Times && d.Affinity == m.Affinity && d.LastTalk == m.LastTalk);
            Check(idle && same, $"six threads changing the same pairs: once idle, the file holds the newest of each ({disk.Count} on disk, {stress.Count} in memory)");
        }
        Check(visitBook.Writer.WaitIdle(), "(the visit writer went idle)");
    }
}
