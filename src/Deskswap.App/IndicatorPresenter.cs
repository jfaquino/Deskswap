using Deskswap.App.Resources;
using Deskswap.Core;
using Deskswap.Platform;

namespace Deskswap.App;

/// <summary>Turns pool <see cref="Indicator"/>s into on-screen labels, one reusable form per monitor.</summary>
internal sealed class IndicatorPresenter : IDisposable
{
    private readonly Dictionary<MonitorId, IndicatorForm> _forms = [];

    /// <summary>Shows indicators on their monitors.</summary>
    /// <param name="indicators">The indicators from the last operation.</param>
    /// <param name="layout">The current monitor layout.</param>
    public void Show(IEnumerable<Indicator> indicators, MonitorLayout layout)
    {
        foreach (var indicator in indicators)
        {
            if (!layout.TryGet(indicator.Monitor, out var monitor))
            {
                continue;
            }

            var (title, subtitle) = Describe(indicator);
            if (!_forms.TryGetValue(indicator.Monitor, out var form) || form.IsDisposed)
            {
                form = new IndicatorForm();
                _forms[indicator.Monitor] = form;
            }

            var bounds = monitor.Bounds;
            form.Present(title, subtitle, new Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
        }
    }

    /// <summary>Shows a message on a monitor for long enough to read it (several lines are fine).</summary>
    /// <param name="monitor">The monitor.</param>
    /// <param name="title">The main line.</param>
    /// <param name="detail">The explanation.</param>
    /// <param name="durationMs">How long it stays.</param>
    public void ShowNotice(DisplayMonitor monitor, string title, string detail, int durationMs)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (!_forms.TryGetValue(monitor.Id, out var form) || form.IsDisposed)
        {
            form = new IndicatorForm();
            _forms[monitor.Id] = form;
        }

        var bounds = monitor.Bounds;
        form.Present(title, detail, new Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height), durationMs);
    }

    /// <summary>Gets a value indicating whether an indicator is on screen.</summary>
    public bool AnyVisible => _forms.Values.Any(f => !f.IsDisposed && f.Visible);

    /// <summary>Hides every indicator now, e.g. when the overview opens.</summary>
    /// <returns><see langword="true"/> if one was on screen.</returns>
    public bool HideAll()
    {
        bool any = false;
        foreach (var form in _forms.Values.Where(f => !f.IsDisposed && f.Visible))
        {
            form.Hide();
            any = true;
        }

        return any;
    }

    /// <summary>
    /// Leaves the indicators on screen out of screen captures, or lets captures see them again. Used only around
    /// Deskswap's own snapshots, so screenshots and recordings show indicators as usual.
    /// </summary>
    /// <param name="hidden">Whether captures should leave them out.</param>
    /// <returns><see langword="true"/> if one was on screen.</returns>
    public bool SetHiddenFromCapture(bool hidden)
    {
        bool any = false;
        foreach (var form in _forms.Values.Where(f => !f.IsDisposed && f.Visible))
        {
            WindowStyling.SetHiddenFromCapture(form.Handle, hidden);
            any = true;
        }

        return any;
    }

    public void Dispose()
    {
        foreach (var form in _forms.Values)
        {
            form.Dispose();
        }

        _forms.Clear();
    }

    private static (string Title, string? Subtitle) Describe(Indicator indicator)
    {
        string desktop = indicator.Name ?? Strings.DesktopNumber(indicator.Number);
        return indicator.Kind switch
        {
            IndicatorKind.Added => (desktop, Strings.IndicatorAdded),
            IndicatorKind.Removed => (desktop, Strings.IndicatorRemoved),
            IndicatorKind.EdgeReached => (desktop, Strings.IndicatorEdge),
            IndicatorKind.NotAvailable => (Strings.IndicatorNotAvailable(indicator.Number), null),
            IndicatorKind.Pinned => (desktop, Strings.IndicatorPinned),
            IndicatorKind.Unpinned => (desktop, Strings.IndicatorUnpinned),
            IndicatorKind.WindowMoved => (desktop, Strings.IndicatorWindowMoved),
            IndicatorKind.NotAllowed when indicator.Reason == NotAllowedReason.MaximumDesktopsReached =>
                (desktop, Strings.IndicatorMaximumReached(DesktopPool.MaxDesktops)),
            IndicatorKind.NotAllowed => (desktop, Strings.IndicatorMinimumReached),
            _ => (desktop, null),
        };
    }
}
