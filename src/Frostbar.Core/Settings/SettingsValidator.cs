using Frostbar.Core.AppGrid;
using Frostbar.Core.HotCorners;
using Frostbar.Core.MenuBar;
using Frostbar.Core.Menus;

namespace Frostbar.Core.Settings;

/// <summary>Brings loaded settings back into range. Returns what it changed so the app can log it.</summary>
public static class SettingsValidator
{
    public static readonly string[] Languages = ["system", "ja", "en"];
    public static readonly string[] Themes = ["system", "light", "dark"];
    public static readonly string[] Materials = ["auto", "translucent", "transparent", "solid"];
    public static readonly string[] Toggles = ["system", "on", "off"];
    public static readonly string[] Hour24 = ["auto", "on", "off"];
    public static readonly string[] FileSources = ["auto", "everything", "windowsSearch"];
    public static readonly string[] Modifiers = ["none", "ctrl", "alt", "shift", "win"];
    public static readonly string[] LogLevels = ["error", "warn", "info", "debug"];
    public static readonly string[] HideStrategies = ["hide", "transparent"];
    public static readonly string[] PinKinds = ["appsFolder", "path", DockPin.SeparatorKind];
    /// <summary>What the apps used lately can be (a separator is only ever kept).</summary>
    public static readonly string[] AppKinds = ["appsFolder", "path"];
    public static readonly string[] FileManagers = ["explorer", "custom"];
    /// <summary>The edges the Dock can sit on (the settings page's choices, in order).</summary>
    public static readonly string[] DockPositions = ["left", "bottom", "right"];
    public static readonly string[] DarkSchedules = ["off", "sun", "clock"];

    public static List<string> Normalize(AppSettings s)
    {
        var fixes = new List<string>();

        s.General ??= new(); s.Appearance ??= new(); s.MenuBar ??= new(); s.MenuBar.Clock ??= new(); s.MenuBar.TrayIcons ??= new();
        // Lists the Dock reads at start (a hand-edited null in one stopped every start: the watchdog gave up after 3).
        s.Dock ??= new(); s.Dock.Pins ??= new(); s.Dock.Recent ??= new(); s.Dock.Folders ??= new();
        s.Search ??= new(); s.AppGrid ??= new();
        // The shortcuts are registered at start (a hand-edited null threw there at every start).
        s.HotCorners ??= new(); s.HotCorners.Shortcuts ??= new(); s.Advanced ??= new();
        s.Hud ??= new(); s.Wallpaper ??= new();
        s.Accessibility ??= new(); s.Window ??= new(); s.Sound ??= new();

        s.General.Language = OneOf(s.General.Language, Languages, "system", "general.language", fixes);
        s.General.FileManager = OneOf(s.General.FileManager, FileManagers, "explorer", "general.fileManager", fixes);
        s.General.FileManagerPath ??= "";
        if (s.General.FileManager == "custom" && s.General.FileManagerPath.Trim().Length == 0)
        {
            s.General.FileManager = "explorer";
            fixes.Add("general.fileManager 'custom' without a program -> 'explorer'");
        }
        s.Appearance.Theme = OneOf(s.Appearance.Theme, Themes, "system", "appearance.theme", fixes);
        s.Appearance.Material = OneOf(s.Appearance.Material, Materials, AppearanceSettings.DefaultMaterial, "appearance.material", fixes);
        s.Appearance.ReduceMotion = OneOf(s.Appearance.ReduceMotion, Toggles, "system", "appearance.reduceMotion", fixes);
        s.Appearance.DarkSchedule = OneOf(s.Appearance.DarkSchedule, DarkSchedules, "off", "appearance.darkSchedule", fixes);
        s.Appearance.GlassTint = Step(s.Appearance.GlassTint, Shell.GlassTints.Min, Shell.GlassTints.Max, Shell.GlassTints.StepSize, Shell.GlassTints.Default, "appearance.glassTint", fixes);
        if (s.Appearance.City is not { } city || city.Length > 0 && !Time.Cities.All.Any(c => c.Key == city))
        {
            fixes.Add($"appearance.city '{s.Appearance.City}' -> '' (the time zone's)");
            s.Appearance.City = "";
        }
        s.Appearance.DarkFrom = ClockTime(s.Appearance.DarkFrom, "19:00", "appearance.darkFrom", fixes);
        s.Appearance.DarkUntil = ClockTime(s.Appearance.DarkUntil, "07:00", "appearance.darkUntil", fixes);
        s.Appearance.Accent = OneOf(s.Appearance.Accent, Shell.AccentColors.Names, Shell.AccentColors.System, "appearance.accent", fixes);
        s.Appearance.ScrollBars = OneOf(s.Appearance.ScrollBars, Shell.ScrollBarChoices.Shows, Shell.ScrollBarChoices.WhenScrolling, "appearance.scrollBars", fixes);
        s.Appearance.ScrollBarClick = OneOf(s.Appearance.ScrollBarClick, Shell.ScrollBarChoices.Clicks, Shell.ScrollBarChoices.NextPage, "appearance.scrollBarClick", fixes);

        // 24 or 28 DIP: whole pixels at 100/125/150/175/200% (26 would be 32.5 px at 125%).
        if (s.MenuBar.Height is not (24 or 28)) { fixes.Add($"menuBar.height {s.MenuBar.Height} -> 24"); s.MenuBar.Height = 24; }
        s.MenuBar.Clock.Hour24 = OneOf(s.MenuBar.Clock.Hour24, Hour24, "auto", "menuBar.clock.hour24", fixes);
        foreach (var (key, place) in s.MenuBar.TrayIcons.ToList())
        {
            if (!string.IsNullOrWhiteSpace(key) && TrayPlaces.Names.Contains(place)) continue;
            s.MenuBar.TrayIcons.Remove(key);
            fixes.Add($"menuBar.trayIcons.{key} '{place}' dropped (the icon goes on the bar)");
        }
        // The status items shown and their order (stage 5-1): known ones, each once; none listed is the first order.
        var items = MenuBarItems.Clean(s.MenuBar.Items);
        if (s.MenuBar.Items == null || !items.SequenceEqual(s.MenuBar.Items)) { fixes.Add($"menuBar.items -> {string.Join(",", items)}"); s.MenuBar.Items = items; }
        // Settings written before an item existed (schema 1, stage 5-1) show it as its default does: listing only the
        // items shown, they cannot tell a new one from one the user hid.
        if (s.SchemaVersion < 2)
        {
            foreach (var key in MenuBarItems.NewInSchema2) s.MenuBar.Items = MenuBarItems.Set(s.MenuBar.Items, key, true);
            fixes.Add($"schemaVersion {s.SchemaVersion} -> 2: menuBar.items shows {string.Join(",", MenuBarItems.NewInSchema2)} (new)");
        }
        // Settings written before macOS's defaults (schema 3, 2026-11-01, the user's "切り替える"): written in full, they
        // cannot tell the old defaults from a choice, and take the new ones (a solid background stays).
        if (s.SchemaVersion < 3)
        {
            if (s.Appearance.Material is "auto" or "translucent") s.Appearance.Material = AppearanceSettings.DefaultMaterial;
            s.Dock.ClickActiveAppMinimizes = false;
            fixes.Add($"schemaVersion {s.SchemaVersion} -> 3: appearance.material '{s.Appearance.Material}', dock.clickActiveAppMinimizes off (macOS's defaults)");
        }
        // Settings written before the Dock's new room (schema 4, 2026-11-08, the user's "A アイコン 40 のまま"): the old
        // defaults take the new ones; a value the user moved stays.
        if (s.SchemaVersion < 4)
        {
            if (s.Dock.Spacing == 8) s.Dock.Spacing = 14;
            if (s.Dock.Padding == 10) s.Dock.Padding = 14;
            fixes.Add($"schemaVersion {s.SchemaVersion} -> 4: dock.spacing {s.Dock.Spacing}, dock.padding {s.Dock.Padding} (Apple's picture's room)");
        }
        s.SchemaVersion = Math.Max(s.SchemaVersion, AppSettings.CurrentSchema);
        // The system menu's Recent Items (stage 5-2), read when it opens.
        s.MenuBar.RecentApps ??= new();
        int apps = s.MenuBar.RecentApps.Count;
        s.MenuBar.RecentApps.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Id) || !AppKinds.Contains(p.Kind));
        if (s.MenuBar.RecentApps.Count != apps) fixes.Add($"menuBar.recentApps: dropped {apps - s.MenuBar.RecentApps.Count} invalid");
        if (s.MenuBar.RecentApps.Count > Menus.RecentItems.Max) s.MenuBar.RecentApps.RemoveRange(Menus.RecentItems.Max, s.MenuBar.RecentApps.Count - Menus.RecentItems.Max);
        if (!Menus.RecentItems.Counts.Contains(s.MenuBar.RecentCount))
        {
            fixes.Add($"menuBar.recentCount {s.MenuBar.RecentCount} -> {Menus.RecentItems.Count}");
            s.MenuBar.RecentCount = Menus.RecentItems.Count;
        }
        s.MenuBar.AutoHide = OneOf(s.MenuBar.AutoHide, [.. MenuBarItems.AutoHides], "fullScreen", "menuBar.autoHide", fixes);

        s.Dock.IconSize = Step(s.Dock.IconSize, 32, 128, 4, 40, "dock.iconSize", fixes);
        s.Dock.MagnifiedSize = Step(s.Dock.MagnifiedSize, s.Dock.IconSize, 160, 4, Math.Max(80, s.Dock.IconSize), "dock.magnifiedSize", fixes);
        s.Dock.Spacing = Step(s.Dock.Spacing, 0, 24, 2, 14, "dock.spacing", fixes);
        s.Dock.Padding = Step(s.Dock.Padding, 2, 24, 2, 14, "dock.padding", fixes);
        s.Dock.Position = OneOf(s.Dock.Position, DockPositions, "bottom", "dock.position", fixes);
        int before = s.Dock.Pins.Count;
        // A separator's Id only tells it apart: two with the same one (a hand edit) would be one item to the Dock.
        var separators = new HashSet<string>();
        s.Dock.Pins.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Id) || !PinKinds.Contains(p.Kind)
                                   || p.Kind == DockPin.SeparatorKind && !separators.Add(p.Id));
        if (s.Dock.Pins.Count != before) fixes.Add($"dock.pins: dropped {before - s.Dock.Pins.Count} invalid");
        // Read at start like the pins (a null in them stopped the start).
        before = s.Dock.Recent.Count;
        s.Dock.Recent.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Id) || !AppKinds.Contains(p.Kind));
        if (s.Dock.Recent.Count != before) fixes.Add($"dock.recent: dropped {before - s.Dock.Recent.Count} invalid");
        before = s.Dock.Folders.Count;
        s.Dock.Folders.RemoveAll(string.IsNullOrWhiteSpace);
        if (s.Dock.Folders.Count != before) fixes.Add($"dock.folders: dropped {before - s.Dock.Folders.Count} empty");

        // The keys the settings page can record (stage 6-1), or none ("none": the menu bar's magnifier still opens the search).
        if (s.Search.Hotkey != SearchSettings.NoHotkey && (Shortcut.Parse(s.Search.Hotkey) is not { } searchKeys || !HotCornerActions.KeysAllowed(searchKeys)))
        {
            fixes.Add($"search.hotkey '{s.Search.Hotkey}' -> {SearchSettings.DefaultHotkey}");
            s.Search.Hotkey = SearchSettings.DefaultHotkey;
        }
        s.Search.FileSource = OneOf(s.Search.FileSource, FileSources, "auto", "search.fileSource", fixes);
        // The kinds left out of the answer (2026-10-22): known ones, each once.
        var hidden = (s.Search.Hidden ?? []).Where(c => Search.SearchCategories.Names.Contains(c)).Distinct().ToList();
        if (s.Search.Hidden == null || !hidden.SequenceEqual(s.Search.Hidden)) { fixes.Add($"search.hidden -> {string.Join(",", hidden)}"); s.Search.Hidden = hidden; }
        if (s.Search.WebSearchUrl is not { } url || !url.Contains("{q}") || !(url.StartsWith("https://") || url.StartsWith("http://")))
        {
            fixes.Add("search.webSearchUrl invalid -> Google");
            s.Search.WebSearchUrl = "https://www.google.com/search?q={q}";
        }

        s.AppGrid.Columns = Step(s.AppGrid.Columns, 4, 12, 1, 7, "appGrid.columns", fixes);
        s.AppGrid.Rows = Step(s.AppGrid.Rows, 3, 8, 1, 5, "appGrid.rows", fixes);
        if (s.AppGrid.IconSize != 0) s.AppGrid.IconSize = Step(s.AppGrid.IconSize, GridGeometry.MinIcon, GridGeometry.MaxIcon, 4, 0, "appGrid.iconSize", fixes);
        // Read when the grid is made (a hand-edited null would stop it): places of known kinds with an Id, folders with apps
        // and an Id of their own.
        s.AppGrid.Arrangement ??= new();
        before = s.AppGrid.Arrangement.Count;
        var folderIds = new HashSet<string>(StringComparer.Ordinal);
        s.AppGrid.Arrangement.RemoveAll(p =>
        {
            if (p == null || string.IsNullOrWhiteSpace(p.Id)) return true;
            if (p.Kind == GridPlace.AppKind) return false;
            if (p.Kind != GridPlace.FolderKind) return true;
            p.Apps = (p.Apps ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            p.Name ??= "";
            return p.Apps.Count == 0 || !folderIds.Add(p.Id);
        });
        if (s.AppGrid.Arrangement.Count != before) fixes.Add($"appGrid.arrangement: dropped {before - s.AppGrid.Arrangement.Count} invalid");

        s.HotCorners.DwellMs = Step(s.HotCorners.DwellMs, 0, 1000, 10, 250, "hotCorners.dwellMs", fixes);
        string[] corners = HotCornerActions.ForCorners;
        s.HotCorners.TopLeft = OneOf(s.HotCorners.TopLeft, corners, "none", "hotCorners.topLeft", fixes);
        s.HotCorners.TopRight = OneOf(s.HotCorners.TopRight, corners, "none", "hotCorners.topRight", fixes);
        s.HotCorners.BottomLeft = OneOf(s.HotCorners.BottomLeft, corners, "appGrid", "hotCorners.bottomLeft", fixes);
        s.HotCorners.BottomRight = OneOf(s.HotCorners.BottomRight, corners, "overview", "hotCorners.bottomRight", fixes);
        s.HotCorners.Modifier = OneOf(s.HotCorners.Modifier, Modifiers, "none", "hotCorners.modifier", fixes);
        CleanShortcuts(s.HotCorners.Shortcuts, Shortcut.Parse(s.Search.Hotkey), fixes);

        s.Advanced.LogLevel = OneOf(s.Advanced.LogLevel, LogLevels, "info", "advanced.logLevel", fixes);
        s.Advanced.TaskbarHideStrategy = OneOf(s.Advanced.TaskbarHideStrategy, HideStrategies, "hide", "advanced.taskbarHideStrategy", fixes);
        return fixes;
    }

    /// <summary>
    /// Keeps the shortcuts the settings page could have set: a known action, keys it can read, with Ctrl, Alt or Win
    /// (<see cref="HotCornerActions.KeysAllowed"/>), each keys for one action only (the first in the file keeps them),
    /// and not the search's (<paramref name="searchKeys"/>: two registrations of the same keys, the second fails).
    /// </summary>
    static void CleanShortcuts(Dictionary<string, string> shortcuts, Shortcut? searchKeys, List<string> fixes)
    {
        var taken = new HashSet<Shortcut>();
        if (searchKeys is { } search) taken.Add(search);
        foreach (var (action, text) in shortcuts.ToList())
        {
            string? why = !HotCornerActions.ForKeys.Contains(action) ? "unknown action"
                : Shortcut.Parse(text) is not { } keys ? "keys that cannot be read"
                : !HotCornerActions.KeysAllowed(keys) ? "keys without Ctrl, Alt or Win"
                : !taken.Add(keys) ? (keys == searchKeys ? "the search's keys" : "keys another action has")
                : null;
            if (why == null) continue;
            shortcuts.Remove(action);
            fixes.Add($"hotCorners.shortcuts.{action} '{text}' dropped: {why}");
        }
    }

    /// <summary>A time of the day as "HH:mm" (the schedule's), or the fallback.</summary>
    static string ClockTime(string? value, string fallback, string name, List<string> fixes)
    {
        if (value != null && TimeOnly.TryParseExact(value, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) return value;
        fixes.Add($"{name} '{value}' -> '{fallback}'");
        return fallback;
    }

    static string OneOf(string? value, string[] allowed, string fallback, string name, List<string> fixes)
    {
        if (value != null && allowed.Contains(value)) return value;
        fixes.Add($"{name} '{value}' -> '{fallback}'");
        return fallback;
    }

    static int Step(int value, int min, int max, int step, int fallback, string name, List<string> fixes)
    {
        if (value < min || value > max) { fixes.Add($"{name} {value} -> {fallback}"); return fallback; }
        int snapped = min + (int)Math.Round((value - min) / (double)step) * step;
        if (snapped > max) snapped -= step;
        if (snapped != value) fixes.Add($"{name} {value} -> {snapped}");
        return snapped;
    }
}
