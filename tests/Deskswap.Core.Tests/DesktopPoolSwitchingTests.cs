using static Deskswap.Core.Tests.PoolTestKit;

namespace Deskswap.Core.Tests;

/// <summary>Design §3.2 (first run) and §3.3 (switching).</summary>
public class DesktopPoolSwitchingTests
{
    [Fact]
    public void Create_makes_one_desktop_per_monitor_shown_in_order()
    {
        var pool = DesktopPool.Create([Left, Right]);

        Assert.Equal(2, pool.DesktopCount);
        Assert.Equal(1, pool.GetShownNumber(Left));
        Assert.Equal(2, pool.GetShownNumber(Right));
        AssertInvariants(pool);
    }

    [Fact]
    public void Create_rejects_no_monitors_and_duplicates()
    {
        Assert.Throws<ArgumentException>(() => DesktopPool.Create([]));
        Assert.Throws<ArgumentException>(() => DesktopPool.Create([Left, Left]));
    }

    [Fact]
    public void GoTo_free_desktop_hides_outgoing_and_shows_incoming_windows()
    {
        var pool = Pool(3);
        PlaceWindow(pool, W(1), 1, Left);
        PlaceWindow(pool, W(3), 3, Left);

        var changes = pool.GoTo(Left, 3);

        Assert.Equal(3, pool.GetShownNumber(Left));
        Assert.Equal([W(1)], changes.Hide);
        Assert.Equal([W(3)], changes.Show);
        Assert.Empty(changes.Relocations);
        Assert.Equal(new Indicator(Left, 3, null, IndicatorKind.Switched), Assert.Single(changes.Indicators));
        AssertInvariants(pool);
    }

    [Fact]
    public void GoTo_desktop_shown_on_other_monitor_swaps_and_windows_travel()
    {
        var pool = Pool(3);
        PlaceWindow(pool, W(1), 1, Left);
        PlaceWindow(pool, W(2), 2, Right);

        var changes = pool.GoTo(Left, 2);

        Assert.Equal(2, pool.GetShownNumber(Left));
        Assert.Equal(1, pool.GetShownNumber(Right));
        Assert.Empty(changes.Hide);
        Assert.Empty(changes.Show);
        Assert.Contains(new WindowRelocation(W(1), Left, Right), changes.Relocations);
        Assert.Contains(new WindowRelocation(W(2), Right, Left), changes.Relocations);
        Assert.Contains(new Indicator(Left, 2, null, IndicatorKind.Switched), changes.Indicators);
        Assert.Contains(new Indicator(Right, 1, null, IndicatorKind.Switched), changes.Indicators);
        AssertInvariants(pool);
    }

    [Fact]
    public void GoTo_current_desktop_only_shows_indicator()
    {
        var pool = Pool(3);

        var changes = pool.GoTo(Left, 1);

        Assert.False(changes.HasWindowChanges);
        Assert.Equal(IndicatorKind.Unchanged, Assert.Single(changes.Indicators).Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void GoTo_missing_number_reports_not_available(int number)
    {
        var pool = Pool(3);

        var changes = pool.GoTo(Left, number);

        Assert.False(changes.HasWindowChanges);
        Assert.Equal(new Indicator(Left, number, null, IndicatorKind.NotAvailable), Assert.Single(changes.Indicators));
        Assert.Equal(1, pool.GetShownNumber(Left));
    }

    [Fact]
    public void GoTo_desktop_last_shown_on_other_monitor_relocates_its_windows()
    {
        var pool = Pool(3);
        PlaceWindow(pool, W(3), 3, Right); // desktop 3 was last shown on Right

        var changes = pool.GoTo(Left, 3);

        Assert.Equal([new WindowRelocation(W(3), Right, Left)], changes.Relocations);
        Assert.Equal([W(3)], changes.Show);
    }

    [Fact]
    public void Switching_requests_focus_on_the_target_monitor_and_swap_too()
    {
        var pool = Pool(3);

        Assert.Equal(new FocusRequest(Left), pool.GoTo(Left, 3).Focus);
        Assert.Equal(new FocusRequest(Right), pool.GoTo(Right, 3).Focus); // swap: Right takes desktop 3
        Assert.Null(pool.GoTo(Left, 7).Focus); // not available: nothing to focus
    }

    [Fact]
    public void GoToNext_skips_desktop_shown_on_other_monitor()
    {
        var pool = Pool(4); // Left=1, Right=2

        pool.GoToNext(Left);

        Assert.Equal(3, pool.GetShownNumber(Left));
        Assert.Equal(2, pool.GetShownNumber(Right));
        AssertInvariants(pool);
    }

    [Fact]
    public void GoToNext_at_the_end_reports_edge_and_changes_nothing()
    {
        var pool = Pool(3);
        pool.GoTo(Left, 3);

        var changes = pool.GoToNext(Left);

        Assert.False(changes.HasWindowChanges);
        Assert.Equal(new Indicator(Left, 3, null, IndicatorKind.EdgeReached), Assert.Single(changes.Indicators));
    }

    [Fact]
    public void GoToNext_reports_edge_when_all_following_desktops_are_shown_elsewhere()
    {
        var pool = Pool(2); // Left=1, Right=2

        var changes = pool.GoToNext(Left);

        Assert.Equal(IndicatorKind.EdgeReached, Assert.Single(changes.Indicators).Kind);
        Assert.Equal(1, pool.GetShownNumber(Left));
    }

    [Fact]
    public void GoToPrevious_skips_shown_desktops_and_stops_at_the_start()
    {
        var pool = Pool(4);
        pool.GoTo(Right, 4); // Left=1, Right=4
        pool.GoTo(Left, 3);  // Left=3, Right=4

        pool.GoToPrevious(Left);
        Assert.Equal(2, pool.GetShownNumber(Left));

        pool.GoToPrevious(Left);
        Assert.Equal(1, pool.GetShownNumber(Left));

        var changes = pool.GoToPrevious(Left);
        Assert.Equal(IndicatorKind.EdgeReached, Assert.Single(changes.Indicators).Kind);
        AssertInvariants(pool);
    }

    [Fact]
    public void Switching_on_unknown_monitor_does_nothing()
    {
        var pool = Pool(3);

        Assert.Same(PoolChanges.None, pool.GoTo(Third, 3));
        Assert.Same(PoolChanges.None, pool.GoToNext(Third));
    }
}
