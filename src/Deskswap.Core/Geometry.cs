namespace Deskswap.Core;

/// <summary>A rectangle in physical screen pixels; <see cref="Right"/> and <see cref="Bottom"/> are exclusive.</summary>
/// <param name="Left">The left edge.</param>
/// <param name="Top">The top edge.</param>
/// <param name="Right">The right edge (exclusive).</param>
/// <param name="Bottom">The bottom edge (exclusive).</param>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Gets the width.</summary>
    public int Width => Right - Left;

    /// <summary>Gets the height.</summary>
    public int Height => Bottom - Top;

    /// <summary>Creates a rectangle from a position and a size.</summary>
    /// <param name="left">The left edge.</param>
    /// <param name="top">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <returns>The rectangle.</returns>
    public static ScreenRect FromSize(int left, int top, int width, int height) => new(left, top, left + width, top + height);

    /// <summary>Returns this rectangle moved by an offset.</summary>
    /// <param name="dx">The horizontal offset.</param>
    /// <param name="dy">The vertical offset.</param>
    /// <returns>The moved rectangle.</returns>
    public ScreenRect Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);
}

/// <summary>Geometry used when windows travel with their desktop between monitors (design §3.5).</summary>
public static class WindowGeometry
{
    /// <summary>
    /// Maps a window rectangle from one monitor's work area to another's: the position is scaled proportionally,
    /// the size is kept (but never larger than the target), and the result is clamped inside the target.
    /// </summary>
    /// <param name="window">The window rectangle on the source monitor.</param>
    /// <param name="from">The source monitor's work area.</param>
    /// <param name="to">The target monitor's work area.</param>
    /// <returns>The window rectangle on the target monitor.</returns>
    public static ScreenRect Relocate(ScreenRect window, ScreenRect from, ScreenRect to)
    {
        int width = Math.Min(window.Width, to.Width);
        int height = Math.Min(window.Height, to.Height);

        double relativeX = from.Width > 0 ? (window.Left - from.Left) / (double)from.Width : 0;
        double relativeY = from.Height > 0 ? (window.Top - from.Top) / (double)from.Height : 0;

        int left = to.Left + (int)Math.Round(relativeX * to.Width);
        int top = to.Top + (int)Math.Round(relativeY * to.Height);

        left = Math.Clamp(left, to.Left, Math.Max(to.Left, to.Right - width));
        top = Math.Clamp(top, to.Top, Math.Max(to.Top, to.Bottom - height));

        return ScreenRect.FromSize(left, top, width, height);
    }
}
