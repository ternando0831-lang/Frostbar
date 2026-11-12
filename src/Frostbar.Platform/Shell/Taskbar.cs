using System.Diagnostics;
using Frostbar.Core.Shell;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Shell;

/// <summary>
/// Explorer's taskbar windows. Never use FindWindow("Shell_TrayWnd"): ManagedShell's tray creates its own window with
/// that class, so taskbars are found by class *and* by belonging to explorer.exe.
/// </summary>
public static class Taskbar
{
    public static List<IntPtr> Bars()
    {
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            var c = Native.ClassName(h);
            if (c is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")) return true;
            if (IsExplorer(Native.ProcessId(h))) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static bool IsExplorer(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    sealed record Found(IntPtr Hwnd, uint Pid);
    static volatile Found? _primary;

    /// <summary>
    /// Explorer's primary taskbar, or zero. Remembered while that window lives in the same Explorer: until 2026-10-15
    /// every call walked all top-level windows (and opened each tray-class window's process), and the hider's 1 s
    /// check made four such walks a second, the app-bar messages one each.
    /// </summary>
    public static IntPtr Primary()
    {
        if (_primary is { } f && Native.IsWindow(f.Hwnd) && Native.ProcessId(f.Hwnd) == f.Pid && Native.ClassName(f.Hwnd) == "Shell_TrayWnd") return f.Hwnd;
        var h = Bars().FirstOrDefault(b => Native.ClassName(b) == "Shell_TrayWnd");
        _primary = h != IntPtr.Zero ? new Found(h, Native.ProcessId(h)) : null;
        return h;
    }

    /// <summary>
    /// The taskbars the shell hides: the primary one (<see cref="TaskbarRecoveryPlanner.Hides"/> takes its class only); a
    /// secondary monitor keeps its own. Found without walking every window.
    /// </summary>
    static IEnumerable<IntPtr> Hidden()
    {
        var p = Primary();
        return p != IntPtr.Zero && TaskbarRecoveryPlanner.Hides(Native.ClassName(p)) ? [p] : [];
    }

    /// <summary>
    /// Where Explorer's primary taskbar is: its edge (ABE_LEFT 0, TOP 1, RIGHT 2, BOTTOM 3) and its rectangle, asked
    /// with ABM_GETTASKBARPOS straight to Explorer's window (through our tray's window the answer is lost), or null.
    /// Windows 11 puts it on any edge from 26300.8493 (Settings, taskbar behaviours).
    /// </summary>
    public static (uint Edge, RECT Rect)? Position()
    {
        var bar = Primary();
        if (bar == IntPtr.Zero) return null;
        var d = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(), hWnd = bar };
        if (AppBarMessenger.Send(Native.ABM_GETTASKBARPOS, ref d) == IntPtr.Zero) return null;
        return (d.uEdge, d.rc);
    }

    public static int GetState() => StateOrNull() ?? 0;

    /// <summary>ABM_GETSTATE, or null when Explorer's taskbar is not there or did not answer (a timeout read as 0 is "not auto-hiding").</summary>
    public static int? StateOrNull()
    {
        var d = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(), hWnd = Primary() };
        int state = (int)AppBarMessenger.Send(Native.ABM_GETSTATE, ref d, out bool answered);
        return answered ? state : null;
    }

    /// <summary>ABM_SETSTATE; false when Explorer did not answer (not known to have taken it).</summary>
    public static bool SetState(int state)
    {
        var d = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(), hWnd = Primary(), lParam = new IntPtr(state) };
        AppBarMessenger.Send(Native.ABM_SETSTATE, ref d, out bool answered);
        return answered;
    }

    public static TaskbarSavedState Snapshot(string strategy)
    {
        var covered = FullScreenCovers();
        var windows = new List<TaskbarWindowState>();
        foreach (var h in Bars())
        {
            long ex = Native.ExStyle(h), original = TaskbarRecoveryPlanner.OriginalExStyle(ex, UnderFullScreen(h, covered));
            if (original != ex) Log.Warn($"{Native.ClassName(h)} was not always on top with no fullscreen app in front (lost by an earlier run?): saved as always on top");
            windows.Add(new TaskbarWindowState { Class = Native.ClassName(h), Visible = Native.IsWindowVisible(h), ExStyle = original });
        }
        // Not answered: not known (-1), not "off" (read as 0, a user's auto-hide on was saved as off, and the restore
        // turned it off; 2026-11-06 review).
        return new() { Strategy = strategy, AbmState = StateOrNull() ?? -1, SavedAt = DateTimeOffset.Now, Windows = windows };
    }

    public static List<TaskbarWindowNow> Observe()
    {
        var covered = FullScreenCovers();
        return Bars().Select(h => new TaskbarWindowNow(Native.ClassName(h), Native.IsWindowVisible(h), Native.ExStyle(h), UnderFullScreen(h, covered))).ToList();
    }

    /// <summary>The monitor a fullscreen app in front covers (not one of this process's windows), or zero.</summary>
    static IntPtr FullScreenCovers() => FullScreenDetector.FrontCovers(h => Native.ProcessId(h) == (uint)Environment.ProcessId, out _);

    static bool UnderFullScreen(IntPtr taskbar, IntPtr covered) =>
        covered != IntPtr.Zero && Native.MonitorFromWindow(taskbar, Native.MONITOR_DEFAULTTONEAREST) == covered;

    /// <summary>Puts the plan's steps through; false when its auto-hide state was not answered by Explorer (it may not have been set).</summary>
    public static bool Apply(TaskbarPlan plan)
    {
        var bars = Bars();
        foreach (var step in plan.Steps)
            foreach (var h in bars.Where(b => Native.ClassName(b) == step.Class))
                switch (step.Fix)
                {
                    case TaskbarFix.RemoveTransparency:
                        Native.SetLayeredWindowAttributes(h, 0, 255, 2 /*LWA_ALPHA*/);
                        Native.SetExStyle(h, Native.ExStyle(h) & ~Native.WS_EX_LAYERED);
                        break;
                    case TaskbarFix.Show:
                        Native.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_SHOWWINDOW | Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                        break;
                    case TaskbarFix.MakeTopmost:
                        // Read back: Explorer can keep its taskbar below against it (2026-10-15, for a minute or two).
                        if (!MakeTopmost(h)) Log.Warn($"{step.Class}: Explorer kept it below (always on top not taken)");
                        break;
                }
        return plan.AbmState is not int abm || SetState(abm);
    }

    /// <summary>HWND_TOPMOST for a taskbar window; true when it has taken (read back).</summary>
    public static bool MakeTopmost(IntPtr h)
    {
        Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        return (Native.ExStyle(h) & Native.WS_EX_TOPMOST) != 0;
    }

    /// <summary>Explorer's taskbar windows of these classes (the plan's steps name classes: handles change when Explorer restarts).</summary>
    public static IEnumerable<IntPtr> OfClass(string windowClass) => Bars().Where(b => Native.ClassName(b) == windowClass);

    /// <summary>
    /// H3: hide without touching the z-order. HWND_BOTTOM (as ManagedShell does) silently drops WS_EX_TOPMOST
    /// and it stays lost after the taskbar is shown again (measured 2026-10-11).
    /// </summary>
    public static void HideWindows()
    {
        foreach (var h in Hidden())
            if (Native.IsWindowVisible(h))
                Native.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_HIDEWINDOW | Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>H2: fully transparent and click-through; Explorer still believes the taskbar is shown.</summary>
    public static void MakeTransparent()
    {
        foreach (var h in Hidden())
        {
            long ex = Native.ExStyle(h);
            if ((ex & Native.WS_EX_LAYERED) == 0) Native.SetExStyle(h, ex | Native.WS_EX_LAYERED);
            Native.SetLayeredWindowAttributes(h, 0, 0, 2 /*LWA_ALPHA*/);
        }
    }

    /// <summary>Something brought a taskbar back (visible for "hide", opaque for "transparent").</summary>
    public static bool AnyShowing(string strategy) => Hidden().Any(h => Showing(h, strategy));

    /// <summary>This taskbar window shows: visible for "hide", opaque for "transparent" (one or two quick questions).</summary>
    public static bool Showing(IntPtr h, string strategy) => strategy == "transparent"
        ? !((Native.ExStyle(h) & Native.WS_EX_LAYERED) != 0 && Native.GetLayeredWindowAttributes(h, out _, out byte a, out uint f) && (f & 2) != 0 && a == 0)
        : Native.IsWindowVisible(h);

    /// <summary>Explorer answers messages (it may still be starting at sign-in).</summary>
    public static bool ExplorerReady()
    {
        var p = Primary();
        return p != IntPtr.Zero && Native.SendMessageTimeout(p, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 1000, out _) != IntPtr.Zero;
    }
}
