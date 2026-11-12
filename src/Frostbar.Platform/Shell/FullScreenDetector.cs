using System.Windows.Threading;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Shell;

/// <summary>
/// "Is a fullscreen app (game, video, presentation) in front on this monitor?" — then the menu bar and the Dock hide
/// and stop animating, so they never sit on top of a game. Two signals: Explorer's ABN_FULLSCREENAPP, and the
/// foreground window covering the whole monitor (Explorer misses some borderless games). Invisible and cloaked
/// windows never count: Explorer's own start-up windows covered the screen and kept the bars hidden (E2E 2026-10-12).
/// While any monitor is fullscreen, the background ticker watches the foreground window every 100 ms and wakes the UI
/// thread only when it or its rectangle changes: an app leaving fullscreen (a browser's F11) keeps the foreground, and
/// Explorer's notification can come late, so the bars came back only on the next 1 s check (2026-10-12: ~600 ms).
/// The covering window can also vanish without another window taking the foreground.
/// </summary>
public sealed class FullScreenDetector : IDisposable
{
    const string WatchJob = "fullscreen";
    readonly Func<IntPtr, bool> _isOwnWindow;
    readonly BackgroundTicker _ticker;
    readonly Dispatcher _ui;
    readonly Dictionary<IntPtr, bool> _explorerSays = new();
    readonly Dictionary<IntPtr, bool> _state = new();
    bool _watching;
    // Ticker thread only: what the foreground was at the last look.
    IntPtr _seenWindow;
    RECT _seenRect;
    IntPtr _hook;
    readonly Native.WinEventProc _proc;

    public event Action<IntPtr, bool>? Changed;

    public FullScreenDetector(Func<IntPtr, bool> isOwnWindow, BackgroundTicker ticker)
    {
        _isOwnWindow = isOwnWindow;
        _ticker = ticker;
        _ui = Dispatcher.CurrentDispatcher;
        // GetForegroundWindow() can still name the previous window when the event arrives (see ForegroundTracker):
        // look again once the switch has settled.
        _settle = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => { _settle!.Stop(); Evaluate(); }, _ui);
        _proc = (_, _, _, _, _, _, _) =>
        {
            // A Windows event's callback: a failure in a handler is logged (it ended the process by the emergency path).
            try { Evaluate(); }
            catch (Exception ex) { Log.Warn("fullscreen: not evaluated", ex); }
            _settle.Stop();
            _settle.Start();
        };
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    /// <summary>Ticker thread: has the foreground window, or its rectangle, changed since the last look?</summary>
    void Watch()
    {
        var fg = Native.GetForegroundWindow();
        Native.GetWindowRect(fg, out var r);
        if (fg == _seenWindow && r.Left == _seenRect.Left && r.Top == _seenRect.Top && r.Right == _seenRect.Right && r.Bottom == _seenRect.Bottom) return;
        _seenWindow = fg;
        _seenRect = r;
        _ui.BeginInvoke(DispatcherPriority.Normal, Evaluate);
    }

    readonly DispatcherTimer _settle;

    public bool IsFullScreen(IntPtr monitor) => _state.GetValueOrDefault(monitor);

    public void OnExplorerNotification(IntPtr monitor, bool fullscreen)
    {
        _explorerSays[monitor] = fullscreen;
        Evaluate();
    }

    /// <summary>
    /// Explorer started again: what the old one said is forgotten (the new one never says "left"; a game gone meanwhile
    /// kept the bar and the Dock hidden over a desktop until another fullscreen app came and went). A window that still
    /// covers a monitor is seen by its rectangle.
    /// </summary>
    public void ExplorerRestarted()
    {
        _explorerSays.Clear();
        Evaluate();
    }

    /// <summary>
    /// The displays changed: the monitors gone are forgotten (one kept as full screen kept the 100 ms watch going, and
    /// its "left" never came), and the rest looked at again.
    /// </summary>
    public void DisplaysChanged()
    {
        var now = Monitors.All().Select(m => m.Handle).ToHashSet();
        foreach (var gone in _state.Keys.Where(k => !now.Contains(k)).ToList()) _state.Remove(gone);
        foreach (var gone in _explorerSays.Keys.Where(k => !now.Contains(k)).ToList()) _explorerSays.Remove(gone);
        Evaluate();
    }

    public void Evaluate()
    {
        var covered = ForegroundCoveredMonitor(out string who);
        foreach (var m in Monitors.All())
        {
            bool byExplorer = _explorerSays.GetValueOrDefault(m.Handle) && !TopCoverIsOurs(m), byCover = covered == m.Handle;
            bool fs = byExplorer || byCover;
            if (_state.GetValueOrDefault(m.Handle) == fs) continue;
            _state[m.Handle] = fs;
            Log.Info(fs ? $"fullscreen entered on {m.Device} ({(byCover ? "covered by " + who : "Explorer's notification")})" : $"fullscreen left on {m.Device}");
            Changed?.Invoke(m.Handle, fs);
        }
        bool any = _state.Values.Any(v => v);
        if (any && !_watching)
        {
            _watching = true;
            _ticker.Add(WatchJob, TimeSpan.FromMilliseconds(100), Watch);
        }
        else if (!any && _watching)
        {
            _watching = false;
            _ticker.Remove(WatchJob);
        }
    }

    /// <summary>
    /// Explorer also takes our own topmost overlays for fullscreen apps (2026-10-12: the menu's click shield made the
    /// bar hide and close the menu it had just opened). Its claim is ignored when the topmost window covering the
    /// monitor is one of ours.
    /// </summary>
    bool TopCoverIsOurs(MonitorInfo m)
    {
        IntPtr top = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.Cloaked(h)) return true;
            Native.GetWindowRect(h, out var r);
            if (!r.Contains(m.Bounds) || Native.ClassName(h) is "Progman" or "WorkerW") return true;
            top = h;
            return false;
        }, IntPtr.Zero);
        return top != IntPtr.Zero && _isOwnWindow(top);
    }

    IntPtr ForegroundCoveredMonitor(out string who) => FrontCovers(_isOwnWindow, out who);

    /// <summary>
    /// The monitor a fullscreen app in front covers (the foreground window, not one of <paramref name="isOwnWindow"/>,
    /// over its whole monitor), or zero. Also for the taskbar's snapshot and restore, where Explorer itself keeps its
    /// taskbar below a fullscreen app.
    /// </summary>
    public static IntPtr FrontCovers(Func<IntPtr, bool> isOwnWindow, out string who)
    {
        who = "";
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Native.GetShellWindow() || isOwnWindow(fg)) return IntPtr.Zero;
        if (!Native.IsWindowVisible(fg) || Native.Cloaked(fg)) return IntPtr.Zero;
        var cls = Native.ClassName(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "ForegroundStaging") return IntPtr.Zero;
        var mon = Monitors.Of(fg);
        if (mon == null) return IntPtr.Zero;
        Native.GetWindowRect(fg, out var r);
        if (!r.Contains(mon.Bounds)) return IntPtr.Zero;
        // An ordinary maximized window covers the monitor whenever nothing reserves screen space (Explorer restarting,
        // our bars re-registering): that is not a fullscreen app (2026-10-12: a maximized Bipane hid the menu bar).
        // Fullscreen apps use borderless windows; a maximized window without a caption still counts.
        const long WS_CAPTION = 0x00C00000;
        if (Native.IsZoomed(fg) && (Native.GetWindowLongPtr(fg, Native.GWL_STYLE).ToInt64() & WS_CAPTION) == WS_CAPTION) return IntPtr.Zero;
        who = $"{cls} of {Path.GetFileName(Native.ProcessPath(Native.ProcessId(fg)) ?? "?")}";
        return mon.Handle;
    }

    public void Dispose()
    {
        _settle.Stop();
        if (_watching) _ticker.Remove(WatchJob);
        _watching = false;
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }
}
