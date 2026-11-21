namespace Deskswap.Core;

/// <summary>Operations the overview offers by pointing and dragging (overview-ux.md §5).</summary>
public sealed partial class DesktopPool
{
    /// <summary>
    /// Moves a window to a desktop without the view following it (design §3.4, overview drag and drop). If another
    /// monitor shows the desktop, the window moves to that monitor; if no monitor shows it, the window is hidden and
    /// moved to the monitor that last showed it. A pinned window is unpinned.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="desktop">The desktop to move it to.</param>
    /// <returns>The changes to apply.</returns>
    public PoolChanges MoveWindowToDesktop(WindowId window, DesktopId desktop)
    {
        var target = _desktops.Find(d => d.Id == desktop);
        if (target is null)
        {
            return PoolChanges.None;
        }

        bool wasPinned = _pinned.Remove(window);
        _windowDesktop.TryGetValue(window, out var source);
        if (!wasPinned && source is null)
        {
            return PoolChanges.None; // Not managed.
        }

        if (source == target)
        {
            return PoolChanges.None;
        }

        // Where the window is now: its desktop's monitor, or, for a pinned window, unknown to the pool.
        MonitorId? currentMonitor = source?.ShownOn;
        if (source is not null)
        {
            Unassign(window, source);
        }

        Assign(window, target);

        var changes = new ChangeBuilder();
        if (target.ShownOn is { } shownOn)
        {
            changes.Relocate(window, wasPinned ? null : currentMonitor, shownOn);
            changes.Show(window);
        }
        else
        {
            changes.Hide(window);
            if (target.LastMonitor is { } last && _monitors.Contains(last))
            {
                // It comes back with its desktop from the desktop's last monitor (see RelocateDesktopWindows).
                changes.Relocate(window, wasPinned ? null : currentMonitor, last);
            }
        }

        return changes.Build();
    }

    /// <summary>Adds a desktop and moves a window onto it (dropping a window on the overview's <c>+</c> card).</summary>
    /// <param name="window">The window.</param>
    /// <param name="monitor">The monitor the action came from, for the indicator if the pool is full.</param>
    /// <returns>The changes to apply.</returns>
    public PoolChanges MoveWindowToNewDesktop(WindowId window, MonitorId monitor)
    {
        if (!_windowDesktop.ContainsKey(window) && !_pinned.Contains(window))
        {
            return PoolChanges.None;
        }

        if (_desktops.Count >= MaxDesktops)
        {
            return _shown.TryGetValue(monitor, out var current)
                ? new PoolChanges([], [], [], null, [IndicatorFor(monitor, current, IndicatorKind.NotAllowed, NotAllowedReason.MaximumDesktopsReached)])
                : PoolChanges.None;
        }

        var added = new DesktopState(DesktopId.New(), name: null);
        _desktops.Add(added);
        return MoveWindowToDesktop(window, added.Id);
    }

    /// <summary>
    /// Moves a desktop to another position in the pool (design §3.2, reordering in the overview). Its name, windows
    /// and monitor go with it; only the numbers change.
    /// </summary>
    /// <param name="desktop">The desktop to move.</param>
    /// <param name="number">The 1-based number it should have afterwards.</param>
    /// <returns><see langword="true"/> if the order changed.</returns>
    public bool MoveDesktop(DesktopId desktop, int number)
    {
        int index = _desktops.FindIndex(d => d.Id == desktop);
        if (index < 0)
        {
            return false;
        }

        int target = Math.Clamp(number, 1, _desktops.Count) - 1;
        if (target == index)
        {
            return false;
        }

        var state = _desktops[index];
        _desktops.RemoveAt(index);
        _desktops.Insert(target, state);
        return true;
    }
}
