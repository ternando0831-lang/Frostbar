namespace Frostbar.Core.MenuBar;

/// <summary>Where the user wants a tray icon: on the bar, in the » panel, or not at all.</summary>
public enum TrayPlace { Bar, Overflow, Hidden }

/// <summary>
/// The settings' names of the places (menuBar.trayIcons), in the order the settings page offers them: the one list
/// the page and the validator both use (a list in each lost a choice at the next start: 2026-10-15, the hot corners).
/// </summary>
public static class TrayPlaces
{
    public static readonly string[] Names = ["bar", "overflow", "hidden"];

    /// <summary>An icon the settings do not name, or name with a word they do not know, is on the bar.</summary>
    public static TrayPlace Parse(string? name) => Array.IndexOf(Names, name) is var i and >= 0 ? (TrayPlace)i : TrayPlace.Bar;

    public static string Name(TrayPlace place) => Names[(int)place];
}

/// <summary>A tray icon as the fit sees it: its key, where the user wants it and its width on the bar (DIPs).</summary>
public sealed record TrayFitIcon(string Key, TrayPlace Place, double Width);

/// <summary>
/// What fits: the tray icons on the bar and in the » panel (both in the bar's order), whether the » button shows,
/// and how many of the left titles (the app's name first, then its menus) are shown.
/// </summary>
public sealed record MenuBarLayout(IReadOnlyList<string> Bar, IReadOnlyList<string> Overflow, bool OverflowButton, int Titles);

/// <summary>
/// The menu bar's room shared out. The titles come first: tray icons go into the » panel before any title goes, the
/// ones nearest the titles first (those next to the system items stay). Titles go, the last menu first and the app's
/// name last, only when the mark, the status items and the » button alone do not leave room for them. Until
/// 2026-10-15 every icon sat in the right group, which took the width it wanted: the titles were pushed out first,
/// then the right end went off the screen.
/// </summary>
public static class MenuBarFit
{
    /// <param name="width">The bar's width.</param>
    /// <param name="mark">The system menu's mark at the left (always shown).</param>
    /// <param name="titles">The app's name and its menu titles, in order.</param>
    /// <param name="status">The status items right of the tray, the clock among them (always shown).</param>
    /// <param name="tray">The tray icons in the bar's order.</param>
    /// <param name="button">The » button.</param>
    /// <param name="gap">The least room kept between the titles and the right group.</param>
    public static MenuBarLayout Fit(double width, double mark, IReadOnlyList<double> titles, double status,
        IReadOnlyList<TrayFitIcon> tray, double button, double gap = 8)
    {
        var wanted = tray.Where(t => t.Place == TrayPlace.Bar).ToList();
        bool chosenOut = tray.Any(t => t.Place == TrayPlace.Overflow);
        double fixedWidth = mark + gap + status, titleWidth = titles.Sum();

        // The tray takes what the titles leave; icons go into the panel from the titles' side.
        double room = width - fixedWidth - titleWidth, icons = wanted.Sum(t => t.Width);
        int moved = 0;
        while (moved < wanted.Count && icons + (chosenOut || moved > 0 ? button : 0) > room)
            icons -= wanted[moved++].Width;
        bool anyOut = chosenOut || moved > 0;

        // Titles go only when the rest does not fit beside them.
        int shown = titles.Count;
        double right = icons + (anyOut ? button : 0);
        while (shown > 0 && fixedWidth + right + titleWidth > width) titleWidth -= titles[--shown];

        var bar = new List<string>();
        var panel = new List<string>();
        int seen = 0;
        foreach (var t in tray)
        {
            if (t.Place == TrayPlace.Hidden) continue;
            if (t.Place == TrayPlace.Bar && seen++ >= moved) bar.Add(t.Key);
            else panel.Add(t.Key);
        }
        return new MenuBarLayout(bar, panel, panel.Count > 0, shown);
    }
}

/// <summary>
/// Keys for remembering where a tray icon goes, the same after the app or the PC restarts: the icon's GUID when the
/// app gives one, else its exe's name and the icon's number (ManagedShell's own identifier holds the window's handle,
/// which changes with every start).
/// </summary>
public static class TrayKeys
{
    public static string Of(Guid guid, string exeName, uint uid) =>
        guid != Guid.Empty ? "guid:" + guid.ToString("D") : $"exe:{exeName.Trim().ToLowerInvariant()}:{uid}";
}
