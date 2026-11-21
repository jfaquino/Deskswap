using System.Diagnostics;
using System.Runtime;

namespace Deskswap.App;

/// <summary>
/// Hands memory back to Windows after a burst of work, such as an overview session (design §12). Deskswap is a
/// long-running tray app that is idle most of the time; opening the overview allocates several megabytes of images,
/// mostly on the large object heap, which the garbage collector rarely compacts on its own when the app goes idle.
/// One compacting collection after the burst keeps the footprint flat over days.
/// </summary>
internal static class MemoryTrim
{
    /// <summary>Collects, compacts the large object heap, and logs the result.</summary>
    public static void Run()
    {
        long before = Environment.WorkingSet;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        using var process = Process.GetCurrentProcess();
        Log.Info(FormattableString.Invariant(
            $"Memory after overview: managed {GC.GetTotalMemory(false) / 1048576.0:0.0} MB, private {process.PrivateMemorySize64 / 1048576.0:0.0} MB, working set {before / 1048576.0:0.0} → {process.WorkingSet64 / 1048576.0:0.0} MB."));
    }
}
