namespace Frostbar.Core.Shell;

/// <param name="Edge">The edge it is registered on: ABE_LEFT 0, ABE_TOP 1, ABE_RIGHT 2, ABE_BOTTOM 3.</param>
public readonly record struct BarPlace(uint Edge, ScreenRect Rect);

/// <summary>When Explorer has given its taskbar's space back after the shell hid it.</summary>
public static class TaskbarSettle
{
    /// <summary>
    /// The tray waits for this (ManagedShell starting in the middle of Explorer's switch to auto-hide left the space
    /// reserved, 2026-10-12): auto-hide is on, each of our bars sits on its own edge of the monitor across all of it,
    /// and the work area is the monitor less our bars. Before 2026-10-15 only the bottom bar was checked: with Explorer's
    /// taskbar at the top, the menu bar first sits under it, and the work area below the menu bar passed at once.
    /// </summary>
    public static bool Released(ScreenRect monitor, ScreenRect workArea, IReadOnlyList<BarPlace> ourBars, bool autoHide)
    {
        if (!autoHide) return false;
        int left = monitor.Left, top = monitor.Top, right = monitor.Right, bottom = monitor.Bottom;
        foreach (var b in ourBars)
        {
            var r = b.Rect;
            bool across = r.Left == monitor.Left && r.Right == monitor.Right, down = r.Top == monitor.Top && r.Bottom == monitor.Bottom;
            switch (b.Edge)
            {
                case 0: if (!down || r.Left != monitor.Left) return false; left = Math.Max(left, r.Right); break;
                case 1: if (!across || r.Top != monitor.Top) return false; top = Math.Max(top, r.Bottom); break;
                case 2: if (!down || r.Right != monitor.Right) return false; right = Math.Min(right, r.Left); break;
                case 3: if (!across || r.Bottom != monitor.Bottom) return false; bottom = Math.Min(bottom, r.Top); break;
            }
        }
        return workArea == new ScreenRect(left, top, right, bottom);
    }
}
