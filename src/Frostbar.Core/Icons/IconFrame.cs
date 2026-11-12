namespace Frostbar.Core.Icons;

/// <summary>
/// The shell's large image of an app that only has small icons: its small icon in the middle of a faint frame (a grey
/// ring of low alpha around an empty square) at every size above 48 px — 2026-10-12: RivaTuner's Desktop Overlay Host,
/// a Steam game's shortcut, even Magnifier. The app's own 48 px icon, scaled up, looks more like the app.
/// </summary>
public static class IconFrame
{
    /// <summary>
    /// True when the outermost ring of <paramref name="bgra"/> (premultiplied BGRA, top row first) is faint — partly
    /// transparent all round — and a band an eighth of the way in is empty.
    /// </summary>
    public static bool IsFramed(byte[] bgra, int w, int h)
    {
        if (w < 64 || h < 64 || bgra.Length < w * h * 4) return false;
        int faint = 0, ring = 0;
        void Edge(int x, int y)
        {
            ring++;
            byte a = bgra[(y * w + x) * 4 + 3];
            if (a > 0 && a < 0x80) faint++;
        }
        // Without the corners: the frame's are rounded.
        for (int x = 4; x < w - 4; x++) { Edge(x, 0); Edge(x, h - 1); }
        for (int y = 4; y < h - 4; y++) { Edge(0, y); Edge(w - 1, y); }
        if (faint < ring * 0.9) return false;
        int inset = Math.Min(w, h) / 8, empty = 0, band = 0;
        void Inside(int x, int y)
        {
            band++;
            if (bgra[(y * w + x) * 4 + 3] == 0) empty++;
        }
        for (int x = inset; x < w - inset; x++) { Inside(x, inset); Inside(x, h - 1 - inset); }
        for (int y = inset; y < h - inset; y++) { Inside(inset, y); Inside(w - 1 - inset, y); }
        return empty >= band * 0.9;
    }
}
