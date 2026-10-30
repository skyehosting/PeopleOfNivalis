using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace NivalisAIChatDev;

/// <summary>
/// DEVELOPMENT ONLY. Hot-loads BepInEx\devmods\NivalisAIChat.dll into the running game and reloads it
/// whenever the file changes, by calling its static ModRuntime.Start/Update/LateUpdate/Stop.
/// Remove this plugin (and the devmods folder) for normal play. It refuses to run beside the installed mod (#16, audit
/// step 7c): move BepInEx\plugins\PeopleOfNivalis\NivalisAIChat.dll out for dev work, and back afterwards.
/// </summary>
[BepInPlugin("mira.nivalis.aichat.devloader", "Nivalis AI Chat (dev loader)", "0.1.0")]
public sealed class DevLoaderPlugin : BasePlugin
{
    internal static ManualLogSource L = null!;
    internal static string Watch = "";
    internal static DateTime Stamp;
    internal static Action? Update, LateUpdate, Stop;
    /// <summary>A chat, the console or the phone is open in the running copy: no reload until it closes (the standing rule).</summary>
    internal static Func<bool>? Busy;
    internal static int Generation;
    internal static ConfigFile? ModConfig;
    internal static ManualLogSource? ModLog;
    private static bool _refused;

    /// <summary>The installed mod's plugin ID, which is also its Harmony ID (src/Plugin.cs).</summary>
    private const string ReleaseGuid = "PeopleOfNivalis";

    public override void Load()
    {
        L = Log;
        // With the installed mod beside it, two copies would run at once under one Harmony ID, and a dev Stop
        // (UnpatchSelf) would strip the installed copy's hooks too (#16). The chainloader may load this plugin first, so
        // the plugins folder itself is looked at.
        if (InstalledMod() is { } installed)
        {
            Refuse("the mod is installed (" + installed + ")");
            return;
        }
        string dir = Path.Combine(Paths.BepInExRootPath, "devmods");
        Directory.CreateDirectory(Path.Combine(dir, "loaded"));
        Watch = Path.Combine(dir, "NivalisAIChat.dll");
        AddComponent<DevPump>();
        Log.LogInfo("Dev loader watching " + Watch);
    }

    /// <summary>An exact NivalisAIChat.dll anywhere under BepInEx\plugins (this loader's own file is named otherwise).</summary>
    private static string? InstalledMod()
    {
        try
        {
            return Directory.EnumerateFiles(Paths.PluginPath, "*.dll", SearchOption.AllDirectories)
                .Where(f => string.Equals(Path.GetFileName(f), "NivalisAIChat.dll", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetRelativePath(Paths.BepInExRootPath, f)).FirstOrDefault();
        }
        catch (Exception e)
        {
            L.LogWarning("Couldn't look through the plugins folder for the installed mod: " + e.Message);
            return null;
        }
    }

    /// <summary>At each reload: the installed mod got loaded after all (by the chainloader, or into the default context).</summary>
    private static string? LoadedMod()
    {
        try { if (IL2CPPChainloader.Instance?.Plugins.ContainsKey(ReleaseGuid) == true) return "the " + ReleaseGuid + " plugin is loaded"; }
        catch { }
        return AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().Name == "NivalisAIChat") ? "NivalisAIChat is loaded outside the dev loader" : null;
    }

    private static void Refuse(string why)
    {
        if (_refused) return;
        _refused = true;
        L.LogError($"Dev loader not running: {why}. Two copies of the mod would run at once. For dev work, move the installed " +
                   @"NivalisAIChat.dll out of BepInEx\plugins, and put it back afterwards.");
    }

    internal static void Reload()
    {
        if (LoadedMod() is { } loaded) { Refuse(loaded); return; } // the generation running (if any) stays as it is
        try { Stop?.Invoke(); } catch (Exception e) { L.LogError("Stop failed: " + e); }
        Update = LateUpdate = Stop = null;
        Generation++;
        string copy = Path.Combine(Path.GetDirectoryName(Watch)!, "loaded", $"NivalisAIChat_{DateTime.Now:HHmmss}_{Generation}.dll");
        File.Copy(Watch, copy, true);
        var alc = new AssemblyLoadContext("NivalisAIChat#" + Generation); // falls back to the default context for shared deps
        var asm = alc.LoadFromAssemblyPath(copy);
        var rt = asm.GetType("NivalisAIChat.ModRuntime", true)!;
        // Fresh each generation: entries bound by an earlier copy use that copy's enum types.
        ModConfig = new ConfigFile(Path.Combine(Paths.ConfigPath, "PeopleOfNivalis.cfg"), false);
        ModLog ??= BepInEx.Logging.Logger.CreateLogSource("Nivalis AI Chat");
        rt.GetMethod("Start", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { ModConfig, ModLog, Path.Combine(Paths.ConfigPath, "NivalisAIChat") });
        Update = (Action)Delegate.CreateDelegate(typeof(Action), rt.GetMethod("Update")!);
        LateUpdate = (Action)Delegate.CreateDelegate(typeof(Action), rt.GetMethod("LateUpdate")!);
        Stop = (Action)Delegate.CreateDelegate(typeof(Action), rt.GetMethod("Stop")!);
        var open = new[] { "ChatOpen", "ConsoleOpen", "PhoneOpen" }
            .Select(n => rt.GetProperty(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(p => p != null).ToList();
        Busy = () => open.Any(p => p!.GetValue(null) is true);
        L.LogInfo($"Loaded generation {Generation} from {Path.GetFileName(copy)}");
    }
}

public sealed class DevPump : MonoBehaviour
{
    public DevPump(IntPtr ptr) : base(ptr) { }
    private float _next;
    private bool _saidWaiting;

    public void Update()
    {
        if (Time.unscaledTime >= _next)
        {
            _next = Time.unscaledTime + 1f;
            try
            {
                if (File.Exists(DevLoaderPlugin.Watch))
                {
                    var t = File.GetLastWriteTimeUtc(DevLoaderPlugin.Watch);
                    if (t != DevLoaderPlugin.Stamp && (DateTime.UtcNow - t).TotalSeconds > 1.5)
                    {
                        // Never while a chat (or the console, or the phone) is open: tried again each second until it closes.
                        if (DevLoaderPlugin.Busy?.Invoke() == true)
                        {
                            if (!_saidWaiting) DevLoaderPlugin.L.LogInfo("A new build is waiting: it loads once the chat, console or phone is closed.");
                            _saidWaiting = true;
                        }
                        else
                        {
                            _saidWaiting = false;
                            DevLoaderPlugin.Stamp = t;
                            DevLoaderPlugin.Reload();
                        }
                    }
                }
            }
            catch (Exception e) { DevLoaderPlugin.L.LogError("Reload failed: " + e); }
        }
        try { DevLoaderPlugin.Update?.Invoke(); } catch (Exception e) { DevLoaderPlugin.L.LogError("Update: " + e); DevLoaderPlugin.Update = null; }
    }

    public void LateUpdate()
    {
        try { DevLoaderPlugin.LateUpdate?.Invoke(); } catch (Exception e) { DevLoaderPlugin.L.LogError("LateUpdate: " + e); DevLoaderPlugin.LateUpdate = null; }
    }
}
