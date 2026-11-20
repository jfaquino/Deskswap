using Deskswap.Core.Input;

namespace Deskswap.Core.Tests;

/// <summary>Design §6.1: default shortcuts and how the hook treats key events.</summary>
public class ShortcutMatcherTests
{
    private readonly ShortcutMatcher _matcher = new(ShortcutMap.Defaults);

    [Fact]
    public void Win_Ctrl_Right_triggers_next_desktop_and_suppresses_start_menu()
    {
        Press(VirtualKeys.LeftWin);
        Press(VirtualKeys.LeftControl);

        var decision = _matcher.OnKey(VirtualKeys.Right, isDown: true);

        Assert.True(decision.Swallow);
        Assert.Equal(new ShortcutAction(ShortcutCommand.NextDesktop), decision.Action);
        Assert.True(decision.SuppressStartMenu);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public void Win_Ctrl_digit_goes_to_that_desktop(int n)
    {
        Press(VirtualKeys.RightWin);
        Press(VirtualKeys.RightControl);

        var decision = _matcher.OnKey(VirtualKeys.Digit(n), isDown: true);

        Assert.Equal(new ShortcutAction(ShortcutCommand.GoToDesktop, n), decision.Action);
    }

    [Fact]
    public void Release_of_a_swallowed_key_is_swallowed_too()
    {
        Press(VirtualKeys.LeftWin);
        Press(VirtualKeys.LeftControl);
        _matcher.OnKey(VirtualKeys.Left, isDown: true);

        var release = _matcher.OnKey(VirtualKeys.Left, isDown: false);

        Assert.True(release.Swallow);
        Assert.Null(release.Action);
    }

    [Fact]
    public void Auto_repeat_is_swallowed_without_repeating_the_action()
    {
        Press(VirtualKeys.LeftWin);
        Press(VirtualKeys.LeftControl);
        _matcher.OnKey(VirtualKeys.Right, isDown: true);

        var repeat = _matcher.OnKey(VirtualKeys.Right, isDown: true);

        Assert.True(repeat.Swallow);
        Assert.Null(repeat.Action);
    }

    [Fact]
    public void Extra_modifier_does_not_match_a_shorter_chord()
    {
        Press(VirtualKeys.LeftWin);
        Press(VirtualKeys.LeftControl);
        Press(VirtualKeys.LeftMenu);

        Assert.Equal(KeyDecision.PassThrough, _matcher.OnKey(VirtualKeys.Right, isDown: true));
    }

    [Fact]
    public void Modifiers_and_unbound_keys_pass_through()
    {
        Assert.Equal(KeyDecision.PassThrough, _matcher.OnKey(VirtualKeys.LeftWin, isDown: true));
        Assert.Equal(KeyDecision.PassThrough, _matcher.OnKey(VirtualKeys.Tab, isDown: true));
        Assert.Equal(KeyDecision.PassThrough, _matcher.OnKey(VirtualKeys.Tab, isDown: false));
        Assert.Equal(KeyDecision.PassThrough, _matcher.OnKey(VirtualKeys.LeftWin, isDown: false));
    }

    [Fact]
    public void Released_modifier_no_longer_counts()
    {
        Press(VirtualKeys.LeftWin);
        Press(VirtualKeys.LeftControl);
        _matcher.OnKey(VirtualKeys.LeftControl, isDown: false);

        Assert.Equal(KeyModifiers.Win, _matcher.HeldModifiers);
        Assert.Equal(KeyDecision.PassThrough, _matcher.OnKey(VirtualKeys.Right, isDown: true));
    }

    [Fact]
    public void Shortcut_without_win_does_not_need_start_menu_suppression()
    {
        var map = new ShortcutMap(new Dictionary<KeyChord, ShortcutAction>
        {
            [new(KeyModifiers.Ctrl | KeyModifiers.Alt, VirtualKeys.Right)] = new(ShortcutCommand.NextDesktop),
        });
        var matcher = new ShortcutMatcher(map);
        matcher.OnKey(VirtualKeys.LeftControl, isDown: true);
        matcher.OnKey(VirtualKeys.LeftMenu, isDown: true);

        var decision = matcher.OnKey(VirtualKeys.Right, isDown: true);

        Assert.NotNull(decision.Action);
        Assert.False(decision.SuppressStartMenu);
    }

    [Fact]
    public void Reset_forgets_held_keys()
    {
        Press(VirtualKeys.LeftWin);
        Press(VirtualKeys.LeftControl);

        _matcher.Reset();

        Assert.Equal(KeyModifiers.None, _matcher.HeldModifiers);
    }

    [Fact]
    public void ShortcutMap_rejects_modifier_as_main_key_and_chords_without_modifiers()
    {
        Assert.Throws<ArgumentException>(() => new ShortcutMap(new Dictionary<KeyChord, ShortcutAction>
        {
            [new(KeyModifiers.Win, VirtualKeys.LeftControl)] = new(ShortcutCommand.NextDesktop),
        }));
        Assert.Throws<ArgumentException>(() => new ShortcutMap(new Dictionary<KeyChord, ShortcutAction>
        {
            [new(KeyModifiers.None, VirtualKeys.Right)] = new(ShortcutCommand.NextDesktop),
        }));
    }

    [Fact]
    public void Defaults_bind_every_m1_command()
    {
        var commands = ShortcutMap.Defaults.Bindings.Values.Select(a => a.Command).ToHashSet();

        Assert.Equal(Enum.GetValues<ShortcutCommand>().ToHashSet(), commands);
        Assert.Equal(9, ShortcutMap.Defaults.Bindings.Values.Count(a => a.Command == ShortcutCommand.GoToDesktop));
    }

    [Fact]
    public void Shortcuts_work_after_pause_even_if_releases_were_missed()
    {
        // Win+Ctrl+Shift+F12 pauses; the hook stops matching before the keys are released.
        Press(VirtualKeys.LeftWin);
        Press(VirtualKeys.LeftControl);
        Press(VirtualKeys.LeftShift);
        Assert.Equal(new ShortcutAction(ShortcutCommand.TogglePause), _matcher.OnKey(VirtualKeys.F12, isDown: true).Action);

        // After resuming, the physical keyboard says only Win+Ctrl are held.
        var decision = _matcher.OnKey(VirtualKeys.Right, isDown: true, physicalModifiers: KeyModifiers.Win | KeyModifiers.Ctrl);

        Assert.Equal(new ShortcutAction(ShortcutCommand.NextDesktop), decision.Action);
    }

    [Fact]
    public void Key_whose_release_was_missed_is_not_swallowed_on_its_next_plain_press()
    {
        Press(VirtualKeys.LeftWin);
        Press(VirtualKeys.LeftControl);
        Press(VirtualKeys.LeftShift);
        _matcher.OnKey(VirtualKeys.F12, isDown: true);

        var plainF12 = _matcher.OnKey(VirtualKeys.F12, isDown: true, physicalModifiers: KeyModifiers.None);

        Assert.Equal(KeyDecision.PassThrough, plainF12);
    }

    [Fact]
    public void Observe_keeps_state_current_while_matching_is_off()
    {
        Press(VirtualKeys.LeftWin);
        Press(VirtualKeys.LeftControl);
        _matcher.OnKey(VirtualKeys.Right, isDown: true);

        _matcher.Observe(VirtualKeys.Right, isDown: false);
        _matcher.Observe(VirtualKeys.LeftControl, isDown: false);
        _matcher.Observe(VirtualKeys.LeftWin, isDown: false);

        Assert.Equal(KeyModifiers.None, _matcher.HeldModifiers);
        Assert.Equal(KeyDecision.PassThrough, _matcher.OnKey(VirtualKeys.Right, isDown: true));
    }

    [Fact]
    public void Paused_map_only_matches_the_pause_toggle()
    {
        var paused = new ShortcutMatcher(ShortcutMap.Defaults.Only(ShortcutCommand.TogglePause));
        paused.OnKey(VirtualKeys.LeftWin, isDown: true);
        paused.OnKey(VirtualKeys.LeftControl, isDown: true);

        Assert.Equal(KeyDecision.PassThrough, paused.OnKey(VirtualKeys.Right, isDown: true));
        paused.OnKey(VirtualKeys.LeftShift, isDown: true);
        Assert.Equal(new ShortcutAction(ShortcutCommand.TogglePause), paused.OnKey(VirtualKeys.F12, isDown: true).Action);
    }

    private void Press(int key) => Assert.Equal(KeyDecision.PassThrough, _matcher.OnKey(key, isDown: true));
}
