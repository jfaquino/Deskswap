using System.Windows.Input;
using System.Windows.Threading;
using Deskswap.Core;
using Deskswap.Platform;

namespace Deskswap.Ui.Overview;

/// <summary>
/// Runs the overview (overview-ux.md): one window per monitor, opened together, plus dragging windows between them.
/// The app owns the desktop model; the overview only shows <see cref="OverviewState"/> and reports what the user
/// wants through <see cref="IOverviewActions"/>. Use from the UI thread only.
/// </summary>
public sealed class OverviewHost : IDisposable
{
    /// <summary>A dragged window shrinks a little, like a lifted card.</summary>
    private const double DragScale = 0.95;

    private readonly OverviewText _text;
    private readonly Dictionary<MonitorId, MonitorOverview> _windows = [];
    private Theme? _theme;
    private DragGhost? _ghost;
    private Drag? _drag;
    private (WindowId Window, ScreenRect Bounds)? _landing;
    private OverviewState? _state;
    private ThumbnailAnimator? _animator;

    /// <summary>Initializes a new instance of the <see cref="OverviewHost"/> class.</summary>
    /// <param name="actions">Where user actions go.</param>
    /// <param name="text">Localized text.</param>
    public OverviewHost(IOverviewActions actions, OverviewText text)
    {
        Actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        // Errors in the overview's own event handlers would otherwise end the process without showing hidden windows.
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            try
            {
                CancelDrag();
                Close(animate: false);
            }
#pragma warning disable CA1031 // Already failing: report the original error, whatever closing throws.
            catch (Exception)
#pragma warning restore CA1031
            {
                IsOpen = false;
            }

            Failed?.Invoke(this, e.Exception);
        };
    }

    /// <summary>Raised when the overview (or the app windows view) has finished closing on every monitor.</summary>
    public event EventHandler? Closed;

    /// <summary>Raised when the overview hit an unexpected error; it has closed itself.</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>Raised with a line for the log describing how smoothly an animation ran.</summary>
    public event EventHandler<string>? Measured;

    /// <summary>Gets a value indicating whether the overview or the app windows view is open.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Gets a value indicating whether any overview window is on screen, including while it animates out.</summary>
    public bool IsOnScreen => _windows.Values.Any(w => w.IsShown);

    /// <summary>Gets a value indicating whether what is open is the app windows view (overview-ux.md §4).</summary>
    public bool IsAppView { get; private set; }

    /// <summary>Gets where user actions go.</summary>
    internal IOverviewActions Actions { get; }

    /// <summary>Gets the thread that moves live previews (created on first use).</summary>
    internal ThumbnailAnimator Animator
    {
        get
        {
            if (_animator is null)
            {
                _animator = new ThumbnailAnimator(Dispatcher.CurrentDispatcher);
                _animator.Measured += (_, line) => Report(line);
            }

            return _animator;
        }
    }

    /// <summary>Gets a value indicating whether a window is being dragged.</summary>
    internal bool IsDragging => _drag is not null;

    /// <summary>
    /// Creates the overview windows ahead of time so the first opening is fast. Optional.
    /// </summary>
    /// <param name="monitors">The monitors.</param>
    public void Prewarm(IReadOnlyList<OverviewMonitor> monitors, OverviewState? state = null)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        EnsureWindows(monitors);
        if (state is not null && !IsOpen)
        {
            foreach (var window in _windows.Values)
            {
                window.WarmUp(state);
            }
        }

        _ghost ??= new DragGhost();
        foreach (var monitor in monitors)
        {
            if (Images.LoadWallpaper(monitor.Wallpaper) is { } wallpaper)
            {
                Images.Blurred(wallpaper); // Cached for the first opening.
            }
        }
    }

    /// <summary>Opens the overview on every monitor.</summary>
    /// <param name="state">What to show.</param>
    /// <param name="backdrops">Each monitor's image, taken just before opening.</param>
    /// <param name="focus">The monitor that gets the keyboard (the one under the mouse).</param>
    public void Open(OverviewState state, IReadOnlyDictionary<MonitorId, PixelImage> backdrops, MonitorId focus)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(backdrops);
        if (IsOpen)
        {
            Update(state);
            return;
        }

        _state = state;
        EnsureWindows(state.Monitors);
        IsOpen = true;
        foreach (var monitor in state.Monitors)
        {
            _windows[monitor.Id].Open(state, monitor, backdrops.GetValueOrDefault(monitor.Id));
        }

        if (_windows.TryGetValue(focus, out var focused))
        {
            focused.TakeFocus();
        }
    }

    /// <summary>Opens the app windows view on one monitor, closing the overview if it is open.</summary>
    /// <param name="state">What to show.</param>
    /// <param name="backdrop">The monitor's image, taken just before opening.</param>
    public void OpenAppWindows(AppWindowsState state, PixelImage? backdrop)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (IsOpen)
        {
            Close(animate: false);
        }

        _theme ??= Theme.Current();
        if (!_windows.TryGetValue(state.Monitor.Id, out var window))
        {
            window = new MonitorOverview(this, _theme, _text, state.Monitor);
            _windows[state.Monitor.Id] = window;
        }

        IsOpen = true;
        IsAppView = true;
        window.OpenApp(state, backdrop);
        window.TakeFocus();
    }

    /// <summary>Shows a new state of the app windows view, e.g. after a window closed.</summary>
    /// <param name="state">The state.</param>
    public void UpdateAppWindows(AppWindowsState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (IsOpen && IsAppView && _windows.TryGetValue(state.Monitor.Id, out var window))
        {
            window.UpdateApp(state);
        }
    }

    /// <summary>Shows a new state after the app changed the desktops.</summary>
    /// <param name="state">The state.</param>
    public void Update(OverviewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!IsOpen || IsAppView)
        {
            return;
        }

        _state = state;
        if (!state.Monitors.Select(m => m.Id).ToHashSet().SetEquals(_windows.Keys))
        {
            Close(animate: false); // Monitors changed under the overview; reopening would show stale images.
            return;
        }

        foreach (var window in _windows.Values)
        {
            window.Update(state);
        }
    }

    /// <summary>Closes the overview on every monitor.</summary>
    /// <param name="animate">Whether windows fly back to their places; use <see langword="false"/> after a switch.</param>
    public void Close(bool animate)
    {
        if (!IsOpen && (animate || !IsOnScreen))
        {
            return; // Closing without animation also finishes a closing animation.
        }

        CancelDrag();
        IsOpen = false;
        IsAppView = false;
        foreach (var window in _windows.Values)
        {
            window.Close(animate);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var window in _windows.Values)
        {
            window.Destroy();
        }

        _windows.Clear();
        _ghost?.Close();
        _ghost = null;
        _animator?.Dispose();
        _animator = null;
    }

    /// <summary>Passes on a line describing how smoothly an animation ran.</summary>
    /// <param name="line">The line.</param>
    internal void Report(string line) => Measured?.Invoke(this, line);

    // ------------------------------------------------------------------ called by the monitor windows

    /// <summary>Called when a monitor window finished hiding.</summary>
    internal void OnWindowHidden()
    {
        if (!IsOpen && _windows.Values.All(w => !w.IsShown))
        {
            Images.ClearIconCache();
            _state = null;
            _landing = null;
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Gets a value indicating whether a specific window is being dragged.</summary>
    /// <param name="window">The window.</param>
    /// <returns><see langword="true"/> while it is dragged.</returns>
    internal bool IsDraggingWindow(WindowId window) => _drag?.Window.Id == window;

    /// <summary>Takes the place a dropped window was released at, so its new tile glides from there.</summary>
    /// <param name="window">The window.</param>
    /// <returns>The screen rectangle, or <see langword="null"/>.</returns>
    internal ScreenRect? TakeLanding(WindowId window)
    {
        if (_landing is { } landing && landing.Window == window)
        {
            _landing = null;
            return landing.Bounds;
        }

        return null;
    }

    /// <summary>Starts dragging a window's preview.</summary>
    internal void BeginDrag(OverviewWindow window, ScreenRect tile, int pointerX, int pointerY)
    {
        _theme ??= Theme.Current();
        _ghost ??= new DragGhost();
        int width = (int)(tile.Width * DragScale);
        int height = (int)(tile.Height * DragScale);

        // Keep the grab point under the pointer, proportionally.
        double grabX = (pointerX - tile.Left) / (double)Math.Max(1, tile.Width);
        double grabY = (pointerY - tile.Top) / (double)Math.Max(1, tile.Height);
        _drag = new Drag(window, width, height, grabX, grabY);
        _ghost.Begin(window, Ghost(_drag, pointerX, pointerY), _theme);
        foreach (var overview in _windows.Values)
        {
            overview.RefreshTiles();
        }
    }

    /// <summary>Moves the dragged preview and updates drop targets.</summary>
    internal void DragTo(int x, int y)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        drag.LastX = x;
        drag.LastY = y;

        DropHit? hit = null;
        foreach (var window in _windows.Values)
        {
            hit = window.PointerOver(x, y) ?? hit;
        }

        drag.Hit = hit;

        // As on macOS: nearing the strip, the window shrinks toward card size so it is easy to drop on a card.
        double nearness = hit?.Nearness ?? 0;
        if (hit is { } h && nearness > 0)
        {
            double fit = Math.Min(h.CardSize.Width * 0.9 / drag.Width, h.CardSize.Height * 0.9 / drag.Height);
            double scale = 1 + ((Math.Min(1, fit) - 1) * (1 - Math.Pow(1 - nearness, 2)));
            drag.CurrentWidth = Math.Max(1, (int)(drag.Width * scale));
            drag.CurrentHeight = Math.Max(1, (int)(drag.Height * scale));
        }
        else
        {
            drag.CurrentWidth = drag.Width;
            drag.CurrentHeight = drag.Height;
        }

        _ghost?.MoveTo(Ghost(drag, x, y));
    }

    /// <summary>Ends a drag: drops the window on the target under the pointer, or puts it back.</summary>
    internal void EndDrag(bool drop)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        _drag = null;
        _ghost?.End();
        _landing = (drag.Window.Id, Ghost(drag, drag.LastX, drag.LastY));
        foreach (var window in _windows.Values)
        {
            window.PointerLeft();
        }

        if (drop && drag.Hit is { } hit)
        {
            if (hit.Strip is { IsAdd: true })
            {
                Actions.MoveWindowToNewDesktop(drag.Window.Id, hit.Window.Monitor.Id);
            }
            else if (hit.Strip is { } card)
            {
                Actions.MoveWindow(drag.Window.Id, card.Desktop);
            }
            else if (hit.InArea)
            {
                Actions.MoveWindow(drag.Window.Id, hit.Window.Monitor.Shown);
            }
        }

        // Tiles kept alive during the drag, and the dragged one, take their places again.
        if (IsOpen && _state is not null)
        {
            Update(_state);
        }
    }

    /// <summary>
    /// Esc from the keyboard hook: cancels a rename or a drag, otherwise closes the overview. Works whichever window
    /// has the keyboard.
    /// </summary>
    public void HandleEscape()
    {
        if (!IsOpen)
        {
            return;
        }

        if (_windows.Values.FirstOrDefault(w => w.IsEditingText) is { } editing)
        {
            editing.CancelRename();
        }
        else if (IsDragging)
        {
            CancelDrag();
        }
        else
        {
            Actions.Dismiss();
        }
    }

    /// <summary>Routes a key press from any monitor's overview (overview-ux.md §5, keyboard).</summary>
    internal void OnKey(MonitorOverview window, KeyEventArgs e)
    {
        if (window.IsEditingText)
        {
            return; // The rename box handles its own keys.
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (window.IsAppView)
        {
            // Picking a window is all the app windows view does.
            switch (key)
            {
                case Key.Escape:
                    Actions.Dismiss();
                    break;
                case Key.Left or Key.Right or Key.Up or Key.Down:
                    window.MoveSelection(key);
                    break;
                case Key.Enter or Key.Space:
                    window.ActivateSelection();
                    break;
                default:
                    e.Handled = false;
                    break;
            }

            return;
        }

        switch (key)
        {
            case Key.Escape when IsDragging:
                CancelDrag();
                break;
            case Key.Escape:
                Actions.Dismiss();
                break;
            case Key.Left or Key.Right when ctrl:
                FocusNeighbor(window, key == Key.Left ? -1 : 1);
                break;
            case Key.N when ctrl:
                Actions.AddDesktop(window.Monitor.Id);
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                window.MoveSelection(key);
                break;
            case Key.Enter or Key.Space:
                window.ActivateSelection();
                break;
            case Key.Tab:
                window.ToggleStripFocus();
                break;
            case Key.F2:
                window.RenameFocusedDesktop();
                break;
            case Key.P:
                window.TogglePinSelection();
                break;
            case Key.Delete when window.IsStripFocused:
                Actions.RemoveDesktop(window.FocusedDesktop, window.Monitor.Id);
                break;
            case >= Key.D1 and <= Key.D9:
            case >= Key.NumPad1 and <= Key.NumPad9:
                int number = key >= Key.NumPad1 ? key - Key.NumPad1 + 1 : key - Key.D1 + 1;
                if (_state is not null && number <= _state.Desktops.Count)
                {
                    Actions.ShowDesktop(window.Monitor.Id, _state.Desktops[number - 1].Id, close: true);
                }

                break;
            default:
                e.Handled = false;
                break;
        }
    }

    // ------------------------------------------------------------------ internals

    private void EnsureWindows(IReadOnlyList<OverviewMonitor> monitors)
    {
        var theme = Theme.Current();
        bool themeChanged = _theme is null || theme.IsDark != _theme.IsDark || theme.Accent != _theme.Accent || theme.IsHighContrast != _theme.IsHighContrast;
        _theme = theme;

        foreach (var (id, window) in _windows.ToList())
        {
            if (themeChanged || !monitors.Any(m => m.Id == id))
            {
                window.Destroy();
                _windows.Remove(id);
            }
        }

        foreach (var monitor in monitors)
        {
            if (!_windows.ContainsKey(monitor.Id))
            {
                _windows[monitor.Id] = new MonitorOverview(this, theme, _text, monitor);
            }
        }
    }

    private void CancelDrag()
    {
        if (_drag is not null)
        {
            EndDrag(drop: false);
        }
    }

    private void FocusNeighbor(MonitorOverview current, int direction)
    {
        var ordered = _windows.Values.OrderBy(w => w.Monitor.Bounds.Left).ToList();
        int index = ordered.IndexOf(current) + direction;
        if (index >= 0 && index < ordered.Count)
        {
            ordered[index].TakeFocus();
        }
    }

    private static ScreenRect Ghost(Drag drag, int x, int y) => ScreenRect.FromSize(
        x - (int)(drag.GrabX * drag.CurrentWidth),
        y - (int)(drag.GrabY * drag.CurrentHeight),
        Math.Max(1, drag.CurrentWidth),
        Math.Max(1, drag.CurrentHeight));

    private sealed class Drag(OverviewWindow window, int width, int height, double grabX, double grabY)
    {
        public OverviewWindow Window { get; } = window;

        public int Width { get; } = width;

        public int Height { get; } = height;

        /// <summary>Gets or sets the width now (smaller near the strip).</summary>
        public int CurrentWidth { get; set; } = width;

        /// <summary>Gets or sets the height now (smaller near the strip).</summary>
        public int CurrentHeight { get; set; } = height;

        public double GrabX { get; } = grabX;

        public double GrabY { get; } = grabY;

        public int LastX { get; set; }

        public int LastY { get; set; }

        public DropHit? Hit { get; set; }

    }
}
