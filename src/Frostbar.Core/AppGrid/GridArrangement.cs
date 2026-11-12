using Frostbar.Core.Settings;

namespace Frostbar.Core.AppGrid;

/// <summary>
/// A place of the app grid as it shows (stage 6 E1): an app (<see cref="App"/>, its index in the grid's apps) or a
/// folder of apps (<see cref="Apps"/>, in order). <see cref="Key"/> is the app's parsing name or the folder's Id.
/// </summary>
public sealed record GridTile(string Key, int App, string? FolderName, IReadOnlyList<int> Apps)
{
    public bool IsFolder => FolderName != null;
    /// <summary>The folder of hidden apps (<see cref="GridPlace.HiddenId"/>): last, not carried, not renamed.</summary>
    public bool IsHidden => IsFolder && Key == GridPlace.HiddenId;
    public static GridTile ForApp(int app, string key) => new(key, app, null, []);
    public static GridTile Folder(string id, string name, IReadOnlyList<int> apps) => new(id, -1, name, apps);
}

/// <summary>
/// The app grid's arrangement (Launchpad, stage 6 E1): the user's places in order, then the apps placed nowhere (new
/// ones) by name. An app pressed and moved is carried: the others make room where it would land, and let go on
/// another app's middle it makes a folder with it (on a folder's, it goes in). A folder's apps are taken out the same
/// way, and a folder left with one app becomes that app. Apps hidden (2026-10-24) are in one folder of their own, last
/// (<see cref="GridTile.IsHidden"/>). Everything here works on the tiles as they show; <see cref="Places"/> makes what is
/// kept of them.
/// </summary>
public static class GridArrangement
{
    /// <summary>
    /// The tiles: <paramref name="places"/> in order (apps installed now; folders with at least one of them), then the
    /// apps placed nowhere, in <paramref name="apps"/>' order (by name), then the hidden apps' folder named
    /// <paramref name="hiddenName"/> (blank: the name kept). An app placed twice shows once, at the first; a hidden one
    /// shows hidden.
    /// </summary>
    public static List<GridTile> Tiles(IReadOnlyList<GridApp> apps, IReadOnlyList<GridPlace> places, string hiddenName = "")
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < apps.Count; i++) index.TryAdd(apps[i].ParsingName, i);
        var used = new bool[apps.Count];
        var folders = new HashSet<string>(StringComparer.Ordinal);
        GridTile? hidden = null;
        if (places.FirstOrDefault(p => p.Kind == GridPlace.FolderKind && p.Id == GridPlace.HiddenId) is { } h)
        {
            folders.Add(h.Id);
            var members = Members(h);
            if (members.Count > 0) hidden = GridTile.Folder(h.Id, hiddenName.Length > 0 ? hiddenName : h.Name ?? "", members);
        }
        var tiles = new List<GridTile>();
        foreach (var p in places)
        {
            if (p.Kind == GridPlace.FolderKind)
            {
                if (!folders.Add(p.Id)) continue;
                var members = Members(p);
                if (members.Count == 0) continue;
                tiles.Add(GridTile.Folder(p.Id, p.Name ?? "", members));
            }
            else if (p.Kind == GridPlace.AppKind && index.TryGetValue(p.Id, out int i) && !used[i])
            {
                used[i] = true;
                tiles.Add(GridTile.ForApp(i, apps[i].ParsingName));
            }
        }
        for (int i = 0; i < apps.Count; i++)
            if (!used[i]) tiles.Add(GridTile.ForApp(i, apps[i].ParsingName));
        if (hidden != null) tiles.Add(hidden);
        return tiles;

        // A folder's installed apps not shown before, marked shown.
        List<int> Members(GridPlace p)
        {
            var members = new List<int>();
            foreach (var id in p.Apps ?? [])
                if (index.TryGetValue(id, out int m) && !used[m] && !members.Contains(m)) members.Add(m);
            foreach (int m in members) used[m] = true;
            return members;
        }
    }

    /// <summary>
    /// What is kept for <paramref name="tiles"/>: their places in order, then the places of <paramref name="before"/>
    /// whose apps are not installed now, as they were (an app that comes back returns to its folder, or after the
    /// others; the Start menu's list can lack an app for a while, as during an update).
    /// </summary>
    public static List<GridPlace> Places(IReadOnlyList<GridTile> tiles, IReadOnlyList<GridApp> apps, IReadOnlyList<GridPlace> before)
    {
        var installed = new HashSet<string>(apps.Select(a => a.ParsingName), StringComparer.OrdinalIgnoreCase);
        var oldFolders = new Dictionary<string, GridPlace>(StringComparer.Ordinal);
        foreach (var p in before) if (p.Kind == GridPlace.FolderKind) oldFolders.TryAdd(p.Id, p);
        var places = new List<GridPlace>();
        var shownFolders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in tiles)
        {
            if (!t.IsFolder) { places.Add(GridPlace.App(apps[t.App].ParsingName)); continue; }
            shownFolders.Add(t.Key);
            var ids = t.Apps.Select(i => apps[i].ParsingName).ToList();
            if (oldFolders.TryGetValue(t.Key, out var old))
                foreach (var id in old.Apps ?? [])
                    if (!installed.Contains(id) && !ids.Contains(id, StringComparer.OrdinalIgnoreCase)) ids.Add(id);
            places.Add(GridPlace.Folder(t.Key, t.FolderName!, ids));
        }
        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in before)
        {
            if (p.Kind == GridPlace.AppKind)
            {
                if (!installed.Contains(p.Id) && gone.Add(p.Id)) places.Add(GridPlace.App(p.Id));
            }
            else if (p.Kind == GridPlace.FolderKind && !shownFolders.Contains(p.Id) && p.Apps is { Count: > 0 } members && members.All(id => !installed.Contains(id)))
                places.Add(GridPlace.Folder(p.Id, p.Name ?? "", members));
        }
        return places;
    }

    /// <summary>The tiles without the one at <paramref name="at"/> (picked up to be carried).</summary>
    public static List<GridTile> Without(IReadOnlyList<GridTile> tiles, int at)
    {
        var list = tiles.ToList();
        list.RemoveAt(at);
        return list;
    }

    /// <summary>
    /// The tiles with the app at <paramref name="member"/> of the folder at <paramref name="folder"/> taken out (carried
    /// out of the open folder). The folder stays while the app is carried; <see cref="Settle"/> it once the app is down.
    /// </summary>
    public static List<GridTile> WithoutMember(IReadOnlyList<GridTile> tiles, int folder, int member)
    {
        var list = tiles.ToList();
        var apps = list[folder].Apps.ToList();
        apps.RemoveAt(member);
        list[folder] = list[folder] with { Apps = apps };
        return list;
    }

    /// <summary>
    /// <paramref name="tile"/> put at <paramref name="at"/> (0 to the count: before the tile there, or last; never after
    /// the hidden apps' folder).
    /// </summary>
    public static List<GridTile> Insert(IReadOnlyList<GridTile> tiles, int at, GridTile tile)
    {
        var list = tiles.ToList();
        list.Insert(Math.Clamp(at, 0, Room(list)), tile);
        return list;
    }

    /// <summary>The app <paramref name="app"/> put at <paramref name="at"/> among the apps of the folder at <paramref name="folder"/>.</summary>
    public static List<GridTile> InsertMember(IReadOnlyList<GridTile> tiles, int folder, int at, int app)
    {
        var list = tiles.ToList();
        var apps = list[folder].Apps.ToList();
        apps.Insert(Math.Clamp(at, 0, apps.Count), app);
        list[folder] = list[folder] with { Apps = apps };
        return list;
    }

    /// <summary>
    /// The app <paramref name="app"/> let go on the middle of the tile at <paramref name="onto"/>: on an app, the two make
    /// a folder in its place (that app first; <paramref name="id"/> and <paramref name="name"/> are the new folder's); on a
    /// folder, it goes in last.
    /// </summary>
    public static List<GridTile> Merge(IReadOnlyList<GridTile> tiles, int onto, int app, string id, string name)
    {
        var list = tiles.ToList();
        var target = list[onto];
        list[onto] = target.IsFolder ? target with { Apps = [.. target.Apps, app] } : GridTile.Folder(id, name, [target.App, app]);
        return list;
    }

    /// <summary>
    /// The folder <paramref name="key"/> after an app was taken out of it: left with one app it becomes that app, empty it
    /// goes (a folder holds two or more, as on macOS). The hidden apps' folder stays with one.
    /// </summary>
    public static List<GridTile> Settle(IReadOnlyList<GridTile> tiles, string key, IReadOnlyList<GridApp> apps)
    {
        var list = tiles.ToList();
        int i = list.FindIndex(t => t.IsFolder && t.Key == key);
        if (i < 0 || list[i].Apps.Count >= (list[i].IsHidden ? 1 : 2)) return list;
        if (list[i].Apps.Count == 1) list[i] = GridTile.ForApp(list[i].Apps[0], apps[list[i].Apps[0]].ParsingName);
        else list.RemoveAt(i);
        return list;
    }

    /// <summary>The folder at <paramref name="folder"/> named <paramref name="name"/> (blank: it keeps its name; the hidden apps' keeps its own).</summary>
    public static List<GridTile> Rename(IReadOnlyList<GridTile> tiles, int folder, string name)
    {
        var list = tiles.ToList();
        string clean = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length > 0 && !list[folder].IsHidden) list[folder] = list[folder] with { FolderName = clean };
        return list;
    }

    /// <summary>The folder at <paramref name="folder"/> taken apart: its apps in its place, in its order.</summary>
    public static List<GridTile> Disband(IReadOnlyList<GridTile> tiles, int folder, IReadOnlyList<GridApp> apps)
    {
        var list = tiles.ToList();
        var members = list[folder].Apps;
        list.RemoveAt(folder);
        list.InsertRange(folder, members.Select(i => GridTile.ForApp(i, apps[i].ParsingName)));
        return list;
    }

    /// <summary>
    /// The app <paramref name="app"/> hidden: taken from its place (or its folder) and put last in the hidden apps'
    /// folder, which is made last (named <paramref name="hiddenName"/>) when there is none.
    /// </summary>
    public static List<GridTile> Hide(IReadOnlyList<GridTile> tiles, int app, IReadOnlyList<GridApp> apps, string hiddenName)
    {
        if (HiddenApps(tiles).Contains(app)) return tiles.ToList();
        var list = Take(tiles, app, apps);
        int h = list.FindIndex(t => t.IsHidden);
        if (h < 0) list.Add(GridTile.Folder(GridPlace.HiddenId, hiddenName, [app]));
        else list[h] = list[h] with { Apps = [.. list[h].Apps, app] };
        return list;
    }

    /// <summary>
    /// The hidden app <paramref name="app"/> shown again: last of the places, before the hidden apps' folder (which goes
    /// when it was the last one), as an app installed since.
    /// </summary>
    public static List<GridTile> Unhide(IReadOnlyList<GridTile> tiles, int app, IReadOnlyList<GridApp> apps)
    {
        if (!HiddenApps(tiles).Contains(app)) return tiles.ToList();
        var list = Take(tiles, app, apps);
        list.Insert(Room(list), GridTile.ForApp(app, apps[app].ParsingName));
        return list;
    }

    /// <summary>The tiles without <paramref name="app"/>, wherever it is (a folder it leaves settles).</summary>
    static List<GridTile> Take(IReadOnlyList<GridTile> tiles, int app, IReadOnlyList<GridApp> apps)
    {
        var list = tiles.ToList();
        int at = list.FindIndex(t => !t.IsFolder && t.App == app);
        if (at >= 0) { list.RemoveAt(at); return list; }
        int folder = list.FindIndex(t => t.IsFolder && t.Apps.Contains(app));
        if (folder < 0) return list;
        list[folder] = list[folder] with { Apps = list[folder].Apps.Where(i => i != app).ToList() };
        return Settle(list, list[folder].Key, apps);
    }

    /// <summary>The apps in the hidden apps' folder.</summary>
    public static IReadOnlySet<int> HiddenApps(IReadOnlyList<GridTile> tiles) =>
        tiles.FirstOrDefault(t => t.IsHidden)?.Apps.ToHashSet() ?? [];

    /// <summary>
    /// Where the places that move can go among <paramref name="tiles"/> (0 to this): all of them but the hidden apps'
    /// folder, which stays last.
    /// </summary>
    public static int Room(IReadOnlyList<GridTile> tiles) => tiles.Count > 0 && tiles[^1].IsHidden ? tiles.Count - 1 : tiles.Count;

    /// <summary>
    /// A new folder's name: the words the two apps' names begin with alike ("Microsoft Word" and "Microsoft Excel":
    /// "Microsoft"), else <paramref name="untitled"/> (macOS names it by the apps' category, which Windows does not tell).
    /// </summary>
    public static string FolderName(string first, string second, string untitled)
    {
        var a = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var b = second.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int n = 0;
        while (n < a.Length && n < b.Length && string.Equals(a[n], b[n], StringComparison.CurrentCultureIgnoreCase)) n++;
        // Never the whole of a name (two apps called alike would make a folder named after one of them).
        n = Math.Min(n, Math.Min(a.Length, b.Length) - 1);
        string common = n > 0 ? string.Join(' ', a.Take(n)).TrimEnd('-', ':', ',', '–', ' ') : "";
        return common.Length >= 2 && !common.All(char.IsDigit) ? common : untitled;
    }
}

/// <summary>
/// Where a carried app goes on a page (Launchpad): the other tiles make a gap where it would land, and resting on
/// another tile's middle it would go into it (a folder made or filled). Positions count the tiles shown with the gap.
/// </summary>
public static class GridDrop
{
    /// <summary>How long the carried app rests on a tile's middle before letting go there puts it in (ms).</summary>
    public const int IntoDwellMs = 400;
    /// <summary>
    /// How long it rests in a cell before the others make room there (ms): passing over a tile's edge on the way to its
    /// middle does not push the tile away.
    /// </summary>
    public const int GapDwellMs = 200;
    /// <summary>How long it rests at the grid's side before the page turns (ms), and between turns while it stays.</summary>
    public const int EdgeDwellMs = 500, EdgeRepeatMs = 800;
    /// <summary>A tile's middle: this share of the icon's size off each side does not count.</summary>
    public const double MiddleInset = 0.2;

    /// <summary>The cell under (<paramref name="x"/>, <paramref name="y"/>) on a page (row by row), or -1 outside the cells.</summary>
    public static int CellAt(GridLayout l, double x, double y)
    {
        if (!l.Cells.Contains(x, y)) return -1;
        int col = Math.Clamp((int)((x - l.Cells.X) / l.CellWidth), 0, l.Columns - 1);
        int row = Math.Clamp((int)((y - l.Cells.Y) / l.CellHeight), 0, l.Rows - 1);
        return row * l.Columns + col;
    }

    /// <summary>
    /// The gap after the pointer moved to (<paramref name="x"/>, <paramref name="y"/>) on <paramref name="page"/>, and the
    /// tile whose middle is under it (an index into the <paramref name="rest"/> tiles shown around the gap; -1: none).
    /// Over a tile's middle the gap stays, so that the tile stays under the pointer (only with <paramref name="into"/>);
    /// elsewhere in a cell the gap moves there (past the last tile: after it, but no further than
    /// <paramref name="room"/>, before the tiles that stay last: the hidden apps' folder). Outside the cells nothing changes.
    /// </summary>
    public static (int gap, int over) Target(GridLayout l, double x, double y, int page, int gap, int rest, bool into = true, int room = int.MaxValue)
    {
        int cell = CellAt(l, x, y);
        if (cell < 0) return (gap, -1);
        int at = page * l.PerPage + cell;
        // The tile shown there (around the gap; none on the gap itself).
        int tile = at < gap ? at : at - 1;
        if (into && at != gap && tile >= 0 && tile < rest)
        {
            var (icon, _) = GridGeometry.Item(l, cell);
            double inset = icon.Width * MiddleInset;
            if (x >= icon.X + inset && x < icon.Right - inset && y >= icon.Y + inset && y < icon.Bottom - inset) return (gap, tile);
        }
        return (Math.Min(at, Math.Min(rest, room)), -1);
    }

    /// <summary>
    /// Which side's page the carried app asks for at <paramref name="x"/> (DIPs in the grid's window of
    /// <paramref name="width"/>): the outer half of the margin beside the cells. -1: the previous, 1: the next, 0: none.
    /// </summary>
    public static int Edge(GridLayout l, double x, double width)
    {
        double zone = l.Cells.X / 2;
        return x < zone ? -1 : x >= width - zone ? 1 : 0;
    }
}

/// <summary>An open folder's layout: its apps' cells, the panel around them, the name above it, and its pages.</summary>
public sealed record FolderLayout(GridLayout Apps, Box Panel, Box Title, int Pages);

/// <summary>
/// Where an open folder goes (Launchpad, the user's choice A, 2026-10-16): a dark panel across the grid's columns with
/// the folder's name large above it, its apps in the grid's columns and cells, as many rows as they need up to one fewer
/// than the grid's (more turn as pages, their dots under the apps), centred on the grid's cells. A closed folder shows
/// up to nine of its apps small, three across, on a translucent plate the size of an icon.
/// </summary>
public static class FolderGeometry
{
    public const double TitleHeight = 32, TitleGap = 10, Pad = 16, DotsRoom = 22, Radius = 18, TitleFontSize = 22;
    /// <summary>A closed folder's little icons: their size and the gap between them, as shares of the icon's size.</summary>
    public const double MiniShare = 0.215, MiniGapShare = 0.07;
    public const int Minis = 9;

    public static FolderLayout Compute(GridLayout grid, int count)
    {
        int columns = grid.Columns;
        int rows = Math.Clamp((count + columns - 1) / columns, 1, Math.Max(1, grid.Rows - 1));
        int pages = Math.Max(1, (count + columns * rows - 1) / (columns * rows));
        double width = columns * grid.CellWidth, height = rows * grid.CellHeight;
        double panelHeight = height + 2 * Pad + (pages > 1 ? DotsRoom : 0);
        double total = TitleHeight + TitleGap + panelHeight;
        double top = Math.Round(grid.Cells.Y + Math.Max(0, (grid.Cells.Height - total) / 2));
        var title = new Box(grid.Cells.X - Pad, top, width + 2 * Pad, TitleHeight);
        var panel = new Box(grid.Cells.X - Pad, top + TitleHeight + TitleGap, width + 2 * Pad, panelHeight);
        var cells = new Box(grid.Cells.X, panel.Y + Pad, width, height);
        var apps = new GridLayout(columns, rows, cells, grid.IconSize, grid.Field, cells.Bottom + (Pad + DotsRoom) / 2);
        return new FolderLayout(apps, panel, title, pages);
    }

    /// <summary>
    /// A closed folder's little icons for <paramref name="count"/> apps (the first nine), relative to an icon of
    /// <paramref name="size"/> (any unit; whole units, so give pixels for the picture): three across, centred.
    /// </summary>
    public static IReadOnlyList<Box> MiniIcons(double size, int count)
    {
        double mini = Math.Max(1, Math.Round(size * MiniShare)), gap = Math.Max(1, Math.Round(size * MiniGapShare));
        double start = Math.Round((size - (3 * mini + 2 * gap)) / 2);
        return Enumerable.Range(0, Math.Clamp(count, 0, Minis))
            .Select(i => new Box(start + i % 3 * (mini + gap), start + i / 3 * (mini + gap), mini, mini)).ToList();
    }
}
