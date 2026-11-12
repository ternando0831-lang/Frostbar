using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Frostbar.App.Common;

/// <summary>
/// The keys in a panel that never takes the keyboard (the Control Center, the Notification Center, the tray's », a Dock
/// stack: they open without coming to the front, and Esc was the only key they knew: "D2", 2026-10-19). A part that does
/// something on a click is marked with what it does (<see cref="SetRun"/>); while the panel is open the shell takes the
/// arrows, Tab, Space and Enter (as an open menu does), an arrow goes to the nearest marked part that way, Tab to the
/// next in order, and Space or Enter runs the one chosen. The part chosen wears a ring of the accent.
/// </summary>
public static class KeyNav
{
    public static readonly DependencyProperty RunProperty =
        DependencyProperty.RegisterAttached("Run", typeof(Action), typeof(KeyNav), new PropertyMetadata(null));

    /// <summary>Marks <paramref name="part"/>: what Space or Enter does when the keys are on it (null: not a stop).</summary>
    public static void SetRun(DependencyObject part, Action? run) => part.SetValue(RunProperty, run);
    public static Action? GetRun(DependencyObject part) => (Action?)part.GetValue(RunProperty);

    /// <summary>The marked parts under <paramref name="root"/> that show now, in the tree's order.</summary>
    public static List<FrameworkElement> Parts(DependencyObject root)
    {
        var list = new List<FrameworkElement>();
        void Walk(DependencyObject d)
        {
            if (d is UIElement { IsVisible: false }) return;
            if (d is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0, IsEnabled: true } f && GetRun(f) != null) list.Add(f);
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return list;
    }

    /// <summary>
    /// The part to go to from <paramref name="from"/> with an arrow (<paramref name="dx"/>, <paramref name="dy"/>): the
    /// nearest whose middle lies that way, sideways distance counting double; the first part when none is chosen.
    /// </summary>
    public static FrameworkElement? Next(IReadOnlyList<FrameworkElement> parts, FrameworkElement? from, int dx, int dy, Visual root)
    {
        if (parts.Count == 0) return null;
        if (from == null || !parts.Contains(from)) return parts[0];
        Point Mid(FrameworkElement e)
        {
            try { return e.TransformToAncestor(root).Transform(new Point(e.ActualWidth / 2, e.ActualHeight / 2)); }
            catch (InvalidOperationException) { return new Point(double.NaN, double.NaN); }
        }
        var at = Mid(from);
        FrameworkElement? best = null;
        double bestScore = double.MaxValue;
        foreach (var p in parts)
        {
            if (ReferenceEquals(p, from)) continue;
            var m = Mid(p);
            double along = dx != 0 ? (m.X - at.X) * dx : (m.Y - at.Y) * dy;
            double aside = dx != 0 ? Math.Abs(m.Y - at.Y) : Math.Abs(m.X - at.X);
            if (double.IsNaN(along) || along <= 1) continue;
            double score = along + 2 * aside;
            if (score < bestScore) { bestScore = score; best = p; }
        }
        return best ?? from;
    }

    /// <summary>The ring around the part the keys are on.</summary>
    public sealed class Ring : Adorner
    {
        readonly Pen _pen;
        readonly double _radius;

        public Ring(FrameworkElement part, Color accent) : base(part)
        {
            IsHitTestVisible = false;
            _pen = new Pen(new SolidColorBrush(Color.FromArgb(0x99, accent.R, accent.G, accent.B)), 3);
            _pen.Freeze();
            double r = part is System.Windows.Controls.Border b ? b.CornerRadius.TopLeft : 6;
            _radius = Math.Min(r, Math.Min(part.ActualWidth, part.ActualHeight) / 2) + 2.5;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var e = (FrameworkElement)AdornedElement;
            var r = new Rect(-2.5, -2.5, e.ActualWidth + 5, e.ActualHeight + 5);
            dc.DrawRoundedRectangle(null, _pen, r, _radius, _radius);
        }
    }
}
