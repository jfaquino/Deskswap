using static Deskswap.Core.Tests.PoolTestKit;

namespace Deskswap.Core.Tests;

/// <summary>Design §3.4 (moving windows) and §3.6 (pinning).</summary>
public class DesktopPoolMoveAndPinTests
{
    [Fact]
    public void MoveWindowToNext_moves_it_and_the_monitor_follows()
    {
        var pool = Pool(4);                // Left=1, Right=2
        pool.AddWindow(W(1), Left);
        pool.AddWindow(W(9), Left);

        var changes = pool.MoveWindowToNext(W(1));

        Assert.Equal(3, pool.GetShownNumber(Left)); // skipped 2 (shown on Right)
        Assert.Equal(3, NumberOf(pool, W(1)));
        Assert.Equal([W(9)], changes.Hide);
        Assert.Equal(W(1), changes.Show[^1]);          // shown last: stays on top
        Assert.DoesNotContain(changes.Relocations, r => r.Window == W(1));
        AssertInvariants(pool);
    }

    [Fact]
    public void MoveWindowToPrevious_at_the_start_reports_edge()
    {
        var pool = Pool(3);
        pool.AddWindow(W(1), Left);

        var changes = pool.MoveWindowToPrevious(W(1));

        Assert.Equal(1, NumberOf(pool, W(1)));
        Assert.Equal(IndicatorKind.EdgeReached, Assert.Single(changes.Indicators).Kind);
    }

    [Fact]
    public void MoveWindowTo_desktop_on_other_monitor_moves_the_window_there()
    {
        var pool = Pool(3);
        pool.AddWindow(W(1), Left);

        var changes = pool.MoveWindowTo(W(1), 2); // desktop 2 is on Right

        Assert.Equal(2, NumberOf(pool, W(1)));
        Assert.Equal(1, pool.GetShownNumber(Left));
        Assert.Equal([new WindowRelocation(W(1), Left, Right)], changes.Relocations);
        Assert.Equal(new FocusRequest(Right), changes.Focus);
        Assert.Equal(new Indicator(Right, 2, null, IndicatorKind.WindowMoved), Assert.Single(changes.Indicators));
        AssertInvariants(pool);
    }

    [Fact]
    public void MoveWindowTo_hidden_desktop_follows_the_window()
    {
        var pool = Pool(3);
        pool.AddWindow(W(1), Left);

        pool.MoveWindowTo(W(1), 3);

        Assert.Equal(3, pool.GetShownNumber(Left));
        Assert.Equal(3, NumberOf(pool, W(1)));
    }

    [Theory]
    [InlineData(1, IndicatorKind.Unchanged)]
    [InlineData(9, IndicatorKind.NotAvailable)]
    public void MoveWindowTo_same_or_missing_desktop_changes_nothing(int number, IndicatorKind expected)
    {
        var pool = Pool(3);
        pool.AddWindow(W(1), Left);

        var changes = pool.MoveWindowTo(W(1), number);

        Assert.False(changes.HasWindowChanges);
        Assert.Equal(expected, Assert.Single(changes.Indicators).Kind);
    }

    [Fact]
    public void Moving_an_unmanaged_window_does_nothing()
    {
        var pool = Pool(3);

        Assert.Same(PoolChanges.None, pool.MoveWindowToNext(W(7)));
        Assert.Same(PoolChanges.None, pool.MoveWindowTo(W(7), 3));
    }

    [Fact]
    public void Pinned_window_stays_visible_on_every_desktop_of_its_monitor()
    {
        var pool = Pool(3);
        pool.AddWindow(W(1), Left);

        var pin = pool.TogglePin(W(1), Left);
        var switching = pool.GoTo(Left, 3);

        Assert.Equal(IndicatorKind.Pinned, Assert.Single(pin.Indicators).Kind);
        Assert.True(pool.IsPinned(W(1)));
        Assert.DoesNotContain(W(1), switching.Hide);
        Assert.False(pool.TryGetDesktopOf(W(1), out _));
        AssertInvariants(pool);
    }

    [Fact]
    public void Unpinning_assigns_the_window_to_the_desktop_shown_on_its_monitor()
    {
        var pool = Pool(3);
        pool.AddWindow(W(1), Left);
        pool.TogglePin(W(1), Left);
        pool.GoTo(Left, 3);

        var unpin = pool.TogglePin(W(1), Left);

        Assert.False(pool.IsPinned(W(1)));
        Assert.Equal(3, NumberOf(pool, W(1)));
        Assert.Equal(IndicatorKind.Unpinned, Assert.Single(unpin.Indicators).Kind);
    }

    [Fact]
    public void Reconcile_leaves_pinned_windows_alone_and_forgets_closed_ones()
    {
        var pool = Pool(3);
        pool.AddWindow(W(1), Left);
        pool.TogglePin(W(1), Left);

        pool.Reconcile([new ObservedWindow(W(1), Right, IsVisible: true)]);
        Assert.True(pool.IsPinned(W(1)));
        Assert.False(pool.TryGetDesktopOf(W(1), out _));

        pool.Reconcile([]);
        Assert.False(pool.IsPinned(W(1)));
    }

    [Fact]
    public void Pinned_windows_survive_a_snapshot_round_trip()
    {
        var pool = Pool(3);
        pool.AddWindow(W(1), Left);
        pool.TogglePin(W(1), Left);

        var restored = DesktopPool.Restore(pool.ToSnapshot(), [Left, Right]);

        Assert.True(restored.IsPinned(W(1)));
        AssertInvariants(restored);
    }

    [Fact]
    public void Older_snapshot_without_pinned_windows_restores()
    {
        var pool = Pool(3);
        var old = pool.ToSnapshot() with { Pinned = null };

        var restored = DesktopPool.Restore(old, [Left, Right]);

        Assert.Empty(restored.PinnedWindows);
    }
}
