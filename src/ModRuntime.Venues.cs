using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nivalis;
using Nivalis.GhostSystem.CustomerLoop;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using NivalisAIChat.Voice;
using UnityEngine;
using Exchange = NivalisAIChat.Social.VenuePlan.Kind;
using Slot = NivalisAIChat.Social.VenuePlan.Slot;

namespace NivalisAIChat;

/// <summary>
/// Other places that serve food (not yours): staff taking someone's order (at their table, or at the counter) with the
/// customer saying what they really ordered once the game has it; staff bringing the meal; now and then staff asking
/// someone eating how it is. Short spoken exchanges over their heads. Taking an order can be written by the AI in their
/// own words (FB-34, AiOrderTalk); the rest are written lines. Part of chatter (on with /chatter on).
/// </summary>
public static partial class ModRuntime
{
    private sealed class VenueTalk
    {
        public Character Staff = null!, Customer = null!;
        public string StaffId = "", CustomerId = "", Place = "";
        public Exchange Kind;
        /// <summary>Who says it, which turn it is (its words are picked when it plays: FB-34), and the written words for it.</summary>
        public readonly List<(bool staff, Slot slot, string text)> Lines = new();
        public int Next;
        public float NextAt, StartedAt, OrderBy;
        /// <summary>0.3.3: where the customer was when it started (gone from there = they walked off).</summary>
        public Vector3 CustomerAt;
        /// <summary>How far apart they were at the start (a counter can be wide): the staff "walked away" past more than this.</summary>
        public float StartGap;
        /// <summary>0.3.3: "give me a second": the staff wait a while for the order (or for them to walk off).</summary>
        public bool AwaitLater;
        /// <summary>Waiting for the game to show what they ordered before they say it.</summary>
        public bool AwaitOrder;
        /// <summary>FB-42: the customer is only there to sit: they say so, and the staff go on with their work afterwards.</summary>
        public bool JustSitting;
        /// <summary>Stopped (you talked to one of them, a reset, too long, under 18, you walked off, taken for a call): a voice still on its way isn't played.</summary>
        public bool Cancelled;
        /// <summary>FB-34: the AI's words for the order exchange, once they've come (main thread only); null = the written lines.</summary>
        public VenuePlan.Lines? Ai;
        public bool AiAsked;
        /// <summary>The first line waits this long for the AI's words.</summary>
        public float AiWaitUntil = -1f;
        public readonly CancellationTokenSource Cts = new();
        /// <summary>Both people's epochs when it began: nothing is kept for someone reset since.</summary>
        public readonly Dictionary<string, long> Epochs = new();
        /// <summary>FB-34: the table conversation the customer is sitting in: it waits while these lines are said.</summary>
        public Group? Table;
        /// <summary>Until when the line on show (and the moment after it) keeps the table waiting.</summary>
        public float ShowingUntil;
        /// <summary>The customer walked off without ordering: the staff said "okay then".</summary>
        public bool Left;
        /// <summary>...and the game says they're leaving (not just moving along a queue): the staff remember it (review R9).</summary>
        public bool LeftVisit;
        /// <summary>The customer's order was said out loud (review R2: only then is an order remembered).</summary>
        public bool OrderSaid;
        public bool AiUsed;
        public string Dishes = "", StaffName = "", CustomerName = "";
        public readonly List<(bool staff, string text)> Said = new();
        /// <summary>What the game says of the customer, read at most every quarter second (final review F7).</summary>
        public float ReadAt = -1f;
        public string StepNow = "";
        public List<string> DishesNow = new();
    }

    /// <summary>The customer's visit step and order as the game has them, read at most every quarter second.</summary>
    private static void Look(VenueTalk t, float now)
    {
        if (now - t.ReadAt < 0.25f && t.ReadAt >= 0f) return;
        t.ReadAt = now;
        t.StepNow = GameContext.VisitOf(t.Customer).step;
        t.DishesNow = Ordered(t.Customer);
    }

    private static readonly List<VenueTalk> VenueTalks = new();
    /// <summary>Customers already talked about (once per visit): when. By person and body (bodies get handed around; LEAK-8).</summary>
    private static readonly Dictionary<(string id, IntPtr body, Exchange kind), float> VenueHandled = new();
    /// <summary>FB-34: when order talk was given to the AI (at most a third of ChatterPerHour).</summary>
    private static readonly List<double> OrderTalkAiStarts = new();
    private static float _nextVenueLook;

    private static readonly HashSet<string> StaffActions = VenueRoles.StaffActions;
    /// <summary>Someone at a food place (staff or a customer) was near you at the last look: look more often (FB-18).</summary>
    private static bool _nearVenue;

    private static readonly string[] Greetings1 = { "Hi there! What can I get you?", "Hey. What'll it be?", "Next! What are you having?", "Hi! What can I get for you?" };
    private static readonly string[] EveningGreetings = { "Evening! What can I get you?", "Evening. What'll it be?" };
    private static readonly string[] Orders = { "I'll have the {0}, please.", "Can I get the {0}?", "{0}, please.", "Just the {0} for me." };
    private static readonly string[] NoOrder = { "Hmm... give me a second.", "Uh, let me think." };
    /// <summary>FB-34: the staff answer someone not ready yet.</summary>
    private static readonly string[] NoRush = { "Sure, take your time.", "No rush.", "Okay, I'll give you a minute." };
    /// <summary>0.3.3: the customer walked off before ordering (the author: she says "okay then").</summary>
    /// <summary>FB-42: someone only there to sit, and the staff's answer.</summary>
    private static readonly string[] JustSittingLines =
        { "Oh, I'm just sitting a while, thanks.", "Nothing for me, I'm just taking a seat for a bit.", "I'm fine, just resting my feet a minute.", "Just sitting, thanks. Don't mind me." };
    private static readonly string[] LeaveThemBe =
        { "No problem, take your time.", "Sure thing. Enjoy the quiet.", "Alright, just wave if you change your mind.", "Of course. I'll leave you to it." };
    /// <summary>FB-42: customers who told the staff they're only sitting (by person and body): when. Staff who come back are sent on again.</summary>
    private static readonly Dictionary<(string id, IntPtr body), float> Sitters = new();
    /// <summary>Sitters staff have already come back to once (the log says it the first time only).</summary>
    private static readonly HashSet<(string id, IntPtr body)> SitterReturned = new();
    /// <summary>FB-51: how many times staff came back to each sitter (said in the log at the third: the game still queues them).</summary>
    private static readonly Dictionary<(string id, IntPtr body), int> SitterReturns = new();
    /// <summary>CF-26: how long after this mod's conversation took someone in that venue talk leaves their order to the game (seconds).</summary>
    private const float PulledInFor = 600f;
    private static readonly string[] OkayThen = { "Okay then.", "Alright... another time.", "Okay, never mind then." };
    private static readonly string[] Confirms = { "Coming right up.", "Sure thing.", "Good choice. Won't be long.", "Got it, coming right up." };
    private static readonly string[] MealChecks = { "How's the {0}?", "Everything alright with the {0}?", "How's the food?" };
    private static readonly string[] MealAnswers = { "Really good, thanks.", "Not bad at all.", "It's good, thanks.", "Hits the spot." };
    /// <summary>Staff with a moment free, chatting with someone eating (FB-20: "start small talk when free with customers").</summary>
    private static readonly (string staff, string customer)[] SmallTalk =
    {
        ("Busy today, huh?", "Seems like it."),
        ("Rain's not letting up, is it?", "Never does around here."),
        ("Anything else I can get you?", "I'm good, thanks."),
        ("Need a refill?", "I'm alright, thanks."),
        ("Everything okay over here?", "Yeah, all good."),
    };
    /// <summary>Why order talk last didn't start (logged when it changes, FB-21).</summary>
    private static string _venueWhy = "";

    /// <summary>Every frame: venue exchanges playing out, and now and then a look for someone ordering or eating nearby.</summary>
    private static void TickVenues(ChatterSettings s, bool paused)
    {
        if (VenueTalks.Count > 0) // a copy only when there's something to tick (6b review)
        foreach (var t in VenueTalks.ToList())
        {
            try { TickVenueTalk(t, s, paused); }
            catch (Exception e) { Log.Debug("venue talk: " + e.Message); EndVenueTalk(t, cancel: true); } // its bubbles and any voice on its way go too
        }
        // Order talk has its own look and doesn't take places in Dynamic's people pool, which is usually full (FB-21: it never
        // came up in the author's test). Advanced looks every second, Dynamic every few (DynamicShopWeight 0 = never); two
        // at once either way.
        if (paused || ChatOpen || !_cfg.OrderTalk.Value || VenueTalks.Count >= 2 || (s.Dynamic && s.WeightShop <= 0)) return;
        float now = Time.unscaledTime;
        if (now < _nextVenueLook) return;
        _nextVenueLook = now + (s.Dynamic ? (_nearVenue ? 1.5f : 3f) : 1f); // near a food place: more often (FB-18)
        try
        {
            bool started = TryVenueTalk(s, now, out string why);
            string said = started ? "" : why;
            if (said != _venueWhy) { _venueWhy = said; if (said.Length > 0) Log.Debug("order talk: none right now (" + said + ")"); }
        }
        catch (Exception e) { Log.Debug("venue look: " + e.Message); }
    }

    /// <summary>Their role at a food place, from the game's task and body action (FB-21).</summary>
    private static VenueRoles.Role RoleOf(Character c) =>
        VenueRoles.Classify(GameContext.GhostTask(c), GameContext.ActionName(c), GameContext.IsWorkerTask(c), GameContext.IsSeated(c));

    /// <summary>
    /// FB-34: the table conversation this customer is sitting in, if staff may take their order (or bring their meal) inside
    /// it: held lightly in exactly one table's talk (not winding up, no phone call in it) and busy with nothing else (your
    /// chat, a call, a thought, other shop talk than <paramref name="except"/>).
    /// </summary>
    private static Group? AtTable(Character c, string id, VenueTalk? except = null, Exchange kind = Exchange.Order)
    {
        if (Groups.Count == 0) return null;
        var tables = Groups.Where(g => g.Table && g.Members.Any(m => !m.Gone && Same(m.C, c))).ToList();
        if (tables.Count == 0) return null;
        var g = tables[0];
        var m = g.Members.First(x => !x.Gone && Same(x.C, c));
        // One service talk at a table at a time (review R6): a second waiter waits for the first.
        bool elsewhere = ChatNpcIs(c) || Thinking(c) || (except == null && VenueTalks.Any(v => v.Table == g && !v.Cancelled))
                         || Calls.Any(k => Same(k.C, c) || Same(k.OtherC, c) || (id.Length > 0 && k.OtherId == id))
                         || (id.Length > 0 && Groups.Any(o => o.Script?.Call?.Callee.Record?.Id == id))
                         || VenueTalks.Any(t => t != except && !t.Cancelled && (Same(t.Staff, c) || Same(t.Customer, c)));
        return VenuePlan.MayServeAtTable(kind, m.Light, m.Held, m.Joined, m.Gone, tables.Count, g.WrappingUp, g.Script?.Call != null, elsewhere) ? g : null;
    }

    /// <summary>
    /// Starts one exchange between staff and a customer near you, at a food place that isn't yours (FB-21): staff taking an
    /// order (a waiter at the table, or staff at a counter) with the customer in front of them; a customer ordering at a
    /// counter with the staff nearest; staff bringing a meal; staff checking on someone eating (more often near you).
    /// </summary>
    private static bool TryVenueTalk(ChatterSettings s, float now, out string why)
    {
        why = "";
        if (!_cfg.OrderTalk.Value) { why = "OrderTalk is off"; return false; }
        if (VenueTalks.Count >= ChatterSettings.MaxAtOnce) { why = "enough going on at food places"; return false; }
        foreach (var k in VenueHandled.Where(kv => now - kv.Value > 600f).Select(kv => kv.Key).ToList()) VenueHandled.Remove(k);
        foreach (var k in Sitters.Where(kv => now - kv.Value > 600f).Select(kv => kv.Key).ToList()) { Sitters.Remove(k); SitterReturned.Remove(k); SitterReturns.Remove(k); }
        foreach (var k in PulledIn.Where(kv => now - kv.Value > PulledInFor).Select(kv => kv.Key).ToList()) PulledIn.Remove(k); // CF-26
        if (OrderTalkAiStarts.Count > 0) OrderTalkAiStarts.RemoveAll(x => now - x > 3600);
        var pc = GameContext.PlayerCharacter();
        if (pc == null) { why = "no player"; return false; }
        Vector3 me = pc.transform.position;
        // Within your chatter range, and never past the wrap-up distance (a talk out there would end on the next frame).
        float within = Mathf.Min(Mathf.Clamp(s.Range, 5f, 30f), s.WrapUpDistance);
        var people = new List<(Character c, string id, VenueRoles.Role role, Vector3 pos, Group? table)>();
        foreach (var (c, _, pos) in NearBodies(pc)) // read once a frame for every chatter scan (D2)
        {
            try
            {
                if (Flat(pos - me).magnitude > within) continue;
                var role = RoleOf(c);
                if (role == VenueRoles.Role.Other) continue;
                string id = GameContext.NpcId(c);
                // Someone already talking isn't picked (B2, FLOW-3): in shop talk too, and (by id) the far end of a phone call.
                // A customer sitting in a table's conversation can be (FB-34): the table waits while they're served.
                Group? table = null;
                if (TalkingNow(c, id) && (table = AtTable(c, id)) == null) continue;
                people.Add((c, id, role, pos, table));
            }
            catch { }
        }
        _nearVenue = people.Any(p => Flat(p.pos - me).magnitude <= 8f);
        var staff = people.Where(p => VenueRoles.IsStaff(p.role) && p.table == null).ToList();
        if (staff.Count == 0) { why = "no staff at work near you"; return false; }
        bool ownPlaceOnly = false;
        float D(Vector3 a, Vector3 b) => Flat(a - b).magnitude;
        string myGhost = GameContext.PlayerGhostId(pc);
        // Busy with you (their task is yours, or they're right at you with it unread): the game's, never order talk's (F-20261019-0182).
        bool BusyWithYou((Character c, string id, VenueRoles.Role role, Vector3 pos, Group? table) w, HashSet<string> targets)
        {
            var at = Flat(w.c.transform.forward).normalized;
            return VenuePlan.BusyWithYou(targets, myGhost, D(me, w.pos), Vector3.Dot(at, Flat(me - w.pos).normalized));
        }

        // Their own customer (review H2): the one the game's task is for, else only someone right in front of them.
        List<(Character c, string id, VenueRoles.Role role, Vector3 pos, Group? table)> TheirCustomers((Character c, string id, VenueRoles.Role role, Vector3 pos, Group? table) w, Func<VenueRoles.Role, bool> fits)
        {
            var targets = GameContext.WaiterTargets(w.c);
            // Their task is for you (a player, Nexus: "the waiter walks over to talk but simply stands there"; F-20261019-0182):
            // you're never in `people`, so the fallback below picked the resident beside you and held your waiter. Yours is the game's.
            if (BusyWithYou(w, targets)) return new List<(Character c, string id, VenueRoles.Role role, Vector3 pos, Group? table)>();
            var mine = people.Where(p => fits(p.role) && p.id != w.id);
            if (targets.Count > 0)
            {
                var theirs = mine.Where(p => D(p.pos, w.pos) <= 3f && targets.Contains(GameContext.GhostId(p.c))).OrderBy(p => D(p.pos, w.pos)).ToList();
                if (theirs.Count > 0) return theirs; // else (not near, or not their customer after all): the one in front of them (code review 8)
            }
            var fwd = Flat(w.c.transform.forward).normalized;
            // You're the one in front of them (their task unread): they're at you, not the resident beside you.
            if (D(me, w.pos) <= 1.4f && Vector3.Dot(fwd, Flat(me - w.pos).normalized) > 0.5f) return new List<(Character c, string id, VenueRoles.Role role, Vector3 pos, Group? table)>();
            return mine.Where(p => D(p.pos, w.pos) <= 1.2f && Vector3.Dot(fwd, Flat(p.pos - w.pos).normalized) > 0.5f).OrderBy(p => D(p.pos, w.pos)).ToList();
        }

        // 1. Staff taking an order: their customer (at the table, or across the counter).
        foreach (var w in staff.Where(p => p.role == VenueRoles.Role.StaffTakingOrder))
        {
            var cust = TheirCustomers(w, VenueRoles.IsCustomer);
            // FB-42: staff back at someone who already told them they're only sitting: sent on again, nothing said.
            var sitter = cust.Count > 0 ? (cust[0].id, cust[0].c.Pointer) : default;
            // Only while the game still has them sitting and not eating (audit A: a later visit by the same body orders as usual).
            if (cust.Count > 0 && Sitters.ContainsKey(sitter) && !VenuePlan.JustSitting(GameContext.WantsToEat(cust[0].c), GameContext.VisitOf(cust[0].c).step))
            {
                Sitters.Remove(sitter);
                SitterReturned.Remove(sitter);
                SitterReturns.Remove(sitter);
            }
            if (cust.Count > 0 && Sitters.TryGetValue(sitter, out float toldAt) && _cfg.JustSittingTellsWaiter.Value)
            {
                // Kept while staff keep coming back (review: it lapsed 10 minutes after they first said it, and the whole exchange
                // came again); said in the log the first time each visit, quietly after (no log flood if the game keeps sending them).
                bool first = !SitterReturned.Contains(sitter);
                Sitters[sitter] = now;
                if (GameContext.EndTakeOrder(w.c, cust[0].c))
                {
                    if (first) Log.Info($"venue talk: the staff came back to someone just sitting ({cust[0].c.name}); they went on again");
                    else Log.Debug($"venue talk: staff back again at someone just sitting ({cust[0].c.name}, told {now - toldAt:0} s ago)");
                    SitterReturned.Add(sitter);
                    int back = SitterReturns.TryGetValue(sitter, out int n) ? n + 1 : 1;
                    SitterReturns[sitter] = back;
                    if (back == 3) Log.Info($"venue talk: staff have come back to {cust[0].c.name} 3 times though they're just sitting (the game keeps queueing their order)");
                }
                continue;
            }
            if (cust.Count > 0 && Begin(w.c, w.id, cust[0].c, cust[0].id, Exchange.Order, now, s, ref ownPlaceOnly, cust[0].table)) return true;
        }
        // 2. A customer ordering at a counter: the staff nearest (whoever's taking orders, then whoever's free).
        foreach (var cu in people.Where(p => p.role == VenueRoles.Role.CustomerOrdering))
        {
            // Never the one taking your order at the counter (review F-0.8.3-A).
            var near = staff.Where(x => D(x.pos, cu.pos) <= 4.5f && !BusyWithYou(x, GameContext.WaiterTargets(x.c))).OrderByDescending(x => x.role == VenueRoles.Role.StaffTakingOrder)
                            .ThenByDescending(x => x.role == VenueRoles.Role.StaffFree).ThenBy(x => D(x.pos, cu.pos)).ToList();
            if (near.Count > 0 && Begin(near[0].c, near[0].id, cu.c, cu.id, Exchange.Order, now, s, ref ownPlaceOnly, cu.table)) return true;
        }
        // 3. Staff bringing a meal: to their customer, once they're at the table with it (within 1.3 m).
        foreach (var w in staff.Where(p => p.role == VenueRoles.Role.StaffServing))
        {
            var cust = TheirCustomers(w, r => r is VenueRoles.Role.CustomerWaiting or VenueRoles.Role.CustomerEating)
                .Where(p => D(p.pos, w.pos) <= 1.3f && GameContext.IsSeated(p.c)).ToList();
            if (cust.Count > 0 && Begin(w.c, w.id, cust[0].c, cust[0].id, Exchange.Serve, now, s, ref ownPlaceOnly, cust[0].table)) return true;
        }
        // 4. Staff check on someone eating: rolled once a visit, 30% (50% when they're near you), not again on every look.
        //    Not someone in a table's conversation (a check-on doesn't break into the table: FB-34 review).
        foreach (var e in people.Where(p => p.role == VenueRoles.Role.CustomerEating && p.table == null))
        {
            var key = (e.id, e.c.Pointer, Exchange.MealCheck);
            if (VenueHandled.ContainsKey(key)) continue;
            double chance = D(e.pos, me) <= 6f ? 0.5 : 0.3;
            if (ChatterRng.NextDouble() >= chance) { VenueHandled[key] = now; continue; } // not this visit
            // Never the one bringing your meal (review F-0.8.3-B).
            var near = staff.Where(x => x.role is VenueRoles.Role.StaffFree or VenueRoles.Role.StaffBusy or VenueRoles.Role.StaffServing && D(x.pos, e.pos) <= 4f
                                        && !BusyWithYou(x, GameContext.WaiterTargets(x.c)))
                            .OrderByDescending(x => x.role == VenueRoles.Role.StaffFree).ThenBy(x => D(x.pos, e.pos)).ToList();
            if (near.Count == 0) continue;
            var kind = near[0].role == VenueRoles.Role.StaffFree && ChatterRng.NextDouble() < 0.5 ? Exchange.SmallTalk : Exchange.MealCheck;
            if (Begin(near[0].c, near[0].id, e.c, e.id, kind, now, s, ref ownPlaceOnly, null)) return true;
        }
        why = ownPlaceOnly ? "only customers at your own place: order talk is for other places" : "nobody ordering, being served or eating near staff";
        return false;
    }

    /// <summary>
    /// /nearby (FB-21): everyone within 10 m, nearest first: what the game has them doing (body action and task), their role
    /// as order talk sees it, and what they're in. For checking in game why order talk does or doesn't start.
    /// </summary>
    private static void NearbyCommand(Action<string> say)
    {
        var pc = GameContext.PlayerCharacter();
        if (pc == null) { say("You're not in the world right now."); return; }
        Vector3 me = pc.transform.position;
        var rows = new List<(float d, string line)>();
        foreach (var (c, _, pos) in NearBodies(pc))
        {
            try
            {
                float d = Flat(pos - me).magnitude;
                if (d > 10f) continue;
                string id = GameContext.NpcId(c);
                var rec = id.Length > 0 ? ActiveBrain.Store.Glance(id) : null; // one try, no history
                string name = rec != null && rec.Persona.Name.Length > 0 ? DisplayName(rec) : (c.name ?? "?").Replace("(Clone)", "").Trim();
                string task = GameContext.GhostTask(c), act = GameContext.ActionName(c);
                bool worker = GameContext.IsWorkerTask(c), seated = GameContext.IsSeated(c);
                var role = VenueRoles.Classify(task, act, worker, seated);
                var (step, table) = GameContext.VisitOf(c);
                string eats = step.Length > 0 && GameContext.WantsToEat(c) is bool e ? e ? " · here to eat" : " · just sitting" : ""; // FB-42
                var targets = worker ? GameContext.WaiterTargets(c) : new HashSet<string>();
                string forWhom = "";
                if (targets.Count > 0)
                {
                    var named = NearBodies(pc).Where(b => targets.Contains(GameContext.GhostId(b.c))).Select(b => (b.c.name ?? "?").Replace("(Clone)", "").Trim()).ToList();
                    forWhom = " · for " + (named.Count > 0 ? string.Join(", ", named) : $"{targets.Count} customer(s) not near you");
                }
                var inWhat = new List<string>();
                if (ChatNpcIs(c)) inWhat.Add("talking to you");
                if (Groups.Any(g => g.Members.Any(m => !m.Gone && Same(m.C, c)))) inWhat.Add("a conversation");
                if (Calls.Any(k => Same(k.C, c) || Same(k.OtherC, c))) inWhat.Add("a call");
                if (Thinking(c)) inWhat.Add("muttering");
                var vt = VenueTalks.FirstOrDefault(t => !t.Cancelled && (Same(t.Staff, c) || Same(t.Customer, c)));
                if (vt != null) inWhat.Add(vt.AiUsed ? "order talk (AI)" : vt.AiAsked ? "order talk (asking the AI)" : "order talk");
                if (inWhat.Count == 0 && NpcHold.IsHeld(c)) inWhat.Add("held");
                rows.Add((d, $"{name}: {d:0.0} m · {VenueRoles.Describe(role)} · action {(act.Length > 0 ? act : "?")} · {(task.Length > 0 ? "task " + task : GameContext.PersonOf(c) == null ? "no task (a background extra)" : "task ?")}" +
                             $"{(worker ? " (a worker's)" : "")}{forWhom}{(seated ? " · seated" : "")}{(step.Length > 0 ? $" · {step}{(table.Length > 0 ? $" at table {table[..Math.Min(8, table.Length)]}" : "")}" : "")}{eats}" +
                             $"{(inWhat.Count > 0 ? " · in " + string.Join(", ", inWhat) : "")}"));
            }
            catch { }
        }
        if (rows.Count == 0) { say("Nobody within 10 m."); return; }
        say($"Within 10 m ({rows.Count}), nearest first. Order talk last said: {(_venueWhy.Length > 0 ? _venueWhy : "started one, or hasn't looked yet")}.");
        foreach (var (_, line) in rows.OrderBy(r => r.d)) say(line);
        Log.Info("/nearby: " + string.Join(" | ", rows.OrderBy(r => r.d).Select(r => r.line)));
    }

    /// <summary>One exchange, once per customer visit (a serve has its own once); never at your own place, never with a child.</summary>
    /// <param name="table">The table conversation the customer is in (FB-34): only an order or a meal comes into it.</param>
    private static bool Begin(Character worker, string sid, Character customer, string cid, Exchange kind, float now, ChatterSettings s, ref bool ownPlaceOnly, Group? table)
    {
        if (table != null && kind is not (Exchange.Order or Exchange.Serve)) return false;
        // Once a visit for each kind (review M6): ordering, being served, a check-on (small talk counts as one).
        var handled = (cid, customer.Pointer, kind == Exchange.SmallTalk ? Exchange.MealCheck : kind);
        if (VenueHandled.ContainsKey(handled)) return false;
        Venue? place = null;
        try { place = customer.Agent != null ? customer.Agent.VisitingLocale : null; } catch { }
        if (place == null) try { place = customer.Agent?.MyGhost?.Venue; } catch { }   // the place their visit is to (code review 7)
        if (place == null) try { place = worker.Agent != null ? worker.Agent.VisitingLocale : null; } catch { }
        if (place == null) try { place = worker.Agent?.MyGhost?.Venue; } catch { }
        if (place == null) return false; // where it is can't be told, so it can't be told it isn't yours: no talk
        if (GameContext.PlayersPlace(place)) { VenueHandled[handled] = now; ownPlaceOnly = true; return false; } // your own place: yours to run
        VenueHandled[handled] = now;
        if (MinorInfo(customer, cid).minor || MinorInfo(worker, sid).minor) return false; // children only ever say their written lines elsewhere
        var t = new VenueTalk { Staff = worker, Customer = customer, StaffId = sid, CustomerId = cid, Kind = kind, Place = GameContext.PlaceName(place), StartedAt = now, NextAt = now + 0.4f, Table = table };
        try { t.CustomerAt = customer.transform.position; t.StartGap = Flat(worker.transform.position - t.CustomerAt).magnitude; } catch { }
        t.Epochs[sid] = ActiveBrain.EpochFor(sid);
        t.Epochs[cid] = ActiveBrain.EpochFor(cid);
        switch (kind)
        {
            case Exchange.Order:
                bool evening = GameContext.Hour() >= 17; // the clock alone, not a whole Capture (weather, place, people...)
                t.Lines.Add((true, Slot.Ask, Pick(evening && ChatterRng.NextDouble() < 0.5 ? EveningGreetings : Greetings1)));
                // FB-42: only there to sit: they say so, the staff leave them be and go on (at the end: EndVenueTalk).
                // CF-26: not for someone this mod took into a conversation a moment ago: their order is the game's to sort out
                // (a player's log: a customer from a residents' conversation called "just sitting", her order cancelled, again and again).
                bool sits = _cfg.JustSittingTellsWaiter.Value && VenuePlan.JustSitting(GameContext.WantsToEat(customer), GameContext.VisitOf(customer).step);
                if (sits && PulledIn.TryGetValue(customer.Pointer, out float pulledAt) && now - pulledAt <= PulledInFor)
                {
                    // Marked handled above on purpose (review CF26-4): once a visit, as every exchange, so this isn't looked at
                    // (and logged) every frame; the game has their order this visit.
                    Log.Info($"venue talk at {t.Place}: {customer.name} was in a residents' conversation a moment ago; their order is left to the game");
                    return false;
                }
                if (sits)
                {
                    t.JustSitting = true;
                    t.Lines.Add((false, Slot.JustSitting, Pick(JustSittingLines)));
                    t.Lines.Add((true, Slot.AfterJustSitting, Pick(LeaveThemBe)));
                    Sitters[(cid, customer.Pointer)] = now;
                    break;
                }
                t.Lines.Add((false, Slot.Order, "")); // what they order: filled in once the game has their order
                t.Lines.Add((true, Slot.AfterOrder, Pick(Confirms)));
                t.AwaitOrder = true;
                t.OrderBy = now + 12f; // at table service the order shows up later than at a counter (0.3.3)
                break;
            case Exchange.Serve:
                var dishes = Ordered(customer);
                var (give, thanks) = VenueRoles.ServeLines(dishes.Count > 0 ? dishes[0] : "", ChatterRng);
                t.Lines.Add((true, Slot.Written, give));
                t.Lines.Add((false, Slot.Written, thanks));
                break;
            case Exchange.SmallTalk:
                var (ask, answer) = SmallTalk[ChatterRng.Next(SmallTalk.Length)];
                t.Lines.Add((true, Slot.Written, ask));
                t.Lines.Add((false, Slot.Written, answer));
                break;
            default:
                var eaten = Ordered(customer);
                t.Lines.Add((true, Slot.Written, eaten.Count > 0 ? string.Format(Pick(MealChecks), eaten[0]) : "How's the food?"));
                t.Lines.Add((false, Slot.Written, Pick(MealAnswers)));
                break;
        }
        VenueTalks.Add(t);
        if (kind == Exchange.Order) AskAiForOrder(t, s, now);
        Log.Debug($"venue talk at {t.Place}: {kind switch { Exchange.Order => "taking an order", Exchange.Serve => "bringing a meal", Exchange.SmallTalk => "a little small talk", _ => "checking on someone eating" }} " +
                  $"({customer.name}, {worker.name}){(table != null ? ", at their table's conversation" : "")}{(t.AiAsked ? ", asking the AI for their words" : "")}");
        return true;
    }

    /// <summary>Close enough that you'd see their words or hear them (FB-34: the AI isn't asked for an exchange nobody notices).</summary>
    private static bool WithinHearing(Character c, ChatterSettings s)
    {
        try
        {
            var pc = GameContext.PlayerCharacter();
            if (pc == null) return false;
            float d = Vector3.Distance(pc.transform.position, c.transform.position);
            return s.TextDistance <= 0f || d <= s.TextDistance || (_voices.Enabled && s.VoiceDistance > 0f && d <= s.VoiceDistance + s.VoiceBeyond);
        }
        catch { return false; }
    }

    /// <summary>
    /// FB-34: the order exchange in their own words, when it's allowed (VenuePlan.UsesAi): one background request, counted in
    /// ChatterPerHour. What the game shows of them is read here (game thread); the words land on the game thread too, and
    /// whatever hasn't been said by then plays them. The written lines play while they haven't come, or if they never do.
    /// </summary>
    private static void AskAiForOrder(VenueTalk t, ChatterSettings s, float now)
    {
        if (_chatter == null) return;
        var custRec = ActiveBrain.Store.Glance(t.CustomerId); // one try, no history: someone met or seen talking has one
        var staffRec = ActiveBrain.Store.Glance(t.StaffId);
        bool minor = ActiveBrain.KnownMinors.Contains(t.CustomerId) || ActiveBrain.KnownMinors.Contains(t.StaffId)
                     || (custRec != null && ActiveBrain.IsKnownMinor(custRec)) || (staffRec != null && ActiveBrain.IsKnownMinor(staffRec));
        if (!VenuePlan.UsesAi(_cfg.AiOrderTalk.Value, AiAllowed(), custRec != null, minor, WithinHearing(t.Customer, s),
                              ChatterPlan.InLastHour(ChatterStarts, now), ChatterPlan.InLastHour(OrderTalkAiStarts, now), s.PerHour)) return;
        NoteStart("order");
        OrderTalkAiStarts.Add(now);
        t.AiAsked = true;
        t.AiWaitUntil = now + 2.5f;
        var staffVisual = GameContext.Visual(t.Staff);
        var custVisual = GameContext.Visual(t.Customer);
        var staffSnap = Snapshot(t.Staff);
        var custSnap = Snapshot(t.Customer);
        bool seated = GameContext.IsSeated(t.Customer);
        string sid = t.StaffId, cid = t.CustomerId, place = t.Place;
        bool sitting = t.JustSitting;
        var epochs = new Dictionary<string, long>(t.Epochs);
        var chatter = _chatter;
        var ct = t.Cts.Token;
        Task.Run(async () =>
        {
            (VenuePlan.Lines lines, NpcRecord staff, NpcRecord customer)? wrote = null;
            try { wrote = await chatter.OrderTalkAsync(sid, staffVisual, staffSnap, cid, custVisual, custSnap, place, seated, epochs, ct, sitting).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log.Debug("order talk (AI): " + e.Message); }
            if (wrote == null) return;
            var got = wrote.Value;
            MainThread.Enqueue(() =>
            {
                if (!VenueTalks.Contains(t) || t.Cancelled) return; // over meanwhile: nothing to swap in
                t.Ai = got.lines;
                t.StaffName = got.staff.Persona.Name;
                t.CustomerName = got.customer.Persona.Name;
            });
        });
    }

    /// <summary>
    /// The dishes they ordered, by the game's own names (none until they've ordered): from their order, or (table service,
    /// 0.3.3) from the visit itself, where the game may keep it.
    /// </summary>
    private static List<string> Ordered(Character c)
    {
        var list = new List<string>();
        void From(Order? order)
        {
            var meals = order != null ? order.OrderedMeals : null;
            if (meals == null) return;
            for (int i = 0; i < meals.Count; i++)
            {
                var m = meals[i];
                string name = m != null ? m.Name ?? "" : "";
                if (name.Length > 0) list.Add(name);
            }
        }
        try { From(c.Agent != null ? c.Agent.Order : null); } catch { }
        if (list.Count == 0)
            try { if (GameContext.VisitOf(c).step.Length > 0) From(c.Agent?.MyGhost?.VisitVenueState?.Order); } catch { }
        return list;
    }

    /// <summary>
    /// 0.3.3: the customer walked off: the game says they're leaving, or they're well away from where they were AND from the
    /// staff (review R9: stepping up in a queue isn't walking off).
    /// </summary>
    private static bool WalkedOff(VenueTalk t, string step)
    {
        try
        {
            if (step == "Leaving") return true;
            return Flat(t.Customer.transform.position - t.CustomerAt).magnitude > 1.5f && Gap(t) > t.StartGap + 1.5f;
        }
        catch { return false; }
    }

    private static string Pick(string[] options) => options[ChatterRng.Next(options.Length)];

    private static string Listed(List<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    /// <summary>How far apart staff and customer are now.</summary>
    private static float Gap(VenueTalk t)
    {
        try { return Flat(t.Staff.transform.position - t.Customer.transform.position).magnitude; } catch { return 0f; }
    }

    /// <summary>
    /// One voice each (B2): talking somewhere else stops shop talk, except a customer still sitting in the table conversation
    /// it came into (FB-34), which waits for it.
    /// </summary>
    private static bool BusyElsewhere(VenueTalk t, Character c, string id)
    {
        if (!TalkingNow(c, id, except: t)) return false;
        return t.Table == null || !Groups.Contains(t.Table) || AtTable(c, id, t, t.Kind) != t.Table;
    }

    private static void TickVenueTalk(VenueTalk t, ChatterSettings s, bool paused)
    {
        float now = Time.unscaledTime;
        // At a table it waits for the table's own lines too, so it gets longer (review R5).
        if (!Alive(t.Customer) || !Alive(t.Staff) || now - t.StartedAt > (t.Table != null ? 120f : 60f)) { EndVenueTalk(t, cancel: true); return; }
        // You walked off past the wrap-up distance: it ends, and stops counting in the Dynamic pool (FLOW-7).
        var pc = GameContext.PlayerCharacter();
        if (pc != null && Flat(t.Staff.transform.position - pc.transform.position).magnitude > s.WrapUpDistance
                       && Flat(t.Customer.transform.position - pc.transform.position).magnitude > s.WrapUpDistance)
        {
            EndVenueTalk(t, cancel: true);
            return;
        }
        if (t.Table != null && !Groups.Contains(t.Table)) t.Table = null; // the table's talk is over: nothing to wait for
        if (paused || now < t.NextAt) return;
        if (t.Next >= t.Lines.Count) { EndVenueTalk(t); return; } // finished: a slow last voice still plays
        // The game handed one of their bodies to someone else (LEAK-8): it isn't them any more.
        if (GameContext.NpcId(t.Staff) != t.StaffId || GameContext.NpcId(t.Customer) != t.CustomerId) { EndVenueTalk(t, cancel: true); return; }
        // One of them was taken for something else meanwhile: it stops here, rather than showing bubbles their voice can't
        // follow (one voice each). A call doesn't take someone busy near you, so this is the rare rest: a call that took
        // them from farther away than it looks for its other end, pooled extras sharing an id, a dev hold.
        if (BusyElsewhere(t, t.Staff, t.StaffId) || BusyElsewhere(t, t.Customer, t.CustomerId)) { EndVenueTalk(t, cancel: true); return; }
        // They moved while the staff waited on their order (0.3.3 + audit A2): someone who ordered did order, whatever the game
        // did with them since (never "okay then"); someone who walked off before a word was said just goes; someone who walked
        // off without ordering once it had begun gets "okay then", and the staff remember it.
        if (t.AwaitOrder || t.AwaitLater)
        {
            Look(t, now);
            string stepNow = t.StepNow;
            switch (VenuePlan.AfterWait(t.DishesNow.Count > 0, stepNow, WalkedOff(t, stepNow), t.Next))
            {
                case VenuePlan.AfterWaitOutcome.EndQuietly:
                    Log.Debug($"venue talk at {t.Place}: the customer went before anything was said");
                    EndVenueTalk(t);
                    return;
                case VenuePlan.AfterWaitOutcome.SayLeft:
                    t.Lines.RemoveRange(t.Next, t.Lines.Count - t.Next);
                    t.Lines.Add((true, Slot.Left, Pick(OkayThen)));
                    t.AwaitOrder = t.AwaitLater = false;
                    t.Left = true;
                    t.LeftVisit = stepNow == "Leaving";
                    Log.Debug($"venue talk at {t.Place}: the customer walked off before ordering");
                    break;
            }
        }
        if (t.Next >= t.Lines.Count) { EndVenueTalk(t); return; }
        // FB-34: the first line waits a moment for the AI's words; a table's line still on show is let finish.
        if (t.Next == 0 && t.AiAsked && t.Ai == null && now < t.AiWaitUntil) return;
        if (t.Table != null && now < t.Table.NextAt) return;
        var (isStaff, slot, written) = t.Lines[t.Next];
        if (!isStaff && (t.AwaitOrder || t.AwaitLater))
        {
            // Their actual order, as the game has it: they say it once it's placed; not yet: "give me a second" (the staff answer),
            // and the staff wait a while longer for it (0.3.3); never: it just ends.
            Look(t, now);
            var dishes = t.DishesNow;
            if (dishes.Count == 0 && now < t.OrderBy) return;
            if (dishes.Count == 0 && t.AwaitLater) { EndVenueTalk(t); return; } // waited long enough: the staff move on
            if (dishes.Count == 0)
            {
                // FB-42: what the game has of their visit, to check in game that "just sitting" is read right.
                Log.Info($"venue talk at {t.Place}: {t.Customer.name} isn't ready to order (the game's visit: " +
                         $"{(GameContext.WantsToEat(t.Customer) is bool eat ? eat ? "here to eat" : "just sitting" : "unknown")}, step {t.StepNow})");
                t.Lines.Insert(t.Next, (false, Slot.NotReady, Pick(NoOrder)));       // said now
                t.Lines.Insert(t.Next + 1, (true, Slot.AfterNotReady, Pick(NoRush))); // the staff's answer
                t.Lines[t.Next + 2] = (false, Slot.Ready, "");                        // their order, if it comes
                t.AwaitOrder = false;
                t.AwaitLater = true;
                t.OrderBy = now + 20f;
                (isStaff, slot, written) = t.Lines[t.Next];
            }
            else
            {
                // They ordered, but the staff have gone on (the game moved one of them): nobody to say it to.
                if (Gap(t) > Mathf.Max(4f, t.StartGap + 1.5f)) { Log.Debug($"venue talk at {t.Place}: ordered, the staff have moved on"); EndVenueTalk(t); return; }
                t.Dishes = Listed(dishes.Distinct().ToList());
                written = string.Format(Pick(Orders), t.Dishes);
                t.AwaitOrder = t.AwaitLater = false;
            }
        }
        // The staff walked away (the game's next task): their remaining lines aren't said, except "okay then", said aloud anyway.
        if (isStaff && slot != Slot.Left && Gap(t) > Mathf.Max(3.5f, t.StartGap + 1f))
        {
            t.Next++;
            t.NextAt = now + 0.1f;
            return;
        }
        string text = VenuePlan.Choose(slot, t.Ai, written, t.Dishes);
        bool byAi = text != written; // the AI's words for this turn came (VenuePlan.Choose)
        var who = isStaff ? t.Staff : t.Customer;
        string id = isStaff ? t.StaffId : t.CustomerId;
        float shown = Mathf.Clamp(1.6f + text.Length / 15f, 2f, 6f);
        t.Next++;
        t.NextAt = now + shown + 0.5f;
        t.ShowingUntil = t.NextAt;
        var head = GameContext.HeadOf(who);
        var rec = ActiveBrain.Store.Glance(id); // main thread: one try, no waiting, no history
        // Children are left out of venue talk: the start checked, and a record or the list may know better by now.
        if (ActiveBrain.KnownMinors.Contains(id) || (rec != null && ActiveBrain.IsKnownMinor(rec))) { EndVenueTalk(t, cancel: true); return; }
        if (rec != null && rec.Persona.Name.Length > 0) { if (isStaff) { if (t.StaffName.Length == 0) t.StaffName = rec.Persona.Name; } else if (t.CustomerName.Length == 0) t.CustomerName = rec.Persona.Name; }
        string label = rec != null && DisplayName(rec) != "Stranger" ? Prompts.FirstName(DisplayName(rec)) : "";
        Ui.ShowBark(label, text, head, shown + 0.4f, id, maxDistance: s.TextDistance, body: who.transform);
        NpcHold.Mouth(who, text, 0f);
        t.Said.Add((isStaff, text));
        if (byAi) t.AiUsed = true;
        if (VenuePlan.NamesDish(slot)) t.OrderSaid = true;
        // At a table (FB-34): everyone there turns to whoever's speaking; the customer faces the staff member.
        if (t.Table != null)
            foreach (var m in t.Table.Members.Where(m => m.Joined && !m.Gone && m.Held && !Same(m.C, who)))
                NpcHold.LookAtFor(m.C, head);
        var tone = byAi ? (isStaff ? t.Ai!.StaffVoice : t.Ai!.CustomerVoice)?.Copy() : null;
        if (_transcripts != null) RecordLine("order talk", $"venue:{t.StaffId}:{t.CustomerId}:{t.StartedAt:0.0}", who, id, rec, text,
            "", tone: tone, by: byAi ? "ai-background" : "written",
            speakerName: rec == null ? (isStaff ? "staff" : "customer") + " (" + (who.name ?? "").Replace("(Clone)", "").Trim() + ")" : "");
        if (!WithinVoice(who, s, s.VoiceBeyond) || !_voices.Enabled) return;
        // Voiced in their own voice (someone you've never met still gets one, kept for them).
        var subject = rec != null ? VoiceSubject.From(rec) : SubjectFor(who, id);
        if (tone != null) subject.Tone = tone;
        var ct = CancellationToken.None;
        var table = t.Table;
        float lineUntil = t.ShowingUntil;
        int shownAt = t.Next; // the next line hasn't been shown yet while this is still t.Next (final review F4)
        Task.Run(async () =>
        {
            var voice = await VoiceForAsync(() => subject, text, ct, "order").ConfigureAwait(false);
            if (voice.Audio == null) return;
            MainThread.Enqueue(() =>
            {
                // Stopped meanwhile, or they're talking somewhere else now (your chat, a call...): one voice each (RACE-4).
                if (!Alive(who) || t.Cancelled || BusyElsewhere(t, who, id)) return;
                float at = Time.unscaledTime;
                if (table != null && (at > lineUntil || t.Next != shownAt)) return; // the table or the next line has moved on: no voice over it (R1, F4)
                float spoken = VoicePlayer.Play(who, voice.Audio, LineVolume(id, overheard: true), VoiceReach(s), 3f);
                // The next line, and the table, wait while it's said (the bubble's time came from the words, not the voice).
                float until = at + spoken + 0.3f;
                t.NextAt = Mathf.Max(t.NextAt, until);
                t.ShowingUntil = Mathf.Max(t.ShowingUntil, until);
                if (table != null && Groups.Contains(table)) table.NextAt = Mathf.Max(table.NextAt, until);
                NpcHold.Mouth(who, text, spoken); // the mouth again, now as long as the voice (CF-04)
                // The game's own lip-sync pace back afterwards, also when this voice came after the talk had ended (audit A4).
                Later(spoken + 0.3f, () => { if (Alive(who) && !NpcHold.IsHeld(who) && !VenueTalks.Any(v => !v.Cancelled && (Same(v.Staff, who) || Same(v.Customer, who)))) NpcHold.MouthAtOwnPace(who); });
                VoicePlayer.Release(who); // one line at a time: the voice goes once it has played
            });
        });
    }

    /// <summary>
    /// FB-34: a table conversation waits while staff serve someone in it: a service line is on show, or due now (its turn has
    /// come and it isn't still waiting on the game for their order).
    /// </summary>
    private static bool ServingTable(Group g, float now)
    {
        if (VenueTalks.Count == 0) return false;
        foreach (var t in VenueTalks)
        {
            if (t.Table != g || t.Cancelled) continue;
            if (now < t.ShowingUntil) return true;
            if (t.Next >= t.Lines.Count || now < t.NextAt) continue;
            if (!t.Lines[t.Next].staff && (t.AwaitOrder || t.AwaitLater)) Look(t, now);
            bool waiting = !t.Lines[t.Next].staff && (t.AwaitOrder || t.AwaitLater) && now < t.OrderBy && t.DishesNow.Count == 0;
            if (!waiting) return true;
        }
        return false;
    }

    /// <summary>A voice for someone with no record yet: by how they look (a man, a woman), kept for them from now on.</summary>
    private static VoiceSubject SubjectFor(Character c, string id)
    {
        string body = "";
        try { body = GameContext.Visual(c).Body; } catch { }
        return new VoiceSubject { Id = id, Presentation = body switch { "man" => "male", "woman" => "female", _ => "any" } };
    }

    /// <summary>
    /// Over. <paramref name="cancel"/>: stopped before its end (you talked to one of them, too long, under 18, you walked
    /// off, one of them was taken for a call): its bubbles go, a line already playing fades out (as a broken-up
    /// conversation's or call's does), and a voice still on its way isn't played. A normal finish isn't cancelled (its
    /// last line may still be on its way). An order exchange that used the AI's words, or one the customer walked out of,
    /// is remembered (FB-34).
    /// </summary>
    private static void EndVenueTalk(VenueTalk t, bool cancel = false)
    {
        if (!VenueTalks.Remove(t)) return; // ended already (its memory and bubbles are dealt with once)
        // FB-42: they said they're only sitting and the staff answered: the staff go on with their work.
        if (!cancel && t.JustSitting && t.Said.Any(l => l.staff) && t.Said.Count >= 3)
        {
            bool moved = GameContext.EndTakeOrder(t.Staff, t.Customer);
            Log.Info($"venue talk at {t.Place}: {t.Customer.name} is just sitting; {(moved ? "the staff went on with their work" : "the staff had already moved on")}");
        }
        try { t.Cts.Cancel(); } catch { } // the AI's words, if still being written, aren't wanted now
        // Their lip-sync back at the game's own pace (a voiced line set it to the voice's): review of 0.3.1, L6.
        foreach (var c in new[] { t.Staff, t.Customer })
            if (c != null) NpcHold.MouthAtOwnPace(c);
        // The table carries on after a moment (FB-34).
        if (t.Table != null && Groups.Contains(t.Table)) t.Table.NextAt = Mathf.Max(t.Table.NextAt, Time.unscaledTime + 0.6f);
        // Remembered: a customer who got up and left, or an order said in their own words (review R2, R9).
        bool walkedOut = t.Left && t.LeftVisit;
        if (!cancel && t.Kind == Exchange.Order && (walkedOut || (t.AiUsed && t.OrderSaid)) && t.Said.Count > 0 && _chatter != null)
        {
            var chatter = _chatter;
            string sid = t.StaffId, sname = t.StaffName, cid = t.CustomerId, cname = t.CustomerName, place = t.Place, dishes = t.Dishes;
            var epochs = new Dictionary<string, long>(t.Epochs);
            var said = t.Said.ToList();
            bool left = walkedOut;
            var where = new GameSnapshot();
            try { where = Snapshot(t.Customer); } catch { }
            Task.Run(async () =>
            {
                try { await chatter.RememberOrderTalkAsync(sid, sname, cid, cname, epochs, where, place, said, dishes, left).ConfigureAwait(false); }
                catch (Exception e) { Log.Debug("remembering order talk: " + e.Message); }
            });
        }
        if (!cancel) return;
        t.Cancelled = true;
        foreach (var (c, id) in new[] { (t.Staff, t.StaffId), (t.Customer, t.CustomerId) })
        {
            try { Ui.HideBark(id); } catch { }
            VoicePlayer.Stop(c);
            VoicePlayer.Release(c);
        }
    }

    private static void EndAllVenueTalk()
    {
        foreach (var t in VenueTalks.ToList()) EndVenueTalk(t, cancel: true);
    }

    /// <summary>Where the people in shop talk are standing: company for anyone right beside them (OnTheirOwn).</summary>
    private static List<Vector3> VenueTalkerSpots()
    {
        var spots = new List<Vector3>();
        foreach (var t in VenueTalks)
        {
            if (t.Cancelled) continue;
            try { if (Alive(t.Staff)) spots.Add(t.Staff.transform.position); } catch { }
            try { if (Alive(t.Customer)) spots.Add(t.Customer.transform.position); } catch { }
        }
        return spots;
    }
}
