using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Frostbar.App.Common;
using Frostbar.Core.Localization;
using Frostbar.Core.Overview;
using Frostbar.Core.Settings;
using Frostbar.Core.Shell;
using Frostbar.Core.Wm;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Icons;
using Frostbar.Platform.Identity;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Shell;
using Frostbar.Platform.Sys;
using Frostbar.Platform.Wm;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Overview;

/// <summary>
/// The window overview (M7, macOS's Mission Control): every window on this display that is not minimized and not on
/// another virtual desktop (those are cloaked) flies from where it is to a place in rows — roughly where it was — over
/// the blurred desktop, as live DWM thumbnails. A click brings that window forward while they fly back; Esc, a click on
/// empty space, or the hot corner again goes back to the app that was in front. The arrows and Enter choose too.
/// Opened by a hot corner, or for one app's windows from the Dock's menu ("Show All Windows"). While it is open, the
/// window pointed at has a close button at its top right corner, and windows that come or go take or leave their
/// places, the others gliding to theirs (stage 3-3, macOS's).
/// </summary>
public sealed class OverviewController : IDisposable
{
    const double OpenMs = Motion.OverviewOpen, CloseMs = Motion.OverviewClose, RearrangeMs = Motion.OverviewRearrange;
    // Margins around the thumbnails (DIPs): room above, at the sides, and above the Dock for a window's label; the gap
    // between rows holds the label of the window pointed at (thumbnails are drawn over the window's own content, so a
    // label reaching into the next row was half hidden: 2026-10-12).
    const double MarginTop = 40, MarginSide = 64, MarginBottom = 48, Gap = 40;

    sealed class Item
    {
        public required IntPtr Hwnd;
        public required string AppId;
        public required string Title;
        public required string? Exe;
        public Thumbnail? Thumb;
        public RECT From, To; // pixels in the overview window
    }

    readonly AppSettings _settings;
    readonly ThemeService _theme;
    readonly AppIdentity _identity;
    readonly Func<Strings> _strings;
    readonly Func<RECT> _barRect;
    readonly Action _raiseDock;
    readonly Dispatcher _ui;
    readonly ThumbnailAnimator _animator;
    MonitorInfo _mon;
    OverviewWindow? _window;
    OverviewCloseButton? _close;
    DesktopStripWindow? _strip;
    WallpaperPictures? _walls;
    bool _stripOn;       // this opening shows the strip of virtual desktops (stage 3-4)
    bool _stripWindows;  // its pictures' windows are drawn once the opening has settled
    IconService? _icons;
    WindowTracker? _windows;
    List<Item> _items = new();
    int _originX, _originY; // the window's top-left on the screen, pixels
    Rect2 _area; // where the thumbnails go, pixels in the window

    // The same in DIPs (the window's own units): the line saying there is no window stands in its middle.
    Rect AreaDip => new(_area.X / _mon.Scale, _area.Y / _mon.Scale, _area.Width / _mon.Scale, _area.Height / _mon.Scale);
    IntPtr _previous;
    bool _open, _closing;
    int _selection = -1;
    bool _pointed;  // the selection is the pointer's (the close button shows then only, as on macOS)
    bool _changed;  // windows came or went during a run of the thumbnails: placed when it ends
    bool _repoint;  // after a rearrangement: the window under the pointer is pointed at again once it ends
    int _opening;   // which opening this is: what a gone-to desktop finishes is that opening's, not one opened since
    string? _app;

    public bool IsOpen => _open;
    /// <summary>Under an E2E spec: whether a window may be closed or moved to another desktop from here (the spec guard); null: any.</summary>
    public Func<IntPtr, bool>? MayActOn { get; set; }
    /// <summary>Diagnostics: windows closed from here, and rearrangements while open.</summary>
    public int Closes { get; private set; }
    public int Rearranges { get; private set; }
    /// <summary>Diagnostics: desktops gone to, added and removed from the strip, and windows moved to a desktop.</summary>
    public int Switches { get; private set; }
    public int Adds { get; private set; }
    public int Removes { get; private set; }
    public int Moves { get; private set; }

    // A window pressed (its index, or -1), where, and whether it is being dragged (to a desktop's picture, 3-5).
    int _pressed = -1;
    Point _pressAt;
    bool _dragging;
    RECT _dragged;
    OverviewCloseButton? _deskClose; // a desktop's × on its picture's corner (3-5)
    DispatcherTimer? _deskCloseLater;
    /// <summary>Diagnostics: from the trigger (input or hot corner) to the first frame of the open overview, in ms (-1: not measured).</summary>
    public int LastOpenMs { get; private set; } = -1;
    /// <summary>Diagnostics: how long the last opening took on the UI thread before the animation started, in ms.</summary>
    public int LastPrepareMs { get; private set; } = -1;

    public OverviewController(MonitorInfo mon, AppSettings settings, ThemeService theme, AppIdentity identity, Func<Strings> strings,
        Func<RECT> barRect, Action raiseDock, Dispatcher ui)
    {
        _mon = mon;
        _settings = settings;
        _theme = theme;
        _identity = identity;
        _strings = strings;
        _barRect = barRect;
        _raiseDock = raiseDock;
        _ui = ui;
        _animator = new ThumbnailAnimator(ui);
        // Asked at once, off the UI thread (up to a second): the strip's add button waits for the answer. Asked only in
        // the warm-up 5 s after start, an overview opened before had no button (2026-10-16, spaces).
        Task.Run(DesktopsInternal.Check);
    }

    bool Animations => _theme.Current.Animations;
    Material Material(ThemeSnapshot t) => Materials.Overview(t, _settings.Appearance);

    public void ApplyTheme(ThemeSnapshot t)
    {
        _window?.SetMaterial(Material(t));
        _window?.SetAccent(Color.FromRgb(t.Accent.R, t.Accent.G, t.Accent.B)); // it kept the accent of its first opening
    }

    /// <summary>The displays changed: an open overview closes (its windows' places are of the screen before); at another scale its window is made again.</summary>
    public void MoveTo(MonitorInfo mon)
    {
        bool rescaled = mon.Dpi != _mon.Dpi;
        _mon = mon;
        if (_open || rescaled) Finish();
        if (!rescaled) return;
        _window?.Close();
        _window = null;
        _close?.Close();
        _close = null;
        _deskClose?.Close(); // a tool window too: it gets no WM_DPICHANGED, and kept its size and place at the old scale
        _deskClose = null;
        _strip?.Close();
        _strip = null;
        _icons?.Dispose();
        _icons = null;
    }

    /// <summary>
    /// The windows to follow while open (the Dock's window model; a new one comes with each Dock made): windows that
    /// come or go while the overview is open take or leave their places (stage 3-3).
    /// </summary>
    public void Follow(WindowTracker windows)
    {
        if (_windows != null) _windows.Changed -= OnWindowsChanged;
        _windows = windows;
        windows.Changed += OnWindowsChanged;
    }

    void OnWindowsChanged()
    {
        if (!_open || _closing) return;
        if (_animator.Running) { _changed = true; return; } // placed from where the run leaves them
        // A window carried: placed once it is let go (its fly back ends in Settled; gone to a desktop, the model changes
        // again). Rearranged under it, the pressed place was another window's, and that one went to the desktop.
        if (_dragging) { _changed = true; return; }
        Rearrange();
    }

    OverviewWindow Ensure()
    {
        if (_window != null) return _window;
        var a = _theme.Current.Accent;
        var w = new OverviewWindow(Material(_theme.Current), Color.FromRgb(a.R, a.G, a.B)) { Animate = () => Animations };
        // A press on a window and a move of 6 DIP drags it (to a desktop's picture, stage 3-5); a press and release chooses.
        w.MouseLeftButtonDown += (_, e) => Press(e.GetPosition(w));
        w.MouseMove += (_, e) =>
        {
            var p = e.GetPosition(w);
            if (_pressed >= 0 && e.LeftButton == MouseButtonState.Pressed) Drag(p);
            else Point(p);
        };
        w.MouseLeftButtonUp += (_, e) => Release(e.GetPosition(w));
        w.LostMouseCapture += (_, _) => { if (_dragging) EndDrag(null); };
        w.PreviewKeyDown += OnKey;
        w.Deactivated += (_, _) => { if (_open && !_closing) CloseTo(-1, restore: false); }; // another window came forward: it keeps it
        new System.Windows.Interop.WindowInteropHelper(w).EnsureHandle();
        _window = w;
        return w;
    }

    /// <summary>
    /// Makes the windows once after start-up, so the first opening does not pay for them; asks Explorer whether its
    /// private desktop manager is there (off the UI thread) and decodes the current wallpaper for the strip ahead.
    /// </summary>
    public void Warm()
    {
        Ensure();
        EnsureStrip();
        Task.Run(DesktopsInternal.Check);
        _walls!.Get(Wallpapers.For(VirtualDesktops.Current(), current: true).Path, PictureWidthPx());
    }

    DesktopStripWindow EnsureStrip()
    {
        _walls ??= NewWallpapers();
        if (_strip != null) return _strip;
        var s = new DesktopStripWindow { Animate = () => Animations };
        s.Chosen += GoTo;
        s.AddClicked += AddDesktop;
        s.PictureEntered += PictureEntered;
        s.PictureLeft += _ => DeskCloseSoon();
        new System.Windows.Interop.WindowInteropHelper(s).EnsureHandle();
        _strip = s;
        return s;
    }

    WallpaperPictures NewWallpapers()
    {
        var w = new WallpaperPictures(_ui);
        // A wallpaper decoded after the strip was laid out goes into its picture.
        w.Loaded += () =>
        {
            if (!_open || !_stripOn || _strip == null) return;
            foreach (var d in _strip.Desktops.Where(d => d.Picture == null).ToList())
                if (w.Get(Wallpapers.For(d.Id, d.Current).Path, PictureWidthPx()) is { } picture) _strip.SetPicture(d.Id, picture);
        };
        return w;
    }

    double Aspect => _mon.Bounds.Height > 0 ? _mon.Bounds.Width / (double)_mon.Bounds.Height : 16.0 / 9;

    /// <summary>How many pixels across the strip's wallpapers are decoded at (twice the picture's: sharp when it is drawn smaller).</summary>
    int PictureWidthPx() => (int)Math.Round(DesktopStripWindow.PictureHeight * Aspect * _mon.Scale * 2);

    /// <param name="appId">Only this app's windows (the Dock's "Show All Windows"), or all.</param>
    public void Toggle(string? appId = null, int pressTime = 0)
    {
        if (_open && !_closing) CloseTo(-1, restore: true);
        else Open(appId, pressTime);
    }

    /// <param name="desktops">
    /// The strip of virtual desktops along the top (null: with all windows, as macOS's Mission Control; not with one
    /// app's, as its App Exposé). With one app's windows asked to show it (specs and screenshots), its pictures show
    /// that app's windows only.
    /// </param>
    public void Open(string? appId = null, int pressTime = 0, bool? desktops = null)
    {
        if (_open)
        {
            if (!_closing) return;
            Finish(); // opened again while closing: start over from where the windows are
        }
        _opening++;
        var sw = Stopwatch.StartNew();
        int queued = pressTime != 0 ? Math.Max(0, Environment.TickCount - pressTime) : 0;
        _mon = Monitors.Get(_mon.Handle) ?? _mon;
        double s = _mon.Scale;
        var bar = _barRect();
        int top = bar.Bottom > _mon.Bounds.Top && bar.Bottom < _mon.Bounds.Bottom ? bar.Bottom : _mon.Bounds.Top;

        var items = Collect(appId);

        var w = Ensure();
        var fg = Native.GetForegroundWindow();
        if (fg != w.Handle) _previous = fg;
        _app = appId;
        _selection = -1;
        _pointed = _changed = _repoint = false;
        _stripOn = desktops ?? appId == null;
        _stripWindows = _stripOn;
        w.Point(null, "", null);
        // Below the menu bar to the bottom of the screen; the Dock stays above it.
        w.Place(new Rect(_mon.Bounds.Left / s, top / s, _mon.Bounds.Width / s, (_mon.Bounds.Bottom - top) / s));
        Fades.TakePresses(w); // a closing let them through
        w.Show();
        w.Activate();
        if (Native.GetForegroundWindow() != w.Handle) WindowActions.ForceForeground(w.Handle);
        Native.GetWindowRect(w.Handle, out var wr);
        _originX = wr.Left;
        _originY = wr.Top;

        // Where each goes: the area above the Dock (or beside it, on a side) less the margins, and below the strip, in
        // the order of where the windows are.
        var work = (Monitors.Get(_mon.Handle) ?? _mon).Work;
        double marginTop = _stripOn ? DesktopStripWindow.StripHeight + MarginTop / 2 : MarginTop;
        double left = Math.Max(0, work.Left - wr.Left) + MarginSide * s, right = Math.Max(0, wr.Right - work.Right) + MarginSide * s;
        _area = new Rect2(left, marginTop * s, Math.Max(100, wr.Width - left - right), Math.Max(100, work.Bottom - wr.Top - (marginTop + MarginBottom) * s));
        foreach (var it in items) it.From = InWindow(it.From);
        _items = Place(items);
        // Thumbnails from back to front: the last registered is drawn on top, as the windows were.
        foreach (var it in Enumerable.Reverse(items)) it.Thumb = Thumbnail.Create(w.Handle, it.Hwnd);
        w.Empty(_items.Count == 0 ? _strings()["overview.none"] : null, AreaDip);

        _open = true;
        _closing = false;
        double ms = Animations ? OpenMs : 0;
        w.Dim(true, ms);
        // Reduced motion (accessibility): the thumbnails fade in at their places instead of flying from the windows'.
        _animator.Run(_items.Where(i => i.Thumb != null).Select(i => (i.Thumb!, i.From, i.To)).ToList(), ms, Motion.Out, Settled,
            fade: ms > 0 && ReduceMotion.Calms("overview") ? 1 : 0);
        if (_stripOn) ShowStrip(new Rect(_mon.Bounds.Left / s, top / s, _mon.Bounds.Width / s, DesktopStripWindow.StripHeight), ms);
        LastPrepareMs = (int)sw.ElapsedMilliseconds;
        // Showing and activating put it at the top of the topmost windows: the Dock (and the strip) go back above it.
        RaiseAbove();
        _ui.BeginInvoke(DispatcherPriority.Background, () => { if (_open) RaiseAbove(); });
        LastOpenMs = -1;
        EventHandler? frame = null;
        frame = (_, _) =>
        {
            CompositionTarget.Rendering -= frame;
            LastOpenMs = queued + (int)sw.ElapsedMilliseconds;
        };
        CompositionTarget.Rendering += frame;
        Log.Debug($"overview: {_items.Count} windows{(appId != null ? " of one app" : "")}, prepared in {LastPrepareMs} ms");
    }

    /// <summary>The Dock and the strip above the overview (showing and activating it put it at the top of the topmost windows).</summary>
    void RaiseAbove()
    {
        _raiseDock();
        if (_stripOn && _strip is { IsVisible: true } strip)
            Native.SetWindowPos(strip.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // ---- the strip of virtual desktops (stage 3-4) ---------------------------------------------------------------

    /// <summary>
    /// The desktops as the strip shows them, in their order: from the registry (<paramref name="known"/>: the private
    /// manager's list, which has a desktop added a moment ago), named, with their wallpapers (null while decoded).
    /// </summary>
    List<DesktopStripWindow.Desktop> ReadDesktops(List<(Guid Id, string Name)>? known)
    {
        Guid current = VirtualDesktops.Current();
        var order = known?.Select(k => k.Id).ToList() ?? DesktopStrip.Order(VirtualDesktops.Order(), current).ToList();
        if (order.Count == 0) order.Add(current); // none written yet: the one desktop there is
        var own = order.Select(id => known?.FirstOrDefault(k => k.Id == id).Name is { Length: > 0 } n ? n : VirtualDesktops.Name(id)).ToList();
        var names = DesktopStrip.Names(own, n => _strings().F("desk.numbered", n));
        int px = PictureWidthPx();
        return order.Select((id, i) =>
        {
            bool here = id == current || current == Guid.Empty && i == 0;
            var w = Wallpapers.For(id, here);
            return new DesktopStripWindow.Desktop(id, names[i], here, _walls!.Get(w.Path, px), w.Fit, Color.FromRgb(w.R, w.G, w.B));
        }).ToList();
    }

    /// <summary>Each desktop's windows on this display, top to bottom, with their frames (with one app's overview, that app's only).</summary>
    Dictionary<Guid, List<(IntPtr Hwnd, RECT Frame)>> DesktopWindows()
    {
        Guid current = _strip?.Desktops.FirstOrDefault(d => d.Current)?.Id ?? VirtualDesktops.Current();
        var map = new Dictionary<Guid, List<(IntPtr, RECT)>>();
        foreach (var (h, elsewhere) in WindowList.AppWindowsOfAllDesktops())
        {
            if (Native.IsIconic(h) || Native.MonitorFromWindow(h, 2 /*MONITOR_DEFAULTTONEAREST*/) != _mon.Handle) continue;
            var fb = Native.FrameBounds(h);
            if (fb.Width < 32 || fb.Height < 24) continue;
            if (_app != null && _identity.AppIdForWindow(h) != _app) continue;
            if ((elsewhere ? VirtualDesktops.DesktopOf(h) : current) is not { } desktop) continue;
            if (!map.TryGetValue(desktop, out var list)) map[desktop] = list = new();
            list.Add((h, fb));
        }
        return map;
    }

    void ShowStrip(Rect place, double fadeMs)
    {
        var strip = EnsureStrip();
        Fades.Reset(strip, null);
        bool withAdd = DesktopsInternal.Available;
        // Before it shows (Lay shows it): after, the layered strip could show a frame at full strength (Fades.FadeIn).
        Fades.FadeIn(strip, null, fadeMs);
        strip.Lay(ReadDesktops(null), place, Aspect, withAdd, _strings()["desk.add"]);
        // The registry's list lacks a desktop added a moment ago (ADR-014): the private manager's, when it answers (and
        // the add button, when the manager had not answered by the time the strip was laid out). Asked off the UI thread
        // each time: an answer known costs nothing, a "not there" is asked again after a while.
        var shown = strip.Desktops.Select(d => d.Id).ToList();
        Task.Run(() => DesktopsInternal.Check() ? DesktopsInternal.Desktops() : null).ContinueWith(t =>
        {
            if (!_open || _closing || !_stripOn || t.Result is not { } known) return;
            if (withAdd && known.Select(k => k.Id).SequenceEqual(shown)) return;
            Relay(known);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The strip laid out again for <paramref name="known"/> (a desktop added), its windows drawn again.</summary>
    void Relay(List<(Guid Id, string Name)>? known)
    {
        if (_strip == null || _window == null) return;
        double s = _mon.Scale;
        _strip.Lay(ReadDesktops(known), new Rect(_strip.Left, _strip.Top, _strip.Width, DesktopStripWindow.StripHeight), Aspect, DesktopsInternal.Available, _strings()["desk.add"]);
        if (!_stripWindows) _strip.ShowWindows(DesktopWindows(), _mon.Bounds, s);
    }

    /// <summary>A desktop's picture was clicked: the current one goes back to the app in front; another is gone to.</summary>
    void GoTo(Guid desktop)
    {
        if (!_open || _closing) return;
        if (_strip?.Desktops.FirstOrDefault(d => d.Id == desktop) is not { } d || d.Current) { CloseTo(-1, restore: true); return; }
        Log.Info($"overview: to desktop {desktop}");
        Switches++;
        int opening = _opening;
        // Going there can take 600 ms and more (then the keys): opened again meanwhile, the new one is not put away.
        void Go(Action? then) => Task.Run(() =>
        {
            bool there = DesktopsInternal.Available && DesktopsInternal.Switch(desktop);
            if (!there) there = VirtualDesktops.SwitchTo(desktop); // Win+Ctrl+arrows, as the Dock goes (ADR-014)
            Log.Info($"overview: desktop {desktop} {(there ? "reached" : "not reached")}");
        }).ContinueWith(_ => { if (_opening == opening) then?.Invoke(); }, TaskScheduler.FromCurrentSynchronizationContext());
        if (!Animations || ZoomInto(desktop, () => Go(Finish)) == false)
        {
            Finish(); // at once: what is behind it is another desktop's in a moment
            Go(null);
        }
    }

    /// <summary>How long the overview takes to zoom into a desktop's picture before going there (ms; E2).</summary>
    public const double ZoomMs = 300;

    /// <summary>Zooms into a desktop before going to it (diagnostics).</summary>
    public int Zooms { get; private set; }

    readonly List<Thumbnail> _zoomThumbs = new();

    /// <summary>
    /// Mission Control's going to a desktop (E2, 2026-10-19: the overview went at once and the desktop came after): its
    /// picture grows from the strip to the whole screen, its windows' live thumbnails from where they stand in the
    /// picture to where they stand on the screen; then <paramref name="then"/> (the desktop is gone to, the overview put
    /// away once it is there: what shows under it is that desktop). False when there is no picture to grow.
    /// </summary>
    bool ZoomInto(Guid desktop, Action then)
    {
        if (_window == null || _strip?.PictureOf(desktop, _mon.Scale) is not { } pic) return false;
        if (ReduceMotion.Calms("desktopZoom")) return false; // reduced motion: to the desktop without the zoom
        _closing = true; // nothing else meanwhile: Esc, a click, another desktop
        Zooms++;
        double s = _mon.Scale;
        _close?.Off();
        _deskClose?.Off();
        _window.Point(null, "", null);
        _window.Empty(null, Rect.Empty);
        foreach (var it in _items) it.Thumb?.Hide();
        _strip.ClearWindows();
        Fades.FadeOut(_strip, null, ZoomMs * 0.6, () => { });
        // The picture, the windows' rectangles, in the overview window's pixels.
        RECT In(RECT r) => new(r.Left - _originX, r.Top - _originY, r.Right - _originX, r.Bottom - _originY);
        var from = In(pic.Rect);
        var screen = In(_mon.Bounds);
        static Rect Dip(RECT r, double k) => new(r.Left / k, r.Top / k, r.Width / k, r.Height / k);
        var moves = new List<(Thumbnail, RECT, RECT)>();
        if (DesktopWindows().TryGetValue(desktop, out var windows))
        {
            var bounds = new Rect2(_mon.Bounds.Left, _mon.Bounds.Top, _mon.Bounds.Width, _mon.Bounds.Height);
            var inside = new Rect2(from.Left, from.Top, from.Width, from.Height);
            for (int k = windows.Count - 1; k >= 0; k--) // the bottom one first: the last registered is drawn on top
            {
                var (h, f) = windows[k];
                var r = DesktopStrip.InPicture(new Rect2(f.Left, f.Top, f.Width, f.Height), bounds, inside);
                if (Thumbnail.Create(_window.Handle, h) is not { } t) continue;
                _zoomThumbs.Add(t);
                moves.Add((t, new RECT((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Right), (int)Math.Round(r.Bottom)), In(f)));
            }
        }
        _window.Zoom(pic.Brush, Dip(from, s), Dip(screen, s), ZoomMs, () => { });
        _animator.Run(moves, ZoomMs, Motion.Out, then);
        return true;
    }

    /// <summary>The add button: a desktop at the end, without going there (macOS's +); the strip shows it.</summary>
    void AddDesktop()
    {
        if (!_open || _closing || !DesktopsInternal.Available) return;
        Task.Run(() => DesktopsInternal.Create() is { } id ? (id, DesktopsInternal.Desktops()) : ((Guid, List<(Guid, string)>?)?)null).ContinueWith(t =>
        {
            if (t.Result is not { } added) { Log.Warn("overview: no desktop added"); return; }
            Log.Info($"overview: desktop {added.Item1} added");
            Adds++;
            if (_open && !_closing && _stripOn) Relay(added.Item2);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    Guid CurrentDesktop() => _strip?.Desktops.FirstOrDefault(d => d.Current)?.Id ?? VirtualDesktops.Current();

    // ---- removing a desktop, and moving a window to one (stage 3-5, the private manager) --------------------------

    /// <summary>The pointer on a desktop's picture: a × on its top left corner removes it (macOS's), while there are two or more.</summary>
    void PictureEntered(Guid desktop, Rect dip)
    {
        if (!_open || _closing || _dragging || !DesktopsInternal.Available || _strip is not { Desktops.Count: > 1 })
        {
            Log.Debug($"strip: pointer on desktop {desktop}'s picture, no ×: open {_open}, closing {_closing}, dragging {_dragging}, private {DesktopsInternal.Available}, desktops {_strip?.Desktops.Count}");
            return;
        }
        Log.Debug($"strip: pointer on desktop {desktop}'s picture: its × at {dip.Left:0},{dip.Top:0}");
        _deskCloseLater?.Stop();
        if (_deskClose == null)
        {
            _deskClose = new OverviewCloseButton(() => Animations);
            _deskClose.Clicked += target => { if (target is Guid id) RemoveDesktop(id); };
            _deskClose.MouseLeave += (_, _) => DeskCloseSoon();
        }
        double r = OverviewCloseButton.Diameter / 2;
        _deskClose.ShowAt(new Point(dip.Left - r + 4, dip.Top - r + 4), desktop);
    }

    /// <summary>Off the picture: the × goes a moment later, unless the pointer is on it (it crosses the gap on its way there).</summary>
    void DeskCloseSoon()
    {
        _deskCloseLater ??= new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Input, (_, _) =>
        {
            _deskCloseLater!.Stop();
            if (_deskClose is not { Pointed: false }) return;
            Log.Debug("strip: the pointer left the picture: its × goes");
            _deskClose.Off();
        }, _ui);
        _deskCloseLater.Stop();
        _deskCloseLater.Start();
    }

    /// <summary>A desktop's ×: removed, its windows going to the one on its left (or right), as Task View's close does.</summary>
    void RemoveDesktop(Guid desktop)
    {
        _deskClose?.Off();
        if (!_open || _closing || _strip == null) return;
        if (DesktopStrip.Fallback(_strip.Desktops.Select(d => d.Id).ToList(), desktop) is not { } fallback) return;
        Log.Info($"overview: removing desktop {desktop} (its windows go to {fallback})");
        Task.Run(() => DesktopsInternal.Remove(desktop, fallback) ? DesktopsInternal.Desktops() : null).ContinueWith(t =>
        {
            if (t.Result is not { } known) { Log.Warn($"overview: desktop {desktop} not removed"); return; }
            Removes++;
            // Its windows came here or went to another desktop: the model's change rearranges the windows below.
            if (_open && !_closing && _stripOn) Relay(known);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void Press(Point dip)
    {
        _pressed = !_open || _closing || _animator.Running && !_giveRun ? -1 : At(dip);
        _pressAt = dip;
        // The press brought the overview's window to the top of the topmost windows, over the strip and the Dock: after
        // a drag the strip took no pointer any more (2026-10-16, spaces-move: the overview above, under the pointer).
        if (_open && !_closing) RaiseAbove();
        if (_pressed >= 0 && _pressed < _items.Count) Give(_items[_pressed]);
    }

    // ---- a window's picture giving under a press (2026-10-23, the user's "A 沈む＋隣へ光る") --------------------------------
    // It sinks to Core's PressGive.Window about its middle; let go, it springs back past its size (the animator's frames:
    // its Lerp goes past the end as the spring does); moved away it is carried as before.

    Item? _given;
    bool _giveRun;
    readonly System.Diagnostics.Stopwatch _givenFor = new();

    /// <summary>Pictures given under a press (diagnostics).</summary>
    public int Given { get; private set; }

    static RECT Scaled(RECT r, double k) => new(
        (int)Math.Round(r.Left + r.Width * (1 - k) / 2), (int)Math.Round(r.Top + r.Height * (1 - k) / 2),
        (int)Math.Round(r.Right - r.Width * (1 - k) / 2), (int)Math.Round(r.Bottom - r.Height * (1 - k) / 2));

    void Give(Item it)
    {
        if (it.Thumb == null || !Animations || ReduceMotion.Calms("press")) return;
        _given = it;
        _giveRun = true;
        _givenFor.Restart();
        Given++;
        _animator.Run([(it.Thumb, it.To, Scaled(it.To, PressGive.Window))], Motion.BouncePress, Motion.Out, () => _giveRun = false);
    }

    /// <summary>Let go (a click, or nothing): it springs back from about where it is.</summary>
    void LetGo()
    {
        if (_given is not { Thumb: { } thumb } it) { _given = null; return; }
        _given = null;
        if (!_open || _closing || !_items.Contains(it)) return;
        double k = PressGive.Pressing(1, PressGive.Window, _givenFor.Elapsed.TotalMilliseconds);
        _giveRun = true;
        _animator.Run([(thumb, Scaled(it.To, k), it.To)], Motion.BounceRelease, t => Motion.Spring(t, PressGive.Damping), () => _giveRun = false);
    }

    /// <summary>
    /// A window pressed and moved: it follows the pointer; over the strip a small picture of it stands above the
    /// strip (the overview's own thumbnails are under the strip's window), and the picture it would go to is framed.
    /// Only with the strip and the private manager (nothing else moves another app's window to a desktop).
    /// </summary>
    void Drag(Point dip)
    {
        if (_window == null || _strip == null || _pressed < 0 || _pressed >= _items.Count) return;
        var it = _items[_pressed];
        if (!_dragging)
        {
            if ((dip - _pressAt).Length < 6 || !_stripOn || !DesktopsInternal.Available || it.Thumb == null || MayActOn?.Invoke(it.Hwnd) == false) return;
            _dragging = true;
            if (_given != null) { _animator.Stop(); _giveRun = false; _given = null; } // carried now: no spring under the pointer
            _window.CaptureMouse();
            _close?.Off();
            _deskClose?.Off();
            _window.Point(null, "", null);
        }
        double s = _mon.Scale;
        int dx = (int)Math.Round((dip.X - _pressAt.X) * s), dy = (int)Math.Round((dip.Y - _pressAt.Y) * s);
        _dragged = new RECT(it.To.Left + dx, it.To.Top + dy, it.To.Right + dx, it.To.Bottom + dy);
        int px = (int)Math.Round(dip.X * s) + _originX, py = (int)Math.Round(dip.Y * s) + _originY;
        var target = _strip.DesktopAt(px, py, s);
        _strip.Target(target is { } t && t != CurrentDesktop() ? target : null);
        if (py < (int)Math.Round((_strip.Top + _strip.ActualHeight) * s))
        {
            // Over the strip: a quarter of its size, centred on the pointer.
            int w = it.To.Width / 4, h = it.To.Height / 4;
            it.Thumb!.Hide();
            _strip.Ghost(it.Hwnd, new RECT(px - w / 2, py - h / 2, px + w - w / 2, py + h - h / 2));
        }
        else
        {
            _strip.Ghost(IntPtr.Zero, null);
            it.Thumb!.Show(_dragged);
        }
    }

    void Release(Point dip)
    {
        if (_dragging) { EndDrag(dip); return; }
        _pressed = -1;
        LetGo(); // a choice made by the click takes over from the spring (its fly)
        Click(dip);
    }

    /// <summary>A dragged window let go: on another desktop's picture it goes to that desktop; elsewhere it flies back to its place.</summary>
    void EndDrag(Point? dip)
    {
        var it = _pressed >= 0 && _pressed < _items.Count ? _items[_pressed] : null;
        _dragging = false;
        _pressed = -1;
        _window?.ReleaseMouseCapture();
        RaiseAbove();
        Guid? target = null;
        if (dip is { } p && _strip != null)
        {
            double s = _mon.Scale;
            target = _strip.DesktopAt((int)Math.Round(p.X * s) + _originX, (int)Math.Round(p.Y * s) + _originY, s);
        }
        _strip?.Target(null);
        _strip?.Ghost(IntPtr.Zero, null);
        if (it?.Thumb == null || !_open || _closing) return;
        if (target is { } desktop && desktop != CurrentDesktop())
        {
            var h = it.Hwnd;
            it.Thumb.Hide(); // it leaves this desktop: the model's change takes its place away
            Log.Info($"overview: window 0x{h:X} to desktop {desktop}");
            Task.Run(() => DesktopsInternal.MoveWindow(h, desktop)).ContinueWith(t =>
            {
                if (t.Result) { Moves++; return; }
                Log.Warn($"overview: window 0x{h:X} not moved to desktop {desktop}");
                if (_open && !_closing && _items.Contains(it)) FlyBack(it, _dragged);
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }
        FlyBack(it, _dragged);
    }

    /// <summary>A drag given up (the overview closing): no capture, no ghost, no framed picture.</summary>
    void StopDragging()
    {
        _pressed = -1;
        if (!_dragging) return;
        _dragging = false;
        _window?.ReleaseMouseCapture();
        _strip?.Target(null);
        _strip?.Ghost(IntPtr.Zero, null);
    }

    void FlyBack(Item it, RECT from)
    {
        if (it.Thumb == null) return;
        it.Thumb.Show(from);
        _animator.Run([(it.Thumb, from, it.To)], Animations ? CloseMs : 0, Motion.Out, Settled);
    }

    /// <summary>The windows to show (<paramref name="appId"/>'s only, or all), front to back, with their frames on the screen (pixels).</summary>
    List<Item> Collect(string? appId)
    {
        var items = new List<Item>();
        foreach (var h in WindowList.AppWindows())
        {
            if (Native.IsIconic(h) || Native.MonitorFromWindow(h, 2 /*MONITOR_DEFAULTTONEAREST*/) != _mon.Handle) continue;
            var fb = Native.FrameBounds(h);
            if (fb.Width < 32 || fb.Height < 24) continue;
            string id = _identity.AppIdForWindow(h);
            if (appId != null && id != appId) continue;
            items.Add(new Item { Hwnd = h, AppId = id, Title = Native.Title(h), Exe = Native.ProcessPath(Native.AppOrWindowProcessId(h)), From = fb });
        }
        return items;
    }

    RECT InWindow(RECT screen) => new(screen.Left - _originX, screen.Top - _originY, screen.Right - _originX, screen.Bottom - _originY);

    /// <summary>The items in the order of where their windows are, each with its place in the area (To).</summary>
    List<Item> Place(List<Item> items)
    {
        var order = OverviewLayout.Order(items.Select(i => new Rect2(i.From.Left, i.From.Top, i.From.Width, i.From.Height)).ToList(), _area.Height / 3);
        var placed = order.Select(i => items[i]).ToList();
        var slots = OverviewLayout.Arrange(placed.Select(i => ((double)i.From.Width, (double)i.From.Height)).ToList(), _area, Gap * _mon.Scale);
        for (int i = 0; i < placed.Count; i++)
        {
            var r = slots[i];
            placed[i].To = new RECT((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Right), (int)Math.Round(r.Bottom));
        }
        return placed;
    }

    /// <summary>
    /// Windows came or went while it is open (stage 3-3, as macOS's Mission Control follows its windows): a new one flies
    /// in from where it is, a gone one's thumbnail goes, and the others glide to their new places. Their order stays: it
    /// is by where the windows are, and those do not move while the overview is in front.
    /// </summary>
    void Rearrange()
    {
        if (_window == null) return;
        _changed = false;
        // The strip's pictures follow too (a window that came or went on another desktop changes nothing below).
        if (_stripOn && _strip != null && !_stripWindows) _strip.ShowWindows(DesktopWindows(), _mon.Bounds, _mon.Scale);
        var old = _items.ToDictionary(i => i.Hwnd);
        var now = new List<Item>();
        var come = new List<Item>();
        foreach (var f in Collect(_app))
        {
            f.From = InWindow(f.From);
            if (old.Remove(f.Hwnd, out var it)) { it.From = f.From; it.Title = f.Title; now.Add(it); }
            else { now.Add(f); come.Add(f); }
        }
        if (old.Count == 0 && come.Count == 0) return; // the same windows here (the change was elsewhere: another desktop, a minimized window)
        Rearranges++;
        IntPtr selected = _selection >= 0 && _selection < _items.Count ? _items[_selection].Hwnd : IntPtr.Zero;
        IntPtr pressed = _pressed >= 0 && _pressed < _items.Count ? _items[_pressed].Hwnd : IntPtr.Zero;
        var before = _items.Where(i => !old.ContainsKey(i.Hwnd)).ToDictionary(i => i.Hwnd, i => i.To);
        foreach (var gone in old.Values) gone.Thumb?.Dispose();
        _items = Place(now);
        // A press held on a window (not carried yet) stays on that window, as the selection does: by place, a move would carry another.
        if (pressed != IntPtr.Zero) _pressed = _items.FindIndex(i => i.Hwnd == pressed);
        foreach (var it in Enumerable.Reverse(come)) it.Thumb = Thumbnail.Create(_window.Handle, it.Hwnd); // above the others while they fly in
        Log.Debug($"overview: {come.Count} window(s) came, {old.Count} went; {_items.Count} shown");
        // The frame, the label and the close button go meanwhile; the window under the pointer is pointed at once they are placed.
        _repoint = _pointed || selected != IntPtr.Zero;
        _selection = _items.FindIndex(i => i.Hwnd == selected);
        _window.Point(null, "", null);
        _close?.Off();
        _window.Empty(_items.Count == 0 ? _strings()["overview.none"] : null, AreaDip);
        double ms = Animations ? RearrangeMs : 0;
        _animator.Run(_items.Where(i => i.Thumb != null).Select(i => (i.Thumb!, before.TryGetValue(i.Hwnd, out var b) ? b : i.From, i.To)).ToList(),
            ms, Motion.Out, Settled);
    }

    /// <summary>A run of the thumbnails ended (opening or rearranging): windows that came or went meanwhile are placed now.</summary>
    void Settled()
    {
        if (!_open || _closing) return;
        // The strip's pictures get their windows once it has faded in (DWM draws thumbnails at their own opacity).
        if (_stripOn && _strip != null && _stripWindows)
        {
            _stripWindows = false;
            _strip.ShowWindows(DesktopWindows(), _mon.Bounds, _mon.Scale);
        }
        if (_changed) { Rearrange(); return; }
        if (!_repoint) return;
        _repoint = false;
        if (_pointed && Native.GetCursorPos(out var p)) Select(At(new Point((p.X - _originX) / _mon.Scale, (p.Y - _originY) / _mon.Scale)), pointer: true);
        else Select(_selection, pointer: false);
    }

    /// <summary>Closes it: the windows fly back; <paramref name="chosen"/> (or the app that was in front) comes forward.</summary>
    public void Close() => CloseTo(-1, restore: true);

    void CloseTo(int chosen, bool restore)
    {
        if (!_open || _closing || _window == null) return;
        _closing = true;
        StopDragging();
        _close?.Off();
        _deskClose?.Off();
        _window.Point(null, "", null);
        _window.Empty(null, Rect.Empty);
        if (chosen >= 0 && chosen < _items.Count) WindowActions.ForceForeground(_items[chosen].Hwnd);
        else if (restore && _previous != IntPtr.Zero && Native.IsWindow(_previous)) WindowActions.ForceForeground(_previous);
        double ms = Animations ? CloseMs : 0;
        _window.Dim(false, ms);
        Fades.PassPresses(_window); // as its windows fly back a click goes to what is behind (into the window chosen, it was lost)
        if (_stripOn && _strip is { } strip)
        {
            strip.ClearWindows(); // DWM's thumbnails do not fade with it
            Fades.FadeOut(strip, null, ms, () => { if (!_open || _closing) strip.Off(); });
        }
        // Reduced motion: they fade out where they stand (to = from: the run's fade keeps them at its "to").
        if (ms > 0 && ReduceMotion.Calms("overview")) _animator.Run(_items.Where(i => i.Thumb != null).Select(i => (i.Thumb!, i.To, i.To)).ToList(), ms, Motion.Out, Finish, fade: -1);
        else _animator.Run(_items.Where(i => i.Thumb != null).Select(i => (i.Thumb!, i.To, i.From)).ToList(), ms, Motion.Out, Finish);
    }

    /// <summary>Hides the window and lets the thumbnails go (also when opened again before the closing ended).</summary>
    void Finish()
    {
        _animator.Stop();
        foreach (var i in _items) i.Thumb?.Dispose();
        foreach (var z in _zoomThumbs) z.Dispose();
        _zoomThumbs.Clear();
        _window?.ClearZoom();
        StopDragging();
        _items = new();
        _close?.Off();
        _deskClose?.Off();
        _strip?.Off();
        if (_window != null)
        {
            _window.Point(null, "", null);
            _window.Empty(null, Rect.Empty);
            _window.Hide();
            _window.Shrink();
        }
        _open = _closing = false;
        _selection = -1;
        _pointed = _changed = _repoint = false;
        _stripOn = _stripWindows = false;
        _app = null;
    }

    // ---- choosing --------------------------------------------------------------------------------------------

    int At(Point dip)
    {
        double s = _mon.Scale;
        return OverviewLayout.HitTest(_items.Select(i => new Rect2(i.To.Left, i.To.Top, i.To.Width, i.To.Height)).ToList(), dip.X * s, dip.Y * s);
    }

    void Point(Point dip)
    {
        if (!_open || _closing || _animator.Running) return;
        int i = At(dip);
        if (i != _selection || i >= 0 && !_pointed) Select(i, pointer: true);
    }

    /// <param name="pointer">Pointed at (the close button shows), or chosen with the keys (as on macOS, it does not).</param>
    void Select(int i, bool pointer)
    {
        _selection = i;
        _pointed = pointer && i >= 0;
        if (_window == null) return;
        if (i < 0 || i >= _items.Count) { _window.Point(null, "", null); _close?.Off(); return; }
        var it = _items[i];
        double s = _mon.Scale;
        var r = new Rect(it.To.Left / s, it.To.Top / s, it.To.Width / s, it.To.Height / s);
        _window.Point(r, string.IsNullOrWhiteSpace(it.Title) ? _identity.DisplayName(it.AppId, it.Exe) : it.Title, Icon(it));
        if (!_pointed) { _close?.Off(); return; }
        // At the thumbnail's top right corner, over the window's own close button (the user's choice B, 2026-10-16).
        var b = OverviewLayout.CloseButton(new Rect2(it.To.Left, it.To.Top, it.To.Width, it.To.Height), OverviewCloseButton.Diameter * s, OverviewCloseButton.Inset * s);
        if (_close == null)
        {
            _close = new OverviewCloseButton(() => Animations);
            _close.Clicked += target => { if (target is IntPtr h) CloseWindow(h); };
        }
        _close.ShowAt(new Point((b.X + _originX) / s, (b.Y + _originY) / s), it.Hwnd);
    }

    /// <summary>
    /// The close button: the window is asked to close (WM_CLOSE, as its own close button does; an administrator's window
    /// takes its window menu's Close: WindowActions.Close), and leaves its place once it has gone.
    /// </summary>
    void CloseWindow(IntPtr h)
    {
        _close?.Off();
        if (!_open || _closing || _items.All(i => i.Hwnd != h)) return;
        if (MayActOn?.Invoke(h) == false) { Log.Warn($"overview: window 0x{h:X} left open: not a test target"); return; }
        Log.Info($"overview: closing window 0x{h:X}");
        Closes++;
        WindowActions.Close(h);
    }

    ImageSource? Icon(Item it)
    {
        if (_icons == null)
        {
            _icons = new IconService(_ui, (int)Math.Ceiling(20 * _mon.Scale));
            _icons.Loaded += _ => { if (_selection >= 0 && _selection < _items.Count && !_animator.Running) Select(_selection, _pointed); };
        }
        return _icons.Get("overview:" + it.AppId, _identity.IconSource(it.AppId, it.Exe));
    }

    void Click(Point dip)
    {
        if (!_open || _closing) return;
        CloseTo(At(dip), restore: true); // a window, or empty space: back to the app that was in front
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (!_open || _closing) return;
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseTo(-1, restore: true);
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                e.Handled = true;
                int dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0, dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
                Select(OverviewLayout.Move(_items.Select(i => new Rect2(i.To.Left, i.To.Top, i.To.Width, i.To.Height)).ToList(), _selection, dx, dy), pointer: false);
                break;
            case Key.Enter or Key.Space:
                e.Handled = true;
                if (_selection >= 0) CloseTo(_selection, restore: false);
                break;
        }
    }

    // ---- diagnostics -------------------------------------------------------------------------------------------

    /// <summary>Diagnostics: the overview as the E2E specs read it (rectangles in screen pixels).</summary>
    public object State()
    {
        object Screen(RECT r) => new { Left = r.Left + _originX, Top = r.Top + _originY, Right = r.Right + _originX, Bottom = r.Bottom + _originY };
        var iv = _animator.LastIntervals.OrderBy(x => x).ToArray();
        double P(double q) => iv.Length == 0 ? 0 : iv[Math.Min(iv.Length - 1, (int)Math.Ceiling(q * iv.Length) - 1)];
        return new
        {
            open = _open,
            closing = _closing,
            animating = _animator.Running,
            hwnd = _window?.Handle.ToInt64() ?? 0,
            foreground = _window != null && Native.GetForegroundWindow() == _window.Handle,
            app = _app,
            lastOpenMs = LastOpenMs,
            lastPrepareMs = LastPrepareMs,
            frames = new { count = iv.Length, p50 = P(0.5), p95 = P(0.95), max = iv.Length == 0 ? 0 : iv[^1] },
            intervals = _animator.LastIntervals.Select(x => Math.Round(x, 2)),
            raw = _animator.LastRawIntervals.Select(x => Math.Round(x, 1)),
            selection = _selection,
            pointed = _pointed,
            items = _items.Select(i => new { hwnd = i.Hwnd.ToInt64(), appId = i.AppId, from = Screen(i.From), to = Screen(i.To) }),
            // The close button of the window pointed at (stage 3-3), on the screen; windows closed from here; rearrangements.
            close = CloseState(),
            // The frame of the window pointed at, fading in (B11: its opacity now, its fades so far); the close button's.
            frameOpacity = Math.Round(_window?.FrameOpacity ?? 0, 2), pointFades = _window?.PointFades ?? 0,
            closeOpacity = _close is { IsVisible: true, Content: UIElement c } ? Math.Round(c.Opacity, 2) : 0,
            closes = Closes,
            given = Given, // pictures given under a press (2026-10-23)
            rearranges = Rearranges,
            // The strip of virtual desktops (stage 3-4): its pictures and add button on the screen, the windows drawn in each.
            strip = StripState(),
            // Where the windows go (screen pixels), and the line saying there is none (A7: in the middle of that area).
            area = Screen(new RECT((int)_area.X, (int)_area.Y, (int)(_area.X + _area.Width), (int)(_area.Y + _area.Height))),
            empty = _window?.EmptyAt is { } e ? Screen(new RECT((int)(e.Left * _mon.Scale), (int)(e.Top * _mon.Scale), (int)(e.Right * _mon.Scale), (int)(e.Bottom * _mon.Scale))) : null,
        };
    }

    /// <summary>Whether <paramref name="a"/> stands above <paramref name="b"/> in the z-order (diagnostics).</summary>
    static bool Above(IntPtr a, IntPtr b)
    {
        for (var p = Native.GetWindow(b, Native.GW_HWNDPREV); p != IntPtr.Zero; p = Native.GetWindow(p, Native.GW_HWNDPREV))
            if (p == a) return true;
        return false;
    }

    object? StripState()
    {
        if (!_stripOn || _strip is not { IsVisible: true } strip) return null;
        var (pictures, add) = strip.Rects(_mon.Scale);
        static object R(RECT r) => new { r.Left, r.Top, r.Right, r.Bottom };
        return new
        {
            privateApi = DesktopsInternal.Available,
            desktops = strip.Desktops.Select((d, i) => new
            {
                id = d.Id.ToString(), name = d.Name, current = d.Current, wallpaper = d.Picture != null,
                rect = i < pictures.Count ? R(pictures[i]) : null,
                windows = strip.Shown.Where(w => w.Desktop == d.Id).Select(w => w.Hwnd),
            }),
            add = add is { } a ? R(a) : null,
            // Slides to new places as a desktop came or went (B11), and one under way (the windows are drawn after it).
            slides = strip.Slides, sliding = strip.Sliding,
            // A desktop's × (3-5) where it stands, and for which desktop.
            deskClose = _deskClose is { For: Guid id, IsVisible: true } dc && Native.GetWindowRect(dc.Handle, out var dr) ? new { id = id.ToString(), rect = R(dr) } : null,
            dragging = _dragging,
            // Diagnostics of the pointer over the strip: WPF's view, and whether the overview's window is above the strip's.
            pointerOn = strip.PointerOn?.ToString(),
            mouseOverStrip = strip.IsMouseOver,
            overviewAbove = _window != null && Above(_window.Handle, strip.Handle),
            switches = Switches, zooms = Zooms, // E2: gone to a desktop zooming into its picture
            adds = Adds,
            removes = Removes,
            moves = Moves,
        };
    }

    object? CloseState()
    {
        if (_close is not { For: IntPtr h } || h == IntPtr.Zero || !_close.IsVisible) return null;
        Native.GetWindowRect(_close.Handle, out var r);
        return new { hwnd = h.ToInt64(), rect = new { r.Left, r.Top, r.Right, r.Bottom } };
    }

    public void Dispose()
    {
        Finish();
        if (_windows != null) _windows.Changed -= OnWindowsChanged;
        _windows = null;
        _window?.Close();
        _window = null;
        _close?.Close();
        _close = null;
        _deskClose?.Close();
        _deskClose = null;
        _strip?.Close();
        _strip = null;
        _icons?.Dispose();
        _icons = null;
    }
}
