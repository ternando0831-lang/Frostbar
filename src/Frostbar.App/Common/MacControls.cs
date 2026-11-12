using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Frostbar.Platform.Sys;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Common;

/// <summary>
/// Buttons and text fields in our own look (macOS's): rounded, the default button in the accent colour, a soft focus
/// ring around a field. Windows' own templates showed square grey buttons and a white box in the dark dialogs.
/// </summary>
public static class MacControls
{
    const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    static readonly Lazy<ControlTemplate> ButtonTemplate = new(() => (ControlTemplate)XamlReader.Parse(
        $"<ControlTemplate {Ns} TargetType='Button'>" +
        "<Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' " +
        "CornerRadius='6' Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>" +
        "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' RecognizesAccessKey='True'/></Border>" +
        "<ControlTemplate.Triggers>" +
        "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.9'/></Trigger>" +
        "<Trigger Property='IsPressed' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.72'/></Trigger>" +
        "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='b' Property='Opacity' Value='0.4'/></Trigger>" +
        "</ControlTemplate.Triggers></ControlTemplate>"));

    // With Liquid Glass a button gives under a press instead of dimming (2026-10-23, the user's "A 沈む＋隣へ光る"):
    // the same template without the pressed opacity; GlassBounce shrinks it.
    static readonly Lazy<ControlTemplate> GlassButtonTemplate = new(() => (ControlTemplate)XamlReader.Parse(
        $"<ControlTemplate {Ns} TargetType='Button'>" +
        "<Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' " +
        "CornerRadius='6' Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>" +
        "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' RecognizesAccessKey='True'/></Border>" +
        "<ControlTemplate.Triggers>" +
        "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.9'/></Trigger>" +
        "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='b' Property='Opacity' Value='0.4'/></Trigger>" +
        "</ControlTemplate.Triggers></ControlTemplate>"));

    /// <summary>A push button: <paramref name="primary"/> is the default one (accent, Enter), the other cancels (Esc).</summary>
    public static Button Button(string text, bool primary, ThemeSnapshot t, bool cancels = true)
    {
        var accent = new SolidColorBrush(Color.FromRgb(t.Accent.R, t.Accent.G, t.Accent.B));
        bool glass = GlassKnob.Enabled() && t.Animations;
        var b = new Button
        {
            Content = text, MinWidth = 88, Padding = new Thickness(14, 4, 14, 5), Margin = new Thickness(5, 0, 5, 0), FontSize = 13,
            Template = glass ? GlassButtonTemplate.Value : ButtonTemplate.Value, BorderThickness = new Thickness(primary ? 0 : 0.5), // the focus ring is the shell's (ShellStyles: D1)
            IsDefault = primary, IsCancel = !primary && cancels,
        };
        if (glass)
        {
            GlassBounce.Attach(b, () => GlassKnob.Enabled(), Frostbar.Core.Shell.PressGive.Button);
            PressSpill.SetGlass(b, true);
        }
        if (primary) { b.Background = accent; b.Foreground = Brushes.White; }
        else
        {
            b.Background = new SolidColorBrush(t.Dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Colors.White);
            b.BorderBrush = new SolidColorBrush(t.Dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x33, 0, 0, 0));
            b.Foreground = Materials.Text(t);
        }
        return b;
    }

    /// <summary>The height of an alert's buttons (28 pt in Apple's picture of one).</summary>
    public const double AlertButtonHeight = 28;

    /// <summary>A button of an alert (macOS 26's): a capsule filling its place, the other than the action a plain grey.</summary>
    public static Button AlertButton(string text, bool primary, ThemeSnapshot t)
    {
        var b = Button(text, primary, t);
        b.Margin = new Thickness(0);
        b.MinWidth = 0;
        b.Height = AlertButtonHeight;
        if (!primary)
        {
            b.BorderThickness = new Thickness(0);
            b.Background = new SolidColorBrush(t.Dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0));
        }
        b.ApplyTemplate();
        if (b.Template.FindName("b", b) is Border border) border.CornerRadius = new CornerRadius(AlertButtonHeight / 2);
        return b;
    }

    static ControlTemplate FieldTemplate(string type, Color ring) => (ControlTemplate)XamlReader.Parse(
        $"<ControlTemplate {Ns} TargetType='{type}'>" +
        "<Border x:Name='ring' BorderThickness='3' BorderBrush='Transparent' CornerRadius='8' Margin='-3' SnapsToDevicePixels='True'>" +
        "<Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='5'>" +
        "<ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}' VerticalAlignment='Center' Focusable='False' " +
        "HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='Hidden'/></Border></Border>" +
        "<ControlTemplate.Triggers>" +
        $"<Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='ring' Property='BorderBrush' Value='#{ring.A:X2}{ring.R:X2}{ring.G:X2}{ring.B:X2}'/></Trigger>" +
        "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='ring' Property='Opacity' Value='0.5'/></Trigger>" +
        "</ControlTemplate.Triggers></ControlTemplate>");

    /// <summary>
    /// A segmented control (macOS's): a few choices side by side, the chosen one raised. <paramref name="chosen"/> runs
    /// with the new index when the user picks another. -1 shows none chosen (a value set by hand in settings.json).
    /// </summary>
    public static Segmented Segments(IReadOnlyList<string> labels, int selected, ThemeSnapshot t, Action<int> chosen) => new(labels, selected, t, chosen);

    public sealed class Segmented : Border, Inks.ITinted
    {
        /// <summary>How long the raised part takes to slide to another choice (macOS's).</summary>
        public const double SlideMs = Motion.Segment;

        /// <summary>It lands on a spring, a little past the choice and back (macOS 27's; it eased out until 2026-10-19, "B16").</summary>
        // Asked at each slide: made once, reduced motion turned on later went on overshooting (and turned off, never did).
        static Func<double, double> Landing => Tween.Spring(Motion.SegmentSpring);

        readonly List<Border> _parts = new();
        readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
        /// <summary>
        /// The raised part, under the labels: it slides from one choice to the next (it jumped: each part had the raised
        /// background or not, 2026-10-17).
        /// </summary>
        readonly Border _raised;
        readonly TranslateTransform _raisedAt = new();
        readonly bool _animate;
        readonly bool _dark;
        int _index;

        public IReadOnlyList<Border> Parts => _parts;
        public int Index => _index;

        internal Segmented(IReadOnlyList<string> labels, int selected, ThemeSnapshot t, Action<int> chosen)
        {
            CornerRadius = new CornerRadius(7);
            Padding = new Thickness(2);
            Background = new SolidColorBrush(t.Dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0));
            HorizontalAlignment = HorizontalAlignment.Right;
            _animate = t.Animations;
            _dark = t.Dark;
            _raised = new Border
            {
                CornerRadius = new CornerRadius(5), Background = RaisedFill(t.Dark),
                HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = _raisedAt, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            };
            for (int i = 0; i < labels.Count; i++)
            {
                int index = i;
                var part = new Border
                {
                    CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 2, 10, 3), MinWidth = 44, Background = Brushes.Transparent,
                    Child = new TextBlock { Text = labels[i], FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Materials.Text(t) },
                };
                // Chosen by a press that began on it (a pop-up's dismissing press, or one slid over from a label, chose it).
                bool pressed = false;
                part.MouseLeftButtonDown += (_, _) => pressed = true;
                part.MouseLeave += (_, _) => pressed = false;
                part.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    if (!pressed) return;
                    pressed = false;
                    if (index == _index) return;
                    Select(index);
                    chosen(index);
                };
                _parts.Add(part);
                _row.Children.Add(part);
            }
            var layers = new Grid();
            layers.Children.Add(_raised);
            layers.Children.Add(_row);
            Child = layers;
            // Tab reaches it, ← and → choose (D3); its parts named for a screen reader (D4).
            _chosen = chosen;
            Focusable = true;
            for (int i = 0; i < _parts.Count; i++) System.Windows.Automation.AutomationProperties.SetName(_parts[i], labels[i]);
            // Laid out (or out anew: the words changed): under its choice at once.
            _row.SizeChanged += (_, _) => Raise(animate: false);
            _index = selected;
            Raise(animate: false);
        }

        public void Select(int index)
        {
            if (index == _index) return;
            _index = index;
            Raise(animate: _animate && IsLoaded && IsVisible);
        }

        readonly Action<int> _chosen;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || !IsEnabled || _parts.Count == 0) return;
            int to = e.Key switch
            {
                Key.Left => Math.Max(0, _index - 1),
                Key.Right => Math.Min(_parts.Count - 1, _index + 1),
                Key.Home => 0,
                Key.End => _parts.Count - 1,
                _ => -1,
            };
            if (to < 0) return;
            e.Handled = true;
            if (to == _index) return;
            Select(to);
            _chosen(to);
        }

        /// <summary>Read by a screen reader with its choice as its value (D4).</summary>
        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        sealed class Peer(Segmented s) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(s), System.Windows.Automation.Provider.IValueProvider
        {
            protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => System.Windows.Automation.Peers.AutomationControlType.Tab;
            protected override string GetClassNameCore() => nameof(Segmented);
            public override object? GetPattern(System.Windows.Automation.Peers.PatternInterface p) => p == System.Windows.Automation.Peers.PatternInterface.Value ? this : base.GetPattern(p);
            public string Value => s._index >= 0 && s._index < s._parts.Count ? System.Windows.Automation.AutomationProperties.GetName(s._parts[s._index]) : "";
            public bool IsReadOnly => !s.IsEnabled;
            public void SetValue(string value)
            {
                int i = s._parts.FindIndex(p => System.Windows.Automation.AutomationProperties.GetName(p) == value);
                if (i < 0 || i == s._index) return;
                s.Select(i);
                s._chosen(i);
            }
        }

        /// <summary>The raised part under the chosen one: slid there, or put there at once.</summary>
        void Raise(bool animate)
        {
            if (_index < 0 || _index >= _parts.Count) { _raised.Visibility = Visibility.Collapsed; return; }
            var part = _parts[_index];
            if (part.ActualWidth <= 0 || !_row.IsAncestorOf(part)) return; // not laid out yet: SizeChanged comes
            double x = part.TransformToAncestor(_row).Transform(new Point(0, 0)).X, w = part.ActualWidth;
            bool wasShown = _raised.Visibility == Visibility.Visible;
            _raised.Visibility = Visibility.Visible;
            if (_from is { } from)
            {
                // Going on from where the one this replaces was on its way (SlideFrom).
                _from = null;
                _raisedAt.X = from.X;
                _raised.Width = from.Width;
                Tween.Together(SlideMs, [new(_raisedAt, TranslateTransform.XProperty, x, Landing), new(_raised, WidthProperty, w, Landing)]);
                Continued++;
                return;
            }
            if (!animate || !wasShown)
            {
                Tween.Stop(_raisedAt, TranslateTransform.XProperty);
                Tween.Stop(_raised, WidthProperty);
                _raisedAt.X = x;
                _raised.Width = w;
                return;
            }
            // Its place and width as one movement: each its own, they could round a refresh apart (Tween.Together).
            Tween.Together(SlideMs, [new(_raisedAt, TranslateTransform.XProperty, x, Landing), new(_raised, WidthProperty, w, Landing)]);
        }

        static readonly Brush RaisedDark = Frozen(new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF))), RaisedLight = Frozen(new SolidColorBrush(Colors.White));

        static Brush Frozen(Brush b) { b.Freeze(); return b; }

        // The same brush each time: the panel maps again after each of its layouts.
        static Brush RaisedFill(bool dark) => dark ? RaisedDark : RaisedLight;

        /// <summary>
        /// On glass turned to the other tone, the raised part is that tone's own (white on light glass): mapped as a faint
        /// fill it came out a black darker than the track (#59FFFFFF to #37000000 on #18000000: the battery panel's power
        /// mode, 2026-10-19). The track and the labels are mapped with the rest of the part.
        /// </summary>
        void Inks.ITinted.Tone(Inks.Mapping? map) => _raised.Background = RaisedFill(map == null ? _dark : !map.ToLight);

        /// <summary>The raised part's fill (diagnostics: white on glass turned light).</summary>
        public Color RaisedColor => (_raised.Background as SolidColorBrush)?.Color ?? Colors.Transparent;

        /// <summary>Where the raised part is now, from the left of the choices, and how wide (DIPs; diagnostics: it slides).</summary>
        public (double X, double Width) RaisedAt => (_raisedAt.X, _raised.ActualWidth);

        /// <summary>The raised part's place and width as set at this frame (diagnostics "tween-trace": they move as one).</summary>
        public (double X, double Width) RaisedSet => (_raisedAt.X, _raised.Width);

        /// <summary>The raised part is on its way to the choice.</summary>
        public bool Sliding => Tween.Moving(_raisedAt, TranslateTransform.XProperty);

        (double X, double Width)? _from;

        /// <summary>
        /// Made again in place of one whose raised part was on its way (its page made again right after the choice: a
        /// theme, a choice that changes what other rows can do): it goes on from <paramref name="at"/>, where that one
        /// was. It started again under the choice, the slide cut short (2026-10-19, the user's "B12").
        /// </summary>
        public void SlideFrom((double X, double Width) at)
        {
            if (!_animate) return;
            _from = at;
            Raise(animate: false); // laid out already: at once; else when it is
        }

        /// <summary>Slides gone on from a control made again (diagnostics).</summary>
        public static int Continued { get; private set; }
    }

    /// <summary>
    /// A list's row chosen (a pop-up button's, a "…" menu's): it blinks once (off, on: 60 ms each), as macOS's menus show
    /// the item chosen; then <paramref name="chosen"/> runs and the list fades out (0.18 s, the menus' fade) before it
    /// closes. It closed at once (2026-10-17). At once when animations are off. <paramref name="current"/> says the list
    /// was not opened again meanwhile (a fade of the list before must not close the new one).
    /// </summary>
    static void ChooseRow(GlassList host, Border list, Action lit, Action unlit, bool animate, Func<bool> current, Action chosen)
    {
        if (!animate) { host.FadeOut(false, current, () => Reset(list)); chosen(); return; }
        list.IsHitTestVisible = false;
        unlit();
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(60) };
        int step = 0;
        timer.Tick += (_, _) =>
        {
            if (!current()) { timer.Stop(); return; }
            if (++step == 1) { lit(); return; }
            timer.Stop();
            chosen();
            host.FadeOut(true, current, () => Reset(list));
        };
        timer.Start();
    }

    /// <summary>A list shown fully again, taking clicks (opened again, or closed after its fade).</summary>
    static void Reset(Border list)
    {
        Tween.Stop(list, UIElement.OpacityProperty);
        list.BeginAnimation(UIElement.OpacityProperty, null);
        list.Opacity = 1;
        list.IsHitTestVisible = true;
    }

    /// <summary>
    /// A pop-up button (macOS's): the chosen one's name with a chevron; a click opens the list under it, with a tick on
    /// the chosen one. For choices too many for <see cref="Segments"/>.
    /// </summary>
    public sealed class PopUp : Border
    {
        readonly System.Windows.Controls.Primitives.Popup _popup;
        readonly StackPanel _list = new();
        readonly ScrollViewer _scroller;
        readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly IReadOnlyList<string> _labels;
        readonly ThemeSnapshot _t;
        readonly Action<int> _chosen;
        readonly List<Border> _rows = new();
        readonly Border _listBox;
        readonly GlassList _host;
        /// <summary>How many times the list was opened: a row chosen in an earlier one does not close this one.</summary>
        int _opened;

        public int Index { get; private set; }
        public bool IsOpen => _host.IsOpen;
        /// <summary>The open list stands on the menus' glass (diagnostics: C3).</summary>
        public bool OnGlass => _host.OnGlass;
        /// <summary>How opaque the open list is (diagnostics: it fades out after a row is chosen).</summary>
        public double ListOpacity => _listBox.Opacity;
        /// <summary>The list's rows while it is open (diagnostics: the specs click them).</summary>
        public IReadOnlyList<Border> Rows => _rows;
        /// <summary>Where the list's rows show (they scroll inside it): diagnostics.</summary>
        public FrameworkElement ListView => _scroller;

        public PopUp(IReadOnlyList<string> labels, int selected, ThemeSnapshot t, Action<int> chosen)
        {
            _labels = labels;
            _t = t;
            _chosen = chosen;
            var text = Materials.Text(t);
            CornerRadius = new CornerRadius(6);
            Padding = new Thickness(10, 3, 6, 4);
            MinWidth = 132;
            Background = new SolidColorBrush(t.Dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Colors.White);
            BorderBrush = new SolidColorBrush(t.Dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x33, 0, 0, 0));
            BorderThickness = new Thickness(0.5);
            _text.Foreground = text;
            var chevron = Glyphs.Stroke(Glyphs.Chevron, 11, text, 1.6);
            chevron.LayoutTransform = new RotateTransform(90);
            chevron.Margin = new Thickness(8, 0, 0, 0);
            chevron.VerticalAlignment = VerticalAlignment.Center;
            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(chevron, System.Windows.Controls.Dock.Right);
            row.Children.Add(chevron);
            row.Children.Add(_text);
            Child = row;
            // A long list (the half hours of a day, the cities) scrolls inside a list as tall as a screen holds well.
            _scroller = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 320, Focusable = false };
            MacScrollBar.Apply(_scroller, () => _t.Animations);
            SmoothScroll.Attach(_scroller, () => _t.Animations);
            var list = new Border
            {
                Child = _scroller, Padding = new Thickness(4), CornerRadius = new CornerRadius(GlassShape.Popup),
                Background = new SolidColorBrush(t.Dark ? Color.FromRgb(0x32, 0x32, 0x34) : Color.FromRgb(0xF6, 0xF6, 0xF6)),
                BorderBrush = new SolidColorBrush(t.Dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0, 0, 0)), BorderThickness = new Thickness(0.5),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.3, Direction = 270 },
                Margin = new Thickness(0, 0, 8, 10),
                // A popup made in code is outside the window's tree: it inherits nothing (its rows came in the system's
                // font and size under a button in ours).
                UseLayoutRounding = true, Language = System.Windows.Markup.XmlLanguage.GetLanguage("ja-JP"),
            };
            TextElement.SetFontFamily(list, Materials.UiFont);
            TextElement.SetFontSize(list, 13);
            _listBox = list;
            _popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
                VerticalOffset = 2,
                Child = list,
            };
            _host = new GlassList(this, list, t, _popup);
            MouseLeftButtonDown += (_, e) => e.Handled = true;
            MouseLeftButtonUp += (_, e) => { e.Handled = true; Open(); };
            Focusable = true; // the keys: OnKeyDown
            Select(selected);
        }

        /// <summary>Read by a screen reader as a list that opens, its choice as its value (D4).</summary>
        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        sealed class Peer(PopUp p) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(p), System.Windows.Automation.Provider.IValueProvider, System.Windows.Automation.Provider.IExpandCollapseProvider
        {
            protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => System.Windows.Automation.Peers.AutomationControlType.ComboBox;
            protected override string GetClassNameCore() => nameof(PopUp);
            public override object? GetPattern(System.Windows.Automation.Peers.PatternInterface i) =>
                i is System.Windows.Automation.Peers.PatternInterface.Value or System.Windows.Automation.Peers.PatternInterface.ExpandCollapse ? this : base.GetPattern(i);
            public string Value => p._text.Text;
            public bool IsReadOnly => !p.IsEnabled;
            public void SetValue(string value)
            {
                int i = p._labels.ToList().IndexOf(value);
                if (i < 0 || i == p.Index) return;
                p.Select(i);
                p._chosen(i);
            }
            public System.Windows.Automation.ExpandCollapseState ExpandCollapseState => p.IsOpen ? System.Windows.Automation.ExpandCollapseState.Expanded : System.Windows.Automation.ExpandCollapseState.Collapsed;
            public void Expand() { if (!p.IsOpen) p.Open(); }
            public void Collapse() => p.CloseList();
        }

        public void Select(int index)
        {
            Index = index;
            _text.Text = index >= 0 && index < _labels.Count ? _labels[index] : "—";
        }

        /// <summary>Closes the list (Esc: the window's own Esc closed the whole settings window under it).</summary>
        public void CloseList() => _host.Close();

        /// <summary>Every label it offers (the settings' search reads the unchosen ones too).</summary>
        public IReadOnlyList<string> Labels => _labels;

        // The open list's rows as the keys light and choose them, and the row the keys are on (-1: none).
        readonly List<(Action Lit, Action Unlit, Action Choose)> _keys = new();
        int _keyed = -1;

        void Key(int i)
        {
            if (_keyed >= 0 && _keyed < _keys.Count) _keys[_keyed].Unlit();
            _keyed = i;
            if (i < 0 || i >= _keys.Count) return;
            _keys[i].Lit();
            SmoothScroll.IntoView(_scroller, _rows[i], _t.Animations);
        }

        /// <summary>
        /// Tab reaches it; Space, Enter or ↓ opens the list, ↑ ↓ (Home End) go along it, Space or Enter chooses (Esc
        /// closes it: the window's). It took the mouse only (D3, 2026-10-19).
        /// </summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || !IsEnabled) return;
            if (!IsOpen)
            {
                if (e.Key is not (System.Windows.Input.Key.Space or System.Windows.Input.Key.Enter or System.Windows.Input.Key.Down or System.Windows.Input.Key.Up)) return;
                e.Handled = true;
                Open();
                Key(Index >= 0 && Index < _keys.Count ? Index : 0);
                return;
            }
            if (!_listBox.IsHitTestVisible) { e.Handled = true; return; } // a row chosen: blinking out
            int n = _keys.Count, to = e.Key switch
            {
                System.Windows.Input.Key.Down => Math.Min(n - 1, _keyed + 1),
                System.Windows.Input.Key.Up => Math.Max(0, _keyed - 1),
                System.Windows.Input.Key.Home => 0,
                System.Windows.Input.Key.End => n - 1,
                _ => -2,
            };
            if (to >= 0) { e.Handled = true; Key(to); return; }
            if (e.Key is System.Windows.Input.Key.Space or System.Windows.Input.Key.Enter && _keyed >= 0 && _keyed < n)
            {
                e.Handled = true;
                _keys[_keyed].Choose();
            }
        }

        void Open()
        {
            int opened = ++_opened;
            Reset(_listBox); // a list fading out from before shows fully again
            _list.Children.Clear();
            _rows.Clear();
            _keys.Clear();
            var text = Materials.Text(_t);
            var accent = new SolidColorBrush(Color.FromRgb(_t.Accent.R, _t.Accent.G, _t.Accent.B));
            for (int i = 0; i < _labels.Count; i++)
            {
                int index = i;
                var tick = Glyphs.Stroke(Glyphs.Check, 12, text, 1.6);
                tick.Visibility = i == Index ? Visibility.Visible : Visibility.Hidden;
                tick.Margin = new Thickness(0, 0, 6, 0);
                var label = new TextBlock { Text = _labels[i], Foreground = text, VerticalAlignment = VerticalAlignment.Center };
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(tick);
                line.Children.Add(label);
                var r = new Border { Child = line, Padding = new Thickness(6, 3, 14, 4), CornerRadius = new CornerRadius(GlassShape.PopupRow), Background = Brushes.Transparent, MinWidth = Math.Max(0, ActualWidth - 8) };
                void Lit() { r.Background = accent; label.Foreground = Brushes.White; tick.Stroke = Brushes.White; }
                void Unlit() { r.Background = Brushes.Transparent; label.Foreground = text; tick.Stroke = text; }
                void Choose() => ChooseRow(_host, _listBox, Lit, Unlit, _t.Animations, () => opened == _opened, () =>
                {
                    if (index == Index) return;
                    Select(index);
                    _chosen(index);
                });
                r.MouseEnter += (_, _) => { Key(-1); Lit(); };
                r.MouseLeave += (_, _) => { if (_listBox.IsHitTestVisible) Unlit(); }; // not while it blinks
                r.MouseLeftButtonUp += (_, e) => { e.Handled = true; Choose(); };
                _rows.Add(r);
                _keys.Add((Lit, Unlit, Choose));
                _list.Children.Add(r);
            }
            _keyed = -1;
            _host.Open(); // on the menus' glass (C3)
            // The chosen row in view (a long list opens where it is, as macOS's does).
            if (Index >= 0 && Index < _rows.Count)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => _rows[Index].BringIntoView());
        }
    }

    /// <summary>An item of a <see cref="MenuButton"/>: its words, a tick, and what it does.</summary>
    public sealed record MenuChoice(string Label, bool Checked, Action Run);

    /// <summary>
    /// A small "…" button that opens a short menu under it (macOS's action button in the Wi-Fi settings' list of known
    /// networks). The items are asked for each time it opens, so their ticks are current.
    /// </summary>
    public sealed class MenuButton : Border
    {
        readonly System.Windows.Controls.Primitives.Popup _popup;
        readonly StackPanel _list = new();
        readonly Func<IReadOnlyList<MenuChoice>> _items;
        readonly ThemeSnapshot _t;
        readonly List<Border> _rows = new();
        readonly Border _listBox;
        readonly GlassList _host;
        int _opened;

        public bool IsOpen => _host.IsOpen;
        /// <summary>The menu's rows while it is open (diagnostics: the specs click them).</summary>
        public IReadOnlyList<Border> Rows => _rows;

        public MenuButton(ThemeSnapshot t, Func<IReadOnlyList<MenuChoice>> items)
        {
            _t = t;
            _items = items;
            var text = Materials.Text(t);
            Width = 26;
            Height = 22;
            CornerRadius = new CornerRadius(6);
            Background = Brushes.Transparent;
            var hover = new SolidColorBrush(t.Dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0));
            MouseEnter += (_, _) => Background = hover;
            MouseLeave += (_, _) => Background = Brushes.Transparent;
            Child = new TextBlock { Text = "…", Foreground = text, FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -4, 0, 0) };
            var list = new Border
            {
                Child = _list, Padding = new Thickness(4), CornerRadius = new CornerRadius(GlassShape.Popup),
                Background = new SolidColorBrush(t.Dark ? Color.FromRgb(0x32, 0x32, 0x34) : Color.FromRgb(0xF6, 0xF6, 0xF6)),
                BorderBrush = new SolidColorBrush(t.Dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0, 0, 0)), BorderThickness = new Thickness(0.5),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.3, Direction = 270 },
                Margin = new Thickness(0, 0, 8, 10), UseLayoutRounding = true, Language = XmlLanguage.GetLanguage("ja-JP"),
            };
            TextElement.SetFontFamily(list, Materials.UiFont);
            TextElement.SetFontSize(list, 13);
            _listBox = list;
            _popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
                VerticalOffset = 2, Child = list,
            };
            _host = new GlassList(this, list, t, _popup);
            MouseLeftButtonDown += (_, e) => e.Handled = true;
            MouseLeftButtonUp += (_, e) => { e.Handled = true; Open(); };
        }

        public void CloseList() => _host.Close();

        void Open()
        {
            int opened = ++_opened;
            Reset(_listBox); // a menu fading out from before shows fully again
            _list.Children.Clear();
            _rows.Clear();
            var text = Materials.Text(_t);
            var accent = new SolidColorBrush(Color.FromRgb(_t.Accent.R, _t.Accent.G, _t.Accent.B));
            foreach (var item in _items())
            {
                var tick = Glyphs.Stroke(Glyphs.Check, 12, text, 1.6);
                tick.Visibility = item.Checked ? Visibility.Visible : Visibility.Hidden;
                tick.Margin = new Thickness(0, 0, 6, 0);
                var label = new TextBlock { Text = item.Label, Foreground = text, VerticalAlignment = VerticalAlignment.Center };
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(tick);
                line.Children.Add(label);
                var r = new Border { Child = line, Padding = new Thickness(6, 3, 14, 4), CornerRadius = new CornerRadius(GlassShape.PopupRow), Background = Brushes.Transparent };
                void Lit() { r.Background = accent; label.Foreground = Brushes.White; tick.Stroke = Brushes.White; }
                void Unlit() { r.Background = Brushes.Transparent; label.Foreground = text; tick.Stroke = text; }
                r.MouseEnter += (_, _) => Lit();
                r.MouseLeave += (_, _) => { if (_listBox.IsHitTestVisible) Unlit(); }; // not while it blinks
                var run = item.Run;
                r.MouseLeftButtonUp += (_, e) => { e.Handled = true; ChooseRow(_host, _listBox, Lit, Unlit, _t.Animations, () => opened == _opened, run); };
                _rows.Add(r);
                _list.Children.Add(r);
            }
            _host.Open(); // on the menus' glass (C3)
        }
    }

    /// <summary>Gives a text or password box our look.</summary>
    public static T Field<T>(T box, ThemeSnapshot t) where T : Control
    {
        var accent = Color.FromRgb(t.Accent.R, t.Accent.G, t.Accent.B);
        box.Template = FieldTemplate(typeof(T).Name, Color.FromArgb(0x80, accent.R, accent.G, accent.B));
        box.Background = new SolidColorBrush(t.Dark ? Color.FromArgb(0x4D, 0, 0, 0) : Colors.White);
        box.BorderBrush = new SolidColorBrush(t.Dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x33, 0, 0, 0));
        box.Foreground = Materials.Text(t);
        box.Padding = new Thickness(6, 3, 6, 4);
        box.FontSize = 13;
        box.FocusVisualStyle = null;
        var selection = new SolidColorBrush(accent);
        switch (box)
        {
            case PasswordBox p: p.CaretBrush = Materials.Text(t); p.SelectionBrush = selection; break;
            case TextBox x: x.CaretBrush = Materials.Text(t); x.SelectionBrush = selection; break;
        }
        return box;
    }
}
