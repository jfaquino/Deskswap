namespace Deskswap.Platform.Tests;

/// <summary>Overview previews (overview-ux.md §9): captured images are shrunk in memory.</summary>
public class PixelImageTests
{
    [Fact]
    public void Downscale_averages_blocks_of_pixels()
    {
        // 4×2 image: left half black, right half white.
        var pixels = new byte[4 * 2 * 4];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 2; x < 4; x++)
            {
                int i = ((y * 4) + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
            }
        }

        var small = new PixelImage(4, 2, pixels).Downscale(2);

        Assert.Equal(2, small.Width);
        Assert.Equal(1, small.Height);
        Assert.Equal(0, small.Pixels[0]);
        Assert.Equal(255, small.Pixels[4]);
    }

    [Fact]
    public void Crop_copies_the_rectangle_and_clips_to_the_image()
    {
        var pixels = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++)
        {
            pixels[i * 4] = (byte)i; // Blue channel = pixel index.
        }

        var crop = new PixelImage(4, 4, pixels).Crop(new Core.ScreenRect(2, 1, 9, 3));

        Assert.NotNull(crop);
        Assert.Equal(2, crop.Width);
        Assert.Equal(2, crop.Height);
        Assert.Equal(6, crop.Pixels[0]);   // (2,1)
        Assert.Equal(11, crop.Pixels[12]); // (3,2)
    }

    [Fact]
    public void Crop_outside_the_image_is_null()
    {
        Assert.Null(new PixelImage(4, 4, new byte[64]).Crop(new Core.ScreenRect(10, 10, 20, 20)));
    }

    [Fact]
    public void Downscale_keeps_an_image_that_is_small_enough()
    {
        var image = new PixelImage(2, 2, new byte[16]);

        Assert.Same(image, image.Downscale(10));
    }
}
