using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Frostbar.App.Common;
using Frostbar.Core.Localization;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Sys;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Menus;

/// <summary>A small panel under a status item (volume, battery…): like a menu it never activates and closes on an outside click.</summary>
public class PanelPopup : Window
{
    protected readonly StackPanel Body = new() { Margin = new Thickness(14, 12, 14, 12), MinWidth = 260 };
    readonly WindowBackdrop _backdrop;
    protected readonly Brush Text, Dim;
    /// <summary>Animations are on (Windows' setting, or the settings' choice over it).</summary>
    protected readonly bool Animate;
    public IntPtr Handle => new WindowInteropHelper(this).Handle;
    public IntPtr BackdropHandle => _backdrop.BackdropHandle;

    /// <summary>Fades in as it opens (the Control Center); the status items' panels open at once, as menus do.</summary>
    public virtual bool FadesIn => false;

    /// <summary>Shown whole at once: it takes the place of the same panel in other colours (MenuController.SwapPanel).</summary>
    public bool AppearAtOnce { get; set; }

    /// <summary>
    /// Fades in; a panel of pieces of glass (macOS 27's Control Center and Notification Center) materializes instead:
    /// grows out of its menu bar item, its glass first (the user's "B", 2026-10-18).
    /// </summary>
    public void FadeIn()
    {
        if (!Animate || AppearAtOnce) return;
        // One that opens at once (a status item's, as menus do): its content there at once, its glass by its lens
        // (the user's "A レンズの強さで実体化", 2026-10-23).
        if (!FadesIn) { Fades.Materialize(this, _backdrop, Motion.PresenceIn); return; }
        if (_pieces != null) Fades.FadeIn(this, _backdrop, Fades.MaterializeInMs, center: MaterializeCenter);
        else Fades.FadeIn(this, _backdrop, Fades.PanelInMs);
    }

    /// <summary>Fades out, then closes (at once when animations are off); a panel of pieces goes back into its item.</summary>
    public void CloseFading()
    {
        if (Animate && _pieces != null) Fades.Dematerialize(this, _backdrop, MaterializeCenter, Fades.MaterializeOutMs, Close);
        else
        {
            // A stack goes back the way it came: into the Dock as it fades (macOS's).
            if (Animate && _rose is { } edge && IsVisible)
            {
                var (dx, dy) = TowardsDock(edge);
                Sinking = true;
                Fades.GoBy(this, dx, dy, Fades.PanelOutMs);
            }
            // By its lens: its glass a little longer, its content first (as a menu's).
            bool lensy = _backdrop.Presents && _rose == null;
            Fades.CloseFading(this, _backdrop, !Animate ? 0 : lensy ? Motion.PresenceOut : Fades.PanelOutMs, lensy ? Motion.PresenceContentOut : 1);
        }
    }

    /// <summary>The menu bar item it opened under (DIPs on the screen; empty: none showing, as from a hot corner).</summary>
    public Rect AnchorDip { get; set; } = Rect.Empty;

    /// <summary>Where it grows from and goes back into: its item's bottom middle, in its content's coordinates (asked at each frame: it is moved after it shows).</summary>
    Point MaterializeCenter()
    {
        double x = AnchorDip.IsEmpty ? ActualWidth - 24 : AnchorDip.Left + AnchorDip.Width / 2 - Left;
        double y = AnchorDip.IsEmpty ? 0 : AnchorDip.Bottom - Top;
        var m = Content is FrameworkElement f ? f.Margin : default;
        return new Point(x - m.Left, y - m.Top);
    }

    /// <summary>Still appearing (materializing or fading in): its parts are not where they will be (diagnostics: specs wait).</summary>
    public bool Appearing => Fades.IsAppearing(this);

    /// <summary>Comes out of the Dock as it opens (a stack, macOS's): placed already, it rises from the Dock's side.</summary>
    public virtual bool RisesFromDock => false;

    /// <summary>How far a panel rises out of the Dock, and how long it takes (2026-10-17: a stack only faded in where it was).</summary>
    public const double RiseDistance = 12, RiseMs = Motion.Rise;

    /// <summary>
    /// Starts <see cref="RiseDistance"/> nearer the Dock's <paramref name="edge"/> and glides to where it was placed,
    /// as it fades in (nothing when animations are off or it does not rise).
    /// </summary>
    public void Rise(Frostbar.Core.Dock.DockEdge edge)
    {
        if (!Animate || !RisesFromDock) return;
        _rose = edge;
        var (dx, dy) = TowardsDock(edge);
        Fades.ComeFrom(this, dx, dy, RiseMs, Motion.RiseSpring); // a spring (B16)
    }

    // The Dock's edge it rose from (null: it did not rise): it sinks back there as it fades out.
    Frostbar.Core.Dock.DockEdge? _rose;

    static (double dx, double dy) TowardsDock(Frostbar.Core.Dock.DockEdge edge) => edge switch
    {
        Frostbar.Core.Dock.DockEdge.Left => (-RiseDistance, 0.0),
        Frostbar.Core.Dock.DockEdge.Right => (RiseDistance, 0.0),
        _ => (0.0, RiseDistance),
    };

    /// <summary>Sinking back into the Dock as it closes (diagnostics).</summary>
    public bool Sinking { get; private set; }

    /// <summary>Still gliding to its place as it opens (diagnostics).</summary>
    public bool Rising => Fades.IsGliding(this);

    // ---- pieces of glass (macOS 27's Control Center and Notification Center, the user's "A", 2026-10-18) -------------

    /// <summary>A part of the panel that is a piece of glass: the element, its radius (DIPs, held to half its height), whether it casts a shadow.</summary>
    protected readonly record struct Piece(FrameworkElement Element, double Radius, bool Shadow = true);

    Func<IEnumerable<Piece>>? _pieces;
    Material? _pieceMaterial;
    IReadOnlyList<GlassPiece>? _placed;

    /// <summary>
    /// Lays no plate: the parts <paramref name="pieces"/> names are each a piece of glass of <paramref name="material"/>,
    /// following them as the layout changes and as they move (a card sliding away), each clipped to the list it scrolls
    /// in and as faded as it is.
    /// </summary>
    protected void UsePieces(Func<IEnumerable<Piece>> pieces, Material material)
    {
        _pieces = pieces;
        _pieceMaterial = material;
        // No plate from the start (none placed yet): before the first pieces the panel's own plate drew a lens of its
        // whole size (2026-10-18, lens-flicker: 10 lenses appearing for the Control Center's 9 pieces as it opened, 9
        // with this).
        _backdrop.SetPieces([], material);
        LayoutUpdated += (_, _) => PlacePieces();
        // Laid out before it shows, its parts were not visible then (none placed: the Notification Center, whose layout
        // does not change after it shows, had no glass at all, 2026-10-18): placed again once drawn.
        IsVisibleChanged += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, PlacePieces); };
        Tween.Stepped += PlacePieces;
        Fades.Stepped += PlacePieces; // materializing: the content scales without a layout pass
        Closed += (_, _) => { Tween.Stepped -= PlacePieces; Fades.Stepped -= PlacePieces; };
    }

    /// <summary>The pieces as placed last (diagnostics: pixels in the window).</summary>
    public IReadOnlyList<GlassPiece>? PlacedPieces => _placed;

    // ---- the glass's tone (item 4, the user's "A", 2026-10-19) ----------------------------------------------------------

    readonly bool _appearanceDark;
    // The parts whose glass turned from the appearance (a piece, or the whole panel), and the tone of each.
    readonly Dictionary<FrameworkElement, bool> _turned = new();

    void OnTone(object? key, bool dark)
    {
        if ((key as FrameworkElement ?? Content as FrameworkElement) is not { } e) return;
        if (dark == _appearanceDark) _turned.Remove(e); else _turned[e] = dark;
        Inks.Apply(e, dark, _appearanceDark);
        Toned(e);
    }

    /// <summary>A part's glass turned: what its code paints in the part's tone (<see cref="ToneOf"/>) is painted again.</summary>
    protected virtual void Toned(FrameworkElement part) { }

    /// <summary>The colours of the parts turned mapped again: after a layout (new rows), and after code painted a part without one.</summary>
    protected void Retone()
    {
        if (_turned.Count == 0) return;
        foreach (var (e, dark) in _turned.ToArray()) Inks.Apply(e, dark, _appearanceDark);
    }

    /// <summary>How the colours of what is on <paramref name="e"/> are mapped now (its part's glass turned), or null: as built.</summary>
    protected Inks.Mapping? ToneOf(DependencyObject e)
    {
        for (DependencyObject? o = e; o != null; o = VisualTreeHelper.GetParent(o))
            if (o is FrameworkElement f && _turned.TryGetValue(f, out bool dark)) return Inks.For(dark, _appearanceDark);
        return null;
    }

    /// <summary>Diagnostics: the glass under each part (its rectangle in the window's pixels, its tone, what is behind it) and a colour of the text on it.</summary>
    public object Tones() => _backdrop.Tones().Select(x =>
    {
        var e = x.key as FrameworkElement ?? Content as FrameworkElement;
        var piece = x.key is FrameworkElement k ? PlacedPieceOf(k) : null;
        return new
        {
            rect = piece is { } p ? new[] { p.L, p.T, p.L + p.W, p.T + p.H } : null,
            dark = x.dark,
            luma = double.IsNaN(x.luma) ? (double?)null : Math.Round(x.luma, 3),
            ink = e != null ? InkOf(e) : null,
            // What is drawn in the wrong tone on it (Inks.Strays: a row lit by the pointer on glass turned light, white).
            strays = e != null ? Inks.Strays(e, x.dark, _appearanceDark) : [],
            // Segmented controls on it: the track's and the raised part's fills (on glass turned light the raised part is white).
            segments = e != null ? Segments(e).Select(g => new { track = (g.Background as SolidColorBrush)?.Color.ToString(), raised = g.RaisedColor.ToString() }).ToArray() : [],
        };
    }).ToArray();

    static IEnumerable<MacControls.Segmented> Segments(DependencyObject e)
    {
        if (e is MacControls.Segmented g) { yield return g; yield break; }
        int n = VisualTreeHelper.GetChildrenCount(e);
        for (int i = 0; i < n; i++)
            foreach (var x in Segments(VisualTreeHelper.GetChild(e, i))) yield return x;
    }

    /// <summary>The first text's or glyph's colour on <paramref name="e"/> that is not on a fill of its own (diagnostics).</summary>
    static string? InkOf(DependencyObject e)
    {
        if (e is System.Windows.Controls.TextBlock tb && tb.Foreground is SolidColorBrush s) return s.Color.ToString();
        if (e is System.Windows.Shapes.Shape sh && (sh.Fill ?? sh.Stroke) is SolidColorBrush g && g.Color.A > 0) return g.Color.ToString(); // a glyph
        if (e is System.Windows.Controls.Border { Background: SolidColorBrush { Color.A: >= Inks.OpaqueFill } }) return null;
        int n = VisualTreeHelper.GetChildrenCount(e);
        for (int i = 0; i < n; i++)
            if (InkOf(VisualTreeHelper.GetChild(e, i)) is { } c) return c;
        return null;
    }

    /// <summary>
    /// The pieces are not placed meanwhile: the panel is between two forms of its parts (a view being put in, the
    /// glass growing into it not yet set up), and a layout pass in the middle placed the view's glass alone for a
    /// moment: the other parts' lenses went and came again (2026-10-18, lens-flicker: 8 appearing again as Wi-Fi's
    /// view grew, none with this). Placed again when it is let go.
    /// </summary>
    protected bool HoldPieces { get; set; }

    /// <summary>Diagnostics: the named layers left out of the panel's glass (WindowBackdrop.LeaveOut).</summary>
    public void LeaveOut(IReadOnlyCollection<string> off) => _backdrop.LeaveOut(off);

    /// <summary>The piece placed last for <paramref name="e"/>, or null (diagnostics).</summary>
    public GlassPiece? PlacedPieceOf(FrameworkElement e) => _placed?.FirstOrDefault(p => ReferenceEquals(p.Key, e));

    /// <summary>Places the pieces again where their parts are now (only when something changed).</summary>
    protected void PlacePieces()
    {
        if (_pieces == null || HoldPieces || PresentationSource.FromVisual(this) == null) return;
        var s = VisualTreeHelper.GetDpi(this);
        double kx = s.DpiScaleX, ky = s.DpiScaleY;
        static float Px(double v) => (float)Math.Floor(Math.Round(v, 6) + 0.5); // to a pixel, halves up
        var list = new List<GlassPiece>();
        foreach (var p in _pieces())
        {
            var e = p.Element;
            if (!e.IsVisible || e.ActualWidth <= 0 || e.ActualHeight <= 0) continue;
            Rect r;
            try { r = e.TransformToAncestor(this).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight)); }
            catch (InvalidOperationException) { continue; } // not under this window (being replaced)
            double opacity = 1;
            (float, float, float, float)? clip = null;
            for (DependencyObject? o = e; o != null && o != this; o = VisualTreeHelper.GetParent(o))
            {
                // Not the content's own: the panel's fade (Fades.FadeIn: from 0, on a clock of its own) fades the pieces
                // with the plate already (FadePlate). Counted, it left every piece out while it began, and nothing
                // placed them again after it (the Notification Center had no glass at all, 2026-10-18).
                if (o is UIElement u && !ReferenceEquals(o, Content)) opacity *= u.Opacity;
                if (o is ScrollViewer sv && o != e && clip == null)
                {
                    var v = sv.TransformToAncestor(this).TransformBounds(new Rect(0, 0, sv.ViewportWidth, sv.ViewportHeight));
                    clip = ((float)(v.Left * kx), (float)(v.Top * ky), (float)(v.Right * kx), (float)(v.Bottom * ky));
                }
            }
            if (opacity <= 0.01) continue;
            double radius = Math.Min(p.Radius, Math.Min(r.Width, r.Height) / 2);
            // Its edges rounded, then its size: the position and the size rounded each on its own put the far edges a
            // pixel in and out by turns while a piece grows and moves (a panel opening on its spring, a press's bounce:
            // 2026-10-23).
            float l = Px(r.Left * kx), t = Px(r.Top * ky);
            list.Add(new GlassPiece(l, t, Px(r.Right * kx) - l, Px(r.Bottom * ky) - t,
                (float)(radius * kx), Glass: true, Shadow: p.Shadow, Clip: clip) { Opacity = (float)opacity, Key = e });
        }
        if (_placed != null && _placed.SequenceEqual(list)) return;
        _placed = list;
        _backdrop.SetPieces(list, _pieceMaterial);
    }

    // ---- the keys (KeyNav: "D2", 2026-10-19) ---------------------------------------------------------------------------

    readonly Color _keyAccent;
    FrameworkElement? _keyed;
    KeyNav.Ring? _ring;

    /// <summary>It has parts the keys can go to: the shell takes the arrows, Tab, Space and Enter while it is open.</summary>
    public bool TakesKeys => Content is DependencyObject root && HasKeyParts(root);

    static bool HasKeyParts(DependencyObject d)
    {
        if (KeyNav.GetRun(d) != null) return true;
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++) if (HasKeyParts(VisualTreeHelper.GetChild(d, i))) return true;
        return false;
    }

    List<FrameworkElement> KeyParts() => Content is DependencyObject root ? KeyNav.Parts(root) : [];

    /// <summary>An arrow: the keys go to the nearest part that way (to the first while none is chosen).</summary>
    public void KeyMove(int dx, int dy) => KeyTo(KeyNav.Next(KeyParts(), _keyed, dx, dy, this));

    /// <summary>Tab (<paramref name="step"/> 1) or Shift+Tab (−1): the next part in order.</summary>
    public void KeyStep(int step)
    {
        var parts = KeyParts();
        if (parts.Count == 0) return;
        int i = _keyed == null ? (step > 0 ? -1 : 0) : parts.IndexOf(_keyed);
        KeyTo(parts[((i + step) % parts.Count + parts.Count) % parts.Count]);
    }

    /// <summary>Space or Enter: what the part chosen does on a click.</summary>
    public void KeyRun()
    {
        if (_keyed is not { IsVisible: true } part || KeyNav.GetRun(part) is not { } run) return;
        KeyRuns++;
        run();
        // It may have gone (a view opened in its place): the ring goes with it.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { if (_keyed is { IsVisible: false }) KeyTo(null); });
    }

    void KeyTo(FrameworkElement? part)
    {
        if (_ring != null) { AdornerLayer.GetAdornerLayer(_ring.AdornedElement)?.Remove(_ring); _ring = null; }
        _keyed = part;
        if (part == null || AdornerLayer.GetAdornerLayer(part) is not { } layer) return;
        _ring = new KeyNav.Ring(part, _keyAccent);
        layer.Add(_ring);
        KeyMoves++;
    }

    /// <summary>The part the keys are on (diagnostics), and the keys' moves and runs since the start.</summary>
    public FrameworkElement? KeyedPart => _keyed;
    public static int KeyMoves { get; private set; }
    public static int KeyRuns { get; private set; }

    public PanelPopup(ThemeSnapshot t, Material m)
    {
        _keyAccent = Color.FromRgb(t.Accent.R, t.Accent.G, t.Accent.B);
        Animate = t.Animations;
        Title = "Frostbar.Panel";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = Materials.UiFont;
        FontSize = 13;
        UseLayoutRounding = true;
        Language = XmlLanguage.GetLanguage("ja-JP");
        Text = Materials.Text(t);
        var c = ((SolidColorBrush)Text).Color;
        Dim = new SolidColorBrush(Color.FromArgb((byte)(c.A * 0.6), c.R, c.G, c.B));
        Foreground = Text;
        Content = Body;
        _backdrop = new WindowBackdrop(this, m);
        // The glass suits what is behind it, each piece on its own (item 4, the user's "A", 2026-10-19).
        _appearanceDark = t.Dark;
        _backdrop.ToneChanged += OnTone;
        _backdrop.Adapts = true;
        LayoutUpdated += (_, _) => Retone();
        PressLight.Attach(this, () => Animate && _backdrop.Glassy);
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

    protected TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };

    protected Border LinkRow(string text, Action click)
    {
        var b = new Border { Child = new TextBlock { Text = text }, Padding = new Thickness(6, 4, 6, 4), CornerRadius = new CornerRadius(GlassShape.PanelRow), Background = Brushes.Transparent, Margin = new Thickness(-6, 6, -6, 0) };
        b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => click();
        KeyNav.SetRun(b, click); // D2
        return b;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _backdrop.Dispose();
    }
}

/// <summary>
/// Battery (macOS's battery menu): the charge, the power source and the time left, the power mode of the source in use
/// (macOS's Low Power Mode, as Windows has it: three modes), and a link to the Battery page of the settings.
/// </summary>
public sealed class BatteryPanel : PanelPopup
{
    /// <summary>The power mode's choice (diagnostics: the specs click it).</summary>
    public MacControls.Segmented Mode { get; }

    public BatteryPanel(ThemeSnapshot t, Material m, Strings s, BatteryMonitor battery, Action close, Action<string>? openSettings = null) : base(t, m)
    {
        var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 2) };
        var percent = new TextBlock { Text = $"{battery.Percent}%", Foreground = Dim, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(percent, System.Windows.Controls.Dock.Right);
        head.Children.Add(percent);
        head.Children.Add(new TextBlock { Text = s["set.battery"], FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Body.Children.Add(head);
        string source = battery.OnAc ? s["battery.sourceAc"] : s["battery.sourceBattery"];
        string state = battery.Charging ? s["status.charging"]
            : PowerSettings.SecondsLeft is int left ? s.F("battery.left", Core.Devices.PowerChoices.Duration(TimeSpan.FromSeconds(left), s)) : "";
        Body.Children.Add(new TextBlock { Text = s.F("battery.source", source), Foreground = Dim, FontSize = 12 });
        if (state.Length > 0) Body.Children.Add(new TextBlock { Text = state, Foreground = Dim, FontSize = 12 });
        if (PowerSettings.EnergySaverOn) Body.Children.Add(new TextBlock { Text = s["battery.saverOn"], Foreground = Dim, FontSize = 12 });

        // The power mode of the source in use.
        Body.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(t.Dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0)), Margin = new Thickness(0, 8, 0, 8) });
        Body.Children.Add(new TextBlock { Text = s["power.mode"], FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Dim, Margin = new Thickness(0, 0, 0, 6) });
        bool ac = battery.OnAc;
        Mode = MacControls.Segments([s["power.efficiency"], s["power.balanced"], s["power.performance"]], (int)PowerSettings.GetMode(ac), t,
            i => PowerSettings.SetMode(ac, (PowerMode)i));
        Mode.HorizontalAlignment = HorizontalAlignment.Left;
        Body.Children.Add(Mode);
        Body.Children.Add(LinkRow(s["status.batterySettings"], () =>
        {
            close();
            if (openSettings != null) openSettings("battery");
            else CommandRunner.Launch("ms-settings:powersleep");
        }));
    }
}
