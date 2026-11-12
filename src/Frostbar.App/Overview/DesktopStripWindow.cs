using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using Frostbar.App.Common;
using Frostbar.Core.Overview;
using Frostbar.Core.Wm;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Shell;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Overview;

/// <summary>
/// The strip of virtual desktops along the top of the window overview (stage 3-4; the user's choice B, 2026-10-16, the
/// pictures always shown): each desktop's picture (its wallpaper and, as small live thumbnails, its windows), its name
/// under it, the current desktop framed, and the add button at the right end. A window of its own above the overview:
/// wide and low, so fading it costs little where the overview's screen-sized window would be drawn again every frame.
/// It never takes the foreground (the overview keeps the keys). A click on a picture goes to that desktop; the button
/// adds one.
/// </summary>
public sealed class DesktopStripWindow : Window
{
    public const double PictureHeight = 63, Gap = 18, Side = 14, TopPad = 12, NameGap = 5, NameHeight = 16, BottomPad = 10, AddSize = 40;

    /// <summary>One desktop as the strip shows it.</summary>
    public sealed record Desktop(Guid Id, string Name, bool Current, ImageSource? Picture, WallpaperFit Fit, Color Back);

    static readonly Brush Band = new SolidColorBrush(Color.FromArgb(0x38, 0, 0, 0));
    static readonly Brush FrameLit = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
    static readonly Brush FrameHover = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
    static readonly Brush Edge = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    static readonly Brush NameText = new SolidColorBrush(Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF));
    static readonly Brush NameDimText = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    static readonly Brush AddFill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), AddLit = new SolidColorBrush(Color.FromArgb(0x52, 0xFF, 0xFF, 0xFF));

    readonly Canvas _canvas = new();
    readonly List<Border> _pictures = new();
    readonly List<Border> _frames = new();
    readonly List<Thumbnail> _thumbs = new();
    Thumbnail? _ghost;
    IntPtr _ghostOf;
    Guid? _target;
    readonly List<(Guid Desktop, long Hwnd)> _shown = new();
    IReadOnlyList<Desktop> _desktops = [];
    StripLayout _layout;
    Border? _add;

    public IntPtr Handle => new WindowInteropHelper(this).Handle;
    public IReadOnlyList<Desktop> Desktops => _desktops;
    /// <summary>Diagnostics: the windows drawn in the pictures, by desktop.</summary>
    public IReadOnlyList<(Guid Desktop, long Hwnd)> Shown => _shown;
    public event Action<Guid>? Chosen;
    public event Action? AddClicked;
    /// <summary>The pointer came onto a desktop's picture (its rectangle on the screen, DIPs), or left it (stage 3-5's ×).</summary>
    public event Action<Guid, Rect>? PictureEntered;
    public event Action<Guid>? PictureLeft;

    public DesktopStripWindow()
    {
        Title = "Frostbar.DesktopStrip";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        FontFamily = Materials.UiFont;
        UseLayoutRounding = true;
        Language = XmlLanguage.GetLanguage("ja-JP");
        Width = 1; Height = 1; Left = -32000; Top = -32000; // placed when shown
        Content = _canvas;
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

    /// <summary>The strip's height (DIPs) for pictures of full size.</summary>
    public static double StripHeight => TopPad + PictureHeight + NameGap + NameHeight + BottomPad;

    /// <summary>
    /// Lays the strip out for <paramref name="desktops"/> across <paramref name="place"/> (DIPs on the screen: the
    /// overview's top, its full width) with pictures of the screen's shape (<paramref name="aspect"/>), the add button
    /// when <paramref name="canAdd"/>; shown above the overview (not yet faded in: the caller fades it).
    /// </summary>
    public void Lay(IReadOnlyList<Desktop> desktops, Rect place, double aspect, bool canAdd, string addLabel)
    {
        ClearWindows();
        _slide++; // a slide under way ends with the elements it moved
        // Laid out again while shown (a desktop added or removed): from where each picture was (it was all made again
        // in a frame: 2026-10-19, the user's "B11").
        bool slide = IsVisible && Animate() && _layout.Pictures != null && Math.Abs(place.Left - Left) < 0.5 && Math.Abs(place.Top - Top) < 0.5;
        var before = new Dictionary<Guid, (Rect2 Picture, Rect2 Name)>();
        var gone = new List<(Desktop Desktop, Rect2 Picture)>();
        Rect2? addBefore = slide ? _layout.Add : null;
        if (slide)
            for (int i = 0; i < _desktops.Count && i < _layout.Pictures!.Count; i++)
            {
                before[_desktops[i].Id] = (_layout.Pictures[i], _layout.Names[i]);
                if (desktops.All(d => d.Id != _desktops[i].Id)) gone.Add((_desktops[i], _layout.Pictures[i]));
            }
        _desktops = desktops;
        _layout = DesktopStrip.Arrange(desktops.Count, place.Width, aspect, PictureHeight, Gap, Side, TopPad, NameGap, NameHeight, AddSize, canAdd);
        Left = place.Left;
        Top = place.Top;
        Width = place.Width;
        Height = _layout.Height + BottomPad;
        _canvas.Children.Clear();
        _pictures.Clear();
        _frames.Clear();
        _names.Clear();
        _target = null;
        var band = new Border { Width = place.Width, Height = Height, Background = Band };
        _canvas.Children.Add(band);
        for (int i = 0; i < desktops.Count; i++) AddDesktop(i, desktops[i]);
        _add = null;
        if (_layout.Add is { } a) AddButton(a, addLabel);
        if (!IsVisible) Show();
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _sliding = false;
        if (slide) Slide(before, gone, addBefore);
    }

    /// <summary>The shell's animation setting (the strip slides when a desktop comes or goes with it on).</summary>
    public Func<bool> Animate { get; set; } = () => true;

    /// <summary>How long the pictures take to slide to their new places when a desktop comes or goes (ms).</summary>
    public const double SlideMs = Motion.StripSlide;

    /// <summary>Slides since the start, and whether one is under way (diagnostics: the windows are drawn after it).</summary>
    public int Slides { get; private set; }
    public bool Sliding => _sliding;

    readonly List<TextBlock> _names = new();
    bool _sliding;
    int _slide;
    // The windows to draw in the pictures once they stand still (DWM's thumbnails do not move with them).
    (IReadOnlyDictionary<Guid, List<(IntPtr Hwnd, RECT Frame)>> Windows, RECT Monitor, double Scale)? _windowsLater;

    /// <summary>
    /// Each desktop that was there slides (and resizes) from where it was; one come grows in from small in its place;
    /// one gone fades away where it was; the add button slides along. One movement for all of them: their places agree
    /// at every frame. The windows in the pictures are drawn once they stand still.
    /// </summary>
    void Slide(Dictionary<Guid, (Rect2 Picture, Rect2 Name)> before, List<(Desktop Desktop, Rect2 Picture)> gone, Rect2? addBefore)
    {
        var parts = new List<Tween.Part>();
        // Reduced motion: a desktop that comes or goes only fades (it grew in and shrank away from 0.6).
        double small = ReduceMotion.Calms("stripGrow") ? 1 : 0.6;
        void Move(FrameworkElement e, double x0, double y0, double w0, double h0, double x, double y, double w, double h)
        {
            Canvas.SetLeft(e, x0);
            Canvas.SetTop(e, y0);
            e.Width = w0;
            if (!double.IsNaN(h0)) e.Height = h0;
            parts.Add(new(e, Canvas.LeftProperty, x));
            parts.Add(new(e, Canvas.TopProperty, y));
            parts.Add(new(e, WidthProperty, w));
            if (!double.IsNaN(h)) parts.Add(new(e, HeightProperty, h));
        }
        for (int i = 0; i < _desktops.Count && i < _pictures.Count; i++)
        {
            var r = _layout.Pictures[i];
            var n = _layout.Names[i];
            if (before.TryGetValue(_desktops[i].Id, out var b))
            {
                var p = b.Picture;
                if (p.Equals(r) && b.Name.Equals(n)) continue;
                Move(_pictures[i], p.X, p.Y, p.Width, p.Height, r.X, r.Y, r.Width, r.Height);
                Move(_frames[i], p.X - 3, p.Y - 3, p.Width + 6, p.Height + 6, r.X - 3, r.Y - 3, r.Width + 6, r.Height + 6);
                Move(_names[i], b.Name.X - Gap + 2, b.Name.Y, b.Name.Width + 2 * Gap - 4, double.NaN, n.X - Gap + 2, n.Y, n.Width + 2 * Gap - 4, double.NaN);
                continue;
            }
            // Come: it grows in from small about its middle, faded in.
            foreach (var e in new FrameworkElement[] { _pictures[i], _frames[i], _names[i] })
            {
                var grow = new ScaleTransform(small, small);
                e.RenderTransformOrigin = new Point(0.5, 0.5);
                e.RenderTransform = grow;
                e.Opacity = 0;
                parts.Add(new(grow, ScaleTransform.ScaleXProperty, 1.0));
                parts.Add(new(grow, ScaleTransform.ScaleYProperty, 1.0));
                parts.Add(new(e, OpacityProperty, 1.0, Tween.SineInOut, 0.7));
            }
        }
        if (_add != null && addBefore is { } ab && _layout.Add is { } a && !ab.Equals(a))
            Move(_add, ab.X, ab.Y, ab.Width, ab.Height, a.X, a.Y, a.Width, a.Height);
        // Gone: its picture where it was, fading and shrinking away above the band.
        var ghosts = new List<UIElement>();
        foreach (var (d, p) in gone)
        {
            var shrink = new ScaleTransform(1, 1);
            var ghost = new Border
            {
                Width = p.Width, Height = p.Height, CornerRadius = new CornerRadius(5), Background = PictureBrush(d), BorderBrush = Edge,
                BorderThickness = new Thickness(0.5), IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = shrink,
            };
            Canvas.SetLeft(ghost, p.X);
            Canvas.SetTop(ghost, p.Y);
            _canvas.Children.Insert(1, ghost); // over the band, under the others sliding over its place
            ghosts.Add(ghost);
            parts.Add(new(shrink, ScaleTransform.ScaleXProperty, small));
            parts.Add(new(shrink, ScaleTransform.ScaleYProperty, small));
            parts.Add(new(ghost, OpacityProperty, 0.0, Tween.SineInOut, 0.7));
        }
        if (parts.Count == 0) return;
        _sliding = true;
        Slides++;
        int slide = ++_slide;
        Tween.Together(SlideMs, parts, () =>
        {
            if (slide != _slide) return; // laid out again meanwhile: these elements are gone
            foreach (var g in ghosts) _canvas.Children.Remove(g);
            _sliding = false;
            if (_windowsLater is { } later)
            {
                _windowsLater = null;
                ShowWindows(later.Windows, later.Monitor, later.Scale);
            }
        });
    }

    void AddDesktop(int i, Desktop d)
    {
        var r = _layout.Pictures[i];
        // The frame of the current desktop (and of the one pointed at) stands 3 DIP outside the picture: the windows'
        // thumbnails, drawn over the window's own content, stay inside the picture and do not cover it.
        var frame = new Border
        {
            Width = r.Width + 6, Height = r.Height + 6, CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2), BorderBrush = d.Current ? FrameLit : Brushes.Transparent, IsHitTestVisible = false,
        };
        Canvas.SetLeft(frame, r.X - 3);
        Canvas.SetTop(frame, r.Y - 3);
        var picture = new Border
        {
            Width = r.Width, Height = r.Height, CornerRadius = new CornerRadius(5),
            Background = PictureBrush(d), BorderBrush = Edge, BorderThickness = new Thickness(0.5), Cursor = Cursors.Arrow,
        };
        System.Windows.Automation.AutomationProperties.SetName(picture, d.Name);
        Canvas.SetLeft(picture, r.X);
        Canvas.SetTop(picture, r.Y);
        picture.MouseEnter += (_, _) =>
        {
            if (!d.Current) frame.BorderBrush = FrameHover;
            PictureEntered?.Invoke(d.Id, new Rect(Left + r.X, Top + r.Y, r.Width, r.Height));
        };
        picture.MouseLeave += (_, _) =>
        {
            if (!d.Current && _target != d.Id) frame.BorderBrush = Brushes.Transparent;
            PictureLeft?.Invoke(d.Id);
        };
        picture.MouseLeftButtonUp += (_, e) => { e.Handled = true; Chosen?.Invoke(d.Id); };
        var n = _layout.Names[i];
        var name = new TextBlock
        {
            Text = d.Name, Width = n.Width + 2 * Gap - 4, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 11.5, Foreground = d.Current ? NameText : NameDimText, FontWeight = d.Current ? FontWeights.SemiBold : FontWeights.Normal, IsHitTestVisible = false,
        };
        Canvas.SetLeft(name, n.X - Gap + 2);
        Canvas.SetTop(name, n.Y);
        _canvas.Children.Add(picture);
        _canvas.Children.Add(frame);
        _canvas.Children.Add(name);
        _names.Add(name);
        _pictures.Add(picture);
        _frames.Add(frame);
    }

    /// <summary>Diagnostics: the desktop whose picture WPF has the pointer over (null: none).</summary>
    public Guid? PointerOn
    {
        get
        {
            for (int i = 0; i < _pictures.Count && i < _desktops.Count; i++) if (_pictures[i].IsMouseOver) return _desktops[i].Id;
            return null;
        }
    }

    /// <summary>The desktop whose picture is under (<paramref name="x"/>, <paramref name="y"/>) (pixels on the screen; a little around each counts), or null.</summary>
    public Guid? DesktopAt(int x, int y, double scale)
    {
        if (!IsVisible || _layout.Pictures == null) return null;
        int i = DesktopStrip.PictureAt(_layout.Pictures, x / scale - Left, y / scale - Top, 6);
        return i >= 0 && i < _desktops.Count ? _desktops[i].Id : null;
    }

    /// <summary>The picture a dragged window would go to, framed as if pointed at (null: none).</summary>
    public void Target(Guid? desktop)
    {
        if (_target == desktop) return;
        _target = desktop;
        for (int i = 0; i < _frames.Count && i < _desktops.Count; i++)
            if (!_desktops[i].Current) _frames[i].BorderBrush = _desktops[i].Id == desktop ? FrameHover : Brushes.Transparent;
    }

    /// <summary>
    /// A small live picture of the window being dragged, drawn above the strip (the overview's own thumbnails are under
    /// this window): at <paramref name="px"/> (pixels on the screen); null takes it away.
    /// </summary>
    public void Ghost(IntPtr window, RECT? px)
    {
        if (px is not { } r || window == IntPtr.Zero)
        {
            _ghost?.Dispose();
            _ghost = null;
            _ghostOf = IntPtr.Zero;
            return;
        }
        if (_ghostOf != window)
        {
            _ghost?.Dispose();
            _ghost = Thumbnail.Create(Handle, window);
            _ghostOf = window;
        }
        Native.GetWindowRect(Handle, out var me);
        _ghost?.Show(new RECT(r.Left - me.Left, r.Top - me.Top, r.Right - me.Left, r.Bottom - me.Top), 230);
    }

    static Brush PictureBrush(Desktop d)
    {
        var back = new SolidColorBrush(d.Back);
        if (d.Picture == null) return back;
        var image = new ImageBrush(d.Picture)
        {
            Stretch = d.Fit switch { WallpaperFit.Fit => Stretch.Uniform, WallpaperFit.Stretch => Stretch.Fill, _ => Stretch.UniformToFill },
        };
        if (d.Fit != WallpaperFit.Fit) return image;
        // Fit: the colour around the picture, as Windows shows it.
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(back, null, new RectangleGeometry(new Rect(0, 0, 16, 9))));
        group.Children.Add(new GeometryDrawing(image, null, new RectangleGeometry(new Rect(0, 0, 16, 9))));
        return new DrawingBrush(group) { Stretch = Stretch.Fill };
    }

    void AddButton(Rect2 a, string label)
    {
        var plus = new Path
        {
            Data = Geometry.Parse("M0,6 H12 M6,0 V12"), Stroke = NameText, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        };
        var b = new Border
        {
            Width = a.Width, Height = a.Height, CornerRadius = new CornerRadius(8), Background = AddFill, BorderBrush = Edge,
            BorderThickness = new Thickness(1), Child = plus, ToolTip = label,
        };
        System.Windows.Automation.AutomationProperties.SetName(b, label);
        b.MouseEnter += (_, _) => b.Background = AddLit;
        b.MouseLeave += (_, _) => b.Background = AddFill;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; AddClicked?.Invoke(); };
        Canvas.SetLeft(b, a.X);
        Canvas.SetTop(b, a.Y);
        _canvas.Children.Add(b);
        _add = b;
    }

    /// <summary>A desktop's picture as the strip paints it (its wallpaper laid as on the screen), and its rectangle on the screen (pixels).</summary>
    public (Brush Brush, RECT Rect)? PictureOf(Guid desktop, double scale)
    {
        int i = _desktops.ToList().FindIndex(d => d.Id == desktop);
        if (i < 0 || _layout.Pictures == null || i >= _layout.Pictures.Count) return null;
        var p = _layout.Pictures[i];
        return (PictureBrush(_desktops[i]), new RECT((int)Math.Round((Left + p.X) * scale), (int)Math.Round((Top + p.Y) * scale), (int)Math.Round((Left + p.Right) * scale), (int)Math.Round((Top + p.Bottom) * scale)));
    }

    /// <summary>A desktop's wallpaper came after the strip was laid out.</summary>
    public void SetPicture(Guid desktop, ImageSource picture)
    {
        int i = _desktops.ToList().FindIndex(d => d.Id == desktop);
        if (i < 0 || i >= _pictures.Count) return;
        _desktops = _desktops.Select((d, k) => k == i ? d with { Picture = picture } : d).ToList();
        _pictures[i].Background = PictureBrush(_desktops[i]);
    }

    /// <summary>
    /// Draws each desktop's windows (top to bottom, their frames on the screen in pixels) in its picture as live DWM
    /// thumbnails of the screen <paramref name="monitor"/> made small (DWM shows another desktop's windows too: S17).
    /// </summary>
    public void ShowWindows(IReadOnlyDictionary<Guid, List<(IntPtr Hwnd, RECT Frame)>> windows, RECT monitor, double scale)
    {
        ClearWindows();
        if (_sliding) { _windowsLater = (windows, monitor, scale); return; } // once the pictures stand still
        var screen = new Rect2(monitor.Left, monitor.Top, monitor.Width, monitor.Height);
        for (int i = 0; i < _desktops.Count && i < _layout.Pictures.Count; i++)
        {
            if (!windows.TryGetValue(_desktops[i].Id, out var list)) continue;
            var p = _layout.Pictures[i];
            var inside = new Rect2(p.X * scale + 1, p.Y * scale + 1, p.Width * scale - 2, p.Height * scale - 2);
            for (int k = list.Count - 1; k >= 0; k--) // the bottom one first: the last registered is drawn on top
            {
                var (h, f) = list[k];
                var r = DesktopStrip.InPicture(new Rect2(f.Left, f.Top, f.Width, f.Height), screen, inside);
                if (r.Width < 2 || r.Height < 2 || Thumbnail.Create(Handle, h) is not { } t) continue;
                t.Show(new RECT((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Right), (int)Math.Round(r.Bottom)));
                _thumbs.Add(t);
                _shown.Add((_desktops[i].Id, h.ToInt64()));
            }
        }
    }

    public void ClearWindows()
    {
        foreach (var t in _thumbs) t.Dispose();
        _thumbs.Clear();
        _shown.Clear();
        _windowsLater = null;
        Ghost(IntPtr.Zero, null);
    }

    /// <summary>Hidden and small while the overview is closed (a hidden layered window keeps a bitmap of its size).</summary>
    public void Off()
    {
        ClearWindows();
        _slide++;
        _sliding = false;
        Fades.Reset(this, null);
        if (IsVisible) Hide();
        Width = 1; Height = 1; Left = -32000; Top = -32000;
    }

    /// <summary>Diagnostics: each picture's and the add button's rectangle on the screen (pixels).</summary>
    public (List<RECT> Pictures, RECT? Add) Rects(double scale)
    {
        RECT Px(Rect2 r) => new((int)Math.Round((Left + r.X) * scale), (int)Math.Round((Top + r.Y) * scale), (int)Math.Round((Left + r.Right) * scale), (int)Math.Round((Top + r.Bottom) * scale));
        return (_layout.Pictures?.Select(Px).ToList() ?? [], _layout.Add is { } a ? Px(a) : null);
    }
}
