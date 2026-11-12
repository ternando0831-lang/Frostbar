using System.Runtime.InteropServices;
using System.Windows.Interop;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Shell;

/// <summary>
/// Reserves screen edges through the app-bar API so maximized windows stay clear of the menu bar and the Dock.
/// A bar window may stay hidden (the Dock's bottom reservation is never shown: measured in M0 S3).
/// Bars are registered again when Explorer restarts, and their handles go to state.json so a crash can be cleaned up.
/// </summary>
public sealed class AppBarHost : IDisposable
{
    public sealed class Bar
    {
        public required IntPtr Hwnd { get; init; }
        public required uint Edge { get; init; }
        public double SizeDip { get; set; }
        public IntPtr Monitor { get; set; }
        public bool PositionWindow { get; init; }
        public RECT Rect { get; internal set; }
        /// <summary>Freshly registered (ABM_NEW), or its SETPOS not answered: SETPOS must be sent even if the rectangle did not change.</summary>
        internal bool Fresh { get; set; } = true;
        /// <summary>The window's hook for Explorer's notifications (taken off with the bar).</summary>
        internal HwndSourceHook? Hook { get; set; }
    }

    bool _placePending;

    static readonly uint CallbackMessage = Native.RegisterWindowMessage("Frostbar.AppBar");
    readonly List<Bar> _bars = new();
    readonly StateStore _store;

    public AppBarHost(StateStore store) => _store = store;

    public IReadOnlyList<Bar> Bars => _bars;
    /// <summary>Explorer's fullscreen notification for a monitor (sent to every bar on it).</summary>
    public event Action<IntPtr, bool>? FullScreenApp;
    /// <summary>ABN_STATECHANGE: the taskbar's auto-hide or always-on-top setting changed (sent to every bar).</summary>
    public event Action? TaskbarStateChanged;
    public event Action<Bar>? Placed;

    /// <param name="positionWindow">Move/resize the window to the reserved rectangle (false for windows sized by their owner).</param>
    public Bar Register(IntPtr hwnd, uint edge, double sizeDip, IntPtr monitor, bool positionWindow)
    {
        var bar = new Bar { Hwnd = hwnd, Edge = edge, SizeDip = sizeDip, Monitor = monitor, PositionWindow = positionWindow };
        // Taken off in Remove: a window registered again (the bar's hiding switched) stacked hooks, and each of
        // Explorer's notifications came once for each (2026-11-01 review, left then).
        bar.Hook = (IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) => Hook(bar, msg, wp, lp);
        HwndSource.FromHwnd(hwnd)?.AddHook(bar.Hook);
        New(bar);
        _bars.Add(bar);
        Place(bar);
        _store.Update(s => { if (!s.AppBars.Contains(hwnd.ToInt64())) s.AppBars.Add(hwnd.ToInt64()); });
        return bar;
    }

    static void New(Bar bar)
    {
        var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = bar.Hwnd, uCallbackMessage = CallbackMessage };
        AppBarMessenger.Send(Native.ABM_NEW, ref d);
        bar.Fresh = true;
    }

    public void Place(Bar b)
    {
        var mon = Monitors.Get(b.Monitor) ?? Monitors.Primary();
        int px = mon.Px(b.SizeDip);
        var r = mon.Bounds;
        var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = b.Hwnd, uEdge = b.Edge, rc = r };
        switch (b.Edge)
        {
            case Native.ABE_TOP: d.rc.Bottom = r.Top + px; break;
            case Native.ABE_BOTTOM: d.rc.Top = r.Bottom - px; break;
            case Native.ABE_LEFT: d.rc.Right = r.Left + px; break;
            case Native.ABE_RIGHT: d.rc.Left = r.Right - px; break;
        }
        AppBarMessenger.Send(Native.ABM_QUERYPOS, ref d);
        switch (b.Edge)
        {
            case Native.ABE_TOP: d.rc.Bottom = d.rc.Top + px; break;
            case Native.ABE_BOTTOM: d.rc.Top = d.rc.Bottom - px; break;
            case Native.ABE_LEFT: d.rc.Right = d.rc.Left + px; break;
            case Native.ABE_RIGHT: d.rc.Left = d.rc.Right - px; break;
        }
        // SETPOS only when something changed: every SETPOS makes Explorer send ABN_POSCHANGED to all bars, and
        // answering each of those with another SETPOS ping-pongs (measured: ~10 rounds after hiding the taskbar).
        var cur = b.Rect;
        if (!b.Fresh && cur.Left == d.rc.Left && cur.Top == d.rc.Top && cur.Right == d.rc.Right && cur.Bottom == d.rc.Bottom) return;
        AppBarMessenger.Send(Native.ABM_SETPOS, ref d, out bool answered);
        if (!answered)
        {
            // Explorer busy (5 s): not taken as placed, or no later placing would send it again (the strip never
            // reserved, or left reserved; 2026-11-06 review). Asked again a moment later.
            b.Fresh = true;
            Log.Warn($"app bar {b.Edge}: Explorer did not answer its position: asked again in 2 s");
            AgainLater();
            return;
        }
        b.Fresh = false;
        b.Rect = d.rc;
        if (b.PositionWindow)
            Native.SetWindowPos(b.Hwnd, Native.HWND_TOPMOST, d.rc.Left, d.rc.Top, d.rc.Width, d.rc.Height, Native.SWP_NOACTIVATE);
        Log.Debug($"app bar {b.Edge} placed at {d.rc} ({b.SizeDip} DIP = {px} px)");
        Placed?.Invoke(b);
    }

    IntPtr Hook(Bar bar, int msg, IntPtr wp, IntPtr lp)
    {
        if ((uint)msg != CallbackMessage) return IntPtr.Zero;
        switch (wp.ToInt32())
        {
            case Native.ABN_POSCHANGED:
                // Several bars get the same notification: re-place them all once, after the burst.
                if (!_placePending)
                {
                    _placePending = true;
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                    {
                        _placePending = false;
                        PlaceAll();
                    });
                }
                break;
            case Native.ABN_FULLSCREENAPP: FullScreenApp?.Invoke(bar.Monitor, lp != IntPtr.Zero); break;
            case Native.ABN_STATECHANGE: TaskbarStateChanged?.Invoke(); break;
        }
        return IntPtr.Zero;
    }

    /// <summary>After an Explorer restart every registration is gone.</summary>
    public void RegisterAgain()
    {
        foreach (var b in _bars) { New(b); Place(b); }
        Log.Info($"app bars registered again ({_bars.Count})");
    }

    public void PlaceAll() { foreach (var b in _bars) Place(b); }

    System.Windows.Threading.DispatcherTimer? _again;

    void AgainLater()
    {
        _again ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(2), System.Windows.Threading.DispatcherPriority.Background, (_, _) =>
        {
            _again!.Stop();
            PlaceAll();
        }, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _again.Stop();
        _again.Start();
    }

    public void Remove(Bar b)
    {
        var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = b.Hwnd };
        AppBarMessenger.Send(Native.ABM_REMOVE, ref d, out bool answered);
        if (!answered) Log.Warn($"app bar {b.Edge}: Explorer did not answer its removal (its strip may stay until Explorer drops it)");
        if (b.Hook != null) HwndSource.FromHwnd(b.Hwnd)?.RemoveHook(b.Hook);
        b.Hook = null;
        _bars.Remove(b);
        _store.Update(s => s.AppBars.Remove(b.Hwnd.ToInt64()));
    }

    public void RemoveAll() { foreach (var b in _bars.ToList()) Remove(b); }

    /// <summary>Bars a crashed run left behind (Explorer usually drops them itself once the window is gone).</summary>
    public static void RemoveStale(IEnumerable<long> handles)
    {
        foreach (var h in handles)
        {
            var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = new IntPtr(h) };
            AppBarMessenger.Send(Native.ABM_REMOVE, ref d);
        }
    }

    public void Dispose()
    {
        _again?.Stop();
        RemoveAll();
    }
}
