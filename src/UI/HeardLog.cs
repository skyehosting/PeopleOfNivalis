using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisAIChat.UI;

/// <summary>
/// CF-24 (a tester: "pls add dialogue log because sometimes I miss over the head subtitles"): the lines said over people's
/// heads (residents talking, world chat, greetings, calls, mutters), newest last, for the console's /heard. Only what was
/// shown as a subtitle within hearing; a child's line (their brush-off) is never kept (ShowBark heard: false). Kept for this
/// game session and the save it's in. No Unity, tested.
/// </summary>
public sealed class HeardLog
{
    public sealed record Line(string Clock, string Speaker, string Text);

    private readonly int _max;
    private readonly List<Line> _lines = new();
    private readonly object _lock = new();

    public HeardLog(int max = 80) { _max = Math.Max(1, max); }

    public int Count { get { lock (_lock) return _lines.Count; } }

    private readonly List<string> _keys = new();

    /// <summary>
    /// A subtitle was shown. The same bubble shown once more (same <paramref name="key"/>, or the same person when it has none,
    /// with the same words, among the last few) isn't added twice; review CF24-2: two strangers both "Someone" are told apart by it.
    /// </summary>
    public void Add(string clock, string speaker, string text, string key = "")
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        speaker = (speaker ?? "").Trim();
        if (speaker.Length == 0) speaker = "Someone";
        string who = (key ?? "").Length > 0 ? key! : "name:" + speaker;
        lock (_lock)
        {
            for (int i = Math.Max(0, _lines.Count - 4); i < _lines.Count; i++)
                if (_keys[i] == who && _lines[i].Text == text) return;
            _lines.Add(new Line(clock ?? "", speaker, text));
            _keys.Add(who);
            if (_lines.Count > _max) { _lines.RemoveRange(0, _lines.Count - _max); _keys.RemoveRange(0, _keys.Count - _max); }
        }
    }

    /// <summary>The newest <paramref name="n"/>, oldest first.</summary>
    public List<Line> Last(int n)
    {
        lock (_lock) return _lines.Skip(Math.Max(0, _lines.Count - Math.Max(1, n))).ToList();
    }

    /// <summary>A line as the console shows it: "9:41 PM  Mara: Rough night?"</summary>
    public static string Format(Line l) => $"{(l.Clock.Length > 0 ? l.Clock + "  " : "")}{l.Speaker}: {l.Text}";

    public void Clear() { lock (_lock) { _lines.Clear(); _keys.Clear(); } }
}
