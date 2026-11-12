namespace Frostbar.Core.Overview;

/// <summary>A rectangle (pixels or DIPs, as given).</summary>
public readonly record struct Rect2(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>
/// Where the window overview (M7, Mission Control) puts each window's thumbnail: rows filled in the given order,
/// each window keeping its shape and never drawn larger than it is. Windows much larger than the others are first
/// brought down to about the size of a typical one (the median area), so that one maximized window does not make all
/// the others tiny; then one scale, as large as fits, is used for all of them. Rows are centred, and so is the block.
/// </summary>
public static class OverviewLayout
{
    /// <param name="windows">Window sizes, in the order to show them.</param>
    /// <param name="area">Where the thumbnails may go.</param>
    /// <param name="gap">Space between thumbnails (and rows).</param>
    public static IReadOnlyList<Rect2> Arrange(IReadOnlyList<(double Width, double Height)> windows, Rect2 area, double gap = 32)
    {
        int n = windows.Count;
        if (n == 0 || area.Width <= 0 || area.Height <= 0) return [];
        // Pre-scale: no larger than the median area (keeps smaller windows as they are).
        var areas = windows.Select(w => Math.Max(1, w.Width * w.Height)).OrderBy(a => a).ToArray();
        double median = areas[n / 2];
        var sizes = windows.Select(w =>
        {
            double width = Math.Max(1, w.Width), height = Math.Max(1, w.Height);
            double k = Math.Min(1, Math.Sqrt(median / (width * height)));
            return (w: width * k, h: height * k, cap: 1 / k); // cap: how far it may grow back, at most its real size
        }).ToArray();

        // The largest common scale whose rows fit (binary search; rows are filled greedily in order).
        double lo = 0, hi = sizes.Min(s => s.cap);
        if (Fits(sizes, area, gap, hi)) lo = hi;
        else
            for (int i = 0; i < 40; i++)
            {
                double mid = (lo + hi) / 2;
                if (Fits(sizes, area, gap, mid)) lo = mid; else hi = mid;
            }
        double s = lo;
        var rows = Rows(sizes, area.Width, gap, s);
        double blockHeight = rows.Sum(r => r.height) + gap * (rows.Count - 1);
        double y = area.Y + Math.Max(0, (area.Height - blockHeight) / 2);
        var result = new Rect2[n];
        foreach (var row in rows)
        {
            double x = area.X + Math.Max(0, (area.Width - row.width) / 2);
            for (int i = row.from; i < row.to; i++)
            {
                double w = sizes[i].w * s, h = sizes[i].h * s;
                result[i] = new Rect2(x, y + (row.height - h) / 2, w, h);
                x += w + gap;
            }
            y += row.height + gap;
        }
        return result;
    }

    static bool Fits((double w, double h, double cap)[] sizes, Rect2 area, double gap, double s)
    {
        if (sizes.Any(z => z.w * s > area.Width)) return false;
        var rows = Rows(sizes, area.Width, gap, s);
        return rows.Sum(r => r.height) + gap * (rows.Count - 1) <= area.Height;
    }

    /// <summary>Rows filled in order: a window goes to the next row when it would make this one wider than the area.</summary>
    static List<(int from, int to, double width, double height)> Rows((double w, double h, double cap)[] sizes, double width, double gap, double s)
    {
        var rows = new List<(int, int, double, double)>();
        int start = 0;
        double rowWidth = 0, rowHeight = 0;
        for (int i = 0; i < sizes.Length; i++)
        {
            double w = sizes[i].w * s, h = sizes[i].h * s;
            double next = i == start ? w : rowWidth + gap + w;
            if (i > start && next > width)
            {
                rows.Add((start, i, rowWidth, rowHeight));
                start = i;
                rowWidth = w;
                rowHeight = h;
                continue;
            }
            rowWidth = next;
            rowHeight = Math.Max(rowHeight, h);
        }
        rows.Add((start, sizes.Length, rowWidth, rowHeight));
        return rows;
    }

    /// <summary>
    /// The order to lay the windows out in: top to bottom by where their centres are (in bands of
    /// <paramref name="band"/>), left to right within a band, so each thumbnail lands about where its window is
    /// (macOS keeps the windows' places in the same way). Ties keep the given order (front to back).
    /// </summary>
    public static int[] Order(IReadOnlyList<Rect2> windows, double band) =>
        Enumerable.Range(0, windows.Count)
            .OrderBy(i => band > 0 ? Math.Floor((windows[i].Y + windows[i].Height / 2) / band) : 0)
            .ThenBy(i => windows[i].X + windows[i].Width / 2)
            .ThenBy(i => i)
            .ToArray();

    /// <summary>
    /// Where the close button of the window pointed at stands (stage 3-3; the user's choice 2026-10-16: its thumbnail's
    /// top right corner, over the window's own close button): a circle of <paramref name="diameter"/> whose centre is
    /// <paramref name="inset"/> in from that corner, kept within the thumbnail (a narrow or short one). The same units
    /// as <paramref name="thumb"/>.
    /// </summary>
    public static Rect2 CloseButton(Rect2 thumb, double diameter, double inset)
    {
        double r = diameter / 2;
        double cx = Math.Max(thumb.X + r, thumb.Right - inset), cy = Math.Min(Math.Max(thumb.Y + r, thumb.Bottom - r), thumb.Y + inset);
        return new Rect2(cx - r, cy - r, diameter, diameter);
    }

    /// <summary>The thumbnail under (<paramref name="x"/>, <paramref name="y"/>), or -1.</summary>
    public static int HitTest(IReadOnlyList<Rect2> thumbs, double x, double y)
    {
        for (int i = 0; i < thumbs.Count; i++) if (thumbs[i].Contains(x, y)) return i;
        return -1;
    }

    /// <summary>
    /// The thumbnail an arrow key moves to from <paramref name="from"/>: the nearest one whose centre lies that way
    /// (distance across the arrow counts double), or <paramref name="from"/> when there is none. -1 goes to the first.
    /// </summary>
    public static int Move(IReadOnlyList<Rect2> thumbs, int from, int dx, int dy)
    {
        if (thumbs.Count == 0) return -1;
        if (from < 0 || from >= thumbs.Count) return 0;
        var c = Centre(thumbs[from]);
        int best = from;
        double bestCost = double.MaxValue;
        for (int i = 0; i < thumbs.Count; i++)
        {
            if (i == from) continue;
            var p = Centre(thumbs[i]);
            double along = (p.x - c.x) * dx + (p.y - c.y) * dy, across = Math.Abs((p.x - c.x) * dy) + Math.Abs((p.y - c.y) * dx);
            if (along <= 1) continue;
            double cost = along + 2 * across;
            if (cost < bestCost) { bestCost = cost; best = i; }
        }
        return best;
    }

    static (double x, double y) Centre(Rect2 r) => (r.X + r.Width / 2, r.Y + r.Height / 2);
}
