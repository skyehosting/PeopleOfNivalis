using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;

namespace NivalisAIChat.Ai;

/// <summary>
/// FB-34: a member of staff taking a customer's order at a food place, written by the AI in one small request: both
/// sides, every turn it can take (ordering straight away, not ready yet and then ready, or walking off), in each one's
/// own voice, shaped by their moods and how they feel about each other. The dish is left as {dish}: the code puts in what
/// the game says they ordered, so the AI never names food that isn't on the order.
/// </summary>
public static class ServicePrompts
{
    /// <summary>The fields of the answer, the slot each fills, and who says it.</summary>
    private static readonly (string field, VenuePlan.Slot slot, bool staff)[] OrderFields =
    {
        ("ask", VenuePlan.Slot.Ask, true),
        ("order", VenuePlan.Slot.Order, false),
        ("not_ready", VenuePlan.Slot.NotReady, false),
        ("after_not_ready", VenuePlan.Slot.AfterNotReady, true),
        ("ready", VenuePlan.Slot.Ready, false),
        ("after_order", VenuePlan.Slot.AfterOrder, true),
        ("left", VenuePlan.Slot.Left, true),
    };

    /// <summary>FB-42: someone only there to sit: the staff ask, they say so, the staff leave them be.</summary>
    private static readonly (string field, VenuePlan.Slot slot, bool staff)[] SittingFields =
    {
        ("ask", VenuePlan.Slot.Ask, true),
        ("just_sitting", VenuePlan.Slot.JustSitting, false),
        ("after_just_sitting", VenuePlan.Slot.AfterJustSitting, true),
    };

    private static (string field, VenuePlan.Slot slot, bool staff)[] Fields(bool justSitting) => justSitting ? SittingFields : OrderFields;

    /// <summary>Longest line kept (they're a few words each; anything longer reads as a speech and the written line plays).</summary>
    public const int MaxLine = 120;

    public static LlmRequest OrderRequest(ChatterPrompts.Person staff, ChatterPrompts.Person customer, PairBook.Pair? pair, string place, bool atTable,
        bool justSitting = false)
    {
        var req = new LlmRequest { Purpose = "order talk", MaxTokens = 1500, Background = true, SchemaName = "order_talk" };
        req.System.Add("You write the few words a member of staff and a customer say to each other when an order is taken at a food place in the city " +
                       "of Nivalis. They are real people with their own lives and moods, not characters in a story.\n\n" +
                       ChatterPrompts.City() + ChatterPrompts.Rules());
        if (CustomInstructions.Block() is { Length: > 0 } own) req.System.Add(own);
        if (Prompts.SpeakIn() is { Length: > 0 } speakIn) req.System.Add(speakIn); // ReplyLanguage
        req.System.Add(Prompts.SafetyLast);

        string s = Prompts.FirstName(staff.Name), c = Prompts.FirstName(customer.Name);
        var snap = customer.Snap;
        var sb = new StringBuilder();
        sb.AppendLine("[THE STAFF MEMBER] " + ChatterPrompts.Describe(staff) + $" At work here{(place.Length > 0 ? " at " + place : "")}.");
        sb.AppendLine("[THE CUSTOMER] " + ChatterPrompts.Describe(customer));
        sb.AppendLine("[HOW THEY KNOW EACH OTHER] " + ChatterPrompts.Between(staff, customer, pair));
        sb.AppendLine($"[WHERE AND WHEN] {(place.Length > 0 ? place : "A food place")}, {(atTable ? $"at {c}'s table" : "at the counter")}; " +
                      $"{(snap.PartOfDay.Length > 0 ? snap.PartOfDay : "today")}{(snap.Weather.Length > 0 ? ", " + snap.Weather : "")}.");
        sb.AppendLine();
        if (justSitting)
        {
            // FB-42: they came in only to sit for a while, not to eat or drink.
            sb.AppendLine($"[Scene: {s} comes to take {c}'s order, but {c} only came in to sit for a while, not to eat or drink. One short sentence each " +
                          $"(under 15 words), in each one's own words: no stock phrases. {s} is friendly and professional unless their own mood says otherwise; " +
                          $"{c}'s manner follows their mood and how they feel about {s}.]");
            sb.AppendLine("Answer with one JSON object:");
            sb.AppendLine($"- ask: {s} asks what they'd like.");
            sb.AppendLine($"- just_sitting: {c} says they're only sitting a while and won't order; no food or drink named.");
            sb.AppendLine($"- after_just_sitting: {s} answers that kindly and leaves them be.");
        }
        else
        {
        sb.AppendLine($"[Scene: {s} comes to take {c}'s order. Write every turn it can take, one short sentence each (under 15 words), in each one's own " +
                      $"words: no stock phrases. {s} is friendly and professional unless their own mood says otherwise. {c}'s manner follows their mood and how " +
                      $"they feel about {s}: polite, short, or annoyed.]");
        sb.AppendLine("Answer with one JSON object:");
        sb.AppendLine($"- ask: {s} asks what they'd like.");
        sb.AppendLine($"- order: {c} orders straight away. Write {VenuePlan.DishSlot} exactly once where the food goes; never name any food or drink yourself.");
        sb.AppendLine($"- not_ready: {c} isn't ready to order yet.");
        sb.AppendLine($"- after_not_ready: {s} answers that (no rush, they'll come back).");
        sb.AppendLine($"- ready: a little later {c} is ready and orders, politely or impatiently as they feel: {VenuePlan.DishSlot} exactly once, no food named.");
        sb.AppendLine($"- after_order: {s} takes the order (they'll be right back with it).");
        sb.AppendLine($"- left: {c} has just got up and walked off without ordering; what {s} says out loud to nobody in particular.");
        }
        sb.AppendLine($"- staff_tone, customer_tone: how each of them sounds, one mood word ({string.Join(", ", Moods.All)}).");
        req.Messages.Add(new LlmMessage("user", sb.ToString()));

        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (field, _, _) in Fields(justSitting)) { props[field] = new JsonObject { ["type"] = "string" }; required.Add(field); }
        foreach (var tone in new[] { "staff_tone", "customer_tone" }) { props[tone] = new JsonObject { ["type"] = "string" }; required.Add(tone); }
        req.JsonSchema = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required, ["additionalProperties"] = false };
        return req;
    }

    private static readonly Regex Dish = new(@"\{\s*dish\s*\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private const string Hidden = "DISHSLOTWORD";

    /// <summary>
    /// The lines as written, each checked: spoken words only, a few words long, in character; the order lines must have
    /// {dish} exactly once and every other line none (a line that fails is left empty, and its written line plays).
    /// Null when nothing usable came back.
    /// </summary>
    public static VenuePlan.Lines? ParseOrder(string text, NpcRecord staff, NpcRecord customer, bool justSitting = false)
    {
        var o = LlmClient.ExtractJsonObject(text);
        if (o == null) return null;
        var lines = new VenuePlan.Lines();
        foreach (var (field, slot, isStaff) in Fields(justSitting))
        {
            string raw = o[field] is JsonValue v && v.TryGetValue<string>(out var str) ? ReplyParser.Blank(str) : "";
            var who = isStaff ? staff : customer;
            string clean = Clean(raw, who, VenuePlan.NamesDish(slot));
            if (clean.Length > 0) lines.Said[slot] = clean;
        }
        string Tone(string k) => o[k] is JsonValue tv && tv.TryGetValue<string>(out var w) ? Moods.Normalize(ReplyParser.Blank(w)) : "";
        lines.StaffTone = Tone("staff_tone");
        lines.CustomerTone = Tone("customer_tone");
        return lines.Said.Count > 0 ? lines : null;
    }

    private static string Clean(string raw, NpcRecord who, bool namesDish)
    {
        if (raw.Length == 0) return "";
        int dishes = Dish.Matches(raw).Count;
        if (namesDish ? dishes != 1 : dishes != 0) return "";
        string hidden = Dish.Replace(raw, Hidden);
        if (hidden.Contains('{') || hidden.Contains('}')) return ""; // a slot it made up
        string said = ReplyParser.Parse(hidden, who.Persona.Name, who.Persona.Nickname).Spoken.Trim().Trim('"', '“', '”').Trim();
        if (said.Length == 0 || said.Length > MaxLine) return "";
        if (namesDish && said.Split(Hidden).Length != 2) return ""; // cleaning took the slot out (or doubled it)
        if (ReplyParser.LooksOutOfCharacter(said.Replace(Hidden, "it"), who.Visual.IsAndroid)) return "";
        return said.Replace(Hidden, VenuePlan.DishSlot);
    }
}
