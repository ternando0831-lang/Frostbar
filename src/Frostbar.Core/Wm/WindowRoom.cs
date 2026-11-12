using Frostbar.Core.Shell;

namespace Frostbar.Core.Wm;

/// <summary>
/// Where an app's window opens by the bars (2026-10-24, the user's "起動時にメニューバーより上、Dockより下に行かない"): inside
/// the work area on each side a bar reserves (the menu bar's strip at the top, the Dock's at its edge, a taskbar's on
/// another monitor), sized to the room when it is taller or wider and can be sized. A side no bar reserves (the work
/// area's edge is the monitor's: the Dock hiding, the menu bar hiding always) is left alone. Afterwards Windows itself
/// keeps windows below the menu bar's strip, as the user asked ("起動後のウィンドウは Dock はいいが、メニューバーには重ならない"):
/// a window the user drags with its top under the strip is put back below it (or maximized by Snap), its top edge sized
/// up stops there, and one under the top as the strip is taken is moved down; a window may go behind the Dock
/// (window-room, 2026-10-24). Frames are the visible ones, in pixels.
/// </summary>
public static class WindowRoom
{
    /// <summary>
    /// How far short of its monitor's edges a window may stop and still cover it (pixels): some full-screen surfaces leave a
    /// pixel row out so that Windows does not take them for full-screen apps (the specs' backdrop does). Never moved.
    /// </summary>
    public const int CoverSlackPx = 2;

    /// <summary>The window is as good as over its whole monitor (<see cref="CoverSlackPx"/>): a game's, a video's.</summary>
    public static bool Covers(ScreenRect frame, ScreenRect monitor) =>
        frame.Left <= monitor.Left + CoverSlackPx && frame.Top <= monitor.Top + CoverSlackPx
        && frame.Right >= monitor.Right - CoverSlackPx && frame.Bottom >= monitor.Bottom - CoverSlackPx;

    /// <summary>
    /// The frame a window opening at <paramref name="frame"/> takes on its monitor, or null where it may stay: one off its
    /// monitor (an app's window kept out of sight) or over all of it is left alone. On each axis the top (the caption) and
    /// the left come first: a window that cannot be sized (<paramref name="sizable"/> false) and is taller than the room
    /// stands below the menu bar and goes on behind the Dock.
    /// </summary>
    public static ScreenRect? Opening(ScreenRect frame, ScreenRect monitor, ScreenRect work, bool sizable)
    {
        if (!Meets(frame, monitor) || Covers(frame, monitor)) return null;
        var (left, right) = Fit(frame.Left, frame.Right, work.Left, work.Right, work.Left > monitor.Left, work.Right < monitor.Right, sizable);
        var (top, bottom) = Fit(frame.Top, frame.Bottom, work.Top, work.Bottom, work.Top > monitor.Top, work.Bottom < monitor.Bottom, sizable);
        var to = new ScreenRect(left, top, right, bottom);
        return to == frame ? null : to;
    }

    static bool Meets(ScreenRect a, ScreenRect b) => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    /// <summary>
    /// One axis: <paramref name="from"/>–<paramref name="to"/> left alone unless it reaches over a side a bar stands on
    /// (<paramref name="lowBar"/>, <paramref name="highBar"/>); then kept between the work area's <paramref name="low"/>
    /// and <paramref name="high"/> (on a side without a bar, the monitor's edge: pushed away from a bar, a window does not
    /// go off the other side), sized down to the room when it can be; the low side wins.
    /// </summary>
    static (int From, int To) Fit(int from, int to, int low, int high, bool lowBar, bool highBar, bool sizable)
    {
        if (!(lowBar && from < low) && !(highBar && to > high)) return (from, to);
        int size = to - from;
        if (sizable && size > high - low) size = high - low;
        if (from + size > high) from = high - size;
        if (from < low) from = low;
        return (from, from + size);
    }
}
