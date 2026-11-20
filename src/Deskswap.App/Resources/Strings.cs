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

    public static string DesktopNumber(int number) => Format(nameof(DesktopNumber), number);

    public static string IndicatorNotAvailable(int number) => Format(nameof(IndicatorNotAvailable), number);

    public static string IndicatorMaximumReached(int maximum) => Format(nameof(IndicatorMaximumReached), maximum);

    public static string NotifyRecovered(int count) => Format(nameof(NotifyRecovered), count);

    private static string Get(string name) => Manager.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    private static string Format(string name, object argument) => string.Format(CultureInfo.CurrentCulture, Get(name), argument);
}
