using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NivalisAIChat.Npc;

namespace NivalisAIChat.Tests;

/// <summary>
/// FB-61: memories belong to the save. The author's cases, on a temp folder: talk then save keeps it; load the same save
/// after talking without saving puts it back as it was; save over the same slot again keeps everything; a backup copy of a
/// save (made outside the game) starts from its original; an older save starts from the people met before this update; a
/// new game starts empty; the global files (usage, the under-18 list, voices) are never touched.
/// </summary>
internal static partial class Program
{
    private static void SaveSnapshotsTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- FB-61: memories belong to the save");
        string dir = Path.Combine(Path.GetTempPath(), "pon-saves-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "npcs"));
        Directory.CreateDirectory(Path.Combine(dir, "memory"));
        try { Run(dir); }
        finally { try { Directory.Delete(dir, true); } catch { } }

        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string saves = File.ReadAllText(Path.Combine(src, "ModRuntime.Saves.cs"));
        Check(saves.Contains("if (slot == null && !(newGame && day is >= 0 and <= 1))"), "a new game asked for and cancelled in a game under way never empties the city (nor one whose day can't be read)");
        Check(saves.Contains("string name = SaveSnapshots.BeforeLoadPrefix + DateTime.UtcNow.ToString(\"yyyyMMdd-HHmmss-fff\")")
              && saves.Contains("kept = saves.Take(name, \"\", version) != null;") && saves.Contains("if (!kept)"),
            "the memory as it was is kept before any load puts another back, and nothing is put back if it couldn't be (review F1)");
        Check(saves.Contains("_pendingLoad = null; // review F3"), "a new game asked for after a load drops that load (review F3)");
        Check(saves.Contains("NpcHold.SceneEpochNow() == _askedAtScene"), "a load or new game counts only once play is in a new scene (review F2)");
        Check(saves.Contains("ForgetMinorChecks();") && saves.IndexOf("try { await _minorScan", saves.IndexOf("ForgetMinorChecks();")) > 0,
            "another save's people are checked for anyone under 18 again before anything is said (review F4, F5)");
        Check(saves.Contains("_saveAfterSwap = (slot, autosave)"), "a save made during a swap is kept once it's done (review F14)");
        string hooks = File.ReadAllText(Path.Combine(src, "Game", "SaveHooks.cs"));
        Check(hooks.Contains("if (__result) ModRuntime.OnGameSaved(saveName, isAutoSave)"), "only a save that worked keeps the memory");
    }

    private static void Run(string dir)
    {
        string P(string rel) => Path.Combine(dir, rel);
        void W(string rel, string text) { Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!); File.WriteAllText(P(rel), text); }
        string R(string rel) => File.Exists(P(rel)) ? File.ReadAllText(P(rel)) : "<gone>";
        var warned = new List<string>();
        var s = new SaveSnapshots(dir, warned.Add);

        Check(!SaveSnapshots.PerSave.Any(x => x is "usage.json" or "voice_usage.json" or "voice_ledger.json" or "usage_plan.json" or "minors.json"
                                                 or "voices.json" or "custom_instructions.txt" or "transcripts" or "backups" or "saves"),
            "usage, the under-18 list, voices, your instructions, transcripts and backups are never per save");

        // Before this update: one memory for everything.
        W("npcs/person_a.json", "{\"met\":1}");
        W("world.json", "{\"rumors\":[]}");
        W("minors.json", "[\"kid\"]");
        W("usage.json", "{\"used\":5}");
        var legacy = s.Take(SaveSnapshots.Legacy, "", "0.6.0");
        Check(legacy != null && legacy.Files.Count == 2 && legacy.Files.ContainsKey("npcs/person_a.json") && s.HasLegacy,
            "the first run keeps the people met so far (only the per-save files)");

        // Load save A (from before the update) → the legacy memory; talk; save A.
        var (fromA, whyA) = s.SourceFor("save_A", "fp-a1");
        Check(fromA?.Slot == SaveSnapshots.Legacy && whyA.Contains("before memories were kept per save"), "an older save starts with everyone you'd met");
        Check(s.Restore(fromA) && R("npcs/person_a.json") == "{\"met\":1}", "...put in place");
        W("npcs/person_a.json", "{\"met\":2}");
        W("npcs/person_b.json", "{\"met\":1}");
        Check(s.Take("save_A", "fp-a1", "0.6.0") is { } a1 && a1.Files.Count == 3, "saving A keeps what was said");

        // Talk more, then load A again without saving: it's gone (the author's rollback).
        W("npcs/person_a.json", "{\"met\":3}");
        W("npcs/person_c.json", "{\"met\":1}");
        s.Take(SaveSnapshots.BeforeLoadPrefix + "1", "", "0.6.0");
        Check(s.Restore(s.SourceFor("save_A", "fp-a1").from) && R("npcs/person_a.json") == "{\"met\":2}" && R("npcs/person_c.json") == "<gone>",
            "loading A after talking without saving puts it back as it was saved (what was said since is gone)");
        Check(R("minors.json") == "[\"kid\"]" && R("usage.json") == "{\"used\":5}", "...the under-18 list and your usage are never touched");

        // Load, talk, save, talk, save again on the same slot: everything kept.
        W("npcs/person_a.json", "{\"met\":4}");
        s.Take("save_A", "fp-a2", "0.6.0");
        W("pairs.json", "{\"pairs\":1}");
        s.Take("save_A", "fp-a3", "0.6.0");
        s.Restore(null);
        Check(s.Restore(s.Find("save_A")) && R("npcs/person_a.json") == "{\"met\":4}" && R("pairs.json") == "{\"pairs\":1}",
            "saving over the same save again keeps everything said before");

        // A backup copy of A made outside the game: the same file content, another name → starts from A.
        var (copy, whyCopy) = s.SourceFor("save_A_backup", "fp-a3");
        Check(copy?.Slot == "save_A" && whyCopy.Contains("a copy"), "a copy of a save made outside the game starts from that save");

        // A new game: nobody knows you; the global files stay.
        s.Take(SaveSnapshots.BeforeLoadPrefix + "2", "", "0.6.0");
        Check(s.Restore(null) && !s.WorkingFiles().Any() && R("minors.json") == "[\"kid\"]" && R("usage.json") == "{\"used\":5}",
            "a new game: no per-save files, the global ones untouched");
        Check(s.Restore(s.Find("save_A")) && R("npcs/person_b.json") == "{\"met\":1}", "...and loading A again brings its people back");

        // Saved slots dedupe: one blob per version.
        int blobs = Directory.GetFiles(Path.Combine(dir, "saves", "blobs")).Length;
        s.Take("save_B", "fp-b", "0.6.0");
        Check(Directory.GetFiles(Path.Combine(dir, "saves", "blobs")).Length == blobs, "a save of the same memory adds no new files (each version kept once)");

        // Safety: a file nothing keeps is never removed; a missing blob means nothing changes.
        W("npcs/person_new.json", "{\"new\":true}");
        string legacyPath = Path.Combine(dir, "saves", "slots", SaveSnapshots.Legacy + ".json");
        Check(!s.Restore(s.Find(SaveSnapshots.Legacy)) && R("npcs/person_new.json") == "{\"new\":true}", "a file kept nowhere is left where it is (said in the log)");
        var broken = s.Find("save_B")!;
        broken.Files["npcs/person_x.json"] = new string('0', 64);
        W("npcs/person_a.json", "{\"met\":9}");
        Check(!s.Restore(broken) && R("npcs/person_a.json") == "{\"met\":9}", "a snapshot missing a kept file changes nothing");

        // Review F1: a file whose content is kept nowhere is never written over.
        s.Take(SaveSnapshots.BeforeLoadPrefix + "f1", "", "0.6.0");
        Check(s.Restore(s.Find("save_A")), "(set up) save_A put back");
        W("npcs/person_a.json", "{\"met\":\"unkept\"}");
        Check(!s.Restore(s.Find("save_B")) && R("npcs/person_a.json") == "{\"met\":\"unkept\"}",
            "a file changed since anything was kept is left as it is, not written over (review F1)");
        s.Take(SaveSnapshots.BeforeLoadPrefix + "f1b", "", "0.6.0");

        // Review F7: a slot's previous snapshot is kept one step back.
        var before = s.Find("save_B")!;
        W("npcs/person_prev.json", "{\"x\":1}");
        s.Take("save_B", "fp-b2", "0.6.0");
        var prev = s.Find(SaveSnapshots.PrevPrefix + "save_B");
        Check(prev != null && prev.Fingerprint == before.Fingerprint && prev.Files.Count == before.Files.Count,
            "saving over a slot keeps its snapshot before that one (review F7)");
        Check(s.SourceFor("save_X", before.Fingerprint).from?.Slot != prev?.Slot, "...which is never taken for another save's copy");
        File.Delete(P("npcs/person_prev.json"));

        // Review F13: a manifest naming a path outside the per-save files changes nothing; a damaged blob isn't put back.
        var evil = s.Find("save_B")!;
        evil.Files["../usage.json"] = evil.Files.Values.First();
        W("npcs/person_a.json", "{\"met\":\"evil-test\"}");
        s.Take(SaveSnapshots.BeforeLoadPrefix + "f13", "", "0.6.0");
        Check(!s.Restore(evil) && R("usage.json") == "{\"used\":5}" && R("npcs/person_a.json") == "{\"met\":\"evil-test\"}",
            "a snapshot naming a file outside the per-save ones changes nothing (review F13)");
        var good = s.Find("save_B")!;
        string hashA = good.Files["npcs/person_a.json"];
        string blobA = Path.Combine(dir, "saves", "blobs", hashA + ".gz");
        byte[] keptA = File.ReadAllBytes(blobA);
        using (var ms = new MemoryStream())
        {
            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                gz.Write(System.Text.Encoding.UTF8.GetBytes("{\"damaged\":true}"));
            File.WriteAllBytes(blobA, ms.ToArray());
        }
        Check(!s.Restore(good) && R("npcs/person_a.json") == "{\"met\":\"evil-test\"}", "a damaged kept file isn't put back (review F13)");
        File.WriteAllBytes(blobA, keptA);

        // Tidying: slots whose save file is gone, before-loads past the newest 5, and blobs nothing uses.
        for (int i = 3; i <= 9; i++) s.Take(SaveSnapshots.BeforeLoadPrefix + i, "", "0.6.0");
        W("npcs/person_temp.json", "{\"only\":\"in save_C\"}");
        s.Take("save_C", "fp-c", "0.6.0");
        File.Delete(P("npcs/person_temp.json"));
        foreach (var other in new[] { "save_A", "save_B", "save_A_backup" }) { }
        var exists = new HashSet<string> { "save_A", "save_B" };
        var (slots, freed) = s.Collect(exists.Contains);
        var left = s.All().Select(m => m.Slot).ToList();
        Check(!left.Contains("save_C") && left.Contains("save_A") && left.Contains(SaveSnapshots.Legacy)
              && left.Count(x => x.StartsWith(SaveSnapshots.BeforeLoadPrefix)) == SaveSnapshots.BeforeLoadsKept && slots >= 1 && freed >= 1,
            "tidying: a deleted save's snapshot and files nothing uses go; the legacy one and the newest before-loads stay");
        Check(File.Exists(legacyPath), "...the legacy snapshot is kept for good");
        Check(left.Contains(SaveSnapshots.PrevPrefix + "save_B") && !left.Contains(SaveSnapshots.PrevPrefix + "save_C"),
            "...a slot's previous snapshot goes with the slot's save file");

        Check(SaveSnapshots.SafeSlot("save_2026_10_09_08_58_40") && SaveSnapshots.SafeSlot("AUTOSAVE_2026_10_18_00_57_49") && SaveSnapshots.SafeSlot("_legacy")
              && !SaveSnapshots.SafeSlot("..\\evil") && !SaveSnapshots.SafeSlot("a/b") && !SaveSnapshots.SafeSlot("") && !SaveSnapshots.SafeSlot(" "),
            "a snapshot's name is the game's slot name, never a path");
        File.WriteAllText(P("one.sav"), "same bytes");
        File.WriteAllText(P("two.sav"), "same bytes");
        Check(SaveSnapshots.FingerprintOf(P("one.sav")) == SaveSnapshots.FingerprintOf(P("two.sav")) && SaveSnapshots.FingerprintOf(P("one.sav")).Length == 64
              && SaveSnapshots.FingerprintOf(P("none.sav")) == "", "a save file's fingerprint is its content (a copy has the same)");
        Check(warned.Count >= 2, "what couldn't be done is said in the log");
    }
}
