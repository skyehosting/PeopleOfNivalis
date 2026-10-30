using System;
using System.Collections.Generic;
using Nivalis;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace NivalisAIChat.Game;

/// <summary>
/// One small picture of someone, for an AI that can see (LookAtNewPeople): a throwaway camera with the player camera's
/// settings, standing between them and the player and framing them from the hips up. It renders into its own texture,
/// so menus, the chat window and the rest of the screen are never in it, and the game's own camera is untouched.
/// Everyone else (other people, the player's own character) is hidden for that one render, so only this person is
/// ever shown to the AI. Main thread only.
/// </summary>
public static class Snapshot
{
    public const int Width = 384, Height = 512;

    /// <summary>
    /// A JPEG of this person as the player sees them right now, or null (with the reason) if it couldn't be taken. With the
    /// game's own colour grading when it can be copied; a frame that comes out black is taken again without it.
    /// </summary>
    public static (byte[]? jpeg, string? error) Take(Character npc, Camera viewer)
    {
        var (jpeg, error, dark) = TakeOnce(npc, viewer, graded: true);
        if (jpeg != null && !dark) return (jpeg, null);
        var plain = TakeOnce(npc, viewer, graded: false);
        return plain.jpeg != null ? (plain.jpeg, null) : (jpeg, error ?? plain.error);
    }

    private static (byte[]? jpeg, string? error, bool dark) TakeOnce(Character npc, Camera viewer, bool graded)
    {
        GameObject? go = null;
        RenderTexture? big = null, rt = null;
        Texture2D? tex = null;
        var previous = RenderTexture.active;
        try
        {
            if (npc == null || npc.WasCollected || viewer == null) return (null, "they or the camera weren't there any more", false);
            Vector3 feet = npc.transform.position;
            Vector3 head = GameContext.HeadOf(npc).position;
            if (head.y - feet.y < 0.5f) head = feet + Vector3.up * 1.65f; // no head bone found: an average height
            float top = head.y + 0.22f;
            float bottom = feet.y + (top - feet.y) * 0.25f; // knees up: face, top and trousers, not the floor
            float frame = Mathf.Max(0.6f, top - bottom);
            var centre = new Vector3(head.x, (top + bottom) * 0.5f, head.z);

            // From where the player stands (in front of their own character, so a third-person body isn't in the way).
            Vector3 from = viewer.transform.position;
            try { var pc = GameContext.PlayerCharacter(); if (pc != null) from = pc.transform.position; } catch { }
            Vector3 toward = from - centre;
            toward.y = 0f;
            float gap = toward.magnitude;
            if (gap < 0.05f) { toward = npc.transform.forward; toward.y = 0f; gap = 1.5f; }
            float distance = Mathf.Clamp(gap - 0.4f, 0.7f, 2.2f);

            go = new GameObject("PeopleOfNivalis.Snapshot") { hideFlags = HideFlags.HideAndDontSave };
            go.SetActive(false); // components are set up before anything runs
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.CopyFrom(viewer);
            if (graded) CopyGrading(viewer, go);
            go.SetActive(true);
            // The player camera's own matrices (jitter, custom projection) mustn't carry over: this one frames its own shot.
            cam.ResetProjectionMatrix();
            cam.ResetWorldToCameraMatrix();
            cam.ResetCullingMatrix();
            cam.transform.position = centre + toward.normalized * distance;
            cam.transform.rotation = Quaternion.LookRotation(centre - cam.transform.position, Vector3.up);
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = distance + 6f; // the room around them, not the street far behind
            cam.fieldOfView = Mathf.Clamp(2f * Mathf.Atan(frame * 0.56f / distance) * Mathf.Rad2Deg, 15f, 95f);
            cam.aspect = (float)Width / Height;

            // Rendered at twice the size and scaled down, for smooth edges without relying on MSAA.
            big = RenderTexture.GetTemporary(Width * 2, Height * 2, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = big;
            var hidden = new List<Renderer>();
            try
            {
                HideEveryoneElse(npc, hidden);
                cam.Render();
            }
            finally
            {
                foreach (var r in hidden) { try { if (r != null) r.enabled = true; } catch { } }
            }
            cam.targetTexture = null;
            rt = RenderTexture.GetTemporary(Width, Height, 0, RenderTextureFormat.ARGB32);
            big.filterMode = FilterMode.Bilinear;
            Graphics.Blit(big, rt);

            RenderTexture.active = rt;
            tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
            tex.Apply(false, false);
            bool dark = Brightness(tex) < 0.02f;
            var encoded = ImageConversion.EncodeToJPG(tex, 88);
            if (encoded == null || encoded.Length < 256) return (null, "the picture came out empty", true);
            var bytes = new byte[encoded.Length];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = encoded[i];
            return (bytes, null, dark);
        }
        catch (Exception e) { return (null, e.Message, false); }
        finally
        {
            RenderTexture.active = previous;
            try { if (go != null) { var cam = go.GetComponent<Camera>(); if (cam != null) cam.targetTexture = null; UnityEngine.Object.Destroy(go); } } catch { }
            try { if (big != null) RenderTexture.ReleaseTemporary(big); } catch { }
            try { if (rt != null) RenderTexture.ReleaseTemporary(rt); } catch { }
            try { if (tex != null) UnityEngine.Object.Destroy(tex); } catch { }
        }
    }

    public const int CityWidth = 1024, CityHeight = 576;

    /// <summary>
    /// Nightline's street view (FB-71): what the player's camera sees right now, at 1024x576, with everyone hidden (the
    /// player too), so no one (and never anyone under 18) is ever in it; the UI layer is culled and it renders into its own
    /// texture, so the HUD, menus and this mod's windows are never in it either. The game's own grading when it can be
    /// copied. Main thread only.
    /// </summary>
    public static (byte[]? jpeg, string? error) TakeCity(Camera viewer)
    {
        var (jpeg, error, dark) = TakeCityOnce(viewer, graded: true);
        if (jpeg != null && !dark) return (jpeg, null);
        var plain = TakeCityOnce(viewer, graded: false);
        return plain.jpeg != null && !plain.dark ? (plain.jpeg, null) : (null, error ?? plain.error ?? "the view came out dark");
    }

    private static (byte[]? jpeg, string? error, bool dark) TakeCityOnce(Camera viewer, bool graded)
    {
        GameObject? go = null;
        RenderTexture? rt = null;
        Texture2D? tex = null;
        var previous = RenderTexture.active;
        try
        {
            if (viewer == null) return (null, "no camera", false);
            go = new GameObject("PeopleOfNivalis.CityShot") { hideFlags = HideFlags.HideAndDontSave };
            go.SetActive(false);
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.CopyFrom(viewer);
            if (graded) CopyGrading(viewer, go);
            go.SetActive(true);
            cam.ResetProjectionMatrix();
            cam.ResetWorldToCameraMatrix();
            cam.ResetCullingMatrix();
            cam.cullingMask &= ~(1 << 5); // Unity's UI layer: world-space or camera-space HUD never in the picture
            cam.aspect = (float)CityWidth / CityHeight;
            rt = RenderTexture.GetTemporary(CityWidth, CityHeight, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            var hidden = new List<Renderer>();
            try
            {
                HideEveryoneElse(null, hidden);
                cam.Render();
            }
            finally
            {
                foreach (var r in hidden) { try { if (r != null) r.enabled = true; } catch { } }
            }
            cam.targetTexture = null;
            RenderTexture.active = rt;
            tex = new Texture2D(CityWidth, CityHeight, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, CityWidth, CityHeight), 0, 0, false);
            tex.Apply(false, false);
            bool dark = Brightness(tex) < 0.03f;
            var encoded = ImageConversion.EncodeToJPG(tex, 82);
            if (encoded == null || encoded.Length < 1024) return (null, "the view came out empty", true);
            var bytes = new byte[encoded.Length];
            System.Runtime.InteropServices.Marshal.Copy(encoded.Pointer + 4 * IntPtr.Size, bytes, 0, bytes.Length); // one copy (as Ava's voice)
            return (bytes, null, dark);
        }
        catch (Exception e) { return (null, e.Message, false); }
        finally
        {
            RenderTexture.active = previous;
            try { if (go != null) { var cam = go.GetComponent<Camera>(); if (cam != null) cam.targetTexture = null; UnityEngine.Object.Destroy(go); } } catch { }
            try { if (rt != null) RenderTexture.ReleaseTemporary(rt); } catch { }
            try { if (tex != null) UnityEngine.Object.Destroy(tex); } catch { }
        }
    }

    /// <summary>
    /// FB-80: the phone Camera's photo: the player's whole view as it is (everyone in it), minus the HUD (Unity's UI
    /// layer culled; overlay canvases are never in a camera), at the screen's shape, <paramref name="width"/> wide at most.
    /// Main thread only; a one-off hitch.
    /// </summary>
    public static (byte[]? jpeg, string? error) TakeView(Camera viewer, int width = 1920)
    {
        GameObject? go = null;
        RenderTexture? rt = null;
        Texture2D? tex = null;
        var previous = RenderTexture.active;
        try
        {
            if (viewer == null) return (null, "no camera");
            int w = Mathf.Clamp(Math.Min(Screen.width, width), 320, 3840);
            int h = Mathf.Clamp(Mathf.RoundToInt(w * (float)Screen.height / Math.Max(1, Screen.width)), 180, 2160);
            go = new GameObject("PeopleOfNivalis.PhonePhoto") { hideFlags = HideFlags.HideAndDontSave };
            go.SetActive(false);
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.CopyFrom(viewer);
            CopyGrading(viewer, go);
            go.SetActive(true);
            cam.ResetProjectionMatrix();
            cam.ResetWorldToCameraMatrix();
            cam.ResetCullingMatrix();
            cam.cullingMask &= ~(1 << 5); // the UI layer
            cam.aspect = (float)w / h;
            rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;
            RenderTexture.active = rt;
            tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            tex.Apply(false, false);
            var encoded = ImageConversion.EncodeToJPG(tex, 88);
            if (encoded == null || encoded.Length < 1024) return (null, "the view came out empty");
            var bytes = new byte[encoded.Length];
            System.Runtime.InteropServices.Marshal.Copy(encoded.Pointer + 4 * IntPtr.Size, bytes, 0, bytes.Length); // one copy (as TakeCity)
            return (bytes, null);
        }
        catch (Exception e) { return (null, e.Message); }
        finally
        {
            RenderTexture.active = previous;
            try { if (go != null) { var cam = go.GetComponent<Camera>(); if (cam != null) cam.targetTexture = null; UnityEngine.Object.Destroy(go); } } catch { }
            try { if (rt != null) RenderTexture.ReleaseTemporary(rt); } catch { }
            try { if (tex != null) UnityEngine.Object.Destroy(tex); } catch { }
        }
    }

    /// <summary>
    /// The player camera's post-processing (colour grading, tone mapping, exposure), so colours in the picture are the
    /// ones on screen. No anti-aliasing history is shared with the game's camera.
    /// </summary>
    internal static void CopyGrading(Camera viewer, GameObject go)
    {
        try
        {
            var source = viewer.GetComponent<PostProcessLayer>();
            if (source == null || !source.enabled) return;
            var layer = go.AddComponent<PostProcessLayer>();
            layer.Init(source.m_Resources);
            layer.volumeLayer = source.volumeLayer;
            layer.volumeTrigger = go.transform;
            layer.antialiasingMode = PostProcessLayer.Antialiasing.None;
            layer.stopNaNPropagation = true;
            layer.finalBlitToCameraTarget = false;
        }
        catch (Exception e) { Log.Debug("snapshot grading: " + e.Message); }
    }

    /// <summary>Average brightness, 0..1, sampled sparsely.</summary>
    private static float Brightness(Texture2D tex)
    {
        try
        {
            var px = tex.GetPixels32();
            long sum = 0;
            int n = 0;
            for (int i = 0; i < px.Length; i += 97) { var c = px[i]; sum += c.r + c.g + c.b; n++; }
            return n == 0 ? 0f : sum / (n * 765f);
        }
        catch { return 1f; }
    }

    /// <summary>Switches off (for one render) every other person's renderers, the player's included (null: everyone).</summary>
    private static void HideEveryoneElse(Character? npc, List<Renderer> hidden)
    {
        void Hide(GameObject? go)
        {
            if (go == null) return;
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs == null) return;
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r != null && r.enabled) { r.enabled = false; hidden.Add(r); }
            }
        }
        try { var pc = GameContext.PlayerCharacter(); if (pc != null) Hide(pc.gameObject); } catch { }
        var all = Character.instances;
        if (all == null) return;
        var it = all.GetEnumerator();
        while (it.MoveNext())
        {
            try
            {
                var c = it.Current;
                if (c == null || c.WasCollected || (npc != null && c.Pointer == npc.Pointer)) continue;
                Hide(c.gameObject); // at any distance: nobody else is ever in the picture
            }
            catch { }
        }
    }
}
