using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Frostbar.App.Common;
using Frostbar.Core.Dock;
using Frostbar.Platform.Dwm;
using Windows.UI.Composition;
using Color = System.Windows.Media.Color;
using ContainerVisual = Windows.UI.Composition.ContainerVisual;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Dock;

/// <summary>
/// The Dock's pictures as composition visuals in the backdrop window, above the plate (ADR-007): icons, running dots,
/// the separator, the press shade and the name label. A frame of magnification or bouncing only moves and sizes
/// visuals, which DWM scales and draws, so the WPF window above (it only takes the pointer) is not redrawn. Positions
/// come in DIPs of the Dock's window and are placed on whole pixels. Each picture is uploaded at the resting size, the
/// magnified size and half way, and shown from the smallest of those that is not smaller than it is drawn: the
/// compositor's bilinear filter then never shrinks by more than about a fifth.
/// </summary>
public sealed class DockScene : IDisposable
{
    // The name label: a dark plate with white words in the dark appearance, a light one with dark words in the light
    // (macOS's; it was the dark one in both: C7, 2026-10-19). By the theme's text colour (light words: the dark appearance).
    static readonly Color LabelFillDark = Color.FromArgb(0xD9, 0x2B, 0x2B, 0x2B), LabelRimDark = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
    static readonly Color LabelFillLight = Color.FromArgb(0xE6, 0xF4, 0xF4, 0xF6), LabelRimLight = Color.FromArgb(0x26, 0, 0, 0), LabelTextLight = Color.FromRgb(0x1D, 0x1D, 0x1F);
    bool Dark => _text.R * 0.2126 + _text.G * 0.7152 + _text.B * 0.0722 > 128;
    const byte ShadeAlpha = 0x55;
    const double DotRadius = 2;

    /// <summary>One picture at a few pixel sizes, smallest first, on the GPU device of <see cref="Generation"/>.</summary>
    sealed class Picture
    {
        public readonly int[] Sizes;
        public readonly CompositionSurfaceBrush[] Brushes;
        public readonly int Generation;
        public Picture(int[] sizes, CompositionSurfaceBrush[] brushes, int generation) { Sizes = sizes; Brushes = brushes; Generation = generation; }
        public CompositionSurfaceBrush For(float px)
        {
            for (int i = 0; i < Sizes.Length; i++) if (Sizes[i] >= px - 0.5f) return Brushes[i];
            return Brushes[^1];
        }
        public void Release() { foreach (var b in Brushes) SurfaceFactory.Release(b); }
    }

    readonly SurfaceFactory _surfaces;
    readonly Compositor _c;
    readonly ContainerVisual _root;
    readonly SpriteVisual _label, _shade;
    /// <summary>The shade's mask: the shaded item's own picture (the brush shows it, the mask brush takes its alpha).</summary>
    readonly CompositionSurfaceBrush _shadeMask;
    readonly Dictionary<ImageSource, Picture> _icons = new(ReferenceEqualityComparer.Instance);
    readonly List<(SpriteVisual picture, SpriteVisual dot)> _slots = new();
    /// <summary>What each slot shows: an icon, the app grid, the placeholder, or nothing (separator).</summary>
    readonly List<Picture?> _shows = new();
    /// <summary>The brush each slot has now (set only when it changes).</summary>
    readonly List<CompositionBrush?> _shown = new();
    IReadOnlyList<DockItem> _items = [];
    Func<DockItem, ImageSource?> _icon = _ => null;
    Picture? _grid, _placeholder, _separatorPicture;
    CompositionSurfaceBrush? _dotBrush, _labelBrush;
    CompositionColorBrush? _separatorBrush;
    Color _text = Colors.White;
    double _scale, _baseSize = 40, _maxSize = 80;
    string? _labelText;
    Size _labelSize;

    // ---- the apps' taskbar marks (macOS: a progress bar on the icon of an app downloading, a red badge at its corner) ----

    /// <summary>One app's marks as drawn: its progress (state, 0–1) and its badge (a count drawn red, or the app's own picture).</summary>
    public readonly record struct Mark(ProgressState State, double Progress, string? Count, ImageSource? Picture);

    // A running progress is in the dots' colour (the text's): Windows' accent can be red (this PC's is) and would read as
    // an error (2026-10-15); an error is red, a pause yellow.
    // The system's red and yellow of the appearance (SystemTint: the badge and an error were two reds, C7).

    /// <summary>A slot's mark visuals, made the first time its app has marks.</summary>
    sealed class Marks
    {
        public required SpriteVisual Badge;
        public required ContainerVisual Bar;
        public required ShapeVisual Track, Fill;
        public required CompositionRoundedRectangleGeometry TrackShape, FillShape;
        public required CompositionColorBrush TrackBrush, FillBrush;
        public bool Pulsing;
        /// <summary>
        /// "Done" (0–1, the share filled) and "Vertical" (1 on a side): the fill's size follows them and the track's
        /// size by an expression DWM runs, so a new progress glides there while the bar follows the magnification at
        /// once (null: the expression was refused, the fill set each frame as before).
        /// </summary>
        public CompositionPropertySet? Props;
        public float Done = -1;
    }

    /// <summary>Animations are on (the Dock's setting): the marks' and dots' small movements.</summary>
    public Func<bool> Animations { get; set; } = () => true;

    /// <summary>How long a progress glides to a new value, a badge pops in and a running dot fades in (ms).</summary>
    public const double MarkGlideMs = Motion.MarkGlide, BadgePopMs = Motion.BadgePop, DotInMs = Motion.DotIn;

    /// <summary>Marks' small movements begun (diagnostics: progress glides, badges popped, dots faded in).</summary>
    public (int Glides, int Pops, int DotsIn) MarkMoves => (_glides, _pops, _dotsIn);
    int _glides, _pops, _dotsIn;

    // What each item showed at the last frame, by key: a badge or a dot that comes pops or fades in, not one already
    // there (slots are by index; an item's place changes as others come and go).
    readonly Dictionary<string, (bool dot, bool badge)> _wasShown = new();
    bool _placedOnce;

    readonly List<Marks?> _marks = new();
    readonly Dictionary<(string text, int px, bool dark), (CompositionSurfaceBrush brush, float aspect)> _counts = new(); // by the red of the look too (a switch kept the old one)
    readonly Dictionary<ImageSource, CompositionSurfaceBrush> _overlays = new(ReferenceEqualityComparer.Instance);

    // ---- windows minimized into the Dock (stage 7-2, the user's choice A) ----
    // A minimized window shows as its own picture: its DWM thumbnail in the backdrop window, above the plate and the
    // icons (a thumbnail draws above its window's composition content: M0 S27; a minimized window's still shows it as
    // it was: S19), fitted into its slot. Its app's small icon at the picture's bottom right and the pressed shade go in
    // the window above the pair (WindowBackdrop.Above), so above the thumbnail.

    /// <summary>The small icon's size, a share of the slot.</summary>
    const double BadgeShare = 0.42;
    /// <summary>The picture's margin in its slot, a share of the slot on each side.</summary>
    const double PictureInset = 0.06;

    sealed class MinimizedPicture
    {
        public Thumbnail? Thumb;
        public (int Width, int Height) Source;
        public required SpriteVisual Badge, Shade;
        public ImageSource? BadgeImage;
        public Picture? BadgePicture;
        /// <summary>The thumbnail's rectangle as last placed (pixels in the backdrop window) and whether it shows.</summary>
        public Frostbar.Platform.Interop.RECT Shown;
        public bool Visible;
    }

    readonly IntPtr _thumbHost;
    readonly Func<ContainerVisual>? _above;
    readonly Dictionary<long, MinimizedPicture> _minimized = new();
    /// <summary>Every window a picture was made of so far (diagnostics: a spec's shell must never have made one of the user's).</summary>
    readonly HashSet<long> _everPictured = new();
    public IReadOnlyCollection<long> EverPictured => _everPictured;
    readonly Dictionary<ImageSource, Picture> _badgePictures = new(ReferenceEqualityComparer.Instance);

    /// <param name="thumbHost">The backdrop window: minimized windows' thumbnails are drawn in it (none: no pictures of them).</param>
    /// <param name="above">The visuals of the window above the pair, made when first needed (the small icons on those pictures).</param>
    public DockScene(SurfaceFactory surfaces, ContainerVisual parent, IntPtr thumbHost = default, Func<ContainerVisual>? above = null)
    {
        _surfaces = surfaces;
        _thumbHost = thumbHost;
        _above = above;
        _c = surfaces.Compositor;
        _root = _c.CreateContainerVisual();
        parent.Children.InsertAtTop(_root);
        // Pressed (or the target of a drop), the icon itself darkens, as on macOS: black over the icon's own pixels.
        // It was a rounded square over the slot, which showed the item's square bounds around a round or irregular
        // icon for a moment at every click (2026-10-14: "the hit area shows when I click a Dock icon").
        _shadeMask = _c.CreateSurfaceBrush();
        _shadeMask.Stretch = CompositionStretch.Fill;
        _shadeMask.BitmapInterpolationMode = CompositionBitmapInterpolationMode.Linear;
        var shade = _c.CreateMaskBrush();
        shade.Source = _c.CreateColorBrush(Composition.Color(ShadeAlpha, 0, 0, 0));
        shade.Mask = _shadeMask;
        _shade = _c.CreateSpriteVisual();
        _shade.Brush = shade;
        _shade.IsVisible = false;
        _label = _c.CreateSpriteVisual();
        _label.IsVisible = false;
        _root.Children.InsertAtTop(_shade);
        _root.Children.InsertAtTop(_label);
        surfaces.DeviceReplaced += Reload;
    }

    /// <summary>The theme's text colour (dots, separator, placeholder), the scale, and the icon sizes pictures are made for.</summary>
    public void SetLook(Color text, double scale, double baseSize, double maxSize)
    {
        if (text == _text && scale == _scale && baseSize == _baseSize && maxSize == _maxSize && _dotBrush != null) return;
        bool sizes = scale != _scale || baseSize != _baseSize || maxSize != _maxSize;
        _text = text;
        _scale = scale;
        _baseSize = baseSize;
        _maxSize = maxSize;
        if (sizes) ReleaseIcons();
        MakeFixedPictures();
        SetItems(_items, _icon);
    }

    int Whole(double dip) => (int)Math.Round(dip * _scale);
    // A position: halves up, as the layout rounds (a running dot under an icon of an odd size stands on a half pixel,
    // and Math.Round's halves to even moved it 0 and 2 pixels as its icon moved 1 at a time: 2026-10-23).
    float Px(double dip) => (float)Math.Floor(Math.Round(dip * _scale, 6) + 0.5);

    /// <summary>The pixel sizes a picture is uploaded at: at rest, magnified, and half way.</summary>
    int[] Sizes() => new[] { Whole(_baseSize), Whole((_baseSize + _maxSize) / 2), Whole(_maxSize) }.Distinct().Order().ToArray();

    Picture Leveled(BitmapSource image)
    {
        var sizes = Sizes();
        int generation = _surfaces.Generation; // before uploading: a loss during the upload leaves the picture stale
        return new Picture(sizes, sizes.Select(px => _surfaces.Brush(SurfaceFactory.Scaled(image, px, px))).ToArray(), generation);
    }

    Picture Leveled(Action<DrawingContext, double> draw)
    {
        var sizes = Sizes();
        int generation = _surfaces.Generation;
        return new Picture(sizes, sizes.Select(px => _surfaces.Brush(Draw(px, px, dc => draw(dc, px)))).ToArray(), generation);
    }

    /// <summary>Diagnostics: the slots showing a picture, and how many of those were made on an older GPU device than the current one.</summary>
    public (int pictures, int stale) PictureCount() =>
        (_shows.Count(p => p != null), _shows.Count(p => p != null && p.Generation != _surfaces.Generation));

    /// <summary>Diagnostics (gpu-lost upload): every picture uploaded again through the Dock's own path, as after a new device.</summary>
    public void RemakePictures() => Reload();

    void MakeFixedPictures()
    {
        DropLeaving(); // a leaving item may show the placeholder released here
        _shadeMask.Surface = null; // it may show one of the pictures released here (Update sets it again)
        _grid?.Release();
        _placeholder?.Release();
        _separatorPicture?.Release();
        if (_dotBrush != null) SurfaceFactory.Release(_dotBrush);
        int dot = Whole(2 * DotRadius) + 2;
        _dotBrush = _surfaces.Brush(Draw(dot, dot, dc =>
            dc.DrawEllipse(new SolidColorBrush(WithAlpha(_text, 0.85)), null, new Point(dot / 2.0, dot / 2.0), DotRadius * _scale, DotRadius * _scale)));
        _grid = Leveled((dc, px) => DrawAppGrid(dc, new Rect(0, 0, px, px)));
        _placeholder = Leveled((dc, px) =>
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(WithAlpha(_text, 0.3)), Math.Max(1, _scale)), Deflate(new Rect(0, 0, px, px), px * 0.08), px * 0.2, px * 0.2));
        _separatorBrush = _c.CreateColorBrush(Composition.Color((byte)(_text.A * 0.3), _text.R, _text.G, _text.B));
        // A separator being dragged: its line as tall as in the row (the drag shows pictures at the magnified size),
        // a little bolder so it still shows away from the plate.
        _separatorPicture = Leveled((dc, px) =>
        {
            double w = Math.Max(2, Math.Round(1.5 * _scale)), h = Math.Round(px * _baseSize / Math.Max(_baseSize, _maxSize));
            dc.DrawRectangle(new SolidColorBrush(WithAlpha(_text, 0.6)), null, new Rect(Math.Round((px - w) / 2), Math.Round((px - h) / 2), w, h));
        });
        _labelText = null;
    }

    void ReleaseIcons()
    {
        DropLeaving();
        _shadeMask.Surface = null;
        foreach (var p in _icons.Values) p.Release();
        _icons.Clear();
        ReleaseBadgePictures();
        ReleaseCounts(); // made for the old sizes
    }

    void ReleaseBadgePictures()
    {
        foreach (var m in _minimized.Values) { m.Badge.Brush = null; m.BadgePicture = null; }
        foreach (var p in _badgePictures.Values) p.Release();
        _badgePictures.Clear();
    }

    /// <summary>The GPU device was replaced: every picture is uploaded again.</summary>
    /// <summary>
    /// The pictures were made again on a new GPU device: the slots have none until the next <see cref="Update"/>, so the
    /// owner places them now (2026-10-15: after a lost device the Dock's icons stayed empty until the pointer came by).
    /// </summary>
    public event Action? PicturesReplaced;

    void Reload()
    {
        DropLeaving(); // their surfaces went with the old device
        _shadeMask.Surface = null;
        foreach (var m in _marks) if (m != null) m.Badge.Brush = null;
        _counts.Clear(); // these surfaces too went with the old device
        _overlays.Clear();
        _icons.Clear(); // their surfaces went with the old device
        foreach (var m in _minimized.Values) { m.Badge.Brush = null; m.BadgePicture = null; }
        _badgePictures.Clear();
        _grid = _placeholder = _separatorPicture = null;
        _dotBrush = null;
        _labels.Clear(); // their surfaces went with the old device
        _labelText = null;
        MakeFixedPictures();
        SetItems(_items, _icon);
        PicturesReplaced?.Invoke();
    }

    /// <summary>New items (or icons that have loaded since): one picture and one dot per slot.</summary>
    public void SetItems(IReadOnlyList<DockItem> items, Func<DockItem, ImageSource?> icon)
    {
        _items = items;
        _icon = icon;
        if (_dotBrush == null) return; // SetLook comes first
        while (_slots.Count < items.Count)
        {
            var picture = _c.CreateSpriteVisual();
            var dot = _c.CreateSpriteVisual();
            _root.Children.InsertAtTop(picture);
            _root.Children.InsertAtTop(dot);
            _slots.Add((picture, dot));
            _shows.Add(null);
            _shown.Add(null);
            _marks.Add(null);
        }
        while (_slots.Count > items.Count)
        {
            var (picture, dot) = _slots[^1];
            _root.Children.Remove(picture);
            _root.Children.Remove(dot);
            picture.Dispose();
            dot.Dispose();
            if (_marks[^1] is { } m) { _root.Children.Remove(m.Badge); _root.Children.Remove(m.Bar); m.Badge.Dispose(); m.Bar.Dispose(); }
            _slots.RemoveAt(_slots.Count - 1);
            _shows.RemoveAt(_shows.Count - 1);
            _shown.RemoveAt(_shown.Count - 1);
            _marks.RemoveAt(_marks.Count - 1);
        }
        foreach (var key in _leaving.Keys.Where(k => items.Any(x => x.Key == k)).ToList()) Let(key); // back before its room closed
        foreach (var key in _wasShown.Keys.Where(k => !items.Any(x => x.Key == k)).ToList()) _wasShown.Remove(key); // gone: comes anew
        var used = new HashSet<ImageSource>(ReferenceEqualityComparer.Instance);
        var made = System.Diagnostics.Stopwatch.StartNew();
        int before = _icons.Count;
        for (int i = 0; i < items.Count; i++)
        {
            var (picture, dot) = _slots[i];
            dot.Brush = _dotBrush;
            dot.IsVisible = false;
            if (items[i].Kind == DockItemKind.Separator) { _shows[i] = null; picture.Brush = _shown[i] = _separatorBrush; continue; }
            if (items[i].Kind == DockItemKind.MinimizedWindow)
            {
                // Its own picture is its thumbnail; the slot itself draws nothing under it.
                _shows[i] = null;
                picture.Brush = _shown[i] = null;
                if (items[i].Windows.Count > 0) EnsureMinimized(items[i].Windows[0].Hwnd, icon(items[i]) as BitmapSource);
                continue;
            }
            _shows[i] = items[i].Kind == DockItemKind.AppGrid ? _grid
                : icon(items[i]) is BitmapSource image ? IconPicture(image, used)
                : _placeholder;
            picture.Brush = _shown[i] = null; // set for its size in Update
        }
        if (_icons.Count > before)
            Frostbar.Platform.Infrastructure.Log.Debug($"dock scene: {_icons.Count - before} new picture(s) made and uploaded in {made.ElapsedMilliseconds} ms on the UI thread");
        // Windows no longer minimized here let their thumbnails go.
        var minimizedNow = new HashSet<long>(items.Where(x => x.Kind == DockItemKind.MinimizedWindow && x.Windows.Count > 0).Select(x => x.Windows[0].Hwnd));
        foreach (var gone in _minimized.Keys.Where(h => !minimizedNow.Contains(h)).ToList()) ForgetMinimized(gone);
        // Icons no longer shown give their surfaces back (not those of items still leaving: they shrink away first).
        foreach (var gone in _icons.Keys.Where(k => !used.Contains(k) && !_leaving.Values.Any(l => ReferenceEquals(l.Picture, _icons[k]))).ToList())
        {
            _shadeMask.Surface = null; // it may be one of them (Update sets it again)
            _icons[gone].Release();
            _icons.Remove(gone);
        }
        // The shade and the label stay above the icons.
        _root.Children.Remove(_shade);
        _root.Children.InsertAtTop(_shade);
        _root.Children.Remove(_label);
        _root.Children.InsertAtTop(_label);
    }

    // ---- items leaving the row ---------------------------------------------------------------------------------------
    // The icon of an item that went (an app that quit) shrinks away with its room as the row closes up, as the icon of
    // one that comes grows with its room. It was gone at once, and an empty room closed up for a quarter of a second
    // (2026-10-17).

    sealed record Leaving(SpriteVisual Visual, Picture Picture);
    readonly Dictionary<string, Leaving> _leaving = new();

    /// <summary>Items whose icons are still shrinking away, shown or not, and how large (pixels in the backdrop window): diagnostics.</summary>
    public IEnumerable<(string key, bool shown, float size)> LeavingState() =>
        _leaving.Select(kv => (kv.Key, kv.Value.Visual.IsVisible, kv.Value.Visual.Size.X)).ToList();

    /// <summary>Item <paramref name="i"/>'s icon is drawn in its slot (diagnostics: not while it flies there).</summary>
    public bool IconShown(int i) => i >= 0 && i < _slots.Count && _slots[i].picture.IsVisible;

    /// <summary>Item <paramref name="i"/>'s slot shows a picture now (diagnostics: shown with none, a slot draws nothing).</summary>
    public bool PictureDrawn(int i) => IconShown(i) && _slots[i].picture.Brush != null;

    /// <summary>
    /// Keeps the picture item <paramref name="index"/> of the present items shows, to draw while its room closes up:
    /// called before the new items are set (nothing for a separator or a minimized window's thumbnail).
    /// </summary>
    public void Keep(int index, string key)
    {
        if (_leaving.ContainsKey(key) || index < 0 || index >= _items.Count || index >= _shows.Count || _shows[index] is not { } picture) return;
        if (_items[index].Kind is DockItemKind.Separator or DockItemKind.MinimizedWindow) return;
        var v = _c.CreateSpriteVisual();
        v.IsVisible = false;
        _root.Children.InsertAtBottom(v);
        _leaving[key] = new Leaving(v, picture);
    }

    /// <summary>The room of <paramref name="key"/> has closed up (or it came back): its picture goes.</summary>
    public void Let(string key)
    {
        if (!_leaving.Remove(key, out var l)) return;
        _root.Children.Remove(l.Visual);
        l.Visual.Dispose();
        // Its surfaces too, unless shown again meanwhile (an icon only: the app grid's and the placeholder stay).
        foreach (var (image, p) in _icons.Where(kv => ReferenceEquals(kv.Value, l.Picture)).ToList())
            if (!_shows.Any(s => ReferenceEquals(s, p)) && !_leaving.Values.Any(o => ReferenceEquals(o.Picture, p)))
            {
                _shadeMask.Surface = null; // it may be one of them (Update sets it again)
                p.Release();
                _icons.Remove(image);
            }
    }

    /// <summary>Each leaving picture where its room is, as large as the room's icon (it shrinks as the room closes).</summary>
    void PlaceLeaving(DockLayout layout, DockFrame frame, double iconNear, IReadOnlyList<(string key, int slot)>? leaving)
    {
        foreach (var l in _leaving.Values) l.Visual.IsVisible = false;
        if (leaving == null) return;
        foreach (var (key, slot) in leaving)
        {
            if (!_leaving.TryGetValue(key, out var l) || slot < 0 || slot >= layout.Slots.Count) continue;
            var s = layout.Slots[slot];
            if (s.Size < 1 / _scale) continue;
            var size = Place(l.Visual, frame.Box(s.Left, iconNear, s.Size, s.Size));
            var brush = l.Picture.For(Math.Max(size.X, size.Y));
            if (!ReferenceEquals(l.Visual.Brush, brush)) l.Visual.Brush = brush;
            l.Visual.IsVisible = true;
        }
    }

    /// <summary>Every leaving picture goes at once, its surfaces left to the caller (released with all the icons, or lost with the device).</summary>
    void DropLeaving()
    {
        foreach (var l in _leaving.Values) { _root.Children.Remove(l.Visual); l.Visual.Dispose(); }
        _leaving.Clear();
    }

    Picture IconPicture(BitmapSource image, HashSet<ImageSource> used)
    {
        used.Add(image);
        if (!_icons.TryGetValue(image, out var p)) _icons[image] = p = Leveled(image);
        return p;
    }

    /// <summary>
    /// Places everything for this frame. <paramref name="slotOf"/> maps each item to its slot in
    /// <paramref name="layout"/> (null: item i is slot i; -1: not shown, as while it is dragged). <paramref name="frame"/>
    /// turns places along the row and in from the screen's edge into the window's (stage 7: the bottom or a side);
    /// the icons stand <paramref name="iconNear"/> in, the plate's near side is <paramref name="plateNear"/> in.
    /// <paramref name="lift"/> gives each item's bounce height, <paramref name="hover"/> the item whose name shows,
    /// <paramref name="shaded"/> the item drawn darker (pressed, or the target of a drop), or -1. <paramref name="hidden"/>
    /// is an item whose icon is drawn elsewhere for now (flying into its slot after a drag; its dot stays);
    /// <paramref name="leaving"/> the rooms of items gone, closing up, by slot: their kept pictures shrink with them.
    /// </summary>
    /// <param name="give">Each item's scale about its middle (a press giving and springing back: 2026-10-23), or null: all 1.</param>
    /// <param name="shadeAlpha">How dark <paramref name="shaded"/> is drawn, of its full shade (coming in, going).</param>
    public void Update(DockLayout layout, IReadOnlyList<int>? slotOf, DockFrame frame, double iconNear, double plateNear,
        bool dots, Func<int, double> lift, int hover, int shaded, Func<int, Mark?>? marks = null,
        int hidden = -1, IReadOnlyList<(string key, int slot)>? leaving = null, Func<int, double>? give = null, double shadeAlpha = 1)
    {
        PlaceLeaving(layout, frame, iconNear, leaving);
        int n = Math.Min(_items.Count, _slots.Count);
        int SlotOf(int i) => slotOf == null ? i : i < slotOf.Count ? slotOf[i] : -1;
        // In the plate between the icon and the screen's edge: 2.5 DIP in from the plate's edge, nearer with a narrow
        // padding (it covered the icon).
        double below = Math.Min(2.5, (iconNear - plateNear) / 2), dotIn = plateNear + below;
        for (int i = 0; i < n; i++)
        {
            var (picture, dot) = _slots[i];
            int si = SlotOf(i);
            if (si < 0 || si >= layout.Slots.Count) { picture.IsVisible = false; dot.IsVisible = false; HideMarks(i); HideMinimized(i); continue; }
            picture.IsVisible = true;
            var s = layout.Slots[si];
            if (_items[i].Kind == DockItemKind.MinimizedWindow)
            {
                // No dot (its app's icon has it) and no marks: the window's own picture, fitted into the slot.
                picture.IsVisible = false;
                dot.IsVisible = false;
                HideMarks(i);
                PlaceMinimized(i, frame.Box(s.Left, iconNear, s.Size, s.Size), pressed: shaded == i);
                continue;
            }
            if (s.Kind == SlotKind.Separator)
            {
                // As long across the row as the icons at rest, whatever the plate's padding (it was the plate less 10 DIP
                // at each end: longer than the icons at a wide padding, shorter at a narrow one).
                Place(picture, frame.Box(DockGeometry.Place(s.Center, _scale), iconNear, 1 / _scale, _baseSize));
                dot.IsVisible = false;
                HideMarks(i);
                continue;
            }
            var icon = frame.Box(s.Left, iconNear + lift(i), s.Size, s.Size);
            if (give?.Invoke(i) is double k && k != 1) icon = icon.Scaled(k);
            var size = Place(picture, icon);
            picture.IsVisible = i != hidden;
            var mark = marks?.Invoke(i);
            if (mark is { } mk) ShowMarks(i, mk, s, icon, frame, dotIn);
            else HideMarks(i);
            if (_shows[i] is { } p)
            {
                var brush = p.For(Math.Max(size.X, size.Y));
                if (!ReferenceEquals(_shown[i], brush)) picture.Brush = _shown[i] = brush;
            }
            // A progress stands where the dot is, in its place (it says the app runs as well).
            _visited.Add(_items[i].Key);
            bool showDot = dots && _items[i].Running && mark is not { State: not ProgressState.None };
            dot.IsVisible = showDot;
            if (showDot)
            {
                // Come (an app started, a progress over): it fades in (it was there at once: 2026-10-19, "B7").
                string key = _items[i].Key;
                if (!(_wasShown.TryGetValue(key, out var was) && was.dot) && _placedOnce && Animations()) FadeIn(dot);
                _dotsNow.Add(key);
                float d = Whole(2 * DotRadius) + 2;
                var (cx, cy) = frame.Point(s.Middle, dotIn); // the unrounded middle: it moves only one way as the row does
                dot.Offset = new Vector3(Px(cx) - (float)Math.Round(d / 2), Px(cy) - (float)Math.Round(d / 2), 0);
                dot.Size = new Vector2(d, d);
            }
        }
        int shadedSlot = shaded >= 0 && shaded < n ? SlotOf(shaded) : -1;
        if (shadedSlot >= 0 && shadedSlot < layout.Slots.Count && layout.Slots[shadedSlot].Kind == SlotKind.Icon
            && _shown[shaded] is CompositionSurfaceBrush itsPicture)
        {
            // Where the icon is and as large, masked by the picture it shows at this size.
            var s = layout.Slots[shadedSlot];
            var box = frame.Box(s.Left, iconNear + lift(shaded), s.Size, s.Size);
            if (give?.Invoke(shaded) is double k && k != 1) box = box.Scaled(k); // with its icon, as one box
            Place(_shade, box);
            if (!ReferenceEquals(_shadeMask.Surface, itsPicture.Surface)) _shadeMask.Surface = itsPicture.Surface;
            _shade.Opacity = (float)Math.Clamp(shadeAlpha, 0, 1);
            _shade.IsVisible = shadeAlpha > 0.004;
            ShadeShown = _shade.IsVisible ? (float)shadeAlpha : 0;
        }
        else { _shade.IsVisible = false; ShadeShown = 0; }

        // The name stays while its icon is pressed (macOS): hiding it then made it blink at every click (2026-10-14).
        // While something is dragged in there is no hovered item, so no name.
        int hoverSlot = hover >= 0 && hover < n ? SlotOf(hover) : -1;
        if (hoverSlot >= 0 && hoverSlot < layout.Slots.Count && _items[hover].Kind != DockItemKind.Separator)
        {
            ShowLabel(_items[hover].Name, frame.Vertical ? DockSurface.SideLabelWidth : double.PositiveInfinity);
            var s = layout.Slots[hoverSlot];
            // Past the icon at rest, away from the screen's edge (above it at the bottom, beside it on a side): the name
            // does not bounce along (macOS). It is drawn upright on every edge.
            double length = frame.Vertical ? _labelSize.Height : _labelSize.Width, depth = frame.Vertical ? _labelSize.Width : _labelSize.Height;
            double along = Math.Clamp(s.Middle - length / 2, 2, Math.Max(2, frame.Length - length - 2));
            double inward = Math.Min(iconNear + s.Size + DockSurface.LabelGap, Math.Max(0, frame.Depth - depth));
            var box = frame.Box(along, inward, length, depth);
            _label.Offset = new Vector3(Px(box.X), Px(box.Y), 0);
            _label.IsVisible = true;
            LabelBox = new System.Windows.Rect(box.X, box.Y, box.Width, box.Height);
        }
        else { _label.IsVisible = false; LabelBox = null; }

        // What each item placed showed (one not placed now, dragged or minimized, keeps what it had).
        foreach (var key in _visited) _wasShown[key] = (_dotsNow.Contains(key), _badgesNow.Contains(key));
        _visited.Clear();
        _dotsNow.Clear();
        _badgesNow.Clear();
        _placedOnce = true;
    }

    readonly HashSet<string> _visited = new();

    /// <summary>How dark the shade over a pressed icon is drawn now, of its full darkness (diagnostics).</summary>
    public float ShadeShown { get; private set; }

    /// <summary>Where the hovered item's name is drawn (DIPs in the window), or null while none is (diagnostics).</summary>
    public System.Windows.Rect? LabelBox { get; private set; }

    /// <summary>The pixel sizes the small icons on minimized windows' pictures are uploaded at: at rest, magnified, half way.</summary>
    int[] BadgeSizes() => new[] { Whole(_baseSize * BadgeShare), Whole((_baseSize + _maxSize) / 2 * BadgeShare), Whole(_maxSize * BadgeShare) }.Distinct().Order().ToArray();

    /// <summary>A minimized window's picture: its thumbnail registered in the backdrop window, its small icon and shade above.</summary>
    void EnsureMinimized(long hwnd, BitmapSource? icon)
    {
        if (!_minimized.TryGetValue(hwnd, out var m))
        {
            if (_thumbHost == IntPtr.Zero || _above == null) return;
            var top = _above();
            var shade = _c.CreateSpriteVisual();
            shade.Brush = _c.CreateColorBrush(Composition.Color(ShadeAlpha, 0, 0, 0));
            var badge = _c.CreateSpriteVisual();
            shade.IsVisible = badge.IsVisible = false;
            top.Children.InsertAtTop(shade);
            top.Children.InsertAtTop(badge);
            var thumb = Thumbnail.CreateWhole(_thumbHost, new IntPtr(hwnd));
            _minimized[hwnd] = m = new MinimizedPicture { Thumb = thumb, Source = thumb?.SourceSize ?? (0, 0), Badge = badge, Shade = shade };
            if (_everPictured.Count < 512) _everPictured.Add(hwnd);
        }
        if (m.Source == (0, 0) && m.Thumb != null) m.Source = m.Thumb.SourceSize; // asked again: it can come late
        if (icon != null && (!ReferenceEquals(m.BadgeImage, icon) || m.BadgePicture == null || m.BadgePicture.Generation != _surfaces.Generation))
        {
            if (!_badgePictures.TryGetValue(icon, out var p) || p.Generation != _surfaces.Generation)
            {
                int generation = _surfaces.Generation;
                var sizes = BadgeSizes();
                _badgePictures[icon] = p = new Picture(sizes, sizes.Select(px => _surfaces.Brush(SurfaceFactory.Scaled(icon, px, px))).ToArray(), generation);
            }
            m.BadgeImage = icon;
            m.BadgePicture = p;
            m.Badge.Brush = null; // set for its size in Update
        }
        // Small icons no window shows any more give their surfaces back.
        foreach (var gone in _badgePictures.Keys.Where(k => !_minimized.Values.Any(x => ReferenceEquals(x.BadgeImage, k))).ToList())
        {
            _badgePictures[gone].Release();
            _badgePictures.Remove(gone);
        }
    }

    /// <summary>
    /// Places item <paramref name="i"/>'s picture in its slot (<paramref name="slot"/>, DIPs in the window): the
    /// thumbnail fitted in with a small margin, kept to the window's proportions (only when it moved: each update is a
    /// call to DWM); the small icon standing out of its bottom right corner; the shade over it while it is pressed.
    /// </summary>
    void PlaceMinimized(int i, Frostbar.Core.AppGrid.Box slot, bool pressed)
    {
        if (_items[i].Windows.Count == 0 || !_minimized.TryGetValue(_items[i].Windows[0].Hwnd, out var m)) return;
        double inset = slot.Width * PictureInset, w = slot.Width - 2 * inset, h = slot.Height - 2 * inset;
        double fw = w, fh = h;
        if (m.Source is { Width: > 0, Height: > 0 } src)
        {
            double k = Math.Min(w / src.Width, h / src.Height);
            fw = src.Width * k;
            fh = src.Height * k;
        }
        double fx = slot.X + (slot.Width - fw) / 2, fy = slot.Y + (slot.Height - fh) / 2;
        var rect = new Frostbar.Platform.Interop.RECT((int)Px(fx), (int)Px(fy), (int)Px(fx + fw), (int)Px(fy + fh));
        bool show = rect.Width >= 2 && rect.Height >= 2;
        if (m.Thumb != null && (show != m.Visible || show && !SameRect(rect, m.Shown)))
        {
            if (show) m.Thumb.Show(rect);
            else m.Thumb.Hide();
            m.Visible = show;
        }
        m.Shown = rect;
        double b = slot.Width * BadgeShare;
        var badge = Place(m.Badge, fx + fw - b * 0.8, fy + fh - b * 0.8, b, b);
        if (m.BadgePicture is { } p)
        {
            var brush = p.For(Math.Max(badge.X, badge.Y));
            if (!ReferenceEquals(m.Badge.Brush, brush)) m.Badge.Brush = brush;
        }
        m.Badge.IsVisible = show && m.Badge.Brush != null;
        m.Shade.IsVisible = pressed && show;
        if (m.Shade.IsVisible)
        {
            m.Shade.Offset = new Vector3(rect.Left, rect.Top, 0);
            m.Shade.Size = new Vector2(rect.Width, rect.Height);
        }
    }

    static bool SameRect(Frostbar.Platform.Interop.RECT a, Frostbar.Platform.Interop.RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    /// <summary>Item <paramref name="i"/>'s picture is not in the row now (dragged, or gone): nothing of it shows.</summary>
    void HideMinimized(int i)
    {
        if (i >= _items.Count || _items[i].Kind != DockItemKind.MinimizedWindow || _items[i].Windows.Count == 0) return;
        if (!_minimized.TryGetValue(_items[i].Windows[0].Hwnd, out var m)) return;
        if (m.Visible) { m.Thumb?.Hide(); m.Visible = false; }
        m.Badge.IsVisible = m.Shade.IsVisible = false;
    }

    void ForgetMinimized(long hwnd)
    {
        if (!_minimized.Remove(hwnd, out var m)) return;
        m.Thumb?.Dispose();
        if (_above != null)
        {
            var top = _above();
            top.Children.Remove(m.Badge);
            top.Children.Remove(m.Shade);
        }
        m.Badge.Dispose();
        m.Shade.Dispose();
    }

    /// <summary>
    /// Diagnostics: each minimized window's picture: its thumbnail's rectangle (pixels in the window), whether it shows,
    /// the size of its source, and its small icon's rectangle (pixels).
    /// </summary>
    public IReadOnlyList<(long Hwnd, Frostbar.Platform.Interop.RECT Picture, bool Shows, (int Width, int Height) Source, Frostbar.Platform.Interop.RECT Badge, bool BadgeShows, bool Registered)> MinimizedState() =>
        [.. _minimized.Select(kv => (kv.Key, kv.Value.Shown, kv.Value.Visible, kv.Value.Source,
            new Frostbar.Platform.Interop.RECT((int)kv.Value.Badge.Offset.X, (int)kv.Value.Badge.Offset.Y, (int)(kv.Value.Badge.Offset.X + kv.Value.Badge.Size.X), (int)(kv.Value.Badge.Offset.Y + kv.Value.Badge.Size.Y)),
            kv.Value.Badge.IsVisible, kv.Value.Thumb != null))];

    void HideMarks(int i)
    {
        if (i >= _marks.Count || _marks[i] is not { } m) return;
        m.Badge.IsVisible = false;
        m.Bar.IsVisible = false;
    }

    Marks EnsureMarks(int i)
    {
        if (_marks[i] is { } made) return made;
        var trackShape = _c.CreateRoundedRectangleGeometry();
        var fillShape = _c.CreateRoundedRectangleGeometry();
        var fillBrush = _c.CreateColorBrush(Composition.Color(_text.A, _text.R, _text.G, _text.B));
        var trackBrush = _c.CreateColorBrush(Composition.Color((byte)(_text.A * 0.22), _text.R, _text.G, _text.B));
        var track = _c.CreateShapeVisual();
        var trackSprite = _c.CreateSpriteShape(trackShape);
        trackSprite.FillBrush = trackBrush;
        track.Shapes.Add(trackSprite);
        var fill = _c.CreateShapeVisual();
        var fillSprite = _c.CreateSpriteShape(fillShape);
        fillSprite.FillBrush = fillBrush;
        fill.Shapes.Add(fillSprite);
        var bar = _c.CreateContainerVisual();
        bar.Children.InsertAtTop(track);
        bar.Children.InsertAtTop(fill);
        var badge = _c.CreateSpriteVisual();
        bar.IsVisible = badge.IsVisible = false;
        _root.Children.InsertAtTop(bar);
        _root.Children.InsertAtTop(badge);
        // The shade and the name stay above.
        _root.Children.Remove(_shade);
        _root.Children.InsertAtTop(_shade);
        _root.Children.Remove(_label);
        _root.Children.InsertAtTop(_label);
        var marks = new Marks { Badge = badge, Bar = bar, Track = track, Fill = fill, TrackShape = trackShape, FillShape = fillShape, TrackBrush = trackBrush, FillBrush = fillBrush };
        BindFill(marks);
        return (_marks[i] = marks)!;
    }

    /// <summary>
    /// The fill's size from the track's and the share done (Marks.Props): along the row, at least as long as it is thick
    /// (a round end). DWM works it out each frame, so "Done" can glide while the track follows the magnification.
    /// </summary>
    void BindFill(Marks m)
    {
        try
        {
            var props = _c.CreatePropertySet();
            props.InsertScalar("Done", 0f);
            props.InsertScalar("Vertical", 0f);
            const string size = "Vector2(p.Vertical * t.Size.X + (1 - p.Vertical) * Max(t.Size.Y, Round(t.Size.X * p.Done)), " +
                "p.Vertical * Max(t.Size.X, Round(t.Size.Y * p.Done)) + (1 - p.Vertical) * t.Size.Y)";
            foreach (var target in new CompositionObject[] { m.Fill, m.FillShape })
            {
                var e = _c.CreateExpressionAnimation(size);
                e.SetReferenceParameter("t", m.Track);
                e.SetReferenceParameter("p", props);
                target.StartAnimation("Size", e);
            }
            m.Props = props;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Frostbar.Platform.Infrastructure.Log.Warn("dock: the progress fill's expression was refused, set each frame: " + ex.Message);
            m.Fill.StopAnimation("Size");
            m.FillShape.StopAnimation("Size");
            m.Props = null;
        }
    }

    /// <summary>A badge come: from small, a little past full size and back (macOS's badges pop), as it fades in.</summary>
    void Pop(SpriteVisual badge)
    {
        var ease = _c.CreateCubicBezierEasingFunction(new Vector2(0.215f, 0.61f), new Vector2(0.355f, 1f));
        var scale = _c.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3(0.3f, 0.3f, 1));
        scale.InsertKeyFrame(0.55f, new Vector3(1.15f, 1.15f, 1), ease);
        scale.InsertKeyFrame(0.8f, new Vector3(0.96f, 0.96f, 1), ease);
        scale.InsertKeyFrame(1f, Vector3.One, ease);
        scale.Duration = TimeSpan.FromMilliseconds(BadgePopMs);
        var fade = _c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(0.4f, 1f);
        fade.InsertKeyFrame(1f, 1f);
        fade.Duration = TimeSpan.FromMilliseconds(BadgePopMs);
        if (!ReduceMotion.Calms("badgePop")) badge.StartAnimation("Scale", scale); // reduced motion: it fades in at its size
        badge.StartAnimation("Opacity", fade);
        _pops++;
    }

    /// <summary>A running dot come (an app started): it fades in.</summary>
    void FadeIn(SpriteVisual dot)
    {
        var fade = _c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, _c.CreateCubicBezierEasingFunction(new Vector2(0.215f, 0.61f), new Vector2(0.355f, 1f)));
        fade.Duration = TimeSpan.FromMilliseconds(DotInMs);
        dot.StartAnimation("Opacity", fade);
        _dotsIn++;
    }

    /// <summary>
    /// A slot's marks: the progress as a thin rounded bar where the running dot is, between the icon and the screen's
    /// edge and as thick as the dot (the user, 2026-10-15: "the same height as the one below"; it stood over the icon's
    /// bottom first), along the row, in the dot's colour on a faint track (an error red, a pause yellow, one with no
    /// measure full and pulsing); and the badge standing out of the icon's top right corner (<paramref name="icon"/>, the
    /// icon's box in the window), a count drawn as macOS's red badge or else the app's own overlay picture.
    /// <paramref name="dotIn"/> is how far in from the screen's edge the dot's centre is (DIPs).
    /// </summary>
    void ShowMarks(int i, Mark mark, Slot s, Frostbar.Core.AppGrid.Box icon, DockFrame frame, double dotIn)
    {
        var m = EnsureMarks(i);
        double size = s.Size;
        if (mark.State != ProgressState.None)
        {
            // As long as whole pixels, from the icon's unrounded middle: its ends rounded on their own made it a pixel
            // longer and shorter by turns as the row moved (2026-10-23).
            double length = Whole(size * 0.7) / _scale, thick = 2 * DotRadius;
            var box = frame.Box(DockGeometry.Place(s.Middle - length / 2, _scale), dotIn - thick / 2, length, thick);
            float px = Px(box.X), py = Px(box.Y);
            var bar = new Vector2(Math.Max(2, Px(box.Right) - px), Math.Max(2, Px(box.Bottom) - py));
            m.Bar.Offset = new Vector3(px, py, 0);
            m.Bar.Size = bar;
            float across = Math.Min(bar.X, bar.Y);
            var radius = new Vector2(across / 2, across / 2);
            m.Track.Size = bar;
            m.TrackShape.Size = bar;
            m.TrackShape.CornerRadius = radius;
            bool unmeasured = mark.State == ProgressState.Indeterminate;
            // It fills along the row: rightwards at the bottom, downwards on a side.
            float done = (float)Math.Clamp(mark.Progress, 0, 1);
            if (m.Props is { } props)
            {
                // The share filled glides to a new value (it jumped at each step: 2026-10-19, the user's "B7"); shown
                // anew (the bar was hidden) or with animations off, it is there at once.
                float target = unmeasured ? 1f : done;
                props.InsertScalar("Vertical", frame.Vertical ? 1f : 0f);
                if (target != m.Done)
                {
                    if (m.Bar.IsVisible && m.Done >= 0 && Animations())
                    {
                        var glide = _c.CreateScalarKeyFrameAnimation();
                        glide.InsertExpressionKeyFrame(0f, "this.StartingValue");
                        glide.InsertKeyFrame(1f, target, _c.CreateCubicBezierEasingFunction(new Vector2(0.215f, 0.61f), new Vector2(0.355f, 1f)));
                        glide.Duration = TimeSpan.FromMilliseconds(MarkGlideMs);
                        props.StartAnimation("Done", glide);
                        _glides++;
                    }
                    else
                    {
                        props.StopAnimation("Done");
                        props.InsertScalar("Done", target);
                    }
                    m.Done = target;
                }
            }
            else
            {
                var fill = frame.Vertical
                    ? new Vector2(bar.X, unmeasured ? bar.Y : Math.Max(bar.X, (float)Math.Round(bar.Y * done)))
                    : new Vector2(unmeasured ? bar.X : Math.Max(bar.Y, (float)Math.Round(bar.X * done)), bar.Y);
                m.Fill.Size = fill;
                m.FillShape.Size = fill;
            }
            m.FillShape.CornerRadius = radius;
            var c = mark.State switch { ProgressState.Error => SystemTint.Red(Dark), ProgressState.Paused => SystemTint.Yellow(Dark), _ => WithAlpha(_text, 0.85) };
            m.FillBrush.Color = Composition.Color(c.A, c.R, c.G, c.B);
            m.TrackBrush.Color = Composition.Color((byte)(_text.A * 0.22), _text.R, _text.G, _text.B);
            if (unmeasured && !m.Pulsing)
            {
                var pulse = _c.CreateScalarKeyFrameAnimation();
                pulse.InsertKeyFrame(0f, 0.3f);
                pulse.InsertKeyFrame(0.5f, 1f);
                pulse.InsertKeyFrame(1f, 0.3f);
                pulse.Duration = TimeSpan.FromSeconds(1.4);
                pulse.IterationBehavior = AnimationIterationBehavior.Forever;
                m.Fill.StartAnimation("Opacity", pulse); // DWM runs it: nothing wakes here
                m.Pulsing = true;
            }
            else if (!unmeasured && m.Pulsing)
            {
                m.Fill.StopAnimation("Opacity");
                m.Fill.Opacity = 1;
                m.Pulsing = false;
            }
            m.Bar.IsVisible = true;
        }
        else m.Bar.IsVisible = false;

        // As tall as whole pixels (it was whole DIPs: 1.25 px steps at 125%, each pulling it back as its icon grew the
        // other way: a shake as the Dock magnified, 2026-10-23), at the corner of the icon's unrounded box.
        double bh = Whole(size * 0.42) / _scale;
        (CompositionSurfaceBrush brush, float aspect)? badge = mark.Count != null ? CountPicture(mark.Count, Whole(bh))
            : mark.Picture is BitmapSource overlay ? (OverlayPicture(overlay), 1f) : null;
        if (badge is { } b)
        {
            double bw = Whole(bh * b.aspect) / _scale, off = s.Middle - s.Center;
            var exact = frame.Vertical ? icon with { Y = icon.Y + off } : icon with { X = icon.X + off };
            Place(m.Badge, exact.Right - bw * 0.8, exact.Y - bh * 0.2, bw, bh);
            m.Badge.CenterPoint = new Vector3(m.Badge.Size.X / 2, m.Badge.Size.Y / 2, 0); // it pops about its middle
            if (!ReferenceEquals(m.Badge.Brush, b.brush)) m.Badge.Brush = b.brush;
            // Come (not there at the last frame): it pops in (it was there at once: 2026-10-19, the user's "B7").
            string key = _items[i].Key;
            if (!(_wasShown.TryGetValue(key, out var was) && was.badge) && _placedOnce && Animations()) Pop(m.Badge);
            _badgesNow.Add(key);
            m.Badge.IsVisible = true;
        }
        else m.Badge.IsVisible = false;
    }

    readonly HashSet<string> _dotsNow = new(), _badgesNow = new();

    /// <summary>
    /// A count as macOS's badge: white on a red pill, made at the size of the level just above <paramref name="px"/>
    /// (as the icons: at rest, magnified, half way), so a magnifying Dock does not draw one each frame.
    /// </summary>
    (CompositionSurfaceBrush brush, float aspect) CountPicture(string text, int px)
    {
        int level = Sizes().Select(s => (int)Math.Round(s * 0.42)).Where(s => s >= px - 1).DefaultIfEmpty(px).Min();
        if (_counts.TryGetValue((text, level, Dark), out var made)) return made;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Materials.UiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), level * 0.62, Brushes.White, 1.0);
        int w = (int)Math.Max(level, Math.Ceiling(ft.Width + level * 0.6));
        var image = Draw(w, level, dc =>
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(SystemTint.Red(Dark)), null, new Rect(0, 0, w, level), level / 2.0, level / 2.0);
            dc.DrawText(ft, new Point((w - ft.Width) / 2, (level - ft.Height) / 2));
        });
        return _counts[(text, level, Dark)] = (_surfaces.Brush(image), w / (float)level);
    }

    /// <summary>An app's own overlay picture (uploaded once).</summary>
    CompositionSurfaceBrush OverlayPicture(BitmapSource overlay)
    {
        if (!_overlays.TryGetValue(overlay, out var brush)) _overlays[overlay] = brush = _surfaces.Brush(overlay);
        return brush;
    }

    /// <summary>Gives back the overlay pictures no longer among <paramref name="shown"/>, and the counts if many are kept.</summary>
    public void ForgetMarks(IEnumerable<ImageSource> shown)
    {
        var keep = new HashSet<ImageSource>(shown, ReferenceEqualityComparer.Instance);
        foreach (var gone in _overlays.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            foreach (var m in _marks) if (m != null && _overlays[gone] is var b && ReferenceEquals(m.Badge.Brush, b)) m.Badge.Brush = null;
            SurfaceFactory.Release(_overlays[gone]);
            _overlays.Remove(gone);
        }
        if (_counts.Count > 48) ReleaseCounts();
    }

    void ReleaseCounts()
    {
        foreach (var m in _marks) if (m != null) m.Badge.Brush = null;
        foreach (var (brush, _) in _counts.Values) SurfaceFactory.Release(brush);
        _counts.Clear();
    }

    /// <summary>Item <paramref name="item"/>'s picture for a size of <paramref name="px"/> pixels (a dragged icon).</summary>
    public CompositionSurfaceBrush? PictureFor(int item, float px) =>
        item < 0 || item >= _shows.Count || item >= _items.Count ? null
        : _items[item].Kind == DockItemKind.Separator ? _separatorPicture?.For(px)
        : _shows[item]?.For(px);

    /// <summary>A name label like the hovered item's, for other windows (the caller releases it).</summary>
    public CompositionSurfaceBrush LabelPicture(string text, out Vector2 sizePx)
    {
        var image = LabelImage(text, out _);
        sizePx = new Vector2(image.PixelWidth, image.PixelHeight);
        return _surfaces.Brush(image);
    }

    /// <summary>Puts a visual on whole pixels; returns its size in pixels.</summary>
    Vector2 Place(SpriteVisual v, double x, double y, double w, double h)
    {
        float px = Px(x), py = Px(y);
        var size = new Vector2(Math.Max(1, Px(x + w) - px), Math.Max(1, Px(y + h) - py));
        v.Offset = new Vector3(px, py, 0);
        v.Size = size;
        return size;
    }

    Vector2 Place(SpriteVisual v, Frostbar.Core.AppGrid.Box b) => Place(v, b.X, b.Y, b.Width, b.Height);

    double _labelMax = double.PositiveInfinity;
    bool _labelDark;
    // The names' plates made so far, kept while the scene's look stays: a sweep along the Dock drew each name again in
    // software and uploaded it at every change of the hovered item. Same text, width and appearance, same picture.
    readonly Dictionary<(string text, double max, bool dark), (CompositionSurfaceBrush brush, Size size, Vector2 px)> _labels = new();
    const int LabelsKept = 64;

    /// <summary>The hovered item's name (on a side of the screen no wider than <paramref name="maxWidth"/>, ending in "…").</summary>
    void ShowLabel(string text, double maxWidth)
    {
        if (text == _labelText && maxWidth == _labelMax && Dark == _labelDark) return;
        var key = (text, maxWidth, Dark);
        if (!_labels.TryGetValue(key, out var made))
        {
            if (_labels.Count >= LabelsKept) ForgetLabels();
            var image = LabelImage(text, out var size, maxWidth);
            made = (_surfaces.Brush(image), size, new Vector2(image.PixelWidth, image.PixelHeight));
            _labels[key] = made;
        }
        _labelDark = Dark;
        _labelText = text;
        _labelMax = maxWidth;
        _labelSize = made.size;
        _labelBrush = made.brush;
        _label.Brush = made.brush;
        _label.Size = made.px;
    }

    /// <summary>Lets go of the names' plates (the look or the device changes, or too many were kept).</summary>
    void ForgetLabels()
    {
        _label.Brush = null;
        _labelBrush = null;
        _labelText = null;
        foreach (var l in _labels.Values) SurfaceFactory.Release(l.brush);
        _labels.Clear();
    }

    /// <summary>The name label: 13 DIP text on a dark rounded plate with a light rim; <paramref name="size"/> in DIPs.</summary>
    BitmapSource LabelImage(string text, out Size size, double maxWidth = double.PositiveInfinity)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Materials.UiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 13, Dark ? Brushes.White : SystemTint.Brush(LabelTextLight), _scale);
        bool trimmed = ft.Width + 20 > maxWidth;
        if (trimmed)
        {
            ft.MaxTextWidth = Math.Max(1, maxWidth - 20);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        // MaxTextWidth is 0 (no limit) unless set: taken as the limit, every name's plate came out 20 DIP wide.
        double w = Math.Ceiling((trimmed ? Math.Min(ft.Width, ft.MaxTextWidth) : ft.Width) + 20), h = Math.Ceiling(ft.Height + 8);
        size = new Size(w, h);
        return Draw(Whole(w), Whole(h), dc =>
        {
            dc.PushTransform(new ScaleTransform(_scale, _scale));
            dc.DrawRoundedRectangle(new SolidColorBrush(Dark ? LabelFillDark : LabelFillLight), new Pen(new SolidColorBrush(Dark ? LabelRimDark : LabelRimLight), 1), new Rect(0.5, 0.5, w - 1, h - 1), 6, 6);
            dc.DrawText(ft, new Point(10, 4));
            dc.Pop();
        });
    }

    /// <summary>A picture drawn by WPF into a bitmap of <paramref name="w"/> × <paramref name="h"/> pixels.</summary>
    static BitmapSource Draw(int w, int h, Action<DrawingContext> draw)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) draw(dc);
        var rtb = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    static Color WithAlpha(Color c, double k) => Color.FromArgb((byte)(c.A * k), c.R, c.G, c.B);
    static Rect Deflate(Rect r, double by) => new(r.X + by, r.Y + by, Math.Max(0, r.Width - 2 * by), Math.Max(0, r.Height - 2 * by));

    /// <summary>The app grid's icon: our own drawing (a 3×3 grid on a rounded tile), no borrowed artwork.</summary>
    static void DrawAppGrid(DrawingContext dc, Rect r)
    {
        var tile = Deflate(r, r.Width * 0.06);
        dc.DrawRoundedRectangle(GridTile, null, tile, tile.Width * 0.22, tile.Width * 0.22);
        double cell = tile.Width * 0.16, gap = tile.Width * 0.09;
        double start = tile.X + (tile.Width - 3 * cell - 2 * gap) / 2, top = tile.Y + (tile.Height - 3 * cell - 2 * gap) / 2;
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                dc.DrawRoundedRectangle(GridDots[(x + y) % GridDots.Length], null,
                    new Rect(start + x * (cell + gap), top + y * (cell + gap), cell, cell), cell * 0.3, cell * 0.3);
    }

    static readonly Brush GridTile = Frozen(new LinearGradientBrush(Color.FromRgb(0x5A, 0x6B, 0x86), Color.FromRgb(0x3B, 0x47, 0x5C), 90));
    static readonly Brush[] GridDots =
    [
        Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x66))), Frozen(new SolidColorBrush(Color.FromRgb(0x7B, 0xD8, 0x8F))),
        Frozen(new SolidColorBrush(Color.FromRgb(0x6C, 0xB6, 0xFF))), Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80))),
    ];

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public void Dispose()
    {
        _surfaces.DeviceReplaced -= Reload;
        foreach (var h in _minimized.Keys.ToList()) ForgetMinimized(h);
        ReleaseIcons();
        foreach (var b in _overlays.Values) SurfaceFactory.Release(b);
        _overlays.Clear();
        _grid?.Release();
        _placeholder?.Release();
        _separatorPicture?.Release();
        if (_dotBrush != null) SurfaceFactory.Release(_dotBrush);
        ForgetLabels();
        _root.Children.RemoveAll();
        _root.Dispose();
    }
}
