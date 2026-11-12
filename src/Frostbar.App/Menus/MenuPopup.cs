using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using Frostbar.App.Common;
using Frostbar.Core.Menus;
using Frostbar.Platform.Dwm;
using Motion = Frostbar.Core.Shell.MotionTable;
using Frostbar.Platform.Icons;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Sys;
using Menu = Frostbar.Core.Menus.Menu;
using MenuItem = Frostbar.Core.Menus.MenuItem;

namespace Frostbar.App.Menus;

/// <summary>
/// One open menu: a layered, never-activating window on its blurred backdrop (ADR-003), rows of
/// [check | icon and label | shortcut | submenu arrow]. The controller drives highlight and keyboard; the popup reports hover/click.
/// </summary>
public sealed class MenuPopup : Window
{
    public const double Pad = 5, RowHeight = 22, SeparatorHeight = 9, MinWidthDip = 220, RadiusDip = GlassShape.Menu, IconDip = 16;
    readonly List<(MenuItem item, Border row, TextBlock label, TextBlock? keys, Path? arrow)> _rows = new();
    readonly Brush _text, _dim, _accent;
    readonly WindowBackdrop _backdrop;
    readonly bool _animate;
    int _highlight = -1;
    readonly IconService? _icons;
    /// <summary>The rows' icons (the system menu's Recent Items): filled in as they arrive while the menu is open.</summary>
    readonly List<(string key, string source, Image picture)> _pictures = new();

    /// <summary>The key a menu's icon is kept under in the icon service (the menu bar fetches them ahead).</summary>
    public static string IconKey(string source) => "menu:" + source;

    public Menu Menu { get; }
    public IntPtr Handle => new WindowInteropHelper(this).Handle;
    public IntPtr BackdropHandle => _backdrop.BackdropHandle;
    public int Highlight => _highlight;
    public IReadOnlyList<MenuItem> Items => _rows.Select(r => r.item).ToList();
    public event Action<int>? Hovered;
    public event Action<int>? Clicked;

    /// <summary>The row whose submenu is open (-1: none): it stays lit while the pointer is off it, in the submenu (macOS).</summary>
    public int OpenChild { get; set; } = -1;

    /// <param name="icons">Where the items' icons come from (<see cref="MenuItem.Icon"/>); without it the items show none.</param>
    public MenuPopup(Menu menu, ThemeSnapshot theme, Material material, Func<Shortcut, string> formatKeys, IconService? icons = null)
    {
        Menu = menu;
        _icons = icons;
        Title = "Frostbar.Menu";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = Materials.UiFont;
        FontSize = 13;
        UseLayoutRounding = true;
        Language = XmlLanguage.GetLanguage("ja-JP");

        _animate = theme.Animations;
        _text = Materials.Text(theme);
        var tc = ((SolidColorBrush)_text).Color;
        _dim = new SolidColorBrush(Color.FromArgb((byte)(tc.A * 0.45), tc.R, tc.G, tc.B));
        _accent = new SolidColorBrush(Color.FromRgb(theme.Accent.R, theme.Accent.G, theme.Accent.B));

        var panel = new StackPanel { Margin = new Thickness(Pad), MinWidth = MinWidthDip };
        foreach (var item in menu.Items)
        {
            if (item.IsSeparator)
            {
                panel.Children.Add(new Border { Height = SeparatorHeight, Child = new Rectangle { Height = 1, Margin = new Thickness(8, 0, 8, 0), Fill = _dim, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center } });
                _rows.Add((item, new Border(), new TextBlock(), null, null));
                continue;
            }
            var grid = new Grid { Height = RowHeight };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var check = new TextBlock { Text = item.Checked ? "✓" : "", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var label = new TextBlock { Text = item.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420, Margin = new Thickness(0, 0, 24, 0) };
            TextBlock? keys = item.Shortcut is { } sc ? new TextBlock { Text = formatKeys(sc), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) } : null;
            // A submenu's row ends in a chevron (macOS's ›) where keys would stand.
            Path? arrow = item.Children != null ? Glyphs.Stroke(Glyphs.Chevron, 11, item.Enabled ? _text : _dim, 1.6) : null;
            // An app's or a document's icon before its name (the system menu's Recent Items, as macOS shows them).
            FrameworkElement named = label;
            if (item.Icon is { } source && icons != null)
            {
                string key = IconKey(source);
                var picture = new Image { Width = IconDip, Height = IconDip, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Source = icons.Get(key, source) };
                RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
                _pictures.Add((key, source, picture));
                var box = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                box.Children.Add(picture);
                box.Children.Add(label);
                named = box;
            }
            Grid.SetColumn(named, 1);
            grid.Children.Add(check);
            grid.Children.Add(named);
            if (keys != null) { Grid.SetColumn(keys, 2); grid.Children.Add(keys); }
            if (arrow != null)
            {
                arrow.Margin = new Thickness(0, 0, 4, 0);
                arrow.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(arrow, 2);
                grid.Children.Add(arrow);
            }
            var row = new Border { Child = grid, CornerRadius = new CornerRadius(GlassShape.MenuRow), Background = Brushes.Transparent, ToolTip = item.Hint };
            System.Windows.Automation.AutomationProperties.SetName(row, item.Label);
            if (!item.Enabled) PressLight.SetOff(row, true); // pressing it does nothing
            int index = _rows.Count;
            row.MouseEnter += (_, _) => Hovered?.Invoke(index);
            // Off the row (out of the menu, onto a separator) its highlight goes, as on macOS (it stayed lit, 2026-10-14
            // review). One the keyboard moved elsewhere is not this row's; the chosen row's blink is left alone; the row
            // of an open submenu stays lit (the pointer has gone into the submenu).
            row.MouseLeave += (_, _) => { if (IsHitTestVisible && _highlight == index && OpenChild != index) SetHighlight(-1); };
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; Clicked?.Invoke(index); };
            _rows.Add((item, row, label, keys, arrow));
            check.Foreground = label.Foreground = item.Enabled ? _text : _dim;
            if (keys != null) keys.Foreground = _dim;
            panel.Children.Add(row);
        }
        _list = panel;
        Content = panel; // in a scrolling viewer only when it is taller than its room (FitHeight)
        if (_pictures.Count > 0 && _icons != null) _icons.Loaded += OnIconLoaded;

        _backdrop = new WindowBackdrop(this, material);
        // The glass suits what is behind it (item 4, the user's "A", 2026-10-19): light over a white window, its text dark.
        _appearanceDark = _toneDark = theme.Dark;
        _backdrop.ToneChanged += (_, dark) =>
        {
            _toneDark = dark;
            Inks.Apply(panel, dark, _appearanceDark);
            for (int i = 0; i < _rows.Count; i++)
                if (!_rows[i].item.IsSeparator) Paint(i, i == _highlight);
        };
        _backdrop.Adapts = true;
        PressLight.Attach(this, () => _animate && _backdrop.Glassy);
        SourceInitialized += (_, _) =>
        {
            Native.AddExStyle(Handle, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(Handle).AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if ((uint)msg == Native.WM_MOUSEACTIVATE) { handled = true; return new IntPtr(Native.MA_NOACTIVATE); }
                return IntPtr.Zero;
            });
        };
    }

    /// <summary>Screen rectangle of row <paramref name="i"/> in pixels (diagnostics: where to click).</summary>
    public RECT RowPx(int i)
    {
        var row = _rows[i].row;
        if (!row.IsVisible) return default;
        var p = row.PointToScreen(new Point(0, 0)); // device pixels
        var s = VisualTreeHelper.GetDpi(this);
        return new RECT((int)Math.Round(p.X), (int)Math.Round(p.Y), (int)Math.Round(p.X + row.ActualWidth * s.DpiScaleX), (int)Math.Round(p.Y + row.ActualHeight * s.DpiScaleY));
    }

    public bool Selectable(int i) => i >= 0 && i < _rows.Count && !_rows[i].item.IsSeparator && _rows[i].item.Enabled;

    /// <summary>Row <paramref name="i"/> opens a submenu (it runs nothing itself).</summary>
    public bool HasChildren(int i) => i >= 0 && i < _rows.Count && _rows[i].item.Children is { Count: > 0 };

    /// <summary>Row <paramref name="i"/>'s rectangle on the screen, in DIPs (where its submenu stands).</summary>
    public Rect RowDip(int i)
    {
        var row = _rows[i].row;
        var at = row.TranslatePoint(new Point(0, 0), this);
        return new Rect(Left + at.X, Top + at.Y, row.ActualWidth, row.ActualHeight);
    }

    public void SetHighlight(int i)
    {
        if (_highlight == i) return;
        if (_highlight >= 0 && _highlight < _rows.Count) Paint(_highlight, false);
        _highlight = Selectable(i) ? i : -1;
        if (_highlight >= 0) Paint(_highlight, true);
        // Lit by the keys in a menu that scrolls: in sight (one lit by the pointer is in sight already).
        if (_highlight >= 0 && _scroll is { ScrollableHeight: > 0 } && !IsMouseOver) _rows[_highlight].row.BringIntoView();
    }

    readonly StackPanel _list;
    ScrollViewer? _scroll;

    /// <summary>
    /// Taller than <paramref name="room"/> (DIPs; a few rows at least), it scrolls within it: its rows ran off the
    /// screen's bottom (Recent Items at 20 and more, a long Window menu), the lower ones and "Clear Menu" out of reach.
    /// A menu that fits is left as it was (no viewer to make, so its opening costs what it did).
    /// </summary>
    void FitHeight(double room)
    {
        if (double.IsInfinity(room)) return;
        room = Math.Max(RowHeight * 4, room);
        if (_scroll != null) { _scroll.MaxHeight = room; return; }
        _list.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (_list.DesiredSize.Height <= room) return;
        Content = null;
        _scroll = new ScrollViewer { Content = _list, MaxHeight = room, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        MacScrollBar.Apply(_scroll, () => _animate);
        SmoothScroll.Attach(_scroll, () => _animate);
        Content = _scroll;
    }

    readonly bool _appearanceDark;
    bool _toneDark;

    /// <summary>The menu's glass is dark now (item 4: it turns light over a white window; diagnostics).</summary>
    public bool ToneDark => _toneDark;

    /// <summary>Row <paramref name="i"/>'s text colour now (diagnostics).</summary>
    public string? LabelColor(int i) => i >= 0 && i < _rows.Count && _rows[i].label.Foreground is SolidColorBrush s ? s.Color.ToString() : null;

    void Paint(int i, bool on)
    {
        var (item, row, label, keys, arrow) = _rows[i];
        var tone = Inks.For(_toneDark, _appearanceDark);
        Brush Ink(Brush b) => tone?.Ink(b) ?? b;
        row.Background = on ? _accent : Brushes.Transparent;
        label.Foreground = on ? Brushes.White : Ink(item.Enabled ? _text : _dim);
        if (keys != null) keys.Foreground = on ? Brushes.White : Ink(_dim);
        if (arrow != null) arrow.Stroke = label.Foreground;
        if (((Grid)row.Child).Children[0] is TextBlock check) check.Foreground = label.Foreground;
    }

    /// <summary>Next selectable row from <paramref name="from"/> in <paramref name="step"/> direction (wraps).</summary>
    public int Next(int from, int step)
    {
        int n = _rows.Count;
        // Nothing lit and going up: from the last row (it skipped the row before the last, a disabled last row's).
        if (from < 0 && step < 0) from = n;
        for (int k = 1; k <= n; k++)
        {
            int i = ((from + step * k) % n + n) % n;
            if (Selectable(i)) return i;
        }
        return -1;
    }

    /// <summary>Shows the menu with its top-left at (x, y) in DIPs, kept on the monitor.</summary>
    /// <param name="screenBottomDip">The bottom of the room it may take (the work area's): taller, it scrolls.</param>
    public void ShowAt(double x, double y, double screenRightDip, double screenBottomDip = double.PositiveInfinity)
    {
        Left = x; Top = y;
        FitHeight(screenBottomDip - y - 4);
        Materialize();
        Show();
        UpdateLayout();
        if (Left + ActualWidth > screenRightDip) Left = Math.Max(0, screenRightDip - ActualWidth);
    }

    /// <summary>
    /// Shows the menu as the submenu of a row of <paramref name="parent"/> (macOS's): its first row level with that row,
    /// at the parent's right edge, or at its left edge when it would run off <paramref name="screen"/> (DIPs); kept on it.
    /// </summary>
    public void ShowBeside(MenuPopup parent, Rect row, Rect screen)
    {
        Left = -32000; Top = -32000; // measured off-screen first
        FitHeight(screen.Height - 8); // within the screen, where it is then placed
        Materialize(); // over its parent it fades in at once instead (Fades: a plate over another lens)
        Show();
        UpdateLayout();
        double w = ActualWidth, h = ActualHeight;
        double x = parent.Left + parent.ActualWidth - Pad + 1; // over the parent's padding, by its rounded corner
        if (x + w > screen.Right) x = Math.Max(screen.Left, parent.Left - w + Pad - 1);
        double y = row.Top - Pad;
        if (y + h > screen.Bottom) y = Math.Max(screen.Top, screen.Bottom - h);
        Left = x; Top = y;
    }

    /// <summary>
    /// The chosen row blinks once (off, on), as macOS shows which item was chosen; then <paramref name="then"/> runs.
    /// Right away when animations are off.
    /// </summary>
    public void Blink(int i, Action then)
    {
        if (!_animate || i < 0 || i >= _rows.Count) { then(); return; }
        IsHitTestVisible = false;
        _highlight = i;
        Paint(i, false);
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(60) };
        int step = 0;
        timer.Tick += (_, _) =>
        {
            if (++step == 1) { Paint(i, true); return; }
            timer.Stop();
            then();
        };
        timer.Start();
    }

    /// <summary>
    /// Fades out (macOS menus do as they close), then closes. Presses go through it meanwhile; at once when animations are off. With
    /// its lens its glass goes by it, over a little longer, its rows first (2026-10-23).
    /// </summary>
    public void CloseFading()
    {
        bool lensy = _backdrop.Presents;
        Fades.CloseFading(this, _backdrop, !_animate ? 0 : lensy ? Motion.PresenceOut : Fades.MenuOutMs, lensy ? Motion.PresenceContentOut : 1);
    }

    /// <summary>Its glass comes by its lens (its rows there at once, as always): the user's "A レンズの強さで実体化", 2026-10-23.</summary>
    void Materialize()
    {
        if (_animate) Fades.Materialize(this, _backdrop, Motion.PresenceIn);
    }

    /// <summary>How many of the rows' icons show (diagnostics).</summary>
    public int IconsShown => _pictures.Count(p => p.picture.Source != null);

    void OnIconLoaded(string key)
    {
        foreach (var (k, source, picture) in _pictures)
            if (picture.Source == null && string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) picture.Source = _icons!.Get(k, source);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_icons != null) _icons.Loaded -= OnIconLoaded; // the service lives as long as the menu bar
        _backdrop.Dispose();
    }
}
