using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Frostbar.App.Common;
using Frostbar.Core.Shell;
using Frostbar.Platform.Interop;
using Layout = Frostbar.Core.Shell.IslandLayout;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Overlays;

/// <summary>
/// One live activity as the island draws it: which it is (<paramref name="Key"/>: the playing app's id, the progressing
/// app's key), its picture (a record's sleeve, an app's icon), the colour its meter and key line take (from the sleeve),
/// how far it has come (a progress; -1 when it does not know), its words, and whether it plays.
/// </summary>
public sealed record IslandLive(IslandKind Kind, string Key, ImageSource? Picture, Color Tint, double Progress, string Title, string Detail, bool Playing);

/// <summary>What the island shows: the model's shape with its activities and notice made ready to draw.</summary>
public sealed record IslandFace(IslandShape Shape, IslandLive? Main, IslandLive? Second, IslandNotice? Notice);

/// <summary>
/// The Dynamic Island (2026-11-08, the user's "Aでさらに本物に寄せて"; ADR-017), made after Apple's Human Interface
/// Guidelines the same day (the user's "公式資料などを確認しながら", "A 高さ 22"): a black shape hanging at the top's
/// middle in the menu bar, as the OLED MacBook Pro's around its camera, its measures Core's <see cref="Layout"/>. It
/// grows and shrinks between its faces on a spring (its place, width and height one movement, its corners following its
/// height); the picture and the meter of the activity it shows go to their new places in that same movement (the
/// guidelines: existing elements animated to their new positions, not removed and brought back), the words and buttons
/// fading in as it nears its size. Black, not glass: the island is the display's black; over a dark background a key
/// line in the activity's colour goes round it. The window is wider and taller than the island at its largest and
/// transparent around it, so the clicks there go through to what is under it (a layered window's transparent pixels take
/// none); it never takes the foreground.
/// </summary>
public sealed class IslandWindow : Window
{
    /// <summary>The window (DIPs): room for the island opened (371 by 132) and its spring.</summary>
    public const double Wide = 420, High = 176;

    /// <summary>Where the island hangs from: a DIP under the screen's top, within the menu bar's 24.</summary>
    public const double Hang = 1;

    /// <summary>The key line: one DIP, the activity's colour this strong (subtle), white weaker.</summary>
    const byte KeyAlpha = 0x8C, KeyAlphaWhite = 0x4D;

    readonly Canvas _root = new() { Width = Wide, Height = High };
    readonly Border _body = new() { Background = Brushes.Black };
    /// <summary>What the island holds, clipped to its round shape as it changes.</summary>
    readonly Canvas _inside = new();
    /// <summary>The words, buttons and a notice: made again for each face, fading in as the island nears its size.</summary>
    readonly Grid _layer = new();
    /// <summary>The activity's picture and what stands at the trailing end: carried from face to face.</summary>
    readonly Border _art = new() { Opacity = 0 };
    readonly Border _end = new() { Opacity = 0 };
    readonly Border _keyline = new() { BorderThickness = new Thickness(1), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly Border _second = new() { Background = Brushes.Black, Width = Layout.Height, Height = Layout.Height, CornerRadius = new CornerRadius(Layout.Height / 2), Opacity = 0 };
    IslandFace _face = new(IslandShape.Idle, null, null, null);
    IslandPlan _plan = Layout.For(IslandShape.Idle, null, false, null);
    bool _animations = true, _darkBehind, _roundArt;

    static readonly Brush Ink = new SolidColorBrush(Color.FromArgb(0xF2, 255, 255, 255));
    static readonly Brush Dim = new SolidColorBrush(Color.FromArgb(0xA6, 255, 255, 255));
    static readonly Brush Track = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
    static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58), Red = Color.FromRgb(0xFF, 0x45, 0x3A), Purple = Color.FromRgb(0x7D, 0x7A, 0xFF), White = Colors.White;

    /// <summary>The island was clicked (open it, or close it when open).</summary>
    public event Action? Clicked;
    /// <summary>The pointer left the opened island.</summary>
    public event Action? PointerLeft;
    /// <summary>A button of the opened island: "previous", "playpause", "next", "app".</summary>
    public event Action<string>? Pressed;

    /// <summary>The island's size now (DIPs; diagnostics) and where it is on the screen (DIPs).</summary>
    public Size IslandSize => new(_body.Width, _body.Height);
    public Rect IslandRect => new(Left + Canvas.GetLeft(_body), Top + Canvas.GetTop(_body), _body.Width, _body.Height);
    public Rect SecondRect => _second.Opacity > 0 ? new Rect(Left + Canvas.GetLeft(_second), Top + Canvas.GetTop(_second), Layout.Height, Layout.Height) : Rect.Empty;
    /// <summary>The picture where it is on the screen (DIPs) and how much it shows (diagnostics).</summary>
    public Rect ArtRect => new(IslandRect.X + Canvas.GetLeft(_art), IslandRect.Y + Canvas.GetTop(_art), _art.Width, _art.Height);
    public double ArtOpacity => _art.Opacity;
    public bool ArtTakesPointer => _art.IsHitTestVisible;
    /// <summary>The key line shows, and its colour (diagnostics).</summary>
    public bool KeyLineShown => _keyline.Visibility == Visibility.Visible;
    public string? KeyLineColor => (_keyline.BorderBrush as SolidColorBrush)?.Color.ToString();
    /// <summary>Faces the island went to carrying its picture, and with nothing carried (diagnostics).</summary>
    public int Carried { get; private set; }
    public int Faded { get; private set; }
    public IslandShape Shape => _face.Shape;
    public IslandPlan Plan => _plan;
    /// <summary>The island is still taking its shape (its spring, the second circle's fade; diagnostics: read it once it has stopped).</summary>
    public bool Moving => Tween.Moving(_body, WidthProperty) || Tween.Moving(_body, HeightProperty) || Tween.Moving(_body, Canvas.LeftProperty)
        || Tween.Moving(_art, WidthProperty) || Tween.Moving(_end, Canvas.LeftProperty) || Tween.Moving(_second, OpacityProperty);
    /// <summary>The words showing in the island now (diagnostics).</summary>
    public IReadOnlyList<string> Words => Texts(_layer).ToList();
    /// <summary>The buttons of the opened island (diagnostics: a spec clicks them).</summary>
    public IReadOnlyDictionary<string, Rect> Buttons => _buttons.ToDictionary(b => b.Key, b => ScreenRect(b.Value));
    readonly Dictionary<string, FrameworkElement> _buttons = new();

    public IslandWindow()
    {
        Title = "Frostbar.Island";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        UseLayoutRounding = true;
        Width = Wide;
        Height = High;
        FontFamily = Materials.UiFont;
        RenderOptions.SetBitmapScalingMode(_art, BitmapScalingMode.HighQuality);
        _inside.Children.Add(_layer);
        _inside.Children.Add(_art);
        _inside.Children.Add(_end);
        _body.Child = _inside;
        _root.Children.Add(_second);
        _root.Children.Add(_body);
        _root.Children.Add(_keyline);
        Content = _root;
        Canvas.SetLeft(_body, Wide / 2 + _plan.Body.X);
        Canvas.SetTop(_body, Hang);
        _body.Width = _plan.Body.Width;
        _body.Height = _plan.Body.Height;
        foreach (var e in new UIElement[] { _art, _end, _second }) { Canvas.SetLeft(e, 0); Canvas.SetTop(e, 0); }
        // Sizes from the first: a picture's carried from where it is (an unset width is NaN, and the diagnostics' JSON
        // cannot hold one).
        _art.Width = _art.Height = Layout.Picture;
        _end.Width = _end.Height = 0;
        Canvas.SetTop(_second, Hang);
        _body.SizeChanged += (_, _) => Place();
        Place();
        _body.MouseLeftButtonUp += (_, e) => { if (!e.Handled) Clicked?.Invoke(); };
        _body.MouseLeave += (_, _) => { if (_face.Shape == IslandShape.Open) PointerLeft?.Invoke(); };
        // The picture opens the app once the island is open (closed, a click on it opens the island).
        _art.MouseLeftButtonDown += (_, a) => { if (_face.Shape == IslandShape.Open) a.Handled = true; };
        _art.MouseLeftButtonUp += (_, a) =>
        {
            if (_face.Shape != IslandShape.Open) return;
            a.Handled = true;
            Pressed?.Invoke("app");
        };
        SourceInitialized += (_, _) => Native.AddExStyle(new WindowInteropHelper(this).Handle, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
    }

    /// <summary>The shell's animation setting (springs and fades, or at once).</summary>
    public bool Animations { set => _animations = value; }

    /// <summary>What is behind the island reads dark (the menu bar's words are light): its key line shows (HIG).</summary>
    public bool DarkBehind
    {
        set
        {
            if (_darkBehind == value) return;
            _darkBehind = value;
            PaintKeyLine();
        }
    }

    public static IslandPlan PlanOf(IslandFace f) => Layout.For(f.Shape, f.Main?.Kind, f.Second != null, f.Notice?.Kind);

    /// <summary>The island's corners, its clip and its key line where the island is now (each frame of its spring).</summary>
    void Place()
    {
        double w = _body.Width, h = _body.Height, r = Layout.RadiusAt(h);
        _body.CornerRadius = new CornerRadius(r);
        _inside.Width = w;
        _inside.Height = h;
        _inside.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        _layer.Width = w;
        _layer.Height = h;
        _art.CornerRadius = new CornerRadius(_roundArt ? Layout.PictureRadiusAt(h, Canvas.GetTop(_art)) : 0);
        Canvas.SetLeft(_keyline, Canvas.GetLeft(_body));
        Canvas.SetTop(_keyline, Canvas.GetTop(_body));
        _keyline.Width = w;
        _keyline.Height = h;
        _keyline.CornerRadius = new CornerRadius(r);
    }

    /// <summary>
    /// Shows <paramref name="face"/>: the island goes to its place and size on a spring, carrying the picture and the
    /// meter when the same activity shows in both faces; what was in it otherwise goes at once and the new face fades in
    /// as the island nears its size. The same face again only freshens what is in it.
    /// </summary>
    public void Show(IslandFace face)
    {
        var plan = PlanOf(face);
        var was = _face;
        bool reshaped = plan.Body != _plan.Body || face.Shape != was.Shape || face.Notice != was.Notice || (face.Second == null) != (was.Second == null)
            || face.Main?.Key != was.Main?.Key || face.Main?.Kind != was.Main?.Kind;
        bool carry = reshaped && Layout.Carries(was.Shape, was.Main?.Key, face.Shape, face.Main?.Key);
        bool artWas = _plan.Picture != null, endWas = _plan.End != null;
        _face = face;
        _plan = plan;
        Fill(face);
        PaintKeyLine();
        if (!reshaped)
        {
            // The same face freshened: the picture may have come meanwhile (an app's icon loaded after its progress began,
            // its corners square, not the stand-in's round ones; the Gemini review, 2026-11-08).
            Place();
            return;
        }
        if (carry) Carried++; else Faded++;

        bool calm = !_animations || ReduceMotion.Calms("island");
        double ms = calm ? 0 : Motion.IslandMorph;
        Func<double, double> ease = calm ? Tween.Out : Tween.Spring(Motion.IslandSpring);
        // Shrinking, its height settles at most a little under its mark (not a sliver: Core's IslandLayout.Under).
        double fall = _body.Height - plan.Body.Height;
        var parts = new List<Tween.Part>
        {
            new(_body, Canvas.LeftProperty, Wide / 2 + plan.Body.X, ease),
            new(_body, WidthProperty, plan.Body.Width, ease),
            new(_body, HeightProperty, plan.Body.Height, t => Layout.Under(ease(t), fall)),
        };
        // What fades in: the words and buttons, and the picture and meter when not carried (placed at once where they go).
        var fadeIn = new List<UIElement> { _layer };
        Tween.Stop(_layer, OpacityProperty);
        _layer.Opacity = 0;
        // The picture and the meter take the pointer only while this face shows them: hidden, they stay where they were
        // (a clear 52-DIP square with the open island's hand cursor over a notice; the Gemini review, 2026-11-08).
        _art.IsHitTestVisible = plan.Picture != null;
        _end.IsHitTestVisible = plan.End != null;
        if (plan.Picture is { } pic)
        {
            double x = pic.X - plan.Body.X, y = pic.Y - plan.Body.Y;
            if (carry && artWas)
            {
                parts.Add(new(_art, Canvas.LeftProperty, x, ease));
                parts.Add(new(_art, Canvas.TopProperty, y, ease));
                parts.Add(new(_art, WidthProperty, pic.Width, ease));
                parts.Add(new(_art, HeightProperty, pic.Height, ease));
                Tween.Stop(_art, OpacityProperty);
                _art.Opacity = 1;
            }
            else
            {
                Set(_art, x, y, pic.Width, pic.Height);
                fadeIn.Add(_art);
            }
        }
        else Hide(_art);
        if (plan.End is { } end)
        {
            double x = end.X - plan.Body.X - _end.Width / 2, y = end.Y - plan.Body.Y - _end.Height / 2;
            if (carry && endWas)
            {
                parts.Add(new(_end, Canvas.LeftProperty, x, ease));
                parts.Add(new(_end, Canvas.TopProperty, y, ease));
                Tween.Stop(_end, OpacityProperty);
                _end.Opacity = 1;
            }
            else
            {
                Set(_end, x, y);
                fadeIn.Add(_end);
            }
        }
        else Hide(_end);
        Tween.Together(ms, parts);

        if (plan.Second is { } second) Canvas.SetLeft(_second, Wide / 2 + second.X);
        Tween.To(_second, OpacityProperty, plan.Second != null ? 1 : 0, calm ? 0 : Motion.IslandContentIn, Tween.Out);
        // What comes in comes as the island nears its size (the iPhone's: the words never squeezed): nothing over the
        // spring's first part, then a fade.
        double wait = Motion.IslandContentWait;
        foreach (var e in fadeIn)
        {
            if (calm) { Tween.Stop(e, OpacityProperty); e.Opacity = 1; }
            else { e.Opacity = 0; Tween.To(e, OpacityProperty, 1, Motion.IslandMorph, t => t <= wait ? 0 : Tween.Out((t - wait) / (1 - wait))); }
        }
        Place();
    }

    static void Set(FrameworkElement e, double x, double y, double? w = null, double? h = null)
    {
        foreach (var p in new[] { Canvas.LeftProperty, Canvas.TopProperty, WidthProperty, HeightProperty }) Tween.Stop(e, p);
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        if (w is double ww) e.Width = ww;
        if (h is double hh) e.Height = hh;
    }

    static void Hide(UIElement e)
    {
        Tween.Stop(e, OpacityProperty);
        e.Opacity = 0;
    }

    /// <summary>The key line round the island and the circle beside it, in the colour of what shows.</summary>
    void PaintKeyLine()
    {
        bool shown = Layout.KeyLine(_face.Shape, _darkBehind);
        _keyline.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (shown) _keyline.BorderBrush = KeyBrush(KeyOf(_face));
        bool second = shown && _plan.Second != null && _face.Second != null;
        _second.BorderThickness = second ? new Thickness(1) : default;
        _second.BorderBrush = second ? KeyBrush(_face.Second!.Kind == IslandKind.Playing ? _face.Second.Tint : White) : null;
    }

    static Brush KeyBrush(Color c) => new SolidColorBrush(Color.FromArgb(c.R == 255 && c.G == 255 && c.B == 255 ? KeyAlphaWhite : KeyAlpha, c.R, c.G, c.B));

    static Color KeyOf(IslandFace f) => f.Shape == IslandShape.Notice && f.Notice is { } n
        ? n.Kind switch { NoticeKind.Headphones => Green, NoticeKind.Battery => Red, NoticeKind.Focus when n.On => Purple, _ => White }
        : f.Main is { Kind: IslandKind.Playing } m ? m.Tint : White;

    /// <summary>What is in the island for <paramref name="f"/>, made again (where the picture and the meter go is <see cref="Show"/>'s).</summary>
    void Fill(IslandFace f)
    {
        _layer.Children.Clear();
        _buttons.Clear();
        _second.Child = f.Second is { } s ? SecondPicture(s) : null;
        if (f.Main is { } main && f.Shape is IslandShape.Compact or IslandShape.Open)
        {
            FillArt(main);
            FillEnd(main);
        }
        switch (f.Shape)
        {
            case IslandShape.Notice when f.Notice is { } n: Notice(n); break;
            case IslandShape.Open when f.Main is { Kind: IslandKind.Playing } m: OpenPlaying(m); break;
            case IslandShape.Open when f.Main is { } m: OpenProgress(m); break;
        }
        if (f.Shape == IslandShape.Open) _buttons["app"] = _art;
    }

    /// <summary>The picture: a record's sleeve with its corners concentric with the island's; an app's icon as it is (a mark, not a tile).</summary>
    void FillArt(IslandLive m)
    {
        _roundArt = m.Kind == IslandKind.Playing || m.Picture == null;
        _art.Cursor = _face.Shape == IslandShape.Open ? Cursors.Hand : null;
        if (m.Picture is { } image)
        {
            _art.Background = new ImageBrush(image) { Stretch = m.Kind == IslandKind.Playing ? Stretch.UniformToFill : Stretch.Uniform };
            _art.Child = null;
            return;
        }
        // None: a note (or a window) on a grey square, drawn at the open size and shrunk with the picture.
        _art.Background = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
        var glyph = m.Kind == IslandKind.Progress ? Glyphs.Stroke(Glyphs.WindowPicture, 26, Dim, 1.4) : Glyphs.Fill(Glyphs.Note, 26, Dim);
        _art.Child = new Viewbox { Child = Center(glyph), Margin = new Thickness(2) };
    }

    /// <summary>What stands at the trailing end: the level meter in what plays' colour, or how far a progress has come.</summary>
    void FillEnd(IslandLive m)
    {
        if (m.Kind == IslandKind.Playing)
        {
            _end.Width = Layout.MeterWidth;
            _end.Height = 12;
            _end.Child = Meter(m.Tint);
        }
        else
        {
            _end.Width = _end.Height = 16;
            _end.Child = Center(Ring(m.Progress, 6.5, 2.2));
        }
    }

    static FrameworkElement SecondPicture(IslandLive s)
    {
        if (s.Kind == IslandKind.Progress) return Center(Ring(s.Progress, 6.5, 2.2));
        // A sleeve in the detached circle: its margin all round, so as round as the circle.
        return Center(Picture(s.Picture, 14, 7));
    }

    // ---- what is in it -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A notice: one capsule, its glyph and its value each with their middle half its height in from its ends (concentric
    /// with its round ends), the words medium or heavier (HIG), across the middle (no camera there on a PC).
    /// </summary>
    void Notice(IslandNotice n)
    {
        double h = _plan.Body.Height, half = h / 2, valuePad = Math.Round(h * 0.36);
        switch (n.Kind)
        {
            case NoticeKind.Headphones:
            case NoticeKind.Battery:
            {
                bool battery = n.Kind == NoticeKind.Battery;
                var red = new SolidColorBrush(Red);
                FrameworkElement glyph = battery ? BatteryGlyph(n.Percent ?? 20, red) : Glyphs.Stroke(Glyphs.Headphones, 20, Ink, 1.6);
                glyph.HorizontalAlignment = HorizontalAlignment.Left;
                glyph.VerticalAlignment = VerticalAlignment.Center;
                glyph.Margin = new Thickness(half - (battery ? 12 : 10), 0, 0, 0);
                _layer.Children.Add(glyph);
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(h + 2, 0, h + 8, 0) };
                words.Children.Add(Text(n.Title, 13, Ink, FontWeights.SemiBold));
                if (n.Detail.Length > 0) words.Children.Add(Text(n.Detail, 12, Dim, FontWeights.Medium));
                _layer.Children.Add(words);
                if (n.Percent is int p)
                {
                    FrameworkElement right = battery ? Text(p + "%", 13, red, FontWeights.SemiBold) : PercentRing(p);
                    right.HorizontalAlignment = HorizontalAlignment.Right;
                    right.VerticalAlignment = VerticalAlignment.Center;
                    right.Margin = new Thickness(0, 0, battery ? valuePad : half - 12.5, 0);
                    _layer.Children.Add(right);
                }
                break;
            }
            case NoticeKind.Focus:
            {
                var tint = n.On ? new SolidColorBrush(Purple) : Dim;
                Lead(Glyphs.Fill(Glyphs.Moon, 12, tint), half);
                var title = Text(n.Title, 12, Ink, FontWeights.Medium);
                title.Margin = new Thickness(h, 0, 48, 0);
                _layer.Children.Add(title);
                var state = Text(n.Detail, 12, tint, FontWeights.SemiBold);
                state.HorizontalAlignment = HorizontalAlignment.Right;
                state.Margin = new Thickness(0, 0, valuePad, 0);
                _layer.Children.Add(state);
                break;
            }
            default:
            {
                Lead(Glyphs.Stroke(n.Percent == null ? Glyphs.Speaker : Glyphs.Headphones, 12, Ink, 1.4), half);
                var name = Text(n.Title, 12, Ink, FontWeights.Medium);
                name.Margin = new Thickness(h, 0, valuePad, 0);
                _layer.Children.Add(name);
                break;
            }
        }
    }

    /// <summary>A 12-DIP glyph with its middle <paramref name="half"/> in from the leading end.</summary>
    void Lead(FrameworkElement glyph, double half)
    {
        glyph.HorizontalAlignment = HorizontalAlignment.Left;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        glyph.Margin = new Thickness(half - 6, 0, 0, 0);
        _layer.Children.Add(glyph);
    }

    /// <summary>What plays, opened: the sleeve (carried), its title and artist beside it, the meter (carried), the buttons under them; even margins.</summary>
    void OpenPlaying(IslandLive m)
    {
        const double pad = Layout.Margin, art = Layout.Art;
        var head = new Grid { Height = art, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(pad + art + 12, pad, pad + Layout.MeterWidth + 12, 0) };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        words.Children.Add(Text(m.Title, 14, Ink, FontWeights.SemiBold));
        if (m.Detail.Length > 0) words.Children.Add(Text(m.Detail, 12, Dim, FontWeights.Medium));
        Press(words, "app");
        head.Children.Add(words);
        _layer.Children.Add(head);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, pad) };
        buttons.Children.Add(Button(Glyphs.Previous, 18, "previous"));
        buttons.Children.Add(Button(m.Playing ? Glyphs.Pause : Glyphs.Play, 24, "playpause"));
        buttons.Children.Add(Button(Glyphs.Next, 18, "next"));
        _layer.Children.Add(buttons);
    }

    /// <summary>A progress, opened: the icon (carried), the app's name and how far beside it, the track under them; even margins.</summary>
    void OpenProgress(IslandLive m)
    {
        const double pad = Layout.Margin, icon = Layout.Icon;
        var head = new Grid { Height = icon, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(pad + icon + 12, pad, pad, 0) };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Text(m.Title, 14, Ink, FontWeights.SemiBold));
        words.Children.Add(Text(m.Detail, 12, Dim, FontWeights.Medium));
        head.Children.Add(words);
        _layer.Children.Add(head);
        var track = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(pad, 0, pad, pad) };
        track.Children.Add(new Border { Background = Track, CornerRadius = new CornerRadius(2) });
        double p = m.Progress < 0 ? 0.3 : Math.Clamp(m.Progress, 0, 1);
        var fill = new Border { Background = Ink, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
        track.SizeChanged += (_, e) => fill.Width = Math.Max(4, e.NewSize.Width * p);
        track.Children.Add(fill);
        _layer.Children.Add(track);
    }

    // ---- parts ----------------------------------------------------------------------------------------------------

    static TextBlock Text(string s, double size, Brush brush, FontWeight? weight = null) => new()
    {
        Text = s, FontSize = size, Foreground = brush, FontWeight = weight ?? FontWeights.Medium,
        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A picture rounded by <paramref name="radius"/>, or a note on a grey square when there is none.</summary>
    static FrameworkElement Picture(ImageSource? image, double size, double radius)
    {
        var box = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(radius),
            Background = image == null ? new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)) : new ImageBrush(image) { Stretch = Stretch.UniformToFill },
        };
        RenderOptions.SetBitmapScalingMode(box, BitmapScalingMode.HighQuality);
        if (image == null) box.Child = Center(Glyphs.Fill(Glyphs.Note, size * 0.55, Dim));
        return box;
    }

    /// <summary>Five bars of a level meter in what plays' colour, <see cref="Layout.MeterWidth"/> wide (still: nothing on the island moves by itself).</summary>
    static FrameworkElement Meter(Color tint)
    {
        double[] heights = [0.45, 0.9, 0.6, 1.0, 0.5];
        var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 12, HorizontalAlignment = HorizontalAlignment.Center };
        var brush = new SolidColorBrush(tint);
        for (int i = 0; i < heights.Length; i++)
            row.Children.Add(new Rectangle { Width = 2, Height = 12 * heights[i], RadiusX = 1, RadiusY = 1, Fill = brush, Margin = new Thickness(0, 0, i < heights.Length - 1 ? 2 : 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    /// <summary>A progress ring (from the top, clockwise; a third of it when it does not know how far).</summary>
    static FrameworkElement Ring(double progress, double radius, double thickness)
    {
        double size = 2 * radius + thickness;
        var canvas = new Canvas { Width = size, Height = size };
        var c = new Point(size / 2, size / 2);
        canvas.Children.Add(new Ellipse { Width = 2 * radius, Height = 2 * radius, Stroke = Track, StrokeThickness = thickness, Margin = new Thickness(thickness / 2) });
        double p = progress < 0 ? 0.33 : Math.Clamp(progress, 0, 0.999);
        if (p > 0)
        {
            double a = p * 2 * Math.PI;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(c.X, c.Y - radius), false, false);
                ctx.ArcTo(new Point(c.X + radius * Math.Sin(a), c.Y - radius * Math.Cos(a)), new Size(radius, radius), 0, p > 0.5, SweepDirection.Clockwise, true, false);
            }
            canvas.Children.Add(new Path { Data = g, Stroke = new SolidColorBrush(Color.FromArgb(0xD9, 255, 255, 255)), StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        }
        return canvas;
    }

    /// <summary>A headset's battery: a green ring and its number.</summary>
    static FrameworkElement PercentRing(int percent)
    {
        var grid = new Grid();
        var ring = (Canvas)Ring(percent / 100.0, 11, 3);
        foreach (var path in ring.Children.OfType<Path>()) path.Stroke = new SolidColorBrush(Green);
        grid.Children.Add(ring);
        var n = Text(percent.ToString(System.Globalization.CultureInfo.InvariantCulture), 9.5, Ink, FontWeights.SemiBold);
        n.HorizontalAlignment = HorizontalAlignment.Center;
        grid.Children.Add(n);
        return grid;
    }

    /// <summary>A small battery filled to <paramref name="percent"/> in <paramref name="brush"/>.</summary>
    static FrameworkElement BatteryGlyph(int percent, Brush brush)
    {
        var grid = new Grid { Width = 24, Height = 12 };
        grid.Children.Add(new Border { BorderBrush = Dim, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 2, 0) });
        grid.Children.Add(new Border { Background = brush, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 2, 0, 2), Width = Math.Max(2, 18 * Math.Clamp(percent, 0, 100) / 100.0) });
        grid.Children.Add(new Border { Background = Dim, Width = 1.5, Height = 4, HorizontalAlignment = HorizontalAlignment.Right, CornerRadius = new CornerRadius(0, 1, 1, 0) });
        return grid;
    }

    FrameworkElement Button(Geometry glyph, double size, string name)
    {
        var b = new Border { Width = 52, Height = 30, Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = Center(Glyphs.Fill(glyph, size, Ink)) };
        Press(b, name);
        return b;
    }

    /// <summary>A part of the opened island that does something: pressed, it says so (and the island does not take the click as its own).</summary>
    void Press(FrameworkElement e, string name)
    {
        _buttons[name] = e;
        e.MouseLeftButtonDown += (_, a) => a.Handled = true;
        e.MouseLeftButtonUp += (_, a) => { a.Handled = true; Pressed?.Invoke(name); };
    }

    static FrameworkElement Center(FrameworkElement e)
    {
        e.HorizontalAlignment = HorizontalAlignment.Center;
        e.VerticalAlignment = VerticalAlignment.Center;
        return e;
    }

    Rect ScreenRect(FrameworkElement e)
    {
        if (!e.IsLoaded || PresentationSource.FromVisual(e) == null) return Rect.Empty;
        var p = e.TransformToAncestor(this).Transform(new Point(0, 0));
        return new Rect(Left + p.X, Top + p.Y, e.ActualWidth, e.ActualHeight);
    }

    static IEnumerable<string> Texts(DependencyObject d)
    {
        if (d is TextBlock t) yield return t.Text;
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
            foreach (var s in Texts(VisualTreeHelper.GetChild(d, i))) yield return s;
    }

    /// <summary>
    /// The colour a record's sleeve gives its meter: its pixels' mean, brightened and made vivid enough to read on black
    /// (white when it is grey or there is none).
    /// </summary>
    public static Color TintOf(BitmapSource? art)
    {
        if (art == null) return Color.FromArgb(0xD9, 255, 255, 255);
        try
        {
            var small = new TransformedBitmap(new FormatConvertedBitmap(art, PixelFormats.Bgra32, null, 0), new ScaleTransform(8.0 / art.PixelWidth, 8.0 / art.PixelHeight));
            var px = new byte[small.PixelWidth * small.PixelHeight * 4];
            small.CopyPixels(px, small.PixelWidth * 4, 0);
            double r = 0, g = 0, b = 0;
            int n = px.Length / 4;
            for (int i = 0; i < px.Length; i += 4) { b += px[i]; g += px[i + 1]; r += px[i + 2]; }
            (r, g, b) = (r / n, g / n, b / n);
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            if (max - min < 24) return Color.FromArgb(0xD9, 255, 255, 255);
            double k = 235 / Math.Max(max, 1);
            return Color.FromRgb((byte)Math.Min(255, r * k), (byte)Math.Min(255, g * k), (byte)Math.Min(255, b * k));
        }
        catch (Exception) { return Color.FromArgb(0xD9, 255, 255, 255); }
    }
}
