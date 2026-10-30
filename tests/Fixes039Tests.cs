using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;

namespace NivalisAIChat.Tests;

/// <summary>0.3.9 FB-42: someone at a food place only to sit tells the staff, who go on with their work.</summary>
internal static partial class Program
{
    private static void Fixes039Tests()
    {
        Console.WriteLine("-- 0.3.9 FB-42: just sitting");
        Check(VenuePlan.JustSitting(false, "WaitingToOrder"), "the game has them not eating and waiting to order: just sitting");
        Check(!VenuePlan.JustSitting(true, "WaitingToOrder") && !VenuePlan.JustSitting(null, "WaitingToOrder"), "...not when they're here to eat, or it can't be read");
        Check(!VenuePlan.JustSitting(false, "WaitingForOrder") && !VenuePlan.JustSitting(false, "Eating") && !VenuePlan.JustSitting(false, ""),
              "...and only before ordering (never once an order is in, or outside a visit)");

        var ai = new VenuePlan.Lines();
        ai.Said[VenuePlan.Slot.JustSitting] = "Only resting my legs, thanks.";
        Check(VenuePlan.Choose(VenuePlan.Slot.JustSitting, ai, "Oh, I'm just sitting a while, thanks.", "") == "Only resting my legs, thanks."
              && VenuePlan.Choose(VenuePlan.Slot.AfterJustSitting, ai, "No problem, take your time.", "") == "No problem, take your time.",
              "their words when the AI wrote them, the written line for a turn it left empty");

        var staffRec = new NpcRecord { Id = "p_waiter", Persona = new Persona { Name = "Mira Sol", Age = "34", Occupation = "waiter" }, Visual = new NpcVisual { Body = "woman" } };
        var custRec = new NpcRecord { Id = "p_sitter", Persona = new Persona { Name = "Ada Venn", Age = "61" }, Visual = new NpcVisual { Body = "woman" } };
        var req = ServicePrompts.OrderRequest(new ChatterPrompts.Person { Record = staffRec, Snap = new GameSnapshot() },
                                              new ChatterPrompts.Person { Record = custRec, Snap = new GameSnapshot() }, null, "Ancient Spirit", atTable: true, justSitting: true);
        string user = req.Messages[0].Content;
        var required = (req.JsonSchema?["required"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? new List<string>();
        Check(user.Contains("only came in to sit") && !user.Contains("{dish}") && required.SequenceEqual(new[] { "ask", "just_sitting", "after_just_sitting", "staff_tone", "customer_tone" }),
              $"the request for someone just sitting: their three turns only, no dish ({string.Join(", ", required)})");
        var order = ServicePrompts.OrderRequest(new ChatterPrompts.Person { Record = staffRec, Snap = new GameSnapshot() },
                                                new ChatterPrompts.Person { Record = custRec, Snap = new GameSnapshot() }, null, "Ancient Spirit", atTable: true);
        Check(!order.Messages[0].Content.Contains("only came in to sit") && ((order.JsonSchema?["required"] as JsonArray)?.Count ?? 0) == 9, "an ordinary order's request is as it was");

        string answer = "{\"ask\":\"Evening, what can I get you?\",\"just_sitting\":\"Just sitting a while, thank you.\",\"after_just_sitting\":\"Of course, enjoy.\"," +
                        "\"staff_tone\":\"warm\",\"customer_tone\":\"calm\"}";
        var lines = ServicePrompts.ParseOrder(answer, staffRec, custRec, justSitting: true);
        Check(lines != null && lines.Get(VenuePlan.Slot.JustSitting) == "Just sitting a while, thank you." && lines.Get(VenuePlan.Slot.AfterJustSitting) == "Of course, enjoy."
              && lines.Get(VenuePlan.Slot.Order) == "", "their answer is read into the sitting turns");
        var dish = ServicePrompts.ParseOrder(answer.Replace("Just sitting a while, thank you.", "Just the {dish}."), staffRec, custRec, justSitting: true);
        Check(dish != null && dish.Get(VenuePlan.Slot.JustSitting) == "", "someone just sitting never names a dish (that line isn't used)");
    }
}
