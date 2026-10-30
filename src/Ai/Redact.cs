using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NivalisAIChat.Ai;

/// <summary>
/// Settings values made safe to log or show: an address without anything secret in it, and a value that looks like an
/// API key (pasted into the wrong setting) replaced by a stand-in. Keys themselves are never logged at all.
/// </summary>
public static class Redact
{
    /// <summary>How the providers' keys start (OpenAI and Anthropic, ElevenLabs, Google, Voyage, Groq, xAI, Hugging Face, Replicate).</summary>
    private static readonly string[] KeyPrefixes = { "sk-", "sk_", "AIza", "pa-", "gsk_", "xai-", "hf_", "r8_" };

    /// <summary>
    /// An endpoint as scheme://host[:port]/path, without a user name, password, query or fragment, and with any part of
    /// the path that looks like a key hidden. Something that isn't an http(s) address at all comes back as "(not shown)".
    /// </summary>
    public static string Url(string? url)
    {
        string t = (url ?? "").Trim();
        if (t.Length == 0) return "";
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            return "(not shown)";
        string path = string.Join("/", u.AbsolutePath.Split('/').Select(part => LooksLikeKey(Uri.UnescapeDataString(part)) ? "(hidden)" : part));
        return $"{u.Scheme}://{u.Authority}{path}";
    }

    /// <summary>
    /// Whether a value looks like an API key rather than a model or a name: one of the providers' key prefixes (whatever
    /// follows it, dashes and underscores included), or a run of 20 or more letters and digits mixed together (model
    /// names are short words split by dashes).
    /// </summary>
    public static bool LooksLikeKey(string? value)
    {
        string v = (value ?? "").Trim();
        if (v.Length >= 20 && KeyPrefixes.Any(p => v.StartsWith(p, StringComparison.Ordinal) || (p == "sk-" && v.StartsWith("SK-", StringComparison.Ordinal))))
            return true;
        int run = 0;
        bool letter = false, digit = false;
        foreach (char c in v)
        {
            bool isLetter = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z', isDigit = c is >= '0' and <= '9';
            if (isLetter || isDigit)
            {
                run++;
                letter |= isLetter;
                digit |= isDigit;
                if (run >= 20 && letter && digit) return true;
            }
            else { run = 0; letter = digit = false; }
        }
        return false;
    }

    /// <summary>
    /// Whether an item (a voice in the Providers block, say) is plainly a key: a provider's key prefix and 20 or more
    /// characters, "Bearer ...", or one long hex string (32+). Unlike <see cref="LooksLikeKey"/> it has no run rule, so
    /// ElevenLabs voice ids (20 mixed letters and digits) and short names like Kokoro's "hf_alpha" pass.
    /// </summary>
    public static bool PlainlyKey(string? value)
    {
        string v = (value ?? "").Trim();
        return (v.Length >= 20 && KeyPrefixes.Any(p => v.StartsWith(p, StringComparison.Ordinal)))
               || v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
               || (v.Length >= 32 && v.All(Uri.IsHexDigit));
    }

    /// <summary>The value as it is, or a stand-in ("hidden-1a2b3c4d") when it looks like a key: the same value always gives the same stand-in.</summary>
    public static string Value(string? value)
    {
        string v = (value ?? "").Trim();
        if (!LooksLikeKey(v)) return v;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(v));
        return "hidden-" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    // A key-shaped token inside other text: a provider prefix and at least 12 more characters, or 32 or more letters and
    // digits with both in it (32 is above request ids and the like, so error messages stay readable).
    private static readonly Regex KeyToken = new(
        @"(?<![A-Za-z0-9_\-])(?:(?:sk-|sk_|AIza|pa-|gsk_|xai-|hf_|r8_)[A-Za-z0-9_\-]{12,}|(?=[A-Za-z0-9]*[0-9])(?=[A-Za-z0-9]*[A-Za-z])[A-Za-z0-9]{32,})(?![A-Za-z0-9_\-])",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Text a provider sent back (an error, a response), made safe to log or show: each of <paramref name="values"/> that
    /// looks like a key (a model setting holding one, which providers repeat in "model X does not exist") becomes its
    /// stand-in, and any other key-shaped token is hidden.
    /// </summary>
    /// <summary>
    /// The configured key itself, whatever its shape, hidden in text a provider sent back (the full audit, A-003: a proxy that
    /// repeats a short or unusual key in its error would otherwise put it on screen and in the log).
    /// </summary>
    public static string Hide(string? text, string? secret)
    {
        string t = text ?? "", k = (secret ?? "").Trim();
        return k.Length >= 4 ? t.Replace(k, "(hidden)", StringComparison.Ordinal) : t;
    }

    public static string Text(string? text, params string?[] values)
    {
        string t = text ?? "";
        if (t.Length == 0) return t;
        foreach (var raw in values)
        {
            string v = (raw ?? "").Trim();
            if (v.Length > 0 && LooksLikeKey(v)) t = t.Replace(v, Value(v), StringComparison.Ordinal);
        }
        return KeyToken.Replace(t, "(hidden)");
    }
}
