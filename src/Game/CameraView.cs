using System;
using Nivalis;
using UnityEngine;

namespace NivalisAIChat.Game;

/// <summary>
/// FB-80 (the author: "it creates a little camera display on the phone by using the players display (minus the UI)"):
/// the phone Camera's live view: a copy of the player's camera, on it (so it looks where you look), its colours graded the
/// same, the HUD left out, rendering into a small texture the phone shows cropped to its own shape. Main thread only.
/// </summary>
public static class CameraView
{
    private static GameObject? _go;
    private static Camera? _cam, _viewer;
    private static RenderTexture? _feed;

    /// <summary>The live view (null when the Camera isn't open).</summary>
    public static RenderTexture? Feed => _feed;

    /// <summary>The live view's width over its height (the screen's).</summary>
    public static float Aspect => _feed != null && _feed.height > 0 ? (float)_feed.width / _feed.height : (float)Screen.width / Math.Max(1, Screen.height);

    /// <summary>Starts (or keeps) the live view on the player's camera. False when there's none.</summary>
    public static bool Start(Camera? viewer)
    {
        if (viewer == null) return false;
        if (_cam != null && _viewer == viewer && _feed != null) return true;
        Stop();
        try
        {
            int w = 640, h = Mathf.Clamp(Mathf.RoundToInt(w * (float)Screen.height / Math.Max(1, Screen.width)), 180, 960);
            _feed = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "PeopleOfNivalis.CameraFeed", hideFlags = HideFlags.HideAndDontSave };
            _feed.Create();
            _go = new GameObject("PeopleOfNivalis.PhoneCamera") { hideFlags = HideFlags.HideAndDontSave };
            _go.SetActive(false);
            _cam = _go.AddComponent<Camera>();
            _cam.enabled = false;
            _cam.CopyFrom(viewer);
            Snapshot.CopyGrading(viewer, _go);
            _go.transform.SetParent(viewer.transform, false);
            _go.transform.localPosition = Vector3.zero;
            _go.transform.localRotation = Quaternion.identity;
            _go.SetActive(true);
            _cam.ResetProjectionMatrix();
            _cam.ResetWorldToCameraMatrix();
            _cam.ResetCullingMatrix();
            _cam.cullingMask &= ~(1 << 5); // Unity's UI layer: no HUD in the view
            _cam.aspect = (float)w / h;
            _cam.depth = viewer.depth - 1;
            _cam.targetTexture = _feed;
            _cam.enabled = true;
            _viewer = viewer;
            return true;
        }
        catch (Exception e) { Log.Debug("phone camera: " + e.Message); Stop(); return false; }
    }

    /// <summary>Every frame while it's open: the zoom kept the same as the player's.</summary>
    public static void Tick()
    {
        if (_cam == null || _viewer == null) return;
        try { if (_viewer.WasCollected) { Stop(); return; } _cam.fieldOfView = _viewer.fieldOfView; } catch { }
    }

    /// <summary>The live view let go (the Camera closed, the phone put away, the mod stopping).</summary>
    public static void Stop()
    {
        try { if (_cam != null) { _cam.enabled = false; _cam.targetTexture = null; } } catch { }
        try { if (_go != null) UnityEngine.Object.Destroy(_go); } catch { }
        try { if (_feed != null) { _feed.Release(); UnityEngine.Object.Destroy(_feed); } } catch { }
        _go = null; _cam = null; _viewer = null; _feed = null;
    }

    /// <summary>A JPEG (or PNG) shrunk to <paramref name="maxWidth"/> wide at most, as a JPEG; null when it can't be read.</summary>
    public static byte[]? Shrink(byte[] image, int maxWidth, int quality = 85)
    {
        Texture2D? tex = null, small = null;
        RenderTexture? rt = null;
        var previous = RenderTexture.active;
        try
        {
            tex = new Texture2D(2, 2, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
            if (!ImageConversion.LoadImage(tex, image, false)) return null;
            var src = tex;
            if (tex.width > maxWidth)
            {
                int w = maxWidth, h = Mathf.Max(1, Mathf.RoundToInt(tex.height * (float)maxWidth / tex.width));
                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                small = new Texture2D(w, h, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
                small.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                small.Apply(false, false);
                src = small;
            }
            var encoded = ImageConversion.EncodeToJPG(src, quality);
            if (encoded == null || encoded.Length == 0) return null;
            var bytes = new byte[encoded.Length];
            System.Runtime.InteropServices.Marshal.Copy(encoded.Pointer + 4 * IntPtr.Size, bytes, 0, bytes.Length);
            return bytes;
        }
        catch (Exception e) { Log.Debug("phone photo shrink: " + e.Message); return null; }
        finally
        {
            RenderTexture.active = previous;
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
            if (tex != null) UnityEngine.Object.Destroy(tex);
            if (small != null) UnityEngine.Object.Destroy(small);
        }
    }

    /// <summary>
    /// Someone under 18 in the camera's view within <paramref name="within"/> m (their body's box in the frustum; walls not
    /// counted, so it errs towards yes). A check that can't be made says yes: such a photo is never shown to an AI.
    /// </summary>
    public static bool ChildInView(Camera? viewer, Func<Character, bool> isMinor, float within = 80f)
    {
        if (viewer == null) return true;
        try
        {
            var planes = GeometryUtility.CalculateFrustumPlanes(viewer);
            Vector3 eye = viewer.transform.position;
            foreach (var c in Character.instances)
            {
                if (c == null || !c.isActiveAndEnabled) continue;
                Vector3 pos;
                try { pos = c.transform.position; } catch { continue; }
                if ((pos - eye).sqrMagnitude > within * within) continue;
                var box = new Bounds(pos + Vector3.up * 0.9f, new Vector3(1.4f, 2.4f, 1.4f)); // review F3: room for sitting, crouching, a pivot off the feet
                if (!GeometryUtility.TestPlanesAABB(planes, box)) continue;
                bool minor;
                try { minor = isMinor(c); } catch { minor = true; }
                if (minor) return true;
            }
            return false;
        }
        catch (Exception e) { Log.Debug("phone camera, who's in view: " + e.Message); return true; }
    }
}
