namespace Deskswap.Core;

/// <summary>
/// Computes where each window's preview goes in the overview's window area (overview-ux.md §3), the way macOS
/// Mission Control does it: previews are as large as the space allows, keep their windows' relative sizes and aspect
/// ratios, and keep their order on screen (top windows in the top rows, left windows on the left), with room for a
/// caption under each. Pure and deterministic: the same input always gives the same tiles.
/// </summary>
public static class ExposeLayout
{
    /// <summary>Arranges window previews inside an area without overlap.</summary>
    /// <param name="windows">The windows' real rectangles, in any order.</param>
    /// <param name="area">The rectangle the previews and their captions must fit in.</param>
    /// <param name="gap">The minimum space between previews, horizontally and between rows.</param>
    /// <param name="captionHeight">Space reserved under each preview for its icon and title.</param>
    /// <param name="maxScale">The largest a preview may be relative to its window (e.g. 0.85).</param>
    /// <returns>One preview rectangle per window (captions excluded), in the same order as <paramref name="windows"/>.</returns>
    public static IReadOnlyList<ScreenRect> Arrange(
        IReadOnlyList<ScreenRect> windows,
        ScreenRect area,
        int gap = 24,
        int captionHeight = 0,
        double maxScale = 0.85)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentOutOfRangeException.ThrowIfNegative(gap);
        ArgumentOutOfRangeException.ThrowIfNegative(captionHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxScale);
        if (windows.Count == 0 || area.Width <= 0 || area.Height <= 0)
        {
            return [.. windows.Select(_ => new ScreenRect(area.Left, area.Top, area.Left, area.Top))];
        }

        var sizes = windows.Select(w => (W: (double)Math.Max(w.Width, 1), H: (double)Math.Max(w.Height, 1))).ToArray();

        // Reading order: by vertical center, then horizontal center (ties by input order, so it is deterministic).
        var byRow = Enumerable.Range(0, windows.Count)
            .OrderBy(i => windows[i].Top + (windows[i].Height / 2))
            .ThenBy(i => windows[i].Left + (windows[i].Width / 2))
            .ThenBy(i => i)
            .ToArray();

        List<int[]>? bestRows = null;
        double bestScale = -1;
        for (int rowCount = 1; rowCount <= windows.Count; rowCount++)
        {
            var rows = SplitIntoRows(byRow, sizes, rowCount);
            double scale = ScaleFor(rows, sizes, area, gap, captionHeight);

            // More rows only win if they make previews clearly larger; fewer rows keep the layout calmer.
            if (scale > bestScale * 1.02)
            {
                bestScale = scale;
                bestRows = rows;
            }
        }

        double s = Math.Min(bestScale, maxScale);
        if (s <= 0)
        {
            s = 0.01;
        }

        return Place(bestRows!, windows, sizes, area, gap, captionHeight, s);
    }

    /// <summary>Splits windows (already in reading order) into rows of similar total width.</summary>
    private static List<int[]> SplitIntoRows(int[] order, (double W, double H)[] sizes, int rowCount)
    {
        double total = order.Sum(i => sizes[i].W);
        double target = total / rowCount;
        var rows = new List<int[]>();
        var current = new List<int>();
        double width = 0;
        for (int k = 0; k < order.Length; k++)
        {
            int i = order[k];
            int remainingWindows = order.Length - k;
            int remainingRows = rowCount - rows.Count;

            // Close the row when adding this window overshoots the target more than leaving it out undershoots it,
            // but never leave a later row empty.
            bool overshoots = current.Count > 0 && width + sizes[i].W - target > target - width;
            bool mustClose = current.Count > 0 && remainingWindows <= remainingRows - 1;
            if ((overshoots || mustClose) && rows.Count < rowCount - 1)
            {
                rows.Add([.. current]);
                current.Clear();
                width = 0;
            }

            current.Add(i);
            width += sizes[i].W;
        }

        rows.Add([.. current]);
        return rows;
    }

    private static double ScaleFor(List<int[]> rows, (double W, double H)[] sizes, ScreenRect area, int gap, int captionHeight)
    {
        double heights = rows.Sum(row => row.Max(i => sizes[i].H));
        double fixedHeight = (rows.Count * captionHeight) + ((rows.Count - 1) * gap);
        double scale = (area.Height - fixedHeight) / heights;
        foreach (var row in rows)
        {
            double width = row.Sum(i => sizes[i].W);
            scale = Math.Min(scale, (area.Width - ((row.Length - 1) * gap)) / width);
        }

        return scale;
    }

    private static ScreenRect[] Place(
        List<int[]> rows,
        IReadOnlyList<ScreenRect> windows,
        (double W, double H)[] sizes,
        ScreenRect area,
        int gap,
        int captionHeight,
        double scale)
    {
        var result = new ScreenRect[windows.Count];
        double totalHeight = rows.Sum(row => (row.Max(i => sizes[i].H) * scale) + captionHeight) + ((rows.Count - 1) * gap);
        double y = area.Top + ((area.Height - totalHeight) / 2);
        foreach (var row in rows)
        {
            // Left to right by where the windows are on screen.
            var ordered = row.OrderBy(i => windows[i].Left + (windows[i].Width / 2)).ThenBy(i => i).ToArray();
            double rowHeight = ordered.Max(i => sizes[i].H) * scale;
            double rowWidth = ordered.Sum(i => sizes[i].W * scale) + ((ordered.Length - 1) * gap);
            double x = area.Left + ((area.Width - rowWidth) / 2);
            foreach (int i in ordered)
            {
                double w = sizes[i].W * scale;
                double h = sizes[i].H * scale;

                // Previews sit on a shared baseline so captions line up across the row.
                result[i] = ScreenRect.FromSize(
                    (int)Math.Round(x),
                    (int)Math.Round(y + (rowHeight - h)),
                    Math.Max(1, (int)Math.Round(w)),
                    Math.Max(1, (int)Math.Round(h)));
                x += w + gap;
            }

            y += rowHeight + captionHeight + gap;
        }

        return result;
    }
}
