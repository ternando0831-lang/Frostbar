using Frostbar.Core.Shell;
using Frostbar.Core.Wm;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Shell;

namespace Frostbar.Platform.Wm;

/// <summary>
/// Operations on other apps' windows. Only asynchronous calls (ShowWindowAsync, PostMessage) or quick ones, so a hung
/// app cannot freeze the menu bar. Frames are aligned on the visible frame (DWMWA_EXTENDED_FRAME_BOUNDS), not on the
/// invisible resize border.
/// </summary>
public static class WindowActions
{
    /// <summary>
    /// SetForegroundWindow from a background process is often refused. Try plainly, then by attaching to the
    /// foreground thread's input, then SwitchToThisWindow (ported from Bipane's ShellMenu.ForceForeground).
    /// Must run on a thread with a message loop. A minimized window is restored first (<see cref="Restore"/>): an
    /// administrator's window was brought to the front still minimized, unseen, with the keyboard (2026-10-16).
    /// </summary>
    public static bool ForceForeground(IntPtr h)
    {
        if (Native.IsIconic(h)) Restore(h);
        if (Native.SetForegroundWindow(h) && Native.GetForegroundWindow() == h) return true;
        IntPtr fg = Native.GetForegroundWindow();
        uint fgThread = Native.GetWindowThreadProcessId(fg, out _), me = Native.GetCurrentThreadId();
        if (fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true))
        {
            try { Native.BringWindowToTop(h); Native.SetForegroundWindow(h); }
            finally { Native.AttachThreadInput(me, fgThread, false); }
            if (Native.GetForegroundWindow() == h) return true;
        }
        Native.SwitchToThisWindow(h, true);
        return Native.GetForegroundWindow() == h;
    }

    /// <summary>
    /// Restores a minimized window without waiting on it. An administrator's window refuses ShowWindowAsync (UIPI: the
    /// call returns false and nothing happens) but takes the window menu's Restore (WM_SYSCOMMAND, SC_RESTORE) and
    /// restores itself, as from its own menu (elevated-ops, 2026-10-16: back and in front 30 ms later).
    /// </summary>
    public static void Restore(IntPtr h)
    {
        if (!Native.ShowWindowAsync(h, Native.SW_RESTORE)) Native.PostMessage(h, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_RESTORE), IntPtr.Zero);
    }

    public static void Activate(IntPtr h) => ForceForeground(h);

    /// <summary>
    /// Minimizes without waiting on it. An administrator's window refuses ShowWindowAsync (UIPI) but takes its window
    /// menu's Minimize (WM_SYSCOMMAND, SC_MINIMIZE) and minimizes itself (elevated-ops, 2026-10-16: 29–31 ms later).
    /// </summary>
    public static void Minimize(IntPtr h)
    {
        if (!Native.ShowWindowAsync(h, Native.SW_MINIMIZE)) Native.PostMessage(h, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_MINIMIZE), IntPtr.Zero);
    }

    /// <summary>Not for an administrator's window: it refuses ShowWindowAsync, and its window menu's Maximize as well.</summary>
    public static void ToggleZoom(IntPtr h) => Native.ShowWindowAsync(h, Native.IsZoomed(h) ? Native.SW_RESTORE : Native.SW_MAXIMIZE);

    /// <summary>
    /// Asks the window to close, as its own close button does (the app may ask about unsaved work first). An
    /// administrator's window refuses a posted WM_CLOSE (UIPI) but takes its window menu's Close (SC_CLOSE), which comes
    /// to it as a WM_CLOSE of its own (elevated-ops, 2026-10-16: closed 29–31 ms later).
    /// </summary>
    public static void Close(IntPtr h)
    {
        if (!Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero)) Native.PostMessage(h, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_CLOSE), IntPtr.Zero);
    }

    /// <summary>
    /// Puts the window's visible frame at <paramref name="place"/> of its monitor's work area (<see cref="TileLayout"/>:
    /// a half, a quarter, all of it, or its own size in the middle), gliding there when asked (<see cref="WindowGlide"/>).
    /// Returns its frame from before and whether it was maximized (a maximized or minimized window is restored first, and
    /// measured at its own size: measured maximized, it was stretched over the whole work area); null when a hung app's
    /// window was left alone. <paramref name="margins"/>: the settings' "Tiled windows have margins"
    /// (<see cref="TileLayout.MarginDip"/> at the monitor's scale).
    /// </summary>
    public static TileBefore? Tile(IntPtr h, TilePlace place, bool glide = false, bool margins = false)
    {
        if (Hung(h)) return null;
        bool zoomed = Native.IsZoomed(h);
        if (zoomed || Native.IsIconic(h)) { Native.ShowWindow(h, Native.SW_RESTORE); glide = false; }
        var mon = Monitors.Of(h);
        var work = mon?.Work ?? Native.WorkArea();
        var f = Native.FrameBounds(h);
        var before = new ScreenRect(f.Left, f.Top, f.Right, f.Bottom);
        int gap = margins ? (int)Math.Round(TileLayout.MarginDip * (mon?.Scale ?? 1)) : 0;
        var to = TileLayout.Frame(place, new ScreenRect(work.Left, work.Top, work.Right, work.Bottom), before, gap);
        PlaceFrame(h, new RECT(to.Left, to.Top, to.Right, to.Bottom), glide);
        return new TileBefore(before, zoomed);
    }

    /// <summary>Puts the window back to the frame it had before Move &amp; Resize (maximized again if it was).</summary>
    public static void Return(IntPtr h, TileBefore before, bool glide = false)
    {
        var f = before.Frame;
        PlaceFrame(h, new RECT(f.Left, f.Top, f.Right, f.Bottom), glide && !before.Maximized);
        if (before.Maximized) Native.ShowWindowAsync(h, Native.SW_MAXIMIZE);
    }

    /// <summary>
    /// Moves the window so that its *visible* frame lands on <paramref name="frame"/>: gliding there when asked (a
    /// maximized or minimized window is restored first and goes at once).
    /// </summary>
    public static void PlaceFrame(IntPtr h, RECT frame, bool glide = false)
    {
        if (Hung(h)) return;
        if (Native.IsZoomed(h) || Native.IsIconic(h)) { Native.ShowWindow(h, Native.SW_RESTORE); glide = false; }
        Native.GetWindowRect(h, out var outer);
        var visible = Native.FrameBounds(h);
        int l = visible.Left - outer.Left, t = visible.Top - outer.Top, r = outer.Right - visible.Right, b = outer.Bottom - visible.Bottom;
        var to = new RECT(frame.Left - l, frame.Top - t, frame.Right + r, frame.Bottom + b);
        if (glide) { WindowGlide.To(h, to); return; }
        Native.SetWindowPos(h, IntPtr.Zero, to.Left, to.Top, to.Width, to.Height,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_ASYNCWINDOWPOS);
    }

    /// <summary>
    /// A hung app's window is left alone: restoring and measuring it are synchronous calls into the app, which would hold
    /// the shell's UI thread until it answers (2026-10-12).
    /// </summary>
    static bool Hung(IntPtr h)
    {
        if (!Native.IsHungAppWindow(h)) return false;
        Log.Debug($"window {h}: not responding, left alone");
        return true;
    }

    static RECT WorkAreaOf(IntPtr h) => Monitors.Of(h)?.Work ?? Native.WorkArea();

    /// <summary>
    /// Raises the windows (given top to bottom) above all others, keeping their order; the first ends up active. It is
    /// made active first, so that its app is the one in front, and the others go right under it, each under the one
    /// before. Raised the other way round (each to the top, the lowest first, while another app was in front), the
    /// lowest stayed under that app's window (2026-10-16, dock-activate): the request had been carried out before the
    /// app came to the front. The requests go to the windows' threads without waiting (a hung app cannot hold the shell);
    /// one to go under a window of another thread waits on a pool thread (at most 100 ms) until that window is in place,
    /// as their threads would take them in any order (File Explorer's windows each have their own).
    /// </summary>
    public static void BringAllToFront(IReadOnlyList<IntPtr> topToBottom)
    {
        var windows = topToBottom.Where(h => !Native.IsHungAppWindow(h)).ToList(); // a synchronous call into it would hold the shell
        if (windows.Count == 0) return;
        foreach (var h in windows) if (Native.IsIconic(h)) Restore(h);
        ForceForeground(windows[0]);
        if (windows.Count == 1) return;
        Task.Run(() =>
        {
            for (int i = 1; i < windows.Count; i++)
            {
                IntPtr above = windows[i - 1], h = windows[i];
                if (ThreadOf(above) != ThreadOf(h))
                {
                    var placed = i == 1 ? (Func<bool>)(() => OnTop(above)) : () => Native.GetWindow(above, Native.GW_HWNDPREV) == windows[i - 2];
                    for (long until = Environment.TickCount64 + 100; !placed() && Environment.TickCount64 < until;) Thread.Sleep(1);
                }
                Native.SetWindowPos(h, above, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_ASYNCWINDOWPOS);
            }
        });
    }

    static uint ThreadOf(IntPtr h) => Native.GetWindowThreadProcessId(h, out _);

    /// <summary>No window that is shown here and not topmost stands above it.</summary>
    static bool OnTop(IntPtr h)
    {
        for (var p = Native.GetWindow(h, Native.GW_HWNDPREV); p != IntPtr.Zero; p = Native.GetWindow(p, Native.GW_HWNDPREV))
            if (Native.IsWindowVisible(p) && (Native.ExStyle(p) & Native.WS_EX_TOPMOST) == 0 && !Native.Cloaked(p)) return false;
        return true;
    }

    /// <summary>"Hide" on Windows: minimise. Returns the windows it minimised (Show All restores exactly those).</summary>
    public static List<IntPtr> MinimizeAll(IEnumerable<IntPtr> windows)
    {
        var done = new List<IntPtr>();
        foreach (var h in windows)
            if (!Native.IsIconic(h)) { Minimize(h); done.Add(h); }
        Log.Debug($"minimised {done.Count} window(s)");
        return done;
    }
}
