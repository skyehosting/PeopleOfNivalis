using System;
using System.Linq;
using NivalisAIChat.UI;

namespace NivalisAIChat.Tests;

/// <summary>0.3.4 CF-07: the on-screen keyboard you type with on a controller.</summary>
internal static partial class Program
{
    private static void KeyboardLayoutTests()
    {
        Console.WriteLine("-- 0.3.4 CF-07: the on-screen keyboard");
        Check(new[] { false, true }.All(sym => KeyboardLayout.Rows(sym).All(r => r.Sum(k => k.Span) == KeyboardLayout.Width)),
              "every row of both layers fills the same 11 places");
        Check(KeyboardLayout.At(false, 0, 0).Output == "q" && KeyboardLayout.At(true, 0, 0).Output == "1", "letters, and numbers and signs on the other layer");

        Check(KeyboardLayout.Move(false, (0, 10), +1, 0) == (0, 0) && KeyboardLayout.Move(false, (0, 0), -1, 0) == (0, 10), "left/right go round the end of a row");
        // FB-51: a key for what LB does, at the right end of the top row, on both layers.
        Check(KeyboardLayout.At(false, 0, 10).Action == KeyAction.Backspace && KeyboardLayout.At(true, 0, 10).Action == KeyAction.Backspace
              && KeyboardLayout.Type("hey", KeyboardLayout.At(false, 0, 10), false) == "he" && KeyboardLayout.Type("", KeyboardLayout.At(false, 0, 10), false) == "",
              "del at the top right of both layers takes the last character away");
        Check(KeyboardLayout.Move(false, (0, 0), 0, -1) == (0, 0) && KeyboardLayout.Move(false, (3, 1), 0, +1) == (3, 1), "up/down stop at the top and bottom");
        Check(KeyboardLayout.Move(false, (2, 4), 0, +1) == (3, 1), "down from the middle letters lands on the space bar");
        Check(KeyboardLayout.Move(false, (2, 0), 0, +1) == (3, 0) && KeyboardLayout.Move(false, (2, 9), 0, +1) == (3, 3), "...from the left edge on the layer key, from the right on close");
        Check(KeyboardLayout.Move(false, (3, 1), 0, -1).row == 2 && KeyboardLayout.At(false, 2, KeyboardLayout.Move(false, (3, 1), 0, -1).index).Output == "b",
              "up from the space bar lands on a letter above its middle");
        Check(KeyboardLayout.Move(false, (3, 3), -1, 0) == (3, 2), "the bottom row moves key by key too (close to Send)");

        var a = KeyboardLayout.At(false, 1, 0);
        Check(KeyboardLayout.Type("hi ", a, shift: false) == "hi a" && KeyboardLayout.Type("", a, shift: true) == "A", "A types the key; LT held makes a capital");
        Check(KeyboardLayout.Type("hi", KeyboardLayout.At(false, 3, 1), false) == "hi " && KeyboardLayout.Type("hi", KeyboardLayout.At(false, 3, 2), false) == "hi",
              "space adds a space; Send doesn't change the text (it sends it)");
        Check(KeyboardLayout.Backspace("hey") == "he" && KeyboardLayout.Backspace("") == "", "LB/L1 deletes the last character (nothing to delete: nothing happens)");
        Check(KeyboardLayout.Label(a, true) == "A" && KeyboardLayout.Label(KeyboardLayout.At(false, 3, 1), true) == "space", "keys show capitals while shift is held");
        Check(KeyboardLayout.At(false, 3, 0).Action == KeyAction.Layer && KeyboardLayout.At(true, 3, 0).Label == "ABC", "the layer key switches between letters and 123");
    }
}
