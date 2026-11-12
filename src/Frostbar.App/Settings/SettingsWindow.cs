using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Frostbar.App.Common;
using Frostbar.Core.AppGrid;
using Frostbar.Core.HotCorners;
using Frostbar.Core.Localization;
using Frostbar.Core.MenuBar;
using Frostbar.Core.Search;
using Frostbar.Core.Settings;
using Mods = Frostbar.Core.Menus.Mods;
using Shortcut = Frostbar.Core.Menus.Shortcut;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Sys;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Settings;

/// <summary>What a change touches, so the shell redoes only that.</summary>
public enum SettingsArea { Other, Language, Appearance, GlassTint, MenuBarHeight, Clock, Tray, TrayIcons, Dock, Log, Taskbar, HotCorners, AppGrid, Hud, Banners, Schedule, Wallpaper, MenuBarItems, MenuBarAutoHide, Battery, RecentItems, ScrollBars, Motion }

/// <summary>A tray icon as the menu bar page lists it: the key its place is kept under, the app's name, the icon's tip, its picture.</summary>
public sealed record SettingsTrayIcon(string Key, string Name, string Detail, ImageSource? Icon);

/// <summary>The shell, as the settings window sees it.</summary>
public interface ISettingsHost
{
    AppSettings Settings { get; }
    /// <summary>Something in <paramref name="area"/> changed: apply it now, save a moment later.</summary>
    void Apply(SettingsArea area);
    /// <summary>False in a development build started without --hide-taskbar (the switch is greyed).</summary>
    bool TaskbarHidingAvailable { get; }
    /// <summary>The tray setting the shell started with: a change applies at the next start.</summary>
    bool TrayAtStart { get; }
    void RestoreTaskbarNow();
    /// <summary>Starts the shell again (a new instance replaces this one) with the settings open.</summary>
    void Relaunch();
    string LogFolder { get; }
    string SettingsFile { get; }
    /// <summary>The app grid's icon size on this screen for <paramref name="columns"/> × <paramref name="rows"/>: when left to fit, and the largest that fits.</summary>
    (int auto, int fits) AppGridIconSizes(int columns, int rows);
    /// <summary>Starting at sign-in can be set (installed builds only), whether it is on, and setting it.</summary>
    bool LoginItemAvailable { get; }
    bool LoginItemOn { get; }
    void SetLoginItem(bool on);
    /// <summary>Whether Windows is dark now (a spec's shell: its own dark mode).</summary>
    bool WindowsDark { get; }
    /// <summary>Where the dark mode by itself was at the shell's last look (null: off): the change the page writes is the one it will make.</summary>
    Frostbar.Core.Time.AppearanceSchedule.State? Schedule { get; }
    /// <summary>Raised on the UI thread when that moved (a change passed, the settings or a spec's clock changed).</summary>
    event Action? ScheduleMoved;
    /// <summary>The services the system pages (Wi-Fi, Bluetooth, Battery, Sound) show and change; null before the menu bar has them.</summary>
    SystemServices? System { get; }
    /// <summary>The shell's own shortcuts let go (true) while keys are being recorded, so one already set reaches the page.</summary>
    void HoldShortcuts(bool hold);
    /// <summary>The shortcut for <paramref name="action"/> could not be taken (another app has those keys).</summary>
    bool ShortcutTaken(string action);
    /// <summary>The tray's icons now, one for each place key (empty while the tray is off).</summary>
    IReadOnlyList<SettingsTrayIcon> TrayIcons();
    /// <summary>The signed-in user for the sidebar's top (macOS's account row); a spec's shell has one of its own.</summary>
    Account Account { get; }
}

/// <summary>
/// What the system pages show and change: the menu bar's services (they live as long as the shell), the Wi-Fi
/// password window, and app names and icons for the volume mixer.
/// </summary>
public sealed record SystemServices(WifiService Wifi, RadioService Radios, BluetoothService Bluetooth, AudioService Audio, BatteryMonitor Battery,
    Func<string, Task> AskWifiPassword, Func<string, string?, string> AppName, Func<string, string, ImageSource?> Icon, BrightnessService? Brightness = null);

/// <summary>
/// Frostbar's own settings, in the shape of macOS's System Settings: a sidebar of sections, each a column of grouped rows
/// (switches, segmented choices, sliders). Changes apply at once and are saved a moment later; the few that need the
/// shell made again say so and offer to restart it.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    public const double SidebarWidth = 216, PaneWidth = 524, WindowHeight = 580;
    /// <summary>The sidebar's sections, in its order (Core's <see cref="ShellSettingsPages"/>: the search finds the same pages).</summary>
    public static readonly string[] PageKeys = ShellSettingsPages.Sidebar;
    /// <summary>Pages opened from a row of another (General → About, Storage): the sidebar marks the parent, the header has a back arrow.</summary>
    public static IReadOnlyDictionary<string, string> SubPages => ShellSettingsPages.SubPages;
    static IEnumerable<string> AllPages => ShellSettingsPages.All;
    /// <summary>Where the sidebar leaves a gap before a section (macOS groups its sections).</summary>
    static readonly HashSet<string> GapBefore = ["general", "sound", "keyboard", "advanced"];
    /// <summary>Words a section is found by without showing them (Core's, shared with the search).</summary>
    static IReadOnlyDictionary<string, string[]> AlsoFoundBy => ShellSettingsPages.AlsoFoundBy;

    readonly ISettingsHost _host;
    readonly AppSettings _settings;
    readonly Strings _s;
    ThemeSnapshot _t;
    Brush _text = Brushes.Black, _dim = Brushes.Gray, _card = Brushes.White, _line = Brushes.LightGray, _accent = Brushes.DodgerBlue, _hover = Brushes.Transparent;
    readonly StackPanel _sidebar = new() { Margin = new Thickness(10, 0, 10, 10) };
    // The sections scroll under the search field when the window is too short for them (stage 4: 17 sections).
    readonly ScrollViewer _sideScroll;
    readonly StackPanel _page = new() { Margin = new Thickness(20, ToolbarHeight, 20, 24) }; // its top under the toolbar
    readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 20, 0) };
    // The toolbar's ‹ › (System Settings' back and forward through the pages seen; 2026-10-22). Before, only a sub-page
    // had an arrow, back to its parent.
    // Since the window became glass (2026-10-22, the user's "C"), they stand on one floating glass capsule (macOS 26's
    // toolbar buttons).
    readonly Border _back = new() { Width = 30, Height = 26, CornerRadius = new CornerRadius(13), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _forward = new() { Width = 30, Height = 26, CornerRadius = new CornerRadius(13), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _capsule = new() { Height = 30, CornerRadius = new CornerRadius(15), Padding = new Thickness(1), Margin = new Thickness(14, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1) };
    /// <summary>The toolbar's height: the page scrolls under it, into its bar.</summary>
    const double ToolbarHeight = 52;
    readonly Border _header = new() { Height = ToolbarHeight, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Top };
    // macOS 27's uniform toolbar: a bar under the controls once the page scrolls under them, its edge clear, the page
    // diffused in it, how much by the glass slider (MacStories' review and its picture of Photos; "no more progressive blur
    // for scroll edges in System Settings", 2026-11-01). The page faded out at the toolbar's top until then (macOS 26's edge).
    const double BandBlur = 16, BandFadeBy = 8;
    readonly Grid _band = new() { Height = ToolbarHeight, VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly Border _bandTint = new() { BorderThickness = new Thickness(0, 0, 0, 1) };
    readonly System.Windows.Shapes.Rectangle _bandPicture = new()
    {
        VerticalAlignment = VerticalAlignment.Top, Height = ToolbarHeight + 2 * BandBlur, Margin = new Thickness(20, -BandBlur, 20, 0),
        Effect = new System.Windows.Media.Effects.BlurEffect { Radius = BandBlur, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance },
    };
    /// <summary>The toolbar's bar as shown, 0 to 1 (diagnostics: 0 at the page's top).</summary>
    public double BandOpacity => _band.Visibility == Visibility.Visible ? _band.Opacity : 0;
    // The glass sidebar's sheen from its top.
    readonly Border _sideSheen = new() { IsHitTestVisible = false };

    /// <summary>
    /// How the window is drawn (diagnostics): "glass" (Liquid Glass: the whole window see-through over Acrylic, its cards
    /// a lighter glass; 2026-10-22, the user's "C"), "mica" (Liquid Glass off: Mica in the sidebar, the page opaque), or
    /// "solid" (reduce transparency, Windows' transparency effects off, energy saver, high contrast).
    /// </summary>
    public string Look { get; private set; } = "solid";
    /// <summary>The toolbar the page scrolls under (diagnostics: a spec clicks only below it).</summary>
    public FrameworkElement Toolbar => _header;
    /// <summary>The page's first part shown (diagnostics: below the toolbar at the page's top, under it once scrolled).</summary>
    public FrameworkElement? FirstPart => _page.Children.OfType<FrameworkElement>().FirstOrDefault(c => c.Visibility == Visibility.Visible);
    readonly List<string> _history = new();
    int _at = -1;
    bool _travelling;
    readonly ScrollViewer _scroll;
    readonly Border _sidebarBack = new(), _pane = new();
    readonly Dictionary<string, Border> _items = new();
    // The search at the top of the sidebar (System Settings' search field).
    readonly TextBox _field = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center };
    readonly TextBlock _placeholder = new() { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _noResults = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 6, 8, 0), Visibility = Visibility.Collapsed };
    readonly Border _fieldBox = new() { Height = 28, CornerRadius = new CornerRadius(7), Margin = new Thickness(10, 50, 10, 10), BorderThickness = new Thickness(1) };
    string _query = ""; // normalized (TextNormalizer)
    bool _composing;
    Dictionary<string, string[]>? _words;
    readonly List<Border> _rows = new();
    readonly List<Border> _found = new();

    /// <summary>The section shown.</summary>
    public string Page { get; private set; } = "";
    public IReadOnlyDictionary<string, Border> SidebarItems => _items;
    /// <summary>Where the sections show (they scroll inside it): a spec scrolls a section into it before a click.</summary>
    public FrameworkElement SidebarView => _sideScroll;
    /// <summary>The page or the sidebar is still gliding under a wheel's notch (diagnostics: a spec waits before it reads where a control is).</summary>
    public bool Scrolling => SmoothScroll.Gliding(_scroll) || SmoothScroll.Gliding(_sideScroll);

    /// <summary>The page's list: where it is, how far it is stretched past an end, and its scroll bar's opacity (diagnostics).</summary>
    public (double Offset, double Stretched, double Bar, bool BarTakes) PageScroll => (_scroll.VerticalOffset, SmoothScroll.Stretched(_scroll), MacScrollBar.Shown(_scroll), MacScrollBar.Takes(_scroll));
    /// <summary>The page's scroll bar and its thumb, and how far the page can scroll (diagnostics: a spec clicks the track).</summary>
    public (FrameworkElement? Bar, FrameworkElement? Thumb, double Scrollable) PageBar => MacScrollBar.Parts(_scroll) is var (b, th) ? (b, th, _scroll.ScrollableHeight) : default;
    /// <summary>The search field, what is searched for (normalized), and the rows of the page that match it (diagnostics).</summary>
    public FrameworkElement SearchField => _fieldBox;
    public string Query => _query;
    public IReadOnlyList<Border> Found => _found;
    /// <summary>The page's controls by setting (diagnostics: the specs click and drag them).</summary>
    public Dictionary<string, FrameworkElement> Controls { get; } = new();
    public Ellipse CloseButton { get; } = Light(Color.FromRgb(0xFF, 0x5F, 0x57));
    public Ellipse ZoomButton { get; } = Light(Color.FromRgb(0x28, 0xC8, 0x40));

    /// <summary>The size it had when last closed, while the shell runs (E4): it opens so again.</summary>
    static Size? _lastSize;
    // Where it stood before the green light filled the work area (null: not zoomed).
    Rect? _unzoomed;

    /// <summary>Filled the work area by the green light (diagnostics).</summary>
    public bool Zoomed => _unzoomed != null;

    /// <summary>The green light: the work area of its monitor, or back to where and how large it was.</summary>
    void ToggleZoom()
    {
        if (_unzoomed is { } was)
        {
            _unzoomed = null;
            (Left, Top, Width, Height) = (was.Left, was.Top, was.Width, was.Height);
            return;
        }
        var handle = new WindowInteropHelper(this).Handle;
        if (Frostbar.Platform.Shell.Monitors.Of(handle) is not { } mon) return;
        _unzoomed = new Rect(Left, Top, Width, Height);
        double k = mon.Scale;
        (Left, Top, Width, Height) = (mon.Work.Left / k, mon.Work.Top / k, mon.Work.Width / k, mon.Work.Height / k);
    }
    /// <summary>The toolbar's back and forward arrows (diagnostics: shown as null while they cannot go anywhere).</summary>
    public FrameworkElement BackButton => _back;
    public FrameworkElement ForwardButton => _forward;

    bool CanGoBack => _at > 0 || SubPages.ContainsKey(Page);
    bool CanGoForward => _at >= 0 && _at < _history.Count - 1;

    /// <summary>Back through the pages seen, or a sub-page opened by itself (the search) to its parent.</summary>
    void GoBack()
    {
        if (_at > 0) Travel(_at - 1);
        else if (SubPages.TryGetValue(Page, out var parent))
        {
            // A sub-page opened by itself (the search, Windows' page it takes the place of): its parent goes before it,
            // so › comes back to it. Added after it, ‹ on the parent went back to the sub-page (the Gemini review, 2026-10-22).
            int at = Math.Max(0, _at);
            _history.Insert(at, parent);
            Travel(at);
        }
    }

    void GoForward()
    {
        if (CanGoForward) Travel(_at + 1);
    }

    void Travel(int to)
    {
        _at = to;
        _travelling = true;
        try { Show(_history[to]); }
        finally { _travelling = false; }
        Arrows();
    }

    /// <summary>A page shown by a choice (the sidebar, a row, the search): the pages ahead of it are forgotten.</summary>
    void Visited(string key)
    {
        if (_travelling || _at >= 0 && _history[_at] == key) { Arrows(); return; }
        if (_at < _history.Count - 1) _history.RemoveRange(_at + 1, _history.Count - _at - 1);
        _history.Add(key);
        if (_history.Count > 50) _history.RemoveAt(0);
        _at = _history.Count - 1;
        Arrows();
    }

    /// <summary>The arrows lit where they go somewhere, dim (and deaf) where they do not.</summary>
    void Arrows()
    {
        foreach (var (b, on) in new[] { (_back, CanGoBack), (_forward, CanGoForward) })
        {
            b.Opacity = on ? 1 : 0.3;
            b.IsHitTestVisible = on;
            if (!on) b.Background = Brushes.Transparent;
        }
    }

    public SettingsWindow(ISettingsHost host, ThemeSnapshot t, Strings s, string? page)
    {
        _host = host;
        _settings = host.Settings;
        _s = s;
        _t = t;
        Title = s["set.windowTitle"];
        NativeFrame.Apply(this, resizable: true); // a real frame: Windows animates opening, closing and minimizing
        // Its edges take it larger or smaller (E4), no narrower than a page needs.
        ResizeMode = ResizeMode.CanResize;
        MinWidth = SidebarWidth + 420;
        MinHeight = 400;
        Width = SidebarWidth + PaneWidth;
        // Not taller than the work area (a short screen): the sidebar and the page scroll instead.
        Height = Math.Min(WindowHeight, Math.Max(400, SystemParameters.WorkArea.Height - 16));
        if (_lastSize is { } last)
        {
            Width = Math.Min(Math.Max(MinWidth, last.Width), SystemParameters.WorkArea.Width);
            Height = Math.Min(Math.Max(MinHeight, last.Height), SystemParameters.WorkArea.Height);
        }
        // Only a window that showed: the search's index makes one it never shows and closes (0 × 0), and every window
        // after it opened at its smallest (2026-10-20, lens: the page under the Dock, the wheel on the sidebar).
        Closing += (_, _) => { if (IsLoaded && ActualWidth > 0 && ActualHeight > 0 && _unzoomed == null && WindowState == WindowState.Normal) _lastSize = new Size(ActualWidth, ActualHeight); };
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        FontFamily = Materials.UiFont;
        FontSize = 13;
        UseLayoutRounding = true;
        Language = XmlLanguage.GetLanguage("ja-JP"); // Japanese glyphs for Japanese names in either language (as the panels do)

        // Sidebar | header and page
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SidebarWidth) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        // The search field stays at the top (System Settings'); the sections under it scroll when there is no room.
        var side = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_fieldBox, System.Windows.Controls.Dock.Top);
        side.Children.Add(_fieldBox);
        _sideScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _sidebar, Focusable = false };
        MacScrollBar.Apply(_sideScroll, () => _t.Animations);
        SmoothScroll.Attach(_sideScroll, () => _t.Animations);
        side.Children.Add(_sideScroll);
        _sidebarBack.Child = side;
        root.Children.Add(EdgeSidebar());
        var lights = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        CloseButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; Close(); };
        var minimize = Light(Color.FromRgb(0xFE, 0xBC, 0x2E));
        minimize.MouseLeftButtonUp += (_, e) => { e.Handled = true; WindowState = WindowState.Minimized; };
        // The window takes another size (macOS 27's System Settings: E4, 2026-10-19; it kept one size): its edges, and the
        // green light filling the work area and back.
        var zoom = ZoomButton;
        zoom.MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleZoom(); };
        lights.Children.Add(CloseButton);
        lights.Children.Add(minimize);
        lights.Children.Add(zoom);
        root.Children.Add(lights);

        var right = new DockPanel { LastChildFill = true };
        // The toolbar's ‹ › through the pages seen (System Settings'); a sub-page's back reaches its parent.
        foreach (var (arrow, go) in new (Border, Action)[] { (_back, GoBack), (_forward, GoForward) })
        {
            arrow.MouseEnter += (_, _) => arrow.Background = _hover;
            arrow.MouseLeave += (_, _) => arrow.Background = Brushes.Transparent;
            arrow.MouseLeftButtonDown += (_, e) => e.Handled = true;
            arrow.MouseLeftButtonUp += (_, e) => { e.Handled = true; go(); };
        }
        System.Windows.Automation.AutomationProperties.SetName(_back, s["set.back"]);
        System.Windows.Automation.AutomationProperties.SetName(_forward, s["set.forward"]);
        _back.ToolTip = s["set.back"];
        _forward.ToolTip = s["set.forward"];
        // The mouse's own back and forward buttons go through the pages too (as System Settings' do).
        MouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1) { e.Handled = true; GoBack(); }
            else if (e.ChangedButton == MouseButton.XButton2) { e.Handled = true; GoForward(); }
        };
        var arrows = new StackPanel { Orientation = Orientation.Horizontal };
        arrows.Children.Add(_back);
        arrows.Children.Add(_forward);
        _capsule.Child = arrows;
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
        headerRow.Children.Add(_capsule);
        headerRow.Children.Add(_title);
        _header.Child = headerRow;
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _page, Focusable = false };
        MacScrollBar.Apply(_scroll, () => _t.Animations, barTop: ToolbarHeight); // the bar below the toolbar (macOS's)
        SmoothScroll.Attach(_scroll, () => _t.Animations);
        // The page goes under the toolbar's bar at its edge: above it, only the bar's diffused picture of the page.
        _scroll.OpacityMask = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0x00, 0, 0, 0), 0), new(Color.FromArgb(0x00, 0, 0, 0), 1), new(Color.FromArgb(0xFF, 0, 0, 0), 1),
        }, new Point(0, 0), new Point(0, ToolbarHeight)) { MappingMode = BrushMappingMode.Absolute };
        _bandPicture.Fill = new VisualBrush(_page) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute };
        _band.Children.Add(_bandPicture);
        _band.Children.Add(_bandTint);
        _scroll.ScrollChanged += (_, _) => PlaceBand();
        // A control the keys go to (Tab) is brought in below the toolbar, not under it (WPF brings it to the view's top).
        _page.RequestBringIntoView += (_, e) =>
        {
            if (e.TargetObject is not FrameworkElement target || !_page.IsAncestorOf(target) || target.ActualHeight <= 0) return;
            e.Handled = true;
            SmoothScroll.IntoView(_scroll, target, _t.Animations, above: ToolbarHeight + 8);
        };
        // The page shown before fades out over the new one as a picture (Show); the toolbar over both.
        var pageArea = new Grid();
        pageArea.Children.Add(_scroll);
        pageArea.Children.Add(_was);
        pageArea.Children.Add(_band);
        pageArea.Children.Add(_header);
        right.Children.Add(pageArea);
        _pane.Child = right;
        Grid.SetColumn(_pane, 1);
        root.Children.Add(_pane);
        Content = root;

        // Dragged by the header and the sidebar's empty parts (a drag started under a control would swallow its click).
        void Drag(object _, MouseButtonEventArgs e) { if (e.Handled) return; try { DragMove(); } catch (InvalidOperationException) { } }
        _header.MouseLeftButtonDown += Drag;
        _sidebarBack.MouseLeftButtonDown += Drag;
        // The glass window (the user's "C"): what is pressed lights where it is pressed and the light spills onto the
        // cards, the toolbar's capsule and the sidebar near by; the ‹ › sink as icons do (2026-10-23, the user's "A 沈む＋
        // 隣へ光る").
        PressLight.Attach(this, () => Look == "glass" && _t.Animations);
        PressSpill.SetGlass(_capsule, true);
        PressSpill.SetGlass(_sidebarBack, true);
        GlassBounce.Attach(_back, () => Look == "glass" && _t.Animations, Frostbar.Core.Shell.PressGive.Icon);
        GlassBounce.Attach(_forward, () => Look == "glass" && _t.Animations, Frostbar.Core.Shell.PressGive.Icon);
        KeyDown += (_, e) =>
        {
            // Esc closes an open pop-up list first (the hot corners'), not the window under it.
            if (e.Key == Key.Escape && Controls.Values.OfType<MacControls.PopUp>().FirstOrDefault(p => p.IsOpen) is { } list) { e.Handled = true; list.CloseList(); return; }
            if (e.Key == Key.Escape && Controls.Values.OfType<MacControls.MenuButton>().FirstOrDefault(p => p.IsOpen) is { } menu) { e.Handled = true; menu.CloseList(); return; }
            // Alt+F4 too: the window has no system menu to take it (Chrome).
            if (e.Key == Key.Escape || (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control) || e.Key == Key.System && e.SystemKey == Key.F4) { e.Handled = true; Close(); }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; _field.Focus(); _field.SelectAll(); }
        };
        MakeSearch(s);
        SourceInitialized += (_, _) => Chrome();
        Restyle(t);
        Show(page != null && AllPages.Contains(page) ? page : "general");
        Closed += (_, _) => Leave();
    }

    static Ellipse Light(Color c) => new() { Width = 12, Height = 12, Fill = new SolidColorBrush(c), Margin = new Thickness(0, 0, 8, 0) };

    // The sidebar runs to the window's edges, a line between it and the page (macOS 27 took back 26's floating sidebar:
    // the user's "作業 5 は両方戻す", 2026-10-19). From 2026-10-18 (the user's "C") it floated as glass 8 DIP in, rounded
    // by 12, with a shadow round it and its rim's line of light. The window's backdrop shows in it when Windows allows:
    // with Liquid Glass, under a sheen from its top and with a line of light at its edge (2026-10-22, the user's "C").
    readonly Border _sideRim = new() { BorderThickness = new Thickness(0, 0, 1, 0), IsHitTestVisible = false };

    Grid EdgeSidebar()
    {
        var host = new Grid();
        host.Children.Add(_sidebarBack);
        host.Children.Add(_sideSheen);
        host.Children.Add(_sideRim);
        return host;
    }

    IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>The look for <paramref name="t"/> and the appearance's settings (<see cref="Look"/>).</summary>
    string LookFor(ThemeSnapshot t) =>
        !Materials.GlassPossible(t, _settings.Appearance) ? "solid" : _settings.Appearance.LiquidGlass ? "glass" : "mica";

    /// <summary>
    /// Rounded corners, dark title colours, no system menu, and the window's backdrop when Windows allows it: Acrylic for
    /// the glass (it blurs what is behind the window, where Mica only tints by the wallpaper), Mica otherwise.
    /// </summary>
    void Chrome()
    {
        var h = Handle;
        if (h == IntPtr.Zero) return;
        Native.SetDwm(h, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, _t.Dark ? 1 : 0);
        NativeFrame.Round(h, _t.Dark);
        long style = Native.GetWindowLongPtr(h, Native.GWL_STYLE).ToInt64();
        if ((style & Native.WS_SYSMENU) != 0)
        {
            // Through the see-through page DWM drew its own caption buttons at the top right (─ □ ×, the prototype's
            // pictures): no system menu, as the dialogs (2026-10-16). Esc, Ctrl+W and Alt+F4 still close it (KeyDown).
            Native.SetWindowLongPtr(h, Native.GWL_STYLE, new IntPtr(style & ~Native.WS_SYSMENU));
            Native.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        }
        if (Look == "solid")
        {
            Native.SetDwm(h, 38 /*DWMWA_SYSTEMBACKDROP_TYPE*/, 1 /*none*/); // under the window's own fill from now (a change while open)
            return;
        }
        var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(h, ref m);
        HwndSource.FromHwnd(h).CompositionTarget.BackgroundColor = Colors.Transparent;
        Native.SetDwm(h, 38 /*DWMWA_SYSTEMBACKDROP_TYPE*/, Look == "glass" ? 3 /*Acrylic*/ : 2 /*Mica*/);
    }

    /// <summary>The system backdrop DWM has for the window (diagnostics: 3 Acrylic, 2 Mica, 0 or 1 none; -1 unread).</summary>
    public int Backdrop => Handle != IntPtr.Zero && Native.DwmGetWindowAttribute(Handle, 38, out int v, 4) == 0 ? v : -1;

    /// <summary>The toolbar's bar comes as the page scrolls under it (over the first 8 DIPs), showing what is there, diffused.</summary>
    void PlaceBand()
    {
        double y = _scroll.VerticalOffset, shown = Math.Clamp(y / BandFadeBy, 0, 1);
        _band.Opacity = shown;
        _band.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (shown > 0 && _bandPicture.Fill is VisualBrush picture)
            picture.Viewbox = new Rect(0, y - ToolbarHeight - BandBlur, Math.Max(1, _page.ActualWidth), ToolbarHeight + 2 * BandBlur); // the page's own top is the toolbar's height down
    }

    /// <summary>The toolbar's bar in the pane's colour: with glass as see-through as the slider says (macOS 27's: "the background frosts over to a nearly solid color"), else nearly or wholly solid.</summary>
    void TintBand()
    {
        var c = ((SolidColorBrush)_pane.Background).Color;
        byte alpha = Look switch
        {
            "glass" => Frostbar.Core.Shell.GlassTints.Alpha(_t.Dark ? (byte)0xB0 : (byte)0xC8, _settings.Appearance.GlassTint),
            "mica" => 0xE0,
            _ => 0xFF,
        };
        _bandTint.Background = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        _bandTint.BorderBrush = _line;
        _bandPicture.Visibility = alpha < 0xFF ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The window has a system menu (diagnostics: DWM draws its caption buttons through the glass if it has).</summary>
    public bool SystemMenu => Handle != IntPtr.Zero && (Native.GetWindowLongPtr(Handle, Native.GWL_STYLE).ToInt64() & Native.WS_SYSMENU) != 0;

    /// <summary>Colours for <paramref name="t"/>; the page is made again in them (the appearance page changes them).</summary>
    public void Restyle(ThemeSnapshot t)
    {
        _t = t;
        _text = Materials.Text(t);
        var c = ((SolidColorBrush)_text).Color;
        // Accessibility's "Increase contrast" (2026-10-22): dimmed words darker, the cards' lines and borders plain.
        bool contrast = Materials.HighContrastEdges && !t.HighContrast;
        _dim = new SolidColorBrush(Color.FromArgb((byte)(c.A * (contrast ? 0.82 : 0.6)), c.R, c.G, c.B));
        Look = LookFor(t);
        bool glass = Look == "glass";
        var plain = new SolidColorBrush(t.Dark ? Color.FromRgb(0x28, 0x28, 0x28) : Color.FromRgb(0xE8, 0xE8, 0xE8)); // without a backdrop
        // The glass window (2026-10-22, the user's "C"): the page see-through, its cards a lighter glass with a rim of
        // light (CardEdge); opaque otherwise.
        _card = new SolidColorBrush(glass ? (t.Dark ? Color.FromArgb(0x8C, 0x34, 0x34, 0x3A) : Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF))
            : t.Dark ? Color.FromRgb(0x2C, 0x2C, 0x2C) : Colors.White);
        _line = new SolidColorBrush(contrast ? (t.Dark ? Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x52, 0, 0, 0))
            : t.Dark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0));
        _accent = new SolidColorBrush(Color.FromRgb(t.Accent.R, t.Accent.G, t.Accent.B));
        Foreground = _text;
        _title.Foreground = _text;
        _back.Child = Glyphs.Stroke(Glyphs.Back, 14, _text, 1.8);
        _forward.Child = Glyphs.Stroke(Glyphs.Chevron, 14, _text, 1.8);
        _hover = new SolidColorBrush(t.Dark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0));
        Background = Look != "solid" ? Brushes.Transparent : plain;
        _pane.Background = new SolidColorBrush(glass ? (t.Dark ? Color.FromArgb(0x50, 0x1E, 0x1E, 0x22) : Color.FromArgb(0x60, 0xF2, 0xF2, 0xF4))
            : t.Dark ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Color.FromRgb(0xF2, 0xF2, 0xF2));
        // The sidebar: the backdrop (or the old sidebar's colour without it), a line at its edge to the page; as glass a
        // faint tint, a sheen from its top and the line a line of light.
        _sidebarBack.Background = glass ? new SolidColorBrush(t.Dark ? Color.FromArgb(0x30, 0x14, 0x14, 0x1A) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))
            : Look == "mica" ? Brushes.Transparent : plain;
        _sideSheen.Background = glass ? new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb((byte)(t.Dark ? 0x16 : 0x40), 0xFF, 0xFF, 0xFF), 0), new(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 0.4),
        }, 90) : null;
        _sideRim.BorderBrush = contrast ? _line : new SolidColorBrush(glass ? (t.Dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF))
            : t.Dark ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0, 0, 0));
        TintBand();
        // The toolbar's capsule: a light glass in either look (lit from the top, a rim of light, a soft shadow under it);
        // a plain border with increase contrast, the theme's own colours in high contrast.
        if (t.HighContrast)
        {
            _capsule.Background = Brushes.Transparent;
            _capsule.BorderBrush = _text;
            _capsule.Effect = null;
        }
        else
        {
            _capsule.Background = t.Dark
                ? new LinearGradientBrush(Color.FromArgb(0x9E, 0x4C, 0x4C, 0x52), Color.FromArgb(0x9E, 0x30, 0x30, 0x36), 90)
                : new LinearGradientBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xC0, 0xF0, 0xF0, 0xF4), 90);
            var rim = Materials.ContrastRim(t.Dark);
            _capsule.BorderBrush = contrast ? new SolidColorBrush(Color.FromArgb(rim.A, rim.R, rim.G, rim.B)) : t.Dark
                ? new LinearGradientBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), 90)
                : new LinearGradientBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x24, 0, 0, 0), 90);
            _capsule.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = t.Dark ? 0.35 : 0.16 };
        }
        Chrome();
        BuildSidebar();
        // Made again in the new colours where the user was reading (a theme refresh comes with energy saver, an accent
        // change...: the long Dock page jumped back to its top).
        if (Page.Length > 0) ShowAgain(now: true);
    }

    /// <summary>
    /// The page made again where the user was reading: a theme, or a choice that changes what other rows can do (after
    /// the choice's own popup has closed, unless <paramref name="now"/>).
    /// </summary>
    void ShowAgain(bool now = false)
    {
        void Again()
        {
            double at = _scroll.VerticalOffset;
            // The part the keys were on gets them again in the page made anew (an appearance's step with → or Space made
            // it again, the keyboard's focus went with the old part, and the next key did nothing: D3).
            string? keyed = KeyedControl();
            Show(Page);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                _scroll.ScrollToVerticalOffset(at);
                if (keyed != null && Controls.TryGetValue(keyed, out var again) && again.Focusable) again.Focus();
            });
        }
        if (now) Again();
        else Dispatcher.BeginInvoke(DispatcherPriority.Background, Again);
    }

    /// <summary>The name of the page's part that has the keyboard (or holds what has it), when it is one that takes the keys.</summary>
    string? KeyedControl()
    {
        if (Keyboard.FocusedElement is not Visual focused || !_page.IsAncestorOf(focused)) return null;
        foreach (var (key, c) in Controls)
            if (c.Focusable && (ReferenceEquals(c, focused) || c.IsAncestorOf(focused))) return key;
        return null;
    }

    // ---- sidebar ----------------------------------------------------------------------------------------------

    void BuildSidebar()
    {
        _sidebar.Children.Clear();
        _items.Clear();
        StyleSearch();
        _sidebar.Children.Add(AccountRow()); // macOS's account at the top (2026-10-22)
        var bars = new Grid { Width = 16, Height = 16 };
        for (int i = 0; i < Glyphs.WifiArcs.Length; i++) bars.Children.Add(i == 0 ? Glyphs.Fill(Glyphs.WifiArcs[i], 16, Brushes.White) : Glyphs.Stroke(Glyphs.WifiArcs[i], 16, Brushes.White, 1.6));
        bars.LayoutTransform = new ScaleTransform(14 / 16.0, 14 / 16.0);
        _sidebar.Children.Add(Item("wifi", bars, Color.FromRgb(0x0A, 0x84, 0xFF), _s["cc.wifi"]));
        _sidebar.Children.Add(Item("bluetooth", Picture(Glyphs.Bluetooth, true), Color.FromRgb(0x0A, 0x84, 0xFF), _s["cc.bluetooth"]));
        var battery = new Grid { Width = 16, Height = 16 };
        battery.Children.Add(Glyphs.Stroke(Glyphs.BatteryPicture, 16, Brushes.White, 1.4));
        battery.Children.Add(Glyphs.Fill(Glyphs.BatteryPictureLevel, 16, Brushes.White));
        battery.LayoutTransform = new ScaleTransform(14 / 16.0, 14 / 16.0);
        _sidebar.Children.Add(Item("battery", battery, Color.FromRgb(0x34, 0xC7, 0x59), _s["set.battery"]));
        _sidebar.Children.Add(Item("general", Picture(Glyphs.Gear, true), Color.FromRgb(0x8E, 0x8E, 0x93), _s["set.general"]));
        _sidebar.Children.Add(Item("accessibility", Picture(Glyphs.AccessibilityFigure, true), AccessibilityBlue, _s["set.accessibility"]));
        var contrast = new Grid { Width = 16, Height = 16 };
        contrast.Children.Add(Glyphs.Stroke(Glyphs.Contrast, 16, Brushes.White, 1.5));
        contrast.Children.Add(Glyphs.Fill(Glyphs.ContrastHalf, 16, Brushes.White));
        contrast.LayoutTransform = new ScaleTransform(14 / 16.0, 14 / 16.0);
        _sidebar.Children.Add(Item("appearance", contrast, Color.FromRgb(0x2C, 0x2C, 0x2E), _s["set.appearance"]));
        _sidebar.Children.Add(Item("menuBar", Picture(Glyphs.MenuBarPicture, true), Color.FromRgb(0x0A, 0x84, 0xFF), _s["set.menuBar"]));
        _sidebar.Children.Add(Item("dock", Picture(Glyphs.DockPicture, true), Color.FromRgb(0x5E, 0x5C, 0xE6), _s["set.dock"]));
        _sidebar.Children.Add(Item("appGrid", Picture(Glyphs.AppGridPicture, false), Color.FromRgb(0x5A, 0x6B, 0x86), _s["set.appGrid"]));
        _sidebar.Children.Add(Item("hotCorners", Picture(Glyphs.CornerPicture, true), Color.FromRgb(0xFF, 0x9F, 0x0A), _s["set.hotCorners"]));
        _sidebar.Children.Add(Item("display", Picture(Glyphs.DisplayPicture, true), Color.FromRgb(0x0A, 0x84, 0xFF), _s["set.display"]));
        var wallpaper = new Grid { Width = 16, Height = 16 };
        wallpaper.Children.Add(Glyphs.Stroke(Glyphs.WallpaperPicture, 16, Brushes.White, 1.4));
        wallpaper.Children.Add(Glyphs.Fill(Glyphs.WallpaperSun, 16, Brushes.White));
        wallpaper.LayoutTransform = new ScaleTransform(14 / 16.0, 14 / 16.0);
        _sidebar.Children.Add(Item("wallpaper", wallpaper, Color.FromRgb(0x32, 0xAD, 0xE6), _s["set.wallpaper"]));
        _sidebar.Children.Add(Item("search", Picture(Glyphs.Search, true), Color.FromRgb(0x63, 0x63, 0x66), _s["set.search"]));
        var speaker = new Grid { Width = 16, Height = 16 };
        speaker.Children.Add(Glyphs.Fill(Glyphs.Speaker, 16, Brushes.White));
        speaker.Children.Add(Glyphs.Stroke(Glyphs.SpeakerWaves[1], 16, Brushes.White, 1.5));
        speaker.LayoutTransform = new ScaleTransform(14 / 16.0, 14 / 16.0);
        _sidebar.Children.Add(Item("sound", speaker, Color.FromRgb(0xFF, 0x37, 0x5F), _s["cc.sound"]));
        _sidebar.Children.Add(Item("keyboard", Picture(Glyphs.Keyboard, true), Color.FromRgb(0x8E, 0x8E, 0x93), _s["set.keyboard"]));
        _sidebar.Children.Add(Item("mouse", Picture(Glyphs.Mouse, true), Color.FromRgb(0x8E, 0x8E, 0x93), _s["set.mouse"]));
        _sidebar.Children.Add(Item("trackpad", Picture(Glyphs.TrackpadPicture, true), Color.FromRgb(0x8E, 0x8E, 0x93), _s["set.trackpad"]));
        var knobs = new Grid { Width = 16, Height = 16 };
        knobs.Children.Add(Glyphs.Stroke(Glyphs.ControlCenterTracks, 16, Brushes.White, 1.5));
        knobs.Children.Add(Glyphs.Fill(Glyphs.ControlCenterKnobs, 16, Brushes.White));
        knobs.LayoutTransform = new ScaleTransform(14 / 16.0, 14 / 16.0);
        _sidebar.Children.Add(Item("advanced", knobs, Color.FromRgb(0x30, 0xB0, 0xC7), _s["set.advanced"]));
        _sidebar.Children.Add(_noResults);
        if (_query.Length > 0) FilterSidebar();
    }

    // ---- search -----------------------------------------------------------------------------------------------
    // As in System Settings: typing keeps the sections that have a matching setting in the sidebar, shows the first of
    // them, and marks the matching rows of the page shown (scrolled to the first). Esc empties the field; Ctrl+F goes to it.

    void MakeSearch(Strings s)
    {
        _placeholder.Text = s["search.placeholder"];
        _noResults.Text = s["set.noResults"];
        var inner = new Grid();
        inner.Children.Add(_placeholder);
        inner.Children.Add(_field);
        var row = new DockPanel { Margin = new Thickness(8, 0, 8, 0) };
        _glass.Margin = new Thickness(0, 0, 6, 0);
        _glass.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_glass, System.Windows.Controls.Dock.Left);
        row.Children.Add(_glass);
        row.Children.Add(inner);
        _fieldBox.Child = row;
        _fieldBox.MouseLeftButtonDown += (_, e) => { e.Handled = true; _field.Focus(); }; // not a drag of the window
        _field.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _field.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!_composing) Search(_field.Text);
        };
        // The IME's text is not the query yet (as in the search panel).
        TextCompositionManager.AddPreviewTextInputStartHandler(_field, (_, _) => _composing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(_field, (_, e) => _composing = e.TextComposition.CompositionText.Length > 0);
        TextCompositionManager.AddPreviewTextInputHandler(_field, (_, _) => { _composing = false; Dispatcher.BeginInvoke(() => Search(_field.Text)); });
        _field.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _field.Text.Length > 0) { e.Handled = true; _field.Clear(); }
        };
    }

    FrameworkElement _glass = new Grid();

    void StyleSearch()
    {
        var dim = new SolidColorBrush(Color.FromArgb(0x99, ((SolidColorBrush)_text).Color.R, ((SolidColorBrush)_text).Color.G, ((SolidColorBrush)_text).Color.B));
        _fieldBox.Background = new SolidColorBrush(_t.Dark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0, 0, 0));
        _fieldBox.BorderBrush = new SolidColorBrush(_t.Dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0));
        _field.Foreground = _text;
        _field.CaretBrush = _text;
        _field.SelectionBrush = _accent;
        _placeholder.Foreground = dim;
        _noResults.Foreground = dim;
        // The magnifier in the theme's colours.
        if (_fieldBox.Child is DockPanel row)
        {
            var glass = Glyphs.Stroke(Glyphs.Search, 13, dim, 1.4);
            glass.Margin = _glass.Margin;
            glass.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(glass, System.Windows.Controls.Dock.Left);
            row.Children.RemoveAt(0);
            row.Children.Insert(0, glass);
            _glass = glass;
        }
    }

    /// <summary>Puts <paramref name="text"/> in the search field as if typed (diagnostics).</summary>
    public void SetSearch(string text) => _field.Text = text;

    void Search(string text)
    {
        string q = TextNormalizer.Normalize(text);
        if (q == _query) return;
        _query = q;
        FilterSidebar();
        if (_query.Length == 0) { Show(Page); return; }
        var hits = Matches();
        Show(hits.Contains(Page) || hits.Count == 0 ? Page : hits[0]);
    }

    /// <summary>The pages with a setting that matches, the sidebar's sections first in its order, then the sub-pages.</summary>
    List<string> Matches() => _query.Length == 0 ? [.. PageKeys] : AllPages.Where(k => Words()[k].Any(w => w.Contains(_query))).ToList();

    void FilterSidebar()
    {
        // A sub-page's match keeps its parent's section (General for About).
        var hits = Matches().Select(k => SubPages.GetValueOrDefault(k, k)).ToHashSet();
        foreach (var (k, item) in _items) item.Visibility = hits.Contains(k) ? Visibility.Visible : Visibility.Collapsed;
        _noResults.Visibility = hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The words of each page (its title, labels, details and choices), normalized: each page is made once to read them.</summary>
    Dictionary<string, string[]> Words() =>
        _words ??= PageTexts().ToDictionary(kv => kv.Key, kv => (string[])[TextNormalizer.Normalize(kv.Value.Title), .. kv.Value.Texts.Select(TextNormalizer.Normalize),
                                                                          .. AlsoFoundBy.GetValueOrDefault(kv.Key, []).Select(TextNormalizer.Normalize)]);

    Dictionary<string, (string Title, string[] Texts)>? _texts;
    bool _textsRead;

    /// <summary>Each page's title and texts as shown (labels, details, choices): each page is made once to read them.</summary>
    Dictionary<string, (string Title, string[] Texts)> PageTexts()
    {
        while (ReadNextPage() != null) { }
        return _texts!;
    }

    /// <summary>
    /// Makes the next page not read yet and reads its texts (a page a call): the page read, or null once every page is
    /// read, the last one taken down. A page made for this starts nothing (<see cref="_indexing"/>), so it may stay
    /// until the next call.
    /// </summary>
    string? ReadNextPage()
    {
        if (_textsRead) return null;
        _texts ??= new();
        string? key = AllPages.FirstOrDefault(k => !_texts.ContainsKey(k));
        if (key != null)
        {
            _indexing = true;
            try
            {
                Leave();
                _page.Children.Clear();
                Controls.Clear();
                _rows.Clear();
                Build(key);
                _texts[key] = (_title.Text, [.. Texts(_page)]);
            }
            finally { _indexing = false; }
            return key;
        }
        Leave();
        _page.Children.Clear();
        Controls.Clear();
        _rows.Clear();
        _textsRead = true;
        return null;
    }

    /// <summary>Frostbar's pages read for the search: the pages, the UI thread's time in all, its longest step (ms).</summary>
    public sealed record PageIndex(List<ShellPageWords> Pages, long BusyMs, long LongestMs);

    /// <summary>The Wi-Fi page's networks as the search's index read them off the UI thread (null: read them at once).</summary>
    (WlanConnection? Joined, IReadOnlyList<WlanProfile> Known)? _wlanForIndex;

    /// <summary>
    /// Frostbar's pages as the search finds them (stage 6-1): each is made once in a settings window that is never shown,
    /// to read its texts as the window's own search does, then that window is closed and <paramref name="done"/> gets
    /// them (null: they could not be read). A page an idle moment of the UI thread: all at once held it for 160–260 ms,
    /// and a Dock launch's landing and the menu bar's front app waited behind it (2026-10-16, dock-launch). A text on
    /// three pages or more (「オン」, 「なし」) finds none of them (<see cref="ShellSettingsSearch.Index"/>).
    /// </summary>
    public static void SearchIndex(ISettingsHost host, ThemeSnapshot t, Strings s, Strings other, Action<PageIndex?> done)
    {
        // The Wi-Fi page's networks, read off the UI thread meanwhile: the pages wait for them (Wi-Fi is the first).
        var wlan = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var networks = (Joined: WlanProfiles.Current(), Known: WlanProfiles.List());
            Frostbar.Platform.Infrastructure.Log.Debug($"search: the Wi-Fi networks read for the settings index in {sw.ElapsedMilliseconds} ms (off the UI thread)");
            return networks;
        });
        SettingsWindow? w = null;
        long busy = 0, longest = 0;
        void Step()
        {
            // Not while something moves: a page's 40–60 ms held a list's glide (2026-10-17, frame-pace: a notch just
            // after the shell started stood still for 50, 83 and 100 ms). Nor while a Dock icon bounces: the window's
            // step (50–120 ms) held a launch's landing (2026-10-18, dock-launch).
            if (Still.Moving)
            {
                var later = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(100) };
                later.Tick += (_, _) => { later.Stop(); Step(); };
                later.Start();
                return;
            }
            var sw = Stopwatch.StartNew();
            try
            {
                string? read;
                if (w == null) { w = new SettingsWindow(host, t, s, "general"); read = "(the window)"; } // a step of its own
                else
                {
                    w._wlanForIndex ??= wlan.IsCompletedSuccessfully ? wlan.Result : (null, []);
                    read = w.ReadNextPage();
                }
                if (read != null)
                {
                    Tally(read);
                    var next = w;
                    wlan.ContinueWith(_ => next.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Step));
                    return;
                }
                var pages = ShellSettingsSearch.Index(w._texts!.Select(kv => (kv.Key, kv.Value.Title,
                    ShellSettingsPages.OtherNames(kv.Key, k => other[k]),
                    (IReadOnlyList<string>)ShellSettingsPages.AlsoFoundBy.GetValueOrDefault(kv.Key, []), (IReadOnlyList<string>)kv.Value.Texts)));
                w.Close();
                Tally("(the index)");
                done(new PageIndex(pages, busy, longest));
            }
            catch (Exception ex)
            {
                Frostbar.Platform.Infrastructure.Log.Warn("search: Frostbar's settings pages could not be read", ex);
                w?.Close();
                done(null);
            }
            void Tally(string what)
            {
                long ms = sw.ElapsedMilliseconds;
                busy += ms;
                longest = Math.Max(longest, ms);
                Frostbar.Platform.Infrastructure.Log.Debug($"search: settings index {what} in {ms} ms");
            }
        }
        Step();
    }

    // ---- a page's life -----------------------------------------------------------------------------------------
    // The system pages follow their services while shown: what they subscribe to (and their timers) is undone when
    // another page is shown or the window closes. Made only to read their words for the search (_indexing), they start
    // nothing (no scan, no reads off the UI thread).

    readonly List<Action> _leave = new();
    bool _indexing;
    /// <summary>Counts the pages shown: an answer read off the UI thread for a page that is gone is dropped.</summary>
    int _shown;

    void OnLeave(Action undo) => _leave.Add(undo);

    void Leave()
    {
        foreach (var undo in _leave) { try { undo(); } catch (Exception) { } }
        _leave.Clear();
        _shown++;
    }

    /// <summary>
    /// Runs <paramref name="read"/> off the UI thread and <paramref name="show"/> with its answer, if this page is still
    /// the one shown (and once the button is up: see <see cref="WhenUp"/>).
    /// </summary>
    void OffUi<T>(Func<T> read, Action<T> show)
    {
        if (_indexing) return;
        int page = _shown;
        Task.Run(read).ContinueWith(t =>
        {
            if (page != _shown || t.Exception != null) return;
            var answer = t.Result;
            WhenUp(page, () => show(answer));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Runs <paramref name="show"/> now, or once the left button is up: rows made again under a press lose its click
    /// (2026-10-12, the Dock). Dropped when another page is shown meanwhile.
    /// </summary>
    void WhenUp(int page, Action show)
    {
        if (page != _shown) return;
        // Nor while a row's menu or pop-up list is open: the list stayed open over a row made again, which no longer
        // was the one in the page (2026-10-13, the Wi-Fi page under a network's "…", while Wi-Fi reported a scan).
        bool listOpen = Controls.Values.Any(c => c is MacControls.MenuButton { IsOpen: true } or MacControls.PopUp { IsOpen: true });
        if (Mouse.LeftButton != MouseButtonState.Pressed && !listOpen) { show(); return; }
        Dispatcher.BeginInvoke(async () => { await Task.Delay(150); WhenUp(page, show); });
    }

    /// <summary>
    /// Drops the controls of the rows a page is about to make again (the diagnostics still listed a forgotten network's
    /// row, no longer on the page: 2026-10-13).
    /// </summary>
    void ForgetControls(params string[] prefixes)
    {
        foreach (var k in Controls.Keys.Where(k => prefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal))).ToList()) Controls.Remove(k);
    }

    /// <summary>Calls <paramref name="tick"/> every <paramref name="ms"/> while this page is shown (the volume mixer, the battery's estimate).</summary>
    void Every(int ms, Action tick)
    {
        if (_indexing) return;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background, (_, _) => tick(), Dispatcher);
        timer.Start();
        OnLeave(timer.Stop);
    }

    /// <summary>Calls <paramref name="show"/> when <paramref name="subscribe"/>'s event fires, until the page is left.</summary>
    void Follow(Action<Action> subscribe, Action<Action> unsubscribe, Action show)
    {
        if (_indexing) return;
        int page = _shown;
        bool waiting = false;
        // A change waits for the button to come up (WhenUp); several changes meanwhile show once.
        void Calm()
        {
            if (waiting) return;
            waiting = true;
            WhenUp(page, () => { waiting = false; show(); });
        }
        subscribe(Calm);
        OnLeave(() => unsubscribe(Calm));
    }

    static IEnumerable<string> Texts(DependencyObject e)
    {
        if (e is UIElement { Visibility: Visibility.Collapsed }) yield break; // not on the page (the language's restart row until asked)
        if (e is TextBlock { Text.Length: > 0 } t) yield return t.Text;
        if (e is ContentControl { Content: string s }) yield return s;
        // A pop-up shows its choice only: the others are found too (「ロック」「通知センター」 found nothing).
        if (e is MacControls.PopUp p) foreach (var label in p.Labels) yield return label;
        foreach (var child in LogicalTreeHelper.GetChildren(e).OfType<DependencyObject>())
            foreach (var text in Texts(child)) yield return text;
    }

    /// <summary>Marks the page's rows that match, and brings the first into view.</summary>
    void MarkFound()
    {
        _found.Clear();
        if (_query.Length == 0) return;
        var tint = new SolidColorBrush(Color.FromArgb(_t.Dark ? (byte)0x40 : (byte)0x2E, _t.Accent.R, _t.Accent.G, _t.Accent.B));
        foreach (var row in _rows)
        {
            if (!Texts(row).Any(t => TextNormalizer.Normalize(t).Contains(_query))) continue;
            row.Background = tint;
            _found.Add(row);
        }
        if (_found.Count > 0)
        {
            var first = _found[0];
            // The page glides to it (it jumped: 2026-10-19, "B4").
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => SmoothScroll.IntoView(_scroll, first, _t.Animations, above: ToolbarHeight + 8));
        }
    }

    static FrameworkElement Picture(Geometry g, bool stroke) => stroke ? Glyphs.Stroke(g, 14, Brushes.White, 1.5) : Glyphs.Fill(g, 14, Brushes.White);

    Border Item(string key, FrameworkElement picture, Color color, string label)
    {
        picture.HorizontalAlignment = HorizontalAlignment.Center;
        picture.VerticalAlignment = VerticalAlignment.Center;
        var square = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(color), Child = picture, Margin = new Thickness(0, 0, 9, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(square);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = _text });
        var item = new Border { Child = row, Padding = new Thickness(6, 4, 6, 4), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Margin = new Thickness(0, GapBefore.Contains(key) ? 10 : 0, 0, 2) };
        item.MouseLeftButtonDown += (_, e) => e.Handled = true; // not a drag of the window
        item.MouseLeftButtonUp += (_, e) => { e.Handled = true; Show(key); };
        // The keys (D3): Tab reaches the section shown, ↑ ↓ go to the one above or below and show it (System Settings').
        item.Focusable = true;
        System.Windows.Automation.AutomationProperties.SetName(item, label);
        item.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space) { e.Handled = true; Show(key); return; }
            if (e.Key is not (Key.Up or Key.Down)) return;
            e.Handled = true;
            var shown = _items.Where(kv => kv.Value.Visibility == Visibility.Visible).Select(kv => kv.Key).ToList();
            int at = shown.IndexOf(key), to = at + (e.Key == Key.Down ? 1 : -1);
            if (at < 0 || to < 0 || to >= shown.Count) return;
            Show(shown[to]);
            _items[shown[to]].Focus();
            SmoothScroll.IntoView(_sideScroll, _items[shown[to]], _t.Animations);
        };
        _items[key] = item;
        return item;
    }

    // ---- pages ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Shows page <paramref name="key"/> (one of <see cref="PageKeys"/> or <see cref="SubPages"/>). Another page
    /// crossfades with the one before (B12); the same page made again (a theme, a choice that changes other rows) keeps
    /// the segmented controls' slides going.
    /// </summary>
    public void Show(string key)
    {
        if (!AllPages.Contains(key)) key = "general";
        bool again = key == Page;
        var sliding = again ? Controls.Where(kv => kv.Value is MacControls.Segmented { Sliding: true })
            .Select(kv => (kv.Key, At: ((MacControls.Segmented)kv.Value).RaisedSet)).ToList() : [];
        bool fade = !again && Page.Length > 0 && _t.Animations && IsVisible && Snapshot();
        Show(key, sliding);
        if (!fade) return;
        PageFades++;
        _page.Opacity = _title.Opacity = 0;
        Tween.Together(PageFadeMs, [new(_was, OpacityProperty, 0.0, Tween.SineInOut), new(_page, OpacityProperty, 1.0, Tween.SineInOut), new(_title, OpacityProperty, 1.0, Tween.SineInOut)], () =>
        {
            _was.Visibility = Visibility.Collapsed;
            _was.Source = null;
        });
    }

    /// <summary>How long a page takes to fade in over the one before (ms), and the pages faded so (diagnostics).</summary>
    public const double PageFadeMs = Motion.PageFade;
    public int PageFades { get; private set; }
    public double PageOpacity => _page.Opacity;

    // The page shown before, as a picture over the new one while it fades out.
    readonly Image _was = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };

    /// <summary>
    /// The page as shown now (its list's view), as the picture over the next one: a page changed in a frame (2026-10-19,
    /// the user's "B12"). False when there is nothing to take.
    /// </summary>
    bool Snapshot()
    {
        double w = _scroll.ActualWidth, h = _scroll.ActualHeight;
        if (w < 1 || h < 1) return false;
        var dpi = VisualTreeHelper.GetDpi(this);
        var picture = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(w * dpi.DpiScaleX), (int)Math.Ceiling(h * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        var view = new Rect(0, 0, w, h);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new VisualBrush(_scroll) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = view, ViewportUnits = BrushMappingMode.Absolute, Viewport = view }, null, view);
            // And the toolbar's bar over a page scrolled under it: it fades with the page (it went in a frame, the new
            // page scrolled to its top at once; 2026-11-06 review).
            if (_band.Visibility == Visibility.Visible && _band.ActualWidth > 0 && _band.ActualHeight > 0)
            {
                var at = _band.TranslatePoint(new Point(0, 0), _scroll);
                var band = new Rect(at.X, at.Y, _band.ActualWidth, _band.ActualHeight);
                dc.PushOpacity(_band.Opacity);
                dc.DrawRectangle(new VisualBrush(_band) { Stretch = Stretch.Fill }, null, band);
                dc.Pop();
            }
        }
        picture.Render(visual);
        picture.Freeze();
        Tween.Stop(_was, OpacityProperty);
        _was.Source = picture;
        _was.Width = w;
        _was.Height = h;
        _was.Opacity = 1;
        _was.Visibility = Visibility.Visible;
        return true;
    }

    void Show(string key, List<(string Key, (double X, double Width) At)> sliding)
    {
        Leave();
        Page = key;
        string section = SubPages.GetValueOrDefault(key, key);
        foreach (var (k, item) in _items)
        {
            bool on = k == section;
            item.Background = on ? _accent : Brushes.Transparent;
            if (((StackPanel)item.Child).Children[1] is TextBlock tb) tb.Foreground = on ? Brushes.White : _text;
        }
        Visited(key);
        _page.Children.Clear();
        Controls.Clear();
        _rows.Clear();
        Build(key);
        _scroll.ScrollToTop();
        MarkFound();
        foreach (var (k, at) in sliding)
            if (Controls.GetValueOrDefault(k) is MacControls.Segmented s) s.SlideFrom(at);
    }

    void Build(string key)
    {
        switch (key)
        {
            case "wifi": WifiPage(); _title.Text = _s["cc.wifi"]; break;
            case "bluetooth": BluetoothPage(); _title.Text = _s["cc.bluetooth"]; break;
            case "battery": BatteryPage(); _title.Text = _s["set.battery"]; break;
            case "sound": SoundPage(); _title.Text = _s["cc.sound"]; break;
            case "about": AboutPage(); _title.Text = _s["set.about"]; break;
            case "storage": StoragePage(); _title.Text = _s["set.storage"]; break;
            case "loginItems": LoginItemsPage(); _title.Text = _s["set.loginItems"]; break;
            case "general": General(); _title.Text = _s["set.general"]; break;
            case "accessibility": AccessibilityPage(); _title.Text = _s["set.accessibility"]; break;
            case "appearance": Appearance(); _title.Text = _s["set.appearance"]; break;
            case "menuBar": MenuBar(); _title.Text = _s["set.menuBar"]; break;
            case "dock": Dock(); _title.Text = _s["set.dock"]; break;
            case "appGrid": AppGridPage(); _title.Text = _s["set.appGrid"]; break;
            case "hotCorners": HotCorners(); _title.Text = _s["set.hotCorners"]; break;
            case "search": SearchPage(); _title.Text = _s["set.search"]; break;
            case "keyboard": KeyboardPage(); _title.Text = _s["set.keyboard"]; break;
            case "mouse": MousePage(); _title.Text = _s["set.mouse"]; break;
            case "display": DisplayPage(); _title.Text = _s["set.display"]; break;
            case "wallpaper": WallpaperPage(); _title.Text = _s["set.wallpaper"]; break;
            case "trackpad": TrackpadPage(); _title.Text = _s["set.trackpad"]; break;
            case "advanced": Advanced(); _title.Text = _s["set.advanced"]; break;
        }
    }

    void General()
    {
        // System Settings' General begins with its header, then About, Storage and Login Items, each a page of its own.
        Add(PageHeader(Glyphs.Gear, stroke: true, Color.FromRgb(0x8E, 0x8E, 0x93), _s["set.general"], _s["general.header"]));
        Add(Group(NavRow("general.about", Glyphs.Info, Color.FromRgb(0x8E, 0x8E, 0x93), _s["set.about"], "about"),
                  NavRow("general.storage", Glyphs.Drive, Color.FromRgb(0x8E, 0x8E, 0x93), _s["set.storage"], "storage"),
                  NavRow("general.loginItems", Glyphs.ListPicture, Color.FromRgb(0x8E, 0x8E, 0x93), _s["set.loginItems"], "loginItems")));
        var g = _settings.General;
        string[] langs = ["system", "ja", "en"];
        // Shown while the language chosen is not the one the shell runs in: it was shown only right after the change, and
        // gone once the page was made again (another page, a theme refresh), the new language still waiting.
        var restart = Row(_s["set.languageNote"], Button(_s["set.relaunch"], _host.Relaunch));
        Visibility Pending() => Strings.For(g.Language, CultureInfo.CurrentUICulture).Language != _s.Language ? Visibility.Visible : Visibility.Collapsed;
        restart.Visibility = Pending();
        var lang = Choice("general.language", [_s["set.followWindows"], "日本語", "English"], Array.IndexOf(langs, g.Language), i =>
        {
            g.Language = langs[i];
            _host.Apply(SettingsArea.Language);
            restart.Visibility = Pending();
        });
        Add(Group(Row(_s["set.language"], lang), restart));

        bool can = _host.TaskbarHidingAvailable;
        var hide = Switch("general.hideTaskbar", g.HideWindowsTaskbar, on => { g.HideWindowsTaskbar = on; _host.Apply(SettingsArea.Taskbar); });
        if (!can) { hide.IsEnabled = false; hide.Opacity = 0.4; }
        Add(Group(Row(_s["set.hideTaskbar"], hide, can ? _s["set.hideTaskbarDetail"] : _s["set.hideTaskbarDev"])));

        // The file manager (macOS's Finder): File Explorer, or a program chosen with "Other…" (named once chosen).
        bool custom = g.FileManager == FileManager.Custom && g.FileManagerPath.Length > 0;
        var managerRows = new List<FrameworkElement>
        {
            Row(_s["set.fileManager"], Choice("general.fileManager", [_s["fm.explorer"], custom ? FileManager.AppName(g.FileManagerPath) : _s["fm.other"]], custom ? 1 : 0,
                i =>
                {
                    if (i == 0) { g.FileManager = FileManager.Explorer; _host.Apply(SettingsArea.Other); return; }
                    PickFileManager(); // cancelled: the page is made again with File Explorer still chosen
                }), _s["set.fileManagerDetail"]),
        };
        if (custom) managerRows.Add(Row(_s["fm.app"], Button(_s["fm.change"], PickFileManager), g.FileManagerPath));
        Add(Group([.. managerRows]));

        var version = Value(Frostbar.Platform.Infrastructure.AppPaths.Version);
        Controls["general.version"] = version;
        Add(Group(Row(_s["set.version"], version)));
    }

    void Appearance()
    {
        var a = _settings.Appearance;
        string[] themes = ["system", "light", "dark"];
        Add(Group(Row(_s["set.theme"], Choice("appearance.theme", [_s["set.auto"], _s["set.light"], _s["set.dark"]], Array.IndexOf(themes, a.Theme),
            i => { a.Theme = themes[i]; _host.Apply(SettingsArea.Appearance); }), _s["set.themeDetail"]),
            AccentRow())); // macOS's accent colour under the appearance (2026-10-22)
        DarkSchedulePart(a); // Windows' own light and dark by itself (stage 4-3)
        // "translucent" is the same as "auto" (blur when Windows allows it).
        string[] materials = ["auto", "transparent", "solid"];
        Add(Group(Row(_s["set.material"], Choice("appearance.material", [_s["set.blur"], _s["set.transparent"], _s["set.solid"]],
            a.Material == "translucent" ? 0 : Array.IndexOf(materials, a.Material), i =>
            {
                bool glass = Materials.GlassPossible(_t, a);
                a.Material = materials[i];
                _host.Apply(SettingsArea.Appearance);
                if (Materials.GlassPossible(_t, a) != glass) ShowAgain(); // the Liquid Glass row below comes alive or goes dim
            }), _s["set.materialDetail"])));
        // ReduceMotion "off" = animations on.
        string[] motion = ["system", "off", "on"];
        Add(Group(Row(_s["set.animations"], Choice("appearance.animations", [_s["set.followWindows"], _s["set.on"], _s["set.off"]], Array.IndexOf(motion, a.ReduceMotion),
            i => { a.ReduceMotion = motion[i]; _host.Apply(SettingsArea.Appearance); }), _s["set.animationsDetail"])));
        // Drawn on the blur: over a solid background the switch would do nothing, so it says why instead.
        bool glassShows = Materials.GlassPossible(_t, a);
        bool lensShows = Materials.LensPossible(_t, a);
        // How tinted the glass is (macOS 27's slider, from ultra clear to tinted; the user's choice, 2026-10-18). Applied
        // to the shell's glass only: the theme's apply makes this page again, which would drop the knob being dragged.
        var tint = new MacSlider(Frostbar.Core.Shell.GlassTints.Min, Frostbar.Core.Shell.GlassTints.Max, Frostbar.Core.Shell.GlassTints.StepSize, a.GlassTint, _t);
        Controls["appearance.glassTint"] = tint;
        tint.ValueChanged += v => { a.GlassTint = (int)v; TintBand(); _host.Apply(SettingsArea.GlassTint); };
        void TintShows(bool on) { tint.IsEnabled = on; tint.Opacity = on ? 1 : 0.4; }
        TintShows(glassShows && a.LiquidGlass);
        var glass = Switch("appearance.liquidGlass", a.LiquidGlass, on =>
        {
            a.LiquidGlass = on;
            _host.Apply(SettingsArea.Appearance);
            TintShows(glassShows && on);
            if (Materials.LensPossible(_t, a) != lensShows) ShowAgain(); // the lens's row below comes alive or goes dim
        });
        if (!glassShows) { glass.IsEnabled = false; glass.Opacity = 0.4; }
        // The lens (ADR-016): the glass bends what is behind it, read from a capture of the screen without the shell's
        // glass, so the switch says that the glass is then not in screenshots (the user's choice, 2026-10-18).
        var lens = Switch("appearance.lens", a.Lens, on => { a.Lens = on; _host.Apply(SettingsArea.Appearance); });
        if (!lensShows) { lens.IsEnabled = false; lens.Opacity = 0.4; }
        Add(Group(Row(_s["set.liquidGlass"], glass, glassShows ? _s["set.liquidGlassDetail"] : _s["set.liquidGlassSolid"]),
            SliderRow(_s["set.glassTint"], tint, _s["set.glassClear"], _s["set.glassTinted"]),
            Row(_s["set.lens"], lens, lensShows ? _s["set.lensDetail"] : !Frostbar.Platform.Dwm.LensCapture.Available ? _s["set.lensNone"] : _s["set.lensNeedsGlass"])));
        Add(ScrollBarsGroup()); // macOS's scroll bars at the end of its Appearance (2026-10-22)
    }

    void MenuBar()
    {
        var m = _settings.MenuBar;
        var c = m.Clock;
        // When it hides by itself (stage 5-1): macOS's four, in its order (2026-11-01: over a full-screen app or always until then).
        var hiding = new MacControls.PopUp([.. MenuBarItems.AutoHides.Select(k => _s["bar.autoHide." + k])], Math.Max(0, MenuBarItems.AutoHides.ToList().IndexOf(m.AutoHide)), _t,
            i => { m.AutoHide = MenuBarItems.AutoHides[i]; _host.Apply(SettingsArea.MenuBarAutoHide); });
        Controls["menuBar.autoHide"] = hiding;
        Add(Group(Row(_s["set.barHeight"], Choice("menuBar.height", [_s["set.standard"], _s["set.large"]], m.Height == 28 ? 1 : 0,
            i => { m.Height = i == 1 ? 28 : 24; _host.Apply(SettingsArea.MenuBarHeight); })),
            Row(_s["bar.autoHide"], hiding, _s["bar.autoHideDetail"])));

        // The status items shown (stage 5-1; moved on the bar itself, with Ctrl and a drag).
        Add(Caption(_s["bar.items"]));
        Add(Group([.. MenuBarItems.Movable.Select(key => ItemRow(BarItemPicture(key), _s["bar.item." + key], key switch
        {
            "focus" => _s["bar.focusDetail"], "battery" => _s["bar.batteryDetail"], "nowPlaying" => _s["bar.nowPlayingDetail"], _ => null,
        }, Switch("menuBar.item." + key, m.Items.Contains(key), on => { m.Items = MenuBarItems.Set(m.Items, key, on); _host.Apply(SettingsArea.MenuBarItems); })))]));
        Add(Note(_s["bar.itemsNote"]));
        Add(Group(BatteryPercentRow()));
        Add(Caption(_s["set.clock"]));
        string[] hours = ["on", "off", "auto"];
        Add(Group(
            Row(_s["set.showDate"], Switch("clock.showDate", c.ShowDate, on => { c.ShowDate = on; _host.Apply(SettingsArea.Clock); })),
            Row(_s["set.showWeekday"], Switch("clock.showWeekday", c.ShowWeekday, on => { c.ShowWeekday = on; _host.Apply(SettingsArea.Clock); })),
            Row(_s["set.seconds"], Switch("clock.seconds", c.Seconds, on => { c.Seconds = on; _host.Apply(SettingsArea.Clock); })),
            Row(_s["set.flashSeparators"], Switch("clock.flashSeparators", c.FlashSeparators, on => { c.FlashSeparators = on; _host.Apply(SettingsArea.Clock); })),
            Row(_s["set.hourFormat"], Choice("clock.hour24", [_s["set.h24"], _s["set.h12"], _s["set.hRegion"]], Array.IndexOf(hours, c.Hour24),
                i => { c.Hour24 = hours[i]; _host.Apply(SettingsArea.Clock); }))));
        Add(RecentCountGroup()); // the system menu's Recent Items (macOS's menu bar page has the count)
        // Applies at the next start: a restart row (as the language's) while the setting and the bar differ.
        var trayRestart = Row(_s["set.restartNote"], Button(_s["set.relaunch"], _host.Relaunch));
        Visibility TrayPending() => m.ShowTrayIcons != _host.TrayAtStart ? Visibility.Visible : Visibility.Collapsed;
        trayRestart.Visibility = TrayPending();
        Add(Group(Row(_s["set.tray"], Switch("menuBar.tray", m.ShowTrayIcons, on => { m.ShowTrayIcons = on; _host.Apply(SettingsArea.Tray); trayRestart.Visibility = TrayPending(); })),
            trayRestart,
            Row(_s["set.trayBanners"], Switch("menuBar.trayBanners", m.TrayBanners, on => { m.TrayBanners = on; _host.Apply(SettingsArea.Banners); }))));

        // Where each of the tray's icons goes (stage 0-8): the bar, the » panel or nowhere; one the bar has no room for
        // goes into the » panel anyway. The choices are TrayPlaces.Names in order: the validator's list too.
        if (!_host.TrayAtStart) return;
        Add(Caption(_s["set.trayIcons"]));
        var icons = _host.TrayIcons();
        if (icons.Count == 0) { Add(Note(_s["set.trayIconsNone"])); return; }
        string[] places = [.. TrayPlaces.Names.Select(n => _s["set.tray" + char.ToUpperInvariant(n[0]) + n[1..]])];
        Add(Group([.. icons.Select(icon =>
        {
            FrameworkElement picture = icon.Icon != null ? new Image { Source = icon.Icon, Width = 16, Height = 16 } : new Border { Width = 16, Height = 16 };
            var choice = Choice("menuBar.trayIcon." + icon.Key, places, (int)TrayPlaces.Parse(m.TrayIcons.GetValueOrDefault(icon.Key)), i =>
            {
                if ((TrayPlace)i == TrayPlace.Bar) m.TrayIcons.Remove(icon.Key); // the default is not written
                else m.TrayIcons[icon.Key] = TrayPlaces.Names[i];
                _host.Apply(SettingsArea.TrayIcons);
            });
            return ItemRow(picture, icon.Name, icon.Detail == icon.Name ? null : icon.Detail, choice);
        })]));
        Add(Note(_s["set.trayIconsDetail"]));
    }

    /// <summary>A status item's picture as the bar draws it (16 DIP, the text colour), in a 28 DIP square for the row.</summary>
    FrameworkElement BarItemPicture(string key)
    {
        FrameworkElement picture;
        switch (key)
        {
            case "ime": picture = new TextBlock { Text = "あ", FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = _text }; break;
            case "wifi": picture = WifiPicture(4, _text); break;
            case "volume":
                var speaker = new Grid { Width = 16, Height = 16 };
                speaker.Children.Add(Glyphs.Fill(Glyphs.Speaker, 16, _text));
                speaker.Children.Add(Glyphs.Stroke(Glyphs.SpeakerWaves[1], 16, _text, 1.5));
                picture = speaker;
                break;
            case "battery":
                var battery = new Grid { Width = 16, Height = 16 };
                battery.Children.Add(Glyphs.Stroke(Glyphs.BatteryPicture, 16, _text, 1.4));
                battery.Children.Add(Glyphs.Fill(Glyphs.BatteryPictureLevel, 16, _text));
                picture = battery;
                break;
            case "focus": picture = Glyphs.Fill(Glyphs.Moon, 14, _text); break;
            case "nowPlaying": picture = Glyphs.Fill(Glyphs.PlayPause, 16, _text); break;
            default: picture = Glyphs.Stroke(Glyphs.Search, 16, _text, 1.5); break;
        }
        picture.HorizontalAlignment = HorizontalAlignment.Center;
        picture.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Width = 28, Height = 28, Child = picture };
    }

    void Dock()
    {
        var d = _settings.Dock;
        var size = new MacSlider(32, 128, 4, d.IconSize, _t);
        var magnified = new MacSlider(d.IconSize, 160, 4, Math.Max(d.MagnifiedSize, d.IconSize), _t);
        Controls["dock.iconSize"] = size;
        Controls["dock.magnifiedSize"] = magnified;
        size.ValueChanged += v =>
        {
            d.IconSize = (int)v;
            magnified.SetRange(d.IconSize, 160);
            d.MagnifiedSize = Math.Max(d.MagnifiedSize, d.IconSize);
            _host.Apply(SettingsArea.Dock);
        };
        magnified.ValueChanged += v => { d.MagnifiedSize = (int)v; _host.Apply(SettingsArea.Dock); };
        // Room around the icons: between them, and on the plate above and below them.
        var spacing = new MacSlider(0, 24, 2, d.Spacing, _t);
        var padding = new MacSlider(2, 24, 2, d.Padding, _t);
        Controls["dock.spacing"] = spacing;
        Controls["dock.padding"] = padding;
        spacing.ValueChanged += v => { d.Spacing = (int)v; _host.Apply(SettingsArea.Dock); };
        padding.ValueChanged += v => { d.Padding = (int)v; _host.Apply(SettingsArea.Dock); };
        magnified.IsEnabled = d.Magnification;
        magnified.Opacity = d.Magnification ? 1 : 0.4;
        var magnify = Switch("dock.magnification", d.Magnification, on =>
        {
            d.Magnification = on;
            magnified.IsEnabled = on;
            magnified.Opacity = on ? 1 : 0.4;
            _host.Apply(SettingsArea.Dock);
        });
        Add(Group(
            SliderRow(_s["set.size"], size, _s["set.small"], _s["set.big"]),
            Row(_s["set.magnification"], magnify),
            SliderRow(_s["set.magnifiedSize"], magnified, _s["set.small"], _s["set.big"]),
            SliderRow(_s["set.spacing"], spacing, _s["set.narrow"], _s["set.wide"]),
            SliderRow(_s["set.padding"], padding, _s["set.narrow"], _s["set.wide"])));
        // The edge it stands on (stage 7), in macOS's order: left, bottom, right.
        string[] positions = SettingsValidator.DockPositions;
        var position = Choice("dock.position", [.. positions.Select(p => _s["set.pos." + p])], Array.IndexOf(positions, d.Position), i =>
        {
            d.Position = positions[i];
            _host.Apply(SettingsArea.Dock);
        });
        Add(Group(
            Row(_s["set.dockPosition"], position),
            // macOS's: off (the default), a minimized window shows as its own picture before the trash.
            Row(_s["set.minimizeIntoIcon"], Switch("dock.minimizeIntoIcon", d.MinimizeIntoIcon, on => { d.MinimizeIntoIcon = on; _host.Apply(SettingsArea.Dock); })),
            Row(_s["set.autoHide"], Switch("dock.autoHide", d.AutoHide, on => { d.AutoHide = on; _host.Apply(SettingsArea.Dock); })),
            Row(_s["set.bounce"], Switch("dock.bounce", d.BounceOnLaunch, on => { d.BounceOnLaunch = on; _host.Apply(SettingsArea.Other); })),
            Row(_s["set.recent"], Switch("dock.recent", d.ShowRecent, on => { d.ShowRecent = on; _host.Apply(SettingsArea.Dock); })),
            Row(_s["set.indicators"], Switch("dock.indicators", d.ShowIndicators, on => { d.ShowIndicators = on; _host.Apply(SettingsArea.Dock); })),
            Row(_s["set.clickMinimizes"], Switch("dock.clickMinimizes", d.ClickActiveAppMinimizes, on => { d.ClickActiveAppMinimizes = on; _host.Apply(SettingsArea.Other); }))));
        DesktopPart(); // Desktop & Dock's default browser and desktop items (2026-10-22)
        WindowsPart(); // and its windows
    }

    /// <summary>
    /// The app grid (opened from the Dock): its icons' size and how many go across and down a page. Applied when a
    /// slider is let go (the grid is laid out again, and made again for larger icons). The size slider ends at the
    /// largest icon that fits the chosen columns and rows. A page of its own: on the Dock's page, its icon size was taken
    /// for the Dock's (2026-10-12).
    /// </summary>
    void AppGridPage()
    {
        var g = _settings.AppGrid;
        var (auto, fits) = _host.AppGridIconSizes(g.Columns, g.Rows);
        double Shown(int fit, int automatic) => g.IconSize > 0 ? Math.Min(g.IconSize, fit) : automatic;
        var icon = new MacSlider(GridGeometry.MinIcon, Math.Max(GridGeometry.MinIcon + 4, fits), 4, Shown(fits, auto), _t);
        var columns = new MacSlider(4, 12, 1, g.Columns, _t);
        var rows = new MacSlider(3, 8, 1, g.Rows, _t);
        Controls["appGrid.iconSize"] = icon;
        Controls["appGrid.columns"] = columns;
        Controls["appGrid.rows"] = rows;
        double was = icon.Value;
        void Refit()
        {
            var (a, f) = _host.AppGridIconSizes(g.Columns, g.Rows);
            icon.SetRange(GridGeometry.MinIcon, Math.Max(GridGeometry.MinIcon + 4, f));
            icon.Value = was = Shown(f, a);
        }
        // Automatic (0): as large as the cells allow nicely, following the columns and rows. Once the slider had been
        // touched there was no way back to it, and a plain click on the slider fixed the size it showed.
        void Automatic(bool on) { icon.IsEnabled = !on; icon.Opacity = on ? 0.4 : 1; }
        var autoSize = Switch("appGrid.autoIconSize", g.IconSize == 0, on =>
        {
            g.IconSize = on ? 0 : (int)icon.Value;
            Automatic(on);
            Refit();
            _host.Apply(SettingsArea.AppGrid);
        });
        Automatic(g.IconSize == 0);
        icon.Committed += v =>
        {
            if (v == was) return; // let go where it was
            was = v;
            g.IconSize = (int)v;
            _host.Apply(SettingsArea.AppGrid);
        };
        columns.Committed += v => { g.Columns = (int)v; Refit(); _host.Apply(SettingsArea.AppGrid); };
        rows.Committed += v => { g.Rows = (int)v; Refit(); _host.Apply(SettingsArea.AppGrid); };
        Add(Group(
            Row(_s["set.appGridAutoSize"], autoSize, _s["set.appGridAutoSizeDetail"]),
            SliderRow(_s["set.appGridIcons"], icon, _s["set.small"], _s["set.big"]),
            SliderRow(_s["set.appGridColumns"], columns, "4", "12"),
            SliderRow(_s["set.appGridRows"], rows, "3", "8")));
    }

    // The corners' actions and the shortcuts' come from Core, the validator's lists too: a list of its own here let the
    // validator lack "appWindows", which then went back to the default at the next start (2026-10-15).
    static readonly string[] CornerActions = HotCornerActions.ForCorners;

    /// <summary>Hot corners (macOS's sheet of the same name): the screen with a pop-up at each corner, then the modifier and the delay.</summary>
    void HotCorners()
    {
        var h = _settings.HotCorners;
        string[] labels = CornerActions.Select(a => _s["corner." + a]).ToArray();
        MacControls.PopUp Corner(string key, string value, Action<string> set, HorizontalAlignment x, VerticalAlignment y)
        {
            var p = new MacControls.PopUp(labels, Array.IndexOf(CornerActions, value), _t, i => { set(CornerActions[i]); _host.Apply(SettingsArea.HotCorners); })
            {
                HorizontalAlignment = x, VerticalAlignment = y,
            };
            Controls[key] = p;
            // Named for a screen reader by its corner (D4): it stands alone, not in a row.
            System.Windows.Automation.AutomationProperties.SetName(p, _s["set.hotCorners"] + " " + _s["corner.at." + key[(key.IndexOf('.') + 1)..]]);
            return p;
        }
        var grid = new Grid { Margin = new Thickness(14, 16, 14, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Put(UIElement e, int row, int column) { Grid.SetRow(e, row); Grid.SetColumn(e, column); grid.Children.Add(e); }
        Put(Corner("hotCorners.topLeft", h.TopLeft, v => h.TopLeft = v, HorizontalAlignment.Right, VerticalAlignment.Bottom), 0, 0);
        Put(Corner("hotCorners.topRight", h.TopRight, v => h.TopRight = v, HorizontalAlignment.Left, VerticalAlignment.Bottom), 0, 2);
        Put(Corner("hotCorners.bottomLeft", h.BottomLeft, v => h.BottomLeft = v, HorizontalAlignment.Right, VerticalAlignment.Top), 2, 0);
        Put(Corner("hotCorners.bottomRight", h.BottomRight, v => h.BottomRight = v, HorizontalAlignment.Left, VerticalAlignment.Top), 2, 2);
        // A small picture of the screen between them: a menu bar and a Dock.
        var screen = new Grid { Width = 136, Height = 86, Margin = new Thickness(8, 8, 8, 8) };
        screen.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(_t.Dark ? Color.FromRgb(0x3A, 0x3A, 0x3E) : Color.FromRgb(0xD9, 0xDC, 0xE3)), BorderBrush = _line, BorderThickness = new Thickness(1) });
        screen.Children.Add(new Border { Height = 7, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(6, 6, 0, 0), Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) });
        screen.Children.Add(new Border { Width = 70, Height = 9, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5), CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)) });
        Put(screen, 1, 1);
        Add(Group(grid));
        Add(new TextBlock { Text = _s["set.cornersNote"], FontSize = 11.5, Foreground = _dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, -6, 4, 12) });

        string[] modifiers = ["none", "ctrl", "alt", "shift", "win"];
        var delay = new MacSlider(100, 800, 50, h.DwellMs, _t);
        Controls["hotCorners.delay"] = delay;
        delay.ValueChanged += v => { h.DwellMs = (int)v; _host.Apply(SettingsArea.HotCorners); };
        Add(Group(
            Row(_s["set.cornerModifier"], Choice("hotCorners.modifier", [_s["set.none"], "Ctrl", "Alt", "Shift", "Win"], Array.IndexOf(modifiers, h.Modifier),
                i => { h.Modifier = modifiers[i]; _host.Apply(SettingsArea.HotCorners); }), _s["set.cornerModifierDetail"]),
            SliderRow(_s["set.cornerDelay"], delay, _s["set.shorter"], _s["set.longer"])));

        // Keys for the same actions are on the Keyboard page (macOS keeps them in its Keyboard Shortcuts).
        Add(Group(NavRow("hotCorners.keys", Glyphs.Keyboard, Color.FromRgb(0x8E, 0x8E, 0x93), _s["set.shortcuts"], "keyboard")));
    }

    /// <summary>
    /// Keyboard (macOS's Keyboard ▸ Keyboard Shortcuts): the keys that open what the hot corners open (Mission Control),
    /// and those that move the window in front as the Window menu's Move &amp; Resize does (stage 3-2; none by default).
    /// A touchpad's gestures can send them.
    /// </summary>
    void KeyboardPage()
    {
        _shortcutShows.Clear();
        KeyRepeat(); // first, as macOS's Keyboard page has it (stage 4-2)
        Add(Caption(_s["set.keysOpen"]));
        Add(Group(HotCornerActions.ForOpenKeys.Select(a => ShortcutRow(a, _s["corner." + a])).ToArray()));
        Add(Caption(_s["set.keysWindows"]));
        Add(Group(HotCornerActions.ForWindowKeys.Select(a => ShortcutRow(a, _s["key." + a])).ToArray()));
        Add(new TextBlock { Text = _s["set.windowKeysNote"], FontSize = 11.5, Foreground = _dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, -6, 4, 12) });
        var touchpad = MacControls.Button(_s["set.touchpadSettings"], primary: false, _t, cancels: false);
        touchpad.HorizontalAlignment = HorizontalAlignment.Right;
        touchpad.Click += (_, _) => Menus.CommandRunner.Launch("ms-settings:devices-touchpad");
        Controls["shortcuts.touchpad"] = touchpad;
        Add(new TextBlock { Text = _s["set.shortcutsNote"], FontSize = 11.5, Foreground = _dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 8) });
        Add(touchpad);
    }

    /// <summary>The shortcut rows' redraws (a key moved from one action to another redraws both).</summary>
    readonly List<Action> _shortcutShows = new();

    /// <summary>
    /// A row with the keys for <paramref name="action"/>: a button showing them (or "None"). Pressed, it waits for keys
    /// held with Ctrl, Alt or Win (a key alone would be taken from every app); Esc gives up, Delete or Backspace clears.
    /// The shell's own shortcuts are let go meanwhile, so keys already set reach the page.
    /// </summary>
    FrameworkElement ShortcutRow(string action, string label)
    {
        var h = _settings.HotCorners;
        return KeysRow("shortcut." + action, action, label,
            () => h.Shortcuts.TryGetValue(action, out var k) ? k : null,
            text => { if (text == null) h.Shortcuts.Remove(action); else h.Shortcuts[action] = text; });
    }

    /// <summary>
    /// The same keys set elsewhere move to <paramref name="to"/> (a hot corner's action, or the search's
    /// <see cref="ShellApp.SearchShortcut"/>): two registrations of the same keys, the second fails.
    /// </summary>
    void MoveKeys(Shortcut keys, string to)
    {
        var h = _settings.HotCorners;
        foreach (var other in h.Shortcuts.Where(kv => kv.Key != to && Shortcut.Parse(kv.Value) == keys).Select(kv => kv.Key).ToList()) h.Shortcuts.Remove(other);
        if (to != ShellApp.SearchShortcut && Shortcut.Parse(_settings.Search.Hotkey) == keys) _settings.Search.Hotkey = SearchSettings.NoHotkey;
    }

    /// <summary>
    /// A row with keys: a button showing them (or "None"). Pressed, it waits for keys held with Ctrl, Alt or Win (a key
    /// alone would be taken from every app); Esc gives up, Delete or Backspace clears. The shell's own shortcuts are let
    /// go meanwhile, so keys already set reach the page. <paramref name="taken"/> names them for the shell, which says
    /// whether another app has them.
    /// </summary>
    FrameworkElement KeysRow(string control, string taken, string label, Func<string?> get, Action<string?> set)
    {
        var box = MacControls.Button("", primary: false, _t, cancels: false);
        box.MinWidth = 150;
        var detail = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed };
        bool capturing = false;
        string? note = null;
        void Show()
        {
            var keys = Shortcut.Parse(get());
            box.Content = capturing ? _s["set.shortcutPress"] : keys is { } s ? Frostbar.App.MenuBar.MenuBarController.FormatKeys(s) : _s["set.shortcutNone"];
            string? text = note ?? (keys != null && !capturing && _host.ShortcutTaken(taken) ? _s["set.shortcutTaken"] : null);
            detail.Text = text ?? "";
            detail.Foreground = note != null ? _dim : new SolidColorBrush(SystemTint.Red(_t.Dark));
            detail.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
        }
        void Stop()
        {
            if (!capturing) return;
            capturing = false;
            _host.HoldShortcuts(false);
            foreach (var show in _shortcutShows) show();
        }
        box.Click += (_, _) =>
        {
            if (capturing) { Stop(); return; }
            capturing = true;
            note = null;
            _host.HoldShortcuts(true);
            box.Focus();
            Show();
        };
        box.LostKeyboardFocus += (_, _) => Stop();
        box.PreviewKeyDown += (_, e) =>
        {
            if (!capturing) return;
            e.Handled = true; // not the window's: Esc would close it
            var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            var held = Keyboard.Modifiers;
            if (held == ModifierKeys.None && key == Key.Escape) { Stop(); return; }
            if (held == ModifierKeys.None && key is Key.Delete or Key.Back)
            {
                set(null);
                _host.Apply(SettingsArea.HotCorners);
                Stop();
                return;
            }
            var mods = (held.HasFlag(ModifierKeys.Control) ? Mods.Ctrl : Mods.None) | (held.HasFlag(ModifierKeys.Alt) ? Mods.Alt : Mods.None)
                       | (held.HasFlag(ModifierKeys.Shift) ? Mods.Shift : Mods.None) | (held.HasFlag(ModifierKeys.Windows) ? Mods.Win : Mods.None);
            var keys = new Shortcut(mods, (ushort)KeyInterop.VirtualKeyFromKey(key));
            if (!HotCornerActions.KeysAllowed(keys) || keys.Store() is not { } text)
            {
                note = _s["set.shortcutWithModifier"];
                Show();
                return;
            }
            // The same keys set for another action (or for the search) move here.
            MoveKeys(keys, taken);
            set(text);
            Stop(); // the shell takes its shortcuts back, these among them
            _host.Apply(SettingsArea.HotCorners);
            foreach (var show in _shortcutShows) show();
        };
        Controls[control] = box;
        _shortcutShows.Add(Show);
        Show();
        var row = (Border)Row(label, box);
        var words = (StackPanel)((Grid)row.Child).Children[0];
        words.Children.Add(detail);
        return row;
    }

    static readonly (string name, string url)[] Engines =
    [
        ("Google", "https://www.google.com/search?q={q}"),
        ("Bing", "https://www.bing.com/search?q={q}"),
        ("DuckDuckGo", "https://duckduckgo.com/?q={q}"),
    ];

    void SearchPage()
    {
        var q = _settings.Search;
        // The keys that open the search (stage 6-1): recorded as the shortcuts are; Delete leaves none (the menu bar's
        // magnifier still opens it).
        _shortcutShows.Clear();
        Add(Group(KeysRow("search.hotkey", ShellApp.SearchShortcut, _s["set.searchKey"],
            () => q.Hotkey == SearchSettings.NoHotkey ? null : q.Hotkey, text => q.Hotkey = text ?? SearchSettings.NoHotkey)));
        // The file search's source: a choice only where Everything runs (or was chosen, to be able to set it back);
        // elsewhere "auto" is Windows Search and there is nothing to choose.
        if (Frostbar.Platform.Search.EverythingClient.Running() || q.FileSource == "everything")
        {
            string[] sources = ["auto", "everything", "windowsSearch"];
            Add(Group(Row(_s["set.fileSource"], Choice("search.fileSource", [_s["set.auto"], "Everything", "Windows Search"], Array.IndexOf(sources, q.FileSource),
                i => { q.FileSource = sources[i]; _host.Apply(SettingsArea.Other); }), _s["set.fileSourceDetail"])));
        }
        int engine = Array.FindIndex(Engines, e => e.url == q.WebSearchUrl);
        Add(Group(Row(_s["set.web"], Choice("search.web", Engines.Select(e => e.name).ToArray(), engine,
            i => { q.WebSearchUrl = Engines[i].url; _host.Apply(SettingsArea.Other); }), engine < 0 ? _s["set.webCustom"] : null)));
        SearchResultsPart(); // Spotlight's kinds of results (2026-10-22)
    }

    void Advanced()
    {
        var a = _settings.Advanced;
        Add(Group(Row(_s["set.restoreTaskbar"], Button(_s["set.restore"], _host.RestoreTaskbarNow), _s["set.restoreTaskbarDetail"])));
        string[] levels = ["error", "warn", "info", "debug"];
        Add(Group(
            Row(_s["set.logLevel"], Choice("advanced.logLevel", [_s["set.logError"], _s["set.logWarn"], _s["set.logInfo"], _s["set.logDebug"]], Array.IndexOf(levels, a.LogLevel),
                i => { a.LogLevel = levels[i]; _host.Apply(SettingsArea.Log); })),
            Row(_s["set.openLogs"], Button(_s["set.open"], () => Open(_host.LogFolder, select: false))),
            Row(_s["set.openSettings"], Button(_s["set.show"], () => Open(_host.SettingsFile, select: true)))));
    }

    /// <summary>In the file manager of the settings (File Explorer, or the app chosen).</summary>
    static void Open(string path, bool select)
    {
        if (select) FileManager.Reveal(path);
        else FileManager.OpenFolder(path);
    }

    /// <summary>The program that opens folders, from a file dialog; the page is made again either way (to name it, or to put the choice back).</summary>
    void PickFileManager()
    {
        var g = _settings.General;
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = _s["fm.pick"],
            Filter = $"{_s["fm.programs"]}|*.exe",
            InitialDirectory = System.IO.File.Exists(g.FileManagerPath) ? System.IO.Path.GetDirectoryName(g.FileManagerPath) : programs,
        };
        if (dialog.ShowDialog(this) == true)
        {
            g.FileManager = FileManager.Custom;
            g.FileManagerPath = dialog.FileName;
            _host.Apply(SettingsArea.Other);
        }
        ShowAgain();
    }

    // ---- pieces -----------------------------------------------------------------------------------------------

    void Add(UIElement e) => _page.Children.Add(e);

    TextBlock Caption(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Foreground = _text, Margin = new Thickness(4, 10, 0, 6) };

    Border Group(params FrameworkElement[] rows)
    {
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0)
            {
                var line = new Border { Height = 1, Background = _line, Margin = new Thickness(12, 0, 12, 0) };
                // A hidden row hides the line above it too.
                if (rows[i].Visibility != Visibility.Visible) line.Visibility = Visibility.Collapsed;
                var row = rows[i];
                row.IsVisibleChanged += (_, _) => line.Visibility = row.Visibility;
                stack.Children.Add(line);
            }
            stack.Children.Add(rows[i]);
        }
        var (edge, width) = CardEdge();
        var card = new Border
        {
            Background = _card, CornerRadius = new CornerRadius(10), Child = stack, Margin = new Thickness(0, 0, 0, 12),
            BorderBrush = edge, BorderThickness = new Thickness(width),
        };
        PressSpill.SetGlass(card, true); // a press near it lights its edge (the glass window)
        return card;
    }

    /// <summary>
    /// A card's border: plain in both appearances with increase contrast, a rim of light on the glass window (lit from
    /// its top), faint otherwise (none in the dark).
    /// </summary>
    (Brush Brush, double Width) CardEdge()
    {
        if (Materials.HighContrastEdges && !_t.HighContrast) return (_line, 1);
        if (Look == "glass")
            return (new LinearGradientBrush(_t.Dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                _t.Dark ? Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0, 0, 0), 90), 1);
        return (_line, _t.Dark ? 0 : 0.5);
    }

    /// <summary>A row of a group, in a rounded cell that a search marks (<see cref="MarkFound"/>).</summary>
    Border Cell(FrameworkElement content)
    {
        var cell = new Border { Child = content, CornerRadius = new CornerRadius(7), Margin = new Thickness(4, 2, 4, 2) };
        _rows.Add(cell);
        return cell;
    }

    FrameworkElement Row(string label, FrameworkElement? control, string? detail = null)
    {
        // The row's words name its control for a screen reader (D4: the switches and choices had no name).
        if (control != null && string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(control))) System.Windows.Automation.AutomationProperties.SetName(control, label);
        var grid = new Grid { Margin = new Thickness(8, 7, 8, 7), MinHeight = 22 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = label, Foreground = _text, TextWrapping = TextWrapping.Wrap });
        if (detail != null) words.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Foreground = _dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        grid.Children.Add(words);
        if (control != null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
        return Cell(grid);
    }

    /// <summary>A row that opens another page (General → About): a small coloured picture, the label, a chevron; the whole row takes the click.</summary>
    FrameworkElement NavRow(string key, Geometry glyph, Color color, string label, string page)
    {
        var square = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(color), Margin = new Thickness(0, 0, 10, 0), Child = Glyphs.Stroke(glyph, 13, Brushes.White, 1.5) };
        var chevron = Glyphs.Stroke(Glyphs.Chevron, 12, _dim, 1.6);
        var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(8, 6, 8, 6), Background = Brushes.Transparent };
        DockPanel.SetDock(square, System.Windows.Controls.Dock.Left);
        DockPanel.SetDock(chevron, System.Windows.Controls.Dock.Right);
        dock.Children.Add(square);
        dock.Children.Add(chevron);
        dock.Children.Add(new TextBlock { Text = label, Foreground = _text, VerticalAlignment = VerticalAlignment.Center });
        var cell = Cell(dock);
        cell.Background = Brushes.Transparent;
        cell.MouseEnter += (_, _) => { if (!_found.Contains(cell)) cell.Background = _hover; };
        cell.MouseLeave += (_, _) => { if (!_found.Contains(cell)) cell.Background = Brushes.Transparent; };
        cell.MouseLeftButtonDown += (_, e) => e.Handled = true;
        cell.MouseLeftButtonUp += (_, e) => { e.Handled = true; Show(page); };
        // The keys reach it too (D3, as the sidebar's sections): About, Storage and Login Items took the mouse only.
        cell.Focusable = true;
        System.Windows.Automation.AutomationProperties.SetName(cell, label);
        cell.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { e.Handled = true; Show(page); } };
        Controls[key] = cell;
        return cell;
    }

    /// <summary>A slider under its label, with words for its ends (macOS's Dock size).</summary>
    FrameworkElement SliderRow(string label, MacSlider slider, string low, string high)
    {
        if (string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(slider))) System.Windows.Automation.AutomationProperties.SetName(slider, label); // D4
        var grid = new Grid { Margin = new Thickness(8, 7, 8, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new TextBlock { Text = label, Foreground = _text, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
        var column = new StackPanel();
        column.Children.Add(slider);
        var ends = new Grid { Margin = new Thickness(2, 0, 2, 0) };
        ends.Children.Add(new TextBlock { Text = low, FontSize = 11, Foreground = _dim, HorizontalAlignment = HorizontalAlignment.Left });
        ends.Children.Add(new TextBlock { Text = high, FontSize = 11, Foreground = _dim, HorizontalAlignment = HorizontalAlignment.Right });
        column.Children.Add(ends);
        Grid.SetColumn(column, 1);
        grid.Children.Add(column);
        return Cell(grid);
    }

    PillSwitch Switch(string key, bool on, Action<bool> toggled)
    {
        var s = new PillSwitch(_accent, new SolidColorBrush(_t.Dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0, 0, 0)), on, _t.Animations);
        s.Toggled += toggled;
        Controls[key] = s;
        return s;
    }

    MacControls.Segmented Choice(string key, IReadOnlyList<string> labels, int selected, Action<int> chosen)
    {
        var c = MacControls.Segments(labels, selected, _t, chosen);
        Controls[key] = c;
        return c;
    }

    Button Button(string text, Action click)
    {
        var b = MacControls.Button(text, primary: false, _t, cancels: false);
        b.MinWidth = 72;
        b.Padding = new Thickness(12, 2, 12, 3);
        b.Margin = new Thickness(0);
        b.Click += (_, _) => click();
        return b;
    }

    TextBlock Value(string text) => new() { Text = text, Foreground = _dim };
}
