using Deskswap.Core;

namespace Deskswap.Platform;

/// <summary>
/// Applies <see cref="PoolChanges"/> to real windows in the order the safety rules require (design §8): journal,
/// hide, relocate, show, forget, focus.
/// </summary>
/// <param name="journal">The write-ahead journal of hidden windows.</param>
public sealed class ChangeApplier(HiddenWindowJournal journal)
{
    private readonly HiddenWindowJournal _journal = journal ?? throw new ArgumentNullException(nameof(journal));

    /// <summary>
    /// Stacking rank of each hidden window, recorded just before it was hidden: higher was closer to the top.
    /// Hiding changes the z-order (Windows activates and raises the next window), so it can't be read back later.
    /// </summary>
    private readonly Dictionary<WindowId, long> _stackRank = [];
    private long _nextRank;

    /// <summary>When each window was last hidden, to tell Deskswap's own hide apart from an app showing itself.</summary>
    private readonly Dictionary<WindowId, long> _hiddenAt = [];

    /// <summary>How long a hide may take to land (hide requests are asynchronous).</summary>
    private static readonly TimeSpan HideGracePeriod = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Gets a value indicating whether Deskswap asked to hide this window moments ago. Such a window may still look
    /// visible because the hide is asynchronous; it must not be mistaken for a window asking for attention.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <returns>
    /// <see langword="true"/> while the hide may still be in flight: shortly after it was requested, or for as long as
    /// the window's app isn't responding (it will process the hide when it recovers).
    /// </returns>
    public bool IsHidePending(WindowId window) =>
        _hiddenAt.TryGetValue(window, out long at)
        && (System.Diagnostics.Stopwatch.GetElapsedTime(at) < HideGracePeriod || WindowInspector.IsHung((nint)window.Handle));

    /// <summary>Gets the windows Deskswap has hidden and not shown again.</summary>
    public IEnumerable<WindowId> HiddenWindows => _journal.Windows;

    /// <summary>Applies changes.</summary>
    /// <param name="changes">The changes from the pool.</param>
    /// <param name="layout">The current monitor layout.</param>
    /// <returns>The indicators to display.</returns>
    public IReadOnlyList<Indicator> Apply(PoolChanges changes, MonitorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(layout);

        if (changes.Hide.Count > 0)
        {
            // Write-ahead: the journal is on disk before any window disappears.
            _journal.Record(changes.Hide.Select(w => JournalEntry.For(w, WindowInspector.GetTitle((nint)w.Handle))));

            // Record the stack, then hide bottom-most first so only the last (top-most) hide moves activation.
            var bottomMostFirst = WindowOperations.TopMostFirst(changes.Hide).Reverse().ToList();
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var window in bottomMostFirst)
            {
                _stackRank[window] = ++_nextRank;
                _hiddenAt[window] = now;
                WindowOperations.Hide(window);
            }
        }

        foreach (var relocation in changes.Relocations)
        {
            if (!layout.TryGet(relocation.To, out var to))
            {
                continue;
            }

            // Unknown origin: move the window from wherever it actually is, and not at all if it's already there.
            var from = relocation.From is { } known && layout.TryGet(known, out var origin)
                ? origin
                : layout.MonitorOf((nint)relocation.Window.Handle);
            if (from is not null && from != to)
            {
                WindowOperations.Relocate(relocation.Window, from, to, layout.Primary);
            }
        }

        if (changes.Show.Count > 0)
        {
            // Bottom-most first, so the desktop comes back stacked exactly as it was hidden.
            WindowOperations.ShowStacked(InRecordedOrder(changes.Show));
            _journal.Forget(changes.Show);
            ForgetRanks(changes.Show);
        }

        if (changes.Focus is { } focus)
        {
            ApplyFocus(focus, changes);
        }

        return changes.Indicators;
    }

    /// <summary>
    /// Shows every window Deskswap hid, on top of the others and in their recorded stacking order, and clears the
    /// journal (tray "Show all windows", pause, exit, errors).
    /// </summary>
    /// <returns>The number of windows shown.</returns>
    public int ShowAll()
    {
        var startTimes = new Dictionary<uint, long>();
        var hidden = _journal.Windows
            .Where(w => WindowInspector.IsAlive(w, startTimes) && !WindowInspector.IsVisible((nint)w.Handle))
            .ToList();
        int shown = WindowOperations.ShowStacked(InRecordedOrder(hidden));
        _journal.Clear();
        _stackRank.Clear();
        _hiddenAt.Clear();
        return shown;
    }

    /// <summary>
    /// Orders windows bottom-most first by recorded rank. Hidden windows without a rank go to the bottom; windows that
    /// are already visible (e.g. one just moved to this desktop) go to the top so they keep focus.
    /// </summary>
    private List<WindowId> InRecordedOrder(IEnumerable<WindowId> windows) =>
        [.. windows.OrderBy(w => _stackRank.TryGetValue(w, out long rank) ? rank
            : WindowInspector.IsVisible((nint)w.Handle) ? long.MaxValue
            : 0)];

    private void ForgetRanks(IEnumerable<WindowId> windows)
    {
        foreach (var window in windows)
        {
            _stackRank.Remove(window);
            _hiddenAt.Remove(window);
        }
    }

    /// <summary>
    /// Focuses the top-most window that just arrived on the monitor: with the stacking order kept, that is the
    /// window that was active when the desktop was left. Minimized windows are skipped because focusing them would
    /// not restore them.
    /// </summary>
    private static void ApplyFocus(FocusRequest focus, PoolChanges changes)
    {
        var candidates = new HashSet<long>(changes.Show.Select(w => w.Handle));
        candidates.UnionWith(changes.Relocations.Where(r => r.To == focus.Monitor).Select(r => r.Window.Handle));

        foreach (nint window in WindowInspector.EnumerateTopLevel())
        {
            if (candidates.Contains(window) && !WindowInspector.IsMinimized(window))
            {
                WindowOperations.Focus(window);
                return;
            }
        }
    }
}
