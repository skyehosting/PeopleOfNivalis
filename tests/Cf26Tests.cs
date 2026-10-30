using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace NivalisAIChat.Tests;

/// <summary>CF-26: waiters stopped taking orders with ReplaceGameChatter on (a player, Nexus, 0.10.2).</summary>
internal static partial class Program
{
    private static void Cf26Tests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- CF-26: the game's groups with staff or customers stay the game's");
        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string Read(string f) => File.ReadAllText(Path.Combine(src, f));

        string mute = Read(Path.Combine("Game", "GameChatter.cs"));
        int prefix = mute.IndexOf("private static bool Prefix(CharacterGroup __instance)", StringComparison.Ordinal);
        int letThrough = mute.IndexOf("if (who.Length > 0)", prefix, StringComparison.Ordinal);
        int silenced = mute.IndexOf("return false;", prefix, StringComparison.Ordinal);
        Check(prefix > 0 && letThrough > prefix && silenced > letThrough && mute.Contains("try { who = ServiceMemberCached(__instance); } catch { return true; }"),
              "CF-26: a game group with someone at work or on a visit in it keeps its own lines (only pure background chatter is silenced; unreadable = the game's)");
        Check(mute.Contains("var list = g.Characters;") && mute.Contains("list[i].TryGetExistingValue(out Character c)") && mute.Contains("seen.count == count")
              && mute.Contains("if (!Muted) { if (Checked.Count > 0) Checked.Clear(); return true; }") && mute.Contains("GameContext.ServiceWhy(c)") && mute.Contains("catch { return \"a member who can't be read\"; }"),
              "...every member of the group is looked at through the game's checked read (review CF26-1), read again when someone joins or leaves (CF26-2), and a group that can't be read counts as busy");

        string ctx = Read(Path.Combine("Game", "GameContext.cs"));
        int why = ctx.IndexOf("public static string ServiceWhy(Character c)", StringComparison.Ordinal);
        string body = why > 0 ? ctx.Substring(why, ctx.IndexOf("public static bool IsWorkerTask", why, StringComparison.Ordinal) - why) : "";
        Check(body.Contains("IsWorkerTask(c) || AtWork(c)") && body.Contains("OnShift(c)") && body.Contains("PlayerHired") && body.Contains("VisitOf(c).step")
              && body.Contains("c.Agent.VisitingLocale") && body.Contains("catch { return \"can't be read\"; }"),
              "...'in service' is at work, on shift, your staff, a customer's step, or visiting a place");

        string chatter = Read("ModRuntime.Chatter.cs");
        int gathered = chatter.IndexOf("private static bool TryGatheredGroup", StringComparison.Ordinal);
        int check = chatter.IndexOf("busy = GameChatter.ServiceMember(gg)", gathered, StringComparison.Ordinal);
        int start = chatter.IndexOf("if (StartGroup(s, grp[0], grp[1]", gathered, StringComparison.Ordinal);
        Check(gathered > 0 && check > gathered && start > check,
              "...and this mod never takes over such a group: the whole group the game gathered is checked, not only who's free here");
        Check(chatter.Contains("if (!table) // CF-26") && chatter.Contains("foreach (var m in g.Members) if (m.C != null) PulledIn[m.C.Pointer] = t0;")
              && chatter.Contains("if (PulledIn.Count > 200)"),
              "...who a residents' conversation took in is remembered (a table's own talk isn't: FB-34 as before)");

        string venues = Read("ModRuntime.Venues.cs");
        int sits = venues.IndexOf("bool sits = ", StringComparison.Ordinal);
        int left = venues.IndexOf("their order is left to the game\");", sits, StringComparison.Ordinal);
        int mark = venues.IndexOf("Sitters[(cid, customer.Pointer)] = now;", sits, StringComparison.Ordinal);
        Check(sits > 0 && left > sits && mark > left && venues.Substring(left, mark - left).Contains("return false;")
              && venues.Contains("PulledIn.Where(kv => now - kv.Value > PulledInFor)"),
              "...and venue talk never calls them 'just sitting' (nor cancels their order) for 10 minutes after: the game sorts it out");
    }
}
