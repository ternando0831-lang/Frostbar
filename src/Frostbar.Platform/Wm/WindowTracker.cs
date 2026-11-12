using System.Windows.Interop;
using System.Windows.Threading;
using Frostbar.Platform.Identity;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Wm;

/// <summary>An app window as the Dock sees it (<paramref name="Elsewhere"/>: on another virtual desktop).</summary>
public sealed record TrackedWindow(IntPtr Hwnd, string AppId, string AppName, string? ExePath, bool Minimized, bool Elsewhere = false);

/// <summary>
/// The app windows (Explorer's taskbar rule, <see cref="WindowList"/>), top to bottom, with their AppIDs. Kept up to
/// date by the shell hook (created, destroyed, flashing, activated) and by show/hide/cloak/minimize WinEvents; bursts
/// are coalesced into one rebuild. An AppID is read again during a window's first seconds: apps such as Chromium set
/// their window's AppUserModelID a moment after creating it.
/// </summary>
public sealed class WindowTracker : IDisposable
{
    const uint EVENT_SYSTEM_MINIMIZESTART = 0x16, EVENT_SYSTEM_MINIMIZEEND = 0x17,
        EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018;
    const int HSHELL_WINDOWCREATED = 1, HSHELL_WINDOWDESTROYED = 2, HSHELL_WINDOWACTIVATED = 4, HSHELL_GETMINRECT = 5, HSHELL_WINDOWREPLACED = 13,
        HSHELL_RUDEAPPACTIVATED = 0x8004, HSHELL_FLASH = 0x8006;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct SHELLHOOKINFO { public IntPtr hwnd; public RECT rc; }

    /// <summary>
    /// Where a window minimizes to and restores from (screen pixels): Windows asks every shell hook window before it
    /// animates a minimize or a restore, one after the other, and the last answer stands. Null leaves the rectangle as
    /// the others gave it (Explorer's taskbar button). The rectangle travels packed in 16-bit halves (an old kludge):
    /// rc.left = MAKELONG(left, top), rc.top = MAKELONG(right, bottom) (measured 2026-10-12: Explorer's answer for a
    /// button of the hidden taskbar came as left 0x03FC0048, top 0x043800C9 = 72,1020 – 201,1080).
    /// </summary>
    public Func<IntPtr, RECT?>? MinimizeTarget { get; set; }

    /// <summary>Diagnostics: the last rectangle asked for, as it came and as we left it.</summary>
    public (IntPtr hwnd, RECT given, RECT? answered, long at) LastMinRect { get; private set; }

    readonly AppIdentity _identity;
    readonly HwndSource _hookWindow;
    readonly uint _shellHookMessage;
    readonly Native.WinEventProc _proc;
    readonly List<IntPtr> _hooks = new();
    readonly DispatcherTimer _settle;
    readonly Dictionary<IntPtr, (string appId, string name, string? exe, long seenAt)> _known = new();

    public IReadOnlyList<TrackedWindow> Windows { get; private set; } = [];
    public event Action? Changed;
    /// <summary>A window asks for attention (FlashWindowEx): the Dock bounces its app.</summary>
    public event Action<IntPtr>? Flashing;
    /// <summary>A window came to the front (stops a bounce).</summary>
    public event Action<IntPtr>? Activated;
    /// <summary>A new app window appeared (ends a launch bounce).</summary>
    public event Action<IntPtr>? Created;

    public WindowTracker(AppIdentity identity, Dispatcher ui)
    {
        _identity = identity;
        _settle = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) => { _settle!.Stop(); Rebuild(); }, ui);
        // A hidden top-level window (WindowStyle WS_POPUP, never shown) receives the shell hook's messages.
        _hookWindow = new HwndSource(new HwndSourceParameters("Frostbar.ShellHook") { WindowStyle = unchecked((int)0x80000000), Width = 0, Height = 0 });
        _shellHookMessage = Native.RegisterWindowMessage("SHELLHOOK");
        _hookWindow.AddHook(Hook);
        if (!Native.RegisterShellHookWindow(_hookWindow.Handle)) Log.Warn("RegisterShellHookWindow failed: windows are followed through WinEvents only");

        _proc = (_, ev, hwnd, idObject, idChild, _, _) =>
        {
            if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;
            if (ev is EVENT_OBJECT_SHOW or EVENT_OBJECT_HIDE && Native.GetAncestor(hwnd, 2 /*GA_ROOT*/) != hwnd) return;
            Kick();
        };
        foreach (var (min, max) in new[] { (EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND), (EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE), (EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED) })
            _hooks.Add(Native.SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT));
        Rebuild();
    }

    void Kick()
    {
        if (!_settle.IsEnabled) _settle.Start();
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg != _shellHookMessage) return IntPtr.Zero;
        switch (wParam.ToInt32())
        {
            case HSHELL_WINDOWCREATED: Kick(); Created?.Invoke(lParam); break;
            case HSHELL_WINDOWDESTROYED or HSHELL_WINDOWREPLACED: Kick(); break;
            case HSHELL_FLASH: Flashing?.Invoke(lParam); break;
            case HSHELL_WINDOWACTIVATED or HSHELL_RUDEAPPACTIVATED: Activated?.Invoke(lParam); break;
            case HSHELL_GETMINRECT when lParam != IntPtr.Zero:
            {
                var info = System.Runtime.InteropServices.Marshal.PtrToStructure<SHELLHOOKINFO>(lParam);
                var given = new RECT((short)(info.rc.Left & 0xFFFF), (short)(info.rc.Left >> 16), (short)(info.rc.Top & 0xFFFF), (short)(info.rc.Top >> 16));
                RECT? to = null;
                try { to = MinimizeTarget?.Invoke(info.hwnd); }
                catch (Exception ex) { Log.Warn("minimize target", ex); }
                LastMinRect = (info.hwnd, given, to, Environment.TickCount64);
                Log.Debug($"GETMINRECT 0x{info.hwnd:X}: given {given.Left},{given.Top},{given.Right},{given.Bottom}" + (to is { } t ? $" -> {t.Left},{t.Top},{t.Right},{t.Bottom}" : ""));
                if (to is { } r)
                {
                    static int Pack(int lo, int hi) => (ushort)(short)lo | ((ushort)(short)hi << 16);
                    info.rc.Left = Pack(r.Left, r.Top);
                    info.rc.Top = Pack(r.Right, r.Bottom);
                    System.Runtime.InteropServices.Marshal.StructureToPtr(info, lParam, false);
                    handled = true;
                    return new IntPtr(1);
                }
                break;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Reads the window list again now (also after start-up, once the Start menu names are known).</summary>
    public void Rebuild()
    {
        Stats.Inc("windows.rebuild");
        long now = Environment.TickCount64;
        var list = new List<TrackedWindow>();
        var seen = new HashSet<IntPtr>();
        foreach (var (h, elsewhere) in WindowList.AppWindowsOfAllDesktops())
        {
            seen.Add(h);
            if (!_known.TryGetValue(h, out var k) || now - k.seenAt < 5000)
            {
                string appId = _identity.AppIdForWindow(h);
                string? exe = Native.ProcessPath(Native.AppOrWindowProcessId(h)); // a packaged app's own exe, not ApplicationFrameHost's
                k = (appId, _identity.DisplayName(appId, exe), exe, _known.TryGetValue(h, out var old) ? old.seenAt : now);
                _known[h] = k;
            }
            list.Add(new TrackedWindow(h, k.appId, k.name, k.exe, Native.IsIconic(h), elsewhere));
        }
        foreach (var gone in _known.Keys.Where(h => !seen.Contains(h)).ToList()) _known.Remove(gone);
        if (list.SequenceEqual(Windows)) return;
        Windows = list;
        Changed?.Invoke();
    }

    /// <summary>Forgets cached names (after the Start menu catalog loads).</summary>
    public void Refresh()
    {
        _known.Clear();
        Rebuild();
    }

    /// <summary>
    /// Explorer restarted and registered its shell hook window again, after ours: register ours again so that it is the
    /// last one asked, and our minimize rectangles stand over the taskbar's.
    /// </summary>
    public void RegisterAgain()
    {
        if (_hookWindow.IsDisposed) return;
        Native.DeregisterShellHookWindow(_hookWindow.Handle);
        if (!Native.RegisterShellHookWindow(_hookWindow.Handle)) Log.Warn("RegisterShellHookWindow failed again");
    }

    public void Dispose()
    {
        _settle.Stop();
        foreach (var h in _hooks) if (h != IntPtr.Zero) Native.UnhookWinEvent(h);
        _hooks.Clear();
        Native.DeregisterShellHookWindow(_hookWindow.Handle);
        _hookWindow.Dispose();
    }
}
