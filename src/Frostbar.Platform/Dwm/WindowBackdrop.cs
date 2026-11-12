using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Frostbar.Platform.Interop;
using Windows.UI.Composition.Desktop;

namespace Frostbar.Platform.Dwm;

/// <summary>
/// A piece of glass in a window (macOS 27's Control Center: each control its own glass), in pixels of the window:
/// its rectangle and radius; <paramref name="Glass"/> false for a piece the window draws itself (an active button, a
/// white circle) that keeps only its shadow; <paramref name="Shadow"/> false for one too small for it; <paramref name="Clip"/>
/// the part of the window it may show in (a list's view), or null for all of it.
/// </summary>
public sealed record GlassPiece(float L, float T, float W, float H, float Radius, bool Glass = true, bool Shadow = true, (float L, float T, float R, float B)? Clip = null)
{
    /// <summary>How opaque the piece shows (its part fading: a card going).</summary>
    public float Opacity { get; init; } = 1;

    /// <summary>What the piece is the glass of (its part's element): it keeps its plate and lens while the list changes round it.</summary>
    public object? Key { get; init; }
}

/// <summary>
/// The material behind a WPF window (ADR-003, revised 2026-10-12). A composition target always draws *above* its
/// window's own (WPF) content — isTopmost only orders it against child windows — so the plate cannot share the WPF
/// window. Instead it lives in a click-through <see cref="CompositionWindow"/> right behind it; the WPF window is
/// layered (per-pixel alpha) and owned by the backdrop so it always stays on top of it. The WPF window keeps an
/// almost invisible fill (alpha 1) where it should take clicks.
/// </summary>
public sealed class WindowBackdrop : IDisposable
{
    readonly Window _content;
    readonly CompositionWindow _backdrop;
    readonly Plate _plate;
    Thickness _inset;
    bool _syncing, _checkQueued;
    Material _material;
    // Liquid Glass's lens (ADR-016): the plate's place on the screen for it, and the pair left out of screen capture.
    LensTarget? _lens;
    bool _excluded;
    RECT? _rect;
    (float l, float t, float w, float h) _platePx;

    /// <param name="content">A layered window (AllowsTransparency = true), not yet shown.</param>
    public WindowBackdrop(Window content, Material material)
    {
        if (!content.AllowsTransparency) throw new ArgumentException("the content window must be layered (AllowsTransparency)");
        _content = content;
        _backdrop = new CompositionWindow(LayoutRect() ?? new RECT(0, 0, 1, 1), clickThrough: true);
        _plate = new Plate(material);
        _material = material;
        _plateShown = material;
        var c = Composition.Compositor;
        var root = _root = c.CreateContainerVisual();
        root.Children.InsertAtTop(_plate.Visual);
        Overlay = c.CreateContainerVisual();
        root.Children.InsertAtTop(Overlay);
        _backdrop.SetRoot(root);
        new WindowInteropHelper(content).Owner = _backdrop.Handle; // owned windows stay above their owner
        // A size-to-content window raises SizeChanged before its HWND is resized, and its HWND starts at a default
        // size (2026-10-12: menu plates came out 2x2 px, then 1130x598). So the plate follows WPF's layout at once,
        // and the real window rectangle once rendering has settled.
        content.SizeChanged += (_, _) => Sync(LayoutRect());
        content.LocationChanged += (_, _) => Sync(LayoutRect());
        content.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(content).Handle;
            HwndSource.FromHwnd(h).AddHook(Hook);
            if (_excluded) Native.SetWindowDisplayAffinity(h, Native.WDA_EXCLUDEFROMCAPTURE);
        };
        content.DpiChanged += (_, _) => QueueCheck();
        content.AddHandler(UIElement.MouseMoveEvent, new System.Windows.Input.MouseEventHandler((_, e) => PointAt(e.GetPosition(content))), handledEventsToo: true);
        content.MouseLeave += (_, _) => LightOff();
        content.IsVisibleChanged += (_, _) =>
        {
            if (!content.IsVisible) { _droplets = 0; LightOff(); _shadow?.Hide(); _backdrop.Hide(); _above?.Hide(); RefreshLens(); return; }
            Sync(LayoutRect());
            _shadow?.Show();
            _backdrop.Show();
            _above?.Show();
            QueueCheck();
        };
        SetLens(material);
        SetShadow(material);
        Live.Add(new WeakReference<WindowBackdrop>(this));
    }

    // ---- a press's light spilling onto the glass round it (Core's GlassSpill, the user's "A 沈む＋隣へ光る", 2026-10-23) ----

    static readonly List<WeakReference<WindowBackdrop>> Live = new();

    /// <summary>
    /// Diagnostics "lens-presence": every shown glass's shadow at the presence held (null: whole again), as a coming by
    /// its lens has it. The lenses alone were held, and the photo at nothing showed the shadow round the plate
    /// (2026-10-23).
    /// </summary>
    public static void HoldShadows(float? presence)
    {
        Live.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in Live.ToList())
            if (w.TryGetTarget(out var b) && b._shadow != null && b._content.IsVisible)
            {
                b._shadow.Visual.StopAnimation("Opacity");
                b._shadow.Visual.Opacity = presence ?? 1;
            }
    }

    /// <summary>
    /// The glass of this window takes the light of a press near by (the Dock, menus, panels, the pieces of the Control
    /// Center). Off for glass that is not a thing on the screen but its ground (the app grid's and the overview's
    /// backdrops) and for the menu bar.
    /// </summary>
    public bool TakesSpill { get; set; } = true;

    /// <summary>
    /// A press at (<paramref name="xPx"/>, <paramref name="yPx"/>) on the screen (pixels): every piece of glass shown
    /// within <see cref="Frostbar.Core.Shell.GlassSpill.Reach"/> DIPs of it, in every window, lit at its point nearest the
    /// press. <paramref name="pressedLit"/>: the part pressed has its own light (<c>PressLight</c>), and the glass the
    /// press is on is left to it; else that glass lights where it was pressed (the Dock's plate under an icon).
    /// Returns how many pieces it reached.
    /// </summary>
    public static int SpillAt(double xPx, double yPx, bool pressedLit)
    {
        int lit = 0;
        Live.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in Live.ToList())
            if (w.TryGetTarget(out var b)) lit += b.SpillFrom(xPx, yPx, pressedLit);
        return lit;
    }

    /// <summary>
    /// The plate lit at (<paramref name="xPx"/>, <paramref name="yPx"/>) on the screen by <paramref name="strength"/>
    /// of a press's light, as the light spilled is (the Dock's plate under an icon hopping and landing, 2026-10-23).
    /// </summary>
    public void SpillHere(double xPx, double yPx, float strength)
    {
        if (!Glassy || _rect is not { } r || !_content.IsVisible) return;
        var (pl, pt, pw, ph) = _platePx;
        if (pw < 1 || ph < 1) return;
        float glow = (float)(Frostbar.Core.Shell.GlassSpill.GlowRadius * VisualTreeHelper.GetDpi(_content).DpiScaleX);
        var (nx, ny, _) = Frostbar.Core.Shell.GlassSpill.Nearest(new Frostbar.Core.Shell.RoundedBox(r.Left + pl, r.Top + pt, pw, ph, _plate.Radius), xPx, yPx);
        _plate.Spill(new System.Numerics.Vector2((float)(nx - r.Left - pl), (float)(ny - r.Top - pt)), strength, glow);
    }

    /// <summary>The plate's last spilled light: where (its pixels), how strong, when (Environment ms); diagnostics.</summary>
    public (System.Numerics.Vector2 at, float strength, long when)? LastSpill => _plate.LastSpill;

    /// <summary>For each piece shown, its last spilled light (diagnostics; the plate's for a window without pieces).</summary>
    public IReadOnlyList<(object? key, (System.Numerics.Vector2 at, float strength, long when)? spill)> Spills() =>
        _pieceList == null ? [(null, _plate.LastSpill)] : [.. _pieces.Where(s => s.Piece != null).Select(s => (s.Key, s.Plate.LastSpill))];

    /// <summary>
    /// Diagnostics "glass-spill": the pieces of glass a press's light reached within the last <paramref name="withinMs"/>
    /// ms, in every window: the window's title, the piece's key (null: the plate), how strong, where (its pixels).
    /// </summary>
    public static IReadOnlyList<(string window, string? key, float strength, float x, float y, long ago)> Spilled(long withinMs)
    {
        long now = Environment.TickCount64;
        var list = new List<(string, string?, float, float, float, long)>();
        foreach (var w in Live)
            if (w.TryGetTarget(out var b))
                foreach (var (key, s) in b.Spills())
                    if (s is { } spill && now - spill.when <= withinMs)
                        list.Add((b._content.Title, key?.GetType().Name + (key is FrameworkElement { Name.Length: > 0 } fe ? "#" + fe.Name : key is FrameworkElement e2 ? ":" + System.Windows.Automation.AutomationProperties.GetName(e2) : ""),
                            spill.strength, spill.at.X, spill.at.Y, now - spill.when));
        return list;
    }

    int SpillFrom(double x, double y, bool pressedLit)
    {
        if (!TakesSpill || !Glassy || _rect is not { } r || !_content.IsVisible || !_content.IsHitTestVisible) return 0;
        double s = VisualTreeHelper.GetDpi(_content).DpiScaleX;
        double reach = Frostbar.Core.Shell.GlassSpill.Reach * s;
        float glow = (float)(Frostbar.Core.Shell.GlassSpill.GlowRadius * s);
        int lit = 0;
        void Light(Plate plate, double l, double t, double w, double h, double radius)
        {
            var box = new Frostbar.Core.Shell.RoundedBox(l, t, w, h, radius);
            var (nx, ny, d) = Frostbar.Core.Shell.GlassSpill.Nearest(box, x, y);
            if (d <= 0 && pressedLit) return;
            double strength = Frostbar.Core.Shell.GlassSpill.Strength(d, reach);
            if (strength <= 0) return;
            plate.Spill(new System.Numerics.Vector2((float)(nx - l), (float)(ny - t)), (float)strength, glow);
            lit++;
        }
        if (_pieceList != null)
        {
            foreach (var slot in _pieces)
                if (slot.Piece is { } q && slot.Holder.IsVisible && q.Opacity > 0.5f) Light(slot.Plate, r.Left + q.L, r.Top + q.T, q.W, q.H, slot.Plate.Radius);
            return lit;
        }
        var (pl, pt, pw, ph) = _platePx;
        if (pw >= 1 && ph >= 1) Light(_plate, r.Left + pl, r.Top + pt, pw, ph, _plate.Radius);
        return lit;
    }

    ShadowLayer? _shadow;

    /// <summary>The shadow under the plate (<see cref="Material.Shadow"/>): its own window right under the pair, made or let go.</summary>
    void SetShadow(Material m)
    {
        if (m.Shadow == null)
        {
            _shadow?.Dispose();
            _shadow = null;
            return;
        }
        if (_shadow == null)
        {
            _shadow = new ShadowLayer();
            _shadow.SetPlateDepth(_plateDepth);
            if (_excluded) Native.SetWindowDisplayAffinity(_shadow.Handle, Native.WDA_EXCLUDEFROMCAPTURE);
            if (_content.IsVisible) _shadow.Show();
        }
        if (_rect is { } r) _shadow.Place(r, _platePx, _plate.Radius, m.Shadow, VisualTreeHelper.GetDpi(_content).DpiScaleX, _backdrop.Handle);
    }

    readonly Windows.UI.Composition.ContainerVisual _root;

    // ---- pieces (macOS 27's Control Center and Notification Center, the user's "A", 2026-10-18) -------------------------

    /// <summary>A piece's plate, the holder that clips and fades it, and its lens; kept for its part (<see cref="GlassPiece.Key"/>).</summary>
    sealed class PieceSlot(Windows.UI.Composition.ContainerVisual holder, Plate plate, Material shown)
    {
        public readonly Windows.UI.Composition.ContainerVisual Holder = holder;
        public readonly Plate Plate = plate;
        public LensTarget? Lens;
        public object? Key;
        /// <summary>The piece it shows now (glass), or null: hidden.</summary>
        public GlassPiece? Piece;
        /// <summary>The material its plate has now (the pieces' material in the slot's tone), and the tone its part was last told of.</summary>
        public Material? Shown = shown;
        public object? ToldKey;
        public bool ToldDark;
        /// <summary>How deep its shadow is for what lies behind it (Core's ShadowDepth), and what was read there last.</summary>
        public float Depth = 1;
        public double Luma = double.NaN, Deviation = double.NaN;
    }

    // Stacked in this order (the last on top): the ones showing as listed, then the hidden ones.
    List<PieceSlot> _pieces = new();
    Windows.UI.Composition.ContainerVisual? _piecesRoot;
    IReadOnlyList<GlassPiece>? _pieceList;
    Material? _pieceMaterial;

    /// <summary>The pieces shown instead of the plate (null: the plate), as last set (diagnostics).</summary>
    public IReadOnlyList<GlassPiece>? Pieces => _pieceList;

    /// <summary>
    /// Pieces of glass instead of the one plate (macOS 26 and 27 lay no panel under the Control Center: each control is
    /// its own glass, floating on the wallpaper): rectangles in pixels of the window, each its own plate of
    /// <paramref name="material"/> (its own radius, lens, shadow and pointer light), clipped as given (a card scrolled
    /// half out of its list). A piece that is not glass (an active button: a white circle the window draws) keeps only
    /// its shadow. Cheap enough to set as the pieces move (a card going); null goes back to the plate.
    /// </summary>
    public void SetPieces(IReadOnlyList<GlassPiece>? pieces, Material? material = null)
    {
        var c = Composition.Compositor;
        var m = material ?? _pieceMaterial ?? _material;
        bool newMaterial = m != _pieceMaterial;
        _pieceMaterial = m;
        _pieceList = pieces;
        _plate.Visual.IsVisible = pieces == null;
        if (_piecesRoot == null && pieces != null)
        {
            _piecesRoot = c.CreateContainerVisual();
            // As faded as the plate is now: placed once the window shows, after its fade began from 0 (Fades.FadeIn
            // steps the plate's opacity each frame), the pieces showed whole for one frame over content still faint
            // (panels-appear, 2026-10-18).
            _piecesRoot.Opacity = _plate.Visual.Opacity;
            _root.Children.InsertAbove(_piecesRoot, _plate.Visual);
        }
        if (_piecesRoot != null) _piecesRoot.IsVisible = pieces != null;
        if (newMaterial) foreach (var s in _pieces) { s.Shown = null; ShowTone(s); }
        int n = pieces?.Count ?? 0;
        // Each piece to the plate its part had (by its key). Taken by their places in the list, a part's plate and lens
        // went to another part as the list changed (the Control Center's parts fading out of it as a view's glass
        // grows), and the lens's picture was made again for the other's size (2026-10-18, lens-flicker: 11 times as
        // Wi-Fi's view grew and 6 going back; 5 and 1 by key, the glass itself growing). A new key takes a plate no
        // part of the list has, the nearest in place and size: the part pressed hands its plate and lens to the glass
        // growing out of it, and that glass to the view's.
        var slotFor = new PieceSlot[n];
        var taken = new HashSet<PieceSlot>();
        for (int i = 0; i < n; i++)
            if (pieces![i].Key is { } key && _pieces.FirstOrDefault(s => !taken.Contains(s) && Equals(s.Key, key)) is { } own)
            {
                slotFor[i] = own;
                taken.Add(own);
            }
        var keys = new HashSet<object>();
        for (int i = 0; i < n; i++) if (pieces![i].Key is { } key) keys.Add(key);
        for (int i = 0; i < n; i++)
        {
            if (slotFor[i] != null) continue;
            var p = pieces![i];
            PieceSlot? best = null;
            double nearest = double.MaxValue;
            foreach (var s in _pieces)
            {
                if (taken.Contains(s) || s.Key != null && keys.Contains(s.Key)) continue;
                double d = s.Piece is { } q ? Math.Abs(q.L - p.L) + Math.Abs(q.T - p.T) + Math.Abs(q.W - p.W) + Math.Abs(q.H - p.H) : 1e6;
                if (d < nearest) { nearest = d; best = s; }
            }
            if (best == null)
            {
                best = new PieceSlot(c.CreateContainerVisual(), new Plate(m), m);
                best.Holder.Children.InsertAtTop(best.Plate.Visual);
                _piecesRoot!.Children.InsertAtTop(best.Holder);
                _pieces.Add(best);
            }
            slotFor[i] = best;
            taken.Add(best);
        }
        foreach (var s in _pieces)
            if (!taken.Contains(s)) { s.Piece = null; s.Holder.IsVisible = false; }
        // Stacked as listed (the last on top: the glass growing over the parts fading).
        var order = new List<PieceSlot>(_pieces.Count);
        foreach (var s in slotFor) if (!order.Contains(s)) order.Add(s);
        foreach (var s in _pieces) if (!taken.Contains(s)) order.Add(s);
        if (!order.SequenceEqual(_pieces))
        {
            foreach (var s in order)
            {
                _piecesRoot!.Children.Remove(s.Holder);
                _piecesRoot.Children.InsertAtTop(s.Holder);
            }
            _pieces = order;
        }
        for (int i = 0; i < n; i++)
        {
            var slot = slotFor[i];
            var p = pieces![i];
            var (holder, plate) = (slot.Holder, slot.Plate);
            slot.Key = p.Key;
            slot.Piece = p.Glass ? p : null;
            if (!p.Glass) { holder.IsVisible = false; continue; }
            plate.SetRadius(p.Radius);
            plate.SetBounds(p.L, p.T, p.W, p.H);
            if (p.Clip is { } clip)
            {
                var inset = holder.Clip as Windows.UI.Composition.InsetClip ?? c.CreateInsetClip();
                // The holder spans the window: its clip is the piece's clip measured from the window's edges.
                holder.Size = new System.Numerics.Vector2(_rect is { } r ? r.Width : 0, _rect is { } r2 ? r2.Height : 0);
                inset.LeftInset = Math.Max(0, clip.L);
                inset.TopInset = Math.Max(0, clip.T);
                inset.RightInset = Math.Max(0, holder.Size.X - clip.R);
                inset.BottomInset = Math.Max(0, holder.Size.Y - clip.B);
                holder.Clip = inset;
            }
            else holder.Clip = null;
            holder.Opacity = p.Opacity;
            holder.IsVisible = true;
        }
        if (_shadow != null)
        {
            var s = VisualTreeHelper.GetDpi(_content).DpiScaleX;
            _shadow.SetPieces(pieces == null ? null : m.Shadow == null ? [] : pieces, m.Shadow ?? new PlateShadow(0, 0, 0), s, pieces == null ? null : PieceDepths(pieces));
        }
        RefreshLens();
    }

    /// <summary>Diagnostics: the named layers left out of the plate and every piece (<see cref="Plate.LeaveOut"/>).</summary>
    public void LeaveOut(IReadOnlyCollection<string> off)
    {
        _plate.LeaveOut(off);
        foreach (var slot in _pieces) slot.Plate.LeaveOut(off);
    }

    /// <summary>The pieces' lenses: one target each (glass only), placed on the screen as the window is.</summary>
    void RefreshPieceLenses(LensCapture? capture)
    {
        var s = VisualTreeHelper.GetDpi(_content).DpiScaleX;
        var style = _pieceMaterial is { } m && m.Kind == MaterialKind.Blur ? m.Lens : Frostbar.Core.Shell.LensStyle.None;
        bool Wanted(PieceSlot slot) => slot.Piece != null && _pieceList != null && style != Frostbar.Core.Shell.LensStyle.None && capture != null;
        // The lenses wanted first, those let go after: the window stays out of capture through a change of list.
        foreach (var slot in _pieces)
        {
            if (!Wanted(slot)) continue;
            var p = slot.Piece!;
            var lens = slot.Lens;
            bool added = lens == null;
            lens ??= slot.Lens = new LensTarget(slot.Plate, Exclude) { Style = style, WindowExcluded = () => _excluded };
            lens.Style = style;
            lens.Presence = _presence; // a piece made as the glass comes: as far in as the rest
            slot.Plate.SetPresence(_presence);
            var held = slot;
            SetTone(lens, _pieceMaterial!, _ => ShowTone(held));
            lens.Behind = (luma, deviation) => Seen(held, luma, deviation);
            lens.Dark = lens.Tone?.Dark ?? _pieceMaterial!.Dark;
            ShowTone(slot);
            if (added) capture!.Add(lens);
            lens.Rect = _rect is { } r && p.W >= 1 && p.H >= 1
                ? new Frostbar.Core.Shell.ScreenRect(r.Left + (int)Math.Round(p.L), r.Top + (int)Math.Round(p.T), r.Left + (int)Math.Round(p.L + p.W), r.Top + (int)Math.Round(p.T + p.H))
                : default;
            bool clipped = p.Clip is { } c && (c.R <= p.L || c.L >= p.L + p.W || c.B <= p.T || c.T >= p.T + p.H);
            lens.Visible = _content.IsVisible && !clipped;
            lens.Radius = p.Radius;
            lens.Scale = s;
            Join(lens, slot, s);
            capture!.Update(lens);
        }
        foreach (var slot in _pieces)
            if (!Wanted(slot) && slot.Lens != null)
            {
                capture?.Remove(slot.Lens, withPlate: !slot.Holder.IsVisible);
                slot.Lens = null;
                ShowTone(slot);
            }
    }

    // ---- pieces of glass running together like drops (2026-11-10; Core's LiquidJoin) ----------------------------------------

    double _droplets;

    /// <summary>
    /// How far apart (DIPs) the pieces' shapes run together now (0: each its own). A panel coming out of its item or
    /// going back into it sets it at each frame, before its pieces are placed (Fades); hidden, it is none again.
    /// </summary>
    public double Droplets { get => _droplets; set => _droplets = Math.Max(0, value); }

    /// <summary>
    /// Diagnostics "droplets": every shown panel's pieces held running together this far (DIPs), as on their way out of
    /// their item, for a photo; null: as they are.
    /// </summary>
    public static void HoldDroplets(double? dips)
    {
        HeldDroplets = dips;
        Live.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in Live.ToList())
            if (w.TryGetTarget(out var b) && b._pieceList != null && b._content.IsVisible) b.RefreshLens();
    }

    static double? HeldDroplets;

    /// <summary>How many pieces' lenses are drawn joined with a neighbour's now (diagnostics).</summary>
    public int JoinedPieces => _pieces.Count(s => s.Piece != null && s.Lens is { JoinMargin: > 0 });

    /// <summary>The most pieces that were joined at once since it was last asked (diagnostics: a coming is over in a third of a second).</summary>
    public int TakeJoinedMost() { int most = _joinedMost; _joinedMost = 0; return most; }
    int _joinedMost;

    /// <summary>
    /// A piece's lens joined with those of the pieces near it: every other piece shown whose rectangle is nearer than
    /// the blend (the nearest eight), and the piece's tint, which its lens draws while joined. None when nothing runs
    /// together, or no piece is near.
    /// </summary>
    void Join(LensTarget lens, PieceSlot slot, double scale)
    {
        float blend = (float)((HeldDroplets ?? _droplets) * scale);
        if (blend <= 0 || !lens.Visible || _rect is not { } r)
        {
            lens.JoinBlend = 0;
            lens.Neighbours = [];
            return;
        }
        var a = lens.Rect;
        var near = new List<(double gap, Frostbar.Core.Shell.ScreenRect rect, float radius)>();
        foreach (var other in _pieces)
        {
            if (other == slot || other.Piece is not { } p || p.W < 1 || p.H < 1) continue;
            var b = new Frostbar.Core.Shell.ScreenRect(r.Left + (int)Math.Round(p.L), r.Top + (int)Math.Round(p.T), r.Left + (int)Math.Round(p.L + p.W), r.Top + (int)Math.Round(p.T + p.H));
            double dx = Math.Max(0, Math.Max(a.Left - b.Right, b.Left - a.Right)), dy = Math.Max(0, Math.Max(a.Top - b.Bottom, b.Top - a.Bottom));
            double gap = Math.Sqrt(dx * dx + dy * dy);
            if (gap < blend) near.Add((gap, b, p.Radius));
        }
        near.Sort((x, y) => x.gap.CompareTo(y.gap));
        lens.Neighbours = [.. near.Take(Frostbar.Core.Shell.LensJoin.Most).Select(n => (n.rect, n.radius))];
        lens.JoinBlend = lens.Neighbours.Length > 0 ? blend : 0;
        var tint = (slot.Shown ?? _pieceMaterial ?? _material).Tint;
        lens.JoinTint = (tint.R / 255f, tint.G / 255f, tint.B / 255f, tint.A / 255f);
        if (lens.JoinMargin > 0) _joinedMost = Math.Max(_joinedMost, JoinedPieces);
    }

    // ---- the shadow's depth for what lies behind the glass (2026-11-10; Core's ShadowDepth) --------------------------------

    float _plateDepth = 1;
    double _plateLuma = double.NaN, _plateDeviation = double.NaN;

    /// <summary>
    /// A lens read what is behind its plate (the mean of its brightness and how far it varies): the plate's shadow, or
    /// the piece's, deepens over something busy and goes shallow over a plain light background. Shown only when it
    /// differs enough from what is shown (each read differs a little).
    /// </summary>
    void Seen(PieceSlot? slot, double luma, double deviation)
    {
        float depth = (float)Frostbar.Core.Shell.ShadowDepth.Of(luma, deviation);
        if (slot == null)
        {
            (_plateLuma, _plateDeviation) = (luma, deviation);
            if (!Frostbar.Core.Shell.ShadowDepth.Differs(_plateDepth, depth)) return;
            _plateDepth = depth;
            _shadow?.SetPlateDepth(depth);
            return;
        }
        (slot.Luma, slot.Deviation) = (luma, deviation);
        if (!Frostbar.Core.Shell.ShadowDepth.Differs(slot.Depth, depth)) return;
        slot.Depth = depth;
        if (_shadow != null && _pieceList != null) _shadow.SetPieceDepths(PieceDepths(_pieceList));
    }

    /// <summary>Each listed piece's shadow's depth (its slot's; 1 for a piece without glass).</summary>
    float[] PieceDepths(IReadOnlyList<GlassPiece> pieces)
    {
        var depths = new float[pieces.Count];
        for (int i = 0; i < depths.Length; i++)
        {
            depths[i] = 1;
            foreach (var s in _pieces)
                if (ReferenceEquals(s.Piece, pieces[i])) { depths[i] = s.Depth; break; }
        }
        return depths;
    }

    /// <summary>
    /// Diagnostics "shadows": for each shown glass with a shadow, its window's rectangle and each shadow's depth with
    /// what was read behind it (the plate's: piece false).
    /// </summary>
    public static IReadOnlyList<object> ShadowStates()
    {
        Live.RemoveAll(w => !w.TryGetTarget(out _));
        var list = new List<object>();
        foreach (var w in Live.ToList())
        {
            if (!w.TryGetTarget(out var b) || b._shadow == null || !b._content.IsVisible) continue;
            static double R(double v) => double.IsNaN(v) ? -1 : Math.Round(v, 3);
            var parts = new List<object>();
            if (b._pieceList == null) parts.Add(new { piece = false, depth = Math.Round(b._plateDepth, 3), luma = R(b._plateLuma), deviation = R(b._plateDeviation) });
            else
                foreach (var s in b._pieces)
                    if (s.Piece != null) parts.Add(new { piece = true, depth = Math.Round(s.Depth, 3), luma = R(s.Luma), deviation = R(s.Deviation), at = new[] { s.Piece.L, s.Piece.T, s.Piece.W, s.Piece.H } });
            list.Add(new { window = b._content.GetType().Name, rect = b._rect is { } r ? new[] { r.Left, r.Top, r.Right, r.Bottom } : null, parts });
        }
        return list;
    }

    // ---- the glass's tone (item 4, the user's "A", 2026-10-19) ------------------------------------------------------------

    bool _adapts;

    /// <summary>
    /// The glass suits what is behind it, each piece on its own (or the plate): light over a backdrop clearly brighter, dark
    /// over one clearly darker (Core's <see cref="Frostbar.Core.Shell.GlassTone"/>), when its material has the other tone
    /// (<see cref="Material.Opposite"/>: only with the lens, which reads what is behind). The owner colours what is on
    /// each piece as <see cref="ToneChanged"/> tells it. Off by default: a window whose content keeps its colours.
    /// </summary>
    public bool Adapts
    {
        get => _adapts;
        set
        {
            if (_adapts == value) return;
            _adapts = value;
            SetLens(_material);
            ShowPlateTone();
            RefreshLens();
        }
    }

    /// <summary>
    /// The glass under a part turned (key: the piece's <see cref="GlassPiece.Key"/>, null for the plate; true: dark now).
    /// Also told when a part comes onto glass already turned. UI thread.
    /// </summary>
    public event Action<object?, bool>? ToneChanged;

    /// <summary>A lens's tone: kept while the appearance is the same (a new tint, a new list of pieces), made anew for a new one; none unless the window adapts.</summary>
    void SetTone(LensTarget lens, Material m, Action<bool> turned)
    {
        if (!_adapts || m.Opposite == null)
        {
            lens.Tone = null;
            lens.Turned = null;
            return;
        }
        if (lens.Tone == null || lens.Tone.AppearanceDark != m.Dark) lens.Tone = new Frostbar.Core.Shell.GlassTone(m.Dark);
        lens.Turned = turned;
    }

    /// <summary>A piece's plate in its tone, and its part told when the tone it shows changed (or a part came onto it turned).</summary>
    void ShowTone(PieceSlot slot)
    {
        if (_pieceMaterial is not { } m) return;
        bool dark = slot.Lens?.Tone?.Dark ?? m.Dark;
        var wanted = m.InTone(dark);
        if (!ReferenceEquals(slot.Shown, wanted))
        {
            slot.Plate.SetMaterial(wanted);
            slot.Shown = wanted;
        }
        if (slot.Key == null) return;
        bool sameKey = Equals(slot.ToldKey, slot.Key);
        bool tell = sameKey ? slot.ToldDark != dark : dark != m.Dark;
        slot.ToldKey = slot.Key;
        slot.ToldDark = dark;
        if (tell) ToneChanged?.Invoke(slot.Key, dark);
    }

    Material? _plateShown;
    bool _plateToldDark = true, _plateTold;

    /// <summary>The plate's tone now.</summary>
    public bool PlateDark => _lens?.Tone?.Dark ?? _material.Dark;

    /// <summary>The plate in its tone, and the owner told when that changed.</summary>
    void ShowPlateTone()
    {
        bool dark = PlateDark;
        var wanted = _material.InTone(dark);
        if (!ReferenceEquals(_plateShown, wanted))
        {
            _plate.SetMaterial(wanted);
            SetShadow(wanted);
            _plateShown = wanted;
        }
        bool tell = _plateTold ? _plateToldDark != dark : dark != _material.Dark;
        _plateTold = true;
        _plateToldDark = dark;
        if (tell) ToneChanged?.Invoke(null, dark);
    }

    /// <summary>
    /// Diagnostics: the tone of the glass under each part shown (key → dark; what is behind it, NaN when it does not
    /// suit it: the light appearance, no lens), or of the plate (key null) when there are no pieces.
    /// </summary>
    public IReadOnlyList<(object? key, bool dark, double luma)> Tones()
    {
        var list = new List<(object?, bool, double)>();
        if (_pieceList == null) list.Add((null, PlateDark, _lens?.Tone?.Luma ?? double.NaN));
        foreach (var s in _pieces)
            if (s.Piece != null) list.Add((s.Key, s.Lens?.Tone?.Dark ?? _pieceMaterial?.Dark ?? _material.Dark, s.Lens?.Tone?.Luma ?? double.NaN));
        return list;
    }

    /// <summary>Takes the lens on or off for a material (it has one: <see cref="Material.Lens"/>, over the blur, where the lens runs).</summary>
    void SetLens(Material m)
    {
        var capture = m.Lens != Frostbar.Core.Shell.LensStyle.None && m.Kind == MaterialKind.Blur ? LensCapture.For(_content.Dispatcher) : null;
        if (capture == null)
        {
            if (_lens != null) LensCapture.Current?.Remove(_lens);
            _lens = null;
            return;
        }
        if (_lens == null)
        {
            _lens = new LensTarget(_plate, Exclude) { Style = m.Lens, WindowExcluded = () => _excluded };
            capture.Add(_lens);
        }
        _lens.Style = m.Lens;
        SetTone(_lens, m, _ => ShowPlateTone());
        _lens.Behind = (luma, deviation) => Seen(null, luma, deviation);
        _lens.Dark = _lens.Tone?.Dark ?? m.Dark;
        RefreshLens();
    }

    /// <summary>Tells the lens where the plate is now (and whether it shows): it draws it again at once.</summary>
    void RefreshLens()
    {
        if (_pieces.Count > 0 || _pieceList != null)
        {
            bool wanted = _pieceList != null && _pieceMaterial is { Kind: MaterialKind.Blur } pm && pm.Lens != Frostbar.Core.Shell.LensStyle.None;
            RefreshPieceLenses(LensCapture.Current ?? (wanted ? LensCapture.For(_content.Dispatcher) : null));
        }
        if (_lens == null || LensCapture.Current is not { } capture) return;
        var (l, t, w, h) = _platePx;
        _lens.Rect = _rect is { } r && w >= 1 && h >= 1
            ? new Frostbar.Core.Shell.ScreenRect(r.Left + (int)Math.Round(l), r.Top + (int)Math.Round(t), r.Left + (int)Math.Round(l + w), r.Top + (int)Math.Round(t + h))
            : default;
        _lens.Visible = _content.IsVisible && _pieceList == null; // pieces have lenses of their own
        _lens.Presence = _presence;
        _lens.Radius = Roundness != null ? _plate.Radius : _material.RadiusPx;
        _lens.Scale = VisualTreeHelper.GetDpi(_content).DpiScaleX;
        capture.Update(_lens);
    }

    // The lenses (the plate's, the pieces') that want the window out of capture: it goes back only when none does.
    int _excludedBy;

    /// <summary>Leaves the pair (and the window above it) out of screen capture, or puts it back (the lens reads the screen without its own glass).</summary>
    void Exclude(bool on)
    {
        _excludedBy = Math.Max(0, _excludedBy + (on ? 1 : -1));
        on = _excludedBy > 0;
        if (on == _excluded) return;
        _excluded = on;
        uint affinity = on ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE;
        Native.SetWindowDisplayAffinity(_backdrop.Handle, affinity);
        var h = new WindowInteropHelper(_content).Handle;
        if (h != IntPtr.Zero) Native.SetWindowDisplayAffinity(h, affinity);
        if (_above != null) Native.SetWindowDisplayAffinity(_above.Handle, affinity);
        if (_shadow != null) Native.SetWindowDisplayAffinity(_shadow.Handle, affinity);
    }

    /// <summary>Whether the pair is left out of screen capture now (diagnostics).</summary>
    public bool Excluded => _excluded;

    CompositionWindow? _above;
    Windows.UI.Composition.ContainerVisual? _aboveRoot;

    /// <summary>
    /// Visuals drawn above everything of the pair, DWM thumbnails placed in the backdrop window too (a thumbnail draws
    /// above its window's own composition content: M0 S27): a second click-through window with the same rectangle,
    /// owned by the content window so that it stays above it. Made the first time it is asked for (the Dock's minimized
    /// windows: the small app icon on each picture).
    /// </summary>
    public Windows.UI.Composition.ContainerVisual Above
    {
        get
        {
            if (_aboveRoot != null) return _aboveRoot;
            var owner = new WindowInteropHelper(_content).Handle;
            _above = new CompositionWindow(WindowRect() ?? LayoutRect() ?? new RECT(0, 0, 1, 1), clickThrough: true, owner);
            _aboveRoot = Composition.Compositor.CreateContainerVisual();
            _above.SetRoot(_aboveRoot);
            if (_excluded) Native.SetWindowDisplayAffinity(_above.Handle, Native.WDA_EXCLUDEFROMCAPTURE);
            if (_content.IsVisible) _above.Show();
            return _aboveRoot;
        }
    }

    /// <summary>The window above the pair (<see cref="Above"/>), or none yet.</summary>
    public IntPtr AboveHandle => _above?.Handle ?? IntPtr.Zero;

    IntPtr Hook(IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg == 0x0047 /*WM_WINDOWPOSCHANGED*/ && !_syncing)
        {
            uint flags = (uint)Marshal.ReadInt32(lp, 2 * IntPtr.Size + 16); // WINDOWPOS.flags
            const uint unchanged = Native.SWP_NOMOVE | Native.SWP_NOSIZE;
            if ((flags & unchanged) != unchanged) QueueCheck();
        }
        return IntPtr.Zero;
    }

    public IntPtr BackdropHandle => _backdrop.Handle;

    /// <summary>
    /// Visuals drawn above the plate, in the backdrop window's pixels (the Dock's icons, ADR-007). Click-through like
    /// the rest of the backdrop window; the content window above takes the pointer.
    /// </summary>
    public Windows.UI.Composition.ContainerVisual Overlay { get; }

    /// <summary>Leave room around the plate (for a shadow, or a menu's margin), in DIPs.</summary>
    public Thickness Inset { get => _inset; set { _inset = value; QueueCheck(); } }

    /// <summary>The plate's material as last set (diagnostics).</summary>
    public Material Material => _material;

    public void SetMaterial(Material m)
    {
        _material = m;
        SetLens(m);
        _plateShown = null;
        ShowPlateTone(); // the plate and its shadow in its tone
    }

    /// <summary>Liquid Glass's light where it is touched (pixels in the plate along its row; strength 0 to 1). See <see cref="Plate.SetLight"/>.</summary>
    public void SetLight(float? along, float strength, LightSide inner = LightSide.Top) => _plate.SetLight(along, strength, inner);

    /// <summary>
    /// The glass lights up where the pointer is over it (the user's "A", 2026-10-18; only with Liquid Glass). On by
    /// default; the Dock, which lights its own row (<see cref="SetLight"/>), turns it off.
    /// </summary>
    public bool PointerLight
    {
        get => _pointerLight;
        set { _pointerLight = value; if (!value) LightOff(); }
    }
    bool _pointerLight = true;

    // The soft light's radii round the pointer, and how far along the rim it brightens (DIPs).
    const float GlowX = 130, GlowY = 110, RimX = 140, RimY = 90;

    void PointAt(Point p)
    {
        if (!_pointerLight) return;
        float s = (float)VisualTreeHelper.GetDpi(_content).DpiScaleX;
        var glow = new System.Numerics.Vector2(GlowX * s, GlowY * s);
        var rim = new System.Numerics.Vector2(RimX * s, RimY * s);
        if (_pieceList != null)
        {
            // Each piece lit from the same point: those near it catch the light, those far off barely.
            foreach (var slot in _pieces)
                slot.Plate.SetPointLight(slot.Piece is { } q ? new System.Numerics.Vector2((float)p.X * s - q.L, (float)p.Y * s - q.T) : null, glow, rim);
            return;
        }
        _plate.SetPointLight(new System.Numerics.Vector2((float)p.X * s - _platePx.l, (float)p.Y * s - _platePx.t), glow, rim);
    }

    /// <summary>The pointer light goes from the plate and every piece.</summary>
    void LightOff()
    {
        _plate.SetPointLight(null, default, default);
        foreach (var slot in _pieces) slot.Plate.SetPointLight(null, default, default);
    }

    /// <summary>The plate is Liquid Glass now (its material's glass is on).</summary>
    public bool Glassy => _material.Glass > 0;

    /// <summary>The pointer light is on now (diagnostics).</summary>
    public bool PointLightOn => _plate.PointLightOn || _pieces.Any(x => x.Plate.PointLightOn);

    /// <summary>
    /// Fades the plate to <paramref name="opacity"/> over <paramref name="ms"/> (DWM runs it; 0 ms sets it at once).
    /// Quick to start by default (appearing); <paramref name="smooth"/>: slow at both ends, the smoothstep curve
    /// (disappearing, in step with a layered window faded by the same curve).
    /// </summary>
    public void FadePlate(float opacity, double ms, bool smooth = false)
    {
        // The plate and its shadow (in its own window) together.
        var faded = new List<Windows.UI.Composition.Visual> { _plate.Visual };
        if (_shadow != null) faded.Add(_shadow.Visual);
        if (_piecesRoot != null) faded.Add(_piecesRoot);
        foreach (var v in faded)
        {
            if (ms <= 0) { v.StopAnimation("Opacity"); v.Opacity = opacity; continue; }
            var c = Composition.Compositor;
            var a = c.CreateScalarKeyFrameAnimation();
            a.InsertKeyFrame(1f, opacity, smooth
                ? c.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(1 / 3f, 0f), new System.Numerics.Vector2(2 / 3f, 1f)) // = 3t² − 2t³
                : c.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.2f, 0.8f), new System.Numerics.Vector2(0.25f, 1f)));
            a.Duration = TimeSpan.FromMilliseconds(ms);
            v.StartAnimation("Opacity", a);
        }
    }

    // ---- the glass coming by its lens, not by a fade (ADR-016's materializing; the user's "A レンズの強さで実体化", 2026-10-23) ----

    float _presence = 1;
    bool _presenting;

    /// <summary>The glass's presence now (diagnostics), and whether it comes by its lens (true) or by its fade.</summary>
    public float Presence => _presence;
    public bool Presenting => _presenting;

    /// <summary>The plate (or its pieces) has a lens where the lens runs: it may come by it.</summary>
    bool LensWanted
    {
        get
        {
            var m = _pieceList != null ? _pieceMaterial ?? _material : _material;
            return LensCapture.Available && m.Kind == MaterialKind.Blur && m.Lens != Frostbar.Core.Shell.LensStyle.None;
        }
    }

    IReadOnlyCollection<LensTarget> MyLenses() => _pieceList != null
        ? [.. _pieces.Where(s => s.Lens != null && s.Piece != null).Select(s => s.Lens!)]
        : _lens != null ? [_lens] : [];

    /// <summary>
    /// Whether the glass comes by its lens now: it has one where the lens runs, and it stands clear of the other plates
    /// with a lens (a submenu over its menu fades: <see cref="LensCapture.Clear"/>). Before it shows its place is not
    /// known: it may.
    /// </summary>
    public bool Presents
    {
        get
        {
            if (!LensWanted) return false;
            if (!_content.IsVisible) return true;
            return LensCapture.Current is not { } capture || capture.Clear(MyLenses());
        }
    }

    /// <summary>
    /// The glass <paramref name="k"/> of the way in (0–1): by its lens's presence where it comes by its lens
    /// (<see cref="Presents"/>: the plate whole, its lens, its tint and lights, its shadow at <paramref name="presence"/>
    /// or <paramref name="k"/>), else faded as before (<see cref="FadePlate"/> at <paramref name="k"/>, at once). The
    /// fades call it at each frame. <paramref name="fade"/>: faded whatever it could do (Accessibility's "Reduce motion":
    /// the lens's magnification and bend growing in is a motion; its fade out already went by its fade).
    /// </summary>
    public void Appear(float k, float? presence = null, bool fade = false)
    {
        if (!fade && Presents)
        {
            if (!_presenting)
            {
                _presenting = true;
                FadePlate(1, 0); // whole: the presence brings it in
            }
            Present(presence ?? k);
            return;
        }
        if (_presenting)
        {
            _presenting = false;
            Present(1);
        }
        FadePlate(k, 0);
    }

    /// <summary>The glass's presence: the plate and pieces (what lies over their lenses), the shadow, the lenses drawn again.</summary>
    void Present(float k)
    {
        k = Math.Clamp(k, 0, 1);
        if (k == _presence) return;
        _presence = k;
        _plate.SetPresence(k);
        foreach (var slot in _pieces) slot.Plate.SetPresence(k);
        if (_shadow != null) { _shadow.Visual.StopAnimation("Opacity"); _shadow.Visual.Opacity = k; }
        if (LensCapture.Current is not { } capture) return;
        foreach (var lens in MyLenses())
        {
            lens.Presence = k;
            capture.Repaint(lens);
        }
    }

    (float l, float t, float w, float h)? _plateBounds;

    /// <summary>
    /// With <see cref="SetPlateBounds"/>: the plate cut with this share of its thinner side as its radius (whole pixels),
    /// its lens and its shadow too, in place of the material's radius (the Dock: macOS 27's rounded plate, 2026-11-08).
    /// </summary>
    public float? Roundness { get; init; }

    /// <summary>The plate set with <see cref="SetPlateBounds"/> (pixels in the window; diagnostics).</summary>
    public (float l, float t, float w, float h)? PlateBounds => _plateBounds;

    /// <summary>
    /// A plate smaller than the window, in pixels relative to the window (the Dock: its plate grows and shrinks with
    /// magnification inside a window of fixed size). Null goes back to the whole window minus <see cref="Inset"/>.
    /// Cheap enough for every frame: only the composition visual changes.
    /// </summary>
    public void SetPlateBounds((float l, float t, float w, float h)? px)
    {
        _plateBounds = px;
        if (px is { } b)
        {
            if (Roundness is { } k) _plate.SetRadius(MathF.Round(Math.Min(b.w, b.h) * k));
            _plate.SetBounds(b.l, b.t, b.w, b.h);
            if (_material.Shadow is { } look) _shadow?.SetPlate(b, _plate.Radius, look, VisualTreeHelper.GetDpi(_content).DpiScaleX);
            if (_platePx != b) { _platePx = b; RefreshLens(); }
        }
        else QueueCheck();
    }

    /// <summary>WPF's own placement in pixels (null before the first layout).</summary>
    RECT? LayoutRect()
    {
        double w = _content.ActualWidth > 0 ? _content.ActualWidth : _content.Width;
        double h = _content.ActualHeight > 0 ? _content.ActualHeight : _content.Height;
        if (!double.IsFinite(_content.Left) || !double.IsFinite(_content.Top) || !(w > 0) || !(h > 0)) return null;
        var s = VisualTreeHelper.GetDpi(_content);
        int l = AsWpf(_content.Left * s.DpiScaleX), t = AsWpf(_content.Top * s.DpiScaleY);
        return new RECT(l, t, l + AsWpf(w * s.DpiScaleX), t + AsWpf(h * s.DpiScaleY));
    }

    /// <summary>
    /// A pixel as WPF places the window: its DoubleUtil.DoubleToInt sends halves away from zero (from WPF's source; not
    /// measured here). Math.Round sends them to the even one, so at 125 % a window at 100.4 DIP (125.5 px) could stand a
    /// pixel from its plate, its shadow and its lens until the window's own rectangle was read after the layout.
    /// </summary>
    static int AsWpf(double px) => px > 0 ? (int)(px + 0.5) : (int)(px - 0.5);

    RECT? WindowRect()
    {
        var h = new WindowInteropHelper(_content).Handle;
        return h != IntPtr.Zero && Native.GetWindowRect(h, out var r) ? r : null;
    }

    /// <summary>Re-reads the real window rectangle after layout and rendering (coalesced).</summary>
    void QueueCheck()
    {
        if (_checkQueued) return;
        _checkQueued = true;
        _content.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { _checkQueued = false; Sync(WindowRect()); });
    }

    void Sync(RECT? rect)
    {
        if (_syncing || rect is not { } r || r.Width <= 0 || r.Height <= 0) return;
        _syncing = true;
        try
        {
            _backdrop.Move(r);
            var s = VisualTreeHelper.GetDpi(_content);
            float l = (float)(_inset.Left * s.DpiScaleX), t = (float)(_inset.Top * s.DpiScaleY);
            _platePx = _plateBounds ?? (l, t, r.Width - l - (float)(_inset.Right * s.DpiScaleX), r.Height - t - (float)(_inset.Bottom * s.DpiScaleY));
            _plate.SetBounds(_platePx.l, _platePx.t, _platePx.w, _platePx.h);
            // keep the content right above its backdrop
            var h = new WindowInteropHelper(_content).Handle;
            if (h != IntPtr.Zero) Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            // and the window above last, on top of both: owned by the content window, it still ended under the backdrop
            // when only the backdrop and the content were raised (2026-10-16, the Dock's dock-zorder: content, backdrop, above).
            _above?.Move(r);
            bool resized = _rect is not { } was || was.Width != r.Width || was.Height != r.Height;
            _rect = r;
            if (_shadow != null && _material.Shadow is { } look) _shadow.Place(r, _platePx, _plate.Radius, look, s.DpiScaleX, _backdrop.Handle);
            // The pieces' clips are measured from the window's edges: placed again when its size changes.
            if (resized && _pieceList != null) SetPieces(_pieceList);
            else RefreshLens();
        }
        finally { _syncing = false; }
    }

    public void Dispose()
    {
        Live.RemoveAll(w => !w.TryGetTarget(out var b) || ReferenceEquals(b, this));
        if (_lens != null) LensCapture.Current?.Remove(_lens, withPlate: true);
        _lens = null;
        foreach (var slot in _pieces)
        {
            if (slot.Lens != null) LensCapture.Current?.Remove(slot.Lens, withPlate: true);
            slot.Plate.Dispose();
        }
        _pieces.Clear();
        _plate.Dispose();
        _above?.Dispose();
        _shadow?.Dispose();
        _backdrop.Dispose();
    }
}

/// <summary>
/// V3 (ADR-003): a plain Win32 window that shows composition content only (no redirection bitmap). With
/// layered + transparent styles it blurs and lets clicks through (M0 S4).
/// </summary>
public sealed class CompositionWindow : IDisposable
{
    static Native.WndProcDelegate? _proc;
    static bool _registered;
    const string ClassName = "Frostbar.Composition";
    readonly DesktopWindowTarget _target;

    public IntPtr Handle { get; }

    /// <param name="owner">The window this one stays above (owned windows stay above their owner); none by default.</param>
    public CompositionWindow(RECT r, bool clickThrough, IntPtr owner = default)
    {
        if (!_registered)
        {
            _proc = static (h, msg, w, l) => msg == Native.WM_MOUSEACTIVATE ? new IntPtr(Native.MA_NOACTIVATE) : Native.DefWindowProc(h, msg, w, l);
            var wc = new Native.WNDCLASSEX { cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = Native.GetModuleHandle(null), lpszClassName = ClassName };
            if (Native.RegisterClassEx(ref wc) == 0) throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
            _registered = true;
        }
        long ex = Native.WS_EX_NOREDIRECTIONBITMAP | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
        if (clickThrough) ex |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT;
        Handle = Native.CreateWindowEx((int)ex, ClassName, "", Native.WS_POPUP, r.Left, r.Top, r.Width, r.Height, owner, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
        if (clickThrough) Native.SetLayeredWindowAttributes(Handle, 0, 255, 2 /*LWA_ALPHA*/);
        Native.SetDwm(Handle, Native.DWMWA_USE_HOSTBACKDROPBRUSH, 1);
        Native.SetDwm(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_DONOTROUND);
        _target = Composition.CreateTarget(Handle, topmost: true);
    }

    public void SetRoot(Windows.UI.Composition.Visual v) => _target.Root = v;
    public void Show() => Native.ShowWindow(Handle, Native.SW_SHOWNA);
    public void Hide() => Native.ShowWindow(Handle, Native.SW_HIDE);
    public void Move(RECT r) => Native.SetWindowPos(Handle, Native.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOACTIVATE);

    /// <summary>Moves it and puts it right under <paramref name="above"/> (a plate's shadow under the plate's window).</summary>
    public void MoveBelow(RECT r, IntPtr above) => Native.SetWindowPos(Handle, above, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOACTIVATE);
    public void Dispose()
    {
        // Its compositor target let go with it: each menu and panel made two or three of these, and their targets (and
        // the visuals under them) lived on until the finalizer.
        try { _target.Root = null; _target.Dispose(); } catch (Exception) { }
        Native.DestroyWindow(Handle);
    }
}
