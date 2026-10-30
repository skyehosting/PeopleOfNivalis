using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Phone;

/// <summary>
/// Texts with the people who gave you their number (the author: "cute text messages between people and friends,
/// relationships"): the request for their answer, or for a text they send first, in their own voice and with their memory
/// of you; and their answer split into the short messages a phone shows. No Unity, tested.
/// </summary>
public static class Texting
{
    public const int MaxMessages = 3, MaxChars = 320, ThreadShown = 24, TogetherShown = 8;

    /// <summary>How close they are, as the phone's tag says it: "Partner", "Friend" or "".</summary>
    public static string Tag(NpcState st) => st.Romance.Length > 0 ? "Partner" : st.Familiarity == "friend" ? "Friend" : "";

    /// <summary>
    /// Their answer to what you texted (<paramref name="first"/> false), or a text they send you first. The thread is what's
    /// been texted so far, oldest first.
    /// </summary>
    public static LlmRequest Request(NpcRecord rec, GameSnapshot snap, IReadOnlyList<PhoneBook.Text> thread, bool first, double friendliness, string playerGender, int maxTokens, string note = "")
    {
        int tokens = Math.Clamp(maxTokens, 200, 1500);
        // Each written out (the usage test reads them): your texts are your conversation; one they send first is like a greeting.
        var req = first
            ? new LlmRequest { Purpose = "text first", MaxTokens = tokens, PlainTextOk = true, Background = true }
            : new LlmRequest { Purpose = "text", MaxTokens = tokens, PlainTextOk = true };
        req.System.Add(Prompts.IdentityBlock(rec, structured: false, friendliness, playerGender));
        if (CustomInstructions.Block() is { Length: > 0 } own) req.System.Add(own);
        req.System.Add(Block(rec, snap, first, note));
        if (Prompts.SpeakIn() is { Length: > 0 } speak) req.System.Add(speak);
        req.System.Add(Prompts.SafetyLast);

        // The texts as the conversation: theirs are "assistant", yours "user"; yours in a row are one message.
        var shown = thread.TakeLast(ThreadShown).ToList();
        foreach (var t in shown)
        {
            string role = t.From == PhoneBook.You ? "user" : "assistant";
            if (req.Messages.Count > 0 && req.Messages[^1].Role == role)
                req.Messages[^1] = new LlmMessage(role, req.Messages[^1].Content + "\n" + t.Body);
            else req.Messages.Add(new LlmMessage(role, t.Body));
        }
        if (first || req.Messages.Count == 0 || req.Messages[^1].Role != "user")
            req.Messages.Add(new LlmMessage("user", first
                ? "[You pick up your phone and text them first. One or two short texts, only the words you'd type.]"
                : "[Answer their texts. Only the words you'd type.]"));
        return req;
    }

    /// <summary>What a text conversation is, and who they are to you, for the request.</summary>
    public static string Block(NpcRecord rec, GameSnapshot snap, bool first, string note = "")
    {
        var st = rec.State;
        var sb = new StringBuilder();
        sb.AppendLine("[TEXTING]");
        sb.AppendLine($"It's {(snap.DayOfWeek.Length > 0 ? snap.DayOfWeek : "a weekday")}, day {snap.GameDay}, {snap.Clock}. You're not with them: you're texting on your phone, wherever you are right now.");
        sb.AppendLine("Text the way you'd really text this person: short, casual, in your own voice. One to three short messages, each on its own line. " +
                      "No actions, no stage directions, no asterisks, no quotation marks, no emoji (a text smiley like :) or <3 is fine if it's you). " +
                      "Never mention being an AI, a game or a prompt. You can't see them or anything around them.");
        sb.AppendLine("Hidden notes read here (on their own line, never typed to them): [[affinity: +N]] or [[affinity: -N]] for how what they texted made you feel (1-10), and [[mood: ...]]. Nothing else.");
        sb.AppendLine(first
            ? "You're texting them first because they're on your mind: a plan, something that happened to you, a question, checking in, or (if you two are together) missing them. Keep it like you."
            : "Answer what they texted. If it's late, you might be sleepy; if you're busy, you might be brief. You can ask something back.");
        if (note.Length > 0) sb.AppendLine(note); // FB-70: they didn't look for a while, or saw it and didn't answer then
        sb.AppendLine();
        sb.AppendLine("[THEM]");
        sb.AppendLine(st.PlayerName.Length > 0 ? $"Their name is {st.PlayerName}." : "You don't know their name; they're the newcomer who has your number.");
        sb.AppendLine("You gave them your number, so you're in each other's phones.");
        if (Features.Relationships) sb.AppendLine(Prompts.AffinityWords(st.Affinity));
        if (st.Romance.Length > 0) sb.AppendLine(st.Romance == Npc.Romance.Living ? "You two are together and live together." : "You two are together (dating).");
        if (Prompts.PlayerPronounsNote("They") is { Length: > 0 } pronouns) sb.AppendLine(pronouns);
        if (st.PlayerFacts.Count > 0) sb.AppendLine("What you know about them: " + string.Join("; ", st.PlayerFacts.TakeLast(10)) + ".");
        if (rec.Meetings.Count > 0)
        {
            sb.AppendLine("Your last times together (your diary):");
            foreach (var m in rec.Meetings.TakeLast(6)) sb.AppendLine($"- Day {m.GameDay}{(m.Location.Length > 0 ? ", " + m.Location : "")}: {m.Summary}");
        }
        if (rec.MemorySummary.Length > 0) sb.AppendLine("Your memory of them: " + rec.MemorySummary);
        var together = rec.History.Where(l => !l.Folded && l.Role is "player" or "npc").TakeLast(TogetherShown).ToList();
        if (together.Count > 0)
        {
            sb.AppendLine("The last things said in person:");
            foreach (var l in together) sb.AppendLine($"- {(l.Role == "npc" ? "You" : "They")}: {ReplyParser.Quotable(l.Text, 200)}");
        }
        var life = rec.LifeLog.TakeLast(3).ToList();
        if (life.Count > 0)
        {
            sb.AppendLine("Your life lately:");
            foreach (var e in life) sb.AppendLine($"- Day {e.GameDay}: {e.Text}");
        }
        var p = rec.Persona;
        sb.AppendLine($"On your mind: {p.CurrentWorry}. What you want right now: {p.ShortTermGoal}.");
        return sb.ToString();
    }

    private static readonly Regex Emoji = new(@"[☀-➿️‍]|\p{Cs}", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[ \t]{2,}", RegexOptions.Compiled);

    /// <summary>Emoji out (the game's font has none: they'd show as boxes); text smileys stay.</summary>
    public static string StripEmoji(string s) => Spaces.Replace(Emoji.Replace(s ?? "", ""), " ").Trim();

    /// <summary>
    /// Their answer as the phone shows it: the words only (notes, actions, labels, quotes out), at most three messages,
    /// each at most <see cref="MaxChars"/>. Also the affinity note, when there was one.
    /// </summary>
    public static (List<string> messages, int affinity, string? mood, bool coming) Read(string text, string name, string nickname)
    {
        var messages = new List<string>();
        int affinity = 0;
        string? mood = null;
        // FB-72: [[coming]], they're on their way to keep you company (never typed to you).
        bool coming = Coming.IsMatch(text ?? "");
        text = Coming.Replace(text ?? "", "");
        foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
        {
            var p = ReplyParser.Parse(raw, name, nickname);
            affinity += p.AffinityDelta;
            mood ??= p.Mood;
            string said = StripEmoji(p.Spoken).Trim().Trim('"', '“', '”').Trim();
            if (said.Length == 0) continue;
            if (said.Length > MaxChars) said = said[..MaxChars].TrimEnd() + "…";
            messages.Add(said);
            if (messages.Count >= MaxMessages) break;
        }
        return (messages, Math.Clamp(affinity, -10, 10), mood, coming && messages.Count > 0);
    }

    private static readonly Regex Coming = new(@"\[\[\s*coming\s*\]\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Ava (the author: "your first contact that should always appear is Ava ... you'd have to pull every line of dialogue
    /// she has ... so the ai can really understand how ava speaks ... always make sure leaks are prevented"). She has no
    /// record of her own: who she is comes only from her story lines the player has seen (StoryBook: the speech sheet, her
    /// own words, the conversations), never from the game's files, and she says nothing about herself or the story beyond
    /// them. <paramref name="voiceOffer"/>: she may ask for her words to go as a voice note ([[voice]]).
    /// </summary>
    public static LlmRequest AvaRequest(string name, string playerName, string sheet, IReadOnlyList<string> ownLines, IReadOnlyList<StoryBook.Scene> scenes,
        GameSnapshot snap, IReadOnlyList<PhoneBook.Text> thread, bool first, int maxTokens, string note, bool voiceOffer)
    {
        int tokens = Math.Clamp(maxTokens, 200, 1500);
        var req = first
            ? new LlmRequest { Purpose = "text first", MaxTokens = tokens, PlainTextOk = true, Background = true }
            : new LlmRequest { Purpose = "text", MaxTokens = tokens, PlainTextOk = true };
        var sb = new StringBuilder();
        sb.AppendLine("[WHO YOU ARE]");
        sb.AppendLine($"You are {name}. You're with them through their HUD; on their phone, your words come as texts. Everything you know about " +
                      "yourself, them and the city is in the lines below, from your conversations together: you are exactly who those lines show.");
        sb.AppendLine("Never say more about what you are, where you are or where you came from than those lines already say; never hint at things " +
                      "that haven't happened in them; if they ask about something the lines don't cover, answer as you would without inventing it.");
        if (CustomInstructions.Block() is { Length: > 0 } own) req.System.Add(own);
        req.System.Add(sb.ToString());
        if (Prompts.StoryVoiceBlock(sheet, ownLines, examples: 10) is { Length: > 0 } voice) req.System.Add(voice);
        if (Prompts.StoryBlock(scenes, playerName) is { Length: > 0 } story) req.System.Add(story);
        var t = new StringBuilder();
        t.AppendLine("[TEXTING]");
        t.AppendLine($"It's {(snap.DayOfWeek.Length > 0 ? snap.DayOfWeek : "a weekday")}, day {snap.GameDay}, {snap.Clock}.");
        t.AppendLine("Text the way you talk in those lines, as short texts: one to three, each on its own line. No actions, no asterisks, " +
                     "no quotation marks, no emoji. Never mention a game, a prompt or a model.");
        // FB-78: those lines are how she talks, not things to say again.
        t.AppendLine("Say something new each time, in your own words: never repeat a line from those conversations, or a text you've already sent.");
        if (voiceOffer) t.AppendLine("If you'd rather say this one out loud (a voice note), add [[voice]] on its own line. Only when you want to; usually not.");
        t.AppendLine(first ? "You're texting them first, the way you do: a good morning, a thought, a nudge about what's ahead, as those lines show you." : "Answer what they texted.");
        if (note.Length > 0) t.AppendLine(note);
        if (Prompts.PlayerPronounsNote("They") is { Length: > 0 } pronouns) t.AppendLine(pronouns);
        req.System.Add(t.ToString());
        if (Prompts.SpeakIn() is { Length: > 0 } speak) req.System.Add(speak);
        req.System.Add(Prompts.SafetyLast);
        foreach (var x in thread.Where(x => !x.Story).TakeLast(ThreadShown))
        {
            string role = x.From == PhoneBook.You ? "user" : "assistant";
            if (req.Messages.Count > 0 && req.Messages[^1].Role == role) req.Messages[^1] = new LlmMessage(role, req.Messages[^1].Content + "\n" + x.Body);
            else req.Messages.Add(new LlmMessage(role, x.Body));
        }
        if (first || req.Messages.Count == 0 || req.Messages[^1].Role != "user")
            req.Messages.Add(new LlmMessage("user", first ? "[You text them first. Only the words you'd type.]" : "[Answer their texts. Only the words you'd type.]"));
        return req;
    }

    private static readonly System.Text.RegularExpressions.Regex SentenceEnd = new(@"(?<=[.!?…])\s+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string Plain(string s) =>
        string.Join(' ', new string((s ?? "").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// FB-78 (the author: "she seems to just be repeating lines from her history"): her texts with every sentence of three
    /// words or more that she's already said word for word (a story line, a text she sent, an earlier text of this answer)
    /// taken out; a text left with nothing goes. Short words ("Yes.") may come again.
    /// </summary>
    public static List<string> Fresh(IEnumerable<string> texts, IEnumerable<string> said)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in said ?? Enumerable.Empty<string>())
            foreach (var part in SentenceEnd.Split(s ?? "")) if (Plain(part) is { } p && p.Split(' ').Length >= 3) seen.Add(p);
        var fresh = new List<string>();
        foreach (var t in texts ?? Enumerable.Empty<string>())
        {
            var keep = new List<string>();
            foreach (var part in SentenceEnd.Split(t ?? ""))
            {
                string p = Plain(part);
                if (p.Length == 0) continue;
                if (p.Split(' ').Length >= 3 && !seen.Add(p)) continue; // said already (or earlier in this answer)
                keep.Add(part.Trim());
            }
            if (keep.Count > 0) fresh.Add(string.Join(" ", keep));
        }
        return fresh;
    }

    /// <summary>Whether her answer asked to go as a voice note ([[voice]]).</summary>
    public static bool WantsVoice(string text) => (text ?? "").Contains("[[voice]]", StringComparison.OrdinalIgnoreCase);

    /// <summary>The scene line kept in their record (so a chat in person knows what you texted about).</summary>
    public static string SceneLine(int day, string clock, IReadOnlyList<string> yours, IReadOnlyList<string> theirs, bool first)
    {
        string Q(IEnumerable<string> xs) => string.Join(" / ", xs.Select(x => "\"" + ReplyParser.Quotable(x, 160) + "\""));
        return first
            ? $"[By text, day {day}, {clock}: you texted them first: {Q(theirs)}]"
            : $"[By text, day {day}, {clock}: they texted you {Q(yours)}; you texted back {Q(theirs)}]";
    }
}
