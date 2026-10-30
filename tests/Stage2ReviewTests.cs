using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using NivalisAIChat.Ai;
using NivalisAIChat.Api;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Tests;

/// <summary>The stage 2 review's fixes (2026-10-18): the plugin API's isolation, the key hidden in any form, the snapshot's checks, written hours.</summary>
internal static partial class Program
{
    private static void Stage2ReviewTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- the stage 2 review's fixes");

        // F-TEST-1 / F-PON-1: a throwing handler first doesn't stop the next; three in a row switch its plugin off.
        PonApi.ResetForTests();
        var warned = new List<string>();
        PonApi.LogWarn = warned.Add;
        try
        {
            int heard = 0;
            Action<NpcInfo> boom = _ => throw new InvalidOperationException("boom");
            Action<NpcInfo> count = _ => heard++;
            PonApi.ChatOpened += boom;
            PonApi.ChatOpened += count;
            PonApi.RaiseChatOpened(new NpcInfo());
            Check(heard == 1, "a handler that throws first doesn't stop the one after it");
            string switchedOff = "";
            PonApi.SwitchedOff = id => switchedOff = id;
            PonApi.OwnAssembly(typeof(Program).Assembly, "tester");
            for (int i = 0; i < 3; i++) PonApi.RaiseChatOpened(new NpcInfo());
            Check(PonApi.IsOff("tester") && switchedOff == "tester", "a plugin whose event handler fails three times in a row is switched off (and told: Unload)");
            int before = heard;
            PonApi.RaiseChatOpened(new NpcInfo());
            Check(heard == before, "...and its handlers come off the events, even ones added without On()");

            // Consecutive, not cumulative.
            PonApi.ResetForTests();
            int calls = 0;
            PonApi.RegisterContext("sometimes", _ => ++calls % 2 == 0 ? throw new Exception("odd") : "fine");
            for (int i = 0; i < 8; i++) PonApi.ContextFor(new ContextRequest());
            Check(!PonApi.IsOff("sometimes"), "an error now and then (with calls that work between) doesn't switch a plugin off");
            Check(!PonApi.RegisterReplyTag("x", "wants_food", "", (_, _) => { }) && !PonApi.RegisterReplyTag("x", "self", "", (_, _) => { }),
                "every tag the mod reads itself is reserved (F-PON-2)");
            PonApi.RegisterReplyTag("x", "wink", "when [YOU] wink.", (_, _) => { });
            Check(!PonApi.TagInstructions().Contains("[YOU]"), "a tag's instruction can't make a [section] (F-PON-3)");
        }
        finally { PonApi.ResetForTests(); PonApi.LogWarn = _ => { }; PonApi.SwitchedOff = null; }

        // F-STT-1: a key that doesn't look like one is still never in an error.
        var echo = new FakeSpeech { Status = HttpStatusCode.BadRequest, Answer = "{\"error\":{\"message\":\"bad token my-local-token-here\"}}" };
        string err = "";
        try { new SpeechToText(() => new SpeechSettings { Url = "http://127.0.0.1:8080/v1", Key = "my-local-token-here" }, new HttpMessageInvoker(echo)).TranscribeAsync(new short[1600], 16000, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (SpeechException e) { err = e.Message; }
        Check(err.Length > 0 && !err.Contains("my-local-token-here"), "a speech service echoing any key back: it's hidden from the error");

        // F-SNAP-1: a short hash, or an NTFS stream in a path, changes nothing (and doesn't throw).
        string dir = Path.Combine(Path.GetTempPath(), "pon-s2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "npcs"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "npcs", "a.json"), "{}");
            var s = new SaveSnapshots(dir, _ => { });
            var m = s.Take("slot", "fp", "0.6.0")!;
            var shortHash = new SaveSnapshots.Manifest { Slot = "x", Files = new Dictionary<string, string> { ["npcs/a.json"] = "abc" } };
            var stream = new SaveSnapshots.Manifest { Slot = "y", Files = new Dictionary<string, string> { ["npcs/a.json:evil"] = m.Files.Values.First() } };
            bool threw = false, r1 = true, r2 = true;
            try { r1 = s.Restore(shortHash); r2 = s.Restore(stream); } catch { threw = true; }
            Check(!threw && !r1 && !r2 && File.ReadAllText(Path.Combine(dir, "npcs", "a.json")) == "{}", "a damaged manifest (a short hash, a ':' in a path) is refused, nothing changed");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        // F-PARSE-1: a written "12am" is midnight; a bare 0 is still no hour.
        var twelve = ReplyParser.ParseStructured("{\"say\":\"Ok.\",\"reminder\":{\"what\":\"the late show\",\"inDays\":0,\"atHour\":\"12am\",\"place\":\"\"}}", "Mae", "");
        var noon = ReplyParser.ParseStructured("{\"say\":\"Ok.\",\"reminder\":{\"what\":\"lunch\",\"inDays\":0,\"atHour\":\"noon\",\"place\":\"\"}}", "Mae", "");
        Check(twelve!.Extras.ReminderAtHour == 0 && noon!.Extras.ReminderAtHour == 12, "a written 12am is midnight, noon is 12 (F-PARSE-1)");

        // The game-side fixes (read from the source: they need the game to run).
        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string hold = File.ReadAllText(Path.Combine(src, "Game", "NpcHold.cs"));
        string follow = File.ReadAllText(Path.Combine(src, "ModRuntime.Follow.cs"));
        string romance = File.ReadAllText(Path.Combine(src, "ModRuntime.Romance.cs"));
        Check(hold.Contains("if (h == _chat) { h.KeepForChat = false; return; }"), "F1: someone seated, kept for a chat, is let go by the chat when their follow or visit ends");
        Check(follow.Contains("if (_date?.Id != f.Id || !Features.Romance)") && follow.Contains("if (_date == null) FreeDateSeat(f);") && follow.Contains("f.StandThenFollow = true;"),
            "F2: a seated follower whose date ended gets up (and the chair is given back)");
        Check(romance.Contains("if (Followers.Any(f => Same(f.C, c)) || WouldFollowForDate(c)) StartDate(c, rec, day);"), "F5: no date used up when they can't come along");
    }
}
