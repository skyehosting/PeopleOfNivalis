using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NivalisAIChat.Voice;

namespace NivalisAIChat.Tests;

/// <summary>The 2026-10-20 audit's fixes that live in the game layer (checked from the source; the rest have their own tests).</summary>
internal static partial class Program
{
    private static void Audit1020Tests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- the 2026-10-20 audit's fixes");
        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string probe = File.ReadAllText(Path.Combine(src, "Dev", "CapabilityProbe.cs"));
        string main = File.ReadAllText(Path.Combine(src, "ModRuntime.cs"));
        Check(main.Contains("Dev.CapabilityProbe.StopAll(\"the mod is stopping\")") && probe.Contains("public static void StopAll(string why)")
              && probe.Contains("try { StopEvents(why); } catch { }") && probe.Contains("try { ResearchProbe.StopWatch(why); } catch { }"),
              "a stop or hot reload ends every probe's game-event hooks (not only /probe watch)");
        Check(probe.Contains("try { EndWalk(why); } catch { }") && probe.Contains("try { EndSchedule(why); } catch { }")
              && probe.Contains("if (s.noSave != null && !s.noSave.IsReleased) s.noSave.Release();") && probe.Contains("w.who.ChangeWalkWeight(w.before, 0.5f)"),
              "...and puts back a probe's sad walk, schedule entry and save block, even when its own timer never runs");
        Check(probe.Contains("if (a == \"stop\") { StopAll(\"stopped\");") && probe.Contains("EndWalk(\"restarted\");") && probe.Contains("EndSchedule(\"restarted\");"),
              "/probe stop does the same; starting one again first puts back the last");
        Check(probe.Contains("if (t == 20 && _walkRun == run) EndWalk(\"done\");") && probe.Contains("if (t == 25 && _scheduleRun == run) EndSchedule(\"done\");"),
              "review R3: an earlier probe run's timer never ends a newer one");

        string phone = File.ReadAllText(Path.Combine(src, "ModRuntime.Phone.cs"));
        int tick = phone.IndexOf("private static void TickTexts()", StringComparison.Ordinal);
        Check(tick > 0 && phone.IndexOf("if (book == null || !Features.Phone) return;", tick, 400, StringComparison.Ordinal) > 0
              && phone.IndexOf("TickPlans(book);", tick, StringComparison.Ordinal) > tick,
              "the phone switched off: no texts sent, delivered or answered (so nothing is spent), and it goes on when it's back");
        Check(phone.Contains("if (!_phoneOpen && Features.Phone) // an answer already written as the phone was switched off is kept, quietly"),
              "...and an answer already on its way when it went off is kept, with no card, pip or chime");

        // Voices: a bare 403 (no access, a blocked region) sets that provider aside like a bad key; a 403 about one voice still
        // swaps only that voice; and every voice error has the provider's own key hidden, whatever its shape.
        Check(VoiceService.AccountDown("HTTP 403: {\"error\":{\"code\":\"unsupported_country_region_territory\"}}") && VoiceService.AccountDown("HTTP 401: invalid api key")
              && !VoiceService.AccountDown("HTTP 404: {\"detail\":{\"status\":\"voice_not_found\"}}"),
              "a 403 with nothing about a voice: the provider can't be used just now (another one speaks)");
        Check(VoiceService.VoiceRefused("HTTP 403: {\"detail\":{\"status\":\"only_for_creator+\",\"message\":\"Professional voices need Creator\"}}"),
              "...while a 403 about one voice still only swaps that voice (decided first)");
        Check(!VoiceService.AccountDown("HTTP 403: {\"detail\":{\"status\":\"only_for_creator+\",\"message\":\"Professional voices need Creator\"}}"),
              "review R1: ...and on a retry with a stand-in voice (no voice check then), a 403 about one voice never sets the provider aside");
        string voice = File.ReadAllText(Path.Combine(src, "Voice", "VoiceService.cs"));
        Check(voice.Contains("{ failed = Ai.Redact.Text(Ai.Redact.Hide(e.Message, slot.ApiKey)); }") && voice.Contains("AccountDown(failed) && (LongOutage(failed)"),
              "a voice error is logged and shown with its own key hidden (BytesAsync's echo of a request included)");

        string feed = File.ReadAllText(Path.Combine(src, "UI", "PhoneWindow.Nightline.cs"));
        Check(feed.Contains("if (onScreen.Contains(old)) { i++; continue; }") && !feed.Contains("var old = _photoOrder[0]; _photoOrder.RemoveAt(0);"),
              "Nightline never lets go of a photo that's on screen (past 40 that was a disk read and redraw every frame)");

        string ctx = File.ReadAllText(Path.Combine(src, "Game", "GameContext.cs"));
        int run = ctx.IndexOf("public static float TypicalRunParam(", StringComparison.Ordinal);
        Check(run > 0 && ctx.IndexOf("if (n >= 12) break;", run, StringComparison.Ordinal) is > 0 and var at && at - run < 1200
              && ctx.Contains("if (_runParam <= 0f && now - _runParamAt < 5f) return walkParam * 2.2f;"),
              "the running value stops at 12 runners, and with nobody running looks again every 5 s (not on every call)");

        string follow = File.ReadAllText(Path.Combine(src, "ModRuntime.Follow.cs"));
        int same = follow.IndexOf("private static bool SameGhost(Follower f)", StringComparison.Ordinal);
        Check(same > 0 && follow.IndexOf("if (g == null) return f.GhostPtr == IntPtr.Zero || GameContext.NpcId(f.C) is var now && (now == f.Id || !now.StartsWith(\"person_\", StringComparison.Ordinal));", same, StringComparison.Ordinal) is > 0 and var gn && gn - same < 700,
              "FB-78: a follower whose body has no ghost (a background person) still follows while it's alive; only a body given to another ghost is lost");
        string romance = File.ReadAllText(Path.Combine(src, "ModRuntime.Romance.cs"));
        string plans = File.ReadAllText(Path.Combine(src, "ModRuntime.Plans.cs"));
        Check(romance.Contains("d != _date.Day && (d < _date.Day || GameContext.Hour() >= 6)) EndDate(\"it's a new day\")")
              && plans.Contains("|| Visitors.Any(v => v.Id == r.WhoId && v.Greeted)) { Settle(r, Meetups.Met, persona); continue; }"),
              "review DAY-3: a date runs on past midnight (ends on a later day from 06:00); OWN-1: someone keeping you company has met you");
        string nlf = File.ReadAllText(Path.Combine(src, "ModRuntime.Nightline.cs"));
        Check(nlf.Contains("while (FaceOrder.Count >= FacesCached) DropFace(FaceOrder[0]);") && !nlf.Contains("if (FaceSprites.Count >= FacesCached) FreeFaceSprites();")
              && nlf.Contains("!FaceLoading.Contains(item.id)") && nlf.Contains("if (_faceRefreshAt < 0) _faceRefreshAt = Time.unscaledTime + 1f;"),
              "review PH-5: faces go least-lately-shown first (never all at once), a forgotten one's read is dropped, the feed redraws at most once a second");
        // FB-81 (the author: "move the "controls" out of the advanced sub tab so it sits next to usage optimizer and the others? And label
        // it keybinds? ... a keybinding for all the keys related with people of nivalis").
        string cfgK = File.ReadAllText(Path.Combine(src, "ModConfig.cs")), setK = File.ReadAllText(Path.Combine(src, "ModRuntime.Settings.cs"));
        int kb0 = cfgK.IndexOf("Section(\"Keybinds\"", StringComparison.Ordinal), kb1 = cfgK.IndexOf("Section(\"You\"", kb0, StringComparison.Ordinal);
        string kbs = kb0 > 0 && kb1 > kb0 ? cfgK[kb0..kb1] : "";
        Check(new[] { "TalkKey", "ConsoleKey", "WorldChatKey", "PhoneKey", "WorldChatNextKey", "CameraShutterKey", "VoiceInputKey", "PadTalkButton", "PadConsoleButton", "PadVoiceButton" }
                  .All(k => kbs.Contains($"Bind(\"{k}\"")) && !kbs.Contains("Bind(\"MaxTalkDistance\""),
              "FB-81: every key and button the mod listens for is in [Keybinds] (world chat's next-person and the Camera's shutter keys too)");
        Check(cfgK.Contains("[\"Keybinds\"] = new[] { \"Controls\", \"Behaviour\", \"Voice Input\" }") && cfgK.Contains("\"Controls/MaxTalkDistance\"")
              && File.ReadAllText(Path.Combine(src, "SettingsHistory.cs")).Contains("(\"Voice Input\", \"VoiceInputKey\")")
              && cfgK.Contains("if (SettingsHistory.Retired.Contains((l.section, l.name))) continue;"),
              "...your keys come over from [Controls] and [Voice Input] once; the three that aren't keys move to [Conversations] with their values; old lines are noted, never warned about");
        Check(setK.Contains("else if (name == KeybindsGroup) keybindsIntro = intro;") && setK.Contains("[\"name\"] = KeybindsGroup, [\"intro\"] = keybindsIntro"),
              "...and the Keybinds tab sits with the Wizard, the Usage Optimizer and Features, not under Advanced");
        Check(cfgK.Contains("return sections.Any(s => IsOlderGroup(s) && s != \"Controls\");") && cfgK.Contains("&& _upgrades.Once($\"{section}.{key}=from {k}\")")
              && setK.Contains("if (!NonTypingKey(k)) return") && File.ReadAllText(Path.Combine(src, "ModRuntime.Camera.cs")).Contains("if (key == Key.Escape || key == _phoneKey || key == _talkKey || key == _consoleKey)"),
              "review F81-1..4: the left-behind [Controls] doesn't stop the explanations' refresh; a moved value is carried once; the next-person key never types; no key clashes with talk, console or the phone");
        // FB-78 (the author: "theres no portraits of the npc's profiles").
        string nl = File.ReadAllText(Path.Combine(src, "ModRuntime.Nightline.cs"));
        string ph = File.ReadAllText(Path.Combine(src, "ModRuntime.Phone.cs"));
        Check(nl.Contains("return PortraitOf(id, model) ?? FaceSprite(id);") && nl.Contains("m = ActiveBrain.Store.Glance(id)?.Visual?.Model ?? \"\";")
              && ph.Contains("_portraitsByName.TryGetValue(model + \"_portrait\", out found)"),
              "FB-78: everyone on Nightline gets their picture: the game's portrait by their model (\"female015\" as \"female015_portrait\"), else their kept face");
        Check(nl.Contains("if (id == LineBook.You || KnownUnder18(id)) return null;") && nl.Contains("bool hasFace = _cfg.LineSelfInPhotos.Value && File.Exists(FacePath(id));")
              && nl.Contains("FaceSprites.Remove(id, out var s)") && nl.Contains("PortraitMisses.Add(model)") == false && ph.Contains("PortraitMisses.Add(model);"),
              "...never a child's; a face kept for a picture isn't used in photos unless LineSelfInPhotos; forgotten with them; a missing portrait isn't searched for again and again");
    }
}
