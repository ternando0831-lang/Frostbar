namespace Frostbar.Core.MenuBar;

/// <summary>How a wallpaper's picture is laid on the screen (Windows' choices).</summary>
public enum PictureFit { Fill, Fit, Stretch, Center, Tile, Span }

/// <summary>
/// The menu bar's words dark or light by the wallpaper under it (2026-10-19, the user's "C2"): macOS's menu bar takes its
/// tone from the desktop picture, black words over a bright one whatever the appearance; ours kept the appearance's (white
/// words, the dark tint) over a white picture. Read from the picture itself (the strip under the bar, as it is laid),
/// not from the screen: a lens across the screen's whole width would read every window drawn under the bar.
/// </summary>
public static class BarTone
{
    /// <summary>Brighter than this, dark words; darker than <see cref="Back"/>, light ones; between, as they were (GlassTone's).</summary>
    public const double Bright = 0.62, Back = 0.5;

    /// <summary>
    /// The part of a <paramref name="picW"/>×<paramref name="picH"/> picture under a bar <paramref name="barH"/> high
    /// at the top of a <paramref name="screenW"/>×<paramref name="screenH"/> screen (all in pixels), as
    /// <paramref name="fit"/> lays it; null when the bar stands over the plain colour around the picture.
    /// </summary>
    public static (double X, double Y, double W, double H)? Band(double picW, double picH, double screenW, double screenH, double barH, PictureFit fit)
    {
        if (picW <= 0 || picH <= 0 || screenW <= 0 || screenH <= 0 || barH <= 0) return null;
        switch (fit)
        {
            case PictureFit.Stretch:
                return (0, 0, picW, Math.Min(picH, barH * picH / screenH));
            case PictureFit.Tile:
                return (0, 0, Math.Min(picW, screenW), Math.Min(picH, barH));
            case PictureFit.Fit or PictureFit.Center:
            {
                double scale = fit == PictureFit.Center ? 1 : Math.Min(screenW / picW, screenH / picH);
                double top = (screenH - picH * scale) / 2, left = (screenW - picW * scale) / 2;
                if (top >= barH) return null; // the colour around it
                double y0 = Math.Max(0, -top) / scale, y1 = Math.Min(picH, (barH - top) / scale);
                double x0 = Math.Max(0, -left) / scale, x1 = Math.Min(picW, (screenW - left) / scale);
                return (x0, y0, x1 - x0, y1 - y0);
            }
            default: // Fill, Span: covered, the middle of the picture
            {
                double scale = Math.Max(screenW / picW, screenH / picH);
                double visW = screenW / scale, visH = screenH / scale;
                return ((picW - visW) / 2, (picH - visH) / 2, visW, Math.Min(visH, barH / scale));
            }
        }
    }

    /// <summary>Dark words over a picture this bright under the bar (0–1), given whether they were dark.</summary>
    public static bool DarkWords(double luma, bool wereDark) => luma > Bright || (luma >= Back && wereDark);

    /// <summary>The brightness of an sRGB colour (0–1), as GlassTone reads a lens's picture.</summary>
    public static double Luma(byte r, byte g, byte b) => (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
}
