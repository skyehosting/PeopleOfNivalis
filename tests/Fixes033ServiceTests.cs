using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;

namespace NivalisAIChat.Tests;

/// <summary>FB-34: staff and customers talk the order through, written by the AI.</summary>
internal static partial class Program
{
    private static async Task ServiceTalkTests(string dir)
    {
        Console.WriteLine("-- FB-34: order talk in their own words (AI), and what happens when the customer moves");

        // When the AI writes it.
        bool Uses(bool on = true, bool ai = true, bool rec = true, bool minor = false, bool near = true, int starts = 0, int orders = 0, int perHour = 75) =>
            VenuePlan.UsesAi(on, ai, rec, minor, near, starts, orders, perHour);
        Check(Uses(), "order talk is written by the AI with the setting on, AI allowed, a customer with a record, an adult, near you");
        Check(!Uses(on: false), "...not with AiOrderTalk off");
        Check(!Uses(ai: false), "...not over the token limit");
        Check(!Uses(rec: false), "...not for a customer without a record (nobody is created to order a coffee)");
        Check(!Uses(minor: true), "...never with anyone under 18");
        Check(!Uses(near: false), "...not out of your sight and hearing");
        Check(!Uses(starts: 75), "...not once the hour's chatter is used up");
        Check(!Uses(orders: 25) && Uses(orders: 24), "...and order talk takes at most a third of the hour (25 of 75)");
        Check(!Uses(perHour: 0) && Uses(perHour: 2, starts: 1, orders: 0), "...ChatterPerHour 0 means none; a small allowance still allows one");

        // The customer moved while the staff waited (audit MEDIUM, Venues.cs:351).
        Check(VenuePlan.AfterWait(true, "", walkedOff: true, linesSaid: 1) == VenuePlan.AfterWaitOutcome.CarryOn,
              "someone who ordered and was moved by the game carries on: never \"okay then\"");
        Check(VenuePlan.AfterWait(false, "WaitingForOrder", walkedOff: true, linesSaid: 1) == VenuePlan.AfterWaitOutcome.CarryOn,
              "...nor someone the game says is waiting for their food");
        Check(VenuePlan.AfterWait(false, "Eating", walkedOff: true, linesSaid: 2) == VenuePlan.AfterWaitOutcome.CarryOn, "...nor someone eating");
        Check(VenuePlan.AfterWait(false, "WaitingToOrder", walkedOff: true, linesSaid: 0) == VenuePlan.AfterWaitOutcome.EndQuietly,
              "someone who walked off before a word was said: it just ends (no \"okay then\" as the staff's first words)");
        Check(VenuePlan.AfterWait(false, "Leaving", walkedOff: true, linesSaid: 1) == VenuePlan.AfterWaitOutcome.SayLeft,
              "someone who walked off without ordering after it began: the staff say \"okay then\"");
        Check(VenuePlan.AfterWait(false, "WaitingToOrder", walkedOff: false, linesSaid: 1) == VenuePlan.AfterWaitOutcome.CarryOn, "still there, no order yet: they wait on");

        // Who may be served inside a table's conversation.
        bool Table(VenuePlan.Kind k = VenuePlan.Kind.Order, bool light = true, bool held = true, bool joined = true, bool gone = false, int tables = 1,
                   bool wrapping = false, bool call = false, bool busy = false) => VenuePlan.MayServeAtTable(k, light, held, joined, gone, tables, wrapping, call, busy);
        Check(Table() && Table(VenuePlan.Kind.Serve), "a customer sitting in a table's conversation can have their order taken or meal brought");
        Check(!Table(VenuePlan.Kind.MealCheck) && !Table(VenuePlan.Kind.SmallTalk), "...a check-on or small talk waits (it doesn't break into the table)");
        Check(!Table(light: false), "...only someone held lightly (a customer mid-visit), not someone fully held");
        Check(!Table(gone: true) && !Table(joined: false) && !Table(held: false), "...someone who left the table, or isn't in it yet, isn't");
        Check(!Table(tables: 2) && !Table(tables: 0), "...exactly one table conversation");
        Check(!Table(wrapping: true) && !Table(call: true) && !Table(busy: true), "...not while the table winds up or is on a call, nor someone busy elsewhere");

        // Which words a line plays.
        var ai = new VenuePlan.Lines();
        ai.Said[VenuePlan.Slot.Ask] = "What'll it be?";
        ai.Said[VenuePlan.Slot.Order] = "The {dish}, and quick.";
        Check(VenuePlan.Choose(VenuePlan.Slot.Ask, ai, "Hi there!", "") == "What'll it be?", "a line plays the AI's words once they've come");
        Check(VenuePlan.Choose(VenuePlan.Slot.Ask, null, "Hi there!", "") == "Hi there!", "...the written line while they haven't");
        Check(VenuePlan.Choose(VenuePlan.Slot.AfterOrder, ai, "Coming right up.", "") == "Coming right up.", "...and the written line for a turn the AI left empty");
        Check(VenuePlan.Choose(VenuePlan.Slot.Order, ai, "I'll have the noodles.", "noodles and tea") == "The noodles and tea, and quick.", "the order line gets the game's real dishes");
        Check(VenuePlan.Choose(VenuePlan.Slot.Order, ai, "written", "") == "written", "...and without a dish from the game, the written line");
        Check(VenuePlan.Choose(VenuePlan.Slot.Written, ai, "How's the food?", "noodles") == "How's the food?", "serving and check-ons stay written");

        // The request.
        var staffRec = new NpcRecord { Id = "p_waiter", Persona = new Persona { Name = "Mira Sol", Age = "34", Occupation = "waiter" }, Visual = new NpcVisual { Body = "woman" } };
        var custRec = new NpcRecord { Id = "p_diner", Persona = new Persona { Name = "Dov Kass", Age = "52" }, Visual = new NpcVisual { Body = "man" } };
        custRec.State.Mood = "grumpy";
        var pairs = new PairBook(Path.Combine(dir, "service-pairs.json"));
        pairs.Record("p_waiter", "Mira Sol", "p_diner", "Dov Kass", -30, 2, "argued about a bill");
        var req = ServicePrompts.OrderRequest(new ChatterPrompts.Person { Record = staffRec, Snap = new GameSnapshot() },
                                              new ChatterPrompts.Person { Record = custRec, Snap = new GameSnapshot { Hour = 19 } },
                                              pairs.Get("p_waiter", "p_diner"), "Ancient Spirit", atTable: true);
        string user = req.Messages[0].Content;
        Check(req.Background && req.MaxTokens <= 1500, "one small background request");
        Check(req.System.Last() == Prompts.SafetyLast, "the safety text comes last");
        Check(user.Contains("Mira Sol") && user.Contains("Dov Kass") && user.Contains("Mood: grumpy") && user.Contains("have talked 1 time") && user.Contains("argued about a bill"),
              "the request has both of them, the customer's mood and how they feel about each other");
        Check(user.Contains("Ancient Spirit") && user.Contains("table"), "...and where (the place, at their table)");
        Check(user.Contains("{dish}") && user.Contains("never name any food"), "...and leaves the dish to the code");
        var required = (req.JsonSchema?["required"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? new List<string>();
        Check(new[] { "ask", "order", "not_ready", "after_not_ready", "ready", "after_order", "left", "staff_tone", "customer_tone" }.All(required.Contains),
              $"every turn is a required field of a strict answer ({string.Join(", ", required)})");

        // Reading the answer.
        string J(string order, string ask = "Hi, what can I get you?", string left = "Okay then.") =>
            "{\"ask\":" + Json(ask) + ",\"order\":" + Json(order) + ",\"not_ready\":\"Not yet.\",\"after_not_ready\":\"Sure.\",\"ready\":\"Fine, the {dish}.\"," +
            "\"after_order\":\"Right away.\",\"left\":" + Json(left) + ",\"staff_tone\":\"Cheerful\",\"customer_tone\":\"grumpy\"}";
        var good = ServicePrompts.ParseOrder(J("The {dish}, please."), staffRec, custRec);
        Check(good != null && good.Get(VenuePlan.Slot.Order) == "The {dish}, please." && good.Get(VenuePlan.Slot.Left) == "Okay then." && good.StaffTone == "cheerful",
              "a good answer is read whole, tones as mood words");
        Check(ServicePrompts.ParseOrder(J("The {Dish}, please."), staffRec, custRec)?.Get(VenuePlan.Slot.Order) == "The {dish}, please.", "{Dish} in any case is the dish");
        Check(ServicePrompts.ParseOrder(J("The ramen, please."), staffRec, custRec)?.Get(VenuePlan.Slot.Order) == "", "an order naming food itself (no {dish}) isn't used");
        Check(ServicePrompts.ParseOrder(J("The {dish} and the {dish}."), staffRec, custRec)?.Get(VenuePlan.Slot.Order) == "", "...nor one with the dish twice");
        Check(ServicePrompts.ParseOrder(J("The {dish}.", ask: "Want the {dish}?"), staffRec, custRec)?.Get(VenuePlan.Slot.Ask) == "", "a {dish} where it doesn't belong empties that line");
        Check(ServicePrompts.ParseOrder(J("The {dish}.", ask: "Hi {name}!"), staffRec, custRec)?.Get(VenuePlan.Slot.Ask) == "", "...and so does a slot it made up");
        Check(ServicePrompts.ParseOrder(J("The {dish}.", left: new string('a', 130)), staffRec, custRec)?.Get(VenuePlan.Slot.Left) == "", "a speech instead of a few words isn't used");
        Check(ServicePrompts.ParseOrder(J("The {dish}.", ask: "As an AI language model, I can take your order."), staffRec, custRec)?.Get(VenuePlan.Slot.Ask) == "",
              "a line out of character isn't used");
        Check(ServicePrompts.ParseOrder("not json at all", staffRec, custRec) == null, "an unreadable answer: nothing (the written lines play)");

        // Written by the brain: the customer must exist already; nobody under 18; nothing after a reset.
        var talkAi = new ScriptedAi();
        var client = new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(talkAi));
        var store = new NpcStore(Path.Combine(dir, "service-store"), _ => { });
        store.Save(new NpcRecord { Id = "p_diner", Persona = new Persona { Name = "Dov Kass", Age = "52" }, Visual = new NpcVisual { Body = "man" } });
        var brain = new NpcBrain(client, store, () => new BrainSettings { LifeUpdates = false, MeetingSummaries = false }, _ => { });
        var chatter = new ChatterBrain(brain, client, new PairBook(Path.Combine(dir, "service-pairs2.json")), _ => { });
        var snap = new GameSnapshot { GameDay = 4, Hour = 19, Location = "Ancient Spirit" };
        Dictionary<string, long> Epochs() => new() { ["p_newwaiter"] = brain.EpochFor("p_newwaiter"), ["p_diner"] = brain.EpochFor("p_diner") };
        var wrote = await chatter.OrderTalkAsync("p_newwaiter", new NpcVisual { Body = "woman" }, snap, "p_diner", new NpcVisual { Body = "man" }, snap,
                                                 "Ancient Spirit", true, Epochs(), CancellationToken.None);
        Check(wrote != null && wrote.Value.lines.Get(VenuePlan.Slot.Ask) == "What can I get you?", "the order exchange is written in one request");
        Check(talkAi.Kinds.Count(k => k == "order") == 1 && talkAi.Kinds.Contains("persona"), "...one order request, the staff member created on the way");

        var stale = Epochs();
        stale["p_diner"] = stale["p_diner"] - 1; // as if they were reset after it began
        talkAi.Seen.Clear();
        var afterReset = await chatter.OrderTalkAsync("p_newwaiter", new NpcVisual { Body = "woman" }, snap, "p_diner", new NpcVisual { Body = "man" }, snap,
                                                      "Ancient Spirit", true, stale, CancellationToken.None);
        Check(afterReset == null && !talkAi.Kinds.Contains("order"), "after a reset of either of them: nothing is asked or written");

        brain.KnownMinors.Add("p_kid");
        store.Save(new NpcRecord { Id = "p_kid", Persona = new Persona { Name = "Lio Kass", Age = "12" }, Visual = new NpcVisual { Body = "man", Minor = true } });
        talkAi.Seen.Clear();
        var kid = await chatter.OrderTalkAsync("p_newwaiter", new NpcVisual { Body = "woman" }, snap, "p_kid", new NpcVisual { Body = "man", Minor = true }, snap,
                                               "Ancient Spirit", true, new Dictionary<string, long> { ["p_newwaiter"] = brain.EpochFor("p_newwaiter"), ["p_kid"] = brain.EpochFor("p_kid") }, CancellationToken.None);
        Check(kid == null && talkAi.Seen.IsEmpty, "a child's order is never sent to the AI (nothing is asked at all)");

        // What they keep.
        var said = new List<(bool staff, string text)> { (true, "What can I get you?"), (false, "Give me a minute.") };
        await chatter.RememberOrderTalkAsync("p_newwaiter", "Mira Sol", "p_diner", "Dov Kass", Epochs(), snap, "Ancient Spirit", said, "", walkedOff: true);
        await brain.Settled("p_newwaiter");
        var waiter = store.Load("p_newwaiter");
        Check(waiter != null && waiter.Talks.Any(t => t.Kind == Talk.Service && t.Summary.Contains("got up and left before ordering")),
              "a customer who walks off is remembered by the waiter");
        Check(waiter != null && waiter.LifeLog.Any(e => e.Text.StartsWith("A customer got up and left before ordering at Ancient Spirit")),
              "...as part of their day, and by no name they never learned (\"a customer\")");
        Check(chatter.Pairs.Get("p_newwaiter", "p_diner") == null, "...and no acquaintance is made of it (no pair, no phone calls between them)");
        var diner = store.Load("p_diner");
        Check(diner == null || diner.Talks.All(t => t.Kind != Talk.Service), "...the one who walked off keeps nothing of it");

        // Their own small cap: orders never push out real conversations.
        var full = new NpcRecord { Id = "p_cap" };
        for (int i = 0; i < NpcRecord.MaxTalks; i++) full.AddTalk(new Talk { Kind = Talk.InPerson, With = "Ana", Summary = "talk " + i });
        for (int i = 0; i < 6; i++) full.AddTalk(new Talk { Kind = Talk.Service, With = "a customer", Summary = "order " + i });
        Check(full.Talks.Count(t => t.Kind == Talk.InPerson) == NpcRecord.MaxTalks && full.Talks.Count(t => t.Kind == Talk.Service) == NpcRecord.MaxService,
              $"order talk keeps its own {NpcRecord.MaxService} (newest), and every conversation stays");
        Check(full.Talks.Where(t => t.Kind == Talk.Service).Select(t => t.Summary).SequenceEqual(new[] { "order 3", "order 4", "order 5" }), "...the newest ones");
    }

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);
}
