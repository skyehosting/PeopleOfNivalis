using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NivalisAIChat;

/// <summary>
/// The settings file read the way BepInEx reads it (audit step 5, E5): its lines split as File.ReadAllLines splits them
/// (at "\r\n", "\n" or a lone "\r"), and every trimmed line that isn't empty, a "#" comment or a "[Section]" header is
/// "Key = value", split at the first '='. A key (or the section it's in) holding one of ' " \ [ ] = or a tab, or a
/// section name with spaces at its edges, makes BepInEx throw: on a reload, and at the next start before the mod can
/// run. The voice Providers block isn't BepInEx's, but its lines are read the same way, so
/// "(…, O'Neil:male, model=eleven_v3)," broke the whole file (ROBUST-12). Pure text in, text out; Unity-free (tested).
/// </summary>
public static class SettingsFileText
{
    private static readonly char[] Forbidden = { '\'', '"', '\\', '[', ']', '=', '\t', '\n' };

    // "model=" (any case, spaces allowed) as one of a provider line's items: after "(" or ",", never inside an address ("?model=").
    private static readonly Regex ModelEquals = new(@"(?i)(?<=[(,]\s*)model\s*=\s*", RegexOptions.CultureInvariant);

    /// <summary>
    /// Lines as BepInEx's File.ReadAllLines ends them: at "\r\n", "\n" or a lone "\r" (a file saved with old Mac line
    /// endings, or one stray CR, splits there for BepInEx too). Each keeps its own ending ("" for an unended last line).
    /// </summary>
    public static List<(string text, string end)> Lines(string text)
    {
        var lines = new List<(string, string)>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\n' && c != '\r') continue;
            bool crlf = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n';
            lines.Add((text[start..i], crlf ? "\r\n" : c.ToString()));
            if (crlf) i++;
            start = i + 1;
        }
        if (start < text.Length) lines.Add((text[start..], ""));
        return lines;
    }

    private static string Newline(string text) => text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : text.Contains('\r') ? "\r" : "\n";

    /// <summary>
    /// A section header to BepInEx, matched exactly as it matches one: <c>StartsWith("[")</c> and <c>EndsWith("]")</c> in the
    /// current culture (which looks past invisible characters, like a zero-width space pasted after the "]"), and the
    /// name as <c>Substring(1, n - 2)</c>, untrimmed. So a header BepInEx would stop at is found here too (re-review).
    /// </summary>
    private static bool Header(string trimmed, out string name)
    {
        name = "";
        if (trimmed.Length < 2 || !trimmed.StartsWith("[") || !trimmed.EndsWith("]")) return false;
        name = trimmed.Substring(1, trimmed.Length - 2);
        return true;
    }

    /// <summary>A group header as BepInEx reads one (see <see cref="Header"/>), for the settings writer.</summary>
    public static bool IsHeader(string trimmed, out string name) => Header(trimmed, out name);

    /// <summary>A name as a person might retype it: case, spaces, '_' and '-' don't count ("Api Key", "api_key" = ApiKey).</summary>
    public static string Norm(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            if (!char.IsWhiteSpace(c) && c != '_' && c != '-') sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>A line with its comment marks taken off ("#", ";" or "//", any number), and whether it had any.</summary>
    private static string Uncommented(string trimmed, out bool commented)
    {
        commented = false;
        string body = trimmed;
        while (true)
        {
            if (body.StartsWith("#", StringComparison.Ordinal) || body.StartsWith(";", StringComparison.Ordinal)) body = body[1..].TrimStart();
            else if (body.StartsWith("//", StringComparison.Ordinal)) body = body[2..].TrimStart();
            else return body;
            commented = true;
        }
    }

    /// <summary>
    /// Every line that holds a setting named one of <paramref name="names"/> (as <see cref="Norm"/> compares them), in any
    /// group, commented out or not, written "Name = value" or "Name: value". <c>Read</c> marks the one line BepInEx applies
    /// for a setting: not commented, in the group <paramref name="isSetting"/> binds it in, under its exact name, and the
    /// last such line (BepInEx applies them in order). The others are copies the mod's rewrite would drop (re-review W2).
    /// <c>Equals</c>: the name is followed by "=", as a commented-out setting is (a comment "# Name: ..." is prose).
    /// </summary>
    public static List<(int line, string section, string name, string value, bool commented, bool read, bool equals)> NamedLines(
        string text, IEnumerable<string> names, Func<string, string, bool> isSetting)
    {
        var wanted = new HashSet<string>(names.Select(Norm), StringComparer.Ordinal);
        var found = new List<(int, string, string, string, bool, bool, bool)>();
        var lastRead = new Dictionary<(string, string), int>();
        string section = "";
        var lines = Lines(text);
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].text.Trim();
            if (t.Length == 0) continue;
            if (Header(t, out var header)) { section = header; continue; }
            string body = Uncommented(t, out bool commented);
            // Uncommented: where BepInEx splits ("=", the first); otherwise whichever of "=" or ":" comes first.
            int cut = !commented && body.Contains('=') ? body.IndexOf('=') : body.IndexOfAny(new[] { '=', ':' });
            if (cut <= 0) continue;
            string name = body[..cut].Trim();
            if (!wanted.Contains(Norm(name))) continue;
            bool readable = !commented && body[cut] == '=' && isSetting(section, name);
            if (readable) lastRead[(section, name)] = found.Count;
            found.Add((i + 1, section, name, body[(cut + 1)..].Trim(), commented, false, body[cut] == '='));
        }
        foreach (int at in lastRead.Values) found[at] = found[at] with { Item6 = true };
        return found;
    }

    private static bool BadName(string name) => name.IndexOfAny(Forbidden) >= 0;

    /// <summary>Whether BepInEx would refuse this line as a "Key = value" line (a key with ' " \ [ ] or a tab before its '=').</summary>
    public static bool BreaksBepInEx(string line)
    {
        string t = line.Trim();
        if (t.Length == 0 || t.StartsWith("#") || Header(t, out _)) return false;
        int eq = t.IndexOf('=');
        return eq >= 0 && BadName(t[..eq].Trim());
    }

    /// <summary>
    /// The file with its voice Providers lines made readable by BepInEx: only a line BepInEx would refuse is changed.
    /// "model=" becomes "model: " and tabs become spaces; a line still refused after that is switched off with "# ", with
    /// a note in <paramref name="problems"/> (never quoting the line: it may hold a key). The same string comes back when
    /// nothing needed changing. Each line keeps its own ending.
    /// </summary>
    public static string FixProviderLines(string text, List<string> problems)
    {
        var lines = Lines(text);
        string section = "";
        bool changed = false;
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].text.Trim();
            if (Header(t, out var name)) { section = name.Trim(); continue; }
            if (section != "Voices" || !t.StartsWith("Providers") || !t.Contains('=')) continue;
            string after = t[(t.IndexOf('=') + 1)..].Trim();
            if (!after.StartsWith("[") || after.IndexOf(']') >= 0) continue; // no block, or all on one line (its key is "Providers")
            int n = 0;
            for (i++; i < lines.Count; i++)
            {
                string lt = lines[i].text.Trim();
                if (lt.StartsWith("]")) break; // the end of the block
                // the next group, or the first setting after a block never closed: read as such next
                if (Header(lt, out _) || SettingLine.IsMatch(lt)) { i--; break; }
                n++;
                bool last = !lt.StartsWith("#") && lt.EndsWith("]");
                if (!BreaksBepInEx(lines[i].text)) { if (last) break; continue; }
                string fixedLine = ModelEquals.Replace(lines[i].text.Replace('\t', ' '), "model: ");
                if (BreaksBepInEx(fixedLine))
                {
                    int lead = fixedLine.Length - fixedLine.TrimStart().Length;
                    string body = fixedLine[lead..].TrimEnd();
                    // The block's last line carries its closing "]": that stays, on a line of its own, or the block never ends.
                    if (last) body = body[..^1].TrimEnd() + (lines[i].end.Length > 0 ? lines[i].end : Newline(text)) + fixedLine[..lead] + "]";
                    fixedLine = fixedLine[..lead] + "# " + body;
                    problems.Add($"Voice provider line {n} was switched off: the settings loader can't read a line with ' \" \\ [ or ] before an '='. " +
                                 "Write model: instead of model=, and keep those characters after it.");
                }
                else problems.Add($"Voice provider line {n} was rewritten so the settings loader can read it (model= is now model:).");
                lines[i] = (fixedLine, lines[i].end);
                changed = true;
                if (last) break;
            }
        }
        if (!changed) return text;
        var sb = new StringBuilder(text.Length + 16);
        foreach (var (line, end) in lines) sb.Append(line).Append(end);
        return sb.ToString();
    }

    /// <summary>
    /// The lines between "Providers = [" and "]" in the [Voices] group, exactly as written; null if there's no block.
    /// <paramref name="closed"/> is false when the block runs into the next group or the end of the file without its "]":
    /// cut short, or with the settings after it read as voice lines, so it isn't one to use over a list already read.
    /// </summary>
    public static List<string>? ProviderBlock(string text, out bool closed)
    {
        closed = false;
        var lines = Lines(text);
        string section = "";
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].text.Trim();
            if (Header(t, out var name)) { section = name.Trim(); continue; }
            if (section != "Voices" || !t.StartsWith("Providers") || !t.Contains('=')) continue;
            string after = t[(t.IndexOf('=') + 1)..].Trim();
            var inner = new List<string>();
            if (!after.StartsWith("[")) { if (after.Length > 0) inner.Add(after); closed = true; return inner; }
            after = after[1..];
            int close = after.LastIndexOf(']');
            if (close >= 0) { inner.Add(after[..close]); closed = true; return inner; } // all on one line
            if (after.Trim().Length > 0) inner.Add(after);
            for (i++; i < lines.Count; i++)
            {
                string raw = lines[i].text, lt = raw.Trim();
                if (lt.StartsWith("]")) { closed = true; return inner; }
                // Never closed: it ends at the next group, or at the first setting after it ("Volume = 80"), without the
                // settings' own comments and blank lines before that (re-review: it used to take in the rest of [Voices]).
                if (Header(lt, out _) || SettingLine.IsMatch(lt)) return Unclosed(inner);
                if (!lt.StartsWith("#") && lt.EndsWith("]")) { inner.Add(raw.TrimEnd()[..^1]); closed = true; return inner; }
                inner.Add(raw.TrimEnd());
            }
            return Unclosed(inner);
        }
        return null;
    }

    /// <summary>
    /// Where the voice Providers block sits, as 0-based line numbers (<see cref="Lines"/>): its "Providers =" line through
    /// its "]", or, never closed, up to the line before the next group or setting (the rule <see cref="ProviderBlock"/>
    /// uses). Null when there's none.
    /// </summary>
    public static (int start, int end)? ProviderRange(string text)
    {
        var lines = Lines(text);
        string section = "";
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].text.Trim();
            if (Header(t, out var name)) { section = name.Trim(); continue; }
            if (section != "Voices" || !t.StartsWith("Providers") || !t.Contains('=')) continue;
            string after = t[(t.IndexOf('=') + 1)..].Trim();
            if (!after.StartsWith("[") || after[1..].LastIndexOf(']') >= 0) return (i, i); // no list, or all on one line
            for (int j = i + 1; j < lines.Count; j++)
            {
                string lt = lines[j].text.Trim();
                if (lt.StartsWith("]")) return (i, j);
                if (Header(lt, out _) || SettingLine.IsMatch(lt)) return (i, j - 1);
                if (!lt.StartsWith("#") && lt.EndsWith("]")) return (i, j);
            }
            return (i, lines.Count - 1);
        }
        return null;
    }

    /// <summary>"Name = value" with a plain name: a setting, never a voice provider line (those are "(…)", or "# (…)").</summary>
    private static readonly Regex SettingLine = new(@"^[A-Za-z][A-Za-z0-9]*\s*=");

    /// <summary>
    /// A block that was never closed, without the trailing blank lines and plain comments (the next setting's own
    /// description). Any other line is a provider (one written without brackets too) and stays; a switched-off provider
    /// ("# (…)") stays as well. The file itself keeps every line either way: the mod only writes lines it changed.
    /// </summary>
    private static List<string> Unclosed(List<string> inner)
    {
        while (inner.Count > 0)
        {
            string last = inner[^1].Trim();
            string body = Uncommented(last, out bool comment);
            if (last.Length > 0 && (!comment || body.StartsWith("("))) break;
            inner.RemoveAt(inner.Count - 1);
        }
        return inner;
    }

    /// <summary>
    /// Every name written before an '=' or ':' on a line that BepInEx doesn't read as one of the settings
    /// (<paramref name="isSetting"/> says which "[Section] Key" pairs are): commented out ("# ApiKey = ..."), in another
    /// group, typed in another case (BepInEx matches case), or written "Key: value". Compared ignoring case. A setting
    /// whose line is gone but whose name is here was moved, not deleted: resetting it would rewrite the file and lose
    /// that copy (audit step 5 review).
    /// </summary>
    public static HashSet<string> StrayNames(string text, Func<string, string, bool> isSetting)
    {
        // Names as Norm makes them: "Talk Key" and "talk_key" are TalkKey; ";" and "//" comment a line out too (re-review).
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string section = "";
        foreach (var (line, _) in Lines(text))
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            if (Header(t, out var name)) { section = name; continue; }
            string body = Uncommented(t, out bool comment);
            int cut = body.IndexOfAny(new[] { '=', ':' });
            if (cut <= 0) continue;
            string key = body[..cut].Trim();
            if (key.Length == 0) continue;
            if (!comment && body[cut] == '=' && isSetting(section, key)) continue; // a setting where BepInEx reads it
            names.Add(Norm(key));
        }
        return names;
    }

    /// <summary>Every "[Section] Key" in the file, matched as BepInEx matches them (section names as written, keys trimmed, case-sensitive), and every section.</summary>
    public static HashSet<(string section, string key)> KeysIn(string text, out HashSet<string> sections)
    {
        var keys = new HashSet<(string, string)>();
        sections = new HashSet<string>(StringComparer.Ordinal);
        string section = "";
        foreach (var (line, _) in Lines(text))
        {
            string t = line.Trim();
            if (t.Length == 0 || t.StartsWith("#")) continue;
            if (Header(t, out var name)) { section = name; sections.Add(name); continue; }
            int eq = t.IndexOf('=');
            if (eq >= 0) keys.Add((section, t[..eq].Trim()));
        }
        return keys;
    }

    /// <summary>
    /// The first line (1-based) BepInEx would throw on: a key with a forbidden character, or the header of a section with
    /// one (or with spaces at its edges) that a key line follows. 0 if there's none.
    /// </summary>
    public static int FirstBadLine(string text)
    {
        var lines = Lines(text);
        string section = "";
        int sectionLine = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].text.Trim();
            if (t.Length == 0 || t.StartsWith("#")) continue;
            if (Header(t, out var name)) { section = name; sectionLine = i + 1; continue; }
            int eq = t.IndexOf('=');
            if (eq < 0) continue;
            if (sectionLine > 0 && (BadName(section) || section != section.Trim())) return sectionLine;
            if (BadName(t[..eq].Trim())) return i + 1;
        }
        return 0;
    }

    /// <summary>What the mod writes now, for <see cref="Refresh"/>: the file's header, each group's banner, each setting's explanation.</summary>
    public sealed class Template
    {
        /// <summary>The comment lines at the top of the file.</summary>
        public List<string> Header = new();
        /// <summary>Per group: the comment lines above its [header].</summary>
        public Dictionary<string, List<string>> Banners = new(StringComparer.Ordinal);
        /// <summary>Per setting (group, key): the comment lines above its line, with "# Default:" as it should read for its value.</summary>
        public Dictionary<(string section, string key), List<string>> Blocks = new();
        /// <summary>Settings an earlier version wrote that nothing reads now: a note goes above them (never removed).</summary>
        public HashSet<(string section, string key)> Retired = new();
    }

    /// <summary>The note above a setting nothing reads any more.</summary>
    public const string RetiredNote = "# No longer read by the mod (a newer version replaced it). You can delete this line.";

    /// <summary>
    /// A comment block the way it's compared (FB-23): each line's trailing spaces gone, the "# Default:" lines left out
    /// (they depend on the player's own value), one line after another.
    /// </summary>
    public static string Normalized(IEnumerable<string> block) =>
        string.Join("\n", block.Select(l => l.TrimEnd()).Where(l => !l.TrimStart().StartsWith("# Default:", StringComparison.Ordinal)));

    /// <summary>
    /// The file with the mod's own explanations brought up to date (FB-23: an update keeps every setting and refreshes the
    /// file). Only a comment block the mod itself wrote in an earlier version (<paramref name="wroteBefore"/>: "HEADER",
    /// "[Group]" or "Group|Key", with the block <see cref="Normalized"/>), or the current one with a "# Default:" that no
    /// longer fits, is replaced by today's; a setting nothing reads gets <see cref="RetiredNote"/> above it. Every other
    /// line stays exactly as it is: the player's own comments (a block with one of their lines in it never matches), every
    /// value and key, the voice list, the line endings. Checked before it's returned: the lines that aren't comments must
    /// come out the same, in the same order, or the text is given back unchanged. <paramref name="changed"/> says what was
    /// refreshed. A voice list with no closing ']' leaves the whole file as it is.
    /// </summary>
    public static string Refresh(string text, Template now, Func<string, string, bool> wroteBefore, out List<string> changed) =>
        Refresh(text, now, wroteBefore, out changed, out _);

    /// <param name="refused">The file was left alone because its voice list never closes (so the refresh isn't done: audit B5).</param>
    public static string Refresh(string text, Template now, Func<string, string, bool> wroteBefore, out List<string> changed, out bool refused)
    {
        changed = new List<string>();
        refused = false;
        var lines = Lines(text);
        string nl = Newline(text);
        bool IsComment(int i) => lines[i].text.TrimStart().StartsWith("#");
        // Each replacement: from line `at`, `count` lines out, these in (`count` 0 = an insertion before `at`).
        var edits = new List<(int at, int count, List<string> lines)>();
        int BlockStart(int end) { int b = end; while (b > 0 && IsComment(b - 1)) b--; return b; } // the comment lines just above line `end`
        bool Ours(string kind, List<string> block, List<string> current) =>
            Normalized(block) == Normalized(current) || wroteBefore(kind, Normalized(block));
        bool Differs(List<string> block, List<string> current) =>
            !block.Select(l => l.TrimEnd()).SequenceEqual(current.Select(l => l.TrimEnd()));
        List<string> Text(int from, int to) => lines.Skip(from).Take(to - from).Select(l => l.text).ToList();

        // The file's own header: the comment lines it starts with.
        int top = 0;
        while (top < lines.Count && IsComment(top)) top++;
        if (top > 0 && top < lines.Count && now.Header.Count > 0)
        {
            var block = Text(0, top);
            if (Differs(block, now.Header) && Ours("HEADER", block, now.Header)) { edits.Add((0, top, now.Header)); changed.Add("the header"); }
        }

        string section = "";
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].text.Trim();
            if (t.Length == 0 || t.StartsWith("#")) continue;
            if (Header(t, out var name))
            {
                section = name;
                int b = Math.Max(BlockStart(i), top); // never the file's own header
                if (b < i && now.Banners.TryGetValue(name, out var banner))
                {
                    var block = Text(b, i);
                    if (Differs(block, banner) && Ours("[" + name + "]", block, banner)) { edits.Add((b, i - b, banner)); changed.Add("[" + name + "]"); }
                }
                continue;
            }
            int eq = t.IndexOf('=');
            if (eq < 0) continue;
            string key = t[..eq].Trim();
            if (section == "Voices" && key == "Providers")
            {
                // The voice list is never touched: skipped to its closing ']' (none: the whole file is left as it is).
                if (!t[(eq + 1)..].Trim().StartsWith("[") || t.Contains(']')) continue;
                // It ends where the mod reads it as ending (ProviderRange): a "]" line, or a provider line ending in "]" (audit B5).
                int close = -1;
                for (int j = i + 1; j < lines.Count; j++)
                {
                    string lt = lines[j].text.Trim();
                    // In ProviderRange's order: a "[Group]" header ends in "]" too, and ends the list unclosed (review R3).
                    if (lt.StartsWith("]")) { close = j; break; }
                    if (Header(lt, out _) || SettingLine.IsMatch(lt)) break;
                    if (!lt.StartsWith("#") && lt.EndsWith("]")) { close = j; break; }
                }
                if (close < 0) { changed.Clear(); refused = true; return text; }
                i = close;
                continue;
            }
            if (now.Blocks.TryGetValue((section, key), out var current))
            {
                int b = Math.Max(BlockStart(i), top);
                var block = Text(b, i);
                if (block.Count > 0 && Differs(block, current) && Ours(section + "|" + key, block, current))
                {
                    edits.Add((b, i - b, current));
                    changed.Add(key);
                }
            }
            else if (now.Retired.Contains((section, key)) && (i == 0 || lines[i - 1].text.Trim() != RetiredNote))
            {
                edits.Add((i, 0, new List<string> { RetiredNote }));
                changed.Add(key + " (no longer read)");
            }
        }
        if (edits.Count == 0) return text;

        var sb = new StringBuilder();
        int next = 0;
        foreach (var (at, count, add) in edits.OrderBy(e => e.at))
        {
            for (; next < at; next++) sb.Append(lines[next].text).Append(lines[next].end);
            foreach (var l in add) sb.Append(l).Append(nl);
            next = at + count;
        }
        for (; next < lines.Count; next++) sb.Append(lines[next].text).Append(lines[next].end);
        string result = sb.ToString();

        // Nothing but comments changed: every other line the same, in the same order (or nothing is changed at all).
        static List<string> Kept(string s) => Lines(s).Select(l => l.text).Where(l => !l.TrimStart().StartsWith("#")).ToList();
        if (!Kept(text).SequenceEqual(Kept(result)) || FirstBadLine(result) != FirstBadLine(text)) { changed.Clear(); return text; }
        return result;
    }
}
