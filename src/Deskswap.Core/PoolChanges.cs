namespace Deskswap.Core;

/// <summary>What the on-screen indicator should say after an action.</summary>
public enum IndicatorKind
{
    /// <summary>The monitor now shows another desktop.</summary>
    Switched,

    /// <summary>The requested desktop is already shown on the monitor; nothing changed.</summary>
    Unchanged,

    /// <summary>Next or previous reached the end of the pool; nothing changed.</summary>
    EdgeReached,

    /// <summary>The requested desktop number doesn't exist.</summary>
    NotAvailable,

    /// <summary>A desktop was added and the monitor now shows it.</summary>
    Added,

    /// <summary>A desktop was removed; the indicator shows the desktop the monitor shows now.</summary>
    Removed,

    /// <summary>The action is not allowed; see <see cref="NotAllowedReason"/>.</summary>
    NotAllowed,
}

/// <summary>Why an action was not allowed.</summary>
public enum NotAllowedReason
{
    /// <summary>The action was allowed.</summary>
    None,

    /// <summary>The pool already has <see cref="DesktopPool.MaxDesktops"/> desktops.</summary>
    MaximumDesktopsReached,

    /// <summary>Removing a desktop would leave fewer desktops than connected monitors.</summary>
    MinimumDesktopsReached,
}

/// <summary>A request to show the desktop indicator on a monitor.</summary>
/// <param name="Monitor">The monitor to show the indicator on.</param>
/// <param name="Number">The 1-based desktop number to display.</param>
/// <param name="Name">The desktop's name, if it has one.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Reason">Why the action was refused, when <paramref name="Kind"/> is <see cref="IndicatorKind.NotAllowed"/>.</param>
public sealed record Indicator(MonitorId Monitor, int Number, string? Name, IndicatorKind Kind, NotAllowedReason Reason = NotAllowedReason.None);

/// <summary>A window that must move from one monitor to another, keeping its relative position and state.</summary>
/// <param name="Window">The window to move.</param>
/// <param name="From">The monitor the window is on now.</param>
/// <param name="To">The monitor the window must move to.</param>
public sealed record WindowRelocation(WindowId Window, MonitorId From, MonitorId To);

/// <summary>
/// A request to focus the desktop now shown on a monitor: its top-most window, which (with the stacking order kept)
/// is the window that was active when that desktop was left.
/// </summary>
/// <param name="Monitor">The monitor whose desktop should receive focus.</param>
public sealed record FocusRequest(MonitorId Monitor);

/// <summary>
/// The effects of a pool operation that the platform layer must apply, in this order: hide, relocate, show,
/// focus, indicators.
/// </summary>
/// <param name="Hide">Windows to hide.</param>
/// <param name="Relocations">Windows to move between monitors.</param>
/// <param name="Show">Windows to show.</param>
/// <param name="Focus">Where to put focus afterwards, if anywhere.</param>
/// <param name="Indicators">Indicators to display.</param>
public sealed record PoolChanges(
    IReadOnlyList<WindowId> Hide,
    IReadOnlyList<WindowRelocation> Relocations,
    IReadOnlyList<WindowId> Show,
    FocusRequest? Focus,
    IReadOnlyList<Indicator> Indicators)
{
    /// <summary>Gets an instance that changes nothing.</summary>
    public static PoolChanges None { get; } = new([], [], [], null, []);

    /// <summary>Gets a value indicating whether any window must be hidden, shown or moved.</summary>
    public bool HasWindowChanges => Hide.Count > 0 || Relocations.Count > 0 || Show.Count > 0;
}

/// <summary>Collects changes while an operation runs, keeping hide and show consistent.</summary>
internal sealed class ChangeBuilder
{
    private readonly List<WindowId> _hide = [];
    private readonly List<WindowId> _show = [];
    private readonly List<WindowRelocation> _relocations = [];
    private readonly List<Indicator> _indicators = [];
    private FocusRequest? _focus;

    public void Hide(WindowId window)
    {
        _show.Remove(window);
        if (!_hide.Contains(window))
        {
            _hide.Add(window);
        }
    }

    public void Show(WindowId window)
    {
        _hide.Remove(window);
        if (!_show.Contains(window))
        {
            _show.Add(window);
        }
    }

    public void Relocate(WindowId window, MonitorId from, MonitorId to)
    {
        if (from != to)
        {
            _relocations.Add(new WindowRelocation(window, from, to));
        }
    }

    public void Focus(MonitorId monitor) => _focus = new FocusRequest(monitor);

    public void Indicate(Indicator indicator) => _indicators.Add(indicator);

    public PoolChanges Build() =>
        _hide.Count == 0 && _show.Count == 0 && _relocations.Count == 0 && _focus is null && _indicators.Count == 0
            ? PoolChanges.None
            : new PoolChanges([.. _hide], [.. _relocations], [.. _show], _focus, [.. _indicators]);
}
