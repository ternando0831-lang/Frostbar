using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Frostbar.App.Common;

/// <summary>
/// An on/off switch as macOS draws it: a rounded track, the accent colour when on, a white knob that slides over in a
/// moment. A click switches it and raises <see cref="Toggled"/>; <see cref="IsOn"/> set from outside (the radio changed
/// elsewhere) only moves the knob.
/// </summary>
public sealed class PillSwitch : FrameworkElement, Inks.ITinted
{
    Inks.Mapping? _tone;

    /// <summary>On glass of the other tone (item 4): its faint off track mapped; on, the accent and the knob keep theirs.</summary>
    public void Tone(Inks.Mapping? map)
    {
        if (ReferenceEquals(_tone, map)) return;
        _tone = map;
        InvalidateVisual();
    }

    public static readonly DependencyProperty KnobProperty =
        DependencyProperty.Register(nameof(Knob), typeof(double), typeof(PillSwitch), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The knob's place, 0 (off) to 1 (on); animated.</summary>
    public double Knob { get => (double)GetValue(KnobProperty); set => SetValue(KnobProperty, value); }

    public static readonly DependencyProperty PressProperty =
        DependencyProperty.Register(nameof(Press), typeof(double), typeof(PillSwitch), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How far the knob has turned to glass while held (<see cref="GlassKnob"/>), 0 to 1; animated.</summary>
    public double Press { get => (double)GetValue(PressProperty); set => SetValue(PressProperty, value); }

    bool _held;

    void Hold(bool on)
    {
        if (on == _held || (on && !GlassKnob.Enabled())) return;
        _held = on;
        GlassKnob.Held += on ? 1 : -1;
        if (on) GlassKnob.Pressed++;
        Tween.To(this, PressProperty, on ? 1 : 0, _animate ? (on ? GlassKnob.PressMs : GlassKnob.LetGoMs) : 0);
    }

    Brush _on = Brushes.DodgerBlue, _off = Brushes.Gray;
    static readonly Brush KnobFill = Frozen(Brushes.White), KnobShadow = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)));
    bool _isOn;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value) return;
            _isOn = value;
            Slide();
        }
    }

    /// <summary>The user switched it (the new state).</summary>
    public event Action<bool>? Toggled;

    /// <param name="animate">The shell's animation setting (with its override), as every other surface follows: not Windows' own.</param>
    public PillSwitch(Brush on, Brush off, bool isOn, bool animate = true)
    {
        _on = on;
        _off = off;
        _isOn = isOn;
        _animate = animate;
        Knob = isOn ? 1 : 0;
        Width = 32;
        Height = 18;
        // Tab reaches it and Space or Enter switches it (it took the mouse only: D3, 2026-10-19). A click does not move
        // the keyboard's focus to it (a FrameworkElement takes focus only when asked).
        Focusable = true;
        Cursor = Cursors.Arrow;
        // In a panel the arrows and Enter reach it too (the panels take no focus: KeyNav's parts are the marked ones).
        KeyNav.SetRun(this, () =>
        {
            if (!IsEnabled) return;
            IsOn = !IsOn;
            Toggled?.Invoke(IsOn);
        });
    }

    bool _pressed; // the press began on it: a release that only ends over it (a press elsewhere, a pop-up's dismissing press) does nothing

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key is not (Key.Space or Key.Enter) || !IsEnabled) return;
        e.Handled = true;
        IsOn = !IsOn;
        Toggled?.Invoke(IsOn);
    }

    /// <summary>Read by a screen reader as a switch, on or off (D4: it was a nameless element).</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    sealed class Peer(PillSwitch s) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(s), System.Windows.Automation.Provider.IToggleProvider
    {
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => System.Windows.Automation.Peers.AutomationControlType.CheckBox;
        protected override string GetClassNameCore() => nameof(PillSwitch);
        public override object? GetPattern(System.Windows.Automation.Peers.PatternInterface p) => p == System.Windows.Automation.Peers.PatternInterface.Toggle ? this : base.GetPattern(p);
        public System.Windows.Automation.ToggleState ToggleState => s.IsOn ? System.Windows.Automation.ToggleState.On : System.Windows.Automation.ToggleState.Off;
        public void Toggle()
        {
            if (!s.IsEnabled) throw new System.Windows.Automation.ElementNotEnabledException();
            s.IsOn = !s.IsOn;
            s.Toggled?.Invoke(s.IsOn);
        }
    }

    static Brush Frozen(Brush b) { if (b.CanFreeze) b.Freeze(); return b; }

    readonly bool _animate;

    void Slide() => Tween.To(this, KnobProperty, _isOn ? 1 : 0, _animate ? Frostbar.Core.Shell.MotionTable.SwitchKnob : 0);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, r = h / 2, k = Knob, p = GlassKnob.Shown(Press);
        // The track fades from the off colour to the on colour as the knob moves.
        void Track(DrawingContext d)
        {
            d.DrawRoundedRectangle(_tone?.Fill(_off) ?? _off, null, new Rect(0, 0, w, h), r, r);
            d.PushOpacity(k);
            d.DrawRoundedRectangle(_on, null, new Rect(0, 0, w, h), r, r);
            d.Pop();
        }
        Track(dc);
        double x = r + k * (w - h);
        if (p < 1)
        {
            dc.PushOpacity(1 - p);
            dc.DrawEllipse(KnobShadow, null, new Point(x, r + 0.6), r - 1.2, r - 1.2);
            dc.DrawEllipse(KnobFill, null, new Point(x, r), r - 1.6, r - 1.6);
            dc.Pop();
        }
        GlassKnob.Draw(dc, GlassKnob.Pill(new Point(x, r), r - 1.6, p), p, Track);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        e.Handled = true;
        Hold(false);
        if (!_pressed) return;
        _pressed = false;
        IsOn = !IsOn;
        Toggled?.Invoke(IsOn);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        _pressed = true;
        Hold(true);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _pressed = false;
        Hold(false);
    }
}
