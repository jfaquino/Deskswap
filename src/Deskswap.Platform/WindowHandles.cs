using Windows.Win32;
using Windows.Win32.Foundation;

namespace Deskswap.Platform;

/// <summary>Thin wrappers over Win32 window-handle queries.</summary>
public static class WindowHandles
{
    /// <summary>The window that currently has keyboard focus, or <see cref="IntPtr.Zero"/>.</summary>
    public static nint Foreground => PInvoke.GetForegroundWindow();

    /// <summary>Whether <paramref name="handle"/> still identifies an existing window.</summary>
    public static bool Exists(nint handle) => PInvoke.IsWindow(new HWND(handle));
}
