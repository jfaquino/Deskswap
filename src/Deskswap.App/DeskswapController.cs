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
    private readonly HiddenWindowJournal _journal;
    private readonly ChangeApplier _applier;
    private readonly WindowInspector _inspector = new();
    private readonly LayoutStore _layoutStore = new(AppPaths.Layout);
    private readonly IndicatorPresenter _indicators = new();
    private MonitorLayout _layout;
    private DesktopPool _pool;

    public DeskswapController()
    {
        _journal = new HiddenWindowJournal(AppPaths.Journal);
        _applier = new ChangeApplier(_journal);
        _layout = MonitorLayout.Capture();
        _pool = DesktopPool.Create(_layout.Ids);
    }

    /// <summary>Raised with a message for the user (shown as a tray notification).</summary>
    public event EventHandler<string>? Notify;

    /// <summary>Raised when the paused state changes.</summary>
    public event EventHandler? PausedChanged;

    /// <summary>Gets a value indicating whether Deskswap is paused (all windows visible, nothing managed).</summary>
    public bool IsPaused { get; private set; }

    /// <summary>
    /// Starts up: shows windows a previous run left hidden, restores the saved layout, then re-applies it so windows
    /// on hidden desktops are hidden again (design §8.3, §5.3).
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
        Guard(Reconcile);
    }

    /// <summary>Runs a shortcut command on the monitor under the mouse cursor.</summary>
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

        if (IsPaused)
        {
            return;
        }

        Guard(() =>
        {
            // Adopt windows opened since the last sweep, so they belong to the desktop they were opened on.
            Reconcile();

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
            Guard(Reconcile);
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
    /// Reacts to monitors being connected, disconnected or rearranged. The pool is rebuilt from its own snapshot
    /// for the new monitors; reconciling then hides or shows windows to match (design §4).
    /// </summary>
    public void OnDisplaysChanged()
    {
        Guard(() =>
        {
            MonitorLayout layout;
            try
            {
                layout = MonitorLayout.Capture();
            }
            catch (InvalidOperationException)
            {
                return; // Mid-change with no monitors; the next notification will try again.
            }

            bool sameMonitors = layout.Ids.SequenceEqual(_layout.Ids);
            _layout = layout;
            if (!sameMonitors)
            {
                Log.Info($"Monitors changed: now {layout.Monitors.Count}.");
                _pool = DesktopPool.Restore(_pool.ToSnapshot(), layout.Ids);
            }

            if (!IsPaused)
            {
                Reconcile();
            }
        });
    }

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
        Guard(Reconcile);
    }

    /// <summary>Saves the layout and shows every window. Call on exit.</summary>
    public void Shutdown()
    {
        SaveLayout();
        int shown = _applier.ShowAll();
        Log.Info($"Exited; showed {shown} hidden windows.");
    }

    public void Dispose() => _indicators.Dispose();

    private void Reconcile()
    {
        var observed = _inspector.Observe(_layout, _applier.HiddenWindows.ToList());
        Apply(_pool.Reconcile(observed));
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
