using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Frostbar.App.Dock;
using Frostbar.App.MenuBar;
using Frostbar.Core.Localization;
using Frostbar.Core.Shell;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Shell;
using Frostbar.Platform.Sys;

namespace Frostbar.App.Overlays;

/// <summary>
/// The Dynamic Island's life (2026-11-08; ADR-017): the window at the primary monitor's top middle, Core's
/// <see cref="IslandModel"/> deciding what shows, and what it hears: what is played and the battery and the output
/// (the menu bar's services), Focus (do not disturb and focus sessions), apps' progress (the Dock's). It shows while the
/// menu bar does (not over a full-screen app). A spec's shell (FROSTBAR_E2E_ISLAND=spec, with --diag only) hears none of
/// them: the spec sets what plays, progresses and is told through the diagnostics pipe.
/// </summary>
public sealed class IslandHost : IDisposable
{
    readonly Dispatcher _ui;
    readonly Func<Strings> _strings;
    readonly IslandModel _model = new();
    readonly IslandWindow _window = new();
    readonly DispatcherTimer _timer;
    readonly DispatcherTimer _leave;
    readonly bool _spec;
    MenuBarController? _bar;
    DockController? _dock;
    Action? _unwatchFocus;
    bool? _focusWas;
    int? _batteryWas;
    string? _outputWas;
    bool _disposed;

    // A spec's own: what plays (app id, title, artist; null: nothing), the apps progressing.
    (string app, string title, string artist, bool playing)? _specPlaying;
    List<DockController.DockProgress> _specProgress = [];

    /// <summary>Times each kind of notice was shown, and the island opened (diagnostics).</summary>
    public Dictionary<string, int> Counts { get; } = new();

    public IslandWindow Window => _window;

    public IslandHost(Dispatcher ui, Func<Strings> strings, bool spec)
    {
        _ui = ui;
        _strings = strings;
        _spec = spec;
        _timer = new DispatcherTimer(DispatcherPriority.Normal, ui);
        _timer.Tick += (_, _) => { _timer.Stop(); Refresh(); };
        // The pointer left the opened island: it closes a moment later unless it came back (no click outside is seen: the
        // island never takes the foreground).
        _leave = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Normal, (_, _) =>
        {
            _leave!.Stop();
            if (!_window.IsMouseOver) Close();
        }, ui);
        _leave.Stop();
        _window.Clicked += OnClicked;
        _window.PointerLeft += () => _leave.Start();
        _window.Pressed += OnPressed;
        if (!spec) _unwatchFocus = FocusSessions.Watch(() => ui.BeginInvoke(OnFocus));
    }

    static double Now => Environment.TickCount64;

    /// <summary>Hears the bar and the Dock made now (made again on a display change): the window follows the bar's showing.</summary>
    public void Attach(MenuBarController bar, DockController? dock, MonitorInfo mon, bool animations)
    {
        Detach();
        _bar = bar;
        _dock = dock;
        _window.Animations = animations;
        bar.Window.IsVisibleChanged += OnBarShown;
        bar.Menus.Closed += Raise;
        bar.LookChanged += OnBarLook;
        OnBarLook();
        if (!_spec)
        {
            bar.Battery.Changed += OnBattery;
            bar.Audio.Changed += OnOutput;
            bar.ServicesStarted += OnServices;
            if (bar.Media != null) bar.Media.Changed += OnMedia;
            if (dock != null) dock.ProgressChanged += OnProgress;
            _batteryWas ??= bar.Battery.Present ? bar.Battery.Percent : null;
            _outputWas ??= bar.Audio.DeviceName;
            _focusWas ??= DoNotDisturb.IsOn() == true || FocusSessions.Active;
        }
        MoveTo(mon);
        OnMedia();
        OnProgress();
        Refresh();
        OnBarShown();
    }

    /// <summary>At the top middle of the primary monitor (and again when it moves or changes size at the same scale).</summary>
    public void MoveTo(MonitorInfo mon)
    {
        var s = mon.Scale;
        _window.Left = Math.Round(mon.Bounds.Left / s + (mon.Bounds.Width / s - IslandWindow.Wide) / 2);
        _window.Top = mon.Bounds.Top / s;
    }

    /// <summary>
    /// The Dock made again (another edge, larger icons): the apps' progress is heard from the new one (the old one,
    /// disposed, no longer hears the taskbar, and a click on its item went to it).
    /// </summary>
    public void UseDock(DockController? dock)
    {
        if (_dock != null) _dock.ProgressChanged -= OnProgress;
        _dock = dock;
        if (!_spec && dock != null) dock.ProgressChanged += OnProgress;
        OnProgress();
    }

    void Detach()
    {
        if (_bar is { } bar)
        {
            bar.Window.IsVisibleChanged -= OnBarShown;
            bar.Menus.Closed -= Raise;
            bar.LookChanged -= OnBarLook;
            bar.Battery.Changed -= OnBattery;
            bar.Audio.Changed -= OnOutput;
            bar.ServicesStarted -= OnServices;
            if (bar.Media != null) bar.Media.Changed -= OnMedia;
        }
        if (_dock != null) _dock.ProgressChanged -= OnProgress;
        _bar = null;
        _dock = null;
    }

    public bool Animations { set => _window.Animations = value; }

    /// <summary>The bar's background turned dark or light: the island's key line follows (HIG: shown over a dark background).</summary>
    void OnBarLook()
    {
        if (!_disposed && _bar != null) _window.DarkBehind = _bar.DarkBehind;
    }

    /// <summary>The island shows with the menu bar, over it (a window raised after it, as the bar is shown again).</summary>
    void OnBarShown(object? sender = null, DependencyPropertyChangedEventArgs e = default) => OnBarShown();

    void OnBarShown()
    {
        if (_disposed) return;
        bool shown = _bar?.Window.IsVisible == true;
        if (shown && !_window.IsVisible) _window.Show();
        else if (!shown && _window.IsVisible) _window.Hide();
        if (shown) _ui.BeginInvoke(DispatcherPriority.Background, Raise);
    }

    /// <summary>
    /// The island over the menu bar (both stay on top: the one raised last is over). Raised again whenever the bar is
    /// raised over it: shown again, its menus and panels closed (they raise the bar over their click shield), standing
    /// over a full-screen app. Under the bar, the bar's near-clear background (it takes clicks on its empty parts) would
    /// take the island's pointer (the Gemini review, 2026-11-08; read in the code, not seen on the screen).
    /// </summary>
    public void Raise()
    {
        if (_disposed) return;
        var h = new WindowInteropHelper(_window).Handle;
        if (h != IntPtr.Zero && _window.IsVisible)
            Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // ---- what it hears ------------------------------------------------------------------------------------------

    void OnServices()
    {
        if (_bar?.Media is { } m) { m.Changed -= OnMedia; m.Changed += OnMedia; }
        OnMedia();
    }

    void OnMedia()
    {
        var now = _spec ? null : _bar?.Media?.Current;
        bool playing = _spec ? _specPlaying is { playing: true } : now is { Playing: true };
        _model.SetPlaying(playing ? (_spec ? _specPlaying!.Value.app : now!.AppId) : null);
        Refresh();
    }

    void OnProgress()
    {
        var list = _spec ? _specProgress : _dock?.Progressing ?? [];
        _model.SetProgress(list.Select(p => new IslandActivity(IslandKind.Progress, p.Key, p.Progress)));
        Refresh();
    }

    void OnBattery()
    {
        if (_bar is not { } bar || !bar.Battery.Present) return;
        int now = bar.Battery.Percent;
        if (LowBattery.Crossed(_batteryWas, now, !bar.Battery.OnAc) is int level)
            Notify(new IslandNotice(NoticeKind.Battery, _strings()["island.lowBattery"], _strings()["island.plugIn"], now));
        _batteryWas = now;
    }

    void OnOutput()
    {
        if (_bar is not { } bar) return;
        string now = bar.Audio.DeviceName;
        bool headphones = bar.Audio.Headphones;
        if (OutputNotice.For(_outputWas, now, headphones) is NoticeKind kind)
        {
            int? battery = headphones && bar.Audio.DeviceContainer is Guid c ? bar.Bluetooth?.BatteryOfContainer(c) : null;
            Notify(kind == NoticeKind.Headphones
                ? new IslandNotice(NoticeKind.Headphones, now, _strings()["island.connected"], battery)
                : new IslandNotice(NoticeKind.Output, now));
        }
        _outputWas = now;
    }

    void OnFocus()
    {
        if (_disposed) return;
        bool on = DoNotDisturb.IsOn() == true || FocusSessions.Active;
        if (_focusWas is bool was && was != on)
        {
            var s = _strings();
            Notify(new IslandNotice(NoticeKind.Focus, DoNotDisturb.IsOn() == true || !on ? s["island.dnd"] : s["cc.focus"], on ? s["island.on"] : s["island.off"], On: on));
        }
        _focusWas = on;
    }

    void Notify(IslandNotice n)
    {
        _model.Notify(n, Now);
        Counts[n.Kind.ToString()] = Counts.GetValueOrDefault(n.Kind.ToString()) + 1;
        Log.Debug($"island: {n.Kind} {n.Title}");
        Refresh();
    }

    // ---- what it shows ------------------------------------------------------------------------------------------

    void Refresh()
    {
        if (_disposed) return;
        var view = _model.At(Now);
        _window.Show(new IslandFace(view.Shape, Live(view.Main), Live(view.Second), view.Notice));
        _timer.Stop();
        if (_model.NextChange(Now) is double at)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, at - Now));
            _timer.Start();
        }
    }

    /// <summary>An activity made ready to draw: what plays from the media's now playing, a progress from the Dock's.</summary>
    IslandLive? Live(IslandActivity? a)
    {
        if (a == null) return null;
        var s = _strings();
        if (a.Kind == IslandKind.Playing)
        {
            if (_spec && _specPlaying is { } sp) return new IslandLive(IslandKind.Playing, a.Key, null, IslandWindow.TintOf(null), 0, sp.title, sp.artist, sp.playing);
            var now = _bar?.Media?.Current;
            return new IslandLive(IslandKind.Playing, a.Key, now?.Art, IslandWindow.TintOf(now?.Art), 0, now?.Title ?? "", now?.Artist ?? "", now?.Playing == true);
        }
        var p = (_spec ? _specProgress : _dock?.Progressing ?? []).FirstOrDefault(x => x.Key == a.Key);
        if (p == null) return null;
        string detail = p.State switch
        {
            Core.Dock.ProgressState.Paused => s["island.paused"],
            Core.Dock.ProgressState.Error => s["island.error"],
            _ => p.Progress < 0 ? s["island.inProgress"] : $"{Math.Round(p.Progress * 100)}%",
        };
        return new IslandLive(IslandKind.Progress, a.Key, p.Icon, IslandWindow.TintOf(null), p.Progress, p.Name, detail, true);
    }

    void OnClicked()
    {
        if (_window.Shape == IslandShape.Open) { Close(); return; }
        if (_model.Open(Now))
        {
            Counts["opened"] = Counts.GetValueOrDefault("opened") + 1;
            Refresh();
        }
    }

    void Close()
    {
        _leave.Stop();
        _model.Close();
        Refresh();
    }

    void OnPressed(string what)
    {
        Counts["pressed " + what] = Counts.GetValueOrDefault("pressed " + what) + 1;
        if (_spec) return; // a spec's shell commands nothing (its presses are counted)
        var main = _model.At(Now).Main;
        var media = _bar?.Media;
        switch (what)
        {
            case "previous": if (media != null) _ = media.PreviousAsync(); break;
            case "next": if (media != null) _ = media.NextAsync(); break;
            case "playpause": if (media != null) _ = media.PlayPauseAsync(); break;
            case "app" when main?.Kind == IslandKind.Playing && media?.Current is { AppId.Length: > 0 } n:
                _bar?.Runner.Open(@"shell:AppsFolder\" + n.AppId);
                Close();
                break;
            case "app" when main?.Kind == IslandKind.Progress:
                _dock?.ClickItem(main.Key);
                Close();
                break;
        }
    }

    // ---- a spec's own ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Diagnostics "island-set" (a spec's shell): "play &lt;app&gt;|&lt;title&gt;|&lt;artist&gt;", "pause", "stop",
    /// "progress &lt;key&gt;|&lt;name&gt;|&lt;0-1 or -1&gt;" (adds or moves one), "progress-clear", "notice
    /// &lt;Headphones|Output|Focus|Battery&gt;|&lt;title&gt;|&lt;detail&gt;|&lt;percent or -&gt;|&lt;on or off&gt;", "open", "close".
    /// </summary>
    public string SpecSet(string arg)
    {
        if (!_spec) return "error: not a spec's island (FROSTBAR_E2E_ISLAND=spec)";
        int space = arg.IndexOf(' ');
        string verb = space < 0 ? arg : arg[..space], rest = space < 0 ? "" : arg[(space + 1)..];
        var parts = rest.Split('|');
        switch (verb)
        {
            case "play" when parts.Length == 3: _specPlaying = (parts[0], parts[1], parts[2], true); OnMedia(); return "ok";
            case "pause" when _specPlaying is { } p: _specPlaying = p with { playing = false }; OnMedia(); return "ok";
            case "stop": _specPlaying = null; OnMedia(); return "ok";
            case "progress" when parts.Length == 3 && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v):
                _specProgress.RemoveAll(x => x.Key == parts[0]);
                _specProgress.Insert(0, new DockController.DockProgress(parts[0], parts[1], null, v, v < 0 ? Core.Dock.ProgressState.Indeterminate : Core.Dock.ProgressState.Normal));
                OnProgress();
                return "ok";
            case "progress-clear": _specProgress = []; OnProgress(); return "ok";
            case "notice" when parts.Length == 5 && Enum.TryParse<NoticeKind>(parts[0], out var kind):
                Notify(new IslandNotice(kind, parts[1], parts[2], int.TryParse(parts[3], out int pc) ? pc : null, parts[4] != "off"));
                return "ok";
            case "open": OnClicked(); return _window.Shape == IslandShape.Open ? "ok" : "not opened";
            case "close": Close(); return "ok";
            default: return "error: play <app>|<title>|<artist>, pause, stop, progress <key>|<name>|<0-1 or -1>, progress-clear, notice <kind>|<title>|<detail>|<percent or ->|<on or off>, open, close";
        }
    }

    /// <summary>Diagnostics "island".</summary>
    public object State()
    {
        var r = _window.IslandRect;
        var second = _window.SecondRect;
        var art = _window.ArtRect;
        var plan = _window.Plan;
        static double[] Box(Rect b) => [Math.Round(b.X, 1), Math.Round(b.Y, 1), Math.Round(b.Width, 1), Math.Round(b.Height, 1)];
        return new
        {
            shown = _window.IsVisible,
            shape = _window.Shape.ToString(),
            moving = _window.Moving,
            rect = Box(r),
            second = second.IsEmpty ? null : Box(second),
            // Where it is going (DIPs, x from the screen's middle), the picture now (screen DIPs) and how much it shows,
            // the key line, and how many faces carried the picture or faded everything in (diagnostics).
            target = new[] { plan.Body.X, plan.Body.Width, plan.Body.Height },
            art = Box(art),
            artShows = Math.Round(_window.ArtOpacity, 2),
            artTakesPointer = _window.ArtTakesPointer,
            artTarget = plan.Picture is { } p ? new[] { p.X, p.Y, p.Width, p.Height } : null,
            keyLine = _window.KeyLineShown,
            keyLineColor = _window.KeyLineColor,
            carried = _window.Carried,
            faded = _window.Faded,
            words = _window.Words,
            buttons = _window.Buttons.ToDictionary(b => b.Key, b => new[] { Math.Round(b.Value.X, 1), Math.Round(b.Value.Y, 1), Math.Round(b.Value.Width, 1), Math.Round(b.Value.Height, 1) }),
            counts = Counts,
            spec = _spec,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
        _unwatchFocus?.Invoke();
        _timer.Stop();
        _leave.Stop();
        _window.Close();
    }
}
