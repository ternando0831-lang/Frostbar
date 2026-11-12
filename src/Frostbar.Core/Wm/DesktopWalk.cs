namespace Frostbar.Core.Wm;

/// <summary>
/// The way from the current virtual desktop to another with Win+Ctrl+Left/Right, one desktop at a time. Windows has
/// no public call to switch desktops. It goes to the desktop of a window brought to the front, but not when that
/// window already is the foreground window, hidden there (2026-10-15, vd-probe on 26300): the walk is for then. The
/// order is the registry's list, which lags behind: a desktop just added is missing from it (the same day, still
/// missing 30 s later). Win+Ctrl+D adds at the end, so a missing desktop is taken for one past the last; a way that
/// changes nothing (the end of the row, or an order that was wrong) is not tried again, and the other way is.
/// </summary>
public static class DesktopWalk
{
    /// <summary>
    /// -1 (left), +1 (right) or 0 (there, or no way left to try). <paramref name="leftEnd"/>, <paramref name="rightEnd"/>:
    /// a step that way changed nothing.
    /// </summary>
    public static int Next(IReadOnlyList<Guid> order, Guid current, Guid target, bool leftEnd, bool rightEnd)
    {
        if (current == target || target == Guid.Empty) return 0;
        int c = IndexOf(order, current), t = IndexOf(order, target);
        int way = c >= 0 && t >= 0 ? Math.Sign(t - c) : c >= 0 ? 1 : t >= 0 ? -1 : 1;
        if (way > 0 && rightEnd) way = leftEnd ? 0 : -1;
        else if (way < 0 && leftEnd) way = rightEnd ? 0 : 1;
        return way;
    }

    /// <summary>
    /// Steps until <paramref name="target"/> is current, at most <paramref name="maxSteps"/> presses; true when it is.
    /// <paramref name="step"/> presses one way and says whether the current desktop changed. Without a current desktop
    /// (none written) nothing is pressed: the arrival could not be told.
    /// </summary>
    public static bool Run(Guid target, Func<Guid> current, Func<IReadOnlyList<Guid>> order, Func<int, bool> step, int maxSteps = 12)
    {
        bool leftEnd = false, rightEnd = false;
        for (int i = 0; i < maxSteps; i++)
        {
            Guid now = current();
            if (now == target) return true;
            if (now == Guid.Empty) return false;
            int way = Next(order(), now, target, leftEnd, rightEnd);
            if (way == 0) return false;
            if (step(way)) continue;
            if (way < 0) leftEnd = true; else rightEnd = true;
        }
        return current() == target;
    }

    static int IndexOf(IReadOnlyList<Guid> order, Guid id)
    {
        for (int i = 0; i < order.Count; i++) if (order[i] == id) return i;
        return -1;
    }
}
