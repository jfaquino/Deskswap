using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace Deskswap.Platform;

/// <summary>Styling for Deskswap's own windows.</summary>
public static class WindowStyling
{
    /// <summary>Asks Windows 11 to draw rounded corners on a window. Does nothing on systems that don't support it.</summary>
    /// <param name="window">The window handle.</param>
    public static unsafe void UseRoundedCorners(nint window)
    {
        var preference = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        _ = PInvoke.DwmSetWindowAttribute(new HWND(window), DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &preference, sizeof(DWM_WINDOW_CORNER_PREFERENCE));
    }
}

/// <summary>Releases native icon handles, e.g. from <c>Bitmap.GetHicon</c>.</summary>
public static class NativeIcons
{
    /// <summary>Destroys an icon handle.</summary>
    /// <param name="icon">The icon handle.</param>
    /// <returns><see langword="true"/> if the handle was destroyed.</returns>
    public static bool Destroy(nint icon) => PInvoke.DestroyIcon(new Windows.Win32.UI.WindowsAndMessaging.HICON(icon));
}
