using Frostbar.Platform.Identity;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Wm;

/// <summary>The app in front, as the menu bar shows it.</summary>
public sealed record FrontWindow(IntPtr Hwnd, uint Pid, string? ExePath, string AppId, string DisplayName, bool IsDesktop, bool Elevated)
{
    public string ExeName => Path.GetFileName(ExePath ?? "");
}

/// <summary>
/// Follows the foreground window and keeps the last *app* in front. Our own windows, Explorer's shell surfaces (Start,
/// search, notification centre, quick settings, Alt+Tab, IME candidate windows) and the lock screen leave it unchanged,
/// like macOS keeps showing the app while Spotlight is open. The desktop counts as Explorer (macOS shows Finder).
/// </summary>
public sealed class ForegroundTracker : IDisposable
{
    static readonly HashSet<string> ShellExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "searchhost.exe", "startmenuexperiencehost.exe", "shellexperiencehost.exe", "shellhost.exe", "lockapp.exe",
        "textinputhost.exe", "pickerhost.exe", "screenclippinghost.exe", "useroobebroker.exe", "credentialuibroker.exe",
    };
    static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "ForegroundStaging", "#32768", "MultitaskingViewFrame", "XamlExplorerHostIslandWindow",
        "TaskListThumbnailWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland", "Shell_InputSwitchTopLevelWindow",
    };

    readonly AppIdentity _identity;
    readonly Func<IntPtr, bool> _isOwn;
    readonly Native.WinEventProc _proc;
    IntPtr _hook;

    public FrontWindow? Current { get; private set; }
    public event Action<FrontWindow>? Changed;
    /// <summary>How many listen to <see cref="Changed"/> (diagnostics: a Dock or menu bar made again must not leave the old one listening).</summary>
    public int Listeners => Changed?.GetInvocationList().Length ?? 0;

    public ForegroundTracker(AppIdentity identity, Func<IntPtr, bool> isOwn, BackgroundTicker ticker)
    {
        _identity = identity;
        _isOwn = isOwn;
        _ticker = ticker;
        var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;

        // 1. The event's own window, never GetForegroundWindow(): right after a switch made through AttachThreadInput
        //    (ours or another app's) it still returns the previous window for a moment, and no second event follows
        //    (2026-10-12: every other switch between two test apps was missed; a re-check that trusted it reverted
        //    the bar to the previous app for up to 400 ms).
        // A Windows event's callback: what fails in it (a handler the bar or the Dock gave) is logged, as the window
        // room's (RoomWatch). It reached no dispatcher handler and ended the process by the emergency path.
        _proc = (_, _, hwnd, _, _, _, _) =>
        {
            try
            {
                Stats.Inc("foreground.event");
                _lastSeen = hwnd;
                _candidate = IntPtr.Zero;
                Track(hwnd != IntPtr.Zero ? hwnd : Native.GetForegroundWindow());
            }
            catch (Exception ex) { Log.Warn("foreground: a change could not be followed", ex); }
        };

        // 2. A starting app's window takes the foreground before it is shown, and no event follows when it appears:
        //    that same window is looked at again until it shows (every 150 ms, up to ~1.5 s; 2026-10-12: after a
        //    launch the bar kept showing the previous app).
        _waitShow = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _waitShow.Tick += (_, _) =>
        {
            if (_pending == IntPtr.Zero || --_waitForShow <= 0 || Evaluate(_pending) != Skip.NotShownYet)
            {
                _waitShow.Stop();
                _pending = IntPtr.Zero;
            }
        };

        // 3. Safety net for switches that raise no event at all (seen 2026-10-12): the ticker reads the foreground
        //    window every 400 ms (one cheap call, off the UI thread). A new value counts once it is seen twice in a
        //    row: the stale value right after a switch (see 1) lasts well under 400 ms.
        ticker.Add("foreground", TimeSpan.FromMilliseconds(400), () =>
        {
            var fg = Native.GetForegroundWindow();
            if (fg == _lastSeen) { _candidate = IntPtr.Zero; return; }
            if (fg != _candidate) { _candidate = fg; return; }
            _lastSeen = fg;
            _candidate = IntPtr.Zero;
            ui.BeginInvoke(() =>
            {
                var root = Native.GetAncestor(fg, Native.GA_ROOTOWNER);
                if (Current != null && (root != IntPtr.Zero ? root : fg) == Current.Hwnd) return;
                Stats.Inc("foreground.poll");
                Track(fg);
            });
        });
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    readonly System.Windows.Threading.DispatcherTimer _waitShow;
    readonly BackgroundTicker _ticker;
    volatile IntPtr _lastSeen, _candidate;
    IntPtr _pending;
    int _waitForShow;

    /// <summary>Evaluates a window that just came to the front; one still being shown is looked at again.</summary>
    void Track(IntPtr hwnd)
    {
        _waitShow.Stop();
        _pending = IntPtr.Zero;
        _waitForShow = 10;
        if (Evaluate(hwnd) == Skip.NotShownYet)
        {
            _pending = hwnd;
            _waitShow.Start();
        }
    }

    /// <summary>Why a foreground window did not become the front app.</summary>
    public enum Skip { None, NotAnApp, NotShownYet }

    public void Refresh()
    {
        Evaluate(Native.GetForegroundWindow());
        // At start-up the foreground can be nothing, a shell surface or one of ours: the top app window stands in.
        if (Current == null && WindowList.AppWindows().FirstOrDefault() is var top && top != IntPtr.Zero) Evaluate(top);
    }

    public Skip Evaluate(IntPtr fg)
    {
        if (fg == IntPtr.Zero) return Skip.NotAnApp;
        var root = Native.GetAncestor(fg, Native.GA_ROOTOWNER);
        if (root == IntPtr.Zero) root = fg;
        // What came forward without being an app is logged too: an own window taking the foreground hands it to whatever
        // window is next when it goes (2026-10-12: another app came to the front 1 ms after the menus' warm-up).
        if (_isOwn(root) || _isOwn(fg)) { Log.Debug($"foreground: own window {Native.ClassName(fg)} '{Native.Title(fg)}'"); return Skip.NotAnApp; }
        var cls = Native.ClassName(root);
        bool desktop = cls is "Progman" or "WorkerW";
        if (!desktop && ShellClasses.Contains(cls)) { Log.Debug($"foreground: shell surface {cls}"); return Skip.NotAnApp; }
        uint pid = Native.AppOrWindowProcessId(root); // a packaged app's own process, not ApplicationFrameHost
        var exe = Native.ProcessPath(pid);
        if (!desktop && exe != null && ShellExes.Contains(Path.GetFileName(exe))) { Log.Debug($"foreground: shell process {Path.GetFileName(exe)} {cls}"); return Skip.NotAnApp; }
        if (!desktop && !Native.IsWindowVisible(root)) return Skip.NotShownYet;

        string appId = desktop ? "Microsoft.Windows.Explorer" : _identity.AppIdForWindow(root);
        var f = new FrontWindow(root, pid, exe, appId, _identity.DisplayName(appId, exe), desktop, Native.IsMoreElevatedThanUs(pid));
        bool sameApp = Current != null && Current.AppId == f.AppId && Current.IsDesktop == f.IsDesktop;
        Current = f;
        if (!sameApp) Log.Debug($"front app: {f.DisplayName} ({Path.GetFileName(exe)}){(f.Elevated ? " [elevated]" : "")}");
        Changed?.Invoke(f);
        return Skip.None;
    }

    /// <summary>The front app's windows, top to bottom (resolved to the same AppID).</summary>
    public List<IntPtr> WindowsOf(FrontWindow app)
    {
        if (app.IsDesktop) return WindowList.AppWindows().Where(h => Native.ClassName(h) == "CabinetWClass").ToList();
        return WindowList.AppWindows().Where(h => Native.ProcessId(h) == app.Pid || _identity.AppIdForWindow(h) == app.AppId).ToList();
    }

    public void Dispose()
    {
        _ticker.Remove("foreground");
        _waitShow.Stop();
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }
}
