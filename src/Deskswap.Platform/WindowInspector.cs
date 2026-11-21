using Deskswap.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Deskswap.Platform;

/// <summary>Finds the windows Deskswap manages (design §7) and reads their identity and state.</summary>
public sealed class WindowInspector
{
    /// <summary>Shell and system window classes that are never managed.</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow",
        "ApplicationManager_ImmersiveShellWindow",
        "EdgeUiInputTopWndClass",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland",
    };

    private readonly uint _ownProcessId = (uint)Environment.ProcessId;

    /// <summary>Per process instance: whether it runs at a higher integrity level than Deskswap.</summary>
    private readonly Dictionary<(int ProcessId, long StartTicks), bool> _elevated = [];

    /// <summary>
    /// Gets how many eligible windows the last <see cref="Observe"/> skipped because their app runs at a higher
    /// integrity level (e.g. as administrator). Deskswap can't hide those, so they stay visible on every desktop
    /// (design §6.4).
    /// </summary>
    public int ElevatedWindowsSkipped { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the last <see cref="Observe"/> found a window on a monitor the layout doesn't
    /// know: the layout is stale and the observation must not be trusted (windows would look closed).
    /// </summary>
    public bool SawUnknownMonitor { get; private set; }

    /// <summary>Lists all top-level windows in z-order, top-most first.</summary>
    /// <returns>The window handles.</returns>
    public static IReadOnlyList<nint> EnumerateTopLevel()
    {
        var windows = new List<nint>();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            windows.Add(ToNint(hwnd));
            return true;
        }, default);
        return windows;
    }

    /// <summary>Gets the current foreground window.</summary>
    public static nint ForegroundWindow => ToNint(PInvoke.GetForegroundWindow());

    /// <summary>Gets a value indicating whether a window is visible (it may be minimized).</summary>
    /// <param name="window">The window handle.</param>
    /// <returns><see langword="true"/> if the window has the visible style.</returns>
    public static bool IsVisible(nint window) => PInvoke.IsWindowVisible(new HWND(window));

    /// <summary>Gets a value indicating whether a window's app has stopped responding.</summary>
    /// <param name="window">The window handle.</param>
    /// <returns><see langword="true"/> if Windows considers the window hung.</returns>
    public static bool IsHung(nint window) => PInvoke.IsHungAppWindow(new HWND(window));

    /// <summary>Gets a value indicating whether a window is minimized.</summary>
    /// <param name="window">The window handle.</param>
    /// <returns><see langword="true"/> if the window is minimized.</returns>
    public static bool IsMinimized(nint window) => PInvoke.IsIconic(new HWND(window));

    /// <summary>Gets a window's title.</summary>
    /// <param name="window">The window handle.</param>
    /// <returns>The title, or an empty string.</returns>
    public static string GetTitle(nint window)
    {
        var hwnd = new HWND(window);
        int length = PInvoke.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        Span<char> buffer = length < 512 ? stackalloc char[length + 1] : new char[length + 1];
        int copied = PInvoke.GetWindowText(hwnd, buffer);
        return buffer[..Math.Max(0, copied)].ToString();
    }

    /// <summary>Gets a window's class name.</summary>
    /// <param name="window">The window handle.</param>
    /// <returns>The class name, or an empty string.</returns>
    public static string GetClassName(nint window)
    {
        Span<char> buffer = stackalloc char[257];
        int copied = PInvoke.GetClassName(new HWND(window), buffer);
        return buffer[..Math.Max(0, copied)].ToString();
    }

    /// <summary>
    /// Decides whether Deskswap should manage a window (design §7): top-level, visible, unowned, not cloaked,
    /// titled, not a tool window, not a shell window, not ours. Cloaking also excludes windows on other Windows
    /// virtual desktops.
    /// </summary>
    /// <param name="window">The window handle.</param>
    /// <returns><see langword="true"/> if the window is eligible.</returns>
    public bool IsEligible(nint window) => IsVisible(window) && IsManageableIgnoringVisibility(window);

    /// <summary>Reads the identity of a live window.</summary>
    /// <param name="window">The window handle.</param>
    /// <param name="id">The identity, when the method returns <see langword="true"/>.</param>
    /// <param name="startTimes">Optional cache of process start times for one pass over many windows.</param>
    /// <returns><see langword="true"/> if the window exists and its process could be queried.</returns>
    public static bool TryGetId(nint window, out WindowId id, Dictionary<uint, long>? startTimes = null)
    {
        id = default;
        uint processId = ProcessIdOf(window);
        if (processId == 0)
        {
            return false;
        }

        if (startTimes is null || !startTimes.TryGetValue(processId, out long startTicks))
        {
            if (!ProcessInfo.TryGetStartTicks(processId, out startTicks))
            {
                return false;
            }

            startTimes?.Add(processId, startTicks);
        }

        id = new WindowId(window, (int)processId, startTicks);
        return true;
    }

    /// <summary>
    /// Gets a value indicating whether a window id still refers to the same live window: the handle exists and
    /// belongs to the same process instance. Guards against Windows reusing handles.
    /// </summary>
    /// <param name="id">The window identity.</param>
    /// <param name="startTimes">Optional cache of process start times.</param>
    /// <returns><see langword="true"/> if the window is alive and unchanged.</returns>
    public static bool IsAlive(WindowId id, Dictionary<uint, long>? startTimes = null) =>
        PInvoke.IsWindow(new HWND((nint)id.Handle)) && TryGetId((nint)id.Handle, out var current, startTimes) && current == id;

    /// <summary>
    /// Observes the windows the pool should know about: every eligible visible window, plus every window Deskswap
    /// hid that is still alive. Used for <see cref="DesktopPool.Reconcile"/>.
    /// </summary>
    /// <param name="layout">The current monitor layout.</param>
    /// <param name="hiddenByDeskswap">Windows Deskswap hid.</param>
    /// <returns>The observed windows.</returns>
    public IReadOnlyList<ObservedWindow> Observe(MonitorLayout layout, IEnumerable<WindowId> hiddenByDeskswap)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(hiddenByDeskswap);

        var startTimes = new Dictionary<uint, long>();
        var observed = new List<ObservedWindow>();
        var seen = new HashSet<WindowId>();
        int elevated = 0;
        SawUnknownMonitor = false;

        foreach (nint window in EnumerateTopLevel())
        {
            if (!IsEligible(window) || !TryGetId(window, out var id, startTimes))
            {
                continue;
            }

            if (layout.MonitorOf(window) is not { } monitor)
            {
                SawUnknownMonitor = true;
                continue;
            }

            if (IsElevated(id))
            {
                elevated++;
                continue;
            }

            observed.Add(new ObservedWindow(id, monitor.Id, IsVisible: true));
            seen.Add(id);
        }

        ElevatedWindowsSkipped = elevated;

        foreach (var id in hiddenByDeskswap)
        {
            if (seen.Contains(id) || !IsAlive(id, startTimes))
            {
                continue;
            }

            if (layout.MonitorOf((nint)id.Handle) is not { } monitor)
            {
                SawUnknownMonitor = true;
                continue;
            }

            observed.Add(new ObservedWindow(id, monitor.Id, IsVisible((nint)id.Handle)));
        }

        return observed;
    }

    /// <summary>
    /// Gets a value indicating whether a window's app runs at a higher integrity level than Deskswap (e.g. as
    /// administrator, like Task Manager). Windows keeps Deskswap from hiding its windows, and keeps the keys typed
    /// into them from Deskswap's keyboard hook, so shortcuts don't work while one is in front (design §6.4).
    /// </summary>
    /// <param name="id">The window.</param>
    /// <returns><see langword="true"/> if it is out of Deskswap's reach.</returns>
    public bool IsElevated(WindowId id)
    {
        var key = (id.ProcessId, id.ProcessStartTicks);
        if (!_elevated.TryGetValue(key, out bool elevated))
        {
            elevated = ProcessInfo.IsMoreTrustedThanUs((uint)id.ProcessId);
            _elevated[key] = elevated;
        }

        return elevated;
    }

    private bool IsManageableIgnoringVisibility(nint window)
    {
        var hwnd = new HWND(window);
        if (PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER) != HWND.Null)
        {
            return false;
        }

        uint processId = ProcessIdOf(window);
        if (processId == 0 || processId == _ownProcessId)
        {
            return false;
        }

        var exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if (exStyle.HasFlag(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) && !exStyle.HasFlag(WINDOW_EX_STYLE.WS_EX_APPWINDOW))
        {
            return false;
        }

        if (PInvoke.GetWindowTextLength(hwnd) == 0 || IsCloaked(hwnd))
        {
            return false;
        }

        return !ShellClasses.Contains(GetClassName(window));
    }

    /// <summary>Gets a window's visible frame and its full rectangle (which includes invisible resize borders).</summary>
    /// <param name="window">The window handle.</param>
    /// <param name="frame">The visible frame, in screen pixels.</param>
    /// <param name="outer">The window rectangle, in screen pixels.</param>
    /// <returns><see langword="true"/> if the window exists.</returns>
    public static unsafe bool TryGetFrame(nint window, out ScreenRect frame, out ScreenRect outer)
    {
        var hwnd = new HWND(window);
        frame = outer = default;
        if (!PInvoke.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        outer = MonitorLayout.ToScreenRect(rect);
        RECT visible;
        frame = PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &visible, (uint)sizeof(RECT)).Succeeded
            ? MonitorLayout.ToScreenRect(visible)
            : outer;
        return true;
    }

    /// <summary>Gets the size a window has when not minimized or maximized.</summary>
    /// <param name="window">The window handle.</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    /// <returns><see langword="true"/> if the window exists.</returns>
    public static bool TryGetRestoredSize(nint window, out int width, out int height)
    {
        var placement = new WINDOWPLACEMENT { length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        bool ok = PInvoke.GetWindowPlacement(new HWND(window), ref placement);
        width = ok ? placement.rcNormalPosition.right - placement.rcNormalPosition.left : 0;
        height = ok ? placement.rcNormalPosition.bottom - placement.rcNormalPosition.top : 0;
        return ok;
    }

    /// <summary>
    /// Gets where a window is, for laying it out in the overview: its visible frame, or for a minimized window the
    /// place it returns to when restored (its monitor's work area if it was maximized).
    /// </summary>
    /// <param name="window">The window handle.</param>
    /// <param name="primary">The primary monitor (placement uses coordinates relative to its work area).</param>
    /// <param name="bounds">The bounds, in screen pixels.</param>
    /// <returns><see langword="true"/> if the window exists.</returns>
    public static bool TryGetLayoutBounds(nint window, DisplayMonitor primary, out ScreenRect bounds)
    {
        ArgumentNullException.ThrowIfNull(primary);
        bounds = default;
        var hwnd = new HWND(window);
        if (PInvoke.IsIconic(hwnd))
        {
            var placement = new WINDOWPLACEMENT { length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!PInvoke.GetWindowPlacement(hwnd, ref placement))
            {
                return false;
            }

            // Maximized before it was minimized: it comes back maximized, not at its normal size.
            if (placement.flags.HasFlag(WINDOWPLACEMENT_FLAGS.WPF_RESTORETOMAXIMIZED) && TryGetWorkAreaOf(hwnd, out bounds))
            {
                return true;
            }

            bounds = MonitorLayout.ToScreenRect(placement.rcNormalPosition)
                .Offset(primary.WorkArea.Left - primary.Bounds.Left, primary.WorkArea.Top - primary.Bounds.Top);
            return true;
        }

        return TryGetFrame(window, out bounds, out _);
    }

    /// <summary>Gets the work area of a window's monitor (for a minimized window, the one it was on).</summary>
    private static unsafe bool TryGetWorkAreaOf(HWND window, out ScreenRect workArea)
    {
        var monitor = PInvoke.MonitorFromWindow(window, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(monitor, &info))
        {
            workArea = default;
            return false;
        }

        workArea = MonitorLayout.ToScreenRect(info.rcWork);
        return true;
    }

    /// <summary>Gets the executable file of the app a window belongs to.</summary>
    /// <param name="window">The window.</param>
    /// <returns>The full path, or <see langword="null"/> if the process can't be queried.</returns>
    public static string? GetAppPath(WindowId window) => ProcessInfo.TryGetImagePath((uint)window.ProcessId);

    /// <summary>Gets a window's large icon without waiting on an app that isn't responding.</summary>
    /// <param name="window">The window handle.</param>
    /// <returns>The icon handle (owned by the window; don't destroy it), or 0.</returns>
    public static unsafe nint GetIcon(nint window)
    {
        const uint IconBig = 1;
        const uint IconSmall2 = 2;
        var hwnd = new HWND(window);
        foreach (uint kind in (ReadOnlySpan<uint>)[IconBig, IconSmall2])
        {
            nuint result = 0;
            var sent = PInvoke.SendMessageTimeout(
                hwnd,
                PInvoke.WM_GETICON,
                new WPARAM(kind),
                new LPARAM(0),
                SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG | SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_BLOCK,
                50,
                &result);
            if (sent != 0 && result != 0)
            {
                return (nint)result;
            }
        }

        // Icon handles fit in 32 bits, so the 32-bit getter is enough on 64-bit Windows.
        return (nint)(int)PInvoke.GetClassLong(hwnd, GET_CLASS_LONG_INDEX.GCL_HICON);
    }

    private static unsafe bool IsCloaked(HWND hwnd)
    {
        int cloaked = 0;
        var result = PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int));
        return result.Succeeded && cloaked != 0;
    }

    private static uint ProcessIdOf(nint window)
    {
        PInvoke.GetWindowThreadProcessId(new HWND(window), out uint processId);
        return processId;
    }

    private static unsafe nint ToNint(HWND hwnd) => (nint)hwnd.Value;
}
