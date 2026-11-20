using static Deskswap.Core.Tests.PoolTestKit;

namespace Deskswap.Core.Tests;

/// <summary>Design §3.2: macOS-style add, remove and rename.</summary>
public class DesktopPoolLifecycleTests
{
    [Fact]
    public void AddDesktop_appends_and_switches_target_monitor()
    {
        var pool = Pool(3);
        PlaceWindow(pool, W(1), 1, Left);

        var changes = pool.AddDesktop(Left);

        Assert.Equal(4, pool.DesktopCount);
        Assert.Equal(4, pool.GetShownNumber(Left));
        Assert.Equal([W(1)], changes.Hide);
        Assert.Equal(new Indicator(Left, 4, null, IndicatorKind.Added), Assert.Single(changes.Indicators));
        AssertInvariants(pool);
    }

    [Fact]
    public void AddDesktop_without_target_only_appends()
    {
        var pool = Pool(2);

        var changes = pool.AddDesktop(switchTarget: null);

        Assert.Equal(3, pool.DesktopCount);
        Assert.Equal(1, pool.GetShownNumber(Left));
        Assert.Same(PoolChanges.None, changes);
    }

    [Fact]
    public void AddDesktop_at_maximum_is_not_allowed()
    {
        var pool = Pool(DesktopPool.MaxDesktops);

        var changes = pool.AddDesktop(Left);

        Assert.Equal(DesktopPool.MaxDesktops, pool.DesktopCount);
        var indicator = Assert.Single(changes.Indicators);
        Assert.Equal(IndicatorKind.NotAllowed, indicator.Kind);
        Assert.Equal(NotAllowedReason.MaximumDesktopsReached, indicator.Reason);
    }

    [Fact]
    public void Empty_desktops_are_kept()
    {
        var pool = Pool(4);
        pool.GoTo(Left, 3);
        pool.GoTo(Left, 1);

        Assert.Equal(4, pool.DesktopCount);
    }

    [Fact]
    public void RemoveDesktop_hidden_moves_its_windows_to_target_desktop_and_shows_them()
    {
        var pool = Pool(3);
        PlaceWindow(pool, W(3), 3, Right); // desktop 3 last shown on Right

        var changes = pool.RemoveDesktop(3, Left);

        Assert.Equal(2, pool.DesktopCount);
        Assert.Equal(1, NumberOf(pool, W(3)));
        Assert.Equal([new WindowRelocation(W(3), Right, Left)], changes.Relocations);
        Assert.Equal([W(3)], changes.Show);
        Assert.Equal(new Indicator(Left, 1, null, IndicatorKind.Removed), Assert.Single(changes.Indicators));
        AssertInvariants(pool);
    }

    [Fact]
    public void RemoveDesktop_shown_switches_to_previous_free_desktop_and_merges_windows()
    {
        var pool = Pool(4);              // Left=1, Right=2
        PlaceWindow(pool, W(1), 1, Left);
        pool.GoTo(Left, 3);              // Left=3, desktop 1 free
        pool.AddWindow(W(3), Left);
        pool.NoteActiveWindow(W(3));

        var changes = pool.RemoveDesktop(3, Left);

        Assert.Equal(3, pool.DesktopCount);
        Assert.Equal(1, pool.GetShownNumber(Left));
        Assert.Equal(1, NumberOf(pool, W(3)));
        Assert.Equal([W(1)], changes.Show);
        Assert.Empty(changes.Hide); // W(3) stays visible
        Assert.Equal(new FocusRequest(Left, W(3)), changes.Focus);
        Assert.Equal(new Indicator(Left, 1, null, IndicatorKind.Removed), Assert.Single(changes.Indicators));
        AssertInvariants(pool);
    }

    [Fact]
    public void RemoveDesktop_shown_uses_next_free_desktop_when_none_before_it()
    {
        var pool = Pool(3); // Left=1, Right=2, 3 free

        pool.RemoveDesktop(1, Left);

        // Old desktop 3 is now desktop 2 after renumbering.
        Assert.Equal(2, pool.DesktopCount);
        Assert.Equal(1, pool.GetShownNumber(Right));
        Assert.Equal(2, pool.GetShownNumber(Left));
        AssertInvariants(pool);
    }

    [Fact]
    public void RemoveDesktop_renumbers_following_desktops_but_keeps_ids()
    {
        var pool = Pool(4);
        var fourth = pool.GetDesktops()[3].Id;

        pool.RemoveDesktop(3, Left);

        Assert.Equal(3, pool.GetNumber(fourth));
    }

    [Fact]
    public void RemoveDesktop_below_monitor_count_is_not_allowed()
    {
        var pool = Pool(2);

        var changes = pool.RemoveDesktop(2, Left);

        Assert.Equal(2, pool.DesktopCount);
        var indicator = Assert.Single(changes.Indicators);
        Assert.Equal(IndicatorKind.NotAllowed, indicator.Kind);
        Assert.Equal(NotAllowedReason.MinimumDesktopsReached, indicator.Reason);
    }

    [Fact]
    public void RemoveDesktop_missing_number_reports_not_available()
    {
        var pool = Pool(3);

        var changes = pool.RemoveDesktop(7, Left);

        Assert.Equal(IndicatorKind.NotAvailable, Assert.Single(changes.Indicators).Kind);
        Assert.Equal(3, pool.DesktopCount);
    }

    [Fact]
    public void Rename_sets_trims_and_clears_names_shown_in_indicators()
    {
        var pool = Pool(3);
        var third = pool.GetDesktops()[2].Id;

        Assert.True(pool.Rename(third, "  Work "));
        Assert.Equal(new Indicator(Left, 3, "Work", IndicatorKind.Switched), Assert.Single(pool.GoTo(Left, 3).Indicators));

        pool.Rename(third, "   ");
        Assert.Null(pool.GetDesktops()[2].Name);
        Assert.False(pool.Rename(DesktopId.New(), "x"));
    }
}
