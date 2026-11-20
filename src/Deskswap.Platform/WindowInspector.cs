using Deskswap.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
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

    private bool IsElevated(WindowId id)
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
