using Deskswap.Core;
using Deskswap.Platform;

namespace Deskswap.Ui.Overview;

/// <summary>A monitor as the overview sees it.</summary>
/// <param name="Id">The monitor.</param>
/// <param name="Bounds">Its bounds, in screen pixels.</param>
/// <param name="WorkArea">Its work area (without the taskbar), in screen pixels.</param>
/// <param name="Shown">The desktop it shows.</param>
/// <param name="Label">A short name for badges, e.g. "Left".</param>
/// <param name="Wallpaper">Its desktop wallpaper, if Windows reported one.</param>
public sealed record OverviewMonitor(MonitorId Id, ScreenRect Bounds, ScreenRect WorkArea, DesktopId Shown, string Label, Wallpaper? Wallpaper);

/// <summary>A desktop card in the strip.</summary>
/// <param name="Id">The desktop.</param>
/// <param name="Number">Its 1-based number.</param>
/// <param name="Title">Its name, or "Desktop N".</param>
/// <param name="ShownOn">The monitor showing it, if any.</param>
/// <param name="PreviewMonitor">The monitor whose shape and wallpaper its card uses (where it is or was last shown).</param>
/// <param name="Windows">Its windows for the card, bottom-most first.</param>
public sealed record OverviewDesktop(
    DesktopId Id,
    int Number,
    string Title,
    MonitorId? ShownOn,
    MonitorId PreviewMonitor,
    IReadOnlyList<DesktopWindowPreview> Windows);

/// <summary>A window drawn in miniature on a desktop card.</summary>
/// <param name="Id">The window.</param>
/// <param name="Bounds">Its place relative to the card's monitor (0,0 = the monitor's top-left), in pixels.</param>
/// <param name="Image">The last picture of it, if Deskswap saw it on screen; otherwise its icon is drawn.</param>
/// <param name="Icon">Its icon handle (not owned), or 0.</param>
public sealed record DesktopWindowPreview(WindowId Id, ScreenRect Bounds, PixelImage? Image, nint Icon);

/// <summary>A window tile in a monitor's window area.</summary>
/// <param name="Id">The window.</param>
/// <param name="Monitor">The monitor it is on.</param>
/// <param name="Bounds">Its bounds (for a minimized window, where it restores to), in screen pixels.</param>
/// <param name="Title">Its title.</param>
/// <param name="IsMinimized">Whether it is minimized (no live preview).</param>
/// <param name="IsPinned">Whether it is pinned to every desktop.</param>
/// <param name="Icon">Its icon handle (not owned), or 0.</param>
/// <param name="IsOnScreen">Whether it is on a desktop that is shown (else it is hidden: no live preview).</param>
/// <param name="Image">Its last picture, drawn when there is no live preview.</param>
public sealed record OverviewWindow(
    WindowId Id,
    MonitorId Monitor,
    ScreenRect Bounds,
    string Title,
    bool IsMinimized,
    bool IsPinned,
    nint Icon,
    bool IsOnScreen = true,
    PixelImage? Image = null)
{
    /// <summary>Gets the window handle.</summary>
    public nint Handle => (nint)Id.Handle;
}

/// <summary>Everything the overview shows. Windows are listed bottom-most first.</summary>
/// <param name="Monitors">The monitors.</param>
/// <param name="Desktops">The desktops, in pool order.</param>
/// <param name="Windows">The windows of the shown desktops, bottom-most first.</param>
/// <param name="CanAddDesktop">Whether the pool has room for another desktop.</param>
/// <param name="CanRemoveDesktop">Whether a desktop can be removed (one per monitor must remain).</param>
public sealed record OverviewState(
    IReadOnlyList<OverviewMonitor> Monitors,
    IReadOnlyList<OverviewDesktop> Desktops,
    IReadOnlyList<OverviewWindow> Windows,
    bool CanAddDesktop,
    bool CanRemoveDesktop);

/// <summary>
/// The app windows view (overview-ux.md §4, <c>Win+Ctrl+↓</c>): every window of one app, on every desktop, grouped
/// by desktop, on one monitor.
/// </summary>
/// <param name="Monitor">The monitor it opens on (the focused window's).</param>
/// <param name="AppName">The app's name.</param>
/// <param name="AppIcon">The app's icon handle (not owned), or 0.</param>
/// <param name="Groups">One group per desktop with windows of the app, in desktop order.</param>
public sealed record AppWindowsState(OverviewMonitor Monitor, string AppName, nint AppIcon, IReadOnlyList<AppWindowGroup> Groups);

/// <summary>The app's windows on one desktop.</summary>
/// <param name="Desktop">The desktop.</param>
/// <param name="Title">The header, e.g. "Desktop 3 · Music".</param>
/// <param name="Detail">Where the desktop is, e.g. "On this monitor" or "Shown on Left", or <see langword="null"/>.</param>
/// <param name="Windows">The windows, bottom-most first.</param>
public sealed record AppWindowGroup(DesktopId Desktop, string Title, string? Detail, IReadOnlyList<OverviewWindow> Windows);

/// <summary>Localized text the overview displays; the app supplies it from its resources.</summary>
/// <param name="NoWindows">Shown in an empty window area.</param>
/// <param name="NewDesktop">Tooltip of the <c>+</c> card.</param>
/// <param name="Rename">Menu item.</param>
/// <param name="Remove">Menu item and tooltip of <c>×</c>.</param>
/// <param name="CannotRemove">Tooltip when a desktop can't be removed.</param>
/// <param name="MoveTo">Window menu item.</param>
/// <param name="Pin">Window menu item.</param>
/// <param name="Unpin">Window menu item.</param>
/// <param name="ShownOn">Format for "shown on {monitor}".</param>
public sealed record OverviewText(
    string NoWindows,
    string NewDesktop,
    string Rename,
    string Remove,
    string CannotRemove,
    string MoveTo,
    string Pin,
    string Unpin,
    string ShownOn);

/// <summary>
/// What the overview asks the app to do. The app runs the operation on the pool, applies it to real windows, and then
/// calls <see cref="OverviewHost.Update"/> or <see cref="OverviewHost.Close"/>.
/// </summary>
public interface IOverviewActions
{
    /// <summary>The user chose a window: close the overview and focus it.</summary>
    /// <param name="window">The window.</param>
    void ChooseWindow(WindowId window);

    /// <summary>The user chose a desktop for a monitor: show it there (swap if needed).</summary>
    /// <param name="monitor">The monitor.</param>
    /// <param name="desktop">The desktop.</param>
    /// <param name="close">Whether to close the overview afterwards.</param>
    void ShowDesktop(MonitorId monitor, DesktopId desktop, bool close);

    /// <summary>The user dropped a window on a desktop.</summary>
    /// <param name="window">The window.</param>
    /// <param name="desktop">The desktop.</param>
    void MoveWindow(WindowId window, DesktopId desktop);

    /// <summary>The user dragged a desktop card to a new place in the strip.</summary>
    /// <param name="desktop">The desktop.</param>
    /// <param name="number">Its new 1-based number.</param>
    void MoveDesktop(DesktopId desktop, int number);

    /// <summary>The user dropped a window on the <c>+</c> card.</summary>
    /// <param name="window">The window.</param>
    /// <param name="monitor">The monitor whose strip it was dropped on.</param>
    void MoveWindowToNewDesktop(WindowId window, MonitorId monitor);

    /// <summary>The user clicked <c>+</c>.</summary>
    /// <param name="monitor">The monitor whose strip was clicked.</param>
    void AddDesktop(MonitorId monitor);

    /// <summary>The user removed a desktop.</summary>
    /// <param name="desktop">The desktop.</param>
    /// <param name="monitor">The monitor whose strip it was removed from.</param>
    void RemoveDesktop(DesktopId desktop, MonitorId monitor);

    /// <summary>The user renamed a desktop.</summary>
    /// <param name="desktop">The desktop.</param>
    /// <param name="name">The new name; empty clears it.</param>
    void RenameDesktop(DesktopId desktop, string? name);

    /// <summary>The user pinned or unpinned a window.</summary>
    /// <param name="window">The window.</param>
    /// <param name="monitor">The monitor it is on.</param>
    void TogglePin(WindowId window, MonitorId monitor);

    /// <summary>The user dismissed the overview without choosing anything.</summary>
    void Dismiss();
}
