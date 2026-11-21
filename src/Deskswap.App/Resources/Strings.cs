using System.Globalization;
using System.Resources;

namespace Deskswap.App.Resources;

/// <summary>
/// Localized UI text (design §11). English is the neutral language; Spanish lives in <c>Strings.es.resx</c>.
/// The language follows the Windows display language.
/// </summary>
internal static class Strings
{
    private static readonly ResourceManager Manager = new("Deskswap.App.Resources.Strings", typeof(Strings).Assembly);

    public static string AppTooltip => Get(nameof(AppTooltip));

    public static string AppTooltipPaused => Get(nameof(AppTooltipPaused));

    public static string MenuShowAllAndPause => Get(nameof(MenuShowAllAndPause));

    public static string MenuResume => Get(nameof(MenuResume));

    public static string MenuExit => Get(nameof(MenuExit));

    public static string IndicatorAdded => Get(nameof(IndicatorAdded));

    public static string IndicatorRemoved => Get(nameof(IndicatorRemoved));

    public static string IndicatorEdge => Get(nameof(IndicatorEdge));

    public static string IndicatorMinimumReached => Get(nameof(IndicatorMinimumReached));

    public static string NotifyPaused => Get(nameof(NotifyPaused));

    public static string NotifyError => Get(nameof(NotifyError));

    public static string NotifyHookFailed => Get(nameof(NotifyHookFailed));

    public static string NotifyLayoutReset => Get(nameof(NotifyLayoutReset));

    public static string NotifyElevated => Get(nameof(NotifyElevated));

    public static string ElevatedInFrontDetail => Get(nameof(ElevatedInFrontDetail));

    public static string ElevatedAppFallback => Get(nameof(ElevatedAppFallback));

    public static string MenuRestartAsAdministrator => Get(nameof(MenuRestartAsAdministrator));

    public static string ElevatedInFrontTitle(string app) => Format(nameof(ElevatedInFrontTitle), app);

    public static string IndicatorPinned => Get(nameof(IndicatorPinned));

    public static string IndicatorUnpinned => Get(nameof(IndicatorUnpinned));

    public static string IndicatorWindowMoved => Get(nameof(IndicatorWindowMoved));

    public static string DesktopNumber(int number) => Format(nameof(DesktopNumber), number);

    public static string IndicatorNotAvailable(int number) => Format(nameof(IndicatorNotAvailable), number);

    public static string IndicatorMaximumReached(int maximum) => Format(nameof(IndicatorMaximumReached), maximum);

    public static string NotifyRecovered(int count) => Format(nameof(NotifyRecovered), count);

    public static string OverviewNoWindows => Get(nameof(OverviewNoWindows));

    public static string OverviewNewDesktop => Get(nameof(OverviewNewDesktop));

    public static string OverviewRename => Get(nameof(OverviewRename));

    public static string OverviewRemove => Get(nameof(OverviewRemove));

    public static string OverviewCannotRemove => Get(nameof(OverviewCannotRemove));

    public static string OverviewMoveTo => Get(nameof(OverviewMoveTo));

    public static string OverviewPin => Get(nameof(OverviewPin));

    public static string OverviewUnpin => Get(nameof(OverviewUnpin));

    public static string MonitorLeft => Get(nameof(MonitorLeft));

    public static string MonitorRight => Get(nameof(MonitorRight));

    public static string MonitorCenter => Get(nameof(MonitorCenter));

    /// <summary>Gets the format string "Shown on {0}" (the overview fills in the monitor).</summary>
    public static string OverviewShownOn => Get(nameof(OverviewShownOn));

    public static string AppWindowsThisMonitor => Get(nameof(AppWindowsThisMonitor));

    public static string ShownOnMonitor(string monitor) => Format(nameof(OverviewShownOn), monitor);

    public static string MonitorNumber(int number) => Format(nameof(MonitorNumber), number);

    private static string Get(string name) => Manager.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    private static string Format(string name, object argument) => string.Format(CultureInfo.CurrentCulture, Get(name), argument);
}
