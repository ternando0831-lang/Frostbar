using System.Text.Json;
using System.Text.Json.Serialization;

namespace Frostbar.Core.Settings;

/// <summary>
/// User settings (%APPDATA%\Frostbar\settings.json). Every section keeps fields it does not know
/// (<see cref="JsonExtensionDataAttribute"/>) so an older build never drops what a newer one wrote — and settings
/// taken out (menuBar.showOnAllDisplays, appGrid.hidden, advanced.menuInput: never read, 2026-10-15) stay in old
/// files as such fields, unread. Values are clamped by <see cref="SettingsValidator"/> after loading.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// The settings' form: 2 since stage 5-2 (Now Playing in the menu bar), 3 since 2026-11-01 (macOS's defaults: a
    /// clear menu bar, a click on the app in front not minimizing it), 4 since 2026-11-08 (the Dock's room, as Apple's
    /// picture of macOS 27). Settings of an older one are brought up to date by <see cref="SettingsValidator"/>; every
    /// file written has the number (old files say 1).
    /// </summary>
    public const int CurrentSchema = 4;
    public int SchemaVersion { get; set; } = CurrentSchema;
    public GeneralSettings General { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public MenuBarSettings MenuBar { get; set; } = new();
    public DockSettings Dock { get; set; } = new();
    public SearchSettings Search { get; set; } = new();
    public AppGridSettings AppGrid { get; set; } = new();
    public HotCornerSettings HotCorners { get; set; } = new();
    public HudSettings Hud { get; set; } = new();
    public WallpaperSettings Wallpaper { get; set; } = new();
    public AccessibilitySettings Accessibility { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
    public SoundSettings Sound { get; set; } = new();
    public AdvancedSettings Advanced { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// macOS's Accessibility › Display, for the shell's own windows (2026-10-22). Reduce Transparency is the appearance's
/// "solid" background (<see cref="AppearanceSettings.Material"/>), shown on both pages.
/// </summary>
public sealed class AccessibilitySettings
{
    /// <summary>
    /// macOS's "Reduce motion": what moves, grows or bounces (a panel growing out of its item, the springs, the app grid's
    /// and Mission Control's zoom, a banner or a stack sliding in) fades in place instead. Not the animations' off
    /// (<see cref="AppearanceSettings.ReduceMotion"/>, a name from before this one), which leaves no motion at all.
    /// </summary>
    public bool ReduceMotion { get; set; }
    /// <summary>macOS's "Increase contrast": the glass and the settings' cards get a plain border, the dimmed words are darker.</summary>
    public bool IncreaseContrast { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>macOS's Desktop &amp; Dock › Windows, for the Window menu's Move &amp; Resize (2026-10-22).</summary>
public sealed class WindowSettings
{
    /// <summary>macOS 15's "Tiled windows have margins": room around and between the windows Move &amp; Resize puts side by side (off: none, as before).</summary>
    public bool TileMargins { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>macOS's Sound › Sound Effects, for the shell's own (2026-10-22): Windows' sounds of its sound scheme.</summary>
public sealed class SoundSettings
{
    /// <summary>"Play feedback when volume is changed": the volume keys and the Control Center's slider play the default sound at the new volume.</summary>
    public bool VolumeFeedback { get; set; }
    /// <summary>"Play user interface sound effects": emptying the trash plays Windows' sound for it (off: silent).</summary>
    public bool InterfaceSounds { get; set; } = true;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The volume and brightness display (stage 2-2, ADR-015).</summary>
public sealed class HudSettings
{
    /// <summary>Frostbar's display for the volume and brightness keys, with Windows' own kept from showing. Off: Windows' display.</summary>
    public bool Enabled { get; set; } = true;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class GeneralSettings
{
    /// <summary>"system" (Windows display language), "ja" or "en".</summary>
    public string Language { get; set; } = "system";
    public bool LaunchAtLogin { get; set; }
    public bool HideWindowsTaskbar { get; set; } = true;
    /// <summary>
    /// The first start's welcome was answered (or closed); until then the taskbar is not hidden. Null: absent from the
    /// file (one from before the welcome existed); false: shown and not answered yet (<see cref="WelcomeRule"/>).
    /// </summary>
    public bool? Welcomed { get; set; }
    /// <summary>The watchdog starts Frostbar again after a crash (at most 3 times in 5 minutes).</summary>
    public bool RelaunchAfterCrash { get; set; } = true;
    /// <summary>
    /// The app that opens folders and shows files in them (macOS's Finder): "explorer" (File Explorer) or "custom" (the
    /// program in <see cref="FileManagerPath"/>, given the folder's path). The Recycle Bin always opens in File Explorer.
    /// </summary>
    public string FileManager { get; set; } = "explorer";
    /// <summary>The program chosen for "custom" (<see cref="FileManager"/>); "" when none.</summary>
    public string FileManagerPath { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AppearanceSettings
{
    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; set; } = "system";
    /// <summary>"auto" (blur when Windows allows it), "translucent", "transparent" or "solid" (<see cref="BackdropRule"/>).</summary>
    public string Material { get; set; } = DefaultMaterial;
    /// <summary>
    /// The menu bar clear, the rest blurred: macOS 26's default (its "Show menu bar background" is off until chosen).
    /// It was "auto", a frosted bar, until 2026-11-01.
    /// </summary>
    public const string DefaultMaterial = "transparent";
    /// <summary>"system", "on" or "off".</summary>
    public string ReduceMotion { get; set; } = "system";
    /// <summary>Liquid Glass (macOS 26): rims that catch the light, clearer glass. Off: the plain frosted glass (lighter on the GPU).</summary>
    public bool LiquidGlass { get; set; } = true;
    /// <summary>
    /// Liquid Glass's lens (ADR-016): what is behind the Dock, the menus and the panels bends at their rims, read from a
    /// capture of the screen that leaves the shell's own glass out; so while it is on, they are not in screenshots or
    /// screen sharing. Only with <see cref="LiquidGlass"/>.
    /// </summary>
    public bool Lens { get; set; } = true;
    /// <summary>
    /// How tinted the glass is (macOS 27's Liquid Glass slider, from ultra clear to tinted; the user's choice "足す（既定は
    /// 中）", 2026-10-18): 0 clear … 100 tinted in steps of 5, 50 today's tint (<see cref="Shell.GlassTints"/>). Only with
    /// <see cref="LiquidGlass"/>.
    /// </summary>
    public int GlassTint { get; set; } = 50;
    /// <summary>
    /// Windows' dark mode by itself (stage 4-3, macOS's automatic appearance): "off", "sun" (from sunset to sunrise at
    /// <see cref="City"/>) or "clock" (from <see cref="DarkFrom"/> until <see cref="DarkUntil"/>).
    /// </summary>
    public string DarkSchedule { get; set; } = "off";
    /// <summary>The place of the sunset (a key of Frostbar.Core.Time.Cities); empty: the one of the PC's time zone.</summary>
    public string City { get; set; } = "";
    /// <summary>Dark from and until, "HH:mm" (local time).</summary>
    public string DarkFrom { get; set; } = "19:00";
    public string DarkUntil { get; set; } = "07:00";
    /// <summary>
    /// The accent colour (macOS's Appearance › Accent colour, 2026-10-22): "system" (Windows', as before) or one of
    /// macOS's (<see cref="Shell.AccentColors.Names"/>), for the shell only (Windows' own is not changed).
    /// </summary>
    public string Accent { get; set; } = Shell.AccentColors.System;
    /// <summary>macOS's "Show scroll bars" (<see cref="Shell.ScrollBarChoices.Shows"/>): "scrolling" (as before: while a list moves) or "always".</summary>
    public string ScrollBars { get; set; } = Shell.ScrollBarChoices.WhenScrolling;
    /// <summary>macOS's "Click in the scroll bar to" (<see cref="Shell.ScrollBarChoices.Clicks"/>): "page" (the next page) or "spot" (the spot clicked).</summary>
    public string ScrollBarClick { get; set; } = Shell.ScrollBarChoices.NextPage;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The wallpaper's own settings (stage 4-3): a picture for the light appearance and one for the dark, put on as Windows changes between them.</summary>
public sealed class WallpaperSettings
{
    public bool Paired { get; set; }
    public string? Light { get; set; }
    public string? Dark { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class MenuBarSettings
{
    public int Height { get; set; } = 24;
    /// <summary>Other apps' tray icons in the bar (ManagedShell, ADR-001). Off: Windows keeps them (Win+B shows them).</summary>
    public bool ShowTrayIcons { get; set; } = true;
    /// <summary>
    /// Where each tray icon goes (key from TrayKeys → "bar", "overflow" or "hidden", TrayPlaces); one not listed is on
    /// the bar. The » panel also takes the ones the bar has no room for (MenuBarFit).
    /// </summary>
    public Dictionary<string, string> TrayIcons { get; set; } = new();
    /// <summary>A tray app's balloon also shows as a banner under the bar at the right (stage 2-1); it is in the notification centre either way.</summary>
    public bool TrayBanners { get; set; } = true;
    /// <summary>
    /// The status items shown, left to right (stage 5-1, Core's MenuBarItems.Movable: "nowPlaying", "ime", "wifi",
    /// "volume", "battery", "focus", "search"); one not listed is hidden. Now Playing shows only while something plays,
    /// Focus only while it is on. Moved with Ctrl and a drag on the bar.
    /// </summary>
    public List<string> Items { get; set; } = [.. MenuBar.MenuBarItems.Movable];
    /// <summary>When the bar hides by itself (MenuBarItems.AutoHides): "always", "desktop" (not over a full-screen app), "fullScreen" (over one only: as before) or "never".</summary>
    public string AutoHide { get; set; } = "fullScreen";
    /// <summary>The apps that came to the front lately, the most recent first (the system menu's Recent Items, stage 5-2; at most 10).</summary>
    public List<DockPin> RecentApps { get; set; } = new();
    /// <summary>Recent Items' "Clear Menu" (UTC): the documents used until then are not listed (Windows' list is not touched).</summary>
    public DateTime? RecentClearedAt { get; set; }
    /// <summary>
    /// How many apps and documents Recent Items lists of each (macOS's "Recent documents, applications and servers",
    /// <see cref="Menus.RecentItems.Counts"/>; 0: none, the submenu not there). 10 as before.
    /// </summary>
    public int RecentCount { get; set; } = Menus.RecentItems.Count;
    /// <summary>The battery's item shows its percentage beside the picture (macOS's "Show Percentage"; on, as before).</summary>
    public bool BatteryPercent { get; set; } = true;
    public ClockSettings Clock { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ClockSettings
{
    public bool ShowDate { get; set; } = true;
    public bool ShowWeekday { get; set; } = true;
    public bool Seconds { get; set; }
    /// <summary>"auto" (follow the region's time format), "on" (24-hour) or "off".</summary>
    public string Hour24 { get; set; } = "auto";
    /// <summary>macOS's "Flash the time separators": the colons blink every second (the clock then ticks every second).</summary>
    public bool FlashSeparators { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class DockSettings
{
    /// <summary>
    /// DIPs. macOS's icons keep a margin inside their square and Windows' fill it, so at macOS's 48 they looked too big
    /// for the plate (the user, 2026-10-12): 40 is about the size macOS's artwork shows at 48.
    /// </summary>
    public int IconSize { get; set; } = 40;
    public bool Magnification { get; set; } = true;
    public int MagnifiedSize { get; set; } = 80;
    /// <summary>
    /// DIPs between icons (and at the plate's ends). Windows' icons fill their square more than macOS's do, so they want
    /// room. 14 (8 before) and <see cref="Padding"/> 14 (10 before): Apple's picture of macOS 27 has icon : room above and
    /// below : between = 41 : 15 : 14.5 px (measured 2026-11-08; the user's "A アイコン 40 のまま").
    /// </summary>
    public int Spacing { get; set; } = 14;
    /// <summary>DIPs of plate above and below the icons (the plate 68 DIPs high with the icons at 40: 85 px at 125%).</summary>
    public int Padding { get; set; } = 14;
    /// <summary>The screen's edge the Dock sits on: "bottom" (the default), "left" or "right" (macOS's "Position on screen").</summary>
    public string Position { get; set; } = "bottom";
    public bool AutoHide { get; set; }
    /// <summary>
    /// macOS's "Minimize windows into application icon": on, a minimized window goes into its app's icon (as before
    /// stage 7-2); off (the default, the user's choice A), it shows as a picture of itself before the trash.
    /// </summary>
    public bool MinimizeIntoIcon { get; set; }
    /// <summary>
    /// A click on the app in front minimizes its windows (Windows' taskbar). Off by default since 2026-11-01: macOS's
    /// Dock has no such thing.
    /// </summary>
    public bool ClickActiveAppMinimizes { get; set; }
    public bool ShowIndicators { get; set; } = true;
    public bool BounceOnLaunch { get; set; } = true;
    public bool PinsImported { get; set; }
    public List<DockPin> Pins { get; set; } = new();
    /// <summary>macOS's "recent applications": a section for the apps not kept, running or used lately (off by default).</summary>
    public bool ShowRecent { get; set; }
    /// <summary>Apps not kept in the Dock that quit lately, the most recent first (remembered whether shown or not).</summary>
    public List<DockPin> Recent { get; set; } = new();
    /// <summary>Folders kept in the Dock (macOS's stacks), before the trash.</summary>
    public List<string> Folders { get; set; } = new();
    /// <summary>The Downloads folder was put in the Dock once (as macOS has it); removing it is then for good.</summary>
    public bool DownloadsAdded { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class DockPin
{
    /// <summary>
    /// "appsFolder" (Id is the AppsFolder parsing name), "path" (Id is an .exe/.lnk path) or "separator" (a line the
    /// user put between the kept apps; its Id only tells it apart from the others).
    /// </summary>
    public string Kind { get; set; } = "appsFolder";
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public const string SeparatorKind = "separator";

    /// <summary>A separator to keep among the apps, with an Id of its own.</summary>
    public static DockPin NewSeparator() => new() { Kind = SeparatorKind, Id = "separator-" + Guid.NewGuid().ToString("N")[..8] };
}

public sealed class SearchSettings
{
    public const string DefaultHotkey = "Alt+Space", NoHotkey = "none";
    /// <summary>The keys that open the search (with Ctrl, Alt or Win), or <see cref="NoHotkey"/>.</summary>
    public string Hotkey { get; set; } = DefaultHotkey;
    /// <summary>"auto" (Everything when running, else Windows Search), "everything" or "windowsSearch".</summary>
    public string FileSource { get; set; } = "auto";
    public string WebSearchUrl { get; set; } = "https://www.google.com/search?q={q}";
    /// <summary>
    /// The kinds of results left out of the search's answer (Spotlight's "Search results" switches,
    /// <see cref="Search.SearchCategories"/>; 2026-10-22). Those left out, not those shown: a kind added later shows.
    /// The filters (Ctrl+1…4) still list theirs.
    /// </summary>
    public List<string> Hidden { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AppGridSettings
{
    public int Columns { get; set; } = 7;
    public int Rows { get; set; } = 5;
    /// <summary>The icons' size in DIPs (4 DIP steps); 0: as large as the cells allow nicely (the default).</summary>
    public int IconSize { get; set; }
    /// <summary>
    /// The user's arrangement (Launchpad, stage 6 E1): the grid's places in order, apps and folders. Empty: by name (the
    /// default). Apps placed nowhere come after the places, by name; places of apps not installed now are kept.
    /// </summary>
    public List<GridPlace> Arrangement { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A place of the app grid's arrangement: an app, or a folder of apps.</summary>
public sealed class GridPlace
{
    public const string AppKind = "app", FolderKind = "folder";
    /// <summary>
    /// The Id of the folder of hidden apps (the user, 2026-10-24): shown last whatever its place, named in the display
    /// language, kept with one app (it goes with none).
    /// </summary>
    public const string HiddenId = "hidden";

    /// <summary>"app" (Id is the AppsFolder parsing name) or "folder" (Id tells it apart; Name and Apps, in order).</summary>
    public string Kind { get; set; } = AppKind;
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    /// <summary>A folder's apps (AppsFolder parsing names), in order.</summary>
    public List<string>? Apps { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static GridPlace App(string id) => new() { Kind = AppKind, Id = id };
    public static GridPlace Folder(string id, string name, IEnumerable<string> apps) => new() { Kind = FolderKind, Id = id, Name = name, Apps = [.. apps] };
    /// <summary>A folder's Id of its own.</summary>
    public static string NewFolderId() => "folder-" + Guid.NewGuid().ToString("N")[..8];
}

public sealed class HotCornerSettings
{
    public int DwellMs { get; set; } = 250;
    /// <summary>Corner actions: "none", "overview", "appWindows", "appGrid", "search", "desktop", "notifications", "lock".</summary>
    public string TopLeft { get; set; } = "none";
    public string TopRight { get; set; } = "none";
    public string BottomLeft { get; set; } = "appGrid";
    public string BottomRight { get; set; } = "overview";
    /// <summary>"none", "ctrl", "alt", "shift" or "win".</summary>
    public string Modifier { get; set; } = "none";
    /// <summary>
    /// Keys for the same actions, plus "appWindows" (macOS's Mission Control shortcuts; Windows' touchpad gestures can send
    /// them): action → "Ctrl+Alt+Up". None by default: a key taken everywhere is taken from every app.
    /// </summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AdvancedSettings
{
    /// <summary>"error", "warn", "info" or "debug".</summary>
    public string LogLevel { get; set; } = "info";
    /// <summary>"hide" (H3: hidden, re-hidden on show events) or "transparent" (H2).</summary>
    public string TaskbarHideStrategy { get; set; } = "hide";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
public sealed partial class SettingsJson : JsonSerializerContext;
