using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;

namespace NivalisAIChat.Tests;

/// <summary>
/// Step 4a of the audit (B7: ROBUST-1, ROBUST-2): AI answers in the wrong shape (a number where text goes, text where a
/// list goes, nulls, a plain string inside a list) never refuse or break a person. Written against the public API only,
/// so the same checks run on the code before the change (where they fail) and after it.
/// </summary>
internal static partial class Program
{
    private static T? OrNull<T>(Func<T?> read) where T : class
    {
        try { return read(); } catch { return null; }
    }

    private static bool Fails(Action act)
    {
        try { act(); return false; } catch { return true; }
    }

    private static async Task JsonShapesTests(string dir)
    {
        Console.WriteLine("-- audit 2026-10-11: AI answers in the wrong shape (step 4a)");
        var snap = new GameSnapshot { GameDay = 5, Hour = 12 };

        // ROBUST-1a: a number where text goes, a list written as text, "yes" for a yes/no, text where their speech style goes.
        var odd = OrNull(() => NpcBrain.ParsePersona("{\"name\":\"Oda Venn\",\"age\":34,\"personality\":\"curious, blunt; dry\",\"speech\":\"casual\",\"pronounsFlexible\":\"yes\"}"));
        Check(odd != null && odd.Age == "34" && odd.Personality.SequenceEqual(new[] { "curious", "blunt", "dry" }) && odd.Speech != null && odd.PronounsFlexible,
              "a persona with a number for an age, a list written as text, text for a speech style and \"yes\" for a yes/no is read, not refused (ROBUST-1a)");
        var speech = OrNull(() => NpcBrain.ParsePersona("{\"name\":\"Oda Venn\",\"age\":\"34\",\"speech\":{\"slangLevel\":null,\"formality\":2,\"quirks\":null,\"catchphrases\":\"right, so\"}}"));
        Check(speech != null && speech.Speech.SlangLevel == 0 && speech.Speech.Formality == "2" && speech.Speech.Quirks.Count == 0
              && speech.Speech.Catchphrases.SequenceEqual(new[] { "right", "so" }),
              "inside a speech style: a null number is 0, a number for text is its text, a null list is empty, a list written as text is split");
        var whole = OrNull(() => NpcBrain.ParsePersona("{\"name\":\"Oda Venn\",\"speech\":{\"slangLevel\":2.0}}"));
        var words = OrNull(() => NpcBrain.ParsePersona("{\"name\":\"Oda Venn\",\"speech\":{\"slangLevel\":\"two\"}}"));
        Check(whole?.Speech.SlangLevel == 2 && words?.Speech.SlangLevel == 0, "a number written as 2.0 is 2; one written as a word is the default");

        // ROBUST-1b: nulls never reach their record, and their prompts can always be built.
        var nulls = OrNull(() => NpcBrain.ParsePersona("{\"name\":\"Oda Venn\",\"age\":\"34\",\"nickname\":null,\"personality\":null,\"speech\":null,\"people\":[\"Teo — brother\",null,7,{\"x\":1}]}"));
        bool prompts = nulls != null
                       && !Fails(() => Prompts.IdentityBlock(new NpcRecord { Id = "p_n", Persona = nulls, Visual = new NpcVisual { Body = "woman" } }))
                       && !Fails(() => Moods.Roll(new Random(1), nulls, 12, "clear", 0, false));
        Check(nulls != null && nulls.Nickname == "" && nulls.Personality.Count == 0 && nulls.Speech != null
              && nulls.People.SequenceEqual(new[] { "Teo — brother", "7" }) && prompts,
              "nulls in a persona become empty, never null, and their prompts and mood can be worked out (ROBUST-1b)");

        // Under 18 still wins (Q4): an age or a look written as an object is read as its text, so someone written as 16
        // (no picture) is asked for once more, then refused, never created with an adult's dice age.
        {
            var young = new ScriptedAi
            {
                PersonaOverride = "{\"name\":\"Kit Moss\",\"age\":{\"value\":16},\"appearance\":{\"look\":\"a lanky kid with a school bag\"},\"occupation\":\"rig worker\"}",
            };
            var youngClient = new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(young));
            var youngStore = new NpcStore(Path.Combine(dir, "shapes-young"), _ => { });
            var youngBrain = new NpcBrain(youngClient, youngStore, () => new BrainSettings { LifeUpdates = false, MeetingSummaries = false }, _ => { });
            var (kid, _, kidErr) = await youngBrain.BeginAsync("p_kit", new NpcVisual { Body = "man" }, snap, System.Threading.CancellationToken.None);
            int asked = young.Kinds.Count(k => k == "persona");
            Check(kid == null && youngStore.Load("p_kit") == null && asked == 2 && (kidErr ?? "").Contains("under 18"),
                  $"an age written as {{\"value\": 16}} is still read as 16: asked for again, then refused, never created as an adult ({asked} asks: {kidErr})");
        }

        // A record saved with nulls (an older version wrote it, or it was edited by hand) loads whole.
        string shapes = Path.Combine(dir, "shapes");
        var store = new NpcStore(shapes, _ => { });
        Directory.CreateDirectory(shapes);
        File.WriteAllText(Path.Combine(shapes, NpcStore.FileNameFor("p_nulls")),
            "{\"id\":\"p_nulls\",\"persona\":{\"name\":\"Ada Moss\",\"age\":\"41\",\"nickname\":null,\"personality\":null,\"speech\":null,\"likes\":\"tea, rain\"}," +
            "\"visual\":{\"body\":\"woman\",\"description\":null},\"state\":null,\"history\":[null,{\"role\":\"player\",\"text\":null,\"gameDay\":2}],\"talks\":null,\"meetings\":[null]}");
        var loaded = OrNull(() => store.Load("p_nulls"));
        bool whole2 = loaded != null && loaded.Persona.Nickname == "" && loaded.Persona.Speech != null && loaded.Persona.Likes.SequenceEqual(new[] { "tea", "rain" })
                      && loaded.Visual.Description == "" && loaded.State != null && loaded.Talks != null && loaded.Meetings.Count == 0
                      && loaded.History.Count == 1 && loaded.History[0].Text == "";
        bool works = loaded != null && !Fails(() => Prompts.IdentityBlock(loaded)) && !Fails(() => Prompts.SituationBlock(loaded, snap));
        Check(whole2 && works, "a saved record with nulls in it loads with defaults in their place, and their prompts can be built");

        // The converters only read: a normal record saved, loaded and saved again is the same file, byte for byte.
        var normal = new NpcRecord
        {
            Id = "p_same", CreatedUtc = new DateTime(2026, 10, 1, 8, 30, 15, DateTimeKind.Utc),
            Persona = new Persona { Name = "Ivo Lund", Age = "52", Personality = new() { "patient", "wry" }, Speech = new SpeechStyle { SlangLevel = 2, Quirks = new() { "hums" } } },
            Visual = new NpcVisual { Body = "man", Description = "A grey coat." },
            State = new NpcState { TimesMet = 3, Affinity = -7, Mood = "content" },
        };
        normal.History.Add(new ChatLine { Role = "player", Text = "Hi \"there\" é—", GameDay = 4, Utc = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc) });
        store.Save(normal);
        string path = Path.Combine(shapes, NpcStore.FileNameFor("p_same"));
        byte[] first = File.ReadAllBytes(path);
        var again = store.Load("p_same");
        if (again != null) store.Save(again);
        Check(again != null && File.ReadAllBytes(path).AsSpan().SequenceEqual(first), "a normal record saved, loaded and saved again is the same file, byte for byte");

        // ROBUST-2: a plain string inside an AI list is skipped; everything else in the list is kept.
        var told = OrNull(() => ReplyParser.ParseStructured("{\"say\":\"I'm 22.\",\"toldAboutMe\":[\"age: 22\",{\"topic\":\"age\",\"said\":\"I'm 22\"}]}", "Oda"));
        Check(told != null && told.Told.Count == 1, "a stray string among what they told you is skipped, the rest kept (ROBUST-2, a chat reply)");
        var phone = OrNull(() => ChatterBrain.ParsePhone("{\"lines\":[\"hi\",{\"side\":\"caller\",\"say\":\"Hey.\"},{\"side\":\"other\",\"say\":\"Hi.\"},{\"side\":\"caller\",\"say\":\"Bye.\"}],\"summary\":\"x\",\"change\":0}", 4));
        Check(phone != null && phone.Lines.Count == 3, "...a phone call");
        var readTold = OrNull(() => NpcBrain.ReadTold(JsonNode.Parse("[\"x\",{\"topic\":\"job\",\"said\":\"I fix boats\"}]")));
        Check(readTold?.Count == 1, "...what a diary says they told you");

        var ann = MetBefore("p_ann", "Ann Lee");
        var ben = MetBefore("p_ben", "Ben Cole");
        var persons = new List<ChatterPrompts.Person> { new() { Record = ann, Snap = snap, MaxLines = 3 }, new() { Record = ben, Snap = snap, MaxLines = 3 } };
        string raw = "{\"lines\":[\"stray\",{\"speaker\":\"Ann\",\"to\":\"Ben\",\"say\":\"Long shift?\",\"leaving\":false}," +
                     "{\"speaker\":\"Ben\",\"to\":\"Ann\",\"say\":\"Always. Got to go.\",\"leaving\":true}]," +
                     "\"exits\":[\"stray\",{\"speaker\":\"Ann\",\"leave\":\"My bus is here.\",\"bye\":\"See you.\"}]," +
                     "\"getAlong\":[\"stray\",{\"a\":\"Ann\",\"b\":\"Ben\",\"change\":3}],\"summary\":\"Long shifts.\",\"heardAboutPlayer\":[\"stray\"]}";
        var script = OrNull(() => ChatterBrain.ParseScript(raw, persons));
        Check(script != null && script.Lines.Count == 2 && script.Exits.Count == 1, "...a residents' conversation (its lines and its reasons to leave)");

        var ai = new ScriptedAi();
        var client = new LlmClient(() => new LlmSettings { ApiKey = "k" }, _ => { }, new HttpMessageInvoker(ai));
        var people = new NpcStore(Path.Combine(dir, "shapes-people"), _ => { });
        var brain = new NpcBrain(client, people, () => new BrainSettings { LifeUpdates = false, MeetingSummaries = false }, _ => { });
        var pairs = new PairBook(Path.Combine(dir, "shapes-pairs.json"));
        var chatter = new ChatterBrain(brain, client, pairs, _ => { });
        people.Save(ann);
        people.Save(ben);
        bool remembered = false;
        if (script != null)
        {
            script.Raw = raw;
            script.Persons = persons;
            try { await chatter.RememberAsync(script, script.Lines.Count); remembered = true; } catch { }
        }
        Check(remembered && pairs.Get("p_ann", "p_ben")?.Relationship == 50.15 && TalkedWith(people, "p_ann", "Ben Cole"),
              "...and remembering it: stray strings in how they get on and in what they heard are skipped, the rest kept");
    }
}
