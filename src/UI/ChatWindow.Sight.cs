using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisAIChat.UI;

/// <summary>
/// FB-29 `/sight on`: each nearby resident's sight triangle drawn as thin lines over the world. On a canvas of its own,
/// under the mod's other UI (the chat and the console stay on top), with no scaler, so its units are screen pixels.
/// </summary>
internal sealed partial class ChatWindow
{
    private GameObject? _sightRoot;
    private readonly List<Image> _sightLines = new();

    /// <summary>Draws these world-space segments (a, b, colour) for this frame; any lines from the last frame not used are hidden.</summary>
    public void DrawSight(Camera cam, List<(Vector3 a, Vector3 b, Color color)> segments)
    {
        if (_sightRoot == null)
        {
            _sightRoot = new GameObject("NivalisAIChat.Sight");
            UnityEngine.Object.DontDestroyOnLoad(_sightRoot);
            var canvas = _sightRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100; // under the chat window and console (32000)
        }
        int used = 0;
        float near = cam.nearClipPlane + 0.05f;
        var camT = cam.transform; // once, not twice a line (each read is a call into the game)
        float sw = Screen.width, sh = Screen.height;
        foreach (var (a0, b0, color) in segments)
        {
            Vector3 a = a0, b = b0;
            // Behind the camera: cut at the near plane (a point behind it would be drawn mirrored).
            float za = camT.InverseTransformPoint(a).z, zb = camT.InverseTransformPoint(b).z;
            if (za < near && zb < near) continue;
            if (za < near) a = Vector3.Lerp(a, b, (near - za) / (zb - za));
            else if (zb < near) b = Vector3.Lerp(b, a, (near - zb) / (za - zb));
            Vector3 sa = cam.WorldToScreenPoint(a), sb = cam.WorldToScreenPoint(b);
            // Wholly off one side of the screen: not drawn (most of a ring around you is behind or beside you).
            if ((sa.x < 0 && sb.x < 0) || (sa.x > sw && sb.x > sw) || (sa.y < 0 && sb.y < 0) || (sa.y > sh && sb.y > sh)) continue;
            var d = new Vector2(sb.x - sa.x, sb.y - sa.y);
            float len = d.magnitude;
            if (len < 1f) continue;
            var img = SightLine(used++);
            var rt = img.rectTransform;
            rt.position = new Vector3((sa.x + sb.x) * 0.5f, (sa.y + sb.y) * 0.5f, 0f);
            rt.sizeDelta = new Vector2(len, 3f);
            rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            img.color = color;
            if (!img.gameObject.activeSelf) img.gameObject.SetActive(true);
        }
        for (int i = used; i < _sightLines.Count; i++)
            if (_sightLines[i] != null && _sightLines[i].gameObject.activeSelf) _sightLines[i].gameObject.SetActive(false);
    }

    private Image SightLine(int i)
    {
        while (_sightLines.Count <= i)
        {
            var go = new GameObject("SightLine");
            go.transform.SetParent(_sightRoot!.transform, false);
            var img = go.AddComponent<Image>(); // no sprite: a plain filled rectangle
            img.raycastTarget = false;
            _sightLines.Add(img);
        }
        return _sightLines[i];
    }

    /// <summary>Takes the lines away (/sight off, and when the UI is destroyed).</summary>
    public void HideSight()
    {
        if (_sightRoot != null) UnityEngine.Object.Destroy(_sightRoot);
        _sightRoot = null;
        _sightLines.Clear();
    }
}
