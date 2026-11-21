using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Deskswap.Platform;

/// <summary>What happened to a top-level window.</summary>
public enum WindowEventKind
{
    /// <summary>The window was shown.</summary>
    Shown,

    /// <summary>The window was hidden.</summary>
    Hidden,

    /// <summary>The window was destroyed.</summary>
    Destroyed,

    /// <summary>The window became the foreground window.</summary>
    Foreground,

    /// <summary>The user finished moving or resizing the window.</summary>
    MoveSizeEnded,

    /// <summary>The window was restored from minimized.</summary>
    MinimizeEnded,

    /// <summary>The window was minimized.</summary>
    MinimizeStarted,

    /// <summary>The window's title changed (it may only now become eligible).</summary>
    Renamed,

    /// <summary>The window was cloaked (e.g. a suspended Store app, or it moved to another Windows desktop).</summary>
    Cloaked,

    /// <summary>The window was uncloaked.</summary>
    Uncloaked,
}

/// <summary>A window event.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Window">The window handle.</param>
public sealed record WindowEvent(WindowEventKind Kind, nint Window);

/// <summary>
/// Listens to window events from every process (<c>SetWinEventHook</c>, out of context) so Deskswap reacts to new,
/// closed, moved and re-shown windows immediately instead of polling (design §12).
/// </summary>
/// <remarks>
/// Install it from a thread with a message loop (the UI thread): events are delivered through that loop, so the
/// <see cref="WindowChanged"/> handler runs on that thread. Handlers must be quick; Deskswap's own windows are
/// skipped.
/// </remarks>
public sealed class WindowEventHook : IDisposable
{
    private readonly List<HWINEVENTHOOK> _hooks = [];
    private readonly WINEVENTPROC _callback; // Rooted for the lifetime of the hooks.

    /// <summary>Initializes a new instance of the <see cref="WindowEventHook"/> class.</summary>
    public WindowEventHook() => _callback = OnEvent;

    /// <summary>Raised on the installing thread for each event on a top-level window.</summary>
    public event EventHandler<WindowEvent>? WindowChanged;

    /// <summary>Installs the hooks.</summary>
    /// <returns><see langword="true"/> if every hook was installed.</returns>
    public bool Start()
    {
        if (_hooks.Count > 0)
        {
            return true;
        }

        bool all = true;
        foreach (var (min, max) in new (uint, uint)[]
        {
            (PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND),
            (PInvoke.EVENT_SYSTEM_MOVESIZEEND, PInvoke.EVENT_SYSTEM_MOVESIZEEND),
            (PInvoke.EVENT_SYSTEM_MINIMIZESTART, PInvoke.EVENT_SYSTEM_MINIMIZEEND),
            (PInvoke.EVENT_OBJECT_DESTROY, PInvoke.EVENT_OBJECT_HIDE),
            (PInvoke.EVENT_OBJECT_NAMECHANGE, PInvoke.EVENT_OBJECT_NAMECHANGE),
            (PInvoke.EVENT_OBJECT_CLOAKED, PInvoke.EVENT_OBJECT_UNCLOAKED),
        })
        {
            var hook = PInvoke.SetWinEventHook(min, max, HMODULE.Null, _callback, 0, 0, PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
            if (hook.IsNull)
            {
                all = false;
            }
            else
            {
                _hooks.Add(hook);
            }
        }

        return all;
    }

    /// <summary>Removes the hooks.</summary>
    public void Dispose()
    {
        foreach (var hook in _hooks)
        {
            PInvoke.UnhookWinEvent(hook);
        }

        _hooks.Clear();
    }

    private static WindowEventKind? KindOf(uint eventId) => eventId switch
    {
        PInvoke.EVENT_OBJECT_SHOW => WindowEventKind.Shown,
        PInvoke.EVENT_OBJECT_HIDE => WindowEventKind.Hidden,
        PInvoke.EVENT_OBJECT_DESTROY => WindowEventKind.Destroyed,
        PInvoke.EVENT_SYSTEM_FOREGROUND => WindowEventKind.Foreground,
        PInvoke.EVENT_SYSTEM_MOVESIZEEND => WindowEventKind.MoveSizeEnded,
        PInvoke.EVENT_SYSTEM_MINIMIZEEND => WindowEventKind.MinimizeEnded,
        PInvoke.EVENT_SYSTEM_MINIMIZESTART => WindowEventKind.MinimizeStarted,
        PInvoke.EVENT_OBJECT_NAMECHANGE => WindowEventKind.Renamed,
        PInvoke.EVENT_OBJECT_CLOAKED => WindowEventKind.Cloaked,
        PInvoke.EVENT_OBJECT_UNCLOAKED => WindowEventKind.Uncloaked,
        _ => null,
    };

    private unsafe void OnEvent(HWINEVENTHOOK hook, uint eventId, HWND hwnd, int idObject, int idChild, uint threadId, uint time)
    {
        // Only the window itself (OBJID_WINDOW, CHILDID_SELF), not its caret, scroll bars or child objects.
        if (idObject != 0 || idChild != 0 || hwnd.IsNull || KindOf(eventId) is not { } kind)
        {
            return;
        }

        try
        {
            WindowChanged?.Invoke(this, new WindowEvent(kind, (nint)hwnd.Value));
        }
#pragma warning disable CA1031 // An exception must never cross into the native callback; the periodic sweep catches up.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
