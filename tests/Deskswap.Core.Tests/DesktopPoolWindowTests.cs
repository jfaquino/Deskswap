using static Deskswap.Core.Tests.PoolTestKit;

namespace Deskswap.Core.Tests;

/// <summary>Design §3.4 and §5.1: window tracking and reconciliation.</summary>
public class DesktopPoolWindowTests
{
    [Fact]
    public void AddWindow_assigns_to_desktop_shown_on_its_monitor()
    {
        var pool = Pool(3);

        Assert.True(pool.AddWindow(W(1), Right));

        Assert.Equal(2, NumberOf(pool, W(1)));
        Assert.True(pool.ShouldBeVisible(W(1)));
        Assert.False(pool.AddWindow(W(1), Left));   // already managed
        Assert.False(pool.AddWindow(W(2), Third));  // unknown monitor
    }

    [Fact]
    public void Windows_with_the_same_handle_but_another_process_are_different()
    {
        var pool = Pool(2);
        var original = new WindowId(100, ProcessId: 1, ProcessStartTicks: 1);
        var reused = new WindowId(100, ProcessId: 2, ProcessStartTicks: 5);

        pool.AddWindow(original, Left);

        Assert.True(pool.AddWindow(reused, Right));
        Assert.Equal(1, NumberOf(pool, original));
        Assert.Equal(2, NumberOf(pool, reused));
    }

    [Fact]
    public void RemoveWindow_forgets_it()
    {
        var pool = Pool(3);
        pool.AddWindow(W(1), Left);

        Assert.True(pool.RemoveWindow(W(1)));

        Assert.False(pool.TryGetDesktopOf(W(1), out _));
        Assert.False(pool.RemoveWindow(W(1)));
        Assert.Empty(pool.GoTo(Left, 3).Show);
    }

    [Fact]
    public void MoveWindowToMonitor_reassigns_to_desktop_shown_there()
    {
        var pool = Pool(3);
        pool.AddWindow(W(1), Left);

        Assert.True(pool.MoveWindowToMonitor(W(1), Right));

        Assert.Equal(2, NumberOf(pool, W(1)));
        Assert.False(pool.MoveWindowToMonitor(W(1), Right));
    }

    [Fact]
    public void Reconcile_adopts_new_visible_windows_and_forgets_vanished_ones()
    {
        var pool = Pool(3);
        pool.AddWindow(W(1), Left);

        var changes = pool.Reconcile([new ObservedWindow(W(2), Right, IsVisible: true)]);

        Assert.False(changes.HasWindowChanges);
        Assert.False(pool.TryGetDesktopOf(W(1), out _));
        Assert.Equal(2, NumberOf(pool, W(2)));
    }

    [Fact]
    public void Reconcile_ignores_unknown_hidden_windows()
    {
        var pool = Pool(2);

        pool.Reconcile([new ObservedWindow(W(9), Left, IsVisible: false)]);

        Assert.False(pool.TryGetDesktopOf(W(9), out _));
    }

    [Fact]
    public void Reconcile_hides_visible_window_whose_desktop_is_hidden()
    {
        var pool = Pool(3);
        PlaceWindow(pool, W(3), 3, Left);

        var changes = pool.Reconcile([new ObservedWindow(W(3), Left, IsVisible: true)]);

        Assert.Equal([W(3)], changes.Hide);
        Assert.Equal(3, NumberOf(pool, W(3)));
    }

    [Fact]
    public void Reconcile_shows_hidden_window_whose_desktop_is_shown_and_brings_it_to_that_monitor()
    {
        var pool = Pool(3);
        pool.AddWindow(W(1), Left);

        var changes = pool.Reconcile([new ObservedWindow(W(1), Right, IsVisible: false)]);

        Assert.Equal([W(1)], changes.Show);
        Assert.Equal([new WindowRelocation(W(1), Right, Left)], changes.Relocations);
    }

    [Fact]
    public void Reconcile_follows_a_visible_window_the_user_moved_to_another_monitor()
    {
        var pool = Pool(3);
        pool.AddWindow(W(1), Left);

        var changes = pool.Reconcile([new ObservedWindow(W(1), Right, IsVisible: true)]);

        Assert.False(changes.HasWindowChanges);
        Assert.Equal(2, NumberOf(pool, W(1)));
        AssertInvariants(pool);
    }
}
