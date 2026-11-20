namespace Deskswap.Core;

/// <summary>A serializable copy of the pool, used to restore it after Deskswap restarts.</summary>
/// <param name="Desktops">The desktops, in order (index 0 is desktop 1).</param>
/// <param name="Monitors">Which desktop each monitor showed.</param>
/// <param name="Windows">Which desktop each window belonged to.</param>
public sealed record PoolSnapshot(
    IReadOnlyList<DesktopSnapshot> Desktops,
    IReadOnlyList<MonitorAssignment> Monitors,
    IReadOnlyList<WindowAssignment> Windows);

/// <summary>A desktop in a <see cref="PoolSnapshot"/>.</summary>
/// <param name="Id">The desktop's id.</param>
/// <param name="Name">The desktop's name, if any.</param>
/// <param name="LastMonitor">The monitor that showed the desktop most recently, if any.</param>
public sealed record DesktopSnapshot(DesktopId Id, string? Name, MonitorId? LastMonitor);

/// <summary>A monitor and the desktop it showed.</summary>
/// <param name="Monitor">The monitor.</param>
/// <param name="Desktop">The desktop it showed.</param>
public sealed record MonitorAssignment(MonitorId Monitor, DesktopId Desktop);

/// <summary>A window and the desktop it belonged to.</summary>
/// <param name="Window">The window.</param>
/// <param name="Desktop">The desktop it belonged to.</param>
public sealed record WindowAssignment(WindowId Window, DesktopId Desktop);

/// <summary>A read-only view of one desktop in the pool.</summary>
/// <param name="Id">The desktop's id.</param>
/// <param name="Number">The desktop's 1-based number (its position in the pool).</param>
/// <param name="Name">The desktop's name, if any.</param>
/// <param name="ShownOn">The monitor showing the desktop, or <see langword="null"/> if it is hidden.</param>
/// <param name="Windows">The windows on the desktop.</param>
public sealed record DesktopInfo(DesktopId Id, int Number, string? Name, MonitorId? ShownOn, IReadOnlyCollection<WindowId> Windows);

/// <summary>A window as the platform currently sees it, used by <see cref="DesktopPool.Reconcile"/>.</summary>
/// <param name="Id">The window.</param>
/// <param name="Monitor">The monitor the window is physically on.</param>
/// <param name="IsVisible">Whether the window is currently visible (shown, possibly minimized).</param>
public sealed record ObservedWindow(WindowId Id, MonitorId Monitor, bool IsVisible);
