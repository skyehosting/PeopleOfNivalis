using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Tests;

/// <summary>
/// FB-66: a group conversation you join (Npc/GroupChat.cs): what the request tells the AI (everyone, how they know you,
/// the conversation so far, the 0-3 line rule), the answer read (unknown speakers dropped, at most three lines, who it's
/// to, how each was moved), leavers gone, and what each member keeps in their own record.
/// </summary>
internal static partial class Program
{
    private static void GroupChatTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- FB-66: group conversations");
        NpcRecord P(string id, string name, int met = 0)
        {
            var r = new NpcRecord { Id = id };
            r.Persona.Name = name;
            r.Persona.Age = "30";
            r.State.TimesMet = met;
            r.State.Relationship = Relationships.Neutral;
            return r;
        }
        var g = new GroupChat { Where = "Meridian Market", Day = 12, Clock = "18:40" };
        g.Members.Add(new GroupChat.Member { Record = P("a", "Ana Ruiz", met: 2), Label = "Ana" });
        g.Members.Add(new GroupChat.Member { Record = P("b", "Bo Lind"), Label = "Stranger (Man)" });
        g.Members[0].Record.State.PlayerName = "Sam";

        var req = g.Request("Hey Ana, who's your friend?", (_, _) => null, 2000, "French");
        string all = string.Join("\n", req.System) + "\n" + string.Join("\n", req.Messages.Select(m => m.Content));
        Check(all.Contains("Ana Ruiz") && all.Contains("Bo Lind") && all.Contains("knows their name is Sam") && all.Contains("has never talked with them"),
            "the request: everyone in it, and how each knows you");
        Check(all.Contains("0 to 3 lines") && all.Contains("the others chime in only when it's natural") && all.Contains("Nobody speaks for the newcomer"),
            "...the rule: whoever was spoken to answers, others only when natural");
        Check(all.Contains("The newcomer says: \"Hey Ana, who's your friend?\"") && all.Contains("Everyone speaks French"), "...your words, and the reply language");
        Check(req.JsonSchema!.ToJsonString().Contains("\"feel\"") && req.JsonSchema.ToJsonString().Contains("\"leaving\""), "...and the answer's shape");

        var reply = g.Parse("{\"lines\":[{\"speaker\":\"Ana\",\"to\":\"newcomer\",\"say\":\"This is Bo, from work.\",\"tone\":\"cheerful\",\"gesture\":\"\",\"leaving\":false}," +
                            "{\"speaker\":\"Zed\",\"to\":\"\",\"say\":\"Who am I?\",\"tone\":\"\",\"gesture\":\"\",\"leaving\":false}," +
                            "{\"speaker\":\"Bo Lind\",\"to\":\"Ana\",\"say\":\"From work, she says. I'm off, see you.\",\"tone\":\"wry\",\"gesture\":\"wave\",\"leaving\":true}," +
                            "{\"speaker\":\"Ana\",\"to\":\"\",\"say\":\"Bye!\",\"tone\":\"\",\"gesture\":\"\",\"leaving\":false}," +
                            "{\"speaker\":\"Ana\",\"to\":\"\",\"say\":\"One too many.\",\"tone\":\"\",\"gesture\":\"\",\"leaving\":false}]," +
                            "\"feel\":[{\"name\":\"Ana\",\"change\":3},{\"name\":\"Bo\",\"change\":-40}]}");
        Check(reply.Ok && reply.Lines.Count == 3 && reply.Lines[0].SpeakerId == "a" && reply.Lines[0].ToId == GroupChat.PlayerId
              && reply.Lines[1].SpeakerId == "b" && reply.Lines[1].ToId == "a" && reply.Lines[1].Leaving,
            "the answer: lines from people in it only (by full or first name), at most three, each with who it's to");
        Check(reply.Feel["a"] == 3 && reply.Feel["b"] == -10, "...how each was moved, kept to -10..10");
        g.Heard("Hey Ana, who's your friend?", reply, reply.Lines.Count);
        Check(g.Members[1].Gone && g.Present.Count() == 1 && g.Transcript.Count == 4, "someone who leaves is gone from it; what was said is the conversation so far");
        Check(!g.Parse("not json").Ok && g.Parse("{\"lines\":[]}").Lines.Count == 0, "an unreadable answer fails; nobody answering is fine");
        var later = g.Request("And you, Ana?", (_, _) => null, 2000);
        string so = string.Join("\n", later.Messages.Select(m => m.Content));
        Check(so.Contains("[THE CONVERSATION SO FAR]") && so.Contains("The newcomer: Hey Ana") && so.Contains("Bo Lind: From work") && !string.Join("\n", later.System).Contains("Bo Lind:"),
            "the next request carries the conversation so far (and only who's still there)");

        var ana = P("a", "Ana Ruiz", met: 2);
        double before = ana.State.Relationship;
        Check(g.Remember(ana, rating => rating * 0.5, DateTime.UtcNow), "each member keeps it");
        Check(ana.History.Any(h => h.Role == "player" && h.Text.StartsWith("Hey Ana")) && ana.History.Any(h => h.Role == "npc" && h.Text == "This is Bo, from work.")
              && ana.History.Any(h => h.Role == "scene" && h.Text.StartsWith("Bo: From work")) && ana.History.Any(h => h.Role == "scene" && h.Text.Contains("that the stranger joined")),
            "...in their history: your lines as yours, theirs as theirs, the others' as what was said around them");
        Check(ana.Meetings.Count == 1 && ana.Meetings[0].Summary.Contains("Bo") && ana.State.TimesMet == 3 && ana.State.Relationship > before,
            "...a meeting, one more time met, and the bar moved by how it went");
        var silent = new GroupChat();
        silent.Members.Add(new GroupChat.Member { Record = P("c", "Cy") });
        Check(!silent.Remember(P("c", "Cy"), r => r, DateTime.UtcNow), "nothing kept when you never said anything");

        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string rt = File.ReadAllText(Path.Combine(src, "ModRuntime.GroupChat.cs"));
        string chat = File.ReadAllText(Path.Combine(src, "ModRuntime.Chat.cs"));
        Check(rt.Contains(".Where(m => !MinorInfo(m.C, m.Id).minor)") && rt.Contains("_brain.IsKnownMinor(rec)") && rt.Contains("!brain.IsKnownMinor(r)"),
            "never anyone under 18 in a group conversation, or in what's kept of it");
        Check(chat.Contains("if (TryOpenGroupChat(c, cam)) return;") && chat.Contains("try { EndGroupChat(); }") && rt.Contains("_cfg.GroupVoicesOverlap.Value"),
            "talking to someone in a group opens it for all; closing it lets everyone go; voices one after another unless GroupVoicesOverlap");
    }
}
