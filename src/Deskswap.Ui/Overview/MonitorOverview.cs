using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Deskswap.Core;
using Deskswap.Platform;

namespace Deskswap.Ui.Overview;

/// <summary>Where a dragged window is over a monitor's overview.</summary>
/// <param name="Window">The overview under the pointer.</param>
/// <param name="Strip">The strip card under the pointer, if any.</param>
/// <param name="InArea">Whether the pointer is over the window area.</param>
/// <param name="Nearness">How close the pointer is to the strip, from 0 (far) to 1 (on it).</param>
/// <param name="CardSize">The size of a desktop card on this monitor, in screen pixels.</param>
internal readonly record struct DropHit(MonitorOverview Window, StripTarget? Strip, bool InArea, double Nearness, Size CardSize);

/// <summary>
/// One monitor's overview (overview-ux.md §3): a full-screen window with a blurred image of the monitor behind, the
/// desktop strip on top, and live previews of the monitor's windows arranged in the window area.
/// </summary>
internal sealed class MonitorOverview : Window
{
    private const double SideMargin = 56;
    private const double BottomMargin = 40;
    private const double AreaGap = 24;
    private const double TileGap = 28;
    private const double DragThreshold = 6;
    private const double NearStrip = 72;

    /// <summary>Space under each preview for the window's icon and title, in DIPs.</summary>
    private const double CaptionHeight = 30;

    private const double OpenMs = 280;
    private const double CloseMs = 200;

    /// <summary>How far below the strip a dragged window starts shrinking toward card size, in DIPs.</summary>
    private const double ShrinkZone = 220;
    private const double MoveMs = 200;
    private const double HoverMs = 120;

    /// <summary>Minimized windows are drawn a little faded, so they read as not on screen.</summary>
    private const byte MinimizedOpacity = 191;

    /// <summary>How long a tile's frame, shadow and caption take to appear once its preview has arrived.</summary>
    private const double DecorationsFadeMs = 140;

    /// <summary>If previews haven't reported arriving by this long after closing should have ended, it ends anyway.</summary>
    private const double CloseGraceMs = 500;

    private readonly OverviewHost _host;
    private readonly Theme _theme;
    private readonly OverviewText _text;
    private readonly nint _hwnd;
    private readonly Grid _root = new() { Focusable = true, FocusVisualStyle = null, ClipToBounds = true };
    private readonly Border _wallpaper = new();
    private readonly Image _screen = new() { Stretch = Stretch.Fill };
    private readonly Image _blurred = new() { Stretch = Stretch.UniformToFill, Opacity = 0 };
    private double _tintOpacity;
    private readonly TranslateTransform _stripOffset = new();
    private readonly Rectangle _tint = new();
    private readonly Canvas _canvas = new();
    private readonly TextBlock _empty;
    private readonly DesktopStrip _strip;
    private readonly Dictionary<WindowId, Tile> _tiles = [];
    private readonly List<WindowId> _order = [];
    private OverviewState? _state;
    private double _scale = 1;
    private Phase _phase = Phase.Hidden;
    private long _phaseStart;
    private FrameTiming? _phaseTiming;
    private int _handedBack;

    /// <summary>Preview motions of the opening, started on the first frame drawn so none of it happens unseen.</summary>
    private readonly List<Action> _motionsAtFirstFrame = [];
    private bool _rendering;
    private bool _allowClose;
    private bool _stripFocused;
    private WindowId? _selected;
    private Tile? _pressed;
    private int _pressX;
    private int _pressY;
    private bool _dragging;
    private bool _clickEndsRename;

    /// <summary>The app windows view's state; <see langword="null"/> in the overview.</summary>
    private AppWindowsState? _app;
    private readonly StackPanel _appHeader = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 22, 0, 0),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    /// <summary>One header per desktop group, created when the view's content changes and only moved by layout.</summary>
    private readonly List<(DesktopId Desktop, Grid Header)> _groupHeaders = [];

    public MonitorOverview(OverviewHost host, Theme theme, OverviewText text, OverviewMonitor monitor)
    {
        _host = host;
        _theme = theme;
        _text = text;
        Monitor = monitor;

        Title = "Deskswap overview";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = theme.Backdrop;

        RenderOptions.SetBitmapScalingMode(_screen, BitmapScalingMode.Linear);
        RenderOptions.SetBitmapScalingMode(_blurred, BitmapScalingMode.Linear);
        _tint.Fill = theme.Backdrop;
        _tint.Opacity = 0;
        _empty = new TextBlock
        {
            FontFamily = Theme.TextFont,
            FontSize = 20,
            Foreground = theme.TextSecondary,
            Text = text.NoWindows,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        _strip = new DesktopStrip(theme, text);
        _strip.DesktopClicked += (_, desktop) => _host.Actions.ShowDesktop(Monitor.Id, desktop, close: true);
        _strip.AddClicked += (_, _) => _host.Actions.AddDesktop(Monitor.Id);
        _strip.RemoveClicked += (_, desktop) => _host.Actions.RemoveDesktop(desktop, Monitor.Id);
        _strip.Renamed += (_, e) => _host.Actions.RenameDesktop(e.Desktop, e.Name);
        _strip.RenameEnded += (_, _) => Keyboard.Focus(_root);
        _strip.ExpandedChanged += (_, _) => Relayout(animate: true, DesktopStrip.ExpandDuration);
        _strip.DesktopMoved += (_, e) => _host.Actions.MoveDesktop(e.Desktop, e.Number);

        // As on macOS: the strip opens as names, and once the pointer reaches it, it stays expanded until the
        // overview closes.
        _strip.MouseEnter += (_, _) => _strip.SetExpanded(true);
        _strip.RenderTransform = _stripOffset;

        _root.Children.Add(_wallpaper);
        _root.Children.Add(_blurred);
        _root.Children.Add(_screen);
        _root.Children.Add(_tint);
        _root.Children.Add(_canvas);
        _root.Children.Add(_empty);
        _root.Children.Add(_strip);
        _root.Children.Add(_appHeader);
        _root.MouseLeftButtonUp += (_, e) =>
        {
            if (!e.Handled && !_strip.IsMouseOver && _phase == Phase.Open && !_clickEndsRename)
            {
                _host.Actions.Dismiss();
            }
        };

        // Clicking anywhere outside the rename box saves the name, and the click still does what it would have done
        // (switch desktops, pick a window). A click on empty space only ends editing; it doesn't close the overview.
        _root.PreviewMouseDown += (_, e) => _clickEndsRename = _strip.IsRenaming && !_strip.IsInRenameBox(e.OriginalSource);
        _root.PreviewMouseUp += (_, _) =>
        {
            if (_clickEndsRename)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    _clickEndsRename = false;
                    _strip.CommitRename();
                });
            }
        };
        Content = _root;

        PreviewKeyDown += (_, e) => _host.OnKey(this, e);
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Place();

            // Windows also reports this when the window is shown; only a real change of scale needs a new layout
            // (re-laying out without animation would stop the opening where it is).
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (Math.Abs(scale - _scale) > 0.001)
            {
                _scale = scale;
                Relayout(animate: false);
            }
        });

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(this);
        Place();
    }

    private enum Phase
    {
        Hidden,
        Opening,
        Open,
        Closing,
    }

    /// <summary>Gets the monitor this overview covers.</summary>
    public OverviewMonitor Monitor { get; private set; }

    /// <summary>Gets a value indicating whether it is on screen (including while it animates out).</summary>
    public bool IsShown => _phase != Phase.Hidden;

    /// <summary>Gets the desktop selected in the strip with the keyboard, or else the one shown here.</summary>
    public DesktopId FocusedDesktop => _stripFocused && _strip.SelectedTarget is { IsAdd: false } target ? target.Desktop : Monitor.Shown;

    /// <summary>Shows the overview.</summary>
    /// <param name="state">What to show.</param>
    /// <param name="monitor">This monitor's current data.</param>
    /// <param name="backdrop">The image of the monitor taken just before opening.</param>
    public void Open(OverviewState state, OverviewMonitor monitor, PixelImage? backdrop)
    {
        SetMode(null);
        BeginOpen(monitor, backdrop);
        Update(state, fromRealPlaces: true);
        EnsureRendering();
    }

    /// <summary>Shows the app windows view (overview-ux.md §4) on this monitor.</summary>
    /// <param name="state">What to show.</param>
    /// <param name="backdrop">The image of the monitor taken just before opening.</param>
    public void OpenApp(AppWindowsState state, PixelImage? backdrop)
    {
        SetMode(state);
        BeginOpen(state.Monitor, backdrop);
        UpdateApp(state, fromRealPlaces: true);
        EnsureRendering();
    }

    /// <summary>Shows a new state of the app windows view.</summary>
    /// <param name="state">The state.</param>
    /// <param name="fromRealPlaces">Start every tile at its window's real place (the opening animation).</param>
    public void UpdateApp(AppWindowsState state, bool fromRealPlaces = false)
    {
        _app = state;
        Monitor = state.Monitor;
        _appHeader.Children.Clear();
        if (Images.FromIcon(state.AppIcon) is { } icon)
        {
            _appHeader.Children.Add(new Image { Source = icon, Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        }

        _appHeader.Children.Add(new TextBlock { Text = state.AppName, FontFamily = Theme.TextFont, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = _theme.Text, VerticalAlignment = VerticalAlignment.Center });
        RebuildGroupHeaders(state);
        SyncTiles([.. state.Groups.SelectMany(g => g.Windows)], fromRealPlaces);
    }

    /// <summary>
    /// Creates the desktop headers. Only here, never during layout: changing the window area's children while WPF
    /// lays them out throws.
    /// </summary>
    private void RebuildGroupHeaders(AppWindowsState state)
    {
        ClearGroupHeaders();
        foreach (var group in state.Groups.Where(g => g.Windows.Count > 0))
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            line.Children.Add(new TextBlock { Text = group.Title, FontFamily = Theme.TextFont, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = _theme.Text });
            if (group.Detail is { } detail)
            {
                line.Children.Add(new TextBlock { Text = "  ·  " + detail, FontFamily = Theme.TextFont, FontSize = 13, Foreground = _theme.TextSecondary });
            }

            // A full-width row with the text centered in it, so layout only has to move it.
            var header = new Grid { IsHitTestVisible = false, Children = { line } };
            _canvas.Children.Add(header);
            _groupHeaders.Add((group.Desktop, header));
        }
    }

    /// <summary>Gets a value indicating whether this window shows the app windows view.</summary>
    public bool IsAppView => _app is not null;

    private void SetMode(AppWindowsState? app)
    {
        _app = app;
        _strip.Visibility = app is null ? Visibility.Visible : Visibility.Collapsed;
        _appHeader.Visibility = app is null ? Visibility.Collapsed : Visibility.Visible;
        ClearGroupHeaders();
    }

    private void ClearGroupHeaders()
    {
        foreach (var (_, header) in _groupHeaders)
        {
            _canvas.Children.Remove(header);
        }

        _groupHeaders.Clear();
    }

    private void BeginOpen(OverviewMonitor monitor, PixelImage? backdrop)
    {
        Monitor = monitor;
        _stripFocused = false;
        _selected = null;
        _strip.Selected = -1;
        _strip.SetExpanded(false);
        Place();
        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        SetBackdrop(monitor, backdrop); // Before the first frame, which must look exactly like the screen.
        Show();
        Place();
        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;

        _phase = Phase.Opening;
        _phaseStart = 0; // Timed from the first frame drawn, so setup work never eats into the animation.
        _phaseTiming = new FrameTiming();
    }

    /// <summary>Shows a new state: tiles appear, disappear and glide to their new places.</summary>
    /// <param name="state">The state.</param>
    /// <param name="fromRealPlaces">Start every tile at its window's real place (the opening animation).</param>
    public void Update(OverviewState state, bool fromRealPlaces = false)
    {
        _state = state;
        if (state.Monitors.FirstOrDefault(m => m.Id == Monitor.Id) is { } monitor)
        {
            Monitor = monitor;
        }

        double width = Monitor.Bounds.Width / _scale;
        double height = Monitor.Bounds.Height / _scale;
        _strip.Update(state, Monitor, width, height);

        SyncTiles([.. state.Windows.Where(w => w.Monitor == Monitor.Id)], fromRealPlaces);
    }

    /// <summary>Makes the tiles match a list of windows: they appear, disappear and glide to their new places.</summary>
    private void SyncTiles(List<OverviewWindow> windows, bool fromRealPlaces)
    {
        var wanted = windows.Select(w => w.Id).ToHashSet();
        foreach (var id in _tiles.Keys.Where(id => !wanted.Contains(id) && !_host.IsDraggingWindow(id)).ToList())
        {
            RemoveTile(id);
        }

        _order.RemoveAll(id => !_tiles.ContainsKey(id));
        foreach (var window in windows)
        {
            var real = ToRect(window.Bounds.Offset(-Monitor.Bounds.Left, -Monitor.Bounds.Top));
            if (!_tiles.TryGetValue(window.Id, out var tile))
            {
                tile = CreateTile(window);
                tile.Current = _host.TakeLanding(window.Id) is { } landing
                    ? ToRect(landing.Offset(-Monitor.Bounds.Left, -Monitor.Bounds.Top))
                    : real;
                _tiles[window.Id] = tile;
                _order.Add(window.Id);
            }

            var before = tile.Model;
            tile.Model = window;
            tile.Real = real;
            if (before.IsMinimized != window.IsMinimized || before.IsOnScreen != window.IsOnScreen
                || before.Bounds.Width != window.Bounds.Width || before.Bounds.Height != window.Bounds.Height)
            {
                RefreshThumbnail(tile); // Minimized, restored, maximized, resized, or its desktop hidden or shown.
            }

            UpdatePlaceholder(tile);
            UpdatePin(tile);
        }

        if (fromRealPlaces)
        {
            foreach (var tile in _tiles.Values.Where(IsOnThisScreen))
            {
                tile.Current = tile.Real;
            }
        }

        if (_selected is { } selected && !_tiles.ContainsKey(selected))
        {
            _selected = null;
        }

        Relayout(animate: true, fromRealPlaces ? OpenMs : MoveMs);
    }

    /// <summary>Hides the overview, optionally flying windows back to their places first.</summary>
    /// <param name="animate">Whether to animate.</param>
    public void Close(bool animate)
    {
        if (_phase == Phase.Hidden)
        {
            return;
        }

        foreach (var tile in _tiles.Values)
        {
            tile.Hover = false;
        }

        _strip.CommitRename(); // Closing keeps what was typed, as clicking elsewhere does.
        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            FinishClose();
            return;
        }

        _phase = Phase.Closing;
        _phaseStart = 0;
        _phaseTiming = new FrameTiming();
        foreach (var tile in _tiles.Values)
        {
            // Windows on screen here fly back to their places; others (hidden, minimized, elsewhere) shrink away.
            Animate(tile, IsOnThisScreen(tile) ? tile.Real : Inflate(tile.Target, -0.08), CloseMs, easeIn: true);
        }

        EnsureRendering();
    }

    /// <summary>Gives this overview the keyboard.</summary>
    public void TakeFocus()
    {
        WindowStyling.Activate(_hwnd);
        Activate();
        Keyboard.Focus(_root);

        // Windows sometimes refuses the first attempt (e.g. right after a key it handled itself); try once more when
        // the window has been drawn.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_phase != Phase.Hidden && !IsActive)
            {
                WindowStyling.Activate(_hwnd);
                Activate();
            }

            if (_phase != Phase.Hidden && !IsEditingText)
            {
                Keyboard.Focus(_root);
            }
        });
    }

    /// <summary>Cancels a rename in progress.</summary>
    public void CancelRename() => _strip.CancelRename();

    /// <summary>
    /// Draws the window once, off-screen and without activating it, so the first real opening doesn't pay for
    /// WPF's one-time work (graphics device, shaders, code compilation of the overview's paths).
    /// </summary>
    /// <param name="state">A real state, so the strip and text paths are exercised too.</param>
    public void WarmUp(OverviewState state)
    {
        if (_phase != Phase.Hidden)
        {
            return;
        }

        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _strip.Update(state, Monitor, Monitor.Bounds.Width / _scale, Monitor.Bounds.Height / _scale);
        _strip.SetExpanded(true);
        WindowStyling.PlaceTopmost(_hwnd, Monitor.Bounds.Offset(-32000 - Monitor.Bounds.Left, -32000 - Monitor.Bounds.Top));
        Show();
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (_phase == Phase.Hidden)
            {
                Hide();
                _strip.Clear();
                _strip.SetExpanded(false);
                Place();
            }
        });
    }

    /// <summary>Closes the window for good (app exit or monitor change).</summary>
    public void Destroy()
    {
        FinishClose();
        if (_rendering)
        {
            CompositionTarget.Rendering -= OnRendering; // A static event would keep this window alive.
            _rendering = false;
        }

        _allowClose = true;
        Close();
    }

    // ------------------------------------------------------------------ drag and drop (driven by the host)

    /// <summary>Tests where a dragged window is and highlights the drop target.</summary>
    /// <param name="x">Pointer x, in screen pixels.</param>
    /// <param name="y">Pointer y, in screen pixels.</param>
    /// <returns>The hit, or <see langword="null"/> if the pointer isn't on this monitor.</returns>
    public DropHit? PointerOver(int x, int y)
    {
        var bounds = Monitor.Bounds;
        if (x < bounds.Left || x >= bounds.Right || y < bounds.Top || y >= bounds.Bottom)
        {
            PointerLeft();
            return null;
        }

        var point = new Point((x - bounds.Left) / _scale, (y - bounds.Top) / _scale);
        double stripBottom = _strip.TargetHeight;
        if (point.Y < stripBottom + NearStrip)
        {
            _strip.SetExpanded(true); // And it stays expanded, as when the pointer reaches it.
        }

        var target = _strip.HitTest(_root.TranslatePoint(point, _strip));
        _strip.Highlight(target);
        stripBottom = _strip.TargetHeight;
        double nearness = Math.Clamp((stripBottom + ShrinkZone - point.Y) / ShrinkZone, 0, 1);
        var card = new Size(_strip.CardSize.Width * _scale, _strip.CardSize.Height * _scale);
        return new DropHit(this, target, target is null && point.Y > stripBottom, nearness, card);
    }

    /// <summary>Clears drag highlights.</summary>
    public void PointerLeft() => _strip.Highlight(null);

    /// <summary>Re-applies every tile, e.g. when a drag starts or ends.</summary>
    public void RefreshTiles()
    {
        foreach (var tile in _tiles.Values)
        {
            TakeOver(tile, resume: true);
            Apply(tile);
        }
    }

    // ------------------------------------------------------------------ keyboard (driven by the host)

    /// <summary>Moves the keyboard selection with an arrow key.</summary>
    /// <param name="key">The arrow key.</param>
    public void MoveSelection(Key key)
    {
        if (_stripFocused)
        {
            int step = key is Key.Left or Key.Up ? -1 : 1;
            _strip.Selected = Math.Max(0, _strip.Selected + step);
            return;
        }

        if (_order.Count == 0)
        {
            return;
        }

        if (_selected is not { } current || !_tiles.TryGetValue(current, out var from))
        {
            Select(_order[^1]);
            return;
        }

        var (dx, dy) = key switch
        {
            Key.Left => (-1.0, 0.0),
            Key.Right => (1.0, 0.0),
            Key.Up => (0.0, -1.0),
            _ => (0.0, 1.0),
        };

        var origin = Center(from.Target);
        var best = _order
            .Where(id => id != current)
            .Select(id => (Id: id, Center: Center(_tiles[id].Target)))
            .Select(c => (c.Id, Along: ((c.Center.X - origin.X) * dx) + ((c.Center.Y - origin.Y) * dy), Across: Math.Abs(((c.Center.X - origin.X) * dy) + ((c.Center.Y - origin.Y) * dx))))
            .Where(c => c.Along > 1)
            .OrderBy(c => c.Along + (2 * c.Across))
            .Select(c => (WindowId?)c.Id)
            .FirstOrDefault();
        if (best is { } next)
        {
            Select(next);
        }
    }

    /// <summary>Acts on the keyboard selection (Enter/Space).</summary>
    public void ActivateSelection()
    {
        if (_stripFocused)
        {
            switch (_strip.SelectedTarget)
            {
                case { IsAdd: true }:
                    _host.Actions.AddDesktop(Monitor.Id);
                    break;
                case { } target:
                    _host.Actions.ShowDesktop(Monitor.Id, target.Desktop, close: true);
                    break;
            }

            return;
        }

        if (_selected is { } window)
        {
            _host.Actions.ChooseWindow(window);
        }
    }

    /// <summary>Moves the keyboard between the strip and the window area (Tab).</summary>
    public void ToggleStripFocus()
    {
        _stripFocused = !_stripFocused;
        if (_stripFocused)
        {
            _strip.SetExpanded(true);
            int shownIndex = _state?.Desktops.ToList().FindIndex(d => d.Id == Monitor.Shown) ?? 0;
            _strip.Selected = Math.Max(0, shownIndex);
            Select(null);
        }
        else
        {
            _strip.Selected = -1;
        }
    }

    /// <summary>Starts renaming the focused desktop (F2).</summary>
    public void RenameFocusedDesktop() => _strip.BeginRename(FocusedDesktop);

    /// <summary>Gets a value indicating whether the keyboard is on the strip (Tab) rather than the windows.</summary>
    public bool IsStripFocused => _stripFocused;

    /// <summary>Gets a value indicating whether a text box has the keyboard (keys go to it).</summary>
    public bool IsEditingText => _strip.IsRenaming;

    /// <inheritdoc/>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true; // Alt+F4 dismisses the overview instead of destroying it.
            _host.Actions.Dismiss();
        }

        base.OnClosing(e);
    }

    // ------------------------------------------------------------------ tiles

    private Tile CreateTile(OverviewWindow window)
    {
        // No blur effects on tiles: they would be re-rendered on every animation frame. A soft offset shape reads
        // as a shadow under the live preview at no cost.
        var shadow = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x38, 0, 0, 0)),
            IsHitTestVisible = false,
        };
        var frame = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = _theme.Surface,
            BorderBrush = _theme.Stroke,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };
        var (caption, captionText, pin) = CreateCaption(window);
        var ring = new Border
        {
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(2),
            BorderBrush = _theme.AccentBrush,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        // One layer per tile, so its frame, shadow, ring and caption hide and fade in together while its preview moves.
        var layer = new Canvas { Children = { shadow, ring, frame, caption } };
        _canvas.Children.Add(layer);

        var tile = new Tile(window, layer, frame, ring, shadow, caption, captionText, pin)
        {
            Thumbnail = CreateThumbnail(window),
        };
        System.Windows.Automation.AutomationProperties.SetName(frame, window.Title);

        frame.MouseEnter += (_, _) => SetHover(tile, true);
        frame.MouseLeave += (_, _) => SetHover(tile, false);
        caption.MouseEnter += (_, _) => UpdatePin(tile);
        caption.MouseLeave += (_, _) => UpdatePin(tile);
        caption.MouseLeftButtonDown += (_, e) => e.Handled = true;
        caption.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true; // Clicking a window's name picks the window, like clicking its preview.
            if (_phase == Phase.Open)
            {
                _host.Actions.ChooseWindow(tile.Model.Id);
            }
        };
        pin.MouseEnter += (_, _) => UpdatePin(tile);
        pin.MouseLeave += (_, _) => UpdatePin(tile);
        pin.MouseLeftButtonDown += (_, e) => e.Handled = true;
        pin.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_phase == Phase.Open)
            {
                _host.Actions.TogglePin(tile.Model.Id, Monitor.Id);
            }
        };
        frame.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (_phase != Phase.Open && _phase != Phase.Opening)
            {
                return;
            }

            _pressed = tile;
            WindowStyling.TryGetCursorPosition(out _pressX, out _pressY);
            frame.CaptureMouse();
        };
        frame.MouseMove += (_, e) =>
        {
            if (_app is not null)
            {
                return; // The app windows view is for picking a window, not rearranging.
            }

            if (_pressed != tile || e.LeftButton != MouseButtonState.Pressed || !WindowStyling.TryGetCursorPosition(out int x, out int y))
            {
                return;
            }

            if (!_dragging && Math.Max(Math.Abs(x - _pressX), Math.Abs(y - _pressY)) > DragThreshold * _scale)
            {
                _dragging = true;
                SetHover(tile, false);
                _host.BeginDrag(tile.Model, ToScreen(tile.Current), _pressX, _pressY);
            }

            if (_dragging)
            {
                _host.DragTo(x, y);
            }
        };
        frame.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            bool wasDragging = _dragging;
            bool wasPressed = _pressed == tile;
            _dragging = false;
            _pressed = null;
            frame.ReleaseMouseCapture();
            if (wasDragging)
            {
                _host.EndDrag(drop: true);
            }
            else if (wasPressed)
            {
                _host.Actions.ChooseWindow(tile.Model.Id);
            }
        };
        frame.LostMouseCapture += (_, _) =>
        {
            if (_dragging)
            {
                _dragging = false;
                _pressed = null;
                _host.EndDrag(drop: false);
            }
        };
        frame.ContextMenu = new ContextMenu();
        frame.ContextMenuOpening += (_, e) =>
        {
            if (_app is not null)
            {
                e.Handled = true;
                return;
            }

            FillWindowMenu(frame.ContextMenu, tile);
        };
        return tile;
    }

    /// <summary>
    /// The caption under a preview: app icon, window title (so windows are easy to tell apart) and a pin button.
    /// Everything that must be clickable sits here, outside the preview, because the live preview is drawn above
    /// the overview's own content.
    /// </summary>
    private (StackPanel Caption, TextBlock Text, Border Pin) CreateCaption(OverviewWindow window)
    {
        var caption = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        if (Images.FromIcon(window.Icon) is { } icon)
        {
            caption.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        }

        var text = new TextBlock
        {
            Text = window.Title,
            FontFamily = Theme.TextFont,
            FontSize = 12,
            Foreground = _theme.Text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        caption.Children.Add(text);

        var pin = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Child = new TextBlock { FontFamily = Theme.IconFont, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        caption.Children.Add(pin);
        return (caption, text, pin);
    }

    /// <summary>
    /// Shows the pin button's state: a filled accent pin on pinned windows (always visible), an outline pin on the
    /// others while the window is pointed at.
    /// </summary>
    private void UpdatePin(Tile tile)
    {
        var pin = tile.PinButton;
        var glyph = (TextBlock)pin.Child;
        bool pinned = tile.Model.IsPinned;
        bool pointed = tile.Hover || tile.Caption.IsMouseOver;
        string label = pinned ? _text.Unpin : _text.Pin;
        pin.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(pin, label);
        glyph.Text = pinned ? "\uE842" : "\uE718";
        glyph.Foreground = pinned ? _theme.OnAccent : _theme.Text;
        pin.Background = pinned ? _theme.AccentBrush : pin.IsMouseOver ? _theme.SurfaceHover : _theme.Surface;
        pin.BorderBrush = _theme.Stroke;
        pin.BorderThickness = new Thickness(pinned ? 0 : 1);

        bool visible = _app is null && (pinned || pointed);
        pin.IsHitTestVisible = visible;
        pin.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 120 : 0)));
    }

    /// <summary>Pins or unpins the window selected with the keyboard (P).</summary>
    public void TogglePinSelection()
    {
        if (!_stripFocused && _selected is { } window)
        {
            _host.Actions.TogglePin(window, Monitor.Id);
        }
    }

    private void FillWindowMenu(ContextMenu menu, Tile tile)
    {
        menu.Items.Clear();
        if (_state is null)
        {
            return;
        }

        var moveTo = new MenuItem { Header = _text.MoveTo };
        foreach (var desktop in _state.Desktops)
        {
            var item = new MenuItem { Header = desktop.Title, IsEnabled = tile.Model.IsPinned || desktop.Id != Monitor.Shown };
            item.Click += (_, _) => _host.Actions.MoveWindow(tile.Model.Id, desktop.Id);
            moveTo.Items.Add(item);
        }

        var pin = new MenuItem { Header = tile.Model.IsPinned ? _text.Unpin : _text.Pin };
        pin.Click += (_, _) => _host.Actions.TogglePin(tile.Model.Id, Monitor.Id);
        menu.Items.Add(moveTo);
        menu.Items.Add(pin);
    }

    /// <summary>
    /// A live preview, for any window on screen, minimized ones included: the compositor keeps a minimized window's
    /// last content, as taskbar previews show. Windows on hidden desktops have none.
    /// </summary>
    private WindowThumbnail? CreateThumbnail(OverviewWindow window)
    {
        if (!window.IsOnScreen || WindowThumbnail.TryCreate(_hwnd, window.Handle) is not { } thumbnail)
        {
            return null;
        }

        if (!thumbnail.HasContent)
        {
            thumbnail.Dispose();
            return null;
        }

        return thumbnail;
    }

    /// <summary>Makes a tile's preview match its window's state now; it may gain, lose or reshape its live preview.</summary>
    private void RefreshThumbnail(Tile tile)
    {
        TakeOver(tile, resume: true);
        if (tile.Thumbnail is { } thumbnail && tile.Model.IsOnScreen)
        {
            thumbnail.Refresh();
            if (thumbnail.HasContent)
            {
                return;
            }
        }

        tile.Thumbnail?.Dispose();
        tile.Thumbnail = CreateThumbnail(tile.Model);
    }

    private void UpdatePlaceholder(Tile tile)
    {
        bool needsPlaceholder = tile.Thumbnail is null;
        if (needsPlaceholder && tile.Model.Image is { } picture)
        {
            // No live preview (hidden desktop, or a minimized window the compositor kept nothing of): show the last
            // picture Deskswap took of the window.
            // Fill and crop (from the top left, where title bars are), never stretch: the window may have changed shape
            // since its picture was taken.
            var image = new Image { Source = Images.ToBitmap(picture), Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            tile.Frame.Child = new Border { Child = image, CornerRadius = new CornerRadius(7), ClipToBounds = true };
            tile.Frame.Opacity = 1;
            return;
        }

        tile.Frame.Opacity = tile.Model.IsMinimized ? 0.75 : 1;
        if (!needsPlaceholder)
        {
            tile.Frame.Child = null;
            return;
        }

        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (Images.FromIcon(tile.Model.Icon) is { } icon)
        {
            content.Children.Add(new Image { Source = icon, Width = 40, Height = 40 });
        }

        content.Children.Add(new TextBlock
        {
            Text = tile.Model.Title,
            FontFamily = Theme.TextFont,
            FontSize = 12,
            Foreground = _theme.Text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(8, 6, 8, 0),
        });
        tile.Frame.Child = content;
    }

    private void RemoveTile(WindowId id)
    {
        if (!_tiles.Remove(id, out var tile))
        {
            return;
        }

        if (tile.Thumbnail is { } thumbnail)
        {
            _host.Animator.Stop(thumbnail, out _, out _); // Before releasing it: the motion thread must not touch it after.
            thumbnail.Dispose();
        }

        _canvas.Children.Remove(tile.Layer);
        _order.Remove(id);
    }

    private void Select(WindowId? window)
    {
        _selected = window;
        foreach (var (id, tile) in _tiles)
        {
            tile.Ring.Visibility = id == window ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SetHover(Tile tile, bool hover)
    {
        if (tile.Hover == hover || (hover && (_host.IsDragging || _phase != Phase.Open)))
        {
            return;
        }

        tile.Hover = hover;
        UpdatePin(tile);
        Animate(tile, hover ? Inflate(tile.Target, 0.015) : tile.Target, HoverMs, small: true);
    }

    // ------------------------------------------------------------------ layout and animation

    private void Relayout(bool animate, double durationMs = MoveMs)
    {
        if (_phase == Phase.Hidden || _phase == Phase.Closing)
        {
            return;
        }

        if (_app is not null)
        {
            RelayoutApp(animate, durationMs);
            return;
        }

        if (_state is null)
        {
            return;
        }

        var bounds = Monitor.Bounds;
        double s = _scale;
        double stripPx = _strip.TargetHeight * s;
        var area = new ScreenRect((int)(SideMargin * s), (int)(stripPx + (AreaGap * s)), (int)(bounds.Width - (SideMargin * s)), (int)(bounds.Height - (BottomMargin * s)));
        var tiles = _order.Select(id => _tiles[id]).ToList();
        var targets = ExposeLayout.Arrange([.. tiles.Select(t => ToScreenRect(t.Real))], area, (int)(TileGap * s), (int)(CaptionHeight * s));
        for (int i = 0; i < tiles.Count; i++)
        {
            PlaceTile(tiles[i], targets[i], animate ? durationMs : 0);
        }

        _empty.Visibility = tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Margin = new Thickness(0, stripPx / s, 0, 0);
        EnsureRendering();
    }

    /// <summary>Moves a tile to its place in the layout.</summary>
    private void PlaceTile(Tile tile, ScreenRect target, double durationMs)
    {
        tile.Target = ToRect(target);
        if (!tile.Placed && !IsOnThisScreen(tile))
        {
            // Not visible on this monitor (minimized, hidden, or on another monitor): grow in where it belongs.
            tile.Current = Inflate(tile.Target, -0.08);
        }

        tile.Placed = true;
        Animate(tile, tile.Hover ? Inflate(tile.Target, 0.015) : tile.Target, durationMs);
    }

    private bool IsOnThisScreen(Tile tile) => !tile.Model.IsMinimized && tile.Model.IsOnScreen && tile.Model.Monitor == Monitor.Id;

    /// <summary>
    /// The app windows view: one band per desktop, top to bottom in desktop order, each with a header and that
    /// desktop's windows of the app laid out like the overview.
    /// </summary>
    private void RelayoutApp(bool animate, double durationMs)
    {
        var groups = _app!.Groups.Where(g => g.Windows.Count > 0).ToList();
        var bounds = Monitor.Bounds;
        double s = _scale;
        const double headerTop = 76;
        const double groupHeader = 30;
        double top = headerTop + AreaGap;
        double bottom = (bounds.Height / s) - BottomMargin;
        double band = groups.Count == 0 ? 0 : (bottom - top) / groups.Count;

        for (int g = 0; g < groups.Count; g++)
        {
            double y = top + (g * band);
            if (_groupHeaders.FirstOrDefault(h => h.Desktop == groups[g].Desktop).Header is { } header)
            {
                header.Width = bounds.Width / s;
                Canvas.SetLeft(header, 0);
                Canvas.SetTop(header, y);
            }

            var area = new ScreenRect(
                (int)(SideMargin * s),
                (int)((y + groupHeader) * s),
                (int)(bounds.Width - (SideMargin * s)),
                (int)((y + band - (AreaGap / 2)) * s));
            var tiles = groups[g].Windows.Where(w => _tiles.ContainsKey(w.Id)).Select(w => _tiles[w.Id]).ToList();
            var targets = ExposeLayout.Arrange([.. tiles.Select(t => ToScreenRect(t.Real))], area, (int)(TileGap * s), (int)(CaptionHeight * s));
            for (int i = 0; i < tiles.Count; i++)
            {
                PlaceTile(tiles[i], targets[i], animate ? durationMs : 0);
            }
        }

        _empty.Visibility = Visibility.Collapsed;
        EnsureRendering();
    }

    /// <summary>
    /// Moves a tile. Opening, closing and re-layout move only the live preview, on the compositor's clock
    /// (<see cref="ThumbnailAnimator"/>); its frame, shadow and caption wait at the destination and fade in when it
    /// arrives, so nothing drawn by WPF has to keep up with it. Small motions (hover), and tiles without a live
    /// preview, move here on the UI thread.
    /// </summary>
    private void Animate(Tile tile, Rect to, double durationMs, bool easeIn = false, bool small = false)
    {
        if ((tile.Moving || tile.Animating) && tile.To == to && tile.EaseIn == easeIn)
        {
            return; // Already on its way there (e.g. a refresh that changed nothing for this tile).
        }

        TakeOver(tile, resume: false);
        double duration = SystemParameters.ClientAreaAnimation ? durationMs : 0;
        tile.From = tile.Current;
        tile.To = to;
        tile.EaseIn = easeIn;
        tile.Duration = duration;
        if (!small && duration > 0 && tile.Thumbnail is { } thumbnail && !_host.IsDraggingWindow(tile.Model.Id) && tile.Current != to)
        {
            tile.Animating = false;
            tile.Moving = true;
            int motion = ++tile.Motion;
            HideDecorations(tile);
            ApplyDecorations(tile, to);
            var from = tile.Current;
            void Start()
            {
                if (tile.Moving && tile.Motion == motion)
                {
                    _host.Animator.Start(thumbnail, from, to, duration, easeIn, PreviewOpacity(tile), () => OnArrived(tile, motion));
                }
            }

            if (_phase == Phase.Opening && _phaseStart == 0)
            {
                // The window hasn't been drawn yet: show the preview where it starts, and move it from the first frame.
                thumbnail.Update(ToScreenRect(from), PreviewOpacity(tile));
                _motionsAtFirstFrame.Add(Start);
                EnsureRendering();
            }
            else
            {
                Start();
            }

            return;
        }

        tile.Start = 0; // Set on the next frame drawn.
        tile.Animating = true;
        EnsureRendering();
    }

    /// <summary>
    /// Takes a tile's preview back from the motion thread, where it is now, so the UI thread can update it.
    /// </summary>
    /// <param name="tile">The tile.</param>
    /// <param name="resume">Finish the rest of the motion here; otherwise the caller starts a new one.</param>
    private void TakeOver(Tile tile, bool resume)
    {
        if (!tile.Moving)
        {
            return;
        }

        tile.Moving = false;
        tile.Motion++;
        if (tile.Thumbnail is not { } thumbnail || !_host.Animator.Stop(thumbnail, out var current, out double remaining))
        {
            current = tile.Current; // Not started yet (waiting for the first frame).
            remaining = tile.Duration;
        }
        else
        {
            _handedBack++;
        }

        tile.Current = current;
        if (resume)
        {
            tile.From = current;
            tile.Start = 0;
            tile.Duration = remaining;
            tile.Animating = true;
            EnsureRendering();
        }

        ShowDecorations(tile, fade: false);
    }

    /// <summary>Called on the UI thread when a tile's preview has arrived.</summary>
    private void OnArrived(Tile tile, int motion)
    {
        if (!tile.Moving || tile.Motion != motion || !_tiles.TryGetValue(tile.Model.Id, out var current) || current != tile)
        {
            return; // Stopped, restarted or removed since.
        }

        tile.Moving = false;
        tile.Current = tile.To;
        Apply(tile);
        if (_phase != Phase.Closing)
        {
            ShowDecorations(tile, fade: true); // Closing: the real window takes its place; nothing more to show.
        }
    }

    private static void HideDecorations(Tile tile)
    {
        tile.Layer.BeginAnimation(OpacityProperty, null);
        tile.Layer.Opacity = 0;
    }

    private static void ShowDecorations(Tile tile, bool fade)
    {
        if (fade && SystemParameters.ClientAreaAnimation)
        {
            tile.Layer.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(DecorationsFadeMs)));
            return;
        }

        tile.Layer.BeginAnimation(OpacityProperty, null);
        tile.Layer.Opacity = 1;
    }

    private static byte PreviewOpacity(Tile tile) => tile.Model.IsMinimized ? MinimizedOpacity : (byte)255;

    private void EnsureRendering()
    {
        if (!_rendering)
        {
            _rendering = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        _phaseTiming?.Frame(now);
        if (_motionsAtFirstFrame.Count > 0)
        {
            var starts = _motionsAtFirstFrame.ToArray();
            _motionsAtFirstFrame.Clear();
            foreach (var start in starts)
            {
                start();
            }
        }

        bool busy = false;
        foreach (var tile in _tiles.Values)
        {
            if (!tile.Animating)
            {
                continue;
            }

            if (tile.Start == 0)
            {
                tile.Start = now;
            }

            double t = tile.Duration <= 0 ? 1 : Math.Clamp(Stopwatch.GetElapsedTime(tile.Start, now).TotalMilliseconds / tile.Duration, 0, 1);
            double eased = tile.EaseIn ? t * t * t : 1 - Math.Pow(1 - t, 3);
            tile.Current = Lerp(tile.From, tile.To, eased);
            Apply(tile);
            if (t >= 1)
            {
                tile.Animating = false;
            }
            else
            {
                busy = true;
            }
        }

        busy |= AnimatePhase(now);
        _phaseTiming?.Work(Stopwatch.GetTimestamp() - now);
        if (!busy)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }
    }

    /// <summary>Fades the blurred backdrop, strip and captions in on open and out on close.</summary>
    private bool AnimatePhase(long now)
    {
        if (_phase is not (Phase.Opening or Phase.Closing))
        {
            return false;
        }

        if (_phaseStart == 0)
        {
            _phaseStart = now;
        }

        double duration = _phase == Phase.Opening ? OpenMs : CloseMs;
        double t = SystemParameters.ClientAreaAnimation ? Math.Clamp(Stopwatch.GetElapsedTime(_phaseStart, now).TotalMilliseconds / duration, 0, 1) : 1;
        double amount = _phase == Phase.Opening ? 1 - Math.Pow(1 - t, 3) : 1 - (t * t * t);
        _blurred.Opacity = amount;
        _tint.Opacity = _tintOpacity * amount;
        _screen.Opacity = 1 - amount; // The taskbar and anything Deskswap doesn't manage fade away.
        _strip.Opacity = amount;
        _stripOffset.Y = -12 * (1 - amount);
        foreach (var tile in _tiles.Values)
        {
            tile.Caption.Opacity = amount;
        }

        if (t < 1)
        {
            return true;
        }

        // Closing ends when the previews are back in place (they run on their own clock), or after a grace period.
        if (_phase == Phase.Closing && _tiles.Values.Any(tile => tile.Moving)
            && Stopwatch.GetElapsedTime(_phaseStart, now).TotalMilliseconds < duration + CloseGraceMs)
        {
            return true;
        }

        ReportPhaseTiming();
        if (_phase == Phase.Opening)
        {
            _phase = Phase.Open;
        }
        else
        {
            FinishClose();
        }

        return false;
    }

    private void FinishClose()
    {
        _phase = Phase.Hidden;
        _motionsAtFirstFrame.Clear();
        foreach (var id in _tiles.Keys.ToList())
        {
            RemoveTile(id);
        }

        ClearGroupHeaders();
        _app = null;
        _state = null;
        _strip.Clear(); // Cards hold desktop pictures; nothing is kept while the overview is closed.
        _appHeader.Children.Clear();
        _wallpaper.Child = null;
        _screen.Source = null;
        _screen.Clip = null;
        _blurred.Source = null;
        _blurred.Opacity = 0;
        _tint.Opacity = 0;
        Hide();
        _host.OnWindowHidden();
    }

    private void Apply(Tile tile)
    {
        ApplyDecorations(tile, tile.Current);
        if (!tile.Moving)
        {
            tile.Thumbnail?.Update(ToScreenRect(tile.Current), PreviewOpacity(tile), visible: !_host.IsDraggingWindow(tile.Model.Id));
        }
    }

    /// <summary>Places a tile's frame, ring, shadow and caption (everything WPF draws) at a rectangle.</summary>
    private void ApplyDecorations(Tile tile, Rect r)
    {
        double s = _scale;
        bool dragged = _host.IsDraggingWindow(tile.Model.Id);
        Canvas.SetLeft(tile.Frame, r.X / s);
        Canvas.SetTop(tile.Frame, r.Y / s);
        tile.Frame.Width = Math.Max(1, r.Width / s);
        tile.Frame.Height = Math.Max(1, r.Height / s);
        // Transparent, not hidden: a hidden element loses the mouse capture that keeps the drag going.
        tile.Frame.Opacity = dragged ? 0 : tile.Model.IsMinimized ? 0.75 : 1;

        Canvas.SetLeft(tile.Ring, (r.X / s) - 5);
        Canvas.SetTop(tile.Ring, (r.Y / s) - 5);
        tile.Ring.Width = (r.Width / s) + 10;
        tile.Ring.Height = (r.Height / s) + 10;

        Canvas.SetLeft(tile.Shadow, (r.X / s) - 2);
        Canvas.SetTop(tile.Shadow, (r.Y / s) + 2);
        tile.Shadow.Width = (r.Width / s) + 4;
        tile.Shadow.Height = (r.Height / s) + 4;
        tile.Shadow.Opacity = dragged ? 0 : 1;

        // The caption sits centered under the preview, never wider than it.
        double width = r.Width / s;
        double maxText = Math.Max(24, Math.Round(width - (tile.Caption.Children.Count > 2 ? 22 : 0) - 30));
        if (tile.CaptionText.MaxWidth != maxText || tile.Caption.DesiredSize.Width == 0)
        {
            tile.CaptionText.MaxWidth = maxText;
            tile.Caption.Measure(new Size(double.PositiveInfinity, CaptionHeight));
        }
        Canvas.SetLeft(tile.Caption, (r.X / s) + ((width - tile.Caption.DesiredSize.Width) / 2));
        Canvas.SetTop(tile.Caption, (r.Bottom / s) + 8);
        tile.Caption.Visibility = dragged ? Visibility.Hidden : Visibility.Visible;
    }

    private void ReportPhaseTiming()
    {
        if (_phaseTiming is { Frames: > 0 } timing)
        {
            string what = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Overview {(_phase == Phase.Opening ? "opening" : "closing")} on {Monitor.Label}, UI thread (render tier {RenderCapability.Tier >> 16}, {_tiles.Count} windows, {_tiles.Values.Count(t => t.Thumbnail is not null)} live, {_handedBack} motions handed back)");
            _host.Report(timing.Report(what));
        }

        _phaseTiming = null;
        _handedBack = 0;
    }

    private void Place() => WindowStyling.PlaceTopmost(_hwnd, Monitor.Bounds);

    /// <summary>
    /// Sets what is behind the previews. As on macOS: the desktop picture, softly blurred and dimmed, so windows
    /// shrink away from it without ghosts of themselves behind. The screen image is kept only outside the work area
    /// (the taskbar), fading out as the overview opens, so the first frame matches the screen exactly.
    /// </summary>
    private void SetBackdrop(OverviewMonitor monitor, PixelImage? screen)
    {
        var wallpaperImage = Images.LoadWallpaper(monitor.Wallpaper);
        _wallpaper.Child = monitor.Wallpaper is null && screen is not null ? null : Images.WallpaperView(monitor.Wallpaper, _theme.Backdrop);

        var screenBitmap = screen is null ? null : Images.ToBitmap(screen);
        if (monitor.Wallpaper is null)
        {
            // Unknown wallpaper: fall back to the blurred screen image.
            _screen.Source = screenBitmap;
            _screen.Clip = null;
            _blurred.Source = screen is null ? null : Images.Blurred(screen);
            _tintOpacity = _theme.TintOpacity;
        }
        else
        {
            _screen.Source = screenBitmap;
            var bounds = monitor.Bounds;
            var work = monitor.WorkArea.Offset(-bounds.Left, -bounds.Top);
            double s = _scale;
            var outside = new GeometryGroup { FillRule = FillRule.EvenOdd };
            outside.Children.Add(new RectangleGeometry(new Rect(0, 0, bounds.Width / s, bounds.Height / s)));
            outside.Children.Add(new RectangleGeometry(new Rect(work.Left / s, work.Top / s, work.Width / s, work.Height / s)));
            _screen.Clip = outside;
            _blurred.Source = wallpaperImage is null ? null : Images.Blurred(wallpaperImage);
            _tintOpacity = _theme.IsHighContrast ? 1 : _theme.TintOpacity * 0.6;
        }

        _blurred.Opacity = 0;
        _screen.Opacity = 1;
    }

    private ScreenRect ToScreen(Rect r) => ToScreenRect(r).Offset(Monitor.Bounds.Left, Monitor.Bounds.Top);

    private static Rect ToRect(ScreenRect r) => new(r.Left, r.Top, r.Width, r.Height);

    private static ScreenRect ToScreenRect(Rect r) =>
        ScreenRect.FromSize((int)Math.Round(r.X), (int)Math.Round(r.Y), Math.Max(1, (int)Math.Round(r.Width)), Math.Max(1, (int)Math.Round(r.Height)));

    private static Point Center(Rect r) => new(r.X + (r.Width / 2), r.Y + (r.Height / 2));

    private static Rect Inflate(Rect r, double fraction)
    {
        double dx = r.Width * fraction;
        double dy = r.Height * fraction;
        return new Rect(r.X - dx, r.Y - dy, r.Width + (2 * dx), r.Height + (2 * dy));
    }

    private static Rect Lerp(Rect a, Rect b, double t) => new(
        a.X + ((b.X - a.X) * t),
        a.Y + ((b.Y - a.Y) * t),
        Math.Max(1, a.Width + ((b.Width - a.Width) * t)),
        Math.Max(1, a.Height + ((b.Height - a.Height) * t)));

    /// <summary>A window's preview: input frame and shadow, selection ring, live thumbnail and animation state.</summary>
    private sealed class Tile(OverviewWindow model, Canvas layer, Border frame, Border ring, Border shadow, StackPanel caption, TextBlock captionText, Border pinButton)
    {
        public OverviewWindow Model { get; set; } = model;

        /// <summary>Gets the layer holding everything WPF draws for the tile.</summary>
        public Canvas Layer { get; } = layer;

        /// <summary>Gets or sets a value indicating whether the motion thread is moving its preview.</summary>
        public bool Moving { get; set; }

        /// <summary>Gets or sets the number of the latest motion, so a stale arrival is ignored.</summary>
        public int Motion { get; set; }

        public Border Frame { get; } = frame;

        public Border Ring { get; } = ring;

        public WindowThumbnail? Thumbnail { get; set; }

        public Border Shadow { get; } = shadow;

        public StackPanel Caption { get; } = caption;

        public TextBlock CaptionText { get; } = captionText;

        public Border PinButton { get; } = pinButton;

        /// <summary>Gets or sets the window's real place, in monitor-relative pixels.</summary>
        public Rect Real { get; set; }

        /// <summary>Gets or sets the tile's place in the layout.</summary>
        public Rect Target { get; set; }

        /// <summary>Gets or sets where the tile is drawn now.</summary>
        public Rect Current { get; set; }

        public Rect From { get; set; }

        public Rect To { get; set; }

        public long Start { get; set; }

        public double Duration { get; set; }

        public bool EaseIn { get; set; }

        public bool Animating { get; set; }

        public bool Hover { get; set; }

        /// <summary>Gets or sets a value indicating whether the tile has been laid out once.</summary>
        public bool Placed { get; set; }
    }
}
