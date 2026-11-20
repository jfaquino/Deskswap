using static Deskswap.Core.Tests.PoolTestKit;

namespace Deskswap.Core.Tests;

/// <summary>Design §4: monitors connected, disconnected and reconnected.</summary>
public class DesktopPoolMonitorTests
{
    [Fact]
    public void Disconnecting_hides_the_desktop_and_keeps_it_in_the_pool()
    {
        var pool = Pool(3);
        pool.AddWindow(W(2), Right);

        var changes = pool.SetMonitors([Left]);

        Assert.Equal([W(2)], changes.Hide);
        Assert.Equal(3, pool.DesktopCount);
        Assert.Equal(2, NumberOf(pool, W(2)));
        Assert.Null(pool.GetMonitorShowing(pool.GetDesktops()[1].Id));
        AssertInvariants(pool);
    }

    [Fact]
    public void Windows_moved_by_Windows_after_a_disconnect_stay_on_their_desktop_and_are_hidden()
    {
        var pool = Pool(3);
        pool.AddWindow(W(1), Left);
        pool.AddWindow(W(2), Right);
        pool.SetMonitors([Left]);

        // Windows moved W(2) onto Left and it is still visible (the hide is in flight or was undone by the move).
        var changes = pool.Reconcile(
            [new ObservedWindow(W(1), Left, IsVisible: true), new ObservedWindow(W(2), Left, IsVisible: true)],
            ReconcileMode.Enforce);

        Assert.Equal([W(2)], changes.Hide);
        Assert.Equal(2, NumberOf(pool, W(2)));
        Assert.Equal(1, pool.GetShownNumber(Left));
        AssertInvariants(pool);
    }

    [Fact]
    public void Reconnecting_gives_the_monitor_its_desktop_back_from_wherever_windows_are()
    {
        var pool = Pool(3);
        pool.AddWindow(W(2), Right);
        pool.SetMonitors([Left]);

        var changes = pool.SetMonitors([Left, Right]);

        Assert.Equal(2, pool.GetShownNumber(Right));
        Assert.Equal([W(2)], changes.Show);
        Assert.Equal([new WindowRelocation(W(2), null, Right)], changes.Relocations);
        AssertInvariants(pool);
    }

    [Fact]
    public void Hold_mode_moves_windows_back_to_their_desktop_instead_of_reassigning_them()
    {
        var pool = Pool(3);
        pool.AddWindow(W(2), Right);
        pool.SetMonitors([Left]);
        pool.SetMonitors([Left, Right]);   // desktop 2 is back on Right

        // Windows (not the user) left W(2) visible on Left.
        var changes = pool.Reconcile([new ObservedWindow(W(2), Left, IsVisible: true)], ReconcileMode.Hold);

        Assert.Equal(2, NumberOf(pool, W(2)));
        Assert.Equal([new WindowRelocation(W(2), Left, Right)], changes.Relocations);
        AssertInvariants(pool);
    }

    [Fact]
    public void Outside_hold_mode_a_window_on_another_monitor_joins_the_desktop_there()
    {
        var pool = Pool(3);
        pool.AddWindow(W(2), Right);

        var changes = pool.Reconcile([new ObservedWindow(W(2), Left, IsVisible: true)], ReconcileMode.Enforce);

        Assert.Equal(1, NumberOf(pool, W(2)));
        Assert.Empty(changes.Relocations);
    }

    [Fact]
    public void Hold_mode_also_hides_visible_windows_of_hidden_desktops()
    {
        var pool = Pool(3);
        PlaceWindow(pool, W(3), 3, Left);

        var changes = pool.Reconcile([new ObservedWindow(W(3), Left, IsVisible: true)], ReconcileMode.Hold);

        Assert.Equal([W(3)], changes.Hide);
        Assert.Equal(1, pool.GetShownNumber(Left));
    }

    [Fact]
    public void Reconnecting_after_the_desktop_was_taken_uses_the_lowest_free_desktop()
    {
        var pool = Pool(3);
        pool.SetMonitors([Left]);
        pool.GoTo(Left, 2);                 // Left took Right's old desktop

        pool.SetMonitors([Left, Right]);

        Assert.Equal(1, pool.GetShownNumber(Right));
        AssertInvariants(pool);
    }

    [Fact]
    public void A_new_monitor_with_no_free_desktop_gets_a_new_one()
    {
        var pool = Pool(2);

        pool.SetMonitors([Left, Right, Third]);

        Assert.Equal(3, pool.DesktopCount);
        Assert.Equal(3, pool.GetShownNumber(Third));
        AssertInvariants(pool);
    }

    [Fact]
    public void Remembered_monitors_survive_a_snapshot_round_trip()
    {
        var pool = Pool(3);
        pool.SetMonitors([Left]);

        var restored = DesktopPool.Restore(pool.ToSnapshot(), [Left]);
        restored.SetMonitors([Left, Right]);

        Assert.Equal(2, restored.GetShownNumber(Right));
    }

    [Fact]
    public void Removing_a_remembered_desktop_forgets_the_mapping()
    {
        var pool = Pool(3);
        pool.SetMonitors([Left]);

        pool.RemoveDesktop(2, Left);
        pool.SetMonitors([Left, Right]);

        AssertInvariants(pool);
        Assert.Equal(2, pool.DesktopCount);
    }
}
