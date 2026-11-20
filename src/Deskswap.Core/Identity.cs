namespace Deskswap.Core;

/// <summary>Identifies a desktop for its whole lifetime, independent of its position (number) in the pool.</summary>
/// <param name="Value">The unique value.</param>
public readonly record struct DesktopId(Guid Value)
{
    /// <summary>Creates a new, unique desktop id.</summary>
    /// <returns>A desktop id that has never been used before.</returns>
    public static DesktopId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("N")[..8];
}

/// <summary>
/// Stable identity of a physical monitor, such as its EDID-based device path. Unlike <c>\\.\DISPLAYn</c>
/// names, it survives docking and reboots.
/// </summary>
/// <param name="Value">The platform-specific stable id.</param>
public readonly record struct MonitorId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Identity of a live top-level window. The owning process is part of the identity so that a window
/// handle reused by Windows is never confused with the window that held it before.
/// </summary>
/// <param name="Handle">The window handle.</param>
/// <param name="ProcessId">The id of the process that owns the window.</param>
/// <param name="ProcessStartTicks">The UTC start time of that process, in ticks.</param>
public readonly record struct WindowId(long Handle, int ProcessId, long ProcessStartTicks);
