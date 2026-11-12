using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Frostbar.Core.Dock;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Shell;

namespace Frostbar.App.Dock;

/// <summary>
/// The Dock's window pair (ADR-003, ADR-007): this layered, never-activating WPF window takes the pointer (its surface
/// draws only an almost invisible hit zone), over a click-through backdrop window that shows the plate and, above it,
/// the pictures (<see cref="DockScene"/>). The pair is as long as the Dock gets when magnified plus room for a name
/// label, centred along the monitor's edge it stands on (the bottom, or a side: stage 7), and reaches in as far as
/// magnified icons, bounces and the label need.
/// </summary>
public sealed class DockWindow : Window
{
    readonly WindowBackdrop _backdrop;
    readonly SurfaceFactory _surfaces;
    MonitorInfo _mon;

    /// <summary>The screen's edge it stands on.</summary>
    public DockEdge Edge => Surface.Edge;

    public DockSurface Surface { get; } = new();
    public IntPtr Handle => new WindowInteropHelper(this).Handle;
    public IntPtr BackdropHandle => _backdrop.BackdropHandle;
    /// <summary>The window above the pair (the small icons on minimized windows' pictures), or none yet.</summary>
    public IntPtr AboveHandle => _backdrop.AboveHandle;
    public (float l, float t, float w, float h)? BackdropPlate => _backdrop.PlateBounds;

    public DockWindow(MonitorInfo mon, Material material, DockEdge edge = DockEdge.Bottom)
    {
        _mon = mon;
        Surface.Edge = edge;
        Title = "Frostbar.Dock";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent; // the surface draws its own hit zone (alpha 1) where the Dock takes the pointer
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        UseLayoutRounding = true;
        Content = Surface;
        Height = 130;
        Width = 400;
        Place(400, 130);
        _backdrop = new WindowBackdrop(this, material) { PointerLight = false, Roundness = (float)Common.GlassShape.DockRoundness }; // its own light along the row (SetLight)
        _backdrop.SetPlateBounds((0, 0, 0, 0)); // nothing until the surface has laid out its plate
        // The pictures live in the backdrop window, above the plate (ADR-007).
        _surfaces = SurfaceFactory.Shared(mon);
        // Minimized windows' thumbnails in the backdrop window too, above its plate and icons; their small icons in the
        // window above the pair (stage 7-2, M0 S27).
        Surface.Scene = new DockScene(_surfaces, _backdrop.Overlay, _backdrop.BackdropHandle, () => _backdrop.Above);
        Surface.PlateChanged += r =>
        {
            double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            // Its edges rounded, then its size (the layout puts them on pixels already; rounded each on its own, a plate
            // off them would have its far end step back and forth).
            float l = Px(r.X * s), t = Px(r.Y * s);
            _backdrop.SetPlateBounds((l, t, Px(r.Right * s) - l, Px(r.Bottom * s) - t));
            // Liquid Glass lights up under the pointer, as strong as the magnification there; its rim on the inner side.
            var (along, strength) = Surface.Light;
            double start = Edge == DockEdge.Bottom ? r.X : r.Y;
            var inner = Edge switch { DockEdge.Left => LightSide.Right, DockEdge.Right => LightSide.Left, _ => LightSide.Top };
            _backdrop.SetLight(along is { } at ? (float)((at - start) * s) : null, (float)strength, inner);
        };
        // A press lights the plate under the icon and spills onto the glass near by; a hop and a landing light the plate
        // under the bouncing icon (2026-10-23, the user's "A 沈む＋隣へ光る" and "A どこから開いても跳ねる").
        Surface.Pressed += along =>
        {
            if (Window.GetWindow(Surface) is not { } w || PresentationSource.FromVisual(w) == null) return;
            var px = Surface.PointToScreen(Surface.PlatePoint(along));
            WindowBackdrop.SpillAt(px.X, px.Y, pressedLit: false);
        };
        Surface.Energized += (_, along, landing) =>
        {
            if (PresentationSource.FromVisual(this) == null) return;
            var px = Surface.PointToScreen(Surface.PlatePoint(along));
            _backdrop.SpillHere(px.X, px.Y, landing ? EnergizeLand : EnergizeHop);
        };
        SourceInitialized += (_, _) =>
        {
            Native.AddExStyle(Handle, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(Handle).AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if ((uint)msg == Native.WM_MOUSEACTIVATE) { handled = true; return new IntPtr(Native.MA_NOACTIVATE); }
                return IntPtr.Zero;
            });
        };
    }

    static float Px(double v) => (float)Math.Floor(Math.Round(v, 6) + 0.5); // to a pixel, halves up

    /// <summary>How strongly the plate is lit as a hop begins and as an icon lands (of a press's light, 1).</summary>
    public const float EnergizeHop = 0.45f, EnergizeLand = 0.8f;

    public void SetMaterial(Material m) => _backdrop.SetMaterial(m);

    /// <summary>
    /// The plate suits what is behind it (item 4, the user's "A", 2026-10-19): light over a white window (its dots and
    /// separators dark with it). Told on the UI thread (true: dark now).
    /// </summary>
    public event Action<bool>? ToneChanged
    {
        add { if (value != null) { _toned += value; _backdrop.ToneChanged -= OnTone; _backdrop.ToneChanged += OnTone; _backdrop.Adapts = true; } }
        remove { _toned -= value; }
    }
    Action<bool>? _toned;
    void OnTone(object? key, bool dark) { if (key == null) _toned?.Invoke(dark); }

    /// <summary>The plate's tone now, and what is behind it (diagnostics).</summary>
    public (bool dark, double luma) Tone => (_backdrop.PlateDark, _backdrop.Tones().FirstOrDefault(x => x.key == null).luma);

    /// <summary>The plate's material now (diagnostics: its tint and edge).</summary>
    public Material Material => _backdrop.Material;

    /// <summary>The GPU device the pictures are on (shared with the app grid).</summary>
    public SurfaceFactory Surfaces => _surfaces;

    /// <summary>
    /// Size for the widest magnified layout (<paramref name="lengthDip"/> along the edge, <paramref name="depthDip"/> in from
    /// it), centred along the monitor's edge, standing on <paramref name="edgePx"/>: the outer side of the Dock's reserved
    /// strip (at the bottom: the monitor's bottom once Explorer's taskbar is hidden, above it otherwise). On a side the
    /// row is centred on the work area's height (below the menu bar, above a taskbar that shows).
    /// </summary>
    public void Place(double lengthDip, double depthDip, MonitorInfo? mon = null, int? edgePx = null)
    {
        if (mon != null) _mon = mon;
        if (edgePx != null) _edgePx = edgePx;
        double s = _mon.Scale;
        double depth = Math.Ceiling(depthDip);
        if (Edge == DockEdge.Bottom)
        {
            double monLeft = _mon.Bounds.Left / s, monWidth = _mon.Bounds.Width / s;
            double w = Math.Min(Math.Ceiling(lengthDip), monWidth);
            Width = w;
            Height = depth;
            _resting = new Point(monLeft + Math.Round((monWidth - w) / 2 * s) / s, EdgePx / s - depth);
        }
        else
        {
            double workTop = _mon.Work.Top / s, workHeight = _mon.Work.Height / s;
            double h = Math.Min(Math.Ceiling(lengthDip), workHeight);
            Width = depth;
            Height = h;
            double top = workTop + Math.Round((workHeight - h) / 2 * s) / s;
            // On the right its far side is the screen's edge to the pixel (the plate is placed from that side).
            _resting = new Point(Edge == DockEdge.Left ? EdgePx / s : (EdgePx - Math.Round(depth * s)) / s, top);
        }
        Move();
    }

    int? _edgePx;
    Point _resting;
    double _slide;

    /// <summary>The window at its resting place, moved out by the slide (towards the screen's edge it stands on).</summary>
    void Move()
    {
        double s = _mon.Scale, by = Math.Round(_slide * s) / s;
        Left = _resting.X + (Edge == DockEdge.Left ? -by : Edge == DockEdge.Right ? by : 0);
        Top = _resting.Y + (Edge == DockEdge.Bottom ? by : 0);
    }

    /// <summary>How far past its place towards its edge the Dock is (DIPs): 0 shown, more while it slides out of sight (auto-hide).</summary>
    public double Slide
    {
        get => _slide;
        set
        {
            if (value == _slide) return;
            _slide = value;
            Move();
        }
    }

    /// <summary>
    /// Where the Dock stands, in pixels on the screen: the y of its edge at the bottom (the strip's bottom), the x of the
    /// screen's edge on a side (the strip's outer side: its left on the left, its right on the right).
    /// </summary>
    public int EdgePx => _edgePx ?? Edge switch { DockEdge.Left => _mon.Bounds.Left, DockEdge.Right => _mon.Bounds.Right, _ => _mon.Bounds.Bottom };

    /// <summary>The window's top left corner when shown (DIPs), wherever it has slid to.</summary>
    public Point Resting => _resting;

    /// <summary>
    /// Screen rectangle (DIPs) of a rectangle given inside the window; empty for an empty one (an item being dragged has
    /// no slot: Rect.Empty's negative size threw, in the diagnostics and in the answer to where a window minimizes).
    /// </summary>
    public Rect ToScreen(Rect inside) => inside.IsEmpty ? Rect.Empty : new(Left + inside.X, Top + inside.Y, inside.Width, inside.Height);

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Surface.Close();
        Surface.Scene?.Dispose();
        Surface.Scene = null; // the surface factory is shared and stays
        _backdrop.Dispose();
    }
}
