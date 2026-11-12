using System.Windows.Threading;
using Frostbar.App.Common;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Shell;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.MenuBar;

/// <summary>
/// macOS's menu bar over a full-screen app (and everywhere when it hides always: stage 5-1, the strip at the top let
/// go): hidden, it comes down when the pointer rests on the top edge, and goes a moment after the pointer has left it
/// with no menu open. Never while the pointer is hidden or held inside a window
/// (a game playing). The window stays hidden in between: a window over a full-screen game, even a transparent one,
/// can keep the game from presenting its frames directly. The pointer is looked at on the background ticker, after
/// input only (as the Dock's auto-hide).
/// </summary>
public sealed class MenuBarPeek
{
    const string Job = "menubar-peek";
    const long RevealMs = 350, HideMs = 600;

    readonly MenuBarController _bar;
    readonly BackgroundTicker _ticker;
    readonly Dispatcher _ui;
    readonly Func<bool> _animate;
    volatile bool _on, _shown;
    // The monitor and the bar's bottom, in screen pixels (written on the UI thread, read on the ticker's).
    volatile int _left, _top, _right, _bottom, _barBottom;
    long _edgeSince = -1, _awaySince = -1;
    uint _lastInput;
    bool _fast;

    public MenuBarPeek(MenuBarController bar, BackgroundTicker ticker, Dispatcher ui, Func<bool> animate)
    {
        _bar = bar;
        _ticker = ticker;
        _ui = ui;
        _animate = animate;
        // Held down by a menu or a panel: it looks again when they close (with input or not), and not before.
        _bar.Menus.Closed += () => { if (_held) { _held = false; _lastInput = 0; } };
    }

    // Down and held there by a menu or a panel open from it (UI thread writes, the ticker reads): the pointer is not
    // looked at meanwhile. Each look found it away, the bar asked to go, was kept, and looked again from the start, 62
    // times a second while the menu stayed open (2026-11-06, review).
    volatile bool _held;

    /// <summary>The menu bar is down over a full-screen app (diagnostics).</summary>
    public bool Shown => _shown;
    /// <summary>The bar waits at the top edge (a full-screen app, or the bar hiding always: stage 5-1).</summary>
    public bool Waiting => _on;
    /// <summary>The bar came down or went (UI thread): what stands under it moves.</summary>
    public event Action? ShownChanged;

    /// <summary>A full-screen app on <paramref name="mon"/>: the bar goes out of sight and waits at the top edge.</summary>
    public void Start(MonitorInfo mon)
    {
        (_left, _top, _right, _bottom) = (mon.Bounds.Left, mon.Bounds.Top, mon.Bounds.Right, mon.Bounds.Bottom);
        _bar.Menus.Close();
        _bar.Window.Hide();
        _shown = false;
        _held = false;
        _edgeSince = _awaySince = -1;
        if (_on) return;
        _on = true;
        _fast = false;
        _ticker.Add(Job, TimeSpan.FromMilliseconds(100), Look);
    }

    /// <summary>The full-screen app has gone: the bar is back for good.</summary>
    public void Stop()
    {
        if (!_on) return;
        _on = false;
        _ticker.Remove(Job);
        _shown = false;
        _held = false;
        _bar.Window.ResetFade();
        PutBack(_bar.Window); // ResetFade leaves a slide up past the edge running: it would end there, the bar "back" off the screen
        _bar.Window.Show();
    }

    /// <summary>
    /// A full-screen app in front, and the bar hides only on the desktop or never (macOS's "On Desktop Only", "Never",
    /// 2026-11-01): it stays, above the app.
    /// </summary>
    public void StandOver()
    {
        Stop();
        Raise(_bar.Window);
    }

    static void Raise(MenuBarWindow w)
    {
        Native.SetWindowPos(w.BackdropHandle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        Native.SetWindowPos(w.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    void Look()
    {
        if (!_on) return;
        if (_held)
        {
            if (_fast) { _fast = false; _ticker.Retime(Job, TimeSpan.FromMilliseconds(100)); }
            return;
        }
        uint input = Native.LastInputTick();
        if (input == _lastInput && _edgeSince < 0 && _awaySince < 0)
        {
            // Nothing moved, nothing pending: no need to look often until input comes (until 2026-10-15 a bar that had
            // just slid away went on looking 62 times a second until the next input).
            if (_fast) { _fast = false; _ticker.Retime(Job, TimeSpan.FromMilliseconds(100)); }
            return;
        }
        _lastInput = input;
        if (!Native.GetCursorPos(out var p)) return;
        long now = Environment.TickCount64;
        bool across = p.X >= _left && p.X < _right;
        if (!_shown)
        {
            bool atEdge = across && p.Y <= _top + 1;
            if (!atEdge) _edgeSince = -1;
            else if (_edgeSince < 0) _edgeSince = now;
            else if (now - _edgeSince >= RevealMs)
            {
                // Rested long enough. The pointer hidden or held in a window (a game): not now, and not looked at again
                // until input comes (it went on reading the pointer 62 times a second while it rested there).
                _edgeSince = -1;
                if (Native.PointerFree(new RECT(_left, _top, _right, _bottom)))
                {
                    _shown = true;
                    _ui.BeginInvoke(Reveal);
                }
            }
        }
        else
        {
            bool over = across && p.Y < _barBottom + 2;
            if (over) _awaySince = -1;
            else if (_awaySince < 0) _awaySince = now;
            else if (now - _awaySince >= HideMs) { _awaySince = -1; _ui.BeginInvoke(Conceal); }
        }
        bool fast = _shown || across && p.Y <= _top + 48;
        if (fast != _fast) { _fast = fast; _ticker.Retime(Job, TimeSpan.FromMilliseconds(fast ? 16 : 100)); }
    }

    void Reveal()
    {
        // Not when it went meanwhile (a full-screen change put it out of sight before this came: it stayed down while
        // the peek thought it hidden, and nothing took it up again).
        if (!_on || !_shown) return;
        var w = _bar.Window;
        bool animate = _animate();
        w.FadeIn(animate ? Fades.PanelInMs : 0); // before it shows (Fades.FadeIn)
        PutBack(w);
        w.Show();
        Raise(w);
        _barBottom = Native.FrameBounds(w.Handle).Bottom; // where it stands, before it starts from above it
        Slides++;
        // What stands under it is placed where it will stand, before it starts from above it: placed after, the banners
        // and the volume display went under the bar's place as if it were hidden, 16 DIP under the bar once it was down
        // (2026-11-06, review).
        ShownChanged?.Invoke();
        // It comes down from the screen's top edge as it fades in (macOS's); it only faded where it was (2026-10-19, "B9").
        if (animate) { _top0 = w.Top; Fades.ComeFrom(w, 0, -w.ActualHeight, SlideMs); }
    }

    void Conceal()
    {
        if (!_on || !_shown) return;
        // A menu or a panel open from the bar keeps it down; the next look decides again, input or not (a menu closed
        // with no input, by the front app changing, left the bar down until the mouse moved: the Dock's _lookAgain).
        if (_bar.Menus.IsOpen || _bar.Menus.Panel != null) { _held = true; return; }
        _shown = false;
        var w = _bar.Window;
        bool animate = _animate();
        // Back up past the top edge as it fades (macOS's), then hidden where it stands.
        // Where it stands now (still coming down: where it was going), put back once hidden.
        if (animate) { if (!Fades.IsGliding(w) || _top0 == null) _top0 = w.Top; Fades.GoBy(w, 0, -w.ActualHeight, Fades.PanelOutMs); }
        w.FadeOut(animate ? Fades.PanelOutMs : 0, () => { if (_on && !_shown) { w.Hide(); PutBack(w); ShownChanged?.Invoke(); } });
    }

    /// <summary>How long the bar takes to come down (ms; its fade is Fades.PanelInMs), and its slides (diagnostics).</summary>
    public const double SlideMs = Motion.BarSlide;
    public int Slides { get; private set; }

    // Where the bar stands, kept while it goes up past the edge: it is put back there when hidden or shown again.
    double? _top0;

    void PutBack(System.Windows.Window w)
    {
        Fades.StopGliding(w);
        if (_top0 is double top) { w.Top = top; _top0 = null; }
    }
}
