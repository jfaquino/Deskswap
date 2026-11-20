using static Deskswap.Core.Tests.PoolTestKit;

namespace Deskswap.Core.Tests;

/// <summary>Design §5.3: restoring the pool after Deskswap restarts.</summary>
public class DesktopPoolSnapshotTests
{
    [Fact]
    public void Snapshot_round_trip_keeps_order_names_monitors_and_windows()
    {
        var pool = Pool(4);
        pool.Rename(pool.GetDesktops()[2].Id, "Work");
        PlaceWindow(pool, W(3), 3, Left);
        pool.AddWindow(W(1), Left);
        pool.GoTo(Left, 3);
        pool.NoteActiveWindow(W(3));

        var restored = DesktopPool.Restore(pool.ToSnapshot(), [Left, Right]);

        Assert.Equal(
            pool.GetDesktops().Select(d => (d.Id, d.Number, d.Name, d.ShownOn)),
            restored.GetDesktops().Select(d => (d.Id, d.Number, d.Name, d.ShownOn)));
        Assert.Equal(3, NumberOf(restored, W(3)));
        Assert.Equal(1, NumberOf(restored, W(1)));
        Assert.Equal(new FocusRequest(Left, W(3)), RoundTripFocus(restored));
        AssertInvariants(restored);
    }

    [Fact]
    public void Restore_with_empty_snapshot_creates_a_fresh_pool()
    {
        var restored = DesktopPool.Restore(new PoolSnapshot([], [], []), [Left, Right]);

        Assert.Equal(2, restored.DesktopCount);
        AssertInvariants(restored);
    }

    [Fact]
    public void Restore_gives_a_new_monitor_the_lowest_free_desktop()
    {
        var pool = Pool(3);

        var restored = DesktopPool.Restore(pool.ToSnapshot(), [Left, Third]);

        Assert.Equal(1, restored.GetShownNumber(Left));
        Assert.Equal(2, restored.GetShownNumber(Third));
        AssertInvariants(restored);
    }

    [Fact]
    public void Restore_adds_a_desktop_when_more_monitors_than_desktops()
    {
        var pool = DesktopPool.Create([Left]);

        var restored = DesktopPool.Restore(pool.ToSnapshot(), [Left, Right]);

        Assert.Equal(2, restored.DesktopCount);
        AssertInvariants(restored);
    }

    [Fact]
    public void Restore_ignores_two_monitors_mapped_to_the_same_desktop()
    {
        var pool = Pool(3);
        var snapshot = pool.ToSnapshot();
        var first = snapshot.Desktops[0].Id;
        var corrupt = snapshot with { Monitors = [new MonitorAssignment(Left, first), new MonitorAssignment(Right, first)] };

        var restored = DesktopPool.Restore(corrupt, [Left, Right]);

        Assert.Equal(1, restored.GetShownNumber(Left));
        Assert.Equal(2, restored.GetShownNumber(Right));
        AssertInvariants(restored);
    }

    private static FocusRequest? RoundTripFocus(DesktopPool pool)
    {
        pool.GoTo(Left, 4);
        return pool.GoTo(Left, 3).Focus;
    }
}
