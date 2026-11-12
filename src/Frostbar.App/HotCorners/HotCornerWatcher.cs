using System.IO;
using System.Windows.Threading;
using Frostbar.Core.HotCorners;
using Frostbar.Core.Settings;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.App.HotCorners;

/// <summary>
/// Watches the pointer for the hot corners (M7) on the background ticker, without a hook (anti-cheat): every 100 ms
/// while it is away from the corners, every 16 ms once it is near one, so the dwell is kept to about a frame. Not while
/// a mouse button is down, a fullscreen app is in front, or the chosen modifier is up (<see cref="HotCornerTracker"/>).
/// A corner that fires runs its action on the UI thread.
/// </summary>
public sealed class HotCornerWatcher : IDisposable
{
    const string Job = "hot-corners";
    const int Near = 64; // pixels from a corner's point: look more often
    static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(100), Fast = TimeSpan.FromMilliseconds(16);
    static readonly Corner[] Corners = [Corner.TopLeft, Corner.TopRight, Corner.BottomLeft, Corner.BottomRight];

    readonly AppSettings _settings;
    readonly BackgroundTicker _ticker;
    readonly Dispatcher _ui;
    readonly Action<string> _run;
    readonly object _lock = new();
    HotCornerTracker _tracker;
    ScreenEdges _edges;
    volatile bool _fullScreen;
    bool _fast, _havePoint;
    uint _lastInput; // the last input's time at the last full look (GetTickCount ms)
    POINT _point; // where the pointer was at the last input

    /// <summary>The last corner that fired and when (TickCount64 ms), for the specs.</summary>
    public (Corner corner, long at) LastFired { get; private set; }
    public int Fired { get; private set; }

    public HotCornerWatcher(AppSettings settings, BackgroundTicker ticker, Dispatcher ui, ScreenEdges edges, Action<string> run)
    {
        _settings = settings;
        _ticker = ticker;
        _ui = ui;
        _run = run;
        _edges = edges;
        _tracker = new HotCornerTracker(settings.HotCorners.DwellMs);
        _ticker.Add(Job, Slow, Poll);
    }

    /// <summary>The screen moved or changed size.</summary>
    public void SetEdges(ScreenEdges e) { lock (_lock) _edges = e; }

    /// <summary>A fullscreen app came to the front or left it (hot corners rest meanwhile).</summary>
    public void SetFullScreen(bool on) => _fullScreen = on;

    /// <summary>The settings changed (the dwell is read again; the actions and the modifier are read at every look).</summary>
    public void SettingsChanged() { lock (_lock) _tracker = new HotCornerTracker(_settings.HotCorners.DwellMs); }

    public string ActionOf(Corner c)
    {
        var h = _settings.HotCorners;
        return c switch
        {
            Corner.TopLeft => h.TopLeft,
            Corner.TopRight => h.TopRight,
            Corner.BottomLeft => h.BottomLeft,
            Corner.BottomRight => h.BottomRight,
            _ => "none",
        };
    }

    bool ModifierHeld() => _settings.HotCorners.Modifier switch
    {
        "ctrl" => Native.KeyDown(0x11),
        "alt" => Native.KeyDown(0x12),
        "shift" => Native.KeyDown(0x10),
        "win" => Native.KeyDown(0x5B) || Native.KeyDown(0x5C),
        _ => true,
    };

    void Poll()
    {
        ScreenEdges e;
        HotCornerTracker tracker;
        lock (_lock) { e = _edges; tracker = _tracker; }
        long now = Environment.TickCount64;
        // No input since the last look: the pointer has not moved. Reading it costs about 0.15 ms of a cold CPU ten
        // times a second (0.2% of a core while idle, 2026-10-12); asking for the last input, 0.015. Without input only a
        // dwell under way can change anything, and it is looked at where the pointer was: until 2026-10-15 a pointer
        // left near a corner was read 62 times a second for as long as it stayed there.
        uint input = Native.LastInputTick();
        bool moved = input != _lastInput || !_havePoint;
        if (!moved && tracker.Waiting.corner == Corner.None)
        {
            if (tracker.Idle) tracker.Away(now);
            if (_fast) { _fast = false; _ticker.Retime(Job, Slow); } // input brings the quick looks back
            return;
        }
        if (moved)
        {
            _lastInput = input;
            if (!Native.GetCursorPos(out _point)) return;
            _havePoint = true;
        }
        var p = _point;
        long entered = now - (uint)((uint)Environment.TickCount - input);
        // Near a corner: look often enough to keep the dwell; elsewhere rarely (idle CPU).
        bool near = false;
        foreach (var c in Corners)
        {
            var (x, y) = HotCornerTracker.Point(c, e);
            if (Math.Max(Math.Abs(p.X - x), Math.Abs(p.Y - y)) <= Near && ActionOf(c) != "none") { near = true; break; }
        }
        if (near != _fast)
        {
            _fast = near;
            _ticker.Retime(Job, near ? Fast : Slow);
        }
        // Far from the corners with nothing pending, that is all.
        if (!near && tracker.Idle) { tracker.Away(now); return; }
        bool button = Native.KeyDown(0x01) || Native.KeyDown(0x02) || Native.KeyDown(0x04);
        // Where Explorer's taskbar shows (development runs, or "hide the taskbar" off), its corners are its own: the
        // Start button and "show desktop" are there (2026-10-12).
        bool taskbar = HotCornerTracker.At(p.X, p.Y, e) != Corner.None && OverExplorerTaskbar(p);
        // The pointer's arrival is the last input (looks 100 ms apart would otherwise add up to 100 ms to the dwell).
        var fired = tracker.Update(p.X, p.Y, e, now, button, ModifierHeld(), _fullScreen, c => ActionOf(c) != "none" && !taskbar, entered);
        if (fired != Corner.None)
        {
            string action = ActionOf(fired);
            LastFired = (fired, Environment.TickCount64);
            Fired++;
            Stats.Inc("hotcorner.fired");
            Log.Debug($"hot corner: {fired} → {action}");
            _ui.BeginInvoke(() => _run(action));
        }
    }

    static bool OverExplorerTaskbar(POINT p)
    {
        var h = Native.WindowFromPoint(p);
        if (h == IntPtr.Zero) return false;
        var root = Native.GetAncestor(h, 2 /*GA_ROOT*/);
        if (Native.ClassName(root) is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")) return false;
        // Explorer's, not the window of that class our tray (ManagedShell) makes.
        return string.Equals(Path.GetFileName(Native.ProcessPath(Native.ProcessId(root)) ?? ""), "explorer.exe", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _ticker.Remove(Job);
}
