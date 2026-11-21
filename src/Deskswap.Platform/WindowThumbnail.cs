using Deskswap.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace Deskswap.Platform;

/// <summary>
/// A live preview of a window drawn by the desktop compositor into one of Deskswap's windows (overview-ux.md §9).
/// It costs Deskswap no CPU, follows the source window's content in real time, and is drawn on top of the
/// destination window's own content. For most minimized windows Windows has no content to show, only a small
/// stand-in picture; <see cref="HasContent"/> is <see langword="false"/> then.
/// </summary>
/// <remarks>
/// The part of the source shown is its visible frame, without the invisible resize borders. That part changes when
/// the window is resized, maximized, restored or minimized: call <see cref="Refresh"/> then.
/// </remarks>
public sealed class WindowThumbnail : IDisposable
{
    /// <summary>A source smaller than this is a minimized window's title bar, not its content.</summary>
    private const int MinimumContentSize = 64;

    private const int MaxRememberedBorders = 256;

    /// <summary>
    /// Each window's invisible borders in its preview's pixels, learned while it wasn't minimized; a minimized window
    /// reports no frame to compute them from.
    /// </summary>
    private static readonly Dictionary<nint, Borders> s_knownBorders = [];

    private readonly nint _thumbnail;
    private readonly nint _source;
    private RECT? _sourceArea;
    private bool _disposed;

    private WindowThumbnail(nint thumbnail, nint source)
    {
        _thumbnail = thumbnail;
        _source = source;
    }

    /// <summary>
    /// Gets a value indicating whether the compositor has the window's content to show: not a title bar, and not the
    /// generic stand-in picture (about 200 by 108 pixels) it gives for most minimized windows.
    /// </summary>
    public bool HasContent { get; private set; }

    /// <summary>Registers a thumbnail of <paramref name="source"/> in <paramref name="destination"/>.</summary>
    /// <param name="destination">Deskswap's top-level window that draws it.</param>
    /// <param name="source">The window to preview.</param>
    /// <returns>The thumbnail, or <see langword="null"/> if the compositor refused (e.g. the window is gone).</returns>
    public static WindowThumbnail? TryCreate(nint destination, nint source)
    {
        var result = PInvoke.DwmRegisterThumbnail(new HWND(destination), new HWND(source), out nint thumbnail);
        if (result.Failed)
        {
            return null;
        }

        var created = new WindowThumbnail(thumbnail, source);
        created.Refresh();
        return created;
    }

    /// <summary>
    /// Reads again which part of the source window to show, after it was resized, maximized, restored or minimized.
    /// </summary>
    public void Refresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sourceArea = null;
        HasContent = false;
        if (PInvoke.DwmQueryThumbnailSourceSize(_thumbnail, out SIZE size).Failed || size.cx <= 0 || size.cy <= 0)
        {
            return;
        }

        bool minimized = WindowInspector.IsMinimized(_source);
        HasContent = size.cx >= MinimumContentSize && size.cy >= MinimumContentSize;
        if (minimized && HasContent)
        {
            // Real content has the size of the restored window (give or take DPI scaling); the stand-in doesn't.
            HasContent = WindowInspector.TryGetRestoredSize(_source, out int width, out int height)
                && size.cx >= width * 0.4 && size.cy >= height * 0.4;
        }
        Borders? borders = null;
        if (!minimized && WindowInspector.TryGetFrame(_source, out var visible, out var outer) && outer.Width > 0 && outer.Height > 0)
        {
            // In the preview's pixels, which differ from screen pixels for windows Windows scales for DPI.
            double sx = size.cx / (double)outer.Width;
            double sy = size.cy / (double)outer.Height;
            borders = new Borders(
                (int)Math.Round((visible.Left - outer.Left) * sx),
                (int)Math.Round((visible.Top - outer.Top) * sy),
                (int)Math.Round((outer.Right - visible.Right) * sx),
                (int)Math.Round((outer.Bottom - visible.Bottom) * sy));
            Remember(borders.Value);
        }
        else if (s_knownBorders.TryGetValue(_source, out var known))
        {
            borders = known;
        }

        if (borders is { } b && size.cx - b.Left - b.Right > 0 && size.cy - b.Top - b.Bottom > 0)
        {
            _sourceArea = new RECT(b.Left, b.Top, size.cx - b.Right, size.cy - b.Bottom);
        }
    }

    /// <summary>Places the thumbnail, or hides it.</summary>
    /// <param name="destination">Where to draw it, in the destination window's client pixels.</param>
    /// <param name="opacity">0 (transparent) to 255 (opaque).</param>
    /// <param name="visible">Whether to draw it at all.</param>
    /// <returns><see langword="true"/> if the compositor accepted the update.</returns>
    public bool Update(ScreenRect destination, byte opacity = 255, bool visible = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var flags = PInvoke.DWM_TNP_RECTDESTINATION | PInvoke.DWM_TNP_OPACITY | PInvoke.DWM_TNP_VISIBLE | PInvoke.DWM_TNP_SOURCECLIENTAREAONLY;
        var properties = new DWM_THUMBNAIL_PROPERTIES
        {
            rcDestination = new RECT(destination.Left, destination.Top, destination.Right, destination.Bottom),
            opacity = opacity,
            fVisible = visible,
            fSourceClientAreaOnly = false,
        };

        if (_sourceArea is { } area)
        {
            flags |= PInvoke.DWM_TNP_RECTSOURCE;
            properties.rcSource = area;
        }

        properties.dwFlags = flags;
        return PInvoke.DwmUpdateThumbnailProperties(_thumbnail, in properties).Succeeded;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _ = PInvoke.DwmUnregisterThumbnail(_thumbnail);
        }
    }

    private void Remember(Borders borders)
    {
        if (s_knownBorders.Count >= MaxRememberedBorders && !s_knownBorders.ContainsKey(_source))
        {
            s_knownBorders.Clear(); // Closed windows' entries; rebuilt as windows are previewed again.
        }

        s_knownBorders[_source] = borders;
    }

    private readonly record struct Borders(int Left, int Top, int Right, int Bottom);
}
