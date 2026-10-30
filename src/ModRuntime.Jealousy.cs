using System;
using System.Linq;
using Nivalis;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;
using NivalisAIChat.Social;
using UnityEngine;

namespace NivalisAIChat;

/// <summary>
/// FB-63 (the author: "jealous 'malicious' acts that are roleplay plus visiting and animation"): someone whose partner
/// flirted with someone else has it in for that someone (a grudge, Social/Couples.cs). When both are around you, they go
/// over (walking up from as far as 35 m on a NavMesh path) and a residents' conversation starts between them, told that
/// they're jealous: words and gestures, nothing physical. Once it has been heard, the grudge is spent.
/// </summary>
public static partial class ModRuntime
{
    private sealed class JealousWalk
    {
        public Character Walker = null!, Rival = null!;
        public string WalkerId = "", RivalId = "", Name = "";
        public float Since;
    }

    private static JealousWalk? _jealous;
    private static float _nextJealousLook;

    private static void TickJealousy()
    {
        if (_jealous != null) { TickJealousWalk(_jealous); return; }
        float now = Time.unscaledTime;
        if (now < _nextJealousLook) return;
        _nextJealousLook = now + 10f;
        if (!Features.Romance || !_cfg.Jealousy.Value || _couples == null || _chatter == null || !_cfg.Chatter.Value || _resetting || !InGameplay()) return;
        var grudges = _couples.Grudges().Where(g => g.Partner != CoupleBook.Player && g.With != CoupleBook.Player).ToList();
        if (grudges.Count == 0) return;
        var s = ChatterSettingsNow();
        var cands = ChatterCandidates(s);
        foreach (var g in grudges)
        {
            var walker = cands.FirstOrDefault(c => c.Id == g.Partner && c.Holdable && !c.Seated && !c.Worker);
            var rival = cands.FirstOrDefault(c => c.Id == g.With);
            if (walker == null || rival == null) continue;
            float dist = Flat(walker.Pos - rival.Pos).magnitude;
            if (dist <= 7f)
            {
                if (StartGroup(s, rival, walker, false, cands, out string why)) Log.Info($"Jealousy: {g.Partner} goes over to {g.WithName}.");
                else Log.Debug("jealousy: " + why);
                return;
            }
            if (dist > 35f) continue;
            if (!NpcHold.EngageMember(walker.C, _cfg.AnimateNpc.Value)) continue;
            if (!NpcHold.Follow(walker.C, rival.C.transform, 3f, player: false)) { NpcHold.ReleaseMember(walker.C); continue; }
            _jealous = new JealousWalk { Walker = walker.C, Rival = rival.C, WalkerId = walker.Id, RivalId = rival.Id, Name = g.WithName, Since = now };
            Log.Info($"Jealousy: someone heads over to {g.WithName} ({dist:0} m away).");
            return;
        }
    }

    private static void TickJealousWalk(JealousWalk j)
    {
        float now = Time.unscaledTime;
        bool alive = Alive(j.Walker) && Alive(j.Rival);
        if (!alive || now - j.Since > 45f || _resetting || !Features.Romance)
        {
            if (Alive(j.Walker)) { NpcHold.StopFollow(j.Walker); NpcHold.ReleaseMember(j.Walker); }
            _jealous = null;
            return;
        }
        if (Flat(j.Walker.transform.position - j.Rival.transform.position).magnitude > 4.5f) return;
        NpcHold.StopFollow(j.Walker);
        NpcHold.ReleaseMember(j.Walker);
        _jealous = null;
        var s = ChatterSettingsNow();
        var cands = ChatterCandidates(s);
        var walker = cands.FirstOrDefault(c => c.Id == j.WalkerId);
        var rival = cands.FirstOrDefault(c => c.Id == j.RivalId);
        if (walker != null && rival != null && StartGroup(s, rival, walker, false, cands, out string why)) Log.Info($"Jealousy: face to face with {j.Name}.");
        else Log.Debug("jealousy: they didn't get talking");
    }

    /// <summary>A load, a reset or the mod stopping: whoever was walking over is let go.</summary>
    private static void EndJealousWalk()
    {
        if (_jealous is { } j && Alive(j.Walker)) { try { NpcHold.StopFollow(j.Walker); NpcHold.ReleaseMember(j.Walker); } catch { } }
        _jealous = null;
    }
}
