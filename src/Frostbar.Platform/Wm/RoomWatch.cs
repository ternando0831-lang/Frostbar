using Frostbar.Core.Shell;
using Frostbar.Core.Wm;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Shell;

namespace Frostbar.Platform.Wm;

/// <summary>
/// Puts an app window shown for the first time between the menu bar and the Dock at once (<see cref="WindowRoom"/>,
/// 2026-10-24): EVENT_OBJECT_SHOW, or its uncloaking (a packaged app's frame shows cloaked). Windows open before the shell
/// started count as opened already, and a window an app moves later is left where it puts it: afterwards Windows keeps
/// windows below the menu bar's strip itself (window-room). A maximized or minimized window is left alone (Windows keeps a
/// maximized one in the work area), and so is one the <c>mayMove</c> refuses (a spec's shell moves its test target's
/// windows only).
/// </summary>
public sealed class RoomWatch : IDisposable
{
    const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    const long WS_THICKFRAME = 0x40000;

    readonly Func<IntPtr, bool> _mayMove;
    readonly Native.WinEventProc _proc;
    readonly List<IntPtr> _hooks = new();
    // Each window seen with its thread: a handle Windows hands to another window later is another window.
    readonly Dictionary<IntPtr, uint> _seen = new();
    readonly List<(IntPtr hwnd, ScreenRect from, ScreenRect to, long at)> _done = new();
    int _pruneAt;

    /// <summary>Windows put between the bars as they opened (diagnostics).</summary>
    public int Opened { get; private set; }

    /// <param name="mayMove">Whether a window may be moved (a spec's guard).</param>
    public RoomWatch(Func<IntPtr, bool> mayMove)
    {
        _mayMove = mayMove;
        // Only the app windows open now (on any desktop): one hidden then (an app in the tray, one started in the
        // background) opens when it is first shown (Gemini's review, 2026-10-24).
        foreach (var (h, _) in WindowList.AppWindowsOfAllDesktops()) _seen[h] = Native.GetWindowThreadProcessId(h, out _);
        _pruneAt = Math.Max(1024, _seen.Count * 2);
        _proc = (_, ev, hwnd, idObject, idChild, _, _) =>
        {
            if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero || Native.GetAncestor(hwnd, 2 /*GA_ROOT*/) != hwnd) return;
            try { Appeared(hwnd); }
            catch (Exception ex) { Log.Warn("window room", ex); }
        };
        foreach (var ev in new[] { Native.EVENT_OBJECT_SHOW, EVENT_OBJECT_UNCLOAKED })
            _hooks.Add(Native.SetWinEventHook(ev, ev, IntPtr.Zero, _proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT));
    }

    void Appeared(IntPtr h)
    {
        uint thread = Native.GetWindowThreadProcessId(h, out _);
        if (_seen.TryGetValue(h, out var known) && known == thread) return;
        if (!WindowList.IsAppWindow(h)) return; // not one yet: owned, a tool window, or still cloaked
        _seen[h] = thread;
        if (_seen.Count > _pruneAt)
        {
            foreach (var gone in _seen.Keys.Where(w => !Native.IsWindow(w)).ToList()) _seen.Remove(gone);
            _pruneAt = Math.Max(1024, _seen.Count * 2);
        }
        if (Native.IsZoomed(h) || Native.IsIconic(h) || !_mayMove(h) || Monitors.Of(h) is not { } mon) return;
        bool sizable = (Native.GetWindowLongPtr(h, Native.GWL_STYLE).ToInt64() & WS_THICKFRAME) != 0;
        var f = Native.FrameBounds(h);
        var frame = new ScreenRect(f.Left, f.Top, f.Right, f.Bottom);
        if (WindowRoom.Opening(frame, S(mon.Bounds), S(mon.Work), sizable) is not { } to) return;
        WindowActions.PlaceFrame(h, new RECT(to.Left, to.Top, to.Right, to.Bottom));
        Opened++;
        _done.Add((h, frame, to, Environment.TickCount64));
        if (_done.Count > 16) _done.RemoveAt(0);
        Log.Debug($"window 0x{h:X} opened at {frame}: put at {to}");
    }

    static ScreenRect S(RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);

    /// <summary>Diagnostics: the count and the last windows put in their place (newest last).</summary>
    public object State() => new
    {
        seen = _seen.Count, opened = Opened,
        last = _done.Select(d => new
        {
            kind = "opened", hwnd = d.hwnd.ToInt64(), ago = Environment.TickCount64 - d.at,
            from = new[] { d.from.Left, d.from.Top, d.from.Right, d.from.Bottom }, to = new[] { d.to.Left, d.to.Top, d.to.Right, d.to.Bottom },
        }),
    };

    public void Dispose()
    {
        foreach (var h in _hooks) if (h != IntPtr.Zero) Native.UnhookWinEvent(h);
        _hooks.Clear();
    }
}
