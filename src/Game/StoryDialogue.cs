using System;
using System.Collections.Generic;
using Nivalis.Dialogue;
using Nivalis.GhostSystem.Ai;
using Nivalis.UI;
using NivalisAIChat.Npc;
using UnityEngine;

namespace NivalisAIChat.Game;

/// <summary>
/// FB-50: the game's story conversations, kept as the player sees them (<see cref="StoryBook"/>). Two ways in, both showing
/// only what the player has seen, so nothing can spoil a part of the story they haven't reached:
/// - live: each line the game's dialogue window shows (DialogueUI.DisplayLine) and the choice the player picks (the node
///   that showed the choices, read when the next line comes or the dialogue ends);
/// - the game's own chat history page in the Character window (CharacterWindowWidgetChatHistory.ShowPage): each page the
///   player looks at is read from the bubbles it shows, for conversations from before this was kept.
/// Only the game's big methods are hooked (never a small setter that could share its native body). Under-18 characters:
/// nothing of theirs is kept, as a partner or as a speaker in someone else's scene. Main thread only.
/// </summary>
internal static class StoryDialogue
{
    /// <summary>Set by ModRuntime: where it's kept (null: nothing kept), and what it needs from the rest of the mod.</summary>
    public static StoryBook? Book;
    public static Func<string> PlayerName = () => "";
    public static Func<string, bool> KnownMinor = _ => false;
    public static Func<(int day, string clock)> Now = () => (0, "");
    /// <summary>A story conversation finished with lines kept: id, name, lines.</summary>
    public static Action<string, string, int>? Ended;
    /// <summary>
    /// The phone (Ava): a line of theirs as it's shown, with a way to get its recording ("" none). Only what the game shows
    /// right now: never a line from its files the player hasn't reached.
    /// </summary>
    public static Action<string, string, string, Func<AudioClip?>>? Voiced;
    /// <summary>Runs something a moment later on the main thread (the chat history page builds its bubbles).</summary>
    public static Action<float, Action>? Later;

    // The conversation being kept now.
    private static string? _id;
    private static string _name = "";
    private static bool _childScene;
    private static (int day, string clock) _when;
    private static DialogueStepDisplayData? _choiceNode;
    // Its choice index when the choices were shown: a dialogue that ends without the index changing had no pick (FB50-2).
    private static int _choiceBefore;
    private static string _lastChoice = "";

    private static T? Read<T>(Func<T> get) { try { return get(); } catch { return default; } }

    private static string NameOf(Person p) => Read(() => p.DisplayedName) ?? "";

    private static bool Same(Person a, Person b)
    {
        try { return a.Pointer == b.Pointer || GameContext.PersonId(a) == GameContext.PersonId(b); } catch { return false; }
    }

    private static bool Child(Person p) =>
        Minors.IsStoryChild(Read(() => p.Guid), NameOf(p), Read(() => p.Description)) || KnownMinor(GameContext.PersonId(p));

    // The player's name, read once per conversation or page (the game calls an unnamed protagonist "You").
    private static string _player = "";

    private static bool IsPlayer(string name)
    {
        string n = name.Trim();
        return n.Length > 0 && (n.Equals("You", StringComparison.OrdinalIgnoreCase) || n.Equals("Player", StringComparison.OrdinalIgnoreCase)
                                || (_player.Length > 0 && n.Equals(_player, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The conversation is with <paramref name="partner"/> (a story character): opens it if it's a new one.</summary>
    private static bool Follow(Person partner)
    {
        string id = GameContext.PersonId(partner);
        if (_id == id) return !_childScene;
        Finish();
        _id = id;
        _name = NameOf(partner);
        _childScene = Child(partner);
        _when = Now();
        _player = Read(() => PlayerName()) ?? "";
        _lastChoice = "";
        // FB-51: one line per story conversation, so a test can tell the hooks are seen (the end says how many lines were kept).
        Log.Info(_childScene ? $"Story: a conversation with {(_name.Length > 0 ? _name : "someone")} isn't kept (under 18)."
                             : $"Story: keeping your conversation with {(_name.Length > 0 ? _name : "them")}.");
        return !_childScene;
    }

    // ------------------------------------------------------------------ FB-53: the player's picks, from the choice button

    // The pick wasn't read from the node afterwards in game (no line of yours was kept). The choice button pressed knows its
    // own choice (DialogueChoiceUI._choice): read just before it acts. (The dialogue window's "choice made" event was tried
    // first: it hands its arguments over as a struct, which the bridge can misread: review of 0.4.4.)
    private static void Picked(DialogueChoiceUI ui)
    {
        if (!Features.StoryMemory || Book == null || ui == null) return;
        // Only a choice button: if this body were shared with another method, the fields below wouldn't be this class's.
        try { if (ui.GetIl2CppType()?.Name != "DialogueChoiceUI") return; } catch { return; }
        string text = StoryBook.Clean(Read(() => ui._choice?.Text));
        if (text.Length == 0) return;
        var step = Read(() => DialogueUI._instance?._currentStepData);
        var partner = step != null ? Read(() => step.Character) ?? Read(() => step.Speaker) : null;
        if (partner == null) partner = Read(() => DialogueUI._instance?._currentSpeaker);
        if (partner == null || !GameContext.IsNamed(partner) || !Follow(partner)) return;
        _lastChoice = text;
        _choiceNode = null; // taken here: not again from the node
        Book.Add(_id!, _name, StoryBook.You, text, step != null && Read(() => step.IsMessage), _when.day, _when.clock);
        if (!_saidPicks) { _saidPicks = true; Log.Info("Story: your choices in the game's dialogue are kept too."); }
    }

    private static bool _saidPicks;

    /// <summary>A line the dialogue window shows.</summary>
    internal static void Line(DialogueStepDisplayData d, DialogueUI? ui = null)
    {
        if (!Features.StoryMemory || Book == null || d == null) return;
        // The next line after the choices: the player picked one (a line shown for the choices' own node comes before any pick).
        if (_choiceNode != null && !SameNode(d, _choiceNode)) TakeChoice(continued: true);
        var speaker = Read(() => d.Speaker);
        var partner = Read(() => d.Character) ?? speaker;
        if (partner == null || !GameContext.IsNamed(partner) || !Follow(partner)) return;
        string text = StoryBook.Clean(Read(() => d.Text));
        if (text.Length == 0) return;
        string who;
        if (speaker == null || Same(speaker, partner)) who = StoryBook.Them;
        else
        {
            string name = NameOf(speaker);
            if (text == _lastChoice) return; // the player's own pick, shown back: kept once, as the choice
            if (IsPlayer(name)) who = StoryBook.You;
            else if (Child(speaker)) return; // a child's line in someone else's scene isn't kept
            else who = name.Length > 0 ? name : "Someone";
        }
        if (Book.Add(_id!, _name, who, text, Read(() => d.IsMessage), _when.day, _when.clock) && who != StoryBook.You) _lastChoice = ""; // the same pick may come again later
        if (who == StoryBook.Them && Voiced != null && ui != null)
        {
            string vo = Read(() => d.VOFileName) ?? "";
            // The clip is asked for again over 3 s while it loads: never from a window that's gone (review R82-1).
            if (vo.Length > 0) try { Voiced(_id!, _name, text, () => ui == null || ui.WasCollected ? null : ui.GetLineAudioClip(vo)); } catch (Exception e) { Log.Debug("story voice: " + e.Message); }
        }
    }

    /// <summary>The window shows choices for this node: what the player picks is read when the next line comes.</summary>
    internal static void Choices(DialogueStepDisplayData d)
    {
        if (!Features.StoryMemory || Book == null || d == null) return;
        _choiceNode = d;
        _choiceBefore = Read(() => d.lastChoiceIndex);
    }

    private static bool SameNode(DialogueStepDisplayData a, DialogueStepDisplayData b)
    {
        try { return a.Pointer == b.Pointer; } catch { return false; }
    }

    /// <summary>
    /// The choice the player picked on the node that showed choices, kept as their line. <paramref name="continued"/>: the
    /// dialogue went on, so a pick was made; at the end, only when the index changed since the choices were shown (FB50-2).
    /// </summary>
    private static void TakeChoice(bool continued)
    {
        var node = _choiceNode;
        _choiceNode = null;
        if (node == null || Book == null) return;
        try
        {
            var partner = Read(() => node.Character) ?? Read(() => node.Speaker);
            if (partner == null || !GameContext.IsNamed(partner) || !Follow(partner)) return;
            var choices = node.Choices;
            int pick = node.lastChoiceIndex;
            if (choices == null || pick < 0 || pick >= choices.Count) return;
            if (!continued && pick == _choiceBefore) return; // ended at the choices without a pick that can be told apart
            string text = StoryBook.Clean(choices[pick]?.Text);
            if (text.Length == 0 || text == _lastChoice) return; // kept already, from the game's own event
            _lastChoice = text;
            Book.Add(_id!, _name, StoryBook.You, text, Read(() => node.IsMessage), _when.day, _when.clock);
        }
        catch (Exception e) { Log.Debug("story choice: " + e.Message); }
    }

    /// <summary>The dialogue window closed: the conversation is kept.</summary>
    internal static void End()
    {
        TakeChoice(continued: false);
        Finish();
    }

    private static void Finish()
    {
        string? id = _id;
        bool child = _childScene;
        _id = null;
        _childScene = false;
        _choiceNode = null;
        if (id == null || child || Book == null) return;
        int kept = Book.End(id);
        if (kept <= 0) return;
        Log.Info($"Story: kept {kept} line{(kept == 1 ? "" : "s")} of your conversation with {(_name.Length > 0 ? _name : "them")}.");
        try { Ended?.Invoke(id, _name, kept); } catch (Exception e) { Log.Debug("story ended: " + e.Message); }
    }

    // ------------------------------------------------------------------ the game's chat history page

    /// <summary>A page of the Character window's chat history was shown: its bubbles are read (now, and once more shortly).</summary>
    internal static void Page(CharacterWindowWidgetChatHistory w, int index)
    {
        if (!Features.StoryMemory || Book == null || w == null) return;
        if (!ReadPage(w, index)) Later?.Invoke(0.25f, () => { try { if (w != null && !w.WasCollected) ReadPage(w, index); } catch { } });
    }

    /// <summary>True when the page had bubbles to read (whether or not they were new).</summary>
    private static bool ReadPage(CharacterWindowWidgetChatHistory w, int index)
    {
        var person = Read(() => w._person);
        if (person == null || !GameContext.IsNamed(person) || Child(person)) return true;
        var bubbles = Read(() => w._chatBubbleInstances);
        if (bubbles == null || bubbles.Count == 0) return false;
        string name = NameOf(person);
        _player = Read(() => PlayerName()) ?? "";
        int day = 0;
        try { var pages = w._history?.Pages; if (pages != null && index >= 0 && index < pages.Count) day = pages[index].Item1; } catch { }
        var lines = new List<StoryBook.Line>();
        for (int i = 0; i < bubbles.Count; i++)
        {
            var go = bubbles[i];
            if (go == null) continue;
            var chat = Read(() => go.GetComponent<CharacterWindowChatBubble>());
            if (chat != null)
            {
                string bubble = Read(() => chat.nameText?.text) ?? "";
                string text = Read(() => chat.lineText?.text) ?? "";
                // Only theirs and yours: anyone else here is just a name, and a name can't be checked for age (review FB50-1).
                if (StoryBook.WhoOnPage(bubble, name, IsPlayer(bubble)) is { } who) lines.Add(new StoryBook.Line { Who = who, Text = text });
                continue;
            }
            var choice = Read(() => go.GetComponent<CharacterWindowChoiceBubble>());
            if (choice != null) lines.Add(new StoryBook.Line { Who = StoryBook.You, Text = Read(() => choice.lineText?.text) ?? "" });
        }
        if (lines.Count == 0) return true;
        string id = GameContext.PersonId(person);
        if (Book!.ImportPage(id, name, day, lines))
            Log.Info($"Story: read a page of your story with {name}{(day > 0 ? $" (day {day})" : "")} from the game's chat history ({lines.Count} lines).");
        return true;
    }

    // ------------------------------------------------------------------ hooks (each one on its own: a game update loses only that one)

    [HarmonyLib.HarmonyPatch(typeof(DialogueUI), nameof(DialogueUI.DisplayLine))]
    private static class SeeLine
    {
        private static void Postfix(DialogueUI __instance, DialogueStepDisplayData __0) { try { Line(__0, __instance); } catch (Exception e) { Log.Debug("story line: " + e.Message); } }
    }

    [HarmonyLib.HarmonyPatch(typeof(DialogueUI), nameof(DialogueUI.DisplayChoices))]
    private static class SeeChoices
    {
        private static void Postfix(DialogueStepDisplayData __0) { try { Choices(__0); } catch (Exception e) { Log.Debug("story choices: " + e.Message); } }
    }

    [HarmonyLib.HarmonyPatch(typeof(DialogueUI), nameof(DialogueUI.OnDialogueEnd))]
    private static class SeeEnd
    {
        private static void Postfix() { try { End(); } catch (Exception e) { Log.Debug("story end: " + e.Message); } }
    }

    [HarmonyLib.HarmonyPatch(typeof(DialogueChoiceUI), nameof(DialogueChoiceUI.OnChoiceSelected))]
    private static class SeePick
    {
        private static void Prefix(DialogueChoiceUI __instance) { try { Picked(__instance); } catch (Exception e) { Log.Debug("story pick: " + e.Message); } }
    }

    [HarmonyLib.HarmonyPatch(typeof(CharacterWindowWidgetChatHistory), nameof(CharacterWindowWidgetChatHistory.ShowPage))]
    private static class SeePage
    {
        private static void Postfix(CharacterWindowWidgetChatHistory __instance, int __0) { try { Page(__instance, __0); } catch (Exception e) { Log.Debug("story page: " + e.Message); } }
    }
}
