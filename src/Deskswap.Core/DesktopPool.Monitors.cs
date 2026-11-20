namespace Deskswap.Core;

/// <summary>Monitors being connected, disconnected or rearranged (design §4).</summary>
public sealed partial class DesktopPool
{
    /// <summary>
    /// Updates the pool for a new set of connected monitors. A disconnected monitor's desktop stays in the pool
    /// with its windows hidden; a reconnected monitor gets that desktop back if no other monitor shows it; any other
    /// new monitor shows the lowest-numbered free desktop, and a desktop is added if none is free.
    /// </summary>
    /// <param name="monitors">The monitors connected now, in order.</param>
    /// <returns>The changes to apply.</returns>
    /// <exception cref="ArgumentException">The monitor list is invalid.</exception>
    public PoolChanges SetMonitors(IReadOnlyList<MonitorId> monitors)
    {
        ValidateMonitors(monitors);
        var changes = new ChangeBuilder();

        foreach (var gone in _monitors.Where(m => !monitors.Contains(m)).ToList())
        {
            var desktop = _shown[gone];
            foreach (var window in desktop.Windows)
            {
                changes.Hide(window);
            }

            // Windows moves the windows of a disconnected monitor somewhere else: their position is unknown now.
            desktop.ShownOn = null;
            desktop.LastMonitor = null;
            _shown.Remove(gone);
            _remembered[gone] = desktop;
        }

        _monitors.Clear();
        _monitors.AddRange(monitors);

        foreach (var monitor in monitors)
        {
            if (_shown.ContainsKey(monitor))
            {
                continue;
            }

            var desktop = _remembered.Remove(monitor, out var remembered) && remembered.ShownOn is null && _desktops.Contains(remembered)
                ? remembered
                : _desktops.Find(d => d.ShownOn is null);
            if (desktop is null)
            {
                desktop = new DesktopState(DesktopId.New(), name: null);
                _desktops.Add(desktop);
            }

            RelocateDesktopWindows(desktop, monitor, changes);
            foreach (var window in desktop.Windows)
            {
                changes.Show(window);
            }

            SetShown(monitor, desktop);
            changes.Indicate(IndicatorFor(monitor, desktop, IndicatorKind.Switched));
        }

        return changes.Build();
    }
}
