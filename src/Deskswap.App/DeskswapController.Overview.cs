using Deskswap.App.Resources;
using Deskswap.Core;
using Deskswap.Platform;
using Deskswap.Ui.Overview;

namespace Deskswap.App;

/// <summary>The overview (milestone 3, overview-ux.md): opening it, what it shows, and what it asks for.</summary>
internal sealed partial class DeskswapController : IOverviewActions
{
    /// <summary>Width of the image behind each monitor's overview; it is blurred, so it needn't be sharp.</summary>
    private const int BackdropWidth = 960;

    /// <summary>Largest width kept for a window's picture (desktop cards and the app windows view).</summary>
    private const int WindowImageWidth = 360;

    /// <summary>How long after the overview closes the memory it used is handed back to Windows.</summary>
    private const int TrimDelayMs = 2000;

    /// <summary>Width of the monitor image windows are cut from when their desktop is hidden.</summary>
    private const int SnapshotWidth = 640;

    /// <summary>How often the window in use gets a fresh picture, for when it is minimized (overview-ux.md §9).</summary>
    private const int ActivePictureMs = 3000;

    /// <summary>
    /// The last picture of each window, cut from a monitor image while it was on screen. Desktop cards draw their
    /// windows from these (overview-ux.md §9: memory only, never on disk).
    /// </summary>
    private readonly Dictionary<WindowId, PixelImage> _windowImages = [];
    private IReadOnlyDictionary<MonitorId, Wallpaper> _wallpapers = new Dictionary<MonitorId, Wallpaper>();
    private Dictionary<MonitorId, DesktopId> _lastShown = [];
    private readonly Dictionary<(int ProcessId, long StartTicks), string?> _appPaths = [];
    private readonly System.Windows.Forms.Timer _trimTimer = new() { Interval = TrimDelayMs };
    private readonly System.Windows.Forms.Timer _pictureTimer = new() { Interval = ActivePictureMs };
    private DeskswapSettings _settings = DeskswapSettings.Default;
    private (WindowId Window, ScreenRect Frame, uint Input)? _lastActivePicture;

    /// <summary>
    /// Monitors whose window pictures were taken as the overview opened. Until it has closed, nothing new was on
    /// screen there, so hiding a desktop needn't (and can't, with the overview in the way) take new ones.
    /// </summary>
    private readonly HashSet<MonitorId> _picturedByOverview = [];
    private OverviewHost? _overview;
    private AppView? _appView;
    private nint _foregroundBeforeOverview;

    /// <summary>Raised when the overview opens or finishes closing (the keyboard hook binds Esc while it is open).</summary>
    public event EventHandler? OverviewOpenChanged;

    /// <summary>Gets or sets the user's preferences.</summary>
    public DeskswapSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value ?? throw new ArgumentNullException(nameof(value));
            UpdatePictureTimer();
        }
    }

    /// <summary>Gets a value indicating whether the overview is open.</summary>
    public bool IsOverviewOpen => _overview?.IsOpen == true;

    private OverviewHost Overview => _overview ??= CreateOverview();

    private OverviewHost CreateOverview()
    {
        var host = new OverviewHost(this, new OverviewText(
        Strings.OverviewNoWindows,
        Strings.OverviewNewDesktop,
        Strings.OverviewRename,
        Strings.OverviewRemove,
        Strings.OverviewCannotRemove,
        Strings.OverviewMoveTo,
        Strings.OverviewPin,
        Strings.OverviewUnpin,
        Strings.OverviewShownOn));
        host.Failed += (_, ex) => FailSafe(ex);
        host.Measured += (_, line) => Log.Info(line);
        var ui = SynchronizationContext.Current;
        host.Closed += (_, _) =>
        {
            OverviewOpenChanged?.Invoke(this, EventArgs.Empty);

            // After the operation that closed it (e.g. a switch picked in the overview), which still relies on them.
            ui?.Post(
                _ =>
                {
                    if (_overview?.IsOnScreen != true)
                    {
                        _picturedByOverview.Clear();
                    }
                },
                null);
            _trimTimer.Stop();
            _trimTimer.Start();
        };
        _trimTimer.Tick += (_, _) =>
        {
            _trimTimer.Stop();
            if (!IsOverviewOpen)
            {
                MemoryTrim.Run();
            }
        };
        return host;
    }

    /// <summary>Creates the overview windows ahead of time so the first opening is quick.</summary>
    public void PrewarmOverview()
    {
        if (IsPaused)
        {
            return;
        }

        try
        {
            _wallpapers = Wallpapers.Read(_layout);
            Overview.Prewarm(BuildMonitors(), BuildOverviewState());
        }
#pragma warning disable CA1031 // Optional warm-up: on any failure the overview is simply created on first use.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Error("Could not prepare the overview ahead of time.", ex);
        }
    }

    /// <summary>Opens the overview on every monitor (<c>Win+Tab</c>, tray click), or closes it.</summary>
    public void ToggleOverview()
    {
        if (IsOverviewOpen && !Overview.IsAppView)
        {
            Dismiss();
            return;
        }

        bool wasShown = _overview?.IsOnScreen == true;
        if (wasShown)
        {
            // Win+Tab from the app windows view switches to the overview; a closing overview is finished at once.
            Overview.Close(animate: false);
        }

        if (IsPaused || _displaysSettling)
        {
            return;
        }

        Guard(() =>
        {
            Reconcile(ReconcileMode.FollowActivation);
            if (_layout.MonitorUnderCursor() is not { } focus)
            {
                return;
            }

            if (_appView is null)
            {
                _foregroundBeforeOverview = WindowInspector.ForegroundWindow;
            }

            _appView = null;
            _wallpapers = Wallpapers.Read(_layout);

            // Deskswap's windows are visible to screenshots and recordings, so its own snapshot waits until they're gone.
            if (_indicators.HideAll() | wasShown)
            {
                ScreenCapture.WaitForCompositor();
            }

            var backdrops = new Dictionary<MonitorId, PixelImage>();
            foreach (var monitor in _layout.Monitors)
            {
                // One capture per monitor: the taskbar's first frame, and fresh pictures of the windows on screen
                // for the desktop cards (hidden desktops keep the pictures taken when they were hidden).
                if (ScreenCapture.Capture(monitor.Bounds, BackdropWidth) is { } backdrop)
                {
                    backdrops[monitor.Id] = backdrop;
                    RememberWindowImages(backdrop, monitor, WindowsShownOn(monitor.Id));
                    _picturedByOverview.Add(monitor.Id);
                }
            }

            Overview.Open(BuildOverviewState(), backdrops, focus.Id);
            OverviewOpenChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    // ------------------------------------------------------------------ IOverviewActions

    /// <inheritdoc/>
    public void ChooseWindow(WindowId window)
    {
        Guard(() =>
        {
            nint handle = (nint)window.Handle;

            // In the app windows view a window on screen is just focused, on whichever monitor it is. A window on a
            // hidden desktop brings that desktop back on the monitor that last showed it (its windows are there).
            if (Overview.IsAppView && _appView is { } view && _pool.TryGetDesktopOf(window, out var desktop)
                && _pool.GetMonitorShowing(desktop) is null)
            {
                var last = _pool.GetDesktops().First(d => d.Id == desktop).LastMonitor;
                var target = last is { } monitor && _layout.TryGet(monitor, out _) ? monitor : view.Monitor;
                Overview.Close(animate: false);
                Apply(_pool.GoTo(target, _pool.GetNumber(desktop)));
                if (WindowInspector.IsMinimized(handle))
                {
                    WindowOperations.Restore(handle);
                }

                WindowOperations.Focus(handle);
                return;
            }

            if (WindowInspector.IsMinimized(handle))
            {
                WindowOperations.Restore(handle);
            }

            WindowOperations.Focus(handle);
            Overview.Close(animate: true);
        });
    }

    /// <inheritdoc/>
    public void ShowDesktop(MonitorId monitor, DesktopId desktop, bool close)
    {
        if (close)
        {
            // Close first so the switch is seen directly, and the desktop's top window gets focus.
            Overview.Close(animate: false);
        }

        RunPoolOperation(() => _pool.GoTo(monitor, _pool.GetNumber(desktop)));
    }

    /// <inheritdoc/>
    public void MoveWindow(WindowId window, DesktopId desktop) => RunPoolOperation(() => _pool.MoveWindowToDesktop(window, desktop));

    /// <inheritdoc/>
    public void MoveDesktop(DesktopId desktop, int number) =>
        RunPoolOperation(() =>
        {
            _pool.MoveDesktop(desktop, number);
            return PoolChanges.None;
        });

    /// <inheritdoc/>
    public void MoveWindowToNewDesktop(WindowId window, MonitorId monitor) =>
        RunPoolOperation(() => _pool.MoveWindowToNewDesktop(window, monitor));

    /// <inheritdoc/>
    public void AddDesktop(MonitorId monitor) => RunPoolOperation(() => _pool.AddDesktop(switchTarget: null));

    /// <inheritdoc/>
    public void RemoveDesktop(DesktopId desktop, MonitorId monitor) =>
        RunPoolOperation(() => _pool.RemoveDesktop(_pool.GetNumber(desktop), monitor));

    /// <inheritdoc/>
    public void RenameDesktop(DesktopId desktop, string? name) =>
        RunPoolOperation(() =>
        {
            _pool.Rename(desktop, name);
            return PoolChanges.None;
        });

    /// <inheritdoc/>
    public void TogglePin(WindowId window, MonitorId monitor) => RunPoolOperation(() => _pool.TogglePin(window, monitor));

    /// <inheritdoc/>
    public void Dismiss()
    {
        Guard(() =>
        {
            // Give the keyboard back to the window that had it, while the overview can still pass it on.
            nint previous = _foregroundBeforeOverview;
            if (previous != 0 && WindowInspector.IsVisible(previous) && !WindowInspector.IsMinimized(previous))
            {
                WindowOperations.Focus(previous);
            }

            Overview.Close(animate: true);
        });
    }

    // ------------------------------------------------------------------ internals

    private void RunPoolOperation(Func<PoolChanges> operation)
    {
        if (IsPaused || _displaysSettling)
        {
            return;
        }

        Guard(() =>
        {
            var changes = operation();
            if (changes == PoolChanges.None)
            {
                RefreshOverview();
            }
            else
            {
                Apply(changes);
            }
        });
    }

    /// <summary>Shows the current pool in the overview, if it is open.</summary>
    private void RefreshOverview()
    {
        if (!IsOverviewOpen)
        {
            return;
        }

        if (Overview.IsAppView)
        {
            if (BuildAppWindowsState() is { } app)
            {
                Overview.UpdateAppWindows(app);
            }
        }
        else
        {
            Overview.Update(BuildOverviewState());
        }
    }

    // ------------------------------------------------------------------ app windows view (overview-ux.md §4)

    /// <summary>
    /// Shows every window of the focused app on every desktop (<c>Win+Ctrl+↓</c>), on the focused window's monitor,
    /// or closes that view. From the overview, it switches to this view.
    /// </summary>
    public void ToggleAppWindows()
    {
        if (IsOverviewOpen && Overview.IsAppView)
        {
            Dismiss();
            return;
        }

        if (IsPaused || _displaysSettling)
        {
            return;
        }

        Guard(() =>
        {
            if (!IsOverviewOpen)
            {
                Reconcile(ReconcileMode.FollowActivation);
                _foregroundBeforeOverview = WindowInspector.ForegroundWindow;
            }

            // The app of the window you were using (from the overview: the one in use before it opened).
            nint focused = _foregroundBeforeOverview;
            if (!WindowInspector.TryGetId(focused, out var id) || _layout.MonitorOf(focused) is not { } monitor
                || AppPathOf(id) is not { } app)
            {
                return;
            }

            _appView = new AppView(app, monitor.Id, AppNameOf(app), WindowInspector.GetIcon(focused));
            if (BuildAppWindowsState() is null)
            {
                _appView = null;
                return;
            }

            // From the overview: it goes before the snapshot, which must show the screen without it.
            bool wasShown = _overview?.IsOnScreen == true;
            if (wasShown)
            {
                Overview.Close(animate: false);
            }

            if (_indicators.HideAll() | wasShown)
            {
                ScreenCapture.WaitForCompositor();
            }

            _wallpapers = Wallpapers.Read(_layout);
            var backdrop = ScreenCapture.Capture(monitor.Bounds, BackdropWidth);
            if (backdrop is not null)
            {
                RememberWindowImages(backdrop, monitor, WindowsShownOn(monitor.Id));
                _picturedByOverview.Add(monitor.Id);
            }

            if (BuildAppWindowsState() is not { } state)
            {
                _appView = null;
                return;
            }

            Overview.OpenAppWindows(state, backdrop);
            OverviewOpenChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private AppWindowsState? BuildAppWindowsState()
    {
        if (_appView is not { } view || !_layout.TryGet(view.Monitor, out var monitor))
        {
            return null;
        }

        var monitors = BuildMonitors();
        var groups = new List<AppWindowGroup>();
        foreach (var desktop in _pool.GetDesktops())
        {
            var pinned = desktop.ShownOn is { } on ? _pool.PinnedWindows.Where(w => _layout.MonitorOf((nint)w.Handle)?.Id == on) : [];
            var windows = new List<OverviewWindow>();
            foreach (var id in WindowOperations.TopMostFirst(desktop.Windows.Concat(pinned).Where(w => AppPathOf(w) == view.App)).Reverse())
            {
                nint handle = (nint)id.Handle;
                if (!WindowInspector.TryGetLayoutBounds(handle, _layout.Primary, out var bounds))
                {
                    continue;
                }

                windows.Add(new OverviewWindow(
                    id,
                    _layout.MonitorOf(handle)?.Id ?? view.Monitor,
                    bounds,
                    WindowInspector.GetTitle(handle),
                    WindowInspector.IsMinimized(handle),
                    _pool.IsPinned(id),
                    WindowInspector.GetIcon(handle),
                    IsOnScreen: desktop.ShownOn is not null,
                    Image: PictureFor(id, handle)));
            }

            if (windows.Count == 0)
            {
                continue;
            }

            string number = Strings.DesktopNumber(desktop.Number);
            string title = desktop.Name is { } name ? $"{number} · {name}" : number;
            string? detail = desktop.ShownOn == view.Monitor ? Strings.AppWindowsThisMonitor
                : desktop.ShownOn is { } other ? Strings.ShownOnMonitor(monitors.FirstOrDefault(m => m.Id == other)?.Label ?? string.Empty)
                : null;
            groups.Add(new AppWindowGroup(desktop.Id, title, detail, windows));
        }

        if (groups.Count == 0)
        {
            return null;
        }

        var here = monitors.First(m => m.Id == monitor.Id);
        return new AppWindowsState(here, view.Name, view.Icon, groups);
    }

    /// <summary>The executable a window belongs to, cached per process instance.</summary>
    private string? AppPathOf(WindowId window)
    {
        var key = (window.ProcessId, window.ProcessStartTicks);
        if (!_appPaths.TryGetValue(key, out var path))
        {
            path = WindowInspector.GetAppPath(window);
            _appPaths[key] = path;
        }

        return path;
    }

    private static string AppNameOf(string path)
    {
        try
        {
            string? description = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description;
            }
        }
        catch (FileNotFoundException)
        {
            // Fall back to the file name.
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>What the app windows view shows: one app's windows, on one monitor.</summary>
    private sealed record AppView(string App, MonitorId Monitor, string Name, nint Icon);

    /// <summary>Closes the overview at once, e.g. on pause or when displays change.</summary>
    private void CloseOverviewNow() => _overview?.Close(animate: false);

    /// <summary>
    /// Takes a picture of each monitor whose desktop is about to be hidden and keeps each of its windows' part, for
    /// that desktop's card in the overview (overview-ux.md §9). Call before applying changes. Monitors under the
    /// overview keep the pictures taken as it opened; indicators on screen are left out of the capture.
    /// </summary>
    private void CaptureOutgoingDesktops(PoolChanges changes)
    {
        if (changes.Hide.Count == 0)
        {
            return;
        }

        var monitors = _layout.Monitors
            .Where(m => !_picturedByOverview.Contains(m.Id)
                && _lastShown.TryGetValue(m.Id, out var before) && before != _pool.GetShownDesktop(m.Id))
            .ToList();
        if (monitors.Count == 0)
        {
            return;
        }

        // Only for this snapshot: screenshots and recordings still see the indicators.
        bool hidIndicators = _indicators.SetHiddenFromCapture(true);
        try
        {
            if (hidIndicators)
            {
                ScreenCapture.WaitForCompositor();
            }

            var hiding = changes.Hide.ToHashSet();
            foreach (var monitor in monitors)
            {
                if (ScreenCapture.Capture(monitor.Bounds, SnapshotWidth) is { } snapshot)
                {
                    RememberWindowImages(snapshot, monitor, hiding.Where(w => _layout.MonitorOf((nint)w.Handle)?.Id == monitor.Id));
                }
            }
        }
        finally
        {
            if (hidIndicators)
            {
                _indicators.SetHiddenFromCapture(false);
            }
        }
    }

    /// <summary>Cuts each window's visible frame out of a monitor image and keeps it as that window's picture.</summary>
    private void RememberWindowImages(PixelImage capture, DisplayMonitor monitor, IEnumerable<WindowId> windows)
    {
        double scale = capture.Width / (double)Math.Max(1, monitor.Bounds.Width);
        foreach (var window in windows)
        {
            nint handle = (nint)window.Handle;
            if (WindowInspector.IsMinimized(handle) || !WindowInspector.IsVisible(handle) || !WindowInspector.TryGetFrame(handle, out var frame, out _))
            {
                continue;
            }

            var relative = frame.Offset(-monitor.Bounds.Left, -monitor.Bounds.Top);
            var area = new ScreenRect(
                (int)(relative.Left * scale),
                (int)(relative.Top * scale),
                (int)Math.Ceiling(relative.Right * scale),
                (int)Math.Ceiling(relative.Bottom * scale));
            if (capture.Crop(area) is { } image)
            {
                // Cards draw windows a few hundred pixels wide at most; keep no more than that.
                _windowImages[window] = image.Downscale(WindowImageWidth);
            }
        }
    }

    /// <summary>
    /// Keeps a fresh picture of the window in use while minimized windows are shown with their last content
    /// (<see cref="MinimizedWindowPreview.LastContent"/>). Windows keeps nothing of most minimized windows, and by the
    /// time a minimize is reported the content is gone, so the picture must already exist.
    /// </summary>
    private void UpdatePictureTimer()
    {
        _pictureTimer.Stop();
        if (_settings.MinimizedWindows == MinimizedWindowPreview.LastContent)
        {
            _pictureTimer.Start();
        }
    }

    /// <summary>
    /// Takes a card-sized picture of the window in use, from the screen (it is in front, so that is its content).
    /// Skipped while the overview or an indicator is on screen, which would be in the picture.
    /// </summary>
    private void TakeActiveWindowPicture()
    {
        if (IsPaused || _displaysSettling || _overview?.IsOnScreen == true || _indicators.AnyVisible)
        {
            return;
        }

        nint handle = WindowInspector.ForegroundWindow;
        if (!WindowInspector.TryGetId(handle, out var id) || !_knownHandles.Contains(id.Handle)
            || WindowInspector.IsMinimized(handle) || !WindowInspector.TryGetFrame(handle, out var frame, out _)
            || _layout.MonitorOf(handle) is not { } monitor || frame.Intersect(monitor.Bounds) is not { } visible
            || visible != frame)
        {
            return; // Not a managed window, or partly off its monitor (the picture would be cut).
        }

        // Nobody touched anything since the last picture of it: keep that one (each picture is a few hundred KB).
        var taken = (id, frame, UserInput.LastInputTick);
        if (_lastActivePicture == taken && _windowImages.ContainsKey(id))
        {
            return;
        }

        if (ScreenCapture.Capture(frame, WindowImageWidth) is { } picture)
        {
            _windowImages[id] = picture;
            _lastActivePicture = taken;
        }
    }

    /// <summary>The windows on screen on a monitor: its desktop's and the pinned ones there.</summary>
    private IEnumerable<WindowId> WindowsShownOn(MonitorId monitor)
    {
        var shown = _pool.GetDesktops().FirstOrDefault(d => d.ShownOn == monitor)?.Windows ?? [];
        return shown.Concat(_pool.PinnedWindows.Where(w => _layout.MonitorOf((nint)w.Handle)?.Id == monitor));
    }

    /// <summary>A window's last picture, unless it is minimized and the user prefers icons for those.</summary>
    private PixelImage? PictureFor(WindowId window, nint handle) =>
        _settings.MinimizedWindows == MinimizedWindowPreview.AppIcon && WindowInspector.IsMinimized(handle)
            ? null
            : _windowImages.GetValueOrDefault(window);

    private void RememberShownDesktops() =>
        _lastShown = _pool.Monitors.ToDictionary(m => m, _pool.GetShownDesktop);

    private OverviewState BuildOverviewState()
    {
        var desktops = _pool.GetDesktops();
        var managed = desktops.SelectMany(d => d.Windows).Concat(_pool.PinnedWindows).ToHashSet();
        foreach (var gone in _windowImages.Keys.Where(id => !managed.Contains(id)).ToList())
        {
            _windowImages.Remove(gone); // Closed windows.
        }

        var visible = desktops.Where(d => d.ShownOn is not null).SelectMany(d => d.Windows).Concat(_pool.PinnedWindows).Distinct();
        var windows = new List<OverviewWindow>();
        foreach (var id in WindowOperations.TopMostFirst(visible).Reverse())
        {
            nint handle = (nint)id.Handle;
            if (_layout.MonitorOf(handle) is not { } monitor || !WindowInspector.TryGetLayoutBounds(handle, _layout.Primary, out var bounds))
            {
                continue;
            }

            windows.Add(new OverviewWindow(
                id,
                monitor.Id,
                bounds,
                WindowInspector.GetTitle(handle),
                WindowInspector.IsMinimized(handle),
                _pool.IsPinned(id),
                WindowInspector.GetIcon(handle),
                Image: PictureFor(id, handle))); // When the compositor has no live preview of it.
        }

        return new OverviewState(
            BuildMonitors(),
            [.. desktops.Select(BuildDesktop)],
            windows,
            CanAddDesktop: desktops.Count < DesktopPool.MaxDesktops,
            CanRemoveDesktop: desktops.Count > _pool.Monitors.Count);
    }

    /// <summary>A desktop's card: drawn on the monitor it is (or was last) shown on, with its windows in z-order.</summary>
    private OverviewDesktop BuildDesktop(DesktopInfo desktop)
    {
        var cardMonitor = desktop.ShownOn is { } shown && _layout.TryGet(shown, out var on) ? on
            : desktop.LastMonitor is { } last && _layout.TryGet(last, out var was) ? was
            : _layout.Primary;

        var pinnedHere = _pool.PinnedWindows.Where(w => _layout.MonitorOf((nint)w.Handle)?.Id == cardMonitor.Id);
        var windows = new List<DesktopWindowPreview>();
        foreach (var id in WindowOperations.TopMostFirst(desktop.Windows.Concat(pinnedHere)).Reverse())
        {
            nint handle = (nint)id.Handle;
            if (WindowInspector.IsMinimized(handle) || !WindowInspector.TryGetFrame(handle, out var frame, out _))
            {
                continue; // As on macOS, minimized windows aren't drawn on desktop cards.
            }

            // Place it relative to the card's monitor, scaled if it is still on another monitor.
            var actual = _layout.MonitorOf(handle) ?? cardMonitor;
            double sx = cardMonitor.Bounds.Width / (double)Math.Max(1, actual.Bounds.Width);
            double sy = cardMonitor.Bounds.Height / (double)Math.Max(1, actual.Bounds.Height);
            var relative = frame.Offset(-actual.Bounds.Left, -actual.Bounds.Top);
            var bounds = new ScreenRect((int)(relative.Left * sx), (int)(relative.Top * sy), (int)(relative.Right * sx), (int)(relative.Bottom * sy));
            windows.Add(new DesktopWindowPreview(id, bounds, _windowImages.GetValueOrDefault(id), WindowInspector.GetIcon(handle)));
        }

        return new OverviewDesktop(desktop.Id, desktop.Number, desktop.Name ?? Strings.DesktopNumber(desktop.Number), desktop.ShownOn, cardMonitor.Id, windows);
    }

    private List<OverviewMonitor> BuildMonitors()
    {
        var ordered = _layout.Monitors.OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
        return
        [
            .. _layout.Monitors.Select(m => new OverviewMonitor(
                m.Id,
                m.Bounds,
                m.WorkArea,
                _pool.GetShownDesktop(m.Id),
                MonitorLabel(ordered.IndexOf(m), ordered.Count),
                _wallpapers.GetValueOrDefault(m.Id))),
        ];
    }

    private static string MonitorLabel(int index, int count) => (index, count) switch
    {
        (0, 2) or (0, 3) => Strings.MonitorLeft,
        (1, 2) or (2, 3) => Strings.MonitorRight,
        (1, 3) => Strings.MonitorCenter,
        _ => Strings.MonitorNumber(index + 1),
    };
}
