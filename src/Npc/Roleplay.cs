using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace NivalisAIChat.Npc;

/// <summary>
/// Roleplay (a player's idea, the author's spec: "after an ai's line they could display like a roleplay action that could
/// help provide detail, body movements, etc. 'she looks at him', her, etc depending on what gender the user set, otherwise
/// if not set use them ... they don't have to appear all the time, I want to allow the AI to genuinely control this part"):
/// now and then, with a reply, a short stage direction of what they do as they say it ("She rolls her eyes."), shown under
/// their line in slanted gold-brown text. The AI decides when (the reply's action field, or a [[action: ...]] note, or the
/// *rolls her eyes* a model writes into its words anyway); the code keeps it to one plain third-person sentence about their
/// own body, in their pronouns, the player in theirs (they/them when not set). When it names a gesture the game has
/// (a nod, a wave, looking down), their body does it too. No Unity, tested.
/// </summary>
public static class Roleplay
{
    public sealed record Pronouns(string Subject, string Object, string Possessive)
    {
        public static readonly Pronouns She = new("she", "her", "her"), He = new("he", "him", "his"), They = new("they", "them", "their"), It = new("it", "it", "its");
        public string Cap => Subject.Length == 0 ? Subject : char.ToUpperInvariant(Subject[0]) + Subject[1..];
    }

    /// <summary>Someone's pronouns from their record ("she/her", "he/they", "they/them"): the first set named; they/them otherwise.</summary>
    public static Pronouns Of(string? pronouns)
    {
        string p = (pronouns ?? "").Trim().ToLowerInvariant();
        if (p.StartsWith("she")) return Pronouns.She;
        if (p.StartsWith("he")) return Pronouns.He;
        if (p.StartsWith("it")) return Pronouns.It;
        return Pronouns.They;
    }

    /// <summary>The player's, from their Gender setting ("woman", "trans man", "non-binary"...): they/them when it isn't set or isn't one of them.</summary>
    public static Pronouns Player(string? gender)
    {
        string g = (gender ?? "").Trim().ToLowerInvariant();
        if (g.Length == 0) return Pronouns.They;
        if (Regex.IsMatch(g, @"\b(she|her)\b")) return Pronouns.She;
        if (Regex.IsMatch(g, @"\b(he|him)\b")) return Pronouns.He;
        if (Regex.IsMatch(g, @"non[- ]?binary|enby|agender|genderfluid|they")) return Pronouns.They;
        if (Regex.IsMatch(g, @"\b(woman|female|girl|lady|femme)\b")) return Pronouns.She;
        if (Regex.IsMatch(g, @"\b(man|male|guy|boy|dude)\b")) return Pronouns.He;
        return Pronouns.They;
    }

    private static readonly System.Text.RegularExpressions.Regex Starred = new(@"\*([^*\n]{1,200})\*", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// CF-20 (a player: "please add a feature for me to log my action toward the npc as well"): what the player writes in
    /// *asterisks* is something they do, put to them as [They do: ...]. Given text already made safe (EscapePlayerText:
    /// every bracket round), so the only square brackets are these. Roleplay off: left as it is.
    /// </summary>
    public static string PlayerActions(string text)
    {
        if (!Features.Roleplay || string.IsNullOrEmpty(text) || text.IndexOf('*') < 0) return text;
        return Starred.Replace(text, m =>
        {
            string a = m.Groups[1].Value.Trim().TrimEnd('.', '!', '?', ',').Trim();
            return a.Length == 0 ? m.Value : $"[They do: {a}.]";
        });
    }

    /// <summary>CF-20: how the player's own actions are to be read (with Roleplay on).</summary>
    public const string PlayerActionRule = "The stranger may act as well as speak: [They do: ...] in what they say is something they visibly do, not words. " +
                                           "React to it as something that just happened in front of you (you saw it, felt it, were handed it), the way you would.";

    /// <summary>The reply's action field, as the AI is told it (JSON: a field; plain text: a [[action: ...]] note).</summary>
    public static string Instruction(Pronouns npc, Pronouns player, bool structured)
    {
        string ex = $"\"{npc.Cap} {Verb(npc, "roll", "rolls")} {npc.Possessive} eyes.\", \"{npc.Cap} {Verb(npc, "glance", "glances")} at {player.Object}, then away.\", " +
                    $"\"{npc.Cap} {Verb(npc, "lean", "leans")} back, arms folded.\"";
        string body = "now and then, when it shows something your words don't, a short stage direction of what you visibly do as you say it, in the third " +
                      $"person and present tense, one sentence ({ex}): your face, eyes, hands, posture, where you look, what you do with what's in your hands. " +
                      $"Call yourself {npc.Subject}/{npc.Object} and the person in front of you {player.Subject}/{player.Object}. Only what anyone could see you " +
                      "do: never your thoughts, never what they do, never words. Fit it to [RIGHT NOW]: where you are and what you were doing there " +
                      "(at work, at your stall or counter: nothing you'd only do off duty; no drink, food or thing you wouldn't have with you there). " +
                      "Most replies have none; when it would only repeat your words, leave it empty.";
        return structured ? "- action: " + body + " Otherwise an empty string." : "[[action: what you visibly do]] — " + body;
    }

    private static string Verb(Pronouns p, string plural, string singular) => p.Subject == "they" ? plural : singular;

    private static readonly Regex Quoted = new("[\"“”][^\"“”]*[\"“”]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex FirstPerson = new(@"(?i)^\s*(i|i'm|im|my|me)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex YouWord = new(@"(?i)\b(you|your)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The action as it's shown: one line, nothing quoted (no speech), no brackets, braces, tags or asterisks, at most about
    /// 160 characters (cut at a sentence end), starting with a capital and ending with a stop. A bare verb ("rolls her eyes")
    /// gets their subject in front; "you" and "your" become the player's pronouns. Written in the first person, as a thought,
    /// or out of character: "" (nothing shown).
    /// </summary>
    public static string Clean(string? text, Pronouns npc, Pronouns player)
    {
        string t = (text ?? "").Replace('\n', ' ').Replace('\r', ' ');
        t = Quoted.Replace(t, " ");
        t = new string(t.Where(c => c is not ('[' or ']' or '{' or '}' or '<' or '>' or '*' or '"' or '“' or '”' or '_')).ToArray());
        t = Spaces.Replace(t, " ").Trim().Trim('(', ')', '-', '—', ' ');
        t = Ai.ReplyParser.Blank(t);
        if (t.Length < 3 || FirstPerson.IsMatch(t) || Ai.ReplyParser.LooksOutOfCharacter(t, false)) return "";
        t = YouWord.Replace(t, m => m.Value.Equals("your", StringComparison.OrdinalIgnoreCase) ? player.Possessive : player.Object);
        // A bare verb ("rolls her eyes", "shrugs") is theirs: their subject in front.
        if (char.IsLower(t[0]) && !t.StartsWith(npc.Subject + " ", StringComparison.Ordinal))
            t = npc.Cap + " " + t;
        t = char.ToUpperInvariant(t[0]) + t[1..];
        if (t.Length > 160)
        {
            int end = t.LastIndexOfAny(new[] { '.', '!', '?', '…' }, 159);
            t = end > 20 ? t[..(end + 1)] : t[..160].TrimEnd(',', ';', ' ') + "…";
        }
        if (!".!?…".Contains(t[^1])) t += ".";
        return t;
    }

    /// <summary>A gesture of the game's that the action names (a nod, a wave, a laugh...), for their body; "" when none does.</summary>
    public static string GestureFor(string action)
    {
        string a = (action ?? "").ToLowerInvariant();
        if (a.Length == 0) return "";
        if (Regex.IsMatch(a, @"\bshak(e|es|ing) (her|his|their|its) head\b")) return "shake head";
        if (Regex.IsMatch(a, @"\bnod(s|ding|ded)?\b")) return "nod";
        if (Regex.IsMatch(a, @"\bwav(e|es|ing) (goodbye|bye)\b")) return "wave goodbye";
        if (Regex.IsMatch(a, @"\bwav(e|es|ing)\b")) return "wave";
        if (Regex.IsMatch(a, @"\bpoint(s|ing)?\b")) return "point";
        if (Regex.IsMatch(a, @"\b(look(s|ing)?|glanc(e|es|ing)|stare(s)?) (down|at the (floor|ground))\b")) return "look down";
        if (Regex.IsMatch(a, @"\b(laugh(s|ing)?|chuckl(e|es|ing)|giggl(e|es|ing))\b")) return "laugh";
        if (Regex.IsMatch(a, @"\b(sigh(s)?|slump(s)?)\b")) return "sad";
        return "";
    }
}
