using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Nivalis;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Voice;
using UnityEngine;

namespace NivalisAIChat;

/// <summary>
/// FB-30: transcripts of everything said (your chats, residents' conversations, calls, mutters, order talk, greetings), with
/// what shaped each line, while KeepTranscripts is on. `/transcripts export` makes a readable document for research.
/// Nobody under 18 is ever recorded; keys never are.
/// </summary>
public static partial class ModRuntime
{
    private static TranscriptLog? _transcripts;
    /// <summary>Starting failed (a folder that can't be written, say): not tried again before this (code review 6).</summary>
    private static float _transcriptsRetryAt;

    private static string TranscriptDir => Path.Combine(_dataDir, "transcripts");

    /// <summary>Starts or stops with the setting (a /transcripts command, an edit to the file, or a reload). Called every frame: two checks.</summary>
    private static void SyncTranscripts()
    {
        bool on = _cfg.KeepTranscripts.Value;
        if (on && _transcripts == null)
        {
            if (Time.unscaledTime < _transcriptsRetryAt) return;
            TranscriptLog? log = null;
            try
            {
                log = new TranscriptLog(TranscriptDir, w => Log.Debug(w));
                log.Header(SessionHeader());
                _transcripts = log;
                Log.Info($"Keeping transcripts in {TranscriptDir} (KeepTranscripts). /transcripts export makes a readable copy.");
            }
            catch (Exception e)
            {
                try { log?.Close(); } catch { } // its writer doesn't run on (code review 6)
                _transcriptsRetryAt = Time.unscaledTime + 60f;
                Log.Warn("Transcripts couldn't start (tried again in a minute): " + e.Message);
            }
        }
        else if (!on && _transcripts != null) StopTranscripts();
    }

    private static void StopTranscripts()
    {
        try { _transcripts?.Close(); } catch { }
        _transcripts = null;
    }

    /// <summary>The settings and instructions in force for this session (models, chatter, voices without their keys).</summary>
    private static JsonObject SessionHeader()
    {
        var s = ChatterSettingsNow();
        var voices = new JsonArray();
        try
        {
            foreach (var slot in VoiceSettingsNow().Slots)
            {
                string host = "";
                try { if (Uri.TryCreate(slot.Url, UriKind.Absolute, out var u)) host = u.Host; } catch { }
                voices.Add($"{slot.Provider}{(host.Length > 0 ? " (" + host + ")" : "")}, model {Redact.Value(VoiceService.ModelOf(slot))}, {slot.Chance:0}%");
            }
        }
        catch { }
        var instructions = new JsonObject();
        foreach (var f in CustomInstructions.Fields)
        {
            string now = CustomInstructions.Get(f.Name);
            instructions[f.Name] = now == f.Default.Trim() ? "default" : now.Length == 0 ? "(left out)" : now;
        }
        if (CustomInstructions.Text is { Length: > 0 } own) instructions["your own text"] = own;
        return new JsonObject
        {
            ["mod"] = Plugin.Version,
            ["started"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            ["chat model"] = Redact.Value(_cfg.Model.Value),
            ["background model"] = Redact.Value(_cfg.BackgroundModel.Value),
            ["reasoning effort"] = _cfg.ReasoningEffort.Value,
            ["structured replies"] = _cfg.StructuredReplies.Value,
            ["voice tones"] = _cfg.VoiceTones.Value,
            ["chatter"] = $"{(s.Enabled ? "on" : "off")}, {(s.Dynamic ? $"Dynamic every {Social.ChatterSettings.Pretty(s.DynamicEverySeconds)}, at most {s.DynamicMaxNpcs} talking, {s.DynamicLines} lines each" : "Advanced")}",
            ["voices"] = voices,
            ["instructions"] = instructions,
        };
    }

    /// <summary>How someone talks, from their record (written once per file).</summary>
    private static JsonObject PersonEntry(NpcRecord rec)
    {
        var p = rec.Persona;
        var sp = p.Speech;
        static string J(List<string>? items) => string.Join("; ", items ?? new List<string>()); // a list missing from an old file (code review 14)
        return new JsonObject
        {
            ["name"] = p.Name, ["age"] = p.Age, ["pronouns"] = p.Pronouns, ["job"] = p.Occupation, ["temperament"] = p.Temperament,
            ["personality"] = J(p.Personality), ["humour"] = p.Humor, ["faith"] = p.Faith,
            ["speech"] = sp == null ? "" : $"slang {sp.SlangLevel}/3, {sp.Formality}, {sp.Verbosity}, swearing {sp.Swearing}, rhythm \"{sp.Rhythm}\", " +
                                            $"quirks [{J(sp.Quirks)}], catchphrases [{J(sp.Catchphrases)}], favourite words [{J(sp.FavouriteWords)}]",
            ["voice"] = p.Voice,
        };
    }

    /// <summary>
    /// One line said, with what shaped it, while transcripts are on (nothing at all otherwise). Never for someone under 18.
    /// <paramref name="by"/>: "ai" (the chat model) or "written" (the mod's own lines).
    /// </summary>
    private static void RecordLine(string kind, string group, Character? who, string id, NpcRecord? rec, string text, string to = "",
        string mood = "", ToneState? tone = null, string gesture = "", VoiceOutcome? voice = null, float seconds = 0f, string by = "ai",
        GameSnapshot? where = null, double? relationship = null, string speakerName = "")
    {
        var log = _transcripts;
        if (log == null || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            if (rec != null && ActiveBrain.IsKnownMinor(rec)) return;
            if (id.Length > 0 && ActiveBrain.KnownMinors.Contains(id)) return;
            if (who != null && id.Length > 0 && MinorInfo(who, id).minor) return;
            string name = speakerName.Length > 0 ? speakerName : rec != null && rec.Persona.Name.Length > 0 ? rec.Persona.Name
                        : id == "player" ? "You" : who != null ? (who.name ?? "").Replace("(Clone)", "").Trim() : id;
            if (rec != null) log.Person(rec.Id, () => PersonEntry(rec));
            var o = new JsonObject
            {
                ["t"] = DateTime.UtcNow.ToString("o"), ["kind"] = kind, ["group"] = group, ["speaker"] = name, ["to"] = to, ["text"] = text,
            };
            if (where != null)
            {
                o["day"] = where.GameDay.ToString();
                o["clock"] = where.Clock;
                if (where.Location.Length > 0) o["place"] = where.Location;
            }
            if (mood.Length > 0) o["mood"] = mood;
            if (tone != null && Tone.Phrase(tone) is { Length: > 0 } phrase) o["tone"] = phrase;
            if (gesture.Length > 0) o["gesture"] = gesture;
            if (relationship is { } r && double.IsFinite(r)) o["relationship"] = $"{r:0.#}%";
            string spokeBy = voice == null ? "" : string.Join(" ", new[] { voice.Provider, voice.Model, voice.Voice }.Where(x => !string.IsNullOrWhiteSpace(x)));
            o["voice"] = voice?.Audio != null ? $"{(spokeBy.Length > 0 ? spokeBy + ", " : "")}{seconds:0.0} s"
                       : voice?.Error is { Length: > 0 } err ? "none (" + Redact.Text(err) + ")"
                       : "text only";
            // Background requests (thoughts, calls, greetings, brush-offs) go to BackgroundModel when it's set (code review 12).
            string background = _cfg.BackgroundModel.Value.Trim();
            o["by"] = by switch
            {
                "ai" => "AI: " + Redact.Value(_cfg.Model.Value),
                "ai-background" => "AI: " + Redact.Value(background.Length > 0 ? background : _cfg.Model.Value),
                _ => by,
            };
            log.Line(o);
        }
        catch (Exception e) { Log.Debug("transcript: " + e.Message); }
    }

    /// <summary>A written line someone said (order talk, a goodbye at a table): recorded as the mod's own words.</summary>
    private static void RecordWritten(Character who, string id, NpcRecord? rec, string text, string kind) =>
        RecordLine(kind, kind + ":" + id, who, id, rec, text, by: "written");

    /// <summary>How they feel about each other, between two residents (0-100%), when they've met.</summary>
    private static double? PairRelationship(string a, string b)
    {
        try { return _pairs?.Get(a, b)?.Relationship; } catch { return null; }
    }

    /// <summary>/transcripts on|off|status|export [all]</summary>
    private static void TranscriptsCommand(string sub, string arg, Action<string> say)
    {
        switch (sub)
        {
            case "on":
            case "off":
                _cfg.KeepTranscripts.Value = sub == "on"; // saved to the settings file at once
                SyncTranscripts();
                say(sub == "on" ? $"Keeping transcripts (saved in your settings): {TranscriptDir}" : "Transcripts off (the files kept so far stay).");
                break;
            case "export":
            {
                string dir = TranscriptDir;
                string stamp = _transcripts?.Stamp ?? "";
                bool all = arg == "all";
                say("Exporting...");
                int askedInConsole = _consoleOpen ? _consoleSession : -1, askedInChat = ChatOpen ? _openSerial : -1;
                // Off the game's thread: a long session can be big (code review 3). The answer goes to the window that asked, if it's still open.
                Task.Run(() =>
                {
                    string result;
                    try
                    {
                        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*" + TranscriptLog.Extension).ToList() : new List<string>();
                        if (files.Count == 0) result = "No transcripts yet. /transcripts on starts keeping them.";
                        else
                        {
                            if (!all)
                            {
                                // This session's file(s) by default; the newest one kept when transcripts are off now; "all" for every one.
                                var mine = stamp.Length > 0 ? TranscriptLog.SessionFiles(dir, stamp) : new List<string>();
                                files = mine.Count > 0 ? mine : new List<string> { files.OrderBy(File.GetLastWriteTimeUtc).Last() };
                            }
                            string outFile = Path.Combine(dir, $"export-{DateTime.Now:yyyy-MM-dd_HHmmss}.md");
                            File.WriteAllText(outFile, TranscriptLog.Export(files, DateTime.Now));
                            TranscriptLog.RotateExports(dir, TranscriptLog.KeepExports);
                            result = $"Exported {files.Count} session file(s) to {outFile}";
                        }
                    }
                    catch (Exception e) { result = "Couldn't export: " + e.Message; }
                    MainThread.Enqueue(() =>
                    {
                        bool stillThere = (askedInConsole >= 0 && _consoleOpen && _consoleSession == askedInConsole)
                                          || (askedInChat >= 0 && ChatOpen && _openSerial == askedInChat);
                        if (stillThere) say(result);
                        Log.Info("/transcripts export: " + result);
                    });
                });
                break;
            }
            default:
                say(_transcripts != null
                    ? $"Keeping transcripts: {_transcripts.CurrentFile}. /transcripts export (this session) or /transcripts export all; /transcripts off."
                    : "Not keeping transcripts. /transcripts on starts (it's saved in your settings as KeepTranscripts).");
                break;
        }
    }
}
