using Frostbar.Core.Overview;

namespace Frostbar.Core.Wm;

/// <summary>Where the strip's parts stand (DIPs from the strip's top left); <see cref="Add"/> is null without the add button.</summary>
public readonly record struct StripLayout(IReadOnlyList<Rect2> Pictures, IReadOnlyList<Rect2> Names, Rect2? Add, double Height);

/// <summary>
/// The strip of virtual desktops along the top of the window overview (stage 3-4; the user's choice B, 2026-10-16: the
/// pictures always shown, as macOS's Spaces bar unfolded): each desktop a small picture of the screen (its wallpaper
/// and its windows) with its name under it, in a row centred on the strip, and the add button at its right end.
/// </summary>
public static class DesktopStrip
{
    /// <summary>
    /// The desktops in their order: the registry's list, with the current desktop at its end when the list lacks it (a
    /// desktop added a moment ago is missing from the list for a while; Windows adds at the end, ADR-014). Repeats are
    /// dropped; an empty current is left out.
    /// </summary>
    public static IReadOnlyList<Guid> Order(IReadOnlyList<Guid> listed, Guid current)
    {
        var order = listed.Where(id => id != Guid.Empty).Distinct().ToList();
        if (current != Guid.Empty && !order.Contains(current)) order.Add(current);
        return order;
    }

    /// <summary>The names shown: a desktop's own, else <paramref name="numbered"/> of its place from 1 (as Windows names them, "Desktop 2").</summary>
    public static IReadOnlyList<string> Names(IReadOnlyList<string?> own, Func<int, string> numbered) =>
        own.Select((name, i) => string.IsNullOrWhiteSpace(name) ? numbered(i + 1) : name.Trim()).ToList();

    /// <summary>
    /// The strip's parts for <paramref name="count"/> desktops on a strip <paramref name="width"/> wide: pictures of the
    /// screen's shape (<paramref name="aspect"/>, width over height) <paramref name="pictureHeight"/> high and
    /// <paramref name="gap"/> apart, in a row centred on the strip, made smaller together when they do not fit between
    /// the margins and the add button; each name <paramref name="nameGap"/> under its picture; the add button
    /// (<paramref name="addSize"/> square) at the right end, level with the pictures' middle.
    /// </summary>
    public static StripLayout Arrange(int count, double width, double aspect, double pictureHeight, double gap, double margin,
        double top, double nameGap, double nameHeight, double addSize, bool add)
    {
        double ph = pictureHeight, pw = ph * aspect, g = gap;
        // Room for the row: a margin each side, and the add button with a gap on both of its sides.
        double room = width - 2 * margin - (add ? 2 * (addSize + gap) : 0);
        double row = count * pw + Math.Max(0, count - 1) * g;
        if (count > 0 && row > room && room > 0)
        {
            double k = room / row;
            pw *= k; ph *= k; g *= k;
            row = room;
        }
        double x = (width - row) / 2;
        var pictures = new List<Rect2>();
        var names = new List<Rect2>();
        for (int i = 0; i < count; i++)
        {
            var p = new Rect2(x + i * (pw + g), top, pw, ph);
            pictures.Add(p);
            names.Add(new Rect2(p.X, p.Bottom + nameGap, pw, nameHeight));
        }
        Rect2? plus = add ? new Rect2(width - margin - addSize, top + (ph - addSize) / 2, addSize, addSize) : null;
        return new StripLayout(pictures, names, plus, top + ph + nameGap + nameHeight);
    }

    /// <summary>
    /// Where a removed desktop's windows go (stage 3-5): the desktop left of it, or for the first one the one to its
    /// right, as Task View's close does; null when it is the only one (it cannot be removed) or not among them.
    /// </summary>
    public static Guid? Fallback(IReadOnlyList<Guid> order, Guid removed)
    {
        int i = order.ToList().IndexOf(removed);
        if (i < 0 || order.Count < 2) return null;
        return i > 0 ? order[i - 1] : order[1];
    }

    /// <summary>
    /// The picture under (<paramref name="x"/>, <paramref name="y"/>) (the pictures' coordinates), taking a little
    /// around each (<paramref name="slack"/>: a window let go near a picture is meant for it); -1 when none.
    /// </summary>
    public static int PictureAt(IReadOnlyList<Rect2> pictures, double x, double y, double slack)
    {
        for (int i = 0; i < pictures.Count; i++)
        {
            var p = pictures[i];
            if (x >= p.X - slack && x < p.Right + slack && y >= p.Y - slack && y < p.Bottom + slack) return i;
        }
        return -1;
    }

    /// <summary>A window's frame on the screen in a desktop's picture: <paramref name="screen"/> scaled into <paramref name="picture"/>.</summary>
    public static Rect2 InPicture(Rect2 window, Rect2 screen, Rect2 picture)
    {
        if (screen.Width <= 0 || screen.Height <= 0) return new Rect2(picture.X, picture.Y, 0, 0);
        double kx = picture.Width / screen.Width, ky = picture.Height / screen.Height;
        double l = Math.Clamp((window.X - screen.X) * kx, 0, picture.Width), t = Math.Clamp((window.Y - screen.Y) * ky, 0, picture.Height);
        double r = Math.Clamp((window.Right - screen.X) * kx, 0, picture.Width), b = Math.Clamp((window.Bottom - screen.Y) * ky, 0, picture.Height);
        return new Rect2(picture.X + l, picture.Y + t, Math.Max(0, r - l), Math.Max(0, b - t));
    }
}
