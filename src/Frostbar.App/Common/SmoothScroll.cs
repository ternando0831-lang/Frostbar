using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Interop;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Common;

/// <summary>
/// Scrolling as on macOS. Every turn moves where the list is going (Windows' lines a notch × WPF's 16 DIP line: 48 at
/// Windows' default of 3, in proportion for a precision touchpad's small turns), and the list approaches that place
/// frame by frame, closing the same share of the way in the same time: a wheel's notch glides there over about 0.2 s,
/// a touchpad's turns follow a frame or two behind, evenly. Turns that come quickly carry on from where the list was going,
/// at the speed it had. WPF's ScrollViewer moved 48 DIP in one step for every turn, whatever its size and whatever
/// Windows' setting (the Mouse page sets it): a touchpad stroke of eight small turns, one notch's worth, went 384 DIP
/// (2026-10-17, scroll-feel). A movement gives way to anything else that moves the list (its scroll bar, the code
/// turning to a row).
/// </summary>
public static class SmoothScroll
{
    /// <summary>A notch's glide: within 1% of its distance after this long (4.6 of its time constants).</summary>
    public const double GlideMs = Motion.Scroll;

    /// <summary>
    /// How quickly the list closes on where it is going, in ms (the share left after t is e^(−t/τ)): a notch leaves at
    /// once, easing out; a touchpad's turns are first averaged over two frames, then followed with <see cref="TurnTau"/>.
    /// A touchpad's turns come at its own pace, not the display's: set at once as they came, one frame moved two turns
    /// and the next none (2026-10-17). Followed in one stage of 24 ms, turns about 10 ms apart still moved the list 27,
    /// 35, 28, 35 DIP in successive frames, and in two stages of 16 ms 30, 35, 30, 35 (frame-pace): frames that get one
    /// turn and two by turns swing at half the display's rate, which such stages hardly damp, and two frames' average
    /// takes out entirely.
    /// </summary>
    const double NotchTau = GlideMs / 4.6, TurnTau = 16;

    /// <param name="animate">The shell's animation setting, asked at each turn: off, a turn moves the list at once.</param>
    public static void Attach(ScrollViewer viewer, Func<bool> animate)
    {
        var glide = new Glide(viewer, animate);
        viewer.PreviewMouseWheel += glide.OnWheel;
        // The clock made ahead (its thread, the display's refresh rate): made at the first turn, the first glide after
        // the shell started waited 50 ms for its first step and 117 for a later one (2026-10-17).
        Clock(viewer.Dispatcher);
    }

    /// <summary>
    /// Glides <paramref name="viewer"/> to <paramref name="offset"/> as a notch does (the code turning to a row: the
    /// search's selection moved by the arrow keys, the settings' search finding a row). It jumped there in one step
    /// (2026-10-19, the user's "B4"). At once when <paramref name="animate"/> is off or the list is not shown.
    /// </summary>
    public static void To(ScrollViewer viewer, double offset, bool animate)
    {
        if (!Glides.TryGetValue(viewer, out var g)) g = new Glide(viewer, () => animate);
        g.GlideTo(offset, animate && viewer.IsVisible);
    }

    /// <summary>
    /// Glides <paramref name="viewer"/> just far enough that <paramref name="row"/> is in view (nothing when it is), below
    /// the top <paramref name="above"/> DIP of the view (where a toolbar stands over the list: the settings window's).
    /// </summary>
    public static void IntoView(ScrollViewer viewer, FrameworkElement row, bool animate, double above = 0)
    {
        if (!viewer.IsAncestorOf(row) || row.ActualHeight <= 0) return;
        double top = row.TransformToAncestor(viewer).Transform(new Point(0, 0)).Y + GoingBy(viewer);
        double bottom = top + row.ActualHeight, view = viewer.ViewportHeight;
        double at = Glides.TryGetValue(viewer, out var g) && g.Running ? g.Target : viewer.VerticalOffset;
        if (top < above) To(viewer, Math.Max(0, at + top - above), animate);
        else if (bottom > view) To(viewer, at + Math.Min(bottom - view, top - above), animate);
    }

    // How far the list still has to go to where it is gliding (a row's place is where the list is now).
    static double GoingBy(ScrollViewer viewer) => Glides.TryGetValue(viewer, out var g) && g.Running ? viewer.VerticalOffset - g.Target : 0;

    /// <summary>The list is still moving to where turns sent it (diagnostics: the specs wait until it is not).</summary>
    public static bool Gliding(ScrollViewer viewer) => Glides.TryGetValue(viewer, out var g) && g.Running;

    /// <summary>A list is moving: work that can wait (an idle moment's step) waits until it stops.</summary>
    public static bool Busy => Moving.Count > 0;

    /// <summary>Glides the code asked for (To, IntoView), since the start (diagnostics).</summary>
    public static int CodeGlides { get; private set; }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, Glide> Glides = new();

    static List<(double ms, double offset)>? _trace;

    /// <summary>
    /// Starts recording every moving list's offset at each step, with the time of the refresh it is shown at (ms on
    /// the Stopwatch's clock), or stops and returns the record (diagnostics: how evenly a list moves frame to frame).
    /// </summary>
    public static IReadOnlyList<(double ms, double offset)> Trace(bool on)
    {
        var was = _trace ?? new();
        _trace = on ? new() : null;
        return was;
    }

    // One display-paced clock for every moving list (DwmFlush: a tick after each frame DWM composes). WPF's own frame
    // clock (CompositionTarget.Rendering) came out of its rest unevenly: as a list started to move its frames came 19,
    // 15, 0 and 37 ms apart, and a notch's glide went 89, 21, 1 and 28 px in its first pictures on the screen, which DWM
    // showed every 17 ms (2026-10-17, frame-pace); once WPF was running, every frame was 17 ms. Each step is placed by the
    // refresh it will be shown at, as the thumbnails' (ThumbnailAnimator): the UI thread's own moment is uneven too.
    static FrameClock? _clock;
    static readonly List<Glide> Moving = new();
    static double _period = 1000 / 60.0; // read again with each glide begun (DisplaySteps: the refresh rate can change)

    static FrameClock Clock(System.Windows.Threading.Dispatcher ui)
    {
        if (_clock != null) return _clock;
        _period = DisplaySteps.Period;
        _clock = new FrameClock(ui);
        _clock.Tick += _ =>
        {
            foreach (var m in Moving.ToList()) m.Step();
            if (Moving.Count == 0) _clock!.Stop();
        };
        return _clock;
    }

    static void Run(Glide g, System.Windows.Threading.Dispatcher ui)
    {
        if (!Moving.Contains(g)) Moving.Add(g);
        Clock(ui).Start();
    }

    sealed class Glide
    {
        readonly ScrollViewer _v;
        readonly Func<bool> _animate;
        // Where the turns sent it, a touchpad's turns averaged on their way there, and where the list is; a touchpad's
        // turns come since the last step, and those of the step before.
        double _to, _mid, _set, _came, _cameBefore;
        bool _turns;
        long _start, _frame;
        public bool Running { get; private set; }

        public Glide(ScrollViewer v, Func<bool> animate)
        {
            _v = v;
            _animate = animate;
            _band = new Band(v);
            Glides.AddOrUpdate(v, this);
        }

        readonly Band _band;

        /// <summary>How far the list is stretched past an end now (DIPs; diagnostics).</summary>
        public double Stretched => _band.Shown;

        public void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || _v.ScrollableHeight <= 0 || InnerTakes(e)) return;
            e.Handled = true;
            int lines = SystemParameters.WheelScrollLines;
            double perNotch = lines < 0 ? _v.ViewportHeight : lines * 16.0;
            double by = -e.Delta / 120.0 * perNotch;
            if (!_animate())
            {
                Stop();
                _v.ScrollToVerticalOffset(Clamp(_v.VerticalOffset + by));
                return;
            }
            // A touchpad's turns past an end stretch the list there (macOS's rubber band); turns back take the stretch
            // up first. A wheel's notch stops at the end, as on macOS.
            if (e.Delta % 120 != 0)
            {
                double going = Running ? _to : _v.VerticalOffset;
                if (_band.Over != 0 && Math.Sign(by) != Math.Sign(_band.Over)) { _band.Pull(by); return; }
                double past = going + by - Clamp(going + by);
                if (past != 0)
                {
                    _band.Pull(past);
                    by -= past;
                    if (Math.Abs(by) < 0.01) return;
                }
            }
            if (!Running)
            {
                _set = _mid = _to = _v.VerticalOffset;
                _came = _cameBefore = 0;
                _start = Stopwatch.GetTimestamp(); _period = DisplaySteps.Period;
                _frame = 0;
                _trace?.Add((_start * 1000.0 / Stopwatch.Frequency, _set)); // where it starts from
                Running = true;
                Run(this, _v.Dispatcher);
            }
            double was = _to;
            _to = Clamp(_to + by);
            // A wheel's notch glides from where the list is; a touchpad's (or a free-spinning wheel's) small turn is
            // followed in two stages.
            _turns = e.Delta % 120 != 0;
            if (_turns) _came += _to - was;
            else { _mid = _to; _came = _cameBefore = 0; }
        }

        double Clamp(double offset) => Math.Clamp(offset, 0, Math.Max(0, _v.ScrollableHeight));

        /// <summary>Where the list is going (where it is when it is not moving).</summary>
        public double Target => Running ? _to : _v.VerticalOffset;

        /// <summary>To <paramref name="offset"/> as a notch goes (from where it is, or from where it was going), or at once.</summary>
        public void GlideTo(double offset, bool animate)
        {
            if (!animate)
            {
                Stop();
                _v.ScrollToVerticalOffset(Clamp(offset));
                return;
            }
            if (!Running)
            {
                _set = _mid = _to = _v.VerticalOffset;
                _came = _cameBefore = 0;
                _start = Stopwatch.GetTimestamp(); _period = DisplaySteps.Period;
                _frame = 0;
                Running = true;
                Run(this, _v.Dispatcher);
            }
            _turns = false;
            _came = _cameBefore = 0;
            _to = _mid = Clamp(offset);
            CodeGlides++;
        }

        public void Step()
        {
            // Moved by something else since the last frame (its scroll bar dragged, the page turned to a row): it gives way.
            if (Math.Abs(_v.VerticalOffset - Clamp(_set)) > 1 || !_v.IsVisible) { Stop(); return; }
            // The refresh this step will be shown at, counted from the turn: one more than the last at least (woken twice
            // within a refresh), and a refresh missed counts (up to three, so that the list does not leap after a stall).
            long frame = (long)Math.Round(Stopwatch.GetElapsedTime(_start).TotalMilliseconds / _period);
            if (frame <= _frame) frame = _frame + 1;
            double dt = Math.Min(3, frame - _frame) * _period;
            _frame = frame;
            _to = Clamp(_to);
            if (_turns)
            {
                // The turns of this step and the last, halved: a touchpad's turns come about 1.5 to 2.5 a frame, so the
                // frames get one and two (or two and three) by turns, and the average of two frames is even.
                _mid = Clamp(_mid + (_came + _cameBefore) / 2);
                _cameBefore = _came;
                _came = 0;
                if (_cameBefore == 0) _mid = _to; // the turns are over: all of them counted
            }
            else _mid = _to;
            _set += (_mid - _set) * (1 - Math.Exp(-dt / (_turns ? TurnTau : NotchTau)));
            bool there = Math.Abs(_to - _set) < 0.25 && Math.Abs(_to - _mid) < 0.25;
            if (there) _set = _mid = _to;
            _v.ScrollToVerticalOffset(_set);
            _trace?.Add((_start * 1000.0 / Stopwatch.Frequency + frame * _period, _set));
            if (there) Stop();
        }

        void Stop()
        {
            if (!Running) return;
            Running = false;
            Moving.Remove(this);
        }

        /// <summary>
        /// The turn is over a list inside this one that scrolls itself that way (it takes its own turns), not over this
        /// list's own content.
        /// </summary>
        bool InnerTakes(MouseWheelEventArgs e)
        {
            for (var d = e.OriginalSource as DependencyObject; d != null && d != _v; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is ScrollViewer inner && inner.ScrollableHeight > 0
                    && (e.Delta < 0 ? inner.VerticalOffset < inner.ScrollableHeight : inner.VerticalOffset > 0))
                    return true;
            return false;
        }
    }

    /// <summary>Stretches since the start (diagnostics: a touchpad's turns past an end).</summary>
    public static int Stretches { get; private set; }

    /// <summary>How far <paramref name="viewer"/>'s list is stretched past an end now (DIPs; 0: not; diagnostics).</summary>
    public static double Stretched(ScrollViewer viewer) => Glides.TryGetValue(viewer, out var g) ? g.Stretched : 0;

    /// <summary>
    /// macOS's rubber band: a touchpad's turns past an end move the list's content that way, less and less the further
    /// it goes (half the viewport's height at most), and once the turns stop for a moment it springs back (0.35 s,
    /// easing out). The lists stopped dead at their ends (2026-10-19, the user's "B5"). The content's own translate
    /// (none when it has another transform: it stays put).
    /// </summary>
    sealed class Band
    {
        public const double ReleaseMs = Motion.StretchRelease, BackMs = Motion.StretchBack;
        const double Give = 0.55; // Apple's: the first DIPs follow the turns at about half
        readonly ScrollViewer _v;
        System.Windows.Threading.DispatcherTimer? _release;
        TranslateTransform? _move;

        public Band(ScrollViewer v) => _v = v;

        /// <summary>The turns past the end so far (DIPs, negative: past the top).</summary>
        public double Over { get; private set; }

        public double Shown => _move?.Y is double y ? -y : 0;

        public void Pull(double by)
        {
            var move = Move();
            if (move == null) return;
            double was = Over;
            Over += by;
            if (was != 0 && Math.Sign(Over) != Math.Sign(was)) Over = 0; // back past where it rests
            double d = Math.Max(1, _v.ViewportHeight);
            double shown = Math.Sign(Over) * (1 - 1 / (Math.Abs(Over) * Give / d + 1)) * d;
            Tween.To(move, TranslateTransform.YProperty, -shown, 40);
            if (was == 0 && Over != 0) Stretches++;
            _release ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(ReleaseMs), System.Windows.Threading.DispatcherPriority.Normal, (_, _) => Release(), _v.Dispatcher);
            _release.Stop();
            _release.Start();
        }

        void Release()
        {
            _release?.Stop();
            Over = 0;
            if (_move != null) Tween.To(_move, TranslateTransform.YProperty, 0.0, BackMs, Tween.Out);
        }

        TranslateTransform? Move()
        {
            if (_v.Content is not UIElement content) return null;
            if (_move != null && ReferenceEquals(content.RenderTransform, _move)) return _move;
            if (content.RenderTransform is not (null or MatrixTransform { Matrix.IsIdentity: true })) return null;
            _move = new TranslateTransform();
            content.RenderTransform = _move;
            return _move;
        }
    }
}
