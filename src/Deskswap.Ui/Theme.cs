using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Deskswap.Ui;

/// <summary>
/// Windows 11 colors for Deskswap's UI: light or dark following the system, the user's accent color, and system
/// colors in high contrast (overview-ux.md §6).
/// </summary>
internal sealed class Theme
{
    private Theme(bool isDark, bool highContrast, Color accent)
    {
        IsDark = isDark;
        IsHighContrast = highContrast;
        Accent = accent;

        if (highContrast)
        {
            Backdrop = Frozen(SystemColors.WindowColor);
            Surface = Frozen(SystemColors.ControlColor);
            SurfaceHover = Frozen(SystemColors.HighlightColor);
            Text = Frozen(SystemColors.WindowTextColor);
            TextSecondary = Frozen(SystemColors.GrayTextColor);
            AccentBrush = Frozen(SystemColors.HighlightColor);
            OnAccent = Frozen(SystemColors.HighlightTextColor);
            Stroke = Frozen(SystemColors.WindowTextColor);
            TintOpacity = 1;
            return;
        }

        Backdrop = Frozen(isDark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3));
        Surface = Frozen(isDark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
        SurfaceHover = Frozen(isDark ? Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));
        Text = Frozen(isDark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A));
        TextSecondary = Frozen(isDark ? Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x9E, 0x00, 0x00, 0x00));

        // Windows uses a lighter accent on dark backgrounds and a darker one on light backgrounds.
        var shown = isDark ? Blend(accent, Colors.White, 0.35) : Blend(accent, Colors.Black, 0.15);
        AccentBrush = Frozen(shown);
        OnAccent = Frozen(Luminance(shown) > 0.5 ? Colors.Black : Colors.White);
        Stroke = Frozen(isDark ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
        TintOpacity = 0.6;
    }

    /// <summary>Gets a value indicating whether apps use the dark theme.</summary>
    public bool IsDark { get; }

    /// <summary>Gets a value indicating whether a high contrast theme is on (no translucency then).</summary>
    public bool IsHighContrast { get; }

    /// <summary>Gets the system accent color.</summary>
    public Color Accent { get; }

    /// <summary>Gets the color the backdrop is tinted toward.</summary>
    public SolidColorBrush Backdrop { get; }

    /// <summary>Gets how strongly the backdrop is tinted (0–1).</summary>
    public double TintOpacity { get; }

    /// <summary>Gets the fill of cards and pills.</summary>
    public SolidColorBrush Surface { get; }

    /// <summary>Gets the fill of hovered cards and pills.</summary>
    public SolidColorBrush SurfaceHover { get; }

    /// <summary>Gets the primary text color.</summary>
    public SolidColorBrush Text { get; }

    /// <summary>Gets the secondary text color.</summary>
    public SolidColorBrush TextSecondary { get; }

    /// <summary>Gets the accent fill and outline.</summary>
    public SolidColorBrush AccentBrush { get; }

    /// <summary>Gets the text color on accent fills.</summary>
    public SolidColorBrush OnAccent { get; }

    /// <summary>Gets the subtle border of cards.</summary>
    public SolidColorBrush Stroke { get; }

    /// <summary>Gets the font for text.</summary>
    public static FontFamily TextFont { get; } = new("Segoe UI Variable Text, Segoe UI");

    /// <summary>Gets the Windows 11 icon font.</summary>
    public static FontFamily IconFont { get; } = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>Reads the current system theme.</summary>
    /// <returns>The theme.</returns>
    public static Theme Current()
    {
        bool dark = false;
        Color accent = Color.FromRgb(0x00, 0x78, 0xD4);
        try
        {
            using (var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                dark = personalize?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }

            using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (dwm?.GetValue("AccentColor") is int abgr)
            {
                accent = Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
            }
        }
        catch (System.Security.SecurityException)
        {
            // Keep the defaults: light theme, Windows blue.
        }

        return new Theme(dark, SystemParameters.HighContrast, accent);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + ((to.R - from.R) * amount)),
        (byte)(from.G + ((to.G - from.G) * amount)),
        (byte)(from.B + ((to.B - from.B) * amount)));

    private static double Luminance(Color color) => ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255;
}
