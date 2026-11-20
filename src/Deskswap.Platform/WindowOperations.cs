using Deskswap.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Deskswap.Platform;

/// <summary>Changes real windows: hide, show, move between monitors, focus.</summary>
/// <remarks>
/// Hide and show use <c>ShowWindowAsync</c> so a hung application can never block Deskswap.
/// </remarks>
public static class WindowOperations
{
    /// <summary>Hides a window.</summary>
    /// <param name="window">The window.</param>
    /// <returns><see langword="true"/> if the request was posted.</returns>
    public static bool Hide(WindowId window) =>
        PInvoke.ShowWindowAsync(new HWND((nint)window.Handle), SHOW_WINDOW_CMD.SW_HIDE);

    /// <summary>Shows a window in its current state (minimized stays minimized) without activating it.</summary>
    /// <param name="window">The window.</param>
    /// <returns><see langword="true"/> if the request was posted.</returns>
    public static bool Show(WindowId window) =>
        PInvoke.ShowWindowAsync(new HWND((nint)window.Handle), SHOW_WINDOW_CMD.SW_SHOWNA);

    /// <summary>
    /// Moves a window from one monitor to another, keeping its relative position (design §3.5). Maximized and
    /// minimized windows keep their state on the new monitor.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="from">The monitor the window is on.</param>
    /// <param name="to">The monitor to move it to.</param>
    /// <param name="primary">The primary monitor (needed to convert placement coordinates).</param>
    /// <returns><see langword="true"/> if the window was moved.</returns>
    public static bool Relocate(WindowId window, DisplayMonitor from, DisplayMonitor to, DisplayMonitor primary)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(primary);

        var hwnd = new HWND((nint)window.Handle);
        if (PInvoke.IsZoomed(hwnd) || PInvoke.IsIconic(hwnd))
        {
            return RelocateByPlacement(hwnd, from, to, primary);
        }

        if (!PInvoke.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        var moved = WindowGeometry.Relocate(MonitorLayout.ToScreenRect(rect), from.WorkArea, to.WorkArea);
        return PInvoke.SetWindowPos(
            hwnd,
            HWND.Null,
            moved.Left,
            moved.Top,
            moved.Width,
            moved.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
    }

    /// <summary>Brings a window to the front and gives it keyboard focus.</summary>
    /// <param name="window">The window handle.</param>
    /// <returns><see langword="true"/> if Windows made it the foreground window.</returns>
    public static bool Focus(nint window)
    {
        var target = new HWND(window);
        var foreground = PInvoke.GetForegroundWindow();
        uint foregroundThread = foreground == HWND.Null ? 0 : PInvoke.GetWindowThreadProcessId(foreground);
        uint currentThread = PInvoke.GetCurrentThreadId();

        // Windows only lets the foreground thread change the foreground window; borrow its input state briefly.
        bool attached = foregroundThread != 0 && foregroundThread != currentThread
            && PInvoke.AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            PInvoke.BringWindowToTop(target);
            return PInvoke.SetForegroundWindow(target);
        }
        finally
        {
            if (attached)
            {
                PInvoke.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    /// <summary>
    /// Moves a maximized or minimized window by editing its restore rectangle. Placement rectangles use workspace
    /// coordinates, which are offset from screen coordinates by the primary monitor's taskbar.
    /// </summary>
    private static bool RelocateByPlacement(HWND hwnd, DisplayMonitor from, DisplayMonitor to, DisplayMonitor primary)
    {
        var placement = new WINDOWPLACEMENT { length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!PInvoke.GetWindowPlacement(hwnd, ref placement))
        {
            return false;
        }

        int dx = primary.WorkArea.Left - primary.Bounds.Left;
        int dy = primary.WorkArea.Top - primary.Bounds.Top;
        var normal = MonitorLayout.ToScreenRect(placement.rcNormalPosition).Offset(dx, dy);
        var moved = WindowGeometry.Relocate(normal, from.WorkArea, to.WorkArea).Offset(-dx, -dy);

        placement.rcNormalPosition = new RECT(moved.Left, moved.Top, moved.Right, moved.Bottom);

        // Maximized: SW_SHOWMAXIMIZED re-maximizes on the monitor of the new restore rectangle. For a hidden window
        // this shows it early, which is fine: relocated windows are shown right after (PoolChanges order).
        // Minimized: keep it minimized and never activate it.
        if (placement.showCmd == SHOW_WINDOW_CMD.SW_SHOWMINIMIZED)
        {
            placement.showCmd = SHOW_WINDOW_CMD.SW_SHOWMINNOACTIVE;
        }

        return PInvoke.SetWindowPlacement(hwnd, in placement);
    }
}
