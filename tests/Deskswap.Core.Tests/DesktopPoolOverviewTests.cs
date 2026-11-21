using static Deskswap.Core.Tests.PoolTestKit;

namespace Deskswap.Core.Tests;

/// <summary>Overview drag and drop (design §3.4) and reordering (design §3.2).</summary>
public class DesktopPoolOverviewTests
{
    private static DesktopId IdOf(DesktopPool pool, int number) => pool.GetDesktops()[number - 1].Id;

    [Fact]
    public void MoveWindowToDesktop_hidden_target_hides_it_and_the_view_stays()
    {
        var pool = Pool(3);                // Left=1, Right=2
        pool.AddWindow(W(1), Left);

        var changes = pool.MoveWindowToDesktop(W(1), IdOf(pool, 3));

        Assert.Equal(1, pool.GetShownNumber(Left));
        Assert.Equal(3, NumberOf(pool, W(1)));
        Assert.Equal([W(1)], changes.Hide);
        Assert.Empty(changes.Show);
        Assert.Null(changes.Focus);
        AssertInvariants(pool);
    }

    [Fact]
    public void MoveWindowToDesktop_hidden_target_moves_it_to_the_desktops_last_monitor()
    {
        var pool = Pool(3);
        pool.GoTo(Right, 3);               // desktop 3 last shown on Right
        pool.GoTo(Right, 2);
        pool.AddWindow(W(1), Left);

        var changes = pool.MoveWindowToDesktop(W(1), IdOf(pool, 3));

        Assert.Equal(new WindowRelocation(W(1), Left, Right), Assert.Single(changes.Relocations));
    }

    [Fact]
    public void MoveWindowToDesktop_shown_on_other_monitor_moves_it_there_visible()
    {
        var pool = Pool(3);
        pool.AddWindow(W(1), Left);

        var changes = pool.MoveWindowToDesktop(W(1), IdOf(pool, 2));

        Assert.Equal(2, NumberOf(pool, W(1)));
        Assert.Equal(new WindowRelocation(W(1), Left, Right), Assert.Single(changes.Relocations));
        Assert.Equal([W(1)], changes.Show);
        Assert.Empty(changes.Hide);
        AssertInvariants(pool);
    }

    [Fact]
    public void MoveWindowToDesktop_same_desktop_changes_nothing()
    {
        var pool = Pool(3);
        pool.AddWindow(W(1), Left);

        Assert.Same(PoolChanges.None, pool.MoveWindowToDesktop(W(1), IdOf(pool, 1)));
    }

    [Fact]
    public void MoveWindowToDesktop_unpins_a_pinned_window()
    {
        var pool = Pool(3);
        pool.AddWindow(W(1), Left);
        pool.TogglePin(W(1), Left);

        var changes = pool.MoveWindowToDesktop(W(1), IdOf(pool, 3));

        Assert.False(pool.IsPinned(W(1)));
        Assert.Equal(3, NumberOf(pool, W(1)));
        Assert.Equal([W(1)], changes.Hide);
        AssertInvariants(pool);
    }

    [Fact]
    public void MoveWindowToDesktop_unknown_window_or_desktop_changes_nothing()
    {
        var pool = Pool(3);
        pool.AddWindow(W(1), Left);

        Assert.Same(PoolChanges.None, pool.MoveWindowToDesktop(W(2), IdOf(pool, 3)));
        Assert.Same(PoolChanges.None, pool.MoveWindowToDesktop(W(1), DesktopId.New()));
    }

    [Fact]
    public void MoveWindowToNewDesktop_adds_a_desktop_with_the_window()
    {
        var pool = Pool(2);
        pool.AddWindow(W(1), Left);

        var changes = pool.MoveWindowToNewDesktop(W(1), Left);

        Assert.Equal(3, pool.DesktopCount);
        Assert.Equal(3, NumberOf(pool, W(1)));
        Assert.Equal([W(1)], changes.Hide);
        Assert.Equal(1, pool.GetShownNumber(Left));
        AssertInvariants(pool);
    }

    [Fact]
    public void MoveWindowToNewDesktop_when_full_is_not_allowed()
    {
        var pool = Pool(DesktopPool.MaxDesktops);
        pool.AddWindow(W(1), Left);

        var changes = pool.MoveWindowToNewDesktop(W(1), Left);

        Assert.Equal(DesktopPool.MaxDesktops, pool.DesktopCount);
        Assert.Equal(IndicatorKind.NotAllowed, Assert.Single(changes.Indicators).Kind);
        Assert.Equal(1, NumberOf(pool, W(1)));
    }

    [Fact]
    public void MoveDesktop_reorders_and_keeps_windows_and_monitor()
    {
        var pool = Pool(4);                // Left=1, Right=2
        PlaceWindow(pool, W(3), 3, Left);
        var third = IdOf(pool, 3);
        var leftDesktop = pool.GetShownDesktop(Left);

        Assert.True(pool.MoveDesktop(third, 1));

        Assert.Equal(third, IdOf(pool, 1));
        Assert.Equal(1, NumberOf(pool, W(3)));
        Assert.Equal(leftDesktop, pool.GetShownDesktop(Left));
        Assert.Equal(2, pool.GetShownNumber(Left));
        AssertInvariants(pool);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 4)]
    public void MoveDesktop_clamps_the_position(int requested, int expected)
    {
        var pool = Pool(4);
        var second = IdOf(pool, 2);

        pool.MoveDesktop(second, requested);

        Assert.Equal(expected, pool.GetNumber(second));
    }

    [Fact]
    public void MoveDesktop_to_same_place_or_unknown_returns_false()
    {
        var pool = Pool(3);

        Assert.False(pool.MoveDesktop(IdOf(pool, 2), 2));
        Assert.False(pool.MoveDesktop(DesktopId.New(), 1));
    }
}
