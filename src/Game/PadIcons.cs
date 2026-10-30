using System;
using System.Collections.Generic;
using Nivalis;
using TMPro;
using UnityEngine.InputSystem;

namespace NivalisAIChat.Game;

/// <summary>
/// CF-12 (the author: "a sprite sheet of the controller mappings for playstation and xbox"): the game's own controller
/// icons, the ones its prompts show. The game keeps one sprite sheet per kind of controller (Xbox, PlayStation) and a table
/// from each control to its sprite (<c>DeviceDisplayConfigurator</c>, held by <c>PlayerInputManager.inputConfigurator</c>).
/// This asks the game's own lookup (never reading its table directly), then a sprite named after the control; whatever it
/// finds is checked against the sheet before it's used. Nothing found (another kind of pad, a game update): null, and the text label is shown as before. Each answer is
/// kept, and said once in the log, so the first test shows what the game gave.
/// </summary>
internal static class PadIcons
{
    private static readonly Dictionary<string, (TMP_SpriteAsset? sprites, string? name, float at)> Found = new(StringComparer.Ordinal);
    /// <summary>The input manager the answers came from: another one (a scene or save change) and they're asked again.</summary>
    private static IntPtr _from;
    /// <summary>A miss is asked again after this long (the game may not have had its icons ready yet).</summary>
    private const float MissFor = 30f;

    /// <summary>The Input System's name for a button as <see cref="Pad.Normal"/> names it.</summary>
    public static string? Control(string button) => NivalisAIChat.UI.PadRepeat.ControlName(button);

    /// <summary>The icon for <paramref name="button"/> on the controller in use: a TextMeshPro sprite tag and its sheet.</summary>
    public static bool Try(string button, out TMP_SpriteAsset sprites, out string tag)
    {
        sprites = null!;
        tag = "";
        string? control = Control(button);
        if (control == null) return false;
        Gamepad? pad = null;
        try { pad = Gamepad.current; } catch { }
        if (pad == null) return false;
        string layout = "";
        try { layout = pad.layout ?? ""; } catch { }
        string key = layout + "|" + control;
        float now = UnityEngine.Time.unscaledTime;
        IntPtr from = IntPtr.Zero;
        try { from = GameContext.Existing<Nivalis.PlayerInputManager>()?.Pointer ?? IntPtr.Zero; } catch { }
        if (from != _from) { Found.Clear(); _from = from; } // review CF12-3: a new input manager, new answers
        bool known = Found.TryGetValue(key, out var hit);
        if (known && hit.name == null && now - hit.at > MissFor) known = false; // a miss is asked again later
        if (!known)
        {
            bool first = !Found.ContainsKey(key);
            (TMP_SpriteAsset?, string?) found;
            try { found = Find(layout, control); }
            catch (Exception e) { found = (null, null); Log.Debug("controller icons: " + e.Message); }
            hit = (found.Item1, found.Item2, now);
            Found[key] = hit;
            if (first || hit.name != null)
                Log.Info(hit.name != null
                    ? $"Controller icons: the game's icon for {control} on {Or(layout)} is '{hit.name}'."
                    : $"Controller icons: the game has no icon for {control} on {Or(layout)}; the button's name is shown instead.");
        }
        if (hit.sprites == null || hit.name == null) return false;
        try { if (hit.sprites.Pointer == IntPtr.Zero || string.IsNullOrEmpty(hit.sprites.name)) throw new InvalidOperationException(); }
        catch { Found.Remove(key); return false; } // the sheet is gone (destroyed with its scene): the text label instead
        sprites = hit.sprites;
        tag = $"<sprite name=\"{hit.name}\">";
        return true;
    }

    /// <summary>Forgotten (a game update, another controller plugged in is a new key anyway).</summary>
    public static void Clear() => Found.Clear();

    private static (TMP_SpriteAsset?, string?) Find(string layout, string control)
    {
        var input = GameContext.Existing<Nivalis.PlayerInputManager>();
        var config = input?.inputConfigurator;
        var sets = config?.listDeviceSets;
        if (config == null || sets == null) return (null, null);
        DeviceDisplayConfigurator.DeviceSet? set = null;
        for (int i = 0; i < sets.Count && set == null; i++)
        {
            var s = sets[i];
            if (s != null && layout.Length > 0 && (s.deviceRawPath ?? "").Contains(layout, StringComparison.OrdinalIgnoreCase)) set = s;
        }
        // A layout the table doesn't name (another make of the same pad): the set for its kind.
        string kind = Pad.PlayStation ? "playstation" : "xbox";
        for (int i = 0; i < sets.Count && set == null; i++)
        {
            var s = sets[i];
            if (s != null && (s.deviceDisplayName ?? "").Contains(kind, StringComparison.OrdinalIgnoreCase)) set = s;
        }
        var sheet = set?.spriteAsset;
        if (set == null || sheet == null) return (null, null);

        bool OnSheet(string? name) => !string.IsNullOrEmpty(name) && sheet.GetSpriteIndexFromName(name) >= 0;

        // The game's own lookup, by the path forms a binding takes.
        foreach (string path in new[] { $"<Gamepad>/{control}", control, $"<{layout}>/{control}" })
        {
            try
            {
                var d = config.GetDeviceAssetSprite(set.deviceRawPath, path);
                if (d != null && OnSheet(d.sprite)) return (sheet, d.sprite);
            }
            catch { }
        }
        // Review XR-2: the game's table itself (DeviceSet.references) is an array of structs, which the interop would read as
        // objects (a native crash try/catch can't stop): never read. A sprite named after the control is the only other way.
        return OnSheet(control) ? (sheet, control) : (null, null);
    }

    private static string Or(string layout) => layout.Length > 0 ? layout : "this controller";
}
