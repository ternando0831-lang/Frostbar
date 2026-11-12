using System.Runtime.InteropServices;
using Frostbar.Platform.Infrastructure;

namespace Frostbar.Platform.Wm;

/// <summary>
/// Explorer's private IVirtualDesktopManagerInternal, which the public IVirtualDesktopManager lacks (stage 3-4 and 3-5;
/// checked on this build in stage 1's S17, ADR-014): the desktops with their names, switching to one, adding and
/// removing desktops, moving another app's window to a desktop. The ids are those of Windows 11 24H2 (26H2 has the same
/// base). An id Explorer does not know is refused (E_NOINTERFACE) before any of its methods can be called, so a build
/// that changed them turns these off (<see cref="Available"/>) instead of calling the wrong methods. Every call runs on
/// a thread of the pool (the proxies of Explorer's objects work there) with objects made for it, and is waited for at
/// most a moment: a hung Explorer cannot hold the shell.
/// </summary>
public static class DesktopsInternal
{
    static readonly Guid ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    static readonly Guid ManagerInternalService = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider10
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);
    }

    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IObjectArray
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);
    }

    /// <summary>Only passed through (MoveViewToDesktop); none of its methods is called.</summary>
    [ComImport, Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationView { }

    [ComImport, Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationViewCollection
    {
        [PreserveSig] int GetViews(out IntPtr array);
        [PreserveSig] int GetViewsByZOrder(out IntPtr array);
        [PreserveSig] int GetViewsByAppUserModelId(IntPtr id, out IntPtr array);
        [PreserveSig] int GetViewForHwnd(IntPtr hwnd, out IApplicationView? view);
    }

    [ComImport, Guid("3F07F4BE-B107-441A-AF0F-39D82529072C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktop
    {
        [PreserveSig] int IsViewVisible(IApplicationView view, out int visible);
        [PreserveSig] int GetId(out Guid id);
        [PreserveSig] int GetName(out IntPtr name); // an HSTRING
    }

    [ComImport, Guid("53F5CA0B-158F-4124-900C-057158060B27"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManagerInternal
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop);
        [PreserveSig] int CanViewMoveDesktops(IApplicationView view, out int can);
        [PreserveSig] int GetCurrentDesktop(out IVirtualDesktop? desktop);
        [PreserveSig] int GetDesktops(out IObjectArray? desktops);
        [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop? desktop);
        [PreserveSig] int SwitchDesktop(IVirtualDesktop desktop);
        [PreserveSig] int SwitchDesktopAndMoveForegroundView(IVirtualDesktop desktop);
        [PreserveSig] int CreateDesktop(out IVirtualDesktop? desktop);
        [PreserveSig] int MoveDesktop(IVirtualDesktop desktop, int index);
        [PreserveSig] int RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
    }

    [DllImport("combase.dll")] static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstring);

    /// <summary>-1: not checked yet, 0: not there, 1: there.</summary>
    static volatile int _available = -1;
    /// <summary>When it was last found not there (it is asked again 30 s later: Explorer may have been restarting).</summary>
    static long _missedAt;

    /// <summary>The private manager answered on this build (<see cref="Check"/> run once off the UI thread). False until checked.</summary>
    public static bool Available => _available == 1;

    /// <summary>
    /// Asks Explorer whether the private manager is there with these ids (blocks up to a second: off the UI thread).
    /// Once there, it stays so; not there, it is asked again 30 s later (Explorer may have been restarting: the shell
    /// started while it was down would never have offered the add button).
    /// </summary>
    public static bool Check()
    {
        if (_available == 1) return true;
        if (_available == 0 && Environment.TickCount64 - Interlocked.Read(ref _missedAt) < 30_000) return false;
        bool there = Run((m, _) => m.GetCount(out int n) >= 0 && n > 0, 1000);
        int was = _available;
        _available = there ? 1 : 0;
        if (!there) Interlocked.Exchange(ref _missedAt, Environment.TickCount64);
        if (was != _available) Log.Info($"virtual desktops: the private manager is {(there ? "there" : "not there (the ids of this build differ, or no Explorer)")}");
        return there;
    }

    /// <summary>The desktops in their order, with their own names ("" when not named); null when not answered.</summary>
    public static List<(Guid Id, string Name)>? Desktops(int ms = 400) =>
        !Available ? null : Run((m, _) => List(m).Select(d => (d.Id, d.Name)).ToList(), ms);

    /// <summary>Adds a desktop at the end without going there; its id, or null.</summary>
    public static Guid? Create(int ms = 1500) =>
        !Available ? null : Run<Guid?>((m, _) => m.CreateDesktop(out var d) >= 0 && d != null && d.GetId(out var id) >= 0 ? id : null, ms);

    /// <summary>Goes to <paramref name="desktop"/> (7 to 9 ms in S17). False when it is not there or Explorer did not answer.</summary>
    public static bool Switch(Guid desktop, int ms = 1500) =>
        Available && Run((m, _) => Find(m, desktop) is { } d && m.SwitchDesktop(d) >= 0, ms);

    /// <summary>Removes <paramref name="desktop"/>; its windows go to <paramref name="fallback"/> (as Task View's close does).</summary>
    public static bool Remove(Guid desktop, Guid fallback, int ms = 1500) =>
        Available && desktop != fallback && Run((m, _) => Find(m, desktop) is { } d && Find(m, fallback) is { } f && m.RemoveDesktop(d, f) >= 0, ms);

    /// <summary>Moves another app's window to <paramref name="desktop"/> (the public call moves only the caller's own windows).</summary>
    public static bool MoveWindow(IntPtr hwnd, Guid desktop, int ms = 1500) =>
        Available && Run((m, views) => views != null && views.GetViewForHwnd(hwnd, out var view) >= 0 && view != null
                                       && Find(m, desktop) is { } d && m.MoveViewToDesktop(view, d) >= 0, ms);

    /// <summary>Runs <paramref name="work"/> on a thread of the pool with a manager made for it; the default when it fails or takes longer than <paramref name="ms"/>.</summary>
    static T? Run<T>(Func<IVirtualDesktopManagerInternal, IApplicationViewCollection?, T> work, int ms)
    {
        var task = Task.Run(() =>
        {
            try
            {
                var m = Manager(out var views);
                return m == null ? default : work(m, views);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException)
            {
                Log.Debug("virtual desktops (private): " + ex.Message);
                return default;
            }
        });
        if (task.Wait(ms)) return task.Result;
        Log.Warn($"virtual desktops (private): no answer within {ms} ms");
        return default;
    }

    static IVirtualDesktopManagerInternal? Manager(out IApplicationViewCollection? views)
    {
        views = null;
        if (Type.GetTypeFromCLSID(ImmersiveShell) is not { } type || Activator.CreateInstance(type) is not IServiceProvider10 shell) return null;
        Guid service = ManagerInternalService, iid = typeof(IVirtualDesktopManagerInternal).GUID;
        int hr = shell.QueryService(ref service, ref iid, out var m);
        Guid viewsIid = typeof(IApplicationViewCollection).GUID;
        if (shell.QueryService(ref viewsIid, ref viewsIid, out var v) >= 0) views = v as IApplicationViewCollection;
        return hr >= 0 ? m as IVirtualDesktopManagerInternal : null;
    }

    static List<(Guid Id, string Name, IVirtualDesktop Desktop)> List(IVirtualDesktopManagerInternal m)
    {
        var list = new List<(Guid, string, IVirtualDesktop)>();
        if (m.GetDesktops(out var array) < 0 || array == null || array.GetCount(out int n) < 0) return list;
        Guid iid = typeof(IVirtualDesktop).GUID;
        for (int i = 0; i < n; i++)
            if (array.GetAt(i, ref iid, out var o) >= 0 && o is IVirtualDesktop d && d.GetId(out var id) >= 0)
                list.Add((id, d.GetName(out var h) >= 0 ? Text(h) : "", d));
        return list;
    }

    static IVirtualDesktop? Find(IVirtualDesktopManagerInternal m, Guid id) => List(m).FirstOrDefault(d => d.Id == id).Desktop;

    static string Text(IntPtr hstring)
    {
        if (hstring == IntPtr.Zero) return "";
        var raw = WindowsGetStringRawBuffer(hstring, out uint length);
        string s = Marshal.PtrToStringUni(raw, (int)length);
        WindowsDeleteString(hstring);
        return s;
    }
}
