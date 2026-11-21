namespace Deskswap.Core.Tests;

/// <summary>Shared fixtures and invariant checks for <see cref="DesktopPool"/> tests.</summary>
internal static class PoolTestKit
{
    public static readonly MonitorId Left = new("left");
    public static readonly MonitorId Right = new("right");
    public static readonly MonitorId Third = new("third");

    public static WindowId W(int n) => new(n, ProcessId: 1000 + n, ProcessStartTicks: 42);

    /// <summary>Creates a pool with <paramref name="desktops"/> desktops on the given monitors.</summary>
    public static DesktopPool Pool(int desktops, params MonitorId[] monitors)
    {
        var pool = DesktopPool.Create(monitors.Length == 0 ? [Left, Right] : monitors);
        while (pool.DesktopCount < desktops)
        {
            pool.AddDesktop(switchTarget: null);
        }

        return pool;
    }

    /// <summary>Puts a window on desktop <paramref name="number"/> by briefly showing it on <paramref name="via"/>.</summary>
    public static void PlaceWindow(DesktopPool pool, WindowId window, int number, MonitorId via)
    {
        int original = pool.GetShownNumber(via);
        pool.GoTo(via, number);
        Assert.True(pool.AddWindow(window, via));
        pool.GoTo(via, original);
    }

    public static int NumberOf(DesktopPool pool, WindowId window)
    {
        Assert.True(pool.TryGetDesktopOf(window, out var desktop));
        return pool.GetNumber(desktop);
    }

    /// <summary>Asserts the invariants of design §3.1.</summary>
    public static void AssertInvariants(DesktopPool pool)
    {
        var desktops = pool.GetDesktops();
        Assert.True(desktops.Count >= pool.Monitors.Count, "Pool must have at least one desktop per monitor.");
        Assert.True(desktops.Count <= DesktopPool.MaxDesktops);

        var shownDesktops = pool.Monitors.Select(pool.GetShownDesktop).ToList();
        Assert.Equal(shownDesktops.Count, shownDesktops.Distinct().Count());

        foreach (var monitor in pool.Monitors)
        {
            var shown = pool.GetShownDesktop(monitor);
            Assert.Equal(monitor, pool.GetMonitorShowing(shown));
        }

        Assert.Equal(desktops.Count(d => d.ShownOn is not null), pool.Monitors.Count);

        var allWindows = desktops.SelectMany(d => d.Windows).ToList();
        Assert.Equal(allWindows.Count, allWindows.Distinct().Count());
        Assert.DoesNotContain(allWindows, pool.IsPinned);
    }
}
