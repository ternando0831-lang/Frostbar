using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Frostbar.Core.Menus;
using Frostbar.Core.Wm;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Input;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Wm;

/// <summary>
/// Which virtual desktop a window is on, through Windows' public IVirtualDesktopManager (Explorer answers it from
/// another process, so only windows the shell cloaked are asked about: a window that is not cloaked is here, and one
/// cloaked otherwise is not counted). The decision is Core's <see cref="WindowPresence"/>. Switching desktops has no
/// public call: Windows goes to the desktop of a window brought to the front, except when that window already is the
/// foreground window, hidden there; for then <see cref="SwitchTo"/> presses Win+Ctrl+Left/Right.
/// </summary>
public static class VirtualDesktops
{
    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, out int onCurrent);
        [PreserveSig] int GetWindowDesktopId(IntPtr hwnd, out Guid desktop);
        [PreserveSig] int MoveWindowToDesktop(IntPtr hwnd, ref Guid desktop);
    }

    [ComImport, Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
    class VirtualDesktopManager { }

    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    [ThreadStatic] static IVirtualDesktopManager? _manager;
    [ThreadStatic] static long _retryAt;

    /// <summary>
    /// The desktop manager of this thread, made again after Explorer went away: a manager made before answers every
    /// call with a disconnection from then on, and one that could not be made (no Explorer) is tried again 5 s later.
    /// Before 2026-10-15 one failure turned the desktops off for good: after Explorer restarted, the Dock no longer saw
    /// another desktop's windows.
    /// </summary>
    static IVirtualDesktopManager? Manager()
    {
        if (_manager != null) return _manager;
        if (Environment.TickCount64 < _retryAt) return null;
        try { return _manager = (IVirtualDesktopManager)new VirtualDesktopManager(); }
        catch (COMException) { _retryAt = Environment.TickCount64 + 5000; return null; }
    }

    /// <summary>RPC_E_DISCONNECTED, RPC_S_SERVER_UNAVAILABLE, RPC_S_CALL_FAILED, CO_E_OBJNOTCONNECTED: the Explorer it was made in is gone.</summary>
    static bool Gone(int hr) => hr is unchecked((int)0x80010108) or unchecked((int)0x800706BA) or unchecked((int)0x800706BE) or unchecked((int)0x800401FD);

    public static Presence PresenceOf(IntPtr h)
    {
        int cloaked = Native.CloakedFlags(h);
        if (cloaked == 0) return Presence.Here;
        if ((cloaked & WindowPresence.CloakedShell) == 0) return Presence.Hidden;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (Manager() is not { } m) return Presence.Hidden; // no desktop manager (Explorer is not running): as before
            int hrOn = m.IsWindowOnCurrentVirtualDesktop(h, out int on);
            int hrId = m.GetWindowDesktopId(h, out var id);
            if (Gone(hrOn) || Gone(hrId)) { _manager = null; continue; }
            return WindowPresence.Of(cloaked, hrOn >= 0 ? on != 0 : null, hrId >= 0 ? id : null);
        }
        return Presence.Hidden;
    }

    /// <summary>The desktop of a window the shell cloaked on another desktop, or null (here, not counted, or not known).</summary>
    public static Guid? DesktopOf(IntPtr h)
    {
        if (PresenceOf(h) != Presence.Elsewhere || Manager() is not { } m) return null;
        return m.GetWindowDesktopId(h, out var id) >= 0 && id != Guid.Empty ? id : null;
    }

    /// <summary>The current desktop as Explorer writes it to the registry, at once on a switch (Guid.Empty: none written).</summary>
    public static Guid Current()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue("CurrentVirtualDesktop") is byte[] { Length: 16 } b ? new Guid(b) : Guid.Empty;
    }

    /// <summary>
    /// The desktops in order as the registry lists them. Written later than the current one: a desktop just added is
    /// missing for a while (2026-10-15).
    /// </summary>
    public static List<Guid> Order()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        var ids = k?.GetValue("VirtualDesktopIDs") as byte[] ?? [];
        var list = new List<Guid>();
        for (int i = 0; i + 16 <= ids.Length; i += 16) list.Add(new Guid(ids.AsSpan(i, 16)));
        return list;
    }

    /// <summary>A desktop's own name, as Task View's rename writes it (null: not named).</summary>
    public static string? Name(Guid desktop) => DesktopValue(desktop, "Name");

    /// <summary>A desktop's own wallpaper (null: the one of all desktops). The key stays after the desktop is gone (ADR-014).</summary>
    public static string? WallpaperOf(Guid desktop) => DesktopValue(desktop, "Wallpaper");

    static string? DesktopValue(Guid desktop, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"{Key}\Desktops\{desktop:B}");
        return k?.GetValue(name) is string s && s.Length > 0 ? s : null;
    }

    /// <summary>
    /// Goes to <paramref name="desktop"/> with Win+Ctrl+Left/Right, one desktop at a time (the way: Core's
    /// <see cref="DesktopWalk"/>), each step waited for in the registry. Blocks a few hundred ms a step: call it off
    /// the UI thread. True when that desktop is current. Keys do not reach Windows while a window running as
    /// administrator is in front (UIPI): nothing changes then, and it returns false.
    /// </summary>
    public static bool SwitchTo(Guid desktop)
    {
        var sw = Stopwatch.StartNew();
        int steps = 0;
        bool there = DesktopWalk.Run(desktop, Current, Order, way =>
        {
            steps++;
            Guid was = Current();
            InputInjector.SendChord(new Shortcut(Mods.Ctrl | Mods.Win, way < 0 ? Shortcut.VK_LEFT : Shortcut.VK_RIGHT), Native.GetForegroundWindow());
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 1500)
            {
                Thread.Sleep(15);
                if (Current() == was) continue;
                Thread.Sleep(250); // the slide: keys pressed during it may be lost
                return true;
            }
            return false;
        });
        Log.Info($"virtual desktops: to {desktop} {(there ? "there" : "not there")} after {steps} step(s), {sw.ElapsedMilliseconds} ms");
        return there;
    }
}
