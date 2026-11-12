using Frostbar.Core.AppGrid;

namespace Frostbar.Core.Dock;

/// <summary>The screen's edge the Dock sits on (macOS's "Position on screen").</summary>
public enum DockEdge { Bottom, Left, Right }

/// <summary>
/// The Dock's window seen from its edge (stage 7): positions <em>along</em> the row (x at the bottom, y on a side) and
/// <em>inward</em> from the screen's edge (up at the bottom, rightwards on the left, leftwards on the right), and the
/// window's own DIPs. The Dock lays its row out along (<see cref="DockGeometry"/> is one-dimensional) and draws through
/// this, so that one layout serves the three edges.
/// </summary>
public readonly record struct DockFrame(DockEdge Edge, double Width, double Height)
{
    public bool Vertical => Edge != DockEdge.Bottom;

    /// <summary>The row's room: the window's width at the bottom, its height on a side.</summary>
    public double Length => Vertical ? Height : Width;

    /// <summary>How far the window reaches in from the screen's edge.</summary>
    public double Depth => Vertical ? Width : Height;

    /// <summary>A point <paramref name="along"/> the row, <paramref name="inward"/> from the screen's edge, in the window.</summary>
    public (double X, double Y) Point(double along, double inward) => Edge switch
    {
        DockEdge.Bottom => (along, Height - inward),
        DockEdge.Left => (inward, along),
        _ => (Width - inward, along),
    };

    /// <summary>
    /// A box in the window from its place on the row: it starts <paramref name="along"/> and runs
    /// <paramref name="length"/> along the row; its side nearest the screen's edge is <paramref name="inward"/> in, and it
    /// reaches <paramref name="depth"/> further in.
    /// </summary>
    public Box Box(double along, double inward, double length, double depth) => Edge switch
    {
        DockEdge.Bottom => new Box(along, Height - inward - depth, length, depth),
        DockEdge.Left => new Box(inward, along, depth, length),
        _ => new Box(Width - inward - depth, along, depth, length),
    };

    /// <summary>A point of the window as (along, inward).</summary>
    public (double Along, double Inward) Of(double x, double y) => Edge switch
    {
        DockEdge.Bottom => (x, Height - y),
        DockEdge.Left => (y, x),
        _ => (y, Width - x),
    };

    /// <summary>
    /// Where a menu or a stack of <paramref name="width"/> × <paramref name="height"/> opens for the Dock's item at
    /// <paramref name="item"/> (DIPs on the screen): above it at the bottom, beside it on a side (macOS), <paramref name="gap"/>
    /// off the icon, kept on <paramref name="screen"/>. Returns its top left corner.
    /// </summary>
    public static (double Left, double Top) Beside(DockEdge edge, Box item, double width, double height, Box screen, double gap = 10)
    {
        double Along(double centre, double size, double min, double max) => Math.Clamp(centre - size / 2, min + 4, Math.Max(min + 4, max - size - 4));
        return edge switch
        {
            DockEdge.Left => (Math.Min(item.Right + gap, Math.Max(screen.X + 4, screen.Right - width - 4)), Along(item.Y + item.Height / 2, height, screen.Y, screen.Bottom)),
            DockEdge.Right => (Math.Max(screen.X + 4, item.X - gap - width), Along(item.Y + item.Height / 2, height, screen.Y, screen.Bottom)),
            _ => (Along(item.X + item.Width / 2, width, screen.X, screen.Right), Math.Max(screen.Y + 4, item.Y - gap - height)),
        };
    }

    public static DockEdge Parse(string? position) => position switch
    {
        "left" => DockEdge.Left,
        "right" => DockEdge.Right,
        _ => DockEdge.Bottom,
    };

    public static string Name(DockEdge edge) => edge switch
    {
        DockEdge.Left => "left",
        DockEdge.Right => "right",
        _ => "bottom",
    };
}
