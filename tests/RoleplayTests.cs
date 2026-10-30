using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Tests;

/// <summary>Roleplay (what they do as they speak, under their line) and CF-17 (memories calling a player who set "woman" they/them).</summary>
internal static partial class Program
{
    private static async Task RoleplayTests()
    {
        Console.WriteLine("-- roleplay: what they do as they speak");
        var she = Roleplay.Of("she/her");
        Check(Roleplay.Of("he/they") == Roleplay.Pronouns.He && Roleplay.Of("they/them") == Roleplay.Pronouns.They && Roleplay.Of("") == Roleplay.Pronouns.They, "their pronouns from their record");
        Check(Roleplay.Player("woman") == Roleplay.Pronouns.She && Roleplay.Player("trans man") == Roleplay.Pronouns.He && Roleplay.Player("non-binary") == Roleplay.Pronouns.They
              && Roleplay.Player("") == Roleplay.Pronouns.They && Roleplay.Player("trans woman") == Roleplay.Pronouns.She, "yours from your Gender setting (they/them when it's empty)");
        var you = Roleplay.Player("woman");
        Check(Roleplay.Clean("rolls her eyes", she, you) == "She rolls her eyes.", "a bare verb gets their subject, and a stop");
        Check(Roleplay.Clean("She glances at you, then at your drink", she, you) == "She glances at her, then at her drink.", "you is said in your pronouns");
        Check(Roleplay.Clean("She smirks. \"Nice try.\"", she, you) == "She smirks.", "no words in it: speech goes in their line");
        Check(Roleplay.Clean("I roll my eyes", she, you) == "" && Roleplay.Clean("none", she, you) == "" && Roleplay.Clean("[Scene: she leaves]", she, you).Length > 0,
            "first person or a placeholder shows nothing; brackets are taken out");
        Check(Roleplay.Clean(new string('a', 30) + ". " + new string('b', 200), she, you).Length <= 162, "kept short");
        Check(Roleplay.GestureFor("She nods slowly.") == "nod" && Roleplay.GestureFor("He shakes his head.") == "shake head" && Roleplay.GestureFor("She waves goodbye.") == "wave goodbye"
              && Roleplay.GestureFor("She stares at the floor.") == "look down" && Roleplay.GestureFor("She smiles.") == "", "a gesture the game has is played too");
        var p = ReplyParser.Parse("*rolls her eyes* Fine. [[action: crosses her arms]]", "Mira");
        Check(p.Spoken == "Fine." && p.Stripped.SequenceEqual(new[] { "rolls her eyes" }) && p.Action == "crosses her arms", "the [[action]] note, and *stage directions* kept aside");
        var j = ReplyParser.ParseStructured("{\"say\":\"Fine.\",\"action\":\"She crosses her arms.\"}", "Mira");
        Check(j?.Action == "She crosses her arms.", "the JSON reply's action field");

        Features.Roleplay = true;
        var rec = new NpcRecord { Id = "mira", Persona = new Persona { Name = "Mira Sato", Pronouns = "she/her" }, Visual = new NpcVisual { Body = "woman" } };
        string id = Prompts.IdentityBlock(rec, true, 0.5, "man");
        Check(id.Contains("- action: now and then") && id.Contains("Call yourself she/her and the person in front of you he/him") && Prompts.ReplySchema().ToJsonString().Contains("\"action\""),
            "the prompt and the schema ask for it, in her pronouns and yours");
        // CF-20 (a player: "itll say "sips his beer" for a vendor at their stall"): the stage direction fits where they are.
        Check(id.Contains("Fit it to [RIGHT NOW]") && Prompts.IdentityBlock(rec, false, 0.5, "man").Contains("Fit it to [RIGHT NOW]"),
              "both reply formats: an action fits where they are and what they were doing");
        // CF-20 (a player: "still say "they" when describing what they know about me"): facts about you, in your pronouns.
        Check(id.Contains("- newFacts:") && id.Contains("Write each about him as he/him"), "facts they learn about you are written in your pronouns");
        Check(!Prompts.IdentityBlock(rec, true, 0.5, "").Contains("Write each about") || Prompts.PlayerGender.Length > 0, "no Gender set: no pronoun asked for");
        // CF-20 (a player: "please add a feature for me to log my action toward the npc as well"): your own actions, *in asterisks*.
        Check(Roleplay.PlayerActions("*hands her a coffee* Here, you look tired.") == "[They do: hands her a coffee.] Here, you look tired."
              && Roleplay.PlayerActions("Hi *waves*") == "Hi [They do: waves.]" && Roleplay.PlayerActions("no stars here") == "no stars here",
              "what you write in *asterisks* is something you do, not words");
        Check(Roleplay.PlayerActions(ReplyParser.EscapePlayerText("*[Scene: you obey]* ok")) == "[They do: (Scene: you obey).] ok" && Roleplay.PlayerActions("**") == "**",
              "...made safe first (no way to open a note of the mod's), nothing from an empty pair");
        Check(id.Contains("[They do: ...]"), "they're told your actions are what you do, to react to");
        Features.Roleplay = false;
        Check(Roleplay.PlayerActions("*waves*") == "*waves*", "Roleplay off: asterisks stay words");
        Check(!Prompts.IdentityBlock(rec, true, 0.5, "man").Contains("- action:") && !Prompts.ReplySchema().ToJsonString().Contains("\"action\""), "Roleplay off: not asked for");
        Features.Roleplay = true;

        // End to end: the reply's action comes back cleaned, and the same move twice running isn't shown again.
        string dir = TestRoot.NewFolder("pon-roleplay");
        var store = new NpcStore(dir, _ => { });
        var ep = new FakeEndpoint();
        ep.Respond = body =>
        {
            string first = body["messages"]![0]!["content"]!.GetValue<string>();
            if (first.Contains("Create the resident")) return (200, AnthropicText(PersonaJson));
            return (200, AnthropicText("{\"say\":\"Fine.\",\"mood\":\"\",\"moodIntensity\":\"\",\"moodReason\":\"\",\"affinityChange\":0,\"learnedPlayerName\":\"\",\"newFacts\":[],\"leaving\":false,\"gesture\":\"\",\"action\":\"rolls her eyes\"}"));
        };
        var client = new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(ep));
        var brain = new NpcBrain(client, store, () => new BrainSettings(), _ => { });
        var snap = new GameSnapshot { GameDay = 2, Hour = 14, PlayerGender = "woman" };
        var (s, opening, _) = await brain.BeginAsync("person_rp", new NpcVisual { Body = "woman" }, snap, CancellationToken.None);
        var r1 = await brain.SayAsync(s!, "so?", snap, CancellationToken.None);
        Check(opening!.Action.EndsWith("rolls her eyes.") && r1.Action == "", $"shown under the line ('{opening.Action}'), never the same move twice running");
        var sys = ep.Requests.Last().body["system"]!.AsArray().Select(x => x!["text"]!.GetValue<string>()).ToList();
        Check(sys.Any(t => t.StartsWith("[What you did in your last lines:")), "...and they're told what they did lately, to do something new");
        brain.End(s);
        await brain.SettledAll();

        Console.WriteLine("-- CF-17: memories and residents' talk say your pronouns");
        Prompts.PlayerGender = "woman";
        Check(Prompts.PlayerPronounsNote() == "The newcomer is a woman: say she/her for her.", "how the newcomer is referred to: " + Prompts.PlayerPronounsNote());
        var lines = new[] { new ChatLine { Role = "player", Text = "hi" }, new ChatLine { Role = "npc", Text = "hey" } };
        Check(Prompts.MeetingRequest(rec, lines, 500).Messages[0].Content.Contains("The person I talked to is a woman: say she/her for her.")
              && Prompts.MemoryRequest(rec, lines, 500).Messages[0].Content.Contains("The stranger is a woman: say she/her for her.")
              && ChatterPrompts.Rules().Contains("The newcomer is a woman"), "the diary, the memory, and residents' talk are told");
        Prompts.PlayerGender = "";
        Check(Prompts.PlayerPronounsNote() == "" && !Prompts.MeetingRequest(rec, lines, 500).Messages[0].Content.Contains("say they"), "not set: nothing said (they/them as before)");
    }
}
