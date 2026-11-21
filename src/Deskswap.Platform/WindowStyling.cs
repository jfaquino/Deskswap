using Deskswap.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

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

    /// <summary>
    /// Keeps a window out of screen captures, or lets them see it again. Deskswap's windows are normally visible to
    /// screenshots and recordings; this is only for the moment Deskswap takes its own snapshot while one of them is
    /// on screen (e.g. a switch indicator), and must be undone right after.
    /// </summary>
    /// <param name="window">The window handle.</param>
    /// <param name="hidden">Whether captures should leave the window out.</param>
    /// <returns><see langword="true"/> if Windows accepted it (Windows 10 2004 and later).</returns>
    public static bool SetHiddenFromCapture(nint window, bool hidden) =>
        PInvoke.SetWindowDisplayAffinity(new HWND(window), hidden ? WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE : WINDOW_DISPLAY_AFFINITY.WDA_NONE);

    /// <summary>Brings one of Deskswap's own windows to the foreground so it gets the keyboard.</summary>
    /// <param name="window">The window handle.</param>
    /// <returns><see langword="true"/> if it became the foreground window.</returns>
    public static bool Activate(nint window) => WindowOperations.Focus(window);

    /// <summary>Places one of Deskswap's own windows above all normal windows at exact physical-pixel bounds.</summary>
    /// <param name="window">The window handle.</param>
    /// <param name="bounds">The bounds, in screen pixels.</param>
    /// <returns><see langword="true"/> if the window was moved.</returns>
    public static bool PlaceTopmost(nint window, ScreenRect bounds) =>
        PInvoke.SetWindowPos(
            new HWND(window),
            HWND.HWND_TOPMOST,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);

    /// <summary>Makes a window ignore the mouse and never take focus, e.g. the drag preview.</summary>
    /// <param name="window">The window handle.</param>
    public static void MakeClickThrough(nint window)
    {
        var hwnd = new HWND(window);
        var style = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        style |= WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        _ = PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)style);
    }

    /// <summary>Gets the mouse cursor position.</summary>
    /// <param name="x">The x coordinate, in screen pixels.</param>
    /// <param name="y">The y coordinate, in screen pixels.</param>
    /// <returns><see langword="true"/> if it could be read.</returns>
    public static bool TryGetCursorPosition(out int x, out int y)
    {
        bool ok = PInvoke.GetCursorPos(out var point);
        x = point.X;
        y = point.Y;
        return ok;
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
