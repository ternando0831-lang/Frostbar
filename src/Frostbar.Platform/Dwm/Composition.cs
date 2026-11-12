using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace Frostbar.Platform.Dwm;

/// <summary>Windows.UI.Composition on the WPF UI thread (needs a DispatcherQueue on that thread first).</summary>
public static class Composition
{
    [StructLayout(LayoutKind.Sequential)]
    struct DispatcherQueueOptions { public int dwSize; public int threadType; public int apartmentType; }

    [DllImport("CoreMessaging.dll")]
    static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    static IntPtr _controller;
    static Compositor? _compositor;

    public static Compositor Compositor
    {
        get
        {
            if (_compositor != null) return _compositor;
            if (Windows.System.DispatcherQueue.GetForCurrentThread() == null)
            {
                var o = new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2 /*CURRENT*/, apartmentType = 2 /*STA*/ };
                Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(o, out _controller));
            }
            _compositor = new Compositor();
            return _compositor;
        }
    }

    /// <summary>ICompositorDesktopInterop::CreateDesktopWindowTarget through the raw vtable (slot 3).</summary>
    public static unsafe DesktopWindowTarget CreateTarget(IntPtr hwnd, bool topmost)
    {
        IntPtr unk = ((IWinRTObject)Compositor).NativeObject.ThisPtr;
        Guid iid = new("29E691FA-4567-4DCA-B319-D0F207EB6807");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, in iid, out IntPtr interop));
        try
        {
            IntPtr raw;
            var vtbl = *(IntPtr**)interop;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, IntPtr*, int>)vtbl[3];
            Marshal.ThrowExceptionForHR(fn(interop, hwnd, topmost ? 1 : 0, &raw));
            var target = DesktopWindowTarget.FromAbi(raw);
            Marshal.Release(raw);
            return target;
        }
        finally { Marshal.Release(interop); }
    }

    public static Windows.UI.Color Color(byte a, byte r, byte g, byte b) => new() { A = a, R = r, G = g, B = b };
}

public enum MaterialKind { Blur, Solid, Clear }

/// <summary>
/// How a surface is filled: blurred backdrop + tint, a solid colour, or nothing (macOS 26 style clear bar).
/// <paramref name="Glass"/>: how much of Liquid Glass's light it shows (0 none, 1 full; macOS 26). <paramref name="Edge"/>:
/// macOS 27's rim with it: a darkened edge, a brighter highlight just inside it, and a soft band of light inside that
/// (the glass's thickness).
/// </summary>
public sealed record Material(MaterialKind Kind, Windows.UI.Color Tint, Windows.UI.Color Rim, float RadiusPx, float Glass = 0, Frostbar.Core.Shell.LensStyle Lens = Frostbar.Core.Shell.LensStyle.None, PlateShadow? Shadow = null, bool Edge = false, bool Dark = true)
{
    /// <summary>
    /// The same glass in the other tone (light for dark): a plate whose window lets it suit what is behind it
    /// (<see cref="WindowBackdrop.Adapts"/>, item 4, the user's "A", 2026-10-19) turns to it over a backdrop clearly
    /// the other way. Only with the lens, which reads what is behind; null: it keeps its tone.
    /// </summary>
    public Material? Opposite { get; init; }

    /// <summary>This glass in the tone given (itself, or <see cref="Opposite"/>).</summary>
    public Material InTone(bool dark) => dark == Dark || Opposite == null ? this : Opposite;
}

/// <summary>A plate's inner side, where Liquid Glass's light catches its rim (the Dock's: the top at the bottom of the screen).</summary>
public enum LightSide { Top, Left, Right }

/// <summary>
/// A plate of material drawn by composition: host-backdrop blur (stays blurred on inactive windows, unlike DWM acrylic:
/// M0 S4), tint, 1 px rim, clipped to a rounded rectangle of any radius with smooth edges; and with
/// <see cref="Material.Glass"/>, the light of Liquid Glass (see <see cref="ApplyGlass"/>).
/// </summary>
public sealed class Plate
{
    readonly Compositor _c = Composition.Compositor;
    readonly ContainerVisual _root;
    // Everything over the lens (tint, band, lights, sheen, rims): what comes and goes with the glass's presence (SetPresence).
    readonly ContainerVisual _dress;
    readonly SpriteVisual _backdrop, _lens, _tint, _light, _sheen;
    readonly ShapeVisual _rimVisual, _glass, _lightRimVisual;
    readonly CompositionRoundedRectangleGeometry _clipGeometry, _rimGeometry, _specularGeometry;
    readonly CompositionSpriteShape _rim, _specular, _glintTop, _glintBottom, _lightRim;
    readonly CompositionRadialGradientBrush _lightBrush, _lightRimBrush;
    readonly CompositionBrush _host;
    Material _material;
    float _glassOn;

    public Visual Visual => _root;

    public Plate(Material material)
    {
        _root = _c.CreateContainerVisual();
        _clipGeometry = _c.CreateRoundedRectangleGeometry();
        _root.Clip = _c.CreateGeometricClip(_clipGeometry);
        _host = _c.CreateHostBackdropBrush();
        _backdrop = _c.CreateSpriteVisual();
        // Liquid Glass's lens (ADR-016): the backdrop bent at the rim, over the blurred one until it is first drawn.
        _lens = _c.CreateSpriteVisual();
        _lens.IsVisible = false;
        _tint = _c.CreateSpriteVisual();
        _sheen = _c.CreateSpriteVisual();
        _rimVisual = _c.CreateShapeVisual();
        _rimGeometry = _c.CreateRoundedRectangleGeometry();
        _rim = _c.CreateSpriteShape(_rimGeometry);
        _rim.StrokeThickness = 1;
        _rimVisual.Shapes.Add(_rim);
        _glass = _c.CreateShapeVisual();
        _specularGeometry = _c.CreateRoundedRectangleGeometry();
        _specular = _c.CreateSpriteShape(_specularGeometry);
        _glintTop = _c.CreateSpriteShape(_specularGeometry);
        _glintBottom = _c.CreateSpriteShape(_specularGeometry);
        _glass.Shapes.Add(_specular);
        _glass.Shapes.Add(_glintTop);
        _glass.Shapes.Add(_glintBottom);
        // The light under the pointer (the Dock): a glow in the glass, and the rim above it brighter.
        _light = _c.CreateSpriteVisual();
        _lightBrush = _c.CreateRadialGradientBrush();
        _lightBrush.MappingMode = CompositionMappingMode.Absolute;
        _lightBrush.ColorStops.Add(_c.CreateColorGradientStop(0, White(0.12f)));
        _lightBrush.ColorStops.Add(_c.CreateColorGradientStop(1, White(0)));
        _light.Brush = _lightBrush;
        _light.Opacity = 0;
        _lightRimVisual = _c.CreateShapeVisual();
        _lightRim = _c.CreateSpriteShape(_specularGeometry);
        _lightRimBrush = _c.CreateRadialGradientBrush();
        _lightRimBrush.MappingMode = CompositionMappingMode.Absolute;
        _lightRimBrush.ColorStops.Add(_c.CreateColorGradientStop(0, White(0.55f)));
        _lightRimBrush.ColorStops.Add(_c.CreateColorGradientStop(1, White(0)));
        _lightRim.StrokeBrush = _lightRimBrush;
        _lightRim.StrokeThickness = SpecularWidth; // on the rim's own line, not a second one
        _lightRimVisual.Shapes.Add(_lightRim);
        _lightRimVisual.Opacity = 0;
        _dress = _c.CreateContainerVisual();
        _root.Children.InsertAtTop(_backdrop);
        _root.Children.InsertAtTop(_lens);
        _root.Children.InsertAtTop(_dress);
        _dress.Children.InsertAtTop(_tint);
        _dress.Children.InsertAtTop(_light);
        _dress.Children.InsertAtTop(_sheen);
        _dress.Children.InsertAtTop(_rimVisual);
        _dress.Children.InsertAtTop(_glass);
        _dress.Children.InsertAtTop(_lightRimVisual);
        MakePointLight();
        MakeSpill();
        // macOS 27's rim (Material.Edge): the band of light over the tint, the darkened edge under the highlight.
        _band = _c.CreateSpriteVisual();
        _band.IsVisible = false;
        _dress.Children.InsertAbove(_band, _tint);
        _edgeVisual = _c.CreateShapeVisual();
        _edgeGeometry = _c.CreateRoundedRectangleGeometry();
        _edge = _c.CreateSpriteShape(_edgeGeometry);
        _edge.StrokeThickness = 1;
        _edgeVisual.Shapes.Add(_edge);
        _edgeVisual.IsVisible = false;
        _dress.Children.InsertAbove(_edgeVisual, _rimVisual);
        _material = material;
        Apply();
    }

    readonly SpriteVisual _band;
    readonly ShapeVisual _edgeVisual;
    readonly CompositionRoundedRectangleGeometry _edgeGeometry;
    readonly CompositionSpriteShape _edge;
    float? _radius;
    float _specInset = SpecularInset;
    // The band's picture is made once per radius; a new device takes them away (GlassBand.Replaced).
    SurfaceFactory? _bandFactory;
    (int r, int band) _bandKey = (-1, 0);

    /// <summary>The plate's own radius over its material's (a piece of macOS 27's Control Center: each its own shape).</summary>
    public void SetRadius(float px)
    {
        if (_radius == px) return;
        _radius = px;
        Corners();
    }

    /// <summary>Lets go of the device's event the band listens to (the plate's window is gone).</summary>
    public void Dispose()
    {
        if (_bandFactory != null) GlassBand.Replaced -= BandAgain;
        _bandFactory = null;
    }

    void BandAgain() { _bandKey = (-1, 0); Band(); }

    /// <summary>The band of light inside the rim, for the radius drawn now (macOS 27's edge only).</summary>
    void Band()
    {
        bool on = _glassOn > 0 && _material.Edge;
        _band.IsVisible = on;
        // Not before the plate has its size: the Dock's material asks for a pill (a radius far past any plate, cut to
        // half its thickness), and its band's picture made at that radius overflowed and the shell did not start
        // (2026-10-18, 27's edge on the Dock). SetBounds comes back here.
        if (!on || float.IsNaN(_bounds.x)) return;
        var key = ((int)Math.Round(Radius), BandPx);
        if (key == _bandKey) return;
        _bandKey = key;
        if (_bandFactory == null)
        {
            _bandFactory = SurfaceFactory.Shared(Shell.Monitors.Primary());
            GlassBand.Replaced += BandAgain;
        }
        _band.Brush = GlassBand.Brush(_bandFactory, key.Item1, key.Item2);
        _band.Opacity = _lensShown ? 0 : _glassOn; // the lens draws its own band
    }

    // The band's width (pixels): about 5 DIPs at 125%.
    const int BandPx = 6;

    float _lightX = float.NaN, _lightStrength;

    /// <summary>
    /// Liquid Glass lights up where it is touched: a soft glow in the glass around <paramref name="along"/> (pixels in the
    /// plate along its row: x on a plate lying down, y on one standing on a side of the screen) and its rim on the inner
    /// side brighter (<paramref name="inner"/>: the plate's top at the bottom of the screen, its right side on the left,
    /// its left side on the right), as strong as <paramref name="strength"/> (0 to 1: the Dock passes its magnification,
    /// so the light comes and goes with it). Nothing without the glass.
    /// </summary>
    public void SetLight(float? along, float strength, LightSide inner = LightSide.Top)
    {
        if (_glassOn <= 0 || along is not { } px) strength = 0;
        strength = Math.Clamp(strength, 0, 1);
        if (strength < 0.01f) strength = 0;
        if (strength == _lightStrength && (strength == 0 || along == _lightX) && inner == _lightSide) return;
        _lightStrength = strength;
        _lightSide = inner;
        _light.Opacity = strength;
        _lightRimVisual.Opacity = strength * _glassOn;
        if (strength == 0 || along is not { } at) return;
        _lightX = at;
        if (inner == LightSide.Top)
        {
            float h = Math.Max(1, _bounds.h);
            _lightBrush.EllipseCenter = new Vector2(at, h * 0.3f);
            _lightBrush.EllipseRadius = new Vector2(Math.Max(48, h * 1.4f), h * 0.95f);
            _lightRimBrush.EllipseCenter = new Vector2(at, 0);
            _lightRimBrush.EllipseRadius = new Vector2(Math.Max(64, h * 1.9f), h * 0.8f);
            return;
        }
        // Standing on a side: the same light turned, its rim the inner side.
        float w = Math.Max(1, _bounds.w);
        float rim = inner == LightSide.Right ? w : 0;
        _lightBrush.EllipseCenter = new Vector2(inner == LightSide.Right ? w * 0.7f : w * 0.3f, at);
        _lightBrush.EllipseRadius = new Vector2(w * 0.95f, Math.Max(48, w * 1.4f));
        _lightRimBrush.EllipseCenter = new Vector2(rim, at);
        _lightRimBrush.EllipseRadius = new Vector2(w * 0.8f, Math.Max(64, w * 1.9f));
    }

    LightSide _lightSide = LightSide.Top;

    // Liquid Glass's light where the pointer is (the user's "A", 2026-10-18): the Dock's light (SetLight) for the other
    // glass, a soft glow round the pointer and the rim brighter near it.
    SpriteVisual _pointGlow = null!;
    ShapeVisual _pointRimVisual = null!;
    CompositionRadialGradientBrush _pointGlowBrush = null!, _pointRimBrush = null!;
    bool _pointOn;

    void MakePointLight()
    {
        _pointGlow = _c.CreateSpriteVisual();
        _pointGlowBrush = _c.CreateRadialGradientBrush();
        _pointGlowBrush.MappingMode = CompositionMappingMode.Absolute;
        _pointGlowBrush.ColorStops.Add(_c.CreateColorGradientStop(0, White(0.2f)));
        _pointGlowBrush.ColorStops.Add(_c.CreateColorGradientStop(1, White(0)));
        _pointGlow.Brush = _pointGlowBrush;
        _pointGlow.Opacity = 0;
        _pointRimVisual = _c.CreateShapeVisual();
        var rim = _c.CreateSpriteShape(_specularGeometry);
        _pointRimBrush = _c.CreateRadialGradientBrush();
        _pointRimBrush.MappingMode = CompositionMappingMode.Absolute;
        _pointRimBrush.ColorStops.Add(_c.CreateColorGradientStop(0, White(0.8f)));
        _pointRimBrush.ColorStops.Add(_c.CreateColorGradientStop(1, White(0)));
        rim.StrokeBrush = _pointRimBrush;
        rim.StrokeThickness = SpecularWidth; // on the rim's own line, not a second one
        _pointRimVisual.Shapes.Add(rim);
        _pointRimVisual.Opacity = 0;
        // Over the tint (as the Dock's light), under the sheen and the rim's own line.
        _dress.Children.InsertAbove(_pointGlow, _light);
        _dress.Children.InsertAbove(_pointRimVisual, _lightRimVisual);
    }

    // A press's light spilling onto this glass from near by (Core's GlassSpill; the user's "A 沈む＋隣へ光る",
    // 2026-10-23): a glow at the point nearest the press and the rim lit there, coming and going by key frames that
    // DWM runs (nothing to do on the UI thread each frame).
    SpriteVisual _spillGlow = null!;
    ShapeVisual _spillRimVisual = null!;
    CompositionRadialGradientBrush _spillGlowBrush = null!, _spillRimBrush = null!;

    void MakeSpill()
    {
        _spillGlow = _c.CreateSpriteVisual();
        _spillGlowBrush = _c.CreateRadialGradientBrush();
        _spillGlowBrush.MappingMode = CompositionMappingMode.Absolute;
        _spillGlowBrush.ColorStops.Add(_c.CreateColorGradientStop(0, White((float)Frostbar.Core.Shell.GlassSpill.Glow)));
        _spillGlowBrush.ColorStops.Add(_c.CreateColorGradientStop(1, White(0)));
        _spillGlow.Brush = _spillGlowBrush;
        _spillGlow.Opacity = 0;
        _spillRimVisual = _c.CreateShapeVisual();
        var rim = _c.CreateSpriteShape(_specularGeometry);
        _spillRimBrush = _c.CreateRadialGradientBrush();
        _spillRimBrush.MappingMode = CompositionMappingMode.Absolute;
        _spillRimBrush.ColorStops.Add(_c.CreateColorGradientStop(0, White((float)Frostbar.Core.Shell.GlassSpill.Rim)));
        _spillRimBrush.ColorStops.Add(_c.CreateColorGradientStop(1, White(0)));
        rim.StrokeBrush = _spillRimBrush;
        rim.StrokeThickness = SpecularWidth;
        _spillRimVisual.Shapes.Add(rim);
        _spillRimVisual.Opacity = 0;
        _dress.Children.InsertAbove(_spillGlow, _pointGlow);
        _dress.Children.InsertAbove(_spillRimVisual, _pointRimVisual);
    }

    static (float at, float value)[]? _spillKeys;

    /// <summary>
    /// A press's light reaching this glass: a glow round <paramref name="at"/> (the plate's pixels: its point nearest
    /// the press) <paramref name="glowRadius"/> pixels wide and the rim lit there, as strong as
    /// <paramref name="strength"/> (0 to 1), rising and going over <see cref="Frostbar.Core.Shell.MotionTable.Spill"/>.
    /// Another while one shows goes on from its light. Nothing without the glass.
    /// </summary>
    public void Spill(Vector2 at, float strength, float glowRadius)
    {
        if (_glassOn <= 0 || strength <= 0.01f) return;
        _spillKeys ??= Frostbar.Core.Shell.MotionTable.Sample(Frostbar.Core.Shell.GlassSpill.Envelope, 24);
        _spillGlowBrush.EllipseCenter = at;
        _spillGlowBrush.EllipseRadius = new Vector2(glowRadius, glowRadius);
        _spillRimBrush.EllipseCenter = at;
        _spillRimBrush.EllipseRadius = new Vector2(glowRadius * 1.4f, glowRadius * 1.4f);
        var linear = _c.CreateLinearEasingFunction();
        foreach (var (v, most) in new (Visual, float)[] { (_spillGlow, strength), (_spillRimVisual, strength * _glassOn) })
        {
            var a = _c.CreateScalarKeyFrameAnimation();
            // From the light showing now (a second press while the first's shows: no dip), into this one's as it rises.
            a.InsertExpressionKeyFrame(0f, "this.StartingValue", linear);
            const float rise = (float)Frostbar.Core.Shell.MotionTable.SpillRise;
            foreach (var (t, value) in _spillKeys)
            {
                if (t <= 0) continue;
                float v0 = value * most;
                if (t < rise)
                    a.InsertExpressionKeyFrame(t, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Max(this.StartingValue * {1 - t / rise}, {v0})"), linear);
                else a.InsertKeyFrame(t, v0, linear);
            }
            a.Duration = TimeSpan.FromMilliseconds(Frostbar.Core.Shell.MotionTable.Spill);
            v.StartAnimation("Opacity", a);
        }
        Spills++;
        LastSpill = (at, strength, Environment.TickCount64);
    }

    /// <summary>How many spilled lights any plate has shown (diagnostics), and this plate's last: where, how strong, when (ms).</summary>
    public static int Spills { get; private set; }
    public (Vector2 at, float strength, long when)? LastSpill { get; private set; }

    float _presence = 1;

    /// <summary>
    /// How much of the glass is there (0–1; ADR-016's materializing, the user's "A レンズの強さで実体化", 2026-10-23):
    /// what lies over the lens (tint, band, lights, rims) and the blurred backdrop under it by that much. The lens itself
    /// is drawn at that presence by <see cref="LensCapture"/>: at 0 it is what is behind, as if the plate were not there.
    /// </summary>
    public void SetPresence(float k)
    {
        k = Math.Clamp(k, 0, 1);
        if (k == _presence) return;
        _presence = k;
        _dress.Opacity = k;
        _backdrop.Opacity = k;
    }

    /// <summary>The glass's presence now (diagnostics).</summary>
    public float Presence => _presence;

    /// <summary>
    /// The light where the pointer is on the glass: <paramref name="at"/> in the plate's pixels (null: it goes, over a
    /// quarter second), <paramref name="glow"/> the soft light's radii round it and <paramref name="rim"/> how far along
    /// the rim it brightens. It comes in over 0.15 s. Nothing without the glass.
    /// </summary>
    public void SetPointLight(Vector2? at, Vector2 glow, Vector2 rim)
    {
        if (at is not { } p || _glassOn <= 0)
        {
            if (!_pointOn) return;
            _pointOn = false;
            PointLightsOn--;
            Fade(_pointGlow, 0, 250);
            Fade(_pointRimVisual, 0, 250);
            return;
        }
        _pointGlowBrush.EllipseCenter = p;
        _pointGlowBrush.EllipseRadius = glow;
        _pointRimBrush.EllipseCenter = p;
        _pointRimBrush.EllipseRadius = rim;
        if (_pointOn) return;
        _pointOn = true;
        PointLightsOn++;
        Fade(_pointGlow, 1, 150);
        Fade(_pointRimVisual, _glassOn, 150);
    }

    /// <summary>The pointer light is on (diagnostics).</summary>
    public bool PointLightOn => _pointOn;

    /// <summary>How many plates have the pointer light on now (diagnostics).</summary>
    public static int PointLightsOn { get; private set; }

    void Fade(Visual v, float to, double ms)
    {
        var a = _c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1f, to, _c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.25f, 1f)));
        a.Duration = TimeSpan.FromMilliseconds(ms);
        v.StartAnimation("Opacity", a);
    }

    /// <summary>
    /// The lens's drawing (ADR-016) as the plate's backdrop: <paramref name="brush"/> shows a surface drawn by
    /// <see cref="LensCapture"/> at the plate's pixels from its top left (the surface may be larger than the plate);
    /// null goes back to the blurred backdrop. <paramref name="appear"/>: it comes in over 0.15 s (the first drawing).
    /// </summary>
    public void SetLens(CompositionBrush? brush, bool appear = false)
    {
        if (brush == null)
        {
            SetJoined(0);
            _lens.StopAnimation("Opacity");
            _lens.IsVisible = false;
            _lens.Brush = null;
            Overlays(lens: false, appear: false);
            return;
        }
        if (_lens.Brush != brush) _lens.Brush = brush;
        if (_lens.IsVisible) return;
        _lens.IsVisible = true;
        Overlays(lens: true, appear);
        if (!appear) { _lens.Opacity = 1; return; }
        _lens.Opacity = 0;
        var a = _c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1f, 1f, _c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.25f, 1f)));
        a.Duration = TimeSpan.FromMilliseconds(150);
        _lens.StartAnimation("Opacity", a);
    }

    bool _lensShown;

    float _joined;

    /// <summary>
    /// The lens's drawing is the plate joined with its neighbours (Core's LiquidJoin; 2026-11-10): <paramref name="margin"/>
    /// pixels more all round the plate, its silhouette and its tint drawn in it. So the plate's own rounded clip, what
    /// lies over the lens and the blurred backdrop under it are off meanwhile (0: as usual). Set by LensCapture with the
    /// drawing itself: the clip must not go before a joined drawing shows, nor stay after one.
    /// </summary>
    public void SetJoined(float margin)
    {
        if (margin == _joined) return;
        bool was = _joined > 0, on = margin > 0;
        _joined = margin;
        _lens.Offset = new Vector3(-margin, -margin, 0);
        _lens.Size = new Vector2(_root.Size.X + 2 * margin, _root.Size.Y + 2 * margin);
        if (was == on) return;
        _root.Clip = on ? null : _c.CreateGeometricClip(_clipGeometry);
        _dress.IsVisible = !on;
        _backdrop.IsVisible = !on;
        JoinChanges++;
    }

    /// <summary>The margin the plate is joined by now, and how many times any plate went into or out of a join (diagnostics).</summary>
    public float Joined => _joined;
    public static int JoinChanges { get; private set; }

    /// <summary>
    /// The lens draws the band inside the rim and the rim's light itself, from what is behind (the user's "B4" and "まだ縁
    /// が意図的に塗られた感がする", 2026-10-18): while it shows, macOS 27's white band, the darkened edge, the sheen, the
    /// rim's line and its glints go (a white line all round read as painted on); without it they are the glass's edge as
    /// before. They go and come with the lens (over 0.15 s when it appears). The pointer's and the Dock's lights stay.
    /// </summary>
    void Overlays(bool lens, bool appear)
    {
        if (_lensShown == lens) return;
        _lensShown = lens;
        OverlayChanges++;
        // The material's own faint outline stays (the light theme's: light glass over a white window keeps its edge).
        foreach (var (v, back) in new (Visual, float)[] { (_band, _glassOn), (_edgeVisual, 1), (_sheen, 1), (_glass, 1) })
        {
            float to = lens ? 0 : back;
            if (appear) Fade(v, to, 150);
            else { v.StopAnimation("Opacity"); v.Opacity = to; }
        }
    }

    /// <summary>The lens is shown on the plate (diagnostics).</summary>
    public bool LensShown => _lensShown;

    /// <summary>How many times any plate's own rim lines went or came with its lens (diagnostics: a flicker counts two).</summary>
    public static int OverlayChanges { get; private set; }

    /// <summary>
    /// Diagnostics: the plate's layers named in <paramref name="off"/> left out (backdrop, tint, band, sheen, light,
    /// glass: the rim's light, rim, edge, clip: the rounded clip taken off), the others as they were.
    /// </summary>
    public void LeaveOut(IReadOnlyCollection<string> off)
    {
        _left ??= new Dictionary<Visual, bool>();
        foreach (var (name, v) in new (string, Visual)[] { ("backdrop", _backdrop), ("tint", _tint), ("band", _band), ("sheen", _sheen), ("light", _pointGlow), ("glass", _glass), ("rim", _rimVisual), ("edge", _edgeVisual) })
        {
            if (off.Contains(name)) { _left.TryAdd(v, v.IsVisible); v.IsVisible = false; }
            else if (_left.Remove(v, out bool was)) v.IsVisible = was;
        }
        _root.Clip = off.Contains("clip") ? null : _c.CreateGeometricClip(_clipGeometry);
    }

    Dictionary<Visual, bool>? _left;

    public void SetMaterial(Material m) { _material = m; Apply(); }

    (float x, float y, float w, float h) _bounds = (float.NaN, 0, 0, 0);

    public void SetBounds(float x, float y, float w, float h)
    {
        // Called every frame while the Dock magnifies; each property set is a call into the compositor.
        if (_bounds == (x, y, w, h)) return;
        _bounds = (x, y, w, h);
        _root.Offset = new Vector3(x, y, 0);
        var size = new Vector2(Math.Max(0, w), Math.Max(0, h));
        _root.Size = size;
        _backdrop.Size = size;
        _lens.Size = new Vector2(size.X + 2 * _joined, size.Y + 2 * _joined);
        _tint.Size = size;
        _light.Size = size;
        _sheen.Size = size;
        _rimVisual.Size = size;
        _glass.Size = size;
        _lightRimVisual.Size = size;
        _pointGlow.Size = size;
        _pointRimVisual.Size = size;
        _spillGlow.Size = size;
        _spillRimVisual.Size = size;
        _dress.Size = size;
        _band.Size = size;
        _edgeVisual.Size = size;
        _clipGeometry.Size = size;
        _rimGeometry.Offset = new Vector2(0.5f, 0.5f);
        _rimGeometry.Size = new Vector2(Math.Max(0, w - 1), Math.Max(0, h - 1));
        Inset(_edgeGeometry, 0.5f, w, h);
        Inset(_specularGeometry, _specInset, w, h);
        Corners();
    }

    static void Inset(CompositionRoundedRectangleGeometry g, float inset, float w, float h)
    {
        g.Offset = new Vector2(inset, inset);
        g.Size = new Vector2(Math.Max(0, w - 2 * inset), Math.Max(0, h - 2 * inset));
    }

    // The glass's rim: one crisp 1.5 px line just inside the edge.
    const float SpecularWidth = 1.5f, SpecularInset = SpecularWidth / 2;

    float _corners = float.NaN;

    /// <summary>
    /// The radius the plate is drawn with: the material's, held to half its thickness (the Dock asks for more, and its
    /// ends become half circles), and half exactly when it is a hair under (Core's PlateRadius: a straight part of a few
    /// hundredths of a pixel showed thin lines across the glass, 2026-10-18, cc-seams-motion; the clip's seams, it is
    /// thought, as only the clip fills the shape: not tried with the clip taken off while it springs).
    /// </summary>
    public float Radius => float.IsNaN(_bounds.x) ? (_radius ?? _material.RadiusPx) : Frostbar.Core.Shell.PlateRadius.Of(_radius ?? _material.RadiusPx, _bounds.w, _bounds.h);

    void Corners()
    {
        float r = Radius;
        if (r == _corners) return;
        _corners = r;
        _clipGeometry.CornerRadius = new Vector2(r, r);
        _rimGeometry.CornerRadius = new Vector2(Math.Max(0, r - 0.5f), Math.Max(0, r - 0.5f));
        _edgeGeometry.CornerRadius = new Vector2(Math.Max(0, r - 0.5f), Math.Max(0, r - 0.5f));
        _specularGeometry.CornerRadius = new Vector2(Math.Max(0, r - _specInset), Math.Max(0, r - _specInset));
        Band();
    }

    void Apply()
    {
        var m = _material;
        Corners();
        float glass = m.Kind == MaterialKind.Blur ? Math.Clamp(m.Glass, 0, 1) : 0;
        _backdrop.Brush = m.Kind != MaterialKind.Blur ? null : glass > 0 ? Vivid(glass) : _host;
        _tint.Brush = m.Kind == MaterialKind.Clear ? null : _c.CreateColorBrush(m.Tint);
        _rim.StrokeBrush = m.Rim.A == 0 ? null : _c.CreateColorBrush(m.Rim);
        ApplyGlass(glass);
    }

    // No lens: Liquid Glass bends what is behind it at its edges, but composition refuses to transform a backdrop
    // brush ("Backdrop brushes cannot be transformed", 2026-10-12), and the backdrop Windows gives is already blurred.

    static CompositionEffectFactory? _vividFactory;
    static bool _effectsFailed;
    CompositionEffectBrush? _vivid;

    /// <summary>
    /// Liquid Glass gathers the colours behind it: the backdrop more saturated (a colour-matrix effect). Without effects
    /// (the factory refused), the plain backdrop.
    /// </summary>
    CompositionBrush Vivid(float glass)
    {
        if (_effectsFailed) return _host;
        try
        {
            _vividFactory ??= D2DEffect.Saturate("vivid", 1.45f, "backdrop").Factory(_c);
            if (_vivid == null)
            {
                _vivid = _vividFactory.CreateBrush();
                _vivid.SetSourceParameter("backdrop", _host);
            }
            return _vivid;
        }
        catch (Exception ex)
        {
            _effectsFailed = true;
            Infrastructure.Log.Warn("glass: composition effects are not available; the glass goes without them", ex);
            return _host;
        }
    }

    /// <summary>
    /// Liquid Glass (macOS 26): the light the glass catches. Its rim shines where it faces the light (the top left) and
    /// again, more softly, where the light leaves it (the bottom right); a softer rim one step inside gives the edge
    /// its thickness; a sheen lies across the top and a faint shade along the bottom. Shapes and gradients only, which
    /// the compositor draws without effects.
    /// </summary>
    void ApplyGlass(float g)
    {
        bool on = g > 0;
        _glassOn = g;
        _glass.IsVisible = on;
        _sheen.IsVisible = on;
        _light.IsVisible = on;
        _lightRimVisual.IsVisible = on;
        bool edge = on && _material.Edge;
        _edgeVisual.IsVisible = edge;
        // macOS 27: the highlight sits just inside the darkened edge.
        float inset = edge ? 1 + SpecularInset : SpecularInset;
        if (inset != _specInset)
        {
            _specInset = inset;
            _corners = float.NaN;
            if (!float.IsNaN(_bounds.x)) Inset(_specularGeometry, _specInset, _bounds.w, _bounds.h);
            Corners();
        }
        Band();
        if (!on) SetPointLight(null, default, default);
        if (!on) return;
        if (edge)
        {
            // macOS 27 ("a darkened edge along with brighter specular highlights", WWDC26): the edge a dark line, the
            // highlight brighter at the top left and again at the bottom right, the glints stronger. The rim lit all
            // round (never under 0.45: Apple's Control Center, enlarged, has its whole rim lit; the user's "B4",
            // 2026-10-18; it dimmed to 0.14 between before).
            _edge.StrokeBrush = _c.CreateColorBrush(Composition.Color((byte)(0.30f * g * 255), 0, 0, 0));
            _specular.StrokeThickness = SpecularWidth;
            _specular.StrokeBrush = Gradient(new Vector2(0, 0), new Vector2(1, 1), (0f, White(0.95f * g)), (0.22f, White(0.62f * g)), (0.5f, White(0.45f * g)), (0.78f, White(0.55f * g)), (1f, White(0.85f * g)));
            _glintTop.StrokeThickness = SpecularWidth;
            _glintTop.StrokeBrush = Radial(new Vector2(0.08f, 0.04f), new Vector2(0.22f, 0.8f), White(0.9f * g));
            _glintBottom.StrokeThickness = SpecularWidth;
            _glintBottom.StrokeBrush = Radial(new Vector2(0.92f, 0.96f), new Vector2(0.2f, 0.7f), White(0.6f * g));
            _sheen.Brush = Gradient(new Vector2(0, 0), new Vector2(0, 1), (0f, White(0.10f * g)), (0.5f, White(0.01f * g)), (1f, Composition.Color((byte)(0.05f * g * 255), 0, 0, 0)));
            return;
        }
        // One line at the rim, lit from above: its top edge bright, fading down the sides, a little light again at the
        // bottom (bounced). One line only: more, stacked inside it, read as layers (the user, 2026-10-12).
        _specular.StrokeThickness = SpecularWidth;
        _specular.StrokeBrush = Gradient(new Vector2(0, 0), new Vector2(0.25f, 1), (0f, White(0.60f * g)), (0.18f, White(0.26f * g)), (0.55f, White(0.08f * g)), (1f, White(0.22f * g)));
        // Glints on that same line where it turns towards the light (top left) and, dimmer, where the light leaves (bottom right).
        _glintTop.StrokeThickness = SpecularWidth;
        _glintTop.StrokeBrush = Radial(new Vector2(0.08f, 0.04f), new Vector2(0.22f, 0.8f), White(0.65f * g));
        _glintBottom.StrokeThickness = SpecularWidth;
        _glintBottom.StrokeBrush = Radial(new Vector2(0.92f, 0.96f), new Vector2(0.2f, 0.7f), White(0.30f * g));
        // A sheen across the top, a faint shade along the bottom: one smooth gradient (no steps to see).
        _sheen.Brush = Gradient(new Vector2(0, 0), new Vector2(0, 1), (0f, White(0.10f * g)), (0.5f, White(0.01f * g)), (1f, Composition.Color((byte)(0.05f * g * 255), 0, 0, 0)));
    }

    CompositionRadialGradientBrush Radial(Vector2 center, Vector2 radius, Windows.UI.Color color)
    {
        var b = _c.CreateRadialGradientBrush();
        b.EllipseCenter = center;
        b.EllipseRadius = radius;
        b.ColorStops.Add(_c.CreateColorGradientStop(0, color));
        b.ColorStops.Add(_c.CreateColorGradientStop(1, Composition.Color(0, 255, 255, 255)));
        return b;
    }

    static Windows.UI.Color White(float alpha) => Composition.Color((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), 255, 255, 255);

    CompositionLinearGradientBrush Gradient(Vector2 start, Vector2 end, params (float offset, Windows.UI.Color color)[] stops)
    {
        var b = _c.CreateLinearGradientBrush();
        b.StartPoint = start;
        b.EndPoint = end;
        foreach (var (offset, color) in stops) b.ColorStops.Add(_c.CreateColorGradientStop(offset, color));
        return b;
    }
}
