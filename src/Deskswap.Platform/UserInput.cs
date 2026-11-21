using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Deskswap.Platform;

/// <summary>When the user last used the keyboard or mouse (in this session).</summary>
public static class UserInput
{
    /// <summary>Gets the tick count (milliseconds since startup, wrapping) of the last keyboard or mouse input.</summary>
    public static uint LastInputTick
    {
        get
        {
            var info = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
            return PInvoke.GetLastInputInfo(ref info) ? info.dwTime : 0;
        }
    }
}
