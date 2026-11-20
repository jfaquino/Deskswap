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
