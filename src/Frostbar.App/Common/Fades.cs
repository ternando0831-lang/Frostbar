using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Interop;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Common;

/// <summary>
/// The shell's appearing and disappearing, macOS's timings: menus open at once and fade out as they close; panels like
/// the Control Center and the search panel fade in and out; windows (settings, dialogs) appear with a small zoom and
/// fade out. A window with a <see cref="WindowBackdrop"/> fades in two parts at once: its WPF content (opacity, WPF's
/// clock) and its blurred plate (DWM's clock). Nothing slides or zooms with a plate under it: the plate is another
/// window and would part from the content (the zoom is for windows that draw their own background).
/// </summary>
public static class Fades
{
    public const double MenuOutMs = Motion.MenuOut, PanelInMs = Motion.PanelIn, PanelOutMs = Motion.PanelOut, WindowInMs = Motion.WindowIn, WindowOutMs = Motion.WindowOut;

    /// <summary>A window's content zooms in from this scale as it appears (<see cref="Attach"/>).</summary>
    public const double ZoomFrom = 0.94;

    /// <summary>
    /// macOS 27's glass materializing (the user's "B", 2026-10-18; <c>Lab mock motion</c>): a panel of pieces grows out of
    /// its menu bar item from this scale, its glass first and its content a little after, and goes back into it.
    /// </summary>
    public const double MaterializeFrom = 0.6, MaterializeInMs = Motion.MaterializeIn, MaterializeOutMs = Motion.MaterializeOut;

    /// <summary>
    /// How springy the growing is (Core's DockMotion.Spring: past full size by about 3% of the way, back, settled): the
    /// user's "なんか動きが硬い" (2026-10-18) after it eased out and stopped.
    /// </summary>
    public const double MaterializeDamping = 0.72;

    /// <summary>After each frame of the fades (pieces of glass follow content scaled without a layout pass).</summary>
    public static event Action? Stepped;

    static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Fades <paramref name="w"/> out over <paramref name="ms"/>, then closes it (at once for 0). Presses go through it meanwhile.</summary>
    public static void CloseFading(Window w, WindowBackdrop backdrop, double ms, double contentShare = 1) => FadeOut(w, backdrop, ms, w.Close, contentShare);

    /// <summary>
    /// Fades <paramref name="w"/> out over <paramref name="ms"/>, then calls <paramref name="done"/> (at once for 0). Presses
    /// go through it meanwhile (<see cref="PassPresses"/>); <see cref="Reset"/> puts it back (a window shown again, or a
    /// fade cut short: callers that may cut it short check in <paramref name="done"/> that it is still wanted).
    /// </summary>
    /// <param name="contentShare">
    /// The share of <paramref name="ms"/> the content takes to go (1: all of it). A plate that comes by its lens goes by it
    /// (its presence down over all of <paramref name="ms"/>), its content first (a menu's: by 0.6 of it, 2026-10-23).
    /// </param>
    public static void FadeOut(Window w, WindowBackdrop? backdrop, double ms, Action done, double contentShare = 1)
    {
        Appearing.RemoveAll(a => ReferenceEquals(a.Window, w)); // a fade in cut short: the fade out goes on from there
        if (ms <= 0 || !w.IsVisible || w.Content is not UIElement root) { done(); return; }
        w.IsHitTestVisible = false;
        PassPresses(w);
        // By its lens (the user's "A レンズの強さで実体化"): the presence stepped at each frame here; else DWM fades the plate.
        bool lensy = backdrop?.Presents == true && !ReduceMotion.Calms("presence");
        if (!lensy) backdrop?.FadePlate(0, ms, smooth: true);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        bool layered = w.AllowsTransparency && hwnd != IntPtr.Zero;
        Action<double>? step = null;
        if (lensy || layered && contentShare < 1)
            step = t =>
            {
                double content = Frostbar.Core.Shell.LensPresence.ContentClose(t, contentShare);
                if (layered) Native.SetLayeredOpacity(hwnd, content);
                if (lensy) backdrop!.Appear((float)Frostbar.Core.Shell.LensPresence.Close(t));
            };
        bool finished = false;
        Fade? fade = null;
        void Finish()
        {
            if (finished) return;
            finished = true;
            if (fade != null) Fading.Remove(fade);
            done();
        }
        fade = new Fade(w, layered ? hwnd : IntPtr.Zero, Stopwatch.GetTimestamp(), ms, Finish, Cut: () =>
        {
            // Cut short: ended at once. Not later: menus reopened every 35 ms then left up to nine closing windows alive
            // at a time, and the shell kept 300–450 more GDI objects and 100 MB for good (2026-10-12, menu-cycles).
            if (finished) return;
            if (!layered) root.BeginAnimation(UIElement.OpacityProperty, null);
            w.Hide();
            Finish();
        }, Stop: () =>
        {
            // Called back: the window stays (it is shown again), without the fade's ending.
            if (finished) return;
            finished = true;
            Fading.Remove(fade!);
            if (!layered) root.BeginAnimation(UIElement.OpacityProperty, null);
        }, Step: step);
        Fading.Add(fade);
        if (step != null) Clock(w.Dispatcher).Start();
        if (layered)
        {
            // DWM fades what WPF drew last (the window's layered alpha): nothing is drawn again. A WPF opacity animation
            // drew the menu again every frame, and opening the next menu meanwhile took 34 ms instead of 18 (p50,
            // 2026-10-12: fading only the blurred plate left it at 18).
            Clock(w.Dispatcher).Start();
        }
        else
        {
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out };
            a.Completed += (_, _) => Finish();
            root.BeginAnimation(UIElement.OpacityProperty, a);
        }
        // Should the fade never complete (the window hidden meanwhile), finish anyway.
        var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms + 250) };
        guard.Tick += (_, _) => { guard.Stop(); Finish(); };
        guard.Start();
    }

    /// <summary>
    /// A fade out in progress: its window (and its handle when DWM fades it), when it started, how long, how it ends,
    /// how it is cut short (gone at once) and how it is called back (stays, the fade forgotten).
    /// </summary>
    sealed record Fade(Window Window, IntPtr Hwnd, long Start, double Ms, Action Finish, Action Cut, Action Stop, Action<double>? Step = null);

    /// <summary>The windows <see cref="PassPresses"/> made click-through (not those that are so of themselves): <see cref="Reset"/> puts them back.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> Passing = new();

    /// <summary>
    /// A layered window going away lets presses through to what is behind it (WS_EX_TRANSPARENT) until <see cref="Reset"/>.
    /// Its IsHitTestVisible off kept WPF from routing them, but the window still took them from Windows, and a press where
    /// a menu had just been chosen was lost (2026-10-24, grid-menu: "hit" answered Frostbar.Menu). Its click shields are gone
    /// by then, so a press outside it already went to the app below.
    /// </summary>
    /// <remarks>
    /// Also for the full-screen windows that fade out their own way (the app grid, the overview): a click into the app
    /// just opened or chosen went to their fading window and was lost.
    /// </remarks>
    public static void PassPresses(Window w)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (!w.AllowsTransparency || hwnd == IntPtr.Zero || (Native.ExStyle(hwnd) & Native.WS_EX_TRANSPARENT) != 0) return;
        Native.AddExStyle(hwnd, Native.WS_EX_TRANSPARENT);
        Passing.AddOrUpdate(w, true);
    }

    /// <summary>Its presses its own again after <see cref="PassPresses"/> (a window shown again; <see cref="Reset"/> does it too).</summary>
    public static void TakePresses(Window w)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (Passing.Remove(w) && hwnd != IntPtr.Zero) Native.SetExStyle(hwnd, Native.ExStyle(hwnd) & ~Native.WS_EX_TRANSPARENT);
    }

    static readonly List<Fade> Fading = new();
    static FrameClock? _clock;

    // A panel's fade in and a stack's rise begin once the window has been drawn (AfterDrawn: after WPF's render pass),
    // and go by the display's refreshes (DisplaySteps). Begun at once, they lost their first frames to the window's
    // making (a stack showed nothing for 100 ms and then most of its rise was over), and WPF's own clock came out of its
    // rest unevenly (2026-10-17, frame-pace: pictures 33 ms apart in the Control Center's 150 ms fade).

    /// <summary>A window gliding to a place: from, to (DIPs), how long; its steps once it is drawn.</summary>
    /// <param name="Away">Going (easing in, quicker as it goes: a stack sinking back into the Dock); else coming (easing out).</param>
    sealed record Glide(Window Window, double FromX, double FromY, double ToX, double ToY, double Ms, bool Away = false, double Spring = 0)
    {
        public DisplaySteps Steps { get; } = new();
    }
    static readonly List<Glide> Gliding = new();

    /// <summary>
    /// A window's content and plate fading in: how long; its steps once it is drawn. With <paramref name="Center"/> it
    /// materializes instead: grows from there (the content's coordinates), its plate before its content.
    /// </summary>
    sealed record Appear(Window Window, UIElement Root, WindowBackdrop? Backdrop, double Ms, ScaleTransform? Zoom, Func<Point>? Center = null, bool PresenceOnly = false)
    {
        public DisplaySteps Steps { get; } = new();

        /// <summary>Its glass fades in even where it could come by its lens (reduced motion).</summary>
        public bool FadeGlass { get; init; }
    }

    /// <summary>Scales <paramref name="root"/> by <paramref name="k"/> about <paramref name="c"/> (its own coordinates).</summary>
    static void ScaleAbout(UIElement root, Point c, double k)
    {
        if (root.RenderTransform is not ScaleTransform st || st.IsFrozen) root.RenderTransform = st = new ScaleTransform();
        st.CenterX = c.X;
        st.CenterY = c.Y;
        st.ScaleX = st.ScaleY = k;
    }

    static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    static readonly List<Appear> Appearing = new();

    /// <summary>Runs <paramref name="begin"/> once WPF has drawn what is pending (a window just shown: its first frame).</summary>
    static void AfterDrawn(Window w, Action begin) =>
        w.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { begin(); Clock(w.Dispatcher).Start(); });

    /// <summary>One display-paced clock for every layered fade and glide (a thread waiting on DWM's frames while any runs).</summary>
    static FrameClock Clock(Dispatcher ui)
    {
        if (_clock != null) return _clock;
        _clock = new FrameClock(ui);
        _clock.Tick += _ =>
        {
            long now = Stopwatch.GetTimestamp();
            foreach (var f in Fading.ToList())
            {
                if (f.Hwnd == IntPtr.Zero && f.Step == null) continue; // a WPF animation of its own
                double t = Math.Min(1, (now - f.Start) / (double)Stopwatch.Frequency * 1000 / f.Ms);
                if (f.Step != null) f.Step(t); // going back into its item (Dematerialize)
                else Native.SetLayeredOpacity(f.Hwnd, 1 - Motion.Smooth(t)); // as the plate (FadePlate smooth)
                if (t >= 1) f.Finish();
            }
            foreach (var g in Gliding.ToList())
            {
                if (!g.Window.IsVisible) { Gliding.Remove(g); continue; } // closed on its way
                if (!g.Steps.Begun) continue;
                double t = g.Steps.Next(g.Ms);
                double k = g.Away ? Motion.In(t) : g.Spring > 0 ? Motion.Spring(t, g.Spring) : Motion.Out(t); // ease in going, out (or a spring) coming
                g.Window.Left = Math.Round(g.FromX + (g.ToX - g.FromX) * k, 2);
                g.Window.Top = Math.Round(g.FromY + (g.ToY - g.FromY) * k, 2);
                if (t >= 1) Gliding.Remove(g);
            }
            foreach (var a in Appearing.ToList())
            {
                if (!a.Window.IsVisible) { Appearing.Remove(a); continue; }
                if (!a.Steps.Begun) continue;
                double t = a.Steps.Next(a.Ms);
                double k = Motion.Out(t);
                if (a.PresenceOnly)
                {
                    // A menu: its content there at once, its glass coming by its lens (2026-10-23).
                    a.Backdrop?.Appear(1, (float)Frostbar.Core.Shell.LensPresence.Open(t));
                    if (t >= 1) Appearing.Remove(a);
                    continue;
                }
                if (a.Center != null)
                {
                    // Materializing: the glass grows out of its item on a spring and comes first; the content follows.
                    // By its lens where it has one (its presence, eased), else faded as before.
                    ScaleAbout(a.Root, a.Center(), MaterializeFrom + (1 - MaterializeFrom) * Frostbar.Core.Dock.DockMotion.Spring(t, MaterializeDamping));
                    // Its pieces come out as one drop and part as they reach their places (Core's LiquidJoin; set before the
                    // pieces are placed, which the step's end does).
                    if (a.Backdrop != null) a.Backdrop.Droplets = t >= 1 ? 0 : Frostbar.Core.Shell.LiquidJoin.Coming(t);
                    a.Backdrop?.Appear((float)Math.Min(1, t * 2.6), (float)Frostbar.Core.Shell.LensPresence.Materialize(t));
                    a.Root.Opacity = Clamp01((t - 0.2) / 0.35);
                    if (t >= 1) Appearing.Remove(a);
                    continue;
                }
                a.Root.Opacity = k;
                a.Backdrop?.Appear((float)k, fade: a.FadeGlass);
                if (a.Zoom != null)
                {
                    // WPF's BackEase out, amplitude 0.25: a little past full size, then back.
                    double u = 1 - t, back = 1 - (u * u * u - u * 0.25 * Math.Sin(Math.PI * u));
                    a.Zoom.ScaleX = a.Zoom.ScaleY = ZoomFrom + (1 - ZoomFrom) * back;
                }
                if (t >= 1) Appearing.Remove(a);
            }
            Stepped?.Invoke();
            if (!Fading.Any(f => f.Hwnd != IntPtr.Zero || f.Step != null) && Gliding.Count == 0 && Appearing.Count == 0) _clock!.Stop();
        };
        return _clock;
    }

    /// <summary>
    /// Moves <paramref name="w"/> by (<paramref name="dx"/>, <paramref name="dy"/>) DIPs at once and glides it back to
    /// where it is over <paramref name="ms"/> (easing out, or on a spring of that damping): it comes from there. The
    /// window itself moves, so a <see cref="WindowBackdrop"/>'s plate stays under it (a content's own transform would
    /// part from the plate).
    /// </summary>
    public static void ComeFrom(Window w, double dx, double dy, double ms, double spring = 0)
    {
        Gliding.RemoveAll(g => ReferenceEquals(g.Window, w));
        if (ms <= 0 || dx == 0 && dy == 0 || ReduceMotion.Calms("comeFrom")) return; // reduced motion: it fades where it is
        double x = w.Left, y = w.Top;
        w.Left = x + dx;
        w.Top = y + dy;
        var glide = new Glide(w, x + dx, y + dy, x, y, ms, Spring: spring);
        Gliding.Add(glide);
        AfterDrawn(w, glide.Steps.Begin);
    }

    /// <summary>
    /// Glides <paramref name="w"/> from where it is by (<paramref name="dx"/>, <paramref name="dy"/>) DIPs over
    /// <paramref name="ms"/>, quicker as it goes: it goes there (a stack sinking back into the Dock as it fades out;
    /// it only faded where it was, 2026-10-19, the user's "A6"). The window itself moves, its plate with it.
    /// </summary>
    public static void GoBy(Window w, double dx, double dy, double ms)
    {
        Gliding.RemoveAll(g => ReferenceEquals(g.Window, w));
        if (ms <= 0 || dx == 0 && dy == 0 || ReduceMotion.Calms("goBy")) return;
        var glide = new Glide(w, w.Left, w.Top, w.Left + dx, w.Top + dy, ms, Away: true);
        Gliding.Add(glide);
        AfterDrawn(w, glide.Steps.Begin);
    }

    /// <summary>Stops <paramref name="w"/>'s glide where it is (something else moves it now).</summary>
    public static void StopGliding(Window w) => Gliding.RemoveAll(g => ReferenceEquals(g.Window, w));

    /// <summary>How long a shake takes (ms), how far it first goes (DIPs), and how many times it goes there and back.</summary>
    public const double ShakeMs = Motion.Shake, ShakeBy = 12, ShakeTimes = 3;

    /// <summary>Shakes since the start (diagnostics).</summary>
    public static int Shakes { get; private set; }

    /// <summary>
    /// macOS's "no": <paramref name="w"/> shakes from side to side, less each time, and stands where it was (a wrong
    /// password). It stood still with a note under the field (2026-10-19, the user's "B13"). Nothing with animations
    /// off; a shake under way goes on (a second from where it is would leave the window off its place).
    /// </summary>
    public static void Shake(Window w, bool animate)
    {
        if (!animate || !w.IsVisible || Tween.Moving(w, Window.LeftProperty)) return;
        if (ReduceMotion.Calms("shake")) return; // reduced motion: the "no" is said by its note under the field only
        Shakes++;
        // From where it stands once laid out: what changed with the "no" (a note under the field) may have centred it
        // again (a dialog keeps its middle as it grows); begun before, the shake put it back off its middle (9 px, 2026-10-20).
        w.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (!w.IsVisible || Tween.Moving(w, Window.LeftProperty)) return;
            Tween.To(w, Window.LeftProperty, w.Left + ShakeBy, ShakeMs, k => Math.Sin(k * ShakeTimes * 2 * Math.PI) * (1 - k));
        });
    }

    /// <summary><paramref name="w"/> is still gliding to its place (diagnostics).</summary>
    public static bool IsGliding(Window w) => Gliding.Any(g => ReferenceEquals(g.Window, w));

    /// <summary>Where <paramref name="w"/> is coming to (its left and top), while it glides there (<see cref="ComeFrom"/>); else null.</summary>
    public static Point? ComingTo(Window w) => Gliding.FirstOrDefault(g => ReferenceEquals(g.Window, w) && !g.Away) is { } g ? new Point(g.ToX, g.ToY) : null;

    /// <summary>
    /// Ends every fade still running at once: called as a menu or panel opens. A menu still fading out slowed the next
    /// menu's opening (2026-10-12, menus reopened 0.1 s after closing).
    /// </summary>
    public static void CutShort()
    {
        foreach (var f in Fading.ToList()) f.Cut();
    }

    /// <summary>
    /// Fully shown again at once, taking input (before showing a window that faded out, or one called back while it
    /// fades: its fade stops, or DWM would go on fading it out while it is open again).
    /// </summary>
    public static void Reset(Window w, WindowBackdrop? backdrop)
    {
        foreach (var f in Fading.Where(f => ReferenceEquals(f.Window, w)).ToList()) f.Stop();
        Appearing.RemoveAll(a => ReferenceEquals(a.Window, w));
        w.IsHitTestVisible = true;
        backdrop?.Appear(1);
        w.BeginAnimation(UIElement.OpacityProperty, null);
        w.Opacity = 1;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (w.AllowsTransparency && hwnd != IntPtr.Zero) Native.SetLayeredOpacity(hwnd, 1); // after a layered fade
        TakePresses(w); // and its presses its own again
        if (w.Content is not UIElement root) return;
        root.BeginAnimation(UIElement.OpacityProperty, null);
        root.Opacity = 1;
        if (root.RenderTransform is ScaleTransform z)
        {
            z.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            z.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            z.ScaleX = z.ScaleY = 1;
        }
    }

    /// <summary>
    /// Fades <paramref name="w"/>'s content and plate in over <paramref name="ms"/> (nothing for 0), both set at each
    /// display frame from when the window has been drawn (<see cref="Steps"/>): the plate was DWM's animation and the
    /// content WPF's, each on its own clock from the moment of the call. Called before the window is shown: shown first,
    /// a layered window drew its content once at full strength without its plate, a frame before the fade (2026-10-17,
    /// frame-pace: the Control Center's first picture in 3 runs of 4 in that spec's flow; in none of 4 since. Why only
    /// there is not known).
    /// </summary>
    /// <param name="zoom">The content's own scale, zoomed in from <see cref="ZoomFrom"/> along (a window's appearing).</param>
    /// <param name="center">
    /// Materializes instead of fading (a panel of pieces of glass, macOS 27): its content grows from
    /// <see cref="MaterializeFrom"/> about this point (the content's coordinates, asked at each frame: the window may
    /// be moved after this call), the plate (its pieces) ahead of the content.
    /// </param>
    public static void FadeIn(Window w, WindowBackdrop? backdrop, double ms, ScaleTransform? zoom = null, Func<Point>? center = null)
    {
        if (ms <= 0 || w.Content is not UIElement root) return;
        // Reduced motion: it fades in at its size and place (no zoom, nothing growing out of its item).
        if ((zoom != null || center != null) && ReduceMotion.Calms(center != null ? "materialize" : "zoomIn"))
        {
            if (zoom != null) zoom.ScaleX = zoom.ScaleY = 1;
            // A panel that grew out of its item last time keeps its scale's transform: at its full size.
            if (center != null && root.RenderTransform is ScaleTransform { IsFrozen: false } grown) grown.ScaleX = grown.ScaleY = 1;
            zoom = null;
            center = null;
        }
        Appearing.RemoveAll(a => ReferenceEquals(a.Window, w));
        root.BeginAnimation(UIElement.OpacityProperty, null);
        root.Opacity = 0;
        // Reduced motion: the glass fades in, not by its lens (its magnification and bend growing in), as it fades out.
        bool fadeGlass = backdrop?.Presents == true && ReduceMotion.Calms("presence");
        backdrop?.Appear(0, fade: fadeGlass); // by its lens: whole, its presence at 0 (what is behind as it is); else faded out
        if (zoom != null) zoom.ScaleX = zoom.ScaleY = ZoomFrom;
        if (center != null) ScaleAbout(root, center(), MaterializeFrom);
        // A layered window shown again shows what WPF drew last until it draws again: unseen until then (and its
        // content at 0 from the first frame it draws).
        IntPtr Layered() => w.AllowsTransparency ? new System.Windows.Interop.WindowInteropHelper(w).Handle : IntPtr.Zero;
        if (Layered() is var hidden && hidden != IntPtr.Zero) Native.SetLayeredOpacity(hidden, 0);
        var appear = new Appear(w, root, backdrop, ms, zoom, center) { FadeGlass = fadeGlass };
        Appearing.Add(appear);
        AfterDrawn(w, () =>
        {
            if (Layered() is var h && h != IntPtr.Zero) Native.SetLayeredOpacity(h, 1);
            if (center != null) ScaleAbout(root, center(), MaterializeFrom); // where the window stands now
            appear.Steps.Begin();
        });
    }

    /// <summary>
    /// A menu's glass coming by its lens over <paramref name="ms"/> (the user's "A レンズの強さで実体化", 2026-10-23): its
    /// content is there from the first frame as before (menus open at once), its lens gathers under it from nothing
    /// (what is behind as it is) to all of it. Nothing for a plate without a lens (it is there at once, as before).
    /// Called before the window is shown.
    /// </summary>
    public static void Materialize(Window w, WindowBackdrop backdrop, double ms)
    {
        if (ms <= 0 || !backdrop.Presents || ReduceMotion.Calms("presence") || w.Content is not UIElement root) return;
        Appearing.RemoveAll(a => ReferenceEquals(a.Window, w));
        backdrop.Appear(0);
        var appear = new Appear(w, root, backdrop, ms, null, PresenceOnly: true);
        Appearing.Add(appear);
        AfterDrawn(w, appear.Steps.Begin);
    }

    /// <summary>
    /// A panel of pieces going back into its menu bar item (macOS 27, the user's "B"): its content fades in the first
    /// half while all of it shrinks to <see cref="MaterializeFrom"/> about <paramref name="center"/>, its glass fades
    /// after, then <paramref name="done"/>. Presses go through it meanwhile; cut short or called back as a fade out is.
    /// </summary>
    public static void Dematerialize(Window w, WindowBackdrop backdrop, Func<Point> center, double ms, Action done)
    {
        Appearing.RemoveAll(a => ReferenceEquals(a.Window, w));
        if (ms <= 0 || !w.IsVisible || w.Content is not UIElement root) { done(); return; }
        w.IsHitTestVisible = false;
        PassPresses(w);
        bool finished = false;
        Fade? fade = null;
        void Finish()
        {
            if (finished) return;
            finished = true;
            if (fade != null) Fading.Remove(fade);
            done();
        }
        double opacity0 = root.Opacity;
        bool still = ReduceMotion.Calms("dematerialize"); // reduced motion: it fades where it is, its glass with it
        fade = new Fade(w, IntPtr.Zero, Stopwatch.GetTimestamp(), ms, Finish, Cut: () =>
        {
            if (finished) return;
            w.Hide();
            Finish();
        }, Stop: () =>
        {
            if (finished) return;
            finished = true;
            Fading.Remove(fade!);
        }, Step: t =>
        {
            if (still)
            {
                root.Opacity = opacity0 * (1 - t);
                backdrop.Appear((float)(1 - t), fade: true); // not by its lens either (its bend going is a motion)
                return;
            }
            double s = t * t; // drawn in, faster as it goes
            backdrop.Droplets = Frostbar.Core.Shell.LiquidJoin.Going(t); // its pieces run together again as they are drawn in
            ScaleAbout(root, center(), 1 - (1 - MaterializeFrom) * s);
            root.Opacity = opacity0 * (1 - Clamp01(t / 0.45));
            backdrop.Appear((float)Frostbar.Core.Shell.LensPresence.Dematerialize(t)); // the glass after the content: by its lens where it has one
        });
        Fading.Add(fade);
        Clock(w.Dispatcher).Start();
        var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms + 250) };
        guard.Tick += (_, _) => { guard.Stop(); Finish(); };
        guard.Start();
    }

    /// <summary>Something fades or glides: work that can wait (an idle moment's step) waits until it is over.</summary>
    public static bool Busy => Appearing.Count > 0 || Gliding.Count > 0 || Fading.Count > 0;

    /// <summary>A fade in still under way (diagnostics).</summary>
    public static bool IsAppearing(Window w) => Appearing.Any(a => ReferenceEquals(a.Window, w));

    /// <summary>
    /// A window's own appearing and closing (settings, dialogs): it zooms in from a little smaller as it fades in when
    /// first shown, and fades out when closed, however it is closed (its button, Esc, Alt+F4). <paramref name="animate"/>
    /// is asked each time (the setting may change while it is open).
    /// </summary>
    public static void Attach(Window w, WindowBackdrop? backdrop, Func<bool> animate)
    {
        bool faded = false;
        w.SourceInitialized += (_, _) =>
        {
            if (!animate() || w.Content is not FrameworkElement root) return;
            // The zoom: content only (a backdrop's plate would part from it); centred.
            var z = new ScaleTransform(ZoomFrom, ZoomFrom);
            root.RenderTransformOrigin = new Point(0.5, 0.5);
            root.RenderTransform = z;
            FadeIn(w, backdrop, WindowInMs, z);
        };
        w.Closing += (_, e) =>
        {
            if (faded || e.Cancel || !animate() || !w.IsVisible) return;
            e.Cancel = true;
            faded = true;
            FadeOut(w, backdrop, WindowOutMs, w.Close);
        };
    }
}
