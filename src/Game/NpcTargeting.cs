using System;
using Nivalis;
using UnityEngine;

namespace NivalisAIChat.Game;

/// <summary>Finds the NPC the player is looking at and decides whether the mod may talk to them.</summary>
internal static class NpcTargeting
{
    /// <summary>The character closest to the centre of view, within reach and not behind a wall.</summary>
    public static Character? LookedAt(float maxDistance)
    {
        var cam = GameContext.PlayerCamera();
        if (cam == null) return null;
        var pc = GameContext.PlayerCharacter();
        Vector3 eye = cam.transform.position;
        Vector3 fwd = cam.transform.forward;

        Character? best = null;
        float bestScore = float.MaxValue;
        var all = Character.instances;
        if (all == null) return null;
        var it = all.GetEnumerator();
        while (it.MoveNext())
        {
            var c = it.Current;
            if (c == null || !c.isActiveAndEnabled) continue;
            Vector3 feet = c.transform.position;
            Vector3 chest = feet + Vector3.up * 1.3f;
            Vector3 to = chest - eye;
            float dist = to.magnitude;
            if (dist > maxDistance + 0.6f || dist < 0.2f) continue;
            // Distance from the view ray to the body's centre line.
            float along = Vector3.Dot(to, fwd);
            if (along <= 0) continue;
            Vector3 closest = eye + fwd * along;
            Vector2 lateral = new(closest.x - feet.x, closest.z - feet.z);
            float vertical = closest.y - feet.y;
            if (lateral.magnitude > 0.42f || vertical < -0.1f || vertical > 2.1f) continue;
            float score = lateral.magnitude * 3f + dist * 0.1f;
            if (score < bestScore && !Occluded(eye, chest, c, pc)) { best = c; bestScore = score; }
        }
        return best;
    }

    internal static bool Occluded(Vector3 eye, Vector3 target, Character c, PlayerCharacter? pc)
    {
        Vector3 dir = target - eye;
        var hits = Physics.RaycastAll(eye, dir.normalized, dir.magnitude, ~0, QueryTriggerInteraction.Ignore);
        foreach (var h in hits)
        {
            var t = h.collider != null ? h.collider.transform : null;
            if (t == null) continue;
            if (t.IsChildOf(c.transform)) continue;
            if (pc != null && t.IsChildOf(pc.transform)) continue;
            if (t.GetComponentInParent<Character>() != null) continue; // other people don't block the view
            if (h.distance > dir.magnitude - 0.35f) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Story characters and anyone the game already offers a conversation with are left alone.
    /// Returns null when the NPC is fair game, otherwise the reason.
    /// </summary>
    public static string? WhyNot(Character c, bool allowNamed)
    {
        try
        {
            if (c.Type == Character.CharacterType.Real && c.Agent == null) return "no agent";
            var person = GameContext.PersonOf(c);
            if (person != null && !allowNamed && GameContext.IsNamed(person)) return "story character";
            var di = c.DialogueInteraction;
            if (di != null)
            {
                bool inProgress = false, available = false;
                try { inProgress = di.dialogueInProgress; } catch { }
                try { available = !allowNamed && di.isActiveAndEnabled && di.CanInteract(); } catch { }
                if (inProgress) return "in the game's own conversation";
                if (available) return "has the game's own conversation";
            }
            try { if (c.Communication != null && c.Communication.IsPlayerDialogue) return "already in a dialogue"; } catch { }
            return null;
        }
        catch (Exception e)
        {
            return "error: " + e.Message;
        }
    }
}
