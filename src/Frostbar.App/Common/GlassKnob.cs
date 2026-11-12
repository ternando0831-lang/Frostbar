using System.Windows;
using System.Windows.Media;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Common;

/// <summary>
/// Liquid Glass's knob (the user's "B", 2026-10-18): while a slider or a switch is held, its white knob turns into a
/// small lens, 1.35 times as large, of clear glass (white 25 %, a lit rim, a soft shadow) through which the track under
/// it shows magnified 1.5 times. A switch's knob stretches sideways into a pill. Only with Liquid Glass on.
/// </summary>
public static class GlassKnob
{
    /// <summary>Liquid Glass is on and shows (the shell sets it at start; read at each press).</summary>
    public static Func<bool> Enabled { get; set; } = () => false;

    /// <summary>The shell's animations are on (for the knobs that are not told it when made).</summary>
    public static Func<bool> Animate { get; set; } = () => true;

    public const double Grow = 1.35, Magnify = 1.5, PressMs = Motion.KnobPress, LetGoMs = Motion.KnobLetGo;

    /// <summary>
    /// For photos (diagnostics "glass-knobs"): every knob drawn this far turned to glass, without a press; null as they
    /// are. The knobs on screen are drawn again.
    /// </summary>
    public static double? Forced
    {
        get => _forced;
        set
        {
            _forced = value;
            foreach (Window w in Application.Current.Windows) Redraw(w);
        }
    }
    static double? _forced;

    static void Redraw(DependencyObject o)
    {
        if (o is PillSwitch or PillSlider or MacSlider) ((UIElement)o).InvalidateVisual();
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(o); i < n; i++) Redraw(VisualTreeHelper.GetChild(o, i));
    }

    /// <summary>How far a knob shows as glass: its press, or the photo's.</summary>
    public static double Shown(double press) => _forced ?? press;

    /// <summary>How many knobs have turned to glass (diagnostics), and how many are held now.</summary>
    public static int Pressed { get; set; }
    public static int Held { get; set; }

    static readonly Brush Fill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)));
    static readonly Brush Shade = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)));
    static readonly Pen Rim = FrozenPen(new LinearGradientBrush(Color.FromArgb(0xF0, 255, 255, 255), Color.FromArgb(0x40, 255, 255, 255), 90), 1.6);

    static Brush Frozen(Brush b) { b.Freeze(); return b; }
    static Pen FrozenPen(Brush b, double w) { b.Freeze(); var p = new Pen(b, w); p.Freeze(); return p; }

    /// <summary>
    /// The glass knob <paramref name="p"/> of the way in (0 nothing, 1 all), a rounded rectangle <paramref name="r"/>
    /// (a circle when its sides are equal): its shadow, what <paramref name="under"/> draws magnified about the knob's
    /// centre, the glass and its rim. The resting knob is drawn by the caller, faded by 1 − p.
    /// </summary>
    public static void Draw(DrawingContext dc, Rect r, double p, Action<DrawingContext> under)
    {
        if (p <= 0) return;
        double radius = Math.Min(r.Width, r.Height) / 2;
        var centre = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        dc.PushOpacity(p);
        dc.DrawRoundedRectangle(Shade, null, new Rect(r.X - 1, r.Y + 1 + 2 * p, r.Width + 2, r.Height + 2), radius + 1, radius + 1);
        dc.PushClip(new RectangleGeometry(r, radius, radius));
        double m = 1 + (Magnify - 1) * p;
        dc.PushTransform(new ScaleTransform(m, m, centre.X, centre.Y));
        under(dc);
        dc.Pop();
        dc.Pop();
        dc.DrawRoundedRectangle(Fill, null, r, radius, radius);
        var inner = new Rect(r.X + 0.8, r.Y + 0.8, Math.Max(0, r.Width - 1.6), Math.Max(0, r.Height - 1.6));
        dc.DrawRoundedRectangle(null, Rim, inner, radius - 0.8, radius - 0.8);
        dc.Pop();
    }

    /// <summary>A round knob of radius <paramref name="radius"/> at <paramref name="centre"/>, grown by the press <paramref name="p"/>.</summary>
    public static Rect Round(Point centre, double radius, double p)
    {
        double k = radius * (1 + (Grow - 1) * p);
        return new Rect(centre.X - k, centre.Y - k, 2 * k, 2 * k);
    }

    /// <summary>A switch's knob stretched sideways: 3.2 radii wide and 2.6 high when all the way in.</summary>
    public static Rect Pill(Point centre, double radius, double p)
    {
        double w = radius * (2 + 1.2 * p), h = radius * (2 + 0.6 * p);
        return new Rect(centre.X - w / 2, centre.Y - h / 2, w, h);
    }

    // ---- stretched by its speed (2026-11-10; Core's KnobStretch) -------------------------------------------------------

    /// <summary>How far a slider's knob is stretched along its way (0: round); drawn again when it changes.</summary>
    public static readonly DependencyProperty StretchProperty = DependencyProperty.RegisterAttached("Stretch", typeof(double), typeof(GlassKnob),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static double StretchOf(UIElement slider) => ForcedStretch ?? (double)slider.GetValue(StretchProperty);

    /// <summary>For photos (diagnostics "glass-knobs <press> <stretch>"): every knob stretched this far; null as they are. Set before <see cref="Forced"/> (which draws them again).</summary>
    public static double? ForcedStretch { get; set; }

    /// <summary>The most a drag has stretched a knob (diagnostics "glass-light").</summary>
    public static double StretchMost { get; set; }

    sealed class Drag { public double X = double.NaN; public long At; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UIElement, Drag> Drags = new();

    /// <summary>How many moves have stretched a knob (diagnostics "glass-light").</summary>
    public static int Stretched { get; set; }

    /// <summary>
    /// The pointer dragging <paramref name="slider"/>'s knob is at <paramref name="x"/> (DIPs along its track): the
    /// knob stretches by its speed since the last move, and springs back round from there (each move starts the spring
    /// again, so it settles once the pointer rests). Nothing without the glass knob, with animations off, or with
    /// Reduce Motion.
    /// </summary>
    public static void Dragged(UIElement slider, double x)
    {
        var d = Drags.GetOrCreateValue(slider);
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double ms = double.IsNaN(d.X) ? double.NaN : System.Diagnostics.Stopwatch.GetElapsedTime(d.At, now).TotalMilliseconds;
        double dx = x - d.X;
        d.X = x;
        d.At = now;
        // The first move of a drag, or one after a rest: no speed to tell yet.
        if (double.IsNaN(ms) || ms > 120 || ms < 0.5 || !Enabled() || !Animate()) return;
        if (ReduceMotion.Calms("knobStretch")) return;
        double shown = Frostbar.Core.Shell.KnobStretch.Towards(StretchOf(slider), dx / ms);
        Tween.Stop(slider, StretchProperty);
        slider.SetValue(StretchProperty, shown);
        Stretched++;
        StretchMost = Math.Max(StretchMost, shown);
        Tween.To(slider, StretchProperty, 0, Motion.KnobSettle, Tween.Spring(Motion.KnobSettleDamping));
    }

    /// <summary>A drag begins or ends: the next move has no speed.</summary>
    public static void DragEnds(UIElement slider)
    {
        if (Drags.TryGetValue(slider, out var d)) d.X = double.NaN;
    }

    /// <summary>A round knob's rectangle stretched along its way by <paramref name="stretch"/>, about its middle.</summary>
    public static Rect Along(Rect r, double stretch)
    {
        if (stretch == 0) return r;
        var (wide, high) = Frostbar.Core.Shell.KnobStretch.Size(stretch);
        double w = r.Width * wide, h = r.Height * high;
        return new Rect(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
    }
}
