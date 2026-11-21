using static Deskswap.Core.Tests.PoolTestKit;

namespace Deskswap.Core.Tests;

/// <summary>Design §5.4: a window that shows itself while its desktop is hidden brings that desktop forward.</summary>
public class DesktopPoolActivationTests
{
    [Fact]
    public void Visible_window_of_hidden_desktop_switches_its_monitor_to_that_desktop()
    {
        var pool = Pool(3);
        PlaceWindow(pool, W(3), 3, Left);
        PlaceWindow(pool, W(4), 3, Left);
        pool.AddWindow(W(1), Left);

        var changes = pool.Reconcile(
            [new ObservedWindow(W(3), Left, IsVisible: true), new ObservedWindow(W(4), Left, IsVisible: false), new ObservedWindow(W(1), Left, IsVisible: true)],
            ReconcileMode.FollowActivation);

        Assert.Equal(3, pool.GetShownNumber(Left));
        Assert.Equal([W(1)], changes.Hide);
        Assert.Contains(W(4), changes.Show);
        Assert.Equal(new FocusRequest(Left), changes.Focus);
        Assert.Equal(new Indicator(Left, 3, null, IndicatorKind.Switched), Assert.Single(changes.Indicators));
        AssertInvariants(pool);
    }

    [Fact]
    public void Activation_moves_the_rest_of_the_desktop_from_wherever_it_actually_is()
    {
        var pool = Pool(3);
        PlaceWindow(pool, W(3), 3, Right); // desktop 3 last shown on Right
        PlaceWindow(pool, W(4), 3, Right);

        // The app showed W(3) on Left.
        var changes = pool.Reconcile(
            [new ObservedWindow(W(3), Left, IsVisible: true), new ObservedWindow(W(4), Right, IsVisible: false)],
            ReconcileMode.FollowActivation);

        Assert.Equal(3, pool.GetShownNumber(Left));
        Assert.All(changes.Relocations, r => Assert.Null(r.From));
        Assert.Contains(changes.Relocations, r => r.Window == W(4) && r.To == Left);
    }

    [Fact]
    public void Activation_reports_the_window_that_caused_it()
    {
        var pool = Pool(3);
        PlaceWindow(pool, W(3), 3, Left);

        var changes = pool.Reconcile([new ObservedWindow(W(3), Left, IsVisible: true)], ReconcileMode.FollowActivation);

        Assert.Equal(W(3), changes.ActivatedBy);
    }

    [Fact]
    public void Windows_that_may_not_activate_are_hidden_instead()
    {
        var pool = Pool(3);
        PlaceWindow(pool, W(3), 3, Left);

        var changes = pool.Reconcile(
            [new ObservedWindow(W(3), Left, IsVisible: true)],
            ReconcileMode.FollowActivation,
            mayNotActivate: new HashSet<WindowId> { W(3) });

        Assert.Equal(1, pool.GetShownNumber(Left));
        Assert.Equal([W(3)], changes.Hide);
        Assert.Null(changes.ActivatedBy);
    }

    [Fact]
    public void Enforce_mode_hides_instead_of_switching()
    {
        var pool = Pool(3);
        PlaceWindow(pool, W(3), 3, Left);

        var changes = pool.Reconcile([new ObservedWindow(W(3), Left, IsVisible: true)], ReconcileMode.Enforce);

        Assert.Equal(1, pool.GetShownNumber(Left));
        Assert.Equal([W(3)], changes.Hide);
    }

    [Fact]
    public void When_two_hidden_desktops_ask_at_once_the_foreground_window_wins()
    {
        var pool = Pool(4);
        PlaceWindow(pool, W(3), 3, Left);
        PlaceWindow(pool, W(4), 4, Left);

        var changes = pool.Reconcile(
            [new ObservedWindow(W(3), Left, IsVisible: true), new ObservedWindow(W(4), Left, IsVisible: true)],
            ReconcileMode.FollowActivation,
            foreground: W(4));

        Assert.Equal(4, pool.GetShownNumber(Left));
        Assert.Contains(W(3), changes.Hide);
        Assert.DoesNotContain(W(4), changes.Hide);
        AssertInvariants(pool);
    }
}
