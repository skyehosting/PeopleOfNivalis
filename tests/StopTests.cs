using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using NivalisAIChat.Voice;

namespace NivalisAIChat.Tests;

/// <summary>
/// Audit step 7 (7b: LEAK-2, RACE-9, "F"): a hot reload starts a new copy on the same files, so once the old copy's Stop
/// has run it writes nothing more. Its stores, books and memory index refuse late work (said once), and indexing stops
/// mid-call. ModRuntime's own wait (the open request, closing chats, SettledAll) is Unity code: the in-game checklist.
/// </summary>
internal static partial class Program
{
    /// <summary>A Voyage stand-in that holds its first request until released (or cancelled).</summary>
    private sealed class HoldingVoyage : HttpMessageHandler
    {
        public readonly ManualResetEventSlim Started = new();
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IgnoreCancel;
        public int Requests;
        public volatile bool Canceled;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(CancellationToken.None))!.AsObject();
            Started.Set();
            try { await (IgnoreCancel ? Release.Task : Release.Task.WaitAsync(ct)).ConfigureAwait(false); }
            catch (OperationCanceledException) { Canceled = true; throw; }
            var data = new JsonArray();
            int i = 0;
            foreach (var _ in body["input"]!.AsArray())
                data.Add(new JsonObject { ["object"] = "embedding", ["embedding"] = new JsonArray(1.0, 0.0, 0.0), ["index"] = i++ });
            var resp = new JsonObject { ["object"] = "list", ["data"] = data, ["model"] = body["model"]!.GetValue<string>(), ["usage"] = new JsonObject { ["total_tokens"] = 1 } };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(resp.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    private static async Task StopTests()
    {
        Console.WriteLine("Stopping: nothing written after a hot reload's Stop (audit step 7b)");
        string dir = TestRoot.NewFolder("stop");
        Directory.CreateDirectory(dir);
        try
        {
            // A part that throws is a failed check, not the end of the run (a late write can move a file a check reads).
            async Task Part(string name, Func<Task> run)
            {
                try { await run(); }
                catch (Exception e) { Check(false, $"{name} threw {e.GetType().Name}: {e.Message}"); }
            }
            await Part("closed stores", () => StoreCloseTests(dir));
            await Part("closed books", () => { BookCloseTests(dir); return Task.CompletedTask; });
            await Part("indexing stopped", () => IndexerStopTests(dir));
            await Part("other books closed", () => { OtherBooksCloseTests(dir); return Task.CompletedTask; });
            await Part("the under-18 list merged", () => { MinorListMergeTests(dir); return Task.CompletedTask; });
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static string[] Files(string dir) => Directory.Exists(dir) ? Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(f => f).ToArray()! : Array.Empty<string>();

    /// <summary>A file's text, or a marker if it's gone or busy after a few tries (a write landing just then).</summary>
    private static string TextOf(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : "<gone>"; }
            catch (IOException) when (attempt < 4) { Thread.Sleep(20); }
            catch (IOException) { return "<busy>"; }
        }
    }

    private static async Task StoreCloseTests(string dir)
    {
        // People's files.
        var warns = new List<string>();
        string npcs = Path.Combine(dir, "npcs"), backup = Path.Combine(dir, "backup");
        var store = new NpcStore(npcs, w => { lock (warns) warns.Add(w); });
        store.Save(Person("p_kept", "40"));
        string kept = Path.Combine(npcs, NpcStore.FileNameFor("p_kept"));
        string before = File.ReadAllText(kept);
        store.Close();
        store.Save(Person("p_kept", "41"));
        store.Create(Person("p_new", "30"));
        bool archived = store.Archive("p_kept", backup);
        int archivedAll = store.ArchiveAll(backup);
        Check(TextOf(kept) == before && Files(npcs).SequenceEqual(new[] { NpcStore.FileNameFor("p_kept") }) && !archived && archivedAll == 0
              && !Directory.Exists(backup) && warns.Count == 1,
            $"a closed store writes, creates and moves nothing, no temp file is left, and it's said once ({warns.Count} said)");
        File.WriteAllText(Path.Combine(npcs, NpcStore.FileNameFor("p_bad")), "{ not json");
        bool refused = false;
        try { store.Load("p_bad"); } catch (NpcFileUnreadableException) { refused = true; }
        Check(refused && Files(npcs).Length == 2, "...and a damaged file found after it closed isn't moved aside");

        // Saves racing with Close: none lands after Close returns.
        string raceDir = Path.Combine(dir, "race");
        var race = new NpcStore(raceDir, _ => { });
        using var stopSaving = new CancellationTokenSource();
        int saves = 0;
        var savers = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (int i = 0; !stopSaving.IsCancellationRequested; i++)
            {
                var r = Person($"p_race{t}", "40", name: "Race " + i);
                try { race.Save(r); } catch { }
                Interlocked.Increment(ref saves);
            }
        })).ToArray();
        while (Volatile.Read(ref saves) < 40) await Task.Delay(5);
        race.Close();
        var atClose = Directory.GetFiles(raceDir, "*.json").ToDictionary(f => f, TextOf);
        await Task.Delay(150); // the savers carry on meanwhile
        stopSaving.Cancel();
        await Task.WhenAll(savers);
        var later = Directory.GetFiles(raceDir, "*.json").ToDictionary(f => f, TextOf);
        Check(atClose.Count == 4 && later.Count == 4 && atClose.All(kv => later.TryGetValue(kv.Key, out var now) && now == kv.Value && now != "<busy>") && Directory.GetFiles(raceDir, "*.tmp").Length == 0,
            $"saves racing with Close: none lands after it returns, and no temp file is left ({saves} saves)");

        // A save in the middle of its commit when Close comes: Close waits for it, and it lands whole.
        string heldDir = Path.Combine(dir, "held");
        var heldStore = new NpcStore(heldDir, _ => { });
        var inCommit = new ManualResetEventSlim();
        var letCommit = new ManualResetEventSlim();
        heldStore.BeforeCommit = _ => { inCommit.Set(); letCommit.Wait(5000); };
        var saving = Task.Run(() => heldStore.Save(Person("p_held", "40", name: "Held Save")));
        inCommit.Wait(5000);
        var closingStore = Task.Run(heldStore.Close);
        bool storeClosedEarly = closingStore.Wait(200);
        letCommit.Set();
        saving.Wait(5000);
        closingStore.Wait(5000);
        Check(!storeClosedEarly && TextOf(Path.Combine(heldDir, NpcStore.FileNameFor("p_held"))).Contains("Held Save"),
            "a store's Close waits for the save in the middle of its commit, which lands whole");

        // The memory index.
        var mwarns = new List<string>();
        string memory = Path.Combine(dir, "memory"), mbackup = Path.Combine(dir, "mbackup");
        var ms = new MemoryStore(memory, mwarns.Add);
        var file = new MemoryFile();
        file.Entries.Add(new MemoryEntry { Key = "k1", Text = "the storm" });
        ms.Save("p_a", file);
        string mpath = Path.Combine(memory, NpcStore.FileNameFor("p_a"));
        string mbefore = File.ReadAllText(mpath);
        long generation = ms.GenerationOf("p_a");
        ms.Close();
        file.Entries.Add(new MemoryEntry { Key = "k2", Text = "the docks" });
        bool saved = ms.Save("p_a", file, generation);
        ms.Save("p_b", file);
        ms.Archive("p_a", mbackup);
        ms.ArchiveAll(mbackup);
        File.WriteAllText(Path.Combine(memory, NpcStore.FileNameFor("p_bad")), "{ not json");
        var damaged = ms.Load("p_bad");
        Check(!saved && TextOf(mpath) == mbefore && Files(memory).Length == 2 && !Directory.Exists(mbackup) && damaged == null && mwarns.Count == 1,
            $"a closed memory store writes and moves nothing (a damaged index included), said once ({mwarns.Count} said)");
    }

    private static void BookCloseTests(string dir)
    {
        // Close writes what's waiting, then nothing more.
        var pwarns = new List<string>();
        string pairsPath = Path.Combine(dir, "pairs.json");
        var pairs = new PairBook(pairsPath, w => { lock (pwarns) pwarns.Add(w); });
        Action? held = null;
        pairs.Writer.Schedule = work => held = work; // the pool's write held back: only Close's own flush can write what's waiting
        pairs.Record("p_a", "Ann", "p_b", "Ben", 3, 4, "The rain.");
        pairs.Close(); // waits for the held pool (WaitIdle, 2 s), then writes
        string atClose = TextOf(pairsPath);
        held?.Invoke(); // the pool's late run
        pairs.Record("p_a", "Ann", "p_c", "Cy", 2, 5, "The docks.");
        pairs.Flush();
        Check(atClose.Contains("p_b") && TextOf(pairsPath) == atClose && pwarns.Count == 1,
            $"pairs: Close writes what's waiting, then nothing after it (said once; {pwarns.Count} said)");

        string visitsPath = Path.Combine(dir, "visits.json");
        var visits = new VisitBook(visitsPath, _ => { });
        visits.Note("p_a", 4);
        visits.Close();
        string vAtClose = TextOf(visitsPath);
        visits.Note("p_b", 5);
        visits.Save();
        visits.Writer.WaitIdle();
        Check(vAtClose.Contains("p_a") && TextOf(visitsPath) == vAtClose, "visits: Close writes what's waiting, then nothing after it");

        // Close waits for a write in progress, which lands whole.
        string bookPath = Path.Combine(dir, "book.json");
        var inGate = new ManualResetEventSlim();
        var letGo = new ManualResetEventSlim();
        int snapshots = 0;
        var writer = new BookWriter(bookPath, () => "{\"write\":" + Interlocked.Increment(ref snapshots) + "}", null);
        writer.BeforeWrite = () => { if (snapshots == 1) { inGate.Set(); letGo.Wait(5000); } };
        var writing = Task.Run(writer.Write);
        inGate.Wait(5000);
        var closing = Task.Run(writer.Close);
        bool closedEarly = closing.Wait(200);
        letGo.Set();
        writing.Wait(5000);
        closing.Wait(5000);
        writer.Write();
        Check(!closedEarly && TextOf(bookPath) == "{\"write\":1}" && closing.IsCompleted,
            "a book's Close waits for the write in progress (it lands whole); a write after it does nothing");
    }

    /// <summary>Gossip and news, usage, voices: written whole, so a late change would write over the new copy's (review of a0fa75f).</summary>
    private static void OtherBooksCloseTests(string dir)
    {
        var said = new List<string>();
        void Warn(string w) { lock (said) said.Add(w); }
        string worldPath = Path.Combine(dir, "world.json"), usagePath = Path.Combine(dir, "usage.json"), voicesPath = Path.Combine(dir, "voices.json");
        var world = new WorldMemory(worldPath, Warn);
        world.AddHeadline(4, "Docks flooded", "The docks flooded overnight.");
        var budget = new TokenBudget(() => (100000, TokenBudget.Span.Day), usagePath, Warn);
        budget.Add(10);
        var voices = new VoiceBook(voicesPath, Warn);
        voices.Set("p_a", new VoiceAssignment { Provider = "OpenAI", VoiceId = "nova", VoiceName = "Nova", Gender = "female" });
        string w0 = TextOf(worldPath), u0 = TextOf(usagePath), v0 = TextOf(voicesPath);
        world.Close();
        budget.Close();
        voices.Close();
        world.AddHeadline(5, "Market reopened", "The market reopened.");
        budget.Add(20);
        voices.Set("p_b", new VoiceAssignment { Provider = "OpenAI", VoiceId = "onyx", VoiceName = "Onyx", Gender = "male" });
        Check(TextOf(worldPath) == w0 && TextOf(usagePath) == u0 && TextOf(voicesPath) == v0 && w0.Contains("Docks") && said.Count == 3,
            $"a closed gossip book, usage count and voice book write nothing after Stop, each said once ({said.Count} said)");
    }

    /// <summary>Two copies of the mod (a hot reload) each add someone to the under-18 list: neither drops the other's (review of a0fa75f).</summary>
    private static void MinorListMergeTests(string dir)
    {
        string path = Path.Combine(dir, "minors", MinorList.FileName);
        var oldCopy = new MinorList(path);
        var newCopy = new MinorList(path); // read before the old copy's addition
        oldCopy.Add("p_late_old");
        newCopy.Add("p_new");
        oldCopy.Add("p_later_old");
        var onDisk = new MinorList(path);
        Check(onDisk.Contains("p_late_old") && onDisk.Contains("p_new") && onDisk.Contains("p_later_old") && newCopy.Contains("p_late_old")
              && oldCopy.Contains("p_new") && Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0,
            "two copies adding to the under-18 list: the file keeps everyone either added, each copy counts the other's, no temp file left");
    }

    private static async Task IndexerStopTests(string dir)
    {
        foreach (bool ignoresCancel in new[] { false, true })
        {
            string tag = ignoresCancel ? "an answer that comes anyway" : "a call cancelled";
            string sub = Path.Combine(dir, ignoresCancel ? "ix-late" : "ix");
            var voyage = new HoldingVoyage { IgnoreCancel = ignoresCancel };
            var client = new VoyageClient(() => new VoyageSettings { Key = "pa-test" }, new HttpMessageInvoker(voyage));
            var people = new NpcStore(Path.Combine(sub, "people"), _ => { });
            for (int i = 0; i < 4; i++)
            {
                var p = Person($"p_ix{i}", "40");
                p.AddTalk(new Talk { GameDay = 1, Clock = "12:00", Kind = Talk.InPerson, With = "Ann Lee", Summary = "talked about the storm " + i, Lines = new List<string> { "Ann: the storm" } });
                people.Save(p);
            }
            var log = new List<string>();
            string memory = Path.Combine(sub, "memory");
            var indexer = new MemoryIndexer(client, people, new MemoryStore(memory, log.Add), () => true, s => { lock (log) log.Add(s); }, _ => { });
            indexer.QueueAll(new[] { "p_ix0", "p_ix1", "p_ix2" });
            bool started = voyage.Started.Wait(5000);
            indexer.Stop();
            indexer.Queue("p_ix3");
            voyage.Release.TrySetResult(); // the held call answers (if it wasn't cancelled)
            await Task.Delay(500); // the loop would have moved on to the next person by now
            int sentByLoop = voyage.Requests, direct = -1;
            try { direct = await indexer.IndexAsync("p_ix3", CancellationToken.None); } catch { } // the held call answers at once now
            bool quiet;
            lock (log) quiet = !log.Any(l => l.Contains("failed") || l.Contains("paused"));
            Check(started && sentByLoop == 1 && Files(memory).Length == 0 && indexer.Waiting == 0 && quiet && (ignoresCancel || voyage.Canceled),
                $"indexing stopped mid-call ({tag}): no index written (a direct one after Stop included: {direct}), nobody else sent, nothing queued after, no failure logged " +
                $"({sentByLoop} sent by the loop, {Files(memory).Length} written, cancelled {voyage.Canceled})");
        }
    }
}
