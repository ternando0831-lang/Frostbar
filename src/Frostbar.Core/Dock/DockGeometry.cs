namespace Frostbar.Core.Dock;

public enum SlotKind { Icon, Separator }

/// <summary>
/// One laid-out slot: left edge and size in DIPs (icons are square; a separator has a fixed width), on whole pixels;
/// and its middle unrounded (<see cref="Middle"/>), which what follows it (a name, a dot) is placed from: the rounded
/// middle of an icon whose size ticks a pixel steps half a pixel back and forth.
/// </summary>
public readonly record struct Slot(SlotKind Kind, double Left, double Size, double Exact = double.NaN)
{
    public double Center => Left + Size / 2;
    public double Right => Left + Size;
    public double Middle => double.IsNaN(Exact) ? Center : Exact;
}

/// <summary>The whole layout: slots and the plate behind them (DIPs, x relative to the area's left edge).</summary>
public sealed record DockLayout(IReadOnlyList<Slot> Slots, double PlateLeft, double PlateWidth);

/// <summary>
/// Dock geometry with macOS-style magnification. Each icon grows with the pointer's distance from its *resting* centre
/// (a raised-cosine falloff over a few icons), so the sizes do not chase the pointer; the row stays centred and the
/// plate grows sideways with it. <paramref name="strength"/> (0..1) eases magnification in and out when the pointer
/// enters or leaves, so nothing jumps. Sizes snap to whole device pixels at the given scale.
/// <para>
/// Laid out without rounding first, then each slot's middle and the plate's ends are rounded to a pixel (2026-10-23,
/// the user's "Dockをカーソルでなぞってるとびくびく動いてる"). The row was the plate's rounded left end plus every
/// rounded size before a slot: a size ticking a pixel anywhere moved everything after it, and the rounded end went the
/// other way half the time, so the icons and the plate's ends shook a pixel back and forth all along the row as the
/// pointer swept (half the moves of icons that kept their size went against the way they were going). And the
/// falloff reached three icon widths, not a whole number of half steps from icon to icon: the magnified row's length
/// rose and fell between icons, and the whole row swayed (by 1.4 px with the user's 32 and 36 DIP, by 11 px magnified
/// to 64). It reaches <see cref="ReachPitches"/> steps now, with separators taken out of the distance, and the
/// magnified row keeps its length wherever the pointer is along it.
/// </para>
/// </summary>
public static class DockGeometry
{
    /// <summary>Gap between slots and at both ends of the plate when none is given (the settings' spacing gives it).</summary>
    public const double Gap = 4;
    public const double SeparatorWidth = 12;

    /// <summary>
    /// How far magnification reaches from the pointer, in steps from one icon to the next (an icon and a gap). A
    /// raised cosine spread over a whole number of steps (here 5) adds up to the same along an even row wherever it
    /// stands, so the row's length stays. 2.5 steps of the default 40 DIP icons and 8 DIP gaps are the three icon
    /// widths it reached before.
    /// </summary>
    public const double ReachPitches = 2.5;

    /// <param name="weights">
    /// How much of each slot is there, 0–1 (null: all of it): a slot coming in or going out takes that share of its
    /// width and of the gap after it, so the row opens and closes smoothly as apps come and go (macOS). The icon in it
    /// is that much smaller.
    /// </param>
    /// <param name="spacing">Gap between slots and at both ends of the plate (DIPs).</param>
    public static DockLayout Compute(IReadOnlyList<SlotKind> kinds, double areaWidth, double baseSize, double maxSize,
        double? pointerX, double strength, double scale, IReadOnlyList<double>? weights = null, double spacing = Gap)
    {
        int n = kinds.Count;
        double gap = Snap(spacing, scale);
        double W(int i) => weights == null ? 1 : Math.Clamp(weights[i], 0, 1);
        // Resting layout first: the magnification is driven by distances to these centres.
        double restWidth = gap;
        for (int i = 0; i < n; i++) restWidth += (Width(kinds[i], baseSize, scale) + gap) * W(i);
        double restLeft = (areaWidth - restWidth) / 2;
        var restCenters = new double[n];
        // A separator and half the gaps at its sides are left out of the distances: the icons stand a step apart
        // across it as everywhere else (a pointer over it is between its two neighbours).
        var flat = new List<(double from, double to)>();
        double x = restLeft + gap;
        for (int i = 0; i < n; i++)
        {
            double w = Width(kinds[i], baseSize, scale) * W(i);
            restCenters[i] = x + w / 2;
            if (kinds[i] == SlotKind.Separator) flat.Add((x - (i == 0 ? gap : gap * W(i - 1)) / 2, x + w + gap * W(i) / 2));
            x += w + gap * W(i);
        }
        double Along(double at)
        {
            double u = at;
            foreach (var (from, to) in flat) u -= Math.Clamp(at - from, 0, to - from);
            return u;
        }

        // Without rounding: the sizes, the length, and each slot's middle.
        var sizes = new double[n];
        double total = gap;
        double reach = (baseSize + gap) * ReachPitches;
        double? along = pointerX is double p ? Along(p) : null;
        for (int i = 0; i < n; i++)
        {
            double size = Width(kinds[i], baseSize, scale);
            if (kinds[i] == SlotKind.Icon && along is double a && strength > 0 && maxSize > baseSize)
            {
                // Quantized so icons at mirrored distances get exactly the same size (no rounding noise at half pixels).
                double d = Math.Round(Math.Abs(a - Along(restCenters[i])), 6);
                double k = d >= reach ? 0 : (Math.Cos(d / reach * Math.PI) + 1) / 2;
                size = baseSize + (maxSize - baseSize) * k * Math.Clamp(strength, 0, 1);
            }
            sizes[i] = W(i) >= 1 ? size : size * W(i);
            total += sizes[i] + gap * W(i);
        }
        double left = (areaWidth - total) / 2;

        // Then each slot as large as its size rounded (an icon whole pixels), round its middle: it moves only the way
        // its middle does, whatever the others do.
        var slots = new Slot[n];
        x = left + gap;
        for (int i = 0; i < n; i++)
        {
            double size = W(i) >= 1 ? Snap(sizes[i], scale) : sizes[i];
            slots[i] = new Slot(kinds[i], Place(x + sizes[i] / 2 - size / 2, scale), size, x + sizes[i] / 2);
            x += sizes[i] + gap * W(i);
        }
        double plateLeft = Place(left, scale);
        return new DockLayout(slots, plateLeft, Place(left + total, scale) - plateLeft);
    }

    /// <summary>
    /// The widest the plate gets, wherever the pointer is (full strength), found by trying the pointer at every device
    /// pixel along the resting row (each size snaps to whole pixels, so the peak is not exactly at a centre or a
    /// midpoint). The Dock's window needs no more than this, plus room for a name label: a layered window costs per
    /// pixel on every frame.
    /// </summary>
    public static double MaxPlateWidth(IReadOnlyList<SlotKind> kinds, double baseSize, double maxSize, double scale, double spacing = Gap)
    {
        const double area = 100_000;
        var rest = Compute(kinds, area, baseSize, baseSize, null, 0, scale, spacing: spacing);
        double widest = rest.PlateWidth;
        if (maxSize <= baseSize || kinds.Count == 0) return widest;
        double step = scale > 0 ? 1 / scale : 1;
        for (double x = rest.PlateLeft; x <= rest.PlateLeft + rest.PlateWidth; x += step)
            widest = Math.Max(widest, Compute(kinds, area, baseSize, maxSize, x, 1, scale, spacing: spacing).PlateWidth);
        return widest;
    }

    /// <summary>The smallest icons a Dock shrinks to when its row would not fit the screen (DIPs).</summary>
    public const double MinFitSize = 16;

    /// <summary>
    /// The sizes the Dock is drawn at when its row must fit in <paramref name="room"/> DIPs (the screen's width; macOS
    /// shrinks its Dock the same way): the icons shrink until the row at rest fits, then the magnification until the
    /// widest magnified row does (never below the icons). Whole device pixels; as given when everything fits. The row was
    /// cut off at the screen's edges instead (large icons, many apps).
    /// </summary>
    public static (double BaseSize, double MaxSize) Fit(IReadOnlyList<SlotKind> kinds, double baseSize, double maxSize, double scale,
        double room, double spacing = Gap)
    {
        int icons = kinds.Count(k => k == SlotKind.Icon);
        double px = scale > 0 ? scale : 1;
        double b = baseSize;
        if (icons > 0)
        {
            // The row at rest is its gaps and separators plus the icons: the largest whole-pixel size that fits.
            double fixedPart = Compute(kinds, 100_000, 0, 0, null, 0, scale, spacing: spacing).PlateWidth;
            double fits = Math.Floor((room - fixedPart) / icons * px) / px;
            if (fits < b) b = Math.Max(MinFitSize, fits);
        }
        double m = maxSize > baseSize ? maxSize : b; // no magnification stays none
        if (m > b && MaxPlateWidth(kinds, b, m, scale, spacing) > room)
        {
            // The magnified row is wider the larger the magnification: the largest whole-pixel size that fits.
            int lo = (int)Math.Round(b * px), hi = (int)Math.Round(m * px);
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (MaxPlateWidth(kinds, b, mid / px, scale, spacing) <= room) lo = mid; else hi = mid - 1;
            }
            m = Math.Max(b, lo / px);
        }
        return (b, m);
    }

    static double Width(SlotKind k, double baseSize, double scale) => Snap(k == SlotKind.Separator ? SeparatorWidth : baseSize, scale);

    /// <summary>Rounds a DIP length to a whole number of device pixels.</summary>
    public static double Snap(double dip, double scale) => scale <= 0 ? dip : Math.Round(dip * scale) / scale;

    /// <summary>
    /// Rounds a DIP position to the nearest device pixel, halves up: a position moving a pixel moves its rounding a
    /// pixel (Math.Round's halves to even went 0 and 2 pixels a step there). Quantized first: the sums the layout is
    /// made of carry floating-point noise, which must not flip a half.
    /// </summary>
    public static double Place(double dip, double scale) => scale <= 0 ? dip : Math.Floor(Math.Round(dip * scale, 6) + 0.5) / scale;

    /// <summary>The slot under <paramref name="x"/> (DIPs), or -1; gaps belong to the nearer neighbour.</summary>
    public static int HitTest(DockLayout layout, double x)
    {
        var s = layout.Slots;
        if (s.Count == 0 || x < layout.PlateLeft || x > layout.PlateLeft + layout.PlateWidth) return -1;
        for (int i = 0; i < s.Count; i++)
        {
            double from = i == 0 ? layout.PlateLeft : (s[i - 1].Right + s[i].Left) / 2;
            double to = i == s.Count - 1 ? layout.PlateLeft + layout.PlateWidth : (s[i].Right + s[i + 1].Left) / 2;
            if (x >= from && x <= to) return i;
        }
        return -1;
    }
}
