using Frostbar.Core.Shell;

namespace Frostbar.Core.Wm;

/// <summary>Where the Window menu's Move &amp; Resize puts a window (stage 3-2, macOS's): halves, quarters, fill, centre.</summary>
public enum TilePlace { Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight, Fill, Center }

public static class TileLayout
{
    /// <summary>The room around and between tiled windows when the settings ask for margins (DIPs; macOS 15's "Tiled windows have margins").</summary>
    public const int MarginDip = 8;

    /// <summary>
    /// The frame (the visible one, pixels) a window takes in <paramref name="work"/> (its monitor's work area). Halves
    /// and quarters split it (an odd pixel goes to the right and lower ones); Fill is all of it, without maximizing;
    /// Center keeps the window's size (at most the work area's) in the middle. With a <paramref name="gap"/> (pixels:
    /// the settings' margins), the tiles and Fill keep that much room from the work area's edges and between each other.
    /// </summary>
    public static ScreenRect Frame(TilePlace place, ScreenRect work, ScreenRect current, int gap = 0)
    {
        if (place == TilePlace.Center) return Centered(work, current);
        gap = Math.Max(0, gap);
        // Inside the margins, the tiles split what is left of the room between them.
        var inner = new ScreenRect(work.Left + gap, work.Top + gap, work.Right - gap, work.Bottom - gap);
        int left = (inner.Right - inner.Left - gap) / 2, top = (inner.Bottom - inner.Top - gap) / 2;
        int x1 = inner.Left + left, x2 = x1 + gap, y1 = inner.Top + top, y2 = y1 + gap;
        return place switch
        {
            TilePlace.Left => new(inner.Left, inner.Top, x1, inner.Bottom),
            TilePlace.Right => new(x2, inner.Top, inner.Right, inner.Bottom),
            TilePlace.Top => new(inner.Left, inner.Top, inner.Right, y1),
            TilePlace.Bottom => new(inner.Left, y2, inner.Right, inner.Bottom),
            TilePlace.TopLeft => new(inner.Left, inner.Top, x1, y1),
            TilePlace.TopRight => new(x2, inner.Top, inner.Right, y1),
            TilePlace.BottomLeft => new(inner.Left, y2, x1, inner.Bottom),
            TilePlace.BottomRight => new(x2, y2, inner.Right, inner.Bottom),
            _ => inner, // Fill
        };
    }

    static ScreenRect Centered(ScreenRect work, ScreenRect current)
    {
        int ww = work.Right - work.Left, wh = work.Bottom - work.Top;
        int w = Math.Min(current.Right - current.Left, ww), h = Math.Min(current.Bottom - current.Top, wh);
        int x = work.Left + (ww - w) / 2, y = work.Top + (wh - h) / 2;
        return new(x, y, x + w, y + h);
    }
}

/// <summary>A window's frame from before Move &amp; Resize first moved it, and whether it was maximized then.</summary>
public readonly record struct TileBefore(ScreenRect Frame, bool Maximized);

/// <summary>
/// Each window's frame from before Move &amp; Resize first moved it: "Return to Previous Size" puts it back (and forgets
/// it). Moving it again by these keeps the first one (macOS returns to the size before the tiling began).
/// </summary>
public sealed class TileMemory
{
    readonly Dictionary<long, TileBefore> _before = new();

    public int Count => _before.Count;

    /// <summary>The window is about to be moved: its frame is kept unless one is kept already.</summary>
    public void Moving(long window, TileBefore before) => _before.TryAdd(window, before);

    public bool Has(long window) => _before.ContainsKey(window);

    /// <summary>The frame to go back to (null: none kept); forgotten.</summary>
    public TileBefore? Take(long window) => _before.Remove(window, out var b) ? b : null;

    /// <summary>Only the windows still there are kept (the others' numbers may come back for other windows).</summary>
    public void KeepOnly(Func<long, bool> alive)
    {
        foreach (var gone in _before.Keys.Where(w => !alive(w)).ToList()) _before.Remove(gone);
    }
}
