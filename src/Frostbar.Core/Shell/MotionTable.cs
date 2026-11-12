using Frostbar.Core.Dock;

namespace Frostbar.Core.Shell;

/// <summary>
/// The shell's movements in one table (2026-10-19, the user's "B15"): the curves, and how long each kind of movement
/// takes. The cubic ease out was written out in five places, and two different curves were both called EaseInOut (the
/// Dock's growing slots a smoothstep, its sliding away a cubic); the times were numbers in some thirty files. A part
/// names its row here (a segmented control's slide is <see cref="Segment"/>): a time changed here changes that kind of
/// movement everywhere it shows. Nothing moves differently for the table: each row is the time the part had.
/// </summary>
public static class MotionTable
{
    // ---- curves (0–1 → 0–1, exactly 0 and 1 at the ends) ----------------------------------------------------------

    /// <summary>Cubic out: quick to start, settling. Most things coming into place.</summary>
    public static double Out(double t) => t <= 0 ? 0 : t >= 1 ? 1 : 1 - Math.Pow(1 - t, 3);

    /// <summary>Cubic in: slow to start, quickest at the end. Things going away (a stack sinking back into the Dock).</summary>
    public static double In(double t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * t;

    /// <summary>Cubic in and out: gentle at both ends. A thing going out of sight (the Dock hiding).</summary>
    public static double InOut(double t) => t <= 0 ? 0 : t >= 1 ? 1 : t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    /// <summary>
    /// Smoothstep (3t² − 2t³): gentle at both ends, less so than <see cref="InOut"/> (the middle is not as steep). The
    /// Dock's slots growing and shrinking, a layered window fading out with its plate.
    /// </summary>
    public static double Smooth(double t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - 2 * t);

    /// <summary>Sine in and out: the gentlest at both ends. Fades (a list's row, a card's, a page's).</summary>
    public static double SineInOut(double t) => t <= 0 ? 0 : t >= 1 ? 1 : (1 - Math.Cos(Math.PI * t)) / 2;

    /// <summary>A spring let go: past its mark by its <paramref name="damping"/>, back, settled at the end (<see cref="DockMotion.Spring"/>).</summary>
    public static double Spring(double t, double damping) => DockMotion.Spring(t, damping);

    /// <summary>
    /// A curve as <paramref name="count"/> + 1 even steps (0 to 1 and its values), for a movement DWM runs by key frames
    /// (a press's light spilling: nothing to do on the UI thread each frame, the curves still this table's).
    /// </summary>
    public static (float at, float value)[] Sample(Func<double, double> curve, int count)
    {
        count = Math.Max(1, count);
        var keys = new (float, float)[count + 1];
        for (int i = 0; i <= count; i++)
        {
            double t = i / (double)count;
            keys[i] = ((float)t, (float)curve(t));
        }
        return keys;
    }

    // ---- times (ms) ------------------------------------------------------------------------------------------------

    // Appearing and going (Fades): menus open at once and fade out; panels fade in and out; windows zoom in a little.
    public const double MenuOut = 180, PanelIn = 150, PanelOut = 160, WindowIn = 200, WindowOut = 150;
    /// <summary>macOS 27's glass: a panel of pieces growing out of its item, and going back into it.</summary>
    public const double MaterializeIn = 380, MaterializeOut = 200;
    /// <summary>A Dock stack rising from its item; a window shaking its "no".</summary>
    public const double Rise = 200, Shake = 420;

    // The small controls
    /// <summary>A round switch's colours changing (the Control Center's), a pill switch's knob sliding.</summary>
    public const double Switch = 150, SwitchKnob = 140, Segment = 200, SliderGlide = 120, KnobPress = 120, KnobLetGo = 180;
    /// <summary>
    /// A slider's glass knob stretched along its way by its speed (<see cref="KnobStretch"/>), and settling when the
    /// pointer rests: a spring of this long and damping (it passes its round shape by a little, taller for a moment).
    /// </summary>
    public const double KnobSettle = 340, KnobSettleDamping = 0.5;
    /// <summary>A glass plate's shadow going to another depth as what lies behind it changes (<see cref="ShadowDepth"/>).</summary>
    public const double ShadowTurn = 300;
    /// <summary>Glass pressed (it shrinks) and let go (a spring back); the light spreading under a press, and fading.</summary>
    public const double BouncePress = 100, BounceRelease = 480, PressSpread = 300, PressFade = 150;
    /// <summary>
    /// A button's new glyph popping in (play turned pause), on a spring of this damping: past full size and back ("B8",
    /// 2026-10-19). Its own, not the press's: it shared the press's, and when that went to 0.7 (2026-11-01) the pop
    /// reached 1.02 where it had reached 1.15 (2026-11-06 review).
    /// </summary>
    public const double GlyphPop = 360, GlyphPopDamping = 0.32;
    /// <summary>
    /// A pressed icon's shade coming and going (it was there at once, 2026-10-23); the menu bar's capsule under a pressed
    /// item coming and going (nothing showed until its menu opened).
    /// </summary>
    public const double PressShadeIn = 60, PressShadeOut = 120, BarPressIn = 60, BarPressOut = 120;
    /// <summary>A press's light spilling onto the glass around it (GlassSpill), the share of it rising.</summary>
    public const double Spill = 600, SpillRise = 0.25;
    /// <summary>The Dock's plate lit under an icon as it hops and as it lands (an app starting).</summary>
    public const double Energize = 600;
    /// <summary>A plate's lens gathering as it comes (a menu, its content there at once) and going as it goes; the content's share of the going.</summary>
    public const double PresenceIn = 160, PresenceOut = 190, PresenceContentOut = 0.6;

    // Lists and pages
    /// <summary>A notch's glide (and the code's: a search's row brought into view).</summary>
    public const double Scroll = 200;
    /// <summary>A row coming, going or moving in a list made again.</summary>
    public const double Rows = 240;
    /// <summary>The overlay scroll bar: showing, staying after the list stops, going.</summary>
    public const double ScrollBarIn = 120, ScrollBarStay = 1000, ScrollBarOut = 300;
    /// <summary>A touchpad's stretch past a list's end: let go a moment after the last turn, back in place.</summary>
    public const double StretchRelease = 120, StretchBack = 350;
    /// <summary>A settings page fading in over the one before; the welcome's step coming in from its side.</summary>
    public const double PageFade = 180, StepTurn = 280;
    /// <summary>The search panel's list reaching a new length while it shows (its first answers come at once).</summary>
    public const double SearchGrow = 160;
    /// <summary>The search capsule's filter buttons fading in and out (the pointer over it, something typed, a filter on).</summary>
    public const double SearchButtons = 120;
    /// <summary>
    /// The Dynamic Island growing or shrinking between its shapes, on a spring (<see cref="IslandSpring"/>); what is in it
    /// waits the first <see cref="IslandContentWait"/> of that, then fades in (the iPhone's: words never squeezed); the
    /// second activity's circle fades in <see cref="IslandContentIn"/>. Not measured on Apple's: seen in its videos.
    /// </summary>
    public const double IslandMorph = 420, IslandContentIn = 160, IslandContentWait = 0.4;


    // The Dock
    /// <summary>Hiding and showing (auto-hide, another edge chosen).</summary>
    public const double DockSlide = 250;
    public const double DockMagnify = 160, DockUnmagnify = 240, DockHop = 500, DockLand = 120, DockFly = 200, DockGrow = 250;
    /// <summary>Its marks: a progress bar's value gliding, a badge popping, a running dot fading in; the poof of a pin dragged off.</summary>
    public const double MarkGlide = 250, BadgePop = 350, DotIn = 220, Poof = 340;

    // Panels and overlays
    /// <summary>The Control Center growing into a view (and back).</summary>
    public const double Morph = 380;
    /// <summary>A song's words and artwork fading out and the next ones in.</summary>
    public const double MediaOut = 90, MediaIn = 160;
    /// <summary>A notification's card going.</summary>
    public const double CardLeave = 220;
    /// <summary>A banner coming in from past the screen's edge; banners gliding to new places.</summary>
    public const double BannerEnter = 340, BannerGlide = 200;
    /// <summary>The volume display sliding; the menu bar coming down from past the top edge; its items stepping aside.</summary>
    public const double HudSlide = 120, BarSlide = 200, BarStepAside = 150;

    // The window overview and the app grid
    public const double OverviewOpen = 250, OverviewClose = 220, OverviewRearrange = 250;
    /// <summary>The frame and the close button of the window pointed at, fading in and out; the desktops' strip sliding.</summary>
    public const double OverviewPointIn = 120, OverviewPointOut = 100, StripSlide = 260;
    public const double GridPage = 320, GridOpen = 180, GridClose = 200, GridMove = 200, FolderOpen = 180, FolderClose = 160, GridFlyBack = 220;

    /// <summary>Another app's window gliding to its new place (the Window menu's tiling).</summary>
    public const double WindowGlide = 220;

    // ---- springs (the damping ζ of Spring: past the mark by e^(−πζ/√(1−ζ²)) of the way) -----------------------------
    // macOS 27's movements land on springs (the glass materializing, a press let go): these settled on a cubic ease out
    // until 2026-10-19 (the user's "B16").

    /// <summary>A segmented control's raised part reaching its choice: past it by about 1.5% of the way.</summary>
    public const double SegmentSpring = 0.8;
    /// <summary>A Dock stack rising into place: about 4.6% past.</summary>
    public const double RiseSpring = 0.7;
    /// <summary>A banner coming in from the screen's edge: about 1.5% past (some 5 DIPs).</summary>
    public const double BannerSpring = 0.8;
    /// <summary>The Dynamic Island taking a new shape: about 9% past (the iPhone's island visibly overshoots and settles).</summary>
    public const double IslandSpring = 0.6;
}

/// <summary>
/// A slider's glass knob as it is dragged (2026-11-10, the user's "アニメーションも足りないものすべて追加"; Apple's
/// sliders "preserve momentum and stretch when they are moved", WWDC25): wider along its way the faster it goes, a
/// little lower, and round again when it rests. Only the knob's look: the value it sets follows the pointer as before.
/// </summary>
public static class KnobStretch
{
    /// <summary>The stretch a DIP a millisecond gives, and the most there is (the knob 1.45 times as wide).</summary>
    public const double PerSpeed = 0.3, Most = 0.45;

    /// <summary>How much lower the knob is for each part it is wider.</summary>
    public const double Thins = 0.3;

    /// <summary>The stretch (0: round) of a knob moving at <paramref name="dipPerMs"/>, either way.</summary>
    public static double Of(double dipPerMs) => double.IsFinite(dipPerMs) ? Math.Min(Most, Math.Abs(dipPerMs) * PerSpeed) : 0;

    /// <summary>
    /// What is shown after a move: half way from what was shown to the speed's own (single moves a millisecond apart
    /// give wild speeds).
    /// </summary>
    public static double Towards(double shown, double dipPerMs) => shown + (Of(dipPerMs) - shown) * 0.5;

    /// <summary>The knob's width and height for a stretch (under 0, the spring passing round: narrower and taller), as parts of its round size.</summary>
    public static (double wide, double high) Size(double stretch)
    {
        double s = Math.Clamp(stretch, -Most, Most);
        return (1 + s, 1 - Thins * s);
    }
}
