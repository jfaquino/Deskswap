namespace Deskswap.Core;

/// <summary>
/// What the overview shows for a minimized window. Windows keeps no picture of most minimized windows, so showing
/// their content means Deskswap keeps one itself.
/// </summary>
public enum MinimizedWindowPreview
{
    /// <summary>
    /// The window's last content: Deskswap keeps a recent picture of the window in use (taken from the screen every
    /// few seconds, card-sized, in memory only), so it is there when the window gets minimized.
    /// </summary>
    LastContent,

    /// <summary>The app's icon. Deskswap takes no pictures beyond those the overview and desktop cards need.</summary>
    AppIcon,
}

/// <summary>
/// The user's preferences (design §10, <c>settings.json</c>). Until the settings window and file exist (M4),
/// Deskswap runs with <see cref="Default"/>.
/// </summary>
public sealed record DeskswapSettings
{
    /// <summary>Gets the settings Deskswap starts with.</summary>
    public static DeskswapSettings Default { get; } = new();

    /// <summary>Gets what the overview shows for minimized windows.</summary>
    public MinimizedWindowPreview MinimizedWindows { get; init; } = MinimizedWindowPreview.LastContent;
}
