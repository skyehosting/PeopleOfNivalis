using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace NivalisAIChat.Api;

/// <summary>
/// FB-67: loads the plugins in BepInEx\plugins\PeopleOfNivalis\plugins (each a DLL with a public class implementing
/// <see cref="IPeopleOfNivalisPlugin"/> and a public constructor taking nothing). A DLL that won't load, or a plugin whose
/// Load throws, is skipped with a line in the log; the rest load. DisabledPlugins (ids or file names) are left out.
/// A plugin is code that runs in the game like any BepInEx plugin: only put in ones you trust. No Unity here.
/// </summary>
public sealed class PluginLoader
{
    public sealed class Loaded
    {
        public IPeopleOfNivalisPlugin Plugin = null!;
        public string File = "";
    }

    private readonly Action<string> _info, _warn;
    public readonly List<Loaded> Plugins = new();

    public PluginLoader(Action<string> info, Action<string> warn)
    {
        _info = info;
        _warn = warn;
        // Review F-PON-1: a plugin the API switches off (three errors in a row) is told, as its contract says.
        PonApi.SwitchedOff = id =>
        {
            Loaded? l;
            lock (Plugins) { l = Plugins.FirstOrDefault(x => SameId(x.Plugin, id)); if (l != null) Plugins.Remove(l); } // re-check V-1
            if (l == null) return;
            try { l.Plugin.Unload(); } catch (Exception e) { _warn($"Plugins: {id} failed to unload: {e.Message}"); }
        };
    }

    /// <summary>Loads every plugin in <paramref name="folder"/> (made if it isn't there). Returns how many loaded.</summary>
    public int LoadAll(string folder, IReadOnlyCollection<string> disabled)
    {
        try { Directory.CreateDirectory(folder); } catch { }
        string[] files;
        try { files = Directory.GetFiles(folder, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (Exception e) { _warn("Plugins: the folder can't be read: " + e.Message); return 0; }
        foreach (var file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (disabled.Contains(name, StringComparer.OrdinalIgnoreCase)) { _info($"Plugins: {name} is switched off (DisabledPlugins)."); continue; }
            Assembly asm;
            try { asm = Assembly.LoadFrom(file); }
            catch (Exception e) { _warn($"Plugins: {name}.dll couldn't be loaded ({e.GetType().Name}: {e.Message})."); continue; }
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).Select(t => t!).ToArray(); }
            foreach (var t in types.Where(t => t.IsClass && !t.IsAbstract && typeof(IPeopleOfNivalisPlugin).IsAssignableFrom(t)))
            {
                IPeopleOfNivalisPlugin p;
                try { p = (IPeopleOfNivalisPlugin)Activator.CreateInstance(t)!; }
                catch (Exception e) { _warn($"Plugins: {t.FullName} couldn't be made ({e.GetBaseException().Message})."); continue; }
                string id = "";
                try { id = p.Id ?? ""; } catch { }
                if (id.Length == 0 || disabled.Contains(id, StringComparer.OrdinalIgnoreCase) || Plugins.Any(x => SameId(x.Plugin, id)))
                {
                    if (id.Length > 0 && disabled.Contains(id, StringComparer.OrdinalIgnoreCase)) _info($"Plugins: {id} is switched off (DisabledPlugins).");
                    else if (id.Length == 0) _warn($"Plugins: {t.FullName} has no Id; skipped.");
                    else _warn($"Plugins: two plugins are called '{id}'; the second ({name}.dll) is skipped.");
                    continue;
                }
                try
                {
                    PonApi.OwnAssembly(asm, id); // its event handlers are its own (counted, and taken off with it)
                    p.Load();
                    if (PonApi.IsOff(id)) { try { p.Unload(); } catch { } _warn($"Plugins: {id} failed while loading; it's off."); continue; } // re-check V-1
                    lock (Plugins) Plugins.Add(new Loaded { Plugin = p, File = file });
                    _info($"Plugins: {Describe(p)} loaded ({name}.dll).");
                }
                catch (Exception e)
                {
                    _warn($"Plugins: {id} failed to load ({e.GetBaseException().GetType().Name}: {e.GetBaseException().Message}); it's off.");
                    PonApi.Forget(id);
                }
            }
        }
        return Plugins.Count;
    }

    /// <summary>Every plugin is told the mod is stopping, and let go.</summary>
    public void UnloadAll()
    {
        List<Loaded> all;
        lock (Plugins) { all = Plugins.ToList(); Plugins.Clear(); }
        foreach (var l in all)
        {
            string id = "";
            try { id = l.Plugin.Id; l.Plugin.Unload(); }
            catch (Exception e) { _warn($"Plugins: {id} failed to unload: {e.Message}"); }
            PonApi.Forget(id);
        }
        Plugins.Clear();
    }

    private static bool SameId(IPeopleOfNivalisPlugin p, string id)
    {
        try { return string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase); } catch { return false; }
    }

    private static string Describe(IPeopleOfNivalisPlugin p)
    {
        try { return $"{p.Name} {p.Version} ({p.Id})"; } catch { return "a plugin"; }
    }
}
