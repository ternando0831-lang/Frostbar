using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Frostbar.App.Common;
using Frostbar.Core.MenuBar;
using Frostbar.Core.Settings;
using Frostbar.Core.Time;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Shell;
using Frostbar.Platform.Sys;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.MenuBar;

/// <summary>A tray icon as the menu bar shows it (built from the tray thread's snapshot), and where the user wants it.</summary>
public sealed record TrayIconView(string Key, ImageSource? Icon, string Tooltip, TrayPlace Place = TrayPlace.Bar);

/// <summary>
/// The menu bar: system menu mark, the front app's bold name and menu titles on the left; status items on the right.
/// It never activates (the app in front keeps focus and its IME state). Titles report press/hover; the menu controller
/// opens the menus.
/// </summary>
public sealed class MenuBarWindow : Window
{
    const double BarFont = 13;
    readonly StackPanel _left = new() { Orientation = Orientation.Horizontal }, _right = new() { Orientation = Orientation.Horizontal };
    readonly StackPanel _tray = new() { Orientation = Orientation.Horizontal };
    readonly List<Border> _titles = new();
    readonly Border _logo;
    readonly TextBlock _clock = new() { VerticalAlignment = VerticalAlignment.Center }, _ime = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
    readonly TextBlock _batteryText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(0, 0, 5, 0) };
    readonly Canvas _wifi = new() { Width = 16, Height = 16 }, _volume = new() { Width = 16, Height = 16 }, _battery = new() { Width = 25, Height = 12 };
    readonly Border _imeItem, _wifiItem, _volumeItem, _batteryItem, _focusItem;
    /// <summary>The » button left of the tray: the icons the bar has no room for, or the user put there (stage 0-8).</summary>
    readonly Border _more;
    readonly ClockSettings _clockSettings;
    readonly DispatcherTimer _tick = new(DispatcherPriority.Background);
    WindowBackdrop? _backdrop;
    Brush _text = Brushes.White, _hover = Brushes.Transparent;
    int? _open;
    /// <summary>Fixed glyphs recoloured on theme changes (the status glyphs are rebuilt by their setters).</summary>
    readonly List<Path> _themed = new();

    Path Themed(Path p) { _themed.Add(p); return p; }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;
    public IntPtr BackdropHandle => _backdrop?.BackdropHandle ?? IntPtr.Zero;

    /// <summary>Fades in (a full-screen app's menu bar coming down: <see cref="MenuBarPeek"/>); at once for 0.</summary>
    public void FadeIn(double ms) { Fades.Reset(this, _backdrop); Fades.FadeIn(this, _backdrop, ms); }

    /// <summary>Fades out, then <paramref name="done"/>.</summary>
    public void FadeOut(double ms, Action done) => Fades.FadeOut(this, _backdrop, ms, done);

    /// <summary>Fully shown and taking input again.</summary>
    public void ResetFade() => Fades.Reset(this, _backdrop);
    /// <summary>-1 is the system menu (the mark), 0 the app menu, then the app's menus.</summary>
    public event Action<int>? TitlePressed;
    public event Action<int>? TitleEntered;
    public event Action<string, Rect>? StatusClicked;
    /// <summary>A tray icon was pressed (true) or released (false) with a mouse button.</summary>
    public event Action<string, MouseButton, bool>? TrayIconMouse;
    /// <summary>The tray icons moved on screen (new icons, or the clock got wider).</summary>
    public event Action? TrayMoved;
    /// <summary>A press on the bar outside any item (closes an open menu, like macOS).</summary>
    public event Action? BackgroundPressed;
    /// <summary>Message time (GetTickCount clock) of the press or hover that is being handled, for latency figures.</summary>
    public int LastInputTime { get; set; }
    public string ClockText => _clock.Text;
    public string ImeText => _ime.Text;
    public IReadOnlyList<string> Titles => _titles.Select(t => ((TextBlock)t.Child).Text).ToList();

    public MenuBarWindow(MonitorInfo mon, double heightDip, ClockSettings clock)
    {
        _clockSettings = clock;
        Title = "Frostbar.MenuBar";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // takes clicks on its empty parts
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Left = mon.Bounds.Left / mon.Scale;
        Top = mon.Bounds.Top / mon.Scale;
        Width = mon.Bounds.Width / mon.Scale;
        Height = heightDip;
        FontFamily = Materials.UiFont;
        FontSize = BarFont;
        UseLayoutRounding = true;
        Language = XmlLanguage.GetLanguage("ja-JP"); // Japanese app names render with Japanese glyphs

        _logo = Item(Themed(Glyphs.Fill(Glyphs.Logo, 14, Brushes.White)), new Thickness(12, 0, 12, 0));
        _logo.MouseLeftButtonDown += (_, e) => { e.Handled = true; LastInputTime = e.Timestamp; PressItem(_logo, e); TitlePressed?.Invoke(-1); };
        _logo.MouseEnter += (_, e) => { LastInputTime = e.Timestamp; TitleEntered?.Invoke(-1); };
        _left.Children.Add(_logo);

        // Now Playing (stage 5-2): there while something plays or is paused (macOS's "Show When Active").
        Status("nowPlaying", Themed(Glyphs.Fill(Glyphs.PlayPause, 16, Brushes.White)));
        _imeItem = Status("ime", _ime);
        _wifiItem = Status("wifi", _wifi);
        _volumeItem = Status("volume", _volume);
        var batteryRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        batteryRow.Children.Add(_batteryText);
        batteryRow.Children.Add(_battery);
        _batteryItem = Status("battery", batteryRow);
        // A moon while do not disturb or a focus session is on (macOS's Focus in the menu bar); out of sight otherwise.
        _focusItem = Status("focus", Themed(Glyphs.Fill(Glyphs.Moon, 14, Brushes.White)));
        _focusItem.Visibility = Visibility.Collapsed;
        // As wide as a tray icon (16 + 5 + 5 + 1 + 1), so the fit counts it as one (MoreWidth).
        _more = Status("tray-overflow", Themed(Glyphs.Stroke(Glyphs.Overflow, 16, Brushes.White, 1.6)), new Thickness(5, 0, 5, 0));
        _more.Visibility = Visibility.Collapsed;
        Status("search", Themed(Glyphs.Stroke(Glyphs.Search, 16, Brushes.White)));
        // The privacy capsule (stage 5-2): left of the Control Center while the microphone or the camera is in use.
        Status("privacy", _privacyPill, new Thickness(2, 0, 2, 0)).Visibility = Visibility.Collapsed;
        Status("control-center", ControlCenterGlyph());
        Status("clock", _clock, new Thickness(8, 0, 12, 0));
        // The ones the user shows, hides and moves (stage 5-1): put in place by ArrangeItems, moved with Ctrl and a drag.
        foreach (var key in MenuBarItems.Movable) Movable(key, _status[key]);
        ArrangeItems(MenuBarItems.Movable);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_right, 1);
        grid.Children.Add(_left);
        grid.Children.Add(_right);
        Content = grid;

        SourceInitialized += (_, _) =>
        {
            Native.AddExStyle(Handle, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(Handle).AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if ((uint)msg == Native.WM_MOUSEACTIVATE) { handled = true; return new IntPtr(Native.MA_NOACTIVATE); }
                return IntPtr.Zero;
            });
        };
        MouseDown += (_, e) => { if (!e.Handled) BackgroundPressed?.Invoke(); };
        _right.SizeChanged += (_, _) => { TrayMoved?.Invoke(); RequestFit(); }; // the clock, the battery's text…
        SizeChanged += (_, _) => RequestFit();
        _tick.Tick += (_, _) => UpdateClock();
        UpdateClock();
    }

    Canvas ControlCenterGlyph()
    {
        var c = new Canvas { Width = 16, Height = 16 };
        c.Children.Add(Themed(Glyphs.Stroke(Glyphs.ControlCenterTracks, 16, Brushes.White, 1.6)));
        c.Children.Add(Themed(Glyphs.Fill(Glyphs.ControlCenterKnobs, 16, Brushes.White)));
        return c;
    }

    Border Item(UIElement content, Thickness padding) => new()
    {
        Child = content, Padding = padding, CornerRadius = new CornerRadius(4), Margin = new Thickness(1, 3, 1, 3),
        Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Stretch,
    };

    readonly Dictionary<string, Border> _status = new();

    Border Status(string key, UIElement content, Thickness? padding = null)
    {
        var b = Item(content, padding ?? new Thickness(7, 0, 7, 0));
        b.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            LastInputTime = e.Timestamp;
            PressItem(b, e);
            StatusClicked?.Invoke(key, ScreenRect(b));
        };
        _status[key] = b;
        return b;
    }

    // ---- the status items the user shows, hides and moves (stage 5-1) -----------------------------------------------
    // Left to right: the tray's » and icons, the movable items in the settings' order, the Control Center, the clock (as
    // macOS keeps its Control Center and clock at the end). An item shows when it is shown in the settings and has
    // something to show (the input mode while there is one, the battery on a PC that has one, Focus while it is on).

    readonly Dictionary<string, bool> _available = new() { ["nowPlaying"] = false, ["ime"] = true, ["wifi"] = true, ["volume"] = true, ["battery"] = true, ["focus"] = false, ["search"] = true };

    readonly Border _privacyPill = new() { Height = 18, CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Center };
    bool _darkTheme = true;

    /// <summary>What the privacy capsule shows (diagnostics): the kind, and the pictures in it.</summary>
    public (PrivacyKind kind, int pictures) PrivacyShown { get; private set; }

    /// <summary>
    /// The privacy capsule (the user's choice B): orange with the microphone while it is in use, green while the camera
    /// is (with the microphone's picture too when both are). macOS's colours, in their light and dark shades.
    /// </summary>
    public void SetPrivacy(PrivacyState state, string tooltip)
    {
        var item = _status["privacy"];
        var kind = state.Kind;
        PrivacyShown = (kind, 0);
        item.Visibility = kind == PrivacyKind.None ? Visibility.Collapsed : Visibility.Visible;
        if (kind == PrivacyKind.None) { Describe("privacy", null); return; }
        _privacyPill.Background = new SolidColorBrush(kind == PrivacyKind.Camera
            ? (_darkTheme ? Color.FromRgb(0x30, 0xD1, 0x58) : Color.FromRgb(0x34, 0xC7, 0x59))
            : (_darkTheme ? Color.FromRgb(0xFF, 0x9F, 0x0A) : Color.FromRgb(0xFF, 0x95, 0x00)));
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (state.Camera.Count > 0) row.Children.Add(Glyphs.Fill(Glyphs.Camera, 12, Brushes.White));
        if (state.Microphone.Count > 0)
        {
            var mic = Glyphs.Stroke(Glyphs.Microphone, 12, Brushes.White, 1.5);
            if (row.Children.Count > 0) mic.Margin = new Thickness(5, 0, 0, 0);
            row.Children.Add(mic);
        }
        // One picture: 7 DIP each side (26 wide); two: 5 (40 wide).
        _privacyPill.Padding = row.Children.Count > 1 ? new Thickness(5, 0, 6, 0) : new Thickness(7, 0, 7, 0);
        _privacyPill.Child = row;
        PrivacyShown = (kind, row.Children.Count);
        Describe("privacy", tooltip);
    }

    /// <summary>Now Playing's item shows while a session plays or is paused (the system media controls'), named in its tooltip.</summary>
    public void SetNowPlaying(bool active, string tooltip)
    {
        _available["nowPlaying"] = active;
        ShowItem("nowPlaying");
        Describe("nowPlaying", tooltip);
    }
    List<string> _shownItems = [.. MenuBarItems.Movable];

    /// <summary>The movable items shown, left to right.</summary>
    public IReadOnlyList<string> ShownItems => _shownItems;
    /// <summary>The status items by key (diagnostics: where each one is).</summary>
    public IReadOnlyDictionary<string, Border> StatusItems => _status;
    /// <summary>The movable items were put in another order by a drag (Ctrl held at the press): the shown ones, left to right.</summary>
    public event Action<IReadOnlyList<string>>? ItemsReordered;

    /// <summary>Puts the movable items in <paramref name="shown"/>'s order and out of sight the others.</summary>
    public void ArrangeItems(IReadOnlyList<string> shown)
    {
        _shownItems = MenuBarItems.Clean(shown);
        _right.Children.Clear();
        _right.Children.Add(_more);
        _right.Children.Add(_tray);
        foreach (var key in _shownItems) _right.Children.Add(_status[key]);
        foreach (var key in MenuBarItems.Movable.Except(_shownItems)) _right.Children.Add(_status[key]); // kept, out of sight
        _right.Children.Add(_status["privacy"]);
        _right.Children.Add(_status["control-center"]);
        _right.Children.Add(_status["clock"]);
        foreach (var key in MenuBarItems.Movable) ShowItem(key);
        RequestFit();
    }

    void ShowItem(string key) =>
        _status[key].Visibility = _shownItems.Contains(key) && _available[key] ? Visibility.Visible : Visibility.Collapsed;

    // A drag: the item follows the pointer and the ones it passes step aside (transforms only: taking it out of the row
    // while it holds the mouse would lose the capture); the new order is put in place when it is let go.
    Border? _dragItem;
    bool _animations = true;
    string _dragKey = "";
    double _dragStartX;
    List<(string Key, Border Item, double Left, double Width)> _dragRow = [];
    int _dragFrom, _dragTo;

    void Movable(string key, Border b)
    {
        b.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // Ctrl at the press (macOS's ⌘-drag; asked of the keyboard itself: the bar never has the focus).
            if (!Native.KeyDown(0x11 /*VK_CONTROL*/)) return;
            e.Handled = true; // not the item's own panel
            _dragRow = _shownItems.Where(k => _status[k].Visibility == Visibility.Visible)
                .Select(k => { var it = _status[k]; var r = it.TransformToAncestor(_right).TransformBounds(new Rect(it.RenderSize)); return (k, it, r.Left, r.Width); })
                .ToList();
            _dragFrom = _dragTo = _dragRow.FindIndex(x => x.Key == key);
            if (_dragFrom < 0) return;
            _dragItem = b;
            _dragKey = key;
            _dragStartX = e.GetPosition(_right).X;
            b.Opacity = 0.6;
            b.CaptureMouse();
        };
        b.MouseMove += (_, e) =>
        {
            if (_dragItem != b) return;
            double dx = e.GetPosition(_right).X - _dragStartX;
            var me = _dragRow[_dragFrom];
            var follow = Shift(b);
            Tween.Stop(follow, TranslateTransform.XProperty);
            follow.BeginAnimation(TranslateTransform.XProperty, null);
            follow.X = dx;
            // Its place: as many of the others as have their middle left of its middle.
            double centre = me.Left + me.Width / 2 + dx;
            _dragTo = 0;
            for (int i = 0; i < _dragRow.Count; i++) if (i != _dragFrom && centre > _dragRow[i].Left + _dragRow[i].Width / 2) _dragTo++;
            for (int i = 0; i < _dragRow.Count; i++)
            {
                if (i == _dragFrom) continue;
                double shift = _dragFrom < i && i <= _dragTo ? -me.Width : _dragTo <= i && i < _dragFrom ? me.Width : 0;
                StepAside(_dragRow[i].Item, shift);
            }
        };
        b.PreviewMouseLeftButtonUp += (_, e) => { if (_dragItem != b) return; e.Handled = true; b.ReleaseMouseCapture(); };
        b.LostMouseCapture += (_, _) => { if (_dragItem == b) EndDrag(); };
    }

    // The items passed step aside by sliding there (0.15 s), and once let go every item slides from where it shows to its
    // new place (macOS). They jumped aside, and the dragged one jumped from the pointer into its place (2026-10-17).
    const double StepAsideMs = Motion.BarStepAside;
    readonly Dictionary<Border, double> _aside = new();

    /// <summary>The item's own shift along the bar (made the first time: its render transform is otherwise the shared frozen identity).</summary>
    static TranslateTransform Shift(Border item)
    {
        if (item.RenderTransform is TranslateTransform { IsFrozen: false } t) return t;
        var made = new TranslateTransform();
        item.RenderTransform = made;
        return made;
    }

    void StepAside(Border item, double to)
    {
        if (_aside.TryGetValue(item, out var was) && was == to) return;
        _aside[item] = to;
        Slide(Shift(item), null, to);
    }

    /// <summary>Slides a shift to <paramref name="to"/> from <paramref name="from"/> (null: from where it shows now); at once without animations.</summary>
    void Slide(TranslateTransform t, double? from, double to)
    {
        if (!_animations) { Tween.Stop(t, TranslateTransform.XProperty); t.BeginAnimation(TranslateTransform.XProperty, null); t.X = to; return; }
        if (from is double f) { Tween.Stop(t, TranslateTransform.XProperty); t.BeginAnimation(TranslateTransform.XProperty, null); t.X = f; }
        Tween.To(t, TranslateTransform.XProperty, to, StepAsideMs);
    }

    void EndDrag()
    {
        var b = _dragItem!;
        _dragItem = null;
        b.Opacity = 1;
        _aside.Clear();
        // Where each shows now (its place and its shift), to slide from once the new order is laid out.
        var shown = _dragRow.Select(x => (x.Item, at: x.Item.TransformToAncestor(_right).TransformBounds(new Rect(x.Item.RenderSize)).Left)).ToList();
        foreach (var x in _dragRow) { var t = Shift(x.Item); Tween.Stop(t, TranslateTransform.XProperty); t.BeginAnimation(TranslateTransform.XProperty, null); t.X = 0; }
        if (_dragTo != _dragFrom)
        {
            // Next to the item it was let go beside, among all the shown ones (some may be out of sight: Focus while off).
            string beside = _dragRow[_dragTo].Key;
            var rest = _shownItems.Where(k => k != _dragKey).ToList();
            int at = rest.IndexOf(beside) + (_dragTo > _dragFrom ? 1 : 0);
            var order = MenuBarItems.Move(_shownItems, _dragKey, at);
            ArrangeItems(order);
            ItemsReordered?.Invoke(order);
        }
        if (!_animations) return;
        _right.UpdateLayout();
        foreach (var (item, was) in shown)
        {
            if (!item.IsVisible) continue;
            double now = item.TransformToAncestor(_right).TransformBounds(new Rect(item.RenderSize)).Left;
            if (Math.Abs(was - now) >= 0.5) Slide(Shift(item), was - now, 0);
        }
    }

    Rect ScreenRect(FrameworkElement el)
    {
        if (!el.IsVisible) return Rect.Empty;
        var p = el.PointToScreen(new Point(0, 0));
        var s = VisualTreeHelper.GetDpi(this);
        return new Rect(p.X / s.DpiScaleX, p.Y / s.DpiScaleY, el.ActualWidth, el.ActualHeight);
    }

    /// <summary>Tooltip and accessible name (UI Automation) of a status item.</summary>
    public void Describe(string key, string? text)
    {
        if (!_status.TryGetValue(key, out var b)) return;
        b.ToolTip = string.IsNullOrWhiteSpace(text) ? null : text;
        AutomationProperties.SetName(b, text ?? key);
    }

    /// <summary>Accessible name of the system menu's mark.</summary>
    public void DescribeLogo(string name) => AutomationProperties.SetName(_logo, name);

    /// <summary>Screen rectangle (DIP) of a status item ("clock", "volume"…), empty when hidden.</summary>
    public Rect StatusRect(string key) => _status.TryGetValue(key, out var b) ? ScreenRect(b) : Rect.Empty;

    /// <summary>The front app's name (bold) and its menu titles.</summary>
    public void SetTitles(IReadOnlyList<string> titles)
    {
        Stats.Inc("bar.setTitles");
        foreach (var t in _titles) _left.Children.Remove(t);
        _titles.Clear();
        for (int i = 0; i < titles.Count; i++)
        {
            int index = i;
            var tb = new TextBlock { Text = titles[i], VerticalAlignment = VerticalAlignment.Center, Foreground = _text, FontWeight = i == 0 ? FontWeights.SemiBold : FontWeights.Normal };
            var b = Item(tb, new Thickness(8, 0, 8, 0));
            AutomationProperties.SetName(b, titles[i]);
            b.MouseLeftButtonDown += (_, e) => { e.Handled = true; LastInputTime = e.Timestamp; PressItem(b, e); TitlePressed?.Invoke(index); };
            b.MouseEnter += (_, e) => { LastInputTime = e.Timestamp; TitleEntered?.Invoke(index); };
            _titles.Add(b);
            _left.Children.Add(b);
        }
        // Their natural widths, for the fit (a title the fit hides measures nothing).
        _titleWidths.Clear();
        foreach (var b in _titles) { b.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); _titleWidths.Add(b.DesiredSize.Width); }
        SetOpen(_open);
        RequestFit();
    }

    readonly List<double> _titleWidths = new();

    /// <summary>Highlights the title whose menu is open (null: none).</summary>
    public void SetOpen(int? index)
    {
        _open = index;
        var plain = GlassBar ? Brushes.Transparent : _hover;
        _logo.Background = index == -1 ? plain : Brushes.Transparent;
        for (int i = 0; i < _titles.Count; i++) _titles[i].Background = index == i ? plain : Brushes.Transparent;
        ShowPlate();
    }

    // ---- the plate behind the item pressed or open, with Liquid Glass (BarPlates; 2026-10-23) --------------------------

    BarPlates? _plates;
    Border? _trayHeld;

    /// <summary>The bar's items take their plate from composition (Liquid Glass on): it comes in under a press, with its light.</summary>
    bool GlassBar => _backdrop != null && GlassKnob.Enabled();

    /// <summary>The plate behind the item pressed or open now (diagnostics "bar-press"): its name, shown, presses.</summary>
    public (string? item, bool shown, int presses) Plate =>
        (_plates?.Item is Border b ? AutomationProperties.GetName(b) is { Length: > 0 } n ? n : b == _logo ? "logo" : _status.FirstOrDefault(kv => kv.Value == b).Key ?? "tray" : null,
         _plates?.Shown ?? false, _plates?.Presses ?? 0);

    /// <summary>A press on <paramref name="item"/>: its plate comes in with the press's light, and the light spills onto the glass near by.</summary>
    void PressItem(Border item, MouseButtonEventArgs e)
    {
        if (!GlassBar) return;
        ShowPlate(item, e.GetPosition(item));
        if (_animations) PressLight.Spill(this, e.GetPosition(this), lit: true);
    }

    /// <summary>The plate behind the item open (a title's menu, a status item's panel) or a tray icon held; none: it goes.</summary>
    void ShowPlate(Border? pressed = null, Point? at = null)
    {
        if (!GlassBar) { _plates?.Hide(false); return; }
        Border? open = _open == -1 ? _logo : _open is int i && i >= 0 && i < _titles.Count ? _titles[i]
            : (_statusOpen ?? _statusLit) is { } plated && _status.TryGetValue(plated, out var st) ? st : _trayHeld;
        var item = pressed ?? open;
        if (item == null || !item.IsVisible || PresentationSource.FromVisual(this) == null) { _plates?.Hide(_animations); return; }
        _plates ??= new BarPlates(_backdrop!.Overlay);
        float s = (float)VisualTreeHelper.GetDpi(this).DpiScaleX;
        var o = item.TranslatePoint(new Point(0, 0), this);
        var px = ((float)Math.Round(o.X * s), (float)Math.Round(o.Y * s), (float)Math.Round(item.ActualWidth * s), (float)Math.Round(item.ActualHeight * s));
        var c = ((SolidColorBrush)_hover).Color;
        System.Numerics.Vector2? press = at is Point p ? new System.Numerics.Vector2(px.Item1 + (float)(p.X * s), px.Item2 + (float)(p.Y * s)) : null;
        _plates.Show(item, px, (float)(item.CornerRadius.TopLeft * s), Composition.Color(c.A, c.R, c.G, c.B), press, _animations, s);
    }

    string? _statusOpen;

    /// <summary>
    /// The status item whose panel is open ("clock", "control-center"…; null: none) keeps a plate behind it meanwhile,
    /// as macOS's do (the app menus' titles had one, the status items none: 2026-10-14 review).
    /// </summary>
    public void SetStatusOpen(string? key)
    {
        _statusOpen = key;
        ShowStatusPlate();
    }

    string? _statusLit;

    /// <summary>
    /// The status item the volume display stands under ("volume"; null: none) has a plate behind it while it shows
    /// (macOS 27.2's pictures, 2026-11-01). An item with its panel open goes before it.
    /// </summary>
    public void SetStatusLit(string? key)
    {
        _statusLit = key;
        ShowStatusPlate();
    }

    void ShowStatusPlate()
    {
        var key = _statusOpen ?? _statusLit;
        var plain = GlassBar ? Brushes.Transparent : _hover;
        foreach (var (k, b) in _status) b.Background = k == key ? plain : Brushes.Transparent;
        ShowPlate();
    }

    /// <summary>The status item shown open (diagnostics).</summary>
    public string? StatusOpen => _statusOpen;

    /// <summary>Screen rectangle (DIP) of a title, to place its menu under it.</summary>
    public Rect TitleRect(int index)
    {
        var el = index < 0 || _titles.Count == 0 ? _logo : _titles[Math.Min(index, _titles.Count - 1)];
        return ScreenRect(el);
    }

    /// <summary>
    /// The bar's bottom (DIPs) where it stands: while it comes down from the top edge, where it is coming to (a menu or a
    /// panel opened as it slides stood where the bar was passing, over it).
    /// </summary>
    public double BottomDip => (Fades.ComingTo(this)?.Y ?? Top) + ActualHeight;
    /// <summary>The bar's right end (DIPs): the right edge of its monitor, where menus and panels stop.</summary>
    public double RightDip => Left + ActualWidth;

    public void SetIme(string text, string tooltip)
    {
        Stats.Inc("bar.setIme");
        _ime.Text = text;
        Describe("ime", tooltip);
        _available["ime"] = !string.IsNullOrEmpty(text);
        ShowItem("ime");
    }

    public void SetWifi(NetKind kind, int bars, string tooltip)
    {
        Stats.Inc("bar.setWifi");
        _wifi.Children.Clear();
        if (kind == NetKind.Ethernet) _wifi.Children.Add(Glyphs.Stroke(Glyphs.Ethernet, 16, _text, 1.4));
        else
        {
            int level = kind == NetKind.Wifi ? Math.Clamp((bars + 1) / 2 + 1, 1, 4) : 0;
            for (int i = 0; i < Glyphs.WifiArcs.Length; i++)
            {
                var dim = new SolidColorBrush(((SolidColorBrush)_text).Color) { Opacity = i < level ? 1 : 0.3 };
                _wifi.Children.Add(i == 0 ? Glyphs.Fill(Glyphs.WifiArcs[0], 16, dim) : Glyphs.Stroke(Glyphs.WifiArcs[i], 16, dim, 1.7));
            }
        }
        Describe("wifi", tooltip);
    }

    /// <summary>The volume item as drawn (waves, 0 muted, -1 headphones, and its tooltip); null: to be drawn (the theme changed).</summary>
    (int waves, string tooltip)? _volumeShown;

    /// <param name="headphones">The output is headphones or a headset: headphones instead of the speaker (macOS).</param>
    public void SetVolume(float level, bool muted, bool headphones, string tooltip)
    {
        int waves = muted || level <= 0.001f ? 0 : headphones ? -1 : level < 0.34f ? 1 : level < 0.67f ? 2 : 3;
        // The audio service also reports the microphone's changes (a call's automatic gain moves it often): the same
        // picture is not made again.
        if (_volumeShown == (waves, tooltip)) return;
        _volumeShown = (waves, tooltip);
        Stats.Inc("bar.setVolume");
        _volume.Children.Clear();
        if (waves < 0) _volume.Children.Add(Glyphs.Stroke(Glyphs.Headphones, 16, _text, 1.5));
        else
        {
            _volume.Children.Add(Glyphs.Fill(Glyphs.Speaker, 16, _text));
            if (waves == 0) _volume.Children.Add(Glyphs.Stroke(Glyphs.Mute, 16, _text, 1.5));
            else for (int i = 0; i < waves; i++) _volume.Children.Add(Glyphs.Stroke(Glyphs.SpeakerWaves[i], 16, _text, 1.5));
        }
        Describe("volume", tooltip);
    }

    /// <summary>The volume item's picture now (diagnostics): "speaker", "muted" or "headphones".</summary>
    public string VolumePicture => _volumeShown is { } v ? v.waves < 0 ? "headphones" : v.waves == 0 ? "muted" : "speaker" : "";

    /// <summary>The moon shows while a Focus is on (do not disturb or a focus session), as macOS's Focus does.</summary>
    public void SetFocus(bool on, string tooltip)
    {
        _available["focus"] = on;
        ShowItem("focus");
        Describe("focus", tooltip);
    }

    /// <summary>The moon is shown (diagnostics).</summary>
    public bool FocusShown => _focusItem.Visibility == Visibility.Visible;

    /// <summary>The clock was set again (every minute, or second with seconds shown): for the slow checks that ride on it.</summary>
    public event Action? ClockTicked;

    /// <summary>The battery's percentage shows beside its picture (the settings' "Show Percentage", macOS's; on by default).</summary>
    public bool BatteryPercent { get; set; } = true;

    /// <summary>The battery's percentage is on the bar now (diagnostics).</summary>
    public bool BatteryPercentShown => _batteryText.Visibility == Visibility.Visible;

    public void SetBattery(bool present, int percent, bool charging, string tooltip)
    {
        Stats.Inc("bar.setBattery");
        _available["battery"] = present;
        ShowItem("battery");
        if (!present) return;
        _batteryText.Text = $"{percent}%";
        _batteryText.Visibility = BatteryPercent ? Visibility.Visible : Visibility.Collapsed;
        _battery.Children.Clear();
        var body = new Path { Data = Glyphs.BatteryBody, Stroke = _text, StrokeThickness = 1, Opacity = 0.9 };
        _battery.Children.Add(body);
        _battery.Children.Add(new Path { Data = Glyphs.BatteryTip, Fill = _text, Opacity = 0.6 });
        var fillColor = percent <= 10 && !charging ? Brushes.OrangeRed : _text;
        var level = new Rectangle { Width = Math.Max(1, 17.0 * percent / 100), Height = 7, Fill = fillColor, RadiusX = 1, RadiusY = 1 };
        Canvas.SetLeft(level, 2.5); Canvas.SetTop(level, 2.5);
        _battery.Children.Add(level);
        if (charging) _battery.Children.Add(new Path { Data = Glyphs.Bolt, Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 0.6 });
        Describe("battery", tooltip);
    }

    /// <summary>
    /// Other apps' tray icons, left of the system items (macOS "menu extras"). Those the user wants on the bar get an
    /// item; which of them show, and whether the » button does, is the fit's (<see cref="Layout"/>).
    /// </summary>
    public void SetTray(IReadOnlyList<TrayIconView> icons)
    {
        Stats.Inc("bar.setTray");
        _tray.Children.Clear();
        _trayItems.Clear();
        _trayViews = icons;
        foreach (var icon in icons.Where(i => i.Place == TrayPlace.Bar))
        {
            var img = new Image { Source = icon.Icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var b = Item(img, new Thickness(5, 0, 5, 0));
            b.ToolTip = string.IsNullOrWhiteSpace(icon.Tooltip) ? null : icon.Tooltip;
            AutomationProperties.SetName(b, icon.Tooltip);
            string key = icon.Key;
            b.MouseDown += (_, e) =>
            {
                e.Handled = true;
                if (GlassBar) { _trayHeld = b; PressItem(b, e); } else b.Background = _hover;
                TrayIconMouse?.Invoke(key, e.ChangedButton, true);
            };
            b.MouseUp += (_, e) => { e.Handled = true; LetTray(b); TrayIconMouse?.Invoke(key, e.ChangedButton, false); };
            b.MouseLeave += (_, _) => LetTray(b);
            _tray.Children.Add(b);
            _trayItems.TryAdd(key, b);
        }
        _layout = null; // a new set: the fit tells where they went even when the result looks the same
        RequestFit();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => TrayMoved?.Invoke());
    }

    IReadOnlyList<TrayIconView> _trayViews = [];
    readonly Dictionary<string, Border> _trayItems = new();

    /// <summary>A tray icon let go (or left while held): its plate goes.</summary>
    void LetTray(Border b)
    {
        b.Background = Brushes.Transparent;
        if (!ReferenceEquals(_trayHeld, b)) return;
        _trayHeld = null;
        ShowPlate();
    }

    /// <summary>A tray icon's width on the bar and the » button's (16 + padding 5 + 5 + margins 1 + 1).</summary>
    const double TrayIconWidth = 28, MoreWidth = 28;

    MenuBarLayout? _layout;
    bool _fitPending;

    /// <summary>What fits now: the icons on the bar and in the » panel, the button, the titles shown (null before the first fit).</summary>
    public MenuBarLayout? Layout => _layout;

    /// <summary>The icons the » panel shows, in the bar's order.</summary>
    public IReadOnlyList<TrayIconView> OverflowIcons =>
        _layout == null ? [] : [.. _layout.Overflow.Select(k => _trayViews.FirstOrDefault(v => v.Key == k)).OfType<TrayIconView>()];

    void RequestFit()
    {
        if (_fitPending) return;
        _fitPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Refit);
    }

    /// <summary>
    /// Shares the bar out (Core's MenuBarFit) from what each part takes at its natural width (not how it shows now),
    /// so a fit's own changes do not change the next fit's answer.
    /// </summary>
    void Refit()
    {
        _fitPending = false;
        if (ActualWidth <= 0) return;
        double status = 0;
        foreach (UIElement c in _right.Children)
            if (c != _tray && c != _more) status += Natural(c);
        var icons = _trayViews.Select(v => new TrayFitIcon(v.Key, v.Place, TrayIconWidth)).ToList();
        var layout = MenuBarFit.Fit(ActualWidth, Natural(_logo), _titleWidths, status, icons, MoreWidth);
        bool changed = _layout is not { } was || !was.Bar.SequenceEqual(layout.Bar) || !was.Overflow.SequenceEqual(layout.Overflow)
                       || was.OverflowButton != layout.OverflowButton || was.Titles != layout.Titles;
        _layout = layout;
        // Always set (the titles and icons may be new ones, all shown; the same value again changes nothing).
        var shown = layout.Bar.ToHashSet();
        foreach (var (key, b) in _trayItems) b.Visibility = shown.Contains(key) ? Visibility.Visible : Visibility.Collapsed;
        _more.Visibility = layout.OverflowButton ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < _titles.Count; i++) _titles[i].Visibility = i < layout.Titles ? Visibility.Visible : Visibility.Collapsed;
        if (!changed) return;
        Stats.Inc("bar.fit");
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => TrayMoved?.Invoke());
    }

    static double Natural(UIElement el)
    {
        if (el.Visibility == Visibility.Collapsed) return 0;
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return el.DesiredSize.Width;
    }

    /// <summary>
    /// Screen rectangles (pixels) of the tray icons, by key: an icon in the » panel is where the » button is (an app
    /// that places its pop-up at its icon puts it there; the open panel tells the cells instead).
    /// </summary>
    public Dictionary<string, RECT> TrayRects()
    {
        var map = new Dictionary<string, RECT>();
        if (!IsVisible) return map;
        foreach (var (key, b) in _trayItems)
            if (Pixels(ScreenRect(b)) is RECT r) map[key] = r;
        if (_layout != null && Pixels(ScreenRect(_more)) is RECT more)
            foreach (var key in _layout.Overflow) map.TryAdd(key, more);
        return map;
    }

    RECT? Pixels(Rect r)
    {
        if (r.IsEmpty) return null;
        var s = VisualTreeHelper.GetDpi(this);
        return new RECT((int)Math.Round(r.Left * s.DpiScaleX), (int)Math.Round(r.Top * s.DpiScaleY),
            (int)Math.Round(r.Right * s.DpiScaleX), (int)Math.Round(r.Bottom * s.DpiScaleY));
    }

    /// <summary>The bar's material as last applied: clear, blurred or solid (diagnostics).</summary>
    public MaterialKind MaterialKind { get; private set; }

    /// <summary>First call before the window is shown: it creates the backdrop window that owns this one.</summary>
    public void ApplyTheme(ThemeSnapshot t, Material m)
    {
        MaterialKind = m.Kind;
        if (_backdrop == null) _backdrop = new WindowBackdrop(this, m) { TakesSpill = false }; // the bar: its plate comes from BarPlates
        else _backdrop.SetMaterial(m);
        _text = Materials.Text(t);
        _animations = t.Animations;
        _darkTheme = t.Dark; // the privacy capsule's shades (set again by the bar's controller)
        _hover = new SolidColorBrush(t.Dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0, 0, 0));
        Foreground = _text;
        _volumeShown = null; // drawn again in the new colour
        foreach (var p in _themed) { if (p.Fill != null) p.Fill = _text; if (p.Stroke != null) p.Stroke = _text; }
        foreach (var t2 in _titles) ((TextBlock)t2.Child).Foreground = _text;
        _plates?.Hide(false); // in its new colour from the next showing
        SetOpen(_open);
        ShowStatusPlate();
    }

    void UpdateClock()
    {
        var now = DateTime.Now;
        string text = ClockFormatter.Format(now, Lang, CultureInfo.CurrentCulture, _clockSettings);
        if (!_clockSettings.FlashSeparators)
        {
            _clock.Text = text; // the colons as they come (and back, once flashing is turned off)
            ColonsShown = true;
        }
        else
        {
            // The colons flash (macOS's "Flash the time separators"): hidden ones keep their room, the time stays put.
            ColonsShown = ClockFormatter.SeparatorsShown(now, _clockSettings);
            _clock.Inlines.Clear();
            var parts = text.Split(':');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    var colon = new System.Windows.Documents.Run(":");
                    if (!ColonsShown) colon.Foreground = Brushes.Transparent; // shown, it takes the clock's colour (a theme's too)
                    _clock.Inlines.Add(colon);
                }
                if (parts[i].Length > 0) _clock.Inlines.Add(new System.Windows.Documents.Run(parts[i]));
            }
        }
        _tick.Interval = ClockFormatter.UntilNextTick(now, ClockFormatter.EverySecond(_clockSettings));
        _tick.Start();
        ClockTicked?.Invoke();
    }

    /// <summary>The time's colons show now (diagnostics: they flash with the setting).</summary>
    public bool ColonsShown { get; private set; } = true;

    /// <summary>Display language for the clock (set from the Strings language).</summary>
    public CultureInfo Lang { get; set; } = CultureInfo.CurrentUICulture;
    public void RefreshClock() => UpdateClock();

    protected override void OnClosed(EventArgs e)
    {
        _tick.Stop();
        base.OnClosed(e);
        _backdrop?.Dispose();
    }
}
