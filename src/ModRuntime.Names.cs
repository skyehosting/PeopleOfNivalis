using System;
using System.Collections.Generic;
using Nivalis;
using Nivalis.GhostSystem;
using Nivalis.GhostSystem.Ai;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using UnityEngine;

namespace NivalisAIChat;

/// <summary>
/// CF-19 (a player: "you are able to see customer names that stop by your shops ... before you get to know them"; the
/// author: "blank is fine ... story npc's and shop npcs and such should still have their base game names above"): the
/// name over a customer at your place and in the customer pop-up is left blank until you know it: they told you
/// (NameRevealed), the game has you met (HasBeenMet), or they're the story cast. Display only: nothing of the game's is
/// changed but the text shown. The name comes back by itself once you know it. HideUnmetCustomerNames.
/// Hooks (each on its own native body, checked in the game 2026-10-19: docs/research/probe-2026-10-19.md):
/// PlayerCustomerARUi.SetCustomerName (prefix) and CustomerPopup.Show (postfix).
/// </summary>
public static partial class ModRuntime
{
    /// <summary>Overlays whose name was blanked: put back once it's known (looked at every two seconds; dead ones dropped).</summary>
    private static readonly Dictionary<IntPtr, (PlayerCustomerARUi ui, Person? person, string name)> BlankedNames = new();
    private static float _nextNamesLook;

    internal static bool NameHidden(Person? person)
    {
        if (person == null || _cfg == null || !_cfg.HideUnmetCustomerNames.Value) return false;
        try
        {
            if (GameContext.IsNamed(person)) return false;
            bool met = false;
            try { met = person.RuntimeData != null && person.RuntimeData.HasBeenMet; } catch { }
            return Social.NameRules.HideCustomerName(true, false, met, ToldYouTheirName(GameContext.PersonId(person)));
        }
        catch { return false; }
    }

    /// <summary>The talk prompt's cache of names they've told you, filled from their record on a miss (one glance, no waiting).</summary>
    private static bool ToldYouTheirName(string id)
    {
        if (KnownNames.TryGetValue(id, out var known)) return known.Length > 0;
        NpcRecord? rec = null;
        try { rec = ActiveBrain.Store.Glance(id); } catch { }
        bool told = rec != null && rec.State.NameRevealed;
        if (KnownNames.Count > 500) KnownNames.Clear();
        KnownNames[id] = told ? Prompts.FirstName(DisplayName(rec!)) : ""; // NoteKnownName keeps it current from here
        return told;
    }

    /// <summary>Every couple of seconds from the update: a blanked name that's now known is shown again.</summary>
    private static void TickCustomerNames()
    {
        if (BlankedNames.Count == 0 || Time.unscaledTime < _nextNamesLook) return;
        _nextNamesLook = Time.unscaledTime + 2f;
        List<IntPtr>? done = null;
        foreach (var (key, (ui, person, name)) in BlankedNames)
        {
            bool gone = true;
            // A pooled overlay given to someone else since: theirs isn't this name (only the same person's is put back).
            // Review R4: blanked before the game had given it its customer: that customer is the one, once they're there.
            Person? now = null;
            try { now = ui == null || ui.WasCollected ? null : PersonOf(ui.agent); } catch { }
            try { gone = ui == null || ui.WasCollected || (person != null && (person.WasCollected || now?.Pointer != person.Pointer)); } catch { }
            if (!gone && (now == null || NameHidden(now))) continue;
            if (!gone) try { var label = ui!.nameText; if (label != null && label.text.Length == 0) label.text = name; } catch { }
            (done ??= new()).Add(key);
        }
        if (done != null) foreach (var k in done) BlankedNames.Remove(k);
    }

    private static Person? PersonOf(Agent? agent)
    {
        try { return agent == null ? null : agent.Person ?? agent.initialPerson; } catch { return null; }
    }

    /// <summary>NameHooks: the overlay's name is about to be set (blank it and remember it, or forget a blank no longer needed).</summary>
    internal static bool BlankOverlayName(PlayerCustomerARUi ui, string name)
    {
        IntPtr key = ui.Pointer;
        var person = PersonOf(ui.agent);
        // Review R4: no customer on it yet: blanked until one is (TickCustomerNames decides then), never shown unchecked.
        if (person != null && !NameHidden(person)) { BlankedNames.Remove(key); return false; }
        if (_cfg == null || !_cfg.HideUnmetCustomerNames.Value) return false;
        if (BlankedNames.Count > 400) BlankedNames.Clear(); // pooled overlays: never a long list
        BlankedNames[key] = (ui, person, name);
        return true;
    }

    /// <summary>NameHooks: the pop-up's customer.</summary>
    internal static bool PopupNameHidden(AgentGhost customer) => NameHidden(customer.Person);
}
