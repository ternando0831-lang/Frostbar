using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Frostbar.App.Common;
using Frostbar.App.Menus;
using Frostbar.Core.Localization;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Sys;
using Windows.Devices.Radios;
using Path = System.Windows.Shapes.Path;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.ControlCenter;

/// <summary>What the Control Center shows and changes (services that live as long as the shell).</summary>
/// <param name="Privacy">What uses the microphone and the camera as the panel opens (stage 5-2); the menu bar tells it of changes.</param>
public sealed record ControlCenterServices(AudioService Audio, NetworkMonitor Network, RadioService Radios, BrightnessService Brightness,
    MediaService Media, WifiService Wifi, BluetoothService Bluetooth, Func<string, string> AppName, Func<string, Task> AskWifiPassword,
    FocusServices Focus, Action<string>? OpenSettings = null, Frostbar.Core.MenuBar.PrivacyState? Privacy = null, Action? VolumeLetGo = null);

/// <summary>
/// The Control Center (M6, macOS): under its menu bar item, like a menu it never takes the foreground and closes on an
/// outside click or Esc. Tiles: Wi-Fi and Bluetooth (switch, and the network's name), Focus (Do Not Disturb and focus
/// sessions, in a view of its own), Screen Mirroring (Windows' Cast: there is no API to cast the screen), the display (brightness, dark mode),
/// sound (volume, the output device), and what plays (play/pause, previous, next). It follows changes made elsewhere
/// while it is open.
/// </summary>
public sealed class ControlCenterPanel : PanelPopup
{
    // macOS 27's grid (GlassShape): four 60-DIP cells 12 apart, and the window's margin of 10 round them.
    const double Cell = GlassShape.CcCell, Gap = GlassShape.CcGap;
    public const double PanelWidth = 4 * GlassShape.CcCell + 3 * GlassShape.CcGap + 20;

    /// <summary>A round switch, the line under its title, and the row holding both.</summary>
    public sealed record Toggle(Border Circle, TextBlock Subtitle, FrameworkElement Row);

    readonly ThemeSnapshot _t;
    readonly Strings _s;
    readonly ControlCenterServices _sv;
    readonly Action<string> _chord;
    readonly Action _close;
    readonly Brush _circleOff, _accent;
    readonly Border _displayModule;
    bool _hasMedia;
    readonly Toggle _wifi, _bluetooth, _dark;
    readonly Border _focusCircle;
    /// <summary>The Focus tile (diagnostics: the specs click it).</summary>
    public Border FocusTile { get; }
    readonly PillSlider _brightness, _volume;
    readonly Border _media;
    Border _artBox = null!, _playPause = null!, _previous = null!, _next = null!;
    readonly Image _art = new() { Width = 44, Height = 44, Stretch = Stretch.UniformToFill };
    readonly TextBlock _mediaTitle = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _mediaArtist = new() { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Action _unsubscribe;

    public PillSlider BrightnessSlider => _brightness;
    public PillSlider VolumeSlider => _volume;
    public Toggle Wifi => _wifi;
    public Toggle Bluetooth => _bluetooth;
    public Toggle DarkMode => _dark;
    public Border PlayPauseButton => _playPause;
    public bool ShowsMedia => _hasMedia;
    public string MediaTitle => _mediaTitle.Text;
    public bool ShowsPause => (_playPause.Child as FrameworkElement)?.Tag as string == "pause";

    /// <param name="setDark">Switches dark mode as the shell shows it: Frostbar's own mode when Settings → Appearance fixes one, else Windows'.</param>
    /// <param name="pieces">The glass of each control (macOS 27: no panel under them; Materials.Piece).</param>
    public ControlCenterPanel(ThemeSnapshot t, Material m, Material pieces, Strings s, ControlCenterServices services, Action<string> chord, Action close, Action<bool>? setDark = null) : base(t, m)
    {
        _t = t;
        _s = s;
        _sv = services;
        _chord = chord;
        _close = close;
        _setDark = setDark;
        _circleOff = Freeze(new SolidColorBrush(t.Dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0)));
        _accent = Freeze(new SolidColorBrush(Color.FromRgb(t.Accent.R, t.Accent.G, t.Accent.B)));
        // The controls 10 DIP in from the window's edges; a view's glass reaches the edges, its rows 10 DIP inside (ShowView).
        Body.Margin = new Thickness(0);
        Body.MinWidth = 0;
        Body.Width = PanelWidth;
        _main.Margin = new Thickness(10);

        // What uses the microphone and the camera, first (macOS's Control Center names them at its top): a capsule.
        _privacyTile = Module(_privacyLines);
        _privacyTile.Padding = new Thickness(14, 10, 14, 10);
        _privacyTile.Margin = new Thickness(0, 0, 0, Gap);
        _main.Children.Add(_privacyTile);
        ShowPrivacy(services.Privacy ?? Frostbar.Core.MenuBar.PrivacyState.None);

        // macOS 27's grid (the user's "A", 2026-10-18; `Lab mock cc`): no panel; each control its own glass. Wi-Fi and
        // Bluetooth capsules (the circle switches, the words open the view: Bluetooth's was a circle with its view on a
        // right click, which the user could not find, 2026-10-18), Now Playing two cells square, then Focus, dark mode,
        // Screen Mirroring and Windows' other quick settings circles; then Display and Sound, wide.
        var grid = new Grid();
        foreach (var w in new[] { Cell, Gap, Cell, Gap, Cell, Gap, Cell }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        foreach (var h in new[] { Cell, Gap, Cell, Gap, Cell }) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(h) });
        void Put(FrameworkElement e, int col, int row, int cols = 1, int rows = 1)
        {
            Grid.SetColumn(e, col * 2);
            Grid.SetRow(e, row * 2);
            Grid.SetColumnSpan(e, cols * 2 - 1);
            Grid.SetRowSpan(e, rows * 2 - 1);
            grid.Children.Add(e);
        }
        _wifi = CapsuleToggle([(Glyphs.WifiArcs[0], false), (Glyphs.WifiArcs[1], true), (Glyphs.WifiArcs[2], true), (Glyphs.WifiArcs[3], true)],
            s["cc.wifi"], () => Switch(RadioKind.WiFi), () => ShowView(new WifiView(Kit(), _sv.Wifi, _sv.Radios, _close, ShowMain, _sv.AskWifiPassword), _wifi!.Row), out var wifi);
        Put(wifi, 0, 0, 2);
        _bluetooth = CapsuleToggle([(Glyphs.Bluetooth, true)], s["cc.bluetooth"], () => Switch(RadioKind.Bluetooth),
            () => ShowView(new BluetoothView(Kit(), _sv.Bluetooth, _sv.Radios, _close, ShowMain), _bluetooth!.Row), out var bluetooth);
        Put(bluetooth, 0, 1, 2);
        _media = MediaModule();
        Put(_media, 2, 0, 2, 2);
        // Focus a circle that opens its view (white while Do Not Disturb or a session is on), its name its tooltip.
        var focus = _focusCircle = CircleButton(Glyphs.Moon, stroke: false, s["cc.focus"], () => ShowView(new FocusView(Kit(), _sv.Focus, _close, ShowMain), _focusCircle!));
        FocusTile = focus;
        Put(focus, 0, 2);
        _dark = CircleToggle([(Glyphs.Contrast, true), (Glyphs.ContrastHalf, false)], s["cc.darkMode"], ToggleDark);
        Put(_dark.Circle, 1, 2);
        Put(CircleButton(Glyphs.Mirroring, stroke: true, s["cc.mirroring"], () => { _close(); _chord("Win+K"); }), 2, 2);
        Put(CircleButton(Dots, stroke: false, s["cc.more"], () => { _close(); _chord("Win+A"); }), 3, 2);
        _main.Children.Add(grid);

        // Display (brightness) and Sound (volume, the output): wide modules, knobless sliders between a small and a large glyph.
        _brightness = new PillSlider(Glyphs.Sun, strokeGlyph: true) { Knobless = true };
        _brightness.ValueChanged += v => _sv.Brightness.Set((int)Math.Round(v * 100));
        _displayModule = SliderModule(s["cc.display"], _brightness, Glyphs.Stroke(Glyphs.Sun, 12, Text, 1.4), Glyphs.Stroke(Glyphs.Sun, 17, Text, 1.5), null);
        _main.Children.Add(_displayModule);
        _volume = new PillSlider(Glyphs.Speaker) { Knobless = true };
        _volume.ValueChanged += v => _sv.Audio.SetVolume((float)v);
        _volume.LetGo += _ => _sv.VolumeLetGo?.Invoke(); // the volume's feedback (the settings' sound effects)
        var output = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = _circleOff, Margin = new Thickness(10, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Arrow };
        var outputPicture = new Grid { Width = 16, Height = 16 };
        outputPicture.Children.Add(Glyphs.Fill(Glyphs.Speaker, 16, Text));
        foreach (var wave in Glyphs.SpeakerWaves.Take(2)) outputPicture.Children.Add(Glyphs.Stroke(wave, 16, Text, 1.5));
        outputPicture.LayoutTransform = new ScaleTransform(0.8, 0.8);
        output.Child = outputPicture;
        output.ToolTip = s["cc.output"];
        output.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowView(new SoundView(Kit(), _sv.Audio, _close, ShowMain), output); };
        KeyNav.SetRun(output, () => ShowView(new SoundView(Kit(), _sv.Audio, _close, ShowMain), output)); // D2
        OutputLink = output;
        var loud = new Grid { Width = 17, Height = 17 };
        loud.Children.Add(Glyphs.Fill(Glyphs.Speaker, 17, Text));
        foreach (var wave in Glyphs.SpeakerWaves) loud.Children.Add(Glyphs.Stroke(wave, 17, Text, 1.5));
        _main.Children.Add(SliderModule(s["cc.sound"], _volume, Glyphs.Fill(Glyphs.Speaker, 12, Text), loud, output));
        _main.VerticalAlignment = VerticalAlignment.Top;
        _stage.Children.Add(_main);
        Body.Children.Add(_stage);

        var track = Freeze(new SolidColorBrush(t.Dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0, 0, 0)));
        var fill = Freeze(new SolidColorBrush(t.Dark ? Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF) : Colors.White));
        var glyph = Freeze(new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70)));
        var shadow = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)));
        _brightness.SetColors(track, fill, glyph, shadow);
        _volume.SetColors(track, fill, glyph, shadow);

        Action radios2 = ShowRadios, brightness = ShowBrightness, volume = ShowVolume, playing = ShowMedia;
        _sv.Radios.Changed += radios2;
        _sv.Network.Changed += radios2;
        _sv.Brightness.Changed += brightness;
        _sv.Audio.Changed += volume;
        _sv.Media.Changed += playing;
        // The services live as long as the shell: a panel that stayed subscribed would never be collected.
        _unsubscribe = () =>
        {
            _sv.Radios.Changed -= radios2;
            _sv.Network.Changed -= radios2;
            _sv.Brightness.Changed -= brightness;
            _sv.Audio.Changed -= volume;
            _sv.Media.Changed -= playing;
        };
        ShowRadios();
        ShowBrightness();
        ShowVolume();
        ShowMedia();
        // After the first frame: asking Windows (a COM object, the focus session manager) must not slow the opening.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, ShowFocus);
        Paint(_dark.Circle, t.ChosenDark);
        UsePieces(Pieces, pieces);
    }

    readonly Border _privacyTile;
    readonly StackPanel _privacyLines = new();

    /// <summary>The privacy tile's lines (diagnostics): the device, then its apps.</summary>
    public IReadOnlyList<string> PrivacyLines { get; private set; } = [];

    /// <summary>
    /// The devices in use at the top, each a coloured circle with its picture (macOS's orange and green), its name and
    /// its apps; the tile goes when none is in use.
    /// </summary>
    public void ShowPrivacy(Frostbar.Core.MenuBar.PrivacyState state)
    {
        _privacyLines.Children.Clear();
        var lines = state.Lines();
        _privacyTile.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var text = new List<string>();
        foreach (var (kind, apps) in lines)
        {
            bool camera = kind == Frostbar.Core.MenuBar.PrivacyKind.Camera;
            var color = camera ? (_t.Dark ? Color.FromRgb(0x30, 0xD1, 0x58) : Color.FromRgb(0x34, 0xC7, 0x59))
                               : (_t.Dark ? Color.FromRgb(0xFF, 0x9F, 0x0A) : Color.FromRgb(0xFF, 0x95, 0x00));
            FrameworkElement picture = camera ? Glyphs.Fill(Glyphs.Camera, 14, Brushes.White) : Glyphs.Stroke(Glyphs.Microphone, 14, Brushes.White, 1.5);
            picture.HorizontalAlignment = HorizontalAlignment.Center;
            picture.VerticalAlignment = VerticalAlignment.Center;
            var circle = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(color), Child = picture, Margin = new Thickness(0, 0, 8, 0) };
            string device = Frostbar.Core.MenuBar.PrivacyState.Device(kind, _s), who = string.Join(_s["privacy.and"], apps);
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, text.Count > 0 ? 8 : 0, 0, 0) };
            DockPanel.SetDock(circle, System.Windows.Controls.Dock.Left);
            row.Children.Add(circle);
            var words = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new TextBlock { Text = device, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) });
            words.Children.Add(new TextBlock { Text = who, FontSize = 12, Foreground = Dim, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(words);
            _privacyLines.Children.Add(row);
            text.Add($"{device} {who}");
        }
        PrivacyLines = text;
    }

    /// <summary>The Focus tile lights up while Do Not Disturb or a focus session is on (read when shown: Windows tells no one).</summary>
    void ShowFocus() => Paint(_focusCircle, _sv.Focus.DoNotDisturb() == true || _sv.Focus.SessionActive());

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    readonly StackPanel _main = new();

    /// <summary>The view open inside the panel instead of the tiles (Wi-Fi, Bluetooth, Sound), or null (diagnostics).</summary>
    public PanelView? CurrentView { get; private set; }

    /// <summary>"Output ›" in the sound tile (diagnostics: tests click it).</summary>
    public FrameworkElement OutputLink { get; }

    ViewKit Kit() => new(_t, _s) { OpenSettings = _sv.OpenSettings, RowRadius = GlassShape.CcViewRow };

    public override bool FadesIn => true;

    /// <summary>
    /// A tile's details in place of the tiles, with a back arrow (macOS opens them inside the Control Center): the glass
    /// of the part pressed (<paramref name="from"/>) grows into the view's (macOS 27's morph, the user's "C", 2026-10-18;
    /// it slid in sideways before).
    /// </summary>
    /// <summary>Which view is open ("wifi", "bluetooth", "focus", "sound"), or null: the tiles.</summary>
    public string? CurrentViewKey => CurrentView switch
    {
        WifiView => "wifi",
        BluetoothView => "bluetooth",
        FocusView => "focus",
        SoundView => "sound",
        _ => null,
    };

    /// <summary>
    /// Opens the view <paramref name="key"/> at once, no morph (this panel takes the place of one in other colours with
    /// that view open: MenuController.SwapPanel). Back from it, its glass goes back into its part as usual.
    /// </summary>
    public void OpenViewAtOnce(string? key)
    {
        switch (key)
        {
            case "wifi": ShowView(new WifiView(Kit(), _sv.Wifi, _sv.Radios, _close, ShowMain, _sv.AskWifiPassword), _wifi.Row, atOnce: true); break;
            case "bluetooth": ShowView(new BluetoothView(Kit(), _sv.Bluetooth, _sv.Radios, _close, ShowMain), _bluetooth.Row, atOnce: true); break;
            case "focus": ShowView(new FocusView(Kit(), _sv.Focus, _close, ShowMain), _focusCircle!, atOnce: true); break;
            case "sound": ShowView(new SoundView(Kit(), _sv.Audio, _close, ShowMain), OutputLink, atOnce: true); break;
        }
    }

    void ShowView(PanelView view, FrameworkElement from, bool atOnce = false)
    {
        // The parts' glass stays as it is until the glass growing into the view is set up (HoldPieces).
        HoldPieces = true;
        try
        {
            if (_morphing) EndMorph(_legTo == 1); // another part pressed while one morphs: that one ends where it was going
            if (_viewFrame != null) { _stage.Children.Remove(_viewFrame); _viewFrame = null; }
            CurrentView?.Detach();
            view.Width = PanelWidth - 20;
            // One piece of glass the window's width, the view 10 DIP inside it (as on the panel before macOS 27's form):
            // the view itself as the glass put its rows and its back arrow on the glass's rim (2026-10-18, the user's "並び
            // や文字が切れる").
            _viewFrame = new Border { Child = view, Padding = new Thickness(GlassShape.CcViewInset), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Top };
            _stage.Children.Add(_viewFrame);
            CurrentView = view;
            if (!Animate || atOnce)
            {
                _main.Visibility = Visibility.Collapsed;
                _morphFrom = from; // back, its glass goes into this part
                return;
            }
            BeginMorph(from, toView: true);
        }
        finally
        {
            HoldPieces = false;
            PlacePieces();
        }
    }

    Border? _viewFrame;
    readonly Grid _stage = new();

    void ShowMain()
    {
        // The view's glass stays as it is until the glass going back into the part is set up (HoldPieces): the tiles
        // shown again came in whole for a layout pass first.
        HoldPieces = true;
        try
        {
            CurrentView?.Detach();
            CurrentView = null;
            ShowFocus();
            if (_viewFrame == null) return;
            _main.Visibility = Visibility.Visible;
            if (!Animate || _morphFrom == null)
            {
                _stage.Children.Remove(_viewFrame);
                _viewFrame = null;
                return;
            }
            // Back: the view's glass goes back into the part it came out of; the tiles take clicks at once (the view no more).
            _viewFrame.IsHitTestVisible = false;
            BeginMorph(_morphFrom, toView: false);
        }
        finally
        {
            HoldPieces = false;
            PlacePieces();
        }
    }

    // ---- macOS 27's morph: a part's glass grows into its view's and back (the user's "C", 2026-10-18) ---------------

    /// <summary>
    /// How long the glass takes to grow into the view, and back, on a spring (Core's DockMotion.Spring, <see
    /// cref="MorphDamping"/>: half the way in a quarter of the time, settled by three quarters; going back it shrinks a
    /// little past the part and settles). It went both ends smooth in 260 ms first, and moved stiffly (the user's "なんか
    /// 動きが硬い", 2026-10-18).
    /// </summary>
    public const double MorphMs = Motion.Morph, MorphDamping = 0.8;

    static readonly DependencyProperty MorphProperty = DependencyProperty.Register("Morph", typeof(double), typeof(ControlCenterPanel),
        new PropertyMetadata(0.0, (d, e) => ((ControlCenterPanel)d).ApplyMorph((double)e.NewValue)));

    /// <summary>How much of this leg's time has gone (0–1, set at each frame by Tween).</summary>
    double Morph => (double)GetValue(MorphProperty);

    bool _morphing;
    FrameworkElement? _morphFrom;
    Border? _morphGlass;
    Rect _morphA, _morphB, _morphRoom;
    double _morphRadius;
    /// <summary>Where the glass is between the part (0) and the view (1), past either on the spring; where this leg began and where it goes.</summary>
    double _shape, _legFrom, _legTo;
    /// <summary>Reduced motion (accessibility): this leg crossfades under the view's glass instead of growing.</summary>
    bool _morphCalm;

    /// <summary>A morph under way (diagnostics).</summary>
    public bool Morphing => _morphing;

    /// <summary>The growing glass (diagnostics: its rectangle as it goes), or null.</summary>
    public FrameworkElement? MorphGlass => _morphing ? _morphGlass : null;

    /// <summary>
    /// Starts the glass of <paramref name="from"/> growing into the view's (or the view's going back into it): one
    /// stand-in, sized each frame between the two rectangles, is the only glass of the two meanwhile; the other parts
    /// fade, the part's own content fades in the first half, the view's in the second.
    /// </summary>
    void BeginMorph(FrameworkElement from, bool toView)
    {
        _morphFrom = from;
        _stage.UpdateLayout();
        if (_viewFrame == null) return;
        _morphA = from.TransformToAncestor(_stage).TransformBounds(new Rect(0, 0, from.ActualWidth, from.ActualHeight));
        _morphB = new Rect(0, 0, _viewFrame.ActualWidth, _viewFrame.ActualHeight);
        _morphRadius = from is Border b ? b.CornerRadius.TopLeft : Math.Min(_morphA.Width, _morphA.Height) / 2;
        // The glass keeps inside the stage as it is now (the tiles and the view): the view's glass is the window's size,
        // and a stand-in past it would make the window grow and shrink with the spring.
        if (_morphGlass == null)
        {
            _morphRoom = new Rect(0, 0, _stage.ActualWidth, _stage.ActualHeight);
            _morphGlass = new Border { Background = Brushes.Transparent, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            _stage.Children.Add(_morphGlass);
        }
        // A reversal goes on from where the glass is, on a spring of its own.
        _legFrom = _morphing ? _shape : toView ? 0 : 1;
        _legTo = toView ? 1 : 0;
        _morphCalm = ReduceMotion.Calms("morph");
        _morphing = true;
        Tween.Stop(this, MorphProperty);
        SetValue(MorphProperty, 0.0);
        ApplyMorph(0);
        // Laid out before the pieces are placed: not yet, the stand-in had no size and the part's glass was gone for a
        // frame (2026-10-18, cc-motion: its first reading 0 px wide).
        _stage.UpdateLayout();
        PlacePieces();
        Tween.To(this, MorphProperty, 1.0, MorphMs, Tween.Linear, done: () => EndMorph(toView));
    }

    static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    void ApplyMorph(double q)
    {
        if (!_morphing || _morphGlass == null || _morphFrom == null) return;
        if (_morphCalm) { Crossfade(q); return; }
        double s = _shape = _legFrom + (_legTo - _legFrom) * Frostbar.Core.Dock.DockMotion.Spring(q, MorphDamping);
        double l = _morphA.Left + (_morphB.Left - _morphA.Left) * s, t = _morphA.Top + (_morphB.Top - _morphA.Top) * s;
        double w = _morphA.Width + (_morphB.Width - _morphA.Width) * s, h = _morphA.Height + (_morphB.Height - _morphA.Height) * s;
        // Past the view it stays in the stage (its spring there not drawn: the window's edge); past the part, inside it.
        double left = Math.Max(_morphRoom.Left, l), top = Math.Max(_morphRoom.Top, t);
        var r = new Rect(left, top, Math.Max(1, Math.Min(_morphRoom.Right, l + w) - left), Math.Max(1, Math.Min(_morphRoom.Bottom, t + h) - top));
        // Both ends on whole pixels and the size between them: under the layout rounding WPF rounded the margin and
        // the width each its own way, and the far edge went in and out by a pixel as the glass grew (one of the 1-px
        // spots found on 2026-10-23). The view's clip takes the same rectangle.
        var dpi = VisualTreeHelper.GetDpi(_morphGlass);
        static double Snap(double v, double k) => Math.Floor(v * k + 0.5) / k;
        double x0 = Snap(r.Left, dpi.DpiScaleX), x1 = Snap(r.Right, dpi.DpiScaleX), y0 = Snap(r.Top, dpi.DpiScaleY), y1 = Snap(r.Bottom, dpi.DpiScaleY);
        r = new Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
        _morphGlass.Margin = new Thickness(r.X, r.Y, 0, 0);
        _morphGlass.Width = r.Width;
        _morphGlass.Height = r.Height;
        // The others go as the glass grows past them, the part's own content first; the view's comes in as it fills.
        double others = 1 - Clamp01(s * 1.6), own = 1 - Clamp01(s * 2.5);
        foreach (var (e, _) in _modules) e.Opacity = ReferenceEquals(e, _morphFrom) ? own : others;
        if (!_modules.Any(m => ReferenceEquals(m.e, _morphFrom))) _morphFrom.Opacity = own; // a part inside a module (the output)
        if (_viewFrame?.Child is UIElement view) view.Opacity = Clamp01((s - 0.45) / 0.4);
        // The view shows only inside the glass growing round it: its rows stood out below the glass on their way
        // (2026-10-18, cc-motion's film). The frame stands at the stage's corner: the same coordinates.
        if (_viewFrame != null)
        {
            double radius = MorphRadius(r);
            _viewFrame.Clip = new RectangleGeometry(r, radius, radius);
        }
    }

    /// <summary>
    /// Reduced motion's leg: the glass stands at the view's size all along (nothing grows or shrinks), the parts fade
    /// out as the view fades in, or the other way, linearly.
    /// </summary>
    void Crossfade(double q)
    {
        double f = _shape = _legFrom + (_legTo - _legFrom) * q;
        var r = new Rect(_morphB.Left, _morphB.Top, Math.Max(1, Math.Min(_morphRoom.Width, _morphB.Width)), Math.Max(1, Math.Min(_morphRoom.Height, _morphB.Height)));
        _morphGlass!.Margin = new Thickness(r.X, r.Y, 0, 0);
        _morphGlass.Width = r.Width;
        _morphGlass.Height = r.Height;
        foreach (var (e, _) in _modules) e.Opacity = 1 - f;
        if (!_modules.Any(m => ReferenceEquals(m.e, _morphFrom))) _morphFrom!.Opacity = 1 - f;
        if (_viewFrame?.Child is UIElement view) view.Opacity = f;
        if (_viewFrame != null) _viewFrame.Clip = null;
    }

    /// <summary>The growing glass's corners: the part's to the view's as it goes, never more than half its height.</summary>
    double MorphRadius(Rect r) => Math.Max(0, Math.Min(_morphRadius + (GlassShape.LargePanel - _morphRadius) * (_morphCalm ? 1 : Clamp01(_shape)), Math.Min(r.Width, r.Height) / 2));

    void EndMorph(bool toView)
    {
        if (!_morphing) return;
        Tween.Stop(this, MorphProperty);
        _morphing = false;
        if (_morphGlass != null) { _stage.Children.Remove(_morphGlass); _morphGlass = null; }
        foreach (var (e, _) in _modules) e.Opacity = 1;
        if (_morphFrom != null) _morphFrom.Opacity = 1;
        if (_viewFrame?.Child is UIElement view) view.Opacity = 1;
        if (_viewFrame != null) _viewFrame.Clip = null;
        if (toView) _main.Visibility = Visibility.Collapsed;
        else if (_viewFrame != null)
        {
            _stage.Children.Remove(_viewFrame);
            _viewFrame = null;
        }
        PlacePieces();
    }

    // ---- macOS 27's controls: each its own piece of glass (the window draws only what is on it) -----------------------

    /// <summary>The parts that are glass: the controls, or the view open in their place (one piece, as a panel).</summary>
    IEnumerable<Piece> Pieces()
    {
        if (_morphing && _morphGlass != null)
        {
            // The parts fading, and the one glass growing between the part pressed and the view (last: over them).
            foreach (var (e, r) in _modules) if (!ReferenceEquals(e, _morphFrom)) yield return new Piece(e, r);
            yield return new Piece(_morphGlass, MorphRadius(new Rect(0, 0, _morphGlass.Width, _morphGlass.Height)));
            yield break;
        }
        if (CurrentView != null && _viewFrame != null) { yield return new Piece(_viewFrame, GlassShape.LargePanel); yield break; }
        foreach (var (e, r) in _modules) yield return new Piece(e, r);
    }

    readonly List<(FrameworkElement e, double radius)> _modules = new();

    /// <summary>A control's own shape: transparent (its glass is under it), rounded as given, remembered as a piece.</summary>
    Border Module(UIElement? child, double radius = GlassShape.Module)
    {
        var b = new Border { Child = child, Background = Brushes.Transparent, CornerRadius = new CornerRadius(radius) };
        _modules.Add((b, radius));
        return b;
    }

    static readonly Geometry Dots = Freeze(new GeometryGroup
    {
        Children = { new EllipseGeometry(new Point(3.5, 8), 1.5, 1.5), new EllipseGeometry(new Point(8, 8), 1.5, 1.5), new EllipseGeometry(new Point(12.5, 8), 1.5, 1.5) },
    });

    FrameworkElement Picture((Geometry g, bool stroke)[] glyph, double size)
    {
        var picture = new Grid { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (g, stroke) in glyph) picture.Children.Add(stroke ? Glyphs.Stroke(g, 16, Text, 1.5) : Glyphs.Fill(g, 16, Text));
        picture.LayoutTransform = new ScaleTransform(size / 16, size / 16);
        return picture;
    }

    /// <summary>
    /// A capsule two cells wide: a 40-DIP circle that switches (white with the accent's glyph when on), the title and a
    /// line under it that open the view (Wi-Fi).
    /// </summary>
    Toggle CapsuleToggle((Geometry g, bool stroke)[] glyph, string title, Action toggle, Action open, out Border capsule)
    {
        var circle = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = _circleOff, Child = Picture(glyph, 17), Margin = new Thickness(10, 0, 10, 0) };
        circle.MouseLeftButtonUp += (_, e) => { toggle(); e.Handled = true; };
        KeyNav.SetRun(circle, toggle); // D2
        // Two lines at most, broken between words only (a long network name is cut on its line: it broke inside it), then
        // cut (some 60 DIPs across: "Not Connected" was cut on one line: C6).
        var subtitle = new TextBlock { FontSize = 11.5, Foreground = Dim, TextWrapping = TextWrapping.WrapWithOverflow, TextTrimming = TextTrimming.CharacterEllipsis,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = 15, MaxHeight = 30 };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Margin = new Thickness(0, 0, 12, 0) };
        words.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        words.Children.Add(subtitle);
        words.MouseLeftButtonUp += (_, _) => open();
        KeyNav.SetRun(words, open); // D2: the words open the view
        System.Windows.Automation.AutomationProperties.SetName(circle, title); // D4
        System.Windows.Automation.AutomationProperties.SetName(words, title);
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(circle, System.Windows.Controls.Dock.Left);
        row.Children.Add(circle);
        row.Children.Add(words);
        capsule = Module(row, Cell / 2);
        Bouncy(capsule);
        return new Toggle(circle, subtitle, capsule);
    }

    readonly List<FrameworkElement> _bouncy = new();

    /// <summary>The parts that give under a press and spring back (diagnostics).</summary>
    public IReadOnlyList<FrameworkElement> BouncyParts => _bouncy;

    /// <summary>A button of glass that gives under a press and springs back (macOS 27, the user's "A"; GlassBounce).</summary>
    void Bouncy(FrameworkElement e)
    {
        GlassBounce.Attach(e, () => Animate);
        _bouncy.Add(e);
    }

    /// <summary>
    /// A circle a cell wide that switches: white with the accent's glyph when on, the glass with the text's glyph when
    /// off (dark mode). Its title is its tooltip. A switch with a view of its own is a capsule, its words opening the
    /// view (CapsuleToggle): Bluetooth's view on a right click of its circle went unfound (2026-10-18).
    /// </summary>
    Toggle CircleToggle((Geometry g, bool stroke)[] glyph, string title, Action toggle)
    {
        var circle = Module(Picture(glyph, 20), Cell / 2);
        circle.Width = circle.Height = Cell;
        circle.Background = new SolidColorBrush(Clear);
        circle.Tag = false;
        circle.MouseLeftButtonUp += (_, e) => { toggle(); e.Handled = true; };
        KeyNav.SetRun(circle, toggle); // D2
        circle.ToolTip = title;
        System.Windows.Automation.AutomationProperties.SetName(circle, title); // D4
        Bouncy(circle);
        var subtitle = new TextBlock { Visibility = Visibility.Collapsed }; // its state in words (diagnostics): no room on a circle
        return new Toggle(circle, subtitle, circle);
    }

    /// <summary>A circle a cell wide that does something (Focus, Screen Mirroring, Windows' other quick settings); its title is its tooltip.</summary>
    Border CircleButton(Geometry glyph, bool stroke, string title, Action click)
    {
        var circle = Module(Picture([(glyph, stroke)], 20), Cell / 2);
        circle.Width = circle.Height = Cell;
        circle.ToolTip = title;
        circle.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        KeyNav.SetRun(circle, click); // D2
        System.Windows.Automation.AutomationProperties.SetName(circle, title); // D4
        Bouncy(circle);
        return circle;
    }

    /// <summary>Now Playing, two cells square: the cover, the title and artist, and previous, play/pause, next under them.</summary>
    Border MediaModule()
    {
        _artBox = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(10), Background = _circleOff, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left };
        _art.Width = _art.Height = 44;
        _mediaArtist.Foreground = Dim;
        _previous = MediaButton(Glyphs.Previous, "previous", () => _ = _sv.Media.PreviousAsync());
        _playPause = MediaButton(Glyphs.Play, "play", () => _ = _sv.Media.PlayPauseAsync());
        _next = MediaButton(Glyphs.Next, "next", () => _ = _sv.Media.NextAsync());
        var buttons = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
        for (int i = 0; i < 3; i++) buttons.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var (b, i) in new[] { (_previous, 0), (_playPause, 1), (_next, 2) }) { Grid.SetColumn(b, i); b.HorizontalAlignment = HorizontalAlignment.Center; buttons.Children.Add(b); }
        var texts = _mediaTexts = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        texts.Children.Add(_mediaTitle);
        texts.Children.Add(_mediaArtist);
        var body = new DockPanel { LastChildFill = false, Margin = new Thickness(14, 14, 14, 8) };
        DockPanel.SetDock(_artBox, System.Windows.Controls.Dock.Top);
        DockPanel.SetDock(texts, System.Windows.Controls.Dock.Top);
        DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);
        body.Children.Add(_artBox);
        body.Children.Add(texts);
        body.Children.Add(buttons);
        return Module(body, GlassShape.Module);
    }

    /// <summary>A wide module: its title, then a knobless slider between a small and a large glyph, and maybe a button at its end (the output).</summary>
    Border SliderModule(string title, PillSlider slider, FrameworkElement small, FrameworkElement large, FrameworkElement? trailing)
    {
        slider.Height = 26;
        slider.VerticalAlignment = VerticalAlignment.Center;
        small.VerticalAlignment = large.VerticalAlignment = VerticalAlignment.Center;
        small.Margin = new Thickness(0, 0, 10, 0);
        large.Margin = new Thickness(10, 0, 0, 0);
        var line = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(small, System.Windows.Controls.Dock.Left);
        line.Children.Add(small);
        if (trailing != null) { DockPanel.SetDock(trailing, System.Windows.Controls.Dock.Right); trailing.VerticalAlignment = VerticalAlignment.Center; line.Children.Add(trailing); }
        DockPanel.SetDock(large, System.Windows.Controls.Dock.Right);
        line.Children.Add(large);
        line.Children.Add(slider);
        var stack = new StackPanel { Margin = new Thickness(16, 12, 14, 0) };
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(line);
        var module = Module(stack, GlassShape.Module);
        module.Height = GlassShape.ModuleHeight;
        module.Margin = new Thickness(0, Gap, 0, 0);
        return module;
    }

    // A circle module's fill when off: almost nothing (its glass shows), enough to take the click.
    static readonly Color Clear = Color.FromArgb(0x01, 0xFF, 0xFF, 0xFF);
    static readonly Color OnWhite = Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF);

    Border MediaButton(Geometry glyph, string tag, Action click)
    {
        var b = new Border { Width = 32, Height = 32, Background = Brushes.Transparent, CornerRadius = new CornerRadius(16) };
        SetGlyph(b, glyph, tag);
        b.MouseEnter += (_, _) => b.Background = _circleOff;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { if (b.Opacity > 0.5) click(); e.Handled = true; };
        KeyNav.SetRun(b, () => { if (b.Opacity > 0.5) click(); }); // D2
        return b;
    }

    void SetGlyph(Border b, Geometry glyph, string tag)
    {
        // The same glyph: kept (the session is read again every change of its state).
        if (b.Child is FrameworkElement was && Equals(was.Tag, tag)) return;
        bool swap = b.Child != null && Animate && IsVisible;
        var p = Glyphs.Fill(glyph, 14, Text);
        p.Tag = tag;
        b.Child = p;
        if (!swap) return;
        // Play turned pause (or back): the new glyph pops in from small, as macOS's symbols change (it swapped at once,
        // 2026-10-19, "B8").
        double small = ReduceMotion.Calms("glyphPop") ? 1 : 0.55; // reduced motion: it fades in at its size
        var scale = new ScaleTransform(small, small);
        p.RenderTransformOrigin = new Point(0.5, 0.5);
        p.RenderTransform = scale;
        p.Opacity = 0;
        Tween.To(scale, 1.0, GlyphPopMs, Tween.Spring(Frostbar.Core.Shell.MotionTable.GlyphPopDamping));
        Tween.To(p, OpacityProperty, 1.0, GlyphPopMs * 0.4);
        GlyphPops++;
    }

    /// <summary>How long a media button's new glyph takes to pop in (ms), and pops since the start (diagnostics).</summary>
    public const double GlyphPopMs = Motion.GlyphPop;
    public int GlyphPops { get; private set; }

    /// <summary>How long a round switch's colours take to change (macOS's: they went at once until 2026-10-17).</summary>
    public const double SwitchMs = Motion.Switch;

    /// <summary>
    /// Diagnostics: the dark mode's round switch drawn on or off, nothing switched (a spec watches its colours change;
    /// the real switches change Windows or the radios). The next look at the real state draws it as it is.
    /// </summary>
    public void PaintDarkSwitch(bool on) => Paint(_dark.Circle, on);

    /// <summary>A part's glass turned (item 4): its round switches painted again in its tone, as they are.</summary>
    protected override void Toned(FrameworkElement part)
    {
        foreach (var circle in new[] { _wifi.Circle, _bluetooth.Circle, _dark.Circle, _focusCircle })
            if (ReferenceEquals(circle, part) || circle.IsDescendantOf(part)) Paint(circle, circle.Tag is true);
    }

    /// <summary>
    /// A round switch on (macOS 27: white, with the accent's glyph: the user's choice, 2026-10-18) or off (a circle on its own glass
    /// shows the glass; one inside a capsule a faint fill). Switched while the panel shows, its colours change over
    /// <see cref="SwitchMs"/>; at once as it is made, or without animations. Its state is in its Tag (diagnostics: the
    /// colours are on their way for a moment).
    /// </summary>
    void Paint(Border circle, bool on)
    {
        bool was = circle.Tag is true;
        circle.Tag = on;
        bool fade = Animate && circle.IsLoaded && IsVisible && was != on;
        Color Of(Brush b) => ((SolidColorBrush)b).Color;
        void To(Brush? current, Color target, Action<Brush> set)
        {
            if (current is SolidColorBrush { IsFrozen: false } b && fade)
            {
                Tween.To(b, target, SwitchMs);
                return;
            }
            // Its own brush, from the colour it has now: it can change over time next.
            var mine = new SolidColorBrush(current is SolidColorBrush c ? c.Color : target);
            set(mine);
            if (fade) Tween.To(mine, target, SwitchMs);
            else mine.Color = target;
        }
        bool own = _modules.Any(x => x.e == circle); // a circle that is a piece of glass itself
        var tone = ToneOf(circle); // its glass turned (item 4): off, its fill and glyph in that tone
        To(circle.Background, on ? OnWhite : own ? Clear : Of(tone?.Fill(_circleOff) ?? _circleOff), b => circle.Background = b);
        var glyph = on ? Of(_accent) : Of(tone?.Ink(Text) ?? Text);
        foreach (var p in Paths(circle.Child))
        {
            if (p.Fill != null) To(p.Fill, glyph, b => p.Fill = b);
            if (p.Stroke != null) To(p.Stroke, glyph, b => p.Stroke = b);
        }
    }

    static IEnumerable<Path> Paths(UIElement? e) => e switch
    {
        Path p => [p],
        Panel panel => panel.Children.OfType<UIElement>().SelectMany(Paths),
        _ => [],
    };

    void ShowRadios()
    {
        var r = _sv.Radios;
        var n = _sv.Network;
        bool wifiOn = r.Wifi == RadioState.On;
        Paint(_wifi.Circle, wifiOn);
        _wifi.Subtitle.Text = !r.HasWifi ? (n.Kind == NetKind.Ethernet ? $"{_s["cc.ethernet"]}: {n.Name}" : _s["cc.unavailableShort"])
            : !wifiOn ? _s["cc.off"]
            : n.Kind == NetKind.Wifi && n.Name.Length > 0 ? n.Name : _s["cc.notConnected"];
        bool btOn = r.Bluetooth == RadioState.On;
        Paint(_bluetooth.Circle, btOn);
        _bluetooth.Subtitle.Text = !r.HasBluetooth ? _s["cc.unavailableShort"] : btOn ? _s["cc.on"] : _s["cc.off"];
    }

    void ShowBrightness()
    {
        bool can = _sv.Brightness.Available;
        _brightness.Visibility = can ? Visibility.Visible : Visibility.Collapsed;
        _displayModule.Visibility = can ? Visibility.Visible : Visibility.Collapsed; // a desktop monitor: nothing to show there
        if (can) _brightness.Glide(_sv.Brightness.Level / 100.0, Animate); // the keys held: it slides as the display does
    }

    void ShowVolume() => _volume.Glide(_sv.Audio.Muted ? 0 : _sv.Audio.Volume, Animate);

    readonly Action<bool>? _setDark;

    /// <summary>
    /// The switch shows the shell's mode, so it changes that one (2026-10-12: with Appearance set to Light or Dark it
    /// wrote Windows' mode instead: nothing changed, or Windows' apps went light while the shell stayed dark).
    /// </summary>
    void ToggleDark()
    {
        bool dark = !_t.ChosenDark;
        Paint(_dark.Circle, dark);
        if (_setDark != null) _setDark(dark);
        else ThemeService.SetDark(dark); // the shell follows through ThemeService.Changed and opens this panel again in the new colours
    }

    void Switch(RadioKind kind)
    {
        // No such adapter: nothing to switch (its circle lit, then went off again when the service said no).
        if (!(kind == RadioKind.WiFi ? _sv.Radios.HasWifi : _sv.Radios.HasBluetooth)) return;
        bool on = (kind == RadioKind.WiFi ? _sv.Radios.Wifi : _sv.Radios.Bluetooth) != RadioState.On;
        Paint(kind == RadioKind.WiFi ? _wifi.Circle : _bluetooth.Circle, on); // at once; the real state follows
        _ = _sv.Radios.SetAsync(kind, on).ContinueWith(_ => Dispatcher.InvokeAsync(ShowRadios));
    }

    void ShowMedia()
    {
        var now = _sv.Media.Current;
        // macOS 27 keeps the module (its place in the grid): with nothing playing it says so, its buttons dimmed.
        _hasMedia = now != null;
        // Another song (or another app's, or none): the words and the artwork fade out and the new ones in (macOS's).
        // They changed at once (2026-10-19, the user's "B8"). The buttons follow at once.
        // The artwork by whether there is one: it is decoded again at every read (each play and pause), a new picture.
        string key = now == null ? "" : now.AppId + "\n" + now.Title + "\n" + now.Artist + "\n" + (now.Art != null);
        bool changed = _mediaKey != null && key != _mediaKey;
        _mediaKey = key;
        if (changed && Animate && IsVisible) CrossfadeMedia(() => ShowMediaWords(now));
        else ShowMediaWords(now);
        SetGlyph(_playPause, now?.Playing == true ? Glyphs.Pause : Glyphs.Play, now?.Playing == true ? "pause" : "play");
        Usable(_playPause, now?.CanPlayPause == true);
        Usable(_previous, now?.CanPrevious == true);
        Usable(_next, now?.CanNext == true);
    }

    /// <summary>A media button dimmed when it can do nothing, and then not lit by a press either (it lit: C4, 2026-10-19).</summary>
    static void Usable(Border b, bool can)
    {
        b.Opacity = can ? 1 : 0.35;
        PressLight.SetOff(b, !can);
    }

    string? _mediaKey;
    StackPanel? _mediaTexts;

    /// <summary>How long the old song's words and artwork take to fade out, and the new ones in (ms).</summary>
    public const double MediaOutMs = Motion.MediaOut, MediaInMs = Motion.MediaIn;

    /// <summary>Songs changed while the panel was open, faded across (diagnostics).</summary>
    public int MediaCrossfades { get; private set; }

    void ShowMediaWords(Frostbar.Platform.Sys.NowPlaying? now)
    {
        if (now == null)
        {
            _mediaTitle.Text = _s["cc.notPlaying"];
            _mediaArtist.Text = "";
            _artBox.Child = Glyphs.Fill(Glyphs.Note, 18, Dim);
            return;
        }
        // A session without a title (some players report none) still shows, under its app's name: it can be paused.
        string app = _sv.AppName(now.AppId);
        _mediaTitle.Text = now.Title.Length > 0 ? now.Title : app;
        _mediaArtist.Text = now.Artist.Length > 0 ? now.Artist : now.Title.Length > 0 ? app : "";
        _art.Source = now.Art;
        _artBox.Child = now.Art != null ? _art : Glyphs.Fill(Glyphs.Note, 18, Dim);
    }

    void CrossfadeMedia(Action change)
    {
        MediaCrossfades++;
        var parts = new UIElement[] { _artBox, _mediaTexts! };
        // Out and in, each as one movement; a change meanwhile takes over from where they are, and the last one shows.
        Tween.Together(MediaOutMs, [.. parts.Select(p => new Tween.Part(p, OpacityProperty, 0.0, Tween.SineInOut))], () =>
        {
            change();
            Tween.Together(MediaInMs, [.. parts.Select(p => new Tween.Part(p, OpacityProperty, 1.0, Tween.Out))]);
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _unsubscribe();
        CurrentView?.Detach();
        base.OnClosed(e);
    }
}
