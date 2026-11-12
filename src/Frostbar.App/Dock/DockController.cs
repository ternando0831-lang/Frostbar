using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Frostbar.App.Common;
using Frostbar.App.Menus;
using Frostbar.Core.Dock;
using Frostbar.Core.Localization;
using Frostbar.Core.Menus;
using Frostbar.Core.Settings;
using Frostbar.Platform.Apps;
using Frostbar.Platform.Icons;
using Frostbar.Platform.Identity;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Input;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Shell;
using Frostbar.Platform.Sys;
using Frostbar.Platform.Tray;
using Frostbar.Platform.Wm;
using Menu = Frostbar.Core.Menus.Menu;
using MenuItem = Frostbar.Core.Menus.MenuItem;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Dock;

/// <summary>
/// Everything behind the Dock: pinned apps (imported once from Explorer's taskbar), running apps grouped by AppID,
/// icons, clicks (start, bring forward, minimize; middle click opens another window), bounces while an app starts or
/// asks for attention, the right-click menu, and the Recycle Bin.
/// </summary>
public sealed class DockController : IDisposable, AppGrid.IAppShelf
{
    const double LaunchBounceSeconds = 10;
    const int AttentionHops = 3;

    readonly AppSettings _settings;
    readonly SettingsSaver _saver;
    readonly ThemeService _theme;
    readonly AppIdentity _identity;
    readonly ForegroundTracker _front;
    readonly WindowTracker _windows;
    readonly IconService _icons;
    readonly RecycleBin _bin;
    readonly PopupMenuSession _menu;
    readonly Func<Strings> _strings;
    readonly Dispatcher _ui;
    readonly List<string> _runningOrder = new();
    /// <summary>The windows minimized into the Dock as pictures, in the order they were first seen minimized (stage 7-2).</summary>
    readonly List<long> _minimizedOrder = new();
    /// <summary>AppIDs being started: when, and how many windows they had then (the bounce ends when one more shows).</summary>
    readonly Dictionary<string, LaunchUnderWay> _launching = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// An app starting from an item: when, its item's windows then, the program started and its windows then, the window
    /// in front then; landed once the app came to the front (its window not listed yet), and that window.
    /// </summary>
    sealed class LaunchUnderWay
    {
        public long Since;
        public int Windows;
        public string Key = "";
        public string? Program;
        public int OfProgram;
        public IntPtr FrontBefore;
        public bool Landed;
        public IntPtr FrontWindow;
    }
    readonly DispatcherTimer _launchTimeout;
    readonly Dictionary<string, (string key, string source)> _iconOf = new();
    List<PinnedApp> _pins = new();
    IReadOnlyList<DockItem> _items = [];
    MonitorInfo _mon;
    /// <summary>The kept shortcuts' and programs' AppIDs between starts (asked of Explorer's resolver only when a file changed).</summary>
    readonly ShortcutIds _shortcutIds = ShortcutIds.Parse(ReadShortcutIds());
    static string ShortcutIdsFile => Path.Combine(AppPaths.Cache, "shortcut-ids.json");

    public DockWindow Window { get; }
    public IReadOnlyList<DockItem> Items => _items;
    public WindowTracker Windows => _windows;
    public RecycleBin Bin => _bin;
    public PopupMenuSession Menu => _menu;

    /// <param name="mayShowWindow">See <see cref="MayShowWindow"/>: given here, as the first rows are built in here.</param>
    public DockController(MonitorInfo mon, AppSettings settings, SettingsSaver saver, ThemeService theme, AppIdentity identity, ForegroundTracker front,
        HotkeyService hotkeys, Func<RECT> barRect, Func<Strings> strings, Dispatcher ui, string? trashFolder = null,
        Func<RunningWindow, bool>? mayShowWindow = null, Func<RunningWindow, bool>? mayListWindow = null)
    {
        MayShowWindow = mayShowWindow;
        MayListWindow = mayListWindow;
        _mon = mon;
        _settings = settings;
        _saver = saver;
        _theme = theme;
        _identity = identity;
        _front = front;
        _strings = strings;
        _ui = ui;
        var d = settings.Dock;
        var took = Stopwatch.StartNew();
        long Lap() { long ms = took.ElapsedMilliseconds; took.Restart(); return ms; }
        _windows = new WindowTracker(identity, ui) { MinimizeTarget = MinimizeTargetOf };
        long tracker = Lap();
        _iconPx = IconPxFor(d, mon);
        _icons = new IconService(ui, _iconPx);
        _bin = new RecycleBin(ui, trashFolder);
        _menu = new PopupMenuSession(hotkeys, barRect);
        _launchTimeout = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ExpireLaunches(), ui);
        long services = Lap();

        Window = new DockWindow(mon, Material(theme.Current), DockFrame.Parse(d.Position));
        long window = Lap();
        var s = Window.Surface;
        s.BaseSize = d.IconSize;
        s.MaxSize = d.MagnifiedSize;
        s.Spacing = d.Spacing;
        s.PlatePad = d.Padding;
        s.Magnify = d.Magnification;
        s.ShowDots = d.ShowIndicators;
        s.Animations = theme.Current.Animations;
        s.SetTheme(Materials.Text(theme.Current));
        // Its plate light over a white window, dark over a dark one (item 4): the dots and separators with it.
        Window.ToneChanged += dark => Window.Surface.SetTheme(Materials.Text(_theme.Current, dark));
        s.ItemClicked += OnClick;
        s.ItemMenu += OnMenu;
        s.ItemMoved += OnItemMoved;
        s.ItemDraggedOut += OnDraggedOut;
        s.FilesDropped += OnFilesDropped;
        long look = Lap();

        ImportTaskbarPinsOnce();
        AddDownloadsOnce();
        ResolvePins();
        ResolveRecent();
        _showingRecent = d.ShowRecent;
        _intoIcon = d.MinimizeIntoIcon;
        long pins = Lap();
        _windows.Changed += Rebuild;
        _windows.Flashing += OnFlashing;
        _windows.Activated += OnActivated;
        _onFront = _ => Rebuild();
        _front.Changed += _onFront;
        _bin.Changed += Rebuild;
        _icons.Loaded += key =>
        {
            if (key.StartsWith(StackIcon, StringComparison.Ordinal)) (_menu.Panel as FolderStackPopup)?.RefreshIcons();
            else Window.Surface.Redraw();
        };
        Window.Surface.PlateChanged += _ => { if (Hiding) UpdateSpan(); };
        // Closed without input too (an item chosen blinks first; the diagnostics): the ticker looks again, or a Dock with
        // auto-hide waited for the next input to go (2026-10-16, dock-autohide: it stayed up with the pointer away).
        _menu.Closed += () => { _menuOpen = false; Window.Surface.Hold = false; _lookAgain = true; };
        Rebuild();
        long rebuild = Lap();
        Log.Debug($"dock: made in {tracker + services + window + look + pins + rebuild} ms (windows {tracker}, services {services}, window and GPU {window}, look {look}, pins {pins}, first layout {rebuild})");
        // The AppIDs kept from the last start are asked again a while after this one, a pin an idle moment.
        _pinCheck = new DispatcherTimer(TimeSpan.FromSeconds(6), DispatcherPriority.ApplicationIdle, (_, _) => { _pinCheck!.Stop(); CheckPinIds(0, 0); }, ui);
        _pinCheck.Start();
        // Written now (a small file): a shell that ends soon after starting (the startup spec's) left none behind when
        // this waited for an idle moment, and every start asked again.
        if (_shortcutIds.Changed) SaveShortcutIds();
    }

    readonly DispatcherTimer? _pinCheck;

    static string? ReadShortcutIds()
    {
        try { return File.Exists(ShortcutIdsFile) ? File.ReadAllText(ShortcutIdsFile) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Debug("dock: the kept AppIDs unread: " + ex.Message); return null; }
    }

    /// <summary>Writes the kept AppIDs (only the files still kept in the Dock or among its recent apps).</summary>
    void SaveShortcutIds()
    {
        _shortcutIds.KeepOnly(_settings.Dock.Pins.Concat(_settings.Dock.Recent).Where(p => p.Kind == "path").Select(p => p.Id));
        if (!_shortcutIds.Changed) return;
        try
        {
            Directory.CreateDirectory(AppPaths.Cache);
            string tmp = ShortcutIdsFile + ".tmp";
            File.WriteAllText(tmp, _shortcutIds.ToJson());
            File.Move(tmp, ShortcutIdsFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Debug("dock: the kept AppIDs not written: " + ex.Message); }
    }

    /// <summary>Diagnostics: the kept AppIDs asked again after start-up (pins), and how many differed.</summary>
    public int PinIdsChecked { get; private set; } = -1;
    public int PinIdsDiffered { get; private set; }

    /// <summary>
    /// Asks Explorer's resolver again for the kept pins' (and recent apps') AppIDs, one an idle moment, and puts right
    /// those that differ from the ones kept from the last start: the Dock is made again with them.
    /// </summary>
    void CheckPinIds(int at, int differed)
    {
        if (_disposed) return;
        var files = _settings.Dock.Pins.Concat(_settings.Dock.Recent).Where(p => p.Kind == "path").Select(p => p.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (at < files.Count)
        {
            string path = files[at];
            if (File.Exists(path))
            {
                long written = File.GetLastWriteTimeUtc(path).Ticks;
                string appId = _identity.AppIdForShortcut(path) ?? path;
                if (_shortcutIds.Get(path, written) != appId)
                {
                    _shortcutIds.Set(path, written, appId);
                    differed++;
                    Log.Info($"dock: a kept AppID differed and was put right: {Log.Redact(path)}");
                }
            }
            _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, () => CheckPinIds(at + 1, differed));
            return;
        }
        PinIdsChecked = files.Count;
        PinIdsDiffered = differed;
        if (differed > 0)
        {
            ResolvePins();
            ResolveRecent();
            Rebuild();
        }
        SaveShortcutIds();
    }

    // The radius the plate is cut with follows its thickness as it is laid out (DockWindow: WindowBackdrop.Roundness);
    // the settings' thickness gives the material's until then.
    Platform.Dwm.Material Material(ThemeSnapshot t) => Materials.DockPlate(t, _settings.Appearance,
        (float)((_settings.Dock.IconSize + 2 * _settings.Dock.Padding) * GlassShape.DockRoundness * _mon.Scale));

    // ---- the apps' taskbar progress and badges (macOS's Dock shows a download's progress on its app, and a red badge) ----
    // They come to the tray's window while this shell's tray runs (TaskbandListener); each app's windows' marks are put
    // together into one progress and one badge on its item.

    TaskbandListener? _taskband;

    /// <summary>Where the apps' taskbar marks come from (the tray's window, once the tray runs); null: none are shown.</summary>
    public TaskbandListener? Taskband
    {
        get => _taskband;
        set
        {
            if (_taskband != null) _taskband.Changed -= UpdateMarks;
            _taskband = value;
            if (value != null)
            {
                value.Changed += UpdateMarks;
                // Marks set before it listened: the apps set them again when asked (as for a new taskbar button).
                value.AskAgain(_windows.Windows.Select(w => w.Hwnd));
            }
            UpdateMarks();
        }
    }

    void UpdateMarks()
    {
        if (_taskband == null) { Window.Surface.SetMarks(null); SetProgressing([]); return; }
        var marks = new Dictionary<string, DockScene.Mark>();
        var progressing = new List<DockProgress>();
        foreach (var item in _items)
        {
            if (item.Kind != DockItemKind.App || item.Windows.Count == 0) continue;
            var received = item.Windows.Select(w => _taskband.MarksOf(new IntPtr(w.Hwnd))).OfType<ReceivedMarks>().ToList();
            if (received.Count == 0) continue;
            var app = DockMarks.Combine(received.Select(r => r.Marks).ToList());
            if (!app.Any) continue;
            marks[item.Key] = new DockScene.Mark(app.State, app.Progress, app.Count, app.BadgeWindow >= 0 ? received[app.BadgeWindow].Overlay : null);
            if (app.State is ProgressState.Normal or ProgressState.Indeterminate or ProgressState.Paused or ProgressState.Error)
                progressing.Add(new DockProgress(item.Key, item.Name, IconFor(item), app.State == ProgressState.Indeterminate ? -1 : app.Progress, app.State));
        }
        Window.Surface.SetMarks(marks.Count > 0 ? marks : null);
        SetProgressing(progressing);
    }

    /// <summary>An app's progress as the Dock shows it (the Dynamic Island shows it too): its item, name, icon, how far (-1: not known).</summary>
    public sealed record DockProgress(string Key, string Name, System.Windows.Media.ImageSource? Icon, double Progress, ProgressState State);

    /// <summary>The apps showing a progress now, in the Dock's order.</summary>
    public IReadOnlyList<DockProgress> Progressing { get; private set; } = [];
    /// <summary><see cref="Progressing"/> changed.</summary>
    public event Action? ProgressChanged;

    void SetProgressing(List<DockProgress> now)
    {
        if (now.SequenceEqual(Progressing)) return;
        Progressing = now;
        ProgressChanged?.Invoke();
    }

    /// <summary>A click on the item <paramref name="key"/>, as on its icon (the Dynamic Island opens an app this way).</summary>
    public void ClickItem(string key)
    {
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].Key == key) { OnClick(i, MouseButton.Left); return; }
    }

    public void ApplyTheme(ThemeSnapshot t)
    {
        Window.SetMaterial(Material(t));
        Window.Surface.SetTheme(Materials.Text(t, Window.Tone.dark));
        Window.Surface.Animations = t.Animations;
    }

    // ---- auto-hide (macOS's "Automatically hide and show the Dock") ----------------------------------------------
    // Hidden, the Dock waits below the edge it stands on; the pointer resting on that edge under it brings it up, and
    // it goes again a moment after the pointer has left it (not while its menu is open). The pointer is looked at on the
    // background ticker without a hook, and only after input (a cold GetCursorPos costs 0.15 ms: see the hot corners).

    // A full-screen app hides the Dock the same way (macOS), with two differences: the window itself is hidden while out
    // of sight (a window over a full-screen game, even a transparent one, can keep the game from presenting its frames
    // directly), and the edge does nothing while the pointer is hidden or held inside a window (a game playing).

    const double SlideSeconds = Motion.DockSlide / 1000;
    const long RevealMs = 100, HideMs = 450;
    const string AutoHideJob = "dock-autohide";
    BackgroundTicker? _ticker;
    volatile bool _autoHide, _fullScreen, _shown = true, _menuOpen;
    /// <summary>Something the ticker judges by changed without input (its menu closed): it looks once more.</summary>
    volatile bool _lookAgain;
    bool _watching;
    bool Hiding => _autoHide || _fullScreen;
    // The Dock's span along its edge (x at the bottom, y on a side), how far in from the edge it reaches when shown, the
    // edge it stands on and which one, in screen pixels (written on the UI thread, read on the ticker's).
    volatile int _spanFrom, _spanTo, _reach, _edge, _side;
    long _edgeSince = -1, _awaySince = -1;
    uint _lastInput;
    bool _fastLooks;
    FrameClock? _slideClock;
    double _slideFrom, _slideTo, _slideStart;
    readonly Stopwatch _slideWatch = Stopwatch.StartNew();

    /// <summary>The Dock is hidden away (auto-hide, a full-screen app), or on its way out of sight (diagnostics).</summary>
    public bool Hidden => Hiding && !_shown;
    public bool AutoHide => _autoHide;

    /// <summary>How far out the Dock goes to be out of sight (down, or aside on a side): its plate and the margin outside it.</summary>
    double HiddenSlide => _settings.Dock.IconSize + 2 * _settings.Dock.Padding + DockSurface.BottomMargin + 4;

    /// <summary>Turns auto-hide on (the Dock slides away) or off (it comes back and stays). Needs the ticker.</summary>
    public void SetAutoHide(bool on, BackgroundTicker? ticker = null)
    {
        _ticker ??= ticker;
        if (on == _autoHide) return;
        _autoHide = on;
        UpdateHiding(animate: true);
    }

    /// <summary>
    /// A full-screen app on the Dock's monitor came or went: out of sight at once, waiting at its edge; back as it was
    /// afterwards.
    /// </summary>
    public void SetFullScreen(bool on)
    {
        if (on == _fullScreen) return;
        _fullScreen = on;
        if (on)
        {
            _menu.Close();
            _slideClock?.Stop();
            _shown = false;
            Window.Slide = HiddenSlide;
            Window.Hide();
        }
        else Window.Show();
        UpdateHiding(animate: false);
    }

    void UpdateHiding(bool animate)
    {
        if (Hiding)
        {
            UpdateSpan();
            if (!_watching) { _ticker?.Add(AutoHideJob, TimeSpan.FromMilliseconds(100), Look); _watching = true; }
            if (_shown) SlideTo(hidden: true, animate);
        }
        else
        {
            if (_watching) { _ticker?.Remove(AutoHideJob); _watching = false; }
            SlideTo(hidden: false, animate);
        }
    }

    /// <summary>The Dock's span from its resting place (the slide aside), for the ticker.</summary>
    void UpdateSpan()
    {
        double s = _mon.Scale;
        var plate = Window.Surface.PlateRect;
        double magnified = (_settings.Dock.Magnification ? Math.Max(0, _settings.Dock.MagnifiedSize - _settings.Dock.IconSize) : 0) + 8;
        var at = Window.Resting; // the window when shown (DIPs)
        int edge = Window.EdgePx;
        int Px(double dip) => (int)Math.Round(dip * s);
        switch (Window.Edge)
        {
            case DockEdge.Left:
                (_spanFrom, _spanTo) = (Px(at.Y + plate.Top) - 8, Px(at.Y + plate.Bottom) + 8);
                _reach = Px(at.X + plate.Right + magnified) - edge + 1;
                break;
            case DockEdge.Right:
                (_spanFrom, _spanTo) = (Px(at.Y + plate.Top) - 8, Px(at.Y + plate.Bottom) + 8);
                _reach = edge - Px(at.X + plate.Left - magnified);
                break;
            default:
                (_spanFrom, _spanTo) = (Px(at.X + plate.Left) - 8, Px(at.X + plate.Right) + 8);
                _reach = edge - Px(at.Y + plate.Top - magnified);
                break;
        }
        _edge = edge;
        _side = (int)Window.Edge;
    }

    void Look()
    {
        if (!Hiding) return;
        uint input = Native.LastInputTick();
        bool shown = _shown;
        if (!_lookAgain && input == _lastInput && _edgeSince < 0 && _awaySince < 0)
        {
            // Nothing moved, nothing pending: no need to look often until input comes (until 2026-10-15 a Dock that had
            // just slid away went on looking 62 times a second until the next input).
            if (_fastLooks) { _fastLooks = false; _ticker?.Retime(AutoHideJob, TimeSpan.FromMilliseconds(100)); }
            return;
        }
        _lookAgain = false;
        _lastInput = input;
        if (!Native.GetCursorPos(out var p)) return;
        long now = Environment.TickCount64;
        // Along the edge, and in from it (its outermost row or column of pixels is 1; past it, over a taskbar, 0 or less).
        var (along, inward) = (DockEdge)_side switch
        {
            DockEdge.Left => (p.Y, p.X - _edge + 1),
            DockEdge.Right => (p.Y, _edge - p.X),
            _ => (p.X, _edge - p.Y),
        };
        bool inSpan = along >= _spanFrom && along < _spanTo;
        bool atEdge = inSpan && inward <= 2;
        if (!shown)
        {
            if (!atEdge) _edgeSince = -1;
            else if (_edgeSince < 0) _edgeSince = now;
            else if (now - _edgeSince >= RevealMs && (!_fullScreen || Native.PointerFree(_mon.Bounds)))
            {
                _edgeSince = -1;
                _shown = true;
                _ui.BeginInvoke(() => SlideTo(hidden: false));
            }
        }
        else
        {
            bool over = inSpan && inward <= _reach;
            if (over || _menuOpen) _awaySince = -1;
            else if (_awaySince < 0) _awaySince = now;
            else if (now - _awaySince >= HideMs) { _awaySince = -1; _shown = false; _ui.BeginInvoke(() => { if (!_menu.IsOpen) SlideTo(hidden: true); else _shown = true; }); }
        }
        // Looks often while it matters (at the edge, or shown), rarely otherwise.
        bool fast = shown || inward <= 64;
        if (fast != _fastLooks) { _fastLooks = fast; _ticker?.Retime(AutoHideJob, TimeSpan.FromMilliseconds(fast ? 16 : 100)); }
    }

    /// <summary>
    /// Where a window minimizes to and restores from: its app's icon in the Dock (pixels). Windows' own minimize and
    /// restore animations then shrink the window into the icon and grow it out of it (macOS's scale effect).
    /// </summary>
    RECT? MinimizeTargetOf(IntPtr hwnd)
    {
        long h = hwnd.ToInt64();
        Rect? inside = null;
        // Shown as its own picture: it comes back from there (stage 7-2).
        int own = IndexWhere(x => x.Kind == DockItemKind.MinimizedWindow && x.Windows.Any(w => w.Hwnd == h));
        if (own >= 0) inside = Window.Surface.SlotRect(own);
        else
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Kind != DockItemKind.App || _items[i].Windows.FirstOrDefault(w => w.Hwnd == h) is not { } w) continue;
                // Being minimized into a picture: where that picture will stand once it is in the row, before the trash.
                inside = ShowsAsPicture(w) ? Window.Surface.SlotRectIfAdded(MinimizedInsertAt) : Window.Surface.SlotRect(i);
                break;
            }
        if (inside is not { } at) return null;
        var r = Window.ToScreen(at);
        if (r.IsEmpty) return null;
        double s = _mon.Scale;
        return new RECT((int)Math.Round(r.Left * s), (int)Math.Round(r.Top * s), (int)Math.Round(r.Right * s), (int)Math.Round(r.Bottom * s));
    }

    /// <summary>A window minimized now would show as a picture of itself (not go into its app's icon).</summary>
    bool ShowsAsPicture(RunningWindow w) => !_settings.Dock.MinimizeIntoIcon && !w.Elsewhere && (MayShowWindow?.Invoke(w) ?? true);

    /// <summary>Where a newly minimized window's picture comes in: last among the pictures, before the trash.</summary>
    int MinimizedInsertAt => IndexWhere(x => x.Kind == DockItemKind.Trash) is var t and >= 0 ? t : _items.Count;

    int IndexWhere(Func<DockItem, bool> match)
    {
        for (int i = 0; i < _items.Count; i++) if (match(_items[i])) return i;
        return -1;
    }

    /// <summary>Diagnostics: the last minimize rectangle Windows asked for: window, as given, as answered, ms ago.</summary>
    public string MinRectState()
    {
        var (h, given, answered, at) = _windows.LastMinRect;
        static string R(RECT r) => $"{r.Left},{r.Top},{r.Right},{r.Bottom}";
        return h == IntPtr.Zero ? "none" : $"{h.ToInt64()} {R(given)} {(answered is { } a ? R(a) : "-")} {Environment.TickCount64 - at}";
    }

    /// <summary>
    /// Diagnostics (cheap): auto-hide on, shown, the slide (DIPs), the plate's inner side on the screen, the edge it
    /// stands on, the span the ticker watches (along the edge, then its inner limit), whether it is sliding, over a
    /// full-screen app, the window shown, and the edge's name. Pixels, across the edge: y at the bottom (the plate's
    /// top), x on a side (the plate's right on the left, its left on the right).
    /// </summary>
    public string AutoHideState()
    {
        var plate = Window.Surface.PlateRect;
        double s = _mon.Scale;
        var edge = Window.Edge;
        int plateInner = (int)Math.Round(edge switch
        {
            DockEdge.Left => (Window.Left + plate.Right) * s,
            DockEdge.Right => (Window.Left + plate.Left) * s,
            _ => (Window.Top + plate.Top) * s,
        });
        int limit = edge == DockEdge.Left ? _edge + _reach - 1 : _edge - _reach;
        string slide = Window.Slide.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        // Then what the ticker last judged by: its menu open, the input tick it last looked at and the present one, and
        // how long the pointer has been away (ms; -1: not counting).
        long away = _awaySince < 0 ? -1 : Environment.TickCount64 - _awaySince;
        return $"{(_autoHide ? 1 : 0)} {(_shown ? 1 : 0)} {slide} {plateInner} {_edge} {_spanFrom} {_spanTo} {limit} {(_slideClock?.Running == true ? 1 : 0)} {(_fullScreen ? 1 : 0)} {(Window.IsVisible ? 1 : 0)} {DockFrame.Name(edge)} " +
               $"{(_menuOpen ? 1 : 0)} {_lastInput} {Native.LastInputTick()} {away}";
    }

    void SlideTo(bool hidden, bool animate = true)
    {
        _shown = !hidden;
        double target = hidden ? HiddenSlide : 0;
        // Over a full-screen app the window is shown only while it is up or on its way (see SetFullScreen).
        if (!hidden && _fullScreen && !Window.IsVisible)
        {
            Window.Slide = HiddenSlide;
            Window.Show();
        }
        if (!animate || !_theme.Current.Animations)
        {
            _slideClock?.Stop();
            Window.Slide = target;
            Settled();
            return;
        }
        _slideFrom = Window.Slide;
        _slideTo = target;
        _slideStart = _slideWatch.Elapsed.TotalSeconds;
        if (_slideClock == null)
        {
            _slideClock = new FrameClock(_ui);
            _slideClock.Tick += _ =>
            {
                double t = Math.Min(1, (_slideWatch.Elapsed.TotalSeconds - _slideStart) / SlideSeconds);
                // Coming up it eases out (there quickly, settling); going away it eases in and out, so it does not crawl
                // the last pixels out of sight as an ease out did (2026-10-17).
                double k = _slideTo < _slideFrom ? Motion.Out(t) : Motion.InOut(t);
                Window.Slide = _slideFrom + (_slideTo - _slideFrom) * k;
                if (t >= 1) { _slideClock!.Stop(); Settled(); }
            };
        }
        _slideClock.Start();
    }

    /// <summary>Out of sight over a full-screen app: the window goes too. Gone into its edge for good: what follows.</summary>
    void Settled()
    {
        if (_fullScreen && !_shown && Window.IsVisible) Window.Hide();
        if (!_shown && _goneAfter is { } gone) { _goneAfter = null; gone(); }
    }

    Action? _goneAfter;

    /// <summary>
    /// Another edge was chosen (stage 7): this Dock goes into its edge as it does to hide, then <paramref name="done"/>
    /// (the Dock made for the new edge comes out of that one: <see cref="WaitOutOfSight"/>, <see cref="ComeIn"/>). It
    /// was gone and the new one there in a frame (2026-10-19, the user's "B14"). It no longer follows the pointer nor
    /// takes clicks meanwhile; its pointer watch (a job of the shell's ticker, named as the new Dock's) is let go first.
    /// </summary>
    public void GoAway(Action done)
    {
        if (_watching) { _ticker?.Remove(AutoHideJob); _watching = false; }
        _ticker = null;
        _menu.Close();
        Window.IsHitTestVisible = false;
        if (!_theme.Current.Animations || !Window.IsVisible || Hiding && !_shown) { done(); return; }
        _goneAfter = done;
        SlideTo(hidden: true);
    }

    /// <summary>Made for a newly chosen edge: out of sight there before it shows (unless it hides anyway or animations are off).</summary>
    public void WaitOutOfSight()
    {
        if (!Hiding && _theme.Current.Animations) Window.Slide = HiddenSlide;
    }

    /// <summary>Out of its edge, as it shows from hiding (after <see cref="WaitOutOfSight"/>).</summary>
    public void ComeIn()
    {
        if (Hiding || Window.Slide == 0) return;
        CameIn++;
        SlideTo(hidden: false);
    }

    /// <summary>Docks that came out of a newly chosen edge (diagnostics).</summary>
    public static int CameIn { get; private set; }

    readonly int _iconPx;
    static int IconPxFor(DockSettings d, MonitorInfo mon) => (int)Math.Ceiling(Math.Max(d.IconSize, d.Magnification ? d.MagnifiedSize : 0) * mon.Scale);

    /// <summary>
    /// The settings' sizes, magnification and indicators, at once (the settings window's sliders move it live). Icons
    /// were read for the sizes at start: larger ones stretch them until the Dock is made again
    /// (<see cref="NeedsLargerIcons"/>).
    /// </summary>
    public void ApplySettings()
    {
        var d = _settings.Dock;
        var s = Window.Surface;
        s.Spacing = d.Spacing;
        s.PlatePad = d.Padding;
        s.Magnify = d.Magnification;
        s.ShowDots = d.ShowIndicators;
        _placedLength = 0;
        _widestKey = null;
        FitSizes(); // the sizes the row fits at first, then one restyle (each move of a slider made the pictures twice)
        s.Restyle();
        // The recent section came or went; minimized windows go into their apps' icons or come out as pictures.
        if (d.ShowRecent != _showingRecent || d.MinimizeIntoIcon != _intoIcon)
        {
            _showingRecent = d.ShowRecent;
            _intoIcon = d.MinimizeIntoIcon;
            Rebuild();
        }
        PlaceWindow();
        if (Hiding && !_shown) Window.Slide = HiddenSlide; // a new size: still out of sight
    }

    bool _showingRecent, _intoIcon;

    public bool NeedsLargerIcons => IconPxFor(_settings.Dock, _mon) > _iconPx;

    public void MoveTo(MonitorInfo mon)
    {
        _mon = mon;
        _placedLength = 0;
        PlaceWindow();
    }

    /// <summary>
    /// The outer side of the Dock's reserved strip moved, or the work area did (pixels: a y at the bottom, an x on a
    /// side). The monitor's work area is read again: a side's row is centred on its height.
    /// </summary>
    public void StandOn(int edgePx)
    {
        _mon = Monitors.Get(_mon.Handle) ?? _mon;
        _placedLength = 0;
        _edgePx = edgePx;
        PlaceWindow();
    }

    int? _edgePx;

    /// <summary>The kept apps, the apps used lately and the folders read again from the settings (the welcome changed them).</summary>
    public void Reload()
    {
        ResolvePins();
        ResolveRecent();
        _showingRecent = _settings.Dock.ShowRecent;
        Rebuild();
    }

    /// <summary>The Start menu's names arrived: read windows and pins again with them.</summary>
    public void CatalogLoaded()
    {
        ResolvePins();
        ResolveRecent();
        _windows.Refresh();
        Rebuild();
    }

    // ---- pins ------------------------------------------------------------------------------------------------

    void ImportTaskbarPinsOnce()
    {
        var d = _settings.Dock;
        if (d.PinsImported) return;
        foreach (var pin in TaskbarPins.Read())
        {
            if (d.Pins.Any(p => string.Equals(p.Id, pin.Id, StringComparison.OrdinalIgnoreCase))) continue;
            d.Pins.Add(pin.IsShortcut
                ? new DockPin { Kind = "path", Id = pin.Id, Name = Path.GetFileNameWithoutExtension(pin.Id) }
                : new DockPin { Kind = "appsFolder", Id = pin.Id }); // named from the Start menu's list once it has loaded
        }
        d.PinsImported = true;
        Save();
        Log.Info($"dock: imported {d.Pins.Count} pins from the taskbar");
    }

    void ResolvePins()
    {
        var took = Stopwatch.StartNew();
        long apps = 0, paths = 0;
        int named = 0, unnamed = 0, files = 0;
        var pins = new List<PinnedApp>();
        foreach (var p in _settings.Dock.Pins)
        {
            long before = took.ElapsedTicks;
            if (Resolve(p, DockModel.PinPrefix) is { } app) pins.Add(app);
            long ticks = took.ElapsedTicks - before;
            if (p.Kind == "appsFolder") { apps += ticks; if (p.Name == null) unnamed++; else named++; }
            else if (p.Kind != DockPin.SeparatorKind) { paths += ticks; files++; }
        }
        _pins = pins;
        Log.Debug($"dock: {pins.Count} pins resolved in {took.ElapsedMilliseconds} ms (AppsFolder {named + unnamed}: " +
                  $"{apps * 1000 / Stopwatch.Frequency} ms, {unnamed} without a name; shortcuts and programs {files}: {paths * 1000 / Stopwatch.Frequency} ms)");
    }

    // ---- folders (macOS's stacks) ------------------------------------------------------------------------------

    /// <summary>The Downloads folder goes in the Dock once, before the trash (as macOS has it); removed, it stays out.</summary>
    void AddDownloadsOnce()
    {
        var d = _settings.Dock;
        if (d.DownloadsAdded) return;
        d.DownloadsAdded = true;
        if (Native.KnownFolderPath(ShellFiles.Downloads) is { } downloads && Directory.Exists(downloads)
            && !d.Folders.Any(f => string.Equals(f, downloads, StringComparison.OrdinalIgnoreCase)))
            d.Folders.Add(downloads);
        Save();
    }

    readonly Dictionary<string, string> _folderNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folders kept in the Dock that exist, with the names File Explorer shows ("ダウンロード").</summary>
    IReadOnlyList<(string Path, string Name)> Folders() => _settings.Dock.Folders
        .Where(p => ShellFiles.IsLocal(p) && Directory.Exists(p))
        .Select(p => (p, _folderNames.TryGetValue(p, out var n) ? n : _folderNames[p] = ShellFiles.DisplayName(p)))
        .ToList();

    /// <summary>The icon keys of files shown in stacks (the IconService keeps every icon it has read).</summary>
    const string StackIcon = "file:";
    const int StackIconsKept = 180;
    readonly LinkedList<string> _stackIcons = new(); // the most recently shown last

    /// <summary>The Dock's monitor in DIPs (its menus and stacks stay on it).</summary>
    Rect ScreenDip => new(_mon.Bounds.Left / _mon.Scale, _mon.Bounds.Top / _mon.Scale, _mon.Bounds.Width / _mon.Scale, _mon.Bounds.Height / _mon.Scale);

    /// <summary>
    /// A folder's stack: its newest items in a grid above its icon, beside it on a side (a click outside, on the icon
    /// too, closes it).
    /// </summary>
    void OpenStack(int index)
    {
        var item = _items[index];
        if (item.LaunchTarget is not { } folder) return;
        var t = _theme.Current;
        var s = _strings();
        var stack = new FolderStackPopup(t, Materials.Menu(t, _settings.Appearance, (float)(GlassShape.Stack * _mon.Scale)), s, folder, item.Name,
            path => _icons.Get(StackIcon + path, path),
            path => { _menu.Close(); if (GuardedOpen(path)) return; if (Directory.Exists(path)) FileManager.OpenFolder(path); else if (!Launching(path, "stack")) CommandRunner.Launch(path); },
            () => { _menu.Close(); if (GuardedOpen(folder)) return; FileManager.OpenFolder(folder); });
        KeepStackIcons(stack.Cells.Select(c => StackIcon + c.Path));
        _menu.ShowPanelBeside(stack, Window.Edge, Window.ToScreen(Window.Surface.SlotRect(index)), ScreenDip);
        _menuOpen = _menu.IsOpen; // auto-hide: the Dock stays while the stack is open
        Window.Surface.Hold = _menu.IsOpen; // and magnified as it was, under the stack
        Log.Debug($"dock: stack of {Log.Redact(folder)} opened ({stack.Cells.Count} items)");
    }

    /// <summary>Keeps the icons of the files shown last; older ones are forgotten (read again from the disk cache).</summary>
    void KeepStackIcons(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            _stackIcons.Remove(key);
            _stackIcons.AddLast(key);
        }
        while (_stackIcons.Count > StackIconsKept)
        {
            _icons.Forget(_stackIcons.First!.Value);
            _stackIcons.RemoveFirst();
        }
    }

    /// <summary>
    /// Diagnostics: the open stack (pixels), its items (file names, newest first) and where they are, and how many of
    /// their icons have arrived.
    /// </summary>
    public object? StackState()
    {
        if (_menu.Panel is not FolderStackPopup p || !p.IsVisible) return null;
        double k = _mon.Scale;
        Native.GetWindowRect(p.Handle, out var r);
        return new
        {
            rect = new { r.Left, r.Top, r.Right, r.Bottom },
            hwnd = p.Handle.ToInt64(), // a spec follows it as it closes (it sinks back into the Dock: A6, 2026-10-19)
            rising = p.Rising, // still coming out of the Dock (2026-10-17)
            icons = p.IconsShown,
            items = p.Cells.Select(c =>
            {
                var at = c.Cell.PointToScreen(new Point(0, 0));
                return new { name = Path.GetFileName(c.Path), rect = new { Left = (int)at.X, Top = (int)at.Y, Right = (int)(at.X + c.Cell.ActualWidth * k), Bottom = (int)(at.Y + c.Cell.ActualHeight * k) } };
            }).ToList(),
        };
    }

    void AddFolders(IEnumerable<string> folders)
    {
        bool changed = false;
        foreach (var f in folders)
        {
            if (!ShellFiles.IsLocal(f)) { Log.Info($"dock: not keeping {Log.Redact(f)}: not on a disk of this PC"); continue; }
            if (!Directory.Exists(f) || _settings.Dock.Folders.Any(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase))) continue;
            _settings.Dock.Folders.Add(f);
            Log.Info($"dock: kept the folder {Log.Redact(f)}");
            changed = true;
        }
        if (!changed) return;
        Save();
        Rebuild();
    }

    /// <summary>Keeps a folder in the Dock (diagnostics: a sample folder for screenshots).</summary>
    public void KeepFolder(string path) => AddFolders([path]);

    /// <summary>Takes a folder out of the Dock (its menu, a drag off it; diagnostics).</summary>
    public void RemoveFolder(string path)
    {
        if (_settings.Dock.Folders.RemoveAll(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase)) == 0) return;
        Save();
        Rebuild();
    }

    void RemoveFolder(DockItem item)
    {
        if (item.LaunchTarget is { } path) RemoveFolder(path);
    }

    /// <summary>The apps used lately as the Dock shows them (macOS's recent applications).</summary>
    void ResolveRecent() => _recent = _settings.Dock.Recent.Select(p => Resolve(p, DockModel.RecentPrefix)).OfType<PinnedApp>().ToList();

    IReadOnlyList<PinnedApp> _recent = [];

    /// <summary>A kept (or remembered) app with its AppID; null when its file is gone.</summary>
    PinnedApp? Resolve(DockPin p, string prefix)
    {
        if (p.Kind == DockPin.SeparatorKind) return new PinnedApp(prefix + p.Id, "", _strings()["dock.separator"], "", Separator: true);
        if (p.Kind == "appsFolder") return new PinnedApp(prefix + p.Id, p.Id, p.Name ?? _identity.DisplayName(p.Id, null), @"shell:AppsFolder\" + p.Id);
        if (!File.Exists(p.Id)) { Log.Debug($"dock: file missing: {Log.Redact(p.Id)}"); return null; }
        // The taskbar's own AppID for a shortcut (ADR-005), so the app's windows land on its pin: kept from the last
        // start while the file is unchanged (asking took about 6.5 ms a pin), asked again a while after start-up.
        long written = File.GetLastWriteTimeUtc(p.Id).Ticks;
        string appId = _shortcutIds.Get(p.Id, written) ?? AskAppId(p.Id, written);
        return new PinnedApp(prefix + p.Id, appId, p.Name ?? Path.GetFileNameWithoutExtension(p.Id), p.Id);
    }

    /// <summary>Explorer's resolver's AppID for a kept file (the path itself when it has none), kept for the next start.</summary>
    string AskAppId(string path, long written)
    {
        string appId = _identity.AppIdForShortcut(path) ?? path;
        _shortcutIds.Set(path, written, appId);
        return appId;
    }

    /// <summary>
    /// Apps not kept in the Dock whose last window has gone (<paramref name="before"/>: the items before, against the
    /// windows now) are remembered as used lately, shown or not (macOS's recent applications).
    /// </summary>
    void NoteQuits(IReadOnlyList<DockItem> before, IReadOnlyList<RunningWindow> windows)
    {
        bool changed = false;
        foreach (var gone in before.Where(i => i.Kind == DockItemKind.App && !i.Pinned && i.Running))
        {
            if (windows.Any(w => string.Equals(w.AppId, gone.AppId, StringComparison.OrdinalIgnoreCase))) continue;
            if (PinFor(gone) is not { } app) continue;
            if (_settings.Dock.Pins.Any(p => string.Equals(p.Id, app.Id, StringComparison.OrdinalIgnoreCase))) continue;
            DockModel.NoteRecent(_settings.Dock.Recent, app);
            changed = true;
        }
        if (!changed) return;
        ResolveRecent();
        Save();
    }

    /// <summary>Takes an app out of the ones used lately (its menu's "Remove from Dock", or dragged off).</summary>
    void Forget(DockItem item)
    {
        string appId = item.AppId;
        int removed = _settings.Dock.Recent.RemoveAll(p => Resolve(p, "") is { } r && string.Equals(r.AppId, appId, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return;
        Save();
        ResolveRecent();
        Rebuild();
    }

    void Save()
    {
        try { _saver.Save(_settings); } // serialised here, written off the UI thread
        catch (Exception ex) { Log.Warn("dock: could not save the pins", ex); }
        if (_shortcutIds.Changed) SaveShortcutIds(); // a pin or a recent app added: its AppID kept with it
    }

    /// <summary>A pin for a running app: its Start menu entry when it has one, else its program.</summary>
    DockPin? PinFor(DockItem item)
    {
        // A running app's program; one used lately: what it is started with (not an AppsFolder name).
        var exe = item.Windows.FirstOrDefault()?.ExePath
                  ?? (item.LaunchTarget is { } t && !t.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? t : null);
        return _identity.IsCatalogEntry(item.AppId)
            ? new DockPin { Kind = "appsFolder", Id = item.AppId, Name = item.Name }
            : exe != null ? new DockPin { Kind = "path", Id = exe, Name = item.Name } : null;
    }

    void Pin(DockItem item)
    {
        if (PinFor(item) is not { } pin) return;
        // Already kept under another identity (an app that sets its own AppID): one pin per file.
        if (_settings.Dock.Pins.Any(p => string.Equals(p.Id, pin.Id, StringComparison.OrdinalIgnoreCase))) return;
        _settings.Dock.Pins.Add(pin);
        Save();
        ResolvePins();
        Rebuild();
    }

    /// <summary>
    /// A dragged item was let go in the row with <paramref name="before"/> other items left of it: a kept app or a
    /// separator moves there; a running app dropped among the kept ones is kept in the Dock there (macOS), elsewhere it
    /// stays where it was.
    /// </summary>
    void OnItemMoved(int index, int before)
    {
        if (index < 0 || index >= _items.Count) return;
        var dragged = _items[index];
        if (!dragged.Movable) return;
        int position = DockModel.KeptBefore(_items, index, before);
        var pins = _settings.Dock.Pins;
        if (dragged.Pinned)
        {
            if (!DockModel.MovePin(pins, _items, index, before)) return; // let go where it was
            Log.Info($"dock: moved {dragged.Name} to place {position + 1} of the kept items");
        }
        else
        {
            var others = _items.Where((_, i) => i != index).ToList();
            if (before > others.FindLastIndex(x => x.Pinned) + 1) return; // among the running apps: no change
            if (PinFor(dragged) is not { } pin) return;
            // Already kept under another identity (as Pin): one pin per file. Two items with the same key mixed up their
            // presses, drags and bounces, and taking one off took both.
            if (pins.Any(p => string.Equals(p.Id, pin.Id, StringComparison.OrdinalIgnoreCase))) return;
            pins.Insert(Math.Min(DockModel.PinIndexFor(pins, _items, position, null), pins.Count), pin);
            Log.Info($"dock: kept {dragged.Name} at place {position + 1}");
        }
        Save();
        ResolvePins();
        Rebuild();
    }

    /// <summary>A separator kept right of a kept app (its menu): dragged, it moves like the apps; dragged off, it goes.</summary>
    void AddSeparatorAfter(DockItem item)
    {
        var pins = _settings.Dock.Pins;
        int at = pins.FindIndex(p => DockModel.PinPrefix + p.Id == item.Key);
        if (at < 0) return;
        pins.Insert(at + 1, DockPin.NewSeparator());
        Log.Info($"dock: a separator after {item.Name}");
        Save();
        ResolvePins();
        Rebuild();
    }

    /// <summary>A dragged app was let go away from the Dock: a pin is removed (a running app just goes back).</summary>
    void OnDraggedOut(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        var item = _items[index];
        bool recent = item.Key.StartsWith(DockModel.RecentPrefix, StringComparison.Ordinal), folder = item.Kind == DockItemKind.Folder;
        if (!item.Pinned && !recent && !folder) return;
        Log.Info($"dock: removed {item.Name} (dragged off)");
        // A puff of smoke where it was let go (macOS).
        if (_theme.Current.Animations && Native.GetCursorPos(out var p)) Poof.At(new Point(p.X / _mon.Scale, p.Y / _mon.Scale), _theme.Current.Dark);
        if (folder) RemoveFolder(item);
        else if (recent) Forget(item);
        else Unpin(item);
    }

    /// <summary>
    /// Files from another app: onto an app (opened with it), onto a folder (copied into it), onto the trash (moved to the
    /// Recycle Bin), programs and shortcuts between apps (kept in the Dock there, <paramref name="before"/> items of the
    /// row left of them), or folders elsewhere (kept as stacks).
    /// </summary>
    void OnFilesDropped(int onto, int before, string[] files)
    {
        if (onto >= 0 && onto < _items.Count)
        {
            var target = _items[onto];
            if (target.Kind == DockItemKind.Trash)
            {
                // On a thread of its own, as the copy below: a large folder held the Dock and the menu bar up meanwhile
                // (and the shell's question about what cannot be recycled waits for an answer).
                var bin = new Thread(() =>
                {
                    if (RecycleBin.MoveToBin(files) is { } error) Log.Warn("dock: moving to the Recycle Bin failed: " + error);
                    _ui.BeginInvoke(() => { if (!_disposed) _bin.Refresh(); });
                }) { Name = "move-to-bin" };
                bin.SetApartmentState(ApartmentState.STA);
                bin.Start();
            }
            else if (target.Kind == DockItemKind.Folder && target.LaunchTarget is { } folder)
            {
                if (MayStack != null && !MayStack(folder)) { Guarded++; Log.Info("dock: a drop into a folder not the spec's own: guarded"); return; }
                // Not into itself or where it already is (the shell would ask to replace each file with itself).
                var copied = files.Where(f => !string.Equals(Path.GetDirectoryName(f.TrimEnd('\\')), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                                              && !string.Equals(f.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();
                if (copied.Count == 0) return;
                Log.Info($"dock: copying {copied.Count} item(s) into {Log.Redact(folder)}");
                // On a thread of its own with the shell's progress window: a large copy must not hold the Dock up.
                // A foreground thread, so that a copy under way finishes even if the shell quits meanwhile.
                var copy = new Thread(() =>
                {
                    if (ShellFiles.CopyInto(copied, folder) is { } error) Log.Warn("dock: copying into the folder failed: " + error);
                }) { Name = "copy-into-folder" };
                copy.SetApartmentState(ApartmentState.STA);
                copy.Start();
            }
            else if (target.Kind == DockItemKind.App && !Refused(target, "drop")) OpenFiles(target, files);
            return;
        }
        if (before < 0)
        {
            AddFolders(files); // folders let go on the plate (the Dock asks for them only when every one is a folder)
            return;
        }
        var pins = _settings.Dock.Pins;
        int pinPosition = DockModel.KeptBefore(_items, -1, before);
        bool changed = false;
        foreach (var file in files.Where(f => Path.GetExtension(f).ToLowerInvariant() is ".exe" or ".lnk" && File.Exists(f)))
        {
            if (pins.Any(p => string.Equals(p.Id, file, StringComparison.OrdinalIgnoreCase))) continue;
            pins.Insert(Math.Min(DockModel.PinIndexFor(pins, _items, pinPosition, null), pins.Count), new DockPin { Kind = "path", Id = file, Name = Path.GetFileNameWithoutExtension(file) });
            Log.Info($"dock: kept {Path.GetFileName(file)} (dropped) at place {pinPosition + 1}");
            pinPosition++;
            changed = true;
            ResolvePins();
            Rebuild();
        }
        if (changed) Save();
    }

    // ---- apps carried in from the app grid --------------------------------------------------------------------
    // The grid keeps the pointer while an app is carried out of it (Launchpad) and says where it is; over the Dock a gap
    // opens among the kept apps, and let go there the app is kept (one that is kept already moves there instead).

    /// <summary>A screen point (pixels) on the Dock's surface (DIPs), or null while the Dock is not shown.</summary>
    Point? OnSurface(int x, int y) =>
        Window.IsVisible && PresentationSource.FromVisual(Window.Surface) != null ? Window.Surface.PointFromScreen(new Point(x, y)) : null;

    /// <summary>The kept item of an app (the grid names apps by their AppID), or -1.</summary>
    int KeptItemOf(string appId)
    {
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].Kind == DockItemKind.App && _items[i].Pinned && string.Equals(_items[i].AppId, appId, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    bool AppGrid.IAppShelf.Carry(string appId, int x, int y) =>
        Window.Surface.CarryApp(OnSurface(x, y), KeptItemOf(appId) is var k and >= 0 ? _items[k].Key : null);

    void AppGrid.IAppShelf.CarryAway() => Window.Surface.CarryApp(null, null);

    bool AppGrid.IAppShelf.Covers(int x, int y) => OnSurface(x, y) is Point p && Window.Surface.Covers(p);

    bool AppGrid.IAppShelf.Drop(string appId, string name, int x, int y, int pictureX, int pictureY, double sizePx)
    {
        int kept = KeptItemOf(appId);
        var s = Window.Surface;
        s.CarryApp(OnSurface(x, y), kept >= 0 ? _items[kept].Key : null); // where it was let go
        int before = s.DropApp();
        if (before < 0) return false;
        var app = new DockPin { Kind = "appsFolder", Id = appId, Name = name };
        bool moved = DockModel.KeepCarried(_settings.Dock.Pins, _items, app, kept, before);
        if (!moved && kept < 0) return false;
        if (moved)
        {
            Log.Info($"dock: {(kept >= 0 ? "moved" : "kept")} {Log.Redact(name)} (carried from the app grid) at place {DockModel.KeptBefore(_items, kept, before) + 1}");
            Save();
            ResolvePins();
            Rebuild();
        }
        // Its picture flies from where it was let go into its place (kept there now, or back where it was kept).
        if (KeptItemOf(appId) is var now and >= 0) s.FlyIn(_items[now].Key, pictureX, pictureY, sizePx);
        return true;
    }

    bool AppGrid.IAppShelf.Keeps(string appId) => KeptItemOf(appId) >= 0;

    void AppGrid.IAppShelf.Keep(string appId, string name)
    {
        if (KeptItemOf(appId) >= 0 || _settings.Dock.Pins.Any(p => string.Equals(p.Id, appId, StringComparison.OrdinalIgnoreCase))) return;
        _settings.Dock.Pins.Add(new DockPin { Kind = "appsFolder", Id = appId, Name = name });
        var took = Stopwatch.StartNew();
        Save();
        long saved = took.ElapsedMilliseconds;
        ResolvePins();
        Rebuild();
        Log.Info($"dock: kept {Log.Redact(name)} (the app grid's menu): handed to be saved in {saved} ms, in the Dock {took.ElapsedMilliseconds} ms");
    }

    void AppGrid.IAppShelf.Remove(string appId)
    {
        if (KeptItemOf(appId) is not (>= 0 and var k)) return;
        Log.Info($"dock: removed {Log.Redact(_items[k].Name)} (the app grid's menu)");
        Unpin(_items[k]);
    }

    /// <summary>Opens files with an app: a packaged app through the activation manager, else its program with the files.</summary>
    void OpenFiles(DockItem item, string[] files)
    {
        string? target = item.LaunchTarget ?? item.Windows.FirstOrDefault()?.ExePath;
        if (target == null) return;
        Log.Info($"dock: opening {files.Length} file(s) with {item.Name}");
        const string apps = @"shell:AppsFolder\";
        if (target.StartsWith(apps, StringComparison.OrdinalIgnoreCase))
        {
            Native.AllowSetForegroundWindow(-1);
            if (OpenWith.Packaged(target[apps.Length..], files) is { } error) Log.Warn($"dock: opening with {item.Name} failed: {error}");
        }
        else if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && OpenWith.Resolve(target) is { } link)
            CommandRunner.Launch(link.path, (link.args + " " + OpenWith.Quote(files)).Trim());
        else CommandRunner.Launch(target, OpenWith.Quote(files));
        if (!item.Running && _settings.Dock.BounceOnLaunch) Bounce(item, ProgramOf(item));
    }

    /// <summary>
    /// An item bouncing while its app starts, until a window of its own comes, or one of the program started
    /// (<paramref name="program"/>): a program opened by its Start menu shortcut can show its window under the
    /// shortcut's AppID, not its pin's, and the pin bounced its full 10 s (2026-10-23, launch-bounce).
    /// </summary>
    /// <param name="bounce">False: watched as a launch (its window, its 10 s) without bouncing.</param>
    void Bounce(DockItem item, string? program, bool bounce = true)
    {
        int ofProgram = program == null ? 0 : _windows.Windows.Count(w => LaunchMatch.SameFile(w.ExePath, program));
        _launching[item.AppId] = new LaunchUnderWay
        {
            Since = Environment.TickCount64, Windows = item.Windows.Count, Key = item.Key, Program = program, OfProgram = ofProgram,
            FrontBefore = _front.Current?.Hwnd ?? IntPtr.Zero,
        };
        if (bounce) Window.Surface.StartBounce(item.Key, 0);
        _launchTimeout.Start();
    }

    void Unpin(DockItem item)
    {
        _settings.Dock.Pins.RemoveAll(p => DockModel.PinPrefix + p.Id == item.Key);
        Save();
        ResolvePins();
        Rebuild();
    }

    // ---- model -----------------------------------------------------------------------------------------------

    void Rebuild()
    {
        if (_disposed) return; // a rebuild put off (a launch's end, a posted one) after the Dock was made again: its window is closed
        var s = _strings();
        var windows = _windows.Windows.Select(w => new RunningWindow(w.Hwnd.ToInt64(), w.AppId, w.AppName, w.ExePath, w.Minimized, w.Elsewhere))
            .Where(w => MayListWindow?.Invoke(w) ?? true).ToList();
        NoteQuits(_items, windows);
        // Minimized windows as pictures of themselves before the trash, named by their titles (macOS with "Minimize
        // windows into application icon" off).
        IReadOnlyList<RunningWindow> minimized = _settings.Dock.MinimizeIntoIcon ? [] : DockModel.MinimizedInOrder(windows, _minimizedOrder, MayShowWindow);
        SettleStarting(windows);
        _items = DockModel.Build(_pins, windows, _front.Current?.AppId, _runningOrder, s["dock.appGrid"], s["dock.trash"],
            _settings.Dock.ShowRecent ? _recent : null, Folders(), [.. minimized.Select(w => (w, Native.Title(new IntPtr(w.Hwnd))))],
            [.. _starting.Values], _stayRecent);

        var front = _front.Current;
        foreach (var item in _items)
        {
            if (!_launching.TryGetValue(item.AppId, out var l)) continue;
            if (item.Windows.Count > l.Windows || l.Program != null && windows.Count(w => LaunchMatch.SameFile(w.ExePath, l.Program)) > l.OfProgram)
            {
                _launching.Remove(item.AppId);
                if (!l.Landed) Window.Surface.StopBounce(item.Key);
                continue;
            }
            // In front before the Dock lists its window (macOS stops the bounce as the app comes up): an app ended as
            // soon as it showed a window bounced its full 10 s, and its item then went from under the next click
            // (2026-10-23, grid-launch: the click opened the stack beside it).
            if (!l.Landed && front != null && front.Hwnd != l.FrontBefore
                && (string.Equals(front.AppId, item.AppId, StringComparison.OrdinalIgnoreCase) || l.Program != null && LaunchMatch.SameFile(front.ExePath, l.Program)))
            {
                l.Landed = true;
                l.FrontWindow = front.Hwnd;
                Window.Surface.StopBounce(item.Key);
            }
            // That window gone and none listed: the app ended; an item of its own while it started goes at once.
            else if (l.Landed && item.Windows.Count == 0 && !Native.IsWindow(l.FrontWindow))
            {
                _launching.Remove(item.AppId);
                if (_starting.Remove(item.AppId)) _ui.BeginInvoke(DispatcherPriority.Background, new Action(Rebuild));
            }
        }

        _iconOf.Clear();
        foreach (var item in _items)
        {
            string? source = item.Kind switch
            {
                DockItemKind.Trash => "stock:" + (_bin.Full ? RecycleBin.StockFull : RecycleBin.StockEmpty),
                DockItemKind.Folder => item.LaunchTarget, // the folder's own icon (Downloads has its own)
                DockItemKind.App when item.Pinned || !item.Running && item.LaunchTarget != null => item.LaunchTarget, // kept, or used lately
                DockItemKind.App => _identity.IconSource(item.AppId, item.Windows.FirstOrDefault()?.ExePath),
                DockItemKind.MinimizedWindow => _identity.IconSource(item.AppId, item.Windows.FirstOrDefault()?.ExePath), // the small icon on its picture
                _ => null,
            };
            if (source != null) _iconOf[item.Key] = ("icon:" + source, source);
        }
        Window.Surface.RemoveText = s["dock.remove"];
        Window.Surface.SetItems(_items, IconFor);
        PlaceWindow();
        if (_taskband != null)
        {
            _taskband.Forget(_windows.Windows.Select(w => w.Hwnd)); // windows gone without clearing their marks
            UpdateMarks();
        }
    }

    System.Windows.Media.ImageSource? IconFor(DockItem item) =>
        _iconOf.TryGetValue(item.Key, out var i) ? _icons.Get(i.key, i.source) : null;

    /// <summary>Where an item's icon comes from (diagnostics).</summary>
    public string? IconSourceOf(string key) => _iconOf.TryGetValue(key, out var i) ? i.source : null;

    double _placedLength, _widest, _fitMax;
    string? _widestKey;

    /// <summary>The row did not fit the screen at the settings' sizes: it is drawn smaller (diagnostics).</summary>
    public bool Fitted { get; private set; }

    /// <summary>
    /// Just big enough for the widest magnified plate, a name label at either end and a bounce: a layered window sends
    /// all of its pixels on every frame (2026-10-12: 2065×188 px took 9–10% of a core in WPF's render thread while
    /// the pointer swept along 24 icons).
    /// </summary>
    /// <summary>
    /// The sizes the row is drawn at: the settings', or smaller where the row would not fit the screen with a name's room
    /// at each end (macOS: its icons, then its magnification, shrink; large icons or many apps were cut off at the
    /// screen's edges). Measured again only when the row itself changed (rebuilds come with every change of the front
    /// app). True when the surface got new sizes (a restyle makes its pictures for them).
    /// </summary>
    bool FitSizes()
    {
        var d = _settings.Dock;
        var kinds = _items.Select(i => i.Kind == DockItemKind.Separator ? SlotKind.Separator : SlotKind.Icon).ToList();
        double max = d.Magnification ? Math.Max(d.MagnifiedSize, d.IconSize) : d.IconSize;
        // The screen's width less a name's room at each end; on a side the work area's height (below the menu bar),
        // the names beside the icons.
        var edge = Window.Edge;
        double room = (edge == DockEdge.Bottom ? _mon.Bounds.Width : _mon.Work.Height) / _mon.Scale - 2 * DockSurface.EndMargin(edge);
        string key = $"{string.Concat(kinds.Select(k => k == SlotKind.Separator ? '|' : 'o'))} {d.IconSize} {max} {_mon.Scale} {d.Spacing} {room}";
        if (key == _widestKey) return false;
        var (fitBase, fitMax) = DockGeometry.Fit(kinds, d.IconSize, max, _mon.Scale, room, d.Spacing);
        _fitMax = fitMax;
        Fitted = fitBase < d.IconSize || fitMax < max;
        _widest = DockGeometry.MaxPlateWidth(kinds, fitBase, fitMax, _mon.Scale, d.Spacing);
        _widestKey = key;
        var s = Window.Surface;
        double surfaceMax = d.Magnification ? fitMax : d.MagnifiedSize;
        if (s.BaseSize == fitBase && s.MaxSize == surfaceMax) return false;
        if (Fitted) Log.Info($"dock: {kinds.Count} items do not fit {room:0} DIP at {d.IconSize}/{max} DIP: drawn at {fitBase:0.##}/{fitMax:0.##}");
        s.BaseSize = fitBase;
        s.MaxSize = surfaceMax;
        return true;
    }

    void PlaceWindow()
    {
        var d = _settings.Dock;
        if (FitSizes()) Window.Surface.Restyle();
        var edge = Window.Edge;
        double length = _widest + 2 * DockSurface.EndMargin(edge);
        if (Math.Abs(length - _placedLength) < 1 && Window.IsLoaded) return;
        _placedLength = length;
        Window.Place(length, DockSurface.DepthFor(edge, _fitMax, d.Padding), _mon, _edgePx);
        if (Hiding) UpdateSpan();
    }

    // ---- clicks ----------------------------------------------------------------------------------------------

    /// <summary>A click on item <paramref name="index"/> as if made with the mouse (diagnostics).</summary>
    public void Click(int index, MouseButton button) => OnClick(index, button);

    /// <summary>
    /// A spec's shell (Core's SpecGuard): the apps its Dock may start, bring forward, minimize or quit (the test target's);
    /// null outside the specs. It opens neither the user's file manager nor the recycle bin there either: a spec's click
    /// that fell on the Dock started the user's app, its pins taken from the user's taskbar (2026-10-16).
    /// </summary>
    public Func<DockItem, bool>? MayActOn { get; set; }
    /// <summary>Clicks and menu commands a spec's shell did not carry out (diagnostics).</summary>
    public int Guarded { get; private set; }

    /// <summary>
    /// A spec's shell: the minimized windows its Dock may show as pictures (the test target's). It shows no picture of a
    /// window of the user's (a spec's pictures of the Dock would carry it). Null outside the specs. Given to the
    /// constructor: set afterwards, the rows built in the constructor showed the user's minimized windows (2026-10-16:
    /// the next rows lost them again, and dock-trash and settings read places and counts in between).
    /// </summary>
    public Func<RunningWindow, bool>? MayShowWindow { get; }

    /// <summary>
    /// A shell that is photographed for the public (FROSTBAR_E2E_DOCK=spec, with --diag): the windows its Dock counts as
    /// running at all (the test target's). The user's open apps are not in its pictures. Null otherwise. Given to the
    /// constructor, as <see cref="MayShowWindow"/> is.
    /// </summary>
    public Func<RunningWindow, bool>? MayListWindow { get; }

    /// <summary>
    /// A spec's shell: the folders its Dock may open as stacks (the spec's own; a stray click opened the user's Downloads
    /// in it, its files then in the pictures), and the files a stack may open (the test target's: a document opens in the
    /// user's app, a folder in the user's file manager). Null outside the specs. The menu's "open-folder" was guarded,
    /// the click and the stack's rows were not.
    /// </summary>
    public Func<string, bool>? MayStack { get; set; }
    public Func<string, bool>? MayOpenFile { get; set; }

    bool GuardedOpen(string path)
    {
        if (MayOpenFile == null || MayOpenFile(path)) return false;
        Guarded++;
        Log.Info($"dock: open {Log.Redact(path)}: guarded (a spec's shell opens the test target only)");
        return true;
    }

    bool Refused(DockItem item, string what)
    {
        if (MayActOn == null || item.Kind is DockItemKind.App or DockItemKind.MinimizedWindow && MayActOn(item)) return false;
        Guarded++;
        Log.Info($"dock: {what} on {item.Name}: guarded (a spec's shell acts on the test target only)");
        return true;
    }

    void OnClick(int index, MouseButton button)
    {
        if (index < 0 || index >= _items.Count) return;
        var item = _items[index];
        switch (item.Kind)
        {
            case DockItemKind.AppGrid:
                ToggleAppGrid();
                return;
            case DockItemKind.Trash:
                if (Refused(item, "open")) return;
                FileManager.OpenRecycleBin();
                return;
            case DockItemKind.Folder:
                if (MayStack != null && !(item.LaunchTarget is { } shown && MayStack(shown)))
                {
                    Guarded++;
                    Log.Info($"dock: stack of {item.Name}: guarded (a spec's shell opens its own folders only)");
                    return;
                }
                OpenStack(index);
                return;
            case DockItemKind.MinimizedWindow:
                // Its window comes back, out of the picture (Windows' restore animation starts at its slot).
                if (button != MouseButton.Left || Refused(item, "click")) return;
                if (DockModel.Click(item, false) is { Action: DockAction.Restore } back) BringForward(new IntPtr(back.Hwnd));
                return;
            case DockItemKind.App:
                if (Refused(item, "click")) return;
                if (button == MouseButton.Middle) { Launch(item); return; }
                if (button != MouseButton.Left) return;
                var click = DockModel.Click(item, _settings.Dock.ClickActiveAppMinimizes);
                Log.Debug($"dock: {click.Action} {item.Name} (item {index}, window {click.Hwnd})");
                switch (click.Action)
                {
                    // Once while it starts (it bounces): another click opened a second copy of the apps that allow
                    // several (macOS takes no notice of clicks on an app still starting).
                    case DockAction.Launch when _launching.TryGetValue(item.AppId, out var starting) && !starting.Landed:
                        Log.Debug($"dock: {item.Name} is starting: no second launch");
                        break;
                    case DockAction.Launch: Launch(item); break;
                    case DockAction.Activate: BringForward(new IntPtr(click.Hwnd)); break;
                    case DockAction.ActivateAll: WindowActions.BringAllToFront([.. click.Windows!.Select(h => new IntPtr(h))]); break; // all of this desktop's, in order (macOS)
                    case DockAction.MinimizeAll: foreach (var w in DockModel.ToMinimize(item)) WindowActions.Minimize(new IntPtr(w.Hwnd)); break; // this desktop's
                }
                return;
        }
    }

    int _switching; // 1 while Win+Ctrl+arrows take the PC to another desktop

    /// <summary>Diagnostics: desktop switches for a window of another desktop, and how the last one went.</summary>
    public (int count, string last) DesktopSwitches { get; private set; } = (0, "");

    /// <summary>
    /// A window to the front. Windows goes to the desktop of a window of another desktop brought to the front (about
    /// 50 ms, vd-probe 2026-10-15), as macOS goes to the app's Space, but not when that window already is the
    /// foreground window, hidden there (moved there while in front): bringing it forward changes nothing then. So when
    /// its desktop is not current 600 ms later, the Dock goes there itself (Win+Ctrl+arrows, off the UI thread).
    /// </summary>
    void BringForward(IntPtr h)
    {
        var desktop = VirtualDesktops.DesktopOf(h);
        if (!WindowActions.ForceForeground(h)) Log.Debug($"dock: window {h} did not come to the front (foreground {Native.GetForegroundWindow()})");
        if (desktop is not { } d) return;
        if (Interlocked.Exchange(ref _switching, 1) == 1) { Log.Debug($"dock: window {h}: a desktop switch is under way"); return; }
        var sw = Stopwatch.StartNew();
        Task.Run(() =>
        {
            string how;
            bool there = false;
            try
            {
                while (sw.ElapsedMilliseconds < 600 && !(there = VirtualDesktops.Current() == d)) Thread.Sleep(15);
                if (there) how = $"Windows went there in {sw.ElapsedMilliseconds} ms";
                else
                {
                    there = VirtualDesktops.SwitchTo(d);
                    how = $"{(there ? "keys took it there" : "keys did not take it there")} in {sw.ElapsedMilliseconds} ms";
                }
            }
            catch (Exception ex) { Log.Warn("dock: desktop switch", ex); how = "failed: " + ex.Message; }
            _ui.BeginInvoke(() =>
            {
                bool front = there && WindowActions.ForceForeground(h); // after the keys, the desktop's last window came forward
                DesktopSwitches = (DesktopSwitches.count + 1, $"{how}, {(front ? "in front" : "not in front")}");
                Log.Debug($"dock: window {h} on desktop {d}: {DesktopSwitches.last}");
                Volatile.Write(ref _switching, 0);
            });
        });
    }

    /// <summary>Opens or closes the app grid (M5), timed from the click's message; without one, Windows' Start menu.</summary>
    public Action<int>? OpenAppGrid { get; set; }

    /// <summary>The window overview for one app's windows (M7, the menu's "Show All Windows"): its AppID.</summary>
    public Action<string>? ShowAllWindows { get; set; }

    void ToggleAppGrid()
    {
        if (OpenAppGrid != null) OpenAppGrid(Native.GetMessageTime());
        else InputInjector.SendChord(new Shortcut(Mods.None, 0x5B /*VK_LWIN*/), Native.GetForegroundWindow());
    }

    /// <summary>Puts the Dock back above the other topmost windows (the app grid, activated over it).</summary>
    public void Raise()
    {
        const uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
        Native.SetWindowPos(Window.BackdropHandle, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
        Native.SetWindowPos(Window.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, flags); // owned by the backdrop: above it
        // The window above the pair last: owned by the content window, it still stayed under the backdrop (dock-zorder).
        if (Window.AboveHandle != IntPtr.Zero) Native.SetWindowPos(Window.AboveHandle, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
    }

    void Launch(DockItem item)
    {
        string? target = item.LaunchTarget ?? item.Windows.FirstOrDefault()?.ExePath;
        if (target == null) return;
        CommandRunner.Launch(target);
        // Bounces while an app starts (macOS). A running app asked for a new window does not: one that keeps a single
        // instance (Discord, Obsidian, Settings) opened no window and bounced for the full 10 s (2026-10-12).
        if (!_settings.Dock.BounceOnLaunch || item.Running) return;
        // One of the apps used lately stays in its place there while it runs (it moved to the running apps' end).
        if (item.Key.StartsWith(DockModel.RecentPrefix, StringComparison.Ordinal)) _stayRecent.Add(item.AppId);
        Bounce(item, ProgramOf(item));
    }

    void ExpireLaunches()
    {
        long now = Environment.TickCount64;
        foreach (var (appId, l) in _launching.Where(kv => now - kv.Value.Since > LaunchBounceSeconds * 1000).ToList())
        {
            _launching.Remove(appId);
            if (!l.Landed) Window.Surface.StopBounce(l.Key);
            // An app that never showed a window: its item lands, then goes with its room.
            if (_starting.ContainsKey(appId))
            {
                if (!l.Landed) Expired++;
                var drop = new DispatcherTimer(TimeSpan.FromMilliseconds(Motion.DockLand + 20), DispatcherPriority.Background, (s, _) => { ((DispatcherTimer)s!).Stop(); if (_starting.Remove(appId)) Rebuild(); }, _ui);
                drop.Start();
            }
        }
        if (_launching.Count == 0) _launchTimeout.Stop();
    }

    // ---- an app opened from elsewhere (2026-10-23, the user's "A どこから開いても跳ねる" and "macOS と同じく前面に出す") ----

    /// <summary>Apps starting that the Dock had no item for: an item of their own while they start (Core's DockModel.Build).</summary>
    readonly Dictionary<string, PinnedApp> _starting = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Apps started from the recent section: they stay in its place while they run.</summary>
    readonly HashSet<string> _stayRecent = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>A shortcut's or a program's AppID and program, kept while the file is unchanged (path → (written, appId, program)).</summary>
    readonly Dictionary<string, (long written, string? appId, string? program)> _launchIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Diagnostics "dock-launches": launches told of, brought forward, bounced, given an item, expired, mismatched.</summary>
    public int Told { get; private set; }
    public int Forwarded { get; private set; }
    public int Bounced { get; private set; }
    public int Added { get; private set; }
    public int Expired { get; private set; }
    public int Mismatched { get; private set; }
    /// <summary>The last launch told of: where from, what, how its item was found, what the Dock did.</summary>
    public string LastLaunch { get; private set; } = "";

    /// <summary>
    /// An app is being opened from elsewhere (<paramref name="source"/>: the app grid, the search, a stack, the recent
    /// items), its target <paramref name="target"/> (an AppsFolder name, a shortcut, a program; documents, folders and
    /// addresses are left alone: another app opens them). As macOS does: an app that runs is brought forward instead
    /// (true: the caller does not start it again); one the Dock keeps bounces until its first window comes; one it
    /// does not keep gets its item at once (it grows into the row) and bounces in it. Called once the caller's own
    /// guard has let the launch through; the Dock starts nothing itself here (a spec's shell acts on the test target
    /// only: <see cref="MayActOn"/> holds for bringing forward).
    /// </summary>
    public bool Launching(string target, string source)
    {
        Told++;
        var kind = LaunchTargets.Kind(target, Directory.Exists(target));
        var (appId, program) = IdentityOf(target, kind);
        var (index, by) = LaunchMatch.Find(_items, appId, program, ProgramOf);
        var item = index >= 0 ? _items[index] : null;
        var decision = LaunchPlan.Decide(item, kind, _settings.Dock.BounceOnLaunch);
        LastLaunch = $"{source} {kind} by {by}: {(decision.BringForward ? "forward" : decision.AddItem ? "item" : decision.Bounce ? "bounce" : "nothing")}";
        Log.Debug($"dock: launch from {source} of {Log.Redact(target)} ({kind}, AppID {appId ?? "?"}): {LastLaunch}");
        if (decision.BringForward && item != null)
        {
            if (MayActOn != null && !MayActOn(item)) { Guarded++; return false; }
            var click = DockModel.Click(item, false);
            if (click.Action == DockAction.Activate) BringForward(new IntPtr(click.Hwnd));
            else if (click.Action == DockAction.ActivateAll) WindowActions.BringAllToFront([.. click.Windows!.Select(h => new IntPtr(h))]);
            else return false; // nothing of it here to bring (its windows on another desktop only): started as asked
            Forwarded++;
            return true;
        }
        if (!decision.Bounce && !decision.AddItem) return false;
        string id = item?.AppId ?? appId ?? program ?? target;
        if (_launching.TryGetValue(id, out var under) && !under.Landed) return false; // landed (in front, its window not listed yet): it may be opened again
        if (item == null)
        {
            if (!decision.AddItem) return false;
            _starting[id] = new PinnedApp("running:" + id, id, NameFor(id, program, target), program ?? target);
            Added++;
            Rebuild();
            item = _items.FirstOrDefault(i => i.Kind == DockItemKind.App && string.Equals(i.AppId, id, StringComparison.OrdinalIgnoreCase));
            if (item == null) { _starting.Remove(id); return false; }
        }
        else if (item.Key.StartsWith(DockModel.RecentPrefix, StringComparison.Ordinal)) _stayRecent.Add(item.AppId);
        // Bouncing turned off, it is still watched for its window as a launch: its own item went only with a launch's
        // end, so one that never showed a window stayed until the shell restarted (and a recent one left its place).
        Bounce(item, program, decision.Bounce);
        if (decision.Bounce) Bounced++;
        return false;
    }

    /// <summary>The AppID and program a launch's target stands for (kept per file while it is unchanged).</summary>
    (string? appId, string? program) IdentityOf(string target, LaunchKind kind)
    {
        switch (kind)
        {
            case LaunchKind.AppsFolder:
            {
                string? id = LaunchTargets.AppsFolderId(target);
                if (_launchIds.TryGetValue(target, out var known)) return (id, known.program);
                string? program = ShellApi.LinkTarget(target);
                _launchIds[target] = (0, id, program);
                return (id, program);
            }
            case LaunchKind.Shortcut or LaunchKind.Program:
            {
                long written = File.Exists(target) ? File.GetLastWriteTimeUtc(target).Ticks : 0;
                if (_launchIds.TryGetValue(target, out var known) && known.written == written) return (known.appId, known.program);
                string? program = kind == LaunchKind.Shortcut ? OpenWith.Resolve(target)?.path : target;
                string? appId = _shortcutIds.Get(target, written) ?? _identity.AppIdForShortcut(target);
                _launchIds[target] = (written, appId, program);
                return (appId, program);
            }
            default: return (null, null);
        }
    }

    /// <summary>An item's program, to find it by: a kept shortcut's target, a kept AppsFolder entry's link, a running app's window's.</summary>
    string? ProgramOf(DockItem item)
    {
        if (item.Windows.FirstOrDefault()?.ExePath is { } exe) return exe;
        if (item.LaunchTarget is not { } t) return null;
        var kind = LaunchTargets.Kind(t, false);
        return kind is LaunchKind.Shortcut or LaunchKind.AppsFolder or LaunchKind.Program ? IdentityOf(t, kind).program : null;
    }

    string NameFor(string appId, string? program, string target)
    {
        try { return _identity.DisplayName(appId, program); }
        catch (Exception) { return Path.GetFileNameWithoutExtension(program ?? target); }
    }

    /// <summary>
    /// After a new row: starting apps whose windows came keep their item as an ordinary running app; one whose windows
    /// came under another AppID (a launcher, an updater's stub) gives its item up (counted); recent apps that stopped
    /// running leave the recent section's hold.
    /// </summary>
    void SettleStarting(IReadOnlyList<RunningWindow> windows)
    {
        foreach (var (id, app) in _starting.ToList())
        {
            if (windows.Any(w => string.Equals(w.AppId, id, StringComparison.OrdinalIgnoreCase))) { _starting.Remove(id); continue; }
            if (app.LaunchTarget is { } program && windows.Any(w => LaunchMatch.SameFile(w.ExePath, program)))
            {
                Mismatched++;
                Log.Debug($"dock: {Log.Redact(program)} came with another AppID than {id}: its starting item goes");
                _starting.Remove(id);
                if (_launching.Remove(id)) Window.Surface.StopBounce("running:" + id);
            }
        }
        _stayRecent.RemoveWhere(id => !_launching.ContainsKey(id) && !windows.Any(w => string.Equals(w.AppId, id, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Diagnostics "dock-launches".</summary>
    public object LaunchState() => new
    {
        told = Told, forwarded = Forwarded, bounced = Bounced, added = Added, expired = Expired, mismatched = Mismatched, last = LastLaunch,
        starting = _starting.Keys.ToArray(), launching = _launching.Select(kv => new { appId = kv.Key, key = kv.Value.Key, ms = Environment.TickCount64 - kv.Value.Since, landed = kv.Value.Landed }).ToArray(),
        stayRecent = _stayRecent.ToArray(),
        hopped = Window.Surface.Hopped, landed = Window.Surface.Landed,
    };

    DockItem? ItemOf(IntPtr hwnd)
    {
        var w = _windows.Windows.FirstOrDefault(x => x.Hwnd == hwnd);
        return w == null ? null : _items.FirstOrDefault(i => i.Kind == DockItemKind.App && string.Equals(i.AppId, w.AppId, StringComparison.OrdinalIgnoreCase));
    }

    void OnFlashing(IntPtr hwnd)
    {
        if (ItemOf(hwnd) is { Active: false } item) Window.Surface.StartBounce(item.Key, AttentionHops);
    }

    void OnActivated(IntPtr hwnd)
    {
        if (ItemOf(hwnd) is { } item && !_launching.ContainsKey(item.AppId)) Window.Surface.StopBounce(item.Key);
    }

    // ---- the right-click menu --------------------------------------------------------------------------------

    void OnMenu(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        var item = _items[index];
        var s = _strings();
        var none = new MenuAction(ActionKind.None, "dock");
        var entries = new List<MenuItem>();
        if (item.Kind == DockItemKind.Trash)
        {
            // macOS: Open, Empty Trash.
            entries.Add(new MenuItem("open-trash", s["dock.open"], null, none));
            entries.Add(MenuItem.Separator);
            entries.Add(new MenuItem("empty-trash", s["trash.emptyAction"], null, none, Enabled: _bin.Full));
        }
        else if (item.Kind == DockItemKind.AppGrid) entries.Add(new MenuItem("open-grid", s["dock.open"], null, none));
        else if (item.Kind == DockItemKind.Separator && item.Pinned) entries.Add(new MenuItem("unpin", s["dock.removeSeparator"], null, none));
        else if (item.Kind == DockItemKind.Folder)
        {
            entries.Add(new MenuItem("open-folder", s["dock.open"], null, none));
            entries.Add(new MenuItem("remove-folder", s["dock.remove"], null, none));
        }
        else if (item.Kind == DockItemKind.MinimizedWindow && item.Windows.Count > 0)
        {
            // Its window: back, or closed (the app goes on running). An administrator's window takes both as its window
            // menu's commands (WindowActions.Restore, Close).
            entries.Add(new MenuItem("restore", s["dock.showWindow"], null, none));
            entries.Add(MenuItem.Separator);
            entries.Add(new MenuItem("close-window", s["dock.closeWindow"], null, none));
        }
        else if (item.Kind == DockItemKind.App)
        {
            var front = Native.GetForegroundWindow();
            foreach (var w in item.Windows.Take(12))
            {
                string title = Native.Title(new IntPtr(w.Hwnd));
                entries.Add(new MenuItem("win:" + w.Hwnd, string.IsNullOrWhiteSpace(title) ? item.Name : title, null, none, Checked: new IntPtr(w.Hwnd) == front));
            }
            if (entries.Count > 0) entries.Add(MenuItem.Separator);
            // The overview shows this desktop's windows that are not minimized: with none it said "no windows to show".
            if (item.Windows.Any(w => !w.Minimized && !w.Elsewhere) && ShowAllWindows != null) entries.Add(new MenuItem("all-windows", s["dock.allWindows"], null, none));
            // Not running: "Open" (macOS); one of the apps used lately can also leave that section.
            if (item.LaunchTarget != null || item.Windows.Count > 0) entries.Add(new MenuItem("new", item.Running ? s["dock.newWindow"] : s["dock.open"], null, none));
            entries.Add(item.Pinned ? new MenuItem("unpin", s["dock.remove"], null, none) : new MenuItem("pin", s["dock.keep"], null, none));
            // A line to set the kept apps apart in groups (the user, 2026-10-12): it can then be dragged along them. After
            // the item: to its right at the bottom, below it on a side.
            if (item.Pinned) entries.Add(new MenuItem("add-separator", s[Window.Edge == DockEdge.Bottom ? "dock.addSeparator" : "dock.addSeparatorBelow"], null, none));
            if (item.Key.StartsWith(DockModel.RecentPrefix, StringComparison.Ordinal)) entries.Add(new MenuItem("forget", s["dock.remove"], null, none));
            if (FolderPathOf(item) != null) entries.Add(new MenuItem("show", s.F("dock.showIn", FileManager.NameIn(s)), null, none));
            if (item.Running)
            {
                entries.Add(MenuItem.Separator);
                bool shift = (Native.GetAsyncKeyState(0x10) & 0x8000) != 0;
                var quit = shift ? new MenuItem("force-quit", s["dock.forceQuit"], null, none) : new MenuItem("quit", s["dock.quit"], null, none);
                // An app running as administrator quits (its windows take their window menu's Close: WindowActions.Close)
                // but cannot be forced to (its process takes no kill from us).
                bool refused = shift && item.Windows.Any(w => Native.IsMoreElevatedThanUs(Native.AppOrWindowProcessId(new IntPtr(w.Hwnd))));
                entries.Add(refused ? quit with { Enabled = false, Hint = s["app.elevatedForceQuit"] } : quit);
            }
        }
        if (entries.Count == 0) return;
        var t = _theme.Current;
        var popup = new MenuPopup(new Menu("dock", item.Name, entries), t, Materials.Menu(t, _settings.Appearance, (float)(MenuPopup.RadiusDip * _mon.Scale)), k => k.ToString());
        _menu.ShowBeside(popup, Window.Edge, Window.ToScreen(Window.Surface.SlotRect(index)), ScreenDip, chosen => Run(item, chosen.Id));
        _menuOpen = _menu.IsOpen; // auto-hide: the Dock stays while its menu is open
        Window.Surface.Hold = _menu.IsOpen; // and magnified as it was, under the menu
    }

    static string? FolderPathOf(DockItem item)
    {
        var exe = item.Windows.FirstOrDefault()?.ExePath;
        if (exe != null && File.Exists(exe)) return exe;
        return item.LaunchTarget != null && File.Exists(item.LaunchTarget) ? item.LaunchTarget : null;
    }

    void Run(DockItem item, string id)
    {
        Log.Debug($"dock: menu {id} on {item.Name}");
        // The ones that act on an app or open the user's file manager or recycle bin: not from a spec's shell, but on the test target.
        bool acts = id.StartsWith("win:", StringComparison.Ordinal) || id is "new" or "quit" or "force-quit" or "open-trash" or "empty-trash" or "show" or "open-folder"
            or "restore" or "close-window";
        if (acts && Refused(item, "menu " + id)) return;
        if (id.StartsWith("win:", StringComparison.Ordinal) && long.TryParse(id[4..], out var h)) { BringForward(new IntPtr(h)); return; }
        switch (id)
        {
            case "restore": if (item.Windows.Count > 0) BringForward(new IntPtr(item.Windows[0].Hwnd)); break;
            case "close-window": if (item.Windows.Count > 0) WindowActions.Close(new IntPtr(item.Windows[0].Hwnd)); break;
            case "open-trash": FileManager.OpenRecycleBin(); break;
            case "empty-trash": AskEmptyTrash(); break; // one question at a time (twice from the menu, two dialogs emptied it twice)
            case "open-grid": ToggleAppGrid(); break;
            case "open-folder": if (item.LaunchTarget != null) FileManager.OpenFolder(item.LaunchTarget); break;
            case "remove-folder": RemoveFolder(item); break;
            case "all-windows": ShowAllWindows?.Invoke(item.AppId); break;
            case "new": Launch(item); break;
            case "pin": Pin(item); break;
            case "unpin": Unpin(item); break;
            case "add-separator": AddSeparatorAfter(item); break;
            case "forget": Forget(item); break;
            case "show":
                if (FolderPathOf(item) is { } path) FileManager.Reveal(path);
                break;
            case "quit":
                foreach (var w in item.Windows)
                {
                    var hwnd = new IntPtr(w.Hwnd);
                    // File Explorer: its windows only, never the desktop's process
                    if (IsExplorer(w.ExePath) && Native.ClassName(hwnd) != "CabinetWClass") continue;
                    WindowActions.Close(hwnd);
                }
                break;
            case "force-quit":
                // The app's own process: never ApplicationFrameHost, which every packaged app's window shares (0 is left out).
                foreach (var pid in item.Windows.Where(w => !IsExplorer(w.ExePath)).Select(w => Native.AppProcessId(new IntPtr(w.Hwnd))).Where(p => p != 0).Distinct())
                {
                    try { Process.GetProcessById((int)pid).Kill(); Log.Info($"dock: force quit pid {pid}"); }
                    catch (Exception ex) { Log.Warn($"dock: force quit pid {pid} failed", ex); }
                }
                break;
        }
    }

    static bool IsExplorer(string? exe) => exe != null && Path.GetFileName(exe).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>The open "Empty Trash" question (diagnostics: the specs answer it).</summary>
    public DialogWindow? EmptyTrashQuestion { get; private set; }

    /// <summary>Something is in the bin (the search offers to empty it only then).</summary>
    public bool TrashFull => _bin.Full;

    /// <summary>The search's "Empty Trash" action (stage 6-1): asked first, as from the Dock's menu.</summary>
    public void AskEmptyTrash()
    {
        if (EmptyTrashQuestion != null) { EmptyTrashQuestion.Activate(); return; }
        ConfirmEmptyTrash();
    }

    /// <summary>A window to the front from the search's windows (stage 6-1), going to its desktop as the Dock's click does.</summary>
    public void BringWindowForward(IntPtr h) => BringForward(h);

    /// <summary>
    /// macOS's "Empty Trash": asked first (it cannot be undone), then the bin is emptied on a thread of its own (Windows
    /// shows its progress for a large one).
    /// </summary>
    void ConfirmEmptyTrash()
    {
        var s = _strings();
        EmptyTrashQuestion = Dialogs.Confirm(_theme.Current, s["trash.confirmTitle"], s["trash.confirmDetail"], s["dlg.cancel"], s["trash.confirmAction"],
            () => _bin.Empty(error =>
            {
                if (error == null) Log.Info("trash: emptied");
                else Log.Warn("trash: emptying failed: " + error);
            }, sound: _settings.Sound.InterfaceSounds));
        EmptyTrashQuestion.Closed += (_, _) => EmptyTrashQuestion = null;
    }

    /// <summary>
    /// Explorer restarted: our shell hook window goes after its new one, now and once more when its taskbar has settled,
    /// so that windows still minimize into the Dock (the last answer to Windows' question stands).
    /// </summary>
    public void OnExplorerRestarted()
    {
        _windows.RegisterAgain();
        var later = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (s, _) => { ((DispatcherTimer)s!).Stop(); _windows.RegisterAgain(); }, _ui);
        later.Start();
    }

    /// <summary>Kept to stop listening: the tracker outlives a Dock made again (larger icons, a menu bar of a new height, a new DPI).</summary>
    readonly Action<FrontWindow> _onFront;

    bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        _pinCheck?.Stop();
        if (_shortcutIds.Changed) SaveShortcutIds();
        _front.Changed -= _onFront;
        if (_taskband != null) _taskband.Changed -= UpdateMarks; // the listener outlives a Dock made again
        _ticker?.Remove(AutoHideJob);
        _slideClock?.Dispose();
        _menu.Dispose(); // its click shields too (they stayed, hidden, with this Dock behind them)
        _launchTimeout.Stop();
        _windows.Dispose();
        _icons.Dispose();
        _bin.Dispose();
        Window.Close();
    }
}
