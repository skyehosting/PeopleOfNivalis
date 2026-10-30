using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace NivalisAIChat.Tests;

/// <summary>
/// Audit step 7 (7d): every test folder goes under one folder for this run, <c>%TEMP%\nivalis-run-&lt;pid&gt;-&lt;guid&gt;</c>,
/// deleted when the run ends (all green, a failed check, or an exception), so a run leaves nothing in %TEMP%. Nothing
/// outside it is touched, another run's folders included.
/// </summary>
internal static class TestRoot
{
    public static readonly string Folder = Path.Combine(Path.GetTempPath(), $"nivalis-run-{Environment.ProcessId}-{Guid.NewGuid():N}");

    private static int _cleaned;

    static TestRoot()
    {
        SweepEndedRuns();
        Directory.CreateDirectory(Folder);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanUp(); // Environment.Exit skips finally blocks
    }

    /// <summary>
    /// Folders of runs that ended without cleaning up (killed, crashed: Environment.Exit's handler never ran): their process
    /// is gone. A run still going, and anything that isn't a run's folder, is left alone; a pid since reused only means a
    /// folder is kept a little longer (review of a0fa75f).
    /// </summary>
    private static void SweepEndedRuns()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "nivalis-run-*"))
            {
                var parts = Path.GetFileName(dir).Split('-'); // nivalis, run, <pid>, <guid>
                if (parts.Length != 4 || parts[3].Length != 32 || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                    || pid == Environment.ProcessId || Running(pid)) continue;
                try { Directory.Delete(dir, recursive: true); } catch { /* still in use: next time */ }
            }
        }
        catch { /* %TEMP% unreadable: nothing to sweep */ }
    }

    private static bool Running(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; } // no such process
        catch { return true; } // can't tell: keep its folder
    }

    /// <summary>A new folder's path under this run's root (not created: each test makes its own).</summary>
    public static string NewFolder(string name) => Path.Combine(Folder, name + "-" + Guid.NewGuid().ToString("N"));

    /// <summary>Deletes this run's root, once. A file a background save still has open gets a few short retries.</summary>
    public static void CleanUp()
    {
        if (Interlocked.Exchange(ref _cleaned, 1) == 1) return;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
        Console.WriteLine("  (couldn't delete the test folder " + Folder + ")");
    }
}
