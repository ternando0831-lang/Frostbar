using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Frostbar.App.Common;
using Frostbar.Core.AppGrid;
using Frostbar.Core.Shell;
using Frostbar.Platform.Dwm;
using Windows.UI.Composition;
using Color = System.Windows.Media.Color;
using ContainerVisual = Windows.UI.Composition.ContainerVisual;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.App.AppGrid;

/// <summary>
/// The app grid's pictures as composition visuals above its blurred plate (as the Dock's, ADR-007): an icon and a name
/// per place (an app, or a folder showing its first apps small) on a strip of pages laid side by side, which moves as a
/// whole to turn a page (an animation DWM runs, with no work on the UI thread per frame), the page dots, the press shade
/// and the keyboard selection. While an app is carried the places slide to make room for it. An open folder dims the
/// grid and shows its apps on a dark panel with its name above (stage 6 E1, look A). Icons are uploaded once at the
/// size they are drawn; names (white with a soft shadow) are drawn when their page is first needed. Positions are whole
/// pixels of the grid's window.
/// </summary>
public sealed class AppGridScene : IDisposable
{
    public const double PageMs = Motion.GridPage, OpenMs = Motion.GridOpen, CloseMs = Motion.GridClose, LabelFontSize = 12, MoveMs = Motion.GridMove, FolderOpenMs = Motion.FolderOpen, FolderCloseMs = Motion.FolderClose;
    const double LabelPadX = 4, LabelPadY = 3, DotSize = 7, DotHit = 9;
    /// <summary>A carried app's place, and its picture's place while it flies back: faint (Launchpad).</summary>
    const float FaintOpacity = 0.35f;

    /// <summary>A place's pictures: its icon and its name.</summary>
    abstract class Tile
    {
        public readonly SpriteVisual Icon, Label;
        public CompositionSurfaceBrush? IconBrush, LabelBrush;
        public abstract string Name { get; }
        protected Tile(SpriteVisual icon, SpriteVisual label) { Icon = icon; Label = label; }
    }

    sealed class AppItem(GridApp app, SpriteVisual icon, SpriteVisual label) : Tile(icon, label)
    {
        public GridApp App = app;
        public ImageSource? IconImage;
        public bool HasIcon;
        public override string Name => App.Name;
    }

    /// <summary>A closed folder: a translucent plate with its first nine apps small (look A), drawn as one picture.</summary>
    sealed class FolderItem(string key, SpriteVisual icon, SpriteVisual label) : Tile(icon, label)
    {
        public readonly string Key = key;
        public string FolderName = "";
        public IReadOnlyList<int> Apps = [];
        public bool Dirty = true;
        public override string Name => FolderName;
    }

    readonly SurfaceFactory _surfaces;
    readonly Compositor _c;
    readonly ContainerVisual _root, _pages, _dots;
    readonly SpriteVisual _shade, _selection, _into;
    /// <summary>The shade's mask: the pressed place's own icon (the mask brush takes its alpha).</summary>
    readonly CompositionSurfaceBrush _shadeMask;
    readonly CompositionEasingFunction _ease, _smooth;
    /// <summary>Places that move slide there (while an app is carried and as it lands).</summary>
    readonly ImplicitAnimationCollection _moves;
    readonly List<AppItem> _items = new();
    readonly Dictionary<string, FolderItem> _folders = new(StringComparer.Ordinal);
    readonly List<SpriteVisual> _dotSprites = new();
    readonly Typeface _labelFace = new(Materials.UiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    readonly Typeface _titleFace = new(Materials.UiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    IReadOnlyList<GridTile> _shown = [];
    readonly List<Tile> _shownTiles = new();
    Func<GridApp, ImageSource?> _icon = _ => null;
    GridLayout _layout;
    double _scale = 1, _widthDip;
    int _widthPx, _heightPx, _iconPx, _page, _pressed = -1, _selected = -1, _faint = -1;
    bool _moving;
    long _animatingUntil;
    CompositionSurfaceBrush? _placeholder, _dot, _selectionBrush, _plateMask, _intoBrush, _panelSource, _titleBrush;

    // The open folder: the grid dimmed, the panel with the name above it, its apps on a strip of pages clipped to the panel.
    readonly SpriteVisual _dim, _panel, _title;
    readonly ContainerVisual _folderLayer, _folderClip, _folderStrip, _folderDots;
    readonly CompositionNineGridBrush _panelBrush;
    readonly List<SpriteVisual> _folderDotSprites = new();
    readonly HashSet<int> _inFolder = new();
    FolderItem? _open;
    FolderLayout? _openLayout;
    IReadOnlyList<int> _openShown = [];
    int _folderPage, _folderFaint = -1, _folderPressed = -1;
    long _folderAnimatingUntil;
    System.Windows.Threading.DispatcherTimer? _folderClosing;
    Action? _folderClosed;

    public int Page => _page;
    public int Pages => AppGridModel.PageCount(_shown.Count, _layout.PerPage);
    public bool Animating => Environment.TickCount64 < _animatingUntil;
    public int LabelsMade => _items.Count(i => i.LabelBrush != null) + _folders.Values.Count(f => f.LabelBrush != null);
    public int IconsShown => _items.Count(i => i.HasIcon);
    /// <summary>Diagnostics: the time the last batch of names took to draw and upload, and how many.</summary>
    public (int count, double ms) LastLabels { get; private set; }
    /// <summary>The open folder's key, or null.</summary>
    public string? OpenFolder => _open?.Key;
    public int FolderPage => _folderPage;
    public int FolderPages => _openLayout == null ? 0 : AppGridModel.PageCount(_openShown.Count, _openLayout.Apps.PerPage);
    public bool FolderAnimating => Environment.TickCount64 < _folderAnimatingUntil || _folderClosing?.IsEnabled == true;

    public AppGridScene(SurfaceFactory surfaces, ContainerVisual parent, GridLayout layout, double scale, double widthDip, double heightDip)
    {
        _surfaces = surfaces;
        _c = surfaces.Compositor;
        _root = _c.CreateContainerVisual();
        _root.Opacity = 0; // hidden until it opens (Hidden): made at full strength, the first opening showed it so for a frame
        _pages = _c.CreateContainerVisual();
        _dots = _c.CreateContainerVisual();
        _selection = _c.CreateSpriteVisual();
        _selection.IsVisible = false;
        // Where a carried app would go in (a folder made or filled): a plate behind that place, like the folder's.
        _into = _c.CreateSpriteVisual();
        _into.IsVisible = false;
        // Pressed, the icon itself darkens (as in the Dock and on macOS), not a rounded square around it.
        _shadeMask = _c.CreateSurfaceBrush();
        _shadeMask.Stretch = CompositionStretch.Fill;
        _shadeMask.BitmapInterpolationMode = CompositionBitmapInterpolationMode.Linear;
        var shade = _c.CreateMaskBrush();
        shade.Source = _c.CreateColorBrush(Composition.Color(0x55, 0, 0, 0));
        shade.Mask = _shadeMask;
        _shade = _c.CreateSpriteVisual();
        _shade.Brush = shade;
        _shade.IsVisible = false;
        _pages.Children.InsertAtBottom(_into);
        _pages.Children.InsertAtTop(_selection);
        _pages.Children.InsertAtTop(_shade);
        _root.Children.InsertAtTop(_pages);
        _root.Children.InsertAtTop(_dots);
        // The folder's layer above the grid.
        _dim = _c.CreateSpriteVisual();
        _dim.Brush = _c.CreateColorBrush(Composition.Color(0x66, 0x10, 0x10, 0x14));
        _dim.IsVisible = false;
        _panelBrush = _c.CreateNineGridBrush();
        _panel = _c.CreateSpriteVisual();
        _panel.Brush = _panelBrush;
        _title = _c.CreateSpriteVisual();
        _folderLayer = _c.CreateContainerVisual();
        _folderLayer.IsVisible = false;
        _folderClip = _c.CreateContainerVisual();
        _folderClip.Clip = _c.CreateInsetClip();
        _folderStrip = _c.CreateContainerVisual();
        _folderDots = _c.CreateContainerVisual();
        _folderClip.Children.InsertAtTop(_folderStrip);
        _folderLayer.Children.InsertAtTop(_panel);
        _folderLayer.Children.InsertAtTop(_title);
        _folderLayer.Children.InsertAtTop(_folderClip);
        _folderLayer.Children.InsertAtTop(_folderDots);
        _root.Children.InsertAtTop(_dim);
        _root.Children.InsertAtTop(_folderLayer);
        parent.Children.InsertAtTop(_root);
        // Quick to leave, gentle to settle (a page turn, the opening zoom, a place making room).
        _ease = _c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.25f, 1f));
        _smooth = _c.CreateCubicBezierEasingFunction(new Vector2(1 / 3f, 0f), new Vector2(2 / 3f, 1f)); // closing: slow at both ends, as the blur fades
        _moves = _c.CreateImplicitAnimationCollection();
        var move = _c.CreateVector3KeyFrameAnimation();
        move.Target = "Offset";
        move.InsertExpressionKeyFrame(1f, "this.FinalValue", _ease);
        move.Duration = TimeSpan.FromMilliseconds(MoveMs);
        _moves["Offset"] = move;
        _layout = layout;
        SetLayout(layout, scale, widthDip, heightDip);
        surfaces.DeviceReplaced += Reload;
    }

    int Whole(double dip) => (int)Math.Round(dip * _scale);
    float Px(double dip) => (float)Math.Round(dip * _scale);

    IEnumerable<Tile> AllTiles => _items.Cast<Tile>().Concat(_folders.Values);

    /// <summary>A new size or scale: every picture is made again for it (an open folder closes first: the controller).</summary>
    public void SetLayout(GridLayout layout, double scale, double widthDip, double heightDip)
    {
        _layout = layout;
        _scale = scale;
        _widthDip = widthDip;
        _widthPx = Whole(widthDip);
        _heightPx = Whole(heightDip);
        _iconPx = Whole(layout.IconSize);
        _root.Size = new Vector2(_widthPx, _heightPx);
        _root.CenterPoint = new Vector3(_widthPx / 2f, _heightPx / 2f, 0);
        _dim.Size = new Vector2(_widthPx, _heightPx);
        MakeFixedPictures();
        foreach (var it in _items)
        {
            ReleaseLabel(it);
            ShowIcon(it, null, force: true);
        }
        foreach (var f in _folders.Values)
        {
            ReleaseLabel(f);
            f.Dirty = true;
        }
        RefreshIcons(_icon);
        Arrange(_shown);
    }

    void MakeFixedPictures()
    {
        _shadeMask.Surface = null; // it may show a picture released here (a press sets it again)
        foreach (var b in new[] { _placeholder, _dot, _selectionBrush, _plateMask, _intoBrush, _panelSource }) if (b != null) SurfaceFactory.Release(b);
        double r = _layout.IconSize * 0.22;
        _placeholder = _surfaces.Brush(Draw(_iconPx, _iconPx, dc =>
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)), null, Inset(_iconPx, _iconPx * 0.06), r * _scale, r * _scale)));
        int sel = Whole(_layout.IconSize + 16);
        _selectionBrush = _surfaces.Brush(Draw(sel, sel, dc =>
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF)), null, new Rect(0, 0, sel, sel), (r + 6) * _scale, (r + 6) * _scale)));
        int d = Whole(DotSize);
        _dot = _surfaces.Brush(Draw(d, d, dc => dc.DrawEllipse(Brushes.White, null, new Point(d / 2.0, d / 2.0), d / 2.0, d / 2.0)));
        // A folder's shape for the press shade: its plate, opaque.
        double plate = _iconPx * FolderCorner;
        _plateMask = _surfaces.Brush(Draw(_iconPx, _iconPx, dc => dc.DrawRoundedRectangle(Brushes.White, null, new Rect(0, 0, _iconPx, _iconPx), plate, plate)));
        int into = IntoPx;
        _intoBrush = _surfaces.Brush(Draw(into, into, dc => Plate(dc, into)));
        // The open folder's panel (candidate A): black 35 % with a white 15 % line, stretched by a nine-grid.
        int radius = (int)Math.Ceiling(FolderGeometry.Radius * _scale), n = 2 * radius + 4;
        double line = Math.Max(1, Math.Round(_scale));
        _panelSource = _surfaces.Brush(Draw(n, n, dc =>
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)), new Pen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), line),
                new Rect(line / 2, line / 2, n - line, n - line), radius - line / 2, radius - line / 2)));
        _panelBrush.Source = _panelSource;
        _panelBrush.SetInsets(radius + 2);
        _shade.Size = new Vector2(_iconPx, _iconPx);
        _selection.Brush = _selectionBrush;
        _selection.Size = new Vector2(sel, sel);
        _into.Brush = _intoBrush;
        _into.Size = new Vector2(into, into);
        foreach (var s in _dotSprites.Concat(_folderDotSprites)) { s.Brush = _dot; s.Size = new Vector2(d, d); }
    }

    /// <summary>A folder's plate is rounded as the apps' icons about are (candidate A: 0.225 of its size).</summary>
    const double FolderCorner = 0.225;
    int IntoPx => (int)Math.Round(_iconPx * 1.24);

    /// <summary>The folder's plate (look A): white 25 % with a white 19 % line, <paramref name="px"/> square.</summary>
    void Plate(DrawingContext dc, int px)
    {
        double line = Math.Max(1, Math.Round(_scale)), r = px * FolderCorner;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), line),
            new Rect(line / 2, line / 2, px - line, px - line), r, r);
    }

    static Rect Inset(int px, double by) => new(by, by, Math.Max(0, px - 2 * by), Math.Max(0, px - 2 * by));

    SpriteVisual NewSprite()
    {
        var s = _c.CreateSpriteVisual();
        s.IsVisible = false;
        if (_moving) s.ImplicitAnimations = _moves;
        _pages.Children.InsertAtTop(s);
        return s;
    }

    /// <summary>The press shade stays above the places, the folder's highlight below them.</summary>
    void KeepOrder()
    {
        if (_shade.Parent == _pages) { _pages.Children.Remove(_shade); _pages.Children.InsertAtTop(_shade); }
        _pages.Children.Remove(_into);
        _pages.Children.InsertAtBottom(_into);
    }

    /// <summary>The apps, in grid order. Pictures of apps that stay are kept; their icons come from <paramref name="icon"/>.</summary>
    public void SetApps(IReadOnlyList<GridApp> apps, Func<GridApp, ImageSource?> icon)
    {
        _icon = icon;
        var old = _items.ToDictionary(i => i.App.ParsingName, StringComparer.OrdinalIgnoreCase);
        var next = new List<AppItem>(apps.Count);
        foreach (var app in apps)
        {
            if (old.Remove(app.ParsingName, out var it))
            {
                if (it.App.Name != app.Name) ReleaseLabel(it);
                it.App = app;
            }
            else
            {
                it = new AppItem(app, NewSprite(), NewSprite());
                it.Icon.Size = new Vector2(_iconPx, _iconPx);
            }
            next.Add(it);
        }
        foreach (var gone in old.Values) Dispose(gone);
        _items.Clear();
        _items.AddRange(next);
        _inFolder.Clear(); // the controller closes an open folder before
        foreach (var f in _folders.Values) f.Dirty = true; // its apps' indices changed
        KeepOrder();
        RefreshIcons(icon);
        _shown = [];
        _shownTiles.Clear();
    }

    /// <summary>The folders there are now (all of them, shown or not): pictures made, updated or let go.</summary>
    public void SetFolders(IEnumerable<GridTile> folders)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in folders)
        {
            if (!t.IsFolder) continue;
            keep.Add(t.Key);
            if (!_folders.TryGetValue(t.Key, out var f))
            {
                f = new FolderItem(t.Key, NewSprite(), NewSprite());
                f.Icon.Size = new Vector2(_iconPx, _iconPx);
                _folders.Add(t.Key, f);
            }
            if (f.FolderName != t.FolderName) { ReleaseLabel(f); f.FolderName = t.FolderName!; }
            if (!f.Apps.SequenceEqual(t.Apps)) { f.Apps = t.Apps; f.Dirty = true; }
        }
        foreach (var gone in _folders.Values.Where(f => !keep.Contains(f.Key) && f != _open).ToList())
        {
            _folders.Remove(gone.Key);
            Dispose(gone);
        }
        KeepOrder();
        DrawFolders();
    }

    void Dispose(Tile it)
    {
        (it.Icon.Parent as ContainerVisual)?.Children.Remove(it.Icon);
        (it.Label.Parent as ContainerVisual)?.Children.Remove(it.Label);
        if (it.IconBrush != null) ReleaseIcon(it.IconBrush);
        it.IconBrush = null;
        ReleaseLabel(it);
        it.Icon.Dispose();
        it.Label.Dispose();
    }

    /// <summary>Icons that have loaded since (the ones already shown are not uploaded again); the folders show them too.</summary>
    public void RefreshIcons(Func<GridApp, ImageSource?> icon)
    {
        _icon = icon;
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            var image = icon(it.App);
            if (ReferenceEquals(image, it.IconImage) && it.Icon.Brush != null) continue;
            ShowIcon(it, image);
            foreach (var f in _folders.Values) if (!f.Dirty && f.Apps.Take(FolderGeometry.Minis).Contains(i)) f.Dirty = true;
        }
        DrawFolders();
    }

    void ShowIcon(AppItem it, ImageSource? image, bool force = false)
    {
        if (!force && ReferenceEquals(image, it.IconImage) && it.Icon.Brush != null) return;
        it.IconImage = image;
        if (it.IconBrush != null) { ReleaseIcon(it.IconBrush); it.IconBrush = null; }
        it.HasIcon = image is BitmapSource;
        if (image is BitmapSource b)
        {
            it.IconBrush = _surfaces.Brush(SurfaceFactory.Scaled(b, _iconPx, _iconPx));
            it.Icon.Brush = it.IconBrush;
        }
        else it.Icon.Brush = _placeholder;
        it.Icon.Size = new Vector2(_iconPx, _iconPx);
        if (PressedTile() == it) ShadeFor(it); // its icon loaded while it was pressed
    }

    /// <summary>Draws the folders' pictures that changed (their apps, or their apps' icons).</summary>
    void DrawFolders()
    {
        foreach (var f in _folders.Values.Where(f => f.Dirty))
        {
            f.Dirty = false;
            if (f.IconBrush != null) ReleaseIcon(f.IconBrush);
            f.IconBrush = _surfaces.Brush(FolderImage(f));
            f.Icon.Brush = f.IconBrush;
            f.Icon.Size = new Vector2(_iconPx, _iconPx);
        }
    }

    /// <summary>A closed folder (look A): the plate with its first nine apps small, three across.</summary>
    BitmapSource FolderImage(FolderItem f)
    {
        int px = _iconPx;
        var minis = FolderGeometry.MiniIcons(px, f.Apps.Count);
        return Draw(px, px, dc =>
        {
            Plate(dc, px);
            for (int i = 0; i < minis.Count; i++)
            {
                var b = minis[i];
                var rect = new Rect(b.X, b.Y, b.Width, b.Height);
                if (f.Apps[i] >= 0 && f.Apps[i] < _items.Count && _items[f.Apps[i]].IconImage is BitmapSource image) dc.DrawImage(image, rect);
                else dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)), null, rect, b.Width * 0.22, b.Width * 0.22);
            }
        }, highQuality: true);
    }

    /// <summary>Gives an icon's surface back; the shade lets go of it first.</summary>
    void ReleaseIcon(CompositionSurfaceBrush brush)
    {
        if (ReferenceEquals(_shadeMask.Surface, brush.Surface)) _shadeMask.Surface = null;
        SurfaceFactory.Release(brush);
    }

    Tile? TileOf(GridTile t) =>
        t.IsFolder ? _folders.GetValueOrDefault(t.Key) : t.App >= 0 && t.App < _items.Count ? _items[t.App] : null;

    /// <summary>The place shown at <paramref name="position"/>, or null.</summary>
    Tile? TileAt(int position) => position >= 0 && position < _shownTiles.Count ? _shownTiles[position] : null;

    Tile? PressedTile() => _folderPressed >= 0 ? FolderTileAt(_folderPressed) : TileAt(_pressed);

    /// <summary>The press shade takes the shape of the place's icon as it is shown now (a folder: its plate).</summary>
    void ShadeFor(Tile it) => _shadeMask.Surface = it is FolderItem ? _plateMask?.Surface : (it.Icon.Brush as CompositionSurfaceBrush)?.Surface;

    /// <summary>
    /// Shows these places in this order, page after page (the arrangement, a search's apps, or the places around a carried
    /// app's gap), the one at <paramref name="faint"/> faint. The apps of an open folder stay on its panel.
    /// </summary>
    public void Arrange(IReadOnlyList<GridTile> shown, int faint = -1)
    {
        Faint(-1); // by the old order
        _shown = shown;
        _shownTiles.Clear();
        var visible = new HashSet<Tile>();
        for (int k = 0; k < shown.Count; k++)
        {
            var it = TileOf(shown[k]);
            if (it == null || !shown[k].IsFolder && _inFolder.Contains(shown[k].App)) { _shownTiles.Add(null!); continue; }
            _shownTiles.Add(it);
            visible.Add(it);
            var (icon, label) = GridGeometry.Item(_layout, k % _layout.PerPage);
            Place(it, OnStrip(k, icon.X, icon.Y), OnStrip(k, label.X - LabelPadX, label.Y - LabelPadY));
        }
        for (int i = 0; i < _items.Count; i++)
            if (!visible.Contains(_items[i]) && !_inFolder.Contains(i)) Hide(_items[i]);
        foreach (var f in _folders.Values) if (!visible.Contains(f)) Hide(f);
        DrawFolders();
        EnsureLabels(_page); // a folder made or renamed just now has no name drawn yet
        MakeDots();
        Faint(faint);
        Press(-1);
        Select(-1);
        Into(-1);
    }

    /// <summary>Puts a place's icon and name at these offsets; one that was hidden comes there at once, not sliding.</summary>
    void Place(Tile it, Vector3 icon, Vector3 label)
    {
        bool appears = !it.Icon.IsVisible;
        if (appears && _moving) { it.Icon.ImplicitAnimations = null; it.Label.ImplicitAnimations = null; }
        it.Icon.Offset = icon;
        it.Label.Offset = label;
        if (appears && _moving) { it.Icon.ImplicitAnimations = _moves; it.Label.ImplicitAnimations = _moves; }
        it.Icon.IsVisible = true;
        it.Label.IsVisible = it.LabelBrush != null;
    }

    static void Hide(Tile it)
    {
        it.Icon.IsVisible = it.Label.IsVisible = false;
        it.Icon.Opacity = it.Label.Opacity = 1;
    }

    /// <summary>
    /// Where a point (<paramref name="x"/>, <paramref name="y"/>, DIPs on its page) of the place at
    /// <paramref name="position"/> goes on the strip of pages, in pixels: page n starts n window widths to the right.
    /// </summary>
    Vector3 OnStrip(int position, double x, double y) => new(position / _layout.PerPage * _widthPx + Px(x), Px(y), 0);

    /// <summary>The boxes of the place at <paramref name="position"/> in the window as it is now (DIPs), or null when it is not on this page.</summary>
    public (Box icon, Box label)? ItemBoxes(int position)
    {
        if (position < 0 || position >= _shown.Count || position / _layout.PerPage != _page) return null;
        return GridGeometry.Item(_layout, position % _layout.PerPage);
    }

    /// <summary>The place at <paramref name="position"/> at the given page's place, wherever the strip is (DIPs): where a carried picture lands.</summary>
    public (Box icon, Box label) BoxesOnItsPage(int position) => GridGeometry.Item(_layout, Math.Max(0, position) % _layout.PerPage);

    /// <summary>Slides the places that move (on while an app is carried, until it has landed).</summary>
    public void Moving(bool on)
    {
        if (_moving == on) return;
        _moving = on;
        foreach (var it in AllTiles) it.Icon.ImplicitAnimations = it.Label.ImplicitAnimations = on ? _moves : null;
    }

    /// <summary>The place at <paramref name="position"/> faint (a carried app's place; -1: none).</summary>
    public void Faint(int position)
    {
        if (_faintTile is { } was) was.Icon.Opacity = was.Label.Opacity = 1;
        _faint = position;
        _faintTile = TileAt(position);
        if (_faintTile is { } it) it.Icon.Opacity = it.Label.Opacity = FaintOpacity;
    }

    Tile? _faintTile, _folderFaintTile;

    /// <summary>The plate behind the place at <paramref name="position"/> a carried app would go into (-1: none).</summary>
    public void Into(int position)
    {
        if (TileAt(position) == null) { _into.IsVisible = false; return; }
        var (icon, _) = GridGeometry.Item(_layout, position % _layout.PerPage);
        float grow = (IntoPx - _iconPx) / 2f;
        var at = OnStrip(position, icon.X, icon.Y);
        _into.Offset = new Vector3(at.X - grow, at.Y - grow, 0);
        _into.IsVisible = true;
    }

    void MakeDots()
    {
        int pages = Pages;
        MakeDots(_dots, _dotSprites, pages > 1 ? pages : 0);
        var xs = GridGeometry.Dots(_widthDip, pages);
        float d = Whole(DotSize);
        for (int i = 0; i < _dotSprites.Count; i++)
            _dotSprites[i].Offset = new Vector3(Px(xs[i]) - (float)Math.Round(d / 2), Px(_layout.DotsY) - (float)Math.Round(d / 2), 0);
        PaintDots();
    }

    void MakeDots(ContainerVisual parent, List<SpriteVisual> sprites, int want)
    {
        while (sprites.Count < want)
        {
            var s = _c.CreateSpriteVisual();
            s.Brush = _dot;
            s.Size = new Vector2(Whole(DotSize), Whole(DotSize));
            parent.Children.InsertAtTop(s);
            sprites.Add(s);
        }
        while (sprites.Count > want)
        {
            var s = sprites[^1];
            parent.Children.Remove(s);
            s.Dispose();
            sprites.RemoveAt(sprites.Count - 1);
        }
    }

    void PaintDots()
    {
        for (int i = 0; i < _dotSprites.Count; i++) _dotSprites[i].Opacity = i == _page ? 0.92f : 0.35f;
    }

    /// <summary>The page whose dot is under (<paramref name="x"/>, <paramref name="y"/>) (DIPs), or -1.</summary>
    public int DotAt(double x, double y)
    {
        if (_dotSprites.Count == 0 || Math.Abs(y - _layout.DotsY) > DotHit + 2) return -1;
        var xs = GridGeometry.Dots(_widthDip, Pages);
        for (int i = 0; i < xs.Length; i++) if (Math.Abs(x - xs[i]) <= DotHit) return i;
        return -1;
    }

    /// <summary>Diagnostics: the dots' centres in the window (DIPs).</summary>
    public IReadOnlyList<Point> DotCentres() =>
        _dotSprites.Count == 0 ? [] : GridGeometry.Dots(_widthDip, Pages).Select(x => new Point(x, _layout.DotsY)).ToList();

    /// <summary>Turns to <paramref name="page"/>, sliding the strip there unless <paramref name="animate"/> is false.</summary>
    public void GoTo(int page, bool animate)
    {
        _page = Math.Clamp(page, 0, Pages - 1);
        PaintDots();
        EnsureLabels(_page);
        Slide(_pages, -_page * _widthPx, animate);
        _animatingUntil = animate ? Environment.TickCount64 + (long)PageMs : 0;
    }

    /// <summary>A strip of pages to <paramref name="x"/> pixels: at once, or turning there from wherever it is.</summary>
    void Slide(ContainerVisual strip, float x, bool animate)
    {
        strip.StopAnimation("Offset.X");
        if (!animate) { strip.Offset = new Vector3(x, 0, 0); return; }
        // No key frame at 0: it starts where the strip is (after a drag, or in the middle of another turn).
        var a = _c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1f, x, _ease);
        a.Duration = TimeSpan.FromMilliseconds(PageMs);
        strip.StartAnimation("Offset.X", a);
    }

    /// <summary>The strip follows a drag: <paramref name="dxDip"/> from the current page.</summary>
    public void Follow(double dxDip)
    {
        _pages.StopAnimation("Offset.X");
        _animatingUntil = 0;
        _pages.Offset = new Vector3(-_page * _widthPx + Px(dxDip), 0, 0);
    }

    /// <summary>The closing: the pictures grow a little and fade out (as Launchpad's do), from wherever they are.</summary>
    public void Closing()
    {
        _root.StopAnimation("Opacity");
        _root.StopAnimation("Scale");
        var fade = _c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f, _smooth);
        fade.Duration = TimeSpan.FromMilliseconds(CloseMs);
        _root.StartAnimation("Opacity", fade);
        if (ReduceMotion.Calms("gridZoom")) return; // reduced motion: it fades where it is
        var zoom = _c.CreateVector3KeyFrameAnimation();
        zoom.InsertKeyFrame(1f, new Vector3(1.06f, 1.06f, 1f), _smooth);
        zoom.Duration = TimeSpan.FromMilliseconds(CloseMs);
        _root.StartAnimation("Scale", zoom);
    }

    /// <summary>
    /// The grid hidden: its pictures at nothing. Left at full strength while hidden, they showed so for one frame as
    /// the grid opened again, before its blur and before the fade in (2026-10-18, the user's "一回パッと消える",
    /// grid-appear: the icons over the bare screen 76 ms after the click, gone at 92 ms, then fading in).
    /// </summary>
    public void Hidden()
    {
        _root.StopAnimation("Opacity");
        _root.StopAnimation("Scale");
        _root.Opacity = 0;
        _root.Scale = Vector3.One;
    }

    /// <summary>
    /// The opening: the pictures zoom in from a little larger and fade in (none without animations). They stand where
    /// the fade begins until it runs (a frame before it showed them whole).
    /// </summary>
    public void Opening(bool animate)
    {
        _root.StopAnimation("Opacity");
        _root.StopAnimation("Scale");
        bool still = animate && ReduceMotion.Calms("gridZoom"); // reduced motion: it fades in where it is
        _root.Opacity = animate ? 0 : 1;
        _root.Scale = animate && !still ? new Vector3(1.06f, 1.06f, 1f) : Vector3.One;
        if (!animate) return;
        var fade = _c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, _ease);
        fade.Duration = TimeSpan.FromMilliseconds(OpenMs);
        if (still) { _root.StartAnimation("Opacity", fade); return; }
        var zoom = _c.CreateVector3KeyFrameAnimation();
        zoom.InsertKeyFrame(0f, new Vector3(1.06f, 1.06f, 1f));
        zoom.InsertKeyFrame(1f, Vector3.One, _ease);
        zoom.Duration = TimeSpan.FromMilliseconds(OpenMs);
        _root.StartAnimation("Opacity", fade);
        _root.StartAnimation("Scale", zoom);
    }

    /// <summary>Darkens the icon at <paramref name="position"/> while it is pressed (-1: none).</summary>
    public void Press(int position)
    {
        _pressed = position;
        _folderPressed = -1;
        if (TileAt(position) is not { } it) { LetGo(); return; }
        MoveShade(_pages);
        var (icon, _) = GridGeometry.Item(_layout, position % _layout.PerPage);
        ShadeFor(it);
        _shade.Offset = OnStrip(position, icon.X, icon.Y);
        _shade.IsVisible = true;
        GiveTo(it);
    }

    // ---- a press giving (2026-10-23, the user's "A 沈む＋隣へ光る") ------------------------------------------------------
    // The icon pressed sinks to Core's PressGive.Icon and its shade comes in over 60 ms (it was there at once); let go,
    // it springs back past its size (DWM's key frames, sampled from the table's spring) and its shade goes over 120 ms.

    /// <summary>A press gives (animations on, motion not reduced): set by the controller as each press begins.</summary>
    public bool Gives { get; set; }

    Tile? _given;

    /// <summary>The icon given now and how far (diagnostics "grid-state": its scale as last asked; DWM moves it).</summary>
    public string? GivenName => _given?.Name;

    void GiveTo(Tile it)
    {
        if (_given != null && !ReferenceEquals(_given, it)) LetGo();
        _given = it;
        if (!Gives) { _shade.StopAnimation("Opacity"); _shade.Opacity = 1; return; }
        var centre = new Vector3(it.Icon.Size.X / 2, it.Icon.Size.Y / 2, 0);
        it.Icon.CenterPoint = centre;
        _shade.CenterPoint = centre;
        float k = (float)PressGive.Icon;
        foreach (var v in new Windows.UI.Composition.Visual[] { it.Icon, _shade })
        {
            var a = _c.CreateVector3KeyFrameAnimation();
            a.InsertKeyFrame(1f, new Vector3(k, k, 1), _pressEase);
            a.Duration = TimeSpan.FromMilliseconds(Motion.BouncePress);
            v.StartAnimation("Scale", a);
        }
        var shade = _c.CreateScalarKeyFrameAnimation();
        shade.InsertKeyFrame(0f, 0f);
        shade.InsertKeyFrame(1f, 1f, _pressEase);
        shade.Duration = TimeSpan.FromMilliseconds(Motion.PressShadeIn);
        _shade.StartAnimation("Opacity", shade);
        Given++;
    }

    void LetGo()
    {
        var it = _given;
        _given = null;
        if (it == null || !Gives) { _shade.IsVisible = false; return; }
        var linear = _c.CreateLinearEasingFunction();
        foreach (var v in new Windows.UI.Composition.Visual[] { it.Icon, _shade })
        {
            // From where it is, back on the spring past its size.
            var a = _c.CreateVector3KeyFrameAnimation();
            a.InsertExpressionKeyFrame(0f, "this.StartingValue", linear);
            foreach (var (t, s) in SpringKeys)
                if (t > 0) a.InsertExpressionKeyFrame(t, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"this.StartingValue + (Vector3(1, 1, 1) - this.StartingValue) * {s}"), linear);
            a.Duration = TimeSpan.FromMilliseconds(Motion.BounceRelease);
            v.StartAnimation("Scale", a);
        }
        var shade = _c.CreateScalarKeyFrameAnimation();
        shade.InsertKeyFrame(1f, 0f, _c.CreateCubicBezierEasingFunction(new Vector2(1 / 3f, 0f), new Vector2(2 / 3f, 1f))); // smoothstep
        shade.Duration = TimeSpan.FromMilliseconds(Motion.PressShadeOut);
        _shade.StartAnimation("Opacity", shade);
    }

    /// <summary>Presses that gave (diagnostics).</summary>
    public int Given { get; private set; }

    static readonly (float at, float value)[] SpringKeys = Motion.Sample(t => Motion.Spring(t, PressGive.Damping), 24);
    CompositionEasingFunction? _pressEaseFn;
    CompositionEasingFunction _pressEase => _pressEaseFn ??= _c.CreateCubicBezierEasingFunction(new Vector2(0.33f, 1f), new Vector2(0.68f, 1f)); // cubic out

    void MoveShade(ContainerVisual parent)
    {
        if (_shade.Parent == parent) return;
        (_shade.Parent as ContainerVisual)?.Children.Remove(_shade);
        parent.Children.InsertAtTop(_shade);
    }

    /// <summary>The picture the place at <paramref name="position"/> shows now (its icon, a folder's picture, or the placeholder), or null.</summary>
    public CompositionBrush? IconOf(int position) => TileAt(position)?.Icon.Brush;

    /// <summary>The picture <paramref name="tile"/> shows now (an app's icon or the placeholder, a folder's picture), or null.</summary>
    public CompositionBrush? IconOf(GridTile tile) => TileOf(tile)?.Icon.Brush;

    /// <summary>Diagnostics: <paramref name="tile"/>'s name is drawn and shows.</summary>
    public bool Named(GridTile tile) => TileOf(tile) is { LabelBrush: not null } it && it.Label.IsVisible;

    /// <summary>Highlights the place at <paramref name="position"/> chosen with the keyboard (-1: none).</summary>
    public void Select(int position)
    {
        _selected = position;
        if (position < 0 || position >= _shown.Count) { _selection.IsVisible = false; return; }
        var (icon, _) = GridGeometry.Item(_layout, position % _layout.PerPage);
        _selection.Offset = OnStrip(position, icon.X - 8, icon.Y - 8);
        _selection.IsVisible = true;
    }

    // ---- the open folder -------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens the folder <paramref name="folder"/> laid out as <paramref name="layout"/>: the grid dims, the panel and the
    /// name come with its apps (zooming in a little and fading, unless <paramref name="animate"/> is false).
    /// </summary>
    public void Open(GridTile folder, FolderLayout layout, bool animate)
    {
        if (_folderClosing?.IsEnabled == true) FinishClosingFolder();
        if (!_folders.TryGetValue(folder.Key, out var f)) return;
        _open = f;
        _openLayout = layout;
        var panel = layout.Panel;
        _panel.Offset = new Vector3(Px(panel.X), Px(panel.Y), 0);
        _panel.Size = new Vector2(Whole(panel.Width), Whole(panel.Height));
        _folderClip.Offset = _panel.Offset;
        _folderClip.Size = _panel.Size;
        _folderStrip.Offset = Vector3.Zero;
        SetTitle(folder.FolderName!);
        _folderPage = 0;
        ArrangeFolder(folder.Apps);
        _dim.IsVisible = _folderLayer.IsVisible = true;
        _title.IsVisible = true;
        foreach (var v in new Windows.UI.Composition.Visual[] { _dim, _folderLayer }) { v.StopAnimation("Opacity"); v.Opacity = 1; }
        _folderLayer.StopAnimation("Scale");
        _folderLayer.Scale = Vector3.One;
        _folderAnimatingUntil = 0;
        FadeGrid(GridUnderFolder, animate ? FolderOpenMs : 0);
        if (!animate) return;
        _folderLayer.CenterPoint = new Vector3(Px(panel.X + panel.Width / 2), Px(panel.Y + panel.Height / 2), 0);
        var fade = _c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, _ease);
        fade.Duration = TimeSpan.FromMilliseconds(FolderOpenMs);
        var zoom = _c.CreateVector3KeyFrameAnimation();
        zoom.InsertKeyFrame(0f, FolderZoom());
        zoom.InsertKeyFrame(1f, Vector3.One, _ease);
        zoom.Duration = TimeSpan.FromMilliseconds(FolderOpenMs);
        _dim.StartAnimation("Opacity", fade);
        _folderLayer.StartAnimation("Opacity", fade);
        _folderLayer.StartAnimation("Scale", zoom);
        _folderAnimatingUntil = Environment.TickCount64 + (long)FolderOpenMs;
    }

    /// <summary>
    /// The open folder laid out again for the apps it has now (one taken out or shown again while it is open): its panel
    /// and its name take their new places; <see cref="ArrangeFolder"/> then places its apps on it.
    /// </summary>
    public void Relayout(FolderLayout layout, string name)
    {
        if (_open == null) return;
        _openLayout = layout;
        var panel = layout.Panel;
        _panel.Offset = new Vector3(Px(panel.X), Px(panel.Y), 0);
        _panel.Size = new Vector2(Whole(panel.Width), Whole(panel.Height));
        _folderClip.Offset = _panel.Offset;
        _folderClip.Size = _panel.Size;
        _folderLayer.CenterPoint = new Vector3(Px(panel.X + panel.Width / 2), Px(panel.Y + panel.Height / 2), 0);
        SetTitle(name);
    }

    /// <summary>The folder's panel's size where it opens from and closes to: a little smaller (reduced motion: its own, it only fades).</summary>
    static Vector3 FolderZoom() => ReduceMotion.Calms("folderZoom") ? Vector3.One : new Vector3(0.96f, 0.96f, 1f);

    /// <summary>The open folder's name's width as drawn (DIPs): a click on it edits it.</summary>
    public double TitleWidth { get; private set; }

    /// <summary>
    /// The grid's own places while a folder is open: faint, so that the panel's apps read on their own (macOS fades them
    /// away; 2026-10-16: through the panel's 35 % the places behind read as clearly as the folder's own).
    /// </summary>
    const float GridUnderFolder = 0.12f;

    /// <summary>The grid's pages and dots fade to <paramref name="to"/> (at once for 0 ms).</summary>
    void FadeGrid(float to, double ms)
    {
        foreach (var v in new Windows.UI.Composition.Visual[] { _pages, _dots })
        {
            v.StopAnimation("Opacity");
            if (ms <= 0) { v.Opacity = to; continue; }
            var a = _c.CreateScalarKeyFrameAnimation();
            a.InsertKeyFrame(1f, to, to < 1 ? _ease : _smooth);
            a.Duration = TimeSpan.FromMilliseconds(ms);
            v.StartAnimation("Opacity", a);
        }
    }

    /// <summary>The folder's name drawn above its panel (a new name, after renaming).</summary>
    public void SetTitle(string name)
    {
        if (_openLayout == null) return;
        if (_titleBrush != null) SurfaceFactory.Release(_titleBrush);
        var measured = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _titleFace, FolderGeometry.TitleFontSize, Brushes.White, _scale)
        {
            MaxTextWidth = Math.Max(1, _openLayout.Title.Width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
        };
        TitleWidth = Math.Min(measured.WidthIncludingTrailingWhitespace, _openLayout.Title.Width);
        var image = TitleImage(name, _openLayout.Title.Width);
        _titleBrush = _surfaces.Brush(image);
        _title.Brush = _titleBrush;
        _title.Size = new Vector2(image.PixelWidth, image.PixelHeight);
        var t = _openLayout.Title;
        _title.Offset = new Vector3(Px(t.X) - Whole(LabelPadX), Px(t.Y + (t.Height - image.PixelHeight / _scale) / 2), 0);
    }

    /// <summary>The name hidden while it is being edited (the edit box shows it).</summary>
    public void HideTitle(bool hidden) => _title.IsVisible = !hidden && _open != null;

    /// <summary>
    /// The open folder's apps in this order on its panel (a carried app's place among them faint at
    /// <paramref name="faint"/>): apps that came in move onto the panel, apps taken out go back to the grid's layer.
    /// </summary>
    public void ArrangeFolder(IReadOnlyList<int> apps, int faint = -1)
    {
        if (_openLayout == null) return;
        var l = _openLayout.Apps;
        foreach (int i in _inFolder.Where(i => !apps.Contains(i)).ToList())
        {
            _inFolder.Remove(i);
            if (i < _items.Count) MoveTo(_items[i], _pages, hide: true);
        }
        var panel = _openLayout.Panel;
        int panelPx = Whole(panel.Width);
        for (int k = 0; k < apps.Count; k++)
        {
            int i = apps[k];
            if (i < 0 || i >= _items.Count) continue;
            var it = _items[i];
            if (_inFolder.Add(i)) MoveTo(it, _folderStrip, hide: false);
            var (icon, label) = GridGeometry.Item(l, k % l.PerPage);
            float x0 = k / l.PerPage * panelPx - Px(panel.X), y0 = -Px(panel.Y);
            Place(it, new Vector3(x0 + Px(icon.X), y0 + Px(icon.Y), 0), new Vector3(x0 + Px(label.X - LabelPadX), y0 + Px(label.Y - LabelPadY), 0));
            it.Icon.Opacity = it.Label.Opacity = 1;
        }
        _openShown = apps;
        _folderFaintTile = null;
        FaintMember(faint);
        EnsureFolderLabels();
        int pages = FolderPages;
        MakeDots(_folderDots, _folderDotSprites, pages > 1 ? pages : 0);
        var xs = GridGeometry.Dots(panel.Width, pages);
        float d = Whole(DotSize);
        for (int i = 0; i < _folderDotSprites.Count; i++)
            _folderDotSprites[i].Offset = new Vector3(Px(panel.X + xs[i]) - (float)Math.Round(d / 2), Px(l.DotsY) - (float)Math.Round(d / 2), 0);
        _folderPage = Math.Clamp(_folderPage, 0, Math.Max(0, pages - 1));
        PaintFolderDots();
        PressMember(-1);
    }

    void MoveTo(AppItem it, ContainerVisual parent, bool hide)
    {
        foreach (var v in new[] { it.Icon, it.Label })
        {
            (v.Parent as ContainerVisual)?.Children.Remove(v);
            parent.Children.InsertAtTop(v);
        }
        if (hide) Hide(it);
        if (parent == _pages) KeepOrder();
        else if (_shade.Parent == _folderStrip) { _folderStrip.Children.Remove(_shade); _folderStrip.Children.InsertAtTop(_shade); }
    }

    void PaintFolderDots()
    {
        for (int i = 0; i < _folderDotSprites.Count; i++) _folderDotSprites[i].Opacity = i == _folderPage ? 0.92f : 0.35f;
    }

    /// <summary>The open folder's app shown at <paramref name="position"/> on its panel, or null.</summary>
    Tile? FolderTileAt(int position) =>
        position >= 0 && position < _openShown.Count && _openShown[position] >= 0 && _openShown[position] < _items.Count ? _items[_openShown[position]] : null;

    /// <summary>The boxes of the folder's app at <paramref name="position"/> (DIPs in the window), or null when not on its page.</summary>
    public (Box icon, Box label)? FolderItemBoxes(int position)
    {
        if (_openLayout is not { } f || position < 0 || position >= _openShown.Count || position / f.Apps.PerPage != _folderPage) return null;
        return GridGeometry.Item(f.Apps, position % f.Apps.PerPage);
    }

    /// <summary>The folder's page whose dot is under (<paramref name="x"/>, <paramref name="y"/>) (DIPs), or -1.</summary>
    public int FolderDotAt(double x, double y)
    {
        if (_openLayout is not { } f || _folderDotSprites.Count == 0 || Math.Abs(y - f.Apps.DotsY) > DotHit + 2) return -1;
        var xs = GridGeometry.Dots(f.Panel.Width, FolderPages);
        for (int i = 0; i < xs.Length; i++) if (Math.Abs(x - (f.Panel.X + xs[i])) <= DotHit) return i;
        return -1;
    }

    /// <summary>Turns the open folder to <paramref name="page"/>.</summary>
    public void GoToFolderPage(int page, bool animate)
    {
        if (_openLayout == null) return;
        _folderPage = Math.Clamp(page, 0, Math.Max(0, FolderPages - 1));
        PaintFolderDots();
        Slide(_folderStrip, -_folderPage * Whole(_openLayout.Panel.Width), animate);
    }

    /// <summary>The folder's apps follow a drag: <paramref name="dxDip"/> from its current page.</summary>
    public void FolderFollow(double dxDip)
    {
        if (_openLayout == null) return;
        _folderStrip.StopAnimation("Offset.X");
        _folderStrip.Offset = new Vector3(-_folderPage * Whole(_openLayout.Panel.Width) + Px(dxDip), 0, 0);
    }

    /// <summary>Darkens the folder's app at <paramref name="position"/> while it is pressed (-1: none).</summary>
    public void PressMember(int position)
    {
        _folderPressed = position;
        if (position >= 0) _pressed = -1;
        if (FolderTileAt(position) is not { } it || _openLayout is not { } f) { if (_pressed < 0) LetGo(); return; }
        MoveShade(_folderStrip);
        var (icon, _) = GridGeometry.Item(f.Apps, position % f.Apps.PerPage);
        ShadeFor(it);
        _shade.Offset = new Vector3(position / f.Apps.PerPage * Whole(f.Panel.Width) - Px(f.Panel.X) + Px(icon.X), Px(icon.Y) - Px(f.Panel.Y), 0);
        _shade.IsVisible = true;
        GiveTo(it);
    }

    /// <summary>The folder's app at <paramref name="position"/> faint (-1: none).</summary>
    public void FaintMember(int position)
    {
        if (_folderFaintTile is { } was) was.Icon.Opacity = was.Label.Opacity = 1;
        _folderFaint = position;
        _folderFaintTile = FolderTileAt(position);
        if (_folderFaintTile is { } it) it.Icon.Opacity = it.Label.Opacity = FaintOpacity;
    }

    /// <summary>
    /// Closes the open folder: the panel fades and shrinks a little with the dimming, then its apps go back to the
    /// grid's layer and <paramref name="closed"/> runs (at once without <paramref name="animate"/>).
    /// </summary>
    public void Close(bool animate, Action? closed = null)
    {
        if (_open == null) { closed?.Invoke(); return; }
        _folderClosed = closed;
        PressMember(-1);
        if (!animate) { FinishClosingFolder(); return; }
        FadeGrid(1, FolderCloseMs);
        var fade = _c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f, _smooth);
        fade.Duration = TimeSpan.FromMilliseconds(FolderCloseMs);
        var zoom = _c.CreateVector3KeyFrameAnimation();
        zoom.InsertKeyFrame(1f, FolderZoom(), _smooth);
        zoom.Duration = TimeSpan.FromMilliseconds(FolderCloseMs);
        _dim.StartAnimation("Opacity", fade);
        _folderLayer.StartAnimation("Opacity", fade);
        _folderLayer.StartAnimation("Scale", zoom);
        _folderClosing ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(FolderCloseMs + 20), System.Windows.Threading.DispatcherPriority.Normal,
            (_, _) => FinishClosingFolder(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _folderClosing.Stop();
        _folderClosing.Start();
    }

    void FinishClosingFolder()
    {
        _folderClosing?.Stop();
        foreach (int i in _inFolder.ToList()) if (i < _items.Count) MoveTo(_items[i], _pages, hide: true);
        _inFolder.Clear();
        MoveShade(_pages);
        _shade.IsVisible = false;
        _dim.IsVisible = _folderLayer.IsVisible = false;
        FadeGrid(1, 0);
        _open = null;
        _openLayout = null;
        _openShown = [];
        _folderFaint = _folderPressed = -1;
        _folderFaintTile = null;
        var closed = _folderClosed;
        _folderClosed = null;
        closed?.Invoke();
    }

    // ---- names --------------------------------------------------------------------------------------------------

    /// <summary>Draws the names of the pages around <paramref name="page"/> that are not drawn yet.</summary>
    public void EnsureLabels(int page)
    {
        int from = Math.Max(0, (page - 1) * _layout.PerPage), to = Math.Min(_shownTiles.Count, (page + 2) * _layout.PerPage);
        MakeLabels(Enumerable.Range(from, Math.Max(0, to - from)).Select(k => _shownTiles[k]).Where(t => t != null), int.MaxValue);
    }

    void EnsureFolderLabels() => MakeLabels(_openShown.Where(i => i >= 0 && i < _items.Count).Select(i => (Tile)_items[i]), int.MaxValue);

    /// <summary>Draws up to <paramref name="max"/> names not drawn yet (all places, a few at a time while idle); returns how many are left.</summary>
    public int MakeSomeLabels(int max) => MakeLabels(AllTiles, max);

    int MakeLabels(IEnumerable<Tile> tiles, int max)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int made = 0;
        foreach (var it in tiles)
        {
            if (it.LabelBrush != null) continue;
            if (made == max) break;
            var image = LabelImage(it.Name);
            it.LabelBrush = _surfaces.Brush(image);
            it.Label.Brush = it.LabelBrush;
            it.Label.Size = new Vector2(image.PixelWidth, image.PixelHeight);
            it.Label.IsVisible = it.Icon.IsVisible;
            made++;
        }
        if (made > 0) LastLabels = (made, sw.Elapsed.TotalMilliseconds);
        return AllTiles.Count(x => x.LabelBrush == null);
    }

    void ReleaseLabel(Tile it)
    {
        if (it.LabelBrush == null) return;
        it.Label.Brush = null;
        SurfaceFactory.Release(it.LabelBrush);
        it.LabelBrush = null;
        it.Label.IsVisible = false;
    }

    /// <summary>
    /// A name as the grid shows it: 12 DIP white text centred over the cell's width, cut short with an ellipsis, with a
    /// soft dark shadow so it reads over any wallpaper. The bitmap has a small margin for the shadow.
    /// </summary>
    BitmapSource LabelImage(string name)
    {
        var (_, box) = GridGeometry.Item(_layout, 0);
        return TextImage(name, _labelFace, LabelFontSize, box.Width, GridGeometry.LabelHeight);
    }

    /// <summary>An open folder's name (look A): 22 DIP SemiBold, white with the names' shadow, centred over the panel.</summary>
    BitmapSource TitleImage(string name, double width) => TextImage(name, _titleFace, FolderGeometry.TitleFontSize, width, FolderGeometry.TitleHeight);

    BitmapSource TextImage(string text, Typeface face, double size, double width, double height)
    {
        double w = width + 2 * LabelPadX, h = height + 2 * LabelPadY;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, Brushes.White, _scale)
        {
            MaxTextWidth = Math.Max(1, width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center,
        };
        var v = new ShadowedVisual(new DropShadowEffect
        {
            Color = Colors.Black, Direction = 270, ShadowDepth = Math.Max(1, _scale), BlurRadius = 3 * _scale, Opacity = 0.55, RenderingBias = RenderingBias.Quality,
        });
        using (var dc = v.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(_scale, _scale));
            dc.DrawText(ft, new Point(LabelPadX, LabelPadY + Math.Max(0, (height - ft.Height) / 2)));
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(Whole(w), Whole(h), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>A drawing with an effect (Visual.VisualEffect is only settable from a subclass).</summary>
    sealed class ShadowedVisual : DrawingVisual
    {
        public ShadowedVisual(Effect effect) => VisualEffect = effect;
    }

    /// <summary>A picture drawn by WPF into a bitmap of <paramref name="w"/> × <paramref name="h"/> pixels.</summary>
    static BitmapSource Draw(int w, int h, Action<DrawingContext> draw, bool highQuality = false)
    {
        var dv = new DrawingVisual();
        if (highQuality) RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen()) draw(dc);
        var rtb = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>The GPU device was replaced: every picture is uploaded again.</summary>
    void Reload()
    {
        _shadeMask.Surface = null;
        _placeholder = _dot = _selectionBrush = _plateMask = _intoBrush = _panelSource = null; // their surfaces went with the old device
        MakeFixedPictures();
        foreach (var it in _items)
        {
            it.IconBrush = null;
            it.LabelBrush = null;
            it.Label.Brush = null;
            it.Label.IsVisible = false;
            ShowIcon(it, it.IconImage, force: true);
        }
        foreach (var f in _folders.Values)
        {
            f.IconBrush = null;
            f.LabelBrush = null;
            f.Label.Brush = null;
            f.Label.IsVisible = false;
            f.Dirty = true;
        }
        DrawFolders();
        if (_open != null && _openLayout != null)
        {
            _titleBrush = null;
            SetTitle(_open.FolderName);
            EnsureFolderLabels();
        }
        EnsureLabels(_page);
    }

    public void Dispose()
    {
        _surfaces.DeviceReplaced -= Reload;
        _folderClosing?.Stop();
        foreach (var it in AllTiles.ToList()) Dispose(it);
        _items.Clear();
        _folders.Clear();
        _shadeMask.Surface = null;
        foreach (var b in new[] { _placeholder, _dot, _selectionBrush, _plateMask, _intoBrush, _panelSource, _titleBrush }) if (b != null) SurfaceFactory.Release(b);
        foreach (var s in _dotSprites.Concat(_folderDotSprites)) s.Dispose();
        _dots.Children.RemoveAll();
        _folderDots.Children.RemoveAll();
        _folderStrip.Children.RemoveAll();
        _folderClip.Children.RemoveAll();
        _folderLayer.Children.RemoveAll();
        _pages.Children.RemoveAll();
        _root.Children.RemoveAll();
        _root.Dispose();
    }
}
