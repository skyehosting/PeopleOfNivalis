using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Nivalis.Dialogue;
using Nivalis.GhostSystem.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using UnityEngine;

namespace NivalisAIChat;

/// <summary>
/// FB-75 (the author: "I was hoping it would also backfill the audio messages that are played so I could go into my save
/// and re-play those lines audio", and "yes" to bringing in the rest of her seen lines): once a save has loaded, Ava's
/// conversations from the game's own history (CharacterDialogueHistoryManager: what you've seen) come into StoryBook, and so
/// into her phone thread, and each of her lines there is voiced by the game's own recording of it: played from the game
/// when you press play, nothing copied (a "game-" note; the clip's name is kept). Only lines on the game's history pages,
/// and of those only hers and your picks (anyone else's is left out); nothing is logged of a line's words.
/// Evidence: docs/research/probe-2026-10-19.md (17 of 18 lines matched, every clip loaded with no dialogue open).
/// </summary>
public static partial class ModRuntime
{
    private static int _avaBackfillGen = int.MinValue;
    private static float _avaBackfillAt = -1f, _nextAvaVoice;
    private static readonly Queue<StoryHistory.VoiceJob> AvaVoiceJobs = new();
    private static int _avaVoiceTries, _avaVoiced, _avaJobsGen;
    private static string _avaBackfillId = "";

    /// <summary>Every frame: the history read once per save (a quarter minute in), then one voice every 0.3 s.</summary>
    private static void TickAvaBackfill()
    {
        if (!Features.Phone || !Features.StoryMemory || _phone == null || _story == null || _resetting) return;
        if (_avaJobsGen != _resetGeneration) { AvaVoiceJobs.Clear(); _avaJobsGen = _resetGeneration; }
        if (_avaBackfillGen != _resetGeneration)
        {
            if (!InGameplay()) { _avaBackfillAt = -1f; return; }
            if (_avaBackfillAt < 0) { _avaBackfillAt = Time.unscaledTime + 15f; return; } // the save settles first
            if (Time.unscaledTime < _avaBackfillAt) return;
            _avaBackfillGen = _resetGeneration;
            _avaBackfillAt = -1f;
            try { BackfillAva(); } catch (Exception e) { Log.Warn("Ava's earlier lines couldn't be read from the game: " + e.GetType().Name); Log.Debug("ava backfill: " + e); }
            return;
        }
        if (AvaVoiceJobs.Count == 0 || Time.unscaledTime < _nextAvaVoice) return;
        _nextAvaVoice = Time.unscaledTime + 0.3f;
        if (InStoryDialogue()) return; // the game's own conversation has its clips: not now
        try { if (VoiceAvaLine(AvaVoiceJobs.Peek())) { AvaVoiceJobs.Dequeue(); _avaVoiceTries = 0; } }
        catch (Exception e) { AvaVoiceJobs.Dequeue(); _avaVoiceTries = 0; Log.Debug("ava voice: " + e.Message); }
        if (AvaVoiceJobs.Count == 0 && _avaVoiced > 0)
            Log.Info($"Story: {_avaVoiced} of Ava's earlier lines have her voice on your phone now (played from the game's own recordings).");
    }

    private static bool InStoryDialogue()
    {
        try { var m = GameContext.Existing<DialogueTreeProgressManager>(); return m != null && m.IsInDialogue; } catch { return true; }
    }

    /// <summary>The game's history with Ava, page by page, into StoryBook; her voiced lines queued.</summary>
    private static void BackfillAva()
    {
        Person? ava = null;
        foreach (var p in Resources.FindObjectsOfTypeAll<Person>())
            if (p != null && string.Equals(p.DisplayedName ?? "", "Ava", StringComparison.OrdinalIgnoreCase)) { ava = p; break; }
        if (ava == null) return;
        string id = GameContext.PersonId(ava);
        if (id.Length == 0 || KnownUnder18(id)) return;
        var hist = GameContext.Existing<CharacterDialogueHistoryManager>();
        var progress = GameContext.Existing<DialogueTreeProgressManager>();
        if (hist == null || progress == null) return;
        var data = hist.GetDataFor(ava);
        var all = progress.AllDialogueTrees;
        if (data == null || all == null || data.Pages == null || data.TreesGuids == null) return;

        // The lines of the conversations the game says you've seen with her: theirs (Ava's, or no speaker named, as
        // StoryDialogue reads a live line) and your picks, by id.
        var nodes = new Dictionary<string, StoryHistory.Node>(StringComparer.Ordinal);
        var choices = new Dictionary<string, StoryHistory.Choice>(StringComparer.Ordinal);
        IntPtr avaPtr = ava.Pointer;
        foreach (var guid in data.TreesGuids)
        {
            if (string.IsNullOrEmpty(guid) || !all.ContainsKey(guid)) continue;
            var tree = all[guid];
            var concrete = tree?.TryCast<DialogueTree>();
            if (concrete == null) continue;
            bool message = false;
            try { message = tree!.IsMessage; } catch { }
            var list = new Il2CppSystem.Collections.Generic.List<IDialogue>();
            try { concrete.GetAllLines(list); } catch { continue; }
            for (int i = 0; i < list.Count; i++)
            {
                var node = list[i]?.TryCast<DialogueWithChoices>();
                if (node == null) continue;
                string nodeId = node.articyGuid ?? "";
                if (nodeId.Length == 0) continue;
                Person? speaker = null;
                try { speaker = node.speaker; } catch { }
                // FB-78: by her id too: the tree's speaker can be another copy of her than the one found above (by pointer
                // alone, 0.10.0's first read dropped every line of hers).
                bool theirs = speaker == null || speaker.Pointer == avaPtr || GameContext.PersonId(speaker) == id;
                // Only ids the history lists are ever used (PageLines): the words are read here but kept only for those.
                if (!nodes.ContainsKey(nodeId)) nodes[nodeId] = new StoryHistory.Node(list[i].Text ?? "", theirs, node.voFileName ?? "", message);
                try
                {
                    var picks = node.choices;
                    if (picks == null) continue;
                    for (int c = 0; c < picks.Count; c++)
                    {
                        var choice = picks[c]?.plainObj?.TryCast<DialogueChoice>();
                        string cid = choice?.ArticyGUID ?? "";
                        if (choice == null || cid.Length == 0 || choices.ContainsKey(cid)) continue;
                        choices[cid] = new StoryHistory.Choice(new IChoice(choice.Pointer).Text ?? "", message);
                    }
                }
                catch { }
            }
        }

        // FB-78: what 0.10.0's first read left (pages with only your picks) goes, on her phone too, and is read again below.
        int repaired = 0;
        if (_story!.DropHistoryWithoutThem(id) is int broken && broken > 0)
        {
            repaired = _phone?.DropUntimedStory(id) ?? 0;
            Log.Info($"Story: {broken} pages of your history with Ava were read without her lines; reading them again ({repaired} lines off her phone first).");
        }
        int pages = 0, added = 0, hers = 0, yours = 0;
        var jobs = new List<StoryHistory.VoiceJob>();
        foreach (var entry in data.Pages)
        {
            var page = entry.Item2;
            if (page?.Lines == null) continue;
            pages++;
            var ids = new List<(string, bool)>();
            foreach (var line in page.Lines) ids.Add((line.Item1 ?? "", line.Item2));
            var (lines, voices) = StoryHistory.PageLines(ids, nodes, choices);
            if (lines.Count == 0) continue;
            if (_story!.ImportPage(id, "Ava", Math.Max(0, entry.Item1), lines))
            {
                added++;
                hers += lines.Count(l => l.Who == StoryBook.Them);
                yours += lines.Count(l => l.Who == StoryBook.You);
            }
            jobs.AddRange(voices);
        }
        nodes.Clear(); choices.Clear(); // nothing kept of what wasn't on a page
        _avaId ??= id;
        _avaBackfillId = id;
        int threaded = 0;
        try
        {
            EnsureAva();
            MirrorAva();
            // The pages read in go before what's mirrored already: put in at their place in time (MirrorStory only adds newer).
            if (_avaId == id) threaded = _phone!.MirrorEarlierStory(id, AvaStoryLines());
        }
        catch (Exception e) { Log.Debug("ava mirror: " + e.Message); }
        _avaVoiced = 0;
        foreach (var j in jobs.GroupBy(j => j.Text, StringComparer.Ordinal).Select(g => g.First()))
            if (!_phone!.HasStoryVoice(j.Text)) AvaVoiceJobs.Enqueue(j);
        if (added > 0 || threaded > 0 || AvaVoiceJobs.Count > 0)
            Log.Info($"Story: the game's history with Ava read ({pages} pages): {added} page{(added == 1 ? "" : "s")} added to your story with her ({hers} of her lines, {yours} of yours), " +
                     $"{threaded} earlier line{(threaded == 1 ? "" : "s")} into her phone thread, {AvaVoiceJobs.Count} of her lines to give her voice.");
    }

    /// <summary>One of her lines voiced from the game's recording: true when it's done (voiced, or nothing to be had).</summary>
    private static bool VoiceAvaLine(StoryHistory.VoiceJob job)
    {
        if (_phone == null || _phone.HasStoryVoice(job.Text)) return true;
        var clip = Resources.Load(job.Voice, Il2CppType.Of<AudioClip>())?.TryCast<AudioClip>();
        if (clip == null) return true;
        if (clip.loadState != AudioDataLoadState.Loaded)
        {
            if (clip.loadState == AudioDataLoadState.Failed) return true;
            if (clip.loadState == AudioDataLoadState.Unloaded) clip.LoadAudioData();
            return ++_avaVoiceTries > 12; // three seconds, then it's let go
        }
        int channels = Math.Max(1, clip.channels), frames = Math.Min(clip.samples, clip.frequency * 20);
        string wave = "";
        if (frames > 0)
        {
            var data = new Il2CppStructArray<float>(frames * channels);
            if (clip.GetData(data, 0))
            {
                var samples = new float[frames * channels];
                // One copy of the array's data: an IL2CPP array is 4 pointers (class, monitor, bounds, length), then its elements.
                System.Runtime.InteropServices.Marshal.Copy(data.Pointer + 4 * IntPtr.Size, samples, 0, samples.Length);
                wave = Phone.VoiceNote.WaveText(Phone.VoiceNote.Wave(samples));
            }
        }
        _phone.StoryGameVoice(_avaBackfillId, job.Text, job.Voice, clip.length, wave);
        _avaVoiced++;
        // Ours to let go of: the game loads it again whenever it's needed (Resources.UnloadAsset), never while a note plays it.
        if (!PhoneUi.PlayingGameVoice) try { Resources.UnloadAsset(clip); } catch { }
        return true;
    }

    /// <summary>The phone's player: a game note's clip, by its name (FB-75).</summary>
    private static AudioClip? GameVoiceClip(string file)
    {
        string? voice = _phone?.GameVoiceOf(file);
        if (string.IsNullOrEmpty(voice)) return null;
        try
        {
            var clip = Resources.Load(voice, Il2CppType.Of<AudioClip>())?.TryCast<AudioClip>();
            if (clip != null && clip.loadState == AudioDataLoadState.Unloaded) clip.LoadAudioData();
            return clip;
        }
        catch (Exception e) { Log.Debug("game voice: " + e.Message); return null; }
    }
}
