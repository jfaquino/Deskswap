using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Deskswap.Platform;

namespace Deskswap.Ui;

/// <summary>Converts native images into WPF image sources.</summary>
internal static class Images
{
    private static readonly Dictionary<nint, ImageSource?> Icons = [];
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PixelImage, BitmapSource> Bitmaps = [];
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, BitmapSource> BlurCache = [];
    private static readonly Dictionary<string, BitmapSource?> WallpaperCache = [];

    /// <summary>Wraps captured pixels as a frozen bitmap.</summary>
    /// <param name="image">The image.</param>
    /// <returns>The bitmap.</returns>
    public static BitmapSource ToBitmap(PixelImage image) => Bitmaps.GetValue(image, Create);

    private static BitmapSource Create(PixelImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgr32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Decodes a wallpaper image once (per file version) at a size good enough for a full screen.</summary>
    /// <param name="wallpaper">The wallpaper.</param>
    /// <returns>The image, or <see langword="null"/> for a solid color or an unreadable file.</returns>
    public static BitmapSource? LoadWallpaper(Wallpaper? wallpaper)
    {
        if (wallpaper?.ImagePath is not { } path)
        {
            return null;
        }

        string key;
        try
        {
            key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (WallpaperCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        BitmapSource? image = null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.DecodePixelWidth = 1600; // Sharp enough behind a blur and in cards; far cheaper than 4K or 8K.
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
        }
        catch (IOException)
        {
            // Missing or locked: fall back to the color.
        }
        catch (NotSupportedException)
        {
            // Not an image format WPF can decode.
        }
        catch (UnauthorizedAccessException)
        {
            // No access to the file.
        }

        if (WallpaperCache.Count >= 8)
        {
            WallpaperCache.Clear(); // Old versions of changed wallpapers; only the current ones are worth keeping.
        }

        WallpaperCache[key] = image;
        return image;
    }

    /// <summary>Draws a wallpaper the way Windows fits it to a monitor.</summary>
    /// <param name="wallpaper">The wallpaper, or <see langword="null"/> if unknown.</param>
    /// <param name="fallback">The color to use when nothing is known.</param>
    /// <returns>An element that fills its parent.</returns>
    public static FrameworkElement WallpaperView(Wallpaper? wallpaper, Brush fallback)
    {
        var grid = new Grid
        {
            Background = wallpaper is null ? fallback : new SolidColorBrush(Color.FromRgb(wallpaper.BackgroundRed, wallpaper.BackgroundGreen, wallpaper.BackgroundBlue)),
            ClipToBounds = true,
        };
        if (LoadWallpaper(wallpaper) is { } image)
        {
            var view = new System.Windows.Controls.Image
            {
                Source = image,
                Stretch = wallpaper!.Fit switch
                {
                    WallpaperFit.Fit or WallpaperFit.Center => Stretch.Uniform,
                    WallpaperFit.Stretch => Stretch.Fill,
                    _ => Stretch.UniformToFill,
                },
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(view, BitmapScalingMode.HighQuality);
            grid.Children.Add(view);
        }

        return grid;
    }

    /// <summary>Makes a heavily blurred, small copy of a bitmap (e.g. the wallpaper) for the overview's backdrop.</summary>
    /// <param name="source">The bitmap.</param>
    /// <returns>The blurred bitmap.</returns>
    public static BitmapSource Blurred(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (BlurCache.TryGetValue(source, out var cached))
        {
            return cached;
        }

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        var blurred = Blurred(new PixelImage(width, height, pixels));
        BlurCache.AddOrUpdate(source, blurred);
        return blurred;
    }

    /// <summary>
    /// Makes a heavily blurred, small copy of an image for the overview's backdrop. Blurring once on a small image
    /// and letting the GPU stretch it is far cheaper than a blur effect re-rendered on every animation frame.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <returns>The blurred bitmap.</returns>
    public static BitmapSource Blurred(PixelImage image)
    {
        var small = image.Downscale(160);
        int width = small.Width;
        int height = small.Height;
        var a = (byte[])small.Pixels.Clone();
        var b = new byte[a.Length];

        // Three box-blur passes in each direction approximate a Gaussian blur.
        for (int pass = 0; pass < 3; pass++)
        {
            BoxBlur(a, b, width, height, radius: 3, horizontal: true);
            BoxBlur(b, a, width, height, radius: 3, horizontal: false);
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, a, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void BoxBlur(byte[] source, byte[] target, int width, int height, int radius, bool horizontal)
    {
        int lines = horizontal ? height : width;
        int length = horizontal ? width : height;
        int step = horizontal ? 4 : width * 4;
        for (int line = 0; line < lines; line++)
        {
            int start = horizontal ? line * width * 4 : line * 4;
            for (int channel = 0; channel < 3; channel++)
            {
                int sum = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    sum += source[start + (Math.Clamp(k, 0, length - 1) * step) + channel];
                }

                for (int k = 0; k < length; k++)
                {
                    target[start + (k * step) + channel] = (byte)(sum / ((2 * radius) + 1));
                    int leaving = Math.Clamp(k - radius, 0, length - 1);
                    int entering = Math.Clamp(k + radius + 1, 0, length - 1);
                    sum += source[start + (entering * step) + channel] - source[start + (leaving * step) + channel];
                }
            }

            for (int k = 0; k < length; k++)
            {
                target[start + (k * step) + 3] = 255;
            }
        }
    }

    /// <summary>Gets an image for a window icon handle, cached per handle.</summary>
    /// <param name="icon">The icon handle (not owned), or 0.</param>
    /// <returns>The image, or <see langword="null"/> if there is no usable icon.</returns>
    public static ImageSource? FromIcon(nint icon)
    {
        if (icon == 0)
        {
            return null;
        }

        if (Icons.TryGetValue(icon, out var cached))
        {
            return cached;
        }

        ImageSource? image = null;
        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            image = bitmap;
        }
        catch (COMException)
        {
            // The handle was destroyed meanwhile; show no icon.
        }
        catch (ArgumentException)
        {
            // Not an icon handle.
        }

        Icons[icon] = image;
        return image;
    }

    /// <summary>Forgets cached icons (handles may be reused after windows close).</summary>
    public static void ClearIconCache() => Icons.Clear();
}
