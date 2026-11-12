using System.Runtime.InteropServices;
using System.Text;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;
using Microsoft.Win32;

namespace Frostbar.Platform.Shell;

/// <summary>
/// macOS's Desktop &amp; Dock › "Show Items: On Desktop" (2026-10-22): Windows' desktop icons, shown or hidden as
/// File Explorer's desktop menu ("View › Show desktop icons") does. Whether they show is whether the desktop's list of
/// icons is visible; the change is Explorer's own command sent to the desktop's view (the menu's item, 0x7402), which
/// flips it, so it is sent only when they are not as wanted, and looked at again after (an error when they did not
/// change). Explorer's value HKCU\…\Explorer\Advanced\HideIcons is read only without the list: the command showed and
/// hid the list on this PC, the value staying 1 for 3 s after either (2026-10-22, desktop-icons yes). Whether Explorer
/// keeps the change across its restart is not known. Under
/// a spec, a value of the spec's own kept in memory (FROSTBAR_E2E_DESKTOP_ICONS=spec, with --diag only): no spec hides
/// the user's icons unless it asks for Windows' (desktop-icons yes).
/// </summary>
public static class DesktopItems
{
    const int ShowDesktopIconsCommand = 0x7402;
    static bool? _spec;
    static int _writes;

    public static void UseSpec(bool shown) => _spec = shown;
    public static bool Spec => _spec != null;
    /// <summary>Changes asked for (diagnostics).</summary>
    public static int Writes => _writes;

    /// <summary>Windows' desktop icons show: their list is visible (without it, Explorer's value; true when that is not there).</summary>
    public static bool Shown()
    {
        if (_spec is { } own) return own;
        if (DesktopList() is { } list) return Native.IsWindowVisible(list);
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        return !(k?.GetValue("HideIcons") is int hide && hide != 0);
    }

    /// <summary>The desktop's list of icons (SysListView32 in the desktop's view), or null.</summary>
    public static IntPtr? DesktopList()
    {
        if (DesktopView() is not { } view) return null;
        var list = FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
        return list == IntPtr.Zero ? null : list;
    }

    /// <summary>Shows or hides them; what went wrong, or null. Off the UI thread: Explorer is asked and waited for.</summary>
    public static string? Set(bool shown)
    {
        Interlocked.Increment(ref _writes);
        if (_spec != null) { _spec = shown; return null; }
        if (Shown() == shown) return null;
        if (DesktopView() is not { } view || view == IntPtr.Zero) return "no desktop view";
        Native.SendMessageTimeout(view, 0x0111 /*WM_COMMAND*/, new IntPtr(ShowDesktopIconsCommand), IntPtr.Zero, 0x0002 /*SMTO_ABORTIFHUNG*/, 2000, out _);
        for (int i = 0; i < 20 && Shown() != shown; i++) Thread.Sleep(50);
        if (Shown() == shown) return null;
        Log.Warn($"desktop items: still {(shown ? "hidden" : "shown")} after Explorer's command");
        return "Explorer did not change them";
    }

    /// <summary>The desktop's view (SHELLDLL_DefView): under Progman, or under a WorkerW once a wallpaper slideshow or animation moved it.</summary>
    static IntPtr? DesktopView()
    {
        var progman = Native.FindWindow("Progman", null);
        var view = progman == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (view != IntPtr.Zero) return view;
        var worker = IntPtr.Zero;
        while ((worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
        {
            view = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view != IntPtr.Zero) return view;
        }
        return null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);
}

/// <summary>
/// macOS's Desktop &amp; Dock › "Default web browser" (2026-10-22): the name of the app that opens web links (the
/// association of "http"), shown on the Dock page with a button to Windows' default apps (where it is chosen: Windows
/// keeps that choice to itself).
/// </summary>
public static class DefaultBrowser
{
    /// <summary>The browser's name, or null when Windows does not say.</summary>
    public static string? Name()
    {
        try
        {
            uint size = 512;
            var sb = new StringBuilder((int)size);
            int hr = AssocQueryString(0x1000 /*ASSOCF_IS_PROTOCOL*/, 4 /*ASSOCSTR_FRIENDLYAPPNAME*/, "http", null, sb, ref size);
            return hr == 0 && sb.Length > 0 ? sb.ToString() : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int AssocQueryString(uint flags, int str, string assoc, string? extra, StringBuilder? output, ref uint size);
}
