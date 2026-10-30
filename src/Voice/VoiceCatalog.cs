using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NivalisAIChat.Voice;

/// <summary>Voice lists the mod knows by heart, the user's own lists, and reading gender from what providers tell us.</summary>
public static class VoiceCatalog
{
    private static VoiceInfo V(string id, string gender) => new() { Id = id, Name = id, Gender = gender };

    /// <summary>OpenAI's built-in voices (gpt-4o-mini-tts has all 13; tts-1 / tts-1-hd the first nine).</summary>
    public static List<VoiceInfo> OpenAI(string model)
    {
        var all = new List<VoiceInfo>
        {
            V("alloy", "neutral"), V("ash", "male"), V("coral", "female"), V("echo", "male"), V("fable", "male"),
            V("onyx", "male"), V("nova", "female"), V("sage", "female"), V("shimmer", "female"),
            V("ballad", "male"), V("verse", "male"), V("marin", "female"), V("cedar", "male"),
        };
        bool legacy = model.StartsWith("tts-1", StringComparison.OrdinalIgnoreCase);
        return legacy ? all.Take(9).ToList() : all;
    }

    /// <summary>Deepgram's English Aura-2 voices, as their model list labels them.</summary>
    public static List<VoiceInfo> Deepgram()
    {
        string[] female = { "amalthea", "andromeda", "asteria", "athena", "aurora", "callista", "cora", "cordelia", "delia", "electra", "harmonia",
                            "helena", "hera", "iris", "janus", "juno", "luna", "minerva", "ophelia", "pandora", "phoebe", "selene", "thalia", "theia", "vesta" };
        string[] male = { "apollo", "arcas", "aries", "atlas", "draco", "hermes", "hyperion", "jupiter", "mars", "neptune", "odysseus", "orion",
                          "orpheus", "pluto", "saturn", "zeus" };
        return female.Select(n => new VoiceInfo { Id = $"aura-2-{n}-en", Name = n, Gender = "female" })
            .Concat(male.Select(n => new VoiceInfo { Id = $"aura-2-{n}-en", Name = n, Gender = "male" })).ToList();
    }

    private static readonly Regex NamedId = new(@"^(?<name>[^()]+?)\s*\((?<id>[^()]+)\)$");

    /// <summary>
    /// "nova:female, onyx:male, af_bella, Aria (9BWtsMINqrJLrRacOk9x):female" → voices. Gender after a colon (else guessed
    /// from Kokoro-style names); "Name (id)" when the provider's id isn't a readable name.
    /// </summary>
    public static List<VoiceInfo> Parse(string list)
    {
        var result = new List<VoiceInfo>();
        foreach (var raw in (list ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string item = raw.Trim();
            if (item.Length == 0) continue;
            string id = item, gender = "";
            int colon = item.LastIndexOf(':');
            if (colon > 0)
            {
                string tail = Gender(item[(colon + 1)..]);
                if (tail.Length > 0) { id = item[..colon].Trim(); gender = tail; }
            }
            string name = id;
            var named = NamedId.Match(id);
            if (named.Success) { name = named.Groups["name"].Value.Trim(); id = named.Groups["id"].Value.Trim(); }
            if (gender.Length == 0) gender = KokoroGender(id);
            result.Add(new VoiceInfo { Id = id, Name = name, Gender = gender });
        }
        return result;
    }

    /// <summary>
    /// Ready-made Voices lines for the settings file (written commented out under an empty Voices setting).
    /// OpenAI doesn't publish genders; these follow how the voices are commonly heard. ElevenLabs ids are its
    /// published voice ids; its classic default voices are retired on 31 December 2026.
    /// </summary>
    public static List<(string heading, string list)> ReadyMade(VoiceProvider provider) => provider switch
    {
        VoiceProvider.OpenAI => new()
        {
            ("OpenAI's 13 voices (gpt-4o-mini-tts; tts-1 only has the first nine). Delete any you don't want:",
             "alloy:neutral, ash:male, coral:female, echo:male, fable:male, onyx:male, nova:female, sage:female, shimmer:female, ballad:male, verse:male, marin:female, cedar:male"),
        },
        VoiceProvider.ElevenLabs => new()
        {
            ("ElevenLabs' default voices (accounts made before March 2026; ElevenLabs retires them on December 31, 2026):",
             "Aria (9BWtsMINqrJLrRacOk9x):female, Roger (CwhRBWXzGAHq8TQ4Fs17):male, Sarah (EXAVITQu4vr4xnSDxMaL):female, " +
             "Laura (FGY2WhTYpPnrIDTdsKH5):female, Charlie (IKne3meq5aSn9XLyUdCD):male, George (JBFqnCBsd6RMkjVDRZzb):male, " +
             "Callum (N2lVS1w4EtoT3dr4eOWO):male, River (SAz9YHcvj6GT2YYXdXww):neutral, Liam (TX3LPaxmHKxFdv7VOQHJ):male, " +
             "Charlotte (XB0fDUnXU5powFXDhCwa):female, Alice (Xb7hH8MSUJpSbSDYk0k2):female, Matilda (XrExE9yKIg1WjnnlVkGX):female, " +
             "Will (bIHbv24MWmeRgasZH58o):male, Jessica (cgSgspJ2msm6clMCkdW9):female, Eric (cjVigY5qzO86Huf0OWal):male, " +
             "Chris (iP95p4xoKVk53GoZ742B):male, Brian (nPczCjzI2devNBz1zQrb):male, Daniel (onwK4e9ZLuTAKqWW03F9):male, " +
             "Lily (pFZP5JQG7iQjIQuC4Bku):female, Bill (pqHfZKP75CvOlQylNhV4):male"),
            ("ElevenLabs' newer voices that replace them (Voice Library voices: using them through the API needs a paid plan):",
             "Darian (gOupLcAkjEnguROwi4oS):male, Talia (OZ0L6eISlOejga3XjDFt):female, Elara (WQP7cQUF5aAS6Axh5yaa):female, " +
             "Baxter (jSuBIjxMKhqIfb0wCK1F):male, Eldrin (6WwXjDDEMyNmFG95zycZ):male, Sawyer (8dEUmyPMdDdK91vboYih):male, " +
             "Alicia (DNYvvkdiu5NYK2rSLwDL):female, Bella (hpp4J3VqNfWAUOO0d1Us):female, Caleb (AaOhDHYJ1XLZk74lXhdE):male, " +
             "Eddie (l7kNoIfnJKPg7779LI2t):male, Elowen (dvbL7qkNGZY1IqPGZAjM):female, Florence (22N9cF8z0o7y23njdyaY):female, " +
             "Jade (g7LVvkPWALzPxOQbF6OE):female, Kaelen (10NkTYmU7tSz3Kkl3Lex):male, Lawrence (ktkP7Nsj67dw2zcplQYt):male, " +
             "Maisie (QtY3JBOUKEB5xzrRfOKc):female, Warren (7QN34D2r3hCNwbOYIeK0):male, Wyatt (FrS6cKLB1wg4WYgPa9GW):male"),
        },
        VoiceProvider.Deepgram => new()
        {
            ("Deepgram's English Aura-2 voices:",
             string.Join(", ", Deepgram().Select(v => $"{v.Id}:{v.Gender}"))),
        },
        _ => new(),
    };

    /// <summary>Normalises whatever a provider calls it: Male/MALE/masculine/man → male, and so on. "" when unknown.</summary>
    public static string Gender(string? word)
    {
        string w = (word ?? "").Trim().ToLowerInvariant();
        return w switch
        {
            "male" or "m" or "man" or "masculine" or "boy" => "male",
            "female" or "f" or "woman" or "feminine" or "girl" => "female",
            "neutral" or "non-binary" or "nonbinary" or "androgynous" or "ssml_voice_gender_unspecified" => "neutral",
            _ => "",
        };
    }

    private static readonly Regex KokoroName = new(@"^[a-z]([fm])_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Kokoro-style names carry gender in the prefix: af_bella (American female), bm_george (British male).</summary>
    public static string KokoroGender(string id)
    {
        var m = KokoroName.Match(id ?? "");
        return !m.Success ? "" : m.Groups[1].Value.ToLowerInvariant() == "f" ? "female" : "male";
    }

    /// <summary>"young", "middle aged", "old" labels → the band a person's age falls in.</summary>
    public static string AgeBand(int age) => age <= 0 ? "" : age < 30 ? "young" : age >= 55 ? "old" : "middle";

    /// <summary>
    /// A voice for this person. With gender matching on, only voices of their presentation (or neutral / unknown ones)
    /// qualify; returns null when the provider has none, so another provider can be tried. Age labels, where the
    /// provider has them, narrow it further when there's enough choice.
    /// </summary>
    public static VoiceInfo? Pick(IReadOnlyList<VoiceInfo> voices, VoiceSubject who, bool matchGender, Random rng)
    {
        if (voices.Count == 0) return null;
        var pool = voices.ToList();
        if (matchGender && who.Presentation is "male" or "female")
        {
            var same = pool.Where(v => v.Gender == who.Presentation).ToList();
            pool = same.Count > 0 ? same : pool.Where(v => v.Gender is "" or "neutral").ToList();
            if (pool.Count == 0) return null;
        }
        else if (matchGender && who.Presentation == "any")
        {
            var neutral = pool.Where(v => v.Gender == "neutral").ToList();
            if (neutral.Count > 0 && rng.NextDouble() < 0.5) pool = neutral;
        }
        string band = AgeBand(who.Age);
        if (band.Length > 0)
        {
            var aged = pool.Where(v => v.Age.Contains(band, StringComparison.OrdinalIgnoreCase)).ToList();
            if (aged.Count >= 2) pool = aged;
        }
        return pool[rng.Next(pool.Count)];
    }
}
