using System.Diagnostics;
using System.Globalization;

namespace Deskswap.Platform;

/// <summary>Shows windows left hidden by a previous run (design §8, steps 2 and 3).</summary>
public static class WindowRecovery
{
    /// <summary>
    /// Shows every journaled window that still exists (same handle, same process instance) and clears the journal.
    /// </summary>
    /// <param name="journal">The journal.</param>
    /// <returns>The number of windows shown.</returns>
    public static int RestoreAll(HiddenWindowJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var startTimes = new Dictionary<uint, long>();
        int restored = 0;
        foreach (var entry in journal.Entries)
        {
            if (WindowInspector.IsAlive(entry.Window, startTimes)
                && !WindowInspector.IsVisible((nint)entry.Handle)
                && WindowOperations.Show(entry.Window))
            {
                restored++;
            }
        }

        journal.Clear();
        return restored;
    }
}

/// <summary>
/// A helper process that outlives Deskswap: when Deskswap exits, it shows any window still in the journal. On a
/// clean exit the journal is already empty, so it does nothing.
/// </summary>
public static class Watchdog
{
    /// <summary>The command-line switch that starts the executable in watchdog mode.</summary>
    public const string Switch = "--watchdog";

    /// <summary>Starts the watchdog for the current process.</summary>
    /// <param name="executablePath">The Deskswap executable.</param>
    /// <param name="journalPath">The journal file to recover from.</param>
    /// <returns>The watchdog process, or <see langword="null"/> if it could not be started.</returns>
    public static Process? Start(string executablePath, string journalPath)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(Switch);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(journalPath);
        return Process.Start(startInfo);
    }

    /// <summary>Waits for the watched process to exit, then restores journaled windows.</summary>
    /// <param name="watchedProcessId">The Deskswap process to watch.</param>
    /// <param name="journalPath">The journal file.</param>
    /// <returns>The number of windows restored.</returns>
    public static int Run(int watchedProcessId, string journalPath)
    {
        try
        {
            using var watched = Process.GetProcessById(watchedProcessId);
            watched.WaitForExit();
        }
        catch (ArgumentException)
        {
            // Already gone: recover right away.
        }

        return WindowRecovery.RestoreAll(new HiddenWindowJournal(journalPath));
    }
}
