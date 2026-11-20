using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
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

    /// <summary>Gets the mandatory integrity level of the current process (e.g. 0x2000 medium, 0x3000 high).</summary>
    public static int CurrentIntegrityLevel { get; } = ReadIntegrityLevel(new SafeFileHandle(PInvoke.GetCurrentProcess(), ownsHandle: false)) ?? 0x2000;

    /// <summary>
    /// Gets a value indicating whether a process runs at a higher integrity level than Deskswap (e.g. an app run as
    /// administrator). Windows won't let Deskswap hide such windows. A process whose level can't be read is treated
    /// as higher, because that is the usual reason it can't be read.
    /// </summary>
    /// <param name="processId">The process id.</param>
    /// <returns><see langword="true"/> if Deskswap can't manage the process's windows.</returns>
    public static bool IsMoreTrustedThanUs(uint processId)
    {
        using var process = Open(processId);
        return process.IsInvalid || (ReadIntegrityLevel(process) ?? int.MaxValue) > CurrentIntegrityLevel;
    }

    private static unsafe int? ReadIntegrityLevel(SafeHandle process)
    {
        if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, out var token))
        {
            return null;
        }

        using (token)
        {
            PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, null, 0, out uint size);
            if (size == 0)
            {
                return null;
            }

            byte* buffer = stackalloc byte[(int)size];
            if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buffer, size, out _))
            {
                return null;
            }

            var label = (TOKEN_MANDATORY_LABEL*)buffer;
            byte count = *PInvoke.GetSidSubAuthorityCount(label->Label.Sid);
            return count == 0 ? null : (int)*PInvoke.GetSidSubAuthority(label->Label.Sid, (uint)(count - 1));
        }
    }

    private static SafeFileHandle Open(uint processId) =>
        new(PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId), ownsHandle: true);

    private static long ToFileTimeUtc(FILETIME time) => ((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
}
