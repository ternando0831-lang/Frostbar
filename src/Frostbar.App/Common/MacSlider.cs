using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Frostbar.Platform.Sys;

namespace Frostbar.App.Common;

/// <summary>
/// A settings slider (macOS's): a thin track, filled with the accent colour up to a round white knob, snapping to
/// <see cref="Step"/>. <see cref="ValueChanged"/> comes with every move (applied live); <see cref="Committed"/> when the
/// pointer lets go, or after a key step, for what is too heavy to redo on every move. The arrow keys step it.
/// </summary>
public sealed class MacSlider : FrameworkElement, Inks.ITinted
{
    Inks.Mapping? _tone;

    /// <summary>On glass of the other tone (item 4): its faint track mapped; the accent fill and the knob keep theirs.</summary>
    public void Tone(Inks.Mapping? map)
    {
        if (ReferenceEquals(_tone, map)) return;
        _tone = map;
        InvalidateVisual();
    }

    const double Knob = 10; // radius
    readonly Brush _track, _fill, _knob, _shadow, _focus;
    double _value;
    bool _dragging;

    public double Minimum { get; private set; }
    public double Maximum { get; private set; }
    public double Step { get; }

    public event Action<double>? ValueChanged;
    public event Action<double>? Committed;

    public MacSlider(double min, double max, double step, double value, ThemeSnapshot t)
    {
        Minimum = min;
        Maximum = Math.Max(min, max);
        Step = step;
        _value = Snap(value);
        var accent = Color.FromRgb(t.Accent.R, t.Accent.G, t.Accent.B);
        _track = Frozen(new SolidColorBrush(t.Dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0, 0, 0)));
        _fill = Frozen(new SolidColorBrush(accent));
        _knob = Frozen(new SolidColorBrush(t.Dark ? Color.FromRgb(0xE8, 0xE8, 0xE8) : Colors.White));
        _shadow = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)));
        _focus = Frozen(new SolidColorBrush(Color.FromArgb(0x80, accent.R, accent.G, accent.B)));
        Height = 2 * Knob + 4;
        MinWidth = 120;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Arrow;
    }

    static Brush Frozen(Brush b) { b.Freeze(); return b; }

    /// <summary>Set from outside: moves the knob without raising events (ignored while the pointer holds it).</summary>
    public double Value
    {
        get => _value;
        set => Set(value, 0);
    }

    /// <summary>
    /// Set from outside, the knob sliding there (another app's change, the volume keys held: it jumped at each step,
    /// 2026-10-19) as the volume display's bar does (PillSlider.GlideMs). At once when <paramref name="animate"/> is off.
    /// </summary>
    public void Glide(double value, bool animate) => Set(value, animate ? PillSlider.GlideMs : 0);

    void Set(double value, double ms)
    {
        if (_dragging) return;
        double v = Snap(value);
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
        DependencyProperty.Register(nameof(Drawn), typeof(double), typeof(MacSlider), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The value as drawn: <see cref="Value"/>, or on its way there (<see cref="Glide"/>).</summary>
    public double Drawn { get => (double)GetValue(DrawnProperty); set => SetValue(DrawnProperty, value); }

    /// <summary>New ends (the magnified size cannot be less than the size); the value moves inside them, without events.</summary>
    public void SetRange(double min, double max)
    {
        Minimum = min;
        Maximum = Math.Max(min, max);
        _value = Snap(_value);
        if (!double.IsNaN(Drawn)) Draw(_value);
        InvalidateVisual();
    }

    double Snap(double v)
    {
        v = Math.Clamp(v, Minimum, Maximum);
        if (Step > 0) v = Math.Clamp(Minimum + Math.Round((v - Minimum) / Step) * Step, Minimum, Maximum);
        return v;
    }

    // Where the knob is drawn (on its way to the value after a Glide).
    double Fraction => Maximum > Minimum ? Math.Clamp(((double.IsNaN(Drawn) ? _value : Drawn) - Minimum) / (Maximum - Minimum), 0, 1) : 0;

    /// <summary>The knob's centre in the slider (DIPs): for the specs, which drag it.</summary>
    public Point KnobCentre => new(Knob + 1 + Fraction * Math.Max(0, ActualWidth - 2 * Knob - 2), ActualHeight / 2);

    public static readonly DependencyProperty PressProperty =
        DependencyProperty.Register(nameof(Press), typeof(double), typeof(MacSlider), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

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

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, cy = ActualHeight / 2, p = GlassKnob.Shown(Press);
        if (w <= 2 * Knob) return;
        double left = Knob + 1, right = w - Knob - 1, x = KnobCentre.X;
        void Track(DrawingContext d)
        {
            d.DrawRoundedRectangle(_tone?.Fill(_track) ?? _track, null, new Rect(left - 2, cy - 2, right - left + 4, 4), 2, 2);
            d.DrawRoundedRectangle(_fill, null, new Rect(left - 2, cy - 2, x - left + 2, 4), 2, 2);
        }
        Track(dc);
        if (IsKeyboardFocused && _ring) dc.DrawEllipse(null, new Pen(_focus, 3), new Point(x, cy), Knob + 1.5, Knob + 1.5);
        if (p < 1)
        {
            dc.PushOpacity(1 - p);
            dc.DrawEllipse(_shadow, null, new Point(x, cy + 0.8), Knob, Knob);
            dc.DrawEllipse(_knob, null, new Point(x, cy), Knob - 0.5, Knob - 0.5);
            dc.Pop();
        }
        GlassKnob.Draw(dc, GlassKnob.Along(GlassKnob.Round(new Point(x, cy), Knob - 0.5, p), GlassKnob.StretchOf(this) * p), p, Track);
    }

    double ValueAt(double px)
    {
        double left = Knob + 1, width = Math.Max(1, ActualWidth - 2 * Knob - 2);
        return Snap(Minimum + Math.Clamp((px - left) / width, 0, 1) * (Maximum - Minimum));
    }

    void Move(double v)
    {
        v = Snap(v);
        if (v == _value) return;
        _value = v;
        Draw(v);
        ValueChanged?.Invoke(v);
    }

    /// <summary>
    /// The focus ring shows for the keyboard only (macOS): focus from a click took it too, and it stayed on the slider
    /// after the mouse had let go of it. The arrow keys bring it back.
    /// </summary>
    bool _ring, _clicked;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _clicked = true;
        _ring = false;
        Focus();
        _clicked = false;
        InvalidateVisual();
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
        if (was) Committed?.Invoke(_value);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        Hold(false);
        if (!_dragging) return;
        _dragging = false;
        Committed?.Invoke(_value);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int dir = e.Key switch { Key.Left or Key.Down => -1, Key.Right or Key.Up => 1, _ => 0 };
        if (dir == 0) return;
        if (!_ring) { _ring = true; InvalidateVisual(); }
        Move(_value + dir * Math.Max(Step, (Maximum - Minimum) / 20));
        Committed?.Invoke(_value);
        e.Handled = true;
    }

    // No wheel: the page scrolls under it without changing the setting.

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); _ring = !_clicked; InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); _ring = false; InvalidateVisual(); }

    /// <summary>Read by a screen reader as a slider with its value and range (D4: it was a nameless element).</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    sealed class Peer(MacSlider s) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(s), System.Windows.Automation.Provider.IRangeValueProvider
    {
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => System.Windows.Automation.Peers.AutomationControlType.Slider;
        protected override string GetClassNameCore() => nameof(MacSlider);
        public override object? GetPattern(System.Windows.Automation.Peers.PatternInterface p) => p == System.Windows.Automation.Peers.PatternInterface.RangeValue ? this : base.GetPattern(p);
        public double Value => s._value;
        public double Minimum => s.Minimum;
        public double Maximum => s.Maximum;
        public double SmallChange => Math.Max(s.Step, (s.Maximum - s.Minimum) / 20);
        public double LargeChange => (s.Maximum - s.Minimum) / 5;
        public bool IsReadOnly => !s.IsEnabled;
        public void SetValue(double value)
        {
            s.Move(value);
            s.Committed?.Invoke(s._value);
        }
    }
}
