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

namespace NivalisAIChat.Tests;

/// <summary>
/// Step 4c of the audit (the author's Q3): Voyage is asked to search someone's memories only when the past comes up:
/// the player's line is about it, or the person reaches for a memory themselves ("recall"). Local word-matched recall
/// runs as before.
/// </summary>
internal static partial class Program
{
    private static int Queries(FakeVoyage voyage) => voyage.Requests.Count(r => r["input_type"]?.GetValue<string>() == "query");

    private static async Task MemoryRecallTests(string dir)
    {
        Console.WriteLine("-- audit 2026-10-11: memory search by meaning only when the past comes up (step 4c)");
        var snap = new GameSnapshot { GameDay = 9, Hour = 12 };
        var ai = new ScriptedAi();
        var client = new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(ai));
        var people = new NpcStore(Path.Combine(dir, "recall-people"), _ => { });
        var brain = new NpcBrain(client, people, () => new BrainSettings { LifeUpdates = false, MeetingSummaries = false, NpcsCanLeave = false }, _ => { });
        var voyage = new FakeVoyage();
        var index = new MemoryIndexer(new VoyageClient(() => new VoyageSettings { Key = "pa-test" }, new HttpMessageInvoker(voyage)),
            people, new MemoryStore(Path.Combine(dir, "recall-memory")), () => true, _ => { }, _ => { });
        brain.Memory = index;
        var tess = MetBefore("p_tess", "Tess Ward");
        tess.History.Add(new ChatLine { Role = "player", Text = "Wild storm tonight, huh?", GameDay = 3 });
        tess.History.Add(new ChatLine { Role = "npc", Text = "Thunder's been rattling the boats at the docks all night.", GameDay = 3 });
        tess.LifeLog.Add(new LifeEvent { GameDay = 7, Text = "The landlord raised the rent again." });
        people.Save(tess);
        await index.IndexAsync("p_tess", CancellationToken.None); // their memories are in the index (else nothing would be asked anyway)

        var (s, _, _) = await brain.BeginAsync("p_tess", new NpcVisual { Body = "woman" }, snap, CancellationToken.None);
        if (s == null) { Check(false, "memory search by meaning: no meeting"); return; }
        int before = Queries(voyage);
        await brain.SayAsync(s, "Nice weather today.", snap, CancellationToken.None);
        int plain = Queries(voyage) - before;
        before = Queries(voyage);
        await brain.SayAsync(s, "Do you remember that storm, the night we first met?", snap, CancellationToken.None);
        int past = Queries(voyage) - before;
        before = Queries(voyage);
        ai.RecallOnce = "the storm at the docks";
        await brain.SayAsync(s, "Anyway.", snap, CancellationToken.None);
        int reached = Queries(voyage) - before;
        Check(plain == 0, $"a line about nothing in the past asks Voyage nothing ({plain} queries)");
        Check(past >= 1, $"a line about the past asks Voyage what it brings back ({past} queries)");
        Check(reached >= 1, $"when they reach for a memory themselves (\"recall\"), Voyage is asked ({reached} queries)");
        brain.End(s);
        await brain.Settled("p_tess");

        // What counts as the past: memory words, time gone by, what passed between you, and anyone they know by name.
        var known = MetBefore("p_known", "Rosa Lind");
        known.Talks.Add(new Talk { Kind = Talk.InPerson, With = "Mia Castell", Summary = "the market" });
        known.Persona.People = new List<string> { "Teo — brother — on the rig" };
        string[] pastLines = { "Do you remember me?", "Last time you were angry.", "You said you'd fix it.", "How's Mia?", "Is Teo's boat back?",
                               "Are you still at the docks?", "Did you get the job?", "We talked about rent, right?", "That was ages ago." };
        string[] nowLines = { "Nice weather today.", "I went to the store.", "What do you do for work?", "Hey.", "Want some noodles?" };
        var wrongPast = pastLines.Where(t => !Recall.AboutThePast(t, known)).ToList();
        var wrongNow = nowLines.Where(t => Recall.AboutThePast(t, known)).ToList();
        Check(wrongPast.Count == 0 && wrongNow.Count == 0,
              $"what counts as the past: memory words, time gone by, what passed between you, names they know (missed: {string.Join(" | ", wrongPast)}; wrongly counted: {string.Join(" | ", wrongNow)})");

        // MemoryIndexLimit: the newest are kept, the oldest go, and only new memories inside the limit are sent to Voyage.
        int Documents(FakeVoyage v) => v.Requests.Where(r => r["input_type"]?.GetValue<string>() == "document").Sum(r => r["input"]!.AsArray().Count);
        var many = MetBefore("p_many", "Lena Holt");
        for (int day = 1; day <= 12; day++) many.LifeLog.Add(new LifeEvent { GameDay = day, Text = "Something happened on day " + day + "." });
        people.Save(many);
        int limit = 5;
        var limitedVoyage = new FakeVoyage();
        var limitedStore = new MemoryStore(Path.Combine(dir, "recall-limited"));
        var limited = new MemoryIndexer(new VoyageClient(() => new VoyageSettings { Key = "pa-test" }, new HttpMessageInvoker(limitedVoyage)),
            people, limitedStore, () => true, _ => { }, _ => { }, null, () => limit);
        await limited.IndexAsync("p_many", CancellationToken.None);
        var kept = limitedStore.Load("p_many")!.Entries;
        Check(kept.Count == 5 && kept.All(e => e.Day >= 8) && Documents(limitedVoyage) == 5,
              $"MemoryIndexLimit 5 of 12: the 5 newest are kept, and only they are sent to Voyage ({kept.Count} kept, {Documents(limitedVoyage)} sent)");
        int sentBefore = Documents(limitedVoyage);
        limit = 3;
        await limited.IndexAsync("p_many", CancellationToken.None);
        kept = limitedStore.Load("p_many")!.Entries;
        Check(kept.Count == 3 && kept.All(e => e.Day >= 10) && Documents(limitedVoyage) == sentBefore,
              "a lower limit applies at once: the oldest go, nothing new is sent");
        many.LifeLog.Add(new LifeEvent { GameDay = 13, Text = "Something new on day 13." });
        people.Save(many);
        await limited.IndexAsync("p_many", CancellationToken.None);
        kept = limitedStore.Load("p_many")!.Entries;
        Check(kept.Count == 3 && kept.Any(e => e.Day == 13) && kept.All(e => e.Day >= 11) && Documents(limitedVoyage) == sentBefore + 1,
              "a new memory comes in (sent on its own) and the oldest kept one goes");
        var unlimitedStore = new MemoryStore(Path.Combine(dir, "recall-unlimited"));
        var unlimited = new MemoryIndexer(new VoyageClient(() => new VoyageSettings { Key = "pa-test" }, new HttpMessageInvoker(new FakeVoyage())),
            people, unlimitedStore, () => true, _ => { }, _ => { });
        await unlimited.IndexAsync("p_many", CancellationToken.None);
        Check(unlimitedStore.Load("p_many")!.Entries.Count == 13, "MemoryIndexLimit 0 (the default): everything is kept");

        // A whole meeting shares one game day: with the cut inside it, running again changes nothing.
        var sameDay = MetBefore("p_sameday", "Ola Brandt");
        for (int i = 0; i < 6; i++) sameDay.LifeLog.Add(new LifeEvent { GameDay = 5, Text = "One of several things on day 5, number " + i + "." });
        people.Save(sameDay);
        var tieVoyage = new FakeVoyage();
        var tieStore = new MemoryStore(Path.Combine(dir, "recall-ties"));
        var ties = new MemoryIndexer(new VoyageClient(() => new VoyageSettings { Key = "pa-test" }, new HttpMessageInvoker(tieVoyage)),
            people, tieStore, () => true, _ => { }, _ => { }, null, () => 4);
        await ties.IndexAsync("p_sameday", CancellationToken.None);
        var firstKeys = tieStore.Load("p_sameday")!.Entries.Select(e => e.Key).OrderBy(k => k).ToList();
        int sent = Documents(tieVoyage);
        await ties.IndexAsync("p_sameday", CancellationToken.None);
        var secondKeys = tieStore.Load("p_sameday")!.Entries.Select(e => e.Key).OrderBy(k => k).ToList();
        Check(firstKeys.Count == 4 && firstKeys.SequenceEqual(secondKeys) && Documents(tieVoyage) == sent,
              "with the cut inside one day, running again sends nothing and drops nothing");
    }
}
