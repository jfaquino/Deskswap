using System.ComponentModel;
using System.Runtime.InteropServices;
using Deskswap.Core.Input;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Deskswap.Platform;

/// <summary>
/// A low-level keyboard hook (<c>WH_KEYBOARD_LL</c>) that takes over Deskswap's shortcuts, including the
/// <c>Win+Ctrl</c> ones Windows reserves (design §6.1).
/// </summary>
/// <remarks>
/// The hook runs on its own thread with its own message loop. Its callback only matches keys and hands actions to
/// <c>onAction</c>, which must return immediately (post the work to the UI thread). Windows silently removes
/// low-level hooks whose callbacks are slow.
/// </remarks>
public sealed class KeyboardHook : IDisposable
{
    /// <summary>Marks input Deskswap injects itself, so the hook ignores it.</summary>
    private const nuint InjectedMarker = 0x4453_5750; // "DSWP"

    private readonly ShortcutMatcher _matcher;
    private readonly Action<ShortcutAction> _onAction;
    private readonly ManualResetEventSlim _started = new();
    private Thread? _thread;
    private HOOKPROC? _callback; // Rooted for the lifetime of the hook.
    private HHOOK _hook;
    private uint _threadId;
    private int _startError;
    private volatile bool _resetRequested;
    private ShortcutMap? _pendingMap;

    /// <summary>Initializes a new instance of the <see cref="KeyboardHook"/> class.</summary>
    /// <param name="map">The shortcuts to take over.</param>
    /// <param name="onAction">Called on the hook thread when a shortcut fires; must return immediately.</param>
    public KeyboardHook(ShortcutMap map, Action<ShortcutAction> onAction)
    {
        _matcher = new ShortcutMatcher(map);
        _onAction = onAction ?? throw new ArgumentNullException(nameof(onAction));
    }

    /// <summary>
    /// Replaces the shortcuts to take over, e.g. only the pause toggle while paused. Takes effect on the next key
    /// event; key state is kept, so keys held across the change are not misread.
    /// </summary>
    /// <param name="map">The new shortcuts.</param>
    public void SetMap(ShortcutMap map) => Volatile.Write(ref _pendingMap, map ?? throw new ArgumentNullException(nameof(map)));

    /// <summary>Installs the hook on a dedicated thread.</summary>
    /// <exception cref="Win32Exception">Windows refused to install the hook.</exception>
    /// <exception cref="TimeoutException">The hook thread did not start in time.</exception>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "Deskswap keyboard hook" };
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("The keyboard hook thread did not start.");
        }

        if (_startError != 0)
        {
            throw new Win32Exception(_startError, "Could not install the keyboard hook.");
        }
    }

    /// <summary>Forgets held keys, e.g. after the session is unlocked and key releases may have been missed.</summary>
    public void ResetKeyState() => _resetRequested = true;

    /// <summary>Removes the hook and stops its thread.</summary>
    public void Dispose()
    {
        if (_thread is { IsAlive: true })
        {
            PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, default, default);
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _started.Dispose();
    }

    private void Run()
    {
        _threadId = PInvoke.GetCurrentThreadId();
        _callback = OnKeyboardEvent;
        _hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _callback, HINSTANCE.Null, 0);
        if (_hook.IsNull)
        {
            _startError = Marshal.GetLastPInvokeError();
            _started.Set();
            return;
        }

        _started.Set();
        try
        {
            // Low-level hooks are called through this thread's message loop.
            while (PInvoke.GetMessage(out _, HWND.Null, 0, 0) > 0)
            {
            }
        }
        finally
        {
            PInvoke.UnhookWindowsHookEx(_hook);
        }
    }

    private unsafe LRESULT OnKeyboardEvent(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0)
        {
            var data = (KBDLLHOOKSTRUCT*)lParam.Value;
            if (data->dwExtraInfo != InjectedMarker && Decide(data->vkCode, (uint)wParam.Value))
            {
                return (LRESULT)1;
            }
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
    }

    /// <summary>Matches the key and returns whether to swallow it. Never throws into native code.</summary>
    private bool Decide(uint virtualKey, uint message)
    {
        try
        {
            if (_resetRequested)
            {
                _resetRequested = false;
                _matcher.Reset();
            }

            if (Interlocked.Exchange(ref _pendingMap, null) is { } map)
            {
                _matcher.Map = map;
            }

            bool isDown = message is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN;
            var decision = _matcher.OnKey((int)virtualKey, isDown, isDown ? PhysicalModifiers() : null);
            if (decision.SuppressStartMenu)
            {
                InjectUnassignedKey();
            }

            if (decision.Action is { } action)
            {
                _onAction(action);
            }

            return decision.Swallow;
        }
#pragma warning disable CA1031 // An exception must never cross into the native hook chain; let the key through instead.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <summary>
    /// Reads which modifiers are physically held. Inside a low-level hook this reflects every event before the
    /// current one, so it is exact for the modifiers of a shortcut and immune to missed key releases.
    /// </summary>
    private static KeyModifiers PhysicalModifiers()
    {
        static bool IsDown(int key) => (PInvoke.GetAsyncKeyState(key) & 0x8000) != 0;

        var held = KeyModifiers.None;
        if (IsDown(VirtualKeys.LeftWin) || IsDown(VirtualKeys.RightWin))
        {
            held |= KeyModifiers.Win;
        }

        if (IsDown(VirtualKeys.Control))
        {
            held |= KeyModifiers.Ctrl;
        }

        if (IsDown(VirtualKeys.Shift))
        {
            held |= KeyModifiers.Shift;
        }

        if (IsDown(VirtualKeys.Menu))
        {
            held |= KeyModifiers.Alt;
        }

        return held;
    }

    /// <summary>
    /// Presses and releases an unassigned key. Windows opens the Start menu when the Windows key is released with
    /// no other key seen in between; the swallowed shortcut key doesn't count, this one does.
    /// </summary>
    private static unsafe void InjectUnassignedKey()
    {
        Span<INPUT> inputs = stackalloc INPUT[2];
        for (int i = 0; i < inputs.Length; i++)
        {
            inputs[i].type = INPUT_TYPE.INPUT_KEYBOARD;
            inputs[i].Anonymous.ki = new KEYBDINPUT
            {
                wVk = (VIRTUAL_KEY)VirtualKeys.Unassigned,
                dwFlags = i == 0 ? 0 : KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP,
                dwExtraInfo = InjectedMarker,
            };
        }

        PInvoke.SendInput(inputs, sizeof(INPUT));
    }
}
