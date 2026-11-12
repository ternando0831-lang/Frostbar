using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Frostbar.Platform.Dwm;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Common;

/// <summary>
/// Short movements of WPF properties (a switch's knob and colour, a segmented control's raised part, a row's fade, a
/// card going) set at each display frame by one display-paced clock (FrameClock), each step placed by the refresh it is
/// shown at (<see cref="DisplaySteps"/>). They were WPF's own animations, on WPF's frame clock, which comes out of its
/// rest unevenly: a round switch's 0.15 s colour change showed its first two pictures 33 ms apart (2026-10-17,
/// frame-pace). A movement goes from where the property is; a new one for the same property takes over from there.
/// </summary>
public static class Tween
{
    /// <summary>Easing: cubic out (quick to start), cubic in and out, sine in and out.</summary>
    public static readonly Func<double, double>
        Out = Motion.Out,
        InOut = Motion.InOut,
        SineInOut = Motion.SineInOut,
        Linear = t => t;

    /// <summary>A spring let go (Core's DockMotion.Spring): past its mark by its <paramref name="damping"/>, back, settled at its end.</summary>
    // Reduced motion (accessibility): no overshoot, the plain ease out.
    public static Func<double, double> Spring(double damping) => ReduceMotion.Calms("spring") ? Out : t => Motion.Spring(t, damping);

    sealed record Run((DependencyObject Target, DependencyProperty Property)[] Moved, Action<double> Apply, double Ms, Func<double, double> Ease, Action? Done)
    {
        public DisplaySteps Steps { get; } = new();
        public bool Moves(object o, DependencyProperty p) => Moved.Any(m => ReferenceEquals(m.Target, o) && m.Property == p);
    }

    static readonly List<Run> Running = new();
    static FrameClock? _clock;

    /// <summary>Moves <paramref name="o"/>'s <paramref name="p"/> (a double) from where it is to <paramref name="to"/>.</summary>
    public static void To(DependencyObject o, DependencyProperty p, double to, double ms, Func<double, double>? ease = null, Action? done = null)
    {
        double from = Take(o, p) is double d ? d : to;
        Start(new Run([(o, p)], k => o.SetValue(p, from + (to - from) * k), ms, ease ?? Out, done));
    }

    /// <summary>A property moved with others in one movement (<see cref="Together"/>): to <paramref name="To"/> (a double or a <see cref="Thickness"/>), on its own ease, over its share of the time.</summary>
    public readonly record struct Part(DependencyObject Target, DependencyProperty Property, object To, Func<double, double>? Ease = null, double Share = 1);

    /// <summary>
    /// Values that must agree moved as one movement: a segmented control's raised part's place and width, a card going
    /// (its slide, fade, margins and height). One movement for each was a refresh apart from another in some frames
    /// (the bouncing circle 68 by 70, 2026-10-18; Core's RefreshSteps). Each part from where it is, on its own ease
    /// (cubic out when none), over its share of <paramref name="ms"/> (a fade done in 0.7 of it).
    /// </summary>
    public static void Together(double ms, IReadOnlyList<Part> parts, Action? done = null)
    {
        var moves = new List<Action<double>>();
        foreach (var p in parts)
        {
            var (o, prop, ease, share) = (p.Target, p.Property, p.Ease ?? Out, p.Share);
            object from = Take(o, prop);
            Func<double, object> at = (from, p.To) switch
            {
                (double a, double z) => k => a + (z - a) * k,
                (Thickness a, Thickness z) => k => new Thickness(a.Left + (z.Left - a.Left) * k, a.Top + (z.Top - a.Top) * k,
                    a.Right + (z.Right - a.Right) * k, a.Bottom + (z.Bottom - a.Bottom) * k),
                _ => throw new ArgumentException($"Tween.Together moves doubles and thicknesses: {prop.Name} is {from?.GetType().Name}, to {p.To?.GetType().Name}"),
            };
            moves.Add(t => o.SetValue(prop, at(ease(share <= 0 ? 1 : Math.Min(1, t / share)))));
        }
        Start(new Run([.. parts.Select(p => (p.Target, p.Property))], k => { foreach (var m in moves) m(k); }, ms, Linear, done));
    }

    /// <summary>
    /// Scales <paramref name="s"/> across and down together to <paramref name="to"/>, each from where it is, as one
    /// movement (stopped, or seen moving, by either property). One movement for each was a refresh apart from the
    /// other in some frames: Focus's circle 68 by 70 in its bounce (2026-10-18, cc-seams-motion; Core's RefreshSteps).
    /// </summary>
    public static void To(ScaleTransform s, double to, double ms, Func<double, double>? ease = null, Action? done = null)
    {
        double fromX = Take(s, ScaleTransform.ScaleXProperty) is double x ? x : to;
        double fromY = Take(s, ScaleTransform.ScaleYProperty) is double y ? y : to;
        Start(new Run([(s, ScaleTransform.ScaleXProperty), (s, ScaleTransform.ScaleYProperty)], k =>
        {
            s.ScaleX = fromX + (to - fromX) * k;
            s.ScaleY = fromY + (to - fromY) * k;
        }, ms, ease ?? Out, done));
    }

    /// <summary>Moves a <see cref="Thickness"/> (a margin, a padding) from where it is to <paramref name="to"/>.</summary>
    public static void To(DependencyObject o, DependencyProperty p, Thickness to, double ms, Func<double, double>? ease = null, Action? done = null)
    {
        var from = Take(o, p) is Thickness t ? t : to;
        Start(new Run([(o, p)], k => o.SetValue(p, new Thickness(
            from.Left + (to.Left - from.Left) * k, from.Top + (to.Top - from.Top) * k,
            from.Right + (to.Right - from.Right) * k, from.Bottom + (to.Bottom - from.Bottom) * k)), ms, ease ?? Out, done));
    }

    /// <summary>Changes a brush's colour from what it is to <paramref name="to"/> (its own brush: not frozen).</summary>
    public static void To(SolidColorBrush b, Color to, double ms, Func<double, double>? ease = null, Action? done = null)
    {
        var from = Take(b, SolidColorBrush.ColorProperty) is Color c ? c : to;
        static byte Mix(byte a, byte z, double k) => (byte)Math.Round(a + (z - a) * k);
        Start(new Run([(b, SolidColorBrush.ColorProperty)], k => b.Color = Color.FromArgb(Mix(from.A, to.A, k), Mix(from.R, to.R, k), Mix(from.G, to.G, k), Mix(from.B, to.B, k)),
            ms, ease ?? Linear, done));
    }

    /// <summary>Stops the movement of <paramref name="o"/>'s <paramref name="p"/> where it is (its end is not called).</summary>
    public static void Stop(DependencyObject o, DependencyProperty p) => Running.RemoveAll(r => r.Moves(o, p));

    /// <summary>The property is still moving (diagnostics).</summary>
    public static bool Moving(DependencyObject o, DependencyProperty p) => Running.Any(r => r.Moves(o, p));

    /// <summary>After each frame of the movements (pieces of glass follow parts that slide without a layout pass).</summary>
    public static event Action? Stepped;

    /// <summary>Something moves: work that can wait waits until it is over.</summary>
    public static bool Busy => Running.Count > 0;

    /// <summary>Something is moving <paramref name="o"/> now (a brush's colour on its way: Inks leaves it to its own code).</summary>
    public static bool Moving(DependencyObject o)
    {
        foreach (var r in Running)
            foreach (var (target, _) in r.Moved)
                if (ReferenceEquals(target, o)) return true;
        return false;
    }

    /// <summary>
    /// Diagnostics "tween-skew": every other movement begun before the next frame is begun that many ms earlier, as
    /// two begun apart whose steps round apart (cc-seams-motion skew=: what must agree is one movement, and stays so).
    /// </summary>
    public static double Skew { get; set; }
    static int _begunBeforeFrame;

    /// <summary>
    /// The property's value now, held there as its own value: a WPF animation still on it is taken off (it would hide
    /// what is set), and a movement of it stops.
    /// </summary>
    static object Take(DependencyObject o, DependencyProperty p)
    {
        var now = o.GetValue(p);
        Stop(o, p);
        if (o is IAnimatable a && a.HasAnimatedProperties) a.BeginAnimation(p, null);
        o.SetValue(p, now);
        return now;
    }

    static void Start(Run r)
    {
        if (r.Ms <= 0) { r.Apply(1); r.Done?.Invoke(); return; }
        r.Steps.Begin(_begunBeforeFrame++ % 2 * Skew);
        Running.Add(r);
        Clock().Start();
    }

    static FrameClock Clock()
    {
        if (_clock != null) return _clock;
        _clock = new FrameClock(Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);
        _clock.Tick += _ =>
        {
            _begunBeforeFrame = 0;
            foreach (var r in Running.ToList())
            {
                if (!Running.Contains(r)) continue; // stopped by another's end
                double t = r.Steps.Next(r.Ms);
                r.Apply(r.Ease(t));
                if (t < 1) continue;
                Running.Remove(r);
                r.Done?.Invoke();
            }
            Stepped?.Invoke();
            if (Running.Count == 0) _clock!.Stop();
        };
        return _clock;
    }
}
