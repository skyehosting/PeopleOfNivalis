using System;
using System.IO;
using System.Linq;
using NivalisAIChat.Api;
using NivalisAIChat.Game;
using NivalisAIChat.Npc;

namespace NivalisAIChat;

/// <summary>
/// FB-67: plugins (Api/PonApi.cs, Api/PluginLoader.cs; docs/PLUGINS.md): loaded from BepInEx\plugins\PeopleOfNivalis\plugins
/// at start (LoadPlugins), and told what happens in chats. Only what a plugin needs crosses: a small snapshot of the person
/// (<see cref="NpcInfo"/>), never a key, never anyone under 18 (they're never chatted with).
/// </summary>
public static partial class ModRuntime
{
    private static PluginLoader? _plugins;
    private static double _barSeen = double.NaN;

    private static void StartPlugins()
    {
        PonApi.LogInfo = s => Log.Info(s);
        PonApi.LogWarn = s => Log.Warn(s);
        if (!_cfg.LoadPlugins.Value) { Log.Info("Plugins: not loaded (LoadPlugins is off)."); return; }
        string folder = Path.Combine(BepInEx.Paths.PluginPath, "PeopleOfNivalis", "plugins");
        var disabled = (_cfg.DisabledPlugins.Value ?? "").Split(',', ';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        _plugins = new PluginLoader(s => Log.Info(s), s => Log.Warn(s));
        int n = _plugins.LoadAll(folder, disabled);
        if (n > 0) Log.Info($"Plugins: {n} loaded from {folder}.");
    }

    private static void StopPlugins()
    {
        try { _plugins?.UnloadAll(); } catch (Exception e) { Log.Warn("Plugins: " + e.Message); }
        _plugins = null;
    }

    /// <summary>What a plugin is told about someone.</summary>
    private static NpcInfo InfoOf(NpcRecord rec) => new()
    {
        Id = rec.Id, Name = DisplayName(rec), Relationship = rec.State.Relationship, Mood = rec.State.Mood,
        Romance = Features.Romance ? rec.State.Romance : "", TimesMet = rec.State.TimesMet,
        Occupation = rec.State.Told.LastOrDefault(t => t.Topic == "job")?.Said ?? "",
    };

    /// <summary>Other mods' lines for this person's prompt (asked on the game's thread).</summary>
    private static void FillOtherMods(GameSnapshot snap, NpcRecord? rec)
    {
        snap.OtherMods = new System.Collections.Generic.List<string>();
        if (rec == null || _plugins == null && !PonApi.HasContext) return;
        try { snap.OtherMods = PonApi.ContextFor(new ContextRequest { Npc = InfoOf(rec), Location = snap.Location, GameDay = snap.GameDay, Clock = snap.Clock }); }
        catch (Exception e) { Log.Debug("other mods: " + e.Message); }
    }

    private static void PluginsChatOpened(NpcRecord rec)
    {
        _barSeen = rec.State.Relationship;
        PonApi.RaiseChatOpened(InfoOf(rec));
    }

    /// <summary>A line shown: the plugins hear it, their tags are handed over, a moved bar is told.</summary>
    private static void PluginsLineShown(NpcRecord rec, string line, Ai.ReplyExtras? extras)
    {
        var info = InfoOf(rec);
        PonApi.RaiseNpcSaid(info, line);
        if (extras != null && extras.Other.Count > 0) PonApi.HandleTags(info, extras.Other);
        double now = rec.State.Relationship;
        if (!double.IsNaN(_barSeen) && Math.Abs(now - _barSeen) > 0.0001) PonApi.RaiseRelationship(info, _barSeen, now);
        _barSeen = now;
    }
}
