using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Frostbar.Core.Shell;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Common;

/// <summary>
/// A press's light spilling onto a window's own glass drawn by WPF (the settings' cards, toolbar and sidebar, a dialog's
/// buttons): the settings window and the dialogs are DWM's Acrylic and have no composition plate for
/// <c>WindowBackdrop.SpillAt</c> to light (2026-10-23, the user's "A 沈む＋隣へ光る" there too). A part marked
/// <see cref="GlassProperty"/> within <see cref="GlassSpill.Reach"/> DIPs of the press glows at its point nearest it
/// and its rim lights there, as Core's GlassSpill says, over <see cref="Motion.Spill"/>. The part pressed lights by
/// its own (<see cref="PressLight"/>).
/// </summary>
public static class PressSpill
{
    public static readonly DependencyProperty GlassProperty = DependencyProperty.RegisterAttached("Glass", typeof(bool), typeof(PressSpill), new PropertyMetadata(false));
    public static void SetGlass(DependencyObject o, bool on) => o.SetValue(GlassProperty, on);
    public static bool GetGlass(DependencyObject o) => (bool)o.GetValue(GlassProperty);

    /// <summary>The lights showing now (diagnostics).</summary>
    public static int Showing { get; private set; }

    /// <summary>The parts of <paramref name="w"/> marked as glass round <paramref name="at"/> (its DIPs) lit; how many.</summary>
    public static int At(Window w, Point at)
    {
        int lit = 0;
        foreach (var b in Marked(w))
        {
            if (!b.IsVisible || b.ActualWidth <= 0 || b.ActualHeight <= 0 || AdornerLayer.GetAdornerLayer(b) is not { } layer) continue;
            Rect r;
            try { r = b.TransformToAncestor(w).TransformBounds(new Rect(0, 0, b.ActualWidth, b.ActualHeight)); }
            catch (InvalidOperationException) { continue; } // not under the window (a popup's)
            double radius = Math.Min(Radius(b), Math.Min(r.Width, r.Height) / 2);
            var (nx, ny, d) = GlassSpill.Nearest(new RoundedBox(r.X, r.Y, r.Width, r.Height, radius), at.X, at.Y);
            if (d <= 0) continue; // the press is on it: its own light, or a row's on it
            double strength = GlassSpill.Strength(d);
            if (strength <= 0) continue;
            var glow = new Glow(b, Radius(b), w.TranslatePoint(new Point(nx, ny), b), strength);
            layer.Add(glow);
            Showing++;
            Tween.To(glow, Glow.ProgressProperty, 1, Motion.Spill, Tween.Linear, () => { layer.Remove(glow); Showing--; });
            lit++;
        }
        return lit;
    }

    static IEnumerable<FrameworkElement> Marked(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement e && GetGlass(e)) yield return e;
            foreach (var inner in Marked(child)) yield return inner;
        }
    }

    /// <summary>A part's corners: a border's own, or its template's first border's (a button).</summary>
    static double Radius(FrameworkElement e)
    {
        if (e is Border b) return b.CornerRadius.TopLeft;
        for (var d = (DependencyObject)e; VisualTreeHelper.GetChildrenCount(d) > 0;)
        {
            d = VisualTreeHelper.GetChild(d, 0);
            if (d is Border inner) return inner.CornerRadius.TopLeft;
        }
        return 0;
    }

    sealed class Glow : Adorner
    {
        public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(Glow),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

        readonly FrameworkElement _part;
        readonly double _radius;
        readonly Point _at;
        readonly double _strength;

        public Glow(FrameworkElement part, double radius, Point at, double strength) : base(part)
        {
            _part = part;
            _radius = radius;
            _at = at;
            _strength = strength;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double s = _strength * GlassSpill.Envelope(Progress);
            for (DependencyObject? o = _part; o != null; o = VisualTreeHelper.GetParent(o)) if (o is UIElement u) s *= u.IsVisible ? u.Opacity : 0;
            if (s <= 0.004) return;
            var size = new Rect(0, 0, _part.ActualWidth, _part.ActualHeight);
            double r = Math.Min(_radius, Math.Min(size.Width, size.Height) / 2);
            Brush Radial(double alpha, double radius) => new RadialGradientBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), 255, 255, 255), Color.FromArgb(0, 255, 255, 255))
            {
                MappingMode = BrushMappingMode.Absolute, Center = _at, GradientOrigin = _at, RadiusX = radius, RadiusY = radius,
            };
            dc.DrawRoundedRectangle(Radial(GlassSpill.Glow * s, GlassSpill.GlowRadius), null, size, r, r);
            var inner = new Rect(0.6, 0.6, Math.Max(0, size.Width - 1.2), Math.Max(0, size.Height - 1.2));
            dc.DrawRoundedRectangle(null, new Pen(Radial(GlassSpill.Rim * s, GlassSpill.GlowRadius * 1.4), 1.2), inner, Math.Max(0, r - 0.6), Math.Max(0, r - 0.6));
        }
    }
}
