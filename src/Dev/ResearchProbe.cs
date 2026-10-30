using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Nivalis;
using Nivalis.Dialogue;
using Nivalis.Fishing;
using Nivalis.GhostSystem.Ai;
using Nivalis.InventorySystem;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using UnityEngine;
using Character = Nivalis.Character;

namespace NivalisAIChat.Dev;

/// <summary>
/// Research probe (2026-10-19, for CF-19, Ava's old lines' audio, FB-73). Read-only toward the game and the save; anything
/// it changes for a test (one name plate's text) is put back within three seconds. Answers go to LogOutput.log as
/// "Probe:" lines:
/// - /probe names: who has a name plate and whether the game already hides unmet people's names; whether the game
///   rewrites a plate's text (a one-plate test); the candidate hooks' native code, and how many methods share it.
/// - /probe ava: how Ava's conversations you've seen map to the game's dialogue data and voice files. Only lines already
///   in your StoryBook are looked at by text: every other line is counted, never logged or kept. Then a load test of up
///   to three of those seen lines' clips (nothing saved).
/// - /probe fish: fishing spots, residents fishing now, fishing poses, rods in the world, the water near you, the hooks'
///   native code.
/// - /probe place: every name for where you are (the district, the venue you're in), and the venue areas near you.
/// - /probe watch: for 30 minutes, logs when someone becomes "met" and what fishing collects or unlocks.
/// </summary>
internal static class ResearchProbe
{
    private static void P(string s) => Log.Info("Probe: " + s);

    private static void Guard(string what, Action act)
    {
        try { act(); }
        catch (Exception e) { P($"{what}: FAILED {e.GetType().Name}: {e.Message}"); }
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }

    /// <summary>True when this sub-command was one of ours.</summary>
    public static bool Run(string a, Action<string> say)
    {
        switch (a)
        {
            case "names": Names(); break;
            case "ava": Ava(); break;
            case "fish": Fish(); break;
            case "watch": Watch(); break;
            case "place": Place(); break;
            default: return false;
        }
        say($"Probe {a}: written to LogOutput.log (lines starting \"Probe:\").");
        return true;
    }

    // ------------------------------------------------------------------ native code (shared bodies)

    private static Dictionary<IntPtr, int>? _codeUse;

    /// <summary>How many methods in every loaded assembly use each native body (read through il2cpp's own tables: no game code runs).</summary>
    private static unsafe Dictionary<IntPtr, int> CodeUse()
    {
        if (_codeUse != null) return _codeUse;
        var map = new Dictionary<IntPtr, int>();
        uint n = 0;
        IntPtr* asms = IL2CPP.il2cpp_domain_get_assemblies(IL2CPP.il2cpp_domain_get(), ref n);
        long methods = 0;
        for (uint i = 0; i < n; i++)
        {
            IntPtr image = IL2CPP.il2cpp_assembly_get_image(asms[i]);
            if (image == IntPtr.Zero) continue;
            uint classes = IL2CPP.il2cpp_image_get_class_count(image);
            for (uint c = 0; c < classes; c++)
            {
                IntPtr klass = IL2CPP.il2cpp_image_get_class(image, c);
                if (klass == IntPtr.Zero) continue;
                IntPtr iter = IntPtr.Zero, m;
                while ((m = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                {
                    IntPtr code = *(IntPtr*)m;
                    if (code == IntPtr.Zero) continue;
                    methods++;
                    map[code] = map.TryGetValue(code, out var k) ? k + 1 : 1;
                }
            }
        }
        P($"native: {methods} methods with code in {n} assemblies, {map.Count} distinct bodies");
        return _codeUse = map;
    }

    private static unsafe void Native(Type type, string name, params Type[] args)
    {
        Guard($"native {type.Name}.{name}", () =>
        {
            var target = args.Length > 0 ? AccessTools.Method(type, name, args) : AccessTools.Method(type, name);
            if (target == null) { P($"native {type.Name}.{name}: no such method"); return; }
            var field = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(target);
            if (field == null) { P($"native {type.Name}.{name}: no method info"); return; }
            var info = (IntPtr)field.GetValue(null)!;
            if (info == IntPtr.Zero) { P($"native {type.Name}.{name}: method info is null"); return; }
            byte* code = *(byte**)info;
            if (code == null) { P($"native {type.Name}.{name}: no native code"); return; }
            var hex = new StringBuilder();
            for (int i = 0; i < 16; i++) hex.Append(code[i].ToString("X2")).Append(i == 15 ? "" : " ");
            bool trivial = code[0] == 0xC3 || code[0] == 0xC2 || (code[0] == 0xF3 && code[1] == 0xC3)
                           || ((code[0] == 0x31 || code[0] == 0x33) && code[1] == 0xC0 && code[2] == 0xC3) || (code[0] == 0xB0 && code[2] == 0xC3);
            int shared = CodeUse().TryGetValue((IntPtr)code, out var k) ? k : 0;
            P($"native {type.Name}.{name}({string.Join(",", target.GetParameters().Select(p => p.ParameterType.Name))}): " +
              $"used by {shared} method{(shared == 1 ? "" : "s")}{(shared > 1 ? " (SHARED)" : " (its own)")}{(trivial ? ", TRIVIAL" : "")}; first bytes {hex}");
        });
    }

    // ------------------------------------------------------------------ CF-19: names

    private static bool Met(Person? p) => Safe(() => p != null && p.RuntimeData != null && p.RuntimeData.HasBeenMet);

    private static string Who(Person? p)
    {
        if (p == null) return "(no person)";
        string name = Safe(() => p.DisplayedName) ?? "";
        if (Minors.IsStoryChild(Safe(() => p.Guid), name, Safe(() => p.Description))) return "(a child)";
        return name.Length > 0 ? name : "(no name)";
    }

    private static void Names()
    {
        P("--- names ---");
        CharacterNameDisplay? testPlate = null;
        Guard("plates", () =>
        {
            var all = CharacterNameDisplay.instances;
            int count = Safe(() => all.Count);
            int shownMet = 0, shownUnmet = 0, hiddenMet = 0, hiddenUnmet = 0, noPerson = 0, logged = 0;
            for (int i = 0; i < count; i++)
            {
                var d = Safe(() => all[i]);
                if (d == null || d.WasCollected) continue;
                var person = Safe(() => d.person);
                var ui = Safe(() => d._characterNameInstance);
                string text = Safe(() => ui?.nameText?.text) ?? "";
                bool visible = Safe(() => ui != null && ui.gameObject.activeInHierarchy && (ui.group == null || ui.group.alpha > 0.01f) && text.Length > 0);
                bool should = Safe(() => d.ShouldDisplayName());
                bool met = Met(person);
                if (person == null) noPerson++;
                else if (visible) { if (met) shownMet++; else shownUnmet++; }
                else { if (met) hiddenMet++; else hiddenUnmet++; }
                if (logged < 14 && person != null)
                {
                    logged++;
                    string who = Who(person);
                    P($"plate: {who} met={met} hasStory={Safe(() => d.hasStory)} should={should} visible={visible} text={(who.StartsWith("(") ? "-" : (text == who ? "its name" : $"'{text}'"))}");
                    if (visible && !met && testPlate == null) testPlate = d;
                }
                if (testPlate == null && visible && person != null) testPlate = d;
            }
            P($"plates: {count} in all; shown: {shownMet} met, {shownUnmet} not met; hidden: {hiddenMet} met, {hiddenUnmet} not met; {noPerson} with no person");
        });

        // Does the game write a plate's text once, or again and again?
        if (testPlate != null) Guard("plate test", () =>
        {
            var ui = testPlate._characterNameInstance;
            string original = ui.nameText.text;
            const string Mark = "··";
            ui.nameText.text = Mark;
            float t0 = Time.unscaledTime;
            void Check(float after, bool last)
            {
                ModRuntime.Later(after, () => Guard("plate test", () =>
                {
                    if (ui == null || ui.WasCollected) { P("plate test: the plate went away"); return; }
                    string now = ui.nameText.text;
                    if (now != Mark) { P($"plate test: the game wrote the text again within {Time.unscaledTime - t0:0.00} s ({(now == original ? "the same name" : "something else")})"); return; }
                    if (last) { ui.nameText.text = original; P("plate test: still ours after 3 s (the game writes it only now and then); put back"); }
                }));
            }
            Check(0.05f, false); Check(0.5f, false); Check(3f, true);
            // A safety net: whatever happened, the name is back after 3.2 s.
            ModRuntime.Later(3.2f, () => { try { if (ui != null && !ui.WasCollected && ui.nameText.text == Mark) ui.nameText.text = original; } catch { } });
        });
        else P("plate test: no plate on screen to test (stand near people and run it again)");

        Guard("customer overlay", () =>
        {
            int n = 0;
            foreach (var ar in Resources.FindObjectsOfTypeAll<PlayerCustomerARUi>())
            {
                if (ar == null || !Safe(() => ar.gameObject.scene.IsValid())) continue;
                var person = Safe(() => ar.agent?.Person ?? ar.agent?.initialPerson);
                string text = Safe(() => ar.nameText?.text) ?? "";
                bool on = Safe(() => ar.gameObject.activeInHierarchy && ar.nameCanvasParent != null && ar.nameCanvasParent.activeInHierarchy);
                if (n++ < 8) P($"overlay: {Who(person)} met={Met(person)} nameShown={on && text.Length > 0} visible={Safe(() => ar.ShouldBeVisible)}");
            }
            P($"overlay: {n} customer overlays in the world");
        });
        Guard("popup", () =>
        {
            var pop = Safe(() => CustomerPopup.instance);
            P($"popup: {(pop == null ? "none" : $"present, open={Safe(() => pop.gameObject.activeInHierarchy)}")}");
        });

        Native(typeof(CharacterNameDisplay), nameof(CharacterNameDisplay.TranslateName));
        Native(typeof(CharacterNameDisplay), nameof(CharacterNameDisplay.ShouldDisplayName), typeof(CharacterNameDisplay.UpdateInfo));
        Native(typeof(CharacterNameDisplay), nameof(CharacterNameDisplay.ShouldDisplayName), Type.EmptyTypes);
        Native(typeof(CharacterNameDisplay), nameof(CharacterNameDisplay.DoUpdate));
        Native(typeof(CharacterNameDisplay), nameof(CharacterNameDisplay.OnInit));
        Native(typeof(CharacterNameDisplay), nameof(CharacterNameDisplay.Init));
        Native(typeof(CharacterNameDisplay), nameof(CharacterNameDisplay.UpdateDisplayActive), typeof(bool));
        Native(typeof(PlayerCustomerARUi), nameof(PlayerCustomerARUi.SetCustomerName));
        Native(typeof(PlayerCustomerARUi), nameof(PlayerCustomerARUi.UpdateDisplay));
        Native(typeof(CustomerPopup), nameof(CustomerPopup.Show));
        Native(typeof(ChatRequestIndicator), nameof(ChatRequestIndicator.SetText));
        Native(typeof(RuntimePersonData), "set_HasBeenMet");
    }

    // ------------------------------------------------------------------ Ava's old lines

    /// <summary>Text as compared: StoryBook's cleaning, then only letters and digits, lower case (pronoun and markup tolerant).</summary>
    private static string Loose(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (char c in s) if (char.IsLetterOrDigit(c)) b.Append(char.ToLowerInvariant(c));
        return b.ToString();
    }

    private static void Ava()
    {
        P("--- ava ---");
        var book = StoryDialogue.Book;
        string? id = book?.IdNamed("Ava");
        if (book == null || id == null) { P("ava: not in your StoryBook yet"); return; }
        var seen = book.Scenes(id).SelectMany(s => s.Lines).Where(l => l.Who == StoryBook.Them).Select(l => StoryBook.Clean(l.Text)).Where(t => t.Length > 0).ToList();
        var seenExact = new HashSet<string>(seen, StringComparer.Ordinal);
        var seenLoose = new HashSet<string>(seen.Select(Loose), StringComparer.Ordinal);
        P($"ava: {seen.Count} of her lines in your StoryBook ({seenExact.Count} different)");

        Person? ava = null;
        foreach (var p in Resources.FindObjectsOfTypeAll<Person>())
            if (p != null && string.Equals(Safe(() => p.DisplayedName) ?? "", "Ava", StringComparison.OrdinalIgnoreCase)) { ava = p; break; }
        if (ava == null) { P("ava: no Person named Ava"); return; }

        var hist = GameContext.Existing<CharacterDialogueHistoryManager>();
        var trees = GameContext.Existing<DialogueTreeProgressManager>();
        P($"ava: history manager {(hist != null)}, tree manager {(trees != null)}, dialogue window {(DialogueUI._instance != null)}");
        if (hist == null || trees == null) return;

        var pageGuids = new HashSet<string>(StringComparer.Ordinal);
        var treeGuids = new List<string>();
        Guard("history", () =>
        {
            var data = hist.GetDataFor(ava);
            if (data == null) { P("history: none for Ava"); return; }
            foreach (var g in data.TreesGuids) if (!string.IsNullOrEmpty(g)) treeGuids.Add(g);
            int pages = 0, lines = 0;
            foreach (var page in data.Pages)
            {
                pages++;
                foreach (var line in page.Item2.Lines) { lines++; if (!string.IsNullOrEmpty(line.Item1)) pageGuids.Add(line.Item1); }
            }
            string sample = pageGuids.FirstOrDefault() ?? "";
            P($"history: {treeGuids.Count} conversations seen, {pages} pages, {lines} lines; a line's id looks like {(sample.Length == 0 ? "-" : $"'{sample}' (length {sample.Length})")}; a tree id like '{treeGuids.FirstOrDefault() ?? "-"}'");
        });

        var all = Safe(() => trees.AllDialogueTrees);
        P($"trees: {Safe(() => all?.Count) } in the game's registry");
        if (all == null) return;

        int resolved = 0, nodes = 0, exact = 0, loose = 0, idInPages = 0, matchedIdInPages = 0, voNamed = 0, prefixIsGuid = 0, prefixIsArticy = 0, indexPlusOne = 0, indexSame = 0;
        var voToLoad = new List<string>();
        foreach (var guid in treeGuids)
        {
            IDialogueTree? tree = null;
            Guard("tree", () => { if (all.ContainsKey(guid)) tree = all[guid]; });
            if (tree == null) continue;
            resolved++;
            var concrete = Safe(() => tree.TryCast<DialogueTree>());
            if (concrete == null) continue;
            var list = new Il2CppSystem.Collections.Generic.List<IDialogue>();
            Guard("lines", () => concrete.GetAllLines(list));
            string tg = Safe(() => tree.Guid) ?? "", ta = Safe(() => tree.ArticyGuid) ?? "";
            for (int i = 0; i < list.Count; i++)
            {
                var node = Safe(() => list[i]);
                if (node == null) continue;
                nodes++;
                string text = StoryBook.Clean(Safe(() => node.Text) ?? "");
                string nodeId = Safe(() => node.TryCast<DialogueWithChoices>()?.articyGuid) ?? ""; // a field of the node itself (an interface call on an object that isn't one would crash)
                bool inPages = nodeId.Length > 0 && pageGuids.Contains(nodeId);
                if (inPages) idInPages++;
                bool isExact = text.Length > 0 && seenExact.Contains(text);
                bool isLoose = !isExact && text.Length > 0 && seenLoose.Contains(Loose(text));
                if (!isExact && !isLoose) continue; // a line not in your StoryBook: counted above, nothing else read
                if (isExact) exact++; else loose++;
                if (inPages) matchedIdInPages++;
                string vo = Safe(() => node.VOFileName) ?? "";
                if (vo.Length == 0) continue;
                voNamed++;
                int close = vo.IndexOf(']');
                string prefix = vo.StartsWith("[Dlg_") && close > 5 ? vo.Substring(5, close - 5) : "";
                if (prefix.Length > 0 && tg.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0) prefixIsGuid++;
                if (prefix.Length > 0 && ta.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0) prefixIsArticy++;
                int us = vo.LastIndexOf('_');
                int voIndex = Safe(() => node.VOIndex);
                if (us > 0 && int.TryParse(vo.Substring(us + 1), out int suffix)) { if (suffix == voIndex + 1) indexPlusOne++; else if (suffix == voIndex) indexSame++; }
                if (voToLoad.Count < 3) voToLoad.Add(vo);
            }
        }
        P($"trees: {resolved} of {treeGuids.Count} seen conversations found; {nodes} lines in them");
        P($"match: {exact} of your StoryBook lines found exactly, {loose} more only loosely (pronouns/markup); {exact + loose} of {seenExact.Count}");
        P($"match: lines whose id is in the history pages: {idInPages} (of the matched: {matchedIdInPages}) -> {(idInPages > 0 ? "the history's ids are the lines' ArticyGUIDs" : "the history's ids aren't ArticyGUIDs")}");
        P($"voice: {voNamed} matched lines have a voice file; its [Dlg_x] is the tree's Guid for {prefixIsGuid}, its ArticyGuid for {prefixIsArticy}; its _NN is VOIndex+1 for {indexPlusOne}, VOIndex for {indexSame}");

        // The load test: no dialogue open (or one open), each way the game's clip might be had.
        foreach (var vo in voToLoad) Guard("load", () => LoadTest(vo, 0));
    }

    private static int _loadNo;

    private static void LoadTest(string vo, int _)
    {
        int no = ++_loadNo;
        AudioClip? viaResources = Safe(() => Resources.Load(vo, Il2CppType.Of<AudioClip>())?.TryCast<AudioClip>());
        AudioClip? viaLower = viaResources ?? Safe(() => Resources.Load(vo.ToLowerInvariant(), Il2CppType.Of<AudioClip>())?.TryCast<AudioClip>());
        var ui = DialogueUI._instance;
        AudioClip? viaUi = ui == null ? null : Safe(() => ui.GetLineAudioClip(vo));
        P($"load {no}: Resources.Load {(viaResources != null ? "found" : "nothing")}, lower case {(viaLower != null ? "found" : "nothing")}, the dialogue window's {(ui == null ? "(no window)" : viaUi != null ? "found" : "nothing")}");
        var clip = viaLower ?? viaUi;
        if (clip == null) return;
        P($"load {no}: state {Safe(() => clip.loadState)}, {Safe(() => clip.length):0.0} s, {Safe(() => clip.frequency)} Hz x{Safe(() => clip.channels)}, loadType {Safe(() => clip.loadType)}, preload {Safe(() => clip.preloadAudioData)}");
        if (Safe(() => clip.loadState) == AudioDataLoadState.Unloaded) P($"load {no}: LoadAudioData() -> {Safe(() => clip.LoadAudioData())}");
        float t0 = Time.unscaledTime;
        void Poll(int left)
        {
            ModRuntime.Later(0.25f, () =>
            {
                var st = Safe(() => clip.loadState);
                if (st == AudioDataLoadState.Loaded || st == AudioDataLoadState.Failed || left <= 0)
                {
                    bool data = false;
                    if (st == AudioDataLoadState.Loaded)
                        data = Safe(() => { var a = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>(Math.Max(1, Math.Min(clip.samples, 1024) * Math.Max(1, clip.channels))); return clip.GetData(a, 0); });
                    P($"load {no}: {st} after {Time.unscaledTime - t0:0.00} s; its samples can be read: {data}");
                    return;
                }
                Poll(left - 1);
            });
        }
        Poll(24);
    }

    // ------------------------------------------------------------------ FB-73: fishing

    private static void Fish()
    {
        P("--- fish ---");
        var pc = GameContext.PlayerCharacter();
        Vector3 pp = pc != null ? pc.transform.position : Vector3.zero;
        int scene = Safe(() => pc != null && pc.MyGhost != null ? pc.MyGhost.SceneIndex : -1);
        string place = "";
        Guard("place", () => { foreach (var wl in Resources.FindObjectsOfTypeAll<WorldLocation>()) if (wl != null && wl.SceneIndex == scene) { place = wl.DisplayName ?? ""; break; } });
        P($"you: {pp} scene {scene} '{place}'");

        Guard("manager", () =>
        {
            var fm = GameContext.Existing<FishingManager>();
            P(fm == null ? "manager: none" : $"manager: throw range {Safe(() => fm.SkillThrowRange):0.0}, scanner {Safe(() => fm.SkillScannerRadius):0.0}, luck {Safe(() => fm.SkillLuck):0.00}, database {(Safe(() => fm.Database) != null)}");
        });

        Guard("spots", () =>
        {
            int n = 0;
            var near = new List<(float d, string s)>();
            foreach (var s in Resources.FindObjectsOfTypeAll<FishingSpot>())
            {
                if (s == null || !Safe(() => s.gameObject.scene.IsValid())) continue;
                n++;
                Vector3 at = s.transform.position;
                near.Add(((at - pp).magnitude, $"{at} scene {s.gameObject.scene.buildIndex} item {Safe(() => s.Item?.name) ?? "-"} radius {Safe(() => s.Radius):0.0} collected {Safe(() => s.Collected)} active {s.gameObject.activeInHierarchy}"));
            }
            P($"spots: {n} in the loaded scenes");
            foreach (var (d, s) in near.OrderBy(x => x.d).Take(12)) P($"spot {d:0} m: {s}");
        });

        Guard("poses", () =>
        {
            int n = 0;
            foreach (var ia in Resources.FindObjectsOfTypeAll<IdleAction>())
            {
                if (ia == null) continue;
                var anim = Safe(() => ia.Animation);
                if (anim != Character.AnimatorStateType.Fishing && anim != Character.AnimatorStateType.SitPier) continue;
                if (n++ < 12) P($"pose: '{ia.name}' {anim} snapItem '{Safe(() => ia.snapItem?.name) ?? "-"}' matchSnap {Safe(() => ia.matchSnap)} in a scene {Safe(() => ia.GetInstanceID() != 0)}");
            }
            P($"poses: {n} idle actions that fish or sit on a pier");
        });

        Guard("fishing now", () =>
        {
            int n = 0;
            foreach (var c in Character.instances)
            {
                if (c == null || !c.isActiveAndEnabled) continue;
                var cur = Safe(() => c.state?.current);
                var ia = Safe(() => cur?.TryCast<IdleAction>());
                if (ia == null) continue;
                var anim = Safe(() => ia.Animation);
                if (anim != Character.AnimatorStateType.Fishing && anim != Character.AnimatorStateType.SitPier) continue;
                var person = Safe(() => c.Agent?.Person ?? c.Agent?.initialPerson);
                Vector3 at = c.transform.position;
                string ghost = Safe(() => c.Agent?.MyGhost?.CurrentAction?.Type?.name) ?? "";
                if (n++ < 12) P($"resident {anim}: {Who(person)} at {at} ({(at - pp).magnitude:0} m) action '{cur!.name}' ghost '{ghost}' scene {c.gameObject.scene.buildIndex}");
            }
            P($"fishing now: {n} residents fishing or on a pier in the loaded scenes");
        });

        Guard("rods", () =>
        {
            int n = 0;
            foreach (var r in Resources.FindObjectsOfTypeAll<FishingRodProp>())
            {
                if (r == null || !Safe(() => r.gameObject.scene.IsValid())) continue;
                var holder = Safe(() => r.GetComponentInParent<Character>());
                if (n++ < 10) P($"rod prop at {r.transform.position} held by {(holder == null ? "nobody" : Who(Safe(() => holder.Agent?.Person ?? holder.Agent?.initialPerson)))} active {r.gameObject.activeInHierarchy} parent '{Safe(() => r.transform.parent?.name) ?? "-"}'");
            }
            P($"rods: {n} rod props in the loaded scenes");
        });

        Guard("water", () =>
        {
            int mask = 0;
            foreach (var rod in Resources.FindObjectsOfTypeAll<PlayerFishingRod>()) { if (rod != null) { mask = Safe(() => rod.waterLayer.value); break; } }
            if (mask == 0) { int w = LayerMask.NameToLayer("Water"); if (w >= 0) mask = 1 << w; }
            P($"water: layer mask {mask}");
            if (mask == 0) return;
            var hits = Physics.OverlapSphere(pp, 80f, mask, QueryTriggerInteraction.Collide);
            P($"water: {hits.Length} water colliders within 80 m");
            foreach (var h in hits.Take(10)) P($"water: '{h.name}' centre {h.bounds.center} size {h.bounds.size} ({(h.ClosestPoint(pp) - pp).magnitude:0} m to its edge)");
        });

        Native(typeof(FishingManager), nameof(FishingManager.Collect));
        Native(typeof(FishingManager), nameof(FishingManager.Decide));
        Native(typeof(PlayerFishingRod), nameof(PlayerFishingRod.Decide));
        Native(typeof(FishDatabase), nameof(FishDatabase.RegisterCatch));
        Native(typeof(FishingEquipment), nameof(FishingEquipment.OnHeldStateChanged));
    }

    // ------------------------------------------------------------------ CF-20: where you are, for plans

    private static void Place()
    {
        P("--- place ---");
        var pc = GameContext.PlayerCharacter();
        Vector3 pp = pc != null ? pc.transform.position : Vector3.zero;
        P($"place: names for here: {string.Join(" | ", GameContext.PlacesHere())} (you at {pp})");
        var rows = new List<(float d, string s)>();
        foreach (var a in GameContext.VenueAreas())
        {
            if (a == null || a.WasCollected) continue;
            float d2 = Safe(() => a.GetDistanceToSqr(pp));
            float centre = (a.transform.position - pp).magnitude;
            rows.Add((d2, $"'{GameContext.AreaName(a)}' game distance {Mathf.Sqrt(Math.Max(0, d2)):0.0} m, its centre {centre:0} m"));
        }
        P($"place: {rows.Count} venue areas in the loaded scenes");
        foreach (var (_, s) in rows.OrderBy(r => r.d).Take(8)) P("place: " + s);
    }

    // ------------------------------------------------------------------ the watch

    private static Il2CppSystem.Action<RuntimePersonData, Change<bool>>? _met;
    private static Il2CppSystem.Action<ItemType>? _collected, _unlocked;
    private static FishingManager? _fm;
    private static FishDatabase? _db;
    private static int _watchNo;

    private static void Watch()
    {
        StopWatch("started again");
        int no = ++_watchNo;
        Guard("watch met", () =>
        {
            _met = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<RuntimePersonData, Change<bool>>>(new Action<RuntimePersonData, Change<bool>>((rd, ch) =>
                Guard("met", () =>
                {
                    var tree = GameContext.Existing<DialogueTreeProgressManager>();
                    bool talking = Safe(() => tree != null && tree.IsInDialogue);
                    P($"watch: {Who(Safe(() => rd.ForPerson))} became met={Safe(() => rd.HasBeenMet)} (in a dialogue: {talking}, the mod's chat open: {ModRuntime.ChatOpen})");
                })));
            RuntimePersonData.add_OnHasBeenMetChanged(_met);
        });
        Guard("watch fish", () =>
        {
            _fm = GameContext.Existing<FishingManager>();
            if (_fm != null)
            {
                _collected = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ItemType>>(new Action<ItemType>(it => P($"watch: fishing collected '{Safe(() => it.name)}'")));
                _fm.add_OnItemCollected(_collected);
                _db = Safe(() => _fm.Database);
                if (_db != null)
                {
                    _unlocked = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ItemType>>(new Action<ItemType>(it => P($"watch: a new fish unlocked '{Safe(() => it.name)}'")));
                    _db.add_onFishUnlocked(_unlocked);
                }
            }
        });
        P($"watch: on for 30 minutes (met {(_met != null)}, fishing {(_collected != null)}, new fish {(_unlocked != null)})");
        ModRuntime.Later(1800f, () => { if (no == _watchNo) StopWatch("30 minutes"); });
    }

    public static void StopWatch(string why)
    {
        if (_met == null && _collected == null && _unlocked == null) return;
        try { if (_met != null) RuntimePersonData.remove_OnHasBeenMetChanged(_met); } catch { }
        try { if (_collected != null && _fm != null && !_fm.WasCollected) _fm.remove_OnItemCollected(_collected); } catch { }
        try { if (_unlocked != null && _db != null) _db.remove_onFishUnlocked(_unlocked); } catch { }
        _met = null; _collected = null; _unlocked = null; _fm = null; _db = null;
        P($"watch: off ({why})");
    }
}
