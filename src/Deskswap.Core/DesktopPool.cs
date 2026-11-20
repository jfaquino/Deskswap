namespace Deskswap.Core;

/// <summary>
/// The shared pool of desktops (design §3). Each connected monitor shows exactly one desktop; a desktop is
/// shown on at most one monitor; every managed window belongs to exactly one desktop.
/// </summary>
/// <remarks>
/// The pool is pure state: operations never touch real windows. They return <see cref="PoolChanges"/> that the
/// platform layer applies. Not thread-safe; use it from the UI thread only.
/// </remarks>
public sealed class DesktopPool
{
    /// <summary>The maximum number of desktops in the pool (as on macOS).</summary>
    public const int MaxDesktops = 16;

    private readonly List<DesktopState> _desktops = [];
    private readonly List<MonitorId> _monitors = [];
    private readonly Dictionary<MonitorId, DesktopState> _shown = [];
    private readonly Dictionary<WindowId, DesktopState> _windowDesktop = [];

    private DesktopPool()
    {
    }

    /// <summary>Gets the connected monitors, in order (left to right, then top to bottom).</summary>
    public IReadOnlyList<MonitorId> Monitors => _monitors;

    /// <summary>Gets the number of desktops in the pool.</summary>
    public int DesktopCount => _desktops.Count;

    /// <summary>Creates a pool for a first run: one desktop per monitor, monitor k showing desktop k (design §3.2).</summary>
    /// <param name="monitors">The connected monitors, in order.</param>
    /// <returns>The new pool.</returns>
    /// <exception cref="ArgumentException">There are no monitors, duplicates, or more than <see cref="MaxDesktops"/>.</exception>
    public static DesktopPool Create(IReadOnlyList<MonitorId> monitors)
    {
        ValidateMonitors(monitors);
        var pool = new DesktopPool();
        foreach (var monitor in monitors)
        {
            var desktop = new DesktopState(DesktopId.New(), name: null);
            pool._desktops.Add(desktop);
            pool._monitors.Add(monitor);
            pool.SetShown(monitor, desktop);
        }

        return pool;
    }

    /// <summary>
    /// Restores a pool saved with <see cref="ToSnapshot"/>. Monitors show the desktop they showed before when
    /// possible; any other monitor shows the lowest-numbered free desktop, and a desktop is added if none is free.
    /// </summary>
    /// <param name="snapshot">The saved pool.</param>
    /// <param name="monitors">The monitors connected now, in order.</param>
    /// <returns>The restored pool.</returns>
    /// <exception cref="ArgumentException">The monitor list is invalid.</exception>
    public static DesktopPool Restore(PoolSnapshot snapshot, IReadOnlyList<MonitorId> monitors)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateMonitors(monitors);
        if (snapshot.Desktops.Count == 0)
        {
            return Create(monitors);
        }

        var pool = new DesktopPool();
        var byId = new Dictionary<DesktopId, DesktopState>();
        foreach (var saved in snapshot.Desktops)
        {
            if (pool._desktops.Count == MaxDesktops || byId.ContainsKey(saved.Id))
            {
                continue;
            }

            var desktop = new DesktopState(saved.Id, saved.Name) { LastMonitor = saved.LastMonitor };
            pool._desktops.Add(desktop);
            byId.Add(saved.Id, desktop);
        }

        pool._monitors.AddRange(monitors);

        var savedMapping = new Dictionary<MonitorId, DesktopId>();
        foreach (var assignment in snapshot.Monitors)
        {
            savedMapping.TryAdd(assignment.Monitor, assignment.Desktop);
        }

        foreach (var monitor in monitors)
        {
            if (savedMapping.TryGetValue(monitor, out var id) && byId.TryGetValue(id, out var desktop) && desktop.ShownOn is null)
            {
                pool.SetShown(monitor, desktop);
            }
        }

        foreach (var monitor in monitors)
        {
            if (pool._shown.ContainsKey(monitor))
            {
                continue;
            }

            var free = pool._desktops.Find(d => d.ShownOn is null);
            if (free is null)
            {
                free = new DesktopState(DesktopId.New(), name: null);
                pool._desktops.Add(free);
            }

            pool.SetShown(monitor, free);
        }

        foreach (var assignment in snapshot.Windows)
        {
            if (byId.TryGetValue(assignment.Desktop, out var desktop) && !pool._windowDesktop.ContainsKey(assignment.Window))
            {
                pool.Assign(assignment.Window, desktop);
            }
        }

        return pool;
    }

    /// <summary>Saves the pool so it can be restored with <see cref="Restore"/>.</summary>
    /// <returns>A snapshot of the current state.</returns>
    public PoolSnapshot ToSnapshot() => new(
        [.. _desktops.Select(d => new DesktopSnapshot(d.Id, d.Name, d.LastMonitor))],
        [.. _monitors.Select(m => new MonitorAssignment(m, _shown[m].Id))],
        [.. _windowDesktop.Select(pair => new WindowAssignment(pair.Key, pair.Value.Id))]);

    // ---------------------------------------------------------------- queries

    /// <summary>Gets a read-only view of every desktop, in order.</summary>
    /// <returns>The desktops; index 0 is desktop 1.</returns>
    public IReadOnlyList<DesktopInfo> GetDesktops() =>
        [.. _desktops.Select((d, index) => new DesktopInfo(d.Id, index + 1, d.Name, d.ShownOn, [.. d.Windows]))];

    /// <summary>Gets the desktop a monitor shows.</summary>
    /// <param name="monitor">A connected monitor.</param>
    /// <returns>The id of the desktop shown on <paramref name="monitor"/>.</returns>
    /// <exception cref="ArgumentException">The monitor is not connected.</exception>
    public DesktopId GetShownDesktop(MonitorId monitor) => GetShownState(monitor).Id;

    /// <summary>Gets the number of the desktop a monitor shows.</summary>
    /// <param name="monitor">A connected monitor.</param>
    /// <returns>The 1-based number of the desktop shown on <paramref name="monitor"/>.</returns>
    /// <exception cref="ArgumentException">The monitor is not connected.</exception>
    public int GetShownNumber(MonitorId monitor) => NumberOf(GetShownState(monitor));

    /// <summary>Gets the number of a desktop.</summary>
    /// <param name="desktop">A desktop in the pool.</param>
    /// <returns>The desktop's 1-based number.</returns>
    /// <exception cref="ArgumentException">The desktop is not in the pool.</exception>
    public int GetNumber(DesktopId desktop)
    {
        int index = _desktops.FindIndex(d => d.Id == desktop);
        return index >= 0 ? index + 1 : throw new ArgumentException($"Desktop {desktop} is not in the pool.", nameof(desktop));
    }

    /// <summary>Gets the monitor showing a desktop.</summary>
    /// <param name="desktop">A desktop in the pool.</param>
    /// <returns>The monitor, or <see langword="null"/> if the desktop is hidden or unknown.</returns>
    public MonitorId? GetMonitorShowing(DesktopId desktop) => _desktops.Find(d => d.Id == desktop)?.ShownOn;

    /// <summary>Gets the desktop a window belongs to.</summary>
    /// <param name="window">The window.</param>
    /// <param name="desktop">The window's desktop, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if the window is managed by the pool.</returns>
    public bool TryGetDesktopOf(WindowId window, out DesktopId desktop)
    {
        if (_windowDesktop.TryGetValue(window, out var state))
        {
            desktop = state.Id;
            return true;
        }

        desktop = default;
        return false;
    }

    /// <summary>Gets a value indicating whether a managed window should currently be visible.</summary>
    /// <param name="window">The window.</param>
    /// <returns><see langword="true"/> if the window is managed and its desktop is shown.</returns>
    public bool ShouldBeVisible(WindowId window) =>
        _windowDesktop.TryGetValue(window, out var state) && state.ShownOn is not null;

    // ---------------------------------------------------------------- switching (design §3.3)

    /// <summary>
    /// Shows desktop <paramref name="number"/> on <paramref name="target"/>. If another monitor shows it, the two
    /// monitors swap desktops.
    /// </summary>
    /// <param name="target">The monitor to switch.</param>
    /// <param name="number">The 1-based desktop number.</param>
    /// <returns>The changes to apply.</returns>
    public PoolChanges GoTo(MonitorId target, int number)
    {
        if (!_shown.TryGetValue(target, out var current))
        {
            return PoolChanges.None;
        }

        var changes = new ChangeBuilder();
        if (number < 1 || number > _desktops.Count)
        {
            changes.Indicate(new Indicator(target, number, null, IndicatorKind.NotAvailable));
            return changes.Build();
        }

        var desired = _desktops[number - 1];
        if (desired == current)
        {
            changes.Indicate(IndicatorFor(target, current, IndicatorKind.Unchanged));
        }
        else if (desired.ShownOn is { } other)
        {
            Swap(target, other, changes);
        }
        else
        {
            ShowOn(target, desired, IndicatorKind.Switched, changes);
        }

        return changes.Build();
    }

    /// <summary>
    /// Shows the next desktop that isn't shown on another monitor. At the end of the pool nothing changes.
    /// </summary>
    /// <param name="target">The monitor to switch.</param>
    /// <returns>The changes to apply.</returns>
    public PoolChanges GoToNext(MonitorId target) => Advance(target, +1);

    /// <summary>
    /// Shows the previous desktop that isn't shown on another monitor. At the start of the pool nothing changes.
    /// </summary>
    /// <param name="target">The monitor to switch.</param>
    /// <returns>The changes to apply.</returns>
    public PoolChanges GoToPrevious(MonitorId target) => Advance(target, -1);

    // ---------------------------------------------------------------- lifecycle (design §3.2)

    /// <summary>Appends a desktop and, if <paramref name="switchTarget"/> is given, shows it there.</summary>
    /// <param name="switchTarget">The monitor that should switch to the new desktop, or <see langword="null"/> to only add it.</param>
    /// <returns>The changes to apply.</returns>
    public PoolChanges AddDesktop(MonitorId? switchTarget)
    {
        var changes = new ChangeBuilder();
        DesktopState? current = null;
        if (switchTarget is { } monitor && !_shown.TryGetValue(monitor, out current))
        {
            return PoolChanges.None;
        }

        if (_desktops.Count >= MaxDesktops)
        {
            if (current is not null)
            {
                changes.Indicate(IndicatorFor(switchTarget!.Value, current, IndicatorKind.NotAllowed, NotAllowedReason.MaximumDesktopsReached));
            }

            return changes.Build();
        }

        var added = new DesktopState(DesktopId.New(), name: null);
        _desktops.Add(added);
        if (switchTarget is { } target)
        {
            ShowOn(target, added, IndicatorKind.Added, changes);
        }

        return changes.Build();
    }

    /// <summary>
    /// Removes desktop <paramref name="number"/>. Its windows move to the desktop that takes its place on its
    /// monitor, or, if it was hidden, to the desktop shown on <paramref name="target"/>.
    /// </summary>
    /// <param name="number">The 1-based number of the desktop to remove.</param>
    /// <param name="target">The monitor the action came from.</param>
    /// <returns>The changes to apply.</returns>
    public PoolChanges RemoveDesktop(int number, MonitorId target)
    {
        if (!_shown.TryGetValue(target, out var targetDesktop))
        {
            return PoolChanges.None;
        }

        var changes = new ChangeBuilder();
        if (number < 1 || number > _desktops.Count)
        {
            changes.Indicate(new Indicator(target, number, null, IndicatorKind.NotAvailable));
            return changes.Build();
        }

        if (_desktops.Count - 1 < _monitors.Count)
        {
            changes.Indicate(IndicatorFor(target, targetDesktop, IndicatorKind.NotAllowed, NotAllowedReason.MinimumDesktopsReached));
            return changes.Build();
        }

        var removed = _desktops[number - 1];
        if (removed.ShownOn is { } monitor)
        {
            // Invariant 3 guarantees a desktop that no monitor shows.
            var replacement = FindFree(number - 1, -1) ?? FindFree(number - 1, +1)
                ?? throw new InvalidOperationException("No free desktop although the pool is larger than the monitor count.");

            RelocateDesktopWindows(replacement, monitor, changes);
            foreach (var window in replacement.Windows)
            {
                changes.Show(window);
            }

            MoveAllWindows(removed, replacement);
            _desktops.Remove(removed);
            _shown[monitor] = replacement;
            replacement.ShownOn = monitor;
            replacement.LastMonitor = monitor;
            changes.Focus(monitor);
            changes.Indicate(IndicatorFor(monitor, replacement, IndicatorKind.Removed));
        }
        else
        {
            if (removed.LastMonitor is { } from)
            {
                foreach (var window in removed.Windows)
                {
                    changes.Relocate(window, from, target);
                }
            }

            foreach (var window in removed.Windows)
            {
                changes.Show(window);
            }

            MoveAllWindows(removed, targetDesktop);
            _desktops.Remove(removed);
            changes.Indicate(IndicatorFor(target, targetDesktop, IndicatorKind.Removed));
        }

        return changes.Build();
    }

    /// <summary>Sets or clears a desktop's name.</summary>
    /// <param name="desktop">The desktop to rename.</param>
    /// <param name="name">The new name; empty or whitespace clears it.</param>
    /// <returns><see langword="true"/> if the desktop exists.</returns>
    public bool Rename(DesktopId desktop, string? name)
    {
        var state = _desktops.Find(d => d.Id == desktop);
        if (state is null)
        {
            return false;
        }

        state.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        return true;
    }

    // ---------------------------------------------------------------- windows (design §3.4, §5.1)

    /// <summary>Adds a new window to the desktop shown on the monitor it appeared on.</summary>
    /// <param name="window">The window.</param>
    /// <param name="monitor">The monitor the window appeared on.</param>
    /// <returns><see langword="true"/> if the window was added; <see langword="false"/> if it is already managed or the monitor is unknown.</returns>
    public bool AddWindow(WindowId window, MonitorId monitor)
    {
        if (_windowDesktop.ContainsKey(window) || !_shown.TryGetValue(monitor, out var desktop))
        {
            return false;
        }

        Assign(window, desktop);
        return true;
    }

    /// <summary>Forgets a window (it was closed or is no longer eligible).</summary>
    /// <param name="window">The window.</param>
    /// <returns><see langword="true"/> if the window was managed.</returns>
    public bool RemoveWindow(WindowId window)
    {
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
    /// Brings the pool in line with what the platform observes: adopts new visible windows, forgets vanished
    /// ones, follows windows moved to another monitor, and hides or shows windows whose visibility is wrong.
    /// </summary>
    /// <param name="observed">Every eligible window, plus every window Deskswap hid.</param>
    /// <returns>The changes needed to restore the invariants.</returns>
    public PoolChanges Reconcile(IEnumerable<ObservedWindow> observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        var changes = new ChangeBuilder();
        var seen = new HashSet<WindowId>();

        foreach (var window in observed)
        {
            seen.Add(window.Id);
            if (!_shown.TryGetValue(window.Monitor, out var desktopOnMonitor))
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
                    // M1: enforce the model. M2 (design §5.4) replaces this with activation: a window that
                    // shows itself while its desktop is hidden switches its monitor to that desktop.
                    changes.Hide(window.Id);
                }
                else if (desktop.ShownOn != window.Monitor)
                {
                    Unassign(window.Id, desktop);
                    Assign(window.Id, desktopOnMonitor);
                }
            }
            else if (desktop.ShownOn is { } shownOn)
            {
                changes.Relocate(window.Id, window.Monitor, shownOn);
                changes.Show(window.Id);
            }
        }

        foreach (var window in _windowDesktop.Keys.Where(w => !seen.Contains(w)).ToList())
        {
            RemoveWindow(window);
        }

        return changes.Build();
    }

    // ---------------------------------------------------------------- internals

    private static void ValidateMonitors(IReadOnlyList<MonitorId> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
        {
            throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        }

        if (monitors.Count > MaxDesktops)
        {
            throw new ArgumentException($"At most {MaxDesktops} monitors are supported.", nameof(monitors));
        }

        if (monitors.Distinct().Count() != monitors.Count)
        {
            throw new ArgumentException("Monitors must be unique.", nameof(monitors));
        }
    }

    private DesktopState GetShownState(MonitorId monitor) =>
        _shown.TryGetValue(monitor, out var desktop)
            ? desktop
            : throw new ArgumentException($"Monitor {monitor} is not connected.", nameof(monitor));

    private int NumberOf(DesktopState desktop) => _desktops.IndexOf(desktop) + 1;

    private Indicator IndicatorFor(MonitorId monitor, DesktopState desktop, IndicatorKind kind, NotAllowedReason reason = NotAllowedReason.None) =>
        new(monitor, NumberOf(desktop), desktop.Name, kind, reason);

    private PoolChanges Advance(MonitorId target, int direction)
    {
        if (!_shown.TryGetValue(target, out var current))
        {
            return PoolChanges.None;
        }

        var changes = new ChangeBuilder();
        var nextFree = FindFree(_desktops.IndexOf(current), direction);
        if (nextFree is null)
        {
            changes.Indicate(IndicatorFor(target, current, IndicatorKind.EdgeReached));
        }
        else
        {
            ShowOn(target, nextFree, IndicatorKind.Switched, changes);
        }

        return changes.Build();
    }

    /// <summary>Finds the first desktop no monitor shows, starting next to <paramref name="fromIndex"/>.</summary>
    private DesktopState? FindFree(int fromIndex, int direction)
    {
        for (int i = fromIndex + direction; i >= 0 && i < _desktops.Count; i += direction)
        {
            if (_desktops[i].ShownOn is null)
            {
                return _desktops[i];
            }
        }

        return null;
    }

    /// <summary>Shows a desktop no monitor shows on <paramref name="target"/>, hiding the one it showed.</summary>
    private void ShowOn(MonitorId target, DesktopState incoming, IndicatorKind kind, ChangeBuilder changes)
    {
        var outgoing = _shown[target];
        foreach (var window in outgoing.Windows)
        {
            changes.Hide(window);
        }

        RelocateDesktopWindows(incoming, target, changes);
        foreach (var window in incoming.Windows)
        {
            changes.Show(window);
        }

        SetShown(target, incoming);
        changes.Focus(target);
        changes.Indicate(IndicatorFor(target, incoming, kind));
    }

    /// <summary>Makes two monitors trade desktops. Windows travel with their desktop (design §3.5).</summary>
    private void Swap(MonitorId target, MonitorId other, ChangeBuilder changes)
    {
        var a = _shown[target];
        var b = _shown[other];
        foreach (var window in a.Windows)
        {
            changes.Relocate(window, target, other);
        }

        foreach (var window in b.Windows)
        {
            changes.Relocate(window, other, target);
        }

        _shown[target] = b;
        _shown[other] = a;
        b.ShownOn = b.LastMonitor = target;
        a.ShownOn = a.LastMonitor = other;

        changes.Focus(target);
        changes.Indicate(IndicatorFor(target, b, IndicatorKind.Switched));
        changes.Indicate(IndicatorFor(other, a, IndicatorKind.Switched));
    }

    private static void RelocateDesktopWindows(DesktopState desktop, MonitorId to, ChangeBuilder changes)
    {
        if (desktop.LastMonitor is { } from && from != to)
        {
            foreach (var window in desktop.Windows)
            {
                changes.Relocate(window, from, to);
            }
        }
    }

    private void SetShown(MonitorId monitor, DesktopState desktop)
    {
        if (_shown.TryGetValue(monitor, out var previous))
        {
            previous.ShownOn = null;
        }

        _shown[monitor] = desktop;
        desktop.ShownOn = monitor;
        desktop.LastMonitor = monitor;
    }

    private void Assign(WindowId window, DesktopState desktop)
    {
        desktop.Windows.Add(window);
        _windowDesktop[window] = desktop;
    }

    private static void Unassign(WindowId window, DesktopState desktop) => desktop.Windows.Remove(window);

    private void MoveAllWindows(DesktopState from, DesktopState to)
    {
        foreach (var window in from.Windows)
        {
            Assign(window, to);
        }

        from.Windows.Clear();
    }

    private sealed class DesktopState(DesktopId id, string? name)
    {
        public DesktopId Id { get; } = id;

        public string? Name { get; set; } = name;

        public HashSet<WindowId> Windows { get; } = [];

        public MonitorId? ShownOn { get; set; }

        public MonitorId? LastMonitor { get; set; }
    }
}
