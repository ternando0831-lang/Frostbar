using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Wm;

/// <summary>Which top-level windows are "app windows" (roughly what Explorer's taskbar and Alt+Tab show).</summary>
public static class WindowList
{
    static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "ForegroundStaging", "#32768", "MultitaskingViewFrame",
        "XamlExplorerHostIslandWindow", "TaskListThumbnailWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Core.CoreWindow", "Shell_InputSwitchTopLevelWindow", "TaskManagerWindow_Tooltip",
    };

    /// <summary>An app window shown on this virtual desktop.</summary>
    public static bool IsAppWindow(IntPtr h) => IsAppWindowShape(h) && !Native.Cloaked(h);

    /// <summary>An app window by its style, owner and class, wherever it is (cloaked windows included).</summary>
    static bool IsAppWindowShape(IntPtr h)
    {
        if (!Native.IsWindowVisible(h)) return false;
        long ex = Native.ExStyle(h);
        bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return false;
        if (Native.GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero && !appWindow) return false;
        if ((ex & Native.WS_EX_NOACTIVATE) != 0 && !appWindow) return false;
        if (ShellClasses.Contains(Native.ClassName(h))) return false;
        return Native.ProcessId(h) != (uint)Environment.ProcessId;
    }

    /// <summary>App windows of this virtual desktop from top to bottom of the z-order.</summary>
    public static List<IntPtr> AppWindows()
    {
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) => { if (IsAppWindow(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// App windows of every virtual desktop, top to bottom, each with whether it is on another desktop (for the Dock:
    /// an app with windows only there still runs). Cloaked windows that are not on another desktop are left out.
    /// </summary>
    public static List<(IntPtr Hwnd, bool Elsewhere)> AppWindowsOfAllDesktops()
    {
        var list = new List<(IntPtr, bool)>();
        Native.EnumWindows((h, _) =>
        {
            if (!IsAppWindowShape(h)) return true;
            var presence = VirtualDesktops.PresenceOf(h);
            if (presence != Core.Wm.Presence.Hidden) list.Add((h, presence == Core.Wm.Presence.Elsewhere));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
