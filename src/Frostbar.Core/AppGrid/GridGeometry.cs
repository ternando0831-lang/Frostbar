namespace Frostbar.Core.AppGrid;

/// <summary>A rectangle in DIPs.</summary>
public readonly record struct Box(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;

    /// <summary>The box scaled by <paramref name="k"/> about its centre (a pressed icon giving).</summary>
    public Box Scaled(double k) => k == 1 ? this
        : new(X + Width * (1 - k) / 2, Y + Height * (1 - k) / 2, Width * k, Height * k);
}

/// <summary>A page's layout: <see cref="Columns"/> × <see cref="Rows"/> cells, the icon size, and where the field and the page dots go.</summary>
public sealed record GridLayout(int Columns, int Rows, Box Cells, double IconSize, Box Field, double DotsY)
{
    public int PerPage => Columns * Rows;
    public double CellWidth => Cells.Width / Columns;
    public double CellHeight => Cells.Height / Rows;
}

/// <summary>
/// Where things go on a page of the app grid (DIPs inside the overlay, which starts under the menu bar): the search
/// field centred at the top, the cells in the middle with wide side margins (Launchpad), the page dots between them
/// and the Dock. Icons are a share of the smaller cell side, in 4 DIP steps (whole pixels at 125%).
/// </summary>
public static class GridGeometry
{
    public const double FieldWidth = 240, FieldHeight = 30, FieldTop = 32, TopReserve = 96, BottomReserve = 64, DotsAboveBottom = 34;
    public const double LabelGap = 8, LabelHeight = 18;

    /// <param name="width">The overlay's width.</param>
    /// <param name="height">
    /// From the overlay's top to the top of the Dock's strip (the work area's bottom): the overlay itself goes on under
    /// the Dock, but nothing of the grid should be hidden behind it.
    /// </param>
    /// <param name="iconSize">
    /// The icons' size the user chose (DIPs; 0: as large as the cells allow nicely). It is kept to 4 DIP steps and to
    /// what fits a cell with its name under it, so that icons never overlap (fewer columns or rows make room for more).
    /// </param>
    public static GridLayout Compute(double width, double height, int columns = 7, int rows = 5, int iconSize = 0)
    {
        double side = Math.Round(width * 0.11);
        var cells = new Box(side, TopReserve, Math.Max(1, width - 2 * side), Math.Max(1, height - TopReserve - BottomReserve));
        double cell = Math.Min(cells.Width / columns, cells.Height / rows);
        double fits = Math.Floor(Math.Min(cells.Width / columns - 2 * IconRoom, cells.Height / rows - LabelGap - LabelHeight - 2 * IconRoom) / 4) * 4;
        // The automatic size too stays within what a cell holds with its name: at 8 rows on a 150% screen its least, 40,
        // ran into the names of the row above.
        double icon = Math.Min(Math.Clamp(Math.Floor(cell * 0.6 / 4) * 4, 40, 96), fits);
        if (iconSize > 0) icon = Math.Min(Math.Floor(iconSize / 4.0) * 4, fits);
        icon = Math.Max(MinIcon, icon);
        var field = new Box(Math.Round((width - FieldWidth) / 2), FieldTop, FieldWidth, FieldHeight);
        return new GridLayout(columns, rows, cells, icon, field, height - DotsAboveBottom);
    }

    /// <summary>The icon sizes the settings offer (DIPs).</summary>
    public const int MinIcon = 32, MaxIcon = 160;

    /// <summary>Room kept around a chosen icon inside its cell (DIPs): the pressed shade and the selection reach past it.</summary>
    const double IconRoom = 6;

    /// <summary>The cell of item <paramref name="index"/> of a page (0 = top left, row by row).</summary>
    public static Box Cell(GridLayout l, int index)
    {
        int col = index % l.Columns, row = index / l.Columns;
        return new Box(l.Cells.X + col * l.CellWidth, l.Cells.Y + row * l.CellHeight, l.CellWidth, l.CellHeight);
    }

    /// <summary>Icon and label of item <paramref name="index"/>: centred in the cell together.</summary>
    public static (Box icon, Box label) Item(GridLayout l, int index)
    {
        var c = Cell(l, index);
        double block = l.IconSize + LabelGap + LabelHeight;
        double top = c.Y + (c.Height - block) / 2;
        var icon = new Box(c.X + (c.Width - l.IconSize) / 2, top, l.IconSize, l.IconSize);
        var label = new Box(c.X + 4, top + l.IconSize + LabelGap, c.Width - 8, LabelHeight);
        return (icon, label);
    }

    /// <summary>The item index on a page under (<paramref name="x"/>, <paramref name="y"/>): its icon or its label; -1 elsewhere.</summary>
    public static int HitTest(GridLayout l, double x, double y, int itemsOnPage)
    {
        if (!l.Cells.Contains(x, y)) return -1;
        int col = (int)((x - l.Cells.X) / l.CellWidth), row = (int)((y - l.Cells.Y) / l.CellHeight);
        int index = row * l.Columns + col;
        if (index < 0 || index >= itemsOnPage) return -1;
        var (icon, label) = Item(l, index);
        var hit = new Box(icon.X - 6, icon.Y - 6, icon.Width + 12, label.Bottom - icon.Y + 12);
        return hit.Contains(x, y) || label.Contains(x, y) ? index : -1;
    }

    /// <summary>The page dots' centres (x) for <paramref name="pages"/> pages, centred, <paramref name="spacing"/> apart.</summary>
    public static double[] Dots(double width, int pages, double spacing = 18) =>
        Enumerable.Range(0, pages).Select(i => width / 2 + (i - (pages - 1) / 2.0) * spacing).ToArray();
}
