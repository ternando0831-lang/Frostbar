using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Frostbar.Platform.Interop;
using Windows.UI.Composition;

namespace Frostbar.Platform.Dwm;

/// <summary>A plate's shadow, in DIPs: blurred this much, this far down, black this dark.</summary>
public sealed record PlateShadow(float Blur, float Drop, float Alpha);

/// <summary>
/// The shadow under a glass plate (Liquid Glass, the user's choice "C" of 2026-10-18): a click-through window right under
/// the plate's own, larger by the shadow's reach, so that the panels' windows keep their size and place (menus are put
/// where their plate goes). It shows a nine-grid of one picture: a blurred rounded rectangle, moved down, with the
/// plate's own area cut out (the glass shows what is behind it, and a dark shape under it would darken the glass).
/// The picture is made once per radius and look (WPF draws it, the shared device holds it) and stretched to any size.
/// </summary>
internal sealed class ShadowLayer : IDisposable
{
    readonly CompositionWindow _window;
    readonly SpriteVisual _sprite;
    readonly SurfaceFactory _f;
    (int r, int blur, int drop, int alpha) _key = (-1, 0, 0, 0);
    int _margin;
    bool _shown;

    static readonly Dictionary<(SurfaceFactory, (int, int, int, int)), CompositionSurfaceBrush> Pictures = new();
    static readonly HashSet<SurfaceFactory> Watched = new();

    public IntPtr Handle => _window.Handle;
    /// <summary>Every shadow this layer draws (the plate's, or its pieces'): what fades with the plate.</summary>
    public Windows.UI.Composition.Visual Visual => _root;

    readonly Windows.UI.Composition.ContainerVisual _root;

    public ShadowLayer()
    {
        _f = SurfaceFactory.Shared(Shell.Monitors.Primary());
        if (Watched.Add(_f)) _f.DeviceReplaced += () => Forget(_f);
        _window = new CompositionWindow(new RECT(0, 0, 1, 1), clickThrough: true);
        _sprite = Composition.Compositor.CreateSpriteVisual();
        _root = Composition.Compositor.CreateContainerVisual();
        _sprite.Opacity = Shown(_plateDepth);
        _root.Children.InsertAtTop(_sprite);
        _window.SetRoot(_root);
        _f.DeviceReplaced += Again;
    }

    // ---- how deep each shadow is for what lies behind its glass (Core's ShadowDepth, 2026-11-10) -------------------------

    /// <summary>The deepest a shadow goes: its picture is made that dark, and shown at its depth's part of it.</summary>
    const float Deepest = (float)Frostbar.Core.Shell.ShadowDepth.Deepest;

    float _plateDepth = 1;
    float[] _depths = [];

    static float Shown(float depth) => Math.Clamp(depth, 0, Deepest) / Deepest;
    float DepthAt(int i) => i < _depths.Length ? _depths[i] : 1;

    /// <summary>The plate's shadow goes to this depth (1: as it was), over a moment.</summary>
    public void SetPlateDepth(float depth)
    {
        if (depth == _plateDepth) return;
        _plateDepth = depth;
        Glide(_sprite, Shown(depth));
    }

    /// <summary>The pieces' shadows go to these depths (as the pieces were last listed), over a moment.</summary>
    public void SetPieceDepths(IReadOnlyList<float> depths)
    {
        _depths = [.. depths];
        if (_last is not { } last) return;
        for (int i = 0; i < _pieces.Count && i < last.pieces.Count; i++)
            if (_pieces[i].sprite.IsVisible) Glide(_pieces[i].sprite, last.pieces[i].Opacity * Shown(DepthAt(i)));
    }

    static void Glide(SpriteVisual s, float to)
    {
        var a = Composition.Compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1, to);
        a.Duration = TimeSpan.FromMilliseconds(Frostbar.Core.Shell.MotionTable.ShadowTurn);
        s.StartAnimation("Opacity", a);
    }

    // The pieces' shadows (macOS 27's Control Center, WindowBackdrop.SetPieces): one sprite each, its picture's key.
    readonly List<(SpriteVisual sprite, (int r, int blur, int drop, int alpha) key)> _pieces = new();
    (IReadOnlyList<GlassPiece> pieces, PlateShadow look, double scale)? _last;

    /// <summary>
    /// Shadows under pieces of glass instead of the plate (null: the plate's again), at their rectangles in pixels of
    /// the plate's window, each clipped as its piece is (a card scrolled half out of its list). A piece's shadow is
    /// <paramref name="look"/>'s, lighter than a plate's; its picture fits in the window's margin (the plate's shadow
    /// reaches farther).
    /// </summary>
    public void SetPieces(IReadOnlyList<GlassPiece>? pieces, PlateShadow look, double scale, IReadOnlyList<float>? depths = null)
    {
        _last = pieces == null ? null : (pieces, look, scale);
        if (depths != null) _depths = [.. depths];
        _sprite.IsVisible = pieces == null;
        int n = pieces?.Count ?? 0;
        var c = Composition.Compositor;
        while (_pieces.Count < n)
        {
            var s = c.CreateSpriteVisual();
            _root.Children.InsertAtTop(s);
            _pieces.Add((s, (-1, 0, 0, 0)));
        }
        for (int i = 0; i < _pieces.Count; i++)
        {
            var (s, had) = _pieces[i];
            if (i >= n || !pieces![i].Shadow) { s.IsVisible = false; continue; }
            var p = pieces[i];
            var key = ((int)Math.Round(p.Radius), (int)Math.Round(look.Blur * scale), (int)Math.Round(look.Drop * scale), (int)Math.Round(Math.Min(1, look.Alpha * Deepest) * 255));
            if (key != had)
            {
                s.Brush = Grid(key);
                _pieces[i] = (s, key);
            }
            int m = key.Item2 + 2 + key.Item3, shift = _margin - m;
            s.Offset = new Vector3(p.L + shift, p.T + shift, 0);
            s.Size = new Vector2(p.W + 2 * m, p.H + 2 * m);
            if (p.Clip is { } clip)
            {
                float left = p.L - m, top = p.T - m, right = p.L + p.W + m, bottom = p.T + p.H + m;
                var inset = s.Clip as InsetClip ?? c.CreateInsetClip();
                inset.LeftInset = Math.Max(0, clip.L - left);
                inset.TopInset = Math.Max(0, clip.T - top);
                inset.RightInset = Math.Max(0, right - clip.R);
                inset.BottomInset = Math.Max(0, bottom - clip.B);
                s.Clip = inset;
            }
            else s.Clip = null;
            s.StopAnimation("Opacity"); // a depth on its way: the piece is placed where it goes
            s.Opacity = p.Opacity * Shown(DepthAt(i));
            s.IsVisible = true;
        }
    }

    /// <summary>The device was lost: every picture goes (the next placing makes them again on the new one).</summary>
    static void Forget(SurfaceFactory f)
    {
        foreach (var k in Pictures.Keys.Where(k => k.Item1 == f).ToList()) Pictures.Remove(k);
    }

    /// <summary>The device was replaced (the pictures were forgotten first): this shadow's picture made again on the new one.</summary>
    void Again()
    {
        if (_key.r >= 0) _sprite.Brush = Grid(_key);
        for (int i = 0; i < _pieces.Count; i++) _pieces[i] = (_pieces[i].sprite, (-1, 0, 0, 0));
        if (_last is { } last) SetPieces(last.pieces, last.look, last.scale);
    }

    /// <summary>
    /// Places the shadow for a plate (<paramref name="plate"/>: pixels in the plate's window <paramref name="window"/>),
    /// its window right under <paramref name="above"/> (the plate's window).
    /// </summary>
    public void Place(RECT window, (float l, float t, float w, float h) plate, float radius, PlateShadow look, double scale, IntPtr above)
    {
        Look(radius, look, scale, plate);
        int m = _margin;
        _window.MoveBelow(new RECT(window.Left - m, window.Top - m, window.Right + m, window.Bottom + m), above);
        Move(plate);
        if (_last is { } last) SetPieces(last.pieces, last.look, last.scale); // they sit in the window's margin
    }

    /// <summary>The plate moved inside its window (the Dock magnifying): only the picture moves (made again if its radius changed).</summary>
    public void SetPlate((float l, float t, float w, float h) plate, float radius, PlateShadow look, double scale)
    {
        int margin = _margin;
        Look(radius, look, scale, plate);
        if (_margin != margin) return; // the window has to grow: the next placing does it
        Move(plate);
    }

    void Look(float radius, PlateShadow look, double scale, (float l, float t, float w, float h) plate)
    {
        var key = ((int)Math.Round(radius), (int)Math.Round(look.Blur * scale), (int)Math.Round(look.Drop * scale), (int)Math.Round(Math.Min(1, look.Alpha * Deepest) * 255));
        _margin = key.Item2 + 2 + key.Item3;
        // No picture for an empty plate (the Dock before its first layout): one made for radius 0 cost 14–23 ms of the
        // UI thread at start for nothing (2026-10-18).
        if (key == _key || !(plate.w >= 1 && plate.h >= 1)) return;
        _key = key;
        _sprite.Brush = Grid(key);
    }

    void Move((float l, float t, float w, float h) plate)
    {
        int m = _margin;
        _sprite.Offset = new Vector3(plate.l, plate.t, 0);
        _sprite.Size = plate.w >= 1 && plate.h >= 1 ? new Vector2(plate.w + 2 * m, plate.h + 2 * m) : Vector2.Zero;
    }

    public void Show() { if (!_shown) { _shown = true; _window.Show(); } }
    public void Hide() { if (_shown) { _shown = false; _window.Hide(); } }

    CompositionNineGridBrush Grid((int r, int blur, int drop, int alpha) key)
    {
        if (!Pictures.TryGetValue((_f, key), out var picture))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            picture = _f.Brush(Picture(key.r, key.blur, key.drop, key.alpha / 255.0));
            Pictures[(_f, key)] = picture;
            Frostbar.Platform.Infrastructure.Log.Debug($"shadow: picture for radius {key.r}, blur {key.blur} px made in {sw.ElapsedMilliseconds} ms on the UI thread");
        }
        int a = key.blur + 2 + key.drop, r = key.r;
        var grid = Composition.Compositor.CreateNineGridBrush();
        grid.Source = picture;
        // The corners (and the shadow's corners, lower by the drop) stay as drawn; two pixels in the middle stretch.
        grid.SetInsets(a + r, a + key.drop + r, a + r, a + r);
        grid.IsCenterHollow = true;
        return grid;
    }

    /// <summary>
    /// The picture: the plate's place (a rounded rectangle at (A, A), 2r + 2 wide and 2r + drop + 2 high, so that the
    /// middle two pixels are straight in both it and the shadow below it) cut out of its blurred shadow.
    /// </summary>
    static BitmapSource Picture(int r, int blur, int drop, double alpha)
    {
        int a = blur + 2 + drop;
        int w = 2 * r + 2, h = 2 * r + drop + 2;
        int width = 2 * a + w, height = 2 * a + h;
        var shadow = new DrawingVisual { Effect = blur > 0 ? new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality } : null };
        using (var dc = shadow.RenderOpen())
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), 0, 0, 0)), null, new Rect(a, a + drop, w, h), r, r);
        var root = new System.Windows.Media.ContainerVisual
        {
            Clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, width, height)), new RectangleGeometry(new Rect(a, a, w, h), r, r)),
        };
        root.Children.Add(shadow);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    public void Dispose()
    {
        _f.DeviceReplaced -= Again;
        _window.Dispose();
    }
}
