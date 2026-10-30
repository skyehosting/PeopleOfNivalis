using System;
using System.Collections.Generic;
using System.Linq;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Voice;

public enum VoiceProvider { None, OpenAI, ElevenLabs, Azure, Google, Deepgram, OpenAICompatible, Windows }

/// <summary>One "Voice Provider N" block of the settings.</summary>
public sealed class VoiceSlot
{
    public int Number;
    public VoiceProvider Provider;
    /// <summary>Relative chance a newly met person gets a voice from this provider (33/33/33 = an even split).</summary>
    public double Chance;
    public string ApiKey = "";
    public string Url = "";
    public string Model = "";
    /// <summary>Optional "name:gender, name2:gender" list; empty = every voice the provider offers.</summary>
    public string Voices = "";
    /// <summary>FB-41: the most this provider may use per <see cref="LimitPer"/>, in its own unit (0: no limit).</summary>
    public double Limit;
    /// <summary>"day", "week" or "month".</summary>
    public string LimitPer = "month";
    /// <summary>FB-59: the day of the month a monthly limit starts again on (0: not set; the 1st, or an ElevenLabs account's own day).</summary>
    public int ResetDay;

    public string NormalizedUrl => Url.Trim().TrimEnd('/').ToLowerInvariant();

    public bool NeedsKey => Provider is not (VoiceProvider.Windows or VoiceProvider.OpenAICompatible or VoiceProvider.None);

    /// <summary>Configured well enough to make sound.</summary>
    public bool Usable => Provider != VoiceProvider.None
        && (!NeedsKey || ApiKey.Trim().Length > 0)
        && (Provider is not (VoiceProvider.Azure or VoiceProvider.OpenAICompatible) || Url.Trim().Length > 0)
        && (Provider != VoiceProvider.Windows || OperatingSystem.IsWindows());

    /// <summary>Same provider and address as when a voice was assigned (the key may change freely).</summary>
    public bool Owns(VoiceAssignment a) => a.Provider == Provider.ToString() && a.Url == NormalizedUrl;
}

public sealed class VoiceSettings
{
    public bool Enabled;
    public bool MatchGender = true;
    /// <summary>Voices that can take it get a tone that follows how the conversation is going (see <see cref="Tone"/>).</summary>
    public bool Tones = true;
    public int TimeoutSeconds = 20;
    public List<VoiceSlot> Slots = new();
}

/// <summary>A voice a provider offers. Gender is "male", "female", "neutral" or "" (unknown).</summary>
public sealed class VoiceInfo
{
    public string Id = "";
    public string Name = "";
    public string Gender = "";
    public string Age = "";
    public string Locale = "";
    /// <summary>ElevenLabs: premade, generated, cloned, professional... (what plan it needs).</summary>
    public string Category = "";

    public override string ToString() => $"{Name} ({(Gender.Length > 0 ? Gender : "?")})";
}

/// <summary>The voice a person has for good (saved in voices.json).</summary>
public sealed class VoiceAssignment
{
    public string Provider { get; set; } = "";
    public string Url { get; set; } = "";
    public string VoiceId { get; set; } = "";
    public string VoiceName { get; set; } = "";
    public string Gender { get; set; } = "";
    public string Locale { get; set; } = "";
    /// <summary>A small personal tempo, so two people on the same voice still sound different.</summary>
    public double Speed { get; set; } = 1.0;
    /// <summary>
    /// The voice they speak with while their own can't be used (gone from the provider, or refused, e.g. after a plan
    /// change). Their own is tried again every session (and every half hour); as soon as it works, it's theirs again.
    /// </summary>
    public VoiceAssignment? StandIn { get; set; }
}

/// <summary>Who is speaking, as plain data (safe to use off the main thread).</summary>
public sealed class VoiceSubject
{
    public string Id = "";
    /// <summary>"male", "female" or "any".</summary>
    public string Presentation = "any";
    public int Age;
    /// <summary>For providers that take acting directions: who they are.</summary>
    public string Description = "";
    public string Mood = "";
    /// <summary>How they sound right now in this conversation (moves gradually line by line); null = from their mood as it is.</summary>
    public ToneState? Tone;
    /// <summary>
    /// A child's written line: nothing about them (no description, age or acting directions) goes to the voice provider.
    /// Their voice is still picked on this PC, by <see cref="Presentation"/> and <see cref="Age"/>.
    /// </summary>
    public bool Child;
    /// <summary>FB-41: what the line is for ("you", "chatter", "call", "greeting", "order"): the usage record keeps them apart.</summary>
    public string Purpose = "";

    public static VoiceSubject From(NpcRecord rec)
    {
        // Records are read from JSON written by a model: any text field may be missing (null).
        var p = rec.Persona ?? new Persona();
        var v = rec.Visual ?? new NpcVisual();
        var st = rec.State ?? new NpcState();
        string presentation = v.IsAndroid || Population.IsNonBinary(p.GenderIdentity ?? "") ? "any"
            : v.Body switch { "man" => "male", "woman" => "female", _ => "any" };
        int age = CanonAge.ReadDigits(new string((p.Age ?? "").TakeWhile(char.IsDigit).ToArray())) ?? 0; // digits in any script
        string who = presentation switch { "male" => "man", "female" => "woman", _ => "person" };
        string job = p.Occupation ?? "", temper = p.Temperament ?? "", mood = Npc.Features.Mood(st.Mood); // FB-45
        string desc = $"{(age > 0 ? age + "-year-old " : "")}{(v.IsAndroid ? "android" : who)}" +
                      $"{(job.Length > 0 ? ", " + job : "")}{(temper.Length > 0 ? "; " + temper : "")}";
        mood = mood.Length > 0 ? $"{st.MoodIntensity} {mood}".Trim() : "";
        return new VoiceSubject { Id = rec.Id ?? "", Presentation = presentation, Age = age, Description = desc, Mood = mood };
    }
}

/// <summary>What asking for a line's voice came to: the audio, or why there's none.</summary>
public sealed class VoiceOutcome
{
    public PcmAudio? Audio;
    public bool TimedOut;
    public string? Error;
    /// <summary>VoiceLimit reached: nothing was asked of the provider, and the line goes on as text (Error says why).</summary>
    public bool OverBudget;
    /// <summary>FB-30 (transcripts): who spoke it: the provider and its host, the model, the voice (never a key).</summary>
    public string Provider = "", Model = "", Voice = "";
}

/// <summary>Decoded speech, ready for an audio clip.</summary>
public sealed class PcmAudio
{
    public float[] Samples = Array.Empty<float>();
    public int SampleRate = 24000;
    public int Channels = 1;
    /// <summary>FB-41: what it cost, in its provider's unit (exact from ElevenLabs' header, or worked out); null: not known.</summary>
    public double? Cost;
    public float Seconds => SampleRate <= 0 ? 0 : Samples.Length / (float)(SampleRate * Math.Max(1, Channels));
}
