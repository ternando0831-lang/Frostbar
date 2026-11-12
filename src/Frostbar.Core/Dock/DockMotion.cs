namespace Frostbar.Core.Dock;

/// <summary>
/// The curves the Dock moves by (2026-10-17): a bounce is a ball's arc and lands as it falls; magnification and the
/// like approach where they are going quickly and settle softly, from wherever they are (a target that changes on the
/// way is followed without a jump). They moved at a constant speed and stopped dead, a bounce was a sine's arch and
/// landed at a constant speed.
/// </summary>
public static class DockMotion
{
    /// <summary>
    /// The height of a hop <paramref name="t"/> seconds into a bounce of hops <paramref name="hopSeconds"/> long: a
    /// ball's arc (a parabola), slow at the top and quickest at the bottom, where a sine's arch is softer.
    /// </summary>
    public static double Hop(double t, double hopSeconds, double height)
    {
        if (t <= 0 || hopSeconds <= 0) return 0;
        double u = t % hopSeconds / hopSeconds;
        return height * 4 * u * (1 - u);
    }

    /// <summary>
    /// How many hops of <paramref name="hopSeconds"/> have begun <paramref name="t"/> seconds into a bounce (the first
    /// at 0): the Dock's plate is lit under the icon as each begins (an app starting, 2026-10-23).
    /// </summary>
    public static int HopsBegun(double t, double hopSeconds) =>
        t < 0 || hopSeconds <= 0 ? 0 : (int)Math.Floor(t / hopSeconds) + 1;

    /// <summary>A bounce stopped at <paramref name="from"/> lands within <paramref name="seconds"/>, falling (quicker as it goes).</summary>
    public static double Land(double from, double t, double seconds)
    {
        if (seconds <= 0 || t >= seconds) return 0;
        double u = Math.Max(0, t) / seconds;
        return from * (1 - u * u);
    }

    /// <summary>
    /// One step of <paramref name="dt"/> seconds of <paramref name="value"/> towards <paramref name="target"/>: within
    /// 1% of the way after <paramref name="seconds"/> (then there), quickest at first, never past it.
    /// </summary>
    public static double Approach(double value, double target, double dt, double seconds, double snap = 0.01)
    {
        if (dt <= 0) return value;
        if (seconds <= 0) return target;
        double next = value + (target - value) * (1 - Math.Exp(-dt * 4.6 / seconds)); // e^-4.6 ≈ 1%
        return Math.Abs(target - next) <= snap ? target : next;
    }

    /// <summary>
    /// A spring let go (0–1 → 0 to 1, passing it): an underdamped spring's step response, from rest gathering speed and
    /// most of the way there early (past half by a fifth of the time at ζ 0.6), over its mark
    /// by e^(−πζ/√(1−ζ²)) of the way (ζ = <paramref name="damping"/>: 0.75 about 3%, 0.6 about 9%, 0.35 about 31%),
    /// back, and settled by the end, where it is exactly 1 (macOS 27's glass, 2026-10-18: the user's "なんか動きが硬い").
    /// </summary>
    public static double Spring(double t, double damping)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        double z = Math.Clamp(damping, 0.05, 0.95);
        double w = 4.6 / z; // the swing's envelope e^(−ζωt) is down to 1% at the end
        double wd = w * Math.Sqrt(1 - z * z);
        double x = 1 - Math.Exp(-z * w * t) * (Math.Cos(wd * t) + z * w / wd * Math.Sin(wd * t));
        return x + (1 - x) * Math.Pow(t, 6); // what is left of the swing at the end, taken away there
    }
}
