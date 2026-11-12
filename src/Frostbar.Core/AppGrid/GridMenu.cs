using Frostbar.Core.Localization;
using Frostbar.Core.Menus;

namespace Frostbar.Core.AppGrid;

/// <summary>
/// What the app grid knows of an app when its right-click menu opens: hidden, kept in the Dock (null: there is no Dock
/// to keep it in), and whether it is a program of its own (a Start menu shortcut to an .exe: it runs as administrator)
/// with a file to show (packaged apps have neither).
/// </summary>
public sealed record GridAppFacts(bool Hidden, bool? InDock, bool Program, bool File);

/// <summary>
/// The app grid's right-click menus (the user's choice, 2026-10-24: open, run as administrator, the Dock, show the file,
/// hide; a folder's open, rename, ungroup). An app's: Open, Run as Administrator / Keep in Dock (Remove from Dock),
/// Show in the file manager / Hide (Unhide), the groups apart. A folder's: Open, Rename / Ungroup; the hidden apps'
/// folder: Open.
/// </summary>
public static class GridMenu
{
    public const string Open = "open", RunAsAdmin = "runas", Keep = "keep", Unkeep = "unkeep", Show = "show", Hide = "hide", Unhide = "unhide",
        Rename = "rename", Disband = "disband";

    /// <param name="fileManager">The name of the app that shows files (its "Show in …").</param>
    public static IReadOnlyList<MenuItem> ForApp(Strings s, GridAppFacts f, string fileManager)
    {
        var first = new List<MenuItem> { Item(Open, s["dock.open"]) };
        if (f.Program) first.Add(Item(RunAsAdmin, s["grid.runAsAdmin"]));
        var second = new List<MenuItem>();
        if (f.InDock is bool kept) second.Add(kept ? Item(Unkeep, s["dock.remove"]) : Item(Keep, s["dock.keep"]));
        if (f.File) second.Add(Item(Show, s.F("dock.showIn", fileManager)));
        var third = new List<MenuItem> { f.Hidden ? Item(Unhide, s["grid.unhide"]) : Item(Hide, s["grid.hide"]) };
        return Joined(first, second, third);
    }

    public static IReadOnlyList<MenuItem> ForFolder(Strings s, bool hidden) =>
        hidden ? [Item(Open, s["dock.open"])]
            : Joined([Item(Open, s["dock.open"]), Item(Rename, s["grid.rename"])], [Item(Disband, s["grid.disband"])]);

    /// <summary>
    /// Where a menu of <paramref name="width"/> × <paramref name="height"/> opened at the pointer (<paramref name="x"/>,
    /// <paramref name="y"/>) goes (DIPs on the screen; macOS's context menus): its top left corner there, to the left of
    /// the pointer when it would run off <paramref name="screen"/>'s right, above it off its bottom; 4 DIP inside it.
    /// </summary>
    public static (double Left, double Top) At(double x, double y, double width, double height, Box screen)
    {
        double left = x + width <= screen.Right - 4 ? x : x - width;
        double top = y + height <= screen.Bottom - 4 ? y : y - height;
        return (Math.Clamp(left, screen.X + 4, Math.Max(screen.X + 4, screen.Right - width - 4)),
                Math.Clamp(top, screen.Y + 4, Math.Max(screen.Y + 4, screen.Bottom - height - 4)));
    }

    static MenuItem Item(string id, string label) => new(id, label, null, MenuAction.Nothing);

    /// <summary>The groups that have items, a separator between them.</summary>
    static List<MenuItem> Joined(params List<MenuItem>[] groups)
    {
        var items = new List<MenuItem>();
        foreach (var g in groups.Where(g => g.Count > 0))
        {
            if (items.Count > 0) items.Add(MenuItem.Separator);
            items.AddRange(g);
        }
        return items;
    }
}
