using System.Windows.Interop;
using System.Windows.Threading;
using Frostbar.Core.Shell;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Shell;

/// <summary>
/// Windows' own volume and brightness display (ADR-015): Explorer's XamlExplorerHostIslandWindow, shown again by
/// Explorer for every key. While the shell shows its own, the window is kept from being drawn by an empty window region
/// (another process may set one; no DLL goes into Explorer), and it is given back when the shell leaves: here, by the
/// watchdog and by the next start (state.json's volumeDisplay). It also brings the keys' signals: the volume keys'
/// APPCOMMAND through a shell hook window of its own (the Dock's window list is made again at times), and Explorer's
/// island windows coming out (an out-of-context WinEvent on Explorer's process only, nothing system-wide). The display
/// is told from Explorer's other islands (Alt+Tab's, snap layouts') by being small and coming out right after a volume
/// key or around a brightness change (OsdJudge); from then on its coming out tells that a key was pressed, and which.
/// </summary>
public sealed class VolumeDisplay : IDisposable
{
    public const string IslandClass = "XamlExplorerHostIslandWindow";
    const uint EVENT_OBJECT_SHOW = 0x8002;
    const int HSHELL_APPCOMMAND = 12;

    readonly StateStore _store;
    readonly HwndSource _hookWindow;
    readonly uint _shellHookMessage;
    readonly Native.WinEventProc _proc;
    readonly DispatcherTimer _wait;
    IntPtr _hook;
    uint _explorerPid;
    bool _hide, _disposed;
    long? _appCommandAt, _brightnessAt;
    /// <summary>An island that came out with no reason yet (a brightness change may still follow).</summary>
    (long at, IntPtr hwnd)? _waiting;

    /// <summary>Windows' display (zero until it is told apart).</summary>
    public IntPtr Display { get; private set; }
    /// <summary>The empty region is on it now.</summary>
    public bool Hidden => Display != IntPtr.Zero && RegionKind(Display) == 1;
    /// <summary>It is out now (drawn or not): a key was pressed in the last 2.2 s.</summary>
    public bool Out => Display != IntPtr.Zero && Native.IsWindowVisible(Display);
    /// <summary>A volume key: mute (8), down (9) or up (10).</summary>
    public event Action<int>? VolumeKey;
    /// <summary>Windows' display came out (drawn or not), and why.</summary>
    public event Action<OsdCause>? Shown;
    /// <summary>Diagnostics: the volume keys heard, and the display's showings.</summary>
    public int Keys { get; private set; }
    public int Showings { get; private set; }

    public VolumeDisplay(StateStore store, Dispatcher ui, bool hide)
    {
        _store = store;
        _hide = hide;
        _wait = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(OsdJudge.BrightnessWithinMs) };
        _wait.Tick += (_, _) => GiveUpWaiting();
        // A hidden top-level window (WS_POPUP, never shown: no WS_VISIBLE) hears the shell hook.
        _hookWindow = new HwndSource(new HwndSourceParameters("Frostbar.KeysHook") { WindowStyle = unchecked((int)0x80000000), Width = 0, Height = 0 });
        _shellHookMessage = Native.RegisterWindowMessage("SHELLHOOK");
        _hookWindow.AddHook(Hook);
        if (!Native.RegisterShellHookWindow(_hookWindow.Handle)) Log.Warn("volume display: RegisterShellHookWindow failed (the volume keys are not heard)");
        _proc = (hook, ev, hwnd, obj, child, thread, time) =>
        {
            // A Windows event's callback: a failure in it is logged (it ended the process by the emergency path).
            try { OnShow(hook, ev, hwnd, obj, child, thread, time); }
            catch (Exception ex) { Log.Warn("volume display: a window shown could not be looked at", ex); }
        };
        Attach();
    }

    /// <summary>Follows the Explorer that runs now; takes up the display the last run kept hidden if it is this Explorer's.</summary>
    void Attach()
    {
        var bar = Taskbar.Primary();
        _explorerPid = bar != IntPtr.Zero ? Native.ProcessId(bar) : 0;
        if (_explorerPid == 0) { Log.Warn("volume display: Explorer's taskbar not found (Windows' display stays)"); return; }
        _hook = Native.SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, _explorerPid, 0, Native.WINEVENT_OUTOFCONTEXT);
        var saved = _store.Load().VolumeDisplay;
        if (saved == null) return;
        var h = new IntPtr(saved.Hwnd);
        if (saved.ExplorerPid == _explorerPid && IsIsland(h, _explorerPid))
        {
            // Told apart by an earlier run of this Explorer: hidden from the first key on (no showing of Windows' first).
            Display = h;
            Log.Info($"volume display: 0x{h:X} known from the last run{(saved.Hidden ? " (left hidden)" : "")}");
            if (_hide) Hide(); else GiveBack();
        }
        else _store.Update(s => s.VolumeDisplay = null); // an Explorer gone by: its window went with it
    }

    static bool IsIsland(IntPtr h, uint explorerPid) =>
        Native.IsWindow(h) && Native.ProcessId(h) == explorerPid && Native.ClassName(h) == IslandClass;

    /// <summary>0 no region, 1 an empty one, 2 or 3 a shape.</summary>
    static int RegionKind(IntPtr h)
    {
        IntPtr r = Native.CreateRectRgn(0, 0, 0, 0);
        try { return Native.GetWindowRgn(h, r); }
        finally { Native.DeleteObject(r); }
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg != _shellHookMessage || wParam.ToInt32() != HSHELL_APPCOMMAND) return IntPtr.Zero;
        int command = (int)((lParam.ToInt64() >> 16) & 0x0FFF); // GET_APPCOMMAND_LPARAM: the device bits off
        if (!OsdJudge.IsVolumeKey(command)) return IntPtr.Zero;
        _appCommandAt = Environment.TickCount64;
        Keys++;
        Stats.Inc("hud.volumeKey");
        Log.Debug($"volume display: volume key {command}");
        VolumeKey?.Invoke(command);
        return IntPtr.Zero; // not handled: Explorer hears it too and changes the volume
    }

    void OnShow(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_disposed || idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;
        if (Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd || Native.ClassName(hwnd) != IslandClass) return;
        long now = Environment.TickCount64;
        if (hwnd == Display)
        {
            Showings++;
            if (_hide && RegionKind(hwnd) != 1) Hide(); // given back meanwhile (the watchdog while the shell hung)
            Decide(now, hwnd);
            return;
        }
        if (Display != IntPtr.Zero && Native.IsWindow(Display)) return; // another of Explorer's islands
        Native.GetWindowRect(hwnd, out var r);
        if (!OsdJudge.LooksLikeDisplay(r.Width, r.Height, Monitors.Of(hwnd)?.Scale ?? 1.25)) return;
        Decide(now, hwnd);
    }

    /// <summary>Why it came out; with no reason yet, a brightness change may still follow for a moment.</summary>
    void Decide(long now, IntPtr hwnd)
    {
        var cause = OsdJudge.Cause(now, _appCommandAt, _brightnessAt);
        if (cause == OsdCause.Unknown)
        {
            _waiting = (now, hwnd);
            _wait.Stop();
            _wait.Start();
            return;
        }
        Found(hwnd, cause);
    }

    void Found(IntPtr hwnd, OsdCause cause)
    {
        if (hwnd != Display)
        {
            Display = hwnd;
            Showings++;
            Log.Info($"volume display: Windows' display is 0x{hwnd:X} (Explorer {_explorerPid}; it came out for {cause})");
            if (_hide) Hide(); else Save(hidden: false);
        }
        Stats.Inc("hud.windowsDisplay");
        Shown?.Invoke(cause);
    }

    /// <summary>The brightness changed (not by the shell itself): a display out with no reason yet was the brightness's.</summary>
    public void NoteBrightness()
    {
        long now = Environment.TickCount64;
        _brightnessAt = now;
        if (_waiting is not { } w || now - w.at > OsdJudge.BrightnessWithinMs) return;
        _wait.Stop();
        _waiting = null;
        Found(w.hwnd, OsdCause.Brightness);
    }

    void GiveUpWaiting()
    {
        _wait.Stop();
        if (_waiting is { } w && w.hwnd == Display)
        {
            Log.Debug("volume display: Windows' display came out with no volume key or brightness change around it");
            Shown?.Invoke(OsdCause.Unknown);
        }
        _waiting = null;
    }

    /// <summary>The setting: Frostbar's display (Windows' kept from showing) or Windows' own.</summary>
    public void SetHide(bool hide)
    {
        _hide = hide;
        if (hide) Hide(); else GiveBack();
    }

    void Hide()
    {
        if (Display == IntPtr.Zero || !IsIsland(Display, _explorerPid)) return;
        // Recorded before the region goes on (as the taskbar's state before it is touched): every way back (the end, the
        // watchdog, the next start) takes off only what is recorded. Recorded after, a failed write or an end between
        // the two left Windows' display invisible with nothing to give it back. Not recordable: not hidden.
        try { Save(hidden: true); }
        catch (Exception ex) { Log.Warn("volume display: not recorded, so not hidden", ex); return; }
        if (RegionKind(Display) != 1)
        {
            IntPtr empty = Native.CreateRectRgn(0, 0, 0, 0);
            if (Native.SetWindowRgn(Display, empty, true) == 0)
            {
                Native.DeleteObject(empty);
                Log.Warn($"volume display: the empty region was refused for 0x{Display:X}");
                try { Save(hidden: false); } catch (Exception ex) { Log.Warn("volume display: not recorded", ex); }
                return;
            }
            Log.Info($"volume display: Windows' display 0x{Display:X} kept from showing");
        }
    }

    void GiveBack()
    {
        if (Display == IntPtr.Zero || !IsIsland(Display, _explorerPid)) { _store.Update(s => s.VolumeDisplay = null); return; }
        if (RegionKind(Display) != 0)
        {
            Native.SetWindowRgn(Display, IntPtr.Zero, true);
            Log.Info($"volume display: Windows' display 0x{Display:X} given back");
        }
        Save(hidden: false);
    }

    /// <summary>The display as told apart, and whether the region is on it (state.json: the watchdog and the next start).</summary>
    void Save(bool hidden)
    {
        long hwnd = Display.ToInt64();
        int pid = (int)_explorerPid;
        _store.Update(s => s.VolumeDisplay = new VolumeDisplayState { Hwnd = hwnd, ExplorerPid = pid, Hidden = hidden });
    }

    /// <summary>Explorer started again: its display is a new window, told apart again at the next key.</summary>
    public void ExplorerRestarted()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        Display = IntPtr.Zero;
        _waiting = null;
        _store.Update(s => s.VolumeDisplay = null);
        Attach();
        Log.Info($"volume display: following Explorer {_explorerPid} again");
    }

    /// <summary>
    /// The watchdog and the next start: gives back the display a run left hidden (only the very window it hid: a
    /// window number of an Explorer gone by may be somebody else's). Returns what it did, or "" when there was nothing.
    /// </summary>
    public static string GiveBack(StateStore store)
    {
        var saved = store.Load().VolumeDisplay;
        if (saved is not { Hidden: true }) return "";
        var h = new IntPtr(saved.Hwnd);
        bool there = IsIsland(h, (uint)saved.ExplorerPid);
        if (there) Native.SetWindowRgn(h, IntPtr.Zero, true);
        // Still known (the next start of this Explorer hides it from its first key); gone with its Explorer otherwise.
        store.Update(s => s.VolumeDisplay = there ? new VolumeDisplayState { Hwnd = saved.Hwnd, ExplorerPid = saved.ExplorerPid, Hidden = false } : null);
        return there ? $"Windows' volume display 0x{h:X} given back" : "Windows' volume display: its window is gone (Explorer started again)";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wait.Stop();
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        GiveBack();
        Native.DeregisterShellHookWindow(_hookWindow.Handle);
        _hookWindow.Dispose();
    }
}
