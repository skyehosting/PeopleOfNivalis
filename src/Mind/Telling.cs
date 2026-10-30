using System;
using System.Collections.Generic;
using System.Linq;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;

namespace NivalisAIChat.Mind;

/// <summary>
/// The inner-life round, step 4 (gossip stances and mutation in code): in a residents' conversation, the code decides
/// who brings up something they saw or heard, to whom, and how (straight, going easy on someone they like, or making it
/// sound worse), from Talk of the Town's telling rules; the AI only says it. Once it has been said, each listener keeps it
/// as hearsay, changed the way stories change (WitnessBook.Hear). Never told to the person it's about, never told while
/// they're there, never anything with someone under 18. No Unity, tested.
/// </summary>
public static class Telling
{
    public const string Straight = "straight", Softened = "softened", Embellished = "embellished";

    public sealed class Speaker
    {
        public string Id = "", Name = "";
        public Persona Persona = new();
    }

    public sealed class Plan
    {
        public string TellerId = "", TellerName = "";
        public Belief Belief = null!;
        public string Stance = Straight;
        /// <summary>The line for the writer ([ON THEIR MINDS]).</summary>
        public string Note = "";
    }

    /// <summary>How readily they pass things on: nosy, chatty, theatrical people more; guarded, shy, stoic ones less.</summary>
    public static double Gossipy(Persona p)
    {
        string t = (p.Temperament + " " + string.Join(" ", p.Personality ?? new List<string>())).ToLowerInvariant();
        double g = 1.0;
        if (new[] { "nosy", "gossip", "chatty", "theatrical", "over-sharer", "warm" }.Any(t.Contains)) g += 0.4;
        if (new[] { "guarded", "terse", "shy", "stoic", "suspicious", "deadpan" }.Any(t.Contains)) g -= 0.4;
        string v = (p.Speech?.Verbosity ?? "").ToLowerInvariant();
        if (v is "chatty" or "rambling") g += 0.2;
        else if (v == "terse") g -= 0.2;
        return Math.Clamp(g, 0.3, 1.6);
    }

    /// <summary>How straight they tell things, 0..1, from their honesty dice.</summary>
    public static double Honesty(Persona p)
    {
        string h = (p.Honesty ?? "").ToLowerInvariant();
        if (h.Contains("to a fault")) return 1.0;
        if (h.Contains("guarded")) return 0.85;
        if (h.Contains("mostly honest")) return 0.8;
        if (h.Contains("white lies")) return 0.6;
        if (h.Contains("embellish")) return 0.3;
        if (h.Contains("lies easily")) return 0.2;
        return 0.7;
    }

    /// <summary>
    /// What gets told in this conversation (at most <paramref name="max"/>, one per teller): each teller's strongest belief
    /// worth telling to these listeners, by Talk of the Town's tell score (how strongly it's with them, how much of a talker
    /// they are, how close they are to the listeners), with the stance it's told in. Nothing about anyone here, nothing
    /// every listener has heard already, nothing secret unless the listeners are close, nothing to or about anyone under 18.
    /// </summary>
    public static List<Plan> Pick(WitnessBook book, IReadOnlyList<Speaker> people, Func<string, string, PairBook.Pair?> pair, long now, Random rng,
                                  int max = 2, string player = "the newcomer")
    {
        var plans = new List<Plan>();
        if (people.Count < 2 || now < 0) return plans;
        var here = people.Select(p => p.Id).ToHashSet();
        foreach (var teller in people.OrderBy(_ => rng.Next()))
        {
            if (plans.Count >= max) break;
            var listeners = people.Where(p => p.Id != teller.Id).ToList();
            // Review (core IL-2): what each listener knows already (saw it too, or heard it): nothing new to them.
            var theyKnow = listeners.Select(l => book.Of(l.Id, now).Select(b => b.EventId).ToHashSet()).ToList();
            double closeness = listeners.Average(l => PairBook.Weight(pair(teller.Id, l.Id))) / 1.5;
            double gossipy = Gossipy(teller.Persona);
            Plan? best = null;
            double bestScore = 0;
            foreach (var b in book.Of(teller.Id, now))
            {
                if (b.Involves(teller.Id) && b.How != Belief.Lived) continue; // their own doings are theirs to bring up, not gossip
                if (here.Contains(b.Actor) || here.Contains(b.Target)) continue; // never behind someone's back while they're here
                if (listeners.Select((l, i) => b.ToldTo.Contains(l.Id) || b.Source == l.Id || theyKnow[i].Contains(b.EventId)).All(x => x)) continue;
                double risk = b.Secret ? Math.Max(0, 1.2 - closeness) * 0.5 : 0;
                double score = WitnessBook.Score(b, now) * gossipy * closeness - risk;
                if (score <= bestScore) continue;
                bestScore = score;
                best = new Plan { TellerId = teller.Id, TellerName = teller.Name, Belief = b };
            }
            if (best == null || bestScore < 0.1 || rng.NextDouble() >= Math.Clamp(bestScore * 1.5, 0.15, 0.85)) continue;
            best.Stance = StanceFor(teller, best.Belief, listeners, pair, rng);
            best.Note = NoteFor(best, player, now);
            plans.Add(best);
        }
        return plans;
    }

    /// <summary>
    /// Straight by default; softened when the teller likes who it's about or a listener is close to them; made worse by
    /// someone who isn't too honest, now and then.
    /// </summary>
    public static string StanceFor(Speaker teller, Belief b, IReadOnlyList<Speaker> listeners, Func<string, string, PairBook.Pair?> pair, Random rng)
    {
        string subject = b.Actor == Happenings.Player ? b.Target : b.Actor;
        bool likes = subject.Length > 0 && subject != Happenings.Player && (pair(teller.Id, subject)?.Affinity ?? 0) >= 20;
        bool listenerClose = subject.Length > 0 && subject != Happenings.Player && listeners.Any(l => (pair(l.Id, subject)?.Affinity ?? 0) >= 50);
        if (likes || listenerClose) return Softened;
        double honest = Honesty(teller.Persona);
        if (honest < 0.45 && rng.NextDouble() < 0.7) return Embellished;
        return Straight;
    }

    /// <summary>The writer's line: who may bring up what, and how. Hearsay says where it came from, so a change reads as gossip, not a bug.</summary>
    public static string NoteFor(Plan plan, string player, long now)
    {
        string first = Ai.Prompts.FirstName(plan.TellerName);
        string what = Happenings.Words(plan.Belief, "", player, now, how: false); // in the third person: "the newcomer stood Mira up"
        string how = plan.Belief.How switch
        {
            Belief.Saw => "they saw it themselves",
            Belief.Heard => "they heard it going on nearby",
            Belief.Told => $"they heard it from {Ai.Prompts.FirstName(plan.Belief.SourceName.Length > 0 ? plan.Belief.SourceName : "someone")} and say so",
            _ => "it happened to them",
        };
        string stance = plan.Stance switch
        {
            Softened => "They go easy on it.",
            Embellished => "They make it sound bigger than it was.",
            _ => "They tell it as they know it.",
        };
        return $"{first} may bring up, if the talk allows: {what} ({how}). {stance}";
    }

    /// <summary>
    /// Review (core IL-1): whether the teller's own lines actually brought it up (the writer was only told they may): one of
    /// them names someone in it, the place, or says what kind of thing it was (a row, a breakup, being stood up...), or, for
    /// something about the player, names them or "the newcomer".
    /// </summary>
    public static bool Said(Plan plan, IEnumerable<string> tellerLines, string playerName = "")
    {
        string text = " " + string.Join(" ", tellerLines).ToLowerInvariant() + " ";
        if (text.Trim().Length == 0) return false;
        bool Word(string w) => w.Length >= 2 && System.Text.RegularExpressions.Regex.IsMatch(text, $@"(?<![\p{{L}}]){System.Text.RegularExpressions.Regex.Escape(w.ToLowerInvariant())}(?![\p{{L}}])");
        var b = plan.Belief;
        foreach (var (id, name) in new[] { (b.Actor, b.ActorName), (b.Target, b.TargetName) })
        {
            if (id == Happenings.Player) { if (Word("newcomer") || Word("stranger") || (playerName.Length > 0 && Word(Ai.Prompts.FirstName(playerName)))) return true; }
            else if (id != plan.TellerId && name.Length > 0 && char.IsUpper(name[0]) && Word(Ai.Prompts.FirstName(name))) return true;
        }
        if (b.Place.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w.Length > 3 && Word(w))) return true;
        string[] kind = b.Kind switch
        {
            Happenings.Argued => new[] { "row", "argument", "arguing", "argued", "fight", "fighting", "shouting", "yelling", "screaming" },
            Happenings.BrokeUp => new[] { "broke up", "split up", "split", "dumped", "breakup", "over between" },
            Happenings.GotTogether or Happenings.PlayerCouple => new[] { "together", "dating", "seeing each other", "an item", "a couple" },
            Happenings.Flirted => new[] { "flirting", "flirted", "flirt", "all over" },
            Happenings.Confronted => new[] { "had it out", "confronted", "showdown" },
            Happenings.Jealous => new[] { "jealous", "jealousy" },
            Happenings.Laughed => new[] { "laughing", "laughed", "laugh" },
            Happenings.StoodUp => new[] { "stood up", "stood me up", "never showed", "didn't show", "never came", "waited" },
            Happenings.PlayerDate => new[] { "date", "dating" },
            Happenings.PlayerUpset => new[] { "rude", "upset", "annoyed", "argued" },
            Happenings.PlayerKind => new[] { "gave", "gift", "kind" },
            Happenings.PlayerFollowed => new[] { "following", "tagging along", "followed" },
            Happenings.WalkedOut => new[] { "walked out", "stormed out", "left without" },
            _ => Array.Empty<string>(),
        };
        return kind.Any(Word);
    }

    /// <summary>
    /// It was said (the teller spoke in the conversation): each listener who heard it keeps it as hearsay (trusting it as far
    /// as they trust the teller).
    /// </summary>
    public static int Heard(WitnessBook book, Plan plan, IEnumerable<Speaker> listeners, Func<string, string, PairBook.Pair?> pair, long now, Random rng)
    {
        int n = 0;
        foreach (var l in listeners)
        {
            if (l.Id == plan.TellerId) continue;
            float trust = (float)Math.Clamp(0.5 + (pair(l.Id, plan.TellerId)?.Affinity ?? 0) / 200.0, 0.2, 1.0);
            if (book.Hear(l.Id, l.Name, plan.Belief, plan.TellerId, plan.TellerName, plan.Stance, trust, now, rng) != null) n++;
        }
        return n;
    }
}
