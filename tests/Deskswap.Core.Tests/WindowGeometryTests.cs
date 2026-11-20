namespace Deskswap.Core.Tests;

/// <summary>Design §3.5: windows keep their relative position when they travel between monitors.</summary>
public class WindowGeometryTests
{
    private static readonly ScreenRect FullHd = new(0, 0, 1920, 1040);
    private static readonly ScreenRect FourK = new(1920, 0, 1920 + 3840, 2120);

    [Fact]
    public void Relocate_keeps_relative_position_and_size()
    {
        var window = ScreenRect.FromSize(960, 520, 800, 400); // starts at the centre of FullHd

        var moved = WindowGeometry.Relocate(window, FullHd, FourK);

        Assert.Equal(ScreenRect.FromSize(1920 + 1920, 1060, 800, 400), moved);
    }

    [Fact]
    public void Relocate_shrinks_a_window_larger_than_the_target()
    {
        var window = ScreenRect.FromSize(1920, 0, 3840, 2120);

        var moved = WindowGeometry.Relocate(window, FourK, FullHd);

        Assert.Equal(FullHd, moved);
    }

    [Fact]
    public void Relocate_clamps_the_window_inside_the_target()
    {
        var window = ScreenRect.FromSize(1800, 1000, 600, 300); // hangs off the bottom-right of FullHd

        var moved = WindowGeometry.Relocate(window, FullHd, FullHd);

        Assert.Equal(ScreenRect.FromSize(1320, 740, 600, 300), moved);
    }

    [Fact]
    public void Relocate_to_a_monitor_left_of_the_primary_handles_negative_coordinates()
    {
        var leftMonitor = new ScreenRect(-1280, 0, 0, 984);
        var window = ScreenRect.FromSize(0, 0, 640, 480);

        var moved = WindowGeometry.Relocate(window, FullHd, leftMonitor);

        Assert.Equal(ScreenRect.FromSize(-1280, 0, 640, 480), moved);
    }
}
