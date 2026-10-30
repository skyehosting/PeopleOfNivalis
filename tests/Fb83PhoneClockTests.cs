using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace NivalisAIChat.Tests;

/// <summary>FB-83: the phone's time, bars and battery move while it's out (each game minute), not only when the screen is redrawn.</summary>
internal static partial class Program
{
    private static void Fb83PhoneClockTests([CallerFilePath] string here = "")
    {
        Console.WriteLine("-- FB-83: the phone's status bar keeps time while it's out");
        string src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "src");
        string Read(string f) => File.ReadAllText(Path.Combine(src, f));
        string rt = Read("ModRuntime.Phone.cs"), win = Read(Path.Combine("UI", "PhoneWindow.cs")), screens = Read(Path.Combine("UI", "PhoneWindow.Screens.cs"));

        // A method's body by its signature; "" when it's missing (so a Check fails, never throws: review FB83-1).
        static string Body(string text, string signature)
        {
            int at = text.IndexOf(signature, StringComparison.Ordinal);
            int end = at >= 0 ? text.IndexOf("\n    }", at, StringComparison.Ordinal) : -1;
            return end > at ? text[at..end] : "";
        }
        Check(Body(rt, "private static void TickPhoneWindow()").Contains("TickPhoneClock();"),
              "FB-83: every frame the phone's tick looks at its clock");
        string body = Body(rt, "private static void TickPhoneClock()");
        Check(body.Contains("if (!_phoneOpen || !PhoneUi.Built) return;") && body.Contains("if (_clockShown == (now, _bars, _inApartment == 2)) return;")
              && body.Contains("PhoneUi.RenderClock();") && !body.Contains("RefreshPhone()") && !body.Contains("PhoneUi.Render()"),
              "...only while it's out, only when the game minute, the bars or home change, and only the status bar (the screen isn't redrawn)");
        string status = Body(rt, "private static (int hour, int minute) StatusNow(PhoneView v)");
        Check(status.Contains("v.Time = ") && status.Contains("v.Clock = ") && status.Contains("v.Date = ") && status.Contains("v.Bars = _bars;")
              && status.Contains("v.Battery = Phone.Battery.Percent(hour, minute);") && status.Contains("v.Carrier = ")
              && status.Contains("_clockShown = (hour * 60 + minute, _bars, _inApartment == 2);")
              && rt.Contains("var (hour, _) = StatusNow(v);"),
              "...the time, date, bars, carrier and battery are worked out in one place, by a full redraw and the minute's alike");
        string rcBody = Body(win, "public void RenderClock()");
        Check(rcBody.Contains("RenderStatus();") && rcBody.Contains("_homeClock.text = View.Clock;") && rcBody.Contains("_homeDate.text = View.Date;")
              && screens.Contains("_homeClock = clock;") && screens.Contains("_homeClock = _homeDate = null; // FB-83"),
              "...the home screen's big clock and date move too, and are let go when the screen is redrawn (never a destroyed label)");

        // The battery a minute apart, through the day: it moves (so a minute's redraw shows it), and stays a percentage.
        bool moved = false, inRange = true;
        int last = Phone.Battery.Percent(8, 0);
        for (int m = 8 * 60 + 1; m < 8 * 60 + 24 * 60; m++)
        {
            int p = Phone.Battery.Percent(m / 60 % 24, m % 60);
            if (p != last) moved = true;
            if (p < 1 || p > 100) inRange = false;
            last = p;
        }
        Check(moved && inRange, "...and the battery does change through the day, always 1-100%");
    }
}
