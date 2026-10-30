using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

// FB-40: Nivalis Switchboard (a separate mod, never required) lists the mods carrying this, and shows their settings.
[assembly: System.Reflection.AssemblyMetadata("NivalisSettingsMenu", "1")]

namespace NivalisAIChat;

[BepInPlugin(Guid, Name, Version)]
[BepInProcess("Nivalis Nights.exe")]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "PeopleOfNivalis"; // BepInEx names the settings file after this: PeopleOfNivalis.cfg
    public const string Name = "People of Nivalis";
    public const string Version = "0.10.3"; // beta

    /// <summary>What the static constructor fixed in the settings file, logged once the mod's log is up.</summary>
    internal static readonly List<string> EarlyNotes = new();

    /// <summary>
    /// Runs before BepInEx reads this plugin's settings file (its base constructor does that): a voice Providers line BepInEx
    /// can't read ("…, O'Neil:male, model=eleven_v3") is fixed first, or the whole mod wouldn't load (ROBUST-12). Never
    /// throws: anything going wrong here just leaves the file as it was, and says so once the log is up.
    /// </summary>
    static Plugin()
    {
        try
        {
            string path = Path.Combine(Paths.ConfigPath, Guid + ".cfg");
            var (outcome, text, error) = Npc.DataFile.ReadText(path, tries: 2);
            if (outcome == Npc.DataFile.Outcome.Unreadable) EarlyNotes.Add("its voice provider lines couldn't be checked before loading (" + error + ").");
            if (outcome != Npc.DataFile.Outcome.Read) return;
            var notes = new List<string>();
            string fixedText = SettingsFileText.FixProviderLines(text!, notes);
            if (ReferenceEquals(fixedText, text)) return;
            // Written back byte for byte but the fixed lines: a file saved as "ANSI" keeps its accented letters.
            var (raw, encoding) = Npc.DataFile.ReadKeepingBytes(path);
            Npc.DataFile.WriteAtomic(path, SettingsFileText.FixProviderLines(raw, new List<string>()), encoding: encoding);
            EarlyNotes.AddRange(notes);
        }
        catch (Exception e) { EarlyNotes.Add("its voice provider lines couldn't be checked before loading (" + e.GetType().Name + ")."); }
    }

    public override void Load()
    {
        ModRuntime.Start(Config, Log, Path.Combine(Paths.ConfigPath, "NivalisAIChat"));
        AddComponent<Pump>();
    }

    public override bool Unload()
    {
        ModRuntime.Stop();
        return true;
    }
}

/// <summary>Unity calls into the mod through this one injected behaviour.</summary>
public sealed class Pump : MonoBehaviour
{
    public Pump(IntPtr ptr) : base(ptr) { }

    public void Update() => ModRuntime.Update();

    public void LateUpdate() => ModRuntime.LateUpdate();
}
