namespace Frostbar.Core.Shell;

/// <summary>
/// How much of the lens a plate shows as it comes and goes (0: not there, 1: all its glass; <see cref="LensLook.At"/>):
/// Liquid Glass materializing by its light bending and lensing, not by a fade (Apple, WWDC25 "Meet Liquid Glass"; the
/// user's "A レンズの強さで実体化", 2026-10-23). A menu's content is there from its first frame as before; its glass
/// gathers under it. Going, the content goes first and the glass after.
/// </summary>
public static class LensPresence
{
    /// <summary>Coming (<see cref="MotionTable.PresenceIn"/>, or a panel's fade in): quickly, settling.</summary>
    public static double Open(double t) => MotionTable.Out(t);

    /// <summary>Going (<see cref="MotionTable.PresenceOut"/>): gentle at both ends.</summary>
    public static double Close(double t) => 1 - MotionTable.Smooth(t);

    /// <summary>The content's opacity as its glass goes: gone by <paramref name="share"/> of the way (a menu's: <see cref="MotionTable.PresenceContentOut"/>).</summary>
    public static double ContentClose(double t, double share = MotionTable.PresenceContentOut) =>
        share <= 0 ? 0 : 1 - MotionTable.Smooth(Math.Min(1, t / share));

    /// <summary>
    /// A panel of pieces growing out of its menu bar item (the Control Center, the Notification Center): its glass ahead
    /// of its content, as its plate's fade was (Fades: full by 1 / 2.6 of the way).
    /// </summary>
    public static double Materialize(double t) => MotionTable.Out(Math.Min(1, t * 2.6));

    /// <summary>Going back into its item: the glass after the content (as its plate faded: from 0.3 of the way, on a square).</summary>
    public static double Dematerialize(double t)
    {
        double g = Math.Clamp((t - 0.3) / 0.7, 0, 1);
        return 1 - g * g;
    }

    /// <summary>
    /// Whether a plate at <paramref name="plate"/> may come by its lens: not over another plate with a lens (a submenu
    /// over its parent). The shell's glass is left out of the capture, so what such a plate's lens reads there is what is
    /// behind both: a plate there part of the way in would show a hole through the other; it fades as before.
    /// </summary>
    public static bool Allowed(ScreenRect plate, IEnumerable<ScreenRect> otherLensPlates) =>
        !otherLensPlates.Any(o => LensDamage.Meet(plate, o));
}
