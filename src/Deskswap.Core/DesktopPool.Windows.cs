namespace Deskswap.Core;

/// <summary>Window tracking, reconciliation, moving windows between desktops and pinning.</summary>
public sealed partial class DesktopPool
{
    /// <summary>Gets the windows pinned to every desktop of their monitor (design §3.6).</summary>
    public IReadOnlyCollection<WindowId> PinnedWindows => _pinned;

    /// <summary>Gets a value indicating whether a window is pinned to every desktop of its monitor.</summary>
    /// <param name="window">The window.</param>
    /// <returns><see langword="true"/> if the window is pinned.</returns>
    public bool IsPinned(WindowId window) => _pinned.Contains(window);

    // ---------------------------------------------------------------- tracking (design §5.1)

    /// <summary>Adds a new window to the desktop shown on the monitor it appeared on.</summary>
    /// <param name="window">The window.</param>
    /// <param name="monitor">The monitor the window appeared on.</param>
    /// <returns><see langword="true"/> if the window was added; <see langword="false"/> if it is already known or the monitor is unknown.</returns>
    public bool AddWindow(WindowId window, MonitorId monitor)
    {
        if (_windowDesktop.ContainsKey(window) || _pinned.Contains(window) || !_shown.TryGetValue(monitor, out var desktop))
        {
            return false;
        }

        Assign(window, desktop);
        return true;
    }

    /// <summary>Forgets a window (it was closed or is no longer eligible).</summary>
    /// <param name="window">The window.</param>
    /// <returns><see langword="true"/> if the window was known.</returns>
    public bool RemoveWindow(WindowId window)
    {
        if (_pinned.Remove(window))
        {
            return true;
        }

        if (!_windowDesktop.Remove(window, out var desktop))
        {
            return false;
        }

        desktop.Windows.Remove(window);
        return true;
    }

    /// <summary>Reassigns a window the user moved to another monitor to the desktop shown there.</summary>
    /// <param name="window">The window.</param>
    /// <param name="monitor">The monitor the window is on now.</param>
    /// <returns><see langword="true"/> if the window changed desktop.</returns>
    public bool MoveWindowToMonitor(WindowId window, MonitorId monitor)
    {
        if (!_windowDesktop.TryGetValue(window, out var from) || !_shown.TryGetValue(monitor, out var to) || from == to)
        {
            return false;
        }

        Unassign(window, from);
        Assign(window, to);
        return true;
    }

    /// <summary>
    /// Brings the pool in line with what the platform observes: adopts new visible windows, forgets vanished ones,
    /// follows windows moved to another monitor, and fixes windows whose visibility is wrong.
    /// </summary>
    /// <param name="observed">Every eligible window, plus every window Deskswap hid.</param>
    /// <param name="mode">
    /// What to do with a visible window whose desktop is hidden: hide it (<see cref="ReconcileMode.Enforce"/>), or
    /// switch its monitor to its desktop (<see cref="ReconcileMode.FollowActivation"/>, design §5.4).
    /// </param>
    /// <param name="foreground">The foreground window, preferred when several windows ask for attention at once.</param>
    /// <param name="mayNotActivate">
    /// Windows that must not switch desktops even in <see cref="ReconcileMode.FollowActivation"/>, e.g. an app that
    /// keeps showing itself right after being hidden. They are hidden instead.
    /// </param>
    /// <returns>The changes needed to restore the invariants.</returns>
    public PoolChanges Reconcile(
        IEnumerable<ObservedWindow> observed,
        ReconcileMode mode = ReconcileMode.Enforce,
        WindowId? foreground = null,
        IReadOnlySet<WindowId>? mayNotActivate = null)
    {
        ArgumentNullException.ThrowIfNull(observed);
        var changes = new ChangeBuilder();
        var seen = new HashSet<WindowId>();
        var askingForAttention = new List<(ObservedWindow Window, DesktopState Desktop)>();

        foreach (var window in observed)
        {
            seen.Add(window.Id);
            if (_pinned.Contains(window.Id) || !_shown.TryGetValue(window.Monitor, out var desktopOnMonitor))
            {
                continue;
            }

            if (!_windowDesktop.TryGetValue(window.Id, out var desktop))
            {
                if (window.IsVisible)
                {
                    Assign(window.Id, desktopOnMonitor);
                }

                continue;
            }

            if (window.IsVisible)
            {
                if (desktop.ShownOn is null)
                {
                    if (mode != ReconcileMode.FollowActivation || mayNotActivate?.Contains(window.Id) == true)
                    {
                        changes.Hide(window.Id);
                    }
                    else
                    {
                        askingForAttention.Add((window, desktop));
                    }
                }
                else if (desktop.ShownOn is { } home && home != window.Monitor)
                {
                    if (mode == ReconcileMode.Hold)
                    {
                        // Windows moved it, not the user: put it back on its desktop's monitor.
                        changes.Relocate(window.Id, window.Monitor, home);
                    }
                    else
                    {
                        // The user moved it to another monitor: it joins the desktop shown there.
                        Unassign(window.Id, desktop);
                        Assign(window.Id, desktopOnMonitor);
                    }
                }
            }
            else if (desktop.ShownOn is { } shownOn)
            {
                changes.Relocate(window.Id, window.Monitor, shownOn);
                changes.Show(window.Id);
            }
        }

        if (askingForAttention.Count > 0)
        {
            Activate(askingForAttention, foreground, changes);
        }

        foreach (var window in _windowDesktop.Keys.Where(w => !seen.Contains(w)).ToList())
        {
            RemoveWindow(window);
        }

        _pinned.RemoveWhere(w => !seen.Contains(w));
        return changes.Build();
    }

    // ---------------------------------------------------------------- moving windows (design §3.4)

    /// <summary>
    /// Moves a window to the next desktop that isn't shown on another monitor, and its monitor follows it there.
    /// </summary>
    /// <param name="window">The window (normally the focused one).</param>
    /// <returns>The changes to apply.</returns>
    public PoolChanges MoveWindowToNext(WindowId window) => MoveWindowAdjacent(window, +1);

    /// <summary>
    /// Moves a window to the previous desktop that isn't shown on another monitor, and its monitor follows it there.
    /// </summary>
    /// <param name="window">The window (normally the focused one).</param>
    /// <returns>The changes to apply.</returns>
    public PoolChanges MoveWindowToPrevious(WindowId window) => MoveWindowAdjacent(window, -1);

    /// <summary>
    /// Moves a window to desktop <paramref name="number"/>. If another monitor shows that desktop, the window moves to
    /// that monitor; otherwise its own monitor follows it to the desktop.
    /// </summary>
    /// <param name="window">The window (normally the focused one).</param>
    /// <param name="number">The 1-based desktop number.</param>
    /// <returns>The changes to apply.</returns>
    public PoolChanges MoveWindowTo(WindowId window, int number)
    {
        if (!_windowDesktop.TryGetValue(window, out var source) || source.ShownOn is not { } monitor)
        {
            return PoolChanges.None;
        }

        var changes = new ChangeBuilder();
        if (number < 1 || number > _desktops.Count)
        {
            changes.Indicate(new Indicator(monitor, number, null, IndicatorKind.NotAvailable));
            return changes.Build();
        }

        var target = _desktops[number - 1];
        if (target == source)
        {
            changes.Indicate(IndicatorFor(monitor, source, IndicatorKind.Unchanged));
        }
        else if (target.ShownOn is { } other)
        {
            Unassign(window, source);
            Assign(window, target);
            changes.Relocate(window, monitor, other);
            changes.Show(window); // Raise it on the other monitor.
            changes.Focus(other);
            changes.Indicate(IndicatorFor(other, target, IndicatorKind.WindowMoved));
        }
        else
        {
            MoveAndFollow(window, source, target, monitor, changes);
        }

        return changes.Build();
    }

    // ---------------------------------------------------------------- pinning (design §3.6)

    /// <summary>
    /// Pins a window to every desktop of its monitor, or unpins it (it then belongs to the desktop shown there).
    /// </summary>
    /// <param name="window">The window (normally the focused one).</param>
    /// <param name="monitor">The monitor the window is on.</param>
    /// <returns>The changes to apply.</returns>
    public PoolChanges TogglePin(WindowId window, MonitorId monitor)
    {
        if (!_shown.TryGetValue(monitor, out var shown))
        {
            return PoolChanges.None;
        }

        var changes = new ChangeBuilder();
        if (_pinned.Remove(window))
        {
            Assign(window, shown);
            changes.Indicate(IndicatorFor(monitor, shown, IndicatorKind.Unpinned));
            return changes.Build();
        }

        if (_windowDesktop.Remove(window, out var desktop))
        {
            Unassign(window, desktop);
            if (desktop.ShownOn is null)
            {
                changes.Show(window);
            }
        }

        _pinned.Add(window);
        changes.Indicate(IndicatorFor(monitor, shown, IndicatorKind.Pinned));
        return changes.Build();
    }

    // ---------------------------------------------------------------- internals

    private PoolChanges MoveWindowAdjacent(WindowId window, int direction)
    {
        if (!_windowDesktop.TryGetValue(window, out var source) || source.ShownOn is not { } monitor)
        {
            return PoolChanges.None;
        }

        var changes = new ChangeBuilder();
        var target = FindFree(_desktops.IndexOf(source), direction);
        if (target is null)
        {
            changes.Indicate(IndicatorFor(monitor, source, IndicatorKind.EdgeReached));
        }
        else
        {
            MoveAndFollow(window, source, target, monitor, changes);
        }

        return changes.Build();
    }

    /// <summary>Moves a visible window to a hidden desktop and shows that desktop on the window's monitor.</summary>
    private void MoveAndFollow(WindowId window, DesktopState source, DesktopState target, MonitorId monitor, ChangeBuilder changes)
    {
        // Take the window out first so switching the monitor neither hides nor relocates it.
        Unassign(window, source);
        _windowDesktop.Remove(window);
        ShowOn(monitor, target, IndicatorKind.Switched, changes);
        Assign(window, target);
        changes.Show(window); // Keep it on top of the desktop it joined, so it keeps focus.
    }

    /// <summary>
    /// Switches a monitor to the desktop of a window that showed itself (design §5.4). If several windows of
    /// different hidden desktops did so at once, the foreground one wins and the others are hidden again.
    /// </summary>
    private void Activate(List<(ObservedWindow Window, DesktopState Desktop)> candidates, WindowId? foreground, ChangeBuilder changes)
    {
        var chosen = candidates.FirstOrDefault(c => c.Window.Id == foreground);
        if (chosen.Desktop is null)
        {
            chosen = candidates[0];
        }

        // The window is already where it showed itself; bring the rest of its desktop from wherever it is.
        ShowOn(chosen.Window.Monitor, chosen.Desktop, IndicatorKind.Switched, changes, fromActualPosition: true);
        changes.ActivatedBy(chosen.Window.Id);

        foreach (var (window, desktop) in candidates)
        {
            if (desktop.ShownOn is null)
            {
                changes.Hide(window.Id);
            }
        }
    }
}
