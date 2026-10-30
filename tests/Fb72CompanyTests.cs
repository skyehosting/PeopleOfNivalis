using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NivalisAIChat.Phone;
using NivalisAIChat.Social;

namespace NivalisAIChat.Tests;

/// <summary>FB-72: residents come to keep you company when you ask (by text or on Nightline).</summary>
internal static partial class Program
{
    private static void Fb72CompanyTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- FB-72: come keep me company");
        Check(CompanyRules.NameRule("Raine", "").Contains("Their name is Raine") && CompanyRules.NameRule("", "night_owl").Contains("@night_owl")
              && CompanyRules.NameRule("", "").Contains("don't use one") && !CompanyRules.NameRule("", "").Contains("@"),
              "your name only if they know it; else your Nightline name if that's how; else none (never a made-up one)");
        var (msgs, _, _, coming) = Texting.Read("omw, give me ten\n[[coming]]", "Mara Quill", "");
        Check(coming && msgs.SequenceEqual(new[] { "omw, give me ten" }), "a text answer's [[coming]]: on their way, and never typed to you");
        var (_, _, _, notComing) = Texting.Read("can't tonight, sorry", "Mara Quill", "");
        var (_, _, _, emptyComing) = Texting.Read("[[coming]]", "Mara Quill", "");
        Check(!notComing && !emptyComing, "no [[coming]]: not coming; a bare [[coming]] with no words isn't either");

        var seen = new List<Lines.Seen>
        {
            new() { Key = "L1", Author = "you", MayReact = true, MayComment = true, MayCome = true },
            new() { Key = "L2", Author = "p_x", MayReact = true, MayComment = true, MayCome = false },
        };
        var r = Lines.Read("{\"handle\":\"\",\"post\":null,\"lines\":[{\"line\":\"L1\",\"react\":\"none\",\"comment\":\"on my way!\",\"reply_to\":\"\",\"why\":\"\",\"coming\":true}," +
                           "{\"line\":\"L2\",\"react\":\"like\",\"comment\":\"\",\"reply_to\":\"\",\"why\":\"\",\"coming\":true}]}", seen, false, new List<string>());
        Check(r.Acts.Single(a => a.Key == "L1").Coming && !r.Acts.Single(a => a.Key == "L2").Coming, "Nightline: coming only on your line that allowed it");
        Check(Lines.Schema().ToJsonString().Contains("\"coming\""), "...asked for in the schema");

        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string rt = File.ReadAllText(Path.Combine(src, "ModRuntime.Company.cs"));
        Check(rt.Contains("public const int MaxVisitors = 2;") && rt.Contains("Visitors.Count < MaxVisitors") && rt.Contains("other.ArriveAt + UnityEngine.Random.Range(20f, 45f)"),
              "never more than two, each on their own time");
        Check(rt.Contains("!KnownUnder18(id)") && rt.Contains("MinorInfo(v.C, v.Id).minor") && rt.Contains("rec.Visual.Minor"), "never anyone under 18");
        Check(rt.Contains("if (!CompanyOn || id == _avaId) return note;") && rt.Contains("they have to get to work") && rt.Contains("you walked off"),
              "not Ava (she's in your HUD); they leave for work, or when you walk off");
        string phone = File.ReadAllText(Path.Combine(src, "ModRuntime.Phone.cs"));
        Check(phone.Contains("note = CompanyNote(id, note);") && phone.Contains("QueueVisitor(id, who, \"text\")"), "by text: the reply already made decides it (no extra AI call)");
        string nl = File.ReadAllText(Path.Combine(src, "ModRuntime.Nightline.cs"));
        Check(nl.Contains("QueueVisitor(id, name, \"nightline\")") && nl.Contains("now - l.Minute <= 120 && rec.State.TimesMet > 0"), "on Nightline: your recent line, someone who's met you");
    }
}
