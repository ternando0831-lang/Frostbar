using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Frostbar.App.Common;
using Frostbar.App.Menus;
using Frostbar.Core.AppGrid;
using Frostbar.Core.Localization;
using Frostbar.Core.Search;
using Frostbar.Core.Settings;
using Frostbar.Core.Shell;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Icons;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Input;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Shell;
using Frostbar.Platform.Sys;
using Frostbar.Platform.Wm;
using Menu = Frostbar.Core.Menus.Menu;
using MenuItem = Frostbar.Core.Menus.MenuItem;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.AppGrid;

/// <summary>
/// The app grid (M5, Launchpad-like): every app of the Start menu's "All apps" except uninstallers, manuals and web
/// links, 7 × 5 to a page, over the blurred desktop between the menu bar and the bottom of the screen, in the user's
/// arrangement (stage 6 E1: by name until they move something; new apps come last). Opened from the Dock's app grid
/// item. The wheel, a swipe, a drag of empty space, ←/→, Page Up/Down or a page dot turns the pages; typing filters
/// (arrows choose, Enter opens); a click opens an app or a folder. An app pressed and moved is carried: the others make
/// room where it would land, it can rest on another app to make a folder with it (or on a folder to go in), at the
/// grid's side to turn the page, or go into the Dock. A folder opens on a dark panel with its name above (click it to
/// rename); its apps are moved or carried out the same way. A right-click (or the menu key on the chosen app) opens a
/// place's menu at the pointer (2026-10-24): an app's opens it, as administrator, keeps it in the Dock or takes it out,
/// shows its file, hides it; a folder's opens, renames or ungroups it. Hidden apps go into a folder of their own, last
/// (carried in, they hide; carried out, they show). Esc, a click on empty space or another window coming forward
/// closes it. It takes the keyboard, so it activates; the Dock is put back above it.
/// </summary>
public sealed class AppGridController : IDisposable
{
    const double DragStart = 6, FlyBackSeconds = Motion.GridFlyBack / 1000;
    /// <summary>How far past the open folder's panel a carried app goes before the folder closes and it is over the grid (DIPs).</summary>
    const double FolderLeave = 40;
    /// <summary>The open folder's sides that turn its pages while an app is carried there (DIPs in from the panel's edge).</summary>
    const double FolderSide = 24;

    readonly AppSettings _settings;
    readonly SettingsSaver _saver;
    readonly ThemeService _theme;
    readonly Func<Strings> _strings;
    readonly Func<RECT> _barRect;
    readonly Action _raiseDock;
    readonly Dispatcher _ui;
    readonly WheelPager _wheel = new();
    readonly DispatcherTimer _iconsLoaded, _intoTimer, _edgeTimer, _gapTimer, _moveEnd;
    readonly PopupMenuSession _menu;
    MonitorInfo _mon;
    AppGridWindow? _window;
    SurfaceFactory? _surfaces;
    AppGridScene? _scene;
    IconService? _icons;
    GridLayout? _layout;
    Rect _area;
    IReadOnlyList<GridApp> _apps = [];
    List<Candidate> _names = new();
    /// <summary>The arrangement as it shows (no search).</summary>
    List<GridTile> _tiles = new();
    /// <summary>What the pages show: the arrangement, the search's apps, or the arrangement around a carried app's gap.</summary>
    IReadOnlyList<GridTile> _shown = [];
    int _page, _pageBeforeSearch, _selection = -1;
    IntPtr _previous;
    bool _closing, _labelsQueued;
    string _query = "";

    // A press: where, on what, and whether it became a drag of the pages (the grid's, or the open folder's).
    enum Spot { None, Tile, Member, Title, Panel, Outside }
    Point? _down;
    Spot _downOn;
    int _downAt = -1;
    bool _dragging;
    readonly List<(long ms, double x)> _moves = new();

    // Carrying (Launchpad): the carried place's picture follows the pointer above everything (the grid's own pictures
    // are below the Dock); in the arrangement it is faint where it would land, the others making room. Over the Dock
    // the Dock opens a gap and the arrangement shows it where it was. Let go, its picture flies to where it went.
    GridTile? _carried;
    string? _fromFolder;        // the folder it was taken out of (its key), or null
    int _fromAt = -1;           // where it was: its place in the grid, or among its folder's apps
    List<GridTile> _rest = new(); // the arrangement without it (from a folder: the folder without it)
    int _gap = -1;              // where it would land among _rest (-1: nowhere in the grid, over the Dock)
    int _folderGap = -1;        // where it would land among the open folder's apps
    bool _inFolder, _reorder;   // carried over the open folder's panel; the arrangement shows (not a search's apps)
    int _over = -1, _into = -1; // the place under the pointer's middle, and the one it goes into once it rested (in _rest)
    int _edgeSide;
    Vector _grab;               // from the pointer to the icon's centre where it was picked up (DIPs)
    POINT _carriedAt;           // the picture's centre on the screen (pixels)
    bool _overShelf;
    Dock.DragImage? _carryImage;
    /// <summary>Whose picture is carried (and flies after it is let go): read from the scene each time (icons load meanwhile).</summary>
    GridTile? _picture;
    FrameClock? _flyClock;
    POINT _flyFrom;
    (double X, double Y) _flyTo; // pixels on the screen, unrounded
    readonly Stopwatch _flyWatch = new();
    int _flying = -1;           // the place whose picture flies to it (faint until it lands)

    // The open folder.
    string? _openKey;
    FolderLayout? _folderLayout;
    bool _renaming;

    /// <summary>Where an app carried out of the grid can be kept (the Dock, which may be made again: asked each time).</summary>
    public Func<IAppShelf?> Shelf { get; set; } = () => null;

    /// <summary>
    /// A spec's shell (with --diag): the test target's folder. The grid then opens only programs from there: a click
    /// that lands on one of the user's apps opens nothing (<see cref="SpecGuard"/>).
    /// </summary>
    public string? OnlyAppsIn { get; set; }

    /// <summary>Open, and not on its way out (it is closed for everything else as soon as it starts fading).</summary>
    public bool IsOpen => _window?.IsVisible == true && !_closing;
    DispatcherTimer? _closeTimer;
    /// <summary>Diagnostics: input (the click's message time) to the first frame of the open grid, in ms (-1: not measured).</summary>
    public int LastOpenMs { get; private set; } = -1;
    /// <summary>Diagnostics: the last app opened from the grid, or why not.</summary>
    public string LastLaunch { get; private set; } = "";
    /// <summary>The grid opened (the shell reads the Start menu again if its list is old).</summary>
    public event Action? Opened;
    /// <summary>The places' right-click menus (the menu bar's close when one opens).</summary>
    public PopupMenuSession Menu => _menu;
    /// <summary>Diagnostics: the last menu item run, and on what.</summary>
    public string LastMenu { get; private set; } = "";

    public AppGridController(MonitorInfo mon, AppSettings settings, SettingsSaver saver, ThemeService theme, Func<Strings> strings, Func<RECT> barRect, Action raiseDock, Dispatcher ui,
        HotkeyService hotkeys)
    {
        _mon = mon;
        _settings = settings;
        _saver = saver;
        _theme = theme;
        _strings = strings;
        _barRect = barRect;
        _raiseDock = raiseDock;
        _ui = ui;
        _menu = new PopupMenuSession(hotkeys, barRect);
        // Icons arrive one by one: upload them together, 100 ms after the last.
        _iconsLoaded = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => { _iconsLoaded!.Stop(); _scene?.RefreshIcons(Icon); }, ui);
        _intoTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(GridDrop.IntoDwellMs), DispatcherPriority.Normal, (_, _) => Rested(), ui);
        _edgeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(GridDrop.EdgeDwellMs), DispatcherPriority.Normal, (_, _) => EdgeTurn(), ui);
        _gapTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(GridDrop.GapDwellMs), DispatcherPriority.Normal, (_, _) => Rest(), ui);
        // The places moved by a menu's item have slid there: they stop sliding (unless one is carried meanwhile).
        _moveEnd = new DispatcherTimer(TimeSpan.FromMilliseconds(AppGridScene.MoveMs + 60), DispatcherPriority.Normal, (_, _) =>
        {
            _moveEnd!.Stop();
            if (_carried == null && _flying < 0) _scene?.Moving(false);
        }, ui);
    }

    Material Material(ThemeSnapshot t) => Materials.AppGrid(t, _settings.Appearance);

    public void ApplyTheme(ThemeSnapshot t) => _window?.SetMaterial(Material(t));

    /// <summary>The Start menu's apps (names and AppsFolder parsing names); the grid keeps what it can of its pictures.</summary>
    public void SetApps(IEnumerable<(string Name, string ParsingName)> apps)
    {
        var arranged = AppGridModel.Arrange(apps.Select(a => new GridApp(a.Name, a.ParsingName)), CultureInfo.CurrentUICulture);
        if (arranged.SequenceEqual(_apps)) return;
        Log.Info($"app grid: {arranged.Count} apps");
        if (_carried != null) CancelPointer();
        _menu.Close(); // its app's index changes
        CloseFolder(animate: false);
        _apps = arranged;
        _names = arranged.Select(a => new Candidate(a.Name)).ToList();
        _tiles = GridArrangement.Tiles(_apps, _settings.AppGrid.Arrangement, S["grid.hidden"]);
        if (_scene == null) return;
        _scene.SetApps(_apps, Icon);
        _scene.SetFolders(_tiles);
        Filter();
        QueueLabels();
    }

    ImageSource? Icon(GridApp app) => _icons?.Get("grid:" + app.ParsingName, @"shell:AppsFolder\" + app.ParsingName);

    /// <summary>
    /// Where the grid goes (DIPs): down to the bottom of the screen, under the Dock; and how much of it is above the
    /// Dock's strip (the work area's bottom: also above Explorer's taskbar while it shows, as in development runs),
    /// where the grid itself stays.
    /// </summary>
    (Rect area, double above) Area()
    {
        _mon = Monitors.Get(_mon.Handle) ?? _mon; // its work area now (the bars' reservations come after start-up)
        var bar = _barRect();
        double s = _mon.Scale;
        int top = bar.Bottom > _mon.Bounds.Top && bar.Bottom < _mon.Bounds.Bottom ? bar.Bottom : _mon.Bounds.Top;
        var area = new Rect(_mon.Bounds.Left / s, top / s, _mon.Bounds.Width / s, (_mon.Bounds.Bottom - top) / s);
        return (area, Math.Clamp((_mon.Work.Bottom - top) / s, area.Height / 2, area.Height));
    }

    /// <summary>The icon size for <paramref name="columns"/> × <paramref name="rows"/> on this screen: left to fit, and the largest that fits.</summary>
    public (int auto, int fits) IconSizes(int columns, int rows)
    {
        var (area, above) = Area();
        return ((int)GridGeometry.Compute(area.Width, above, columns, rows).IconSize,
                (int)GridGeometry.Compute(area.Width, above, columns, rows, GridGeometry.MaxIcon).IconSize);
    }

    /// <summary>The window, its pictures and the icon loader, made once (and again on another monitor or scale).</summary>
    void Ensure()
    {
        var (area, above) = Area();
        double s = _mon.Scale;
        var g = _settings.AppGrid;
        var layout = GridGeometry.Compute(area.Width, above, g.Columns, g.Rows, g.IconSize);
        if (_window != null && area == _area && layout == _layout) return;
        if (_window != null && (int)Math.Round(layout.IconSize * s) > _iconPx)
        {
            // Larger icons than were read: everything again, the icons read at the new size (as the Dock does).
            Log.Info($"app grid: icons of {layout.IconSize} DIP: made again with icons read at that size");
            Release();
            Ensure();
            return;
        }
        if (_carried != null) CancelPointer();
        CloseFolder(animate: false); // laid out for the old cells
        _area = area;
        _layout = layout;
        if (_window == null)
        {
            var sw = Stopwatch.StartNew();
            _window = new AppGridWindow(Material(_theme.Current), _strings()["search.placeholder"]);
            _window.QueryChanged += Filter;
            _window.PreviewKeyDown += OnKey;
            // The menu key opened the place's menu: its release is not left to open the search field's own menu (WPF opens
            // a focused field's menu on that release as its source reads; not seen here).
            _window.PreviewKeyUp += (_, e) => { if (!_renaming && MenuKey(e)) e.Handled = true; };
            _window.MouseLeftButtonDown += (_, e) => PointerDown(e.GetPosition(_window));
            _window.MouseMove += (_, e) => PointerMove(e.GetPosition(_window), e.LeftButton == MouseButtonState.Pressed);
            _window.MouseLeftButtonUp += (_, e) => PointerUp(e.GetPosition(_window));
            _window.MouseRightButtonUp += (_, e) =>
            {
                // On the search field or the folder's name: their own cut, copy and paste menu (ShellStyles). Handled
                // here, WPF opened no menu for them.
                if (InTextBox(e.OriginalSource as DependencyObject)) return;
                e.Handled = true;
                MenuAt(e.GetPosition(_window));
            };
            _window.LostMouseCapture += (_, _) => { if (_down != null) CancelPointer(); };
            _window.MouseWheel += (_, e) => Turn(_wheel.Feed(-e.Delta, Environment.TickCount64));
            _window.HorizontalWheel += d => Turn(_wheel.Feed(d, Environment.TickCount64));
            _window.Deactivated += (_, _) => { if (!_closing) Close(restore: false); }; // another window came forward: it keeps it
            _window.NameLeft += () =>
            {
                Log.Debug($"app grid: the name's box lost the keyboard (to {Keyboard.FocusedElement?.GetType().Name ?? "nothing"}; the grid {(Native.GetForegroundWindow() == _window?.Handle ? "in front" : "not in front")})");
                EndRename(commit: true);
            };
            new System.Windows.Interop.WindowInteropHelper(_window).EnsureHandle(); // placed when it opens
            _surfaces = SurfaceFactory.Shared(_mon); // the Dock's: one GPU device for both
            _iconPx = (int)Math.Round(_layout.IconSize * s);
            _icons = new IconService(_ui, _iconPx);
            _icons.Loaded += _ => { _iconsLoaded.Stop(); _iconsLoaded.Start(); };
            _scene = new AppGridScene(_surfaces, _window.Overlay, _layout, s, area.Width, area.Height);
            _scene.SetApps(_apps, Icon);
            _scene.SetFolders(_tiles);
            Filter();
            Log.Debug($"app grid: window and pictures made in {sw.ElapsedMilliseconds} ms ({_apps.Count} apps, {_tiles.Count} places, icons {_layout.IconSize} DIP, {_scene.Pages} pages)");
            QueueLabels();
            return;
        }
        // Same scale (a new scale makes everything again: see MoveTo), another size.
        Log.Debug($"app grid: laid out again for {area.Width}×{area.Height} DIP ({above} above the Dock)");
        _scene!.SetLayout(_layout, s, area.Width, area.Height);
        Filter();
        QueueLabels();
    }

    /// <summary>Draws the names not drawn yet a few at a time while the UI thread is idle.</summary>
    void QueueLabels()
    {
        if (_labelsQueued || _scene == null) return;
        _labelsQueued = true;
        _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            _labelsQueued = false;
            if (_scene == null) return;
            int left = _scene.MakeSomeLabels(12);
            if (left > 0) QueueLabels();
            else Log.Debug($"app grid: all names drawn (last batch: {_scene.LastLabels.count} in {_scene.LastLabels.ms:0.0} ms)");
        });
    }

    /// <summary>Makes the window and its pictures once after start-up, so the first opening does not pay for them.</summary>
    public void Warm() => Ensure();

    int _iconPx;

    /// <summary>
    /// The grid's settings changed (icon size, columns, rows): laid out again now if the grid was made (closing it if
    /// open), otherwise at its first opening.
    /// </summary>
    public void SettingsChanged()
    {
        if (_window == null) return;
        if (IsOpen) Close(restore: false);
        Ensure();
        _page = Math.Clamp(_page, 0, Math.Max(0, Pages - 1));
    }

    /// <summary>
    /// The displays changed: an open grid closes (the screen changed under it). On another monitor or at another scale
    /// everything is made again at the next opening (its pictures are on the GPU of the monitor it was made for);
    /// otherwise it is laid out again for the new size when it opens.
    /// </summary>
    public void MoveTo(MonitorInfo mon)
    {
        bool remake = mon.Dpi != _mon.Dpi || !string.Equals(mon.Device, _mon.Device, StringComparison.OrdinalIgnoreCase);
        _mon = mon;
        if (remake || IsOpen) Close(restore: false);
        if (remake) Release();
    }

    public void Toggle(int pressTime = 0)
    {
        if (IsOpen) Close(restore: true); else Open(pressTime);
    }

    /// <param name="pressTime">When the opening click or key came (TickCount ms), to time the opening; 0: not timed.</param>
    public void Open(int pressTime = 0)
    {
        var sw = Stopwatch.StartNew();
        int queued = pressTime != 0 ? Math.Max(0, Environment.TickCount - pressTime) : 0;
        if (_closing) FinishClosing(hide: false); // asked again while fading out: it comes back
        Ensure();
        var w = _window!;
        var fg = Native.GetForegroundWindow();
        if (fg != w.Handle) _previous = fg;
        _surfaces!.CheckDevice();
        CancelPointer();
        CloseFolder(animate: false);
        w.Field.Clear();
        Filter(); // the arrangement (also when an IME composition was cut off by the last closing)
        _scene!.GoTo(_page, animate: false);
        _scene.Opening(_theme.Current.Animations);
        w.FadeIn(_theme.Current.Animations ? AppGridScene.OpenMs : 0); // the blur comes with the pictures
        w.Place(_area, _layout!);
        w.Show();
        w.Activate();
        if (Native.GetForegroundWindow() != w.Handle) WindowActions.ForceForeground(w.Handle);
        w.Field.Focus();
        Keyboard.Focus(w.Field);
        // Showing and activating put the grid at the top of the topmost windows, and its backdrop puts it there again
        // once the layout has settled: the Dock goes back above it now and after that.
        _raiseDock();
        _ui.BeginInvoke(DispatcherPriority.Background, () => { if (IsOpen) _raiseDock(); });
        LastOpenMs = -1;
        if (pressTime != 0)
        {
            EventHandler? frame = null;
            frame = (_, _) =>
            {
                CompositionTarget.Rendering -= frame;
                LastOpenMs = queued + (int)sw.ElapsedMilliseconds;
            };
            CompositionTarget.Rendering += frame;
        }
        Log.Debug($"app grid opened in {sw.ElapsedMilliseconds} ms (page {_page + 1} of {_scene.Pages})");
        Opened?.Invoke();
    }

    /// <summary>
    /// Closes the grid: the pictures grow a little and fade with the blur (an open folder with them), then the window
    /// hides (at once without animations). With <paramref name="restore"/> the app that was in front gets the foreground
    /// back at once, so typing goes there while the grid fades.
    /// </summary>
    public void Close(bool restore)
    {
        if (_window == null || !_window.IsVisible || _closing) return;
        _closing = true;
        _menu.Close();
        EndRename(commit: true);
        CancelPointer();
        if (_flying >= 0) Landed(); // no picture left flying over a grid that goes
        _window.IsHitTestVisible = false;
        if (restore && _previous != IntPtr.Zero && Native.IsWindow(_previous)) WindowActions.ForceForeground(_previous);
        if (!Animations || _scene == null) { FinishClosing(); return; }
        // Fading, it lets presses through to what is behind it (a click into the app just opened was lost on it).
        Fades.PassPresses(_window);
        _scene.Closing();
        _window.FadeOut(AppGridScene.CloseMs);
        _closeTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(AppGridScene.CloseMs + 30), DispatcherPriority.Normal, (_, _) => FinishClosing(), _ui);
        _closeTimer.Stop();
        _closeTimer.Start();
    }

    /// <summary>The end of a closing: hidden and small again (or, <paramref name="hide"/> false, back as it was: reopened).</summary>
    void FinishClosing(bool hide = true)
    {
        _closeTimer?.Stop();
        if (_window != null)
        {
            if (hide)
            {
                CloseFolder(animate: false); // it faded with the rest
                _window.Hide();
                _window.Shrink();
            }
            _window.IsHitTestVisible = true;
            Fades.TakePresses(_window);
            _window.FadeIn(0);
        }
        if (hide) _scene?.Hidden(); else _scene?.Opening(false);
        _closing = false;
    }

    int Pages => _scene?.Pages ?? 1;
    int PerPage => _layout?.PerPage ?? 35;
    bool Animations => _theme.Current.Animations;
    Strings S => _strings();

    void GoTo(int page, bool animate = true)
    {
        if (_scene == null) return;
        _page = Math.Clamp(page, 0, Pages - 1);
        _scene.GoTo(_page, animate && Animations);
    }

    void GoToFolderPage(int page, bool animate = true) => _scene?.GoToFolderPage(page, animate && Animations);

    void Turn(int step)
    {
        if (step == 0 || _down != null) return;
        if (_openKey != null) GoToFolderPage(_scene!.FolderPage + step);
        else GoTo(_page + step);
    }

    // ---- the search field ---------------------------------------------------------------------------------------

    void Filter()
    {
        if (_scene == null) return;
        if (_carried != null) PutDown(drop: false, animate: false); // the places move: where it was is gone
        string q = TextNormalizer.Normalize(_window?.Field.Text ?? "");
        if (q.Length > 0) CloseFolder(animate: false); // typing searches every app, folders opened up
        bool was = _query.Length > 0;
        if (q.Length > 0 && !was) _pageBeforeSearch = _page;
        _query = q;
        _shown = q.Length == 0 ? _tiles : Found(q);
        _scene.Arrange(_shown);
        if (q.Length > 0)
        {
            _page = 0;
            _selection = _shown.Count > 0 ? 0 : -1;
        }
        else
        {
            if (was) _page = _pageBeforeSearch;
            _selection = -1;
        }
        _page = Math.Clamp(_page, 0, Pages - 1);
        _scene.GoTo(_page, animate: false);
        _scene.Select(_selection);
    }

    static bool MenuKey(KeyEventArgs e) => e.Key == Key.Apps || e.Key == Key.System && e.SystemKey == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift;

    /// <summary>The apps that answer <paramref name="q"/>, best first, the hidden ones after the others (the user's choice, 2026-10-24).</summary>
    List<GridTile> Found(string q) =>
        AppGridModel.Filter(_names, q, GridArrangement.HiddenApps(_tiles)).Select(i => GridTile.ForApp(i, _apps[i].ParsingName)).ToList();

    void Choose(int position)
    {
        _selection = position;
        _scene?.Select(position);
        if (position >= 0 && position / PerPage != _page) GoTo(position / PerPage);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_window == null || e.Key == Key.ImeProcessed) return; // the IME's own keys
        if (_renaming)
        {
            if (e.Key == Key.Enter) { e.Handled = true; EndRename(commit: true); }
            else if (e.Key == Key.Escape) { e.Handled = true; EndRename(commit: false); }
            return; // the rest types the name
        }
        if (_window.Composing) return;
        // The menu key (or Shift+F10) opens the chosen app's menu, by its icon.
        if (MenuKey(e) && _carried == null)
        {
            e.Handled = true;
            if (_selection >= 0 && _selection < _shown.Count && _scene?.ItemBoxes(_selection) is { } chosen)
                ShowMenu(_shown[_selection], new Point(chosen.icon.X + chosen.icon.Width / 2, chosen.icon.Y + chosen.icon.Height / 2));
            return;
        }
        bool searching = _query.Length > 0;
        int columns = _layout?.Columns ?? 7;
        switch (e.Key)
        {
            case Key.Escape when _carried != null:
                e.Handled = true;
                CancelPointer(); // the carried app goes back; the grid stays
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Enter when _carried != null:
                e.Handled = true; // the page stays under the app being carried
                break;
            case Key.Escape when _openKey != null:
                e.Handled = true;
                CloseFolder(animate: true);
                break;
            case Key.Left or Key.Right or Key.PageUp or Key.PageDown when _openKey != null:
                e.Handled = true;
                GoToFolderPage(_scene!.FolderPage + (e.Key is Key.Left or Key.PageUp ? -1 : 1));
                break;
            case Key.Escape:
                e.Handled = true;
                if (_window.Field.Text.Length > 0) _window.Field.Clear(); else Close(restore: true);
                break;
            case Key.Left or Key.Right:
                e.Handled = true;
                int dx = e.Key == Key.Left ? -1 : 1;
                if (searching) Choose(GridPaging.Move(_selection, dx, 0, columns, _shown.Count));
                else GoTo(_page + dx);
                break;
            case Key.Up or Key.Down when searching:
                e.Handled = true;
                Choose(GridPaging.Move(_selection, 0, e.Key == Key.Up ? -1 : 1, columns, _shown.Count));
                break;
            case Key.PageUp or Key.PageDown:
                e.Handled = true;
                GoTo(_page + (e.Key == Key.PageUp ? -1 : 1));
                break;
            case Key.Enter:
                e.Handled = true;
                if (_selection >= 0 && _selection < _shown.Count) Activate(_selection);
                break;
            case Key.Tab:
                e.Handled = true; // the field keeps the keyboard
                break;
        }
    }

    // ---- the pointer --------------------------------------------------------------------------------------------

    /// <summary>The position (in the shown order) of the place under <paramref name="p"/> on this page, or -1.</summary>
    int PositionAt(Point p)
    {
        if (_layout == null) return -1;
        int onPage = Math.Clamp(_shown.Count - _page * PerPage, 0, PerPage);
        int i = GridGeometry.HitTest(_layout, p.X, p.Y, onPage);
        return i < 0 ? -1 : _page * PerPage + i;
    }

    /// <summary>The position of the open folder's app under <paramref name="p"/> (among its apps), or -1.</summary>
    int MemberAt(Point p)
    {
        if (_folderLayout is not { } f || _scene == null || OpenFolderIndex() is not (>= 0 and var fi)) return -1;
        int per = f.Apps.PerPage, page = _scene.FolderPage;
        int i = GridGeometry.HitTest(f.Apps, p.X, p.Y, Math.Clamp(_tiles[fi].Apps.Count - page * per, 0, per));
        return i < 0 ? -1 : page * per + i;
    }

    int OpenFolderIndex() => _openKey == null ? -1 : _tiles.FindIndex(t => t.IsFolder && t.Key == _openKey);

    static int FolderIndex(IReadOnlyList<GridTile> tiles, string? key) => key == null ? -1 : tiles.ToList().FindIndex(t => t.IsFolder && t.Key == key);

    static bool Inside(Box b, Point p) => b.Contains(p.X, p.Y);

    /// <summary>On the open folder's name as it is drawn (and a little around it), not anywhere above the panel.</summary>
    bool OnTitle(FolderLayout f, Point p)
    {
        double half = Math.Max(80, (_scene?.TitleWidth ?? 0) + 24) / 2, centre = f.Title.X + f.Title.Width / 2;
        return p.Y >= f.Title.Y && p.Y < f.Title.Bottom && Math.Abs(p.X - centre) <= half;
    }

    void PointerDown(Point p)
    {
        if (_window == null || _scene == null) return;
        if (_flying >= 0) Landed(); // a picture still flying is home at once
        EndRename(commit: true);    // a click elsewhere keeps the name (one in the name's box does not come here)
        _down = p;
        _dragging = false;
        _moves.Clear();
        _moves.Add((Environment.TickCount64, p.X));
        _downAt = -1;
        _scene.Gives = Animations && !ReduceMotion.Calms("press"); // the icon pressed sinks and springs back (2026-10-23)
        if (_openKey != null && _folderLayout is { } f)
        {
            _downAt = MemberAt(p);
            _downOn = _downAt >= 0 ? Spot.Member : OnTitle(f, p) ? Spot.Title : Inside(f.Panel, p) ? Spot.Panel : Spot.Outside;
            _scene.PressMember(_downAt);
        }
        else
        {
            _downAt = PositionAt(p);
            _downOn = _downAt >= 0 ? Spot.Tile : Spot.None;
            _scene.Press(_downAt);
        }
        _window.CaptureMouse();
    }

    void PointerMove(Point p, bool pressed)
    {
        if (_down is not { } d || _scene == null) return;
        if (!pressed) { CancelPointer(); return; }
        if (_carried != null) { Carry(p); return; }
        double dx = p.X - d.X, dy = p.Y - d.Y;
        bool moved = Math.Max(Math.Abs(dx), Math.Abs(dy)) > DragStart;
        // A press on a place that moves picks it up (Launchpad); one on empty space drags the pages, and so does one on
        // the hidden apps' folder, which stays last.
        if (!_dragging && moved && _downOn == Spot.Tile && _downAt < _shown.Count && _shown[_downAt].IsHidden)
        {
            _downOn = Spot.None;
            _downAt = -1;
        }
        if (!_dragging && moved && _downOn == Spot.Tile) { PickUp(d, p); return; }
        if (!_dragging && moved && _downOn == Spot.Member) { PickUpMember(d, p); return; }
        bool pages = _downOn == Spot.None || _downOn == Spot.Panel && _scene.FolderPages > 1;
        if (!_dragging && pages && Math.Abs(dx) > DragStart && Math.Abs(dx) > Math.Abs(dy))
        {
            _dragging = true;
            _scene.Press(-1);
        }
        if (_dragging)
        {
            if (_downOn == Spot.Panel) _scene.FolderFollow(GridPaging.Follow(_scene.FolderPage, _scene.FolderPages, dx));
            else _scene.Follow(GridPaging.Follow(_page, Pages, dx));
        }
        long now = Environment.TickCount64;
        _moves.Add((now, p.X));
        _moves.RemoveAll(m => now - m.ms > 100);
    }

    void PointerUp(Point p)
    {
        if (_down is not { } d || _scene == null) return;
        if (_carried != null) { PutDown(drop: true); return; }
        var on = _downOn;
        int downAt = _downAt;
        bool dragged = _dragging;
        double velocity = Velocity(p.X);
        EndPress();
        if (dragged)
        {
            if (on == Spot.Panel && _folderLayout is { } f) GoToFolderPage(GridPaging.AfterDrag(_scene.FolderPage, _scene.FolderPages, p.X - d.X, velocity, f.Panel.Width));
            else GoTo(GridPaging.AfterDrag(_page, Pages, p.X - d.X, velocity, _area.Width));
            return;
        }
        switch (on)
        {
            case Spot.Member:
                if (MemberAt(p) == downAt && OpenFolderIndex() is (>= 0 and var fi)) Launch(_tiles[fi].Apps[downAt]);
                return;
            case Spot.Title:
                if (_folderLayout is { } t && OnTitle(t, p)) BeginRename();
                return;
            case Spot.Panel:
                if (_scene.FolderDotAt(p.X, p.Y) is int fd and >= 0) GoToFolderPage(fd);
                return;
            case Spot.Outside:
                CloseFolder(animate: true); // a click beside the folder (Launchpad)
                return;
            case Spot.Tile:
                if (PositionAt(p) == downAt) Activate(downAt);
                return;
        }
        if (_scene.DotAt(p.X, p.Y) is int dot and >= 0) { GoTo(dot); return; }
        if (_window!.FieldRect.Contains(p) || PositionAt(p) >= 0) return;
        Close(restore: true); // a click on empty space (Launchpad)
    }

    /// <summary>The place at <paramref name="position"/> chosen: an app opens, a folder opens on its panel.</summary>
    void Activate(int position)
    {
        if (position < 0 || position >= _shown.Count) return;
        var t = _shown[position];
        if (t.IsFolder) OpenFolder(t.Key);
        else Launch(t.App);
    }

    /// <summary>The pointer's speed over the last ~100 ms (DIPs per ms; negative: leftwards).</summary>
    double Velocity(double x)
    {
        long now = Environment.TickCount64;
        var first = _moves.FirstOrDefault(m => now - m.ms <= 100);
        return first.ms == 0 || now == first.ms ? 0 : (x - first.x) / (now - first.ms);
    }

    /// <summary>Forgets the press (the shade goes, the capture is released).</summary>
    void EndPress()
    {
        _down = null; // first: releasing the capture raises LostMouseCapture
        _downAt = -1;
        _downOn = Spot.None;
        _dragging = false;
        _scene?.Press(-1);
        _scene?.PressMember(-1);
        if (_window?.IsMouseCaptured == true) _window.ReleaseMouseCapture();
    }

    /// <summary>
    /// A press that ends without a release (capture lost, Esc, the grid closing): dragged pages go back into place, a
    /// carried place back to where it was.
    /// </summary>
    void CancelPointer()
    {
        bool wasDragging = _dragging;
        var on = _downOn;
        if (_carried != null) PutDown(drop: false, animate: !_closing);
        EndPress();
        if (!wasDragging) return;
        if (on == Spot.Panel) GoToFolderPage(_scene?.FolderPage ?? 0);
        else GoTo(_page);
    }

    // ---- carrying ------------------------------------------------------------------------------------------------

    /// <summary>The pressed place is picked up: its picture follows the pointer, held where it was taken.</summary>
    void PickUp(Point down, Point at)
    {
        if (_scene?.ItemBoxes(_downAt) is not { } boxes) return;
        _carried = _shown[_downAt];
        _fromFolder = null;
        _fromAt = _downAt;
        // A search's apps are not the arrangement: nothing makes room there (the Dock still takes the app).
        _reorder = _query.Length == 0;
        _rest = _reorder ? GridArrangement.Without(_tiles, _downAt) : new();
        _gap = _downAt;
        _inFolder = false;
        _picture = _carried;
        Lift(boxes.icon, down);
        _scene.Press(-1);
        _scene.Faint(_gap);
        Log.Debug($"app grid: carrying {Describe(_carried)}");
        Carry(at);
    }

    /// <summary>The pressed app of the open folder is picked up: it can move among the folder's apps or be taken out.</summary>
    void PickUpMember(Point down, Point at)
    {
        if (_scene?.FolderItemBoxes(_downAt) is not { } boxes || OpenFolderIndex() is not (>= 0 and var fi)) return;
        int app = _tiles[fi].Apps[_downAt];
        _carried = GridTile.ForApp(app, _apps[app].ParsingName);
        _fromFolder = _openKey;
        _fromAt = _downAt;
        _reorder = true;
        _rest = GridArrangement.WithoutMember(_tiles, fi, _downAt);
        _inFolder = true;
        _folderGap = _downAt;
        _gap = -1;
        _picture = _carried;
        Lift(boxes.icon, down);
        _scene.PressMember(-1);
        _scene.FaintMember(_folderGap);
        Log.Debug($"app grid: carrying {Describe(_carried)} in its folder");
        Carry(at);
    }

    void Lift(Box icon, Point down)
    {
        _grab = new Vector(icon.X + icon.Width / 2 - down.X, icon.Y + icon.Height / 2 - down.Y);
        _overShelf = false;
        _over = _into = -1;
        _edgeSide = 0;
        _pendingGap = NoGap;
        _scene!.Moving(true);
    }

    string Describe(GridTile t) => t.IsFolder ? $"the folder {Log.Redact(t.FolderName!)}" : Log.Redact(_apps[t.App].Name);

    /// <summary>The carried picture to the pointer; the Dock is told where the app is; the places make room for it.</summary>
    void Carry(Point p)
    {
        if (_carried == null || !Native.GetCursorPos(out var c)) return;
        double s = _mon.Scale;
        _carriedAt = new POINT { X = c.X + (int)Math.Round(_grab.X * s), Y = c.Y + (int)Math.Round(_grab.Y * s) };
        ShowCarried(_carriedAt);
        // The Dock keeps apps, not folders.
        bool over = !_carried.IsFolder && Shelf()?.Carry(_apps[_carried.App].ParsingName, c.X, c.Y) == true;
        if (over != _overShelf) Log.Debug($"app grid: the carried app is {(over ? "over a place in the Dock" : "away from the Dock")}");
        _overShelf = over;
        if (!_reorder || _layout == null) return;
        if (_inFolder) { CarryInFolder(p); return; }
        if (over)
        {
            // Kept in the Dock, it stays where it was in the grid: shown there (an app of a folder, in its folder).
            Pending(NoGap);
            MoveGap(_fromFolder == null ? _fromAt : -1);
            Over(-1);
            Edge(0);
            return;
        }
        // Without a gap (back from the Dock) the places show in order, as with a gap after the last.
        int current = _gap >= 0 ? _gap : _rest.Count;
        // Never after the hidden apps' folder; resting on it, an app goes in (hidden).
        var (gap, under) = GridDrop.Target(_layout, p.X, p.Y, _page, current, _rest.Count, into: !_carried.IsFolder, room: GridArrangement.Room(_rest));
        if (_gap < 0 && GridDrop.CellAt(_layout, p.X, p.Y) < 0) gap = -1; // not over a cell yet
        Over(under);
        // The places make room once the app rests in a cell a moment (Launchpad): passing over a place's edge on the way
        // to its middle does not push it away.
        Pending(under < 0 && gap != _gap ? gap : NoGap);
        // Not over the Dock: one on a side stands in the margin that turns the page.
        Edge(Shelf()?.Covers(c.X, c.Y) == true ? 0 : GridDrop.Edge(_layout, p.X, _area.Width));
    }

    const int NoGap = int.MinValue;
    int _pendingGap = NoGap;

    /// <summary>The gap moves to <paramref name="gap"/> once the carried app rests there (<see cref="NoGap"/>: it stays).</summary>
    void Pending(int gap)
    {
        if (gap == _pendingGap) return;
        _pendingGap = gap;
        _gapTimer.Stop();
        if (gap != NoGap) _gapTimer.Start();
    }

    void Rest()
    {
        _gapTimer.Stop();
        int gap = _pendingGap;
        _pendingGap = NoGap;
        if (gap != NoGap && _carried != null && !_inFolder) MoveGap(gap);
    }

    /// <summary>The carried app over the open folder: a gap among its apps, or out of the panel and over the grid.</summary>
    void CarryInFolder(Point p)
    {
        if (_folderLayout is not { } f || FolderIndex(_rest, _fromFolder) is not (>= 0 and var fi)) return;
        if (p.X < f.Panel.X - FolderLeave || p.X > f.Panel.Right + FolderLeave || p.Y < f.Title.Y - FolderLeave || p.Y > f.Panel.Bottom + FolderLeave)
        {
            LeaveFolder(fi, p);
            return;
        }
        var apps = _rest[fi].Apps;
        var (gap, _) = GridDrop.Target(f.Apps, p.X, p.Y, _scene!.FolderPage, _folderGap, apps.Count, into: false);
        if (gap != _folderGap)
        {
            _folderGap = gap;
            _scene.ArrangeFolder([.. apps.Take(gap), _carried!.App, .. apps.Skip(gap)], faint: gap);
        }
        Edge(p.X < f.Panel.X + FolderSide ? -1 : p.X >= f.Panel.Right - FolderSide ? 1 : 0);
    }

    /// <summary>
    /// The carried app went out of the open folder's panel: the folder closes (Launchpad) and the app is over the grid,
    /// its gap after the folder, which shows without it.
    /// </summary>
    void LeaveFolder(int folder, Point p)
    {
        Log.Debug($"app grid: {Describe(_carried!)} carried out of its folder");
        _inFolder = false;
        Edge(0);
        Pending(NoGap);
        _openKey = null;
        _folderLayout = null;
        _window?.DimField(false);
        _scene!.SetFolders(_rest);
        _gap = -2; // shown anew below
        // Its apps go back to the grid's layer once the panel has faded: the arrangement is shown again then.
        _scene.Close(Animations, () => { if (_carried != null && !_inFolder) _scene?.Arrange(_shown, _gap); });
        MoveGap(Math.Min(folder + 1, GridArrangement.Room(_rest))); // out of the hidden apps' folder: before it
        Carry(p);
    }

    /// <summary>The carried place would land at <paramref name="gap"/> among the others (-1: nowhere in the grid).</summary>
    void MoveGap(int gap)
    {
        if (gap == _gap || _carried == null || _scene == null) return;
        _gap = gap;
        _shown = gap < 0 ? _rest : GridArrangement.Insert(_rest, gap, _carried);
        _scene.Arrange(_shown, gap);
        if (_page >= Pages) GoTo(Pages - 1);
        if (_into >= 0) _scene.Into(Shown(_into));
    }

    /// <summary>Where a place of <see cref="_rest"/> shows with the gap.</summary>
    int Shown(int rest) => _gap >= 0 && rest >= _gap ? rest + 1 : rest;

    /// <summary>The place whose middle is under the pointer (-1: none): resting there puts the carried app in.</summary>
    void Over(int under)
    {
        if (under == _over) return;
        _over = under;
        _into = -1;
        _scene?.Into(-1);
        _intoTimer.Stop();
        if (under >= 0) _intoTimer.Start();
    }

    /// <summary>The carried app rested on a place's middle: let go now, it goes into it (a folder made or filled).</summary>
    void Rested()
    {
        _intoTimer.Stop();
        if (_carried == null || _over < 0 || _inFolder) return;
        _into = _over;
        _scene?.Into(Shown(_into));
        Log.Debug($"app grid: let go now, {Describe(_carried)} goes into {Describe(_rest[_into])}");
    }

    /// <summary>The carried app at a side (-1, 1) of the grid or of the open folder (0: not): resting there turns the page.</summary>
    void Edge(int side)
    {
        if (side == _edgeSide) return;
        _edgeSide = side;
        _edgeTimer.Stop();
        if (side == 0) return;
        _edgeTimer.Interval = TimeSpan.FromMilliseconds(GridDrop.EdgeDwellMs);
        _edgeTimer.Start();
    }

    void EdgeTurn()
    {
        if (_carried == null || _edgeSide == 0 || _scene == null) { _edgeTimer.Stop(); return; }
        _edgeTimer.Interval = TimeSpan.FromMilliseconds(GridDrop.EdgeRepeatMs); // it keeps turning while the app stays
        if (_inFolder)
        {
            int to = _scene.FolderPage + _edgeSide;
            if (to < 0 || to >= _scene.FolderPages) return;
            GoToFolderPage(to);
            Log.Debug($"app grid: the folder turned to page {to + 1} under the carried app");
            return;
        }
        int page = _page + _edgeSide;
        if (page < 0 || page >= Pages) return;
        GoTo(page);
        Log.Debug($"app grid: turned to page {page + 1} under the carried app");
    }

    void ShowCarried(POINT centre) => ShowCarried(centre.X, centre.Y);

    void ShowCarried(double x, double y)
    {
        if (_scene == null || _layout == null) return;
        if (_picture == null || _scene.IconOf(_picture) is not { } picture) return;
        _carryImage ??= new Dock.DragImage();
        _carryImage.Show(picture, (float)Math.Round(_layout.IconSize * _mon.Scale), x, y, 1f);
    }

    /// <summary>
    /// The carried place is let go (<paramref name="drop"/>; false: cancelled): kept in the Dock when it was over a place
    /// there; otherwise where its gap was, or into the place it rested on. Its picture flies there (at once without
    /// <paramref name="animate"/>); cancelled, back to where it was.
    /// </summary>
    void PutDown(bool drop, bool animate = true)
    {
        if (_carried is not { } carried || _scene == null) { EndPress(); return; }
        bool kept = false;
        if (!carried.IsFolder)
        {
            var app = _apps[carried.App];
            var shelf = Shelf();
            // Before the capture goes (the pointer is over the Dock, which would take the pointer's moves again).
            if (drop && shelf != null && Native.GetCursorPos(out var c))
                kept = shelf.Drop(app.ParsingName, app.Name, c.X, c.Y, _carriedAt.X, _carriedAt.Y, _layout != null ? Math.Round(_layout.IconSize * _mon.Scale) : 0);
            else shelf?.CarryAway();
        }
        // Let go where it is: a gap waiting for the app to rest goes there now (it is where the pointer is).
        if (drop && !kept && _pendingGap != NoGap && !_inFolder && _into < 0) Rest();
        _gapTimer.Stop();
        _pendingGap = NoGap;
        _intoTimer.Stop();
        _edgeTimer.Stop();
        _scene.Into(-1);
        List<GridTile>? next = null;
        int land = -1;
        bool landInFolder = false;
        if (drop && !kept && _reorder)
        {
            if (_inFolder && FolderIndex(_rest, _fromFolder) is (>= 0 and var fi))
            {
                next = GridArrangement.InsertMember(_rest, fi, _folderGap, carried.App);
                land = _folderGap;
                landInFolder = true;
            }
            else if (!_inFolder && _into >= 0 && _over == _into && !carried.IsFolder)
            {
                var target = _rest[_into];
                string name = target.IsFolder ? target.FolderName! : GridArrangement.FolderName(_apps[target.App].Name, _apps[carried.App].Name, S["grid.folderUntitled"]);
                next = GridArrangement.Merge(_rest, _into, carried.App, GridPlace.NewFolderId(), name);
                land = _into;
            }
            else if (!_inFolder && _gap >= 0)
            {
                next = GridArrangement.Insert(_rest, _gap, carried);
                land = _gap;
            }
            if (next != null && _fromFolder != null && !landInFolder) next = GridArrangement.Settle(next, _fromFolder, _apps);
        }
        bool wasInFolder = _inFolder;
        string? from = _fromFolder;
        int fromAt = _fromAt;
        _carried = null;
        _overShelf = false;
        _over = _into = -1;
        _inFolder = false;
        _edgeSide = 0;
        EndPress();
        if (next != null)
        {
            string what = landInFolder ? "moved in its folder"
                : land >= 0 && land < next.Count && next[land].IsFolder && !carried.IsFolder && next[land].Apps.Contains(carried.App) ? next[land].IsHidden ? "hidden" : "put in a folder"
                : from == GridPlace.HiddenId ? "shown again" : "moved";
            Log.Debug($"app grid: {Describe(carried)} {what}");
            _tiles = next;
            Save();
        }
        else
        {
            // As it was: back to where it came from (kept in the Dock, cancelled, let go nowhere).
            if (wasInFolder) { landInFolder = true; land = fromAt; }
            else if (!_reorder) land = fromAt;
            else land = from == null ? fromAt : FolderIndex(_tiles, from);
            Log.Debug($"app grid: {Describe(carried)} {(kept ? "kept in the Dock" : drop ? "let go where nothing changes" : "no longer carried")}");
        }
        _scene.SetFolders(_tiles);
        if (_query.Length == 0) _shown = _tiles;
        _scene.Arrange(_shown, landInFolder ? -1 : land);
        // Arranging lights nothing: the search's chosen app lit again (Enter still opened it, unlit).
        if (_selection >= _shown.Count) _selection = _shown.Count - 1;
        _scene.Select(_selection);
        if (landInFolder && OpenFolderIndex() is (>= 0 and var open)) _scene.ArrangeFolder(_tiles[open].Apps, land);
        _flying = land;
        _flyingInFolder = landInFolder;
        if (kept || !animate || !Animations || land < 0) Landed(); // kept: it grows into the Dock's gap
        else FlyTo(land, landInFolder);
    }

    bool _flyingInFolder;

    /// <summary>The carried picture flies to the place at <paramref name="position"/> (quick to leave, gentle to settle).</summary>
    void FlyTo(int position, bool inFolder)
    {
        if (_carryImage == null || _scene == null || _layout == null) { Landed(); return; }
        Box icon;
        if (inFolder)
        {
            if (_folderLayout is not { } f) { Landed(); return; }
            if (position / f.Apps.PerPage != _scene.FolderPage) GoToFolderPage(position / f.Apps.PerPage);
            icon = GridGeometry.Item(f.Apps, position % f.Apps.PerPage).icon;
        }
        else
        {
            if (position / PerPage != _page) GoTo(position / PerPage); // it lands on its page, turned back to
            icon = _scene.BoxesOnItsPage(position).icon;
        }
        double s = _mon.Scale;
        _flyFrom = _carriedAt;
        // Its place's middle unrounded: rounded (halves to even) it put the picture a pixel off its place by turns.
        _flyTo = ((_area.Left + icon.X + icon.Width / 2) * s, (_area.Top + icon.Y + icon.Height / 2) * s);
        _flyWatch.Restart();
        if (_flyClock == null)
        {
            _flyClock = new FrameClock(_ui);
            _flyClock.Tick += _ => Fly();
        }
        _flyClock.Start();
    }

    void Fly()
    {
        if (_flying < 0) { _flyClock?.Stop(); return; }
        double t = Math.Min(1, _flyWatch.Elapsed.TotalSeconds / FlyBackSeconds);
        double k = Motion.Out(t);
        ShowCarried(_flyFrom.X + (_flyTo.X - _flyFrom.X) * k, _flyFrom.Y + (_flyTo.Y - _flyFrom.Y) * k);
        if (t >= 1) Landed();
    }

    /// <summary>The carried picture is gone and the place shows fully again.</summary>
    void Landed()
    {
        _flyClock?.Stop();
        // Gone in the frame its place (or the Dock's picture flying on from here) shows it: hidden at once, the window
        // could leave a frame with neither.
        _carryImage?.Vanish();
        _scene?.Faint(-1);
        _scene?.FaintMember(-1);
        _scene?.Moving(false);
        _flying = -1;
        _flyingInFolder = false;
        _picture = null;
    }

    /// <summary>The arrangement kept in the settings (with the places of apps not installed now).</summary>
    void Save()
    {
        _settings.AppGrid.Arrangement = GridArrangement.Places(_tiles, _apps, _settings.AppGrid.Arrangement);
        try { _saver.Save(_settings); } // serialised here, written off the UI thread (it took 106 to 211 ms here: SettingsSaver)
        catch (Exception ex) { Log.Warn("app grid: the arrangement could not be saved", ex); }
    }

    // ---- folders -------------------------------------------------------------------------------------------------

    void OpenFolder(string key)
    {
        int fi = _tiles.FindIndex(t => t.IsFolder && t.Key == key);
        if (fi < 0 || _scene == null || _layout == null || _window == null) return;
        var folder = _tiles[fi];
        _openKey = key;
        _folderLayout = FolderGeometry.Compute(_layout, folder.Apps.Count);
        _scene.Open(folder, _folderLayout, Animations);
        _window.DimField(true);
        Log.Debug($"app grid: the folder {Log.Redact(folder.FolderName!)} opened ({folder.Apps.Count} apps, {_scene.FolderPages} pages)");
    }

    void CloseFolder(bool animate)
    {
        if (_openKey == null) return;
        EndRename(commit: true);
        _openKey = null;
        _folderLayout = null;
        _window?.DimField(false);
        _scene?.Close(animate && Animations);
    }

    /// <summary>The open folder's name is edited where it shows (a click on it, as on macOS).</summary>
    void BeginRename()
    {
        if (OpenFolderIndex() is not (>= 0 and var fi) || _folderLayout is not { } f || _window == null || _tiles[fi].IsHidden) return;
        _renaming = true;
        _scene?.HideTitle(true);
        _window.EditName(new Rect(f.Title.X, f.Title.Y, f.Title.Width, f.Title.Height), _tiles[fi].FolderName!);
        Log.Debug("app grid: the folder's name is edited");
    }

    /// <summary>The name's editing ends: kept (<paramref name="commit"/>; a blank name keeps the old one) or not.</summary>
    void EndRename(bool commit)
    {
        if (!_renaming || _window == null) return;
        _renaming = false;
        string text = _window.NameBox.Text;
        _window.EndEditName();
        int fi = OpenFolderIndex();
        if (commit && fi >= 0)
        {
            var next = GridArrangement.Rename(_tiles, fi, text);
            if (next[fi].FolderName != _tiles[fi].FolderName)
            {
                Log.Debug($"app grid: the folder renamed {Log.Redact(next[fi].FolderName!)}");
                _tiles = next;
                Save();
                _scene?.SetFolders(_tiles);
                if (_query.Length == 0) _shown = _tiles;
                _scene?.Arrange(_shown);
                _scene?.EnsureLabels(_page);
            }
        }
        if (fi >= 0) _scene?.SetTitle(_tiles[fi].FolderName!);
        _scene?.HideTitle(false);
    }

    // ---- opening an app ------------------------------------------------------------------------------------------

    /// <param name="admin">Opened as administrator (the menu's "Run as Administrator"): a new one, as Windows' Start menu does.</param>
    void Launch(int app, bool admin = false)
    {
        if (app < 0 || app >= _apps.Count) return;
        var a = _apps[app];
        if (Guarded(a, "opened")) return;
        LastLaunch = (admin ? "as administrator: " : "") + a.Name;
        Log.Debug($"app grid: open {Log.Redact(a.Name)}{(admin ? " as administrator" : "")}");
        // While the grid is still the foreground window: Windows lets the new app take the foreground then. One that
        // runs comes forward instead (macOS), and the Dock bounces one that starts (2026-10-23).
        string target = @"shell:AppsFolder\" + a.ParsingName;
        if (admin) CommandRunner.LaunchAsAdmin(target, started => { if (!started) _ui.BeginInvoke(() => LastLaunch = "not as administrator: " + a.Name); });
        else if (CommandRunner.Opening?.Invoke(target, "grid") != true) CommandRunner.Launch(target);
        Close(restore: false);
    }

    /// <summary>A spec's grid acts on the test target's programs only: true (and nothing done) for another app.</summary>
    bool Guarded(GridApp a, string what)
    {
        if (OnlyAppsIn is not { } folder || SpecGuard.Allows(folder, ShellApi.LinkTarget(@"shell:AppsFolder\" + a.ParsingName), isDesktop: false)) return false;
        LastLaunch = "guarded: " + a.Name;
        Log.Warn($"app grid: {Log.Redact(a.Name)} not {what}: not a test target's program");
        return true;
    }

    /// <summary>The file an app of a Start menu shortcut runs (null for a packaged app, or one whose file is gone).</summary>
    static string? FileOf(GridApp a) =>
        ShellApi.LinkTarget(@"shell:AppsFolder\" + a.ParsingName) is { } path && File.Exists(path) ? path : null;

    // ---- the right-click menu ------------------------------------------------------------------------------------

    /// <summary>A right-click at <paramref name="p"/> (DIPs in the window): the menu of the place or the open folder's app there.</summary>
    static bool InTextBox(DependencyObject? d)
    {
        for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.TextBoxBase) return true;
        return false;
    }

    void MenuAt(Point p)
    {
        if (_window == null || _scene == null || _down != null || _closing) return;
        EndRename(commit: true); // as a click elsewhere
        if (_openKey != null)
        {
            int m = MemberAt(p);
            if (m >= 0 && OpenFolderIndex() is (>= 0 and var fi)) ShowMenu(GridTile.ForApp(_tiles[fi].Apps[m], _apps[_tiles[fi].Apps[m]].ParsingName), p);
            return;
        }
        int at = PositionAt(p);
        if (at >= 0) ShowMenu(_shown[at], p);
    }

    /// <summary>The menu of <paramref name="tile"/>, opened at <paramref name="p"/> (DIPs in the window).</summary>
    void ShowMenu(GridTile tile, Point p)
    {
        if (_scene == null || _layout == null) return;
        if (_flying >= 0) Landed();
        var s = S;
        IReadOnlyList<MenuItem> items;
        string name, key;
        if (tile.IsFolder)
        {
            items = GridMenu.ForFolder(s, tile.IsHidden);
            name = tile.FolderName!;
            key = tile.Key;
        }
        else
        {
            var app = _apps[tile.App];
            string? file = FileOf(app);
            var facts = new GridAppFacts(GridArrangement.HiddenApps(_tiles).Contains(tile.App), Shelf()?.Keeps(app.ParsingName),
                file != null && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), file != null);
            items = GridMenu.ForApp(s, facts, FileManager.NameIn(s));
            name = app.Name;
            key = app.ParsingName;
        }
        bool folder = tile.IsFolder;
        var t = _theme.Current;
        var popup = new MenuPopup(new Menu("grid", name, items), t, Materials.Menu(t, _settings.Appearance, (float)(MenuPopup.RadiusDip * _mon.Scale)), k => k.ToString());
        _menu.ShowAt(popup, new Point(_area.Left + p.X, _area.Top + p.Y), _area, chosen => RunMenu(key, folder, chosen.Id));
        Log.Debug($"app grid: the menu of {(folder ? "the folder " : "")}{Log.Redact(name)} opened");
    }

    /// <summary>
    /// A menu's item <paramref name="id"/> chosen for the app or folder <paramref name="key"/> (found again: the grid may
    /// have changed while the menu was open).
    /// </summary>
    void RunMenu(string key, bool folder, string id)
    {
        if (!IsOpen || _scene == null) return;
        LastMenu = $"{id} on {(folder ? "folder " : "")}{key}";
        if (folder)
        {
            int fi = _tiles.FindIndex(t => t.IsFolder && t.Key == key);
            if (fi < 0) return;
            switch (id)
            {
                case GridMenu.Open: OpenFolder(key); break;
                case GridMenu.Rename:
                    if (_openKey != key) { CloseFolder(animate: false); OpenFolder(key); }
                    BeginRename();
                    break;
                case GridMenu.Disband:
                    if (_openKey == key) CloseFolder(animate: false);
                    Rearrange(GridArrangement.Disband(_tiles, fi, _apps), $"the folder {Log.Redact(_tiles[fi].FolderName!)} ungrouped");
                    break;
            }
            return;
        }
        int app = -1;
        for (int i = 0; i < _apps.Count && app < 0; i++) if (string.Equals(_apps[i].ParsingName, key, StringComparison.OrdinalIgnoreCase)) app = i;
        if (app < 0) return;
        var a = _apps[app];
        switch (id)
        {
            case GridMenu.Open: Launch(app); break;
            case GridMenu.RunAsAdmin: Launch(app, admin: true); break;
            case GridMenu.Keep: Shelf()?.Keep(a.ParsingName, a.Name); break;
            case GridMenu.Unkeep: Shelf()?.Remove(a.ParsingName); break;
            case GridMenu.Show:
                if (Guarded(a, "shown in the file manager") || FileOf(a) is not { } file) break;
                LastLaunch = "shown: " + a.Name;
                FileManager.Reveal(file);
                Close(restore: false);
                break;
            case GridMenu.Hide: Rearrange(GridArrangement.Hide(_tiles, app, _apps, S["grid.hidden"]), $"{Log.Redact(a.Name)} hidden"); break;
            case GridMenu.Unhide: Rearrange(GridArrangement.Unhide(_tiles, app, _apps), $"{Log.Redact(a.Name)} shown again"); break;
        }
    }

    /// <summary>
    /// The arrangement changed by a menu's item (hidden, shown again, ungrouped): kept, the places sliding to where they
    /// go; an open folder that went (its last hidden app shown) closes.
    /// </summary>
    void Rearrange(List<GridTile> next, string what)
    {
        if (_scene == null) return;
        Log.Debug("app grid: " + what);
        if (Animations && _carried == null)
        {
            _scene.Moving(true);
            _moveEnd.Stop();
            _moveEnd.Start();
        }
        _tiles = next;
        Save();
        if (_openKey != null && OpenFolderIndex() < 0)
        {
            // Its apps go back to the grid's layer once the panel has faded: the arrangement is shown again then.
            _openKey = null;
            _folderLayout = null;
            _window?.DimField(false);
            _scene.SetFolders(_tiles);
            _scene.Close(Animations, () => { if (_carried == null) ShowArrangement(); });
        }
        else
        {
            _scene.SetFolders(_tiles);
            if (OpenFolderIndex() is (>= 0 and var open))
            {
                // Fewer or more apps than when it opened (one shown again from the hidden folder): the panel fits them
                // (it kept the rows it opened with, an empty one under them).
                if (_layout != null && FolderGeometry.Compute(_layout, _tiles[open].Apps.Count) is var fits && fits.Panel != _folderLayout?.Panel)
                {
                    _folderLayout = fits;
                    _scene.Relayout(fits, _tiles[open].FolderName!);
                }
                _scene.ArrangeFolder(_tiles[open].Apps);
            }
        }
        ShowArrangement();
    }

    /// <summary>The places as they are now (the arrangement, or the search's apps), on the page that was shown.</summary>
    void ShowArrangement()
    {
        if (_scene == null) return;
        _shown = _query.Length == 0 ? _tiles : Found(_query);
        _scene.Arrange(_shown);
        _page = Math.Clamp(_page, 0, Pages - 1);
        _scene.GoTo(_page, animate: false);
        if (_query.Length == 0) return;
        _selection = _shown.Count == 0 ? -1 : Math.Clamp(_selection, 0, _shown.Count - 1);
        _scene.Select(_selection);
    }

    /// <summary>Diagnostics: the open menu's items and where they are (pixels), or null.</summary>
    public object? MenuState()
    {
        if (_menu.Popup is not { } p) return null;
        static object Px(RECT r) => new { r.Left, r.Top, r.Right, r.Bottom };
        double s = _mon.Scale;
        return new
        {
            hwnd = p.Handle.ToInt64(),
            left = (int)Math.Round(p.Left * s), top = (int)Math.Round(p.Top * s),
            right = (int)Math.Round((p.Left + p.ActualWidth) * s), bottom = (int)Math.Round((p.Top + p.ActualHeight) * s),
            items = p.Items.Select((i, n) => new { i.Id, i.Label, i.Enabled, rect = i.IsSeparator ? null : Px(p.RowPx(n)) }),
        };
    }

    // ---- diagnostics ---------------------------------------------------------------------------------------------

    /// <summary>Diagnostics: the grid as the E2E specs read it (rectangles in screen pixels).</summary>
    public object State()
    {
        double s = _mon.Scale;
        object Px(Rect r) => new
        {
            Left = (int)Math.Round((_area.Left + r.Left) * s), Top = (int)Math.Round((_area.Top + r.Top) * s),
            Right = (int)Math.Round((_area.Left + r.Right) * s), Bottom = (int)Math.Round((_area.Top + r.Bottom) * s),
        };
        Rect R(Box b) => new(b.X, b.Y, b.Width, b.Height);
        object Tile(GridTile t, int position, (Box icon, Box label) b) => new
        {
            position,
            kind = t.IsFolder ? "folder" : "app",
            Name = t.IsFolder ? t.FolderName : _apps[t.App].Name,
            parsing = t.IsFolder ? t.Key : _apps[t.App].ParsingName,
            apps = t.IsFolder ? t.Apps.Select(i => new { _apps[i].Name, parsing = _apps[i].ParsingName }) : null,
            named = _scene?.Named(t) ?? false,
            icon = Px(R(b.icon)),
            label = Px(R(b.label)),
        };
        var items = new List<object>();
        if (_scene != null)
            for (int k = _page * PerPage; k < Math.Min(_shown.Count, (_page + 1) * PerPage); k++)
                if (_scene.ItemBoxes(k) is { } b) items.Add(Tile(_shown[k], k, b));
        object? folder = null;
        if (_openKey != null && _folderLayout is { } f && OpenFolderIndex() is (>= 0 and var fi) && _scene != null)
        {
            var members = new List<object>();
            var apps = _inFolder && FolderIndex(_rest, _fromFolder) is (>= 0 and var ri)
                ? [.. _rest[ri].Apps.Take(_folderGap), _carried!.App, .. _rest[ri].Apps.Skip(_folderGap)]
                : _tiles[fi].Apps.ToList();
            for (int k = 0; k < apps.Count; k++)
                if (_scene.FolderItemBoxes(k) is { } b) members.Add(Tile(GridTile.ForApp(apps[k], _apps[apps[k]].ParsingName), k, b));
            folder = new
            {
                key = _openKey,
                name = _tiles[fi].FolderName,
                apps = apps.Count,
                page = _scene.FolderPage,
                pages = _scene.FolderPages,
                animating = _scene.FolderAnimating,
                panel = Px(R(f.Panel)),
                title = Px(R(f.Title)),
                renaming = _renaming,
                nameBox = _window is { EditingName: true } ? Px(_window.NameRect) : null,
                items = members,
            };
        }
        var icons = _icons?.Counts ?? (0, 0);
        return new
        {
            open = IsOpen,
            hwnd = _window?.Handle.ToInt64() ?? 0,
            backdrop = _window?.BackdropHandle.ToInt64() ?? 0,
            rect = _window == null ? null : Px(new Rect(0, 0, _area.Width, _area.Height)),
            foreground = _window != null && Native.GetForegroundWindow() == _window.Handle,
            focused = _window?.Field.IsKeyboardFocused ?? false,
            composing = _window?.Composing ?? false,
            text = _window?.Field.Text ?? "",
            page = _page,
            pages = Pages,
            animating = _scene?.Animating ?? false,
            // A press giving (2026-10-23): the icon pressed now (its name) and presses that gave.
            given = _scene?.GivenName, gives = _scene?.Given ?? 0,
            // Carrying: where it shows (faint) while carried (-1: not carrying), and what the arrangement makes of it.
            carrying = _carried == null ? -1 : _inFolder ? _folderGap : Math.Max(0, _gap),
            carried = _carried == null ? null : _carried.IsFolder ? _carried.FolderName : _apps[_carried.App].Name,
            carriedFrom = _fromFolder,
            inFolder = _inFolder,
            gap = _carried == null ? -1 : _gap,
            pendingGap = _pendingGap == NoGap ? -2 : _pendingGap,
            over = _over,
            into = _into,
            overDock = _overShelf,
            flying = _flying,
            columns = _layout?.Columns ?? 0,
            rows = _layout?.Rows ?? 0,
            iconSize = _layout?.IconSize ?? 0,
            apps = _apps.Count,
            places = _tiles.Count,
            folders = _tiles.Count(t => t.IsFolder),
            hiddenApps = GridArrangement.HiddenApps(_tiles).Count,
            // The last places shown, on whichever page (the hidden apps' folder is last of all).
            tail = _shown.Select((t, k) => (t, k)).Skip(Math.Max(0, _shown.Count - 12)).Select(x => new
            {
                position = x.k,
                kind = x.t.IsFolder ? "folder" : "app",
                Name = x.t.IsFolder ? x.t.FolderName : _apps[x.t.App].Name,
                parsing = x.t.IsFolder ? x.t.Key : _apps[x.t.App].ParsingName,
                apps = x.t.IsFolder ? x.t.Apps.Select(i => _apps[i].Name) : null,
            }),
            menu = MenuState(),
            lastMenu = LastMenu,
            arrangement = _settings.AppGrid.Arrangement.Count,
            shown = _shown.Count,
            selection = _selection,
            lastOpenMs = LastOpenMs,
            lastLaunch = LastLaunch,
            labels = _scene?.LabelsMade ?? 0,
            iconsShown = _scene?.IconsShown ?? 0,
            iconsCached = icons.cached,
            iconsFailed = icons.failed,
            field = _window == null ? null : Px(_window.FieldRect),
            dots = _scene?.DotCentres().Select(p => new { x = (int)Math.Round((_area.Left + p.X) * s), y = (int)Math.Round((_area.Top + p.Y) * s) }),
            folder,
            items,
        };
    }

    /// <summary>Diagnostics: every app of the grid in name order.</summary>
    public object Apps() => _apps.Select(a => new { a.Name, parsing = a.ParsingName });

    /// <summary>Diagnostics: puts <paramref name="text"/> in the field as if typed (no IME).</summary>
    public void SetText(string text)
    {
        if (_window == null) return;
        _window.Field.Text = text;
        _window.Field.CaretIndex = text.Length;
    }

    /// <summary>Diagnostics: puts <paramref name="text"/> in the folder's name box as if typed (no IME): false when not editing.</summary>
    public bool SetFolderName(string text)
    {
        if (_window == null || !_renaming) return false;
        _window.NameBox.Text = text;
        _window.NameBox.CaretIndex = text.Length;
        return true;
    }

    /// <summary>
    /// Diagnostics (a spec's setup): the arrangement from JSON, [{"kind":"app","id":…}, {"kind":"folder","id":…,
    /// "name":…,"apps":[…]}], kept as the user's would be. Returns an error, or null.
    /// </summary>
    public string? SetArrangement(string json)
    {
        try
        {
            var places = new List<GridPlace>();
            using var doc = JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                string kind = p.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                string id = p.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                if (kind == GridPlace.FolderKind)
                    places.Add(GridPlace.Folder(id, p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        p.TryGetProperty("apps", out var a) ? a.EnumerateArray().Select(x => x.GetString() ?? "") : []));
                else places.Add(GridPlace.App(id));
            }
            var check = new AppSettings();
            check.AppGrid.Arrangement = places;
            SettingsValidator.Normalize(check);
            if (_carried != null) CancelPointer();
            CloseFolder(animate: false);
            _menu.Close();
            _settings.AppGrid.Arrangement = check.AppGrid.Arrangement;
            _tiles = GridArrangement.Tiles(_apps, _settings.AppGrid.Arrangement, S["grid.hidden"]);
            Save();
            _scene?.SetFolders(_tiles);
            Filter();
            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return ex.Message; }
    }

    /// <summary>Diagnostics: turns to a page.</summary>
    public void ShowPage(int page) => GoTo(page);

    void Release()
    {
        if (_closing) FinishClosing(); // no fading out of a grid that goes away
        _menu.Close();
        _openKey = null;
        _folderLayout = null;
        _renaming = false;
        _scene?.Dispose();
        _scene = null;
        _icons?.Dispose();
        _icons = null;
        _surfaces = null; // shared: it stays
        _window?.Close();
        _window = null;
        _layout = null;
        _area = default;
    }

    public void Dispose()
    {
        _iconsLoaded.Stop();
        _intoTimer.Stop();
        _edgeTimer.Stop();
        _gapTimer.Stop();
        _moveEnd.Stop();
        _menu.Dispose();
        Close(restore: false);
        Release();
        _flyClock?.Dispose();
        _carryImage?.Dispose();
    }
}
