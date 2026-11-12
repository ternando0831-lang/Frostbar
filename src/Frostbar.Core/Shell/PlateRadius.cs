namespace Frostbar.Core.Shell;

/// <summary>
/// The radius a plate of glass is cut with (a rounded rectangle of Windows.UI.Composition, its clip): the radius asked,
/// held to half its shorter side, and one within <see cref="Snap"/> of half made half exactly. Composition's rounded
/// rectangle whose straight part was a few hundredths of a pixel long (0.003 to 0.04) showed thin lines across the glass,
/// the same in every such frame, as the seams of triangles (2026-10-18, cc-seams-motion: Focus's circle in its bounce, a
/// radius of 37.48 px on a 75-px plate: the panel rounds a piece's size to whole pixels, its radius is half the size
/// before; 0.013 and 0.047 drew whole, so there is no edge to keep under); exactly half (a circle's or a capsule's ends)
/// and a straight part of 0.07 px or more drew whole.
/// </summary>
public static class PlateRadius
{
    /// <summary>How near half its side a radius is made half (pixels): a straight part is then none or a pixel at least.</summary>
    public const float Snap = 0.5f;

    /// <summary>The radius for <paramref name="wanted"/> on a plate <paramref name="w"/> by <paramref name="h"/> (pixels).</summary>
    public static float Of(float wanted, float w, float h)
    {
        float half = Math.Max(0, Math.Min(w, h) / 2);
        float r = Math.Clamp(wanted, 0, half);
        return half - r < Snap ? half : r;
    }
}
