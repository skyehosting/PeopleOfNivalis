using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using NivalisAIChat.Voice;

namespace NivalisAIChat.Tests;

/// <summary>
/// Step 3 of the audit (B1 + B8 + A4): who owns a person's record while you talk to them, and what a reset drops.
/// Each case stages its race deterministically (gates on the fake AI, or on the person's queue), and runs on its own:
/// an exception or a hang fails that case with its title instead of stopping the suite.
/// </summary>
internal static partial class Program
{
    /// <summary>A fake AI that knows which kind of request it got, and can hold any kind at a gate.</summary>
    private sealed class ScriptedAi : HttpMessageHandler
    {
        public readonly ConcurrentQueue<(string kind, JsonObject body)> Seen = new();
        public readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> Gates = new();
        public readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> Entered = new();
        public string LookAge = "40";
        public string Reply = "Yeah?";
        /// <summary>The next chat answer asks to remember this (the model's own "recall"), once.</summary>
        public string RecallOnce = "";
        /// <summary>Who the AI says someone new is, instead of the usual persona.</summary>
        public string? PersonaOverride;

        /// <summary>Ann Lee and Ben Cole, two lines and a goodbye.</summary>
        public string TalkScript = "{\"lines\":[{\"speaker\":\"Ann\",\"to\":\"Ben\",\"say\":\"Long shift?\",\"leaving\":false}," +
                                   "{\"speaker\":\"Ben\",\"to\":\"Ann\",\"say\":\"The longest. I'd better get going.\",\"leaving\":true}," +
                                   "{\"speaker\":\"Ann\",\"to\":\"\",\"say\":\"See you.\",\"leaving\":true}]," +
                                   "\"getAlong\":[{\"a\":\"Ann\",\"b\":\"Ben\",\"change\":5}],\"summary\":\"Long shifts at the docks.\",\"heardAboutPlayer\":[]}";

        public string PhoneCall = "{\"lines\":[{\"side\":\"other\",\"say\":\"Hey, you there?\"},{\"side\":\"caller\",\"say\":\"Yeah. What's up?\"}," +
                                  "{\"side\":\"other\",\"say\":\"Rent's going up again.\"},{\"side\":\"caller\",\"say\":\"Ugh. Talk later.\"}]," +
                                  "\"summary\":\"Rent is going up again.\",\"change\":2}";

        /// <summary>FB-34: staff taking an order, every turn written.</summary>
        public string OrderTalk = "{\"ask\":\"What can I get you?\",\"order\":\"The {dish}, please.\",\"not_ready\":\"Give me a minute.\",\"after_not_ready\":\"Take your time.\",\"ready\":\"Okay, the {dish}.\",\"after_order\":\"Back in a bit.\",\"left\":\"Okay then.\",\"staff_tone\":\"content\",\"customer_tone\":\"tired\"}";

        public List<string> Kinds => Seen.Select(x => x.kind).ToList();

        public TaskCompletionSource<bool> Hold(string kind)
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Gates[kind] = gate;
            Entered[kind] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return gate;
        }

        public static string KindOf(JsonObject body)
        {
            string sys0 = body["system"] is JsonArray a && a.Count > 0 ? a[0]?["text"]?.GetValue<string>() ?? "" : "";
            string messages = body["messages"]?.ToJsonString() ?? "";
            if (messages.Contains("Create the resident") || messages.Contains("Flesh out ")) return "persona"; // someone new; a story character
            if (sys0.StartsWith("You describe how a person in a picture looks")) return "look";
            if (sys0.StartsWith("You quietly narrate ordinary lives")) return "life";
            if (sys0.StartsWith("You keep the private diary")) return "diary";
            if (sys0.StartsWith("You keep the private memory")) return "fold";
            if (sys0.StartsWith("You write short conversations")) return "talk";
            if (sys0.StartsWith("You write ordinary phone calls")) return "phone";
            if (sys0.StartsWith("You write the few words a member of staff")) return "order"; // FB-34
            if (messages.Contains("Answer with only the words you say out loud")) return "greet";
            return "chat";
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            string kind = KindOf(body);
            Seen.Enqueue((kind, body));
            if (Entered.TryGetValue(kind, out var entered)) entered.TrySetResult(true);
            if (Gates.TryGetValue(kind, out var gate)) await gate.Task.WaitAsync(ct);
            string recall = "";
            if (kind == "chat" && RecallOnce.Length > 0) { recall = RecallOnce; RecallOnce = ""; }
            string text = kind switch
            {
                "persona" => PersonaOverride ?? PersonaJson,
                "look" => "{\"appearance\":\"A tall man in a grey coat and boots.\",\"style\":\"plain\",\"distinctiveMarks\":\"\",\"apparentAge\":\"" + LookAge + "\"}",
                "life" => "{\"occupation\":\"unchanged\",\"workplace\":\"unchanged\",\"livesIn\":\"unchanged\",\"shortTermGoal\":\"unchanged\",\"longTermGoal\":\"unchanged\"," +
                          "\"currentWorry\":\"unchanged\",\"temperament\":\"unchanged\",\"moodReason\":\"\",\"events\":[\"Fixed a leak in the kitchen.\"]}",
                "diary" => "{\"diary\":\"We talked about the rain and the docks.\",\"gossip\":\"The newcomer asks a lot of questions about the docks.\",\"told\":[]}",
                "fold" => "Long ago we talked about many small things, mostly the weather and the work down at the docks.",
                "talk" => TalkScript,
                "phone" => PhoneCall,
                "order" => OrderTalk,
                "greet" => "Morning.",
                _ => "{\"say\":\"" + (recall.Length > 0 ? "" : Reply) + "\",\"mood\":\"content\",\"moodIntensity\":\"mild\",\"moodReason\":\"\",\"affinityChange\":0,\"learnedPlayerName\":\"\",\"recall\":\"" + recall + "\"," +
                     "\"wantsFood\":\"\",\"wantsFoodHow\":\"\",\"introducedAs\":\"\",\"sharedIdentity\":false,\"toldAboutMe\":[],\"leaving\":false}",
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AnthropicText(text), Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>Holds a person's queue until <paramref name="release"/> completes (the private Serialized, through reflection).</summary>
    private static Task<bool> HoldQueue(NpcBrain brain, string id, Task release) =>
        (Task<bool>)typeof(NpcBrain).GetMethod("Serialized", BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(typeof(bool))
            .Invoke(brain, new object[] { id, (Func<Task<bool>>)(async () => { await release.ConfigureAwait(false); return true; }) })!;

    /// <summary>One lifecycle case: an exception, or no end within a minute, fails it with its title; the rest still run.</summary>
    private static async Task Case(string title, Func<Task> body)
    {
        Task run;
        try { run = body(); }
        catch (Exception e) { Check(false, $"{title}: threw {e.GetType().Name}: {e.Message}"); return; }
        if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false) != run)
        {
            Check(false, $"{title}: didn't finish within a minute");
            return;
        }
        try { await run.ConfigureAwait(false); }
        catch (Exception e) { Check(false, $"{title}: threw {e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>Waits for something a case set up (a request reaching the fake AI) for 10 s at most: a regression fails instead of hanging.</summary>
    private static async Task Arrive(Task what, string waitingFor)
    {
        try { await what.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException) { throw new TimeoutException("nothing after 10 s: " + waitingFor); }
    }

    private static NpcRecord MetBefore(string id, string name = "Bo Reyes") => new()
    {
        Id = id,
        Persona = new Persona { Name = name, Age = "40", Occupation = "rig worker", Temperament = "dry" },
        Visual = new NpcVisual { Body = "man" },
        State = new NpcState { TimesMet = 1 },
        LastLifeUpdateGameDay = 5,
        LastLifeUpdateUtc = DateTime.UtcNow,
    };

    private static bool HasThought(NpcStore store, string id, string about) =>
        store.Load(id)?.Talks.Any(t => t.Kind == Talk.Thought && t.Summary == about) == true;

    private static bool TalkedWith(NpcStore store, string id, string name) =>
        store.Load(id)?.LifeLog.Any(e => e.Text.StartsWith("Talked with " + name)) == true;

    private static bool OnThePhoneWith(NpcStore store, string id, string name) =>
        store.Load(id)?.Talks.Any(t => t.Kind == Talk.Phone && t.With == name) == true;

    private static async Task LifecycleTests(string dir)
    {
        Console.WriteLine("-- audit 2026-10-11: who owns a record while you talk, and what a reset drops (step 3)");
        var quiet = new BrainSettings { LifeUpdates = false, MeetingSummaries = false, NpcsCanLeave = false };
        var snap = new GameSnapshot { GameDay = 5, Hour = 12 };
        var bo = new NpcVisual { Body = "man" };

        (NpcBrain brain, ChatterBrain chatter, NpcStore store, ScriptedAi ai, PairBook pairs) Town(string name, BrainSettings? cfg = null)
        {
            var ai = new ScriptedAi();
            var client = new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(ai));
            var store = new NpcStore(Path.Combine(dir, name), _ => { });
            var b = new NpcBrain(client, store, () => cfg ?? quiet, _ => { });
            var pairs = new PairBook(Path.Combine(dir, name + "-pairs.json"));
            return (b, new ChatterBrain(b, client, pairs, _ => { }), store, ai, pairs);
        }

        (NpcBrain brain, ChatterBrain chatter, NpcStore store, ScriptedAi ai) Setup(string name, BrainSettings? cfg = null)
        {
            var (b, chatter, store, ai, _) = Town(name, cfg);
            return (b, chatter, store, ai);
        }

        // 1. RACE-1: what happens to them while you talk is kept, after your next line and after the meeting ends.
        await Case("a thought they had while you talked", async () =>
        {
            var (brain, chatter, store, _) = Setup("life-1");
            store.Save(MetBefore("p_bo"));
            var (s, _, err) = await brain.BeginAsync("p_bo", bo, snap, CancellationToken.None);
            await chatter.RememberThoughtAsync("p_bo", snap, new[] { "Rent's up." }, "the rent");
            await brain.SayAsync(s!, "hi", snap, CancellationToken.None);
            bool afterLine = HasThought(store, "p_bo", "the rent");
            brain.End(s!);
            await brain.Settled("p_bo");
            Check(s != null && err == null && afterLine && HasThought(store, "p_bo", "the rent") && !brain.IsLive("p_bo"),
                  "a thought they had while you talked is kept after your next line and after the meeting (RACE-1)");
        });

        // 2. Esc while they're being created: nothing is left holding their record, and the next chat starts normally (a guard).
        await Case("Esc while they're being created", async () =>
        {
            var (brain, _, _, ai) = Setup("life-2");
            var gate = ai.Hold("persona");
            using var esc = new CancellationTokenSource();
            var begin = brain.BeginAsync("p_new", bo, snap, esc.Token);
            await Arrive(ai.Entered["persona"].Task, "who they are being written");
            esc.Cancel();
            bool cancelled = false;
            try { await begin; } catch (OperationCanceledException) { cancelled = true; }
            bool freed = !brain.IsLive("p_new");
            gate.TrySetResult(true);
            ai.Gates.Clear();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var (again, _, againErr) = await brain.BeginAsync("p_new", bo, snap, CancellationToken.None);
            Check(cancelled && freed && clock.Elapsed.TotalSeconds < 5 && again != null && againErr == null && brain.IsLive("p_new"),
                  "Esc while they're being created: nothing left holding their record, and the next chat starts at once");
            if (again != null) brain.End(again);
            await brain.Settled("p_new");
        });

        // 3. /resetnpc in the middle of a chat: nothing of the meeting is saved after it, even by a reply still on its way.
        await Case("/resetnpc mid-chat", async () =>
        {
            var (brain, _, store, ai) = Setup("life-3");
            store.Save(MetBefore("p_reset"));
            var (s, _, _) = await brain.BeginAsync("p_reset", bo, snap, CancellationToken.None);
            var gate = ai.Hold("chat");
            var say = brain.SayAsync(s!, "remember this", snap, CancellationToken.None);
            await Arrive(ai.Entered["chat"].Task, "their reply being asked for");
            s!.Ended = true; // what /resetnpc does, in its order
            brain.NewEpoch("p_reset");
            await brain.Release(s, ReleaseMode.Discard);
            gate.TrySetResult(true); // the reply arrives anyway
            await say;
            brain.End(s);
            await brain.Settled("p_reset");
            var file = store.Load("p_reset")!;
            Check(!file.History.Any(l => l.Text == "remember this") && !brain.IsLive("p_reset"), "/resetnpc mid-chat: a reply still on its way saves nothing");
        });

        // 4. A reset while the meeting is starting (their first line on its way): nothing of it is saved after the reset.
        await Case("a reset while the meeting starts", async () =>
        {
            var (brain, _, store, ai) = Setup("life-4");
            store.Save(MetBefore("p_midbegin"));
            ai.Reply = "Who's asking?";
            var gate = ai.Hold("chat");
            var begin = brain.BeginAsync("p_midbegin", bo, snap, CancellationToken.None);
            await Arrive(ai.Entered["chat"].Task, "their first line being asked for");
            brain.NewEpoch("p_midbegin");
            gate.TrySetResult(true);
            var (s, _, _) = await begin;
            if (s != null) { s.Ended = true; await brain.Release(s, ReleaseMode.Discard); }
            await brain.Settled("p_midbegin");
            Check(!store.Load("p_midbegin")!.History.Any(l => l.Text == "Who's asking?"), "a reset while the meeting starts: their first line isn't saved after it");
        });

        // 5. Starting a chat with someone met before (no picture) while they remember something: both kept.
        await Case("a chat starting while they remember", async () =>
        {
            var (brain, chatter, store, ai) = Setup("life-5");
            store.Save(MetBefore("p_both"));
            var gate = ai.Hold("chat");
            var begin = brain.BeginAsync("p_both", bo, snap, CancellationToken.None);
            await Arrive(ai.Entered["chat"].Task, "their first line being asked for");
            await chatter.RememberThoughtAsync("p_both", snap, new[] { "Late again." }, "being late");
            gate.TrySetResult(true);
            var (s, opening, _) = await begin;
            brain.End(s!);
            await brain.Settled("p_both");
            var file = store.Load("p_both")!;
            Check(opening is { Ok: true } && HasThought(store, "p_both", "being late") && file.History.Any(l => l.Role == "npc" && l.Text == "Yeah?"),
                  "a chat starting while they remember something: both are kept");
        });

        // 6. Something remembered while their reply is on its way is added at that reply's save.
        await Case("something remembered while their reply", async () =>
        {
            var (brain, chatter, store, ai) = Setup("life-6");
            store.Save(MetBefore("p_inflight"));
            var (s, _, _) = await brain.BeginAsync("p_inflight", bo, snap, CancellationToken.None);
            var gate = ai.Hold("chat");
            var say = brain.SayAsync(s!, "how's work?", snap, CancellationToken.None);
            await Arrive(ai.Entered["chat"].Task, "their reply being asked for");
            await chatter.RememberThoughtAsync("p_inflight", snap, new[] { "Shift's at six." }, "the shift");
            bool waitingMeanwhile = !HasThought(store, "p_inflight", "the shift");
            gate.TrySetResult(true);
            await say;
            Check(waitingMeanwhile && HasThought(store, "p_inflight", "the shift") && store.Load("p_inflight")!.History.Any(l => l.Text == "how's work?"),
                  "something remembered while their reply is on its way is saved with that reply");
            brain.End(s!);
            await brain.Settled("p_inflight");
        });

        // 7. The diary, the summary of old lines and two things remembered around the end of a meeting: everything lands.
        //    One is queued while their queue is busy as the meeting ends (it's added at the meeting's last save); the other
        //    comes while the diary is being written, after the meeting let go (the diary used to save over it).
        await Case("diary, summary and two memories", async () =>
        {
            var cfg = new BrainSettings { LifeUpdates = false, MeetingSummaries = true, NpcsCanLeave = false, SummarizeAfter = 20, RecentMessages = 6 };
            var (brain, chatter, store, ai) = Setup("life-7", cfg);
            var old = MetBefore("p_three");
            for (int i = 0; i < 30; i++)
                old.History.Add(new ChatLine { Role = i % 2 == 0 ? "player" : "npc", Text = "old line " + i, GameDay = 1, Utc = DateTime.UtcNow.AddMinutes(-100 + i) });
            store.Save(old);
            var (s, _, _) = await brain.BeginAsync("p_three", bo, snap, CancellationToken.None);
            await brain.SayAsync(s!, "about the docks", snap, CancellationToken.None);
            var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = HoldQueue(brain, "p_three", hold.Task);
            var before = chatter.RememberThoughtAsync("p_three", snap, new[] { "Tide's turning." }, "the tide");
            var diaryGate = ai.Hold("diary");
            brain.End(s!);
            hold.TrySetResult(true);
            await holder;
            await before;
            await Arrive(ai.Entered["diary"].Task, "the diary being written");
            await chatter.RememberThoughtAsync("p_three", snap, new[] { "Boots need mending." }, "the boots");
            diaryGate.TrySetResult(true);
            await brain.Settled("p_three");
            var file = store.Load("p_three")!;
            int unfoldedLeft = file.History.Count(l => !l.Folded);
            bool oldestFolded = file.History.Where(l => l.Text.StartsWith("old line ")).Take(10).All(l => l.Folded);
            Check(HasThought(store, "p_three", "the tide") && HasThought(store, "p_three", "the boots") && file.Meetings.Any(m => m.Summary.Contains("rain"))
                  && file.MemorySummary.Contains("docks") && oldestFolded && unfoldedLeft is >= 10 and <= 20,
                  $"diary, summary and two memories around the end of a meeting all land; the oldest lines are the ones folded ({unfoldedLeft} left unfolded)");
        });

        // 8. While you talk to them: a passing greeting writes nothing, and residents' talk gets a copy of their last save.
        await Case("while you talk to them: no passing greeting", async () =>
        {
            var (brain, _, store, ai) = Setup("life-8");
            store.Save(MetBefore("p_greet"));
            var (s, _, _) = await brain.BeginAsync("p_greet", bo, snap, CancellationToken.None);
            int asked = ai.Seen.Count;
            string path = Path.Combine(dir, "life-8", NpcStore.FileNameFor("p_greet"));
            byte[] saved = File.ReadAllBytes(path);
            var line = await brain.BarkAsync("p_greet", "You walk past them.", "Hey.", "content", snap, true, CancellationToken.None);
            var copy = await brain.EnsurePersonAsync("p_greet", bo, snap, CancellationToken.None);
            await brain.Settled("p_greet");
            bool untouched = File.ReadAllBytes(path).AsSpan().SequenceEqual(saved);
            Check(line == null && ai.Seen.Count == asked && copy != null && !ReferenceEquals(copy, s!.Record) && copy.State.TimesMet == s.Record.State.TimesMet && untouched,
                  "while you talk to them: no passing greeting, and residents' talk gets a copy of their last save (not the meeting's own record); nothing is saved");
            brain.End(s!);
            await brain.Settled("p_greet");
        });

        // 9. Waiting for someone's work from inside their own queue is refused (it would wait for itself).
        await Case("Settled from inside", async () =>
        {
            var (brain, _, _, _) = Setup("life-9");
            Exception? refused = null;
            var work = (Task<bool>)typeof(NpcBrain).GetMethod("Serialized", BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(typeof(bool))
                .Invoke(brain, new object[] { "p_self", (Func<Task<bool>>)(async () =>
                {
                    try { await brain.Settled("p_self"); } catch (InvalidOperationException e) { refused = e; }
                    return true;
                }) })!;
            bool finished = await Task.WhenAny(work, Task.Delay(5000)) == work;
            Check(finished && refused != null, "Settled from inside someone's own queue is refused instead of waiting for itself");
        });

        // 10. RACE-6: a voice being chosen while /resetnpc forgets them isn't saved for them afterwards.
        await Case("a voice being chosen while /resetnpc", async () =>
        {
            var voices = new GatedVoices();
            var book = new VoiceBook(Path.Combine(dir, "life-10-voices.json"));
            var slot = new VoiceSlot { Number = 1, Provider = VoiceProvider.ElevenLabs, Chance = 1, ApiKey = "xi" };
            var svc = new VoiceService(() => new VoiceSettings { Enabled = true, Slots = { slot } }, book, _ => { }, new HttpMessageInvoker(voices));
            var who = new VoiceSubject { Id = "p_voice", Presentation = "female" };
            var speak = svc.SpeakAsync(who, "Hi.", CancellationToken.None);
            await Arrive(voices.ListEntered.Task, "the voice list being asked for");
            svc.Forget("p_voice");
            voices.ListGate.TrySetResult(true);
            await speak;
            Check(book.Get("p_voice") == null, "a voice being chosen while /resetnpc forgets them isn't saved (RACE-6)");
        });

        // 11. /resetnpc drops only that person's work in flight; /resetall drops everyone's.
        await Case("/resetnpc drops only that person", async () =>
        {
            var (brain, chatter, store, _) = Setup("life-11");
            store.Save(MetBefore("p_a", "Ann Lee"));
            store.Save(MetBefore("p_b", "Ben Cole"));
            long writtenA = brain.EpochFor("p_a"), writtenB = brain.EpochFor("p_b");
            brain.NewEpoch("p_b");
            await chatter.RememberThoughtAsync("p_a", snap, new[] { "Kettle's on." }, "the kettle", writtenA);
            await chatter.RememberThoughtAsync("p_b", snap, new[] { "Stove's off." }, "the stove", writtenB);
            bool keptForOther = HasThought(store, "p_a", "the kettle"), droppedForThem = !HasThought(store, "p_b", "the stove");
            long againA = brain.EpochFor("p_a"), againB = brain.EpochFor("p_b");
            brain.NewEpoch();
            await chatter.RememberThoughtAsync("p_a", snap, new[] { "Where's my coat?" }, "the coat", againA);
            await chatter.RememberThoughtAsync("p_b", snap, new[] { "Bus is late." }, "the bus", againB);
            Check(keptForOther && droppedForThem && !HasThought(store, "p_a", "the coat") && !HasThought(store, "p_b", "the bus"),
                  "/resetnpc drops only that person's memory in flight, not someone else's; /resetall drops everyone's (RACE-7)");
        });

        // 12. A meeting a reset let go of leaves nothing behind when it ends: no diary, no gossip, nothing saved (a guard).
        await Case("a meeting a reset let go of", async () =>
        {
            var cfg = new BrainSettings { LifeUpdates = false, MeetingSummaries = true, NpcsCanLeave = false, Gossip = true };
            var (brain, _, store, ai) = Setup("life-12", cfg);
            brain.World = new WorldMemory(Path.Combine(dir, "life-12-world.json"));
            store.Save(MetBefore("p_forgot"));
            var (s, _, _) = await brain.BeginAsync("p_forgot", bo, snap, CancellationToken.None);
            await brain.SayAsync(s!, "tell me about the docks", snap, CancellationToken.None);
            s!.Ended = true; // what /resetnpc does
            brain.NewEpoch("p_forgot");
            await brain.Release(s, ReleaseMode.Discard);
            store.Archive("p_forgot", Path.Combine(dir, "life-12-backup"));
            store.Save(MetBefore("p_forgot", "Cy New")); // someone new by the time the old meeting closes
            s.Ended = false; // even if the close arrives as if nothing had happened
            brain.End(s);
            await brain.Settled("p_forgot");
            var heard = Enumerable.Range(0, 40).SelectMany(seed => brain.World.RumorsFor("", 5, "p_other", new Random(seed), 5, 14)).ToList();
            var now = store.Load("p_forgot");
            Check(!ai.Kinds.Contains("diary") && heard.Count == 0 && now?.Persona.Name == "Cy New" && now.History.Count == 0,
                  "a meeting a reset let go of leaves nothing when it ends: no diary, no gossip, nothing written over the new them");
        });

        // 13. A second chat with the same person while the first hasn't let go: it waits, and nothing queued is lost.
        await Case("a second chat with the same person", async () =>
        {
            var (brain, chatter, store, _) = Setup("life-13");
            store.Save(MetBefore("p_twice"));
            var (first, _, _) = await brain.BeginAsync("p_twice", bo, snap, CancellationToken.None);
            await chatter.RememberThoughtAsync("p_twice", snap, new[] { "Busy day." }, "a busy day");
            var secondBegin = brain.BeginAsync("p_twice", bo, new GameSnapshot { GameDay = 5, Hour = 13 }, CancellationToken.None);
            await Task.Delay(150);
            bool waited = !secondBegin.IsCompleted;
            brain.End(first!);
            var (second, _, _) = await secondBegin;
            brain.End(second!);
            await brain.Settled("p_twice");
            var file = store.Load("p_twice")!;
            Check(waited && HasThought(store, "p_twice", "a busy day") && file.State.TimesMet == 3 && second!.Record.State.MinutesSinceLastTalk == 60,
                  $"a second chat with the same person waits for the first to let go; nothing queued meanwhile is lost, and it knows when the first ended ({second!.Record.State.MinutesSinceLastTalk} min)");
        });

        // 14. Esc while they answer your "Hey,": their file has no unanswered opener.
        await Case("Esc while they answer", async () =>
        {
            var (brain, _, store, ai) = Setup("life-14");
            store.Save(MetBefore("p_esc"));
            var gate = ai.Hold("chat");
            using var esc = new CancellationTokenSource();
            var begin = brain.BeginAsync("p_esc", bo, snap, esc.Token);
            await Arrive(ai.Entered["chat"].Task, "their first line being asked for");
            esc.Cancel();
            try { await begin; } catch (OperationCanceledException) { }
            gate.TrySetResult(true);
            await brain.Settled("p_esc");
            var file = store.Load("p_esc")!;
            Check(!file.History.Any(l => l.Role == "player") && !brain.IsLive("p_esc"), "Esc while they answer your \"Hey,\": no unanswered opener in their file");
        });

        // 15. A save that fails while the chat is open (the file in use) leaves nothing stuck; the meeting still ends.
        await Case("a save that fails mid-chat", async () =>
        {
            var (brain, _, store, _) = Setup("life-15");
            store.Save(MetBefore("p_locked"));
            var (s, opening, _) = await brain.BeginAsync("p_locked", bo, snap, CancellationToken.None);
            string path = Path.Combine(dir, "life-15", NpcStore.FileNameFor("p_locked"));
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                brain.Retract(s!, opening!, withPlayerLine: true);
            brain.End(s!);
            await brain.Settled("p_locked");
            Check(!brain.IsLive("p_locked") && store.Load("p_locked")!.State.LastTalkDay == 5, "a save that fails mid-chat (file in use) leaves nothing stuck, and the meeting still ends and saves");
        });

        // 16. A4: someone met before, a picture, and a life update due: the look comes first. A child is turned away before
        //     their life moves on (no AI call about them); an adult's life moves on after the look.
        await Case("the look comes before", async () =>
        {
            var lifeCfg = new BrainSettings { LifeUpdates = true, LifeUpdateAfterGameDays = 2, MeetingSummaries = false, NpcsCanLeave = false };
            var (brain, _, store, ai) = Setup("life-16", lifeCfg);
            var youngLooking = MetBefore("p_look_kid");
            youngLooking.Visual.Model = ""; // no known adult model: the picture decides
            youngLooking.LastLifeUpdateGameDay = 1;
            store.Save(youngLooking);
            ai.LookAge = "15";
            var due = new GameSnapshot { GameDay = 9, Hour = 12 };
            var (kid, _, kidErr) = await brain.BeginAsync("p_look_kid", bo, due, CancellationToken.None, new byte[] { 1, 2, 3 });
            var kinds = ai.Kinds;
            Check(kid == null && kidErr == NpcBrain.MinorError && kinds.FirstOrDefault() == "look" && !kinds.Contains("life")
                  && store.Load("p_look_kid")!.Visual.Minor && !brain.IsLive("p_look_kid"),
                  $"the look comes before their life moves on, and a child is turned away before any AI call about them (A4: {string.Join(", ", kinds)})");
            var grown = MetBefore("p_look_adult");
            grown.LastLifeUpdateGameDay = 1;
            store.Save(grown);
            while (ai.Seen.TryDequeue(out _)) { }
            ai.LookAge = "40";
            var (adult, _, _) = await brain.BeginAsync("p_look_adult", bo, due, CancellationToken.None, new byte[] { 1, 2, 3 });
            var adultKinds = ai.Kinds;
            Check(adult != null && adultKinds.IndexOf("look") == 0 && adultKinds.IndexOf("life") == 1, $"...and an adult's life moves on after the look ({string.Join(", ", adultKinds)})");
            if (adult != null) brain.End(adult);
            await brain.Settled("p_look_adult");
        });

        // ---- the independent code review's cases (2026-10-12)

        // 17. Esc while they answer, with something they remembered waiting for the meeting: it's kept (Abort applies it to a
        //     fresh load of their file), and still no unanswered opener.
        await Case("Esc with a memory waiting", async () =>
        {
            var (brain, chatter, store, ai) = Setup("life-17");
            store.Save(MetBefore("p_esc2"));
            var gate = ai.Hold("chat");
            using var esc = new CancellationTokenSource();
            var begin = brain.BeginAsync("p_esc2", bo, snap, esc.Token);
            await Arrive(ai.Entered["chat"].Task, "their first line being asked for");
            await chatter.RememberThoughtAsync("p_esc2", snap, new[] { "Who's that?" }, "a stranger"); // waits: the meeting owns their record
            bool waiting = !HasThought(store, "p_esc2", "a stranger");
            esc.Cancel();
            try { await begin; } catch (OperationCanceledException) { }
            gate.TrySetResult(true);
            await brain.Settled("p_esc2");
            var file = store.Load("p_esc2")!;
            Check(waiting && HasThought(store, "p_esc2", "a stranger") && !file.History.Any(l => l.Role == "player") && !brain.IsLive("p_esc2"),
                  "Esc with a memory waiting: what they remembered meanwhile is kept, and there's no unanswered opener");
        });

        // 18. A reset of one resident between writing a residents' conversation and remembering it: the other one keeps it,
        //     the one reset doesn't, and how they get on doesn't change (it takes both).
        await Case("a reset between writing a conversation", async () =>
        {
            var (brain, chatter, store, _, pairs) = Town("life-18");
            store.Save(MetBefore("p_ann", "Ann Lee"));
            store.Save(MetBefore("p_ben", "Ben Cole"));
            var members = new List<ChatterMember>
            {
                new() { Id = "p_ann", Visual = new NpcVisual { Body = "woman" }, Snap = snap, MaxLines = 2 },
                new() { Id = "p_ben", Visual = new NpcVisual { Body = "man" }, Snap = snap, MaxLines = 2 },
            };
            var written = await chatter.WriteAsync(members, false, Array.Empty<string>(), Array.Empty<string>(), 4000, CancellationToken.None);
            if (written == null) { Check(false, "a reset between writing a conversation: nothing was written"); return; }
            brain.NewEpoch("p_ben"); // /resetnpc on Ben while it plays
            await chatter.RememberAsync(written, written.Lines.Count);
            Check(TalkedWith(store, "p_ann", "Ben Cole") && !TalkedWith(store, "p_ben", "Ann Lee") && pairs.Get("p_ann", "p_ben") == null,
                  "a reset between writing a conversation and remembering it: the other one keeps it, the one reset doesn't, and how they get on doesn't change");
        });

        // 19. The same for a phone call: written, then the one called is reset before it's remembered.
        await Case("a reset between writing a phone call", async () =>
        {
            var (brain, chatter, store, _, pairs) = Town("life-19");
            store.Save(MetBefore("p_ann", "Ann Lee"));
            store.Save(MetBefore("p_ben", "Ben Cole"));
            pairs.Record("p_ann", "Ann Lee", "p_ben", "Ben Cole", 10, 4, "They know each other from the docks.");
            var call = await chatter.PhoneAsync("p_ann", new NpcVisual { Body = "woman" }, snap, 2, 4000, CancellationToken.None);
            if (call == null || call.Other.Record?.Id != "p_ben") { Check(false, "a reset between writing a phone call: Ann didn't call Ben"); return; }
            brain.NewEpoch("p_ben"); // /resetnpc on Ben while the call plays
            await chatter.RememberCallAsync(call, call.Lines.Count);
            Check(OnThePhoneWith(store, "p_ann", "Ben Cole") && !OnThePhoneWith(store, "p_ben", "Ann Lee") && pairs.Get("p_ann", "p_ben")!.Times == 1,
                  "a reset between writing a phone call and remembering it: the caller keeps it, the one reset doesn't, and how they get on doesn't change");
        });

        // 20. A meeting that never lets go (it shouldn't happen) is taken over after a while: what was waiting for them is
        //     kept for the new meeting, and the old one can't save any more.
        await Case("a stuck meeting is taken over", async () =>
        {
            var (brain, chatter, store, _) = Setup("life-20");
            store.Save(MetBefore("p_stuck"));
            brain.SupersedeAfter = TimeSpan.FromMilliseconds(300);
            var (stuck, _, _) = await brain.BeginAsync("p_stuck", bo, snap, CancellationToken.None); // never ended
            await chatter.RememberThoughtAsync("p_stuck", snap, new[] { "Where did they go?" }, "the stranger");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var (fresh, _, _) = await brain.BeginAsync("p_stuck", bo, new GameSnapshot { GameDay = 5, Hour = 13 }, CancellationToken.None);
            double waitedFor = clock.Elapsed.TotalSeconds;
            await brain.SayAsync(stuck!, "an old line", snap, CancellationToken.None);
            bool oldNotSaved = !store.Load("p_stuck")!.History.Any(l => l.Text == "an old line");
            brain.End(stuck!);
            brain.End(fresh!);
            await brain.Settled("p_stuck");
            var file = store.Load("p_stuck")!;
            Check(fresh != null && stuck!.Released && waitedFor is > 0.2 and < 5 && oldNotSaved && HasThought(store, "p_stuck", "the stranger")
                  && !file.History.Any(l => l.Text == "an old line") && file.State.TimesMet == 3 && !brain.IsLive("p_stuck"),
                  $"a stuck meeting is taken over after a while ({waitedFor:0.0} s): what was waiting for them is kept, and the old meeting can't save");
        });

        // 21. The next meeting with someone waits for the last one's diary, and begins with it.
        await Case("the next meeting waits for the last one's diary", async () =>
        {
            var cfg = new BrainSettings { LifeUpdates = false, MeetingSummaries = true, NpcsCanLeave = false };
            var (brain, _, store, ai) = Setup("life-21", cfg);
            store.Save(MetBefore("p_diary"));
            var (first, _, _) = await brain.BeginAsync("p_diary", bo, snap, CancellationToken.None);
            await brain.SayAsync(first!, "tell me about the docks", snap, CancellationToken.None);
            var diaryGate = ai.Hold("diary");
            brain.End(first!);
            await Arrive(ai.Entered["diary"].Task, "the diary being written");
            var secondBegin = brain.BeginAsync("p_diary", bo, new GameSnapshot { GameDay = 5, Hour = 13 }, CancellationToken.None);
            await Task.Delay(150);
            bool waited = !secondBegin.IsCompleted;
            diaryGate.TrySetResult(true);
            var (second, _, _) = await secondBegin;
            Check(waited && second?.Record.Meetings.Count == 1, "the next meeting waits for the last one's diary, and begins with it");
            if (second != null) brain.End(second);
            await brain.Settled("p_diary");
        });

        // 22. The meeting's last save fails (their file is in use): what was waiting for them isn't lost. It lands at their
        //     next change, ahead of it.
        await Case("a failed final save loses nothing", async () =>
        {
            var (brain, chatter, store, _) = Setup("life-22");
            store.Save(MetBefore("p_lockedend"));
            var (s, _, _) = await brain.BeginAsync("p_lockedend", bo, snap, CancellationToken.None);
            await chatter.RememberThoughtAsync("p_lockedend", snap, new[] { "Long day." }, "a long day"); // waits for the meeting's next save
            string path = Path.Combine(dir, "life-22", NpcStore.FileNameFor("p_lockedend"));
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                brain.End(s!);
                await brain.Settled("p_lockedend");
            }
            bool notYet = !HasThought(store, "p_lockedend", "a long day");
            await chatter.RememberThoughtAsync("p_lockedend", snap, new[] { "Finally home." }, "getting home");
            var talks = store.Load("p_lockedend")!.Talks;
            int first = talks.FindIndex(t => t.Summary == "a long day"), next = talks.FindIndex(t => t.Summary == "getting home");
            Check(notYet && first >= 0 && next > first && !brain.IsLive("p_lockedend"),
                  "a failed final save loses nothing: what was waiting for them lands at their next change, ahead of it");
        });

        // 23. A file whose stored id is an older spelling of the one the game uses (the same file): the meeting still
        //     saves and lets go.
        await Case("a file with an older spelling of their id", async () =>
        {
            var (brain, _, store, _) = Setup("life-23");
            store.Save(MetBefore("p.case")); // FileNameFor turns the dot into "_": the same file as "p_case"
            var (s, _, _) = await brain.BeginAsync("p_case", bo, snap, CancellationToken.None);
            if (s == null) { Check(false, "a file with an older spelling of their id: no meeting"); return; }
            await brain.SayAsync(s, "hello again", snap, CancellationToken.None);
            brain.End(s);
            await brain.Settled("p_case");
            Check(store.Load("p_case")?.History.Any(l => l.Text == "hello again") == true && !brain.IsLive("p_case"),
                  "a file with an older spelling of their id still saves, and the meeting lets go");
        });

        // 24. Someone found to be under 18 during the meeting gets no diary and no summary of their lines: nothing more
        //     about them goes to any AI.
        await Case("found to be under 18 during a meeting", async () =>
        {
            var cfg = new BrainSettings { LifeUpdates = false, MeetingSummaries = true, NpcsCanLeave = false, SummarizeAfter = 20, RecentMessages = 6 };
            var (brain, _, store, ai) = Setup("life-24", cfg);
            var old = MetBefore("p_young");
            for (int i = 0; i < 30; i++)
                old.History.Add(new ChatLine { Role = i % 2 == 0 ? "player" : "npc", Text = "old line " + i, GameDay = 1, Utc = DateTime.UtcNow.AddMinutes(-100 + i) });
            store.Save(old);
            var (s, _, _) = await brain.BeginAsync("p_young", bo, snap, CancellationToken.None);
            await brain.SayAsync(s!, "how old are you?", snap, CancellationToken.None);
            brain.KnownMinors.Add("p_young"); // what was said (or seen) shows they're under 18
            brain.End(s!);
            await brain.Settled("p_young");
            Check(!ai.Kinds.Contains("diary") && !ai.Kinds.Contains("fold"), "found to be under 18 during a meeting: no diary and no summary of their lines go to the AI");
        });
    }

    /// <summary>ElevenLabs, with its voice list held at a gate.</summary>
    private sealed class GatedVoices : HttpMessageHandler
    {
        public readonly TaskCompletionSource<bool> ListEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> ListGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.ToString();
            byte[] body;
            if (url.Contains("/v2/voices"))
            {
                ListEntered.TrySetResult(true);
                await ListGate.Task.WaitAsync(ct);
                body = Encoding.UTF8.GetBytes("{\"voices\":[{\"voice_id\":\"v-rachel\",\"name\":\"Rachel\",\"category\":\"premade\",\"labels\":{\"gender\":\"female\"}}],\"has_more\":false}");
            }
            else if (url.Contains("/v1/user/subscription")) body = Encoding.UTF8.GetBytes("{\"tier\":\"creator\"}");
            else body = new byte[4800];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }
    }
}
