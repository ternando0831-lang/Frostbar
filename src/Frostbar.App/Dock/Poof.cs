using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Frostbar.App.Common;
using Frostbar.Platform.Interop;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Dock;

/// <summary>
/// macOS's puff of smoke where an item dragged off the Dock is let go: a few soft discs that swell outwards and fade
/// (a third of a second), in a small window that takes no clicks and closes itself. It moves by the display's frames
/// (Tween), from once the window has been drawn: it ran on WPF's clocks, begun before the window was made, and lost
/// its first frames to the making (2026-10-19, the user's "A5").
/// </summary>
public sealed class Poof : Window
{
    const double Size = 112, Ms = Motion.Poof;

    /// <summary>Puffs shown so far, and puffs still swelling (diagnostics).</summary>
    public static int Puffs { get; private set; }
    public static int Puffing { get; private set; }

    /// <summary>Puffs at <paramref name="centreDip"/> (screen DIPs).</summary>
    public static void At(Point centreDip, bool dark) => new Poof(dark) { Left = centreDip.X - Size / 2, Top = centreDip.Y - Size / 2 }.Show();

    static readonly DependencyProperty ProgressProperty = DependencyProperty.Register("Progress", typeof(double), typeof(Poof),
        new PropertyMetadata(0.0, (d, e) => ((Poof)d).Step((double)e.NewValue)));

    // A large disc in the middle and smaller ones around it, each drifting a little outwards as it swells.
    static readonly (double x, double y, double r)[] Discs = [(0, 0, 24), (-19, -9, 17), (18, -12, 18), (-14, 14, 16), (16, 13, 15), (2, -21, 14)];
    readonly List<(Ellipse disc, ScaleTransform scale, TranslateTransform move, double x, double y)> _discs = new();

    Poof(bool dark)
    {
        Title = "Frostbar.Poof";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = Height = Size;
        IsHitTestVisible = false;
        var canvas = new Canvas();
        var c = dark ? Color.FromRgb(0xE8, 0xE8, 0xEC) : Color.FromRgb(0xFA, 0xFA, 0xFC);
        foreach (var (x, y, r) in Discs)
        {
            var brush = new RadialGradientBrush(Color.FromArgb(0xF0, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B)) { GradientOrigin = new Point(0.45, 0.4) };
            brush.GradientStops.Insert(1, new GradientStop(Color.FromArgb(0xC8, c.R, c.G, c.B), 0.55));
            var disc = new Ellipse { Width = 2 * r, Height = 2 * r, Fill = brush, RenderTransformOrigin = new Point(0.5, 0.5) };
            Canvas.SetLeft(disc, Size / 2 + x - r);
            Canvas.SetTop(disc, Size / 2 + y - r);
            var scale = new ScaleTransform();
            var move = new TranslateTransform();
            disc.RenderTransform = new TransformGroup { Children = { scale, move } };
            canvas.Children.Add(disc);
            _discs.Add((disc, scale, move, x, y));
        }
        Content = canvas;
        Step(0);
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(h, Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW);
        };
        Puffs++;
        Puffing++;
        // Once drawn (after WPF's render pass): its whole third of a second on the screen.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Tween.To(this, ProgressProperty, 1.0, Ms, Tween.Linear, () => { Puffing--; Close(); }));
    }

    /// <summary>The discs at <paramref name="t"/> of the way: swelled 0.35 to 1.2 and drifted out (cubic out), faded (cubic in).</summary>
    void Step(double t)
    {
        double grow = Tween.Out(t), fade = t * t * t;
        foreach (var (disc, scale, move, x, y) in _discs)
        {
            scale.ScaleX = scale.ScaleY = 0.35 + (1.2 - 0.35) * grow;
            move.X = x * 0.45 * grow;
            move.Y = y * 0.45 * grow;
            disc.Opacity = 1 - fade;
        }
    }
}
