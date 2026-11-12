using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Windows.UI.Composition;

namespace Frostbar.Platform.Dwm;

/// <summary>
/// macOS 27's glass shows its thickness: light gathered in a soft band just inside the rim (the user's "A" for the
/// Control Center, 2026-10-18; `Lab mock cc`). Composition cannot blur a shape, and lines stacked to make it soft read
/// as layers (2026-10-12), so the band is one picture: a rounded rectangle's rim drawn by WPF, blurred and kept inside
/// the shape, stretched by a nine-grid to any size. One picture per radius and band (WPF draws it once, the shared
/// device holds it); made again on a new device.
/// </summary>
internal static class GlassBand
{
    static readonly Dictionary<(SurfaceFactory, int r, int band), CompositionSurfaceBrush> Pictures = new();
    static readonly HashSet<SurfaceFactory> Watched = new();

    /// <summary>The device was replaced: the pictures are gone (a plate asks again on its next look).</summary>
    public static event Action? Replaced;

    /// <summary>The band for a rounded rectangle of radius <paramref name="r"/> and band <paramref name="band"/> (pixels), as a nine-grid brush.</summary>
    public static CompositionNineGridBrush Brush(SurfaceFactory f, int r, int band)
    {
        // A plate's radius is at most half its side; one past any screen's means a radius not cut to a size yet.
        r = Math.Clamp(r, 0, MaxRadius);
        if (Watched.Add(f)) f.DeviceReplaced += () =>
        {
            foreach (var k in Pictures.Keys.Where(k => k.Item1 == f).ToList()) Pictures.Remove(k);
            Replaced?.Invoke();
        };
        if (!Pictures.TryGetValue((f, r, band), out var picture))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            picture = f.Brush(Picture(r, band));
            Pictures[(f, r, band)] = picture;
            Infrastructure.Log.Debug($"glass: band for radius {r}, {band} px made in {sw.ElapsedMilliseconds} ms on the UI thread");
        }
        int k = Inset(r, band);
        var grid = Composition.Compositor.CreateNineGridBrush();
        grid.Source = picture;
        grid.SetInsets(k);
        grid.IsCenterHollow = true;
        return grid;
    }

    const int MaxRadius = 512;

    /// <summary>How far in from each side the picture's corners reach: the radius, or the band's blurred width if wider.</summary>
    static int Inset(int r, int band) => Math.Max(r, 3 * band) + 1;

    /// <summary>A square of 2k + 2: its rim stroked 2 × band wide (half of it inside), blurred by the band, kept inside the shape.</summary>
    static BitmapSource Picture(int r, int band)
    {
        int k = Inset(r, band), size = 2 * k + 2;
        var shape = new RectangleGeometry(new Rect(0, 0, size, size), r, r);
        var ring = new DrawingVisual { Effect = new BlurEffect { Radius = band, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality } };
        using (var dc = ring.RenderOpen())
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x48, 255, 255, 255)), 2 * band), new Rect(0, 0, size, size), r, r);
        var root = new System.Windows.Media.ContainerVisual { Clip = shape };
        root.Children.Add(ring);
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }
}
