using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using NivalisAIChat.Voice;

namespace NivalisAIChat.Tests;

/// <summary>
/// Step 5 of the audit, E1 (ROBUST-4): a file the mod couldn't read is never written over. A person's record that
/// another program held for a moment used to read as "nobody", so a new person was written over them; the small books
/// (voices, pairs, visits, gossip, usage, settings upgrades) and the memory indexes started empty and the next save
/// replaced the file they never read. These checks use only what existed before the fix, so they ran red on it.
/// </summary>
internal static partial class Program
{
    /// <summary>Another program holding the file open (FileShare.None: nothing else can read it; Read: read but not move).</summary>
    private static FileStream Hold(string path, FileShare share) => new(path, FileMode.Open, FileAccess.Read, share);

    private static bool Same(string path, byte[] before) => File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(before);

    /// <summary>The copy kept beside it ("&lt;file&gt;.&lt;tag&gt;-&lt;time&gt;"). Not the file itself: Windows lets "x.json.*" match "x.json" too.</summary>
    private static string? KeptAside(string path) =>
        Directory.GetFiles(Path.GetDirectoryName(path)!)
                 .FirstOrDefault(f => Path.GetFileName(f).StartsWith(Path.GetFileName(path) + ".", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".tmp"));

    private static async Task FileSafetyTests(string dir)
    {
        Console.WriteLine("-- audit 2026-10-11: a file that can't be read is never written over (step 5, E1)");
        var snap = new GameSnapshot { GameDay = 4, Hour = 12 };
        BrainSettings Quiet() => new() { LifeUpdates = false, MeetingSummaries = false };

        // Someone you've met, whose file another program holds as you walk up; the lock is gone by the time the AI would
        // have answered (the real case: an antivirus or sync tool, for a moment).
        foreach (var (who, ensure) in new[] { ("p_ann_locked", false), ("p_cal_locked", true) })
        {
            var store = new NpcStore(Path.Combine(dir, "safety-" + who), _ => { });
            store.Save(MetBefore(who, ensure ? "Cal Ortiz" : "Ann Lee"));
            string path = Path.Combine(dir, "safety-" + who, NpcStore.FileNameFor(who));
            byte[] before = File.ReadAllBytes(path);
            var ai = new ScriptedAi();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ai.Entered["persona"] = entered;
            ai.Gates["persona"] = gate;
            var brain = new NpcBrain(new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(ai)), store, Quiet, _ => { });
            string outcome;
            var held = Hold(path, FileShare.None);
            try
            {
                Task<string> work = ensure
                    ? brain.EnsurePersonAsync(who, new NpcVisual { Body = "man" }, snap, CancellationToken.None).ContinueWith(t =>
                        t.IsFaulted ? "threw " + t.Exception!.InnerException!.GetType().Name : t.Result == null ? "nobody" : "a person: " + t.Result.Persona.Name)
                    : brain.BeginAsync(who, new NpcVisual { Body = "woman" }, snap, CancellationToken.None).ContinueWith(t =>
                    {
                        if (t.IsFaulted) return "threw " + t.Exception!.InnerException!.GetType().Name;
                        if (t.Result.session is { } s) { brain.End(s); return "a meeting began"; }
                        return "no meeting: " + t.Result.error;
                    });
                await Task.WhenAny(work, entered.Task, Task.Delay(10000));
                held.Dispose(); // the lock lifts
                gate.TrySetResult(true);
                outcome = await work.WaitAsync(TimeSpan.FromSeconds(20));
            }
            finally { held.Dispose(); }
            try { await brain.Settled(who).WaitAsync(TimeSpan.FromSeconds(20)); } catch { }
            string how = ensure ? "residents' chatter" : "your chat";
            Check(Same(path, before), $"{how}: a record locked for a moment isn't written over by a new person ({outcome})");
            Check(!ai.Kinds.Contains("persona"), $"{how}: ...and nobody new is asked of the AI for them");
        }

        // A damaged record is moved aside and kept (as before); one that can be read but not moved is left alone.
        {
            var store = new NpcStore(Path.Combine(dir, "safety-damaged"), _ => { });
            string path = Path.Combine(dir, "safety-damaged", NpcStore.FileNameFor("p_damaged"));
            File.WriteAllText(path, "{ \"persona\": { \"name\": ");
            var rec = store.Load("p_damaged");
            Check(rec == null && !File.Exists(path) && KeptAside(path) is { } kept && File.ReadAllText(kept).StartsWith("{ \"persona\""),
                "a damaged record is moved aside (kept) and they start afresh (guard)");

            string stuck = Path.Combine(dir, "safety-damaged", NpcStore.FileNameFor("p_stuck"));
            File.WriteAllText(stuck, "{ \"persona\": { \"name\": ");
            byte[] stuckBefore = File.ReadAllBytes(stuck);
            string result;
            using (Hold(stuck, FileShare.Read))
            {
                try { result = store.Load("p_stuck") == null ? "read as nobody" : "read"; }
                catch (IOException e) { result = "refused: " + e.GetType().Name; }
            }
            Check(result.StartsWith("refused") && Same(stuck, stuckBefore),
                $"a damaged record that can't be moved aside isn't read as \"nobody\" (that would let a new person be written over it): {result}");
        }

        // The small books: damaged = moved aside before anything is saved; locked = left alone, nothing saved over it.
        var books = new (string name, string file, Action<string> use)[]
        {
            ("voices", "voices.json", p => new VoiceBook(p).Set("p_a", new VoiceAssignment { Provider = "OpenAI", VoiceId = "nova" })),
            ("pairs", "pairs.json", p => { var b = new PairBook(p); b.Record("p_a", "Ann", "p_b", "Ben", 3, 4, "The rain."); b.Flush(); }),
            ("visits", "visits.json", p => { var v = new VisitBook(p); v.Note("p_a", 4); v.Save(); }),
            ("gossip", "world.json", p => new WorldMemory(p).AddHeadline(4, "Docks flooded", "The docks flooded overnight.")),
            ("usage", "usage.json", p => new TokenBudget(() => (1000, TokenBudget.Span.Day), p).Add(10)),
        };
        foreach (var (name, file, use) in books)
        {
            string damagedDir = Path.Combine(dir, "safety-book-damaged-" + name);
            Directory.CreateDirectory(damagedDir);
            string damaged = Path.Combine(damagedDir, file);
            File.WriteAllText(damaged, "{ \"damaged\": ");
            byte[] damagedBefore = File.ReadAllBytes(damaged);
            string damagedOutcome = "saved";
            try { use(damaged); } catch (Exception e) { damagedOutcome = "threw " + e.GetType().Name; }
            Check(KeptAside(damaged) is { } aside && File.ReadAllBytes(aside).SequenceEqual(damagedBefore),
                $"{name}: a damaged {file} is kept before anything is saved over it ({damagedOutcome})");

            string lockedDir = Path.Combine(dir, "safety-book-locked-" + name);
            Directory.CreateDirectory(lockedDir);
            string locked = Path.Combine(lockedDir, file);
            use(locked); // a real file first
            byte[] lockedBefore = File.ReadAllBytes(locked);
            string lockedOutcome = "saved";
            var held = Hold(locked, FileShare.None);
            try
            {
                Exception? built = null;
                // Read (or not) while locked; written once the lock has gone.
                var later = new List<Action>();
                try
                {
                    switch (name)
                    {
                        case "voices": { var b = new VoiceBook(locked); later.Add(() => b.Set("p_z", new VoiceAssignment { Provider = "OpenAI", VoiceId = "onyx" })); break; }
                        case "pairs": { var b = new PairBook(locked); later.Add(() => { b.Record("p_y", "Yan", "p_z", "Zed", 1, 5, "Work."); b.Flush(); }); break; }
                        case "visits": { var b = new VisitBook(locked); later.Add(() => { b.Note("p_z", 5); b.Save(); }); break; }
                        case "gossip": { var b = new WorldMemory(locked); later.Add(() => b.AddHeadline(5, "Power cut", "The lights went out.")); break; }
                        default: { var b = new TokenBudget(() => (1000, TokenBudget.Span.Day), locked); later.Add(() => b.Add(5)); break; }
                    }
                }
                catch (Exception e) { built = e; }
                held.Dispose();
                foreach (var a in later)
                {
                    try { a(); } catch (Exception e) { lockedOutcome = "threw " + e.GetType().Name; }
                }
                if (built != null) lockedOutcome = "couldn't start: " + built.GetType().Name;
            }
            finally { held.Dispose(); }
            Check(Same(locked, lockedBefore), $"{name}: a {file} that was locked when read isn't written over later ({lockedOutcome})");
        }

        // Settings upgrades: an unreadable record of what was already decided never moves the player's value again.
        {
            string data = Path.Combine(dir, "safety-upgrades");
            Directory.CreateDirectory(data);
            var first = new SettingsUpgrades(data);
            first.Decide("NPC Chatter", "ChatterPerHour", "12", "12");
            first.Save();
            string path = Path.Combine(data, SettingsUpgrades.FileName);
            byte[] before = File.ReadAllBytes(path);
            bool movedAgain;
            SettingsUpgrades again;
            using (Hold(path, FileShare.None))
            {
                again = new SettingsUpgrades(data);
                movedAgain = again.Decide("NPC Chatter", "ChatterPerHour", "12", "12");
            }
            again.Save();
            Check(!movedAgain, "settings upgrades: a record that can't be read doesn't move the player's chosen value again");
            Check(Same(path, before), "settings upgrades: ...and the record isn't written over");
        }

        // Memory indexes (Voyage): damaged = kept aside; locked = nothing is sent again, nothing written over.
        {
            string memDir = Path.Combine(dir, "safety-memory");
            var mem = new MemoryStore(memDir);
            string damaged = Path.Combine(memDir, NpcStore.FileNameFor("p_mem_damaged"));
            File.WriteAllText(damaged, "{ \"entries\": [ ");
            byte[] damagedBefore = File.ReadAllBytes(damaged);
            var empty = mem.Load("p_mem_damaged");
            mem.Save("p_mem_damaged", empty ?? new MemoryFile());
            Check(KeptAside(damaged) is { } aside && File.ReadAllBytes(aside).SequenceEqual(damagedBefore),
                "memory index: a damaged index is kept before it's made again");

            var people = new NpcStore(Path.Combine(dir, "safety-memory-people"), _ => { });
            var tess = MetBefore("p_mem_locked", "Tess Ward");
            tess.History.Add(new ChatLine { Role = "player", Text = "Wild storm tonight, huh?", GameDay = 3 });
            tess.History.Add(new ChatLine { Role = "npc", Text = "Thunder's been rattling the boats all night.", GameDay = 3 });
            people.Save(tess);
            var voyage = new FakeVoyage();
            var index = new MemoryIndexer(new VoyageClient(() => new VoyageSettings { Key = "pa-test" }, new HttpMessageInvoker(voyage)),
                people, mem, () => true, _ => { }, _ => { });
            await index.IndexAsync("p_mem_locked", CancellationToken.None);
            string indexPath = Path.Combine(memDir, NpcStore.FileNameFor("p_mem_locked"));
            byte[] indexBefore = File.ReadAllBytes(indexPath);
            int sent = voyage.Requests.Count;
            tess.LifeLog.Add(new LifeEvent { GameDay = 4, Text = "Found a stray cat." });
            people.Save(tess);
            string outcome = "done";
            using (Hold(indexPath, FileShare.None))
            {
                try { await index.IndexAsync("p_mem_locked", CancellationToken.None); }
                catch (Exception e) { outcome = "threw " + e.GetType().Name; }
            }
            Check(voyage.Requests.Count == sent, $"memory index: one that's locked isn't sent to Voyage again from scratch ({voyage.Requests.Count - sent} request(s), {outcome})");
            Check(Same(indexPath, indexBefore), "memory index: ...and isn't written over");
        }

        // What only the fixed code has: Peek and IsMinor answer at once on the main thread (no waiting, no throw), Create
        // never writes over a file, a read-only book's reset writes once its backup copy is safe, and the log is told.
        {
            var store = new NpcStore(Path.Combine(dir, "safety-peek"), _ => { });
            store.Save(MetBefore("p_peek", "Rae Quinn"));
            string path = Path.Combine(dir, "safety-peek", NpcStore.FileNameFor("p_peek"));
            byte[] before = File.ReadAllBytes(path);
            var brain = new NpcBrain(new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(new ScriptedAi())), store, Quiet, _ => { });
            var clock = System.Diagnostics.Stopwatch.StartNew();
            NpcRecord? peeked;
            string minor;
            using (Hold(path, FileShare.None))
            {
                peeked = store.Peek("p_peek");
                try { minor = brain.IsMinor("p_peek", new NpcVisual { Body = "woman" }) ? "minor" : "adult"; }
                catch (Exception e) { minor = "threw " + e.GetType().Name; }
            }
            long ms = clock.ElapsedMilliseconds;
            Check(peeked == null && minor == "adult" && ms < 150,
                $"Peek and IsMinor on a locked file answer at once, without throwing (main thread): {(peeked == null ? "nothing" : "a record")}, {minor}, {ms} ms");
            Check(store.Peek("p_peek")?.Persona.Name == "Rae Quinn", "Peek reads them once the lock has gone (guard)");

            string created;
            try { store.Create(MetBefore("p_peek", "Someone New")); created = "written"; }
            catch (NpcFileUnreadableException e) { created = e.Message.Contains("[DATA-READ]") ? "refused" : "refused (no code)"; }
            Check(created == "refused" && Same(path, before), $"Create never writes a new person over a file that's there ({created})");

            var warnings = new List<string>();
            string bookDir = Path.Combine(dir, "safety-reset");
            Directory.CreateDirectory(bookDir);
            string voices = Path.Combine(bookDir, "voices.json");
            new VoiceBook(voices).Set("p_a", new VoiceAssignment { Provider = "OpenAI", VoiceId = "nova" });
            VoiceBook book;
            using (Hold(voices, FileShare.None)) book = new VoiceBook(voices, warnings.Add);
            Check(warnings.Any(w => w.Contains("voices.json") && w.Contains("couldn't be read")), "a book that couldn't be read says so in the log");
            book.Set("p_b", new VoiceAssignment { Provider = "OpenAI", VoiceId = "onyx" });
            Check(warnings.Count(w => w.Contains("isn't saved this session")) == 1, "...and says once that it isn't saving");
            string backup = Path.Combine(bookDir, "backup");
            book.Clear(backup); // /resetall: the old file is safe in the backup now, so the reset is written
            Check(File.Exists(Path.Combine(backup, "voices.json")) && new VoiceBook(voices).Count == 0,
                "a read-only book's reset is written once its backup copy is safe (it isn't undone at the next start)");

            var damagedWarnings = new List<string>();
            string damagedVoices = Path.Combine(bookDir, "damaged-voices.json");
            File.WriteAllText(damagedVoices, "{ \"p_a\": ");
            new VoiceBook(damagedVoices, damagedWarnings.Add);
            Check(damagedWarnings.Any(w => w.Contains("damaged") && w.Contains(".unreadable-")), "a damaged book says where the old one was kept");
        }

        // The safety rule: someone flagged under 18 in their record (not on the list yet), whose file is locked, is
        // never asked of the AI as someone new.
        {
            var store = new NpcStore(Path.Combine(dir, "safety-minor"), _ => { });
            var kid = MetBefore("p_kid_locked", "Pip Moreno");
            kid.Visual.Minor = true;
            kid.Persona.Age = "12";
            store.Save(kid);
            string path = Path.Combine(dir, "safety-minor", NpcStore.FileNameFor("p_kid_locked"));
            var ai = new ScriptedAi();
            var brain = new NpcBrain(new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(ai)), store, Quiet, _ => { });
            string outcome;
            using (Hold(path, FileShare.None))
            {
                try
                {
                    var r = await brain.BeginAsync("p_kid_locked", new NpcVisual { Body = "man" }, snap, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
                    if (r.session is { } s) brain.End(s);
                    outcome = r.session != null ? "a meeting began" : "no meeting: " + r.error;
                }
                catch (Exception e) { outcome = "threw " + e.GetType().Name; }
            }
            try { await brain.Settled("p_kid_locked").WaitAsync(TimeSpan.FromSeconds(20)); } catch { }
            Check(ai.Seen.IsEmpty, $"a child's locked record: nothing at all is asked of the AI ({ai.Seen.Count} request(s); {outcome})");
            Check(store.Load("p_kid_locked") is { } still && still.Visual.Minor, "...and their record still says under 18");

            // The rest of the safety rule (review of part 2): every other way to the AI or Voyage, with the same locked record.
            var voyage = new FakeVoyage();
            var index = new MemoryIndexer(new VoyageClient(() => new VoyageSettings { Key = "pa-test" }, new HttpMessageInvoker(voyage)),
                store, new MemoryStore(Path.Combine(dir, "safety-minor-memory")), () => true, _ => { }, _ => { });
            var pairs = new PairBook(Path.Combine(dir, "safety-minor-pairs.json"));
            var caller = MetBefore("p_rosa_caller", "Rosa Diaz");
            caller.Persona.People.Clear();
            store.Save(caller);
            pairs.Record("p_rosa_caller", "Rosa", "p_kid_locked", "Pip", 5, 3, "School run.");
            var chatter = new ChatterBrain(brain, new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(ai)), pairs, _ => { });
            string bark = "", ensure = "", phone = "";
            using (Hold(path, FileShare.None))
            {
                try { bark = await brain.BarkAsync("p_kid_locked", "You see the newcomer.", "Hi.", "content", snap, true, CancellationToken.None) ?? "no line"; }
                catch (Exception e) { bark = "threw " + e.GetType().Name; }
                try { ensure = (await brain.EnsurePersonAsync("p_kid_locked", new NpcVisual { Body = "man" }, snap, CancellationToken.None)) == null ? "nobody" : "a person"; }
                catch (Exception e) { ensure = "threw " + e.GetType().Name; }
                await index.IndexAsync("p_kid_locked", CancellationToken.None);
                try { phone = (await chatter.PhoneAsync("p_rosa_caller", new NpcVisual { Body = "woman" }, snap, 4, 2000, CancellationToken.None))?.Other.Name ?? "no call"; }
                catch (Exception e) { phone = "threw " + e.GetType().Name; }
            }
            bool named = ai.Seen.Any(x => x.body.ToJsonString().Contains("Pip"));
            Check(bark == "no line", $"a passing greeting from someone whose record is locked: no line at all, so nothing is voiced from an older copy ({bark})");
            Check(ensure.StartsWith("threw"), $"residents' chatter can't take them while their record is locked ({ensure})");
            Check(voyage.Requests.Count == 0, $"their memories aren't sent to Voyage while their record is locked ({voyage.Requests.Count} request(s))");
            Check(phone != "Pip Moreno" && !named, $"a phone call doesn't ring them, and no request names them ({phone})");
            Check(ai.Seen.IsEmpty || ai.Kinds.All(k => k == "phone"), $"...and nothing about them is asked of the AI ({string.Join(",", ai.Kinds)})");
        }

        // Review of part 2: what the player sees never shows the folder, a duplicate key in usage.json is a damaged file
        // (not an exception out of the mod's start), and an unreadable settings-upgrades record says so.
        {
            var store = new NpcStore(Path.Combine(dir, "safety-path"), _ => { });
            store.Save(MetBefore("p_path", "Lou Park"));
            string path = Path.Combine(dir, "safety-path", NpcStore.FileNameFor("p_path"));
            string message = "";
            using (Hold(path, FileShare.None))
            {
                try { store.Load("p_path"); } catch (Exception e) { message = e.Message; }
            }
            Check(message.Contains("[DATA-READ]") && message.Contains(NpcStore.FileNameFor("p_path")) && !message.Contains(Path.Combine(dir, "safety-path")),
                "the [DATA-READ] message names the file but not the folder it's in (that can hold the Windows user name)");

            string usageDir = Path.Combine(dir, "safety-usage-dup");
            Directory.CreateDirectory(usageDir);
            string usage = Path.Combine(usageDir, "usage.json");
            File.WriteAllText(usage, "{\"period\":\"day 2026-10-12\",\"used\":5,\"used\":6}");
            string built;
            try { new TokenBudget(() => (1000, TokenBudget.Span.Day), usage).Add(1); built = "started"; }
            catch (Exception e) { built = "threw " + e.GetType().Name; }
            Check(built == "started" && KeptAside(usage) != null, $"usage.json with a key written twice is a damaged file, kept aside ({built})");

            string data = Path.Combine(dir, "safety-upgrades-warn");
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, SettingsUpgrades.FileName), "NPC Chatter.ChatterPerHour=12\n");
            var warned = new List<string>();
            using (Hold(Path.Combine(data, SettingsUpgrades.FileName), FileShare.None)) new SettingsUpgrades(data, warned.Add);
            Check(warned.Count == 1 && warned[0].Contains("couldn't be read"), "an unreadable settings-upgrades record says so in the log");
        }
    }
}
