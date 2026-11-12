namespace Frostbar.Core.Shell;

/// <summary>A plate or a piece of glass: where it is (DIPs or pixels, as the caller keeps them) and its corners' radius.</summary>
public readonly record struct RoundedBox(double X, double Y, double Width, double Height, double Radius)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>
/// The light of a press spilling onto the glass around it (2026-10-23, the user's "A 沈む＋隣へ光る"; Apple, WWDC25 "Meet
/// Liquid Glass": the light starts under the finger and spreads through the element "and onto nearby Liquid Glass
/// elements"). Each piece of glass within <see cref="Reach"/> of the press glows at its point nearest the press, its rim
/// brighter there, by how near it is; the light rises over the first <see cref="MotionTable.SpillRise"/> of
/// <see cref="MotionTable.Spill"/> and fades over the rest. Until then the light stayed inside the pressed part.
/// </summary>
public static class GlassSpill
{
    /// <summary>How far the light reaches (DIPs).</summary>
    public const double Reach = 180;

    /// <summary>The glow's radius on a piece it reaches (DIPs), and its alpha and the rim's at full strength (a piece touching the press).</summary>
    public const double GlowRadius = 90, Glow = 0.34, Rim = 0.5;

    /// <summary>
    /// The point of <paramref name="box"/> nearest (<paramref name="x"/>, <paramref name="y"/>), its corners measured to
    /// their arcs, and how far it is (0: the point is on or inside it).
    /// </summary>
    public static (double x, double y, double distance) Nearest(RoundedBox box, double x, double y)
    {
        double r = Math.Max(0, Math.Min(box.Radius, Math.Min(box.Width, box.Height) / 2));
        // The rounded box is the inner one (each side in by the radius) grown by the radius all round.
        double qx = Math.Clamp(x, box.X + r, box.Right - r), qy = Math.Clamp(y, box.Y + r, box.Bottom - r);
        double dx = x - qx, dy = y - qy, d = Math.Sqrt(dx * dx + dy * dy);
        if (d <= r) return (x, y, 0);
        return (qx + dx / d * r, qy + dy / d * r, d - r);
    }

    /// <summary>The light's strength at a piece <paramref name="distance"/> away (in the units of <paramref name="reach"/>): 1 touching, 0 at the reach.</summary>
    public static double Strength(double distance, double reach = Reach) =>
        reach <= 0 || distance >= reach ? 0 : Math.Clamp(1 - distance / reach, 0, 1);

    /// <summary>The light <paramref name="t"/> (0–1) of the way through <see cref="MotionTable.Spill"/>: up gently, then away.</summary>
    public static double Envelope(double t)
    {
        if (t <= 0 || t >= 1) return 0;
        const double rise = MotionTable.SpillRise;
        return t < rise ? MotionTable.SineInOut(t / rise) : 1 - MotionTable.Smooth((t - rise) / (1 - rise));
    }

    /// <summary>
    /// The pieces a press at (<paramref name="x"/>, <paramref name="y"/>) lights: for each of <paramref name="boxes"/>
    /// within <paramref name="reach"/> (the units the boxes are in), its index, the point it is lit at and how strongly.
    /// <paramref name="own"/> (an index, or -1) is the piece pressed: lit by its own light, not this.
    /// </summary>
    public static IReadOnlyList<(int index, double x, double y, double strength)> Spread(IReadOnlyList<RoundedBox> boxes, double x, double y, int own = -1, double reach = Reach)
    {
        var lit = new List<(int, double, double, double)>();
        for (int i = 0; i < boxes.Count; i++)
        {
            if (i == own || boxes[i].Width <= 0 || boxes[i].Height <= 0) continue;
            var (nx, ny, d) = Nearest(boxes[i], x, y);
            double s = Strength(d, reach);
            if (s > 0) lit.Add((i, nx, ny, s));
        }
        return lit;
    }
}
