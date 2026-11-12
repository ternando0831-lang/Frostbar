using Frostbar.Core.HotCorners;
using Frostbar.Core.MenuBar;
using Frostbar.Core.Settings;

namespace Frostbar.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "frostbar-tests-" + Guid.NewGuid().ToString("N"));
    string File_ => Path.Combine(_dir, "settings.json");

    public SettingsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var r = SettingsStore.Load(File_);
        Assert.Equal(24, r.Settings.MenuBar.Height);
        Assert.Equal(40, r.Settings.Dock.IconSize);
        Assert.True(r.Settings.General.HideWindowsTaskbar);
        Assert.Equal("hide", r.Settings.Advanced.TaskbarHideStrategy);
        Assert.Empty(r.Fixes);
    }

    [Fact]
    public void Round_trip_keeps_values_and_unknown_fields()
    {
        File.WriteAllText(File_, """
            {
              "schemaVersion": 1,
              "general": { "language": "ja", "futureFlag": true },
              "dock": { "iconSize": 64, "pins": [ { "kind": "appsFolder", "id": "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" } ] },
              "somethingNew": { "a": 1 }
            }
            """);
        var r = SettingsStore.Load(File_);
        Assert.Equal("ja", r.Settings.General.Language);
        Assert.Equal(64, r.Settings.Dock.IconSize);
        Assert.Single(r.Settings.Dock.Pins);
        SettingsStore.Save(File_, r.Settings);
        var text = File.ReadAllText(File_);
        Assert.Contains("\"futureFlag\": true", text);
        Assert.Contains("\"somethingNew\"", text);
        Assert.Equal("ja", SettingsStore.Load(File_).Settings.General.Language);
    }

    [Fact]
    public void Save_keeps_the_previous_file_as_bak()
    {
        var s = new AppSettings();
        SettingsStore.Save(File_, s);
        s.Dock.IconSize = 56;
        SettingsStore.Save(File_, s);
        Assert.True(File.Exists(File_ + ".bak"));
        Assert.Equal(40, SettingsStore.Load(File_ + ".bak").Settings.Dock.IconSize);
        Assert.Equal(56, SettingsStore.Load(File_).Settings.Dock.IconSize);
    }

    /// <summary>
    /// A file that cannot be read (held by another program) is not replaced by the defaults (2026-10-12: the start saved
    /// the defaults over it, and the pins and sizes survived only in .bak).
    /// </summary>
    [Fact]
    public void An_unreadable_file_is_not_saved_over()
    {
        File.WriteAllText(File_, """{ "schemaVersion": 1, "dock": { "iconSize": 64 } }""");
        SettingsLoad load;
        using (new FileStream(File_, FileMode.Open, FileAccess.Read, FileShare.None)) load = SettingsStore.Load(File_);
        Assert.True(load.Unreadable);
        Assert.False(SettingsStore.Save(File_, load.Settings));
        Assert.Equal(64, SettingsStore.Load(File_).Settings.Dock.IconSize); // untouched
    }

    [Fact]
    public void Null_lists_of_the_Dock_become_empty()
    {
        File.WriteAllText(File_, """{ "schemaVersion": 1, "dock": { "pins": null, "recent": null, "folders": null } }""");
        var d = SettingsStore.Load(File_).Settings.Dock;
        Assert.NotNull(d.Pins);
        Assert.NotNull(d.Recent);
        Assert.NotNull(d.Folders);
    }

    [Fact]
    public void Corrupt_file_gives_defaults_and_keeps_a_copy()
    {
        File.WriteAllText(File_, "{ this is not json");
        var r = SettingsStore.Load(File_);
        Assert.Equal(40, r.Settings.Dock.IconSize);
        Assert.NotNull(r.CorruptCopy);
        Assert.True(File.Exists(r.CorruptCopy));
    }

    [Fact]
    public void A_corrupt_file_gives_the_last_good_settings_and_keeps_them()
    {
        // A power cut after a save could leave settings.json broken: the defaults came, and the next save put the broken
        // file in place of the good .bak (2026-11-06 review).
        var s = new AppSettings();
        s.Dock.IconSize = 56;
        SettingsStore.Save(File_, s);
        s.Dock.IconSize = 64;
        SettingsStore.Save(File_, s); // .bak: 56
        File.WriteAllText(File_, "{ this is not json");
        var r = SettingsStore.Load(File_);
        Assert.Equal(56, r.Settings.Dock.IconSize);
        Assert.NotNull(r.CorruptCopy);
        Assert.Contains(r.Fixes, f => f.Contains(".bak"));
        SettingsStore.Save(File_, r.Settings);
        Assert.Equal(56, SettingsStore.Load(File_ + ".bak").Settings.Dock.IconSize); // the good one is still there
    }

    [Theory]
    [InlineData(26, 24)] // half pixels at 125%
    [InlineData(28, 28)]
    [InlineData(0, 24)]
    public void Menu_bar_height_is_24_or_28(int given, int expected)
    {
        var s = new AppSettings { MenuBar = { Height = given } };
        SettingsValidator.Normalize(s);
        Assert.Equal(expected, s.MenuBar.Height);
    }

    [Theory]
    [InlineData(50, 48)]  // snapped to 4 DIP steps
    [InlineData(31, 40)]  // below range -> default
    [InlineData(200, 40)] // above range -> default
    [InlineData(64, 64)]
    public void Dock_icon_size_is_clamped_and_snapped(int given, int expected)
    {
        var s = new AppSettings { Dock = { IconSize = given } };
        SettingsValidator.Normalize(s);
        Assert.Equal(expected, s.Dock.IconSize);
    }

    [Theory]
    [InlineData("explorer", "", "explorer")]
    [InlineData("custom", @"C:\Apps\Files.exe", "custom")]
    [InlineData("custom", "", "explorer")]   // a program is needed
    [InlineData("custom", null, "explorer")]
    [InlineData("auto", "", "explorer")]     // values of earlier versions
    [InlineData("bipane", "", "explorer")]
    [InlineData(null, "", "explorer")]
    public void File_manager_is_one_of_those_known(string? given, string? program, string expected)
    {
        var s = new AppSettings { General = { FileManager = given!, FileManagerPath = program! } };
        SettingsValidator.Normalize(s);
        Assert.Equal(expected, s.General.FileManager);
        Assert.NotNull(s.General.FileManagerPath);
    }

    [Fact]
    public void A_chosen_file_manager_is_saved_and_read_again()
    {
        var s = new AppSettings { General = { FileManager = "custom", FileManagerPath = @"C:\Apps\Files.exe" } };
        var again = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(s, SettingsJson.Default.AppSettings), SettingsJson.Default.AppSettings)!;
        SettingsValidator.Normalize(again);
        Assert.Equal(("custom", @"C:\Apps\Files.exe"), (again.General.FileManager, again.General.FileManagerPath));
    }

    [Fact]
    public void Magnified_size_never_below_icon_size()
    {
        var s = new AppSettings { Dock = { IconSize = 96, MagnifiedSize = 80 } };
        SettingsValidator.Normalize(s);
        Assert.True(s.Dock.MagnifiedSize >= s.Dock.IconSize);
    }

    /// <summary>macOS's defaults (2026-11-01): the menu bar clear, a click on the app in front not minimizing it.</summary>
    [Fact]
    public void New_settings_have_macOS_defaults()
    {
        var s = SettingsStore.Load(File_).Settings;
        Assert.Equal("transparent", s.Appearance.Material);
        Assert.False(s.Dock.ClickActiveAppMinimizes);
        Assert.Equal("fullScreen", s.MenuBar.AutoHide);
    }

    /// <summary>
    /// A file of schema 2, written in full with the old defaults (a frosted bar, a click minimizing), takes macOS's once
    /// (the user's "切り替える", 2026-11-01); chosen again after it, the old values stay. A solid background stays.
    /// </summary>
    [Fact]
    public void A_schema_2_file_takes_macOS_defaults_once()
    {
        File.WriteAllText(File_, """{ "schemaVersion": 2, "appearance": { "material": "auto" }, "dock": { "clickActiveAppMinimizes": true } }""");
        var r = SettingsStore.Load(File_);
        Assert.Equal("transparent", r.Settings.Appearance.Material);
        Assert.False(r.Settings.Dock.ClickActiveAppMinimizes);
        Assert.Contains(r.Fixes, f => f.StartsWith("schemaVersion 2 -> 3"));
        r.Settings.Appearance.Material = "auto";
        r.Settings.Dock.ClickActiveAppMinimizes = true;
        SettingsStore.Save(File_, r.Settings);
        var again = SettingsStore.Load(File_);
        Assert.Equal("auto", again.Settings.Appearance.Material);
        Assert.True(again.Settings.Dock.ClickActiveAppMinimizes);
        Assert.Empty(again.Fixes);

        File.WriteAllText(File_, """{ "schemaVersion": 2, "appearance": { "material": "solid" } }""");
        Assert.Equal("solid", SettingsStore.Load(File_).Settings.Appearance.Material);
        File.WriteAllText(File_, """{ "schemaVersion": 2, "appearance": { "material": "translucent" } }""");
        Assert.Equal("transparent", SettingsStore.Load(File_).Settings.Appearance.Material);
    }

    /// <summary>
    /// A file of schema 3 with the Dock's old spacing (8) and padding (10) takes the room of Apple's picture of macOS 27
    /// once (the user's "A アイコン 40 のまま", 2026-11-08); a value the user chose stays, and chosen again after it, the
    /// old values stay.
    /// </summary>
    [Fact]
    public void A_schema_3_file_takes_the_Docks_new_room_once()
    {
        File.WriteAllText(File_, """{ "schemaVersion": 3, "dock": { "spacing": 8, "padding": 10 } }""");
        var r = SettingsStore.Load(File_);
        Assert.Equal(14, r.Settings.Dock.Spacing);
        Assert.Equal(14, r.Settings.Dock.Padding);
        Assert.Contains(r.Fixes, f => f.StartsWith("schemaVersion 3 -> 4"));
        r.Settings.Dock.Spacing = 8;
        r.Settings.Dock.Padding = 10;
        SettingsStore.Save(File_, r.Settings);
        var again = SettingsStore.Load(File_);
        Assert.Equal(8, again.Settings.Dock.Spacing);
        Assert.Equal(10, again.Settings.Dock.Padding);
        Assert.Empty(again.Fixes);

        File.WriteAllText(File_, """{ "schemaVersion": 3, "dock": { "spacing": 12, "padding": 10 } }""");
        var chosen = SettingsStore.Load(File_).Settings;
        Assert.Equal(12, chosen.Dock.Spacing);
        Assert.Equal(14, chosen.Dock.Padding);
        Assert.Equal(14, new AppSettings().Dock.Spacing);
        Assert.Equal(14, new AppSettings().Dock.Padding);
    }

    [Fact]
    public void Unknown_choices_fall_back()
    {
        var s = new AppSettings();
        s.General.Language = "fr";
        s.Appearance.Material = "glass";
        s.HotCorners.BottomLeft = "launch-rockets";
        s.Advanced.TaskbarHideStrategy = "delete";
        var fixes = SettingsValidator.Normalize(s);
        Assert.Equal("system", s.General.Language);
        Assert.Equal(AppearanceSettings.DefaultMaterial, s.Appearance.Material);
        Assert.Equal("appGrid", s.HotCorners.BottomLeft);
        Assert.Equal("hide", s.Advanced.TaskbarHideStrategy);
        Assert.Equal(4, fixes.Count);
    }

    public static TheoryData<string> CornerActions => new(HotCornerActions.ForCorners);

    /// <summary>
    /// Every action the settings page offers for a corner survives a start (2026-10-15: "appWindows" went back to the
    /// default at the next start; the validator had a list of its own without it).
    /// </summary>
    [Theory]
    [MemberData(nameof(CornerActions))]
    public void Every_corner_action_the_page_offers_survives_a_start(string action)
    {
        var s = new AppSettings();
        s.HotCorners.TopLeft = action;
        s.HotCorners.BottomRight = action;
        SettingsStore.Save(File_, s);
        var read = SettingsStore.Load(File_);
        Assert.Equal(action, read.Settings.HotCorners.TopLeft);
        Assert.Equal(action, read.Settings.HotCorners.BottomRight);
        Assert.Empty(read.Fixes);
    }

    [Fact]
    public void The_actions_keys_open_are_corner_actions_and_the_others_window_operations()
    {
        Assert.Subset(HotCornerActions.ForCorners.ToHashSet(), HotCornerActions.ForOpenKeys.ToHashSet());
        Assert.Equal(HotCornerActions.ForOpenKeys.Concat(HotCornerActions.ForWindowKeys), HotCornerActions.ForKeys);
        // Each window key runs an operation of the Window menu: a place, or the return to the frame from before.
        Assert.All(HotCornerActions.ForWindowKeys, a => Assert.True(HotCornerActions.WindowOp(a) is { } op
            && (Menus.WindowOps.Place(op) != null || op == Menus.WindowOps.ReturnFrame), a));
        Assert.All(HotCornerActions.ForOpenKeys, a => Assert.Null(HotCornerActions.WindowOp(a)));
        Assert.Equal(HotCornerActions.ForWindowKeys.Length, HotCornerActions.ForWindowKeys.Select(HotCornerActions.WindowOp).Distinct().Count());
    }

    // The page's two lists, not the validator's ForKeys: drawn from that, a list the validator lacked would only have had fewer rows.
    public static TheoryData<string> KeyActions => new(HotCornerActions.ForOpenKeys.Concat(HotCornerActions.ForWindowKeys).ToArray());

    /// <summary>Keys set for every action the keyboard page offers survive a start (the corners' lesson).</summary>
    [Theory]
    [MemberData(nameof(KeyActions))]
    public void Keys_for_every_action_the_page_offers_survive_a_start(string action)
    {
        var s = new AppSettings();
        s.HotCorners.Shortcuts[action] = "Ctrl+Alt+Shift+F9";
        SettingsStore.Save(File_, s);
        var read = SettingsStore.Load(File_);
        Assert.Equal("Ctrl+Alt+Shift+F9", read.Settings.HotCorners.Shortcuts[action]);
        Assert.Empty(read.Fixes);
    }

    public static TheoryData<string> TrayPlaceNames => new(TrayPlaces.Names);

    /// <summary>Every place the settings page offers for a tray icon survives a start (the hot corners' lesson).</summary>
    [Theory]
    [MemberData(nameof(TrayPlaceNames))]
    public void Every_tray_place_the_page_offers_survives_a_start(string place)
    {
        var s = new AppSettings();
        s.MenuBar.TrayIcons["exe:line.exe:1"] = place;
        SettingsStore.Save(File_, s);
        var read = SettingsStore.Load(File_);
        Assert.Equal(place, read.Settings.MenuBar.TrayIcons["exe:line.exe:1"]);
        Assert.Equal(place, TrayPlaces.Name(TrayPlaces.Parse(place)));
        Assert.Empty(read.Fixes);
    }

    public static TheoryData<string> DockPositionNames => new(SettingsValidator.DockPositions);

    /// <summary>Every edge the Dock's page offers survives a start, and is the edge the Dock stands on (stage 7).</summary>
    [Theory]
    [MemberData(nameof(DockPositionNames))]
    public void Every_dock_position_the_page_offers_survives_a_start(string position)
    {
        var s = new AppSettings();
        s.Dock.Position = position;
        SettingsStore.Save(File_, s);
        var read = SettingsStore.Load(File_);
        Assert.Equal(position, read.Settings.Dock.Position);
        Assert.Equal(position, Frostbar.Core.Dock.DockFrame.Name(Frostbar.Core.Dock.DockFrame.Parse(position)));
        Assert.Empty(read.Fixes);
    }

    [Theory]
    [InlineData("top")] // macOS has no Dock at the top: the menu bar is there
    [InlineData("Left")]
    [InlineData(null)]
    public void A_dock_position_the_page_could_not_have_set_falls_back_to_the_bottom(string? position)
    {
        var s = new AppSettings();
        s.Dock.Position = position!;
        var fixes = SettingsValidator.Normalize(s);
        Assert.Equal("bottom", s.Dock.Position);
        Assert.Contains(fixes, f => f.StartsWith("dock.position"));
    }

    [Fact]
    public void Tray_places_the_page_could_not_have_set_are_dropped_and_a_null_list_becomes_empty()
    {
        File.WriteAllText(File_, """{ "schemaVersion": 1, "menuBar": { "trayIcons": { "exe:a.exe:1": "overflow", "exe:b.exe:1": "attic", "exe:c.exe:1": null } } }""");
        var read = SettingsStore.Load(File_);
        Assert.Equal(["exe:a.exe:1"], read.Settings.MenuBar.TrayIcons.Keys);
        Assert.Equal(2, read.Fixes.Count(f => f.StartsWith("menuBar.trayIcons.")));
        Assert.Equal(TrayPlace.Bar, TrayPlaces.Parse("attic"));
        File.WriteAllText(File_, """{ "schemaVersion": 1, "menuBar": { "trayIcons": null } }""");
        Assert.Empty(SettingsStore.Load(File_).Settings.MenuBar.TrayIcons);
    }

    /// <summary>A hand-edited null made every start throw where the shortcuts are registered (the watchdog gives up after 3).</summary>
    [Fact]
    public void Null_shortcuts_become_empty()
    {
        File.WriteAllText(File_, """{ "schemaVersion": 1, "hotCorners": { "shortcuts": null } }""");
        var h = SettingsStore.Load(File_).Settings.HotCorners;
        Assert.NotNull(h.Shortcuts);
        Assert.Empty(h.Shortcuts);
    }

    [Fact]
    public void Shortcuts_the_page_could_not_have_set_are_dropped()
    {
        var s = new AppSettings();
        s.HotCorners.Shortcuts["overview"] = "Ctrl+Alt+Up";
        s.HotCorners.Shortcuts["launch-rockets"] = "Ctrl+Alt+R";  // no such action
        s.HotCorners.Shortcuts["appGrid"] = "Ctrl+Alt+Nowhere";   // keys that cannot be read
        s.HotCorners.Shortcuts["desktop"] = "Shift+D";            // without Ctrl, Alt or Win it is taken from every app
        s.HotCorners.Shortcuts["notifications"] = "ctrl+alt+up";  // the same keys as the first: the first keeps them
        s.HotCorners.Shortcuts["appWindows"] = null!;             // a hand-edited null
        var fixes = SettingsValidator.Normalize(s);
        Assert.Equal(new Dictionary<string, string> { ["overview"] = "Ctrl+Alt+Up" }, s.HotCorners.Shortcuts);
        Assert.Equal(5, fixes.Count(f => f.StartsWith("hotCorners.shortcuts")));
    }

    [Fact]
    public void Shortcuts_the_page_sets_are_kept()
    {
        var s = new AppSettings();
        s.HotCorners.Shortcuts["overview"] = "Ctrl+Alt+Shift+F9";
        s.HotCorners.Shortcuts["appWindows"] = "Win+F5";
        s.HotCorners.Shortcuts["desktop"] = "Alt+D";
        SettingsStore.Save(File_, s);
        var read = SettingsStore.Load(File_);
        Assert.Equal(3, read.Settings.HotCorners.Shortcuts.Count);
        Assert.Empty(read.Fixes);
    }

    [Fact]
    public void Web_search_url_must_be_http_with_a_placeholder()
    {
        var s = new AppSettings();
        s.Search.WebSearchUrl = "file:///C:/Windows/System32/calc.exe?{q}";
        SettingsValidator.Normalize(s);
        Assert.StartsWith("https://", s.Search.WebSearchUrl);
    }

    [Fact]
    public void Invalid_pins_are_dropped()
    {
        var s = new AppSettings();
        s.Dock.Pins.Add(new DockPin { Kind = "appsFolder", Id = "" });
        s.Dock.Pins.Add(new DockPin { Kind = "shell-command", Id = "rm -rf" });
        s.Dock.Pins.Add(new DockPin { Kind = "path", Id = @"C:\Windows\notepad.exe" });
        SettingsValidator.Normalize(s);
        Assert.Single(s.Dock.Pins);
    }

    [Fact]
    public void Separators_are_kept_among_the_pins_once_each()
    {
        var s = new AppSettings();
        s.Dock.Pins.Add(new DockPin { Kind = "path", Id = @"C:\Windows\notepad.exe" });
        s.Dock.Pins.Add(new DockPin { Kind = DockPin.SeparatorKind, Id = "separator-1" });
        s.Dock.Pins.Add(new DockPin { Kind = "path", Id = @"C:\Windows\regedit.exe" });
        s.Dock.Pins.Add(new DockPin { Kind = DockPin.SeparatorKind, Id = "separator-1" }); // a hand-edited copy
        s.Dock.Pins.Add(new DockPin { Kind = DockPin.SeparatorKind, Id = "separator-2" });
        SettingsValidator.Normalize(s);
        Assert.Equal(["notepad.exe", "separator-1", "regedit.exe", "separator-2"], s.Dock.Pins.Select(p => Path.GetFileName(p.Id)).ToArray());
    }

    [Fact]
    public void A_separator_saved_and_read_again_is_still_one()
    {
        var s = new AppSettings();
        s.Dock.Pins.Add(DockPin.NewSeparator());
        SettingsStore.Save(File_, s);
        var read = SettingsStore.Load(File_).Settings.Dock.Pins;
        Assert.Equal(DockPin.SeparatorKind, Assert.Single(read).Kind);
        Assert.Equal(s.Dock.Pins[0].Id, read[0].Id);
    }

    [Fact]
    public void The_apps_used_lately_and_the_folders_drop_what_cannot_be_read()
    {
        File.WriteAllText(File_, """
            { "schemaVersion": 1, "dock": {
              "recent": [ null, { "kind": "separator", "id": "separator-1" }, { "kind": "path", "id": "" }, { "kind": "path", "id": "C:\\x\\a.exe" } ],
              "folders": [ "", null, "C:\\Users\\u\\Downloads" ] } }
            """);
        var d = SettingsStore.Load(File_).Settings.Dock;
        Assert.Equal(@"C:\x\a.exe", Assert.Single(d.Recent).Id);
        Assert.Equal(@"C:\Users\u\Downloads", Assert.Single(d.Folders));
    }
}
