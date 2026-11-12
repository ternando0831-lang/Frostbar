using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Frostbar.App.Common;
using Frostbar.Core.Search;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Sys;

namespace Frostbar.App.Search;

/// <summary>
/// The search panel (M4, Spotlight-like): a large field and the answers under it, centred in the upper part of the
/// screen, over the same blurred plate as the menus (ADR-003). Unlike the menus it is typed into, so it activates; the
/// controller gives the foreground back to the previous app when it closes with Esc. Keys: ↑↓ choose, Enter opens,
/// Ctrl+Enter shows in File Explorer, Ctrl+1…4 the filters (stage 6-1, macOS 26's ⌘1…4: the buttons at the field's
/// right), Esc clears the field, then the filter, then closes. Since 2026-11-08 macOS 27's "Search or Ask" (the user's
/// "カプセルで、さらに見本に見た目をよせる", Apple's picture measured): a capsule 500 wide, dark glass shaded from its
/// top, no magnifier, a microphone at its right (Windows' voice typing), the filters' buttons showing only while the
/// pointer is over it, something is typed or a filter is on.
/// </summary>
public sealed class SearchWindow : Window
{
    /// <summary>
    /// DIPs. The capsule in Apple's picture is 0.34 of the screen wide and a sixth of that high (2026-11-08); 500 by 56
    /// here (680 wide with a rounded field before).
    /// </summary>
    public const double PanelWidth = 500, FieldHeight = 56, RowHeight = 32, MaxListHeight = 448, FilterButton = 30;

    readonly WindowBackdrop _backdrop;
    readonly StackPanel _list = new();
    readonly ScrollViewer _scroll;

    /// <summary>The list's scroll bar's opacity now, and whether the list scrolls (diagnostics: C8).</summary>
    public (double Bar, bool Scrolls) ListBar => (MacScrollBar.Shown(_scroll), _scroll.ScrollableHeight > 0);
    readonly Border _divider;
    readonly TextBlock _placeholder;
    readonly string _placeholderText;
    readonly List<(Border row, SearchResult result)> _rows = new();
    readonly Func<SearchFilter, string> _filterName;
    readonly Dictionary<SearchFilter, Border> _filterButtons = new();
    readonly Grid _glassBox = new() { Width = 20, Height = 20, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    Brush _text, _dim, _selected, _selectedText, _pressed, _hover;
    int _selection = -1;
    /// <summary>The shell's animation setting (the list glides under a wheel's notch).</summary>
    bool _animations;

    /// <summary>The filters offered, in the order of their buttons and of Ctrl+1…4.</summary>
    public static readonly SearchFilter[] Filters = [SearchFilter.Apps, SearchFilter.Files, SearchFilter.Actions, SearchFilter.Clipboard];

    /// <summary>The filter on (None: every kind answers).</summary>
    public SearchFilter Filter { get; private set; }
    public event Action? FilterChanged;
    /// <summary>The buttons of the filters offered (diagnostics: a spec clicks them).</summary>
    public IReadOnlyDictionary<SearchFilter, Border> FilterButtons => _filterButtons;

    public TextBox Field { get; }
    public IntPtr Handle => new WindowInteropHelper(this).Handle;
    public IntPtr BackdropHandle => _backdrop.BackdropHandle;
    public IReadOnlyList<SearchResult> Results => _rows.Select(r => r.result).ToList();
    public int Selection => _selection;
    /// <summary>The IME is composing: the text is not final yet (the search waits).</summary>
    public bool Composing { get; private set; }

    public event Action? QueryChanged;
    public event Action<SearchResult, bool>? Chosen; // the result, and whether to show it in File Explorer instead
    public event Action? Dismissed;                   // Esc on an empty field

    readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly Border _mic, _shade;

    /// <summary>The filters' buttons show (diagnostics).</summary>
    public bool ButtonsShown => _buttonsShown;
    bool _buttonsShown;

    /// <summary>Times the microphone asked Windows for voice typing (diagnostics).</summary>
    public int Dictations { get; private set; }

    /// <param name="filters">The filters to offer (the clipboard's only once it is kept).</param>
    /// <param name="dictation">The microphone's name (its tooltip).</param>
    public SearchWindow(ThemeSnapshot t, Material m, string placeholder, Func<SearchFilter, string> filterName, IReadOnlyCollection<SearchFilter> filters, string dictation)
    {
        _placeholderText = placeholder;
        _filterName = filterName;
        _animations = t.Animations;
        Title = "Frostbar.Search";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // takes the clicks over the whole panel
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        // Pinned: with SizeToContent=Height alone, the first WM_SIZE on showing came with the window 2 px wide and WPF
        // took that as its Width (2026-10-12; why WPF sized the HWND so is not established).
        Width = MinWidth = MaxWidth = PanelWidth;
        SizeToContent = SizeToContent.Height;
        FontFamily = Materials.UiFont;
        UseLayoutRounding = true;
        Language = XmlLanguage.GetLanguage("ja-JP"); // CJK in names falls back to Yu Gothic UI, not a Chinese face
        _text = Materials.Text(t);
        (_dim, _selected, _selectedText, _pressed, _hover) = Colors(t);

        Field = new TextBox
        {
            FontSize = 22, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = _text, CaretBrush = _text,
            VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0), Margin = new Thickness(0),
        };
        _placeholder = new TextBlock { Text = placeholder, FontSize = 22, Foreground = _dim, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        var fieldArea = new Grid { Height = FieldHeight };
        fieldArea.Children.Add(_placeholder);
        fieldArea.Children.Add(Field);
        // The filter's picture while one is on (no magnifier otherwise: Apple's capsule has none).
        _glass = Glyphs.Stroke(Glyphs.Search, 20, _dim, 1.6);
        _glassBox.Children.Add(_glass);
        var fieldRow = new DockPanel { Margin = new Thickness(24, 0, 12, 0) };
        // The microphone at the far right: Windows' voice typing into the field (Win+H).
        _mic = new Border
        {
            Width = FilterButton, Height = FilterButton, CornerRadius = new CornerRadius(FilterButton / 2), Background = Brushes.Transparent,
            Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = dictation,
        };
        _mic.MouseEnter += (_, _) => PaintMic();
        _mic.MouseLeave += (_, _) => PaintMic();
        _mic.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _mic.MouseLeftButtonUp += (_, e) => { e.Handled = true; Dictate(); };
        DockPanel.SetDock(_mic, System.Windows.Controls.Dock.Right);
        fieldRow.Children.Add(_mic);
        PaintMic();
        // The filters' buttons at the field's right (macOS 26's Spotlight), Ctrl+1…4 in their order; they show while
        // the pointer is over the capsule, something is typed or a filter is on (Apple's shows only its microphone).
        var buttons = _buttons;
        foreach (var f in Filters.Where(filters.Contains))
        {
            var b = new Border
            {
                Width = FilterButton, Height = FilterButton, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Margin = new Thickness(2, 0, 0, 0),
                ToolTip = string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0} (Ctrl+{1})", filterName(f), Array.IndexOf(Filters, f) + 1),
            };
            var filter = f;
            b.MouseEnter += (_, _) => PaintButton(b, filter);
            b.MouseLeave += (_, _) => PaintButton(b, filter);
            b.MouseLeftButtonDown += (_, e) => e.Handled = true; // not a drag, and the field keeps the focus
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; SetFilter(Filter == filter ? SearchFilter.None : filter); Field.Focus(); };
            _filterButtons[f] = b;
            buttons.Children.Add(b);
        }
        DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Right);
        fieldRow.Children.Add(buttons);
        DockPanel.SetDock(_glassBox, System.Windows.Controls.Dock.Left);
        fieldRow.Children.Add(_glassBox);
        fieldRow.Children.Add(fieldArea);
        PaintFilters();
        _buttons.Opacity = 0;
        MouseEnter += (_, _) => ShowButtons();
        MouseLeave += (_, _) => ShowButtons();
        // Apple's capsule is dark glass, darkest at its top (its picture: black there, what is behind showing through
        // below). Over the field only; dark appearance only (light glass stays light).
        _shade = new Border { CornerRadius = new CornerRadius(GlassShape.Search), Child = fieldRow, Background = Shade(t) };

        _divider = new Border { Height = 1, Background = _dim, Opacity = 0.35, Visibility = Visibility.Collapsed };
        _scroll = new ScrollViewer
        {
            Content = _list, MaxHeight = MaxListHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(8, 6, 8, 8), Visibility = Visibility.Collapsed,
            Focusable = false,
        };
        // The thin bar over the rows while the list moves (it had none: a long list gave no sign of more below, C8).
        MacScrollBar.Apply(_scroll, () => _animations);
        SmoothScroll.Attach(_scroll, () => _animations);
        // Words chosen in the field in the accent (they were the system's blue: C8).
        Field.SelectionBrush = new SolidColorBrush(Color.FromRgb(t.Accent.R, t.Accent.G, t.Accent.B));
        var root = new StackPanel();
        root.Children.Add(_shade);
        root.Children.Add(_divider);
        root.Children.Add(_scroll);
        Content = root;

        Field.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = Field.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowButtons();
            if (!Composing) QueryChanged?.Invoke();
        };
        // The IME's text is not the query yet: search once it is committed.
        TextCompositionManager.AddPreviewTextInputStartHandler(Field, (_, _) => Composing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(Field, (_, e) => Composing = e.TextComposition.CompositionText.Length > 0);
        TextCompositionManager.AddPreviewTextInputHandler(Field, (_, _) => { Composing = false; Dispatcher.BeginInvoke(() => QueryChanged?.Invoke()); });
        PreviewKeyDown += OnKey;

        _backdrop = new WindowBackdrop(this, m);
        PressLight.Attach(this, () => _animations && _backdrop.Glassy);
        SourceInitialized += (_, _) => Native.AddExStyle(Handle, Native.WS_EX_TOOLWINDOW);
    }

    static (Brush dim, Brush selected, Brush selectedText, Brush pressed, Brush hover) Colors(ThemeSnapshot t)
    {
        var c = ((SolidColorBrush)Materials.Text(t)).Color;
        var a = t.Accent;
        // A filter that is on is shaded in the text's colour, not the accent (a state's mark: on this PC the accent is red).
        return (new SolidColorBrush(Color.FromArgb((byte)(c.A * 0.55), c.R, c.G, c.B)),
                new SolidColorBrush(Color.FromRgb(a.R, a.G, a.B)), Brushes.White,
                new SolidColorBrush(Color.FromArgb(0x30, c.R, c.G, c.B)), new SolidColorBrush(Color.FromArgb(0x16, c.R, c.G, c.B)));
    }

    /// <summary>A filter's picture: the apps' grid, a document, an action's bolt, a clipboard.</summary>
    static Geometry FilterGlyph(SearchFilter f) => f switch
    {
        SearchFilter.Apps => Glyphs.AppGridPicture,
        SearchFilter.Files => Glyphs.Document,
        SearchFilter.Actions => Glyphs.Bolt,
        SearchFilter.Clipboard => Glyphs.Clipboard,
        _ => Glyphs.Search,
    };

    /// <summary>
    /// Turns a filter on (None: off), as its button or Ctrl+1…4 does: the magnifier becomes the filter's picture and
    /// the empty field names it. The controller answers again (<see cref="FilterChanged"/>).
    /// </summary>
    public void SetFilter(SearchFilter f)
    {
        if (f != SearchFilter.None && !_filterButtons.ContainsKey(f)) return; // not offered (the clipboard before it is kept)
        if (f == Filter) return;
        Filter = f;
        PaintFilters();
        FilterChanged?.Invoke();
    }

    void PaintFilters()
    {
        foreach (var (f, b) in _filterButtons) PaintButton(b, f);
        _glassBox.Children.Clear();
        // The Apps grid is drawn filled; the others are lines.
        _glass = Filter == SearchFilter.Apps ? Glyphs.Fill(FilterGlyph(Filter), 20, _dim) : Glyphs.Stroke(FilterGlyph(Filter), 20, _dim, 1.6);
        _glassBox.Children.Add(_glass);
        _glassBox.Visibility = Filter == SearchFilter.None ? Visibility.Collapsed : Visibility.Visible;
        _placeholder.Text = Filter == SearchFilter.None ? _placeholderText : _filterName(Filter);
        ShowButtons();
    }

    /// <summary>The filters' buttons fade in while the pointer is over the capsule, something is typed or a filter is on, and out otherwise.</summary>
    void ShowButtons()
    {
        bool show = IsMouseOver || Field.Text.Length > 0 || Filter != SearchFilter.None;
        if (show == _buttonsShown) return;
        _buttonsShown = show;
        Tween.To(_buttons, OpacityProperty, show ? 1 : 0, _animations ? Frostbar.Core.Shell.MotionTable.SearchButtons : 0, Tween.Out);
    }

    void PaintMic()
    {
        _mic.Background = _mic.IsMouseOver ? _hover : Brushes.Transparent;
        _mic.Child = Glyphs.Stroke(Glyphs.Microphone, 18, _text, 1.5);
        ((FrameworkElement)_mic.Child).HorizontalAlignment = HorizontalAlignment.Center;
    }

    /// <summary>The microphone: Windows' voice typing (Win+H) into the field, which keeps the focus.</summary>
    void Dictate()
    {
        Field.Focus();
        Dictations++;
        Note("dictation");
        Frostbar.Platform.Input.InputInjector.SendChord(new Frostbar.Core.Menus.Shortcut(Frostbar.Core.Menus.Mods.Win, 0x48 /*H*/), Handle);
    }

    /// <summary>Apple's dark capsule: black 45% at its top to 8% at its bottom (dark appearance; none in light, high contrast or solid).</summary>
    static Brush Shade(ThemeSnapshot t) => !t.Dark || t.HighContrast
        ? Brushes.Transparent
        : new LinearGradientBrush(Color.FromArgb(0x73, 0, 0, 0), Color.FromArgb(0x14, 0, 0, 0), 90);

    void PaintButton(Border b, SearchFilter f)
    {
        bool on = Filter == f;
        b.Background = on ? _pressed : b.IsMouseOver ? _hover : Brushes.Transparent;
        var brush = on ? _text : _dim;
        b.Child = f == SearchFilter.Apps ? Glyphs.Fill(FilterGlyph(f), 16, brush) : Glyphs.Stroke(FilterGlyph(f), 16, brush, 1.4);
        ((FrameworkElement)b.Child).HorizontalAlignment = HorizontalAlignment.Center;
    }

    /// <summary>Fades in (the field and the blur) over <paramref name="ms"/>; at once for 0.</summary>
    public void FadeIn(double ms) => Fades.FadeIn(this, _backdrop, ms);

    /// <summary>Fades out over <paramref name="ms"/>, then <paramref name="done"/> (at once for 0).</summary>
    public void FadeOut(double ms, Action done) => Fades.FadeOut(this, _backdrop, ms, done);

    /// <summary>Fully shown and taking input again.</summary>
    public void ResetFade() => Fades.Reset(this, _backdrop);

    System.Windows.Shapes.Path _glass;

    public void SetMaterial(ThemeSnapshot t, Material m)
    {
        _backdrop.SetMaterial(m);
        _animations = t.Animations;
        _text = Materials.Text(t);
        (_dim, _selected, _selectedText, _pressed, _hover) = Colors(t);
        Field.Foreground = _text;
        Field.CaretBrush = _text;
        Field.SelectionBrush = new SolidColorBrush(Color.FromRgb(t.Accent.R, t.Accent.G, t.Accent.B)); // an accent chosen since
        _placeholder.Foreground = _dim;
        // The window lives for the whole session: the magnifier, the filters and the divider kept their first colours
        // (nearly invisible after a switch between light and dark).
        PaintFilters();
        PaintMic();
        _shade.Background = Shade(t);
        _divider.Background = _dim;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed || Composing) return; // the IME's own keys (its Enter commits, its Esc cancels)
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        // Ctrl+1…4: the filters in their buttons' order (pressed again: off).
        int n = !ctrl || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? -1 : e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0, Key.D2 or Key.NumPad2 => 1, Key.D3 or Key.NumPad3 => 2, Key.D4 or Key.NumPad4 => 3, _ => -1,
        };
        if (n >= 0)
        {
            e.Handled = true;
            var f = Filters[n];
            SetFilter(Filter == f ? SearchFilter.None : f);
            return;
        }
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (Field.Text.Length > 0) Field.Clear();
                else if (Filter != SearchFilter.None) SetFilter(SearchFilter.None);
                else Dismissed?.Invoke();
                break;
            case Key.Down: e.Handled = true; Select(_selection + 1); Note("key down " + _selection); break;
            case Key.Up: e.Handled = true; Select(_selection - 1); Note("key up " + _selection); break;
            case Key.Enter:
                e.Handled = true;
                if (_selection >= 0 && _selection < _rows.Count) Chosen?.Invoke(_rows[_selection].result, ctrl);
                break;
        }
    }

    /// <summary>
    /// Shows the answers under their section titles; the first one is chosen. With <paramref name="keepChoice"/> (more
    /// answers for the same text: files, icons) the one chosen stays chosen and the list where it was scrolled: they
    /// put the choice back on the first row, and an Enter right after an arrow opened another answer (2026-10-14).
    /// </summary>
    /// <param name="glyph">The picture drawn where an answer has no icon (an action's).</param>
    public void Show(IReadOnlyList<SearchResult> results, Func<SearchResult, string> section, Func<SearchResult, string?> kindLabel,
        Func<SearchResult, ImageSource?> icon, Func<SearchResult, Geometry?> glyph, bool keepChoice = false)
    {
        var chosen = keepChoice && _selection >= 0 && _selection < _rows.Count ? _rows[_selection].result : null;
        double scrolled = _scroll.VerticalOffset;
        // The list's height as it shows now (-1: not shown): a list already open grows or shrinks to its new length (E1).
        double was = _scroll.IsVisible && _scroll.ActualHeight > 0 ? _scroll.ActualHeight : -1;
        _list.Children.Clear();
        _rows.Clear();
        string? last = null;
        foreach (var r in results)
        {
            string s = section(r);
            if (s != last)
            {
                _list.Children.Add(new TextBlock
                {
                    Text = s, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = _dim,
                    Margin = new Thickness(10, _list.Children.Count == 0 ? 2 : 10, 10, 3),
                });
                last = s;
            }
            var row = Row(r, kindLabel(r), icon(r), glyph(r));
            int index = _rows.Count;
            row.MouseLeftButtonUp += (_, _) => { Select(index); Note("click " + index); Chosen?.Invoke(r, false); };
            // The pointer chooses a row only when it moves: WPF also says it entered a row that comes under it at rest.
            row.MouseMove += (_, _) => { if (PointerMoved() && index != _selection) { Select(index, bringIntoView: false); Note("pointer " + index); } };
            _rows.Add((row, r));
            _list.Children.Add(row);
        }
        Native.GetCursorPos(out _pointer); // rows that come under the pointer at rest do not take the choice
        bool any = _rows.Count > 0;
        // The first answers at once, not growing: a window sized to its content that grew frame by frame showed its first
        // answers 80–170 ms later than this (2026-10-12, filmed). Answers fast matter more here than the motion. Once the
        // list shows, a new length is reached gliding (E1, 2026-10-19): the rows are there at once, only the list's edge moves.
        _divider.Visibility = _scroll.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        double rounding = GlassShape.Search;
        _shade.CornerRadius = any ? new CornerRadius(rounding, rounding, 0, 0) : new CornerRadius(rounding); // the capsule's top over a list
        if (any && was > 0 && _animations) Glide(was);
        else
        {
            Note($"grow: not ({(any ? "" : "no rows, ")}{(was > 0 ? "" : "was not shown, ")}{(_animations ? "" : "no animations")})");
            Tween.Stop(_scroll, HeightProperty);
            _scroll.Height = double.NaN;
        }
        _selection = -1;
        int again = chosen == null ? -1 : _rows.FindIndex(x => x.result.Kind == chosen.Kind && x.result.Target == chosen.Target);
        if (again >= 0)
        {
            Select(again, bringIntoView: false);
            Note($"show: kept {again} of {_rows.Count}");
            _scroll.ScrollToVerticalOffset(scrolled);
        }
        else
        {
            Select(any ? 0 : -1);
            Note($"show: first of {_rows.Count}{(chosen != null ? $" ({chosen.Kind} {chosen.Target} gone)" : "")}");
        }
    }

    /// <summary>Lengths the list glided to (diagnostics: E1).</summary>
    public int Glides { get; private set; }

    /// <summary>From <paramref name="was"/> (DIPs) to the list's new height, the window following (it is sized to its content).</summary>
    void Glide(double was)
    {
        Tween.Stop(_scroll, HeightProperty);
        _scroll.Height = double.NaN;
        // The rows themselves measured: the list's frame measured again gave its old height (its measure was not made
        // invalid by new rows until the next layout pass: 448 to 448, 2026-10-20).
        var pad = _scroll.Padding;
        _list.Measure(new Size(Math.Max(1, _scroll.ActualWidth - pad.Left - pad.Right), double.PositiveInfinity));
        double to = Math.Min(MaxListHeight, _list.DesiredSize.Height + pad.Top + pad.Bottom);
        Note($"grow: {was:0.#} to {to:0.#}");
        if (Math.Abs(to - was) < 1) return;
        Glides++;
        _scroll.Height = was;
        Tween.To(_scroll, HeightProperty, to, Frostbar.Core.Shell.MotionTable.SearchGrow, Tween.Out, () => _scroll.Height = double.NaN);
    }

    /// <summary>
    /// Where the pointer was when the rows were last shown or it last chose one (screen pixels). A row that comes under
    /// the pointer at rest (answers that come later: the files) does not take the choice: the pointer must move, as in
    /// Spotlight (2026-10-16: Enter opened a note that had come under the pointer instead of the action chosen).
    /// </summary>
    POINT _pointer;

    bool PointerMoved()
    {
        Native.GetCursorPos(out var p);
        if (p.X == _pointer.X && p.Y == _pointer.Y) return false;
        _pointer = p;
        return true;
    }

    /// <summary>The rows, in the order of the answers (diagnostics: where they are).</summary>
    public IReadOnlyList<FrameworkElement> RowElements => [.. _rows.Select(x => (FrameworkElement)x.row)];

    Border Row(SearchResult r, string? kind, ImageSource? icon, Geometry? glyph)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        UIElement picture = icon != null
            ? new Image { Source = icon, Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center }
            : ReferenceEquals(glyph, Glyphs.AppGridPicture) ? Glyphs.Fill(glyph, 18, _dim) // squares, drawn filled
            : Glyphs.Stroke(glyph ?? (r.Kind == SearchKind.Web ? Glyphs.Globe : r.Kind is SearchKind.Calculation or SearchKind.Conversion ? Glyphs.EqualsSign : Glyphs.Search), 18, _dim, 1.4);
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        var title = new TextBlock { Text = r.Title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = _text };
        var detail = new TextBlock
        {
            Text = kind ?? "", FontSize = 12, Foreground = _dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
            MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis, // 300 when the panel was 680 wide
        };
        Grid.SetColumn(title, 1);
        Grid.SetColumn(detail, 2);
        grid.Children.Add(picture);
        grid.Children.Add(title);
        grid.Children.Add(detail);
        return new Border { Child = grid, Height = RowHeight, Padding = new Thickness(8, 0, 10, 0), CornerRadius = new CornerRadius(GlassShape.SearchRow), Background = Brushes.Transparent };
    }

    readonly Queue<string> _choices = new();

    /// <summary>Diagnostics: the last choices and what made them (show, key, pointer, click), with the time (TickCount ms).</summary>
    public IReadOnlyCollection<string> Choices => _choices;

    void Note(string why)
    {
        _choices.Enqueue($"{Environment.TickCount % 100_000} {why}");
        while (_choices.Count > 12) _choices.Dequeue();
    }

    void Select(int index, bool bringIntoView = true)
    {
        if (_rows.Count == 0) { _selection = -1; return; }
        index = Math.Clamp(index, 0, _rows.Count - 1);
        if (_selection >= 0 && _selection < _rows.Count) Paint(_rows[_selection].row, false);
        _selection = index;
        Paint(_rows[index].row, true);
        // Glides there as a notch does (it jumped a row's height at each key: 2026-10-19, "B4").
        if (bringIntoView) SmoothScroll.IntoView(_scroll, _rows[index].row, _animations);
    }

    void Paint(Border row, bool selected)
    {
        row.Background = selected ? _selected : Brushes.Transparent;
        var grid = (Grid)row.Child;
        ((TextBlock)grid.Children[1]).Foreground = selected ? _selectedText : _text;
        ((TextBlock)grid.Children[2]).Foreground = selected ? _selectedText : _dim;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _backdrop.Dispose();
    }
}
