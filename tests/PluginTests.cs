using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NivalisAIChat.Ai;
using NivalisAIChat.Api;

namespace NivalisAIChat.Tests;

/// <summary>
/// FB-67: the plugin API (Api/PonApi.cs): context lines (cut, capped), reply tags (reserved names refused, the AI told
/// when, the value handed over from either way of answering), line filters, events, and a plugin that keeps failing
/// switched off; the loader skipping what can't load.
/// </summary>
internal static partial class Program
{
    private static void PluginTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- FB-67: plugins");
        PonApi.ResetForTests();
        var warned = new List<string>();
        PonApi.LogWarn = warned.Add;
        try
        {
            var npc = new NpcInfo { Id = "person_x", Name = "Mae", Relationship = 61 };
            PonApi.RegisterContext("weather", r => $"A storm is coming over {r.Location}.");
            PonApi.RegisterContext("long", _ => new string('x', 900));
            PonApi.RegisterContext("quiet", _ => null);
            var lines = PonApi.ContextFor(new ContextRequest { Npc = npc, Location = "the docks" });
            Check(lines.Count == 2 && lines[0] == "A storm is coming over the docks." && lines[1].Length <= PonApi.MaxContextChars + 1,
                "context lines from plugins, cut to size; nothing from one with nothing to say");

            Check(!PonApi.RegisterReplyTag("bad", "mood", "never", (_, _) => { }) && !PonApi.RegisterReplyTag("bad", "x", "short", (_, _) => { })
                  && !PonApi.RegisterReplyTag("bad", "has space", "", (_, _) => { }) && !PonApi.RegisterReplyTag("bad", "digits1", "", (_, _) => { }),
                "the mod's own tags and odd names can't be registered");
            string got = "";
            Check(PonApi.RegisterReplyTag("gifts", "gift", "when you give them something (what you give).", (n, v) => got = n.Name + ":" + v)
                  && !PonApi.RegisterReplyTag("other", "gift", "", (_, _) => { }), "a reply tag, one owner each");
            Check(PonApi.TagInstructions().Contains("[[gift: value]] when you give them something") && PonApi.HasTags, "the AI is told when to use it");
            Check(Prompts.ReplySchema().ToJsonString().Contains("\"notes\""), "...and the JSON answer gets room for it");
            var tagged = ReplyParser.Parse("Here, take this. [[gift: a red scarf]]", "Mae", "");
            Check(tagged.Spoken == "Here, take this." && tagged.Extras.Other["gift"] == "a red scarf", "a hidden note for a plugin is read, never spoken");
            var json = ReplyParser.ParseStructured("{\"say\":\"Here.\",\"notes\":[{\"tag\":\"gift\",\"value\":\"a flower\"}]}", "Mae", "");
            Check(json!.Extras.Other["gift"] == "a flower", "...the same from a JSON answer");
            PonApi.HandleTags(npc, tagged.Extras.Other);
            Check(got == "Mae:a red scarf", "...and handed to its handler");

            PonApi.RegisterLineFilter("shout", (_, l) => l.ToUpperInvariant());
            PonApi.RegisterLineFilter("keep", (_, _) => "");
            Check(PonApi.FilterLine(npc, "hello") == "HELLO", "line filters change a line in order (an empty answer keeps it)");

            int opened = 0;
            Action<NpcInfo> h = _ => opened++;
            PonApi.On("counter", () => PonApi.ChatOpened += h, () => PonApi.ChatOpened -= h);
            Action<NpcInfo> boom = _ => throw new InvalidOperationException("nope");
            PonApi.ChatOpened += boom;
            PonApi.RaiseChatOpened(npc);
            Check(opened == 1 && warned.Any(w => w.Contains("nope")), "events reach every handler; one that throws doesn't stop the rest");
            PonApi.ChatOpened -= boom;
            PonApi.Forget("counter");
            PonApi.RaiseChatOpened(npc);
            Check(opened == 1, "a plugin let go is unsubscribed");

            PonApi.RegisterContext("flaky", _ => throw new Exception("broken"));
            for (int i = 0; i < 4; i++) PonApi.ContextFor(new ContextRequest { Npc = npc });
            Check(PonApi.IsOff("flaky") && warned.Any(w => w.Contains("switched off")), "a plugin that fails three times is switched off");

            string dir = Path.Combine(Path.GetTempPath(), "pon-plugins-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "junk.dll"), "not a dll");
                File.WriteAllText(Path.Combine(dir, "skipped.dll"), "not a dll either");
                var infos = new List<string>();
                var loader = new PluginLoader(infos.Add, warned.Add);
                Check(loader.LoadAll(dir, new[] { "skipped" }) == 0 && warned.Any(w => w.Contains("junk.dll couldn't be loaded")) && infos.Any(i => i.Contains("skipped is switched off")),
                    "the loader skips a DLL that won't load, and leaves out DisabledPlugins");
                Check(new PluginLoader(_ => { }, _ => { }).LoadAll(Path.Combine(dir, "made"), Array.Empty<string>()) == 0 && Directory.Exists(Path.Combine(dir, "made")),
                    "no folder yet: it's made, nothing loads");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
        finally { PonApi.ResetForTests(); PonApi.LogWarn = _ => { }; }
        Check(!Prompts.ReplySchema().ToJsonString().Contains("\"notes\""), "no plugin tags: the JSON answer is as before");

        string root = Path.GetDirectoryName(Path.GetDirectoryName(here))!;
        Check(File.ReadAllText(Path.Combine(root, "release", "obfuscar.xml")).Contains("<SkipNamespace name=\"NivalisAIChat.Api\" />"),
            "the public build keeps the API's names (plugins compile against them)");
        Check(File.Exists(Path.Combine(root, "docs", "PLUGINS.md")) && File.ReadAllText(Path.Combine(root, "docs", "PLUGINS.md")).Contains("IPeopleOfNivalisPlugin"),
            "docs/PLUGINS.md explains it");
        string plugins = File.ReadAllText(Path.Combine(root, "src", "ModRuntime.Plugins.cs"));
        Check(!plugins.Contains("ApiKey") && plugins.Contains("never a key"), "nothing a plugin gets carries a key");
    }
}
