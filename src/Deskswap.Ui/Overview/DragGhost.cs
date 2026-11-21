using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Deskswap.Core;
using Deskswap.Platform;

namespace Deskswap.Ui.Overview;

/// <summary>
/// The window being dragged: a small click-through top-level window with a live preview, so it can travel across
/// monitors and above every overview (overview-ux.md §5).
/// </summary>
internal sealed class DragGhost : Window
{
    private readonly nint _hwnd;
    private WindowThumbnail? _thumbnail;

    public DragGhost()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        Background = Brushes.Black;
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        WindowStyling.MakeClickThrough(_hwnd);
        WindowStyling.UseRoundedCorners(_hwnd);
    }

    /// <summary>Starts showing a window at a screen rectangle.</summary>
    /// <param name="window">The dragged window.</param>
    /// <param name="bounds">Where to show it, in screen pixels.</param>
    /// <param name="theme">The theme, for minimized windows without a preview.</param>
    public void Begin(OverviewWindow window, ScreenRect bounds, Theme theme)
    {
        _thumbnail?.Dispose();
        _thumbnail = null;
        Content = null;
        Background = Brushes.Black;
        WindowStyling.PlaceTopmost(_hwnd, bounds);
        Show();

        if (window.IsOnScreen)
        {
            _thumbnail = WindowThumbnail.TryCreate(_hwnd, window.Handle);
            if (_thumbnail is { HasContent: false })
            {
                _thumbnail.Dispose();
                _thumbnail = null;
            }
        }

        if (_thumbnail is null)
        {
            Background = theme.Backdrop;
            Content = window.Image is { } picture
                ? new Image { Source = Images.ToBitmap(picture), Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top }
                : new Image { Source = Images.FromIcon(window.Icon), Width = 48, Height = 48 };
        }

        MoveTo(bounds);
    }

    /// <summary>Moves (and resizes) the preview.</summary>
    /// <param name="bounds">The new bounds, in screen pixels.</param>
    public void MoveTo(ScreenRect bounds)
    {
        WindowStyling.PlaceTopmost(_hwnd, bounds);
        _thumbnail?.Update(new ScreenRect(0, 0, bounds.Width, bounds.Height));
    }

    /// <summary>Stops showing it.</summary>
    public void End()
    {
        _thumbnail?.Dispose();
        _thumbnail = null;
        Hide();
    }

    /// <inheritdoc/>
    protected override void OnClosed(EventArgs e)
    {
        _thumbnail?.Dispose();
        base.OnClosed(e);
    }
}
