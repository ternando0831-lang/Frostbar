using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Frostbar.Platform.Dwm;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Common;

/// <summary>
/// Liquid Glass's light where it is pressed (the user's "B", 2026-10-18): from the point pressed, light spreads through
/// the tile, button or row under it and fades (bright and small at first, 210 DIPs and faint after 0.3 s, gone at 0.45 s).
/// Only inside that part's rounded shape: the nearest rounded <see cref="Border"/> round the point, smaller than the
/// window (not the panel itself). Not on a slider or a switch, whose knob turns to glass instead.
/// </summary>
public static class PressLight
{
    const double SpreadMs = Motion.PressSpread, FadeMs = Motion.PressFade, FromRadius = 40, ToRadius = 210, FromAlpha = 0.6, ToAlpha = 0.12;

    /// <summary>
    /// Lights the window's parts where they are pressed, while <paramref name="on"/> (Liquid Glass and animations on),
    /// and the light spills onto the glass round it, in this window and the others (the user's "A 沈む＋隣へ光る",
    /// 2026-10-23): the shell's composition glass by <c>WindowBackdrop.SpillAt</c>, a window's own glass drawn by WPF
    /// (the settings, a dialog) by <see cref="PressSpill"/>.
    /// </summary>
    public static void Attach(Window w, Func<bool> on) =>
        w.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (!on() || e.OriginalSource is not DependencyObject d) return;
            var part = Part(d, w);
            if (part != null) Start(part, e.GetPosition(part), null);
            Spill(w, e.GetPosition(w), lit: part != null);
        }), handledEventsToo: true);

    /// <summary>A press's light reaching the glass round <paramref name="at"/> (<paramref name="w"/>'s DIPs): the shell's glass and <paramref name="w"/>'s own.</summary>
    public static void Spill(Window w, Point at, bool lit)
    {
        if (PresentationSource.FromVisual(w) == null) return;
        var px = w.PointToScreen(at);
        Spilled += WindowBackdrop.SpillAt(px.X, px.Y, pressedLit: lit);
        Spilled += PressSpill.At(w, at);
    }

    /// <summary>How many pieces of glass presses' light has reached (diagnostics).</summary>
    public static int Spilled { get; private set; }

    /// <summary>A part that does nothing when pressed (a menu's disabled row): no light.</summary>
    public static readonly DependencyProperty OffProperty = DependencyProperty.RegisterAttached("Off", typeof(bool), typeof(PressLight), new PropertyMetadata(false));
    public static void SetOff(DependencyObject o, bool off) => o.SetValue(OffProperty, off);
    public static bool GetOff(DependencyObject o) => (bool)o.GetValue(OffProperty);

    /// <summary>How many lights have been started (diagnostics).</summary>
    public static int Started { get; private set; }

    /// <summary>The lights spreading now (diagnostics).</summary>
    public static int Spreading { get; private set; }

    static Light? _held;
    static AdornerLayer? _heldLayer;

    static void Start(Border part, Point at, double? hold)
    {
        if (AdornerLayer.GetAdornerLayer(part) is not { } layer) return;
        var light = new Light(part, at);
        layer.Add(light);
        Started++;
        if (hold is { } p)
        {
            light.Progress = p; // for a photo: held there until let go (Let)
            _held = light;
            _heldLayer = layer;
            return;
        }
        Spreading++;
        Tween.To(light, Light.ProgressProperty, 1, SpreadMs + FadeMs, Tween.Linear, () => { layer.Remove(light); Spreading--; });
    }

    /// <summary>
    /// For photos (diagnostics "press-light"): the light of a press at <paramref name="screen"/> (pixels), held
    /// <paramref name="progress"/> of the way through (0 the press, 1 gone), in whichever window of the shell is there. No
    /// click. Returns the window and the part's rectangle on the screen (pixels), or null where nothing would light.
    /// </summary>
    public static string? Show(Point screen, double progress)
    {
        Let();
        foreach (var w in Under(screen))
        {
            if (!w.IsVisible || PresentationSource.FromVisual(w) == null) continue;
            var at = w.PointFromScreen(screen);
            if (at.X < 0 || at.Y < 0 || at.X >= w.ActualWidth || at.Y >= w.ActualHeight) continue;
            if (HitAt(w, at) is not { } hit || Part(hit, w) is not { } part) continue;
            Start(part, w.TranslatePoint(at, part), Math.Clamp(progress, 0, 1));
            var a = part.PointToScreen(new Point(0, 0));
            var z = part.PointToScreen(new Point(part.ActualWidth, part.ActualHeight));
            return $"{w.Title} {a.X:0},{a.Y:0},{z.X:0},{z.Y:0}";
        }
        return null;
    }

    /// <summary>
    /// For tests (diagnostics "press-at"): a press's light at <paramref name="screen"/> (pixels) in whichever window of
    /// the shell is there, spreading and spilling as a press's does, no click. Returns the window's title, what is
    /// there and the part lit (or null where no part lights: the spill still goes), or null where no window of the shell is.
    /// </summary>
    public static (string window, DependencyObject hit, Border? part)? At(Point screen)
    {
        foreach (var w in Under(screen))
        {
            if (!w.IsVisible || PresentationSource.FromVisual(w) == null) continue;
            var at = w.PointFromScreen(screen);
            if (at.X < 0 || at.Y < 0 || at.X >= w.ActualWidth || at.Y >= w.ActualHeight) continue;
            if (HitAt(w, at) is not { } hit) continue;
            var part = Part(hit, w);
            if (part != null) Start(part, w.TranslatePoint(at, part), null);
            Spill(w, at, lit: part != null);
            return (w.Title, hit, part);
        }
        return null;
    }

    /// <summary>
    /// The shell's windows a press at <paramref name="screen"/> would reach: the one Windows finds there first (a real
    /// press's), then the others. A menu's click shield is not among them (a plain Win32 window since 2026-11-08; as a
    /// WPF window it lay under the panels and over everything else, and the creation order found it before the Control
    /// Center, 2026-10-23).
    /// </summary>
    public static IEnumerable<Window> Under(Point screen)
    {
        var all = Application.Current.Windows.Cast<Window>().ToList();
        var root = Frostbar.Platform.Interop.Native.GetAncestor(
            Frostbar.Platform.Interop.Native.WindowFromPoint(new Frostbar.Platform.Interop.POINT { X = (int)screen.X, Y = (int)screen.Y }), Frostbar.Platform.Interop.Native.GA_ROOT);
        var first = all.FirstOrDefault(w => new System.Windows.Interop.WindowInteropHelper(w).Handle == root);
        return first == null ? all : [first, .. all.Where(w => w != first)];
    }

    /// <summary>
    /// What a press at <paramref name="at"/> (<paramref name="w"/>'s DIPs) reaches, as the mouse finds it: WPF's input
    /// hit test, which passes by what takes no hits. VisualTreeHelper.HitTest does not: it found a press's own light
    /// over the part, and the diagnostics read no give under it (2026-10-23, press-spill).
    /// </summary>
    public static DependencyObject? HitAt(Window w, Point at) => w.InputHitTest(at) as DependencyObject;

    /// <summary>Takes away the light held for a photo.</summary>
    public static void Let()
    {
        if (_held != null) _heldLayer?.Remove(_held);
        _held = null;
        _heldLayer = null;
    }

    /// <summary>The rounded part pressed: up from the point to the window, none past a slider, a switch or a part that is off.</summary>
    static Border? Part(DependencyObject d, Window w)
    {
        double all = w.ActualWidth * w.ActualHeight;
        for (var o = d; o != null && o != w; o = Parent(o))
        {
            if (o is PillSlider or PillSwitch or MacSlider || GetOff(o)) return null;
            if (o is Border { Background: not null } b && b.CornerRadius.TopLeft > 0 && b.IsVisible && b.ActualWidth * b.ActualHeight < all * 0.5)
                return b;
        }
        return null;
    }

    static DependencyObject? Parent(DependencyObject o) =>
        o is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(o) ?? LogicalTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o);

    sealed class Light : Adorner
    {
        public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(Light),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

        readonly Border _part;
        readonly Point _at;

        public Light(Border part, Point at) : base(part)
        {
            _part = part;
            _at = at;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double all = SpreadMs + FadeMs, t = Progress * all;
            double spread = Math.Min(1, t / SpreadMs);
            double radius = FromRadius + (ToRadius - FromRadius) * Tween.Out(spread);
            double alpha = t <= SpreadMs ? FromAlpha + (ToAlpha - FromAlpha) * spread : ToAlpha * (1 - (t - SpreadMs) / FadeMs);
            // As faded as its part: an adorner is drawn over the window's content apart from its part's opacity, and the
            // light of a Wi-Fi capsule pressed stayed over the view growing out of it after the capsule had gone
            // (2026-10-18, cc-motion's film).
            for (DependencyObject? o = _part; o != null; o = VisualTreeHelper.GetParent(o)) if (o is UIElement u) alpha *= u.IsVisible ? u.Opacity : 0;
            if (alpha <= 0.004) return;
            var size = new Rect(0, 0, _part.ActualWidth, _part.ActualHeight);
            var brush = new RadialGradientBrush(Color.FromArgb((byte)Math.Round(alpha * 255), 255, 255, 255), Color.FromArgb(0, 255, 255, 255))
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = _at,
                GradientOrigin = _at,
                RadiusX = radius,
                RadiusY = radius,
            };
            // The part's shape filled with the light (not the light clipped to it: under the bounce's scale a clip to a
            // circle showed its triangles' seams as thin lines, 2026-10-18, cc-motion's film; not yet confirmed which).
            double r = Math.Min(_part.CornerRadius.TopLeft, Math.Min(size.Width, size.Height) / 2);
            dc.DrawRoundedRectangle(brush, null, size, r, r);
        }
    }
}
