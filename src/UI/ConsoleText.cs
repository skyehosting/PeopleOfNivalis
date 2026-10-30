using System;
using System.Text;
using System.Text.RegularExpressions;

namespace NivalisAIChat.UI;

/// <summary>
/// How a console line is coloured (FB-17, the author's wish: easier to read, colour only, nothing bigger or bolder).
/// A heading (a line ending in ':') in the section colour; the command a line starts with, and any other /command in
/// it, in the command colour; the label of a "Label: value" line in the label colour; the rest as it is. Every piece of
/// text stays escaped, as in the chat: nothing a provider, a file or the player wrote is read as markup. No Unity here,
/// so it can be tested.
/// </summary>
internal static class ConsoleText
{
    public const string HeaderHex = "#D9A55A", CommandHex = "#5FA8E8", LabelHex = "#C4A062";
    /// <summary>FB-51: a "Used:" line's percentage: well within, getting close (75% on), at or past its limit.</summary>
    public const string OkHex = "#7BC67B", NearHex = "#E0A84E", OverHex = "#E06A5A";
    private static readonly Regex Percent = new(@"\((\d{1,3})%", RegexOptions.CultureInvariant);

    /// <summary>Text shown as it is: TextMeshPro markup in it isn't read.</summary>
    public static string Escape(string s) =>
        string.IsNullOrEmpty(s) ? "" : "<noparse>" + EndTag.Replace(s, "</ noparse>") + "</noparse>";

    /// <summary>A closing noparse in any letter case (TextMeshPro reads tags whatever their case).</summary>
    public static readonly Regex EndTag = new(@"</\s*noparse\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Colour(string hex, string text) => text.Length == 0 ? "" : $"<color={hex}>{Escape(text)}</color>";

    // A /command word: a slash and letters, at the start or after a space or "(" (not "and/or", not a URL's "//").
    private static readonly Regex CommandWord = new(@"(?<=^|[\s(])/[A-Za-z][A-Za-z-]*", RegexOptions.CultureInvariant);

    public static string Format(string line)
    {
        line ??= "";
        string body = line.TrimStart();
        if (body.Length == 0) return "";
        string indent = line[..(line.Length - body.Length)];
        // A heading: "Commands (type them here...):", "3 problems since you last looked:".
        if (body.EndsWith(":") && !body.StartsWith("/")) return Escape(indent) + Colour(HeaderHex, body);
        int colon = body.IndexOf(": ", StringComparison.Ordinal);
        if (body.StartsWith("/"))
        {
            // "/chatter groups ...: residents talking" → the command part up to the colon, then the description.
            if (colon > 0 && colon <= 48) return Escape(indent) + Colour(CommandHex, body[..colon]) + Escape(":") + Commands(body[(colon + 1)..]);
            return Escape(indent) + Commands(body);
        }
        // "Mode: Dynamic", "Voice Provider 1 (ElevenLabs): 24 voices", "also: size <2-4> ...".
        if (colon > 0 && colon <= 40 && char.IsLetter(body[0]) && !body[..colon].Contains('/') && !body[..colon].Contains('.'))
        {
            string rest = body[(colon + 1)..];
            if (body[..colon] == "Used" && Percent.Match(rest) is { Success: true } m && int.TryParse(m.Groups[1].Value, out int pc))
            {
                string hex = pc >= 100 ? OverHex : pc >= 75 ? NearHex : OkHex;
                int at = m.Groups[1].Index;
                return Escape(indent) + Colour(LabelHex, "Used") + Escape(":") + Commands(rest[..at]) + Colour(hex, rest.Substring(at, m.Groups[1].Length + 1)) + Commands(rest[(at + m.Groups[1].Length + 1)..]);
            }
            return Escape(indent) + Colour(LabelHex, body[..colon]) + Escape(":") + Commands(rest);
        }
        return Escape(indent) + Commands(body);
    }

    /// <summary>The text with each /command in it coloured.</summary>
    private static string Commands(string text)
    {
        var sb = new StringBuilder();
        int at = 0;
        foreach (Match m in CommandWord.Matches(text))
        {
            sb.Append(Escape(text[at..m.Index]));
            sb.Append(Colour(CommandHex, m.Value));
            at = m.Index + m.Length;
        }
        sb.Append(Escape(text[at..]));
        return sb.ToString();
    }
}
