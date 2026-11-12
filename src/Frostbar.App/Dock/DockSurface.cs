using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Frostbar.App.Common;
using Frostbar.Core.Dock;
using Frostbar.Core.Shell;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Infrastructure;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.Dock;

/// <summary>
/// The Dock's layout, input and animation. What it shows is drawn by composition (<see cref="DockScene"/>, ADR-007):
/// a frame of magnification or bouncing only moves visuals there. This element draws nothing but an almost invisible
/// hit zone (alpha 1, so the plate and, while the pointer is over the Dock, the space of the magnified icons take the
/// pointer, and the rest of the layered window lets clicks through). The zone stays the same while the pointer is
/// over the Dock, so the WPF window is not redrawn while it magnifies. The render loop runs only while something moves.
/// </summary>
public sealed class DockSurface : FrameworkElement
{
    /// <summary>The plate's margin from the screen's edge it stands on (the bottom, or a side), a bounce's height, the name's gap.</summary>
    public const double BottomMargin = 4, BounceHeight = 20, LabelGap = 10;
    /// <summary>The name label's height (13 DIP text plus padding), and how far it may stick out past the plate's ends.</summary>
    public const double LabelHeight = 26, LabelMargin = 40;
    /// <summary>On a side of the screen the name stands beside its icon: this wide at most (a longer name ends in "…").</summary>
    public const double SideLabelWidth = 260;

    /// <summary>
    /// How far in from its edge the window reaches with icons up to <paramref name="maxSize"/> on a plate of
    /// <paramref name="padding"/>: the plate, the largest icon, and past it the larger of a bounce and the name label
    /// (which stays put while its icon bounces, as on macOS): above the icon at the bottom, beside it on a side.
    /// </summary>
    public static double DepthFor(DockEdge edge, double maxSize, double padding) =>
        BottomMargin + padding + maxSize + Math.Max(BounceHeight, LabelGap + (edge == DockEdge.Bottom ? LabelHeight : SideLabelWidth)) + 4;

    /// <summary>
    /// How far the window reaches past the widest plate at each end of the row: a name's room at the bottom (the name
    /// stands above its icon and may stick out past the plate); a little on a side, where the names are beside the icons.
    /// </summary>
    public static double EndMargin(DockEdge edge) => edge == DockEdge.Bottom ? LabelMargin : 8;

    /// <summary>The screen's edge the Dock stands on (stage 7): its row runs along it, its icons stand in from it.</summary>
    public DockEdge Edge { get; set; } = DockEdge.Bottom;

    /// <summary>The window seen from its edge (DIPs).</summary>
    DockFrame Row => new(Edge, ActualWidth, ActualHeight);

    /// <summary>A point of the window as how far along the row it is.</summary>
    double Along(Point p) => Row.Of(p.X, p.Y).Along;

    /// <summary>A point of the window as how far in from the screen's edge it is.</summary>
    double Inward(Point p) => Row.Of(p.X, p.Y).Inward;

    static Rect ToRect(Frostbar.Core.AppGrid.Box b) => new(b.X, b.Y, b.Width, b.Height);

    /// <summary>The plate above and below the icons, and between them and at its ends (DIPs; the settings').</summary>
    public double PlatePad { get; set; } = 10;
    public double Spacing { get; set; } = 8;
    /// <summary>
    /// Magnification comes and goes within these (it approaches, quick at first and settling: 2026-10-17; it moved at a
    /// constant speed for 0.12 and 0.18 s and stopped dead); a hop, a landing, a dropped icon flying into its slot.
    /// </summary>
    const double MagnifySeconds = Motion.DockMagnify / 1000, UnmagnifySeconds = Motion.DockUnmagnify / 1000, HopSeconds = Motion.DockHop / 1000, LandSeconds = Motion.DockLand / 1000, FlySeconds = Motion.DockFly / 1000;
    const int IdleFramesBeforeStop = 3;

    static readonly Brush HitFill = Frozen(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));

    IReadOnlyList<DockItem> _items = [];
    IReadOnlyList<SlotKind> _kinds = [];
    Func<DockItem, ImageSource?> _icon = _ => null;
    DockLayout _layout = new([], 0, 0);
    double? _pointer, _laidOutPointer;
    /// <summary>Where the pointer is over the window (DIPs); null while it is elsewhere.</summary>
    Point? _mouse;
    /// <summary>The pointer moved since the last layout: the next frame lays out again.</summary>
    bool _layoutDue;
    double _strength, _strengthTarget;
    int _hover = -1, _pressed = -1;
    readonly Dictionary<string, Bounce> _bounces = new();
    bool _animating, _measuring;
    int _idleFrames;
    Color _text = Colors.White;

    /// <summary>The dots' and separators' colour now (diagnostics: dark on a plate turned light).</summary>
    public Color TextColor => _text;
    double _scale = 1.25;
    double _widest;
    (IReadOnlyList<SlotKind> kinds, double size, double max, double scale, double spacing)? _widestFor;
    Rect _drawnZone = Rect.Empty;

    // Dragging: a left press on an app becomes a drag after DragThreshold DIPs; it leaves its slot, a gap opens where
    // it would land, and its picture follows the pointer (DragImage). RemoveDistance above the magnified icons (or
    // beside the window) it leaves the Dock instead.
    const double DragThreshold = 4, RemoveDistance = 24;
    int _dragFrom = -1;
    Point _pressPoint;
    bool _dragging, _removing, _externalDrag, _dropPinnable, _dropFolders, _keepFolders;
    /// <summary>The external drag is an app carried from the app grid (only a gap, never onto an icon).</summary>
    bool _appDrag;
    /// <summary>The kept item of the app carried from the app grid: it leaves its slot while the app is over the Dock (-1: none).</summary>
    int _carriedItem = -1;

    /// <summary>The item out of the row: the one dragged along it, or the kept one of an app carried in from the app grid.</summary>
    int Skipped => _dragging ? _dragFrom : _appDrag ? _carriedItem : -1;
    int _gapAt = -1, _dropOnto = -1;
    int[]? _slotOf;
    DragImage? _dragImage;

    /// <summary>The label over a pinned item dragged off the Dock ("Remove from Dock").</summary>
    public string RemoveText { get; set; } = "Remove";

    /// <summary>A dragged item was let go in the row: (item, number of other items before the place it was dropped).</summary>
    public event Action<int, int>? ItemMoved;
    /// <summary>A dragged item was let go away from the Dock.</summary>
    public event Action<int>? ItemDraggedOut;
    /// <summary>Files from another app were dropped onto an item (item, -1), or between apps (-1, items before).</summary>
    public event Action<int, int, string[]>? FilesDropped;

    bool _hold;

    /// <summary>
    /// The Dock stays as it is while its menu or a stack is open (macOS): the menu's click shield takes the pointer from
    /// it, and the icons shrank back under a menu left floating where the magnified icon had been. Let go, it eases back
    /// unless the pointer is on it again.
    /// </summary>
    public bool Hold
    {
        get => _hold;
        set
        {
            if (_hold == value) return;
            _hold = value;
            // Still on the Dock (the menu closed with the pointer over it): it stays, the next move lays it out. WPF
            // learns that the pointer is back only from that move, so the pointer itself is asked.
            if (value || _dragging || PointerInZone()) return;
            _mouse = null;
            _strengthTarget = 0;
            if (Animations) Animate(); else { _strength = 0; _pointer = null; Relayout(); }
        }
    }

    public double BaseSize { get; set; } = 40;
    public double MaxSize { get; set; } = 80;
    public bool Magnify { get; set; } = true;
    public bool ShowDots { get; set; } = true;
    public bool Animations { get; set; } = true;

    /// <summary>Where the pictures are drawn (set by the window once its backdrop exists).</summary>
    public DockScene? Scene
    {
        get => _scene;
        set
        {
            if (_scene != null) _scene.PicturesReplaced -= Place;
            _scene = value;
            if (_scene != null) _scene.PicturesReplaced += Place; // pictures on a new GPU device: shown at once
            if (_scene != null) _scene.Animations = () => Animations; // the marks' and dots' small movements
        }
    }

    DockScene? _scene;

    public DockLayout Layout => _layout;
    public IReadOnlyList<DockItem> Items => _items;
    public double Strength => _strength;
    public int Hovered => _hover;
    public IEnumerable<string> Bouncing => _bounces.Keys;

    static readonly List<WeakReference<DockSurface>> Made = new();

    /// <summary>
    /// An icon of a Dock bounces now (a launch waits for its window): work that can wait waits. A launch's landing waited
    /// 121 ms behind the settings index's window (2026-10-18, dock-launch: 305 ms after the window, the limit 300). A
    /// bounce older than 12 s is not counted (a launch bounces 10 s at most), so a Dock made again cannot hold it forever.
    /// </summary>
    public static bool AnyBouncing
    {
        get
        {
            Made.RemoveAll(w => !w.TryGetTarget(out _));
            return Made.Any(w => w.TryGetTarget(out var s) && s._bounces.Values.Any(b => s.Now - b.Start < 12));
        }
    }
    /// <summary>The render loop is subscribed (diagnostics: it must not be while nothing moves).</summary>
    public bool Animating => _animating;
    /// <summary>
    /// While an item is dragged, or something is dragged in (files, an app from the app grid): the items left of the
    /// gap it would land in (-1: none, as off the Dock).
    /// </summary>
    public int GapAt => _dragging || _externalDrag ? _gapAt : -1;
    /// <summary>An app carried from the app grid is over the Dock (diagnostics).</summary>
    public bool AppDragOver => _appDrag;
    /// <summary>The pointer along the row as the current layout used it (DIPs in the window: x at the bottom, y on a side; null when it is away).</summary>
    public double? PointerX => _laidOutPointer;

    /// <summary>Plate rectangle in DIPs inside the window (the backdrop follows it).</summary>
    public event Action<Rect>? PlateChanged;
    public event Action<int, MouseButton>? ItemClicked;
    public event Action<int>? ItemMenu;
    /// <summary>Frame intervals while animating (diagnostics: render pacing).</summary>
    public event Action<double>? Frame;
    /// <summary>Each layout as it is made, with the pointer it used (diagnostics "dock-trace": how the row moves as it is swept).</summary>
    public event Action<DockLayout, double?>? LaidOut;

    public DockSurface()
    {
        Made.Add(new WeakReference<DockSurface>(this));
        SnapsToDevicePixels = true;
        AllowDrop = true; // files from Explorer: open with an app, move to the trash, or keep an app in the Dock
        SizeChanged += (_, _) => Relayout();
    }

    public void SetItems(IReadOnlyList<DockItem> items, Func<DockItem, ImageSource?> icon)
    {
        // A press or drag follows its item when the list is rebuilt meanwhile (every change of the front app rebuilds
        // it: 2026-10-12, resetting the press there lost a click that came right after one); it ends if the item is gone.
        string? held = _dragFrom >= 0 && _dragFrom < _items.Count ? _items[_dragFrom].Key : null;
        string? pressedKey = _pressed >= 0 && _pressed < _items.Count ? _items[_pressed].Key : null;
        string? carriedKey = _carriedItem >= 0 && _carriedItem < _items.Count ? _items[_carriedItem].Key : null;
        var oldItems = _items;
        var oldKinds = _kinds;
        _items = items;
        _kinds = items.Select(i => i.Kind == DockItemKind.Separator ? SlotKind.Separator : SlotKind.Icon).ToList();
        NoteComingAndGoing(oldItems, oldKinds);
        _icon = icon;
        int IndexOf(string? key) => key == null ? -1 : items.Select((x, i) => (x, i)).FirstOrDefault(p => p.x.Key == key, (null!, -1)).Item2;
        if (held != null)
        {
            _dragFrom = IndexOf(held);
            if (_dragFrom < 0 && _dragging) EndDrag();
        }
        _pressed = IndexOf(pressedKey);
        _carriedItem = IndexOf(carriedKey);
        foreach (var key in _bounces.Keys.Where(k => items.All(i => i.Key != k)).ToList()) _bounces.Remove(key);
        Scene?.SetItems(items, icon);
        Relayout();
    }

    public void SetTheme(Brush text)
    {
        _text = ((SolidColorBrush)text).Color;
        Look();
        // The scene takes the items' pictures off with a new look and puts them on as it places them: placed now. The
        // plate turning light over a white window (item 4) changed the look alone, and the Dock stood empty until the
        // pointer came (2026-10-19, glass-adapt's photo).
        Place();
    }

    IReadOnlyDictionary<string, DockScene.Mark>? _marks;

    /// <summary>The apps' taskbar marks by item key (a progress, a badge; null: none), drawn over their icons.</summary>
    public void SetMarks(IReadOnlyDictionary<string, DockScene.Mark>? marks)
    {
        _marks = marks;
        Scene?.ForgetMarks(marks?.Values.Select(m => m.Picture).OfType<ImageSource>() ?? []);
        Place();
    }

    /// <summary>An item's marks as drawn (diagnostics), or null.</summary>
    public DockScene.Mark? MarkOf(string key) => _marks != null && _marks.TryGetValue(key, out var m) ? m : null;

    DockScene.Mark? MarkAt(int i) => _marks != null && i >= 0 && i < _items.Count && _marks.TryGetValue(_items[i].Key, out var m) ? m : null;

    void Look() => Scene?.SetLook(_text, _scale, BaseSize, Magnify ? Math.Max(MaxSize, BaseSize) : BaseSize);

    /// <summary>The sizes, magnification or indicators changed (settings): new pictures, new layout.</summary>
    public void Restyle()
    {
        _widestFor = null;
        Look();
        Relayout();
    }

    /// <summary>Diagnostics: item <paramref name="i"/> drawn pressed (-1: none), with no click behind it (pictures).</summary>
    public void ShowPressed(int i)
    {
        _pressed = i >= 0 && i < _items.Count ? i : -1;
        Place();
    }

    /// <summary>Icons changed (one loaded, the trash filled): show them.</summary>
    public void Redraw()
    {
        Scene?.SetItems(_items, _icon);
        Place();
    }

    /// <summary>Where the pointer magnifies the Dock (DIPs along its row in this element), and how much (0 to 1): the glass lights up there.</summary>
    public (double? along, double strength) Light => (_pointer, _strength);

    /// <summary>
    /// The plate's near side, in from the screen's edge (DIPs), so that it falls on a whole pixel of the window: the
    /// window's far side is the screen's edge at the bottom and on the right.
    /// </summary>
    double PlateNear => SnapInward(BottomMargin);
    double PlateDepth => BaseSize + 2 * PlatePad;
    /// <summary>Where the icons stand, in from the screen's edge.</summary>
    double IconNear => PlateNear + PlatePad;
    double Snap(double dip) => DockGeometry.Snap(dip, _scale);
    double SnapInward(double inward) => Edge == DockEdge.Left ? Snap(inward) : Row.Depth - Snap(Row.Depth - inward);

    void Relayout()
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale != _scale) { _scale = scale; Look(); }
        if (_dragging || _externalDrag) FindGap();
        var kinds = LayoutKinds(out _slotOf, out var weights);
        _layout = DockGeometry.Compute(kinds, Row.Length, BaseSize, Magnify ? MaxSize : BaseSize, _pointer, _strength, _scale, weights, Spacing);
        _laidOutPointer = _pointer;
        LaidOut?.Invoke(_layout, _pointer);
        PlateChanged?.Invoke(PlateRect);
        // The item under the pointer in this layout (what is shown, so what a click hits). No name while dragging.
        _hover = !_dragging && !_externalDrag && _mouse is Point p && InZone(p) ? ItemAt(Along(p)) : -1;
        Place();
    }

    /// <summary>Moves the pictures to the current layout and bounces; redraws this element only if the zone changed.</summary>
    void Place()
    {
        int flying = _fly is { } f ? IndexOf(f.Key) : -1;
        double now = Now;
        // The shade: on the item pressed (as deep as it has come), on the target of a drop, or going from one let go.
        int shaded = _pressed >= 0 ? _pressed : _dropOnto;
        double shade = 1;
        if (_pressed >= 0 && _pressKey == _items[_pressed].Key) shade = ShadeOf(_pressKey, now);
        else if (shaded < 0 && _letKey != null && Giving(now)) { shaded = IndexOf(_letKey); shade = ShadeOf(_letKey, now); }
        Scene?.Update(_layout, _slotOf, Row, IconNear, PlateNear, ShowDots,
            i => i < _items.Count && _bounces.TryGetValue(_items[i].Key, out var b) ? Offset(b, now) : 0,
            _hover, shaded, MarkAt, flying, _leavingSlots,
            give: _pressKey == null && _letKey == null ? null : i => i < _items.Count ? GiveOf(_items[i].Key, now) : 1, shadeAlpha: shade);
        if (_dragging) ShowDragImage();
        else if (_fly != null) Fly(flying);
        if (Zone() != _drawnZone) InvalidateVisual();
    }

    int IndexOf(string key)
    {
        for (int i = 0; i < _items.Count; i++) if (_items[i].Key == key) return i;
        return -1;
    }

    // ---- a dropped icon flying into its slot -------------------------------------------------------------------
    // Let go in the row (or where it was, or dragged off and kept by the Dock after all), the icon glides from under the
    // pointer into its slot as it takes its magnified-or-not size (macOS). It was in its slot at once, a jump from
    // above the Dock (2026-10-17).

    sealed record Flight(string Key, double X, double Y, double Size, double Start);
    Flight? _fly;

    /// <summary>The icon flying into its slot after a drop (diagnostics), or null.</summary>
    public string? Flying => _fly?.Key;

    /// <summary>
    /// An app carried in from the app grid was let go at (<paramref name="x"/>, <paramref name="y"/>) (pixels on the
    /// screen), its picture <paramref name="sizePx"/> wide: item <paramref name="key"/> flies from there into its slot,
    /// as it grows into the row. It appeared growing in its slot while the grid's picture went at once (2026-10-17).
    /// </summary>
    public void FlyIn(string key, int x, int y, double sizePx)
    {
        if (!Animations || IndexOf(key) < 0) return;
        _dragImage ??= new DragImage();
        _fly = new Flight(key, x, y, Math.Round(sizePx), Now);
        Animate();
        Place();
    }

    /// <summary>One frame of the flight of item <paramref name="i"/>: from where it was let go to its slot as laid out now (pixels on the screen).</summary>
    void Fly(int i)
    {
        var f = _fly!;
        if (i < 0 || _dragImage == null || Scene == null || Window.GetWindow(this) is not Window w) { EndFly(); return; }
        double t = Math.Min(1, (Now - f.Start) / FlySeconds), k = Motion.Out(t);
        var slot = SlotRect(i);
        if (slot.IsEmpty) { EndFly(); return; }
        double toX = (w.Left + slot.X + slot.Width / 2) * _scale, toY = (w.Top + slot.Y + slot.Height / 2) * _scale, toSize = slot.Width * _scale;
        float size = (float)Math.Round(f.Size + (toSize - f.Size) * k);
        _dragImage.Show(Scene.PictureFor(i, size), size, f.X + (toX - f.X) * k, f.Y + (toY - f.Y) * k, 1f);
        if (t >= 1) EndFly();
    }

    void EndFly()
    {
        _fly = null;
        _dragImage?.Vanish(); // in the frame its slot shows the icon again
        Place();
    }

    // ---- dragging ----------------------------------------------------------------------------------------------

    // ---- coming and going ------------------------------------------------------------------------------------
    // As apps open and close, an item that appears grows into the row and the room of one that went closes up, and the
    // gap a drag opens moves over smoothly (macOS). Each slot takes a share of its width (DockGeometry's weights).

    const double GrowSeconds = Motion.DockGrow / 1000;
    readonly Dictionary<string, double> _appearing = new(); // item key → when it appeared
    // Before which item, since when, from what share, and the item that went (its picture shrinks with its room; null:
    // only room, as an app carried in from the app grid leaves behind).
    readonly List<(int before, SlotKind kind, double start, double from, string? key)> _closing = new();
    /// <summary>The rooms closing up in the present layout, by slot, with their items' keys (for their pictures).</summary>
    readonly List<(string key, int slot)> _leavingSlots = new();
    /// <summary>The item being dragged off the Dock: once it goes, its room is closed already (no room opens to close again).</summary>
    string? _draggedOff;
    int[]? _itemOfSlot; // slot → item (-1: a gap or the room of an item that went); null: one to one
    double _gapStart = -1; // when the drag's gap began to open where it is (-1: open at once)
    int _oldGap = -1; double _oldGapStart, _oldGapFrom; // the gap it left, closing

    bool _transitioning;

    double Grow(double since, double now) => Motion.Smooth((now - since) / GrowSeconds);

    void NoteComingAndGoing(IReadOnlyList<DockItem> oldItems, IReadOnlyList<SlotKind> oldKinds)
    {
        if (!Animations || oldItems.Count == 0) return; // the first row comes as it is
        var now = Now;
        var newKeys = new Dictionary<string, int>();
        for (int i = 0; i < _items.Count; i++) newKeys.TryAdd(_items[i].Key, i);
        var oldKeys = new HashSet<string>(oldItems.Select(i => i.Key));
        bool any = false;
        foreach (var it in _items)
            if (!oldKeys.Contains(it.Key)) { _appearing[it.Key] = now; any = true; }
        // One that comes back while its room still closes: the closing room goes (it grows in afresh).
        _closing.RemoveAll(c => c.key != null && newKeys.ContainsKey(c.key));
        for (int j = 0; j < oldItems.Count; j++)
        {
            if (newKeys.ContainsKey(oldItems[j].Key)) continue;
            // Dragged off the Dock: out of the row already (its slot was skipped while it was dragged).
            if (oldItems[j].Key == _draggedOff) continue;
            // Its room closes up before the next item that stayed (or at the end), its picture shrinking with it.
            int before = _items.Count;
            for (int k = j + 1; k < oldItems.Count; k++)
                if (newKeys.TryGetValue(oldItems[k].Key, out var at)) { before = at; break; }
            _closing.Add((before, oldKinds[j], now, 1, oldItems[j].Key));
            Scene?.Keep(j, oldItems[j].Key); // the scene still has the old items
            any = true;
        }
        if (any) Animate();
    }

    bool Transitioning(double now)
    {
        // Every frame of a sweep: the lists are nearly always empty, and then nothing is made to look through them.
        if (_appearing.Count > 0)
            foreach (var key in _appearing.Where(kv => now - kv.Value >= GrowSeconds).Select(kv => kv.Key).ToList()) _appearing.Remove(key);
        if (_closing.Count > 0)
        {
            foreach (var c in _closing.Where(c => now - c.start >= GrowSeconds && c.key != null)) Scene?.Let(c.key!);
            _closing.RemoveAll(c => now - c.start >= GrowSeconds);
        }
        if (_oldGap >= 0 && now - _oldGapStart >= GrowSeconds) _oldGap = -1;
        if (_gapStart >= 0 && now - _gapStart >= GrowSeconds) _gapStart = -1;
        return _appearing.Count > 0 || _closing.Count > 0 || _oldGap >= 0 || _gapStart >= 0;
    }

    bool _dragGapOpened; // the drag's gap has been placed once (its first is the item's own room, open at once)

    /// <summary>The drag's gap moved from <paramref name="from"/> to <see cref="_gapAt"/>: the old one closes, the new one opens.</summary>
    void GapMoved(int from)
    {
        if (!Animations || from == _gapAt) return;
        double now = Now;
        if (from >= 0) { _oldGapFrom = _gapStart >= 0 ? Grow(_gapStart, now) : 1; _oldGap = from; _oldGapStart = now; }
        _gapStart = _gapAt >= 0 ? now : -1;
        Animate();
    }

    /// <summary>
    /// The row as laid out: while an item is dragged it leaves its slot, and a gap opens where it (or an app dragged in
    /// from Explorer) would land; items coming in and the room of items gone take a share of their width.
    /// <paramref name="slotOf"/> maps items to slots (null: one to one; -1: not in the row).
    /// </summary>
    IReadOnlyList<SlotKind> LayoutKinds(out int[]? slotOf, out IReadOnlyList<double>? weights)
    {
        int skip = Skipped, gap = _gapAt;
        slotOf = null;
        weights = null;
        _itemOfSlot = null;
        _leavingSlots.Clear();
        if (skip < 0 && gap < 0 && _oldGap < 0 && _appearing.Count == 0 && _closing.Count == 0) return _kinds;
        double now = Now;
        var kinds = new List<SlotKind>(_kinds.Count + 2 + _closing.Count);
        var w = new List<double>(kinds.Capacity);
        var itemOf = new List<int>(kinds.Capacity);
        slotOf = new int[_items.Count];
        void Add(SlotKind k, double weight, int item) { kinds.Add(k); w.Add(weight); itemOf.Add(item); }
        var room = skip >= 0 ? _kinds[skip] : SlotKind.Icon; // a dragged separator opens a separator's room
        void Gaps(int kept)
        {
            if (kept == _oldGap && _oldGap != gap) Add(room, _oldGapFrom * (1 - Grow(_oldGapStart, now)), -1);
            if (kept == gap) Add(room, _gapStart >= 0 ? Grow(_gapStart, now) : 1, -1);
        }
        void Closing((int before, SlotKind kind, double start, double from, string? key) c)
        {
            if (c.key != null) _leavingSlots.Add((c.key, kinds.Count));
            Add(c.kind, c.from * (1 - Grow(c.start, now)), -1);
        }
        int kept = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            foreach (var c in _closing) if (c.before == i) Closing(c);
            if (i == skip) { slotOf[i] = -1; continue; }
            Gaps(kept);
            slotOf[i] = kinds.Count;
            Add(_kinds[i], _appearing.TryGetValue(_items[i].Key, out var since) ? Grow(since, now) : 1, i);
            kept++;
        }
        foreach (var c in _closing) if (c.before >= _items.Count) Closing(c);
        Gaps(kept);
        _itemOfSlot = itemOf.ToArray();
        weights = w;
        return kinds;
    }

    /// <summary>The item at <paramref name="x"/> in the current layout (not a gap or the room of an item gone), or -1.</summary>
    int ItemAt(double x)
    {
        int slot = DockGeometry.HitTest(_layout, x);
        if (slot < 0 || _itemOfSlot == null) return slot;
        return slot < _itemOfSlot.Length ? _itemOfSlot[slot] : -1;
    }

    /// <summary>
    /// Where a dragged item or a dropped app would land: the number of items left of the pointer, in the row without the
    /// dragged item and without a gap (so the gap opening does not move the target); never past the last item that
    /// moves (the apps, and the separators kept among them). It counted the apps only, which put the gap on the wrong
    /// side of a separator. An app carried from the app grid is kept among the kept items, so its gap stays there too
    /// (among the running apps it would have opened where the app does not go).
    /// </summary>
    int InsertionAt(double x) => InsertionAt(x, out _);

    /// <param name="amongMovable">
    /// In that same row, x is over an item that moves or the slot just past the last of them (not over the app grid,
    /// the folders or the trash).
    /// </param>
    int InsertionAt(double x, out bool amongMovable)
    {
        var kinds = new List<SlotKind>();
        int limit = 0, movable = 0, skip = Skipped;
        for (int i = 0; i < _items.Count; i++)
        {
            if (i == skip) continue;
            kinds.Add(_kinds[i]);
            if (_items[i].Movable) movable = kinds.Count;
            if (_appDrag ? _items[i].Pinned : _items[i].Movable) limit = kinds.Count;
        }
        var l = DockGeometry.Compute(kinds, Row.Length, BaseSize, Magnify ? MaxSize : BaseSize, x, _strength, _scale, spacing: Spacing);
        int hit = DockGeometry.HitTest(l, x);
        amongMovable = hit >= 0 && hit <= movable;
        int n = 0;
        while (n < l.Slots.Count && l.Slots[n].Center < x) n++;
        return Math.Min(n, limit);
    }

    void FindGap()
    {
        if (_mouse is not Point p) return;
        int was = _gapAt;
        double along = Along(p);
        if (_dragging)
        {
            // Away from the screen's edge past the magnified icons, or past either end of the row.
            _removing = Inward(p) > IconNear + MaxSize + RemoveDistance || along < -RemoveDistance || along > Row.Length + RemoveDistance;
            _gapAt = _removing || !_items[_dragFrom].Movable ? -1 : InsertionAt(along); // folders stay where they are
            _strengthTarget = _removing ? 0 : 1;
            if (!Animations) _strength = _strengthTarget;
            GapMoved(was);
            // The drag's first gap is the room the item leaves: open at once. Growing from nothing, the row closed in by
            // a slot for a frame and slid out again as the drag began.
            if (!_dragGapOpened && _gapAt >= 0) _gapStart = -1;
            _dragGapOpened = true;
            return;
        }
        // Files from another app: onto an icon (open with it, into a folder, or the trash), between icons to keep an
        // app, or folders anywhere else on the plate to keep them as stacks. An app from the app grid only ever goes
        // between icons, among the apps (macOS).
        if (_appDrag)
        {
            // Judged in the row it would land in (without its kept item, without the gap), as a drag along the row.
            _dropOnto = -1;
            _keepFolders = false;
            int n = InsertionAt(along, out bool among);
            _gapAt = among ? n : -1;
            GapMoved(was);
            return;
        }
        var at = DockGeometry.Compute(_kinds, Row.Length, BaseSize, Magnify ? MaxSize : BaseSize, along, _strength, _scale, spacing: Spacing);
        int i = DockGeometry.HitTest(at, along);
        var kind = i >= 0 ? _items[i].Kind : DockItemKind.Separator;
        bool centred = i >= 0 && Math.Abs(along - at.Slots[i].Center) < at.Slots[i].Size * 0.3;
        bool onto = kind is DockItemKind.Trash or DockItemKind.Folder
                    || kind == DockItemKind.App && (!(_dropPinnable || _dropFolders) || centred);
        _dropOnto = onto ? i : -1;
        _gapAt = !onto && _dropPinnable && i >= 0 && i <= LastMovable() + 1 ? InsertionAt(along) : -1;
        _keepFolders = !onto && _dropFolders && i >= 0;
        GapMoved(was);
    }

    /// <summary>Dragged off the Dock, the item goes: a kept app, one used lately, a folder.</summary>
    bool Removable(int i) => i >= 0 && i < _items.Count
        && (_items[i].Pinned || _items[i].Kind == DockItemKind.Folder || _items[i].Key.StartsWith(DockModel.RecentPrefix, StringComparison.Ordinal));

    int LastMovable()
    {
        for (int i = _items.Count - 1; i >= 0; i--) if (_items[i].Movable) return i;
        return -1;
    }

    void ShowDragImage()
    {
        if (_mouse is not Point p || Scene == null || Window.GetWindow(this) is not Window w) return;
        _dragImage ??= new DragImage();
        float size = (float)Math.Round((Magnify ? Math.Max(MaxSize, BaseSize) : BaseSize) * _scale);
        bool removable = Removable(_dragFrom);
        _dragImage.Show(Scene.PictureFor(_dragFrom, size), size,
            (int)Math.Round((w.Left + p.X) * _scale), (int)Math.Round((w.Top + p.Y) * _scale),
            _removing && removable ? 0.5f : 1f, _removing && removable ? RemoveText : null,
            text => (Scene.LabelPicture(text, out var px), px));
    }

    /// <param name="keepImage">The dragged picture stays where it is (it flies into its slot next).</param>
    void EndDrag(bool keepImage = false)
    {
        _dragging = false;
        _removing = false;
        _gapAt = -1;
        _gapStart = -1; // the dropped item takes the gap's place at once
        _oldGap = -1;
        _dragFrom = -1;
        if (!keepImage) _dragImage?.Hide();
        if (IsMouseCaptured) ReleaseMouseCapture();
        Relayout();
    }

    // ---- bounces -----------------------------------------------------------------------------------------------

    sealed class Bounce
    {
        public double Start;
        public int Hops;            // 0: until stopped
        public double? LandFrom, LandStart;
        /// <summary>The hops whose start lit the plate under it so far (the plate is lit as each begins).</summary>
        public int Told;
    }

    /// <summary>
    /// An icon starting a hop (false) or landing (true): its key and where it stands along the row (DIPs). The plate is
    /// lit under it (2026-10-23, the user's "A どこから開いても跳ねる": the glass energized as it bounces).
    /// </summary>
    public event Action<string, double, bool>? Energized;

    /// <summary>How many hops and landings have lit the plate (diagnostics).</summary>
    public int Hopped { get; private set; }
    public int Landed { get; private set; }

    /// <summary>Tells of the hops begun since the last frame and of the landings done.</summary>
    void TellBounces(double now)
    {
        foreach (var (key, b) in _bounces)
        {
            if (b.LandFrom != null) continue;
            int begun = Math.Max(0, DockMotion.HopsBegun(now - b.Start, HopSeconds));
            if (b.Hops > 0) begun = Math.Min(begun, b.Hops);
            if (begun <= b.Told) continue;
            b.Told = begun;
            Hopped++;
            if (Along(key) is double at) Energized?.Invoke(key, at, false);
        }
    }

    double? Along(string key)
    {
        int i = IndexOf(key), si = i >= 0 && _slotOf != null ? (i < _slotOf.Length ? _slotOf[i] : -1) : i;
        return si >= 0 && si < _layout.Slots.Count ? _layout.Slots[si].Center : null;
    }

    // ---- a press giving (2026-10-23, the user's "A 沈む＋隣へ光る") ------------------------------------------------------
    // The icon pressed sinks to Core's PressGive.Icon (0.92) and its shade comes in over 60 ms (it was there at once);
    // let go, it springs back past its size and the shade goes over 120 ms. Icon, shade and a bounce's lift move as one
    // box (DockScene). Not with animations off or reduced motion: the shade at once, as before.

    string? _pressKey, _letKey;
    double _pressStart, _letStart, _pressFrom = 1, _shadeFrom, _letScale = 1, _letShade;

    /// <summary>The plate lit under an icon pressed: where along the row (DIPs).</summary>
    public event Action<double>? Pressed;

    /// <summary>The middle of the plate's thickness at <paramref name="along"/> the row, in the window (DIPs): where a press or a hop lights it.</summary>
    public Point PlatePoint(double along)
    {
        var (x, y) = Row.Point(along, PlateNear + PlateDepth / 2);
        return new Point(x, y);
    }

    bool Gives => Animations && !ReduceMotion.Calms("press");

    double GiveOf(string key, double now)
    {
        if (key == _pressKey) return PressGive.Pressing(_pressFrom, PressGive.Icon, (now - _pressStart) * 1000);
        if (key == _letKey && PressGive.Settling((now - _letStart) * 1000)) return PressGive.LettingGo(_letScale, (now - _letStart) * 1000);
        return 1;
    }

    double ShadeOf(string key, double now)
    {
        if (key == _pressKey) return Gives ? PressGive.ShadeIn(_shadeFrom, (now - _pressStart) * 1000) : 1;
        if (key == _letKey) return PressGive.ShadeOut(_letShade, (now - _letStart) * 1000);
        return 0;
    }

    void PressBegin(int i)
    {
        string key = _items[i].Key;
        double now = Now;
        _pressFrom = key == _letKey ? GiveOf(key, now) : 1;
        _shadeFrom = key == _letKey ? ShadeOf(key, now) : 0;
        if (key == _letKey) _letKey = null;
        _pressKey = key;
        _pressStart = now;
        if (Gives) Animate();
        if (Along(key) is double at) Pressed?.Invoke(at);
    }

    void PressEnd()
    {
        if (_pressKey == null) return;
        double now = Now;
        if (Gives)
        {
            _letScale = GiveOf(_pressKey, now);
            _letShade = ShadeOf(_pressKey, now);
            _letKey = _pressKey;
            _letStart = now;
            Animate();
        }
        _pressKey = null;
    }

    /// <summary>A press giving or springing back (the frames go on).</summary>
    bool Giving(double now) => Gives && (_pressKey != null || _letKey != null && (now - _letStart) * 1000 < Math.Max(Motion.BounceRelease, Motion.PressShadeOut));

    /// <summary>The pressed icon's scale and shade now (diagnostics "dock-light"), and its key.</summary>
    public (string? key, double scale, double shade) PressState
    {
        get
        {
            double now = Now;
            string? key = _pressKey ?? (_letKey != null && Giving(now) ? _letKey : null);
            return key == null ? (null, 1, 0) : (key, GiveOf(key, now), ShadeOf(key, now));
        }
    }

    double Now => _clock.Elapsed.TotalSeconds;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Starts bouncing an item: <paramref name="hops"/> hops, or until <see cref="StopBounce"/> when 0.</summary>
    public void StartBounce(string key, int hops)
    {
        if (!Animations) return;
        // Reduced motion: an app asking for attention does not hop (its launch's bounce has a setting of its own).
        if (hops > 0 && ReduceMotion.Calms("attention")) return;
        if (_bounces.TryGetValue(key, out var b) && b.LandFrom == null) { if (hops > 0) b.Hops = Math.Max(b.Hops, (int)((Now - b.Start) / HopSeconds) + hops); return; }
        _bounces[key] = new Bounce { Start = Now, Hops = hops };
        Animate();
    }

    /// <summary>Lands a bounce quickly (within ~120 ms) instead of finishing the hop.</summary>
    public void StopBounce(string key)
    {
        if (!_bounces.TryGetValue(key, out var b) || b.LandFrom != null) return;
        b.LandFrom = Offset(b, Now);
        b.LandStart = Now;
        Animate();
    }

    double Offset(Bounce b, double now)
    {
        if (b.LandFrom is double from) return DockMotion.Land(from, now - b.LandStart!.Value, LandSeconds);
        double t = now - b.Start;
        if (b.Hops > 0 && t >= b.Hops * HopSeconds) return 0;
        return DockMotion.Hop(t, HopSeconds, BounceHeight);
    }

    bool BounceDone(Bounce b, double now) =>
        b.LandFrom != null ? now - b.LandStart!.Value >= LandSeconds : b.Hops > 0 && now - b.Start >= b.Hops * HopSeconds;

    // ---- animation loop ----------------------------------------------------------------------------------------

    void Animate()
    {
        _idleFrames = 0;
        if (_animating) return;
        _animating = true;
        _firstFrame = true;
        if (_ticks == null)
        {
            // DWM's frames, not WPF's: this element draws nothing while the Dock animates (ADR-007).
            _ticks = new FrameClock(Dispatcher);
            _ticks.Tick += OnFrame;
        }
        _ticks.Start();
    }

    FrameClock? _ticks;
    bool _firstFrame;
    /// <summary>A press gave in the last frame: once it is over, the row is placed at rest once more.</summary>
    bool _shadeLeft;

    /// <summary>Stops the frame clock's thread (the window is closing).</summary>
    public void Close()
    {
        _ticks?.Dispose();
        _dragImage?.Dispose();
        _dragImage = null;
        _ticks = null;
        _animating = false;
    }

    void OnFrame(double dt)
    {
        if (!_firstFrame) Frame?.Invoke(dt * 1000);
        _firstFrame = false;
        dt = Math.Min(dt, 0.1); // after a stall, ease on rather than jump

        bool moving = _layoutDue;
        _layoutDue = false;
        if (_strength != _strengthTarget)
        {
            _strength = DockMotion.Approach(_strength, _strengthTarget, dt, _strengthTarget > _strength ? MagnifySeconds : UnmagnifySeconds);
            if (_strength == 0 && _strengthTarget == 0) _pointer = null;
            moving = true;
        }
        if (_fly != null) moving = true;
        double now = Now;
        bool bouncing = _bounces.Count > 0; // including one that lands now: it is placed at rest once more
        TellBounces(now);
        // Every frame of a sweep: with no bounce (nearly always) nothing is made for the look below.
        if (_bounces.Count > 0) foreach (var key in _bounces.Where(kv => BounceDone(kv.Value, now)).Select(kv => kv.Key).ToList())
        {
            // Down from a launch's bounce (its first window came): the plate lit under it as it lands.
            if (_bounces[key].LandFrom != null && Along(key) is double at) { Landed++; Energized?.Invoke(key, at, true); }
            _bounces.Remove(key);
        }
        bool transitioning = _transitioning; // laid out once more after the last step, at rest
        _transitioning = Transitioning(now);
        if (transitioning || _transitioning) moving = true;
        bool giving = Giving(now);
        if (!giving && _pressKey == null && _letKey != null) _letKey = null; // sprung back: placed at rest once more below
        if (moving) Relayout(); else if (bouncing || giving || _letKey == null && _shadeLeft) Place();
        _shadeLeft = giving;
        // Stops a few frames after the last change, so a pointer that keeps moving does not subscribe and unsubscribe
        // every frame.
        bool idle = !moving && _bounces.Count == 0 && !_measuring && !giving;
        _idleFrames = idle ? _idleFrames + 1 : 0;
        if (_idleFrames >= IdleFramesBeforeStop)
        {
            _ticks?.Stop();
            _animating = false;
            _idleFrames = 0;
        }
    }

    /// <summary>Keeps the render loop running (diagnostics: frame pacing during a sweep), or lets it stop.</summary>
    public void Measure(bool on)
    {
        _measuring = on;
        if (on) Animate();
    }

    // ---- input -------------------------------------------------------------------------------------------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        _mouse = p;
        _pointer = Along(p);
        if (_dragFrom >= 0 && !_dragging && e.LeftButton == MouseButtonState.Pressed
            && (Math.Abs(p.X - _pressPoint.X) > DragThreshold || Math.Abs(p.Y - _pressPoint.Y) > DragThreshold))
        {
            _dragging = true;
            _dragGapOpened = false;
            _pressed = -1;
            PressEnd();
            Log.Debug($"dock: dragging item {_dragFrom}");
        }
        // While dragging, FindGap decides (away from the Dock it shrinks back).
        if (!_dragging && _strengthTarget != 1) { _strengthTarget = 1; if (!Animations) _strength = 1; }
        if (!Animations) { Relayout(); return; }
        // A mouse reports several times per frame (250 Hz and up): lay out once per frame.
        _layoutDue = true;
        Animate();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragging) return; // the drag holds the pointer; it ends with the button
        _mouse = null;
        _hover = -1;
        _pressed = -1;
        PressEnd();
        if (_hold) { Place(); return; } // under its menu: magnified as it was (the name gives way to the menu)
        _strengthTarget = 0;
        if (Animations) Animate(); else { _strength = 0; _pointer = null; Relayout(); }
        Place();
    }

    /// <summary>The left press under way is a double-click's second: its click is ignored (macOS).</summary>
    bool _secondClick;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (_fly != null) EndFly(); // a press while a dropped icon still flies: it is in its slot at once
        var p = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Left) _secondClick = e.ClickCount >= 2;
        _pressed = InZone(p) ? ItemAt(Along(p)) : -1;
        Log.Debug($"dock: {e.ChangedButton} down at {Along(p):0.0} along (pointer {_pointer:0.0}, strength {_strength:0.00}) → item {_pressed}");
        if (_pressed < 0) return;
        e.Handled = true;
        if (_items[_pressed].Kind != DockItemKind.Separator) PressBegin(_pressed);
        if (e.ChangedButton == MouseButton.Left && (_items[_pressed].Movable || _items[_pressed].Kind == DockItemKind.Folder))
        {
            // A drag may start: keep the pointer even when it leaves the window.
            _dragFrom = _pressed;
            _pressPoint = p;
            CaptureMouse();
        }
        Place();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        var p = e.GetPosition(this);
        if (_dragging && e.ChangedButton == MouseButton.Left)
        {
            int item = _dragFrom, gap = _gapAt;
            bool removing = _removing;
            string key = _items[item].Key;
            e.Handled = true;
            Log.Debug($"dock: drop of item {item}: {(removing ? "away from the Dock" : $"{gap} apps before it")}");
            // Where its picture is now (pixels on the screen, centred on the pointer, at the magnified size): it flies
            // from there into the slot it ends up in.
            var w = Window.GetWindow(this);
            Flight? from = Animations && _dragImage != null && w != null
                ? new Flight(key, (w.Left + p.X) * _scale, (w.Top + p.Y) * _scale, Math.Round((Magnify ? Math.Max(MaxSize, BaseSize) : BaseSize) * _scale), Now)
                : null;
            _fly = from; // its slot shows nothing from the first layout on
            EndDrag(keepImage: from != null);
            if (removing)
            {
                _draggedOff = key; // gone, it leaves no room to close (it was out of the row while dragged)
                try { ItemDraggedOut?.Invoke(item); } finally { _draggedOff = null; }
            }
            else if (gap >= 0) ItemMoved?.Invoke(item, gap);
            // Gone from the Dock (dragged off): its picture goes where it was let go (a puff of smoke there). Kept after
            // all (a running app dragged off, a drop where it was), it flies back into its slot.
            if (_fly != null && IndexOf(key) < 0) { _fly = null; _dragImage?.Hide(); }
            if (_fly != null) Animate();
            Place();
            return;
        }
        if (e.ChangedButton == MouseButton.Left)
        {
            _dragFrom = -1;
            if (IsMouseCaptured) ReleaseMouseCapture();
        }
        int at = InZone(p) ? ItemAt(Along(p)) : -1;
        int pressed = _pressed;
        _pressed = -1;
        PressEnd();
        Place();
        if (at != pressed) Log.Debug($"dock: released at {Along(p):0.0} along over item {at}, pressed on {pressed}: no click");
        if (at < 0 || at != pressed) return;
        e.Handled = true;
        if (e.ChangedButton == MouseButton.Left && _secondClick)
        {
            // A double-click (a habit from Windows' desktop) is one click: its second started an app twice, or put an
            // app the first click had brought forward away again (2026-10-14 review).
            _secondClick = false;
            Log.Debug($"dock: the second click of a double-click on item {at}: ignored");
            return;
        }
        if (e.ChangedButton == MouseButton.Right) ItemMenu?.Invoke(at);
        else ItemClicked?.Invoke(at, e.ChangedButton);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging) { Log.Debug("dock: drag cancelled (the pointer was taken away)"); EndDrag(); }
        _dragFrom = -1;
        if (_pressed < 0) PressEnd();
    }

    // ---- files dragged in from other apps ----------------------------------------------------------------------

    static bool Pinnable(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".lnk" && File.Exists(path);

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        Log.Debug($"dock: files dragged in (allowed: {e.AllowedEffects})");
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            // Once per drag, not on every move: the files can be on a slow share.
            _dropPinnable = files.All(Pinnable);
            _dropFolders = files.All(Directory.Exists);
        }
        DragFiles(e);
    }

    protected override void OnDragOver(DragEventArgs e) { base.OnDragOver(e); DragFiles(e); }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        var p = e.GetPosition(this);
        Log.Debug($"dock: files dragged away at ({p.X:0.0}, {p.Y:0.0}); zone {Zone()}");
        EndDragFiles();
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        DragFiles(e);
        int onto = _dropOnto, gap = _gapAt;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        EndDragFiles();
        if (files is not { Length: > 0 } || e.Effects == DragDropEffects.None) return;
        Log.Debug($"dock: {files.Length} file(s) dropped {(onto >= 0 ? $"onto item {onto}" : $"between apps ({gap} before)")}");
        // After OLE's Drop call has returned: the source app stays inside DoDragDrop (its window frozen) until then,
        // so slow work (starting an app, moving files to the Recycle Bin) runs afterwards.
        Dispatcher.BeginInvoke(() => FilesDropped?.Invoke(onto, onto >= 0 ? -1 : gap, files));
    }

    void DragFiles(DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) { e.Effects = DragDropEffects.None; return; }
        var p = e.GetPosition(this);
        _externalDrag = true;
        _mouse = p;
        _pointer = Along(p);
        _strengthTarget = 1;
        if (!Animations) _strength = 1;
        Relayout(); // finds the target now: the effect is answered right away
        var want = _dropOnto >= 0
            ? _items[_dropOnto].Kind == DockItemKind.Trash ? DragDropEffects.Move : DragDropEffects.Copy
            : _gapAt >= 0 || _keepFolders ? DragDropEffects.Link : DragDropEffects.None;
        e.Effects = (e.AllowedEffects & want) != 0 ? want
            : want != DragDropEffects.None && (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
        if (Animations) Animate();
    }

    /// <param name="closeGap">The gap closes up (false: an item takes its place at once).</param>
    void EndDragFiles(bool closeGap = true)
    {
        _externalDrag = false;
        _dropOnto = -1;
        _keepFolders = false;
        int was = _gapAt;
        _gapAt = -1;
        if (closeGap) GapMoved(was); // a gap left open by a drag that went away closes up
        else { _gapStart = -1; _oldGap = -1; }
        _mouse = null;
        _strengthTarget = 0;
        if (Animations) Animate(); else { _strength = 0; _pointer = null; }
        Relayout();
    }

    // ---- an app carried from the app grid ------------------------------------------------------------------------
    // The grid keeps the pointer (it captured it at the press) and tells the Dock where the app is: over the Dock a gap
    // opens where it would be kept, as for a program dragged in from File Explorer.

    /// <summary>
    /// An app carried from the app grid is at <paramref name="p"/> (DIPs in this element; null: it went elsewhere).
    /// <paramref name="keptKey"/> is its item when the Dock keeps it already: that one leaves its slot while the app is
    /// over the Dock, and moves to the gap if it is let go there (one item per app). True while a gap is open for it.
    /// </summary>
    public bool CarryApp(Point? p, string? keptKey)
    {
        if (p is not Point at || !InZone(at))
        {
            if (_appDrag) EndCarry(gapTaken: false);
            return false;
        }
        _mouse = at;
        _pointer = Along(at);
        _strengthTarget = 1;
        if (!Animations) _strength = 1;
        if (!_appDrag)
        {
            _appDrag = true;
            _externalDrag = true;
            _carriedItem = keptKey == null ? -1 : _items.Select((x, i) => (x, i)).FirstOrDefault(q => q.x.Key == keptKey, (null!, -1)).Item2;
            // Its room closes up (it left the row), as one of an app that quits.
            if (_carriedItem >= 0 && Animations) _closing.Add((_carriedItem + 1, _kinds[_carriedItem], Now, 1, null));
            Relayout(); // the zone is now the magnified row's
        }
        else if (Animations) _layoutDue = true; // once per frame, as the Dock's own pointer moves
        else Relayout();
        if (Animations) Animate();
        return _gapAt >= 0;
    }

    /// <summary>
    /// The carried app was let go: the items left of the gap it takes (its kept item left out), or -1 when none was
    /// open (it is not kept, and a kept item comes back to its place).
    /// </summary>
    public int DropApp()
    {
        if (!_appDrag) return -1;
        Relayout(); // the gap for where it was let go, not for the last frame
        int gap = _gapAt;
        EndCarry(gapTaken: gap >= 0);
        return gap;
    }

    /// <summary>
    /// The app carried from the grid is gone from the Dock: dropped into the gap (<paramref name="gapTaken"/>: a kept
    /// item takes it at once, a new one grows into it as it closes), or away (the gap closes, a kept item comes back).
    /// </summary>
    void EndCarry(bool gapTaken)
    {
        int carried = _carriedItem;
        _appDrag = false;
        _carriedItem = -1;
        if (carried >= 0 && !gapTaken && Animations && carried < _items.Count) _appearing[_items[carried].Key] = Now;
        EndDragFiles(closeGap: !(gapTaken && carried >= 0));
    }

    /// <summary>
    /// WPF would otherwise hit-test the drawing on each pointer report; the zone is all that takes the pointer, and
    /// Windows already lets clicks through elsewhere (alpha 0).
    /// </summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters p) =>
        InZone(p.HitPoint) ? new PointHitTestResult(this, p.HitPoint) : null;

    /// <summary>
    /// The plate at rest; while the pointer is over the Dock (or it is still shrinking back) the room the widest
    /// magnified row needs, so the zone does not change from frame to frame. Both reach past the plate to the window's
    /// side on the screen's edge it stands on: a pointer pushed against that edge is on the Dock, as on macOS
    /// (2026-10-12: with auto-hide, the Dock came up under such a pointer and then let go of it).
    /// </summary>
    Rect Zone()
    {
        if (_items.Count == 0 || ActualWidth <= 0 || ActualHeight <= 0) return Rect.Empty;
        if (!(_strength > 0 || IsMouseOver || _dragging || _externalDrag))
            return ToRect(Row.Box(_layout.PlateLeft, 0, _layout.PlateWidth, PlateNear + PlateDepth));
        double widest = Widest();
        return ToRect(Row.Box(Snap((Row.Length - widest) / 2), 0, widest, IconNear + (Magnify ? MaxSize : BaseSize) + PlatePad));
    }

    double Widest()
    {
        // Asked at every frame (Place, and each pointer move through InZone): its inputs are compared as they are, with
        // nothing made. _kinds is a new list whenever the items change (SetItems), never changed in place.
        double max = Magnify ? Math.Max(MaxSize, BaseSize) : BaseSize;
        var w = _widestFor;
        if (w == null || !ReferenceEquals(w.Value.kinds, _kinds) || w.Value.size != BaseSize || w.Value.max != max || w.Value.scale != _scale || w.Value.spacing != Spacing)
        {
            _widest = DockGeometry.MaxPlateWidth(_kinds, BaseSize, max, _scale, Spacing);
            _widestFor = (_kinds, BaseSize, max, _scale, Spacing);
        }
        return _widest;
    }

    bool InZone(Point p) => Zone().Contains(p);

    /// <summary><paramref name="p"/> (DIPs in this element) is where the Dock takes the pointer.</summary>
    public bool Covers(Point p) => InZone(p);

    bool PointerInZone() =>
        Frostbar.Platform.Interop.Native.GetCursorPos(out var p) && PresentationSource.FromVisual(this) != null && InZone(PointFromScreen(new Point(p.X, p.Y)));

    /// <summary>The plate in DIPs inside the window.</summary>
    public Rect PlateRect => ToRect(Row.Box(_layout.PlateLeft, PlateNear, _layout.PlateWidth, PlateDepth));

    /// <summary>
    /// Rectangle of item <paramref name="i"/>'s slot in DIPs inside the window (icon only, bounce excluded); empty while
    /// the item is dragged. A gap in the row shifts the items after it.
    /// </summary>
    public Rect SlotRect(int i)
    {
        int slot = _slotOf == null ? i : i >= 0 && i < _slotOf.Length ? _slotOf[i] : -1;
        if (slot < 0 || slot >= _layout.Slots.Count) return Rect.Empty;
        var s = _layout.Slots[slot];
        double size = s.Kind == SlotKind.Separator ? BaseSize : s.Size;
        return ToRect(Row.Box(s.Left, IconNear, s.Size, size));
    }

    /// <summary>
    /// Where an icon added to the row before item <paramref name="index"/> would stand once it has come in (DIPs in the
    /// window), the pointer where it is now: a window being minimized into a picture of itself goes there (stage 7-2;
    /// the plate stays centred on the screen as it grows, so the place holds as the window is placed again).
    /// </summary>
    public Rect SlotRectIfAdded(int index)
    {
        var kinds = _kinds.ToList();
        index = Math.Clamp(index, 0, kinds.Count);
        kinds.Insert(index, SlotKind.Icon);
        var l = DockGeometry.Compute(kinds, Row.Length, BaseSize, Magnify ? MaxSize : BaseSize, _pointer, _strength, _scale, spacing: Spacing);
        var s = l.Slots[index];
        return ToRect(Row.Box(s.Left, IconNear, s.Size, s.Size));
    }

    // ---- drawing -----------------------------------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        _drawnZone = Zone();
        if (!_drawnZone.IsEmpty) dc.DrawRectangle(HitFill, null, _drawnZone);
    }

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
