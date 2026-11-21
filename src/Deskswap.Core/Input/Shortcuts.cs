using System.Collections.ObjectModel;

namespace Deskswap.Core.Input;

/// <summary>Modifier keys of a shortcut. Left and right variants are treated the same.</summary>
[Flags]
public enum KeyModifiers
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>The Windows key.</summary>
    Win = 1,

    /// <summary>Ctrl.</summary>
    Ctrl = 2,

    /// <summary>Shift.</summary>
    Shift = 4,

    /// <summary>Alt.</summary>
    Alt = 8,
}

/// <summary>Windows virtual-key codes used by Deskswap. Kept here so Core stays free of Win32 references.</summary>
public static class VirtualKeys
{
    /// <summary>Tab.</summary>
    public const int Tab = 0x09;

    /// <summary>Esc.</summary>
    public const int Escape = 0x1B;

    /// <summary>Shift (either side).</summary>
    public const int Shift = 0x10;

    /// <summary>Ctrl (either side).</summary>
    public const int Control = 0x11;

    /// <summary>Alt (either side).</summary>
    public const int Menu = 0x12;

    /// <summary>Left arrow.</summary>
    public const int Left = 0x25;

    /// <summary>Up arrow.</summary>
    public const int Up = 0x26;

    /// <summary>Right arrow.</summary>
    public const int Right = 0x27;

    /// <summary>Down arrow.</summary>
    public const int Down = 0x28;

    /// <summary>The digit 0; digits 1–9 follow consecutively.</summary>
    public const int D0 = 0x30;

    /// <summary>The letter D.</summary>
    public const int D = 0x44;

    /// <summary>The letter P.</summary>
    public const int P = 0x50;

    /// <summary>Left Windows key.</summary>
    public const int LeftWin = 0x5B;

    /// <summary>Right Windows key.</summary>
    public const int RightWin = 0x5C;

    /// <summary>F4.</summary>
    public const int F4 = 0x73;

    /// <summary>F12.</summary>
    public const int F12 = 0x7B;

    /// <summary>Left Shift.</summary>
    public const int LeftShift = 0xA0;

    /// <summary>Right Shift.</summary>
    public const int RightShift = 0xA1;

    /// <summary>Left Ctrl.</summary>
    public const int LeftControl = 0xA2;

    /// <summary>Right Ctrl.</summary>
    public const int RightControl = 0xA3;

    /// <summary>Left Alt.</summary>
    public const int LeftMenu = 0xA4;

    /// <summary>Right Alt.</summary>
    public const int RightMenu = 0xA5;

    /// <summary>An unassigned key code, injected to stop the Start menu opening after a swallowed Win shortcut.</summary>
    public const int Unassigned = 0xE8;

    /// <summary>Gets the virtual-key code of a digit key.</summary>
    /// <param name="digit">A digit from 0 to 9.</param>
    /// <returns>The key code.</returns>
    public static int Digit(int digit) =>
        digit is >= 0 and <= 9 ? D0 + digit : throw new ArgumentOutOfRangeException(nameof(digit));

    /// <summary>Gets the modifier a key code represents, if any.</summary>
    /// <param name="virtualKey">A virtual-key code.</param>
    /// <returns>The modifier, or <see cref="KeyModifiers.None"/> for other keys.</returns>
    public static KeyModifiers ModifierOf(int virtualKey) => virtualKey switch
    {
        LeftWin or RightWin => KeyModifiers.Win,
        Control or LeftControl or RightControl => KeyModifiers.Ctrl,
        Shift or LeftShift or RightShift => KeyModifiers.Shift,
        Menu or LeftMenu or RightMenu => KeyModifiers.Alt,
        _ => KeyModifiers.None,
    };
}

/// <summary>A key combination: modifiers plus one non-modifier key.</summary>
/// <param name="Modifiers">The modifiers that must be held.</param>
/// <param name="VirtualKey">The virtual-key code of the main key.</param>
public readonly record struct KeyChord(KeyModifiers Modifiers, int VirtualKey);

/// <summary>Commands a shortcut can trigger.</summary>
public enum ShortcutCommand
{
    /// <summary>Show the previous free desktop on the target monitor.</summary>
    PreviousDesktop,

    /// <summary>Show the next free desktop on the target monitor.</summary>
    NextDesktop,

    /// <summary>Go to desktop N (the argument); swaps if another monitor shows it.</summary>
    GoToDesktop,

    /// <summary>Add a desktop and switch the target monitor to it.</summary>
    AddDesktop,

    /// <summary>Remove the desktop shown on the target monitor.</summary>
    RemoveDesktop,

    /// <summary>Show every hidden window and pause Deskswap; when paused, resume.</summary>
    TogglePause,

    /// <summary>Move the focused window to the previous free desktop; its monitor follows.</summary>
    MoveWindowToPreviousDesktop,

    /// <summary>Move the focused window to the next free desktop; its monitor follows.</summary>
    MoveWindowToNextDesktop,

    /// <summary>Move the focused window to desktop N (the argument).</summary>
    MoveWindowToDesktop,

    /// <summary>Pin the focused window to every desktop of its monitor, or unpin it.</summary>
    TogglePinWindow,

    /// <summary>Open the overview on every monitor, or close it (overview-ux.md §2).</summary>
    ToggleOverview,

    /// <summary>Show every window of the focused app on every desktop, or close that view (overview-ux.md §4).</summary>
    ToggleAppWindows,

    /// <summary>
    /// Esc while the overview is open: cancel a rename or a drag, else close it. Bound only while the overview is
    /// open, so Esc works even if Windows didn't give the overview the keyboard.
    /// </summary>
    CloseOverview,
}

/// <summary>A command with its argument (the desktop number for <see cref="ShortcutCommand.GoToDesktop"/>).</summary>
/// <param name="Command">The command.</param>
/// <param name="Argument">The argument, or 0 when the command takes none.</param>
public readonly record struct ShortcutAction(ShortcutCommand Command, int Argument = 0);

/// <summary>Maps key chords to actions.</summary>
public sealed class ShortcutMap
{
    private readonly ReadOnlyDictionary<KeyChord, ShortcutAction> _bindings;

    /// <summary>Initializes a new instance of the <see cref="ShortcutMap"/> class.</summary>
    /// <param name="bindings">The bindings; each chord must have a main key that is not a modifier.</param>
    /// <exception cref="ArgumentException">A chord uses a modifier as its main key or has no modifiers.</exception>
    public ShortcutMap(IReadOnlyDictionary<KeyChord, ShortcutAction> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        foreach (var chord in bindings.Keys)
        {
            bool bareEscape = chord.VirtualKey == VirtualKeys.Escape && chord.Modifiers == KeyModifiers.None;
            if (VirtualKeys.ModifierOf(chord.VirtualKey) != KeyModifiers.None || (chord.Modifiers == KeyModifiers.None && !bareEscape))
            {
                throw new ArgumentException($"Invalid shortcut {chord}.", nameof(bindings));
            }
        }

        _bindings = new ReadOnlyDictionary<KeyChord, ShortcutAction>(new Dictionary<KeyChord, ShortcutAction>(bindings));
    }

    /// <summary>Gets the default bindings (design §6.1).</summary>
    public static ShortcutMap Defaults { get; } = CreateDefaults();

    /// <summary>Gets all bindings.</summary>
    public IReadOnlyDictionary<KeyChord, ShortcutAction> Bindings => _bindings;

    /// <summary>
    /// Gets these bindings plus the keys the overview needs even without keyboard focus: Esc alone closes it
    /// (overview-ux.md §2). Esc is never bound otherwise.
    /// </summary>
    /// <returns>The extended map.</returns>
    public ShortcutMap WhileOverviewOpen() =>
        new(new Dictionary<KeyChord, ShortcutAction>(_bindings)
        {
            [new KeyChord(KeyModifiers.None, VirtualKeys.Escape)] = new(ShortcutCommand.CloseOverview),
        });

    /// <summary>Gets a map with only the bindings of one command (e.g. the pause toggle while paused).</summary>
    /// <param name="command">The command to keep.</param>
    /// <returns>The reduced map.</returns>
    public ShortcutMap Only(ShortcutCommand command) =>
        new(_bindings.Where(pair => pair.Value.Command == command).ToDictionary(pair => pair.Key, pair => pair.Value));

    /// <summary>Looks up the action bound to a chord.</summary>
    /// <param name="chord">The chord.</param>
    /// <param name="action">The bound action, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if the chord is bound.</returns>
    public bool TryGetAction(KeyChord chord, out ShortcutAction action) => _bindings.TryGetValue(chord, out action);

    private static ShortcutMap CreateDefaults()
    {
        const KeyModifiers winCtrl = KeyModifiers.Win | KeyModifiers.Ctrl;
        var bindings = new Dictionary<KeyChord, ShortcutAction>
        {
            [new(winCtrl, VirtualKeys.Left)] = new(ShortcutCommand.PreviousDesktop),
            [new(winCtrl, VirtualKeys.Right)] = new(ShortcutCommand.NextDesktop),
            [new(winCtrl, VirtualKeys.D)] = new(ShortcutCommand.AddDesktop),
            [new(winCtrl, VirtualKeys.F4)] = new(ShortcutCommand.RemoveDesktop),
            [new(winCtrl | KeyModifiers.Shift, VirtualKeys.F12)] = new(ShortcutCommand.TogglePause),
            [new(winCtrl | KeyModifiers.Shift, VirtualKeys.Left)] = new(ShortcutCommand.MoveWindowToPreviousDesktop),
            [new(winCtrl | KeyModifiers.Shift, VirtualKeys.Right)] = new(ShortcutCommand.MoveWindowToNextDesktop),
            [new(winCtrl | KeyModifiers.Shift, VirtualKeys.P)] = new(ShortcutCommand.TogglePinWindow),
            [new(KeyModifiers.Win, VirtualKeys.Tab)] = new(ShortcutCommand.ToggleOverview),
            [new(winCtrl, VirtualKeys.Up)] = new(ShortcutCommand.ToggleOverview),
            [new(winCtrl, VirtualKeys.Down)] = new(ShortcutCommand.ToggleAppWindows),
        };

        for (int n = 1; n <= 9; n++)
        {
            bindings[new(winCtrl, VirtualKeys.Digit(n))] = new(ShortcutCommand.GoToDesktop, n);
            bindings[new(winCtrl | KeyModifiers.Shift, VirtualKeys.Digit(n))] = new(ShortcutCommand.MoveWindowToDesktop, n);
        }

        return new ShortcutMap(bindings);
    }
}

/// <summary>What the keyboard hook should do with a key event.</summary>
/// <param name="Swallow">Whether to stop the event reaching Windows and other apps.</param>
/// <param name="Action">The action to run, if the event completed a shortcut.</param>
/// <param name="SuppressStartMenu">
/// Whether to inject a harmless key now, so that releasing the Windows key later doesn't open the Start menu.
/// </param>
public readonly record struct KeyDecision(bool Swallow, ShortcutAction? Action, bool SuppressStartMenu)
{
    /// <summary>Gets a decision that lets the event through untouched.</summary>
    public static KeyDecision PassThrough { get; }
}

/// <summary>
/// Turns a stream of raw key events into shortcut actions. Pure logic used by the low-level keyboard hook, which
/// calls it on its own thread; the instance is not thread-safe.
/// </summary>
/// <param name="map">The bindings to match.</param>
public sealed class ShortcutMatcher(ShortcutMap map)
{
    private readonly HashSet<int> _heldModifierKeys = [];
    private readonly HashSet<int> _swallowedKeys = [];

    /// <summary>Gets or sets the bindings to match.</summary>
    public ShortcutMap Map { get; set; } = map ?? throw new ArgumentNullException(nameof(map));

    /// <summary>Gets the modifiers currently held, as seen by the matcher.</summary>
    public KeyModifiers HeldModifiers
    {
        get
        {
            var held = KeyModifiers.None;
            foreach (int key in _heldModifierKeys)
            {
                held |= VirtualKeys.ModifierOf(key);
            }

            return held;
        }
    }

    /// <summary>Processes one key event.</summary>
    /// <param name="virtualKey">The virtual-key code.</param>
    /// <param name="isDown"><see langword="true"/> for key down (including auto-repeat), <see langword="false"/> for key up.</param>
    /// <param name="physicalModifiers">
    /// The modifiers actually held according to the keyboard, if known. Preferred over the tracked state, which can
    /// go stale when key releases are missed (lock screen, UAC prompts, other hooks).
    /// </param>
    /// <returns>What to do with the event.</returns>
    public KeyDecision OnKey(int virtualKey, bool isDown, KeyModifiers? physicalModifiers = null)
    {
        if (VirtualKeys.ModifierOf(virtualKey) != KeyModifiers.None)
        {
            Observe(virtualKey, isDown);
            return KeyDecision.PassThrough;
        }

        if (!isDown)
        {
            // Swallow the release of a key whose press we swallowed, so apps never see half a keystroke.
            return _swallowedKeys.Remove(virtualKey) ? new KeyDecision(true, null, false) : KeyDecision.PassThrough;
        }

        var held = physicalModifiers ?? HeldModifiers;
        bool bound = Map.TryGetAction(new KeyChord(held, virtualKey), out var action);

        if (_swallowedKeys.Contains(virtualKey))
        {
            if (bound)
            {
                // Auto-repeat while the shortcut is still held: keep swallowing, run the action once per press.
                return new KeyDecision(true, null, false);
            }

            // The release was missed (e.g. shortcuts were switched off meanwhile); this is a fresh press.
            _swallowedKeys.Remove(virtualKey);
        }

        if (!bound)
        {
            return KeyDecision.PassThrough;
        }

        _swallowedKeys.Add(virtualKey);
        return new KeyDecision(true, action, held.HasFlag(KeyModifiers.Win));
    }

    /// <summary>
    /// Tracks a key event without matching it, for when shortcuts are switched off. Keeps modifier state current and
    /// forgets swallowed keys once released, so nothing is stale when matching resumes.
    /// </summary>
    /// <param name="virtualKey">The virtual-key code.</param>
    /// <param name="isDown"><see langword="true"/> for key down, <see langword="false"/> for key up.</param>
    public void Observe(int virtualKey, bool isDown)
    {
        if (VirtualKeys.ModifierOf(virtualKey) != KeyModifiers.None)
        {
            if (isDown)
            {
                _heldModifierKeys.Add(virtualKey);
            }
            else
            {
                _heldModifierKeys.Remove(virtualKey);
            }
        }
        else if (!isDown)
        {
            _swallowedKeys.Remove(virtualKey);
        }
    }

    /// <summary>Forgets all held keys, e.g. after the session was locked and key releases may have been missed.</summary>
    public void Reset()
    {
        _heldModifierKeys.Clear();
        _swallowedKeys.Clear();
    }
}
