namespace Frostbar.Core.AppGrid;

/// <summary>
/// How the app grid's pages turn (Launchpad): a drag past a sixth of the width, or a quick flick, turns one page; the
/// pages stretch only a third as far as the pointer beyond the first and the last page; arrow keys move a selection
/// through search results laid out row by row, page after page.
/// </summary>
public static class GridPaging
{
    /// <summary>A flick: at least this fast (DIPs per ms) over at least this distance (DIPs).</summary>
    public const double FlickSpeed = 0.5, FlickDistance = 24;

    /// <summary>
    /// The page a horizontal drag of <paramref name="dx"/> DIPs ends on (negative: leftwards, towards the next page),
    /// given the pointer's speed at release (DIPs per ms, same sign convention).
    /// </summary>
    public static int AfterDrag(int page, int pages, double dx, double velocity, double width)
    {
        int turn = 0;
        if (Math.Abs(dx) > width / 6) turn = dx < 0 ? 1 : -1;
        else if (Math.Abs(dx) >= FlickDistance && Math.Abs(velocity) >= FlickSpeed && Math.Sign(velocity) == Math.Sign(dx)) turn = dx < 0 ? 1 : -1;
        return Math.Clamp(page + turn, 0, Math.Max(0, pages - 1));
    }

    /// <summary>How far the pages follow a drag of <paramref name="dx"/>: all the way, but a third of it beyond either end.</summary>
    public static double Follow(int page, int pages, double dx) =>
        page <= 0 && dx > 0 || page >= pages - 1 && dx < 0 ? dx / 3 : dx;

    /// <summary>
    /// The selection after an arrow key (<paramref name="dx"/>, <paramref name="dy"/> = -1, 0 or 1) among
    /// <paramref name="count"/> results: left and right step through them in order, up and down by a row. It stays put
    /// at either end, except that down from above a short last row goes to its last item. Nothing chosen (-1): the first.
    /// </summary>
    public static int Move(int selection, int dx, int dy, int columns, int count)
    {
        if (count <= 0) return -1;
        if (selection < 0 || selection >= count) return 0;
        int next = selection + dx + dy * columns;
        if (next >= 0 && next < count) return next;
        if (dy > 0 && (count - 1) / columns > selection / columns) return count - 1;
        return selection;
    }
}

/// <summary>
/// Wheel and touchpad scrolling to page turns. A wheel notch (120) turns a page; so does a touchpad swipe (many small
/// deltas adding up to a notch) — once: while the deltas keep coming (a fast spin of the wheel, a swipe's inertia)
/// nothing more turns until they pause for <see cref="PauseMs"/>, and never sooner than <see cref="MinTurnMs"/>
/// after the last turn.
/// </summary>
public sealed class WheelPager
{
    public const int Notch = 120, PauseMs = 150, MinTurnMs = 250;

    int _sum;
    long _last = long.MinValue / 2, _turned = long.MinValue / 2;
    bool _locked;

    /// <param name="towardsNext">The delta, positive towards the next page (the wheel turned down, or scrolled right).</param>
    /// <param name="nowMs">A millisecond clock.</param>
    /// <returns>1: the next page; -1: the previous; 0: no turn.</returns>
    public int Feed(int towardsNext, long nowMs)
    {
        bool paused = nowMs - _last >= PauseMs;
        _last = nowMs;
        if (_locked && paused && nowMs - _turned >= MinTurnMs) _locked = false;
        if (_locked) return 0;
        if (paused) _sum = 0;
        _sum += towardsNext;
        if (Math.Abs(_sum) < Notch) return 0;
        int turn = Math.Sign(_sum);
        _sum = 0;
        _locked = true;
        _turned = nowMs;
        return turn;
    }
}
