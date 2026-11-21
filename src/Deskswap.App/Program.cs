using System.Globalization;
using Deskswap.Core;
using Deskswap.Platform;

namespace Deskswap.App;

internal static class Program
{
    /// <summary>Command-line switch of an instance started by "Restart as administrator", followed by the old one's id.</summary>
    public const string RestartSwitch = "--restarted-from";

    [STAThread]
    private static int Main(string[] args)
    {
        // Watchdog mode: no UI; wait for the main process to exit, then show any window it left hidden (design §8.2).
        if (args.Length == 3 && args[0] == Watchdog.Switch
            && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int watchedProcessId))
        {
            int restored = Watchdog.Run(watchedProcessId, args[2]);
            if (restored > 0)
            {
                Log.Info($"Watchdog restored {restored} hidden windows.");
            }

            return 0;
        }

        // Restarted as administrator: let the old instance finish exiting (it shows every window) before starting.
        if (args.Length == 2 && args[0] == RestartSwitch
            && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int previousProcessId))
        {
            WaitForExit(previousProcessId);
        }

        using var singleInstance = new Mutex(initiallyOwned: true, $@"Local\{ProductInfo.Name}.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            return 0;
        }

        ApplicationConfiguration.Initialize();

        // Must be set before any control exists on this thread.
        TrayApplicationContext? context = null;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            if (context is null)
            {
                Log.Error("Unhandled exception during startup.", e.Exception);
            }
            else
            {
                context.OnUnhandledException(e.Exception);
            }
        };

        using (context = new TrayApplicationContext())
        {
            Application.Run(context);
        }

        return 0;
    }

    private static void WaitForExit(int processId)
    {
        try
        {
            using var previous = System.Diagnostics.Process.GetProcessById(processId);
            previous.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }
}
