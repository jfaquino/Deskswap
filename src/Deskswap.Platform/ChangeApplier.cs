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
            foreach (var window in changes.Hide)
            {
                WindowOperations.Hide(window);
            }
        }

        foreach (var relocation in changes.Relocations)
        {
            if (layout.TryGet(relocation.From, out var from) && layout.TryGet(relocation.To, out var to))
            {
                WindowOperations.Relocate(relocation.Window, from, to, layout.Primary);
            }
        }

        if (changes.Show.Count > 0)
        {
            foreach (var window in changes.Show)
            {
                WindowOperations.Show(window);
            }

            _journal.Forget(changes.Show);
        }

        if (changes.Focus is { } focus)
        {
            ApplyFocus(focus, changes);
        }

        return changes.Indicators;
    }

    /// <summary>Shows every window Deskswap hid and clears the journal (tray "Show all windows", exit, errors).</summary>
    /// <returns>The number of windows shown.</returns>
    public int ShowAll() => WindowRecovery.RestoreAll(_journal);

    /// <summary>
    /// Focuses the requested window, or else the top-most window that just arrived on the monitor. Minimized
    /// windows are skipped because focusing them would not restore them.
    /// </summary>
    private static void ApplyFocus(FocusRequest focus, PoolChanges changes)
    {
        var candidates = new HashSet<long>(changes.Show.Select(w => w.Handle));
        candidates.UnionWith(changes.Relocations.Where(r => r.To == focus.Monitor).Select(r => r.Window.Handle));

        if (focus.Window is { } preferred && !WindowInspector.IsMinimized((nint)preferred.Handle) && WindowInspector.IsAlive(preferred))
        {
            WindowOperations.Focus((nint)preferred.Handle);
            return;
        }

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
