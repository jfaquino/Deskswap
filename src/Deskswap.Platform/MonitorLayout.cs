using Deskswap.Core;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Deskswap.Platform;

/// <summary>A connected monitor.</summary>
/// <param name="Id">Stable identity (design §4), from the Display Configuration API when available.</param>
/// <param name="Handle">The current <c>HMONITOR</c>; valid only until the display configuration changes.</param>
/// <param name="DeviceName">The GDI device name, e.g. <c>\\.\DISPLAY1</c>; not stable.</param>
/// <param name="Bounds">The monitor rectangle in physical pixels.</param>
/// <param name="WorkArea">The monitor rectangle minus the taskbar and app bars.</param>
/// <param name="IsPrimary">Whether this is the primary monitor.</param>
public sealed record DisplayMonitor(MonitorId Id, nint Handle, string DeviceName, ScreenRect Bounds, ScreenRect WorkArea, bool IsPrimary);

/// <summary>
/// A snapshot of the connected monitors, ordered left to right then top to bottom (design §4). Capture a new
/// layout whenever the display configuration changes.
/// </summary>
public sealed class MonitorLayout
{
    private readonly Dictionary<nint, DisplayMonitor> _byHandle;
    private readonly Dictionary<MonitorId, DisplayMonitor> _byId;

    private MonitorLayout(IReadOnlyList<DisplayMonitor> monitors)
    {
        Monitors = monitors;
        _byHandle = monitors.ToDictionary(m => m.Handle);
        _byId = monitors.ToDictionary(m => m.Id);
    }

    /// <summary>Gets the monitors, in order.</summary>
    public IReadOnlyList<DisplayMonitor> Monitors { get; }

    /// <summary>Gets the stable ids of the monitors, in order.</summary>
    public IReadOnlyList<MonitorId> Ids => [.. Monitors.Select(m => m.Id)];

    /// <summary>Gets the primary monitor.</summary>
    public DisplayMonitor Primary => Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors[0];

    /// <summary>Reads the current monitor configuration from Windows.</summary>
    /// <returns>The layout.</returns>
    /// <exception cref="InvalidOperationException">Windows reports no monitors.</exception>
    public static MonitorLayout Capture()
    {
        var stableIds = ReadStableIds();
        var monitors = new List<DisplayMonitor>();

        unsafe
        {
            PInvoke.EnumDisplayMonitors(HDC.Null, (RECT?)null, (handle, _, _, _) =>
            {
                var info = new MONITORINFOEXW();
                info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
                if (PInvoke.GetMonitorInfo(handle, (MONITORINFO*)&info))
                {
                    string deviceName = info.szDevice.ToString();
                    string id = stableIds.TryGetValue(deviceName, out var path) ? path : deviceName;
                    monitors.Add(new DisplayMonitor(
                        new MonitorId(id),
                        (nint)handle.Value,
                        deviceName,
                        ToScreenRect(info.monitorInfo.rcMonitor),
                        ToScreenRect(info.monitorInfo.rcWork),
                        (info.monitorInfo.dwFlags & 1) != 0)); // MONITORINFOF_PRIMARY
                }

                return true;
            }, default);
        }

        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("Windows reported no monitors.");
        }

        // Two monitors could in theory report the same stable id (cloned outputs); keep ids unique.
        var unique = monitors
            .GroupBy(m => m.Id)
            .SelectMany(g => g.Select((m, i) => i == 0 ? m : m with { Id = new MonitorId($"{m.Id.Value}#{m.DeviceName}") }))
            .OrderBy(m => m.Bounds.Left)
            .ThenBy(m => m.Bounds.Top)
            .ToList();

        return new MonitorLayout(unique);
    }

    /// <summary>
    /// Reads a cheap fingerprint of the connected displays: monitor handles with their bounds and work areas. Two
    /// equal fingerprints a moment apart mean the displays have settled.
    /// </summary>
    /// <returns>The fingerprint.</returns>
    public static string ReadDisplaySignature()
    {
        var parts = new List<string>();
        unsafe
        {
            PInvoke.EnumDisplayMonitors(HDC.Null, (RECT?)null, (handle, _, _, _) =>
            {
                var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
                if (PInvoke.GetMonitorInfo(handle, &info))
                {
                    parts.Add(Describe(KeyOf(handle), ToScreenRect(info.rcMonitor), ToScreenRect(info.rcWork)));
                }

                return true;
            }, default);
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join('|', parts);
    }

    /// <summary>
    /// Gets a value indicating whether this layout still describes the connected displays. Cheap enough to call
    /// before every pass, unlike <see cref="Capture"/>.
    /// </summary>
    /// <returns><see langword="false"/> if a monitor was connected, disconnected, moved or resized since the capture.</returns>
    public bool MatchesCurrentDisplays() => ReadDisplaySignature() == Signature;

    /// <summary>Gets the fingerprint of the displays this layout was captured from.</summary>
    public string Signature
    {
        get
        {
            var parts = Monitors.Select(m => Describe(m.Handle, m.Bounds, m.WorkArea)).ToList();
            parts.Sort(StringComparer.Ordinal);
            return string.Join('|', parts);
        }
    }

    private static string Describe(nint handle, ScreenRect bounds, ScreenRect workArea) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{handle:X}:{bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}:{workArea.Left},{workArea.Top},{workArea.Right},{workArea.Bottom}");

    /// <summary>Gets a monitor by its stable id.</summary>
    /// <param name="id">The id.</param>
    /// <param name="monitor">The monitor, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if the monitor is in this layout.</returns>
    public bool TryGet(MonitorId id, out DisplayMonitor monitor) => _byId.TryGetValue(id, out monitor!);

    /// <summary>Gets the monitor a window is on (the one it overlaps most, or the nearest).</summary>
    /// <param name="window">The window handle.</param>
    /// <returns>The monitor, or <see langword="null"/> if it isn't in this layout (the configuration changed).</returns>
    public DisplayMonitor? MonitorOf(nint window)
    {
        var handle = PInvoke.MonitorFromWindow(new HWND(window), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return _byHandle.GetValueOrDefault(KeyOf(handle));
    }

    /// <summary>Gets the monitor under the mouse cursor: the target of shortcuts (design §2).</summary>
    /// <returns>The monitor, or <see langword="null"/> if it isn't in this layout.</returns>
    public DisplayMonitor? MonitorUnderCursor()
    {
        if (!PInvoke.GetCursorPos(out var point))
        {
            return null;
        }

        var handle = PInvoke.MonitorFromPoint(point, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return _byHandle.GetValueOrDefault(KeyOf(handle));
    }

    private static unsafe nint KeyOf(HMONITOR handle) => (nint)handle.Value;

    internal static ScreenRect ToScreenRect(RECT rect) => new(rect.left, rect.top, rect.right, rect.bottom);

    /// <summary>Maps GDI device names (<c>\\.\DISPLAYn</c>) to stable monitor device paths.</summary>
    private static unsafe Dictionary<string, string> ReadStableIds()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        const QUERY_DISPLAY_CONFIG_FLAGS flags = QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS;
        if (PInvoke.GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount) != WIN32_ERROR.ERROR_SUCCESS)
        {
            return result;
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        if (PInvoke.QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes) != WIN32_ERROR.ERROR_SUCCESS)
        {
            return result;
        }

        foreach (var path in paths.AsSpan(0, (int)pathCount))
        {
            var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            source.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            source.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
            source.header.adapterId = path.sourceInfo.adapterId;
            source.header.id = path.sourceInfo.id;

            var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            target.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            target.header.size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME);
            target.header.adapterId = path.targetInfo.adapterId;
            target.header.id = path.targetInfo.id;

            if (PInvoke.DisplayConfigGetDeviceInfo(&source.header) != 0 || PInvoke.DisplayConfigGetDeviceInfo(&target.header) != 0)
            {
                continue;
            }

            string gdiName = source.viewGdiDeviceName.ToString();
            string devicePath = target.monitorDevicePath.ToString();
            if (gdiName.Length > 0 && devicePath.Length > 0)
            {
                result.TryAdd(gdiName, devicePath);
            }
        }

        return result;
    }
}
