using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Phone;

/// <summary>
/// Nightline's words (FB-71): one AI request each time a resident opens the app. Whether they post (and what, maybe with a
/// photo), what they make of the lines they scrolled past (like, dislike, a comment, a reply, or nothing, and why), and
/// a handle of their own the first time. Then the answer read back, and what they'll remember of it. Their own voice and
/// memory, as their texts. No Unity, tested.
/// </summary>
public static class Lines
{
    public const int MaxTokens = 900, CommentsShown = 4;

    /// <summary>A line they scrolled to, as it's put to them.</summary>
    public sealed class Seen
    {
        /// <summary>"L1".. in the request; the runtime maps it back to the line.</summary>
        public string Key = "";
        public string LineId = "";
        public string Author = "", AuthorName = "", Handle = "", Text = "", Place = "", Ago = "", PhotoShows = "";
        /// <summary>How they feel about whoever posted it (LinePlan.Feeling), or "it's your own line".</summary>
        public string Feeling = "";
        public bool Own, MayReact, MayComment;
        /// <summary>FB-72: your recent line, and they could come keep you company now (only then may they say they're coming).</summary>
        public bool MayCome;
        /// <summary>Reacted already (true like, false dislike, null not).</summary>
        public bool? Had;
        public List<(string key, string name, string handle, string text)> Comments = new();
        /// <summary>Each comment key's real id, taken when the request was built (review NL-1: never by position later).</summary>
        public Dictionary<string, string> CommentIds = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The author of each comment key (who a reply goes to).</summary>
        public Dictionary<string, string> CommentWho = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Their own line: who reacted since they last looked (name, liked), and how many reactions that covers.</summary>
        public List<(string name, bool up)> NewReactions = new();
        public int LookedUpTo;
        /// <summary>FB-80: a photo the player took is on it.</summary>
        public bool YourPhoto;
        /// <summary>FB-80: that photo, for an AI that can see (never one with someone under 18 in view): one a look at most.</summary>
        public byte[]? Image;
    }

    /// <summary>
    /// FB-80 (the author: "NPC's should never describe an entire scene from a photo, only simple human comments if the post
    /// is relating the a pretty picture the user has taken or based on the text in the post"): how they take your photo.
    /// </summary>
    public const string PhotoRule = "React to it the way people do: a few words at most (\"gorgeous\", \"where is that?\", \"ha, love it\"), only if " +
        "you want to; never describe the picture or list what's in it. If the words make a point (a complaint, politics), react to the point.";

    /// <summary>What they asked for or did, as read from the answer.</summary>
    public sealed class Result
    {
        public string Handle = "";
        public string Post = "";
        public string PhotoShows = "", PhotoArea = "";
        public bool PhotoOfMe;
        public List<Act> Acts = new();
        public bool Ok;
    }

    public sealed class Act
    {
        public string Key = "";
        /// <summary>true like, false dislike, null none.</summary>
        public bool? React;
        public string Comment = "", ReplyTo = "", Why = "";
        /// <summary>FB-72: they're coming to keep you company (only on a line that allowed it).</summary>
        public bool Coming;
    }

    /// <summary>
    /// The request. <paramref name="post"/>: they mean to post this time; <paramref name="photoAreas"/>: the places the city has
    /// pictures of (empty: no photo can be made); <paramref name="doing"/>: what they're doing right now, if known ("" not).
    /// </summary>
    public static LlmRequest Request(NpcRecord rec, GameSnapshot snap, bool post, IReadOnlyList<Seen> seen, bool wantHandle,
        IReadOnlyList<string> photoAreas, bool hasOwnPicture, string doing, string withYou, double friendliness, string playerGender)
    {
        var req = new LlmRequest { Purpose = "nightline", MaxTokens = MaxTokens, Background = true, SchemaName = "nightline", JsonSchema = Schema() };
        req.System.Add(Prompts.IdentityBlock(rec, structured: false, friendliness, playerGender));
        if (CustomInstructions.Block() is { Length: > 0 } own) req.System.Add(own);
        req.System.Add(Block(rec, snap, post, wantHandle, photoAreas, hasOwnPicture, doing, withYou));
        if (Prompts.SpeakIn() is { Length: > 0 } speak) req.System.Add(speak);
        req.System.Add(Prompts.SafetyLast);
        var msg = new LlmMessage("user", Feed(seen, post));
        // FB-80: one photo of yours at most, for a model that can see (a refusal: asked again without it).
        if (seen.FirstOrDefault(s => s.Image is { Length: > 0 }) is { } shown)
            msg.Images = new List<LlmImage> { new() { MediaType = "image/jpeg", Bytes = shown.Image! } };
        req.Messages.Add(msg);
        return req;
    }

    /// <summary>What Nightline is, who they are on it, what's going on with them.</summary>
    public static string Block(NpcRecord rec, GameSnapshot snap, bool post, bool wantHandle, IReadOnlyList<string> photoAreas, bool hasOwnPicture, string doing, string withYou)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[NIGHTLINE]");
        sb.AppendLine($"It's {(snap.DayOfWeek.Length > 0 ? snap.DayOfWeek : "a weekday")}, day {snap.GameDay}, {snap.Clock}. You've opened Nightline on your phone: the city's social app, where people drop a short public line (a post) about their day, " +
                      "and like, dislike or comment on each other's. Anyone in the city can read it.");
        sb.AppendLine("Write the way you'd really post: short, in your own voice and slang, the city's references if they're you. No emoji (a text smiley like :) is fine), no hashtags unless that's very you, " +
                      "no stage directions. Never mention being an AI, a game or a prompt.");
        sb.AppendLine("React honestly, as yourself: like what you like, dislike what rubs you wrong, skip what bores you. How you feel about whoever posted it matters: " +
                      "a friend gets a like and a warm word, someone you can't stand may get a dislike, a cold comment, a jab, or nothing at all. Drama is fine; cruelty about someone's body, " +
                      "anyone under 18, or anything hateful about who someone is, is not.");
        if (doing.Length > 0) sb.AppendLine("What you're doing right now: " + doing + ".");
        if (withYou.Length > 0) sb.AppendLine(withYou);
        if (post)
        {
            sb.AppendLine("This time you post a line: what you're up to, something that happened, a thought, a complaint, a plan, something you saw. One to three sentences, at most 280 characters.");
            if (photoAreas.Count > 0)
                sb.AppendLine("You can add a photo if it fits (most posts don't have one): say in plain words what's in it (\"shows\"), which place it was taken (\"area\": one of " +
                              string.Join(", ", photoAreas.Take(16)) + "), and whether you're in it (\"me\"" + (hasOwnPicture ? "" : "; there's no picture of you yet, so better not") +
                              "). Only adults in any photo.");
        }
        if (wantHandle) sb.AppendLine("You don't have a Nightline handle yet: pick one that's you (from your name, your work, what you love, a joke only you'd make). Letters, digits, dots and underscores.");
        var p = rec.Persona;
        sb.AppendLine($"On your mind: {p.CurrentWorry}. What you want right now: {p.ShortTermGoal}.");
        var life = rec.LifeLog.TakeLast(3).ToList();
        if (life.Count > 0)
        {
            sb.AppendLine("Your life lately:");
            foreach (var e in life) sb.AppendLine($"- Day {e.GameDay}: {e.Text}");
        }
        var lately = rec.Talks.Where(t => t.Kind == Kind).TakeLast(3).ToList();
        if (lately.Count > 0)
        {
            sb.AppendLine("What you did on Nightline lately:");
            foreach (var t in lately) sb.AppendLine($"- Day {t.GameDay}: {t.Summary}");
        }
        sb.AppendLine("Answer only with the JSON object asked for.");
        return sb.ToString();
    }

    /// <summary>The kind of their memory a visit to Nightline is kept as (NpcRecord.Talks).</summary>
    public const string Kind = Talk.Nightline;

    /// <summary>The lines they scrolled past, as the user turn.</summary>
    public static string Feed(IReadOnlyList<Seen> seen, bool post)
    {
        var sb = new StringBuilder();
        if (seen.Count == 0) sb.AppendLine("You don't scroll this time.");
        else
        {
            sb.AppendLine("You scroll, newest first. For each line: react (like, dislike or none), comment if you want (\"\" for none), reply_to a comment's key if you're answering it, and why (a few words, private).");
            foreach (var s in seen)
            {
                sb.AppendLine();
                string who = s.Own ? "Your own line" : $"{ReplyParser.Quotable(s.AuthorName, 60)} (@{s.Handle}; {s.Feeling})";
                sb.AppendLine($"{s.Key}. {who}, {s.Ago}{(s.Place.Length > 0 ? ", at " + ReplyParser.Quotable(s.Place, 80) : "")}: \"{ReplyParser.Quotable(s.Text, LineBook.MaxText)}\"");
                if (s.PhotoShows.Length > 0) sb.AppendLine($"   With a photo: {ReplyParser.Quotable(s.PhotoShows, 300)}");
                if (s.YourPhoto)
                    sb.AppendLine(s.Image != null
                        ? $"   With a photo they took: it's the picture with this message. {PhotoRule}"
                        : "   With a photo they took (you can't see it from here): go by the words.");
                if (s.Had is bool had) sb.AppendLine($"   You already {(had ? "liked" : "disliked")} it.");
                foreach (var (key, name, handle, text) in s.Comments.TakeLast(CommentsShown))
                    sb.AppendLine($"   {key} {ReplyParser.Quotable(name, 60)} (@{handle}): \"{ReplyParser.Quotable(text, LineBook.MaxComment)}\"");
                if (s.Own && s.NewReactions.Count > 0)
                    sb.AppendLine("   Since you last looked: " + string.Join(", ", s.NewReactions.Take(8).Select(r => $"{ReplyParser.Quotable(r.name, 60)} {(r.up ? "liked" : "disliked")} it")) + ".");
                if (s.MayCome) sb.AppendLine("   (If this asks for company or for someone to come by, and you want to and can go there now: coming true, and say so in your comment. Otherwise coming false.)");
                if (s.Own) sb.AppendLine("   (Your own: no reaction; you may answer a comment.)");
                else if (!s.MayReact && !s.MayComment) sb.AppendLine("   (You just glance at this one: react none, no comment.)");
                else if (!s.MayComment) sb.AppendLine("   (No comment on this one; a reaction is up to you.)");
                else if (!s.MayReact) sb.AppendLine("   (A comment is up to you; no reaction.)");
            }
        }
        sb.AppendLine();
        sb.AppendLine(post ? "Then write your own line." : "You don't post this time: post is null.");
        return sb.ToString();
    }

    /// <summary>The answer's shape.</summary>
    public static JsonObject Schema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("handle", "post", "lines"),
        ["properties"] = new JsonObject
        {
            ["handle"] = new JsonObject { ["type"] = "string" },
            ["post"] = new JsonObject
            {
                ["type"] = new JsonArray("object", "null"),
                ["additionalProperties"] = false,
                ["required"] = new JsonArray("text", "photo"),
                ["properties"] = new JsonObject
                {
                    ["text"] = new JsonObject { ["type"] = "string" },
                    ["photo"] = new JsonObject
                    {
                        ["type"] = new JsonArray("object", "null"),
                        ["additionalProperties"] = false,
                        ["required"] = new JsonArray("shows", "area", "me"),
                        ["properties"] = new JsonObject
                        {
                            ["shows"] = new JsonObject { ["type"] = "string" },
                            ["area"] = new JsonObject { ["type"] = "string" },
                            ["me"] = new JsonObject { ["type"] = "boolean" },
                        },
                    },
                },
            },
            ["lines"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("line", "react", "comment", "reply_to", "why", "coming"),
                    ["properties"] = new JsonObject
                    {
                        ["line"] = new JsonObject { ["type"] = "string" },
                        ["react"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("like", "dislike", "none") },
                        ["comment"] = new JsonObject { ["type"] = "string" },
                        ["reply_to"] = new JsonObject { ["type"] = "string" },
                        ["why"] = new JsonObject { ["type"] = "string" },
                        ["coming"] = new JsonObject { ["type"] = "boolean" }, // FB-72: false unless the line says it may be true
                    },
                },
            },
        },
    };

    /// <summary>
    /// The answer read back, held to what was asked: a reaction or comment only on a line they were shown and allowed, no
    /// reaction on their own line, a reply only to a comment that's there, a photo area only from the list, words made safe
    /// (no emoji, no brackets) and cut to length. Anything unreadable is left out, never guessed.
    /// </summary>
    public static Result Read(string text, IReadOnlyList<Seen> seen, bool post, IReadOnlyList<string> photoAreas)
    {
        var r = new Result();
        var o = LlmClient.ExtractJsonObject(text ?? "");
        if (o == null) return r;
        r.Ok = true;
        string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        r.Handle = LineBook.Handle(S(o["handle"]));
        if (post && o["post"] is JsonObject po)
        {
            r.Post = Clean(S(po["text"]), LineBook.MaxText);
            if (po["photo"] is JsonObject ph && r.Post.Length > 0)
            {
                string area = S(ph["area"]).Trim();
                string match = photoAreas.FirstOrDefault(a => string.Equals(a, area, StringComparison.OrdinalIgnoreCase)) ?? "";
                string shows = Clean(S(ph["shows"]), LineBook.MaxShows);
                if (match.Length > 0 && shows.Length > 0)
                {
                    r.PhotoArea = match;
                    r.PhotoShows = shows;
                    r.PhotoOfMe = ph["me"] is JsonValue mv && mv.TryGetValue<bool>(out var me) && me;
                }
            }
        }
        if (o["lines"] is JsonArray arr)
        {
            foreach (var n in arr.OfType<JsonObject>())
            {
                string key = S(n["line"]).Trim();
                var s = seen.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (s == null || r.Acts.Any(a => a.Key == s.Key)) continue;
                var act = new Act { Key = s.Key, Why = Clean(S(n["why"]), LineBook.MaxWhy) };
                string react = S(n["react"]).Trim().ToLowerInvariant();
                if (!s.Own && s.MayReact) act.React = react == "like" ? true : react == "dislike" ? false : null;
                if (s.MayComment) act.Comment = Clean(S(n["comment"]), LineBook.MaxComment); // their own line too: only when asked (review NL-9)
                string reply = S(n["reply_to"]).Trim();
                if (act.Comment.Length > 0 && s.Comments.Any(c => c.key.Equals(reply, StringComparison.OrdinalIgnoreCase)))
                    act.ReplyTo = s.Comments.First(c => c.key.Equals(reply, StringComparison.OrdinalIgnoreCase)).key;
                act.Coming = s.MayCome && n["coming"] is JsonValue cv && cv.TryGetValue<bool>(out var come) && come; // FB-72
                if (act.React != null || act.Comment.Length > 0 || act.Coming) r.Acts.Add(act);
            }
        }
        return r;
    }

    private static string Clean(string s, int max) => LineBook.CleanText(ReplyParser.Quotable(Texting.StripEmoji(s.Trim().Trim('"')), max + 40), max);

    /// <summary>
    /// What they keep of this visit (a Talk in their memory, kind Nightline): what they posted, and each line they did
    /// something with, in their own terms. Empty when nothing happened worth remembering.
    /// </summary>
    public static (List<string> lines, string summary) Remembered(Result r, IReadOnlyList<Seen> seen, string photoShows)
    {
        var lines = new List<string>();
        var bits = new List<string>();
        if (r.Post.Length > 0)
        {
            lines.Add($"You posted: \"{r.Post}\"" + (photoShows.Length > 0 ? $" with a photo of {photoShows}" : ""));
            bits.Add("posted \"" + Short(r.Post) + "\"");
        }
        foreach (var a in r.Acts)
        {
            var s = seen.First(x => x.Key == a.Key);
            string whose = s.Own ? "your own line" : $"{s.AuthorName}'s line";
            string what = $"\"{Short(s.Text, 120)}\"" + (s.PhotoShows.Length > 0 ? $" (a photo of {Short(s.PhotoShows, 120)})" : s.YourPhoto ? " (with a photo they took)" : "");
            string did = a.React == true ? "liked it" : a.React == false ? "disliked it" : "";
            if (a.Comment.Length > 0) did += (did.Length > 0 ? " and " : "") + (a.ReplyTo.Length > 0 ? "answered a comment: " : "commented: ") + $"\"{a.Comment}\"";
            lines.Add($"On Nightline you saw {whose} {what}; you {did}" + (a.Why.Length > 0 ? $" ({a.Why})" : "") + ".");
            bits.Add($"{(a.React == true ? "liked" : a.React == false ? "disliked" : "commented on")} {(s.Own ? "your own line" : s.AuthorName + "'s")}");
        }
        foreach (var s in seen.Where(x => x.Own && x.NewReactions.Count > 0))
        {
            var ups = s.NewReactions.Where(x => x.up).Select(x => x.name).ToList();
            var downs = s.NewReactions.Where(x => !x.up).Select(x => x.name).ToList();
            if (ups.Count > 0) lines.Add($"{string.Join(", ", ups.Take(6))} liked your line \"{Short(s.Text, 80)}\".");
            if (downs.Count > 0) lines.Add($"{string.Join(", ", downs.Take(6))} disliked your line \"{Short(s.Text, 80)}\".");
        }
        string summary = bits.Count == 0 ? (lines.Count > 0 ? "looked at who reacted to your line" : "") : "On Nightline you " + string.Join("; ", bits.Take(4));
        return (lines, summary);
    }

    private static string Short(string s, int max = 60) => s.Length > max ? s[..max].TrimEnd() + "…" : s;

    /// <summary>
    /// Words that point at a child (a kid, a little girl, a son...) or name someone the mod knows is under 18 (their first
    /// name, or its possessive): such a photo is never asked for. Letters only, whole words (review NL-7, NL-7b).
    /// Over-blocks on purpose ("son of a...", a "girl" who's an adult): the line still goes up, without a photo.
    /// </summary>
    /// <summary>
    /// FB-78 (the author: "could you have the posts say the time when they were posted?"): a line's time as its card
    /// shows it: "now" under a minute, the clock today ("9:16 AM"), "Yesterday", else its day ("Day 14").
    /// </summary>
    public static string PostedAt(long now, long minute)
    {
        if (minute < 0) return "";
        long day = minute / 1440, today = now < 0 ? day : now / 1440;
        if (now >= 0 && now - minute is >= 0 and < 1) return "now";
        if (day >= today)
        {
            int m = (int)(minute % 1440), h = m / 60;
            return $"{(h % 12 == 0 ? 12 : h % 12)}:{m % 60:00} {(h < 12 ? "AM" : "PM")}";
        }
        return day == today - 1 ? "Yesterday" : $"Day {day}";
    }

    public static bool PointsAtAChild(string text, IEnumerable<string> minorNames)
    {
        var sb = new StringBuilder(" ");
        foreach (char ch in (text ?? "").ToLowerInvariant()) sb.Append(char.IsLetter(ch) ? ch : ' ');
        sb.Append(' ');
        string t = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), " {2,}", " ");
        foreach (var w in new[] { " kid", " child", " toddler", " baby", " babies", " daughter", " son ", " sons ", " boy ", " boys ", " girl ", " girls ", " teen", " schoolkid", " little one", " minor ", " infant" })
            if (t.Contains(w)) return true;
        foreach (var n in minorNames)
        {
            string first = new string((n ?? "").ToLowerInvariant().Split(' ')[0].Where(char.IsLetter).ToArray());
            if (first.Length >= 3 && (t.Contains(" " + first + " ") || t.Contains(" " + first + "s "))) return true;
        }
        return false;
    }

    /// <summary>
    /// The picture's prompt for the image service: a candid phone photo in the city's own look (the reference pictures are
    /// the place), what the poster said it shows, and, when it's of them, the first picture as who they are.
    /// </summary>
    public static string ImagePrompt(string shows, string area, bool ofThem, string theirLook)
    {
        var sb = new StringBuilder();
        sb.Append("A candid smartphone photo someone just took and posted. ");
        sb.Append($"Place: {area}, in Nivalis, a vast vertical cyberpunk megacity built over the sea (neon signs, rain, struts and plasteel, holo-ads). ");
        sb.Append($"It shows: {shows}. ");
        if (ofThem) sb.Append($"The person in the first reference picture is in the photo and must look exactly like them (face, hair, clothes){(theirLook.Length > 0 ? ": " + theirLook : "")}. ");
        sb.Append(ofThem ? "Use the other reference pictures only for the place's look (architecture, light, colours). " : "Use the reference pictures for the place's look (architecture, light, colours). ");
        sb.Append("Natural phone-camera framing and light, slightly imperfect. Adults only. No text, captions, logos or watermarks.");
        return sb.ToString();
    }
}
