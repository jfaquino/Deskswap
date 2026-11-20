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

    private void Press(int key) => Assert.Equal(KeyDecision.PassThrough, _matcher.OnKey(key, isDown: true));
}
