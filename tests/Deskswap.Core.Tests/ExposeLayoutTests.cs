namespace Deskswap.Core.Tests;

/// <summary>The overview's window arrangement (overview-ux.md §3).</summary>
public class ExposeLayoutTests
{
    private const int Caption = 30;
    private static readonly ScreenRect Area = new(100, 150, 1820, 1000);

    private static readonly ScreenRect[] Overlapping =
    [
        new(0, 0, 1920, 1040),        // maximized
        new(200, 100, 1200, 800),
        new(900, 300, 1800, 1000),
        new(400, 400, 1000, 900),
    ];

    [Fact]
    public void Previews_and_captions_do_not_overlap_and_stay_inside_the_area()
    {
        var tiles = ExposeLayout.Arrange(Overlapping, Area, captionHeight: Caption);

        Assert.Equal(Overlapping.Length, tiles.Count);
        foreach (var tile in tiles)
        {
            Assert.True(
                tile.Left >= Area.Left && tile.Top >= Area.Top && tile.Right <= Area.Right + 1 && tile.Bottom + Caption <= Area.Bottom + 1,
                $"{tile} outside {Area}");
        }

        // A tile plus its caption must not run into another tile.
        AssertNoOverlap([.. tiles.Select(t => t with { Bottom = t.Bottom + Caption })]);
    }

    [Fact]
    public void Previews_keep_each_windows_aspect_ratio_and_relative_size()
    {
        var tiles = ExposeLayout.Arrange(Overlapping, Area, captionHeight: Caption);

        double scale = tiles[0].Width / (double)Overlapping[0].Width;
        for (int i = 0; i < tiles.Count; i++)
        {
            Assert.InRange(tiles[i].Width / (double)Overlapping[i].Width, scale * 0.98, scale * 1.02);
            Assert.InRange(tiles[i].Height / (double)Overlapping[i].Height, scale * 0.98, scale * 1.02);
        }
    }

    [Fact]
    public void Windows_keep_their_left_to_right_order()
    {
        ScreenRect[] windows = [new(1100, 100, 1900, 700), new(0, 100, 800, 700)];

        var tiles = ExposeLayout.Arrange(windows, Area);

        Assert.True(tiles[1].Right <= tiles[0].Left);
    }

    [Fact]
    public void Windows_keep_their_top_to_bottom_order_when_in_several_rows()
    {
        var windows = Enumerable.Range(0, 6).Select(i => ScreenRect.FromSize(100 * (i % 3), 300 * (i / 3), 900, 700)).ToArray();

        var tiles = ExposeLayout.Arrange(windows, Area, captionHeight: Caption);

        // The bottom three windows on screen are not above the top three in the overview.
        Assert.True(tiles.Take(3).Max(t => t.Top) <= tiles.Skip(3).Min(t => t.Top));
    }

    [Fact]
    public void Windows_at_the_same_spot_are_separated()
    {
        ScreenRect[] windows = [new(100, 100, 900, 700), new(100, 100, 900, 700), new(100, 100, 900, 700)];

        AssertNoOverlap(ExposeLayout.Arrange(windows, Area));
    }

    [Fact]
    public void A_single_window_is_not_shown_larger_than_the_maximum_scale()
    {
        ScreenRect[] windows = [new(0, 0, 800, 600)];

        var tile = Assert.Single(ExposeLayout.Arrange(windows, Area, maxScale: 0.85));

        Assert.InRange(tile.Width, 670, 690);
    }

    [Fact]
    public void Previews_use_the_space_well()
    {
        var tiles = ExposeLayout.Arrange(Overlapping, Area, captionHeight: Caption);

        double used = tiles.Sum(t => (double)t.Width * t.Height);
        double available = (double)Area.Width * Area.Height;
        Assert.True(used / available > 0.3, $"Previews cover only {used / available:P0} of the area.");
    }

    [Fact]
    public void Same_input_gives_same_output()
    {
        Assert.Equal(ExposeLayout.Arrange(Overlapping, Area), ExposeLayout.Arrange(Overlapping, Area));
    }

    [Fact]
    public void No_windows_gives_no_tiles()
    {
        Assert.Empty(ExposeLayout.Arrange([], Area));
    }

    [Fact]
    public void Many_windows_still_fit()
    {
        var windows = Enumerable.Range(0, 20).Select(i => ScreenRect.FromSize(i * 40, i * 20, 900, 700)).ToArray();

        var tiles = ExposeLayout.Arrange(windows, Area, captionHeight: Caption);

        Assert.All(tiles, t => Assert.True(t.Width > 150, $"{t} is too small"));
        AssertNoOverlap([.. tiles.Select(t => t with { Bottom = t.Bottom + Caption })]);
    }

    private static void AssertNoOverlap(IReadOnlyList<ScreenRect> tiles)
    {
        for (int i = 0; i < tiles.Count; i++)
        {
            for (int j = i + 1; j < tiles.Count; j++)
            {
                var a = tiles[i];
                var b = tiles[j];
                bool overlap = a.Left < b.Right - 1 && b.Left < a.Right - 1 && a.Top < b.Bottom - 1 && b.Top < a.Bottom - 1;
                Assert.False(overlap, $"{a} overlaps {b}");
            }
        }
    }
}
