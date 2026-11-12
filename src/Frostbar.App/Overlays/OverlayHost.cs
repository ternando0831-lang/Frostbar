using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Frostbar.App.Common;
using Frostbar.Core.Localization;
using Frostbar.Core.Settings;
using Frostbar.Core.Shell;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Shell;
using Frostbar.Platform.Sys;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Overlays;

/// <summary>
/// What comes over everything under the menu bar without taking the foreground (stage 2): the banners at the right
/// (<see cref="BannerQueue"/>; the tray apps' balloons in the shape of the notification centre's cards, the user's
/// choice B) and the volume and brightness display at the top centre (<see cref="HudModel"/>; a thin pill, the user's
/// choice C). Placed from the menu bar's bottom (the monitor's top while the bar is hidden over a fullscreen app),
/// <see cref="Inset"/> in, on the primary monitor. One timer, set to the next moment something goes: nothing runs
/// while nothing shows. A banner slides in a little as it fades in, and those under one that goes glide up into its
/// place (positions only, paced by DWM's frames).
/// </summary>
public sealed class OverlayHost : IDisposable
{
    public const double Inset = 8, Between = 8, GlideMs = Motion.BannerGlide;

    /// <summary>
    /// A banner comes in from past the screen's right edge (its width and the inset: macOS's banners slide in from the
    /// edge) over <see cref="EnterMs"/>, and goes <see cref="LeaveBy"/> to the right as it fades. It came only 8 DIP
    /// (2026-10-19, the user's "B10").
    /// </summary>
    public const double EnterFrom = BannerWindow.BannerWidth + Inset + 8, EnterMs = Motion.BannerEnter, LeaveBy = 48;

    readonly Dispatcher _ui;
    readonly Func<ThemeSnapshot> _theme;
    readonly Func<AppearanceSettings> _appearance;
    readonly Func<Strings> _strings;
    readonly Func<string, ImageSource?> _icon;
    /// <summary>DIPs: where things stand from (the bar's bottom, or the monitor's top when it is hidden), and the monitor's left and right.</summary>
    readonly Func<(double top, double left, double right)> _frame;
    /// <summary>DIPs: where a menu bar item is on the screen (null: it is not showing).</summary>
    readonly Func<string, Rect?> _barItem;
    readonly BannerQueue _queue = new();
    readonly HudModel _hud = new();
    readonly Dictionary<uint, BannerWindow> _banners = new();
    readonly DispatcherTimer _timer;
    readonly FrameClock _clock;
    readonly List<Glide> _glides = new();
    HudWindow? _hudWindow;
    bool _disposed;

    sealed record Glide(Window Window, double FromX, double FromY, double ToX, double ToY, long Start, double Ms);

    /// <summary>A banner was clicked: its notice's app hears it (a balloon's NIN_BALLOONUSERCLICK).</summary>
    public event Action<Notice>? BannerClicked;

    public OverlayHost(Dispatcher ui, Func<ThemeSnapshot> theme, Func<AppearanceSettings> appearance, Func<Strings> strings,
        Func<string, ImageSource?> icon, Func<(double top, double left, double right)> frame, Func<string, Rect?> barItem)
    {
        _ui = ui;
        _theme = theme;
        _appearance = appearance;
        _strings = strings;
        _icon = icon;
        _frame = frame;
        _barItem = barItem;
        _timer = new DispatcherTimer(DispatcherPriority.Normal, ui);
        _timer.Tick += (_, _) => OnTimer();
        _clock = new FrameClock(ui);
        _clock.Tick += _ => StepGlides();
    }

    static long Now => Environment.TickCount64;
    static double Scale => Monitors.Primary().Scale;

    Material Glass(double radiusDip) => Materials.Menu(_theme(), _appearance(), (float)(radiusDip * Scale));

    // ---- banners -------------------------------------------------------------------------------------------------

    public IReadOnlyList<BannerWindow> Banners => _queue.Shown.Where(n => _banners.ContainsKey(n.Id)).Select(n => _banners[n.Id]).ToList();
    public bool BannersPaused => _queue.Paused;

    /// <summary>A notice for a banner; <paramref name="quiet"/>: do not disturb, a fullscreen app, the notification centre open.</summary>
    public void Show(Notice n, bool quiet)
    {
        if (_disposed || !_queue.Add(n, Now, quiet)) return;
        Sync(newest: n.Id);
    }

    /// <summary>Every banner goes (a fullscreen app came, do not disturb went on, the setting went off).</summary>
    public void ClearBanners()
    {
        if (_queue.Clear()) Sync();
    }

    // A pass is under way: one called for meanwhile (let through by a window's making or showing, as the volume display's
    // making let a key through, 2026-10-28) is done after it, from the start. Run inside, it made a banner's window again
    // and the first one made was let go of as the outer pass set its own: up for good (seen with the volume display only).
    bool _syncing, _syncAgain;

    /// <summary>Passes done again because one was called for during another (diagnostics).</summary>
    public int BannerPassesAgain { get; private set; }

    /// <summary>Diagnostics "banner-nested": run right after the next banner's window is made, as a call let through by its making would be.</summary>
    public Action? WhileMakingBanner { get; set; }

    /// <summary>The windows follow the queue: those it dropped fade out, a new one comes in, the rest glide into place.</summary>
    void Sync(uint? newest = null)
    {
        if (_syncing) { _syncAgain = true; return; }
        _syncing = true;
        try
        {
            do
            {
                _syncAgain = false;
                SyncPass();
                if (_syncAgain) BannerPassesAgain++;
            } while (_syncAgain);
        }
        finally { _syncing = false; }
    }

    void SyncPass()
    {
        var wanted = _queue.Shown;
        foreach (var gone in _banners.Keys.Where(id => wanted.All(n => n.Id != id)).ToList())
        {
            var w = _banners[gone];
            _banners.Remove(gone);
            _glides.RemoveAll(g => ReferenceEquals(g.Window, w));
            // Out the way it came in: to the right as it fades (it faded where it was, 2026-10-17). Reduced motion: it
            // fades where it is, as it came in (Fades.ComeFrom).
            if (!ReduceMotion.Calms("bannerLeave")) GlideTo(w, w.Left + LeaveBy, w.Top, Fades.PanelOutMs);
            w.CloseFading();
        }
        // One taken away under the pointer hears no MouseLeave: the countdown must not stay stopped for it.
        if (_queue.Paused && !_banners.Values.Any(b => b.PointerOver)) _queue.Resume(Now);
        var (top, _, right) = _frame();
        double y = top + Inset, x = right - Inset - BannerWindow.BannerWidth;
        foreach (var n in wanted)
        {
            if (!_banners.TryGetValue(n.Id, out var w))
            {
                w = Make(n);
                var nested = WhileMakingBanner;
                WhileMakingBanner = null;
                nested?.Invoke();
                _banners[n.Id] = w;
                if (_syncAgain) return; // the queue may have changed meanwhile: the next pass shows it where it goes
            }
            if (!w.IsVisible) // new (made, not shown yet)
            {
                double h = w.MeasuredHeight();
                w.Left = x;
                w.Top = y;
                // From the right as it fades in, both from when it has been drawn (Fades' steps); the fade set before
                // it shows (Fades.FadeIn).
                if (_theme().Animations) Fades.ComeFrom(w, EnterFrom, 0, EnterMs, Motion.BannerSpring);
                w.FadeIn();
                w.Show();
                y += h + Between;
                if (_syncAgain) return;
                continue;
            }
            GlideTo(w, x, y, GlideMs);
            y += w.ActualHeight + Between;
        }
        Arm();
    }

    BannerWindow Make(Notice n)
    {
        var w = new BannerWindow(_theme(), Glass(GlassShape.Banner), _strings(), n, _icon(n.AppId));
        w.Hovered += (_, over) =>
        {
            if (over) _queue.Pause(Now);
            else if (!_banners.Values.Any(b => b.PointerOver)) _queue.Resume(Now);
            Arm();
        };
        w.Clicked += b =>
        {
            Log.Debug($"banner 0x{b.Notice.Id:X8} clicked");
            if (_queue.Dismiss(b.Notice.Id)) { _queue.Resume(Now); Sync(); }
            BannerClicked?.Invoke(b.Notice);
        };
        w.Dismissed += b =>
        {
            if (_queue.Dismiss(b.Notice.Id)) { if (!_banners.Values.Any(o => o != b && o.PointerOver)) _queue.Resume(Now); Sync(); }
        };
        if (!w.HasIcon && n.AppId.Length > 0)
        {
            // The icon may still be loading (the helper process: its first start takes a while, 2026-10-16's pictures):
            // looked at again after 0.4 s, then 0.8 and 1.6 s more.
            int tries = 0;
            var later = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromMilliseconds(400) };
            later.Tick += (_, _) =>
            {
                if (w.IsVisible && _icon(n.AppId) is { } img) { w.SetIcon(img); later.Stop(); return; }
                if (!w.IsVisible || ++tries >= 3) { later.Stop(); return; }
                later.Interval = TimeSpan.FromMilliseconds(400 << tries);
            };
            later.Start();
        }
        return w;
    }

    // ---- the volume and brightness display ----------------------------------------------------------------------

    public HudModel Hud => _hud;
    public HudWindow? HudShown => _hudWindow;

    // The display's window is being made: a call for it meanwhile (a key let through by the making) shows in that window.
    bool _makingHud;

    /// <summary>Calls for the display while its window was being made (diagnostics).</summary>
    public int HudCallsWhileMaking { get; private set; }

    /// <summary>Diagnostics "hud-nested": run inside the next making of the display's window, as a key let through by it would be.</summary>
    public Action? WhileMakingHud { get; set; }

    /// <summary>A key: the display comes (or stays) for a second and a half more.</summary>
    public void ShowHud(HudKind kind, double level, bool muted)
    {
        if (_disposed) return;
        _hud.Show(kind, level, muted, Now);
        if (_hudWindow == null)
        {
            // Called again while its window is made: that window shows the model as it is now, once made. Each call made
            // a window of its own, and the one made inside the other was let go of as the other was set: up for good with
            // what it showed then (2026-10-28, the user's "誤表記の状態で引っ込まない": read from the running shell, no
            // window and no time to hide in the host, the window up and never closed, 7 h on). What let the second call
            // through the making was not caught: a wait on the UI thread inside it may (the GPU's lock is one; not confirmed).
            if (_makingHud) { HudCallsWhileMaking++; return; }
            HudWindow w;
            _makingHud = true;
            try
            {
                w = new HudWindow(_theme(), Glass(HudWindow.PillHeight / 2));
                var nested = WhileMakingHud;
                WhileMakingHud = null;
                nested?.Invoke();
            }
            finally { _makingHud = false; }
            if (!_hud.Visible) { w.Close(); return; } // hidden meanwhile (the setting turned off)
            _hudWindow = w;
            Present(w);
            PlaceHud(w);
            w.FadeIn(); // before it shows (Fades.FadeIn)
            w.Show();
        }
        else
        {
            Present(_hudWindow);
            PlaceHud(_hudWindow); // another kind: under its own item
        }
        Arm();
    }

    /// <summary>The menu bar item lit while the display stands under it (27.2's pictures show a plate behind it); null: none.</summary>
    public event Action<string?>? HudItemLit;
    string? _hudItem;

    /// <summary>The item the display stands under (diagnostics).</summary>
    public string? HudItem => _hudItem;

    void LightHudItem(string? key)
    {
        if (key == _hudItem) return;
        _hudItem = key;
        HudItemLit?.Invoke(key);
    }

    /// <summary>The level of what shows changed (the key's own change arrives a moment after the key).</summary>
    public void UpdateHud(HudKind kind, double level, bool muted)
    {
        // Into the model even while its window is being made (it shows the model once made): it was dropped then.
        if (_hud.Update(kind, level, muted) && _hudWindow != null) Present(_hudWindow);
    }

    void Present(HudWindow w) => w.Set(_hud.Kind, _hud.Percent, _hud.Waves, _hud.Muted);

    void PlaceHud(HudWindow w)
    {
        var (top, left, right) = _frame();
        string? key = HudLayout.ItemOf(_hud.Kind);
        Rect? item = key != null ? _barItem(key) : null;
        w.Left = Math.Round(HudLayout.Left(item is { } r ? r.Left + r.Width / 2 : null, HudWindow.PillWidth, left, right, Inset));
        w.Top = top + Inset;
        LightHudItem(item != null ? key : null);
    }

    public void HideHud()
    {
        _hud.Hide();
        CloseHud();
    }

    void CloseHud()
    {
        var w = _hudWindow;
        _hudWindow = null;
        LightHudItem(null);
        w?.CloseFading(); // the content and its plate together (at once when animations are off)
    }

    // ---- time ---------------------------------------------------------------------------------------------------

    /// <summary>The timer, set to the next moment something goes.</summary>
    void Arm()
    {
        _timer.Stop();
        long? due = _queue.NextDue;
        if (_hud.HideAt is { } h) due = due is { } d ? Math.Min(d, h) : h;
        if (due is not { } at) return;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, at - Now));
        _timer.Start();
    }

    void OnTimer()
    {
        _timer.Stop();
        long now = Now;
        if (_queue.Expire(now)) Sync();
        if (_hud.Expire(now)) CloseHud();
        Arm();
    }

    // ---- glides -------------------------------------------------------------------------------------------------

    void GlideTo(Window w, double x, double y, double ms)
    {
        _glides.RemoveAll(g => ReferenceEquals(g.Window, w));
        Fades.StopGliding(w); // still coming in: it goes on from where it is
        if (Math.Abs(w.Left - x) < 0.5 && Math.Abs(w.Top - y) < 0.5) return;
        if (!_theme().Animations) { w.Left = x; w.Top = y; return; }
        _glides.Add(new Glide(w, w.Left, w.Top, x, y, Stopwatch.GetTimestamp(), ms));
        _clock.Start();
    }

    void StepGlides()
    {
        long now = Stopwatch.GetTimestamp();
        foreach (var g in _glides.ToList())
        {
            if (!g.Window.IsVisible) { _glides.Remove(g); continue; } // gone (a banner that faded out)
            double t = Math.Min(1, (now - g.Start) / (double)Stopwatch.Frequency * 1000 / g.Ms);
            double e = Motion.Out(t);
            g.Window.Left = Math.Round(g.FromX + (g.ToX - g.FromX) * e, 2);
            g.Window.Top = Math.Round(g.FromY + (g.ToY - g.FromY) * e, 2);
            if (t >= 1) _glides.Remove(g);
        }
        if (_glides.Count == 0) _clock.Stop();
    }

    /// <summary>The bar moved, the display changed: everything stands again from the bar.</summary>
    public void Relayout() => Relayout(barMadeAgain: false);

    /// <summary>
    /// <see cref="Relayout"/>; <paramref name="barMadeAgain"/>: the menu bar is a new one, its item under the volume display
    /// not lit yet (the old one's was: a new bar while the display showed kept it unlit to its end, 2026-11-06 review).
    /// </summary>
    public void Relayout(bool barMadeAgain)
    {
        if (barMadeAgain) _hudItem = null;
        if (_banners.Count > 0) Sync();
        if (_hudWindow != null) PlaceHud(_hudWindow);
    }

    /// <summary>Diagnostics: the banners shown (their rectangles in pixels) and the display.</summary>
    public object State() => new
    {
        banners = Banners.Select(b => new
        {
            id = b.Notice.Id, title = b.Notice.Title, app = b.Notice.App, hovered = b.PointerOver,
            rect = Native.FrameBounds(b.Handle) is var r ? new { left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom } : null,
            dismiss = b.PointerOver && b.DismissButton.IsVisible ? ScreenBox(b.DismissButton) : null,
        }).ToList(),
        paused = _queue.Paused,
        nextDueMs = _queue.NextDue is { } d ? d - Now : (long?)null,
        // Every banner window up, the host's or not (one it let go of stays up), and the passes done again.
        bannerWindows = Application.Current.Windows.OfType<BannerWindow>().Count(x => x.IsVisible),
        passesAgain = BannerPassesAgain,
        hud = new
        {
            visible = _hudWindow != null, kind = _hud.Kind.ToString().ToLowerInvariant(), percent = _hud.Percent, muted = _hud.Muted,
            waves = _hud.Waves, hideInMs = _hud.HideAt is { } h ? h - Now : (long?)null,
            rect = _hudWindow != null ? Native.FrameBounds(_hudWindow.Handle) is var hr ? new { left = hr.Left, top = hr.Top, right = hr.Right, bottom = hr.Bottom } : null : null,
            // Every display window up, the host's or not (one it let go of stays up), and the calls while one was made.
            windows = Application.Current.Windows.OfType<HudWindow>().Count(x => x.IsVisible),
            callsWhileMaking = HudCallsWhileMaking,
            // The bar item it stands under and lights, and its bar's parts filled to the end.
            item = _hudItem, partsFull = _hudWindow?.PartsFull,
        },
    };

    static object? ScreenBox(FrameworkElement e)
    {
        if (!e.IsVisible || PresentationSource.FromVisual(e) == null) return null;
        var p = e.PointToScreen(new Point(0, 0));
        var q = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
        return new { left = (int)p.X, top = (int)p.Y, right = (int)q.X, bottom = (int)q.Y };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _clock.Dispose();
        foreach (var w in _banners.Values) w.Close();
        _banners.Clear();
        _hudWindow?.Close();
        _hudWindow = null;
    }
}
