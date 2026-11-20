namespace Deskswap.Platform.Tests;

/// <summary>Sanity checks against the real display configuration of the machine running the tests.</summary>
public class MonitorLayoutTests
{
    [Fact]
    public void Capture_returns_ordered_monitors_with_unique_ids_and_one_primary()
    {
        var layout = MonitorLayout.Capture();

        Assert.NotEmpty(layout.Monitors);
        Assert.Equal(layout.Monitors.Count, layout.Ids.Distinct().Count());
        Assert.Single(layout.Monitors, m => m.IsPrimary);
        Assert.Equal(layout.Monitors.OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top), layout.Monitors);
        Assert.All(layout.Monitors, m => Assert.True(m.WorkArea.Width > 0 && m.WorkArea.Height > 0));
    }

    [Fact]
    public void Monitor_under_cursor_is_part_of_the_layout()
    {
        var layout = MonitorLayout.Capture();

        var monitor = layout.MonitorUnderCursor();

        Assert.NotNull(monitor);
        Assert.Contains(monitor, layout.Monitors);
    }
}
