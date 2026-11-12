using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Frostbar.App.Common;
using Frostbar.Platform.Dwm;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Overview;

/// <summary>
/// The window overview's window (M7, Mission Control): over the monitor below the menu bar (not the whole monitor, or
/// Explorer would take it for a fullscreen app), activated to take Esc, the arrows and Enter. Its own content is all
/// but invisible (it takes the clicks) except, for the window under the pointer, an accent frame and a label with the
/// app's icon and the window's title. The windows themselves are DWM thumbnails drawn above it; behind them the desktop
/// is blurred and darkened by its backdrop window (the real windows stay where they are under it, so they must not
/// show through clearly), faded in and out by DWM.
/// </summary>
public sealed class OverviewWindow : Window
{
    readonly WindowBackdrop _backdrop;
    readonly Canvas _canvas = new();
    readonly Border _frame, _label;
    readonly TextBlock _title = new() { Foreground = Brushes.White, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly Image _icon = new() { Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _empty = new() { Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), FontSize = 15, Visibility = Visibility.Collapsed };

    public IntPtr Handle => new WindowInteropHelper(this).Handle;
    public IntPtr BackdropHandle => _backdrop.BackdropHandle;
    /// <summary>The frame's rectangle in DIPs while a window is pointed at (diagnostics).</summary>
    public Rect? Hovered { get; private set; }

    /// <summary>The frame of the window under the pointer in Windows' accent colour (it changes with the theme).</summary>
    public void SetAccent(Color accent) => _frame.BorderBrush = new SolidColorBrush(accent);

    public OverviewWindow(Material material, Color accent)
    {
        Title = "Frostbar.Overview";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        FontFamily = Materials.UiFont;
        UseLayoutRounding = true;
        Language = XmlLanguage.GetLanguage("ja-JP");
        Width = 1; Height = 1; Left = -32000; Top = -32000; // placed when it opens

        _frame = new Border
        {
            BorderBrush = new SolidColorBrush(accent), BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(10),
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        // A DockPanel, not a horizontal StackPanel: that gives the title all the width it asks for, so a long one was cut
        // off at the label's edge without its ellipsis.
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_icon, System.Windows.Controls.Dock.Left);
        row.Children.Add(_icon);
        row.Children.Add(_title);
        _label = new Border
        {
            Child = row, Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x28, 0x28, 0x2C)), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 10, 3), Visibility = Visibility.Collapsed, IsHitTestVisible = false, MaxWidth = 420,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
        };
        _canvas.Children.Add(_frame);
        _canvas.Children.Add(_label);
        _canvas.Children.Add(_empty);
        Content = _canvas;

        _backdrop = new WindowBackdrop(this, material) { TakesSpill = false }; // the ground under the windows
        _backdrop.FadePlate(0, 0);
    }

    public void SetMaterial(Material m) => _backdrop.SetMaterial(m);

    public void Place(Rect dip)
    {
        Left = dip.Left;
        Top = dip.Top;
        Width = dip.Width;
        Height = dip.Height;
    }

    /// <summary>While closed it keeps a 1×1 window (a hidden screen-sized layered window still holds its bitmap).</summary>
    public void Shrink() => Place(new Rect(-32000, -32000, 1, 1));

    /// <summary>Fades the blurred backdrop in (<paramref name="on"/>) or out over <paramref name="ms"/>.</summary>
    public void Dim(bool on, double ms) => _backdrop.FadePlate(on ? 1f : 0f, ms);

    /// <summary>The shell's animation setting (the frame and the label fade in and out with it on).</summary>
    public Func<bool> Animate { get; set; } = () => true;

    /// <summary>
    /// How long the frame and the label take to fade in at a window and out (ms), and the strength a frame moved to
    /// another window starts from: they came and went at once (2026-10-19, the user's "B11").
    /// </summary>
    public const double PointInMs = Motion.OverviewPointIn, PointOutMs = Motion.OverviewPointOut, MovedFrom = 0.35;

    /// <summary>Times the frame faded in (diagnostics).</summary>
    public int PointFades { get; private set; }

    /// <summary>The frame's opacity now (diagnostics: it fades in).</summary>
    public double FrameOpacity => _frame.Visibility == Visibility.Visible ? _frame.Opacity : 0;

    /// <summary>Frames the thumbnail at <paramref name="thumb"/> (DIPs in the window) and labels it; null hides both.</summary>
    public void Point(Rect? thumb, string title, ImageSource? icon)
    {
        var was = Hovered;
        Hovered = thumb;
        if (thumb is not { } r)
        {
            if (was == null || !Animate() || _frame.Visibility != Visibility.Visible)
            {
                Tween.Stop(_frame, OpacityProperty);
                Tween.Stop(_label, OpacityProperty);
                _frame.Visibility = _label.Visibility = Visibility.Collapsed;
                return;
            }
            Tween.Together(PointOutMs, [new(_frame, OpacityProperty, 0.0, Tween.SineInOut), new(_label, OpacityProperty, 0.0, Tween.SineInOut)], () =>
            {
                if (Hovered == null) _frame.Visibility = _label.Visibility = Visibility.Collapsed;
            });
            return;
        }
        Canvas.SetLeft(_frame, r.Left - 5);
        Canvas.SetTop(_frame, r.Top - 5);
        _frame.Width = r.Width + 10;
        _frame.Height = r.Height + 10;
        _frame.Visibility = Visibility.Visible;
        _title.Text = title;
        _icon.Source = icon;
        _icon.Visibility = icon != null ? Visibility.Visible : Visibility.Collapsed;
        _label.Visibility = Visibility.Visible;
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _label.DesiredSize;
        double x = Math.Clamp(r.Left + (r.Width - size.Width) / 2, 8, Math.Max(8, Width - size.Width - 8));
        double y = Math.Min(r.Bottom + 7, Height - size.Height - 8); // in the gap under it (the frame takes 5)
        Canvas.SetLeft(_label, x);
        Canvas.SetTop(_label, y);
        if (!Animate())
        {
            Tween.Stop(_frame, OpacityProperty);
            Tween.Stop(_label, OpacityProperty);
            _frame.Opacity = _label.Opacity = 1;
            return;
        }
        if (was == r) return;
        // Coming from nothing: from nothing. From another window: it is there at once, a little faint, and strengthens
        // (a frame fading from nothing between two windows next to each other flickered).
        bool shown = was != null && _frame.Opacity > 0;
        _frame.Opacity = _label.Opacity = shown ? Math.Min(_frame.Opacity, MovedFrom) : 0;
        Tween.Together(PointInMs, [new(_frame, OpacityProperty, 1.0), new(_label, OpacityProperty, 1.0)]);
        PointFades++;
    }

    /// <summary>
    /// A line in the middle of <paramref name="area"/> (DIPs in the window: where the windows would go, above the Dock and
    /// under the desktops' strip) when there is no window to show. It stood in the middle of the whole window, the Dock's
    /// room with it (2026-10-12 review, done 2026-10-19).
    /// </summary>
    public void Empty(string? text, Rect area)
    {
        _empty.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        if (text == null) return;
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0) area = new Rect(0, 0, Width, Height);
        _empty.Text = text;
        _empty.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_empty, area.Left + (area.Width - _empty.DesiredSize.Width) / 2);
        Canvas.SetTop(_empty, area.Top + (area.Height - _empty.DesiredSize.Height) / 2);
    }

    Border? _zoom;

    /// <summary>
    /// A desktop's picture growing from <paramref name="from"/> to <paramref name="to"/> (DIPs in the window) over
    /// <paramref name="ms"/>, under the windows' thumbnails (they are drawn over the window's content), then
    /// <paramref name="done"/> (E2: going to a desktop zooms into its picture).
    /// </summary>
    public void Zoom(Brush picture, Rect from, Rect to, double ms, Action done)
    {
        ClearZoom();
        var z = _zoom = new Border { Background = picture, CornerRadius = new CornerRadius(5), IsHitTestVisible = false, Width = from.Width, Height = from.Height };
        Canvas.SetLeft(z, from.Left);
        Canvas.SetTop(z, from.Top);
        _canvas.Children.Insert(0, z);
        Tween.Together(ms, [new(z, Canvas.LeftProperty, to.Left), new(z, Canvas.TopProperty, to.Top), new(z, WidthProperty, to.Width), new(z, HeightProperty, to.Height)], done);
    }

    public void ClearZoom()
    {
        if (_zoom == null) return;
        Tween.Stop(_zoom, Canvas.LeftProperty);
        _canvas.Children.Remove(_zoom);
        _zoom = null;
    }

    /// <summary>The empty line's place and size in the window (DIPs; null: not shown). Diagnostics.</summary>
    public Rect? EmptyAt => _empty.Visibility == Visibility.Visible ? new Rect(Canvas.GetLeft(_empty), Canvas.GetTop(_empty), _empty.DesiredSize.Width, _empty.DesiredSize.Height) : null;

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _backdrop.Dispose();
    }
}
