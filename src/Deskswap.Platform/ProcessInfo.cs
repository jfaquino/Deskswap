using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace Deskswap.Platform;

/// <summary>Reads process details needed for window identity (start time) and, later, rules (image path).</summary>
internal static class ProcessInfo
{
    /// <summary>Gets a process's start time as UTC ticks.</summary>
    /// <param name="processId">The process id.</param>
    /// <param name="startTicks">The start time, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if the process could be queried.</returns>
    public static bool TryGetStartTicks(uint processId, out long startTicks)
    {
        startTicks = 0;
        using var process = Open(processId);
        if (process.IsInvalid || !PInvoke.GetProcessTimes(process, out var creation, out _, out _, out _))
        {
            return false;
        }

        startTicks = ToFileTimeUtc(creation);
        return true;
    }

    /// <summary>Gets the full path of a process's executable.</summary>
    /// <param name="processId">The process id.</param>
    /// <returns>The path, or <see langword="null"/> if the process could not be queried.</returns>
    public static string? TryGetImagePath(uint processId)
    {
        using var process = Open(processId);
        if (process.IsInvalid)
        {
            return null;
        }

        Span<char> buffer = stackalloc char[1024];
        uint length = (uint)buffer.Length;
        return PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref length)
            ? buffer[..(int)length].ToString()
            : null;
    }

    private static SafeFileHandle Open(uint processId) =>
        new(PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId), ownsHandle: true);

    private static long ToFileTimeUtc(FILETIME time) => ((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
}
