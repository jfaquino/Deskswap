using Deskswap.App.Resources;
using Deskswap.Core;
using Deskswap.Core.Input;
using Deskswap.Platform;

namespace Deskswap.App;

/// <summary>
/// The composition root's engine: runs shortcut commands against the <see cref="DesktopPool"/>, applies the
/// resulting changes to real windows, keeps the pool in sync with what's on screen, and fails safe by showing all
/// windows whenever something goes wrong (design §8). Use from the UI thread only.
/// </summary>
internal sealed class DeskswapController : IDisposable
{
    /// <summary>Window events are coalesced for this long before the pool is reconciled.</summary>
    private const int EventSettleMs = 40;

    /// <summary>A window that switches desktops this often within <see cref="ActivationWindow"/> is ignored for a while.</summary>
    private const int ActivationLimit = 2;

    private static readonly TimeSpan ActivationWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ActivationCooldown = TimeSpan.FromMinutes(1);

    /// <summary>The displays must look the same for this long before a change is applied.</summary>
    private const int DisplaySettleMs = 750;

    /// <summary>After a display change, windows Windows moves are put back instead of reassigned for this long.</summary>
    private static readonly TimeSpan HoldAfterDisplayChange = TimeSpan.FromSeconds(3);

    private readonly HiddenWindowJournal _journal;
    private readonly ChangeApplier _applier;
    private readonly WindowInspector _inspector = new();
    private readonly LayoutStore _layoutStore = new(AppPaths.Layout);
    private readonly IndicatorPresenter _indicators = new();
    private readonly WindowEventHook _windowEvents = new();
    private readonly System.Windows.Forms.Timer _eventTimer = new() { Interval = EventSettleMs };
    private readonly Dictionary<WindowId, List<DateTime>> _recentActivations = [];
    private readonly Dictionary<WindowId, DateTime> _activationBlockedUntil = [];
    private readonly System.Windows.Forms.Timer _settleTimer = new() { Interval = DisplaySettleMs };
    private HashSet<long> _knownHandles = [];
    private bool _displaysSettling;
    private string _lastDisplaySignature = string.Empty;
    private DateTime _holdUntil = DateTime.MinValue;
    private bool _elevatedTipShown;
    private MonitorLayout _layout;
    private DesktopPool _pool;

    public DeskswapController()
    {
        _journal = new HiddenWindowJournal(AppPaths.Journal);
        _applier = new ChangeApplier(_journal);
        _layout = MonitorLayout.Capture();
        _pool = DesktopPool.Create(_layout.Ids);
        _eventTimer.Tick += (_, _) =>
        {
            _eventTimer.Stop();
            if (!IsPaused)
            {
                Guard(() => Reconcile(ReconcileMode.FollowActivation));
            }
        };
        _windowEvents.WindowChanged += (_, e) => OnWindowEvent(e);
        _settleTimer.Tick += (_, _) => OnSettleTick();
    }

    /// <summary>Raised with a message for the user (shown as a tray notification).</summary>
    public event EventHandler<string>? Notify;

    /// <summary>Raised when the paused state changes.</summary>
    public event EventHandler? PausedChanged;

    /// <summary>Gets a value indicating whether Deskswap is paused (all windows visible, nothing managed).</summary>
    public bool IsPaused { get; private set; }

    /// <summary>
    /// Starts up: shows windows a previous run left hidden, restores the saved layout, re-applies it so windows on
    /// hidden desktops are hidden again (design §8.3, §5.3), then starts listening to window events.
    /// </summary>
    public void Initialize()
    {
        if (_journal.QuarantinedPath is not null)
        {
            Log.Info($"Journal was corrupt and was moved to {_journal.QuarantinedPath}.");
        }

        int recovered = _applier.ShowAll();
        if (recovered > 0)
        {
            Log.Info($"Recovered {recovered} hidden windows at startup.");
            Notify?.Invoke(this, Strings.NotifyRecovered(recovered));
        }

        var snapshot = _layoutStore.Load();
        if (_layoutStore.QuarantinedPath is not null)
        {
            Log.Info($"Layout was corrupt and was moved to {_layoutStore.QuarantinedPath}.");
            Notify?.Invoke(this, Strings.NotifyLayoutReset);
        }

        if (snapshot is not null)
        {
            _pool = DesktopPool.Restore(snapshot, _layout.Ids);
        }

        Log.Info($"Started with {_layout.Monitors.Count} monitors and {_pool.DesktopCount} desktops.");
        Guard(() => Reconcile(ReconcileMode.Enforce));

        if (!_windowEvents.Start())
        {
            Log.Info("Some window event hooks could not be installed; relying on the periodic sweep.");
        }
    }

    /// <summary>Runs a shortcut command on the monitor under the mouse cursor or on the focused window.</summary>
    /// <param name="action">The action from the keyboard hook.</param>
    public void Execute(ShortcutAction action)
    {
        if (action.Command == ShortcutCommand.TogglePause)
        {
            if (IsPaused)
            {
                Resume();
            }
            else
            {
                Pause();
            }

            return;
        }

        if (IsPaused || _displaysSettling)
        {
            return; // While displays change, monitors and windows are in flux; acting now would be on stale state.
        }

        Guard(() =>
        {
            // Adopt windows opened since the last pass, so they belong to the desktop they were opened on.
            Reconcile(ReconcileMode.FollowActivation);

            if (_layout.MonitorUnderCursor() is not { } monitor)
            {
                return;
            }

            var target = monitor.Id;
            var changes = action.Command switch
            {
                ShortcutCommand.PreviousDesktop => _pool.GoToPrevious(target),
                ShortcutCommand.NextDesktop => _pool.GoToNext(target),
                ShortcutCommand.GoToDesktop => _pool.GoTo(target, action.Argument),
                ShortcutCommand.AddDesktop => _pool.AddDesktop(target),
                ShortcutCommand.RemoveDesktop => _pool.RemoveDesktop(_pool.GetShownNumber(target), target),
                ShortcutCommand.MoveWindowToPreviousDesktop => OnFocusedWindow(_pool.MoveWindowToPrevious),
                ShortcutCommand.MoveWindowToNextDesktop => OnFocusedWindow(_pool.MoveWindowToNext),
                ShortcutCommand.MoveWindowToDesktop => OnFocusedWindow(w => _pool.MoveWindowTo(w, action.Argument)),
                ShortcutCommand.TogglePinWindow => TogglePinFocusedWindow(),
                _ => PoolChanges.None,
            };
            Apply(changes);
        });
    }

    /// <summary>The periodic safety-net sweep (design §12).</summary>
    public void Tick()
    {
        if (!IsPaused)
        {
            Guard(() => Reconcile(ReconcileMode.FollowActivation));
        }
    }

    /// <summary>Saves the layout if it changed since the last save.</summary>
    public void SaveLayout()
    {
        try
        {
            _layoutStore.SaveIfChanged(_pool.ToSnapshot());
        }
        catch (IOException ex)
        {
            Log.Error("Could not save the layout.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error("Could not save the layout.", ex);
        }
    }

    /// <summary>
    /// Reacts to monitors being connected, disconnected or rearranged (design §4): a disconnected monitor's desktop
    /// stays in the pool with its windows hidden, and the monitor gets it back when reconnected.
    /// </summary>
    public void OnDisplaysChanged() => BeginDisplaySettling();

    /// <summary>Shows every window and stops managing them until <see cref="Resume"/> (design §8.4).</summary>
    public void Pause()
    {
        if (IsPaused)
        {
            return;
        }

        SaveLayout();
        int shown = _applier.ShowAll();
        IsPaused = true;
        Log.Info($"Paused; showed {shown} hidden windows.");
        PausedChanged?.Invoke(this, EventArgs.Empty);
        Notify?.Invoke(this, Strings.NotifyPaused);
    }

    /// <summary>Resumes managing windows; windows on hidden desktops are hidden again.</summary>
    public void Resume()
    {
        if (!IsPaused)
        {
            return;
        }

        IsPaused = false;
        Log.Info("Resumed.");
        PausedChanged?.Invoke(this, EventArgs.Empty);
        Guard(() => Reconcile(ReconcileMode.Enforce));
    }

    /// <summary>Saves the layout and shows every window. Call on exit.</summary>
    public void Shutdown()
    {
        _windowEvents.Dispose();
        _eventTimer.Stop();
        _settleTimer.Stop();
        SaveLayout();
        int shown = _applier.ShowAll();
        Log.Info($"Exited; showed {shown} hidden windows.");
    }

    public void Dispose()
    {
        _windowEvents.Dispose();
        _eventTimer.Dispose();
        _settleTimer.Dispose();
        _indicators.Dispose();
    }

    private void OnWindowEvent(WindowEvent e)
    {
        if (IsPaused)
        {
            return;
        }

        // Title changes are frequent (browser tabs, timers); they only matter for windows not managed yet.
        if (e.Kind == WindowEventKind.Renamed && _knownHandles.Contains(e.Window))
        {
            return;
        }

        if (!_eventTimer.Enabled)
        {
            _eventTimer.Start();
        }
    }

    /// <summary>Stops all window decisions until the displays look the same for <see cref="DisplaySettleMs"/>.</summary>
    private void BeginDisplaySettling()
    {
        _lastDisplaySignature = MonitorLayout.ReadDisplaySignature();
        if (!_displaysSettling)
        {
            _displaysSettling = true;
            Log.Info("Displays are changing; waiting for them to settle.");
        }

        _settleTimer.Stop();
        _settleTimer.Start();
    }

    private void OnSettleTick()
    {
        string signature = MonitorLayout.ReadDisplaySignature();
        if (signature != _lastDisplaySignature)
        {
            _lastDisplaySignature = signature; // Still changing: wait another round.
            return;
        }

        _settleTimer.Stop();
        _displaysSettling = false;
        Guard(() =>
        {
            if (!RefreshDisplays())
            {
                BeginDisplaySettling(); // No monitors yet; keep waiting.
                return;
            }

            _holdUntil = DateTime.UtcNow + HoldAfterDisplayChange;
            if (!IsPaused)
            {
                Reconcile(ReconcileMode.Hold);
            }
        });
    }

    /// <summary>
    /// Captures the displays again and, if the set of monitors changed, updates the pool: a disconnected monitor's
    /// desktop is hidden and remembered, a reconnected one gets it back (design §4).
    /// </summary>
    /// <returns><see langword="true"/> if the layout was replaced.</returns>
    private bool RefreshDisplays()
    {
        MonitorLayout layout;
        try
        {
            layout = MonitorLayout.Capture();
        }
        catch (InvalidOperationException)
        {
            return false; // Mid-change with no monitors; the next notification will try again.
        }

        bool sameMonitors = layout.Ids.SequenceEqual(_layout.Ids);
        _layout = layout;
        if (!sameMonitors)
        {
            Log.Info($"Monitors changed: now {layout.Monitors.Count}.");
            var changes = _pool.SetMonitors(layout.Ids);
            if (!IsPaused)
            {
                Apply(changes);
            }
        }

        return true;
    }

    private void Reconcile(ReconcileMode mode)
    {
        // Windows rearranges monitors in several steps and moves windows around on its own while it does. Acting on any
        // intermediate state would put windows in the wrong place or reassign them, so wait until the displays settle.
        if (_displaysSettling)
        {
            return;
        }

        if (!_layout.MatchesCurrentDisplays())
        {
            BeginDisplaySettling();
            return;
        }

        // An incomplete observation would make the pool forget the missing windows, so never reconcile with one.
        var observed = ObserveWindows();
        if (_inspector.SawUnknownMonitor)
        {
            BeginDisplaySettling();
            return;
        }

        // Shortly after a display change, windows on the "wrong" monitor were moved by Windows, not the user.
        if (DateTime.UtcNow < _holdUntil)
        {
            mode = ReconcileMode.Hold;
        }

        WindowId? foreground = WindowInspector.TryGetId(WindowInspector.ForegroundWindow, out var id) ? id : null;
        var changes = _pool.Reconcile(observed, mode, foreground, BlockedActivations());
        if (changes.ActivatedBy is { } activated)
        {
            TrackActivation(activated);
        }

        Apply(changes);
        _knownHandles = [.. observed.Select(w => w.Id.Handle)];
        ShowElevatedTipOnce();
    }

    private void Apply(PoolChanges changes)
    {
        if (changes == PoolChanges.None)
        {
            return;
        }

        var indicators = _applier.Apply(changes, _layout);
        _indicators.Show(indicators, _layout);
    }

    private List<ObservedWindow> ObserveWindows() =>
        [
            .. _inspector.Observe(_layout, _applier.HiddenWindows.ToList())
                .Select(w => w.IsVisible && _applier.IsHidePending(w.Id) ? w with { IsVisible = false } : w), // Our own hide, still in flight.
        ];

    private static PoolChanges OnFocusedWindow(Func<WindowId, PoolChanges> operation) =>
        WindowInspector.TryGetId(WindowInspector.ForegroundWindow, out var window) ? operation(window) : PoolChanges.None;

    private PoolChanges TogglePinFocusedWindow()
    {
        nint handle = WindowInspector.ForegroundWindow;
        return WindowInspector.TryGetId(handle, out var window) && _layout.MonitorOf(handle) is { } monitor
            ? _pool.TogglePin(window, monitor.Id)
            : PoolChanges.None;
    }

    /// <summary>
    /// Stops following a window that keeps showing itself right after being hidden, so two desktops can't
    /// ping-pong. It is hidden normally instead, for a minute.
    /// </summary>
    private void TrackActivation(WindowId window)
    {
        var now = DateTime.UtcNow;
        if (!_recentActivations.TryGetValue(window, out var times))
        {
            times = [];
            _recentActivations[window] = times;
        }

        times.RemoveAll(t => now - t > ActivationWindow);
        times.Add(now);
        if (times.Count >= ActivationLimit)
        {
            _activationBlockedUntil[window] = now + ActivationCooldown;
            times.Clear();
            Log.Info($"Window {window.Handle:X} keeps showing itself; not following it for {ActivationCooldown.TotalSeconds:0} s.");
        }
    }

    private HashSet<WindowId> BlockedActivations()
    {
        var now = DateTime.UtcNow;
        foreach (var expired in _activationBlockedUntil.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToList())
        {
            _activationBlockedUntil.Remove(expired);
        }

        return [.. _activationBlockedUntil.Keys];
    }

    private void ShowElevatedTipOnce()
    {
        if (!_elevatedTipShown && _inspector.ElevatedWindowsSkipped > 0)
        {
            _elevatedTipShown = true;
            Log.Info($"{_inspector.ElevatedWindowsSkipped} windows belong to apps running as administrator; leaving them visible.");
            Notify?.Invoke(this, Strings.NotifyElevated);
        }
    }

    /// <summary>Runs an operation; on any failure, logs it, shows every window and pauses (design §8.6).</summary>
    private void Guard(Action operation)
    {
        try
        {
            operation();
        }
#pragma warning disable CA1031 // Any failure must end with all windows visible, whatever its type.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Error("Operation failed; showing all windows and pausing.", ex);
            try
            {
                _applier.ShowAll();
            }
#pragma warning disable CA1031 // Last resort: the watchdog and the next start will recover from the journal.
            catch (Exception showAllError)
#pragma warning restore CA1031
            {
                Log.Error("Showing all windows failed too.", showAllError);
            }

            if (!IsPaused)
            {
                IsPaused = true;
                PausedChanged?.Invoke(this, EventArgs.Empty);
            }

            Notify?.Invoke(this, Strings.NotifyError);
        }
    }
}
