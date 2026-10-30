using System;
using System.Collections.Generic;
using System.Linq;

namespace NivalisAIChat.UI;

/// <summary>What an on-screen key does when it's typed (CF-07).</summary>
/// <summary>Backspace (FB-51): the last character goes, as LB/L1 does.</summary>
public enum KeyAction { Char, Space, Send, Close, Layer, Backspace }

/// <summary>One key: what it shows, what it types, what it does, and how many of the row's 11 places it takes.</summary>
public sealed record PadKey(string Label, string Output, KeyAction Action, int Span = 1);

/// <summary>
/// CF-07: the on-screen keyboard you type with on a controller: letters (or numbers and signs) in rows of 10 places, and a
/// bottom row with switching layers, Space, Send and closing the keyboard. Moving with the d-pad or a stick goes key by key
/// across a row (round the end) and straight up or down to the key under the same place. LB/L1 deletes and LT/L2 held
/// makes a letter a capital (the runtime does those). No Unity here.
/// </summary>
public static class KeyboardLayout
{
    /// <summary>Places in a row: ten keys and, on the top row, "del" at its right end (FB-51: a key for what LB does).</summary>
    public const int Width = 11;

    private static List<PadKey> Chars(string keys) => keys.Select(ch => new PadKey(ch.ToString(), ch.ToString(), KeyAction.Char)).ToList();

    /// <summary>A row of keys with "del" at its right end.</summary>
    private static List<PadKey> WithDelete(List<PadKey> keys) { keys.Add(new PadKey("del", "", KeyAction.Backspace)); return keys; }

    private static List<PadKey> Bottom(string layer) => new()
    {
        new PadKey(layer, "", KeyAction.Layer, 2),
        new PadKey("space", " ", KeyAction.Space, 5),
        new PadKey("Send", "", KeyAction.Send, 2),
        new PadKey("close", "", KeyAction.Close, 2),
    };

    private static readonly IReadOnlyList<IReadOnlyList<PadKey>> LetterRows = new List<IReadOnlyList<PadKey>>
    {
        WithDelete(Chars("qwertyuiop")), Chars("asdfghjkl'-"), Chars("zxcvbnm,.?!"), Bottom("123"),
    };

    private static readonly IReadOnlyList<IReadOnlyList<PadKey>> SymbolRows = new List<IReadOnlyList<PadKey>>
    {
        WithDelete(Chars("1234567890")), Chars("-/:;()$&@\"_"), Chars("!?,.'#%*+=~"), Bottom("ABC"),
    };

    /// <summary>The rows shown: letters, or numbers and signs.</summary>
    public static IReadOnlyList<IReadOnlyList<PadKey>> Rows(bool symbols) => symbols ? SymbolRows : LetterRows;

    /// <summary>The key at (row, key index).</summary>
    public static PadKey At(bool symbols, int row, int index)
    {
        var rows = Rows(symbols);
        row = Math.Clamp(row, 0, rows.Count - 1);
        return rows[row][Math.Clamp(index, 0, rows[row].Count - 1)];
    }

    /// <summary>The place (0-9) a key's middle sits over, for moving straight up or down.</summary>
    private static double Middle(IReadOnlyList<PadKey> row, int index)
    {
        int start = row.Take(index).Sum(k => k.Span);
        return start + row[index].Span / 2.0;
    }

    /// <summary>The key a move lands on: dx = -1/+1 across the row (round the end), dy = -1/+1 up or down (stops at the edge).</summary>
    public static (int row, int index) Move(bool symbols, (int row, int index) at, int dx, int dy)
    {
        var rows = Rows(symbols);
        int r = Math.Clamp(at.row, 0, rows.Count - 1);
        int i = Math.Clamp(at.index, 0, rows[r].Count - 1);
        if (dx != 0)
        {
            int n = rows[r].Count;
            i = ((i + Math.Sign(dx)) % n + n) % n;
        }
        if (dy != 0)
        {
            int nr = Math.Clamp(r + Math.Sign(dy), 0, rows.Count - 1);
            if (nr != r)
            {
                double mid = Middle(rows[r], i);
                int best = 0;
                double bestGap = double.MaxValue;
                for (int k = 0; k < rows[nr].Count; k++)
                {
                    double gap = Math.Abs(Middle(rows[nr], k) - mid);
                    if (gap < bestGap - 1e-9) { bestGap = gap; best = k; }
                }
                r = nr;
                i = best;
            }
        }
        return (r, i);
    }

    /// <summary>The text after typing this key (a letter as a capital while shift is held); Send, Close and Layer leave it as it is.</summary>
    public static string Type(string text, PadKey key, bool shift)
    {
        text ??= "";
        return key.Action switch
        {
            KeyAction.Char => text + (shift ? key.Output.ToUpperInvariant() : key.Output),
            KeyAction.Space => text + " ",
            KeyAction.Backspace => Backspace(text),
            _ => text,
        };
    }

    /// <summary>LB/L1: the last character goes.</summary>
    public static string Backspace(string text) => string.IsNullOrEmpty(text) ? "" : text[..^1];

    /// <summary>The label a key shows (letters as capitals while shift is held).</summary>
    public static string Label(PadKey key, bool shift) => key.Action == KeyAction.Char && shift ? key.Label.ToUpperInvariant() : key.Label;
}
