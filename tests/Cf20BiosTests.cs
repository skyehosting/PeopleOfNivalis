using System;
using System.Linq;
using NivalisAIChat.Ai;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Tests;

/// <summary>
/// CF-20 (a player: "I can convince someone they work somewhere else, have a different romance partner, are a different
/// orientation ... I literally said "I thought you and your partner broke up" and it changed his relationship status"; the
/// author: "Firm, only real events"): what their profile says can't be rewritten by what you tell them.
/// </summary>
internal static partial class Program
{
    private static void Cf20BiosTests()
    {
        Console.WriteLine("-- CF-20: firm profiles (only real events change them)");
        var rec = new NpcRecord { Id = "p_vend", Persona = new Persona { Name = "Gus Varro", Occupation = "butcher", Workplace = "the Meridian Market" } };
        NpcBrain.ApplyTold(rec, new[] { ("job", "butcher at the market") }, 1, afterMeeting: false);
        NpcBrain.ApplyTold(rec, new[] { ("job", "The meeting is at my shop") }, 2, afterMeeting: false);
        Check(rec.State.Told.Count(t => t.Topic == "job") == 1 && rec.State.Told.First(t => t.Topic == "job").Said == "butcher at the market",
              "a later, different job answer doesn't replace the first (what they said stands)");
        Check(rec.Persona.EstablishedFacts.Contains("job: butcher at the market") && !rec.Persona.EstablishedFacts.Any(f => f.Contains("meeting")),
              "...nor what they stay consistent with");
        NpcBrain.ApplyTold(rec, new[] { ("relationship", "with Mara, three years") }, 2, afterMeeting: false);
        NpcBrain.ApplyTold(rec, new[] { ("relationship", "broke up with Mara") }, 3, afterMeeting: false);
        Check(rec.State.Told.Single(t => t.Topic == "relationship").Said == "with Mara, three years", "talked into a break-up: not true just because you said it");
        NpcBrain.ApplyTold(rec, new[] { ("orientation", "straight") }, 3, afterMeeting: false);
        NpcBrain.ApplyTold(rec, new[] { ("orientation", "gay") }, 4, afterMeeting: false);
        Check(rec.State.Told.Single(t => t.Topic == "orientation").Said == "straight", "nor a different orientation");
        NpcBrain.ApplyTold(rec, new[] { ("hobbies", "pigeon racing") }, 4, afterMeeting: false);
        NpcBrain.ApplyTold(rec, new[] { ("hobbies", "old radios") }, 5, afterMeeting: false);
        Check(rec.State.Told.Count(t => t.Topic == "hobbies") == 2, "new details still add (topics that build up)");
        NpcBrain.ApplyTold(rec, new[] { ("job", "butcher at the market, mornings") }, 5, afterMeeting: false);
        Check(rec.State.Told.Count(t => t.Topic == "job") == 1, "the same answer again isn't a second one");

        // Real events: a romance with you, someone trans or non-binary telling you who they are.
        rec.State.Romance = Romance.Dating; rec.State.RomanceSinceDay = 6;
        NpcBrain.ApplyTold(rec, new[] { ("relationship", "seeing the newcomer") }, 7, afterMeeting: false);
        Check(rec.State.Told.Single(t => t.Topic == "relationship").Said == "seeing the newcomer", "a romance with you since: their relationship answer moves with it");
        // Review R3: a break-up is real too: after it, their answer can move again.
        rec.State.Romance = ""; rec.State.RomanceSinceDay = -1; rec.State.RomanceEndedDay = 9;
        NpcBrain.ApplyTold(rec, new[] { ("relationship", "single again") }, 10, afterMeeting: false);
        Check(rec.State.Told.Single(t => t.Topic == "relationship").Said == "single again", "your romance ended: their relationship answer moves with that");
        NpcBrain.ApplyTold(rec, new[] { ("relationship", "back with Mara") }, 11, afterMeeting: false);
        Check(rec.State.Told.Single(t => t.Topic == "relationship").Said == "single again", "...and is firm again after");
        var trans = new NpcRecord { Id = "p_t", Persona = new Persona { Name = "Lee", GenderIdentity = "trans woman" } };
        NpcBrain.ApplyTold(trans, new[] { ("gender", "woman") }, 1, afterMeeting: false);
        NpcBrain.ApplyTold(trans, new[] { ("gender", "I'm trans, actually") }, 2, afterMeeting: false);
        Check(trans.State.Told.Single(t => t.Topic == "gender").Said == "I'm trans, actually" && trans.State.IdentityShared, "telling you they're trans: their own reveal, kept");

        // And they're told so: your claims about their life are only claims.
        string id = Prompts.IdentityBlock(rec, true);
        Check(id.Contains("What the stranger claims about it") && Prompts.IdentityBlock(rec, false).Contains("What the stranger claims about it"),
              "the prompt: their life changes only when something really happens to them");
    }
}
