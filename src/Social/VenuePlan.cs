using System;
using System.Collections.Generic;

namespace NivalisAIChat.Social;

/// <summary>
/// The rules of order talk at food places (staff and customers), kept apart from the game so they can be tested: which
/// exchange may come into a table's conversation, when the AI writes the order exchange (FB-34), what happens when the
/// customer moves while the staff wait (0.3.3 review of Stage 1), and which text a line plays.
/// </summary>
public static class VenuePlan
{
    /// <summary>The kinds of exchange at a food place.</summary>
    public enum Kind { Order, Serve, MealCheck, SmallTalk }

    /// <summary>
    /// What each line of an exchange is, so its words are picked when it plays (the AI's if they've come, else the
    /// written ones) and never swapped by position while lines are being added or taken out (FB-34 review, blocker 2).
    /// </summary>
    public enum Slot { Written, Ask, Order, NotReady, AfterNotReady, Ready, AfterOrder, Left, JustSitting, AfterJustSitting }

    /// <summary>The AI's words for an order exchange, by slot (empty = the written line plays).</summary>
    public sealed class Lines
    {
        public readonly Dictionary<Slot, string> Said = new();
        /// <summary>How each of them sounds (a mood word), for their voice.</summary>
        public string StaffTone = "", CustomerTone = "";
        /// <summary>Their voices' tone for these lines: their mood, moved by the tone word (set by the brain).</summary>
        public Voice.ToneState? StaffVoice, CustomerVoice;

        public string Get(Slot slot) => Said.TryGetValue(slot, out var s) ? s : "";
    }

    /// <summary>The slot of the customer's order line, the one that names the dish.</summary>
    public static bool NamesDish(Slot slot) => slot is Slot.Order or Slot.Ready;

    /// <summary>
    /// The words a line plays: the AI's for its slot when they've come (the dish filled in where the order line has
    /// {dish}), else the written line. <paramref name="dishes"/> is what the game says they ordered ("noodle soup and tea").
    /// </summary>
    public static string Choose(Slot slot, Lines? ai, string written, string dishes)
    {
        if (ai == null || slot == Slot.Written) return written;
        string said = ai.Get(slot);
        if (said.Length == 0) return written;
        if (NamesDish(slot))
        {
            if (dishes.Length == 0) return written;
            return said.Replace(DishSlot, dishes);
        }
        return said;
    }

    /// <summary>Where the dish goes in the AI's order lines.</summary>
    public const string DishSlot = "{dish}";

    /// <summary>
    /// FB-34: whether the AI writes this order exchange. Only with the setting on and AI allowed (token limit), for a
    /// customer who already has a record (nobody is created to order a coffee), never a child, only when you're close
    /// enough to see or hear it, and within the hour's chatter allowance, of which order talk takes at most a third.
    /// </summary>
    public static bool UsesAi(bool settingOn, bool aiAllowed, bool customerHasRecord, bool anyMinor, bool withinHearing,
                              int startsThisHour, int orderTalkThisHour, int perHour) =>
        settingOn && aiAllowed && customerHasRecord && !anyMinor && withinHearing
        && startsThisHour < perHour && orderTalkThisHour < Math.Max(1, perHour / 3);

    /// <summary>What happens when the staff are waiting on the customer's order and look again.</summary>
    public enum AfterWaitOutcome { CarryOn, EndQuietly, SayLeft }

    /// <summary>
    /// The customer moved while the staff waited (0.3.3, audit MEDIUM): someone who has ordered (the game has their order,
    /// or they're past ordering) did order, whatever the game did with them since, so it carries on (never "okay then");
    /// someone who walked off before anything was said just goes, quietly; only someone who walked off without ordering
    /// once it had begun gets the staff's "okay then".
    /// </summary>
    public static AfterWaitOutcome AfterWait(bool ordered, string step, bool walkedOff, int linesSaid)
    {
        if (ordered || PastOrdering(step)) return AfterWaitOutcome.CarryOn;
        if (!walkedOff) return AfterWaitOutcome.CarryOn;
        return linesSaid == 0 ? AfterWaitOutcome.EndQuietly : AfterWaitOutcome.SayLeft;
    }

    /// <summary>
    /// FB-42: someone the staff came to for an order who is only there to sit (the game's visit says they won't eat) and
    /// hasn't ordered: they tell the staff so, and the staff go on with their work.
    /// </summary>
    public static bool JustSitting(bool? wantsToEat, string? step) => wantsToEat == false && step == "WaitingToOrder";

    /// <summary>
    /// A worker is busy with YOU, so their task is the game's and no order talk takes them (a player on Nexus: "the waiter
    /// walks over to talk but simply stands there"; F-20261019-0182). You're a customer too (the game's PlayerGhost is a
    /// CustomerGhost), but never one of the people order talk reads. True when their task names your ghost; or, with
    /// their task unread (no targets), when you're right at them: within 1.4 m and in front (facing above 0.5).
    /// </summary>
    public static bool BusyWithYou(ISet<string> targets, string myGhost, float distanceToYou, float facingYou)
    {
        if (myGhost.Length > 0 && targets.Contains(myGhost)) return true;
        return targets.Count == 0 && float.IsFinite(distanceToYou) && distanceToYou <= 1.4f && facingYou > 0.5f;
    }

    /// <summary>The game's visit step says they've ordered already (waiting for it, or eating).</summary>
    public static bool PastOrdering(string? step) => step is "WaitingForOrder" or "Eating";

    /// <summary>
    /// Whether staff may take someone's order (or bring their meal) while they're in a table's conversation: only ordering
    /// and serving come into a table (a check-on or small talk waits), and only for someone held lightly (a customer
    /// mid-visit) who is sitting in that one table conversation and nothing else, the table not winding up or on a call.
    /// </summary>
    public static bool MayServeAtTable(Kind kind, bool light, bool held, bool joined, bool gone, int tablesIn, bool wrappingUp, bool callGoingOn, bool busyElsewhere) =>
        kind is Kind.Order or Kind.Serve && light && held && joined && !gone && tablesIn == 1 && !wrappingUp && !callGoingOn && !busyElsewhere;
}
