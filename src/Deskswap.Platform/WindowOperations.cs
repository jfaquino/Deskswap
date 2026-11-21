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

    /// <summary>Restores a minimized window.</summary>
    /// <param name="window">The window handle.</param>
    /// <returns><see langword="true"/> if the request was posted.</returns>
    public static bool Restore(nint window) =>
        PInvoke.ShowWindowAsync(new HWND(window), SHOW_WINDOW_CMD.SW_RESTORE);

    /// <summary>Sorts windows by their current place in the z-order, top-most first.</summary>
    /// <param name="windows">The windows.</param>
    /// <returns>The windows, top-most first; windows that no longer exist come last.</returns>
    public static IReadOnlyList<WindowId> TopMostFirst(IEnumerable<WindowId> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var position = WindowInspector.EnumerateTopLevel()
            .Select((handle, index) => (handle, index))
            .ToDictionary(x => x.handle, x => x.index);
        return [.. windows.OrderBy(w => position.GetValueOrDefault((nint)w.Handle, int.MaxValue))];
    }

    /// <summary>
    /// Shows windows on top of all others without activating them, in the given order: each window lands on top
    /// of the previous one, so pass them bottom-most first to rebuild a stack.
    /// </summary>
    /// <remarks>
    /// Windows places every window it shows on top, and asynchronous calls may run in any order, so this uses
    /// synchronous calls. A window that isn't responding gets the asynchronous <see cref="Show"/> instead, so a
    /// hung application can never block Deskswap.
    /// </remarks>
    /// <param name="bottomMostFirst">The windows, bottom-most first.</param>
    /// <returns>The number of windows shown or posted.</returns>
    public static int ShowStacked(IEnumerable<WindowId> bottomMostFirst)
    {
        ArgumentNullException.ThrowIfNull(bottomMostFirst);
        int shown = 0;
        foreach (var window in bottomMostFirst)
        {
            var hwnd = new HWND((nint)window.Handle);
            bool ok = PInvoke.IsHungAppWindow(hwnd)
                ? PInvoke.ShowWindowAsync(hwnd, SHOW_WINDOW_CMD.SW_SHOWNA)
                : PInvoke.SetWindowPos(
                    hwnd,
                    HWND.Null, // HWND_TOP
                    0,
                    0,
                    0,
                    0,
                    SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOMOVE
                        | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
            shown += ok ? 1 : 0;
        }

        return shown;
    }

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

        // Update only the restore rectangle; keep the current visibility and state, and never activate.
        bool visible = PInvoke.IsWindowVisible(hwnd);
        bool maximized = PInvoke.IsZoomed(hwnd);
        placement.showCmd = !visible ? SHOW_WINDOW_CMD.SW_HIDE
            : maximized ? SHOW_WINDOW_CMD.SW_SHOWNA
            : SHOW_WINDOW_CMD.SW_SHOWMINNOACTIVE;
        if (!PInvoke.SetWindowPlacement(hwnd, in placement))
        {
            return false;
        }

        // Re-applying "maximized" does not move a window that is already maximized, so move it while it stays
        // maximized: same overhang around the work area, on the target monitor.
        return !maximized || MoveMaximized(hwnd, from, to);
    }

    private static bool MoveMaximized(HWND hwnd, DisplayMonitor from, DisplayMonitor to)
    {
        if (!PInvoke.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        var current = MonitorLayout.ToScreenRect(rect);
        return PInvoke.SetWindowPos(
            hwnd,
            HWND.Null,
            to.WorkArea.Left + (current.Left - from.WorkArea.Left),
            to.WorkArea.Top + (current.Top - from.WorkArea.Top),
            current.Width + (to.WorkArea.Width - from.WorkArea.Width),
            current.Height + (to.WorkArea.Height - from.WorkArea.Height),
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
    }
}
