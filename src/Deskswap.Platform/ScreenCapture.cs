using Deskswap.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Deskswap.Platform;

/// <summary>An image in memory: 32-bit BGRA pixels, top row first, with no padding between rows.</summary>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
/// <param name="Pixels">The pixels, <c>Width * Height * 4</c> bytes.</param>
public sealed record PixelImage(int Width, int Height, byte[] Pixels)
{
    /// <summary>Gets the number of bytes per row.</summary>
    public int Stride => Width * 4;

    /// <summary>Copies a rectangle out of the image (clipped to the image).</summary>
    /// <param name="area">The rectangle, in this image's pixels.</param>
    /// <returns>The copy, or <see langword="null"/> if the rectangle is outside the image.</returns>
    public PixelImage? Crop(ScreenRect area)
    {
        int left = Math.Max(0, area.Left);
        int top = Math.Max(0, area.Top);
        int right = Math.Min(Width, area.Right);
        int bottom = Math.Min(Height, area.Bottom);
        if (right - left < 2 || bottom - top < 2)
        {
            return null;
        }

        int width = right - left;
        int height = bottom - top;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            Array.Copy(Pixels, ((top + y) * Stride) + (left * 4), pixels, y * width * 4, width * 4);
        }

        return new PixelImage(width, height, pixels);
    }

    /// <summary>Makes a smaller copy by averaging blocks of pixels (cheap and smooth for whole-number factors).</summary>
    /// <param name="maxWidth">The largest width of the result.</param>
    /// <returns>The smaller image, or this one if it is already small enough.</returns>
    public PixelImage Downscale(int maxWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        if (Width <= maxWidth)
        {
            return this;
        }

        int factor = (int)Math.Ceiling(Width / (double)maxWidth);
        int width = Width / factor;
        int height = Math.Max(1, Height / factor);
        var pixels = new byte[width * height * 4];
        int count = factor * factor;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int b = 0, g = 0, r = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int row = (((y * factor) + dy) * Stride) + (x * factor * 4);
                    for (int dx = 0; dx < factor; dx++)
                    {
                        int i = row + (dx * 4);
                        b += Pixels[i];
                        g += Pixels[i + 1];
                        r += Pixels[i + 2];
                    }
                }

                int o = ((y * width) + x) * 4;
                pixels[o] = (byte)(b / count);
                pixels[o + 1] = (byte)(g / count);
                pixels[o + 2] = (byte)(r / count);
                pixels[o + 3] = 255;
            }
        }

        return new PixelImage(width, height, pixels);
    }
}

/// <summary>
/// Captures what a monitor shows, downscaled, for the overview's desktop previews and backdrop (overview-ux.md §9).
/// Images are kept in memory only, never written to disk. Deskswap's own windows stay visible to other capture
/// tools, so callers take snapshots only while those windows are hidden (after <see cref="WaitForCompositor"/>), or
/// hide them from capture for that moment (<see cref="WindowStyling.SetHiddenFromCapture"/>).
/// </summary>
public static class ScreenCapture
{
    private const int Halftone = 4;

    /// <summary>
    /// Waits until the compositor has drawn the next frame, so windows just hidden (or hidden from capture) are no
    /// longer in what a capture reads. Takes up to one refresh interval.
    /// </summary>
    public static void WaitForCompositor() => _ = PInvoke.DwmFlush();

    /// <summary>Captures part of the screen, scaled down so it is at most <paramref name="maxWidth"/> pixels wide.</summary>
    /// <param name="source">The screen rectangle, in physical pixels.</param>
    /// <param name="maxWidth">The largest width of the result.</param>
    /// <returns>The image, or <see langword="null"/> if the screen couldn't be read (e.g. the session is locked).</returns>
    public static unsafe PixelImage? Capture(ScreenRect source, int maxWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        if (source.Width <= 0 || source.Height <= 0)
        {
            return null;
        }

        int width = Math.Min(source.Width, maxWidth);
        int height = Math.Max(1, (int)Math.Round(source.Height * (double)width / source.Width));

        var screen = PInvoke.GetDC(HWND.Null);
        if (screen.IsNull)
        {
            return null;
        }

        var memory = PInvoke.CreateCompatibleDC(screen);
        try
        {
            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(BITMAPINFOHEADER),
                    biWidth = width,
                    biHeight = -height, // Top-down rows.
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                },
            };

            void* bits;
            var bitmap = PInvoke.CreateDIBSection(memory, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
            if (bitmap.IsNull || bits is null)
            {
                return null;
            }

            try
            {
                var previous = PInvoke.SelectObject(memory, bitmap);
                _ = PInvoke.SetStretchBltMode(memory, (STRETCH_BLT_MODE)Halftone);
                _ = PInvoke.SetBrushOrgEx(memory, 0, 0, null);
                bool copied = PInvoke.StretchBlt(
                    memory, 0, 0, width, height,
                    screen, source.Left, source.Top, source.Width, source.Height,
                    ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT);
                PInvoke.SelectObject(memory, previous);
                if (!copied)
                {
                    return null;
                }

                var pixels = new byte[width * height * 4];
                new ReadOnlySpan<byte>(bits, pixels.Length).CopyTo(pixels);
                return new PixelImage(width, height, pixels);
            }
            finally
            {
                PInvoke.DeleteObject(bitmap);
            }
        }
        finally
        {
            PInvoke.DeleteDC(memory);
            _ = PInvoke.ReleaseDC(HWND.Null, screen);
        }
    }
}
