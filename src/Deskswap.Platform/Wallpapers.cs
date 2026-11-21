using System.Runtime.InteropServices;
using Deskswap.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Deskswap.Platform;

/// <summary>How Windows fits a wallpaper image to a monitor.</summary>
public enum WallpaperFit
{
    /// <summary>Scaled to cover the monitor, cropping the overflow.</summary>
    Fill,

    /// <summary>Scaled to fit inside the monitor, with the background color around it.</summary>
    Fit,

    /// <summary>Stretched to the monitor's size.</summary>
    Stretch,

    /// <summary>Centered at its own size.</summary>
    Center,

    /// <summary>Repeated.</summary>
    Tile,

    /// <summary>One image across all monitors.</summary>
    Span,
}

/// <summary>A monitor's desktop wallpaper.</summary>
/// <param name="ImagePath">The image file, or <see langword="null"/> for a solid color.</param>
/// <param name="Fit">How the image is fitted.</param>
/// <param name="BackgroundRed">Red part of the background color.</param>
/// <param name="BackgroundGreen">Green part of the background color.</param>
/// <param name="BackgroundBlue">Blue part of the background color.</param>
public sealed record Wallpaper(string? ImagePath, WallpaperFit Fit, byte BackgroundRed, byte BackgroundGreen, byte BackgroundBlue);

/// <summary>
/// Reads each monitor's wallpaper through the documented <c>IDesktopWallpaper</c> shell interface, for the overview's
/// backdrop and desktop cards (overview-ux.md §3).
/// </summary>
public static class Wallpapers
{
    /// <summary>Reads the wallpaper of every monitor in a layout.</summary>
    /// <param name="layout">The monitors.</param>
    /// <returns>The wallpapers found, by monitor; monitors Windows doesn't report are missing.</returns>
    public static IReadOnlyDictionary<MonitorId, Wallpaper> Read(MonitorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return OperatingSystem.IsWindowsVersionAtLeast(8) ? ReadAll(layout) : new Dictionary<MonitorId, Wallpaper>();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    private static unsafe Dictionary<MonitorId, Wallpaper> ReadAll(MonitorLayout layout)
    {
        var result = new Dictionary<MonitorId, Wallpaper>();
        IDesktopWallpaper? shell = null;
        try
        {
            shell = (IDesktopWallpaper)new DesktopWallpaper();
            shell.GetPosition(out var position);
            shell.GetBackgroundColor(out var color);
            uint rgb = color.Value;
            var fit = position switch
            {
                DESKTOP_WALLPAPER_POSITION.DWPOS_FIT => WallpaperFit.Fit,
                DESKTOP_WALLPAPER_POSITION.DWPOS_STRETCH => WallpaperFit.Stretch,
                DESKTOP_WALLPAPER_POSITION.DWPOS_CENTER => WallpaperFit.Center,
                DESKTOP_WALLPAPER_POSITION.DWPOS_TILE => WallpaperFit.Tile,
                DESKTOP_WALLPAPER_POSITION.DWPOS_SPAN => WallpaperFit.Span,
                _ => WallpaperFit.Fill,
            };

            shell.GetMonitorDevicePathCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                uint index = i;
                string? id = ReadString(p => shell.GetMonitorDevicePathAt(index, p));
                if (id is null || Match(layout, shell, id) is not { } monitor)
                {
                    continue;
                }

                string? path = ReadString(p =>
                {
                    fixed (char* monitorId = id)
                    {
                        shell.GetWallpaper(new PCWSTR(monitorId), p);
                    }
                });
                result[monitor] = new Wallpaper(
                    string.IsNullOrEmpty(path) || !File.Exists(path) ? null : path,
                    fit,
                    (byte)(rgb & 0xFF),
                    (byte)((rgb >> 8) & 0xFF),
                    (byte)((rgb >> 16) & 0xFF));
            }
        }
        catch (COMException)
        {
            // The shell isn't available (e.g. no Explorer); the overview falls back to the screen image.
        }
        catch (InvalidCastException)
        {
            // Same: the wallpaper interface isn't offered.
        }
        finally
        {
            if (shell is not null)
            {
                Marshal.ReleaseComObject(shell);
            }
        }

        return result;
    }

    /// <summary>Finds which of our monitors a shell monitor path is: by id, or else by bounds.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
    private static MonitorId? Match(MonitorLayout layout, IDesktopWallpaper shell, string id)
    {
        foreach (var monitor in layout.Monitors)
        {
            if (string.Equals(monitor.Id.Value, id, StringComparison.OrdinalIgnoreCase))
            {
                return monitor.Id;
            }
        }

        try
        {
            shell.GetMonitorRECT(id, out var rect);
            var bounds = MonitorLayout.ToScreenRect(rect);
            return layout.Monitors.FirstOrDefault(m => m.Bounds == bounds)?.Id;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private unsafe delegate void StringCall(PWSTR* result);

    private static unsafe string? ReadString(StringCall call)
    {
        PWSTR text = default;
        call(&text);
        try
        {
            return text.Value is null ? null : text.ToString();
        }
        finally
        {
            if (text.Value is not null)
            {
                Marshal.FreeCoTaskMem((IntPtr)text.Value);
            }
        }
    }
}
