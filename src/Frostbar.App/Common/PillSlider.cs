using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Common;

/// <summary>
/// The Control Center's slider (macOS): a rounded track as tall as its knob, filled up to the value, a glyph at its
/// left end. Click or drag anywhere on it; the wheel steps 5%. <see cref="ValueChanged"/> comes with every move (the
/// owner applies it live); <see cref="Value"/> set from outside (another app changed the volume) is ignored while the
/// pointer holds the knob, so the knob does not jump back under the finger.
/// </summary>
public sealed class PillSlider : FrameworkElement, Inks.ITinted
{
    Inks.Mapping? _tone;

    /// <summary>On glass of the other tone (item 4): its faint track mapped; the fill, the knob and the glyph on the fill keep theirs.</summary>
    public void Tone(Inks.Mapping? map)
    {
        if (ReferenceEquals(_tone, map)) return;
        _tone = map;
        InvalidateVisual();
    }

    Brush TrackBrush => _tone?.Fill(_track) ?? _track;

    readonly Geometry _glyph;
    readonly bool _strokeGlyph;
    Brush _track = Brushes.Gray, _fill = Brushes.White, _glyphBrush = Brushes.DimGray, _shadow = Brushes.Black;
    double _value;
    bool _dragging;

    /// <summary>0–1.</summary>
    public double Value
    {
        get => _value;
        set => Set(value, 0);
    }

    /// <summary>How long the bar slides to a value changed from outside (the volume display's, HudWindow.SlideMs).</summary>
    public const double GlideMs = Motion.SliderGlide;

    /// <summary>
    /// Set from outside, the bar sliding there (another app's change, the volume keys held: it jumped at each step while
    /// the volume display slid, 2026-10-19). At once when <paramref name="animate"/> is off or it is not shown.
    /// </summary>
    public void Glide(double value, bool animate) => Set(value, animate ? GlideMs : 0);

    void Set(double value, double ms)
    {
        if (_dragging) return;
        double v = Math.Clamp(value, 0, 1);
        if (v == _value) return;
        _value = v;
        if (ms > 0 && IsLoaded && IsVisible) Tween.To(this, DrawnProperty, v, ms);
        else Draw(v);
    }

    void Draw(double v)
    {
        Tween.Stop(this, DrawnProperty);
        Drawn = v;
    }

    public static readonly DependencyProperty DrawnProperty =
        DependencyProperty.Register(nameof(Drawn), typeof(double), typeof(PillSlider), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The value as drawn: <see cref="Value"/>, or on its way there (<see cref="Glide"/>).</summary>
    public double Drawn { get => (double)GetValue(DrawnProperty); set => SetValue(DrawnProperty, value); }

    public bool Dragging => _dragging;

    /// <summary>The pointer moved the value (0–1).</summary>
    public event Action<double>? ValueChanged;

    /// <summary>The pointer let go of the slider (a click or the end of a drag): macOS's volume plays its feedback then.</summary>
    public event Action<double>? LetGo;

    public PillSlider(Geometry glyph, bool strokeGlyph = false, double height = 26)
    {
        _glyph = glyph;
        _strokeGlyph = strokeGlyph;
        Height = height;
        Cursor = Cursors.Arrow;
        Focusable = false;
    }

    public void SetColors(Brush track, Brush fill, Brush glyph, Brush shadow)
    {
        (_track, _fill, _glyphBrush, _shadow) = (track, fill, glyph, shadow);
        InvalidateVisual();
    }

    public static readonly DependencyProperty PressProperty =
        DependencyProperty.Register(nameof(Press), typeof(double), typeof(PillSlider), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How far the knob has turned to glass while held (<see cref="GlassKnob"/>), 0 to 1; animated.</summary>
    public double Press { get => (double)GetValue(PressProperty); set => SetValue(PressProperty, value); }

    bool _held;

    void Hold(bool on)
    {
        if (on == _held || (on && !GlassKnob.Enabled())) return;
        _held = on;
        GlassKnob.Held += on ? 1 : -1;
        if (on) GlassKnob.Pressed++;
        Tween.To(this, PressProperty, on ? 1 : 0, GlassKnob.Animate() ? (on ? GlassKnob.PressMs : GlassKnob.LetGoMs) : 0);
    }

    /// <summary>
    /// macOS 27's Control Center slider (the user's "A", 2026-10-18): a thick bar with no knob and no glyph in it (the
    /// module puts its glyphs at its ends); the knob shows only while held, as glass (<see cref="GlassKnob"/>). The value
    /// sits where the knob would be, as before.
    /// </summary>
    public bool Knobless { get; init; }

    // The knobless bar's thickness (DIPs).
    const double Bar = 8;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, r = h / 2, p = GlassKnob.Shown(Press);
        if (w <= h) return;
        double knob = r + Drawn * (w - h); // the knob's centre
        if (Knobless)
        {
            void Bar27(DrawingContext d)
            {
                var bar = new Rect(0, r - Bar / 2, w, Bar);
                d.DrawRoundedRectangle(TrackBrush, null, bar, Bar / 2, Bar / 2);
                d.DrawRoundedRectangle(_fill, null, new Rect(0, bar.Y, Math.Max(Bar, knob), Bar), Bar / 2, Bar / 2);
            }
            Bar27(dc);
            GlassKnob.Draw(dc, GlassKnob.Along(GlassKnob.Round(new Point(knob, r), r - 1, p), GlassKnob.StretchOf(this) * p), p, Bar27);
            return;
        }
        void Track(DrawingContext d)
        {
            d.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, w, h), r, r);
            d.DrawRoundedRectangle(_fill, null, new Rect(0, 0, knob + r, h), r, r);
        }
        Track(dc);
        if (p < 1)
        {
            dc.PushOpacity(1 - p);
            dc.DrawEllipse(_shadow, null, new Point(knob, r + 0.5), r - 0.5, r - 0.5);
            dc.DrawEllipse(_fill, null, new Point(knob, r), r - 1, r - 1);
            dc.Pop();
        }
        Glyph(dc);
        GlassKnob.Draw(dc, GlassKnob.Along(GlassKnob.Round(new Point(knob, r), r - 1, p), GlassKnob.StretchOf(this) * p), p, d => { Track(d); Glyph(d); });
    }

    // The glyph on a 16-unit grid, as tall as half the track, at the left end.
    void Glyph(DrawingContext dc)
    {
        double h = ActualHeight, size = h * 0.55, k = size / 16;
        dc.PushTransform(new TranslateTransform((h - size) / 2 + 2, (h - size) / 2));
        dc.PushTransform(new ScaleTransform(k, k));
        if (_strokeGlyph) dc.DrawGeometry(null, new Pen(_glyphBrush, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, _glyph);
        else dc.DrawGeometry(_glyphBrush, null, _glyph);
        dc.Pop();
        dc.Pop();
    }

    double ValueAt(double x)
    {
        double h = ActualHeight;
        return Math.Clamp((x - h / 2) / Math.Max(1, ActualWidth - h), 0, 1);
    }

    void Move(double v)
    {
        if (v == _value) return;
        _value = v;
        Draw(v);
        ValueChanged?.Invoke(v);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = CaptureMouse();
        GlassKnob.DragEnds(this); // a new drag: no speed yet
        Hold(true);
        Move(ValueAt(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        GlassKnob.Dragged(this, e.GetPosition(this).X); // the glass knob stretches by its speed
        Move(ValueAt(e.GetPosition(this).X));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        bool was = _dragging;
        if (_dragging) Move(ValueAt(e.GetPosition(this).X));
        _dragging = false;
        Hold(false);
        ReleaseMouseCapture();
        e.Handled = true;
        if (was) LetGo?.Invoke(_value);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
        Hold(false);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Move(Math.Clamp(_value + Math.Sign(e.Delta) * 0.05, 0, 1));
        e.Handled = true;
    }

    /// <summary>Where the knob's centre is (DIPs in the slider), for the E2E specs.</summary>
    public Point KnobCentre => new(ActualHeight / 2 + _value * Math.Max(0, ActualWidth - ActualHeight), ActualHeight / 2);
}
