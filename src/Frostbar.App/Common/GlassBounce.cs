using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Frostbar.Core.Shell;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Common;

/// <summary>
/// macOS 27's glass that gives under a press and springs back (the user's "A", 2026-10-18: "弾むほうが見栄えいいかも
/// しれない"; <c>Lab mock motion</c>): a part shrinks to <see cref="Pressed"/> while held and, let go, springs back past its
/// size and settles, as one spring (<see cref="Damping"/>: from <see cref="Pressed"/> to about 1.005; to about 1.035 and under to 0.99 until 2026-11-01).
/// Its piece of glass follows (the pieces are placed from the parts' transforms at each frame). Moved by
/// <see cref="Tween"/>, across and down as one movement: one for each made a circle 68 by 70 in some frames of a bounce
/// (2026-10-18). It went past and back in three eased steps first, and moved stiffly (the user's "なんか動きが硬い").
/// </summary>
public static class GlassBounce
{
    public const double Pressed = PressGive.Glass, Damping = PressGive.Damping, PressMs = Motion.BouncePress, ReleaseMs = Motion.BounceRelease;

    /// <summary>
    /// Gives <paramref name="e"/> the bounce, when <paramref name="animate"/> says so at the press, down to
    /// <paramref name="depth"/> (Core's PressGive: a piece of glass 0.90, a button 0.96, a picture 0.95; 2026-10-23,
    /// the user's "A 沈む＋隣へ光る" everywhere).
    /// </summary>
    public static void Attach(FrameworkElement e, Func<bool> animate, double depth = Pressed)
    {
        // The gone ones dropped as it grows (it was only trimmed when the diagnostics read it: one handle more for each
        // part of each panel ever opened, 2026-11-06 review).
        if (Presses.Count >= _trimAt) { Presses.RemoveAll(w => !w.TryGetTarget(out _)); _trimAt = Math.Max(256, Presses.Count * 2); }
        Presses.Add(new WeakReference<FrameworkElement>(e));
        e.SetValue(DepthProperty, depth);
        ScaleTransform? scale = null;
        bool held = false;
        ScaleTransform Scale()
        {
            // Its own scale about its middle; a transform put on it since (a card sliding away) is left alone.
            if (scale != null && ReferenceEquals(e.RenderTransform, scale)) return scale;
            scale = new ScaleTransform(1, 1);
            e.RenderTransformOrigin = new Point(0.5, 0.5);
            if (e.RenderTransform is null || e.RenderTransform == Transform.Identity) e.RenderTransform = scale;
            return scale;
        }
        // What was pressed keeps the mouse until it is let go. Shrinking moved it from under the pointer, and the release
        // went to what was under the pointer then: a card's ×, held as a person holds a click, gave the card's click and
        // opened its app (the user's "通知が個々に消せない", 2026-10-19, nc-dismiss), and a press near a part's edge was
        // let go off it. Where the pointer is counts against the part as it was before the press.
        UIElement? pressed = null;
        bool off = false;
        e.PreviewMouseLeftButtonDown += (_, a) =>
        {
            if (!animate() || ReduceMotion.Calms("bounce")) return; // reduced motion: the press's light only, no give
            var s = Scale();
            if (!ReferenceEquals(e.RenderTransform, s)) return;
            held = true;
            off = false;
            // A button holds the mouse itself (and clicks only when let go on it): taken by the text inside it, the
            // button's own capture took it away again and the press sprang back at once.
            pressed = e is ButtonBase ? null : InputOf(a.OriginalSource);
            if (pressed != null && !pressed.CaptureMouse()) pressed = null;
            Tween.To(s, depth, PressMs, Tween.Out);
        };
        void Let()
        {
            if (!held || scale == null) return;
            held = false;
            Release(scale);
        }
        // Moved off it while held, it springs back (macOS's button lets go of its highlight); back on it, it gives again.
        e.PreviewMouseMove += (_, a) =>
        {
            if (pressed == null && !(e is ButtonBase && (held || off)) || scale == null || a.LeftButton != MouseButtonState.Pressed) return;
            bool on = Within(e, a);
            if (!on && held) { Let(); off = true; }
            else if (on && off) { off = false; held = true; Tween.To(scale, depth, PressMs, Tween.Out); }
        };
        e.PreviewMouseLeftButtonUp += (_, a) =>
        {
            // Let go off it: no click. On it: the click goes to what was pressed (the mouse is still its).
            if (pressed != null && !Within(e, a)) a.Handled = true;
            Let();
            // Free once this release has gone its way (a handled preview raises no click to free it after).
            var p = pressed;
            pressed = null;
            off = false;
            if (p != null) e.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => { if (ReferenceEquals(Mouse.Captured, p)) p.ReleaseMouseCapture(); });
        };
        e.MouseLeave += (_, _) => Let(); // not held by a press of its own (the mouse is free)
        e.LostMouseCapture += (_, _) => { Let(); pressed = null; off = false; };
    }

    /// <summary>The element that takes the mouse for a press on <paramref name="source"/> (a run of text: its text block).</summary>
    static UIElement? InputOf(object source)
    {
        for (var d = source as DependencyObject; d != null; d = d is Visual ? null : LogicalTreeHelper.GetParent(d))
            if (d is UIElement u) return u;
        return null;
    }

    /// <summary>The pointer on <paramref name="e"/> as laid out, its scale about its middle taken off.</summary>
    static bool Within(FrameworkElement e, MouseEventArgs a)
    {
        var p = a.GetPosition(e); // in its own space: through its scale
        if (e.RenderTransform is ScaleTransform s)
        {
            double cx = e.ActualWidth * e.RenderTransformOrigin.X, cy = e.ActualHeight * e.RenderTransformOrigin.Y;
            p = new Point(cx + (p.X - cx) * s.ScaleX, cy + (p.Y - cy) * s.ScaleY);
        }
        return p.X >= 0 && p.Y >= 0 && p.X <= e.ActualWidth && p.Y <= e.ActualHeight;
    }

    /// <summary>Let go: from where it is back to its size on the spring (diagnostics "cc-bounce" too, no press).</summary>
    public static void Release(ScaleTransform s) => Tween.To(s, 1, ReleaseMs, Tween.Spring(Damping));

    /// <summary>How far a part with the bounce gives (<see cref="Attach"/>'s depth).</summary>
    public static readonly DependencyProperty DepthProperty = DependencyProperty.RegisterAttached("Depth", typeof(double), typeof(GlassBounce), new PropertyMetadata(1.0));
    public static double GetDepth(DependencyObject o) => (double)o.GetValue(DepthProperty);

    static readonly List<WeakReference<FrameworkElement>> Presses = new();
    static int _trimAt = 256;

    /// <summary>
    /// For tests and photos (diagnostics "press-at"): the part with the bounce under <paramref name="hit"/> given as a
    /// press gives, without a press: down to its depth now, back on the spring after <paramref name="holdMs"/>. Returns
    /// the part, or null where none is.
    /// </summary>
    public static FrameworkElement? Give(DependencyObject hit, double holdMs)
    {
        Presses.RemoveAll(w => !w.TryGetTarget(out _));
        for (var d = hit; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is not FrameworkElement e || !Presses.Any(w => w.TryGetTarget(out var x) && ReferenceEquals(x, e))) continue;
            if (e.RenderTransform is not ScaleTransform { IsFrozen: false } s)
            {
                s = new ScaleTransform(1, 1);
                e.RenderTransformOrigin = new Point(0.5, 0.5);
                e.RenderTransform = s;
            }
            var held = s;
            Tween.To(held, GetDepth(e), PressMs, Tween.Out, () =>
            {
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(0, holdMs - PressMs)) };
                t.Tick += (_, _) => { t.Stop(); Release(held); };
                t.Start();
            });
            return e;
        }
        return null;
    }

    /// <summary>How many parts are held or still springing back (diagnostics).</summary>
    public static int Moving(IEnumerable<FrameworkElement> parts) =>
        parts.Count(p => p.RenderTransform is ScaleTransform s && (Tween.Moving(s, ScaleTransform.ScaleXProperty) || Math.Abs(s.ScaleX - 1) > 0.001));
}
